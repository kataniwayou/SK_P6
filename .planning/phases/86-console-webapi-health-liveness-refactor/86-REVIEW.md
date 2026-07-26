---
phase: 86-console-webapi-health-liveness-refactor
reviewed: 2026-07-26T19:08:10Z
depth: standard
files_reviewed: 14
files_reviewed_list:
  - src/BaseConsole.Core/Health/ILivenessHeartbeat.cs
  - src/BaseConsole.Core/Health/LivenessHeartbeat.cs
  - src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs
  - src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs
  - src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs
  - src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs
  - src/BaseConsole.Core/DependencyInjection/ConsoleLivenessServiceCollectionExtensions.cs
  - src/BaseApi.Core/Health/ApiRedisReadyHealthCheck.cs
  - src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs
  - src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs
  - src/BaseApi.Core/DependencyInjection/RedisServiceCollectionExtensions.cs
  - src/BaseProcessor.Core/Liveness/ProcessorIdentitySchemaReadyHealthCheck.cs
  - src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/Keeper/Health/BitHealthLoop.cs
  - src/Keeper/Program.cs
findings:
  critical: 1
  warning: 6
  info: 3
  total: 10
status: fixed
fixed_at: 2026-07-26T00:00:00Z
---

# Phase 86: Code Review Report

**Reviewed:** 2026-07-26T19:08:10Z
**Depth:** standard (with targeted cross-file/decompilation verification for the info-disclosure finding)
**Files Reviewed:** 14 (16 listed in required reading; `CLAUDE.md` not present at repo root, no skills directory found)
**Status:** issues_found

## Summary

The core primitives (`ILivenessHeartbeat`/`LivenessHeartbeat`, `LoopLivenessHealthCheck`, `RedisReadyHealthCheck`/`ApiRedisReadyHealthCheck`) are implemented correctly: the Interlocked long-ticks heartbeat has no torn reads, the k=3 staleness boundary math is correct (strict `>=`, matches the documented "boundary is stale" contract), the Redis pings are genuinely bounded and never-throw, and `BitHealthLoop`'s beat is unconditionally the first statement in the tick loop, ahead of the edge-triggered bus I/O — the intended fix for the original keeper starvation bug.

However, one systemic, well-evidenced **Critical** finding undermines the phase's own stated info-disclosure goal: `LatchedReadinessHealthCheck` / `ApiLatchedReadinessHealthCheck` pass the wrapped check's raw `HealthCheckResult` straight through to the HTTP response for every consecutive failure *before* the threshold is reached, and at least two of the wrapped inner checks (`BusReadyHealthCheck`, the third-party `NpgSqlHealthCheck`) are confirmed to attach a raw exception to that result, which the confirmed `UIResponseWriter.WriteHealthCheckUIResponse` (exception-including variant, verified by binary inspection against the exception-*excluding* sibling method `WriteHealthCheckUIResponseNoExceptionDetails` that ships in the same package) will serialize into the `/health/ready` body.

Several further **Warning**-level issues cover: a boundedness gap in `BitHealthLoop`'s own doc claims (the claim only covers edge bus-ops, not the per-tick L2 probe itself), config/constant drift risk for the hardcoded liveness interval literals, and three places where documentation was not updated to match the actual Phase-86 behavior change (two of which describe genuinely "load-bearing" invariants that the new code silently supersedes).

## Critical Issues

### CR-01: Latched readiness checks leak raw dependency-failure detail (including attached exceptions) before the latch trips

**File:** `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs:63-79`
**File:** `src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs:34-61`

**Issue:**
Both latch decorators only substitute the sanitized, static `LatchedMessage` **after** `_consecutiveFailures >= failureThreshold`. For every consecutive-failure evaluation *before* that point (1..`failureThreshold-1`, i.e. up to 4 polls with the default threshold of 5), the wrapper returns the **inner check's result unmodified**:

```csharp
// LatchedReadinessHealthCheck.cs:63-75
var result = await _inner.CheckHealthAsync(context, cancellationToken).ConfigureAwait(false);
if (result.Status == HealthStatus.Unhealthy)
{
    if (Interlocked.Increment(ref _consecutiveFailures) >= _failureThreshold)
    {
        _latched = true;
        return HealthCheckResult.Unhealthy(LatchedMessage);
    }
    return result;   // <-- raw inner result returned verbatim, pre-latch
}
```

