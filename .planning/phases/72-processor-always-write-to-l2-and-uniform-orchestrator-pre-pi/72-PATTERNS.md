# Phase 72: Processor Always-Write to L2 + Uniform Orchestrator Pre-Pipeline + Unresolved-Step Metric - Pattern Map

**Mapped:** 2026-06-17
**Files analyzed:** 11 (6 production modify-in-place, 5 test extend-in-place) + 1 possible net-new (`SelectNextResult` type, co-located in `StepAdvancement.cs`)
**Analogs found:** 11 / 11 (every new behavior has an in-repo analog — this is a gate-removal + 1-counter + 1-DI-edge refactor; nothing is greenfield)

> **MODIFY-IN-PLACE phase.** No net-new production *files*. The only candidate new *type* is the `SelectNext` structured result (`readonly record struct`), which lands inside the existing `StepAdvancement.cs`. Its closest analog is the existing in-file `(Guid stepId, StepProjection step)` tuple shape it replaces.
>
> **Use the REAL line numbers below** — RESEARCH.md's Drift Report flagged two: `OrchestratorPrePipeline` ctor is at `:46-52` (not `:37-43`), and there is a SECOND `Guid.Empty` EntryId source at `OutputTail.BuildStep :83-86` (the SPEC narrative only named `ProcessorPipeline :198-202`). Both verified against live code in this pass.

---

## File Classification

| Modified File | Role | Data Flow | Closest Analog (in-repo) | Match Quality |
|---------------|------|-----------|--------------------------|---------------|
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | write-tail + result builder | file-I/O (L2 write) + transform (build Step*) | **self** — the existing Completed-only write arm + the Completed `EntryId = dr.MessageId` stamp at `:82` are the pattern the Failed/Cancelled arms copy | exact (extend own arm) |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | orchestrator-pipeline (processor side) | request-response + transform | **self** — the success-path `outputTail.RunAsync(carried, d.EntryId, ct)` call at `:124-126` is the exact pattern the 3 catch/input-fail paths copy | exact (relocate own pattern) |
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` | orchestrator-pipeline (orch side) | request-response + file-I/O (L2 read/delete) | **self** — the processor `ProcessorPipeline :67-68/72-78` clean-absent-vs-fault read gate is the cross-tier analog for the new D-10 absent-skip; the existing `:84/:114` Completed branches are removed | exact (cross-tier analog exists) |
| `src/Orchestrator/Dispatch/StepAdvancement.cs` | pure step-advancement function | transform | **self** — the existing `yield return (nextId, next)` tuple shape at `:39-42` is the analog the `{matches, unresolvedIds}` struct generalizes | exact (reshape own return) |
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | metrics holder | event-driven (counter) | **self** — the 3 existing `meter.CreateCounter<long>(...)` lines at `:47-49` are the exact IMeterFactory pattern the new counter copies | exact |
| `src/Orchestrator/Program.cs` | config (DI) | — | **self** — no edit likely needed; both `OrchestratorMetrics` (singleton `:101`) and `OrchestratorPrePipeline` (scoped `:90`) are already registered; ctor resolution is automatic | exact (no-op) |

| Extended Test File (D-12) | Role | Closest Analog (in-repo) | Match Quality |
|---------------------------|------|--------------------------|---------------|
| `tests/BaseApi.Tests/Processor/OutputTailFacts.cs` | test | **self** — `Completed_WritesOutputDataOnce_…` (`:28-41`) is the assert-write+EntryId pattern; rewrite `NonCompleted_Failed_SkipsWrite_…` (`:53-60`) to its inverse | exact |
| `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs` | test | **self** — the 4 `SeamThrows_*` facts (`:204,223,241,258`) + `InputInvalid_…` (`:95`) + `MalformedPayload_…` (`:291`) extend to assert write + real EntryId | exact |
| `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` | test | **self** — `Total_L1_miss_…`(`:289`), `Terminal_step_…`(`:262`), `Failed_continuation_writes_no_data_…`(`:306`); `Build` helper (`:157-162`) gains the metrics arg | exact |
| `tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs` | test | **self** — `Constructs_With_NonNull_Counters_…` (`:24-35`) is the non-null assert pattern; add `metrics.StepUnresolved` | exact |
| `tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs` | test | **self** — all 6 facts chain `.Select(s => s.stepId)` (`:62,78,93,108,126,139`) → migrate to `.Matches.Select(...)`; `DanglingNextStepId_…`(`:104`) also asserts `.UnresolvedIds` | exact |

---

## Pattern Assignments

### `src/BaseProcessor.Core/Processing/OutputTail.cs` (write-tail + result builder; file-I/O + transform)

**Analog:** itself — the Completed arm is the template the Failed/Cancelled arms copy.

**REQ-1 — drop the write gate.** Current write gate (`OutputTail.cs:56-72`, VERIFIED):
```csharp
var result = dr.Result;
if (result == StepOutcome.Completed
    && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, dr.Data, out _))
    result = StepOutcome.Failed;                    // KEEP — output-validate→Failed stays (:56-58)

