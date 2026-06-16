# Phase 51: Processor Forward + Recovery Pipeline - Pattern Map

**Mapped:** 2026-06-11
**Files analyzed:** 6 created/modified + 5 read-only reference
**Analogs found:** 6 / 6 (every new/modified file has an in-repo analog — zero net-new infrastructure)

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | service (framework pipeline) | event-driven / request-response (L2 I/O + send routing) | itself (current `RunAsync` Pre→In→Post→finally) + the four `Build*` builders | rewrite-in-place (self-analog) |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | consumer (MT seam) | request-response | itself (current `Consume` line 35) | 1-line signature change (self-analog) |
| `src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs` | config (options record) | — | `Configuration/ProcessorLivenessOptions.cs` | exact (same idiom) |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` | config (DI bind) | — | line 89 `Configure<ProcessorLivenessOptions>` | exact (one added line) |
| `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` | test | event-driven | `PipelinePostFacts.cs` + `PipelineEndDeleteFacts.cs` | exact (same harness) |
| `tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs` | test | event-driven | `PipelinePostFacts.cs` + `PipelineEndDeleteFacts.cs` | exact (same harness) |
| `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (EXTEND) | test fixture | — | itself (existing fault-mux factories) | extend-in-place (self-analog) |
| `tests/BaseApi.Tests/Processor/*OptionsBinding`-style fact (new SlotArray bind fact) | test | — | `ProcessorOptionsBindingFacts.cs` | exact |

**Read-only references (no edits):** `L2ProjectionKeys.cs` (`MessageIndex`/`ExecutionData`), `ProcessItem.cs`, `ProcessOutcome.cs`, `RetryLoop.cs`/`RetryOutcome`, `KeyAbsentException.cs`, `KeeperInject/Reinject/Delete.cs`.

---

## Pattern Assignments

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (service, rewrite)

**Analog:** itself — the current 242-line file is the closest pattern source for every seam; the rewrite re-sequences existing call sites into two passes.

**Ctor / primary-constructor pattern to extend** (`ProcessorPipeline.cs:55-63`):
```csharp
public sealed class ProcessorPipeline(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    BaseProcessor processor,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    IOptions<ProcessorLivenessOptions> livenessOptions,
    ProcessorMetrics metrics,
    ILogger<ProcessorPipeline> logger)
```
> Phase 51: ADD an `IOptions<SlotArrayOptions> slotOptions` ctor param (D-04). The hermetic `Build(...)` helpers and `DispatchTestKit` must pass it. Signature change: `RunAsync(EntryStepDispatch d, CancellationToken ct)` → `RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)` (D-09).

**Retry-wrapped L2 op site — the verbatim idiom to repeat at EVERY new L2 site** (slot HSET, KeyExpire, data SET, HGETALL, per-entry EXISTS, retire HSET, source delete) (`ProcessorPipeline.cs:143-149`):
```csharp
var write = await RetryLoop.ExecuteAsync(
    () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data, executionDataTtl), limit, ct);
if (!write.Succeeded)                               // output-write exhausted → failed(infra)
{
    await SendKeeper(BuildInject(d, item), limit, ct);   // KeeperInject (infra route)
    continue;                                       // next item — batch NOT aborted
}
```
> `RetryLoop.ExecuteAsync` SURFACES exhaustion (does not throw); `RetryOutcome<T>.Succeeded` gates the per-site infra route (`RetryLoop.cs:10-21,26-30`). Reuse for ALL slot-array ops. SE.Redis HASH ops have no expiry arg — apply the random TTL via a SEPARATE `db.KeyExpireAsync(MessageIndex(messageId), ttl)` after each `HashSetAsync` (D-06, Pitfall 6).

**Top-level branch (NEW — `RunAsync` dispatcher, D-07/FWD-01/RECOV-01):**
```csharp
// model on the existing Pre-read retry shape: wrap KeyExistsAsync in RetryLoop;
// exhaust → SendKeeper(BuildReinject(d)); return. Else branch:
//   exists.Value == false → RunForwardAsync(d, messageId, db, limit, ct)
//   exists.Value == true  → RunRecoveryAsync(d, messageId, db, limit, ct)
```

