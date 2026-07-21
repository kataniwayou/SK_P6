---
phase: 75-recovery-verdict-per-execution-drop-tunable-constants
plan: 03
subsystem: config / L2 TTL policy
tags: [ttl, l2, config, resilience, d75-6, deferred-live]
requires:
  - "The five in-code TTL knobs + the compose env override (all previously 300)"
provides:
  - "All L2 execution-data TTL knobs raised 300→900 in lock-step (D75-6)"
  - "Jittered random[900,1800] floor that outlasts the ~300s non-redis recovery window"
affects:
  - "Live TTL lifetime of skp:data:*/skp:out:* on the RealStack (verify deferred — Docker-less)"
tech-stack:
  added: []
  patterns:
    - "TTL floor supplied into the shared L2ProjectionKeys.OutputDataTtl jitter policy (unchanged)"
    - "compose env override binds OVER appsettings + Options default at container runtime (Pitfall 3)"
key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs
    - src/Keeper/RecoveryOptions.cs
    - src/Orchestrator/Configuration/OrchestratorOutputOptions.cs
    - src/Processor.Sample/appsettings.json
    - src/Processor.BadConfig/appsettings.json
    - compose.yaml
    - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
    - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
decisions:
  - "Raised to 900 (3× the ~300s window) — jittered random[900,1800] comfortably outlasts dwell 45s + return-to-healthy + keeper reinject + drain 60s + poll-to-stable ≤60s"
  - "Also raised the compose Processor__ExecutionDataTtl env override (6th/7th runtime knob the plan under-enumerated) — it binds over appsettings + Options at container runtime, so leaving it at 300 would defeat the neutralization on the live stack"
  - "Liveness TtlSeconds (default 30) left untouched — a DISTINCT sliding-liveness floor, not the execution-data TTL"
metrics:
  duration_min: 44
  completed: 2026-07-15
  tasks_completed: 1
  tasks_deferred: 1
  files_modified: 8
  commits: 1
---

# Phase 75 Plan 03: Neutralize the TTL confounder (D75-6) Summary

Raised every L2 execution-data TTL knob from 300s to 900s in lock-step so a benign TTL
expiry can never be mistaken for a recovery failure on the non-redis fault scenarios
(processor/orchestrator/keeper/rabbitmq — TEST-02/03/04/06); the jittered `random[900,1800]`
floor now comfortably outlasts the ~300s full-recovery window.

## What Was Built

**Task 1 (auto) — raise all TTL knobs 300→900 in lock-step. COMPLETE, committed `e8e9732`.**

Five in-code knobs raised (all feed the shared `L2ProjectionKeys.OutputDataTtl(ttl)` jitter
policy `random[ttl, 2×ttl]`, which is unchanged — only the floor rises):

1. `ProcessorLivenessOptions.ExecutionDataTtlSeconds` = 900 (+ XML doc)
2. `RecoveryOptions.ExecutionDataTtlSeconds` = 900 (+ XML doc)
3. `OrchestratorOutputOptions.OutputDataTtlSeconds` = 900 (+ XML doc)
4. `Processor.Sample/appsettings.json` `ExecutionDataTtl` = 900
5. `Processor.BadConfig/appsettings.json` `ExecutionDataTtl` = 900

Plus the runtime-effective compose env override (see Deviations):

6/7. `compose.yaml` `Processor__ExecutionDataTtl: "900"` on **both** `processor-sample`
    and `processor-badconfig` (+ the explanatory comment).

**Assumption A2 confirmed (no distinct 5th index TTL):** a full `src` grep for
`ExecutionDataTtl`/`OutputDataTtl`/`SlotTtl`/`SlotArray` returns only the five knobs above
and their three shared-jitter consumers (`OutputTail`, both `Inject*Consumer`s, `RelocateTail`)
— the slot-array index EXPIRE was already unified to `ExecutionDataTtl` (quick 260615-dbf).
The `L2ProjectionKeys.OutputDataTtl` jitter policy was verified (floor-only), not edited.

The DISTINCT liveness floor `ProcessorLivenessOptions.TtlSeconds = 30` was left untouched.

## Verification

- **Debug + Release solution build:** both **0 Warning / 0 Error**.
- **Grep proof:** zero `ExecutionDataTtl`/`OutputDataTtl` production knob remains at 300 (src +
  compose); all seven read 900; liveness `TtlSeconds = 30` intact.
- **Coupled hermetic facts GREEN:** `ProcessorOptionsBindingFacts` (baked-default now 900) +
  `ComposeYamlFacts` (compose override now 900) — both classes pass, 0 failures.
- **IO-free logic subsets GREEN:** `PassFailEngineFacts`, `PassFailEngineValueChainFacts`,
  `OutputTailFacts`, `EntryStepDispatchConsumerFacts`, `PostProcessConsumerFacts`,
  `PrePipelineFacts`, `OrchestratorPostProcessConsumerFacts`, `InjectConsumerFacts`,
  `DispatchBindSequenceFacts` — all pass (no TTL-default coupling regressed).
