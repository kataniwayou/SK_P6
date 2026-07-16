# Phase 76: Framework-Emitted Per-Hop Execution Logs Keyed by stepId — Pattern Map

**Mapped:** 2026-07-16
**Files analyzed:** 15 (5 CREATE fact classes, 10 MODIFY)
**Analogs found:** 15 / 15 (all in-repo; this phase is re-key + generalize, not greenfield)

> This map extends `76-RESEARCH.md` §Verified Integration Map into a per-file analog assignment. Line numbers below were re-read live this session and match RESEARCH. The planner should drop the `read_first` / analog columns into each plan's `<action>` block. Do NOT reopen D-11 Option-C (fan-out record omits outbound MessageId) or D-18 (OutputTail returns resolved outcome).

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | production log-emission site | event-driven (per-hop) | `SampleProcessor.cs:89` (placeholder log) + own `:137` `LogInformation` | role+flow exact |
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | production return-shape change (D-18) | request-response | own `RunAsync` `:49` `Task<bool>` → `Task<(bool,StepOutcome)>` | self-analog |
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` | production log-emission site | event-driven (fan-out + terminal) | own `:75-77,:89-91` trip-end `LogInformation` | self-analog exact |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` | analyzer engine | transform (arbiter) | own `HopLabels`/`IsComplete`/verdict-gate | self-analog (re-key) |
| `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` | analyzer model (enum) | — | own `enum Verdict` `:24` (add `Inconclusive`) | self-analog |
| `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` | analyzer model | transform | own `FromLabels` `:114` (add `DistinctStepIds`) | self-analog |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | ES query helper + reader | request-response (ES) | own `BuildStepSearchBody` `:261`, `BuildRunTraces` `:438` | self-analog (re-key) |
| `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` | ES field-path const | config | own `ExecutionIdFieldPath` `:107` | self-analog exact |
| `scripts/phase-67-harness.ps1` | PS harness (exit-code) | batch | own exit-code table `:31-42` | self-analog |
| `scripts/phase-68-sweep.ps1` | PS harness (roll-up class) | batch | own class switch `:80-85` | self-analog |
| **CREATE** `tests/BaseApi.Tests/Processor/PerHopLogFacts.cs` | new hermetic fact class | event-driven | `ReinjectConsumerFacts.CapturingLogger<T>` `:24-48` | pattern exact |
| **CREATE (extend)** `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` | hermetic fact | event-driven | own `CapturingLogger<T>` `:88-101` + `Build` `:169` | self-analog exact |
| **CREATE** `tests/BaseApi.Tests/Observability/FrameworkNoPayloadFacts.cs` | new hermetic fact + grep | test | `PerHopLogFacts` (sibling) + repo Grep | pattern |
| **CREATE** `tests/BaseApi.Tests/Processor/NonBlockingLogFacts.cs` | new hermetic fact | event-driven | throwing-`ILogger` variant of `CapturingLogger<T>` | pattern |
| **CREATE (extend)** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` | hermetic engine fact | transform | own `Complete_AllStartedRuns_Yields_Pass` `:61`, `CleanSnapshot` `:48` | self-analog exact |

---

## Pattern Assignments

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (FW-01/D-08/D-09/D-10, +D-18 consumer)

**Analog:** placeholder-log discipline from `src/Processor.Sample/SampleProcessor.cs:89`; guarded-swallow is NEW (Pitfall 4).

**Method:** `RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)` at `:58`. All six FW-01 ids are in hand as `d.StepId`, `d.ExecutionId`, `d.CorrelationId`, `d.EntryId`, `messageId` (param), + resolved outcome.

**Placeholder template pattern to replicate** (`SampleProcessor.cs:89`):
```csharp
logger.LogInformation("{StepLabel} received {Received} produced {Produced}",
    label, incomingNumber, accumulated);   // NEVER $"...", args surface as attributes.*
