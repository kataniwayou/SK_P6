# Phase 52: Three-State Keeper - Pattern Map

**Mapped:** 2026-06-11
**Files analyzed:** 13 (8 modify / 1 delete-or-verify / ~4 create)
**Analogs found:** 13 / 13 (every new/modified file has an in-repo analog — this is a "wire existing pieces" phase)

> **Read-this-first:** Nearly every pattern this phase needs already exists in `src/Keeper/Recovery/*`, `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs`, and the `tests/BaseApi.Tests/Keeper/*` fact harness. Copy from the cited line ranges below. The ONLY genuinely new surface is the `ConnectReceiveEndpoint` handle plumbing (OQ-1), for which the closest analog is the existing static `AddConsumer` block in `Program.cs:40-50` plus the connect-callback shape in 52-RESEARCH Pattern 1.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Keeper/Recovery/ReinjectConsumer.cs` (MODIFY) | consumer | event-driven (L2 read → re-inject) | itself (in-place flip) + `DeleteConsumer.cs` for drop-on-absent | exact (self) |
| `src/Keeper/Recovery/InjectConsumer.cs` (IMPLEMENT) | consumer | event-driven (write → send → delete) | `ReinjectConsumer.cs:30-48` (Guard+send) + `DeleteConsumer.cs:19-20` (Guard+delete) | exact (sibling) |
| `src/Keeper/Recovery/DeleteConsumer.cs` (VERIFY) | consumer | event-driven (L2 delete) | itself — likely zero change | exact (self) |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs` (MODIFY) | base/abstract consumer | request-response wrapper | itself (remove gate-wait block) | exact (self) |
| `src/Keeper/Recovery/RecoveryGateTimeoutException.cs` (DELETE — lives in base, lines 83-86) | exception | — | (delete, D-09) | n/a |
| `src/Keeper/Recovery/RecoveryDataGoneException.cs` (DELETE) | exception | — | (delete after REINJECT flip, D-06) | n/a |
| `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` (MODIFY) | config/endpoint-definition | config | itself (lines 42-67) — re-home into connect callback OR keep + add policy branch | exact (self) |
| `src/Keeper/Health/BitHealthLoop.cs` (MODIFY) | hosted-service / driver | event-driven (edge-trigger) | itself (lines 36-50 edges) | exact (self) |
| `src/Keeper/RecoveryOptions.cs` (MODIFY) | config/options | config | itself + `ProcessorMetrics`-style enum const conventions | exact (self) |
| `src/Keeper/Program.cs` (MODIFY) | composition root | config | itself (lines 40-50 static AddConsumer) + 52-RESEARCH Pattern 1 connect-callback | role-match |
| `src/Keeper/Observability/KeeperMetrics.cs` (CREATE) | observability/metrics | — | `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` | exact |
| `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` (CREATE) | test | — | `DeleteConsumerFacts.cs` + `ReinjectConsumerFacts.cs` | exact |
| `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` (MODIFY) | test | — | itself (flip the absent fact) | exact (self) |
| pause/accumulate fact + SustainedOutage fact (CREATE) | test (integration) | — | `RecoveryDeadLetterFacts.cs` / `KeeperDlqConsolidationTests.cs` (ITestHarness) | exact |
| `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` (MODIFY) | test | — | itself (add fake handle + Stop/Start assertions) | exact (self) |

---

## Shared Patterns

These cross-cutting conventions apply to MULTIPLE files below. Each per-file section assumes them.

### Guard / RetryLoop (SURVIVES UNCHANGED — apply to every L2 op + Send)
**Source:** `src/Keeper/Recovery/RecoveryConsumerBase.cs:64-75`
```csharp
protected async Task<T> Guard<T>(Func<Task<T>> op, CancellationToken ct)
{
    var outcome = await RetryLoop.ExecuteAsync(op, RetryLimit, ct);
    if (!outcome.Succeeded) throw outcome.Error!;
    return outcome.Value!;
}
protected Task Guard(Func<Task> op, CancellationToken ct)
    => Guard(async () => { await op(); return true; }, ct);
```
**Apply to:** `InjectConsumer` (all 3 ops), `ReinjectConsumer` (read + send), `DeleteConsumer` (delete). The give-up re-throw → inherited `ConsolidatedErrorTransportFilter` → `skp-dlq-1` is the DLQ1 mechanism (KEEP-05/D-02). DO NOT add per-consumer `ConfigureError`.

