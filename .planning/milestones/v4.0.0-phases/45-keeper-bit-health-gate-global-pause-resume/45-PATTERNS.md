# Phase 45: Keeper BIT Health Gate + Global Pause/Resume - Pattern Map

**Mapped:** 2026-06-08
**Files analyzed:** 11 (8 new, 3 modified)
**Analogs found:** 10 / 11 (one new primitive — `IL2HealthGate` — has no repo analog; uses the cited Toub pattern)

All research-suggested analogs were verified against the live codebase this session. Every cited line number is accurate as of HEAD. Read this file alongside `45-RESEARCH.md` §Architecture Patterns (the Pattern 1–4 code blocks are the canonical templates; this file maps each new file to its real on-disk analog with confirmed excerpts).

## File Classification

| New/Modified File | New/Mod | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|---------|------|-----------|----------------|---------------|
| `src/Keeper/Health/IL2HealthGate.cs` | new | interface (sync primitive) | event-driven (signal) | — (no analog; Toub `AsyncManualResetEvent`) | none — cited pattern |
| `src/Keeper/Health/L2HealthGate.cs` | new | utility / primitive | event-driven (signal) | — (no analog; Toub swappable-TCS) | none — cited pattern |
| `src/Keeper/Health/BitHealthLoop.cs` | new | hosted service (`BackgroundService`) | event-driven / pub (Publish) | `src/Orchestrator/Hydration/HydrationBackgroundService.cs` | role-match (BackgroundService outer `while` + `RedisException`/infra catch + `Task.Delay(stoppingToken)` + OCE-on-shutdown) |
| `src/Keeper/Recovery/L2ProbeRecovery.cs` | MODIFY | service (probe helper) | request-response (read+write-then-delete) | itself, `L2ProbeRecovery.cs:34-50` (extract `ProbeOnceAsync` from the `for`-body) | exact (self-refactor) |
| `src/Messaging.Contracts/PauseAll.cs` | new | contract (record) | pub-sub (broadcast control) | `StartOrchestration.cs` (no-`H`, `ICorrelated`, `CorrelationId{get;init;}`) | exact |
| `src/Messaging.Contracts/ResumeAll.cs` | new | contract (record) | pub-sub (broadcast control) | `StartOrchestration.cs` | exact |
| `src/Orchestrator/Consumers/PauseAllConsumer.cs` | new | consumer | request-response (in → scheduler op) | `PauseWorkflowConsumer.cs` | exact |
| `src/Orchestrator/Consumers/ResumeAllConsumer.cs` | new | consumer | request-response → iterate L1 | `ResumeWorkflowConsumer.cs` (+ L1 enumerate) | exact |
| `src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs` | new | config (ConsumerDefinition) | config | `PauseWorkflowConsumerDefinition.cs` (retry-owner) | exact |
| `src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs` | new | config (ConsumerDefinition) | config | `ResumeWorkflowConsumerDefinition.cs:13-27` (NO second retry) | exact |
| `src/Orchestrator/Scheduling/WorkflowScheduler.cs` | MODIFY | service (scheduler seam) | request-response (raw `IScheduler`) | itself, `PauseAsync` at `WorkflowScheduler.cs:115-117` | exact (self, add sibling seam) |
| `src/Orchestrator/Program.cs` | MODIFY | config (composition root) | config | itself, fan-out registration `Program.cs:40-50` | exact (self, add new endpoint) |

## Pattern Assignments

---

### `src/Keeper/Health/IL2HealthGate.cs` + `L2HealthGate.cs` (primitive, event-driven)

**Analog:** NONE in repo. Use `45-RESEARCH.md` §Pattern 1 (Stephen Toub `AsyncManualResetEvent`, swappable `TaskCompletionSource`, starts CLOSED per D-12). The full ~30-line impl is in RESEARCH lines 162-203 — copy it verbatim and place under `Keeper.Health`.

