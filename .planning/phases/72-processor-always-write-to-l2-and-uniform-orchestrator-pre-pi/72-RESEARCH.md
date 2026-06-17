# Phase 72: Processor Always-Write to L2 + Uniform Orchestrator Pre-Pipeline + Unresolved-Step Metric — Research

**Researched:** 2026-06-17
**Domain:** .NET / C# message-bus consumers; Redis L2 projections; System.Diagnostics.Metrics (IMeterFactory)
**Confidence:** HIGH (every touchpoint verified against live code; line numbers re-grounded below)

## Summary

This phase is tightly specified: SPEC.md (6 locked requirements) and CONTEXT.md (D-01..D-13) decide the WHAT and most of the HOW. This research **verified every cited line number and signature against the live code** and re-grounds them with real values. The live code matches the SPEC's described *shape* very closely — the asymmetry (Completed-only write gate in `OutputTail`, two `outcome == Completed` branches in `OrchestratorPrePipeline`, `Guid.Empty` EntryId on non-completed results, no metrics holder in the orchestrator pipeline) is exactly as described and ready to be removed. A handful of line numbers have drifted by 1-3 lines; all are itemized in the **Drift Report**.

The plumbing is concentrated in four production files (`OutputTail.cs`, `ProcessorPipeline.cs`, `OrchestratorPrePipeline.cs`, `StepAdvancement.cs`), one metrics holder (`OrchestratorMetrics.cs`), one DI site (`Orchestrator/Program.cs`), and four test files to extend in-place (`OutputTailFacts`, `PrePipelineFacts`, `OrchestratorPrePipelineFacts`, `OrchestratorMetricsFacts` + `StepAdvancementTests`). All test infrastructure exists today (NSubstitute Redis fakes, capturing send providers, capturing loggers, real `IMeterFactory`) — no new framework or fixture scaffolding is required; the Wave-0 gap is purely a metrics-assertion seam (a `MeterListener` or `MetricCollector<long>`) for the new counter.

**Primary recommendation:** Remove the two write gates and the two relocation branches at their exact sites (re-grounded below), reshape `SelectNext` to a struct result, constructor-inject `OrchestratorMetrics` into `OrchestratorPrePipeline` (the only new DI edge), and extend the four existing fact classes in-place. Use a `System.Diagnostics.Metrics.MetricCollector<long>` (Microsoft.Extensions.Diagnostics.Testing) or a raw `MeterListener` for the new counter's increment assertions — this is the single missing test seam.

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-13)

- **D-01:** `StepAdvancement.SelectNext` is reshaped to return a **structured result** — `{ matches: (Guid stepId, StepProjection step)[], unresolvedIds: Guid[] }` — by iterating `NextStepIds` ONCE and classifying each declared id three ways: (a) **missing from L1** → `unresolvedIds`; (b) **in L1 but `EntryCondition` ≠ outcome and ≠ Always** → silently filtered (legit conditional branch, NOT collected anywhere); (c) **in L1 and condition matches** → `matches`.
- **D-02:** `SelectNext` stays a **pure, no-I/O, harness-free function** (preserves T-24-06 design intent) — it does NOT touch metrics or Redis. The pipeline (`OrchestratorPrePipeline`) increments `orchestrator_step_unresolved` once per `unresolvedId` (the stage-3 miss → continue, never throw) and fans out `matches`.
- **D-03:** The condition-filtered case (b) is NOT a resolution failure → **no metric**. Only the L1-miss (a) increments. A `matches` count of 0 with no `unresolvedIds` is the legit `completed-terminal` trip-end (log only, no metric).
- **D-04:** Always-write covers only the **terminal** outcomes — `Completed`, `Failed`, `Cancelled`. `StepProcessing` (transient "still working") is **NOT** given an `out:` blob and keeps `EntryId = Guid.Empty`.
- **D-05:** A `Processing` result therefore has no blob → the orchestrator Pre-pipeline's **clean-absent idempotent skip** naturally prevents it from advancing. This narrows SPEC req 1's "all four outcomes" to the three terminal outcomes by design.
- **D-06:** `OutputTail` loses its `if (result == StepOutcome.Completed)` write gate and **always writes `dr.Data`** for the three terminal outcomes; output-definition validation continues to flip a failed-output Completed to `Failed` but never blocks the write.
- **D-07:** The `ProcessorPipeline` catch blocks + the input-schema-validation failure **route through `outputTail.RunAsync`** (write + send) instead of bypassing it via `SendResult`. They build a `DataResult{ Result=Failed/Cancelled/Processing, Data=validatedData, MessageId=… }` — no new `OutputTail` parameter; each upstream path owns what `dr.Data` is. A thrown `ExecuteAsync` is caught into a `DataResult{failed}` with the error message and `Data = validatedData`.
- **D-08:** **Keep input-left-to-TTL on failure** — a failed/thrown path does NOT delete the input `data:` blob. The success path's input-delete is unchanged.
- **D-09:** Drop both `if (outcome == StepOutcome.Completed)` branches in `OrchestratorPrePipeline` (the `out:` read and the delete) → identical flow for all terminal outcomes: exists-gate `L2[entryId]` → read → resolve `L1[stepId]` (stage-1 miss → log + increment + return) → SelectNext (D-01) → per next step (unresolved → increment + continue; condition-mismatch → skip; match → send `NextStepHandoff`) → delete `L2[entryId]`.
- **D-10:** A cleanly-absent `L2[entryId]` (no Redis fault) → **idempotent skip** (ack, no fan-out, no keeper). A Redis *fault* on read/delete still escalates REINJECT/DELETE as today. `ExecutionId` stays threaded onto each `NextStepHandoff`. `outcome` is used ONLY for entry-condition matching, never for L2 behavior.
- **D-11:** `OrchestratorMetrics` gains `orchestrator_step_unresolved` (snake_case, no `_total` in code), built via the existing `IMeterFactory` pattern (never a static `Meter`); constructor-injected into `OrchestratorPrePipeline` (Phase 71 deliberately omitted it). Label: `workflowId` only (camelCase, value `m.WorkflowId`). Incremented at stage-1 and stage-3. No `reason`/`stage` label.
- **D-12:** **Rename/extend in-place** — update `OutputTailFacts` + the processor Pre/Post facts to assert always-write for every terminal outcome + real `EntryId`; update the Phase-71 orchestrator Pre-pipeline facts to assert the uniform gate + clean-absent skip + `Processing`-skips-via-clean-absent; add explicit `orchestrator_step_unresolved` emission assertions. Preserve test history.
- **D-13 (SPEC constraint):** `ExecutionId` MUST remain threaded onto every `NextStepHandoff`.

### Claude's Discretion

- The exact `SelectNext` return type name/shape, the precise local plumbing for threading `validatedData` into the catch-built `DataResult`, the `OutputTail.RunAsync` signature mechanics, and the per-file test rename details are left to planning/execution.

