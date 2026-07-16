---
phase: 77-consistent-framework-logging-model-scope-carried-execution-i
plan: 02
subsystem: observability
tags: [logging, masstransit, messageid, processor, framework-record, structured-logging]

# Dependency graph
requires:
  - phase: 77-01
    provides: "consume-side per-hop `hop executed {MessageId}` framework record (the symmetric counterpart)"
  - phase: 72
    provides: "OutputTail always-write + BuildStep EntryId=messageId (the send tail this record wraps)"
provides:
  - "OutputTail.SendResult mints, stamps (ctx => ctx.MessageId = outboundId), and RETURNS the outbound envelope MessageId"
  - "OutputTail.RunAsync emits the FW-04-guarded framework record `result sent {MessageId} {Outcome}` (Tier-2 id + Tier-3 outcome, no payload)"
  - "OutputTail gains a trailing OPTIONAL ILogger<OutputTail> ctor param (NullLogger default) — all existing constructions compile unchanged; DI fills the real logger"
  - "ResultSendLogFacts — hermetic proof that the logged MessageId equals the stamped outbound envelope id"
affects: [phase-78-sweep-rework, live-analyzer-produced-edges, orchestrator-result-queue]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Send side self-mints its delivery-unit id ONCE outside the RetryLoop (stable across attempts) and returns it for the framework record"
    - "Optional trailing ILogger<T>?=null ctor param (NullLogger default) to add logging without breaking direct-construction test sites"

key-files:
  created:
    - tests/BaseApi.Tests/Processor/ResultSendLogFacts.cs
  modified:
    - src/BaseProcessor.Core/Processing/OutputTail.cs

key-decisions:
  - "Mint the outbound MessageId once outside the RetryLoop so every retry attempt stamps the SAME id (stable across redelivery) and the logged id is deterministic"
  - "Add the logger as a trailing OPTIONAL param defaulting to NullLogger — keeps all 8 existing `new OutputTail(...)` test sites compiling; production DI (AddScoped<OutputTail>) fills the real ILogger"
  - "Do NOT change the RunAsync tuple shape (would ripple into ProcessorPipeline/PostProcessConsumer owned elsewhere) — SendResult's return type carries the id instead"
  - "Self-minting the outbound id is safe (T-77-06 accept): the Result endpoint has no inbox/dedup, parity with the keeper reinject send; a Guid.NewGuid() is as unique as the MassTransit-minted id"

patterns-established:
  - "Framework send record: `result sent {MessageId} {Outcome}` — ids/outcome only, FW-04 try/catch guard, never dr.Data/payload (FW-03)"

requirements-completed: [LOG-03]

# Metrics
duration: 35min
completed: 2026-07-16
---

# Phase 77 Plan 02: Capture and Log the Outbound Result-Send MessageId Summary

**OutputTail.SendResult now self-mints, stamps (ctx => ctx.MessageId), and returns the outbound envelope MessageId, and RunAsync emits an FW-04-guarded `result sent {MessageId} {Outcome}` framework record — the send-side delivery-unit id, symmetric with the consume-side `hop executed {MessageId}`.**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-07-16T16:15:48Z
- **Completed:** 2026-07-16T16:50:43Z
- **Tasks:** 2
- **Files modified:** 2 (1 modified, 1 created)

## Accomplishments
- `OutputTail.SendResult` mints the outbound envelope id ONCE outside the RetryLoop (stable across retry attempts), stamps it on the Step* envelope via the `ctx => ctx.MessageId = outboundId` override idiom (BaseProcessor.cs:102), and returns it (`Task<Guid>`).
- `OutputTail.RunAsync` emits exactly one Information record `result sent {MessageId} {Outcome}` under an FW-04 try/catch — ids/outcome only, never payload (FW-03).
- Added a trailing OPTIONAL `ILogger<OutputTail>? logger = null` ctor param (→ `NullLogger` default) so all eight direct `new OutputTail(...)` test sites compile unchanged; production DI fills the real logger.
- New hermetic fact `ResultSendLogFacts` proves the logged `MessageId` equals the single id stamped on the outbound envelope (captured via `CapturingSendProvider.SentMessageIds`), carries `Outcome == "Completed"`, and leaks no payload.

## Task Commits

Each task was committed atomically:

