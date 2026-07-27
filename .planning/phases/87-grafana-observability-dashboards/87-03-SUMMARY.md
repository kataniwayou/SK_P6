---
phase: 87
plan: 03
subsystem: observability-dashboards
tags: [grafana, prometheus, promql, dashboard-as-code, runtime-instrumentation]
requires:
  - "scripts/phase-87-dashboard-lint.ps1 (87-01) — the hermetic gate every task verified against"
  - "k8s/dashboards/runtime.json (87-01) — the three-variable contract + panel id 1, the structural template"
provides:
  - "k8s/dashboards/runtime.json — the complete 17-panel runtime dashboard (uid skp-runtime)"
  - "coverage rule C6 satisfied — `-Dashboard runtime` (coverage enabled) now exits 0 for the first time"
  - "the four RTD-02 honest-substitute panels with descriptions naming the metric that does not exist"
affects:
  - "87-02 (its configMapGenerator picks up this file verbatim; no interface change)"
  - "87-05 (the live proof renders these 17 panels; panel 17's A4 caveat is an explicit live question for it)"
  - "87-06 (phase findings — the A4 `resets()` caveat is carried forward as an item)"
tech-stack:
  added: []
  patterns:
    - "one panel per axis with `sum/max by (source, service_instance_id, …)` + a table legend showing per-series mean and max, instead of `repeat` rows on $source (research 'Row organisation')"
    - "gauges read with `max by (...)`, counters with `rate(...[$__rate_interval])` — never a hardcoded [5m]"
    - "every substitution panel carries a description naming the absent real metric AND why it cannot be obtained this milestone"
key-files:
  created: []
  modified:
    - k8s/dashboards/runtime.json
decisions:
  - "Panel 17's threshold steps are green at null / red at 1, so any nonzero restart-proxy value colours red — deliberately louder than the A4 confidence warrants, because a false red is cheap (read the caveat in the description) and a missed restart is not."
  - "Both stat panels use `reduceOptions.calcs = [lastNotNull]`. For `Pods reporting` that is the current pod count; for `Process restarts in range` the `[$__range]` window already collapses the range, so lastNotNull reads the range total rather than re-reducing it."
  - "`Loaded assemblies` and `Active timers` were split into two panels (ids 13/14) rather than the single combined row the research table sketched — one metric per panel keeps the table legend readable and keeps every gridPos origin unique."
metrics:
  duration: ~20 min
  completed: 2026-07-27
  tasks: 3
  commits: 3
---

# Phase 87 Plan 03: Runtime Instrumentation Dashboard Panels Summary

`k8s/dashboards/runtime.json` grown from one contract panel to the complete 17-panel set over the live-verified `process_runtime_dotnet_*` inventory — GC, memory, thread pool, exceptions, contention, JIT and two uptime substitutes — every expression filtered by both dropdowns and every timeseries grouped per replica, closing coverage rule C6.

## What Was Built

### The 17 panels, in file order

| id | Title | Type | Unit | Aggregation |
|---|---|---|---|---|
| 1 | GC collections / sec by generation | timeseries | ops | `sum by (source, service_instance_id, generation) (rate(...))` (87-01) |
| 2 | GC heap size by generation | timeseries | bytes | `sum by (…, generation)` |
| 3 | GC heap fragmentation by generation | timeseries | bytes | `sum by (…, generation)` |
| 4 | Allocation rate | timeseries | Bps | `sum by (…) (rate(...))` |
| 5 | **GC committed memory** | timeseries | bytes | `sum by (…)` — RTD-02 substitute |
| 6 | Live object bytes | timeseries | bytes | `sum by (…)` |
| 7 | **GC time per second** | timeseries | percentunit | `sum by (…) (rate(...)) / 1e9` — RTD-02 substitute |
| 8 | Thread-pool threads | timeseries | short | `max by (…)` |
| 9 | Thread-pool queue length | timeseries | short | `max by (…)` |
| 10 | Thread-pool completion rate | timeseries | ops | `sum by (…) (rate(...))` |
| 11 | Exception rate | timeseries | ops | `sum by (…) (rate(...))` |
| 12 | Lock contention rate | timeseries | ops | `sum by (…) (rate(...))` |
| 13 | Loaded assemblies | timeseries | short | `max by (…)` |
| 14 | Active timers | timeseries | short | `max by (…)` |
| 15 | JIT methods compiled / sec | timeseries | ops | `sum by (…) (rate(...))` |
| 16 | **Pods reporting** | stat | short | `count by (source) (…{…, generation="gen0"})` — RTD-02 substitute |
| 17 | **Process restarts in range** | stat | short | `sum by (…) (resets(…[$__range]))` — RTD-02 substitute |

