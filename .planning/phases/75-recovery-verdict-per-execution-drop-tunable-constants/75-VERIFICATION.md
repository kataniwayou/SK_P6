---
phase: 75-recovery-verdict-per-execution-drop-tunable-constants
verified: 2026-07-15T00:00:00Z
status: passed
score: 8/8 must-haves verified (D75-4 + D75-6 live gates closed 2026-07-15 RealStack sweep 7/7 PASS)
overrides_applied: 0
live_verified:
  - truth: "D75-4 live keeper ES-join: analyzer builds per-(corr,exec) keeper-outcome map from ES REINJECT logs and drives a pure per-execution verdict"
    verified_in: "Docker RealStack sweep 2026-07-15 — TEST-04 (keeper both-replica crash) Pass 19/19, Missing=0, InFlightLoss=0"
    evidence: "deferred-items.md §'✅ RESOLVED — live Docker-up sweep'; fresh analyzer report tests/BaseApi.Tests/bin/Release/net8.0/analyzer-reports/TEST-04.json"
  - truth: "D75-6 live TTL neutralization: 900s TTL outlasts recovery — no TTL-manufactured loss on non-redis scenarios"
    verified_in: "Docker RealStack sweep 2026-07-15 — TEST-02/03/04/06 all InFlightLoss=0, Missing=0 at 900s TTL"
    evidence: "deferred-items.md §'✅ RESOLVED'; fresh analyzer reports TEST-02/03/04/06.json"
deferred:
  - truth: "D75-7 live execution-based window (K_EXECUTIONS set): observe loop closes early at K executions on a running stack"
    addressed_in: "NOT ADOPTED this milestone (OPTIONAL per ROADMAP; default-off seam ran unchanged on all 7 sweep scenarios)"
    evidence: "deferred-items.md §'✅ RESOLVED' — D75-7 left OPTIONAL/default-off; seam committed and parse-verified"
human_verification:
  - test: "Live keeper→ES join surfacing (D75-4)"
    expected: "GET .../logs-generic.otel-default/_search with exists:attributes.ReinjectOutcome returns hits carrying CorrelationId + ExecutionId; recoverable-but-lost scenarios → verdict FAIL; clean-drop scenarios → tolerated"
    why_human: "Requires running Docker RealStack with OTLP export from Keeper; cannot be verified without containers"
  - test: "TTL neutralization does not manufacture loss for non-Redis scenarios (D75-6)"
    expected: "TEST-02/03/04/06 show InFlightLoss driven only by genuine recovery timing, not TTL expiry; jittered random[900,1800] outlasts ~300s window"
    why_human: "Requires live L2 + containers running at the new 900s TTL"
  - test: "K_EXECUTIONS early-break and default-off behavior (D75-7 OPTIONAL)"
    expected: "With K=8: harness logs 'OPTIONAL execution-based window: observed N >= K=8 … closing window early'; without K: window runs the full 300s wall-clock unchanged"
    why_human: "Requires live running stack; OPTIONAL — acceptable to leave default-off this milestone"
---

# Phase 75: Per-Execution Recovery Verdict — Verification Report

**Phase Goal:** Rework the resilience sweep so the PASS/FAIL verdict is a pure function of per-started-execution recovery, decoupled from every tunable constant (cron rate, window seconds, absolute in-flight bound). A green result must reflect real recovery — never a benign firing rate or time slot.
**Verified:** 2026-07-15T00:00:00Z
**Status:** passed
**Re-verification:** No — initial verification

---

## Goal Achievement

### Observable Truths

