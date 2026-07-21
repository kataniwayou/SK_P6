---
phase: 82-orchestrator-ha-leader-election
plan: 01
subsystem: infra
tags: [kubernetes, leader-election, high-availability, orchestrator, backgroundservice, volatile]

# Dependency graph
requires:
  - phase: 80-81-k8s-deploy
    provides: k8s deployment of the orchestrator (namespace skp, single replica) that HA extends
provides:
  - KubernetesClient dependency pinned (CPM) and referenced by the Orchestrator project
  - LeaderState — single-writer volatile leadership snapshot holder (follower default)
  - LeaderElectionService — build-only BackgroundService running the KubernetesClient LeaderElector over the skp/orchestrator-leader Lease
affects: [82-02-fire-gate-and-role-enricher, 82-04-hermetic-ha-tests, 83-failover-proof]

# Tech tracking
tech-stack:
  added: [KubernetesClient 18.0.13]
  patterns:
    - "Single-writer volatile whole-record swap for lock-free cross-thread snapshot state"
    - "Election timings/lease coordinates as public static constants for test assertion without starting the service"
    - "Build-only BackgroundService (D-06): wired in-cluster only, never started under test"

key-files:
  created:
    - src/Orchestrator/Election/LeaderState.cs
    - src/Orchestrator/Election/LeaderElectionService.cs
  modified:
    - Directory.Packages.props
    - src/Orchestrator/Orchestrator.csproj

key-decisions:
  - "Fix-forwarded the KubernetesClient pin off the plan's 15.x line to 18.0.13: 15.0.1 carries advisory GHSA-w7r3-mgwf-4mqq that the repo NuGetAudit promotes to a build error"
  - "LeaderElector callbacks are the SOLE LeaderState writer (grep-enforced single-writer invariant, HA-03)"
  - "Election timings exposed as public constants so Plan 04 asserts RenewDeadline < LeaseDuration without starting the service (D-06)"

patterns-established:
  - "LeaderState volatile record-swap: reads through a volatile field, writes publish a new immutable LeaderSnapshot — no locks, torn-read-safe (mirrors StartupGate Volatile discipline + WorkflowFireJob record-with-swap)"
  - "Election constants (LeaseDuration/RenewDeadline/RetryPeriod/LeaseNamespace/LeaseName) are public static members — the testable seam replacing a started service"

requirements-completed: [HA-02, HA-03, HA-04, HA-06]

# Metrics
duration: 12min
completed: 2026-07-18
---

# Phase 82 Plan 01: Leader-Election Core Summary

**KubernetesClient LeaderElector wired into a build-only BackgroundService whose callbacks are the sole writer of a single-writer volatile LeaderState (follower default), pinned via CPM to a non-vulnerable 18.0.13.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-07-18T18:50:00Z
- **Completed:** 2026-07-18T19:02:35Z
- **Tasks:** 3
- **Files modified:** 4 (2 created, 2 modified)

## Accomplishments
- `KubernetesClient` is pinned in Central Package Management and referenced Orchestrator-only; `dotnet restore` resolves clean (no audit error).
- `LeaderState` — a lock-free, single-writer volatile snapshot holder defaulting to `IsLeader==false` / `Role=="follower"`, mutated post-construction only via the three writer methods.
- `LeaderElectionService` — a `BackgroundService` that constructs a `LeaderElector` over the `skp/orchestrator-leader` Lease with the fixed SPEC timings, wiring the election callbacks as the sole `LeaderState` writer; builds 0-warning in Debug and Release.

## Task Commits

Each task was committed atomically:

1. **Task 1: Add and pin the KubernetesClient dependency** - `d6fd3e2` (feat)
2. **Task 2: LeaderState — single-writer volatile snapshot holder** - `c2d06a9` (feat)
3. **Task 3: LeaderElectionService — BackgroundService running the LeaderElector** - `29cff1c` (feat)

## Files Created/Modified
- `Directory.Packages.props` - CPM `PackageVersion` pin for `KubernetesClient` 18.0.13
- `src/Orchestrator/Orchestrator.csproj` - `PackageReference` for `KubernetesClient` (no Version=, CPM)
- `src/Orchestrator/Election/LeaderState.cs` - `LeaderSnapshot` immutable record + `LeaderState` volatile holder (follower default; BecomeLeader/BecomeFollower/SetLeaderId writers)
- `src/Orchestrator/Election/LeaderElectionService.cs` - build-only `BackgroundService` running the `LeaderElector`; public timing/lease constants; callbacks are the sole `LeaderState` writer; OperationCanceledException clean-shutdown branch

