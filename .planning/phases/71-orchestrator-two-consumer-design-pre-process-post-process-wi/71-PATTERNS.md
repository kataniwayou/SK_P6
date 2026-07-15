# Phase 71: Orchestrator two-consumer design (Pre-Process + Post-Process) with keeper recovery - Pattern Map

**Mapped:** 2026-06-17
**Files analyzed:** 22 (10 new, 12 modified)
**Analogs found:** 22 / 22 (every new file has a direct Phase-70 analog — this is a mechanical mirror)

> **Reading note for the planner:** Phase 71 is the orchestrator-side mirror of the shipped Phase-70 processor two-consumer design. Almost every new file is a structural copy of a Phase-70 processor file or a Keeper recovery file with two locked asymmetries: (a) orchestrator keeper ops re-inject a **result** (not a dispatch) and write **`data:`** (not `out:`) → NEW contracts/consumers (D-01/D-04); (b) no-silent-loss is a **two-reason trip-end metric/log** (D-18), not a keeper/park. The data namespaces cross-pair: orchestrator **reads `out:`**, **writes `data:`** (processor does the reverse).

## File Classification

### New files

| New File (suggested name) | Role | Data Flow | Closest Analog | Match Quality |
|---------------------------|------|-----------|----------------|---------------|
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` | pipeline | request-response → fan-out | `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | exact (role+flow) |
| `src/Orchestrator/Dispatch/RelocateTail.cs` | utility (shared tail) | file-I/O (L2 write) + request-response | `src/BaseProcessor.Core/Processing/OutputTail.cs` | exact |
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs` | consumer (thin shell) | event-driven (bus) | `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs` | exact |
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumerDefinition.cs` *(if startup-bind)* | config | — | `src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs` | exact |
| `src/Messaging.Contracts/NextStepHandoff.cs` | model (wire contract) | request-response | `src/Messaging.Contracts/DataResult.cs` | role-match (shape differs) |
| `src/Messaging.Contracts/OrchestratorReinject.cs` | model (keeper contract) | event-driven | `src/Messaging.Contracts/KeeperReinject.cs` | exact |
| `src/Messaging.Contracts/OrchestratorInject.cs` | model (keeper contract) | event-driven | `src/Messaging.Contracts/KeeperInject.cs` | exact (shape differs) |
| `src/Messaging.Contracts/OrchestratorDelete.cs` | model (keeper contract) | event-driven | `src/Messaging.Contracts/KeeperDelete.cs` | exact |
| `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs` | consumer (recovery) | file-I/O + bus | `src/Keeper/Recovery/ReinjectConsumer.cs` | exact |
| `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` | consumer (recovery) | file-I/O + bus | `src/Keeper/Recovery/InjectConsumer.cs` | exact |
| `src/Keeper/Recovery/OrchestratorDeleteConsumer.cs` | consumer (recovery) | file-I/O | `src/Keeper/Recovery/DeleteConsumer.cs` | exact |
| `src/Orchestrator` (queue const) — add `ResultPost` to `OrchestratorQueues` *(modifies existing file)* | config | — | `src/Messaging.Contracts/OrchestratorQueues.cs` (`Result`) | exact |

### Modified files