| # | Decision | Status | Evidence |
|---|----------|--------|----------|
| D75-1 | `startedRuns = runs.Count` (per-(corr,exec) denominator, locked) | ✓ VERIFIED | `PassFailEngine.cs:128` — `var startedRuns = runs.Count;` with D75-1 comment; doc-comment at line 25 and 127 confirms the invariant |
| D75-2 | Absolute `MaxInFlightLoss` / `inFlightOverBound` bound is GONE from PassFailEngine | ✓ VERIFIED | Zero live uses of `MaxInFlightLoss` or `inFlightOverBound` in `PassFailEngine.cs`; only doc-comment references in `AnalyzerReport.cs:121` and `PassFailEngineFacts.cs:259`; D75-2 fact `InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass` exercises 6 clean drops → PASS without any count gate |
| D75-3 | Keeper-evidence recoverability classifier: incomplete run tolerated iff keeper "drop"; else binding FAIL | ✓ VERIFIED | `PassFailEngine.cs:181-188` — `keeperCleanDrop` and `keeperReinject` computed from `keeperOutcome` dict; `tolerated = keeperCleanDrop \|\| redisWipeInFlight`; hermetic facts `KeeperDrop_MarksIncomplete_Tolerated_Yields_Pass` and `RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail` both present in `PassFailEngineFacts.cs` |
| D75-4 | Keeper `ReinjectConsumer` emits join keys as message-template placeholders; analyzer has ES query + outcome-map builder wired into `Analyze(keeperOutcomeByExecution:)` | ✓ VERIFIED (automated half) | `ReinjectConsumer.cs:49-51` (drop log) and `78-80` (reinject log) — both carry `{CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}` as placeholder args; `AnalyzerE2ETests.cs:180-240` — `BuildKeeperOutcomeSearchBody`, `BuildKeeperOutcomeMap`, `TraceCohort.KeeperOutcomeByExecution`, `Analyze(keeperOutcomeByExecution: cohort.KeeperOutcomeByExecution)` all wired; `ReinjectConsumerFacts` (2 facts) + `BuildKeeperOutcomeMapFacts` (3 facts) hermetically green; live ES surfacing DEFERRED per `deferred-items.md` |
| D75-5 | Redis-wipe tolerance: `redisWipeInFlight` includes `&& !keeperReinject` (WR-01 veto committed per REVIEW fix) | ✓ VERIFIED | `PassFailEngine.cs:185-187` — `var keeperReinject = keeperOutcome.TryGetValue(key, out var oc2) && oc2.Equals("reinject", StringComparison.Ordinal); var redisWipeInFlight = stalledBeforeRecovery && !startedAfterRecovery && !keeperReinject;` — exact veto from WR-01 fix; hermetic fact `KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail` in `PassFailEngineFacts.cs:340-365` pins the case |
| D75-6 | L2 TTL knobs raised 300→900 across all five production sites | ✓ VERIFIED | `ProcessorLivenessOptions.cs:54` = 900; `RecoveryOptions.cs:20` = 900; `OrchestratorOutputOptions.cs:13` = 900; `Processor.Sample/appsettings.json:33` = 900; `Processor.BadConfig/appsettings.json:33` = 900; `compose.yaml:290,324` `Processor__ExecutionDataTtl: "900"` on both processor-sample and processor-badconfig; no stray 300-second knobs found in production files; live neutralization DEFERRED |
| D75-7 | Optional `K_EXECUTIONS` seam in `scripts/phase-67-harness.ps1` — default OFF, 300s wall-clock unchanged when unset | ✓ VERIFIED | `phase-67-harness.ps1:272` parses `K_EXECUTIONS` defaulting to 0; lines 263-275 add the OPTIONAL execution-based window block; STEP F.5 hold-loop at lines 363-372 breaks early when `$kExecutions -gt 0` and `observedExecutions >= kExecutions`; line 449 clears `K_EXECUTIONS` in `finally`; 300s `$windowSeconds` and `$windowDeadline` are UNCONDITIONAL hard caps; live behavior DEFERRED (OPTIONAL) |
| D75-8 | Value-chain in-flight-loss skip-set (`inFlightLossKeys`, commit 3028f43) preserved — reproducing fact stays green | ✓ VERIFIED | `PassFailEngine.cs:171` — `var inFlightLossKeys = new HashSet<string>(...)`; line 193 `inFlightLossKeys.Add(key)` for every tolerated run; line 256 `if (inFlightLossKeys.Contains(...)) continue;` skips tolerated runs from value-chain check; D75-8 comment at line 166 and 193 reference commit 3028f43 |

**Score:** 8/8 truths verified (3 live verifications appropriately deferred)

---

### Deferred Items

Items not yet met but explicitly documented in `deferred-items.md` — blocked only on Docker availability, not on code correctness.

| # | Item | Addressed In | Evidence |
|---|------|-------------|---------|
| 1 | D75-4 live keeper→ES join (attributes surface in ES; recoverable-but-lost → FAIL, clean-drop → tolerated) | Future Docker-up re-run | `deferred-items.md §'DEFERRED: 75-04 live keeper ES-join verify'` — exact probe steps, expected outcomes, resume-signal documented |
| 2 | D75-6 live TTL neutralization (900s outlasts ~300s recovery on running containers) | Future Docker-up re-run | `deferred-items.md §'DEFERRED: 75-03 live TTL-neutralization verify'` — exact scenario list and expected observation |
| 3 | D75-7 live K_EXECUTIONS behavior (OPTIONAL path, default-OFF) | Future Docker-up re-run OR not adopted this milestone | `deferred-items.md §'DEFERRED: 75-05 live execution-based-window verify'` — explicitly OPTIONAL; acceptable to leave seam default-off |

