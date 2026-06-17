---
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
plan: 01
subsystem: processor
tags: [redis, l2-projection, output-tail, processor-pipeline, step-result, always-write]

# Dependency graph
requires:
  - phase: 70-two-consumer-processor-design-pre-process-post-process-with-
    provides: "OutputTail shared write-tail + ProcessorPipeline linear Pre flow + DataResult spine"
provides:
  - "OutputTail always writes L2[out:messageId]=data for the 3 terminal outcomes (Completed/Failed/Cancelled); Processing keeps no blob"
  - "StepFailed/StepCancelled now carry a real EntryId (= output messageId); only Processing keeps Guid.Empty"
  - "ProcessorPipeline input-fail + both catch blocks route through OutputTail carrying Data=validatedData"
  - "DataResult carries ErrorMessage/CancellationMessage so rerouted catch paths keep their diagnostics through the tail"
affects: [72-02, 72-03, orchestrator-pre-pipeline, uniform-result-read]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Single-tail always-write: the write gate is result != StepOutcome.Processing (terminal outcomes write, transient does not)"
    - "Catch/input-fail paths build a DataResult and route through OutputTail.RunAsync rather than a bypass SendResult"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Processing/OutputTail.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/Messaging.Contracts/DataResult.cs
    - tests/BaseApi.Tests/Processor/OutputTailFacts.cs
    - tests/BaseApi.Tests/Processor/PrePipelineFacts.cs

key-decisions:
  - "Write gate expressed as result != StepOutcome.Processing (D-04/A3) — cleanest exclusion of the transient status"
  - "EntryId = dr.MessageId stamped in OutputTail.BuildStep (the single write+build seam), not in the now-deleted pipeline builders"
  - "Added ErrorMessage/CancellationMessage to DataResult so the catch-path diagnostics (e.Message, joined input errors, sanitized deser constant) survive routing through OutputTail.BuildStep (Rule 3 — required to make D-07 work; DataResult had no such field)"
  - "Deleted the now-orphaned SendResult/ResultOutcome alongside BuildFailed/BuildCancelled/BuildProcessing (A2) to hold the 0-warning gate — the Step* send now lives entirely in OutputTail"

patterns-established:
  - "Always-write at the single tail: every terminal Step* carries a real blob + EntryId for the orchestrator to read uniformly"
  - "Sanitized-constant discipline preserved end-to-end: the unexpected/deser catch wire field stays 'input deserialization failed' (WR-03), never ex.Message"

requirements-completed: [SPEC-1, SPEC-2, SPEC-3, SPEC-6]

# Metrics
duration: 25min
completed: 2026-06-17
---

# Phase 72 Plan 01: Processor Always-Write to L2 Summary

**OutputTail now writes the out: blob and stamps a real EntryId for every terminal outcome (Completed/Failed/Cancelled), and the ProcessorPipeline catch + input-fail paths route through that tail instead of bypassing it — so every terminal Step\* carries a blob the orchestrator can read uniformly.**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-06-17T10:44:33Z
- **Completed:** 2026-06-17T11:09:44Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Widened the `OutputTail` write gate from `result == Completed` to `result != Processing`, so Failed/Cancelled now write `L2[out:messageId]=data` with the existing jittered TTL (REQ-1/D-04/D-06); Processing still writes nothing.
- Stamped `EntryId = dr.MessageId` on the Failed/Cancelled `BuildStep` arms (REQ-2); Processing keeps `Guid.Empty`.
- Rerouted all three `ProcessorPipeline` bypass paths (input-schema fail, `ProcessStatusException` catch, unexpected/deser catch) through `outputTail.RunAsync` carrying `Data = validatedData` + `MessageId = messageId` (REQ-3/D-07), with no entry delete on failure (D-08).
- Removed the dead `BuildFailed`/`BuildCancelled`/`BuildProcessing` and the orphaned `SendResult`/`ResultOutcome`, holding the 0-warning Debug + Release gate (A2/SPEC-6).

## Task Commits

Each task was committed atomically:

1. **Task 1: Always-write tail + real EntryId on Failed/Cancelled (OutputTail)** - `c7fdbc0` (feat)
2. **Task 2: Route catch + input-fail paths through OutputTail carrying validatedData (ProcessorPipeline)** - `e2076f2` (feat)

