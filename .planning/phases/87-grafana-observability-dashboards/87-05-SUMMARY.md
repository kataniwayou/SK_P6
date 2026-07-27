---
phase: 87
plan: 05
subsystem: observability-verification
tags: [grafana, prometheus, promql, live-proof, ver-01, ver-02, dashboard-as-code, provisioning]
requires:
  - "k8s/23-grafana.yaml + k8s/kustomization.yaml configMapGenerator (87-02) — the Grafana this proof queries"
  - "k8s/dashboards/runtime.json (87-03) — 17 panels / 17 targets, all Class A"
  - "k8s/dashboards/business.json (87-04) — 14 panels / 19 targets, the 6-guarded / 13-unguarded partition this proof reproduced exactly"
  - "scripts/lib/exit-code-resolution.ps1 (Phase 76) — Resolve-AnalyzerExitCode / Resolve-SweepClass, dot-sourced not reimplemented"
provides:
  - "scripts/phase-87-dashboards-verify.ps1 — the single-purpose live proof of VER-01 + VER-02 with structured exit codes"
  - "analyzer-reports/phase-87-dashboards.json — Verdict Pass, 10/10 claim booleans true, in the phase-83-ha single-proof schema"
  - "the empirical answer to 87-03's A4 question: a stable stack shows ZERO spurious resets() on Panel 17"
affects:
  - "87-06 (phase findings — the A4 answer, the webapi/USERPC dropdown entry, and the live-cluster image drift are all recorded below)"
tech-stack:
  added: []
  patterns:
    - "replay every checked-in targets[].expr through GRAFANA'S OWN datasource proxy rather than straight at Prometheus — one assertion then covers both the datasource wiring and the PromQL"
    - "a purely mechanical two-class classifier (substring presence, zero exception list) made safe by symmetric authoring-side lint rules C3/C7/C8"
    - "report the Class-B membership itself (ClassBExprs), so a silently-added guard surfaces as a new entry instead of as silence"
    - "canonicalise a parsed-JSON spec by recursive key sort before byte-comparison — ConvertFrom-Json does not preserve key order"
key-files:
  created:
    - scripts/phase-87-dashboards-verify.ps1
    - analyzer-reports/phase-87-dashboards.json
  modified: []
decisions:
  - "The VER-02 byte-equality drops the top-level `id` and `version` keys before canonicalising. Both are Grafana DB surrogates that appear in nothing the repo authors (k8s/dashboards/*.json deliberately omits `id` — an exported `id` breaks file provisioning) and Grafana's store is an emptyDir, so a pod delete necessarily mints fresh ones. Comparing them would assert a property the requirement does not claim. Every byte the repo contributes — uid, title, description, tags, templating, every panel, every target — IS compared, and the canonicaliser was unit-checked to still detect a one-character panel-title change."
  - "A tenth claim, KeeperChainFiltered, was added alongside the nine the plan enumerates, because the plan asks STEP G to assert VAR-03 chaining but lists no boolean for it. Folding it into PodDropdownSuperset would have hidden it; a separate claim keeps it visible in the artifact and in the verdict."
  - "The report carries two diagnostic arrays the plan did not name — ClassBOffSpecExprs and MissingDropdownPods — so a Class-B miss or a missing pod is as auditable from the artifact as a Class-A miss already was. Both are empty on this run."
metrics:
  duration: ~55 min
  completed: 2026-07-27
  tasks: 2
  commits: 2
---

# Phase 87 Plan 05: Dashboard Live Proof (VER-01 + VER-02) Summary

Both dashboards are proven by assertion rather than by eye: all **36** checked-in panel expressions were replayed through Grafana's own datasource proxy and satisfied their class (30 unguarded returned real samples, 6 guarded returned exactly one), both dropdowns enumerated correctly, a Grafana pod delete reproduced both specs byte-identically from the repo, and a UI save was rejected — **Verdict Pass, exit 0, 10/10 claims green.**

## The verdict artifact

`analyzer-reports/phase-87-dashboards.json`, in the `phase-83-ha.json` single-proof schema (one object, PascalCase, `ScenarioId` first, `Verdict` second, booleans, then scalars and round-trip `o` timestamps, `HumanSummary` last).

