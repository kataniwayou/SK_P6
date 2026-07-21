---
phase: 75-recovery-verdict-per-execution-drop-tunable-constants
plan: 02
subsystem: infra
tags: [keeper, recovery, telemetry, observability, elasticsearch, otel, structured-logging]

# Dependency graph
requires:
  - phase: 75-01
    provides: "keeperOutcomeByExecution classifier param in PassFailEngine.Analyze (consumes attributes.ReinjectOutcome once the live ES-join lands)"
  - phase: 74
    provides: "keeper CountSent / keeper_messages_sent send-success discipline the reinject-success log sits after"
provides:
  - "Keeper REINJECT drop log widened to carry (CorrelationId, ExecutionId, EntryId, MessageId, ReinjectOutcome=drop) as message-template placeholders"
  - "New symmetric REINJECT-success LogInformation after CountSent carrying the same four ids + ReinjectOutcome=reinject"
  - "CapturingLogger<T> hermetic test double materializing structured message-template state"
  - "ReinjectConsumerFacts assert the five join fields on both drop and success paths"
affects: [75-04, keeper-es-join, analyzer-recoverability-classifier]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Explicit message-template {Placeholder} join-key logging for consumers with NO ambient BeginScope (keeper RecoveryConsumerBase) — mirrors SampleProcessor:77 but passes all ids explicitly since no ExecutionLogScope applies"
    - "CapturingLogger<T> : ILogger<T> that reads state as IReadOnlyList<KeyValuePair<string,object?>> to assert placeholder-form fields hermetically"

key-files:
  created: []
  modified:
    - src/Keeper/Recovery/ReinjectConsumer.cs
    - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs

key-decisions:
  - "ReinjectOutcome discriminator is a static compile-time literal (\"drop\"/\"reinject\") — the analyzer (Plan 04) partitions keeper docs on attributes.ReinjectOutcome"
  - "Success log placed AFTER CountSent (never on the drop early-return) preserving the Phase-74 send-success / drop-no-send invariant (sent==1 / sent==0)"
  - "m.Payload is NEVER added to any log template — preserves the file's never-log-Payload invariant (T-75-03 information-disclosure mitigation)"
  - "All four ids ride as explicit placeholder args, not string interpolation — RecoveryConsumerBase.Consume opens no BeginScope, so only placeholder-form surfaces as ES attributes.* (Pitfall 2)"

patterns-established:
  - "Placeholder-form join-key logging for scope-less recovery consumers"
  - "Capturing-logger hermetic proof of structured log fields without a live ES/OTLP stack"

requirements-completed: [D75-4]

# Metrics
duration: 43min
completed: 2026-07-15
---

# Phase 75 Plan 02: Keeper reinject telemetry join-key widening Summary

**Widened the keeper REINJECT drop log and added a symmetric reinject-success log, both carrying `(CorrelationId, ExecutionId, EntryId, MessageId, ReinjectOutcome)` as message-template placeholders so the analyzer can join keeper recovery outcomes to lost `(correlationId, executionId)` executions via ES `attributes.*`.**

## Performance

- **Duration:** 43 min
- **Started:** 2026-07-15T11:49:50Z
- **Completed:** 2026-07-15T12:32:47Z
- **Tasks:** 2 (TDD: RED + GREEN)
- **Files modified:** 2

## Accomplishments
- Closed the structured hole in the keeper's clean-absent DROP log: it previously carried only `EntryId`; it now carries all four join keys plus a `ReinjectOutcome="drop"` discriminator.
- Added a new symmetric `LogInformation` on the reinject-success path (after the confirmed `CountSent`) carrying the same four ids + `ReinjectOutcome="reinject"`, so a recovered (recoverable) execution is also joinable.
- All join keys ride as explicit message-template `{Placeholder}` args (Pitfall 2 — the keeper consumer has NO ambient `BeginScope`), so they will surface as ES `attributes.CorrelationId/ExecutionId/EntryId/MessageId/ReinjectOutcome` via the existing MEL→OTLP bridge (`ParseStateValues=true`).
- Proved both paths hermetically with a new `CapturingLogger<T>` that materializes the structured message-template state — no live ES/OTLP stack required.

## Task Commits

Each task was committed atomically (TDD cycle):

1. **Task 1: Assert structured join fields via capturing logger (RED)** - `5517a58` (test)
2. **Task 2: Widen drop log + add reinject-success log with join keys (GREEN)** - `b00b702` (feat)

**Plan metadata:** (this commit) (docs: complete plan)

## Files Created/Modified
- `src/Keeper/Recovery/ReinjectConsumer.cs` - Drop `LogWarning` widened to `{CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}` (="drop"); new `LogInformation` "REINJECT sent" after `CountSent` with the same five placeholders (="reinject").
- `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` - Added `CapturingLogger<T> : ILogger<T>`; swapped `NullLogger` for it on both facts; both facts now assert the five join fields (`AssertJoinFields` helper) with `ReinjectOutcome` "drop"/"reinject"; existing `MeterListener` sent-count asserts (0/1) unchanged.

## Decisions Made
- `ReinjectOutcome` values are static compile-time literals (no external input controls the template — T-75-04 tampering accepted).
- Placeholder-form only, no `$"..."` interpolation, no `m.Payload` in any template — the two acceptance-critical invariants (attributes surfacing + never-log-Payload) are grep-verified.
- Success log positioned strictly after `CountSent` so the drop path (early-return) never emits it and the Phase-74 counter semantics (sent==1 success / sent==0 drop) are preserved.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None. RED failed for the exact expected reason (drop log carried only `EntryId`; success path emitted no `Information` log); GREEN turned both facts green with a clean 0-warning Debug and Release (Keeper) build.

## Threat Surface

No new threat surface beyond the plan's `<threat_model>`. T-75-03 (information disclosure) mitigation upheld: only correlation/execution/entry/message GUIDs + a static outcome label are logged; `m.Payload` is never logged (grep-verified — no `m.Payload` in any log template). T-75-04 (tampering) accepted: field names and outcome values are compile-time literals.

## Test Results
- `*ReinjectConsumerFacts`: 6/6 GREEN (both widened facts pass; the 4 sibling facts unaffected).
- Full hermetic suite (`--filter-not-trait Category=RealStack`): 538/810 pass; the 272 failures are ALL pre-existing infra-dependent tests (Integration/Controller/broker `rabbitmq://` connection failures) in this Docker-less sandbox — NONE are `ReinjectConsumer`/`PassFailEngine` facts (grep-confirmed). Judged per the plan note: success = new facts green + clean build, not the global count.
- Build: 0-warning Debug (test project) and 0-warning Release (Keeper).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The keeper now emits joinable drop/success telemetry. Plan 04's live ES-join (D75-4 live half — RealStack, Docker) can query `attributes.ReinjectOutcome` + the four id fields to build the `keeperOutcomeByExecution` map that Plan 01's classifier already consumes.
- Live ES-surfacing of the new keeper attributes remains deferred-automated (Docker unavailable), consistent with prior phases — validated in Plan 04 on the live stack.

## Self-Check: PASSED

- Files verified present: `src/Keeper/Recovery/ReinjectConsumer.cs`, `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs`, `75-02-SUMMARY.md`.
- Commits verified present: `5517a58` (RED test), `b00b702` (GREEN feat).

---
*Phase: 75-recovery-verdict-per-execution-drop-tunable-constants*
*Completed: 2026-07-15*
