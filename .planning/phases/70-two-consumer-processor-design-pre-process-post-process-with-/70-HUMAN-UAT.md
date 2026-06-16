---
status: partial
phase: 70-two-consumer-processor-design-pre-process-post-process-with-
source: [70-VERIFICATION.md]
started: 2026-06-17T00:00:00Z
updated: 2026-06-17T00:00:00Z
---

## Current Test

[awaiting human confirmation]

## Tests

### 1. Solution build + hermetic suite green (req-12 bar)
expected: `dotnet build SK_P.sln -c Release` 0-warn/0-err; `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait "Category=RealStack"` → 615 passed / 0 failed / 0 skipped
result: [pending] — reported GREEN by plan 70-04; recorded here as an explicitly human-confirmed checkpoint (the verifier inspected code, did not re-run the suite)

## Summary

total: 1
passed: 0
issues: 0
pending: 1
skipped: 0
blocked: 0

## Gaps

### CR-01 (critical, code-review quality finding — tracked, does not negate goal delivery)
Per-dispatch seam state (`_entryId`/`_messageId`/`_escalateDelete`/…) lives on a **Singleton** `BaseProcessor`, mutated per-consume via `ProcessorPipeline.SetSeamState(...)` with no `ConcurrentMessageLimit` and no memory barrier. Under concurrent consumes on one replica, consume A can read consume B's clobbered state → wrong-key `OutputData` write, cross-message data swap, or wrong/leaked `entryId` delete — the silent-loss class the SPEC pins against. The 615 hermetic facts drive the seam serially on freshly-`new`'d processors, so the suite cannot surface it.
- files: `BaseProcessor.cs`, `ProcessorPipeline.cs` (SetSeamState), `BaseProcessorServiceCollectionExtensions.cs` (Singleton contract), `ProcessorStartupOrchestrator.cs` (no ConcurrentMessageLimit on either endpoint)
- suggested fix: move seam state to a Scoped/AsyncLocal accessor (author API unchanged), OR switch the author registration to Scoped + set `ConcurrentMessageLimit` on both the entry and `-post` endpoints.
- status: open

### WR-01 / WR-02 / WR-03 (warnings — see 70-REVIEW.md)
- WR-01: `SpawnToPost` swallows every exception type on exhaust + resolves `GetSendEndpoint` outside the RetryLoop.
- WR-02: `PostProcessConsumer` trusts a fully wire-shaped `DataResult` (no `dr.ProcessorId == context.Id` provenance check) — new ingress; wants an explicit SECURITY note or check.
- WR-03: deserialize `JsonException` → `BuildFailed(d, ex.Message)` may put payload bytes on the `StepFailed` wire (never-log-payload discipline).
