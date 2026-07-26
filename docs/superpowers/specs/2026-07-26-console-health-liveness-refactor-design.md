# Console & WebApi Health / Liveness Refactor — Design

**Date:** 2026-07-26
**Status:** Approved (design), pending implementation plan
**Scope:** `BaseConsole.Core`, `BaseProcessor.Core`, `Keeper`, `Orchestrator`, `BaseApi.Core`, k8s manifests

---

## 1. Problem

Under an infrastructure outage (RabbitMQ made unreachable in a live experiment), the four app services behaved inconsistently:

- **orchestrator, baseapi** — stayed running (correct).
- **keeper, processor** — **crash-looped**: their `/health/live` probe returned 503, so kubelet killed and restarted the pods (Exit 137 / SIGTERM), ~90–135s into the outage.

Root cause: the keeper and processor fold a **bus-entangled self-watchdog onto `/health/live`**, so an infrastructure failure trips *liveness* (which triggers a restart) instead of only *readiness* (which does not).

- **Processor** — `ProcessorLivenessHeartbeat` stamps the liveness timestamp *only when the replica is already "Healthy"*, which requires bus-based identity+schema resolution. Broker down → never Healthy → never stamps → `LivenessWatchdogHealthCheck` reports `"liveness loop not started"`.
- **Keeper** — `BitHealthLoop` stamps unconditionally, but its edge block awaits `ReceiveEndpoint.Start(ct).Ready` + `bus.Publish(ResumeAll)` — **bus calls that hang when the broker is unreachable** — so the loop freezes mid-tick and the stamp goes stale → `"BIT loop stale"`.

`/health/live` is therefore answering a *readiness* question ("am I connected to the broker / fully bootstrapped?") instead of a *liveness* question ("is my critical loop still turning?"). That is the category error this refactor corrects.

## 2. Goals (requirements)

- **R1 — Liveness = watchdog-loop liveness.** `/health/live` checks only "is the critical loop still iterating?" A silently-dead loop is a genuine fault; **restart is the only remedy**.
- **R2 — Infra never crashes the app.** Postgres / Redis / RabbitMQ failures are caught, **logged**, and never crash the process or trip liveness — *including inside the processor identity+schema loop*.
- **R3 — One shared design in BaseConsole** for orchestrator / keeper / processor. WebApi keeps its own (`BaseApi.Core`) pattern.
- **R4 — The processor has extra "become healthy" work** (identity + schema resolution). That work is a **readiness** concern; only the *loop's staleness* is a liveness concern.
- **No self-heal.** Recovery from a sustained infra outage is by **operator restart**, not automatic reconnection.

## 3. The three probe roles (uniform contract)

HTTP mapping (unchanged): worst status wins; **Unhealthy → 503**, **Healthy/Degraded → 200**.

| Probe | Question it answers | Reacts to infra? | Effect on failure |
|---|---|---|---|
| `/health/startup` | Has one-time boot finished (and has the watchdog loop beaten at least once)? | webapi: yes (migrations); consoles: no | gate live/ready until done; **migration failure stays failed → restart** |
| `/health/ready` | Are my **required** infra deps reachable, and am I bootstrapped? | **yes** | NotReady — **never restart**; recover by restart (§6 latch) |
| `/health/live` | Is my watchdog loop still turning? | **no** | **restart** (only cure for a silently-dead loop) |

## 4. Per-service checks (target)

