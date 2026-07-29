---
phase: quick-260729-cfj
plan: 01
type: execute
wave: 1
depends_on: []
autonomous: true
requirements: [QUICK-260729-cfj]
files_modified:
  # deleted (whole file)
  - src/Messaging.Contracts/PauseWorkflow.cs
  - src/Messaging.Contracts/ResumeWorkflow.cs
  - src/Orchestrator/Consumers/PauseWorkflowConsumer.cs
  - src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs
  - src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
  - src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs
  - tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs
  - tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs
  # edited
  - src/Orchestrator/Program.cs
  - src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs
  - src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs
  - src/Orchestrator/Hydration/WorkflowLifecycle.cs
  - src/Orchestrator/Scheduling/WorkflowScheduler.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs
  - tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs
  - tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
  - tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs

must_haves:
  truths:
    - "No symbol named PauseWorkflow or ResumeWorkflow exists anywhere under src/ or tests/"
    - "The orchestrator declares exactly TWO per-replica fan-out endpoint bases (LifecycleBase, GlobalPauseResumeBase) — the third per-pod exclusive queue is gone"
    - "The global PauseAll/ResumeAll control path is byte-unchanged in behavior and still proven by the surviving hermetic tests"
    - "WorkflowLifecycle.ResumeAsync and WorkflowScheduler.GetTriggerStateAsync remain live and still observe TriggerState.Paused produced by the global PauseAll path"
    - "Release build of the whole solution succeeds with ZERO warnings (TreatWarningsAsErrors=true makes any warning a build failure)"
    - "Hermetic suite failure count equals the pre-change baseline captured in Task 1 — no new failures"
  artifacts:
    - path: "src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs"
      provides: "Fan-out endpoint base-name SoT, reduced to two bases"
      contains: "GlobalPauseResumeBase"
    - path: "src/Orchestrator/Scheduling/WorkflowScheduler.cs"
      provides: "Live scheduler seams after PauseAsync removal"
      contains: "PauseAllAsync"
    - path: "tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs"
      provides: "HA-07 RESOURCE_LOCKED regression guard, still guarding two bases"
      contains: "PerInstance_TwoDifferentInstanceIds_YieldDistinctNames"
    - path: "tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs"
      provides: "Live global resume proof, retargeted onto the live group-level pause"
      contains: "PauseAllAsync"
  key_links:
    - from: "src/Orchestrator/Consumers/ResumeAllConsumer.cs"
      to: "WorkflowScheduler.ResumeAllGroupsAsync"
      via: "post-loop group-flag clear (must remain untouched)"
      pattern: "ResumeAllGroupsAsync"
    - from: "src/Orchestrator/Hydration/WorkflowLifecycle.cs"
      to: "WorkflowScheduler.GetTriggerStateAsync"
      via: "ResumeAsync == Paused guard (must remain untouched)"
      pattern: "GetTriggerStateAsync"
    - from: "src/Orchestrator/Program.cs"
      to: "OrchestratorFanoutEndpoints.GlobalPauseResumeBase"
      via: "per-instance endpoint name for the surviving global pair"
      pattern: "GlobalPauseResumeBase"
---

<objective>
Remove the per-workflow `PauseWorkflow`/`ResumeWorkflow` fan-out control-message pair from the
orchestrator. It has had no publisher anywhere in `src/` since commit `5f0e210` deleted the reactive
`Fault<T>` recovery handler in the Keeper. It survived the Phase 48 teardown on a FALSE premise —
`.planning/milestones/v4.0.0-phases/48-v3-x-teardown/48-RESEARCH.md:117` and its Pitfall 3 label the
pair "v4 BIT-gate contracts" on the "live pause/resume path and the BitHealthLoop publish", which is a
conflation with the SEPARATE and genuinely LIVE global `PauseAll`/`ResumeAll` pair. `BitHealthLoop`
publishes `PauseAll`/`ResumeAll`, never `PauseWorkflow`/`ResumeWorkflow`.

Purpose: delete a dead message contract, two dead consumers, two dead consumer definitions, two dead
scheduler/lifecycle seams, and — the operationally meaningful part — the THIRD per-pod exclusive
`orchestrator-pauseresume-{instanceId}` fan-out queue that every orchestrator replica declares for
messages that can never arrive.

