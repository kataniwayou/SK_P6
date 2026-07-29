---
phase: quick-260729-fg3
plan: 01
subsystem: orchestrator-hydration
tags: [start-reload, l1-store, quartz, redis, partial-failure, build-then-commit]
requires: []
provides:
  - "Start reload is BUILD-then-COMMIT: every Redis read completes into a local WorkflowL1 before the first mutation"
  - "L1 is never absent during a reload — the commit Upserts old->new in place, so a concurrent ExecutionResult on the same pod cannot fall into an L1 hole"
  - "A Redis fault in the read half leaves the prior L1 entry AND the prior Quartz job intact, and propagates for clean redelivery"
  - "WorkflowLifecycle.TeardownAsync deleted (zero remaining references in src/ or tests/)"
  - "FaultAfterNReadsL2 stub — places a Redis fault at an exact point of the hydration read sequence"
affects:
  - src/Orchestrator/Hydration/WorkflowLifecycle.cs
  - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
  - src/Orchestrator/Hydration/HydrationBackgroundService.cs
tech-stack:
  added: []
  patterns:
    - "Build-then-commit lifecycle: a private pure-read half returning a nullable value object (null = business skip) composed with a private mutate-only half, so a partial read can never leave in-memory state half-applied"
    - "TryGet-guarded old-JobId unschedule inside the commit half — preserves orphan-job cleanup under a fresh-JobId-per-Upsert projection root while being a natural no-op on an empty (boot) L1"
key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/StartReloadNonDestructiveTests.cs
  modified:
    - src/Orchestrator/Hydration/WorkflowLifecycle.cs
    - src/Orchestrator/Consumers/StartOrchestrationConsumer.cs
    - src/Orchestrator/Consumers/StopOrchestrationConsumer.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
    - tests/BaseApi.Tests/Orchestrator/StartConsumerLifecycleTests.cs
  deleted: []
decisions:
  - "Kept HydrateAndScheduleAsync's name/signature and made the split private, so all seven existing call sites (HydrationBackgroundService + five test files) compile and behave identically with no edits"
  - "The commit half unschedules the OLD JobId read from L1 BEFORE the Upsert — ScheduleAsync is a bare ScheduleJob ADD that throws ObjectAlreadyExistsException on a live JobKey, and doing it pre-Upsert keeps the unscheduled window to two adjacent in-memory Quartz calls"
  - "Reworded the StopOrchestrationConsumer inline comment that named TeardownAsync — required to make the zero-remaining-references verification true; the file is otherwise untouched"
  - "Proved gate teeth by reverting only the three orchestrator source files to HEAD, rebuilding, and observing all 3 new tests FAIL for the predicted reasons, then restoring — RED before GREEN without weakening anything"
  - "BuildAsync keeps its unused CancellationToken parameter for signature symmetry with CommitAsync; adding a ThrowIfCancellationRequested was rejected as an unrequested behavior change (build is zero-warning either way)"
requirements-completed: [QUICK-260729-fg3]
metrics:
  duration: ~75 min
  completed: 2026-07-29
commit: 87bdf42
---

# Quick Task 260729-fg3: Close the Start-reload teardown-first gap — Summary

**A Start reload now BUILDS then COMMITS instead of DESTROYING then REBUILDING: L1 swaps old→new in place (never absent), and a Redis fault mid-build mutates nothing.**

## Performance

- **Duration:** ~75 min (dominated by two full hermetic suite runs, ~20 min each)
- **Tasks:** 3 of 3
- **Files modified:** 6 (5 modified, 1 created)
- **Commit:** `87bdf42`

## Accomplishments

1. **Transient L1 hole eliminated.** `StartOrchestrationConsumer` no longer calls `TeardownAsync`
   (`scheduler.UnscheduleAsync` + `store.Remove`) before hydrating. `store.Remove` is gone from the
   reload path entirely, so the `1 + N` Redis round-trip window in which the workflow was ABSENT from
   L1 no longer exists. Any `IStepResult` handled by that pod during a reload now resolves in L1
   instead of hitting the `OrchestratorPrePipeline` miss that acked it as "completed-unresolved" and
   incremented `orchestrator_step_unresolved` — a silently lost continuation. `WorkflowFireJob` missed
   the same way and is fixed by the same change.

2. **Destructive partial failure eliminated.** `WorkflowLifecycle.HydrateAndScheduleAsync` is now
   composed of a private pure-read `BuildAsync` (returns the L1 entry, or `null` for a business skip;
   contains zero `store.` and zero `scheduler.` calls) and a private mutate-only `CommitAsync`. A
   Redis fault anywhere in the read half — including mid-BFS, which had no coverage at all before —
   leaves both the prior L1 entry and the prior Quartz job untouched and propagates for a clean
   redelivery retry.

