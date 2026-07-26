---
phase: 86
slug: console-webapi-health-liveness-refactor
status: draft
nyquist_compliant: true
wave_0_complete: false
created: 2026-07-26
---

# Phase 86 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution. Derived from 86-RESEARCH.md "Validation Architecture".

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit.v3 + NSubstitute + `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (single test project references ALL src projects incl. BaseConsole.Core, Keeper, BaseProcessor.Core) |
| **Quick run command** | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-class "*<Class>*"` (run the .exe directly — `dotnet test` hangs on Windows MTP) |
| **Full suite command** | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` |
| **Estimated runtime** | ~2–5s (hermetic subset); full hermetic ~30–60s |

**Trait rule:** new hermetic tests MUST NOT carry `[Trait("Category","RealStack")]`; tag new tests `[Trait("Phase","86")]`.

---

## Sampling Rate

- **After every task commit:** run the affected namespace/class via the quick filter.
- **After every plan wave:** full hermetic run.
- **Before phase gate:** full hermetic green + `grep -c startupProbe k8s/3{0,1,2,3}-*.yaml` + the live broker-unreachable proof (`RESTARTS 0`).
- **Max feedback latency:** ~60s (full hermetic).

---

## Per-Requirement Verification Map

| Req ID | Behavior | Test Type | Command | File (Wave 0 unless ✅) |
|--------|----------|-----------|---------|-------------------------|
| HLTH-01/06 | `LoopLivenessHealthCheck`: fresh beat→Healthy; stopped→stale Unhealthy; null→not-started; exact boundary (k=3) | unit | quick (class filter) | ❌ `Console/LoopLivenessHealthCheckTests.cs` (model on `LivenessWatchdogHealthCheckTests.cs`) |
| HLTH-01 | `ILivenessHeartbeat.Beat()` stamps; `Current` reads; never-beaten sentinel | unit | quick | ❌ `Console/LivenessHeartbeatTests.cs` |
| HLTH-05 | Latch: N consecutive failures → latched → stays Unhealthy after inner recovers; single blip resets | unit | quick | ❌ `Console/LatchedReadinessHealthCheckTests.cs` |
| HLTH-04/08 | Redis readiness: reachable→Healthy; unreachable/null-mux→Unhealthy; never throws; no secret in body | unit | quick | ❌ `Console/RedisReadyHealthCheckTests.cs` (model on `ConsoleBusReadyHealthCheckTests.cs`) |
| HLTH-04 | Processor identity+schema readiness = `IProcessorContext.IsHealthy` false→Unhealthy, true→Healthy | unit | quick | ❌ `Processor/IdentitySchemaReadyHealthCheckTests.cs` |
| HLTH-02 | `ProcessorStartupOrchestrator` broker-down → caught+logged+retried, never throws out | unit | quick | ❌ `Processor/StartupOrchestratorResilienceFacts.cs` (lock the existing broad catch) |
| HLTH-03 | Processor beats unconditionally when NOT Healthy; keeper beats even when edge bus-op would hang | unit | quick | ❌ model on `Processor/LivenessHeartbeatFacts.cs` + `Keeper/Health/BitHealthLoopTests.cs` |
| HLTH-07 | first beat calls `IStartupGate.MarkReady`; startup-gate wiring per service | unit | quick | ❌ Wave 0 (new) |
| HLTH-07 (k8s) | startupProbe present on all four manifests; path `/health/startup` | CI grep | `grep -c startupProbe k8s/3{0,1,2,3}-*.yaml` | manual |
| regression | `ConsoleHealthLiveTests`, `ConsoleBusReadyHealthCheckTests`, `BitHealthLoopTests`, `LivenessHeartbeatFacts` stay green (or ported for retired types) | unit | full | ✅ exist |
| live proof | broker unreachable → all four `RESTARTS 0`, NotReady, recover only on restart | RealStack/manual | `kubectl set env deployment/<svc> -n skp RabbitMq__Host=rabbitmq-unreachable.invalid` | manual, NOT hermetic |

*Status legend: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements (test scaffolds — must exist before implementation)

- [ ] `tests/BaseApi.Tests/Console/LoopLivenessHealthCheckTests.cs` — HLTH-01/06 (copy `LivenessWatchdogHealthCheckTests.cs`, k=3, timestamp-only)
- [ ] `tests/BaseApi.Tests/Console/LivenessHeartbeatTests.cs` — HLTH-01
- [ ] `tests/BaseApi.Tests/Console/LatchedReadinessHealthCheckTests.cs` — HLTH-05 (sticky-after-recovery)
- [ ] `tests/BaseApi.Tests/Console/RedisReadyHealthCheckTests.cs` — HLTH-04/08 (null-mux + never-throw + no-secret body)
- [ ] `tests/BaseApi.Tests/Processor/IdentitySchemaReadyHealthCheckTests.cs` — HLTH-04
- [ ] `tests/BaseApi.Tests/Processor/StartupOrchestratorResilienceFacts.cs` — HLTH-02
- [ ] Retire/port tests referencing `KeeperLivenessWatchdogHealthCheck` / `LivenessWatchdogHealthCheck` (they break compile after the delete)
- [ ] Framework install: none — all pinned.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live broker-outage tolerance | HLTH-02 (live) | needs the k8s cluster | `kubectl set env deployment/{baseapi-service,orchestrator,keeper,processor-sample} -n skp RabbitMq__Host=rabbitmq-unreachable.invalid`; assert all `RESTARTS 0` + NotReady + logs; revert `=rabbitmq`. Mind the k8s local-image-stale-on-rebuild trap. |
| startupProbe wiring | HLTH-07 | manifest grep | `grep -c startupProbe k8s/30-baseapi-service.yaml k8s/31-orchestrator.yaml k8s/32-keeper.yaml k8s/33-processor-sample.yaml` → each = 1 |
