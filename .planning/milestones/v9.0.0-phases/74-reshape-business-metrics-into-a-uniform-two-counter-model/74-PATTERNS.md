# Phase 74: Reshape Business Metrics to a Uniform Two-Counter Model — Pattern Map

**Mapped:** 2026-06-18
**Files analyzed:** 13 production touchpoints + 13 test files (26 total)
**Analogs found:** all NEW constructs have an in-repo proven shape (no greenfield patterns)

> **DRIFT ALERT (read first).** The SPEC/CONTEXT line numbers were written 2026-06-17 against the
> pre–phase-73 tree. Phase 71 (orchestrator two-consumer reshape) and Phase 72 (always-write-out:)
> have since SHIPPED and moved several increment sites to entirely different files. **Every
> SPEC line-number citation below is stale.** The biggest structural surprises:
> 1. `OrchestratorResultPipeline.cs` (SPEC §Background, "`:298`") **does not exist** — its
>    `SendDispatch`/`SendResult`/`SendKeeper` sends were split across **`OrchestratorPrePipeline.cs`**
>    (fan-out + keeper) and **`RelocateTail.cs`** (the actual `EntryStepDispatch` dispatch).
> 2. `processor_result_sent` (SPEC "`ProcessorPipeline.cs:382`") **moved to `OutputTail.cs:129`** in
>    Phase 72 — `ProcessorPipeline` is only 238 lines and no longer sends results.
> 3. The keeper has **6 recovery consumers, not 5** — SPEC/CONTEXT undercount by one
>    (`OrchestratorDeleteConsumer` exists alongside `DeleteConsumer`). **2 are non-sending** (both
>    Delete variants), **4 are sending**.
> 4. The keeper consumer filenames in CONTEXT are wrong: there is **no** `ProcessorReinjectConsumer` /
>    `ProcessorInjectConsumer`. The processor-side ones are `ReinjectConsumer.cs` / `InjectConsumer.cs`.

---

## File Classification

| File to modify | Role | Data flow | Closest in-repo analog | Match |
|----------------|------|-----------|------------------------|-------|
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | meter-class | — | ProcessorMetrics.cs (sibling) | exact |
| `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` | meter-class | — | OrchestratorMetrics.cs (sibling) | exact |
| `src/Keeper/Observability/KeeperMetrics.cs` | meter-class | — | ProcessorMetrics.cs (its own doc says "modeled EXACTLY on") | exact |
| `src/Orchestrator/Dispatch/StepDispatcher.cs` | increment-site (sent) | request-response | self (current `DispatchSent.Add`) | exact |
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` | increment-site (sent ×N fan-out + keeper) | event-driven | self (`StepUnresolved.Add` already here) | exact |
| `src/Orchestrator/Dispatch/RelocateTail.cs` | increment-site (sent via dispatcher / INJECT keeper) | event-driven | StepDispatcher dispatch | role-match |
| `src/Orchestrator/Consumers/TypedResultConsumer.cs` | increment-site (consumed) | event-driven | self (current `ResultConsumed.Add`) | exact |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | increment-site (consumed) | event-driven | self (current `DispatchConsumed.Add`) | exact |
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | increment-site (sent, drop `outcome`) | event-driven | self (current `ResultSent.Add`) | exact |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs` | increment-site (consumed choke + `CountSent` helper) | event-driven | TypedResultConsumer consume-top increment | role-match |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | increment-site (sent) | event-driven | self (already injects KeeperMetrics) | exact |
| `src/Keeper/Recovery/InjectConsumer.cs` | increment-site (sent) | event-driven | ReinjectConsumer (KeeperMetrics injection) | role-match |
| `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs` | increment-site (sent) | event-driven | self (already injects KeeperMetrics) | exact |
| `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` | increment-site (sent) | event-driven | ReinjectConsumer (KeeperMetrics injection) | role-match |
| `src/Keeper/Recovery/DeleteConsumer.cs` | (no change — sends nothing) | — | — | — |
| `src/Keeper/Recovery/OrchestratorDeleteConsumer.cs` | (no change — sends nothing) | — | — | — |
| `src/Keeper/Health/BitHealthLoop.cs` | increment-site (`keeper_l2_probe`, label-less) | batch/loop | ProcessorLivenessHeartbeat loop | role-match |
| `tests/.../Analysis/PromCounterSnapshot.cs` | analyzer DTO | — | self | exact |
| `tests/.../Analysis/PassFailEngine.cs` | analyzer logic | — | self | exact |
| `tests/.../Observability/AnalyzerE2ETests.cs` | analyzer query-builder (RealStack) | — | self | exact |
| (10 metric test files — see §Test Inventory) | test | — | — | — |

---

## Pattern Assignments

### `OrchestratorMetrics.cs` (meter-class) — current shape (lines 22-59)

