---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 08
subsystem: testing
tags: [grafana, prometheus, kubernetes, powershell, fault-injection, duration-ladder, minimum-detectable-duration, roll-up, discrimination-matrix, disc-02, disc-07, wave-7]

# Dependency graph
requires:
  - phase: 88-01
    provides: "the batch Grafana panel DOM reader — Invoke-PanelReadBatch, Get-PinnedWindowSeries, Get-PanelSamples/States/SeriesCount, Get-PanelBand, Test-PanelMoved with its first-class NoData direction"
  - phase: 88-02
    provides: "Invoke-TierScaleFault with its live-read replica capture, terminate-wait, dwell and explicit ReplicasRestored claim; Assert-StackRestored; the 60/61/62/65 failure-code vocabulary"
  - phase: 88-03
    provides: "the locked levers — LocatorMode viewpanel, RateIntervalSeconds 240 (PQ-04 RateIntervalPinned), and the corrected ~9.4-day retention figure that made a long ladder affordable"
  - phase: 88-04
    provides: "analyzer-reports/phase-88-BASE-01.json — the 42 DISC-01 bands the roll-up collapses, and the measured fact that panel 4 cannot be banded at 60 s"
  - phase: 88-05
    provides: "-Mode Scenario and the capture/banding/scoring helpers this plan lifted into the shared section; the sustained HTTP load driver; the measured fact that panel 14 is TWO rungs"
  - phase: 88-06
    provides: "the ~180 s rate-survival measurement, the panel-4 empties-under-fault finding, and the scalar-vs-map ReplicasFieldNote warning the roll-up honours"
  - phase: 88-07
    provides: "panel 2 proven, panel 8 measured-unreachable with its three enumerated reasons, and the DISC-04 escalation this roll-up carries forward"
provides:
  - "analyzer-reports/phase-88-LADDER-01.json — the DISC-07 minimum detectable fault duration, MEASURED per regime: 240 s counter, 120 s gauge, plus the stated Regime-B any-duration property"
  - "the measured falsification of the research's 120 s counter prediction — at 120 s the panel dips 33 % and clears its band for exactly ONE sample, one short of the two the rule requires"
  - "the measured fact that a queued pipeline HIDES a short outage from a rate panel: the broker buffers and the tier drains the backlog inside the same rate window, so the trough model over-predicts the dip at every rung"
  - "the measured fact that the gauge regime is limited EXACTLY by the export cadence: consecutive-samples-outside equalled floor(D/60) at all three rungs, 0, 1 and 2"
  - "scripts/phase-88-rollup.ps1 — the panel-keyed roll-up writer, read-only, never re-scores"
  - "analyzer-reports/phase-88-discrimination.json — the fourteen-row matrix: 10 Proven, 4 AcceptedUnproven, 13 misleading-by-default, every discard and every measured-unsatisfiable criterion carried on the row it belongs to"
