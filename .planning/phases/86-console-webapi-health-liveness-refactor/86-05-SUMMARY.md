---
phase: 86-console-webapi-health-liveness-refactor
plan: 05
subsystem: infra
tags: [health-checks, readiness, redis, aspnetcore, latch, webapi]

# Dependency graph
requires:
  - phase: 86-console-webapi-health-liveness-refactor
    provides: "console Redis-readiness + hard-latch pattern (RedisReadyHealthCheck / LatchedReadinessHealthCheck) mirrored here for the webapi"
provides:
  - "ApiRedisReadyHealthCheck — bounded, never-throw Redis readiness probe for the webapi (BaseApi.Core)"
  - "ApiLatchedReadinessHealthCheck — sticky no-self-heal readiness latch for the webapi"
  - "baseapi /health/ready now gates on latched Postgres + Redis; bus stays soft/Degraded and unlatched"
  - "D-06 corrected: Redis is a required, latched readiness dependency (no longer 'Redis down => ready 200')"
affects: [console-webapi-health-liveness-refactor, k8s-probes, resilience-sweep]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-process singleton readiness latch via keyed singletons + HealthCheckRegistration factory (Pitfall 4 safe)"
    - "Check-time IConnectionMultiplexer resolution from the root provider (never captured at registration)"
    - "Env-var-in-ctor dead-dependency injection for WebApplicationFactory fixtures (reliably beats appsettings.Development)"

key-files:
  created:
    - src/BaseApi.Core/Health/ApiRedisReadyHealthCheck.cs
    - src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs
    - tests/BaseApi.Tests/Api/ApiReadinessLatchTests.cs
  modified:
    - src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs
    - src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs

key-decisions:
  - "Kept the Postgres readiness check named 'npgsql' (prior AddNpgSql default) to preserve the /health/ready body contract asserted by existing tests"
  - "failureThreshold sourced from Health:ReadinessFailureThreshold, default 5 (matches k8s baseapi readinessProbe.failureThreshold)"
  - "Bus check left untouched at MinimalFailureStatus=Degraded and unlatched (soft publish-only dep, T-86-11 accepted)"
  - "Dead-Redis test fixture switched to env-var injection + unresolvable .invalid host — the in-memory-only override was silently losing to appsettings.Development (localhost:6380, live here)"

patterns-established:
  - "Keyed-singleton latch wrappers: two ApiLatchedReadinessHealthCheck instances (Postgres, Redis) resolved per-process via HealthCheckRegistration factories"
  - "WebApi readiness = latched required deps (Postgres, Redis); soft deps (bus) capped at Degraded and never latched"

requirements-completed: [HLTH-04, HLTH-05, HLTH-08]

# Metrics
duration: 80min
completed: 2026-07-26
---

# Phase 86 Plan 05: WebApi Redis-Readiness + Hard Readiness Latch Summary

**baseapi /health/ready now includes a bounded never-throw Redis probe and wraps Postgres + Redis in a sticky no-self-heal latch (restart-only recovery), while the bus stays soft/Degraded and unlatched — the D-06 "Redis down => ready 200" contract is superseded.**

## Performance

- **Duration:** ~80 min
- **Started:** 2026-07-26T14:50Z (approx)
- **Completed:** 2026-07-26T16:06Z
- **Tasks:** 2
- **Files modified:** 6 (3 created, 3 modified)

## Accomplishments
- `ApiRedisReadyHealthCheck`: resolves `IConnectionMultiplexer` at check time (root provider), issues a bounded (~2s) never-throw PING; null → Unhealthy "Redis not started", fault/timeout → Unhealthy "Redis unreachable"; static-literal messages only (no secret/connection-string leak, T-86-09).
- `ApiLatchedReadinessHealthCheck`: consecutive-Unhealthy counter + volatile sticky latch; once `failureThreshold` is reached it returns Unhealthy forever until restart (no self-heal, T-86-10); a single non-Unhealthy result resets the counter.
- `AddBaseApiHealth` wires both Postgres (`npgsql`) and Redis readiness through per-process singleton latch instances (keyed) tagged `ready`; `self`/`live` + `startup` unchanged; the bus check (MinimalFailureStatus=Degraded) is deliberately not latched.
- Corrected the stale D-06 XML-doc on `RedisServiceCollectionExtensions` (Redis is now a latched required readiness dep).
- Hermetic proof: `ApiReadinessLatchTests` 5/5 (sticky-after-recovery, blip-reset, Redis null/unreachable, no-secret) and `HealthEndpointsTests` 11/11 including the new `HealthReady_503_When_Redis_Unreachable`.

## Task Commits

1. **Task 1 (RED): failing latch + Redis-readiness tests** - `cf7e96e` (test)
2. **Task 1 (GREEN): implement Redis check + sticky latch** - `3e7431e` (feat)
3. **Task 2: wire Postgres+Redis latch into /health/ready; fix D-06 comment** - `c489d2a` (feat)

_Task 1 is a TDD task (RED test commit → GREEN feat commit)._

## Files Created/Modified
- `src/BaseApi.Core/Health/ApiRedisReadyHealthCheck.cs` - bounded never-throw Redis readiness probe (webapi mirror of the console check)
- `src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs` - sticky no-self-heal readiness latch decorator
- `tests/BaseApi.Tests/Api/ApiReadinessLatchTests.cs` - hermetic latch + Redis-mapping facts (Phase 86, NSubstitute)
- `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` - registers latched Postgres+Redis on the `ready` tag via keyed singletons
- `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs` - D-06 XML-doc corrected (Redis is a latched required readiness dep)
- `tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs` - flipped the superseded Redis-down-ready test to 503 + hardened the dead-Redis fixture

