---
phase: 82-orchestrator-ha-leader-election
plan: 04
subsystem: testing
tags: [kubernetes, leader-election, high-availability, orchestrator, hermetic-tests, xunit, otel, quartz, masstransit]

# Dependency graph
requires:
  - phase: 82-01-leader-election-core
    provides: LeaderState (single-writer volatile snapshot) + LeaderElectionService public timing constants
  - phase: 82-02-fire-gate-and-role-enricher
    provides: WorkflowFireJob IsLeader && hydrated fire gate, OrchestratorRoleLogEnricher, OrchestratorTestStubs.Leader()/ReadyGate() seams
provides:
  - LeaderStateTransitionTests — HA-02/HA-03/HA-04 single-writer transition + timing-fence proof
  - OrchestratorRoleEnricherTests — HA-05 role-enricher (follower/leader, never empty, live post-flip read) proof
  - WorkflowFireJobGateTests — HA-01 leader-only fire-gate proof (follower/cold-leader send nothing yet still refresh L1 + reschedule; hydrated leader sends)
affects: [83-failover-proof]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "D-06 hermetic election proof: drive the REAL LeaderState/enricher/WorkflowFireJob directly — no IKubernetes stub, no started LeaderElectionService; simulate OnStartedLeading/OnStoppedLeading via the BecomeLeader/BecomeFollower writers"
    - "Standby Quartz scheduler (never Start()ed) for deterministic trigger-reschedule inspection — a past-dated one-shot trigger cannot misfire the DI-constructed job on the scheduler thread"
    - "Advance FakeTimeProvider PAST the original occurrence between ScheduleAsync and Execute so the reschedule recomputes a DISTINCT next-fire time — proves RescheduleAsync ran, not just the pre-existing schedule"

key-files:
  created:
    - tests/BaseApi.Tests/Election/LeaderStateTransitionTests.cs
    - tests/BaseApi.Tests/Observability/OrchestratorRoleEnricherTests.cs
    - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobGateTests.cs
  modified: []

key-decisions:
  - "Follower/cold-leader reschedule asserted via a STANDBY scheduler + advanced FakeTimeProvider (12:05→12:10 move) rather than the analog's started scheduler — deterministic and side-effect-free (no background misfire of the DI-constructed WorkflowFireJob)"
  - "Zero-send asserted via harness.Consumed.Any<EntryStepDispatch>() == false (waits the harness inactivity timeout then returns false); positive send asserted via Consumed.Any == true + Single(dispatched)"
  - "attributes.role assertions key off the lowercase literal \"role\" (never an ExecutionLogScope.* const), matching the enricher's raw-literal key"

patterns-established:
  - "Election-callback simulation: tests invoke LeaderState.BecomeLeader()/BecomeFollower() directly (the SAME writers OnStartedLeading/OnStoppedLeading call) to prove the snapshot flip without a live elector"
  - "Live-read enricher proof: ONE OrchestratorRoleLogEnricher instance over a mutable LeaderState across two emits; a mid-life BecomeLeader flip surfaces on the second record"

requirements-completed: [HA-01, HA-02, HA-03, HA-04, HA-05]

# Metrics
duration: 19min
completed: 2026-07-18
---

# Phase 82 Plan 04: Hermetic HA Tests Summary

**Three hermetic test classes (10 tests) that prove the Phase-82 leader-election seams directly (D-06 — no live cluster, no IKubernetes stub): the leader-only WorkflowFireJob fire gate (HA-01), the single-writer LeaderState transitions + RenewDeadline<LeaseDuration fence (HA-02/03/04), and the live-read role enricher (HA-05) — all green, 0-warning Debug and Release.**

## Performance

- **Duration:** ~19 min
- **Started:** 2026-07-18T19:23:03Z
- **Completed:** 2026-07-18T19:41:43Z
- **Tasks:** 3
- **Files modified:** 3 (3 created, 0 modified)

## Accomplishments
- `LeaderStateTransitionTests` (4 facts) — default/unstarted is follower; `BecomeLeader→leader` / `BecomeFollower→follower` snapshot flip (simulating the election callbacks); off-cluster `startAsLeader` seed starts leader; `RenewDeadline < LeaseDuration` fence asserted with the exact 15/10/2s constants. No IKubernetes type referenced, no election service constructed/started.
- `OrchestratorRoleEnricherTests` (3 facts) — follower state stamps `attributes.role == "follower"`, leader stamps `"leader"`, each non-empty; a post-construction `BecomeLeader` flip surfaces on the NEXT record with the SAME enricher instance (live per-record read, D-09). Real OTel logger provider + downstream `CapturingProcessor` (mirrors `ProcessorIdEnricherTests`).
- `WorkflowFireJobGateTests` (3 facts) — follower (hydrated) → ZERO `EntryStepDispatch` sends but L1 liveness refresh AND trigger reschedule still ran; leader AND hydrated → the entry-step dispatch fires; cold leader (leader but NOT hydrated) → ZERO sends, ungated tail still refreshes L1 (D-05). Real `WorkflowFireJob` + real `WorkflowL1Store` + in-memory MassTransit harness + `FakeTimeProvider` + standby Quartz scheduler.
- Test assembly builds **0-warning Debug AND Release**; all 10 new tests green.

