# Resilience Suite Enhancement — Metric-Bound, Honest Proof

**Date:** 2026-06-18
**Status:** Design — pending review
**Scope constraint (load-bearing):** This work touches **`tests/` and `scripts/` only**. The production engine under `src/` is **not modified**. The enhancement changes *how the resilience proof is judged*, not *what is proven*.

---

## 1. Context

The platform has a 7-scenario live resilience suite, driven by `scripts/phase-67-harness.ps1` (per scenario: build → up → reset → seed → start → observe → inject fault → recover → analyze → teardown) and `scripts/phase-68-sweep.ps1` (run all 7 + roll-up). The deterministic proof workload is the `v8-fanout-proof` DAG `A→B→C→{D1→E1→F1, D2→E2→F2}→G` (shared convergent terminal `G`).

The seven scenarios:

| # | Fault |
|---|-------|
| TEST-01 | happy path (no fault) |
| TEST-02 | processor whole-tier crash |
| TEST-03 | orchestrator crash |
| TEST-04 | keeper crash |
| TEST-05 | redis crash |
| TEST-06 | rabbitmq crash |
| TEST-07 | redis + rabbitmq combined crash |

The verdict today is produced by `PassFailEngine` (`tests/BaseApi.Tests/Observability/Analysis/`), **scored entirely from Elasticsearch** (started runs, complete runs, missing, illegitimate duplicates, value-chain). Prometheus counters are read but explicitly **non-binding corroboration**.

A fresh full sweep against the current (phase-74) stack produced **7/7 PASS** — establishing a trustworthy green baseline before this enhancement.

## 2. Problem statement

Three weaknesses, all surfaced with live evidence this cycle (see Appendix A):

1. **Metric-name drift breaks tooling silently.** Phase 74 renamed `orchestrator_dispatch_sent → orchestrator_messages_sent` and updated the analyzer but **not** the ops harness, whose fire-gate polled the dead name and aborted every fault scenario at exit 60. The name is hard-coded in three independent places (instrument / analyzer / harness) with no drift detection. *(The harness instance is already fixed — commit `1f3ad75` — but nothing prevents recurrence.)*

2. **The Prometheus corroboration math is miscalibrated to its own instruments.** `PassFailEngine` derives `impliedRuns = round(orchestrator_messages_sent / 9)`. The `9` assumed "one dispatch per step" — true for the *old* counter. The new `orchestrator_messages_sent` counts ~**18–21 per run** (the two-consumer internal post-queue hop), so it computes ~2× the real run count and raises a spurious "dead-run" warning on every clean run. The companion `consumed ≈ sent + spawnExtra` check is broken for the same reason. The verdict is unaffected (Prom is non-binding) but the corroboration layer is now noise.

3. **The proof is ES-only, and one scenario is flaky.** Nothing binds on the business metrics, so a run can pass without proving recovery *did anything* (e.g. that the keeper actually performed recovery). And TEST-05 (redis) intermittently FAILs on a genuine but rare in-flight loss: an execution whose `data:` blob is in Redis at the exact wipe instant is unrecoverable (ephemeral Redis, no in-flight L3 persistence). The current all-or-nothing "zero missing" criterion treats this rare, expected loss identically to a real recovery failure.

## 3. Goals / Non-goals

**Goals**
- Make the existing suite's judgment trustworthy (fix calibration, prevent silent drift).
- Add a **binding metric gate** beside ES that proves recovery *happened correctly* (no message loss/leak; keeper recovery exercised where expected; keeper liveness intact).
- Make TEST-05 (and any L2-wipe scenario) **deterministic and honest**: tolerate-and-quantify the rare in-flight loss, while still catching real post-recovery failures and duplicates.

**Non-goals (explicit YAGNI)**
- **No durable in-flight `data:`** (no L3 persistence of in-flight blobs, no broker-carried blobs, no Redis AOF). This is the only path that would touch `src/`, and it contradicts the deliberate "ephemeral L2, rebuildable from L3" posture. Out of scope unless zero-loss-through-a-total-wipe becomes a hard requirement.
- **No recalibration of the `impliedRuns` math** — it is deleted, not fixed (redundant: ES already owns the run count).
- **No metric-name config framework** — a single hermetic guard test is sufficient.
- **No new fault shapes** (rolling/partial crashes, partitions) until the existing 7 are metric-trustworthy.

## 4. Design

Three independent increments, sequenced smallest-trust-first. Each lands and is verifiable on its own.

### Increment 1 — Trust the existing suite

**1a. Harness fire-gate fix** — *done* (`1f3ad75`): `Get-FireCount` polls `orchestrator_messages_sent_total`.

**1b. Retire the miscalibrated corroboration.** Remove from `PassFailEngine`:
- `impliedRuns = round(OrchestratorMessagesSentDelta / 9)` and its `deadRunExcess` "dead-run" warning, plus `LabelsPerRun`, `CorroborationRunTolerance`, `PromImpliedRuns`.
- the `expectedResultConsumed = sent + spawnExtra` spawn-recon warning and `spawnExtra`/`ExpectedResultConsumed`.
- the now-dead fields from `PromCounterSnapshot` / `AnalyzerReport`.

