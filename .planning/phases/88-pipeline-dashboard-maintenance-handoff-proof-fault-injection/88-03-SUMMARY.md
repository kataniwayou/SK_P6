---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 03
subsystem: testing
tags: [grafana, playwright, kubernetes, prometheus, powershell, fault-injection, probe, wave-0, blocking]

# Dependency graph
requires:
  - phase: 88-01
    provides: "the batch Grafana panel DOM reader and its PowerShell invocation contract (Invoke-PanelReadBatch, Get-PinnedWindowSeries, Get-PanelSamples, Get-PanelStates)"
  - phase: 88-02
    provides: "the runtime-only cluster-ops library (Invoke-TierScaleFault, Set-Phase88Seam, Clear-Phase88Seam, Assert-StackRestored) and the 60/61/62/65 failure-code vocabulary"
  - phase: 87-grafana-observability-dashboards
    provides: "the harness frame, the datasource-proxy query_range helper, the derived $__rate_interval arithmetic, and the Fail-beats-Inconclusive verdict precedence"
provides:
  - "scripts/phase-88-wave0-probe.ps1 — the seven-question live probe, one independently recorded answer per question"
  - "analyzer-reports/phase-88-wave0-probe.json — the measured answers every downstream scenario plan branches on"
  - "88-PROBE-DECISIONS.md — the locked Locked/Dropped status for all ten scenarios, each citing the probe field that decided it"
  - "the measured fact that this cluster produces NO keeper recovery traffic from a bare processor crash"
  - "the measured fact that no WebApi endpoint returns 5xx on safe input, and that neither unresolved-step route is reachable"
  - "a corrected Prometheus retention figure (225 h, not the ~12 h Phase 87 recorded)"
