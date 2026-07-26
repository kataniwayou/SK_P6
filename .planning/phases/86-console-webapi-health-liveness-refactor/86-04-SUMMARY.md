---
phase: 86-console-webapi-health-liveness-refactor
plan: 04
subsystem: console-health
tags: [health-checks, readiness, redis, latch, no-self-heal, tdd-green, stackexchange-redis, masstransit]

# Dependency graph
requires:
  - phase: 86-01
    provides: RedisReadyHealthCheck + LatchedReadinessHealthCheck RED skeletons with locked signatures + failing hermetic tests
provides:
  - "RedisReadyHealthCheck: bounded, never-throw Redis PING readiness resolving IConnectionMultiplexer from the outer provider at check time (HLTH-04/08)"
  - "LatchedReadinessHealthCheck: per-process sticky readiness latch that defeats client auto-reconnect self-heal (HLTH-05)"
  - "All three consoles (orchestrator/keeper/processor) gain latched bus + Redis readiness on /health/ready via one embedded-listener change; /health/live stays self-only"
affects: [console-health-liveness-refactor, k8s-readiness-probes]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Bounded StackExchange.Redis PingAsync via linked-CTS + WaitAsync (2.13.1 has no CancellationToken overload)"
    - "Sticky readiness latch: Interlocked consecutive-failure counter + volatile bool, short-circuit inner once latched, reset on any non-Unhealthy"
    - "Per-process latch instance constructed once in the IHostedService StartAsync (never per request) so sticky state persists across probe polls"

key-files:
  created: []
  modified:
    - src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs
    - src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs
    - src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs

key-decisions:
  - "Readiness latch threshold sourced from ConsoleHealth:ReadinessLatchThreshold, default 5 (matches k8s keeper readiness failureThreshold)"
  - "PingAsync bounded via a linked CTS + WaitAsync (~2s) because StackExchange.Redis 2.13.1 PingAsync exposes no CancellationToken overload"
  - "Genuine shutdown OperationCanceledException (outer cancellationToken cancelled) passes through per the existing never-throw idiom; a bounded-ping timeout does NOT and maps to Unhealthy"
  - "Latch wraps BOTH required deps (bus + Redis) in separate instances so each latches independently; the outer-descriptor fold-in loop and the self/startup checks are untouched"

requirements-completed: [HLTH-04, HLTH-05, HLTH-08]

# Metrics
duration: ~12min
completed: 2026-07-26
---

# Phase 86 Plan 04: GREEN Redis Readiness + Sticky Latch Summary

**Filled the two Wave-0 RED readiness skeletons — `RedisReadyHealthCheck` (bounded, never-throw Redis PING) and `LatchedReadinessHealthCheck` (per-process sticky no-self-heal latch) — then wired one embedded-listener change so all three consoles gain latched bus + Redis on `/health/ready` while `/health/live` stays self-only (HLTH-04/05/08).**

## Performance

- **Duration:** ~12 min
- **Tasks:** 3
- **Files modified:** 3 (all production)

## Accomplishments
- **RedisReadyHealthCheck (GREEN):** resolves `IConnectionMultiplexer` from the outer provider at check time; null → `Unhealthy("Redis not started")`; a bounded `GetDatabase().PingAsync()` (linked CTS + `WaitAsync`, ~2s) → `Healthy`; ANY fault → static `Unhealthy("Redis unreachable")` without the exception escaping. Genuine shutdown cancellation passes through. Static-literal messages only — no connection string or exception detail on the surface (T-86-06). 3/3 hermetic tests green.
- **LatchedReadinessHealthCheck (GREEN):** counts consecutive inner `Unhealthy` results via `Interlocked`; at `failureThreshold` it flips a `volatile bool _latched` and returns a static latched `Unhealthy` for the process lifetime — even after the inner check recovers (T-86-07). Any non-Unhealthy result resets the counter, so a single blip never latches. 2/2 hermetic tests green.
- **Embedded listener wiring:** one `EmbeddedHealthEndpointService.StartAsync` change constructs one `RedisReadyHealthCheck(_outer)` and wraps BOTH bus-ready and redis-ready in their own per-process `LatchedReadinessHealthCheck` instances (threshold from `ConsoleHealth:ReadinessLatchThreshold`, default 5). All three consoles inherit latched bus + Redis on `/health/ready` from this single change point; `/health/live` (self), `/health/startup`, and the outer-descriptor fold-in loop are untouched. ConsoleHealth* 2/2 green; full Console-namespace hermetic subset 48/48 green.

