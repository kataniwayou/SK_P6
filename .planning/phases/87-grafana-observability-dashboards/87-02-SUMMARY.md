---
phase: 87
plan: 02
subsystem: observability-deployment
tags: [grafana, kubernetes, kustomize, configmapgenerator, provisioning, dashboard-as-code]
requires:
  - "87-01 (k8s/dashboards/runtime.json and business.json must exist — they are the configMapGenerator's inputs)"
provides:
  - "k8s/23-grafana.yaml — Grafana Service + Deployment + grafana-datasources + grafana-dashboard-provider ConfigMaps"
  - "k8s/kustomization.yaml configMapGenerator — the repo's first generator, producing the unsuffixed grafana-dashboards ConfigMap"
  - "A live Grafana in skp: default read-only Prometheus datasource, both dashboards provisioned in folder SKP, no PVC"
  - "Port 3000 as the Grafana loopback forward, owned per-invocation (never in .k8s-portforward-pids)"
affects:
  - "87-05 (re-applies after Wave 2 to carry the final panel set into the ConfigMap; owns the panel-content and pod-recreate proofs)"
  - "87-06 (any dashboard JSON edit now reaches the cluster through this generator)"
tech-stack:
  added:
    - "grafana/grafana:12.3.9 (official Docker Hub org, patch-pinned)"
  patterns:
    - "kustomize configMapGenerator with disableNameSuffixHash: true, so a by-name Deployment volume ref survives every dashboard edit"
    - "whole-directory ConfigMap mounts at the provisioning LEAVES (inverting 21-prometheus.yaml's single-file subPath, same T-80-07 anti-shadowing intent)"
    - "emptyDir as a positive requirement statement (absence of persistence IS DASH-03)"
    - "throwaway port-forward whose PID lives in an in-memory variable only (T-87-11)"
key-files:
  created:
    - k8s/23-grafana.yaml
  modified:
    - k8s/kustomization.yaml
decisions:
  - "generatorOptions.labels omitted — verified against real `kubectl kustomize k8s/` output that the top-level `labels:` block (includeSelectors: false) already stamps part-of + managed-by onto the GENERATED ConfigMap, so adding them would be pure duplication."
  - "Header prose was reworded to avoid the literal strings `:latest`, `/var/lib/grafana/dashboards` and a second `grafana/grafana:12.3.9` — the plan's acceptance criteria are grep guards, and a comment that quotes the anti-pattern defeats the guard that forbids it."
  - "DASH-01/02/03 checkboxes deliberately left unticked — 87-05 re-delivers all three and is the last owning plan (same precedent 87-01 set)."
metrics:
  duration: ~50 min
  completed: 2026-07-27
  tasks: 3
  commits: 2
---

# Phase 87 Plan 02: Grafana Deployment + Dashboard Provisioning Summary

Grafana 12.3.9 now arrives through the same single `kubectl apply -k k8s/` as the rest of the stack, boots with the in-cluster Prometheus already its default read-only datasource and both repo dashboards already in folder SKP, and keeps zero persistent state — proven live with 13/13 assertions green.

## What Was Built

### `k8s/23-grafana.yaml` (four documents)

`Service` → `Deployment` → ConfigMap `grafana-datasources` → ConfigMap `grafana-dashboard-provider`, in that order, following `21-prometheus.yaml`'s house shape (boxed header banner, Service before Deployment, no `type:` key on the Service, pinned public image with no `imagePullPolicy`, memory-only `resources`).

Two things in this file have **no analog anywhere in `k8s/`** and are authored with explanatory comments:

| New to the repo | Value | Why |
|---|---|---|
| `securityContext` (pod-level) | `runAsUser: 472`, `runAsGroup: 0`, `fsGroup: 472` | The image already defaults to uid 472; pinning it means the container can never silently run as root (T-87-05) |
| `emptyDir` | `/var/lib/grafana` | The **absence** of persistence *is* DASH-03 — a pod recreate must rebuild every byte of Grafana state from the repo |

**The mount rule is an inversion, not a copy.** `21-prometheus.yaml` mounts a single file by `subPath` to avoid shadowing its sibling console libs (T-80-07). Grafana needs three *whole-directory* mounts, so it honours the same anti-shadowing intent by mounting at the **leaves** (`/etc/grafana/provisioning/datasources`, `/etc/grafana/provisioning/dashboards`) instead. Mounting over `/etc/grafana/provisioning` itself would erase the image's `alerting/`, `plugins/`, `access-control/` and `notifiers/` subdirectories. The dashboard JSONs land at `/etc/grafana/dashboards` — a *sibling* of the data dir, never nested inside the `emptyDir`.

