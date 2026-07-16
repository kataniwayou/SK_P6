---
phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
plan: 03
subsystem: testing
tags: [observability, analyzer, stepid, pass-fail-engine, telemetry, anl-01, anl-02, anl-03, smp-01]

# Dependency graph
requires:
  - phase: 76-01
    provides: "Framework per-hop record `hop executed {StepId} {ExecutionId} {CorrelationId} {EntryId} {MessageId} {Outcome}`; Mode-2 entry marker (ExecutionId==Guid.Empty, D-09)"
  - phase: 76-02
    provides: "Orchestrator FW-02 fan-out record `fan-out {..} {EntryId} {NextStepId}` + terminal-reached record `terminal reached {..} {EntryId} {StepId}` — attributes.NextStepId ES surface"
provides:
  - "Analyzer structural completeness re-keyed onto attributes.StepId (RunTrace.DistinctStepIds) — HopLabels deleted, single stepId-keyed source of truth (ANL-01/D-15)"
  - "ES-derived expected set (FW-02 NextStepId) gates completeness; dispatched-but-never-executed → binding Fail (ANL-02, TEST-08 closed)"
  - "Framework-redundancy reconciliation: a dropped hop record proven-run by an orchestrator record consuming M_N reconciles as a non-binding telemetry gap with NO seed oracle (ANL-03)"
  - "SMP-01 degrade: value-oracle-supplied is non-empty-seed-gated so framework-records-only cohorts degrade the value chain to N/A, never FAIL"
  - "RunTrace.FromStepIds structural factory + EsIndexNames.StepIdFieldPath/NextStepIdFieldPath"
affects: [76-04, 76-05, analyzer, verdict-class, live-gate]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Dual-axis RunTrace: structural stepId axis (DistinctStepIds, ANL-01) + separate degradable value-oracle label axis (Values/DistinctLabels, D-16)"
    - "Per-(corr,exec) expected-set resolution ladder: explicit ES FW-02 set > value-oracle canonical hop set (ExpectedHopOffset, legacy) > union-of-observed fallback"
    - "Three-query ES reader: structural (exists StepId) + dispatch (exists NextStepId) + value oracle (exists StepLabel), merged per (corr,exec)"

key-files:
  created: []
  modified:
    - "tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs"
    - "tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs"
    - "tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs"

key-decisions:
  - "Completeness fallback derives the legacy value-oracle hop set from the KEPT ExpectedHopOffset (offset!=0 excludes the Step_A seed) rather than a standalone structural constant — keeps PassFailEngineValueChainFacts (out of scope) green UNCHANGED while honoring D-15's HopLabels deletion"
  - "SMP-01 distinguisher: valueOracleSupplied = seedsByExecution is {Count>0} — a non-empty seed map means the oracle is genuinely present (broken-mapping → WR-02 FAIL); an empty/null map means framework-records-only → value chain N/A (never FAIL). This alone separates SMP-01 degrade from the WR-02 vacuous-green guard without a cohort-level anyValuesSurfaced signal"
  - "Structural StepId query returns both processor per-hop records (has attributes.MessageId) and orchestrator terminal-reached records (has attributes.WorkflowId, no MessageId); BuildRunTraces discriminates by MessageId presence — processor records are OBSERVED, terminal-reached records feed the ANL-03 proven set"
  - "ANL-03 engine contract is a clean set of proven stepIds per (corr,exec); the live EntryId->stepId resolver is best-effort (terminal-reached names the stepId directly; fan-out records resolve via the present-record MessageId->StepId map)"

patterns-established:
  - "RunTrace.FromStepIds(corr, exec, stepIds, values?, labels?, convergentStepId?) mirrors FromLabels' duplicate/convergent arithmetic on the stepId axis"
  - "Entry-marker (ExecutionId==Guid.Empty) exclusion is a shared executionId rule (PassFailEngine.IsEntryMarker + fixture IsEntryMarkerExecution), generalizing the old Step_A special case"

requirements-completed: [ANL-01, ANL-02, ANL-03, SMP-01]

# Metrics
duration: 45min
completed: 2026-07-16
---

# Phase 76 Plan 03: Analyzer StepId Re-key Summary

**The analyzer's structural verdict now binds to `attributes.StepId` framework records (not author `Step_*` strings): `HopLabels` is deleted, completeness is stepId-keyed against an ES-derived FW-02 expected set (dispatched-but-never-executed → Fail, TEST-08 closed), a dropped hop record is reconciled from orchestrator redundancy with no seed oracle (ANL-03), and the value oracle degrades to N/A for a processor with zero author logs (SMP-01).**

## Performance