### Deferred Ideas (OUT OF SCOPE)

- Deleting the input `data:` blob on failure (full uniform input lifecycle) — deferred; current C-2 model leaves it to TTL.
- The imported parallel-project Phase-72 uniform two-counter metrics reshape (`{service}_messages_consumed`/`_sent`, `keeper_l2_probe`, analyzer rework) — separate `/gsd-import`, independent of this phase.
- Adding a `reason`/`stage` label to `orchestrator_step_unresolved`.
- Metering the `completed-terminal` trip-end or the entry-condition skip.
- The infra read-failure path (`ProcessorPipeline.cs` input-read exhausted → REINJECT) — correctly writes no blob; unchanged.
- Live-stack close-gate run — deferred-automated where the sandbox lacks Docker.
- Output-definition validation + startup definition loading (already implemented).
</user_constraints>

<phase_requirements>
## Phase Requirements (6 locked, from 72-SPEC.md)

| ID | Description | Research Support |
|----|-------------|------------------|
| REQ-1 | Processor writes a blob for every terminal business outcome (no Completed-only write gate) | `OutputTail.cs:60` gate verified; removal site + always-write target mapped. Narrowed to 3 terminal outcomes by D-04/D-05. |
| REQ-2 | Non-completed results carry a real `EntryId` (= output messageId) + data | `BuildStep` arms at `OutputTail.cs:83-86` set `EntryId = Guid.Empty`; `BuildFailed/BuildCancelled` at `ProcessorPipeline.cs:198-202` set `EntryId = Guid.Empty`. Both verified. EntryId is `init`-settable on all four `Step*` records. |
| REQ-3 | Thrown `ExecuteAsync` caught into a failed `DataResult` carrying `validatedData` | `validatedData` read at `ProcessorPipeline.cs:79`; catch blocks at `:96-117`. Both verified in scope. |
| REQ-4 | New `orchestrator_step_unresolved` counter at stage-1 + stage-3 L1 misses, label `workflowId` only | `OrchestratorMetrics` ctor at `:44-50` (IMeterFactory pattern); `OrchestratorPrePipeline` ctor at `:46-52` (no metrics holder — verified). Stage-1 miss at `:58`; stage-3 miss currently silent in `SelectNext` `:40`. |
| REQ-5 | Uniform orchestrator Pre-pipeline (no Completed-only branch) | Two `outcome == StepOutcome.Completed` branches verified at `OrchestratorPrePipeline.cs:84` (read) and `:114` (delete). |
| REQ-6 | Naming + DI conventions preserved (snake_case no `_total`, camelCase label, IMeterFactory, meter name `Orchestrator`, 0-warning build) | `OrchestratorMetrics.cs` conventions verified (`MeterName = "Orchestrator"` const; existing counters snake_case). DI site verified at `Program.cs:101-102`. |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Always-write `out:` blob for terminal outcomes | Processor (`OutputTail`) | Processor (`ProcessorPipeline` catch routing) | The write tail is the single source of truth shared by Pre inline tail + PostProcessConsumer; the gate lives in `OutputTail.RunAsync`. |
| Real `EntryId` stamping on `Step*` | Processor (`OutputTail.BuildStep` + `ProcessorPipeline` builders) | — | `EntryId = dr.MessageId` is stamped where the `Step*` record is constructed; two construction sites exist (the tail's `BuildStep` and the pipeline's `BuildFailed/BuildCancelled`). |
| Threading `validatedData` into catch-built results | Processor (`ProcessorPipeline` catch blocks) | — | `validatedData` is the `:79` input local, in scope through every catch. |
| Uniform Pre-pipeline (drop outcome branches) | Orchestrator (`OrchestratorPrePipeline`) | — | The relocation read/delete are orchestrator-side L2 ops; removing the branch makes all four `TypedResultConsumer<T>` shells uniform. |
| Stage-3 unresolved classification | Orchestrator (`StepAdvancement.SelectNext`, pure) | Orchestrator (`OrchestratorPrePipeline` increment loop) | `SelectNext` stays pure (D-02); the pipeline owns the metric + the graceful-skip `continue`. |
| `orchestrator_step_unresolved` counter definition | Orchestrator (`OrchestratorMetrics`) | Orchestrator (`Program.cs` DI) | Counter built via the holder's `IMeterFactory`; injected into the pipeline via DI. |

---

## Drift Report (live code vs. SPEC/CONTEXT line citations)

Verified each cited line against the live files. The structural shape matches; several line numbers drifted. **Planner must use the REAL line numbers below.**

| Touchpoint | SPEC/CONTEXT cited | ACTUAL (verified) | Drift |
|-----------|---------------------|-------------------|-------|
| `OutputTail` Completed-only write gate | `OutputTail.cs:60` | `OutputTail.cs:60` (`if (result == StepOutcome.Completed)`) | ✅ exact |
| `OutputTail` output-validation→Failed | `OutputTail.cs:56-58` | `OutputTail.cs:56-58` | ✅ exact |
| `OutputTail.RunAsync` signature | (implied) | `public async Task<bool> RunAsync(DataResult dr, Guid deleteEntryId, CancellationToken ct)` at `:48` | new fact |
| `OutputTail.BuildStep` Failed/Cancelled `EntryId = Guid.Empty` | (implied by req 7) | `OutputTail.cs:83-86` — Failed/Cancelled arms hard-set `EntryId = Guid.Empty`; **Completed already stamps `EntryId = dr.MessageId` at `:82`** | DRIFT — SPEC frames EntryId only on `ProcessorPipeline`, but the tail's `BuildStep` ALSO stamps `Guid.Empty` on non-completed. **Both sites must change.** |
| `ProcessorPipeline` catch blocks | `ProcessorPipeline.cs:96-117` | `:96-117` (`:96-108` ProcessStatusException; `:109-117` unexpected) | ✅ exact |
| `ProcessorPipeline` input-validation-fail | `ProcessorPipeline.cs:83` | `:81-85` (`TryValidate` at `:81`; `SendResult(BuildFailed…)` at `:83`) | ✅ exact |
| `ProcessorPipeline` `BuildFailed`/`BuildCancelled` | `ProcessorPipeline.cs:199,202` | `BuildFailed` at `:198-199`; `BuildCancelled` at `:201-202` | ~1 line — `BuildFailed` body spans `:198-199` |
| `ProcessorPipeline` `validatedData` read | `ProcessorPipeline.cs:79` | `:79` (`var validatedData = read.Value!;`) | ✅ exact |
| `ProcessorPipeline` input-delete success path | `ProcessorPipeline.cs:128-130` | `:128-130` (KeyDeleteAsync after tail) | ✅ exact |
| `OrchestratorPrePipeline` outcome==Completed read branch | `:84` | `:84` (`if (outcome == StepOutcome.Completed)` wrapping the read `:84-93`) | ✅ exact |
| `OrchestratorPrePipeline` outcome==Completed delete branch | `:114` | `:114` (`if (outcome == StepOutcome.Completed)` wrapping the delete `:114-119`) | ✅ exact |
| `OrchestratorPrePipeline` stage-1 trip-end log | `:58` | `:58` (`if (!store.TryGet… || !wf.Steps.TryGetValue…)`; log `:60-62`) | ✅ exact |
| `OrchestratorPrePipeline` stage-2 terminal trip-end log | `:69` | `:69-74` (`if (matches.Count == 0)`) | ✅ exact |
| `OrchestratorPrePipeline` ctor (no metrics holder) | `:37-43` | `:46-52` — ctor params are `(IWorkflowL1Store store, StepAdvancement advancement, IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IOptions<RetryOptions> retryOptions, ILogger<OrchestratorPrePipeline> logger)` | DRIFT — ctor is at `:46-52`, not `:37-43`. **Confirmed: NO `OrchestratorMetrics` injected.** |
| `OrchestratorPrePipeline` ExecutionId thread (D-13) | `:105` | `:105` (`ExecutionId = m.ExecutionId` on the handoff) | ✅ exact |
| `StepAdvancement.SelectNext` | `:36-43` | `:36-43` (signature `:36-37`; loop `:39-42`) | ✅ exact |
| `OrchestratorMetrics` ctor + `IMeterFactory.Create("Orchestrator")` | (implied) | ctor `:44-50`; `meterFactory.Create(MeterName)` at `:46`; `MeterName = "Orchestrator"` const at `:25` | ✅ verified |
| `L2ProjectionKeys.OutputData` + `OutputDataTtl` | (implied) | `OutputData(Guid messageId)` at `:62`; `OutputDataTtl(int ttlSeconds)` at `:70-71` | ✅ verified |
| Four `TypedResultConsumer<T>` shells registered | `Program.cs:61-64` | `Program.cs:61-64` (`StepCompleted/Failed/Cancelled/Processing` consumers) | ✅ exact |