### IN-01 inner-send convention (apply to every broker `Send` inside a body)
**Source:** `src/Keeper/Recovery/ReinjectConsumer.cs:44-47`
```csharp
// Inner broker Send uses CancellationToken.None ("do not abort a started broker send");
// the OUTER Guard keeps ct so the bounded RetryLoop still observes bus shutdown between attempts.
await Guard(() => ep.Send(dispatch, CancellationToken.None), ct);
```
**Apply to:** `InjectConsumer`'s `StepCompleted` send, `ReinjectConsumer`'s `EntryStepDispatch` send.

### L2 key builder (single source of truth)
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:42`
```csharp
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";
```
**Apply to:** REINJECT reads `ExecutionData(m.EntryId)`; INJECT writes `ExecutionData(m.EntryId)` and deletes `ExecutionData(m.DeleteEntryId)`; DELETE deletes `ExecutionData(m.EntryId)`. NO key-shape change this phase.

### Structured-logging holes (V7 — never interpolate)
**Source convention (cited in 52-RESEARCH §Security):** `PauseAllConsumer.cs:23` style — `{EntryId}` holes, never `$"..."`. Apply to the D-07 drop log. NEVER log `m.Data` / `m.Payload` contents.

---

## Pattern Assignments

### `src/Keeper/Recovery/ReinjectConsumer.cs` (consumer, event-driven) — MODIFY (KEEP-01 / D-06 / D-07)

**Analog:** itself. Keep the read+reconstruct+send body; flip ONLY the absent-key path from throw → drop + log + metric. Delete-on-absent reference shape is `DeleteConsumer.cs:20`.

**Current body to keep** (`ReinjectConsumer.cs:37-47` — reconstruction + send unchanged):
```csharp
var dispatch = new EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload)
{
    CorrelationId = m.CorrelationId,
    ExecutionId = m.ExecutionId,
    EntryId = m.EntryId,
};
var ep = await Send.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}"));
await Guard(() => ep.Send(dispatch, CancellationToken.None), ct);   // IN-01
```

**The ONLY change** — replace the throw block (`ReinjectConsumer.cs:30-35`):
```csharp
// BEFORE (Phase 46) — throw-terminal on absent:
await Guard(async () =>
{
    if (await Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)) == 0)
        throw new RecoveryDataGoneException();   // terminal → skp-dlq-1
    return true;
}, ct);

// AFTER (Phase 52) — Guard the READ (a Redis EXCEPTION is still infra → exhaustion policy),
// but absent/empty (no exception, STRLEN==0) → silent ack + log + counter:
var present = await Guard(
    () => Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)),
    ct) != 0;
if (!present)
{
    _metrics.ReinjectDropped.Add(1);                                          // D-07 (new ctor dep)
    _logger.LogWarning("REINJECT drop: L2 data gone EntryId={EntryId}", m.EntryId);  // D-07 structured hole
    return;                                                                   // D-06 silent ack
}
// ... then the unchanged reconstruct + send above
```
**Notes for planner:**
- STRLEN (not StringGet) per IN-04 — STRLEN returns 0 for absent OR empty without pulling the blob (`ReinjectConsumer.cs:26-29` comment).
- This adds TWO ctor deps (`KeeperMetrics`, `ILogger<ReinjectConsumer>`). The 5-param base-ctor forwarding pattern is at `ReinjectConsumer.cs:17-20` — extend the primary ctor with the two new params (do NOT pass them to `base(...)`).
- The exact `Guard(...) != 0` bool shape is a planner detail; the `Guard<T>` overload returns `T` (here `long`).

---

### `src/Keeper/Recovery/InjectConsumer.cs` (consumer, event-driven) — IMPLEMENT (KEEP-02)

**Analog:** `ReinjectConsumer.cs:30-48` (Guard + GetSendEndpoint + IN-01 send) and `DeleteConsumer.cs:19-20` (Guard + delete). Current state is a no-op stub (`InjectConsumer.cs:21-22`).

**Current stub to replace** (`InjectConsumer.cs:21-22`):
```csharp
protected override Task HandleAsync(KeeperInject m, CancellationToken ct)
    => Task.CompletedTask;   // Phase-50 (D-01) compile-only no-op stub
