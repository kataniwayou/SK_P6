---
phase: 87-grafana-observability-dashboards
status: passed
date: 2026-07-28
method: inline (goal-backward against ROADMAP success criteria; no verifier agent spawned)
evidence: analyzer-reports/phase-87-dashboards.json
requirements: [DASH-01, DASH-02, DASH-03, DASH-04, RTD-01, RTD-02, RTD-03, BPD-01, BPD-02, BPD-03, VAR-01, VAR-02, VAR-03, VAR-04, VER-01, VER-02]
---

# Phase 87 — Verification

Verified **inline** rather than by spawning a `gsd-verifier` agent (session instruction: no subagents unless requested). Every claim below is backed by a command run against the live stack or the checked-in files, not by reading the plans.

## Success criteria (from ROADMAP.md)

| # | Criterion | Verdict | Evidence |
|---|---|---|---|
| 1 | `apply -k` brings up Grafana with Prometheus as default datasource and both dashboards present, zero manual UI setup (DASH-01/02/03) | **PASS** | `deploy/grafana 1/1`; `/api/datasources/uid/skp-prometheus` → `isDefault=True readOnly=True`; `/api/search` → `skp-runtime` + `skp-business`, both folder SKP |
| 2 | Deleting the Grafana pod reproduces both dashboards identically from the repo (DASH-03, VER-02) | **PASS** | `ReproducedAfterPodDelete=True`, `NoPvc=True` in the verdict artifact; pod deleted and recaptured within the proof |
| 3 | Runtime dashboard renders GC/memory/thread-pool/exception/JIT for all four classes; no panel titled CPU/Uptime/Working Set (RTD-01/02/03) | **PASS** | 17 panels, title scan clean; lint rule S12 enforces it; substitution panels 5/7/16/17 carry explanatory descriptions |
| 4 | Business dashboard renders the nine pipeline counters with conservation legible and fault indicators on dedicated panels (BPD-01/02/03) | **PASS** | 14 panels / 19 targets; lint coverage rules C1-C5, C8 green; conservation legibility confirmed by rendered capture after the `timeInterval` fix |
| 5 | `source` lists the four classes; pod dropdown lists only the selected source's pods; both multi-select with All, both `label_values()`-driven (VAR-01/02/03) | **PASS** | `AllFourClassesPresent=True`, `PodDropdownSuperset=True`, `KeeperChainFiltered=True`; UI-confirmed narrowing to `keeper-*` |
| 6 | Each dashboard references its datasource through a template variable, no hardcoded UID (DASH-04) | **PASS** | No `skp-prometheus` literal in either JSON; lint S3/S4 enforce; additionally proven portable on Grafana **11.1.0** with `schemaVersion` 39 unmigrated |
| 7 | Every panel renders data under the two-class rule; pod dropdown asserted as superset (VER-01) | **PASS** | `Verdict=Pass`, 30 Class A / 6 Class B, `EmptyClassAExprs` 0, `QueryMode=query_range` at the datasource-derived 240 s rate interval |

**Score: 7/7.**

## Requirement traceability

All 16 requirements delivered. RTD-02, BPD-02, VAR-04 and VER-01 are delivered **with accepted limitations** recorded in `87-FINDINGS.md` — the instrumentation genuinely does not supply CPU/uptime/working-set, a keeper reinject counter, or a `service_name` processor discriminator, and those substitutions are titled and described for what they actually measure.

## What verification would have missed, and why it did not

Criterion 7 was reported **PASS by the original live proof while three panels rendered "No data" in a browser.** The proof issued instant queries with `$__rate_interval` hardcoded to `5m`; the panels issue range queries at the datasource-derived interval. Both choices independently masked the defect.

This is recorded because it is the phase's most transferable lesson: *a proof that does not reproduce the caller's query shape proves only that the PromQL parses.* The gate was corrected (finding §12), the root cause fixed (§11), and the correction proven with a negative control — reintroducing `timeInterval: "15s"` drives the proof to **Fail with 16 of 30 Class A empty**, where it previously reported 30/30 green.

Criterion 7's PASS above is therefore issued against the **corrected** gate.

## Human verification

87-06 Task 2 was gated `human-verify`. It was **signed off by user instruction, not by a reviewer walking the panels**. The mechanical properties are evidenced in `87-06-SUMMARY.md` with per-criterion attribution; the aesthetic judgement the checkpoint was written for is not independently confirmed. Carried forward rather than papered over.

## Carried into Phase 88

The dashboards are proven to **render**; they are not proven to **discriminate**. Ten of the fourteen business panels have only ever been observed in a single state — panels 2/6/7/8/13 nothing but zero, panel 9 a flat line, the WebApi row idle. A panel that always reads the same value is indistinguishable from a broken one. Phase 88 exists to close that.

## Verdict

**PASSED** — 7/7 success criteria, 16/16 requirements, live proof green under a gate that has been shown to have teeth.