affects: [88-04, 88-05, 88-06, 88-07, 88-08, 88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "a probe whose verdict is about whether the QUESTIONS were answered and the stack left clean — never about whether an answer was true; a false answer is a measurement, not a defect"
    - "stack-at-rest precondition gate (all tiers fully Ready + zero seam residue) so a concurrent sweep or a left-armed seam aborts rather than silently confounding every measurement"
    - "reuse an already-live loopback tunnel instead of starting a second one the script would not own"
    - "a probe workflow that is stopped through the API before anything is deleted, with teardown on an inner finally AND the outer one"

key-files:
  created:
    - scripts/phase-88-wave0-probe.ps1
    - analyzer-reports/phase-88-wave0-probe.json
    - analyzer-reports/phase-88-screenshots/ (30 PNGs)
    - .planning/phases/88-pipeline-dashboard-maintenance-handoff-proof-fault-injection/88-PROBE-DECISIONS.md
  modified: []

key-decisions:
  - "ZERO-02 (panel 2) lever is `seam`, not `scale` — PQ-01 measured zero keeper recovery traffic from a real 90 s processor-tier crash"
  - "LocatorMode is `viewpanel` for the whole phase — PQ-02 read 20/20 pairs with both numeric parses succeeding"
  - "WEB-02 is DROPPED and panel 13 joins the accepted-unproven register — seven safe-input endpoints, zero 5xx; the redis-outage route was NOT substituted"
  - "ZERO-01 is DROPPED and panel 6 joins the register — the dangling edge is refused 422 at step-create, and the L2 step key is rewritten by any stop/start"
  - "One RateIntervalSeconds (240) is stated in every Phase-88 artifact — PQ-04 derived the same value for a 10-minute and a 2-hour window"
  - "ZERO-03 proceeds, but PQ-06 proves only that the seam LANDS; assumption A6 (the deployed image honours it) is explicitly not retired"
  - "The reader's legend row NAMES do not bind on this Grafana render — recorded as a required 88-04 parser amendment rather than silently carried into a baseline"
  - "No requirement is marked Complete: this plan decides levers, it proves no panel"

patterns-established:
  - "Probe artifact records the question it could not evaluate in UnevaluableQuestions[] rather than leaving a null to be misread as an answer"
  - "A dirty stack keeps its own exit code (65) even though the artifact Verdict is Fail — the code is a more specific rendering of the same finding"
  - "Every psql -c is a single-line static double-quoted literal so the no-interpolation guard has teeth instead of passing vacuously"

# This plan's `requirements` frontmatter names DISC-02, DISC-04, DISC-05, DISC-06 and HAND-04.
# NONE is marked complete — see Decisions. This plan decides which panels CAN be proven and how;
# it proves no panel and authors no register.
requirements-completed: []

# Metrics
duration: 95min
completed: 2026-07-28
---

# Phase 88 Plan 03: Wave-0 Probe Summary

**Seven questions that would have silently mis-shaped eight downstream scenarios were measured against the live `skp` cluster in one 25-minute run — and three of them came back "no", dropping two scenarios, converting a third from a free scale fault into a two-rollout seam scenario, and surfacing a reader defect that would have poisoned every baseline.**

## Performance

- **Duration:** ~95 min (of which the live probe run is ~25 min)
- **Completed:** 2026-07-28
- **Tasks:** 3 of 3
- **Files modified:** 33 (4 tracked paths created; 30 of them screenshots)

## Accomplishments

- **The phase's blocking step is closed with measurements instead of assumptions.** Research assumptions A1-A4 are now four recorded numbers. Three of the four came back negative, which is precisely why the probe existed: locking the scenario plans first would have discovered them mid-sweep.
- **`Verdict: Pass`, exit 0, `UnevaluableQuestions: []`.** All seven questions produced an answer; none was declared unevaluable; the stack was left byte-identical — `SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` all true with empty mismatch arrays, independently re-verified afterwards by direct `kubectl` reads (replicas 1/2/3/2, all images `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY`).
- **The seam mechanism ran live for the first time.** Everything asserted about `Set-Phase88Seam` before today was static. It armed, landed on the live Deployment spec, rolled the keeper (`keeper-99b8c574b-*` → `keeper-75dcb7cb47-*`), disarmed, and left no residue — inside the library's 180 s settle bound, which is now measured rather than reasoned.
- **Two scenarios are dropped on evidence, not on effort.** Panel 13 has no safe 5xx route across seven probed endpoints; panel 6's counter is unreachable from outside `src/` by either enumerated route. Both enter the HAND-04 register with their blocked routes and observed statuses recorded, and the costly redis-outage substitute was explicitly refused rather than quietly adopted.
- **A defect that would have voided the highest-value scenario was caught before any baseline was captured.** The panel reader parses legend *values* correctly and binds legend *names* to nothing — so panel 2's legend-name discrimination signal is currently unreadable, and the `data-testid VizLegend series` recovery path that plan 88-01 designed for exactly this matches no elements on this Grafana render. Recorded as a required 88-04 amendment with the measured evidence.
- **A propagated wrong number is corrected.** Prometheus retention measures **225 h**, not the ~12 h `87-FINDINGS.md §14` recorded and `88-RESEARCH.md` turned into a standing constraint.

## Task Commits

1. **Task 1: Probe frame + PQ-02/PQ-04/PQ-07** — `9bfe667` (feat)
2. **Task 2: Cluster questions PQ-01/PQ-03/PQ-05/PQ-06 + artifact and verdict** — `1c9d049` (feat)
3. **Task 3: Live run + `88-PROBE-DECISIONS.md`** — `2fc3af3` (docs)

## The Seven Answers

| # | Question | Measured | Decided |
|---|---|---|---|
| PQ-01 | keeper recovery traffic from a bare processor crash? | **No** — `KeeperConsumedSeriesCount: 0` over 30 min covering a real 90 s tier crash | ZERO-02 lever = `seam` (`PROCESSOR_DEFEAT_READ`, reinject left intact) + a re-baseline |
| PQ-02 | `viewPanel` locator + `innerText` parseable? | **Yes** — 20/20 pairs, `State: Ok`, stat `0`, legend Means `0.200 ops/s` | `LocatorMode: viewpanel` phase-wide |
| PQ-03 | any safe-input 5xx? | **No** — seven endpoints, five 404s, one 400, one 422 | WEB-02 **Dropped**; panel 13 → HAND-04 |
| PQ-04 | `$__rate_interval` pinned? | **Yes** — `timeInterval` 60 s read live; 240 s for both a 10-min and a 2-h window | one `RateIntervalSeconds: 240` in every artifact |
| PQ-05 | route to `orchestrator_step_unresolved`? | **Neither** — dangling edge 422 at step-create; L2 key rewritten by stop/start | ZERO-01 **Dropped**; panel 6 → HAND-04 |
| PQ-06 | does the seam land and clear? | **Yes, both** — first live arm, rollout pair recorded, zero residue | ZERO-03 proceeds |
| PQ-07 | evidentiary horizon? | oldest sample `2026-07-19T07:38:06Z`, **225 h**, not horizon-limited | corrects the ~12 h figure |

Scenario status is locked in `88-PROBE-DECISIONS.md`: **BASE-01, WEB-01, SCALE-01/02/03, ZERO-02, ZERO-03, LADDER-01 Locked; WEB-02 and ZERO-01 Dropped** — ten rows, each citing the probe field that decided it.

## Files Created

- `scripts/phase-88-wave0-probe.ps1` — the probe. Comment-based help with a lettered STEP flow and a complete exit-code table (0/1/2 plus 15/20/50/60/61/62/63/64/65). **No parameters at all**, so no workload name, path or query can be caller-derived. PRE gate extends the Phase-87 shape with a stack-at-rest check. STEP A owns a loopback-only forward and reuses an already-live one rather than starting a second. STEPs B-H answer the seven questions; STEP J writes the artifact with `-Depth 10` and only then resolves the exit code from the same in-memory object.
- `analyzer-reports/phase-88-wave0-probe.json` — 45 fields: one per answer, the seven `ProbedEndpoints` rows, the three restore claims plus their mismatch arrays, `UnevaluableQuestions[]`, the how-it-verified block (`ViewportWidth`/`Height`/`RateIntervalSeconds`), the pre-run replica and image maps, and `HumanSummary` last.
- `analyzer-reports/phase-88-screenshots/` — 30 PNGs (panels 8, 9 over ten pinned windows each; panel 2 over ten more). Evidence only, never a verdict.
- `88-PROBE-DECISIONS.md` — eight numbered records (**Question** / **Measured answer** / **Decision** / **Consequence for the scenario plans**), the ten-row scenario status table, the fourteen panel titles verbatim, and a requirement-traceability table.

## Decisions Made

- **Two records go beyond the branch table, and both say so.** Record 3 (the legend-name binding defect) exists because the table branches on `TimeseriesLegendParsed`, which is `true` — the defect is invisible to the flag that was supposed to catch it. Record 7 explicitly refuses to retire assumption A6 on the strength of a green PQ-06. Where the table had a field, it was applied literally; where it had none, the addition is flagged and grounded in a measured value.
- **The redis-outage route to a 5xx was not substituted.** The branch table forbids it and the reasoning holds independently: the Phase-86 hard readiness latch means the WebApi does not self-heal from a dependency outage, so the route costs a pod restart and would perturb every later baseline.
- **`65` overrides the resolved exit code on a dirty stack.** `Resolve-AnalyzerExitCode` runs on the same object the artifact was written from and would return 1 for `Verdict: Fail`; 65 is a strictly more specific rendering of the same finding, and the artifact's Verdict stays authoritative. Both are reachable only through the dot-sourced library — there is no inline verdict switch.
- **No requirement is marked Complete.** DISC-02 asserts every panel has been *seen* changing (none has), DISC-04 that panels 2 and 8 have been *seen* non-zero (neither has), DISC-05 that the stack is asserted restored after *every scenario* (no scenario exists), DISC-06 that every scenario re-baselines (likewise), HAND-04 that a register *exists* (88-09 authors it). This continues 88-01's deviation 3 and 88-02's decision.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] The plan's own artifact fields could not distinguish a retention measurement from the probe's own horizon**

