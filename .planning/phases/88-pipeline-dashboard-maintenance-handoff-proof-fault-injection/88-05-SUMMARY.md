---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 05
subsystem: testing
tags: [grafana, playwright, prometheus, powershell, kubernetes, scenario-engine, cross-talk, disc-02, disc-03, disc-06, wave-4]

# Dependency graph
requires:
  - phase: 88-01
    provides: "the batch Grafana panel DOM reader — Get-PinnedWindowSeries, Invoke-PanelReadBatch, Get-PanelSamples/States/SeriesCount/SeriesNameAt, Get-PanelBand, Test-PanelMoved"
  - phase: 88-02
    provides: "the runtime-only cluster-ops library — Invoke-TierScaleFault, Set-/Clear-Phase88Seam, Get-TierPodNames, Wait-TierSettled, Assert-StackRestored and the 60/61/62/65 failure-code vocabulary"
  - phase: 88-03
    provides: "the locked levers — LocatorMode viewpanel, RateIntervalSeconds 240, WEB-02 Dropped on Safe5xxFound=false, and the two ENUMERATED inert status drivers (404 unmatched path, 400 empty activation)"
  - phase: 88-04
    provides: "the driver frame, the static scenario table, the PRE gate, -Mode Baseline and analyzer-reports/phase-88-BASE-01.json — the 42 DISC-01 bands this plan scores against"
provides:
  - "-Mode Scenario — the scenario engine every remaining fault scenario in the phase drives unchanged"
  - "the mandatory arm -> rollout -> settle >=150 s -> re-baseline -> trigger -> after -> restore -> assert sequence, with the rollout discontinuity recorded as an EXPECTED artifact"
  - "cross-talk scoring with teeth: strict StayedPut, MaxExcursion, and a PreFaultStayedPut reading that separates blast radius from baseline drift"
  - "a sustained (never burst) HTTP load driver, ~10 parallel requesters, non-health routes only"
  - "-RecordDroppedRow — a Dropped scenario becomes a ROW with an enumerated reason, after its drop is re-confirmed against the wave-0 probe"
  - "analyzer-reports/phase-88-WEB-01.json — panels 10, 11, 12 and 14 proven to discriminate, cross-talk 1/3/9 held"
  - "analyzer-reports/phase-88-WEB-02.json — panel 13's accepted-unproven record with its observed guarded zero"
  - "the measured fact that http_server_active_requests reads 0 at EVERY export tick under ~10 sustained requesters"
  - "the measured fact that panel 12's status-code series never leave the legend, so its discrimination is numeric and not a legend gain"
