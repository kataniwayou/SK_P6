# Phase 75: Per-execution recovery verdict — decouple the sweep from tunable constants - Pattern Map

**Mapped:** 2026-07-15
**Files analyzed:** 12 (2 keeper src, 1 engine, 3 config Options, 3 appsettings, 2 test facts, 1 fixture) + 2 harness scripts
**Analogs found:** 12 / 12 (all edits are surgical to existing files; the one "new" thing — keeper→ES join-field logging — has a byte-for-byte in-repo analog in `SampleProcessor`)

> This phase is almost entirely **surgical edits to files that are their own best analog**. The table below therefore lists, per target file, the *in-file* pattern to mirror (existing conventions in the same file) AND — where a genuinely new capability is added (the keeper join-field log, the new `Analyze` parameter) — the closest sibling analog to copy the shape from.

## File Classification

| Target file (modify) | Role | Data Flow | Closest analog (pattern source) | Match Quality |
|----------------------|------|-----------|--------------------------------|---------------|
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` | analyzer (pure arbiter) | transform | itself (B-criterion loop `:147-168`, existing `recoveryUtc`/hop-map param convention) | exact / in-file |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | consumer (recovery) | event-driven | `src/Processor.Sample/SampleProcessor.cs:77` (structured `{Placeholder}` log → `attributes.*`) | exact (log form) |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | test fixture (ES-read, RealStack) | request-response (ES query) | itself (`BuildStepSearchBody` `:251`, `BuildRunTraces` `:339`, `recoveryUtc` seam `:216-230`) | exact / in-file |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` | test (hermetic facts) | transform | itself (`InFlight_*` facts `:222-271`) | exact / in-file |
| `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` | test (hermetic facts) | event-driven | itself (`Reinject_present_*` / `Reinject_absent_*` `:37-156`) | exact / in-file |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs` | test (hermetic facts) | transform | itself (`ToleratedInFlightLoss_*` `:261`) | exact / in-file (audit only) |
| `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` | model (report record) | — | itself (`InFlightLoss`/`InFlightLossDetail` fields `:118-123`) | exact / in-file |
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` | config (Options) | — | itself (`:51`) + sibling `RecoveryOptions.cs:19`, `OrchestratorOutputOptions.cs:11` | exact |
| `src/Keeper/RecoveryOptions.cs` | config (Options) | — | `ProcessorLivenessOptions.cs:51` (lock-step 300 default) | exact |
| `src/Orchestrator/Configuration/OrchestratorOutputOptions.cs` | config (Options) | — | `ProcessorLivenessOptions.cs:51` (lock-step 300 default) | exact |
| `src/Processor.Sample/appsettings.json` (+ `Processor.BadConfig`) | config (JSON) | — | itself (`"ExecutionDataTtl": 300` `:33`) | exact |
| `scripts/phase-67-harness.ps1` | harness (script) | — | itself (`$windowSeconds` `:257`, env seam `:393-398`) | exact / in-file (D75-7 optional) |

**No genuinely-new file is required.** D75-3/D75-4 add a new *parameter* + *report field* + *widened log*, not a new module. A NEW hermetic facts file (`PassFailEngineRecoverabilityFacts.cs`) is optional (see Shared Pattern: hermetic facts) — it mirrors `PassFailEngineFacts.cs` verbatim.

---

## Pattern Assignments

### `src/Keeper/Recovery/ReinjectConsumer.cs` (consumer, event-driven) — D75-4 THE ONE NEW PATTERN

**Analog:** `src/Processor.Sample/SampleProcessor.cs:77` (canonical join-field structured log).

