# Phase 70: Two-consumer processor design (Pre-Process + Post-Process) with keeper recovery — Research

**Researched:** 2026-06-16
**Domain:** .NET 8 / C# backend — MassTransit 8.5.5 messaging + StackExchange.Redis 2.13.1 (L2) + RabbitMQ; in-process runtime-bound receive endpoints; hermetic xUnit.v3 unit tests
**Confidence:** HIGH (every claim below is grounded in a read of the current code with file:line; the few genuinely-new mechanisms — envelope MessageId override, the `-post` endpoint — are flagged explicitly as net-new with the exact insertion point)

## Summary

Phase 70 is a **replace-in-place rewrite** of the single-consumer slot-array `ProcessorPipeline` into a pinned two-consumer (Pre-Process + Post-Process) design. Every collaborator the new design needs already exists and is proven by ~30 hermetic facts: the runtime-bind endpoint pattern (`ConnectReceiveEndpoint` + `ExcludeFromConfigureEndpoints`), the `RetryLoop`→keeper-escalation discipline, the `ISendEndpointProvider` send pattern, the `RecoveryConsumerBase.Guard` keeper wrapper, the 4 `Step*` result contracts, the `StepOutcome` 4-value enum, and the `DispatchTestKit`/`FakeRedis`/`CapturingSendProvider` doubles. The work is mostly **re-wiring existing primitives into the new control flow** plus **two net-new pieces**: (1) a `DataResult` contract that is simultaneously the `ProcessAsync` return type, the Post wire message, and the `KeeperInject` body; (2) a second runtime-bound receive endpoint `{processorId:D}-post` for `IConsumer<DataResult>`.

**Two genuinely-new mechanisms not present anywhere in the current code** (both required by the SPEC, neither hand-rollable from an existing call site):
1. **Outbound envelope `MessageId` override** — the SPEC requires spawn sends and keeper re-injections to override the MassTransit envelope `MessageId` with the carried `messageId` (SPEC constraint, req 6/7/11). **No current send does this.** The mechanism is the `ISendEndpoint.Send(msg, IPipe<SendContext>)` / `ctx => ctx.MessageId = carried` overload (analogous to the existing `OutboundCorrelationSendFilter` which sets `context.CorrelationId` on `SendContext`). The planner must introduce it; there is no copy-paste source.
2. **The `-post` receive endpoint** — a second `ConnectReceiveEndpoint` call in `ProcessorStartupOrchestrator`, sibling to the existing `{Id:D}` bind, attaching the new `PostProcessConsumer`.

**Primary recommendation:** Rewrite `ProcessorPipeline.RunAsync` to the linear Pre flow (gate `L2[entryId]` → read → input-validate → deserialize → `ProcessAsync` → null-return early-exit → `OutputTail`); delete `RunRecoveryAsync`/`RunForwardAsync`/`SlotTtl`-on-MessageIndex/all slot code; factor the output tail into a shared `OutputTail` helper that `PostProcessConsumer` reuses; reshape the 3 keeper contracts to carry `DataResult`; add `L2ProjectionKeys.OutputData`; bind a `-post` endpoint; rewrite the test slice. **Build + `dotnet test` only — no container build this phase** (SPEC out-of-scope).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Entry dispatch consume + Pre gate/read/validate/process/tail | Processor (`BaseProcessor.Core`, runtime-bound `{id:D}` queue) | — | The processor owns the round-trip; orchestrator only sends `EntryStepDispatch` and receives `Step*` |
| Spawn handoff (Pre Mode-2 → Post) | Processor (same process, `{id:D}-post` queue) | RabbitMQ (transport) | SPEC D-16: Post runs in the SAME console process, distinct queue |
| Output tail (validate → conditional write → send by result) | Processor (shared `OutputTail`, used by Pre inline + Post) | Redis (L2[messageId] write), Orchestrator (Step* recv) | SPEC req 4/5, D-15: single source of truth for write-gated-on-completed |
| Keeper escalation (REINJECT/INJECT/DELETE) | Keeper (`keeper-recovery` partitioned queue) | Redis (L2 ops) | On L2-op exhaustion the processor escalates; Keeper does the out-of-band op |
| L2 key formats (`skp:data:`, `skp:out:`) | `Messaging.Contracts` (`L2ProjectionKeys`) | — | Single source of truth shared by writer/reader/keeper |
| Result contracts (`Step*`, `StepOutcome`, `DataResult`) | `Messaging.Contracts` | — | Wire shapes shared across processor/keeper/orchestrator |

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01 … D-17 — from 70-CONTEXT.md `## Implementation Decisions`)

**`<data result>` contract & seam (Area 1)**
- **D-01:** One unified `DataResult` record in `Messaging.Contracts`, serving as BOTH the `ProcessAsync` seam return type AND the Post-Process wire contract (`IConsumer<DataResult>`). Spawn sends the exact object the author returned. Must be serializable.
- **D-02:** `DataResult` is self-contained: carries `data` (string JSON), `result` (4-type outcome), `executionId`, the correlation ids (`correlationId`/`workflowId`/`stepId`/`processorId`), and the carried `messageId`. Post and INJECT need nothing from elsewhere (satisfies req 7 "INJECT self-contained / never recomputes").
- **D-03:** Type named **`DataResult`**. `ProcessItem` (retired list-seam type) is removed.
- **D-04:** `result` reuses the existing **4-value `StepOutcome`** (`Processing`/`Completed`/`Failed`/`Cancelled`). No new outcome enum.
- **D-05:** Author seam keeps the name `ProcessAsync`; only the return type changes: `Task<List<ProcessItem>>` → `Task<DataResult?>` (one-or-null).

**Spawn/Delete helpers & swallow semantics (Area 2)**
- **D-06:** `SpawnToPost(DataResult, Guid executionId)` and `DeleteEntry()` are **protected methods on `BaseProcessor`** (author calls `this.SpawnToPost(...)`/`this.DeleteEntry()`). Framework wires send-provider, `RetryLoop`, keeper-escalation behind them. No new parameter on the seam signature.
- **D-07:** `SpawnToPost` runs bounded `RetryLoop`, then **SWALLOWS (drops)** the spawn on exhaustion — no throw, no keeper. Scheduler re-fires the whole entry later. ("escalate per spec" = swallow for spawn.)
- **D-08:** `DeleteEntry()` runs bounded `RetryLoop`, then escalates to the redefined `DELETE` keeper state (delete-only, no orchestrator send) on exhaustion — exactly as the framework-owned inline-tail delete does. Author writes no `RetryLoop`/keeper code.
- **D-09:** Author mints the N distinct `executionId`s and passes each to `SpawnToPost(result, newExecutionId)`. Author owns spawn policy; framework owns resilience.

**L2 output keying & keeper-contract reshape (Area 3)**
- **D-10:** Add `L2ProjectionKeys.OutputData(messageId)` → `skp:out:{messageId:D}` for `write L2[messageId]=data`. Carries the jittered `messageId` TTL (`random[ExecutionDataTtl, 2×ExecutionDataTtl]`).
- **D-11:** Delete the slot-array HASH builder `MessageIndex(messageId)` + the `SlotTtl` helper (slot model fully retired). Keep `ExecutionData(entryId)` = `skp:data:{entryId:D}`. Add `OutputData`.
- **D-12:** Reshape the three keeper contracts in-place (`KeeperReinject`/`KeeperInject`/`KeeperDelete`). Reuses the keeper-recovery queue, partitioner (4-tuple), and `RecoveryConsumerBase`. No parallel model.
  - `KeeperInject`: carries the whole `DataResult` + source `entryId` to delete.
  - `KeeperDelete`: delete-only — `entryId` only; drop the `MessageId` field/role; no orchestrator send.
  - `KeeperReinject`: stays (read `L2[entryId]`, re-inject the original dispatch with payload, envelope `MessageId` overridden to carried `messageId`; clean-absent drops).
- **D-13:** `KeeperInject` embeds the whole `DataResult` object (not flattened fields).

