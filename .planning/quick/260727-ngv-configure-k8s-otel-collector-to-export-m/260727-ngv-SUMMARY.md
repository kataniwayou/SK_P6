---
quick_id: 260727-ngv
status: complete
date: 2026-07-27
commit: 418416a
files_modified:
  - k8s/02-configmaps.yaml
---

# Quick Task 260727-ngv — k8s OTel Collector export paths

## Headline

**Items 1 and 2 required no configuration work — both export paths were already built and functional.** This task was predominantly *verification*, and the verification passed end-to-end against the live `skp` cluster.

## What was found

| Export path | Mechanism | Where |
|---|---|---|
| metrics → Prometheus | **PULL.** Collector `prometheus` exporter on `0.0.0.0:8889` (`resource_to_telemetry_conversion: true`); Prometheus scrapes it via a hard-coded `static_configs` job `otel-collector` → `otel-collector:8889`, `metrics_path: /metrics`. Not a ServiceMonitor, not `kubernetes_sd`, not remote-write. | `k8s/02-configmaps.yaml` (`otel-collector-config`, `prometheus-config`), `k8s/20-otel-collector.yaml`, `k8s/21-prometheus.yaml` |
| logs → Elasticsearch | contrib `elasticsearch` exporter → `http://elasticsearch:9200`, `mapping.mode: none`, data stream `logs-generic.otel-default` | `k8s/02-configmaps.yaml` (`otel-collector-config`), `k8s/13-elasticsearch.yaml` |

The untracked `k8s/22-otel-collector-servicemonitor.yaml` is out of scope (user-stated) and is also absent from `k8s/kustomization.yaml`, so `apply -k` never touches it. Left untouched and still untracked.

## The one change made

`k8s/02-configmaps.yaml` — comment only, in the `prometheus-config` ConfigMap body. The trailing rationale claimed the `exported_job`/`exported_instance` duplicates were fixed upstream *"see the `resource/prom_identity` processor"*. **No such processor exists** — the collector's only processor is `filter/health_metrics`, and commit `0548d02` records those duplicates as knowingly not addressed. Replaced with the accurate wording already in the repo-root `prometheus.yml`; the two bodies are now byte-identical.

No YAML keys changed (`global`, `scrape_configs`, `job_name`, `targets`, `metrics_path` untouched). Prometheus was **not** restarted — it is ephemeral with no PVC, so a restart would wipe the TSDB. Verified: pod `startTime` still `2026-07-19T07:09:08Z`.

Commit: `418416a`.

## INCIDENT — `kubectl apply -k k8s/` reverted all 4 service images

Applying the manifests to sync the ConfigMap **also rolled all four services from `:tags-const-1544` (what was actually running) back to `:local`**, because the manifests pin `image: <svc>:local` while the live stack had been deployed via `kubectl set image` under a unique tag.

Caught within seconds; restored with `kubectl set image deployment/<svc> <svc>=<svc>:tags-const-1544 -n skp` for all four. All pods returned Running/Ready on the correct tag before the happy path was driven. Infra pods (elasticsearch, postgres, prometheus, rabbitmq, redis, otel-collector) were never restarted — ages preserved.

**Lesson:** `kubectl apply -k k8s/` is NOT safe as a ConfigMap-only sync whenever the live stack is running a `kubectl set image` tag. Either target the single object (`kubectl apply -f` / `kubectl create cm --dry-run | kubectl replace`) or re-pin the images immediately after.

Side effect: the baseapi `8080` port-forward died with its pod and was restarted (new PID 31660, `.k8s-portforward-pids` refreshed). Prometheus/ES forwards survived.

## Item 3 — all 4 services wired: CONFIRMED

Verified twice. Static — all 4 manifests carry `OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"` (4/4). Live — the same value read back off all 4 running Deployments.

| # | Workload | Prom `service_name` | ES `resource.attributes.Source` |
|---|---|---|---|
| 1 | `baseapi-service` | `sk-api_3.2.0` | `webapi` |
| 2 | `orchestrator` | `orchestrator_3.4.0` | `orchestrator` |
| 3 | `keeper` | `keeper_3.7.0` | *(near-silent by design)* |
| 4 | `processor-sample` | `processor-sample_3.5.0`, `unresolved_0.0.0` | `processor` |

Metrics use `{name}_{version}`, logs use the bare name — **different by design** (MLBL-01 / MLBL-04), set on separate per-provider `SetResourceBuilder` calls. Do not "fix".

## Item 4 — happy path + telemetry queries: PASS

`pwsh -File scripts/phase-80-harness.ps1 -Scenario TEST-01 -SkipBringUp` → **exit 0**, `analyzer verdict exit = 0 (PASS)`. Drain reached conservation (`orch_consumed=210, proc_sent=210, gap=0`). Report: `tests/BaseApi.Tests/bin/Release/net8.0/analyzer-reports/TEST-01.json`.

Window `2026-07-27T15:00:09Z` → `15:13:01Z`:

| Gate | Result |
|---|---|
| Logs in ES (in-window, by `resource.attributes.Source`) | `processor` 422, `orchestrator` 343, `webapi` 212 — **PASS** |
| `up{job="otel-collector"}` | `1` (`instance=otel-collector:8889`) — **PASS** |
| 4/4 identities as `service_name` in-window | all present — **PASS** |
| `sum(increase(orchestrator_messages_sent_total[15m]))` | `412.19` — **PASS** |
| `sum(increase(processor_messages_sent_total[15m]))` | `215.10` — **PASS** |
| health-route series reaching Prometheus | `0` (filter working) — **PASS** |
| ES doc delta | `803276 → 804253` = **+977** — **PASS** |

**`keeper` produced zero log docs and that is correct** — it is the recovery-path consumer and is near-silent on a no-fault run (39 docs/24h vs processor's 6794). Keeper's liveness is proven via metrics (`keeper_3.7.0` present in-window), not logs. A naive "all 4 services logged" criterion would fail every healthy happy path.

## Notes for future work

- **ES field shape is lowercase** — `resource.attributes.service.name`, `body.text`, `severity_text`. Older Phase-11 docs claiming capitalised `Resource.`/`Body` are wrong; `mapping.mode: none` yields lowercase.
- An unrelated `MCP.Terminal` `service_name` appears in Prometheus — some other local process is pushing OTLP into the collector. Harmless here, but it means the collector's OTLP receiver is not exclusive to the 4 services.