This is fine for the phase's own hand-rolled checks (`RedisReadyHealthCheck`, `ApiRedisReadyHealthCheck`) because their doc comments and code are meticulous about never attaching an exception or dynamic detail — every branch returns a STATIC literal. But two of the checks this exact wrapper is used to protect are **not** sanitized:

1. `src/BaseConsole.Core/Health/BusReadyHealthCheck.cs:64` — `HealthCheckResult.Unhealthy(result.Description, result.Exception)` — attaches the MassTransit `BusHealthResult.Exception` (e.g. a RabbitMQ connection fault) directly. This is wrapped by `LatchedReadinessHealthCheck` in `EmbeddedHealthEndpointService.cs:85` (`latchedBusReady`).
2. `src/BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs:51-53` — wraps the third-party `HealthChecks.NpgSql` `NpgSqlHealthCheck` in `ApiLatchedReadinessHealthCheck`. The Xabaril `NpgSqlHealthCheck` implementation (confirmed by the package's standard pattern across its health-check family) returns `new HealthCheckResult(failureStatus, exception: ex)` on any connection/auth fault — i.e. the raw `NpgsqlException` (which can include host, port, database name, and auth-failure detail) is attached, not a static string.

Both `/health/ready` endpoints render results with the **exception-including** JSON writer, confirmed for both consoles and the API:
- `src/BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs:112-116` — `ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse`
- `src/BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs:51-55` — same

I verified by inspecting the installed `AspNetCore.HealthChecks.UI.Client` 9.0.0 assembly that `WriteHealthCheckUIResponse` is a **distinct** method from a sibling `WriteHealthCheckUIResponseNoExceptionDetails` shipped in the same package specifically for callers who want to avoid this exact leak — the code under review uses the leaking variant everywhere.

Net effect: during a sustained Postgres or RabbitMQ outage, the first `failureThreshold - 1` polls of `/health/ready` (a Kubernetes-probed, and potentially externally-reachable, endpoint) will include the raw driver exception message — which can disclose internal hostnames, ports, database/queue names, and auth-failure detail — directly contradicting this phase's own explicitly documented info-disclosure guard (repeated verbatim in `RedisReadyHealthCheck.cs:27-31`, `LoopLivenessHealthCheck.cs:33-36`, `ApiRedisReadyHealthCheck.cs:33-36`) that "the connection string and the raw exception detail are NEVER placed in the message or Data." That guard was applied to every check the phase authors *wrote*, but not to the pre-existing checks the new latch wrapper *wraps*.

**Fix:** sanitize inside the latch wrapper itself, not just at the latched terminus, e.g.:
```csharp
var result = await _inner.CheckHealthAsync(context, cancellationToken).ConfigureAwait(false);
if (result.Status == HealthStatus.Unhealthy)
{
    if (Interlocked.Increment(ref _consecutiveFailures) >= _failureThreshold)
        _latched = true;
    // never forward the inner result's Description/Exception — always return a static message
    return HealthCheckResult.Unhealthy(_latched ? LatchedMessage : UnhealthyMessage);
}
Interlocked.Exchange(ref _consecutiveFailures, 0);
return HealthCheckResult.Healthy();
```
or, if the un-latched detail is wanted for operator diagnosis, switch both `/health/ready` endpoint registrations to `UIResponseWriter.WriteHealthCheckUIResponseNoExceptionDetails`.

## Warnings

### WR-01: BitHealthLoop's documented boundedness only covers edge bus-ops, not the per-tick L2 probe itself

**File:** `src/Keeper/Health/BitHealthLoop.cs:34-41, 73`

**Issue:** The class comment (lines 34-41) and the in-loop comments (lines 57-61) claim the top-of-tick beat plus the `EdgeOpTimeout`-bounded `WaitAsync` calls mean "a hung broker can no longer starve the beat and trip a false /health/live restart." That claim is only true for the edge-triggered `Start`/`Stop`/`Publish` calls. `await probe.ProbeOnceAsync(stoppingToken)` (line 73) runs **unconditionally every tick**, before the edge block, and is **not** wrapped in any `WaitAsync`/timeout at this call site. Inspecting the callee (`src/Keeper/Recovery/L2ProbeRecovery.cs`, outside this review's file list but directly load-bearing here) shows its `CancellationToken ct` parameter is entirely unused — none of `StringGetAsync`/`StringSetAsync`/`KeyDeleteAsync` are cancellation-bound or time-boxed by this code; boundedness depends entirely on `IConnectionMultiplexer`'s own internal (unconfigured-here) sync/async timeouts. If a probe call ever blocks longer than `k × intervalSeconds` (15s), the very next `heartbeat.Beat()` (which only fires after `ProbeOnceAsync` returns) is starved and `/health/live` will go stale — exactly the failure mode this phase was built to eliminate, just relocated one call earlier than the fix covers.

**Fix:** wrap the probe call itself with the same `WaitAsync(EdgeOpTimeout, stoppingToken)` pattern used for the edge ops (or a slightly larger explicit bound), and/or thread a hard command timeout through `L2ProbeRecovery.ProbeOnceAsync`'s currently-unused `ct` parameter so a wedged Redis connection cannot silently exceed the staleness window.

### WR-02: Liveness interval baked as a hardcoded literal, independent of the config value that governs the actual loop cadence

**File:** `src/Keeper/Program.cs:62` — `builder.Services.AddConsoleLivenessWatchdog(intervalSeconds: 5);`
**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:153` — `services.AddConsoleLivenessWatchdog(intervalSeconds: 10);`

**Issue:** `LoopLivenessHealthCheck`'s staleness window is `intervalSeconds × k`. The literal `5`/`10` passed here is a second, independent source of truth from the actual tick cadence: Keeper's `BitHealthLoop` reads its delay from `IOptions<ProbeOptions>.Value.DelaySeconds` (config-bound, `Probe:DelaySeconds`), and the processor's `ProcessorLivenessHeartbeat` reads `IOptions<ProcessorLivenessOptions>.Value.IntervalSeconds` (config-bound, `Processor:IntervalSeconds`, `BaseProcessorServiceCollectionExtensions.cs:115`). Both currently default to the same numbers (5 and 10 respectively) so nothing is broken today, but an operator who overrides either config value (e.g. via a k8s env var / ConfigMap, which this codebase clearly uses extensively for tuning) without also editing and redeploying the corresponding C# literal will silently desynchronize the staleness math from the real tick period — in the worst case narrowing or eliminating the k=3 grace margin and causing spurious liveness restarts, or in the other direction weakening the "restart on genuine stall" guarantee.

**Fix:** derive the interval passed to `AddConsoleLivenessWatchdog` from the same bound options object at the point the loop's own period is computed, rather than a separately-maintained literal (e.g. resolve `IOptions<ProbeOptions>`/`IOptions<ProcessorLivenessOptions>` once at composition-root time, or have the watchdog registration read the same config key directly).

### WR-03: Stale file-level documentation contradicts the actual (correct) code in Keeper/Program.cs

**File:** `src/Keeper/Program.cs:14-18` vs `src/Keeper/Program.cs:24-33`

**Issue:** The top-of-file comment block states: "Keeper mirrors Orchestrator MINUS ... MINUS the default-readiness-service removal ... the base library's default readiness service is KEPT here, not stripped." Immediately below, the actual (new, Phase-86) code does the opposite — it explicitly removes `StartupCompletionService`:
```csharp
// lines 28-33
foreach (var d in builder.Services
             .Where(d => d.ImplementationType == typeof(StartupCompletionService))
             .ToList())
{
    builder.Services.Remove(d);
}
```
The code is almost certainly correct (and is consistent with the local Phase-86 comment directly above it explaining *why* it now removes the service); the top-of-file summary simply was not updated for this phase's change and now actively misleads a reader about Keeper's readiness/startup wiring.

**Fix:** update the top-of-file summary to reflect that Phase 86 now also strips `StartupCompletionService` from Keeper (mirroring Orchestrator/Processor), removing the now-false "KEPT here, not stripped" claim.

### WR-04: Documentation for the processor startup-gate invariant is now stale/contradictory across two files

**File:** `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs:96-100`
**File:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:34-36`
**File:** `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:49-59` (context, not in review file list but directly relevant)

**Issue:** `BaseProcessorServiceCollectionExtensions.cs:34-36` and `ProcessorStartupOrchestrator.cs:49-59` both describe, as a still-current, "load-bearing" invariant: `IStartupGate.MarkReady()` fires only once the processor's identity+schema resolve (or hits a terminal Gate-A clash) — explicitly "NOT at bare host start" (`BaseProcessorServiceCollectionExtensions.cs:36`) and "/startup and /ready flip green HERE, not at bare host start" (`ProcessorStartupOrchestrator.cs:59`).

Phase 86 adds a second, competing call site: `ProcessorLivenessHeartbeat.ExecuteAsync` now calls `_gate.MarkReady()` unconditionally on the very first loop tick (lines 96-99), which fires within milliseconds of process start — independent of `IProcessorContext.IsHealthy` and requiring no bus connectivity. Because `IStartupGate.MarkReady()` is a one-way idempotent latch (`src/BaseConsole.Core/Health/IStartupGate.cs:44`) and both `ProcessorStartupOrchestrator` and `ProcessorLivenessHeartbeat` are registered as hosted services that begin running concurrently at host start, the heartbeat's near-instant first beat will, in practice, always win the race and flip `/health/startup` to Ready long before identity/schema resolution completes — making `ProcessorStartupOrchestrator`'s own `gate.MarkReady()` calls (lines 229, 291) effectively dead code for the purpose of gating `/health/startup`.

This appears to be an *intentional* behavior change — `k8s/33-processor-sample.yaml:78-80` explicitly documents the new contract ("startup-gate probe holds off readiness/liveness until boot completes (**first beat**)") and sizes the startupProbe's grace window around it — and the actual runtime safety nets hold (`/health/live` depends only on the heartbeat's `Beat()`, not the gate; `/health/ready` is independently gated by the un-latched `ProcessorIdentitySchemaReadyHealthCheck` reading `IsHealthy`). But the two older doc blocks describing the "load-bearing order" as `MarkReady` happening only post-Healthy were not updated to acknowledge the new first-beat short-circuit, leaving two contradictory descriptions of the same "load-bearing" contract in the codebase — a real risk for a future maintainer who edits `ProcessorStartupOrchestrator` trusting the stale invariant.

**Fix:** update `BaseProcessorServiceCollectionExtensions.cs:34-36` and `ProcessorStartupOrchestrator.cs:49-59` to acknowledge that `/health/startup` now flips on the liveness heartbeat's first beat (Phase 86), and that `ProcessorStartupOrchestrator`'s own `MarkReady()` calls are now redundant/defensive rather than the sole gate.

### WR-05: RedisReadyHealthCheck / ApiRedisReadyHealthCheck's "~2s bound" does not cover first-time multiplexer construction

**File:** `src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs:38-40, 53, 63-66`
**File:** `src/BaseApi.Core/Health/ApiRedisReadyHealthCheck.cs:50, 58-60`

**Issue:** Both checks document (and correctly implement) a bounded window around `PingAsync()`. But `_outer.GetService<IConnectionMultiplexer>()` is the check's *first* line of work, and `IConnectionMultiplexer` is registered as `services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(connStr))` in both `ConsoleRedisServiceCollectionExtensions.cs` and `RedisServiceCollectionExtensions.cs:61-62` (the latter is in this review's scope) — a **synchronous, blocking** connect on first resolution, explicitly documented as having "no pre-warm IHostedService" (`RedisServiceCollectionExtensions.cs:43-45`, D-17). If this health check is the first component in the process to ever resolve `IConnectionMultiplexer` (plausible for BaseApi, which — unlike Keeper/Processor — has no other background loop that touches Redis at boot), `GetService<IConnectionMultiplexer>()` itself can block for the multiplexer's own (unbounded-by-this-check) connect timeout before the declared 2-second `PingTimeout` window even starts, which is not reflected in either file's "bounded ping deadline" claim.

