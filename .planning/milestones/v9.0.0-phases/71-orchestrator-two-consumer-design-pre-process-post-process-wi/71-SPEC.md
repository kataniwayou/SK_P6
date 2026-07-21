# Phase 71: Orchestrator two-consumer design (Pre-Process + Post-Process) with keeper recovery — Specification

**Created:** 2026-06-17
**Ambiguity score:** 0.16 (gate: ≤ 0.20)
**Requirements:** 12 locked

## Goal

Mirror the Phase-70 processor two-consumer pattern on the orchestrator's result-advancement path and close the Phase-70-deferred `entryId`↔`messageId` threading (A1). A **Pre-Process** consumer gates on `L2[entryId]` (reading the upstream step's output from the `out:` namespace), resolves next steps from the L1 graph by entry-condition match, fans out one **Post-Process** message per matching next step (carrying `payload` + relocated `data`), then deletes `L2[entryId]`; a **Post-Process** consumer writes the next step's input `data` to `L2[messageId]` (`data:` namespace, TTL'd) and dispatches `EntryStepDispatch` to the processor with `entryId = messageId`. Keeper `REINJECT`/`INJECT`/`DELETE` are redefined with envelope-`MessageId` override. **A1 closes:** the processor result stamps `EntryId = its output messageId` (replacing the `Guid.Empty` placeholder).

## Background

**The Phase-70 processor two-consumer design is the pattern of record; Phase 71 is its orchestrator-side mirror.** The data flow becomes a symmetric cross-pairing: the processor reads input from `skp:data:` and writes output to `skp:out:`; the orchestrator reads upstream output from `skp:out:` and writes the next step's input to `skp:data:`.

Current code (to be reshaped):
- The orchestrator's result path is **L1-only and passes no runtime data between steps**. `TypedResultConsumer<TMessage>` (with sealed `StepCompleted`/`StepFailed`/`StepCancelled`/`StepProcessing` arms) consumes one typed `IStepResult` off the shared `orchestrator-result` queue, reads the completed step + successors from the L1 store ONLY (`store.TryGet` → `wf.Steps`), matches each successor's `EntryCondition` against the per-type `Outcome` knob via `StepAdvancement.SelectNext`, and `StepDispatcher.DispatchAsync` `Send`s one `EntryStepDispatch` per match to `queue:{processorId:D}` carrying only the successor's **static** `step.Payload`. There is no L2 read/write on this path.
- `EntryId` flows straight through as `Guid.Empty` — the A1 placeholder, stamped in the processor's `OutputTail.BuildStep` (`{ … EntryId = Guid.Empty }`). So today steps are data-isolated: each reads `L2[data:Guid.Empty]` (skipped via the source sentinel) and gets only its static payload.
- An L1 miss / no-matching-successor is today a **silent business ack** (`TypedResultConsumer.cs:68–75`, log + `return`).

Key L2 keys (single source of truth, `L2ProjectionKeys`):
- `ExecutionData(entryId)` → `skp:data:{entryId:D}` — the input blob the processor reads.
- `OutputData(messageId)` → `skp:out:{messageId:D}` — the output blob the processor writes (completed-only), carrying the jittered `OutputDataTtl` policy.

Processor keeper mirror (Phase 70, done): `ReinjectConsumer` reads `L2[entryId]` (STRLEN — absent/empty ⇒ by-design silent drop; Redis exception ⇒ infra → `Guard`/exhaustion), re-injects with the outbound envelope `MessageId` overridden to the carried `messageId`; `InjectConsumer`/`DeleteConsumer` similarly self-contained.

## Requirements

1. **Orchestrator Pre-Process consumer (new) gates on `L2[entryId]`**: a Pre-Process consumer replaces the straight-through result-advancement on the result queue; it branches on the existence/readability of `L2[out:entryId]` (the upstream output blob, `entryId` = the upstream output messageId).
   - Current: `TypedResultConsumer` advances L1-only with no L2 gate or read.
   - Target: Pre-Process gates on `L2[out:entryId]`; an L2-op **exhaustion** on the gate/read routes to keeper `REINJECT(messageId, ids, …)` and returns (no fan-out, no result sent).
   - Acceptance: an injected Redis fault on the gate/read produces exactly one orchestrator-side `REINJECT` keeper message and no fan-out; a `Completed` result with a present `out:` blob proceeds to resolution + relocation.

