---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 06
subsystem: testing
tags: [grafana, prometheus, kubernetes, powershell, fault-injection, tier-outage, nodata, cross-talk, disc-02, disc-03, disc-06, wave-5]

# Dependency graph
requires:
  - phase: 88-01
    provides: "the batch Grafana panel DOM reader — Invoke-PanelReadBatch, Get-PinnedWindowSeries, Get-PanelSamples/States/SeriesCount, Test-PanelMoved with its first-class NoData direction"
  - phase: 88-02
    provides: "Invoke-TierScaleFault — the live-read replica capture, terminate-wait, dwell, restore-to-the-read-count and the explicit ReplicasRestored claim; Wait-TierSettled; Assert-StackRestored"
  - phase: 88-03
    provides: "the locked levers — LocatorMode viewpanel, RateIntervalSeconds 240, SCALE-01/02/03 all Locked with `scale`, and the corrected ~9.4-day retention figure"
  - phase: 88-04
    provides: "analyzer-reports/phase-88-BASE-01.json, and the measured fact that panel 4 cannot be banded at 60 s so it is read in its own 120 s batch"
  - phase: 88-05
    provides: "-Mode Scenario — the arm -> rollout -> settle >=150 s -> re-baseline -> trigger -> after -> restore -> assert engine, with the scale branch authored but never exercised"
provides:
  - "analyzer-reports/phase-88-SCALE-01.json — panels 3, 4 and 5 under a processor-tier outage"
  - "analyzer-reports/phase-88-SCALE-02.json — panel 1 under an orchestrator-tier outage, with the Phase-83 HA replica count preserved and verified twice"
  - "analyzer-reports/phase-88-SCALE-03.json — panel 9's NoData proven to BE the discrimination signal under a dead keeper"
  - "the FIRST exercise of the DISC-06 re-baseline path — three rollouts, three distinct and disjoint pod-identity pairs, three post-settle authoritative bands"
  - "panel-level NoData scoring: a panel that empties has no series, so a series-only scorer reports 'nothing moved' for a panel that went completely blank"
  - "PredictionHeld / ObservedDirection / PredictionsCorrectedByMeasurement[] — a wrong prediction is a loud finding that degrades the verdict, never something a Pass absorbs"
  - "the measured fact that rate(counter[240s]) for a dead pod survives ~180 s past its last real sample, which is what sizes any nodata-predicting dwell"
  - "the measured fact that panel 5 EMPTIES WITHOUT EVER SPIKING on this stack, and panel 4 goes blank rather than sloping up"
  - "TrafficResumedAfterRestore — measured through the datasource proxy, so an idle stack and a recovered one cannot be confused"
  - "the locked screenshot policy, recorded in .gitignore: 19 of 406 captures committed"
