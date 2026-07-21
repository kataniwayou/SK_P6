# Phase 81: k8s Fault-Recovery Sweep (TEST-01..07 via kubectl scale) — Specification

**Created:** 2026-07-18
**Ambiguity score:** 0.15 (gate: ≤ 0.20)
**Requirements:** 6 locked

## Goal

Re-run the 7-scenario fault-recovery capstone (TEST-01..TEST-07) on the local Kubernetes (Docker Desktop) target and reach **7/7 VERDICT_PASS**, using `kubectl scale --replicas=0` (then scale-back) as the whole-tier crash/recovery injection — proving the two-consumer recovery architecture recovers on k8s the same way it does on compose (already proven in v8.0.0), verified SOLELY from the reused Prometheus + Elasticsearch analyzer.

## Background

Phase 80 ported the 11-service compose stack to k8s manifests and proved the **happy path** (TEST-01) end-to-end. `scripts/phase-80-harness.ps1` hardcodes `$scenarioId = 'TEST-01'` (line 96) with no `-ScenarioId` parameter and no crash sequencer — STEP F.2 is the no-fault baseline only.

The compose capstone already exists: `scripts/phase-67-harness.ps1` carries the full scenario table and a crash sequencer (STEP F.3 `docker compose stop <svc>` → dwell → `docker compose start <svc>` → NDJSON per-replica health-wait) and takes `-ScenarioId`; `scripts/phase-68-sweep.ps1` drives it over `TEST-01..07` and rolls up the verdict. The analyzer (`~Analyze_Window_Yields_Pass`) + `PassFailEngine` metric gate read Prometheus (`localhost:9090`) + ES (`localhost:9200`) via the Phase-80 port-forwards.

The gap: **no k8s crash sequencer and no k8s sweep exist**. The crash injection is coupled to the docker-compose CLI (`docker compose stop/start`), which cannot address k8s workloads — this is the third instance of the RESEARCH Pitfall-1 CLI-coupling re-target (after the reset and STEP B1/D re-targets in Phase 80).

## Requirements

1. **Scenario-parameterized k8s harness**: The k8s harness runs any single scenario TEST-01..07 by id.
   - Current: `phase-80-harness.ps1` hardcodes `$scenarioId = 'TEST-01'`; no `-ScenarioId` param; no scenario table; no crash sequencer.
   - Target: the k8s harness accepts `-ScenarioId <TEST-01..07>` and carries the same scenario-descriptor shape as `phase-67-harness.ps1` (`targetContainers`, `faultType` ∈ {`none`,`stop-start`}, `injectAfterNFires`, `dwellSeconds`) for exactly the 7 capstone scenarios.
   - Acceptance: invoking with `-ScenarioId TEST-05` runs the redis crash; `-ScenarioId TEST-01` runs the no-fault baseline; an id outside TEST-01..07 exits non-zero as a config-usage error (distinct code).

2. **kubectl-scale crash injection (Pitfall-1 re-target, 3rd instance)**: Whole-tier crash via scale-to-0, restore via scale-back.
   - Current: the crash sequencer uses `docker compose stop <svc>` / `docker compose start <svc>` (phase-67-harness STEP F.3), which cannot see k8s pods.
   - Target: crash = `kubectl -n skp scale <kind>/<tier> --replicas=0`; restore = `kubectl -n skp scale <kind>/<tier> --replicas=<original>`. Kind resolved per tier: Deployments (`processor-sample`, `orchestrator`, `keeper`) and StatefulSets (`redis`, `rabbitmq`). No `docker`/`docker compose` CLI call appears anywhere in the k8s crash path.
   - Acceptance: grep of the k8s crash path shows `kubectl … scale … --replicas=0` and **zero** `docker compose stop`/`start`; a TEST-04 run scales `keeper` to 0 (both replicas gone) then back.

3. **Replica-count preservation on restore**: Each crashed tier is restored to its exact Phase-80 replica count.
   - Current: no k8s crash/restore exists.
   - Target: restore uses the fixed Phase-80 counts — orchestrator 1, keeper 2, processor-sample 2, redis 1, rabbitmq 1 — never a blanket `--replicas=1` for the ×2 tiers.
   - Acceptance: after a TEST-02 (processor-sample) or TEST-04 (keeper) run, `kubectl -n skp get deploy <tier>` shows the tier back at **2/2** Ready.

