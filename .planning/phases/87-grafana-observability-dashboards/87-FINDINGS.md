---
phase: 87
slug: grafana-observability-dashboards
date: 2026-07-27
status: accepted
requirements: [RTD-01, RTD-02, BPD-02, VAR-02, VAR-04, VER-01]
findings: [F-1, F-2, F-3, F-4, F-5, F-6, F-7]
evidence: analyzer-reports/phase-87-dashboards.json
---

# Phase 87 — Accepted Limitations

Nine records of the gaps between what `.planning/REQUIREMENTS.md` asked for and what the
instrumentation actually supplies. Each is an **accepted limitation**, not a defect and not
future work inside this phase: the requirement text was amended on 2026-07-27 against a
live-verified metric inventory (`87-RESEARCH.md`, findings F-1…F-8), and this document is
the record RTD-02 explicitly demands ("*the gap is recorded as an accepted limitation in the
phase VERIFICATION*").

Measured evidence throughout is the live run of `scripts/phase-87-dashboards-verify.ps1`
against the `skp` Docker-Desktop cluster, window `2026-07-27T19:18:03Z` → `19:20:03Z`,
recorded in `analyzer-reports/phase-87-dashboards.json`: **Verdict `Pass`, all ten claim
booleans true, 36 target expressions = 30 Class A + 6 Class B, `EmptyClassAExprs` empty.**

---

## 1. RTD-02 — process CPU, process uptime and working set do not exist (F-2)

**Asked for:** RTD-02 lists "memory / **working set**" and "process **uptime / CPU** where the
instrumentation provides it".

**What actually exists:** none of the three. A full `__name__` enumeration of the live
Prometheus found **no `process_cpu_*`, no `process_start_time_seconds`, and no
`process_memory_working_set`** anywhere. Every runtime series in this stack comes from
`OpenTelemetry.Instrumentation.Runtime` 1.15.0 and is confined to the seventeen
`process_runtime_dotnet_*` names inventoried in F-2 — GC, thread pool, exceptions,
contention, assemblies, timers and JIT. There is no process-level instrumentation at all.

**What shipped instead** — three honestly-titled substitutes on `k8s/dashboards/runtime.json`,
each carrying a `description` that names the absent real metric and why it cannot be obtained:

| Axis asked for | Panel shipped | What it actually measures |
|---|---|---|
| Working set | `GC committed memory` (id 5) | `process_runtime_dotnet_gc_committed_memory_size_bytes` — GC-committed bytes, **not** the OS working set |
| Uptime | `Process restarts in range` (id 17) | `resets()` over the monotonic JIT method counter — a restart *signal*, not an uptime reading (see §2) |
| CPU | `GC time per second` (id 7) | `rate(process_runtime_dotnet_gc_duration_nanoseconds_total)/1e9` — GC *pressure*, deliberately not titled CPU |

A fourth substitute, `Pods reporting` (id 16), is the availability readout that stands where an
uptime gauge would otherwise sit.

**Why the real metrics are unobtainable inside this milestone's fences.** Exactly two routes
exist, and both are fenced out by REQUIREMENTS.md's Out of Scope list:

1. **`OpenTelemetry.Instrumentation.Process`** — the package that emits `process.cpu.*` and
   `process.memory.*`. It is still **pre-GA (`1.17.0-rc.1`)** on NuGet, and wiring it in is a
   `src/` change to `ObservabilityServiceCollectionExtensions.cs` /
   `BaseConsoleObservabilityExtensions.cs`. The milestone's opening premise is "**No production
   source code changes**".
2. **A cAdvisor / kubelet scrape job** in `prometheus.yml`. That is a Prometheus config change,
   and "Changing the collector, Prometheus, or any exporter config" is explicitly out of scope.

**Why the substitution is legitimate rather than a shortfall.** RTD-02's own clause — "*where
the instrumentation provides it*" — licenses it, and the amended requirement makes the
condition sharp: "*Panels must be titled for what they measure — an empty 'CPU' panel is a
defect, not a substitution.*" That condition is mechanically enforced: lint rule **S12** in
`scripts/phase-87-dashboard-lint.ps1` fails the build on any panel title matching
`\b(cpu|uptime|working set)\b`. Measured: **0 such titles across both dashboards**, and the
word `uptime` appears in `runtime.json` only inside descriptions.

**Measured evidence:** all four substitute panels are Class A and all four returned real
samples in the live run (`ClassAPanelsNonEmpty` true, 0 of 30 empty).

---

## 2. RTD-02 / assumption A4 — the `resets()` restart proxy is weaker than its title suggests

**The risk the research flagged.** Assumption A4 rated `resets()` as a restart proxy at
*Medium* confidence: a counter reset can also be produced by the OTel collector's
`metric_expiration` (5 min default) when a series goes quiet, so a stable stack could in
principle show a spurious nonzero value on Panel 17. 87-03 shipped the panel with that caveat
written into its own `description` and handed the question to the live run.

**What the live run measured — the `metric_expiration` risk did NOT materialise.** Measured
immediately after the proof, over the full `[1h]` window, across a period in which **every app
Deployment was in fact rolled**:

```
panel-17 series = 21 ;  nonzero series = 0 of 21
```

Zero spurious values. So the specific hazard A4 named — `metric_expiration` manufacturing a
false restart — did not appear, even on a window that contained real restarts *and* real
series expiry.

**But the measurement exposed a stronger structural caveat, and it is the one that matters.**
The reason the count is zero is not that the panel is well-behaved; it is that **a k8s pod
restart never resets an existing series at all.** A restarted pod comes back with a new
`service_instance_id` and therefore mints an *entirely new* series; the old series is retired
rather than zeroed. Expiry behaves the same way. Consequently:

> **Panel 17 is an *in-process* counter-reset detector, not a k8s restart counter.** For the
> containerised case it reads `0` through a full rolling restart of the stack. It detects a
> counter reset within one pod identity — which in practice means a collector-side reset or an
> in-place runtime anomaly, not the pod churn its title evokes.

**Accepted, not fixed.** The real uptime metric is `process_start_time_seconds`, which §1
establishes does not exist and cannot be added inside this milestone. The honest k8s restart
signal is `kube_state_metrics`' `kube_pod_container_status_restarts_total`, which would require
a new scrape target — the same fenced-off Prometheus config change. The panel is retained
because a restart signal with a stated failure mode is more useful to an operator than no
restart panel, and rule C6 requires the title to exist; its threshold steps are deliberately
loud (green at null, red at 1) because a false red is cheap to dismiss and a missed restart is
not. **Both halves of this finding belong in the panel description at any future revision: the
non-materialisation of the `metric_expiration` risk, and the new-series-per-pod caveat that
supersedes it.**

---

## 3. RTD-01 / F-1 — the runtime family is `process_runtime_dotnet_*`, not `dotnet_*`

**Asked for / assumed:** the phase brief and the ordinary mental model of "modern .NET runtime
metrics" both point at `dotnet_gc_collections_total` / `dotnet_process_memory_working_set_bytes`.

**What actually exists:** `Directory.Build.props:29` pins **`net8.0`** and
`Directory.Packages.props:84` pins **`OpenTelemetry.Instrumentation.Runtime` 1.15.0**. On
net8.0 that package emits the *legacy* `process.runtime.dotnet.*` instruments — confirmed live,
every runtime series carries `otel_scope_name="OpenTelemetry.Instrumentation.Runtime"`,
`otel_scope_version="1.15.0"`. The modern `dotnet.*` names appear only when .NET 9's built-in
`System.Runtime` meter is the source. RTD-01 was amended to state the correct family.

**The `dotnet_*` series in this Prometheus are not ours.** They exist, and they carry data —
but tracing them over the full 15-day retention, the **only** emitter is
`service_name="MCP.Terminal"`, `otel_scope_name="System.Runtime"`: an unrelated .NET 9 host
process leaking into the collector through the `localhost:4317` port-forward (the same leak
STATE.md records for quick task `260727-ngv`). It carries **no `source` label at all**, so it
would never satisfy a dropdown-filtered expression — but a panel written against `dotnet_*`
would look plausible in an editor and render blank in the cluster.

**The forward-looking hazard, recorded deliberately.** A `net8.0` → `net9.0` upgrade — or a
bump of `OpenTelemetry.Instrumentation.Runtime` past the version that switches meters — will
**silently rename the entire family** and blank the whole runtime dashboard. Nothing in
Grafana would report an error; seventeen panels would simply go empty.

**Why this is accepted rather than mitigated in production code.** Pinning or shimming the
metric names is a `src/` change. Instead the hazard is caught **hermetically**: rule **S9/S10**
of `scripts/phase-87-dashboard-lint.ps1` restrict every metric token to a 17-name
`process_runtime_dotnet_*` allowlist, and a dedicated rule **hard-fails on any `\bdotnet_`
reference**. Measured: **0 `\bdotnet_` matches across both dashboards**. A future framework
upgrade that renames the family therefore surfaces as a **lint failure in ~5 seconds**, not as
an empty dashboard discovered weeks later. That trade — detect fast rather than prevent — is
the accepted position.

---

## 4. F-3 — the webapi is missing three GC gauges until it performs its first GC

**What actually exists:** `process_runtime_dotnet_gc_heap_size_bytes`,
`_gc_heap_fragmentation_size_bytes` and `_gc_committed_memory_size_bytes` are all derived from
`GC.GetGCMemoryInfo()`, which yields nothing before the first collection. Research measured all
three present for `keeper`, `orchestrator` and `processor` and **absent for `source="webapi"`**,
whose `process_runtime_dotnet_gc_collections_count_total` read `0` for gen0/gen1/gen2.

**Did they populate during the live run's traffic window? No.** Re-measured against the live
Prometheus after the proof:

```
count by (source) (process_runtime_dotnet_gc_committed_memory_size_bytes)
  → keeper=2  orchestrator=3  processor=2        (webapi absent)
count by (source) (process_runtime_dotnet_gc_heap_size_bytes)
  → keeper=10 orchestrator=15 processor=10       (webapi absent)
count by (source) (process_runtime_dotnet_gc_heap_fragmentation_size_bytes)
  → keeper=10 orchestrator=15 processor=10       (webapi absent)

process_runtime_dotnet_gc_collections_count_total{source="webapi"}
  → baseapi-service-7d59dc679c-mcs29  gen0=0  gen1=0  gen2=0
```

The in-cluster WebApi still has not run a single garbage collection. The traffic the live proof
drove reached the pipeline through the seeder's **in-proc** WebApi, so the cluster's
`baseapi-service` pod remained near-idle and never allocated its way to a gen0 collection.

**Why this is not a Class A failure.** Panels 2, 3 and 5 aggregate with `sum by (source, …)`
over whatever series exist, so on an "All" selection they return real samples from the other
three classes and the live proof's `ClassAPanelsNonEmpty` claim is honestly true (0 of 30
empty). The gap is visible only when a reviewer narrows the `source` dropdown to `webapi`
alone, where those three panels legitimately read "No data".

