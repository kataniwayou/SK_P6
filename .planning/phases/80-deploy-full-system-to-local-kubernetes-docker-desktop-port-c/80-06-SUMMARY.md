---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 06
subsystem: infra
tags: [kubernetes, k8s, deployment, keeper, processor-sample, replicas, health-probes, d-08-seams, docker-desktop]

# Dependency graph
requires:
  - phase: 80-01
    provides: skp-dev-secrets Secret (RABBITMQ_DEFAULT_* keys) consumed via secretKeyRef
  - phase: 80-02
    provides: postgres + redis StatefulSets whose Service DNS the keeper/processor connection strings target
  - phase: 80-03
    provides: rabbitmq StatefulSet (bus both readiness probes gate on)
  - phase: 80-04
    provides: otel-collector Service DNS (OTEL_EXPORTER_OTLP_ENDPOINT target)
  - phase: 80-05
    provides: sibling app-Deployment probe/secretKeyRef/literal-TTL pattern reused verbatim
provides:
  - "k8s/32-keeper.yaml — keeper Deployment replicas:2, NO Service (two-consumer recovery worker)"
  - "k8s/33-processor-sample.yaml — processor-sample Deployment replicas:2, NO Service (step-execution worker); bad-config subject excluded"
  - "D-08 seam discipline proven on the two most fault-seam-dense tiers: all four fault seams OMITTED, only literal-900 TTLs shipped"
affects: [80-07-build-seed, 80-08-kustomization, 80-09-bringup-portforward]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "replicas:2 pure-consumer Deployment: shared container-internal port, NO Service (only kubelet probe reaches the pod IP)"
    - "D-08 OMIT-ENTIRELY seam discipline: absent env ⇒ code :-0 default ⇒ OFF; only production TTL literals ship as explicit env"
    - "Decoupled readiness /health/ready + liveness /health/live so a slow ~40s rabbitmq never CrashLoops the worker (Pitfall 3)"

key-files:
  created:
    - k8s/32-keeper.yaml
    - k8s/33-processor-sample.yaml
  modified: []

key-decisions:
  - "keeper: ships literal Recovery__ExecutionDataTtlSeconds \"900\"; both reinject fault seams (KEEPER_DEFEAT_REINJECT, KEEPER_REINJECT_DELAY_MS) omitted entirely — absent ⇒ OFF (D-08 / T-80-11)"
  - "processor-sample: ships literal Processor__ExecutionDataTtl \"900\" (load-bearing production default); both fault seams (PROCESSOR_DEFEAT_READ, PROCESSOR_STEP_DELAY_MS) omitted entirely (D-08 / T-80-11)"
  - "the profile-gated bad-config processor clone is EXCLUDED — no manifest authored (D-04 / T-80-12); acceptance grep asserts that token's absence"
  - "both are replicas:2 pure consumers with NO Service; compose depends_on dropped (phased bring-up handles ordering)"

patterns-established:
  - "Pattern: replicas:2 no-Service worker Deployment (keeper + processor-sample) with decoupled probes on the container-internal health port"

requirements-completed: [GOAL-80-HEALTHY, GOAL-80-ROUNDTRIP]

# Metrics
duration: 8min
completed: 2026-07-18
---

# Phase 80 Plan 06: keeper + processor-sample worker Deployments Summary

**Ported the compose keeper (two-consumer recovery worker) and processor-sample (step-execution worker) to raw k8s Deployments — both replicas:2 with NO Service (pure consumers), shipping only the production TTL literal 900 while OMITTING all four test-only fault seams entirely, with the profile-gated bad-config subject excluded (no manifest); both validate client-side individually and combined.**

## Performance

