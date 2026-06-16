---
phase: 70-two-consumer-processor-design-pre-process-post-process-with
plan: 04
subsystem: tests (processor + keeper hermetic facts)
tags: [processor, two-consumer, pre-process, post-process, output-tail, hermetic-tests, d-17, req-12]
requires:
  - "70-03 (BaseProcessor.Core/Sample core rewrite — the seam/OutputTail/PostProcessConsumer/pipeline under test)"
  - "70-02 (reshaped keeper consumers + migrated keeper facts that could not run until the suite compiled)"
  - "70-01 (DataResult contract; KeeperInject embeds DataResult; KeeperDelete delete-only; MessageIndex retired)"
provides:
  - "Migrated processor test slice to the two-consumer model — whole hermetic suite compiles + passes (req 12 gate)"
  - "PrePipelineFacts / OutputTailFacts / PostProcessConsumerFacts / BaseProcessorSeamFacts / SampleProcessorFacts / extended DispatchBindSequenceFacts"
  - "DispatchTestKit re-shaped to the DataResult? seam (FakeProcessor return/throw/Mode-2 + Result factory + SentMessageIds + new fault muxes)"
  - "Test doubles capture the REAL virtual ISendEndpoint.Send(.., IPipe<SendContext>, ..) (object + typed) — the prior Action<SendContext> stub leaked matchers"
affects: [orchestrator-phase]
tech-stack:
  added: []
  patterns:
    - "NSubstitute capture of the real ISendEndpoint.Send(object/T, IPipe<SendContext>, ct) — materialize the pipe against a substituted SendContext to record the carried envelope MessageId (the Action<SendContext> form is an extension method that leaks Arg matchers)"
    - "Overload-agnostic StringSetAsync received-call inspection via ReceivedCalls() by method name (SE.Redis 2.13 binds the 3-arg write to the Expiration/ValueCondition or keepTtl real overload)"
    - "Drive the BaseProcessor seam helpers (SpawnToPost/DeleteEntry/NewResult) via the internal SetSeamState + InternalsVisibleTo, with closure hooks the test inspects"
key-files:
  created:
    - tests/BaseApi.Tests/Processor/PrePipelineFacts.cs
    - tests/BaseApi.Tests/Processor/OutputTailFacts.cs
    - tests/BaseApi.Tests/Processor/PostProcessConsumerFacts.cs
  modified:
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    - tests/BaseApi.Tests/Processor/DispatchBindSequenceFacts.cs
    - tests/BaseApi.Tests/Processor/EntryStepDispatchConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs
    - tests/BaseApi.Tests/Contracts/KeeperContractTests.cs
    - tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
    - tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs
    - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
  deleted:
    - tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs
    - tests/BaseApi.Tests/Processor/PipelinePostFacts.cs
    - tests/BaseApi.Tests/Processor/PipelinePreFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineInFacts.cs
    - tests/BaseApi.Tests/Processor/PipelineEndDeleteFacts.cs
decisions:
  - "The real virtual ISendEndpoint.Send overload is the IPipe<SendContext> form; the Action<SendContext> envelope-override form is an extension method NSubstitute cannot intercept (stubbing it leaks Arg matchers). Both DispatchTestKit AND RecoveryTestKit (70-02) carried the extension stub and were fixed."
  - "SC2 recovery E2E migrated to the two-consumer model (INJECT→DataResult/OutputData, DELETE→single-key) and the retired organic slot-recovery [Fact] + its exclusive helpers removed — the linear Pre flow has no recovery branch."
  - "KeeperContractTests / L2ProjectionKeysTests / ModelBContractsRetiredFacts stale-API assertions updated to the 70-01 reshape (KeeperInject embeds DataResult; MessageIndex retired; OutputData added) — required for the whole suite to compile + pass (req 12), beyond the plan's enumerated Processor/* file list."
metrics:
  duration_minutes: 63
  tasks_completed: 2
  files_created: 3
  files_modified: 10
  files_deleted: 6
  completed: 2026-06-16
---

# Phase 70 Plan 04: Migrate the Processor Test Slice to the Two-Consumer Model Summary

**One-liner:** Migrated the Phase-50–68 processor test slice to the two-consumer (Pre/Post) design (D-17) — extended `DispatchTestKit` to the `DataResult?` seam, deleted the no-analog slot/recovery facts, rewrote the linear Pre-flow facts, and added `OutputTail`/`PostProcessConsumer`/seam/sample/-post-bind facts — so the **whole hermetic `tests/BaseApi.Tests` suite compiles and passes (req-12 gate): 615 passed / 0 failed / 0 skipped**, with `dotnet build SK_P.sln` 0-warning Release + Debug.

## What Was Built

