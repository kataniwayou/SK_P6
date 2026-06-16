# Phase 70: Two-consumer processor design (Pre-Process + Post-Process) with keeper recovery - Pattern Map

**Mapped:** 2026-06-16
**Files analyzed:** 18 (10 modified, 4 new, 2 deleted, + test slice)
**Analogs found:** 16 / 16 in-scope files (2 deletions need no analog)

> This is a **replace-in-place re-wire**, not a greenfield build. Every new file has a direct analog in the SAME codebase. The only genuinely net-new mechanism (envelope `MessageId` override) has a *precedent* but no drop-in source — flagged below.
> All line cites verified against the live code on 2026-06-16. CONTEXT decisions D-01..D-17 are **locked** — patterns below honor them and propose no alternatives.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Messaging.Contracts/DataResult.cs` **(NEW)** | contract/record | wire-message | `src/Messaging.Contracts/KeeperInject.cs` + `StepCompleted.cs` | exact (same record shape) |
| `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs` **(NEW)** | consumer | event-driven (`IConsumer<DataResult>`) | `EntryStepDispatchConsumer.cs` (thin shell) + `RecoveryConsumerBase.cs` | role-match |
| `src/BaseProcessor.Core/Processing/OutputTail.cs` **(NEW, name = discretion)** | utility/helper | transform + L2-write + send | output-tail block inside `ProcessorPipeline.cs:257-310` | role-match (extracted from analog) |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` **(REWRITE)** | pipeline | request-response (Pre flow) | itself (current `RunForwardAsync` `:196-310`) | exact (in-place) |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` **(MODIFY)** | seam (abstract) | seam-return | itself `:24-25` | exact (in-place) |
| `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` **(MODIFY)** | seam (generic) | seam-return | itself `:19-38` | exact (in-place) |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` **(MODIFY)** | config/key-builder | n/a | itself: `ExecutionData` `:55`, `MessageIndex` `:61` | exact (in-place) |
| `src/Messaging.Contracts/KeeperInject.cs` **(RESHAPE)** | contract/record | wire-message | itself + new `DataResult.cs` | exact (in-place) |
| `src/Messaging.Contracts/KeeperDelete.cs` **(RESHAPE)** | contract/record | wire-message | itself `:8-14` | exact (in-place) |
| `src/Messaging.Contracts/KeeperReinject.cs` **(MODIFY — add MessageId)** | contract/record | wire-message | itself `:7-13` | exact (in-place) |
| `src/Keeper/Recovery/InjectConsumer.cs` **(RESHAPE)** | consumer | event-driven | itself `:22-41` + shared `OutputTail` | exact (in-place) |
| `src/Keeper/Recovery/DeleteConsumer.cs` **(RESHAPE)** | consumer | event-driven | itself `:19-24` | exact (in-place) |
| `src/Keeper/Recovery/ReinjectConsumer.cs` **(MODIFY — add MessageId override)** | consumer | event-driven | itself `:28-56` | exact (in-place) |
| `src/Processor.Sample/SampleProcessor.cs` **(REWRITE)** | author processor | transform | itself `:37-88` | exact (in-place) |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` **(MODIFY — bind `-post`)** | startup/binder | config/bind | the entry bind `:266-281` (same file) | exact (sibling bind) |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` **(MODIFY — register)** | config/DI | n/a | the entry registration `:81` + pipeline `:99` | exact (sibling line) |
| `src/BaseProcessor.Core/Processing/ProcessItem.cs` **(DELETE)** | — | — | — | n/a (retired, D-03) |
| `src/BaseProcessor.Core/Processing/ProcessOutcome.cs` **(DELETE)** | — | — | — | n/a (retired, D-04 → use `StepOutcome`) |
| `tests/BaseApi.Tests/Processor/*` + `Keeper/*` **(MIGRATE)** | test | n/a | existing facts + `DispatchTestKit`/`RecoveryTestKit`/`FakeRedis` | role-match |
| `docs/design/processor-keeper-recovery-spec.md` **(SUPERSEDE marker)** | doc | n/a | — | n/a (req 12 / D-17 discretion) |

---

## Pattern Assignments

### `src/Messaging.Contracts/DataResult.cs` (NEW — contract/record)

**Analog:** `src/Messaging.Contracts/KeeperInject.cs:8-15` + `StepCompleted.cs:7-12` (positional-id ctor + `init` props, no `[JsonPropertyName]`, default STJ).

