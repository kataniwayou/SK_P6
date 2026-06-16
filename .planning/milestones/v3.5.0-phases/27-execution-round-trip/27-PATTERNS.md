# Phase 27: Execution Round-Trip - Pattern Map

**Mapped:** 2026-06-01
**Files analyzed:** 16 (5 new src, 6 modified src, ~9 new/extended test)
**Analogs found:** 16 / 16 (all brownfield mirrors — no orphan files)

> This is a brownfield mirror phase. Every new file has a verbatim-or-near in-repo precedent named in CONTEXT `<canonical_refs>` and RESEARCH. The planner copies the cited analog excerpts directly; nothing here is invented. Line numbers are as of 2026-06-01.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` **(NEW)** | consumer (framework) | event-driven (consume→transform→send) | `src/Orchestrator/Consumers/ResultConsumer.cs` | exact (symmetric half) |
| `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` **(NEW)** | utility (ported validator) | transform (validate) | `src/BaseApi.Service/.../JsonSchemaConfig.cs` + `.../PayloadConfigSchemaValidator.cs` | exact (port) |
| result builder + one-by-one Send loop **(NEW — inside the consumer, or a sibling helper)** | service (id-mint + send) | request-response / pub | `src/Orchestrator/Dispatch/StepDispatcher.cs` | exact (field-thread mirror) |
| dispatch-endpoint retry config (`UseMessageRetry(Immediate(3))`) **(NEW — inline in the runtime bind, NOT a `ConsumerDefinition`)** | config (endpoint) | event-driven | `src/Orchestrator/Consumers/ResultConsumerDefinition.cs` | role-match (definition→inline bind) |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` **(MODIFIED)** | startup (orchestrator) | event-driven (bind) | itself, lines 136-139 (the `MarkHealthy` seam) | exact (extend in place) |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` **(MODIFIED)** | config (composition root) | — | itself, lines 54-59 | exact (extend in place) |
| `src/BaseProcessor.Core/Processing/ProcessResult.cs` **(MODIFIED)** | model (record) | transform | itself, line 9 | exact (firm up) |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` **(seam invoked, not edited)** | model (abstract base) | transform | itself, lines 22-23 | exact (invoke seam) |
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` **(MODIFIED)** | config (options) | — | itself, lines 20-26 | exact (add 5th knob) |
| L2 input-read + output-write (inside the consumer) | service (Redis I/O) | file-I/O (L2) | `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` lines 86-100 | exact (same mux idiom) |
| `tests/.../Processor/EntryStepDispatchConsumer*Facts.cs` **(NEW ×8)** | test | event-driven | `tests/.../Orchestrator/ResultConsumeTests.cs` + `AckSemanticsTests.cs` | exact (harness + stubs) |
| `tests/.../Processor/ProcessorJsonSchemaValidatorFacts.cs` **(NEW)** | test | transform | `tests/.../Orchestrator/AckSemanticsTests.cs` (unit shape) | role-match |
| `tests/.../Processor/DispatchOutputWriteFacts.cs` **(NEW)** | test (integration) | file-I/O | `tests/.../Processor/LivenessHeartbeatFacts.cs` (RedisFixture write+TTL) | exact |
| `tests/.../Processor/ProcessorOptionsBindingFacts.cs` **(EXTEND)** | test | — | itself | exact (add a case) |

---

## Pattern Assignments

### `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` (NEW — consumer, event-driven)

**Primary analog:** `src/Orchestrator/Consumers/ResultConsumer.cs` (the symmetric orchestrator-half: an `IConsumer<T>` that reads, fans out work, Sends, and returns-to-ack / throws-on-infra).
**Secondary analogs:** `ProcessorLivenessHeartbeat.cs` (L2 I/O), `StepDispatcher.cs` (build + Send), `PayloadConfigSchemaValidator.cs` (validate).

**Constructor-injection + class shape** — copy the primary-ctor `IConsumer<T>` shape from `ResultConsumer.cs:37-42`:
```csharp
public sealed class ResultConsumer(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IStepDispatcher dispatcher,
    ILogger<ResultConsumer> logger) : IConsumer<ExecutionResult>
{
    public async Task Consume(ConsumeContext<ExecutionResult> context)
```
For the dispatch consumer this becomes `IConsumer<EntryStepDispatch>` injecting `IConnectionMultiplexer redis`, `IProcessorContext context`, `BaseProcessor processor`, `IOptions<ProcessorLivenessOptions> options`, `ILogger<...> logger`. Note: `EntryStepDispatch` does NOT need the disambiguation alias that `ExecutionResult` needs (`using ExecutionResult = Messaging.Contracts.ExecutionResult;` at `ResultConsumer.cs:5`) — but `ExecutionResult` (which this file constructs) DOES collide with `MassTransit.ExecutionResult`, so carry that alias.

**Business-ack vs infra-throw discipline** — the load-bearing pattern, copied from `ResultConsumer.cs:43-64`. Returning normally = ACK; the only escape is an infra `Send`/Redis fault:
```csharp
// BUSINESS ack — log + return, NEVER throw (mirror WorkflowLifecycle.IsBusiness)
logger.LogInformation("No L1 entry for ... — acking result (business)", ...);
return;
// ...
// returns normally -> ACK. An infra fault from Send propagates -> Immediate(3) -> _error.
```
Apply per D-09/D-15: input-missing / output-validation-fail / empty-list / caught `ProcessAsync` exception are all in-consumer BUSINESS outcomes (build a `Failed`/`Cancelled`/no-message and still Send/ack). Only a `Send` failure or an L2-output-**write** fault THROWS (D-15 / Pitfall 5 — write fault is INFRA, grounded in `WorkflowLifecycle.IsInfra`).

**L2 input read (existence-check first)** — mirror `ProcessorLivenessHeartbeat.cs:86,97` (the `_redis.GetDatabase()` + `StringSetAsync(key, json, expiry:)` idiom) for the GET side, using `L2ProjectionKeys.ExecutionData(entryId)`:
```csharp
var db = redis.GetDatabase();                                   // heartbeat line 86
var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(dispatch.EntryId));
if (raw.IsNullOrEmpty) { /* D-07: Failed before ProcessAsync */ }
var inputData = raw.ToString();
```
The `IsNullOrEmpty` existence idiom is the same one `WorkflowLifecycle` uses (RESEARCH Pattern 2). NOTE the heartbeat's resilience pattern at `ProcessorLivenessHeartbeat.cs:102-110` is **log-and-continue** — do NOT copy that catch onto the execution path; an L2-output-write fault must propagate (D-15).

**L2 output write (TTL'd)** — mirror `ProcessorLivenessHeartbeat.cs:97-100` exactly (same `StringSetAsync(key, value, expiry: TimeSpan.FromSeconds(...))` signature), but with the new CONFIG-02 TTL:
```csharp
var newEntryId = NewId.NextGuid();
await db.StringSetAsync(
    L2ProjectionKeys.ExecutionData(newEntryId),
    result.OutputData,                                          // RAW string, NO JSON wrapper (D-08/D-09)
    expiry: TimeSpan.FromSeconds(options.ExecutionDataTtlSeconds));   // CONFIG-02
