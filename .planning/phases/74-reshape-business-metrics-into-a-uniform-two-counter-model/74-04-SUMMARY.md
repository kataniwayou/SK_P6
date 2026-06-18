---
phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
plan: 04
subsystem: testing
tags: [metrics, observability, prometheus, analyzer, passfailengine, refactor, realstack]

# Dependency graph
requires:
  - phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
    provides: "Plans 74-01/02/03 renamed the production instruments to the uniform {service}_messages_consumed/_sent pair (+ keeper_l2_probe), removed ResultDeduped/DispatchDeduped/ReinjectDropped, and dropped the processor outcome label"
provides:
  - "PromCounterSnapshot + PassFailEngine rebound to the new *_messages_consumed/*_messages_sent field names (D-12/D-13); outcome corroboration WARNING path deleted"
  - "AnalyzerE2ETests RealStack PromQL retargeted to the uniform metric names + 4-counter CounterSet/BuildSnapshot"
  - "Metric facts migrated in-place with 3 removed-counter absence asserts (orchestrator_result_deduped, processor_dispatch_deduped, keeper_reinject_dropped) + success-path keeper_messages_sent assert (D-14)"
  - "MetricsRoundTripE2ETests cardinality guard INVERTED — workflowId now REQUIRED; processor outcome label asserted absent; camelCase processorId queries"
affects: [live-stack-close-gate, prometheus-dashboards, future-observability-phases]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Reference-identity MeterListener scoping (ReferenceEquals on instrument/meter) for hermetic per-instance metric capture under parallel test classes"
    - "Absence-of-series assertion via InstrumentPublished name capture scoped to the test's exact meter"
    - "Compile-time absence proof: a removed counter has no PromCounterSnapshot field, so the analyzer file compiling IS the absence proof"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
    - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
    - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs
    - tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs
    - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs
    - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs
    - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
    - src/Keeper/Program.cs

key-decisions:
  - "PromCounterSnapshot delta fields renamed to OrchestratorMessagesSent/Consumed + ProcessorMessagesSent/Consumed; the three removed-counter deltas + the outcome-keyed NonCompletedOutcomes dictionary deleted (D-13)"
  - "PassFailEngine close-gate Expected = COMPLETE × 9 math survives by repointing to processor_messages_sent total (D-12: all-complete fixture ⇒ total == completed)"
  - "Three removed counters proven absent by no-series MeterListener asserts (BreakerMetricsFacts for the two dedup counters; Reinject/OrchestratorReinjectConsumerFacts for the keeper drop) + a compile-time field-absence fact in PassFailEngineFacts (D-14)"
  - "MetricsRoundTripE2ETests AssertBusinessLabels inverted: workflowId REQUIRED (was forbidden), outcome label forbidden (was required); queries use camelCase processorId"
  - "Live-stack PassFailEngine close gate recorded as deferred-automated (SPEC AC #8) — sandbox lacks Docker"

patterns-established:
  - "Scope every per-instance MeterListener by instrument/meter reference identity, never by meter/instrument NAME — name filters double-count or leak across xUnit v3 parallel test classes"
  - "Removed-counter absence is asserted two ways: compile-time (no analyzer field) and runtime (no published series on the test's own meter)"

requirements-completed: [1, 2, 3, 5, 6]

# Metrics
duration: 19min
completed: 2026-06-18
---

# Phase 74 Plan 04: Analyzer Rebind + Metric-Facts Migration + Final Gate Summary

**Rebound the live-stack analyzer (PromCounterSnapshot/PassFailEngine/AnalyzerE2ETests) to the uniform `*_messages_consumed`/`*_messages_sent` names, deleted the dropped-`outcome` corroboration WARNING path, migrated 10 metric test files in-place with explicit absence asserts for the three removed counters, inverted the MetricsRoundTrip cardinality guard (workflowId now REQUIRED), and proved the whole new model green hermetically (668/668) at 0-warning Debug+Release.**

## Performance

- **Duration:** ~19 min
- **Started:** 2026-06-18T06:41:47Z
- **Completed:** 2026-06-18T07:01:10Z
- **Tasks:** 3
- **Files modified:** 12 (11 test + 1 src comment fix)