```

**FW-04 swallow guard (NEW — required only on the new call, RESEARCH Pitfall 4):**
```csharp
try
{
    logger.LogInformation(
        "hop executed {StepId} {ExecutionId} {CorrelationId} {EntryId} {MessageId} {Outcome}",
        d.StepId, d.ExecutionId, d.CorrelationId, d.EntryId, messageId, outcome);
}
catch { /* FW-04: observability must never fail or delay the hop */ }
```

**Insertion points — the 5 "emit" branches (RESEARCH §1 branch map). Emit on these; skip clean-absent `:82`, reinject `:81`/`:92`, Processing:**
| Line | Branch | Outcome to log |
|------|--------|----------------|
| `:105-106` input-schema fail → tail | `Failed` |
| `:138-139` seam `ProcessStatusException` → tail | `Failed`/`Cancelled` (skip Processing) |
| `:153-154` unexpected exception → tail | `Failed` |
| `:157` `if (dr is null) return;` Mode-2 spawn | `Completed` (D-09 entry marker; `d.ExecutionId==Guid.Empty` passed EXPLICITLY) |
| `:162-173` normal completion via `outputTail.RunAsync` | resolved outcome from D-18 return |

**Recommended shape:** a private `LogHopExecuted(EntryStepDispatch d, Guid messageId, string outcome)` helper wrapping the guarded call, OR a `string? outcome` local + single guarded emission before each `return` (null on skip branches). Constructor already injects `ILogger<ProcessorPipeline> logger` (`:56`) — no DI change.

**D-09 marker note:** pass `d.ExecutionId` (= `Guid.Empty` for entry) as an EXPLICIT placeholder arg — the ambient `ExecutionLogScope` OMITS empty GUIDs (`InboundExecutionScopeConsumeFilter.cs:31`), and `CapturingLogger` reads only explicit args, so the all-zeros marker must be explicit.

---

### `src/BaseProcessor.Core/Processing/OutputTail.cs` (D-18 return resolved outcome)

**Analog:** self — current signature `public async Task<bool> RunAsync(DataResult dr, Guid deleteEntryId, CancellationToken ct)` at `:49`.

**Current outcome-resolution site** (`:56-59`) — the `Completed→Failed` downgrade the pipeline cannot see:
```csharp
var result = dr.Result;
if (result == StepOutcome.Completed
    && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, dr.Data, out _))
    result = StepOutcome.Failed;