3. **The necessary half of teardown survives.** `CommitAsync` keeps the `TryGet`-guarded
   `scheduler.UnscheduleAsync(old.JobId, ct)`. Because `RedisProjectionWriter` mints a fresh `JobId`
   into the L2 root on every Upsert, only the L1 entry knows the old JobId — without this delete the
   fresh-JobId-per-Upsert root would accumulate orphan Quartz jobs. `TeardownAsync` itself is deleted
   with zero remaining references.

4. **Both properties pinned hermetically, with proven teeth.** Three new test cases fail against the
   old teardown-first code and pass against the new code (see Gate Evidence).

## Task Commits

Single commit as the plan specified (Task 1 is a measurement task that modifies no repo file; Tasks 2
and 3 ship together because Task 3's tests are the acceptance gate for Task 2's refactor):

1. **Task 1: Capture the pre-change baseline** — no commit (scratch-dir artifact only)
2. **Tasks 2 + 3: build/commit split, TeardownAsync deletion, and the two regression guards** —
   `87bdf42` (fix)

## Files Created/Modified

- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` — `HydrateAndScheduleAsync` is now
  `BuildAsync` → (null? return) → `CommitAsync`. `BuildAsync` holds the verbatim read sequence (root
  `StringGetAsync`, business skips, BFS over the step graph, liveness computation, `WorkflowL1`
  construction) with every `return;` becoming `return null;` and every log string preserved
  byte-for-byte. `CommitAsync` is three statements with no I/O between them: guarded old-JobId
  unschedule → `store.Upsert` → `scheduler.ScheduleAsync`. `TeardownAsync` deleted; the
  `UnscheduleOnlyAsync` doc comment retargets its contrast onto the reload's commit half rather than
  dropping the D-07 drain rationale; the class doc comment now states the real shape.
- `src/Orchestrator/Consumers/StartOrchestrationConsumer.cs` — the loop body is the log line plus one
  `lifecycle.HydrateAndScheduleAsync` call. Still conditionless: no existence skip, no per-workflow
  stripe, no try/catch. Both stale comment blocks rewritten (the old inline comment asserted the
  transient remove "is harmless", which was exactly the defect).
- `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs` — one inline comment reworded off the
  deleted symbol name. No code change.
- `tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs` — new `FaultAfterNReadsL2(values,
  okReads, out db)`: N reads resolve like `PresentL2`, the next throws
  `RedisConnectionException(UnableToConnect)` so `WorkflowLifecycle.IsInfra` classifies it INFRA.
- `tests/BaseApi.Tests/Orchestrator/StartReloadNonDestructiveTests.cs` (new, 181 lines) — the two
  regression guards.
- `tests/BaseApi.Tests/Orchestrator/StartConsumerLifecycleTests.cs` — prose only; all three Facts keep
  their exact assertion bodies.

## Gate Evidence

**Falsification (RED) — the new tests have teeth.** The three orchestrator source files were reverted
to HEAD (`git checkout --` on those paths only), rebuilt, and the new test class run:

```
failed StartReload_L1ResolvableAtEveryRedisRoundTrip
  Assert.All() Failure: 2 out of 2 items in the collection did not pass.
       Error: L1 entry was absent (or empty) during a reload round-trip
       Error: L1 entry was absent (or empty) during a reload round-trip
failed StartReload_BuildFault_LeavesPriorL1AndScheduleIntact(okReads: 0)
failed StartReload_BuildFault_LeavesPriorL1AndScheduleIntact(okReads: 1)
  total: 3  failed: 3  succeeded: 0