```

**A18 forward-only body** (write → send StepCompleted → delete; strict order per Pitfall 5):
```csharp
protected override async Task HandleAsync(KeeperInject m, CancellationToken ct)
{
    // 1) write L2[entryId] = data  (data in-hand on the envelope — forward-only, NO presence read)
    await Guard(() => Db.StringSetAsync(L2ProjectionKeys.ExecutionData(m.EntryId), m.Data), ct);

    // 2) send StepCompleted → orchestrator result queue (A15)
    var completed = new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
    {
        CorrelationId = m.CorrelationId,
        ExecutionId   = m.ExecutionId,
        EntryId       = m.EntryId,        // the REAL data key just written
    };
    var ep = await Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
    await Guard(() => ep.Send(completed, CancellationToken.None), ct);   // IN-01

    // 3) delete L2[deleteEntryId]  (source cleanup tail — AFTER the confirmed send)
    await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct);
}
```
**RESOLVED — orchestrator result-queue URI (52-RESEARCH A4):** INJECT sends to `queue:{OrchestratorQueues.Result}` (= `"queue:orchestrator-result"`). This is the SAME const the processor's result-send uses — VERIFIED at `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:315`:
```csharp
var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
```
`OrchestratorQueues.Result = "orchestrator-result"` (`src/Messaging.Contracts/OrchestratorQueues.cs:16`). This is a DIFFERENT target than REINJECT's `queue:{ProcessorId:D}`.

**Contracts (VERIFIED, final from Phase 50):**
- `KeeperInject(Guid WorkflowId, Guid StepId, Guid ProcessorId)` + `{CorrelationId, ExecutionId, EntryId, Data, DeleteEntryId}` (`src/Messaging.Contracts/KeeperInject.cs:8-15`).
- `StepCompleted(Guid WorkflowId, Guid StepId, Guid ProcessorId)` + `{CorrelationId, ExecutionId, EntryId}` (`src/Messaging.Contracts/StepCompleted.cs:7-12`).

**New ctor `using`:** add `using Messaging.Contracts;` already present; `StepCompleted`/`OrchestratorQueues` are in `Messaging.Contracts`, `L2ProjectionKeys` in `Messaging.Contracts.Projections` (add that using — present in `DeleteConsumer.cs:5`).

---

### `src/Keeper/Recovery/DeleteConsumer.cs` (consumer, event-driven) — VERIFY (KEEP-03)

**Analog:** itself. Likely ZERO change. The full body (`DeleteConsumer.cs:19-20`):
```csharp
protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
    => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);
```
`KeyDeleteAsync` no-ops on a missing key → drops-on-absent (KEEP-03 satisfied, VERIFIED). Confirm against A18 line 217; research found it matches. No metric/log required (only REINJECT's absent-drop is observability-instrumented per D-07).

---

### `src/Keeper/Recovery/RecoveryConsumerBase.cs` (base consumer) — MODIFY (D-04 / D-09)

**Analog:** itself. KEEP `Guard`/`Guard<T>` (lines 64-75) and the ctor/DI shape (lines 26-36). REMOVE the bounded gate-wait block and the `RecoveryGateTimeoutException` class.

**REMOVE — the gate-wait block** (`RecoveryConsumerBase.cs:38-58`): the `using var cts`, `cts.CancelAfter(GateWaitSeconds)`, `gate.WaitForOpenAsync`, and the `catch (OperationCanceledException) → throw new RecoveryGateTimeoutException()`. After removal `Consume` collapses to:
```csharp
public Task Consume(ConsumeContext<TMessage> context)
    => HandleAsync(context.Message, context.CancellationToken);   // gate now enforced at the ENDPOINT (D-04)