- **Duration:** ~45 min
- **Started:** 2026-07-16T11:45:00Z (approx)
- **Completed:** 2026-07-16T12:40:00Z (approx)
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments
- **ANL-01 clean break (D-15):** the `StepLabel`-keyed `HopLabels` constant + static `IsComplete` are DELETED; structural completeness is computed per `(correlationId, executionId)` from `RunTrace.DistinctStepIds` (framework `attributes.StepId` records). `rg "HopLabels"` in `PassFailEngine.cs` returns 0.
- **ANL-02 expected set (TEST-08 closed):** completeness compares `DistinctStepIds` against a per-`(corr,exec)` expected set derived from the orchestrator FW-02 `attributes.NextStepId` records; a dispatched stepId with no matching processor record is a binding miss → `Verdict.Fail`. Derived from ES records only — no Postgres/graph read on the analyzer path (T-76-07 mitigated).
- **ANL-03 framework redundancy:** a run missing a hop's own processor record but whose missing stepId is proven-run by an orchestrator record consuming its output EntryId (M_N) reconciles as a NON-binding telemetry gap — with **no seed oracle** — while missing-from-BOTH stays a binding miss (D-01 non-binding-on-PASS preserved).
- **SMP-01 degrade:** the value oracle is a separate, degradable axis (D-16); with framework records only (no `StepLabel`/`Received`/`Produced`), the value chain is not-applicable (`ValueChainOk` stays true), never FAIL.
- **D-09 entry marker:** an `ExecutionId == Guid.Empty` record is counted entry-ran but excluded from every `(corr,exec)` expected/complete set, in both the engine (`IsEntryMarker`) and the live fixture (`IsEntryMarkerExecution`).

## Task Commits

Each task was committed atomically:

1. **Task 1: StepId field paths + RunTrace structural model + ES reader re-key** - `0e1e92b` (feat)
2. **Task 2: delete HopLabels; stepId-keyed completeness + ES-derived expected set (ANL-01/02, SMP-01)** - `16fde90` (feat)
3. **Task 3: generalize telemetry-gap reconciliation to framework redundancy (ANL-03)** - `a003609` (feat)

_The tasks are not TDD-split (test→feat) because this plan is a re-key of already-tested machinery: each task's facts and implementation land in one coherent feat commit, verified by the existing + new hermetic facts._

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` - Added `StepIdFieldPath` (`attributes.StepId`) + `NextStepIdFieldPath` (`attributes.NextStepId`), direct paths, no `.keyword` (Pitfall 2); kept `StepLabel`/`Sum`/`Produced`.
- `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` - Added the `StepIds`/`DistinctStepIds` structural axis + the `FromStepIds` factory (mirrors `FromLabels`' convergent-duplicate arithmetic on stepIds); `FromLabels` mirrors its labels into the stepId axis so the value-oracle facts keep computing completeness.
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` - Structural query re-keyed to `attributes.StepId`; added `BuildDispatchSearchBody` (FW-02) + `BuildValueOracleSearchBody` (D-16); `BuildRunTraces` builds stepId-keyed runs, discriminates processor vs terminal-reached records, handles D-09, and produces the ES-derived expected set + orchestrator-redundancy proven set; wired both into the `Analyze(...)` call.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` - Deleted `HopLabels`/`IsComplete`; added stepId-keyed `ExpectedFor`/`MissingStepIds`/`RunComplete`, the expected-set resolution ladder, the framework-redundancy reconciliation branch, `IsEntryMarker` (D-09), and the two new optional params (`expectedStepIdsByExecution`, `orchestratorConsumedStepIdsByExecution`). Re-gated `valueOracleSupplied` on a non-empty seed map (SMP-01).
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` - Added 6 stepId-set facts (completeness Pass, ExpectedSet/TEST-08 Fail, D-09 marker exclusion, oracle-absent N/A Pass, redundancy-reconcile Pass no-seed-oracle, missing-both binding Fail) — all built from non-`Step_*` synthetic stepIds.

## Decisions Made
See `key-decisions` frontmatter. The load-bearing one: the completeness fallback for a legacy value-oracle run derives its canonical hop set from the KEPT `ExpectedHopOffset` (D-16), so `PassFailEngineValueChainFacts` (out of scope, not in `files_modified`) and every pre-existing `PassFailEngineFacts` case stay green **unchanged** while `HopLabels` is deleted — a strict clean break for the production/live path with no second structural constant.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `net8.0` verify host instead of the plan's `net9.0`**
- **Found during:** Task 1 (verification)
- **Issue:** The plan's `<verify>` commands reference `bin/Debug/net9.0/BaseApi.Tests.exe`; the test project targets **net8.0** (confirmed on disk + prior-wave context).
- **Fix:** Used `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe` throughout.
- **Files modified:** none (verification-only).
- **Verification:** exe runs; 37/37 Analysis facts green.
- **Committed in:** n/a.