**Code & test structure (Area 4)**
- **D-14:** Rewrite `ProcessorPipeline` in-place as the Pre flow. `EntryStepDispatchConsumer` stays the thin shell. Recovery-pass code is **deleted**.
- **D-15:** Add `PostProcessConsumer : IConsumer<DataResult>` in `BaseProcessor.Core`. Factor the output tail into one shared helper (e.g. `OutputTail`) used by BOTH `PostProcessConsumer` and Pre's inline tail. Pre's tail additionally deletes `L2[entryId]`; Post never touches `entryId`.
- **D-16:** Post-Process runs in the same processor console process, bound at runtime like the entry endpoint, on a distinct per-processor queue `queue:{processorId:D}-post`. Same endpoint policy as entry. `SpawnToPost` sends here with carried `messageId` overriding the outbound envelope `MessageId`.
- **D-17:** Test migration: delete tests with no analog (`PipelineRecoveryFacts`, slot-allocation bits of `PipelinePostFacts`, `RecoveryPartitionFacts` slot specifics). Rewrite Pre/Post/keeper facts. Reuse hermetic doubles. `dotnet test` green is the req-12 bar.

### Claude's Discretion (from 70-CONTEXT.md)
- Exact C# property names / nullability on `DataResult` (keep consistent with existing `Step*` records and `ProcessorConfig.SerializerOptions`).
- How `result` maps to the four `Step*` send contracts in `OutputTail` (mechanical switch on `StepOutcome`).
- Input-validation / deserialize-failure → exactly-one `StepFailed` wiring (per req 2; no `entryId` delete on that path — left to TTL).
- The superseded-doc marker mechanism for `docs/design/processor-keeper-recovery-spec.md` (header marker vs replacement; default to a marker pointing at `70-SPEC.md`).
- Naming of the shared `OutputTail` helper and the new test files.