### Task 1 (commit `4be0946`) — DispatchTestKit re-shape + no-analog deletions
- **`FakeProcessor`** now overrides `Task<DataResult?> ProcessAsync` with three ctors: return a `DataResult?` (or null), throw (the `ProcessStatusException` family + unexpected), or a Mode-2 delegate that calls `this.SpawnToPost`/`this.DeleteEntry` and returns null. Added a `Result(outcome, data, messageId?)` factory and public wrappers (`SpawnToPostAsync`/`DeleteEntryAsync`/`NewResultPublic`) over the protected helpers. Removed the `Items(...)`/`List<ProcessItem>` ctors.
- **`CapturingSendProvider`** records `IStepResult`→`Sent`, `IKeeperRecoverable`→`SentKeeper`, `DataResult`→`SentData`/`SentDataToUri`, and the envelope `MessageId` set by the override overload into `SentMessageIds`.
- **New fault muxes** for the linear Pre / OutputTail surfaces: `GateFaultL2` (existence-check throws → REINJECT), `CleanAbsentL2` (KeyExists false → no processing), `OutputWriteFaultL2` (output write throws → INJECT); retargeted `ReadWriteDeleteOkL2`/`ReadFaultL2`/`AbsentReadL2`/`ReadOkDeleteFaultL2` to the gate-open single-key model.
- **Deleted** `PipelineRecoveryFacts` + the slot/recovery `PipelineForwardFacts`/`PipelinePostFacts`, plus `PipelinePreFacts`/`PipelineInFacts`/`PipelineEndDeleteFacts` (re-homed into the new files in Task 2). Fixed `EntryStepDispatchConsumerFacts` to the new ctor (+ `OutputTail`).

### Task 2 (commit `858b251`) — new facts + whole-suite green
- **`PrePipelineFacts` (NEW)** — the linear Pre flow: req 1 gate-fault→REINJECT & clean-absent→no-op; req 2 read-fault→REINJECT & **input-invalid→1 StepFailed + `DidNotReceive().KeyDeleteAsync` (C-2)**; req 3 seam-null→no write/send/delete; req 4 completed→OutputData write+StepCompleted+entry delete, delete-exhaust→DELETE, write-exhaust→INJECT (no StepCompleted, no delete); seam-throw parity (Failed/Cancelled/Processing/unexpected/malformed-deser → 1 Step*, no entry delete).
- **`OutputTailFacts` (NEW)** — completed write keyed by messageId with a non-null jittered TTL + StepCompleted; Failed no-write + StepFailed; write-exhaust→INJECT (carrying the DataResult + DeleteEntryId) + `RunAsync` returns false.
- **`PostProcessConsumerFacts` (NEW, req 5)** — completed→OutputData write (`data == dr.Data`) + StepCompleted AND `DidNotReceive().StringGetAsync`/`KeyDeleteAsync` (no entry touch); write-exhaust→INJECT.
- **`BaseProcessorSeamFacts` (rewrite, req 9)** — SpawnToPost swallows on send-exhaust (drop hook fires, no throw, no keeper) + overrides the envelope MessageId on success; DeleteEntry escalates the DELETE hook on delete-exhaust (no throw).
- **`SampleProcessorFacts` (rewrite, req 10)** — entry (`executionId == Guid.Empty`)→exactly 2 spawns with DISTINCT execIds to `{id:D}-post` + entry deleted + seam returns null + no inline Step*; downstream→1 completed reusing the inbound exec (no spawn/delete); both log `"… had the following numbers: …"`; null-config still spawns 2.
- **`DispatchBindSequenceFacts` (extend, req 11)** — the orchestrator now binds BOTH `{id:D}` and `{id:D}-post` before MarkHealthy → ordered log `[connect, ready, connect, ready, markhealthy]`; both queue names asserted (bare id + `-post`, no `queue:` prefix).

## Verification