15 timeseries + 2 stat. 17 targets, one per panel, all refId A. Layout is a strict two-column progression — `h 8, w 12`, x alternating 0/12, y advancing by 8 — giving 17 unique `(x, y)` origins.

Fourteen of the seventeen allowlisted runtime metric names are referenced. The three not used are `process_runtime_dotnet_jit_il_compiled_size_bytes_total` and `process_runtime_dotnet_jit_compilation_time_nanoseconds_total` (the JIT axis is represented by the method-count rate, which is the legible one) and — as an axis rather than a name — nothing else: `process_runtime_dotnet_jit_methods_compiled_count_total` carries both the JIT panel and the restart proxy. No lint rule requires full runtime-allowlist coverage (C6 asserts titles, not names), so this is by design, not a gap.

### The four RTD-02 honest substitutions

Research F-2 established that this instrumentation emits **no process CPU, no uptime, and no working set**, and that obtaining any of the three needs either `OpenTelemetry.Instrumentation.Process` (a `src/` change) or a cAdvisor/kubelet scrape job (a Prometheus scrape-config change) — both fenced out of this milestone. Each substitute panel therefore states in its `description` what it actually measures, what it is standing in for, and the exact reason the real metric cannot be added:

- **GC committed memory** — GC-committed bytes, *not* the OS working set; also carries the F-3 caveat that this gauge is absent for `source="webapi"` until that pod performs its first GC.
- **GC time per second** — seconds of GC per second, a GC-*pressure* proxy; explicitly not titled CPU, because an empty or mislabelled CPU panel is a defect under the RTD-02 amendment (lint S12).
- **Pods reporting** — pods currently exporting runtime series per selected class; the availability readout, not uptime.
- **Process restarts in range** — a counter-reset proxy; names `process_start_time_seconds` as the metric that does not exist, and carries the **A4 caveat** in full (see below).

The word `uptime` appears in this file only inside descriptions — never in a title. Rule S12 verifies that mechanically.

## Finding Carried Forward: the A4 `resets()` caveat (for 87-06)

Panel 17's `resets(process_runtime_dotnet_jit_methods_compiled_count_total[$__range])` is a restart **signal**, not a certified restart count. A counter reset is also produced by the OTel collector's `metric_expiration` (5 min default) when a series goes quiet, so a stable stack can in principle show a spurious nonzero value. Research assumption A4 rates this Medium confidence and asks for validation before shipping.

**This plan ships the panel with the caveat written into the panel body rather than dropping it**, because a restart signal with a stated failure mode is strictly more useful to an operator than no restart panel at all, and rule C6 requires the title to exist. **Plan 87-05's live run must record whether a stable stack produces nonzero values here**; if it does, 87-06 should either widen the panel description or replace the source counter with one that cannot go quiet. That instruction is written into panel 17's own `description`, so it travels with the artifact.

## Task Commits

| Task | Name | Commit | Files |
|---|---|---|---|
| 1 | GC and memory panel family (ids 2-7) | `b93f5b9` | `k8s/dashboards/runtime.json` |
| 2 | Thread-pool, exception, contention and JIT family (ids 8-15) | `1191c56` | `k8s/dashboards/runtime.json` |
| 3 | The two uptime-substitute stat panels (ids 16-17) + C6 | `9b3c1b9` | `k8s/dashboards/runtime.json` |

Zero file deletions across all three commits (`git diff --diff-filter=D HEAD~1 HEAD` empty after each).

## Verification Results

| Check | Result |
|---|---|
| `pwsh -File scripts/phase-87-dashboard-lint.ps1 -Dashboard runtime -ShapeOnly` (after Task 1) | exit **0** — 7 panels, 7 targets, 14 rules |
| `pwsh -File scripts/phase-87-dashboard-lint.ps1 -Dashboard runtime -ShapeOnly` (after Task 2) | exit **0** — 15 panels, 15 targets, 14 rules |
| `pwsh -File scripts/phase-87-dashboard-lint.ps1 -Dashboard runtime` (full, coverage on) | exit **0** — 17 panels, 17 targets, **15 rules incl. C6** |
| `panels.Count` | **17** |
| Panel types | timeseries **15**, stat **2** (`Pods reporting`, `Process restarts in range`) |
| Titles matching `(?i)\b(cpu\|uptime\|working set)\b` | **0** |
| Targets missing `source=~"$source"` | **0 / 17** |
| Targets missing `service_instance_id=~"$pod"` | **0 / 17** |
| Timeseries panels missing `by (source, service_instance_id` | **0 / 15** |
| Exprs matching `\bdotnet_` | **0** |
| Metric tokens outside the 17-name RUNTIME allowlist (S9 + S10) | **0** |
| Unique `gridPos` (x, y) origins | **17 of 17** |
| Panel 17 expr contains `resets(` and `[$__range]` | both **True** |
| Panel 17 description contains `metric_expiration` and `uptime` | both **True** |
| C6 required titles present | 4 / 4 |
| `kubectl kustomize k8s/` | exit **0** |
| `git status --porcelain k8s/23-grafana.yaml k8s/kustomization.yaml k8s/dashboards/business.json scripts/phase-87-dashboard-lint.ps1` | **empty** — the other plans' files untouched |

