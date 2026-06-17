---
status: resolved
phase: 70-two-consumer-processor-design-pre-process-post-process-with-
source: [70-VERIFICATION.md, 70-REVIEW-FIX.md]
started: 2026-06-17T00:00:00Z
updated: 2026-06-17T12:00:00Z
---

> Build/test checkpoint PASS and all 8 code-review findings fixed (`/gsd-code-review-fix 70 --all`). One advisory remains: a human sanity-check of CR-01's concurrent behavior is recommended (regression closed + tested, but concurrency is hard to fully prove via unit test).

## Current Test

[awaiting human confirmation]

## Tests

### 1. Solution build + hermetic suite green (req-12 bar)
expected: `dotnet build SK_P.sln -c Release` 0-warn/0-err; `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait "Category=RealStack"` → all hermetic tests pass
result: PASS — independently re-run 2026-06-17 after the code-review fixes: `dotnet build SK_P.sln -c Release` = 0 warn / 0 err; hermetic subset exit 0 (617 tests, 615 + 2 new CR-01/WR-02 regression facts). NOTE: a bare full-suite `dotnet test` shows 10 failures — these are the 17 RealStack/E2E tests (`*E2ETests.cs`, `RealStackNetZeroSweepFixture`) that require the live application consoles (orchestrator/processor/keeper) + host broker on `localhost:6380`/`sk-rabbitmq`, which are NOT running; they are excluded from this hermetic phase's gate by design (`Category=RealStack`) and are environmental, not Phase-70 regressions.

## Summary

total: 1
passed: 1
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

> **Update 2026-06-17 — all findings fixed via `/gsd-code-review-fix 70 --all`** (REVIEW-FIX.md, status `all_fixed`, 8/8 fixed, 0 skipped; 6 atomic `fix(70):` commits; build 0/0; hermetic suite green with 2 new regression facts). Statuses below reflect that pass.

### CR-01 (critical) — RESOLVED 2026-06-17
Fixed by moving the per-dispatch seam state off the Singleton `BaseProcessor` into a private `AsyncLocal<SeamState>` (registration stays Singleton; author API unchanged; `ProcessorPipeline` clears it per-consume in a `finally`). A new interleave regression fact fails against the old instance-field design and passes now. **Recommend a human sanity-check of true concurrent behavior** (the fixer flagged it requires human verification — concurrency is hard to fully prove via unit test), but the race is closed and tested.
Original finding (for reference):
Per-dispatch seam state (`_entryId`/`_messageId`/`_escalateDelete`/…) lives on a **Singleton** `BaseProcessor`, mutated per-consume via `ProcessorPipeline.SetSeamState(...)` with no `ConcurrentMessageLimit` and no memory barrier. Under concurrent consumes on one replica, consume A can read consume B's clobbered state → wrong-key `OutputData` write, cross-message data swap, or wrong/leaked `entryId` delete — the silent-loss class the SPEC pins against. The 615 hermetic facts drive the seam serially on freshly-`new`'d processors, so the suite cannot surface it.
- files: `BaseProcessor.cs`, `ProcessorPipeline.cs` (SetSeamState), `BaseProcessorServiceCollectionExtensions.cs` (Singleton contract), `ProcessorStartupOrchestrator.cs` (no ConcurrentMessageLimit on either endpoint)
- suggested fix: move seam state to a Scoped/AsyncLocal accessor (author API unchanged), OR switch the author registration to Scoped + set `ConcurrentMessageLimit` on both the entry and `-post` endpoints.
- status: open

### WR-01 / WR-02 / WR-03 (warnings) — RESOLVED 2026-06-17
- WR-01: `SpawnToPost` now resolves `GetSendEndpoint` inside the RetryLoop and narrows the swallow to a transient-fault allowlist (programming/serialization faults surface).
- WR-02: `PostProcessConsumer` now drops a `-post` `DataResult` whose `ProcessorId` isn't this processor's (new regression fact).
- WR-03: deserialize/unexpected branch now emits a sanitized `"input deserialization failed"` instead of `ex.Message`.

### IN-01..04 (info) — RESOLVED 2026-06-17
`OutputTail` send-endpoint resolved inside the RetryLoop; stale `RetryLoop` `_error`/`D-10` comment corrected; dedicated `processor_spawn_dropped` counter (no longer overloads `DispatchDeduped`); jittered-TTL policy hoisted to a shared `L2ProjectionKeys.OutputDataTtl`.