**Critical distinction (load-bearing — verified this session):** the SampleProcessor's `CorrelationId`/`ExecutionId` ride an **ambient MEL scope** (`ExecutionLogScope` + a consume-filter `CorrelationId` `BeginScope`), so its log line passes ONLY `{StepLabel} {Received} {Produced}` and still surfaces `attributes.CorrelationId`/`attributes.ExecutionId`. The keeper `ReinjectConsumer` runs under `RecoveryConsumerBase.Consume` which dispatches **straight to `HandleAsync` with NO `BeginScope`** (`RecoveryConsumerBase.cs:36-46`). Therefore the keeper log MUST pass all four ids as **explicit message-template placeholder args** — they will NOT arrive via ambient scope. This is exactly Pitfall 2 in the research.

**The placeholder-form convention to mirror** (`SampleProcessor.cs:77`):
```csharp
// {Placeholder} args → ES attributes.<Name> via ParseStateValues=true (BaseConsoleObservabilityExtensions.cs:55)
logger.LogInformation("{StepLabel} received {Received} produced {Produced}",
    label, incomingNumber, accumulated);
```

**Current drop log to widen** (`ReinjectConsumer.cs:43`):
```csharp
logger.LogWarning("REINJECT drop: L2 data gone EntryId={EntryId}", m.EntryId);   // structured hole
return;                                                                          // D-06 silent ack
```

**D75-4 change (drop path):** widen to carry all four join keys — the contract already exposes them (`KeeperReinject.cs:10-13`: `CorrelationId`, `ExecutionId`, `EntryId`, `MessageId`), and add an `outcome` discriminator so the analyzer ES-query can partition drop-vs-reinject:
```csharp
logger.LogWarning(
    "REINJECT drop: L2 data gone {CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}",
    m.CorrelationId, m.ExecutionId, m.EntryId, m.MessageId, "drop");
return;
```

**D75-4 change (success path):** add a SYMMETRIC structured success log next to `CountSent` (`ReinjectConsumer.cs:65`) so a *reinjected* (recoverable) execution is joinable too:
```csharp
CountSent(m.WorkflowId, m.ProcessorId);
logger.LogInformation(
    "REINJECT sent {CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}",
    m.CorrelationId, m.ExecutionId, m.EntryId, m.MessageId, "reinject");
```

