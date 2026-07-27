# Requirements: Steps API — v13.0.0 Observability Dashboards

Milestone goal: make the telemetry the stack already emits *readable*. Two Grafana dashboards — one for **runtime instrumentation** across all four service classes, one for **business/pipeline** counters across the three pipeline services — each filterable by a multi-select `source` (service class) and a multi-select pod (`service_instance_id`) dropdown. Grafana itself is deployed into the existing k8s stack, and the dashboards ship as **checked-in JSON only** (file-provisioned, portable, no Grafana database as a source of truth).

No production source code changes: every label and series the dashboards consume is already emitted today (`resource_to_telemetry_conversion: true` on the collector's Prometheus exporter promotes `source` and `service_instance_id` to Prometheus labels).

> **Amended 2026-07-27** — DASH-01, RTD-01, RTD-02, BPD-02, VAR-04, and VER-01 were corrected against a live-verified metric inventory (the `skp` cluster was up; Grafana 12.3.9 and 13.1.1 containers were run against it). Five requirement premises did not survive contact with the real series: the runtime family name, the absence of CPU/uptime/working-set, the absence of any keeper reinject/drop counter, `service_name`'s uselessness as a processor discriminator, and the unsatisfiability of "every panel non-empty" for fault counters on a healthy stack. Evidence and findings: [`phases/87-grafana-observability-dashboards/87-RESEARCH.md`](phases/87-grafana-observability-dashboards/87-RESEARCH.md) (F-1…F-8, OQ-1…OQ-5).

## v13.0.0 Requirements

### DASH — Grafana Deployment & Provisioning — Phase 87

- [ ] **DASH-01**: Grafana runs in the existing `skp` k8s namespace as a Deployment + Service, brought up by `kubectl apply -k k8s/` alongside the rest of the stack, and reachable by port-forward like the other tools. **Image pinned to `grafana/grafana:12.3.9`** *(amended 2026-07-27, OQ-5)* — both 12.3.9 and 13.1.1 provision hand-authored classic JSON correctly (verified live on both), but Grafana 13 removed the Classic UI export that makes an author-in-the-editor round-trip work.
- [ ] **DASH-02**: The in-cluster Prometheus (`k8s/21-prometheus.yaml`) is provisioned as Grafana's default datasource **at startup, from config** — no manual datasource setup in the UI is ever required.
- [ ] **DASH-03**: Dashboards are file-provisioned from checked-in JSON (dashboard provider + ConfigMap). Grafana carries **no persistent volume**: a pod restart reproduces the identical dashboards from the repo, and any UI-side edit is disposable. The repo JSON is the single source of truth.
- [x] **DASH-04**: Each dashboard JSON is **portable** — the datasource is referenced through a dashboard-level datasource template variable rather than a hardcoded datasource UID, so the same file loads unmodified against another Grafana / another Prometheus datasource.

### RTD — Runtime Instrumentation Dashboard — Phase 87

- [x] **RTD-01**: One dashboard covers **all four service classes** (`webapi`, `orchestrator`, `keeper`, `processor`) from `OpenTelemetry.Instrumentation.Runtime` series only — the one instrumentation every service shares (`BaseApi.Core/DependencyInjection/ObservabilityServiceCollectionExtensions.cs:117`, `BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:129`). **The Prometheus family is `process_runtime_dotnet_*`, NOT `dotnet_*`** *(amended 2026-07-27, research F-1)* — `net8.0` + `OpenTelemetry.Instrumentation.Runtime` 1.15.0 emits the legacy names. The `dotnet_*` series present in this Prometheus originate from `MCP.Terminal` (`otel_scope_name="System.Runtime"`) leaking through the host `localhost:4317` forward, not from this stack; panels written against `dotnet_*` render blank.
- [x] **RTD-02**: It surfaces the runtime health axes the instrumentation actually provides: GC (collections by generation, heap size, heap fragmentation, allocation rate), GC-committed memory, thread pool (thread count, queue length, completed items), exception rate, and JIT activity. **Working set, process CPU, and process uptime do NOT exist in this stack and are substituted by honestly-titled proxies** *(amended 2026-07-27, OQ-3 / research F-2)*: GC-committed memory in place of working set, counter-reset detection ("process restarts in range") in place of uptime, and GC-time-per-second as a GC-pressure proxy in place of CPU. Panels must be titled for what they measure — an empty "CPU" panel is a defect, not a substitution. Obtaining the real three would require `OpenTelemetry.Instrumentation.Process` (pre-GA `1.17.0-rc.1`, a `src/` change) or a Prometheus scrape-config change; both are out of scope, so the gap is recorded as an accepted limitation in the phase VERIFICATION.
- [x] **RTD-03**: Panels break down per selected service class and per selected pod, so a single misbehaving replica is visually separable from its peers.

### BPD — Business / Pipeline Dashboard — Phase 87

- [x] **BPD-01**: One dashboard covers the three pipeline services' domain counters: `orchestrator_messages_consumed_total` / `orchestrator_messages_sent_total` / `orchestrator_step_unresolved_total`, `keeper_messages_consumed_total` / `keeper_messages_sent_total` / `keeper_l2_probe_total`, `processor_messages_consumed_total` / `processor_messages_sent_total` / `processor_spawn_dropped_total`.
- [x] **BPD-02**: The consumed-vs-sent conservation relationship (the axis the resilience sweep scores on) is legible at a glance, and the fault indicators (`orchestrator_step_unresolved_total`, `processor_spawn_dropped_total`) are their own panels rather than buried in a combined graph. **The keeper fault panel is the keeper consumed−sent gap plus a `keeper_l2_probe_total` heartbeat, NOT a reinject/drop-outcome counter** *(amended 2026-07-27, OQ-4 / research F-5)* — no such counter exists: Phase 74 deliberately deleted the legacy label-less reinject-drop counter in favour of the uniform consumed/sent pair, leaving `keeper_messages_consumed`, `keeper_messages_sent`, `keeper_l2_probe` as the keeper's only three instruments (`KeeperMetrics.cs:12-21,50-52`). Reinject outcome now lives solely in Elasticsearch as `attributes.ReinjectOutcome`, and ES panels are out of scope. The panel title must state what it shows (the gap), not imply a drop-outcome breakdown.
- [x] **BPD-03**: The **WebApi is represented by its ASP.NET Core request metrics only** (request rate / duration / status), because it emits no domain counters and no MassTransit meter. Adding business counters to the WebApi is explicitly **out of scope** for this milestone (see Out of Scope).

### VAR — Shared Dropdown Behavior — Phase 87

- [ ] **VAR-01**: Both dashboards expose a **multi-select `source`** template variable (service class: `webapi` / `orchestrator` / `keeper` / `processor`), populated by `label_values()` against live series — not a hardcoded list — with an "All" option.
- [ ] **VAR-02**: Both dashboards expose a **multi-select pod** template variable over the `service_instance_id` label (the pod name, resolved from `POD_NAME`), likewise populated by `label_values()` with an "All" option.
- [x] **VAR-03**: The pod variable is **chained** to the source selection — selecting `keeper` lists only keeper pods — and every panel query honors both selections.
- [x] **VAR-04**: Every processor pod shares the value `processor` on the `source` label (deliberate, so one term matches the whole class). Where per-processor separation matters, the finer-grained discriminator is the **`identityName` datapoint tag** on the business counters (`processor_messages_consumed_total` / `_sent_total`, carrying `{db.Name}_{db.Version}`), with `service_instance_id` (pod name) as the discriminator on the runtime series. ***Amended 2026-07-27 (research F-6): `service_name` CANNOT discriminate processors*** — `Processor.Sample/appsettings.json:9-12` sets `Service:Name = "unresolved"` / `Version = "0.0.0"` and MLBL-03 deliberately keeps that sentinel for the pod's whole life (`ProcessorMetrics.cs:40-43`), so every `source="processor"` series carries `service_name="unresolved_0.0.0"` (confirmed live). Note also that only `processor-sample` is deployed to k8s (`k8s/33-processor-sample.yaml`), so "both processor images" is currently a single-image reality in this cluster.

### VER — Live Verification — Phase 87

- [ ] **VER-01**: With the k8s stack up and pipeline traffic flowing, every panel on both dashboards renders data for each selected service, under a **two-class assertion** *(amended 2026-07-27, OQ-1 / research F-4)*:
  - **Class A — must return at least one real sample:** the runtime panels and the steady-state pipeline counters (`orchestrator_`/`processor_` consumed + sent, `keeper_l2_probe_total`). A "No data" here is a failure.
  - **Class B — must render a guarded value, `0` permitted:** the fault indicators (`orchestrator_step_unresolved_total`, `processor_spawn_dropped_total`) and any counter with no live series on a healthy stack. An OTel counter is not exported to Prometheus until first increment, and a green stack *should* have zero unresolved steps and zero dropped spawns — so these panels carry `or vector(0)` guards and rendering `0` satisfies the requirement. Driving a fault scenario to make them fire is explicitly NOT a precondition of this phase.

  Both dropdowns must enumerate the 4 service classes exactly. The pod dropdown is asserted as a **superset** — every live pod appears — not as an exact live list *(amended 2026-07-27, OQ-2 / research F-7)*: `label_values()` reads the Prometheus index and returns recently-dead pods too (measured: 4 keeper pods listed while 2 were alive, even at a 60-second window). This is index granularity, not a Grafana setting, and VAR-02's `label_values()` mandate is kept as-is.
- [ ] **VER-02**: Portability is demonstrated, not assumed: the dashboards are proven to load from the repo JSON alone after a Grafana pod delete/recreate (no PVC, no manual step).

## Future Requirements (deferred — future Kafka milestone)

- **KIMP-01**: A KafkaImporter processor, triggered by the scheduler as a Mode-2 entry step (`executionId == Guid.Empty`), consumes up to N messages per dispatch from a configured topic and consumer group.
- **KIMP-02**: Each consumed message (a binary file) becomes one execution with a freshly-minted `executionId`; its bytes are written directly to L2 and never carried on the message bus.
- **KIMP-03**: The importer commits each Kafka message's offset ONLY after that file's L2 store + hand-off are confirmed (per-message, fail-loud); a failure leaves the offset uncommitted for redelivery. *(Built on PB-01/02 — the fail-loud hand-off primitive Phase 85 established.)*
- **KIMP-04**: The importer is content-agnostic — moves bytes topic→L2 without inspecting file type/content.
- **KIMP-05**: Kafka runs as a standalone container (not a k8s workload); the importer connects via a configured bootstrap (`host.docker.internal:9092`), advertised listeners set for in-cluster reconnect.
- **KIMP-06**: The importer's `ProcessorConfig` carries topic, consumer group, and batch size N.
- **MANIP-01**: A file-manipulator step reads the imported binary from L2, transforms it (e.g. unzip → operate on the XML/WAV), and emits its output. *(Needs `NextStepHandoff.Data` widened to `byte[]` so real binary can relocate through the orchestrator — the Phase-84 OQ-1 scope fence deferred this.)*
- **KEXP-01**: A KafkaExporter terminal step serializes L2 content back to a ZIP and produces it to a Kafka topic.
- Idempotency key derived from the Kafka message (topic-partition-offset) for true exactly-once import dedup.

## Out of Scope (v13.0.0)

- **Adding business/domain counters (or the MassTransit meter) to the WebApi.** The WebApi registers neither a domain meter nor `AddMeter(InstrumentationOptions.MeterName)`, so it contributes no pipeline counters. Representing it via ASP.NET Core request metrics (BPD-03) was chosen deliberately over widening production instrumentation inside a dashboards milestone.
- **Alerting / notification channels.** Grafana ships read-only dashboards here; alert rules, contact points, and silences are a separate concern.
- **Log (Elasticsearch) panels.** The dashboards are Prometheus-only. ES stays the analyzer's domain (`scripts/phase-68-sweep.ps1` + the analyzer verdict path) and is not surfaced in Grafana this milestone.
- **Grafana persistence, auth hardening, or ingress.** No PVC (that is the point of DASH-03), default local access by port-forward, no TLS/ingress/SSO work.
- **Changing the collector, Prometheus, or any exporter config.** Every label the dropdowns need (`source`, `service_instance_id`, `service_name`) is already promoted today — if a dashboard needs a label that does not exist, that is a finding to report, not a config change to make here.
- **Dashboards for infrastructure containers** (Postgres/Redis/RabbitMQ/Elasticsearch themselves) — the two dashboards cover the four application service classes only.

## Traceability

| REQ-ID | Phase | Status |
|--------|-------|--------|
| DASH-01 | 87 | Not started |
| DASH-02 | 87 | Not started |
| DASH-03 | 87 | Not started |
| DASH-04 | 87 | Not started |
| RTD-01 | 87 | Not started |
| RTD-02 | 87 | Not started |
| RTD-03 | 87 | Not started |
| BPD-01 | 87 | Not started |
| BPD-02 | 87 | Not started |
| BPD-03 | 87 | Not started |
| VAR-01 | 87 | Not started |
| VAR-02 | 87 | Not started |
| VAR-03 | 87 | Not started |
| VAR-04 | 87 | Not started |
| VER-01 | 87 | Not started |
| VER-02 | 87 | Not started |
