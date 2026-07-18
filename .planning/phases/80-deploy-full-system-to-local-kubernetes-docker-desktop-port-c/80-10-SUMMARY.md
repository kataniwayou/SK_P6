---
plan: 80-10
phase: 80
title: k8s happy-path proof runner (phase-80-harness.ps1) + live end-to-end proof
status: complete
completed: 2026-07-18
tasks_total: 2
tasks_complete: 2
self_check: PASSED
---

# Plan 80-10 Summary — k8s harness + live end-to-end proof

## Objective
Author `scripts/phase-80-harness.ps1` (the k8s TEST-01 happy-path proof runner, D-16) and run the full end-to-end proof on Docker Desktop Kubernetes — completing the second half of the RESEARCH Pitfall-1 re-target (harness STEP B1 + STEP D → kubectl) and proving the D-16 success bar.

## Outcome: PASSED
The live proof (`pwsh -File scripts/phase-80-harness.ps1`) reached **analyzer verdict PASS (exit 0)** against a fresh Docker Desktop k8s cluster:
- All **10 tiers Ready** (orchestrator ×1, keeper ×2, processor-sample ×2, baseapi ×1, otel-collector ×1, prometheus ×1, postgres/redis/rabbitmq/elasticsearch).
- Activation gate: `POST /orchestration/start` returned **204** (SourceHash currency proven — no 422).
- Analyzer report `TEST-01.json`: `Verdict: Pass`, `Missing: 0`, `StartedRuns: 18 / CompleteRuns: 18`, `Reconciliation: Reconciled`, `MetricGate { ConservationOk: true, ProbeLiveOk: true, Mg1Binding: true }`.
- Round-trip conservation on drain: `orch_consumed=200, proc_sent=200, gap=0`.
- Signals read from the SAME surfaces as the compose sweep: Prometheus (`localhost:9090`) + Elasticsearch (`localhost:9200`) via the plan-80-09 port-forwards.

## Key files
- **Created:** `scripts/phase-80-harness.ps1` (354 lines) — STEP A0 build → A bring-up → **A2 first-run bootstrap** → B reset → B1 orchestrator clean-restart → C seed → D wf-id (kubectl exec psql) → E 204 → F 300s window → H analyzer → Z teardown (keep stack+PVCs). STEP B1 (`kubectl rollout restart deployment/orchestrator`) and STEP D (`kubectl exec … psql`) complete the Pitfall-1 re-target; seeder (C), POST (E), analyzer (H) reused byte-for-byte.

## Tasks
| Task | Description | Commit |
|------|-------------|--------|
| 1 | Author `phase-80-harness.ps1` (Pitfall-1 STEP B1/D re-target; reuse C/E/H) | `3c71f4e` |
| 2 | Run the live k8s end-to-end proof (TEST-01, D-16) → PASS | (proof run; verdict exit 0) |

## Deviations & fixes (two real defects surfaced by the live proof)
The live proof exposed two genuine defects that the manifest/harness authoring could not catch without a real cluster. Both were root-caused (systematic debugging), fixed, committed, and re-proven:

1. **Orchestrator rolling-update deadlock → `strategy: Recreate`** (`k8s/31-orchestrator.yaml`, commit `a12a511`).
   The orchestrator declares an **exclusive** RabbitMQ queue (`orchestrator`). The default RollingUpdate briefly runs old+new pods together, so the new pod hit `RESOURCE_LOCKED` (405) declaring the exclusive queue → readiness 503 → rollout deadlocked past 120s → bring-up exit 10. Fixed by setting `strategy.type: Recreate` on the orchestrator Deployment (terminate old → release exclusive queue → start new) — the correct k8s expression of the documented single-owner ×1 SPOF invariant. Compose never hit this because `restart`/`--force-recreate` stop the old container first.

2. **Fresh-DB processor-row bootstrap → guarded STEP A2** (`scripts/phase-80-harness.ps1`, commit `3aa734c`).
   On a fresh k8s PVC the `processors` table is empty, so the processor's boot-before-register loop keeps its liveness watchdog "not started" until the row is registered — so the STEP B reset heal-wait could never converge on a first deploy (`Liveness did not reconverge in 60s` → reset exit 2). The compose harness never hit this because its persistent DB always carried a prior processor row (reset preserves the `processors` table). Fixed with a guarded first-run bootstrap: when the `processors` table is empty, seed once (`SeedProcessorAsync` GET-or-creates the row; the seeder self-verify is a DB-graph count, not a liveness check, so it succeeds with no live processor) and wait for per-replica liveness to converge, then proceed. A warm re-run skips the bootstrap and runs the proven reset→seed→start path unchanged.

Both fixes make the deploy + one-command proof self-contained and reproducible against a fresh Docker Desktop cluster.

## Self-Check: PASSED
- `phase-80-harness.ps1` parses clean (pwsh AST); STEP B1 = `kubectl rollout restart deployment/orchestrator`; STEP D wf-id = `kubectl exec … psql`; C/E/H reused verbatim.
- Live proof re-run from a clean namespace reached analyzer PASS with both fixes in place.
- Stack + PVCs kept up post-proof (teardown stopped only the 8 port-forwards).