**Fix:** either explicitly pre-warm/pre-resolve the multiplexer in a lightweight hosted service (making first resolution deterministic and off the request/probe path), or wrap the `GetService<IConnectionMultiplexer>()` call itself in the same bounded timeout used for the ping.

### WR-06: Stale "RED skeleton" TDD documentation left in shipped GREEN code

**File:** `src/BaseConsole.Core/Health/LivenessHeartbeat.cs:16-19`
**File:** `src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs:38-40`
**File:** `src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs:32-34`
**File:** `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs:27-30`

**Issue:** Each of these classes retains an XML-doc paragraph describing its "RED skeleton" behavior ("`CheckHealthAsync` returns a static Unhealthy(\"NOT IMPLEMENTED\")…", "both members throw `NotImplementedException`…") from the TDD RED phase. The shipped implementation below each paragraph is fully implemented and does neither of these things — the paragraphs are now factually false and were evidently left in place across the 86-03/86-04 GREEN commits. This is confusing for any future reader trying to understand current behavior from the doc comments (they will momentarily believe the check is an unimplemented stub).

**Fix:** delete the "RED skeleton" paragraphs (or replace with a one-line changelog note) now that the GREEN implementation has landed.

## Info

### IN-01: `ConfigureAwait(false)` inconsistency between BaseConsole.Core and BaseApi.Core mirrors

