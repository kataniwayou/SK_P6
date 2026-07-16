---
phase: 77-consistent-framework-logging-model-scope-carried-execution-i
plan: 01
subsystem: infra
tags: [logging, observability, opentelemetry, elasticsearch, processor, mel-scope]

# Dependency graph
requires:
  - phase: 76-per-hop-framework-record
    provides: "the per-hop framework Information record (LogHopExecuted) + PerHopLogFacts suite that this plan reshapes"
  - phase: 75-per-execution-recovery-verdict
    provides: "InboundExecutionScopeConsumeFilter / ExecutionLogScope.BuildState ambient scope that now supplies the Tier-1 ids"
provides:
  - "Processor per-hop record whose message template carries ONLY {MessageId} + {Outcome}"
  - "Tier-1 ids (Workflow/Step/Processor/Execution/EntryId) arrive solely from the ambient scope as ES attributes.* — never restated in the string"
  - "PerHopLogFacts asserting MessageId+Outcome present and the four Tier-1 ids absent from explicit template state"
affects: [phase-77-remaining-plans, phase-78-sweep-rework, PassFailEngine.IsEntryMarker]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Consistent framework logging: no id is ever duplicated in a string the ambient MEL scope already carries (D1/LOG-01)"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - tests/BaseApi.Tests/Processor/PerHopLogFacts.cs

key-decisions:
  - "Strip the five Tier-1 execution-id placeholders from the per-hop template; keep Tier-2 {MessageId} (scope does not carry it) + Tier-3 {Outcome} (emitter knows it at emit time)"
  - "Leave the SpawnToPost drop warning's {ExecutionId} byte-for-byte unchanged — it is the freshly minted spawn id, a Tier-3 domain datum the ambient scope does NOT carry (removing it would lose information)"
  - "Mode-2 entry-marker ExecutionId is now ABSENT from attributes.ExecutionId (BuildState skips Guid.Empty) rather than an all-zeros value — the live analyzer switch to attribute-absent is a documented Phase-78 handoff"

patterns-established:
  - "Framework log strings carry only ids the ambient scope cannot supply; all Tier-1 execution ids come via InboundExecutionScopeConsumeFilter scope"

requirements-completed: [LOG-01, LOG-06]

# Metrics
duration: 20min
completed: 2026-07-16
---

# Phase 77 Plan 01: Consistent Per-Hop Framework Record (Tier-1 Strip) Summary

**Processor per-hop framework record reduced to `hop executed {MessageId} {Outcome}` — the five Tier-1 execution ids now arrive only via the ambient InboundExecutionScopeConsumeFilter scope, never duplicated in the message string (D1/LOG-01, D6/LOG-06).**

## Performance

- **Duration:** ~20 min
- **Started:** 2026-07-16T15:56:36Z
- **Completed:** 2026-07-16T16:16:00Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- `ProcessorPipeline.LogHopExecuted` message template stripped to `hop executed {MessageId} {Outcome}`; the five Tier-1 ids (WorkflowId/StepId/ProcessorId/ExecutionId/EntryId) surface as ES `attributes.*` solely through the unchanged ambient scope filter.
- FW-04 swallow-guard preserved; the SpawnToPost drop warning's minted `{ExecutionId}` left untouched.
- Method XML doc rewritten to document the new Tier-2/Tier-3 model and the Phase-78 entry-marker handoff (ExecutionId becomes ABSENT, not all-zeros).
- `PerHopLogFacts` reshaped: `AssertSixFields` → `AssertHopRecord`, asserting `MessageId`+`Outcome` present and `StepId/ExecutionId/CorrelationId/EntryId` absent from explicit template state; Mode-2 fact renamed and its explicit-Guid.Empty assertion dropped. All 10 PerHop facts green.

## Task Commits

Each task was committed atomically:

1. **Task 1: Strip Tier-1 placeholders from LogHopExecuted** - `a7e9131` (feat)
2. **Task 2: Update PerHopLogFacts to the stripped shape** - `83d061f` (test)

_Note: this plan's two tasks reshaped pre-existing Phase-76 behavior + its fact suite; the RED baseline (Phase-76 facts asserting all six ids) was inverted to the new shape in Task 2._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - `LogHopExecuted` template stripped to `{MessageId} {Outcome}`; XML doc updated with the D1/LOG-01 rationale and the Phase-78 handoff note.
- `tests/BaseApi.Tests/Processor/PerHopLogFacts.cs` - `AssertHopRecord` helper (present MessageId+Outcome, absent four Tier-1 ids); all callers updated; Mode-2 fact renamed `NoExplicitExecutionId`; class XML doc refreshed.

## Decisions Made
- Kept the method signature `LogHopExecuted(EntryStepDispatch d, ...)` unchanged even though `d` is no longer used in the log call — callers still pass `d`, and the Mode-2 fact uses it for its `Guid.Empty` precondition. No unused-parameter warning results.
- The SpawnToPost drop warning `{ExecutionId}` is a minted spawn id (Tier-3 domain datum) the scope does not carry, so it was left byte-for-byte to avoid information loss.

## Deviations from Plan
None - plan executed exactly as written. The plan's action for Task 2 also implied refreshing the now-inaccurate class-level XML doc (which had described the old six-id shape); this documentation coherence update was made alongside the required assertion changes and is within the task's stated scope.

## Issues Encountered
- The plan's verify command used `... .exe -- --filter-method "*PerHop*"`. The leading `--` separator caused the Microsoft Testing Platform runner to print usage (exit 5). Ran `BaseApi.Tests.exe --filter-class "*PerHop*"` instead (PerHop is the class name) — 10/10 passed.
- The full hermetic subset (`--filter-not-trait Category=RealStack`) surfaces pre-existing infra-dependent failures (RabbitMQ bus "Not Started", Postgres persistence/integration tests) — the documented Docker-less sandbox baseline, unrelated to this logging change. The in-scope PerHop suite is fully green.

## Verification
- `dotnet build SK_P.sln -c Debug` — 0 Warning, Build succeeded.
- `dotnet build SK_P.sln -c Release` — 0 Warning, Build succeeded.
- `BaseApi.Tests.exe --filter-class "*PerHop*"` — 10/10 passed, exit 0.
- All Task 1 + Task 2 acceptance-criteria greps returned their expected counts.

## Next Phase Readiness
- Remaining Phase-77 plans (02–06) can proceed on the same consistent-logging model.
- **Phase-78 handoff (documented, NOT fixed here):** the Mode-2 entry marker's `attributes.ExecutionId` is now ABSENT rather than an all-zeros value. The live analyzer's `PassFailEngine.IsEntryMarker` (`== Guid.Empty`) must switch to "attribute absent" (Category=RealStack, Phase 78).

## Self-Check: PASSED

- SUMMARY.md exists on disk.
- Task commits `a7e9131` (feat) and `83d061f` (test) present in git history.
- Both modified files (`ProcessorPipeline.cs`, `PerHopLogFacts.cs`) exist.

---
*Phase: 77-consistent-framework-logging-model-scope-carried-execution-i*
*Completed: 2026-07-16*