**2. [Rule 3 - Blocking] Separate value-oracle ES query rather than reading `Produced`/`StepLabel` off the structural hits**
- **Found during:** Task 1 (fixture re-key)
- **Issue:** The plan's Task-1 wording says "keep the `attributes.Produced`/`StepLabel` collection as the value oracle" on the same hits, but the framework per-hop record (`attributes.StepId`, 76-01) and the sample's value log (`attributes.StepLabel`/`Produced`, 73-01) are DISTINCT ES documents — the re-keyed structural query (`exists StepId`) no longer returns the value records.
- **Fix:** Added `BuildValueOracleSearchBody` (`exists StepLabel`) as the SEPARATE degradable value-oracle layer (exactly D-16's "separate axis"), and `BuildRunTraces` merges the two result sets per `(corr,exec)`. This is more faithful to D-15/D-16 than a single-query read.
- **Files modified:** `AnalyzerE2ETests.cs`.
- **Verification:** 0-warning build; RealStack fixture compile-gated (deferred, see below).
- **Committed in:** `0e1e92b` (Task 1 commit).

**3. [Note] ANL-03 engine logic committed under Task 2**
- **Found during:** Task 2 (single-method signature edit)
- **Issue:** `PassFailEngine.Analyze` is one method; adding the `orchestratorConsumedStepIdsByExecution` param + the framework-redundancy branch alongside the Task-2 completeness rewrite avoided a second churned edit of the same body. The redundancy logic is inert until evidence is supplied (default ⇒ empty proven set), so Task 2's commit is behavior-neutral for ANL-03.
- **Fix:** Task 3's commit adds the ANL-03 FACTS that exercise the branch + wires the live fixture's proven set. No functional gap between commits.
- **Files modified:** `PassFailEngine.cs` (Task 2), `PassFailEngineFacts.cs` + `AnalyzerE2ETests.cs` (Task 3).
- **Verification:** ANL-03 facts green in `a003609`.
- **Committed in:** `16fde90` (logic) + `a003609` (facts/wiring).

---

**Total deviations:** 3 (2 blocking auto-fixes, 1 commit-boundary note)
**Impact on plan:** No scope creep. Deviation 2 makes the fixture MORE faithful to D-15/D-16 (structural vs value oracle truly separate). All requirements satisfied.

## Issues Encountered
- **`PassFailEngineValueChainFacts` (out of scope) regression risk:** the re-key to stepId-keyed completeness would have broken this file's single-incomplete-run facts (union-fallback can't detect a missing hop in a one-run cohort). Resolved by the `ExpectedHopOffset`-derived value-oracle completeness fallback (see Decisions) — the file stays green UNCHANGED (verified 15/15 in the 37-fact Analysis run).
- **One transient RED during development:** the first SMP-01 attempt added a cohort-level `anyValuesSurfaced` guard that wrongly rescued the WR-02 broken-mapping fact (`Fail_CompleteRun_ZeroSurfacedValues_WithValueOracle`). Corrected by gating `valueOracleSupplied` on a non-empty seed map instead — one signal separates both cases cleanly. Re-verified 31/31 then 37/37.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The analyzer's structural half is now stepId-keyed and ES-only; `RunTrace.DistinctStepIds`, the expected set, and the redundancy proven set are ready for **76-04** (three-class verdict, which also touches `PassFailEngine.cs`/`PassFailEngineFacts.cs`) and **76-05** (live gate).
- **Live surfacing is deferred-automated (Docker-less sandbox):** `AnalyzerE2ETests` is `Category=RealStack` (hermetic-excluded), so the fixture change is a build+compile gate here. The end-to-end ES read of the new `attributes.StepId`/`NextStepId` records (structural + FW-02 + redundancy) requires a rebuilt live stack and is logged for a future Docker-up run (precedent phases 68/73/74/75). The live ANL-03 EntryId→stepId resolver is best-effort for a fully-dropped intermediate producer; the engine logic (what ANL-03 requires) is proven precisely by the hermetic facts.

## Verification
- `dotnet build SK_P.sln -c Release`: **0 Warning, 0 Error**; `dotnet build tests/BaseApi.Tests -c Debug`: 0/0.
- `BaseApi.Tests.exe --filter-namespace BaseApi.Tests.Observability.Analysis`: **37/37 green** (25 pre-existing PassFailEngine + ValueChain facts UNCHANGED + 6 new stepId facts + ... = 37 total, incl. `PassFailEngineValueChainFacts` 15/15).
- `rg "HopLabels" PassFailEngine.cs`: **0 matches** (clean break).
- Full hermetic suite (`--filter-not-trait Category=RealStack`): **843 total, 571 passed, 272 failed** — the 272 are the exact pre-existing infra-dependent (Postgres/RabbitMQ/Redis-connection) baseline for the Docker-less sandbox (0 analyzer facts among them); **zero NEW failures in changed scope**.

---
*Phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec*
*Completed: 2026-07-16*

## Self-Check: PASSED

All 5 modified files exist on disk; all three task commits (0e1e92b, 16fde90, a003609) present in git history; `HopLabels` grep-clean in `PassFailEngine.cs`; 37/37 Analysis facts green; Release 0-warning.