## Task Commits

Each task was committed atomically:

1. **Task 1: Implement RedisReadyHealthCheck (bounded ping, never-throw, no secret)** - `0610b39` (feat)
2. **Task 2: Implement LatchedReadinessHealthCheck (sticky, no self-heal)** - `acc7292` (feat)
3. **Task 3: Wire Redis-ready + latch into the embedded listener for all consoles** - `000cb79` (feat)

_TDD note: this is the GREEN half of the 86-01 RED cycle — the four locked signatures were pinned in `test(86-01): ... b29ff99`; the bodies land here as `feat(...)` commits._

## Files Created/Modified
- `src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs` — filled the bounded, never-throw resolve-ping-map body (outer-provider `IConnectionMultiplexer`, ~2s bounded PING, static-literal Unhealthy on null/fault).
- `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs` — filled the consecutive-failure counter + sticky latch (short-circuit inner once latched, reset on any non-Unhealthy).
- `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` — construct one Redis check + wrap both required deps in per-process latch instances in `StartAsync`; add `redis-ready` (tags: ready); refresh the class doc; drop the now-unused `AddSingleton(new BusReadyHealthCheck)` registration.

## Decisions Made
- **Latch threshold config + default:** `ConsoleHealth:ReadinessLatchThreshold`, default **5** to match the k8s keeper readiness `failureThreshold`. The latch rides the kubelet's own `periodSeconds × failureThreshold` cadence.
- **Bounded ping mechanism:** StackExchange.Redis 2.13.1 `PingAsync` has no `CancellationToken` overload, so the deadline is enforced by a linked CTS + `WaitAsync(~2s)` rather than a token argument (T-86-08 — a hung Redis can never freeze `/health/ready`).
- **Shutdown pass-through vs bounded timeout:** an `OperationCanceledException` is re-thrown ONLY when the outer `cancellationToken` is cancelled (genuine shutdown, existing idiom); a bounded-ping timeout (the linked timeout token) falls through to `Unhealthy("Redis unreachable")`.
- **Two independent latches:** bus and Redis each get their own `LatchedReadinessHealthCheck` instance so one dep latching does not couple the other; the `self`/`startup` checks and the outer-descriptor fold-in loop remain unlatched/as-is.

## Deviations from Plan

None - plan executed exactly as written.

## Threat Surface

All three plan-registered threats are mitigated and covered by the RED-now-GREEN tests:
- **T-86-06 (Information Disclosure):** static-literal "Redis not started"/"Redis unreachable"/latched messages; the `RedisReadyHealthCheckTests` no-secret guard (Password=/abortConnect/`   at `) and the live `ConsoleHealthLiveTests` `/health/ready` body assertion both pass.
- **T-86-07 (self-heal leaks a dead pod back to Ready):** the sticky-after-recovery test proves the latch stays Unhealthy after the inner recovers; latch instances are per-process singletons constructed once in `StartAsync`.
- **T-86-08 (probe hang):** the ~2s bounded PING is exercised by the completing/throwing multiplexer tests; no new unmitigated surface introduced.

## Issues Encountered
- The dead-broker Console fixtures log a benign "Failed to stop bus" / RabbitMQ connect stack trace at teardown (the fixture points at an unreachable broker by design). No test impact — Console-namespace 48/48 green.

## User Setup Required
None — no package installs, no external service configuration (the new config key `ConsoleHealth:ReadinessLatchThreshold` is optional with a default).

## Next Phase Readiness
- The console/api readiness surface now latches on required deps; the k8s manifests' readiness `failureThreshold` should align with the default 5 (or set `ConsoleHealth:ReadinessLatchThreshold`) — a deployment-manifest concern for the phase capstone, not a code blocker.
- No blockers.

## Self-Check: PASSED

- Files: RedisReadyHealthCheck.cs, LatchedReadinessHealthCheck.cs, EmbeddedHealthEndpointService.cs — all FOUND.
- Commits: 0610b39, acc7292, 000cb79 — all FOUND.

---
*Phase: 86-console-webapi-health-liveness-refactor*
*Completed: 2026-07-26*