**File:** `src/BaseApi.Core/Health/ApiRedisReadyHealthCheck.cs:60`
**File:** `src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs:44`
**Issue:** The BaseConsole.Core originals (`RedisReadyHealthCheck.cs:66`, `LatchedReadinessHealthCheck.cs:63`) consistently use `.ConfigureAwait(false)`; their BaseApi.Core mirrors omit it. Harmless under ASP.NET Core (no sync-context to deadlock on) but an avoidable inconsistency between two files explicitly documented as mirrors of each other.
**Fix:** add `.ConfigureAwait(false)` to the two calls for consistency.

### IN-02: Duplicated latch/Redis-check implementations across BaseConsole.Core and BaseApi.Core

**File:** `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs`, `src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs` vs `src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs`, `src/BaseApi.Core/Health/ApiRedisReadyHealthCheck.cs`
**Issue:** Near byte-identical logic exists in two assemblies (acknowledged and justified in the doc comments as "baseapi owns its own health pattern and does NOT reference BaseConsole.Core"). This is an accepted trade-off, but it doubles the maintenance surface for any future fix — including CR-01 above, which needs to be applied in both places.
**Fix:** no action required beyond awareness; consider a future shared `BaseConsole.Core`-independent package if a third consumer needs the same pattern.

### IN-03: Theoretical concurrent-poll race in the consecutive-failure counter

