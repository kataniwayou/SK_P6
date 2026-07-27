---
quick_id: 260727-ngv
type: execute
autonomous: false
files_modified:
  - k8s/02-configmaps.yaml
must_haves:
  truths:
    - "The k8s otel-collector exports metrics to the k8s Prometheus via the pull path (collector /metrics on :8889, scraped by Prometheus static_configs job otel-collector)"
    - "The k8s otel-collector exports logs to the k8s Elasticsearch via the contrib elasticsearch exporter into data stream logs-generic.otel-default"
    - "All 4 services (baseapi-service, orchestrator, keeper, processor-sample) send OTLP to http://otel-collector:4317"
    - "A happy-path run produces metrics queryable in Prometheus and logs queryable in Elasticsearch, both scoped to the run window"
    - "The repo's two copies of the Prometheus scrape config carry the same, factually-correct rationale"
  artifacts:
    - path: "k8s/02-configmaps.yaml"
      provides: "otel-collector-config + prometheus-config ConfigMap bodies"
      contains: "otel-collector:8889"
  key_links:
    - from: "k8s/02-configmaps.yaml (otel-collector-config)"
      to: "http://elasticsearch:9200"
      via: "elasticsearch exporter in the logs pipeline"
      pattern: "exporters: \\[elasticsearch\\]"
    - from: "k8s/02-configmaps.yaml (prometheus-config)"
      to: "otel-collector:8889"
      via: "static_configs scrape target"
      pattern: "otel-collector:8889"
---

<objective>
Confirm and lock in the k8s OTel Collector's two export paths (metrics → Prometheus, logs → Elasticsearch), verify all 4 services are wired to the collector, then prove it end-to-end with a happy-path run and explicit ES + PromQL queries.

Purpose: close the loop on the k8s telemetry backbone with live evidence, and remove the one factually-wrong comment that would mislead future work on the metrics label shape.
Output: a comment-accuracy fix to `k8s/02-configmaps.yaml`, plus a live PASS/FAIL verdict on both export paths.
</objective>

<investigation_findings>
**Read before planning — this is the ground truth, verified against the repo AND the live `skp` cluster on 2026-07-27. Do not re-derive it.**

### Does the collector exist in k8s/? YES — and both export paths are ALREADY functional.

This task is **predominantly verification, not construction.** Nothing about the two export paths is missing. Any executor tempted to "add" an exporter, a receiver, or a scrape job is about to regress a working system.

| Piece | Where | State |
|---|---|---|
| Collector Deployment + ClusterIP Service | `k8s/20-otel-collector.yaml` | Exists. `otel/opentelemetry-collector-contrib:0.152.0`, ports 4317 (OTLP gRPC), 4318 (OTLP HTTP), 8889 (Prom scrape), 13133 (health). |
| Collector config body | `k8s/02-configmaps.yaml` → ConfigMap `otel-collector-config`, data key `otel-collector-config.yaml` | Exists. Mounted at `/etc/otel-collector-config.yaml` via single-file `subPath`. |
| Prometheus Deployment + Service | `k8s/21-prometheus.yaml` | Exists. `prom/prometheus:v3.11.3`, port 9090. |
| Prometheus scrape config | `k8s/02-configmaps.yaml` → ConfigMap `prometheus-config`, data key `prometheus.yml` | Exists. Mounted at `/etc/prometheus/prometheus.yml` via `subPath`. |
| Elasticsearch | `k8s/13-elasticsearch.yaml` | Exists. StatefulSet `elasticsearch-0`, headless Service on 9200, 2Gi PVC. |

### Export path 1 — metrics → Prometheus: WIRED. Nothing missing.

**How the k8s Prometheus discovers targets: a single hard-coded `static_configs` entry.** Not a Prometheus-Operator ServiceMonitor, not `kubernetes_sd_configs`, not a remote-write receiver. From the `prometheus-config` ConfigMap:

```yaml
scrape_configs:
  - job_name: 'otel-collector'
    static_configs:
      - targets:
          - 'otel-collector:8889'
    metrics_path: '/metrics'
```