| Modified File | Role | Change | Reference Analog/Section |
|---------------|------|--------|--------------------------|
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | utility | A1: `BuildStep` Completed arm `EntryId = dr.MessageId` (lines 80-81) | this file (the change is in-place) |
| `src/Keeper/Recovery/InjectConsumer.cs` | consumer | A1 (2nd site): Completed arm `EntryId = dr.MessageId` (lines 44-45) | this file (RESEARCH flags BOTH A1 sites) |
| `src/Orchestrator/Consumers/TypedResultConsumer.cs` | consumer (base) | Becomes thin shell → delegates to `OrchestratorPrePipeline`; silent-ack (68-75) → two-reason trip-end | self (see Pattern Assignments) |
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | observability | Add D-18 two-reason trip-end counter | self (extend ctor pattern, lines 44-50) |
| `src/Orchestrator/Program.cs` | config | Register pipeline/relocate-tail/RetryOptions/TTL-options/metrics; wire `-post` queue | self (lines 31-83) |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs` | config | +3 `UsePartitioner<T>` +3 `ConfigureConsumer<T>` (lines 60-66) | self |
| `src/Keeper/Program.cs` | config | +3 `AddConsumer<…>().ExcludeFromConfigureEndpoints()` (lines 67-69) | self |
| `src/Messaging.Contracts/OrchestratorQueues.cs` | config | Add `ResultPost = "orchestrator-result-post"` const | self (mirror `Result`) |
| `src/Orchestrator/Consumers/Step{Completed,Failed,Cancelled,Processing}Consumer.cs` | consumer | Unchanged signature; inherit reshaped base (D-06) | `StepCompletedConsumer.cs` |
| Tests (multiple) | test | Migrate to two-consumer model | see Shared Patterns → Testing |

---

## Pattern Assignments

### `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` (pipeline, request-response → fan-out) — NEW

**Analog:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (the gate/read/tail/escalate runner)

**Gate/read pattern** — copy `ProcessorPipeline.RunAsync` lines 60-79, **swap `ExecutionData` → `OutputData`**, key off `result.EntryId` (the upstream output messageId via A1):
```csharp
// Source: ProcessorPipeline.cs:65-78 — orchestrator reads out: not data:
var db = redis.GetDatabase();
var limit = retryOptions.Value.Limit;
var exists = await RetryLoop.ExecuteAsync(
    () => db.KeyExistsAsync(L2ProjectionKeys.OutputData(m.EntryId)), limit, ct);   // out: (was ExecutionData)
if (!exists.Succeeded) { await SendKeeper(BuildReinject(m), limit, ct); return; }  // read-exhaust → REINJECT + return
// ASYMMETRY (RESEARCH Pattern 1): a clean-absent out: blob is NOT an escalation — req 4 says a
// non-completed continuation legitimately has no out: blob → proceed with EMPTY relocated data.
```

**Fan-out + entry-delete pattern** — `SelectNext` foreach (unchanged) + send-before-delete ordering (mirrors `ProcessorPipeline.cs:121-130` tail-then-delete; RESEARCH Pattern 3):
```csharp
// SelectNext is reused UNCHANGED from StepAdvancement (src/Orchestrator/Dispatch/StepAdvancement.cs:36-43)
foreach (var (stepId, step) in advancement.SelectNext(Outcome, completed, wf.Steps))
{
    var handoff = new NextStepHandoff(m.WorkflowId, stepId, step.ProcessorId, step.Payload)
        { Data = relocatedData /* "" for non-completed */, CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId };
    // D-11: do NOT override MessageId — MassTransit assigns a fresh envelope id = the next step's messageId.
    // send wrapped in RetryLoop → send-exhaust THROWS (broker redelivery), NO delete on throw (req 5).
}
// AFTER all sends land: delete L2[out:entryId] (delete-exhaust → DELETE keeper + return). Send-BEFORE-delete.
var del = await RetryLoop.ExecuteAsync(() => db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), limit, ct);
if (!del.Succeeded) await SendKeeper(BuildDelete(m), limit, ct);
```

**Send owners (`SendKeeper`)** — copy `ProcessorPipeline.cs:187-193` verbatim (`queue:{KeeperQueues.Recovery}`, `RetryLoop`, throw-on-exhaust). The fan-out `Send` to `queue:{OrchestratorQueues.ResultPost}` mirrors `OutputTail.SendResult` (lines 106-122) but targets the new `-post` queue and does NOT override MessageId.

**Trip-end (no fan-out) arms** — replace `TypedResultConsumer.cs:68-75` silent ack with the D-18 two-reason emit (see Pattern 4 below). The terminal arm (empty `SelectNext`) and unresolved arm (L1 miss) BOTH emit.

> **Gotchas (RESEARCH Pitfalls 1, 3, 6):** resolution failure NEVER escalates a keeper (pure L1 cannot infra-fail); send-before-delete ordering is load-bearing; the gate uses `KeyExistsAsync`/`StringGetAsync`, but the keeper REINJECT drop-check uses `StringLengthAsync` (STRLEN).

---

### `src/Orchestrator/Dispatch/RelocateTail.cs` (utility, file-I/O + request-response) — NEW

**Analog:** `src/BaseProcessor.Core/Processing/OutputTail.cs` (the shared write-then-send/dispatch tail)

**Core structure** — mirror `OutputTail.RunAsync` lines 48-73, but write `ExecutionData` (`data:`) keyed by `messageId` and DISPATCH instead of send-result:
```csharp
// Analog of OutputTail.cs:60-72. messageId = context.MessageId (D-11). Completed path has non-empty h.Data.
if (!string.IsNullOrEmpty(h.Data))   // D-13: Completed wrote an out: blob → relocate; non-completed skips the write
{
    var write = await RetryLoop.ExecuteAsync(
        () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(messageId), h.Data, JitteredTtl()), limit, ct);
    if (!write.Succeeded) { await SendKeeper(BuildInject(h, messageId), limit, ct); return; }   // write-exhaust → INJECT (no dispatch)
}
// dispatch EntryStepDispatch; branch entryId on whether the write ran (RESEARCH OQ-2 / req 6):
var entryId = string.IsNullOrEmpty(h.Data) ? Guid.Empty : messageId;
await dispatcher.DispatchAsync(h.WorkflowId, h.StepId, h.ProcessorId, h.Payload,
    h.CorrelationId, h.ExecutionId, entryId, ct);   // reuse IStepDispatcher (src/Orchestrator/Dispatch/StepDispatcher.cs:23-42)