```

BOTH probes of the reload returned -1 under the old ordering — the L1 hole, observed directly. The
sources were then restored and the same run is `total: 3  failed: 0`.

**Hermetic name-set diff (never counts).**

| | Baseline (pre-change, HEAD 7352fe5) | After |
|---|---|---|
| total | 763 | 766 (+3 new cases) |
| failed | 20 | 20 |
| succeeded | 743 | 746 |
| skipped | 0 | 0 |

`comm` of the two sorted failing-test NAME SETS: **zero new names, zero disappeared names** — the two
sets are identical. All 20 are the documented pre-existing baseline (18 dead `ComposeYamlFacts` +
`ErrorMappingFacts.Delete_Step_Referenced_By_Workflow_Returns422` +
`ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak`, the two
live-infra tests). Baseline set stored at
`<scratchpad>/fg3-baseline-failures.txt`, after-set at `<scratchpad>/fg3-after-failures.txt`.

**Named suites, re-run individually against the final binaries:**

| Suite | total | failed |
|---|---|---|
| StartConsumerLifecycleTests | 3 | 0 |
| StopConsumerLifecycleTests | 2 | 0 |
| HydrationTests | 2 | 0 |
| MultiStepHydrationCascadeTests | 4 | 0 |
| ResumeAllConsumerTests | 3 | 0 |
| ResumeNoBurstTests | 2 | 0 |
| StartReloadNonDestructiveTests | 3 | 0 |
| (whole `BaseApi.Tests.Orchestrator` namespace) | 114 | 0 |

**Static checks:**

- `dotnet build SK_P.sln -c Release` → **0 Warning(s), 0 Error(s)** (`TreatWarningsAsErrors=true`).
- `grep -rn "TeardownAsync" src/ tests/ --include=*.cs` (minus bin/obj) → **0 hits**.
- `BuildAsync` body contains no `store.` and no `scheduler.` call (read by hand, not by whole-file grep).
- `StartOrchestrationConsumer.Consume` contains exactly one `lifecycle.` call.
- `HydrationBackgroundService.cs:55` unchanged, still calls `HydrateAndScheduleAsync`.
- `git diff src/Orchestrator/Scheduling/WorkflowScheduler.cs` → **empty**.
- `UnscheduleOnlyAsync` and `ResumeAsync` bodies unchanged (doc-comment-only delta on the former).
- `git status --short` after the commit still shows every unrelated dirty path unstaged and
  uncommitted; the commit contains exactly 6 files and deletes no tracked file.

## Decisions Made

- **Public surface frozen, split kept private.** `HydrateAndScheduleAsync` keeps its name and
  signature so `HydrationBackgroundService` and all five existing test files needed no edit. The
  build/commit halves are private implementation detail.
- **Unschedule before Upsert, TryGet-guarded.** Before, because `ScheduleAsync` is a bare
  `ScheduleJob` ADD that throws `ObjectAlreadyExistsException` on a live JobKey (the exact case in the
  hermetic tests, whose stub root carries a FIXED jobId). Guarded, because on boot L1 is empty — so
  startup hydration is provably unchanged.
- **Stop's comment reworded.** The plan scoped this to "only if it fails to compile", but the
  verification requires zero `TeardownAsync` references anywhere in `src/` or `tests/`; the plan
  pre-authorized adding that file to the commit for exactly this case.
- **`BuildAsync` keeps an unused `ct`.** Consuming it via `ThrowIfCancellationRequested` would have
  introduced an unrequested cancellation behavior change; the parameter is retained for symmetry and
  the Release build is zero-warning with it unused.

## Deviations from Plan

**1. [Rule 3 - Blocking] Reworded the `TeardownAsync` mention in `StopOrchestrationConsumer.cs`**
- **Found during:** Task 2 Part B
- **Issue:** The plan gated this edit on "only if it fails to compile or references the deleted symbol
  in a `<see cref/>`" — it is a plain `//` comment, so it compiles. But verification item 2 requires
  `grep -rn "TeardownAsync" src/ tests/` to return ZERO hits, which that comment would violate.
- **Fix:** Applied the plan's own suggested wording ("Do NOT use the Start reload's commit half (it
  replaces L1)"). No code change; the rest of the file is untouched.
- **Files modified:** `src/Orchestrator/Consumers/StopOrchestrationConsumer.cs`
- **Verification:** grep returns 0 hits; `StopConsumerLifecycleTests` 2/2 pass.
- **Committed in:** `87bdf42` (the plan explicitly pre-authorized staging this file in this case)

**2. [Process] Added an explicit RED phase not sequenced in the plan**
- **Found during:** Task 3
- **Issue:** The plan orders implementation (Task 2) before the tests (Task 3), so the new guards
  would only ever have been observed GREEN — leaving "do these tests actually have teeth?" unproven.
- **Fix:** After the tests passed, the three orchestrator source files were reverted to HEAD with a
  path-scoped `git checkout --` (backed up to the scratch dir first), rebuilt, and the new class run
  to confirm 3/3 FAIL for the predicted reasons; then restored and re-verified GREEN. No test was
  altered in either direction.
- **Files modified:** none (temporary, fully reverted)
- **Verification:** see Gate Evidence.

---

