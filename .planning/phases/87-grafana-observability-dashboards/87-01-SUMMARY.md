---
phase: 87
plan: 01
subsystem: observability-dashboards
tags: [grafana, prometheus, promql, powershell, hermetic-gate, dashboard-as-code]
requires: []
provides:
  - "scripts/phase-87-dashboard-lint.ps1 — the Wave-0 hermetic gate (15 SHAPE + 8 COVERAGE rules, ~5s, no cluster)"
  - "k8s/dashboards/runtime.json — uid skp-runtime, the shared three-variable contract + 1 verified panel"
  - "k8s/dashboards/business.json — uid skp-business, the same contract + 1 verified conservation panel"
  - "Get-MetricTokens position scoping — the label-vs-metric disambiguation plans 87-04 Panels 10-13 depend on"
affects:
  - "87-02 (configMapGenerator needs both .json files to exist)"
  - "87-03 (authors runtime panels against this contract; C6 becomes satisfiable)"
  - "87-04 (authors business panels; C1-C5, C7, C8 become satisfiable)"
  - "87-05 (its Class-A/Class-B classifier is the same `or vector(0)` substring C3/C7/C8 partition on)"
tech-stack:
  added: []
  patterns:
    - "hermetic PowerShell gate modelled on scripts/verify-sourcehash-reproducible.ps1 (#!/usr/bin/env pwsh, #requires 7.0, CmdletBinding, StrictMode Latest, $RepoRoot, numbered section banners, remediation block on failure)"
    - "accumulate-not-throw failure model — one run reports every defect"
    - "position-scoped metric-token extraction ({-anchor) instead of a raw family-regex sweep"
key-files:
  created:
    - scripts/phase-87-dashboard-lint.ps1
    - k8s/dashboards/runtime.json
    - k8s/dashboards/business.json
  modified: []
decisions:
  - "Get-MetricTokens treats a metric-family regex match as a METRIC token only when the next non-space character is `{`; everything else is a BARE token checked against KNOWN_LABELS (rule S15). The rejected alternative — stripping by()/without()/on()/group_left() clause contents — handles the three grouping collisions but NOT the in-selector `http_response_status_code=~\"5..\"` collision on 87-04 Panel 13."
  - "Rule numbering stays append-only: S15 after S14, C7/C8 after C6, because plans 87-02..87-05 already cite rule ids by number."
  - "Lint helper functions return PLAIN arrays and every call site wraps in @(); the `return ,@(...)` unary-comma idiom is banned in this script because it emits an array-of-array through @(func())."
metrics:
  duration: ~35 min
  completed: 2026-07-27
  tasks: 2
  commits: 3
---

# Phase 87 Plan 01: Dashboard Lint Gate + Shared Dashboard Contract Summary

Hermetic ~5s PowerShell gate (15 SHAPE + 8 COVERAGE rules over a 31-name live-verified metric allowlist) plus the two dashboard JSONs carrying a byte-identical three-variable contract, with metric-token extraction scoped to selector position so PromQL grouping and filter labels are never mistaken for metric names.

## What Was Built

### `scripts/phase-87-dashboard-lint.ps1`

**Exact parameter surface** (what plans 87-02 through 87-06 call):

| Form | Behavior | Current exit |
|---|---|---|
| `pwsh -File scripts/phase-87-dashboard-lint.ps1` | all rules, both files | **1** (coverage C1-C8 not yet satisfiable) |
| `... -Dashboard runtime` | all rules, runtime.json only | **1** (C6 — see below) |
| `... -Dashboard business` | all rules, business.json only | **1** (C1-C5, C8) |
| `... -Dashboard runtime -ShapeOnly` | 14 shape rules, runtime.json | **0** |
| `... -Dashboard business -ShapeOnly` | 12 shape rules, business.json | **0** |
| `... -ShapeOnly` | 15 shape rules, both files | **0** |
| `... -SelfTest` | Get-MetricTokens fixture table only, reads no dashboard file | **0** |

`-Dashboard` is `[ValidateSet('runtime','business','all')]`, default `all`. `-SelfTest` short-circuits before any file read, so it stays runnable in every later wave.

