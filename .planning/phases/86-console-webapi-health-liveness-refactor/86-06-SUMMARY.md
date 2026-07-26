---
phase: 86-console-webapi-health-liveness-refactor
plan: 06
subsystem: keeper-health
tags: [health-checks, liveness, watchdog, keeper, bit-loop, bounded-busop, tdd-green, HLTH-03, HLTH-06]

# Dependency graph
requires:
  - phase: 86-02
    provides: retirement of the two watchdog-only test files (KeeperLivenessWatchdogTests deleted) ahead of the Wave-2 type deletions
  - phase: 86-03
    provides: shared ILivenessHeartbeat + LoopLivenessHealthCheck + AddConsoleLivenessWatchdog opt-in seam (keeper=5s)
  - phase: 86-04
    provides: /health/ready Redis-ready + latch fold-in via EmbeddedHealthEndpointService (inherited by keeper, no keeper code)
provides:
  - "Keeper integrated onto the shared loop-liveness watchdog: BitHealthLoop Beat()s ILivenessHeartbeat at the TOP of every tick, before ProbeOnceAsync and any bus op"
  - "Bounded keeper edge bus-ops (Start().Ready / Publish / Stop each WaitAsync(3s)) so a dead broker can no longer freeze the tick and starve the beat (T-86-12)"
  - "Keeper first-beat startup readiness (base StartupCompletionService removed) + retirement of KeeperLivenessWatchdogHealthCheck/IKeeperLivenessState/KeeperLivenessState (T-86-13)"
affects: [86-07, 86-08, 86-09, keeper-liveness-migration]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Top-of-tick unconditional heartbeat beat BEFORE any infra I/O (the HLTH-03 fix for the original BIT-loop stale-under-outage bug)"
    - "Bounded edge bus-op via Task.WaitAsync(EdgeOpTimeout, stoppingToken): TimeoutException routes to the existing WR-01 catch (prevHealthy un-advanced, idempotent retry); OCE still breaks cleanly on shutdown"
    - "First-beat IStartupGate.MarkReady() replacing the base StartupCompletionService (bus-start readiness) — copies the BaseProcessor ImplementationType-removal idiom adapted to builder.Services"

key-files:
  created: []
  modified:
    - src/Keeper/Health/BitHealthLoop.cs
    - src/Keeper/Program.cs
    - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
  deleted:
    - src/Keeper/Health/KeeperLivenessWatchdogHealthCheck.cs
    - src/Keeper/Health/IKeeperLivenessState.cs
    - src/Keeper/Health/KeeperLivenessState.cs

key-decisions:
  - "Beat FIRST, unconditionally, at the top of the tick — this is the core original-bug fix: the retired beat sat AFTER ProbeOnceAsync + the edge block, so a wedged probe or hung broker edge-op froze the tick and starved the stamp → false /health/live stale → false restart"
  - "Bounded each edge bus-op with WaitAsync(EdgeOpTimeout=3s) rather than moving them off-tick: preserves the existing gate.Open/Close + prevHealthy-retry + OCE-break edge semantics that the 8 legacy BitHealthLoopTests lock, while capping worst-case tick time (≤2 bounded ops = ≤6s) well under the 15s (k=3·5s) stale window"
  - "Removed the base StartupCompletionService so keeper /health/startup flips on the first BIT-loop beat (loop actually ticking) instead of bare host start (HLTH-06)"
  - "Removed the unused clock (TimeProvider) ctor param when deleting liveness.Update — the shared LivenessHeartbeat owns its own injected clock; keeps the keeper 0-warning (no CS9113 unread-parameter)"

requirements-completed: [HLTH-03, HLTH-04, HLTH-05, HLTH-06, HLTH-07, HLTH-08]

# Metrics
duration: ~27min
completed: 2026-07-26
---

# Phase 86 Plan 06: Integrate the Shared Watchdog into the Keeper + Fix the BIT-Loop Stale Bug Summary

**Rewired the keeper's BitHealthLoop onto the shared BaseConsole.Core ILivenessHeartbeat — the beat now fires unconditionally at the TOP of every tick (before ProbeOnceAsync and before any bus op), the three edge bus-ops (Start().Ready / Publish / Stop) are each bounded with WaitAsync(3s) so a dead broker can no longer freeze the tick and starve the beat, the startup gate is marked on the first beat, and the retired keeper-specific watchdog + L1 state (three files) are deleted — closing the "BIT loop stale → false restart under broker outage" bug (HLTH-03/06/07).**

