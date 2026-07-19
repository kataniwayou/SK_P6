---
phase: 83-orchestrator-ha-failover-proof
plan: 05
subsystem: infra
tags: [masstransit, rabbitmq, orchestrator, ha, fan-out, kubernetes, replicas]

# Dependency graph
requires:
  - phase: 82-orchestrator-ha-leader-election
    provides: replicas:3 orchestrator deployment + per-replica InstanceId identity (POD_NAME)
  - phase: 83-orchestrator-ha-failover-proof (83-03)
    provides: HA-07 live-proof manifest that first surfaced the RESOURCE_LOCKED blocker
provides:
  - Per-instance orchestrator fan-out endpoint names (base + '-' + instanceId) so N replicas each declare a distinct exclusive/Temporary queue and none collide on RESOURCE_LOCKED
  - OrchestratorFanoutEndpoints SoT (LifecycleBase/PauseResumeBase/GlobalPauseResumeBase + PerInstance helper) shared by Program.cs and the hermetic facts
  - Root-cause removal of the literal EndpointName from the 6 fan-out ConsumerDefinitions (the literal bypassed the MassTransit 8.5.5 InstanceId formatter)
  - Hermetic OrchestratorFanoutEndpointsFacts regression guard (distinct-per-instance + co-located-per-pair + shape)