2. **No-silent-loss next-step resolution (terminal OR unresolvable → complete with a proper message)**: resolution is a pure L1 lookup; the trip ends explicitly in both outcomes.
   - Current: an L1 miss / no-matching-successor silently acks (log + return) with no distinct, countable signal.
   - Target: (a) a resolved step whose successor set is empty / all-`Never` / no entry-condition match = **terminal** → complete the trip; (b) a result whose workflow/step **cannot be resolved for any reason** (total L1 miss, unknown workflow) → **also** complete the trip — **never park, throw, or keeper-escalate on resolution**. Every trip-end emits a structured log line **and** an orchestration-completed metric, with two independently-countable reasons: `completed-terminal` vs `completed-unresolved`.
   - Acceptance: a terminal-step result increments the `completed-terminal` metric + emits its log signature and acks; a total-L1-miss result increments the `completed-unresolved` metric + emits its distinct log signature and acks; neither path throws, parks, or sends a keeper message.

3. **Continuation is entry-condition-driven and outcome-agnostic**: every result outcome advances by entry-condition match; the outcome value is only what is matched.
   - Current: `StepAdvancement.SelectNext(Outcome, completed, steps)` already matches `EntryCondition == (int)outcome || == Always`; preserved.
   - Target: a continuing successor is selected iff its `EntryCondition` matches the result's outcome or is `Always`; a `Failed`/`Cancelled` outcome **can** continue the workflow when a successor is gated on that outcome; `Never` is never selected; `StepProcessing` remains non-advancing (no fan-out).
   - Acceptance: a `Failed` result with a successor gated on `Failed` fans out to that successor; the same result with only `Completed`-gated successors completes as terminal (no fan-out); a `Never`-gated successor is never dispatched.

4. **Data relocation only when an output blob exists (Completed)**: the `out:`→`data:` relocation runs only for outcomes that produced an output blob.
   - Current: no relocation exists (no runtime data is passed between steps).
   - Target: a continuing `Completed` step relocates its `L2[out:entryId]` blob into each next step's input; a continuing **non-completed** step (no `out:` blob) is dispatched with **no relocated input data** — the gate/read finds nothing and only the entry-condition match drives the fan-out.
   - Acceptance: a `Completed` continuation produces a non-empty `L2[data:messageId]` input write per successor; a `Failed` continuation produces no `data:` write and dispatches the successor with empty/absent input.

5. **Pre fan-out + entry delete**: after resolution, Pre fans out one Post-Process message per matching next step (carrying the next step's `payload` + the relocated `data`), then deletes `L2[entryId]`.
   - Current: Pre dispatches `EntryStepDispatch` directly to the processor; no Post hop, no entry delete.
   - Target: for each matching successor, send one orchestrator Post-Process message carrying `{ next step's payload, relocated data, ids }`; after the fan-out sends, delete `L2[out:entryId]` (exhaustion → keeper `DELETE(messageId, ids)` + return); a send-exhaust throws (broker redelivery).
   - Acceptance: a `Completed` result with 2 matching successors produces exactly 2 Post-Process messages and one `L2[out:entryId]` delete; delete-exhaust → exactly one `DELETE` keeper message; send-exhaust → throw (no delete).

6. **Orchestrator Post-Process consumer (new)**: a new consumer writes the next step's input `data` to `L2[messageId]` (`data:` namespace, TTL'd) and dispatches `EntryStepDispatch` to `queue:{processorId:D}` with `entryId = messageId`.
   - Current: no Post-Process consumer exists on the orchestrator.
   - Target: an `IConsumer<…>` over the fan-out message that writes `L2[data:messageId] = data` with a TTL consistent with the existing `OutputData` jittered policy (exhaustion → keeper `INJECT(messageId, ids, …)` + return), then dispatches `EntryStepDispatch(workflowId, nextStepId, nextProcessorId, payload){ entryId = messageId, executionId unchanged }`; a relocated-empty (non-completed) continuation skips the `data:` write and dispatches with `entryId = Guid.Empty`.
   - Acceptance: a fan-out message produces one `L2[data:messageId]` write (Completed path) and one `EntryStepDispatch` with `EntryId == messageId`; write-exhaust → one `INJECT` keeper message + return (no dispatch).