- **`dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait "Category=RealStack"` → `total: 615, failed: 0, skipped: 0` (EXIT 0)** — the req-12 whole-hermetic-suite gate. (The 8 `Observability` Phase-11 facts poll live ES/Prometheus + an in-network broker; they pass once the `elasticsearch`/`otel-collector`/`prometheus` infra is up — unrelated to Phase 70.)
- **`dotnet build SK_P.sln -c Release` → Build succeeded, 0 Warning(s), 0 Error(s).**
- **`dotnet build SK_P.sln -c Debug` → Build succeeded, 0 Warning(s), 0 Error(s).**
- Scoped class runs all green: PrePipelineFacts 13/13, OutputTailFacts 3/3, PostProcessConsumerFacts 2/2, BaseProcessorSeamFacts 4/4, SampleProcessorFacts 4/4; migrated keeper facts InjectConsumerFacts/ReinjectConsumerFacts/DeleteConsumerFacts/RecoveryDeadLetterFacts/KeeperContractTests all green.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 — Bug] Test doubles stubbed the wrong (extension) Send overload — the envelope-override capture never fired and leaked NSubstitute matchers**
- **Found during:** Task 2 (first execution of the new Processor + the staged 70-02 keeper facts — the suite had never compiled before, so the bug had never run).
- **Issue:** `CapturingSendProvider` (in BOTH `DispatchTestKit` and `RecoveryTestKit`) stubbed `Send(object, Action<SendContext>, ct)`, which is an EXTENSION method on `ISendEndpoint` (the real virtual method takes `IPipe<SendContext>`). NSubstitute cannot intercept extension methods → `RedundantArgumentMatcherException` poisoned every fact that touched the provider, and the envelope-MessageId capture (req 6/11) never recorded.
- **Fix:** intercept the real virtual `ISendEndpoint.Send(object, IPipe<SendContext>, ct)` (and the typed `Send<EntryStepDispatch>(.., IPipe<SendContext<EntryStepDispatch>>, ..)` for REINJECT), materializing the pipe against a substituted `SendContext` to apply + read the carried `MessageId`.
- **Files modified:** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs`, `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs`, `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs`
- **Commit:** `858b251`

**2. [Rule 1 — Bug] OutputData-write-fault mux threw on the wrong StringSetAsync overload → INJECT never escalated**
- **Found during:** Task 2 (the write-exhaust→INJECT facts).
- **Issue:** SE.Redis 2.13 binds the production 3-arg `StringSetAsync(key, value, ttl)` to the `Expiration/ValueCondition` overload; the fault mux only threw on the keepTtl 6-arg form, so the write looked like it succeeded and no INJECT fired.
- **Fix:** the fault mux now throws on BOTH real virtual overloads (Expiration/ValueCondition + keepTtl 6-arg, T-70-11 Pitfall 1); received-write assertions inspect `ReceivedCalls()` by method name (overload-agnostic).
- **Files modified:** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs`
- **Commit:** `858b251`

**3. [Rule 3 — Blocking] Stale-API references outside `Processor/*` blocked the whole-suite compile/pass (req 12)**
- **Found during:** Task 2 (the req-12 build/run gate surfaces all assemblies, not just the Processor slice).
- **Issue:** `KeeperContractTests` (asserted the retired `KeeperInject.EntryId/Data`), `L2ProjectionKeysTests` + `ModelBContractsRetiredFacts` (referenced the retired `L2ProjectionKeys.MessageIndex`), and `SC2RecoveryPathsE2ETests` (`KeeperInject.EntryId/Data`, `KeeperDelete.MessageId`, `MessageIndex`, and the retired organic slot-recovery `[Fact]`) all referenced APIs that 70-01/70-02 deleted. They were not in the plan's enumerated Processor file list but are required for the whole suite (req 12).
- **Fix:** updated `KeeperContractTests` to the reshaped `KeeperInject` (DataResult + DeleteEntryId, no EntryId/Data); `L2ProjectionKeysTests` to pin `OutputData(messageId)`; `ModelBContractsRetiredFacts` to assert `MessageIndex` ABSENT + `OutputData`/`ExecutionData` survive; `SC2RecoveryPathsE2ETests` STATE 3 (INJECT) to the DataResult/OutputData model and STATE 4 (DELETE) to single-key, and removed the retired organic slot-recovery `[Fact]` + its exclusive helpers (no analog in the linear Pre flow). No assertions weakened — each is re-pinned to the new model.
- **Files modified:** `tests/BaseApi.Tests/Contracts/KeeperContractTests.cs`, `tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs`, `tests/BaseApi.Tests/Resilience/ModelBContractsRetiredFacts.cs`, `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs`
- **Commit:** `858b251`

## Threat Surface

No new security-relevant surface. The plan's `<threat_model>` dispositions hold: the OutputData-write-fault mux throws across overloads so a false-green INJECT cannot pass (T-70-11 mitigated); the req-12 gate uses the whole-suite `--filter-not-trait Category=RealStack` (T-70-12 accepted — `--filter` is unreliable on MTP).

## Known Stubs

None. Every new fact drives the real production type (the linear `ProcessorPipeline`, the shared `OutputTail`, `PostProcessConsumer`, `SampleProcessor`, and the `BaseProcessor` seam via `SetSeamState`) against hermetic NSubstitute/FakeRedis doubles — no placeholder data sources.

## Self-Check: PASSED

- PrePipelineFacts.cs, OutputTailFacts.cs, PostProcessConsumerFacts.cs all exist on disk.
- Commits 4be0946 (Task 1) + 858b251 (Task 2) present in git log.
- PipelineRecoveryFacts.cs / PipelineForwardFacts.cs / PipelinePostFacts.cs / PipelinePreFacts.cs / PipelineInFacts.cs / PipelineEndDeleteFacts.cs confirmed deleted from disk.