**Rules implemented:** S1 parse · S2 top-level identity (uid / title / schemaVersion 39 / editable false / no `id` / no `__inputs` / no `__requires`) · S3 no `skp-prometheus` literal · S4 every datasource ref is `{type:prometheus, uid:${datasource}}` · S5 the three template variables incl. the F-8 `allValue: null` pin and `definition == query` · S6 panel types + no Angular `graph` · S7 both variable filters on every expr · S8 no `\bdotnet_` · S9 metric tokens allowlisted · S10 runtime.json is runtime-family only · S11 runtime timeseries group by `service_instance_id` · S12 no CPU/Uptime/Working Set titles · S13 no pinned `source="webapi"` · S14 `kubectl kustomize k8s/` exits 0 · S15 bare tokens are KNOWN_LABELS. Coverage: C1 nine domain counters · C2 dedicated fault panels (at-least-one + `stat` type qualifier) · C3 Class-B guard · C4 WebApi histogram · C5 `identityName` grouping · C6 the four RTD-02 substitute titles · C7 Class-A no-guard · C8 WebApi 5xx-ratio guard.

**Constants embedded:** RUNTIME 17 · DOMAIN 9 · WEBAPI 5 · KNOWN_LABELS 3. The `messaging_masstransit_*` and `dotnet_*` families are deliberately excluded (the latter is hard-failed by S8, research F-1).

### `Get-MetricTokens` — the position-scoping mechanism actually implemented

A family-regex match (`process_runtime_dotnet_\w+|orchestrator_\w+|keeper_\w+|processor_\w+|http_\w+|kestrel_\w+|messaging_\w+|dotnet_\w+`, prefixed by a `(?<![A-Za-z0-9_:])` lookbehind so a match cannot start mid-identifier) is classified as a **METRIC** token if and only if the next character after the match — allowing intervening spaces — is `{`:

```powershell
$isMetricPosition = $tail.TrimStart(' ').StartsWith('{')
```

Everything else lands in **Bare** and is tested by rule S15 against KNOWN_LABELS. Two sets are returned, not one, because the residue is the counterweight: without S15 a metric name written without its mandatory selector would fail the `{`-anchor, land in `Bare`, and be checked by nothing.

The alternation order matters — `process_runtime_dotnet_\w+` precedes `dotnet_\w+` so the leftmost match at a `process_runtime_dotnet_*` name consumes the whole token instead of leaving a bogus inner `dotnet_...` match behind.

The **strip-clauses** alternative was explicitly rejected and the rejection is recorded in the function's block comment: it handles 87-04 Panels 10/11/12 (`by (http_route)`, `by (le, http_route)`, `by (http_response_status_code)`) but NOT Panel 13, whose colliding `http_response_status_code` sits inside the selector braces.

### C6 coverage-rule state

`C6` (runtime.json must contain panels titled `GC committed memory`, `GC time per second`, `Process restarts in range`, `Pods reporting`) **currently FAILS all four titles** — by design. This plan authors one runtime panel (`GC collections / sec by generation`); the four RTD-02 honest substitutes are plan **87-03 Task 3**'s deliverable, and 87-03's final verify is the first `-Dashboard runtime` run (coverage enabled) expected to exit 0. Until then the applicable criterion is the `-ShapeOnly` form, which exits 0. The same holds for business coverage: C1, C2, C3, C4, C5 and C8 fail today and become satisfiable at plan **87-04 Task 3**. **C7 already passes** — the one business panel authored here (orchestrator consumed vs sent) is Class A and correctly carries no `or vector(0)` guard.

### `k8s/dashboards/runtime.json` / `business.json`

Identical top-level shape and byte-identical `templating.list`; only `uid`, `title`, `tags`, `description` and `panels` differ. Both carry a top-level `description` naming the net8.0 / OpenTelemetry.Instrumentation.Runtime 1.15.0 provenance and the net9.0 `dotnet_*` rename hazard (F-1), and both note that lint rule S8 makes that regression surface hermetically.

## Negative-Control Results

