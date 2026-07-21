---
phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi
reviewed: 2026-06-17T00:00:00Z
depth: standard
files_reviewed: 35
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/OutputTail.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/InjectConsumer.cs
  - src/Keeper/Recovery/OrchestratorDeleteConsumer.cs
  - src/Keeper/Recovery/OrchestratorInjectConsumer.cs
  - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
  - src/Keeper/Recovery/RecoveryEndpointBinder.cs
  - src/Messaging.Contracts/NextStepHandoff.cs
  - src/Messaging.Contracts/OrchestratorDelete.cs
  - src/Messaging.Contracts/OrchestratorInject.cs
  - src/Messaging.Contracts/OrchestratorQueues.cs
  - src/Messaging.Contracts/OrchestratorReinject.cs
  - src/Orchestrator/Configuration/OrchestratorOutputOptions.cs
  - src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs
  - src/Orchestrator/Consumers/OrchestratorPostProcessConsumerDefinition.cs
  - src/Orchestrator/Consumers/StepCancelledConsumer.cs
  - src/Orchestrator/Consumers/StepCompletedConsumer.cs
  - src/Orchestrator/Consumers/StepFailedConsumer.cs
  - src/Orchestrator/Consumers/StepProcessingConsumer.cs
  - src/Orchestrator/Consumers/TypedResultConsumer.cs
  - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
  - src/Orchestrator/Dispatch/RelocateTail.cs
  - src/Orchestrator/Program.cs
  - tests/BaseApi.Tests/Contracts/OrchestratorContractTests.cs
  - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/OrchestratorDeleteConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs
  - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
  - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
  - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
  - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
  - tests/BaseApi.Tests/Processor/OutputTailFacts.cs
findings:
  critical: 0
  warning: 0
  info: 3
  total: 3
status: issues_found
---

# Phase 71: Code Review Report

**Reviewed:** 2026-06-17T00:00:00Z
**Depth:** standard
**Files Reviewed:** 35
**Status:** issues_found (3 Info only)

## Summary

Phase 71 mirrors the Phase-70 processor two-consumer recovery pattern onto the orchestrator side: a Pre pipeline (`OrchestratorPrePipeline`) that reads/deletes `L2[out:entryId]` and fans out `NextStepHandoff` messages, a Post tail (`RelocateTail`) that writes `L2[data:messageId]` and dispatches, and three keeper recovery consumers (`OrchestratorReinject/Inject/Delete`).

All four named invariants hold:

- **Send-before-delete ordering** — correct in both the orchestrator Pre pipeline (fan-out completes before the `out:` delete; a send-exhaust throws *before* the delete is reached, so a throw never leaves the source deleted — `OrchestratorPrePipeline.cs:107-119`) and the keeper INJECT (write → dispatch → delete in strict order, asserted by `Received.InOrder` — `OrchestratorInjectConsumer.cs:43-60`).
- **Cross-assembly firewall** — intact. `Keeper.csproj` ProjectReferences only `BaseConsole.Core` + `Messaging.Contracts`, never `Orchestrator`. The relocate body is re-implemented inline in `OrchestratorInjectConsumer` rather than calling `RelocateTail`; only the shared `L2ProjectionKeys` policy is reused.
- **executionId threading without regeneration** — correct end to end. `NextStepHandoff.ExecutionId` is carried unchanged onto each handoff (`OrchestratorPrePipeline.cs:105`), threaded byte-unchanged into the dispatch (`RelocateTail.cs:68-69`), and rebuilt unchanged in every keeper consumer arm. No `NewId.NextGuid`/regeneration anywhere on these paths.
- **EntryId == messageId (A1) on Completed only** — correct in all three sites: `OutputTail.BuildStep` (`OutputTail.cs:81-88`), keeper `InjectConsumer` (`InjectConsumer.cs:45-52`), and `OrchestratorReinjectConsumer` (`OrchestratorReinjectConsumer.cs:52-59`). The Completed arm stamps `EntryId = messageId` (the real `out:` key); Failed/Cancelled/Processing keep `Guid.Empty`.