**FORWARD allocation-before-data block (replaces the Post at `ProcessorPipeline.cs:138-159`, Pitfall 3):**
Current Post (the WRONG order to invert) is `entryId = NewId.NextGuid(); StringSetAsync(data)` with NO slot write. New order inside the per-completed-item block:
1. `var entryId = NewId.NextGuid();`
2. `HashSetAsync(MessageIndex(messageId), slot, entryId.ToString("D"))` + `KeyExpireAsync(rand)` — exhaust → mark `infra_messageId` → DROP (`continue`, no send) (INFRA-01).
3. `StringSetAsync(ExecutionData(entryId), item.Data, executionDataTtl)` — exhaust → `SendKeeper(BuildInject(d, item, entryId, d.EntryId))` (INFRA-02).
> Slot = ordinal of COMPLETED items only (Claude's-Discretion); allocate the slot counter INSIDE the per-completed write block. Use `Random.Shared.Next(min, max + 1)` for the TTL pick.

**Send owners (UNCHANGED — reuse verbatim)** (`ProcessorPipeline.cs:182-216`): `SendResult` → `queue:{OrchestratorQueues.Result}`, `SendKeeper` → `queue:{KeeperQueues.Recovery}`; both wrap the send in `RetryLoop`, and a send-exhaust PROPAGATES via `if (!sent.Succeeded) throw sent.Error!;` (D-10). `SendResult` also increments `metrics.ResultSent` AFTER the success guard.

**Builders — id-sets to reuse + the ONE change** (`ProcessorPipeline.cs:222-241`):
```csharp
// REINJECT/DELETE carry ORIGIN exec (d.ExecutionId) — A1, D-01: NO CHANGE
private static KeeperReinject BuildReinject(EntryStepDispatch d) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId, Payload = d.Payload };
private static KeeperDelete BuildDelete(EntryStepDispatch d) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };

// STALE — current only sets Corr + Exec; MUST be rewritten (Pitfall 1, INFRA-02):
private static KeeperInject BuildInject(EntryStepDispatch d, ProcessItem item) =>
    new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = item.ExecutionId };
```
> `KeeperInject` carries `EntryId` + `Data` + `DeleteEntryId` (`KeeperInject.cs:12-14`). Rewrite to `BuildInject(d, item, entryId, sourceEntryId)` setting `EntryId = entryId`, `Data = item.Data`, `DeleteEntryId = d.EntryId`, `ExecutionId = item.ExecutionId` (D-02/D-03 created exec). This is NEW behavior — task it explicitly.

**RECOVERY fresh-exec mint (D-03, Pitfall 4):** recovery `completed` has no `item` in hand → `BuildCompleted(d, NewId.NextGuid(), entryId)`. `BuildCompleted` signature unchanged (`ProcessorPipeline.cs:222-223`).

**REMOVE: the `finally` end-delete (WR-01 landmine, Pitfall 2)** (`ProcessorPipeline.cs:74,76,167-177`):
```csharp
var readSucceeded = false;   // line 74 — DELETE this gate
try { ... }                  // line 76
finally                      // lines 167-177 — DELETE this whole block
{
    if (readSucceeded)
    {
        var del = await RetryLoop.ExecuteAsync(
            () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
        if (!del.Succeeded) await SendKeeper(BuildDelete(d), limit, ct);
    }
}
```
> Replace with TWO explicit inline source-delete tails (D-08), one per pass, reached ONLY on the no-REINJECT happy path. Forward: after the dispatch loop. Recovery: in the `else` of the `if (anyInfraEntryId) { SendKeeper(BuildReinject(d)); return; }` mutual exclusion. The delete-op body itself (the `KeyDeleteAsync` + exhaust→`BuildDelete`) is reused verbatim — only its placement moves out of `finally`.

---

### `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (consumer, 1-line change)

**Analog:** itself.

**Current call (`EntryStepDispatchConsumer.cs:35`):**
```csharp
await pipeline.RunAsync(ctx.Message, ctx.CancellationToken);
```
**Phase 51 (D-09/D-10):**
```csharp
var messageId = ctx.MessageId ?? throw new InvalidOperationException(
    "EntryStepDispatch arrived with a null MessageId — MassTransit always sets it on Send/Publish; null is a contract violation.");
await pipeline.RunAsync(ctx.Message, messageId, ctx.CancellationToken);
```
> The `context.Id!` bang precedent at line 33 shows the project's null-guard style; here D-10 wants a LOUD throw, not a bang (nullable analyzer satisfied by throw-then-flow). The metric increment at lines 32-33 stays unchanged above the new guard.

---

### `src/BaseProcessor.Core/Configuration/SlotArrayOptions.cs` (config, CREATE)

**Analog:** `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` (exact idiom).

**Mirror this shape** (`ProcessorLivenessOptions.cs:1-3,16,20-21,40-41`):
```csharp
using Microsoft.Extensions.Configuration;

namespace BaseProcessor.Core.Configuration;

public sealed class SlotArrayOptions
{
    [ConfigurationKeyName("SlotArrayTtlMin")]
    public int SlotArrayTtlMinSeconds { get; set; } = 300;   // D-05 floor = ExecutionDataTtl default (the marker outlives the data it indexes)

    [ConfigurationKeyName("SlotArrayTtlMax")]
    public int SlotArrayTtlMaxSeconds { get; set; } = 600;   // D-05 ceiling = 2× for jitter (avoid synchronized-expiry herd)
}
```
> Conventions copied from the analog: `sealed class`, seconds-int auto-props, `[ConfigurationKeyName]` mapping the property's `Seconds`-suffixed name to a bare key, baked defaults. Bound from the SAME `"Processor"` section — NOT the deleted Keeper `BackupOptions` (D-04 explicit). The `ExecutionDataTtlSeconds = 300` default at `ProcessorLivenessOptions.cs:41` is the floor reference. Key names are Claude's-discretion (drop-`Seconds` precedent).

---

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (config, MODIFY)

**Analog:** the existing liveness bind one line up.

**Add next to line 89** (`BaseProcessorServiceCollectionExtensions.cs:88-89`):
```csharp
// 3. Liveness/heartbeat knobs (CONFIG-01) — four independent seconds-ints from the "Processor" section.
services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"));
// Phase 51 (D-04): the slot-array TTL knobs, same "Processor" section.
services.Configure<SlotArrayOptions>(cfg.GetSection("Processor"));
```
> The `ProcessorPipeline` is `AddScoped` at line 86; the added `IOptions<SlotArrayOptions>` ctor dependency resolves from this bind.

---

### `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` & `PipelineRecoveryFacts.cs` (test, CREATE)

**Analog:** `tests/BaseApi.Tests/Processor/PipelinePostFacts.cs` + `PipelineEndDeleteFacts.cs` (same `DispatchTestKit` harness, no MT, plain-object construction — Phase-44 Pattern 1).

**Build helper to extend** (`PipelinePostFacts.cs:29-33`):
```csharp
private static ProcessorPipeline Build(
    IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
    DispatchTestKit.CapturingSendProvider send) =>
    new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
        DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
```
> Phase 51: add a `DispatchTestKit.SlotOptions(300, 600)` arg to the ctor + `Build`; pass `messageId` Guid to `RunAsync(d, messageId, ct)`.

**Send-capture assertion pattern — the `CapturingSendProvider` split** (`PipelinePostFacts.cs:113-116`, `PipelineEndDeleteFacts.cs:43-44,98-100,133`):
```csharp
Assert.Single(send.SentKeeper.OfType<KeeperInject>());     // infra route landed
Assert.Empty(send.Sent.OfType<StepCompleted>());           // no orchestrator result
// REINJECT⊻delete mutual exclusion (RECOV-03) — copy the EndDelete skip-on-reinject shape:
Assert.Single(send.SentKeeper.OfType<KeeperReinject>());
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>());
await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());
// FWD-03 happy tail deletes source:
await db.Received(1).KeyDeleteAsync(L2ProjectionKeys.ExecutionData(entryId));
```
> `CapturingSendProvider` (`DispatchTestKit.cs:253-275`) splits `IStepResult`→`Sent` and `IKeeperRecoverable`→`SentKeeper`, order-preserving — both passes assert against these two lists.

**SLOT-01/02 ordering assertion (NEW pattern):** use NSubstitute `Received.InOrder(() => { ...HashSetAsync...; ...StringSetAsync... })` to prove the slot HSET precedes the data SET. The `db.ReceivedCalls()` filter at `PipelinePostFacts.cs:74-95` is the precedent for inspecting received Redis calls overload-agnostically.

**SlotArrayOptions bind fact** — mirror `ProcessorOptionsBindingFacts.cs:17-40` (in-memory `["Processor:SlotArrayTtlMin"]="300"`, `cfg.GetSection("Processor").Get<SlotArrayOptions>()`, assert the two props + an empty-config baked-defaults case at lines 42-56).

**D-10 null-MessageId fact** — the only fact needing a `ConsumeContext`: NSubstitute `Substitute.For<ConsumeContext<EntryStepDispatch>>()` with `MessageId` returning null → `await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(ctx))`.

---

### `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (test fixture, EXTEND)

**Analog:** itself — the existing fault-mux factories are the template for the slot-array HASH fakes.

**Fault-mux factory template to clone for HASH ops** (`DispatchTestKit.cs:80-118`, the `PresentReadWriteFaultL2` shape): build `Substitute.For<IDatabase>()`, stub `HashGetAllAsync`/`HashSetAsync`/`KeyExistsAsync`/`KeyExpireAsync` returns (and `.When(...).Do(_ => throw boom)` fault variants), wire `mux.GetDatabase(...).Returns(db)`. Mirror the overload-robust `When/Do` style (lines 94-112) for any method with multiple SE.Redis overloads.

**Add helpers** mirroring existing ones:
- `SlotOptions(int min, int max)` → mirror `Options(int)` at lines 212-216 returning `Options.Create(new SlotArrayOptions { ... })`.
- `Dispatch(...)` (lines 239-244) — pass `messageId` through to facts (a separate Guid arg, NOT on the `EntryStepDispatch` record).
- Exist-check fakes: a matrix returning `KeyExistsAsync(MessageIndex)` = `false` (forward) / `true` (recovery) / throws (exhaust→REINJECT); a `HashGetAllAsync` returning a `HashEntry[]` of `(slot, entryId)` with per-entry `KeyExistsAsync(ExecutionData)` = exists/absent/fault (RECOV-01 temp-list matrix).

> Adapt `PipelinePre/In/Post/EndDeleteFacts` to the new `RunAsync(d, messageId, ct)` signature + removed `finally` (the EndDelete facts fold into the forward-tail facts; assertions change from "finally runs" to "inline tail runs").

---

## Shared Patterns

### Per-op bounded retry + `Succeeded`-gated routing (RESIL-01 / D-09/D-10)
**Source:** `src/BaseConsole.Core/Resilience/RetryLoop.cs:10-30`
**Apply to:** EVERY new L2 op (existence-check, slot HSET, KeyExpire, data SET, HGETALL, per-entry EXISTS, retire HSET, source delete) AND every send.
```csharp
var outcome = await RetryLoop.ExecuteAsync(() => db.SomeAsync(...), limit, ct);
if (!outcome.Succeeded) { /* per-site infra route OR (for sends) throw outcome.Error! */ }
```
> `ExecuteAsync` surfaces exhaustion as `RetryOutcome.Exhausted(last)` (does not throw); `Succeeded == false` is the infra gate. Send-exhaust is the lone re-throw (D-10).

### `KeyAbsentException` — Pre-read ONLY (do NOT copy to recovery)
**Source:** `src/BaseProcessor.Core/Resilience/KeyAbsentException.cs`, used at `ProcessorPipeline.cs:86-91`
**Apply to:** the Pre-read closure (unchanged). **Do NOT** reuse in recovery (Pitfall 3 / RESEARCH Pattern 3): recovery `not-exist` is a clean `KeyExistsAsync == false` (→ failed/drop), while a thrown Redis fault inside the RetryLoop is the `infra_entryId` route. They route DIFFERENTLY — never unify them.

### L2 key builders — single source of truth
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:42,48`
**Apply to:** all L2 sites. `ExecutionData(entryId)` = `skp:data:{entryId:D}` (data key); `MessageIndex(messageId)` = `skp:msg:{messageId:D}` (slot-array HASH). Never string-interpolate keys. HASH value format: `entryId.ToString("D")` (mirror `ExecutionData`'s `:D`); retire value `Guid.Empty.ToString()`.

### Keeper/result builders — A1 id-sets
**Source:** `ProcessorPipeline.cs:222-241`; contracts `KeeperInject.cs:8-15`, `KeeperReinject.cs:7-13`, `KeeperDelete.cs:7-12`
**Apply to:** all dispatch sites. REINJECT/DELETE carry origin `d.ExecutionId` (no change); INJECT/completed carry created exec (author-minted forward, `NewId.NextGuid()` recovery). `BuildInject` MUST be fixed to populate `EntryId`/`Data`/`DeleteEntryId` (Pitfall 1).

### Options record + DI bind
**Source:** `ProcessorLivenessOptions.cs:16-42` + `BaseProcessorServiceCollectionExtensions.cs:89`
**Apply to:** `SlotArrayOptions` (D-04). `sealed class`, `[ConfigurationKeyName]`, baked defaults, `services.Configure<>(cfg.GetSection("Processor"))`.

### Plain-object hermetic test construction (Phase-44 Pattern 1 — PRESERVE)
**Source:** `PipelinePostFacts.cs:29-33`, `DispatchTestKit.cs` (whole file)
**Apply to:** both new fact files. Construct `ProcessorPipeline` directly via `Build(...)`, pass `messageId` as a method arg, capture via `CapturingSendProvider`, fault via `DispatchTestKit` muxes. NO MassTransit harness, NO live Redis/RabbitMQ.

---

## No Analog Found

None. Every Phase-51 file maps to an existing in-repo pattern. The two genuinely-new BEHAVIORS (not files) the planner must task explicitly because no exact pattern exists:

| New behavior | Closest partial pattern | Why partial |
|--------------|------------------------|-------------|
| Slot-array HASH I/O (`HashSetAsync`/`HashGetAllAsync`/`KeyExpireAsync`/`KeyExistsAsync`) | `db.StringSetAsync`/`StringGetAsync`/`KeyDeleteAsync` sites in `ProcessorPipeline.cs` | Same `db = redis.GetDatabase()` seam + same `RetryLoop` wrap, but HASH methods are net-new call shapes; HASH has no expiry arg (separate `KeyExpireAsync`). |
| SLOT-01/02 allocation-before-data ORDERING | current Post (`ProcessorPipeline.cs:142-144`) | Current writes data with NO slot — the order must be INVERTED (Pitfall 3); no existing site proves the ordering. |
| RECOVERY fresh-exec mint | forward `BuildCompleted(d, item.ExecutionId, entryId)` | Recovery has no `item`; mints `NewId.NextGuid()` at re-send (D-03) — only id-provenance change in the phase. |

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/{Processing,Configuration,DependencyInjection}`, `src/BaseConsole.Core/Resilience`, `src/Messaging.Contracts` (+ `/Projections`), `tests/BaseApi.Tests/Processor`.
**Files scanned (read this session):** `ProcessorPipeline.cs`, `EntryStepDispatchConsumer.cs`, `ProcessorLivenessOptions.cs`, `L2ProjectionKeys.cs`, `KeeperInject.cs`, `KeeperReinject.cs`, `KeeperDelete.cs`, `RetryLoop.cs`, `ProcessItem.cs`, `DispatchTestKit.cs`, `PipelinePostFacts.cs`, `PipelineEndDeleteFacts.cs`, `ProcessorOptionsBindingFacts.cs`, `BaseProcessorServiceCollectionExtensions.cs`.
**Pattern extraction date:** 2026-06-11
</content>
</invoke>