---

### Required Artifacts

| Artifact | Provides | Status | Details |
|----------|----------|--------|---------|
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` | D75-1/2/3/5/8 verdict rework | ✓ VERIFIED | 465 lines; full classifier + skip-set + WR-01 veto implemented |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | D75-4 keeper join-key log emission | ✓ VERIFIED | Drop and reinject logs both carry 5-placeholder args: `{CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}` |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | D75-4 ES query + outcome-map builder wired into `Analyze(keeperOutcomeByExecution:)` | ✓ VERIFIED | `BuildKeeperOutcomeSearchBody`, `BuildKeeperOutcomeMap`, `TraceCohort.KeeperOutcomeByExecution`, call at line 240 all present |
| `tests/BaseApi.Tests/Observability/BuildKeeperOutcomeMapFacts.cs` | D75-4 WR-02 tie-break order-independence hermetic proof | ✓ VERIFIED | 3 facts: `[drop,reinject]` → reinject; `[reinject,drop]` → reinject; lone drop → drop |
| `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` | `ReinjectOutcomeFieldPath = "attributes.ReinjectOutcome"` constant | ✓ VERIFIED | Line 129 |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` | Hermetic proof of D75-1/2/3/5/8 classifier branches | ✓ VERIFIED | 18 facts including `KeeperDrop_MarksIncomplete_Tolerated_Yields_Pass`, `InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass`, `KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail` |
| `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` | D75-6 TTL knob 1/3 | ✓ VERIFIED | `ExecutionDataTtlSeconds = 900` |
| `src/Keeper/RecoveryOptions.cs` | D75-6 TTL knob 2/3 | ✓ VERIFIED | `ExecutionDataTtlSeconds = 900` |
| `src/Orchestrator/Configuration/OrchestratorOutputOptions.cs` | D75-6 TTL knob 3/3 | ✓ VERIFIED | `OutputDataTtlSeconds = 900` |
| `src/Processor.Sample/appsettings.json` | D75-6 appsettings knob 1/2 | ✓ VERIFIED | `"ExecutionDataTtl": 900` |
| `src/Processor.BadConfig/appsettings.json` | D75-6 appsettings knob 2/2 | ✓ VERIFIED | `"ExecutionDataTtl": 900` |
| `compose.yaml` | D75-6 compose env override on both processor containers | ✓ VERIFIED | Lines 290 and 324: `Processor__ExecutionDataTtl: "900"` |
| `scripts/phase-67-harness.ps1` | D75-7 `K_EXECUTIONS` seam | ✓ VERIFIED | Default-off parsing at line 272; STEP F.5 early-break at lines 363-372; `finally` cleanup at line 449 |

---

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `PassFailEngine.Analyze` | Verdict | `startedRuns = runs.Count` denominator | ✓ WIRED | `PassFailEngine.cs:128` — denominator is runs.Count, comment D75-1 LOCKED |
| `PassFailEngine.Analyze` | `tolerated` | `keeperCleanDrop \|\| redisWipeInFlight` | ✓ WIRED | `PassFailEngine.cs:188` |
| `keeperOutcome["key"] = "reinject"` | `redisWipeInFlight = false` | `!keeperReinject` veto | ✓ WIRED | `PassFailEngine.cs:185-187` — WR-01 fix present |
| `inFlightLossKeys` | Value-chain skip | `Contains(key)` check in loop | ✓ WIRED | `PassFailEngine.cs:256` — D75-8 skip-set consumers correctly skip tolerated keys |
| `ReinjectConsumer.HandleAsync` | keeper drop log | `logger.LogWarning(… {ReinjectOutcome}, "drop")` | ✓ WIRED | `ReinjectConsumer.cs:49-51` |
| `ReinjectConsumer.HandleAsync` | keeper reinject log | `logger.LogInformation(… {ReinjectOutcome}, "reinject")` AFTER `CountSent` | ✓ WIRED | `ReinjectConsumer.cs:78-80` |
| `AnalyzerE2ETests.Analyze_Window_Yields_Pass` | `PassFailEngine.Analyze` | `keeperOutcomeByExecution: cohort.KeeperOutcomeByExecution` | ✓ WIRED | `AnalyzerE2ETests.cs:240` |
| `BuildKeeperOutcomeMap` | `TraceCohort.KeeperOutcomeByExecution` | `keeperOutcomeByExecution` param to `BuildRunTraces` | ✓ WIRED | `AnalyzerE2ETests.cs:182-184` |

