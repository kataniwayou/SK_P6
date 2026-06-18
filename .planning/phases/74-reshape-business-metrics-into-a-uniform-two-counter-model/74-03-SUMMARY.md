---
phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
plan: 03
subsystem: infra
tags: [observability, metrics, opentelemetry, prometheus, keeper, recovery, imeterfactory, counter]

# Dependency graph
requires:
  - phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
    provides: "Plans 74-01 (Orchestrator) + 74-02 (Processor) established the uniform {service}_messages_consumed/_sent counter convention + camelCase workflowId/processorId labels this plan mirrors on the Keeper"
provides:
  - "KeeperMetrics rewritten to the uniform two-counter pair (keeper_messages_consumed / keeper_messages_sent) + label-less keeper_l2_probe heartbeat; legacy keeper_reinject_dropped removed"
  - "RecoveryConsumerBase.Consume choke-point keeper_messages_consumed increment (counts all 6 recovery consumers) + shared protected CountSent helper"
  - "keeper_messages_sent wired into the 4 sending consumers (Reinject/Inject for processor + orchestrator) after each successful Send, never on the drop path"
  - "keeper_l2_probe once-per-BitHealthLoop-tick heartbeat (label-less)"
affects: [74-04, analyzer, passfailengine, live-stack-close-gate, prometheus-dashboards]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Uniform two-counter model on the Keeper via a single consume choke point + a shared CountSent helper on the base recovery consumer (DRY label construction)"
    - "Count-after-successful-Send ordering — CountSent fires only after Guard re-throws-on-exhaustion passes, never on an early-return drop branch (T-74-06 miscount mitigation)"
    - "Label-less heartbeat counter incremented unconditionally once per background-loop tick (keeper_l2_probe), beside the existing every-tick liveness.Update precedent"

key-files:
  created:
    - tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
  modified:
    - src/Keeper/Observability/KeeperMetrics.cs
    - src/Keeper/Recovery/RecoveryConsumerBase.cs
    - src/Keeper/Recovery/ReinjectConsumer.cs
    - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
    - src/Keeper/Recovery/InjectConsumer.cs
    - src/Keeper/Recovery/OrchestratorInjectConsumer.cs
    - src/Keeper/Recovery/DeleteConsumer.cs
    - src/Keeper/Recovery/OrchestratorDeleteConsumer.cs
    - src/Keeper/Health/BitHealthLoop.cs

key-decisions:
  - "keeper_messages_consumed increments ONCE at the RecoveryConsumerBase.Consume choke point all 6 consumers funnel through (D-08) — counts the 2 Delete consumers too"
  - "keeper_messages_sent uses a shared protected CountSent(workflowId, processorId) on the base, called by the 4 senders AFTER the confirmed Send (D-09); the 2 Delete consumers send nothing → no call"
  - "CountSent never fires on the Reinject absent-data early-return drop branch (D-02 / T-74-06)"
  - "keeper_l2_probe is label-less, incremented once per ProbeOnceAsync tick regardless of healthy/unhealthy (D-11)"
  - "The legacy keeper_reinject_dropped drop counter is removed entirely; the by-design-drop structured warning logs survive"

patterns-established:
  - "Shared CountSent helper centralizes the camelCase 2-label keeper_messages_sent increment next to the consumed-counter choke point"
  - "Drop/early-return branches must be counter-silent — only confirmed sends count toward _messages_sent"

requirements-completed: [3, 4, 6]

# Metrics
duration: 29min
completed: 2026-06-18
---

# Phase 74 Plan 03: Keeper Uniform Two-Counter Model + L2 Probe Heartbeat Summary

**Replaced the Keeper's legacy `keeper_reinject_dropped` counter with the uniform `keeper_messages_consumed`/`keeper_messages_sent` pair (one consume choke-point increment counting all 6 recovery consumers, a shared `CountSent` helper firing after each of the 4 senders' confirmed Sends) plus a label-less `keeper_l2_probe` heartbeat ticking once per `BitHealthLoop` probe.**

## Performance

- **Duration:** 29 min
- **Started:** 2026-06-18T06:08:35Z
- **Completed:** 2026-06-18T06:37:30Z
- **Tasks:** 3
- **Files modified:** 9 production + 7 test (1 new test file)