All three ran, produced exit 1 with the named remediation, and reverted cleanly to exit 0.

**NC-1 — the label-vs-metric collision control (the one that proves the position scoping does the work).** Replacing the `{`-anchor with an unconditional `$isMetricPosition = $true` (an unscoped family sweep) made `-SelfTest` **exit 1 with 5/6 fixture rows mismatched**, reporting exactly the predicted violations:

| Fixture | Reported as a metric token outside the allowlists |
|---|---|
| (a) `sum by (http_route) (...)` | `http_route` |
| (b) `sum by (le, http_route) (...)` | `http_route` |
| (c) `sum by (http_response_status_code) (...)` | `http_response_status_code` |
| (d) in-selector `http_response_status_code=~"5.."` | `http_response_status_code` |
| (f) S15 control | flipped to Metric, so S15 stopped rejecting it |

Restoring the anchor returned `-SelfTest` to **exit 0, 6/6**. This is what makes plan 87-04's Panels 10 through 13 known-good against rule S9 before Wave 2 starts (threat T-87-19).

**NC-2 — the `dotnet_*` name (research F-1).** Replacing `process_runtime_dotnet_gc_collections_count_total` with `dotnet_gc_collections_total` in runtime.json's panel target made the lint **exit 1** with the `[S8]` block naming both the panel title (`GC collections / sec by generation`) and the text `F-1`. Restoring the name returned **exit 0**.

**NC-3 — the missing `$pod` filter (VAR-03).** Deleting `, service_instance_id=~"$pod"` from business.json's refId B expression made the lint **exit 1** with `[S7] k8s/dashboards/business.json · panel: Orchestrator consumed vs sent (conservation) · fix: add service_instance_id=~"$pod" to the selector (VAR-03)`. Restoring it returned **exit 0**.

## Task Commits

| Task | Name | Commit | Files |
|---|---|---|---|
| 1 | Hermetic dashboard lint gate + verified allowlist | `a65918c` | `scripts/phase-87-dashboard-lint.ps1` |
| 1 (fix) | Array-return bug found by Task 2's verify | `b6777dc` | `scripts/phase-87-dashboard-lint.ps1` |
| 2 | Both dashboards' shared contract + first panel | `14cb588` | `k8s/dashboards/runtime.json`, `k8s/dashboards/business.json` |

## Verification Results

| Check | Result |
|---|---|
| `pwsh -File scripts/phase-87-dashboard-lint.ps1 -SelfTest` | exit **0**, 6/6 fixtures (a)-(f) PASS |
| `pwsh -File scripts/phase-87-dashboard-lint.ps1 -ShapeOnly` | exit **0** — 2 files, 2 panels, 3 targets, 15 rules |
| `kubectl kustomize k8s/` | exit **0** (unchanged by this plan; the configMapGenerator lands in 87-02) |
| Both JSONs `ConvertFrom-Json` | parse |
| uid / schemaVersion | `skp-runtime` / `skp-business`, both 39 |
| `templating.list.name` | `datasource, source, pod` in both |
| `source` + `pod` vars | `multi=True includeAll=True allValue=null refresh=2 sort=1` in both |
| `skp-prometheus` literal in `k8s/dashboards/*.json` | 0 matches (DASH-04) |
| top-level `id` / `__inputs` / `__requires` | absent in both |
| Every `targets[].expr` carries both filters | 3/3 targets, 0 missing |
| Allowlist greps (`...jit_compilation_time_nanoseconds_total`, `keeper_l2_probe_total`, `http_response_status_code`) | 1 / 3 / 11 matches |
| Script contains a shell JSON tool or a compose invocation | 0 matches of either |
| `git status --porcelain scripts/phase-80-up.ps1 k8s/02-configmaps.yaml k8s/21-prometheus.yaml` | empty (fenced-off infra untouched) |
| File deletions across all three commits | none |

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] The lint's unary-comma array returns faked an empty-name violation**
- **Found during:** Task 2 (the first `-ShapeOnly` run against real dashboard files)
- **Issue:** `Get-S9Violations`, `Get-S15Violations`, `Get-ByClauses`, `Get-DashboardPanels` and `Get-DashboardTargets` returned `,@(...)`. That idiom survives a scalar assignment but, when consumed as `@(func())`, emits a one-element array-of-array — so "no violations" became one violation whose token name was the empty string. Both correct dashboards failed S9 and S15 with a blank offender.
- **Fix:** helpers now return plain arrays and every call site wraps the call in `@()`; the reason is recorded as a block comment above `Get-S9Violations` so it is not reintroduced.
- **Files modified:** `scripts/phase-87-dashboard-lint.ps1`
- **Commit:** `b6777dc`

