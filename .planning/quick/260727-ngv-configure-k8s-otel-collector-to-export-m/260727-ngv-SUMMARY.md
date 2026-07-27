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

## Item 4 — happy path + telemetry queries: PASS (run twice)

`pwsh -File scripts/phase-80-harness.ps1 -Scenario TEST-01 -SkipBringUp` → **exit 0**, `analyzer verdict exit = 0 (PASS)` on both runs. Report: `tests/BaseApi.Tests/bin/Release/net8.0/analyzer-reports/TEST-01.json`.

Run 2 was executed after a full port-forward reap-and-rebind cycle (and after killing the `MCP.Terminal` emitters, below), to confirm the export paths do not depend on port-forward identity.

| Gate | Run 1 (window 15:00:09Z) | Run 2 (window 15:38:17Z) |
|---|---|---|
| Harness exit / analyzer verdict | 0 / PASS | 0 / PASS |
| Drain conservation | `orch_consumed=210, proc_sent=210, gap=0` | `orch_consumed=190, proc_sent=190, gap=0` |
| ES in-window `processor` / `orchestrator` / `webapi` | 422 / 343 / 212 | 382 / 319 / 211 |
| ES doc delta | `803276 → 804253` = **+977** | `804253 → 805165` = **+912** |
| `up{job="otel-collector"}` | `1` (`instance=otel-collector:8889`) | `1` |
| 4/4 identities as `service_name` in-window | all present | all present |
| `sum(increase(orchestrator_messages_sent_total[15m]))` | 412.19 | 373.79 |
| `sum(increase(processor_messages_sent_total[15m]))` | 215.10 | 199.74 |
| health-route series reaching Prometheus | `0` (filter working) | `0` |
| **Overall** | **PASS** | **PASS** |

Artifacts: `otel-happypath.out`/`.err` (run 1), `otel-happypath-run2.out`/`.err` (run 2) — untracked run logs at repo root.

**`keeper` produced zero log docs on both runs and that is correct** — it is the recovery-path consumer and is near-silent on a no-fault run (39 docs/24h vs processor's 6794). Keeper's liveness is proven via metrics (`keeper_3.7.0` present in-window), not logs. A naive "all 4 services logged" criterion would fail every healthy happy path.

## Port-forward restart (before run 2)

Reaped all 8 recorded PIDs and rebound the set from `scripts/phase-80-up.ps1` STEP 6 (`baseapi 8080`, `prometheus 9090`, `elasticsearch 9200`, `rabbitmq-mgmt 15673:15672`, `redis 6380:6379`, `postgres 5433:5432`, `rabbitmq-amqp 5673:5672`, `otel-collector 4317:4317`), then verified each tunnel actually carries traffic rather than merely that the process started: 8080→200, 9090→200, 9200→`yellow` (normal single-node, unassigned replicas), 15673→200, TCP open on 6380/5433/5673/4317. `.k8s-portforward-pids` rewritten with the 8 live PIDs.

Note: `phase-80-up.ps1` uses `Write-Phase` (host stream) inside the `foreach` that builds `$pfProcs`. A replica of that block using `Write-Output` leaks strings into the pipeline and corrupts the PID file with blank entries — the reap filter (`Where-Object { $_ -match '\S' }`) tolerates it, but write the PID file from `Get-CimInstance Win32_Process` if in doubt.

## Notes for future work

- **ES field shape is lowercase** — `resource.attributes.service.name`, `body.text`, `severity_text`. Older Phase-11 docs claiming capitalised `Resource.`/`Body` are wrong; `mapping.mode: none` yields lowercase.
## Who else pushes into the k8s collector (investigated + partially remediated)

The `kubectl port-forward svc/otel-collector 4317:4317 --address 127.0.0.1` in the harness bring-up publishes the in-cluster collector at **the OTel SDK's default OTLP endpoint**. No `OTEL_*` variables are set at User or Machine scope, so any .NET+OpenTelemetry process on the host with a bare exporter lands in the cluster with zero configuration. Two emitters were found:

1. **`MCP.Terminal`** (incidental) — `C:\Users\UserL\Documents\augment-projects\TerminalApp\MCP.Terminal\publish\MCP.Terminal.exe`, 2 instances (PIDs 44428/46012, up since 2026-07-24). .NET 8, OTel SDK 1.12.0. **Metrics only — zero log docs in ES.** Its entire HTTP-client traffic was `POST http://localhost:4317` (4,064 per instance) — purely self-export, not talking to the stack. **Killed on request**; verified 0 processes, 0 TCP connections to `:4317`, no respawn after 7 min, and 0 live Prometheus series once the collector's default 5m `metric_expiration` drained its exporter cache (series flatlined at 238 through 15:30:40, then nothing). Will return if the Augment client respawns the MCP server — the durable fix is app-side (set `OTEL_EXPORTER_OTLP_ENDPOINT` or disable export there), not removing the 4317 forward, which the tests require.
2. **The host test suite** (deliberate) — `tests/BaseApi.Tests/Observability/Phase11WebAppFactory.cs:71`, `Orchestrator/CorrelationPropagationE2ETests.cs:339`, `Orchestrator/MetricsRoundTripE2ETests.cs:469` all pin `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317`; `Console/ConsoleTestHostFixture.cs:98` sets `Service:Name = "orchestrator-test"`. These are the `orchestrator-test` (901 docs) / `orchestrator-test_18.0.0` (10 docs) entries in ES.

**Open risk (not addressed — needs a decision):** the test-suite log docs carry `resource.attributes.Source = keeper` and `processor` — the same bucket values the analyzer aggregates on. A `dotnet test` run overlapping an analyzer window could inject docs under a real service's Source. It did **not** affect either run here (newest `orchestrator-test` doc is `12:41:36Z`, both windows open later; in-window aggregations returned only `processor`/`orchestrator`/`webapi` totalling exactly the ES delta, with no stray keeper bucket). But the harness does run `dotnet test` on the host during STEP C and STEP H, so the overlap risk is structural. Candidate fixes: give the tests a distinct `Source` value, or have the analyzer filter on `service.name` in addition to `Source`.

## Notes for future work

- **`api/v1/label/<name>/values?start=&end=` is block-coarse** — it returns values from any TSDB block overlapping the range, not a per-sample filter, so a dead emitter keeps appearing for a long time. `MCP.Terminal` still listed for "the last 5 minutes" hours after its last datapoint. Use `query_range` on `count({service_name="X"})` when you need the true last-sample time.
- **`timestamp(last_over_time(...))` does NOT give the underlying sample time** — `timestamp()` reports the evaluation timestamp, so it always returns "now". Use `query_range` instead.