4. **Firm recovery-timing gate**: Wait for actual pod termination after scale-0 and all-replicas-Ready after restore before pinning `RECOVERY_UTC`.
   - Current: compose sequencer runs a docker health-wait; `kubectl scale` returns before pods terminate or become Ready.
   - Target: after scale-0, block on a bounded poll until 0 running pods of the tier; after scale-back, block until all replicas Ready (`kubectl rollout status` or an equivalent pod-ready poll); `RECOVERY_UTC` is pinned only after the readiness gate clears.
   - Acceptance: the harness contains bounded termination + readiness waits (verifiable in the crash-path code + log lines), and `RECOVERY_UTC` is assigned after the readiness gate, not before.

5. **k8s capstone sweep with verbatim-reused verdict**: A sweep drives TEST-01..07 on k8s and rolls up the verdict from the unchanged analyzer + metric gate.
   - Current: `phase-68-sweep.ps1` drives `phase-67-harness.ps1` (compose) over TEST-01..07; no k8s sweep exists.
   - Target: a k8s capstone sweep runs TEST-01..07 against the k8s harness (per-scenario child invocation + reset between scenarios via the Phase-80 reset) and emits per-scenario + roll-up verdicts, reusing the phase-68 analyzer (`~Analyze_Window_Yields_Pass`) + conservation/recovery `PassFailEngine` metric gate **byte-for-byte** — no analyzer/metric-gate logic change.
   - Acceptance: the k8s sweep executes all 7 scenarios and emits a roll-up; `git diff` shows the analyzer / `PassFailEngine` / metric-gate source is unchanged.

6. **7/7 PASS bar (with re-runnable INCONCLUSIVE)**: All seven scenarios reach VERDICT_PASS.
   - Current: only TEST-01 is proven on k8s (Phase 80).
   - Target: TEST-01..07 each produce VERDICT_PASS on the k8s target (recovery complete, conservation holds, metric gate green). An INCONCLUSIVE verdict (exit 2 — observability-degraded, e.g. cold-ES flake) is **not** a failure and may be re-run; a VERDICT_FAIL (exit 1 / Missing>0 / recoverable-but-lost binding) fails the phase.
   - Acceptance: the k8s sweep roll-up shows **7/7 VERDICT_PASS** (re-runs permitted to clear INCONCLUSIVE) with **zero** scenarios at VERDICT_FAIL.

## Boundaries

**In scope:**
- Generalize the k8s harness to accept `-ScenarioId` (TEST-01..07) + add the crash sequencer.
- Re-target the whole-tier crash injection from docker-compose CLI to `kubectl scale --replicas=0`/restore for all 5 crashable tiers.
- Firm terminate-after-scale-0 and Ready-after-restore waits around the crash/restore, gating `RECOVERY_UTC`.
- A k8s capstone sweep driving TEST-01..07, reusing the phase-68 analyzer + metric gate verbatim, emitting a 7/7 roll-up.
- The 7 recovery-capstone scenarios: no-fault baseline + whole-tier crash of processor-sample / orchestrator / keeper / redis / rabbitmq / redis+rabbitmq.

**Out of scope:**
- Seam-dependent negative controls TEST-08/09/10 (`stop-only`/`stop-on-inflight`/`stopstart-on-inflight`) and FALSIFY-01/02 — they require the test-only env seams (`PROCESSOR_STEP_DELAY_MS`, `K_EXECUTIONS`, `PROCESSOR_DEFEAT_READ`, `KEEPER_DEFEAT_REINJECT`, `KEEPER_REINJECT_DELAY_MS`, `EXECUTION_DATA_TTL` overrides) that the Phase-80 manifests deliberately omit (D-08). Adding seam injection to the k8s manifests/harness is a separate future phase.
- Any change to the analyzer / `PassFailEngine` / metric-gate logic — reused verbatim, not modified.
- Any app source or two-consumer recovery-architecture change — this phase is harness/tooling only.
- Partial / single-replica crash modes (e.g., killing one of two keeper replicas) — the capstone crashes whole tiers only.
- Multi-node cluster, registry-based image distribution, Helm/overlay packaging — inherited Phase-80 out-of-scope.

