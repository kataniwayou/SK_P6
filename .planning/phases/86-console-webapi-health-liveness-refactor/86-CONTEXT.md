# Phase 86: Console & WebApi Health/Liveness Refactor - Context

**Gathered:** 2026-07-26
**Status:** Ready for planning
**Source:** PRD Express Path (docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md)

<domain>
## Phase Boundary

Refactor the health probes of the three consoles (orchestrator, keeper, processor) and the WebApi (baseapi) so that an infrastructure outage (Postgres / Redis / RabbitMQ) never crashes the process nor restarts the pod — **only a genuinely stalled watchdog loop restarts**. Realized by: one shared `BaseConsole.Core` liveness-watchdog primitive (opt-in), a hard readiness latch (no self-heal), Redis added to readiness, processor identity+schema on readiness, and new k8s startupProbes. WebApi keeps its own `BaseApi.Core` pattern.

This was produced via a full brainstorming session on 2026-07-26; the design spec's decisions are **locked and authoritative** — do not re-open them.
</domain>

<decisions>
## Implementation Decisions

### Probe roles (uniform contract; HTTP: Unhealthy→503, Healthy/Degraded→200)
- `/health/startup` = one-time boot done (webapi: migrations; consoles: no infra). Wire a **new** `startupProbe` on all four (currently absent).
- `/health/ready` = each service's **required** infra deps reachable + (processor) identity+schema resolved. NotReady, **never restart**.
- `/health/live` = is my watchdog loop still turning? Restart is the sole remedy for a silently-dead loop; **never reacts to infra**.