Probes are the Phase-86 trio from `32-keeper.yaml`, all three on Grafana's single `/api/health` (startup 30×5s = 150 s budget against a measured ~9 s first-boot migration). `GF_PATHS_PROVISIONING` and `GF_INSTALL_PLUGINS` are both deliberately **absent**.

### `k8s/kustomization.yaml` — the repo's first `configMapGenerator`

```yaml
generatorOptions:
  disableNameSuffixHash: true

configMapGenerator:
  - name: grafana-dashboards
    files:
      - dashboards/runtime.json
      - dashboards/business.json
```

Plus `- 23-grafana.yaml` inserted between `21-prometheus.yaml` and `30-baseapi-service.yaml`, and the manifest count comment 13 → 14. `22-otel-collector-servicemonitor.yaml` remains deliberately absent (OCP-only CRD).

The load-bearing header comment asserted *"No templating, no patches, no configMapGenerator"* — this change made that literally false, so it was amended rather than left stale. It now states the one generator, why the hash-suffix opt-out is mandatory (the Deployment references `grafana-dashboards` **by name**), why `02-configmaps.yaml`'s hand-authored ConfigMaps are still *not* generated, and why the dashboards are real `.json` files: that is what keeps "the repo JSON is the single source of truth" literally true (DASH-03) and what makes the hermetic lint gate possible at all.

### `generatorOptions.labels` — verified redundant, as the plan asked

87-RESEARCH.md proposed adding it. Inspecting the actual `kubectl kustomize k8s/` output shows the top-level `labels:` block already reaches generated resources:

```yaml
kind: ConfigMap
metadata:
  labels:
    app.kubernetes.io/managed-by: kustomize
    app.kubernetes.io/part-of: skp
  name: grafana-dashboards        # <- no content-hash suffix
  namespace: skp
```

So it was **omitted**, and the reason is recorded inline so it is not "fixed" back in later.

## Live Verification Results (Task 3)

Applied to the Docker-Desktop `skp` cluster (context confirmed `docker-desktop`). `kubectl apply -k k8s/` created `service/grafana`, `deployment.apps/grafana` and all three ConfigMaps; `kubectl -n skp rollout status deployment/grafana --timeout=180s` exited 0; the pod is **1/1 Running, RESTARTS 0**.

Verified over a throwaway `kubectl -n skp port-forward svc/grafana 3000:3000 --address 127.0.0.1` whose PID lived in a shell variable only — `.k8s-portforward-pids` was never read or written (T-87-11), and the forward was torn down behind a recycled-PID guard (kill only if that PID is still a live `kubectl`).

**Responses, verbatim:**

```
GET /api/health
{"database":"ok","version":"12.3.9","commit":"1f48319059aae4ee4dddedb8c265f23fd008c19c"}

GET /api/datasources/uid/skp-prometheus
{"id":1,"uid":"skp-prometheus","orgId":1,"name":"Prometheus","type":"prometheus",
 "typeLogoUrl":"public/plugins/prometheus/img/prometheus_logo.svg","access":"proxy",
 "url":"http://prometheus:9090","user":"","database":"","basicAuth":false,"basicAuthUser":"",
 "withCredentials":false,"isDefault":true,"jsonData":{"httpMethod":"POST","timeInterval":"15s"},
 "secureJsonFields":{},"version":1,"readOnly":true,"apiVersion":""}

GET /api/datasources/uid/skp-prometheus/health
{"details":{"application":"Prometheus","features":{"rulerApiEnabled":false}},
 "message":"Successfully queried the Prometheus API.","status":"OK"}

GET /api/search?type=dash-db
[{"id":2,"uid":"skp-business","orgId":1,"title":"SKP Business / Pipeline",
  "uri":"db/skp-business-pipeline","url":"/d/skp-business/skp-business-pipeline","type":"dash-db",
  "tags":["business","skp"],"folderId":1,"folderUid":"dftedqenm97gge","folderTitle":"SKP",
  "folderUrl":"/dashboards/f/dftedqenm97gge/skp","isDeleted":false},
 {"id":3,"uid":"skp-runtime","orgId":1,"title":"SKP Runtime Instrumentation",
  "uri":"db/skp-runtime-instrumentation","url":"/d/skp-runtime/skp-runtime-instrumentation","type":"dash-db",
  "tags":["runtime","skp"],"folderId":1,"folderUid":"dftedqenm97gge","folderTitle":"SKP",
  "folderUrl":"/dashboards/f/dftedqenm97gge/skp","isDeleted":false}]

GET /api/dashboards/uid/skp-runtime  (.meta)
{"type":"db","canSave":true,"canEdit":true,"canAdmin":true,"canStar":true,"canDelete":true,
 "slug":"skp-runtime-instrumentation","url":"/d/skp-runtime/skp-runtime-instrumentation",
 "created":"2026-07-27T18:51:25Z","updated":"2026-07-27T18:51:25Z","updatedBy":"Anonymous",
 "createdBy":"Anonymous","version":1,"hasAcl":false,"isFolder":false,"apiVersion":"v0alpha1",
 "folderId":1,"folderUid":"dftedqenm97gge","folderTitle":"SKP",
 "folderUrl":"/dashboards/f/dftedqenm97gge/skp",
 "provisioned":true,"provisionedExternalId":"runtime.json", ...}
```

