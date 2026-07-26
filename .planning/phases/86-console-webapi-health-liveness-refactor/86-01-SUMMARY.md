---
phase: 86-console-webapi-health-liveness-refactor
plan: 01
subsystem: testing
tags: [health-checks, liveness, readiness, redis, xunit, nsubstitute, tdd-red, faketimeprovider]

# Dependency graph
requires:
  - phase: 61-processor-self-watchdog
    provides: LivenessWatchdogHealthCheck staleness-math template + OUTER check-time resolution idiom
  - phase: keeper-self-watchdog (quick 260614-b5c)
    provides: KeeperLivenessState Interlocked long-ticks holder idiom
provides:
  - "Four compiling BaseConsole.Core health primitives with locked ctor/member signatures (RED skeletons)"
  - "ILivenessHeartbeat contract (void Beat(); DateTime? Current) shared by keeper+processor"
  - "Four failing Phase-86 hermetic test classes pinning the locked behavior for the GREEN waves"
affects: [86-03, 86-04, console-health-liveness-refactor]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "RED skeleton = final ctor/member signatures + deliberately-wrong bodies so tests fail on behavior not compile"
    - "Check-time OUTER-provider resolution (never capture at registration) generalized into BaseConsole.Core"
    - "Explicit constructors over primary constructors where params are captured-but-unread (CS9113 warnings-as-errors)"

key-files:
  created:
    - src/BaseConsole.Core/Health/ILivenessHeartbeat.cs
    - src/BaseConsole.Core/Health/LivenessHeartbeat.cs
    - src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs
    - src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs
    - src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs
    - tests/BaseApi.Tests/Console/LoopLivenessHealthCheckTests.cs
    - tests/BaseApi.Tests/Console/LivenessHeartbeatTests.cs
    - tests/BaseApi.Tests/Console/RedisReadyHealthCheckTests.cs
    - tests/BaseApi.Tests/Console/LatchedReadinessHealthCheckTests.cs
  modified: []

key-decisions:
  - "Skeletons use explicit constructors (not primary ctors) because captured-but-unread params trip CS9113 under warnings-as-errors"
  - "LivenessHeartbeat skeleton throws NotImplementedException; the other checks return a static Unhealthy(\"NOT IMPLEMENTED\") — both are honest RED"
  - "LatchedReadinessHealthCheck reset-test legitimately passes against the pass-through skeleton; its class is still RED via the sticky-after-recovery test"

patterns-established:
  - "Pattern: k=3 staleness grace (was ×2) for the generalized loop-liveness watchdog"
  - "Pattern: no-secret guard assertion block copied from LivenessWatchdogHealthCheckTests (Password=/abortConnect/'   at ')"

requirements-completed: [HLTH-01, HLTH-04, HLTH-05, HLTH-06, HLTH-08]

# Metrics
duration: ~30min
completed: 2026-07-26
---

# Phase 86 Plan 01: Wave-0 RED Scaffold for Shared Console Health Primitives Summary

**Four compiling BaseConsole.Core health primitives (ILivenessHeartbeat/LivenessHeartbeat, LoopLivenessHealthCheck k=3, RedisReadyHealthCheck, LatchedReadinessHealthCheck) as RED skeletons plus four failing xUnit.v3 hermetic test classes that pin their locked behavior for the 86-03/86-04 GREEN waves.**

## Performance

- **Duration:** ~30 min
- **Started:** 2026-07-26T13:54:04Z
- **Completed:** 2026-07-26T14:20:00Z (approx)
- **Tasks:** 3
- **Files created:** 9 (5 production, 4 test)

## Accomplishments
- Created the shared `ILivenessHeartbeat` contract + `LivenessHeartbeat` holder skeleton (generalizes the retired keeper/processor watchdog holders into one BaseConsole.Core primitive).
- Created `LoopLivenessHealthCheck(IServiceProvider outer, int intervalSeconds, int k = 3)`, `RedisReadyHealthCheck(IServiceProvider outer)`, and `LatchedReadinessHealthCheck(IHealthCheck inner, int failureThreshold)` skeletons with locked signatures and static-literal RED bodies.
- Wrote four `[Trait("Phase","86")]` hermetic test classes (13 tests) that fail on BEHAVIOR (12 fail / 1 pass) — not compile errors — establishing the honest RED half of the TDD cycle.
- Confirmed no regression: the Console-namespace hermetic subset passed 35/35 and the full non-Phase-86 hermetic suite completed with exit code 0.

## Task Commits

Each task was committed atomically:

1. **Task 1: Skeleton the liveness primitive** - `74cebf9` (feat)
2. **Task 2: Skeleton the readiness checks** - `8b101a8` (feat)
3. **Task 3: Write the four RED Phase-86 hermetic test classes** - `b29ff99` (test, TDD RED)