- **Found during:** Task 1 (read-only smoke run)
- **Issue:** PQ-07 was first implemented over a 48 h horizon, matching the ~12 h retention the research recorded. It returned `RetentionHoursObserved: 48` — exactly the horizon. That is a measurement of the *script*, not of Prometheus, and it would have been written into the artifact as though it were retention, quietly confirming a figure that later turned out to be wrong by a factor of nineteen.
- **Fix:** Widened the horizon to 336 h at a 3600 s step and added `RetentionHorizonHours` + `RetentionHorizonLimited` (both additive). The final run returned 225 h with `RetentionHorizonLimited: false`, so the number is now a measurement rather than an artefact of the probe's own window.
- **Files modified:** `scripts/phase-88-wave0-probe.ps1`
- **Committed in:** `9bfe667`

**2. [Rule 2 - Missing critical functionality] Legend values parsed while legend names bound to nothing, and no field would have shown it**

- **Found during:** Task 1 (read-only smoke run), confirmed in the Task 3 run and by a supplementary read
- **Issue:** `TimeseriesLegendParsed` was `true` — a numeric Mean parsed — while every parsed row carried `name: ""`. On this Grafana render `innerText` puts the series name on its own line and the values on the next, so the reader's tab/two-space row split discards the name line and yields rows whose first cell is empty; the `data-testid VizLegend series <name>` attribute route that 88-01 built as the verbatim-name recovery path matches **no elements at all**. Panel 2's discrimination signal is a legend-name change, so the phase would have captured a baseline on a reader that cannot see the very signal ZERO-02 exists to assert — and the plan's own flag would have read green throughout.
- **Fix:** Added `Test-LegendNamesBound` and the additive artifact fields `TimeseriesLegendNamesBound` and `Panel9LegendNames`, reported separately from `TimeseriesLegendParsed` precisely so the two cannot be confused. The reader itself was **not** changed — it belongs to plan 88-01 and its amendment belongs to 88-04, which is where the branch table sends it. Record 3 of `88-PROBE-DECISIONS.md` carries the measured evidence and the four concrete amendment points.
- **Files modified:** `scripts/phase-88-wave0-probe.ps1`
- **Committed in:** `9bfe667` (detection), `2fc3af3` (the recorded amendment)

