---
phase: 85-processor-framework-boundary-hardening
plan: 03
subsystem: infra
tags: [dotnet, processor-framework, redis, keeper, ownership-boundary, fail-loud]

# Dependency graph
requires:
  - phase: 85-processor-framework-boundary-hardening
    plan: 01
    provides: SpawnToPost fail-loud throw (SpawnSendExhaustedException) — the D-01 ordering guarantee that a spawn-exhaust never reaches `return null`
  - phase: 85-processor-framework-boundary-hardening
    plan: 02
    provides: ProcessorPipeline narrow nack-escape catch — a defeated spawn nack-requeues (never reaches the null-path delete)
provides:
  - "Entry/source deletion is FRAMEWORK-OWNED on BOTH completion paths — the Mode-1 inline tail AND the Mode-2 null-return path — via a shared private ProcessorPipeline.DeleteEntryTail(d, db, limit, ct)"
  - "The concrete-processor surface shrank to 'interpret input → business logic → return DataResult (or spawn N + return null)': no DeleteEntry API, no author delete/escalation awareness"
  - "Guid.Empty source net-effect UNCHANGED: the null-path delete is guarded by !SourceStep.IsSource, so a source seed is skipped (old no-op DeleteEntry == new IsSource skip; both leave nothing)"
  - "The DELETE-keeper escalation is preserved — it moved inline to DeleteEntryTail's SendKeeper(BuildDelete(d)) on delete-exhaust"