affects: [88-07, 88-08, 88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "a panel STATE is a first-class discrimination signal, evaluated independently of any band, because an emptied panel renders no series for a series-based scorer to find"
    - "the direction that ACTUALLY carried the movement is recorded per series, so a compound token can never echo itself back as the observed direction"
    - "a prediction corrected by measurement degrades the verdict to Inconclusive and is recorded verbatim — the panel still discriminated, but the model of it was wrong"
    - "a claim about a panel is only made after that panel is OBSERVED: observePanels ride along in the same batch, recorded and never scored, never a control"
    - "a dwell that predicts NoData is sized from the measured lookback survival, not from the nominal metric_expiration"
    - "a scalar ReplicasBefore/ReplicasAfter for the tier a row actually scaled, re-read live after the restore, beside the four-tier maps"

key-files:
  created:
    - analyzer-reports/phase-88-SCALE-01.json
    - analyzer-reports/phase-88-SCALE-01-run1-discarded.json
    - analyzer-reports/phase-88-SCALE-02.json
    - analyzer-reports/phase-88-SCALE-03.json
    - analyzer-reports/phase-88-screenshots/SCALE-01/ (150 PNGs on disk, 13 committed)
    - analyzer-reports/phase-88-screenshots/SCALE-02/ (88 PNGs on disk, 1 committed)
    - analyzer-reports/phase-88-screenshots/SCALE-03/ (168 PNGs on disk, 5 committed)
  modified:
    - scripts/phase-88-panel-discriminate.ps1
    - .gitignore

key-decisions:
  - "SCALE-03's dwell is 480 s, not the tabled 240 s, because a read-only query against the wave-0 probe's OWN recorded keeper rollout measured that rate(counter[240s]) for a dead pod survives ~180 s past its last real sample — at 240 s the panel would still have been rendering and the plan's own two-consecutive-NoData criterion would have been unreachable"
  - "Panel 4's predicted slope-up did NOT happen: sum(increase(proc_consumed[120s])) over a window with no real samples is an EMPTY vector and A - empty is empty, so the stat an operator is told to read the trend of goes BLANK under exactly the fault it exists to reveal. Recorded as a prediction corrected by measurement; SCALE-01's verdict is Inconclusive because of it"
  - "Panel 5 EMPTIED WITHOUT EVER SPIKING — 0.63, 0.588, 0.4, 0.234, 0.183 is a decay, not a spike. The research's model (the orchestrator keeps sending while pickups stop) does not survive a request/response pipeline, where the orchestrator's own send rate falls with it"
  - "SCALE-01 run 1 was discarded for three DEFECTS in this plan's own new scoring code, not for a better number, and both artifacts are committed (the 88-04 precedent)"
  - "'down' admits NoData, because the plan defines it that way in its own text for both SCALE-02 and SCALE-01's panel 3"
  - "Panels 2 and 8 were OBSERVED at exactly 0 across all eight SCALE-03 windows rather than asserted to be — a claim about a panel is made only after that panel is read"
  - "No requirement is marked Complete: DISC-02 is 9/14, DISC-03 has run on 4 of 8 scenarios, DISC-05 and DISC-06 are statements about EVERY scenario and three remain"

patterns-established:
  - "Measure the mechanism from history before coding a dwell: Prometheus retains ~9.4 days, so a previous run's own rollout is a free, read-only experiment"
  - "An artifact must never state a behaviour its own recorded values contradict — the spike is an explicit Direction=up test over the early segment, never inferred from 'some series moved'"
  - "An alt-direction match REPLACES its series entry rather than appending, because the diagnostic aggregate sums every SeriesResults[] row and a duplicate manufactures a proxy disagreement out of double counting"

# This plan's `requirements` frontmatter names DISC-02, DISC-03, DISC-05 and DISC-06. NONE is
# marked complete — see Decisions. Five more panels are proven (nine of fourteen now), the
# cross-talk rule has run under three more scenarios, and the DISC-06 re-baseline path is
# exercised for the first time — but all four statements are about EVERY panel or EVERY scenario,
# and ZERO-02, ZERO-03 and LADDER-01 have not run.
requirements-completed: []

# Metrics
duration: 125min
completed: 2026-07-29
---

# Phase 88 Plan 06: Tier-Outage Scenarios Summary

**Five panels that had only ever been seen in one state are now proven to discriminate from the rendered Grafana panel — and two of them discriminate by going BLANK rather than by moving, which is the opposite of what the plan predicted for one of them and is the single most useful thing this plan measured.**

## Performance

- **Duration:** ~125 min (three live scenarios plus one discarded run, ~35 min each)
- **Completed:** 2026-07-29
- **Tasks:** 3 of 3, plus a prerequisite fix the task list did not contain
- **Files modified:** 2 scripts/config, 4 artifacts, 406 screenshots on disk (19 committed)

## Accomplishments

- **Three scenarios, three artifacts, zero Fails.** SCALE-02 and SCALE-03 are `Pass` (exit 0); SCALE-01 is `Inconclusive` (exit 2) *because* panel 4's prediction did not hold — the honest outcome, not a shortfall.
- **5/5 asserted panels moved**, each on at least two consecutive sub-windows, each read from the rendered panel: panel 3 (4 consecutive), panel 4 (2), panel 5 (2), panel 1 (3, on **both** legend Means), panel 9 (3). Combined with WEB-01's four, **nine of the fourteen business panels are now proven to discriminate.**
- **11/11 cross-talk controls held, every one at `MaxExcursion` exactly 0.0000.** Panels 9/10/12/14 under a processor outage, 9/10/12 under an orchestrator outage, 1/3/10/12 under a keeper outage. The keeper control is the substantive one: the architecture says the keeper serves the recovery path only, and the happy-path conservation counters now say so numerically.
- **The DISC-06 re-baseline path ran for the first time**, three times. Each scenario recorded a distinct, non-overlapping pod-identity pair and scored against a band re-captured after the tier settled — not against the hours-old DISC-01 band.
- **The Phase-83 orchestrator HA survived**, verified twice: `ReplicasBefore` 3 → `ReplicasAfter` 3 re-read live from the Deployment after the restore, plus an independent `kubectl get deploy orchestrator -o jsonpath={.spec.replicas}` reading 3. This is the exact scenario the stale `phase-80-harness.ps1:148-150` map (`orchestrator = 1`) would have damaged silently, because a single orchestrator still works.
- **The stack was left exactly as found.** `Assert-StackRestored` re-run independently afterwards: `Ok=True`, 1/2/3/2 on `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY`, zero replica or image mismatches, zero background jobs. The pre-existing Grafana forward was reused and left running; this plan started none.
- **`pwsh -File scripts/phase-87-dashboard-lint.ps1` still exits 0** — no dashboard JSON was touched.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 0 | Scale-scenario prerequisites (not in the task list) | `fbc0ece` | fix |
| 1 | SCALE-01 — processor-tier outage, panels 3/4/5 | `bb064a4` | feat |
| 2 | SCALE-02 — orchestrator-tier outage, panel 1 | `a502a2f` | feat |
| 3 | SCALE-03 — keeper-tier outage, panel 9 | `4ecf2e3` | feat |

## What The Three Scenarios Measured

| Scenario | Tier | Dwell | Panel | Predicted | **Observed** | Consecutive | Verdict |
|---|---|---|---|---|---|---|---|
| SCALE-01 | processor-sample 2→0→2 | 420 s | 3 | down | **down**, then empty | 4 | Inconclusive |
| | | | 4 | slope-up | **nodata** — prediction corrected | 2 | |
| | | | 5 | up-then-nodata | **nodata half only** — empty without spiking | 2 | |
| SCALE-02 | orchestrator 3→0→3 | 240 s | 1 | down | **down**, both legend Means | 3 | Pass |
| SCALE-03 | keeper 2→0→2 | 480 s | 9 | nodata | **nodata** | 3 | Pass |

Verbatim readings, against bands re-captured after each tier settled:

```
panel 3  consumed …_1.0.0   0.737 0.632 0.447 0.169 0     band 0.6751..0.8251  then NoData NoData
         sent …_1.0.0       0.705 0.604 0.428 0.162 0     band 0.6448..0.7880
panel 4  gap                58                            band 62.7601..82.8399  then NoData NoData   (120 s sub-windows)
panel 5  c3242cd2-…         0.630 0.588 0.400 0.234 0.183 band 0.5080..0.6586  then NoData NoData
panel 1  consumed orch      0.734 0.733 0.718 0.591 0.379 0.139   band 0.6322..0.7764
         sent orch          1.370 1.370 1.350 1.110 0.700 0.257   band 1.1672..1.4548
panel 9  keeper-…-fj2cw     0.197 0.203 0.168 0.129 0.0644        band 0.1800..0.2200  then NoData ×3
         keeper-…-hndxz     0.201 0.185 0.142 0.0905              band 0.1784..0.2180
```

## Findings Worth Carrying Forward

**1. `rate(counter[240s])` for a dead pod survives ~180 s past its last real sample — this is what sizes every nodata dwell.** Measured read-only from the wave-0 probe's own keeper rollout (`keeper-99b8c574b-29mzq`, last real sample `16:47:30Z`, last rate point `16:50:00Z`, decaying `0.199 → 0.152 → 0.131 → 0.104` before disappearing). The 240 s lookback keeps producing a *shrinking* value until fewer than two samples remain inside it — so a panel does not go blank when its tier dies, it **fades first**. Every panel-9 reading above shows the same shape. **This is why SCALE-03's dwell is 480 s and not the plan's 240 s**, and any later plan predicting `nodata` must size its dwell the same way.

**2. Panel 4 goes BLANK under the fault it exists to reveal.** Its expression is `sum(increase(orch_sent[$__range])) - sum(increase(proc_consumed[$__range]))`. Once the processor series has no real samples inside the range, `sum(increase(...))` is an **empty vector**, and `A - empty` is empty — so the stat whose own title says "read the trend" renders "No data" rather than sloping up. The panel discriminated (2 consecutive NoData sub-windows against a `62.76..82.84` band) but not remotely in the predicted way. **HAND-02 material of the first order**, and a direct input to the panel-4 runbook row.

**3. Panel 5 empties without ever spiking, and the reason is structural.** The research model — the orchestrator keeps sending while pickups stop, so the gap spikes to the full send rate — assumes an independent producer. This pipeline is request/response: with the processors gone the orchestrator receives no completions, so its own send rate falls too and the gap **decays** (`0.63 → 0.183`) before the panel empties. The plan anticipated this exact outcome and instructed that it be recorded verbatim rather than treated as a failure; it is, and it goes to the HAND-02 list.

**4. Panel 3's fall is gradual, not a cliff, and an operator watching for a cliff will miss it.** `0.737 → 0.632 → 0.447 → 0.169 → 0` over five minutes of a *total* processor outage, because the 240 s lookback smears the fault across four windows. The first sample is still **inside** the healthy band. The runbook row must say that the signal is the slope over several minutes, not the current value.

**5. Panel 1 never reached NoData inside a 240 s orchestrator outage** — both Means were still rendering, at `0.139` and `0.257`, when the tier came back. The plan allowed either the value fall or NoData; the measurement says which one an operator will actually see at that duration, and it is the fall.

**6. The diagnostic proxy disagrees with panel 4, and the disagreement is the panel's own emptiness.** Rendered `58` (one window rendered, two blank) versus proxy `80.89` (three windows computed). The rendered panel remains the verdict; the disagreement is recorded as a finding and is itself evidence for finding 2. Every other comparable panel agreed: panel 3 `0.765` vs `0.777`, panel 5 `0.420` vs `0.407`, panel 1 `1.598` vs `1.575`, panel 9 `0.286` vs `0.307`.

## Decisions Made

- **SCALE-03's dwell was corrected from 240 s to 480 s BEFORE the run, on a measurement rather than a hunch.** The mechanism was queried read-only out of Prometheus (retention is ~9.4 days, so a previous run's rollout is a free experiment). Running the tabled 240 s would have produced a panel that was still rendering, an unreachable acceptance criterion, and a wasted 35 minutes.
- **A prediction corrected by measurement degrades the verdict.** Panel 4 moved, so DISC-02's question is answered — but calling SCALE-01 a `Pass` would have quietly absorbed a wrong model. `PredictionHeld`, `ObservedDirection` and `PredictionsCorrectedByMeasurement[]` are artifact fields, and the verdict is `Inconclusive`.
- **A compound token satisfied by one half is NOT a corrected prediction.** The plan says panel 5 "counts as moved if EITHER segment shows the predicted behaviour; record which" — so `PredictionHeld` stays true and `ObservedDirection` records `nodata`, with `SegmentBehaviourObserved` stating "EMPTY WITHOUT SPIKING" in full.
- **Panels 2 and 8 were observed, not assumed.** The plan requires SCALE-03 to *state* that a keeper outage cannot open a keeper gap. That statement is now backed by eight windows of exactly `0` on both panels, recorded in `ObservedPanels[]` and quoted in `HumanSummary`, rather than by reasoning alone.
- **The screenshot policy is applied and recorded in `.gitignore`.** 19 of 406 captures committed (792 KB): every panel-4 capture from SCALE-01 (the panel that did not cleanly pass), the three emptied panel-9 captures from SCALE-03 plus the rendered one before them, and one representative capture per scenario. The ~23 MB already committed by 88-03/88-04/88-05 was left untouched, as locked.
- **No requirement is marked Complete.** DISC-02 asks for all fourteen panels (nine are proven); DISC-03 for every scenario (four have run); DISC-05 and DISC-06 are likewise statements about *every* scenario, and ZERO-02, ZERO-03 and LADDER-01 remain. This continues the discipline every plan from 88-01 has held.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical functionality] A panel that empties has no series, so the scorer would have reported "nothing moved" for a panel that went completely blank**