**Record shape to copy** (the universal `Messaging.Contracts` convention — positional ctor for always-present ids, `init` props for the rest, leading `// bus envelope — NO [JsonPropertyName]` comment):
```csharp
// KeeperInject.cs:8-15 (the template):
public sealed record KeeperInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }
    public string Data        { get; init; } = "";
    public Guid DeleteEntryId { get; init; }
}
```
**Apply to `DataResult` (D-01/D-02):** same positional `(Guid WorkflowId, Guid StepId, Guid ProcessorId)` ctor; `init` props for `CorrelationId`, `ExecutionId`, `MessageId` (the carried envelope key), `Result` (type `StepOutcome` — see below), `Data` (`string`, defaulted `= ""`). **Do NOT** implement `IKeeperRecoverable` (it is a seam return + Post wire type, not a keeper message) — but it IS embedded inside the reshaped `KeeperInject` (D-13).
- `Result` uses the existing 4-value enum `StepOutcome` (`StepOutcome.cs:15-21`: `Processing=0/Completed=1/Failed=2/Cancelled=3`) — **no new enum** (D-04). It serializes as int (no `JsonStringEnumConverter` registered anywhere — `StepOutcome.cs:8-13`).
- No `[JsonPropertyName]`, no custom options — the bus uses MassTransit default STJ. **(VERIFIED: `KeeperInject.cs:3`, `StepCompleted.cs:3`, `StepOutcome.cs:8-13`.)**

---

### `src/BaseProcessor.Core/Processing/BaseProcessor.cs` + `BaseProcessor`1.cs` (MODIFY — seam)

**Analog:** themselves (in-place signature change, D-05).