```

**`ProcessAsync` invocation + outcome ownership** — invoke the seam declared at `BaseProcessor.cs:22-23` (`Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)`); `config` = `dispatch.Payload`. Wrap per D-09: `OperationCanceledException` → one `Cancelled`; any other exception → one `Failed` carrying `ex.Message`; empty list → ack-only (no message). The seam is `protected abstract` on `BaseProcessor`, so the consumer holds a `BaseProcessor` reference and the concrete must expose/override it (planner confirms accessibility — likely an internal framework caller or a `protected internal` widening).

---

### `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` (NEW — utility, port)

**Analog:** `src/BaseApi.Service/Features/Schema/JsonSchemaConfig.cs` (the SSRF static-ctor + `DefaultOptions`) + `src/BaseApi.Service/Features/Orchestration/Validation/PayloadConfigSchemaValidator.cs` (parse-guard + `FromText` + `Evaluate` + flatten). **PORT the pattern — do not reference** (firewall, D-05).

**SSRF static ctor — copy verbatim from `JsonSchemaConfig.cs:22-34`:**
```csharp
static JsonSchemaConfig()
{
    Dialect.Default = Dialect.Draft202012;            // library default is V1, not 2020-12
    SchemaRegistry.Global.Fetch = (_, _) => null;     // SSRF lockdown — no outbound $ref fetch
}
public static EvaluationOptions DefaultOptions { get; } = new() { OutputFormat = OutputFormat.List };
```
Pitfall 2/3: the validate method MUST reference `DefaultOptions` so the cctor fires before any `Evaluate`. Both this and `JsonSchemaConfig` set the SAME process-wide globals to IDENTICAL values — safe (Pitfall 3); do NOT diverge dialect/fetcher.

**Parse-guard + Evaluate + flatten — copy from `PayloadConfigSchemaValidator.cs:53-94`:**
```csharp
try { schema = JsonSchema.FromText(definition); }
catch (Exception ex) when (ex is JsonException or JsonSchemaException)
{ /* unparseable -> business Failed (D-06), never a crash */ }
// ...
var results = schema.Evaluate(payloadDoc.RootElement, JsonSchemaConfig.DefaultOptions);
if (!results.IsValid)
{
    var errorStrings = (results.Details ?? Enumerable.Empty<EvaluationResults>())
        .Where(d => d.Errors is { Count: > 0 })
        .SelectMany(d => d.Errors!.Select(kv => $"{d.InstanceLocation}: {kv.Value}"))
        .ToList();
    if (errorStrings.Count == 0 && results.Errors is { Count: > 0 })
        errorStrings = results.Errors.Select(kv => $"{results.InstanceLocation}: {kv.Value}").ToList();
    // ...
}
```
**Behavioral adaptation (D-06):** the WebApi validator THROWS `OrchestrationValidationException` (it's an HTTP gate). The processor port must instead return `bool TryValidate(string? definition, string data, out IReadOnlyList<string> errors)` — `null`/whitespace definition → `true` (skip, guard with `string.IsNullOrWhiteSpace` BEFORE `FromText`, Pitfall 4); unparseable → `false`. The RESEARCH §"Code Examples / Ported SSRF-locked validator" gives the exact target shape (lines 348-396 of 27-RESEARCH.md).

---

### Result builder + one-by-one Send (NEW — inside the consumer; service)

**Analog:** `src/Orchestrator/Dispatch/StepDispatcher.cs:11-27` (the verbatim build-and-Send block, `Send` NOT `Publish`).

**Build with field threading** — mirror `StepDispatcher.cs:17-22` (inherit shared ids, init-set the per-message ids). Target object is `ExecutionResult` (positional `WorkflowId, StepId, ProcessorId, Outcome` per `ExecutionResult.cs:7-11`, then init `CorrelationId`/`ExecutionId`/`EntryId`/`ErrorMessage`/`CancellationMessage`):
```csharp
// StepDispatcher.cs:17-22 shape, retargeted to ExecutionResult per D-11:
var executionResult = new ExecutionResult(
    dispatch.WorkflowId, dispatch.StepId, dispatch.ProcessorId, StepOutcome.Completed)
{
    CorrelationId = dispatch.CorrelationId,   // D-11: copied from dispatch BODY
    ExecutionId   = NewId.NextGuid(),         // D-11: minted per-result (sequential GUID)
    EntryId       = newEntryId,               // D-11/D-12: the L2 write key (Guid.Empty on Failed/Cancelled, D-13)
};
```

**Send to named queue, one-by-one** — mirror `StepDispatcher.cs:25-26` (`GetSendEndpoint(new Uri($"queue:..."))` + `Send`), targeting `OrchestratorQueues.Result` (`"orchestrator-result"`, from `OrchestratorQueues.cs:16`):
```csharp
var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
foreach (var executionResult in builtResults)   // D-14: ONE-BY-ONE, never a batched list
    await endpoint.Send(executionResult, context.CancellationToken);   // infra fault here THROWS (EXEC-09)