The direction is **PULL**. The collector does not push to Prometheus; it exposes a scrape surface. The matching collector side:

```yaml
exporters:
  prometheus:
    endpoint: 0.0.0.0:8889
    resource_to_telemetry_conversion:
      enabled: true      # makes service.name become the service_name label — load-bearing
    send_timestamps: true
service:
  pipelines:
    metrics:
      receivers: [otlp]
      processors: [filter/health_metrics]   # drops /health/* datapoints before egress
      exporters: [prometheus]
```

`otel-collector:8889` resolves via the ClusterIP Service, whose port `8889` is named `prom`. Confirmed live: `up{job="otel-collector"}` returns `1` with `instance="otel-collector:8889"`.

Note: `k8s/22-otel-collector-servicemonitor.yaml` is untracked AND is not listed in `k8s/kustomization.yaml`, so `kubectl apply -k k8s/` never applies it. It is out of scope and must be left alone.

### Export path 2 — logs → Elasticsearch: WIRED. Nothing missing.

```yaml
exporters:
  elasticsearch:
    endpoints:
      - http://elasticsearch:9200
    mapping:
      mode: none          # preserves OTLP raw field structure
service:
  pipelines:
    logs:
      receivers: [otlp]
      exporters: [elasticsearch]
```

Confirmed live: data stream `logs-generic.otel-default`, backing index `.ds-logs-generic.otel-default-2026.07.19-000001`, **803,080 docs**.

**Actual ES field shape** (verified by reading a real doc — `mapping.mode: none` yields *lowercase* `resource`/`body`, not the capitalised `Resource`/`Body` some older planning docs claim):

```json
{
  "@timestamp": "1785156827055.152600",
  "resource": { "attributes": { "service.name": "orchestrator", "Source": "orchestrator" } },
  "severity_text": "Information",
  "body": { "text": "Stop drain for WorkflowId=abb67167-..." }
}
```

### The 4 services and how wiring is verified

| # | k8s workload | Manifest | `Service:Name` (appsettings) | `Source` attr | Prom `service_name` | ES `resource.attributes.service.name` |
|---|---|---|---|---|---|---|
| 1 | `baseapi-service` | `k8s/30-baseapi-service.yaml` | `sk-api` | `webapi` | `sk-api_3.2.0` | `sk-api` |
| 2 | `orchestrator` | `k8s/31-orchestrator.yaml` | `orchestrator` | `orchestrator` | `orchestrator_3.4.0` | `orchestrator` |
| 3 | `keeper` | `k8s/32-keeper.yaml` | `keeper` | `keeper` | `keeper_3.7.0` | `keeper` |
| 4 | `processor-sample` | `k8s/33-processor-sample.yaml` | `unresolved` | `processor` | `sample-proc-<hash>_1.0.0` (per-identity) | `unresolved` |

**Wiring is verified two ways:**
1. **Static** — every one of the 4 manifests sets `OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"`. Verified: 4/4 present.
2. **Live** — each service's identity appears as a `service_name` label in Prometheus and/or a `resource.attributes.Source` bucket in ES. Both confirmed live.

**Metrics vs logs service.name differ BY DESIGN — do not "fix" this.** Metrics use `{name}_{version}` (MLBL-01, so one Prom label carries the version); logs use the bare name plus a standalone `service.version` (MLBL-04). Set on *separate* per-provider `SetResourceBuilder` calls in `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` — a shared `ConfigureResource` would leak the versioned name onto logs and break the ES query contract.

**Keeper is legitimately near-silent on a no-fault run.** Over the last 24h ES holds `processor`=6794, `webapi`=5155, `orchestrator`=4873, but `keeper`=**39**. Keeper is the recovery-path consumer; a clean happy path may produce *zero* keeper log records. **Keeper's happy-path liveness proof is METRICS, not logs** (it emits runtime instrumentation continuously). Task 3's PASS criteria reflect this — do not fail the run because keeper logged nothing.

### The one real defect found