## Accomplishments
- `KeeperMetrics` rewritten to expose exactly `MessagesConsumed` (`keeper_messages_consumed`), `MessagesSent` (`keeper_messages_sent`), and `L2Probe` (`keeper_l2_probe`); `ReinjectDropped` member gone, `MeterName` stays `"Keeper"`, built via `IMeterFactory`.
- `RecoveryConsumerBase.Consume` increments `keeper_messages_consumed` once at the single choke point all 6 recovery consumers funnel through, with camelCase `workflowId`+`processorId` from `IKeeperRecoverable`; a shared protected `CountSent` helper added beside the `Guard` helpers.
- The 4 sending consumers (`ReinjectConsumer`, `OrchestratorReinjectConsumer`, `InjectConsumer`, `OrchestratorInjectConsumer`) call `CountSent` after their confirmed Send; the 2 Delete consumers forward the new base ctor arg only.
- Both Reinject consumers' absent-data drop branches removed the legacy `keeper_reinject_dropped` increment (warning log preserved) and never call `CountSent` on that early-return path.
- `BitHealthLoop` injects `KeeperMetrics` and increments label-less `keeper_l2_probe` once per `ProbeOnceAsync` tick, unconditionally, outside the health-edge guard.
- 0-warning build in Debug and Release for `src/Keeper`; full hermetic suite 668/668 green.

## Task Commits

Each task was committed atomically:

1. **Task 1 (RED): failing KeeperMetrics test** — `be0086c` (test)
2. **Task 1 (GREEN): KeeperMetrics rewrite + base consumed choke + CountSent helper** — `78267e6` (feat)
3. **Task 2: wire CountSent into the 4 sending consumers** — `b6fe38d` (feat)
4. **Task 3: label-less keeper_l2_probe heartbeat in BitHealthLoop** — `890522e` (feat)

**Plan metadata:** (final docs commit — STATE/ROADMAP/REQUIREMENTS + this SUMMARY)

_TDD: Task 1 followed RED (`be0086c`) → GREEN (`78267e6`)._

## Files Created/Modified
- `src/Keeper/Observability/KeeperMetrics.cs` — uniform two-counter pair + L2Probe; ReinjectDropped removed
- `src/Keeper/Recovery/RecoveryConsumerBase.cs` — KeeperMetrics ctor param; consumed choke-point increment; CountSent helper
- `src/Keeper/Recovery/ReinjectConsumer.cs` — forward base arg; drop legacy counter; CountSent after send
- `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs` — forward base arg; drop legacy counter; CountSent after send
- `src/Keeper/Recovery/InjectConsumer.cs` — inject KeeperMetrics; forward base arg; CountSent after send
- `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` — inject KeeperMetrics; forward base arg; CountSent after dispatch
- `src/Keeper/Recovery/DeleteConsumer.cs` — inject KeeperMetrics; forward base arg only (no send → no CountSent)
- `src/Keeper/Recovery/OrchestratorDeleteConsumer.cs` — inject KeeperMetrics; forward base arg only (no send → no CountSent)
- `src/Keeper/Health/BitHealthLoop.cs` — inject KeeperMetrics; label-less L2Probe.Add(1) per tick
- `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` (new) — asserts the 3 uniform instruments emit under meter "Keeper" and the legacy drop counter is gone