```
`sendProvider` may be the injected `ISendEndpointProvider` (what `StepDispatcher` does) OR the `ConsumeContext` itself (it IS an `ISendEndpointProvider`) — either resolves identically (RESEARCH Pattern 4 / A4). Anti-pattern: never `Publish` (would fan-out, `StepDispatcher.cs:9` comment).

---

### Dispatch-endpoint retry config — `UseMessageRetry(Immediate(3))` (NEW — config, inline)

**Analog:** `src/Orchestrator/Consumers/ResultConsumerDefinition.cs:22-32` — the `EndpointName` + `UseMessageRetry(r => r.Immediate(3))` posture.
```csharp
public ResultConsumerDefinition() => EndpointName = OrchestratorQueues.Result;
protected override void ConfigureConsumer(..., IReceiveEndpointConfigurator endpointConfigurator, ...)
{
    endpointConfigurator.UseMessageRetry(r => r.Immediate(3));   // bounded infra retry -> _error
}
```
**Adaptation (D-01/D-02):** the dispatch consumer must NOT use a static `ConsumerDefinition` (it would auto-bind a wrong-named queue at bus start, Pitfall 1). The `EndpointName` is unknown until identity resolves. So this `UseMessageRetry(r => r.Immediate(3))` line moves into the runtime `ConnectReceiveEndpoint` lambda in `ProcessorStartupOrchestrator` (see below) — same retry call, different host. Mirror the `Immediate(3)` value exactly.

---

### `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (MODIFIED — extend in place)