**3. [Rule 3 - Blocking] Loopback 3000 was already bound by a forward this script does not own**

- **Found during:** Task 1 (read-only smoke run)
- **Issue:** Grafana is not one of the eight shared tunnels, but a live forward was already holding 127.0.0.1:3000 (left by 88-01's incidental live test). The plan's shape starts its own forward unconditionally; that second forward would bind-fail and exit immediately, the health poll would pass anyway *through the stray*, and teardown would then either be a silent no-op or, if the PID had been recycled onto something else, act on a process the script never started.
- **Fix:** `Test-GrafanaAlreadyUp` runs before `Start-GrafanaForward`. When the tunnel is already carrying traffic the probe reuses it, records `$gfForwardOwned = $false`, and tears nothing down. The recycled-PID guard is unchanged and the shared forward PID registry is still never touched.
- **Files modified:** `scripts/phase-88-wave0-probe.ps1`
- **Committed in:** `9bfe667`

**4. [Rule 3 - Blocking] The idempotent seeder is a multi-minute no-op on a stack that already carries the workflow**

- **Found during:** Task 2
- **Issue:** The plan's STEP G shape is "seeder, then resolve the wf-id, then activate". The seeder is idempotent and GET-matches its sentinel name, so on this stack — where the workflow has existed since Phase 80 — it is a several-minute build-and-run that changes nothing. It is also `dotnet test` under Microsoft.Testing.Platform on Windows, a combination this project has recorded as prone to hanging.
- **Fix:** Inverted the order: resolve the workflow id first; run the seeder **only** when the lookup comes back empty, then re-resolve; exit 50 if it is still absent. The seeder call and its exit-50 gate are both still present, so a genuinely unseeded stack behaves exactly as the plan specifies.
- **Files modified:** `scripts/phase-88-wave0-probe.ps1`
- **Committed in:** `1c9d049`

**5. [Rule 1 - Bug] Two variables differing only by capitalisation are the same variable in PowerShell**

- **Found during:** Task 2 (pre-commit review)
- **Issue:** The artifact's recorded probe-workflow id was written as `$ProbeWorkflowId` while the teardown flag the outer `finally` reads is `$probeWorkflowId`. PowerShell variable names are case-**insensitive**, so `$ProbeWorkflowId = $probeWorkflowId` followed by `$probeWorkflowId = ''` assigned and then immediately cleared the *same* variable — the artifact would have recorded an empty `ProbeWorkflowId` on every run, making `ProbeWorkflowCleanedUp` unfalsifiable (the plan's own acceptance criterion accepts a true cleanup flag *or* an empty id).
- **Fix:** Renamed the recorded value to `$ProbeWorkflowIdRecorded`, with a comment stating why capitalisation alone is not a distinction. The live run recorded `ProbeWorkflowId: c3b4bad3-bea9-4cc5-8991-0c5cc15c5e79` with `ProbeWorkflowCleanedUp: true`, so the claim now has content.
- **Files modified:** `scripts/phase-88-wave0-probe.ps1`
- **Committed in:** `1c9d049`