## Decisions Made
- **KubernetesClient 18.0.13 over the plan's 15.x line** — see Deviations. The LeaderElector / LeaseLock / LeaderElectionConfig API the plan documents is identical across 15.x→18.x (`RunAndTryToHoldLeadershipForeverAsync`, `OnStartedLeading`/`OnStoppedLeading`/`OnNewLeader`, `LeaseLock(IKubernetes, ns, name, identity)`), so the fix-forward changed only the version, not the code.
- **Doc-comment crefs downgraded to `<c>` spans** for cross-assembly types (`IStartupGate`, `WorkflowFireJob`) to avoid CS1574 under warnings-as-errors — cosmetic, no behavioral effect.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fix-forwarded the KubernetesClient pin from 15.0.1 to 18.0.13**
- **Found during:** Task 1 (Add and pin the KubernetesClient dependency)
- **Issue:** The plan suggested the 15.x–16.x line; 15.0.1 (the current 15.x head) carries known moderate advisory GHSA-w7r3-mgwf-4mqq. The repo's `NuGetAudit` promotes `NU1902` to `Warning As Error`, so `dotnet restore` failed exit 1 — a hard block on the acceptance criterion "restore exits 0".
- **Fix:** Pinned `KubernetesClient` to the non-vulnerable **18.0.13** (stable, targets `net8.0`, same LeaderElector API). Restore then exited 0.
- **Files modified:** Directory.Packages.props
- **Verification:** `dotnet restore src/Orchestrator/Orchestrator.csproj` exit 0 with no NU1902; Debug + Release builds 0-warning.
- **Committed in:** d6fd3e2 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** The fix-forward was necessary to satisfy the plan's own "restore exits 0" acceptance under the repo's audit-as-error policy. The documented API is version-invariant across 15.x–18.x, so no source changed as a result. No scope creep.

## Issues Encountered
None beyond the audit-blocking version bump documented above.

## Known Stubs
None — both types are fully implemented. The `LeaderElectionService` is intentionally build-only (D-06): it is not registered in `Program.cs` in this plan (Plan 02 wires it in-cluster) and is never started by tests. This is a deliberate phase boundary, not a stub.

## Verification
- `dotnet restore src/Orchestrator/Orchestrator.csproj` → exit 0 (no NU1902 audit error on 18.0.13).
- `dotnet build src/Orchestrator/Orchestrator.csproj -c Debug` → **0 warnings / 0 errors**.
- `dotnet build src/Orchestrator/Orchestrator.csproj -c Release` → **0 warnings / 0 errors**.
- Single-writer invariant (HA-03): `grep BecomeLeader|BecomeFollower|SetLeaderId src/Orchestrator` shows the only *callers* are the three election callbacks in `LeaderElectionService.cs` (all other matches are the method definitions in `LeaderState.cs` and doc-comments).
- `RunAndTryToHoldLeadershipForeverAsync` appears exactly once; `catch (OperationCanceledException)` clean-shutdown branch present; constants `LeaseDuration`(15s) / `RenewDeadline`(10s) / `RetryPeriod`(2s) / `LeaseNamespace`("skp") / `LeaseName`("orchestrator-leader") present with `RenewDeadline < LeaseDuration`.

## Next Phase Readiness
- `LeaderState` is the foundation contract Plan 02 (fire gate + role enricher) reads and Plan 04 (hermetic tests) drives via the callbacks — both ready to consume.
- `LeaderElectionService` awaits Program.cs registration (in-cluster gated) in Plan 02 per D-02/D-06; live election behavior is Phase 83.

## Self-Check: PASSED

- FOUND: src/Orchestrator/Election/LeaderState.cs
- FOUND: src/Orchestrator/Election/LeaderElectionService.cs
- FOUND: .planning/phases/82-orchestrator-ha-leader-election/82-01-SUMMARY.md
- FOUND commits: d6fd3e2, c2d06a9, 29cff1c

---
*Phase: 82-orchestrator-ha-leader-election*
*Completed: 2026-07-18*