## Performance

- **Duration:** ~27 min (wall; includes a ~20-min background full-hermetic verification run)
- **Started:** 2026-07-26T16:40:49Z
- **Completed:** 2026-07-26T17:07:45Z
- **Tasks:** 3
- **Files modified:** 3 · **Files deleted:** 3 · **Files created:** 0

## Accomplishments
- **BitHealthLoop top-of-tick beat (HLTH-03):** the ctor swaps `IKeeperLivenessState liveness` (+ the now-unused `TimeProvider clock`) for the shared `ILivenessHeartbeat heartbeat` + `IStartupGate startupGate`. `heartbeat.Beat()` is now the FIRST statement of the while body, before `probe.ProbeOnceAsync` and any bus op — so every iteration the loop reaches stamps a fresh instant regardless of probe/broker state. This is the fix for the original bug (the old `liveness.Update` sat *after* the probe + edge block).
- **Bounded edge bus-ops (T-86-12):** `h.ReceiveEndpoint.Start(ct).Ready`, `bus.Publish(ResumeAll/PauseAll)`, and `h.ReceiveEndpoint.Stop(ct)` are each wrapped in `.WaitAsync(EdgeOpTimeout, stoppingToken)` (3s). A wedged broker now surfaces a `TimeoutException` that routes to the existing WR-01 `catch (Exception)` — `prevHealthy` stays un-advanced, the idempotent edge re-applies next tick — instead of hanging the tick indefinitely. `OperationCanceledException` still breaks cleanly on shutdown.
- **First-beat startup readiness (HLTH-06):** `startupGate.MarkReady()` fires once on the first beat (guarded by a local `firstBeat` bool); Program.cs removes the base `StartupCompletionService` via the `ImplementationType`-filter foreach copied from `BaseProcessorServiceCollectionExtensions`. Keeper `/health/startup` now reflects the BIT loop actually ticking, not bare host start.
- **Shared-watchdog registration + retirement (T-86-13):** Program.cs replaces the `IKeeperLivenessState` singleton + the `"keeper-liveness-watchdog"` `HealthCheckDescriptor` with a single `builder.Services.AddConsoleLivenessWatchdog(intervalSeconds: 5)` (5s matches `Probe:DelaySeconds` → stale at 15s under k=3). Three files deleted: `KeeperLivenessWatchdogHealthCheck.cs`, `IKeeperLivenessState.cs`, `KeeperLivenessState.cs`. `/health/live` is now the shared `LoopLivenessHealthCheck`; `/health/ready` inherits Redis + latch from 86-04 with no keeper code.
- **Tests ported + extended:** `BitHealthLoopTests` swaps the `IKeeperLivenessState` fake for a `LivenessHeartbeat`/`StartupGate` (all 8 legacy edge facts unchanged and green) plus 3 new `[Trait("Phase","86")]` facts. **11/11 green.**

## Task Commits

Each task committed atomically:

1. **Task 1: Rewire BitHealthLoop — top-of-tick beat, bounded edge bus-ops, first-beat MarkReady** — `148cf60` (fix)
2. **Task 2: Swap keeper registration to the shared watchdog; delete retired watchdog + state** — `fcce4f0` (feat)
3. **Task 3: Port BitHealthLoopTests to the shared heartbeat + add beat-first / hung-op facts** — `12076a0` (test)

## Files Created/Modified
- `src/Keeper/Health/BitHealthLoop.cs` — modified: shared-heartbeat ctor; top-of-tick unconditional `Beat()`; first-beat `MarkReady()`; `EdgeOpTimeout` (3s) `WaitAsync` bound on all three edge bus-ops; timeout routes to the WR-01 retry catch.
- `src/Keeper/Program.cs` — modified: `AddConsoleLivenessWatchdog(intervalSeconds: 5)` replaces the retired descriptor + `IKeeperLivenessState` singleton; `StartupCompletionService` removed via the `ImplementationType` foreach.
- `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` — modified: `NewLoop` factory + `Beat_Advances_Every_Tick_Including_Unhealthy` ported to `LivenessHeartbeat`; added `Beat_And_MarkReady_Fire_Before_The_Probe` (ordering lock) + `Beat_Survives_A_Hung_Edge_BusOp` (T-86-12) + a `HangingStartHandle()` helper.
- `src/Keeper/Health/KeeperLivenessWatchdogHealthCheck.cs` — DELETED (retired keeper-specific watchdog).
- `src/Keeper/Health/IKeeperLivenessState.cs` — DELETED (retired keeper-specific L1 state interface).
- `src/Keeper/Health/KeeperLivenessState.cs` — DELETED (retired keeper-specific L1 state holder).

