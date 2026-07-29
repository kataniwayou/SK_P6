---
phase: quick-260729-cfj
plan: 01
subsystem: orchestrator-messaging
tags: [dead-code-removal, fan-out-endpoints, quartz, ha-07]
requires: []
provides:
  - "Orchestrator declares TWO per-replica exclusive fan-out queues (was three)"
  - "Messaging.Contracts free of the publisher-less per-workflow pause/resume control pair"
affects:
  - src/Orchestrator/Program.cs
  - src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs
  - src/Orchestrator/Scheduling/WorkflowScheduler.cs
  - src/Orchestrator/Hydration/WorkflowLifecycle.cs
tech-stack:
  added: []
  patterns: ["group-level Quartz pause + post-reschedule ResumeAll group-flag clear (GAP-49-2 / D-08 ordering) mirrored in tests"]
key-files:
  created: []
  modified:
    - src/Orchestrator/Program.cs
    - src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs
    - src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs
    - src/Orchestrator/Hydration/WorkflowLifecycle.cs
    - src/Orchestrator/Scheduling/WorkflowScheduler.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs
    - tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs
    - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
    - tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs
  deleted:
    - src/Messaging.Contracts/PauseWorkflow.cs
    - src/Messaging.Contracts/ResumeWorkflow.cs
    - src/Orchestrator/Consumers/PauseWorkflowConsumer.cs
    - src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs
    - src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
    - src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs
    - tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs
    - tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs
decisions:
  - "Retargeted the three surviving PauseAsync test call sites onto the live PauseAllAsync seam rather than deleting the tests"
  - "ResumeReschedulesFresh gained an explicit ResumeAllGroupsAsync call after the fresh reschedule, mirroring the live ResumeAllConsumer ordering"
  - "A new hermetic failure was proven pre-existing by building and running the unmodified HEAD tree, not by adjusting the test"
metrics:
  duration: ~50 min
  completed: 2026-07-29
commit: 7db43d6
---

# Quick Task 260729-cfj: Remove the dead PauseWorkflow/ResumeWorkflow fan-out pair — Summary

Deleted the publisher-less per-workflow `PauseWorkflow`/`ResumeWorkflow` control-message pair — 2 contracts, 2 consumers, 2 consumer definitions, 2 `Program.cs` registrations, 1 endpoint base const, 2 orphaned scheduler/lifecycle seams and 2 whole test files — collapsing the orchestrator from three to two per-pod exclusive fan-out queues, with the live global `PauseAll`/`ResumeAll` path behaviorally untouched.

## What Was Done

**Task 1 — baseline + message/consumer/endpoint layer removal.** Captured the pre-change baseline (below), then `git rm`'d the eight dead files and edited four survivors: `Program.cs` lost the `PAUSE-02/03/04` block and both consumer registrations (this is the edit that drops the third per-pod queue); `OrchestratorFanoutEndpoints` lost the `PauseResumeBase` const and had its doc corrected three→two bases, six→four definitions, and the co-location pair list trimmed; `PauseAllConsumerDefinition` had one stale doc line rewritten (zero code change); `OrchestratorFanoutEndpointsFacts` had the dead base removed from `AllBases` and its three `PauseResumeBase` assertions dropped, with the `ThreeBases` Fact renamed to `TwoBases`.

**Task 2 — orphaned seams + test retargeting.** Deleted `WorkflowLifecycle.PauseOnlyAsync` and `WorkflowScheduler.PauseAsync`. The originating investigation had missed that `PauseAsync` also had two callers in `ResumeAllConsumerTests`, so three surviving test call sites were retargeted onto the live `PauseAllAsync` seam rather than deleted.

**Task 3 — doc correction + gate + commit.** Rewrote the `AtLeastOnceStructuralFacts` FACT A rationale paragraph (prose only, no assertion line touched) so it justifies reflection-over-string-scan on its own merits instead of citing a worked example that no longer exists.

## Verification Results

| Gate | Result |
|------|--------|
| 1. `dotnet build SK_P.sln -c Release` | Success, **0 warnings**, 0 errors (`TreatWarningsAsErrors=true`) |
| 2. `PauseWorkflow`/`ResumeWorkflow` under src/ + tests/ | **Zero occurrences** (comments included) |
| 3. `PauseResumeBase` excluding `GlobalPauseResumeBase` | **Zero occurrences** |
| 4. `orchestrator-pauseresume` excluding `global-pauseresume`, across src/ tests/ k8s/ scripts/ | **Zero occurrences** |
| 5. Hermetic suite | No regression — see below |

### Hermetic suite: baseline vs. post-change

| | total | passed | failed | skipped | duration |
|---|---|---|---|---|---|
| **Baseline (Task 1, pre-change)** | 768 | 748 | 20 | 0 | 13m 08s |
| **Post-change (Task 3)** | 763 | 743 | 20 | 0 | 12m 55s |

