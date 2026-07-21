# Phase 84 Plan 03 — SUMMARY (phase verdict)

**Status:** ✅ COMPLETE — the SOLE phase gate is GREEN.
**Completed:** 2026-07-21

## Verdict: 7/7 PASS (the byte[] widening changed nothing observable)

The 7-scenario fault-recovery sweep reproduced its all-PASS baseline against the `byte[]`-widened framework, deployed live on the Docker-Desktop k8s cluster. Per locked D-05 / DATA-04, this all-PASS sweep IS the acceptance proof for the whole widening.

| scenarioId | verdict | zeroMissing | effectOnce | startedRuns | completeRuns | harnessExit | class |
|---|---|---|---|---|---|---|---|
| TEST-01 (no-fault baseline) | Pass | True | True | 18 | 18 | 0 | PASS |
| TEST-02 (processor crash) | Pass | True | True | 19 | 19 | 0 | PASS |
| TEST-03 (orchestrator crash) | Pass | True | True | 17 | 17 | 0 | PASS |
| TEST-04 (keeper crash) | Pass | True | True | 18 | 18 | 0 | PASS |
| TEST-05 (redis wipe) | Pass | True | True | 25 | 25 | 0 | PASS |
| TEST-06 (rabbitmq crash) | Pass | True | True | 16 | 16 | 0 | PASS |
| TEST-07 (redis+rabbitmq crash) | Pass | True | True | 27 | 27 | 0 | PASS |

`CAPSTONE: 7/7 PASS`. Roll-up artifact: `analyzer-reports/phase-81-summary.json`.

## Requirements satisfied

- **DATA-04** (sole gate): the existing 7-scenario sweep reproduced its all-PASS baseline (every `Missing==0` / `zeroMissing`, every `effectOnce`, `startedRuns==completeRuns`) — no new test suite gated the phase.
- **DATA-03 + DATA-01**: byte-for-byte equivalence proven live end-to-end — the sweep is byte-identical in shape to its prior baseline; `Processor.Sample` (UTF-8 JSON) stayed green; `startedRuns==completeRuns` for all 7.
- **DATA-05**: the recovery scenarios (TEST-02..07) exercised `byte[]` Data through `KeeperInject` over the RabbitMQ wire; all recovered with `zeroMissing==True` — byte[] Data survives the recovery backup losslessly. TEST-04 (keeper crash) and TEST-07 (redis+rabbitmq) are the strongest evidence.

## Execution notes / deviations

The plan's Wave-3 runbook referenced the compose-era `phase-68-sweep.ps1`; this is a k8s-only project, so the actual gate is `scripts/phase-81-sweep.ps1` (kubectl-driven). Corrected inline. Two infra issues surfaced and were fixed — **neither was a byte[] defect**:

1. **Stale-image trap** ([[k8s-local-image-stale-tag-rebuild]]): the pods ran 2-day-old (pre-byte[]) images. Rebuilt the 4 app images under a unique tag `:byte84` and deployed via a temporary kustomize `images:` override so containerd imported the fresh `byte[]` bits (a same-`:local` rebuild runs stale). Verified all 4 deployments landed on `:byte84`.
2. **Reseed-first trap** ([[rebuild-sourcehash-reseed-order]]): the `byte84` rebuild changed the processor SourceHash `94ca171c` → `f51a7cc7`; the stale DB processor row fooled the harness warm-DB guard into skipping bootstrap, so the new-hash processor never registered liveness → reset heal-wait failed 7/7 with `harnessExit 20` (INFRA_ABORT) while the roll-up read STALE `verdict:Pass` from the old baseline reports (the exact stale-report trap [[phase-68-sweep-stale-report-read]] — trusted the exit code, not the verdict). Fixed by emptying the `processors` table (FK-safe graph delete) so STEP A2 bootstrapped `f51a7cc7`; liveness then converged (2 per-instance keys) and all 7 scenarios ran clean.

Also: a transient port-forward flake aborted an early attempt at bring-up STEP 7 (health/ready) — resolved by clearing stale port-forwards. The `byte84` app deployed, became `1/1 Ready`, registered liveness, and passed all 7 — confirming the byte[] change did not break the processor/keeper/orchestrator runtime.

**Caveat:** the keeper `:byte84` image was built with the uncommitted FALSIFY-02 `ReinjectConsumer.cs` change baked in (env-gated `KEEPER_REINJECT_DELAY_MS`, default-off, never set by TEST-01..07 → inert for this sweep).

## Waves 01 + 02 recap

- 84-01: `DataResult.Data` string→byte[] outright + thin UTF-8 edge helpers (`NewResult(byte[])`, `DataAsString`); all production consumers + both concrete processors migrated; 0-warning Debug+Release.
- 84-02: hermetic test compile-breaks migrated (11 sites, minimal byte-wraps, no transparent-assertion churn) + A1 (STJ byte[] round-trip) & A2 (invalid-UTF8-under-schema → Failed) facts; hermetic 720/750 (0 new failures).
