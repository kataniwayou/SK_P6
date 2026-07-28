---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 04
subsystem: testing
tags: [grafana, playwright, prometheus, powershell, kubernetes, baseline, banding, disc-01, wave-3]

# Dependency graph
requires:
  - phase: 88-01
    provides: "the batch Grafana panel DOM reader, Get-PinnedWindowSeries / Invoke-PanelReadBatch / Get-PanelBand, and the four-state read outcome model"
  - phase: 88-02
    provides: "the runtime-only cluster-ops library — Get-LiveReplicas, Invoke-Phase88Ctl, Assert-StackRestored and the 60/61/62/65 failure-code vocabulary"
  - phase: 88-03
    provides: "the seven measured probe answers and 88-PROBE-DECISIONS.md — the locked LocatorMode, the pinned rate interval, the Dropped status of WEB-02/ZERO-01, and the BLOCKING Record-3 reader amendment"
  - phase: 87-grafana-observability-dashboards
    provides: "the harness frame, the derived $__rate_interval arithmetic, and the Fail-beats-Inconclusive verdict precedence"
provides:
  - "the four Record-3 reader amendments, applied and re-provable hermetically (PANEL_READ_SELFTEST=1)"
  - "positional per-series reads: Get-PanelSamples -SeriesIndex, Get-PanelSeriesCount, Get-PanelSeriesNameAt"
  - "scripts/phase-88-panel-discriminate.ps1 — the static scenario table all ten rows live in, the PRE gate, and -Mode Baseline"
  - "analyzer-reports/phase-88-BASE-01.json — the DISC-01 healthy band for all fourteen panels, 42 bands, recomputable from the artifact"
  - "the measured fact that panel 4 cannot be banded at a 60 s sub-window, and the stated wider window that bands it"
  - "a diagnostic proxy cross-check with teeth: 13/14 panels comparable, all comparable ones agreeing"
