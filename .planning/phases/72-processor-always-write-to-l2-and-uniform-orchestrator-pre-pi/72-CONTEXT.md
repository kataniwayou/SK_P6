# Phase 72: Processor Always-Write to L2 + Uniform Orchestrator Pre-Pipeline + Unresolved-Step Metric — Context

**Gathered:** 2026-06-17
**Status:** Ready for planning

<domain>
## Phase Boundary

Make every *terminal* business processor result write its `DataResult.Data` blob to L2 (so `StepFailed`/`StepCancelled` carry a real `EntryId`), drop the Completed-only `out:` read/delete branches in `OrchestratorPrePipeline` so all four `TypedResultConsumer<T>` shells run one uniform flow, and add the Phase-71-deferred `orchestrator_step_unresolved` counter. This discussion covers HOW to wire it; the WHAT is locked by SPEC.md.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**6 requirements are locked.** See `72-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `72-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- Removing the Completed-only write gate in `OutputTail` (write for every terminal outcome)
- Routing the `ProcessorPipeline` catch blocks + input-schema-validation failure through `OutputTail` (write + send), carrying `Data = validatedData`
- Stamping a real `EntryId` (= output messageId) on `StepFailed`/`StepCancelled`
- Dropping the two Completed-only branches in `OrchestratorPrePipeline` → uniform flow for all four `TypedResultConsumer<T>`
- Cleanly-absent `L2[entryId]` → idempotent skip in the orchestrator Pre-pipeline
- New `orchestrator_step_unresolved` counter (label `workflowId`), injected `OrchestratorMetrics` into `OrchestratorPrePipeline`, incremented at the stage-1 and stage-3 L1-resolution misses
- Hermetic test coverage; 0-warning Debug + Release

**Out of scope (from SPEC.md):**
- The imported parallel-project Phase-72 uniform two-counter metrics reshape — lands separately via `/gsd-import`
- A `reason`/`stage` label on `orchestrator_step_unresolved` (label-minimal: `workflowId` only)
- Metering `completed-terminal` or the entry-condition skip
- The infra read-failure path (`ProcessorPipeline.cs:78` → REINJECT) — correctly writes no blob; unchanged
- Changing keeper states, meter names, or `AddMeter` registrations
- Output-definition validation + startup definition loading (already implemented)

</spec_lock>

<decisions>
## Implementation Decisions

### Stage-3 unresolved detection (SelectNext)
- **D-01:** `StepAdvancement.SelectNext` is reshaped to return a **structured result** — `{ matches: (Guid stepId, StepProjection step)[], unresolvedIds: Guid[] }` — by iterating `NextStepIds` ONCE and classifying each declared id three ways: (a) **missing from L1** → `unresolvedIds`; (b) **in L1 but `EntryCondition` ≠ outcome and ≠ Always** → silently filtered (legit conditional branch, NOT collected anywhere); (c) **in L1 and condition matches** → `matches`.
- **D-02:** `SelectNext` stays a **pure, no-I/O, harness-free function** (preserves the T-24-06 design intent) — it does NOT touch metrics or Redis. The pipeline (`OrchestratorPrePipeline`) increments `orchestrator_step_unresolved` once per `unresolvedId` (the stage-3 miss → continue, never throw) and fans out `matches`.
- **D-03:** The condition-filtered case (b) is NOT a resolution failure → **no metric**. Only the L1-miss (a) increments. A `matches` count of 0 with no `unresolvedIds` is the legit `completed-terminal` trip-end (log only, no metric).

### StepProcessing handling — Option (B)
- **D-04:** Always-write covers only the **terminal** outcomes — `Completed`, `Failed`, `Cancelled`. `StepProcessing` (the transient "still working" status) is **NOT** given an `out:` blob and keeps `EntryId = Guid.Empty`.
- **D-05:** A `Processing` result therefore has no blob → the orchestrator Pre-pipeline's **clean-absent idempotent skip** naturally prevents it from advancing (a still-processing step must not fan out to next steps). This narrows SPEC req 1's "all four outcomes" to the three terminal outcomes by design — the uniform orchestrator gate handles `Processing` for free via clean-absent.

