# Phase 80: Deploy full system to local Kubernetes — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-18
**Phase:** 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
**Areas discussed:** Manifest tooling & layout, Image build & SourceHash currency, Config & secrets, Persistence posture, Reseed + end-to-end proof mechanism

**Format note:** Per the user's stated preference for conversational clarification over TUI menus on substantive design questions, areas were presented as a single structured recommendation set with rationale; the user reviewed and confirmed all recommendations ("all confirm") in one pass.

---

## Manifest tooling & layout

| Option | Description | Selected |
|--------|-------------|----------|
| Raw kubectl YAML manifests | One file per tier under `k8s/`, namespace `skp`, optional non-templating `kustomization.yaml` aggregator | ✓ |
| Kustomize base + overlays | Base + per-env overlays | |
| Helm chart | Templated chart with releases | |

**User's choice:** Raw YAML manifests.
**Notes:** One deploy target (local Docker Desktop) — overlay/chart indirection unjustified; transparency and diff-against-`compose.yaml` prioritized.

---

## Image build & SourceHash currency

| Option | Description | Selected |
|--------|-------------|----------|
| Local `docker build` + `:local` tag + `imagePullPolicy: IfNotPresent` | Docker Desktop shares the local image store; no registry | ✓ |
| Push to a registry | Tag + push + pull | |

**User's choice:** Local build, `IfNotPresent`.
**Notes:** Build-before-seed ordering (compose STEP A0) preserved so the container SourceHash matches the seeder's host-built `Processor.Sample.dll`. `:latest` avoided (Always-pull semantics). Processor.BadConfig excluded (profile-gated).

---

## Config & secrets

| Option | Description | Selected |
|--------|-------------|----------|
| ConfigMaps for the two bind-mounted config files + committed dev-only Secret for creds + omit test seams | otel + prometheus configs → ConfigMaps; postgres/rabbitmq creds → committed dev Secret; test seams absent ⇒ default-off | ✓ |
| Plain env/ConfigMap for creds | No Secret | |
| Generated-at-apply Secret (not committed) | `kubectl create secret` in a script | |

**User's choice:** ConfigMaps + committed dev-only Secret + omit test seams.
**Notes:** Compose comments already anticipate the k8s-Secret path; same dev creds already in `.env`. Production TTL literals (`900`) carried explicitly; test seams (`KEEPER_DEFEAT_REINJECT`, `PROCESSOR_DEFEAT_READ`, etc.) omitted entirely.

---

## Persistence posture

| Option | Description | Selected |
|--------|-------------|----------|
| Per-service PVC decision (postgres/ES/rabbitmq PVC; redis none; prom/otel Deployments) | StatefulSet for identity; PVC only where state matters; redis ephemeral by design | ✓ |
| PVC on all four StatefulSets | Literal "with persistence" incl. redis | |
| Match compose (postgres PVC only, rest ephemeral) | Faithful compose parity | |

**User's choice:** Per-service PVC decision (postgres + ES + rabbitmq PVCs; redis no PVC; prometheus + otel-collector Deployments).
**Notes:** Redis-without-PVC honors Phase 12 D-03 (L2 rebuildable from L3, persistence off by design). ES + rabbitmq PVCs chosen for run-evidence/queue stability across reschedule (deliberate divergence from compose ephemeral).

---

## Reseed + end-to-end proof mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| Reuse host harness via `kubectl port-forward` at existing localhost ports | ClusterIP services + port-forward 8080/9090/9200/15673/6380/5433; harness runs unchanged | ✓ |
| In-cluster k8s Jobs | Reset/seed/verify as containerized Jobs | |
| NodePort exposure | Static NodePort services | |

**User's choice:** Reuse host harness via port-forward.
**Notes:** Zero harness URL edits; SourceHash reflection stays host-side. New deliverable = k8s bring-up script paralleling `phase-65-up.ps1`. Proof scope confirmed **happy-path only** (healthy stack → seed → 204 → sweep PASS); fault scenarios stay a compose concern (already proven v8.0.0).

---

## Claude's Discretion

- Exact probe wiring/timing per tier (translated from compose healthchecks; otel-collector gets a TCP-socket probe or `running`=ready).
- PVC sizes, storageClass, resource requests/limits, label conventions.
- File naming/grouping within `k8s/`; structure of the build + bring-up scripts.

## Deferred Ideas

- Fault-scenario re-run on k8s (out of scope; proven on compose).
- Registry-based distribution / multi-node cluster.
- Overlay/Helm packaging for multiple environments.
