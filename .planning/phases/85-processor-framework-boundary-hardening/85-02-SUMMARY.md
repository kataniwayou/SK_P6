---
phase: 85-processor-framework-boundary-hardening
plan: 02
subsystem: infra
tags: [dotnet, masstransit, rabbitmq, processor-framework, fail-loud, exception-handling, nack-requeue]

# Dependency graph
requires:
  - phase: 85-processor-framework-boundary-hardening
    plan: 01
    provides: SpawnSendExhaustedException (dedicated fail-loud Mode-2 spawn-exhaust escape) + DispatchTestKit.SendFaultProvider
provides:
  - "ProcessorPipeline.RunAsync PROPAGATES SpawnSendExhaustedException (nack-requeue) instead of acking a defeated Mode-2 spawn as StepFailed (PB-02)"
  - "D-03 poison-safety preserved: deterministic seam faults still route to the generic catch → StepFailed + ack (guarded by an explicit negative-control fact)"
affects: [85-03 (delete-only-on-success ordering relies on this throw guaranteeing no-delete-on-exhaust), KafkaImporter KIMP-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Nack-by-rethrow: a narrow catch that RE-PROPAGATES a dedicated exception (bare throw) so it escapes the consumer → MassTransit default RabbitMQ nack-requeue (no new redelivery/backoff config)"
    - "Narrow exception recognition between two existing catches (a third recognized type that ESCAPES rather than routes to OutputTail+ack)"
    - "Plain-construct pipeline hermetic fact driven by a faulting ISendEndpointProvider through the public ctor"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - tests/BaseApi.Tests/Processor/PrePipelineFacts.cs

key-decisions:
  - "Used the explicit rethrow-catch shape `catch (SpawnSendExhaustedException) { throw; }` (matching the ProcessStatusException catch ordering above it) over the equivalent `when (ex is not ...)` guard — reads better against the existing precedent"
  - "D-05 nack fact drives the REAL SampleProcessor Mode-2 entry fan-out via a Guid.Empty source dispatch (Open-Q 2 recommendation — exercises the true fan-out path) rather than a FakeProcessor delegate"
  - "The throw itself is the terminal no-ack signal: `Assert.ThrowsAsync<SpawnSendExhaustedException>` proves the narrow catch rethrew directly without ever reaching the generic catch / OutputTail (no StepFailed produced)"

requirements-completed: [PB-02]

# Metrics
duration: 18min
completed: 2026-07-22
---

# Phase 85 Plan 02: Narrow Nack-Escape for a Defeated Mode-2 Spawn (PB-02) Summary

**`ProcessorPipeline.RunAsync` now catches the dedicated `SpawnSendExhaustedException` narrowly and re-propagates it (bare `throw`) so a defeated Mode-2 spawn escapes the consumer → MassTransit default RabbitMQ nack-requeue, instead of being routed to `OutputTail`+ack as a silent `StepFailed`; deterministic faults still ack (D-03 poison-safety intact).**

## Performance

- **Duration:** ~18 min
- **Completed:** 2026-07-22
- **Tasks:** 2
- **Files modified:** 2 (0 created, 2 modified)

## Accomplishments
- Inserted `catch (SpawnSendExhaustedException) { throw; }` in `ProcessorPipeline.RunAsync`, positioned between the existing `catch (ProcessStatusException e)` block and the generic `catch (Exception ex)` block — the ack/nack fulcrum (PB-02).
- The bare `throw` reuses the proven `SendKeeper` `:304` escape mechanism (uncaught throw out of the consumer → RabbitMQ nack-requeue). No new redelivery/backoff/endpoint configuration was introduced (D-02).
- The generic `catch (Exception ex)` block is unchanged: `JsonException`/`ArgumentException`/`InvalidOperationException`/deserialize faults still route to `OutputTail` → `StepFailed` + ack, so they cannot nack-loop forever on redelivery (D-03 crux).
- Added the D-05 nack hermetic fact: a `Guid.Empty` source dispatch through a transient-faulting `SendFaultProvider` drives the real `SampleProcessor` Mode-2 fan-out; the defeated spawn re-propagates `SpawnSendExhaustedException` out of `RunAsync` (nack), never producing a `StepFailed` (ack).
- Added the explicit D-03 negative-control fact: a deterministic `InvalidOperationException` seam fault ⇒ `RunAsync` does NOT throw and emits exactly one `StepFailed` — the narrow filter did not let it escape.

## Task Commits

Each task was committed atomically:

1. **Task 1: Insert the narrow nack-escape catch in ProcessorPipeline.RunAsync** - `d6c3412` (feat)
2. **Task 2: Add the D-05 nack fact + the explicit D-03 negative-control fact** - `a725ad6` (test)

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - Added the narrow `catch (SpawnSendExhaustedException) { throw; }` between the `ProcessStatusException` and generic catches (9 lines incl. the D-02/D-03 rationale comment). Generic catch and all other paths byte-identical.
- `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs` - Added a `BuildWith(..., ISendEndpointProvider send)` overload (plain-construct via the public ctor for a faulting provider), the D-05 nack fact (`SpawnExhaust_Propagates_SpawnSendExhaustedException_Nack_NoStepFailed`), and the explicit D-03 negative-control fact (`DeterministicSeamFault_DoesNotEscape_OneStepFailed_Ack`); added `MassTransit` + `Processor.Sample` usings.

## Decisions Made
- Chose the explicit `catch (SpawnSendExhaustedException) { throw; }` form (not the equivalent `when (ex is not SpawnSendExhaustedException)` guard on the generic catch) — it mirrors the `ProcessStatusException` catch ordering directly above it and reads as "a third recognized type that ESCAPES."
- The D-05 fact drives the real `SampleProcessor` entry fan-out (Open-Q 2 recommendation — true Mode-2 path) with a `Guid.Empty` source dispatch, which skips the gate/read and enters the entry spawn loop; the first `SpawnToPost` exhausts and throws.
- Asserted the nack via the exception TYPE alone: `Assert.ThrowsAsync<SpawnSendExhaustedException>` precisely proves the narrow catch rethrew it (had the narrow catch been absent, the generic catch would have caught it, routed to `OutputTail`, and returned WITHOUT throwing) — so the throw is a sufficient "did not ack / no StepFailed" signal (per the plan's terminal-signal guidance for the faulting provider).
- Left the null-return delete tail (`:206`) and the `SetSeamState` `escalateDelete` closure untouched — those are Plan 03 (PB-03) scope, per the plan's explicit note.

## Deviations from Plan

None - plan executed exactly as written.

## TDD Gate Compliance

Task 2 is marked `tdd="true"`, but the plan sequences the production catch as Task 1 and the new/inverting facts as Task 2 (impl-then-test as two atomic tasks — the same ordering as Plan 01). Consequently the RED phase did not fail-first: the Task-2 facts were GREEN immediately against the already-committed Task-1 code. This is a consequence of the plan's task ordering, not a skipped gate — the RED intent (assert the new nack behavior + the D-03 poison-safety boundary) and GREEN outcome are both satisfied, and the D-03 negative-control fact independently guards the narrow-filter boundary. The commit sequence is `feat(d6c3412)` then `test(a725ad6)`.

## Issues Encountered
- The full hermetic suite (`--filter-not-trait Category=RealStack`) reports many failures in this executor sandbox, but they are entirely environmental: no RabbitMQ/Redis broker is running here, so broker-dependent (non-RealStack-tagged) liveness/integration tests fail on connect. None touch `ProcessorPipeline`. All plan-relevant hermetic facts pass: `PrePipelineFacts` 15/15 (13 existing + the 2 new), and the combined `BaseProcessorSeamFacts` + `SampleProcessorFacts` set 10/10 (the standing D-03 controls `MalformedPayload_DeserFailure_*` and `SeamThrows_Unexpected_Failed` remain green unchanged). Solution builds 0-warning in both Release and Debug. The full live suite runs in Wave 4 against real infra.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- PB-02 is complete: a defeated spawn now nack-requeues rather than acking. Plan 03 (PB-03) can now remove the author-owned `DeleteEntry` and move deletion to the framework null-path (delete-only-on-success), relying on this plan's throw to guarantee "no delete when any spawn exhausts" (if any spawn throws, `RunAsync` never reaches the null-return delete tail — the narrow catch rethrows → nack → no delete).
- No blockers.

---
*Phase: 85-processor-framework-boundary-hardening*
*Completed: 2026-07-22*

## Self-Check: PASSED

All declared files exist (`ProcessorPipeline.cs`, `PrePipelineFacts.cs`, `85-02-SUMMARY.md`); both task commits (d6c3412, a725ad6) are present in git history.