**Analog:** itself, lines 136-139 (the current completion block) + RESEARCH Pattern 1.

**Insert the runtime bind between Loop B and `MarkHealthy`.** Current completion block (`ProcessorStartupOrchestrator.cs:136-139`):
```csharp
// --- Completion (D-02): identity + all required non-null definitions resolved. ---
context.MarkHealthy(); // heartbeat may now write.
gate.MarkReady();
```
Replace with the bind-then-Healthy order (D-03 — the load-bearing sequence; RESEARCH Pattern 1, lines 182-204):
```csharp
var queueName = $"{context.Id!.Value:D}";   // plain name -> competing-consumer; Send target is queue:{id:D}
var handle = endpointConnector.ConnectReceiveEndpoint(queueName, (ctx, cfg) =>
{
    cfg.UseMessageRetry(r => r.Immediate(3));                 // mirror ResultConsumerDefinition
    cfg.ConfigureConsumer<EntryStepDispatchConsumer>(ctx);    // DI-resolved consumer attached
});
await handle.Ready;                                           // queue declared + consumer attached BEFORE Healthy
context.MarkHealthy();                                        // NOW the heartbeat's IsHealthy gate opens -> Healthy lands in L2 AFTER the bind
gate.MarkReady();
```
**New ctor dependency:** inject `IReceiveEndpointConnector endpointConnector` into the primary ctor (`ProcessorStartupOrchestrator.cs:43-51`) — `IBus` also implements it; resolvable from DI (RESEARCH Pattern 1 notes / A3). The receive endpoint binds the BARE `{id:D}` name (the `queue:` scheme is sender-only — `OrchestratorQueues.cs:13` + RESEARCH Pattern 1). Durable/AutoDelete defaults already satisfy EXEC-01; the optional `RabbitMqReceiveEndpointConfigurator` cast is non-load-bearing (RESEARCH A2 / Open Q1 — rely on defaults).

---

### `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (MODIFIED — extend in place)

**Analog:** itself, lines 54-59 (the `AddBaseConsoleMessaging` lambda).

