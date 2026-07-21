---
phase: 83-orchestrator-ha-failover-proof
plan: 02
subsystem: testing
tags: [ha-07, failover, elasticsearch, xunit-v3, mtp, realstack, verdict, kubernetes]

# Dependency graph
requires:
  - phase: 83-01
    provides: "HaFireBucketScorer.Score + SendEvidenceRecord/HaFireVerdict types; EsIndexNames.RoleFieldPath const"
  - phase: 66
    provides: "AnalyzerE2ETests ES-fetch/window/drain/write-then-assert plumbing (cloned); EsIndexNames field consts; ElasticsearchTestClient.SearchAllHits"
provides:
  - "HaFailoverAnalyzerE2ETests.cs — the live RealStack HA-07 verdict fact Ha_Failover_Window_Yields_Pass"
  - "Two static ES query builders: BuildSendEvidenceBody (exists StepId+CorrelationId) + BuildRoleFlipBody (term role=leader gt KILL_UTC)"
  - "phase-83-ha.json report (string Verdict) consumable by Resolve-AnalyzerExitCode → 0/1/2"
affects: [83-03, 83-04]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Clone the analyzer ES/window/drain/write-then-assert scaffold, swap only the scoring (feed the pure HaFireBucketScorer)"
    - "Two-ES-stream verdict: exists-StepId leader-only send oracle + earliest post-kill role=leader role-flip"
    - "Verbatim epoch-ms-string-first @timestamp parser (phase-73 trap)"

key-files:
  created:
    - "tests/BaseApi.Tests/Observability/HaFailoverAnalyzerE2ETests.cs"
  modified: []

key-decisions:
  - "roleFlipUtc computed test-side as earliest post-kill role=leader @timestamp (single source for claims #4+#5), fed to the pure scorer"
  - "Report SendCorrIdCount/BucketCount computed report-side (earliest-per-corr → 30s bucket via HaFireBucketScorer.Bucket30s) purely for a self-explaining red report; verdict is the scorer's"
  - "Role-flip stream also poll-to-stable; a genuinely empty stream exhausts the budget and yields roleFlipUtc=null → scorer fail-closes (no vacuous Pass)"

patterns-established:
  - "Pattern: RealStack verdict fact writes the string-Verdict report BEFORE asserting so the harness reads the authoritative 0/1/2 even on red"

requirements-completed: [HA-07]

# Metrics
duration: 18min
completed: 2026-07-19
---

# Phase 83 Plan 02: HA Failover Live Verdict Fact Summary

**`HaFailoverAnalyzerE2ETests.Ha_Failover_Window_Yields_Pass` — the live RealStack HA-07 verdict that fetches two ES evidence streams (exists-StepId leader sends + earliest post-kill role=leader flip) over the harness-pinned failover window, feeds them to the pure `HaFireBucketScorer`, and writes a string-`Verdict` `phase-83-ha.json` before asserting (exit 0/1/2).**

## Performance

- **Duration:** ~18 min
- **Completed:** 2026-07-19
- **Tasks:** 2
- **Files modified:** 1 (created)

## Accomplishments
- Cloned `AnalyzerE2ETests`' live-verified ES-fetch/window/drain/report plumbing byte-for-faithful and swapped only the scoring — the fact feeds two evidence streams into the Plan-01 `HaFireBucketScorer.Score` and writes a `Resolve-AnalyzerExitCode`-mappable report.
- Verbatim `TryReadTimestamp` epoch-ms-numeric-string-first parser (the phase-73 OTLP→ES `@timestamp` trap), plus the `ScenarioIdPattern` path-traversal guard (T-83-01) and static raw-string query templates interpolating only `EsIndexNames.*` consts + validated `DateTimeOffset:o` bounds (T-83-02).
- Send-evidence query filters `exists attributes.StepId` + `exists attributes.CorrelationId` (the clean leader-only send oracle; follower-skip logs carry no StepId and fall out); role-flip query is a `term attributes.role=leader` + `range gt KILL_UTC`, earliest = the survivor's follower→leader flip (claims #4+#5).
- Write-then-assert: the `phase-83-ha.json` report carries a top-level `[JsonConverter(typeof(JsonStringEnumConverter))] Verdict` (reusing the shared `Verdict` enum) written BEFORE `Assert.True(report.Verdict == Verdict.Pass, ...)`, so the harness reads the authoritative 0/1/2 even on a red run.

## Task Commits

1. **Task 1: ES query builders + hit parsers + env seam** — `2b57934` (test)
2. **Task 2: The verdict fact — fetch → drain → score → write-then-assert** — `74bc39c` (feat)

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/HaFailoverAnalyzerE2ETests.cs` (367 lines) — the `[Category=RealStack]` verdict fact + its static ES query builders (`BuildSendEvidenceBody`, `BuildRoleFlipBody`), verbatim `TryReadTimestamp`, `TryParseUtc`, `TryReadCorr`, `ParseSendRecords`, `PollHitsToStableAsync`, and the `HaFailoverReport` record.

## Decisions Made
- Computed `roleFlipUtc` test-side (earliest post-kill `role=leader` `@timestamp`) and passed it to the pure scorer — the scorer owns claims #4+#5 folding; the fact only supplies the parsed instant.
- Surfaced `SendCorrIdCount`/`BucketCount` in the report by recomputing earliest-per-corr → `HaFireBucketScorer.Bucket30s` (a report-only convenience for a self-explaining red run); the binding verdict remains the scorer's `HaFireVerdict.Verdict`.

## Deviations from Plan

None - plan executed exactly as written. (Two doc-comment rewords were required to satisfy the `! grep -q '.keyword'` acceptance gate — the file must not contain the literal substring `.keyword`; the comments now say "direct keyword paths, never a sub-field suffix". This is a wording adjustment to meet a stated acceptance criterion, not a behavioral deviation.)

## Issues Encountered
- `dotnet test --list-tests` does not surface individual test names through the default terminal logger; confirmed the MTP `--filter-method "*Ha_Failover_Window_Yields_Pass*"` resolves to **exactly one** test by running `BaseApi.Tests.exe --list-tests` directly (per the hermetic-test-command memory) → "Test discovery summary: found 1 test(s)".

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 03 (`Resolve-AnalyzerExitCode` wiring / exit-code lib) and Plan 04 (the `scripts/phase-83-ha-failover.ps1` sequencer + live run) can consume the fact: invoke via `dotnet test ... -- --filter-method "*Ha_Failover_Window_Yields_Pass*"` with the `SCENARIO_ID`/`WINDOW_*_UTC`/`KILL_UTC` env seam pinned, then map `analyzer-reports/phase-83-ha.json`'s string `Verdict` to 0/1/2.
- The RealStack fact is hermetic-excluded (`Category=RealStack`); the live behavioral proof (an actual PASS against a killed-leader window) is Plan 04.

## Self-Check: PASSED

- `tests/BaseApi.Tests/Observability/HaFailoverAnalyzerE2ETests.cs` — FOUND
- `.planning/phases/83-orchestrator-ha-failover-proof/83-02-SUMMARY.md` — FOUND
- Commit `2b57934` (Task 1) — FOUND
- Commit `74bc39c` (Task 2) — FOUND

---
*Phase: 83-orchestrator-ha-failover-proof*
*Completed: 2026-07-19*