Namespace cross-pairing (orchestrator reads/deletes `out:`, writes `data:`; processor writes `out:`, reads/writes `data:`) is consistent across all consumers and matches the contract docs. DI registration scopes are correct (`OrchestratorPrePipeline`/`RelocateTail` are `AddScoped` so each `Send` uses the consume-scoped endpoint provider — `Program.cs:90-91`). Test coverage is thorough and the assertions pin the right invariants.

No Critical or Warning issues. Three Info-level robustness/clarity observations follow.

## Info

### IN-01: Null envelope `MessageId` silently degrades the relocate key to `Guid.Empty`

**File:** `src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs:27`
**Issue:** `relocateTail.RunAsync(ctx.Message, ctx.MessageId ?? Guid.Empty, ...)` coalesces a null envelope `MessageId` to `Guid.Empty`. On a Completed continuation (`h.Data` non-empty) this writes `L2[data:00000000-...]` and dispatches with `entryId = Guid.Empty`, so the next processor would read an all-zero `data:` key instead of the real blob — a silent relocation corruption rather than a fail-fast. In practice MassTransit always assigns a `MessageId` on the Pre fan-out send, so this is defensive-only, but the coalesce masks the only condition under which it could go wrong.
**Fix:** Consider failing loudly when `MessageId` is null on a Completed handoff, e.g. throw to force broker redelivery rather than relocating under an empty key:
```csharp
public async Task Consume(ConsumeContext<NextStepHandoff> ctx)
{
    if (ctx.MessageId is null && !string.IsNullOrEmpty(ctx.Message.Data))
        throw new InvalidOperationException("Completed handoff arrived without an envelope MessageId; cannot key the data: relocation.");
    await relocateTail.RunAsync(ctx.Message, ctx.MessageId ?? Guid.Empty, ctx.CancellationToken);
}
```
Same defensive note applies to `TypedResultConsumer.Consume` (`TypedResultConsumer.cs:58`), where a null `MessageId` only weakens a REINJECT's id re-assertion (lower impact, no data write).

### IN-02: `OrchestratorOutputOptions` binds the whole `"Orchestrator"` section, which is also probed ad hoc for `InstanceId`

**File:** `src/Orchestrator/Program.cs:94` (with `Program.cs:30`)
**Issue:** `Configure<OrchestratorOutputOptions>(GetSection("Orchestrator"))` binds the same `"Orchestrator"` config section that line 30 reads directly via `Configuration["Orchestrator:InstanceId"]`. This is functionally correct today (extra keys like `InstanceId` are ignored by the options binder, and `OutputDataTtlSeconds` defaults to 300 when absent — `appsettings.json` defines no `"Orchestrator"` section), but two different ownership models over one section is a latent maintenance trap: a future reader may assume `OrchestratorOutputOptions` is the sole owner of `"Orchestrator"`.
**Fix:** Either document on `OrchestratorOutputOptions` that the `"Orchestrator"` section is shared (InstanceId lives there too), or give the TTL its own subsection (e.g. `"Orchestrator:Output"`) so each section has a single owner. Low priority — no current defect.

### IN-03: REINJECT default `ErrorMessage` text differs from INJECT/OutputTail for the same Failed path

**File:** `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:146`
**Issue:** `BuildReinject` sets `ErrorMessage = (m as StepFailed)?.ErrorMessage ?? ""`, so a Failed result re-injected through REINJECT carries the *inbound* error text (or `""`). The direct/INJECT Failed arms instead use the fixed literal `"output failed schema validation"` (`OutputTail.cs:84`, `InjectConsumer.cs:48`). These are different code paths (a REINJECT faithfully replays the original result, whereas OutputTail's literal is a forced-Failed from output-validation), so the divergence is arguably intentional — but it means a downstream consumer cannot rely on a single canonical Failed `ErrorMessage` string. Worth a one-line comment confirming the intent so it isn't "fixed" into inconsistency later.
**Fix:** Add a brief comment on `BuildReinject` noting that REINJECT replays the inbound diagnostic verbatim (by design), distinct from OutputTail's output-validation literal. No code change required.

---

_Reviewed: 2026-06-17T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
