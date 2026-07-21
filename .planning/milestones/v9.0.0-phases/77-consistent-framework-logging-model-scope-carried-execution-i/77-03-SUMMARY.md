---
phase: 77-consistent-framework-logging-model-scope-carried-execution-i
plan: 03
subsystem: infra
tags: [logging, observability, orchestrator, masstransit, opentelemetry, three-tier-logging]

# Dependency graph
requires:
  - phase: 77 (plans 01-02)
    provides: three-tier logging model (Tier-1 via scope, {MessageId} on send, domain extra otherwise); scope filter emits WorkflowId/StepId/ProcessorId/ExecutionId/EntryId as attributes.*
provides:
  - "OrchestratorPrePipeline execution-path records stripped of redundant Tier-1 id placeholders"
  - "terminal-reached is now the bare argument-less marker (ids arrive via ambient scope)"
  - "fan-out edge record mints + stamps + logs its outbound {MessageId} (reverses D-11 Option C)"
  - "four business trip-end lines carry no Tier-1 id (Tier-3 extras outcome/NextStepId preserved)"
affects: [phase-78-sweep-rework, analyzer, PassFailEngine, AnalyzerE2ETests]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Outbound envelope id captured via send-context callback (ctx => ctx.MessageId = outboundId) and logged as Tier-2 {MessageId}"
    - "Framework execution records restate NO scope-carried id in the template; Tier-1 ids arrive as attributes.* only"

key-files:
  created:
    - .planning/phases/77-consistent-framework-logging-model-scope-carried-execution-i/77-03-SUMMARY.md
  modified:
    - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs

key-decisions:
  - "Reversed D-11 Option C: the fan-out outbound envelope id is now minted once per match, stamped via the send-context callback, and logged as {MessageId} — the id is now in hand at the Pre loop"
  - "terminal reached became an argument-less marker; its five Tier-1 ids are attribute-only (scope), documented Phase-78 analyzer handoff"
  - "CapturingSendProvider fault-injection hook now fires on the override-overload send too, since the fan-out switched from the plain to the ctx.MessageId override overload"

patterns-established:
  - "Tier-1 from scope, {MessageId} on send, domain extra otherwise — no id ever restated in a template the scope already carries"

requirements-completed: [LOG-01, LOG-03, LOG-06]

# Metrics
duration: 12min
completed: 2026-07-16
---

# Phase 77 Plan 03: Orchestrator execution-path Tier-1 strip + fan-out MessageId capture Summary

**Stripped the six orchestrator execution-path framework records of redundant Tier-1 id placeholders and made the fan-out edge mint + stamp + log its outbound `{MessageId}` (reversing D-11 Option C) — Tier-1 now arrives only via the ambient MEL execution scope.**

## Performance

- **Duration:** ~12 min
- **Started:** 2026-07-16T16:47:00Z
- **Completed:** 2026-07-16T16:59:44Z
- **Tasks:** 3
- **Files modified:** 2

## Accomplishments
- `terminal reached` is now the bare argument-less marker; all five Tier-1 ids (Correlation/Execution/Workflow/Entry/Step) arrive via `attributes.*` scope, not the template.
- The fan-out edge record mints an `outboundId` per match, stamps it on the send via `ctx => ctx.MessageId = outboundId`, and logs `fan-out {MessageId} {NextStepId}` — capturing the id D-11 Option C dropped.
- The four business trip-end lines (completed-unresolved, completed-terminal, clean-absent, dangling) dropped `{WorkflowId}`/`{StepId}` while keeping their Tier-3 extras (`outcome={Outcome}`, `{NextStepId}`).
- All FW-04 `try/catch` observability guards and the `metrics.MessagesSent` increment preserved verbatim; Debug **and** Release build 0-warning; `*OrchestratorPrePipeline*` facts 21/21 GREEN.

## Task Commits

Each task was committed atomically:

1. **Task 1: Strip Tier-1 from terminal-reached + four business trip-end lines** - `b2c397f` (refactor)
2. **Task 2: Capture the outbound MessageId on fan-out + strip its Tier-1 placeholders** - `27f70d8` (feat)
3. **Task 3: Re-key OrchestratorPrePipelineFacts to the stripped + captured shape** - `47f8829` (test)

