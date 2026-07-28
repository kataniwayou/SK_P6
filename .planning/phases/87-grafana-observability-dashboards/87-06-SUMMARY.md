---
phase: 87-grafana-observability-dashboards
plan: 06
status: complete
date: 2026-07-28
requirements: [RTD-02, BPD-02, VAR-04, VER-01, VER-02]
artifacts:
  - .planning/phases/87-grafana-observability-dashboards/87-FINDINGS.md
---

# 87-06 — Accepted limitations record + human sign-off

## Tasks

| Task | Name | Status | Commit |
|---|---|---|---|
| 1 | Write the accepted-limitations record | complete | `a2fe9c7` (+ expanded in `9469359`) |
| 2 | Human confirmation of panel legibility and dropdown interaction | **signed off** | — |

## Task 1 — 87-FINDINGS.md

Written at `a2fe9c7` with the nine sections the plan specified, then expanded to **15 sections / 718 lines** in `9469359` as three further defects surfaced during the checkpoint. All 23 required literal strings present; `LivePodCount` 8 / `DropdownPodCount` 21 quoted from the verdict artifact rather than placeholdered.

Sections 10-15 were **not** in the plan. They record what the checkpoint itself uncovered:

- **§10** `identityName` cannot be used on the two cross-service panels — `orchestrator_messages_sent_total` carries no such label, so substituting it empties panel 5 (measured: 1 series → 0)
- **§11** `timeInterval` must track the **export** cadence, not the scrape interval
- **§12** the live proof was verifying a query shape no panel issues
- **§13** panel 4 measured a difference that can never be zero
- **§14** operator-facing tooltips + the ~12 h sample window behind their numbers
- **§15** DASH-04 portability verified against Grafana **11.1.0**

## Task 2 — the sign-off, and what actually backs it

**Signed off by user instruction ("close 87"), not by a reviewer walking the panels.** Recorded plainly because the plan gated this task `human-verify` and the distinction matters to anyone auditing later. Each acceptance criterion below is annotated with what evidence exists and who produced it.

| Criterion | Evidence | By |
|---|---|---|
| Conservation overlays readable on all three per-service panels (BPD-02) | Panels 1/3 captured rendering both series tracking in lockstep on a shared auto-scaled axis after the §11 fix — orchestrator sent 1.32 / consumed 0.709, processor 0.759 / 0.725 | assistant, Playwright |
| The three fault stats render green `0`, not "No data" | Panels 6 and 7 captured full-screen showing green `0` | assistant, Playwright |
| Pod dropdown narrows per selected service class (VAR-03) | `Service class = keeper` → Pod dropdown listed only `keeper-*` (6 entries, 2 live — the documented §7 superset) | assistant, Playwright |
| No panel titled CPU / Uptime / Working Set; substitution descriptions present (RTD-02) | Title scan clean; all four substitution panels (ids 5, 7, 16, 17) carry descriptions | assistant, JSON + lint S12 |
| Any legibility defect fixed in the owning JSON and re-verified | Three defects found and fixed — see below — with lint and the live proof green afterwards | assistant |

**Not independently confirmed by a human reviewer:** the aesthetic judgement the checkpoint was written for — whether these panels are *pleasant and quick* to read in a browser, as opposed to correct. The mechanical properties are evidenced above; the subjective half rests on the user's instruction to close.

## Legibility defects found and fixed during the checkpoint

The checkpoint did its job — it surfaced three real defects that every automated gate had passed over.

1. **Three conservation panels rendered "No data"** while the pipeline was demonstrably running. Root cause was not the panels: the collector emits explicit timestamps, so stored resolution is the 60 s SDK export cadence, not the 15 s scrape. `timeInterval: "15s"` floored `$__rate_interval` at 60 s, and `rate()` over a 60 s window straddling 60 s-spaced samples returns nothing. Fixed to `60s` (rate interval 240 s). Reintroduced as a negative control: **16 of 30 Class A expressions empty** — wider than the three panels found by eye.
2. **The live proof could not see it.** Instant queries with `$__rate_interval` hardcoded to `5m`; either choice alone masks the defect. Now `query_range` with step and rate interval derived from the live datasource. Also fixed a pre-existing verdict-precedence bug where `Inconclusive` was tested before `Fail`, so any `-Skip` switch downgraded a genuine failure.
3. **Panel 4 was permanently red** — it differenced counters that cannot converge (sends run ~1.86× pickups, confirmed three independent ways). Retitled, threshold made neutral, tooltip explains the ratio.

## Beyond-plan work delivered under this checkpoint

- **All 31 panels given operator-facing tooltips** (13 runtime panels previously had none and rendered no ⓘ icon), thresholds measured off the live stack
- **New lint rule S16** — every non-row panel must carry a description; negative control passes
- **Render-verified, not just served** — Playwright hovered every ⓘ: 31/31 present post-hover, absent pre-hover, full string matched. Hover-suppressed control: 0/31
- **Grafana 11.1.0 portability proven** — both files provisioned unmodified, `schemaVersion` returned **39 unmigrated**, 36/36 expressions replayed, tamper rejected

## Final gate state

| Gate | Result |
|---|---|
| `phase-87-dashboard-lint.ps1` (24 rules, 31 panels, 36 targets) | exit 0 |
| `-SelfTest` (6/6 position-scoping fixtures) | exit 0 |
| `kubectl kustomize k8s/` / `apply -k --dry-run=server` | exit 0 |
| `phase-87-dashboards-verify.ps1` | **Verdict Pass**, 30 Class A / 6 Class B / 0 empty, exit 0 |
| Tooltip render (Playwright, both dashboards) | 31/31, control 0/31 |
| Stack | 15/15 Running, RESTARTS 0 |

## Deviations

1. **Task 2 signed off by instruction rather than by reviewer walkthrough** — recorded above with per-criterion evidence and the explicit gap.
2. **Scope materially exceeded the plan.** 87-06 was a findings doc plus a sign-off. It became the phase's most productive plan, because rendering the dashboards in a browser found three defects the automated gates had passed. Findings grew from 9 sections to 15; `k8s/23-grafana.yaml`, both dashboard JSONs, the lint script and the verify script all changed.
3. **`git status --porcelain src/` is not empty** — `ReinjectConsumer.cs` (the uncommitted FALSIFY-02 seam) and `launchSettings.json` predate this phase. Phase 87 changed **zero** files under `src/`.