```
**REMOVE — the exception class** at the bottom of this file (`RecoveryConsumerBase.cs:83-86`):
```csharp
public sealed class RecoveryGateTimeoutException : Exception { ... }   // DELETE (D-09)
```
**Ctor `IL2HealthGate gate` param (`RecoveryConsumerBase.cs:29`):** only consumed by the removed wait. Planner decision — likely drop it from the base ctor (cascades to the 3 subclass ctors + every `new XConsumer(...)` test call site, and the `RecoveryTestKit.OpenGate()` helper). Verify no other base member reads `gate` (it does not — only line 48). Keeping it as an unused param is the lower-churn option if test call-site churn is a concern; planner picks.

---

### `src/Keeper/Recovery/RecoveryDataGoneException.cs` — DELETE (D-06)

**Source-side users of `RecoveryDataGoneException` (grep-verified):** only `ReinjectConsumer.cs:33` (the throw being flipped) and `RecoveryConsumerBase.cs`'s doc-comment cross-ref. INJECT does NOT use it (data in-hand, no read). After the REINJECT flip there are NO source throwers → safe to delete the file.
**Test-side users (MUST update first):** `ReinjectConsumerFacts.cs:64,84` and `RecoveryDeadLetterFacts.cs:21,94,113`. Rewrite those (see test sections) BEFORE deleting, or the build breaks (Pitfall 4).

---

### `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` (config) — MODIFY (KEEP-05 / D-01..D-03 / D-08)

**Analog:** itself. This is the single-owner endpoint config. Two possible homes depending on OQ-1:

**Reusable shape to KEEP (retry + 3× shared partitioner)** (`ReinjectConsumerDefinition.cs:42-67`):
```csharp
endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));   // DLQ1 path
var partition = new Partitioner(_recoveryOptions.Value.PartitionCount, new Murmur3UnsafeHashGenerator());
endpointConfigurator.UsePartitioner<KeeperReinject>(partition, p => PartitionGuid(p.Message));
endpointConfigurator.UsePartitioner<KeeperInject>(partition, p => PartitionGuid(p.Message));
endpointConfigurator.UsePartitioner<KeeperDelete>(partition, p => PartitionGuid(p.Message));
```
**KEEP the static key helpers** (`ReinjectConsumerDefinition.cs:74-84`) — `public static PartitionKey` / `PartitionGuid` are test-pinned by `RecoveryPartitionFacts`; preserve verbatim regardless of OQ-1.

**Policy-conditional branch (KEEP-05):**
- `Dlq1` (default): keep `UseMessageRetry(r => r.Immediate(limit))` exactly as above → exhaustion re-throws → inherited `ConsolidatedErrorTransportFilter` → `skp-dlq-1` (PROVEN by `RecoveryDeadLetterFacts` / `KeeperDlqConsolidationTests`).
- `SustainedOutage`: do NOT consume the retry-then-dead-letter path — configure broker requeue / no dead-letter (52-RESEARCH Pattern 2; exact 8.5.5 call is OQ-2 / Assumption A1 — verify in-phase). KEEP-04 pause/resume is SEPARATE (D-05).

**OQ-1 (planner MUST decide):** If converting to `ConnectReceiveEndpoint` (52-RESEARCH Pattern 1, the only viable 8.5.5 runtime-pause path), this entire `ConfigureConsumer` body RE-HOMES into the single connect callback in `Program.cs`, and the three `ConsumerDefinition`s' no-op concern dissolves. If a static-endpoint pause is found, the definition stays here + add the policy branch. Research found NO static-endpoint runtime pause in 8.5.5.

---

### `src/Keeper/Health/BitHealthLoop.cs` (hosted-service driver) — MODIFY (KEEP-04 / D-04)

**Analog:** itself. Add endpoint Stop/Start onto the EXISTING health edges. Preserve WR-01 (a failed Stop/Start/Publish must NOT advance `prevHealthy`) and the `prevHealthy=null` first-tick semantics (locked by `BitHealthLoopTests`).

**Existing edge block to extend** (`BitHealthLoop.cs:38-49`):
```csharp
if (healthy)
{
    gate.Open();
    await endpointHandle.ReceiveEndpoint.Start(stoppingToken);    // NEW (D-04): resume + drain
    await bus.Publish(new ResumeAll { CorrelationId = NewId.NextGuid() }, stoppingToken);   // existing A14
    logger.LogInformation("L2 healthy — gate OPEN, ResumeAll broadcast");
}
else
{
    gate.Close();
    await endpointHandle.ReceiveEndpoint.Stop(stoppingToken);     // NEW (D-04): basic.cancel; accumulate
    await bus.Publish(new PauseAll { CorrelationId = NewId.NextGuid() }, stoppingToken);     // existing A14
    logger.LogWarning("L2 unhealthy — gate CLOSED, PauseAll broadcast");
}
prevHealthy = healthy;   // advance ONLY after the edge work succeeded (WR-01, line 50)
```
**New ctor dep:** the connected `HostReceiveEndpointHandle` (a singleton from `Program.cs` per OQ-1), injected alongside `probe/gate/bus/opts/logger` (ctor at `BitHealthLoop.cs:12-17`). The WR-01 try/catch (lines 36-60) already covers the new awaits — a Stop/Start throw lands in the same `catch (Exception)` that leaves `prevHealthy` un-advanced. Note `gate.Open()/Close()` likely becomes redundant for the recovery path but is still read elsewhere/by tests — DO NOT remove in-phase (OQ-3, out of D-08 scope).

---

### `src/Keeper/RecoveryOptions.cs` (options) — MODIFY (D-01 / D-09)

**Analog:** itself. ADD the `ExhaustionPolicy` enum + property (D-01, default `Dlq1` per D-02); REMOVE `GateWaitSeconds` (D-09).

**Current file** (`RecoveryOptions.cs:9-22`) — KEEP `PartitionCount` (line 11, still drives the partitioner); DELETE `GateWaitSeconds` (line 21) and its WR-02 doc-comment (lines 13-20).
```csharp
public sealed class RecoveryOptions
{
    public int PartitionCount { get; set; } = 8;                       // KEEP (D-06)
    public ExhaustionPolicy ExhaustionPolicy { get; set; } = ExhaustionPolicy.Dlq1;   // ADD (D-01/D-02)
    // GateWaitSeconds — DELETE (D-09, obsoleted by D-04 pause/resume)
}

