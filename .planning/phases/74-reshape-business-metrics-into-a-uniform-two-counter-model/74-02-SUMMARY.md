---
phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
plan: 02
subsystem: observability
tags: [metrics, processor, prometheus, otel, refactor]
dependency_graph:
  requires:
    - "ProcessorMetrics meter 'BaseProcessor' (IMeterFactory DI pattern)"
    - "OrchestratorMetrics uniform-pair shape from Plan 74-01 (mirrored exactly)"
  provides:
    - "processor_messages_consumed counter (MessagesConsumed)"
    - "processor_messages_sent counter (MessagesSent), no outcome label"
  affects:
    - "Plan 74-04 (metric-test rewrite + absence asserts, D-14)"
    - "Live-stack analyzer PromCounterSnapshot/PassFailEngine (Plan 74-04 rebind)"
tech_stack:
  added: []
  patterns:
    - "Uniform two-counter business model {service}_messages_consumed/_sent"
    - "camelCase workflowId+processorId labels; messageId is the counting unit, never a label (D-04)"
    - "count-after-successful-send (D-02)"
key_files:
  created: []
  modified:
    - "src/BaseProcessor.Core/Observability/ProcessorMetrics.cs"
    - "src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs"
    - "src/BaseProcessor.Core/Processing/OutputTail.cs"
    - "src/BaseProcessor.Core/Processing/PostProcessConsumer.cs"
    - "tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs"
    - "tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs"
decisions:
  - "Rebound the 3rd (Post-hop) consume site PostProcessConsumer to MessagesConsumed — not in the plan's file list (Rule 3)"
  - "BreakerMetricsFacts minimally compile-unblocked; full absence-assert rewrite deferred to Plan 04 (D-14)"
metrics:
  duration: "~6 min"
  completed: 2026-06-18
---

# Phase 74 Plan 02: Processor Uniform Two-Counter Model Summary

Rewrote `ProcessorMetrics` to the uniform `processor_messages_consumed`/`processor_messages_sent` pair (camelCase `workflowId`+`processorId`, no `outcome` label), removed the dormant `DispatchDeduped`, deleted the now-unused `ResultOutcome` helper, and rebound all three processor consume/send increment sites — keeping a 0-warning Debug+Release build (REQ-2, REQ-6).

## What Was Built

**Task 1 — `ProcessorMetrics` rewrite (commit `e4e7944`)**
- Replaced `DispatchConsumed`/`ResultSent`/`DispatchDeduped` with `MessagesConsumed` (`processor_messages_consumed`) + `MessagesSent` (`processor_messages_sent`).
- Kept `SpawnDropped` (`processor_spawn_dropped`) and `MeterName = "BaseProcessor"` untouched (IN-03 — out of the removal list).
- Counters built via `IMeterFactory.Create(MeterName)` (never a static `Meter`); snake_case names with no `_total` (the collector appends it, D-03).
- `ProcessorMetricsFacts` updated to probe the new members; the `DispatchDeduped` assertion dropped, `SpawnDropped` assertion kept.

**Task 2 — increment-site rebind (commit `3a441fb`)**
- `EntryStepDispatchConsumer.Consume`: `MessagesConsumed.Add(1, workflowId=ctx.Message.WorkflowId, processorId=context.Id)` at the consume entry (D-03).
- `OutputTail.SendResult`: `MessagesSent.Add(1, workflowId=result.WorkflowId, processorId=context.Id)` AFTER the `if (!sent.Succeeded) throw` guard (count-after-success, D-02); `outcome` tag dropped; `ResultOutcome` helper deleted (unused private member would break the 0-warning gate).
- `PostProcessConsumer.Consume` (3rd consume site, see Deviations): rebound to `MessagesConsumed` with camelCase labels.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Rebound the Post-hop consume site (`PostProcessConsumer.cs`)**
- **Found during:** Task 2 (post-Task-1 whole-repo grep for old members)
- **Issue:** `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs:44` incremented `metrics.DispatchConsumed.Add(...)` — a third consume site not listed in the plan's `files_modified` or Task 2 action. Removing `DispatchConsumed` in Task 1 broke the `BaseProcessor.Core` compile.
- **Fix:** Rebound it to `MessagesConsumed.Add(1, workflowId=ctx.Message.WorkflowId, processorId=self)` — `DataResult` is `IExecutionCorrelated` so both ids are in-hand; `self == ctx.Message.ProcessorId` is already asserted by the file's provenance guard. Consistent with D-03 (the Post hop is a genuine consumed-message entry).
- **Files modified:** src/BaseProcessor.Core/Processing/PostProcessConsumer.cs
- **Commit:** 3a441fb

**2. [Rule 3 - Blocking] Minimal compile-unblock of `BreakerMetricsFacts.cs`**
- **Found during:** Task 2 (test-project build)
- **Issue:** `tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs` referenced the removed `procMetrics.DispatchDeduped` member (lines ~49/92/98/104), breaking the test-project compile. The plan's critical reminders state the full metric-test rewrite + absence asserts are owned by Plan 04 (D-14).
- **Fix (minimal):** `Processor_New_Counters_Construct_NonNull` now probes the surviving `MessagesConsumed`/`MessagesSent` (mirroring the orchestrator leg already migrated in 74-01). The cardinality-guard fact was repointed from the removed `DispatchDeduped` to the surviving `MessagesSent` and renamed `Recorded_Measurement_Carries_Expected_Label_Keys`, now asserting the NEW camelCase `workflowId`+`processorId` contract (the uniform model requires `workflowId`, inverting the old guard). The full absence-assert rewrite of this fixture remains Plan 04's responsibility.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs
- **Commit:** 3a441fb

### Out-of-scope (left untouched, owned by Plan 04 / D-14)
The PromQL string literals and DTO fields in `AnalyzerE2ETests.cs`, `MetricsRoundTripE2ETests.cs`, and `PromCounterSnapshot.cs` still reference the old `processor_dispatch_consumed`/`processor_result_sent`/`processor_dispatch_deduped` names and the `outcome` label. These are string literals / DTO fields (not removed-member references) so they compile cleanly; their rebind + absence asserts are explicitly Plan 04's scope (D-14). No edits made.

## Verification

- `grep` acceptance for both tasks: new names present, old members absent, `SpawnDropped`/`MeterName` intact, `ResultOutcome`/`outcome` gone, no `"ProcessorId"` tag in the rebound sites — all PASS.
- `dotnet build src/BaseProcessor.Core -c Debug -warnaserror` → **0 Warning, 0 Error**.
- `dotnet build src/BaseProcessor.Core -c Release -warnaserror` → **0 Warning, 0 Error**.
- `dotnet build tests/BaseApi.Tests -c Debug` → **0 Warning, 0 Error** (test project compiles).
- Hermetic run (`BaseApi.Tests.exe --filter-not-trait Category=RealStack`) of `ProcessorMetricsFacts` + `BreakerMetricsFacts` → **6/6 PASSED**.

## Known Stubs

None. Both counters are wired to real increment sites; no placeholder/empty-data paths introduced.

## Self-Check: PASSED
- FOUND: src/BaseProcessor.Core/Observability/ProcessorMetrics.cs (processor_messages_consumed/sent)
- FOUND: src/BaseProcessor.Core/Processing/OutputTail.cs (MessagesSent.Add, no outcome/ResultOutcome)
- FOUND: src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs (MessagesConsumed.Add)
- FOUND: src/BaseProcessor.Core/Processing/PostProcessConsumer.cs (MessagesConsumed.Add)
- FOUND commit e4e7944 (Task 1)
- FOUND commit 3a441fb (Task 2)