1. **Task 1: Mint, stamp, and return the outbound MessageId in SendResult; log it in RunAsync** - `e4ba610` (feat)
2. **Task 2: Hermetic fact — logged MessageId equals the stamped outbound envelope id** - `18ca4b7` (test)

_Note: Both tasks were `tdd="true"`; Task 1 is the implementation change (verified by build gates) and Task 2 is the proving fact._

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/OutputTail.cs` - SendResult mints/stamps/returns the outbound id; RunAsync logs `result sent {MessageId} {Outcome}` under an FW-04 guard; optional ILogger ctor param added.
- `tests/BaseApi.Tests/Processor/ResultSendLogFacts.cs` - Hermetic fact asserting the logged MessageId == the stamped outbound envelope id, Outcome == "Completed", and no payload key.

## Decisions Made
- **Mint once outside the RetryLoop:** guarantees a stable outbound id across retry attempts and a deterministic logged id.
- **Optional trailing logger param:** the least-invasive way to add logging without touching the 8 direct-construction test sites or the RunAsync tuple shape (which would ripple into ProcessorPipeline/PostProcessConsumer).
- **Self-minted id accepted (T-77-06):** the Result endpoint has no dedup, so a `Guid.NewGuid()` per send is safe and unique (parity with keeper reinject).

## Deviations from Plan

None - plan executed exactly as written.

_One minor in-task adjustment (not a plan deviation): the acceptance-criteria grep expects the `result sent {MessageId} {Outcome}` template to appear exactly once, so the added doc-comment was reworded to avoid duplicating the literal template string. Additionally, the xUnit2031 analyzer required `Assert.Single(collection, predicate)` instead of `Assert.Single(collection.Where(...))` — a mechanical form change with identical semantics._

## Issues Encountered
- The MTP test runner rejects the `--` argument separator when invoking `BaseApi.Tests.exe` directly (prints help, exit 5); the correct form is `BaseApi.Tests.exe --filter-method "*ResultSend*"` (no `--`), which runs and exits 0.

## Verification

- **Debug + Release solution build: 0 Warning(s), 0 Error(s)** — the optional-param default keeps all 8 `new OutputTail(...)` test sites compiling.
- `ResultSendLogFacts` (`*ResultSend*`): **1/1 PASS** (exit 0).
- `OutputTailFacts` (`*OutputTail*`): **6/6 PASS** (exit 0) — existing facts unaffected.
- `PerHopLogFacts` (`*PerHop*`): **11/11 PASS** — the symmetric consume-side record unaffected.
- Acceptance greps: `result sent {MessageId} {Outcome}` ×1, `ctx => ctx.MessageId = outboundId` ×1, `Task<Guid> SendResult` ×1, `ILogger<OutputTail>? logger = null` ×1, payload-in-log ×0.
- Full hermetic filter (`--filter-not-trait Category=RealStack`) shows **272 pre-existing failures — ALL environmental** (RabbitMQ/Postgres connection failures in the Docker-less sandbox: Integration/Persistence/Swagger/live-Console/live-Processor tests). Identical baseline to phases 75-01..05; **zero** are OutputTail/logging assertion failures, structurally impossible to be caused by a send-side logging change (they fail at infrastructure connection before any OutputTail code runs).

## Phase-78 Handoff
This adds a NEW framework send record (`result sent {MessageId} {Outcome}`). The live analyzer may now key produced-edges on the result-send `{MessageId}`; wiring that into the sweep is Phase 78.

## Next Phase Readiness
- Send-side delivery-unit id is now logged, symmetric with the consume side — the three-tier framework logging model's `{MessageId}` is present on both send and consume records.
- No blockers. Live surfacing of the new record to ES `attributes.*` (via the OTLP bridge) is exercised by the deferred-automated live sweep (Docker-less sandbox), consistent with the phase-68/73/74/75 precedent.

## Self-Check: PASSED

- FOUND: src/BaseProcessor.Core/Processing/OutputTail.cs
- FOUND: tests/BaseApi.Tests/Processor/ResultSendLogFacts.cs
- FOUND: .planning/phases/77-.../77-02-SUMMARY.md
- FOUND commit: e4ba610 (Task 1)
- FOUND commit: 18ca4b7 (Task 2)

---
*Phase: 77-consistent-framework-logging-model-scope-carried-execution-i*
*Completed: 2026-07-16*