if (result == StepOutcome.Completed)               // ← REQ-1/D-06: remove this write gate
{
    var write = await RetryLoop.ExecuteAsync(
        () => db.StringSetAsync(L2ProjectionKeys.OutputData(dr.MessageId), dr.Data, JitteredTtl()), limit, ct);
    if (!write.Succeeded)
    {
        await SendKeeper(BuildInject(dr, deleteEntryId), limit, ct);   // INJECT + return false
        return false;
    }
}
await SendResult(BuildStep(dr, result), limit, ct);
return true;
```
**Target (Pattern 1 / D-04):** change the gate to `result != StepOutcome.Processing` (write for the 3 terminals, NOT Processing). The INJECT-on-write-exhaust + `return false` block is preserved verbatim inside the new gate. **Pitfall 1:** a naive "delete the gate entirely" writes a blob for `Processing` too and breaks the orchestrator clean-absent skip — gate MUST exclude Processing.

**REQ-2 — real EntryId on terminal arms.** `BuildStep` (`:79-89`, VERIFIED) — the Completed arm at `:82` is the analog:
```csharp
StepOutcome.Completed => new StepCompleted(...) { ..., EntryId = dr.MessageId },          // :82 — already correct (the template)
StepOutcome.Failed    => new StepFailed(...)    { ..., EntryId = Guid.Empty, ErrorMessage = "..." },   // :84 → EntryId = dr.MessageId
StepOutcome.Cancelled => new StepCancelled(...) { ..., EntryId = Guid.Empty, CancellationMessage = "" },// :86 → EntryId = dr.MessageId
_                     => new StepProcessing(...) { ... },                                  // :87-88 — D-04: keep Guid.Empty (no blob)
```
Stamp `EntryId = dr.MessageId` on the Failed + Cancelled arms (copy the Completed arm's `:82` value); leave the Processing arm untouched. This is the cleanest seam (RESEARCH Drift note): the tail both writes the blob under `dr.MessageId` AND builds the `Step*`, so the EntryId stays co-located with the write.

**Unchanged:** `RunAsync` signature `public async Task<bool> RunAsync(DataResult dr, Guid deleteEntryId, CancellationToken ct)` (`:48`); `JitteredTtl()` → `L2ProjectionKeys.OutputDataTtl(...)` (`:39-40`); `SendResult`/`SendKeeper` (`:107-144`).

---

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (processor-side pipeline; request-response + transform)

**Analog:** itself — the success-path tail call at `:124-126` is the exact pattern the catch/input-fail paths copy.

**The success-path template (VERIFIED `:124-130`):**
```csharp
var carried = dr with { MessageId = messageId };
var proceed = await outputTail.RunAsync(carried, d.EntryId, ct);   // ← THE PATTERN to copy into catch blocks
if (!proceed) return;                                              //   (INJECT escalation already ended the trip)
var del = await RetryLoop.ExecuteAsync(
    () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);   // success-only delete — UNCHANGED (D-08)
