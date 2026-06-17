---
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
plan: 03
subsystem: orchestrator
tags: [pre-pipeline, branch-free, clean-absent, always-write, unresolved-metric, fan-out, observability]

# Dependency graph
requires:
  - phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi (Plan 01)
    provides: "Processor always-writes a terminal out: blob (every Completed/Failed/Cancelled now carries a real out: blob keyed by EntryId) — the precondition that lets the orchestrator read+fan-out+delete uniformly"
  - phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi (Plan 02)
    provides: "SelectNext -> readonly record struct SelectNextResult{Matches, UnresolvedIds}; OrchestratorMetrics.StepUnresolved counter; MeterCollector test seam"
provides:
  - "OrchestratorPrePipeline.RunAsync is branch-free (NO `if (outcome == StepOutcome.Completed)`): the out: read, fan-out, and delete run for EVERY outcome (D-09)"
  - "Three-way read (D-10): present -> fan-out+delete / clean-absent (no Redis fault) -> idempotent ack-skip (no keeper) / Redis fault -> REINJECT; a Processing result rides the clean-absent skip for free (D-05)"
  - "orchestrator_step_unresolved wired: increments at stage-1 (L1 miss) + once per dangling next-step id at stage-3 (continue, never throws), labeled workflowId only; terminal/condition-skip/normal-fanout do NOT increment"
affects: [72-verifier, orchestrator-pre-pipeline, unresolved-step-metric]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Branch-free terminal pipeline: outcome consumed ONLY for entry-condition matching (SelectNext), never for L2 behavior — read/fan-out/delete uniform across Completed/Failed/Cancelled/Processing"
    - "Inverted clean-absent/fault discrimination: read lambda returns null on IsNullOrEmpty (clean-absent) so a success-with-absent is distinguishable from a thrown RedisException; clean-absent -> idempotent ack-skip, fault -> REINJECT (orchestrator SPLITS what the processor unifies)"
    - "Constructor-injected metrics holder + both increment sites landed in one change to avoid CS9113 on an otherwise-unused param"

key-files:
  created: []
  modified:
    - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs
    - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs

key-decisions:
  - "Read lambda returns null (not '') on clean-absent so the success-with-absent case is distinguishable from a thrown RedisException (Pitfall 2) — clean-absent -> ack-skip, fault -> REINJECT (D-10)"
  - "Terminal early-return requires BOTH Matches.Count==0 AND UnresolvedIds.Count==0 — empty Matches with non-empty UnresolvedIds falls through so stage-3 counts the dangling ids (D-03 terminal != unresolved)"
  - "stage-3 increment loop sits AFTER the clean-absent early-return gate so a skipped (clean-absent) message never counts stage-3 (Open Question 2)"
  - "Both increments carry the workflowId label ONLY (m.WorkflowId.ToString(\"D\")) — bounded cardinality, no reason/stage label (T-72-07)"

patterns-established:
  - "The four TypedResultConsumer<T> shells now truly run ONE uniform pre-pipeline flow — the Phase-70/71 two-consumer symmetry payoff; the Phase-71-deferred trip-end metric (D-18) is realized here"

requirements-completed: [SPEC-4, SPEC-5, SPEC-6]

# Metrics
duration: 18min
completed: 2026-06-17
---

# Phase 72 Plan 03: Uniform Branch-Free Orchestrator Pre-Pipeline + Unresolved-Step Metric Summary