**File:** `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs:65-79`
**File:** `src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs:46-56`
**Issue:** `_consecutiveFailures` is correctly mutated via `Interlocked.Increment`/`Interlocked.Exchange` (no torn reads), but if `CheckHealthAsync` is ever invoked concurrently (e.g. an external monitoring probe polling `/health/ready` at the same time as kubelet), an interleaving where a stale-Healthy inner result's `Exchange(0)` lands after a genuinely-failing evaluation's `Increment` can transiently erase progress toward the latch threshold. In the standard single-kubelet-prober deployment model this is not exploitable (probes are sequential), so this is informational only.
**Fix:** none required under the current single-prober assumption; worth a comment noting the assumption if multiple concurrent probers are ever introduced.

---

## Fixes Applied

9 of 10 findings applied; **WR-05 was applied then REVERTED** after it regressed two previously-green
tests (see below). `--fix --all`. Each fix committed atomically; only the specific edited source files
were staged. Both `dotnet build SK_P.sln -c Debug` and `-c Release` are 0-warning / 0-error.

**Post-fix verification (orchestrator, corrected):** the fixer initially reported `HealthEndpointsTests`
9/11 and attributed the 2 failures (`Test_HealthReady_200_When_Postgres_Reachable`,
`Health_Ready_Returns_200_When_Broker_Dead`) to a pre-existing live-infra baseline. That was WRONG —
re-running with Postgres@5433 + Redis@6380 both reachable still failed both, with `Expected: OK,
Actual: ServiceUnavailable` because the **redis check went Unhealthy after ~2009ms** (the WR-05 2s
bound). WR-05 had bounded the first-time `IConnectionMultiplexer` resolution into the ping budget, so a
REACHABLE-but-cold Redis whose first synchronous connect exceeds 2s falsely reported NotReady (and could
latch a healthy dependency). Both tests were green pre-fix. **WR-05 reverted** (commit `483c6fa`) —
resolution restored OUTSIDE the ping budget; the ping stays bounded. After the revert: affected classes
**21/21 pass** (`HealthEndpointsTests` 11/11, `RedisReadyHealthCheckTests`, `ApiReadinessLatchTests`,
`LatchedReadinessHealthCheckTests`), both builds 0-warning, no new hermetic failures beyond the known
baseline (18 dead `ComposeYamlFacts` + ~5-6 live-infra tests).