**First green coverage run in the phase.** 87-01 recorded `-Dashboard runtime` as exit **1** on all four C6 titles by design; this plan is the one that flips it. `-ShapeOnly` remains the form used mid-task.

### Note on the `grafana-dashboards` ConfigMap

The plan's `<verification>` asks that the generated `grafana-dashboards` ConfigMap carry the updated `runtime.json`. It does **not** yet, and cannot: the `configMapGenerator` that produces it is plan **87-02**'s deliverable, and 87-02 runs in the same wave as this plan. `kubectl kustomize k8s/` exits 0 (the S14 / T-87-08 assertion this plan is accountable for), and the file this plan owns is exactly what 87-02's generator will pick up — no interface change was made to it. The end-to-end ConfigMap assertion belongs to 87-02's own verify and to the 87-05 live run.

## Deviations from Plan

None affecting behavior. Two authoring choices worth recording, neither a rule change:

**1. `Loaded assemblies` and `Active timers` shipped as two panels, not one.** The research panel table lists them on a single combined row (`max by (…) (assemblies_count)` · `max by (…) (timer_count)`). The plan's Task 2 already specified them as separate panels 13 and 14 with distinct titles, and that is what shipped — two axes on one panel would have shared a table legend across incomparable series. No acceptance criterion or lint rule prefers either form.

**2. Three runtime allowlist names are unreferenced** (`jit_il_compiled_size_bytes_total`, `jit_compilation_time_nanoseconds_total`, and no third — see the What Was Built note). The allowlist is a *permission* list, not a coverage requirement; no rule asserts full use, and adding low-signal JIT byte/nanosecond panels would dilute the JIT axis the method-count rate already covers.

### No lint rule was weakened

Every task's verify ran the real script with no flags removed and no rule edited. `scripts/phase-87-dashboard-lint.ps1` is byte-identical to its 87-01 state (`git status --porcelain` on it is empty). No PromQL this plan authored was rejected by the lint, so the wave-context "report a false rejection as a finding" path was never exercised — there is nothing to report there.

### Pre-Existing Working-Tree Condition (out of scope, NOT fixed)

The plan's Task-3 acceptance criteria include `git status --porcelain src/` being empty. It is **not**: `src/Keeper/Recovery/ReinjectConsumer.cs` is modified and `src/BaseApi.Service/Properties/launchSettings.json` is untracked. Both predate this phase (they appear in the session-start git snapshot and are documented identically in the 87-01 SUMMARY). **This plan created, modified and deleted zero files under `src/`** — the intent of the criterion (no production-code change from a dashboards plan) is satisfied; the literal command is not, because the tree was already dirty there.

### Requirement Checkboxes

This plan's frontmatter lists `RTD-01, RTD-02, RTD-03, VAR-03, DASH-04`. `RTD-01/02/03` are fully delivered here — the runtime dashboard is complete and its content is final. `VAR-03` and `DASH-04` span both dashboards and are re-delivered by 87-04 (business panels) and proven live by 87-05, so the phase-scoped traceability table only becomes truthful for them once those land. Marked complete: **RTD-01, RTD-02, RTD-03**. Left open: VAR-03, DASH-04.

## Authentication Gates

None.

## Known Stubs

None. All 17 panels carry a real expression over a live-verified metric name. The four substitution panels are not stubs — they measure a real, different quantity and say so in their descriptions; the axes they stand in for are documented as unobtainable in this milestone (research F-2), not as future work in this phase.

## Threat Flags

None. This plan introduced no new network endpoint, auth path, file access pattern or schema change; it edited one checked-in JSON file. The threat-register mitigations assigned to it (T-87-08 lint+kustomize, T-87-10 `${datasource}` only, T-87-14 allowlist + no `dotnet_`, T-87-15 forbidden titles + honest-substitute descriptions, T-87-13 both filters on every expression) are each confirmed green in the Verification Results table above.

## Self-Check: PASSED

- `k8s/dashboards/runtime.json` — FOUND (17 panels, parses)
- commit `b93f5b9` — FOUND
- commit `1191c56` — FOUND
- commit `9b3c1b9` — FOUND