**Critical drift to flag for planner:** The SPEC/CONTEXT narrative attributes the `Guid.Empty` EntryId only to `ProcessorPipeline.BuildFailed/BuildCancelled` (`:198-202`). **In live code there are TWO `Guid.Empty` sources:** (1) `ProcessorPipeline.BuildFailed/BuildCancelled` (the catch/input-fail path, which D-07 reroutes through `OutputTail` anyway), AND (2) `OutputTail.BuildStep`'s Failed/Cancelled/Processing arms (`:83-87`), which the *Completed* tail path already exercises (an output-validation-forced-Failed currently emits `EntryId = Guid.Empty`). Once everything routes through `OutputTail` (D-07) and the write is unconditional (D-06), **`OutputTail.BuildStep` is where the real `EntryId = dr.MessageId` stamp belongs for Failed/Cancelled** — because the tail is the single point that both writes the blob (under `dr.MessageId`) and builds the `Step*`. This is the cleanest seam: stamp `EntryId = dr.MessageId` for the terminal arms in `BuildStep`, and `Processing` keeps `Guid.Empty` (D-04).

---

## Standard Stack

No new packages. This phase uses only the already-present libraries.

### Core (already referenced)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Diagnostics.Metrics | net8.0 BCL | `IMeterFactory` / `Counter<long>` for the new counter | Established orchestrator convention (`OrchestratorMetrics` already uses it; D-11/REQ-6 mandate it, never a static `Meter`) |
| StackExchange.Redis | 2.13 (per DispatchTestKit comments) | L2 read/write/delete | Existing L2 projection layer |
| MassTransit | (existing) | `ISendEndpointProvider` send pipeline | Existing bus layer |
| NSubstitute | (existing) | Test doubles for `IDatabase`/`ISendEndpoint` | Existing hermetic test pattern |
| xUnit (v3 — `TestContext.Current.CancellationToken`) | (existing) | Test framework | Existing |

### Supporting (test-only — likely needs adding for metric assertions)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.Diagnostics.Testing | 8.x/9.x | `MetricCollector<long>` — the blessed in-test counter-capture seam | Asserting `orchestrator_step_unresolved` increments + the `workflowId` tag without a live Prometheus collector |

**Version verification:** `Microsoft.Extensions.Diagnostics.Testing` ships the `MetricCollector<T>` type for exactly this purpose (verify `<TargetFramework>` compatibility against the test project). **`[ASSUMED]`** — the test project may not currently reference it; a raw `System.Diagnostics.Metrics.MeterListener` (BCL, zero new deps) is a fully viable fallback and matches the "no new packages" spirit. **Planner should confirm which the team prefers** (see Assumptions Log A1).

**Installation (only if MetricCollector chosen):**
```bash
dotnet add tests/BaseApi.Tests package Microsoft.Extensions.Diagnostics.Testing
```

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `MetricCollector<long>` | Raw `MeterListener` over the `"Orchestrator"` meter | `MeterListener` adds zero deps and is BCL-stable; slightly more boilerplate (subscribe → `RecordObservableInstruments`/measurement callback → filter by instrument name → capture tag). For a single counter it's ~15 lines. |
| Asserting the metric directly | Assert only the distinct trip-end *log line* (as Phase 71 does) | Insufficient — REQ-4/REQ-6 acceptance explicitly require the *counter* to increment with the `workflowId` label; a log-only assertion would not prove the metric. |

---

## Architecture Patterns

### System Data-Flow Diagram