| Finding | What changed | Commit |
|---------|--------------|--------|
| CR-01 | Both latch decorators (`LatchedReadinessHealthCheck`, `ApiLatchedReadinessHealthCheck`) now return a STATIC message on EVERY Unhealthy path — added a `UnhealthyMessage` constant for the pre-latch case and stopped forwarding the inner check's raw `Description`/`Exception`. Latch counter/threshold/never-throw math unchanged. Verified live in the test log (`redis ... Unhealthy ... 'readiness dependency unhealthy'`). | `a927ecb` |
| WR-01 | Bounded the per-tick `probe.ProbeOnceAsync` in `BitHealthLoop` with the same `WaitAsync(EdgeOpTimeout, stoppingToken)` used for the edge bus-ops; a timeout reads as L2-unhealthy for the tick, caught+logged (never crashes the loop). Non-Redis exceptions still propagate. `L2ProbeRecovery` untouched. | `041bdd7` |
| WR-02 | Watchdog interval derived from bound options instead of literals — Keeper from `Probe:DelaySeconds` (`IOptions<ProbeOptions>`), processor from `Processor:Interval` (`IOptions<ProcessorLivenessOptions>`), resolved at composition root. Effective defaults (5 / 10) preserved via the options-type defaults. | `ef93ff3` |
| WR-03 | Removed the false "default readiness service is KEPT here, not stripped" claim from `Keeper/Program.cs`'s top-of-file comment; now states Phase 86 strips `StartupCompletionService`. | `ed0ea05` |
| WR-04 | Reconciled the two stale startup-gate invariant doc blocks (`BaseProcessorServiceCollectionExtensions.cs`, `ProcessorStartupOrchestrator.cs`) to acknowledge `/health/startup` now flips on the liveness heartbeat's first beat and the orchestrator's `MarkReady()` calls are redundant/defensive. | `dc50d28` |
| WR-05 | **Applied then REVERTED (won't-fix).** Bounding the first-time multiplexer resolution into the ping budget regressed a REACHABLE-but-cold Redis to a false NotReady (a slow-but-successful connect timed out to Unhealthy → `/health/ready` 503), broke 2 green `HealthEndpointsTests`, and risked latching a healthy dependency. Reverted to the pre-fix resolve-outside-budget + bounded-ping trade-off, which is the correct behavior; the original theoretical "first-time resolution could block the probe" concern is accepted (a readiness probe tolerating an occasional slow first connect is preferable to a false NotReady). | `9476601` (applied), `483c6fa` (revert) |
| WR-06 | Deleted the stale "RED skeleton" XML-doc paragraphs from `LivenessHeartbeat`, `LoopLivenessHealthCheck`, `RedisReadyHealthCheck`, `LatchedReadinessHealthCheck`. | `f09b2b0` |
| IN-01 | Added `.ConfigureAwait(false)` to `ApiLatchedReadinessHealthCheck` (folded into CR-01) and `ApiRedisReadyHealthCheck` (folded into WR-05). | `a927ecb`, `9476601` |
| IN-02 | No code change (accepted duplication trade-off); the actionable part — applying CR-01 in BOTH mirrors — was done. | — |
| IN-03 | Added a comment documenting the single-prober assumption for the `_consecutiveFailures` counter in both latch mirrors (folded into CR-01). | `a927ecb` |

_Fixed: 2026-07-26 (9 applied; WR-05 applied-then-reverted as a regression, corrected by orchestrator post-fix verification)_
_Fixer: Claude (gsd-code-fixer) + orchestrator WR-05 revert_

---

_Reviewed: 2026-07-26T19:08:10Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