**`OrchestratorPrePipeline.RunAsync` is now branch-free — the `out:` read, fan-out, and delete run for every terminal outcome (D-09, riding Plan-01's always-write), a cleanly-absent `out:` blob acks as an idempotent skip (which makes a `Processing` result stop advancing for free, D-05) while a Redis fault still escalates REINJECT, and the `orchestrator_step_unresolved` counter (Plan 02) is wired at the stage-1 L1 miss and once per dangling next-step id at stage-3 — labeled `workflowId` only, graceful (never throws).**

## Performance

- **Duration:** ~18 min
- **Started:** 2026-06-17T11:21:32Z
- **Completed:** 2026-06-17T11:40:00Z (approx)
- **Tasks:** 2
- **Files modified:** 5 (0 created, 5 modified)

## Accomplishments

- **Task 1 — branch-free pipeline + clean-absent skip:** Dropped BOTH `if (outcome == StepOutcome.Completed)` gates (the `out:` read and the delete). The read now runs for every outcome; the read lambda returns `null` on `IsNullOrEmpty` so a success-with-absent is distinguishable from a thrown `RedisException`. Three-way disposition: read-fault (exhausted) -> REINJECT + return; clean-absent (no fault) -> a DISTINCT `clean-absent` trip-end log + idempotent ack-skip (NO fan-out, NO keeper, NO delete — D-10); present -> relocate. Consumed `SelectNext`'s new `{Matches, UnresolvedIds}` struct; the terminal early-return now requires both empty so a dangling-only step falls through to stage-3. `ExecutionId = m.ExecutionId` threaded unchanged (D-13). 0 `outcome == StepOutcome.Completed` branches remain.
- **Task 2 — metrics injection + increments + full fact coverage:** Added `OrchestratorMetrics metrics` to the primary ctor and landed both increment sites in the same change (no CS9113): stage-1 (L1 miss -> log + increment + return) and a stage-3 `foreach (var unresolvedId in selection.UnresolvedIds)` (log + increment + `continue`, never throws — D-02), both tagged `workflowId` only. Rewrote/extended `OrchestratorPrePipelineFacts` in place (D-12): `Build` gains a metrics arg from a real `IMeterFactory`; the old "proceed empty" Failed fact became a present-`out:`-blob Failed/Cancelled fan-out+delete fact (REQ-5); added clean-absent, Processing-rides-skip, dangling-next-step (resolvable still fans out + exactly one increment), condition-skip no-increment, and normal-multimatch no-increment facts, all with `MeterCollector` tag-set assertions (`{ "workflowId" }` only). `OrchestratorPrePipelineFacts`: **15/15 GREEN** (was 9).

## Task Commits

Each task was committed atomically:

1. **Task 1: Uniform branch-free pre-pipeline + clean-absent skip + ExecutionId thread** — `b501557` (feat)
2. **Task 2: Inject OrchestratorMetrics + stage-1/stage-3 increments + full fact coverage** — `88dbd3f` (feat)

_TDD note (both tasks `tdd="true"`): the structural transform in Task 1 establishes RED at the suite level — the old `Failed_continuation_writes_no_data_and_dispatches_empty` fact (which asserted the now-removed "Failed reads no out:" behavior) and `TypedResultConsumerFacts`'s Failed-gated sub-call both encode the pre-D-09 behavior and fail the instant the read becomes uniform. Task 2's in-place fact rewrite + the `entryId` fix make them GREEN, asserting the new uniform read/fan-out/delete + the wired counter. Task 1's verify is a build (the production transform); Task 2 carries the test green-up — the contract change and its facts are inseparable for a meaningful assertion._

## Files Created/Modified

- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` — branch-free RunAsync (no outcome gate); three-way read (present/clean-absent/fault); `OrchestratorMetrics` ctor injection; stage-1 + stage-3 `orchestrator_step_unresolved` increments (workflowId label); XML doc rewritten to the uniform flow.
- `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` — `Build`/`NewMetrics` gain a real-`IMeterFactory` metrics arg; rewrote Failed -> present-blob fan-out+delete (+ a Cancelled twin); added clean-absent, Processing-rides-skip, dangling-next-step, condition-skip-no-increment, normal-fanout-no-increment facts; `MeterCollector` Total/tag assertions.
- `tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs` — ctor migrated to `OrchestratorTestStubs.Metrics()`; the Failed-gated-successor sub-call now passes the SAME `entryId` whose `out:` blob is present (the Failed read is now uniform).
- `tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs` — `Pipeline` ctor migrated to `OrchestratorTestStubs.Metrics()`.
- `tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs` — inline pipeline ctor migrated to `OrchestratorTestStubs.Metrics()`.

## Decisions Made

- The read lambda returns `null` on a clean-absent value (not `""`) so the success-with-absent is distinguishable from a thrown `RedisException` — the orchestrator SPLITS clean-absent (ack-skip) from a fault (REINJECT), inverting the processor's unify-absent+fault model (Pitfall 2 / D-10).
- Terminal early-return requires BOTH `Matches.Count == 0` AND `UnresolvedIds.Count == 0`; a dangling-only step falls through so stage-3 increments (terminal != unresolved, D-03).
- The stage-3 increment loop sits AFTER the clean-absent gate so a clean-absent (skipped) message never counts stage-3 (Open Question 2).
- Both increments carry the `workflowId` label only (`m.WorkflowId.ToString("D")`) — bounded cardinality, no `reason`/`stage` label (T-72-07).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Migrated 3 additional test ctor sites for the OrchestratorMetrics ctor param**
- **Found during:** Task 2 (adding `OrchestratorMetrics metrics` to the primary ctor)
- **Issue:** Adding the ctor param broke every direct `new OrchestratorPrePipeline(...)` caller. The plan named only `OrchestratorPrePipelineFacts.Build`; a build surfaced three more in the test project: `TypedResultConsumerFacts.Pipeline`, `ResultAckTests.Pipeline`, and an inline ctor in `StopConsumerLifecycleTests`. All must compile to hold the 0-warning gate.
- **Fix:** Passed the existing `OrchestratorTestStubs.Metrics()` helper (a real-`IMeterFactory` `OrchestratorMetrics`) at each site. No behavior change.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs, ResultAckTests.cs, StopConsumerLifecycleTests.cs
- **Verification:** Test project `dotnet build -c Debug -warnaserror` -> 0 warnings; full solution Debug + Release 0-warning.
- **Committed in:** 88dbd3f (Task 2 — inseparable from the ctor change).

**2. [Rule 1 - Test] Updated TypedResultConsumerFacts' Failed-gated sub-call to carry the present-blob entryId**
- **Found during:** Task 2 full-suite run (the hermetic test `StepCompletedConsumer_does_not_advance_a_Failed_gated_successor` failed)
- **Issue:** The fact's second sub-call drove a `StepFailed` with `entryId = Guid.Empty` while the `out:` blob was seeded at the REAL `entryId`, asserting `Assert.Single` (fan-out). That encoded the PRE-D-09 behavior where a Failed result didn't read `out:`. Under the now-uniform read, `OutputData(Guid.Empty)` is clean-absent -> the Failed result hits the ack-skip -> 0 handoffs -> the assertion failed. This is the intended RED->GREEN of the D-09 change surfacing in a sibling fact, not a defect.
- **Fix:** The Failed sub-call now passes the SAME `entryId` whose `out:` blob is present (the processor always-writes a terminal `out:` blob, so a Failed result legitimately relocates). Added a comment noting the D-09 rationale.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
- **Verification:** `--filter-method "*StepCompletedConsumer_does_not_advance_a_Failed_gated_successor"` -> Passed 1/1.
- **Committed in:** 88dbd3f (Task 2).

---

**Total deviations:** 2 auto-fixed (1 Rule 3 - blocking, 1 Rule 1 - test). Both stay within the plan's stated touchpoints (the ctor change + its callers; the uniform-read behavior + its sibling fact). No production-behavior change beyond what the plan specified.

## Issues Encountered

- The xUnit v3 / Microsoft.Testing.Platform runner ignores VSTest-style `--filter`; the correct invocation is `--filter-class "*Name"` / `--filter-method "*Name"` after `--` (per the Plan-01/02 SUMMARY notes).
- The full `dotnet test tests/BaseApi.Tests` run reports 288 failures — these are ALL infra-dependent E2E/Integration/Features/Observability/Persistence tests requiring a live Docker stack (Redis/RabbitMQ/Postgres/sample processors) which the sandbox lacks (`RedisFixtureFacts.InitializeAsync_Connects_Multiplexer` fails, and the log shows RabbitMQ transport connection faults). NONE belong to the hermetic dispatch facts: all 12 failing `Orchestrator.*` classes are `*E2ETests`; the lone hermetic Orchestrator failure was the Failed-gated fact above, now fixed. The directly-affected hermetic facts (`OrchestratorPrePipelineFacts` 15 + `TypedResultConsumerFacts` + `OrchestratorMetricsFacts` + `StepAdvancementTests`) run **40/40 GREEN**. This matches the project's known hermetic-vs-Docker split (Plan 02 ran the same targeted subset).

## Live-Stack Deferral

Per the plan `<verification>` + SPEC AC #10: the live-stack proof (uniform pipeline + always-write + `orchestrator_step_unresolved_total` scraped from Prometheus) is **deferred-automated** — the sandbox lacks Docker. The hermetic facts prove the uniform read/fan-out/delete, the clean-absent/fault split, the Processing-rides-skip, and the stage-1/stage-3 increments + workflowId-only tags.

## User Setup Required

None — no external service configuration required for the hermetic deliverable.

## Next Phase Readiness

- This is the LAST plan (3 of 3) of Phase 72. The verifier can confirm: `grep -c "outcome == StepOutcome.Completed" src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` == 0; `grep -c "metrics.StepUnresolved.Add" ...` == 2; `OrchestratorPrePipelineFacts` 15/15 + affected 40/40 GREEN; full solution 0-warning Debug + Release.
- The Phase-72 trip (Plan 01 processor always-write -> Plan 02 orchestrator foundation -> Plan 03 uniform pipeline + metric) is complete: all four `TypedResultConsumer<T>` shells run one branch-free flow, and the Phase-71-deferred D-18 trip-end metric is realized.

## Self-Check: PASSED

All 5 modified files present; both task commits (`b501557`, `88dbd3f`) exist in git history. Acceptance greps: `outcome == StepOutcome.Completed` -> 0; `metrics.StepUnresolved.Add` -> 2. Affected hermetic facts GREEN (OrchestratorPrePipelineFacts 15/15; combined affected 40/40). Full solution builds 0-warning Debug + Release. The 288 full-suite failures are pre-existing Docker-bound E2E/Integration tests, none in the hermetic dispatch surface this plan touched.

---
*Phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi*
*Completed: 2026-06-17*
