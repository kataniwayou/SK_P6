# Phase 78: Rework the resilience sweep to verify purely from the new framework ES logs — Research

**Researched:** 2026-07-16
**Domain:** Test-side + scripts-side rework of the LIVE resilience analyzer (xUnit / net8.0, ES-log-driven verdict engine). No framework source change.
**Confidence:** HIGH (all claims verified against the actual code under rework, this session)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01 — Entry-marker: DELETE the detector entirely.** Delete `PassFailEngine.IsEntryMarker` (:62-63) + its `scored` guard (:134); delete `AnalyzerE2ETests.IsEntryMarkerExecution` (:714-715), the `entryMarkerCorrelations` set (:543,:587), and every `IsEntryMarkerExecution(...)` guard in `BuildRunTraces`/dispatch/value loops (:575,:585,:617,:648). The `exists attributes.ExecutionId` filter stays as the SOLE entry-marker exclusion. **HARD GATE:** confirm `Step_A`/`M_1` evidence is not load-bearing before delete; else FALL BACK to adapt-and-keep. Default is delete.
- **D-02 — Keeper discrimination: exclude at the QUERY level.** Add `must_not: [ { "exists": { "field": "attributes.ReinjectOutcome" } } ]` to `BuildStepSearchBody` (:281). Keeper records purely excluded from structural completeness; recovery proven separately via `BuildKeeperOutcomeMap` (its own `exists ReinjectOutcome`, :408). Parse-level guard MAY stay (discretion).
- **D-03 — Value oracle: FULL DELETION.** From `AnalyzerE2ETests.cs`: `BuildValueOracleSearchBody` (:334), `valueHits` fetch + `valuesByInstance`/`seedsByExec` construction (:639-694), `TryReadProduced`/`TryReadSum` if unused, value axis of `BuildRunTraces`. From `PassFailEngine.cs`: `CheckValueChain` (:560), `ExpectedHopOffset` (:510), `ResolveSeed` (:536), `valueOracleSupplied`/`seedsByExecution` param + the value-chain loop (:337-394), WR-02 guard (:388), telemetry-gap **path #1** (:243-255). KEEP path #2 (ANL-03, :264-274). DELETE `PassFailEngineValueChainFacts.cs` (375 lines) entirely; `PassFailEngineFacts.cs` stays. Absolute-value proof stays owned by `FanInHermeticHarnessFacts`.
- **D-04 — Live-gate acceptance: BASELINE VERDICT REPRODUCTION.** Reseed FIRST (rebuild both host configs + `docker compose build` → graph-DELETE → seed → 204), run `phase-68-sweep.ps1` over TEST-01..07 (run-all, no fail-fast). PASS GATE: every scenario reproduces its prior `phase-68-summary.json` verdict with the value-oracle axis dark. A legitimate verdict shift → STOP, treat as a finding (do not silently re-baseline). Hermetic GREEN first, THEN the live reseed+sweep.

### Claude's Discretion
- Exact ES query JSON shape for the `must_not` clause (D-02) and whether to keep the parse-level keeper guard.
- Whether `TryReadSum`/`TryReadProduced`/`TryReadTimestamp` are deleted or retained (retain any still used by the surviving structural/trip-duration path).
- Task decomposition and commit granularity.

### Deferred Ideas (OUT OF SCOPE)
- **MessageId-graph reconstruction** (nodes=MessageId, edges=consumed-EntryId→produced-MessageId) — only if D-04 baseline reproduction fails in a way ANL-03 cannot cover.
- Any framework *source* change (`BaseProcessor.Core`, `Orchestrator`, `Keeper`, `Processor.Sample`) — that was Phase 77.
</user_constraints>

## Summary

The entire phase is a **reader-side simplification**: delete the entry-marker detector (D-01), exclude keeper reinject records from the structural cohort at the ES-query level (D-02), and delete the concrete-value oracle end to end (D-03), then re-prove the 7-scenario live gate reproduces the committed baseline (D-04). No framework source changes. `[VERIFIED: read all primary files this session]`

**The two de-risk questions both resolve favorably, with strong code evidence:**

1. **D-01 delete is SAFE (GO, not fall-back).** `Step_A` is the Mode-2 entry marker (`ExecutionId == Guid.Empty`). Its `stepIdByMessageId[M_1]` edge is *already never populated today* — the current `IsEntryMarkerExecution` guard (`AnalyzerE2ETests.cs:585-589`) `continue`s the entry marker *before* the `stepIdByMessageId[...] = stepId` write at `:593`. And `M_1` resolution only ever proves `Step_A`, which is never a member of any expected set (offset-0; the ANL-02 expected set is FW-02 `NextStepId`s = `Step_B..Step_G`). So nothing scored depends on it. Post-77 the `exists attributes.ExecutionId` query filter (`:288`) drops `Step_A` even earlier — behaviourally identical to today's `continue`. `[VERIFIED: AnalyzerE2ETests.cs:562-601, PassFailEngine.cs:158-167]`

2. **D-03/D-04 coverage is NOT at risk — the baseline exercised ZERO tolerance paths.** The committed baseline (`git show HEAD:analyzer-reports/phase-68-summary.json`) shows all 7 scenarios PASS with **`startedRuns == completeRuns`** and `zeroMissing == true`. Because the engine counts a telemetry-gap or in-flight-loss run as *incomplete* (`complete = scored.Where(RunComplete)`, `PassFailEngine.cs:180`), `completeRuns == startedRuns` proves **no run in the baseline was tolerated by any path** — not value-chain path #1, not ANL-03 path #2, not keeper-drop, not redis-wipe. Deleting path #1 therefore cannot flip any baseline verdict; there was nothing for it to tolerate. `[VERIFIED: HEAD:analyzer-reports/phase-68-summary.json — all 7 completeRuns==startedRuns]`