- **Duration:** ~8 min
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- `k8s/32-keeper.yaml`: keeper Deployment `replicas: 2` (`keeper:local` + IfNotPresent), NO Service. Ships the production literal `Recovery__ExecutionDataTtlSeconds: "900"`; the two reinject fault seams (`KEEPER_DEFEAT_REINJECT`, `KEEPER_REINJECT_DELAY_MS`) are omitted entirely (absent ⇒ code `:-0` default ⇒ OFF). RabbitMq creds via secretKeyRef; Redis/OTEL host strings carried byte-for-byte. Decoupled readiness `/health/ready` + liveness `/health/live` on :8083.
- `k8s/33-processor-sample.yaml`: processor-sample Deployment `replicas: 2` (`processor-sample:local` + IfNotPresent), NO Service. Ships the load-bearing production literal `Processor__ExecutionDataTtl: "900"`; both fault seams (`PROCESSOR_DEFEAT_READ`, `PROCESSOR_STEP_DELAY_MS`) omitted entirely. The profile-gated bad-config processor clone is excluded — no manifest (D-04). Decoupled probes on :8082.
- No `${` interpolation and no fault-seam env names appear in either manifest. Both pass `kubectl apply --dry-run=client` individually and combined (exit 0).

## Task Commits

Each task was committed atomically:

1. **Task 1: keeper Deployment (replicas:2, no Service)** — `f132582` (feat)
2. **Task 2: processor-sample Deployment (replicas:2, no Service; badconfig excluded)** — `613e226` (feat)

## Files Created/Modified
- `k8s/32-keeper.yaml` — keeper Deployment replicas:2, NO Service; literal-900 recovery TTL, both reinject seams omitted; decoupled probes on :8083.
- `k8s/33-processor-sample.yaml` — processor-sample Deployment replicas:2, NO Service; literal-900 execution-data TTL, both fault seams omitted; bad-config subject excluded; decoupled probes on :8082.

## Decisions Made
- None beyond the plan/locked decisions. Followed D-04 (`:local` + IfNotPresent, bad-config excluded), D-07 (secretKeyRef into skp-dev-secrets), D-08 (omit all four fault seams, carry only literal-900 TTLs), and Pitfall 3 (decoupled probes) as specified.

## Deviations from Plan

None — plan executed exactly as written.

The only in-flight adjustment was cosmetic and within the plan's intent (mirroring the sibling 80-05 T-80-10 wording fix): my initial explanatory comments in `33-processor-sample.yaml` literally spelled the `badconfig` token when describing the excluded subject. Because Task 2's acceptance grep asserts the *absence* of `badconfig` anywhere in the file (T-80-12), I reworded those comments to "profile-gated bad-config processor clone" (hyphenated, so the literal token no longer matches). No functional YAML changed; the manifest still validates. This is a documentation-wording fix, not a plan deviation.

## Issues Encountered
- Acceptance grep sensitivity (above): caught during Task 2 verification before commit; resolved by rewording comments. Post-fix greps: `${` count = 0, fault-seam-name count = 0, `badconfig` count = 0, `kind: Service` count = 0.

## Known Stubs
None — both manifests are fully wired (real Secret refs, real Service DNS targets, real health endpoints, production TTL literals). No placeholder/empty values.

## Threat Flags
None — no new trust-boundary surface beyond the plan's threat_model. T-80-11 (test-seam absence) is actively satisfied: all four fault seams omitted, only literal-900 TTLs ship. T-80-12 (bad-config exclusion) is satisfied: no manifest authored, grep-asserted absent. Both workers stay pure in-cluster consumers with no inbound Service.

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- All four app Deployments now exist (80-05 baseapi + orchestrator; 80-06 keeper ×2 + processor-sample ×2). Plan 80-07 can build the `:local` images + reseed, and 80-08/80-09 can aggregate (kustomization) + bring up with port-forwards.
- These manifests only validate client-side; live Ready state depends on the backing StatefulSets (80-02/03/04) and the build-before-seed + rollout-restart flow (80-07/80-09) to satisfy SourceHash currency (T-80-09).

## Self-Check: PASSED

- FOUND: k8s/32-keeper.yaml
- FOUND: k8s/33-processor-sample.yaml
- FOUND: .planning/phases/80-.../80-06-SUMMARY.md
- FOUND commit f132582 (Task 1)
- FOUND commit 613e226 (Task 2)

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