affects: [83-04 live HA-07 re-run, orchestrator replicas scaling]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-instance receive-endpoint naming via an explicit e.Name = {base}-{instanceId} (established multi-replica pattern, mirrors ProcessorStartupOrchestrator ConnectReceiveEndpoint($\"{id:D}\"))"
    - "Never pin a literal EndpointName on a fan-out (InstanceId/Temporary) definition — it bypasses the InstanceId formatter in MassTransit 8.5.5"

key-files:
  created:
    - src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs
  modified:
    - src/Orchestrator/Program.cs
    - src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs
    - src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
    - src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs
    - src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs
    - src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs

key-decisions:
  - "Fix at root: delete the literal EndpointName from the 6 fan-out defs and set an explicit per-instance e.Name in Program.cs (dropping e.InstanceId), rather than working around the collision at the manifest level"
  - "Preserve co-location by using the SAME per-instance name string for each pair (Start==Stop, Pause==Resume, PauseAll==ResumeAll) so the pause/resume pairs' shared ConcurrentMessageLimit=1 serialization stays intact"
  - "Keep e.Temporary=true: with a unique-per-pod name an exclusive/auto-delete queue is correct (auto-cleans on pod death, no cross-replica collision)"
  - "Leave the shared competing-consumer orchestrator-result* definitions untouched (non-exclusive, correct)"

patterns-established:
  - "OrchestratorFanoutEndpoints is the single source of truth for the 3 fan-out base names + the PerInstance helper, referenced by both Program.cs and the hermetic guard"

requirements-completed: [HA-07]

# Metrics
duration: 4min
completed: 2026-07-19
---

# Phase 83 Plan 05: Per-Instance Orchestrator Fan-out Endpoint Naming Summary

**Fixed the HA-07 replicas:3 blocker at its root — removed the literal `EndpointName` that bypassed MassTransit 8.5.5's InstanceId formatter and moved the 3 orchestrator fan-out endpoint groups to per-instance names (`{base}-{instanceId}`), co-located per pair, so each replica's exclusive/Temporary queue is unique and never hits RESOURCE_LOCKED.**

## Performance

- **Duration:** 4 min
- **Started:** 2026-07-19T04:50:18Z
- **Completed:** 2026-07-19T04:54:21Z
- **Tasks:** 2
- **Files modified:** 9 (2 created, 7 modified)

## Accomplishments
- New `OrchestratorFanoutEndpoints` SoT: `LifecycleBase="orchestrator"`, `PauseResumeBase="orchestrator-pauseresume"`, `GlobalPauseResumeBase="orchestrator-global-pauseresume"`, and the pure `PerInstance(base, instanceId) => "{base}-{instanceId}"` helper.
- `Program.cs`: all 6 fan-out `.Endpoint(...)` calls now set an explicit per-instance `e.Name` via `OrchestratorFanoutEndpoints.PerInstance` (co-located per pair), drop `e.InstanceId`, keep `e.Temporary = true`.
- Deleted the literal `EndpointName` assignment from all 6 fan-out `ConsumerDefinition`s (the root cause); `ConcurrentMessageLimit = 1` on the four pause/resume defs kept; shared `orchestrator-result*` defs untouched.
- Hermetic `OrchestratorFanoutEndpointsFacts` (4 facts, no RealStack trait) pins distinct-per-instance, co-located-per-pair, three-bases-distinct, and expected-shape — preventing silent recurrence of the RESOURCE_LOCKED collision.

## Task Commits

Each task was committed atomically:

1. **Task 1: Per-instance fan-out endpoint naming (SoT + Program.cs + 6 defs)** - `71ad33e` (fix)
2. **Task 2: Hermetic regression guard** - `1342003` (test)

**Plan metadata:** (final docs commit) — this SUMMARY + STATE + ROADMAP.

## Files Created/Modified
- `src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs` - SoT: 3 base-name constants + `PerInstance` helper, XML-doc'd with the HA-07 root cause.
- `src/Orchestrator/Program.cs` - 6 fan-out endpoints set explicit per-instance `e.Name` (co-located per pair); `using Orchestrator.Messaging;` added.
- `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs` - literal `EndpointName` removed; doc updated.
- `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs` - literal `EndpointName` removed; doc updated.
- `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs` - literal `EndpointName` removed; `ConcurrentMessageLimit=1` kept.
- `src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs` - literal `EndpointName` removed; `ConcurrentMessageLimit=1` kept.
- `src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs` - literal `EndpointName` removed; `ConcurrentMessageLimit=1` kept.
- `src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs` - literal `EndpointName` removed; `ConcurrentMessageLimit=1` kept.
- `tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs` - 4 hermetic facts pinning the naming contract.

## Three per-instance base names
- `orchestrator` (LifecycleBase) — Start + Stop, co-located → `orchestrator-{instanceId}`
- `orchestrator-pauseresume` (PauseResumeBase) — Pause + Resume, co-located → `orchestrator-pauseresume-{instanceId}`
- `orchestrator-global-pauseresume` (GlobalPauseResumeBase) — PauseAll + ResumeAll, co-located → `orchestrator-global-pauseresume-{instanceId}`

## Build result
- Orchestrator: **Debug 0-warning/0-error, Release 0-warning/0-error**.
- BaseApi.Tests: **Debug 0-warning/0-error, Release 0-warning/0-error**.

## Hermetic fact results
- `OrchestratorFanoutEndpointsFacts`: **4/4 passed** (Docker-less, exit 0) via `BaseApi.Tests.exe --filter-class BaseApi.Tests.Orchestrator.OrchestratorFanoutEndpointsFacts`.

## Grep acceptance gates
- `OrchestratorFanoutEndpoints.PerInstance` in Program.cs: 6 call sites (plus 1 comment mention).
- `EndpointName =` assignment lines across the 6 fan-out defs: **0** (all removed; only doc/comment mentions remain).
- `e.InstanceId` in Program.cs: **0**.
- `StepCompletedConsumerDefinition.cs` still contains `EndpointName = OrchestratorQueues.Result` (shared endpoint untouched).

## Decisions Made
See frontmatter `key-decisions`. In short: fix at the root (delete literal EndpointName, set explicit per-instance name), preserve co-location per pair via identical name strings, keep `Temporary=true`, and leave the shared result endpoints alone.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None. The grep gates for `EndpointName`/`e.InstanceId` count comment mentions as well as code; verified separately that no `EndpointName =` assignment line remains on the 6 fan-out defs and that `e.InstanceId` no longer appears anywhere in Program.cs.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The orchestrator can now bring all replicas to Ready without RESOURCE_LOCKED at replicas:3 — unblocks the live HA-07 proof (83-04 re-run).
- Live validation (orchestrator 3/3 Ready, no RESOURCE_LOCKED in pod logs) is the 83-04 concern, not this plan; this plan delivered the code fix + hermetic guard.

---
*Phase: 83-orchestrator-ha-failover-proof*
*Completed: 2026-07-19*

## Self-Check: PASSED

All 3 created files exist on disk and both task commits (71ad33e, 1342003) are present in git history.