Output: the dead unit removed, the live global pause/resume path untouched and still proven, a
zero-warning Release build, and a hermetic suite with no new failures relative to a captured baseline.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md

Sources the executor will edit — read only the ranges named in each task, do not re-read.

@src/Orchestrator/Program.cs
@src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs
@src/Orchestrator/Scheduling/WorkflowScheduler.cs
@src/Orchestrator/Hydration/WorkflowLifecycle.cs
@tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs
@tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs
@tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
@tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs

<interfaces>
<!-- Verified against the working tree on 2026-07-29. Use directly — no exploration needed. -->

src/Orchestrator/Scheduling/WorkflowScheduler.cs — current members (line numbers as of writing):
  L112  public Task UnscheduleAsync(Guid jobId, CancellationToken ct)             // LIVE
  L116  public Task PauseAsync(Guid jobId, CancellationToken ct)                  // DELETE (dead)
  L120  public Task PauseAllAsync(CancellationToken ct) => scheduler.PauseAll(ct) // LIVE
  L141  public Task ResumeAllGroupsAsync(CancellationToken ct)                    // LIVE (group-flag clear)
  L146  public Task<TriggerState> GetTriggerStateAsync(Guid jobId, CancellationToken ct) // LIVE

src/Orchestrator/Hydration/WorkflowLifecycle.cs:
  L163  public async Task PauseOnlyAsync(Guid workflowId, CancellationToken ct)   // DELETE (dead)
  L177  public async Task ResumeAsync(Guid workflowId, CancellationToken ct)      // LIVE

src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs:
  L28   public const string LifecycleBase          = "orchestrator";               // LIVE
  L31   public const string PauseResumeBase        = "orchestrator-pauseresume";   // DELETE (dead)
  L34   public const string GlobalPauseResumeBase  = "orchestrator-global-pauseresume"; // LIVE
  L41   public static string PerInstance(string baseName, string instanceId)       // LIVE

Live resume ordering that the retargeted tests must mirror (ResumeAllConsumer.cs:43-47):
  foreach (workflowId in store.WorkflowIds) await lifecycle.ResumeAsync(...);   // per-job, Paused-guarded
  await scheduler.ResumeAllGroupsAsync(...);                                    // THEN group-flag clear
Quartz 3.18 RAMJobStore semantics that make the ordering load-bearing: after PauseAll(),
`pausedTriggerGroups` stays set, so a trigger scheduled BEFORE the group clear is born Paused.
</interfaces>

<must_not_touch>
Deleting any of these breaks production. They are the SEPARATE, LIVE global pair:
- src/Orchestrator/Consumers/PauseAllConsumer.cs, ResumeAllConsumer.cs and their two Definitions
  (PauseAllConsumerDefinition.cs is edited for a stale DOC LINE ONLY — its code is untouched)