### Deferred Ideas (OUT OF SCOPE)
- None — discussion stayed within phase scope. (Orchestrator-side `entryId`↔`messageId` threading and Mode-2 N-reconciliation are SPEC-declared out of scope, owned by the orchestrator's own phase.)
</user_constraints>

---

<phase_requirements>
## Phase Requirements (SPEC req 1..12 — no REQ-ID codes; refer to them as "SPEC req N")

| SPEC req | Description | Research Support (where the implementation grounds) |
|----------|-------------|------------------------------------------------------|
| 1 | Pre gate on `L2[entryId]`; gate-fault → REINJECT | Replaces the `KeyExistsAsync(MessageIndex)` gate at `ProcessorPipeline.cs:102-114`. New gate is `KeyExistsAsync(ExecutionData(d.EntryId))`. Reinject builder + `SendKeeper` already exist (`:106`, `:360`, `:384`). |
| 2 | Pre read + input-validate + deserialize; read-fault→REINJECT; invalid→1 StepFailed, no entry delete | The read+validate+deserialize sequence is `ProcessorPipeline.cs:211-231` + `BaseProcessor`1.cs:19-26`. Note: current code DELETES the entry on the input-invalid path (`:228`); SPEC req 2 says **do NOT delete** — see Conflict C-2. |
| 3 | Seam returns one `<data result>` or null; null → return before tail | Seam signature change `BaseProcessor.cs:24` + `BaseProcessor`1.cs:37` from `Task<List<ProcessItem>>` to `Task<DataResult?>`. Null early-exit replaces the `foreach (items)` loop at `:259`. |
| 4 | Pre inline tail: validate output; write `L2[messageId]` only when completed; send by result; delete `L2[entryId]` | The new `OutputTail` (D-15). Current write-with-TTL pattern at `:283-284`; send-by-result via `SendResult` `:334`; delete via `DeleteTerminalAsync` `:316`. Write key changes from `ExecutionData(entryId)` to `OutputData(messageId)`. |
| 5 | Post-Process consumer (new) `IConsumer<DataResult>`: output-only tail, no entryId | New `PostProcessConsumer` reusing `OutputTail`. Wired like `EntryStepDispatchConsumer` (DI + `ExcludeFromConfigureEndpoints` + runtime bind). |
| 6 | REINJECT redefined: read `L2[entryId]`, re-inject to Pre, **same messageId** | `ReinjectConsumer.cs:28-56` already reads + re-injects; the **net-new** part is the envelope `MessageId` override on the `ep.Send` at `:55` (see Mechanism 2 below). |
| 7 | INJECT redefined (self-contained): write `L2[messageId]=data` from carried `DataResult`, delete `L2[entryId]`, send by result, same messageId | `InjectConsumer.cs:22-41` reshaped: write key → `OutputData(m.DataResult.MessageId)`; send via `OutputTail`-equivalent switch (currently hard-codes `StepCompleted` at `:28`); delete `ExecutionData(m.DeleteEntryId)`. |
| 8 | DELETE redefined (delete-only): delete `L2[entryId]` only, no send | `DeleteConsumer.cs:19-24` reshaped: drop the `MessageIndex` operand, single-key DEL `ExecutionData(m.EntryId)`. |
| 9 | Seam helpers `SpawnToPost` / `DeleteEntry` | New protected methods on `BaseProcessor` (D-06). Spawn swallows (D-07), Delete escalates to DELETE (D-08). Framework owns `RetryLoop`+keeper. |
| 10 | Sample two-mode `virtualProcess` | Rewrite `SampleProcessor.ProcessAsync` (`SampleProcessor.cs:37-88`): empty execId → 2× `SpawnToPost` + `DeleteEntry()` + `return null`; non-empty → one completed `DataResult`. Keep `"{StepLabel} …"` log. |
| 11 | Resilience invariants preserved | Endpoint policy proven at `ProcessorStartupOrchestrator.cs:267-275` (no retry, no error transport); send-exhaust→throw at `:339`/`:365`; jittered TTL at `:87-91`. |
| 12 | Migrate tests + supersede old doc | Test slice migration (D-17); supersede marker on `docs/design/processor-keeper-recovery-spec.md:1`. |
</phase_requirements>

---

## Standard Stack

This phase introduces **zero new packages**. It re-uses the pinned stack already in `Directory.Packages.props`.

### Core (verified versions from `Directory.Packages.props`)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 `[VERIFIED: Directory.Packages.props:137]` | Bus, `IConsumer<T>`, `ISendEndpointProvider`, `ConnectReceiveEndpoint`, `SendContext` | Last Apache-2.0 line (v9+ commercial — `Directory.Packages.props:133-137`); pinned project-wide |
| MassTransit.RabbitMQ | 8.5.5 `[VERIFIED: Directory.Packages.props:138]` | RabbitMQ transport | Transport for the runtime-bound endpoints |
| StackExchange.Redis | 2.13.1 `[VERIFIED: Directory.Packages.props:131]` | L2 (`IConnectionMultiplexer`/`IDatabase`) reads/writes/deletes | The L2 store; note overload subtleties below |
| xunit.v3 + xunit.v3.assert | 3.2.2 `[VERIFIED: Directory.Packages.props:121-122]` | Test framework (MTP runner) | Hermetic facts; `dotnet test` MTP routing |
| xunit.runner.visualstudio | 3.1.5 `[VERIFIED: Directory.Packages.props:123]` | MTP test host | Pinned for the MTP scaffold |
| NSubstitute | (CPM-pinned) `[VERIFIED: BaseApi.Tests.csproj:111]` | Mock `IDatabase`/`IConnectionMultiplexer`/`ISendEndpoint` | Repo-standard mocking lib (no Moq) |

### Supporting (in-repo, no install)
| Asset | File | Purpose |
|-------|------|---------|
| `RetryLoop.ExecuteAsync<T>` | `src/BaseConsole.Core/Resilience/RetryLoop.cs:10` | Bounded immediate-retry → `RetryOutcome<T>(Succeeded, Value, Error)`; caller routes on exhaustion |
| `RecoveryConsumerBase<T>` + `Guard` | `src/Keeper/Recovery/RecoveryConsumerBase.cs:22,43,51` | Keeper RetryLoop wrapper (rethrows on exhaust → broker nack) |
| `L2ProjectionKeys` | `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:29` | Single source of truth for Redis key formats (`const Prefix="skp:"`) |
| `StepOutcome` (4 values) | `src/Messaging.Contracts/StepOutcome.cs:15-21` | `DataResult.result` type; already exactly 4 values |
| 4 `Step*` records + `IStepResult` | `StepCompleted.cs` / `StepFailed.cs` / `StepCancelled.cs` / `StepProcessing.cs` / `IStepResult.cs` | Orchestrator-bound sends (reused verbatim) |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Envelope `MessageId` override via `Send(msg, ctx => ctx.MessageId = carried)` | A `IFilter<SendContext<T>>` like `OutboundCorrelationSendFilter` | A filter is global/ambient; the per-send lambda is local and explicit. D-16/req 11 want a **per-send** override only on spawn + keeper re-inject, so the inline-lambda overload is the right granularity. Do NOT add a bus-wide MessageId filter (would stamp every send). |
| `DataResult` as a record in `Messaging.Contracts` | A class / DTO in `BaseProcessor.Core` | D-01 mandates `Messaging.Contracts` (it is a wire contract for Post + INJECT). Records with `init` props match every existing contract (`Step*`, `Keeper*`). |

**Installation:** none.

**Version verification:** versions read directly from `Directory.Packages.props` (CPM — single source). No `npm`/registry step applies (this is .NET CPM). MassTransit 8.5.5 / StackExchange.Redis 2.13.1 / xunit.v3 3.2.2 confirmed `[VERIFIED: Directory.Packages.props:121-138]`.

---

## Architecture Patterns

### System Architecture Diagram (data flow)

```
                       orchestrator
                          │ Send EntryStepDispatch (envelope MessageId = M)
                          ▼
        ┌──────────────────────────────────────────────┐
        │ queue:{processorId:D}   (runtime-bound, no retry, no _error)
        │   EntryStepDispatchConsumer (thin shell)       │
        │     messageId = ctx.MessageId (fail-fast null) │
        │     → ProcessorPipeline.RunAsync(d, M, ct)     │
        └───────────────────────┬──────────────────────┘
                                 ▼  PRE FLOW (rewritten in-place)
   exist L2[entryId]? ──fault──► REINJECT(messageId, ids, payload); return
        │ present
        ▼
   read L2[entryId] ──fault──► REINJECT; return
        │ ok
        ▼
   input-validate ──invalid──► send StepFailed; return   (NO entry delete — req 2)
   deserialize    ──invalid──► send StepFailed; return   (NO entry delete — req 2)
        │ ok
        ▼
   DataResult? = ProcessAsync(validatedData, config, executionId, ct)
        │                              │ executionId EMPTY (Mode-2):
        │ non-empty (Mode-1):          │   author: mint N execIds,
        │   returns ONE DataResult     │   SpawnToPost(result, execId) ×N  (SWALLOW on exhaust)
        │                              │   DeleteEntry()  (escalate DELETE on exhaust)
        │                              │   returns null
        ▼                              ▼
   if DataResult == null: return  ◄────┘  (skip tail — req 3)
        │ non-null
        ▼   OutputTail (shared helper — D-15)
   validate output (invalid → result=failed)
   if result==completed: write L2[messageId]=data ──exhaust──► INJECT(DataResult); return
   send Step* by result ──exhaust──► throw → broker redelivery
   [PRE ONLY] delete L2[entryId] ──exhaust──► DELETE(entryId); return


   SpawnToPost ──► queue:{processorId:D}-post  (envelope MessageId = carried M)
        ▼
   ┌──────────────────────────────────────────────┐
   │ queue:{processorId:D}-post  (runtime-bound, same policy)
   │   PostProcessConsumer : IConsumer<DataResult>  │
   │     data = msg.data → OutputTail (NO entry delete)
   └──────────────────────────────────────────────┘
        │ write L2[messageId]=data (completed); send Step*; (no entry delete)


   KEEPER (queue:keeper-recovery, partitioned on 4-tuple, gate-open-only)
     REINJECT  read L2[entryId]; re-inject to Pre (envelope MessageId = carried M); absent→drop
     INJECT    write L2[messageId]=data (from DataResult); delete L2[entryId]; send Step* by result
     DELETE    delete L2[entryId] ONLY (no send)
```

### Recommended file layout (replace-in-place; new files marked +)
```
src/Messaging.Contracts/
├── DataResult.cs                       + (D-01: the unified spine)
├── KeeperInject.cs                       (reshape: carry DataResult + DeleteEntryId)
├── KeeperDelete.cs                       (reshape: entryId-only, drop MessageId)
├── KeeperReinject.cs                     (keep shape; no contract change)
└── Projections/L2ProjectionKeys.cs       (add OutputData; delete MessageIndex)
src/BaseProcessor.Core/Processing/
├── ProcessorPipeline.cs                  (rewrite RunAsync → Pre flow; delete recovery/slot)
├── PostProcessConsumer.cs              + (D-15: IConsumer<DataResult> → OutputTail)
├── OutputTail.cs (or method on a shared type) + (D-15: shared output tail)
├── BaseProcessor.cs                      (seam → Task<DataResult?>; + SpawnToPost/DeleteEntry)
├── BaseProcessor`1.cs                    (seam → Task<DataResult?>)
├── ProcessItem.cs                        DELETE (D-03 retired)
└── ProcessOutcome.cs                     DELETE (D-04: use StepOutcome)
src/Keeper/Recovery/
├── InjectConsumer.cs                     (reshape per req 7)
├── DeleteConsumer.cs                     (reshape per req 8)
└── ReinjectConsumer.cs                   (add envelope MessageId override per req 6)
src/Processor.Sample/SampleProcessor.cs   (two-mode rewrite per req 10)
src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs (bind {id:D}-post)
src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs (register PostProcessConsumer)
docs/design/processor-keeper-recovery-spec.md (supersede marker)
```

### Pattern 1: Runtime-bind a per-processor receive endpoint (answers research Q1)
**What:** Processor consumers are NOT auto-configured. They are registered for DI with `.ExcludeFromConfigureEndpoints()` and bound at runtime AFTER identity resolves, BEFORE `MarkHealthy`, via `IReceiveEndpointConnector.ConnectReceiveEndpoint`.

**Current entry-endpoint bind** (the template the `-post` bind copies):
```csharp
// Source: src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:266-281
var queueName = $"{context.Id!.Value:D}";   // BARE name (queue: scheme is sender-only)
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    // A18 end-state: NO bus retry latch. In-code RetryLoop owns every retry; send-exhaust throws
    // → RabbitMQ nack-requeue (no _error, no dead-letter).
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);
});
await handle.Ready;                  // queue declared + consumer attached BEFORE Healthy
context.MarkHealthy();
gate.MarkReady();
```
**For the `-post` endpoint (D-16):** add a sibling `ConnectReceiveEndpoint($"{context.Id!.Value:D}-post", (ctx, cfg) => cfg.ConfigureConsumer<PostProcessConsumer>(ctx))` and `await handle2.Ready` BEFORE `MarkHealthy()`. The endpoint needs the **same** posture: no `UseMessageRetry`, no `ConfigureError`/dead-letter — i.e. configure NOTHING but `ConfigureConsumer`, exactly as the entry bind does (the "no retry / no error transport" is the *absence* of those calls, confirmed by the comment at `:269-273` and mirrored in `RecoveryEndpointBinder.cs:55-67`). Bind both endpoints before `MarkHealthy` so the orchestrator never sends to a non-existent queue.

**DI registration** (sibling to the entry consumer):
```csharp
// Source: src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:81
x.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints();
// ADD: x.AddConsumer<PostProcessConsumer>().ExcludeFromConfigureEndpoints();
```
`PostProcessConsumer` is a normal scoped consumer; its collaborators (`IConnectionMultiplexer`, `IProcessorContext`, `ISendEndpointProvider`, `IOptions<RetryOptions>`, `IOptions<ProcessorLivenessOptions>`, `ProcessorMetrics`, `ILogger`) are all already DI-registered (the same set `ProcessorPipeline` resolves — `BaseProcessorServiceCollectionExtensions.cs:99` registers the pipeline scoped). Mirror the consumer→pipeline split (`EntryStepDispatchConsumer` delegating to a plain testable object) so the Post tail is hermetically testable too.

### Pattern 2: Send to an endpoint via `ISendEndpointProvider` (answers research Q3)
**What:** every send resolves `GetSendEndpoint(new Uri("queue:..."))` then `ep.Send((object)msg, CancellationToken.None)`, wrapped in `RetryLoop`; send-exhaust **throws** (propagates → broker redelivery).
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:334-339 (SendResult)
var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
var sent = await RetryLoop.ExecuteAsync(
    async () => { await ep.Send((object)result, CancellationToken.None); return true; }, limit, ct);
if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery
```
**`SpawnToPost` reuses this exact shape** but targets `queue:{processorId:D}-post` (the `processorId` is on `context.Id`, or on the `DataResult`/dispatch) and — critically — overrides the envelope MessageId (Pattern 3). The `(object)msg` cast is load-bearing: it makes MassTransit route the runtime type (the `CapturingSendProvider` double depends on this — `DispatchTestKit.cs:578`).

