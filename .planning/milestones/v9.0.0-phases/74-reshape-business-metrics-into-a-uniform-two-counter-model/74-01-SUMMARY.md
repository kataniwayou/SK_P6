---
phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
plan: 01
subsystem: orchestrator-observability
tags: [metrics, observability, prometheus, orchestrator, refactor]
requires:
  - "OrchestratorMetrics (existing meter 'Orchestrator')"
  - "IMeterFactory DI pattern"
  - "IExecutionCorrelated / IKeeperRecoverable (WorkflowId+ProcessorId label sources)"
provides:
  - "orchestrator_messages_consumed counter (workflowId+processorId)"
  - "orchestrator_messages_sent counter (workflowId+processorId)"
  - "every successful orchestrator outbound Send increments orchestrator_messages_sent"
affects:
  - "Plan 04 live-stack PassFailEngine / PromCounterSnapshot rebinding"
  - "BreakerMetricsFacts absence-assert rewrite (Plan 04, D-14)"
tech-stack:
  added: []
  patterns:
    - "uniform two-counter business model (*_messages_consumed / *_messages_sent)"
    - "count-after-successful-send (D-02)"
    - "camelCase bounded labels workflowId+processorId; messageId never a label (D-04/D-07)"
key-files:
  created: []
  modified:
    - src/Orchestrator/Observability/OrchestratorMetrics.cs
    - src/Orchestrator/Consumers/TypedResultConsumer.cs
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
    - src/Orchestrator/Dispatch/RelocateTail.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs
    - tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs
decisions:
  - "RelocateTail now ctor-injects OrchestratorMetrics (singleton DI, no Program.cs change) for the INJECT keeper-escalation sent count"
  - "Both PrePipeline.SendKeeper and RelocateTail.SendKeeper use the IKeeperRecoverable msg's WorkflowId/ProcessorId (D-06 producing-processor id)"
  - "BreakerMetricsFacts ResultDeduped legs stripped to unblock compile; full absence-assert migration deferred to Plan 04"
metrics:
  duration_minutes: 6
  completed: 2026-06-18
  tasks: 3
  files_changed: 8
---

# Phase 74 Plan 01: Orchestrator Uniform Two-Counter Model Summary

Collapsed the orchestrator's `orchestrator_dispatch_sent` + `orchestrator_result_consumed` into the
uniform `orchestrator_messages_consumed` / `orchestrator_messages_sent` pair, dropped the dormant
`orchestrator_result_deduped`, and rebound every increment site so `*_sent` fires after EVERY
successful outbound broker Send (forward dispatch, fan-out per match, REINJECT/DELETE/INJECT keeper
escalations) with camelCase `workflowId`+`processorId` labels — `messageId` deliberately excluded as a
label (it is the counting unit, D-04).

## What Was Built

**Task 1 — `OrchestratorMetrics` rewrite (TDD).**
RED: renamed `OrchestratorMetricsFacts` assertions to `MessagesSent`/`MessagesConsumed` (compile-fail
confirmed). GREEN: replaced `DispatchSent`/`ResultConsumed`/`ResultDeduped` with `MessagesConsumed`
(`orchestrator_messages_consumed`) + `MessagesSent` (`orchestrator_messages_sent`); kept
`StepUnresolved` and `MeterName = "Orchestrator"`; built via the existing `IMeterFactory` DI pattern.
Class/member doc comments updated to the new camelCase 2-label / messageId-not-a-label semantics.

**Task 2 — consume + dispatch rebind.**
`TypedResultConsumer.Consume`: `MessagesConsumed.Add(1, workflowId, processorId)` once at the consume
entry. `StepDispatcher.DispatchAsync`: `MessagesSent.Add(1, workflowId, processorId)` after the
successful `endpoint.Send` (count-after-success — a failed send skips it). This single dispatch site
covers both the orchestrator's direct forward dispatch AND `RelocateTail`'s post-process dispatch
(both call `DispatchAsync`) — no second increment in `RelocateTail`.