affects: [88-05, 88-06, 88-07, 88-08, 88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "a parser amendment proven by a checked-in hermetic self-test against VERBATIM live-recorded input, so 'the fix landed' is re-runnable rather than asserted"
    - "positional (row-index) series selection with the name RECORDED beside the band rather than used as the selector"
    - "the scenario id selects a static table row and nothing else; Dropped scenarios stay visible as rows carrying their reason"
    - "band entries state their own SubWindowSeconds, so bands taken at different widths can never be silently compared"
    - "a structural (JSON-value) depth guard rather than a substring search, so the guard cannot trip on its own diagnostic text"

key-files:
  created:
    - scripts/phase-88-panel-discriminate.ps1
    - analyzer-reports/phase-88-BASE-01.json
    - analyzer-reports/phase-88-BASE-01-run1-discarded.json
    - analyzer-reports/phase-88-screenshots/BASE-01/ (282 PNGs)
  modified:
    - scripts/phase-88-panel-read.js
    - scripts/lib/phase-88-panel-read.ps1

key-decisions:
  - "The Record-3 reader amendment landed BEFORE the baseline and is proven by a 13-case hermetic self-test against the probe's own verbatim innerText — LegendNamesBound came back true live"
  - "Per-series reads are POSITIONAL; the series name is recorded beside each band as corroboration, never used as the selector"
  - "Panel 4 CANNOT be banded at 60 s — increase() needs >= 2 samples and the stored resolution is 60 s — so it is banded over five 120 s sub-windows inside the same settled window, with every entry stating its own width"
  - "Verdict Inconclusive is the honest outcome: 13 per-series entries band at exactly zero and are recorded as findings, because a Class-A series with no live signal is not a valid null hypothesis"
  - "Run 1 was discarded for two driver DEFECTS (not for a better number) and BOTH artifacts are committed"
  - "The T-88-18 depth guard is structural, not a substring search — it tripped on its own captured error message in run 1"
  - "-Mode is a CONFIRMATION that must match the row, never a selector: the row stays authoritative about what it is"
  - "No requirement is marked Complete: DISC-01 is proven, but its statement is graded against the phase register that 88-09 authors"

patterns-established:
  - "A single-letter typed parameter is a BUG in PowerShell, not a style choice: case-insensitive names make [long]$S and foreach($s) the same variable, and the type constraint then throws on the loop assignment"
  - "A Class-A panel or series that bands at exactly zero is recorded as a FINDING, not as a band"
  - "Screenshots are evidence, never a verdict; the band stays recomputable from Values[]/States[] without them"

# This plan's `requirements` frontmatter names DISC-01 and DISC-03. NEITHER is marked complete —
# see Decisions. The baseline exists and the cross-talk rule is enforced in code, but both statements
# are graded against the phase-wide register that 88-09 authors and the scenarios 88-05..88-08 run.
requirements-completed: []

# Metrics
duration: 115min
completed: 2026-07-28
---

# Phase 88 Plan 04: DISC-01 Baseline Capture Summary

**Every one of the fourteen business panels now has a recorded healthy band — 42 of them — captured from a settled stack over ten abutting 60-second absolute windows at a pinned viewport and read from the rendered panel; and the reader defect that would have made the whole capture structurally blind was fixed, and proven fixed, before a single sample was taken.**

## Performance

- **Duration:** ~115 min (of which two live captures are ~2 x 20 min)
- **Completed:** 2026-07-28
- **Tasks:** 3 of 3, plus the blocking Record-3 prerequisite
- **Files modified:** 6 tracked paths (4 created, 2 modified), plus 282 screenshots

## Accomplishments

- **Roadmap success criterion 1 is satisfied.** "It moved" is now falsifiable: each band is a numeric interval plus the regime it belongs to, and the artifact carries `Values[]`, `States[]`, the window bounds, the sub-window width and count, the viewport and the derived rate interval — so a reader can recompute every band rather than trust it.
- **The blocking reader defect was fixed and PROVEN fixed before the baseline.** `LegendNamesBound: true` in the live artifact, and panel 2's rows now read `consumed` / `sent` instead of the empty strings the wave-0 probe measured. Had this been skipped, the baseline would have been captured on a reader that cannot see the one signal ZERO-02 exists to assert — and the reader's own `TimeseriesLegendParsed` flag would have read green throughout.
- **All five Regime-B panels banded at exactly 0..0**, with `RegimeBNonZeroBands` empty. That is the strongest discrimination the phase has: the null hypothesis is exactly zero and a single event falsifies it.
- **Panel 9's band is floor-bound as predicted** — `0.1800..0.2200` around a mean of `0.2000`, `FloorApplied: true`. Its unfloored 3σ would have been roughly 0.0006, a band any trivial perturbation would breach.
- **The diagnostic cross-check has real teeth.** 13 of 14 panels comparable and every comparable one AGREES with its rendered value: panel 1 proxy `2.0549` vs rendered `2.0528`, panel 3 `1.4699` vs `1.4672`, panel 9 `0.40006` vs `0.40000`. This is the direct control against the Phase-87 defect where 30/30 Class-A checks reported green against three panels rendering "No data" — here, if the panel had been blank, the proxy would have disagreed loudly.
- **The stack was left exactly as found.** `SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` all true; independently re-verified afterwards at 1/2/3/2 on `:tags-const-1544` with zero `DEFEAT`/`REINJECT_DELAY`. The pre-existing Grafana forward was reused, not duplicated, and not torn down.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 0 | Record-3 reader amendments (blocking prerequisite) | `82d8324` | fix |
| 1 | Driver frame, static scenario table, unknown-id guard | `8b39a54` | feat |
| 2 | `-Mode Baseline` — capture, banding, artifact writer | `66f3760` | feat |
| 3 | Live DISC-01 baseline capture | `53e5dd5` | feat |
| 3b | BASE-01 screenshot evidence | `ac02cc9` | chore |

## The Blocking Prerequisite: Four Amendments, Proven Not Asserted

88-03 Record 3 measured that legend **values** parsed while legend **names** bound to nothing, and that the `data-testid VizLegend series` recovery path 88-01 designed matches **zero** elements on this render. All four amendment points landed before any capture:

1. **Parser.** `innerText` puts each series name on its own line and the numeric cells on the line that follows. The old parser required ≥ 2 cells on one line, so it discarded every name line as noise. A lone non-numeric line is now held as a pending name and paired with the next values line whose first cell is blank — and a pending name binds to exactly **one** row, so a stray line cannot mislabel every row after it.
2. **Positional reads.** `Get-PanelSamples -SeriesIndex`, plus new `Get-PanelSeriesCount` and `Get-PanelSeriesNameAt`. Row index is the reliable axis; the name is recorded beside each band.
3. **The testid route is treated as ABSENT**, not as a fallback. The code is kept (harmless, version-dependent) and a new `legendTestIdAvailable` field says whether it produced anything.
4. **Assertion order.** Panel 2 asserts numerically first (Regime B, 0..0), legend suffix as corroboration. Per PQ-04 the trailing-space signal does not survive `innerText` at all — only `consumed` → `consumed keeper` is observable.

`PANEL_READ_SELFTEST=1 node scripts/phase-88-panel-read.js` runs **13 hermetic cases** against the verbatim `innerText` the probe recorded live, launches no browser, and passes 13/13. The fix is re-runnable at any time rather than being a claim in a summary.

## The Baseline

| | |
|---|---|
| Verdict | **Inconclusive** (exit 2) — the honest outcome, see below |
| Bands | **42** across **14/14** panels |
| Window | `2026-07-28T17:41:52Z` → `17:51:52Z`, exactly 600 s |
| Sub-windows | 10 × 60 s, abutting, absolute |
| Viewport | 1920 × 1080, `LocatorMode: viewpanel` |
| Rate interval | 240 s, **derived** from the live `timeInterval` of 60 s |
| Evidentiary horizon | oldest sample `2026-07-19T07:32:37Z` — confirms the corrected ~9.4 days, not the research's ~12 h |
| Panel 4 wide level | `2.11` over 1 h — recorded for HAND-02, **never scored** |

**Inconclusive is correct, not a shortfall.** Thirteen per-**series** entries on panels 10, 12 and 14 band at exactly zero — routes never called, status codes never produced, gauges idle. A Class-A series with no live signal has nothing to move away from, so each is recorded as a finding rather than banded. Every one of the fourteen **panels** itself carries a band.

## Decisions Made

- **Run 1 was discarded for defects, not for a better number, and both artifacts are committed.** The plan is explicit that a silently re-rolled baseline is unfalsifiable. `analyzer-reports/phase-88-BASE-01-run1-discarded.json` is in the repo beside the kept one, and the two defects that forced the re-run are named in the commit message and below.
- **No requirement is marked Complete.** DISC-01's band now genuinely exists — but its statement, and DISC-03's cross-talk rule, are graded against the phase-wide register 88-09 authors and the scenarios 88-05..88-08 run. DISC-03 is *enforced in code* (a `scenario` row with an empty cross-talk list aborts at startup) but no scenario has yet run under it. This continues the discipline 88-01 (deviation 3), 88-02 and 88-03 all held.
- **`-Mode` is a confirmation, never a selector.** A row declares its own mode and the parameter may only agree with it, so the "the id selects a row and nothing else" invariant covers the mode too.
- **Dropped scenarios stay in the table.** WEB-02 and ZERO-01 are rows carrying their verbatim reasons and the probe field that dropped them, refused with exit 64. Deleting them would show the roll-up an absence where it must show a row.
- **The host load drives read-only 2xx routes only.** The two confirmed inert non-2xx drivers (404, 400) are deliberately left to WEB-01, so that scenario's status-mix movement is an unambiguous new signal rather than an increase in a rate this baseline already contained.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] The reader amendment was a prerequisite the plan's task list did not contain**

- **Found during:** Task 0 (before Task 1)
- **Issue:** 88-03 Record 3 mandates four reader amendments *before* any baseline, but the plan's three tasks do not include them. A baseline captured on the unamended reader would be structurally blind to panel 2's discrimination signal, and every downstream scenario would inherit that blindness.
- **Fix:** Applied all four points to `scripts/phase-88-panel-read.js` and `scripts/lib/phase-88-panel-read.ps1`, with a 13-case hermetic self-test asserting them against the probe's verbatim recorded `innerText`.
- **Committed in:** `82d8324`

**2. [Rule 3 - Blocking] A bare top-level `require('playwright')` made the self-test unrunnable and hid a failure mode**

- **Found during:** Task 0
- **Issue:** `run.js` copies the reader into the skill directory before requiring it, so `playwright` resolves there — but the hermetic self-test must run straight from the repo, where it does not. Worse, a top-level require that throws kills the process before **any** sentinel line is printed, which the caller can only report as "the reader produced no sentinel line at all".
- **Fix:** Lazy, defensive require. A missing module is now a **named** batch-end error instead of a silent no-output crash.
- **Committed in:** `82d8324`

**3. [Rule 1 - Bug] A single-letter typed parameter silently destroyed the entire diagnostic cross-check**

- **Found during:** Task 3 (run 1)
- **Issue:** `Get-ProxyAggregateMean` took `[long]$S` while looping `foreach ($s in @($res.data.result))`. PowerShell variable names are **case-insensitive**, so the loop re-assigned the typed parameter, whose type constraint is re-enforced on assignment — every call threw `Cannot convert "@{metric=; values=System.Object[]}" ... to "System.Int64"`. Run 1's artifact contains **no** proxy value at all, which means the T-88-12 control was absent from precisely the artifact it was built to protect. This is the same trap 88-03 deviation 5 recorded, in a form where the type constraint made it fail loudly instead of silently.
- **Fix:** Parameters renamed to `$PromQuery` / `$StartUnix` / `$EndUnix` / `$StepSeconds`, with a comment stating that a single-letter name here is a bug rather than a style choice. Reproduced the trap in isolation first to confirm the mechanism.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `53e5dd5`

**4. [Rule 1 - Bug] The T-88-18 depth guard tripped on its own diagnostic text**

- **Found during:** Task 3 (run 1)
- **Issue:** The guard searched for the substring `System.Object[]` anywhere in the written file. Deviation 3's exception message *mentioned* that type, so the guard aborted the run with exit 66 claiming a serialisation truncation that had not happened. A guard that cries wolf on its own error messages eventually gets disabled, and then the real truncation it exists to catch ships.
- **Fix:** The assertion is now **structural** — it matches a JSON *value* equal to that string (`: "System.Object[]"` or a bare array element), which is the actual signature of a nested array serialised as its type name. The captured message is additionally sanitised. The guard keeps its teeth: `-Depth 10` remains, and `-Depth 5` is still asserted absent.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `53e5dd5`

**5. [Rule 2 - Missing critical functionality] Panel 4 cannot be banded at the plan's 60-second sub-window**

- **Found during:** Task 3 (run 1) — a **measurement that corrects the plan**
- **Issue:** The plan's `must_haves` state that "Panel 4 is banded on its per-minute increase at the fixed sub-window". Measured: panel 4 renders **"No data"** at 60 s. The reason is structural, not incidental — panel 4 is `increase(counter[$__range])`, `increase()` needs at least **two** samples inside its range, and the stored resolution on this stack is 60 s (the SDK export cadence, not the 15 s scrape interval), so a 60 s window contains exactly one. Without a fix, panel 4 would have had **no band at all** and SCALE-01's `slope-up` prediction would have had nothing to test against.
- **Fix:** Panel 4 is banded over **five 120 s sub-windows inside the same settled 600 s window** — equal width within its own batch (the reader refuses anything else), absolute, same viewport, entirely inside the traffic window. Every band entry now states its own `SubWindowSeconds`, so a 120 s band can never be silently compared against a 60 s one, and `Panel4SubWindowNote` records the mechanism. The 600 s / 10 × 60 s contract for the other thirteen panels is unchanged. Live result: band `37.41..98.59`, mean `68.00`, n=5.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `53e5dd5`

**6. [Rule 2 - Missing critical functionality] The cross-check excluded the four panels it matters most for**

- **Found during:** Task 3 (run 1)
- **Issue:** The proxy query covered only each panel's **first** target, while the rendered side summed **all** its banded series. For the four multi-target conservation panels (1, 2, 3, 14) that guaranteed a mismatch, so they were marked non-comparable and the T-88-12 control simply did not apply to them.
- **Fix:** Every target is queried and the aggregates summed. Comparability went from 9/14 to **13/14**, and all thirteen agree. Panel 11 remains excluded with a stated reason (`histogram_quantile` is a per-series quantile that cannot be meaningfully summed) rather than asserting a false agreement.
- **Files modified:** `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `53e5dd5`

---

**Total deviations:** 6 auto-fixed (3 bugs, 2 missing critical functionality, 1 blocking prerequisite + 1 blocking)
**Impact on plan:** One `must_have` is **corrected by measurement** (panel 4's 60 s band does not exist; it is banded at a stated 120 s instead). Everything else is additive — `SubWindowSeconds` per band entry, `Panel4*` fields, `TargetCount`/`Queries` on the diagnostic entries, `LegendNamesBound`, `RegimeBNonZeroBands`. No interface was removed or renamed.

## Issues Encountered

- **Verdict is Inconclusive, and it should be.** Thirteen per-series entries band at exactly zero. This is not a defect in the capture — it is the capture correctly refusing to call a zero band a null hypothesis.
- **Panel 11's band is very wide and partly negative** (e.g. `-5.06..21.83` around a mean of `8.38`). p95 latency under a light synthetic load is genuinely noisy, and one series (`Workflows`) yielded only **2 of 10** samples. Panel 11 is nominally a WEB-01 subject with a predicted direction of `up`; on this band, a latency increase would have to be large to clear it. **88-05 should expect panel 11 to be the weakest of its four subjects** and should not read a non-move there as a defect without checking the sample count first.
- **New series shift row indices.** Panel 12 banded series `200/201/204/400/404/422`; when WEB-01 drives its 404 and 400, series that are currently all-zero will carry values. Because names now bind, later plans should match by **name** and fall back to index — both are recorded in every band entry.
- **282 screenshots is ~15 MB for one capture.** Committed for consistency with the 88-03 precedent, but ZERO-02 and ZERO-03 each owe a re-baseline and the ladder adds a capture per rung, so this would exceed 100 MB across 88-05..88-08. Flagged in `ac02cc9` as an explicit decision 88-05 should make rather than inherit.
- **The pre-existing Grafana forward was reused, not replaced.** Both runs detected loopback 3000 already carrying traffic, started nothing, and tore nothing down.
- **The fan-out workflow is left active**, as the wave-0 probe left it. Recorded in the artifact as `TrafficLeftActive: true` rather than being a hidden state change.

## Known Stubs

None in what this plan owns. `-Mode Scenario` and `-Mode DurationLadder` exit 64 naming the plan that authors them (88-05..88-08) — that is the plan's own division of work (`88-04` provides "the frame, scenario table, PRE gate, `-Mode Baseline`, artifact writer"), not a deferral of this plan's scope. The header-walk locator remains a real implemented second path that `viewpanel` did not need.

## Threat Flags

None. No product code, no network endpoint, no auth path, no schema change, no package install (T-88-SC holds). Every threat in the plan's register was exercised:

- **T-88-13** — `NOPE-99`, `WEB-02`, `ZERO-01` and a `-Mode` mismatch each exit 64 before any cluster access; no tier, deployment, env var or panel id is derived from the parameter.
- **T-88-12** — 13/14 panels cross-checked against the proxy, all agreeing; the rendered panel remained the verdict throughout.
- **T-88-17** — the artifact carries `Values[]`, `States[]`, window bounds, sub-window width **and count per band**, viewport and derived rate interval; the discarded run is committed, not erased.
- **T-88-18** — `-Depth 10` present, `-Depth 5` absent, and the guard is now structural so it detects real truncation without false-positiving on diagnostic text.
- **T-88-06** — both `psql -c` calls are single-line static double-quoted literals with no `$`.
- **T-88-04 / T-88-05** — `--address 127.0.0.1` present, `0.0.0.0` absent, the shared forward PID registry absent; the reused forward was left untouched.
- **T-88-02 / T-88-10** — `TierReplicas` and `apply -k` both asserted absent; replicas re-read at 1/2/3/2 and images unchanged after the run.

## User Setup Required

None. `kubectl`, the `docker-desktop` context, `node`, the playwright skill and the pre-existing Grafana forward were all already in place.

## Next Phase Readiness

- **Ready for 88-05 (WEB-01).** Panels 10, 11, 12 and 14 all have bands, and the cross-talk controls (1, 3, 9) are banded too. Two cautions: panel 11's band is wide with one 2-sample series, and panel 12's currently-zero `404`/`400` series will gain values — match series by name first.
- **Ready for 88-06 (SCALE-01/02/03).** Panels 1, 3, 5 and 9 have solid non-zero bands. **Panel 4 is banded at 120 s, not 60 s** — SCALE-01 and LADDER-01 must capture panel 4 at the same 120 s width or its band is meaningless.
- **Ready for 88-07 (ZERO-02, ZERO-03).** Panels 2 and 8 both band at exactly 0..0, so a single event falsifies either. Legend names now bind, so panel 2's `consumed` → `consumed keeper` suffix signal is finally readable as corroboration. Both rows already carry their re-baseline obligation and ZERO-03 carries both seams.
- **Ready for 88-08 (LADDER-01).** `RateIntervalSeconds: 240` is fixed and recorded; no rung re-derives it.
- **Input ready for 88-09.** The HAND-04 register gains, beyond panels 6/7/13, the thirteen zero-signal series and the panel-4 window finding. The HAND-02 misleading-by-default list has its first concrete entry: panel 4's operator-visible level of `2.11`, growing on a demonstrably healthy stack.
- **Concern to carry forward.** Research assumption A6 is still unretired — nothing here tests whether the deployed keeper image *honours* `KEEPER_DEFEAT_REINJECT`. Panel 8's clean 0..0 band means ZERO-03 will have a sharp test, but a flat panel 8 remains an open question about the image before it is one about the mechanism.

## Self-Check: PASSED

- `scripts/phase-88-panel-discriminate.ps1` — FOUND (parses; NOPE-99/WEB-02/ZERO-01/mode-mismatch all exit 64; all ten rows and fourteen verbatim titles present; forbidden-token gate clean)
- `scripts/phase-88-panel-read.js` — FOUND (`node --check` clean; `PANEL_READ_SELFTEST=1` → 13/13 pass)
- `scripts/lib/phase-88-panel-read.ps1` — FOUND (positional-read checks pass hermetically)
- `analyzer-reports/phase-88-BASE-01.json` — FOUND (parses; 42 bands, 14/14 panels; 600 s window; 1920×1080; Regime-B all 0..0; panel 9 floor-bound; wide level present; no `System.Object[]` value)
- `analyzer-reports/phase-88-BASE-01-run1-discarded.json` — FOUND (the discarded run, kept deliberately)
- Commit `82d8324` — FOUND
- Commit `8b39a54` — FOUND
- Commit `66f3760` — FOUND
- Commit `53e5dd5` — FOUND
- Commit `ac02cc9` — FOUND
- `pwsh -File scripts/phase-87-dashboard-lint.ps1` — exit 0 (no dashboard JSON was touched)
- Live stack re-verified after the run: replicas 1/2/3/2, images all `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY`

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-28*