- OrchestratorFanoutEndpoints.GlobalPauseResumeBase
- WorkflowScheduler.PauseAllAsync, WorkflowScheduler.ResumeAllGroupsAsync, WorkflowScheduler.GetTriggerStateAsync
- WorkflowLifecycle.ResumeAsync (called by ResumeAllConsumer's per-job loop)
- The Keeper's BitHealthLoop PauseAll/ResumeAll publishes
- OrchestratorFanoutEndpoints.LifecycleBase (Start/Stop) and the shared competing-consumer
  `orchestrator-result*` endpoints
- tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs — DO NOT DELETE. Its filename
  contains "PauseResume" but it is a RealStack E2E test of the GLOBAL PauseAll/ResumeAll seam only
  (verified: zero references to PauseWorkflow/ResumeWorkflow/PauseAsync). Leave it entirely alone.
- tests/BaseApi.Tests/Orchestrator/Consumers/PauseAllConsumerTests.cs — LIVE, untouched.

Verified non-coupling: `TriggerState.Paused` is still produced after this removal by the global
`PauseAll()` path, so `ResumeAsync`'s `== Paused` guard remains meaningful and reachable.

Verified blast radius OUTSIDE src/ + tests/: NONE. No k8s manifest, Grafana dashboard, alert rule,
script or runbook references `orchestrator-pauseresume` (only `orchestrator-global-pauseresume`,
untouched). The queues are MassTransit `Temporary` (auto-delete) — no broker cleanup is needed; they
vanish when the orchestrator pods restart.
</must_not_touch>

<correction_to_prior_analysis>
The originating investigation stated that `WorkflowScheduler.PauseAsync`'s ONLY caller is
`PauseOnlyAsync`. That is true within `src/`, but it MISSED a second caller in `tests/`:

  tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs:69-70
      await workflowScheduler.PauseAsync(w1.job, ct);
      await workflowScheduler.PauseAsync(w2.job, ct);

That file tests the LIVE `ResumeAllConsumer` and MUST survive. Task 2 therefore retargets it (and the
two call sites in `PauseResumeSchedulingTests.cs`) onto the live group-level pause. Deleting
`PauseAsync` without this edit breaks the test build.
</correction_to_prior_analysis>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Capture the baseline, then delete the dead message + consumer + endpoint layer</name>
  <files>
    src/Messaging.Contracts/PauseWorkflow.cs (delete),
    src/Messaging.Contracts/ResumeWorkflow.cs (delete),
    src/Orchestrator/Consumers/PauseWorkflowConsumer.cs (delete),
    src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs (delete),
    src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs (delete),
    src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs (delete),
    tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs (delete),
    tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs (delete),
    src/Orchestrator/Program.cs,
    src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs,
    src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs,
    tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs
  </files>
  <action>
FIRST, capture the BEFORE baseline — the hermetic suite has ~23-24 PRE-EXISTING failures unrelated to
this work, so a raw failure count is meaningless without it. Do not skip this; Task 3 compares against it.

  1. Build Release: `dotnet build SK_P.sln -c Release`. Record success and warning count (must be 0 —
     Directory.Build.props sets TreatWarningsAsErrors=true, so a warning is already a build failure).
  2. Run the hermetic suite by executing the built binary DIRECTLY — `dotnet test` HANGS on Windows MTP:
     `tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack`
  3. Write the full output to the scratchpad as `baseline-before.txt` and record the exact
     passed / failed / skipped totals in the summary. This number is the acceptance bar for Task 3.

THEN make the deletions. Use `git rm` for every whole-file deletion — it stages ONLY those paths. The
working tree is dirty with UNRELATED changes (src/Keeper/Recovery/ReinjectConsumer.cs, SK_P.sln,
analyzer-reports, many untracked *.out files); NEVER use `git add -A` or `git add .` at any point.

  `git rm` these six production files:
    src/Messaging.Contracts/PauseWorkflow.cs
    src/Messaging.Contracts/ResumeWorkflow.cs
    src/Orchestrator/Consumers/PauseWorkflowConsumer.cs
    src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs
    src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs
    src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs

  `git rm` these two test files — each tests ONLY deleted symbols, nothing else:
    tests/BaseApi.Tests/Messaging/PauseResumeContractTests.cs
    tests/BaseApi.Tests/Orchestrator/PauseResumeConsumerTests.cs

Then edit four files:

(a) src/Orchestrator/Program.cs — delete lines 57-63 in their entirety: the stale `// PAUSE-02/03/04:`
    three-line comment block AND both `x.AddConsumer<PauseWorkflowConsumer, ...>()` /
    `<ResumeWorkflowConsumer, ...>()` registrations with their `.Endpoint(...)` calls. This is the edit
    that drops the third per-pod exclusive queue. Then fix the SURVIVING global-pair comment at lines
    64-68: it currently ends "...independent from \"orchestrator-pauseresume\" so Phase 48 can drop the
    old per-workflow endpoint with zero entanglement." Replace that trailing clause with a statement
    that the old per-workflow `orchestrator-pauseresume` endpoint has now been removed — the
    independence it was designed for was exercised. Leave the PauseAll/ResumeAll registrations at lines
    69-72 and everything from line 73 down BYTE-IDENTICAL.

(b) src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs — delete the `PauseResumeBase` const and
    its `<summary>` doc line (lines 30-31). Then correct the class doc-comment, which is now wrong in
    three places: "three PER-REPLICA fan-out receive-endpoint queue names" becomes TWO; "the six fan-out
    ConsumerDefinitions" becomes FOUR; and the co-location contract list "each pair (Start+Stop,
    Pause+Resume, PauseAll+ResumeAll)" drops the middle pair, leaving "(Start+Stop, PauseAll+ResumeAll)".
    Also drop the now-dangling "keeping the pause/resume pairs' shared ConcurrentMessageLimit = 1
    serialization intact" phrasing down to the single surviving global pair. `LifecycleBase`,
    `GlobalPauseResumeBase` and `PerInstance` are untouched.

(c) src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs — DOC LINE ONLY, no code change. Line 10
    reads "...independent from the per-workflow `orchestrator-pauseresume` so Phase 48 can drop the old
    endpoint with zero entanglement (D-08)." Rewrite so it no longer points at a removed endpoint —
    state that the per-workflow endpoint it was kept independent from has since been removed. Do not
    touch the class body, the constructor, or the ConcurrentMessageLimit/retry configuration.

(d) tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs — this is the HA-07
    RESOURCE_LOCKED regression guard and it MUST survive intact for the two remaining bases. Do NOT
    weaken or delete any Fact. Make exactly these changes:
      - Remove `OrchestratorFanoutEndpoints.PauseResumeBase,` from the `AllBases` array (line 24).
      - In `PerInstance_SameInstanceId_CoLocatesEachPair`, delete the middle `Assert.Equal(...)` pair
        that uses `PauseResumeBase` (lines 50-52); keep the Lifecycle and GlobalPauseResume asserts.
      - Rename `PerInstance_ThreeBases_AreMutuallyDistinct_ForSameInstance` to
        `PerInstance_TwoBases_AreMutuallyDistinct_ForSameInstance`, delete the `pauseResume` local and
        the two assertions that reference it, keeping `Assert.NotEqual(lifecycle, globalPauseResume);`.
      - In `PerInstance_ProducesExpectedShape`, delete the `"orchestrator-pauseresume-pod-x"` assertion
        (lines 78-79). Keep the `"orchestrator-pod-x"` and `"orchestrator-global-pauseresume-pod-x"`
        assertions and the never-null/empty loop.
      - Update the class doc: the co-location list "(Start==Stop, Pause==Resume, PauseAll==ResumeAll)"
        drops the middle entry.
    `PerInstance_TwoDifferentInstanceIds_YieldDistinctNames` — the Fact naming the actual RESOURCE_LOCKED
    root cause — keeps its body unchanged and simply iterates the now-two-element `AllBases`.

After these edits the tree still compiles: `PauseOnlyAsync` and `PauseAsync` remain (Task 2 removes
them). Verify before moving on.
  </action>
  <verify>
    <automated>dotnet build SK_P.sln -c Release</automated>
  </verify>
  <done>
Baseline captured (Release build result + hermetic passed/failed/skipped totals recorded in the summary
and saved to the scratchpad). Eight files deleted and staged via `git rm`. Four files edited. Release
build of the whole solution succeeds with zero warnings. `grep -rn "PauseWorkflowConsumer\|ResumeWorkflowConsumer"
src/` returns nothing. No unrelated file has been staged (`git status --short` shows the dirty unrelated
paths still unstaged).
  </done>
</task>

<task type="auto">
  <name>Task 2: Delete the orphaned PauseOnlyAsync/PauseAsync seams and retarget their surviving test callers</name>
  <files>
    src/Orchestrator/Hydration/WorkflowLifecycle.cs,
    src/Orchestrator/Scheduling/WorkflowScheduler.cs,
    tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs,
    tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs
  </files>
  <action>
Task 1 removed the only production caller of `PauseOnlyAsync` (PauseWorkflowConsumer). Remove the two
now-orphaned seams, and in the SAME change retarget the three surviving test call sites so the test
project still compiles.

(a) src/Orchestrator/Hydration/WorkflowLifecycle.cs — delete `PauseOnlyAsync` (lines 161-172) including
    its `<summary>` doc block. `UnscheduleOnlyAsync` above it and `ResumeAsync` below it are LIVE and
    must be left byte-identical.

(b) src/Orchestrator/Scheduling/WorkflowScheduler.cs — delete `PauseAsync(Guid jobId, ...)` (lines
    115-117) including its `<summary>` line. Do NOT confuse it with `PauseAllAsync` on line 120, which
    is LIVE. `UnscheduleAsync`, `PauseAllAsync`, `ResumeAllGroupsAsync` and `GetTriggerStateAsync` all
    remain.

(c) tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs — do NOT delete this file.
    Its `GetTriggerStateAsync` / resume-ignore coverage guards LIVE seams. Retarget the two `PauseAsync`
    call sites onto the live group-level pause:
      - `PauseSuppressesFire` (line 69): replace `await sut.PauseAsync(jobId, ct);` with
        `await sut.PauseAllAsync(ct);`. Nothing is rescheduled afterwards, so there is no
        `pausedTriggerGroups` interaction. Both surrounding assertions survive unchanged — the
        `TriggerState.Normal` check at line 67 (the load-bearing deterministic
        `.WithIdentity(TriggerKey(jobId.ToString("D")))` stamping proof) and the `TriggerState.Paused`
        check at line 72.
      - `ResumeReschedulesFresh` (line 95): replace `await sut.PauseAsync(jobId, ct);` with
        `await sut.PauseAllAsync(ct);`. This one DOES reschedule afterwards, and a group-level pause
        leaves Quartz's `pausedTriggerGroups` set — so the fresh trigger created at line 100 would be
        born `Paused` and the `TriggerState.Normal` assertion at line 105 would fail. Fix it by
        mirroring the live `ResumeAllConsumer` ordering: after the existing
        `UnscheduleAsync` -> `ScheduleAsync` pair, add `await sut.ResumeAllGroupsAsync(ct);` and then
        leave the four existing assertions (single trigger, Normal, non-null future next-fire) exactly
        as they are. Add a short comment noting this mirrors the load-bearing GAP-49-2 / D-08 ordering
        (per-job fresh reschedule FIRST, group-flag clear SECOND).
      - Update the class doc-comment: the PAUSE-02 bullet's "after `PauseAsync(jobId)`" becomes
        "after `PauseAllAsync()`", and the trailing "RED until Plan 02 adds `PauseAsync`/
        `GetTriggerStateAsync`" sentence must stop naming the removed member. `ResumeIgnoresStoppedAndRunning`
        is untouched. Keep the existing filename — renaming adds churn for no gain.

(d) tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs — this file was MISSED by the
    originating investigation and is the reason this task exists. It tests the LIVE `ResumeAllConsumer`
    and must survive. Replace the two per-job pause calls at lines 69-70 with a single scheduler-wide
    `await workflowScheduler.PauseAllAsync(ct);` and update the comment above them to say the Paused
    precondition now comes from the live scheduler-wide pause rather than a per-job pause. Both
    `TriggerState.Paused` assertions at lines 71-72 still hold (a group-level pause reports `Paused`
    via `GetTriggerState` — already proven in this same file by `Normal_After_PauseAll_Resume_Cycle`,
    which asserts exactly that at its line 166), and the post-resume `TriggerState.Normal` assertions at
    lines 78-79 still hold because `ResumeAllConsumer` runs the per-job loop THEN
    `ResumeAllGroupsAsync` (proven at line 171 of the same file). Also fix two stale doc references:
    the class doc's "(mirroring `PauseResumeConsumerTests`)" points at a file Task 1 deleted, and the
    inline "mirrors the proven `PauseResumeSchedulingTests` cycle" comment no longer describes what the
    code does. Do NOT modify `Resume_Of_Non_Paused_Trigger_Is_Ignored` or
    `Normal_After_PauseAll_Resume_Cycle` — both are untouched by this removal.

Do not stage anything in this task; Task 3 performs the single explicit commit.
  </action>
  <verify>
    <automated>dotnet build SK_P.sln -c Release</automated>
  </verify>
  <done>
`PauseOnlyAsync` and `PauseAsync` no longer exist: `grep -rn "PauseOnlyAsync" src/ tests/` and
`grep -rn "\bPauseAsync\b" src/ tests/` (excluding bin/obj) both return nothing. Release build succeeds
with zero warnings. `PauseResumeSchedulingTests` and `ResumeAllConsumerTests` still contain all of their
original Facts — none deleted, none weakened.
  </done>
</task>

<task type="auto">
  <name>Task 3: Correct the stale AtLeastOnceStructuralFacts rationale, run the full gate, commit</name>
  <files>tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs</files>
  <action>
(a) DOC-COMMENT ONLY — do NOT weaken, delete, or "simplify" either Fact, and do not touch a single line
    of assertion code. FACT A's `<para>` block (lines 34-41) justifies "WHY reflection, NOT a
    string-scan" by citing "a legitimate positional `string H` member on `PauseWorkflow`/`ResumeWorkflow`
    (Messaging.Contracts)". Those two records were deleted in Task 1, and they were verified to be the
    ONLY carriers of a positional `string H` in the codebase — so the worked example no longer exists.

    Rewrite that paragraph to justify the reflection choice on its own merits: a source scan for
    `"flag["` or `".H"` matches on SPELLING, so any legitimately-named member sharing those characters
    false-positives and the scan cannot distinguish a retired dedup key from an unrelated member
    (Pitfall 2); the type/member-NAME guard asks the loaded assemblies whether anything is actually
    NAMED `MessageIdentity`, which is exact and cannot be fooled by naming coincidence. Note that the
    worked example the paragraph used to cite — a legitimate positional member on a per-workflow control
    contract — no longer exists because that dead fan-out pair was removed, and that the reasoning does
    not depend on it.

    CRITICAL: write this WITHOUT using the literal strings `PauseWorkflow` or `ResumeWorkflow`. The
    acceptance gate below asserts zero occurrences of those identifiers anywhere under src/ and tests/,
    and a doc-comment mention would fail it. This is the only occurrence of either name left in the file
    (verified: line 36 only).

(b) Run the full gate. All five checks must pass:

    1. `dotnet build SK_P.sln -c Release` — succeeds, zero warnings (TreatWarningsAsErrors=true).
    2. `grep -rn "PauseWorkflow\|ResumeWorkflow" --include="*.cs" src/ tests/ | grep -v "/bin/\|/obj/"`
       — must return NOTHING (exit 1). Names must not survive even in comments.
    3. `grep -rn "PauseResumeBase" --include="*.cs" src/ tests/ | grep -v "/bin/\|/obj/" | grep -v "GlobalPauseResumeBase"`
       — must return NOTHING. The `grep -v GlobalPauseResumeBase` filter is REQUIRED because
       `GlobalPauseResumeBase` contains `PauseResumeBase` as a substring; a bare grep here is a false
       positive on the surviving live const, not a failure.
    4. `grep -rn "orchestrator-pauseresume" src/ tests/ k8s/ scripts/ | grep -v "/bin/\|/obj/" | grep -v "global-pauseresume"`
       — must return NOTHING (the live `orchestrator-global-pauseresume` is excluded by the filter).
    5. Hermetic suite:
       `tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack`
       Compare the failed count to the Task 1 baseline. The bar is EQUAL-OR-FEWER failures, and the
       failing test NAMES must be a subset of the baseline's — a same-count-but-different-names result
       is a REGRESSION, not a pass. Diff the two name sets, do not compare counts alone. Expect the
       total test count to drop (two whole files removed plus a handful of individual cases); that is
       expected, a new FAILURE is not. If any new failure appears, STOP and report it — do not adjust
       the test to make it pass.

(c) Commit with EXPLICIT paths only. The working tree is dirty with unrelated changes; `git add -A` /
    `git add .` are FORBIDDEN. Task 1's `git rm` already staged the eight deletions. Stage exactly the
    ten edited files by path:

      git add src/Orchestrator/Program.cs \
              src/Orchestrator/Messaging/OrchestratorFanoutEndpoints.cs \
              src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs \
              src/Orchestrator/Hydration/WorkflowLifecycle.cs \
              src/Orchestrator/Scheduling/WorkflowScheduler.cs \
              tests/BaseApi.Tests/Orchestrator/OrchestratorFanoutEndpointsFacts.cs \
              tests/BaseApi.Tests/Orchestrator/Scheduling/PauseResumeSchedulingTests.cs \
              tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs \
              tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs

    Run `git status --short` and confirm the staged set is EXACTLY the 8 deletions + 9 modifications
    above — in particular that src/Keeper/Recovery/ReinjectConsumer.cs, SK_P.sln, analyzer-reports/ and
    the untracked *.out files are all still UNSTAGED. If anything unrelated is staged, unstage it before
    committing. Then commit:

      refactor: remove the dead per-workflow PauseWorkflow/ResumeWorkflow fan-out pair

      No publisher since 5f0e210 deleted the reactive Fault<T> recovery handler. Phase 48
      spared it on a false premise (48-RESEARCH.md:117 conflates it with the live global
      PauseAll/ResumeAll pair, which is what BitHealthLoop actually publishes). Drops the
      third per-pod exclusive orchestrator-pauseresume-{instanceId} fan-out queue.
      Global PauseAll/ResumeAll path untouched.
  </action>
  <verify>
    <automated>dotnet build SK_P.sln -c Release &amp;&amp; tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack</automated>
  </verify>
  <done>
All five gate checks pass. Hermetic failing-test NAME set is a subset of the Task 1 baseline — zero new
failures. One commit exists containing exactly 8 deletions + 9 modifications; `git status --short` still
shows the unrelated dirty paths unstaged and uncommitted.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| broker -> orchestrator consumers | Control messages cross here; removing a consumer removes an attack surface but must not remove a live one |
| working tree -> git index | Unrelated dirty changes must not enter this commit |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-cfj-01 | Denial of Service | Global PauseAll/ResumeAll control path | mitigate | Explicit `<must_not_touch>` list; Task 3 gate check 4 filters `global-pauseresume` out of the removal grep so the live endpoint is provably still present; `ResumeAllConsumerTests` (3 Facts incl. the GAP-49-2 regression) survives and runs |
| T-cfj-02 | Denial of Service | HA-07 RESOURCE_LOCKED regression guard | mitigate | `OrchestratorFanoutEndpointsFacts` is edited, never deleted; `PerInstance_TwoDifferentInstanceIds_YieldDistinctNames` keeps its body and still iterates `AllBases` for the two remaining bases |
| T-cfj-03 | Tampering | Unrelated working-tree changes | mitigate | `git add -A`/`git add .` explicitly forbidden; `git rm` for deletions stages only named paths; Task 3 enumerates every staged path and requires a `git status --short` confirmation before committing |
| T-cfj-04 | Repudiation | Dedup-machinery structural guard (FACT A/B) | mitigate | Change to `AtLeastOnceStructuralFacts.cs` is restricted to one `<para>` of prose; no assertion line may be edited; both Facts still execute in the hermetic run |
| T-cfj-05 | Information Disclosure | n/a — no data path, no new input, no dependency change | accept | Pure deletion of an unreachable consumer; no new code paths, no package installs (no package-legitimacy gate applies) |
| T-cfj-06 | Elevation of Privilege | Silent test weakening to force a green gate | mitigate | Task 3 compares failing-test NAME SETS against the baseline, not counts; explicit instruction to STOP and report rather than adjust a test that newly fails |
</threat_model>

<verification>
1. `dotnet build SK_P.sln -c Release` — success, zero warnings (TreatWarningsAsErrors=true globally).
2. Zero occurrences of `PauseWorkflow` / `ResumeWorkflow` under src/ and tests/ (comments included).
3. Zero occurrences of `PauseResumeBase` (excluding `GlobalPauseResumeBase`) and zero occurrences of
   `orchestrator-pauseresume` (excluding `orchestrator-global-pauseresume`) anywhere.
4. Hermetic run failing-test name set is a subset of the Task 1 baseline set.
5. Surviving live proofs all still present and passing: `OrchestratorFanoutEndpointsFacts` (4 Facts),
   `ResumeAllConsumerTests` (3 Facts), `PauseAllConsumerTests`, `PauseResumeSchedulingTests` (3 Facts),
   `AtLeastOnceStructuralFacts` (both Facts).
6. One commit; `git status --short` shows the unrelated dirty paths still unstaged.
</verification>

<success_criteria>
- The per-workflow Pause/Resume control-message pair is gone: 2 contracts, 2 consumers, 2 consumer
  definitions, 2 Program.cs registrations, 1 endpoint base const, 2 orphaned seams
  (`WorkflowLifecycle.PauseOnlyAsync`, `WorkflowScheduler.PauseAsync`), 2 whole test files.
- Orchestrator replicas declare TWO per-pod exclusive fan-out queues instead of three.
- The global `PauseAll`/`ResumeAll` path is behaviorally unchanged and still proven hermetically.
- Release build is zero-warning; hermetic suite has no new failures vs. the captured baseline.
- Exactly one commit, containing only this plan's files.
</success_criteria>

<output>
Create `.planning/quick/260729-cfj-remove-the-dead-pauseworkflow-resumework/260729-cfj-SUMMARY.md`
when done. Record: the Task 1 baseline totals, the post-change totals, the failing-test name-set diff,
and — if the `ResumeReschedulesFresh` group-clear mirror did not work as specified — exactly what was
done instead and why.
</output>
