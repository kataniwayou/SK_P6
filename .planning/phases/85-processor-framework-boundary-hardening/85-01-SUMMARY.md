---
phase: 85-processor-framework-boundary-hardening
plan: 01
subsystem: infra
tags: [dotnet, masstransit, redis, processor-framework, fail-loud, exception-handling]

# Dependency graph
requires:
  - phase: 70-processor-two-consumer
    provides: SpawnToPost/DeleteEntry seam helpers, OnSpawnDropped telemetry hook, IsTransientSendFault classifier
provides:
  - SpawnSendExhaustedException — the dedicated fail-loud Mode-2 spawn-exhaust escape signal (Exception-derived, ids-only)
  - SpawnToPost fires OnSpawnDropped telemetry THEN throws on transient send-exhaust (SC-1 ordering)
  - Deterministic send faults still throw raw (D-03 preserved); IsTransientSendFault untouched
  - DispatchTestKit.SendFaultProvider — shared configurable-boom send-fault double (transient default)
affects: [85-02 (pipeline narrow-catch nack), 85-03 (delete-only-on-success ordering), KafkaImporter KIMP-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Fail-loud hand-off primitive: telemetry-then-throw on transient transport exhaust"
    - "Dedicated Exception-derived escape signal (not ProcessStatusException) that must ESCAPE the pipeline, not route to OutputTail+ack"
    - "Configurable-boom send-fault test double lifted to the shared kit"

key-files:
  created:
    - src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs
  modified:
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs

key-decisions:
  - "SpawnSendExhaustedException derives from System.Exception DIRECTLY, not ProcessStatusException (that family is caught → OutputTail+ack, the opposite of the intended nack)"
  - "Telemetry (OnSpawnDropped) fires BEFORE the throw (SC-1); the deterministic raw-throw and IsTransientSendFault are byte-identical (D-03)"
  - "SendFaultProvider lifted to DispatchTestKit with an optional boom ctor (default transient RedisConnectionException) to serve both the transient-throw and deterministic-negative facts"

patterns-established:
  - "Fail-loud transient-exhaust: fire drop-telemetry hook, then throw a dedicated ids-only escape exception"
  - "Ids-only on any new throw/log path (FW-03/T-70-10): ExecutionId + inner transport exception only, never Data/Payload"

requirements-completed: [PB-01]

# Metrics
duration: 35min
completed: 2026-07-21
---

# Phase 85 Plan 01: SpawnToPost Fail-Loud (PB-01) Summary

**Mode-2 `SpawnToPost` now fires the `OnSpawnDropped` telemetry hook then throws a dedicated `SpawnSendExhaustedException` on transient send-exhaustion instead of swallowing it as silent success — the prerequisite fail-loud signal PB-02/PB-03 build on.**

## Performance

- **Duration:** 35 min
- **Started:** 2026-07-21T20:19:11Z
- **Completed:** 2026-07-21T20:54:37Z
- **Tasks:** 2
- **Files modified:** 4 (1 created, 3 modified)

## Accomplishments
- New `SpawnSendExhaustedException` — `public sealed`, derives from `System.Exception` directly, carries the spawn `ExecutionId` (Guid) + inner transport exception only (ids-only, FW-03/T-70-10).
- `SpawnToPost` transient-exhaust tail flipped from swallow to telemetry-then-throw; the `OnSpawnDropped` hook still fires FIRST (SC-1 ordering preserved).
- Deterministic-fault raw-throw (`:117`) and the `IsTransientSendFault` classifier are untouched (D-03 poison-safety preserved).
- Inverted the PB-01 hermetic seam fact (now asserts the throw + drop-hook-first + `ex.ExecutionId == spawnExec`) and added a deterministic-negative fact (non-transient boom → raw `ArgumentException`, no drop telemetry).
- Lifted `SendFaultProvider` into `DispatchTestKit` as a reusable `public sealed class` with an optional `boom` ctor arg (default transient `RedisConnectionException`).

## Task Commits

Each task was committed atomically:

1. **Task 1: Add SpawnSendExhaustedException + flip SpawnToPost transient-exhaust to fail-loud** - `b9849bb` (feat)
2. **Task 2: Invert PB-01 seam facts (throw) + deterministic-negative fact; lift SendFaultProvider** - `c8c7d67` (test)

_Note: Task 1 (production flip) landed before Task 2 (tests). Because the production change and its inverting tests are separate atomic tasks, the Task-2 facts were GREEN immediately against the Task-1 code rather than starting RED — see TDD Gate Compliance below._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs` - New dedicated fail-loud spawn-exhaust exception (Exception-derived, ids-only ExecutionId + inner).
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` - SpawnToPost transient-exhaust now telemetry-then-throw; class/method doc-comments updated to fail-loud wording.
- `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` - Inverted the swallow fact to a throw fact; added deterministic-negative fact; removed the private SendFaultProvider (now references the kit).
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` - Added `public sealed class SendFaultProvider(Exception? boom = null)` (default transient RedisConnectionException).

## Decisions Made
- Derive `SpawnSendExhaustedException` from `System.Exception` directly (mirror `KeyAbsentException`'s base type), NOT `ProcessStatusException` — the latter is caught at the pipeline and routed to OutputTail+ack, the exact opposite of the intended nack this exception must produce (Plan 02).
- Keep the `OnSpawnDropped?.Invoke` line and place the throw immediately after it (SC-1 telemetry-before-throw); leave the deterministic raw-throw and `IsTransientSendFault` byte-identical.
- Lift `SendFaultProvider` with an optional `boom` ctor rather than cloning it, so one kit double serves both the transient-throw and deterministic-negative facts.
- PB-03 removals (DeleteEntry, EntryId/EscalateDelete seam fields, WireSeam signature) were deliberately left intact — those belong to Plan 03, per the plan's explicit scope note. `DeleteEntry_*` and `ConcurrentConsumes` facts still compile and pass unchanged.

## Deviations from Plan

None - plan executed exactly as written.

## TDD Gate Compliance

Task 2 is marked `tdd="true"`, but the plan sequences the production flip as Task 1 and the inverting/new tests as Task 2. Consequently the RED phase did not fail-first: the Task-2 facts were green immediately against the already-committed Task-1 code. This is a consequence of the plan's task ordering (impl-then-test as two atomic tasks), not a skipped gate — the RED intent (asserting the NEW fail-loud behavior) and GREEN outcome are both satisfied, and the deterministic-negative fact independently guards D-03. The commit sequence is `feat(b9849bb)` then `test(c8c7d67)`.

## Issues Encountered
- The full hermetic suite (`--filter-not-trait Category=RealStack`) reports ~300 failures in this execution sandbox, but they are entirely environmental: 906 `MassTransit.RabbitMqConnectionException` + 751 `RabbitMQ.Client.Exceptions.BrokerUnreachableException` — no RabbitMQ/Redis broker is running here, so broker-dependent (but not RealStack-tagged) liveness/integration tests fail on connect. None touch `SpawnToPost`. All plan-relevant hermetic facts pass: `BaseProcessorSeamFacts` 6/6, and the combined pipeline/processor set (`BaseProcessorSeamFacts` + `PrePipelineFacts` + `SampleProcessorFacts` + `DispatchBindSequenceFacts` + `SchemaResolutionFacts`) 48/48. The 19 Processor-namespace failures are all Redis-liveness/harness classes (DeadRedis, Ttl, Heartbeat, ClashRefresh) needing live infra.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- PB-01 fail-loud signal is in place; Plan 02 (PB-02) can now add the narrow `catch (SpawnSendExhaustedException) { throw; }` in `ProcessorPipeline` to convert the escape into a MassTransit nack-requeue.
- Plan 03 (PB-03) will remove the author-owned `DeleteEntry` and move deletion to the framework null-path (delete-only-on-success), relying on this plan's throw to guarantee "no delete when any spawn exhausts".
- No blockers.

---
*Phase: 85-processor-framework-boundary-hardening*
*Completed: 2026-07-21*