| Claim | Value |
|---|---|
| `DashboardsProvisioned` | **true** — both uids in `/api/search`, `meta.provisioned` true, `provisionedExternalId` = `runtime.json` / `business.json` |
| `DatasourceDefaultReadOnly` | **true** — `isDefault` and `readOnly`, datasource health `OK` |
| `NoPvc` | **true** — 0 grafana-attributable PVCs, `grafana-storage` really is an `emptyDir` |
| `UiSaveRejected` | **true** — `Cannot save provisioned dashboard` |
| `AllFourClassesPresent` | **true** — `[keeper, orchestrator, processor, webapi]`, exactly |
| `ClassAPanelsNonEmpty` | **true** — 0 of 30 empty |
| `ClassBPanelsGuarded` | **true** — 6 of 6 returned exactly one sample |
| `PodDropdownSuperset` | **true** — 0 live pods missing |
| `KeeperChainFiltered` | **true** — keeper-scoped `match[]` returned only `keeper-*` names (VAR-03) |
| `ReproducedAfterPodDelete` | **true** — both specs byte-identical, still provisioned |

Scalars: `TargetExprCount` 36, `ClassAExprCount` **30**, `ClassBExprCount` **6** (30 + 6 = 36 = every `targets[]` entry across both files), `LivePodCount` **8**, `DropdownPodCount` **21**, `GrafanaPvcCount` 0, `StorageIsEmptyDir` true, `TrafficDriven` true, window `2026-07-27T19:18:03.5652057+00:00` → `2026-07-27T19:20:03.5848909+00:00`. `EmptyClassAExprs`, `ClassBOffSpecExprs`, `MissingDropdownPods`, `SpecDiffs`, `UnevaluableClaims` are all **empty**.

## The VER-01 partition reproduced exactly

87-04 predicted 6 guarded / 13 unguarded across `business.json`'s 19 targets. The live classifier — which reads nothing but the presence of the substring `or vector(0)` — produced precisely that set, and nothing else:

| File | Panel | refId | Samples |
|---|---|---|---|
| business.json | Keeper consumed vs sent (conservation) | A | 1 |
| business.json | Keeper consumed vs sent (conservation) | B | 1 |
| business.json | Orchestrator unresolved steps in range | A | 1 |
| business.json | Processor dropped spawns in range | A | 1 |
| business.json | Keeper consumed - sent gap in range | A | 1 |
| business.json | WebApi 5xx ratio | A | 1 |

Nothing referencing `keeper_l2_probe_total` and nothing referencing the orchestrator/processor consumed-sent counters appears in `ClassBExprs` (asserted explicitly, both counts 0). The keeper conservation panel **passed as a guarded Class-B panel on a happy-path drive with no exception-list entry**, which is the specific must-have the plan called out: the guard 87-04 placed is what carries it, so the classifier never had to be told about it.

Across both dashboards: **30 Class A / 6 Class B**. The 17 runtime targets are all Class A and all returned samples, including the four RTD-02 honest substitutes.

## Findings carried forward to 87-06

### 1. A4 answered — `resets()` shows NO spurious restarts on a stable stack

87-03 asked plan 87-05 to record whether Panel 17's `resets()` restart proxy is tripped by the OTel collector's 5-minute `metric_expiration`. Measured immediately after the run, over the full `[1h]` window and across a period in which every app pod was in fact rolled:

```
panel-17 series = 21;  nonzero series = 0 of 21
```

**Zero spurious values.** The mechanism is now clear and is worth writing into 87-06: a pod restart does not reset an existing series, it starts a **new** series under a new `service_instance_id`, so the restart shows up as an extra series rather than as a `resets()` count on the old one. Expiry likewise retires a series rather than zeroing it. So the A4 risk did not materialise — but the corollary is that Panel 17 is a **weaker** restart signal than its title suggests for the *k8s* case (a restarted pod gets a new identity, so the panel stays at 0), and is really an *in-process* counter-reset detector. 87-06 should decide whether to widen the panel description on that basis rather than on the `metric_expiration` basis 87-03 anticipated.