**13/13 assertions PASS:**

| Assertion | Result |
|---|---|
| `health.database` == `ok` | PASS |
| datasource `isDefault` (DASH-02) | `true` |
| datasource `readOnly` (T-87-03) | `true` |
| datasource `url` | `http://prometheus:9090` |
| `/api/datasources/uid/skp-prometheus/health` `status` | `OK` |
| `/api/search` uid list | `skp-business,skp-runtime` |
| `skp-runtime` `folderTitle` | `SKP` |
| `skp-business` `folderTitle` | `SKP` |
| `skp-runtime` `meta.provisioned` | `true` |
| `skp-runtime` `meta.provisionedExternalId` | `runtime.json` |
| grafana PVCs in `skp` (DASH-03) | **0** |

Note `meta.canSave: true` is Grafana reporting the *user's* permission, not the dashboard's writability — the provider's `allowUiUpdates: false` is what rejects the save, and that rejection is 87-05's live assertion, not this plan's.

**Other gates:**

| Check | Result |
|---|---|
| `kubectl kustomize k8s/` | exit **0** |
| `kubectl apply -k k8s/ --dry-run=server` (T-80-14 whole-stack gate) | exit **0** |
| `pwsh -File scripts/phase-87-dashboard-lint.ps1` (all 23 rules, both files) | exit **0** |
| rendered `grafana-storage` volume | `{"emptyDir":{},"name":"grafana-storage"}` |
| `git status --porcelain .k8s-portforward-pids scripts/phase-80-up.ps1 k8s/02-configmaps.yaml k8s/21-prometheus.yaml k8s/dashboards/ scripts/phase-87-dashboard-lint.ps1` | empty |
| port 3000 listeners after teardown | 0 |

Because the orchestrator reordered Wave 2 so this plan ran **last**, `runtime.json` (17 panels) and `business.json` (14 panels) were already settled — the torn-read hazard the plan's Task 3 SCOPE paragraph anticipated could not occur, and did not. Task 3's assertions were kept exactly as written: **no panel count and no panel title is asserted anywhere**, since panel content is 87-05's proof.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `kubectl apply -k k8s/` reverted two live deployments to stale `:local` images and broke them**

- **Found during:** Task 3, immediately after the apply.
- **Issue:** `orchestrator` and `processor-sample` were running on the tag `:tags-const-1544`, applied out-of-band by an earlier phase via `kubectl set image` (the documented workaround for Docker-Desktop serving stale `:local` images). The manifests pin `:local`, so `apply -k` rolled both back. The `:local` images are older code: the new `processor-sample` pod failed its startup probe with **HTTP 503** and restarted twice; the new `orchestrator` pod failed readiness with **503** for 4+ minutes. Both rollouts timed out. Old ReplicaSets kept the stack Available throughout, so nothing was lost.
- **Fix:** recovered the previously-live tag from the old ReplicaSets (`kubectl get rs -l app=<d> -o jsonpath=...` showed the ready RS on `:tags-const-1544`) and restored both with `kubectl set image`, returning the cluster to its exact pre-apply state. Both rollouts then completed cleanly.
- **Files modified:** none — this is live cluster state only, no repo file was involved.
- **Commit:** n/a (no file change).
- **Root cause is NOT this plan's manifests.** It is pre-existing drift between the checked-in image tags and what the cluster was actually running. Any `kubectl apply -k k8s/` by anyone would have done the same thing. **This is a standing hazard for 87-05, which re-applies:** check `kubectl -n skp get rs -l app=orchestrator` and `-l app=processor-sample` for a non-`:local` tag *before* applying, and restore it after.

Final state, all pods `RESTARTS 0`:

```
baseapi-service  1/1   grafana  1/1   keeper  2/2
orchestrator     3/3   otel-collector 1/1   processor-sample 2/2   prometheus 1/1
elasticsearch/postgres/rabbitmq/redis  1/1 each
```

**2. [Rule 1 - Bug] Header comments defeated three of the plan's own grep guards**

