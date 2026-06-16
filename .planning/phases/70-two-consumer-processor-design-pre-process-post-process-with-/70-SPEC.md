# Phase 70: Two-consumer processor design (Pre-Process + Post-Process) with keeper recovery — Specification

**Created:** 2026-06-16
**Ambiguity score:** 0.14 (gate: ≤ 0.20)
**Requirements:** 12 locked

## Goal

Replace the current single-consumer `ProcessorPipeline` (gate on the `L2[messageId]` slot-array, framework-owned Post) with the pinned two-consumer design: a **Pre-Process** consumer gating on `L2[entryId]` whose `virtualProcess` seam returns one `<data result>` (downstream) or spawns N to a new **Post-Process** consumer and returns null (entry), output keyed by `messageId` and written only when `result == completed`, with the keeper states `REINJECT`/`INJECT`/`DELETE` redefined accordingly.

## Background

**This SPEC's pseudocode is the sole source of truth (it supersedes the slot-array model for this work).** It was pinned over an extended design session; the existing code does not constrain it.

Current code (to be replaced): one `EntryStepDispatchConsumer` → `ProcessorPipeline.RunAsync`, gating on the `L2[messageId]` slot-array HASH, with the framework owning Post (output-validate → mint `entryId` → `write L2[entryId]=data` → slot-record → send `StepCompleted` → source-delete) and keeper `KeeperReinject`/`KeeperInject`/`KeeperDelete`. The `BaseProcessor<TConfig>.ProcessAsync` seam returns `List<ProcessItem>`; `SampleProcessor` demonstrates spawn-2-on-entry / accumulate-1-downstream by returning items. Canonical doc `docs/design/processor-keeper-recovery-spec.md` describes this now-superseded model.

Pinned design (target):

```
Pre-Process pipeline consumer
if key exist L2[entryId]            (L2 exhausted → REINJECT(messageId, ids, payload); return)
    read L2[entryId]               (L2 exhausted → REINJECT(messageId, ids, payload); return)
    validate vs input schema       (invalid → send StepFailed to orchestrator; return)   // no entryId delete
    deserialize payload → config   (invalid → send StepFailed to orchestrator; return)   // no entryId delete
    <data result> = virtualProcess(validatedData, config, executionId)   // abstract, author overrides
        // executionId non-empty → compute, RETURN one <data result>
        // executionId empty      → spawn (gen executionIds, send <data result> to Post, SWALLOW),
        //                          delete L2[entryId] (no-op, empty), RETURN NULL
    if <data result> == null: return          // spawn handled everything; skip the tail
    validate data vs output definition         (invalid → result = failed)
    if result == completed:
        write L2[messageId] = data             (L2 exhausted → INJECT(messageId, ids, <data result>); return)
    send to orchestrator based on result       // 4 consumers
    delete L2[entryId]                         (L2 exhausted → DELETE(messageId, ids); return)
end if

Post-Process pipeline consumer   in: <data result>
    data = <data result>.data
    validate data vs output definition         (invalid → result = failed)
    if result == completed:
        write L2[messageId] = data             (L2 exhausted → INJECT(messageId, ids, <data result>); return)
    send to orchestrator based on result

KEEPER
  REINJECT  read L2[entryId]; re-inject to Pre-Process, same messageId.
  INJECT    write L2[messageId]=data (from <data result>); delete L2[entryId];
            send to orchestrator based on <data result>; same messageId.
  DELETE    delete L2[entryId] ONLY.   (no orchestrator send)

Rules: no outbox; UseMessageRetry none; error routing disabled;
       send → retry → throw → broker redelivery; L2[messageId] written with TTL (current impl);
       output keyed by messageId; four result types (current impl).
```

## Requirements

1. **Pre gate on `L2[entryId]`**: The Pre-Process consumer branches on the existence of `L2[entryId]`.
   - Current: branch is `L2[messageId]` slot-array existence.
   - Target: gate on `L2[entryId]` existence; an L2-op exhaustion on the gate routes to `REINJECT(messageId, ids, payload)` and returns (no result sent).
   - Acceptance: an injected Redis fault on the gate read produces exactly one `REINJECT` keeper message and no `Step*` result; a clean absent `entryId` returns without processing.

2. **Pre read + input validation + deserialize**: read `L2[entryId]`, validate against input schema, deserialize payload → config.
   - Current: this sequence exists inside `ProcessorPipeline.RunForwardAsync`.
   - Target: read exhaustion → `REINJECT` + return; input-schema invalid OR deserialize failure → send exactly one `StepFailed` to the orchestrator and return, **without deleting `L2[entryId]`** (left to TTL).
   - Acceptance: malformed input yields exactly one `StepFailed`; `L2[entryId]` is not deleted on that path; a read fault yields `REINJECT`.