**Accepted:** this is instrumentation semantics, not a dashboard defect, and no dashboard change
can conjure a gauge the runtime has not computed. Panel 5's `description` carries the caveat in
text so a reviewer who narrows to `webapi` reads the explanation on the panel itself. It
resolves by itself the first time the pod does real work.

---

## 5. BPD-02 / F-5 — no keeper reinject / drop-outcome counter exists

**Asked for:** BPD-02 originally asked for "keeper reinject/drop outcomes" as a dedicated panel
alongside `orchestrator_step_unresolved_total` and `processor_spawn_dropped_total`.

**What actually exists:** nothing. `KeeperMetrics.cs:12-21` documents that **Phase 74
deliberately deleted** the legacy label-less reinject-drop counter in favour of the uniform
consumed/sent pair. The keeper's only three instruments are `keeper_messages_consumed`,
`keeper_messages_sent` and `keeper_l2_probe` (`KeeperMetrics.cs:50-52`), confirmed by
`grep CreateCounter src/Keeper --include=*.cs`. Re-adding the counter would be a `src/` change,
and it would also *reverse a deliberate architectural decision from another phase* — the worst
possible thing for a dashboards milestone to do on its own authority.

**Where the data actually lives:** reinject outcome is recorded in **Elasticsearch** as
`attributes.ReinjectOutcome` (Phase 78), read today by the analyzer's verdict path. ES panels
are explicitly out of scope this milestone ("*Log (Elasticsearch) panels … ES stays the
analyzer's domain*"), so the axis is unreachable from a Prometheus-only dashboard.

**What shipped instead**, on `k8s/dashboards/business.json`:

- **`Keeper consumed - sent gap in range`** (panel id 8, `stat`) — the drop *is* the difference
  between the two surviving counters. Guarded with `or vector(0)`.
- **`Keeper L2 probe heartbeat`** (panel id 9, `timeseries`) — `rate(keeper_l2_probe_total)` as
  the "keeper is alive and probing" liveness signal, deliberately **unguarded** so a dead keeper
  reads as "No data" rather than as a comfortable `0`.

Both are titled for exactly what they show. The amended BPD-02 makes that a requirement — "*The
panel title must state what it shows (the gap), not imply a drop-outcome breakdown*" — and lint
rule **T-87-17** verifies mechanically that no panel title matches reinject/drop-outcome
wording. Measured: **0 such titles.**

**Accepted:** the gap axis is a *weaker* signal than a true outcome breakdown (it tells you
*that* messages were lost, not *why* they were dropped), and that weakening is recorded here
rather than papered over. The stronger signal is available today in Elasticsearch via the
analyzer, and would return to Grafana only in a future milestone that admits ES panels.

---

## 6. VER-01 / F-4 — the Class A / Class B split, and the six guarded expressions

**Asked for:** VER-01 originally read "**every** panel on both dashboards renders data". F-4
established that is unsatisfiable on a healthy system: an OTel counter is **not exported to
Prometheus until it is first incremented**, and a green stack *should* have zero unresolved
steps and zero dropped spawns. The requirement was amended into a **two-class assertion**.

- **Class A — must return at least one real sample.** A "No data" is a failure.
- **Class B — must render a guarded value; `0` is permitted.** These carry an `or vector(0)`
  guard, and rendering `0` satisfies the requirement.

The classifier in `scripts/phase-87-dashboards-verify.ps1` reads **nothing but the presence of
the substring `or vector(0)`** — no panel-title list, no expression exception list — which is
what makes the partition auditable rather than asserted. Symmetric authoring-side lint rules
keep it honest in both directions: **C3** fails a Class-B expression that lacks a guard,
**C7** fails a Class-A expression that has one (a guarded liveness panel would let a dead keeper
render a passing `0`), and **C8** covers the one Class-B expression that is not a domain
counter.

**Measured: 30 Class A / 6 Class B over 36 targets. `ClassAPanelsNonEmpty` true (0 of 30
empty); `ClassBPanelsGuarded` true (6 of 6 returned exactly one sample).** The six Class B
expressions, named from `ClassBExprs` in the verdict artifact — **each returned
`SampleCount: 1`, exactly one guarded sample**:

| # | Panel | refId | Metric family | Samples |
|---|---|---|---|---|
| 1 | `Keeper consumed vs sent (conservation)` | A | `keeper_messages_consumed_total` | **1** |
| 2 | `Keeper consumed vs sent (conservation)` | B | `keeper_messages_sent_total` | **1** |
| 3 | `Orchestrator unresolved steps in range` | A | `orchestrator_step_unresolved_total` | **1** |
| 4 | `Processor dropped spawns in range` | A | `processor_spawn_dropped_total` | **1** |
| 5 | `Keeper consumed - sent gap in range` | A | `keeper_messages_consumed_total` − `keeper_messages_sent_total` | **1** |
| 6 | `WebApi 5xx ratio` | A | `http_server_request_duration_seconds_count{http_response_status_code=~"5.."}` | **1** |

All six sit on `business.json`; the entire 17-target `runtime.json` is Class A.
`ClassBOffSpecExprs` is empty — no guarded expression returned zero or more than one sample.

**Why `keeper_messages_*` in particular is a legitimate guarded zero.** Those two counters
increment **only inside a recovery event** (`RecoveryConsumerBase.Consume` / `CountSent`), so
they were **absent from the entire 15-day `__name__` index** — not merely at zero series, but
never emitted in the retention window at all (research Pitfall 6). The live proof drove a
happy-path `FanOutSeeder` run with no fault scenario, so they legitimately stayed at the guarded
zero. **Driving a fault scenario to make them fire is explicitly NOT a precondition of this
phase** (VER-01, as amended). The keeper conservation panel therefore passed as a guarded
Class-B panel on a happy path **with no exception-list entry** — the guard 87-04 placed is what
carried it, so the classifier never had to be told about it.

**Accepted:** the honest cost is that a `0` on those panels is ambiguous between "no recovery
traffic in this window" and "recovery is broken". Panel 2's description states plainly that a
flat `0` means the former, and names Panel 9 (the unguarded L2 probe heartbeat) as the liveness
signal that disambiguates it. Proving these counters *can* fire belongs to the 7-scenario
resilience sweep, which drives real faults — not to a dashboards phase.

---

## 7. VER-01 / VAR-02 / F-7 — the pod dropdown is a superset, not the live pod set

**Asked for:** VER-01 originally implied the pod dropdown lists the live pods per class.

**Why it cannot.** Grafana's `label_values(metric{filter}, label)` resolves through
`PrometheusMetricFindQuery.labelValuesQuery` → `queryLabelValues` → Prometheus
`/api/v1/label/<name>/values?match[]=…&start=…&end=…`. That endpoint answers from the **TSDB
index over overlapping blocks**, not from samples, so it returns any pod whose series appears
in a touched block regardless of whether it is alive. Research measured this directly through
Grafana's own datasource proxy — **4 keeper pods listed while 2 were alive**, and identically at
a 30-minute, a 5-minute and a **60-second** dashboard window. Shortening the range does not
help. By contrast an *instant PromQL* query returns exactly the live set (8 results,
byte-identical to `kubectl get pods`).

**This is index granularity, and there is no Grafana setting that fixes it.** VAR-02's
`label_values()` mandate was kept as-is: the alternative (a hand-maintained list, or an instant
query hack) would lose the "populated from live series, not a hardcoded list" property that
VAR-01/VAR-02 exist to guarantee. VER-01 was therefore amended to assert the dropdown as a
**superset — every live pod appears** — rather than as an exact live list.

**Measured in the live run:** `LivePodCount` **8**, `DropdownPodCount` **21**,
`PodDropdownSuperset` **true**, `MissingDropdownPods` **empty**. Every live pod is present; the
dropdown carries 13 additional entries. The run itself rolled every app Deployment, which is
precisely the condition that maximises the over-return.

**A second asymmetry, different in kind, found during the live run: `USERPC`.** Most of the
13-entry excess is the F-7 index over-return of recently-dead pods. One entry is not a dead pod
at all — **`USERPC`, the host machine name.** The seeder's in-proc WebApi
(`RealStackWebAppFactory`) exports to `localhost:4317` through the shared OTLP forward, and
`ResolveInstanceId()`'s precedence is `POD_NAME → HOSTNAME → MachineName → GUID`, so off-cluster
it falls back to the machine name. It is a **genuine `source="webapi"` series that is not a pod
at all**, and it is indistinguishable from a pod name to `label_values()`.

**Accepted, and harmless to the assertion** — a superset claim tolerates extra entries, and
`AllFourClassesPresent` was still exactly `[keeper, orchestrator, processor, webapi]` because
`USERPC` emits a real `source="webapi"` label. But the consequence should be stated plainly:
**after any host-run seeder, the pod dropdown can show a non-pod entry**, and a reviewer who
selects it will see the host process's series rather than a cluster pod's. This is the same
family as research F-8's `console-test` pollution (the hermetic suite emitting to
`localhost:4317`), which is why the `source` dropdown is scoped to a stack-only metric and
`allValue` is deliberately left unset. Not a dashboard defect; no change made.