```

**Jittered TTL** — verbatim policy reuse (single source of truth, D-17), mirror `OutputTail.cs:39-40`:
```csharp
private TimeSpan JitteredTtl() => L2ProjectionKeys.OutputDataTtl(options.Value.OutputDataTtlSeconds);   // L2ProjectionKeys.cs:70
```

**Consumed by BOTH** the Post consumer AND (re-implemented inline, see firewall note) the keeper `OrchestratorInjectConsumer`. This is the exact role `OutputTail` plays for `PostProcessConsumer` + `InjectConsumer`.

> **CROSS-ASSEMBLY FIREWALL (RESEARCH Pitfall 4 — CRITICAL):** The Keeper assembly references `Messaging.Contracts`, NOT `Orchestrator`. The literal `RelocateTail` class CANNOT be shared into the Keeper `OrchestratorInjectConsumer`. Phase 70 solved the identical problem by sharing only the **policy/keys** (`L2ProjectionKeys.OutputDataTtl`, `ExecutionData`, `OutputData`) in `Messaging.Contracts` while `InjectConsumer` (`src/Keeper/Recovery/InjectConsumer.cs:36-59`) re-implements the small write+send body inline. Do the same: the orchestrator `RelocateTail` lives in `Orchestrator`; the Keeper consumer re-implements write-`data:`+delete-`out:`+dispatch inline, calling the shared keys/TTL. D-08's "single source of truth" holds at the **policy** level, not via a shared class spanning Keeper↔Orchestrator. A new `Keeper → Orchestrator` ProjectReference violates the firewall (`KeeperDependencyFirewallTests`).

---

### `src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs` (consumer thin shell, event-driven) — NEW

**Analog:** `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs` (thin `IConsumer<T>` over the shared tail)

**Shell pattern** — mirror `PostProcessConsumer.cs:24-49`:
```csharp
public async Task Consume(ConsumeContext<NextStepHandoff> ctx)
{
    metrics.<consumed-counter>.Add(1, /* tag */);            // mirror PostProcessConsumer.cs:44-45
    // D-11: messageId = ctx.MessageId (the fresh envelope id Pre let MassTransit assign).
    await relocateTail.RunAsync(ctx.Message, ctx.MessageId, ctx.CancellationToken);
}
```

> **Provenance guard (V4, RESEARCH Security):** `PostProcessConsumer.cs:34` drops a `DataResult` whose `ProcessorId != self`. The orchestrator post queue is a single competing-consumer endpoint with NO per-instance identity, so the literal self-check does NOT apply; instead keep the handoff ids internally consistent and never log `Data`/`Payload` (T-70-10).

---

### `src/Messaging.Contracts/NextStepHandoff.cs` (model, request-response) — NEW

**Analog:** `src/Messaging.Contracts/DataResult.cs` (the self-contained spine record) — **SHAPE DIFFERS** (RESEARCH rejects reuse).

**Pattern** — positional-ctor + `init` props, no `[JsonPropertyName]` (mirror `DataResult.cs:9-16`), but carry the **next-step target**, NOT the completed step's ids:
```csharp
// D-10: shaped for the NEXT step (not the completed one). NO MessageId field (D-11: rides the envelope).
public sealed record NextStepHandoff(Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload)
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }   // D-13: threaded UNCHANGED from the inbound result
    public string Data        { get; init; } = "";   // D-12: relocated input JSON, INLINE; "" for non-completed
}
```
> **Anti-patterns (RESEARCH):** do NOT add a `MessageId` body field (D-11); do NOT reuse `DataResult` (it carries the completed step's `Result` + ids — wrong shape).

---

### `src/Messaging.Contracts/OrchestratorReinject.cs` (keeper contract) — NEW

**Analog:** `src/Messaging.Contracts/KeeperReinject.cs` (lines 8-15)

**Pattern** — `: IKeeperRecoverable`, positional 3-id ctor + `init` props, in-body `MessageId` (D-03). Carries the original **Step-result** to re-inject (not a dispatch payload):
```csharp
// D-02: self-contained REINJECT — carries the original Step-result fields + EntryId (out: key) + in-body MessageId (D-03).
public sealed record OrchestratorReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // the out: key the orchestrator re-reads (STRLEN drop)
    public Guid MessageId     { get; init; }   // D-03: re-assert the SAME id on the re-injected result envelope
    // + whatever the original IStepResult re-injection needs (Outcome to pick Step* type, etc.)
}
```

---

### `src/Messaging.Contracts/OrchestratorInject.cs` (keeper contract) — NEW

**Analog:** `src/Messaging.Contracts/KeeperInject.cs` (lines 9-15) — embeds the payload + the source delete id

**Pattern** — mirror `KeeperInject` but embed the `NextStepHandoff` (not a `DataResult`) + the `out:` `entryId` to delete:
```csharp
// D-02: INJECT carries the next-step handoff (write data: + dispatch) + the out: entryId to delete + in-body MessageId (D-03).
public sealed record OrchestratorInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public NextStepHandoff Handoff { get; init; } = null!;   // mirror KeeperInject.DataResult (KeeperInject.cs:13)
    public Guid MessageId     { get; init; }   // D-03: the data: write key + dispatched entryId
    public Guid DeleteEntryId { get; init; }   // the source out: entryId to delete (mirror KeeperInject.DeleteEntryId)
}
```

---

### `src/Messaging.Contracts/OrchestratorDelete.cs` (keeper contract) — NEW

**Analog:** `src/Messaging.Contracts/KeeperDelete.cs` (lines 8-13) — delete-only, no MessageId

**Pattern** — verbatim shape; the only operand is the `out:` `entryId`:
```csharp
public sealed record OrchestratorDelete(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // DELETE-only operand — the out: entryId (delete-only, req 10)
}
```

---

### `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs` (recovery consumer) — NEW

**Analog:** `src/Keeper/Recovery/ReinjectConsumer.cs` (lines 22-59)

**STRLEN drop + envelope override** — mirror `ReinjectConsumer.cs:33-58`, **swap `ExecutionData` → `OutputData`** (read `out:`), and re-inject a **Step-result** to `queue:{OrchestratorQueues.Result}` (not a dispatch to a processor):
```csharp
// Source: ReinjectConsumer.cs:33-40 — STRLEN drop (D-05), out: namespace, never log payload.
var present = await Guard(() => Db.StringLengthAsync(L2ProjectionKeys.OutputData(m.EntryId)), ct) != 0;
if (!present) { metrics.<reinjectDropped>.Add(1); logger.LogWarning("REINJECT drop: out: gone EntryId={EntryId}", m.EntryId); return; }
// re-inject the original Step-result to the orchestrator-result queue with envelope MessageId overridden:
var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
await Guard(() => ep.Send((object)stepResult, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);  // ReinjectConsumer.cs:58
```
> **Use `StringLengthAsync` not `KeyExistsAsync`** (RESEARCH Pitfall 6): STRLEN returns 0 for both missing AND empty; `KeyExists` is true for an empty-string key. A Redis EXCEPTION on the read is infra → `Guard`/exhaustion (NOT a drop).

---

### `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` (recovery consumer) — NEW

**Analog:** `src/Keeper/Recovery/InjectConsumer.cs` (lines 25-66)

**Strict-order tail** — mirror `InjectConsumer.cs:32-59`, re-implementing the relocate body INLINE (firewall: cannot call the Orchestrator `RelocateTail`). Write `data:` → dispatch `EntryStepDispatch` → delete `out:`:
```csharp
// 1) write L2[data:messageId] (ExecutionData, not OutputData) — gated on Completed (non-empty Handoff.Data).
if (!string.IsNullOrEmpty(m.Handoff.Data))
    await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.MessageId), m.Handoff.Data, JitteredTtl()), ct);
