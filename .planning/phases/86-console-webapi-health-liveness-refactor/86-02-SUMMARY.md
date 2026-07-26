---
phase: 86-console-webapi-health-liveness-refactor
plan: 02
subsystem: testing
tags: [healthcheck, liveness, readiness, IProcessorContext, resilience, HLTH-02, HLTH-04, xunit, nsubstitute]

# Dependency graph
requires:
  - phase: 86-01
    provides: shared ILivenessHeartbeat + LoopLivenessHealthCheck + RED test scaffolds (BaseConsole.Core), superseding watchdog coverage
provides:
  - ProcessorIdentitySchemaReadyHealthCheck RED skeleton (BaseProcessor-specific readiness reading IProcessorContext.IsHealthy)
  - IdentitySchemaReadyHealthCheckTests RED lock of the false->Unhealthy / true->Healthy readiness mapping
  - StartupOrchestratorResilienceFacts green regression lock of the HLTH-02 broker-down-never-throws invariant
  - Retirement of the two watchdog-only test files (compile-safe ahead of the Wave-2 type deletions)
affects: [86-03, 86-04, 86-05, wave-2-watchdog-retirement, processor-readiness-wiring]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Processor readiness = single volatile-safe IProcessorContext.IsHealthy read at check time via OUTER provider (Pattern 4); never read Id/definition props (WR-03)"
    - "Infra-fault resilience lock: drive the real ProcessorStartupOrchestrator loop, inject a non-OCE broker-down exception through the substituted IRequestClient.GetResponse, assert caught+logged+retried + ExecuteAsync never faults"

key-files:
  created:
    - src/BaseProcessor.Core/Liveness/ProcessorIdentitySchemaReadyHealthCheck.cs
    - tests/BaseApi.Tests/Processor/IdentitySchemaReadyHealthCheckTests.cs
    - tests/BaseApi.Tests/Processor/StartupOrchestratorResilienceFacts.cs
  modified: []

key-decisions:
  - "Injected the broker-down fault through IRequestClient.GetResponse<T1,T2> directly (NSubstitute intercepts it as an interface method in MassTransit 8.5.5) rather than the Create seam, which NSubstitute bypassed"
  - "Used a real StackExchange.Redis RedisConnectionException (ConnectionFailureType.UnableToConnect) as the faithful, definitely-constructable broker/store-unreachable non-OCE fault the plan sanctions"
  - "Asserted the resilience invariant via a capturing logger (>=2 retry warnings) plus a fault-attempt counter — robust to whichever internal seam surfaces the fault"

patterns-established:
  - "Pattern 4 processor readiness skeleton left RED (returns NOT IMPLEMENTED) with the locked contract XML-documented for a later wave to implement"
  - "Watchdog test retirement done in the wave BEFORE the type deletions, since removing references never breaks compile and keeps every wave green"

requirements-completed: [HLTH-02, HLTH-04]

# Metrics
duration: 44min
completed: 2026-07-26
---

# Phase 86 Plan 02: Processor Readiness RED Scaffold + HLTH-02 Resilience Lock Summary

**Processor identity+schema readiness RED skeleton (reads IProcessorContext.IsHealthy) + its RED mapping test, a green HLTH-02 broker-down-never-throws regression lock on ProcessorStartupOrchestrator, and retirement of the two watchdog-only test files ahead of the Wave-2 type deletions.**

## Performance

- **Duration:** 44 min
- **Started:** 2026-07-26T14:14:54Z
- **Completed:** 2026-07-26T14:58:46Z
- **Tasks:** 3
- **Files modified:** 5 (3 created, 2 deleted)

## Accomplishments
- `ProcessorIdentitySchemaReadyHealthCheck` skeleton in `BaseProcessor.Core.Liveness` — ctor `(IServiceProvider outer)`, RED body returning `Unhealthy("NOT IMPLEMENTED")`, with the LOCKED Pattern-4 contract XML-documented (resolve `IProcessorContext` at check time, map `IsHealthy`, never read `Id`/definition props — WR-03).
- `IdentitySchemaReadyHealthCheckTests` `[Trait("Phase","86")]` pinning `IsHealthy=false → Unhealthy("identity+schema not yet resolved")` / `true → Healthy("identity+schema resolved")` — confirmed RED (fails on assertions, not compile).
- `StartupOrchestratorResilienceFacts` `[Trait("Phase","86")]` GREEN regression lock of HLTH-02: injects a broker-unreachable `RedisConnectionException` (non-OCE) into the Loop-A identity RPC and asserts caught + logged + retried, `ExecuteAsync` never faults, never falsely Healthy/Ready, clean `StopAsync`. No product change.
- Deleted the two watchdog-only test files (`LivenessWatchdogHealthCheckTests.cs`, `KeeperLivenessWatchdogTests.cs`); solution still builds 0-warning.

## Task Commits

Each task was committed atomically:

1. **Task 1: Skeleton ProcessorIdentitySchemaReadyHealthCheck + RED test** - `dfaf5d9` (test)
2. **Task 2: Lock HLTH-02 broker-down-never-throws invariant** - `2367172` (test)
3. **Task 3: Retire the two watchdog-only test files** - `ab1e82f` (test)

## Files Created/Modified
- `src/BaseProcessor.Core/Liveness/ProcessorIdentitySchemaReadyHealthCheck.cs` - RED readiness skeleton with the locked Pattern-4 contract documented.
- `tests/BaseApi.Tests/Processor/IdentitySchemaReadyHealthCheckTests.cs` - RED mapping lock (false→Unhealthy, true→Healthy).
- `tests/BaseApi.Tests/Processor/StartupOrchestratorResilienceFacts.cs` - GREEN HLTH-02 broker-down-never-throws regression lock.
- `tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs` - DELETED (retired; exercised the Wave-2-deleted `LivenessWatchdogHealthCheck`).
- `tests/BaseApi.Tests/Keeper/Health/KeeperLivenessWatchdogTests.cs` - DELETED (retired; exercised the Wave-2-deleted `KeeperLivenessWatchdogHealthCheck` / `IKeeperLivenessState`).

## Decisions Made
- **Fault-injection seam:** `IRequestClient<TRequest>.GetResponse<T1,T2>` is NSubstitute-interceptable (behaves as an interface method in MassTransit 8.5.5); the initial `Create`-seam stub was silently bypassed (auto-returned a default task → the loop spun without faulting). Switched to stubbing `GetResponse` directly with `Task.FromException`.
- **Fault type:** used `StackExchange.Redis.RedisConnectionException(ConnectionFailureType.UnableToConnect, …)` — a real, definitely-constructable infra "store/broker unreachable" exception that is unambiguously not `OperationCanceledException`, matching the plan's sanctioned "MassTransit/Redis-style exception".
- **Assertion strategy:** proved the invariant via a capturing logger (≥2 retry warnings) plus a fault-attempt counter, so the test holds regardless of which internal seam surfaces the fault.

## Deviations from Plan

None - plan executed exactly as written. (Task 2's fault-injection point moved from the `Create` seam to `GetResponse` during test authoring — this is a within-task test-harness refinement to reach the intended assertion, not a scope or behavior deviation; no product code changed.)

## Issues Encountered
- **NSubstitute seam discovery (Task 2):** the first harness stubbed `IRequestClient.Create` to throw, but the run produced 0 retry warnings (the loop resolved a default substitute response instead of faulting). Diagnosed that `GetResponse<T1,T2>` is intercepted directly, re-pointed the fault there via `Task.FromException`, and the fact went green.
- **Missing using (Task 2):** `StartupGate` needed `using BaseConsole.Core.Health;` — added; build clean.

## Full Hermetic Suite Result
`BaseApi.Tests.exe --filter-not-trait Category=RealStack`: **762 total, 724 passed, 38 failed**, Debug build 0-warning.
- 32 failures are the expected Phase-86 Wave-0 RED tests (86-01 shared scaffolds: `LoopLivenessHealthCheckTests`, `RedisReadyHealthCheckTests`, `LatchedReadinessHealthCheckTests`, `LivenessHeartbeatTests`, `ComposeYamlFacts`; plus this plan's `IdentitySchemaReadyHealthCheckTests`).
- 6 failures are **pre-existing external-infra integration tests** unrelated to this plan — they require live Postgres / Elasticsearch (`ErrorMappingFacts` [Phase8WebAppFactory], `ConcurrencyTokenTests` [PostgresFixture], `LogExportTests` ×2, `SchemasLogsE2ETests`, `LogLevelFilterTests`), which are not available in the hermetic sandbox. Logged to `deferred-items.md`. Not caused by this plan's changes (processor-liveness test additions + watchdog-test deletions cannot affect DB/observability integration tests).

## Wave-2 Follow-ups (not this plan's scope)
- `ClashRefreshFacts.cs` (Processor) and `BitHealthLoopTests.cs` (Keeper) still reference the to-be-deleted `LivenessWatchdogHealthCheck` / `IKeeperLivenessState`. They compile now (types still exist); Wave 2 must update them when it deletes those types. `IProcessorLivenessState` was deliberately left untouched (backs the separate L2 liveness gate — RESEARCH Pitfall 6).

## Next Phase Readiness
- The processor readiness skeleton is in place for a later wave to implement (map `IsHealthy` + register the `identity-schema-ready` `ready`-tagged descriptor) and turn `IdentitySchemaReadyHealthCheckTests` green.
- HLTH-02 is now regression-locked; any future change that lets a broker-down fault escape the startup loop will fail `StartupOrchestratorResilienceFacts`.

---
*Phase: 86-console-webapi-health-liveness-refactor*
*Completed: 2026-07-26*

## Self-Check: PASSED
- All 3 created files present; both watchdog test files confirmed deleted.
- All task commits found in git: dfaf5d9, 2367172, ab1e82f.