## Decisions Made
- Named the latched Postgres check `npgsql` (matching the retired `AddNpgSql` default) so the existing `/health/ready` body-shape test keeps passing.
- `failureThreshold` read from config `Health:ReadinessFailureThreshold`, default `5` to match the k8s baseapi readinessProbe cadence.
- Redis check resolves the multiplexer at check time from the root provider (mirrors the console `_outer` idiom, Pitfall 4) rather than constructor-injecting it, keeping the null-path trivially testable.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Superseded test still asserted the old D-06 contract**
- **Found during:** Task 2 (full hermetic run)
- **Issue:** `HealthEndpointsTests.HealthReady_200_When_Redis_Unreachable` encoded the now-superseded D-06 behavior ("Redis down => /health/ready 200"), which the plan explicitly overturns (HLTH-04/05/08). Not in the plan's `files_modified`.
- **Fix:** Renamed to `HealthReady_503_When_Redis_Unreachable`, asserting 503 + Unhealthy body + no-secret leak; updated the docstring to record the D-06 supersession.
- **Files modified:** tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
- **Verification:** HealthEndpointsTests 11/11 green.
- **Committed in:** c489d2a

**2. [Rule 1 - Bug] Dead-Redis fixture wasn't actually making Redis unreachable in this environment**
- **Found during:** Task 2 (the flipped 503 test returned 200)
- **Issue:** The fixture injected the dead Redis string only via in-memory `ConfigureAppConfiguration`, which silently lost precedence to `appsettings.Development.json` (`Redis=localhost:6380`) — and `localhost:6380` is a LIVE Redis here (compose/k8s port-forward). Redis had never been health-probed before, so this latent precedence gap was invisible. The `/health/ready` body confirmed the `redis` check returning Healthy against the intended-dead endpoint.
- **Fix:** Switched `HealthDeadRedisFixture` to the proven env-var-in-ctor mechanism (`ConnectionStrings__Redis`, set before host build, captured+restored on dispose — mirrors `HealthDeadPostgresFixture`) and pointed it at an unresolvable RFC 6761 `.invalid` host so the failure is env-independent. Removed the now-moot localhost:6379 port-probe guard and its `System.Net.Sockets` using.
- **Files modified:** tests/BaseApi.Tests/Observability/HealthEndpointsTests.cs
- **Verification:** Standalone diagnostics confirmed the check maps both a refused port and the `.invalid` host to Unhealthy; the two Redis fixture tests then pass 2/2 and the full class 11/11.
- **Committed in:** c489d2a

---

**Total deviations:** 2 auto-fixed (both Rule 1). Both confined to the plan's test surface; the plan's mandated behavior change (Redis down => ready 503) is what exposed them. No production scope creep beyond the 5 planned source targets.
**Impact on plan:** Necessary to make the plan's required behavior actually testable/true. No architectural change.

## Issues Encountered
- The full hermetic suite (`--filter-not-trait Category=RealStack`) buffers all output until process exit under the MTP runner; redirecting to a controlled file (not the harness capture) surfaced the streaming log needed to diagnose the readiness body.
- StackExchange.Redis with `abortConnect=false` tolerates a dead endpoint at construction; the "unreachable" signal must come from the bounded PING mapping (proven by unit + standalone diagnostics), not from connection state.

## Known Stubs
None — both types are fully implemented (not RED skeletons) and wired into `/health/ready`.

## Deferred Issues (pre-existing, out of scope — NOT caused by this plan)
The full hermetic run showed 39 failures; exactly 1 (`HealthReady_503_When_Redis_Unreachable`) was mine and is now fixed. The other 38 have zero code dependency on this plan's 6 files:
- **Phase-86 sibling-plan RED skeletons (14):** `Console.LoopLivenessHealthCheckTests` (5), `Console.RedisReadyHealthCheckTests` (3), `Console.LivenessHeartbeatTests` (3), `Console.LatchedReadinessHealthCheckTests` (1), `Processor.IdentitySchemaReadyHealthCheckTests` (2) — RED-by-design until 86-04/other plans GREEN the console/processor side (this plan never touched console/processor).
- **Legacy compose (18):** `Composition.ComposeYamlFacts` — assert compose.yaml (not touched; docker-compose is legacy per project memory).
- **ES/Postgres infra (6):** `Observability.LogExportTests` (2), `Observability.SchemasLogsE2ETests` (1), `Observability.LogLevelFilterTests` (1), `Middleware.ConcurrencyTokenTests` (1), `Integration.ErrorMappingFacts` (1).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- baseapi is the fourth service to gain Redis readiness + the hard latch (HLTH-04/05/08 satisfied for the webapi).
- The console/processor RED skeletons remain for their own plans (86-04 et al.); when those GREEN, the phase's readiness-latch pattern is uniform across all four services.
- k8s baseapi manifest already carries readinessProbe.failureThreshold=5, matching the latch default.

## Self-Check: PASSED
- Files: FOUND ApiRedisReadyHealthCheck.cs, ApiLatchedReadinessHealthCheck.cs, ApiReadinessLatchTests.cs
- Commits: FOUND cf7e96e (test), 3e7431e (feat GREEN), c489d2a (feat wire)

---
*Phase: 86-console-webapi-health-liveness-refactor*
*Completed: 2026-07-26*
