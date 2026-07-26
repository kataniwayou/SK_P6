---
phase: 86-console-webapi-health-liveness-refactor
verified: 2026-07-26T23:05:00Z
status: passed
score: 8/8 must-haves verified
overrides_applied: 0
---

# Phase 86: Console & WebApi Health/Liveness Refactor Verification Report

**Phase Goal:** Console (orchestrator/keeper/processor) and WebApi health probes refactored so an infrastructure outage (Postgres/Redis/RabbitMQ) never crashes the process nor restarts the pod — only a genuinely stalled watchdog loop restarts.

**Verified:** 2026-07-26T23:05:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth (from ROADMAP success criteria) | Status | Evidence |
|---|---|---|---|
| 1 | `/health/live` healthy iff watchdog beat within k×interval (k=3); infra outage does NOT trip `/health/live` | ✓ VERIFIED | `src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs:74` — strict `now >= current + k*interval` staleness math, k=3 default. `EmbeddedHealthEndpointService.cs:88-92` — `/health/live` = `"self"` (always-Healthy) + the folded `"live"`-tagged watchdog descriptor only; Redis/bus/Postgres are never tagged `"live"`. |
| 2 | Postgres/Redis/RabbitMQ outage never crashes/restarts; caught+logged every loop incl. `ProcessorStartupOrchestrator`'s identity/schema loop | ✓ VERIFIED | `ProcessorStartupOrchestrator.cs:141,195` broad `catch (Exception ex) when (ex is not OperationCanceledException)` on both Loop A (identity) and Loop B (schema). `RedisReadyHealthCheck.cs`/`ApiRedisReadyHealthCheck.cs` never throw (verified read). Live proof (86-09-SUMMARY): 4-min broker outage, all 4 services `RESTARTS 0`, logs show caught `BrokerUnreachableException`. |
| 3 | One shared `BaseConsole.Core` primitive, opt-in; keeper+processor beat top of loop before infra I/O; orchestrator self-only; the two divergent watchdogs retired; processor beats unconditionally (Healthy gates only the L2 write) | ✓ VERIFIED | `ILivenessHeartbeat`/`LivenessHeartbeat`/`LoopLivenessHealthCheck` in `BaseConsole.Core/Health`; `AddConsoleLivenessWatchdog` opt-in seam (`ConsoleLivenessServiceCollectionExtensions.cs`). `BitHealthLoop.cs:62` — `heartbeat.Beat()` is the literal first statement of the tick, before `ProbeOnceAsync` (line 73) and all edge bus-ops. `ProcessorLivenessHeartbeat.cs:95-105` — `_heartbeat.Beat()` fires before the `if (_context.IsHealthy...)` gate, which now wraps only the L2 `WriteAsync`. `KeeperLivenessWatchdogHealthCheck.cs`, `IKeeperLivenessState.cs`, `KeeperLivenessState.cs`, `BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` confirmed deleted (`test ! -f` verified) with zero residual code references in `src/` (grep only finds historical doc-comment prose). Orchestrator `Program.cs` never calls `AddConsoleLivenessWatchdog` (confirmed self-only). |
| 4 | `/health/ready` = required infra deps + (processor) identity+schema; NotReady never restarts; Redis included; baseapi bus stays Degraded (soft, unlatched) | ✓ VERIFIED | `EmbeddedHealthEndpointService.cs:85-92` wraps bus + Redis in per-process `LatchedReadinessHealthCheck` on `"ready"` tag for all 3 consoles. `ProcessorIdentitySchemaReadyHealthCheck.cs` folds `identity-schema-ready` onto processor `/health/ready`. `BaseApi.Core/DependencyInjection/HealthServiceCollectionExtensions.cs:51-74` — Postgres + Redis latched; `MessagingServiceCollectionExtensions.cs:80` confirms bus stays `MinimalFailureStatus = Degraded`, untouched/unlatched. |
| 5 | No self-heal: hard readiness latch (once a required dep fails for its failureThreshold window, `/health/ready` latches Unhealthy until restart); migration one-shot | ✓ VERIFIED | `LatchedReadinessHealthCheck.cs:58-79` / `ApiLatchedReadinessHealthCheck.cs:38-61` — `volatile bool _latched` short-circuits to Unhealthy forever once `Interlocked.Increment` reaches `failureThreshold`; only reset by process restart (new instance). `StartupCompletionService.cs` (BaseApi) confirmed pre-existing one-shot migration behavior preserved (catch-log-no-retry, no `MarkReady` on failure). |
| 6 | `startupProbe` → `/health/startup` on all four k8s deployments (30/31/32/33) | ✓ VERIFIED | `grep -c startupProbe` = 1 for each of `k8s/30-baseapi-service.yaml`, `31-orchestrator.yaml`, `32-keeper.yaml`, `33-processor-sample.yaml`; each targets `/health/startup` on the correct port (8080/8081/8083/8082); pre-existing `readinessProbe`/`livenessProbe` blocks confirmed unchanged (independently re-checked in `32-keeper.yaml`). |
| 7 | Infra exceptions always caught+logged | ✓ VERIFIED | Same evidence as #2; additionally `RedisReadyHealthCheck`/`ApiRedisReadyHealthCheck` catch-all around the bounded PING; `LatchedReadinessHealthCheck` never lets an inner exception propagate. |
| 8 | Full hermetic suite green (no Phase-86 regressions) | ✓ VERIFIED | Independently re-ran the full suite twice (not just trusting SUMMARY): 767 total, **24 failed** (SUMMARY claimed 23 — see Anti-Patterns/Info section), 743 passed. Extracted the exact failing-test list via a JUnit report and confirmed all 24 are pre-existing/environmental: 18 `ComposeYamlFacts` (dead tests for `compose.yaml`, deleted in commit `6bb1f83`, confirmed an ancestor of the phase-86 base commit `87fa2fc`), `ErrorMappingFacts` (1, needs Postgres), `ConcurrencyTokenTests` (1, needs Postgres), `LogExportTests` (2, needs ES/OTLP), `SchemasLogsE2ETests` (1, needs ES), `LogLevelFilterTests` (1, needs live infra). Zero Phase-86 test classes fail — independently ran every Phase-86 test class (`LoopLivenessHealthCheckTests`, `LivenessHeartbeatTests`, `RedisReadyHealthCheckTests`, `LatchedReadinessHealthCheckTests`, `IdentitySchemaReadyHealthCheckTests`, `StartupOrchestratorResilienceFacts`, `BitHealthLoopTests`, `ApiReadinessLatchTests`, `LivenessHeartbeatFacts`, `ProcessorHealthLiveTests`) and all pass. |

