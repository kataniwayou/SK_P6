---
phase: 87
plan: 04
subsystem: observability-dashboards
tags: [grafana, prometheus, promql, dashboard-as-code, conservation, fault-indicators, webapi]
requires:
  - "scripts/phase-87-dashboard-lint.ps1 (87-01) — the hermetic gate every task verified against, unmodified"
  - "k8s/dashboards/business.json (87-01) — the three-variable contract + panel id 1, the structural template"
  - "Get-MetricTokens position scoping (87-01) — what makes Panels 10-13's http_* grouping/filter labels pass rules S9/S15"
provides:
  - "k8s/dashboards/business.json — the complete 14-panel business / pipeline dashboard (uid skp-business)"
  - "coverage rules C1, C2, C3, C4, C5 and C8 satisfied — `-Dashboard business` (coverage enabled) exits 0 for the first time"
  - "the full 23-rule gate green across BOTH dashboards for the first time in the phase"
  - "the VER-01 Class-A / Class-B partition of all 19 business targets, mechanically enforced by C3/C7/C8"
affects:
  - "87-02 (its configMapGenerator picks up this file verbatim; no interface change)"
  - "87-05 (its STEP F classifier reads the 6 guarded / 13 unguarded split recorded below; STEP E must drive WebApi traffic before Panels 10-13 are non-empty)"
  - "87-06 (phase findings — the keeper Class-B panels are unprovable-by-happy-path and are an explicit live question)"
tech-stack:
  added: []
  patterns:
    - "consumed-and-sent overlaid on ONE axis per service (two targets, one panel) so conservation reads at a glance, rather than two panels the eye must correlate"
    - "each fault indicator on its own dedicated `stat` panel whose targets reference exactly one metric family, so lint rule C2's exact-token-set match can assert the dedicated-panel property"
    - "the `or vector(0)` guard is a two-valued CLASSIFICATION, not a formatting nicety — its presence/absence is the same substring 87-05's live classifier partitions on, so it is placed per-expression and never propagated by copy-paste"
key-files:
  created: []
  modified:
    - k8s/dashboards/business.json
decisions:
  - "Threshold steps use the Grafana base-step convention `{green, value: null}` + `{red, value: <smallest meaningful positive>}` rather than a literal `{green, value: 0}` step. For the four integer-count stats (ids 4, 6, 7, 8) the red step is 1, because the operand is an integer message/step/drop count so 'above 0' is 'at 1'. For the percentunit 5xx ratio (id 13) the red step is 0.0001 (0.01% of requests), because a red step at 1 would mean 100% and would never fire. Each choice is recorded in its own panel description."
  - "The BPD-03 /health/ route-drop caveat was written into Panel 10's description (panel-level) rather than the dashboard-level description, so it travels with the panel it actually constrains; Panels 11 and 12 cross-reference it and Panel 14 states explicitly that it does NOT apply to the three gauges."
  - "Panel 14's three gauges are read directly, not through rate() — they are gauges, and rate() over a gauge is meaningless."
metrics:
  duration: ~25 min
  completed: 2026-07-27
  tasks: 3
  commits: 3
---

# Phase 87 Plan 04: Business / Pipeline Dashboard Panels Summary

`k8s/dashboards/business.json` grown from one contract panel to the complete 14-panel set — three per-service conservation overlays, two gap panels, three dedicated guarded fault stats, the keeper heartbeat and five WebApi ASP.NET Core request panels — with every VER-01 Class-B expression guarded, every Class-A expression unguarded, and the full 23-rule lint green across both dashboards for the first time in the phase.

## The 14 panels, in file order

| id | Title | Type | Unit | VER-01 class | Guard |
|---|---|---|---|---|---|
| 1 | Orchestrator consumed vs sent (conservation) | timeseries | ops | **A** (×2 targets) | no (87-01) |
| 2 | Keeper consumed vs sent (conservation) | timeseries | ops | **B** (×2 targets) | **yes ×2** |
| 3 | Processor consumed vs sent by image (conservation) | timeseries | ops | **A** (×2 targets) | no |
| 4 | Cross-tier conservation gap (orchestrator sent - processor consumed) | stat | short | **A** | no |
| 5 | Per-processor dispatch gap | timeseries | short | **A** | no |
| 6 | Orchestrator unresolved steps in range | stat | short | **B** | **yes** |
| 7 | Processor dropped spawns in range | stat | short | **B** | **yes** |
| 8 | Keeper consumed - sent gap in range | stat | short | **B** | **yes** |
| 9 | Keeper L2 probe heartbeat | timeseries | ops | **A** | no |
| 10 | WebApi request rate by route | timeseries | reqps | **A** | no |
| 11 | WebApi p95 request duration by route | timeseries | s | **A** | no |
| 12 | WebApi status-code mix | timeseries | reqps | **A** | no |
| 13 | WebApi 5xx ratio | stat | percentunit | **B** | **yes** |
| 14 | WebApi in-flight requests and Kestrel connections | timeseries | short | **A** (×3 targets) | no |