**Task 3 — fan-out + keeper-escalation sends.**
`OrchestratorPrePipeline`: one `MessagesSent.Add` per successful fan-out `post.Send` inside the
`selection.Matches` loop (N matches ⇒ N increments), after the `if (!sent.Succeeded) throw` guard; and
one in `SendKeeper` (covers both the REINJECT read-fault escalation and the DELETE delete-exhaust
escalation). `RelocateTail`: ctor-injected `OrchestratorMetrics` and a `MessagesSent.Add` in its
`SendKeeper` for the INJECT write-exhaust escalation. All keeper-escalation labels come from the
`IKeeperRecoverable msg`'s `WorkflowId`/`ProcessorId` (D-06: producing-processor id, since the
keeper-recovery queue recipient is not a processor).

## Verification

- `dotnet build src/Orchestrator -c Debug -warnaserror`: **Build succeeded** (0-warning).
- `dotnet build src/Orchestrator -c Release -warnaserror`: **Build succeeded** (0-warning).
- `tests/BaseApi.Tests` Debug build: **Build succeeded**.
- Hermetic facts (`OrchestratorMetricsFacts`, `BreakerMetricsFacts`, `OrchestratorPostProcessConsumerFacts`):
  **10/10 passed** via `BaseApi.Tests.exe --filter-not-trait Category=RealStack`.
- New instrument names present: `orchestrator_messages_consumed`, `orchestrator_messages_sent`
  (snake_case, no `_total` in code).
- Removed members/names absent: `DispatchSent`, `ResultConsumed`, `ResultDeduped`, and the PascalCase
  `"ProcessorId"` label key at the rebound sites.
- PrePipeline has 2 `MessagesSent.Add` (fan-out + SendKeeper); `RelocateTail` injects `OrchestratorMetrics`
  and counts in `SendKeeper`; `dispatcher.DispatchAsync` has no adjacent `MessagesSent.Add` (no double-count).

## TDD Gate Compliance

- RED: `0ec1670` `test(74-01): assert OrchestratorMetrics exposes MessagesSent/MessagesConsumed`
- GREEN: `a51740c` `feat(74-01): rewrite OrchestratorMetrics to uniform two-counter pair`
- No separate REFACTOR commit needed (doc-comment cleanup folded into GREEN).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Test project compile broken by this plan's source changes**
- **Found during:** Task 3 verification (test-project Debug build).
- **Issue:** Removing `OrchestratorMetrics.ResultDeduped` (Task 1) broke `BreakerMetricsFacts.cs`
  (3 references to the deleted member), and adding the `OrchestratorMetrics` ctor param to `RelocateTail`
  (Task 3) broke `OrchestratorPostProcessConsumerFacts.cs:134` (ctor call missing the new arg). The test
  project must compile for ANY hermetic fact (including this plan's `OrchestratorMetricsFacts`) to run.
- **Fix:**
  - `OrchestratorPostProcessConsumerFacts.cs`: added a `NewMetrics()` helper (real `IMeterFactory` →
    `OrchestratorMetrics`) and passed it to the `RelocateTail` ctor — pure mechanical wiring.
  - `BreakerMetricsFacts.cs`: stripped the orchestrator-side `ResultDeduped` legs (the dedup counter is
    gone), keeping the processor-side `DispatchDeduped` leg intact; switched a now-single-element count
    assert to `Assert.Single` (xUnit2013 analyzer). The full absence-assert rewrite of this fixture is
    explicitly owned by Plan 04 (D-14, test-inventory #3) and was NOT done here (scope boundary).
- **Files modified:** `tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs`,
  `tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs`
- **Commit:** `964178b`

## Notes for Downstream Plans

- **Plan 04** still owns: the `BreakerMetricsFacts` absence-assert rewrite (assert no
  `orchestrator_result_deduped_*` / `processor_dispatch_deduped_*` / `keeper_reinject_dropped_*` series),
  the `MetricsRoundTripE2ETests` query rename + cardinality-guard inversion (workflowId now REQUIRED), and
  the `PassFailEngine`/`PromCounterSnapshot`/`AnalyzerE2ETests` rebinding. The processor-side
  `DispatchDeduped` member is still referenced by `BreakerMetricsFacts` and `ProcessorMetricsFacts`; it is
  removed by Plan 02.

## Threat Flags

None — internal observability refactor; no new endpoints, auth, or trust-boundary surface. Labels remain
bounded (`workflowId`+`processorId`); `messageId` excluded per D-04 (no cardinality blow-up).

## Self-Check: PASSED

All modified source files compile (0-warning Debug+Release); SUMMARY exists; all 5 task commits (0ec1670, a51740c, 1e44ff2, 0da5350, 964178b) present in git history.