### Pattern 3: Override the outbound envelope MessageId (NET-NEW — answers research Q2)
**What:** the SPEC requires spawn sends and keeper re-injections to set the MassTransit envelope `MessageId` to the carried `messageId`. **No current code does this** (`Grep "MessageId ="` finds only the slot-array `KeeperDelete.MessageId` *body field* at `ProcessorPipeline.cs:388` and `ctx.MessageId` *reads* at `EntryStepDispatchConsumer.cs:42`). The MassTransit mechanism is the `Send` overload taking an action on `SendContext`:
```csharp
// NET-NEW pattern. The only in-repo precedent for mutating SendContext is the bus-wide
// OutboundCorrelationSendFilter (src/BaseConsole.Core/Messaging/OutboundCorrelationSendFilter.cs:17),
// which sets context.CorrelationId. The per-send analog for MessageId:
await ep.Send((object)msg, ctx => ctx.MessageId = carriedMessageId, CancellationToken.None);
```
**Confirm no inbox/MessageId dedup:** the dispatch + post + keeper-recovery endpoints configure ONLY `ConfigureConsumer` (and, for keeper, `UsePartitioner`). There is no `UseMessageRetry`, no `ConfigureError`, and **no `UseInMemoryInboxOutbox` / `UseMessageRetry` / message-dedup middleware** anywhere on these endpoints (`ProcessorStartupOrchestrator.cs:267-275`, `RecoveryEndpointBinder.cs:55-67`). So overriding MessageId to a duplicate value is safe — there is no built-in dedup that would drop a re-injected dispatch carrying a reused MessageId. `[VERIFIED: codebase grep — no inbox/outbox/dedup middleware on these endpoints]`. The planner should add a hermetic fact asserting the captured `SendContext.MessageId == carried` (this requires extending `CapturingSendProvider` to capture the `IPipe<SendContext>`/MessageId — see Test section).

### Pattern 4: `RetryLoop` → keeper-state routing (answers research Q5)
**What:** the per-op routing table the rewrite preserves:
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs — current routing
// gate read  → REINJECT   (:102-108)
// data read  → REINJECT   (:211-222)
// data write → INJECT      (:283-291)  [slot model; new model writes OutputData(messageId)]
// delete     → DELETE      (:316-330)  [new model: single-key ExecutionData(entryId)]
// send       → throw       (:334-339 / :360-366)
var write = await RetryLoop.ExecuteAsync(
    () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data, executionDataTtl), limit, ct);
if (!write.Succeeded) { await SendKeeper(BuildInject(d, item, entryId), limit, ct); ... }
```
**Jittered TTL (D-10 — `OutputData` reuses it):**
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:87-91 (SlotTtl)
private TimeSpan SlotTtl()
{
    var ttl = livenessOptions.Value.ExecutionDataTtlSeconds;
    return TimeSpan.FromSeconds(Random.Shared.Next(ttl, 2 * ttl + 1));   // random[ttl, 2×ttl]
}
```
Currently `SlotTtl()` is applied via `KeyExpireAsync` on the slot HASH (`:173`, `:275`). In the new model, the **same jittered value** is passed directly as the `StringSetAsync(..., ttl)` expiry on the `OutputData(messageId)` write. Rename/keep the helper (the `MessageIndex`-coupled docs at `:79-86` get deleted; the jitter computation survives). The plain data write at `:284` already shows the `StringSetAsync(key, value, ttl)` form — the new write uses `OutputData(messageId)` as key and the jittered TTL.

### Pattern 5: `SpawnToPost` swallow vs `DeleteEntry` escalate (answers research Q6)
Both wrap `RetryLoop`; they differ ONLY in the exhaustion branch:
```csharp
// SpawnToPost (D-07): RetryLoop the send, then SWALLOW on exhaust (no throw, no keeper).
var sent = await RetryLoop.ExecuteAsync(
    async () => { await ep.Send((object)result, ctx => ctx.MessageId = result.MessageId, CancellationToken.None); return true; },
    limit, ct);
// if (!sent.Succeeded) { /* SWALLOW — log + drop; scheduler re-fires the entry */ }   ← NO throw, NO SendKeeper

// DeleteEntry (D-08): RetryLoop the delete, then escalate DELETE on exhaust.
var del = await RetryLoop.ExecuteAsync(
    () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
if (!del.Succeeded) await SendKeeper(BuildDelete(d.EntryId, ...), limit, ct);   // escalate
```
**Swallow precedent:** the closest existing precedent is the REINJECT **clean-absent drop** (`ReinjectConsumer.cs:36-41`: increment a counter, log a structured warning, `return` — no throw, no send). And the recovery-pass "retire exhaust → do nothing" (`ProcessorPipeline.cs:175`). So "RetryLoop then swallow with a counter + warning" is an established shape; reuse the metric+warn discipline for the spawn-drop. Note these helpers run inside `ProcessAsync` (author code calls `this.SpawnToPost`), so the framework must give the seam access to the send provider + db + retry limit + a metric — wire these as protected state/fields on `BaseProcessor` populated by the pipeline before/around the seam call (the pipeline already constructs/holds all of them).

### Anti-Patterns to Avoid
- **A bus-wide MessageId filter.** Use the per-send lambda overload (Pattern 3); a global filter would stamp every send and break correlation/idempotency assumptions elsewhere.
- **Reading `data` from L2 in Post or INJECT.** D-02/D-13/req 7: the `DataResult` is self-contained on the wire — never re-read input. `INJECT` currently writes data it carries (`InjectConsumer.cs:25`) — preserve that "data in-hand, no presence read" property; just relocate the key to `OutputData`.
- **Deleting `L2[entryId]` on the input-invalid path.** SPEC req 2 leaves it to TTL. Current code deletes it (`ProcessorPipeline.cs:228`) — this is Conflict C-2; the rewrite must NOT carry the delete on that path.
- **Two divergent output tails.** D-15 mandates ONE shared `OutputTail` for Pre-inline and Post. Don't copy-paste the write/send logic.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Bounded retry with exhaustion routing | A custom retry loop in `SpawnToPost`/`DeleteEntry`/Post | `RetryLoop.ExecuteAsync` (`RetryLoop.cs:10`) | Single A3 semantics; returns `RetryOutcome<T>` for caller routing; D-08 explicitly says author writes no RetryLoop |
| Keeper escalation send + retry | A bespoke send in the new consumers | `RecoveryConsumerBase.Guard` (`RecoveryConsumerBase.cs:43`) | Rethrow-on-exhaust → broker nack; reuse for reshaped INJECT/DELETE/REINJECT |
| Redis key strings | Inline `$"skp:out:{id}"` | `L2ProjectionKeys.OutputData` (add per D-10) | Single source of truth (writer/reader/keeper must agree) |
| Outcome→Step* mapping | A new enum or if-chain | switch on `StepOutcome` → 4 `Step*` records | D-04/D-15 discretion; the records already exist; current `ResultOutcome` switch at `:351-358` is the template |
| Envelope MessageId override | A new contract field | `Send(msg, ctx => ctx.MessageId = …)` | The envelope `MessageId` IS the carried id (SPEC constraint); a body field would desync from the envelope the consumer reads (`ctx.MessageId`, `EntryStepDispatchConsumer.cs:42`) |
| Partition key for keeper | Re-derive | `ReinjectConsumerDefinition.PartitionGuid` (`ReinjectConsumerDefinition.cs:38`) | 4-tuple (excl. StepId) already pinned by `RecoveryPartitionFacts` |

