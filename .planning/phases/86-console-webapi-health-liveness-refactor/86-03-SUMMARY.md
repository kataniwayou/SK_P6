---
phase: 86-console-webapi-health-liveness-refactor
plan: 03
subsystem: console-health
tags: [health-checks, liveness, watchdog, interlocked, tdd-green, k3-staleness]

# Dependency graph
requires:
  - phase: 86-01
    provides: RED skeletons + failing tests for LivenessHeartbeat / LoopLivenessHealthCheck (locked signatures)
  - phase: keeper-self-watchdog (quick 260614-b5c)
    provides: KeeperLivenessState Interlocked long-ticks idiom + HealthCheckDescriptor "live" fold-in pattern
  - phase: 61-processor-self-watchdog
    provides: LivenessWatchdogHealthCheck ×2 staleness-math template + OUTER check-time resolution idiom
provides:
  - "GREEN shared liveness primitive: LivenessHeartbeat (Interlocked long-ticks) + LoopLivenessHealthCheck (k=3 staleness, OUTER-resolved)"
  - "AddConsoleLivenessWatchdog opt-in registration seam (ILivenessHeartbeat singleton + 'live'-tagged HealthCheckDescriptor)"
affects: [86-05, 86-06, keeper-liveness-migration, processor-liveness-migration]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "k=3 staleness grace (generalized from the retired ×2) for the unified loop-liveness watchdog"
    - "Opt-in liveness seam in a NEW extensions class (isolates from the 86-04 readiness edits on ConsoleHealthServiceCollectionExtensions)"

key-files:
  created:
    - src/BaseConsole.Core/DependencyInjection/ConsoleLivenessServiceCollectionExtensions.cs
  modified:
    - src/BaseConsole.Core/Health/LivenessHeartbeat.cs
    - src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs

key-decisions:
  - "LoopLivenessHealthCheck resolves ILivenessHeartbeat + TimeProvider from the OUTER provider at check time (never captured at registration) — the embedded listener runs its own inner DI container"
  - "Strict >= boundary: now == Current + k*interval is stale, matching the retired watchdog's boundary discipline generalized ×2 → ×k"
  - "Timestamp-only result — no per-schema summary Data (the old processor summary is not part of the new contract); all messages static literals (T-86-04)"
  - "New ConsoleLivenessServiceCollectionExtensions file (not an edit to ConsoleHealthServiceCollectionExtensions) to avoid a Wave-1 conflict with 86-04"

requirements-completed: [HLTH-01, HLTH-03, HLTH-06]

# Metrics
duration: ~20min
completed: 2026-07-26
---

# Phase 86 Plan 03: GREEN the Shared Liveness-Watchdog Primitive Summary

**Filled the two 86-01 RED liveness skeletons (LivenessHeartbeat Interlocked long-ticks holder + LoopLivenessHealthCheck k=3 OUTER-resolved staleness math) and added the AddConsoleLivenessWatchdog opt-in registration seam that surfaces a "live" watchdog on the embedded /health/live via the existing HealthCheckDescriptor fold-in — orchestrator stays self-only by not calling it.**

## Performance

- **Duration:** ~20 min
- **Started:** 2026-07-26T16:18:33Z
- **Completed:** 2026-07-26 (approx)
- **Tasks:** 3
- **Files created:** 1 · **Files modified:** 2