**Current non-generic seam** (`BaseProcessor.cs:24-25`):
```csharp
internal abstract Task<List<ProcessItem>> ExecuteAsync(
    string validatedData, string payload, Guid executionId, CancellationToken ct);
```
**Current generic body + author seam** (`BaseProcessor`1.cs:19-38`):
```csharp
internal sealed override Task<List<ProcessItem>> ExecuteAsync(
    string validatedData, string payload, Guid executionId, CancellationToken ct)
{
    TConfig? config = string.IsNullOrWhiteSpace(payload) ? null
        : JsonSerializer.Deserialize<TConfig>(payload, ProcessorConfig.SerializerOptions);
    return ProcessAsync(validatedData, config, executionId, ct);
}
protected abstract Task<List<ProcessItem>> ProcessAsync(
    string validatedData, TConfig? config, Guid executionId, CancellationToken ct);
```
**Change to copy (D-03/D-05):** every `Task<List<ProcessItem>>` → `Task<DataResult?>` (one-or-null). Keep the name `ProcessAsync` (author-facing) and `ExecuteAsync` (internal). Keep the empty-payload→null-config guard verbatim; keep the deserialize-throws-`JsonException`→pipeline-catch contract.

**Add protected helpers (D-06)** `SpawnToPost(DataResult result, Guid executionId)` and `DeleteEntry()` on the **non-generic** `BaseProcessor` (so author calls `this.SpawnToPost(...)`/`this.DeleteEntry()`). The framework must give the seam access to the send-provider + db + retry-limit + a metric: wire these as protected state/fields populated by `ProcessorPipeline` before invoking the seam (the pipeline already constructs `redis`/`sendProvider`/`retryOptions`/`metrics` — `ProcessorPipeline.cs:63-71`). Helper bodies follow Pattern "swallow vs escalate" below.

---

### `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (REWRITE — Pre flow)

**Analog:** itself. **DELETE** `RunRecoveryAsync` (`:129-192`), the `RetiredSlot` sentinel (`:77`), `SlotTtl`'s `MessageIndex` coupling, and the slot-Post block (`:257-310` slot bits). **KEEP** the linear Pre sequence + the send/keeper/builder owners.

**Gate (req 1 — key change `MessageIndex` → `ExecutionData`):**
```csharp
// CURRENT ProcessorPipeline.cs:101-108 — gate on slot HASH:
var exists = await RetryLoop.ExecuteAsync(
    () => db.KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
if (!exists.Succeeded) { await SendKeeper(BuildReinject(d), limit, ct); return; }
```
**Rewrite:** gate on `db.KeyExistsAsync(L2ProjectionKeys.ExecutionData(d.EntryId))`; exhaust → `REINJECT` + return; clean-absent → return (no processing). NO recovery branch — delete the `if (exists.Value) RunRecovery...` fork (`:110-113`).

**Read + input-validate + deserialize (req 2):** copy the read-with-`KeyAbsentException` unify (`:211-222`) and the input-schema validate (`:225-230`) **verbatim**, EXCEPT — **Conflict C-2 / Pitfall 3** — on input-invalid / deserialize-fail send exactly ONE `StepFailed` and `return` with **NO** `DeleteTerminalAsync` (current code deletes at `:228` — drop that delete; req 2 leaves `L2[entryId]` to TTL).

**Seam call + null early-exit (req 3):**
```csharp
// CURRENT :234-235 returns a List; REWRITE to one-or-null:
var dr = await processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct);
if (dr is null) return;   // spawn handled everything — skip the tail (req 3)
```
(Keep the `try/catch (ProcessStatusException)` → exactly-one `Step*` mapping `:236-255` if the author still throws; that maps cleanly onto `BuildFailed`/`BuildCancelled`/`BuildProcessing`.)

**Inline tail (req 4):** call the shared `OutputTail` helper, then `delete L2[entryId]`. The delete reuses the existing single-key form:
```csharp
// Model the entry delete on DeleteTerminalAsync :316-330, BUT single-key now (no MessageIndex operand):
var del = await RetryLoop.ExecuteAsync(
    () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
if (!del.Succeeded) await SendKeeper(BuildDelete(d.EntryId, /*ids*/), limit, ct);   // → DELETE (req 4/8)
```

**Reuse verbatim:** `SendResult` (`:334-347`), `SendKeeper` (`:360-366`), `ResultOutcome` switch (`:351-358`), and the `metrics.ResultSent` tagging. The `(object)msg` cast on sends is load-bearing (Pitfall 4 / `:338`,`:364`).

---

### `src/BaseProcessor.Core/Processing/OutputTail.cs` (NEW — shared helper, D-15; name = discretion)

**Analog:** the output-tail logic currently inside `ProcessorPipeline.cs` (write `:283-291`, send-by-result `:299`/`:334`, outcome switch `:351-358`).

**Single source of truth for write-gated-on-completed** (used by BOTH Pre inline tail AND `PostProcessConsumer`; **Pre additionally deletes `L2[entryId]` after**, Post never touches `entryId`):
```csharp
// 1) output-validate → on invalid, force result = Failed (SPEC pseudocode line 30/39):
//    ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, dr.Data, out _)  [from :263]
// 2) write only when Completed (req 4/5) — key = OutputData(messageId), jittered TTL:
if (dr.Result == StepOutcome.Completed)
{
    var write = await RetryLoop.ExecuteAsync(
        () => db.StringSetAsync(L2ProjectionKeys.OutputData(dr.MessageId), dr.Data, JitteredTtl()), limit, ct);
    if (!write.Succeeded) { await SendKeeper(BuildInject(dr, /*deleteEntryId*/), limit, ct); return; /* INJECT + return */ }
}
// 3) send Step* by result (mechanical switch on StepOutcome → 4 records, modeled on ResultOutcome :351-358):
await SendResult(BuildStep(dr), limit, ct);   // send-exhaust → throw → broker redelivery
```
The `StringSetAsync(key, value, ttl)` write form already exists at `:284`. The outcome→`Step*` switch is the `ResultOutcome` switch inverted (Code Examples in RESEARCH.md §"Output→Step* mapping").

> **Open question A1 (planner must resolve):** the 4 `Step*` records carry an `EntryId` (`StepCompleted.cs:11` = "the REAL data key"). Output is now keyed by `messageId`. Carry `Guid.Empty` or `messageId` in `Step*.EntryId` — orchestrator threading is SPEC-deferred, so a consistent placeholder is acceptable this phase. **Default: `Guid.Empty`** unless discuss says otherwise.

---

### `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs` (NEW — `IConsumer<DataResult>`)

**Analog:** `EntryStepDispatchConsumer.cs:26-45` (thin-shell consumer → delegates to a plain testable object) + `RecoveryConsumerBase.cs:34-35` (consumer body delegates straight to a handler).

**Thin-shell pattern to copy** (`EntryStepDispatchConsumer.cs:31-45`):
```csharp
public async Task Consume(ConsumeContext<EntryStepDispatch> ctx)
{
    metrics.DispatchConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
    var messageId = ctx.MessageId ?? throw new InvalidOperationException("... null MessageId ...");
    await pipeline.RunAsync(ctx.Message, messageId, ctx.CancellationToken);
}
```
**Apply:** `PostProcessConsumer : IConsumer<DataResult>` resolves the SAME collaborator set as `ProcessorPipeline` (`IConnectionMultiplexer`, `IProcessorContext`, `ISendEndpointProvider`, `IOptions<RetryOptions>`, `IOptions<ProcessorLivenessOptions>`, `ProcessorMetrics`, `ILogger`), reads `data = ctx.Message.Data`, and runs the shared `OutputTail` (NO `entryId` read/delete). Mirror the consumer→plain-object split so the Post tail is hermetically testable (`new` the tail directly). The `DataResult` carries its own `MessageId` field, so no `ctx.MessageId` fail-fast is required for the write key (but Post may still read `ctx.MessageId` for logging).

---

### `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (MODIFY — D-10/D-11)

**Analog:** itself — the existing builders are the template.
```csharp
// KEEP verbatim (:55):
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";
// DELETE (:57-61) the slot HASH builder:
public static string MessageIndex(Guid messageId) => $"{Prefix}msg:{messageId:D}";
// ADD (model on ExecutionData; distinct `out:` namespace, D-10):
public static string OutputData(Guid messageId) => $"{Prefix}out:{messageId:D}";
```
Also delete any `SlotTtl` helper coupled to `MessageIndex` (the jitter computation itself survives — relocate to `OutputTail`/pipeline, see `ProcessorPipeline.cs:87-91`). Update the XML `<list>` doc block (`:19-27`) to drop `MessageIndex` and add `OutputData`.

**Jittered TTL to carry on the `OutputData` write (D-10 — `random[ttl, 2×ttl]`):**
```csharp
// ProcessorPipeline.cs:87-91 (SlotTtl) — keep the jitter formula, drop the MessageIndex coupling:
var ttl = livenessOptions.Value.ExecutionDataTtlSeconds;
return TimeSpan.FromSeconds(Random.Shared.Next(ttl, 2 * ttl + 1));
```

---

### `src/Messaging.Contracts/KeeperInject.cs` / `KeeperDelete.cs` / `KeeperReinject.cs` (RESHAPE, D-12/D-13)

**Analog:** themselves + `DataResult.cs`.

**`KeeperInject` (D-13 — embed whole `DataResult`):** replace the flattened `EntryId`/`Data` fields (`KeeperInject.cs:12-13`) with one `public DataResult DataResult { get; init; }`; keep `DeleteEntryId` (`:14`) as the source `entryId` to delete. Keep the positional `(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable` head — the 4-tuple partition key needs `CorrelationId`/`WorkflowId`/`ProcessorId`/`ExecutionId` on the message (`ReinjectConsumerDefinition.cs:32-33`). So keep `CorrelationId`/`ExecutionId` init props too (sourced from the embedded `DataResult`'s ids for consistency).

**`KeeperDelete` (D-12 — delete-only):** drop the `MessageId` field (`KeeperDelete.cs:13`); keep `EntryId` (`:12`) only. Keep the positional ctor + `CorrelationId`/`ExecutionId` (partition-key needs them).

**`KeeperReinject` (req 6 — ADD `MessageId`):** current shape (`KeeperReinject.cs:7-13`) carries `CorrelationId`/`ExecutionId`/`EntryId`/`Payload`. **Add** `public Guid MessageId { get; init; }` so the consumer has a source value for the envelope override (Assumption A3 — required by req 6; in-scope under D-12 "reshape in-place"). No other field change.

---

### `src/Keeper/Recovery/InjectConsumer.cs` (RESHAPE, req 7)

**Analog:** itself (`:22-41`) + the shared `OutputTail` switch.
```csharp
// CURRENT :22-41 — writes ExecutionData (no TTL), hard-codes StepCompleted:
await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), m.Data), ct);   // :25
var completed = new StepCompleted(...) { ... EntryId = m.EntryId };                            // :28-33
var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);  // :36
await Guard(() => ep.Send(completed, CancellationToken.None), ct);                             // :37
await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct);     // :40
```
**Reshape (D-13/req 7):** read `var dr = m.DataResult;` (D-13). (1) write `L2ProjectionKeys.OutputData(dr.MessageId)` with jittered TTL — **gated on `dr.Result == StepOutcome.Completed`** (Assumption A2 — pseudocode doesn't gate explicitly but req 4/7 imply it). (2) send `Step*` by result via the same outcome switch as `OutputTail` (NOT hard-coded `StepCompleted`), with the envelope `MessageId` override (`ctx => ctx.MessageId = dr.MessageId`, Pattern below). (3) delete `L2ProjectionKeys.ExecutionData(m.DeleteEntryId)`. Keep the `Guard(...)` wrapper on every op (`RecoveryConsumerBase.cs:43-52`) and the strict write→send→delete order (Pitfall 5).

### `src/Keeper/Recovery/DeleteConsumer.cs` (RESHAPE, req 8)

**Analog:** itself (`:19-24`).
```csharp
// CURRENT :19-24 — two-key DEL (data + index):
=> await Guard(() => Db.KeyDeleteAsync(new RedisKey[]
   { L2ProjectionKeys.ExecutionData(m.EntryId), L2ProjectionKeys.MessageIndex(m.MessageId) }), ct);
```
**Reshape:** single-key DEL — drop the `MessageIndex` operand and the array overload; `=> await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);` No orchestrator send (already none). `KeeperDelete` loses its `MessageId` field (above).

### `src/Keeper/Recovery/ReinjectConsumer.cs` (MODIFY, req 6 — add MessageId override)

**Analog:** itself (`:28-56`). Keep the STRLEN presence-read + clean-absent drop (`:33-41`) **verbatim** (this is also the **swallow precedent** for `SpawnToPost`: increment a counter, structured warn, `return`). The single net-new change is on the re-inject send (`:51-55`):
```csharp
// CURRENT :55 (no envelope override):
await Guard(() => ep.Send(dispatch, CancellationToken.None), ct);
// CHANGE to (req 6 — same messageId on the envelope; m.MessageId is the new field added above):
await Guard(() => ep.Send(dispatch, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);
```

---

### `src/Processor.Sample/SampleProcessor.cs` (REWRITE — two-mode, req 10)

**Analog:** itself (`:37-88`) — keep the `executionId == Guid.Empty` branch structure, the `config?.Number`/`config?.Label` guards (`:40-41`), the `JsonSerializer.Serialize(new { number, label }, ProcessorConfig.SerializerOptions)` shape (`:52-54`), and the `BeginScope(ExecutionLogScope.ExecutionId)` + log line (`:57-63`).

**Rewrite per req 10:**
- **Empty `executionId` (Mode-2):** mint 2 distinct `executionId`s (the existing `Guid.NewGuid()` at `:51`), build a `DataResult` per spawn, call `this.SpawnToPost(result, newExecutionId)` for each (D-09, swallow), call `this.DeleteEntry()`, `return null`. Log `"{label} had the following numbers: …"` (SPEC req 10 wording — adjust from the current `"step completed {StepLabel} sum {Sum}"`).
- **Non-empty `executionId` (Mode-1):** build ONE `Completed` `DataResult` (reuse inbound exec, accumulate `incomingNumber + baseNumber` as at `:71-73`), `return` it (no spawn, no delete — Pre's inline tail runs).
- Return type changes `Task<List<ProcessItem>>` → `Task<DataResult?>`; `new(ProcessOutcome.Completed, data, exec)` (`:64`,`:86`) → a `DataResult { Result = StepOutcome.Completed, Data = data, ExecutionId = exec, MessageId = ..., ...ids }`.

---

### `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (MODIFY — bind `-post`, D-16)

**Analog:** the entry-endpoint bind in the SAME method (`:266-281`).
```csharp
// CURRENT entry bind :266-276 (the template — NOTE: no UseMessageRetry, no ConfigureError; bind BEFORE MarkHealthy):
var queueName = $"{context.Id!.Value:D}";
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);
});
await handle.Ready;
// ... then MarkHealthy() :278
```
**Add a sibling bind (D-16 / Pitfall 2):** `ConnectReceiveEndpoint($"{context.Id!.Value:D}-post", (ctx, cfg) => cfg.ConfigureConsumer<PostProcessConsumer>(ctx))`, `await handle2.Ready` — **BOTH** binds and BOTH `.Ready` awaits must complete BEFORE `context.MarkHealthy()` (`:278`). Configure NOTHING but `ConfigureConsumer` (same posture; "no retry/no error" is the *absence* of those calls). Self-spawn means the `-post` queue must exist before the first Mode-2 dispatch runs.

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (MODIFY — register)

**Analog:** the entry consumer registration `:81` + the pipeline registration `:99`.
```csharp
// CURRENT :81:
x.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints();
// ADD (sibling):
x.AddConsumer<PostProcessConsumer>().ExcludeFromConfigureEndpoints();
// CURRENT :99 (register the Pre runner scoped) — add the Post tail / Post pipeline if split out:
services.AddScoped<ProcessorPipeline>();
```
All `PostProcessConsumer` collaborators are already DI-registered (the same set `ProcessorPipeline` resolves — `:86-99`). No new registrations beyond the consumer (+ the shared `OutputTail` type if it needs DI; prefer a plain object the consumer/pipeline construct).

---

### Test migration (D-17 / req 12)

**Analogs (reuse the hermetic doubles):** `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (`CapturingSendProvider` `:568-590`), `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` (`:23`,`:83-89`), `tests/BaseApi.Tests/Keeper/FakeRedis.cs`.

- **DELETE** (no analog in the new model): `PipelineRecoveryFacts.cs` (recovery pass gone), the slot-allocation bits of `PipelinePostFacts.cs`, the slot specifics of `RecoveryPartitionFacts.cs` (keep the 4-tuple partition-key facts).
- **REWRITE** to the new model: new `PrePipelineFacts` (gate `ExecutionData`, read/validate/deserialize, null early-exit, inline tail) and `PostProcessConsumerFacts` (`OutputTail`, no entry touch). Migrate `PipelinePreFacts`/`PipelineInFacts`/`PipelineEndDeleteFacts`/`EntryStepDispatchConsumerFacts`/`SampleProcessorFacts` to the seam-returns-`DataResult?` shape. Migrate `InjectConsumerFacts`/`DeleteConsumerFacts`/`ReinjectConsumerFacts` to the reshaped contracts.
- **EXTEND `CapturingSendProvider`** for the MessageId-override fact: the current double captures only the 2-arg `Send(Arg.Any<object>(), Arg.Any<CancellationToken>())` overload (`DispatchTestKit.cs:578`). To assert `SendContext.MessageId == carried`, add a capture of the `Send(object, IPipe<SendContext>/Action<SendContext>, CancellationToken)` overload and record the resulting `MessageId`. (Keeper `RecoveryTestKit` captures concrete-type overloads `:86-89` — a different convention; extend whichever the consumer-under-test uses.)
- **Pitfall (req-12 bar):** `dotnet test --filter` silently runs the whole suite on this MTP project — run the **whole** suite (`dotnet test tests/BaseApi.Tests`) for the green bar; use `BaseApi.Tests.exe --filter-class "*PrePipelineFacts"` for scoped runs (per `STATE.md:211,236`).

---

## Shared Patterns

### Bounded retry → keeper-state routing
**Source:** `RetryLoop.ExecuteAsync<T>` (`src/BaseConsole.Core/Resilience/RetryLoop.cs:10`) → `RetryOutcome<T>(Succeeded, Value, Error)`.
**Apply to:** every L2 op + send in the Pre flow, `OutputTail`, `SpawnToPost`, `DeleteEntry`, and the keeper consumers (via `Guard`). Routing table the rewrite preserves: gate/data read → **REINJECT**; output write → **INJECT**; entry delete → **DELETE**; send → **throw → broker redelivery**.
```csharp
var outcome = await RetryLoop.ExecuteAsync(() => db.SomeOpAsync(...), limit, ct);
if (!outcome.Succeeded) { /* route per op: SendKeeper(...) OR throw outcome.Error! OR swallow */ }
```

### Keeper op wrapper
**Source:** `RecoveryConsumerBase<T>.Guard` (`src/Keeper/Recovery/RecoveryConsumerBase.cs:43-52`) — RetryLoop + rethrow-on-exhaust → broker nack.
**Apply to:** all 3 reshaped keeper consumers (`InjectConsumer`/`DeleteConsumer`/`ReinjectConsumer`). Body = just the distinct state op; never hand-roll a retry.

### Send via `ISendEndpointProvider` (with `(object)` cast)
**Source:** `ProcessorPipeline.SendResult` (`:334-339`) / `SendKeeper` (`:360-366`).
**Apply to:** `OutputTail` send, `SpawnToPost`, all keeper sends.
```csharp
var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
var sent = await RetryLoop.ExecuteAsync(async () => { await ep.Send((object)result, CancellationToken.None); return true; }, limit, ct);
if (!sent.Succeeded) throw sent.Error!;   // send-exhaust → throw → broker redelivery
```
The `(object)msg` cast is **load-bearing** (Pitfall 4 — `CapturingSendProvider` captures the `object` overload `:578`).

### Envelope `MessageId` override (NET-NEW — precedent only)
**Precedent (NOT a drop-in):** `OutboundCorrelationSendFilter<T>` (`src/BaseConsole.Core/Messaging/OutboundCorrelationSendFilter.cs:15-20`) sets `context.CorrelationId = id` on `SendContext` — proving `SendContext` envelope fields are settable. No current send sets `MessageId`.
**Apply to:** `SpawnToPost` (→ `-post` queue) and keeper `REINJECT`/`INJECT` re-injections. Use the **per-send lambda** overload (NOT a bus-wide filter — Anti-Pattern):
```csharp
await ep.Send((object)msg, ctx => ctx.MessageId = carriedMessageId, CancellationToken.None);
```
Confirm the exact MT 8.5.5 overload arity at implementation (Assumption A4); the guaranteed-present form is `Send(T, IPipe<SendContext<T>>)` if the 3-arg action overload differs. No inbox/dedup middleware on these endpoints, so a reused `MessageId` is safe (RESEARCH §Pattern 3).

### `SpawnToPost` swallow vs `DeleteEntry` escalate (the one asymmetry — D-07/D-08)
**Source:** REINJECT clean-absent drop (`ReinjectConsumer.cs:36-41`: counter + structured warn + `return`, no throw/send) is the **swallow precedent**; `DeleteTerminalAsync` (`ProcessorPipeline.cs:316-330`) is the **escalate** precedent.
- `SpawnToPost`: RetryLoop the send → on exhaust **SWALLOW** (log + drop + counter, like `:36-41`); scheduler re-fires. **No throw, no keeper.**
- `DeleteEntry`: RetryLoop the `KeyDeleteAsync(ExecutionData(entryId))` → on exhaust escalate **DELETE** keeper (like `:329`).

### Runtime-bind endpoint posture
**Source:** entry bind `ProcessorStartupOrchestrator.cs:266-281` + `ExcludeFromConfigureEndpoints` (`BaseProcessorServiceCollectionExtensions.cs:81`).
**Apply to:** the `-post` endpoint — same `ConnectReceiveEndpoint` + `ConfigureConsumer`-only posture, bound before `MarkHealthy`.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| (none) | — | — | Every in-scope file has a same-codebase analog. |

**Net-new MECHANISM (not a file) with precedent-only source:** the per-send envelope `MessageId` override — precedent `OutboundCorrelationSendFilter.cs:17` (sets `CorrelationId`), but the planner must write the `Send(msg, ctx => ctx.MessageId = …)` call fresh; no copy-paste source exists.

---

## Open Questions for the Planner (from RESEARCH Assumptions Log)

| # | Question | Default | Risk |
|---|----------|---------|------|
| A1 | What `EntryId` do the new `Step*` sends carry (output keyed by `messageId`)? | `Guid.Empty` (orchestrator threading SPEC-deferred) | MED — any consistent placeholder acceptable this phase |
| A2 | Does `INJECT` gate the `L2[messageId]` write on `result == completed`? | Yes (consistent with Pre/Post) | LOW |
| A3 | `KeeperReinject` gains a `Guid MessageId` field (req 6 envelope override source) | Yes (in-scope under D-12) | MED — without it req 6 has no source value |
| A4 | Exact MT 8.5.5 `Send(T, Action<SendContext<T>>, ct)` overload arity | Verify at impl; fall back to `Send(T, IPipe<SendContext<T>>)` | LOW-MED |

---

## Metadata

**Analog search scope:** `src/Messaging.Contracts/` (+`/Projections/`), `src/BaseProcessor.Core/Processing/` `/Startup/` `/DependencyInjection/`, `src/Keeper/Recovery/`, `src/BaseConsole.Core/Resilience/` `/Messaging/`, `src/Processor.Sample/`, `tests/BaseApi.Tests/Processor/` `/Keeper/`.
**Files scanned:** 22 read (all cites verified live 2026-06-16).
**Pattern extraction date:** 2026-06-16
**Skills:** installed `.claude/skills` are frontend/UI-oriented — none apply to this .NET backend phase.