`k8s/02-configmaps.yaml` and the repo-root `prometheus.yml` carry the same scrape config but a **drifted trailing comment**. The ConfigMap copy claims the `exported_job`/`exported_instance` duplicates are "fixed UPSTREAM, in the collector's metrics pipeline (see the `resource/prom_identity` processor)".

**There is no `resource/prom_identity` processor.** The collector's only processor is `filter/health_metrics`. Commit `0548d02` states plainly that `exported_job`/`exported_instance` are **NOT addressed**. The ConfigMap comment therefore points a future reader at a non-existent component and contradicts the decision it is supposed to record. The root `prometheus.yml` wording is the accurate one.

### Harness

- **Happy path:** `scripts/phase-80-harness.ps1`, scenario **TEST-01** (the no-fault baseline; default). Steps A→H: bring-up, reset, seed, activate, drive, drain, analyze. The analyzer verdict IS the harness exit code (0=PASS, 1=FAIL, 2=INCONCLUSIVE).
- **Bring-up:** `scripts/phase-80-up.ps1` — `kubectl apply -k k8s/`, phased `kubectl rollout status`, then **8 loopback port-forwards** (all `--address 127.0.0.1`), PIDs written to `.k8s-portforward-pids`:
  `8080`→baseapi · `9090`→prometheus · `9200`→elasticsearch · `15673`→rabbitmq-mgmt · `6380`→redis · `5433`→postgres · `5673`→rabbitmq-amqp · `4317`→otel-collector
- **`-SkipBringUp`** skips STEP A0 (build) + STEP A (up + port-forwards) *and* the STEP Z port-forward teardown — for reusing an already-up stack.
- The harness header mentions `docker compose down`; that is **legacy text**. This stack is Docker-Desktop k8s only.

### Current live state (2026-07-27)

All 12 pods `Running`: baseapi-service, elasticsearch-0, keeper ×2, orchestrator, otel-collector (8d), postgres-0, processor-sample ×2, prometheus (8d), rabbitmq-0, redis-0. The in-cluster `otel-collector-config` ConfigMap matches the repo byte-for-byte. Port-forwards on 9090 and 9200 are already live.
</investigation_findings>

<context>
@k8s/02-configmaps.yaml
@k8s/20-otel-collector.yaml
@k8s/21-prometheus.yaml
@prometheus.yml
@scripts/phase-80-harness.ps1
</context>

<tasks>

<task type="auto">
  <name>Task 1: Correct the drifted scrape-config rationale in the prometheus-config ConfigMap</name>
  <files>k8s/02-configmaps.yaml</files>
  <action>
The trailing comment block in the `prometheus-config` ConfigMap (`data["prometheus.yml"]`, the last ~7 lines of the file) references a `resource/prom_identity` processor that does not exist in the collector config, and asserts the `exported_job`/`exported_instance` duplicates are fixed upstream when commit 0548d02 records that they are deliberately NOT fixed.

Replace that block with the accurate wording already present in the repo-root `prometheus.yml` (its final comment paragraph), preserving the ConfigMap's 4-space YAML block-scalar indentation. The corrected text must:
  - keep the "Prometheus is ORG-OWNED / no honor_labels / no metric_relabel_configs" constraint intact (that is the load-bearing decision);
  - drop the `resource/prom_identity` reference entirely;
  - state that anything needing to be dropped or renamed is handled upstream in the collector or the SDK resource, without claiming it already has been.

This is a COMMENT-ONLY change. Do not touch `global:`, `scrape_configs:`, `job_name`, `targets`, or `metrics_path`. Do not touch the `otel-collector-config` ConfigMap. Do not touch `k8s/22-otel-collector-servicemonitor.yaml`.

Then make the cluster object match the repo:

    kubectl apply -k k8s/

DO NOT restart the prometheus pod. Two reasons: (a) the change is comment-only, so there is no behaviour to pick up; (b) prometheus is ephemeral with NO PVC (D-13) — a restart WIPES the entire TSDB and destroys all metric history. The `subPath` mount means a running pod keeps its current file either way, which is fine here.
  </action>
  <verify>
    <automated>
