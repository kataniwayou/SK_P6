---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 04
subsystem: infra
tags: [kubernetes, k8s, otel-collector, prometheus, deployment, clusterip, configmap, subpath, telemetry]

# Dependency graph
requires:
  - phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
    plan: 01
    provides: "ConfigMaps otel-collector-config + prometheus-config, Namespace skp (subPath mount targets)"
provides:
  - "otel-collector Deployment + 4-port ClusterIP Service (4317/4318/8889/13133) — the OTLP ingest + Prom-scrape backbone"
  - "prometheus Deployment + ClusterIP Service (9090) — scrapes otel-collector:8889 in-cluster"
  - "In-cluster Service DNS: otel-collector:4317 (OTLP), otel-collector:8889 (scrape), prometheus:9090 (query)"
affects: [80-05, 80-06, 80-09]  # app Deployments export OTLP to otel-collector; the proof reads Prometheus + ES via port-forward

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ephemeral telemetry Deployment (no PVC, D-13) with kubelet-side httpGet readiness on a distroless image"
    - "single-file ConfigMap subPath mount at the exact compose in-container path (preserves image dirs, T-80-07)"

key-files:
  created:
    - k8s/20-otel-collector.yaml
    - k8s/21-prometheus.yaml
  modified: []

key-decisions:
  - "otel-collector readiness upgraded from compose no-healthcheck to kubelet-side httpGet :13133 — the kubelet probes from the node, so the distroless (no shell/wget/curl) image is no longer a blocker (RESEARCH Pattern 6)"
  - "Both configs mounted via single-file subPath (not whole-dir) at compose's exact paths — preserves the images' own /etc dirs (T-80-07)"
  - "compose depends_on dropped (no k8s equivalent); ordering deferred to the bring-up script (plan 80-09); host publishes dropped (host reach via port-forward)"

patterns-established:
  - "Telemetry backbone as ephemeral ClusterIP Deployments: in-cluster-only Services, host reach exclusively via 127.0.0.1 port-forward"

requirements-completed: [GOAL-80-HEALTHY]

# Metrics
duration: 3min
completed: 2026-07-18
---

# Phase 80 Plan 04: otel-collector + prometheus Deployments (Telemetry Backbone) Summary

**The OTLP/metrics backbone ported to k8s: otel-collector as a Deployment with a 4-port ClusterIP Service (4317/4318/8889/13133) and a kubelet-side httpGet :13133 readiness probe, and prometheus as a Deployment with a ClusterIP Service on 9090 and an httpGet /-/healthy readiness probe — both mounting their plan-80-01 ConfigMaps via single-file subPath, both ephemeral (no PVC, D-13).**

## Performance

- **Duration:** ~3 min
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- `k8s/20-otel-collector.yaml`: Deployment (`otel-collector`, replicas 1) + ClusterIP Service exposing named ports `otlp-grpc` 4317, `otlp-http` 4318, `prom` 8889, `health` 13133. Image `otel/opentelemetry-collector-contrib:0.152.0` verbatim; `args: [--config=/etc/otel-collector-config.yaml]` verbatim from compose. ConfigMap `otel-collector-config` mounted via `subPath: otel-collector-config.yaml` at `/etc/otel-collector-config.yaml`. Readiness `httpGet: {path: /, port: 13133}` (5s/10s/5). 128Mi/256Mi resources.
- `k8s/21-prometheus.yaml`: Deployment (`prometheus`, replicas 1) + ClusterIP Service on 9090. Image `prom/prometheus:v3.11.3` verbatim; both command flags (`--config.file=/etc/prometheus/prometheus.yml`, `--web.enable-lifecycle`) verbatim. ConfigMap `prometheus-config` mounted via `subPath: prometheus.yml` at `/etc/prometheus/prometheus.yml`. Readiness `httpGet: {path: /-/healthy, port: 9090}` (10s/10s/5s/3). 128Mi/256Mi resources.
- Both individually and together validate `kubectl apply --dry-run=client` exit 0.

## Task Commits

Each task was committed atomically:

1. **Task 1: otel-collector Deployment + ClusterIP Service + config mount** - `0d93235` (feat)
2. **Task 2: prometheus Deployment + ClusterIP Service + config mount** - `9c7551b` (feat)

## Files Created/Modified
- `k8s/20-otel-collector.yaml` - otel-collector Deployment + 4-port ClusterIP Service + otel-collector-config subPath mount + httpGet :13133 readiness.
- `k8s/21-prometheus.yaml` - prometheus Deployment + ClusterIP Service (9090) + prometheus-config subPath mount + httpGet /-/healthy readiness.

## Decisions Made
- **Readiness probe upgrade (otel-collector):** compose ran the distroless contrib image with NO healthcheck (no shell/wget/curl for an in-container probe). k8s uses a kubelet-side `httpGet :13133` readiness probe — the kubelet issues the request from the node, so the distroless image is no longer a limitation, and `rollout status` becomes meaningful (RESEARCH Pattern 6).
- **subPath fidelity (T-80-07):** both configs mounted as single-file `subPath` at compose's exact in-container paths rather than whole-dir mounts, so the images' own `/etc` contents (prometheus console libs, collector dirs) are preserved.
- **Dropped compose seams:** all host publishes (4317/4318/8889/13133, 9090) dropped — host reach is exclusively `127.0.0.1` port-forward (plan 80-09); `depends_on: otel-collector` dropped from prometheus (no k8s equivalent; ordering handled by the bring-up script, self-healing scrape target).

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None. `kubectl` client-side dry-runs passed on first attempt for both manifests.

## Threat Register Outcome
- **T-80-07 (Tampering / ConfigMap subPath hides image dirs, mitigate):** both mounts are single-file `subPath` (not a whole-dir mount over `/etc/prometheus` or `/etc`), so the image console libs / collector dirs are preserved.
- **T-80-08 (Info Disclosure / collector+prometheus endpoints, accept):** both Services are ClusterIP (in-cluster only); no ingress, no host publish. Host reach is 127.0.0.1 port-forward (plan 80-09) — no new external surface.

## Known Stubs
None. Both manifests are complete, self-contained, and reference the real plan-80-01 ConfigMaps.

## User Setup Required
None - no external service configuration required. Live server-side apply is exercised by the bring-up script (plan 80-09).

## Next Phase Readiness
- Telemetry backbone ready. App Deployments (80-05/80-06) can export OTLP to `otel-collector:4317` and prometheus scrapes `otel-collector:8889` in-cluster; the proof (plan 80-09) reads Prometheus (:9090) + ES via port-forward.
- No blockers. Ordering across tiers is the bring-up script's responsibility (plan 80-09).

## Self-Check: PASSED

- Both manifests + SUMMARY.md exist on disk.
- Both task commits (`0d93235`, `9c7551b`) present in git history.
- `kubectl apply -f k8s/20-otel-collector.yaml -f k8s/21-prometheus.yaml --dry-run=client` exits 0.

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