### Execution-Order Clarification (not a plan change)

Task 1's acceptance criteria reference `k8s/dashboards/runtime.json` and the `-ShapeOnly` green, but those files are Task 2's deliverable. Task 1 was therefore committed on the strength of `-SelfTest` (exit 0) plus its four static greps, with `-ShapeOnly` correctly reporting `[S1] file does not exist` for both dashboards; the full Task-1 acceptance set — `-ShapeOnly` green and all three negative controls — was executed after Task 2 landed and is recorded above. No rule was weakened to accommodate this.

### Pre-Existing Working-Tree Condition (out of scope, NOT fixed)

The plan's `<verification>` asks for `git status --porcelain src/` to be empty. It is **not**: `src/Keeper/Recovery/ReinjectConsumer.cs` is modified and `src/BaseApi.Service/Properties/launchSettings.json` is untracked. Both predate this execution (they are present in the session-start git snapshot) and neither was read or written by this plan. Per the scope boundary, they were left alone. **This plan created, modified and deleted zero files under `src/`** — the intent of the criterion is satisfied; the literal command is not, because the working tree was already dirty there.

### Requirement Checkboxes Deliberately NOT Marked Complete

This plan's frontmatter lists ten requirement ids, but every one of them is **re-delivered by a later plan in the same phase** (`RTD-01`/`RTD-03`/`DASH-04` → 87-03; `BPD-01..03`/`VAR-03`/`VAR-04`/`DASH-04` → 87-04; `VAR-01`/`VAR-02`/`VAR-03` → 87-05's live proof), and `REQUIREMENTS.md`'s traceability table is phase-scoped, not plan-scoped. Ticking `BPD-01` ("one dashboard covers the three pipeline services' nine domain counters") today — when `business.json` carries one panel and two counters — would misreport the phase. The checkboxes are left `Not started`; they flip when the last owning plan lands. What this plan actually delivers for those ids is the **mechanical enforcement** (lint rules C1-C8), not the content.

## Authentication Gates

None.

## Known Stubs

None. Both dashboard files render a real, live-verified panel; the remaining panels are the explicit deliverables of plans 87-03 and 87-04, and the coverage rules C1-C6 and C8 are the mechanical proof that those plans landed.

## Interface Contract Published

Plans 87-02, 87-03 and 87-04 build against exactly this:

```
k8s/dashboards/runtime.json   -> uid "skp-runtime",  title "SKP Runtime Instrumentation", tags ["skp","runtime"]
k8s/dashboards/business.json  -> uid "skp-business", title "SKP Business / Pipeline",     tags ["skp","business"]

templating.list (byte-identical in both):
  datasource : type "datasource", query "prometheus"
  source     : label_values(process_runtime_dotnet_gc_collections_count_total, source)
  pod        : label_values(process_runtime_dotnet_gc_collections_count_total{source=~"$source"}, service_instance_id)
               both query vars: multi true, includeAll true, allValue null, refresh 2, sort 1, definition == query

Mandatory on every targets[].expr:  {source=~"$source", service_instance_id=~"$pod"}
Attach the selector directly to the metric name — NO space before `{` (rules S9 / S15).
Rate window: $__rate_interval, never a hardcoded [5m].
```

## Self-Check: PASSED

- `scripts/phase-87-dashboard-lint.ps1` — FOUND
- `k8s/dashboards/runtime.json` — FOUND
- `k8s/dashboards/business.json` — FOUND
- commit `a65918c` — FOUND
- commit `b6777dc` — FOUND
- commit `14cb588` — FOUND