## Files Created/Modified
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` - stripped Tier-1 placeholders from the six execution-path records; fan-out now mints/stamps/logs its outbound `{MessageId}`.
- `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` - `EdgeRecords` re-keyed to NextStepId+MessageId; `TerminalReachedRecords` matches the formatted message; fan-out facts assert one minted override id per handoff == the logged `{MessageId}`; terminal-reached asserts no id placeholders.

## Decisions Made
- **Reversed D-11 Option C** for the fan-out outbound id — captured via the established send-context callback idiom (`BaseProcessor.cs:102`), minted once per match outside the RetryLoop lambda.
- **terminal-reached is attribute-only** — the marker carries no placeholder args; the distinguishing EntryId at a double fan-in arrives via scope. Documented as a Phase-78 analyzer handoff (fan-out/terminal parsing adapts there).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] CapturingSendProvider fault hook did not fire on the override-overload send**
- **Found during:** Task 3 (re-key pipeline facts)
- **Issue:** Task 2 switched the fan-out from the plain `Send(object, ct)` overload to the `Send(object, ctx => ctx.MessageId = …, ct)` override overload. The test double only invoked its `_onSend` fault-injection hook on the plain overload, so `Send_exhaust_throws_and_does_not_delete` no longer exercised the throw path (no exception thrown → fact failed).
- **Fix:** Added `if (_onSend is not null) await _onSend();` to the override-overload branch of `CapturingSendProvider.GetSendEndpoint`, so send-exhaust fault injection applies to whichever overload production uses.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs
- **Verification:** `Send_exhaust_throws_and_does_not_delete` + `Delete_exhaust_escalates_one_DELETE` pass; full `*OrchestratorPrePipeline*` 21/21 GREEN.
- **Committed in:** `47f8829` (Task 3 commit)

**2. [Rule 1 - Bug] terminal-reached marker asserted `Assert.Empty(State)` but MEL always adds `{OriginalFormat}`**
- **Found during:** Task 3 (re-key pipeline facts)
- **Issue:** The plan's re-key intent was "terminal-reached carries no ids", but even an argument-less `LogInformation("terminal reached")` produces a one-entry State (`{OriginalFormat}` = "terminal reached"), so `Assert.Empty(t.State)` failed.
- **Fix:** Changed the assertion to `Assert.DoesNotContain(t.State, kv => kv.Key is "CorrelationId" or "ExecutionId" or "WorkflowId" or "EntryId" or "StepId" or "MessageId" or "NextStepId")` — asserts no *id* placeholder, tolerating the implicit `{OriginalFormat}`.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs
- **Verification:** `Terminal_reached_emits_two_records_at_double_fanin` GREEN.
- **Committed in:** `47f8829` (Task 3 commit)

---

**Total deviations:** 2 auto-fixed (2 bugs, both directly caused by this plan's own Task 2 template/overload change)
**Impact on plan:** Both auto-fixes necessary to make the re-keyed facts correct and green. No scope creep — confined to the plan's own test file. The renamed fact `Terminal_reached_emits_two_records_at_double_fanin` (dropped the `_by_distinct_EntryId` suffix) reflects that the EntryId distinction now lives in scope, not captured State.

## TDD Gate Compliance
Plan `type: execute` (not `type: tdd`); tasks carry `tdd="true"` in the refactor sense. The production template strips (Tasks 1-2) intentionally break the pre-existing facts that assert the OLD log shape (RED), and Task 3 re-keys them to the new shape (GREEN). Commit sequence: `refactor` (Task 1) → `feat` (Task 2) → `test` (Task 3). Because Tasks 1-2 verify build-only (the facts are re-keyed in Task 3), no intermediate commit ran the failing facts.

## Issues Encountered
- The `--filter-method` invocation initially printed help + exited 5 because the `--` separator (used with `dotnet run`) is not passed when invoking `BaseApi.Tests.exe` directly. Dropping `--` ran the filter correctly.

## Threat Flags
None — no new network endpoints, auth paths, file access, or schema surface introduced. The changes stay within the threat-model register (T-77-07 information-disclosure mitigated: no `relocated`/`handoff.Data` blob is ever a log arg — grep-asserted 0; T-77-08 DoS mitigated: all changed logs stay inside their FW-04 try/catch).

## Known Stubs
None.

## Next Phase Readiness
- Orchestrator execution-path records now conform to the three-tier model. Phase 78 must adapt the LIVE analyzer: fan-out parsing to `fan-out {MessageId} {NextStepId}`, terminal-reached to the attribute-only marker (ids absent from the string), and the entry-marker check to "attribute absent" rather than `== Guid.Empty`.
- Remaining Phase-77 plans (77-04..06) continue the source-side refactor across the other framework components (ProcessorPipeline hop-executed, OutputTail result send, Keeper recovery consumers, Processor.Sample log removal).

## Self-Check: PASSED

---
*Phase: 77-consistent-framework-logging-model-scope-carried-execution-i*
*Completed: 2026-07-16*