- **Found during:** Task 1 (pre-run code review against the plan's own acceptance criteria)
- **Issue:** `Test-PanelMoved -Direction nodata` was only reachable from inside the per-series loop, which iterates `$rdAfter.Entries`. A panel rendering "No data" produces **zero** legend series, so the loop body never executes, `$anySeriesMoved` stays false, and the panel lands in `UnevaluablePanels[]`. On **panel 9** — deliberately unguarded precisely so a dead keeper is visible — that is the phase's sixth pitfall in its most damaging possible form, and SCALE-03's acceptance criterion (`Moved` true with two consecutive `NoData`) would have been unsatisfiable by construction.
- **Fix:** The NoData run is evaluated against the panel's own `States[]`, independently of any band, for **every** panel (so `PanelStateNoDataRun` of 0 always means "measured, no run" and never "not checked"). When it carries the movement, a synthetic `(panel state)` entry is added to `SeriesResults[]` with an explanatory note, so the selection stays auditable.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `fbc0ece`

**2. [Rule 2 - Missing critical functionality] `down` did not admit NoData, though the plan defines it that way in its own text**

- **Found during:** Task 1
- **Issue:** The plan states for SCALE-02 that panel 1 may fall below its band "**or** that the panel reaches `NoData` — either is the predicted `down` direction", and for SCALE-01 that panel 3's prediction is "down (toward 0 / NoData)". The engine mapped `down` to `@('down')` only. A tier-outage panel that empties before it falls far enough would have scored as a discrimination failure.
- **Fix:** `'down'` maps to `@('down','nodata')`, with the plan's wording quoted at the mapping. Panel 3 exercised both halves in one window: four consecutive samples below the band, then two consecutive NoData.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `fbc0ece`

**3. [Rule 2 - Missing critical functionality] `ReplicasBefore`/`ReplicasAfter` were four-tier maps, so no scenario could state "this tier went down at N and came back at N"**

- **Found during:** Task 1
- **Issue:** The engine wrote the per-tier maps into those fields. Every scale scenario's acceptance criteria — and the T-88-02 threat mitigation — assert the **scalar** for the tier that was scaled. A map compared against `2` is simply unequal, and more importantly the artifact never stated the one number the control is about.
- **Fix:** `ReplicasBefore`/`ReplicasAfter` carry the scaled tier's scalar (read live before the mutation by the sequencer, **re-read live** after the restore by the driver itself, independently of the sequencer's claim). The maps move to `ReplicasBeforeByTier`/`ReplicasAfterByTier`, and `ReplicasFieldNote` states which shape the artifact carries so the 88-08 roll-up cannot confuse them. `ScaledTier` names the tier.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `fbc0ece`