### 2. `webapi/USERPC` appears in the pod dropdown

`DropdownPodCount` is 21 against `LivePodCount` 8. Most of the excess is the documented F-7 TSDB-index over-return of recently-dead pods (the run itself rolled every app Deployment). One entry is different in kind: `USERPC`, the **host machine name**, emitted by the seeder's in-proc WebApi (`RealStackWebAppFactory` exports to `localhost:4317`, and `ResolveInstanceId()` falls back to the machine name off-cluster). It is a genuine `source="webapi"` series that is not a pod. Harmless for the superset assertion, but it means the pod dropdown can show a non-pod entry after any host-run seeder. Worth a line in 87-06's findings; not a dashboard defect.

### 3. Live-cluster image drift is wider than the phase inherited it

See "Deviations" below — `keeper` and `baseapi-service` were also stale, not just `orchestrator` and `processor-sample`.

## Task Commits

| Task | Name | Commit | Files |
|---|---|---|---|
| 1 | Author `scripts/phase-87-dashboards-verify.ps1` | `a32f271` | `scripts/phase-87-dashboards-verify.ps1` (new, 802 lines) |
| 2 | Run the live proof to a Pass verdict | `3dc8dbd` | `analyzer-reports/phase-87-dashboards.json` (new) |

Zero file deletions in both commits.

## Verification Results

| Check | Result |
|---|---|
| `[ScriptBlock]::Create` parse of the verify script | **ok** |
| `Select-String -SimpleMatch` on the shared port-forward PID filename | **0 matches** (T-87-11) |
| `-SimpleMatch '--address'` / `'0.0.0.0'` | **4** / **0** (T-87-01) |
| `-SimpleMatch 'docker compose'` / `'jq '` | **0** / **0** |
| `-SimpleMatch 'lib/exit-code-resolution.ps1'` / `'Resolve-AnalyzerExitCode'` | **2** / **5** — no inline Pass/Fail/Inconclusive switch anywhere |
| EXIT-CODE TABLE rows for 0, 1, 2, 10, 15, 20, 30, 40, 50, 60, 64 | **11 / 11**, one row each |
| Artifact written (line 764) before `Resolve-AnalyzerExitCode` (line 768) | **ordered** (T-87-18) |
| Classifier keys off `or vector(0)` only; no panel-title or expression exception list | **confirmed** |
| `pwsh -File scripts/phase-87-dashboards-verify.ps1` | exit **0** |
| `analyzer-reports/phase-87-dashboards.json` `ScenarioId` / `Verdict` | `phase-87-dashboards` / **Pass** |
| Ten claim booleans | **10 / 10 true** |
| `EmptyClassAExprs` | **[]** |
| `ClassAExprCount + ClassBExprCount` vs total `targets[]` | **30 + 6 = 36 = 36** |
| `HumanSummary` present, last field, names every claim | **yes** |
| `pwsh -File scripts/phase-87-dashboard-lint.ps1` (both files, all rules) | exit **0** — 31 panels, 36 targets, **23 rules** |
| `kubectl apply -k k8s/ --dry-run=server` | exit **0** |
| `git status --porcelain` on the fenced files (shared up script, `02-configmaps.yaml`, `21-prometheus.yaml`, `23-grafana.yaml`, `kustomization.yaml`, `k8s/dashboards/`, the lint script) | **empty** |
| `Invoke-WebRequest http://localhost:8080/health/ready` after the run | **200** — the shared forwards survived intact |
| grafana ConfigMap after the authoritative apply | `runtime.json` **17 panels**, `business.json` **14 panels** |

**No dashboard JSON was edited.** The wave-context permission to fix a genuine Class A miss in `k8s/dashboards/*.json` was never exercised — there were no Class A misses. No panel was reclassified, no assertion was weakened, and no exception was added.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Live-cluster image drift was wider than the plan's standing hazard described — `keeper` and `baseapi-service` were ALSO stale, and their stale code emits no `source` label**