affects: [88-06, 88-07, 88-08, 88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "a multi-series panel MOVED when at least one series moved, with EVERY series' own outcome recorded in SeriesResults[] and the panel entry NAMING the series that carried it — so the selection is auditable rather than asserted"
    - "band lookup BY NAME FIRST with a row-index fallback, because a new series shifts every later index"
    - "a series with no baseline band is a recorded FINDING, never scored as movement — there is no null hypothesis it could have moved away from"
    - "a band computed from fewer than 3 samples cannot falsify a non-move: too-thin bands make a panel Inconclusive, never Fail"
    - "cross-talk carries a PRE-FAULT reading of the same series, so 'the control drifted' can be split into blast radius vs baseline drift"
    - "a Dropped row is RECORDED behind an explicit switch after re-confirming the probe field that dropped it; without the switch it still exits 64"

key-files:
  created:
    - analyzer-reports/phase-88-WEB-01.json
    - analyzer-reports/phase-88-WEB-02.json
    - analyzer-reports/phase-88-screenshots/WEB-01/ (84 PNGs, 5.9 MB)
    - analyzer-reports/phase-88-screenshots/WEB-02/ (6 PNGs, 244 KB)
  modified:
    - scripts/phase-88-panel-discriminate.ps1

key-decisions:
  - "WEB-02 was NOT attempted and no substitute fault was invented — it is recorded as a ROW with the seven enumerated probe endpoints and the Phase-86 readiness-latch reason"
  - "Panel 12's legend-gain acceptance criterion is MEASURED-UNSATISFIABLE on this stack: all six status series were already present-and-zero before the drive. The discrimination is the stronger numeric form (two exactly-zero bands going non-zero for 6 consecutive samples), and WEB-01 was NOT re-run to chase a criterion the measurement corrects"
  - "http_server_active_requests read exactly 0 at every one of six export ticks under 2456 sustained requests — panel 14's movement is carried by kestrel_active_connections alone. Recorded as a finding for HAND-02 and as a hard input to LADDER-01"
  - "Panel 11's movement is TRANSIENT (a load-onset spike that decays back inside the band under sustained load), not a level shift — it moved on exactly the 2-sample minimum"
  - "Cross-talk StayedPut is STRICT (inside the band for the entire window), matching the locked rule, with SamplesOutside/ConsecutiveSamplesOutside recorded so a single-sample wobble stays distinguishable from a sustained excursion"
  - "No requirement is marked Complete: DISC-02 is 4/14, DISC-03 has run on one scenario, and DISC-06's re-baseline path is authored but UNEXERCISED because WEB-01 causes no rollout"

patterns-established:
  - "A scenario's pre-arm baseline is taken over the window that has just ELAPSED, offset by the export trail — it costs no wall time and is exactly the state immediately before the fault"
  - "The after window is held ENTIRELY inside sustained load plus an export trail, because a burst is invisible to a 60 s-sampled gauge"
  - "The canonical table key, never the caller's string, reaches a file path — an [ordered] hashtable matches keys case-insensitively"

# This plan's `requirements` frontmatter names DISC-02, DISC-03 and DISC-06. NONE is marked
# complete — see Decisions. Four of fourteen panels are proven, one scenario has run under the
# cross-talk rule, and the DISC-06 re-baseline path has not yet been exercised by any rollout.
requirements-completed: []

# Metrics
duration: 75min
completed: 2026-07-28
---

# Phase 88 Plan 05: WEB-01 Scenario Engine Summary

**The engine that turns a fault into a defensible before/after assertion now exists and has been shaken out on the zero-risk row — four of the five WebApi panels are proven to move from the rendered panel over at least two consecutive samples while three pre-declared controls held their bands with an excursion of exactly zero, and the fifth panel is recorded as a row rather than an absence.**

## Performance

- **Duration:** ~75 min (of which the live WEB-01 run is ~17 min and the WEB-02 record ~2 min)
- **Completed:** 2026-07-28
- **Tasks:** 3 of 3
- **Files modified:** 1 script, 2 artifacts, 90 screenshots

## Accomplishments

- **`-Mode Scenario` implements the mandatory DISC-06 sequence literally and in order**: pre-arm baseline (recorded, never authoritative) → arm → `rollout status` → settle ≥ 150 s → **re-capture the authoritative band** → trigger → after → restore, disarm, assert. `RolloutOldInstanceIds` / `RolloutNewInstanceIds` / `RolloutUtc` are artifact fields labelled an **expected artifact**, and a row whose lever causes no rollout **states** `BaselineRecaptured: false` with its reason rather than omitting the field — so "no rollout happened" and "nobody checked" cannot look alike.
- **WEB-01 verdict `Pass`, exit 0. 4/4 asserted panels moved; 3/3 cross-talk controls held.** Every claim and its control were read in **one** `Invoke-PanelReadBatch` over the same six abutting 60-second absolute windows, so the control is measured in the same windows as the claim rather than adjacent to it.
- **Panel 14's gauges were driven by sustained concurrency, not a burst** — 2456 requests from ten parallel requesters spanning six export ticks. `kestrel_active_connections` went from a band of `-0.21..1.21` to `2.5, 4.5, 5.0, 5.0, 3.5, 4.5`, moved on all six.
- **The cross-talk control has real teeth and was measured, not assumed.** Panels 1, 3 and 9 each returned `MaxExcursion` **0.0000** — every sample inside its DISC-01 band for the whole window. HTTP traffic against the WebApi has no causal path to the orchestrator/processor conservation counters or the keeper L2 heartbeat, and the measurement says so numerically.
- **The diagnostic proxy agrees where it can.** Panel 10 proxy `3.284` vs rendered `3.423`; panel 12 `3.284` vs `3.423`; panel 14 `4.000` vs `4.167`. Panel 11 is non-comparable by design (`histogram_quantile` is a per-series quantile that cannot be meaningfully summed) and says so rather than asserting a false agreement.
- **The stack was left exactly as found.** `SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` all true in both artifacts and independently re-verified afterwards through `Assert-StackRestored`: 1/2/3/2 on `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY`, zero background jobs, and the pre-existing Grafana forward reused and left running.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 1 | `-Mode Scenario` — the engine | `0d54ae3` | feat |
| 2 | WEB-01 live run | `68cbcc6` | feat |
| 2b | WEB-01 screenshot evidence | `e3d1921` | chore |
| 3 | WEB-02 accepted-unproven row | `a2053f5` | feat |

## What WEB-01 Measured

| Panel | Predicted | Scored series | Band | After | Consecutive |
|---|---|---|---|---|---|
| 10 `WebApi request rate by route` | up | unlabelled row | `0..0` | `0.056 → 0.436` | **6** |
| 11 `WebApi p95 request duration by route` | up | `.../Orchestration/start` | `0.318..12.082` | `22.0, 19.7, 11.2, 4.9, 4.8, 4.8` | **2** |
| 12 `WebApi status-code mix` | up | `400` | `0..0` | `0.052 → 0.436` | **6** |
| 14 `WebApi in-flight requests and Kestrel connections` | up | `kestrel active …mcs29` | `-0.207..1.207` | `2.5, 4.5, 5.0, 5.0, 3.5, 4.5` | **6** |

Six of panel 10's thirteen series moved (five named read routes plus the unlabelled row); the seven route series that were never driven stayed at exactly 0, which is the correct outcome and is recorded. Panel 12's `400` **and** `404` each moved off an exactly-zero band for six consecutive samples, and `200` went `0.19..0.58 → 3.94`.

Cross-talk: panels 1, 3 and 9, `StayedPut` true, `MaxExcursion` `0.0000`, `PreFaultStayedPut` true.

## Findings Worth Carrying Forward

**1. `http_server_active_requests` read exactly 0 at every export tick — under sustained load.** Pitfall 8 warned that a *burst* would leave panel 14 reading zero. The measurement is stronger and more useful: even with ten parallel requesters issuing 2456 requests over 480 s, the in-flight gauge sampled 0 in **all six** windows, because each request completes in single-digit milliseconds and the gauge is instantaneous at a 60 s cadence. Panel 14's movement is carried entirely by `kestrel_active_connections`, which is a **connection count** kept alive by HTTP keep-alive and is therefore a different kind of quantity. **LADDER-01 (88-08) must not treat panel 14's three series as one gauge rung** — two of them cannot be moved by this class of load at all, and the runbook should say which one an operator can actually read.

**2. Panel 12's status series never leave the legend.** All six (`200/201/204/400/404/422`) were present *and rendering zero* in the pre-fault window and remained the same six afterwards. On this stack the WebApi process has been up long enough that every status counter it has ever emitted retains its series, so a status code that has occurred once never disappears. Panel 12's discrimination is therefore **numeric** (an exactly-zero band going non-zero), not a legend gain. This is the same shape as panel 13's permanent-series note and belongs in the HAND-02 list.

**3. Panel 11's movement is transient, not a level shift.** The p95 spike is at **load onset** (22 ms, 19.7 ms) and decays back inside the band (`4.8 ms`) within three minutes of sustained load — the API is genuinely fast once warm. It cleared the two-consecutive-sample minimum and no more. A maintenance reader watching panel 11 for a *sustained* latency shift would see nothing; the signal is the leading edge.

**4. Panel 10 renders an unlabelled series called `Value`.** The 404 driver hits an unmatched path, which carries no `http_route` label, so Grafana names its row `Value`. It is a real series with a real rate, and it is the row that moved most cleanly — but an operator has no way to tell from the panel what it is. HAND-02 material.

## Decisions Made

- **WEB-02 was not attempted and no substitute was invented.** The probe measured `Safe5xxFound: false` across seven enumerated endpoints; the redis dependency-outage route was already explicitly refused in 88-03 and is refused again here. The row is recorded with all seven endpoints and their observed statuses, the Phase-86 readiness-latch cost, and maintenance guidance that panel 13's guarded green `0` is indistinguishable from "the counter has never been emitted".
- **The dropped-row recorder re-confirms the drop before writing it.** Each Dropped row names its own `droppedProbeField` / `droppedProbeExpect`, and a probe artifact that no longer agreed would exit 64 rather than record a stale unproven claim. Without `-RecordDroppedRow` a Dropped id still exits 64 exactly as 88-04 established — the switch names a *different act*, it does not weaken the guard.
- **Panel 13 was OBSERVED rather than asserted.** Six pinned windows, `PanelStateAfter: Rendered`, values `0,0,0,0,0,0` against its `0..0` band. The observation window overlaps WEB-01's 2456-request drive including its 404s and 400s, so this is a guarded zero seen **under real traffic**, not on an idle stack.
- **WEB-01 was NOT re-run.** The one acceptance criterion that did not pass is corrected by the measurement itself (see Deviation 1); re-rolling a clean run to chase it would have been the unfalsifiable behaviour 88-04 warned against, and it would have risked panel 11's two-sample transient on a second draw.
- **`-Depth 10` with a structural guard, unchanged from 88-04.** `PanelResults[]` and `CrossTalkPanels[]` are objects inside arrays carrying nested `Values[]`/`States[]`; both artifacts were checked for a JSON *value* of the truncation signature and both are clean.
- **No requirement is marked Complete.** DISC-02 asks for all fourteen panels (four are proven), DISC-03 for *every* scenario (one has run), DISC-06 for every seam- or scale-dependent scenario (WEB-01 is neither, so the re-baseline path is authored but unexercised). This continues the discipline 88-01 through 88-04 all held.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Task 2's panel-12 legend-gain criterion is measured-unsatisfiable on this stack**

- **Found during:** Task 2 verification
- **Issue:** The criterion requires `LegendNamesAfter[]` to contain a series name absent from `LegendNamesBaseline[]`, on the theory that driving a 404 and a 400 would *create* those legend rows. Measured: panel 12 rendered exactly `200 | 201 | 204 | 400 | 404 | 422` in **both** windows. The series already exist — Prometheus retains a counter series for as long as the emitting process lives, and the WebApi pod has been up since Phase 80 — so they render **present and zero** before the drive rather than being absent. 88-04's own carry-forward predicted this ("series that are currently all-zero will carry values"), and its BASE-01 artifact bands all six.
- **Fix:** The criterion is recorded as corrected by measurement and the **substantive** claim it was reaching for is proven in the stronger form the phase already has a rule for: panel 12's `400` and `404` each carried a scoring band of **exactly `0..0`** and moved off it for **6 consecutive samples** (`0 → 0.436` and `0 → 0.436` ops/s). That is Regime-B discrimination — a single event falsifies the null hypothesis — and it is machine-checkable from the artifact's `SeriesResults[]` without any new field. No third status driver was invented to manufacture a legend gain; PROBE-DECISIONS Record 4 is explicit that WEB-01 drives *exactly* the two enumerated inert drivers.
- **Files modified:** none (the measurement corrects the criterion; the artifact already carries the evidence)
- **Recorded in:** `68cbcc6`

**2. [Rule 2 - Missing critical functionality] The plan's after-window is longer than its own dwell, so the load had to span the window rather than the dwell**

- **Found during:** Task 1
- **Issue:** WEB-01 declares `dwellSeconds: 180`, but the plan also requires the after-capture to cover "at least 5 sub-windows". Five 60-second windows is 300 s — longer than the dwell — so a load driven for only `dwellSeconds` would leave the earliest sub-windows *outside* the load, and a gauge panel scored across them would read zero for the first two ticks.
- **Fix:** The after window is held **entirely inside** sustained load: ramp 30 s → window 360 s → export trail 120 s, load running for all 510 s. The window is six sub-windows rather than five so it is an exact multiple of 120 s, which is what panel 4 needs whenever a later scenario asserts on it. `DwellSeconds` (declared) and `LoadActualSeconds` (driven) are both recorded so the difference is visible rather than implicit.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `0d54ae3`

**3. [Rule 2 - Missing critical functionality] A single-slot seam teardown would leave ZERO-03's trigger seam armed**

- **Found during:** Task 1
- **Issue:** The driver's outer `finally` tracked exactly one `(tier, var)` pair. ZERO-03 arms **two** seams (`PROCESSOR_DEFEAT_READ` to create the recovery event, `KEEPER_DEFEAT_REINJECT` to suppress the reinject), so an interrupted run would have disarmed one and left the other live — the standing Pitfall-5 poisoning, presenting later as an inexplicable conservation violation with no pointer back.
- **Fix:** A second slot (`$seamArmed2` / `$seamTier2` / `$seamVar2`) declared before the `try` and disarmed unconditionally in the same `finally`, plus a happy-path disarm of both in reverse arm order.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `0d54ae3`

**4. [Rule 2 - Missing critical functionality] The scenario id could reach a file path in the caller's casing**

- **Found during:** Task 1
- **Issue:** `$Scenarios` is an `[ordered]` hashtable, whose key comparison is case-**insensitive**. `-ScenarioId web-01` therefore selects the WEB-01 row and would then have written `analyzer-reports/phase-88-web-01.json` — an artifact the 88-08 roll-up would never find, from a run that otherwise looked entirely successful.
- **Fix:** `$canonicalId` resolves the **table's own key** and is what reaches the artifact path and the `ScenarioId` field. T-88-13's "the id selects a row and nothing else" now covers the filename too.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `0d54ae3`

**5. [Rule 2 - Missing critical functionality] A too-thin band cannot falsify a non-move, and the plan's rules would have called one a Fail**

- **Found during:** Task 1
- **Issue:** 88-04 measured one panel-11 series (`Workflows`) banded from only **2 of 10** samples, and carried forward the caution that a non-move there is an Inconclusive rather than a discrimination defect. The plan's scoring rules as written ("a panel read successfully whose value did not move is Fail") contain no such distinction, so the caution would have depended on a reader noticing it after the fact.
- **Fix:** `$MinBandSamples = 3` in code. A series whose scoring band came from fewer than three samples is recorded but is **not** Fail-eligible, and a panel whose every candidate series is thin-banded goes to `UnevaluablePanels[]` with the reason instead of failing. The judgement is enforced rather than remembered.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `0d54ae3`

**6. [Rule 3 - Blocking] `Get-ProxyAggregateMean` was defined after the point the scenario engine needed it**

- **Found during:** Task 1
- **Issue:** 88-04 defined the proxy-aggregate helper inside its STEP H, which runs *after* the mode dispatch. PowerShell only sees a function once its definition statement has executed, so the scenario engine — which dispatches earlier — could not have called it, and the T-88-12 diagnostic control would have been absent from exactly the artifacts it exists to protect.
- **Fix:** Moved verbatim to the shared HELPERS section (including its long-parameter-name warning comment), with a pointer left at the old site. Both modes now cross-check through one definition.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `0d54ae3`

---

**Total deviations:** 6 auto-fixed (1 measurement-corrected criterion, 4 missing critical functionality, 1 blocking)
**Impact on plan:** One acceptance criterion is **corrected by measurement** and its substantive claim proven in a stronger form. Everything else is additive — no interface was removed or renamed, and `-Mode Baseline` is byte-for-byte unchanged in behaviour.

## Issues Encountered

- **The DISC-06 re-baseline path is authored but UNEXERCISED.** WEB-01's lever is `http`, which rolls nothing, so `BaselineRecaptured` is legitimately false. The seam and scale branches — arm → `Wait-TierSettled` → 150 s settle → ten-sub-window re-capture — have never run. **88-06 is their first real test**, and they add roughly 14 minutes per scenario (150 s settle + 600 s window + 120 s export trail) that plan should budget for.
- **`SeamActiveDuringRebaseline` is an honest caveat 88-07 must resolve, not inherit.** For ZERO-02 the seam *is* the fault, so arming it before the re-baseline means the authoritative band would be captured with the fault already running. The engine records this as an explicit artifact field rather than hiding it. 88-07 must decide whether ZERO-02's seam value can be scoped so the fault only fires at trigger time, or whether that row's band must come from the pre-arm capture instead.
- **The scored-series tie-break is arbitrary among equal movers.** Panel 10's `Value` row and `Orchestration/start` both moved on six consecutive samples; the artifact names the first by sort order. All thirteen series' outcomes are in `SeriesResults[]`, so nothing is hidden — but a reader skimming only the panel entry sees the least self-explanatory series name on the panel.
- **A ZERO-01 dropped-row artifact was produced during a guard smoke test and deleted.** `-RecordDroppedRow` was exercised against ZERO-01 to shake out the path before the WEB-01 run; it worked end to end (exit 2, six windows, stack clean) but ZERO-01's record is not this plan's to own, so the artifact and its screenshots were removed rather than left for 88-09 to find with a window from the wrong day.
- **The pre-existing Grafana forward was reused, not replaced.** Both runs detected loopback 3000 already carrying traffic, started nothing, and tore nothing down.

## Screenshot Evidence Cost — measured, so the pending decision is informed

- **WEB-01: 84 PNGs, 5.9 MB.** 7 panels × 6 windows for the pre-arm capture and again for the after capture.
- **WEB-02: 6 PNGs, 244 KB.**
- **Total this plan: 6.1 MB** — roughly a third of the ~17 MB a per-run extrapolation from BASE-01 predicted, because a scenario reads 7 panels over 6 windows rather than 14 over 10. BASE-01 remains the expensive capture at 15 MB / 282 PNGs.

A scale or seam scenario adds a **third** capture (the ten-window re-baseline), so a rough forward estimate is ~10–12 MB per rollout scenario. The cumulative-policy decision for 88-06 onward is explicitly **not** taken here.

## Known Stubs

None in what this plan owns. `-Mode DurationLadder` still exits 64 naming plan 88-08 — that is the phase's own division of work, not a deferral. The `scale` and `seam` branches of the engine are real implemented paths that WEB-01's lever did not need; they are conditional branches, and their first exercise is 88-06/88-07.

## Threat Flags

None. No product code, no network endpoint, no auth path, no schema change, no package install (T-88-SC holds). Every threat in the plan's register was exercised:

- **T-88-19** — concurrency fixed at ten requesters, bounded by the caller's duration, jobs reaped on the happy path **and** in the outer `finally`; zero background jobs remained afterwards. No `/health/` route appears on any load line.
- **T-88-16** — no dependency outage was driven; `scale statefulset` is absent from the driver entirely, and the WEB-02 dropped branch says so in its own artifact (`DependencyOutageDriven: false`).
- **T-88-12** — the rendered panel decided every verdict; the proxy value is recorded beside it with `DiagnosticAgreesWithPanel`, and panel 11's non-comparability is stated rather than asserted away.
- **T-88-20** — `BaselineRecaptured`, `RolloutOldInstanceIds`, `RolloutNewInstanceIds`, `RolloutUtc` and a `RolloutNote` naming the discontinuity an **expected artifact** are all present; WEB-01 and WEB-02 cause no rollout and say so explicitly.
- **T-88-21** — the startup table validation still refuses any `scenario` row with an empty cross-talk list; WEB-01's three controls were scored in the same batch as the claim, and a drift would have set `CrossTalkHeld` false with its `MaxExcursion` rather than being reclassified.
- **T-88-03** — `Clear-Phase88Seam` on the happy path **and** in the outer `finally`, now for both seam slots; `SeamVarsClean: true` with an empty `SeamVarsFound` in both artifacts.
- **T-88-02** — every restore goes through the live-read path; `ReplicasRestored` re-read independently at 1/2/3/2 after the run.
- **T-88-18** — `-Depth 10` present, the shallower depth asserted absent, and the structural guard clean on both written artifacts.
- **T-88-04 / T-88-05** — `--address 127.0.0.1` present, `0.0.0.0` absent, the shared forward PID registry never touched; the reused forward was left exactly as found.

## User Setup Required

None. `kubectl`, the `docker-desktop` context, `node`, the playwright skill and the pre-existing Grafana forward were all already in place.

## Next Phase Readiness

- **Ready for 88-06 (SCALE-01/02/03), with three cautions.** The engine's `scale` branch is unexercised; budget ~14 min per scenario for the settle + re-baseline + export trail; and **SCALE-01 asserts panel 4**, which the engine reads in its own 120 s batch — the after window must stay an exact multiple of 120 s (six 60 s sub-windows is, five is not).
- **Ready for 88-07 (ZERO-02/03), with one open question.** Both seam slots and the two-seam teardown are in place, but `SeamActiveDuringRebaseline` records that a seam armed before the re-baseline is active *during* it. 88-07 owns that decision. Research assumption A6 remains unretired.
- **Input for 88-08 (LADDER-01).** Panel 14 is **two** rungs, not one: `kestrel_active_connections` responds to ~10 sustained requesters and `http_server_active_requests` did not respond at all. `RateIntervalSeconds: 240` is recorded in both artifacts and no rung re-derives it.
- **Input for 88-09.** The HAND-04 register gains panel 13's record with its seven enumerated endpoints and its observed guarded zero. The HAND-02 misleading-by-default list gains three concrete entries: panel 14's in-flight gauge that reads 0 under real load, panel 12's permanently-present status series, and panel 10's unlabelled `Value` row.
- **Input for 88-10/88-11.** Panel 11's discrimination is a **leading-edge transient**, not a sustained level shift — the runbook row must say so, or an operator will watch a settled panel and conclude nothing happened.

## Self-Check: PASSED

- `scripts/phase-88-panel-discriminate.ps1` — FOUND (parses via `[ScriptBlock]::Create`; all fourteen required tokens present; `Clear-Phase88Seam` ≥ 2; forbidden-token gate clean; no `/health/` on any load line; settle ≥ 150 s found)
- `analyzer-reports/phase-88-WEB-01.json` — FOUND (parses; `Verdict: Pass`; 4/4 panels `Moved` with ≥ 2 consecutive; cross-talk 1/3/9 `StayedPut`; `SubWindowSeconds` 60; after window 360 s; three restore claims true; no `System.Object[]` value)
- `analyzer-reports/phase-88-WEB-02.json` — FOUND (parses; `Verdict: Inconclusive`; `Moved: false`; `AcceptedUnprovenReason` non-empty and cites the readiness latch; three restore claims true; no `System.Object[]` value)
- `analyzer-reports/phase-88-screenshots/WEB-01/` — FOUND (84 PNGs)
- `analyzer-reports/phase-88-screenshots/WEB-02/` — FOUND (6 PNGs)
- Commit `0d54ae3` — FOUND
- Commit `68cbcc6` — FOUND
- Commit `e3d1921` — FOUND
- Commit `a2053f5` — FOUND
- `pwsh -File scripts/phase-87-dashboard-lint.ps1` — exit 0 (no dashboard JSON was touched)
- Live stack re-verified after both runs via `Assert-StackRestored`: `Ok=True`, replicas 1/2/3/2, images all `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY`, zero mismatches, zero background jobs
- Guard paths re-checked: `NOPE-99`, `WEB-02` (without the switch), `ZERO-01` (without the switch) and a `-Mode` mismatch each exit **64**

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-28*