```
                    PROCESSOR SIDE (always-write)
  EntryStepDispatch ──► ProcessorPipeline.RunAsync
        │
        ├─ exists-gate L2[data:entryId]  ──fault──► REINJECT (unchanged, out of scope)
        ├─ read L2[data:entryId] = validatedData (:79)
        │       └─fault──► REINJECT (unchanged)
        ├─ input-schema validate (:81)
        │       └─FAIL──► [D-07] build DataResult{Failed, Data=validatedData} ──┐
        ├─ ExecuteAsync(validatedData) (:95)                                     │
        │       ├─ ProcessStatusException ──► DataResult{Failed/Cancelled/Proc} ┤
        │       │                                  (Data=validatedData) (:96-108)│
        │       ├─ unexpected/deser ──► DataResult{Failed, "input deser failed", │
        │       │                          Data=validatedData} (:109-117)        │
        │       └─ returns DataResult dr ──────────────────────────────────────►│
        │                                                                        ▼
        └────────────────────────────────► OutputTail.RunAsync(dr, deleteEntryId, ct)
                                                  │
                  [D-06] output-validate: Completed w/ bad output → Failed (:56-58)
                                                  │
                  [REQ-1] ALWAYS write L2[out:messageId] = dr.Data  (drop :60 gate)
                          (Completed | Failed | Cancelled; Processing = no blob, D-04)
                          write-exhaust ──► INJECT + return false (unchanged)
                                                  │
                  [REQ-2] BuildStep: EntryId = dr.MessageId for terminal arms (:79-89)
                          (Processing keeps Guid.Empty)
                                                  │
                          send Step* ──► orchestrator-result queue
                                                  │
        ┌─────────────────────────────────────────┘
        ▼  (Pre path only) delete L2[data:entryId] on success (:128-130, unchanged)


                    ORCHESTRATOR SIDE (uniform pre-pipeline)
  Step{Completed|Failed|Cancelled|Processing} ──► TypedResultConsumer<T>.Consume
        │  (ResultConsumed counter incremented at top, unchanged)
        ▼
  OrchestratorPrePipeline.RunAsync(m, outcome, messageId, ct)
        │
        ├─ STAGE-1: resolve L1[workflowId][stepId] (:58)
        │     └─MISS──► log "completed-unresolved" + [REQ-4] metrics.StepUnresolved.Add(1, workflowId) + return
        │
        ├─ STAGE-2: SelectNext(outcome, completed, steps) → {matches, unresolvedIds}  [D-01]
        │     └─ matches.Count==0 && unresolvedIds empty ──► log "completed-terminal" + return (NO metric, D-03)
        │
        ├─ [D-09/REQ-5] exists/read L2[out:entryId]  (NO outcome==Completed gate)
        │     ├─ Redis FAULT ──► REINJECT + return (unchanged)
        │     └─ clean-absent ──► [D-10] idempotent skip: ack, no fan-out, no keeper, no delete
        │                          (Processing rides this skip for free — D-05)
        │
        ├─ STAGE-3: foreach unresolvedId ──► [REQ-4] metrics.StepUnresolved.Add(1, workflowId) + continue (D-02)
        │           foreach match ──► send NextStepHandoff (Data=relocated, ExecutionId threaded :105)
        │
        └─ [D-09/REQ-5] delete L2[out:entryId]  (NO outcome==Completed gate)
                  └─ delete-exhaust ──► DELETE keeper + return (unchanged)
```

### Pattern 1: Always-write at the single tail (REQ-1)
**What:** Remove the `if (result == StepOutcome.Completed)` write gate at `OutputTail.cs:60`; write `L2[out:dr.MessageId] = dr.Data` for Completed/Failed/Cancelled. `Processing` must NOT write (D-04).
**When to use:** This is the source-of-asymmetry removal — it makes every terminal `Step*` carry a real blob the orchestrator can read uniformly.
**Implementation note:** The current structure is:
```csharp
// Source: src/BaseProcessor.Core/Processing/OutputTail.cs:56-69 (VERIFIED)
var result = dr.Result;
if (result == StepOutcome.Completed
    && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, dr.Data, out _))
    result = StepOutcome.Failed;

if (result == StepOutcome.Completed)        // ← REQ-1: this write gate is removed
{
    var write = await RetryLoop.ExecuteAsync(
        () => db.StringSetAsync(L2ProjectionKeys.OutputData(dr.MessageId), dr.Data, JitteredTtl()), limit, ct);
    if (!write.Succeeded)
    {
        await SendKeeper(BuildInject(dr, deleteEntryId), limit, ct);
        return false;
    }
}
await SendResult(BuildStep(dr, result), limit, ct);
return true;
```
Target: the write block runs for `result is Completed or Failed or Cancelled` (not `Processing`). The INJECT-on-write-exhaust + `return false` semantics are preserved for all terminal outcomes. **Decision point for planner:** whether the gate becomes `result != StepOutcome.Processing` (write for the 3 terminals) — this is the cleanest expression of D-04/D-06.

### Pattern 2: Real EntryId on terminal Step* (REQ-2)
**What:** `BuildStep` (`OutputTail.cs:79-89`) currently stamps `EntryId = dr.MessageId` only on the Completed arm (`:82`); Failed/Cancelled set `Guid.Empty` (`:84,86`). Stamp `EntryId = dr.MessageId` on Failed + Cancelled too; `Processing` keeps `Guid.Empty` (`:87-88`, no blob).
**Why the tail (not the pipeline) is the right seam:** Once D-07 routes the catch blocks through `OutputTail`, the tail is the single construction point that both writes the blob under `dr.MessageId` AND builds the `Step*` — so the EntryId stamp stays co-located with the write. `ProcessorPipeline.BuildFailed/BuildCancelled` (`:198-202`) become dead on the business path once their callers route through the tail (the only remaining direct `SendResult` callers are removed by D-07).
**Verified fact:** all four `Step*` records expose `public Guid EntryId { get; init; }` (StepCompleted has no default; the other three default `Guid.Empty`). So the stamp is a plain object-initializer change.

### Pattern 3: Route catch blocks through OutputTail carrying validatedData (REQ-3, D-07)
**What:** Replace the three `SendResult(BuildXxx(d, …))` calls in the catch/input-fail paths with `outputTail.RunAsync(dr, d.EntryId, ct)` where `dr` is a `DataResult{ Result=…, Data=validatedData, MessageId=messageId, …ids }`.
**Verified scope:** `validatedData` is the `:79` local, in scope through the input-validate fail (`:81-85`), the `ProcessStatusException` catch (`:96-108`), and the unexpected catch (`:109-117`). **Caveat:** the input-validation-fail at `:81-85` is reached BEFORE `SetSeamState`; `validatedData` is available, but note the WR-03 sanitization rule (the unexpected-catch emits the constant `"input deserialization failed"`, never `ex.Message`) must be preserved — the `DataResult.Data` carries `validatedData`, but the `ErrorMessage` on a deser failure stays the sanitized constant.
**Threading mechanic (discretion, D-07):** each path builds its own `DataResult` (input-fail → `Failed` + `string.Join("; ", inErrs)` as error; `ProcessStatusException` → mapped Failed/Cancelled/Processing + `e.Message`; unexpected → `Failed` + `"input deserialization failed"`). All carry `Data = validatedData` and `MessageId = messageId`. **Processing caveat (D-04):** a `ProcessingException` builds a `DataResult{Processing}` — but `OutputTail` must NOT write a blob for it (D-04) and must keep `EntryId = Guid.Empty`; the Pattern-1 gate (`result != Processing`) handles this automatically.