- **Found during:** pre-Task-2 reconnaissance (before any run).
- **Issue:** the phase inherited a documented hazard that `kubectl apply -k k8s/` reverts `orchestrator` and `processor-sample` from the live `:tags-const-1544` to the manifest-pinned `:local`, whose containerd copy is stale code. In fact 87-02's apply reverted **all four** app Deployments and only two were restored — the ReplicaSet history shows a `:tags-const-1544` ReplicaSet for `keeper` and `baseapi-service` too. The consequence was not cosmetic: the stale `keeper:local` and `baseapi-service:local` bits **do not emit the `source` resource attribute at all**. Measured before the fix: `group by (source) (process_runtime_dotnet_gc_collections_count_total)` returned `{processor, orchestrator, {}}` — keeper and webapi collapsed into a label-less group — and `keeper_l2_probe_total{source=~"(...)"}`, `kestrel_active_connections{source=~"(...)"}` and `http_server_active_requests{source=~"(...)"}` each returned **0 series**. That would have produced four bogus Class-A misses (business Panel 9 and Panel 14 refIds A/B/C) plus a failed `AllFourClassesPresent`, against dashboards that are in fact correct.
- **Fix:** restored **all four** app Deployments at runtime with `kubectl set image deployment/<d> <d>=<d>:tags-const-1544`, exactly as the standing hazard prescribes, driven by a watcher loop running concurrently with `scripts/phase-80-up.ps1` (whose own STEP 2 is the `apply -k` and whose STEP 5 rollout gate would otherwise have timed out on the stale bits before it ever reached STEP 6 and started the shared forwards). `phase-80-up.ps1` then exited **0**; the eight shared forwards came up; all four Deployments confirmed on `:tags-const-1544`; `group by (source)` immediately returned exactly the four classes.
- **Files modified:** **none.** This is live cluster state only. The manifests were deliberately NOT edited — the tag drift belongs to whichever phase owns it, not to this one.
- **Commit:** n/a (no file change).
- **For 87-06:** the hazard note should be widened from two Deployments to four, and should record *why* it matters beyond probe failures — the stale bits silently drop the `source` label that every expression in both dashboards filters on, so a stale-image cluster makes correct dashboards look broken.

**2. [Rule 1 - Bug] `$obj.PSObject.Properties.Name` is unsafe under `Set-StrictMode -Version Latest`**

- **Found during:** Task 1, unit-checking the canonicaliser before the live run.
- **Issue:** reading `.Name` off a `PSMemberInfoCollection` is **member enumeration**, and on an *empty* collection StrictMode raises `The property 'Name' cannot be found on this object`. Several `{}` blocks inside the dashboard JSON (empty `options` / `custom` objects) hit exactly that path, so `Get-CanonicalNode` threw on the first recursion into one. The same idiom appeared in three other helpers and would have failed the same way on any empty JSON object.
- **Fix:** added a `Get-PropertyNames` helper that iterates the collection and reads each `PSPropertyInfo`'s own `Name` — which cannot trigger member enumeration — and routed all four call sites through it. Re-verified: canonicalisation now round-trips both dashboards (16050 / 23486 chars), is invariant under differing `id` / `version`, and still detects a one-field panel-title change.
- **Files modified:** `scripts/phase-87-dashboards-verify.ps1`
- **Commit:** `a32f271` (fixed before the file was first committed).
- This is a third variant of the PowerShell/JSON traps this phase has now hit three times (87-01's `,@(...)`, 87-02's assign-before-wrap, this one). It belongs in 87-06's findings as a standing rule: **under StrictMode, never dot into a member collection.**

### Design choices recorded rather than deviations

**3. `id` and `version` are excluded from the VER-02 byte comparison.** Rationale in the frontmatter decision above and in a block comment on `Get-DashboardSpecJson`. Everything the repo authors is compared; the two excluded keys are DB surrogates the repo never writes and that an `emptyDir`-backed Grafana must re-mint on every recreate. The canonicaliser was explicitly checked to still fail on a real spec change.

**4. A tenth claim boolean, `KeeperChainFiltered`.** The plan's STEP G asks for a VAR-03 chaining assertion but its STEP J boolean list has no slot for it. Adding a tenth claim (rather than silently AND-ing it into `PodDropdownSuperset`) keeps it visible in the artifact and in the verdict. All nine booleans the acceptance criteria name are present and true.