**Total deviations:** 2 (1 × Rule 3, 1 × process hardening)
**Impact on plan:** None on scope. Deviation 1 was required to satisfy the plan's own verification;
deviation 2 strengthened evidence without changing any artifact.

## Issues Encountered

- **The hermetic suite takes ~20 minutes and cannot be run as a tracked foreground task.** Several
  hermetic-marked tests spin up a MassTransit bus against an unreachable RabbitMQ and emit ~265k lines
  of reconnect stack traces before timing out. The first attempt was reaped at the 10-minute tool
  timeout. Resolution: run the binary detached and wait on the process (`Wait-Process`) rather than on
  the tool. Both the baseline and the after-run were captured this way.
- **Failing-test names carry a duration suffix** (`... (14s 465ms)`), which makes a naive `sort`/`comm`
  diff report spurious changes. Both name sets are normalized by stripping the trailing duration
  before comparison.

## Threat Verification

| Threat ID | Disposition | Status |
|---|---|---|
| T-fg3-01 (lost continuations via the L1 hole) | mitigate | **Closed.** `store.Remove` is absent from the reload path; `StartReload_L1ResolvableAtEveryRedisRoundTrip` probes L1 on every Redis round-trip and requires ≥ 2 probes (2 observed) so it cannot pass vacuously. |
| T-fg3-02 (permanent per-pod outage after a mid-build fault) | mitigate | **Closed.** Both theory cases (`okReads: 0` root fault, `okReads: 1` mid-BFS fault) assert the exception propagates and that the prior entry, its Steps, its JobId, and the prior JobKey all survive. |
| T-fg3-03 (orphaned Quartz jobs if the unschedule were dropped) | mitigate | **Closed.** `CommitAsync` retains the guarded `UnscheduleAsync(old.JobId)`; the fixed-jobId stub makes the reload collide on one JobKey, so a dropped unschedule surfaces as `ObjectAlreadyExistsException` — `StartAlreadyInL1_ReHydratesAndReschedules_NoSkip` passes. |
| T-fg3-04 (collateral breakage of the Stop keep-L1 drain) | mitigate | **Closed.** `UnscheduleOnlyAsync` body byte-unchanged; `StopConsumerLifecycleTests` (2/2) and `StopThenStart_RevivesLiveJob` pass. |
| T-fg3-05 (boot-path regression) | accept | **Verified benign.** Empty L1 → the guard skips the unschedule; `HydrationTests` (2/2) and `MultiStepHydrationCascadeTests` (4/4) pass. |
| T-fg3-06 (unrelated working-tree changes leaking into the commit) | mitigate | **Closed.** Staged by explicit path only; commit contains exactly 6 files; all 7 unrelated modified tracked paths and ~110 untracked artifacts remain unstaged. |
| T-fg3-07 (silent test weakening) | mitigate | **Closed.** Name-set diff (not counts) is identical to baseline; `StartConsumerLifecycleTests` assertions byte-unchanged (prose-only delta); no test was skipped, deleted, or retargeted. |
| T-fg3-08 (information disclosure) | accept | No new input surface; every log string preserved byte-for-byte. |
| T-fg3-SC (package legitimacy) | accept | No package installs; no dependency change. |

## Known Stubs

None. `FaultAfterNReadsL2` is a test double, not production scaffolding.

## Next Readiness

The orchestrator's reload path is now atomic from a reader's perspective. Two follow-ups are visible
but explicitly out of scope:

- The same non-destructive property is now worth asserting **live** — a Start reload issued while
  `IStepResult` traffic is in flight should show `orchestrator_step_unresolved` flat. That belongs in a
  live-gate scenario, not a hermetic test.
- An emergent (unscoped) improvement: on a Redis-backoff RETRY pass, `HydrationBackgroundService`
  previously risked `ObjectAlreadyExistsException` when re-scheduling an already-hydrated workflow.
  The new `TryGet` guard removes that hazard for free. Not separately tested here.

## Self-Check: PASSED

All 6 committed files exist on disk; commit `87bdf42` exists in `git log`;
`StartReloadNonDestructiveTests.cs` is 181 lines (min 90); `WorkflowLifecycle.cs` contains
`BuildAsync`; `OrchestratorTestStubs.cs` contains `FaultAfterNReadsL2`; both
`StartOrchestrationConsumer.cs` and `HydrationBackgroundService.cs` call
`lifecycle.HydrateAndScheduleAsync`, and the Start consumer contains exactly one `lifecycle.` call.

---
*Quick task: 260729-fg3*
*Completed: 2026-07-29*