## Decisions Made
- Followed plan decisions D-02/D-05/D-06/D-08/D-09/D-10/D-11 exactly. The 6-consumer reality (not the SPEC's 5) was already documented in 74-PATTERNS.md and honored: 4 senders count sent, 2 Delete consumers forward-only.
- `keeper_messages_consumed` labels and `CountSent` labels both use the `IKeeperRecoverable` envelope `WorkflowId`/`ProcessorId` (camelCase) — equivalent to the `DataResult`/`Handoff` ids per D-05/D-06.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Test-project compile/run unblock for the new base ctor + removed counter**
- **Found during:** Task 1 (KeeperMetrics rewrite + base ctor change)
- **Issue:** Adding `KeeperMetrics metrics` to `RecoveryConsumerBase`'s ctor broke the 4 Inject/Delete consumer fact files (CS7036 missing arg); removing `ReinjectDropped` made the two Reinject fact files' `keeper_reinject_dropped`/`dropped==1` assertions reference a removed counter; the `OrchestratorInjectConsumerFacts.Inject_never_reads_L1` ctor-shape guard asserted exactly 4 params.
- **Fix:** Forwarded `RecoveryTestKit.Metrics()` into the 4 Inject/Delete fact construction sites; repointed the two Reinject "absent → drops" tests to assert `keeper_messages_sent` is NOT emitted on the drop path (T-74-06) instead of the removed drop counter; updated the ctor-shape guard to 5 params (5th = `KeeperMetrics`) while preserving its real intent via an explicit "no IWorkflowL1Store/Advancement param" assertion; forwarded `RecoveryTestKit.Metrics()` through the `BitHealthLoopTests.NewLoop` factory.
- **Files modified:** tests/BaseApi.Tests/Keeper/{Reinject,OrchestratorReinject,Inject,OrchestratorInject,Delete,OrchestratorDelete}ConsumerFacts.cs, tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
- **Verification:** Test project compiles 0-error; the 6 consumer fact classes (19 tests) + BitHealthLoopTests (9) + KeeperMetricsFacts (3) all green.
- **Committed in:** `78267e6` (Task 1) and `890522e` (Task 3, BitHealthLoopTests factory)
- **Scope note:** These are MINIMAL compile/run unblocks only. The full metric-fact rewrite + the three removed-counter absence asserts are explicitly owned by Plan 04 (D-14), as called out in the plan's critical reminders.

**2. [Rule 1 - Bug] Removed dangling XML-doc/comment crefs to the deleted ReinjectDropped member**
- **Found during:** Task 1 (after deleting the `ReinjectDropped` member)
- **Issue:** `<see cref="KeeperMetrics.ReinjectDropped"/>` crefs in the two Reinject consumers' class docs failed the doc-build (CS1574); literal `keeper_reinject_dropped` tokens lingered in prose, tripping the acceptance "absent everywhere" grep.
- **Fix:** Rephrased the doc/comment text to describe the removal without the dead cref or the literal token.
- **Files modified:** src/Keeper/Observability/KeeperMetrics.cs, src/Keeper/Recovery/ReinjectConsumer.cs, src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
- **Verification:** `grep -rnE 'ReinjectDropped|keeper_reinject_dropped' src/Keeper/` returns nothing; Debug+Release 0-warning.
- **Committed in:** `78267e6` (Task 1) and `b6fe38d` (Task 2)

---

**Total deviations:** 2 auto-fixed (1 blocking test unblock, 1 dead-reference bug)
**Impact on plan:** Both necessary to land the base ctor + counter-removal cleanly. No scope creep — the full metric-test migration remains Plan 04's responsibility per D-14.

## Issues Encountered
- A single transient failure (1/668) appeared on the first full hermetic run, tied to RabbitMQ bus-teardown noise ("Failed to stop bus … Not Started") in the sandbox (no broker). A clean re-run reported 668/668 passing, 0 failures — the flake was non-deterministic harness teardown timing, not a code regression. The deterministic Keeper-specific suites (KeeperMetricsFacts, all 6 consumer facts, BitHealthLoopTests) are reliably green.

## User Setup Required
None - no external service configuration required.

## TDD Gate Compliance
Task 1 followed the RED→GREEN cycle: `be0086c` (test, RED — compile-fails against the ReinjectDropped-only class) → `78267e6` (feat, GREEN — 3 KeeperMetricsFacts pass). No REFACTOR commit needed.

## Next Phase Readiness
- The Keeper now emits the uniform counter pair + the L2 probe heartbeat; Plan 04 can rebind the analyzer (`PromCounterSnapshot`/`PassFailEngine`/`AnalyzerE2ETests`) to the new names and add the three removed-counter absence asserts (D-12/D-13/D-14), then run the live-stack close gate.
- Two Reinject fact files carry interim Phase-74-shaped drop-path asserts that Plan 04's full metric-fact rewrite will supersede.

## Self-Check: PASSED

All claimed files exist on disk (KeeperMetrics.cs, RecoveryConsumerBase.cs, BitHealthLoop.cs, KeeperMetricsFacts.cs, 74-03-SUMMARY.md) and all 4 task commits (be0086c, 78267e6, b6fe38d, 890522e) are present in git history.

---
*Phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model*
*Completed: 2026-06-18*
