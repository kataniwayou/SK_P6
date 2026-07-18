# Phase 81: k8s Fault-Recovery Sweep — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-18
**Phase:** 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o
**Areas discussed:** Script topology, Sweep bring-up & state isolation, Crash injection mechanics
**Note:** SPEC.md locked the requirements (7/7 bar, scale-0 crash, verbatim analyzer). Discussion was HOW-only.

---

## Script topology

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Generalize phase-80-harness.ps1 in place + new phase-81-sweep.ps1 | One scenario-capable harness (k8s phase-67 equivalent), TEST-01 stays default; DRY, mirrors compose | ✓ |
| (b) New phase-81-harness.ps1 + phase-81-sweep.ps1 siblings | Keeps phase-80-harness frozen; ~300 duplicated lines, two harnesses | |

**User's choice:** (a)
**Notes:** Mirrors the compose one-harness model; Phase-80 proof stays reproducible because TEST-01 remains the default and re-runs inside the sweep.

---

## Sweep bring-up & cross-scenario state isolation

| Option | Description | Selected |
|--------|-------------|----------|
| (a) Per-scenario bring-up | Re-apply + rollout the whole stack each scenario (mirrors compose force-recreate); fully isolated but ~60 min | |
| (b) One bring-up + reset between scenarios | Bring up once, loop 7 scenarios sharing the stack, reset+rollout-restart between; ~half the wall-clock | ✓ |

**User's choice:** (b)
**Follow-up (rabbitmq drain):** Because rabbitmq has a PVC (Phase-80 D-11) its durable queues survive both a scale-0 crash and a re-apply, and the current reset does not purge it → stale messages could bleed across scenarios. **User chose: yes, extend phase-80-reset.ps1 to drain rabbitmq.**
**Notes:** Divergence from the compose per-scenario force-recreate model is deliberate, justified by k8s bring-up cost; images don't change mid-sweep. The rabbitmq drain makes cross-scenario isolation explicit.

---

## Crash injection mechanics

| Option | Description | Selected |
|--------|-------------|----------|
| Planner discretion | Static tier→kind map, scale-0 → terminate-poll → dwell → scale-restore → rollout/ready → pin RECOVERY_UTC; exact poll mechanics left to the planner | ✓ |

**User's choice:** Planner discretion (with the recommended approach recorded in CONTEXT.md D-05).
**Notes:** SPEC reqs 2/3/4 already bound the behavior (kubectl scale, replica-count restore, firm terminate/Ready wait); only the implementation detail is open.

## Claude's / Planner's Discretion
- Scale-target resolution + termination/readiness poll mechanics.
- The "bring up once, run scenarios N times" seam (sweep-owned vs harness self-guard).
- rabbitmq drain mechanism + target queues.

## Deferred Ideas
- Seam-dependent negative controls (TEST-08/09/10, FALSIFY-01/02) on k8s — need the omitted env seams; separate future phase.
- Per-scenario full bring-up isolation — rejected for wall-clock; revisit only if state bleed proves un-resettable.
