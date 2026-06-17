# Phase 72: Processor Always-Write to L2 + Uniform Orchestrator Pre-Pipeline + Unresolved-Step Metric — Specification

**Created:** 2026-06-17
**Ambiguity score:** 0.12 (gate: ≤ 0.20)
**Requirements:** 6 locked

## Goal

Make every *business* processor result write its `DataResult.Data` blob to L2 (so `StepFailed`/`StepCancelled` carry a real `EntryId` instead of `Guid.Empty`), then drop the Completed-only `out:` read/delete branches in `OrchestratorPrePipeline` so all four `TypedResultConsumer<T>` shells run one uniform flow — and add the Phase-71-deferred `orchestrator_step_unresolved` counter (label `workflowId` only) at the two L1-resolution misses.

## Background

Today the processor writes the `out:` output blob **only on `Completed`**. `OutputTail.cs:60` gates the write on `if (result == StepOutcome.Completed)`; output-definition validation already flips a Completed whose output fails the `OutputDefinition` to `Failed` (`OutputTail.cs:56-58`, using `context.OutputDefinition` loaded at startup per Phase 57). Non-completed results (`StepFailed`/`StepCancelled`) are built with `EntryId = Guid.Empty` (`ProcessorPipeline.cs:199,202`) and write nothing; the `ProcessorPipeline` catch blocks (`:96-117`) and the input-schema-validation failure (`:83`) `SendResult` directly and bypass `OutputTail`. `validatedData` is the input read at `ProcessorPipeline.cs:79`, in scope through every business failure path.

On the orchestrator side, `OrchestratorPrePipeline.RunAsync` branches on `outcome == StepOutcome.Completed` twice — the `out:` relocation read (`:84`) and the delete (`:114`) — so the four `TypedResultConsumer<T>` shells (`StepCompleted/Failed/Cancelled/Processing`, `Program.cs:61-64`) only *look* uniform; the pipeline still special-cases outcome internally. Two graceful trip-ends exist with distinct logs but **no metric** (Phase 71 D-18 deliberately deferred it — `OrchestratorPrePipeline.cs:37-43` injects no metrics holder): `completed-unresolved` (consumed step absent from L1, `:58`) and `completed-terminal` (resolved, no successor, `:69`). `SelectNext` (`StepAdvancement.cs:39-42`) silently skips a `NextStepId` absent from L1 via `TryGetValue`.

This phase removes the asymmetry at its source (always write) so the orchestrator becomes branch-free, and lands the deferred unresolved-step counter. It is distinct from the imported parallel-project "Phase 72" metrics SPEC (the uniform two-counter reshape), which lands separately via `/gsd-import`.

## Requirements

1. **Processor writes a blob for every business outcome**: The L2 output write is unconditional — it runs for `Completed`, `Failed`, `Cancelled`, and `Processing`, never gated on outcome.
   - Current: `OutputTail.cs:60` gates the `StringSetAsync(OutputData(dr.MessageId), dr.Data, …)` write on `if (result == StepOutcome.Completed)`; non-completed results write nothing
   - Target: the write runs for every business result; `OutputTail` always persists `dr.Data` to `L2[out:messageId]`; output-definition validation (`:56-58`) continues to flip a failed-output Completed to `Failed` but **never** prevents the write
   - Acceptance: a hermetic test drives each of the four outcomes through the processor tail and asserts `L2[out:messageId]` exists with the expected `dr.Data` for all four; no outcome path skips the write

2. **Non-completed results carry a real `EntryId` + data**: `StepFailed`/`StepCancelled` (and the `Processing` result) reference the written blob and carry data.
   - Current: `BuildFailed`/`BuildCancelled` (`ProcessorPipeline.cs:199,202`) set `EntryId = Guid.Empty`; the result carries no data blob reference
   - Target: every business `Step*` result stamps `EntryId = the output blob's messageId` (the same id the blob is written under), and its `DataResult.Data` is populated; the catch blocks (`ProcessorPipeline.cs:96-117`) and the input-schema-validation failure (`:83`) route through `OutputTail` (write + send) rather than bypassing it
   - Acceptance: a hermetic test asserts a `StepFailed` and a `StepCancelled` produced by the pipeline carry a non-`Guid.Empty` `EntryId` equal to the messageId of the blob written in L2; `Guid.Empty` no longer appears as a `Step*` `EntryId` on any business path

3. **Thrown `ExecuteAsync` is caught into a failed result carrying `validatedData`**: An author exception becomes a `DataResult{failed}` with the error message and the read input as its data.
   - Current: `ProcessorPipeline.cs:96-108` (`ProcessStatusException`) and `:109-117` (unexpected, incl. deserialize) call `SendResult(BuildFailed/…)` and return without writing
   - Target: a thrown `ExecuteAsync` is caught and converted into a `DataResult{Result=Failed, error message, Data = validatedData}` (the `:79` input) that flows through the always-write tail (write + send); the `ProcessStatusException` cases preserve their `Failed`/`Cancelled`/`Processing` mapping
   - Acceptance: a hermetic test where the author `ExecuteAsync` throws produces a `StepFailed` carrying the error message, a real `EntryId`, and an `L2[out:messageId]` blob containing `validatedData`