// 2) dispatch EntryStepDispatch with envelope MessageId overridden to carried m.MessageId (InjectConsumer.cs:55-56 idiom);
//    entryId = m.MessageId (Completed) or Guid.Empty (empty data). NEVER recompute successors from L1 (req 9).
// 3) delete L2[out:entryId] (the source DeleteEntryId) AFTER the dispatch (InjectConsumer.cs:58-59 strict order).
await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.DeleteEntryId)), ct);   // out: not data:
private TimeSpan JitteredTtl() => L2ProjectionKeys.OutputDataTtl(recoveryOptions.Value.ExecutionDataTtlSeconds);  // InjectConsumer.cs:66
```
> Ctor mirrors `InjectConsumer.cs:25-30` (`IConnectionMultiplexer`, `ISendEndpointProvider`, `IOptions<RetryOptions>`, `IOptions<RecoveryOptions>`) → `: RecoveryConsumerBase<OrchestratorInject>`.

---

### `src/Keeper/Recovery/OrchestratorDeleteConsumer.cs` (recovery consumer) — NEW

**Analog:** `src/Keeper/Recovery/DeleteConsumer.cs` (lines 16-23) — the one-line delete-only mirror

**Pattern** — verbatim, **swap `ExecutionData` → `OutputData`** (delete `out:`):
```csharp
public sealed class OrchestratorDeleteConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IOptions<RetryOptions> retryOptions)
    : RecoveryConsumerBase<OrchestratorDelete>(redis, sendProvider, retryOptions)
{
    protected override async Task HandleAsync(OrchestratorDelete m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), ct);   // out: not data:
}
```

---

### `src/BaseProcessor.Core/Processing/OutputTail.cs` (utility) — MODIFIED (A1 close, req 7)

**Change in-place** at `BuildStep` Completed arm (lines 80-81):
```csharp
// CURRENT (OutputTail.cs:80-81): EntryId = Guid.Empty
// TARGET (req 7):
StepOutcome.Completed => new StepCompleted(dr.WorkflowId, dr.StepId, dr.ProcessorId)
    { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = dr.MessageId },
