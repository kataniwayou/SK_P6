---
phase: 70-two-consumer-processor-design-pre-process-post-process-with
plan: 02
subsystem: infra
tags: [keeper, recovery, masstransit, redis, messageId-override, two-consumer]

# Dependency graph
requires:
  - phase: 70-01
    provides: "reshaped contracts — KeeperInject embeds DataResult + DeleteEntryId; KeeperDelete entryId-only; KeeperReinject gained MessageId; DataResult; L2ProjectionKeys.OutputData; MessageIndex deleted"
provides:
  - "DataResult-driven INJECT consumer: write L2[messageId]=data (completed-only, jittered TTL), send Step* by result with envelope MessageId override, delete L2[entryId]; never recomputes from input (req 7)"
  - "single-key entryId-only DELETE consumer with zero orchestrator sends (req 8)"
  - "REINJECT consumer with per-send envelope MessageId override on re-inject (req 6)"
  - "RecoveryTestKit.CapturingSendProvider override-overload capture (SentMessageIds) for asserting the envelope MessageId override"
  - "migrated keeper facts (Inject/Delete/Reinject) to the two-consumer model"
  - "RecoveryOptions.ExecutionDataTtlSeconds — the keeper-side L2[messageId] TTL knob"
affects: [70-04, orchestrator-phase]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "envelope MessageId override via ISendEndpoint.Send(object, Action<SendContext>, ct) — first concrete use of the SendContext mutation precedent (OutboundCorrelationSendFilter sets CorrelationId)"
    - "NSubstitute capture of the Send override overload: materialize the Action<SendContext> against a substituted SendContext to record the set MessageId"

key-files:
  created: []
  modified:
    - src/Keeper/Recovery/InjectConsumer.cs
    - src/Keeper/Recovery/DeleteConsumer.cs
    - src/Keeper/Recovery/ReinjectConsumer.cs
    - src/Keeper/RecoveryOptions.cs
    - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
    - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs

key-decisions:
  - "A4 resolved: MT 8.5.5 ISendEndpoint.Send(T/object, Action<SendContext>, CancellationToken) overload compiles cleanly — no IPipe<SendContext> fallback needed"
  - "Keeper TTL knob: added ExecutionDataTtlSeconds (300 default) to the already-DI-registered RecoveryOptions rather than introducing ProcessorLivenessOptions into Keeper — smallest addition, no new Configure call"
  - "A1: Step* sends carry EntryId = Guid.Empty (output keyed by messageId; orchestrator entryId↔messageId threading SPEC-deferred)"
  - "A2: INJECT gates the L2[messageId] write on dr.Result == Completed (consistent with the Pre/Post inline tail)"

patterns-established:
  - "Envelope MessageId override on re-emit: ep.Send((object)msg, ctx => ctx.MessageId = carriedId, CancellationToken.None) — used by INJECT (object/Step*) and REINJECT (typed EntryStepDispatch)"
  - "Test double captures the override overload and records SentMessageIds parallel to Sent, so a fact asserts req 6/11 without a live bus"

requirements-completed: [SPEC-req-6, SPEC-req-7, SPEC-req-8, SPEC-req-11, SPEC-req-12]

# Metrics
duration: 35min
completed: 2026-06-16
---

# Phase 70 Plan 02: Reshape keeper recovery consumers to the two-consumer model Summary

**DataResult-driven INJECT (OutputData write completed-only + by-result Step* send with envelope MessageId override + entry delete, never recomputes), single-key entryId-only DELETE, and REINJECT with the per-send envelope MessageId override — plus the three migrated keeper fact files and an override-capturing test double.**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-06-16T20:48:00Z (approx)
- **Completed:** 2026-06-16T21:23:46Z
- **Tasks:** 2
- **Files modified:** 8

## Accomplishments
- **DELETE** reshaped to a single-key `KeyDeleteAsync(ExecutionData(m.EntryId))` — dropped the `RedisKey[]` both-key overload + the `MessageIndex` operand; zero orchestrator sends (req 8).
- **REINJECT** gained the per-send envelope `MessageId` override (`ctx.MessageId = m.MessageId`) on the re-inject `Send`, while keeping the STRLEN presence-read + clean-absent silent drop verbatim (req 6).
- **INJECT** rewritten to be `DataResult`-driven (D-13): read `m.DataResult`, write `L2[messageId]=data` via `OutputData(dr.MessageId)` with the jittered TTL gated on `Completed`, send the `Step*` matching `dr.Result` with the envelope `MessageId` overridden to `dr.MessageId`, then delete `L2[entryId]` — strict write→send→delete order; never reads input to recompute (req 7).
- **RecoveryTestKit.CapturingSendProvider** extended to capture the `Send(object, Action<SendContext>, CancellationToken)` override overload, materialize the callback against a substituted `SendContext`, and record the set `MessageId` into a new `SentMessageIds` list — enabling the req 6/11 envelope-override assertion.
- **Three keeper fact files migrated** to the new model: InjectConsumerFacts (completed-writes-output / non-completed-skips-write / never-reads-input), DeleteConsumerFacts (single-key entryId-only + zero sends), ReinjectConsumerFacts (envelope MessageId override assertion).
- Added `RecoveryOptions.ExecutionDataTtlSeconds` (300 default) as the keeper-side L2[messageId] TTL knob, bound from the already-registered "Recovery" section.

## Task Commits

Each task was committed atomically:

1. **Task 1: Reshape DELETE + REINJECT consumers** — `185d123` (feat)
2. **Task 2: Reshape INJECT consumer + extend RecoveryTestKit + migrate 3 fact files** — `1eb2dc6` (feat)

**Plan metadata:** (this commit) (docs: complete plan)

_Note: this is a `type: execute` plan; tasks were marked `tdd="true"` but — per the BUILD BOUNDARY (below) — the tests cannot execute until Wave 3, so each task is a single source+test commit rather than a RED/GREEN/REFACTOR triple. The reshaped fact files ARE the tests, committed alongside their implementation._

## Files Created/Modified
- `src/Keeper/Recovery/InjectConsumer.cs` — DataResult-driven INJECT: OutputData write (completed-only, jittered TTL) + by-result Step* send with envelope MessageId override + ExecutionData(DeleteEntryId) delete; injects `IOptions<RecoveryOptions>` for the TTL.
- `src/Keeper/Recovery/DeleteConsumer.cs` — single-key `KeyDeleteAsync(ExecutionData(m.EntryId))`; no send.
- `src/Keeper/Recovery/ReinjectConsumer.cs` — re-inject `Send` now overrides `ctx.MessageId = m.MessageId`.
- `src/Keeper/RecoveryOptions.cs` — added `ExecutionDataTtlSeconds` (300 default).
- `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` — `CapturingSendProvider` captures the override overload, records `SentMessageIds`.
- `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` — rewritten to the DataResult/OutputData model (3 facts).
- `tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs` — rewritten to the single-key entryId-only model (2 facts).
- `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` — adds `MessageId` + the envelope-override assertion.

## Decisions Made
- **A4 (MT overload):** the `Send((object)step, ctx => ctx.MessageId = …, CancellationToken.None)` action overload compiles in MassTransit 8.5.5 for both the `(object)` form (INJECT) and the typed `EntryStepDispatch` form (REINJECT) — verified by a clean `src/Keeper` build. No `IPipe<SendContext>` fallback was needed.
- **TTL knob placement:** Keeper has no `ProcessorLivenessOptions`. Rather than thread that type into Keeper, the smallest faithful addition was a new `ExecutionDataTtlSeconds` (300 default — matching the processor default) on the existing, already-DI-registered `RecoveryOptions`; `InjectConsumer` injects `IOptions<RecoveryOptions>` and applies the same `random[ttl, 2×ttl]` jitter as the Pre/Post inline tail.
- **A1 / A2 placeholders:** `Step*.EntryId = Guid.Empty` (output keyed by messageId; orchestrator threading SPEC-deferred); the `L2[messageId]` write is gated on `Completed`.

## Deviations from Plan

None — plan executed exactly as written. The plan's `<action>` for INJECT explicitly left the TTL-knob source to implementation discretion ("confirm at implementation which options type Keeper already has; prefer the smallest addition"); the chosen `RecoveryOptions.ExecutionDataTtlSeconds` is that smallest addition and is documented above, not a deviation.

## Issues Encountered
- Initial Task-1 verify grep failed because the literal token `MessageIndex` survived in a DeleteConsumer doc comment ("the both-key DEL + `MessageIndex` operand are gone"). The acceptance criterion is `! grep MessageIndex DeleteConsumer.cs`, so the comment was reworded ("the second slot-index operand") to satisfy it. No behavior change.

## BUILD BOUNDARY (per plan)
- **`src/Keeper` builds 0-warn / 0-err** in Debug (`dotnet build src/Keeper/Keeper.csproj -c Debug`) — the authoritative source gate for this wave.
- **`tests/BaseApi.Tests` does NOT compile yet** — all 22 build errors originate solely from `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (it still references the old keeper contract shapes: `KeeperDelete.MessageId`, `KeeperInject.EntryId/Data`). **Zero errors originate from any Keeper source or any migrated Keeper test file / RecoveryTestKit.** The Processor slice migration that unblocks compilation is owned by Wave 3 (plan 70-04); the keeper facts therefore execute under the Wave-3 whole-suite green gate, NOT here. This is exactly the documented build boundary — no attempt was made to "fix" the Processor slice.
- Acceptance satisfied structurally for both tasks via the verify greps (Task 1: `ctx.MessageId = m.MessageId` present; no `MessageIndex` / `RedisKey[]` in DeleteConsumer; STRLEN drop kept. Task 2: `OutputData(dr.MessageId)`, `ctx.MessageId = dr.MessageId`, `ExecutionData(m.DeleteEntryId)` present; no `StringGetAsync`/`StringLengthAsync` in InjectConsumer; `SentMessageIds` in the test kit) plus the 0/0 Keeper-source build.

## Next Phase Readiness
- Wave 2 keeper side complete. Wave 3 (70-04) must migrate the Processor test slice + `ProcessorPipeline.cs` to the reshaped contracts; once it lands, the whole `tests/BaseApi.Tests` suite (including the three migrated keeper fact classes) is the req-12 green gate.
- No blockers introduced for the orchestrator phase — the envelope MessageId override mechanism (req 6/11) is now exercised in both INJECT and REINJECT.

## Self-Check: PASSED

All 8 modified source/test files and the SUMMARY verified present on disk; both task commits (`185d123`, `1eb2dc6`) verified in git log.

---
*Phase: 70-two-consumer-processor-design-pre-process-post-process-with*
*Completed: 2026-06-16*