3. **Seam returns one `<data result>` or null**: `virtualProcess` returns a single `<data result>` (non-empty `executionId`) or `null` (empty `executionId`, author handled spawn+handoff).
   - Current: `ProcessAsync` returns `List<ProcessItem>` (never null).
   - Target: seam returns one `<data result>` or `null`; Pre returns immediately on `null` **before** output-validation/write/send/delete.
   - Acceptance: a `null` return causes the Pre consumer to send nothing and write/delete nothing; a non-null return proceeds to the inline tail.

4. **Pre inline tail (output keyed by `messageId`, write gated on completed)**: validate output; if completed `write L2[messageId]=data`; send by result; delete `L2[entryId]`.
   - Current: framework Post mints `entryId`, writes `L2[entryId]=data`, slot-records, sends.
   - Target: output validated; `write L2[messageId]=data` **only when `result == completed`** (exhaustion → `INJECT(messageId, ids, <data result>)` + return); send to one of the 4 orchestrator consumers by result; then `delete L2[entryId]` (exhaustion → `DELETE(messageId, ids)` + return).
   - Acceptance: a completed result writes `L2[messageId]=data` once, sends `StepCompleted`, deletes `L2[entryId]`; a non-completed result skips the write but still sends + deletes; write-exhaust → `INJECT` + return (no send, no delete); delete-exhaust → `DELETE` + return.

5. **Post-Process consumer (new)**: a new consumer receives a `<data result>`, takes `data = <data result>.data`, validates output, writes `L2[messageId]=data` when completed, sends by result; never touches `entryId`.
   - Current: no Post-Process consumer exists.
   - Target: `IConsumer<DataResult>` (new contract) performing only the output tail; write gated on completed (exhaustion → `INJECT`); **no `L2[entryId]` read or delete**.
   - Acceptance: a `<data result>` message produces the `L2[messageId]` write (if completed) and the orchestrator send; the consumer issues no `entryId` read/delete; write-exhaust → `INJECT` + return.

6. **`REINJECT` redefined**: read `L2[entryId]`, re-inject the original dispatch to the Pre-Process consumer with the same `messageId`.
   - Current: `KeeperReinject` reinjects under the slot model.
   - Target: read `L2[entryId]`, re-inject to Pre-Process with the outbound envelope `MessageId` overridden to the carried `messageId`; a clean-absent `entryId` drops (no re-inject).
   - Acceptance: a re-injected dispatch carries envelope `MessageId == messageId`; when `entryId` is absent, no dispatch is re-injected.

7. **`INJECT` redefined (self-contained)**: `write L2[messageId]=data` (from carried `<data result>`), `delete L2[entryId]`, send to orchestrator by `<data result>`, same `messageId`.
   - Current: `KeeperInject` writes `L2[entryId]=data` + slot.
   - Target: write `data` from the carried `<data result>` to `L2[messageId]`, delete `L2[entryId]` (no-op when empty), send the result; envelope `MessageId` overridden to `messageId`.
   - Acceptance: `INJECT` produces the `L2[messageId]` write, the orchestrator send, and the `entryId` delete; it never reads input to recompute.

8. **`DELETE` redefined (delete-only)**: delete `L2[entryId]` only — no orchestrator send.
   - Current: `KeeperDelete` deletes data + index.
   - Target: issue only the `L2[entryId]` delete; send nothing to the orchestrator.
   - Acceptance: `DELETE` produces exactly one delete operation and zero orchestrator sends.

9. **Seam helpers for spawn + delete**: the `BaseProcessor` seam exposes typed helpers so the author orchestrates policy while the framework owns resilience.
   - Current: the seam offers no send/delete capability (pure return).
   - Target: helpers `SpawnToPost(<data result>, executionId)` (send to Post, swallow-capable) and `DeleteEntry()` (bounded retry → `DELETE` escalation on exhaustion); the framework owns `RetryLoop` and keeper escalation.
   - Acceptance: a sample author spawns N to Post and deletes the entry using only the helpers (no hand-rolled `RetryLoop`/keeper); helpers apply the bounded retry and escalate per the spec.