4. **`orchestrator_step_unresolved` counter**: A new counter increments at the two L1-resolution misses, labeled `workflowId` only.
   - Current: no metric reflects either graceful trip-end; `OrchestratorPrePipeline` injects no `OrchestratorMetrics` (`:37-43`)
   - Target: `OrchestratorMetrics` exposes `orchestrator_step_unresolved` (snake_case, no `_total` in code — collector appends it); `OrchestratorMetrics` is injected into `OrchestratorPrePipeline` via the existing `IMeterFactory` DI pattern (never a static `Meter`); the counter is incremented (a) at stage-1 — the consumed step / workflow absent from L1 (`:58`) → log + increment + return; and (b) at stage-3 — a `NextStepId` absent from L1 → log + increment + **continue** (graceful skip, never throw). It carries only `workflowId` (camelCase, value `m.WorkflowId`)
   - Acceptance: Prometheus shows `orchestrator_step_unresolved_total` carrying a `workflowId` label and no other labels; a consumed result whose step is missing from L1 increments it once; a resolved step with a dangling next-step id increments it once per missing successor and still processes the resolvable successors; a normal fan-out (all successors resolve) and a `completed-terminal` (resolved, no declared successors) do **not** increment it

5. **Uniform orchestrator Pre-pipeline (no Completed-only branch)**: The relocation read + delete run for all outcomes; all four `TypedResultConsumer<T>` shells share one flow.
   - Current: `OrchestratorPrePipeline.cs:84` gates the `out:` read on `outcome == Completed` and `:114` gates the delete on the same; non-completed proceeds with `relocated=""` and never deletes
   - Target: the pipeline runs `exists-gate L2[entryId]` → read → resolve `L1[stepId]` → terminal check → per next step (read `L1[nextStepId]` + entry-condition check + send `NextStepHandoff`) → delete `L2[entryId]`, for **every** outcome; a cleanly-absent `L2[entryId]` (no Redis fault) → idempotent skip (no fan-out, ack); a Redis fault on read/delete still escalates REINJECT/DELETE to the keeper as today; `ExecutionId` is threaded unchanged onto each `NextStepHandoff` (D-13, `:105`); `outcome` is used only for entry-condition matching, never for L2 behavior
   - Acceptance: `OrchestratorPrePipeline.RunAsync` contains no `if (outcome == StepOutcome.Completed)` branch; a hermetic test drives `Failed`/`Cancelled` results through the pipeline and asserts the `out:` blob is read, fanned out (with data), and deleted identically to `Completed`; a cleanly-absent blob acks without fan-out

6. **Naming + DI conventions preserved**: The new counter and injections follow existing conventions and the build stays clean.
   - Current: `OrchestratorMetrics` builds via `IMeterFactory.Create("Orchestrator")`; instrument names are snake_case with no `_total` in code; meter name `Orchestrator` registered via `AddMeter`
   - Target: `orchestrator_step_unresolved` is snake_case with no embedded `_total`; its label key is camelCase `workflowId`; the `Orchestrator` meter name and its `AddMeter(...)` registration are unchanged; `OrchestratorMetrics` is constructor-injected into `OrchestratorPrePipeline` (no static `Meter`)
   - Acceptance: the new instrument name is snake_case with no `_total`; the label key is camelCase; meter name remains `Orchestrator`; 0-warning Debug + Release build; hermetic suite GREEN

## Boundaries

**In scope:**
- Removing the Completed-only write gate in `OutputTail` (write for every business outcome)
- Routing the `ProcessorPipeline` catch blocks + input-schema-validation failure through `OutputTail` (write + send), carrying `Data = validatedData`
- Stamping a real `EntryId` (= output messageId) on `StepFailed`/`StepCancelled`/`Processing` results
- Dropping the two Completed-only branches in `OrchestratorPrePipeline` (read + delete) → uniform flow for all four `TypedResultConsumer<T>`
- Cleanly-absent `L2[entryId]` → idempotent skip in the orchestrator Pre-pipeline
- New `orchestrator_step_unresolved` counter (label `workflowId`), injected `OrchestratorMetrics` into `OrchestratorPrePipeline`, incremented at the stage-1 and stage-3 L1-resolution misses
- Hermetic test coverage for all of the above; 0-warning Debug + Release