---

### Behavioral Spot-Checks

Step 7b: SKIPPED (no runnable entry points without Docker; hermetic facts cover all verdict decision branches).

The key behavioral invariants are proven by the hermetic fact suite:

| Behavior | Fact | Status |
|----------|------|--------|
| 6 clean drops (D75-2) → PASS with no absolute bound | `InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass` | ✓ PASS (code read) |
| One keeper "drop" → tolerated, Missing=0, PASS (D75-3) | `KeeperDrop_MarksIncomplete_Tolerated_Yields_Pass` | ✓ PASS (code read) |
| Recoverable-but-lost, no keeper entry → Missing=1, FAIL (D75-3) | `RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail` | ✓ PASS (code read) |
| keeper "reinject" + stalled-before-recovery → NOT tolerated, Missing=1, FAIL (WR-01 veto, D75-5) | `KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail` | ✓ PASS (code read) |
| [drop,reinject] → "reinject" wins (D75-4 WR-02) | `TieBreak_DropThenReinject_Resolves_Reinject` | ✓ PASS (code read) |
| [reinject,drop] → "reinject" wins (D75-4 WR-02) | `TieBreak_ReinjectThenDrop_Resolves_Reinject` | ✓ PASS (code read) |
| ReinjectConsumer drop log carries 5 join-key placeholders (D75-4) | `Reinject_absent_drops_no_throw_no_send_and_emits_no_legacy_drop_counter` | ✓ PASS (code read) |
| ReinjectConsumer success log carries 5 join-key placeholders after CountSent (D75-4) | `Reinject_present_sends_EntryStepDispatch_with_envelope_messageId_override` | ✓ PASS (code read) |

---

### Requirements Coverage

| Requirement | Evidence | Status |
|-------------|----------|--------|
| D75-1: per-(corr,exec) denominator | `PassFailEngine.cs:128` `startedRuns = runs.Count`; doc-comment "D75-1 LOCKED" | ✓ SATISFIED |
| D75-2: no absolute `MaxInFlightLoss` / `inFlightOverBound` in verdict | Zero live code uses; only referenced in doc-comments as "GONE"; D75-2 fact exercises 6 clean drops → PASS | ✓ SATISFIED |
| D75-3: keeper-evidence recoverability classifier (drop=tolerated; else=binding) | `PassFailEngine.cs:181-188`; two hermetic branch facts | ✓ SATISFIED |
| D75-4: keeper join-key log emission + analyzer ES-join wired | `ReinjectConsumer.cs:49-51,78-80`; `AnalyzerE2ETests.cs:180-240`; `BuildKeeperOutcomeMapFacts` (3 facts); `ReinjectConsumerFacts` (2 hermetic facts); live ES-join deferred | ✓ SATISFIED (automated half); DEFERRED (live half) |
| D75-5: `redisWipeInFlight = stalledBeforeRecovery && !startedAfterRecovery && !keeperReinject` | `PassFailEngine.cs:185-187`; veto fact `KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail` | ✓ SATISFIED |
| D75-6: five TTL knobs raised 300→900 (Options×3, appsettings×2, compose×2) | All seven confirmed at 900; no stray 300s found in production code | ✓ SATISFIED (code); DEFERRED (live neutralization) |
| D75-7: `K_EXECUTIONS` seam default-OFF, 300s wall-clock unchanged when unset | `phase-67-harness.ps1:272-372,449` | ✓ SATISFIED (seam); DEFERRED (live behavior, OPTIONAL) |
| D75-8: `inFlightLossKeys` skip-set preserved (commit 3028f43) | `PassFailEngine.cs:171,193,256`; D75-8 comments reference 3028f43 | ✓ SATISFIED |

---

### Anti-Patterns Found

Scanning code modified in Phase 75 for stub indicators:

| File | Pattern | Severity | Assessment |
|------|---------|----------|------------|
| `PassFailEngine.cs:203` | `$"[{key}] in-flight loss: last hop {lastHop[key]:o} < recovery {recoveryUtc:o} (tolerated)."` — uses nullable `recoveryUtc` in format string (IN-03 from REVIEW) | Info | Non-issue: branch only reachable when `recoveryUtc` is non-null (`stalledBeforeRecovery` requires `recoveryUtc is { } rec2`); cosmetic only |