# Bash tool (POSIX). Gate 1: the phantom processor reference is gone from the whole repo.
test "$(grep -rc 'resource/prom_identity' k8s/02-configmaps.yaml prometheus.yml | awk -F: '{s+=$2} END {print s}')" = "0"

# Gate 2: the two scrape configs no longer drift. Extract the ConfigMap body, strip the
# data-key line and the 4-space block indent, and diff against the root canonical.
sed -n '/^  prometheus\.yml: |$/,$p' k8s/02-configmaps.yaml | tail -n +2 | sed 's/^    //' | diff - prometheus.yml

# Gate 3: the functional scrape lines survived untouched (comment-stripped counts, so
# prose in the header can never satisfy these).
grep -v '^\s*#' k8s/02-configmaps.yaml | grep -c "otel-collector:8889"   # -> 1
grep -v '^\s*#' k8s/02-configmaps.yaml | grep -c "job_name: 'otel-collector'"   # -> 1

# Gate 4: cluster object matches repo.
kubectl get cm prometheus-config -n skp -o jsonpath='{.data.prometheus\.yml}' | diff - prometheus.yml
    </automated>
  </verify>
  <done>All four gates pass: no `resource/prom_identity` anywhere, the ConfigMap body is byte-identical to root `prometheus.yml`, both functional scrape lines are intact, and the in-cluster ConfigMap matches. Prometheus pod age is unchanged (not restarted).</done>
</task>

<task type="auto">
  <name>Task 2: Assert both export paths and all 4 services are wired — static + live, before the run</name>
  <files>(no files modified — verification gate only)</files>
  <action>
Prove the wiring is intact BEFORE driving traffic, so a Task 3 failure can be attributed to the run rather than to configuration. This is a read-only gate: change nothing.

Establish port-forwards first if they are not already live. Check with `Get-Content .k8s-portforward-pids` and confirm the PIDs are live `kubectl` processes; if not, start the two this task needs (PowerShell):

    Start-Process kubectl -PassThru -WindowStyle Hidden -ArgumentList @('port-forward','svc/prometheus','9090:9090','-n','skp','--address','127.0.0.1')
    Start-Process kubectl -PassThru -WindowStyle Hidden -ArgumentList @('port-forward','svc/elasticsearch','9200:9200','-n','skp','--address','127.0.0.1')

Then assert, in order:
  1. All 4 service manifests carry `OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"`.
  2. The collector's logs pipeline exports to `elasticsearch`; its metrics pipeline exports to `prometheus`.
  3. Prometheus's scrape of the collector is UP (the live metrics path).
  4. The ES data stream exists and is accepting docs (the live logs path).

Record the ES doc count and the Prometheus `service_name` label set now — Task 3 compares against these as a baseline.
  </action>
  <verify>
    <automated>
# --- Static gates (Bash tool, POSIX) ---
# 4/4 services point at the collector.
grep -l 'OTEL_EXPORTER_OTLP_ENDPOINT' k8s/30-baseapi-service.yaml k8s/31-orchestrator.yaml k8s/32-keeper.yaml k8s/33-processor-sample.yaml | wc -l   # -> 4
grep -h -A1 'OTEL_EXPORTER_OTLP_ENDPOINT' k8s/3*.yaml | grep -c 'http://otel-collector:4317'   # -> 4

# Both pipelines declare their exporter (comment-stripped).
grep -v '^\s*#' k8s/02-configmaps.yaml | grep -c 'exporters: \[elasticsearch\]'   # -> 1
grep -v '^\s*#' k8s/02-configmaps.yaml | grep -c 'exporters: \[prometheus\]'      # -> 1

# --- Live gates (PowerShell) ---
# Metrics path: the scrape target must be UP.
(Invoke-RestMethod 'http://localhost:9090/api/v1/query?query=up%7Bjob%3D%22otel-collector%22%7D').data.result[0].value[1]
#   PASS = "1"

# All 4 identities present as Prom service_name labels.
$svc = (Invoke-RestMethod 'http://localhost:9090/api/v1/label/service_name/values').data
@('sk-api_3.2.0','orchestrator_3.4.0','keeper_3.7.0') | ForEach-Object { "$_ = $($svc -contains $_)" }
($svc | Where-Object { $_ -like 'sample-proc-*' -or $_ -eq 'unresolved_0.0.0' }).Count -gt 0
#   PASS = the three exact names are True AND the processor count is > 0