---

## 8. VAR-04 / F-6 — `service_name` cannot discriminate processor images

**Asked for:** VAR-04 originally stated that "*the dashboards keep `service_name` visible as the
finer-grained discriminator*" for the two processor images.

**What actually exists:** `src/Processor.Sample/appsettings.json:9-12` configures
`Service:Name = "unresolved"` / `Service:Version = "0.0.0"`, and **MLBL-03 deliberately keeps
that sentinel for the pod's whole life** (`ProcessorMetrics.cs:40-43`: "*a pod keeps ONE
`service_name` (the `unresolved_0.0.0` sentinel) for its whole life*"). Confirmed live: **every
`source="processor"` series carries `service_name="unresolved_0.0.0"`.** The label is constant
across the entire class and discriminates nothing. VAR-04's second half was simply wrong, and
was amended.

**What discriminates them instead, and what shipped:**

| Series family | Discriminator | Used in |
|---|---|---|
| Business counters (`processor_messages_consumed_total` / `_sent_total`) | **`identityName`** — a *datapoint* tag carrying `{db.Name}_{db.Version}` (e.g. `sample-proc-ea1076bd…_1.0.0`) | `business.json` panel 3 `Processor consumed vs sent by image (conservation)`, refIds A and B, in both the group-by and the `legendFormat` |
| Runtime series (`process_runtime_dotnet_*`) | **`service_instance_id`** — the pod name. `identityName` is *not* present on these; they are framework-level and untagged | every `runtime.json` panel, via `by (source, service_instance_id, …)` |