**Constraints (from the file's own doc-comments — do NOT violate):**
- NEVER log `m.Payload` (`:43` comment "never log Payload"). The four ids + outcome only.
- The success log must sit AFTER the confirmed `CountSent` on the send path, and NEVER on the drop early-return (`:42` comment — mirror the CountSent placement rule).
- Keep `outcome`/field NAMES stable and queryable — pick names that survive the `.keyword` trap (they become `attributes.CorrelationId` etc. — the analyzer already queries these paths, see `EsIndexNames.cs:71/107`).

---

### `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` (test, event-driven) — D75-4 hermetic proof

**Analog:** itself. The two existing facts already exercise both paths with a `MeterListener` capture; extend them (or add facts) to assert the NEW structured log fields via a captured `ILogger`.

**Current facts (the shape to mirror), `:37-156`:**
- `Reinject_present_sends_EntryStepDispatch_with_envelope_messageId_override` (`:39`) — the SUCCESS path; asserts the dispatch fields + `keeper_messages_sent==1` via a reference-scoped `MeterListener`.
- `Reinject_absent_drops_no_throw_no_send_and_emits_no_legacy_drop_counter` (`:101`) — the DROP path; asserts no send, no counter.

**Pattern note:** both facts pass `NullLogger<ReinjectConsumer>.Instance` (`:77`, `:148`). To assert the new join fields, swap to a **capturing `ILogger`** (a `FakeLogger`/`TestLogger` recording `(state, args)`) so the test can assert the structured placeholders (`CorrelationId`/`ExecutionId`/`EntryId`/`MessageId`/`ReinjectOutcome`) are present with the right values — mirrors how the existing facts scope the `MeterListener` by reference identity (`:65`, `:132`) for hermetic isolation under parallel classes. The message `m` already sets all four ids (`:44-47`, `:106-109`), so no fixture change is needed there.

**Metric interplay to preserve:** the success log is added AFTER `CountSent`, so the existing `Assert.Equal(1, sent)` (`:96`) and drop-path `Assert.Equal(0, sent)` (`:153`) must stay green.

---

### `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (analyzer, transform) — D75-2 / D75-3 / D75-5 / D75-8

**Analog:** itself. The classifier slot, the parameter convention, and the tolerated-keys skip-set are all already established in this file.

**Parameter convention to mirror** (add the new keeper-outcome map exactly like the existing optional maps, `:101-111`):
```csharp
public AnalyzerReport Analyze(IReadOnlyList<RunTrace> runs, PromCounterSnapshot prom,
                              string scenarioId,
                              IReadOnlyDictionary<string, double>? tripDurationMsByExecution = null,
                              // ... existing optional maps ...
                              DateTimeOffset? recoveryUtc = null,
                              IReadOnlyDictionary<string, DateTimeOffset>? firstHopUtcByExecution = null,
                              IReadOnlyDictionary<string, DateTimeOffset>? lastHopUtcByExecution = null,
                              int maxInFlightLoss = 4)   // ← D75-2 REMOVE this trailing param
```
Add the NEW parameter as another trailing `IReadOnlyDictionary<string, ...>? keeperOutcomeByExecution = null` keyed `"corr|exec"` (same key shape as `firstHopUtcByExecution`), null-coalesced to empty exactly like `firstHop`/`lastHop` at `:131-132`. Default-null keeps all existing callers compiling (the file's own stated convention, `:93` "Default null ⇒ empty map … no behaviour change").

**D75-2 — remove the tunable bound.** Delete `int maxInFlightLoss = 4` (`:111`) and the two usages:
```csharp
// :171  DELETE
var inFlightOverBound = inFlightLoss > maxInFlightLoss;
// :172-174  DELETE the over-bound detail append
// :293  drop the `&& !inFlightOverBound` term from the verdict
var pass = missing == 0 && !inFlightOverBound && !dupFail && valueChainOk && metricGateOk;
```
Also drop `inFlightOverBound` from `BuildSummary` (`:404-421`, the `if (inFlightOverBound) reasons.Add(...)` at `:409`).

**D75-3 — the classifier slot** is the existing B-criterion loop (`:147-168`). Today it uses a **timestamp heuristic** (`stalledBeforeRecovery = lastHop < recovery`). Upgrade IN PLACE: an incomplete run is tolerated iff (a) the keeper logged a clean-absent DROP for its `(corr,exec)` — read from the new `keeperOutcomeByExecution` map — OR (b) it is the redis-wipe timestamp-heuristic path (D75-5 / Pitfall 5, kept as-is for TEST-05/07 which have no keeper drop). Everything else recoverable-but-lost → `bindingMissing++`. Mirror the existing branch shape exactly:
```csharp
if (stalledBeforeRecovery && !startedAfterRecovery)   // ← existing tolerated branch (:155)
{
    inFlightLoss++;
    inFlightLossKeys.Add(key);           // ← D75-8: MUST keep populating this set
    inFlightLossDetail.Add($"[{key}] in-flight loss: ... (tolerated).");
}
else { bindingMissing++; missingDetail.Add(...); }
```

**D75-8 — preserve commit 3028f43 (NON-NEGOTIABLE).** The tolerated-keys set `inFlightLossKeys` (`:145`) is skipped in the value-chain loop (`:216-219`):
```csharp
if (inFlightLossKeys.Contains($"{run.CorrelationId}|{run.ExecutionId}"))
{
    continue;   // partial-by-design trace → not value-chain-checked (prevents TEST-03 false FAIL)
}
```
Whatever replaces the classifier MUST keep emitting an equivalent tolerated-keys set that this loop skips. The reproducing fact (`PassFailEngineValueChainFacts.cs:261` `ToleratedInFlightLoss_PartialTerminalOnly_NotValueChainFailed_Yields_Pass`) must stay green.

---

### `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (fixture, ES-read RealStack) — D75-4 live join

**Analog:** itself. The keeper-outcome ES read mirrors the existing `Step_*` ES read → per-`(corr,exec)` map pipeline verbatim.

**ES query template to mirror** (`BuildStepSearchBody`, `:251-266`) — a static raw-string body over `EsIndexNames.*FieldPath` consts, window-bounded, `.keyword`-trap-avoiding. The NEW keeper query filters on the keeper's `attributes.ReinjectOutcome` existence (the new discriminator) + the window range, selecting `attributes.CorrelationId/ExecutionId/EntryId/ReinjectOutcome`.

**Map-building to mirror** (`BuildRunTraces`, `:339-457`) — group hits by the `(Corr, Exec)` tuple, defensively read each attribute (`attrs.TryGetProperty(... ValueKind == String)` at `:356-361`), skip odd-shaped hits (`continue`), key the output dict `"$"{Corr}|{Exec}"` (`:409`). Build a `keeperOutcomeByExecution` map the same way.

**Caller wiring to mirror** (`:216-230`): parse the seam, then pass the new map into `Analyze` alongside `recoveryUtc`/`firstHopUtc`/`lastHopUtc`:
```csharp
var report = new PassFailEngine().Analyze(
    traces, promSnapshot, scenarioId,
    // ... existing named args ...
    recoveryUtc: recoveryUtc,
    firstHopUtcByExecution: cohort.FirstHopUtcByExecution,
    lastHopUtcByExecution: cohort.LastHopUtcByExecution,
    keeperOutcomeByExecution: cohort.KeeperOutcomeByExecution);   // ← NEW
```
Add `KeeperOutcomeByExecution` to the `TraceCohort` record (`:313-326`) exactly like the existing `FirstHopUtcByExecution`/`LastHopUtcByExecution` required members. The 60s OTLP export skew is already tolerated by `DrainMs=60_000` + `PollHitsToStableAsync` (`:56`, `:282`) — the keeper drop/success docs ride the same pipeline, so no new drain is needed (research Open Question 1: Wave-0 live probe recommended to confirm keeper warnings aren't level-filtered before build).

**Read-only constraint (`:44-47`):** the fixture is ES-read-only. The keeper-outcome join MUST come from keeper *logs in ES*, NEVER a live Redis probe (research anti-pattern).

---

### `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` (hermetic facts) — D75-2 audit + D75-3/D75-5 new facts

**Analog:** itself. The three in-flight facts are the exact templates for the reworked classifier facts.

**Call-site audit for D75-2 (`.Analyze(` grep — all 27 call sites across 3 files use NAMED args or the default; NONE pass `maxInFlightLoss` positionally):**
- Most calls are `Analyze(runs, snap, "unit-test")` — unaffected by param removal.
- The 3 in-flight facts (`:222-271`) pass `recoveryUtc:`/`firstHopUtcByExecution:`/`lastHopUtcByExecution:` by NAME — unaffected by removing the trailing `maxInFlightLoss`.
- **`InFlight_LossExceedsBound_Yields_Fail` (`:256-271`) MUST BE REMOVED or REPURPOSED** — it asserts `InFlightLoss > 4 ⇒ Fail`, which is exactly the tunable bound D75-2 deletes. Replace it with a D75-2 fact: "N in-flight losses (any N) do NOT by themselves fail" (research Test Map D75-2).

**Fact templates to copy** (`:222-254`):
- `InFlight_LossBeforeRecovery_IsTolerated_Yields_Pass` (`:223`) → template for the D75-3 keeper-drop-tolerated fact (feed a `keeperOutcomeByExecution` map marking the incomplete run as a clean-drop → tolerated + Pass).
- `Incomplete_StartedAfterRecovery_Yields_Fail` (`:241`) → template for the D75-3 recoverable-but-lost fact (no keeper drop, not stalled-before-recovery → binding FAIL).
- Both build synthetic `RunTrace` via `RunTrace.FromLabels(...)` + a `ConservingSnapshot(results: 9)` — the hermetic-fact idiom (no ES/Prom/IO). New facts (D75-5 redis-vs-non-redis) follow the same shape.

---

### Config Options + appsettings (config) — D75-6 raise all four TTL knobs in lock-step

**Analog:** the four knobs cross-reference each other's `= 300` default in XML docs; they MUST move together (Pitfall 3).

| Knob | File:line | Current | Doc cross-ref |
|------|-----------|---------|---------------|
| `ProcessorLivenessOptions.ExecutionDataTtlSeconds` | `ProcessorLivenessOptions.cs:51` | `= 300` | `[ConfigurationKeyName("ExecutionDataTtl")]` binds over `appsettings` (`:50`) |
| `RecoveryOptions.ExecutionDataTtlSeconds` | `Keeper/RecoveryOptions.cs:19` | `= 300` | doc: "matches ProcessorLivenessOptions" |
| `OrchestratorOutputOptions.OutputDataTtlSeconds` | `OrchestratorOutputOptions.cs:11` | `= 300` | doc: "matching Processor/Recovery" |
| `appsettings.json "ExecutionDataTtl"` | `Processor.Sample/appsettings.json:33` | `300` | binds over the Options default at runtime |

**Jitter policy (do NOT change — just verify the floor)** `L2ProjectionKeys.OutputDataTtl` (`:70-71`): `random[ttl, 2×ttl]`. Raising the floor to e.g. `900` yields `random[900, 1800]` — comfortably outlasts the ~300s recovery for non-redis scenarios (TEST-02/03/04/06). Raise BOTH the three Options-class defaults AND every appsettings that carries `ExecutionDataTtl` (`Processor.Sample`, `Processor.BadConfig`, and any orchestrator/keeper appsettings — grep before editing). Assumption A2: confirm no distinct "5th index TTL" exists (grep `KeyExpire`/`expiry` on SADD/index keys during planning).

---

### `scripts/phase-67-harness.ps1` (harness, D75-7 OPTIONAL) — execution-based window

**Analog:** itself. `$windowSeconds = 300` (`:257-258`) is wall-clock; the env seam (`SCENARIO_ID`/`WINDOW_*_UTC`/`RECOVERY_UTC`) is set `:393-398` and cleared in `finally` `:414`. D75-7 (marked "Optionally" in ROADMAP) would add a `K_EXECUTIONS` seam symmetrically (set + clear in the same two places). Treat as a stretch requirement — the core verdict decoupling (D75-1..5,8) does not depend on it.

---

## Shared Patterns

### Structured join-field logging (MEL → `attributes.*`) — apply to every keeper log the analyzer joins
**Source:** `src/Processor.Sample/SampleProcessor.cs:77` + bridge `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:51-60`
**Apply to:** `ReinjectConsumer.cs` drop + success logs.
```csharp
// Bridge config that makes {Placeholder} args become ES attributes.<Name>:
o.IncludeFormattedMessage = true;
o.IncludeScopes           = true;    // ambient scope keys (Processor path only)
o.ParseStateValues        = true;    // {Placeholder} template args → structured state → attributes.*
```
**Rule:** use message-template `{Placeholder}` args, NEVER string interpolation (`$"...{x}"` becomes one opaque message, no `attributes.X`). Because the keeper recovery consumer has NO ambient scope (`RecoveryConsumerBase.cs:36-46` dispatches straight to `HandleAsync`, no `BeginScope`), ALL join keys must be explicit placeholder args — unlike the SampleProcessor which gets CorrelationId/ExecutionId from `ExecutionLogScope`.

### ES field-path query (avoid the `.keyword` trap) — apply to the new keeper-outcome ES read
**Source:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs:71,87,107` + `AnalyzerE2ETests.BuildStepSearchBody:251`
**Apply to:** the new keeper drop/reinject ES query in `AnalyzerE2ETests`.
- Query `attributes.CorrelationId` / `attributes.ExecutionId` / `attributes.EntryId` DIRECTLY (const `CorrelationIdFieldPath = "attributes.CorrelationId"` etc.) — the `.keyword` sub-field returns ZERO hits (documented trap, `EsIndexNames.cs:82,102`).
- Add a `ReinjectOutcomeFieldPath` const alongside the existing ones for the new discriminator field.
- Reuse the static raw-string body + `SearchAllHits` + window-range convention from `BuildStepSearchBody`.

### Per-`(corr,exec)` join keying — apply everywhere a keeper outcome maps to a run
**Source:** `PassFailEngine.cs:149` + `AnalyzerE2ETests.cs:409` — `$"{corr}|{exec}"` string key.
**Apply to:** the new `keeperOutcomeByExecution` map (engine param + `TraceCohort` member + fixture builder). Do NOT invent a new tuple/key scheme.

### Optional trailing `Analyze` parameter (default-null, no behaviour change) — apply to the new keeper-outcome param
**Source:** `PassFailEngine.cs:101-111` (every value-chain/trip/hop map added this way) + null-coalesce to empty at `:131-132`, `:252-253`.
**Apply to:** `keeperOutcomeByExecution` — add as a trailing `= null` param, `?? new Dictionary<..>(StringComparer.Ordinal)` at the top of the method, so all 27 existing `.Analyze(` call sites keep compiling unchanged.

### Hermetic fact idiom (synthetic RunTrace, no IO) — apply to all new D75-2/3/5 facts
**Source:** `PassFailEngineFacts.cs:222-254` — `RunTrace.FromLabels(corr, exec, labels)` + `ConservingSnapshot(results: 9)` + `new PassFailEngine().Analyze(...)` with named args; assert `report.InFlightLoss` / `report.Missing` / `report.Verdict`.
**Apply to:** new recoverability facts (optionally in a new `PassFailEngineRecoverabilityFacts.cs` mirroring this file's structure). Feed a synthetic `keeperOutcomeByExecution` map to drive tolerated-vs-binding classification without a live stack.

### AnalyzerReport field addition — apply to the classification-detail field (if added)
**Source:** `AnalyzerReport.cs:118-123` (`InFlightLoss` + `InFlightLossDetail` `required` members).
**Apply to:** a new report field surfacing the keeper-classification detail (e.g. `UnrecoverableLossDetail`), added as a `required` member and populated in the engine's report-build block (`:297-320`).

---

## No Analog Found

None. Every target file is either its own best analog (surgical edit within established conventions) or has a byte-for-byte sibling (the keeper join-field log copies `SampleProcessor.cs:77`; the keeper ES read copies `BuildStepSearchBody`/`BuildRunTraces`). The planner can mirror in-repo patterns for 100% of the phase — no RESEARCH.md-only fallback pattern is needed.

## Metadata

**Analog search scope:** `src/Keeper/Recovery`, `src/Processor.Sample`, `src/BaseConsole.Core/DependencyInjection`, `src/*/Configuration`, `src/Messaging.Contracts`, `tests/BaseApi.Tests/Observability` (Analysis + fixture + helpers), `tests/BaseApi.Tests/Keeper`
**Files scanned:** 14 read + 5 grep passes (`.Analyze(` call-site audit across 3 files; TTL knob cross-refs; SampleProcessor log emission; EsIndexNames field paths; ExecutionLogScope)
**Key verifications this session:**
- `RecoveryConsumerBase.Consume` has NO CorrelationId/ExecutionId `BeginScope` → keeper join keys MUST be explicit placeholder args (not ambient scope).
- All 27 `.Analyze(` call sites use named args / defaults → removing trailing `maxInFlightLoss` needs NO positional-arg fixups; only `InFlight_LossExceedsBound_Yields_Fail` (`PassFailEngineFacts.cs:256`) asserts the bound and must be repurposed.
- The `inFlightLossKeys` skip-set (commit 3028f43, D75-8) is the ONLY downstream consumer of tolerated keys — the value-chain loop at `PassFailEngine.cs:216-219`.
**Pattern extraction date:** 2026-07-15