The verdict is already ES-binding, so this is removal-only — `Reconciliation` stops being spuriously `Unreconciled`. (The Prom snapshot fields that the new metric gate needs — Increment 2 — replace them.)

**1c. Metric-name drift guard.** A new hermetic test that reads the instrument name constant from the metric class (e.g. the `orchestrator_messages_sent` instrument name as defined in `src/Orchestrator/Observability/OrchestratorMetrics.cs`) and asserts the string appears in `scripts/phase-67-harness.ps1`. Fails at build time if a future rename drifts the harness again. (Read-only against `src/`; the test lives in `tests/`.)

**Exit criteria of Increment 1:** clean sweep shows `Reconciliation: Reconciled`; drift-guard test green; 7/7 still PASS.

### Increment 2 — A: binding metric gate (evaluated at quiescence)

**2a. Drain-before-snapshot.** The harness, after the observation window and *before* the analyzer snapshots Prometheus, **stops the workflow cron and waits for quiescence** (counters flat over a short interval, past the OTLP export interval). Rationale: the conservation invariant holds *only* at quiescence — a live windowed delta does not (evidence: TEST-05 windowed showed `orch_consumed 146` vs `proc_sent 124`; drained happy-path showed exact `896 == 896`).

**2b. Read keeper deltas.** `PromCounterSnapshot` gains `KeeperMessagesConsumedDelta`, `KeeperMessagesSentDelta`, and a `KeeperL2ProbeRate`; `AnalyzerE2ETests` adds the corresponding Prom reads. (The old analyzer ignored the keeper entirely.)

**2c. Binding assertions** (added to `Verdict`, *beside* the ES value-chain which remains the primary correctness arbiter):

- **MG-1 Result conservation (universal, binding):** `OrchestratorMessagesConsumedDelta == ProcessorMessagesSentDelta` at quiescence (tolerance ±1 for boundary). A mismatch is real message loss or leak → FAIL.
- **MG-2 Keeper recovery exercised (per-scenario, binding once calibrated):** for scenarios whose recovery path *runs through the keeper*, `KeeperMessagesConsumedDelta > 0 && KeeperMessagesSentDelta > 0`. **Calibration step:** which scenarios exercise the keeper is currently unknown (never measured). So MG-2 ships first **reporting-only**; the first informed sweep records per-scenario keeper deltas; a `expectsKeeperActivity` flag is then set per scenario and MG-2 becomes binding for those. Scenarios recovered purely by broker redelivery (no keeper) assert `== 0` instead.
- **MG-3 Keeper liveness (universal, binding):** `KeeperL2ProbeRate > 0` — the BIT probe is advancing.

**Exit criteria of Increment 2:** MG-1 and MG-3 binding and green across 7/7; per-scenario keeper expectations recorded and MG-2 set accordingly.

### Increment 3 — B: precise PASS criterion for L2-wipe scenarios

**3a. Export recovery timestamp.** The harness already records `WINDOW_START_UTC` / `WINDOW_END_UTC`; add `RECOVERY_UTC` = the moment the crashed tier returned healthy (per scenario; absent for TEST-01).