The 5-test drop is exactly accounted for: `PauseResumeContractTests` (4 Facts) + `PauseResumeConsumerTests` (1 Fact). No Fact was lost anywhere else — `PauseResumeSchedulingTests` (3), `ResumeAllConsumerTests` (3), `OrchestratorFanoutEndpointsFacts` (4) and `AtLeastOnceStructuralFacts` (2) all retain their original Fact counts, and none appear in the failed set.

### Failing-test NAME-SET diff (not counts)

Both runs failed 20, but the sets are not identical:

- **Appeared:** `BaseApi.Tests.Middleware.ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak`
- **Disappeared:** `BaseApi.Tests.Observability.LogLevelFilterTests.Test_Information_Log_Suppressed_When_Default_Warning`
- **Unchanged:** 18 `ComposeYamlFacts` (the documented dead-baseline block) + `ErrorMappingFacts.Delete_Step_Referenced_By_Workflow_Returns422`

Per the plan, a same-count-but-different-names result is treated as a potential regression, so the appeared failure was investigated rather than accepted:

1. It reproduced in isolation (`--filter-class`), so it was not load jitter.
2. It has **no dependency on anything in this diff** — it uses `PostgresFixture` + `WebAppFactory` (EF/Postgres `xmin` + the HTTP error-mapping middleware) and references neither Orchestrator, Quartz, nor the deleted contracts.
3. **Decisive proof:** the unmodified `HEAD` tree was exported via `git archive HEAD | tar -x` into the scratchpad, built clean (0 warnings), and the same test **failed identically there** (`Assert.NotNull() Failure: Value is null` at line 85 — neither racing POST returned 409).

So the failure is a pre-existing environment-dependent flake in a live-DB race test, present on pre-change code, not a regression. `LogLevelFilterTests` flipping the other way (fail→pass) over the same interval is corroborating evidence of bidirectional environment instability in this suite. **No test was weakened, skipped, deleted or adjusted.**

## Deviations from Plan

**1. [Rule 1 — Bug] The plan's own Program.cs replacement text would have failed gate 4**

- **Found during:** Task 3, gate check 4
- **Issue:** Task 1(a) directs the surviving global-pair comment to "state that the old per-workflow `orchestrator-pauseresume` endpoint has now been removed". Writing that literally leaves the string `orchestrator-pauseresume` in `Program.cs`, which gate 4 asserts must have zero occurrences outside `global-pauseresume`. The first phrasing did exactly that and gate 4 caught it.
- **Fix:** Reworded to "the old per-workflow pause/resume endpoint" — same meaning, no literal queue name. Rebuilt and re-ran gate 4 clean.
- **Files modified:** `src/Orchestrator/Program.cs`
- **Note:** the same trap applies to `PauseAsync` in prose (Task 2's done-criteria greps `\bPauseAsync\b`), so the retargeted test doc-comments were worded to avoid that identifier too.

Everything else executed exactly as written. In particular, **the `ResumeReschedulesFresh` group-clear mirror worked exactly as the plan specified** — adding `await sut.ResumeAllGroupsAsync(ct);` after the `UnscheduleAsync` → `ScheduleAsync` pair kept all four existing assertions (single trigger, `Normal`, non-null future next-fire) passing unchanged, confirming the plan's Quartz 3.18 `pausedTriggerGroups` analysis was correct. No fallback was needed.

## Live Path Preserved

`PauseAllConsumer`/`ResumeAllConsumer` and their definitions, `GlobalPauseResumeBase`, `PauseAllAsync`, `ResumeAllGroupsAsync`, `GetTriggerStateAsync`, `WorkflowLifecycle.ResumeAsync`, the Keeper's `BitHealthLoop` publishes, `LifecycleBase`, the `orchestrator-result*` competing-consumer endpoints, `SC3PauseResumeOutageE2ETests` and `PauseAllConsumerTests` are all untouched. `TriggerState.Paused` is still produced by the global `PauseAll()` path, so `ResumeAsync`'s `== Paused` guard remains reachable and meaningful — now proven by `PauseResumeSchedulingTests` driving that very seam.

No broker cleanup is required: the removed queues were MassTransit `Temporary` (auto-delete) and vanish when the orchestrator pods restart.

## Known Stubs

None.

## Commit

`7db43d6` — `refactor: remove the dead per-workflow PauseWorkflow/ResumeWorkflow fan-out pair` (17 files: 8 deletions + 9 modifications, +43/-359)

The unrelated dirty working-tree paths (`src/Keeper/Recovery/ReinjectConsumer.cs`, `SK_P.sln`, `scripts/phase-67-harness.ps1`, `analyzer-reports/*.json`, `.planning/TTL-BELOW-BREAK-RUNPLAN.md` and ~110 untracked artifacts) were never staged and remain uncommitted.

## Self-Check: PASSED

- All 8 deleted paths confirmed absent from the working tree and recorded as `D` in commit `7db43d6`.
- All 9 modified paths confirmed present and recorded as `M` in commit `7db43d6`.
- Commit `7db43d6` confirmed in `git log`.
