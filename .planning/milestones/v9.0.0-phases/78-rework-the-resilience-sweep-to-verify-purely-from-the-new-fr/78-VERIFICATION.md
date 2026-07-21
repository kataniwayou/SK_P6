---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
verified: 2026-07-17T12:00:00Z
status: passed
score: 12/12 must-haves verified
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 11/12
  gaps_closed:
    - "All 7 live scenarios (TEST-01..07) reproduce their committed-HEAD baseline verdict (all PASS), reconstructed from framework ES logs alone (D-04)"
  gaps_remaining: []
  regressions: []
---

# Phase 78: Rework the resilience sweep to verify purely from the new framework ES logs Verification Report

**Phase Goal:** Adapt the analyzer to the Phase-77 consistent-logging model so the resilience verdict reconstructs PURELY AND CORRECTLY from framework ES logs — D-01 (entry-marker self-exclusion via ABSENT ExecutionId), D-02 (keeper reinject `must_not` discrimination), D-03 (drop the concrete value oracle), D-04 (re-run the SourceHash reseed + 7-scenario live gate and confirm 7/7 by ES logs). Keep Prometheus as the secondary collector-blind axis.
**Verified:** 2026-07-17T12:00:00Z
**Status:** passed
**Re-verification:** Yes — after gap closure (78-05 fix + 78-06 live re-gate)

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | The entry marker is excluded ONLY by the `exists attributes.ExecutionId` ES query filter — no engine-level `IsEntryMarker` predicate remains (D-01) | ✓ VERIFIED (regression check) | `grep -n "exists.*ExecutionId\|ExecutionIdFieldPath" AnalyzerE2ETests.cs` still shows the two `exists ExecutionIdFieldPath` clauses at :286/:316, unchanged by the 78-05 fix; `grep -c "IsEntryMarker" PassFailEngine.cs` = 0 (unchanged) |
| 2 | Keeper reinject records never enter the structural completeness cohort — structural query forbids `attributes.ReinjectOutcome` (D-02) | ✓ VERIFIED (regression check) | `grep -n "must_not\|ReinjectOutcome" AnalyzerE2ETests.cs` shows the `must_not exists ReinjectOutcomeFieldPath` clause intact at :290-291 and the symmetric `BuildKeeperOutcomeSearchBody` clause at :383, unchanged by 78-05 |
| 3 | The hermetic analyzer facts compile and run green, including new D-04 regression facts (D-01/D-02/D-03 truths + StructuralCohortFacts) | ✓ VERIFIED | Live re-run this session: `BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-namespace "*Observability.Analysis*"` → total 32, failed 0, succeeded 32 (prior 29 + 3 new `StructuralCohortFacts`), matches 78-05-SUMMARY.md claim exactly |
| 4 | The value-oracle ES query and value-parse path are gone (D-03) | ✓ VERIFIED (regression check, unchanged since prior verification) | Prior grep evidence still holds; 78-05/78-06 touched only the structural cohort discrimination, not the value-oracle deletion |
| 5 | The engine's value-CHAIN machinery is gone (D-03) | ✓ VERIFIED (regression check, unchanged) | Unchanged by 78-05/78-06 |
| 6 | The live structural cohort counts each genuine hop execution exactly ONCE by selecting only the canonical `"hop executed"` processor consume record — multi-record-per-hop framework logs (`result sent`, `fan-out`, `terminal reached`, keeper reinject) no longer inflate the observed stepId list | ✓ VERIFIED | `StructuralCohort.cs` defines `ConsumeRecordPrefix = "hop executed"` (:42) and `TerminalRecordPrefix = "terminal reached"` (:49); `Classify` routes only `"hop executed"` to observed, `"terminal reached"` to proven, everything else ignored; `AnalyzerE2ETests.cs:515` calls `StructuralCohort.Classify(structuralRecords)` |
| 7 | A no-fault run (each stepId's canonical record fires once) yields zero illegitimate duplicates → PASS | ✓ VERIFIED | `StructuralCohortFacts.NoFault_MultiRecordPerHop_Collapses_ToOneObservedHop_Pass` feeds 30 records (10 stepIds × 3 record kinds) → asserts 10 observed hops, `HasIllegitimateDuplicate == false`, `Verdict.Pass`; ran green live this session |
| 8 | A genuinely twice-executed step (TWO `"hop executed"` records for one `{executionId, stepId}`) STILL trips Duplicates — the fix does NOT collapse to presence-only | ✓ VERIFIED | `StructuralCohortFacts.GenuineRedelivery_TwoConsumeRecords_ForOneStep_TripsDuplicate` asserts the duplicate survives (`observed.Count(s => s == "hop-c") == 2`), `HasIllegitimateDuplicate == true`, `Verdict.Fail`; ran green live this session; `grep -c "Distinct\|GroupBy"` on `StructuralCohort.cs` = 0 (no naive de-dup on the observed path) |
| 9 | A hermetic fact reproduces the multi-record-per-hop live shape and pins the collapse — the bug class invisible under the old one-record-per-stepId `RunTrace.FromStepIds` shape is now visible hermetically | ✓ VERIFIED | `StructuralCohortFacts.cs` (172 lines, 3 facts) reproduces the exact 78-04 evidence shape (30 records/10 stepIds/3 per hop) via synthetic `FrameworkLogRecord`s, in the shared `BaseApi.Tests.Observability.Analysis` namespace picked up by the hermetic filter |
| 10 | The hermetic analyzer suite is green (0 new failures) and the solution builds 0-warning Debug+Release | ✓ VERIFIED | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` this session → Build succeeded, 0 Warning(s), 0 Error(s); analyzer subset 32/32 green |
| 11 | The SourceHash reseed runs FIRST before the sweep, and all 7 scenarios (TEST-01..07) reproduce their committed-HEAD baseline verdict (all PASS), reconstructed from framework ES logs ALONE (D-04) | ✓ VERIFIED | `78-06-SUMMARY.md`: reseed-first runbook executed (graph-DELETE → seed → 204 confirmed clean, no heal-wait trap this run), live 7-scenario sweep on Docker (~2h), all 7 scenarios Pass with `Missing==0, Duplicates==0` (TEST-01 19/19, TEST-02 17/17, TEST-03 18/18, TEST-04 17/17, TEST-05 23/23, TEST-06 14/14, TEST-07 25/25), gated against the genuine `da91d32` all-PASS baseline (7/7) |
| 12 | The tracked `analyzer-reports/phase-68-summary.json` at HEAD reflects the 7/7 PASS live re-gate (not the 78-04 clobbered failed-finding state) | ✓ VERIFIED | Read the tracked file directly this session: 7 entries, all `"verdict": "Pass"`, `startedRuns`/`completeRuns` match 78-06-SUMMARY.md's table exactly (19/19, 17/17, 18/18, 17/17, 23/23, 14/14, 25/25); `git status --short analyzer-reports/` clean (no working-tree drift); `git log` shows commit `d8d836b` "docs(78-06): complete D-04 live re-gate — 7/7 PASS, D-04 CLOSED" as the current tip for this file, superseding the `5096dcd` clobber |

**Score:** 12/12 truths verified.

### Deferred Items

None.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `tests/BaseApi.Tests/Observability/Analysis/StructuralCohort.cs` | Pure canonical-record classifier + `FrameworkLogRecord` type, shared by fixture + hermetic facts | ✓ VERIFIED | Exists (10,129 bytes, mtime 2026-07-17 09:13); `ConsumeRecordPrefix`/`TerminalRecordPrefix` constants present; `Classify` routes correctly; no `Distinct`/`GroupBy` on the observed path |
| `tests/BaseApi.Tests/Observability/Analysis/StructuralCohortFacts.cs` | Hermetic regression facts pinning collapse→Pass and genuine-redelivery→Fail | ✓ VERIFIED | Exists (9,546 bytes, mtime 2026-07-17 09:16); 3 facts, all reference `HasIllegitimateDuplicate`/`Duplicates`/`Verdict`; ran green (3/3) this session as part of the 32/32 namespace run |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | `BuildRunTraces` delegates to `StructuralCohort.Classify`; D-01/D-02 filters unchanged | ✓ VERIFIED | `StructuralCohort.Classify(structuralRecords)` called at :515; `exists ExecutionIdFieldPath` (D-01) and `must_not exists ReinjectOutcomeFieldPath` (D-02) both present and unchanged |
| `analyzer-reports/phase-68-summary.json` (tracked, HEAD) | 7-scenario roll-up reproducing the all-PASS baseline | ✓ VERIFIED | 7/7 `"verdict": "Pass"`; matches 78-06-SUMMARY.md table; clean git status (no drift) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| `AnalyzerE2ETests.BuildRunTraces` | `StructuralCohort.Classify` | shared classifier call | ✓ WIRED | `AnalyzerE2ETests.cs:515` `var cohort = StructuralCohort.Classify(structuralRecords);` |
| `AnalyzerE2ETests.BuildStepSearchBody` | `EsIndexNames.ReinjectOutcomeFieldPath` | `must_not exists` clause (D-02) | ✓ WIRED (regression-checked) | Unchanged since prior verification, `AnalyzerE2ETests.cs:290-291` |
| `AnalyzerE2ETests.BuildStepSearchBody` | `EsIndexNames.ExecutionIdFieldPath` | `exists` clause (D-01) | ✓ WIRED (regression-checked) | Unchanged since prior verification, `AnalyzerE2ETests.cs:286` |
| `StructuralCohort.Classify` | `RunTrace.FromStepIds` | observed `stepIdsByInstance` per `(corr,exec)` | ✓ WIRED | `StructuralCohortFacts.cs` calls `RunTrace.FromStepIds(Corr, Exec, observed)` directly against `Classify`'s output in all 3 facts |
| `scripts/phase-68-sweep.ps1` | `scripts/phase-67-harness.ps1` | per-scenario run-all no-fail-fast | ✓ WIRED (outcome confirmed) | 78-06-SUMMARY.md: sweep executed across all 7 scenarios, clean 7/7 PASS this time (vs the 78-04 mechanical-only wiring) |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|---------------|--------|---------------------|--------|
| `analyzer-reports/{id}.json` (fresh Release reports TEST-01..07, 78-06 run) | `Verdict`/`StartedRuns`/`CompleteRuns`/`Duplicates` | Live ES query via `BuildStepSearchBody` → `StructuralCohort.Classify` against the Docker compose stack, post-78-05 fix | Yes — real, non-fabricated live data (78-06-SUMMARY.md: all mtime 2026-07-17 09:45..10:47, one report per scenario, no Debug shadow) | ✓ FLOWING |
| `analyzer-reports/phase-68-summary.json` (tracked, HEAD) | roll-up of the 7 `{id}.json` | `phase-68-sweep.ps1` tabulation | Yes — 7/7 Pass, verified directly against the file content this session | ✓ FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Hermetic analyzer facts (Observability.Analysis namespace) run green, including new D-04 regression facts | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-namespace "*Observability.Analysis*"` (run live this session) | total 32, succeeded 32, failed 0, 1s 117ms | ✓ PASS |
| Debug build 0-warning | `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` (run live this session) | Build succeeded, 0 Warning(s), 0 Error(s) | ✓ PASS |
| Commits for 78-05/78-06 exist and match claimed content | `git show --stat a1068ee / e470428 / d8d836b` | All 3 found; `a1068ee` = StructuralCohort classifier, `e470428` = regression facts, `d8d836b` = D-04 live re-gate + roll-up restore | ✓ PASS |
| Tracked baseline file is 7/7 Pass at HEAD, no working-tree drift | `cat analyzer-reports/phase-68-summary.json`; `git status --short analyzer-reports/`; `git log --oneline -- analyzer-reports/phase-68-summary.json` | 7/7 `"verdict": "Pass"`; clean status; `d8d836b` is the current tip, correcting the `5096dcd` clobber | ✓ PASS |
| No `Distinct`/`GroupBy` collapse on the observed did-run path (forbidden naive fix rejected) | `grep -c "Distinct\|GroupBy" StructuralCohort.cs` | exit 1 (no matches) | ✓ PASS |

Step 7b note: the D-04 live re-run itself was NOT re-executed by this verification (per explicit instruction — ~3.5h + live Docker stack). `78-06-SUMMARY.md` and the tracked `analyzer-reports/phase-68-summary.json` are treated as authoritative live evidence. Both were cross-checked for internal consistency this session: the tracked file's per-scenario `startedRuns`/`completeRuns` values match the SUMMARY's verdict table exactly (19/19, 17/17, 18/18, 17/17, 23/23, 14/14, 25/25), and `git log`/`git status` confirm the file is genuinely at HEAD with no drift — this is not merely trusting prose, it is byte-level cross-verification of the tracked artifact against the narrative.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|-------------|-------------|--------|----------|
| D-01 | 78-01-PLAN.md | Entry-marker: delete the detector; `exists attributes.ExecutionId` is the sole exclusion | ✓ SATISFIED | Unchanged since prior verification; regression-confirmed this session |
| D-02 | 78-01-PLAN.md | Keeper discrimination: `must_not exists attributes.ReinjectOutcome` on the structural query | ✓ SATISFIED | Unchanged since prior verification; regression-confirmed this session |
| D-03 | 78-02-PLAN.md, 78-03-PLAN.md | Value oracle: full deletion | ✓ SATISFIED | Unchanged since prior verification |
| D-04 | 78-04-PLAN.md (finding), 78-05-PLAN.md (fix), 78-06-PLAN.md (re-gate) | Live-gate acceptance: reseed + 7-scenario sweep reproduces the all-PASS baseline from framework ES logs alone | ✓ SATISFIED — CLOSED | 78-05 fixed the structural over-count via `StructuralCohort` canonical-record selection (hermetically proven, 32/32 green); 78-06 re-ran the live gate and confirmed 7/7 PASS on real Phase-77 data, `Duplicates==0` on every scenario including no-fault TEST-01 (was 19/19 in the 78-04 finding); tracked baseline restored to 7/7 at HEAD |

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| (none) | — | No TODO/FIXME/PLACEHOLDER/stub patterns found in `StructuralCohort.cs`, `StructuralCohortFacts.cs`, or the modified `AnalyzerE2ETests.cs` regions | — | The D-04 fix is a genuine canonical-record selection, not a stub or forced-green shortcut; the explicitly-forbidden naive de-dup was verifiably NOT used (`grep -c "Distinct\|GroupBy"` = 0 on the observed path) |

### Human Verification Required

None. All D-01/D-02/D-03/D-04 evidence is machine-verifiable: static grep against acceptance criteria, live hermetic test re-run (32/32 green this session), live build re-run (0-warning Debug), git commit/history inspection, and byte-level cross-check of the tracked baseline JSON against the 78-06-SUMMARY.md narrative. No visual/UX/subjective judgment is needed.

### Gaps Summary

None. The single gap from the prior verification — D-04's live-gate baseline reproduction — is now CLOSED.

**Regression check on D-01/D-02/D-03:** the 78-05 fix touched only the structural cohort's record-kind discrimination inside `BuildRunTraces`; it did not modify the D-01 `exists ExecutionId` or D-02 `must_not ReinjectOutcome` query filters, which were re-confirmed unchanged by direct grep this session (line numbers identical to the prior verification report). The value-oracle deletion (D-03) was untouched by 78-05/78-06 (those plans only added the `StructuralCohort`/`StructuralCohortFacts` files and re-ran the live gate).

**D-04 closure evidence, independently cross-checked (not merely trusted from SUMMARY prose):**
1. `StructuralCohort.cs` exists, defines the canonical `"hop executed"` prefix as the sole observed-hop discriminator, routes `"terminal reached"` to proven, and contains no `Distinct`/`GroupBy` collapse on the observed path (the explicitly-forbidden naive fix was verifiably rejected).
2. `StructuralCohortFacts.cs` contains 3 facts that (a) reproduce the exact 78-04 live evidence shape (30 records / 10 stepIds / 3 per hop) and assert collapse→Pass, and (b) assert a genuine second `"hop executed"` record for one step still trips `HasIllegitimateDuplicate`→Fail — proving the fix does not blind duplicate detection.
3. Live re-run this session: `dotnet build -c Debug` succeeds 0-warning/0-error; the full `*Observability.Analysis*` hermetic subset runs 32/32 green (matching the SUMMARY's claimed count exactly).
4. `AnalyzerE2ETests.cs:515` genuinely delegates to `StructuralCohort.Classify`, confirming the live fixture and the hermetic facts share the identical code path (the seam the 78-04 gap lacked).
5. Commits `a1068ee`, `e470428` (78-05) and `d8d836b` (78-06) all exist in git history with content matching their claimed diffs.
6. The tracked `analyzer-reports/phase-68-summary.json` at HEAD is 7/7 `"verdict": "Pass"` with `startedRuns`/`completeRuns` values matching 78-06-SUMMARY.md's table exactly, and `git status` confirms no working-tree drift — the file genuinely reflects the post-fix live re-gate, correcting the `5096dcd` 78-04-clobbered state.

The phase's terminal proof now holds: D-01/D-02/D-03 shipped correctly and remain regression-clean, and D-04's live re-gate confirms the resilience verdict reconstructs PURELY AND CORRECTLY from framework ES logs alone (structural canonical-record completeness + ANL-03 redundancy + the Prometheus metric gate, value-oracle axis fully dark) on real Phase-77 data.

---

*Verified: 2026-07-17T12:00:00Z*
*Verifier: Claude (gsd-verifier)*