## Accomplishments
- `PromCounterSnapshot` reduced to the four uniform deltas; `PassFailEngine` repointed (`TriggerCountFrom`, `promImpliedRuns`, `expectedResultConsumed`, `spawnReconExcess`) and the non-completed `outcome` WARNING block deleted (D-12/D-13).
- The three removed counters (`orchestrator_result_deduped`, `processor_dispatch_deduped`, `keeper_reinject_dropped`) carry explicit no-series absence asserts; the keeper drop path additionally proves no `keeper_messages_sent`, and the success paths prove exactly one `keeper_messages_sent` (D-14).
- `MetricsRoundTripE2ETests` PromQL renamed to `*_messages_*` with camelCase `processorId`; `AssertBusinessLabels` INVERTED so `workflowId` is required and `outcome` is forbidden; the `outcome` PromQL loop dropped from `AnalyzerE2ETests`.
- Hermetic metric-facts subset 38/38 and full hermetic suite **668/668** green; 0-warning Debug AND Release; `src/` clean of every removed-counter name and the `outcome` label.

## Task Commits

Each task was committed atomically:

1. **Task 1: Rebind PromCounterSnapshot + PassFailEngine (drop outcome WARNING)** — `43b7460` (refactor)
2. **Task 2: Migrate metric fact tests + 3 absence asserts + invert workflowId guard** — `a7dc9b4` (test)
3. **Task 3: Final hermetic gate + 0-warning build (incl. listener-scope + src-comment fixes)** — `6b7b6d3` (fix)

**Plan metadata:** final docs commit (STATE/ROADMAP + this SUMMARY).

## Files Created/Modified
- `PromCounterSnapshot.cs` — renamed surviving deltas to the four uniform names; deleted the 3 removed-counter deltas + the `NonCompletedOutcomes` dictionary.
- `PassFailEngine.cs` — repointed all field reads; deleted the `outcome` corroboration WARNING block; doc-comment tokens updated.
- `AnalyzerReport.cs` — doc-comment field-name references updated; `ExpectedResultConsumed` field retained.
- `PassFailEngineFacts.cs` / `PassFailEngineValueChainFacts.cs` — snapshot factories repointed; dropped the dedup/outcome fixtures; added a compile-time removed-counter field-absence fact.
- `BreakerMetricsFacts.cs` — rewritten as no-series absence asserts for both dedup counters + the new camelCase label-key probe.
- `ReinjectConsumerFacts.cs` / `OrchestratorReinjectConsumerFacts.cs` — drop-path `keeper_reinject_dropped` absence + no `keeper_messages_sent`; success-path `keeper_messages_sent == 1`; listeners scoped by reference identity.
- `AnalyzerE2ETests.cs` (RealStack) — `CounterSet`/`ReadCounterSetAsync`/`BuildSnapshot` rebuilt on the four uniform totals; `outcome` loop + dormant-delta helpers removed.
- `MetricsRoundTripE2ETests.cs` (RealStack) — uniform `*_messages_*` PromQL, camelCase `processorId`, INVERTED cardinality guard (workflowId required), `outcome` assert dropped.
- `SC2RecoveryPathsE2ETests.cs` (RealStack) — doc/comment narrative updated for the removed drop counter.
- `src/Keeper/Program.cs` — D-07 comment rephrased to the uniform counters (no removed-counter name lingers in `src/`).

## Decisions Made
- Chose descriptive analyzer field identifiers (`OrchestratorMessagesSentDelta`, etc.) matching the production metric names, and updated ALL references in `PassFailEngine` + the two fact files for consistency (the plan left field naming to discretion).
- Proved removed-counter absence with BOTH a compile-time signal (no snapshot field) and a runtime signal (no published series), giving D-14 a belt-and-suspenders guarantee.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Stale `keeper_reinject_dropped` reference in a production comment**
- **Found during:** Task 3 (final cross-cutting `src/` absence grep)
- **Issue:** `src/Keeper/Program.cs:86` still named the removed `keeper_reinject_dropped` counter in a D-07 handoff comment, tripping the acceptance grep `! grep -rnE 'keeper_reinject_dropped' src/`.
- **Fix:** Rephrased the comment to describe the uniform `keeper_messages_*` counters + the `keeper_l2_probe` heartbeat. No behavior change.
- **Files modified:** src/Keeper/Program.cs
- **Verification:** `grep -rnE 'orchestrator_result_deduped|processor_dispatch_deduped|keeper_reinject_dropped' src/` returns nothing; `dotnet build src/Keeper -c Debug -warnaserror` 0-warning.
- **Committed in:** `6b7b6d3`