**Primary recommendation:** Proceed with all four deletes. The single non-obvious hazard is NOT in the live path — it is that **deleting `ExpectedHopOffset` breaks ~5 surviving hermetic facts** in `PassFailEngineFacts.cs` that rely on it (via `valueOracleHopSet`) to know a single stalled run is incomplete. Resolve that coupling in the same task (migrate those facts to the stepId path, or retain `ExpectedHopOffset` + `valueOracleHopSet` as a hermetic completeness scaffold). See **Load-bearing verification → Hazard C**.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| ES → RunTrace parse (query shape, entry-marker exclusion, keeper discrimination) | Test fixture (`AnalyzerE2ETests`, `Category=RealStack`) | — | The fixture is the only IO boundary; D-01/D-02 query edits live here |
| Pure verdict (completeness, tolerance, metric gate) | Pure engine (`PassFailEngine`, no IO) | — | Every deletion here is provable hermetically before any live run |
| Structural completeness evidence | Framework ES logs (`attributes.StepId` FW-01, `attributes.NextStepId` FW-02) | — | Survives Phase 77 unchanged; the sole binding completeness axis post-D-03 |
| Recovery evidence | Keeper ES logs (`attributes.ReinjectOutcome`) | — | D-02 keeps the keeper query symmetric with the structural `must_not` |
| Metric corroboration (MG-1/2/3) | Prometheus windowed deltas | — | Collector-blind secondary axis; PRESERVED unchanged (not a gray area) |
| Live-gate orchestration | `phase-68-sweep.ps1` → `phase-67-harness.ps1` | Reseed ops | Wrapper unchanged; only reseed + rerun |

<phase_requirements>
## Phase Requirements

No REQUIREMENTS.md exists for this milestone (v9.0.0). Requirements are tracked via ROADMAP §Phase 78 (line 805) + CONTEXT decisions D-01..D-04. The mapping below is the researcher's derivation.

| ID | Description | Research Support |
|----|-------------|------------------|
| D-01 | Delete entry-marker detector; `exists attributes.ExecutionId` is sole exclusion | Delete-safety proven GO — see Load-bearing verification Q1 |
| D-02 | Exclude keeper reinject records via `must_not exists attributes.ReinjectOutcome` on the structural query | Exact JSON placement — see ES Query Change section |
| D-03 | Full value-oracle deletion (fixture + engine + `PassFailEngineValueChainFacts.cs`) | Precise delete inventory + `ExpectedHopOffset` coupling hazard |
| D-04 | Live-gate reproduces the 7-scenario baseline with value axis dark | Per-scenario tolerance-path table + runbook |
</phase_requirements>

## Load-bearing verification

### Q1 — D-01 delete-safety: is `Step_A` / `M_1` evidence load-bearing? → **GO (delete is safe)**

**The claim to disprove:** "If `Step_A`'s structural record vanishes, `stepIdByMessageId[M_1]` never populates, so the ANL-03 redundancy edge for the first hop cannot resolve, and something scored breaks."

**Evidence it does NOT break anything scored:**

1. **`Step_A` is already excluded from `stepIdByMessageId` TODAY.** In `BuildRunTraces` (`AnalyzerE2ETests.cs:562-601`), the write `stepIdByMessageId[msgEl.GetString()!] = stepId` is at **:593**, which is reached only *after* the entry-marker guard at **:585-589** does `continue`. `Step_A` (Mode-2 seed, `ExecutionId == Guid.Empty`) hits that `continue`, so `stepIdByMessageId[M_1]` is *never populated even now*. The ANL-03 resolve `stepIdByMessageId.TryGetValue(entryEl…, out var producerStep)` (`:633`) already fails best-effort for `M_1`. `[VERIFIED: AnalyzerE2ETests.cs:585-593, :632-636]`

2. **`M_1` only ever proves `Step_A`, and `Step_A` is never expected.** The ANL-03 edge resolves each fan-out's inbound `EntryId` (`M_N`) to its producer step. `M_1` = `Step_A`'s output → resolving it would only ever add `Step_A` to `proven`. But:
   - The ANL-02 expected set is the FW-02 dispatch `NextStepId`s (`Step_B..Step_G`) — **`Step_A` is never a `NextStepId`** (nothing dispatches *to* the seed). `[VERIFIED: AnalyzerE2ETests.cs:604-621]`
   - The engine's `valueOracleHopSet` explicitly excludes offset-0 (`ExpectedHopOffset.Where(kv => kv.Value != 0)`, `PassFailEngine.cs:158`) — `Step_A` (offset 0) is excluded from the label-fallback expected set too. `[VERIFIED: PassFailEngine.cs:158, :513]`
   - Therefore `Step_A` is never in `MissingStepIds(r)`, so ANL-03 never needs to reconcile it. Proving it ran is dead weight.

3. **Every OTHER hop's ANL-03 edge is unaffected.** `Step_B..Step_G` each populate `stepIdByMessageId` from their own real (non-empty-execution) processor records (`M_2, M_3, …`) at `:593`. Those writes are untouched by D-01. The fan-out targeting `Step_C` resolves its inbound `M_2` → `Step_B` (proven), etc. `[VERIFIED: AnalyzerE2ETests.cs:591-593, :629-636]`