### Pattern 4: SelectNext → structured classification (D-01/D-02)
**What:** Reshape `SelectNext` from `IEnumerable<(Guid, StepProjection)>` (yield-return, `:36-43`) to a pure function returning `{ matches: (Guid stepId, StepProjection step)[], unresolvedIds: Guid[] }`. Iterate `NextStepIds ?? Enumerable.Empty<Guid>()` ONCE; classify each id: not in `steps` → `unresolvedIds`; in `steps` but `EntryCondition != (int)outcome && != Always` → filtered (collected nowhere); else → `matches`.
**Verified current body:**
```csharp
// Source: src/Orchestrator/Dispatch/StepAdvancement.cs:39-42 (VERIFIED)
foreach (var nextId in completed.NextStepIds ?? Enumerable.Empty<Guid>())
    if (steps.TryGetValue(nextId, out var next) &&
        (next.EntryCondition == (int)outcome || next.EntryCondition == Always))
        yield return (nextId, next);
```
The dangling-id (case a) and condition-mismatch (case b) are TODAY both silently dropped by the single `if`. The reshape splits them: case (a) → `unresolvedIds`, case (b) → still dropped. **`Always = 4` is a private const at `:23`; `Never = 5` falls out of the predicate** — both preserved.
**Return-type shape (discretion):** a `readonly record struct` (e.g., `SelectNextResult(IReadOnlyList<(Guid stepId, StepProjection step)> Matches, IReadOnlyList<Guid> UnresolvedIds)`) keeps it allocation-light and harness-free. **Pitfall:** the existing `StepAdvancementTests` call `SelectNext(...).Select(s => s.stepId)` and `.ToList()` directly — those tests must migrate to `.Matches.Select(...)` (D-12 in-place extension), and the existing `DanglingNextStepId_IsSkipped_NoThrow` fact now also asserts the dangling id appears in `.UnresolvedIds`.

### Pattern 5: Uniform pre-pipeline (drop both outcome branches) (REQ-5/D-09)
**What:** Remove `if (outcome == StepOutcome.Completed)` at `:84` (read) and `:114` (delete). The read/exists-gate/delete run for every terminal outcome. The clean-absent skip (D-10) replaces the current `relocated = ""` non-completed path.
**Critical behavior shift to design:** TODAY a non-completed result skips the read entirely and proceeds with `relocated = ""` (always fans out). AFTER this phase, a non-completed result reads `L2[out:entryId]`; if **cleanly absent** → idempotent skip (NO fan-out, ack). This is the mechanism by which `Processing` (no blob, D-05) stops advancing. **The current code reads `OutputData(m.EntryId)` and treats `raw.IsNullOrEmpty ? "" : raw.ToString()` as success** (`:88-89`) — i.e., clean-absent currently means "proceed with empty data." The new D-10 contract requires clean-absent to mean "skip fan-out + delete entirely." Planner must add an explicit absent-check that short-circuits to ack (distinct from the existing Redis-fault → REINJECT path at `:91`).

### Pattern 6: Inject OrchestratorMetrics + increment (REQ-4/D-11)
**What:** Add `orchestrator_step_unresolved` to `OrchestratorMetrics` (mirror `:47-49`); add `OrchestratorMetrics metrics` to the `OrchestratorPrePipeline` primary ctor (`:46-52`); increment `metrics.StepUnresolved.Add(1, new KeyValuePair<string,object?>("workflowId", m.WorkflowId.ToString("D")))` at stage-1 (`:58` branch) and per `unresolvedId` at stage-3.
**Verified pattern to mirror:**
```csharp
// Source: src/Orchestrator/Observability/OrchestratorMetrics.cs:44-50 (VERIFIED)
public OrchestratorMetrics(IMeterFactory meterFactory)
{
    var meter = meterFactory.Create(MeterName);          // MeterName = "Orchestrator" (:25)
    DispatchSent   = meter.CreateCounter<long>("orchestrator_dispatch_sent");
    ResultConsumed = meter.CreateCounter<long>("orchestrator_result_consumed");
    ResultDeduped  = meter.CreateCounter<long>("orchestrator_result_deduped");
}
```
Add: `StepUnresolved = meter.CreateCounter<long>("orchestrator_step_unresolved");` + a `public Counter<long> StepUnresolved { get; }` property. **DI note:** `OrchestratorMetrics` is already a `AddSingleton` (`Program.cs:101`); injecting it into the `AddScoped<OrchestratorPrePipeline>` (`:90`) is valid (singleton into scoped). No `Program.cs` change is needed beyond the (automatic) ctor resolution — the DI container already has both registered. **Verify:** the existing `OrchestratorPrePipelineFacts.Build` helper (`:157-162`) constructs the pipeline directly with no metrics arg — it must be updated to pass an `OrchestratorMetrics` built from a real `IMeterFactory` (mirror `DispatchTestKit.Metrics()` / `OrchestratorMetricsFacts`).

### Anti-Patterns to Avoid
- **Static `Meter`:** REQ-6/D-11 forbid it — always `IMeterFactory.Create("Orchestrator")` (hermetic test isolation). Verified the existing holder already complies.
- **`_total` in the instrument name:** the collector's Prometheus exporter appends it (verified in `OrchestratorMetrics.cs:18-20,47` comments). Use `orchestrator_step_unresolved`, assert `orchestrator_step_unresolved_total` only in a (deferred) live-stack check.
- **`SelectNext` touching metrics/Redis:** D-02 — the pure function must stay harness-free; the pipeline owns the increment + the `continue`.
- **Throwing on a dangling next-step:** the stage-3 miss is a graceful `continue` (preserves T-24-06 no-throw); only adds observability.
- **Writing an `out:` blob for `Processing`:** D-04 — `Processing` keeps `Guid.Empty` and no blob; it must ride the clean-absent skip.
- **Escalating to the keeper on clean-absent:** D-10 — clean-absent ≠ fault; only a Redis *exception* escalates.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| In-test metric capture | A custom counter wrapper or a fake `Meter` | `MetricCollector<long>` OR a BCL `MeterListener` over `"Orchestrator"` | `MeterListener` is the supported BCL subscription API; a hand-rolled fake breaks the IMeterFactory isolation REQ-6 requires |
| Redis fault injection in tests | New mux fakes | The existing `DispatchTestKit` + `OrchestratorPrePipelineFacts` private mux helpers (`OutPresentL2`, `ReadFaultL2`, `DeleteFaultL2`, `CleanAbsentL2`) | Full fault-surface coverage already exists; add only a "clean-absent-on-non-completed" variant (or reuse `OutPresentL2` with an empty dict) |
| Jittered TTL on the write | Inline `Random` | `L2ProjectionKeys.OutputDataTtl(...)` (already the single SoT, `:70-71`) | Keeper INJECT + processor share it; never duplicate |

**Key insight:** Nearly all plumbing already exists — this phase deletes gates and adds one counter + one DI edge. The only genuinely new test seam is metric capture.

## Common Pitfalls

### Pitfall 1: `Processing` accidentally getting a blob
**What goes wrong:** A naive "drop the gate entirely" makes `OutputTail` write a blob for `Processing` too, so the orchestrator's clean-absent skip never triggers and a still-processing step fans out.
**Why it happens:** SPEC req 1 literally says "all four outcomes"; CONTEXT D-04/D-05 narrows it to the 3 terminals.
**How to avoid:** Gate the write on `result != StepOutcome.Processing` (or `is Completed or Failed or Cancelled`), and keep `Processing`'s `EntryId = Guid.Empty` in `BuildStep`.
**Warning sign:** A `Processing` result that produces a fan-out in the orchestrator test.

