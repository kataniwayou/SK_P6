# Phase 86: Console & WebApi Health/Liveness Refactor - Research

**Researched:** 2026-07-26
**Domain:** ASP.NET Core health checks (Microsoft.Extensions.Diagnostics.HealthChecks) in a two-container embedded-Kestrel console pattern + MassTransit 8.5.5 bus health + StackExchange.Redis 2.13.1 + k8s probes
**Confidence:** HIGH (all findings are direct reads of the exact files named in the spec; design is locked, this is implementation mapping)

> This is IMPLEMENTATION research. The design in
> `docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md` is LOCKED.
> Nothing below reopens a decision — it maps each locked decision to the concrete current code
> shape and the exact change point.

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **Probe roles (uniform; HTTP Unhealthy→503, Healthy/Degraded→200):** `/health/startup` = one-time boot done (webapi: migrations; consoles: no infra) — wire a **new** `startupProbe` on all four. `/health/ready` = each service's **required** infra deps reachable + (processor) identity+schema resolved; NotReady, **never restart**. `/health/live` = is my watchdog loop still turning? restart is the sole remedy; **never reacts to infra**.
- **Shared liveness watchdog primitive (BaseConsole.Core) — HLTH-01/03/06:** new `ILivenessHeartbeat` singleton (`Beat()` stamps a `TimeProvider` timestamp; pure in-memory, zero I/O) + `LoopLivenessHealthCheck` (tag `"live"`), registered via `AddBaseConsole` **opt-in per service**. Stale iff `now − lastBeat > k × interval`, **default k=3** (today's watchdogs use k=2). Beat at the top of every critical-loop iteration, before any infra call, unconditionally. **No unbounded blocking on the beat path** — Keeper: move `ReceiveEndpoint.Start().Ready` / `bus.Publish(ResumeAll)` off the beat path (bounded/off-tick). **Keeper + processor opt in; orchestrator stays `self`-only (NO watchdog).** **Retire** `Keeper.Health.KeeperLivenessWatchdogHealthCheck` and `BaseProcessor.Core.Liveness.LivenessWatchdogHealthCheck`. **Processor beats unconditionally** — drop the "only when Healthy" gate from the `/health/live` beat; keep that gate only for the L2 liveness write.
- **Readiness — Option B — HLTH-04/08:** consoles: bus is a **hard** dep → broker down = NotReady. Add **Redis** to readiness for all services (currently on no probe). Processor readiness also includes **identity+schema resolved** (pure in-memory read; never throws). BaseApi: bus is publish-only/soft → keep `MinimalFailureStatus = Degraded` cap; BaseApi bus is **NOT latched**.
- **No self-heal → hard readiness latch — HLTH-05:** once a **required** dep has failed for its readiness `failureThreshold` window, **latch `/health/ready` Unhealthy until process restart** even if the client reconnects. **Migration stays one-shot** (`StartupCompletionService`). Loops still retry + log to stay alive.
- **Infra-exception invariant — HLTH-02:** every Redis/RMQ/Postgres call is caught + logged and never crashes the process — including `ProcessorStartupOrchestrator`'s identity/schema loop (already compliant; **lock with a test**). `OperationCanceledException` from shutdown is the one deliberate pass-through.
- **Startup-gate marking + k8s — HLTH-07:** `IStartupGate.IsReady` trigger per service — orchestrator = hydration complete (existing `HydrationBackgroundService.MarkReady`); keeper/processor = **first watchdog beat** (new); baseapi = migrations (existing). Add `startupProbe → /health/startup` to all four k8s deployments.

### Claude's Discretion (implementation details)
- Exact `ILivenessHeartbeat` API shape, the latch wrapper's storage/reset mechanism, per-service beat interval values (respect current: processor 10s→stale 20-30s, keeper 5s→stale 10-15s under k=3), startupProbe numeric config, and file/namespace placement — follow existing `BaseConsole.Core/Health` patterns.

### Deferred Ideas (OUT OF SCOPE)
- Orchestrator liveness/deadlock detection (would need an own lease-poll loop to beat from).
- Making migration self-heal (retry-to-success).
- Disabling MassTransit/Redis client auto-reconnect (we latch above the clients, not disable them).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| HLTH-01 | Shared `ILivenessHeartbeat` + `LoopLivenessHealthCheck` in BaseConsole.Core, opt-in | §Standard Stack, §Pattern 1, §Pattern 2 — model on the two existing per-service watchdog checks (both retired); the `HealthCheckDescriptor` fold-in seam is already the opt-in mechanism |
| HLTH-02 | Infra-exception invariant; `ProcessorStartupOrchestrator` already compliant, lock with test | §Component map (ProcessorStartupOrchestrator L141-147, L195-201 broad catch); §Test Scaffolds — mirror a resilience fact |
| HLTH-03 | Beat unconditionally at top of critical loop; bounded infra calls | §Pattern 1, §Pitfall 1 (keeper edge block), §Pitfall 2 (processor gate) |
| HLTH-04 | Readiness = required deps (bus hard for consoles; +Redis; +processor identity/schema) | §Pattern 3 (Redis check), §Pattern 4 (processor readiness via `IProcessorContext.IsHealthy`) |
| HLTH-05 | Hard readiness latch — sustained failure sticks Unhealthy until restart | §Pattern 5 (latch wrapper) |
| HLTH-06 | Retire the two per-service watchdogs; orchestrator opts out | §Component map (delete `KeeperLivenessWatchdogHealthCheck` + `LivenessWatchdogHealthCheck` + holders/descriptors) |
| HLTH-07 | Startup-gate marking per service; add k8s startupProbe ×4 | §Pattern 6 (first-beat MarkReady), §k8s manifests |
| HLTH-08 | Redis added to readiness on all four services | §Pattern 3, §Pattern 5 (latched on console bus+Redis and baseapi Postgres+Redis) |
</phase_requirements>

## Summary

The system already has a mature, well-factored health surface. This phase is a **consolidation + inversion refactor**, not greenfield. Two nearly-identical per-service self-watchdog health checks (`Keeper.Health.KeeperLivenessWatchdogHealthCheck` and `BaseProcessor.Core.Liveness.LivenessWatchdogHealthCheck`, each with its own L1 timestamp holder and its own `HealthCheckDescriptor` registration) get replaced by ONE `ILivenessHeartbeat` + `LoopLivenessHealthCheck` primitive in `BaseConsole.Core.Health`, wired through the **already-generic `HealthCheckDescriptor` fold-in seam** that `EmbeddedHealthEndpointService` enumerates. Keeper and processor opt in (register the descriptor + a beat call in their loop); orchestrator does not.

The infra category-error fix is small and surgical: the **processor** drops its "only when Healthy" gate from the *liveness beat* (the gate stays on the L2 write only — `ProcessorLivenessHeartbeat.ExecuteAsync` L76), and the **keeper** moves `ReceiveEndpoint.Start().Ready` / `bus.Publish(ResumeAll)` off the beat path so a hung broker can't freeze the `BitHealthLoop` tick (`BitHealthLoop.ExecuteAsync` L59-96). Readiness gains a **Redis** check (currently NO service health-checks Redis) plus a **latch wrapper** that sticks readiness Unhealthy after a sustained-failure window, defeating MassTransit/Redis auto-reconnect self-heal. The processor also gets an identity+schema readiness check — cleanly implemented as a pure-volatile read of `IProcessorContext.IsHealthy` (already `Volatile.Read`, never throws, means exactly "identity + all required definitions resolved"). Four k8s manifests each gain a `startupProbe → /health/startup`.

**Primary recommendation:** Build the `ILivenessHeartbeat`/`LoopLivenessHealthCheck` pair by lifting the retired watchdogs' math verbatim (change `×2` → `×k` with k=3), register it via the existing descriptor seam, and reuse the two existing watchdog unit-test files as the test scaffold (they already use `FakeTimeProvider` + a stub `IServiceProvider`). Implement the Redis readiness check as a hand-rolled never-throw `IHealthCheck` (do NOT add `AspNetCore.HealthChecks.Redis` — a bounded `try/catch → Unhealthy` custom check is simpler and satisfies the never-throw requirement). Implement the latch as a thin `IHealthCheck` decorator holding a per-process sticky `long` failure-count/latched flag.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Liveness watchdog primitive (`ILivenessHeartbeat`/`LoopLivenessHealthCheck`) | BaseConsole.Core (shared console base library) | Keeper, BaseProcessor.Core (opt-in beat + descriptor) | R3: one shared design in the base library; services opt in only if they have a real loop |
| Beat emission | Keeper `BitHealthLoop`, Processor `ProcessorLivenessHeartbeat` | — | The critical loop owns the beat; must beat before infra I/O |
| Redis readiness check | BaseConsole.Core (consoles) + BaseApi.Core (webapi) | — | Each host library registers its own Redis check on `/health/ready` |
| Readiness latch | BaseConsole.Core (console bus+Redis) + BaseApi.Core (Postgres+Redis) | — | Latch wrapper lives beside the checks it wraps; baseapi bus is NOT latched |
| Processor identity+schema readiness | BaseProcessor.Core | — | Reads processor-specific `IProcessorContext` — cannot live in the console base |
| Startup-gate trigger | Per-service host (`Program.cs` / composition root) | — | Orchestrator=hydration, keeper/processor=first beat, baseapi=migration |
| k8s startupProbe | Ops manifests (`k8s/30-33`) | — | Probe cadence is a deployment concern |

## Standard Stack

### Core (all already referenced — NO new packages required for the recommended path)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Microsoft.Extensions.Diagnostics.HealthChecks | (SDK, .NET 8) | `IHealthCheck`, `HealthCheckResult`, tag-predicate mapping | Already the entire health surface |
| MassTransit | 8.5.5 | `IBusControl.CheckHealth()` → `BusHealthResult` (programmatic bus readiness) | `BusReadyHealthCheck` already reads it; NO public `IBusHealth` in 8.5.5 |
| StackExchange.Redis | 2.13.1 | `IConnectionMultiplexer` singleton; ping via `GetDatabase().PingAsync()` or `ExecuteAsync("PING")` | Already the console + api Redis client (`abortConnect=false`, lazy) |
| AspNetCore.HealthChecks.UI.Client | (pinned) | `UIResponseWriter.WriteHealthCheckUIResponse` per-check JSON body | Already the probe response writer |
| Microsoft.Extensions.TimeProvider.Testing | (pinned in test csproj) | `FakeTimeProvider` for deterministic staleness math | Already used by both retired-watchdog test files |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Hand-rolled Redis `IHealthCheck` | `AspNetCore.HealthChecks.Redis` (`.AddRedis(...)`) | Adds a new package + its exception surface; does NOT guarantee the "never throw at check time" contract as cleanly. **Reject** — a ~15-line custom check with a bounded `try/catch → Unhealthy` is the locked "never-throw" shape and matches the existing custom-check idiom (`BusReadyHealthCheck`, the watchdogs). |
| New latch decorator | Reusing `MinimalFailureStatus` capping | Capping only downgrades severity; it does NOT make a recovered client stay NotReady. The latch is a genuinely new behavior. |

**Installation:** None. All packages already referenced. (If the planner instead chose `AspNetCore.HealthChecks.Redis`, verify with `dotnet list package` / the registry — but the recommended path adds nothing.)

## Package Legitimacy Audit

No external packages are installed by the recommended implementation path (all APIs used are already-referenced SDK/framework/pinned packages). **Disposition: N/A — no new dependencies.**

If the planner deviates and adds `AspNetCore.HealthChecks.Redis`, gate it behind a `checkpoint:human-verify` and run the legitimacy protocol; but this is explicitly NOT recommended.

## Component Responsibilities (exact current shape → change point)

| File | Current shape | Change |
|------|---------------|--------|
| `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` | Two-container listener. Inner container: `AddCheck("self", Healthy, tags:["live"])`, `AddCheck<StartupHealthCheck>(tags:["startup"])`, `AddCheck<BusReadyHealthCheck>(tags:["ready"])`. **Then folds every OUTER `HealthCheckDescriptor` via `_outer.GetServices<HealthCheckDescriptor>()`** (L84-89), bridging `_outer` through `d.Factory`. Maps `/health/{live,ready,startup}` by tag predicate (L93-107). | **No structural change needed** — the seam is already generic. The new `LoopLivenessHealthCheck` arrives as a `"live"`-tagged descriptor from keeper/processor. The new Redis readiness check + latch arrive either as `"ready"`-tagged descriptors OR wired into the inner `AddHealthChecks()` chain. **Decision for planner:** simplest is to register Redis + latch as `HealthCheckDescriptor`s too, so BaseConsole.Core stays generic and orchestrator/keeper/processor all pick up Redis-ready identically. |
| `src/BaseConsole.Core/Health/HealthCheckDescriptor.cs` | `record(string Name, string[] Tags, Func<IServiceProvider,IHealthCheck> Factory)` | Reuse as-is for the new checks. |
| `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs` | `IHealthCheck` resolving `IBusControl` from `_outer`; `CheckHealth()` → Degraded|Unhealthy ⇒ Unhealthy; null bus ⇒ Unhealthy("Bus not started"). Never throws. | Unchanged. This is the console bus-ready check the **latch wraps** (console bus is a hard dep). |
| `src/BaseConsole.Core/Health/StartupHealthCheck.cs` | Reads `IStartupGate.IsReady`. | Unchanged. |
| `src/BaseConsole.Core/Health/IStartupGate.cs` (`StartupGate`) | `IsReady` (`Volatile.Read`), `MarkReady()` (`Interlocked.Exchange`), idempotent one-shot. | Unchanged interface. New trigger wiring (first-beat) lives in keeper/processor composition, not here. |
| `src/BaseConsole.Core/Health/StartupCompletionService.cs` | Console hosted service: `StartAsync` → `gate.MarkReady()` at bare host start. | Already REMOVED from processor (`BaseProcessorServiceCollectionExtensions` L234-239) and orchestrator (`Orchestrator/Program.cs` L150-155). Keeper still KEEPS it (`Keeper/Program.cs` L22 comment "default readiness service is KEPT"). **Under the new model keeper/processor mark ready on FIRST BEAT** → keeper must also drop `StartupCompletionService` and mark the gate from the first `BitHealthLoop` beat. |
| `src/BaseConsole.Core/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` | `AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connStr))`. No health check. | Add the Redis readiness check registration (as a `HealthCheckDescriptor`, or via a new `AddBaseConsoleHealth` line). The multiplexer is the check's dep. |
| `src/BaseConsole.Core/DependencyInjection/ConsoleHealthServiceCollectionExtensions.cs` | Outer chain: `self`(live) + `StartupHealthCheck`(startup,ready). Registers `StartupGate`, `StartupCompletionService`, `EmbeddedHealthEndpointService`. | Registration home for the new primitive's opt-in helper (e.g. an `AddConsoleLivenessWatchdog()` that registers `ILivenessHeartbeat` singleton + the `"live"` descriptor). Add Redis-ready + latch here or in the Redis extension. |
| `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` | `IHealthCheck(_outer)`; reads `IProcessorLivenessState.Current` + `TimeProvider` at check time; null⇒Unhealthy("liveness loop not started"), `now >= ts + interval×2`⇒Unhealthy("liveness loop stale"), carries per-schema summary in `Data`. | **DELETE** (retired). Its staleness math + info-disclosure discipline are the template for `LoopLivenessHealthCheck` (but timestamp-only, no summary — the processor summary was a bonus, not required by the new contract). |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` | `BackgroundService`; loop wraps the L2 write in `if (_context.IsHealthy && _context.Id is {} id)` (L76). Redis fault log-and-continue (L96-106). `Task.Delay(period, _clock, stoppingToken)`. | **Add an unconditional `heartbeat.Beat()` at the top of the loop (before the `IsHealthy` gate).** Keep the `IsHealthy` gate ONLY around the L2 `_writer.WriteAsync`. On the FIRST beat also `gate.MarkReady()` (or route first-beat → gate via the primitive). Now a not-yet-Healthy processor still beats → `/health/live` healthy → no restart. |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` | Loop A (identity) + Loop B (definitions) with broad `catch (Exception ex) when (ex is not OperationCanceledException)` (L141-147, L195-201) → log + backoff. Never throws out. | **No behavior change — already HLTH-02 compliant.** Add a regression test that locks the broker-down-caught-and-retried invariant (spec §10, §7). |
| `src/BaseProcessor.Core/Liveness/IProcessorLivenessState.cs` / `ProcessorLivenessState.cs` | Volatile-ref holder of `ProcessorLivenessEntry`, updated by both loops, read by the retired watchdog. | Keep (still used by the L2 liveness gate mechanism). It is NO LONGER the `/health/live` source — the new `ILivenessHeartbeat` is. Confirm nothing else reads it as a liveness-probe source after the watchdog delete. |
| `src/BaseProcessor.Core/Identity/IProcessorContext.cs` (`ProcessorContext`) | `IsHealthy` = `Volatile.Read(ref _isHealthy)==1` (never throws) = "identity + all required non-null definitions resolved". Other identity props are WR-03 (read-after-Healthy only). | **The processor readiness check reads `IProcessorContext.IsHealthy`** — the volatile-safe, never-throw signal that means exactly "identity+schema resolved". Do NOT read `Id`/`InputDefinition`/etc directly (WR-03 stale-null hazard). |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` L145-162 | Registers the `"live"` `HealthCheckDescriptor` for `LivenessWatchdogHealthCheck` (L152-155), the L1 state singleton, the writer, the heartbeat + startup orchestrator hosted services. Already removes base `StartupCompletionService` (L234-239). | Swap the retired-watchdog descriptor for the shared `LoopLivenessHealthCheck` opt-in. Add the processor identity+schema `"ready"` descriptor. Wire first-beat→`MarkReady`. |
| `src/Keeper/Health/BitHealthLoop.cs` | Unconditional `liveness.Update(clock.GetUtcNow())` every tick (L57). Edge block (L59-96) awaits `h.ReceiveEndpoint.Start(stoppingToken).Ready` (L71) + `bus.Publish(ResumeAll)` (L72) / `Stop` + `PauseAll` — all bus calls that HANG on a dead broker, inside the tick. Trailing `Task.Delay`. | **Beat via `ILivenessHeartbeat.Beat()` at the top of the loop, before `probe.ProbeOnceAsync`.** Move the `Start().Ready`/`Publish` edge bus-ops off the beat path (bounded wait + catch-log, or a separate task) so a hung broker cannot starve the beat. First beat → `gate.MarkReady()`. Delete the `IKeeperLivenessState liveness` dependency + `liveness.Update`. |
| `src/Keeper/Health/KeeperLivenessWatchdogHealthCheck.cs` + `IKeeperLivenessState.cs` + `KeeperLivenessState.cs` | Timestamp-only self-watchdog + its holder. | **DELETE all three** (retired — replaced by the shared primitive). |
| `src/Keeper/Program.cs` L45-54 | Registers `TimeProvider.System`, `IKeeperLivenessState`, and the `"keeper-liveness-watchdog"` `"live"` descriptor. Keeps base `StartupCompletionService` (L22). | Replace the descriptor with the shared `AddConsoleLivenessWatchdog()` opt-in; drop the state singleton. Drop `StartupCompletionService` (keeper now marks ready on first beat). Add Redis-ready + latch (via BaseConsole helper). |
| `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` | `self`(live) + `StartupHealthCheck`(startup,ready) + `.AddNpgSql(...)`(ready). | Add a Redis readiness check (ready) + wrap Postgres + Redis in the latch. Bus is NOT here (it's in Messaging ext) and NOT latched. |
| `src/BaseApi.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` L75-81 | `ConfigureHealthCheckOptions(o => o.MinimalFailureStatus = Degraded)`; bus tags default `["ready","masstransit"]`. | **Unchanged** — bus stays capped at Degraded, NOT latched (locked). |
| `src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs` | `IConnectionMultiplexer` singleton + `RedisProjectionOptions`. Comments say "does NOT register a Redis health check" (D-06). | Add the Redis readiness check here (or in Health ext). NOTE: the old D-06 "Redis soft / no health check" decision is SUPERSEDED by this phase — update the XML-doc comment so it doesn't contradict the new behavior. |
| `src/BaseApi.Core/Health/StartupCompletionService.cs` | One-shot migration; failure → `LogCritical` + gate stays Unhealthy, no rethrow, no `MarkReady`. | **Unchanged** — migration stays one-shot (locked). |
| `k8s/30-baseapi-service.yaml`, `31-orchestrator.yaml`, `32-keeper.yaml`, `33-processor-sample.yaml` | readinessProbe + livenessProbe present; **NO startupProbe**. Ports 8080/8081/8083/8082. Liveness `initialDelaySeconds 30-45, periodSeconds 15, failureThreshold 6`. | **Add a `startupProbe → /health/startup`** on each (own grace window). Once startupProbe guards boot, liveness `initialDelaySeconds` can be tightened (spec §8 says revisit after — keep conservative or note as discretion). |

## Architecture Patterns

### System Architecture Diagram

```
                         kubelet
              ┌────────────┬──────────────┬───────────────┐
              │ startup    │ readiness    │ liveness      │  (NEW: startupProbe on all 4)
              ▼            ▼              ▼
   ┌───────────────────────────────────────────────────────────┐
   │ EmbeddedHealthEndpointService (consoles)  /  Kestrel (api) │
   │  tag predicate:                                           │
   │   /health/startup → ["startup"]                          │
   │   /health/ready   → ["ready"]                            │
   │   /health/live    → ["live"]                            │
   └───────────────────────────────────────────────────────────┘
       │ startup            │ ready                    │ live
       ▼                    ▼                          ▼
  StartupHealthCheck   ┌──────────────────────┐   "self"(always Healthy)
  (IStartupGate)       │ LATCH wrapper (NEW)   │        +
                       │  ├ BusReadyHealthCheck │   LoopLivenessHealthCheck (NEW,
                       │  ├ RedisReadyCheck(NEW)│    opt-in via descriptor)
                       │  └ [proc] IdentitySchema│         ▲
                       │      Ready (NEW)        │         │ reads ILivenessHeartbeat.Current
                       └──────────────────────┘         │
                            ▲ sustained-fail →           │  Beat() called at TOP of:
                            │ sticky Unhealthy            ├─ Keeper.BitHealthLoop tick
                            │ until restart               └─ Processor.ProcessorLivenessHeartbeat tick
                            │
              MassTransit IBusControl.CheckHealth()  /  IConnectionMultiplexer ping  /  NpgSql

   Startup gate trigger (MarkReady):
     orchestrator → HydrationBackgroundService (existing)
     keeper/processor → FIRST watchdog Beat() (NEW)
     baseapi → migration success (existing)
```

Data flow to trace: kubelet hits `/health/live` → tag `"live"` predicate → `self` (always Healthy) + `LoopLivenessHealthCheck` reads `ILivenessHeartbeat.Current`; if `now − Current > k×interval` ⇒ Unhealthy ⇒ 503 ⇒ restart. Infra outage never enters this path (the beat is emitted before any infra call, unconditionally).

### Recommended Project Structure (new/changed files)
```
src/BaseConsole.Core/Health/
├── ILivenessHeartbeat.cs          # NEW: Beat() + Current (TimeProvider-stamped, in-memory)
├── LivenessHeartbeat.cs           # NEW: sealed impl (Interlocked long ticks, like KeeperLivenessState)
├── LoopLivenessHealthCheck.cs     # NEW: "live" check; now - Current > k*interval => stale (k=3)
├── RedisReadyHealthCheck.cs       # NEW: bounded ping, never-throw => Unhealthy
└── LatchedReadinessHealthCheck.cs # NEW: decorator; sustained fail => sticky Unhealthy
src/BaseConsole.Core/DependencyInjection/
└── ConsoleHealthServiceCollectionExtensions.cs  # + AddConsoleLivenessWatchdog() opt-in; + Redis-ready + latch
src/BaseProcessor.Core/Liveness/
├── LivenessWatchdogHealthCheck.cs # DELETE
└── ProcessorIdentitySchemaReadyHealthCheck.cs   # NEW: reads IProcessorContext.IsHealthy
src/Keeper/Health/
├── KeeperLivenessWatchdogHealthCheck.cs  # DELETE
├── IKeeperLivenessState.cs               # DELETE
└── KeeperLivenessState.cs                # DELETE
```

### Pattern 1: Shared liveness heartbeat + loop check (HLTH-01/03/06)

**What:** `ILivenessHeartbeat` singleton stamps a `TimeProvider` timestamp on `Beat()`; `LoopLivenessHealthCheck` reads it and reports stale.
**When to use:** keeper + processor (opt-in). Orchestrator does NOT register it.
**Implementation notes (lift from retired code):**
- Impl storage: mirror `KeeperLivenessState` exactly — `private long _ticks; Update via Interlocked.Exchange(ref _ticks, clock.GetUtcNow().UtcDateTime.Ticks); Current via Interlocked.Read`, `0` sentinel = "never beaten". `DateTime?` can't be `volatile`, hence the long-ticks idiom.
- Staleness math (lift from `LivenessWatchdogHealthCheck` L67 / `KeeperLivenessWatchdogHealthCheck` L52-58): `now >= Current + k*interval ⇒ Unhealthy("liveness loop stale")`; `Current == null ⇒ Unhealthy("... not started")` (though with first-beat→startup-gate handoff, `/health/live` should never see the null state in steady state — startupProbe covers boot). Use **k=3** not the old ×2.
- The check must resolve `ILivenessHeartbeat` + `TimeProvider` from `_outer` **at check time** (never captured at registration) — the `BusReadyHealthCheck(_outer)` / watchdog idiom (RESEARCH Pitfall 4 in the codebase). Registered via `HealthCheckDescriptor(Name:"liveness-watchdog", Tags:["live"], Factory: outer => new LoopLivenessHealthCheck(outer))`.
- `interval` source: read from the service's existing options (`ProcessorLivenessOptions.IntervalSeconds`=10 → stale at 30s under k=3; keeper `ProbeOptions.DelaySeconds`=5 → stale at 15s under k=3). The check needs the interval value — either inject options via `_outer` (keeper watchdog already did: resolves `IOptions<ProbeOptions>`) or bake it into the primitive at registration.

**Example (staleness core, from the file being deleted — `LivenessWatchdogHealthCheck.cs` L64-72):**
```csharp
// Source: src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs (to be generalized, ×2 → ×k)
var now = clock.GetUtcNow().UtcDateTime;
if (now >= current.Timestamp.AddSeconds(current.Interval * 2))   // change 2 → k (=3)
    return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop stale"));
return Task.FromResult(HealthCheckResult.Healthy("live"));
```

### Pattern 2: Unconditional beat at loop top (HLTH-03)

**Processor** (`ProcessorLivenessHeartbeat.ExecuteAsync`, L71-107) — the beat moves ABOVE the `IsHealthy` gate:
```csharp
while (!stoppingToken.IsCancellationRequested)
{
    _heartbeat.Beat();                       // NEW: unconditional, before any infra, before the gate
    if (_firstBeat) { _gate.MarkReady(); _firstBeat = false; }   // HLTH-07 first-beat startup trigger
    if (_context.IsHealthy && _context.Id is { } id)             // gate now ONLY guards the L2 write
    {
        try { ... await _writer.WriteAsync(id, _instanceId, entry); }
        catch (Exception ex) { _logger.LogWarning(...); }        // existing log-and-continue
    }
    try { await Task.Delay(period, _clock, stoppingToken); } catch (OperationCanceledException) { return; }
}
```

**Keeper** (`BitHealthLoop.ExecuteAsync`, L44-100) — beat first, un-hang the edge bus-ops:
```csharp
while (!stoppingToken.IsCancellationRequested)
{
    _heartbeat.Beat();                        // NEW: replaces liveness.Update; unconditional, first
    if (_firstBeat) { _gate.MarkReady(); _firstBeat = false; }
    var healthy = await probe.ProbeOnceAsync(stoppingToken);
    metrics.L2Probe.Add(1);
    if (prevHealthy != healthy) { /* edge: Start().Ready / Publish must be bounded or off-tick */ }
    try { await Task.Delay(delay, stoppingToken); } catch (OperationCanceledException) { break; }
}
```
The edge block's `h.ReceiveEndpoint.Start(ct).Ready` (L71) and `bus.Publish(...)` (L72, L82) currently sit inside the tick with no timeout → a dead broker freezes the tick and (today) starves the stamp. Because the beat is now BEFORE `ProbeOnceAsync`, one hung tick still leaves a fresh beat — but a PERMANENTLY hung `Start().Ready` means the loop never returns to the top. **Bound these with a cancellation/timeout** (e.g. `WaitAsync(TimeSpan)`) + catch-log so the tick always completes, OR move the edge Start/Stop to a separate task. Locked decision: off the beat path.

### Pattern 3: Redis readiness check — bounded, never-throw (HLTH-04/08)

**What:** a hand-rolled `IHealthCheck` resolving `IConnectionMultiplexer` from `_outer` at check time, issuing a bounded PING, mapping any exception → Unhealthy (never throws out).
```csharp
// NEW: src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs (mirror for BaseApi.Core)
public sealed class RedisReadyHealthCheck(IServiceProvider outer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default)
    {
        var mux = outer.GetService<IConnectionMultiplexer>();
        if (mux is null) return HealthCheckResult.Unhealthy("Redis not started");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));                 // bounded — never hang the probe
            await mux.GetDatabase().PingAsync();                     // StackExchange.Redis 2.13.1
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Redis unreachable");  // NO connection-string in message (T-18-08)
        }
    }
}
```
Note `PingAsync()` has no `CancellationToken` overload in StackExchange.Redis; use `.WaitAsync(cts.Token)` if strict cancellation is required, or rely on the multiplexer's own `connectTimeout=5000`/`syncTimeout`. Do NOT put the connection string or exception detail in the result message (info-disclosure guard).

### Pattern 4: Processor identity+schema readiness (HLTH-04)

**What:** the volatile-safe, never-throw signal that identity + all required definitions resolved is exactly `IProcessorContext.IsHealthy` (`Volatile.Read`).
```csharp
// NEW: src/BaseProcessor.Core/Liveness/ProcessorIdentitySchemaReadyHealthCheck.cs
public sealed class ProcessorIdentitySchemaReadyHealthCheck(IServiceProvider outer) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default)
    {
        var context = outer.GetRequiredService<IProcessorContext>();
        return Task.FromResult(context.IsHealthy
            ? HealthCheckResult.Healthy("identity+schema resolved")
            : HealthCheckResult.Unhealthy("identity+schema not yet resolved"));
    }
}
```
Registered as `HealthCheckDescriptor(Name:"identity-schema-ready", Tags:["ready"], Factory: outer => new ...)`. **Do NOT read `context.Id`/`InputDefinition`/etc directly** — those are WR-03 (only safe after `IsHealthy`) and can return stale nulls cross-thread. `IsHealthy` is the one synchronized signal and it means precisely the readiness condition.

### Pattern 5: Readiness latch decorator (HLTH-05)

**What:** a thin `IHealthCheck` wrapping a required-dep check; counts consecutive failed evaluations; once the count reaches the readiness `failureThreshold` window it flips a per-process sticky flag and returns Unhealthy forever (until restart), regardless of the inner check recovering.
```csharp
// NEW: src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs
public sealed class LatchedReadinessHealthCheck(IHealthCheck inner, int failureThreshold) : IHealthCheck
{
    private int _consecutiveFailures;
    private volatile bool _latched;
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext ctx, CancellationToken ct = default)
    {
        if (_latched) return HealthCheckResult.Unhealthy("readiness latched (sustained dependency failure — restart required)");
        var r = await inner.CheckHealthAsync(ctx, ct);
        if (r.Status == HealthStatus.Unhealthy)
        {
            if (Interlocked.Increment(ref _consecutiveFailures) >= failureThreshold) _latched = true;
        }
        else Interlocked.Exchange(ref _consecutiveFailures, 0);   // transient blip resets
        return _latched ? HealthCheckResult.Unhealthy("readiness latched (restart required)") : r;
    }
}
```
**Keys off consecutive failed evaluations** (spec §6: "it can key off consecutive failed evaluations") — this rides the kubelet's own `periodSeconds × failureThreshold` cadence since the probe is polled at that rate. Wrap: console bus-ready + Redis; baseapi Postgres + Redis. **Do NOT wrap the baseapi bus check** (soft/Degraded, not latched). The latch instance is per-process singleton (state must persist across probe polls) — construct it ONCE (in the descriptor factory, capture a singleton latch instance, NOT `new` per check).

> **Latch subtlety for the planner:** the descriptor `Factory` is invoked once per listener build (`EmbeddedHealthEndpointService` folds it at `StartAsync`), so a latch created inside the factory closure IS effectively per-process for the console listener. For baseapi (single Kestrel container, outer chain) register the latch as a singleton and reference it. Verify the latch instance is not re-created per probe request.

### Pattern 6: First-beat startup-gate trigger (HLTH-07)

Keeper/processor drop `BaseConsole.Core.Health.StartupCompletionService` (processor + orchestrator already do; keeper must now too) and call `IStartupGate.MarkReady()` on the first `Beat()`. Simplest: a `bool _firstBeat` in the loop (shown in Pattern 2), or fold "first beat marks the gate" into the `ILivenessHeartbeat`/registration so the loop just calls `Beat()`. Orchestrator keeps `HydrationBackgroundService.MarkReady` (`Orchestrator/Program.cs` L130); baseapi keeps migration-success `MarkReady`.

### Anti-Patterns to Avoid
- **Capturing `IProcessorLivenessState`/`ILivenessHeartbeat`/`TimeProvider` at registration** instead of resolving from `_outer` at check time — the whole two-container design depends on check-time resolution (the codebase calls this "RESEARCH Pitfall 4"). Every existing check follows `Factory: outer => new Check(outer)`.
- **Reading processor identity props directly for readiness** — WR-03 stale-null hazard. Use `IsHealthy`.
- **Putting the beat AFTER the infra call** — defeats the entire fix (infra hang starves the beat → false restart).
- **Latching the baseapi bus check** — explicitly forbidden (soft dep).
- **Overriding MassTransit bus-check `Tags`** — replaces the defaults `["ready","masstransit"]` (codebase "Pitfall 7"); leave tags alone, only touch `MinimalFailureStatus`.
- **Info leakage in probe bodies** — no connection strings / stack traces in `HealthCheckResult` descriptions or `Data` (T-18-08 / T-61-04, asserted by existing tests).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Bus readiness | A custom RMQ connection probe | `IBusControl.CheckHealth()` (already in `BusReadyHealthCheck`) | MassTransit already tracks endpoint-ready state; a manual probe would drift |
| Probe HTTP surface | New endpoints | Existing `EmbeddedHealthEndpointService` tag-predicate map | Already maps live/ready/startup; just add tagged checks |
| Opt-in wiring | A per-service if/switch in BaseConsole | Existing `HealthCheckDescriptor` fold-in seam | Already generic; orchestrator registering nothing = self-only automatically |
| Cross-thread timestamp | `lock`/`volatile DateTime` | `Interlocked` long-ticks (copy `KeeperLivenessState`) | `DateTime?` can't be volatile; the codebase already solved this |
| Startup latch | New gate type | Existing `IStartupGate`/`StartupGate` | Idempotent one-shot already shared by all four |

**Key insight:** almost every primitive this phase needs already exists in a per-service form — the work is *generalizing one, deleting the duplicate, and adding two small checks (Redis, latch)*.

## Runtime State Inventory

This is a code/config refactor with NO stored-data or external-registration migration. Explicit per-category:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — the retired watchdogs read in-memory L1 holders only (`IKeeperLivenessState`, `IProcessorLivenessState`); no Redis/DB key stores a watchdog value. `IProcessorLivenessState` still backs the L2 liveness gate (a separate business signal) and is NOT deleted. | Code edit only |
| Live service config | k8s Deployment probe blocks (`k8s/30-33`) — live cluster manifests. Adding `startupProbe` is a manifest edit + redeploy. | Manifest edit + `kubectl apply` / redeploy |
| OS-registered state | None. | None — verified: no Task Scheduler / systemd / pm2 references in scope. |
| Secrets/env vars | None renamed. Probe env (`ConnectionStrings__Redis` etc.) unchanged. | None |
| Build artifacts | Deleting `KeeperLivenessWatchdogHealthCheck`/state + `LivenessWatchdogHealthCheck` requires a clean rebuild; the k8s local-image-stale trap applies to the live proof (deploy under a unique tag). | Rebuild + unique-tag redeploy for live proof |

## Common Pitfalls

### Pitfall 1: Keeper edge bus-ops freeze the tick under a dead broker
**What goes wrong:** `h.ReceiveEndpoint.Start(ct).Ready` and `bus.Publish(...)` inside `BitHealthLoop`'s edge block hang indefinitely when the broker is unreachable; the loop never returns to the top and the beat goes stale → false liveness restart (this is the ORIGINAL bug for keeper).
**How to avoid:** beat FIRST (top of loop), and bound the edge ops with a timeout/cancellation + catch-log, or run them off the tick. Locked decision (spec §5).
**Warning sign:** keeper `RESTARTS > 0` under the broker-unreachable live proof.

### Pitfall 2: Processor "only when Healthy" gate blocks the beat during bootstrap
**What goes wrong:** if the beat stays inside `if (_context.IsHealthy)`, a processor bootstrapping against a down bus never becomes Healthy → never beats → watchdog trips → restart (the ORIGINAL bug for processor).
**How to avoid:** beat unconditionally above the gate; keep the `IsHealthy` gate ONLY around `_writer.WriteAsync` (the L2 business write).
**Warning sign:** processor `RESTARTS > 0` during a cold boot with a slow/absent broker.

### Pitfall 3: k8s local-image staleness on rebuild (live proof)
**What goes wrong:** Docker-Desktop kubelet runs STALE code after a rebuild (same `:local` tag + `IfNotPresent`). The live broker-unreachable proof would validate old bits.
**How to avoid:** deploy under a unique tag (`fix-<sha>`) + `kubectl set image`, then run the harness `-SkipBringUp` with manual port-forwards (per project memory + spec §rollout). Live stack is k8s ONLY, never docker-compose.

### Pitfall 4: Latch re-created per probe request loses its sticky state
**What goes wrong:** if `LatchedReadinessHealthCheck` is `new`'d inside a per-request path, `_consecutiveFailures`/`_latched` reset every poll → never latches.
**How to avoid:** the latch must be a per-process singleton (construct once in the descriptor factory closure / register as singleton). Verify with a test that N consecutive failures then a "recovered" inner check still returns Unhealthy.

### Pitfall 5: startupProbe not added → tightened liveness kills slow starts
**What goes wrong:** the spec anticipates tightening liveness after startupProbe lands; tightening WITHOUT the startupProbe would CrashLoop slow (~40s rabbitmq cold-start) pods.
**How to avoid:** add `startupProbe → /health/startup` on all four FIRST; keep liveness `initialDelaySeconds` conservative until the startupProbe is proven. Ordering matters.

### Pitfall 6: Deleting `IProcessorLivenessState` by mistake
**What goes wrong:** `IProcessorLivenessState`/`ProcessorLivenessState` look like watchdog-only state but they back the **L2 liveness gate** (a separate business signal written by both startup + heartbeat loops via `ProcessorLivenessWriter`). Only the `LivenessWatchdogHealthCheck` READER is retired.
**How to avoid:** delete only the health-check + its `HealthCheckDescriptor` registration; keep the state holder + writer. Grep for `IProcessorLivenessState` consumers before deleting.

## State of the Art

| Old (current) | New (this phase) | Impact |
|--------------|------------------|--------|
| Two per-service watchdog checks + two L1 holders | One `ILivenessHeartbeat` + `LoopLivenessHealthCheck` in BaseConsole, opt-in | DRY; orchestrator opts out cleanly via the empty-descriptor path |
| Watchdog stale grace = interval×2 (k=2) | k×interval with **k=3** | Extra margin avoids false-trip on one slow tick under the new unconditional-beat model |
| Redis on NO probe (soft, D-06 in api) | Redis on `/health/ready` for all four | A `/health/ready` failure is now a real "required dep down" signal; **supersedes** the BaseApi D-06 comment |
| MassTransit/Redis auto-reconnect silently returns pod to Ready | Latch sticks Unhealthy after sustained failure until restart | "No self-heal" — operator restart is the only recovery |
| No startupProbe | startupProbe on all four | Boot gets its own grace window; steady-state liveness trustable |

**Deprecated/outdated:** the `RedisServiceCollectionExtensions` D-06 comment ("does NOT register a Redis health check", "Redis down ⇒ /health/ready 200") is now historically inaccurate — update it.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `IProcessorContext.IsHealthy` is the intended processor readiness signal (spec says "pure in-memory read of IProcessorContext/IProcessorLivenessState") | Pattern 4 | Low — `IsHealthy` literally means "identity + all required definitions resolved" and is the only volatile-safe never-throw field. If the planner prefers reading `IProcessorLivenessState.Current.Status`, that also works but couples to the L2 entry shape. |
| A2 | Hand-rolled Redis check is preferred over `AspNetCore.HealthChecks.Redis` | Standard Stack, Pattern 3 | Low — custom matches the never-throw contract + existing custom-check idiom; the package is a valid alternative if the planner wants it (then run the legitimacy gate). |
| A3 | Latch keys off consecutive failed probe evaluations (kubelet cadence) rather than a wall-clock timer | Pattern 5 | Low — spec §6 explicitly permits "key off consecutive failed evaluations". A timer-based window is an alternative if evaluation cadence is deemed unreliable. |
| A4 | `StackExchange.Redis` `PingAsync()` on `IDatabase` is the bounded ping (no CT overload; rely on multiplexer timeouts or `.WaitAsync`) | Pattern 3 | Low — version 2.13.1 confirmed pinned; exact ping API should be verified at implementation (`IDatabase.PingAsync()` exists; `IServer.PingAsync` is an alternative). |

## Open Questions

1. **Where does the latch live for the console (inner container) vs. baseapi (outer chain)?**
   - What we know: console checks run in the inner Kestrel container (folded descriptors); baseapi checks run in the single outer `AddHealthChecks` chain.
   - What's unclear: whether to register the latch as a wrapping descriptor (console) and a singleton-wrapped check (baseapi), or unify.
   - Recommendation: console → wrap inside the `HealthCheckDescriptor.Factory` (per-listener singleton); baseapi → register the latch instance as a singleton and reference it in `.AddCheck`. Verify with the Pitfall-4 test.

2. **Should `LoopLivenessHealthCheck` retain the null-not-started branch given first-beat→startup handoff?**
   - What we know: with startupProbe guarding boot and first-beat marking the gate, `/health/live` should only ever see a beaten state in steady state.
   - Recommendation: keep the null→Unhealthy("not started") branch as defense-in-depth (cheap), matching both retired checks.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build/test | ✓ (project targets net8.0) | 8.0 | — |
| Microsoft.Extensions.TimeProvider.Testing | new hermetic tests (FakeTimeProvider) | ✓ | pinned in `tests/BaseApi.Tests/BaseApi.Tests.csproj` | — |
| StackExchange.Redis | Redis readiness check | ✓ | 2.13.1 | — |
| MassTransit | bus readiness (unchanged) | ✓ | 8.5.5 | — |
| Docker-Desktop k8s cluster (`skp` ns) | live broker-unreachable proof | assumed ✓ (prior phases used it) | — | live proof is the only step needing it; hermetic tests need nothing external |

**Missing dependencies with no fallback:** none identified. **New external packages:** none (recommended path).

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 + NSubstitute + Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (single test project references ALL src projects incl. BaseConsole.Core, Keeper, BaseProcessor.Core) |
| Quick run command | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` (run the .exe directly — `dotnet test` hangs on Windows MTP) |
| Full suite command | same exe, no extra filter (RealStack requires a live stack) |