- **Full hermetic filter:** 272 pre-existing infra-dependent failures remain (Postgres
  `127.0.0.1:5433` connection-refused + RabbitMQ broker-unreachable + Redis) — environmental,
  Docker-less sandbox, NOT caused by this config change (see Deferred Issues). The one
  hermetic failure this change DID cause (the stale `= 300` baked-default assertion) was fixed.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking / missing-critical] Raised the compose `Processor__ExecutionDataTtl` env override (the 6th/7th runtime knob)**
- **Found during:** Task 1, grepping the tests tree for stale `300` assertions after editing the five named knobs.
- **Issue:** `compose.yaml` sets `Processor__ExecutionDataTtl: "300"` as an environment
  override on **both** `processor-sample` (line 287) and `processor-badconfig` (line 321). Per
  Pitfall 3, this env override binds OVER appsettings.json AND the Options default at container
  runtime — it is the value the LIVE containers actually read. The plan enumerated only the
  three Options defaults + two appsettings values; it did not list the compose override. Leaving
  it at 300 would leave the live stack effectively at 300 despite all five in-code knobs at 900,
  making the entire TTL neutralization a no-op on the RealStack — directly defeating the plan's
  must-have truth "no knob left at 300" and the live-verify checkpoint's purpose. The compose
  comment itself documents this override as the source of the Phase-68 TEST-06 TTL-desync artifact.
- **Fix:** raised both `Processor__ExecutionDataTtl` overrides 300→900 and updated the compose
  comment to explain the lock-step + Pitfall-3 rationale.
- **Files modified:** `compose.yaml`
- **Commit:** `e8e9732`

**2. [Rule 1 - Stale assertion] `ProcessorOptionsBindingFacts.Empty_Config_Yields_Baked_Defaults` asserted 300**
- **Found during:** Task 1 hermetic verification (the sole hermetic, IO-free failure my change caused).
- **Issue:** the fact asserts the baked `ExecutionDataTtlSeconds` default is 300 — stale once the plan raised the default to 900.
- **Fix:** assertion 300→900 with a D75-6 explanatory comment. The companion
  `Binds_Six_Independent_Seconds_Knobs...` (which binds an explicit 120) is unaffected and stays green.
- **Files modified:** `tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs`
- **Commit:** `e8e9732`

**3. [Rule 1 - Stale assertion] `ComposeYamlFacts.ComposeYaml_ProcessorSample_Sets_ExecutionDataTtl_To_ProductionDefault` asserted "300"**
- **Found during:** Task 1, as the mirror of deviation 1 (the compose assertion must track the compose value).
- **Issue:** the fact's regex asserts `Processor__ExecutionDataTtl:\s*"300"` — stale once the compose override rose to 900.
- **Fix:** regex 300→900 with an updated D75-6 comment.
- **Files modified:** `tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs`
- **Commit:** `e8e9732`

## Deferred Verification (Task 2 — checkpoint:human-verify, gate="blocking")

**Live TTL-neutralization verify is DEFERRED-AUTOMATED — Docker is unavailable in this sandbox.**

Established precedent: phases 68/73/74 close/live gates are deferred-automated when Docker is
down (per STATE.md). The AUTOMATED half of this plan is fully done and committed; only the live
observation on a running RealStack (redis/rabbitmq/elasticsearch/prometheus + the three services)
is deferred. The exact RealStack steps a future Docker-up run must perform are recorded in
`deferred-items.md` under **"DEFERRED: 75-03 live TTL-neutralization verify"**:

1. `pwsh -File scripts/phase-65-up.ps1` (rebuild so the new appsettings/env TTL bakes in);
   confirm effective `Processor__ExecutionDataTtl=900`.
2. `pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-02,TEST-03,TEST-04,TEST-06`.
3. Expected: ZERO TTL-manufactured in-flight loss (the Phase-68 TEST-06 self-expiry artifact is
   gone); analyzer InFlightLoss for those scenarios is driven only by genuine recovery timing.
4. Redis-crash TEST-05/07 are moot for TTL (redis is the wiped store).

Automated gate satisfied in lieu of the live gate: hermetic coupled facts GREEN + Debug/Release
0-warning build.

## Deferred Issues (pre-existing, out of scope)

The full hermetic-filter run shows 272 failures, ALL pre-existing infra-dependent tests failing
because the Docker-less sandbox has no Postgres (`127.0.0.1:5433` connection-refused), RabbitMQ
broker, or Redis. Representative: `WorkflowsIntegrationTests` → `Npgsql.NpgsqlException: Failed
to connect to 127.0.0.1:5433 ... actively refused`. Zero analyzer/options facts among them. Not
caused by this config change; logged in `deferred-items.md`. No auto-fix (scope boundary:
pre-existing, unrelated files).

## Self-Check: PASSED