affects: [85-04 (SC-4 seven-scenario sweep validates the net-effect live), KafkaImporter KIMP-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Ownership-boundary shrink: move a capability (entry delete + its keeper escalation) OFF the author seam INLINE into the framework, reusing an existing tail via a shared private helper"
    - "DRY completion-tail: one DeleteEntryTail called from both the Mode-1 inline tail and the Mode-2 null-return path (source-skip + delete-exhaust → DELETE keeper)"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/Processor.Sample/SampleProcessor.cs
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    - tests/BaseApi.Tests/Processor/PrePipelineFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs

key-decisions:
  - "Extracted a shared private DeleteEntryTail(EntryStepDispatch, IDatabase, int, CancellationToken) from the Mode-1 tail and called it from BOTH sites (RESEARCH Open-Q 1 recommendation) — reads as framework-owned deletion at both completion paths, no duplicated retry/escalation logic"
  - "Removed the entire author DeleteEntry surface in ONE atomic compile unit (BaseProcessor.DeleteEntry method + SeamState.EntryId + SeamState.EscalateDelete + the SetSeamState entryId/escalateDelete params + the pipeline escalateDelete closure + the SampleProcessor call) so the SetSeamState signature change breaks and is fixed across all callers together"
  - "PrePipelineFacts.SeamReturnsNull re-pointed to a Guid.Empty source (matches real Mode-2 semantics: the scheduler seeds Guid.Empty) so the DidNotReceive().KeyDeleteAsync assertion now proves the !IsSource SKIP rather than a coincidental non-delete; a NEW non-source fact proves the framework DOES delete a real entryId and a delete-exhaust variant proves the DELETE escalation moved onto the null path"

requirements-completed: [PB-03]

# Metrics
duration: 40min
completed: 2026-07-22
---

# Phase 85 Plan 03: Framework-Owned Entry Deletion (PB-03) Summary

**Entry/source deletion moved entirely off the author seam INTO the framework: a shared `ProcessorPipeline.DeleteEntryTail` now issues the `L2[entryId]` delete + DELETE-keeper escalation on BOTH completion paths (the Mode-1 inline tail and the Mode-2 null-return path), the concrete `DeleteEntry` API / `SeamState.EntryId` / `SeamState.EscalateDelete` are removed, and the `SampleProcessor` no longer deletes — with the `Guid.Empty` source net-effect and the keeper DELETE escalation both preserved.**

## Performance

- **Duration:** ~40 min
- **Completed:** 2026-07-22
- **Tasks:** 3
- **Files modified:** 7 (0 created, 7 modified)

## Accomplishments
- Removed the author-owned entry-delete surface from `BaseProcessor` in one atomic compile unit: the `DeleteEntry()` method, `SeamState.EntryId`, `SeamState.EscalateDelete`, the `SetSeamState` `entryId`/`escalateDelete` params (+ their assignments), and the now-unused `Messaging.Contracts.Projections` using.
- Extracted a shared private `DeleteEntryTail(EntryStepDispatch d, IDatabase db, int limit, CancellationToken ct)` from the Mode-1 tail (source-skip → `RetryLoop` `KeyDeleteAsync(L2[entryId])` → delete-exhaust `SendKeeper(BuildDelete(d))`) and called it from BOTH the Mode-1 inline tail and the Mode-2 `dr is null` branch (after `LogHopExecuted`, after the succeeded spawns).
- Dropped the pipeline's `entryId:` arg + `escalateDelete:` closure from the `SetSeamState` wiring; kept the `onSpawnDropped:` PB-01 telemetry closure verbatim.
- `SampleProcessor` Mode-2 entry path now spawns two + `return null` with NO author delete call — the spawn-exhaust throw propagates transparently (D-01), the author writes no catch/delete/escalation.
- Updated the four hermetic fact files in lockstep: dropped `FakeProcessor.DeleteEntryAsync`; flipped the `SampleProcessorFacts` entry fact to `db.DidNotReceive().KeyDeleteAsync(...)`; re-pointed `PrePipelineFacts.SeamReturnsNull` to a `Guid.Empty` source; added a non-source null-path delete fact (`db.Received(1).KeyDeleteAsync(ExecutionData(entryId), ...)`) and a delete-exhaust variant (exactly one `KeeperDelete`); removed the two obsolete `DeleteEntry_*` seam facts and re-expressed `ConcurrentConsumes` to the messageId-stamp isolation only.

## Task Commits

Each task was committed atomically:

1. **Task 1: Remove the DeleteEntry surface from BaseProcessor** - `179cf7a` (refactor)
2. **Task 2: Move deletion into the pipeline null path + remove the SampleProcessor DeleteEntry call** - `9c3ec2a` (feat)
3. **Task 3: Update the hermetic tests in lockstep** - `85972a2` (test)

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` - Removed `DeleteEntry()`, `SeamState.EntryId`/`EscalateDelete`, and the `SetSeamState` `entryId`/`escalateDelete` params; updated the class + CR-01 doc-comments to framework-owned deletion; dropped the now-unused projections using.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - Extracted the shared `DeleteEntryTail`; grafted it onto the null-return branch after `LogHopExecuted`; dropped `entryId`/`escalateDelete` from the `SetSeamState` wiring (kept `onSpawnDropped`); updated the class + `SetSeamState` doc-comments.
- `src/Processor.Sample/SampleProcessor.cs` - Removed the `this.DeleteEntry()` call; the Mode-2 path now only spawns + returns null; doc-comments updated to fail-loud spawn + framework-owned delete.
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` - Removed `FakeProcessor.DeleteEntryAsync`; updated the Mode-2 delegate + class doc-comments (kept `SpawnToPostAsync`/`NewResultPublic` and the `ReadWriteDeleteOkL2`/`ReadOkDeleteFaultL2` fakes — now exercised by the pipeline null-path delete).
- `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` - `WireSeam` lost `entryId`/`escalateDelete`; the entry fact renamed + flipped to `DidNotReceive().KeyDeleteAsync`; class doc updated.
- `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` - `WireSeam` + the three SpawnToPost facts lost `entryId`/`escalateDelete`; removed `DeleteEntry_Success_NoEscalation` + `DeleteEntry_DeleteExhaust_Escalates_ToDeleteHook`; re-expressed `ConcurrentConsumes` to the messageId-stamp isolation; dropped the unused projections using; header doc updated.
- `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs` - `SeamReturnsNull` re-pointed to a `Guid.Empty` source; added `SeamReturnsNull_NonSource_FrameworkDeletesEntry` + `SeamReturnsNull_NonSource_DeleteExhaust_EscalatesDelete`; class doc req-3 updated.

## Decisions Made
- Chose the shared-helper form (`DeleteEntryTail` called from both sites) over inlining — RESEARCH Open-Q 1's recommendation. `db`/`limit`/`SendKeeper`/`BuildDelete(d)` were already in `RunAsync` scope, so the helper needed no new plumbing, and both completion paths now read identically as "framework-owned delete-then-escalate".
- Kept the removal a single atomic compile unit across production + tests: the `SetSeamState` signature change intentionally breaks every caller at once (Task 1 alone does not compile solution-wide — its acceptance is source-grep only), then Tasks 2-3 restore green.
- Re-pointed the `SeamReturnsNull` fact to a `Guid.Empty` source rather than leaving it on a random entryId — this makes the `DidNotReceive().KeyDeleteAsync` assertion prove the real `!IsSource` SKIP (matching the scheduler's Mode-2 seed), and the new non-source fact carries the "framework DOES delete a real entryId" half that used to live on the author `DeleteEntry_Success` seam fact.
- Left the DELETE-keeper escalation semantics byte-identical — only the ISSUER moved (author `EscalateDelete` closure → inline `DeleteEntryTail.SendKeeper(BuildDelete(d))`); the new delete-exhaust null-path fact asserts exactly one `KeeperDelete`, proving T-85-05 (no lost escalation).

## Deviations from Plan

None - plan executed exactly as written. (Two incidental hygiene edits within scope: dropped the now-unused `Messaging.Contracts.Projections` using from `BaseProcessor.cs` and `BaseProcessorSeamFacts.cs` after their last `L2ProjectionKeys` reference was removed — required to hold the 0-warning bar; tracked here rather than as a rule-numbered deviation since both are direct consequences of the plan's removals.)

## TDD Gate Compliance

Task 3 is marked `tdd="true"`, but the plan sequences the production removal/relocation as Tasks 1-2 and the lockstep test updates as Task 3 (impl-then-test as atomic tasks — the same ordering as Plans 01/02). Consequently the RED phase did not fail-first: because the `SetSeamState` signature change breaks compilation of all callers at once, the test files could not build (let alone run RED) until Task 3 updated them in lockstep with Tasks 1-2. The RED intent (assert the NEW framework-owned deletion semantics + the preserved `Guid.Empty` source net-effect + the relocated DELETE escalation) and the GREEN outcome are both satisfied. Commit sequence: `refactor(179cf7a)` → `feat(9c3ec2a)` → `test(85972a2)`.

## Issues Encountered
- The full hermetic suite (`--filter-not-trait Category=RealStack`) reports many failures in this executor sandbox, but they are entirely environmental: no RabbitMQ/Redis broker is running here, so broker-dependent (non-RealStack-tagged) liveness/integration tests fail on connect. None touch the PB-03 deletion path. All plan-relevant hermetic facts pass: `BaseProcessorSeamFacts` + `SampleProcessorFacts` + `PrePipelineFacts` = **44/44** (Debug). Solution builds **0-warning** in both Release and Debug. The full live suite + the SC-4 seven-scenario sweep run in Wave 4 against real infra.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- PB-03 complete: the concrete-processor surface is now exactly "interpret input → business logic → return DataResult (or spawn N + return null)"; entry deletion + its keeper escalation are framework-owned on both completion paths. The `Guid.Empty` source net-effect is unchanged (D-04), verified by the `SeamReturnsNull_Source` fact and to be re-confirmed live by the SC-4 sweep (Plan 04).
- Manual A1 (VALIDATION.md) remains for Wave 4: confirm the scheduler seeds `Guid.Empty` for the Mode-2 entry so the `!IsSource` skip is the operative branch in production.
- No blockers.

---
*Phase: 85-processor-framework-boundary-hardening*
*Completed: 2026-07-22*

## Self-Check: PASSED

All seven declared source/test files + the SUMMARY exist; all three task commits (179cf7a, 9c3ec2a, 85972a2) are present in git history.