10. **Sample two-mode `virtualProcess`**: payload = `{label, number}`.
    - Current: `SampleProcessor` returns 2/1 `ProcessItem`s via the list seam.
    - Target: empty `executionId` → 2 random numbers each plus payload `number`, log `"{label} had the following numbers: …"`, spawn 2 `<data result>` to Post (distinct minted `executionId`s, swallow), return `null`; non-empty `executionId` → 1 random number plus payload `number`, log `"{label} had the following numbers: …"`, return one completed `<data result>`.
    - Acceptance: an empty-`executionId` dispatch produces exactly 2 Post messages with distinct `executionId`s and the Pre consumer sends/writes/deletes nothing inline; a non-empty-`executionId` dispatch produces one completed result through the inline tail; both paths emit the `"{label} had the following numbers: …"` log line.

11. **Resilience invariants preserved**: no outbox; `UseMessageRetry` none; error routing disabled; every L2 op bounded-retry → keeper state on exhaustion; every send bounded-retry → throw → broker redelivery; `L2[messageId]` TTL = current impl; four result types = current impl.
    - Current: these hold in the existing pipeline.
    - Target: preserved unchanged in the two-consumer design; spawn/keeper sends override the outbound envelope `MessageId` with the carried `messageId`; no MassTransit built-in inbox dedup on these endpoints.
    - Acceptance: dispatch endpoints have no bus retry and no `_error`; a send-exhaustion throws (nack-requeue); `L2[messageId]` carries the jittered `random[ExecutionDataTtl, 2×ExecutionDataTtl]` TTL.

12. **Migrate tests + supersede old spec doc**: the existing Phase-50–68 processor tests are updated to the new model; the old canonical doc is marked superseded.
    - Current: ~30 test files encode the slot-array/inline model; `docs/design/processor-keeper-recovery-spec.md` describes it.
    - Target: the processor test suite is rewritten/updated to the two-consumer model and passes; `docs/design/processor-keeper-recovery-spec.md` carries a "superseded by Phase 70" marker (or is replaced by this design).
    - Acceptance: `dotnet test` for the processor suite passes against the new implementation; the old spec doc shows the superseded marker.

## Boundaries

**In scope:**
- Pre-Process consumer: gate `L2[entryId]`, read, input-validate, deserialize, `virtualProcess`, inline output tail (write `L2[messageId]` on completed, send by result, delete `L2[entryId]`).
- Post-Process consumer (new) + `<data result>` message contract: output-only tail, no `entryId`.
- Redefined keeper states `REINJECT` / `INJECT` / `DELETE` per the pseudocode.
- `BaseProcessor` seam change: `virtualProcess` returns one `<data result>` or null; helpers `SpawnToPost` and `DeleteEntry`.
- Sample two-mode `virtualProcess` (label/number; spawn-2-on-empty / accumulate-1-on-non-empty; `"{label} had the following numbers: …"` log).
- Output keyed by `messageId`; write gated on `result == completed`.
- Replace the existing `ProcessorPipeline` slot-array/inline model in-place; migrate the ~30 Phase-50–68 processor tests; mark `docs/design/processor-keeper-recovery-spec.md` superseded.

**Out of scope:**
- Orchestrator design and the downstream `entryId` ↔ `messageId` threading (the "#2 output-keying" decision) — that is the orchestrator's responsibility, deferred to its own phase.
- Mode-2 partial-spawn reconciliation in the orchestrator (best-effort spawn; orchestrator does not track N; scheduler re-fires) — only the processor-side swallow + scheduler-re-trigger assumption is in scope.
- Building or deploying the container image — the stack is busy; build is explicitly excluded this phase (code + `dotnet test` only).
- Changing the four result contracts or the TTL policy — reuse current implementation.
- Cleaning up `L2[entryId]` on input-validation/deserialize-failure paths beyond its TTL — left to TTL per the source of truth.

## Constraints

- **No outbox** (immediate sends) — required so "send-before-delete" is genuinely durable; an in-memory outbox would defer the Post send past the immediate `entryId` delete and reopen silent loss.
- `UseMessageRetry` none; error routing disabled; send-exhaustion → throw → RabbitMQ nack-requeue (broker redelivery); no `_error`.
- Every L2 op runs in the bounded `RetryLoop` (`Retry:Limit`); on exhaustion it escalates to the keeper state named in the pseudocode.
- `L2[messageId]` TTL = current implementation (jittered `random[ExecutionDataTtl, 2×ExecutionDataTtl]`, index outlives data).
- `messageId` = MassTransit envelope `MessageId`; spawn sends and keeper re-injections **override the outbound envelope `MessageId`** with the carried `messageId`; **no** built-in MassTransit inbox/`MessageId` dedup on these endpoints.
- `virtualProcess` must be deterministic / side-effect-idempotent — recovery re-runs the Pre flow, and Mode-2 spawns are re-fired by the scheduler.

