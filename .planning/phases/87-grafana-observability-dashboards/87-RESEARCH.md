# Phase 87: Grafana Observability Dashboards - Research

**Researched:** 2026-07-27
**Domain:** Grafana deployment + provisioning on k8s; Prometheus/OTel metric inventory; dashboard-as-code (classic JSON)
**Confidence:** HIGH (the metric inventory and Grafana provisioning behavior were verified LIVE against the running `skp` cluster and against real Grafana 12.3.9 + 13.1.1 containers this session)

## Summary

This phase does not need to build anything novel. Grafana file-provisioning is a solved, well-documented pattern, and the stack already exports everything the dropdowns need. The research effort therefore went almost entirely into **ground-truthing the metric inventory**, and that is where the surprises are.

The single most important finding: **the requirements' runtime metric expectations are partly wrong, and several named panels have no backing series.** The stack runs `OpenTelemetry.Instrumentation.Runtime` **1.15.0 on net8.0**, which emits the *legacy* `process.runtime.dotnet.*` names → `process_runtime_dotnet_*` in Prometheus. The modern `dotnet_*` names DO appear in this Prometheus index, but they come from an unrelated `MCP.Terminal` process leaking through the host `localhost:4317` port-forward — **not from this stack**. Any dashboard written against `dotnet_gc_collections_total` would render empty. Further, `process_runtime_dotnet_*` provides **no process CPU, no process uptime, and no working-set** metric at all — RTD-02 names three axes the instrumentation does not supply. The nine domain counters ARE real and correctly named (`_total` suffix confirmed live), but three of them (`keeper_messages_consumed_total`, `keeper_messages_sent_total`, `orchestrator_step_unresolved_total`) currently have **zero live series**, because an OTel counter only materializes in Prometheus after its first increment — which for fault counters means a healthy stack renders them as "No data". That directly threatens VER-01 as written.

The second important finding concerns the pod dropdown. `label_values()` is backed by Prometheus's `/api/v1/label/<name>/values?match[]=` endpoint, which is **index/block-granular, not sample-exact**. Verified live: even with a 60-second dashboard window, the keeper pod dropdown returned 4 pods when only 2 were alive (the other 2 died 79 minutes earlier). An instant PromQL query (`count by (service_instance_id) (...)`) returned **exactly the 8 live pods, matching `kubectl get pods` 8/8**. So VAR-02's `label_values()` mandate and VER-01's "dropdowns enumerate the live pod set" cannot both be satisfied strictly; the planner must pick a resolution.

Everything on the Grafana side was verified empirically: a real `grafana/grafana:12.3.9` container was run against the live port-forwarded Prometheus with the exact provisioning layout recommended below. The datasource provisioned read-only, the dashboard provisioned into a folder with `meta.provisioned: true` / `meta.provisionedExternalId: "runtime.json"`, a `POST /api/dashboards/db` overwrite was rejected with `{"message":"Cannot save provisioned dashboard"}`, and the datasource proxy returned live PromQL results. The identical JSON also provisioned cleanly on `grafana/grafana:13.1.1`.

**Primary recommendation:** Pin `grafana/grafana:12.3.9`, mount two provisioning ConfigMaps at `/etc/grafana/provisioning/{datasources,dashboards}` and the dashboard JSON at `/etc/grafana/dashboards`, ship the dashboard JSONs as **standalone `.json` files** generated into a ConfigMap via `configMapGenerator` + `disableNameSuffixHash: true`, author at `schemaVersion: 39` with a `datasource`-type variable and `label_values()` query variables (no custom `allValue`), and write every panel against the **`process_runtime_dotnet_*`** family — then report the RTD-02 CPU/uptime/working-set gap and the VER-01 empty-fault-panel problem back to the user rather than silently papering over them.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Metric emission (runtime + domain counters) | Application (`src/`) | — | Already shipped; **explicitly untouched this phase** |
| Resource→label promotion (`source`, `service_instance_id`, `service_name`) | OTel Collector (`k8s/02-configmaps.yaml:85`) | — | `resource_to_telemetry_conversion: true`; **out of scope to change** |
| Metric storage + query | Prometheus (`k8s/21-prometheus.yaml`) | — | In-cluster, scrapes `otel-collector:8889`; **out of scope to change** |
| Datasource wiring | Grafana provisioning ConfigMap | — | New; file-provisioned at startup, never UI |
| Dashboard definition | Repo JSON files (`k8s/dashboards/*.json`) | Grafana dashboard-provider ConfigMap | Repo is the single source of truth (DASH-03) |
| Dashboard portability | Dashboard-level `datasource` template variable | — | Decouples JSON from any concrete datasource UID (DASH-04) |
| Grafana runtime state (SQLite DB, sessions) | `emptyDir` volume | — | Deliberately disposable — no PVC (DASH-03) |
| Host access | `kubectl port-forward` | — | Matches the existing 8-forward convention (`scripts/phase-80-up.ps1:131-140`) |
| Live verification | Grafana HTTP API + datasource proxy | Prometheus HTTP API | Assertable by a script, not eyeballed (VER-01/VER-02) |

## Project Constraints

No `./CLAUDE.md` exists at the repo root, and neither `.claude/skills/` nor `.agents/skills/` exists. Constraints therefore come from REQUIREMENTS.md, ROADMAP.md, STATE.md and the phase brief:

1. **NO production source changes.** Nothing under `src/` may be edited. Any missing metric/label is a *finding*, not a fix. `[CITED: .planning/REQUIREMENTS.md:5,58]`
2. **No collector / Prometheus / exporter config changes.** `[CITED: .planning/REQUIREMENTS.md:58]`
3. **No Grafana PVC, no auth hardening, no ingress, no alerting, no ES/log panels, no infra-container dashboards.** `[CITED: .planning/REQUIREMENTS.md:55-59]`
4. **Live stack = Docker-Desktop k8s only, never docker-compose.** `[VERIFIED: MEMORY stack-bringup-k8s-only; `kubectl get pods -n skp` returned 14 running pods this session]`
5. **kustomization.yaml convention:** raw manifests, `namespace: skp`, `labels:` with `includeSelectors: false`, numeric filename prefixes, resources listed in tier order. `[CITED: k8s/kustomization.yaml:21-61]`
6. **`k8s/22-otel-collector-servicemonitor.yaml` is deliberately NOT in `kustomization.yaml`** (OCP-only CRD). Do not "fix" that. `[CITED: k8s/22-otel-collector-servicemonitor.yaml:16-24]`
7. **Services are ClusterIP only; host reach is loopback-bound `kubectl port-forward`** (`--address 127.0.0.1`, never `0.0.0.0`). `[CITED: k8s/21-prometheus.yaml:29; scripts/phase-80-up.ps1:121-123]`

## Standard Stack

### Core

| Component | Version | Purpose | Why Standard |
|-----------|---------|---------|--------------|
| `grafana/grafana` | `12.3.9` | Dashboard server | Last release line where the UI's **Classic** dashboard-JSON export still exists, which is what makes the "build in UI → export → commit" authoring loop work. `[VERIFIED: docker run, `grafana version 12.3.9`, provisioning + API all green this session]` |
| Grafana file provisioning (`apiVersion: 1`) | built-in | Datasource + dashboard provisioning at startup | The only mechanism that satisfies DASH-02/DASH-03 with zero UI steps. `[CITED: grafana.com/docs/grafana/latest/administration/provisioning/]` |
| Classic dashboard JSON | `schemaVersion: 39` | Dashboard definition | Verified to provision unmodified on **both** 12.3.9 and 13.1.1; Grafana stored it at 39 without forced migration. `[VERIFIED: GET /api/dashboards/uid/skp-runtime → schemaVersion 39 on both]` |
| kustomize `configMapGenerator` | kustomize v5.7.1 (bundled with kubectl v1.34.1) | Turn standalone `.json` files into a ConfigMap | Keeps the dashboard JSON as real, lintable, diffable `.json` files instead of YAML-indented heredocs. `[VERIFIED: kubectl kustomize produced `data: runtime.json: \|` with `disableNameSuffixHash: true`]` |

### Supporting

| Component | Version | Purpose | When to Use |
|-----------|---------|---------|-------------|
| Grafana HTTP API `/api/health` | n/a | k8s readiness/liveness probe target | Returns `{"database":"ok","version":"12.3.9","commit":...}`. `[VERIFIED live]` |
| Grafana HTTP API `/api/search?type=dash-db` | n/a | Assert both dashboards provisioned (VER-02) | `[VERIFIED live]` |
| Grafana HTTP API `/api/dashboards/uid/:uid` | n/a | Assert `meta.provisioned: true` + `provisionedExternalId` (DASH-03/VER-02) | `[VERIFIED live]` |
| Grafana HTTP API `/api/datasources/uid/:uid/health` | n/a | Assert Prometheus datasource reachable (DASH-02) | Returned `{"status":"OK","message":"Successfully queried the Prometheus API."}`. `[VERIFIED live]` |
| Grafana HTTP API `/api/datasources/proxy/uid/:uid/api/v1/query` | n/a | Run each panel's PromQL **through Grafana's datasource** (VER-01) | `[VERIFIED live — returned the 4-source vector]` |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `grafana/grafana:12.3.9` | `grafana/grafana:13.1.1` | 13.1.1 **does** provision hand-authored classic JSON correctly (verified). But Grafana 13 removed the **Classic** export option from the UI, so a human who tweaks a panel in the browser can no longer export a provisioning-compatible file — exported JSON is v2 and fails with `dashboard appears to be in v2 format. Please use the /apis/dashboard.grafana.app/v2 API`. That breaks the practical authoring loop for a dashboards milestone. `[CITED: github.com/grafana/grafana/issues/123607, /122663]` |
| `configMapGenerator` for dashboard JSON | Hand-authored ConfigMap with the JSON inline as a YAML block scalar | Matches the existing `02-configmaps.yaml` posture and avoids introducing a generator. But it forces every dashboard edit through YAML re-indentation, makes `jq`/lint impossible without extraction, and muddies "the repo JSON is the single source of truth" (DASH-03). Note the existing kustomization already pre-authorizes a generator *provided* `disableNameSuffixHash: true` is set. `[CITED: k8s/02-configmaps.yaml:5-9]` |
| `label_values()` pod variable | `query_result(count by (service_instance_id) (...))` + `regex` extraction | `query_result` gives the **exact live pod set** (verified 8/8 vs `kubectl`), but VAR-02 explicitly mandates `label_values()`. See Open Question OQ-2. |
| `emptyDir` for `/var/lib/grafana` | No volume at all (write into the container layer) | Works, but leaves the SQLite DB in the writable layer; `emptyDir` is explicit about the disposability DASH-03 demands. |

**Installation:** none — no package manager involvement. This phase adds k8s YAML and JSON only.

**Version verification:**
```bash
# grafana tags, newest first (run 2026-07-27)
curl -s "https://hub.docker.com/v2/repositories/grafana/grafana/tags?page_size=25&ordering=last_updated"
#   13.1.1 / 13.0.4 published 2026-07-22 ; 12.3.9 published 2026-07-21
docker run --rm --entrypoint sh grafana/grafana:12.3.9 -c 'grafana --version'   # => grafana version 12.3.9
```
`[VERIFIED: Docker Hub registry API + local `docker run`, 2026-07-27]`

## Package Legitimacy Audit

This phase installs **no** language packages (no npm / PyPI / crates / NuGet additions). The only external artifact is a container image.

| Artifact | Registry | Age | Pulls | Source Repo | slopcheck | Disposition |
|----------|----------|-----|-------|-------------|-----------|-------------|
| `grafana/grafana:12.3.9` | Docker Hub (official org `grafana`) | tag published 2026-07-21; org since 2014 | >1B image pulls | github.com/grafana/grafana | n/a (not a language package) | **Approved** — pulled and executed locally this session, reported `grafana version 12.3.9` |

