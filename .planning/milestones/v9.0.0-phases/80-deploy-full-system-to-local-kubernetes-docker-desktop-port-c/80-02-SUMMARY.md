---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 02
subsystem: infra
tags: [kubernetes, k8s, statefulset, postgres, redis, headless-service, pvc, docker-desktop]

# Dependency graph
requires:
  - phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
    plan: 01
    provides: "Namespace skp + Secret skp-dev-secrets (secretKeyRef source for POSTGRES_* creds)"
provides:
  - "postgres StatefulSet + 1Gi PVC + headless Service — L3 store, in-cluster DNS postgres:5432 (D-09)"
  - "redis StatefulSet (NO PVC) + headless Service — ephemeral L2 cache, in-cluster DNS redis:6379 (D-12)"
affects: [80-05, 80-06, 80-09]  # app Deployments dial postgres:5432 / redis:6379; port-forward re-exposes at 5433/6380

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "StatefulSet + same-named headless Service (clusterIP: None) for stable pod identity + DNS (Pattern 1)"
    - "per-service PVC choice: postgres gets volumeClaimTemplates 1Gi; redis deliberately omits it (ephemeral D-12)"
    - "backing-service creds via secretKeyRef into skp-dev-secrets — never baked into the image (D-07)"

key-files:
  created:
    - k8s/10-postgres.yaml
    - k8s/11-redis.yaml
  modified: []

key-decisions:
  - "postgres readiness uses pg_isready -U postgres -d stepsdb (literal creds, since .env resolves USER/DB to postgres/stepsdb) — translated from compose CMD-SHELL healthcheck"
  - "redis command args carried byte-for-byte ['redis-server','--save','','--appendonly','no'] — persistence stays OFF (D-12), NO PVC"
  - "storageClassName omitted on the postgres PVC so Docker Desktop's default hostpath provisioner binds"
  - "compose host publishes (5433:5432, 6380:6379) dropped — they re-emerge only at the port-forward layer (D-14, plan 80-09)"

patterns-established:
  - "Backing-service StatefulSet template: headless Service first, StatefulSet second, app label + part-of skp label, memory requests/limits for single-node scheduling"

requirements-completed: [GOAL-80-HEALTHY]

# Metrics
duration: 3min
completed: 2026-07-18
---

# Phase 80 Plan 02: Backing StatefulSets (postgres + redis) Summary

**postgres (StatefulSet + 1Gi PVC + headless Service, secret-backed creds, pg_isready readiness) and redis (StatefulSet, NO PVC, verbatim persistence-off command, redis-cli ping readiness) — two of the four backing services, reachable in-cluster at the bare names postgres:5432 and redis:6379 that every app connection string resolves to.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-07-18T06:31:52Z
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- `k8s/10-postgres.yaml` — headless Service (`clusterIP: None`) + StatefulSet (`serviceName: postgres`, `replicas: 1`), `image: postgres:17-alpine` verbatim, all three `POSTGRES_*` env via `secretKeyRef` into `skp-dev-secrets`, `pg_isready -U postgres -d stepsdb` readiness probe (5s/5s/10/5s), `volumeClaimTemplates` `data` 1Gi mounted at `/var/lib/postgresql/data` (D-09), memory requests/limits for single-node scheduling.
- `k8s/11-redis.yaml` — headless Service + StatefulSet (`serviceName: redis`, `replicas: 1`), `image: redis:7.4.9-alpine` verbatim, `command: ["redis-server","--save","","--appendonly","no"]` carried byte-for-byte (D-12), `redis-cli ping` readiness probe (5s/3s/10/5s), and deliberately **NO** `volumeClaimTemplates`/`volumeMounts` (ephemeral by design).
- Per-file and combined `kubectl apply --dry-run=client` both exit 0.

## Task Commits

Each task was committed atomically:

1. **Task 1: postgres StatefulSet + PVC + headless Service** - `e348ccd` (feat)
2. **Task 2: redis StatefulSet (no PVC) + headless Service** - `0d2b342` (feat)

## Files Created/Modified
- `k8s/10-postgres.yaml` - postgres StatefulSet + 1Gi PVC + headless Service (D-09).
- `k8s/11-redis.yaml` - redis StatefulSet (no PVC) + headless Service, persistence-off command (D-12).

## Decisions Made
- postgres readiness probe uses literal `-U postgres -d stepsdb` (the `.env`-resolved values), translated from the compose `CMD-SHELL pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB` — kubelet exec probes take no shell env interpolation, and the seeded creds are fixed at postgres/stepsdb.
- redis `command` array carried byte-for-byte with the empty-string `--save` value; persistence stays OFF and NO PVC is provisioned (D-12) — L2 is rebuildable from L3.
- `storageClassName` omitted on the postgres PVC so Docker Desktop's default `hostpath` provisioner binds automatically.
- compose host publishes dropped (5433:5432, 6380:6379); they are a compose-only host-port concern and re-emerge only at the `kubectl port-forward` layer in plan 80-09 (D-14).

## Deviations from Plan

None - plan executed exactly as written.

## Threat Register Outcome
- **T-80-04 (Spoofing / postgres creds, mitigate):** all three `POSTGRES_*` values are sourced only from `skp-dev-secrets` via `secretKeyRef` — never baked into the manifest or image, preserving the compose dev-only posture.
- **T-80-03 (Info Disclosure, accept):** both Services are headless/ClusterIP-class (`clusterIP: None`) — no NodePort/ingress; reachable only in-cluster within `skp` or via explicit 127.0.0.1 port-forward (plan 80-09).

## User Setup Required
None - no external service configuration required (dev-only committed creds; local Docker Desktop k8s). Live `kubectl apply` (server-side) is exercised by the bring-up script plan (80-08/80-09), not this manifest plan.

## Next Phase Readiness
- postgres and redis manifests validate client-side and expose the bare Service DNS (`postgres:5432`, `redis:6379`) that the app Deployment plans reference byte-for-byte. Sibling backing-service plans (rabbitmq 80-03, elasticsearch 80-04) follow the same StatefulSet + headless Service template. No blockers.

## Self-Check: PASSED

- Both manifests exist on disk (`k8s/10-postgres.yaml`, `k8s/11-redis.yaml`).
- Both task commits (`e348ccd`, `0d2b342`) present in git history.
- Combined `kubectl apply --dry-run=client` exits 0 (both StatefulSets + both Services).

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