**Score:** 8/8 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|---|---|---|---|
| `src/BaseConsole.Core/Health/ILivenessHeartbeat.cs` | Beat()/Current contract | ✓ VERIFIED | Exists, matches locked signature |
| `src/BaseConsole.Core/Health/LivenessHeartbeat.cs` | Interlocked long-ticks impl | ✓ VERIFIED | Fully implemented (not RED skeleton), `Interlocked.Exchange`/`Interlocked.Read` |
| `src/BaseConsole.Core/Health/LoopLivenessHealthCheck.cs` | k=3 staleness check, OUTER-resolved | ✓ VERIFIED | Fully implemented, strict `>=` boundary |
| `src/BaseConsole.Core/Health/RedisReadyHealthCheck.cs` | bounded never-throw Redis PING | ✓ VERIFIED | Fully implemented, ~2s bound, no-secret |
| `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs` | sticky no-self-heal latch | ✓ VERIFIED | Fully implemented; see CR-01 note below re: pre-latch pass-through leak |
| `src/BaseConsole.Core/DependencyInjection/ConsoleLivenessServiceCollectionExtensions.cs` | `AddConsoleLivenessWatchdog` opt-in seam | ✓ VERIFIED | Exists, registers `ILivenessHeartbeat` + `"live"` descriptor |
| `src/BaseApi.Core/Health/ApiRedisReadyHealthCheck.cs` | webapi Redis mirror | ✓ VERIFIED | Fully implemented, mirrors console pattern |
| `src/BaseApi.Core/Health/ApiLatchedReadinessHealthCheck.cs` | webapi latch mirror | ✓ VERIFIED | Fully implemented |
| `src/BaseProcessor.Core/Liveness/ProcessorIdentitySchemaReadyHealthCheck.cs` | identity+schema readiness | ✓ VERIFIED | Reads only `IProcessorContext.IsHealthy` (never Id/definitions — WR-03 hazard avoided) |
| `src/Keeper/Health/BitHealthLoop.cs` | top-of-tick beat + bounded edge ops | ✓ VERIFIED | `heartbeat.Beat()` first line of loop; edge bus-ops wrapped in `WaitAsync(3s)` |
| `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` | unconditional beat above gate | ✓ VERIFIED | `_heartbeat.Beat()` before `if (_context.IsHealthy...)` |
| `k8s/30-33-*.yaml` | startupProbe on all four | ✓ VERIFIED | Confirmed via grep, readiness/liveness preserved |
| Retired: `KeeperLivenessWatchdogHealthCheck.cs`, `IKeeperLivenessState.cs`, `KeeperLivenessState.cs`, `BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` | deleted | ✓ VERIFIED | All four confirmed absent from filesystem; zero code references remain in `src/` |

