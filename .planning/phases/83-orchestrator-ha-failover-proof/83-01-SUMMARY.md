---
phase: 83-orchestrator-ha-failover-proof
plan: 01
subsystem: testing
tags: [ha-failover, leader-election, elasticsearch, verdict-scorer, hermetic-facts, tdd, otel]

# Dependency graph
requires:
  - phase: 82-orchestrator-ha-leader-election
    provides: "OrchestratorRoleLogEnricher stamping attributes.role, WorkflowFireJob per-fire correlationId + leader gate — the two ES evidence streams this scorer folds"
provides:
  - "HaFireBucketScorer — pure static scorer folding the five HA-07 claims to a Verdict (Pass/Fail/Inconclusive)"
  - "SendEvidenceRecord struct + HaFireVerdict result record (the Plan 02 contract seam)"
  - "HaFireBucketScorerFacts — six hermetic synthetic-record facts pinning the fail-closed fold"
  - "EsIndexNames.RoleFieldPath = attributes.role — the direct keyword const for the Plan 02 role-flip term query"
affects: [83-02 (HaFailoverAnalyzerE2ETests live verdict consumes Score + RoleFieldPath), 83-03 (exit-code mapping over the Verdict), 83-04 (live k8s failover run)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Pure static classifier + hermetic synthetic-record facts pair (mirrors StructuralCohort/StructuralCohortFacts) so the load-bearing decision logic is Docker-less provable before any live stack"
    - "Fail-closed anti-vacuous fold: observability gaps -> Inconclusive, positive findings -> Fail, never a vacuous Pass"
    - "EsIndexNames single-const edit convention: direct keyword path, NO .keyword sub-field"

key-files:
  created:
    - tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorer.cs
    - tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorerFacts.cs
  modified:
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs

key-decisions:
  - "Election-window walk replaces the verbatim RESEARCH Code Example 4 boundary arithmetic (which makes notBackfilled tautologically true and the required backfill-Fail fold unreachable)"
  - "Fail-closed fold order implemented exactly: zero-sends -> Inconclusive; duplicate -> Fail; no role flip -> Fail; negative recovery -> Inconclusive; recovery>30s -> Fail; backfilled gap -> Fail; no gap observed -> Inconclusive; else Pass"

patterns-established:
  - "Pure scorer isolated in the shared Observability.Analysis namespace so the live fixture and hermetic facts exercise the identical Score(...) entry point"
  - "Verdict enum reused (not redeclared) so the report JSON string names map through Resolve-AnalyzerExitCode to 0/1/2"

requirements-completed: [HA-07]

# Metrics
duration: 18min
completed: 2026-07-18
---

# Phase 83 Plan 01: HA-07 Fire-Bucket Scorer + Role Const Summary

**Pure 30s-bucket + gap + recovery scorer (HaFireBucketScorer) folding the five HA-07 claims to a fail-closed Verdict, pinned by six hermetic synthetic-record facts, plus the EsIndexNames.RoleFieldPath keyword const the live role-flip query consumes.**

## Performance

- **Duration:** 18 min
- **Started:** 2026-07-18T21:02:20Z
- **Completed:** 2026-07-18T21:20:15Z
- **Tasks:** 2
- **Files modified:** 3 (2 created, 1 modified)

## Accomplishments
- `HaFireBucketScorer.Score(sends, killUtc, roleFlipUtc)` — the load-bearing decision logic for the whole phase: per-tick send-uniqueness (claims #1+#2), election-gap skip-not-backfilled (claim #3), bounded role-flip recovery (claims #4+#5), folded to one `Verdict`.
- Six hermetic facts green Docker-less: happy-path Pass, split-brain Fail, no-gap Inconclusive, backfilled-gap Fail, recovery-over-30s Fail, zero-sends Inconclusive — pinning the anti-vacuous (T-83-04a) contract that an observability gap is never a false green.
- `EsIndexNames.RoleFieldPath = "attributes.role"` (direct keyword path, no `.keyword`) for Plan 02's role-flip term query.
- 0-warning Debug + Release build; no regression in the sibling analyzer facts (StructuralCohortFacts 3/3, PassFailEngineFacts 30/30 still green).

## Task Commits

Each task was committed atomically:

1. **Task 1: RoleFieldPath keyword const** - `d7c5981` (feat)
2. **Task 2 (RED): failing HaFireBucketScorer facts** - `bad08e1` (test)
3. **Task 2 (GREEN): HaFireBucketScorer fold** - `8cef46c` (feat)

_TDD gate compliance: `test(...)` RED (`bad08e1`) precedes `feat(...)` GREEN (`8cef46c`). No refactor commit needed — the GREEN implementation was already clean._

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorer.cs` - Pure static scorer: `Bucket30s` wall-clock flooring, per-corrId earliest-timestamp bucketing, election-window gap/backfill walk, role-flip recovery bound, fail-closed fold; `SendEvidenceRecord` struct + `HaFireVerdict` result record (reuses `AnalyzerReport.Verdict`).
- `tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorerFacts.cs` - Six `[Fact]`s over synthetic `SendEvidenceRecord[]` on a 30s-aligned base instant; no `RealStack` trait (runs hermetically).
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` - Added `RoleFieldPath = "attributes.role"` const (single-const edit, direct keyword path).

## Wave-0 Mapping Probe (Task 1)

`curl http://localhost:9200/logs-generic.otel-default/_mapping/field/attributes.role` returned
`{".ds-logs-generic.otel-default-2026.07.18-000001":{"mappings":{}}}` — ES is up, but no
`attributes.role` documents have been indexed yet in the live data stream (empty mapping), the same
"field-not-yet-present" state the existing `attributes.Sum` const note documents. **Probe non-blocking:**
the const value `"attributes.role"` is correct regardless per the `EsIndexNames` direct-keyword-path
convention (RESEARCH A5); Plan 04's live failover run (which brings up orchestrator replicas that stamp
`attributes.role` on every log) confirms the `keyword` mapping and that the Plan-02 `term` query hits.

## Decisions Made
- **Election-window gap walk (see Deviation 1):** the gap/backfill detection walks the election window `[killBucket, recoveryBucket]` forward from the last pre-kill fire, collecting contiguous empty ticks and flagging a fire on a post-gap election tick as a backfill. This preserves the RESEARCH Code Example 4 *intent* (contiguous empty gap between last-pre-kill and first-post-recovery, overlapping `[KILL_UTC, roleFlipUtc]`) while keeping the required backfill-Fail path reachable.
- **Fold order exact per plan:** zero-sends → Inconclusive; `!zeroDuplicate` → Fail; `!roleFlipVisible` → Fail; `recovery < 0` → Inconclusive; `recovery > 30s` → Fail; `gapObserved && !notBackfilled` → Fail; `!gapObserved` → Inconclusive; else Pass.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Election-window walk instead of verbatim Code Example 4 boundary arithmetic**
- **Found during:** Task 2 (GREEN scorer design)
- **Issue:** The plan instructs implementing RESEARCH Code Example 4 "VERBATIM": `lastPreKill = fireBuckets.Where(b => b < killBucket).Max()`, `firstPostRec = fireBuckets.Where(b => b >= killBucket).Min()`, then `notBackfilled = gapBoundaries.All(b => !byBucket.ContainsKey(b))`. But no fire bucket can ever lie strictly between the two nearest fire buckets around `killBucket`, so `notBackfilled` is **tautologically true** under that arithmetic — which makes the plan's REQUIRED `BackfilledGapTick_IsFail` fact (asserting `NotBackfilled == false` → `Fail`) **unreachable**.
- **Fix:** Implemented Code Example 3 (bucketing) and Code Example 5 (recovery) verbatim, and Code Example 4's *semantics* via an election-window (`[killBucket, recoveryBucket]`) forward walk that (a) collects contiguous empty in-election ticks as the gap (satisfying the anti-vacuous overlap guard by construction), and (b) flags a fire on an election tick that follows an already-skipped gap tick as a backfill. Same intent; backfill-Fail path now genuinely reachable.
- **Files modified:** tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorer.cs
- **Verification:** All six facts green (including `BackfilledGapTick_IsFail` → Fail and `NoGapBucketObserved_IsInconclusive_NotPass` → Inconclusive), 0-warning Debug + Release.
- **Committed in:** `8cef46c` (Task 2 GREEN commit)

**2. [Rule 3 - Blocking] Renamed recovery pattern variable to avoid CS0136 name clash**
- **Found during:** Task 2 (GREEN, first Debug build)
- **Issue:** `recovery is { } r && ...` clashed with the `r` foreach loop variable (`foreach (var r in sends)`) — CS0136 compile error.
- **Fix:** Renamed the pattern variable to `rec` (`recovery is { } rec && rec >= ... && rec <= ...`).
- **Files modified:** tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorer.cs
- **Verification:** Debug build 0-error/0-warning after the rename.
- **Committed in:** `8cef46c` (Task 2 GREEN commit)

---

**Total deviations:** 2 auto-fixed (1 bug — logic-reachability correction, 1 blocking — compile fix)
**Impact on plan:** Both necessary for correctness. Deviation 1 keeps the scorer's fold faithful to the plan's fold order AND makes every required fact reachable — without it the plan's own acceptance (six green facts) is unsatisfiable. No scope creep; the three deliverable files and the fold contract are exactly as specified.

## Issues Encountered
- The broad `--filter-class "*Facts*"` regression sweep pulls in RabbitMQ/Postgres-dependent Integration facts that fail in the Docker-less sandbox (the documented baseline — memory [[hermetic-test-command]]). Resolved by running the relevant hermetic analyzer facts by explicit class filter (`HaFireBucketScorerFacts`, `StructuralCohortFacts`, `PassFailEngineFacts`) — all green, confirming zero new hermetic regressions from the additive change.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The scorer contract (`SendEvidenceRecord`, `HaFireVerdict`, `HaFireBucketScorer.Score`) and `EsIndexNames.RoleFieldPath` are ready for **Plan 02** (`HaFailoverAnalyzerE2ETests`): it parses `exists attributes.StepId` hits into `SendEvidenceRecord[]`, queries `RoleFieldPath` for the earliest post-kill `role=leader` record, feeds both into `Score(...)`, and serializes the `Verdict` to a `phase-83-ha.json` report.
- No blockers. The live `attributes.role` keyword mapping is confirmed at Plan 04 (Wave-0 probe deferred, non-blocking).

## TDD Gate Compliance
- **RED gate:** `bad08e1` `test(83-01): add failing HaFireBucketScorer facts` (scorer types absent → compile failure).
- **GREEN gate:** `8cef46c` `feat(83-01): implement HaFireBucketScorer 30s-bucket + gap + recovery fold` (all six facts green).
- **REFACTOR gate:** none needed (GREEN implementation clean, 0-warning).

## Self-Check: PASSED
- All 3 created/modified files present on disk.
- All 3 task commits present in git history (`d7c5981`, `bad08e1`, `8cef46c`).

---
*Phase: 83-orchestrator-ha-failover-proof*
*Completed: 2026-07-18*
