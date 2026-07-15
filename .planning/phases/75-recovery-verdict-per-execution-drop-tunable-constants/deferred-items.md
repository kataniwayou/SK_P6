# Phase 75 — Deferred Items

## Out-of-scope pre-existing failures (Docker-less sandbox)

**Discovered during:** 75-01 Task 2 full-hermetic-suite verification (2026-07-15)

**Observation:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` reports
810 total / 538 passed / **272 failed**. All 272 failures are broker-connection
failures (`Connection Failed: rabbitmq://localhost/` and `rabbitmq://rabbitmq/`,
892 such lines in the run) — MassTransit/RabbitMQ-dependent tests that carry a
broker dependency but are NOT tagged `Category=RealStack`, so they run under the
hermetic filter and fail with no broker present in the Docker-less sandbox.

**Scope determination:** OUT OF SCOPE for 75-01. This plan's change is confined to
the pure in-process `PassFailEngine` / `AnalyzerReport` / `PassFailEngineFacts`
(no messaging, no network). Zero analyzer facts appear in the 272 failures; all
28 `*PassFailEngineFacts` + `*PassFailEngineValueChainFacts` are GREEN.

**Precedent:** Consistent with STATE.md — phases 68/73/74 ran their live/broker
close gates "deferred-automated" when Docker is unavailable. These broker-bound
hermetic-filter tests are environmental, not introduced by this phase.

**Action:** None taken (do NOT auto-fix — pre-existing, unrelated files). The live
`phase-68-sweep.ps1` / RealStack analyzer gate for D75-4/D75-6 remains
deferred-automated pending a Docker-up environment.

## DEFERRED: 75-03 live TTL-neutralization verify (D75-6, checkpoint:human-verify)

**Deferred during:** 75-03 Task 2 (`checkpoint:human-verify`, gate="blocking") — 2026-07-15

**Why deferred:** The TTL neutralization is only OBSERVABLE on a running Docker
RealStack (redis/rabbitmq/elasticsearch/prometheus + orchestrator/processor/keeper).
Docker is NOT available in this sandbox — established deferred-automated precedent
(phases 68/73/74 live close gates). The AUTOMATED half of 75-03 is FULLY done and
committed (`e8e9732`): all TTL knobs raised 300→900 in lock-step, Debug+Release
0-warning, the two coupled hermetic facts (`ProcessorOptionsBindingFacts`,
`ComposeYamlFacts`) GREEN.

**Knobs raised to 900 (verify these are the effective values on the live stack):**
1. `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` `ExecutionDataTtlSeconds = 900`
2. `src/Keeper/RecoveryOptions.cs` `ExecutionDataTtlSeconds = 900`
3. `src/Orchestrator/Configuration/OrchestratorOutputOptions.cs` `OutputDataTtlSeconds = 900`
4. `src/Processor.Sample/appsettings.json` `ExecutionDataTtl: 900`
5. `src/Processor.BadConfig/appsettings.json` `ExecutionDataTtl: 900`
6. `compose.yaml` `Processor__ExecutionDataTtl: "900"` on **both** processor-sample
   and processor-badconfig (this env override binds OVER appsettings + the Options
   default at container runtime — Pitfall 3; it is the value the LIVE containers read).

**Exact RealStack steps a future Docker-up run MUST perform to close this gate:**
1. Rebuild the containers so the new appsettings/env TTL is baked in:
   `pwsh -File scripts/phase-65-up.ps1` (or the standard compose-up). Confirm the
   effective `Processor__ExecutionDataTtl=900` is picked up (e.g.
   `docker inspect sk-processor-sample | grep ExecutionDataTtl` or a container log).
2. Run the non-redis fault scenarios:
   `pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-02,TEST-03,TEST-04,TEST-06`.
3. **Expected:** each scenario shows ZERO TTL-manufactured in-flight loss — the
   previously-observed TTL-expiry artifact (e.g. the Phase-68 TEST-06 self-expiry at
   5s/300s vs the 45s outage) is gone; the analyzer report's InFlightLoss for these
   scenarios is driven ONLY by genuine recovery timing, not by TTL. The jittered
   `random[900,1800]` floor must comfortably exceed the ~300s window (dwell 45s +
   return-to-healthy + keeper reinject + drain 60s + poll-to-stable ≤60s).
4. Redis-crash TEST-05/07 are MOOT for TTL (redis is the wiped store — in-flight-at-
   wipe durability boundary); do not expect a TTL effect there.

**Resume-signal to record when run:** "approved" (TTL no longer manufactures loss for
TEST-02/03/04/06), or describe any TTL-manufactured loss still observed.