**Packages removed due to slopcheck [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

`slopcheck` was not available in this environment (`pip` not on PATH under the sandboxed shell) and is not applicable to container images regardless. The image was instead verified the strongest available way: **pulled, run, and functionally exercised** (health API, provisioning, datasource proxy) during this research session.

## Ground-Truth Metric Inventory (Q1)

All of the following was captured LIVE from the in-cluster Prometheus on 2026-07-27 via `kubectl -n skp port-forward svc/prometheus 9090:9090`. The cluster was up: 14 pods running (`baseapi-service` ×1, `keeper` ×2, `orchestrator` ×3, `processor-sample` ×2, plus infra).

### The nine domain counters — requirements are CORRECT

| Requirement name (REQUIREMENTS.md:24) | Instrument name in source | Prometheus name | Live now? |
|---|---|---|---|
| `orchestrator_messages_consumed_total` | `orchestrator_messages_consumed` (`OrchestratorMetrics.cs:55`) | `orchestrator_messages_consumed_total` | ✅ 3 series |
| `orchestrator_messages_sent_total` | `orchestrator_messages_sent` (`:56`) | `orchestrator_messages_sent_total` | ✅ 3 series |
| `orchestrator_step_unresolved_total` | `orchestrator_step_unresolved` (`:57`) | `orchestrator_step_unresolved_total` | ❌ **0 live series** |
| `keeper_messages_consumed_total` | `keeper_messages_consumed` (`KeeperMetrics.cs:50`) | `keeper_messages_consumed_total` | ❌ **0 live series** (not even in `__name__`) |
| `keeper_messages_sent_total` | `keeper_messages_sent` (`:51`) | `keeper_messages_sent_total` | ❌ **0 live series** (not even in `__name__`) |
| `keeper_l2_probe_total` | `keeper_l2_probe` (`:52`) | `keeper_l2_probe_total` | ✅ 2 series |
| `processor_messages_consumed_total` | `processor_messages_consumed` (`ProcessorMetrics.cs:90`) | `processor_messages_consumed_total` | ✅ 2 series |
| `processor_messages_sent_total` | `processor_messages_sent` (`:91`) | `processor_messages_sent_total` | ✅ 2 series |
| `processor_spawn_dropped_total` | `processor_spawn_dropped` (`:92`) | `processor_spawn_dropped_total` | ❌ 0 live series (present in `__name__` historically) |

**The `_total` suffix is appended by the collector's Prometheus exporter**, not by the instrument name — the source deliberately omits it (`OrchestratorMetrics.cs:17-19`). The requirements' names are therefore exactly right. `[VERIFIED: live Prometheus `/api/v1/label/__name__/values`]`

**Domain-counter label sets (live):**

```
orchestrator_messages_consumed_total / _sent_total
  → source, service_name, service_instance_id, workflowId, processorId,
    otel_scope_name="Orchestrator", exported_job, exported_instance, job, instance
processor_messages_consumed_total / _sent_total
  → same + identityName (e.g. "sample-proc-ea1076bd..._1.0.0"), otel_scope_name="BaseProcessor"
keeper_l2_probe_total
  → source, service_name, service_instance_id (LABEL-LESS otherwise — no workflowId/processorId)
orchestrator_step_unresolved_total  → workflowId only (+ resource labels)   [source: OrchestratorMetrics.cs:46-50]
processor_spawn_dropped_total       → processorId only (+ resource labels)  [source: ProcessorMetrics.cs:80-85]
```
Tag names are camelCase and centralised: `ConsoleMetricTags.WorkflowIdTag = "workflowId"`, `ProcessorIdTag = "processorId"` (`src/BaseConsole.Core/Observability/ConsoleMetricTags.cs`).

### FINDING F-1 (CRITICAL): the runtime family is `process_runtime_dotnet_*`, NOT `dotnet_*`

The phase brief and the mental model of "modern .NET runtime metrics" both point at `dotnet_gc_collections_total` / `dotnet_process_memory_working_set_bytes`. **Those names exist in this Prometheus but carry no data from this stack.**

```
group by (source, service_name) (dotnet_gc_collections_total)                     → EMPTY
group by (source, service_name) (process_runtime_dotnet_gc_collections_count_total)
  → {source="webapi",       service_name="sk-api_3.2.0"}        3 series
    {source="keeper",       service_name="keeper_3.7.0"}        6
    {source="processor",    service_name="unresolved_0.0.0"}    6
    {source="orchestrator", service_name="orchestrator_3.4.0"}  9
```
Tracing the `dotnet_*` series over 15 days: the **only** emitter is `service_name="MCP.Terminal"`, `otel_scope_name="System.Runtime"` — an unrelated .NET 9 process on the host leaking into the collector via the `localhost:4317` port-forward. This is the same leak STATE.md records for quick task `260727-ngv` ("traced + killed 2 `MCP.Terminal` OTLP emitters leaking into the collector"). It carries **no `source` label at all**.

Root cause of the naming: `Directory.Build.props:29` pins `net8.0`, and `Directory.Packages.props:84` pins `OpenTelemetry.Instrumentation.Runtime` **1.15.0**. On net8.0 that package emits the legacy `process.runtime.dotnet.*` instruments under scope `OpenTelemetry.Instrumentation.Runtime` — confirmed live: every runtime series carries `otel_scope_name="OpenTelemetry.Instrumentation.Runtime"`, `otel_scope_version="1.15.0"`. The `dotnet.*` names only appear when the built-in .NET 9 `System.Runtime` meter is the source.

**Consequence for the plan:** every runtime panel must use `process_runtime_dotnet_*`. A future move to `net9.0` would silently rename the entire family and blank the dashboard — worth a comment in the JSON. `[VERIFIED: live PromQL + Directory.Build.props:29 + Directory.Packages.props:84]`

### Complete live runtime inventory (`process_runtime_dotnet_*`)

| Prometheus metric | Type | Extra labels | webapi | orch | keeper | proc |
|---|---|---|---|---|---|---|
| `process_runtime_dotnet_gc_collections_count_total` | counter | `generation` = gen0/gen1/gen2 | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_gc_heap_size_bytes` | gauge | `generation` = gen0/gen1/gen2/loh/poh (5) | ❌ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_gc_heap_fragmentation_size_bytes` | gauge | `generation` (5) | ❌ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_gc_committed_memory_size_bytes` | gauge | — | ❌ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_gc_allocations_size_bytes_total` | counter | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_gc_duration_nanoseconds_total` | counter | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_gc_objects_size_bytes` | gauge | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_thread_pool_threads_count` | gauge | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_thread_pool_queue_length` | gauge | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_thread_pool_completed_items_count_total` | counter | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_exceptions_count_total` | counter | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_monitor_lock_contention_count_total` | counter | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_assemblies_count` | gauge | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_timer_count` | gauge | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_jit_methods_compiled_count_total` | counter | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_jit_il_compiled_size_bytes_total` | counter | — | ✅ | ✅ | ✅ | ✅ |
| `process_runtime_dotnet_jit_compilation_time_nanoseconds_total` | counter | — | ✅ | ✅ | ✅ | ✅ |

### FINDING F-2 (CRITICAL, RTD-02): no process CPU, no uptime, no working set

RTD-02 asks for "memory/**working set**" and "process **uptime/CPU** where the instrumentation provides it". **The instrumentation provides none of the three.** There is no `process_cpu_*`, no `process_start_time_seconds`, no `process_memory_working_set` anywhere in the live inventory — the closest available signals are:

- **Memory:** `process_runtime_dotnet_gc_committed_memory_size_bytes` (GC-committed bytes) — *not* the OS working set, and **absent for the webapi**.
- **CPU:** nothing. `process_runtime_dotnet_gc_duration_nanoseconds_total` gives GC time-per-second, which is a GC-pressure proxy, not CPU.
- **Uptime:** nothing. Counter *resets* (`resets(process_runtime_dotnet_jit_methods_compiled_count_total[$__range])`) can detect a restart, but not report uptime.

Getting the real thing would require **either** adding `OpenTelemetry.Instrumentation.Process` (currently `1.17.0-rc.1`, still pre-GA on NuGet — a `src/` change, forbidden) **or** adding a cAdvisor/kubelet scrape job to `prometheus.yml` (a collector/Prometheus config change, explicitly out of scope per REQUIREMENTS.md:58). RTD-02's own escape hatch — "*where the instrumentation provides it*" — covers this, but the planner must state the substitution explicitly rather than shipping an empty "CPU" panel. `[VERIFIED: full `__name__` enumeration on live Prometheus; NuGet flatcontainer index for OpenTelemetry.Instrumentation.Process]`

### FINDING F-3: the webapi is missing three GC gauges

`process_runtime_dotnet_gc_heap_size_bytes`, `_gc_heap_fragmentation_size_bytes`, and `_gc_committed_memory_size_bytes` returned **no data for `source="webapi"`** while all three exist for the other three classes. The webapi's `process_runtime_dotnet_gc_collections_count_total` reads **0 for gen0/gen1/gen2** — the pod has simply never run a GC (idle, 79m old). Those three gauges derive from `GC.GetGCMemoryInfo()`, which yields nothing before the first collection. They will populate once the webapi does real work. **VER-01 must therefore be evaluated with traffic flowing**, exactly as the requirement says — and even then a low-traffic webapi may not GC. `[VERIFIED: live]`

### FINDING F-4: fault counters legitimately render "No data" on a healthy stack

An OTel counter is not exported to Prometheus until it is incremented at least once. Live right now:
- `orchestrator_step_unresolved_total` — 0 series
- `processor_spawn_dropped_total` — 0 series (present in the 15d `__name__` list, so it *has* fired historically)
- `keeper_messages_consumed_total` / `keeper_messages_sent_total` — 0 series, and **absent from `__name__` entirely** over the full 15d retention

VER-01 says "**every** panel renders non-empty data". For fault indicators that is unsatisfiable on a healthy system — a green stack *should* have zero unresolved steps and zero dropped spawns. See Open Question OQ-1 and the `or vector(0)` mitigation in Code Examples.

### FINDING F-5: `keeper_reinject`/drop-outcome has no metric at all

BPD-02 asks for "keeper reinject/drop outcomes" as a dedicated panel. **No such counter exists.** `KeeperMetrics.cs:12-21` documents that Phase 74 *removed* the legacy label-less reinject-drop counter and replaced it with the uniform consumed/sent pair. The keeper's only three instruments are `keeper_messages_consumed`, `keeper_messages_sent`, `keeper_l2_probe` (`KeeperMetrics.cs:50-52`) — confirmed by `grep CreateCounter src/Keeper --include=*.cs`. Reinject outcome lives only in Elasticsearch as `attributes.ReinjectOutcome` (STATE.md, Phase 78), and ES panels are out of scope (REQUIREMENTS.md:56). Available Prometheus substitutes:
- **consumed − sent gap** for the keeper (the drop is the difference),
- `messaging_masstransit_consume_errors_ea_total{source="keeper"}` — exists in the 15d index with a `messaging_masstransit_exception_type` label, but no live series now,
- `keeper_l2_probe_total` rate as the "keeper is alive and probing" heartbeat (`rate(...) > 0`, per `KeeperMetrics.cs:20`).

## Label Reality (Q2)

All three labels are genuinely present, and the promotion mechanism is exactly as the requirements assume.

**Where the attributes are set** (both files set them identically on the **metrics** resource):
- `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:68-71` → `metricAttrs = { "service.instance.id" = ResolveInstanceId(), "source" = source }`, applied at `:98-109` via `SetResourceBuilder(...AddService($"{name}_{version}").AddAttributes(metricAttrs))`.
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:80-84` → identical shape, applied at `:115-125`.
- `ResolveInstanceId()` precedence: `POD_NAME → HOSTNAME → MachineName → GUID` (`:136-140` / `:148-152`).
- The collector promotes them to Prometheus labels via `resource_to_telemetry_conversion: {enabled: true}` (`k8s/02-configmaps.yaml:85-86`).

**Live label values (time-scoped to the last 15 minutes):**
```
label_values(source)               → ["keeper","orchestrator","processor","webapi"]     ← exactly the 4
label_values(service_name)         → sk-api_3.2.0, orchestrator_3.4.0, keeper_3.7.0, unresolved_0.0.0
label_values(service_instance_id)  → pod names (baseapi-service-…, keeper-…, orchestrator-…, processor-sample-…)
```

**Additional labels present on every series** (relevant to legends and to avoiding accidental grouping):
`job="otel-collector"`, `instance="otel-collector:8889"` (the *scrape* identity), plus **`exported_job`** and **`exported_instance`** — Prometheus's collision-rename of the collector's own `job`/`instance` labels (which mirror `service_name`/`service_instance_id`). Also `telemetry_sdk_language="dotnet"`, `telemetry_sdk_name`, `telemetry_sdk_version="1.15.3"`, `otel_scope_name`, `otel_scope_version`. Never group by `instance` or `job` — they are constant across the whole stack.

### FINDING F-6 (VAR-04 is WRONG as written)

VAR-04 states: "*both processor images share the value `processor` on the `source` label … the dashboards keep `service_name` visible as the finer-grained discriminator*".

The first half is true. **The second half is not, for metrics.** `src/Processor.Sample/appsettings.json:9-12` configures `Service:Name = "unresolved"`, `Service:Version = "0.0.0"`, and MLBL-03 deliberately keeps it that way for the pod's whole life (`ProcessorMetrics.cs:40-43`: "*a pod keeps ONE `service_name` (the `unresolved_0.0.0` sentinel) for its whole life*"). Live confirmation: every `source="processor"` series carries `service_name="unresolved_0.0.0"`. So `service_name` **cannot** discriminate processor images.

What actually discriminates them:
- **`identityName`** — a **datapoint** tag carrying `{db.Name}_{db.Version}` (e.g. `sample-proc-ea1076bd…_1.0.0`), present on `processor_messages_consumed_total` / `_sent_total`. `[VERIFIED live]` It is **not** on the runtime series (those are framework-level, untagged).
- **`processorId`** — the DB identity GUID, on the business counters.
- **`service_instance_id`** — the pod name, on everything.

Also note `Processor.BadConfig` *does* set a real `Service:Name = "processor-badconfig"`, but **only `processor-sample` is deployed to k8s** (`k8s/33-processor-sample.yaml:52` is the sole processor image; there is no second processor Deployment). So "both processor images" is currently a single-image reality in this cluster.

**Planner action:** on the business dashboard, use `identityName` in the processor panels' `legendFormat` / group-by. On the runtime dashboard, `service_name` is degenerate for processors — use `service_instance_id`. Report VAR-04's premise as corrected.

## Live Verification (Q3) — DONE, cluster was UP

The Docker-Desktop `skp` cluster was running at research time, so everything above is **live-verified, not source-derived**. Evidence:

```
kubectl get pods -n skp
  baseapi-service-66ffd97c8-pnngr     1/1 Running   79m
  keeper-69ccc978c7-{bdbw2,flhmj}     1/1 Running   79m
  orchestrator-7b5675d958-{572s9,bccjc,jwq8t}  1/1 Running  35m
  processor-sample-6bb6b7d86c-{2tcdd,zlpbx}    1/1 Running  37m
  + elasticsearch-0, postgres-0, rabbitmq-0, redis-0, otel-collector, prometheus

kubectl -n skp port-forward svc/prometheus 9090:9090
curl 127.0.0.1:9090/api/v1/...     # metric names, label values, series, instant queries
```
Traffic was flowing: `orchestrator_messages_consumed_total` read 63-64 per orchestrator pod, `processor_messages_consumed_total` read 100 per processor pod, all sharing `workflowId="cc6e6538-95db-4657-9fb0-296be979c27c"`.

Additionally a **real Grafana was run against this live Prometheus** (`docker run grafana/grafana:12.3.9 -p 3001:3000` with `host.docker.internal:9090`), and again with `13.1.1` on `:3002`. Both containers were removed afterwards; nothing in the repo or cluster was mutated.

### FINDING F-7 (CRITICAL for VAR-02/VER-01): `label_values()` over-returns dead pods

Grafana's `label_values(metric{filter}, label)` resolves through `PrometheusMetricFindQuery.labelValuesQuery` → `languageProvider.queryLabelValues` → Prometheus `/api/v1/label/<name>/values?match[]=…&start=…&end=…`. That endpoint answers from the **TSDB index over overlapping blocks**, not from samples. Measured live, through Grafana's own datasource proxy, with `match[]=process_runtime_dotnet_gc_collections_count_total{source=~"(keeper)"}`:

| Dashboard window | Values returned |
|---|---|
| 30 min | 4 pods — 2 live + `keeper-69db947897-rlcvc`, `keeper-69db947897-z8qbc` (dead ~79 min) |
| 5 min | same 4 |
| **60 sec** | **same 4** |

By contrast an **instant PromQL query** returns exactly the live set:
```
count by (service_instance_id) (process_runtime_dotnet_gc_collections_count_total{source=~"(keeper|processor|orchestrator|webapi)"})
  → 8 results, byte-identical to `kubectl get pods` (8/8 match)
```
So `label_values()` cannot deliver "the live pod set per class" (VER-01), no matter how short the time range. See OQ-2.

### FINDING F-8 (VAR-01/VAR-02): unscoped `label_values` is polluted; `allValue: ".*"` is a trap

Over the full 15d retention, `label_values(source)` returns **5** values — the four classes plus **`console-test`**, from the hermetic test suite emitting to `localhost:4317`. And the `process_runtime_dotnet_gc_collections_count_total` family contains a large mass of series with **no `source` label at all** (older builds, `service_name` values like `processor-sample_3.5.0`, `sk-api_3.2.0`, `sample-proc-*`, plus `MCP.Terminal`).

Because PromQL `label=~".*"` **matches series where the label is absent**, setting `allValue: ".*"` would sweep all of it in. Measured:
```
{source=~".*"}                                        → 1044 series / 268 distinct pods
{source=~"webapi|orchestrator|keeper|processor"}      →  159 series /  51 distinct pods
```
**Therefore: leave `allValue` unset (null).** Grafana's `getAllValue()` then expands "All" to the explicit option list, and `interpolateQueryExpr()` renders it as `(webapi|orchestrator|keeper|processor)` — an exact alternation that cannot match label-less series. `[VERIFIED: live PromQL + grafana v12.3.0 source `public/app/features/templating/template_srv.ts:236-245` and `packages/grafana-prometheus/src/escaping.ts:7-27`]`

Also: `label_values(source)` **must** be scoped to a metric that only this stack emits — use `label_values(process_runtime_dotnet_gc_collections_count_total, source)`, not the bare one-arg form.

## Grafana Deployment Shape (Q4)

### Container facts (verified by running the image)

```
docker run --rm --entrypoint sh grafana/grafana:12.3.9 -c 'id; env | grep GF_PATHS; ls /etc/grafana/provisioning'
  uid=472(grafana) gid=0(root) groups=0(root)
  GF_PATHS_HOME=/usr/share/grafana
  GF_PATHS_LOGS=/var/log/grafana
  GF_PATHS_PROVISIONING=/etc/grafana/provisioning
  GF_PATHS_PLUGINS=/var/lib/grafana/plugins
  GF_PATHS_CONFIG=/etc/grafana/grafana.ini
  GF_PATHS_DATA=/var/lib/grafana
  /etc/grafana/provisioning/{access-control,alerting,dashboards,datasources,notifiers,plugins}
```
`[VERIFIED: local docker run, 2026-07-27]`

**Mount at the leaf directories, never at `/etc/grafana/provisioning` itself** — mounting the parent would erase the other five provisioning subdirectories.

**Do not set `GF_PATHS_PROVISIONING`.** The default is already correct; overriding it adds a failure mode for no benefit.

### Recommended manifest set — `k8s/23-grafana.yaml`

Numbering: the observability tier occupies `20`(collector) / `21`(prometheus) / `22`(servicemonitor, un-kustomized). **`23-grafana.yaml`** continues the tier and stays below the `30+` app tier. Add exactly one line to `kustomization.yaml` resources, after `21-prometheus.yaml`.

Contents (mirroring the `21-prometheus.yaml` house style — commented header, ClusterIP Service first, then Deployment):

- **Service** `grafana`, ClusterIP, `port: 3000 → targetPort: 3000`, `selector: {app: grafana}`, labels `app: grafana` + `app.kubernetes.io/part-of: skp`.
- **Deployment** `grafana`, `replicas: 1`, `selector.matchLabels: {app: grafana}`.
  - `image: grafana/grafana:12.3.9` (**pin the patch** — the repo's `prom/prometheus:v3.11.3` sets that precedent; never `:latest`).
  - `securityContext: { runAsUser: 472, runAsGroup: 0, fsGroup: 472 }` at pod level.
  - `ports: [{containerPort: 3000}]`.
  - **env:**
    | Var | Value | Why |
    |---|---|---|
    | `GF_SECURITY_ADMIN_USER` | `admin` | Explicit; the verification script uses basic auth |
    | `GF_SECURITY_ADMIN_PASSWORD` | `admin` | Dev posture, matches the repo's no-auth stance (`k8s/02-configmaps.yaml:157-158`). Do **not** put it in `skp-dev-secrets` unless the planner wants the extra indirection — REQUIREMENTS.md:57 rules out auth hardening |
    | `GF_AUTH_ANONYMOUS_ENABLED` | `"true"` | Port-forward → browser with zero login (DASH-01 "reachable by port-forward like the other tools") |
    | `GF_AUTH_ANONYMOUS_ORG_ROLE` | `Viewer` | Read-only by default; reinforces "repo is the source of truth" |
    | `GF_ANALYTICS_REPORTING_ENABLED` | `"false"` | No outbound calls from a dev cluster |
    | `GF_ANALYTICS_CHECK_FOR_UPDATES` | `"false"` | Same; also removes a startup log warning |
    | `GF_USERS_ALLOW_SIGN_UP` | `"false"` | Default already, explicit for clarity |
  - **volumeMounts:**
    | mountPath | volume | readOnly |
    |---|---|---|
    | `/etc/grafana/provisioning/datasources` | ConfigMap `grafana-datasources` | yes |
    | `/etc/grafana/provisioning/dashboards` | ConfigMap `grafana-dashboard-provider` | yes |
    | `/etc/grafana/dashboards` | ConfigMap `grafana-dashboards` (the JSONs) | yes |
    | `/var/lib/grafana` | `emptyDir: {}` | no — **this is the no-PVC statement (DASH-03)** |
  - **probes** (Grafana's `/api/health` returns 200 + `{"database":"ok",...}` once the SQLite DB is migrated):
    ```yaml
    startupProbe:   { httpGet: {path: /api/health, port: 3000}, periodSeconds: 5,  failureThreshold: 30 }
    readinessProbe: { httpGet: {path: /api/health, port: 3000}, initialDelaySeconds: 10, periodSeconds: 10, timeoutSeconds: 3, failureThreshold: 3 }
    livenessProbe:  { httpGet: {path: /api/health, port: 3000}, initialDelaySeconds: 30, periodSeconds: 15, failureThreshold: 6 }
    ```
    (A `startupProbe` matches the Phase-86 convention now on all four app deployments; Grafana's first-boot DB migration took ~9 s in the measured run, so the 150 s startup budget is ample.)
  - **resources:** `requests: {memory: "128Mi", cpu: "50m"}`, `limits: {memory: "512Mi"}` — Grafana 12 idled around 100-150 MiB in the measured container; `21-prometheus.yaml` uses 128Mi/256Mi as the house shape but Grafana needs more headroom.
- **`strategy: {type: Recreate}`** is *not* needed (no exclusive resource, unlike the orchestrator), but `RollingUpdate` with 1 replica briefly runs 2 pods sharing nothing — harmless.

### Dashboard JSON packaging — the size question

`kubectl kustomize` was verified to produce a valid ConfigMap keyed by file **basename** (`runtime.json`) from `configMapGenerator.files: [dashboards/runtime.json]` with `generatorOptions.disableNameSuffixHash: true`.

**ConfigMap size limit: 1 MiB total per ConfigMap** (the etcd request-size ceiling; kube-apiserver rejects larger). Two hand-authored dashboards with ~15-25 panels each land in the 40-120 KB range — roughly 10x headroom. Risk is low but real if panels multiply: if the pair ever approaches ~700 KB, **split into two ConfigMaps** (`grafana-dashboards-runtime`, `grafana-dashboards-business`) mounted at two paths, and add a second provider entry (or one provider with `foldersFromFilesStructure: true`). Recommend noting the limit in the manifest header rather than pre-splitting.

### Adding the port-forward

`scripts/phase-80-up.ps1:131-140` holds the 8-forward table. Adding Grafana means a 9th entry:
```powershell
@{ svc = 'svc/grafana'; map = '3000:3000' },   # Grafana UI + HTTP API (dashboards)
```
**Caution:** the harness header comments state a fixed count ("EIGHT loopback port-forwards", `phase-80-up.ps1:12,116`; `phase-80-harness.ps1:17,34`) and the sweep is exit-code-load-bearing. Adding a forward touches shared harness infrastructure used by the 7-scenario resilience sweep. Two options for the planner:
- **(a) Safer:** do **not** touch `phase-80-up.ps1`; the Phase-87 verification script starts its own `kubectl -n skp port-forward --address 127.0.0.1 svc/grafana 3000:3000` and tears it down. Zero blast radius on the sweep.
- **(b) Integrated:** add the 9th entry and update the four count comments. Convenient for humans, but perturbs the sweep's bring-up.
Recommend **(a)**.

## Datasource Provisioning (Q5)

The in-cluster Prometheus Service is `prometheus` in namespace `skp` on port `9090` (`k8s/21-prometheus.yaml:22-34`) — so the in-cluster URL is **`http://prometheus:9090`** (bare Service DNS, exactly as every other manifest addresses its peers).

`k8s/23-grafana.yaml` (or a `24-grafana-provisioning.yaml`) — ConfigMap `grafana-datasources`, key `prometheus.yaml`:

```yaml
apiVersion: 1

# prune: true removes datasources that were provisioned by a previous version of
# this file and have since been deleted from it. Accepted by 12.3.9 (verified).
prune: true

datasources:
  - name: Prometheus
    uid: skp-prometheus          # STABLE, explicit — never let Grafana mint a random one
    type: prometheus
    access: proxy                # Grafana server-side fetch; the browser never talks to :9090
    url: http://prometheus:9090  # k8s/21-prometheus.yaml Service name + port
    isDefault: true              # DASH-02
    editable: false              # UI cannot mutate it -> API reports readOnly: true
    jsonData:
      httpMethod: POST           # long PromQL survives; avoids URL length limits
      timeInterval: "15s"        # matches prometheus.yml global.scrape_interval (02-configmaps.yaml:151)
                                 # -> $__rate_interval floors at 4x15s = 60s
```
Verified live: `GET /api/datasources` returned this exact object with `"readOnly": true`, `"isDefault": true`; `GET /api/datasources/uid/skp-prometheus/health` returned `{"status":"OK","message":"Successfully queried the Prometheus API."}`.

### How the stable `uid` and DASH-04 coexist (they are not in tension)

They serve two different consumers:

- **The stable `uid: skp-prometheus`** is what makes *this deployment* deterministic. Without it Grafana generates a random UID on first provision; after a pod delete/recreate (VER-02) with an `emptyDir` data dir it would generate a *different* one, and any dashboard hardcoding the old value would break. It also gives the verification script a fixed address (`/api/datasources/uid/skp-prometheus/...`).
- **The `datasource` template variable** is what makes the *JSON file* portable. Every panel and every query variable references `"datasource": {"type": "prometheus", "uid": "${datasource}"}`. The literal string `skp-prometheus` appears **nowhere** in either dashboard JSON.

The `datasource`-type variable resolves at load time to whichever Prometheus datasource the target Grafana marks default. So: drop the same file into the org's OCP Grafana (per `EXTERNAL-SERVICES.md`, where Prometheus is org-provided via UWM and will have a completely different UID) and it binds correctly with no edit. The stable uid is an operational property of *our* datasource YAML; the variable is a portability property of the *dashboards*. Neither reads the other.

`[VERIFIED: the test dashboard used only `${datasource}`; the provisioned datasource had `uid: skp-prometheus`; queries resolved and returned data]`

## Dashboard Provider Provisioning

ConfigMap `grafana-dashboard-provider`, key `skp.yaml`, mounted at `/etc/grafana/provisioning/dashboards`:

```yaml
apiVersion: 1

providers:
  - name: skp
    orgId: 1
    folder: SKP                    # both dashboards land in a named folder
    type: file
    disableDeletion: true          # UI cannot delete a provisioned dashboard
    allowUiUpdates: false          # UI "Save" is rejected -> repo is the source of truth (DASH-03)
    updateIntervalSeconds: 30      # re-reads the dir; with a ConfigMap this also picks up kubelet
                                   # ConfigMap refresh (~60s) without a pod restart
    options:
      path: /etc/grafana/dashboards
      foldersFromFilesStructure: false
```
Verified live: dashboard appeared under folder `SKP`; `meta.provisioned: true`, `meta.provisionedExternalId: "runtime.json"`; `POST /api/dashboards/db` with `overwrite: true` was rejected `{"message":"Cannot save provisioned dashboard"}` (HTTP 400).

## Dashboard JSON Schema Specifics (Q6)

**`schemaVersion: 39`** is the recommendation. Grafana 12.3's internal current is `DASHBOARD_SCHEMA_VERSION = 42` (`public/app/features/dashboard/state/DashboardMigrator.ts:90`), but Grafana stores provisioned JSON as authored and migrates in-memory on load — the test dashboard read back at `schemaVersion: 39` on **both** 12.3.9 and 13.1.1. 39 is the widest-compatible number that still supports the modern `timeseries` panel and the object-form `datasource` reference.

### Required / recommended top-level fields

```jsonc
{
  "uid": "skp-runtime",            // REQUIRED for a stable /d/<uid> URL and API assertions (8-40 chars)
  "title": "SKP Runtime Instrumentation",   // REQUIRED
  "tags": ["skp","runtime"],
  "timezone": "browser",
  "editable": false,               // pairs with allowUiUpdates:false
  "graphTooltip": 1,               // shared crosshair — useful across per-pod panels
  "schemaVersion": 39,             // REQUIRED
  "version": 1,
  "refresh": "30s",                // >= the 15s scrape interval
  "time": { "from": "now-1h", "to": "now" },
  "annotations": { "list": [] },
  "links": [],
  "templating": { "list": [ /* see below */ ] },
  "panels": [ /* ... */ ]
}
```
Omit `"id"` entirely (it is DB-assigned; a stale `id` can collide). Do **not** include `__inputs` / `__requires` — those belong to the *export-for-sharing* format and make file provisioning fail.

### (a) `datasource`-type variable (DASH-04)

```json
{
  "type": "datasource",
  "name": "datasource",
  "label": "Datasource",
  "query": "prometheus",
  "current": {},
  "refresh": 1,
  "regex": "",
  "hide": 0,
  "skipUrlSync": false
}
```
`query` is the **plugin id**, not a PromQL string. Referenced everywhere as `"uid": "${datasource}"`.

### (b) `query` variable with `label_values(metric, label)`, multi + includeAll

```json
{
  "type": "query",
  "name": "source",
  "label": "Service class",
  "datasource": { "type": "prometheus", "uid": "${datasource}" },
  "query": "label_values(process_runtime_dotnet_gc_collections_count_total, source)",
  "definition": "label_values(process_runtime_dotnet_gc_collections_count_total, source)",
  "refresh": 2,
  "sort": 1,
  "multi": true,
  "includeAll": true,
  "allValue": null,
  "current": {},
  "options": [],
  "hide": 0,
  "skipUrlSync": false
}
```
- **`query` as a plain string is fully supported** and is the right form for hand-authored JSON. `PrometheusVariableSupport.query()` explicitly handles the "grafana as code from jsonnet variable queries which are strings and not objects" path (`packages/grafana-prometheus/src/variables.ts:33-37`). The richer object form (`{"qryType": 1, "label": "...", "metric": "...", "refId": "PrometheusVariableQueryEditor-VariableQuery"}`) is what the UI writes; both work, the string is far more legible in a diff. `[VERIFIED: grafana v12.3.0 source]`
- `refresh: 2` = "On time range change" — needed so the dropdowns track the dashboard window. (`1` = on dashboard load only.)
- `sort: 1` = alphabetical ascending.
- **`allValue: null` is load-bearing** — see F-8.

### (c) Chained query variable

```json
{
  "type": "query",
  "name": "pod",
  "label": "Pod",
  "datasource": { "type": "prometheus", "uid": "${datasource}" },
  "query": "label_values(process_runtime_dotnet_gc_collections_count_total{source=~\"$source\"}, service_instance_id)",
  "definition": "label_values(process_runtime_dotnet_gc_collections_count_total{source=~\"$source\"}, service_instance_id)",
  "refresh": 2, "sort": 1, "multi": true, "includeAll": true, "allValue": null,
  "current": {}, "options": [], "hide": 0, "skipUrlSync": false
}
```
Grafana interpolates `$source` through the Prometheus datasource's `interpolateQueryExpr` before issuing the label-values request, so the emitted match is e.g. `{source=~"(keeper|orchestrator)"}`. `[VERIFIED: variables.ts:53 passes `this.datasource.interpolateQueryExpr` into `templateSrv.replace`]`

### The "All" + `=~` mechanics (exact, from source)

`packages/grafana-prometheus/src/escaping.ts:7-27`:
```ts
export function interpolateQueryExpr(value, variable) {
  if (!variable.multi && !variable.includeAll) return prometheusRegularEscape(value);
  if (typeof value === 'string')               return prometheusSpecialRegexEscape(value);
  const escaped = value.map(prometheusSpecialRegexEscape);
  if (escaped.length === 1) return escaped[0];
  return '(' + escaped.join('|') + ')';
}
```
`public/app/features/templating/template_srv.ts:236-245`:
```ts
getAllValue(variable) {
  if (variable.allValue) return variable.allValue;      // custom allValue short-circuits
  const values = [];
  for (let i = 1; i < variable.options.length; i++) values.push(variable.options[i].value);
  return values;                                         // else: the full discovered list
}
```
Net effect with `allValue: null`:

| Selection | `$source` renders as |
|---|---|
| one value | `keeper` |
| two values | `(keeper|orchestrator)` |
| **All** | `(webapi|orchestrator|keeper|processor)` |

Always write the matcher as `{source=~"$source"}` — **`=~`, never `=`** — because even a single-value multi variable can become an alternation. `[CITED: grafana.com/docs/grafana/latest/datasources/prometheus/template-variables/ — "When Multi-value or Include All is enabled, use `=~` … since the variable value becomes a regex pattern"]`

### Schema differences between Grafana majors that could break the JSON

| Change | Version | Impact here |
|---|---|---|
| Angular `graph` panel removed | 11.0 | Use `"type": "timeseries"` (and `stat` / `table` / `bargauge`). Never `graph`. |
| `datasource` as a bare name string → object `{type, uid}` | 8.3+ | Always use the object form. |
| Classic **export** option removed from the UI | **13.0** | File provisioning of *hand-authored* classic JSON still works (verified on 13.1.1) — but a UI export can no longer be committed back. This is the reason to pin 12.x. `[CITED: grafana/grafana#123607, #122663]` |
| Dashboards v2 / Kubernetes-resource provisioning | 12.2+/13 | Only required for v2/dynamic dashboards. Not used here. `[CITED: grafana.com/docs/grafana/latest/administration/provisioning/]` |
| `/api/*` deprecation in favour of `/apis/*` | 13 | Legacy `/api/*` remains functional (verified on 13.1.1: `/api/search`, `/api/dashboards/uid/...` both worked). |

### Minimal but complete, copy-pasteable skeleton

This exact file was provisioned successfully into Grafana **12.3.9** and **13.1.1** this session:

```json
{
  "uid": "skp-runtime",
  "title": "SKP Runtime Instrumentation",
  "tags": ["skp", "runtime"],
  "timezone": "browser",
  "editable": false,
  "graphTooltip": 1,
  "schemaVersion": 39,
  "version": 1,
  "refresh": "30s",
  "time": { "from": "now-1h", "to": "now" },
  "annotations": { "list": [] },
  "links": [],
  "templating": {
    "list": [
      { "type": "datasource", "name": "datasource", "label": "Datasource",
        "query": "prometheus", "current": {}, "refresh": 1, "regex": "",
        "hide": 0, "skipUrlSync": false },
      { "type": "query", "name": "source", "label": "Service class",
        "datasource": { "type": "prometheus", "uid": "${datasource}" },
        "query": "label_values(process_runtime_dotnet_gc_collections_count_total, source)",
        "definition": "label_values(process_runtime_dotnet_gc_collections_count_total, source)",
        "refresh": 2, "sort": 1, "multi": true, "includeAll": true, "allValue": null,
        "current": {}, "options": [], "hide": 0, "skipUrlSync": false },
      { "type": "query", "name": "pod", "label": "Pod",
        "datasource": { "type": "prometheus", "uid": "${datasource}" },
        "query": "label_values(process_runtime_dotnet_gc_collections_count_total{source=~\"$source\"}, service_instance_id)",
        "definition": "label_values(process_runtime_dotnet_gc_collections_count_total{source=~\"$source\"}, service_instance_id)",
        "refresh": 2, "sort": 1, "multi": true, "includeAll": true, "allValue": null,
        "current": {}, "options": [], "hide": 0, "skipUrlSync": false }
    ]
  },
  "panels": [
    {
      "id": 1,
      "type": "timeseries",
      "title": "GC collections / sec by generation",
      "datasource": { "type": "prometheus", "uid": "${datasource}" },
      "gridPos": { "h": 8, "w": 12, "x": 0, "y": 0 },
      "fieldConfig": { "defaults": { "unit": "ops" }, "overrides": [] },
      "options": {
        "legend": { "displayMode": "table", "placement": "bottom", "calcs": ["mean", "max"] },
        "tooltip": { "mode": "multi", "sort": "desc" }
      },
      "targets": [
        {
          "refId": "A",
          "datasource": { "type": "prometheus", "uid": "${datasource}" },
          "editorMode": "code",
          "range": true,
          "expr": "sum by (source, service_instance_id, generation) (rate(process_runtime_dotnet_gc_collections_count_total{source=~\"$source\", service_instance_id=~\"$pod\"}[$__rate_interval]))",
          "legendFormat": "{{source}} / {{service_instance_id}} / {{generation}}"
        }
      ]
    }
  ]
}
```

**Row organisation:** for RTD-03 ("a single misbehaving replica is visually separable"), prefer one panel per axis with `sum by (source, service_instance_id, …)` and a table legend, over `repeat` rows. `repeat` on `$source` multiplies panel count by 4 and inflates the JSON; a table legend with per-series `mean`/`max` makes the outlier replica obvious in one place.

## Panel / PromQL Design (Q7)

Common filter (append inside every selector): `{source=~"$source", service_instance_id=~"$pod"}`.
Rate window: **`$__rate_interval`** everywhere — with `timeInterval: "15s"` on the datasource it floors at 60 s (4× scrape), which is the correct minimum for a 15 s scrape. Do not hardcode `[5m]`.

### Runtime dashboard (RTD-01/02/03)

| Panel | PromQL | Panel type / unit |
|---|---|---|
| GC collections rate by generation | `sum by (source, service_instance_id, generation) (rate(process_runtime_dotnet_gc_collections_count_total{$F}[$__rate_interval]))` | timeseries, `ops` |
| GC heap size by generation | `sum by (source, service_instance_id, generation) (process_runtime_dotnet_gc_heap_size_bytes{$F})` | timeseries, `bytes` |
| GC heap fragmentation | `sum by (source, service_instance_id, generation) (process_runtime_dotnet_gc_heap_fragmentation_size_bytes{$F})` | timeseries, `bytes` |
| Allocation rate | `sum by (source, service_instance_id) (rate(process_runtime_dotnet_gc_allocations_size_bytes_total{$F}[$__rate_interval]))` | timeseries, `Bps` |
| GC committed memory *(memory proxy — see F-2)* | `sum by (source, service_instance_id) (process_runtime_dotnet_gc_committed_memory_size_bytes{$F})` | timeseries, `bytes` |
| Live object bytes | `sum by (source, service_instance_id) (process_runtime_dotnet_gc_objects_size_bytes{$F})` | timeseries, `bytes` |
| GC pause fraction (sec of GC per sec) | `sum by (source, service_instance_id) (rate(process_runtime_dotnet_gc_duration_nanoseconds_total{$F}[$__rate_interval])) / 1e9` | timeseries, `percentunit` |
| Thread-pool threads | `max by (source, service_instance_id) (process_runtime_dotnet_thread_pool_threads_count{$F})` | timeseries, `short` |
| Thread-pool queue length | `max by (source, service_instance_id) (process_runtime_dotnet_thread_pool_queue_length{$F})` | timeseries, `short` |
| Thread-pool completion rate | `sum by (source, service_instance_id) (rate(process_runtime_dotnet_thread_pool_completed_items_count_total{$F}[$__rate_interval]))` | timeseries, `ops` |
| Exception rate | `sum by (source, service_instance_id) (rate(process_runtime_dotnet_exceptions_count_total{$F}[$__rate_interval]))` | timeseries, `ops` |
| Lock contention rate | `sum by (source, service_instance_id) (rate(process_runtime_dotnet_monitor_lock_contention_count_total{$F}[$__rate_interval]))` | timeseries, `ops` |
| Loaded assemblies / timers | `max by (source, service_instance_id) (process_runtime_dotnet_assemblies_count{$F})` · `max by (…) (process_runtime_dotnet_timer_count{$F})` | timeseries, `short` |
| JIT compile rate | `sum by (source, service_instance_id) (rate(process_runtime_dotnet_jit_methods_compiled_count_total{$F}[$__rate_interval]))` | timeseries, `ops` |
| **Pods reporting** (the honest substitute for "uptime") | `count by (source) (process_runtime_dotnet_gc_collections_count_total{$F, generation="gen0"})` | stat, `short` |
| **Process restarts in range** (uptime proxy) | `sum by (source, service_instance_id) (resets(process_runtime_dotnet_jit_methods_compiled_count_total{$F}[$__range]))` | stat, `short` |

**RTD-02 axes NOT satisfiable (F-2):** *process CPU* — no metric exists, none can be derived; *process uptime* — no `process_start_time_seconds`, only the restart-count proxy above; *working set* — only GC-committed memory, which is a different quantity (and missing on an idle webapi, F-3). The plan should ship the proxies with honest panel titles ("GC committed memory", "Process restarts in range") and **not** label them "Working set" / "Uptime".

### Business / pipeline dashboard (BPD-01/02/03)

**Conservation (BPD-02, "legible at a glance"):** three per-service panels plus one cross-tier gap panel.

```promql
# per service, consumed vs sent overlaid (repeat this shape for keeper_/processor_)
sum by (source) (rate(orchestrator_messages_consumed_total{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval]))
sum by (source) (rate(orchestrator_messages_sent_total{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval]))

# cross-tier conservation gap over the dashboard window — the axis the resilience sweep scores on
  sum(increase(orchestrator_messages_sent_total{service_instance_id=~"$pod"}[$__range]))
- sum(increase(processor_messages_consumed_total{service_instance_id=~"$pod"}[$__range]))

# per-processor bottleneck view (the analyzer's historical shape, current names)
  sum by (processorId) (rate(orchestrator_messages_sent_total[$__rate_interval]))
- sum by (processorId) (rate(processor_messages_consumed_total[$__rate_interval]))
```

**Fault indicators — each on its own panel (BPD-02), each `or vector(0)`-guarded (F-4):**
```promql
sum(increase(orchestrator_step_unresolved_total{service_instance_id=~"$pod"}[$__range])) or vector(0)
sum(increase(processor_spawn_dropped_total{service_instance_id=~"$pod"}[$__range]))    or vector(0)
# keeper: no reinject/drop counter exists (F-5). Substitutes:
sum(increase(keeper_messages_consumed_total{service_instance_id=~"$pod"}[$__range]))
  - sum(increase(keeper_messages_sent_total{service_instance_id=~"$pod"}[$__range]))   or vector(0)
sum by (service_instance_id) (rate(keeper_l2_probe_total{service_instance_id=~"$pod"}[$__rate_interval]))  # heartbeat, must be > 0
```
Use `stat` panels with thresholds (green 0 / red >0) for the two true fault counters — a stat with `or vector(0)` renders "0" rather than "No data", which is what makes VER-01 pass honestly.

**Processor per-image separation (VAR-04, corrected — F-6):** add `identityName` to the group-by/legend on processor panels:
```promql
sum by (identityName, service_instance_id) (rate(processor_messages_consumed_total{source="processor", service_instance_id=~"$pod"}[$__rate_interval]))
```

**WebApi row (BPD-03) — ASP.NET Core request metrics only.** Live-verified labels on `http_server_request_duration_seconds_*`: `http_route`, `http_request_method`, `http_response_status_code`, `url_scheme`, `network_protocol_version`, `otel_scope_name="Microsoft.AspNetCore.Hosting"`.
```promql
# request rate by route
sum by (http_route) (rate(http_server_request_duration_seconds_count{source="webapi", service_instance_id=~"$pod"}[$__rate_interval]))
# p95 latency by route
histogram_quantile(0.95, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{source="webapi", service_instance_id=~"$pod"}[$__rate_interval])))
# status-code mix
sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{source="webapi", service_instance_id=~"$pod"}[$__rate_interval]))
# error ratio
  sum(rate(http_server_request_duration_seconds_count{source="webapi", http_response_status_code=~"5.."}[$__rate_interval]))
/ sum(rate(http_server_request_duration_seconds_count{source="webapi"}[$__rate_interval]))
# in-flight + kestrel
sum by (service_instance_id) (http_server_active_requests{source="webapi"})
sum by (service_instance_id) (kestrel_active_connections{source="webapi"})
sum by (service_instance_id) (kestrel_queued_connections{source="webapi"})
# routing (available, low value): aspnetcore_routing_match_attempts_total
```
**Caveat:** the collector drops every `http.server.request.duration` datapoint whose `http.route` starts with `/health/` (`k8s/02-configmaps.yaml:64-69`). Since the harness only calls `/orchestration/start` and `/stop`, the live webapi request series is **2 series with `_count` = 2** on an idle stack. These panels are empty unless traffic is driven — relevant to VER-01.

### Optional: MassTransit as a supporting infra row

Not required by any REQ-ID, but present on all three consoles and useful. Labels verified live: `messaging_masstransit_message_type`, `_consumer_type`, `_destination`, `_service`.
```promql
sum by (source, messaging_masstransit_message_type) (rate(messaging_masstransit_consume_ea_total{source=~"$source"}[$__rate_interval]))
sum by (source, messaging_masstransit_exception_type) (rate(messaging_masstransit_consume_errors_ea_total{source=~"$source"}[$__rate_interval])) or vector(0)
histogram_quantile(0.95, sum by (le, source) (rate(messaging_masstransit_consume_duration_milliseconds_bucket{source=~"$source"}[$__rate_interval])))
```
Note the MassTransit histogram unit is **milliseconds**, unlike the ASP.NET Core one (seconds).

## Verification Mechanics (Q8)

All endpoints below were exercised live this session against `grafana/grafana:12.3.9`.

### VER-01 — every panel renders non-empty, both dropdowns enumerate correctly

Do **not** eyeball the UI. Extract every `targets[].expr` from the checked-in JSON, interpolate the variables, and run each expression **through Grafana's datasource proxy** — that proves the datasource wiring *and* the PromQL in one assertion.

```bash
GF=http://127.0.0.1:3000
AUTH='-u admin:admin'

# 1. Grafana up + DB migrated
curl -sf $GF/api/health | jq -e '.database == "ok"'

# 2. Datasource provisioned, default, read-only, and actually reachable
curl -sf $AUTH $GF/api/datasources/uid/skp-prometheus | jq -e '.isDefault and .readOnly'
curl -sf $AUTH $GF/api/datasources/uid/skp-prometheus/health | jq -e '.status == "OK"'

# 3. Both dashboards provisioned
curl -sf $AUTH "$GF/api/search?type=dash-db" | jq -e '[.[].uid] | contains(["skp-runtime","skp-business"])'

# 4. Dropdown membership: source == exactly the four classes
curl -sf $AUTH -G "$GF/api/datasources/proxy/uid/skp-prometheus/api/v1/query" \
  --data-urlencode 'query=group by (source) (process_runtime_dotnet_gc_collections_count_total)' \
  | jq -e '[.data.result[].metric.source] | sort == ["keeper","orchestrator","processor","webapi"]'

# 5. Dropdown membership: pods per class (superset assertion — see OQ-2)
curl -sf $AUTH -G "$GF/api/datasources/proxy/uid/skp-prometheus/api/v1/label/service_instance_id/values" \
  --data-urlencode 'match[]=process_runtime_dotnet_gc_collections_count_total{source=~"(keeper)"}' \
  --data-urlencode "start=$(($(date +%s)-1800))" --data-urlencode "end=$(date +%s)"
#   assert: every live keeper pod from `kubectl get pods` IS PRESENT (subset, not equality)

# 6. Per-panel non-emptiness: for each expr in the JSON, with $source/$pod -> ".+"
curl -sf $AUTH -G "$GF/api/datasources/proxy/uid/skp-prometheus/api/v1/query" \
  --data-urlencode "query=<interpolated expr>" | jq -e '.data.result | length > 0'
```
Step 6 must classify each panel into **must-be-non-empty** (all runtime panels; orchestrator/processor consumed+sent; keeper l2 probe; webapi request panels *if* traffic was driven) vs **expected-zero-guarded** (the fault counters and any `or vector(0)` expression, where the assertion is "returns exactly one sample" rather than ">0").

**Driving the traffic:** reuse the existing seeder + `POST /orchestration/start` path from `scripts/phase-80-harness.ps1` (STEP C seed, STEP E activation). A short happy-path run is enough to populate `orchestrator_*`, `processor_*` and the webapi request series. Note `keeper_messages_consumed/sent` require a **recovery** event — only a fault-injection scenario from `scripts/phase-81-sweep.ps1` produces them; a happy path will not.

### VER-02 — pod delete/recreate reproduces dashboards from repo JSON

```bash
# baseline
BEFORE=$(curl -sf $AUTH $GF/api/dashboards/uid/skp-runtime | jq -S '.dashboard')

kubectl -n skp delete pod -l app=grafana
kubectl -n skp rollout status deployment/grafana --timeout=120s
# restart the port-forward, wait for /api/health

AFTER=$(curl -sf $AUTH $GF/api/dashboards/uid/skp-runtime | jq -S '.dashboard')
[ "$BEFORE" = "$AFTER" ]     # byte-identical dashboard spec

# and prove it came from the repo, not a DB
curl -sf $AUTH $GF/api/dashboards/uid/skp-runtime \
  | jq -e '.meta.provisioned == true and .meta.provisionedExternalId == "runtime.json"'

# prove there is no PVC anywhere in the namespace attributable to grafana
kubectl -n skp get pvc -o json | jq -e '[.items[].metadata.name] | map(test("grafana")) | any | not'
kubectl -n skp get deploy grafana -o json \
  | jq -e '.spec.template.spec.volumes[] | select(.name=="grafana-storage") | has("emptyDir")'

# prove the UI cannot become the source of truth
curl -s $AUTH -X POST -H 'Content-Type: application/json' \
  -d '{"dashboard":{"uid":"skp-runtime","title":"tamper","schemaVersion":39,"panels":[]},"overwrite":true}' \
  $GF/api/dashboards/db | jq -e '.message == "Cannot save provisioned dashboard"'
```
The last assertion was **verified live and returned exactly that message**.

### Repo conventions to reuse

- Verification script: `scripts/phase-87-dashboards-verify.ps1`, following the shape of `scripts/phase-83-ha-failover.ps1` (single-purpose live proof, structured exit codes).
- Machine-readable output: `analyzer-reports/phase-87-dashboards.json` — matches the existing `phase-83-ha.json` / `phase-85-summary.json` convention, so the verdict is reviewable after the fact.
- Long runs must be **detached** (`Start-Process` + a separate watcher loop) per MEMORY `long-sweep-detached-process` — though this verification is short (< 2 min) and does not need it.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---|---|---|---|
| Getting dashboards into Grafana | A `curl POST /api/dashboards/db` bootstrap Job/initContainer | File provisioning (`providers:` + ConfigMap) | API-imported dashboards are DB-owned and editable — the exact opposite of DASH-03. Provisioned ones report `meta.provisioned: true` and reject saves. |
| Making the datasource default | UI click-through, or a startup `curl POST /api/datasources` | `apiVersion: 1` datasources YAML with `isDefault: true`, `editable: false` | DASH-02 says "at startup, from config". Provisioning is also idempotent across pod recreates. |
| Portable datasource reference | `sed`-ing the UID at deploy time, or `__inputs`/`__requires` import blocks | A `datasource`-type template variable + `"uid": "${datasource}"` | `__inputs` is the *share/export* format and **fails** file provisioning. |
| "All" selection semantics | `allValue: ".*"` | Leave `allValue: null` | `=~".*"` matches series where the label is **absent** — measured 1044 series / 268 pods vs 159 / 51 (F-8). |
| Rate windows | Hardcoded `[5m]` | `$__rate_interval` + datasource `timeInterval: "15s"` | Auto-adapts to the panel width and never under-samples a 15 s scrape. |
| Dashboard JSON in YAML | Inline block scalar inside a hand-authored ConfigMap | Real `.json` files + `configMapGenerator` (`disableNameSuffixHash: true`) | Keeps DASH-03's "repo JSON is the single source of truth" literally true, and makes JSON lint/`jq` assertions possible. |
| Grafana state durability | A PVC "just in case" | `emptyDir` | The absence of persistence IS the requirement (DASH-03/VER-02). |
| Detecting live pods | Parsing `kubectl get pods` inside the dashboard | Prometheus instant query / `label_values` | But be aware of the F-7 staleness asymmetry between the two. |

**Key insight:** every "clever" shortcut in this domain (API import, UID rewriting, `.*` catch-alls) converts a *declarative, reproducible* dashboard into an *imperative, drifting* one — which is precisely the failure mode DASH-03 and VER-02 exist to prevent.

## Common Pitfalls

### Pitfall 1: writing panels against `dotnet_*`
**What goes wrong:** every runtime panel is empty, but the metric name autocompletes in Grafana's query editor (because `MCP.Terminal` put it in the index).
**Why:** net8.0 + `OpenTelemetry.Instrumentation.Runtime` 1.15.0 emits the legacy `process.runtime.dotnet.*` family.
**Avoid:** use `process_runtime_dotnet_*` exclusively; add a JSON header comment noting a net9.0 upgrade would rename the family.
**Warning sign:** query returns data only when `source` is unset in the selector.

### Pitfall 2: mounting a ConfigMap over `/etc/grafana/provisioning`
**What goes wrong:** the `alerting/`, `plugins/`, `access-control/`, `notifiers/` subdirectories vanish; Grafana logs provisioning errors at boot.
**Avoid:** mount only at `/etc/grafana/provisioning/datasources` and `/etc/grafana/provisioning/dashboards`; keep the dashboard JSON out of the provisioning tree entirely (`/etc/grafana/dashboards`).

### Pitfall 3: nesting the dashboard-JSON ConfigMap inside the `emptyDir`
**What goes wrong:** mounting the JSON ConfigMap at `/var/lib/grafana/dashboards` (the Helm-chart convention) nests a ConfigMap volume inside the `emptyDir` mount. It usually works, but the mount ordering is an implementation detail and it makes the "no persistence" story confusing to read.
**Avoid:** use `/etc/grafana/dashboards` — a sibling path, no nesting.

### Pitfall 4: `allValue: ".*"`
**What goes wrong:** "All" silently includes 15 days of dead pods, hermetic-test emitters (`source="console-test"`), and unrelated host processes (`MCP.Terminal`) that carry no `source` label at all.
**Avoid:** `allValue: null`. Also scope the `source` variable's `label_values()` to a stack-specific metric, never the bare one-arg form.

### Pitfall 5: expecting fault-counter panels to have data
**What goes wrong:** VER-01 fails on a *healthy* stack because `orchestrator_step_unresolved_total` and `processor_spawn_dropped_total` have no series.
**Why:** OTel counters materialize only after the first increment.
**Avoid:** `... or vector(0)` on a `stat` panel; classify these panels as "expected-zero" in the verification script.

### Pitfall 6: expecting `keeper_messages_*` on a happy path
**What goes wrong:** the keeper conservation panel stays empty through a normal round-trip.
**Why:** those counters increment only in `RecoveryConsumerBase.Consume` / `CountSent` — i.e. only during a recovery event. Confirmed: they are absent from the entire 15-day `__name__` index.
**Avoid:** either drive a fault scenario (`scripts/phase-81-sweep.ps1`) for VER-01, or explicitly scope the keeper conservation panel as "expected-zero unless recovery traffic".

### Pitfall 7: `job` / `instance` are not what you think
**What goes wrong:** grouping by `instance` collapses everything into `otel-collector:8889`.
**Why:** Prometheus's scrape labels win; the collector's own `job`/`instance` (which mirror `service_name`/`service_instance_id`) are renamed to `exported_job` / `exported_instance`.
**Avoid:** group by `source` / `service_name` / `service_instance_id` only.

### Pitfall 8: assuming Grafana 13 is a drop-in
**What goes wrong:** a human iterates on a panel in the UI, exports, commits — and the next pod recreate fails to provision with `dashboard appears to be in v2 format`.
**Avoid:** pin 12.3.9. (Hand-authored classic JSON *does* provision on 13.1.1 — verified — so this is about the authoring loop, not a hard block.)

### Pitfall 9: the `USERPC` / `console-test` host emitters
**What goes wrong:** the pod dropdown lists `USERPC` and GUID-shaped instance ids alongside real pods.
**Why:** the host test suite and the `~FanOutSeeder` in-proc WebApi push OTLP to `localhost:4317`, which the `phase-80-up.ps1` forward tunnels straight into the cluster collector (STATE.md records this as an "open Source-collision risk").
**Avoid:** scoping the pod variable by `{source=~"$source"}` filters most of it; note that a host run that sets a real `source` value would still leak in.

### Pitfall 10: touching the 8-forward table
**What goes wrong:** the 7-scenario resilience sweep's bring-up changes shape and its exit-code contract gets perturbed.
**Avoid:** the Phase-87 verification script owns its own `svc/grafana 3000:3000` forward. (See Q4 option (a).)

## Code Examples

### Provisioning ConfigMaps (verified working)

```yaml
# --- ConfigMap: grafana-datasources -> /etc/grafana/provisioning/datasources ---
apiVersion: 1
prune: true
datasources:
  - name: Prometheus
    uid: skp-prometheus
    type: prometheus
    access: proxy
    url: http://prometheus:9090
    isDefault: true
    editable: false
    jsonData:
      httpMethod: POST
      timeInterval: "15s"
```
```yaml
# --- ConfigMap: grafana-dashboard-provider -> /etc/grafana/provisioning/dashboards ---
apiVersion: 1
providers:
  - name: skp
    orgId: 1
    folder: SKP
    type: file
    disableDeletion: true
    allowUiUpdates: false
    updateIntervalSeconds: 30
    options:
      path: /etc/grafana/dashboards
      foldersFromFilesStructure: false
```

### kustomize entry for the dashboard JSONs (verified with `kubectl kustomize`)

```yaml
# k8s/kustomization.yaml  (additions only)
generatorOptions:
  disableNameSuffixHash: true          # MANDATORY — the Deployment references the ConfigMap by name
  labels:
    app.kubernetes.io/part-of: skp

configMapGenerator:
  - name: grafana-dashboards
    files:
      - dashboards/skp-runtime.json
      - dashboards/skp-business.json

resources:
  # ... existing, with 23-grafana.yaml inserted after 21-prometheus.yaml
  - 23-grafana.yaml
```
Produces `data: { "skp-runtime.json": "...", "skp-business.json": "..." }` (keys are basenames).

### Deployment volume block

```yaml
volumeMounts:
  - { name: gf-datasources, mountPath: /etc/grafana/provisioning/datasources, readOnly: true }
  - { name: gf-provider,    mountPath: /etc/grafana/provisioning/dashboards,  readOnly: true }
  - { name: gf-dashboards,  mountPath: /etc/grafana/dashboards,               readOnly: true }
  - { name: grafana-storage, mountPath: /var/lib/grafana }
volumes:
  - { name: gf-datasources, configMap: { name: grafana-datasources } }
  - { name: gf-provider,    configMap: { name: grafana-dashboard-provider } }
  - { name: gf-dashboards,  configMap: { name: grafana-dashboards } }
  - { name: grafana-storage, emptyDir: {} }        # DASH-03 — no PVC, deliberately disposable
```

### The exact live-verification calls that worked

```bash
$ curl -s http://127.0.0.1:3001/api/health
{"database":"ok","version":"12.3.9","commit":"1f48319059aae4ee4dddedb8c265f23fd008c19c"}

$ curl -s -u admin:admin http://127.0.0.1:3001/api/datasources/uid/skp-prometheus/health
{"details":{"application":"Prometheus",...},"message":"Successfully queried the Prometheus API.","status":"OK"}

$ curl -s -u admin:admin http://127.0.0.1:3001/api/dashboards/uid/skp-runtime | jq '.meta | {provisioned, provisionedExternalId}'
{"provisioned": true, "provisionedExternalId": "runtime.json"}

$ curl -s -u admin:admin -G \
    "http://127.0.0.1:3001/api/datasources/proxy/uid/skp-prometheus/api/v1/query" \
    --data-urlencode 'query=count by (source) (process_runtime_dotnet_gc_collections_count_total)'
{"status":"success","data":{"resultType":"vector","result":[
  {"metric":{"source":"webapi"},"value":[...,"3"]},
  {"metric":{"source":"keeper"},"value":[...,"6"]},
  {"metric":{"source":"processor"},"value":[...,"6"]},
  {"metric":{"source":"orchestrator"},"value":[...,"9"]}]}}

$ curl -s -u admin:admin -X POST -H 'Content-Type: application/json' \
    -d '{"dashboard":{"uid":"skp-runtime","title":"hacked","schemaVersion":39,"panels":[]},"overwrite":true}' \
    http://127.0.0.1:3001/api/dashboards/db
{"message":"Cannot save provisioned dashboard"}
```

**Benign log noise to expect:** every datasource-proxy call logs
`level=error msg="plugin route is not covered by RBAC and disabled default falling back to 403" route=/api/datasources/proxy/uid/... datasource=prometheus`
— yet the call returns HTTP 200 with correct data. Do not treat this line as a failure signal in the verification script.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|---|---|---|---|
| `graph` (Angular) panels | `timeseries` panels | Grafana 11.0 (Angular removed) | Never author `"type": "graph"` |
| `"datasource": "Prometheus"` (name string) | `"datasource": {"type":"prometheus","uid":"..."}` | Grafana 8.3+ | Object form only |
| Sideloading dashboards via `POST /api/dashboards/db` | File provisioning with `allowUiUpdates: false` | long-standing best practice | Only provisioning gives `meta.provisioned` + save rejection |
| Classic dashboard JSON as the export default | **V2 Resource** (Kubernetes-kind) export default | Grafana 12.2; Classic export **removed** in 13.0 | Pin 12.x to keep the UI→file authoring loop |
| `process.runtime.dotnet.*` (OTel Runtime instrumentation) | `dotnet.*` (built-in `System.Runtime` meter on .NET 9+) | .NET 9 / newer instrumentation | **This stack is still on the OLD names** — net8.0 + Runtime 1.15.0 |
| `commonLabels:` in kustomize | `labels:` with `includeSelectors: false` | kustomize v5 | Already adopted (`k8s/kustomization.yaml:38-42`) |

**Deprecated / outdated in this context:**
- `label_values(label)` one-argument form — works, but returns 15 days of unscoped history including `console-test`. Always pass the metric.
- `interval` / `intervalFactor` on Prometheus targets — superseded by `$__rate_interval`.
- `OpenTelemetry.Instrumentation.Process` — the only package that would supply CPU/memory-usage, still **pre-GA** (`1.17.0-rc.1` on NuGet as of 2026-07-27), and out of scope regardless.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|---|---|---|
| A1 | ConfigMap max size is 1 MiB (etcd request-size ceiling) | Grafana Deployment Shape | Low — measured dashboards are ~40-120 KB, ~10x under. If wrong the apply fails loudly at deploy time. |
| A2 | Grafana 12.3.9 will remain a supported/patched line for the life of this milestone | Standard Stack | Low — cosmetic; 13.1.1 was verified to provision the same JSON, so the upgrade path is open. |
| A3 | Two dashboards with 15-25 panels each produce 40-120 KB of JSON | Grafana Deployment Shape | Low — only affects the ConfigMap-split decision, which is deferrable. |
| A4 | `resets()` over a monotonic JIT counter is a reliable process-restart proxy | Panel/PromQL Design | Medium — a counter reset can also occur on collector `metric_expiration` (5 min default). Validate before shipping it as an "uptime" substitute, or drop the panel. |
| A5 | The webapi's three missing GC gauges will populate once it performs GCs | FINDING F-3 | Medium — if they never populate under harness traffic, VER-01 needs those panels marked webapi-exempt. Verify during the live run. |
| A6 | Adding a 9th port-forward to `phase-80-up.ps1` would perturb the resilience sweep | Grafana Deployment Shape | Low — the recommendation (option (a), self-owned forward) avoids the question entirely. |

## Open Questions

1. **OQ-1 — VER-01 vs. fault counters that are legitimately empty.**
   - *What we know:* `orchestrator_step_unresolved_total`, `processor_spawn_dropped_total`, `keeper_messages_consumed/sent_total` have zero live series on a healthy stack (F-4). `or vector(0)` on a `stat` panel makes them render "0" instead of "No data".
   - *What's unclear:* whether VER-01's "every panel renders non-empty" is satisfied by a guarded `0`, or whether the user wants a fault scenario driven so the counters actually fire.
   - *Recommendation:* ship `or vector(0)` guards, and split VER-01's assertion into **must-be->0** vs **must-return-exactly-one-sample** classes. Surface this to the user in discuss-phase — it is a requirement-interpretation decision, not a technical one. If they want real fault data, the plan needs a `scripts/phase-81-sweep.ps1` fault scenario as a VER-01 precondition, which is a materially bigger task.

2. **OQ-2 — VAR-02 (`label_values()`) vs. VER-01 ("the live pod set").**
   - *What we know:* `label_values()` returned 4 keeper pods when 2 were alive, even at a 60-second window (F-7). An instant query returned exactly the 8 live pods, matching `kubectl` 8/8. This is Prometheus index granularity, not a Grafana bug — there is no setting that fixes it.
   - *What's unclear:* which requirement yields.
   - *Recommendation:* keep `label_values()` (VAR-02 is explicit and it is the idiomatic form), and **soften VER-01's dropdown assertion to a superset check** — "every live pod appears in the dropdown". Offer `query_result(count by (service_instance_id) (...))` + a `regex` extractor as an alternative if the user prefers an exact live list and is willing to amend VAR-02. Needs a user decision.

3. **OQ-3 — RTD-02's CPU / uptime / working-set axes (F-2).**
   - *What we know:* none of the three exists. The only routes to them are a `src/` change (forbidden) or a Prometheus scrape-config change (forbidden).
   - *Recommendation:* ship honestly-titled proxies ("GC committed memory", "Process restarts in range", "Pods reporting") and record the gap in the phase VERIFICATION as a known, accepted limitation. RTD-02's "*where the instrumentation provides it*" clause already licenses this, but it should be an explicit, visible decision rather than a silent omission.

4. **OQ-4 — BPD-02's "keeper reinject/drop outcomes" panel (F-5).**
   - *What we know:* no such counter exists; the legacy one was deliberately deleted in Phase 74. The signal lives only in Elasticsearch (`attributes.ReinjectOutcome`), and ES panels are out of scope.
   - *Recommendation:* substitute the keeper **consumed − sent gap** panel plus the `keeper_l2_probe_total` heartbeat, and rename the panel accordingly. Needs the user to accept the substitution.

5. **OQ-5 — Grafana version pin: 12.3.9 vs 13.1.1.**
   - *What we know:* both provision hand-authored classic JSON correctly (both verified live). Only the UI-export round-trip differs.
   - *Recommendation:* 12.3.9. If the user intends to author every panel by hand in the editor and never touch the UI, 13.1.1 is equally valid and newer. Low-stakes, but worth one line of confirmation.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|---|---|---|---|---|
| Docker Desktop + k8s cluster (`skp` ns) | DASH-01, VER-01, VER-02 | ✓ | 14 pods Running | — |
| `kubectl` | apply, port-forward, pod delete | ✓ | v1.34.1 | — |
| kustomize (bundled) | `apply -k`, `configMapGenerator` | ✓ | v5.7.1 | — |
| In-cluster Prometheus | DASH-02, all panels | ✓ | `prom/prometheus:v3.11.3`, healthy, 15d retention | — |
| OTel Collector | label promotion | ✓ | running 8d, `resource_to_telemetry_conversion: true` | — |
| `grafana/grafana:12.3.9` image | DASH-01 | ✓ | pulled + run locally this session | 13.1.1 (also verified) |
| `curl` | verification script | ✓ | present | `Invoke-WebRequest` in PowerShell |
| `jq` | verification assertions | ✗ | — | `python -m json.tool` / PowerShell `ConvertFrom-Json` — used throughout this research |
| `ctx7` (Context7 CLI) | doc lookup | ✗ | — | WebFetch on grafana.com + raw GitHub source (used) |
| `slopcheck` | package legitimacy | ✗ | — | N/A — no language packages added; image verified by execution |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** `jq` (use `python -m json.tool` / `ConvertFrom-Json`, matching this repo's PowerShell harness style); `ctx7` (WebFetch + GitHub raw source, which yielded exact line-level answers).

## Validation Architecture

### Test Framework

| Property | Value |
|---|---|
| Framework | **No .NET test project applies.** This phase adds zero C#. Validation is (a) static YAML/JSON gates via `kubectl`/`kustomize`/JSON parse, and (b) a live PowerShell verification script following `scripts/phase-83-ha-failover.ps1` |
| Config file | none — see Wave 0 |
| Quick run command | `kubectl kustomize k8s/ > /dev/null && for f in k8s/dashboards/*.json; do python -m json.tool "$f" > /dev/null; done` |
| Full suite command | `pwsh scripts/phase-87-dashboards-verify.ps1` (writes `analyzer-reports/phase-87-dashboards.json`) |

The existing hermetic suite (`BaseApi.Tests.exe --filter-not-trait Category=RealStack`) carries ~23-24 known baseline failures unrelated to any phase (MEMORY `hermetic-preexisting-failures`) and touches nothing this phase changes. **Do not add Phase-87 assertions to it** — no `src/` code changes means no unit-testable surface.

### Phase Requirements → Test Map

| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|---|---|---|---|---|
| DASH-01 | Grafana Deployment+Service come up via `apply -k` | live | `kubectl -n skp rollout status deployment/grafana --timeout=120s` | ❌ Wave 0 |
| DASH-01 | Reachable by port-forward | live | `curl -sf $GF/api/health \| jq -e '.database=="ok"'` | ❌ Wave 0 |
| DASH-02 | Prometheus is the default datasource, from config | live | `curl $GF/api/datasources/uid/skp-prometheus \| jq -e '.isDefault and .readOnly'` + `/health` → `status=="OK"` | ❌ Wave 0 |
| DASH-03 | No PVC; `emptyDir` only | hermetic | `kubectl kustomize k8s/ \| grep -A2 grafana-storage \| grep emptyDir` **and** no `kind: PersistentVolumeClaim` for grafana | ❌ Wave 0 |
| DASH-03 | UI cannot become the source of truth | live | `POST /api/dashboards/db` → `{"message":"Cannot save provisioned dashboard"}` | ❌ Wave 0 |
| DASH-04 | No hardcoded datasource UID in either JSON | hermetic | `! grep -q 'skp-prometheus' k8s/dashboards/*.json` **and** every `datasource.uid` equals `${datasource}` | ❌ Wave 0 |
| RTD-01 | Panels use only `process_runtime_dotnet_*` | hermetic | extract all `targets[].expr`; assert none matches `\bdotnet_` and none matches a non-runtime family | ❌ Wave 0 |
| RTD-01 | All four classes present | live | `group by (source) (process_runtime_dotnet_gc_collections_count_total)` → exactly the 4 | ❌ Wave 0 |
| RTD-02 | GC / memory / threadpool / exception panels non-empty | live | each `expr` via `/api/datasources/proxy/.../api/v1/query` → `result \| length > 0` | ❌ Wave 0 |
| RTD-03 | Panels break down per pod | hermetic | every runtime `expr` contains `service_instance_id` in its `by (...)` clause **and** `service_instance_id=~"$pod"` in its selector | ❌ Wave 0 |
| BPD-01 | All nine counters referenced | hermetic | grep the business JSON for each of the 9 names | ❌ Wave 0 |
| BPD-02 | Fault indicators are dedicated panels | hermetic | assert a panel whose only target references `orchestrator_step_unresolved_total`, ditto `processor_spawn_dropped_total` | ❌ Wave 0 |
| BPD-02 | Fault panels render `0`, not "No data" | live | each fault `expr` → `result \| length == 1` (the `or vector(0)` guard) | ❌ Wave 0 |
| BPD-03 | WebApi via ASP.NET Core metrics only | hermetic | no `*_messages_*`/`*_total` domain counter appears with `source="webapi"`; `http_server_request_duration_seconds_*` does | ❌ Wave 0 |
| VAR-01 | `source` var is `label_values()`, multi, includeAll, no custom allValue | hermetic | JSON assertion on `templating.list[name=="source"]`: `multi==true`, `includeAll==true`, `allValue==null`, `query` starts `label_values(` | ❌ Wave 0 |
| VAR-02 | pod var likewise over `service_instance_id` | hermetic | same shape assertion | ❌ Wave 0 |
| VAR-03 | pod var chained to `$source`; every panel honors both | hermetic | pod var `query` contains `source=~"$source"`; **every** `targets[].expr` contains both `source=~"$source"` and `service_instance_id=~"$pod"` | ❌ Wave 0 |
| VAR-03 | Chaining actually filters | live | `label_values` with `match[]={source=~"(keeper)"}` returns only keeper-prefixed pods | ❌ Wave 0 |
| VAR-04 | Per-image discrimination present | hermetic | processor business panels group by/legend on `identityName` (**not** `service_name` — F-6) | ❌ Wave 0 |
| VER-01 | Every panel non-empty with traffic | live | full expr sweep after a happy-path seed+start run | ❌ Wave 0 |
| VER-02 | Pod delete/recreate reproduces from repo | live | dashboard spec byte-identical before/after `kubectl delete pod -l app=grafana`; `meta.provisioned==true` | ❌ Wave 0 |

### Sampling Rate

- **Per task commit (hermetic, < 5 s):** `kubectl kustomize k8s/ > /dev/null` (whole-stack build) + JSON well-formedness on every `k8s/dashboards/*.json` + the DASH-04 / VAR-01..04 / RTD-03 grep-and-parse assertions. Fast enough to run on every edit; catches the overwhelmingly likely failure modes (bad JSON, forgotten `$pod` filter, leaked hardcoded UID).
- **Per wave merge:** the above **plus** `kubectl apply -k k8s/ --dry-run=server` (the repo's existing whole-stack lint gate, `k8s/kustomization.yaml:18-20`) — validates against the live API server without creating anything.
- **Phase gate:** `pwsh scripts/phase-87-dashboards-verify.ps1` against the live cluster with traffic driven, emitting `analyzer-reports/phase-87-dashboards.json`, then `/gsd:verify-work`.

The Nyquist argument: the JSON is edited far more often than the cluster changes, so the *fast* gate must cover JSON-shape errors (sampled every commit) while the *slow* gate covers datasource wiring and data presence (sampled per wave / per phase). Panel-expression correctness is sampled at **both** rates — statically (does it reference a real metric name from the verified inventory?) and dynamically (does it return rows?).

### Wave 0 Gaps

- [ ] `scripts/phase-87-dashboards-verify.ps1` — the live VER-01/VER-02 proof (covers DASH-01/02/03, RTD-01/02, BPD-02, VAR-03, VER-01, VER-02). Model on `scripts/phase-83-ha-failover.ps1`; owns its own `svc/grafana 3000:3000` port-forward.
- [ ] `scripts/phase-87-dashboard-lint.ps1` (or an inline block in the verify script) — the hermetic JSON gate: well-formedness, `schemaVersion`, no hardcoded datasource UID, every `expr` carries both variable filters, every referenced metric name is in the Phase-87 verified inventory, template-variable shape assertions. Covers DASH-04, RTD-01, RTD-03, BPD-01, BPD-03, VAR-01, VAR-02, VAR-03, VAR-04.
- [ ] `analyzer-reports/phase-87-dashboards.json` — verdict artifact (matches the `phase-83-ha.json` convention).
- [ ] Metric-name allowlist constant — the verified inventory from this document, embedded in the lint script so a typo'd metric name fails hermetically rather than as an empty panel.
- [ ] Framework install: **none** (PowerShell + `kubectl` + `curl` already present).

## Security Domain

`security_enforcement` is not set in `.planning/config.json` (absent = enabled), so this section is included.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---|---|---|
| V2 Authentication | partial | Grafana admin basic-auth (`admin`/`admin`) + `GF_AUTH_ANONYMOUS_ENABLED=true` with `Viewer` role. Deliberate dev posture — **auth hardening is explicitly out of scope** (REQUIREMENTS.md:57) and matches the stack's existing no-auth Prometheus/ES posture (`k8s/02-configmaps.yaml:157-158`). |
| V3 Session Management | no | Grafana-managed; no custom session code. |
| V4 Access Control | yes | `editable: false` on the datasource + `allowUiUpdates: false` / `disableDeletion: true` on the dashboard provider make Grafana state effectively read-only. Anonymous role is `Viewer`, never `Editor`/`Admin`. |
| V5 Input Validation | yes | Dashboard JSON is validated hermetically (parse + shape assertions) before it can reach the cluster; `kubectl apply -k --dry-run=server` validates the manifests. |
| V6 Cryptography | no | No secrets minted; nothing encrypted. The admin password is a literal dev credential by design. |
| V13 API | partial | Verification uses Grafana's own HTTP API over loopback only. |
| V14 Configuration | yes | ClusterIP Service only; host reach exclusively via `kubectl port-forward --address 127.0.0.1` (never `0.0.0.0`), matching the T-80-17 threat control already documented at `scripts/phase-80-up.ps1:121-123`. No Ingress, no NodePort, no TLS termination. |

### Known Threat Patterns for this stack

| Pattern | STRIDE | Standard Mitigation |
|---|---|---|
| Grafana exposed to the LAN via a `0.0.0.0` port-forward bind | Information Disclosure | Bind `--address 127.0.0.1` explicitly, exactly as the existing 8 forwards do (T-80-17) |
| Default `admin`/`admin` reachable from the network | Elevation of Privilege | Confined to loopback by the ClusterIP + loopback-forward posture; auth hardening explicitly deferred (REQUIREMENTS.md:57) |
| A UI edit silently becoming the source of truth | Tampering / Repudiation | `allowUiUpdates: false` + `disableDeletion: true` + `editable: false` — verified to produce `Cannot save provisioned dashboard` |
| Datasource credential leakage into a checked-in dashboard | Information Disclosure | Datasource carries no credentials (unauthenticated in-cluster Prometheus); dashboards reference it only via `${datasource}` |
| Grafana making outbound calls from a dev cluster | Information Disclosure | `GF_ANALYTICS_REPORTING_ENABLED=false`, `GF_ANALYTICS_CHECK_FOR_UPDATES=false` |
| A plugin install pulling arbitrary code at boot | Tampering | No `GF_INSTALL_PLUGINS`; only bundled core datasources are used |
| Container running as root | Elevation of Privilege | Image already defaults to `uid=472`; pin it explicitly via `securityContext.runAsUser: 472` |
| Unbounded label cardinality from `service_instance_id` | Denial of Service | Read-only concern here — the dashboards do not create series. Noted only because the pod dropdown enumerates 268 historical values over 15 d (F-8). |

## Sources

### Primary (HIGH confidence)

- **Live in-cluster Prometheus** (`kubectl -n skp port-forward svc/prometheus 9090:9090`, 2026-07-27) — `/api/v1/label/__name__/values`, `/api/v1/label/{source,service_name,service_instance_id}/values` (scoped + unscoped), `/api/v1/series?match[]=...`, `/api/v1/query`. Source of the entire metric inventory, all label sets, F-1 through F-8.
- **Live `grafana/grafana:12.3.9` and `13.1.1` containers** run against that Prometheus — provisioning behavior, `/api/health`, `/api/datasources`, `/api/datasources/uid/:uid/health`, `/api/search`, `/api/dashboards/uid/:uid` (`meta.provisioned`), `/api/datasources/proxy/uid/:uid/api/v1/{query,label/.../values}`, `POST /api/dashboards/db` rejection, container `id` / `GF_PATHS_*` / provisioning tree.
- **Repo source, read directly:** `src/BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs`, `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs`, `src/BaseConsole.Core/Observability/ConsoleMetricTags.cs`, `src/Orchestrator/Observability/OrchestratorMetrics.cs`, `src/Keeper/Observability/KeeperMetrics.cs`, `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs`, `src/Processor.Sample/appsettings.json`, `Directory.Build.props`, `Directory.Packages.props`, `k8s/*.yaml`, `scripts/phase-80-up.ps1`.
- **Grafana v12.3.0 source (raw.githubusercontent.com):** `public/app/features/dashboard/state/DashboardMigrator.ts:90` (`DASHBOARD_SCHEMA_VERSION = 42`), `public/app/features/templating/template_srv.ts:236-245` (`getAllValue`), `packages/grafana-prometheus/src/escaping.ts:7-27` (`interpolateQueryExpr`), `packages/grafana-prometheus/src/variables.ts:24-58`, `packages/grafana-prometheus/src/metric_find_query.ts:46-89`, `packages/grafana-prometheus/src/types.ts:116-141`.
- **Grafana docs:** [Provision Grafana](https://grafana.com/docs/grafana/latest/administration/provisioning/), [Configure Docker](https://grafana.com/docs/grafana/latest/setup-grafana/configure-docker/), [Data source HTTP API](https://grafana.com/docs/grafana/latest/developers/http_api/data_source/), [Prometheus template variables](https://grafana.com/docs/grafana/latest/datasources/prometheus/template-variables/), [Dashboard JSON model](https://grafana.com/docs/grafana/latest/dashboards/build-dashboards/view-dashboard-json-model/).
- **Registries:** Docker Hub tag API for `grafana/grafana` (13.1.1/13.0.4 @ 2026-07-22, 12.3.9 @ 2026-07-21); NuGet flatcontainer for `OpenTelemetry.Instrumentation.Runtime` (1.17.0) and `.Process` (1.17.0-rc.1).

### Secondary (MEDIUM confidence)

- [grafana/grafana#123607](https://github.com/grafana/grafana/issues/123607) — "file-based provisioning cannot load dashboard JSON exported from the GUI in Grafana 13" (closed/Done; the Classic export option removal is the reported cause). Corroborated by our own live test: hand-authored classic JSON *does* provision on 13.1.1, so the break is scoped to the UI-export round-trip.
- [grafana/grafana#122663](https://github.com/grafana/grafana/issues/122663) — "dashboards saved in grafana 13 cannot be provisioned"; same root cause, affects both static-file and git-sync provisioning.
- `.planning/STATE.md` (quick task `260727-ngv`) — independent record of the `MCP.Terminal` OTLP leak through the `localhost:4317` forward, corroborating F-1's attribution of the `dotnet_*` series.

### Tertiary (LOW confidence)

- The 1 MiB ConfigMap size ceiling (A1) — widely-known etcd limit, not re-verified this session. It fails loudly at apply time if wrong, and the measured JSON sizes are ~10x under it.
- `resets()` as a process-restart proxy (A4) — plausible but not validated against a real pod restart; flagged for validation before shipping.

## Metadata

**Confidence breakdown:**
- **Metric inventory + label reality:** HIGH — enumerated exhaustively from the live in-cluster Prometheus with the stack running and traffic flowing; cross-checked against the instrument registration sites in source.
- **Grafana deployment + provisioning shape:** HIGH — every claim was executed against real 12.3.9 and 13.1.1 containers, including the negative assertions (`Cannot save provisioned dashboard`, `allValue: ".*"` over-matching).
- **Dashboard JSON schema + variable interpolation:** HIGH — read from the pinned Grafana v12.3.0 source, then confirmed end-to-end by provisioning a real dashboard.
- **PromQL panel design:** HIGH for metric names/labels/units (all live-verified); MEDIUM for the specific aggregation shapes, which are conventional but not yet rendered in a browser.
- **Pitfalls:** HIGH — 8 of 10 were observed directly this session rather than recalled.
- **Grafana 12 vs 13 recommendation:** MEDIUM — the technical facts are verified on both; the *preference* rests on GitHub issue reports about the UI export workflow rather than first-hand use of Grafana 13's export UI.

**Research date:** 2026-07-27
**Valid until:** 2026-08-26 (30 days). Two triggers invalidate it sooner: (a) a `net8.0 → net9.0` upgrade, which renames the entire runtime metric family from `process_runtime_dotnet_*` to `dotnet_*`; (b) a Grafana major bump past 13, given the ongoing classic-JSON → v2-resource migration.