## Task Commits

Each task was committed atomically (normal commits, hooks on):

1. **Task 1: LeaderState single-writer transition + timing-fence tests (HA-02/03/04)** — `620168c` (test)
2. **Task 2: OrchestratorRoleLogEnricher role-stamp tests (HA-05)** — `cd4fe68` (test)
3. **Task 3: WorkflowFireJob leader-only fire-gate tests (HA-01)** — `36a10bb` (test)

## Files Created/Modified
- `tests/BaseApi.Tests/Election/LeaderStateTransitionTests.cs` — HA-02/03/04 LeaderState transitions + RenewDeadline<LeaseDuration fence (drives SUT directly, D-06)
- `tests/BaseApi.Tests/Observability/OrchestratorRoleEnricherTests.cs` — HA-05 role enricher (follower/leader/live-flip), real OTel provider + CapturingProcessor
- `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobGateTests.cs` — HA-01 leader-only fire gate (follower/leader/cold-leader), in-memory MassTransit harness + standby Quartz scheduler

## Decisions Made
- **Standby Quartz scheduler for reschedule inspection** — the analog (`FireDispatchTests`) starts the scheduler and only asserts liveness/dispatch. To assert the follower's *reschedule* side-effect deterministically, this plan leaves the scheduler in standby (never `Start()`ed) so the past-dated one-shot trigger cannot misfire the DI-constructed `WorkflowFireJob` on the scheduler thread. `GetTrigger` still returns the stored, recomputed next-fire time. Combined with advancing `FakeTimeProvider` 12:00→12:06, the follower reschedule is proven by the trigger moving 12:05→12:10.
- **Election-callback simulation via the writer methods** — per D-06 the tests never construct/start `LeaderElectionService`; they call `LeaderState.BecomeLeader()`/`BecomeFollower()` directly (the exact writers `OnStartedLeading`/`OnStoppedLeading` call), so the snapshot transition is proven without a live elector.
- **Reused the Plan-02 seams** — `OrchestratorTestStubs.ReadyGate()` supplies the hydrated `StartupGate`; the follower/cold-leader gates construct `new LeaderState()` / `new StartupGate()` inline. No seam duplication.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] xUnit2031: Assert.Single misuse in the enricher role helper**
- **Found during:** Task 2 (OrchestratorRoleEnricherTests)
- **Issue:** `Assert.Single(collection.Where(predicate))` trips the xUnit analyzer rule xUnit2031 (use the filtering overload), which the repo's warnings-as-errors promotes to a build error.
- **Fix:** Switched to the filtering overload `Assert.Single(capture.LastAttributes, kvp => kvp.Key == "role")`.
- **Files modified:** tests/BaseApi.Tests/Observability/OrchestratorRoleEnricherTests.cs
- **Verification:** Debug + Release build 0-warning; the 3 enricher tests pass.
- **Committed in:** `cd4fe68` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 analyzer-as-error bug). No scope creep — a mechanical assertion-shape fix to satisfy the repo's warnings-as-errors gate; test intent unchanged.

## Issues Encountered
- The full `--filter-not-trait Category=RealStack` run reports 283/866 failures — ALL compose-backed integration tests (`Composition`/`Controllers`/`Features.Orchestration`; `RedisFixture` connects to `localhost:6380`, fails-loud by design when compose is down). This is the pre-existing Docker-less-sandbox infra baseline (same as phases 68/72/73/74/75/79), NOT introduced by this plan (which adds only pure hermetic unit tests and touches no SUT code). Logged to `deferred-items.md`. The three new Phase-82 classes are 100% green in isolation and together.

## User Setup Required
None - no external service configuration required.

## Known Stubs
None — all three test files are fully implemented and green.

## Verification
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` → **0 warnings / 0 errors**.
- `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release` → **0 warnings / 0 errors**.
- `BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-class ...LeaderStateTransitionTests` → 4/4 pass.
- `... --filter-class ...OrchestratorRoleEnricherTests` → 3/3 pass.
- `... --filter-class ...WorkflowFireJobGateTests` → 3/3 pass.
- All three classes together → **10/10 pass**.
- Full hermetic run: the only failures are the pre-existing compose-backed integration tests (infra down) — see Issues Encountered / deferred-items.md.

## Next Phase Readiness
- Phase 82's acceptance is closed hermetically: HA-01..HA-05 proven green + 0-warning Debug/Release build. All live election/failover behavior is deferred to Phase 83 (failover proof), which will exercise the same LeaderState/gate/enricher under a real KubernetesClient LeaderElector.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Election/LeaderStateTransitionTests.cs
- FOUND: tests/BaseApi.Tests/Observability/OrchestratorRoleEnricherTests.cs
- FOUND: tests/BaseApi.Tests/Orchestrator/WorkflowFireJobGateTests.cs
- FOUND commits: 620168c, cd4fe68, 36a10bb

---
*Phase: 82-orchestrator-ha-leader-election*
*Completed: 2026-07-18*