### Shared liveness watchdog primitive (BaseConsole.Core) — HLTH-01, HLTH-03, HLTH-06
- New `ILivenessHeartbeat` singleton (`Beat()` stamps a `TimeProvider` timestamp; pure in-memory, zero I/O) + `LoopLivenessHealthCheck` (tag `"live"`), registered via `AddBaseConsole` **opt-in per service**. Stale iff `now − lastBeat > k × interval`, **default k=3** (today's watchdogs use k=2).
- **Beat at the top of every critical-loop iteration, before any infra call, unconditionally.**
- **No unbounded blocking on the beat path.** Keeper: move `ReceiveEndpoint.Start().Ready` / `bus.Publish(ResumeAll)` off the beat path (bounded/off-tick) so a dead broker cannot freeze `BitHealthLoop`.
- **Keeper + processor opt in.** **Orchestrator stays `self`-only (NO watchdog)** — verified it has no beatable steady-state loop (hydration exits after boot; the leader-election renewal loop is library-internal + in-cluster-only + transition-callback-only). Accepted: orchestrator has no liveness-based deadlock detection.
- **Retire** `Keeper.Health.KeeperLivenessWatchdogHealthCheck` and `BaseProcessor.Core.Liveness.LivenessWatchdogHealthCheck`.
- **Processor beats unconditionally** — drop the "only when Healthy" gate from the `/health/live` beat; keep that gate only for the L2 liveness write.

### Readiness — Option B (required deps, not "all infra") — HLTH-04, HLTH-08
- Consoles: bus is a **hard** dep → broker down = NotReady. Add **Redis** to readiness for all services (currently on no probe).
- Processor readiness also includes **identity+schema resolved** (pure in-memory read of `IProcessorContext`/`IProcessorLivenessState`; never throws).
- BaseApi: the bus is **publish-only / soft** → keep `MinimalFailureStatus = Degraded` cap (broker down → Degraded → still Ready). BaseApi bus is **NOT latched**.

### No self-heal → hard readiness latch — HLTH-05
- Once a **required** dep has failed for its readiness `failureThreshold` window (transient blips ignored), **latch `/health/ready` Unhealthy until process restart** — even if the client reconnects. Recovery = operator restart.
- **Migration stays one-shot** (`StartupCompletionService` — no retry-to-success).
- Loops still retry + log to stay alive and surface logs — not to guarantee recovery.

### Infra-exception invariant — HLTH-02
- Every Redis/RabbitMQ/Postgres call is caught + logged and never crashes the process — including `ProcessorStartupOrchestrator`'s identity/schema loop (already compliant: broad `catch (Exception ex) when (ex is not OperationCanceledException)`; **lock with a test**). `OperationCanceledException` from shutdown is the one deliberate pass-through.

### Startup-gate marking + k8s — HLTH-07
- `IStartupGate.IsReady` trigger per service: **orchestrator** = hydration complete (existing `HydrationBackgroundService.MarkReady`); **keeper / processor** = **first watchdog beat** (new); **baseapi** = migrations (existing).
- Add `startupProbe → /health/startup` to all four k8s deployments (`k8s/30/31/32/33-*.yaml`).

### Claude's Discretion (implementation details)
- Exact `ILivenessHeartbeat` API shape, the latch wrapper's storage/reset mechanism, per-service beat interval values (respect current: processor 10s→stale 20-30s, keeper 5s→stale 10-15s under k=3), startupProbe numeric config, and file/namespace placement — follow existing `BaseConsole.Core/Health` patterns.
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Design spec (authoritative — the PRD for this phase)
- `docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md` — every locked decision, per-service check tables, the shared watchdog primitive, the latch, and testing/rollout.

### Current health wiring to mirror / modify
- `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` — the console embedded listener; maps `/health/live|ready|startup` by tag (`self`/`bus-ready`/`startup` + folded `HealthCheckDescriptor`s).
- `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs` — bus readiness (Degraded|Unhealthy→Unhealthy); `StartupHealthCheck` — `IStartupGate`.
- `src/BaseConsole.Core/DependencyInjection/ConsoleRedisServiceCollectionExtensions.cs` — Redis singleton (`abortConnect=false`, lazy, no health check today).
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` (~L145-155) — the `HealthCheckDescriptor` seam; `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` + `ProcessorLivenessHeartbeat.cs`; `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` (identity/schema loop).
- `src/Keeper/Health/BitHealthLoop.cs` + `KeeperLivenessWatchdogHealthCheck.cs`; `src/Keeper/Program.cs` (descriptor registration).
- `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs` + `MessagingServiceCollectionExtensions.cs` (bus `MinimalFailureStatus=Degraded`) + `RedisServiceCollectionExtensions.cs`; `src/BaseApi.Core/Health/StartupCompletionService.cs` (one-shot migration).
- `k8s/30-baseapi-service.yaml`, `k8s/31-orchestrator.yaml`, `k8s/32-keeper.yaml`, `k8s/33-processor-sample.yaml` — probe blocks (add `startupProbe`).
</canonical_refs>

<specifics>
## Specific Ideas

- Live proof = repeat the broker-unreachable experiment: `kubectl set env deployment/<svc> -n skp RabbitMq__Host=rabbitmq-unreachable.invalid` on all four → all stay `RESTARTS 0`, go NotReady, show logs; revert with `RabbitMq__Host=rabbitmq`.
- Hermetic tests: run `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe` directly (dotnet test hangs on Windows MTP), `--filter-not-trait Category=RealStack`. **New tests must NOT be `Category=RealStack`.**
- k8s local-image staleness on rebuild is a known trap — deploy under a unique tag + `kubectl set image`, run harness `-SkipBringUp` with manual port-forwards.
</specifics>

<deferred>
## Deferred Ideas

- Orchestrator liveness/deadlock detection (would need an own lease-poll loop to beat from) — explicitly out of scope.
- Making migration self-heal (retry-to-success) — out of scope; migration stays a one-shot restart gate.
- Disabling MassTransit/Redis client auto-reconnect — out of scope; we latch above the clients, not disable them.
</deferred>

---

*Phase: 86-console-webapi-health-liveness-refactor*
*Context gathered: 2026-07-26 via PRD Express Path*