```

**Change:** return the resolved `result` so `ProcessorPipeline.cs:163` logs the TRUE terminal outcome. Two shapes (planner's choice, D-18 permits either):
- `Task<(bool proceed, StepOutcome resolved)>` — both return sites (`:70` INJECT-escalation, `:75` success) yield `result`.
- keep `Task<bool>` + add `out StepOutcome resolved` (can't `out` in async — prefer the tuple).

**Both callers must update:** `ProcessorPipeline.cs:163` (`var proceed = await outputTail.RunAsync(...)`) AND `PostProcessConsumer` (research names it as the second caller — grep `outputTail.RunAsync` / `OutputTail(`  before editing). `PostProcessConsumer` does not log a per-hop record, so it can discard the new tuple field.

---

### `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` (FW-02/D-11 Option-C, D-12, D-13, D-14)

**Analog:** self — the existing trip-end `LogInformation` lines are the exact template style + field-passing convention to mirror.

**Terminal-reached record (D-12/D-13) — insertion at `:87-93` completed-terminal branch.** The branch already logs; ADD the structured record here (fires ×2 at Step_G, distinct `m.EntryId`). Existing line to sit beside:
```csharp
if (selection.Matches.Count == 0 && selection.UnresolvedIds.Count == 0)
{
    logger.LogInformation(
        "Trip ended (completed-terminal): no matching successor for ({WorkflowId}, {StepId}) outcome={Outcome} — acking (business)",
        m.WorkflowId, m.StepId, outcome);
    // ADD (D-12): guarded terminal-reached record — (CorrelationId, ExecutionId, WorkflowId, EntryId, StepId)
    return;
}
```
Do NOT emit a terminal-reached record on the `:73-80` L1-miss branch nor the `:113-119` clean-absent branch (RESEARCH §3).

**Fan-out edge record (D-11 Option-C) — insertion inside the `:126-145` next-step loop.** Identities in hand: `m.CorrelationId`, `m.ExecutionId`, `m.WorkflowId`, `m.EntryId` (inbound = M_N), loop `stepId` (next). Outbound MessageId is OMITTED (Option-C — NOT recoverable here; `post.Send` at `:135` uses NO id override, comment `:122-123`). Add the guarded record after the successful send/metric:
```csharp
foreach (var (stepId, step) in selection.Matches)
{
    // ... existing handoff build + post.Send (:128-136) + metrics.MessagesSent (:142-144) ...
    // ADD (D-11 Option-C): guarded fan-out edge record — (CorrelationId, ExecutionId, WorkflowId, EntryId, NextStepId=stepId)
}
```

**FW-04 guard + placeholder discipline:** same `try { logger.LogInformation("{...}", ...); } catch { }` pattern as the processor site. New attribute key `NextStepId` in PascalCase (Claude's-discretion naming per D-11 note / RESEARCH §5), so `attributes.NextStepId` surfaces consistently. Constructor already injects `ILogger<OrchestratorPrePipeline> logger` (`:67`) — no DI change.

---

### `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` (D-17)

**Analog:** `ExecutionIdFieldPath` at `:107` — copy the const + XML-doc shape verbatim, mind Pitfall 2 (NO `.keyword`).
```csharp
public const string ExecutionIdFieldPath = "attributes.ExecutionId";
```
**Add:** `public const string StepIdFieldPath = "attributes.StepId";` (and, if the orchestrator record needs its own query field, `NextStepIdFieldPath = "attributes.NextStepId"`). **KEEP** `StepLabelFieldPath` `:87`, `SumFieldPath` `:142`, `CorrelationIdFieldPath` `:71` — value oracle still uses them (D-16/D-17). Structural queries stop referencing `StepLabelFieldPath`.

---

### `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (ANL-01/ANL-02 ES re-key)

**Analog:** self — `BuildStepSearchBody` `:261-276` and `BuildRunTraces` `:438-505`.

**Structural query re-key** (`:261-276`) — the current `exists StepLabel` filter becomes `exists StepId`; ADD a second query body for FW-02 orchestrator records (expected set). Current template to copy the raw-string / const-interpolation shape:
```csharp
"filter": [
  { "exists": { "field": "{{EsIndexNames.StepLabelFieldPath}}" } },   // → StepIdFieldPath (structural)
  { "exists": { "field": "{{EsIndexNames.ExecutionIdFieldPath}}" } },
  { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": { "gte": "{{windowStart:o}}", "lte": "{{snapshot:o}}" } } }
]
```

**Reader re-key** (`BuildRunTraces` `:451-505`) — the loop currently keys on `attributes.StepLabel`:
```csharp
if (!attrs.TryGetProperty("StepLabel", out var labelEl) || labelEl.ValueKind != JsonValueKind.String) continue;
```
Structural completeness/expected-set/reconciliation reads `attributes.StepId` (framework records); `attributes.Produced`/`StepLabel` value collection (`:480-487`) STAYS as the layered oracle (D-16). Handle the D-09 empty-`ExecutionId` marker: a framework record with `ExecutionId == Guid.Empty` (all-zeros string) is counted as "entry ran" but excluded from any `(corr,exec)` expected set. Grep-clean: no `Step_*` literal on the structural path.

---

### `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` (ANL-04/D-01)

**Analog:** self — `enum Verdict` at `:24-39` (currently `Pass`, `Fail`). Add `Inconclusive` with an XML-doc mirroring the existing members. The report already serializes via `[JsonConverter(typeof(JsonStringEnumConverter))]` (`:90`) so the sweep reads `"Inconclusive"` from JSON for free (A4). No new report fields required beyond the enum member (existing `CorroborationDetail`/`HumanSummary` carry the blind-case reason).

---

### `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` (D-15 structural model)

**Analog:** self — `FromLabels` `:114-155` and the `DistinctLabels` `:76` / `Values` `:104` split. Under D-15 the structural dimension becomes a `StepId` set. Planner's discretion (Open-Q3): add a `DistinctStepIds` `HashSet<string>` alongside `DistinctLabels`, keep `Values` (label→int) for the oracle. `FromLabels` is the shared build path both the live fixture and hermetic facts call — mirror it for a `FromStepIds` (or an overload carrying stepIds).

---

### `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (ANL-01..05, SMP-01)

**Analog:** self — this is the largest re-key. Exact surface (all confirmed live):
| Element | Line | Action |
|---------|------|--------|
| `HopLabels` (9 `Step_*`) | `:65-66` | **DELETE** (ANL-01/D-15) |
| `IsComplete` (StepLabel set-compare) | `:74-76` | **REPLACE** with stepId-keyed completeness vs ANL-02 expected set |
| STARTED denominator `runs.Count` | `:128` | keep, but built from framework records |
| Telemetry-gap reconciliation (value-oracle path) | `:185-209` | **GENERALIZE** (ANL-03) — add framework-redundancy path: orchestrator record consuming M_N reconciles the hop with NO seed oracle |
| `valueOracleSupplied` gate | `:161` | **KEEP + generalize** (SMP-01 degrade-to-N/A) |
| `metricGateOk` | `:360` | **INVERT for blind case** (ANL-05): frozen counters + absent traces → INCONCLUSIVE not FAIL |
| verdict `pass` gate | `:368` | **RE-THREAD** to three-class (ANL-04) |
| `ExpectedHopOffset` (label→depth) | `:407-417` | **KEEP** (D-16 value oracle) |
| `ResolveSeed`/`CheckValueChain` | `:433-478` | **KEEP** (value oracle) |

**Current binding verdict gate to re-thread** (`:368-369`):
```csharp
var pass = missing == 0 && !dupFail && valueChainOk && metricGateOk;
var verdict = pass ? Verdict.Pass : Verdict.Fail;
```
**Three-class threading (ANL-04/05):** first test evidence-sufficiency — total trace darkness (`startedRuns == 0`) + self-consistent conservation (`conservationOk`, i.e. `orch_consumed == proc_sent`) → `Verdict.Inconclusive` regardless of `metricGateOk` (this is the TEST-01 cold-ES shape, RESEARCH §6). Only if evidence sufficient apply the existing Pass/Fail. The `mg1Binding` seam (`:351`) already distinguishes "conservation is a valid measure"; ANL-05's blind branch is metric-gate-fails-because-tier-absent → INCONCLUSIVE, vs metric-gate-fails-on-live-gap → FAIL. Preserve D-01: a non-zero `TelemetryGap` stays NON-binding on PASS (the `6e90216` behavior at `:199-208`).

---

## Shared Patterns

### Capturing-logger hermetic assertion (FW-01/02/03/04 fact base)
**Source:** `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs:24-48` (structured-state capture) — this is the exact pattern for the NEW `PerHopLogFacts`/`FrameworkNoPayloadFacts`/`NonBlockingLogFacts`. Copy verbatim; it records placeholder args only (not scope):
```csharp
internal sealed class CapturingLogger<T> : ILogger<T>
{
    internal sealed record Entry(LogLevel Level, IReadOnlyList<KeyValuePair<string, object?>> State);
    private readonly List<Entry> _entries = new();
    public IReadOnlyList<Entry> Entries => _entries;
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var pairs = state is IReadOnlyList<KeyValuePair<string, object?>> kvps ? kvps.ToList()
            : new List<KeyValuePair<string, object?>>();
        _entries.Add(new Entry(logLevel, pairs));
    }
    private sealed class NullScope : IDisposable { internal static readonly NullScope Instance = new(); public void Dispose() { } }
}
```
**Apply to:** all new FW fact classes. Assert `entry.State` contains each of the six/five fields as an explicit `KeyValuePair` (Pitfall 1 — scope is invisible). **FW-04 variant:** a `ThrowingLogger<T>` whose `Log` throws — assert `RunAsync` completes and the pipeline outcome is unchanged.

The `OrchestratorPrePipelineFacts` (`:88-101`) has its OWN thinner `CapturingLogger<T>` that captures only `formatter(state, exception)` STRINGS (`Messages.Add`). For FW-02's structured-field assertions, the extend-in-place facts must switch to the `ReinjectConsumerFacts` variant (State list) — a string `Contains("fan-out")` cannot assert `attributes.NextStepId` distinctness.

### Hermetic engine-fact structure (ANL-01..05, SMP-01)
**Source:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs:48` (`CleanSnapshot`) + `:61` (`Complete_..._Yields_Pass`). Every new engine fact builds synthetic `RunTrace` via `RunTrace.FromLabels(...)` + a `PromCounterSnapshot` and calls `new PassFailEngine().Analyze(...)`. Post-D-15 the facts build from stepId sets (zero `Step_*` labels present) to prove ANL-01. Per-verdict-class Nyquist sampling (RESEARCH §Validation): ≥1 PASS fact, ≥2 FAIL, ≥2 INCONCLUSIVE, ≥2 reconciliation-redundancy.

**Blind-case snapshot pattern** (invert `CleanSnapshot` `:48-59`): frozen/zero counters + `startedRuns == 0` → assert `Verdict.Inconclusive`.

### Orchestrator-pipeline hermetic harness (FW-02 facts)
**Source:** `OrchestratorPrePipelineFacts` `Build` `:169-175` (+ `CapturingSendProvider` `:41`, `OutPresentL2` `:113`, `Seed` `:150`, `Completed`/`Failed`/`Cancelled` builders `:177-187`). The 2-way fan-out fact reuses `Two_matches_produce_two_post_messages_and_one_delete` (`:229`) as the scaffold — assert 2 captured fan-out records with DISTINCT `NextStepId`. The terminal-reached ×2 fact reuses `Terminal_step_logs_completed_terminal_and_acks` (`:317`) — but with a Step_G-shaped double fan-in, assert 2 records with distinct inbound `EntryId`.

### Placeholder-only / no-payload discipline (FW-03)
**Source:** `SampleProcessor.cs:89` + the existing never-log-payload guards (`ProcessorPipeline.cs:147,196` "never log Payload (T-70-10)"). New framework templates carry ids + outcome ONLY. FW-03 acceptance = repo Grep for `validatedData` / `dr.Data` / `d.Payload` in the new log templates (must be clean) + a sentinel-through-blob fact asserting the sentinel appears in no captured record.

### Exit-code table discipline (D-02/D-03/D-04)
**Source:** `scripts/phase-67-harness.ps1:31-42` exit-code table (`0` PASS, `1` FAIL, `10..70` infra, `64` bad-arg; `2` UNUSED). Add `2 = INCONCLUSIVE` to the table. STEP H must map the analyzer's `Verdict == Inconclusive` (read from the JSON artifact, written before the assert at `AnalyzerE2ETests.cs:245-246`) → `exit 2`.
**Source:** `scripts/phase-68-sweep.ps1:80-85` class switch — add `2 { 'INCONCLUSIVE' }`:
```powershell
$class = switch ($code) {
    0       { 'PASS' }
    1       { 'VERDICT_FAIL' }
    64      { 'BAD_ARG' }
    default { 'INFRA_ABORT' }
}
```
Final exit stays "0 IFF all PASS" → INCONCLUSIVE is sweep-fatal (D-03); the roll-up emits a DISTINCT instrument-failure message advising a deliberate warm-ES re-run, NO auto-retry (D-04).

---

## No Analog Found

None. Every file is a re-key/extension of tested in-repo machinery. The only genuinely new production code is three guarded log-emission sites (one processor, two orchestrator) + the FW-04 swallow; all follow the `SampleProcessor.cs:89` placeholder precedent and the `ReinjectConsumerFacts` capturing-logger precedent.

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/Processing`, `src/Orchestrator/Dispatch`, `src/Processor.Sample`, `src/Messaging.Contracts`, `tests/BaseApi.Tests/{Observability,Orchestrator,Keeper,Processor}`, `scripts/`
**Files read live:** ProcessorPipeline.cs, OutputTail.cs, OrchestratorPrePipeline.cs, SampleProcessor.cs, PassFailEngine.cs, AnalyzerReport.cs, RunTrace.cs, EsIndexNames.cs, AnalyzerE2ETests.cs (BuildStepSearchBody/BuildRunTraces), PassFailEngineFacts.cs, ReinjectConsumerFacts.cs, OrchestratorPrePipelineFacts.cs, phase-67-harness.ps1, phase-68-sweep.ps1
**Pattern extraction date:** 2026-07-16