**Key insight:** Almost everything is a re-wire of existing primitives. The only genuinely-new code is (1) the `DataResult` contract, (2) the per-send MessageId override lambda, (3) the `-post` endpoint bind, (4) the shared `OutputTail`. Everything else is deletion + key/contract reshaping.

---

## Runtime State Inventory

This is a code/contract rewrite with **no live-service or OS-registered state migration** in scope (container build is explicitly out of scope — SPEC §Boundaries). The audit:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Redis L2 keys change shape: the slot-array HASH `skp:msg:{messageId:D}` (`L2ProjectionKeys.cs:61`) is **retired**; output moves from `skp:data:{entryId:D}` (per-entry, framework-Post) to `skp:out:{messageId:D}` (per-message). Input key `skp:data:{entryId:D}` is **unchanged**. | **Code edit only.** No data migration: SPEC §Boundaries defers orchestrator threading and explicitly excludes the running stack; existing `skp:msg:*`/`skp:data:*` keys self-expire via their TTLs. No production data is rewritten this phase. |
| Live service config | None. No n8n/Datadog/Tailscale/Cloudflare config references these names. The only "live config" is the runtime endpoint name `{id:D}` (and the new `{id:D}-post`), which is bound fresh at startup from `context.Id` — not stored anywhere persistent. | None — verified by grep (no external service config in repo references the slot/output keys). |
| OS-registered state | None — verified. No Task Scheduler / pm2 / systemd registration embeds these identifiers; the processor binds queues at runtime from identity. | None. |
| Secrets/env vars | None. `Retry:Limit`, `Processor:ExecutionDataTtlSeconds` are config keys (unchanged). No secret name references the slot/output model. | None — verified by reading `BaseProcessorServiceCollectionExtensions.cs:102-108` (only `"Processor"` + `"Retry"` sections bound). |
| Build artifacts / installed packages | Stale `obj/` for `Messaging.Contracts`, `BaseProcessor.Core`, `Processor.Sample`, `Keeper` after the contract/seam changes. No `egg-info`/global-install analog (.NET). | `dotnet build` regenerates `obj/`; no manual reinstall. The two deleted files (`ProcessItem.cs`, `ProcessOutcome.cs`) must be removed from disk (not just emptied) so stale compiled references don't linger. |

**The canonical question — after every file is updated, what runtime systems still hold the old shape?** Only expired-by-TTL Redis keys from a prior run of the (out-of-scope) live stack. Since the container is not rebuilt/deployed this phase, there is **no runtime system to migrate** — the bar is `dotnet build` + `dotnet test` green (req 12).

---

## Common Pitfalls

### Pitfall 1: SE.Redis 2.13.1 `StringSetAsync` overload ambiguity in test doubles
**What goes wrong:** NSubstitute stubs/asserts bind to the wrong `StringSetAsync` overload, so an injected fault never fires or a `Received()` assertion never matches.
**Why it happens:** SE.Redis 2.13.1 carries multiple `StringSetAsync` overloads (the `TimeSpan? + bool keepTtl + When + flags` 6-arg, the obsolete `When` overloads, and the newer `Expiration/ValueCondition` overload). The compiler can bind a 2-arg-style call to the `Expiration/ValueCondition` overload.
**How to avoid:** The existing doubles already solve this with `When/Do` across every overload — copy that idiom verbatim (`DispatchTestKit.cs:101-119`, `:230-246`; `InjectConsumerFacts.cs:59-72` asserts on the `Expiration/ValueCondition` overload). The new `OutputData(messageId)` write fault double should mirror `PresentReadWriteFaultL2`.
**Warning signs:** a fault-injection fact that passes when it should fail (false-green); an `InOrder`/`Received` that never matches.

### Pitfall 2: Binding the `-post` endpoint after `MarkHealthy`
**What goes wrong:** the orchestrator (admits only Healthy processors) could `SpawnToPost` to a queue that isn't bound yet → send fails or message dead-ends.
**Why it happens:** the load-bearing order is bind-before-Healthy (`ProcessorStartupOrchestrator.cs:262-278`). Adding the `-post` bind in the wrong spot breaks it.
**How to avoid:** bind BOTH `{id:D}` and `{id:D}-post`, `await` both `handle.Ready`, THEN `MarkHealthy()` + `MarkReady()`. (Self-spawn within the same process means the post queue must exist before the entry consumer can run a Mode-2 spawn — which only happens post-Healthy.)
**Warning signs:** intermittent "no consumer"/send timeouts under the first dispatch after boot.

### Pitfall 3: The input-invalid path delete (Conflict C-2)
**What goes wrong:** carrying the current `DeleteTerminalAsync` call on the input-schema-invalid path violates SPEC req 2 ("`L2[entryId]` NOT deleted on that path; left to TTL").
**Why it happens:** the current code deletes on that path (`ProcessorPipeline.cs:228`) by design of the *old* model.
**How to avoid:** on input-invalid / deserialize-fail, send exactly one `StepFailed` and `return` — **no delete**. The acceptance test asserts `db.DidNotReceive().KeyDeleteAsync(ExecutionData(entryId))` on that path.
**Warning signs:** a migrated `PipelinePreFacts`/`PipelineInFacts` still asserts a delete on the invalid path.

### Pitfall 4: `(object)msg` cast on sends
**What goes wrong:** sending the statically-typed message instead of `(object)msg` makes MassTransit route the static type, and the `CapturingSendProvider` double (which captures the `object` overload) misses it.
**Why it happens:** the doubles capture `Send(Arg.Any<object>(), …)` (`DispatchTestKit.cs:578`). The keeper double captures concrete overloads (`RecoveryTestKit.cs:86-89`) — a different convention.
**How to avoid:** in `BaseProcessor.Core` sends use `(object)msg` (matches `ProcessorPipeline` convention `:338`/`:364`). When adding the MessageId-override overload, keep the `(object)` cast: `ep.Send((object)msg, ctx => ctx.MessageId = …, ct)`.
**Warning signs:** a capturing-provider list is empty when the send clearly happened.