# Logs path: data stream exists and is non-empty. Record the baseline count.
$esBaseline = (Invoke-RestMethod 'http://localhost:9200/logs-generic.otel-default/_count').count
"ES baseline docs = $esBaseline"
#   PASS = the call succeeds (data stream exists) and $esBaseline -gt 0
    </automated>
  </verify>
  <done>4/4 manifests point at `http://otel-collector:4317`; both pipelines declare their exporter; `up{job="otel-collector"}` is 1; all 4 service identities resolve as Prom `service_name` labels; the `logs-generic.otel-default` data stream answers with a non-zero baseline doc count (recorded for Task 3).</done>
</task>

<task type="checkpoint:human-verify" gate="blocking">
  <what-built>
Task 1 corrected the drifted scrape-config rationale and synced the ConfigMap to the cluster. Task 2 proved, statically and live, that all 4 services target `http://otel-collector:4317` and that both collector export paths are declared and healthy.

This final step drives real traffic through the stack and proves telemetry produced by that traffic actually LANDS in both backends, scoped to the run window.
  </what-built>
  <how-to-verify>
Run every command below in PowerShell from the repo root. A single happy-path run is short — run it in the FOREGROUND (only the ~2h sweeps need detaching).

**Step 1 — pin the window start (UTC, before any traffic).**

    $windowStart = [DateTimeOffset]::UtcNow
    $esBefore = (Invoke-RestMethod 'http://localhost:9200/logs-generic.otel-default/_count').count
    "windowStart = $($windowStart.ToString('o'))   esBefore = $esBefore"

**Step 2 — run the happy path (TEST-01, the no-fault baseline).**

The stack is already up with port-forwards live, so use `-SkipBringUp` (this also leaves the forwards up afterwards, which the queries below need):

    pwsh -File scripts/phase-80-harness.ps1 -Scenario TEST-01 -SkipBringUp

PASS = the harness exits **0** and prints `analyzer verdict exit = 0 (PASS ...)`.
(1 = FAIL, 2 = INCONCLUSIVE — both are a FAIL for this task.)

**Step 3 — pin the window end and let one scrape interval elapse.**

Prometheus scrapes every 15s. Wait before querying or you will read a stale sample:

    $windowEnd = [DateTimeOffset]::UtcNow
    Start-Sleep -Seconds 20

**Step 4 — VERIFY LOGS LANDED IN ELASTICSEARCH.**