### Key Link Verification

| From | To | Via | Status | Details |
|---|---|---|---|---|
| `BitHealthLoop.ExecuteAsync` | `ILivenessHeartbeat.Beat()` | top-of-loop call, before `ProbeOnceAsync` | WIRED | Line-order confirmed by direct read |
| `ProcessorLivenessHeartbeat.ExecuteAsync` | `ILivenessHeartbeat.Beat()` | top-of-loop call, above `IsHealthy` gate | WIRED | Line-order confirmed by direct read |
| `LoopLivenessHealthCheck.CheckHealthAsync` | `ILivenessHeartbeat`/`TimeProvider` | `outer.GetRequiredService<T>()` at check time | WIRED | OUTER-resolution confirmed, not captured at registration |
| `EmbeddedHealthEndpointService.StartAsync` | `LatchedReadinessHealthCheck(BusReadyHealthCheck)` + `LatchedReadinessHealthCheck(RedisReadyHealthCheck)` | constructed once in `StartAsync`, tagged `"ready"` | WIRED | Per-process singleton construction confirmed (not per-request) |
| `HealthServiceCollectionExtensions.AddBaseApiHealth` | `ApiLatchedReadinessHealthCheck` over Postgres+Redis | keyed singleton factories | WIRED | Confirmed; bus check registration untouched (`MinimalFailureStatus=Degraded` still present) |
| `k8s/3{0,1,2,3}-*.yaml` | `/health/startup` | `startupProbe.httpGet.path` | WIRED | Confirmed on correct ports for all four |
| Orchestrator `Program.cs` | `AddConsoleLivenessWatchdog` | (absence) | NOT CALLED (intentional) | Confirmed orchestrator never opts in — self-only liveness by design |

### Requirements Coverage

| Requirement | Description | Status | Evidence |
|---|---|---|---|
| HLTH-01 | `/health/live` = watchdog-loop liveness only, k=3 | ✓ SATISFIED | Truth #1 |
| HLTH-02 | Infra outage caught+logged, never crashes, incl. ProcessorStartupOrchestrator | ✓ SATISFIED | Truth #2 |
| HLTH-03 | Shared primitive, beat before infra I/O | ✓ SATISFIED | Truth #3 |
| HLTH-04 | `/health/ready` = required deps + identity/schema, Redis included | ✓ SATISFIED | Truth #4 |
| HLTH-05 | Hard latch, no self-heal | ✓ SATISFIED | Truth #5 |
| HLTH-06 | Retired watchdogs removed; processor beats unconditionally | ✓ SATISFIED | Truth #3 |
| HLTH-07 | startupProbe on all four manifests | ✓ SATISFIED | Truth #6 |
| HLTH-08 | baseapi bus stays Degraded, not latched | ✓ SATISFIED | Truth #4 |

