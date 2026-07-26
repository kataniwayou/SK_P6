---
phase: quick-260726-f7x
plan: 01
subsystem: orchestrator-scheduling
tags: [resilience, quartz, fire-path, ha, tdd]
requires: []
provides:
  - "WorkflowFireJob fire path survives a single entry-step infra send fault (schedule chain intact)"
  - "Hermetic proof that a fire send fault does not silence a self-rescheduling workflow"
affects:
  - src/Orchestrator/Scheduling/WorkflowFireJob.cs
tech-stack:
  added: []
  patterns:
    - "Per-entry-step swallow-log-continue with a cancellation-aware exception filter (when guard)"
key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobResilienceTests.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - src/Orchestrator/Dispatch/StepDispatcher.cs
    - src/Orchestrator/Dispatch/IStepDispatcher.cs
decisions:
  - "Use a C# exception filter (catch Exception when !(OCE && ct.IsCancellationRequested)) so host-shutdown cancellation propagates while infra faults are swallowed — no rethrow-in-catch needed"
  - "Log with entryStepId + exception as template args (ids in the open scope) to sidestep CA2017, mirroring the business-skip LogWarning above it"
  - "StepDispatcher/RelocateTail logic untouched — the continuation path keeps throw -> nack -> redelivery; only the fire path catches"
metrics:
  duration: ~25m
  completed: 2026-07-26
  tasks: 2
  files: 4
requirements: [QUICK-260726-f7x]
---

# Phase quick-260726-f7x Plan 01: Scheduler Fire Resilience (WorkflowFireJob) Summary

Made the Quartz self-rescheduling fire chain resilient to a single transient entry-step send fault: `WorkflowFireJob.Execute` now swallow-logs-continues an infra `DispatchAsync` fault per entry step so the L1 liveness refresh and `WorkflowScheduler.RescheduleAsync` still run, keeping the non-durable one-shot trigger alive instead of letting Quartz auto-purge the job and silently stop the workflow.

## What Was Built

- **RED (Task 1):** `WorkflowFireJobResilienceTests.cs` — three hermetic tests over the real `WorkflowFireJob` + real `WorkflowScheduler` (STANDBY Quartz) + `FakeTimeProvider`, injecting a local `FakeThrowingDispatcher` (no MassTransit/broker):
  - **Test A** `Fire_Send_Fault_Survives_Schedule_Chain` — dispatcher throws `InvalidOperationException`; asserts Execute does NOT throw, L1 liveness advanced to 12:06, and the stored trigger moved 12:05 → 12:10 (RescheduleAsync ran).
  - **Test B** `Host_Shutdown_Cancellation_Is_Not_Swallowed` — dispatcher throws an `OperationCanceledException` tied to an already-cancelled context token; asserts Execute propagates it and the trigger stays at the original 12:05 (no reschedule).
  - **Test C** `One_Entry_Step_Fault_Does_Not_Drop_The_Sibling` — two entry steps, first faults; asserts the second was still dispatched and the reschedule still ran.
  - Confirmed RED: Tests A and C failed against unmodified code (the throw propagated out of Execute); Test B already passed (current cancellation semantics).
- **GREEN (Task 2):** wrapped ONLY the per-entry-step `dispatcher.DispatchAsync(...)` call (inside the existing `if (fireEnabled)` foreach) in a try/catch with the filter `when (!(ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested))`; on catch it logs a warning and `continue`s. Cancellation from host shutdown is not caught, so graceful shutdown proceeds. Doc comments on `WorkflowFireJob`, `StepDispatcher`, and `IStepDispatcher` were realigned (comment-only) to state Send still throws and the two callers differ in handling.

## Deviations from Plan

None — plan executed exactly as written.

## Scope Boundary Verification

`git diff` on `StepDispatcher.cs` / `IStepDispatcher.cs` confirmed comment-only changes: filtering the diff for non-comment executable lines returned zero hits. `StepDispatcher.DispatchAsync` and `RelocateTail` logic are byte-for-byte unchanged, so the continuation-dispatch path keeps throw → nack → broker redelivery. No retry was added to the fire send path.

## Verification

- Targeted resilience suite: 3/3 PASS after the fix (`--filter-class "*WorkflowFireJobResilienceTests*"`).
- Plan-named fire/scheduler families all green in this broker-less environment: `WorkflowFireJobGateTests` 3/3, `WorkflowFireJobScopeTests` 1/1, `FireDispatchTests` 3/3, `Scheduling` namespace 4/4.
- Full `--filter-not-trait Category=RealStack` sweep surfaced 30 failures, all pre-existing and environmental (docker-compose `ComposeYamlFacts`, EF/Postgres integration, and RabbitMQ-harness `ResultAckTests`/`TypedResultConsumerFacts`/`StopConsumerLifecycleTests` consumers) — infra-dependent tests leaking through the trait filter with no local Postgres/RabbitMQ. None invoke `WorkflowFireJob.Execute`, so they cannot be caused by a fire-path try/catch plus doc comments.

## Known Stubs

None.

## Commits

- `9ed5462` test(quick-260726-f7x-01): add failing hermetic fire-send resilience proof
- `71a2756` feat(quick-260726-f7x-01): swallow-log-continue entry-step send fault on fire path

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Orchestrator/WorkflowFireJobResilienceTests.cs
- FOUND commit: 9ed5462
- FOUND commit: 71a2756