### OutputTail write API
- **D-06:** `OutputTail` loses its `if (result == StepOutcome.Completed)` write gate (`OutputTail.cs:60`) and **always writes `dr.Data`** for the three terminal outcomes; output-definition validation (`:56-58`) continues to flip a failed-output Completed to `Failed` but never blocks the write.
- **D-07:** The `ProcessorPipeline` catch blocks (`:96-117`) and the input-schema-validation failure (`:83`) **route through `outputTail.RunAsync`** (write + send) instead of bypassing it via `SendResult`. They build a `DataResult{ Result=Failed/Cancelled/Processing, Data=validatedData, MessageId=… }` — no new `OutputTail` parameter; each upstream path owns what `dr.Data` is. A thrown `ExecuteAsync` is caught into a `DataResult{failed}` with the error message and `Data = validatedData` (the `:79` read).
- **D-08:** **Keep input-left-to-TTL on failure** — a failed/thrown path does NOT delete the input `data:` blob (preserves the C-2 model at `:84,107`; the TTL reaps it). Deleting the input on failure is out of scope (behavior change beyond this phase). The success path's input-delete (`:128-130`) is unchanged.

### Orchestrator uniform Pre-pipeline
- **D-09:** Drop both `if (outcome == StepOutcome.Completed)` branches in `OrchestratorPrePipeline` — the `out:` read (`:84`) and the delete (`:114`) — so the flow is identical for all (terminal) outcomes: exists-gate `L2[entryId]` → read → resolve `L1[stepId]` (stage-1 miss → log + increment + return) → SelectNext (D-01) → per next step (read `L1[nextStepId]` via the classification; unresolved → increment + continue; condition-mismatch → skip; match → send `NextStepHandoff`) → delete `L2[entryId]`.
- **D-10:** A cleanly-absent `L2[entryId]` (no Redis fault) → **idempotent skip** (ack, no fan-out, no keeper). A Redis *fault* on read/delete still escalates REINJECT/DELETE as today. `ExecutionId` stays threaded onto each `NextStepHandoff` (D-13). `outcome` is used ONLY for entry-condition matching, never for L2 behavior.

### Metric wiring
- **D-11:** `OrchestratorMetrics` gains `orchestrator_step_unresolved` (snake_case, no `_total` in code), built via the existing `IMeterFactory` pattern (never a static `Meter`); `OrchestratorMetrics` is constructor-injected into `OrchestratorPrePipeline` (Phase 71 deliberately omitted it). Label: `workflowId` only (camelCase, value `m.WorkflowId`). Incremented at stage-1 (consumed step/workflow missing from L1 → return) and stage-3 (each `unresolvedId` → continue). No `reason`/`stage` label — stage distinction lives in the existing distinct trip-end logs.

### Test migration
- **D-12:** **Rename/extend in-place** — update `OutputTailFacts` + the processor Pre/Post facts to assert always-write for every terminal outcome + real `EntryId`; update the Phase-71 orchestrator Pre-pipeline facts to assert the uniform gate + clean-absent skip + `Processing`-skips-via-clean-absent; add explicit `orchestrator_step_unresolved` emission assertions (workflowId label; increments at stage-1 & stage-3; does NOT increment on `completed-terminal`, condition-skip, or normal fan-out). Preserve test history.