4. **Post-77 the query filter makes it cleaner, not different.** `Step_A`'s `attributes.ExecutionId` is ABSENT post-77 (empty GUID skipped by `ExecutionLogScope.BuildState`), so `BuildStepSearchBody`'s `exists attributes.ExecutionId` filter (`:288`) drops the record before it enters the loop. Net effect on scoring: identical to today's `continue`. `[CITED: 77-CONTEXT.md "Load-bearing couplings #1"]`

**Verdict: GO.** Delete `IsEntryMarker`/`IsEntryMarkerExecution` and all guards. The `exists attributes.ExecutionId` filter is a complete replacement. No fall-back to adapt-and-keep is needed.

> **Note for the planner:** `entryMarkerCorrelations` (`:543`, `:587`) is *write-only* dead code even today — it is `.Add`-ed at `:587` but never read anywhere. Deleting it is pure cleanup with zero behavioural effect. `[VERIFIED: grep — no read of entryMarkerCorrelations]`

### Q2 — D-03/D-04 coverage: does deleting value-chain path #1 flip any scenario? → **No scenario at risk**

**The decisive fact:** the committed baseline had **no incomplete-but-tolerated runs at all**.

```
HEAD:analyzer-reports/phase-68-summary.json  (all 7 scenarios)
TEST-01 Pass startedRuns=11 completeRuns=11   TEST-05 Pass startedRuns=8  completeRuns=8
TEST-02 Pass startedRuns=10 completeRuns=10   TEST-06 Pass startedRuns=8  completeRuns=8
TEST-03 Pass startedRuns=9  completeRuns=9    TEST-07 Pass startedRuns=8  completeRuns=8
TEST-04 Pass startedRuns=10 completeRuns=10
```

Since `complete = scored.Where(RunComplete)` (`PassFailEngine.cs:180`) and a telemetry-gap / in-flight-loss run is by definition *incomplete* (it fails `RunComplete`, `:176`), `completeRuns == startedRuns` for all 7 means **every started run was fully complete** — zero runs went through path #1, path #2, keeper-drop, or redis-wipe tolerance. `[VERIFIED: HEAD:analyzer-reports/phase-68-summary.json + PassFailEngine.cs:176-180, :211-312]`

