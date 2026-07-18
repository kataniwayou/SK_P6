---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 07
subsystem: infra
tags: [kubernetes, kustomize, docker, powershell, sourcehash, deploy]

# Dependency graph
requires:
  - phase: 80 (plans 01-06, Wave 1)
    provides: the 13 raw k8s manifests (namespace, secret, configmaps, 4 stateful backing services, otel/prometheus, 4 app deployments)
provides:
  - "scripts/phase-80-build.ps1 — builds the 4 app images :local (baseapi-service, orchestrator, keeper, processor-sample) with fail-loud checks, badconfig excluded"
  - "k8s/kustomization.yaml — ordered-apply aggregator listing all 13 manifests under namespace skp + common labels"
  - "whole-stack lint gate: kubectl apply -k k8s/ --dry-run=client and --dry-run=server both exit 0"
affects: [80-08, 80-09, 80-10, k8s bring-up, phase-80-up]

# Tech tracking
tech-stack:
  added: [kustomize aggregator (kustomize.config.k8s.io/v1beta1)]
  patterns:
    - "per-image docker build -t <name>:local (replaces compose STEP-A0 docker compose build) for SourceHash currency under IfNotPresent"
    - "kustomization = resource-list + namespace/label stamp ONLY (D-01/D-03, no templating/overlays/generators)"

key-files:
  created:
    - scripts/phase-80-build.ps1
    - k8s/kustomization.yaml
  modified: []

key-decisions:
  - "Kept the hand-authored 02-configmaps.yaml as a listed resource rather than a configMapGenerator (RESEARCH Open-Q4 default) — a generator's content-hash name suffix would break the Deployment volume configMap.name references"
  - "Retained commonLabels (plan default) despite the kustomize deprecation warning — functional, and no immutable-selector conflict arises on fresh resources; the labels/includeSelectors migration was unnecessary"
  - "Server dry-run requires the skp namespace to pre-exist (kubectl chicken-and-egg on namespace-creating bundles) — created it transiently to prove whole-stack server validation, then deleted it to leave the cluster in its pre-plan state"

patterns-established:
  - "Build script fail-loud contract: $ErrorActionPreference='Stop' + per-build $LASTEXITCODE -ne 0 → exit 10, ported verbatim from harness STEP A0"
  - "Whole-stack malformed-manifest guard (T-80-14): kubectl apply -k --dry-run validates the entire aggregate before any real apply"

requirements-completed: [GOAL-80-HEALTHY, GOAL-80-ROUNDTRIP]

# Metrics
duration: 3min
completed: 2026-07-18
---

# Phase 80 Plan 07: Build Script + Kustomization Aggregator Summary

**phase-80-build.ps1 builds the 4 app images `:local` with fail-loud checks, and k8s/kustomization.yaml aggregates all 13 manifests under namespace `skp` — the whole stack passes both `kubectl apply -k` client and server dry-runs (exit 0).**

## Performance

- **Duration:** 3 min
- **Started:** 2026-07-18T06:51:19Z
- **Completed:** 2026-07-18T06:54:05Z
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- `scripts/phase-80-build.ps1` — ports compose STEP-A0's single `docker compose build` to 4 explicit `docker build -t <name>:local -f <Dockerfile> .` invocations (baseapi-service→Dockerfile, orchestrator/keeper/processor-sample→their src Dockerfiles), each with a fail-loud `$LASTEXITCODE -ne 0 → exit 10` check; `Processor.BadConfig` explicitly excluded (D-04) with an inline note; preserves SourceHash currency (D-05) for the IfNotPresent kubelet reads.
- `k8s/kustomization.yaml` — the D-03 ordered-apply convenience aggregator listing all 13 manifests, stamping `namespace: skp` + the two common labels (`part-of: skp`, `managed-by: kustomize`); no templating, no configMapGenerator (the hand-authored `02-configmaps.yaml` stays a listed resource).
- Whole-stack lint gate proven: `kubectl apply -k k8s/ --dry-run=client` exits 0 (all 21 rendered objects), and `--dry-run=server` exits 0 against the live Docker Desktop API (with the `skp` namespace pre-existing).

## Task Commits

Each task was committed atomically:

1. **Task 1: phase-80-build.ps1 — 5 local image builds** - `45a71bc` (feat)
2. **Task 2: kustomization.yaml aggregator + whole-stack dry-run gate** - `1521506` (feat)

**Plan metadata:** (this commit) (docs: complete plan)

## Files Created/Modified
- `scripts/phase-80-build.ps1` - Builds the 4 app images `:local` (badconfig excluded) with fail-loud `$LASTEXITCODE` checks; ports compose STEP-A0 for SourceHash currency.
- `k8s/kustomization.yaml` - Lists all 13 manifests + stamps `namespace: skp` and common labels; the single `kubectl apply -k k8s/` entry point and the phase's whole-stack lint gate.

## Decisions Made
- **Kept `02-configmaps.yaml` as a listed resource, no `configMapGenerator`** — the RESEARCH Open-Q4 default. A generator would append a content-hash suffix to the ConfigMap names, breaking the Deployment volume `configMap.name` references authored in Wave 1.
- **Retained `commonLabels`** (plan default) despite kustomize's deprecation warning. It is functional; the labels are additive supersets of the per-tier `app:` selectors and inject cleanly into fresh resources (no immutable-selector conflict), so the `labels:`/`includeSelectors` migration the plan offered as a fallback was not needed.
- **Server dry-run needs the `skp` namespace to exist first** — a known kubectl limitation for a bundle that creates a namespace and its contents in one apply (`namespaces "skp" not found` on `--dry-run=server` because dry-run never actually creates the namespace). Created the namespace transiently to obtain a genuine whole-stack server validation, then deleted it to leave the cluster unchanged from its pre-plan state (the real apply is plan 80-09/80-10's job).

## Deviations from Plan

None - plan executed exactly as written. (The build script's "5 image builds" task name reflects D-04's five-app-image count; only 4 Dockerfiles are built, with `Processor.BadConfig` intentionally excluded, exactly as the task action specified.)

## Issues Encountered
- **`--dry-run=server` initial failure (`namespaces "skp" not found`)** — not a manifest defect. Server-side dry-run does not persist the `Namespace` object, so the 20 namespaced resources could not be validated against it. Resolved by creating the `skp` namespace for real (idempotent; it is the deploy target), re-running the full server dry-run to exit 0, then deleting the namespace. All 21 objects render and validate; the client dry-run (namespace-independent) is clean throughout.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The aggregation deliverables are complete: `phase-80-build.ps1` and `kustomization.yaml` are the two inputs the k8s bring-up script (`scripts/phase-80-up.ps1`, plan 80-08/80-09) consumes — build images `:local`, then `kubectl apply -k k8s/`, then `kubectl rollout restart` to force pods onto fresh bits.
- The whole-stack server dry-run is proven (exit 0), so the manifests are API-server-valid ahead of the live bring-up + round-trip proof.
- No blockers.

## Self-Check: PASSED

All created files verified on disk (`scripts/phase-80-build.ps1`, `k8s/kustomization.yaml`, `80-07-SUMMARY.md`); both task commits (`45a71bc`, `1521506`) verified in git log.

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