// Failed/Cancelled/Processing arms KEEP EntryId = Guid.Empty (no out: blob exists).
```

---

### `src/Keeper/Recovery/InjectConsumer.cs` (consumer) — MODIFIED (A1 close, 2nd site, req 7)

**RESEARCH flags BOTH A1 sites.** Change in-place at `InjectConsumer.cs:44-45` Completed arm:
```csharp
// CURRENT: EntryId = Guid.Empty
// TARGET: EntryId = dr.MessageId   (a keeper-INJECT'd completion must also carry the real out: key,
//   else the orchestrator Pre gate-misses out: on a keeper-recovered completion)
StepOutcome.Completed => new StepCompleted(dr.WorkflowId, dr.StepId, dr.ProcessorId)
    { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = dr.MessageId },
```

---

### `src/Orchestrator/Consumers/TypedResultConsumer.cs` (consumer base) — MODIFIED (D-06)

**Becomes a thin shell** delegating to `OrchestratorPrePipeline` (mirrors Phase-70 D-14: the processor's straight-through `EntryStepDispatchConsumer` became a thin shell over `ProcessorPipeline`). Keep the `protected abstract StepOutcome Outcome` knob (lines 51-54) and the top-of-`Consume` `metrics.ResultConsumed.Add` (line 63). The 4 sealed arms (`StepCompletedConsumer` etc.) stay byte-unchanged (`StepCompletedConsumer.cs:17-26`). Replace the L1-only advancement body (lines 56-89) + the silent-ack (lines 68-75) with a delegation that passes `context.MessageId` + `Outcome` to the pipeline.

> **Behavior change (RESEARCH State-of-the-Art):** the current `foreach … DispatchAsync(… m.ExecutionId …)` threads `ExecutionId` through (good), but the processor-side `ProcessorPipeline.BuildFailed/Cancelled/Processing` (lines 199-205) REGENERATE `ExecutionId` via `NewId.NextGuid()`. Req 11/D-13 mandate `ExecutionId` threaded UNCHANGED on the continuation hop. The migrated tests must flip from asserting regeneration to asserting equality.

---

### `src/Orchestrator/Observability/OrchestratorMetrics.cs` (observability) — MODIFIED (D-18)

**Extend the ctor pattern** (lines 44-50). Add one trip-end counter; the two reasons ride a `Reason` tag (RESEARCH OQ-3 recommends one counter + low-cardinality tag, matching `DispatchSent`/`ResultConsumed`):
```csharp
// snake_case name, NO _total suffix (collector appends it — OrchestratorMetrics.cs:47-49). PascalCase tag keys.
TripEnded = meter.CreateCounter<long>("orchestrator_trip_ended");
// increment site (in the pipeline trip-end arms):
metrics.TripEnded.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")),
    new KeyValuePair<string, object?>("Reason", "completed-terminal"));   // or "completed-unresolved"