7. **A1 close — processor stamps `EntryId = output messageId`**: the processor result carries its output `messageId` as `EntryId`, replacing the `Guid.Empty` placeholder.
   - Current: `OutputTail.BuildStep` stamps `EntryId = Guid.Empty` on every Step* result (A1 placeholder).
   - Target: the `Completed` arm stamps `EntryId = dr.MessageId` (the output blob key); non-completed arms continue to carry `Guid.Empty` (no output blob); the orchestrator Pre then gates/reads `L2[out:EntryId]` off this id.
   - Acceptance: a processor `StepCompleted` carries `EntryId == its OutputData messageId`; the orchestrator Pre reads `L2[out:EntryId]` and finds the blob the processor wrote.

8. **`REINJECT` redefined (orchestrator side)**: read `L2[entryId]`, re-inject the original result to the orchestrator Pre-Process with the same `messageId`; clean-absent drops.
   - Current: no orchestrator-side keeper recovery exists.
   - Target: read `L2[out:entryId]`; re-inject the original step-result to the orchestrator Pre-Process consumer with the outbound envelope `MessageId` overridden to the carried `messageId`; an absent/empty `entryId` (STRLEN == 0, no Redis exception) is a by-design silent drop (ack, no re-inject); a Redis exception on the read is infra → exhaustion policy.
   - Acceptance: a re-injected result carries envelope `MessageId == messageId`; an absent `entryId` re-injects nothing (drop counted); a Redis fault on read escalates per the exhaustion policy.

9. **`INJECT` redefined (orchestrator side, self-contained)**: write `L2[data:messageId] = data`, delete `L2[entryId]`, dispatch `EntryStepDispatch`, same `messageId`.
   - Current: no orchestrator-side `INJECT`.
   - Target: write the carried next-step input `data` to `L2[data:messageId]`, delete `L2[out:entryId]` (no-op when empty), dispatch the reconstructed `EntryStepDispatch` (`entryId = messageId`, `executionId` unchanged); envelope `MessageId` overridden to `messageId`; never recomputes from input.
   - Acceptance: `INJECT` produces the `L2[data:messageId]` write, the `EntryStepDispatch` dispatch, and the `L2[out:entryId]` delete; it issues no L1 read to recompute successors.

10. **`DELETE` redefined (orchestrator side, delete-only)**: delete `L2[entryId]` only — no dispatch, no result send.
    - Current: no orchestrator-side `DELETE`.
    - Target: issue only the `L2[out:entryId]` delete; send/dispatch nothing.
    - Acceptance: `DELETE` produces exactly one delete operation and zero dispatches/sends.

11. **Resilience invariants preserved (= Phase 70)**: no outbox; `UseMessageRetry` none; error routing disabled; every L2 op bounded-retry → keeper state on exhaustion; every send bounded-retry → throw → broker redelivery; `executionId` threaded unchanged; envelope `MessageId` override on fan-out + keeper re-injections; no MassTransit inbox/`MessageId` dedup on these endpoints.
    - Current: these hold on the existing dispatch/result endpoints (no bus retry, no `_error`).
    - Target: preserved unchanged in the two-consumer orchestrator design; the orchestrator Post-Process endpoint mirrors the entry endpoint policy (distinct per-orchestrator queue, retry off, error routing off, send-exhaust → throw); `L2[data:messageId]` carries the jittered TTL.
    - Acceptance: the orchestrator Pre/Post endpoints have no bus retry and no `_error`; a send-exhaustion throws (nack-requeue); `executionId` is byte-identical across a `Completed`→continuation hop; `L2[data:messageId]` carries the jittered `OutputDataTtl`-policy TTL.