Lint rule **C5** asserts the `identityName` grouping exists on the processor conservation panel
(measured: 2 expressions), and a companion rule asserts **zero** expressions use
`by (service_name` or `service_name=~` anywhere in either dashboard (measured: **0**).

**A second, quieter limitation:** `Processor.BadConfig` *does* set a real
`Service:Name = "processor-badconfig"` — but **only `processor-sample` is deployed to k8s**
(`k8s/33-processor-sample.yaml` is the sole processor Deployment). So "both processor images"
is **currently a single-image reality in this cluster**: the `identityName` grouping is correct
and will separate images the moment a second one is deployed, but in this cluster it renders one
series. The discrimination is designed-in and untested-by-multiplicity, and that is stated here
rather than claimed as proven.

---

## 9. Deliberate non-changes and fenced-off files

This phase was fenced by REQUIREMENTS.md's Out of Scope list, and the fences held.

**Zero production source changes.** No plan in phase 87 created, modified or deleted **any file
under `src/`**. The milestone's opening premise — "*No production source code changes: every
label and series the dashboards consume is already emitted today*" — is satisfied in full.

> *Note on the literal check.* The plans' acceptance criteria phrase this as
> `git status --porcelain src/` being empty. That command is **not** empty in this working tree:
> `src/Keeper/Recovery/ReinjectConsumer.cs` is modified and
> `src/BaseApi.Service/Properties/launchSettings.json` is untracked. **Both predate this phase**
> — they appear in the session-start git snapshot and are documented identically in the 87-01,
> 87-02, 87-03, 87-04 and 87-05 summaries. The assertion this phase can and does make is the
> stronger, correctly-scoped one: **phase 87 changed nothing under `src/`.** The tree was
> already dirty there when the phase began.

