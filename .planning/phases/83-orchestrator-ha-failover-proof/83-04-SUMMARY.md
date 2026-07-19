# 83-04 — Live HA-07 Failover Proof — SUMMARY

**Plan:** 83-04 (checkpoint: human-verify — driven + verified by operator authorization "no human verification required — do it yourself")
**Requirement:** HA-07
**Outcome:** ✅ PASS (harness exit 0)

## Result

`scripts/phase-83-ha-failover.ps1 -SkipBringUp` exited **0 (PASS)**. The captured verdict artifact `analyzer-reports/phase-83-ha.json` shows `Verdict: Pass` with all five HA-07 claim flags true:

| Claim | Flag | Value |
|-------|------|-------|
| Zero duplicate triggers (per-tick uniqueness) | ZeroDuplicate | true |
| Election-gap tick skipped | GapObserved | true (**gapTicks = 1**, one empty non-backfilled gap bucket) |
| Not backfilled (skip-on-gap held) | NotBackfilled | true |
| Role transition visible in ES | RoleFlipVisible | true |
| Bounded single-leader recovery | BoundedRecovery | true (**RecoverySeconds = 15.44**, ≤ 2×LeaseDuration = 30s) |

- SendCorrIdCount = 4, BucketCount = 4 (one distinct correlationId per 30s cron tick — no split-brain).
- KILL_UTC = 2026-07-19T05:25:27Z (leader `orchestrator-7655485b7f-snb7v` force-deleted `--grace-period=0 --force`, phase-aligned ~3s before a :00/:30 boundary).
- Observation window 172s (pre-kill / election-gap / post-recovery). Orchestrator recovered to 3/3 Ready under a new leader (`orchestrator-7655485b7f-pl57s`).

## Path to PASS (two blockers found + fixed during the live proof)

1. **Phase-82 exclusive-fan-out-queue defect (blocked bring-up).** The orchestrator could not reach replicas:3 — its Start/Stop, Pause/Resume, and PauseAll/ResumeAll consumer definitions pinned a **fixed `EndpointName`**, which in MassTransit 8.5.5 bypasses the per-pod `InstanceId` formatter → fixed-name `Temporary` (exclusive) queues → replicas 2/3 hit `RESOURCE_LOCKED`, bus faults, `/health/ready` 503. **Fixed in plan 83-05** (per-instance `e.Name` via `OrchestratorFanoutEndpoints`, co-located per pair, literal `EndpointName` removed; hermetic guard added). Orchestrator now 3/3 Ready with per-instance queues.
2. **Scorer same-bucket gap-detection bug (false INCONCLUSIVE).** The first end-to-end run scored INCONCLUSIVE (GapObserved=false) despite the other four claims green + a clean 17.26s recovery. Root cause: the phase-aligned kill floors to the **same** 30s bucket as the last pre-kill fire, but the gap walk used `lastPreKill = max(bucket < killBucket)` → excluded that fire → walk skipped → genuine gap tick undetected. **Fixed** (`< killBucket` → `<= killBucket`, commit 775eadc) + a regression fact reproducing the live same-bucket timing (7/7 hermetic facts green).

## Deploy note (recorded to memory)

Docker-Desktop k8s runs STALE code after a rebuild when the deploy keeps the same `:local` tag + `imagePullPolicy: IfNotPresent` (containerd imports a docker-built image into the k8s.io namespace only on FIRST deploy). Worked around by deploying under a unique tag (`orchestrator:fix-df21f41`) + `kubectl set image`, then running the harness `-SkipBringUp` with manual port-forwards. See memory `k8s-local-image-stale-tag-rebuild`.

## Verification
- Harness exit 0; `analyzer-reports/phase-83-ha.json` Verdict=Pass, all five flags true.
- Recovery 15.44s ≤ 30s; exactly one empty gap bucket overlapping [KILL_UTC, roleFlipUtc].
- INCONCLUSIVE (first run) was re-run to a clean PASS, never accepted as the proof.

## Self-Check: PASSED
