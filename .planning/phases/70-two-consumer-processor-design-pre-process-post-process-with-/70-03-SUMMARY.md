---
phase: 70-two-consumer-processor-design-pre-process-post-process-with
plan: 03
subsystem: BaseProcessor.Core / Processor.Sample (processor core)
tags: [processor, two-consumer, pre-process, post-process, seam, output-tail, keeper-escalation]
requires:
  - DataResult (Plan 01 — Messaging.Contracts)
  - KeeperInject/KeeperReinject/KeeperDelete reshaped (Plan 01)
  - L2ProjectionKeys.OutputData (Plan 01)
  - RetryLoop.ExecuteAsync (BaseConsole.Core)
provides:
  - "ProcessAsync seam returns Task<DataResult?> (one-or-null)"
  - "BaseProcessor.SpawnToPost (swallow-on-exhaust) + DeleteEntry (escalate DELETE) + NewResult factory"
  - "OutputTail — shared write-gated-on-completed + send-by-result tail (single source of truth)"
  - "PostProcessConsumer : IConsumer<DataResult> on queue:{id:D}-post"
  - "ProcessorPipeline linear Pre flow gating on L2[entryId]"
  - "SampleProcessor two-mode worked example"
affects:
  - src/BaseProcessor.Core/Processing/*
  - src/Processor.Sample/SampleProcessor.cs
  - src/Processor.BadConfig/BadConfigProcessor.cs
tech-stack:
  added: []
  patterns:
    - "per-send envelope MessageId override: ep.Send((object)msg, ctx => ctx.MessageId = carried, ct)"
    - "RetryLoop → keeper-state routing (gate/read→REINJECT, write→INJECT, delete→DELETE, send→throw)"
    - "swallow-vs-escalate asymmetry (SpawnToPost swallows; DeleteEntry escalates)"
    - "framework-populated per-dispatch seam state via SetSeamState"
key-files:
  created:
    - src/BaseProcessor.Core/Processing/OutputTail.cs
    - src/BaseProcessor.Core/Processing/PostProcessConsumer.cs
  modified:
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/BaseProcessor.Core/Processing/BaseProcessor`1.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - src/Processor.Sample/SampleProcessor.cs
    - src/Processor.BadConfig/BadConfigProcessor.cs
  deleted:
    - src/BaseProcessor.Core/Processing/ProcessItem.cs
    - src/BaseProcessor.Core/Processing/ProcessOutcome.cs
decisions:
  - "A1: Step* sends carry EntryId = Guid.Empty (orchestrator entryId↔messageId threading is SPEC-deferred)"
  - "Escalation hooks typed Func<Task>/Action<Guid> (no sync-over-async) wired by the pipeline as closures"
  - "Reused DispatchDeduped counter for SpawnToPost swallow telemetry (no new instrument)"
metrics:
  duration_minutes: 7
  tasks_completed: 2
  files_created: 2
  files_modified: 7
  files_deleted: 2
  completed: 2026-06-16
---

# Phase 70 Plan 03: Two-Consumer Processor Core Rewrite Summary

**One-liner:** Rewrote the processor core to the two-consumer (Pre/Post) design — the `ProcessAsync` seam now returns `Task<DataResult?>`, `BaseProcessor` gained `SpawnToPost`/`DeleteEntry`/`NewResult`, a shared `OutputTail` owns write-gated-on-completed + send-by-result, a new `PostProcessConsumer` binds on `queue:{id:D}-post`, `ProcessorPipeline` became the linear Pre flow gating on `L2[entryId]`, and `SampleProcessor` is the two-mode worked example; `ProcessItem`/`ProcessOutcome` deleted.

## What Was Built

### Task 1 (commit `ebbb525`) — seam + helpers + OutputTail + PostProcessConsumer
- **`BaseProcessor.cs`**: the internal `ExecuteAsync` seam returns `Task<DataResult?>` (D-03/D-05). Added protected `SpawnToPost(DataResult, Guid executionId)` (bounded `RetryLoop` then **SWALLOW** on send-exhaust — D-07, logs executionId only + counter, scheduler re-fires), protected `DeleteEntry()` (bounded `RetryLoop` then **escalate to DELETE keeper** on exhaust — D-08), and a protected `NewResult(StepOutcome, string)` factory that stamps the ambient ids + carried `MessageId`. Framework state (`_db`/`_sendProvider`/`_retryLimit`/`_entryId`/`_processorId`/ambient ids/escalation hooks) is populated via an `internal SetSeamState(...)` the pipeline calls before invoking the seam.
- **`BaseProcessor`1.cs`**: both `ExecuteAsync` and the author `ProcessAsync` changed `Task<List<ProcessItem>>` → `Task<DataResult?>`; the empty-payload→null-config guard and the deserialize-throws-`JsonException`→pipeline-catch contract kept verbatim.
- **`OutputTail.cs` (NEW, D-15)**: the single source of truth — output-validate (invalid Completed → force Failed) → write `L2[OutputData(messageId)]=data` **only when Completed** (write-exhaust → INJECT + return `false`) → send Step* by result (send-exhaust → throw). Returns a bool ("caller may run its own tail"). `SendResult`/`SendKeeper` copied verbatim from `ProcessorPipeline` (object-cast, throw-on-exhaust, `metrics.ResultSent` tag). Jittered `random[ttl,2×ttl]` TTL relocated here.
- **`PostProcessConsumer.cs` (NEW, D-15/D-16)**: thin `IConsumer<DataResult>` shell → `OutputTail.RunAsync(ctx.Message, Guid.Empty, ct)` (never touches `entryId`).

### Task 2 (commit `341c1fc`) — pipeline + sample + wiring + deletions
- **`ProcessorPipeline.cs` (D-14)**: replaced the whole slot-array/recovery model with the linear Pre flow — gate `exist L2[entryId]` (fault→REINJECT carrying `messageId`, clean-absent→return), read `L2[entryId]` (fault→REINJECT), input-validate (**invalid → exactly one `StepFailed`, NO entry delete** — C-2/req 2), `SetSeamState` then seam (null→return; `ProcessStatusException`/`JsonException`→one Step*, NO delete), shared `OutputTail` inline tail, then delete `L2[entryId]` (exhaust→DELETE). `BuildReinject` now carries `MessageId` (req 6); `BuildDelete` is delete-only (req 8). `RunRecoveryAsync`/`RunForwardAsync`/`SlotTtl`/`RetiredSlot`/`DeleteTerminalAsync`/`BuildCompleted`/`BuildInject` removed; `livenessOptions` ctor param removed (jitter moved to `OutputTail`).
- **`SampleProcessor.cs` (req 10)**: two-mode — empty `executionId` → 2 numbers (`base + random[0,99]`), log `"{label} had the following numbers: …"`, 2× `SpawnToPost(NewResult(Completed,data), Guid.NewGuid())`, `DeleteEntry()`, `return null`; non-empty → accumulate deterministically, log the same line, return one completed `DataResult with { ExecutionId = inbound }`.
- **`ProcessorStartupOrchestrator.cs` (D-16)**: sibling `ConnectReceiveEndpoint($"{id:D}-post", ConfigureConsumer<PostProcessConsumer>)` with `await postHandle.Ready` **before** `MarkHealthy()` (Pitfall 2 — bind-before-Healthy). Same posture (no `UseMessageRetry`, no error transport).
- **DI**: `AddConsumer<PostProcessConsumer>().ExcludeFromConfigureEndpoints()` + `AddScoped<OutputTail>()`.
- **Deletions**: `ProcessItem.cs` + `ProcessOutcome.cs` removed from disk (D-03/D-04).

## Verification

- `dotnet build src/Processor.Sample/Processor.Sample.csproj -c Release` → **0 warn / 0 err** (transitively builds `BaseProcessor.Core` + `Messaging.Contracts` + `BaseConsole.Core`).
- `dotnet build src/Processor.BadConfig/Processor.BadConfig.csproj -c Release` → **0 warn / 0 err**.
- All Task 1 + Task 2 acceptance greps pass (seam, helpers, `IConsumer<DataResult>`, `OutputData(dr.MessageId)`, `KeyExistsAsync(ExecutionData(d.EntryId))`, `outputTail.RunAsync`, `-post`, `SpawnToPost`/`DeleteEntry`/`return null`/the log line).
- No `ProcessItem`/`ProcessOutcome`/`MessageIndex`/`RunRecoveryAsync`/`RunForwardAsync` code remains in `src/BaseProcessor.Core` or `src/Processor.Sample` (only doc-comment mentions of the deleted methods in the pipeline's XML summary describing what was removed).

### Build boundary (expected, documented)
`dotnet build SK_P.sln -c Release` reports **11 `CS0246` errors, ALL originating SOLELY from `tests/BaseApi.Tests/Processor/*`** (old `ProcessItem` references in `BaseProcessorSeamFacts.cs`, `DispatchTestKit.cs`, `PipelineInFacts.cs`, `SampleProcessorFacts.cs`). **NONE originate from any `src/` file.** This is the documented Wave-3 / 70-04 boundary — the Processor test slice migration is 70-04's job; test files were NOT edited per the plan's explicit instruction.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 — Blocking] Migrated `Processor.BadConfig/BadConfigProcessor.cs` (not in the plan's file list)**
- **Found during:** Task 2 (grep for remaining `ProcessItem`/`ProcessOutcome` references per Pitfall 5).
- **Issue:** `BadConfigProcessor` (a production project that must compile) overrode `ProcessAsync` returning `Task<List<ProcessItem>>` and constructed `new(ProcessOutcome.Completed, …)`. Deleting `ProcessItem`/`ProcessOutcome` would break its compile, blocking the build-boundary success criterion.
- **Fix:** changed its dead-path seam to `Task<DataResult?>` returning `this.NewResult(StepOutcome.Completed, "processor-badconfig-ok")` (the path is unreachable — Gate A withholds the queue bind).
- **Files modified:** `src/Processor.BadConfig/BadConfigProcessor.cs`
- **Commit:** `341c1fc`

**2. [Rule 3 — Blocking] Removed the now-unread `livenessOptions` ctor parameter from `ProcessorPipeline`**
- **Found during:** Task 2 build (CS9113 "parameter unread" — analyzers treat it as an error).
- **Issue:** the jittered-TTL helper (`SlotTtl`) moved into `OutputTail`, so the pipeline no longer reads `IOptions<ProcessorLivenessOptions>`; the primary-ctor param became unread → build error.
- **Fix:** dropped the parameter from the `ProcessorPipeline` primary ctor.
- **Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
- **Commit:** `341c1fc`

**3. [Rule 1 — Bug] `<see cref>` to protected base helpers failed cross-assembly doc resolution**
- **Found during:** Task 2 build (CS1574 on `SpawnToPost`/`DeleteEntry`/`NewResult` crefs in `SampleProcessor` XML doc — treated as errors).
- **Fix:** changed those `<see cref="BaseProcessor.X"/>` doc references to plain `<c>X</c>` (the methods are protected base members; the cref added no value).
- **Files modified:** `src/Processor.Sample/SampleProcessor.cs`
- **Commit:** `341c1fc`

## Decisions / Open-Question Resolutions

- **A1 (Step*.EntryId):** carried `Guid.Empty` on all four Step* sends from `OutputTail` (output is keyed by `messageId` via `OutputData`; the orchestrator-side `entryId`↔`messageId` threading is SPEC-deferred). Consistent placeholder accepted this phase.
- **Escalation hooks:** typed `Func<Task>? _escalateDelete` and `Action<Guid>? _onSpawnDropped` on `BaseProcessor`, wired by the pipeline as closures over `SendKeeper`/`logger`/`metrics` — no sync-over-async (the plan's preferred form).
- **Swallow telemetry:** the `SpawnToPost` drop reuses the existing `ProcessorMetrics.DispatchDeduped` counter (no new instrument) plus a structured `LogWarning` that logs the executionId only (never the payload — T-70-10).
- **Inline/Post send envelope MessageId:** NOT overridden on the `OutputTail` Step* send (parity with the existing `SendResult`; the inbound envelope already carries the messageId on the Pre path, and Post re-emits to the orchestrator Result queue). The override applies only to `SpawnToPost` (req 11) and the keeper INJECT/REINJECT (Plan 02, already landed).

## Threat Surface

No new security-relevant surface beyond the plan's `<threat_model>`. The dispositions hold: input-invalid path no longer deletes `L2[entryId]` (T-70-07 accept), `-post` bound before MarkHealthy with bounded retries (T-70-09 mitigate), swallow logs executionId only (T-70-10 mitigate).

## Known Stubs

None. The two-mode `SampleProcessor` and the linear pipeline are fully wired (no placeholder data sources). The Step*.EntryId = Guid.Empty is a deliberate SPEC-deferred placeholder (A1), not an unwired stub — the orchestrator phase will redefine the keying.

## Self-Check: PASSED

- OutputTail.cs, PostProcessConsumer.cs, 70-03-SUMMARY.md all exist on disk.
- Commits ebbb525 (Task 1) + 341c1fc (Task 2) present in git log.
- ProcessItem.cs / ProcessOutcome.cs confirmed deleted from disk.
