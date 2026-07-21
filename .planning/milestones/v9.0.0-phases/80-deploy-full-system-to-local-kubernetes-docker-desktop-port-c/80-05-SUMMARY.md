---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 05
subsystem: infra
tags: [kubernetes, k8s, deployment, baseapi, orchestrator, secretKeyRef, health-probes, docker-desktop]

# Dependency graph
requires:
  - phase: 80-01
    provides: skp-dev-secrets Secret (POSTGRES_* + RABBITMQ_DEFAULT_* keys) consumed via secretKeyRef
  - phase: 80-02
    provides: postgres + redis StatefulSets whose Service DNS the baseapi/orchestrator connection strings target
  - phase: 80-03
    provides: rabbitmq StatefulSet (bus the orchestrator readiness probe gates on)
  - phase: 80-04
    provides: otel-collector Service DNS (OTEL_EXPORTER_OTLP_ENDPOINT target)
provides:
  - "k8s/30-baseapi-service.yaml — baseapi-service Deployment x1 + ClusterIP Service on 8080 (the sole HTTP entrypoint for the harness)"
  - "k8s/31-orchestrator.yaml — orchestrator Deployment x1 (documented SPOF), NO Service (pure consumer)"
  - "Pattern 3 $(VAR) dependent-env Postgres string; literal-900 TTL with all test seams dropped; decoupled readiness/liveness probes"
affects: [80-06-keeper-processor, 80-07-build-seed, 80-08-kustomization, 80-09-bringup-portforward]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "k8s dependent-env expansion: secretKeyRef entries ordered BEFORE the $(VAR)-composed connection string"
    - "Decoupled probes: readiness /health/ready (traffic gate), liveness /health/live (always-Healthy self check) so a slow broker never CrashLoops the pod"
    - "Pure-consumer app tiers get NO Service (only the kubelet probe reaches the pod IP)"

key-files:
  created:
    - k8s/30-baseapi-service.yaml
    - k8s/31-orchestrator.yaml
  modified: []

key-decisions:
  - "baseapi-service: 3 POSTGRES_* secretKeyRef listed before ConnectionStrings__Postgres so the kubelet expands $(POSTGRES_DB)/$(POSTGRES_USER)/$(POSTGRES_PASSWORD) (RESEARCH Pattern 3)"
  - "orchestrator ships the production literal Orchestrator__OutputDataTtlSeconds: \"900\" — the interpolated test seam is dropped entirely (D-08 / T-80-10); manifest has no ${ interpolation and no seam env names"
  - "orchestrator gets NO Service (pure consumer); replicas:1 LOCKED (documented SPOF)"

patterns-established:
  - "Pattern 1: dependent-env ordering for $(VAR)-composed secret-backed connection strings"
  - "Pattern 2: decoupled readiness(/health/ready) + liveness(/health/live) probes on HARD-on-broker app tiers (Pitfall 3)"

requirements-completed: [GOAL-80-HEALTHY, GOAL-80-ROUNDTRIP]

# Metrics
duration: 12min
completed: 2026-07-18
---

# Phase 80 Plan 05: baseapi-service + orchestrator app Deployments Summary

**Ported the compose baseapi-service (HTTP control plane, Deployment x1 + ClusterIP Service 8080 with a $(VAR)-composed secret-backed Postgres string) and orchestrator (documented-SPOF Deployment x1, NO Service, literal-900 TTL with all test seams dropped) to raw k8s manifests, both validating client-side with decoupled /health/ready + /health/live probes.**

## Performance

- **Duration:** 12 min
- **Started:** 2026-07-18T06:32:00Z
- **Completed:** 2026-07-18T06:44:04Z
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- `k8s/30-baseapi-service.yaml`: baseapi-service Deployment x1 (`baseapi-service:local` + IfNotPresent) with the 3 POSTGRES_* secretKeyRef entries ordered before the `$(VAR)`-composed `ConnectionStrings__Postgres`, Redis/OTEL/RabbitMq host strings carried byte-for-byte, RabbitMq creds via secretKeyRef, decoupled readiness/liveness probes on 8080, and a ClusterIP Service exposing 8080.
- `k8s/31-orchestrator.yaml`: orchestrator Deployment x1 (documented SPOF, `replicas:1` locked, `orchestrator:local` + IfNotPresent) shipping the production literal `Orchestrator__OutputDataTtlSeconds: "900"` with NO `${` interpolation and NO test-seam env names, decoupled probes on 8081, and NO Service (pure consumer).
- Both manifests pass `kubectl apply --dry-run=client` individually and combined.

## Task Commits

Each task was committed atomically:

1. **Task 1: baseapi-service Deployment + ClusterIP Service (8080)** - `3419487` (feat)
2. **Task 2: orchestrator Deployment x1 (SPOF, no Service)** - `5f25c47` (feat)

**Plan metadata:** _(this SUMMARY + STATE/ROADMAP)_

## Files Created/Modified
- `k8s/30-baseapi-service.yaml` - baseapi-service Deployment x1 + ClusterIP Service 8080; secret-backed $(VAR)-composed Postgres string; decoupled probes.
- `k8s/31-orchestrator.yaml` - orchestrator Deployment x1 (no Service, SPOF); literal-900 TTL, no test seams; decoupled probes on 8081.

## Decisions Made
- None beyond the plan/locked decisions. Followed D-04 (`:local` + IfNotPresent), D-07 (secretKeyRef into skp-dev-secrets), D-08 (drop interpolated test seams, carry literal 900), and Pitfall 3 (decoupled probes) as specified.

## Deviations from Plan

None - plan executed exactly as written.

The only in-flight adjustment was cosmetic and within the plan's intent: my initial explanatory YAML comments in `31-orchestrator.yaml` literally spelled out the dropped seam names (`ORCH_OUTPUT_TTL`) and the `${...}` interpolation form. Because the plan's acceptance criteria / threat mitigation T-80-10 grep for the *absence* of `${` and seam names anywhere in the file, I reworded the comments to describe the drop without reproducing those literal tokens. No functional YAML changed; both manifests still validate. This is a documentation-wording fix, not a plan deviation.

## Issues Encountered
- Acceptance grep sensitivity (above): caught during Task 2 verification before commit; resolved by rewording comments. Post-fix greps: `${` count = 0, seam-name count = 0.

## Known Stubs
None — both manifests are fully wired (real Secret refs, real Service DNS targets, real health endpoints). No placeholder/empty values.

## Threat Flags
None — no new trust-boundary surface beyond the plan's threat_model. baseapi remains the single HTTP entrypoint (T-80-08, reached only via port-forward); T-80-10 (test-seam absence) is actively satisfied by the literal-900 orchestrator config.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- App Deployments for the two singletons are ready. Plan 80-06 can add keeper (replicas:2) + processor-sample (replicas:2) following the same secretKeyRef + decoupled-probe + literal-TTL patterns.
- These manifests only validate client-side; live Ready state depends on the backing StatefulSets (80-02/03/04) and the build-before-seed + rollout-restart flow (80-07/80-09) to satisfy SourceHash currency (T-80-09).

## Self-Check: PASSED

- FOUND: k8s/30-baseapi-service.yaml
- FOUND: k8s/31-orchestrator.yaml
- FOUND: .planning/phases/80-.../80-05-SUMMARY.md
- FOUND commit 3419487 (Task 1)
- FOUND commit 5f25c47 (Task 2)

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