if (!del.Succeeded) await SendKeeper(BuildDelete(d), limit, ct);
```

**REQ-3/D-07 — route 3 bypass paths through the tail.** The current bypasses each call `SendResult(BuildXxx(...))` directly and skip the write:

| Path | Current (VERIFIED) | Target (D-07) |
|------|--------------------|---------------|
| input-schema fail | `:83` `SendResult(BuildFailed(d, string.Join("; ", inErrs)))` | build `DataResult{ Result=Failed, Data=validatedData, MessageId=messageId, ErrorMessage=string.Join("; ", inErrs) }` → `outputTail.RunAsync(dr, d.EntryId, ct)` |
| `ProcessStatusException` | `:96-108` `SendResult` of Failed/Cancelled/Processing | build `DataResult{ Result=mapped, Data=validatedData, MessageId, ErrorMessage=e.Message }` → tail. **D-04:** the Processing case → tail with `Result=Processing` (tail writes NO blob, keeps `Guid.Empty`). |
| unexpected/deser | `:109-117` `SendResult(BuildFailed(d, "input deserialization failed"))` | build `DataResult{ Result=Failed, Data=validatedData, MessageId, ErrorMessage="input deserialization failed" }` → tail. **WR-03 (Pitfall, Security):** keep the sanitized constant — NEVER `ex.Message` on the wire. |

**Verified in-scope local:** `var validatedData = read.Value!;` at `:79` — live through all three paths (`:81-85`, `:96-108`, `:109-117`).

**D-08 — NO input-delete on failure.** Each rerouted path keeps `return;` after the tail (no `KeyDeleteAsync`); the input `data:` blob is left to TTL (the `:124-130` success-delete is the ONLY delete and is unchanged).

**A2 (dead-code cleanup for 0-warning):** `BuildFailed`/`BuildCancelled`/`BuildProcessing` (`:198-205`) become unused once their callers build `DataResult`s inline. Verified `BuildReinject`/`BuildDelete` (`:209+`) do NOT call them. Planner: remove them (or repurpose to build the `DataResult` fields) to avoid an unused-member warning. Note `:199,202` use `ExecutionId = NewId.NextGuid()` (not `d.ExecutionId`) — preserve whichever ExecutionId source the rerouted `DataResult` should carry.

---

### `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` (orchestrator-side pipeline; request-response + file-I/O)

**Cross-tier analog:** `ProcessorPipeline.cs:72-79` — its clean-absent-vs-fault read gate is the exact pattern D-10 needs:
```csharp
// ProcessorPipeline.cs:72-78 (VERIFIED) — the clean-absent/fault discrimination to mirror
var read = await RetryLoop.ExecuteAsync(async () =>
{
    var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(d.EntryId));
    if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // A2: clean-absent unified WITH fault here (processor)
    return raw.ToString();
}, limit, ct);
if (!read.Succeeded) { await SendKeeper(BuildReinject(d, messageId), limit, ct); return; }
```
**Note the asymmetry (Pitfall 2):** the processor *unifies* absent+fault → REINJECT. The orchestrator D-10 must do the OPPOSITE — split them three ways: present (fan out + delete), clean-absent (ack, skip, NO keeper), fault (REINJECT). Branch on `raw.IsNullOrEmpty` INSIDE the success path → idempotent-skip return; only a thrown `RedisException` (loop exhaust) → REINJECT.

**REQ-5/D-09 — drop both outcome branches.** Current (VERIFIED):
```csharp
string relocated = "";
if (outcome == StepOutcome.Completed)              // :84 ← REMOVE
{
    var read = await RetryLoop.ExecuteAsync(async () =>
    {
        var raw = await db.StringGetAsync(L2ProjectionKeys.OutputData(m.EntryId));
        return raw.IsNullOrEmpty ? "" : raw.ToString();   // :88-89 ← clean-absent currently = "proceed empty"; D-10 changes to "skip"
    }, limit, ct);
    if (!read.Succeeded) { await SendKeeper(BuildReinject(m, outcome, messageId), limit, ct); return; }
    relocated = read.Value!;
}
// ... fan-out loop :98-110 (ExecutionId threaded at :105 — KEEP, D-13) ...
if (outcome == StepOutcome.Completed)              // :114 ← REMOVE
{
    var del = await RetryLoop.ExecuteAsync(
        () => db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), limit, ct);
    if (!del.Succeeded) { await SendKeeper(BuildDelete(m), limit, ct); return; }
}
```
**Target uniform flow (D-09):** resolve `L1[stepId]` (stage-1, `:58`) → `SelectNext` (stage-2/3) → exists/read `L2[out:EntryId]` for EVERY outcome → clean-absent ⇒ idempotent-skip return (ack, no fan-out, no keeper, no delete) → fan out `matches` (`:98-110`, ExecutionId thread at `:105` UNCHANGED) → delete `L2[out:EntryId]`. `outcome` is used ONLY for `SelectNext` entry-condition matching, NEVER for L2 behavior.

**REQ-4/D-11 — inject metrics + 2 increment sites.** Add `OrchestratorMetrics metrics` to the primary ctor (`:46-52`, currently `(store, advancement, redis, sendProvider, retryOptions, logger)` — NO metrics holder, the XML doc `:38-44` documents WHY Phase 71 omitted it: CS9113). **Pitfall 5:** add the param AND both increments in the SAME change (an unused param trips CS9113 / breaks 0-warning).
- **Stage-1** (`:58` L1-miss branch, before `return`): increment once.
- **Stage-3** (per `unresolvedId` from the reshaped `SelectNext`): increment + `continue` (graceful, never throw). Per Open-Question-2, place this AFTER the clean-absent gate so a skipped/absent message does not count stage-3.

Increment shape (Code Example, mirror `TypedResultConsumer.cs:53` tag convention but camelCase key per REQ-6):
```csharp
metrics.StepUnresolved.Add(1, new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")));
```

**D-03 — what does NOT increment:** `completed-terminal` (matches==0 && no unresolvedIds, `:69-74`), entry-condition skip (SelectNext case b), normal fan-out. Only L1-misses (stage-1 + stage-3).

**Consumer of `SelectNext` reshape (Pitfall 3):** the `:68` call `advancement.SelectNext(...).ToList()` + the `:99` `foreach (var (stepId, step) in matches)` break on the struct return — change to `.Matches` and add a `.UnresolvedIds` increment loop.

---

### `src/Orchestrator/Dispatch/StepAdvancement.cs` (pure step-advancement function; transform)

**Analog:** itself — the existing single-`if` yield loop is the template the 3-way classification generalizes. **D-02: stays pure — NO metrics, NO Redis.**

Current (VERIFIED `:36-43`):
```csharp
public IEnumerable<(Guid stepId, StepProjection step)> SelectNext(
    StepOutcome outcome, StepProjection completed, IReadOnlyDictionary<Guid, StepProjection> steps)
{
    foreach (var nextId in completed.NextStepIds ?? Enumerable.Empty<Guid>())
        if (steps.TryGetValue(nextId, out var next) &&
            (next.EntryCondition == (int)outcome || next.EntryCondition == Always))   // Always = 4 const :23
            yield return (nextId, next);
}
```
**Target (Pattern 4 / D-01):** return a `readonly record struct` (suggested `SelectNextResult(IReadOnlyList<(Guid stepId, StepProjection step)> Matches, IReadOnlyList<Guid> UnresolvedIds)` — co-located in this file, allocation-light, harness-free). Iterate `NextStepIds ?? Enumerable.Empty<Guid>()` ONCE, classify each id:
- **(a)** not in `steps` → `UnresolvedIds` (the new stage-3 signal — currently silently dropped by the `TryGetValue` guard).
- **(b)** in `steps` but `EntryCondition != (int)outcome && != Always` → filtered, collected NOWHERE (legit conditional branch / `Never`=5; D-03 — NO metric).
- **(c)** in `steps` and condition matches → `Matches`.

Preserve: the `?? Enumerable.Empty<Guid>()` terminal guard (`:39`, the `TerminalStep_NullNextStepIds_…` fact depends on it); `Always = 4` / `Never = 5` semantics (`:23`).

---

### `src/Orchestrator/Observability/OrchestratorMetrics.cs` (metrics holder; event-driven)

**Analog:** itself — the 3 existing counters are the exact IMeterFactory pattern. **REQ-6/D-11: snake_case, no `_total`, never a static `Meter`.**

VERIFIED ctor (`:44-50`):
```csharp
public OrchestratorMetrics(IMeterFactory meterFactory)
{
    var meter = meterFactory.Create(MeterName);                                  // MeterName = "Orchestrator" const :25
    DispatchSent   = meter.CreateCounter<long>("orchestrator_dispatch_sent");    // :47 — the template
    ResultConsumed = meter.CreateCounter<long>("orchestrator_result_consumed");
    ResultDeduped  = meter.CreateCounter<long>("orchestrator_result_deduped");
}
```
**Add (copy the `:47` line + a property, mirror `:28`/`:31` doc style):**
```csharp
public Counter<long> StepUnresolved { get; }              // alongside DispatchSent/ResultConsumed/ResultDeduped
...
StepUnresolved = meter.CreateCounter<long>("orchestrator_step_unresolved");   // D-03 — collector appends _total
```
Anti-pattern (verified avoided in the holder already): no `_total` in the name; no static `Meter`.

---

### `src/Orchestrator/Program.cs` (config / DI) — likely NO edit

VERIFIED: `OrchestratorMetrics` is `AddSingleton` (`:101`); `OrchestratorPrePipeline` is `AddScoped` (`:90`). A singleton resolves into a scoped ctor automatically — no registration change is needed beyond the (automatic) ctor param. `AddMeter(OrchestratorMetrics.MeterName)` (`:102`) is unchanged (REQ-6). **Do not** add an `AddMeter`/registration line.

---

## Shared Patterns

### Counter-increment tag convention (REQ-4 / D-11)
**Source:** `src/Orchestrator/Consumers/TypedResultConsumer.cs:53` (VERIFIED)
**Apply to:** the 2 new increment sites in `OrchestratorPrePipeline`.
```csharp
// existing (PascalCase key by convention):
metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));
// new — REQ-6 DEVIATES intentionally to camelCase for THIS counter's label:
metrics.StepUnresolved.Add(1, new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")));
```
Assert the `workflowId` key exactly — it is an intentional, spec-locked deviation from the existing PascalCase tags.

### Jittered TTL on every L2 output write (REQ-1)
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:70-71` (`OutputDataTtl`, the single SoT) + key `OutputData(messageId)` at `:62`.
**Apply to:** the now-unconditional `StringSetAsync` write in `OutputTail` (Failed/Cancelled writes inherit the existing `JitteredTtl()` → `OutputDataTtl(...)` call; no TTL policy change).

### RetryLoop → keeper-on-exhaust, clean-absent ≠ escalation (REQ-5)
**Source:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:72-78` (read) + `:128-130` (delete).
**Apply to:** the `OrchestratorPrePipeline` uniform read/delete — but INVERT the absent handling (processor unifies absent→fault; orchestrator D-10 splits absent→ack-skip).

---

## Test Pattern Assignments (extend-in-place, D-12)

### `tests/BaseApi.Tests/Processor/OutputTailFacts.cs`
**Analog:** `Completed_WritesOutputDataOnce_WithTtl_AndSendsStepCompleted_ReturnsTrue` (`:28-41`) — uses `DispatchTestKit.ReceivedStringSets` + `HasNonNullTtl` + asserts `completed.EntryId == messageId` (`:41`).
- **Rewrite** `NonCompleted_Failed_SkipsWrite_StillSendsStepFailed_ReturnsTrue` (`:53-60`) → assert a write NOW happens + `failed.EntryId == messageId` (its inverse).
- **Add** a Cancelled write+EntryId fact, and a Processing **no-write** + `EntryId == Guid.Empty` fact (D-04). Parameterize over the 3 terminals if convenient.
- **Pitfall 4:** use `DispatchTestKit.ReceivedStringSets(db)` (overload-agnostic) — never stub a single `StringSetAsync` overload.

### `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs`
**Analog:** the 4 `SeamThrows_*` facts (`:204,223,241,258`) + `InputInvalid_OneStepFailed_AndNoEntryDelete` (`:95`) + `MalformedPayload_DeserFailure_…` (`:291`).
- **Extend** each to assert a write happened, the blob == `validatedData`, and the `Step*` carries a non-empty `EntryId` (REQ-3). `SeamThrows_Processing` (`:241`) keeps **no-write + `Guid.Empty`** (D-04).
- WR-03: the deser/unexpected fact still asserts `ErrorMessage == "input deserialization failed"` (sanitized).

### `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs`
**Analogs:** mux helpers `OutPresentL2` (`:110`), `ReadFaultL2` (`:121`), `DeleteFaultL2` (`:131`); facts `Total_L1_miss_…` (`:289`), `Terminal_step_…` (`:262`), `Failed_continuation_writes_no_data_and_dispatches_empty` (`:306`).
- **`Build` helper (`:157-162`) gains a metrics arg** — construct `OrchestratorMetrics` from a real `IMeterFactory` (mirror `OrchestratorMetricsFacts:26-31`). This is the load-bearing wiring change for every fact.
- **Rewrite** `Failed_continuation_writes_no_data_…` (`:306`) → a Failed result with a present blob now reads+fans-out+deletes identically to Completed (REQ-5).
- **Add** clean-absent fact (`OutPresentL2(emptyDict)` + Failed → no Handoffs, no SentKeeper, no delete) and a Processing-rides-clean-absent fact.
- **Add metric assertions** (Wave-0 seam): stage-1 increment on `Total_L1_miss_…`; a new dangling-next-step fact (seed `[resolvable, dangling]` nextIds → 1 increment + still fans out the resolvable); assert NO increment on `Terminal_step_…` + a normal fan-out; assert the `workflowId`-only label.

### `tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs`
**Analog:** `Constructs_With_NonNull_Counters_From_Real_MeterFactory` (`:24-35`).
- **Add** `Assert.NotNull(metrics.StepUnresolved);` to that fact (copy the `:33-34` lines).

### `tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs`
**Analog:** all 6 facts chain `.Select(s => s.stepId)` / `.ToList()` directly off `SelectNext(...)` (`:62, 78, 93, 108, 126, 139`).
- **Migrate** every chain to `.Matches.Select(...)` / `.Matches.ToList()` (Pitfall 3).
- **`DanglingNextStepId_IsSkipped_NoThrow` (`:104-115`)** additionally asserts `DanglingId` now appears in `.UnresolvedIds` (the new observability signal).
- `HelperPerformsNoIo_…` (`:131`) and `TerminalStep_NullNextStepIds_…` (`:117`) assert against `.Matches` (purity + terminal guard preserved).

---

## Wave-0 Test Seam (the ONLY net-new test infrastructure)

A counter-increment capture seam for `orchestrator_step_unresolved` (REQ-4). No in-repo analog exists (the other metrics facts only assert non-null). Two options (A1):
- **Option A:** `MetricCollector<long>(meterFactory, "Orchestrator", "orchestrator_step_unresolved")` — needs `Microsoft.Extensions.Diagnostics.Testing` on `tests/BaseApi.Tests`.
- **Option B (zero new deps):** a ~15-line `MeterListener` helper in `tests/BaseApi.Tests/Orchestrator/` filtering `instrument.Meter.Name == "Orchestrator" && instrument.Name == "orchestrator_step_unresolved"`, accumulating `(value, tags)`.

Planner picks one; both are viable. Closest in-repo "metrics test" analog for the surrounding boilerplate: `OrchestratorMetricsFacts` (real `IMeterFactory` via `ServiceCollection().AddMetrics()`).

---

## No Analog Found

| File / Type | Role | Data Flow | Reason | Mitigation |
|-------------|------|-----------|--------|------------|
| metric-capture test seam (`MetricCollector<long>` / `MeterListener`) | test-infra | event-driven | No counter-increment-assertion seam exists today (existing facts assert non-null only) | RESEARCH A1: BCL `MeterListener` (zero-dep) or `Microsoft.Extensions.Diagnostics.Testing` |

Every production change has an exact in-repo analog (mostly the file's own existing pattern). The `SelectNextResult` struct is co-located in `StepAdvancement.cs` and modeled on the existing tuple — not a "no analog" case.

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/Processing/`, `src/Orchestrator/Dispatch/`, `src/Orchestrator/Observability/`, `src/Orchestrator/Consumers/`, `src/Messaging.Contracts/Projections/`, `tests/BaseApi.Tests/{Processor,Orchestrator}/`
**Files scanned (read, this pass):** `OutputTail.cs`, `ProcessorPipeline.cs` (`:70-209`), `OrchestratorPrePipeline.cs`, `StepAdvancement.cs`, `OrchestratorMetrics.cs`, `Program.cs` (`:85-108`), `L2ProjectionKeys.cs` (`:55-75`), `OutputTailFacts.cs`, `PrePipelineFacts.cs`, `OrchestratorPrePipelineFacts.cs`, `OrchestratorMetricsFacts.cs`, `StepAdvancementTests.cs`
**Pattern extraction date:** 2026-06-17
**Line numbers:** verified live this pass; consistent with RESEARCH.md Drift Report (ctor `:46-52`; second `Guid.Empty` source `OutputTail.BuildStep :83-86`).