**4. [Rule 2 - Missing critical functionality] Four artifact obligations the plan states had no implementation at all**

- **Found during:** Task 1
- **Issue:** `RangeCumulativeLevelAfter` (panel 4's wide-window level, recorded and never scored), panel 5's early/late after-segments, the SCALE-02 traffic-resumption check, and SCALE-03's required statement about panels 2 and 8 were all specified by the plan and absent from the engine — `-Mode Scenario` had only ever run the `http` lever.
- **Fix:** A `STEP S6b` wide panel-4 capture; segment cutting from the **same** sub-window series (no extra capture, so the two segments are comparable by construction); a post-restore `sum(rate(orchestrator_messages_sent_total[240s]))` read through the same datasource proxy the panels use; and `observePanels`/`ObservedPanels[]` plus a per-row `humanSummarySuffix` so a required statement is backed by a reading.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `fbc0ece`

**5. [Rule 1 - Bug, MEASURED CORRECTION] SCALE-03's tabled 240 s dwell cannot produce the NoData its own acceptance criterion requires**

- **Found during:** Task 1 (before any fault was driven)
- **Issue:** The plan tables `dwell 240 s` for SCALE-03 and requires "at least two consecutive after sub-windows must report `PanelState` of `NoData`". A read-only Prometheus query against the wave-0 probe's own keeper rollout measured that `rate(counter[240s])` for a dead pod keeps producing a decaying value for roughly 180 s past its last real sample. At a 240 s dwell the panel would have been *rendering* for most of the outage and blank for at most the final partial window.
- **Fix:** Dwell 480 s, with the measurement and its reasoning recorded at the table row. The live run produced exactly the predicted shape: five rendered sub-windows fading `0.197 → 0.0644`, then three consecutive `NoData`.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `fbc0ece`

**6. [Rule 1 - Bug] SCALE-01 run 1's artifact stated a spike its own recorded values contradict**

- **Found during:** Task 1 verification of run 1
- **Issue:** `SegmentBehaviourObserved` inferred "the gap spiked" from "some series moved with ≥2 consecutive samples outside" — but the series in question had been scored MOVED by the **NoData half of its own compound token**. Run 1 therefore claimed "BOTH: the gap spiked … AND the panel emptied" while recording early-segment values of `0.602, 0.551, 0.371, 0.245` against a `0.5153..0.6569` band. That is a false statement in an artifact whose entire purpose is to be trusted over a screenshot.
- **Fix:** The spike is an **explicit `Direction=up` test over the EARLY segment's values only**, against the same scoring band, with `SpikeObserved`/`SpikeConsecutive`/`SpikeSeries`/`SpikeTest` recorded. Run 2 states "EMPTY WITHOUT SPIKING", which is what the numbers say.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `bb064a4`

**7. [Rule 1 - Bug] An alt-direction match appended a duplicate series entry, and the diagnostic aggregate double-counted it**

- **Found during:** Task 1 verification of run 1
- **Issue:** When the predicted direction produced nothing and an alternative direction did, the alternative was **appended** to `SeriesResults[]` rather than replacing the original. STEP S10 sums every row's mean to build the rendered side of the proxy cross-check, so run 1 reported panel 4's rendered sum as `132.2` for a panel whose single value was `66.1` — a fabricated 4× proxy disagreement produced entirely by double counting. The T-88-12 control would have been reporting on arithmetic rather than on the dashboard.
- **Fix:** The alternative entry replaces the entry for the same `SeriesIndex` in place. Run 2's panel-4 rendered sum reads `58`, matching its single rendered window.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `bb064a4`

**8. [Rule 1 - Bug] `PanelStateNoDataRun` was unmeasured for panels whose prediction did not admit NoData**

- **Found during:** Task 1 verification of run 1
- **Issue:** Panel 4's run 1 entry read `PanelStateMoveApplied: false, PanelStateNoDataRun: 0` while the panel had **two consecutive NoData sub-windows** and had in fact been scored on them. A zero that can mean either "no run" or "nobody looked" is exactly the ambiguity this phase exists to remove.
- **Fix:** The NoData run is computed for every panel regardless of prediction; `PanelStateMoveApplied` says separately whether that run is what carried the movement, and `PanelStateNoDataNote` states the distinction in the artifact.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `bb064a4`

**9. [Rule 1 - Bug] The diagnostic proxy substituted `$__range` with the wrong window width for panel 4**

- **Found during:** Task 1
- **Issue:** The proxy substituted the 60 s sub-window into every panel's `$__range`, but panel 4 is read in its own **120 s** batch (88-04 measured that it renders "No data" at 60 s). The cross-check would have compared a rendered 120 s value against a proxy computed over half that window and manufactured a disagreement out of nothing.
- **Fix:** `$__range` is substituted with the width the panel was actually read at.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `fbc0ece`

**10. [Rule 3 - Blocking] A new variable name collided with the T-88-02 forbidden-token guard**

- **Found during:** Task 1
- **Issue:** `$ScaledTierReplicasBefore` contains the substring `TierReplicas`, which the T-88-02 control asserts **absent** from this file (it is the name of the stale phase-80 static replica map). The guard fired on a variable that is the exact opposite of what it protects against.
- **Fix:** Renamed to `$ScaledReplicasBefore`/`$ScaledReplicasAfter`. The guard is left with its teeth rather than weakened to accommodate a name.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `fbc0ece`

---

**Total deviations:** 10 auto-fixed (5 bugs, 4 missing critical functionality, 1 blocking)
**Impact on plan:** One tabled parameter is **corrected by measurement** (SCALE-03's dwell), one prediction is **corrected by measurement** (panel 4's `slope-up`), and one predicted behaviour occurred only in half (panel 5's spike). No scenario was substituted, no panel was dropped, no fault was invented. Everything else is additive — no artifact field was removed, and `-Mode Baseline` is untouched.

## Issues Encountered

- **SCALE-01 run 1 was discarded and is committed alongside run 2**, exactly as 88-04 did. It was discarded for the three defects above — all in this plan's own new scoring code — and not for a better number. Both artifacts are in `analyzer-reports/` so the discard is auditable rather than a claim.
- **`ReplicasBefore`/`ReplicasAfter` changed shape for scale rows.** `phase-88-WEB-01.json` and `phase-88-WEB-02.json` carry four-tier **maps** in those fields; the three SCALE artifacts carry **scalars**, with the maps under `*ByTier`. `ReplicasFieldNote` states which shape each artifact holds. **The 88-08 roll-up must read `ReplicasFieldNote` (or `*ByTier`) rather than assuming one shape.**
- **The pre-arm baselines of the second and third scenarios contain a recovery burst.** Because the scenarios run back to back, each pre-arm window partly overlaps the previous scenario's restore (SCALE-01's panel-5 pre-arm reads `2.9, 2.57, 1.32, 0.442`). This is harmless — the pre-arm is explicitly recorded and never authoritative, and every score is against the post-settle re-baseline — but a reader comparing `BaselineValues` to `AfterValues` directly would be comparing against a transient.
- **Cross-talk `PreFaultStayedPut` is false for panels 1 and 3 in SCALE-03** for the same reason: their pre-arm window caught SCALE-02's orchestrator recovery burst. `StayedPut` (the control that matters, measured in the same windows as the claim) is true at `MaxExcursion` 0.0000 for all four.
- **The `up-then-nodata` early/late split is fixed at 240 s / 300 s, matching the plan's text**, which was itself written on the research's ~5-minute expiry model. The measured expiry is faster, so the "early" segment is wider than the phenomenon it was cut for. The raw per-sub-window values are in the artifact either way, so a later reader can re-cut the boundary without a re-run.
- **~406 screenshots (38 MB) sit on disk and are gitignored.** Only 19 (792 KB) are committed. Anyone re-verifying visually from a fresh clone has the four not-clean captures and one representative per scenario, and must re-run to see the rest.

## Known Stubs

None in what this plan owns. `-Mode DurationLadder` still exits 64 naming plan 88-08, and the `seam` branch of the engine remains a real implemented path that 88-07 is the first to drive — those are the phase's division of work, not deferrals.

## Threat Flags

None. No product code, no network endpoint, no auth path, no schema change, no package install (T-88-SC holds). Every threat in the plan's register was exercised:

- **T-88-02** — every restore target read live before the mutation and **re-read live afterwards by the driver**, independently of the sequencer's own claim. SCALE-02 additionally verified by a direct `kubectl get deploy orchestrator -o jsonpath={.spec.replicas}` reading 3. The stale phase-80 map is asserted absent from the file, and the guard's teeth survived a name collision rather than being loosened.
- **T-88-01** — the tier comes from the static scenario row; only `processor-sample`, `orchestrator` and `keeper` were scaled, each validated against `$Phase88Tiers`. Nothing is derived from `-ScenarioId`.
- **T-88-22** — no telemetry-path workload appears in the allow-list, so the scenario table cannot express killing the collector, Prometheus or Grafana. None was touched.
- **T-88-20** — three rollouts, three non-empty and **disjoint** pod-identity pairs, `BaselineRecaptured` true on all three, and every score taken against the post-settle re-capture. `RolloutNote` labels the discontinuity an expected artifact.
- **T-88-21** — 11 pre-declared controls across three scenarios, all held at `MaxExcursion` 0.0000, each measured in the **same batch and same windows** as its claim. Panel 1 is correctly absent from SCALE-01's list and panel 3 from SCALE-02's, so each control asserts something falsifiable.
- **T-88-23** — panel 9's `PredictedDirection` is `nodata`, `Moved` is true, and `AfterStates[]` carries three consecutive `NoData` entries. The panel-level rule that makes this scoreable at all is deviation 1.
- **T-88-03** — no seam was armed; `SeamVarsClean` true with an empty `SeamVarsFound` in all three artifacts, re-verified afterwards by a direct env read on both seam-capable tiers.
- **T-88-10** — `ImagesUnchanged` true in all three; `apply -k` asserted absent; all four tiers still on `:tags-const-1544`.
- **T-88-18** — `-Depth 10` present, the shallower depth asserted absent, and the structural guard clean on all four written artifacts.
- **T-88-12** — the rendered panel decided every verdict. Panel 4's proxy disagreement is recorded as a finding and is itself evidence that the panel emptied; every other comparable panel agreed.
- **T-88-04 / T-88-05** — `--address 127.0.0.1` present, `0.0.0.0` absent, the shared forward PID registry never touched; the pre-existing Grafana forward was reused and left exactly as found.

## User Setup Required

None. `kubectl`, the `docker-desktop` context, `node`, the playwright skill and the pre-existing Grafana forward were all already in place.

## Next Phase Readiness

- **Ready for 88-07 (ZERO-02, ZERO-03), with three inputs.** The `scale` branch is now proven end to end and the re-baseline path has run three times, so the `seam` branch's only untested part is the arm itself (PQ-06 already proved that lands). Budget ~35 min per scenario. `SeamActiveDuringRebaseline` is still 88-07's to resolve — this plan's rows arm nothing, so the field is legitimately false in all three artifacts and the question is untouched, not answered. Research assumption A6 remains unretired.
- **Input for 88-08 (LADDER-01) — the most load-bearing carry-forward here.** The 240 s rate lookback **smears every scale rung**: a total processor outage took panel 3 four minutes to fall out of its band and its first in-fault sample was still *inside* it. A short rung will therefore not move a counter panel at all, and a long one will empty it. The ladder must expect three regimes per rung (inside band → below band → NoData), not two, and its panel-4 rungs must expect NoData rather than a rising number.
- **Input for 88-09 — the HAND-02 misleading-by-default list gains four concrete entries**: panel 4 blanks under the very fault it exists to reveal; panel 5 decays and empties rather than spiking; panel 3's fall is a four-minute slope whose first sample is still healthy-looking; and panel 9 fades through decreasing values before it says "No data". The HAND-04 register is unchanged by this plan.
- **Input for 88-10/88-11 (runbook).** Every one of these panels needs a row that says *what the operator will actually see*, because in four of five cases it is not what the dashboard's own title implies. Panel 4's row must say the number disappears; panel 5's must say the gap shrinks before it vanishes; panel 3's must say to read the slope; panel 9's must say "No data" means the keeper is gone and is the correct reading.
- **Schema note for the roll-up.** `ReplicasBefore`/`ReplicasAfter` are scalars in scale artifacts and maps in `http` ones; read `ReplicasFieldNote` or use `ReplicasBeforeByTier`/`ReplicasAfterByTier`, which are always maps.

## Self-Check: PASSED

- `analyzer-reports/phase-88-SCALE-01.json` — FOUND (parses; `Verdict: Inconclusive`; panels 3/4/5 all `Moved` with ≥2 consecutive; `RolloutOld`/`New` non-empty and disjoint; `ReplicasBefore`/`After` = 2; `RangeCumulativeLevelAfter` = 2.12; cross-talk 9/10/12/14 all `StayedPut`; panel 5 early **and** late segments present with the behaviour stated; no `System.Object[]` value) — the plan's own automated verification prints **"SCALE-01 proven"**
- `analyzer-reports/phase-88-SCALE-01-run1-discarded.json` — FOUND (the discarded run, kept deliberately)
- `analyzer-reports/phase-88-SCALE-02.json` — FOUND (parses; `Verdict: Pass`; `ReplicasBefore`/`After` = 3 with `ReplicasRestored` true; panel 1 `Moved` on 3 consecutive; cross-talk 9/10/12 held; `TrafficResumedAfterRestore` true) — the plan's verification prints **"SCALE-02 proven and HA intact"**, including its independent live `kubectl` read of 3
- `analyzer-reports/phase-88-SCALE-03.json` — FOUND (parses; `Verdict: Pass`; panel 9 `PredictedDirection` `nodata`, `Moved` true, three consecutive `NoData` in `AfterStates[]`; `ReplicasBefore`/`After` = 2; cross-talk 1/3/10/12 held; screenshots on disk; `HumanSummary` records panels 2 and 8) — the plan's verification prints **"SCALE-03 proven"**
- `scripts/phase-88-panel-discriminate.ps1` — FOUND (parses via `[ScriptBlock]::Create`; forbidden-token gate clean including `TierReplicas`, `apply -k`, `0.0.0.0`, `-Depth 5`, `scale statefulset`; `NOPE-99`, `ZERO-01` and a `-Mode` mismatch each still exit 64)
- Commit `fbc0ece` — FOUND
- Commit `bb064a4` — FOUND
- Commit `a502a2f` — FOUND
- Commit `4ecf2e3` — FOUND
- `pwsh -File scripts/phase-87-dashboard-lint.ps1` — exit 0 (no dashboard JSON was touched)
- Live stack re-verified after all three runs by an independent `Assert-StackRestored`: `Ok=True`, `SeamVarsClean`/`ReplicasRestored`/`ImagesUnchanged` all true, zero replica mismatches, zero image mismatches, zero seam vars, zero unknown tiers; `kubectl` reads 1/2/3/2 on `:tags-const-1544`; zero background jobs; the pre-existing Grafana forward left running and none started

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-29*