Current members: `DispatchSent`, `ResultConsumed`, `ResultDeduped` (dormant), `StepUnresolved`.

```csharp
public const string MeterName = "Orchestrator";
public Counter<long> DispatchSent   { get; }   // line 28 — REMOVE / replace
public Counter<long> ResultConsumed { get; }   // line 31 — REMOVE / replace
public Counter<long> ResultDeduped  { get; }   // line 42 — REMOVE (REQ-1, acceptance "no ResultDeduped member")
public Counter<long> StepUnresolved { get; }   // line 49 — KEEP (Phase 72, out of scope)

public OrchestratorMetrics(IMeterFactory meterFactory)              // line 51
{
    var meter = meterFactory.Create(MeterName);
    DispatchSent   = meter.CreateCounter<long>("orchestrator_dispatch_sent");    // 54 → MessagesSent  = "orchestrator_messages_sent"
    ResultConsumed = meter.CreateCounter<long>("orchestrator_result_consumed");  // 55 → MessagesConsumed = "orchestrator_messages_consumed"
    ResultDeduped  = meter.CreateCounter<long>("orchestrator_result_deduped");   // 56 → DELETE
    StepUnresolved = meter.CreateCounter<long>("orchestrator_step_unresolved");  // 57 → KEEP unchanged
}
```
**Target:** add `MessagesConsumed` (`orchestrator_messages_consumed`) + `MessagesSent`
(`orchestrator_messages_sent`); delete `DispatchSent`/`ResultConsumed`/`ResultDeduped`; keep
`StepUnresolved`. **Drift:** SPEC line cites are accurate for THIS file (no phase-73 drift here).

### `ProcessorMetrics.cs` (meter-class) — current shape (lines 25-56)

Current members: `DispatchConsumed`, `ResultSent`, `DispatchDeduped`, `SpawnDropped`.

```csharp
public const string MeterName = "BaseProcessor";
public Counter<long> DispatchConsumed { get; }   // 31 → MessagesConsumed = "processor_messages_consumed"
public Counter<long> ResultSent       { get; }   // 34 → MessagesSent     = "processor_messages_sent"
public Counter<long> DispatchDeduped  { get; }   // 40 → DELETE (REQ-2, "no DispatchDeduped member")
public Counter<long> SpawnDropped     { get; }   // 46 → KEEP (IN-03, out of scope)
// ctor lines 48-55 build all four via meter.CreateCounter<long>(...)
```
**Drift:** accurate for THIS file. **Note:** `SpawnDropped` (`processor_spawn_dropped`) is NOT in
SPEC's removal list — it must survive untouched.

### `KeeperMetrics.cs` (meter-class) — current shape (lines 16-31)

```csharp
public const string MeterName = "Keeper";
public Counter<long> ReinjectDropped { get; }                  // 24 → DELETE (REQ-3)
public KeeperMetrics(IMeterFactory meterFactory)               // 26
{
    var meter = meterFactory.Create(MeterName);
    ReinjectDropped = meter.CreateCounter<long>("keeper_reinject_dropped");   // 29 → DELETE
}
```
**Target — add THREE counters** (analog: the sibling ctor shape above):
`MessagesConsumed` (`keeper_messages_consumed`), `MessagesSent` (`keeper_messages_sent`),
`L2Probe` (`keeper_l2_probe`); delete `ReinjectDropped`.

---

### `StepDispatcher.cs` (increment-site, sent) — current (lines 33-41)

```csharp
var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
await endpoint.Send(msg, ct);
// METRIC-04 (D-04): SENT = count-AFTER-Send …
metrics.DispatchSent.Add(1, new KeyValuePair<string, object?>("ProcessorId", processorId.ToString("D")));   // line 41
```
**Drift:** SPEC "`StepDispatcher.cs:41`" is **ACCURATE** (still line 41).
**Target:** `metrics.MessagesSent.Add(1, ("workflowId", workflowId…), ("processorId", processorId…))`.
Both label values are in-hand as method params (`workflowId`, `processorId`). D-05: `processorId` =
the dispatch recipient = this `processorId` (the target queue's processor). **Note:** this method is
called by BOTH `RelocateTail.RunAsync` (post-process dispatch) AND the keeper `InjectConsumer`'s
inline dispatch path? — NO: the keeper builds its own `EntryStepDispatch` and does not call
`StepDispatcher` (cross-assembly firewall). So this one increment covers the orchestrator's direct
forward dispatch + the RelocateTail dispatch (both call `DispatchAsync`).

### `OrchestratorPrePipeline.cs` (increment-site, sent ×N + keeper) — current sends

This file replaces the SPEC's nonexistent `OrchestratorResultPipeline.cs`. It contains **two**
outbound-send classes of site:
- **Fan-out** (lines 125-137): `post.Send` once **per match** in `foreach (var (stepId, step) in selection.Matches)`.
  D-02 fan-out rule → increment **once per successful `post.Send`** inside the loop (after the
  `if (!sent.Succeeded) throw` guard). D-06: recipient is the `orchestrator-result-post` queue (not a
  processor), so use the **producing processor's** id — available as `m.ProcessorId` (the inbound
  `IStepResult`); `workflowId` = `m.WorkflowId`.