9 timeseries + 5 stat. **19 targets.** Layout is a strict two-column progression (`h 8, w 12`, x alternating 0/12, y advancing by 8) giving **14 unique `(x, y)` origins**.

## The VER-01 partition plan 87-05 must assert

**6 guarded (Class B) — each must return EXACTLY ONE sample, value 0 on a happy path.** These are the expressions 87-05's STEP F classifies by the presence of the `or vector(0)` substring:

```promql
# Panel 2 refId A
sum by (source) (rate(keeper_messages_consumed_total{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval])) or vector(0)
# Panel 2 refId B
sum by (source) (rate(keeper_messages_sent_total{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval])) or vector(0)
# Panel 6 refId A
sum(increase(orchestrator_step_unresolved_total{source=~"$source", service_instance_id=~"$pod"}[$__range])) or vector(0)
# Panel 7 refId A
sum(increase(processor_spawn_dropped_total{source=~"$source", service_instance_id=~"$pod"}[$__range])) or vector(0)
# Panel 8 refId A
sum(increase(keeper_messages_consumed_total{source=~"$source", service_instance_id=~"$pod"}[$__range])) - sum(increase(keeper_messages_sent_total{source=~"$source", service_instance_id=~"$pod"}[$__range])) or vector(0)
# Panel 13 refId A
sum(rate(http_server_request_duration_seconds_count{source=~"$source", service_instance_id=~"$pod", http_response_status_code=~"5.."}[$__rate_interval])) / sum(rate(http_server_request_duration_seconds_count{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval])) or vector(0)
```

**13 unguarded (Class A) — each must return REAL samples.** Panels 1 (A, B), 3 (A, B), 4, 5, 9, 10, 11, 12, 14 (A, B, C). 6 + 13 = 19, so no target sits outside the partition.

**Two live-proof cautions for 87-05:**

1. **Panels 10-13 are near-empty until traffic is driven.** The collector drops every `http.server.request.duration` datapoint whose `http.route` starts with `/health/` (`k8s/02-configmaps.yaml:64-69`), so on an idle stack only the orchestration endpoints appear — 2 series, `_count` = 2 (research "WebApi row"). STEP E's traffic drive is what makes these four Class-A panels assertable. Panel 14's three gauges are NOT affected (they carry no `http.route` attribute) and are populated on an idle stack.
2. **Panel 3's `identityName` grouping needs at least one processor message.** It is Class A, and `identityName` is a *datapoint* tag present only on `processor_messages_consumed_total` / `_sent_total` — a stack with no dispatched work yields no series at all.

## Why the guard placement is load-bearing, not cosmetic

The `or vector(0)` substring is the *only* thing 87-05's mechanical classifier reads, so a misplaced guard is a silent correctness failure in both directions:

- **A guard omitted from a Class-B expression** (lint C3) ships a panel that renders "No data", which a reviewer reads as "no faults" (T-87-16), and fails the live proof because the expression returns zero samples.
- **A guard added to a Class-A expression** (lint C7) silently demotes a real signal: a **dead keeper** (Panel 9) or a **fully stalled pipeline** (Panels 1/3/4/5) would render `0` and **PASS** the live proof (T-87-20).

The keeper is the case where both mistakes are tempting at once, and this dashboard splits them deliberately: Panel 2's conservation overlay is **guarded** (those two counters increment only inside `RecoveryConsumerBase.Consume` / `CountSent`, so they fire only during a recovery event — research F-4 measured zero live series and Pitfall 6 records they are absent from the entire 15-day `__name__` index, and 87-05's STEP E is a happy-path `FanOutSeeder` run with no fault scenario), while Panel 9's L2 probe heartbeat is **unguarded** (2 live series on a healthy stack). Panel 2's description states plainly that a flat `0` means "no recovery traffic in this window", **not** "keeper down" — and names Panel 9 as the liveness signal.