public enum ExhaustionPolicy { Dlq1, SustainedOutage }                 // ADD (D-01)
```
**Binding:** already bound from the `"Recovery"` section in `Program.cs:30` — the new `ExhaustionPolicy` key binds automatically; a removed `GateWaitSeconds` appsettings entry becomes inert (no binder error). Add `Recovery:ExhaustionPolicy` to `src/Keeper/appsettings.json`; optionally clean the stale `Recovery:GateWaitSeconds` entry.

---

### `src/Keeper/Program.cs` (composition root) — MODIFY (OQ-1)

**Analog:** itself (current static registration) + 52-RESEARCH Pattern 1 (connect-callback). Current site (`Program.cs:40-50`):
```csharp
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<Keeper.Recovery.ReinjectConsumer, Keeper.Recovery.ReinjectConsumerDefinition>();
    x.AddConsumer<Keeper.Recovery.InjectConsumer,   Keeper.Recovery.InjectConsumerDefinition>();
    x.AddConsumer<Keeper.Recovery.DeleteConsumer,   Keeper.Recovery.DeleteConsumerDefinition>();
});
```
**OQ-1 conversion (if adopted):** register the three consumers WITHOUT auto-configuring the `keeper-recovery` endpoint, then `connector.ConnectReceiveEndpoint(KeeperQueues.Recovery, (ctx, cfg) => { ... })` once at startup (after bus start), moving the retry + 3× partitioner + ExhaustionPolicy branch from `ReinjectConsumerDefinition` into the callback, `await handle.Ready`, and store the handle in a singleton for `BitHealthLoop`. Pitfall 1: exactly ONE source configures `keeper-recovery` (no static + connect collision). Pitfall 3: consider connecting STOPPED so the first healthy BIT edge starts it (matches the fail-safe-closed gate). See 52-RESEARCH Pattern 1 lines 162-182 for the connect-callback shape.

**ALSO register the new meter** (mirror `ProcessorMetrics` registration, see below):
```csharp
builder.Services.AddSingleton<Keeper.Observability.KeeperMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(Keeper.Observability.KeeperMetrics.MeterName));
```

---

### `src/Keeper/Observability/KeeperMetrics.cs` (observability) — CREATE (D-07)

**Analog (EXACT — copy structure):** `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs`. A prior `KeeperMetrics` was deleted as orphaned in Phase 48, so this is a fresh file modeled on ProcessorMetrics' `IMeterFactory` pattern.

**Pattern to copy** (`ProcessorMetrics.cs:25-49`):
```csharp
using System.Diagnostics.Metrics;
namespace Keeper.Observability;