## Constraints

- Crash mechanism is `kubectl -n skp scale <kind>/<tier> --replicas=0` / restore for **all** tiers; scale target resolved by workload kind (`deployment/` for processor-sample/orchestrator/keeper; `statefulset/` for redis/rabbitmq).
- Crash fidelity is inherent to the Phase-80 manifest posture and is accepted: redis (no PVC, D-12) loses L2 on scale-0 — the genuine wipe boundary matching the compose redis-crash intent; rabbitmq (PVC, D-11) retains durable queues/mnesia across the crash — matching the compose rabbitmq-crash intent.
- Runs on the same single-node Docker Desktop k8s target as Phase 80, brought up by `phase-80-up.ps1` (8 loopback port-forwards); the analyzer/sweep read `localhost:9090` (Prometheus) + `localhost:9200` (ES).
- Between-scenario state reset uses the Phase-80 reset (`phase-80-reset.ps1`); the fresh-DB processor-row bootstrap is already handled by `phase-80-harness.ps1` STEP A2.
- INCONCLUSIVE (exit 2) is a re-runnable non-failure; only VERDICT_FAIL (exit 1 or a recoverable-but-lost binding) fails the phase.

## Acceptance Criteria

- [ ] The k8s harness accepts `-ScenarioId` and runs any of TEST-01..07; an out-of-range id exits non-zero (config-usage error).
- [ ] The k8s crash path uses `kubectl … scale … --replicas=0`/restore and contains **zero** `docker compose stop`/`start` calls.
- [ ] All 5 crashable tiers crash via scale-0; restore returns each to its Phase-80 replica count (orchestrator 1, keeper 2, processor-sample 2, redis 1, rabbitmq 1).
- [ ] The harness waits for actual pod termination after scale-0 **and** all-replicas-Ready after restore before pinning `RECOVERY_UTC`.
- [ ] A k8s capstone sweep runs TEST-01..07 and emits per-scenario + roll-up verdicts.
- [ ] The analyzer / `PassFailEngine` / metric-gate source is unchanged (reused verbatim — `git diff` clean for those files).
- [ ] The sweep roll-up reaches **7/7 VERDICT_PASS** (INCONCLUSIVE re-runs allowed; zero VERDICT_FAIL).
- [ ] TEST-08/09/10 and FALSIFY-01/02 are NOT run by this phase's sweep.

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                              |
|--------------------|-------|------|--------|----------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | 7/7 hard PASS bar, verified from reused analyzer    |
| Boundary Clarity   | 0.85  | 0.70 | ✓      | Scale-0 all tiers in; seam-negative-controls out    |
| Constraint Clarity | 0.82  | 0.65 | ✓      | Firm terminate/Ready wait; per-tier crash fidelity  |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | 8 pass/fail criteria                                |
| **Ambiguity**      | 0.15  | ≤0.20| ✓      |                                                     |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective                     | Question summary                                  | Decision locked                                                              |
|-------|---------------------------------|---------------------------------------------------|------------------------------------------------------------------------------|
| 1     | Researcher/Boundary/Constraint  | Pass bar for the k8s sweep?                       | 7/7 hard VERDICT_PASS (TEST-01 re-run inside the sweep)                       |
| 1     | Failure Analyst                 | Does INCONCLUSIVE count as failure?               | No — exit-2 INCONCLUSIVE is re-runnable; only VERDICT_FAIL fails the phase    |
| 1     | Boundary Keeper                 | Crash mechanism / fidelity per tier?              | `kubectl scale --replicas=0` for ALL tiers incl. StatefulSets; PVC-driven fidelity accepted (redis wipes L2, rabbitmq durable) |
| 1     | Failure Analyst                 | Recovery-timing rigor on k8s?                     | Firm wait: terminate-after-scale-0 + Ready-after-restore before pinning RECOVERY_UTC |

---

*Phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o*
*Spec created: 2026-07-18*
*Next step: /gsd-discuss-phase 81 — implementation decisions (script naming/shape: extend phase-80-harness vs new phase-81-harness/phase-81-sweep; termination/readiness poll mechanics; per-scenario reset wiring; dwell/injectAfterNFires carry-over)*