Exact query — counts log docs per emitting service within the run window:

    $body = @{
      size  = 0
      query = @{ range = @{ '@timestamp' = @{ gte = $windowStart.ToString('o') } } }
      aggs  = @{ src = @{ terms = @{ field = 'resource.attributes.Source'; size = 10 } } }
    } | ConvertTo-Json -Depth 10

    $es = Invoke-RestMethod -Method Post -Uri 'http://localhost:9200/logs-generic.otel-default/_search' `
                            -ContentType 'application/json' -Body $body
    $es.aggregations.src.buckets | Format-Table key, doc_count

**PASS for logs** = the buckets include **`webapi`**, **`orchestrator`**, and **`processor`**, each with `doc_count` > 0.

**`keeper` is NOT required here and its absence is NOT a failure.** Keeper is the recovery-path consumer and is near-silent on a no-fault run (39 docs in the last 24h vs 6794 for processor). Keeper is proven live via metrics in Step 5 instead.

**Step 5 — VERIFY METRICS LANDED IN PROMETHEUS.**

5a. Scrape target still healthy:

    (Invoke-RestMethod 'http://localhost:9090/api/v1/query?query=up%7Bjob%3D%22otel-collector%22%7D').data.result[0].value[1]

PASS = `1`.

5b. All 4 services reported metrics *inside the run window* (this is keeper's proof):

    $s = $windowStart.ToUnixTimeSeconds(); $e = $windowEnd.ToUnixTimeSeconds()
    $names = (Invoke-RestMethod "http://localhost:9090/api/v1/label/service_name/values?start=$s&end=$e").data
    $names

PASS = the returned set contains **`sk-api_3.2.0`**, **`orchestrator_3.4.0`**, **`keeper_3.7.0`**, and at least one processor identity (`sample-proc-*_1.0.0` or `unresolved_0.0.0`).

5c. The business counters actually advanced during the run — proves *fresh* metrics, not leftovers:

    $q = 'sum(increase(orchestrator_messages_sent_total[10m]))'
    (Invoke-RestMethod "http://localhost:9090/api/v1/query?query=$([uri]::EscapeDataString($q))").data.result[0].value[1]

    $q2 = 'sum(increase(processor_messages_sent_total[10m]))'
    (Invoke-RestMethod "http://localhost:9090/api/v1/query?query=$([uri]::EscapeDataString($q2))").data.result[0].value[1]

PASS = both values are > 0.

5d. The health-route filter is still doing its job (guards the `filter/health_metrics` processor):

    $q3 = 'count(http_server_request_duration_seconds_count{http_route=~"/health/.*"})'
    (Invoke-RestMethod "http://localhost:9090/api/v1/query?query=$([uri]::EscapeDataString($q3))").data.result.Count

PASS = `0` (empty result — no health-route samples ever reach Prometheus).

**Step 6 — confirm ES grew.**

    $esAfter = (Invoke-RestMethod 'http://localhost:9200/logs-generic.otel-default/_count').count
    "esBefore = $esBefore   esAfter = $esAfter   delta = $($esAfter - $esBefore)"

PASS = `delta` > 0.

---

**OVERALL PASS** = harness exit 0, AND logs show `webapi`+`orchestrator`+`processor` in-window, AND all 4 identities report metrics in-window, AND both business counters increased, AND the health filter yields 0, AND the ES delta is positive.

**If the harness exits non-zero:** that is a stack/run failure, not a telemetry-wiring failure — Task 2 already proved the wiring. Report the analyzer verdict and the report path (`analyzer-reports/TEST-01.json`) rather than editing collector config.

**If logs are missing but metrics are present:** check the collector for ES export drops — `kubectl logs deploy/otel-collector -n skp --tail=100 | Select-String "Dropping data"`. A restarting `elasticsearch-0` (OOM, exit 137) makes the exporter drop in-flight batches; check `kubectl get pod elasticsearch-0 -n skp` RESTARTS.
  </how-to-verify>
  <resume-signal>Type "approved" with the harness exit code and the ES bucket / Prom label results, or paste the failing output.</resume-signal>
</task>

</tasks>

<success_criteria>
- `k8s/02-configmaps.yaml` no longer references the non-existent `resource/prom_identity` processor; its `prometheus.yml` body is byte-identical to the repo-root canonical.
- The in-cluster `prometheus-config` ConfigMap matches the repo; the prometheus pod was NOT restarted (TSDB intact).
- All 4 services (baseapi-service, orchestrator, keeper, processor-sample) are confirmed wired to `http://otel-collector:4317` statically and live.
- Metrics path proven: `up{job="otel-collector"}` is 1, all 4 identities appear as `service_name` labels in the run window, and `orchestrator_messages_sent_total` / `processor_messages_sent_total` increased.
- Logs path proven: `webapi`, `orchestrator`, and `processor` all logged into `logs-generic.otel-default` within the run window; total doc count increased.
- `scripts/phase-80-harness.ps1 -Scenario TEST-01 -SkipBringUp` exits 0.
- `k8s/22-otel-collector-servicemonitor.yaml` is untouched and still untracked.
</success_criteria>

<output>
Commit Task 1 atomically (config change only, before the live run):

    git add k8s/02-configmaps.yaml
    git commit -m "docs(k8s): drop the phantom resource/prom_identity reference from prometheus-config"

Do NOT stage `k8s/22-otel-collector-servicemonitor.yaml`, the `*.out`/`*.err`/`*.pid` run artifacts, or any regenerated `analyzer-reports/*.json`.
</output>