**Known quirk (confirmed, not a gap):** `.planning/REQUIREMENTS.md` contains zero `HLTH-0x` entries and zero reference to Phase 86 or v12.0.0 — the tracked REQUIREMENTS.md file is still the v11.0.0 snapshot. This matches the documented milestone-numbering quirk noted consistently across all nine plan SUMMARYs (`requirements mark-complete is a no-op`). Not treated as ORPHANED/BLOCKED since this is a known tooling gap, not missing implementation.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|---|---|---|---|---|
| `src/BaseConsole.Core/Health/LatchedReadinessHealthCheck.cs` | 63-75 | Pre-latch pass-through of raw inner `HealthCheckResult` (incl. attached exceptions from `BusReadyHealthCheck`/`NpgSqlHealthCheck`) to the exception-including `UIResponseWriter.WriteHealthCheckUIResponse` on `/health/ready` | WARNING (advisory — see below) | For polls 1..(failureThreshold-1) during a sustained outage, `/health/ready` can leak raw driver exception detail (hostnames, ports, DB/queue names). Independently confirmed in code (`BusReadyHealthCheck.cs:64` attaches `result.Exception`; `BaseApiApplicationBuilderExtensions.cs` uses the exception-including writer). This is CR-01 from `86-REVIEW.md` (marked Critical there for info-disclosure). It does **not** affect crash/restart/self-heal behavior — the phase's core headline outcome is unaffected — so it is reported as advisory per the task brief, not as a blocking gap against the ROADMAP success criteria. |
| `.planning/phases/86-.../86-09-SUMMARY.md` | verdict table | SUMMARY undercounts hermetic failures (23 claimed vs 24 actual) | INFO | Independently re-ran the full suite twice and extracted a JUnit report: 24 failures, not 23. The missing one (`ConcurrencyTokenTests.Test_RacingWrites_Produce_409_WithGenericMessage_NoXminLeak`, needs live Postgres) was already correctly catalogued as pre-existing/infra-dependent in `86-02-SUMMARY.md` and `deferred-items.md` — it was simply dropped from 86-09's final tally table. Not a regression; does not affect goal achievement. |
| `src/BaseConsole.Core/Health/LivenessHeartbeat.cs` etc. (4 files) | doc comments | Stale "RED skeleton" XML-doc paragraphs left in shipped GREEN code | INFO | WR-06 from 86-REVIEW.md, confirmed present; cosmetic only |
| `src/Keeper/Program.cs` | 14-18 vs 24-33 | Stale top-of-file comment contradicts the actual (correct) StartupCompletionService-removal code | INFO | WR-03 from 86-REVIEW.md, confirmed present; cosmetic only |

No TBD/FIXME/XXX debt markers found in any Phase-86-modified health/liveness file (checked `BaseConsole.Core/Health`, `BaseApi.Core/Health`, `BaseProcessor.Core/Liveness`, `Keeper/Health/BitHealthLoop.cs`, `Keeper/Program.cs`).

### Human Verification Required

None outstanding. The live k8s broker-unreachable proof — the one item in this phase that inherently needs a live cluster and would normally route to human verification — was already executed as a blocking `checkpoint:human-verify` task within plan 86-09 and is recorded with specific, independently-plausible evidence (unique image tag `p86-e24cb5a`, exact timestamps, real pod names, actual `BrokerUnreachableException` log excerpts, a documented SourceHash-reseed side-effect). I did not have live k8s cluster access in this verification session to re-run the experiment myself, so I cross-checked it against the code-level guarantees instead (Section "Observable Truths" #1-#3: `/health/live` is provably self+watchdog-only and never reads Redis/bus/Postgres; every infra call path is provably caught; the latch provably cannot crash the process). The code fully supports the claimed live-proof outcome. If the user wants an independent live re-run, that would require live k8s cluster access outside this session's environment.

### Gaps Summary

No blocking gaps found. All 8 HLTH requirements are demonstrably satisfied at the code level (not merely claimed in SUMMARY.md), the retired watchdog types are genuinely deleted with zero residual references, the shared primitive is genuinely beat-first/unconditional in both keeper and processor, the readiness latch genuinely has no self-heal path, and all four k8s manifests genuinely carry startupProbes with readiness/liveness preserved. The full hermetic suite was independently re-run (not trusted from SUMMARY) and confirmed 0 Phase-86 regressions (24 pre-existing/environmental failures, one more than SUMMARY's stated 23, but the extra one is itself pre-existing per the phase's own deferred-items.md).

One advisory-only finding (CR-01, info-disclosure via the latch's pre-threshold pass-through) is carried forward from the code review; it does not affect the phase's crash/restart headline goal and is not part of the ROADMAP-declared success criteria, so it does not block phase completion. It is worth a follow-up fix (documented in `86-REVIEW.md` with a concrete patch).

---

_Verified: 2026-07-26T23:05:00Z_
_Verifier: Claude (gsd-verifier)_