**Register the dispatch consumer WITHOUT a static endpoint** — add to the existing `configureConsumers` lambda (after `BaseProcessorServiceCollectionExtensions.cs:58`):
```csharp
services.AddBaseConsoleMessaging(cfg, x =>
{
    x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
    x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));
    // EXEC-01 / D-01: register for DI but EXCLUDE from the unconditional ConfigureEndpoints(ctx)
    x.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints();   // Pitfall 1
});
```
Pitfall 1: `AddBaseConsoleMessaging` calls `c.ConfigureEndpoints(ctx)` UNCONDITIONALLY (RESEARCH cites `MessagingServiceCollectionExtensions.cs:58`); a plain `AddConsumer` would create a wrong-named kebab `entry-step-dispatch` queue. `.ExcludeFromConfigureEndpoints()` is the verified-correct suppression (RESEARCH Decision A / alternatives table).

**Register the `BaseProcessor` resolution seam** — the consumer needs a `BaseProcessor` to invoke; the concrete registers its subclass. Add a registration (planner confirms shape — the concrete's `Program.cs` likely does `AddSingleton<BaseProcessor, ConcreteProcessor>()`, P28). The existing DI ordering (steps 1-8 at lines 47-93) and the `StartupCompletionService` removal block (lines 87-92) stay intact.

**Wire the CONFIG-02 TTL** — no new code needed: `services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"))` at line 62 already binds the section, so the new `ExecutionDataTtlSeconds` property auto-binds.

---

### `src/BaseProcessor.Core/Processing/ProcessResult.cs` (MODIFIED — firm up)

**Analog:** itself, line 9. Change `public sealed record ProcessResult();` → `public sealed record ProcessResult(string OutputData);` (D-08; RESEARCH Code Examples line 344). Output-data string ONLY — the framework owns all outcomes (D-10); do NOT add an outcome field (anti-pattern).

---

### `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` (MODIFIED — add a 5th knob)

**Analog:** itself, lines 20-26 (the `IntervalSeconds`/`TtlSeconds` `[ConfigurationKeyName]` + baked-default pattern).
```csharp
[ConfigurationKeyName("Ttl")]
public int TtlSeconds { get; set; } = 30;
```
Add `ExecutionDataTtlSeconds` in the same shape (D-17 / CONFIG-02 — RESEARCH recommends this property on the existing class, not a sibling):
```csharp
[ConfigurationKeyName("ExecutionDataTtl")]
public int ExecutionDataTtlSeconds { get; set; } = /* planner picks default (e.g. 300) */;
```
DISTINCT from the liveness `Ttl`; applied on every `L2[data(newEntryId)]` write. The class XML-doc header (lines 5-15) currently says "Four INDEPENDENT seconds-int" — update to five.

---

## Shared Patterns

### Business-ack vs infra-throw (the spine of this phase)
**Source:** `src/Orchestrator/Hydration/WorkflowLifecycle.cs:154-161` (the canonical classifier) + `src/Orchestrator/Consumers/ResultConsumer.cs:43-64` (applied).
**Apply to:** `EntryStepDispatchConsumer.Consume`.
```csharp
public static bool IsInfra(Exception ex) =>
    ex is RedisConnectionException or RedisTimeoutException or RedisException;
public static bool IsBusiness(Exception ex) => !IsInfra(ex);
```
D-15 / Pitfall 5: input-missing, output-validation-fail, empty-list, caught `ProcessAsync` exception = BUSINESS (build outcome, Send, ack). `Send` fault + L2-output-**write** fault = INFRA (throw → `Immediate(3)` → `_error`). The output-write fault is classified INFRA precisely because `RedisConnectionException`/`RedisException` are `IsInfra` here.

### L2 I/O via the soft-dep multiplexer
**Source:** `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs:86,97-100`.
**Apply to:** the consumer's input read + output write.
```csharp
var db = _redis.GetDatabase();
await db.StringSetAsync(key, json, expiry: TimeSpan.FromSeconds(opts.TtlSeconds));
```
Use `L2ProjectionKeys.ExecutionData(entryId)` (`L2ProjectionKeys.cs:39` → `skp:data:{entryId:D}`). Existence-check reads with `RedisValue.IsNullOrEmpty`. CAUTION: do NOT copy the heartbeat's log-and-continue catch (`ProcessorLivenessHeartbeat.cs:102-110`) onto the write path (D-15).

### Id minting
**Source:** `EntryStepDispatch.cs:6` comment ("minted with NewId.NextGuid()") + `StepDispatcher`.
**Apply to:** per-result `ExecutionId` and output `EntryId`. Use `NewId.NextGuid()` (sequential GUIDs), never `Guid.NewGuid()` (Don't-Hand-Roll table).

### `cfg.Require`-style fail-fast options (CONFIG-02)
**Source:** `ProcessorLivenessOptions.cs:18-35` (the `[ConfigurationKeyName]` + baked-default convention).
**Apply to:** `ExecutionDataTtlSeconds`. (Note: CONTEXT D-17 references a `cfg.Require` posture; the existing options class uses baked defaults via `[ConfigurationKeyName]` rather than literal `cfg.Require` calls — mirror the ACTUAL existing convention in this file.)

### Correlation flow (EXEC-10)
**Source:** BaseConsole.Core outbound correlation filter (bus-wide, already wired by `AddBaseConsoleMessaging`) + explicit body copy per D-11.
**Apply to:** every built `ExecutionResult` — set `CorrelationId = dispatch.CorrelationId` explicitly (Pattern 3 above); the envelope is auto-stamped by the inherited filter. No manual envelope stamping (Don't-Hand-Roll table).

---

## Test Pattern Assignments

All tests live in the SINGLE `tests/BaseApi.Tests` project, slice `tests/BaseApi.Tests/Processor/*`. Run filter: `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Processor"`.

### In-memory harness consume tests (EXEC-01 consume, EXEC-07/08 send, EXEC-10)
**Analog:** `tests/.../Orchestrator/ResultConsumeTests.cs:36-55` (the `AddMassTransitTestHarness` + static `cfg.ReceiveEndpoint($"{id:D}", e => e.ConfigureConsumer<T>(ctx))` pattern — Pitfall 6 says test via a STATIC named endpoint, NOT the runtime `ConnectReceiveEndpoint`).
```csharp
.AddMassTransitTestHarness(x =>
{
    x.AddConsumer<CapturingDispatchConsumer>();
    x.UsingInMemory((ctx, cfg) =>
    {
        cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<...>(ctx));
        cfg.ConfigureEndpoints(ctx);
    });
})
```
Capture sent `ExecutionResult`s via `harness.Consumed.Select<ExecutionResult>(ct)` (bind a capturing consumer on `orchestrator-result`) — mirror `ResultConsumeTests.cs:125-137`. Reuse `ProcessorTestHarness.cs` conventions for the processor slice harness.

### Unit consume tests with NSubstitute fakes (EXEC-02/03 input, EXEC-09 ack semantics)
**Analog:** `tests/.../Orchestrator/OrchestratorTestStubs.cs` (the `AbsentL2`/`PresentL2`/`InfraFaultL2` `IConnectionMultiplexer` factories + `Context<T>(message, ct)` `ConsumeContext` stub) + `AckSemanticsTests.cs:107-127` (the infra-throw `Assert.ThrowsAsync<RedisConnectionException>` proof).
```csharp
var mux = OrchestratorTestStubs.InfraFaultL2(out _);
await Assert.ThrowsAsync<RedisConnectionException>(
    () => consumer.Consume(OrchestratorTestStubs.Context(dispatch, ct)));
```
`OrchestratorTestStubs` is `internal static` in the `Orchestrator` test namespace — usable cross-namespace within the single test project (RESEARCH confirms). For a missing-input business case, use `AbsentL2` and assert a `Failed` `ExecutionResult` was Sent (not a throw).

### Real-Redis integration write+TTL test (EXEC-05)
**Analog:** `tests/.../Processor/LivenessHeartbeatFacts.cs:68-109` (RedisFixture write + `KeyTimeToLiveAsync` TTL assertion) + `tests/.../Composition/RedisFixture.cs` (`Track` net-zero teardown — D-23 triple-SHA discipline).
```csharp
public sealed class DispatchOutputWriteFacts : IClassFixture<RedisFixture>
// ...
_redis.Track(L2ProjectionKeys.ExecutionData(newEntryId));   // net-zero teardown (D-23)
var remaining = await db.KeyTimeToLiveAsync(key);
Assert.InRange(remaining!.Value.TotalSeconds, ttl - 5, ttl);
```
MUST `Track` every `skp:data:{entryId:D}` key written so the shared keyspace returns to BEFORE state (RedisFixture.cs:54 — no wildcard SCAN).

### Ported-validator unit test (EXEC-03/05, SSRF lockdown)
**Analog:** plain xUnit fact (no harness) — assert skip (null/whitespace → valid), fail (bad data → false + flattened errors), pass, and the SSRF case (an `http://` `$ref` evaluates closed, Pitfall 2 warning sign). Mirror the assertion style of `ProcessorOptionsBindingFacts.cs`.

### Options-binding test (CONFIG-02) — EXTEND
**Analog:** `tests/.../Processor/ProcessorOptionsBindingFacts.cs:16-36` — add `["Processor:ExecutionDataTtl"] = "..."` to the in-memory config and assert `opts.ExecutionDataTtlSeconds`; add the default to the `Empty_Config_Yields_Baked_Defaults` case (line 47-50).

### Bind-sequencing unit test (EXEC-01 ordering)
**Analog:** no exact in-repo analog for the fake-`IReceiveEndpointConnector` ordering proof. Assert `MarkHealthy` is called AFTER `await handle.Ready` using a fake connector whose `Ready` task ordering is observable (RESEARCH Pitfall 6 / test map line 474). Defer the real `ConnectReceiveEndpoint`-after-Healthy proof to Phase 28 E2E. **This is the ONE test with no verbatim analog** — see below.

---

## No Analog Found

| File / sub-pattern | Role | Data Flow | Reason |
|--------------------|------|-----------|--------|
| `DispatchBindSequenceFacts.cs` (the fake-`IReceiveEndpointConnector` MarkHealthy-after-Ready ordering proof) | test | event-driven | No existing test fakes `IReceiveEndpointConnector` for ordering. Closest is the general harness-start pattern; planner builds a minimal fake connector. The runtime-bind MECHANISM itself (RESEARCH Pattern 1) is the only genuinely new code in the phase — its full proof is deferred to Phase 28 real-stack E2E (Pitfall 6). |

Everything else is a verbatim or near-verbatim brownfield mirror.

---

## Metadata

**Analog search scope:** `src/Orchestrator/{Consumers,Dispatch,Hydration}`, `src/BaseProcessor.Core/{Processing,Liveness,Startup,Configuration,Identity,DependencyInjection}`, `src/BaseApi.Service/Features/{Schema,Orchestration/Validation}`, `src/Messaging.Contracts`, `tests/BaseApi.Tests/{Processor,Orchestrator,Composition}`.
**Files read this pass:** 22 (ResultConsumer, ResultConsumerDefinition, StepDispatcher, JsonSchemaConfig, PayloadConfigSchemaValidator, ProcessorStartupOrchestrator, BaseProcessorServiceCollectionExtensions, ProcessorLivenessHeartbeat, ProcessResult, BaseProcessor, ProcessorLivenessOptions, ExecutionResult, EntryStepDispatch, OrchestratorQueues, L2ProjectionKeys, StepOutcome, WorkflowLifecycle §IsBusiness, IProcessorContext, ProcessorTestHarness, ResultConsumeTests, OrchestratorTestStubs, ProcessorOptionsBindingFacts, AckSemanticsTests, LivenessHeartbeatFacts, RedisFixture, FakeProcessorContext).
**Pattern extraction date:** 2026-06-01