**6. [Rule 2 - Missing critical functionality] The plan's `psql -c` guard would have passed vacuously**

- **Found during:** Task 2
- **Issue:** The Phase-87 traffic drive splits `psql` and `-c "…"` across a backtick continuation. The plan's per-line assertion only inspects lines containing both `psql` and `-c `, so with the continuation neither line is ever checked and the T-88-06 injection control passes without evaluating anything.
- **Fix:** Both `psql` invocations are written on a single line so `psql`, `-c` and the static double-quoted literal are all on the checked line. The guard now genuinely evaluates the control it exists to enforce.
- **Files modified:** `scripts/phase-88-wave0-probe.ps1`
- **Committed in:** `1c9d049`

---

**Total deviations:** 6 auto-fixed (2 bugs, 2 missing critical functionality, 2 blocking)
**Impact on plan:** No scope change, no new dependency, no interface removed. Every artifact field the plan specified is present; five fields were added (`TimeseriesLegendNamesBound`, `Panel9LegendNames`, `UnresolvedRouteApiDanglingEdgeStage`, `RetentionHorizonHours`, `RetentionHorizonLimited`), all additive.

## Issues Encountered

- **The panel-2 raw text is not in the artifact.** `Panel2LegendNamesAfter` came back empty, and without the raw text an empty array is ambiguous between "the panel rendered nothing" and "the panel rendered rows whose names did not bind". A supplementary read-only two-window read of panel 2 through the same library resolved it after the run: the panel renders `consumed` / `sent` rows with `0 ops/s` Means, the guard **is** firing, and the names fail to bind for the Deviation-2 reason. The evidence is quoted verbatim in Record 3. The probe was not re-run for this — PQ-01's answer rests on the counter query (0 series), not on the panel read, so the gap is evidentiary rather than decisional. **88-04 should record `Panel2RawText` alongside the legend names.**
- **`innerText` normalises away the trailing space.** The research proposed detecting the guard by `consumed ` (trailing space) becoming `consumed keeper`. The rendered text is the bare word `consumed` — the trailing space does not survive `innerText` at all. The suffix change remains detectable once names bind; the trailing-space formulation does not, and no later plan should look for it.
- **A pre-existing Grafana port-forward (PID 46176) was found holding 127.0.0.1:3000 and was deliberately left running.** It predates this plan, is not one of the eight shared tunnels, and later plans need Grafana reachable. This plan started no forward of its own and therefore left none behind.
- **A live `dotnet test` seeder run never happened** — the fan-out workflow was already present, so the inverted order (Deviation 4) skipped it. The seeder path in STEP G is therefore *authored but unexercised* on this stack; a future run against a reset-clean database is its first real test.

## Known Stubs

None. Every step the plan specifies is implemented and was executed live. The header-walk fallback in STEP D is a real second path that this run did not need (`viewpanel` succeeded), and the seeder call in STEP G is a real path that this run did not need (the workflow already existed) — both are conditional branches, not deferrals.

## Threat Flags

None. This plan adds no product code, no network endpoint, no auth path and no schema change, and installs no package (T-88-SC holds). Every threat in the plan's register is mitigated and was exercised:

- **T-88-13** — the endpoint list is a static in-script `[ordered]` table; the script takes **no parameters at all**, so nothing caller-supplied can reach a command line. Six GETs plus one inert `POST … []` were issued, exactly as tabled.
- **T-88-06** — both `psql -c` calls are single-line static double-quoted literals with no `$`; the guard now has teeth (Deviation 6).
- **T-88-11** — the probe workflow was stopped through the API **before** any deletion; `ProbeWorkflowCleanedUp: true`; row counts re-verified back at 1/10/10/10 and a Redis scan for the probe prefix returns nothing.
- **T-88-03** — `$seamArmed` declared before the `try`; `Clear-Phase88Seam` on both the happy path and the outer `finally`; `SeamVarsClean: true` with an empty `SeamVarsFound`.
- **T-88-02** — the scale fault read `spec.replicas` live (2), restored to 2, and claimed `ReplicasRestored: true`; independently re-read afterwards as 1/2/3/2.
- **T-88-16** — no dependency outage was driven; `scale statefulset` is asserted absent from the source.
- **T-88-04 / T-88-05** — `--address 127.0.0.1` present, `0.0.0.0` absent, the shared forward PID registry absent; the probe's own PID would have lived in one in-memory variable behind a recycled-PID guard (it started no forward this run).
- **T-88-07** — one distinct exit code per abort; the verdict comes only from the dot-sourced fail-closed `Resolve-AnalyzerExitCode`, with no inline verdict switch.
- **T-88-10** — `apply -k` absent; `ImagesUnchanged: true`, all four tiers still on `:tags-const-1544`.

## User Setup Required

None. `kubectl`, the `docker-desktop` context, `node`, the playwright skill and the eight shared forwards were all pre-existing.

## Next Phase Readiness

- **Ready for 88-04 (BASE-01 baseline capture) — with one mandatory amendment first.** The parser must pair a legend name line with the values line that follows it before any band is captured; until it does, `Get-PanelSamples -SeriesName` matches nothing on this stack and per-series reads must be taken by row index. Record 3 of `88-PROBE-DECISIONS.md` carries the four amendment points and the measured evidence for each.
- **Ready for 88-05 (WEB-01).** Panel 12's two status drivers are confirmed and inert: `GET /api/v1/__phase88_probe_unmatched` → 404 and `POST /api/v1/orchestration/start` with `[]` → 400. WEB-02 is not authored.
- **Ready for 88-06 (SCALE-01/02/03).** The scale sequencer ran live end to end with `ReplicasRestored: true`; `ReplicasBefore` records the orchestrator at 3, so the stale-map hazard has a measured counter-example in the artifact.
- **Ready for 88-07 (ZERO-02, ZERO-03) — at a higher cost than planned.** ZERO-02 needs `PROCESSOR_DEFEAT_READ` plus a re-baseline it was not expected to need. ZERO-03 must arm **both** seams, because PQ-01 established the cluster produces no recovery event without a trigger. ZERO-01 is not authored.
- **Ready for 88-08 (LADDER-01).** `RateIntervalSeconds: 240` is fixed for every rung, so no rung re-derives it.
- **Input ready for 88-09.** The accepted-unproven register has three entries waiting: panel 7 (user-locked), panel 13 (PQ-03), panel 6 (PQ-05), the latter two with their enumerated blocked routes and observed statuses.
- **Concern to carry forward.** PQ-06 proves the seam *lands*, not that the deployed `keeper:tags-const-1544` image *honours* it. Research assumption A6 is unretired, and `src/Keeper/Recovery/ReinjectConsumer.cs` remains modified in the working tree. If ZERO-03 sees a flat panel 8, the image is the suspect before the mechanism is.

## Self-Check: PASSED

- `scripts/phase-88-wave0-probe.ps1` — FOUND (parses via `[ScriptBlock]::Create`; all twelve exit-code rows present; forbidden-token gate clean)
- `analyzer-reports/phase-88-wave0-probe.json` — FOUND (parses; `Verdict: Pass`; three restore claims true; `UnevaluableQuestions` empty)
- `analyzer-reports/phase-88-screenshots/` — FOUND (30 PNGs)
- `.planning/phases/88-.../88-PROBE-DECISIONS.md` — FOUND (7 PQ records + 10 scenario rows, each citing a probe field)
- Commit `9bfe667` — FOUND
- Commit `1c9d049` — FOUND
- Commit `2fc3af3` — FOUND
- Live stack re-verified after the run: replicas 1/2/3/2, images all `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY`, DB rows back at 1/10/10/10
- `pwsh -File scripts/phase-87-dashboard-lint.ps1` — exit 0 (24 rules, 31 panels, 36 targets; no dashboard JSON was touched)

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-28*
