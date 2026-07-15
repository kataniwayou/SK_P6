# Phase 75: Per-execution recovery verdict — decouple the sweep from tunable constants - Research

**Researched:** 2026-07-15
**Domain:** .NET resilience-sweep analyzer (PassFailEngine), keeper recovery machinery, L2 (Redis) TTL config, keeper→Elasticsearch instrumentation
**Confidence:** HIGH (every decision grounded in current code, file:line + quoted; no external library research needed — this is an in-repo rework)

## Summary

Phase 75 reworks the resilience sweep's PASS/FAIL verdict so it is a pure function of per-`(correlationId, executionId)` recovery, decoupled from every tunable constant. The verdict machinery lives entirely in one pure class — `PassFailEngine.Analyze` (`tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`) — fed by one RealStack fixture (`AnalyzerE2ETests.cs`) and driven by two PowerShell scripts (`phase-67-harness.ps1` window/dwell/RECOVERY_UTC seam, `phase-68-sweep.ps1` roll-up). The engine already keys the started-execution denominator by `(correlationId, executionId)` [VERIFIED: PassFailEngine.cs:115-117] (decision 1 is already satisfied). The `MaxInFlightLoss=4` bound to drop (decision 2) is a **method parameter default** (`int maxInFlightLoss = 4`, line 111), used only at lines 171-174 — it is not a shared const, so removal is surgically contained but the RealStack caller (`AnalyzerE2ETests.cs:221-230`) does NOT currently pass it (relies on the default), so removing the parameter requires no call-site edit there.

The hard part is decision (3)+(4): replacing the absolute in-flight bound with a **recoverability classifier** driven by keeper evidence. Today the keeper's `ReinjectConsumer` silent-DROP (`STRLEN L2[entryId]==0`) emits only `logger.LogWarning("REINJECT drop: L2 data gone EntryId={EntryId}", m.EntryId)` (`ReinjectConsumer.cs:43`) — a structured hole that surfaces in ES as `attributes.EntryId` (the MEL→OTLP bridge with `ParseStateValues=true` + `IncludeScopes=true`, `BaseConsoleObservabilityExtensions.cs:51-60`), but carries **no `CorrelationId`/`ExecutionId`/`StepLabel`** join keys — so the analyzer cannot currently join a keeper drop to a lost `(correlationId, executionId)`. The `KeeperReinject` message contract DOES already carry all four ids (`CorrelationId`, `ExecutionId`, `EntryId`, `MessageId` — `KeeperReinject.cs:10-13`), so the fix is to widen the drop-log's structured fields (and add a REINJECT *success* log symmetrically) so the analyzer's ES read can classify each lost execution as recoverable-but-lost (binding FAIL) vs provably-unrecoverable (keeper saw a clean-absent drop → tolerated, cause-labeled).

**Primary recommendation:** (a) Widen the keeper REINJECT drop + success logs to structured `(CorrelationId, ExecutionId, EntryId, MessageId, outcome)` fields keyed for ES join; (b) extend `AnalyzerE2ETests.cs` to ES-query keeper drop/reinject logs and pass a per-`(corr,exec)` keeper-outcome map into `Analyze`; (c) replace the `maxInFlightLoss` bound in `PassFailEngine.Analyze` with a classifier that FAILs any recoverable-but-lost execution and only tolerates the keeper-confirmed-unrecoverable (clean-absent-drop) or in-flight-at-wipe (redis-crash) losses; (d) raise the four test-env L2 TTL defaults (all currently 300s) well above ~300s recovery for non-redis scenarios.

## Phase Requirements

No REQUIREMENTS.md / SPEC.md exists yet — the 8 numbered ROADMAP decisions ARE the requirements. A 75-SPEC.md must be authored during planning. Mapping:

| ID | Decision (from ROADMAP goal) | Research Support |
|----|------------------------------|------------------|
| D75-1 | Keep per-`(correlationId, executionId)` denominator | ALREADY DONE — `PassFailEngine.cs:115-117` (`startedRuns = runs.Count`, one RunTrace per `(corr,exec)`); `AnalyzerE2ETests.cs:342` keys `byInstance` by `(Corr, Exec)` tuple |
| D75-2 | Drop absolute `MaxInFlightLoss=4` bound | `PassFailEngine.cs:111` (param default), `:171-174` (usage), `:293` (verdict `!inFlightOverBound`); NOT a shared const; RealStack caller uses default |
| D75-3 | Recoverability classifier (recoverable-but-lost → FAIL; provably-unrecoverable → reported+labeled) | Slots into the B-criterion loop `PassFailEngine.cs:147-168`; needs new keeper-outcome input |
| D75-4 | Derive unrecoverable from keeper REINJECT-vs-DROP per entryId + keeper→ES instrumentation | `ReinjectConsumer.cs:35-65` (the DROP + REINJECT); `KeeperReinject.cs:10-13` (join keys exist); `BaseConsoleObservabilityExtensions.cs:51-60` (logs→ES); analyzer join site `AnalyzerE2ETests.cs:339-457` |
| D75-5 | Spec ruling: in-flight-at-wipe tolerated; recoverable-but-lost binding FAIL | Existing in-flight tolerance logic `PassFailEngine.cs:124-174`; TEST-05/07 are the redis-wipe boundary |
| D75-6 | Raise test-env L2 TTLs above full recovery (~300s+) | Four 300s defaults: `ProcessorLivenessOptions.cs:51`, `RecoveryOptions.cs:19`, `OrchestratorOutputOptions.cs:11`, appsettings `ExecutionDataTtl:300`; jitter policy `L2ProjectionKeys.cs:70-71` |
| D75-7 | Optional execution-based observation window (run until K executions) | `phase-67-harness.ps1:257-258` (`$windowSeconds = 300`); `ConservationTol=1.0` `PassFailEngine.cs:256` |
| D75-8 | Preserve the value-chain in-flight-loss fix (commit 3028f43) | `PassFailEngine.cs:139-145, 209-219` (inFlightLossKeys skip in value-chain loop) — must survive the classifier rework |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| PASS/FAIL verdict computation | Test/Analyzer (`PassFailEngine`, pure) | — | Pure arbiter, no IO; all logic changes land here (decisions 1,2,3,5,8) |
| Keeper recovery evidence (REINJECT/DROP) | Keeper service (`src/Keeper/Recovery`) | — | The recovery machinery owns the drop condition; instrumentation is a keeper-side log edit (decision 4) |
| Keeper→ES telemetry join | Keeper (emit) + Test/Analyzer (read) | BaseConsole.Core (MEL→OTLP bridge) | Keeper emits structured logs; analyzer ES-queries + joins them (decision 4) |
| L2 TTL policy | Config (appsettings + Options classes) | Messaging.Contracts (`L2ProjectionKeys.OutputDataTtl`) | TTL floors are per-service config; the jitter policy is shared (decision 6) |
| Observation window / recovery timing | Harness scripts (`phase-67-harness.ps1`) | Test fixture (`AnalyzerE2ETests`) | Window bounds + RECOVERY_UTC are harness-controlled env seams (decision 7) |

## Standard Stack

This is an in-repo rework — no new libraries. Existing stack is authoritative:

| Component | Where | Purpose |
|-----------|-------|---------|
| .NET 8 + xUnit v3 (`TestContext.Current`) | `tests/BaseApi.Tests` | Test host; RealStack tests tagged `Category=RealStack` |
| StackExchange.Redis | keeper/processor/orchestrator | L2 store; `StringLengthAsync` (STRLEN) is the drop probe |
| MassTransit + RabbitMQ | all services | Message transport; keeper recovery consumers |
| OpenTelemetry (MEL bridge, OTLP) | `BaseConsole.Core` | Logs→ES (`attributes.*`), metrics→Prometheus |
| Elasticsearch | live stack | `logs-generic.otel-default` data stream; analyzer reads `attributes.StepLabel/ExecutionId/CorrelationId/Produced` |
| Prometheus | live stack | `keeper_messages_consumed/sent`, `keeper_l2_probe` (MG-1/2/3) |

**Hermetic test harness note:** run `BaseApi.Tests.exe` directly (`dotnet test` hangs on Windows MTP), filter `--filter-not-trait Category=RealStack` for hermetic. The engine facts (`PassFailEngineFacts`, `PassFailEngineValueChainFacts`) are hermetic (synthetic `RunTrace` inputs) — decisions 1,2,3,5,8 are ALL provable hermetically without Docker. Only decisions 4 (ES join) and 6/7 (live TTL/window) need the live stack.

## Architecture Patterns

### System Data Flow