**Trait rule:** new hermetic tests must NOT carry `[Trait("Category", "RealStack")]`. RealStack tests are excluded by `--filter-not-trait Category=RealStack`. Existing traits use `[Trait("Phase","NN")]` — tag new tests `[Trait("Phase","86")]`.

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| HLTH-01/06 | `LoopLivenessHealthCheck`: fresh beat→Healthy; stopped beats→stale Unhealthy; null→not-started; exact-boundary (k=3) | unit | `...BaseApi.Tests.exe --filter-not-trait Category=RealStack` (namespace filter) | ❌ Wave 0 — model on `Features/Liveness/LivenessWatchdogHealthCheckTests.cs` + `Keeper/Health/KeeperLivenessWatchdogTests.cs` |
| HLTH-01 | `ILivenessHeartbeat.Beat()` stamps; `Current` reads; never-beaten sentinel | unit | same | ❌ Wave 0 — model on `Processor/ProcessorLivenessStateFacts.cs` |
| HLTH-05 | Latch: N consecutive failures → latched → stays Unhealthy after inner recovers; single transient blip resets | unit | same | ❌ Wave 0 (new) |
| HLTH-04/08 | Redis readiness: reachable→Healthy; unreachable/null-mux→Unhealthy; never throws; no secret in body | unit | same | ❌ Wave 0 — model on `Console/ConsoleBusReadyHealthCheckTests.cs` (null-provider path) |
| HLTH-04 | Processor identity+schema readiness: `IsHealthy` false→Unhealthy, true→Healthy | unit | same | ❌ Wave 0 (new; stub `IProcessorContext`) |
| HLTH-02 | `ProcessorStartupOrchestrator` broker-down → caught+logged+retried, never throws out | unit | same | ❌ Wave 0 — model on `Processor/LivenessResilienceFacts.cs` |
| HLTH-03 | Processor beats unconditionally when NOT Healthy; keeper beats even when edge bus-op would hang | unit | same | ❌ Wave 0 — model on `Processor/LivenessHeartbeatFacts.cs` + `Keeper/Health/BitHealthLoopTests.cs` (ScriptedRedis + FakeTimeProvider driver) |
| HLTH-07 | first beat calls `IStartupGate.MarkReady`; keeper no longer registers base `StartupCompletionService` | unit | same | ❌ Wave 0 (new) |
| (regression) | existing green: `ConsoleHealthLiveTests`, `ConsoleBusReadyHealthCheckTests`, `BitHealthLoopTests`, `LivenessHeartbeatFacts` must stay green (or be updated for retired types) | unit | full hermetic run | ✅ exist |
| HLTH-07 (k8s) | startupProbe present on all four manifests; path `/health/startup` | manual/CI grep | `grep -c startupProbe k8s/3{0,1,2,3}-*.yaml` | manual |
| (live proof) | broker unreachable → all four `RESTARTS 0`, go NotReady, recover only on restart | RealStack/manual | `kubectl set env deployment/<svc> -n skp RabbitMq__Host=rabbitmq-unreachable.invalid` (revert with `=rabbitmq`) | manual, NOT hermetic |

