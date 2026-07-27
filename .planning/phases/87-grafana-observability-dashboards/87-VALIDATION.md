---
phase: 87
slug: grafana-observability-dashboards
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-07-27
---

# Phase 87 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `87-RESEARCH.md` § Validation Architecture (live-verified 2026-07-27).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | **No .NET test project applies** — this phase adds zero C#. Validation is (a) static YAML/JSON gates via `kubectl` / `kustomize` / JSON parse, and (b) a live PowerShell verification script modelled on `scripts/phase-83-ha-failover.ps1` |
| **Config file** | none — see Wave 0 |
| **Quick run command** | `pwsh scripts/phase-87-dashboard-lint.ps1` (kustomize build + JSON well-formedness + shape assertions) |
| **Full suite command** | `pwsh scripts/phase-87-dashboards-verify.ps1` (writes `analyzer-reports/phase-87-dashboards.json`) |
| **Estimated runtime** | ~5 s quick · ~3–5 min full (live cluster + traffic) |

**Do NOT add Phase-87 assertions to the hermetic .NET suite.** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` carries ~23–24 known baseline failures unrelated to any phase (memory: `hermetic-preexisting-failures`) and touches nothing this phase changes. No `src/` changes means no unit-testable surface.

---

## Sampling Rate

- **After every task commit (hermetic, <5 s):** `kubectl kustomize k8s/ > /dev/null` (whole-stack build) + JSON well-formedness on every `k8s/dashboards/*.json` + the DASH-04 / VAR-01..04 / RTD-03 parse assertions. Catches the overwhelmingly likely failure modes: bad JSON, forgotten `$pod` filter, leaked hardcoded datasource UID, typo'd metric name.
- **After every plan wave:** the above **plus** `kubectl apply -k k8s/ --dry-run=server` — the repo's existing whole-stack lint gate (`k8s/kustomization.yaml:18-20`), validating against the live API server without creating anything.
- **Before `/gsd:verify-work`:** `pwsh scripts/phase-87-dashboards-verify.ps1` green against the live cluster with traffic driven.
- **Max feedback latency:** 5 seconds (quick gate).

**Nyquist argument:** the dashboard JSON is edited far more often than the cluster changes, so the *fast* gate must cover JSON-shape errors (sampled every commit) while the *slow* gate covers datasource wiring and data presence (sampled per wave / per phase). Panel-expression correctness is sampled at **both** rates — statically (does it reference a real metric name from the verified inventory?) and dynamically (does it return rows?).

---

## Per-Task Verification Map

Task IDs are assigned at plan time. The authoritative requirement→test mapping below is lifted from `87-RESEARCH.md` § Validation Architecture; the planner must attach each row to the task that delivers it.

| Requirement | Behavior | Test Type | Automated Command | File Exists |
|-------------|----------|-----------|-------------------|-------------|
| DASH-01 | Grafana Deployment+Service come up via `apply -k` | live | `kubectl -n skp rollout status deployment/grafana --timeout=120s` | ❌ W0 |
| DASH-01 | Reachable by port-forward | live | `curl -sf $GF/api/health` → `.database == "ok"` | ❌ W0 |
| DASH-02 | Prometheus is the default datasource, from config | live | `GET /api/datasources/uid/skp-prometheus` → `.isDefault and .readOnly`; `/health` → `status == "OK"` | ❌ W0 |
| DASH-03 | No PVC; `emptyDir` only | hermetic | `kubectl kustomize k8s/` shows `grafana-storage` → `emptyDir`, and no `kind: PersistentVolumeClaim` for grafana | ❌ W0 |
| DASH-03 | UI cannot become the source of truth | live | `POST /api/dashboards/db` → `{"message":"Cannot save provisioned dashboard"}` | ❌ W0 |
| DASH-04 | No hardcoded datasource UID in either JSON | hermetic | no `skp-prometheus` literal in `k8s/dashboards/*.json`; every `datasource.uid` equals `${datasource}` | ❌ W0 |
| RTD-01 | Panels use only `process_runtime_dotnet_*` | hermetic | extract all `targets[].expr`; assert none matches `\bdotnet_` and none matches a non-runtime family | ❌ W0 |
| RTD-01 | All four classes present | live | `group by (source) (process_runtime_dotnet_gc_collections_count_total)` → exactly the 4 | ❌ W0 |
| RTD-02 | GC / memory / threadpool / exception panels non-empty | live | each `expr` via `/api/datasources/proxy/.../api/v1/query` → `result | length > 0` | ❌ W0 |
| RTD-02 | CPU/uptime/working-set proxies are titled for what they measure | hermetic | no panel titled `CPU`/`Uptime`/`Working Set`; the substituting panels carry the proxy wording | ❌ W0 |
| RTD-03 | Panels break down per pod | hermetic | every runtime `expr` contains `service_instance_id` in its `by (...)` clause **and** `service_instance_id=~"$pod"` in its selector | ❌ W0 |
| BPD-01 | All nine counters referenced | hermetic | each of the 9 names appears in the business JSON | ❌ W0 |
| BPD-02 | Fault indicators are dedicated panels | hermetic | a panel whose only target references `orchestrator_step_unresolved_total`, ditto `processor_spawn_dropped_total`, ditto the keeper consumed−sent gap | ❌ W0 |
| BPD-02 | Fault panels render `0`, not "No data" | live | each fault `expr` → `result | length == 1` (the `or vector(0)` guard) | ❌ W0 |
| BPD-03 | WebApi via ASP.NET Core metrics only | hermetic | no domain counter appears with `source="webapi"`; `http_server_request_duration_seconds_*` does | ❌ W0 |
| VAR-01 | `source` var is `label_values()`, multi, includeAll, no custom allValue | hermetic | `templating.list[name=="source"]`: `multi==true`, `includeAll==true`, `allValue==null`, `query` starts `label_values(` | ❌ W0 |
| VAR-02 | pod var likewise over `service_instance_id` | hermetic | same shape assertion | ❌ W0 |
| VAR-03 | pod var chained to `$source`; every panel honors both | hermetic | pod var `query` contains `source=~"$source"`; **every** `targets[].expr` contains both `source=~"$source"` and `service_instance_id=~"$pod"` | ❌ W0 |
| VAR-03 | Chaining actually filters | live | `label_values` with `match[]={source=~"(keeper)"}` returns only keeper-prefixed pods | ❌ W0 |
| VAR-04 | Per-image discrimination present | hermetic | processor business panels group by / legend on `identityName` (**not** `service_name` — research F-6) | ❌ W0 |
| VER-01 | Class A panels return ≥1 real sample with traffic | live | full expr sweep after a happy-path seed+start run | ❌ W0 |
| VER-01 | Class B (fault) panels render a guarded value, `0` permitted | live | guarded expr → exactly one sample | ❌ W0 |
| VER-01 | Dropdowns: 4 classes exactly; pods a superset of the live set | live | `label_values(source)` == the 4; every `kubectl`-live pod present in the pod dropdown | ❌ W0 |
| VER-02 | Pod delete/recreate reproduces from repo | live | dashboard spec byte-identical before/after `kubectl delete pod -l app=grafana`; `meta.provisioned == true` | ❌ W0 |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `scripts/phase-87-dashboard-lint.ps1` — the hermetic JSON gate: well-formedness, `schemaVersion`, no hardcoded datasource UID, every `expr` carries both variable filters, every referenced metric name is in the Phase-87 verified inventory, template-variable shape assertions. Covers DASH-04, RTD-01, RTD-03, BPD-01, BPD-03, VAR-01, VAR-02, VAR-03, VAR-04.
- [ ] `scripts/phase-87-dashboards-verify.ps1` — the live VER-01/VER-02 proof (covers DASH-01/02/03, RTD-01/02, BPD-02, VAR-03, VER-01, VER-02). Model on `scripts/phase-83-ha-failover.ps1`; owns its own `svc/grafana 3000:3000` port-forward on `127.0.0.1`.
- [ ] Metric-name allowlist constant — the live-verified inventory from `87-RESEARCH.md`, embedded in the lint script so a typo'd metric name fails hermetically rather than as a silently empty panel.
- [ ] `analyzer-reports/phase-87-dashboards.json` — verdict artifact (matches the `phase-83-ha.json` convention).
- [ ] Framework install: **none** — PowerShell, `kubectl`, and `curl` are already present. `jq` is NOT available; use `ConvertFrom-Json` / `python -m json.tool`.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Panels are visually legible — conservation readable at a glance, no overlapping/misscaled axes | BPD-02 | "Legible at a glance" is a rendering judgement no query assertion captures; the API proves data presence, not readability | Port-forward `svc/grafana 3000:3000`, open both dashboards with traffic flowing, confirm consumed-vs-sent overlays read as conservation and fault panels are visually distinct |
| Dropdown interaction — selecting `keeper` visibly narrows the pod list in the UI | VAR-03 | The chained-query filter is asserted via the API, but the browser interaction path is not scripted | Select each `source` value in turn; confirm the pod dropdown re-populates |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 5s (quick gate)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