**The four fenced-off files, left untouched:**

| File | Why fenced |
|---|---|
| `k8s/02-configmaps.yaml` | the OTel collector config. Changing it is out of scope ("*Changing the collector, Prometheus, or any exporter config*"). It is also where the `/health/` route-drop filter lives — the BPD-03 caveat on `business.json` panel 10 documents that behaviour rather than removing it |
| `k8s/21-prometheus.yaml` | the Prometheus config. A cAdvisor/kubelet scrape job here is one of the two routes to the §1 metrics, and it is the fenced one |
| `scripts/phase-80-up.ps1` | the shared 8-port-forward table used by the 7-scenario resilience sweep. Research recommended option (a): the phase-87 verify script starts and tears down its **own** loopback Grafana forward, so the sweep's bring-up is unperturbed and no header count comment goes stale |
| `.k8s-portforward-pids` | the sweep's forward-PID ledger. The verify script keeps its forward PID in one in-memory variable; the shared filename appears nowhere in it |

Verified: `git status --porcelain` on all four is **empty**, and
`Invoke-WebRequest http://localhost:8080/health/ready` returned **200** after the live run — the
shared forwards survived intact.

**One runtime-only intervention, recorded rather than omitted.** During Waves 2 and 3 the
executors **did mutate live cluster state**: `kubectl apply -k k8s/` reverts the four app
Deployments (`orchestrator`, `processor-sample`, `keeper`, `baseapi-service`) from the live
`:tags-const-1544` images to the manifests' stale `:local` pins, so each apply was followed by
`kubectl set image deployment/<d> <d>=<d>:tags-const-1544` to restore them. This mattered
materially, not cosmetically: **the stale `:local` bits do not emit the `source` resource
attribute at all**, so a stale-image cluster makes correct dashboards look broken — measured
before the fix, `group by (source) (process_runtime_dotnet_gc_collections_count_total)` returned
`{processor, orchestrator, {}}` with keeper and webapi collapsed into a label-less group, and
three `source`-filtered business expressions returned zero series.