**Conventions to replicate (from research, mandatory):**
- `TaskCreationOptions.RunContinuationsAsynchronously` on EVERY TCS construction (D-10; without it `Open()`'s `TrySetResult` runs awaiter continuations inline on the BIT-loop thread → deadlock — Pitfall 1).
- Gate field starts **pending** (CLOSED, D-12): `private volatile TaskCompletionSource<bool> _tcs = new(RunContinuationsAsynchronously);`
- `Open()` → `_tcs.TrySetResult(true)` (idempotent set).
- `Close()` → only swap if current is completed, via `Interlocked.CompareExchange(ref _tcs, fresh, current)` (atomic against a concurrent `Open()`).
- `WaitForOpenAsync(CancellationToken ct)` → snapshot `_tcs.Task`; fast-path return if `IsCompleted`; else `return openTask.WaitAsync(ct)` (net8.0 built-in, throws OCE on cancel — honors D-11 caller bound).
- `sealed` class, file-scoped namespace (repo convention — every contract/consumer file above uses `namespace X;`).

**DI:** register as a singleton in `Keeper/Program.cs` — `builder.Services.AddSingleton<IL2HealthGate, L2HealthGate>();` (mirror the existing `AddSingleton<...>` calls at `Keeper/Program.cs:37,40,48`).

---

### `src/Keeper/Health/BitHealthLoop.cs` (BackgroundService, event-driven + Publish)

**Analog:** `src/Orchestrator/Hydration/HydrationBackgroundService.cs` — the repo's canonical `BackgroundService` outer-loop with infra-only catch, `Task.Delay(stoppingToken)`, and graceful OCE-on-shutdown. The BIT loop's logical shape (edge-trigger publish) is in `45-RESEARCH.md` §Pattern 2 (lines 215-252).

**`BackgroundService` shell + graceful shutdown** (`HydrationBackgroundService.cs:24-37, 72-79`):
```csharp
public sealed class HydrationBackgroundService(
    IConnectionMultiplexer redis,
    WorkflowLifecycle lifecycle,
    IStartupGate gate,
    ILogger<HydrationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // ... work ...
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { return; } // shutdown requested mid-backoff
        }
    }
}
```
Replicate: primary-ctor DI, `sealed`, `: BackgroundService`, `while (!stoppingToken.IsCancellationRequested)`, `Task.Delay(delay, stoppingToken)` wrapped in `try/catch (OperationCanceledException)` to break cleanly (NOT treated as a probe failure — Pitfall 5 / research line 255). BIT-loop ctor deps per research line 215: `L2ProbeRecovery probe, IL2HealthGate gate, IBus bus, IOptions<ProbeOptions> opts, ILogger<BitHealthLoop> logger`.

**Edge-trigger (D-06) — NEW vs analog:** the analog runs once then `return`s on success; the BIT loop runs forever. Track `bool? prevHealthy = null` (null = no prior tick → first probe always counts as a transition, satisfying D-12 without special-casing). Publish `PauseAll`/`ResumeAll` ONLY when `prevHealthy != healthy`. Use `bus.Publish(...)` NOT `Send` (fan-out — D-08; Pitfall 3).

**Delay knob:** reuse `IOptions<ProbeOptions>.Value.DelaySeconds` (already bound at `Keeper/Program.cs:29`; see `ProbeOptions.cs:10`). `MaxAttempts` is irrelevant to the outer loop (research OQ-2 → reuse, don't add a new knob now).

**Structured logging template holes** (NEVER interpolate — T-37-05): research line 237/243 — `logger.LogInformation("L2 healthy — gate OPEN, ResumeAll broadcast")` / `logger.LogWarning("L2 unhealthy — gate CLOSED, PauseAll broadcast")`.

**DI:** `builder.Services.AddSingleton<IL2HealthGate, L2HealthGate>();` then `builder.Services.AddHostedService<BitHealthLoop>();` in `Keeper/Program.cs` (mirror Orchestrator's `AddHostedService<HydrationBackgroundService>()` at `Program.cs:74`). `IBus` is already available from `AddBaseConsoleMessaging`.

---

### `src/Keeper/Recovery/L2ProbeRecovery.cs` (MODIFY — extract `ProbeOnceAsync`)

**Analog:** itself. Extract the single-iteration read + write-then-delete body out of the `for`-loop at `L2ProbeRecovery.cs:34-50` into a `public async Task<bool> ProbeOnceAsync(...)`.

**Existing probe core to extract** (`L2ProbeRecovery.cs:36-49`):
```csharp
try
{
    _ = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(entryId));   // READ — value need NOT exist (D-02)
    var scratch = (RedisKey)L2ProjectionKeys.KeeperProbe(h);
    await db.StringSetAsync(scratch, "1", expiry: TimeSpan.FromSeconds(30)); // WRITE w/ short TTL
    await db.KeyDeleteAsync(scratch);                                        // then delete (net-zero)
    return ProbeOutcome.Recovered;                                          // both ops, no exception
}
catch (RedisException)   // RedisConnectionException + RedisTimeoutException both derive from this
{
    metrics.L2ProbeFailed.Add(1, procTag);
    // ...
}
```
Extracted form returns `Task<bool>` (`true` = both ops no exception; `catch (RedisException) → false`). Template in `45-RESEARCH.md` §"ProbeOnceAsync Extraction" (lines 323-342).

**Conventions to replicate (mandatory):**
- **`catch (RedisException)` ONLY** — NEVER `catch (Exception)` (repo discipline, see the existing catch at `L2ProbeRecovery.cs:44`; Pitfall 5). A genuine bug must propagate, not masquerade as "L2 down" → false `PauseAll` storm. Note the existing XML-doc comment (lines 16-17) states this rule explicitly.
- `var db = redis.GetDatabase();` then `StringGetAsync` / `StringSetAsync(expiry: 30s)` / `KeyDeleteAsync`.
- Reuse `L2ProjectionKeys.ExecutionData(...)` and `L2ProjectionKeys.KeeperProbe(...)` (already imported via `using Messaging.Contracts.Projections;`).

**Refactor discipline:** `RunAsync` (the v3.x bounded caller at line 25) must stay byte-identical in behavior — its `for`-loop calls `ProbeOnceAsync` once per attempt, keeping its `metrics.InFlight`/`L2ProbeFailed`/`Task.Delay` + outcome shape. Do NOT move metrics into `ProbeOnceAsync` (the BIT loop has its own observability; `RunAsync` owns the bounded-probe metric labels — see lines 22-29).

**OQ-1 (planner decides):** `RunAsync` probes a caller-supplied `entryId`/`h`; the BIT loop has no inbound message. Parameterize `ProbeOnceAsync(Guid? entryId, string? h, CancellationToken ct)` with sentinel defaults for the standing BIT probe (read target need not exist — the read only proves reachability). Small Plan-task decision; changes no locked decision.

---

### `src/Messaging.Contracts/PauseAll.cs` + `ResumeAll.cs` (contract, pub-sub)

**Analog:** `src/Messaging.Contracts/StartOrchestration.cs` — the no-`H`, `ICorrelated`, `CorrelationId { get; init; }` control-record shape.

**Exact shape to copy** (`StartOrchestration.cs:1-7`):
```csharp
namespace Messaging.Contracts;

/// <summary>Control message: start orchestration for the given workflows. Body-carries the correlation id (D-01).</summary>
public sealed record StartOrchestration(Guid[] WorkflowIds) : ICorrelated
{
    public Guid CorrelationId { get; init; }
}
```

**Conventions to replicate (mandatory — no-`H` posture, RETIRE-01 / D-08-shape):**
- `public sealed record PauseAll : ICorrelated { public Guid CorrelationId { get; init; } }` — and same for `ResumeAll`.
- **NO `H` and NO dedup key** — contrast with `PauseWorkflow.cs:4` (`PauseWorkflow(Guid WorkflowId, string H) : ICorrelated`) which carries `H`; the new global contracts carry NO payload at all (no `WorkflowId`, no `H`) — a pure broadcast. `CorrelationId` for tracing only (`NewId.NextGuid()` at publish, research line 236/242).
- `ICorrelated` only (NOT `IKeeperRecoverable` — that is the 5-id partition marker for `KeeperUpdate` et al at `KeeperUpdate.cs:7`, irrelevant here).
- File-scoped `namespace Messaging.Contracts;`, XML-doc one-liner, `sealed record`.

---

### `src/Orchestrator/Consumers/PauseAllConsumer.cs` (consumer, request-response)

**Analog:** `src/Orchestrator/Consumers/PauseWorkflowConsumer.cs`.

**Pattern to copy** (`PauseWorkflowConsumer.cs:17-28`):
```csharp
public sealed class PauseWorkflowConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<PauseWorkflowConsumer> logger) : IConsumer<PauseWorkflow>
{
    public async Task Consume(ConsumeContext<PauseWorkflow> context)
    {
        var m = context.Message;
        // Structured template holes — NEVER interpolated (T-37-05 / security V5).
        logger.LogInformation("Pause WorkflowId={WorkflowId} H={H}", m.WorkflowId, m.H);
        await lifecycle.PauseOnlyAsync(m.WorkflowId, context.CancellationToken);
    }
}
```

**Adapt for global (D-01/D-03):** inject `WorkflowScheduler scheduler` (NOT `WorkflowLifecycle` — pause-all is scheduler-wide), `ILogger<PauseAllConsumer>`. Body: `logger.LogWarning("Global PauseAll CorrelationId={CorrelationId}", context.Message.CorrelationId);` then `await scheduler.PauseAllAsync(context.CancellationToken);`. Idempotent (re-pausing already-paused groups is a Quartz no-op — no new guard). Template: research lines 296-300.

**Conventions:** `sealed`, primary-ctor DI, structured template holes (`{CorrelationId}` — never interpolate, T-37-05 / V5), `context.CancellationToken` threaded through, returns normally → ACK.

---

### `src/Orchestrator/Consumers/ResumeAllConsumer.cs` (consumer, request-response → iterate L1)

**Analog:** `src/Orchestrator/Consumers/ResumeWorkflowConsumer.cs` + the `IWorkflowL1Store.WorkflowIds` snapshot.

**Pattern to copy** (`ResumeWorkflowConsumer.cs:18-29`): same `sealed` + primary-ctor + structured-log + `context.CancellationToken` shape as above.

**Adapt for global (D-02 — the load-bearing decision):** inject `IWorkflowL1Store store, WorkflowLifecycle lifecycle, ILogger<ResumeAllConsumer>`. Body (research lines 303-308):
```csharp
logger.LogInformation("Global ResumeAll CorrelationId={CorrelationId}", context.Message.CorrelationId);
foreach (var workflowId in store.WorkflowIds)                              // L1 snapshot — IWorkflowL1Store.WorkflowIds:23
    await lifecycle.ResumeAsync(workflowId, context.CancellationToken);    // TriggerState==Paused guard inside
```

**Conventions / load-bearing facts:**
- **NEVER call native `scheduler.ResumeAll()`** (D-02; Pitfall 2; research lines 348/376) — it applies misfire-on-resume to every past-due one-shot `WithMisfireHandlingInstructionFireNow()` trigger (`WorkflowScheduler.cs:47,81`) → cross-workflow herd. Per-job `ResumeAsync` instead.
- Per-job idempotency is FREE via the existing guard `if (state != TriggerState.Paused) return;` at `WorkflowLifecycle.cs:186` → duplicate `ResumeAll` finds already-Normal triggers and no-ops each (D-03). `ResumeAsync` then does `UnscheduleAsync` + fresh-from-now `ScheduleAsync` (`WorkflowLifecycle.cs:197-198`) = skip-to-next, no burst.
- Enumerate `store.WorkflowIds` — the L1 snapshot (`IWorkflowL1Store.cs:23` — `IReadOnlyCollection<Guid> WorkflowIds`).

---

### `src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs` (config — RETRY OWNER)

**Analog:** `src/Orchestrator/Consumers/PauseWorkflowConsumerDefinition.cs`.

**Pattern to copy** (`PauseWorkflowConsumerDefinition.cs:20-40`):
```csharp
public sealed class PauseWorkflowConsumerDefinition : ConsumerDefinition<PauseWorkflowConsumer>
{
    private readonly IOptions<RetryOptions> _retryOptions;
    public PauseWorkflowConsumerDefinition(IOptions<RetryOptions> retryOptions)
    {
        _retryOptions = retryOptions;
        EndpointName = "orchestrator-pauseresume";   // shared base name
    }
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PauseWorkflowConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;                              // serial — no lock/stripe
        endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit)); // OWNS shared-endpoint retry
    }
}
```

**Adapt (D-08 — independence):**
- `EndpointName = "orchestrator-global-pauseresume";` — a **NEW** base name, NOT `"orchestrator-pauseresume"`. This independence lets Phase 48 drop the old per-workflow endpoint with zero entanglement.
- This definition OWNS the per-endpoint retry: `endpointConfigurator.UseMessageRetry(r => r.Immediate(_retryOptions.Value.Limit));` (inject `IOptions<RetryOptions>` — bound at `Program.cs:29`).
- `ConcurrentMessageLimit = 1` (serialize Pause/Resume on this replica).
- NO `r.Ignore<...>()` — there is no L2-hydration business exception on this control path (mirrors PauseWorkflow def; contrast Start def at `StartOrchestrationConsumerDefinition.cs:41` which DOES ignore `WorkflowRootNotFoundException`).

---

### `src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs` (config — NO retry)

**Analog:** `src/Orchestrator/Consumers/ResumeWorkflowConsumerDefinition.cs:13-27` — the retry-NON-owner that shares the endpoint.

**Exact pattern to copy** (`ResumeWorkflowConsumerDefinition.cs:13-27`):
```csharp
public sealed class ResumeWorkflowConsumerDefinition : ConsumerDefinition<ResumeWorkflowConsumer>
{
    public ResumeWorkflowConsumerDefinition()
    {
        EndpointName = "orchestrator-pauseresume";   // SAME base name as Pause def
    }
    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ResumeWorkflowConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1; // serial; retry owned by Pause def
    }
}
```

**Adapt (Pitfall 4 — DO NOT register retry twice):**
- `EndpointName = "orchestrator-global-pauseresume";` — SAME as the new Pause def (both share one endpoint).
- `ConcurrentMessageLimit = 1` only. **NO `UseMessageRetry`** — `UseMessageRetry` is per-ENDPOINT and the Pause def already owns it; a second registration on the same endpoint double-wraps retry (Pitfall 4 / research line 388-392). No `IOptions<RetryOptions>` ctor dependency needed (parameterless ctor like the analog).

---

### `src/Orchestrator/Scheduling/WorkflowScheduler.cs` (MODIFY — add `PauseAllAsync` seam)

**Analog:** itself — the existing thin `PauseAsync` seam at `WorkflowScheduler.cs:115-117`.

**Existing sibling seam to mirror** (`WorkflowScheduler.cs:115-117`):
```csharp
/// <summary>Pause the job's current triggers (Quartz PauseJob) — Pause, D-06/D-08. Idempotent.</summary>
public Task PauseAsync(Guid jobId, CancellationToken ct) =>
    scheduler.PauseJob(KeyFor(jobId), ct);
```

**Add (D-01):**
```csharp
/// <summary>Scheduler-wide pause-all (ORCH-02, D-01). Idempotent — re-pausing is a Quartz no-op.</summary>
public Task PauseAllAsync(CancellationToken ct) => scheduler.PauseAll(ct);
```
A one-line expression-bodied seam over the raw injected `IScheduler` (the ctor already injects `IScheduler scheduler` at `WorkflowScheduler.cs:17`). Mirror the XML-doc one-liner convention. Do NOT add a `ResumeAllAsync` here — resume is per-job via `WorkflowLifecycle.ResumeAsync` (D-02), NOT a scheduler-wide seam.

---

### `src/Orchestrator/Program.cs` (MODIFY — register new fan-out endpoint)

**Analog:** itself — the per-replica fan-out registration at `Program.cs:40-50`.

**Existing fan-out idiom to mirror** (`Program.cs:40-50`, inside `AddBaseConsoleMessaging`'s `x => { ... }`):
```csharp
x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
x.AddConsumer<PauseWorkflowConsumer, PauseWorkflowConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
x.AddConsumer<ResumeWorkflowConsumer, ResumeWorkflowConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
```

**Add (D-08) — two new consumers on the SAME per-replica `instanceId`:**
```csharp
x.AddConsumer<PauseAllConsumer, PauseAllConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
x.AddConsumer<ResumeAllConsumer, ResumeAllConsumerDefinition>()
    .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
```

**Conventions to replicate (mandatory):**
- Capture the SAME `instanceId` variable already declared at `Program.cs:35` (`var instanceId = builder.Configuration["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N");`) so both new consumers share ONE temp fan-out queue `orchestrator-global-pauseresume-{instanceId}` per replica — exactly how Start+Stop share `orchestrator-{instanceId}`.
- `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` — the per-replica fan-out idiom (every replica gets its own auto-delete queue → `Publish` reaches all). NEVER omit `InstanceId` (that would make it a shared competing-consumer like `ResultConsumer` at `Program.cs:53`).
- The new `EndpointName` (`"orchestrator-global-pauseresume"`) lives on the Definition classes, NOT here — `Program.cs` only sets `InstanceId`/`Temporary`.

---

## Shared Patterns

### Per-replica fan-out endpoint (broadcast, not competing-consumer)
**Source:** `src/Orchestrator/Program.cs:40-50` + `instanceId` at line 35
**Apply to:** `PauseAllConsumer`/`ResumeAllConsumer` registration (`Program.cs`) and their Definitions (`EndpointName`).
**Rule:** `Publish` (fan-out, reaches every replica) + `.Endpoint(InstanceId, Temporary=true)` + a shared captured `instanceId`. NEW base name `orchestrator-global-pauseresume` (D-08 — independent from `orchestrator-pauseresume`). Use `Publish`, never `Send` (Pitfall 3).

### Per-endpoint retry ownership (one definition owns it)
**Source:** `PauseWorkflowConsumerDefinition.cs:24-38` (owner) + `ResumeWorkflowConsumerDefinition.cs:13-27` (non-owner)
**Apply to:** `PauseAllConsumerDefinition` (owns `UseMessageRetry(Immediate(_retry.Value.Limit))`) + `ResumeAllConsumerDefinition` (sets only `ConcurrentMessageLimit=1`, NO retry).
**Rule:** `UseMessageRetry` is per-ENDPOINT; two definitions on one endpoint → only the FIRST registers retry (Pitfall 4). Both set `ConcurrentMessageLimit = 1`. `RetryOptions` bound from `"Retry"` section (`Program.cs:29`).

### `RedisException`-only infra discipline
**Source:** `src/Keeper/Recovery/L2ProbeRecovery.cs:44` (+ doc comment lines 16-17); echoed by `WorkflowLifecycle.IsInfra` at `WorkflowLifecycle.cs:205-206`
**Apply to:** `ProbeOnceAsync` (the extracted probe core) and the BIT loop.
**Rule:** `catch (RedisException)` ONLY (it is the superset of `RedisConnectionException` + `RedisTimeoutException`). NEVER `catch (Exception)` — a genuine bug must propagate, not be relabeled "L2 down" → false `PauseAll` storm (Pitfall 5). In the BIT loop, a non-Redis throw should propagate out of `ExecuteAsync` and be surfaced by the host, NOT swallowed.

### Structured-logging template holes (never interpolate)
**Source:** `PauseWorkflowConsumer.cs:24-25`, `ResumeWorkflowConsumer.cs:25-26`, `WorkflowLifecycle.cs:191-193`
**Apply to:** EVERY new consumer + the BIT loop.
**Rule:** `logger.LogX("... {Name}", value)` with template holes — NEVER string interpolation `$"..."` (T-37-05 / security V5; log-injection mitigation). Global control messages log only `{CorrelationId}` (a Guid — no free-text).

### No-`H` control-record shape (RETIRE-01)
**Source:** `StartOrchestration.cs:4-7` (no-`H`) vs `PauseWorkflow.cs:4` (legacy `H`-carrying)
**Apply to:** `PauseAll`/`ResumeAll`.
**Rule:** `public sealed record X : ICorrelated { public Guid CorrelationId { get; init; } }` — no `H`, no dedup key, no payload (pure broadcast). `ICorrelated` only.

### Graceful-shutdown `BackgroundService` loop
**Source:** `src/Orchestrator/Hydration/HydrationBackgroundService.cs:33-37, 72-79`
**Apply to:** `BitHealthLoop`.
**Rule:** `while (!stoppingToken.IsCancellationRequested)`, `await Task.Delay(delay, stoppingToken)` inside `try/catch (OperationCanceledException)` → break/return cleanly (host stop is NOT a probe failure). Register via `AddHostedService<T>()` (Orchestrator `Program.cs:74`).

## No Analog Found

| File | Role | Data Flow | Reason / Substitute |
|------|------|-----------|---------------------|
| `src/Keeper/Health/IL2HealthGate.cs` + `L2HealthGate.cs` | primitive | event-driven (signal) | No async reset-event exists in the repo. Use the cited Stephen Toub `AsyncManualResetEvent` swappable-TCS pattern — full impl in `45-RESEARCH.md` §Pattern 1 (lines 162-203). It is a textbook ~30-line primitive; do NOT add `Nito.AsyncEx` (research line 91/95). DI-register as a singleton in `Keeper/Program.cs` like the existing `AddSingleton` calls. |

## Metadata

**Analog search scope:** `src/Keeper/`, `src/Orchestrator/`, `src/Messaging.Contracts/` (full file listing via `git ls-files`).
**Files scanned (read in full this session):** `L2ProbeRecovery.cs`, `Keeper/Program.cs`, `ProbeOptions.cs`, `Orchestrator/Program.cs`, `WorkflowScheduler.cs`, `WorkflowLifecycle.cs`, `IWorkflowL1Store.cs`, `WorkflowFireJob.cs` (head), `HydrationBackgroundService.cs`, `PauseWorkflowConsumer.cs`, `ResumeWorkflowConsumer.cs`, `PauseWorkflowConsumerDefinition.cs`, `ResumeWorkflowConsumerDefinition.cs`, `StartOrchestrationConsumerDefinition.cs`, `PauseWorkflow.cs`, `StartOrchestration.cs`, `KeeperUpdate.cs`, `ICorrelated.cs`.
**Line-citation verification:** all research citations confirmed accurate — `PauseAsync` @ `WorkflowScheduler.cs:116`, `ResumeAsync` guard @ `WorkflowLifecycle.cs:186` (Unschedule+reschedule @ 197-198), fan-out registration @ `Program.cs:40-50`, `ResumeWorkflowConsumerDefinition` retry-non-owner @ 13-27, `L2ProbeRecovery` probe core @ 36-49, `[DisallowConcurrentExecution]` @ `WorkflowFireJob.cs:29`, one-shot `WithMisfireHandlingInstructionFireNow()` @ `WorkflowScheduler.cs:47,81`.
**Pattern extraction date:** 2026-06-08
```

## PATTERN MAPPING COMPLETE

**Phase:** 45 - Keeper BIT Health Gate + Global Pause/Resume
**Files classified:** 11 (8 new, 3 modified)
**Analogs found:** 10 / 11

### Coverage
- Files with exact analog: 9 (contracts, consumers, definitions, scheduler seam, Program.cs — all self/sibling exact matches)
- Files with role-match analog: 1 (`BitHealthLoop` → `HydrationBackgroundService`)
- Files with no analog: 1 (`IL2HealthGate`/`L2HealthGate` — cited Toub `AsyncManualResetEvent`, no repo precedent)

### Key Patterns Identified
- **Per-replica fan-out registration** (`Program.cs:40-50` `.Endpoint(InstanceId, Temporary=true)` + shared `instanceId`) — new global Pause/Resume consumers mirror Start/Stop/PauseWorkflow on a NEW endpoint `orchestrator-global-pauseresume` (D-08).
- **Per-endpoint retry ownership** — Pause def OWNS `UseMessageRetry`, Resume def sets only `ConcurrentMessageLimit=1` (verified `PauseWorkflowConsumerDefinition.cs:38` vs `ResumeWorkflowConsumerDefinition.cs:13-27`; Pitfall 4).
- **`TriggerState==Paused` guard + fresh-from-now reschedule** (`WorkflowLifecycle.cs:186,197-198`) gives ORCH-02 idempotency and the no-burst resume for free — never native `ResumeAll()` (D-02/D-03).
- **`RedisException`-only catch** (`L2ProbeRecovery.cs:44`) is the load-bearing probe discipline for the extracted `ProbeOnceAsync` and the BIT loop.
- **No-`H` `ICorrelated` record shape** (`StartOrchestration.cs:4-7`) is the template for `PauseAll`/`ResumeAll` (RETIRE-01), contrasted against the legacy `H`-carrying `PauseWorkflow.cs:4`.
- **Graceful `BackgroundService` loop** (`HydrationBackgroundService.cs`) is the structural analog for `BitHealthLoop`; the edge-trigger `prevHealthy` logic is the only genuinely new behavior.

### File Created
`C:\Users\UserL\source\repos\SK_P4\.planning\phases\45-keeper-bit-health-gate-global-pause-resume\45-PATTERNS.md`

### Ready for Planning
All research-suggested analogs verified against the live codebase (every cited line number is accurate at HEAD). The only new primitive (`IL2HealthGate`) is flagged under "No Analog Found" with the cited Toub pattern pointer. The planner can now reference each analog file:line directly in PLAN.md action sections.