```
harness (phase-67-harness.ps1)
  ├─ seed fan-out cron  → orchestrator fires DAG A→B→C→{D1→E1→F1,D2→E2→F2}→G per (corr,exec)
  ├─ inject fault @ N fires, dwell 45s, recover → records RECOVERY_UTC (:231,330)
  ├─ set env: SCENARIO_ID, WINDOW_START_UTC, WINDOW_END_UTC, RECOVERY_UTC (:393-398)
  └─ dotnet test ~Analyzer  ──────────────┐
                                          ▼
                          AnalyzerE2ETests.Analyze_Window_Yields_Pass
                            ├─ drain 60s + poll-to-stable ES hits (:153-170)
                            ├─ ES read Step_* hits → RunTrace per (corr,exec) (:339-457)
                            │     attributes.{CorrelationId,ExecutionId,StepLabel,Produced,@timestamp}
                            ├─ [NEW D75-4] ES read keeper REINJECT drop/success logs
                            │     → per-(corr,exec) keeper-outcome map
                            ├─ Prom windowed deltas → PromCounterSnapshot (MG-1/2/3)
                            └─ PassFailEngine.Analyze(...)  ── PURE ──┐
                                                                     ▼
                          verdict = missing==0 && !inFlightOverBound && !dupFail
                                    && valueChainOk && metricGateOk   (:293)
                                    [D75-3 replaces the inFlightOverBound bound with a classifier]
                            └─ write analyzer-reports/{scenario}.json + assert Pass
```

### The current verdict formula (PassFailEngine.cs:293)

```csharp
var pass = missing == 0 && !inFlightOverBound && !dupFail && valueChainOk && metricGateOk;
```

- `missing` = started-but-incomplete runs classified as BINDING (not in-flight-at-wipe) — `:170`
- `inFlightOverBound` = `inFlightLoss > maxInFlightLoss` — `:171` ← **THE cron-rate coupling to remove (D75-2)**
- `dupFail`, `valueChainOk`, `metricGateOk` — orthogonal, preserved

### Current B-criterion classification (the slot for D75-3), PassFailEngine.cs:147-174

```csharp
foreach (var r in incomplete)
{
    var key = $"{r.CorrelationId}|{r.ExecutionId}";
    var startedAfterRecovery = recoveryUtc is { } rec
        && firstHop.TryGetValue(key, out var fh) && fh > rec;
    var stalledBeforeRecovery = recoveryUtc is { } rec2
        && lastHop.TryGetValue(key, out var lh) && lh < rec2;

    if (stalledBeforeRecovery && !startedAfterRecovery)
    {
        inFlightLoss++;                    // TOLERATED (timestamp heuristic)
        inFlightLossKeys.Add(key);
        ...
    }
    else
    {
        bindingMissing++;                  // binding FAIL
        ...
    }
}
var missing = bindingMissing;
var inFlightOverBound = inFlightLoss > maxInFlightLoss;   // ← D75-2 removes this
```

**Pattern for D75-3:** the classifier currently uses a **timestamp heuristic** (last hop < RECOVERY_UTC ⇒ tolerated). Decision 4 upgrades this to **keeper evidence**: an incomplete run is *provably-unrecoverable/tolerated* iff the keeper logged a clean-absent DROP for its `entryId` (data physically gone), AND (decision 5) it is an in-flight-at-wipe case (redis-crash scenario). Everything else recoverable-but-lost → binding FAIL. The new classifier reads a `keeperOutcomeByExecution` map (new `Analyze` parameter) alongside the existing `recoveryUtc`/hop maps.

### Anti-Patterns to Avoid
- **Re-introducing a tunable bound.** The whole point of D75-2 is that a green verdict must not depend on `MaxInFlightLoss` (a proxy for cron rate × window). Do NOT replace `maxInFlightLoss=4` with another absolute count — replace it with per-execution keeper-evidence classification.
- **Failing on a keeper-confirmed clean-absent drop (decision 5).** In-flight-at-wipe loss (data only in L2, L2 wiped, no outbox) is ACCEPTED. The classifier must count these as tolerated + cause-labeled, never binding.
- **Reading Redis in the analyzer.** `AnalyzerE2ETests` is ES-read-only (`:44-46`). The keeper-outcome join MUST come from keeper *logs* in ES, not a live L2 probe.
- **Breaking commit 3028f43 (decision 8).** The `inFlightLossKeys` set (`:145`) is skipped in the value-chain loop (`:216-219`) so a terminal-only survivor doesn't false-FAIL. The reworked classifier must keep populating an equivalent "tolerated keys" set that the value-chain loop skips.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Structured keeper telemetry → ES | Custom ES writer in keeper | Existing MEL→OTLP bridge (`BaseConsoleObservabilityExtensions`) — just add structured log fields | `ParseStateValues=true`+`IncludeScopes=true` already surface `{Placeholder}` as `attributes.Placeholder` |
| ES field-path query | New index-name literals | `EsIndexNames.*FieldPath` consts + `ElasticsearchTestClient.SearchAllHits` | `.keyword` sub-field trap documented (`EsIndexNames.cs:82,102`); query `attributes.X` directly |
| Per-`(corr,exec)` join keying | New tuple scheme | The `$"{corr}|{exec}"` string key already used everywhere (`AnalyzerE2ETests.cs:409`, `PassFailEngine.cs:149`) | Consistency with existing hop/seed/trip maps |
| Clean-absent-drop probe | New probe logic | Keeper's existing `STRLEN L2[entryId]==0` (`ReinjectConsumer.cs:35-37`) | `STRLEN` (not `KeyExists`) already handles empty-string keys (IN-04) |