- **SendKeeper** (lines 164-173, called from line 112 REINJECT and line 159 DELETE escalation):
  `ep.Send` to `queue:{KeeperQueues.Recovery}`. D-06: keeper escalation recipient is not a processor →
  use the producing processor id. Labels available from the `IKeeperRecoverable msg` param
  (`msg.WorkflowId`/`msg.ProcessorId`) or the captured `m`.

```csharp
// line 112  read-fault → REINJECT escalation
if (!read.Succeeded) { await SendKeeper(BuildReinject(m, outcome, messageId), limit, ct); return; }
// lines 126-137  FAN-OUT loop — increment per send here:
foreach (var (stepId, step) in selection.Matches)
{
    var handoff = new NextStepHandoff(...);
    var sent = await RetryLoop.ExecuteAsync(
        async () => { await post.Send((object)handoff, CancellationToken.None); return true; }, limit, ct);
    if (!sent.Succeeded) throw sent.Error!;
    // ← NEW: metrics.MessagesSent.Add(1, ("workflowId", m.WorkflowId…), ("processorId", m.ProcessorId…));
}
// line 159  delete-exhaust → DELETE escalation
if (!del.Succeeded) { await SendKeeper(BuildDelete(m), limit, ct); return; }
```
**Plumbing note (Claude's Discretion / CONTEXT D-15):** `OrchestratorMetrics` is **already
constructor-injected** into `OrchestratorPrePipeline` (line 66 — for `StepUnresolved`). No new ctor
param needed. The cleanest place to count the keeper escalations is **inside `SendKeeper`** (lines
164-173) after the `if (!sent.Succeeded) throw` — but `SendKeeper` takes only `IKeeperRecoverable msg`,
which exposes `WorkflowId`/`ProcessorId` (see §Label Sources), so the labels are available there.

### `RelocateTail.cs` (increment-site) — current sends (lines 45-98)

- The actual next-step `EntryStepDispatch` goes out via `dispatcher.DispatchAsync(...)` (line 68) →
  **counted inside `StepDispatcher` already** (do NOT double-count here).
- `SendKeeper` (lines 89-98, called line 58 INJECT on write-exhaust): `ep.Send` to keeper recovery →
  needs a `keeper`-escalation sent increment. Labels from `BuildInject`'s `h` (`h.WorkflowId`,
  `h.ProcessorId`).
**Plumbing note:** `RelocateTail` does NOT currently inject `OrchestratorMetrics` (ctor lines 28-34).
If the INJECT-escalation send must count (acceptance: "keeper escalation" sent), add `OrchestratorMetrics`
to its primary ctor — analog injection shape is `OrchestratorPrePipeline`'s ctor (line 66).

### `TypedResultConsumer.cs` (increment-site, consumed) — current (lines 46-53)

```csharp
public async Task Consume(ConsumeContext<TMessage> context)
{
    var m = context.Message;
    // METRIC-04: count EVERY consumed result at the TOP …
    metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));   // line 53
    await pipeline.RunAsync(m, Outcome, context.MessageId ?? Guid.Empty, context.CancellationToken);
}
```
**Drift:** SPEC "`TypedResultConsumer.cs:62`" is STALE — the increment is now **line 53** (Phase 71
thin-shell reshape shrank the file). **Target:** `metrics.MessagesConsumed.Add(1, ("workflowId",
m.WorkflowId…), ("processorId", m.ProcessorId…))`. `m` is `IStepResult` which is `IExecutionCorrelated`
→ both ids present.

### `EntryStepDispatchConsumer.cs` (increment-site, consumed) — current (lines 31-44)

```csharp
metrics.DispatchConsumed.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));   // lines 37-38
```
**Drift:** SPEC "`EntryStepDispatchConsumer.cs:37`" is **ACCURATE** (line 37-38).
**Target:** `metrics.MessagesConsumed.Add(1, ("workflowId", ctx.Message.WorkflowId…), ("processorId",
context.Id!.Value…))`. `workflowId` comes from `ctx.Message.WorkflowId` (the `EntryStepDispatch`, which
is `IExecutionCorrelated`); `processorId` is the consuming processor's own `context.Id` (already used).

### `OutputTail.cs` (increment-site, sent — DROP `outcome`) — current (lines 116-143)

**This is the real `processor_result_sent` site** (SPEC's "`ProcessorPipeline.cs:382`" moved here in
Phase 72). The `outcome` label is computed by `ResultOutcome(result)` (lines 134-143).