_TDD note: this is the RED half only. Task 3 is a single `test(...)` commit; the GREEN `feat(...)` commits land in plans 86-03 (liveness) and 86-04 (readiness) when the skeleton bodies are filled._

## Files Created/Modified
- `src/BaseConsole.Core/Health/ILivenessHeartbeat.cs` - shared heartbeat contract (Beat / Current)
- `src/BaseConsole.Core/Health/LivenessHeartbeat.cs` - RED holder skeleton (Interlocked long-ticks lands in 86-03)
- `src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs` - RED loop-liveness watchdog (k=3 staleness, 86-03)
- `src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs` - RED bounded never-throw Redis PING check (86-04)
- `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs` - RED sticky-latch readiness decorator (86-04)
- `tests/BaseApi.Tests/Console/LoopLivenessHealthCheckTests.cs` - null/fresh/stale/exact-boundary/one-tick + no-secret guard
- `tests/BaseApi.Tests/Console/LivenessHeartbeatTests.cs` - never-beaten sentinel + Beat-stamps-clock + second-beat-updates
- `tests/BaseApi.Tests/Console/RedisReadyHealthCheckTests.cs` - null-mux / completing-ping / throwing-mux never-throw + no-secret guard
- `tests/BaseApi.Tests/Console/LatchedReadinessHealthCheckTests.cs` - threshold-3 sticky-after-recovery + single-blip reset

## Decisions Made
- **Explicit constructors instead of primary constructors** for the three checks + the holder: the RED skeletons capture ctor args they do not yet consult, and unread primary-constructor parameters raise **CS9113** which the solution treats as an error (warnings-as-errors). Explicit `_field = arg` assignment plus a `_ = (...)` discard keeps the fields "read" at zero warnings while preserving the exact locked ctor signature strings the acceptance greps require.
- **RED body shape:** `LivenessHeartbeat` members throw `NotImplementedException` (per plan); the three `IHealthCheck` skeletons return a static `Unhealthy("NOT IMPLEMENTED")` (or pass through, for the latch) so behavioral assertions fail on value, not on a thrown exception where avoidable.
- **Latch reset test passes under the skeleton** and that is acceptable: the pass-through skeleton happens to satisfy the "single blip does not latch" property, but the class is still RED because the sticky-after-recovery test fails. The reset test will remain green in the GREEN wave.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Primary-constructor unread params broke the build (CS9113)**
- **Found during:** Task 1 (first `dotnet build` of BaseConsole.Core)
- **Issue:** The plan's skeleton shape (primary ctors storing args with unimplemented bodies) does not consult the captured params yet; `LivenessHeartbeat(TimeProvider clock)` and `LoopLivenessHealthCheck(... outer, intervalSeconds, k)` failed to compile with CS9113 "Parameter is unread" under the repo's warnings-as-errors.
- **Fix:** Converted the four primitives that capture-but-do-not-yet-read their args to explicit constructors with `_field` assignment, and reference the fields via a harmless `_ = (...)` discard in the skeleton body. Exact locked ctor signature strings are preserved (verified by the acceptance greps).
- **Files modified:** LivenessHeartbeat.cs, LoopLivenessHealthCheck.cs, RedisReadyHealthCheck.cs, LatchedReadinessHealthCheck.cs
- **Verification:** `dotnet build src/BaseConsole.Core` → Build succeeded, 0 Warnings; ctor-signature greps still match.
- **Committed in:** `74cebf9` (Task 1) and `8b101a8` (Task 2)

---

**Total deviations:** 1 auto-fixed (1 blocking).
**Impact on plan:** The fix preserves every locked signature and the RED intent; it is a mechanical compile-compliance adjustment, no scope change.

## Issues Encountered
- The full `--filter-not-trait Category=RealStack` suite is slow and buffers output (xUnit.v3 MTP flushes at end), exceeding the 600s foreground cap. Resolved by running the targeted Console-namespace subset (35/35 green) for a fast regression signal; the background full non-Phase-86 run subsequently completed with exit code 0, confirming no regression.

## User Setup Required
None - no external service configuration required (no package installs; threat register T-86-SC = accept/N-A).

## Next Phase Readiness
- The four primitive types exist as compiling skeletons with the exact signatures 86-03/86-04 bind against; the four RED test classes are ready to be turned GREEN by filling the bodies only.
- 86-03 fills the liveness primitive (Interlocked long-ticks holder + k=3 staleness math); 86-04 fills the Redis bounded PING and the sticky readiness latch.
- No blockers.

## Self-Check: PASSED

---
*Phase: 86-console-webapi-health-liveness-refactor*
*Completed: 2026-07-26*