```

---

### `src/Orchestrator/Program.cs` (config) — MODIFIED

**Register** (mirror existing lines 72-83): `AddSingleton<OrchestratorPrePipeline>`, `AddScoped/Singleton<RelocateTail>`, the orchestrator `OutputDataTtl` options (`Configure<…>(…GetSection("…"))` — mirror Keeper `Program.cs:26`), `Configure<RetryOptions>`. **Wire the `-post` queue** in the `AddBaseConsoleMessaging` lambda (lines 31-67): add `x.AddConsumer<OrchestratorPostProcessConsumer, OrchestratorPostProcessConsumerDefinition>();` (startup-bind, mirroring the 4 typed result consumers at lines 60-63 — RESEARCH Pitfall 5 recommends startup-bind because the post queue name is a static const, unlike the processor's runtime `{id:D}`).

---

### `src/Keeper/Recovery/RecoveryEndpointBinder.cs` + `src/Keeper/Program.cs` (config) — MODIFIED

**Binder** (lines 60-66): add 3 `UsePartitioner<T>` + 3 `ConfigureConsumer<T>` for the new contracts, reusing `ReinjectConsumerDefinition.PartitionGuid` (the 4-tuple helper, unchanged):
```csharp
cfg.UsePartitioner<OrchestratorReinject>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.UsePartitioner<OrchestratorInject>(partition,   p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.UsePartitioner<OrchestratorDelete>(partition,   p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.ConfigureConsumer<OrchestratorReinjectConsumer>(ctx);
cfg.ConfigureConsumer<OrchestratorInjectConsumer>(ctx);
cfg.ConfigureConsumer<OrchestratorDeleteConsumer>(ctx);
```
**Program** (lines 67-69): add 3 `x.AddConsumer<…>().ExcludeFromConfigureEndpoints();`. The 3 new contracts must implement `IKeeperRecoverable` (the 4-tuple) so `PartitionGuid` works unchanged.

---

## Shared Patterns

### Bounded-retry → keeper escalation map (D-16)
**Source:** `src/BaseConsole.Core/Resilience/RetryLoop.cs` (`RetryLoop.ExecuteAsync<T>` returning `RetryOutcome<T>`)
**Apply to:** every new L2 op in `OrchestratorPrePipeline` + `RelocateTail` + the 3 keeper consumers.
**Map:** read→`REINJECT`, write→`INJECT`, delete→`DELETE`, send→throw. Live path (orchestrator) uses the `if (!outcome.Succeeded) { await SendKeeper(...); return; }` idiom (`ProcessorPipeline.cs:67`); keeper path uses `Guard`/`Guard<T>` which re-throws on exhaustion (`RecoveryConsumerBase.cs:43-52`).

### L2 keys + TTL policy (single source of truth, D-12/D-17)
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`
**Apply to:** all new L2 call sites. `OutputData(entryId)` = `skp:out:` (orchestrator READS, Pre/keeper REINJECT+DELETE), `ExecutionData(messageId)` = `skp:data:` (orchestrator WRITES, Post/keeper INJECT). `OutputDataTtl(floor)` (line 70) is the ONLY jitter formula — never hand-roll `random[ttl,2ttl]`.

### Keeper recovery scaffolding (D-04)
**Source:** `src/Keeper/Recovery/RecoveryConsumerBase.cs` (`Guard`/`Guard<T>`, endpoint-gated `Consume`) + `RecoveryEndpointBinder.cs` (partitioned gate-open-only endpoint, no bus retry, no `_error`) + `ReinjectConsumerDefinition.PartitionGuid` (lines 38-42).
**Apply to:** all 3 new orchestrator keeper consumers — subclass `RecoveryConsumerBase<T>`, register `.ExcludeFromConfigureEndpoints()`, bind in the same partitioned `keeper-recovery` endpoint. The BIT health gate governs orchestrator recovery symmetrically.

### Envelope `MessageId` override (D-03/D-11)
**Source:** `ReinjectConsumer.cs:58` / `InjectConsumer.cs:56` — `ep.Send(msg, ctx => ctx.MessageId = m.MessageId, CancellationToken.None)`.
**Apply to:** the 3 keeper consumers (override with the carried in-body `MessageId`). **Do NOT apply** on the live Pre→Post fan-out (D-11: MassTransit assigns a fresh envelope id there).

### Endpoint policy (req 11)
**Source:** `StepCompletedConsumerDefinition.cs:19-27` (startup-bind, `EndpointName`, no bus retry) + `RecoveryEndpointBinder.cs:55-67` (runtime-bind alternative).
**Apply to:** the new `orchestrator-result-post` endpoint — no `UseMessageRetry`, no `_error`, send-exhaust → throw → broker redelivery. Startup-bind via an `OrchestratorPostProcessConsumerDefinition` mirroring `StepCompletedConsumerDefinition` (RESEARCH Pitfall 5).

### Never-log-payload discipline (T-70-10)
**Source:** `ProcessorPipeline.cs:113-115`, `ReinjectConsumer.cs:39`, `PostProcessConsumer.cs:36-38`.
**Apply to:** the new D-18 trip-end log + all keeper drop logs — log ids only, never `NextStepHandoff.Data` / `Payload`.

### Testing
**Source models:** `tests/BaseApi.Tests/Processor/{PrePipelineFacts,PostProcessConsumerFacts,OutputTailFacts}.cs`, `tests/BaseApi.Tests/Keeper/{Reinject,Inject,Delete}ConsumerFacts.cs` + `RecoveryTestKit.cs`, `tests/BaseApi.Tests/Orchestrator/{TypedResultConsumerFacts,ResultAckTests,OrchestratorTestStubs}.cs`.
**Apply to:** new `OrchestratorPrePipelineFacts` / `OrchestratorPostProcessConsumerFacts` / `Orchestrator{Reinject,Inject,Delete}ConsumerFacts` modeled on the processor/keeper analogs; REUSE `OrchestratorTestStubs` (Pre/Post) + `RecoveryTestKit` (keeper); UPDATE `InjectConsumerFacts.cs:75` (`Guid.Empty` → `dr.MessageId`) + the `OutputTailFacts` Completed-EntryId assertion; MIGRATE `TypedResultConsumerFacts`/`ResultAckTests` to assert `ExecutionId` equality (not regeneration). Framework: xUnit v3 under MTP; `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`.

---

## No Analog Found

None. Every new file has a direct Phase-70 (processor) or Keeper-recovery analog. The two locked asymmetries are NOT missing-analog cases — they are deliberate shape differences within an existing analog:

| File | Analog | Asymmetry (not absence) |
|------|--------|-------------------------|
| `NextStepHandoff.cs` | `DataResult.cs` | next-step target shape vs completed-step shape (D-10) — same record idiom |
| `Orchestrator{Reinject,Inject,Delete}.cs` | `Keeper{Reinject,Inject,Delete}.cs` | re-inject a result / write `data:` vs dispatch / write `out:` (D-01) — same `IKeeperRecoverable` idiom |
| D-18 trip-end metric | `OrchestratorMetrics.cs` existing counters | no-silent-loss via metric+log vs keeper/park (D-18) — same `IMeterFactory` counter idiom |

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/Processing/`, `src/Keeper/Recovery/`, `src/Orchestrator/{Consumers,Dispatch,Observability}/`, `src/Messaging.Contracts/` (+ `Projections/`), `src/{Keeper,Orchestrator}/Program.cs`.
**Files scanned:** 22 source files read in full (no re-reads).
**Pattern extraction date:** 2026-06-17
</content>
</invoke>