**Out of scope:**
- The imported parallel-project Phase-72 uniform two-counter metrics reshape (`{service}_messages_consumed`/`_sent`, `keeper_l2_probe`, analyzer/test rework) — lands separately via `/gsd-import` (likely renumbered) and is independent of this phase
- Adding a `reason`/`stage` label to `orchestrator_step_unresolved` — kept label-minimal (`workflowId` only); which stage failed lives in the logs
- Metering the `completed-terminal` trip-end or the entry-condition skip — neither is a resolution failure
- The infra read-failure path (`ProcessorPipeline.cs:78` input-read exhausted → REINJECT) — it correctly writes no blob (recovery escalation, not a business result); unchanged
- Changing keeper states (REINJECT/INJECT/DELETE), meter names, or `AddMeter` registrations
- Live-stack close-gate run — deferred-automated where the sandbox lacks Docker
- Output-definition validation itself (already implemented at `OutputTail.cs:56-58`) and startup definition loading (already implemented, Phase 57)

## Constraints

- Instrument name MUST be snake_case with NO `_total` suffix in code (the collector's prometheus exporter appends it); label key MUST be camelCase (`workflowId`).
- The meter MUST be built via `IMeterFactory` (existing DI pattern) — never a static `Meter` (hermetic test isolation); `OrchestratorMetrics` is constructor-injected into `OrchestratorPrePipeline`.
- The stage-3 unresolved next-step is a **graceful skip** (`continue`, never a throw) — it preserves the existing T-24-06 no-throw behavior, only adding observability.
- The cleanly-absent `L2[entryId]` skip MUST NOT escalate to the keeper (a Redis *fault* still does, as today).
- The processor's always-write blob carries the existing jittered TTL (`OutputDataTtl`); no TTL policy change.
- `ExecutionId` MUST remain threaded onto every `NextStepHandoff` (D-13).
- Build MUST be 0-warning in both Debug and Release; verification is machine-verified (never "human verification").

## Acceptance Criteria

- [ ] `OutputTail` writes `L2[out:messageId]=dr.Data` for all four business outcomes (no `if (result == Completed)` write gate)
- [ ] No business `Step*` result carries `EntryId = Guid.Empty`; `StepFailed`/`StepCancelled` reference the written blob's messageId
- [ ] A thrown `ExecuteAsync` yields a `StepFailed` with the error message + a blob containing `validatedData`
- [ ] `OrchestratorPrePipeline.RunAsync` contains no `if (outcome == StepOutcome.Completed)` branch; `Failed`/`Cancelled` are read, fanned out (with data), and deleted identically to `Completed`
- [ ] A cleanly-absent `L2[entryId]` acks without fan-out (idempotent skip), no keeper escalation
- [ ] `orchestrator_step_unresolved_total` is emitted with a `workflowId` label and no other labels
- [ ] The counter increments at stage-1 (consumed step missing from L1 → return) and stage-3 (dangling next-step id → continue); `completed-terminal`, entry-condition skip, and normal fan-out do not increment it
- [ ] `OrchestratorMetrics` is constructor-injected into `OrchestratorPrePipeline` (no static `Meter`); meter name `Orchestrator` + `AddMeter` unchanged
- [ ] Hermetic suite GREEN + 0-warning Debug & Release build
- [ ] Live-stack proof of the uniform pipeline + always-write + the new counter — deferred-automated where the sandbox lacks Docker

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                              |
|--------------------|-------|------|--------|--------------------------------------------------------------------|
| Goal Clarity       | 0.93  | 0.75 | ✓      | Always-write + uniform gate + unresolved metric all decided        |
| Boundary Clarity   | 0.88  | 0.70 | ✓      | Imported metrics reshape + infra read path explicitly out of scope |
| Constraint Clarity | 0.82  | 0.65 | ✓      | snake_case/camelCase, IMeterFactory DI, graceful skip, TTL kept    |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | Per-outcome write, real EntryId, branch-free gate, label-minimal   |
| **Ambiguity**      | 0.12  | ≤0.20| ✓      |                                                                    |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                  | Decision locked                                                              |
|-------|-----------------|---------------------------------------------------|------------------------------------------------------------------------------|
| 0     | (prior convo)   | Deferred orchestrator trip-end metric?            | New `orchestrator_step_unresolved`, label `workflowId` only                  |
| 0     | (prior convo)   | Which trip-ends / stages increment the metric?    | Stage-1 (return) + stage-3 (continue) L1 misses; terminal stays log-only     |
| 0     | (prior convo)   | Stage-3 dangling next-step behavior?              | Graceful skip + metric (supersedes earlier "drop-all"), never throw          |
| 0     | (prior convo)   | How to make the orchestrator gate uniform?        | Processor always-writes a blob for every outcome → drop Completed-only branch |
| 0     | (prior convo)   | What data is written on non-completed?            | `dr.Data` always; thrown failure → `validatedData` fallback; real `EntryId`  |
| 0     | (prior convo)   | Completed output-validation-fail + startup defs?  | Already implemented (`OutputTail.cs:56-58` + Phase 57) — no work              |
| 0     | (prior convo)   | Cleanly-absent `L2[entryId]` in the orchestrator? | Idempotent skip (ack, no fan-out, no keeper); Redis fault still escalates     |

---

*Phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi*
*Spec created: 2026-06-17*
*Next step: /gsd-discuss-phase 72 — implementation decisions (how to build what's specified above)*