_TDD note: each task combined the inverted/new facts and the production change into one atomic feat commit (the RED state was established by code inspection — the existing Completed-only gate made the new Failed/Cancelled write assertions fail before the change)._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/OutputTail.cs` - Write gate widened to `result != Processing`; BuildStep stamps real EntryId on Failed/Cancelled and reads diagnostics from DataResult.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - Three bypass paths now build a DataResult and route through OutputTail; dead builders + orphaned send helpers removed.
- `src/Messaging.Contracts/DataResult.cs` - Added `ErrorMessage`/`CancellationMessage` (default "") so rerouted catch paths carry diagnostics through the tail.
- `tests/BaseApi.Tests/Processor/OutputTailFacts.cs` - Inverted the Failed skip-write fact; added Cancelled write+EntryId and Processing no-write facts.
- `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs` - Extended the input-fail/seam-throw/deser facts to assert the write + blob==validatedData + real EntryId; Processing keeps no-write + Guid.Empty.

## Decisions Made
- Expressed the write gate as `result != StepOutcome.Processing` per D-04/A3 (cleanest terminal-vs-transient split).
- Co-located the EntryId stamp in `OutputTail.BuildStep` (the single write+build seam) rather than the pipeline, per the RESEARCH drift note (second `Guid.Empty` source).
- Kept `SendKeeper` (REINJECT/DELETE) in the pipeline; only the Step* result send moved into OutputTail.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added ErrorMessage/CancellationMessage to DataResult**
- **Found during:** Task 1/Task 2 (BuildStep + catch-path routing)
- **Issue:** The plan's Task 2 instructs building `DataResult{... ErrorMessage = ...}`, but `DataResult` had no `ErrorMessage`/`CancellationMessage` field. Without them the rerouted catch-path diagnostics (`e.Message`, the joined input-validation errors, the sanitized `"input deserialization failed"` constant) would be lost when the path routes through `OutputTail.BuildStep` (which previously hard-coded `"output failed schema validation"`/`""`). The change could not compile/preserve behavior otherwise.
- **Fix:** Added `string ErrorMessage` and `string CancellationMessage` (default `""`) to `DataResult`; `OutputTail.BuildStep` now reads them — Failed falls back to the output-schema constant when empty (the output-validation-forced-Failed case), Cancelled uses `dr.CancellationMessage`.
- **Files modified:** src/Messaging.Contracts/DataResult.cs, src/BaseProcessor.Core/Processing/OutputTail.cs
- **Verification:** PrePipelineFacts assert `failed.ErrorMessage == "x"` / `"input deserialization failed"` and `cancelled.CancellationMessage == "c"` survive the routing; 27/27 GREEN.
- **Committed in:** c7fdbc0 (DataResult + BuildStep) and exercised by e2076f2 (Task 2 facts)

**2. [Rule 3 - Blocking] Deleted orphaned SendResult/ResultOutcome (beyond the plan's A2 builder cleanup)**
- **Found during:** Task 2 (rerouting all sends through OutputTail)
- **Issue:** Once all three catch/input-fail paths route through `OutputTail`, `ProcessorPipeline.SendResult` (and its `ResultOutcome` helper) had zero callers — an unused private member trips the 0-warning gate the plan's A2 cleanup targets.
- **Fix:** Deleted `SendResult` and `ResultOutcome` alongside the plan-specified `BuildFailed`/`BuildCancelled`/`BuildProcessing`. `SendKeeper` (still used by REINJECT/DELETE) retained.
- **Files modified:** src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
- **Verification:** `dotnet build src/BaseProcessor.Core -c Debug -warnaserror` → 0 warnings; full solution Debug + Release → 0 warnings.
- **Committed in:** e2076f2 (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 3 - blocking)
**Impact on plan:** Both were necessary to make D-07's "route through OutputTail" routing actually compile, preserve catch-path diagnostics, and hold the 0-warning gate. No scope creep — both stay within the plan's stated touchpoints (OutputTail, ProcessorPipeline, DataResult).

## Issues Encountered
- The xUnit v3 + Microsoft.Testing.Platform runner ignores VSTest-style `--filter`; the correct filter is `--filter-class "*OutputTailFacts"` passed after `--`. The first run silently executed the full 790-test suite (287 failures were all Postgres:5433 / RabbitMQ:5672 infra-dependent tests that require Docker — out of scope, pre-existing). The two affected hermetic fact classes (OutputTailFacts, PrePipelineFacts) passed in that run and pass in isolation (27/27).
- An orphaned `BaseApi.Tests.exe` (PID 21096) from a prior session held the output DLLs, causing MSB3027 copy-lock build errors; killed it before re-running.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Every terminal processor `Step*` now carries a real blob + `EntryId`, which is the precondition for Plan 03's uniform orchestrator Pre-pipeline (drop the `outcome == Completed` branches and read every outcome's `L2[out:EntryId]` uniformly; Processing rides the clean-absent skip).
- `DataResult.ErrorMessage`/`CancellationMessage` are now available for any downstream path that needs to faithfully rebuild a `Step*` through the tail.

## Self-Check: PASSED

All 5 modified files present; both task commits (`c7fdbc0`, `e2076f2`) exist in git history. Affected hermetic tests GREEN (OutputTailFacts 5/5, PrePipelineFacts 22/22 = 27/27); solution builds 0-warning Debug + Release.

---
*Phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi*
*Completed: 2026-06-17*