```csharp
private async Task SendResult(IStepResult result, int limit, CancellationToken ct)
{
    var sent = await RetryLoop.ExecuteAsync(async () => { … ep.Send((object)result …); return true; }, limit, ct);
    if (!sent.Succeeded) throw sent.Error!;
    metrics.ResultSent.Add(1,                                                                   // line 129
        new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
        new KeyValuePair<string, object?>("outcome", ResultOutcome(result)));                   // line 131 — DROP
}
private static string ResultOutcome(IStepResult result) => result switch { … };                // 134-143 — DELETE the whole helper
```
**Target:** `metrics.MessagesSent.Add(1, ("workflowId", result.WorkflowId…), ("processorId",
context.Id!.Value…))`. Drop the `outcome` tag AND delete the now-unused `ResultOutcome` helper
(0-warning build → unused private method would warn). D-05/D-06: `processorId` = this processor's own
id (the producing processor); `workflowId` from `result.WorkflowId` (`IStepResult : IExecutionCorrelated`).

---

### Keeper consumers — the 6-consumer hierarchy (SPEC says 5; actual = 6)

All extend `RecoveryConsumerBase<TMessage>`; all funnel through ITS `Consume` (the choke point).

| Consumer | Message | Sends? | Currently injects KeeperMetrics? |
|----------|---------|--------|----------------------------------|
| `ReinjectConsumer` | `KeeperReinject` | YES (dispatch to `queue:{procId:D}`) | **YES** (for `ReinjectDropped`) |
| `InjectConsumer` | `KeeperInject` | YES (Step* to `orchestrator-result`) | no |
| `OrchestratorReinjectConsumer` | `OrchestratorReinject` | YES (Step* to `orchestrator-result`) | **YES** (for `ReinjectDropped`) |
| `OrchestratorInjectConsumer` | `OrchestratorInject` | YES (dispatch to `queue:{procId:D}`) | no |
| `DeleteConsumer` | `KeeperDelete` | **NO** | no |
| `OrchestratorDeleteConsumer` | `OrchestratorDelete` | **NO** | no |

#### `RecoveryConsumerBase.cs` (choke point + `CountSent` helper) — current (lines 22-53)

```csharp
public abstract class RecoveryConsumerBase<TMessage>(
    IConnectionMultiplexer redis,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions) : IConsumer<TMessage>           // ← ADD KeeperMetrics param (D-10)
    where TMessage : class, IKeeperRecoverable
{
    protected IDatabase Db { get; } = redis.GetDatabase();
    protected ISendEndpointProvider Send => sendProvider;
    protected int RetryLimit => retryOptions.Value.Limit;

    public Task Consume(ConsumeContext<TMessage> context)               // line 34 — THE CHOKE POINT
        => HandleAsync(context.Message, context.CancellationToken);
    // ↑ D-08: count keeper_messages_consumed ONCE here, before HandleAsync.
    //   Labels from context.Message (IKeeperRecoverable → WorkflowId/ProcessorId).

    protected abstract Task HandleAsync(TMessage m, CancellationToken ct);   // line 39
    protected async Task<T> Guard<T>(Func<Task<T>> op, CancellationToken ct) { … }   // 43
    protected Task Guard(Func<Task> op, CancellationToken ct) { … }                   // 51
    // ↑ D-09: ADD `protected void CountSent(Guid workflowId, Guid processorId)` here.
}
```
**D-10 injection — every subclass MUST forward the new ctor arg.** Two subclasses already pass extra
deps to the base ctor; mirror that exact `: RecoveryConsumerBase<T>(redis, sendProvider, retryOptions)`
base-call shape, appending `metrics`. Pattern to copy (a subclass that already has KeeperMetrics in
hand): `ReinjectConsumer.cs:22-26`.

#### `ReinjectConsumer.cs` (sent) — current send (lines 36-58)

CRITICAL ORDER: there is an **early `return` drop path** (lines 36-41) BEFORE the send. `CountSent`
MUST be called only AFTER the real `ep.Send` succeeds (line 58), never on the drop branch.

```csharp
if (!present) { metrics.ReinjectDropped.Add(1); logger.LogWarning(...); return; }   // 36-41 — drop counter DELETED (REQ-3)
var dispatch = new EntryStepDispatch(...);
var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}")), ct);
await Guard(() => ep.Send(dispatch, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);   // 58
// ← NEW: CountSent(m.WorkflowId, m.ProcessorId);
```
**Drift:** SPEC "`ProcessorReinjectConsumer.cs:38`" wrong filename AND line — real file is
`ReinjectConsumer.cs`, the `ReinjectDropped.Add` is **line 38** (coincidentally matches), inside the
drop branch. After this phase the `KeeperMetrics`/`ILogger` ctor deps stay (now for `CountSent` +
the surviving warning log) — but `ReinjectDropped` is gone, so the `metrics.ReinjectDropped.Add(1)`
line is **deleted** (keep the `logger.LogWarning` drop line per the by-design-drop log).