**5. Two extra diagnostic arrays.** `ClassBOffSpecExprs` and `MissingDropdownPods` mirror `EmptyClassAExprs` for the Class-B and dropdown assertions, so every failure mode is nameable from the artifact alone. Both empty on this run.

### Pre-Existing Working-Tree Condition (out of scope, NOT fixed)

The plan's acceptance criteria include `git status --porcelain .k8s-portforward-pids src/` being empty. `.k8s-portforward-pids` is clean. `src/` is **not**: `src/Keeper/Recovery/ReinjectConsumer.cs` is modified and `src/BaseApi.Service/Properties/launchSettings.json` is untracked. Both predate this phase and are documented identically in the 87-01, 87-02, 87-03 and 87-04 summaries. **This plan created, modified and deleted zero files under `src/`** — it touched exactly two files, both new, both its own. The intent of the criterion (no production-code change from a verification plan) is satisfied; the literal command is not, because the tree was already dirty there.

### Requirement Checkboxes

Frontmatter lists `VER-01, VER-02, DASH-01, DASH-02, DASH-03, RTD-01, RTD-02, BPD-02, VAR-01, VAR-02, VAR-03` — all eleven marked complete, this being the last owning plan for each:

- **VER-01** — every checked-in expression replayed through the datasource proxy and satisfied its class; the partition is recorded in the artifact.
- **VER-02** — pod delete/recreate reproduced both specs byte-identically with `meta.provisioned` still true.
- **DASH-01** — both dashboards provisioned from the repo files (`provisionedExternalId` confirmed).
- **DASH-02** — datasource `isDefault`, `readOnly`, health `OK`.
- **DASH-03** — 0 PVCs, `emptyDir` confirmed, and the UI save actively rejected with `Cannot save provisioned dashboard` (87-02 left this half-proven by design).
- **RTD-01 / RTD-02** — all 17 runtime targets, including the four honest substitutes, returned real samples live.
- **BPD-02** — the conservation overlays and all three dedicated fault stats behaved as designed; the guarded stats render an honest `0`.
- **VAR-01** — the source dropdown is exactly the four service classes.
- **VAR-02** — every live pod appears in the pod dropdown (superset, per F-7).
- **VAR-03** — both filters on every expression, and the keeper-scoped chain returns only keeper pods.

## Authentication Gates

None. Grafana's `admin:admin` is provisioned by the manifest and reachable only over loopback (T-87-02, accepted); the basic-auth header is built inline. No human step anywhere in the run.

## Known Stubs

None. The verify script is complete and was driven to a real Pass against the live cluster. The six guarded expressions are not stubs — each returned exactly one honest `0` sample meaning "this fault has not occurred in this window", which is what the guard exists to express.

## Threat Flags

None. This plan introduced one new network surface — a loopback-only `kubectl port-forward svc/grafana 3000:3000 --address 127.0.0.1` — which is the surface the plan's own threat register already covers (T-87-01), and it is torn down in the outer `finally` behind a recycled-PID guard. No schema change, no new auth path, no new file access pattern.

Mitigations confirmed green: **T-87-01** (`--address 127.0.0.1`, zero `0.0.0.0`), **T-87-11** (the shared PID filename appears nowhere in the script; the forward PID lives in one in-memory variable; `git status` on that file is empty and `/health/ready` still answered 200 afterwards), **T-87-12** (static-literal psql, no interpolated input), **T-87-18** (11 distinct exit codes, artifact written before the exit is resolved, `Resolve-AnalyzerExitCode` dot-sourced and fail-closed), **T-87-03** (the tamper was actively attempted and rejected), **T-87-19** (only the grafana pod was deleted; the user confirmed nothing else was running against `skp`), **T-87-20** (`ClassBExprs` publishes the full Class-B membership, and it contains exactly the six expected entries).

## Self-Check: PASSED

- `scripts/phase-87-dashboards-verify.ps1` — FOUND (parses, all guards pass)
- `analyzer-reports/phase-87-dashboards.json` — FOUND (parses, `Verdict` = `Pass`)
- commit `a32f271` — FOUND
- commit `3dc8dbd` — FOUND