public sealed class KeeperMetrics
{
    public const string MeterName = "Keeper";   // MUST equal the AddMeter("Keeper") registration
    public Counter<long> ReinjectDropped { get; }

    public KeeperMetrics(IMeterFactory meterFactory)   // IMeterFactory, NEVER a static Meter
    {
        var meter = meterFactory.Create(MeterName);
        ReinjectDropped = meter.CreateCounter<long>("keeper_reinject_dropped");  // snake_case, NO suffix
    }
}
```
**Conventions (from ProcessorMetrics doc-comment, lines 5-24):** `IMeterFactory`-built (never `static Meter` — leaks across the hermetic test process); snake_case name with NO Prometheus suffix (the collector's `add_metric_suffixes` appends `_total` itself — matches `processor_dispatch_deduped`, `ProcessorMetrics.cs:47`). Register the meter name via `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName))` (mirror `BaseProcessorServiceCollectionExtensions.cs:152-153`). Inject into `ReinjectConsumer` and increment at the by-design absent drop (D-07).

---

### `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` (test) — CREATE (KEEP-02)

**Analog:** `DeleteConsumerFacts.cs` (whole file — the consumer-fact skeleton) + `RecoveryTestKit.cs` (the Db/Mux/CapturingSendProvider doubles). `CapturingSendProvider` ALREADY captures `StepCompleted` sends (`RecoveryTestKit.cs:75-76`), so the kit needs no change for INJECT.

**Skeleton to copy** (`DeleteConsumerFacts.cs:16-42`):
```csharp
[Fact]
[Trait("Phase", "52")]
public async Task Inject_writes_sends_completed_deletes_source_in_order()
{
    var ct = TestContext.Current.CancellationToken;
    var db = RecoveryTestKit.Db();
    var send = new RecoveryTestKit.CapturingSendProvider();
    var consumer = new InjectConsumer(
        RecoveryTestKit.Mux(db), send, RecoveryTestKit.OpenGate(),     // OpenGate() drops if base ctor loses gate (see base section)
        RecoveryTestKit.Retry(), RecoveryTestKit.Recovery());

    var m = new KeeperInject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
    {
        CorrelationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(),
        EntryId = Guid.NewGuid(), Data = "{\"out\":1}", DeleteEntryId = Guid.NewGuid(),
    };
    var ctx = Substitute.For<ConsumeContext<KeeperInject>>();
    ctx.Message.Returns(m); ctx.CancellationToken.Returns(ct);

    await consumer.Consume(ctx);

    // 1) write L2[entryId]=data
    await db.Received(1).StringSetAsync(
        (RedisKey)L2ProjectionKeys.ExecutionData(m.EntryId), (RedisValue)m.Data,
        Arg.Any<TimeSpan?>(), Arg.Any<bool>(), Arg.Any<When>(), Arg.Any<CommandFlags>());
    // 2) send StepCompleted → orchestrator-result
    var (uri, msg) = Assert.Single(send.Sent);
    Assert.Equal(new Uri($"queue:{OrchestratorQueues.Result}"), uri);
    var completed = Assert.IsType<StepCompleted>(msg);
    Assert.Equal(m.EntryId, completed.EntryId);
    // 3) delete L2[deleteEntryId]
    await db.Received(1).KeyDeleteAsync(
        (RedisKey)L2ProjectionKeys.ExecutionData(m.DeleteEntryId), Arg.Any<CommandFlags>());
}
```
For the strict ORDER assertion (Pitfall 5), use NSubstitute `Received.InOrder(() => { ... })` over the three calls. Note `RecoveryTestKit.Db()` stubs `StringSetAsync`/`KeyDeleteAsync` to succeed (`RecoveryTestKit.cs:55-59`) so `Received()` works.

---

### `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` (test) — MODIFY (KEEP-01 / D-06 / D-07)

**Analog:** itself. KEEP the present-path fact (`ReinjectConsumerFacts.cs:26-60`) verbatim. REWRITE the absent fact (`ReinjectConsumerFacts.cs:62-86`).

**Current absent fact to replace** (`ReinjectConsumerFacts.cs:84-85`):
```csharp
await Assert.ThrowsAsync<RecoveryDataGoneException>(() => consumer.Consume(Ctx(m, ct)));
Assert.Empty(send.Sent);
```
**New absent fact (D-06/D-07)** — assert NO throw, NO send, counter incremented:
```csharp
// StringLengthAsync defaults to 0 (RecoveryTestKit.Db) → absent/empty → DROP
await consumer.Consume(Ctx(m, ct));      // no throw
Assert.Empty(send.Sent);                 // nothing re-injected
// assert keeper_reinject_dropped incremented — via a MeterListener over the "Keeper" meter,
// OR inject a KeeperMetrics built from a real IMeterFactory and observe the counter (see ProcessorMetricsFacts).
```
The fact now needs a `KeeperMetrics` (real `IMeterFactory`, per `ProcessorMetricsFacts.cs:26-31`) + an `ILogger` (`NullLogger<ReinjectConsumer>.Instance`) passed to the new `ReinjectConsumer` ctor params. Drop the `[Trait("Phase","46")]` → `[Trait("Phase","52")]` on the rewritten fact.

---

### pause/accumulate fact + SustainedOutage fact (test, integration) — CREATE (KEEP-04 / KEEP-05)

**Analog:** `RecoveryDeadLetterFacts.cs` and `KeeperDlqConsolidationTests.cs` — the in-memory `ITestHarness` builder pattern.

**Harness builder shape to copy** (`KeeperDlqConsolidationTests.cs:68-93` / `RecoveryDeadLetterFacts.cs:59-89`):
```csharp
new ServiceCollection()
    .AddLogging()
    .AddSingleton(/* mux / gate / options doubles */)
    .AddMassTransitTestHarness(x =>
    {
        x.AddConsumer<ReinjectConsumer>();   // + InjectConsumer / DeleteConsumer as needed
        // consolidated sink so a moved ConsolidatedFault is observable:
        x.AddHandler((ConsumeContext<ConsolidatedFault> _) => Task.CompletedTask)
            .Endpoint(e => e.Name = ConsolidatedErrorTransportFilter.Dlq1);
        x.AddConfigureEndpointsCallback((context, name, e) =>
        {
            e.UseMessageRetry(r => r.Immediate(retryLimit));
            e.ConfigureError(ep => { ep.UseFilter(new GenerateFaultFilter());
                                     ep.UseFilter(new ConsolidatedErrorTransportFilter()); });
        });
        x.UsingInMemory((ctx, cfg) => cfg.ConfigureEndpoints(ctx));
    })
    .BuildServiceProvider(true);