### Pitfall 2: Clean-absent vs. Redis-fault conflation on the orchestrator read
**What goes wrong:** Treating an absent `L2[out:entryId]` as a fault → spurious REINJECT; or treating a fault as clean-absent → silent data loss.
**Why it happens:** The current code (`:88-89`) folds `IsNullOrEmpty` into a successful `""` read; the new D-10 semantics need three distinct outcomes: present (fan out + delete), clean-absent (ack, skip), fault (REINJECT/DELETE).
**How to avoid:** Inside the `RetryLoop` success path, branch on `raw.IsNullOrEmpty` → idempotent-skip return; only a thrown `RedisException` (loop exhaust) → REINJECT. Mirror the processor `ProcessorPipeline` gate which already distinguishes clean-absent (`:68`) from fault (`:67`).
**Warning sign:** A test with an empty L2 dict that produces a REINJECT instead of an ack.

### Pitfall 3: `SelectNext` callers break on the return-type change
**What goes wrong:** `OrchestratorPrePipeline.cs:68` does `advancement.SelectNext(...).ToList()` and iterates `foreach (var (stepId, step) in matches)`; `StepAdvancementTests` chains `.Select(...)`. Both break when the return type becomes a struct.
**How to avoid:** Update the pipeline call to consume `.Matches` and loop `.UnresolvedIds` for increments; migrate the 6 `StepAdvancementTests` facts to `.Matches`/`.UnresolvedIds` (D-12).
**Warning sign:** Compile errors at `OrchestratorPrePipeline.cs:68` and in `StepAdvancementTests`.

### Pitfall 4: NSubstitute `StringSetAsync` overload trap (existing, must preserve)
**What goes wrong:** Stubbing only one `StringSetAsync` overload false-greens (an unstubbed `Task<bool>` returns `false`). The new always-write tests for Failed/Cancelled writes must use the existing `DispatchTestKit.ReceivedStringSets` (overload-agnostic, by method name) + `StubWriteOk`/`StubWriteFault` (both real virtual overloads).
**How to avoid:** Reuse `DispatchTestKit.ReceivedStringSets(db)` and `HasNonNullTtl(...)` exactly as `OutputTailFacts` does today (`:45-49`).
**Warning sign:** A write-fault test that passes when it should escalate INJECT.

### Pitfall 5: 0-warning build — unused ctor param (CS9113)
**What goes wrong:** Phase 71 deliberately omitted the metrics holder from `OrchestratorPrePipeline` *specifically* because an unused primary-ctor param trips CS9113 and breaks the 0-warning gate (documented in the class XML doc `:40-42`). If the injected `OrchestratorMetrics` is added but not used on every path, the build can warn.
**How to avoid:** Add the param AND the two increment sites in the same change; never land the injection without the usage.
**Warning sign:** CS9113 "Parameter 'metrics' is unread" in Debug/Release.

## Code Examples

### Verified: the metric increment shape (mirror existing tag convention)
```csharp
// Pattern from ResultConsumed.Add — Source: src/Orchestrator/Consumers/TypedResultConsumer.cs:53 (VERIFIED)
metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));

// New (REQ-4 / D-11) — label key camelCase "workflowId", value m.WorkflowId:
metrics.StepUnresolved.Add(1, new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")));
```
Note the existing convention tags use PascalCase keys (`ProcessorId`), but REQ-6/D-11 explicitly require the *new* label key to be camelCase `workflowId`. This is an intentional deviation locked by the spec — assert it exactly.

### Verified: real EntryId stamp (BuildStep terminal arms)
```csharp
// Source: src/BaseProcessor.Core/Processing/OutputTail.cs:79-89 (VERIFIED — current)
StepOutcome.Completed => new StepCompleted(...) { ..., EntryId = dr.MessageId },          // already correct
StepOutcome.Failed    => new StepFailed(...)    { ..., EntryId = Guid.Empty, ... },         // REQ-2: → dr.MessageId
StepOutcome.Cancelled => new StepCancelled(...) { ..., EntryId = Guid.Empty, ... },         // REQ-2: → dr.MessageId
_                     => new StepProcessing(...) { ... },                                    // D-04: keep Guid.Empty (no blob)
```

## State of the Art

| Old Approach | Current (this phase) | When Changed | Impact |
|--------------|----------------------|--------------|--------|
| Completed-only write; non-completed → `Guid.Empty` EntryId, no blob | Always-write for 3 terminals; real EntryId | Phase 72 | Orchestrator becomes branch-free |
| `OrchestratorPrePipeline` special-cases `outcome == Completed` twice | Uniform flow for all terminals; clean-absent skip | Phase 72 | All 4 `TypedResultConsumer<T>` truly uniform |
| Trip-end metric deferred (Phase 71 D-18) | `orchestrator_step_unresolved` counter at stage-1 + stage-3 | Phase 72 | Observability for L1-graph-resolution misses |
| `SelectNext` yields matches, silently drops dangling ids | `SelectNext` returns `{matches, unresolvedIds}` (pure) | Phase 72 | Dangling next-step ids become observable |