### Sampling Rate
- **Per task commit:** run the affected namespace via the hermetic exe filter.
- **Per wave merge:** full hermetic run `BaseApi.Tests.exe --filter-not-trait Category=RealStack`.
- **Phase gate:** full hermetic green + the k8s startupProbe grep + the live broker-unreachable proof (`RESTARTS 0`).

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Console/LoopLivenessHealthCheckTests.cs` — HLTH-01/06 (copy `LivenessWatchdogHealthCheckTests.cs`, k=3, timestamp-only)
- [ ] `tests/BaseApi.Tests/Console/LivenessHeartbeatTests.cs` — HLTH-01 (`ILivenessHeartbeat` stamp/read)
- [ ] `tests/BaseApi.Tests/Console/LatchedReadinessHealthCheckTests.cs` — HLTH-05 (Pitfall 4 sticky-after-recovery)
- [ ] `tests/BaseApi.Tests/Console/RedisReadyHealthCheckTests.cs` — HLTH-04/08 (null-mux + never-throw + no-secret body)
- [ ] `tests/BaseApi.Tests/Processor/IdentitySchemaReadyHealthCheckTests.cs` — HLTH-04
- [ ] `tests/BaseApi.Tests/Processor/StartupOrchestratorResilienceFacts.cs` — HLTH-02 (broker-down never-throws; lock the existing broad catch)
- [ ] Update/retire tests referencing `KeeperLivenessWatchdogHealthCheck`, `IKeeperLivenessState`, `LivenessWatchdogHealthCheck` (they will fail to compile after the delete — `KeeperLivenessWatchdogTests.cs`, `LivenessWatchdogHealthCheckTests.cs` must be removed or ported to the new primitive)
- [ ] Framework install: none — all pinned.

## Security Domain

This phase only touches health-probe surfaces; it does not add auth, crypto, or user input handling. ASVS relevance is narrow:

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | no | probes take no user input |
| V6 Cryptography | no | none |
| V7/V8 Error handling & data protection (info leakage) | **yes** | **No connection strings / stack traces in `HealthCheckResult` descriptions or `Data`** — enforced by existing tests (`ConsoleHealthLiveTests.Live_Body_Has_No_Secrets`, `LivenessWatchdogHealthCheckTests.AssertSummaryDataPresent` info-disclosure guard: no `Password=`, `abortConnect`, `   at `). New Redis/latch/readiness checks MUST carry the same guard — static literal messages only, never the exception detail or connection string. |

| Threat Pattern | STRIDE | Mitigation |
|----------------|--------|------------|
| Probe body leaks Redis/RMQ/Postgres connection string or stack trace | Information Disclosure | Static-literal `HealthCheckResult` messages; assert absence of secret tokens in new hermetic tests (mirror existing guard) |
| Latch never engages (self-heal leaks a should-be-dead pod back to Ready) | (availability/correctness) | Pitfall-4 test: sustained failure → sticky Unhealthy after inner recovery |

## Sources

### Primary (HIGH confidence — direct file reads this session)
- `docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md` — locked design
- `.planning/phases/86-.../86-CONTEXT.md` — distilled decisions + canonical refs
- `src/BaseConsole.Core/Health/{EmbeddedHealthEndpointService,BusReadyHealthCheck,StartupHealthCheck,HealthCheckDescriptor,IStartupGate,StartupCompletionService}.cs`
- `src/BaseConsole.Core/DependencyInjection/{ConsoleRedisServiceCollectionExtensions,ConsoleHealthServiceCollectionExtensions,BaseConsoleServiceCollectionExtensions}.cs`
- `src/BaseProcessor.Core/Liveness/{LivenessWatchdogHealthCheck,ProcessorLivenessHeartbeat,IProcessorLivenessState,ProcessorLivenessState}.cs`, `Startup/ProcessorStartupOrchestrator.cs`, `Identity/IProcessorContext.cs`, `Configuration/ProcessorLivenessOptions.cs`, `DependencyInjection/BaseProcessorServiceCollectionExtensions.cs`
- `src/Keeper/Health/{BitHealthLoop,KeeperLivenessWatchdogHealthCheck,IKeeperLivenessState,KeeperLivenessState}.cs`, `Keeper/Program.cs`, `Keeper/ProbeOptions.cs`
- `src/BaseApi.Core/DependencyInjection/{HealthServiceCollectionExtensions,MessagingServiceCollectionExtensions,RedisServiceCollectionExtensions}.cs`, `Health/StartupCompletionService.cs`
- `src/Orchestrator/Program.cs`
- `k8s/{30-baseapi-service,31-orchestrator,32-keeper,33-processor-sample}.yaml`
- `tests/BaseApi.Tests/Features/Liveness/LivenessWatchdogHealthCheckTests.cs`, `tests/BaseApi.Tests/Keeper/Health/{KeeperLivenessWatchdogTests,BitHealthLoopTests}.cs`, `tests/BaseApi.Tests/Console/{ConsoleHealthLiveTests,ConsoleBusReadyHealthCheckTests}.cs`, `tests/BaseApi.Tests/BaseApi.Tests.csproj`, `Directory.Packages.props`, `.planning/config.json`

### Secondary (MEDIUM)
- Project memory notes (hermetic-test command, k8s local-image staleness, stack bring-up k8s-only)

### Tertiary (LOW)
- None — no WebSearch needed; all findings are codebase-verified.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all packages/APIs verified as already referenced; MassTransit `CheckHealth()` + StackExchange.Redis singleton confirmed in current code
- Architecture / change points: HIGH — every file read directly; the `HealthCheckDescriptor` seam is confirmed generic and already the opt-in mechanism
- Pitfalls: HIGH — Pitfalls 1 & 2 are the documented original bugs; 3-6 derive from read code + project memory
- Test scaffolds: HIGH — the exact mirror files exist and use the same `FakeTimeProvider`/stub-provider idiom

**Research date:** 2026-07-26
**Valid until:** ~2026-08-25 (stable internal codebase; no fast-moving external deps)