```
**Assertion idioms to copy** (`RecoveryDeadLetterFacts.cs:97-120`): `harness.Start()` → `harness.Bus.Publish(msg, ct)` → `harness.Consumed.Any<T>(ct)` / `harness.Published.Any<Fault<T>>(ct)` in a `try/finally { harness.Stop(ct); }`.

- **pause/accumulate (KEEP-04):** start the harness, Stop the recovery endpoint handle (the `ITestHarness` connected-endpoint Stop/Start), publish, assert NOT consumed; Start, assert consumed/drained. The endpoint Stop/Start surface mirrors the production `ConnectReceiveEndpoint` handle (52-RESEARCH Pattern 1).
- **SustainedOutage (KEEP-05):** build the harness in `SustainedOutage` mode (no consolidated error move / requeue), publish a faulting op, assert NO `ConsolidatedFault` consumed and the message is redelivered (the inverse of `RecoveryDeadLetterFacts` / `Dlq1_Consolidated`).
- **Adapt `RecoveryDeadLetterFacts.DataGone_reinject_faults_and_routes_to_dead_letter`** (line 94): the data-gone→dead-letter assertion now contradicts D-06 (data-gone is a DROP). Repurpose it to op-exhaustion→dead-letter for the Dlq1 mode (a Redis-EXCEPTION on the read, NOT absent/empty), keeping the consolidated-route assertion.

---

### `tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs` (test) — MODIFY (KEEP-04 driver)

**Analog:** itself. EXTEND the existing `ScriptedRedis`-driven edge facts with a fake endpoint handle and assert Stop on the unhealthy edge / Start on the healthy edge (mirroring the existing `bus.Received(1).Publish(...PauseAll/ResumeAll...)` assertions at lines 120-121, 175-176, 192-193, 210-211). The `NewLoop` factory (line 94) and `RunScriptThenStop` helper (line 98) gain the fake handle param; preserve the `prevHealthy=null` first-tick semantics (the "first healthy tick → 1 ResumeAll" assertion, line 174 — and now → 1 Start).

---

## No Analog Found

None. Every file maps to an in-repo analog. The single MEDIUM-confidence item is the `ConnectReceiveEndpoint` connect-callback + handle plumbing (OQ-1) — no existing repo call site uses `ConnectReceiveEndpoint`, so its closest guide is the static `AddConsumer` block (`Program.cs:40-50`) plus the API-cited shape in 52-RESEARCH Pattern 1 (lines 162-182). The exact 8.5.5 "requeue, no dead-letter" call for SustainedOutage (OQ-2 / Assumption A1) also needs in-phase assembly verification.

---

## Metadata

**Analog search scope:** `src/Keeper/**`, `src/BaseProcessor.Core/Observability/**`, `src/BaseProcessor.Core/Processing/**`, `src/Messaging.Contracts/**`, `tests/BaseApi.Tests/Keeper/**`, `tests/BaseApi.Tests/Processor/**`
**Files read in full:** `RecoveryConsumerBase.cs`, `ReinjectConsumer.cs`, `InjectConsumer.cs`, `DeleteConsumer.cs`, `ReinjectConsumerDefinition.cs`, `RecoveryOptions.cs`, `BitHealthLoop.cs`, `Program.cs` (Keeper), `RecoveryDataGoneException.cs`, `L2HealthGate.cs`, `ProcessorMetrics.cs`, `KeeperInject.cs`, `KeeperReinject.cs`, `StepCompleted.cs`, `EntryStepDispatch.cs`, `KeeperQueues.cs`, `OrchestratorQueues.cs`, `RecoveryTestKit.cs`, `ReinjectConsumerFacts.cs`, `DeleteConsumerFacts.cs`, `RecoveryDeadLetterFacts.cs`, `RecoveryGateWaitFacts.cs`, `KeeperDlqConsolidationTests.cs`, `BitHealthLoopTests.cs`, `ProcessorMetricsFacts.cs`
**Targeted reads:** `BaseProcessorServiceCollectionExtensions.cs:140-159` (AddMeter site), `L2ProjectionKeys.cs:42` (ExecutionData), `ProcessorPipeline.cs:315` (orchestrator-result URI — via grep)
**Pattern extraction date:** 2026-06-11