**No manifest was edited to achieve this.** The intervention is live cluster state only; zero
files changed. The underlying **tag drift between the manifests' `:local` pins and the images
the cluster actually runs is pre-existing and out of this phase's scope** — it belongs to
whichever phase owns the image-tagging convention. It is recorded here so that a future reader
who finds the cluster on `:tags-const-1544` while the manifests say `:local` knows it was a
deliberate runtime restoration and not a silent divergence introduced by the dashboards work.

**Grafana's security posture is an accepted dev posture.** Grafana ships with
`GF_AUTH_ANONYMOUS_ENABLED=true` at role **`Viewer`**, `GF_USERS_ALLOW_SIGN_UP=false`, and
`admin`/`admin` credentials in the manifest. This **matches the stack's existing posture** — the
in-cluster Prometheus and Elasticsearch are both unauthenticated, and every tool is reached by a
loopback `--address 127.0.0.1` port-forward. "*Grafana persistence, auth hardening, or ingress*"
is out of scope per REQUIREMENTS.md. The anonymous role is read-only and cannot mutate anything;
`allowUiUpdates: false` additionally rejects a provisioned-dashboard save outright — actively
proven in the live run, which attempted the tamper and received `Cannot save provisioned
dashboard`.

---

## If a future milestone wants the real metrics

