---
phase: 85-processor-framework-boundary-hardening
plan: 04
subsystem: infra
tags: [verification, sc-4-gate, fault-recovery-sweep, k8s, no-regression]

# Dependency graph
requires:
  - phase: 85-processor-framework-boundary-hardening
    plan: 01
    provides: PB-01 fail-loud SpawnToPost (SpawnSendExhaustedException on transient send-exhaust)
  - phase: 85-processor-framework-boundary-hardening
    plan: 02
    provides: PB-02 narrow nack-escape in ProcessorPipeline (defeated spawn nack-requeues, acks only when all spawns succeed)
  - phase: 85-processor-framework-boundary-hardening
    plan: 03
    provides: PB-03 framework-owned entry deletion on both completion paths; concrete DeleteEntry surface removed
provides:
  - "SC-4 terminal proof: the 7-scenario fault-recovery sweep (TEST-01..07) reproduced its all-PASS baseline live on k8s — every Missing==0, every Duplicates==0, harness exit 0 — driving Processor.Sample"
  - "Live confirmation that PB-01/02/03 introduced NO happy-path regression (the Guid.Empty source net-effect held under all 7 fault modes, including the rabbitmq-crash nack-requeue scenario)"
affects: [KafkaImporter milestone (the hardened boundary is its prerequisite)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Run-only terminal gate: the SC-4 sweep is the SOLE integration proof; trust the harness exit code, not a report read"
    - "k8s fresh-code deploy under a unique fix-<sha> tag + kubectl set image (defeats the Docker-Desktop same-:local-tag stale-image trap), then the harness -SkipBringUp loop with manual port-forwards"

key-files:
  created: []
  modified: []

key-decisions:
  - "Substituted the k8s sweep (phase-81-sweep loop over phase-80-harness -SkipBringUp) for the plan's phase-68-sweep reference — the milestone runs on k8s, not docker-compose; phase-68-sweep is the retired compose harness"
  - "Full hermetic suite (`--filter-not-trait Category=RealStack`) can't complete on this host — it includes broker-dependent, non-RealStack-tagged integration tests that hang retrying the in-cluster `rabbitmq` DNS name; the plan's real must_have (PB facts + new facts + standing negative controls green) was confirmed via the targeted fact classes instead. This is a pre-existing environmental trait unrelated to PB-01/02/03; the SC-4 sweep is the actual integration proof"
---

# 85-04 SUMMARY — SC-4 Terminal Gate

## What was done
Ran the phase's final verification gate: proved the processor↔framework boundary hardening (PB-01/02/03) introduced no happy-path regression by reproducing the existing 7-scenario fault-recovery sweep's all-PASS baseline live on the k8s cluster, driving `Processor.Sample`. This plan edits no source — it is a run-only verification gate.

## Verification results

### Hermetic suite (PB-relevant facts, broker-free)
Built `SK_P.sln` Release 0-warning and the test project Debug. Ran the PB-relevant fact classes against the fresh build:
- `BaseProcessorSeamFacts` — 4/4 (PB-01 fail-loud SpawnToPost)
- `PrePipelineFacts` — 36/36 (PB-02 nack-escape + D-03 negative controls)
- `SampleProcessorFacts` — 4/4 (PB-03 source-skip net-effect)

The full `--filter-not-trait Category=RealStack` filter does not complete on this host: it includes broker-dependent, non-RealStack-tagged integration tests that hang retrying the in-cluster `rabbitmq` DNS name (`rabbitmq://rabbitmq/`) — a pre-existing environmental trait (the wave 1-3 executors flagged the same broker failures), unrelated to PB-01/02/03. The plan's real must_have (PB facts + the two new facts + the standing MalformedPayload / SeamThrows_Unexpected negative controls green) is satisfied by the targeted classes above.

### SC-4 seven-scenario fault-recovery sweep (k8s) — 7/7 PASS
Deployed fresh `fix-f5f6aec` code (SourceHash `dbcf2f66…`, verified equal on host build and running container), started 8 loopback port-forwards, and ran the sweep detached (`phase-80-harness.ps1 -SkipBringUp` looped over TEST-01..07, roll-up in `analyzer-reports/phase-85-summary.json`).

| Scenario | Fault | Verdict | Missing | Duplicates | Started/Complete | Exit |
|----------|-------|---------|---------|------------|------------------|------|
| TEST-01 | no-fault baseline | Pass | 0 | 0 | 19/19 | 0 |
| TEST-02 | processor whole-tier crash | Pass | 0 | 0 | 18/18 | 0 |
| TEST-03 | orchestrator crash (RAMJobStore rehydrate) | Pass | 0 | 0 | 14/14 | 0 |
| TEST-04 | keeper crash (both replicas) | Pass | 0 | 0 | 19/19 | 0 |
| TEST-05 | redis crash (L2 wipe) | Pass | 0 | 0 | 28/28 | 0 |
| TEST-06 | rabbitmq crash (nack-requeue redelivery) | Pass | 0 | 0 | 16/16 | 0 |
| TEST-07 | redis + rabbitmq combined crash | Pass | 0 | 0 | 22/22 | 0 |

**SC-4 CAPSTONE: 7/7 PASS** — every `Missing==0`, every `Duplicates==0`, every harness exit 0. Operator-confirmed (blocking human-verify). The `Guid.Empty` source net-effect held live under all 7 fault modes, including the rabbitmq-crash scenario that directly exercises PB-02's nack-requeue path.

## Issues encountered
- **Wrong-hash processor row → first sweep INFRA_ABORT (exit 20), resolved.** The initial launch aborted at the first scenario's STEP B reset heal-wait: the `fix-f5f6aec` build's new SourceHash `dbcf2f66` had no processor row, and the harness STEP A2 bootstrap guard checks the `processors` row *count* (not hash-match), so it skipped seeding because the stale `byte84` row (`f51a7cc7`) survived the reset (D-06 preserves `processors`). Fix: FK-safe graph-DELETE the 6 workflow tables → `DELETE FROM processors` → re-seed (FanOutSeeder confirmed host `Processor.Sample` hash == container `dbcf2f66`) → both replicas' `skp:proc` liveness keys converged in ~5s → relaunched. TEST-01 then cleared the heal-wait and all 7 passed. This is an operator-re-runnable INFRA_ABORT, NOT a code verdict. Recorded the A2-guard nuance in memory [[rebuild-sourcehash-reseed-order]].
- **Docker-Desktop stale-image trap avoided.** Deployed under a unique `fix-f5f6aec` tag + `kubectl set image` rather than same-`:local` rollout-restart, so the sweep tested current code (memory [[k8s-local-image-stale-tag-rebuild]], [[stack-bringup-k8s-only]]).

## Deviations
- Used the k8s sweep (`phase-81-sweep` loop / `phase-80-harness -SkipBringUp`) instead of the plan's `phase-68-sweep.ps1` (the retired docker-compose harness) — the milestone runs on k8s. Same TEST-01..07 contract, same all-PASS bar.
- Confirmed the hermetic must_have via targeted PB fact classes rather than the full `--filter-not-trait Category=RealStack` filter (see key-decisions / Verification).

## Self-Check: PASSED
- SC-4: 7-scenario sweep reproduced its all-PASS baseline (harness exit 0, every Missing==0 / Duplicates==0) — no happy-path regression from PB-01/02/03.
- Hermetic PB facts + standing negative controls green.
- Operator-confirmed verdict (blocking checkpoint).