### Claude's Discretion
- The exact `SelectNext` return type name/shape, the precise local plumbing for threading `validatedData` into the catch-built `DataResult`, the `OutputTail.RunAsync` signature mechanics, and the per-file test rename details are left to planning/execution.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi/72-SPEC.md` — Locked requirements, boundaries, acceptance criteria. MUST read before planning.

### Recovery pipeline (the two-consumer design this phase modifies)
- `docs/design/processor-keeper-recovery-spec.md` — canonical recovery spec for the INJECT/REINJECT/DELETE flows (marked superseded-by-Phase-70 but the SoT for the recovery semantics).
- `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-SPEC.md` — the processor two-consumer design (D-01..D-17) this phase's always-write builds on.
- `.planning/phases/71-orchestrator-two-consumer-design-pre-process-post-process-wi/71-SPEC.md` — the orchestrator two-consumer design (REQ-71-01..12); the deferred trip-end metric (D-18) is realized here.

### Code touchpoints (full paths)
- `src/BaseProcessor.Core/Processing/OutputTail.cs` — remove the Completed-only write gate (`:60`); output-validation→Failed already at `:56-58`
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — catch blocks (`:96-117`) + input-validation-fail (`:83`) route through OutputTail; `BuildFailed`/`BuildCancelled` real EntryId (`:199,202`); `validatedData` read (`:79`); input-delete success-only kept (`:128-130`)
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` — drop Completed-only branches (`:84,114`); inject `OrchestratorMetrics`; stage-1/stage-3 increments; clean-absent skip; ExecutionId thread (`:105`)
- `src/Orchestrator/Dispatch/StepAdvancement.cs` — `SelectNext` (`:36-43`) reshaped to `{matches, unresolvedIds}`
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` — add `orchestrator_step_unresolved` (IMeterFactory, no static Meter)
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` — the four shells (`StepCompleted/Failed/Cancelled/Processing`); stay uniform (D-07 no status if/switch)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `OutputData` key + `OutputDataTtl` (unchanged TTL policy)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`OutputTail`** — the shared write tail (Pre inline tail + PostProcessConsumer); already validates output→Failed and owns the write/send. Only the Completed-only write gate is removed.
- **`IMeterFactory` DI pattern** — `OrchestratorMetrics` already builds via `meterFactory.Create("Orchestrator")`; the new counter follows the same shape. Inject the holder into `OrchestratorPrePipeline` (currently has none — Phase 71 omitted it to keep 0-warning).
- **`OrchestratorPrePipeline`** — already has the exists-gate/read/fan-out/delete skeleton + the two distinct trip-end LOG lines (`completed-unresolved`/`completed-terminal`); this phase removes the outcome branching and adds the metric.
- **`validatedData`** (`ProcessorPipeline.cs:79`) — read before every business failure point; in scope in both catch blocks for the failed-result fallback.

### Established Patterns
- Instrument names snake_case, no `_total` in code (collector appends it); label keys camelCase. Meters via `IMeterFactory`, never static.
- Resilience: send→bounded RetryLoop→throw→broker redelivery; every L2 op→keeper (REINJECT/DELETE/INJECT) on exhaustion; clean-absent ≠ escalation.
- D-07: routing is by message type; NO status if/switch in the result consumers — the lone `outcome` check stays a relocation concern inside the pipeline (and is now removed entirely).

### Integration Points
- Processor `out:` write → orchestrator `out:` read/relocate → `RelocateTail` writes next step's `data:` input → `EntryStepDispatch`. Always-write makes every terminal result carry a real `EntryId` into this chain.
- `orchestrator_step_unresolved` is consumed by Prometheus (label `workflowId`); independent of the imported uniform two-counter reshape.

</code_context>

<specifics>
## Specific Ideas

- The metric counts L1-graph-resolution failures, NOT data/L2 problems — stage-1 (consumed step missing) + stage-3 (declared next-step missing). Condition-filtered successors and clean-absent L2 are not failures and are not counted.
- `Processing` deliberately rides the clean-absent skip rather than getting its own no-advance special-case — keeps the orchestrator branch-free.

</specifics>

<deferred>
## Deferred Ideas

- Deleting the input `data:` blob on failure (full uniform input lifecycle) — deferred; current C-2 model leaves it to TTL.
- The imported parallel-project Phase-72 uniform two-counter metrics reshape (`{service}_messages_consumed`/`_sent`, `keeper_l2_probe`, analyzer rework) — separate `/gsd-import`, independent of this phase.

</deferred>

---

*Phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi*
*Context gathered: 2026-06-17*
