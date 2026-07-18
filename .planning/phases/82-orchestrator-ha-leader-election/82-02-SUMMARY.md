---
phase: 82-orchestrator-ha-leader-election
plan: 02
subsystem: orchestrator
tags: [kubernetes, leader-election, high-availability, orchestrator, fire-gate, observability, otel]

# Dependency graph
requires:
  - phase: 82-01-leader-election-core
    provides: LeaderState (single-writer volatile snapshot) + LeaderElectionService (build-only BackgroundService)
provides:
  - OrchestratorRoleLogEnricher — OTel LogRecord processor stamping attributes.role=leader|follower on every log
  - WorkflowFireJob entry-step send gate on IsLeader && hydrated (follower skips sends, still refreshes L1 + reschedules)
  - Program.cs wiring — POD_NAME identity, LeaderState singleton, role enricher, in-cluster-only election service
affects: [82-03-manifests, 82-04-hermetic-ha-tests, 83-failover-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Fire gate snapshotted ONCE at fire top (IsLeader && IsReady) — single read, not per-iteration (D-04)"
    - "Role log enricher: logger-provider twin of the meter registration, DI-resolved ConfigureOpenTelemetryLoggerProvider (mirrors ProcessorIdLogEnricher)"
    - "In-cluster gating via KUBERNETES_SERVICE_HOST — the election hosted service registers only in-cluster; off-cluster/test defaults to leader (D-02/D-06)"

key-files:
  created:
    - src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs
  modified:
    - src/Orchestrator/Scheduling/WorkflowFireJob.cs
    - src/Orchestrator/Program.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
    - tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs
    - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs

key-decisions:
  - "Suppressed CA2017 for the single follower-skip log line: the verbatim literal keeps {WorkflowId} for readability but passes NO template arg (WorkflowId rides the open scope per T-18-04) — a deliberate scope-only line, not an arg-count bug"
  - "POD_NAME identity feeds BOTH the bus InstanceId and the LeaseLock holder from ONE variable (T-82-06 collision-avoidance); Orchestrator:InstanceId config key dropped (D-01)"
  - "LeaderState singleton seeded !inCluster → leader off-cluster (lone instance must fire), follower in-cluster until the elector wins (D-02/D-07/HA-02)"

patterns-established:
  - "OrchestratorRoleLogEnricher: no null-guard (unlike ProcessorId) — role is never empty, so always appended; live per-record read surfaces a failover flip on the next line"
  - "Fire-gate test seams: OrchestratorTestStubs.Leader() + ReadyGate() supply the leader+hydrated pre-condition pre-82 fire tests assume"

requirements-completed: [HA-01, HA-04, HA-05, HA-06]

# Metrics
duration: 6min
completed: 2026-07-18
---

# Phase 82 Plan 02: Fire Gate + Role Enricher Summary

**Wired the Plan-01 leader-election core into runtime behavior: WorkflowFireJob now gates its entry-step DispatchAsync loop on `IsLeader && hydrated` (follower skips ONLY the sends, still refreshes L1 + reschedules), every orchestrator log carries `attributes.role`, and Program.cs derives per-pod identity from POD_NAME while registering the election service in-cluster only.**

## Performance

- **Duration:** ~6 min
- **Started:** 2026-07-18T19:11:23Z
- **Completed:** 2026-07-18T19:17:14Z
- **Tasks:** 3
- **Files modified:** 6 (1 created, 5 modified)

## Accomplishments
- `OrchestratorRoleLogEnricher` — a `BaseProcessor<LogRecord>` that ALWAYS appends the lowercase literal `"role"` = live `LeaderState.Role` (no null-guard; role is never empty), keyed off the raw literal (NOT an `ExecutionLogScope.*` const).
- `WorkflowFireJob` — injects `LeaderState` + `IStartupGate`, snapshots `fireEnabled = leaderState.IsLeader && startupGate.IsReady` ONCE at the fire top (D-04), and wraps ONLY the `foreach … DispatchAsync` loop in `if (fireEnabled)`. The follower/un-hydrated branch emits the one verbatim info log; the L1 liveness refresh + `RescheduleAsync` stay OUTSIDE the gate, ungated for all replicas (HA-01). `RelocateTail` is untouched (HA-03).
- `Program.cs` — `instanceId` from `POD_NAME` → `MachineName` (D-01, config key dropped); `inCluster` detection via `KUBERNETES_SERVICE_HOST`; `LeaderState` singleton (leader off-cluster / follower in-cluster); role enricher on the logger provider; `LeaderElectionService` as a hosted service in-cluster ONLY.
- Orchestrator builds 0-warning Debug AND Release; the full solution builds 0-warning Debug; the 4 affected fire tests pass.

## Task Commits

Each task was committed atomically (normal commits, hooks on):

1. **Task 1: OrchestratorRoleLogEnricher** — `65e086c` (feat)
2. **Task 2: Gate WorkflowFireJob entry-step sends on IsLeader && hydrated** — `e4c6bea` (feat)
3. **Task 3: Program.cs wiring + test-compile fixes** — `05d8f68` (feat)

## Files Created/Modified
- `src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs` — the HA-05/D-09 role enricher (created)
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — ctor + fire-gate snapshot + gated loop + follower log
- `src/Orchestrator/Program.cs` — POD_NAME identity, LeaderState singleton, role enricher, in-cluster election
- `tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs` — added `Leader()` + `ReadyGate()` fire-gate seams
- `tests/BaseApi.Tests/Orchestrator/FireDispatchTests.cs` — 3 ctor call sites updated for the two new params
- `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs` — 1 ctor call site updated

## Decisions Made
- **CA2017 suppressed for the follower-skip log** — see Deviations. The message template intentionally carries `{WorkflowId}` for human readability but passes no argument, because WorkflowId already rides the open log scope (T-18-04, ids-in-scope-not-template). Under the repo's warnings-as-errors, CA2017 (placeholder/arg count) would otherwise block; a scoped `#pragma warning disable/restore CA2017` with an explanatory comment preserves the plan's verbatim literal AND the scope-only intent.
- **Cross-assembly cref downgraded** — the enricher's `<see cref="ProcessorIdLogEnricher"/>` (in BaseProcessor.Core, not referenced by Orchestrator) tripped CS1574 under warnings-as-errors; downgraded to a `<c>` span exactly as Plan 01 did for its cross-assembly crefs. Cosmetic, no behavior change.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] CS1574 cross-assembly cref in the enricher doc-comment**
- **Found during:** Task 1
- **Issue:** `<see cref="BaseProcessor.Core.Observability.ProcessorIdLogEnricher"/>` cannot resolve — Orchestrator does not reference BaseProcessor.Core; CS1574 is promoted to a build error by repo-wide warnings-as-errors.
- **Fix:** Downgraded the cref to a `<c>ProcessorIdLogEnricher</c>` span (same fix Plan 01 applied to its cross-assembly crefs).
- **Files modified:** src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs
- **Committed in:** 65e086c (Task 1 commit)

**2. [Rule 3 - Blocking] CA2017 on the verbatim follower-skip log literal**
- **Found during:** Task 2
- **Issue:** The plan's mandated verbatim literal `"Follower — leader gate closed; skipping entry-step sends for {WorkflowId}"` has a `{WorkflowId}` placeholder but passes NO argument (WorkflowId rides the open scope per T-18-04). CA2017 (placeholder/arg-count mismatch) is a build error under warnings-as-errors.
- **Fix:** Wrapped the single log statement in `#pragma warning disable CA2017 … restore CA2017` with a comment explaining the scope-only convention. Preserves the exact literal (acceptance grep) and the no-arg intent.
- **Files modified:** src/Orchestrator/Scheduling/WorkflowFireJob.cs
- **Committed in:** e4c6bea (Task 2 commit)

**3. [Rule 3 - Blocking] Existing fire tests broke on the new WorkflowFireJob ctor**
- **Found during:** Task 3 (full-solution build)
- **Issue:** The two new ctor params (`LeaderState`, `IStartupGate`) broke compilation of pre-existing tests that construct `WorkflowFireJob` directly (FireDispatchTests ×3, WorkflowFireJobScopeTests ×1) — CS7036.
- **Fix:** Added `OrchestratorTestStubs.Leader()` (leader-seeded LeaderState) + `ReadyGate()` (hydrated StartupGate) helpers and passed them at the 4 call sites. These preserve the tests' original semantics (they assert the fire DOES dispatch), which requires a leader-and-hydrated gate. Follower/un-hydrated gating is the province of the Plan-04 hermetic tests.
- **Files modified:** OrchestratorTestStubs.cs, FireDispatchTests.cs, WorkflowFireJobScopeTests.cs
- **Verification:** 4/4 affected tests pass; full solution builds 0-warning Debug.
- **Committed in:** 05d8f68 (Task 3 commit)

---

**Total deviations:** 3 auto-fixed (3 blocking). No scope creep — all three were compile-blockers under the repo's warnings-as-errors + TDD test-compile gate; behavior is unchanged from the plan's intent.

## Issues Encountered
None beyond the three build-blockers documented above.

## Known Stubs
None — all three artifacts are fully implemented and wired. `LeaderElectionService` remains registered in-cluster only (D-02/D-06); off-cluster the lone instance defaults to leader and fires. This is a deliberate phase boundary (live election is Phase 83), not a stub.

## Verification
- `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug` → **0 warnings / 0 errors**.
- `dotnet build src/Orchestrator/Orchestrator.csproj -c Release` → **0 warnings / 0 errors**.
- `dotnet build -c Debug` (full solution) → **0 warnings / 0 errors**.
- Grep confirms: single gate snapshot (`leaderState.IsLeader && startupGate.IsReady` ×1), verbatim follower-skip literal present, `RelocateTail` has 0 `LeaderState|IsLeader` references (HA-03), `Orchestrator:InstanceId` dropped (0), `POD_NAME` + `Environment.MachineName` present, `new LeaderState(startAsLeader: !inCluster)` present, `AddHostedService<LeaderElectionService>()` inside `if (inCluster)`, `ConfigureOpenTelemetryLoggerProvider` present.
- 4/4 affected fire tests pass (`FireDispatchTests`, `WorkflowFireJobScopeTests`) via `BaseApi.Tests.exe`.

## Next Phase Readiness
- Plan 03 supplies the k8s manifest changes: the `POD_NAME` downward-API env, the `orchestrator-leader` Lease + least-privilege RBAC, and dropping `Orchestrator__InstanceId`.
- Plan 04 (hermetic HA tests) drives the follower/un-hydrated gate and the role enricher directly via `LeaderState` + `IStartupGate`, asserting the follower-skip log and `attributes.role`.

## Self-Check: PASSED

- FOUND: src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs
- FOUND: .planning/phases/82-orchestrator-ha-leader-election/82-02-SUMMARY.md
- FOUND commits: 65e086c, e4c6bea, 05d8f68

---
*Phase: 82-orchestrator-ha-leader-election*
*Completed: 2026-07-18*