#### `OrchestratorReinjectConsumer.cs` (sent) — current (lines 31-67)

Same drop-then-send shape: `metrics.ReinjectDropped.Add(1)` at **line 42** (drop branch, DELETE), real
send at **line 66**. Add `CountSent(m.WorkflowId, m.ProcessorId)` after line 66.

#### `InjectConsumer.cs` (sent) — current send (line 57)

No drop path; one unconditional Step* send. **Does NOT currently inject KeeperMetrics** (ctor lines
26-29 take `redis, sendProvider, retryOptions, recoveryOptions`). After the base ctor gains the
`metrics` param, this subclass's base-call must forward it. Add `CountSent(dr.WorkflowId,
dr.ProcessorId)` after the `await Guard(() => ep.Send(...))` at line 57. (`dr` = `m.DataResult`, an
`IStepResult` → ids; or use `m.WorkflowId`/`m.ProcessorId` from the `IKeeperRecoverable` envelope.)

#### `OrchestratorInjectConsumer.cs` (sent) — current send (line 57)

One dispatch send (`ep.Send(dispatch, …)`). **Does NOT currently inject KeeperMetrics** (ctor lines
30-33). D-05: this is an `OrchestratorInject` → recipient IS a processor → `processorId` =
`h.ProcessorId` / `m.ProcessorId` (the `NextProcessorId` per D-05). Add `CountSent(m.WorkflowId,
m.ProcessorId)` after line 57.

#### `DeleteConsumer.cs` / `OrchestratorDeleteConsumer.cs` — NO send increment

Confirmed: `DeleteConsumer.cs:21-22` is a single `KeyDeleteAsync` Guard, no send.
`OrchestratorDeleteConsumer.cs:21-22` likewise. They still pass through the base `Consume` choke point,
so `keeper_messages_consumed` counts them (D-08), but they make **no** `CountSent` call. They still must
forward the new base ctor `metrics` arg (compile requirement).

---

### `BitHealthLoop.cs` (`keeper_l2_probe`, label-less) — current (lines 21-95)

```csharp
public sealed class BitHealthLoop(
    L2ProbeRecovery probe, IL2HealthGate gate, IBus bus, RecoveryEndpointHandle endpointHandle,
    IOptions<ProbeOptions> opts, ILogger<BitHealthLoop> logger, TimeProvider clock,
    IKeeperLivenessState liveness) : BackgroundService                  // ← ADD KeeperMetrics param (D-10)
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var healthy = await probe.ProbeOnceAsync(stoppingToken);   // line 44 — THE TICK
            // ← NEW (D-11): metrics.L2Probe.Add(1);  — once per tick, no labels, regardless of healthy/unhealthy
            liveness.Update(clock.GetUtcNow().UtcDateTime);            // line 50 (existing unconditional-per-tick precedent)
            …
        }
    }
}
```
**Plumbing:** the `metrics.L2Probe.Add(1)` goes right after line 44 (or alongside the existing
unconditional `liveness.Update` at line 50 — same "every tick, outside the edge guard" placement the
file already documents). Add `KeeperMetrics metrics` to the primary ctor (line 21-29).
**Analog for an unconditional per-loop counter:** `ProcessorLivenessHeartbeat.cs` (same project family)
runs an identical `while + Task.Delay` loop — but the simplest in-repo proof is the existing
unconditional `liveness.Update` on every tick in THIS very loop.

---

### Analyzer rebinding

#### `PromCounterSnapshot.cs` — current fields (lines 23-58)

```csharp
public required double DispatchSentDelta { get; init; }            // 26  orchestrator_dispatch_sent_total
public required double ResultConsumedDelta { get; init; }          // 29  orchestrator_result_consumed_total
public required double DispatchConsumedDelta { get; init; }        // 32  processor_dispatch_consumed_total
public required double ResultSentCompletedDelta { get; init; }     // 35  processor_result_sent_total{outcome="completed"}  ← D-12 repoint to processor_messages_sent total
public required double KeeperReinjectDroppedDelta { get; init; }   // 38  keeper_reinject_dropped_total  ← REMOVE
public double? ResultDedupedDelta { get; init; }                   // 44  orchestrator_result_deduped_total  ← REMOVE (dormant)
public double? DispatchDedupedDelta { get; init; }                 // 50  processor_dispatch_deduped_total   ← REMOVE (dormant)
public IReadOnlyDictionary<string,double> NonCompletedOutcomes …   // 57  ← REMOVE (D-13, outcome label gone)
```
**Target (D-13):** rename the surviving deltas to the new metric names; **drop** the
`outcome`-keyed `NonCompletedOutcomes` field and the two dormant nullable dedup deltas and
`KeeperReinjectDroppedDelta`. Repoint `ResultSentCompletedDelta` → a `processor_messages_sent` total
(D-12: all-complete fixture ⇒ total == completed).

#### `PassFailEngine.cs` — current bindings to rework

- `TriggerCountFrom` (line 105) reads `prom.DispatchSentDelta` → repoint to the new
  `orchestrator_messages_sent` delta field.
- `promImpliedRuns = round(prom.DispatchSentDelta / 9)` (line 257) → repoint field name.
- `expectedResultConsumed = prom.DispatchSentDelta + spawnExtra` (line 296) and
  `spawnReconExcess = prom.ResultConsumedDelta - expectedResultConsumed` (line 297) → repoint field names.
- **`nonCompletedOutcomes = prom.NonCompletedOutcomes…` (line 280) + the WARNING block (281-287) →
  DELETE** (D-13 — the `outcome` split no longer exists).
- The `ResultSentCompletedDelta`-based "complete × 9" math note (lines 175-181 are in the TEST, not
  here) — in the engine, the close-gate "Expected = COMPLETE × 9" survives via the repointed
  `processor_messages_sent` total (D-12).

#### `AnalyzerE2ETests.cs` (RealStack) — the live PromQL builders (lines 539-601)

This is where the **literal Prometheus query strings** live and MUST be repointed:
```csharp
$"processor_result_sent_total{{outcome=\"{outcome}\"}}"            // 542  ← remove (outcome loop 539-543 deleted)
"orchestrator_dispatch_sent_total"                                 // 547  → orchestrator_messages_sent_total
"processor_result_sent_total{outcome=\"completed\"}"               // 551  → processor_messages_sent_total
// 594-601 the delta-subtraction block maps before/after fields → rename to match PromCounterSnapshot
```
RealStack-tagged (`Category=RealStack`), so excluded from the hermetic run, but **must still compile**.

---

## Shared Patterns

### Counter construction (IMeterFactory DI — the blessed pattern)
**Source:** `OrchestratorMetrics.cs:51-58` / `ProcessorMetrics.cs:48-55`
**Apply to:** all three meter classes' new counters.
```csharp
var meter = meterFactory.Create(MeterName);
MessagesConsumed = meter.CreateCounter<long>("{service}_messages_consumed");  // collector appends _total
MessagesSent     = meter.CreateCounter<long>("{service}_messages_sent");
```
Never a `static Meter`; the const `MeterName` is referenced by both `Create` and the `AddMeter(...)`
registration (unchanged this phase).

### Two-label increment (camelCase)
**Source (current PascalCase shape to convert):** `StepDispatcher.cs:41`, `TypedResultConsumer.cs:53`,
`EntryStepDispatchConsumer.cs:37-38`, `OutputTail.cs:129-131`.
**Source (the NEW camelCase + 2-label shape already in-repo):** `OrchestratorPrePipeline.cs:78` /
`:150` — `metrics.StepUnresolved.Add(1, new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")))`.
**Apply to:** every consumed/sent increment.
```csharp
metrics.MessagesSent.Add(1,
    new KeyValuePair<string, object?>("workflowId", workflowId.ToString("D")),
    new KeyValuePair<string, object?>("processorId", processorId.ToString("D")));
```

### Count-after-successful-send
**Source:** `StepDispatcher.cs:35-41` (increment is the statement AFTER `await endpoint.Send`),
`OutputTail.cs:127-129` (increment AFTER `if (!sent.Succeeded) throw`). A drop/throw skips the count.
**Apply to:** all `*_messages_sent` sites — D-02 (failed/exhausted send does not count); the keeper
drop-path early-returns (`ReinjectConsumer.cs:41`, `OrchestratorReinjectConsumer.cs:44`) must NOT count.

### KeeperMetrics ctor injection into a RecoveryConsumerBase subclass
**Source:** `ReinjectConsumer.cs:22-26` / `OrchestratorReinjectConsumer.cs:25-29` — primary ctor takes
`KeeperMetrics metrics` and forwards `redis, sendProvider, retryOptions` to the base.
**Apply to:** `InjectConsumer`, `OrchestratorInjectConsumer`, both Delete consumers (forward the new
base ctor arg), and `BitHealthLoop` (add `KeeperMetrics` to its primary ctor).
**DI registration:** `KeeperMetrics` is already a registered singleton (see
`tests/.../Keeper/RecoveryDeadLetterFacts.cs:53` `.AddSingleton<KeeperMetrics>()` and the keeper
`Program.cs`) — verify the keeper `Program.cs` registers it once; no new `AddMeter` needed (meter name
"Keeper" registration is in scope-unchanged territory).

### Label sources (no new plumbing — D-07)
- `IKeeperRecoverable` (`src/Messaging.Contracts/IKeeperRecoverable.cs:8-14`) exposes `WorkflowId` +
  `ProcessorId` → available at every keeper increment via `context.Message` / `m`.
- `IExecutionCorrelated` (`src/Messaging.Contracts/IExecutionCorrelated.cs:11-18`) exposes
  `WorkflowId` + `ProcessorId` → `IStepResult` and `EntryStepDispatch` both implement it, so the
  orchestrator/processor sites have both labels in-hand.

---

## No Analog Found

None. Every new construct (`keeper_messages_*`, `keeper_l2_probe`, the `CountSent` helper, the
choke-point consumed increment) has a proven in-repo shape listed above. The plan should copy from the
cited lines, not from RESEARCH.md.

---

## Consolidated File-Modification Table

| File | Role | Metric(s) involved | Action |
|------|------|--------------------|--------|
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | meter-class | `orchestrator_messages_consumed`, `orchestrator_messages_sent` (add); `orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `orchestrator_result_deduped` (remove) | rewrite |
| `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` | meter-class | `processor_messages_consumed`, `processor_messages_sent` (add); `processor_dispatch_consumed`, `processor_result_sent`, `processor_dispatch_deduped` (remove); `processor_spawn_dropped` (keep) | rewrite |
| `src/Keeper/Observability/KeeperMetrics.cs` | meter-class | `keeper_messages_consumed`, `keeper_messages_sent`, `keeper_l2_probe` (add); `keeper_reinject_dropped` (remove) | rewrite |
| `src/Orchestrator/Dispatch/StepDispatcher.cs` | increment-site | `orchestrator_messages_sent` | rebind `.Add` (line 41) + camelCase 2-label |
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` | increment-site | `orchestrator_messages_sent` (fan-out ×N + keeper escalations) | add `.Add` in fan-out loop (≈126-137) + in `SendKeeper` (164-173) |
| `src/Orchestrator/Dispatch/RelocateTail.cs` | increment-site | `orchestrator_messages_sent` (INJECT keeper escalation) | inject `OrchestratorMetrics` + add `.Add` in `SendKeeper` (89-98) |
| `src/Orchestrator/Consumers/TypedResultConsumer.cs` | increment-site | `orchestrator_messages_consumed` | rebind `.Add` (line 53) + camelCase 2-label |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` | increment-site | `processor_messages_consumed` | rebind `.Add` (line 37) + camelCase 2-label |
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | increment-site | `processor_messages_sent` (drop `outcome`) | rebind `.Add` (129-131) + delete `ResultOutcome` helper (134-143) |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs` | increment-site | `keeper_messages_consumed` (Consume choke), `CountSent` helper | add `KeeperMetrics` ctor param + count in `Consume` (34) + add `CountSent` |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | increment-site | `keeper_messages_sent`; remove `keeper_reinject_dropped` | delete drop `.Add` (38) + add `CountSent` after send (58) |
| `src/Keeper/Recovery/InjectConsumer.cs` | increment-site | `keeper_messages_sent` | forward base ctor arg + `CountSent` after send (57) |
| `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs` | increment-site | `keeper_messages_sent`; remove `keeper_reinject_dropped` | delete drop `.Add` (42) + add `CountSent` after send (66) |
| `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` | increment-site | `keeper_messages_sent` | forward base ctor arg + `CountSent` after send (57) |
| `src/Keeper/Recovery/DeleteConsumer.cs` | (compile-only) | — | forward base ctor `metrics` arg only |
| `src/Keeper/Recovery/OrchestratorDeleteConsumer.cs` | (compile-only) | — | forward base ctor `metrics` arg only |
| `src/Keeper/Health/BitHealthLoop.cs` | increment-site | `keeper_l2_probe` | inject `KeeperMetrics` + `.Add(1)` per tick (after 44) |
| `tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs` | analyzer | new field names; drop `NonCompletedOutcomes` + dedup + reinject-dropped fields | rename/remove |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` | analyzer | repoint `DispatchSentDelta`/`ResultConsumedDelta`/`ResultSentCompletedDelta`; delete `outcome` WARNING (280-287) | rework |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | analyzer (RealStack) | live PromQL strings (542/547/551/594-601); delete `outcome` loop | rework query builders |
| (10 metric test files — below) | test | — | rename-in-place + absence asserts (D-14) |

---

## Test Inventory (D-14 migration target — exact literals)

**13 test files reference the old metric names / counter members / analyzer fields.** Hermetic vs
RealStack matters: RealStack files are excluded from the hermetic suite but must still compile.

| # | File | Category | Old literals referenced | Migration |
|---|------|----------|-------------------------|-----------|
| 1 | `tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs` | hermetic | `metrics.DispatchSent` (33), `metrics.ResultConsumed` (34); keeps `StepUnresolved` (35) | rename to `MessagesSent`/`MessagesConsumed` |
| 2 | `tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs` | hermetic | `metrics.DispatchConsumed` (33), `metrics.ResultSent` (34), `metrics.DispatchDeduped` (35); keeps `SpawnDropped` (36) | rename + DROP the `DispatchDeduped` assert |
| 3 | `tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs` | hermetic | `processor_dispatch_deduped` + `orchestrator_result_deduped` (doc); `metrics.DispatchDeduped` (49,93,100,107), `metrics.ResultDeduped` (61,94,101,108); `"ProcessorId"` tag (106) | this entire test targets the REMOVED dedup counters → **rewrite as absence asserts** (no `DispatchDeduped`/`ResultDeduped` member) or repurpose |
| 4 | `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` | hermetic | `keeper_reinject_dropped` (19,99), `KeeperMetrics` (90,93), `Interlocked.Read(ref dropped)==1` (113) | the drop-counter assert is gone → assert NO drop counter + assert `keeper_messages_sent` on the success path; the by-design-drop now asserts ack+no-send only |
| 5 | `tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs` | hermetic | `keeper_reinject_dropped` (119), `KeeperMetrics` (113) | same as #4 |
| 6 | `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs` | hermetic (kit) | `KeeperMetrics.Metrics()` factory (38-45) | keep (KeeperMetrics survives); ensure new ctor + counters constructible |
| 7 | `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` | hermetic | `.AddSingleton<KeeperMetrics>()` (53) | keep — verify DI still satisfies the new ctor for the 4 senders + base |
| 8 | `tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs` | hermetic (DTO) | all old delta field names + `NonCompletedOutcomes` (see §Analyzer) | rename fields / remove dropped ones |
| 9 | `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` | hermetic | `DispatchSentDelta`/`ResultConsumedDelta`/`DispatchConsumedDelta`/`ResultSentCompletedDelta`/`KeeperReinjectDroppedDelta`/`ResultDedupedDelta`/`DispatchDedupedDelta` (55-61,132-133,159-160,181,196-197,224-225,255-257) | repoint to new field names; drop the dedup/reinject/outcome fixtures |
| 10 | `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs` | hermetic | same snapshot field set (72-78) | repoint field names |
| 11 | `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` | hermetic (DTO) | `DispatchSentDelta`/`ExpectedResultConsumed` — **doc-comment + `ExpectedResultConsumed` field** (23,37,108,118,124,127,130,138) | mostly doc; `ExpectedResultConsumed` stays; verify no removed-field reference |
| 12 | `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | **RealStack** | live PromQL: `processor_result_sent_total{outcome=…}` (542,551), `orchestrator_dispatch_sent_total` (547), delta block (594-601) | rework query builders (must compile) |
| 13 | `tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs` | **RealStack** | `orchestrator_dispatch_sent_total` (124,144,204), `orchestrator_result_consumed_total` (128), `processor_dispatch_consumed_total` (134,145,185), `processor_result_sent_total` (138), `outcome` assert + **`NO workflowId` cardinality guard** (151-152,231-256) | rename queries; **INVERT** the cardinality guard (new model REQUIRES `workflowId`); drop the `outcome` assert |
| 14 | `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` | **RealStack** | `keeper_reinject_dropped` (doc 38, comments 155) | doc/comment only — update narrative; assert no drop counter |

> **Net: ~11 files need real source edits** (#1-5, #8-10, #12-14). #6/#7/#11 are kit/doc-adjacent and
> need verification but minimal change. The "~11 affected metric test files" in SPEC/CONTEXT matches
> #1-5 + #8-10 + #12-13 (≈10-11). **`DispatchTestKit.cs` was a false positive** — its `outcome`/
> `NewResultPublic(StepOutcome outcome,…)` matches are the `DataResult` outcome param, NOT a metric
> label; no metric edit needed there.

### D-14 absence-assert targets (the three removed counters)
Add explicit "no series" assertions for:
- `orchestrator_result_deduped_*` (was BreakerMetricsFacts #3)
- `processor_dispatch_deduped_*` (was BreakerMetricsFacts #3 / ProcessorMetricsFacts #2)
- `keeper_reinject_dropped_*` (was ReinjectConsumerFacts #4 / OrchestratorReinjectConsumerFacts #5)

Plus the renamed-not-removed but old-named: `orchestrator_dispatch_sent_*`,
`orchestrator_result_consumed_*`, `processor_dispatch_consumed_*`, `processor_result_sent_*` and the
`outcome` label (acceptance criteria require these absent too).

## Metadata

**Analog search scope:** `src/Orchestrator`, `src/BaseProcessor.Core`, `src/Keeper`,
`src/Messaging.Contracts`, `tests/BaseApi.Tests`.
**Files scanned:** 27 (3 meter classes, 11 increment-site/contract files, 13 test files).
**Pattern extraction date:** 2026-06-18.
</content>
</invoke>