### Pitfall 5: Deleting `ProcessItem`/`ProcessOutcome` while references remain
**What goes wrong:** `ProcessItem` (`ProcessItem.cs:7`) and `ProcessOutcome` (`ProcessOutcome.cs:5`) are referenced by the seam (`BaseProcessor.cs:24`, `BaseProcessor`1.cs:19,37`), the pipeline (`ProcessorPipeline.cs:234,261-305`), the sample (`SampleProcessor.cs` throughout), and `DispatchTestKit` (`:42-79`). Deleting the files without updating all references is a compile break.
**How to avoid:** sequence the seam + pipeline + sample + test-double changes in one wave; the build won't go green until all `ProcessItem`/`ProcessOutcome` references are gone (this is a feature — the compiler enforces completeness). `Grep "ProcessItem\|ProcessOutcome"` to find every site before deleting.

### Pitfall 6: `dotnet test --filter` is unreliable on this MTP project
**What goes wrong:** scoping the run with `dotnet test --filter "FullyQualifiedName~Processor"` silently runs the WHOLE suite (the MTP runner ignores the VSTest filter — documented in `STATE.md:636`), and `--filter-not-trait` is "unusable on this MTP project" (`STATE.md:211`).
**How to avoid:** for a scoped run use the MTP executable directly with `--filter-class` (e.g. `BaseApi.Tests.exe --filter-class "*PrePipelineFacts"`), per `STATE.md:211,236`. For the req-12 bar, run the **whole** suite: `dotnet test tests/BaseApi.Tests` (or `dotnet test SK_P.sln`). See Build/verify section.

---

## Code Examples

### `DataResult` contract shape (D-01/D-02 — answers research Q7)
The contracts library uses **default System.Text.Json** (no `[JsonPropertyName]`, no `JsonStringEnumConverter` registered — `StepOutcome.cs:9-13` confirms enums serialize as int). Records with `init` props + a positional ctor for the always-present ids is the universal convention (`KeeperInject.cs:8-15`, `StepCompleted.cs:7-12`). A faithful `DataResult` matching the convention:
```csharp
// Source: new src/Messaging.Contracts/DataResult.cs — modeled on KeeperInject.cs:8 + StepCompleted.cs:7
namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization (matches Step*/Keeper* convention).
public sealed record DataResult(Guid WorkflowId, Guid StepId, Guid ProcessorId)
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid MessageId     { get; init; }   // the carried messageId — envelope key for L2[messageId]=data
    public StepOutcome Result { get; init; }   // D-04: the existing 4-value enum (Processing/Completed/Failed/Cancelled)
    public string Data        { get; init; } = "";   // raw-JSON output (D-02 self-contained)
}
```
Note: `DataResult` is the seam return type too, so it lives in `Messaging.Contracts` (D-01) — `BaseProcessor.Core` already references `Messaging.Contracts`. `StepOutcome` already has exactly the 4 values D-04 requires `[VERIFIED: StepOutcome.cs:15-21]`. The serializer is `ProcessorConfig.SerializerOptions` for config (`ProcessorConfig.cs:18`); the bus envelope uses MassTransit's default STJ (no custom options) — `DataResult` needs no `[JsonPropertyName]`.

### Output→Step* mapping in `OutputTail` (D-15 discretion)
```csharp
// Modeled on src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:351-358 (ResultOutcome switch)
IStepResult result = dr.Result switch
{
    StepOutcome.Completed  => new StepCompleted(dr.WorkflowId, dr.StepId, dr.ProcessorId)
                                  { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = /* output key */ },
    StepOutcome.Failed     => new StepFailed(...)    { ... ErrorMessage = ... },
    StepOutcome.Cancelled  => new StepCancelled(...) { ... CancellationMessage = ... },
    StepOutcome.Processing => new StepProcessing(...) { ... },
};
```
**Open question for the planner (Q-A1):** the 4 `Step*` records carry an `EntryId` (`StepCompleted.cs:11` = "the REAL data key"). In the new model the output is keyed by `messageId` (`OutputData`), not a minted `entryId`. The planner must decide what `EntryId` the `StepCompleted` carries (likely `Guid.Empty` or the `messageId`) — this is the orchestrator-threading seam that the SPEC defers (§Boundaries out-of-scope #1). Flag for discuss-phase: the processor still has to put *something* in `StepCompleted.EntryId`. Default recommendation: carry `Guid.Empty` (the orchestrator phase will redefine the keying) OR the `messageId` — **ASSUMED**, needs confirmation.

### Keeper INJECT reshape (req 7 — answers research Q5/Q7)
```csharp
// Reshape src/Keeper/Recovery/InjectConsumer.cs:22-41 — write OutputData(messageId), send by result, delete entry.
protected override async Task HandleAsync(KeeperInject m, CancellationToken ct)
{
    var dr = m.DataResult;   // D-13: the whole DataResult embedded
    // 1) write L2[messageId] = data (in-hand; no presence read — req 7) — only when completed
    if (dr.Result == StepOutcome.Completed)
        await Guard(() => Db.StringSetAsync(L2ProjectionKeys.OutputData(dr.MessageId), dr.Data, /* jittered ttl */), ct);
    // 2) send Step* by result (the OutputTail switch — NOT hard-coded StepCompleted)
    var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
    await Guard(() => ep.Send((object)BuildStep(dr), ctx => ctx.MessageId = dr.MessageId, CancellationToken.None), ct);
    // 3) delete L2[entryId]  (the source DeleteEntryId)
    await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct);
}
```
Note the current INJECT hard-codes `StepCompleted` (`InjectConsumer.cs:28`) and writes `ExecutionData` with no TTL (`:25`) — both change. **Conflict C-3:** the pseudocode's INJECT says "write `L2[messageId]=data` … send to orchestrator based on `<data result>`" — it does **not** explicitly gate the write on `completed`, but req 7's "self-contained" + req 4's "write only when completed" imply the same gate applies. The planner should confirm INJECT writes only on `completed` (consistent with Pre/Post). **ASSUMED** — flag for discuss.

### Keeper DELETE reshape (req 8 — answers research Q5)
```csharp
// Reshape src/Keeper/Recovery/DeleteConsumer.cs:19-24 — single-key, entryId only, no send.
protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
    => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);