- **Found during:** Task 1 verification.
- **Issue:** the plan requires the header banner to explain the image pin, the `:latest` prohibition and the Pitfall-3 anti-pattern — but its acceptance criteria are `Select-String` guards asserting `grafana/grafana:12.3.9` appears *exactly once*, and that `:latest` and `/var/lib/grafana/dashboards` appear *zero* times. Quoting an anti-pattern in prose makes the guard forbidding it fire.
- **Fix:** reworded the prose to convey the same rules without the literal tokens ("patch-pinned at 12.3.9", "never a floating tag", "NEVER placed in a subdirectory of /var/lib/grafana"). The rationale survives intact; the guards now pass.
- **Files modified:** `k8s/23-grafana.yaml`
- **Commit:** `0a0a0d8`

### Verification-Script Bugs (scratchpad only, no repo file)

Three defects in the throwaway assertion script, worth recording because the third is a repeat of a trap 87-01 already documented:

1. `@($search.uid)` — StrictMode `Latest` forbids member enumeration over an array; projected with `ForEach-Object` instead.
2. Inline `@(...)[0].folderTitle` inside a function argument returned `System.Object[]`; assigned to a variable first.
3. **The real one:** `$search = @(Invoke-RestMethod ...)` produced a **1-element array-of-array**. `Invoke-RestMethod` emits a JSON array as a *single* pipeline item, so `@()` wraps rather than enumerates. `Where-Object` then matched the whole inner array (its `.uid` member-enumeration was non-empty, hence truthy) and every field came back as a 2-element array. Fix: assign first, **then** wrap — `$raw = Invoke-RestMethod ...; $search = @($raw)`. This is the same array-of-array class of bug that plan 87-01 hit in `Get-S9Violations` and banned the `,@(...)` idiom for. **Any later plan calling `/api/search` must assign before wrapping.**

None of these ever produced a false PASS — each surfaced as a loud FAIL or a thrown error, and the raw JSON captured above independently corroborates every corrected assertion.

### Requirement Checkboxes Deliberately NOT Marked Complete

The frontmatter lists `DASH-01`, `DASH-02`, `DASH-03`, but **87-05's frontmatter re-delivers all three** (`requirements: [VER-01, VER-02, DASH-01, DASH-02, DASH-03, ...]`) and `REQUIREMENTS.md`'s traceability table is phase-scoped. DASH-03 in particular is only half-proven here: the no-PVC fact and the `allowUiUpdates: false` mechanism are in place and asserted, but its two behavioural claims — *a pod restart reproduces the identical dashboards* (VER-02) and *any UI-side edit is disposable* (the `Cannot save provisioned dashboard` rejection) — are 87-05's live assertions. Following the precedent 87-01 set, the boxes flip when the last owning plan lands.

### Pre-Existing Working-Tree Condition (out of scope, NOT fixed)

`git status --porcelain src/` is not empty: `src/Keeper/Recovery/ReinjectConsumer.cs` modified, `src/BaseApi.Service/Properties/launchSettings.json` untracked. Both predate this phase and were already recorded in 87-01's summary. This plan read and wrote **zero** files under `src/`.

## Authentication Gates

None. Grafana's `admin:admin` is provisioned by the manifest itself, so the basic-auth header was built inline — no human step.

## Known Stubs

None. Both manifests are complete and live-proven.

## Threat Flags

None. The plan's `<threat_model>` anticipated every surface this plan introduced; no new endpoint, auth path, file access pattern or trust-boundary schema change appeared beyond T-87-01…T-87-SC.

Threat mitigations actually landed and verified: T-87-01 (`--address 127.0.0.1` on the forward), T-87-03 (`readOnly: true` confirmed live), T-87-04 (both analytics vars false), T-87-05 (`runAsUser: 472`), T-87-06 (no `GF_INSTALL_PLUGINS`), T-87-07 (anonymous role Viewer, sign-up false), T-87-08 (`--dry-run=server` exit 0), T-87-11 (PID file untouched, confirmed by an empty `git status`).

## Task Commits

| Task | Name | Commit | Files |
|---|---|---|---|
| 1 | Grafana manifest + provisioning ConfigMaps | `0a0a0d8` | `k8s/23-grafana.yaml` |
| 2 | Wire resource entry + configMapGenerator | `52d571a` | `k8s/kustomization.yaml` |
| 3 | Live apply + contract-level smoke proof | n/a — changes no files by design; it proves the two commits above | — |

## Self-Check: PASSED

- `k8s/23-grafana.yaml` — FOUND
- `k8s/kustomization.yaml` — FOUND
- `.planning/phases/87-grafana-observability-dashboards/87-02-SUMMARY.md` — FOUND
- commit `0a0a0d8` — FOUND
- commit `52d571a` — FOUND