The WebApi 5xx ratio (Panel 13) is the one expression in the phase that is Class B without being one of C3's four domain counters, which is exactly why lint rule **C8** exists instead of an exception inside C3 or C7. Its description says so, so the guard is not mistaken for a copy-paste from the fault stats.

## Task Commits

| Task | Name | Commit | Panels added |
|---|---|---|---|
| 1 | Keeper/processor conservation panels + cross-tier gaps | `e8feed9` | ids 2-5 |
| 2 | Dedicated guarded fault-indicator panels + keeper heartbeat | `7d874f3` | ids 6-9 |
| 3 | WebApi ASP.NET Core request row + close the coverage gate | `277f981` | ids 10-14 |

Zero file deletions across all three commits (`git diff --diff-filter=D HEAD~1 HEAD` empty after each). One file modified; nothing created.

## Verification Results

| Check | Result |
|---|---|
| `... -Dashboard business -ShapeOnly` (after Task 1) | exit **0** — 5 panels, 8 targets, 12 rules |
| `... -Dashboard business -ShapeOnly` (after Task 2) | exit **0** — 9 panels, 12 targets, 12 rules |
| `... -Dashboard business` (full, coverage on) | exit **0** — 14 panels, 19 targets, **19 rules incl. C1 C2 C3 C4 C5 C7 C8** |
| `... ` (no flags — BOTH dashboards, all rules) | exit **0** — 31 panels, 36 targets, **23 rules** (first fully green run in the phase) |
| `... -SelfTest` | exit **0**, 6/6 fixtures — position scoping intact after Panels 10-13 landed |
| `panels.Count` | **14** |
| Panel types | timeseries **9**, stat **5** |
| All nine domain counters referenced (C1) | **9 / 9** |
| All five WebApi names referenced | **5 / 5** |
| Targets missing `source=~"$source"` or `service_instance_id=~"$pod"` | **0 / 19** |
| Exprs matching `\bdotnet_` | **0** |
| Literal `source="webapi"` / `source=~"webapi"` (S13) | **0** |
| Exprs with `by (service_name` or `service_name=~` | **0** |
| Exprs with `by (identityName)` (C5 / VAR-04) | **2** (Panel 3 A and B) |
| C3 violations (Class-B expr without a guard) | **0** |
| C7 violations (Class-A expr with a guard) | **0** |
| Panel 13 guarded (C8) | **True** |
| Panels mixing WebApi and domain counters (BPD-03) | **0** |
| C2 exact-token dedicated panels | id 6 `{orchestrator_step_unresolved_total}`, id 7 `{processor_spawn_dropped_total}`, id 8 `stat` `{keeper_messages_consumed_total, keeper_messages_sent_total}` — 3/3 |
| Unique `gridPos` (x, y) origins | **14 of 14** |
| `kubectl kustomize k8s/` | exit **0** |
| `git status --porcelain k8s/23-grafana.yaml k8s/kustomization.yaml k8s/dashboards/runtime.json scripts/phase-87-dashboard-lint.ps1 scripts/phase-80-up.ps1 k8s/02-configmaps.yaml k8s/21-prometheus.yaml` | **empty** — every fenced-off and other-plan-owned file untouched |

## Deviations from Plan

None affecting behavior. Three authoring choices worth recording, none a rule change:

**1. Threshold step values are `null` / smallest-meaningful-positive, not a literal `0` step.** The plan and the acceptance criteria say "thresholds green at 0 and red above 0". Grafana's threshold model requires the FIRST step to be the base step with `value: null`; a literal `{green, value: 0}` first step is not the canonical form and leaves negative values uncoloured. Shipped as `{green, null}` + `{red, 1}` for the four integer-count stats (ids 4, 6, 7, 8) — the operand is an integer count, so "above 0" is exactly "at 1" — and `{green, null}` + `{red, 0.0001}` for the percentunit 5xx ratio (id 13), where a red step at 1 would mean 100% and could never fire. Each panel's description states its own choice and why. Semantics are identical to the criterion's intent: zero is green, anything above zero is red. No lint rule inspects threshold values.

**2. Panel 4's `legendFormat` is a literal `"gap"` (likewise Panel 8).** Neither expression has a grouping clause, so there is no label to interpolate; a `{{...}}` template would render empty.

**3. The BPD-03 `/health/` caveat is panel-level, not dashboard-level.** The plan permits either. It sits on Panel 10 (the panel it most directly constrains), is cross-referenced from Panels 11 and 12, and Panel 14 explicitly records that it does NOT apply to the three Kestrel/in-flight gauges — which is the non-obvious half.