**2. [Rule 1 - Bug] Name-scoped MeterListener double-counted `keeper_messages_sent` under parallel test classes**
- **Found during:** Task 3 (first hermetic metric-facts run)
- **Issue:** My Task-2 success-path assert filtered the listener by instrument NAME (`keeper_messages_sent`); xUnit v3 runs test classes in parallel, so the concurrently-running `OrchestratorReinjectConsumerFacts` increment leaked in and `ReinjectConsumerFacts.Reinject_present...` saw 2 measurements instead of 1.
- **Fix:** Scoped both consumer-fact listeners to THIS metrics instance's exact instrument/meter via `ReferenceEquals` (mirroring `BreakerMetricsFacts`), for both the sent-counter capture and the `keeper_reinject_dropped` absence capture.
- **Files modified:** tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs, tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs
- **Verification:** metric-facts subset 38/38, full hermetic 668/668 green.
- **Committed in:** `6b7b6d3`

---

**Total deviations:** 2 auto-fixed (both Rule 1 bugs surfaced by the acceptance gate).
**Impact on plan:** Both essential to pass the 0-warning + GREEN gate. No scope creep — the listener fix corrects a test-isolation bug in this plan's own Task-2 additions; the comment fix completes the cross-cutting absence requirement.

## Issues Encountered
- The full hermetic run logs RabbitMQ connection failures and FluentValidation exceptions — these are expected negative-path test logging and broker-teardown noise in the broker-less sandbox, NOT test failures (summary: 668/668 passed, 0 failed).
- `RecoveryDeadLetterFacts.cs` (in the plan's files_modified) needed NO source change: its `.AddSingleton<KeeperMetrics>()` already satisfies the new `RecoveryConsumerBase` 4-arg ctor and the direct `ReinjectConsumer` construction passes `RecoveryTestKit.Metrics()`. Verified by the green build + run; left untouched.
- `DispatchTestKit.cs` confirmed a false positive (its `outcome` is a `DataResult` param, not a metric label) — not touched, per the plan's critical reminder.

## Known Stubs
None. Every metric fact asserts against real instrument behavior or compile-time field shape; no placeholder/empty-data paths introduced.

## Deferred Items
- **Live-stack `PassFailEngine` close gate (SPEC AC #8):** deferred-automated. The sandbox lacks Docker, so the live net-zero sweep over the real Prometheus/Elasticsearch stack cannot run here. The hermetic metric-facts subset + the compiling (but RealStack-excluded) `AnalyzerE2ETests`/`MetricsRoundTripE2ETests` cover the contract; the live close gate runs on the Docker stack.

## User Setup Required
None - no external service configuration required.

## TDD Gate Compliance
This plan is `type: execute` (not `type: tdd`); no RED/GREEN gate sequence applies. Tasks were committed as refactor → test → fix.

## Next Phase Readiness
- Phase 74 is fully landed across both waves: production instruments (74-01/02/03) and the analyzer + tests (74-04) all reference the uniform two-counter model; the three legacy counters and the processor `outcome` label are gone from `src/` and asserted absent in tests.
- The only outstanding item is the deferred-automated live-stack close gate (Docker required), tracked above.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs (MessagesSent fields, no removed deltas)
- FOUND: tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs (repointed, no outcome WARNING)
- FOUND: tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs (inverted workflowId guard)
- FOUND: src/Keeper/Program.cs (uniform-counter comment)
- FOUND commit 43b7460 (Task 1)
- FOUND commit a7dc9b4 (Task 2)
- FOUND commit 6b7b6d3 (Task 3)

---
*Phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model*
*Completed: 2026-06-18*