## Decisions Made
- **Beat first, unconditionally:** placing `Beat()` above `ProbeOnceAsync` is the whole point of the plan — it guarantees a fresh stamp even on a tick whose bounded edge-op times out. Proven deterministically by `Beat_And_MarkReady_Fire_Before_The_Probe` (a non-Redis probe throw faults `ExecuteAsync` *at* the probe, yet the pinned `heartbeat.Current` and `startupGate.IsReady` are already set).
- **Bound rather than move off-tick:** WaitAsync bounding keeps the edge actions inside the one WR-01 `try`, so the existing `gate.Open/Close` idempotence + `prevHealthy`-retry + `OperationCanceledException`-break semantics (locked by the 8 legacy facts) are preserved unchanged. `EdgeOpTimeout = 3s` caps a healthy edge's two sequential ops at ≤6s ≪ the 15s stale window.
- **Removed the unused clock param:** deleting `liveness.Update(clock…)` left `TimeProvider clock` unread; removed it to keep the keeper 0-warning (avoids CS9113). The shared `LivenessHeartbeat` owns its own injected clock.
- **Interval = 5s:** matches `Probe:DelaySeconds` so a silently-stalled BIT loop goes stale at 15s (k=3), preserving the retired watchdog's DelaySeconds×2 intent generalized to ×k=3.

## Deviations from Plan

None — plan executed exactly as written. (The plan's Task 1 said to add an `IStartupGate gate`; since the ctor already had an `IL2HealthGate gate`, the new param was named `startupGate` to avoid a name clash — a naming detail, not a scope change. The plan also did not explicitly mention removing the now-unused `TimeProvider clock` ctor param; removing it is required to keep the keeper 0-warning after `liveness.Update` was deleted — a correctness/cleanliness follow-through of the specified change, not new scope.)

## Known Stubs
None — the BIT loop is fully rewired, all three retired files are gone, and the behavioral tests (including the two new T-86-12/ordering facts) are green.

## Threat Flags
None — no new network endpoint, auth path, or trust-boundary surface. The `/health/live` fold-in reuses the existing `EmbeddedHealthEndpointService` `"live"` predicate (via the shared `AddConsoleLivenessWatchdog` descriptor from 86-03). Both plan threats are mitigated and test-locked:
- **T-86-12 (DoS / false restart, dead broker → tick):** beat-first + WaitAsync-bounded edge ops; `Beat_Survives_A_Hung_Edge_BusOp` asserts the beat stays fresh when `Start().Ready` never completes.
- **T-86-13 (tampering / retired-type residue):** the three files are deleted; `grep -rn` over `src/` shows zero remaining *code* references (only historical prose in doc-comments).

## Verification
- **Keeper build:** `dotnet build src/Keeper/Keeper.csproj -c Debug` → **0 warnings, 0 errors** (after each of Task 1 and Task 2).
- **BitHealthLoopTests:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-class "*BitHealthLoopTests*"` → **11/11 passed** (8 legacy edge facts + 3 new Phase-86 facts; the hung-edge-op fact exercises the real 3s WaitAsync timeout).
- **Full hermetic suite:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` → runner **exit code 0** (no failing tests; per project practice the harness exit code is authoritative — the retained output tail is captured resilience-test broker-down log noise, not a failure). No new failures introduced by this plan.

## Next Phase Readiness
- Keeper now mirrors the shared liveness contract; the processor migration (interval=10) can follow the same `AddConsoleLivenessWatchdog` swap in a later wave.
- The keeper's `/health/ready` Redis + latch inheritance (86-04) is exercised transitively; no keeper-side wiring was needed.
- No blockers.

## Self-Check: PASSED
- SUMMARY.md present; all 3 modified files present; all 3 deleted files confirmed gone.
- All task + summary commits found in git: 148cf60, fcce4f0, 12076a0, a42c768.

---
*Phase: 86-console-webapi-health-liveness-refactor*
*Completed: 2026-07-26*