Two blocked routes, and what each would cost:

**Route A — `OpenTelemetry.Instrumentation.Process` (closes §1: real CPU, working set; and via
`process.uptime`, real uptime).**
Add the package to `Directory.Packages.props` and call `.AddProcessInstrumentation()` in both
`ObservabilityServiceCollectionExtensions.cs` and `BaseConsoleObservabilityExtensions.cs`.
*Cost:* a `src/` change across the shared observability seam, therefore a rebuild and redeploy of
all four service images; and the package is still **pre-GA at `1.17.0-rc.1`**, so it drags a
release-candidate dependency into every service. Gains `process_cpu_time_seconds_total`,
`process_cpu_count`, `process_memory_usage_bytes`, `process_memory_virtual_bytes`. Would let
panels 5, 7 and 17 be replaced by honestly-titled CPU / Working set / Uptime panels, and would
retire §2 entirely.

**Route B — a cAdvisor / kubelet scrape job in `prometheus.yml` (closes §1 partially, and §2
properly).**
Add a `kubernetes-nodes-cadvisor` scrape job plus `kube-state-metrics`.
*Cost:* a Prometheus config change (`k8s/21-prometheus.yaml` / `k8s/02-configmaps.yaml`), RBAC
for node/metrics access, and a new workload in the namespace. Gains
`container_cpu_usage_seconds_total`, `container_memory_working_set_bytes` — the *container's*
CPU and working set, which is arguably the more operationally useful pair — and
`kube_pod_container_status_restarts_total`, which is the **correct** k8s restart counter that §2
establishes `resets()` cannot be. The labels are k8s labels (`pod`, `container`), not this
stack's `source` / `service_instance_id`, so the dropdown contract would need a
`label_replace()` bridge or a second variable pair.

**Neither is proposed as an action item for this milestone.** Both are recorded so the next
milestone can price them instead of rediscovering the gap.

---

## Requirement traceability

| Requirement | Limitation recorded | Finding | Section |
|---|---|---|---|
| RTD-01 | family is `process_runtime_dotnet_*`; net9 upgrade hazard | F-1 | §3 |
| RTD-02 | no CPU / uptime / working set; three honest substitutes | F-2 | §1 |
| RTD-02 (A4) | `resets()` is an in-process reset detector, not a k8s restart counter | A4 | §2 |
| RTD-02 | webapi's three GC gauges absent until first GC | F-3 | §4 |
| BPD-02 | no keeper reinject/drop-outcome counter; gap + heartbeat instead | F-5 | §5 |
| VER-01 | Class A / Class B two-class assertion; six guarded zeros | F-4 | §6 |
| VER-01 / VAR-02 | pod dropdown asserted as a superset (+ the `USERPC` non-pod entry) | F-7, F-8 | §7 |
| VAR-04 | `service_name` is the `unresolved_0.0.0` sentinel; `identityName` discriminates | F-6 | §8 |
| — | fenced-off files, zero `src/` changes, runtime-only image restoration, dev auth posture | — | §9 |