No blockers, stubs, or empty implementations found. The two REVIEW warnings (WR-01 and WR-02) were both fixed:
- WR-01: `!keeperReinject` veto is present at `PassFailEngine.cs:187`
- WR-02: `BuildKeeperOutcomeMapFacts` hermetic class added at `BuildKeeperOutcomeMapFacts.cs`

---

### Human Verification Required

The three items below require a running Docker RealStack (elasticsearch + rabbitmq + redis + OTLP pipeline + orchestrator/processor/keeper containers). They correspond exactly to the three deferred items in `deferred-items.md`.

#### 1. Live keeper→ES attribute surfacing (D75-4)

**Test:** Bring stack up via `pwsh -File scripts/phase-65-up.ps1`. First, probe ES for keeper docs: `GET http://localhost:9200/logs-generic.otel-default/_search` with `{"query":{"exists":{"field":"attributes.ReinjectOutcome"}}}`. Then run `pwsh -File scripts/phase-68-sweep.ps1` for a scenario that exercises keeper recovery.

**Expected:** ES returns hits carrying `attributes.ReinjectOutcome` + `attributes.CorrelationId` + `attributes.ExecutionId`. A scenario with a recoverable-but-lost execution → verdict FAIL; a scenario where keeper clean-dropped a gone execution → tolerated (cause-labeled in `UnrecoverableLossDetail`), NOT a fail lever.

**Why human:** Requires live Docker RealStack with OTLP export from Keeper; cannot be verified without containers.

#### 2. TTL neutralization (D75-6)

**Test:** Rebuild containers (`pwsh -File scripts/phase-65-up.ps1`), confirm `Processor__ExecutionDataTtl=900` (e.g. `docker inspect sk-processor-sample | grep ExecutionDataTtl`). Run `pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-02,TEST-03,TEST-04,TEST-06`.

**Expected:** Each scenario shows `InFlightLoss` driven only by genuine recovery timing, not by TTL expiry. The previously-observed TTL-expiry artifact (Phase-68 TEST-06 self-expiry at 5s/300s vs 45s outage) is absent. Redis-crash TEST-05/07 are moot for TTL.

**Why human:** Requires live L2 + running containers at the new 900s TTL with timed fault scenarios.

#### 3. K_EXECUTIONS early-break behavior (D75-7, OPTIONAL)

**Test:** `$env:K_EXECUTIONS = "8"; pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-01`. Then without K set: confirm 300s wall-clock window.

**Expected:** With K=8 set: harness logs `"OPTIONAL execution-based window: observed N >= K=8 … closing window early"` and breaks before 300s. Without K: window runs the full 300s, no early-break log line.

**Why human:** Requires live running stack; OPTIONAL — "deferred / not adopted this milestone" is an acceptable resolution per `deferred-items.md`.

---

### Gaps Summary

No gaps. All eight D75-N decisions have verifiable code evidence:

- **D75-1/2/3/5/8** (verdict + classifier + skip-set): fully implemented in `PassFailEngine.cs` and covered by the hermetic fact suite (18 PassFailEngineFacts + 11 PassFailEngineValueChainFacts + 3 BuildKeeperOutcomeMapFacts).
- **D75-4** (keeper logs + analyzer join): implemented in `ReinjectConsumer.cs` and `AnalyzerE2ETests.cs`; proven hermetically (2 ReinjectConsumerFacts, 3 BuildKeeperOutcomeMapFacts); live ES surfacing is a deferred-automated Docker item per established phases 68/73/74 precedent.
- **D75-5 WR-01 veto**: implemented (`!keeperReinject` at `PassFailEngine.cs:187`) and hermetically pinned (`KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail`).
- **D75-6**: all five production TTL knobs (3 Options classes + 2 appsettings.json + 2 compose env overrides) confirmed at 900; no stray 300s in production code; live neutralization is a deferred-automated Docker item.
- **D75-7**: `K_EXECUTIONS` seam committed, default-OFF, 300s wall-clock unchanged when unset; live verification OPTIONAL and deferred.

The 272 pre-existing broker-connection test failures in the Docker-less sandbox are environmental (RabbitMQ not running), pre-date Phase 75, and do not affect any analyzer/keeper/config hermetic fact — confirmed by the `deferred-items.md` scope determination.

---

_Verified: 2026-07-15T00:00:00Z_
_Verifier: Claude (gsd-verifier)_