**3b. Classify incomplete runs in `PassFailEngine`.** Each STARTED-but-incomplete `(correlationId, executionId)` is classified by its hop timestamps:
- **Tolerated (in-flight-at-wipe):** its last logged hop is *before* `RECOVERY_UTC`. It was mid-traversal when L2 was wiped; unrecoverable by design. **Counted and reported, does not fail.**
- **FAIL:** an incomplete run whose *first* hop is *after* `RECOVERY_UTC` (started post-recovery and still didn't finish → recovery is broken); OR tolerated count exceeds a small bound `MaxInFlightLoss` (default derived from observed in-flight concurrency, ~2–4); OR any illegitimate duplicate (unchanged, still fail-closed).

The verdict becomes: **Pass iff** (every post-recovery run complete) **and** (no illegitimate duplicate) **and** (value-chain intact) **and** (MG-1, MG-3, and applicable MG-2 hold) **and** (in-flight loss ≤ `MaxInFlightLoss`).

**Honest boundary:** a "fully-dead" fire during an outage (dispatched but never logged `Step_A`, because L2 was down at fire time) leaves no ES trace and is *not* a started run; its identity is unrecoverable from telemetry. B's criterion is scoped to **started** runs. Outage-dead fires (the workflow simply not running while infra is down) are out of scope — expected, and uncountable from ES.

## 5. Architecture & data flow

```
phase-68-sweep.ps1 ─loops─▶ phase-67-harness.ps1 (per scenario)
                                 │ build/up/reset/seed/start
                                 │ observe N fires ─▶ inject fault ─▶ recover (record RECOVERY_UTC)
                                 │ hold window ─▶ DRAIN to quiescence (NEW, 2a)
                                 │ env seam: SCENARIO_ID, WINDOW_*_UTC, RECOVERY_UTC (NEW)
                                 ▼
                          AnalyzerE2ETests (RealStack fixture)
                                 │ ES read  ─▶ RunTrace[] by (corr,exec)   (unchanged)
                                 │ Prom read ─▶ PromCounterSnapshot          (+ keeper deltas, 2b)
                                 ▼
                          PassFailEngine.Analyze(...)
                                 │ ES value-chain + completeness   (unchanged, primary)
                                 │ MG-1/2/3 metric gate            (NEW, 2c)
                                 │ in-flight-vs-post-recovery class (NEW, 3b)
                                 ▼  AnalyzerReport  (verdict = ES ∧ metric-gate ∧ B-criterion)
```

Every changed unit has a single clear purpose and is independently testable: `PassFailEngine` stays a pure function over `(RunTrace[], PromCounterSnapshot, timestamps)` — no IO — so all new branches are provable with synthetic inputs in hermetic facts (the existing `PassFailEngineFacts` pattern).

## 6. Files affected (all `tests/` and `scripts/` — zero `src/`)

| File | Change | Increment |
|------|--------|-----------|
| `scripts/phase-67-harness.ps1` | fire-gate (done); drain-to-quiescence; export `RECOVERY_UTC` | 1a, 2a, 3a |
| `tests/.../Analysis/PassFailEngine.cs` | retire corroboration; MG-1/2/3; in-flight classification | 1b, 2c, 3b |
| `tests/.../Analysis/PromCounterSnapshot.cs` | drop dead fields; add keeper deltas + probe rate | 1b, 2b |
| `tests/.../Analysis/AnalyzerReport.cs` | report shape (tolerated-loss count, MG results) | 1b, 2c, 3b |
| `tests/.../AnalyzerE2ETests.cs` | keeper Prom reads; pass `RECOVERY_UTC`; quiescence-aware snapshot | 2b, 2a, 3a |
| `tests/.../*Facts*.cs` (new + existing) | drift-guard test; hermetic facts for MG-1/2/3 + classification | 1c, 2c, 3b |

## 7. Testing strategy

- **Hermetic (primary for the analyzer):** `PassFailEngine` is pure, so every new decision branch (MG-1 pass/fail, MG-2 expected/not, MG-3, in-flight vs post-recovery, `MaxInFlightLoss`) is covered by synthetic `RunTrace`/`PromCounterSnapshot`/timestamp facts. The drift-guard is a hermetic string-match test.
- **Live (integration):** a full `phase-68-sweep` re-run validates 7/7 still PASS with the metric gate binding, MG-1/MG-3 green, MG-2 expectations recorded.
- **Targeted B validation:** a redis-crash run timed to land in the in-flight window (the method from this cycle) must produce a *tolerated* (not FAIL) classification with a reported in-flight-loss count of ≥1 — and a synthetic post-recovery incomplete must FAIL.

## 8. Constraints

- **`src/` untouched** — stated and enforced (the drift-guard reads `src/` constants but modifies nothing).
- Verification fully automated (no "human verification" gates).
- `PassFailEngine` remains pure / IO-free.
- ES value-chain remains the **primary** correctness arbiter; the metric gate is additive and must never *weaken* an ES FAIL.

## 9. Open items (resolved during implementation, not now)

- **Per-scenario keeper expectation (MG-2):** empirically determined by the first reporting-only sweep — which of TEST-02..07 actually drive `keeper_messages_*`.
- **`MaxInFlightLoss` bound:** calibrated from observed in-flight concurrency (≈ executions live per fire × fault dwell coverage); start at a conservative small N, tighten with data.

---

## Appendix A — Evidence gathered this cycle

- **Drained happy-path conservation (exact):** `orchestrator_messages_consumed = 896 == processor_messages_sent = 896`; `orchestrator_messages_sent = 1663` (≈ 21/run, the two-consumer hop), `processor_messages_consumed = 939`. ~80 runs; all four counters reconcile to the same run count when divided by their true per-run constant.
- **Fresh full sweep (post harness-fix): 7/7 PASS** — TEST-01 20/20, 02 18/18, 03 17/17, 04 19/19, 05 18/18, 06 17/17, 07 13/13; all zero-missing, zero-duplicate.
- **Miscalibration proof (TEST-05 solo):** PASS 15/15, but `Reconciliation: Unreconciled` because `PromImpliedRuns = round(268/9) = 30` vs `StartedRuns = 15`; windowed (non-drained) `orch_consumed 146` vs `proc_sent 124` (conservation only holds drained).
- **TEST-05 flakiness characterized:** 5 clean runs (2 manual + sweep + solo + …) vs 1 prior FAIL; the loss is a sub-second in-flight window against a 30 s cron — rare, genuine, timing-bound, not attribution (recovered runs show clean single `(corr,exec)` with no re-mint split).
- **Baseline `(corr,exec)` structure:** per fire = 1 trunk (`Step_A`, no `executionId`, excluded) + ~2 full executions, each traversing the whole DAG with `Step_G` logged ×2 (fan-in) under one `executionId`.