12. **Migrate tests**: the orchestrator advancement/dispatch tests are updated to the two-consumer + data-relocation model and pass.
    - Current: the orchestrator test suite encodes the L1-only straight-through advancement (no L2 relocation, `EntryId = Guid.Empty`).
    - Target: tests are rewritten/updated to the Pre/Post two-consumer model (gate/read `out:`, fan-out, `data:` write, `entryId = messageId` dispatch, keeper `REINJECT`/`INJECT`/`DELETE`, A1 `EntryId` stamp, trip-end metrics) and pass; the existing processor A1 stamp test is updated.
    - Acceptance: `dotnet test` for the orchestrator (+ the A1-affected processor) suite passes against the new implementation.

## Boundaries

**In scope:**
- Orchestrator **Pre-Process** consumer: gate/read `L2[out:entryId]`, resolve next steps by entry-condition match, fan out one Post message per match (payload + relocated data), delete `L2[out:entryId]`.
- Orchestrator **Post-Process** consumer (new) + its fan-out message contract: write `L2[data:messageId]` (TTL'd) on the Completed path, dispatch `EntryStepDispatch` with `entryId = messageId`.
- Trip-end completion semantics: structured log + orchestration-completed metric with `completed-terminal` / `completed-unresolved` reasons (no-silent-loss by construction).
- Orchestrator-side keeper states `REINJECT` / `INJECT` / `DELETE` (envelope-`MessageId` override; `REINJECT` clean-absent drop).
- **A1 close:** processor `OutputTail.BuildStep` stamps `Completed` `EntryId = output messageId`.
- Namespace cross-pairing: orchestrator reads `out:`, writes `data:`.
- `executionId` threaded unchanged across the hop.
- Migrate the orchestrator advancement/dispatch tests (+ the A1-affected processor test).

**Out of scope:**
- **Fan-in / merge / AND-join** — the processor is stateless: a step fires once per arriving message regardless of producer; OR-join / fire-per-edge stands. A true barrier-merge needs persistent join-state + dedup + a merge contract + lineage reconciliation that fight the at-least-once no-dedup posture — a dedicated future phase.
- **Parking / throwing / keeper-escalating on next-step *resolution* failure** — resolution failure is always a clean trip-completion with a proper message; keeper/throw paths exist only for **infra** (L2-op exhaustion, send fault), never for resolution.
- Changing the four step-result contracts, the entry-condition semantics, or the L1 hydration model — reuse current implementation.
- Container build / deploy — code + `dotnet test` only this phase (stack is busy), matching the Phase-70 convention.
- Relocating data for non-completed outcomes (no `out:` blob exists) — non-completed continuations carry no input by design.

## Constraints

- **No outbox** (immediate sends) — required so "send-before-delete" is genuinely durable; an in-memory outbox would defer the Post fan-out past the immediate `entryId` delete and reopen silent loss.
- `UseMessageRetry` none; error routing disabled; send-exhaustion → throw → RabbitMQ nack-requeue (broker redelivery); no `_error` on the Pre/Post/keeper endpoints.
- Every L2 op runs in the bounded `RetryLoop` (`Retry:Limit`); on exhaustion it escalates to the orchestrator-side keeper state named in the pseudocode (`REINJECT` on gate/read, `INJECT` on `data:` write, `DELETE` on `entryId` delete).
- `L2[data:messageId]` is written with a TTL consistent with the existing `OutputDataTtl` jittered `random[ttl, 2×ttl]` policy (single source of truth in `L2ProjectionKeys`); exact floor/option resolved in discuss-phase.
- `messageId` = MassTransit envelope `MessageId`; fan-out sends and keeper re-injections **override the outbound envelope `MessageId`** with the carried `messageId`; **no** built-in MassTransit inbox/`MessageId` dedup on these endpoints.
- `executionId` is threaded **unchanged** end-to-end (no regen) — preserves per-instance lineage from entry through every continuation.
- Resolution is a pure in-memory L1 lookup (no Redis) — it cannot infra-fail; its only outcomes are continue (match) or complete-the-trip (terminal/unresolved).

## Acceptance Criteria

- [ ] Orchestrator Pre-Process gates on `L2[out:entryId]`; gate/read exhaustion → exactly one orchestrator `REINJECT`; a present `Completed` blob proceeds.
- [ ] Terminal-step result → `completed-terminal` metric + log + ack; total-L1-miss result → `completed-unresolved` metric + distinct log + ack; neither throws/parks/keeper-escalates.
- [ ] Continuation is entry-condition match (`== (int)outcome || Always`); a `Failed`-gated successor advances on `Failed`; `Never` never advances; `Processing` does not fan out.
- [ ] A `Completed` continuation writes a non-empty `L2[data:messageId]` per successor; a non-completed continuation writes no `data:` and dispatches with empty input.
- [ ] Pre fans out exactly one Post message per matching successor, then deletes `L2[out:entryId]`; delete-exhaust → one `DELETE`; send-exhaust → throw (no delete).
- [ ] Orchestrator Post-Process writes `L2[data:messageId]` (TTL'd, Completed path) and dispatches `EntryStepDispatch` with `EntryId == messageId`; write-exhaust → one `INJECT` + return (no dispatch).
- [ ] Processor `StepCompleted` carries `EntryId == its output messageId` (A1 closed); non-completed results carry `Guid.Empty`.
- [ ] `REINJECT` re-injects to Pre with envelope `MessageId == messageId`; absent `entryId` drops (counted); Redis fault on read escalates.
- [ ] `INJECT` writes `L2[data:messageId]`, deletes `L2[out:entryId]`, dispatches `EntryStepDispatch`; never recomputes successors from L1.
- [ ] `DELETE` performs only the `L2[out:entryId]` delete and dispatches/sends nothing.
- [ ] `executionId` is byte-identical across a `Completed`→continuation hop; orchestrator Pre/Post endpoints have no bus retry and no `_error`; `L2[data:messageId]` carries the jittered TTL.
- [ ] Orchestrator (+ A1-affected processor) `dotnet test` suite passes against the new implementation.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                                 |
|--------------------|-------|------|--------|-----------------------------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | Two-consumer mirror precise; continuation entry-condition-driven; A1 close |
| Boundary Clarity   | 0.84  | 0.70 | ✓      | Fan-in out of scope (stateless); resolution-failure never parks; build excluded |
| Constraint Clarity | 0.82  | 0.65 | ✓      | No-outbox, L2-op→keeper, envelope-MessageId override, executionId unchanged, TTL |
| Acceptance Criteria| 0.80  | 0.70 | ✓      | 12 pass/fail checks incl. distinct trip-end metrics                    |
| **Ambiguity**      | 0.16  | ≤0.20| ✓      |                                                                       |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                       | Decision locked                                                                 |
|-------|-----------------|--------------------------------------------------------|---------------------------------------------------------------------------------|
| 1     | Researcher      | What is today's orchestrator result path / data flow?  | L1-only, no data passing, `EntryId = Guid.Empty` (A1 placeholder)               |
| 1     | Failure Analyst | No-silent-loss: what happens when next step can't resolve? | Terminal OR unresolvable (any reason) → complete trip with a proper message; never park/throw/keeper on resolution |
| 1     | Boundary Keeper | What is "a proper message"? (delegated to Claude)      | Structured log + orchestration-completed metric, reasons `completed-terminal` / `completed-unresolved` (consistent with existing observability) |
| 1     | Simplifier      | Result-type scope of the relocation path?              | Continuation entry-condition-driven for ALL outcomes; data relocation only when an `out:` blob exists (Completed) |
| 1     | Boundary Keeper | Fan-in / merge in scope?                               | Out of scope — processor stateless, fires per arriving message; OR-join / fire-per-edge |

---

*Phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi*
*Spec created: 2026-06-17*
*Next step: /gsd-discuss-phase 71 — implementation decisions (Pre/Post wiring, keeper contract reuse-vs-new, `data:` TTL floor, fan-out message contract shape)*
