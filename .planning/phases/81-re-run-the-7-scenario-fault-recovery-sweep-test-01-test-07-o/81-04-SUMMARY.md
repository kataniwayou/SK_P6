---
phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o
plan: 04
status: complete
requirements: [SPEC-5, SPEC-6]
self_check: PASSED
---

# Plan 81-04 SUMMARY — k8s capstone sweep → 7/7 VERDICT_PASS

## Outcome

**The k8s fault-recovery capstone reaches 7/7 VERDICT_PASS.** `analyzer-reports/phase-81-summary.json`
shows all seven capstone scenarios (TEST-01..07) at `harnessExit=0 / verdict=Pass`, zero `harnessExit=1`,
zero out-of-scope rows (no TEST-08/09/10/FALSIFY). Sweep console: `CAPSTONE: 7/7 PASS`. All per-scenario
reports were freshly written this run (mtimes 16:35–17:38, 2026-07-18).

| Scenario | Fault | Runs | Missing | Dup | Verdict |
|---|---|---|---|---|---|
| TEST-01 | baseline | 19/19 | 0 | 0 | Pass |
| TEST-02 | processor crash | 16/16 | 0 | 0 | Pass |
| TEST-03 | orchestrator crash | 17/17 | 0 | 0 | Pass |
| TEST-04 | keeper crash | 19/19 | 0 | 0 | Pass |
| TEST-05 | redis wipe | 24/24 | 0 | 0 | Pass |
| TEST-06 | rabbitmq crash | 16/16 | 0 | 0 | Pass |
| TEST-07 | redis+rabbitmq | 21/21 | 0 | 0 | Pass |

All crashes injected on k8s via `kubectl -n skp scale` (e.g. TEST-04 `scale deployment/keeper --replicas=0 → 2`,
then Ready-gated rollout). The two-consumer recovery architecture recovers on k8s exactly as it did on
compose in v8.0.0: structural zero-loss + effect-once under every fault.

## Task-by-task

- **Task 1 (reused-verbatim guard):** `PassFailEngine.cs` and the `Analysis/` metric-gate engine are
  byte-unchanged in the phase-81 range (`0dbb8f7..HEAD`). The ONLY analyzer-side delta is a single boolean
  in `AnalyzerE2ETests.cs` (the per-scenario `Mg1Binding` config table) — the user-authorized SPEC-5
  exception below. The metric-gate ENGINE itself was never touched.
- **Task 2 (run → 7/7):** achieved after two gap-closure fixes (below). Ran as a detached OS process to
  survive the tool-runner reaping long tracked background tasks.
- **Task 3 (operator confirmation):** the user explicitly WAIVED human verification ("no human
  verification — do yourself"). Self-verified the full Task-3 checklist against the artifacts: 7 rows all
  `harnessExit=0`/Pass; `CAPSTONE: 7/7 PASS`; TEST-04 keeper crashed via `kubectl scale` on k8s; no
  out-of-scope scenarios; analyzer metric-gate engine unchanged. All pass.

## Two gap-closure fixes (both required to reach 7/7)

The first sweep returned 5 PASS / 2 FAIL (TEST-04 keeper, TEST-06 rabbitmq), both failing SOLELY on the
MG-1 conservation gate with perfect structural recovery (Missing=0, zero-dup). Investigation split them
into two distinct, fully-diagnosed causes:

1. **Cumulative counter drift (fixed in the harness — `425d30c`).** The D-03 shared-stack sweep restarts
   only the orchestrator per scenario (STEP B1, Quartz clean-window), so `orchestrator_consumed` reset each
   scenario while `processor_sent` accumulated across the whole sweep (210→322→542→1094). MG-1 compares
   absolute `@end` counters, so the drift failed the binding scenarios. **Fix:** STEP B0 rollout-restarts
   the processor-sample tier per scenario, aligning both counter-owning tiers to a per-scenario baseline
   (matching compose's whole-stack recreate). Verified: `proc_sent` dropped from 1094 to per-scenario ~220,
   and TEST-01/03/04 conservation now settles to gap=0.

2. **rabbitmq-PVC redelivery on TEST-06 (SPEC-5 exception — `a57f80b`).** Even baseline-aligned, TEST-06
   retained a residual per-scenario gap (`proc_sent=220 > orch_consumed=176`, drain plateaued at 44). Per
   the user's "confirm redelivery first" instruction, verified from ES that this is NOT lost work: the
   analyzer's ES-primary structural check (the binding arbiter) reports `Missing=0 / MissingDetail=[] /
   InFlightLoss=0`, zero duplicates, all 16 runs complete; recovery used NO keeper reinject (keeper 8 logs,
   0 REINJECT) — it was pure broker-level redelivery after rabbitmq (PVC-durable, Phase-80 D-11) recovered.
   `TypedResultConsumer.cs:55` counts every receipt before dedup, so `orch_consumed < proc_sent` means the
   44 excess sends were redundant re-sends the orchestrator never needed. The gap appears ONLY on
   rabbitmq-crash scenarios (TEST-06=44, TEST-07=11), zero on keeper/baseline — the fingerprint of
   at-least-once broker redelivery. **Fix:** flip `Mg1Binding["TEST-06"] = false`, matching the already
   non-binding TEST-05/07/FALSIFY-02 (same tolerated-inflation class) and the engine's own stated design
   (ES-primary binding; Prom conservation corroborating-only).

TEST-04 (keeper) stayed binding=true — its conservation genuinely settles to gap=0; its first-sweep FAIL
was a stale-report read (T-81-11), cleared by this run's fresh report.

## Deviations

- **SPEC req 5 amended (user-authorized).** SPEC req 5 locked the analyzer/binding source as
  byte-unchanged. The user explicitly authorized the exception after the redelivery confirmation. Scope is
  minimal: one boolean in the `Mg1Binding` config table; the `PassFailEngine` metric-gate ENGINE remains
  byte-identical. Rationale + evidence recorded in the analyzer comment and commit `a57f80b`.
- **Harness STEP B0 added** (`425d30c`) — a Phase-81 harness change (in-scope; the 3 deliverable scripts).
- **Operational:** the sweep was run as a detached OS process because the tool-runner reaps long tracked
  background tasks mid-run (two prior sweeps killed; their child processes survived). The detached run
  completed all 7 scenarios + roll-up + teardown cleanly.
- **Infra (during first sweep):** stale pre-IN-03 workload controllers with `commonLabels`-injected
  immutable selectors blocked `kubectl apply` (exit 10); deleted the stale controllers (PVCs retained),
  re-applied clean. Manifests were already correct; only the live objects were stale.

## Verification

- `node` roll-up gate: **7/7 PASS** (7 rows `harnessExit=0`, 0 rows `harnessExit=1`).
- Only TEST-01..07 in the roll-up; zero TEST-08/09/10/FALSIFY (SPEC req 8).
- `git diff --stat 0dbb8f7..HEAD -- .../Analysis/PassFailEngine.cs` empty (metric-gate engine unchanged).
- Test project builds Release 0-warning / 0-error.
- Stack left up + inspectable (PVCs retained); port-forwards torn down by STEP Z.

## Self-Check: PASSED
