---
phase: 82-orchestrator-ha-leader-election
plan: 03
subsystem: infra
tags: [kubernetes, rbac, leader-election, coordination-lease, kustomize, orchestrator]

# Dependency graph
requires:
  - phase: 80-deploy-full-system-to-local-kubernetes
    provides: k8s manifests (31-orchestrator.yaml, kustomization.yaml) with skp namespace + label conventions
provides:
  - Least-privilege orchestrator RBAC (ServiceAccount + Role + RoleBinding) for coordination Lease access in skp
  - Orchestrator Deployment scaled to 3 HA replicas with RollingUpdate
  - Per-pod identity via POD_NAME downward-API env; hardcoded InstanceId removed
  - RBAC registered in the kustomize aggregate; whole stack validates client-side
affects: [phase-83-failover-test, orchestrator-leader-election]

# Tech tracking
tech-stack:
  added: [coordination.k8s.io/leases RBAC, kubernetes downward-API POD_NAME env]
  patterns: [namespaced least-privilege Role scoped to a single resource+verb set, dedicated per-workload ServiceAccount]

key-files:
  created: [k8s/34-orchestrator-rbac.yaml]
  modified: [k8s/31-orchestrator.yaml, k8s/kustomization.yaml]

key-decisions:
  - "Role grants only get/create/update on leases in coordination.k8s.io within skp — no wildcards, cluster scope, or extra verbs (T-82-09)"
  - "Manifest acceptance is client-side (kubectl --dry-run=client / kubectl kustomize) since no live cluster is assumed in the sandbox"
  - "RBAC listed after 31-orchestrator.yaml in kustomization for human diff-ability; kustomize kind-sorts RBAC before the Deployment at apply time regardless"

patterns-established:
  - "Per-workload dedicated ServiceAccount + namespaced least-privilege Role for k8s API access"
  - "POD_NAME downward-API env supplies distinct per-replica identity for HA fanout + lease holder"

requirements-completed: [HA-06]

# Metrics
duration: 3min
completed: 2026-07-18
---

# Phase 82 Plan 03: Orchestrator HA Deploy Surface Summary

**Least-privilege leases-only RBAC (SA + Role + RoleBinding) plus a 3-replica RollingUpdate orchestrator Deployment with POD_NAME per-pod identity, all validated client-side.**

## Performance

- **Duration:** 3 min
- **Started:** 2026-07-18T19:06:18Z
- **Completed:** 2026-07-18T19:08:34Z
- **Tasks:** 3
- **Files modified:** 3 (1 created, 2 modified)

## Accomplishments
- Authored `k8s/34-orchestrator-rbac.yaml`: a three-document manifest (ServiceAccount + Role + RoleBinding) granting the orchestrator SA exactly `get/create/update` on `leases` in `coordination.k8s.io`, namespaced to `skp`.
- Scaled `k8s/31-orchestrator.yaml` to `replicas: 3` with `strategy: RollingUpdate`, added a `POD_NAME` downward-API env, referenced the new `orchestrator` ServiceAccount, and dropped the hardcoded `Orchestrator__InstanceId`.
- Rewrote the stale SPOF / exclusive-queue / "LOCKED at 1" / Recreate header prose so the manifest no longer contradicts the HA model.
- Registered the RBAC manifest in `k8s/kustomization.yaml`; the whole stack passes `kubectl apply --dry-run=client -k k8s/`.

## Task Commits

Each task was committed atomically:

1. **Task 1: Author the least-privilege RBAC manifest** - `8941b73` (feat)
2. **Task 2: Scale + reconfigure the orchestrator Deployment** - `3ac285b` (feat)
3. **Task 3: Register the RBAC manifest in kustomization** - `02bc345` (feat)

**Plan metadata:** (docs commit — this SUMMARY + STATE/ROADMAP)

## Files Created/Modified
- `k8s/34-orchestrator-rbac.yaml` (created) - ServiceAccount `orchestrator`, Role `orchestrator-leader-election` (leases get/create/update), and RoleBinding, all in `skp`.
- `k8s/31-orchestrator.yaml` (modified) - replicas 3, RollingUpdate, POD_NAME env, serviceAccountName, dropped InstanceId, rewritten HA header prose.
- `k8s/kustomization.yaml` (modified) - added `34-orchestrator-rbac.yaml` to `resources` in tier order.

## Decisions Made
- Least-privilege enforced by grep: no `"*"`, no ClusterRole/ClusterRoleBinding, no list/watch/delete/patch verbs.
- Validation is client-side only (SPEC constraint — no live cluster assumed); kubectl v1.34.1 / kustomize v5.7.1 present in the sandbox so all three verification gates ran and passed.

## Deviations from Plan

None - plan executed exactly as written.

During Task 2 two acceptance greps briefly failed because my own rewritten comment prose contained the literal tokens `replicas: 3` and `Orchestrator__InstanceId`; I reworded the comments ("three replicas" / "hardcoded per-instance identity env") so the grep counts matched the intended 1 and 0. This was self-correction of the comment wording within the planned task, not an unplanned change to behavior — no deviation rule invoked.

## Issues Encountered
- Acceptance grep counts were sensitive to comment wording (see above); resolved by rewording. No functional impact.

## User Setup Required

None - no external service configuration required. (Live cluster apply and failover behavior are Phase-83 scope.)

## Next Phase Readiness
- Deploy surface is ready for Phase-83 failover testing: 3 replicas, lease RBAC, and per-pod identity are in place.
- The C# leader-election wiring (Program.cs reading POD_NAME, LeaderElector) is delivered by sibling plans in this phase; this plan only lands the deploy manifests.
- Blocker/dependency: a live k8s cluster is required to exercise actual lease contention and failover (not available in this sandbox).

## Self-Check: PASSED

All created/modified files present; all three task commits (8941b73, 3ac285b, 02bc345) confirmed in git history.

---
*Phase: 82-orchestrator-ha-leader-election*
*Completed: 2026-07-18*