## Acceptance Criteria

- [ ] Pre-Process consumer gates on `L2[entryId]`; gate fault → `REINJECT`; clean-absent → return (no result).
- [ ] Input-schema/deserialize failure → exactly one `StepFailed`, `L2[entryId]` NOT deleted, return.
- [ ] `virtualProcess` returning `null` causes the Pre consumer to write/send/delete nothing; non-null proceeds to the tail.
- [ ] Completed result: `write L2[messageId]=data` once, `StepCompleted` sent, `L2[entryId]` deleted; write-exhaust → `INJECT` (no send/delete); delete-exhaust → `DELETE`.
- [ ] Non-completed result: write skipped, result still sent, `L2[entryId]` deleted.
- [ ] Post-Process consumer takes `<data result>.data`, writes `L2[messageId]` on completed, sends by result, and performs no `entryId` read/delete.
- [ ] `REINJECT` re-injects to Pre with envelope `MessageId == messageId`; drops on absent `entryId`.
- [ ] `INJECT` writes `L2[messageId]=data`, deletes `L2[entryId]`, sends the result; never recomputes from input.
- [ ] `DELETE` performs only the `entryId` delete and sends nothing to the orchestrator.
- [ ] Seam exposes `SpawnToPost` and `DeleteEntry` helpers; the sample author uses them without hand-rolled retry/keeper.
- [ ] Empty-`executionId` sample dispatch → exactly 2 Post messages with distinct `executionId`s, Pre sends/writes/deletes nothing inline; non-empty → one completed result via the inline tail; both log `"{label} had the following numbers: …"`.
- [ ] No-outbox, `UseMessageRetry` none, error routing disabled, send-exhaust → throw; `L2[messageId]` carries the jittered TTL.
- [ ] Processor `dotnet test` suite passes against the new implementation; old spec doc marked superseded.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                            |
|--------------------|-------|------|--------|------------------------------------------------------------------|
| Goal Clarity       | 0.90  | 0.75 | ✓      | Outcome precise: pinned two-consumer pseudocode is source of truth |
| Boundary Clarity   | 0.88  | 0.70 | ✓      | Replace-in-place + helpers-in-scope; explicit out-of-scope list   |
| Constraint Clarity | 0.82  | 0.65 | ✓      | No-outbox, retry/keeper rules, TTL, envelope-MessageId override   |
| Acceptance Criteria| 0.80  | 0.70 | ✓      | 13 pass/fail checks derived from the pseudocode                   |
| **Ambiguity**      | 0.14  | ≤0.20| ✓      |                                                                  |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

The Socratic interview was conducted across the design conversation that produced this phase; each decision below was explicitly confirmed by the user.

| Round | Perspective     | Question summary                                   | Decision locked                                                        |
|-------|-----------------|----------------------------------------------------|------------------------------------------------------------------------|
| 1     | Researcher      | What is the gate / current model?                  | Gate moves to `L2[entryId]`; supersedes `L2[messageId]` slot-array      |
| 2     | Simplifier      | What does the seam return?                          | One `<data result>` (downstream) or null (entry/spawn)                 |
| 3     | Boundary Keeper | Pre inline vs Post consumer?                        | Hybrid by `executionId`: non-empty → Pre inline; empty → spawn to Post  |
| 3     | Boundary Keeper | Replace vs parallel? Seam helpers?                  | Replace in-place; framework helpers (`SpawnToPost`, `DeleteEntry`) in scope |
| 4     | Failure Analyst | Send/delete ordering; outbox?                       | Send-before-delete; no outbox; send-exhaust → throw → redelivery        |
| 4     | Failure Analyst | Keeper `DELETE` send?                               | `DELETE` is delete-only (no orchestrator send)                          |
| 5     | Seed Closer     | Write content/keying; conditional write?            | `write L2[messageId]=data`; gated on `result==completed`; key by messageId |
| 5     | Seed Closer     | Mode-2 recovery contract?                           | Best-effort spawn; author owns swallow; orchestrator doesn't track N; scheduler re-fires |

---

*Phase: 70-two-consumer-processor-design-pre-process-post-process-with-*
*Spec created: 2026-06-16*
*Next step: /gsd-discuss-phase 70 — implementation decisions (how to build what's specified above)*