**Deprecated/outdated after this phase:**
- `ProcessorPipeline.BuildFailed`/`BuildCancelled` (`:198-202`) on the business path — their callers route through `OutputTail` (D-07). They MAY remain only if still used by REINJECT/DELETE builders (verify: `BuildReinject`/`BuildDelete` use `d.*` directly, NOT `BuildFailed` — so `BuildFailed`/`BuildCancelled`/`BuildProcessing` likely become fully dead and should be removed to keep 0-warning, or the catch blocks keep using them to *build the DataResult's fields*).

## Runtime State Inventory

Not a rename/refactor/migration phase — this is a behavior change (gate removal + counter add). The one data-shape consideration:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | `L2[out:{messageId}]` blobs now written for Failed/Cancelled too (previously only Completed). Keyed by messageId, jittered TTL (`OutputDataTtl`). No key-format change. | None — new blobs use the existing key/TTL scheme; old behavior wrote fewer blobs, new writes more. No migration. |
| Live service config | None | None — verified: no config/env touchpoints; meter name + `AddMeter` unchanged (REQ-6). |
| OS-registered state | None | None. |
| Secrets/env vars | None | None. |
| Build artifacts | None | None — no package/project-file changes except possibly the test-project package ref (A1). |

---

## Validation Architecture

> Nyquist validation is ENABLED (`.planning/config.json: nyquist_validation: true`). This section is REQUIRED.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit **v3** (uses `TestContext.Current.CancellationToken`) + NSubstitute |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (single test project for processor + orchestrator facts) |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OutputTailFacts|FullyQualifiedName~PrePipelineFacts|FullyQualifiedName~OrchestratorPrePipelineFacts|FullyQualifiedName~StepAdvancementTests|FullyQualifiedName~OrchestratorMetrics"` |
| Full suite command | `dotnet test tests/BaseApi.Tests` |

### Hermetic test seams that already exist (reuse — D-12)
- **Processor Redis muxes:** `DispatchTestKit.ReadWriteDeleteOkL2`, `OutputWriteFaultL2`, `ReadOkDeleteFaultL2`, `GateFaultL2`, `CleanAbsentL2`, `ReadFaultL2`, `AbsentReadL2`.
- **Processor capture:** `DispatchTestKit.CapturingSendProvider` (captures `Sent` IStepResult + `SentKeeper` + `SentData`), `ReceivedStringSets(db)` (overload-agnostic write inspection), `HasNonNullTtl(...)`.
- **Processor seam:** `DispatchTestKit.FakeProcessor` has ctors for **return a DataResult**, **throw an Exception** (covers ProcessStatusException family + unexpected), and a Mode-2 delegate. `Result(outcome, data, messageId?)` builds the DataResult.
- **Orchestrator muxes:** `OrchestratorPrePipelineFacts.OutPresentL2`, `ReadFaultL2`, `DeleteFaultL2`, `Wrap`.
- **Orchestrator capture:** local `CapturingSendProvider` (captures `Handoffs` + `SentKeeper` + `OverrideMessageIds`), `CapturingLogger<T>` (captures formatted log lines), `Seed(...)` L1 store builder, `Step(...)` projection builder, `Completed(...)` result builder.
- **Metrics:** `OrchestratorMetricsFacts` builds `OrchestratorMetrics` from a real `IMeterFactory` (`ServiceCollection().AddMetrics()`); `DispatchTestKit.Metrics()` does the same for `ProcessorMetrics`.

### Wave-0 gap (the ONLY missing seam)
A counter-increment capture seam. Two options (A1):
- **Option A:** `MetricCollector<long>(meterFactory, "Orchestrator", "orchestrator_step_unresolved")` — assert `.GetMeasurementSnapshot()` count + the `workflowId` tag. Requires `Microsoft.Extensions.Diagnostics.Testing`.
- **Option B (zero new deps):** a small `MeterListener` test helper subscribing to `instrument.Meter.Name == "Orchestrator" && instrument.Name == "orchestrator_step_unresolved"`, accumulating `(value, tags)` into a list. ~15 lines; lives in the orchestrator test folder.

### Phase Requirements → Test Map
| Req | Behavior to prove | Test type | Where (extend in-place, D-12) | Exists? |
|-----|-------------------|-----------|-------------------------------|---------|
| REQ-1 | `OutputTail` writes `L2[out:messageId]=data` for **Completed, Failed, Cancelled**; NOT for Processing | unit | `OutputTailFacts` — change `NonCompleted_Failed_SkipsWrite…` to assert a write; add Cancelled + Processing(no-write) facts | ✅ extend |
| REQ-1 | No outcome path skips the write (sampling: all 3 terminal outcomes driven through the tail) | unit | `OutputTailFacts` (parameterize over the 3 terminals) | ✅ extend |
| REQ-2 | `StepFailed`/`StepCancelled` carry `EntryId == messageId` of the written blob | unit | `OutputTailFacts` (assert `failed.EntryId == messageId`); `PrePipelineFacts` seam-throw facts assert non-empty EntryId | ✅ extend |
| REQ-2 | `Guid.Empty` no longer appears as a `Step*` EntryId on any business path (Processing excepted) | unit | `OutputTailFacts` + `PrePipelineFacts` | ✅ extend |
| REQ-3 | thrown `ExecuteAsync` → `StepFailed` with error msg + `L2[out:messageId]` containing `validatedData` | unit | `PrePipelineFacts.SeamThrows_*` — extend to assert a write happened AND blob == validatedData + non-empty EntryId | ✅ extend |
| REQ-3 | input-schema fail + unexpected/deser → blob carries validatedData (WR-03: error stays sanitized constant) | unit | `PrePipelineFacts.InputInvalid_*`, `SeamThrows_Unexpected_*`, `MalformedPayload_*` | ✅ extend |
| REQ-4 | stage-1 (consumed step missing from L1) increments `orchestrator_step_unresolved` once, `workflowId` label | unit | `OrchestratorPrePipelineFacts.Total_L1_miss_*` — add metric assertion via the new seam | ✅ extend + Wave 0 seam |
| REQ-4 | stage-3 (dangling next-step id) increments once per missing successor AND still processes resolvable successors (continue, no throw) | unit | new fact in `OrchestratorPrePipelineFacts` (seed a step with [resolvable, dangling] nextIds) | ✅ extend + Wave 0 seam |
| REQ-4 | `completed-terminal`, condition-skip, normal fan-out do NOT increment | unit | `OrchestratorPrePipelineFacts.Terminal_step_*` + a normal-fanout fact assert count == 0 | ✅ extend |
| REQ-4 | label is `workflowId` only (no other labels) | unit | metric-seam assertion on tag keys | ✅ extend + Wave 0 seam |
| REQ-5 | `RunAsync` has no `if (outcome == StepOutcome.Completed)` branch; Failed/Cancelled read+fanout+delete identically to Completed | unit | `OrchestratorPrePipelineFacts` — change `Failed_continuation_writes_no_data_*` to assert a read+delete now happen; drive Cancelled through the present-blob path | ✅ extend |
| REQ-5 | clean-absent `L2[entryId]` → ack, no fan-out, no keeper | unit | new fact: `OutPresentL2(emptyDict)` + Failed result → assert no Handoffs, no SentKeeper, no delete-keeper | ✅ extend |
| REQ-5 | Processing rides clean-absent (no fan-out) | unit | new fact: Processing result with empty L2 → no fan-out | ✅ extend |
| REQ-6 | instrument name snake_case no `_total`; meter name `Orchestrator`; counter non-null from real `IMeterFactory` | unit | `OrchestratorMetricsFacts` — add `Assert.NotNull(metrics.StepUnresolved)` + (optional) a name-introspection assertion | ✅ extend |
| REQ-6 | 0-warning Debug + Release | build gate | `dotnet build -c Debug -warnaserror` + `-c Release -warnaserror` | machine-verified |

### Sampling Rate
- **Per task commit:** the quick-run filter above (the 5 affected fact classes).
- **Per wave merge:** `dotnet test tests/BaseApi.Tests` (full suite) + `dotnet build -c Release -warnaserror`.
- **Phase gate:** full suite GREEN + 0-warning Debug & Release before `/gsd-verify-work`. Live-stack proof (uniform pipeline + always-write + counter) is **deferred-automated** where the sandbox lacks Docker (SPEC acceptance line 92).

### Wave 0 Gaps
- [ ] Metric-capture seam — `MetricCollector<long>` (add `Microsoft.Extensions.Diagnostics.Testing`) OR a `MeterListener` helper in `tests/BaseApi.Tests/Orchestrator/` (covers REQ-4). **This is the only net-new test infrastructure.**
- [ ] (If reshaping `SelectNext`) — no new file; `StepAdvancementTests` migrates in-place to `.Matches`/`.UnresolvedIds`.

*All other phase requirements are covered by existing fact classes + the existing `DispatchTestKit` / `OrchestratorPrePipelineFacts` doubles — extend in-place per D-12.*

---

## Security Domain

`security_enforcement` is not set in config (default = enabled). This phase is internal pipeline plumbing with no new external input surface, auth, or crypto.

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes (existing) | `ProcessorJsonSchemaValidator.TryValidate` already gates input/output (unchanged) |
| V2/V3/V4 Auth/Session/Access | no | internal bus consumers; no user-facing surface |
| V6 Cryptography | no | none |

**Threat note (data-exposure discipline, T-70-10 / WR-03 — already enforced, must preserve):** the always-write change writes `validatedData` into `L2[out:messageId]` on failure paths. The existing never-log-payload discipline (logs reference ids only; the unexpected-catch emits the sanitized constant `"input deserialization failed"` not `ex.Message`) MUST be preserved — `dr.Data = validatedData` goes into Redis (acceptable, same as the input blob already in L2), but it must NOT leak into log lines or `ErrorMessage` wire fields.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The metric-capture test seam will use either `Microsoft.Extensions.Diagnostics.Testing` (`MetricCollector<long>`) or a BCL `MeterListener`; the test project may not currently reference the former | Validation Architecture / Standard Stack | Low — if the package is undesirable, the `MeterListener` fallback needs zero new deps. Planner should pick one; both are viable. |
| A2 | `ProcessorPipeline.BuildFailed/BuildCancelled/BuildProcessing` (`:198-205`) become dead on the business path once D-07 routes catches through `OutputTail`, and should be removed (or repurposed to build the `DataResult` fields) to keep 0-warning | State of the Art / Pitfall 5 | Medium — if left unused they trip a warning; planner must decide remove-vs-repurpose. Verified REINJECT/DELETE builders do NOT call them, so they are removable. |
| A3 | The cleanest write-gate expression is `result != StepOutcome.Processing` (write for the 3 terminals) per D-04 | Pattern 1 | Low — pure discretion (D-06 leaves the mechanic open); any equivalent expression is fine. |

---

## Open Questions (RESOLVED)

Both questions are answered by the recommendation lines below, and both answers are baked into the plans (Plan 72-01 Task 2 passes `d.EntryId` as `deleteEntryId`; Plan 72-03 Task 2 places the stage-3 increment after the clean-absent gate).

1. **Does `OutputTail` need the input-validation-fail path's `DataResult` built by the *pipeline* or the *tail*?**
   - What we know: D-07 says "each upstream path owns what `dr.Data` is" and routes through `outputTail.RunAsync`. The tail already takes a `DataResult` and a `deleteEntryId`.
   - What's unclear: whether the input-fail path (reached before `SetSeamState`, `:81-85`) should pass `d.EntryId` as `deleteEntryId` (it does NOT delete on failure per D-08, but the tail's Pre caller deletes only on success).
   - Recommendation: pass `d.EntryId` as `deleteEntryId` (it's only used for the INJECT-on-write-exhaust operand); the entry-delete stays in the pipeline's success path (`:128-130`), unchanged by D-08. The tail's `return false` (INJECT) path already skips the pipeline delete.
   - **RESOLVED:** pass `d.EntryId` as `deleteEntryId`; success-path entry-delete (`:128-130`) unchanged. Implemented in Plan 72-01 Task 2.

2. **Should the stage-3 metric increment be inside `OrchestratorPrePipeline` before or after the relocation read?**
   - What we know: D-09 orders it as resolve → SelectNext → read → fan-out. Stage-3 unresolved ids come from SelectNext.
   - Recommendation: increment per `unresolvedId` in the fan-out loop region (after the clean-absent gate, alongside the `matches` fan-out), so a clean-absent skip (which returns early) does NOT increment stage-3 — matching D-10's "skip means no advancement, no counting." Planner to confirm ordering against the clean-absent early-return.
   - **RESOLVED:** increment per `unresolvedId` after the clean-absent early-return gate (clean-absent skip never increments stage-3). Implemented in Plan 72-03 Task 2.

## Sources

### Primary (HIGH confidence — live code, this session)
- `src/BaseProcessor.Core/Processing/OutputTail.cs` (read full) — write gate `:60`, output-validate `:56-58`, `BuildStep` `:79-89`, `RunAsync` sig `:48`
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (read full) — catch blocks `:96-117`, input-fail `:81-85`, `validatedData` `:79`, builders `:198-205`, success-delete `:128-130`
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` (read full) — ctor `:46-52`, stage-1 `:58`, terminal `:69`, read branch `:84`, delete branch `:114`, ExecutionId `:105`
- `src/Orchestrator/Dispatch/StepAdvancement.cs` (read full) — `SelectNext` `:36-43`
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` (read full) — ctor `:44-50`, MeterName `:25`
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` + `StepFailedConsumer.cs` + `StepProcessingConsumer.cs` (read) — the four shells, tag convention `:53`
- `src/Orchestrator/Program.cs` (read full) — DI `:90-102`, consumer registration `:61-64`
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (read full) — `OutputData` `:62`, `OutputDataTtl` `:70-71`
- `src/Messaging.Contracts/DataResult.cs` + Step* records (read/grep) — `EntryId` init-settable; Processing/Failed/Cancelled default `Guid.Empty`, Completed no default
- `src/Messaging.Contracts/Projections/StepProjection.cs` (grep) — `int EntryCondition` record shape
- Test files (read full): `OutputTailFacts.cs`, `PrePipelineFacts.cs`, `OrchestratorPrePipelineFacts.cs`, `OrchestratorMetricsFacts.cs`, `StepAdvancementTests.cs`, `DispatchTestKit.cs`
- `.planning/config.json` — `nyquist_validation: true`
- `72-SPEC.md`, `72-CONTEXT.md` (read full) — locked requirements + decisions

### Secondary (MEDIUM confidence)
- `Microsoft.Extensions.Diagnostics.Testing` `MetricCollector<T>` as the metric-capture seam — `[ASSUMED]` per A1; `MeterListener` (BCL) is the verified zero-dep fallback.

## Metadata

**Confidence breakdown:**
- Touchpoint locations / signatures: HIGH — every cited line verified against live code; drift itemized
- Standard stack: HIGH — no new runtime deps; one possible test-only dep (A1)
- Architecture / patterns: HIGH — derived directly from verified code structure + locked decisions
- Pitfalls: HIGH — grounded in the actual NSubstitute overload traps + CS9113 history documented in the code
- Validation architecture: HIGH — all seams except metric capture already exist

**Research date:** 2026-06-17
**Valid until:** 2026-07-17 (stable internal code; re-verify line numbers if other phases touch these files first)