### No lint rule was weakened, and no correct PromQL was rejected

Every task's verify ran the real script with no flags removed and no rule edited. `scripts/phase-87-dashboard-lint.ps1` is byte-identical to its 87-01 state (`git status --porcelain` on it is empty). **The wave-context "report a false rejection as a finding" path was never exercised** — not one expression this plan authored was rejected by any rule on any run, including the four `http_*` label forms (Panels 10-13) that motivated 87-01's `Get-MetricTokens` position scoping. `-SelfTest` re-run after Panels 10-13 landed still reports 6/6, so the scoping is confirmed intact against the real panels it was built for, not only against its fixtures. There is nothing to report on that axis.

### Pre-Existing Working-Tree Condition (out of scope, NOT fixed)

Task 3's acceptance criteria include `git status --porcelain src/` being empty. It is **not**: `src/Keeper/Recovery/ReinjectConsumer.cs` is modified and `src/BaseApi.Service/Properties/launchSettings.json` is untracked. Both predate this phase — they appear in the session-start git snapshot and are documented identically in the 87-01 and 87-03 summaries. **This plan created, modified and deleted zero files under `src/`**; it touched exactly one file, `k8s/dashboards/business.json`. The intent of the criterion (no production-code change from a dashboards plan) is satisfied; the literal command is not, because the tree was already dirty there.

### Note on the `grafana-dashboards` ConfigMap

The plan's `<verification>` asks that the generated ConfigMap carry the updated `business.json`. It does **not** yet, and cannot: the `configMapGenerator` producing it is plan **87-02**'s deliverable. `kubectl kustomize k8s/` exits 0 (the S14 / T-87-08 assertion this plan is accountable for) and the file this plan owns is exactly what 87-02's generator will pick up — no interface change was made to it. Identical to the note 87-03 recorded.

### Requirement Checkboxes

Frontmatter lists `BPD-01, BPD-02, BPD-03, VAR-03, VAR-04, DASH-04`. All six are fully delivered as of this plan and are marked complete:

- **BPD-01** — all nine domain counters referenced (C1 green).
- **BPD-02** — conservation legible at a glance (three overlays + two gap panels); each fault indicator on its own dedicated panel (C2 green), guarded so it renders 0 (C3 green). Delivered as amended: no reinject/drop-outcome breakdown, because no such counter exists (F-5).
- **BPD-03** — the WebApi is five ASP.NET Core request panels and zero domain counters (C4 green, S13 green).
- **VAR-03** — both dropdown filters on every expr in BOTH dashboards (S7 green over 36 targets).
- **VAR-04** — corrected per F-6 and in force: processor discrimination via `identityName`, zero `service_name` grouping (C5 green).
- **DASH-04** — no hardcoded datasource uid anywhere; every reference is `${datasource}` (S3/S4 green over both files).

`VAR-01` and `VAR-02` remain open — they are 87-05's live proof (the dropdowns must actually populate against a running Prometheus), not something a checked-in JSON can establish.

## Authentication Gates

None.

## Known Stubs

None. All 14 panels carry a real expression over a live-verified metric name. The six guarded expressions are not stubs: they render an honest `0` that means "this fault has not occurred in this window", and each says so in its own description. The two axes deliberately absent — a keeper reinject/drop-outcome breakdown (no Prometheus counter exists; it lives only in Elasticsearch as `attributes.ReinjectOutcome`, and ES panels are out of scope) and WebApi domain counters (the service registers no domain meter) — are documented as unobtainable in this milestone, not as future work in this phase.

## Threat Flags

None. This plan introduced no network endpoint, auth path, file access pattern or schema change; it edited one checked-in JSON file. The threat-register mitigations assigned to it are each confirmed green above: T-87-08 (lint + `kubectl kustomize` on every task), T-87-10 (`${datasource}` only, S3/S4), T-87-14 (allowlist S9 + bare-token S15), T-87-16 (C3 guards on all six Class-B exprs), T-87-20 (C7 — zero guarded Class-A exprs), T-87-21 (C8 — Panel 13's guard), T-87-17 (Panel 8 titled for the gap, description cites the Phase-74 deletion, zero titles matching reinject/drop outcome), T-87-13 (both filters on all 19 targets).

## Self-Check: PASSED

- `k8s/dashboards/business.json` — FOUND (14 panels, parses, full lint exit 0)
- commit `e8feed9` — FOUND
- commit `7d874f3` — FOUND
- commit `277f981` — FOUND