```
Drop the `MessageIndex(m.MessageId)` operand and the array overload; `KeeperDelete` loses its `MessageId` field (D-12).

### Keeper REINJECT MessageId override (req 6 — answers research Q2)
```csharp
// src/Keeper/Recovery/ReinjectConsumer.cs:51-55 — add the envelope MessageId override on re-inject.
var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}")), ct);
await Guard(() => ep.Send(dispatch, ctx => ctx.MessageId = /* carried messageId */, CancellationToken.None), ct);
```
**Conflict C-4 / Open question:** the current `KeeperReinject` contract has **no `messageId` field** (`KeeperReinject.cs:7-13` carries `CorrelationId/ExecutionId/EntryId/Payload`). To re-inject "with the same messageId" (req 6), `KeeperReinject` must **carry the messageId** (add a `Guid MessageId` field). D-12 says "KeeperReinject stays" — but "stays" refers to its read+reinject *behavior*; it still needs a `MessageId` field added to satisfy req 6/11's envelope override. Flag for the planner: **KeeperReinject needs a `MessageId` field added** (minor contract change, consistent with D-02's "carried messageId"). **ASSUMED** the planner adds it.

---

## State of the Art

| Old Approach (current code) | New Approach (Phase 70) | Impact |
|------------------------------|--------------------------|--------|
| Gate on `L2[messageId]` slot-array HASH (`ProcessorPipeline.cs:102`) | Gate on `L2[entryId]` existence | Single read; no HASH; no recovery pass |
| Forward + Recovery passes (`RunForwardAsync`/`RunRecoveryAsync`) | One linear Pre flow | `RunRecoveryAsync` (`:129-192`) deleted entirely |
| Framework owns Post (mint entryId, slot-record, write `L2[entryId]`) | Author owns spawn (`SpawnToPost`); Pre/Post own the output tail keyed by `messageId` | Output key `skp:data:` → `skp:out:`; no slots |
| Seam returns `List<ProcessItem>` (`BaseProcessor.cs:24`) | Seam returns `DataResult?` (one-or-null) | `ProcessItem`/`ProcessOutcome` retired |
| `KeeperInject` carries flattened EntryId/Data/DeleteEntryId (`KeeperInject.cs`) | `KeeperInject` embeds whole `DataResult` + DeleteEntryId | No field drift; self-contained |
| `KeeperDelete` two-key DEL (data + index) (`DeleteConsumer.cs:20-24`) | `KeeperDelete` single-key DEL (entry only); no send | Simpler; index gone |

**Deprecated/outdated (delete this phase):**
- `L2ProjectionKeys.MessageIndex` + the slot HASH (`L2ProjectionKeys.cs:57-61`).
- `ProcessorPipeline.RunRecoveryAsync` + `RunForwardAsync` slot/Post bits + the `RetiredSlot` sentinel (`:77`).
- `ProcessItem.cs`, `ProcessOutcome.cs`.
- `docs/design/processor-keeper-recovery-spec.md` slot/recovery-pass model → supersede marker (req 12).

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | What `EntryId` the new `Step*` sends carry (output keyed by `messageId`, but `Step*.EntryId` still exists) — default `Guid.Empty` or `messageId` | Code Examples (Output→Step*) | MED — wrong value would mis-thread the orchestrator; but orchestrator threading is SPEC-deferred, so any consistent placeholder is acceptable this phase. Confirm in discuss. |
| A2 | INJECT gates the `L2[messageId]` write on `result==completed` (consistent with Pre/Post) | Code Examples (INJECT) | LOW — pseudocode doesn't gate it explicitly but req 4/7 imply it; mismatched gating only affects non-completed-via-keeper, a rare path. |
| A3 | `KeeperReinject` gains a `Guid MessageId` field to satisfy "re-inject with same messageId" (req 6) | Code Examples (REINJECT) | MED — without it, req 6's envelope override has no source value. Almost certainly required; planner should confirm the field add is in scope (it is, under D-12 "reshape … in-place"). |
| A4 | The MassTransit 8.5.5 `ISendEndpoint.Send(T, Action<SendContext<T>>, CancellationToken)` overload exists and sets `MessageId` on the envelope | Pattern 3 | LOW-MED — this is the standard MT pattern; the in-repo `OutboundCorrelationSendFilter` mutating `SendContext.CorrelationId` confirms `SendContext.MessageId` is settable. Verify the exact overload arity against the 8.5.5 API at implementation (a 2-arg `Send(T, IPipe<SendContext<T>>)` is the guaranteed-present form if the 3-arg action overload differs). |
| A5 | Post + entry endpoints need NO extra middleware beyond `ConfigureConsumer` (no retry/no error) | Pattern 1 / Pitfall 2 | LOW — directly mirrors the entry bind (`ProcessorStartupOrchestrator.cs:267-275`) and keeper bind (`RecoveryEndpointBinder.cs:55-67`). |

**If this table looks long:** most are minor "confirm the obvious" items; A1 and A3 are the two the planner genuinely must resolve (both are contract-shape decisions the pseudocode leaves implicit).

---

## Open Questions (RESOLVED)

> Resolved during /gsd-plan-phase 70 and baked into the plans. Annotations added 2026-06-16 (plan-checker reconciliation).

1. **What does `StepCompleted.EntryId` carry now that output is keyed by `messageId`?** (A1)
   - What we know: the 4 `Step*` records keep an `EntryId` field (`StepCompleted.cs:11`); output now lives at `OutputData(messageId)`, not `ExecutionData(entryId)`.
   - What's unclear: whether the processor sets `EntryId = Guid.Empty`, `EntryId = messageId`, or something else — this is the orchestrator-threading seam the SPEC defers.
   - **RESOLVED:** default `Guid.Empty` (the orchestrator `entryId`↔`messageId` threading is SPEC-deferred, out of scope). Applied in Plan 70-02 (INJECT) and Plan 70-03 (`OutputTail.BuildStep`) — 7 `EntryId = Guid.Empty` occurrences; the orchestrator seam is explicitly NOT invented this phase.

2. **Does `KeeperReinject` need a new `MessageId` field?** (A3)
   - What we know: req 6 requires re-inject "with the same messageId"; the current contract has no messageId.
   - What's unclear: D-12 says "KeeperReinject stays" (behavioral) — does that permit a field add?
   - **RESOLVED:** yes — add `Guid MessageId` to `KeeperReinject` (in-scope under D-12 "reshape in-place"). Applied in Plan 70-01 Task 2; consumed by the REINJECT envelope override in Plan 70-02.

3. **MassTransit 8.5.5 exact `Send` + `SendContext.MessageId` API.** (A4)
   - What we know: `SendContext` is mutable (the repo's `OutboundCorrelationSendFilter` sets `CorrelationId`); MassTransit exposes a `Send` overload taking a context-callback.
   - What's unclear: the exact arity/signature in 8.5.5 (`Send(T, Action<SendContext<T>>, CancellationToken)` vs an `IPipe<SendContext<T>>` form).
   - **RESOLVED:** plans pin the per-send lambda `Send(msg, ctx => ctx.MessageId = carried, ct)` with a documented `IPipe<SendContext>` fallback to confirm at implementation; a hermetic fact captures the resulting `SendContext.MessageId` (Plan 70-04 + `CapturingSendProvider` extension). Not a bus-wide filter.

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build + test | ✓ (repo targets net8.0 throughout) | 8.0 | — |
| MassTransit / RabbitMQ libs | compile | ✓ (CPM-pinned) | 8.5.5 | — |
| StackExchange.Redis lib | compile | ✓ (CPM-pinned) | 2.13.1 | — |
| xunit.v3 MTP runner | `dotnet test` | ✓ (`BaseApi.Tests.csproj` MTP scaffold) | 3.2.2 | — |
| Live RabbitMQ / Redis / container stack | — | **NOT required** | — | All Phase-70 tests are hermetic (FakeRedis/NSubstitute/CapturingSendProvider). Container build is SPEC out-of-scope. |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** the live stack — not needed; the suite is fully hermetic (no `[Trait("Category","RealStack")]` test is added or required by this phase).

---

## Validation Architecture

Nyquist validation is enabled (no `.planning/config.json` override found disabling it). The existing suite is **unit/hermetic** (NSubstitute `IDatabase` doubles + `CapturingSendProvider` + `FakeProcessor`); no live stack is used or needed this phase.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 3.2.2 under Microsoft.Testing.Platform (MTP) |
| Config file | `tests/BaseApi.Tests/xunit.runner.json` (copied to output — `BaseApi.Tests.csproj:60`) |
| Quick run command (scoped) | MTP executable: `dotnet run --project tests/BaseApi.Tests -- --filter-class "*PrePipelineFacts"` (or run `BaseApi.Tests.exe --filter-class "*<Facts>"` from `bin/`) — **`dotnet test --filter` is unreliable here, `STATE.md:636`** |
| Full suite command | `dotnet test tests/BaseApi.Tests` (or `dotnet test SK_P.sln`) — this is the req-12 bar |

### Phase Requirements → Test Map (hermetic; one fact per acceptance criterion)
| SPEC req / AC | Behavior | Test Type | Hermetic command (scoped) | File Exists? |
|---------------|----------|-----------|----------------------------|-------------|
| req 1 | Gate-fault → exactly one REINJECT, no Step* | unit | `--filter-class "*PrePipelineFacts"` | ❌ Wave 0 (rewrite `PipelinePreFacts`) |
| req 1 | clean-absent entry → return, no processing | unit | `*PrePipelineFacts` | ❌ Wave 0 |
| req 2 | input-invalid → 1 StepFailed, **no entry delete** | unit | `*PrePipelineFacts` | ❌ Wave 0 (assert `DidNotReceive().KeyDeleteAsync`) |
| req 2 | read-fault → REINJECT | unit | `*PrePipelineFacts` (reuse `ReadFaultL2`/`AbsentReadL2`) | ❌ Wave 0 |
| req 3 | `ProcessAsync` null → no write/send/delete | unit | `*PrePipelineFacts` | ❌ Wave 0 |
| req 4 | completed → write `OutputData(messageId)` once + StepCompleted + delete entry | unit | `*OutputTailFacts` / `*PrePipelineFacts` | ❌ Wave 0 (rewrite `PipelinePostFacts`) |
| req 4 | write-exhaust → INJECT (no send/delete) | unit | `*OutputTailFacts` (new `OutputWriteFaultL2` double) | ❌ Wave 0 |
| req 4 | delete-exhaust → DELETE | unit | `*PrePipelineFacts` (reuse `ReadOkDeleteFaultL2`) | ❌ Wave 0 (rewrite `PipelineEndDeleteFacts`) |
| req 4 | non-completed → skip write, still send + delete | unit | `*OutputTailFacts` | ❌ Wave 0 |
| req 5 | Post consumer: `data=dr.data`, write-on-completed, send, **no entry read/delete** | unit | `*PostProcessConsumerFacts` | ❌ Wave 0 (NEW) |
| req 5 | Post write-exhaust → INJECT | unit | `*PostProcessConsumerFacts` | ❌ Wave 0 (NEW) |
| req 6 | REINJECT re-injects with envelope `MessageId == messageId`; absent→drop | unit | `*ReinjectConsumerFacts` | ✅ rewrite (`ReinjectConsumerFacts.cs` — add MessageId capture) |
| req 7 | INJECT writes `OutputData(messageId)`, deletes entry, sends by result, never recomputes | unit | `*InjectConsumerFacts` | ✅ rewrite (`InjectConsumerFacts.cs`) |
| req 8 | DELETE single delete, zero sends | unit | `*DeleteConsumerFacts` | ✅ rewrite (`DeleteConsumerFacts.cs`) |
| req 9 | author uses `SpawnToPost`/`DeleteEntry`; spawn swallows, delete escalates | unit | `*BaseProcessorSeamFacts` / `*SpawnDeleteFacts` | ❌ Wave 0 (rewrite `BaseProcessorSeamFacts`) |
| req 10 | empty execId → 2 Post msgs distinct execIds, Pre sends/writes/deletes nothing; non-empty → 1 completed via tail; both log `"{StepLabel} …"` | unit | `*SampleProcessorFacts` | ✅ rewrite (`SampleProcessorFacts.cs`) |
| req 11 | no retry/no error on endpoints; send-exhaust throws; jittered TTL | unit | `*DispatchBindSequenceFacts` + `*OutputTailFacts` (TTL assert) | ✅ partial (bind facts exist) / ❌ Wave 0 (TTL) |
| req 12 | full processor suite green; old doc superseded | suite | `dotnet test tests/BaseApi.Tests` | gate |

### Sampling Rate
- **Per task commit:** scoped MTP `--filter-class "*<Facts under edit>"`.
- **Per wave merge:** `dotnet test tests/BaseApi.Tests` (whole suite — the only reliable scope on MTP).
- **Phase gate:** `dotnet test tests/BaseApi.Tests` (or `dotnet test SK_P.sln`) green + `dotnet build SK_P.sln -c Release` 0 warn/0 err (the repo's standard close signal, `STATE.md` passim).

### Wave 0 Gaps (test infra to create/migrate before/with implementation — D-17)
- [ ] **DELETE** `PipelineRecoveryFacts.cs` (5 facts — no analog; recovery pass removed).
- [ ] **DELETE** the slot-allocation facts in `PipelinePostFacts.cs` (5 facts) and `RecoveryPartitionFacts.cs` slot specifics (keep the 4-tuple partition-key pin if `PartitionGuid` survives unchanged).
- [ ] **Rewrite** `PipelinePreFacts.cs` (4 facts) → new `PrePipelineFacts` (gate `L2[entryId]`, no-delete-on-invalid).
- [ ] **Rewrite** `PipelineForwardFacts.cs` (8) / `PipelineInFacts.cs` (5) / `PipelineEndDeleteFacts.cs` (7) to the linear Pre flow (drop forward/recovery branch assertions).
- [ ] **NEW** `PostProcessConsumerFacts` (req 5).
- [ ] **NEW** `OutputTailFacts` (the shared tail — write-gated-on-completed, INJECT escalation, TTL).
- [ ] **Rewrite** `SampleProcessorFacts.cs` (4) for the two-mode `DataResult?` seam (spawn-2 / accumulate-1).
- [ ] **Rewrite** `BaseProcessorSeamFacts.cs` (1) for `Task<DataResult?>` + `SpawnToPost`/`DeleteEntry`.
- [ ] **Rewrite** keeper facts: `InjectConsumerFacts` (INJECT→OutputData+DataResult), `DeleteConsumerFacts` (single-key), `ReinjectConsumerFacts` (envelope MessageId).
- [ ] **Extend** `DispatchTestKit`: add an `OutputData(messageId)`-write-fault mux (clone `PresentReadWriteFaultL2`); extend `CapturingSendProvider` to capture the `SendContext.MessageId` set by the override lambda (the current capture ignores the context callback — `DispatchTestKit.cs:578` / `RecoveryTestKit.cs:86`). Replace `Items(...)`/`FakeProcessor` `List<ProcessItem>` ctors with `DataResult?`-returning shapes.
- [ ] Framework install: none (xunit.v3 already wired).

*Existing infra reused as-is:* `FakeProcessorContext`, `RetryLoopFacts`, the `Options`/`Retry`/`Metrics`/`Dispatch` helpers in `DispatchTestKit`, `RecoveryTestKit.Db/Mux/Retry/Metrics`, `FakeRedis`.

---

## Build / Verify Commands (answers research Q9)

- **Solution file:** `SK_P.sln` (repo root). **Test project:** `tests/BaseApi.Tests/BaseApi.Tests.csproj` (the single xUnit project; references `BaseProcessor.Core`, `Processor.Sample`, `Keeper`, `Messaging.Contracts` transitively — `BaseApi.Tests.csproj:122-156`).
- **Build:** `dotnet build SK_P.sln -c Debug` (and `-c Release` for the close gate). 0 warn / 0 err is the repo standard.
- **Full test (req-12 bar):** `dotnet test tests/BaseApi.Tests` — MTP routes via `TestingPlatformDotnetTestSupport`/`UseMicrosoftTestingPlatformRunner` (`BaseApi.Tests.csproj:40-52`).
- **Scoped test (per-fact iteration):** run the MTP executable with `--filter-class "*<FactsClass>"` (NOT `dotnet test --filter` — ignored on MTP, `STATE.md:636`; `--filter-not-trait` "unusable", `STATE.md:211`).
- **No container build this phase** — SPEC §Boundaries explicitly excludes building/deploying the image ("code + `dotnet test` only").

---

## Sources

### Primary (HIGH confidence — read this session)
- `70-SPEC.md` (12 requirements + pinned pseudocode + 13 ACs) — sole source of truth.
- `70-CONTEXT.md` (D-01..D-17 locked decisions).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (current single-consumer pipeline — full read).
- `src/BaseProcessor.Core/Processing/{EntryStepDispatchConsumer,BaseProcessor,BaseProcessor`1,ProcessItem,ProcessOutcome}.cs`.
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (runtime endpoint bind).
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (consumer registration).
- `src/Keeper/Recovery/{ReinjectConsumer,InjectConsumer,DeleteConsumer,RecoveryConsumerBase,RecoveryEndpointBinder,ReinjectConsumerDefinition}.cs`.
- `src/Messaging.Contracts/{KeeperReinject,KeeperInject,KeeperDelete,IKeeperRecoverable,StepCompleted,StepFailed,StepCancelled,StepProcessing,IStepResult,StepOutcome,EntryStepDispatch,IExecutionCorrelated,ICorrelated,OrchestratorQueues,KeeperQueues,SourceStep}.cs` + `Projections/L2ProjectionKeys.cs`.
- `src/BaseConsole.Core/Resilience/RetryLoop.cs`; `src/BaseConsole.Core/Messaging/OutboundCorrelationSendFilter.cs`.
- `src/Processor.Sample/{SampleProcessor,SampleConfig,Program}.cs`.
- `tests/BaseApi.Tests/Processor/{DispatchTestKit,EntryStepDispatchConsumerFacts}.cs`; `tests/BaseApi.Tests/Keeper/{FakeRedis,RecoveryTestKit,InjectConsumerFacts,ReinjectConsumerFacts}.cs`; `tests/BaseApi.Tests/BaseApi.Tests.csproj`.
- `Directory.Packages.props` (version pins); `.planning/STATE.md` (MTP test-routing caveats).

### Secondary (MEDIUM confidence)
- MassTransit 8.5.5 `Send`/`SendContext.MessageId` overload — inferred from the in-repo `SendContext.CorrelationId` mutation precedent; **verify exact overload arity at implementation** (Open Q3 / A4).

### Tertiary (LOW confidence)
- None — every load-bearing claim is grounded in a file read.

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — versions read from CPM; zero new packages.
- Architecture / control flow: HIGH — the new flow is a re-wire of read, verified primitives; insertion points cited with file:line.
- Net-new mechanisms (MessageId override, `-post` bind, `DataResult`): HIGH on *where/how to insert*, MEDIUM on the exact MassTransit `Send` overload signature (A4) and the two contract-shape decisions (A1 `Step*.EntryId`, A3 `KeeperReinject.MessageId`).
- Pitfalls: HIGH — SE.Redis overload + MTP-filter + bind-order caveats are all observed in the current code/STATE.
- Test migration: HIGH — every existing fact file enumerated and classified.

**SPEC↔code conflicts the planner must resolve (consolidated):**
- **C-2 (must fix):** input-invalid path must NOT delete `L2[entryId]` (current `ProcessorPipeline.cs:228` deletes). SPEC req 2.
- **C-3 (confirm):** INJECT write gated on `completed`? (pseudocode implicit; A2.)
- **C-4 / A3 (must add):** `KeeperReinject` needs a `MessageId` field for the req-6 envelope override.
- **A1 (decide):** `Step*.EntryId` value under messageId-keyed output (orchestrator seam, deferred — pick a consistent placeholder).

**Research date:** 2026-06-16
**Valid until:** ~30 days (stable .NET/MassTransit stack; the only volatility is the A4 MassTransit overload, verifiable on demand).