affects: [88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "a helper defined inside a mode branch does not EXIST for any other mode — shared measurement code lives in one definition or the two modes measure differently"
    - "a rung is an independent measurement: its own pre-rung band, and its baseline window may not BEGIN until the rate window has cleared plus a settle past the previous fault"
    - "the measured quantity and the modelled quantity are recorded side by side WITH their kinds, so a drop is never silently compared against a residual"
    - "a metric that is never null: 'not computable' and 'zero' are different statements, so a zero-mean band and an emptied panel each get a stated basis rather than a null"
    - "a control the fault is EXPECTED to move is not a control — panel 12 is excluded from the gauge rungs because the HTTP load a gauge rung drives is what moves it"
    - "a roll-up that joins artifacts written by four different plans reads every cross-artifact field defensively; an absent field is a stated default, never an abort and never an invention"
    - "when the schema is a fixed-length array, the things that must not be smoothed away are carried ON the rows rather than dropped for lack of a top-level object"

key-files:
  created:
    - analyzer-reports/phase-88-LADDER-01.json
    - analyzer-reports/phase-88-discrimination.json
    - scripts/phase-88-rollup.ps1
    - analyzer-reports/phase-88-screenshots/LADDER-01/ (230 PNGs on disk, 21 committed)
  modified:
    - scripts/phase-88-panel-discriminate.ps1

key-decisions:
  - "MinDetectableCounterSeconds is 240 s, NOT the 120 s the research predicted. At 120 s panel 3 dipped 33 % and cleared its band for exactly ONE consecutive sample — one short of the two-sample rule. Both numbers are recorded and the prediction is NOT adjusted to fit"
  - "MinDetectableGaugeSeconds is 120 s, which AGREES with the prediction — and the agreement is stronger than a coincidence: the measured consecutive-samples-outside equalled the theoretical maximum floor(D/60) at every one of the three rungs (0, 1, 2)"
  - "The plan's literal TheoreticalDipFraction expression `1 - min(D,240)/240` is the RESIDUAL trough as a fraction of baseline, not a DROP, while the measured DipFraction it sits beside is a drop. Comparing them would have manufactured a divergence at every rung, so both quantities are recorded with their kinds stated and neither is substituted for the other"
  - "The scenario engine's 'a panel that was read and did not move is a Fail' rule deliberately does NOT apply to a ladder: finding where detection STOPS is the whole point, so a non-moving rung is a measurement. Fail is reserved for a stack that did not restore or a control that drifted"
  - "Cross-talk is measured on the LONGEST rung of each regime rather than on all eight, because a fault's blast radius is monotonic in its duration; panel 12 is excluded from the gauge controls because it is causally downstream of the load a gauge rung drives"
  - "The roll-up's `baselineBand` envelope is a HUMAN SUMMARY and nothing scores against it; all 42 per-series bands are reproduced verbatim, and choosing a primary series was rejected as hidden evidence loss"
  - "Only DISC-07 is marked Complete. DISC-02's answer now exists in full (14/14 accounted for) but the requirement binds it to HAND-04's register, which 88-09 authors"

patterns-established:
  - "Measure the mechanism before trusting the model: five counter rungs falsified a trough model that had been carried since the research, and the reason (broker buffering) is invisible from the panel"
  - "A detectability theory is testable in its own terms: recording MaxPossibleConsecutiveSamples beside the measured run turned 'the gauge rung did not move' from a shrug into a prediction that held exactly"
  - "A long unattended run is launched detached and polled by a separate short watcher; its per-wait log line states what it is waiting FOR, because a silent sixteen-minute sleep is indistinguishable from a hang"

# This plan's `requirements` frontmatter names DISC-02 and DISC-07.
# DISC-07 IS marked complete — it is measured, per regime, from real rungs, and this plan is the
# only one that could satisfy it. DISC-02 is NOT: its answer is complete and machine-checkable in
# the matrix, but REQUIREMENTS.md binds it to HAND-04's register ("the two cannot drift apart"),
# and that register is 88-09's deliverable.
requirements-completed: [DISC-07]

# Metrics
duration: 180min
completed: 2026-07-29
---

# Phase 88 Plan 08: Duration Ladder and Discrimination Roll-Up Summary

**The minimum detectable fault duration is 240 seconds for counter-backed panels and 120 seconds for gauge-backed ones — and the counter number falsifies the prediction this phase has carried since its research, because a queued pipeline hides a short outage from a rate panel by draining the backlog inside the same rate window.**

## Performance

- **Duration:** ~180 min, of which the live ladder is ~161 min unattended (00:06Z → 02:47Z)
- **Completed:** 2026-07-29
- **Tasks:** 3 of 3
- **Files modified:** 1 script modified, 1 created, 2 artifacts, 230 screenshots on disk (21 committed)

## Accomplishments

- **DISC-07 is measured, in two regimes, from eight real rungs** — five counter rungs driven by a `processor-sample` tier outage and three gauge rungs driven by the same sustained HTTP concurrency WEB-01 used. Every rung carries its own pre-rung band, its own after window, its own `ReplicasRestored` claim and its own start/end timestamps.
- **The counter prediction is falsified and the gauge prediction is confirmed**, and the artifact records both predictions beside both measurements with `PredictionAgreesWithMeasurement: false` and a per-regime detail string, so the disagreement is visible rather than smoothed over.
- **Regime B is stated as the third answer** it is: for a zero-floor guarded counter a single event is detectable at *any* duration — with the two caveats that matter more than the property (the guard makes a green 0 indistinguishable from "never emitted", and the property belongs to the `increase()`/`$__range` form, not to `rate()`).
- **3/3 cross-talk controls held at `MaxExcursion` exactly 0.0000** — panels 9 and 12 on the 480 s counter rung, panel 9 on the 120 s gauge rung. LADDER-01 was the last scenario in the phase with no cross-talk measurement at all.
- **The fourteen-row discrimination matrix exists**: `10/14 Proven, 4/14 AcceptedUnproven`, `13/14 misleadingByDefault` with a note grounded in what a scenario measured, all 42 BASE-01 per-series bands reproduced verbatim, and every row citing the artifact each value came from.
- **Nothing was smoothed away.** All three discarded runs, six measured-unsatisfiable acceptance criteria and the DISC-04 escalation survive into the matrix, carried on the rows they belong to.
- **The stack was left exactly as found.** `Assert-StackRestored` true on all three claims; an independent `kubectl` read afterwards shows 1/2/3/2 on `:tags-const-1544` with zero `DEFEAT`/`REINJECT_DELAY`. `pwsh -File scripts/phase-87-dashboard-lint.ps1` still exits 0. The pre-existing Grafana forward was reused and left running.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 1 | `-Mode DurationLadder` — the two-regime rung ladder | `41c6609` | feat |
| 2 | LADDER-01 live run — DISC-07 measured | `789575c` | feat |
| 3 | `scripts/phase-88-rollup.ps1` + the fourteen-row matrix | `9e0c498` | feat |

## What The Ladder Measured

### Counter regime — panels 3 and 4, `scale` on `processor-sample`

| Rung | Measured dip | Theoretical dip | Consecutive samples outside | Detected? |
|---|---|---|---|---|
| 30 s | **0.001** | 0.125 | 0 | no |
| 60 s | **0.100** | 0.250 | 1 | no |
| 120 s | **0.329** | 0.500 | 1 | **no** — the research predicted this rung |
| 240 s | **0.499** | 1.000 | 2 | **YES — the measured answer** |
| 480 s | **0.744** | 1.000 | 3 | yes |

`MinDetectableCounterSeconds = 240`. Predicted 120. **DISAGREES.**

Verbatim, the 120 s rung — the one the prediction named:

```
band   0.4989 .. 0.9017   (mean 0.7003, 10 x 60 s, floor applied)
after  0.767  0.767  0.767  0.783  0.681  0.470
                                          ^^^^^  one sample below the band. One.
```

### Gauge regime — panel 14, sustained HTTP load

| Rung | Consecutive samples outside | Max possible at a 60 s export cadence | Detected? |
|---|---|---|---|
| 30 s | **0** | 0 | no |
| 60 s | **1** | 1 | no |
| 120 s | **2** | 2 | **YES — the measured answer** |

`MinDetectableGaugeSeconds = 120`. Predicted 120. **AGREES** — and the agreement is not luck: the measured run equalled the theoretical maximum `floor(D/60)` at *every* rung, which is the export-cadence model holding exactly.

```
gauge 60 s   band 0..0   after  0 0 0 0 0 2      one sample, one short
gauge 120 s  band 0..0   after  0 0 0 0 3 5.5    two consecutive, detected
```

## Findings Worth Carrying Forward

**1. A QUEUED PIPELINE HIDES A SHORT OUTAGE FROM A RATE PANEL. This is the single most useful thing the ladder measured.** The trough model this phase carried from its research — `baseline x (1 - D/240)` — assumes the fault DESTROYS throughput for its duration. It does not. The broker buffers the work while `processor-sample` is at zero replicas, and the tier drains the backlog the moment it comes back, *inside the same 240 s rate window*. The counter is cumulative, so the window's total is largely restored and the rate barely moves. Measured ratio of observed dip to modelled dip, rung by rung: **0.004, 0.40, 0.66, 0.50, 0.74** — the model over-predicts at every single rung. A 30 s total outage of the processor tier produced a dip of **one tenth of one percent**: on the dashboard, it did not happen.

**2. The 120 s rung missed by exactly one sample, and that is a statement about the RULE as much as about the stack.** A 33 % dip is not subtle; the panel visibly dropped. What it did not do is stay outside its band for two consecutive 60 s samples, because the smearing spreads the trough across windows and only the deepest one clears. The two-consecutive rule is right — a single 60 s excursion is not distinguishable from a sampling artifact — but maintenance should know that it costs a factor of two in detectable duration here.

**3. Panel 4 is NOT the panel that carries the counter answer, and its numbers are noisy enough to mislead.** Its dip fractions across the five rungs read `0.344, 0.278, 0.125, 0.155, 0.471` — non-monotonic, and its largest reading is on the *shortest* rung. It is an `increase()` stat with a band roughly `37..99` wide, and 88-06 already measured that it EMPTIES rather than dipping. It did move at 240 s and 480 s (2 consecutive each, with NoData runs of 1 and 3), so it discriminates — but the rung-level `DipFraction` is deliberately taken from panel 3, and the artifact says so.

**4. The scored series flipped from `consumed` to `sent` at the 480 s rung.** The selection rule (longest run outside the band, tie-broken by the deepest excursion) picked `sent sample-proc-…` there and `consumed sample-proc-…` on the other four. Both series are recorded in full on every rung, so the choice is auditable rather than asserted — but a reader comparing rung-level numbers across the ladder must check which series each one names.

**5. The 480 s rung shows the full three-phase shape a maintenance reader will actually see.** `0.678, 0.711, 0.670, 0.502, 0.296, 0.183` and then four consecutive `NoData` — inside band, then below band, then blank. 88-06 predicted exactly three regimes per rung rather than two, and the longest rung is where all three appear in one window.

**6. The gauge regime's failure mode is arithmetic, not noise.** At 30 s the panel *cannot* be detected: `floor(30/60) = 0` samples can possibly fall inside the fault. At 60 s exactly one can, and exactly one did. `MaxPossibleConsecutiveSamples` is recorded on every gauge rung, so "the rung did not move" is a falsifiable statement about the export cadence rather than an inconclusive shrug.

**7. The three-hour ladder is 88 % waiting.** 930 s of actual fault across five counter rungs, and roughly 1110 s of mandatory settle before each rung's baseline window could even begin. That is the price of independence between rungs (T-88-24) and it is not compressible without giving up the claim that each rung is its own measurement.

## What The Roll-Up Says

```
CAPSTONE: 10/14 Proven, 4/14 AcceptedUnproven
13/14 panels are MISLEADING BY DEFAULT with a measured note
14/14 rows carry a discarded run; 6/14 carry a measured-unsatisfiable criterion; 1/14 carry an escalation
```

| Panel | Status | provenBy | Also |
|---|---|---|---|
| 1 Orchestrator consumed vs sent | Proven | SCALE-02 | the only row NOT flagged misleading |
| 2 Keeper consumed vs sent | Proven | ZERO-02 | guard + `rate()` blindness |
| 3 Processor consumed vs sent | Proven | SCALE-01 | corroborated by LADDER-01, 5 rungs |
| 4 Orchestrator sends minus pickups | Proven | SCALE-01 | corroborated by LADDER-01, 5 rungs; 2 unsatisfiable criteria |
| 5 Per-processor dispatch gap | Proven | SCALE-01 | empties without spiking |
| 6 Orchestrator unresolved steps | **AcceptedUnproven** | — | ZERO-01 dropped, both routes enumerated |
| 7 Processor dropped spawns | **AcceptedUnproven** | — | **user decision, not measurement** |
| 8 Keeper consumed − sent gap | **AcceptedUnproven** | — | measured-unreachable + the DISC-04 escalation |
| 9 Keeper L2 probe heartbeat | Proven | SCALE-03 | axis-zoom; ladder control ×1 |
| 10 WebApi request rate by route | Proven | WEB-01 | unlabelled `Value` row |
| 11 WebApi p95 duration | Proven | WEB-01 | leading-edge transient only |
| 12 WebApi status-code mix | Proven | WEB-01 | permanently-present series; ladder control ×1 |
| 13 WebApi 5xx ratio | **AcceptedUnproven** | — | WEB-02 dropped, seven endpoints enumerated |
| 14 WebApi in-flight + Kestrel | Proven | WEB-01 | corroborated by LADDER-01, 3 rungs |

Panel 1 is the single row not flagged misleading-by-default, and it says why in its own note. That one exception is what stops the flag from being a rubber stamp.

## Decisions Made

- **The after window is a pinned ABSOLUTE window read retrospectively.** The plan says "restore, then wait for the rate window to clear FULLY … before capturing the after-window", which only makes sense because every capture in this phase is an absolute window: the driver waits 240 s + 150 s past the fault's end so the whole window is stored and the next rung is not measured on this one's tail, and *then* reads the windows that span the fault. The same wait therefore serves both the export guarantee and rung independence, and it is recorded once as `IndependenceNote`.
- **A non-moving rung is a MEASUREMENT, not a Fail.** The scenario engine's rule exists to catch a panel that cannot discriminate under a fault that should have moved it; a ladder exists to find where detection stops, so applying that rule would have made three of five counter rungs "failures" of the very thing they were measuring. `Fail` is reserved for a stack that did not restore or a control that drifted, and `ScoringNote` states the distinction in the artifact.
- **The verdict is `Inconclusive` (exit 2) because of one finding, and that is the honest outcome.** The 240 s rung's measured dip diverges from the theoretical by 0.501 — more than a third — and the engine records that as a finding rather than adjusting the theory. Both regime answers are still measured from real rungs; the Inconclusive says the *model* was wrong, not that the measurement was.
- **Cross-talk was measured on the longest rung of each regime**, not on all eight, on the grounds that a fault's blast radius is monotonic in its duration: a control that held under a 480 s total tier outage held under the 30 s one. The reasoning is recorded on the artifact rather than left implicit, and panel 12 is deliberately *excluded* from the gauge controls with its reason stated.
- **The 180 s gauge fallback rung was implemented and NOT driven**, because the 120 s rung moved. The artifact records `GaugeFallbackDriven: false` and the declared rung lists, so "the fallback was not needed" and "the fallback was forgotten" cannot look alike.
- **The screenshot policy was applied as locked.** 230 captures on disk (14 MB, gitignored); **21 committed** — the last two sub-windows of every rung's asserted panel, so both the trough and the NoData tail are visible for each of the eight rungs.
- **Only DISC-07 is marked Complete.** It is measured, per regime, from rungs that were actually run, and this plan is the only one that could satisfy it. DISC-02's answer is now complete and machine-checkable — but REQUIREMENTS.md binds DISC-02 and HAND-04 together explicitly ("the two cannot drift apart"), and HAND-04's register is 88-09's deliverable.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] The ladder could not call a single one of the helpers the plan told it to reuse**

- **Found during:** Task 1, before any code was written
- **Issue:** `Invoke-Phase88Capture`, `Get-Phase88PanelReading`, `New-Phase88BandSet`, `Find-Phase88Band`, `Get-Phase88Excursion`, `Save-Phase88ScenarioArtifact` and the HTTP load driver were all defined INSIDE `if ($rowMode -eq 'scenario')`. PowerShell only sees a function once its definition statement has executed, so a helper defined in a branch that does not run does not exist — and the ladder mode dispatches before that branch. The plan's `read_first` names these as "the banding and capture code to reuse"; without a fix the only options were a second copy (two code paths measuring the same thing) or no reuse at all.
- **Fix:** All 374 lines moved verbatim into the shared HELPERS section above the mode dispatch, de-indented, with a pointer comment left at the old site. This is the same fix 88-05 deviation 6 applied to `Get-ProxyAggregateMean` for the same reason. `-Mode Scenario` and `-Mode Baseline` are behaviourally untouched.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `41c6609`

**2. [Rule 1 - Bug] The plan's `TheoreticalDipFraction` formula is a RESIDUAL, and the number it sits beside is a DROP**

- **Found during:** Task 1
- **Issue:** The plan instructs "record the measured `DipFraction` (the fractional drop from the band mean) alongside the `TheoreticalDipFraction` of `1 - min(D,240)/240`". But `1 - D/240` is the trough as a fraction of BASELINE — the residual — while the model's predicted DROP is `D/240`. Implementing the expression literally would have compared a drop against a residual, guaranteeing a large divergence at every rung and firing the plan's own "diverges by more than a third → record as a finding" clause on all eight, which would have buried the one rung where the divergence is real.
- **Fix:** `TheoreticalDipFraction` is `min(D, rateInterval) / rateInterval` — the same KIND of quantity as the measurement — and `TheoreticalTroughFraction` carries the plan's literal expression beside it. Both kinds are stated verbatim in `DipFractionKind` / `TheoreticalDipFractionKind`, and the divergence finding is raised only when the two are comparable. Nothing is lost: a reader can compute either from the other.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `41c6609`

**3. [Rule 2 - Missing critical functionality] `DipFraction` would have been null on the gauge rungs, which the plan's own acceptance criteria forbid**

- **Found during:** Task 1 (before the run, from the gauge rungs' expected band shape)
- **Issue:** The plan's task-2 verification throws if any rung's `DipFraction` is null. A fractional change from a band MEAN of zero is not a ratio — and every gauge rung's pre-rung band is captured on an idle stack, where `kestrel_active_connections` reads exactly 0 (measured: all three gauge rungs banded `0..0`). A panel that EMPTIES has no scored series at all, which is the same problem from the other side.
- **Fix:** The panel-level dip is resolved through three STATED bases, never a null: the fractional change where the band mean is non-zero; the binary Regime-B form (1.0 if the panel left an exactly-zero band, 0.0 if not) where the mean is zero, with `AbsoluteChange` carrying the size in the panel's own units; and 1.0 where the panel emptied, so "the panel went blank" is never confused with "the value did not move". `DipFractionBasis` names which applies on every row.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `41c6609`

**4. [Rule 2 - Missing critical functionality] The ladder had no cross-talk control, though its own row declares one and DISC-03 covers every scenario**

- **Found during:** Task 1
- **Issue:** The plan's ladder artifact schema has no cross-talk field, and the task list does not mention one — but the `LADDER-01` row declares `crossTalkPanels = @('9','12')`, DISC-03's text is "EVERY scenario carries a cross-talk control", and 88-07's traceability explicitly carried "LADDER-01 remains" forward for DISC-03. Shipping the last scenario of the phase with no blast-radius measurement would have left DISC-03 permanently short by one.
- **Fix:** Controls scored on the LONGEST rung of each regime, in the same batch and same windows as the claim, against that rung's own pre-rung band (a local reference, on the ZERO-01 precedent — the DISC-01 band was captured hours earlier under a different host load). Panel 12 is excluded from the GAUGE controls with its reason recorded: it is causally downstream of the very HTTP load a gauge rung drives, and WEB-01 measured its 400 and 404 series moving off an exactly-zero band under precisely that load. A control the fault is expected to move is not a control.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `41c6609`

**5. [Rule 1 - Bug] The roll-up aborted on the first artifact whose shape differed**

- **Found during:** Task 3 (smoke test against a synthetic ladder artifact, before the live one existed)
- **Issue:** Under `Set-StrictMode` a bare property access on an absent member throws. The nine artifacts this roll-up joins were written by four different plans as the engine grew, and their shapes legitimately differ: 88-06 changed `ReplicasBefore`/`After` from a map to a scalar and left a `ReplicasFieldNote` warning this very roll-up about it, the WEB-* cross-talk entries carry a panel-level `BandLow` while LADDER-01's carry theirs per series, and the older subject rows have no `ObservedDirection` at all. The roll-up died on the first mismatch with a message naming a property rather than a scenario.
- **Fix:** Every cross-artifact read goes through one defensive accessor that returns a stated default for an absent field. It is a READ, not a repair — nothing is invented, and an absent field stays absent in the row.
- **Files modified:** `scripts/phase-88-rollup.ps1`
- **Committed in:** `9e0c498`

**6. [Rule 2 - Missing critical functionality] The schema is exactly fourteen rows, so the things that must not be smoothed away had nowhere to live**

- **Found during:** Task 3
- **Issue:** The plan fixes the artifact as "a JSON ARRAY of exactly fourteen camelCase rows" — no top-level object. But three discarded runs, the acceptance criteria this phase could not satisfy as written, and the DISC-04 escalation all have to survive into this file, and a fifteenth element would break the schema every downstream reader is told to expect.
- **Fix:** They are carried ON the row they are about: `discardedRuns[]` (all three, each with the defect that forced it, on every panel whose evidence it touched), `measuredUnsatisfiableCriteria[]` (six rows — including DISC-04 on panel 8 and the panel-12 legend-gain criterion) and `escalations[]` (DISC-04, half met and half measured-unreachable). The array stays exactly fourteen elements and nothing is lost.
- **Files modified:** `scripts/phase-88-rollup.ps1`
- **Committed in:** `9e0c498`

---

**Total deviations:** 6 auto-fixed (2 bugs, 3 missing critical functionality, 1 blocking)
**Impact on plan:** One acceptance-criterion formula is **corrected for internal consistency** (deviation 2) with both quantities recorded so nothing is lost, and one prediction is **falsified by measurement** (the 120 s counter answer). No rung was re-run to chase a number, no scenario was substituted, no `src/` file was changed and no manifest was edited. Everything else is additive.

## Issues Encountered

- **The verdict is `Inconclusive` (exit 2), by design and for one reason:** the 240 s rung's measured dip diverges from the model by more than a third. Both regime answers are measured; the finding is about the theory.
- **The counter regime's answer is stack-specific in a way the runbook must state.** 240 s is what *this* pipeline gives, and the mechanism (broker buffering plus backlog drain) means the number would change with queue depth, consumer prefetch or a different fault class. The measured-versus-theoretical columns are in the artifact precisely so a future re-measurement can tell whether it disagrees because the stack changed or because a run was noisy.
- **Panel 4's rung numbers are noisy and non-monotonic.** They are recorded in full but the rung-level dip is taken from panel 3, and `DipFractionKind` says so.
- **The ladder took ~161 minutes for ~15.5 minutes of fault.** Anyone re-running it should budget three hours and launch it detached; the `-Rungs` filter and the carry-forward merge exist so a partial re-run is cheap, and the merge path is implemented but was **not exercised** by this run (the ladder completed in one part, `RunInParts: false`).
- **The gauge rungs' scored series is `in-flight` on the 30 s rung**, because no series moved and the tie-break falls to the first non-thin series. It carries no information there; the panel-level answer is `Moved: false` regardless and all three series' outcomes are in `SeriesResults[]`.
- **230 screenshots (14 MB) sit on disk, gitignored.** 21 are committed.

## Known Stubs

None. `-Mode DurationLadder` was the last unimplemented mode in the driver; all three modes are now real. The 180 s gauge fallback rung is implemented and conditional, not stubbed — it was not driven because the 120 s rung moved, and the artifact records that.

## Threat Flags

None. No product code, no network endpoint, no auth path, no schema change, no package install (T-88-SC holds). Every threat in the plan's register was exercised:

- **T-88-24** (rungs contaminating each other) — each rung re-captures its own pre-rung band over ten 60 s sub-windows and no rung's baseline window may BEGIN until the derived 240 s rate window plus a 150 s settle have elapsed past the previous fault. `RungStartUtc`/`RungEndUtc` and `IndependenceNote` are recorded per rung.
- **T-88-25** (fitting the theory to the measurement) — `DipFraction`, `TheoreticalDipFraction`, `TheoreticalTroughFraction`, `PredictionDivergence` and both `*Kind` strings sit side by side; the 240 s divergence is recorded as a FINDING and degrades the verdict rather than being reconciled. `PredictedCounterSeconds` 120 and `PredictedGaugeSeconds` 120 are recorded beside measurements of 240 and 120.
- **T-88-02** (replica restore across eight rungs) — `ReplicasRestored` is asserted PER RUNG from the sequencer's claim AND an independent live re-read of all four tiers; a failure aborts the ladder. All eight rungs true; `kubectl` reads 1/2/3/2 afterwards.
- **T-88-13** (`-Rungs` input validation) — validated against the static rung list before ANY cluster access; `-Rungs 999` exits 64, asserted by an actual invocation, as do `NOPE-99`, `ZERO-01` without the switch, a `-Mode` mismatch, and `-Rungs` on a non-ladder row.
- **T-88-26** (roll-up re-scoring) — `scripts/phase-88-rollup.ps1` reads and tabulates only; the cluster client's name is asserted absent from the file (and deliberately not written even in a comment); every row carries `sourceArtifact`; a `Proven` row must carry a `provenBy` and an `AcceptedUnproven` row without a reason exits 1.
- **T-88-27** (a missing proof presented as an absence) — the matrix is exactly fourteen rows. Panels 6, 7, 8 and 13 are rows with reasons of 1893, 921, 4538 and 1336 characters respectively.
- **T-88-03** (left-armed seam) — no seam was armed; `SeamVarsClean` true with an empty `SeamVarsFound`, re-verified by a direct env read on both seam-capable tiers.
- **T-88-18** (shallow serialisation) — `-Depth 10` on both artifacts with a post-write structural `System.Object[]` assertion; the shallower depth is asserted absent from the driver.
- **T-88-04 / T-88-05** — the pre-existing Grafana forward was detected, reused and left running; this plan started none.

## Requirement Traceability

| ID | What this plan contributed | Status |
|---|---|---|
| DISC-01 | Nothing new. The ladder re-captured a fresh band per rung rather than scoring against BASE-01, which is the DISC-06 discipline applied to a ladder. | Pending |
| DISC-02 | **The answer is now complete**: 14/14 panels accounted for in one machine-checkable artifact — 10 Proven with a `provenBy`, 4 AcceptedUnproven with reasons. Not marked, because REQUIREMENTS.md binds DISC-02 to HAND-04's register and 88-09 authors it. | Pending |
| DISC-03 | LADDER-01 now carries controls (3/3 held at 0.0000), so every scenario in the phase has a cross-talk measurement. Not marked: the ladder scores its controls against a LOCAL pre-rung band rather than the DISC-01 band, and on the longest rung of each regime rather than every rung — both deliberate and both stated, but neither is the requirement's literal text. | Pending |
| DISC-04 | Nothing new. The escalation from 88-07 is carried into the matrix as a row-level `escalations[]` entry on panel 8. | Pending — **escalated** |
| DISC-05 | Eight more faults driven through the runtime-only mechanism, stack asserted restored after every one. The statement is about every scenario and the register that grades it is 88-09's. | Pending |
| DISC-06 | Eight more re-baselines, one per rung, each with its own window bounds recorded. | Pending |
| DISC-07 | **MEASURED AND COMPLETE.** Both regime answers from real rungs (240 s counter, 120 s gauge), the Regime-B any-duration property stated, both research predictions recorded beside the measurements, and the disagreement recorded rather than smoothed. | **Complete** |
| HAND-01/02/03 | The matrix is the input all three read: per panel, the band, the observed states, the proving scenario, the status, and whether it misleads by default. | Pending |
| HAND-04 | Panels 6, 7, 8 and 13 each carry their full reason in the matrix, ready to be lifted into the register. | Pending |

## Next Phase Readiness

- **88-09 (HAND-02/HAND-03/HAND-04) has its input.** `analyzer-reports/phase-88-discrimination.json` is the fourteen-row join: `baselineBandSeries` + `baselineBand`, `observedStates` with per-observation records, `provenBy`/`status`/`reason`, `misleadingByDefault`, `crossTalkAppearances`, `ladderRungs` and `sourceArtifact` on every row. Regenerate it any time with `pwsh -File scripts/phase-88-rollup.ps1` — it reads only.
- **Three things 88-09 must NOT let the register lose.** (a) DISC-04 is half met and half measured-unreachable — it is on panel 8's row as an `escalations[]` entry and must be recorded as an escalation, not left Pending in silence. (b) Panel 7 is accepted-unproven **by user decision**, not by measurement, and its row says so in the first sentence. (c) Panels 6 and 13 were DROPPED BY MEASUREMENT after enumerated probing — their reasons name the routes and the observed status codes.
- **Input for 88-10/88-11 (runbook) — three new rows' worth.** A processor-tier outage shorter than **four minutes** does not register on panel 3 at all; the operator's threshold for "the dashboard would have told me" is 240 s, not the 120 s the research assumed. A WebApi condition shorter than **two minutes** cannot register on panel 14 by arithmetic. And for the guarded Regime-B panels the story inverts: a single event is visible at any duration — which is the one place this dashboard is *better* than an operator would guess.
- **Operational note.** The ladder is ~3 hours for ~15 minutes of fault and must be launched detached. Its `-Rungs` filter plus carry-forward merge make a partial re-run cheap, but that path has **not** been exercised live — the first plan to need it should expect to shake it out.

## Self-Check: PASSED

- `analyzer-reports/phase-88-LADDER-01.json` — FOUND (parses; `Verdict: Inconclusive`; all 5 counter rungs and all 3 gauge rungs present; every rung carries `BaselineBand`, `AfterValues`, `DipFraction`, `TheoreticalDipFraction` and `ReplicasRestored` true; `MinDetectableCounterSeconds` 240 and `MinDetectableGaugeSeconds` 120 are both rungs that were actually run; `RegimeBDetectionNote` non-empty; `PredictedCounterSeconds`/`PredictedGaugeSeconds` both 120; `SeamVarsClean`/`ReplicasRestored`/`ImagesUnchanged` all true; no `System.Object[]` value) — the plan's own verification prints **"DISC-07 measured"**, including its independent live `kubectl` read of 1/2/3/2
- `analyzer-reports/phase-88-discrimination.json` — FOUND (parses as an array of exactly 14 elements; every row has a non-null `panelId`/`panelTitle`/`regime`/`baselineBand`/`baselineBandSeries`/`observedStates`/`status`/`sourceArtifact`; every title byte-matches `k8s/dashboards/business.json`; every `baselineBandSeries.Count` equals the BASE-01 entry count for that panel and equals `seriesCount`; every envelope is the min/max across its series with `floorApplied` the OR; panel 7 is `AcceptedUnproven` naming `processor_spawn_dropped_total`; panels 2/4/5/6/7/8/9/13 all carry a misleading-by-default note) — the plan's verification prints **"discrimination matrix: 10/14 Proven"** and **"roll-up ok"**
- `scripts/phase-88-rollup.ps1` — FOUND (parses via `[ScriptBlock]::Create`; exits 0; the cluster client's name is absent)
- `scripts/phase-88-panel-discriminate.ps1` — FOUND (parses; all six ladder tokens present; forbidden-token gate clean including `TierReplicas`, `apply -k`, `-Depth 5`, `now-1h`, `0.0.0.0`, `scale statefulset`; `-Rungs 999`, `NOPE-99`, `ZERO-01`, a `-Mode` mismatch and `-Rungs` on a non-ladder row each exit 64)
- `analyzer-reports/phase-88-screenshots/LADDER-01/` — FOUND (230 PNGs; 21 committed)
- Commits `41c6609`, `789575c`, `9e0c498` — all FOUND
- `pwsh -File scripts/phase-87-dashboard-lint.ps1` — exit 0 (no dashboard JSON was touched)
- Live stack re-verified after the run: replicas 1/2/3/2, all four images `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY` on `keeper` and `processor-sample`, the pre-existing Grafana forward left running and none started

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-29*