**Key insight:** Decision 4's "keeper→ES instrumentation" is a **log-field widening**, not a new telemetry pipeline. The keeper already emits `LogWarning` on drop and would emit `keeper_messages_sent` on reinject; both flow to ES/Prom. The gap is that the drop log lacks the `CorrelationId`/`ExecutionId` join keys the analyzer needs.

## Runtime State Inventory

> This is a verdict-logic + config + instrumentation rework, NOT a rename/migration. No stored-data renames. But the TTL change (D75-6) and observation-window change (D75-7) alter live-service config, so an abbreviated inventory applies.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — the L2 keyspace shape is unchanged (`skp:data:{entryId}`, `skp:out:{messageId}`). Only TTL *values* change, not key shapes. | None (data migration N/A) |
| Live service config (TTL) | Four TTL floors all default 300s: `ProcessorLivenessOptions.ExecutionDataTtlSeconds` (`ProcessorLivenessOptions.cs:51`), `RecoveryOptions.ExecutionDataTtlSeconds` (`RecoveryOptions.cs:19`), `OrchestratorOutputOptions.OutputDataTtlSeconds` (`OrchestratorOutputOptions.cs:11`), + `appsettings.json` `Redis:ExecutionDataTtl:300` (`Processor.Sample/appsettings.json:33`). The "unified slot-array index TTL" the ROADMAP references is the SAME `ExecutionDataTtl` floor unified in quick `260615-dbf-unify-l2-slot-array-index-ttl`. | Code edit: raise defaults / config edit: raise test-env appsettings. Verify all four move in lock-step (they cross-reference each other's 300 default). |
| Live service config (keeper logs) | Keeper REINJECT drop log carries only `EntryId` (`ReinjectConsumer.cs:43`); no success log for a completed reinject beyond `CountSent` metric. | Code edit: widen drop log fields + add structured reinject-success log (D75-4). |
| Observation window | `phase-67-harness.ps1:257-258` `$windowSeconds = 300` (wall-clock). RECOVERY_UTC recorded `:231,330`. | Script edit if D75-7 (execution-based window) is adopted; the fixture drain (`AnalyzerE2ETests.cs:56` `DrainMs=60_000`) is separate. |
| Secrets/env vars | None. The env seam (SCENARIO_ID/WINDOW_*_UTC/RECOVERY_UTC) is harness-set, cleared in `finally` (`phase-67-harness.ps1:414`). A NEW env var may be added if D75-7 uses `K_EXECUTIONS`. | None (or add + clear a new seam symmetrically). |
| Build artifacts | Docker container images rebuild from source on `phase-65-up`. No stale-artifact hazard for a config/log change. | Rebuild containers before a live sweep (standard). |

**Nothing found for stored-data renames** — verified: the phase changes verdict logic, TTL magnitudes, and log fields, none of which reshape any L2 key or ES field name.

## Common Pitfalls

### Pitfall 1: Removing `maxInFlightLoss` param breaks hermetic facts
**What goes wrong:** `PassFailEngineFacts` / `PassFailEngineValueChainFacts` call `Analyze(...)` — some may pass `maxInFlightLoss` positionally or rely on the default.
**Why it happens:** It is the LAST optional parameter (`:111`). Commit e716e05 and 3028f43 both added facts exercising in-flight loss.
**How to avoid:** Grep every `.Analyze(` call site (3 files: `PassFailEngineFacts.cs`, `PassFailEngineValueChainFacts.cs`, `AnalyzerE2ETests.cs`) before removing the param. The RealStack caller (`AnalyzerE2ETests.cs:221-230`) does NOT pass it (uses default), so only the hermetic facts need audit.
**Warning signs:** Compile error on removed param, or a fact asserting `InFlightLoss > 4 ⇒ Fail`.

### Pitfall 2: Keeper drop log fields don't surface as ES attributes
**What goes wrong:** Adding `CorrelationId`/`ExecutionId` to the drop log via string interpolation (`$"... {m.CorrelationId}"`) instead of message-template placeholders means they DON'T become `attributes.*`.
**Why it happens:** Only `{Placeholder}` message-template args are parsed to structured state (`ParseStateValues=true`); an interpolated string is one opaque formatted message.
**How to avoid:** Use `logger.LogWarning("REINJECT drop ... {CorrelationId} {ExecutionId} {EntryId}", m.CorrelationId, m.ExecutionId, m.EntryId)` — placeholder form. Mirror how `SampleProcessor` emits `{StepLabel}`/`{Produced}` (surfaces as `attributes.StepLabel`/`attributes.Produced`).
**Warning signs:** ES query on `attributes.CorrelationId` for keeper docs returns zero hits.

### Pitfall 3: TTL raised in Options default but not in test-env appsettings (or vice-versa)
**What goes wrong:** The four TTL knobs cross-reference each other's 300 default in XML docs; raising one class default but leaving `appsettings.json` at 300 (or the reverse) leaves an inconsistent lifetime.
**Why it happens:** `appsettings.json` binds over the Options default (`ConfigurationKeyName("ExecutionDataTtl")`, `ProcessorLivenessOptions.cs:50`). The live container reads appsettings; a hermetic test reads the Options default.
**How to avoid:** Raise BOTH the Options-class defaults AND the three appsettings files (`Processor.Sample`, `Processor.BadConfig`, plus any orchestrator/keeper appsettings). Confirm the jitter policy `random[ttl, 2×ttl]` (`L2ProjectionKeys.cs:70-71`) still yields a floor ≥ recovery.
**Warning signs:** A non-redis scenario shows in-flight loss that the TTL was supposed to neutralize.

### Pitfall 4: Value-chain false-FAIL regression (undoing 3028f43)
**What goes wrong:** The reworked classifier stops populating the tolerated-keys set the value-chain loop skips (`:216`), reintroducing the TEST-03 orchestrator-crash false FAIL.
**Why it happens:** Commit 3028f43 is subtle — the tolerated in-flight-loss keys are collected during classification and skipped in a SEPARATE downstream loop.
**How to avoid:** Whatever replaces the `inFlightLoss`/`inFlightLossKeys` split (`:135-145`) must still emit a set of tolerated `(corr,exec)` keys that the value-chain loop (`:209-219`) skips. Keep the reproducing fact from 3028f43 green.
**Warning signs:** A terminal-only survivor (Step_G @ seed+6, no Step_B) recovers seed 0 and fails "Step_G expected 6 got 206".

### Pitfall 5: Redis-crash (TEST-05/07) scenarios have no keeper drop evidence
**What goes wrong:** For a redis crash, L2 is the wiped store — the keeper's `STRLEN L2[entryId]` probe itself fails (Redis exception → routed to exhaustion, NOT a clean-absent drop), so there may be NO clean-drop log to key the "unrecoverable" classification on.
**Why it happens:** `ReinjectConsumer.cs:32-37`: a Redis EXCEPTION on the read routes to the exhaustion policy (`Guard`), it is NOT the by-design silent drop (which requires a SUCCESSFUL STRLEN returning 0).
**How to avoid:** Decision 5 already rules TEST-05/07 in-flight-at-wipe loss as tolerated *by the durability boundary* (redis is the wiped store), NOT by keeper drop evidence. The classifier must tolerate redis-crash incomplete runs via the scenario/durability path, and use keeper-drop evidence for the NON-redis scenarios. Keep the existing `Mg1Binding[TEST-05/07]=false` reporting-only treatment (`AnalyzerE2ETests.cs:90-91`).
**Warning signs:** TEST-05/07 FAIL because no keeper drop log exists to justify their in-flight loss.

## Code Examples

### The exact drop condition to instrument (ReinjectConsumer.cs:35-45)
```csharp
// Source: src/Keeper/Recovery/ReinjectConsumer.cs
var present = await Guard(
    () => Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)),  // STRLEN skp:data:{entryId}
    ct) != 0;
if (!present)
{
    // CURRENT: only EntryId — analyzer cannot join to (corr,exec).
    logger.LogWarning("REINJECT drop: L2 data gone EntryId={EntryId}", m.EntryId);   // structured hole
    return;                                                                          // D-06 silent ack
}
// ... reinject path: builds EntryStepDispatch, sends to queue:{ProcessorId}, CountSent (:65)
```
**D75-4 change:** widen to `logger.LogWarning("REINJECT drop ... {CorrelationId} {ExecutionId} {EntryId} {MessageId}", m.CorrelationId, m.ExecutionId, m.EntryId, m.MessageId)` and add a symmetric structured success log before/after `CountSent(...)` so a *reinjected* (recoverable) execution is also joinable.

### The join keys already on the contract (KeeperReinject.cs:10-13)
```csharp
// Source: src/Messaging.Contracts/KeeperReinject.cs — all four ids are present
public Guid CorrelationId { get; init; }
public Guid ExecutionId   { get; init; }
public Guid EntryId       { get; init; }
public Guid MessageId     { get; init; }
```

### The verdict + classifier to rework (PassFailEngine.cs:170-174, 293)
```csharp
// Source: tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
var missing = bindingMissing;
var inFlightOverBound = inFlightLoss > maxInFlightLoss;   // ← DELETE (D75-2); replace with keeper-evidence classifier (D75-3)
...
var pass = missing == 0 && !inFlightOverBound && !dupFail && valueChainOk && metricGateOk;   // ← drop !inFlightOverBound term
```

### The four TTL knobs (all default 300s — raise for D75-6)
```csharp
// src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs:50-51
[ConfigurationKeyName("ExecutionDataTtl")] public int ExecutionDataTtlSeconds { get; set; } = 300;
// src/Keeper/RecoveryOptions.cs:19            public int ExecutionDataTtlSeconds { get; set; } = 300;
// src/Orchestrator/Configuration/OrchestratorOutputOptions.cs:11  public int OutputDataTtlSeconds { get; set; } = 300;
// src/Processor.Sample/appsettings.json:33    "ExecutionDataTtl": 300
// Shared jitter policy — src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:70-71
public static TimeSpan OutputDataTtl(int ttlSeconds)
    => TimeSpan.FromSeconds(Random.Shared.Next(ttlSeconds, 2 * ttlSeconds + 1));   // random[ttl, 2×ttl]
```

## The 7 scenarios (phase-67-harness.ps1:88-94)

| Scenario | Target | Fault | Dwell | redis-crash? | TTL relevant? (D75-6) | Mg1Binding |
|----------|--------|-------|-------|-------------|----------------------|-----------|
| TEST-01 | (none) | none | 0s | no | baseline | true |
| TEST-02 | processor-sample | stop-start | 45s | no | **YES — raise TTL** | false (counter reset) |
| TEST-03 | orchestrator | stop-start | 45s | no | **YES — raise TTL** | false (counter reset) |
| TEST-04 | keeper | stop-start | 45s | no | **YES — raise TTL** | true |
| TEST-05 | redis | stop-start | 45s | **YES** | moot (redis is wiped store — in-flight-at-wipe boundary) | false (L2 wipe) |
| TEST-06 | rabbitmq | stop-start | 45s | no | **YES — raise TTL** | true |
| TEST-07 | redis+rabbitmq | stop-start | 45s | **YES** | moot (redis wiped) | false (L2 wipe) |

`injectAfterNFires=4` for all faults → the fault lands after 4 cron fires. Recovery = dwell(45s) + return-to-healthy + keeper reinject + drain(60s) + poll-to-stable(≤60s). The ROADMAP's "~300s+" recovery-outlasting TTL target lines up: raise the non-redis TTLs to e.g. 900s (3× the 300s window) so a stalled-then-reinjected execution's L2 blob survives until the keeper replays it.

## Validation Architecture

> nyquist_validation is enabled (config.json `workflow.nyquist_validation: true`).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (`TestContext.Current.CancellationToken`), .NET 8 |
| Config file | `tests/BaseApi.Tests/*.csproj` (MTP runner) |
| Quick run (hermetic) | `tests/BaseApi.Tests/bin/<cfg>/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` |
| Engine-only run | `... BaseApi.Tests.exe --filter-class *PassFailEngineFacts --filter-class *PassFailEngineValueChainFacts` |
| Live sweep | `pwsh -File scripts/phase-68-sweep.ps1` (all 7) or `-ScenarioIds TEST-03` |

### Observable signals + how the analyzer proves recovery (the VALIDATION.md core)

The verdict is a pure function over these observable signals (all already ES/Prom-sourced except the NEW keeper-outcome join):

| Signal | Source | Proves | New? |
|--------|--------|--------|------|
| STARTED = distinct `(corr,exec)` with ≥1 Step_* log | ES `attributes.CorrelationId`+`ExecutionId` | Denominator (D75-1) | existing |
| COMPLETE = 9-hop distinct label set | ES `attributes.StepLabel` (`PassFailEngine.HopLabels`) | Real recovery per execution | existing |
| Value-chain intact = `value == seed + hop-count`, Step_G ×2 @ seed+6 | ES `attributes.Produced` | Effect correctness, not just count | existing (preserve 3028f43) |
| Illegitimate duplicate (Step_G ≠ ×2, any other ×>1) | ES label multiplicity | Effect-once | existing |
| **Keeper REINJECT-vs-DROP per `entryId`** | **ES keeper `attributes.EntryId/CorrelationId/ExecutionId/outcome`** | **Recoverable-but-lost (FAIL) vs provably-unrecoverable (tolerated)** | **NEW (D75-4)** |
| MG-1/2/3 metric gate | Prom `keeper_messages_consumed/sent`, `keeper_l2_probe`, conservation | Corroboration (binding per-scenario) | existing |

**The recovery proof (post-rework):** a green verdict ⟺ every started `(corr,exec)` either (a) completed all 9 hops with an intact value-chain, or (b) is a keeper-confirmed clean-absent DROP (provably-unrecoverable, reported+labeled, non-binding), or (c) is an in-flight-at-wipe loss on a redis-crash scenario (TEST-05/07, durability boundary). Any started execution that was recoverable (keeper could have reinjected — L2 blob present) but did not complete is a BINDING FAIL. No absolute count, no window-seconds, no cron-rate appears in the verdict.

### Phase Requirements → Test Map
| Req | Behavior | Test Type | Command | Exists? |
|-----|----------|-----------|---------|---------|
| D75-1 | denominator keyed `(corr,exec)` | unit | `*PassFailEngineFacts` | ✅ existing |
| D75-2 | no `MaxInFlightLoss` in verdict | unit | new fact: in-flight count N (any N) does not by itself fail | ❌ Wave 0 |
| D75-3 | classifier: recoverable-lost→FAIL, unrecoverable→tolerated | unit | new facts: keeper-drop map ⇒ tolerated; no-drop incomplete ⇒ FAIL | ❌ Wave 0 |
| D75-4 | keeper log fields surface + analyzer joins | unit (keeper log) + RealStack (join) | `*ReinjectConsumerFacts` asserts structured fields; live sweep | ⚠️ extend `ReinjectConsumerFacts.cs` |
| D75-5 | in-flight-at-wipe tolerated; recoverable-lost binding | unit | new facts per Pitfall 5 (redis vs non-redis) | ❌ Wave 0 |
| D75-6 | raised TTL outlasts recovery | RealStack (TEST-02/03/04/06 zero in-flight loss) | `phase-68-sweep.ps1` | manual/live gate |
| D75-7 | execution-based window (optional) | script + fixture | harness edit; manual verify | ❌ if adopted |
| D75-8 | value-chain fix preserved | unit | 3028f43 reproducing fact stays green | ✅ existing |

### Sampling Rate
- **Per task commit:** engine-only hermetic run (`*PassFailEngineFacts` + `*PassFailEngineValueChainFacts` + `*ReinjectConsumerFacts`) — sub-30s.
- **Per wave merge:** full hermetic suite (`--filter-not-trait Category=RealStack`).
- **Phase gate:** full hermetic green + a live `phase-68-sweep.ps1` 7/7 (operator-gated — Docker; deferred-automated per prior phases when Docker unavailable).

### Wave 0 Gaps
- [ ] New hermetic facts in `PassFailEngineFacts.cs` / a new `PassFailEngineRecoverabilityFacts.cs` — cover D75-2, D75-3, D75-5 (synthetic `RunTrace` + synthetic keeper-outcome map).
- [ ] Extend `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs` — assert the drop + success logs carry the structured join fields (D75-4).
- [ ] New `Analyze` parameter (`keeperOutcomeByExecution`) + corresponding `AnalyzerReport` field for the classification detail.
- No framework install needed — xUnit + hermetic harness already in place.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Keeper `LogWarning` structured placeholders surface as `attributes.<Name>` in ES exactly like `SampleProcessor`'s Step logs | D75-4 / Pitfall 2 | If the keeper's log pipeline differs (e.g. a filter drops warnings or a different resource shape), the analyzer join fails — verify with a Wave-0 live ES probe of a keeper drop doc before building the join |
| A2 | The "unified slot-array index TTL" the ROADMAP names is the SAME `ExecutionDataTtl` floor unified in quick `260615-dbf` (not a distinct 5th knob) | D75-6 | If a separate index TTL exists elsewhere, raising only the 4 found knobs leaves a confounder — grep for any `KeyExpire`/`expiry:` on an index/SADD key during planning |
| A3 | The value-chain fix (3028f43) skip-set is the only place tolerated keys must be excluded downstream | Pitfall 4 / D75-8 | If a future consumer of `inFlightLossKeys` exists, the rework could regress it |
| A4 | Raising non-redis TTLs to ~3× the window (e.g. 900s) is sufficient headroom and does not break the net-zero close gate (residual TTL-bound keys) | D75-6 | Longer TTLs mean `RealStackNetZeroSweepFixture` (active DELETE sweep, not TTL wait) still reclaims them — verified it deletes `skp:data:*`/`skp:msg:*` actively (`RealStackNetZeroSweepFixture.cs:61`), so longer TTL is safe; but confirm no gate does a TTL-expiry wait |

## Open Questions

1. **Does the keeper drop/reinject log reliably reach ES within the analyzer's window?**
   - What we know: keeper logs go through the OTLP→ES pipeline (`BaseConsoleObservabilityExtensions`); the analyzer already tolerates ~60s OTLP export skew via `DrainMs=60_000` + poll-to-stable.
   - What's unclear: whether keeper warning-level logs are filtered by any `LogLevelFilter` before export.
   - Recommendation: Wave-0 live probe — inject a drop, query ES for the keeper doc, confirm the join fields land. (`LogLevelFilterTests.cs` exists — check the console log level.)

2. **Decision 7 (execution-based window) — adopt or defer?**
   - What we know: it is explicitly marked "Optionally" in the ROADMAP; `ConservationTol=±1` is already non-binding + drain-settled.
   - What's unclear: whether a fixed-K window materially improves determinism vs the 300s wall-clock for this milestone.
   - Recommendation: treat as a stretch requirement in 75-SPEC; the core verdict decoupling (D75-1..5,8) does not depend on it.

3. **For redis-crash TEST-05/07, what is the exact tolerated-classification path if there is NO keeper drop log (Pitfall 5)?**
   - What we know: a Redis exception routes to exhaustion, not a clean-absent drop; decision 5 tolerates in-flight-at-wipe by the durability boundary.
   - What's unclear: whether the classifier keys TEST-05/07 tolerance off the scenario id, off `Mg1Binding=false`, or off the timestamp heuristic (last hop < RECOVERY_UTC).
   - Recommendation: keep the timestamp heuristic (`stalledBeforeRecovery`) as the redis-wipe path AND layer keeper-drop evidence for non-redis scenarios — document the two paths explicitly in 75-SPEC.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build + hermetic tests | assumed ✓ (repo builds today) | 8.x | — |
| `BaseApi.Tests.exe` (MTP) | hermetic facts | ✓ (per MEMORY hermetic-test-command) | — | — |
| Docker + compose stack (redis/rabbitmq/ES/Prometheus/orchestrator/processor/keeper) | D75-4 live join, D75-6/7 live sweep | ✗ per prior phases (74 close gate deferred-automated: "Docker unavailable") | — | Hermetic facts prove D75-1,2,3,5,8; live gate deferred-automated (precedent set in phases 68/73/74) |
| `pwsh` | sweep scripts | assumed ✓ (scripts exist + run) | — | — |

**Missing with fallback:** Docker/live stack — the verdict-logic rework (the bulk of the phase) is fully hermetic-provable; the ES-join (D75-4 live half) and TTL/window neutralization (D75-6/7) are validated on the live sweep, which prior phases have run deferred-automated when Docker is up. No blocking dependency for the core logic.

## Sources

### Primary (HIGH confidence — read this session)
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (full) — verdict formula, MaxInFlightLoss, B-criterion classifier, ConservationTol, 3028f43 skip-set
- `src/Keeper/Recovery/ReinjectConsumer.cs` + `RecoveryConsumerBase.cs` — the STRLEN drop, REINJECT send, CountSent
- `src/Messaging.Contracts/KeeperReinject.cs` — join keys on the contract
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — key shapes + OutputDataTtl jitter
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (full) — the Analyze caller, ES read, RECOVERY_UTC/window seams
- `scripts/phase-67-harness.ps1` (scenarios + window/dwell/RECOVERY_UTC), `scripts/phase-68-sweep.ps1` (roll-up)
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` — MEL→OTLP log-attribute bridge
- TTL knobs: `ProcessorLivenessOptions.cs:50-51`, `RecoveryOptions.cs:19`, `OrchestratorOutputOptions.cs:11`, `Processor.Sample/appsettings.json:33`
- `RunTrace.cs`, `EsIndexNames.cs` — trace model + ES field paths
- Commits `3028f43` (value-chain in-flight-loss fix), `e716e05` (in-flight vs post-recovery classification) — `git show`
- `.planning/ROADMAP.md:748-753` — Phase 75 goal (authoritative)

### Secondary
- Auto-memory: `steps-api-architecture.md`, `hermetic-test-command.md`, `gsd-phase-numbering.md`

## Metadata

**Confidence breakdown:**
- Verdict logic (D75-1,2,3,5,8): HIGH — full source read, exact line numbers, hermetic-provable
- Keeper instrumentation (D75-4): HIGH on the code path + join keys; MEDIUM on live ES-surfacing (A1 — needs Wave-0 probe)
- TTL config (D75-6): HIGH — all four knobs located; MEDIUM on "5th index TTL" existence (A2)
- Observation window (D75-7): HIGH — window var located; it is explicitly optional

**Research date:** 2026-07-15
**Valid until:** ~2026-08-15 (stable in-repo code; only churns if the PassFailEngine or keeper recovery path is edited before planning)
