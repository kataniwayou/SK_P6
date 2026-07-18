---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 03
subsystem: infra
tags: [kubernetes, statefulset, rabbitmq, elasticsearch, startupProbe, pvc, docker-desktop]

# Dependency graph
requires:
  - phase: 80 (plan 00-namespace)
    provides: namespace skp
  - phase: 80 (plan 01-secret)
    provides: skp-dev-secrets with RABBITMQ_DEFAULT_USER/PASS keys
provides:
  - "rabbitmq StatefulSet + 1Gi PVC + dual-port (5672/15672) headless Service with a 60s-budget startupProbe"
  - "elasticsearch StatefulSet + 2Gi PVC + headless Service (9200) with a 120s-budget yellow-status startupProbe"
  - "in-cluster DNS: rabbitmq:5672/15672 and elasticsearch:9200"
affects: [80-20-otel-collector, 80-21-prometheus, 80-30-baseapi, 80-31-orchestrator, 80-32-keeper, 80-33-processor, 80-up-script, 80-reset-script]

# Tech tracking
tech-stack:
  added: [rabbitmq:4.1.8-management-alpine, "docker.elastic.co/elasticsearch/elasticsearch:8.15.5"]
  patterns:
    - "startupProbe (periodSeconds x failureThreshold) translates the compose start_period cold-start budget so a slow start never CrashLoops liveness"
    - "single-node ES readiness gated on wait_for_status=yellow (never green — replicas unassignable)"
    - "backing-service durability via volumeClaimTemplates deliberately diverging from compose ephemeral posture"

key-files:
  created:
    - k8s/12-rabbitmq.yaml
    - k8s/13-elasticsearch.yaml
  modified: []

key-decisions:
  - "rabbitmq startupProbe 10s x 6 = 60s covers the ~40s compose start_period; readiness reuses rabbitmq-diagnostics ping"
  - "elasticsearch startupProbe 10s x 12 = 120s covers the ~60s compose start_period; wait_for_status=yellow is load-bearing"
  - "rabbitmq 1Gi PVC (D-11) for durable mnesia; elasticsearch 2Gi PVC (D-10) for run-evidence stability"
  - "elasticsearch resources 700Mi/1Gi hold the 512m JVM heap + off-heap overhead (Pitfall 6)"

patterns-established:
  - "startupProbe budget = periodSeconds x failureThreshold >= compose start_period, decoupled from liveness (Pitfall 3)"
  - "compose host publishes (5673/15673/9200) dropped; re-emerge only at port-forward (D-14)"

requirements-completed: [GOAL-80-HEALTHY]

# Metrics
duration: 2min
completed: 2026-07-18
---

# Phase 80 Plan 03: rabbitmq + elasticsearch StatefulSets Summary

**rabbitmq (1Gi PVC, dual-port headless Service, 60s-budget diagnostics-ping startupProbe) and elasticsearch (2Gi PVC, 120s-budget yellow-status startupProbe, 4 verbatim env vars) both validate client-side against Docker Desktop k8s.**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-07-18T06:35:34Z
- **Completed:** 2026-07-18T06:36:49Z
- **Tasks:** 2
- **Files modified:** 2 (created)

## Accomplishments
- Ported the compose `rabbitmq` block (L161-176) to a StatefulSet with a 1Gi PVC at `/var/lib/rabbitmq`, a headless Service exposing BOTH 5672 (amqp) and 15672 (mgmt), secret-backed creds, and a `rabbitmq-diagnostics ping` startupProbe sized to the 40s cold start.
- Ported the compose `elasticsearch` block (L28-49) to a StatefulSet with a 2Gi PVC at `/usr/share/elasticsearch/data`, all 4 env vars carried verbatim, and a `wait_for_status=yellow` httpGet startupProbe sized to the 60s cold start.
- Combined `kubectl apply --dry-run=client` over both manifests exits 0.

## Task Commits

Each task was committed atomically:

1. **Task 1: rabbitmq StatefulSet + PVC + startupProbe + headless Service** - `f9a5f15` (feat)
2. **Task 2: elasticsearch StatefulSet + PVC + startupProbe (ready at yellow)** - `95ad555` (feat)

## Files Created/Modified
- `k8s/12-rabbitmq.yaml` - rabbitmq StatefulSet + 1Gi PVC + dual-port (5672/15672) headless Service + 60s-budget diagnostics-ping startupProbe + secretKeyRef creds.
- `k8s/13-elasticsearch.yaml` - elasticsearch StatefulSet + 2Gi PVC + headless Service (9200) + 120s-budget yellow-status startupProbe + 4 verbatim env vars.

## Decisions Made
None beyond the plan — followed the locked CONTEXT decisions (D-10 ES +2Gi PVC, D-11 rabbitmq +1Gi PVC, D-14 drop host publishes) and PATTERNS probe arithmetic exactly.

## Deviations from Plan

None - plan executed exactly as written.

## Threat Surface
Both manifests match the plan's `<threat_model>`. T-80-06 (OOM DoS, `mitigate`) is honored via conservative memory requests/limits (rabbitmq 256Mi/512Mi; ES 700Mi/1Gi over the 512m heap). T-80-05 (ES security disabled, `accept`) carried verbatim from the proven compose posture. No new trust-boundary surface introduced — both stay ClusterIP/headless, no ingress.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required. Live apply is exercised by the plan 80-up bring-up script; this plan is a client-side validation gate only (Docker Desktop k8s not required for `--dry-run=client`).

## Next Phase Readiness
- Both backing-service manifests validate and expose stable in-cluster DNS for the otel-collector (ES sink), the 4 app tiers (rabbitmq broker), and the bring-up/reset scripts.
- No blockers. Ready for the remaining tier manifests (20/21 observability, 30-33 apps) and the phased bring-up.

## Self-Check: PASSED

- FOUND: k8s/12-rabbitmq.yaml
- FOUND: k8s/13-elasticsearch.yaml
- FOUND: .planning/phases/80-.../80-03-SUMMARY.md
- FOUND commit: f9a5f15 (Task 1)
- FOUND commit: 95ad555 (Task 2)

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