## Accomplishments
- **LivenessHeartbeat GREEN:** `Beat()` stamps `_clock.GetUtcNow().UtcDateTime.Ticks` via `Interlocked.Exchange`; `Current` reads via `Interlocked.Read` with `0` as the never-beaten sentinel → `null`, else the reconstructed UTC instant. Pure in-memory, lock-free across the loop-writer and probe-reader threads (mirrors the retired `KeeperLivenessState`). LivenessHeartbeatTests 3/3.
- **LoopLivenessHealthCheck GREEN:** resolves `ILivenessHeartbeat` + `TimeProvider` from the OUTER provider at check time; `Current` null → `Unhealthy("liveness loop not started")`; `now >= Current + k*intervalSeconds` (strict `>=`) → `Unhealthy("liveness loop stale")`; else `Healthy("live")`. Timestamp-only, static-literal messages. LoopLivenessHealthCheckTests 5/5 (fresh / stale / null / exact-boundary / one-tick-before).
- **AddConsoleLivenessWatchdog seam:** new `ConsoleLivenessServiceCollectionExtensions` — `TryAddSingleton(TimeProvider.System)` + `AddSingleton<ILivenessHeartbeat, LivenessHeartbeat>()` + a `"live"`-tagged `HealthCheckDescriptor("liveness-watchdog", …, outer => new LoopLivenessHealthCheck(outer, intervalSeconds, k))` that `EmbeddedHealthEndpointService` auto-folds onto `/health/live`. Interval baked at registration (keeper=5, processor=10); a service that never calls it keeps self-only liveness.
- **No regression:** all `*Liveness*` hermetic tests green 40/40 (new Phase-86 + existing processor/keeper watchdog classes); BaseConsole.Core builds 0-warning.

## Task Commits

1. **Task 1: Implement LivenessHeartbeat (Interlocked long-ticks)** — `e80bb70` (feat)
2. **Task 2: Implement LoopLivenessHealthCheck k=3 staleness math** — `99ef270` (feat)
3. **Task 3: Add AddConsoleLivenessWatchdog opt-in seam** — `5270c9c` (feat)

_TDD note: this is the GREEN half of the 86-01 RED cycle — the `test(...)` RED commit (`b29ff99`) landed in 86-01; these three `feat(...)` commits fill the bodies. RED and GREEN gates both satisfied across the two plans._

## Files Created/Modified
- `src/BaseConsole.Core/Health/LivenessHeartbeat.cs` — modified: filled `Beat()` / `Current` with the Interlocked long-ticks idiom.
- `src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs` — modified: filled `CheckHealthAsync` with OUTER-resolution + k=3 staleness; added `using Microsoft.Extensions.DependencyInjection` for `GetRequiredService`.
- `src/BaseConsole.Core/DependencyInjection/ConsoleLivenessServiceCollectionExtensions.cs` — created: the opt-in `AddConsoleLivenessWatchdog` registration seam.

## Decisions Made
- **OUTER check-time resolution:** the check resolves `ILivenessHeartbeat` + `TimeProvider` from `_outer` at check time (RESEARCH Pitfall 4 / T-61-06) because the embedded health listener runs its own inner DI container — matches the existing `LivenessWatchdogHealthCheck(_outer)` idiom.
- **Strict `>=` boundary:** the exact boundary instant (`now == Current + k*interval`) is treated as stale, agreeing with the retired watchdog's `deadline <= now => stale` discipline, generalized ×2 → ×k. Pinned by the exact-boundary + one-tick-before tests.
- **Timestamp-only contract:** dropped the old per-schema summary `Data` — it is not part of the new unified contract (Rule: match the plan's stated contract, not the retired check's shape).
- **New extensions file:** created `ConsoleLivenessServiceCollectionExtensions` rather than editing `ConsoleHealthServiceCollectionExtensions` to avoid a Wave-1 file conflict with 86-04.

## Deviations from Plan

None — plan executed exactly as written. (Task 2 required adding a `using Microsoft.Extensions.DependencyInjection;` for `GetRequiredService`, which is the ordinary import for the plan-specified OUTER resolution, not a scope change.)

## Known Stubs
None — all three primitives are fully implemented and their behavioral tests are green.

## Threat Flags
None — no new network endpoint, auth path, or trust-boundary surface introduced. The `/health/live` fold-in reuses the existing `EmbeddedHealthEndpointService` predicate (`Tags.Contains("live")`); T-86-04 (info-disclosure) and T-86-05 (false-restart boundary) are mitigated by static-literal messages + strict-boundary tests, both asserted green.

## Next Phase Readiness
- The shared primitive is GREEN and the opt-in seam exists; keeper (interval=5) and processor (interval=10) can migrate their divergent per-service watchdogs to a single `AddConsoleLivenessWatchdog(intervalSeconds)` call in a later wave.
- Orchestrator stays self-only by simply not calling the seam (HLTH-01/06).
- No blockers.

## Self-Check: PASSED

---
*Phase: 86-console-webapi-health-liveness-refactor*
*Completed: 2026-07-26*