| Service | `/health/live` | `/health/ready` (required deps) | `/health/startup` |
|---|---|---|---|
| **orchestrator** | `self` only — **no watchdog** (keeps today's behaviour; no continuous critical loop to monitor) | bus + **Redis (new)** | hydration complete |
| **keeper** | `self` + **shared loop-watchdog** (BIT loop beats it) | bus + **Redis (new)** | first beat |
| **processor** | `self` + **shared loop-watchdog** (identity/heartbeat loop beats it) | bus + **Redis (new)** + **identity+schema resolved (new)** | first beat |
| **baseapi** (webapi base; own pattern) | `self` only | Postgres + **Redis (new)** + bus **capped at Degraded** (publish-only, soft) | migrations (one-shot) |

**Readiness principle (Option B — required deps, not "all infra"):** each service's readiness reflects the deps it *needs to do its job*. RabbitMQ is a **hard** dep for the consoles (no bus → cannot dispatch/recover/process) so a broker outage makes them NotReady. For **baseapi** the bus is **publish-only / soft** — CRUD works without it — so the existing `MinimalFailureStatus = Degraded` cap stays (broker down → Degraded → still Ready). This is a deliberate per-service inconsistency, not a bug.

**Orchestrator keeps no watchdog (R3 reconciliation):** BaseConsole *provides* the watchdog primitive; a service *opts in* only if it has a real critical loop. Orchestrator has none, so it registers no beat and keeps `self`-only liveness. Accepted trade-off: the orchestrator has **no liveness-based deadlock detection** — acceptable because it is HA / leader-elected.

**Startup-gate marking (what flips `IStartupGate.IsReady`, gating the new `startupProbe`):** the mechanism (`StartupHealthCheck` → `IStartupGate`) is shared; the *trigger* is per-service — **orchestrator:** hydration complete (existing `HydrationBackgroundService.MarkReady`); **keeper / processor:** the watchdog loop's **first beat** (new — the gate is marked ready the first time the loop beats, so `/health/live` never has to evaluate a "never beaten" state once the startupProbe has handed off); **baseapi:** migrations complete (existing). This keeps the "not-yet-started" window on `startup`, so steady-state `live` only ever distinguishes "turning" from "stale".

## 5. Shared liveness watchdog primitive (R1, R3)

Retire `Keeper.Health.KeeperLivenessWatchdogHealthCheck` and `BaseProcessor.Core.Liveness.LivenessWatchdogHealthCheck`. Replace with **one** primitive in `BaseConsole.Core`, inherited by all consoles and opt-in per service:

- **`ILivenessHeartbeat`** singleton — `Beat()` stamps a `TimeProvider` timestamp; `Current` reads it. **Pure in-memory, zero I/O.**
- **`LoopLivenessHealthCheck`** (tag `"live"`), registered via `AddBaseConsole` only for services that opt in. **Stale iff** `now − lastBeat > k × interval` (default **k = 3**; today's watchdogs use k = 2 — the extra margin avoids false-tripping on one slow tick under the new model; finalize in planning).

**Integration rules:**

- **Beat at the top of every critical-loop iteration, before any infra call, unconditionally.** Then:
  - infra down → loop still enters the iteration, **beats**, attempts the infra op, catches + **logs**, delays, repeats → `live` stays healthy → **no restart** (R2).
  - loop genuinely wedged / dead → beats stop → stale → **restart** (R1).
- **No unbounded blocking on the beat path.** Every infra call inside a beating loop must be timeout/cancellation-bounded so a *hung* dependency cannot freeze the iteration and starve the beat.
  - **Keeper fix:** move `ReceiveEndpoint.Start().Ready` / `bus.Publish(ResumeAll)` off the beat path (or wrap with a bounded wait + catch-log) so a dead broker cannot hang the `BitHealthLoop` tick.
- **Processor fix:** beat **unconditionally** at the top of the heartbeat loop — remove the "only when Healthy" gate from the *liveness beat* (keep that gate only for the *L2 liveness write*, which is a separate business signal). Bootstrapping against a down bus then keeps beating → no restart.

## 6. "No self-heal" — hard latch (B1)

MassTransit and StackExchange.Redis (`abortConnect=false`) reconnect automatically, so a pod would silently return to Ready when infra recovers — self-heal we do **not** want. Enforcement:

- **Latch readiness.** Once a **required** dep has failed for its readiness `failureThreshold` window (so transient blips are ignored), **latch the readiness check Unhealthy and keep it latched until the process restarts** — even if the underlying client reconnects. Operator sees sustained NotReady + logs → fixes infra → **restarts** → clean boot.
- **Migration stays one-shot.** `StartupCompletionService` continues to run `MigrateAsync()` once; failure logs `LogCritical` and leaves the startup gate Unhealthy until restart. No retry-to-success.
- Loops still retry + log **to stay alive and surface logs** (R2) — not to guarantee recovery.

Latch mechanism: a small `BaseConsole.Core` wrapper around each *required-dep* readiness check that, once it observes sustained failure, flips a per-process sticky flag consumed by the check thereafter. (Applies to console bus-ready + Redis, and baseapi Postgres + Redis. The baseapi bus check is soft/Degraded and is **not** latched.)

## 7. Infra-exception invariant (R2)

Every Redis / RabbitMQ / Postgres call sits inside `try { … } catch (infra ex) { log; continue/retry }` on every loop — including `ProcessorStartupOrchestrator`'s identity/schema loop (already compliant: its broad `catch (Exception ex) when (ex is not OperationCanceledException)` catches broker-unreachable, logs a warning, and retries on backoff). No infra fault reaches `IHostedService.StartAsync`/`ExecuteAsync` unhandled. `OperationCanceledException` from shutdown is the one deliberate pass-through. Locked by tests.

## 8. k8s manifest changes

- **Add a `startupProbe` → `/health/startup`** to all four services (currently absent) so boot has its own grace window and steady-state liveness can be trusted/tightened without killing slow starts.
- Redis readiness is now meaningful, so a `/health/ready` failure is a real "a required infra dep is down" signal.
- Liveness restart latency stays the tunable `periodSeconds × failureThreshold` (today 15s × 6 = 90s); revisit after the startupProbe lands.

## 9. Code surface (indicative)

- **`BaseConsole.Core`** — new `ILivenessHeartbeat` + `LoopLivenessHealthCheck` + opt-in registration; new Redis readiness check; readiness **latch** wrapper.
- **`Keeper`** — beat in `BitHealthLoop`; unblock the edge bus-ops (bounded/off-path); delete `KeeperLivenessWatchdogHealthCheck`.
- **`BaseProcessor.Core`** — beat unconditionally in `ProcessorLivenessHeartbeat`; new identity+schema readiness check; delete `LivenessWatchdogHealthCheck`.
- **`Orchestrator`** — no watchdog (unchanged live); gains Redis readiness + latch.
- **`BaseApi.Core`** — new Redis readiness check; keep migration one-shot; bus stays capped.
- **k8s** — add `startupProbe` to `30/31/32/33-*.yaml`.

## 10. Testing

- Hermetic unit tests for `LoopLivenessHealthCheck` (beats → healthy; stopped beats → stale) and the latch (sustained failure → latched → stays latched after "recovery").
- Regression: `ProcessorStartupOrchestrator` broker-down → caught+logged+retried, never throws out (R2).
- Live re-run of the original experiment: broker unreachable → all four **stay running** (`RESTARTS 0`), go NotReady, show logs; recover only on restart.

## 11. Out of scope

- Changing MassTransit/Redis client reconnect behaviour (we latch above them, we don't disable them).
- Orchestrator deadlock detection (deliberately none).
- Readiness liveness-latency tuning beyond adding the startupProbe.