**Consequence:** the "subtle risk" D-04 is designed to catch (a path-#1-tolerated run that must migrate to path #2) **had no instances in the baseline**. Deleting path #1 is verdict-neutral against this baseline by construction. The gate proves the *rerun* stays all-complete; if the rerun happens to produce an OTLP-dropped hop, ANL-03 path #2 (built Phase 76 to need no seed oracle) covers it — that is its design intent and is exercised hermetically by surviving facts (`PassFailEngine_ReconcileRedundancy_*`).

**Per-scenario tolerance-path table** (baseline verdict from summary; fault model from `phase-67-harness.ps1:95-102`):

| Scenario | Fault (harness) | MG-1 binding | Baseline verdict | Tolerated runs in baseline | Path its tolerated runs used | Covered without value oracle? |
|----------|-----------------|--------------|------------------|----------------------------|------------------------------|-------------------------------|
| TEST-01 | none (baseline) | true (`Mg1Binding[TEST-01]=true`) | Pass 11/11 | 0 | none (all complete) | **yes** — no tolerance needed |
| TEST-02 | processor crash, stop-start | false | Pass 10/10 | 0 | none | **yes** |
| TEST-03 | orchestrator crash, stop-start | false | Pass 9/9 | 0 | none | **yes** |
| TEST-04 | keeper crash (both replicas) | true | Pass 10/10 | 0 | none | **yes** |
| TEST-05 | redis crash, stop-start | false | Pass 8/8 | 0 | none (redis-wipe path idle) | **yes** |
| TEST-06 | rabbitmq crash (nack-requeue) | true | Pass 8/8 | 0 | none | **yes** |
| TEST-07 | redis + rabbitmq combined | false | Pass 8/8 | 0 | none (redis-wipe path idle) | **yes** |

No scenario is at risk of flipping to a binding FAIL from the D-03 delete. The recovery in these whole-tier crash scenarios is by broker redelivery + retry (they complete fully after recovery), which is exactly why the baseline is all-complete. `[VERIFIED: phase-67-harness.ps1:96-102 notes + baseline JSON]`

> **Residual watch item (not a blocker):** the *rerun* is a fresh live run — its exact `startedRuns` counts will differ (they are firing-count-dependent), and a genuinely OTLP-dropped hop under load would surface as a `TelemetryGap` via ANL-03. That is a legitimate PASS with `startedRuns > completeRuns`, NOT a baseline mismatch to panic over. D-04's gate is *verdict* reproduction (all PASS), not count reproduction. Per D-04, a real verdict *shift* → STOP and treat as a finding.

### Hazard C — `ExpectedHopOffset` deletion breaks surviving hermetic facts (HIGHEST-VALUE finding)

D-03 lists `ExpectedHopOffset` (`PassFailEngine.cs:510`) for deletion. This is **not free**. After deleting path #1 and `CheckValueChain`, the only remaining use of `ExpectedHopOffset` is:

```
ExpectedHopOffset (:510)  →  valueOracleHopSet (:158)  →  ExpectedFor legacy label fallback (:165)
```

`ExpectedFor` (`:161-167`) resolves a run's expected set as: (1) explicit `expectedStepIdsByExecution`, else (2) `valueOracleHopSet` when `DistinctLabels.Count > 0`, else (3) `observedUnion`. Several **surviving** `PassFailEngineFacts.cs` facts build runs via `FromLabels` (so `DistinctLabels.Count > 0`) **without** passing `expectedStepIdsByExecution`, so they depend on branch (2). If `ExpectedHopOffset`/`valueOracleHopSet` are deleted, those facts fall to branch (3) `observedUnion` — which, for a **single stalled run with no complete run in the cohort**, equals the run's own (short) stepId set, so the run is scored *complete* → the fact's `Missing==1`/`Fail` assertion breaks (false PASS). `[VERIFIED: PassFailEngine.cs:152-167 + PassFailEngineFacts.cs cohort shapes]`

**Facts that BREAK if `ExpectedHopOffset` is deleted without migration** (single incomplete run, no complete run to seed `observedUnion`):
- `Incomplete_StartedAfterRecovery_Yields_Fail` (`:240`, `Missing4Hops` only)
- `RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail` (`:296`, `Missing4Hops` only)
- `KeeperReinject_VetoesRedisWipeTolerance_…_Yields_Fail` (`:339`, `Missing4Hops` only)
- `InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass` (`:256`, six `Missing4Hops`, no complete run — currently passes via tolerance, would pass vacuously as "complete")
- `Verdict_PartialEvidence_RealConservationGap_Yields_Fail` (`:580`, `Missing4Hops` only — currently fails on metric gap; would still fail but for the wrong reason)

**Facts that survive `observedUnion` fallback** (a complete run is present in the cohort, so `observedUnion` = full set): `Incomplete_StartedRun_DropsStepF2` (`:81`, single run — CAUTION: also breaks, `observedUnion` = its own 9-label set → "complete" → false PASS), `InFlight_LossBeforeRecovery` (`:222`), `KeeperDrop_MarksIncomplete` (`:277`), `RedisWipe_StalledBeforeRecovery` (`:316`). Note `Incomplete_StartedRun_DropsStepF2` is single-run and **also breaks**.

**Two clean resolutions (planner picks one):**

- **Option A (recommended, lower-risk): RETAIN `ExpectedHopOffset` + `valueOracleHopSet` + the `ExpectedFor` label-fallback branch** as a hermetic-only completeness scaffold. Delete only the VALUE-CHAIN machinery (`CheckValueChain`, `ResolveSeed`, `valueOracleSupplied`, `seedsByExecution`, path #1, WR-02 guard, the `:337-394` loop). This fully satisfies the phase intent ("drop the concrete value oracle" = drop the `Produced`-value chain) while keeping the label→depth completeness map the surviving facts need. **Deviation:** contradicts D-03's literal inclusion of `ExpectedHopOffset`/`ResolveSeed` in the delete list — flag to the user/discuss before landing (see Assumptions Log A1).
- **Option B (matches D-03 letter): migrate the ~6 breaking label-based facts to `FromStepIds` + explicit `expectedStepIdsByExecution`** (the pattern the Phase-76 facts already use, `PassFailEngineFacts.cs:386-508`), THEN delete `ExpectedHopOffset`/`valueOracleHopSet`/the label fallback. More edits, but removes the last value-oracle vestige from the engine.

Either way, the planner MUST treat "delete `ExpectedHopOffset`" and "migrate/retain the label-based facts" as a **single atomic task** — they cannot be split across waves or the hermetic suite goes red.

## Precise delete / edit inventory

### `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`

| Symbol / region | Lines | Action | Notes |
|-----------------|-------|--------|-------|
| `BuildStepSearchBody` | 281-296 | **MODIFY** | Add `must_not` (D-02) — see ES Query Change |
| `BuildValueOracleSearchBody` | 334-349 | **DELETE** | value oracle query (D-03) |
| `valueHits` fetch (`es.SearchAllHits(BuildValueOracleSearchBody…)`) | 190-191 | **DELETE** | |
| `BuildRunTraces(...)` call — `valueHits` arg | 193 | **MODIFY** | drop `valueHits` argument |
| `BuildRunTraces` signature — `List<JsonElement> valueHits` param | 528-530 | **MODIFY** | remove `valueHits` param |
| Value-oracle loop (`valuesByInstance` build) | 639-656 | **DELETE** | incl. `TryReadSum`/`TryReadProduced` calls at :651-652 |
| `values`/`labels` locals in trace build | 660-662, 673 | **MODIFY** | pass `null` for values/labels to `RunTrace.FromStepIds` |
| `seedsByExec` map build | 686-694 | **DELETE** | |
| `TraceCohort.SeedsByExecution` field | 483 | **DELETE** | |
| `SeedsByExecution = seedsByExec` in returned cohort | 701 | **DELETE** | |
| `Analyze(... seedsByExecution: cohort.SeedsByExecution …)` | 243 | **MODIFY** | remove the `seedsByExecution:` argument |
| `IsEntryMarkerExecution` method | 714-715 | **DELETE** | D-01 |
| `IsEntryMarkerExecution(...)` guards | 575, 585-589, 617, 648 | **DELETE** | :648 vanishes with the value loop; :575/:585/:617 removed per D-01 |
| `entryMarkerCorrelations` declaration + `.Add` | 543, 587 | **DELETE** | write-only dead code |
| `TryReadSum` | 722-730 | **DELETE** | only caller is the deleted value loop (:651) |
| `TryReadProduced` | 743-751 | **DELETE** | only caller is the deleted value loop (:652) |
| `TryReadTimestamp` | 760-796 | **KEEP** | used by the surviving trip-duration span (:595) |
| `BuildKeeperOutcomeSearchBody` / `BuildKeeperOutcomeMap` | 402-469 | **KEEP** | the symmetric keeper query (D-02 partner) |
| Dispatch in-degree convergent derivation | 604-637 | **KEEP** | Phase-76 fix, unaffected |

> After removing the `valueHits`/value-oracle path, `BuildRunTraces` still returns `TraceCohort` with `ExpectedStepIdsByExecution` + `OrchestratorConsumedStepIdsByExecution` (the ANL-02/03 sets) — those stay. `RunTrace.FromStepIds` already defaults `Values`/`Labels` empty (`RunTrace.cs:224-238`), so the traces build cleanly with no value axis.

### `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`

| Symbol / region | Lines | Action | Notes |
|-----------------|-------|--------|-------|
| `IsEntryMarker` | 62-63 | **DELETE** | D-01 |
| `scored = runs.Where(r => !IsEntryMarker(r))` | 134 | **MODIFY** | → `var scored = runs.ToList();` (query filter now excludes empty-execution upstream) |
| `seedsByExecution` param | 120 | **DELETE** | D-03 |
| `valueOracleSupplied` | 209 | **DELETE** | |
| Telemetry-gap **path #1** (value-chain reconciliation) | 235-255 | **DELETE** | `ResolveSeed` + `CheckValueChain` + terminal-anchor block |
| Telemetry-gap **path #2** (ANL-03 framework-redundancy) | 257-274 | **KEEP** | sole non-binding reconciliation |
| Value-chain assertion loop (`valueChainDetail`/`valueChainOk`/`completeRunsWithValues`) | 337-377 | **DELETE** | |
| WR-02 vacuous-green guard | 379-394 | **DELETE** | |
| `valueChainOk` in `pass = …` verdict | 468 | **MODIFY** | drop `&& valueChainOk` |
| `ValueChainOk` / `ValueChainDetail` in report + `BuildSummary` | 491-492, 498, 583-608 | **MODIFY** | discretion: hardcode `ValueChainOk = true` to keep report shape, or remove the fields (sweep reads only Verdict/StartedRuns/CompleteRuns/Missing/Duplicates — see Report Shape note) |
| `ExpectedHopOffset` | 510-520 | **DELETE or KEEP** | **Hazard C** — atomic with fact migration (Option A keep / Option B delete) |
| `valueOracleHopSet` | 158-159 | **DELETE or KEEP** | tied to `ExpectedHopOffset` decision |
| `ExpectedFor` label-fallback branch (`if (r.DistinctLabels.Count > 0) return valueOracleHopSet;`) | 165 | **DELETE or KEEP** | tied to same decision |
| `ResolveSeed` | 536-552 | **DELETE** | only callers are the deleted path #1 + value-chain loop |
| `CheckValueChain` | 560-581 | **DELETE** | |
| Metric gate (MG-1/2/3) | 399-429 | **KEEP** | PRESERVED unchanged (secondary axis) |
| Three-class verdict (evidence-sufficiency / INCONCLUSIVE) | 431-470 | **KEEP** | ANL-04/05, unaffected |

**Report-shape note:** `phase-68-sweep.ps1:102-115` reads only `Verdict`, `StartedRuns`, `CompleteRuns`, `Missing`, `Duplicates` from each `{id}.json`. It never reads `ValueChainOk`/`ValueChainDetail`/`TelemetryGap`. So removing or nulling the value-chain report fields does not break the wrapper. `[VERIFIED: phase-68-sweep.ps1:104-115]`

### Hermetic fact files

| File / method | Action | Notes |
|---------------|--------|-------|
| `PassFailEngineValueChainFacts.cs` (entire, 375 lines) | **DELETE** | D-03 — not skip |
| `PassFailEngineFacts.cs :: PassFailEngine_EntryMarker_EmptyExecutionId_Excluded_From_Scoring_D09` (431-448) | **DELETE** | Engine no longer excludes `Guid.Empty` (D-01 moves exclusion to the ES query, which is fixture-level, not engine-testable). This fact would go red (marker now scored as a started run). |
| `PassFailEngineFacts.cs` label-based facts depending on `valueOracleHopSet` (see Hazard C list) | **MIGRATE** (Option B) or **KEEP** (Option A) | Atomic with `ExpectedHopOffset` decision |
| `PassFailEngineFacts.cs` stepId-path facts (`:386-508`, `:610-661`) | **KEEP** | Already on the surviving structural/metric/ANL-03 path |
| `BuildKeeperOutcomeMapFacts.cs` | **KEEP** | Pins the `"reinject"`-wins tie-break — unaffected by D-02 (the map query is unchanged) |
| `FanInHermeticHarnessFacts` (absolute-value terminal anchor) | **KEEP** | Retains the absolute-value proof the live analyzer no longer re-proves (D-03) |

## ES Query Change (D-02)

Add a `must_not` sibling to the existing `filter` inside the `bool` of `BuildStepSearchBody` (`AnalyzerE2ETests.cs:281-296`). `ReinjectOutcomeFieldPath` already exists (`EsIndexNames.cs:171 = "attributes.ReinjectOutcome"`). Exact target shape:

```json
"query": {
  "bool": {
    "filter": [
      { "exists": { "field": "attributes.StepId" } },
      { "exists": { "field": "attributes.ExecutionId" } },
      { "range": { "@timestamp": { "gte": "<windowStart:o>", "lte": "<snapshot:o>" } } }
    ],
    "must_not": [
      { "exists": { "field": "attributes.ReinjectOutcome" } }
    ]
  }
}
```

In the raw-string template (`$$"""…"""`), the interpolated form is:
```
"must_not": [
  { "exists": { "field": "{{EsIndexNames.ReinjectOutcomeFieldPath}}" } }
]
```
placed as a sibling key after the closing `]` of `"filter"` inside `"bool"`.

**Scope:** apply the `must_not` to `BuildStepSearchBody` **only**. Do NOT add it to `BuildDispatchSearchBody` (:308) — keeper reinject records carry `attributes.StepId`+`MessageId` after Phase-77 D2 but do NOT carry `attributes.NextStepId`, so they never enter the dispatch cohort. The keeper query `BuildKeeperOutcomeSearchBody` (:402) *requires* `exists ReinjectOutcome` — the two queries stay symmetric (one forbids the field, one requires it). `[VERIFIED: AnalyzerE2ETests.cs:281, :308, :402-416; 77-CONTEXT.md D2 + "coupling #2"]`

**Discretion (D-02):** the parse-level guard in `BuildRunTraces` that discriminates processor records by `hasMessageId` (`:560`) may remain as belt-and-suspenders. Note it does NOT currently discriminate keeper records specifically (keeper reinject records also have `MessageId`), so the `must_not` query clause is the real fix — the parse guard alone would let keeper records through as "did-run" hits. Recommend keeping the parse guard AND adding the query `must_not`.

## Live-gate runbook (D-04)

**Ordering: hermetic GREEN first, then reseed + sweep.**

### Step 1 — Hermetic suite green (Docker-less)
Run `BaseApi.Tests.exe` directly (`dotnet test` hangs on Windows MTP — [[hermetic-test-command]]):
```
tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack
```
Acceptance: 0-warning Debug+Release build; changed TEST scope has **zero NEW failures** against the [[hermetic-test-command]] baseline of **272 pre-existing Docker-less failures**. `[CITED: 78-CONTEXT.md D-04; 77-CONTEXT.md specifics]`

### Step 2 — Mandatory SourceHash reseed (Phase 77 touched `BaseProcessor.Core`)
Per [[rebuild-sourcehash-reseed-order]] — the new SourceHash has no processor row, so a reset heal-wait fails until you graph-delete → seed → then start. Order:
1. Rebuild **both** host configs (Debug + the config the compose images build from).
2. `docker compose build` the images (so the running container is the just-built image; the harness itself does `docker compose up -d --force-recreate` at STEP A, `phase-67-harness.ps1:154`).
3. **graph-DELETE** the old graph/processor rows.
4. **seed** the fan-out workflow.
5. Confirm the **204** activation gate.
6. Only THEN run the sweep.

### Step 3 — Run the sweep
```
pwsh -File scripts/phase-68-sweep.ps1
```
Run-all + collect, no fail-fast (`phase-68-sweep.ps1` param default = all 7 in numeric order). Exit 0 iff all 7 PASS. `[VERIFIED: phase-68-sweep.ps1:52-115]`

### Step 4 — Compare to baseline
Every scenario must reproduce its **verdict** (all PASS) from `analyzer-reports/phase-68-summary.json`. A legitimate verdict *shift* → STOP, treat as a finding (do NOT silently re-baseline).

### Known traps
- **Stale Debug analyzer report read** ([[phase-68-sweep-stale-report-read]]): the roll-up can read a stale Debug `{id}.json` from a prior build. **Trust the harness exit code** over the tabulated report row when they disagree. The sweep discovers the report via `Get-ChildItem -Recurse -Filter "$id.json"` under `tests/BaseApi.Tests/bin` (`phase-68-sweep.ps1:110-112`) — a stale artifact under `bin/Debug` vs `bin/Release` can mislead.
- **TEST-01 cold-ES INCONCLUSIVE flake** ([[phase-68-sweep-stale-report-read]]): TEST-01 can flake to INCONCLUSIVE on cold ES (total trace darkness + self-consistent conservation → `Verdict.Inconclusive`, `PassFailEngine.cs:450-465`). INCONCLUSIVE (harness exit 2) is **NOT FAIL** — re-run TEST-01 deliberately against warm ES (`pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-01`). No auto-retry (D-04). `[VERIFIED: phase-68-sweep.ps1 exit-code table :20-22]`
- **Working-tree `phase-68-summary.json` is a stale single-scenario write.** `git status` shows it modified to a single `TEST-07` object; the real 7-scenario baseline is `git show HEAD:analyzer-reports/phase-68-summary.json`. Compare the rerun against **HEAD**, not the working-tree file.

## Validation Architecture

> nyquist_validation is enabled (`config.json workflow.nyquist_validation: true`). The hermetic fact suites are the sampling grid over the engine's decision branches — every deleted/kept branch must have a fact proving it before the live run.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (MTP host), net8.0 |
| Config file | none needed — run the test host exe directly |
| Quick run command | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` |
| Full suite command | above (hermetic) + `scripts/phase-68-sweep.ps1` (live, `Category=RealStack`) |

### Decision-branch → fact map (sampling grid)
| Decision | Branch after rework | Fact (sampling point) | Exists? |
|----------|--------------------|-----------------------|---------|
| D-01 | Entry marker excluded by ES query, NOT engine | (fixture-level — not engine-testable; `PassFailEngine_EntryMarker_…_D09` DELETED) | ❌ delete fact |
| D-02 | Keeper record kept out of structural cohort | `BuildKeeperOutcomeMapFacts` (map side) — KEEP | ✅ |
| D-03 | Value-chain machinery gone | delete `PassFailEngineValueChainFacts.cs` | ✅ (delete) |
| structural completeness | stepId-keyed vs ANL-02 expected | `PassFailEngine_StepIdCompleteness_AllRunsCover` (:386), `_ExpectedSet_Test08` (:410) | ✅ KEEP |
| ANL-03 path #2 (sole tolerance) | redundancy reconciles missing hop, no seed oracle | `PassFailEngine_ReconcileRedundancy_MissingHop…` (:468), `_MissingBoth_Yields_Fail` (:491), `Verdict_NonZeroTelemetryGap_StaysPass_D01` (:610) | ✅ KEEP |
| SMP-01 degrade | framework-records-only cohort passes, value chain N/A | `PassFailEngine_OracleAbsent_FrameworkRecordsOnly…` (:450) | ✅ KEEP |
| keeper-drop / redis-wipe tolerance | timestamp + keeper-outcome classification | `KeeperDrop_MarksIncomplete` (:277), `RedisWipe_StalledBeforeRecovery` (:316), `KeeperReinject_Vetoes…` (:339) | ✅ KEEP (Hazard C: some need migration) |
| metric gate MG-1/2/3 | unchanged | `MetricGate_*` (:152-206) | ✅ KEEP |
| three-class verdict | INCONCLUSIVE / FAIL / PASS on evidence sufficiency | `Verdict_TraceDark_*`, `MetricGate_CollectorBlind_*`, `MetricGate_LiveCounters_GenuineGap_*` (:545-661) | ✅ KEEP |

### Wave 0 gaps
- [ ] Delete `PassFailEngineValueChainFacts.cs`; confirm no other file references its members (grep clean except `bin/obj`).
- [ ] Delete `PassFailEngine_EntryMarker_…_D09` and confirm no compile reference to `IsEntryMarker`.
- [ ] Resolve Hazard C atomically (Option A keep `ExpectedHopOffset`, or Option B migrate the ~6 label facts) — hermetic suite must be green before Step 2.

*(Existing infrastructure covers all surviving branches; the only "new" work is deletions + the Hazard-C fact migration/retention.)*

## Common Pitfalls

### Pitfall 1: Deleting `ExpectedHopOffset` before migrating the label facts
**What goes wrong:** ~6 surviving `PassFailEngineFacts` fall to `observedUnion` and score single stalled runs as "complete" → false-PASS assertions flip red.
**How to avoid:** treat `ExpectedHopOffset` delete + fact migration as one atomic task (Hazard C, Option B), or retain `ExpectedHopOffset`/`valueOracleHopSet` (Option A).
**Warning signs:** `Incomplete_StartedAfterRecovery`, `RecoverableButLost_NoKeeperDrop`, `KeeperReinject_Vetoes` go red with `Missing==0` where `1` expected.

### Pitfall 2: Adding `must_not ReinjectOutcome` to the wrong query
**What goes wrong:** adding it to the keeper query (`BuildKeeperOutcomeSearchBody`) would exclude the very records it needs; adding it to the dispatch query is a harmless no-op but signals a misread.
**How to avoid:** `must_not` goes on `BuildStepSearchBody` only. The keeper query keeps its `exists ReinjectOutcome` (symmetric opposite).

### Pitfall 3: `.keyword` sub-field on any attribute path
**What goes wrong:** the OTel-managed `logs-generic.otel-default` data stream maps string attributes DIRECTLY to `keyword` (no `.keyword` sub-field). Querying `attributes.ReinjectOutcome.keyword` returns ZERO hits (the trap that broke 4 facts at Phase 11 UAT, commit 9370e89 reverted).
**How to avoid:** always use the bare `EsIndexNames.*FieldPath` consts. `[CITED: EsIndexNames.cs:52-69, :162-169]`

### Pitfall 4: Trusting the sweep's tabulated report row over the harness exit code
**What goes wrong:** a stale Debug `{id}.json` mis-tabulates a verdict. **How to avoid:** trust `harnessExit`. `[CITED: [[phase-68-sweep-stale-report-read]]]`

### Pitfall 5: Scoring cold-ES TEST-01 as FAIL
**What goes wrong:** INCONCLUSIVE (exit 2) treated as a regression. **How to avoid:** re-run TEST-01 warm; INCONCLUSIVE ≠ FAIL, no auto-retry.

## Runtime State Inventory

> This is a TEST + scripts rework with a live reseed. No product data model changes, but the live run has runtime state.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Live ES `logs-generic.otel-default` data stream holds Phase-77-shaped logs (Tier-1 ids in `attributes.*`, empty GUIDs absent). The analyzer READS these; it writes none. | none — reader-side only |
| Live service config | Fan-out workflow seeded in Postgres/graph + Redis L2; keeper/orchestrator/processor containers. The reseed (D-04) regenerates all of it. | graph-DELETE → seed → 204 (runbook Step 2) |
| OS-registered state | None — the sweep is invoked ad hoc via `pwsh -File`, no scheduled task. | None — verified: harness/sweep are manual scripts |
| Secrets/env vars | Harness env seams: `SCENARIO_ID`, `WINDOW_START_UTC`/`WINDOW_END_UTC`, `RECOVERY_UTC` (read by the fixture, `AnalyzerE2ETests.cs:112,:140-141,:234-235`). Unchanged by this phase. | none |
| Build artifacts | **SourceHash** — Phase 77 changed `BaseProcessor.Core` source, so the built image's SourceHash differs from any seeded processor row. Stale `bin/`/`obj/` also risk the stale-report trap. | rebuild both host configs + `docker compose build` before reseed ([[rebuild-sourcehash-reseed-order]]) |

## State of the Art

| Old approach (pre-77) | Current approach (post-77, this phase) | Impact |
|-----------------------|----------------------------------------|--------|
| Entry marker = `ExecutionId == Guid.Empty` value on `hop executed` | Entry marker = **absent** `attributes.ExecutionId` (empty GUID skipped by scope) → dropped by `exists ExecutionId` filter | D-01: detector deleted, exclusion is free |
| Keeper hand-carries ids; no ambient scope | Keeper opens the execution scope (77 D2) → reinject records gain `attributes.StepId`+`MessageId` | D-02: must exclude via `must_not ReinjectOutcome` |
| Value oracle via `Processor.Sample` author logs (`attributes.StepLabel`/`Produced`) | Sample author logs DELETED (77 D4) → value-oracle query returns nothing | D-03: full deletion (leaving dormant = vacuous-green trap) |
| Verdict = completeness + duplicate + **value-chain** + metric gate | Verdict = structural stepId completeness + ANL-03 redundancy + metric gate | Value axis dark; ANL-03 is sole non-binding reconciliation |

**Deprecated/outdated:**
- `PassFailEngineValueChainFacts.cs` — deleted; absolute-value proof moves entirely to `FanInHermeticHarnessFacts` (durable L2 blob).
- `BuildValueOracleSearchBody`, `CheckValueChain`, `ResolveSeed`, `TryReadProduced`/`TryReadSum` — dead once the sample author logs are gone.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Retaining `ExpectedHopOffset`+`valueOracleHopSet` (Hazard C Option A) satisfies the phase intent despite D-03 listing them for deletion, because they are the label→depth *completeness* map, not the `Produced`-*value* chain | Hazard C | If the user insists on literal D-03, Option B (migrate ~6 facts) is required instead — larger edit, must land atomically |
| A2 | The live rerun will again produce all-complete runs (matching the baseline), because these whole-tier crash scenarios recover fully by broker redelivery | Q2 / runbook Step 4 | If the rerun produces an OTLP-dropped hop, it surfaces as a legitimate ANL-03 `TelemetryGap` PASS (not a mismatch) — still fine, but the operator must not mistake `startedRuns > completeRuns` for a regression |
| A3 | The parse-level `hasMessageId` guard alone does NOT exclude keeper records (they carry `MessageId`), so the query `must_not` is the real fix | ES Query Change | If keeper reinject records somehow lack `MessageId` live, the parse guard would suffice — but D-02 mandates the query clause regardless (belt-and-suspenders) |
| A4 | net8.0 xUnit host; run exe directly (`dotnet test` hangs on Windows MTP) | Validation Architecture | Stated in CONTEXT + memory; if the host changed, the run command changes but not the logic |

## Open Questions

1. **Hazard C resolution — retain vs migrate (`ExpectedHopOffset`).**
   - What we know: D-03 lists `ExpectedHopOffset`/`ResolveSeed` for deletion; ~6 surviving facts depend on `ExpectedHopOffset` via `valueOracleHopSet`.
   - What's unclear: whether the user wants the literal D-03 delete (⇒ migrate the facts) or accepts retaining the completeness scaffold.
   - Recommendation: default to **Option A (retain)** for lowest risk; surface A1 to discuss-phase for a one-line confirmation. Either way it is one atomic task.

2. **`ValueChainOk`/`ValueChainDetail`/`TelemetryGap` report fields — remove vs null.**
   - What we know: the sweep wrapper never reads them (`phase-68-sweep.ps1:104-115`).
   - Recommendation: keep the fields but hardcode `ValueChainOk = true` (report-shape stability for any ad-hoc JSON consumer); this is Claude's discretion per D-03 scope.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker + compose | Live sweep (`Category=RealStack`) | (verify at run time) | — | none — the live gate cannot run without it |
| Elasticsearch 8.15.x + collector | ES log source | (up via compose) | 8.15.5 / collector 0.152.0 | none |
| Prometheus | MG-1/2/3 secondary axis | (up via compose) | — | none |
| pwsh | `phase-68-sweep.ps1` / `phase-67-harness.ps1` | yes (Windows 11) | — | — |
| net8.0 SDK + `BaseApi.Tests.exe` | hermetic suite | yes | net8.0 | — |

**Missing dependencies with no fallback:** the live gate (Step 3) is Docker-gated; if the compose stack is not healthy, run only the hermetic suite (Steps 1) and defer the live gate. The hermetic suite fully proves every engine-branch deletion before any live run.

## Sources

### Primary (HIGH confidence — read this session)
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — ES→RunTrace fixture (all line numbers verified)
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — pure verdict engine (all line numbers verified)
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs`, `PassFailEngineValueChainFacts.cs`, `RunTrace.cs`
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` (`ReinjectOutcomeFieldPath:171`)
- `git show HEAD:analyzer-reports/phase-68-summary.json` — the 7-scenario baseline (all PASS, all complete)
- `scripts/phase-68-sweep.ps1`, `scripts/phase-67-harness.ps1` (scenario fault model :95-102)
- `.planning/phases/78-*/78-CONTEXT.md`, `.planning/phases/77-*/77-CONTEXT.md`

### Secondary (project memory)
- [[rebuild-sourcehash-reseed-order]], [[hermetic-test-command]], [[phase-68-sweep-stale-report-read]], [[gsd-phase-numbering]]

## Metadata

**Confidence breakdown:**
- D-01 delete-safety: HIGH — traced against actual `BuildRunTraces` control flow; the `Step_A`/`M_1` edge is already dead today.
- D-03/D-04 coverage: HIGH — baseline JSON proves zero tolerance-path reliance (`completeRuns == startedRuns` all 7).
- Delete/edit inventory: HIGH — every symbol + line verified in-file this session.
- Hazard C (`ExpectedHopOffset` coupling): HIGH — reference chain and breaking facts enumerated from the code.
- ES query change: HIGH — const exists, placement verified against the raw-string template.

**Research date:** 2026-07-16
**Valid until:** ~2026-08-15 (stable — internal code, no external deps; re-verify only if the analyzer files change before planning)

## RESEARCH COMPLETE
