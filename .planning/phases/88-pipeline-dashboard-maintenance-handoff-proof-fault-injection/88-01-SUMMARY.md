---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 01
subsystem: testing
tags: [grafana, playwright, node, powershell, prometheus, observability, dom-assertion]

# Dependency graph
requires:
  - phase: 87-grafana-observability-dashboards
    provides: "the provisioned skp-business dashboard (uid skp-business), its table-legend/stat panel shapes, the Get-PropertyNames StrictMode helper, and the Fail-beats-Inconclusive verdict precedence"
provides:
  - "DISC-01..07 + HAND-01..04 registered in .planning/REQUIREMENTS.md with traceability rows"
  - "scripts/phase-88-panel-read.js — batch Grafana panel DOM reader, N panels x M pinned windows in ONE browser session, sentinel-delimited stdout"
  - "scripts/lib/phase-88-panel-read.ps1 — invocation contract, pinned-window minting, baseline banding, and the did-it-move rule"
  - "the phase-wide SAMPLE/CAPTURE vocabulary and the four-state read outcome model (Ok/Truncated/Unreadable/ReaderMissing)"
affects: [88-02, 88-03, 88-04, 88-05, 88-06, 88-07, 88-08, 88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "sentinel-delimited stdout (##PANEL-JSON## / ##PANEL-BATCH-END##) for any node tool invoked through playwright-skill's run.js"
    - "batch-end completeness marker so a truncated capture is Inconclusive rather than silently banded as complete"
    - "absolute pinned equal-width windows + fixed viewport as a precondition of any before/after panel comparison"

key-files:
  created:
    - scripts/phase-88-panel-read.js
    - scripts/lib/phase-88-panel-read.ps1
  modified:
    - .planning/REQUIREMENTS.md

key-decisions:
  - "The reader is a COMPLETE standalone script (own require + async IIFE) so run.js executes it unmodified instead of wrapping it"
  - "One browser session for the whole cross product: a 14-panel x 10-window baseline is 140 reads, and a process launch per read would dominate the run and drift the capture's wall clock"
  - "Unequal-width windows are refused outright (BATCH-END with an error, exit 1) rather than read — the legend Mean is computed over the visible range, so unequal windows are not comparable"
  - "A single failed read never aborts the batch; that pair is emitted as panelState Error and the shortfall shows up in the BATCH-END counts"
  - "Series names are carried verbatim and cross-checked against the VizLegend testid attribute, because panel 2's discrimination signal IS a trailing-space legend name change ('consumed ' -> 'consumed keeper')"
  - "Get-PanelBand returns exactly 0..0 for an all-zero baseline with FloorApplied false — Regime B's null hypothesis is exactly zero and one event falsifies it"
  - "The three helper functions return their arrays UNWRAPPED (no `return ,$array` comma trick) so the repo's standard @()-wrap idiom counts elements, not one nested array"
  - "Invoke-PanelReadBatch never throws: ReaderMissing / Unreadable / Truncated are Inconclusive material, never a Fail"

patterns-established:
  - "Sentinel extraction pattern: select lines matching ^##PANEL-JSON##, ConvertFrom-Json each INDIVIDUALLY, never the whole run.js-banner-polluted capture"
  - "Env contract cleared in a finally so a later batch cannot inherit a stale WINDOWS value and silently read the wrong time range"
  - "NoData modelled as a first-class panel STATE with its own movement direction, not as a read failure"

# Copied from this plan's `requirements` frontmatter — these are the ids this plan SERVES, i.e. the
# ones whose registration and reading mechanism it delivers. NONE of them is PROVEN yet: their
# traceability rows in .planning/REQUIREMENTS.md deliberately remain `Pending` until the later plans
# capture the baselines (88-04), drive the faults (88-05..88-08), and write the runbook/verdict
# (88-09..88-11). See deviation 3.
requirements-completed: [DISC-01, DISC-02, DISC-03, DISC-07, HAND-01, HAND-02, HAND-03, HAND-04]

# Metrics
duration: 35min
completed: 2026-07-28
---

# Phase 88 Plan 01: Panel Reader + Requirement Family Summary

**A checked-in Playwright batch reader that turns rendered Grafana panels into numbers (one sentinel line per panel x pinned-window pair, single browser session), plus the PowerShell contract that bands them and decides "did it move" — and the DISC-*/HAND-* requirement family every later Phase-88 plan cites.**

## Performance

- **Duration:** ~35 min
- **Started:** 2026-07-28 (session)
- **Completed:** 2026-07-28
- **Tasks:** 3 of 3
- **Files modified:** 3 (2 created, 1 modified)

## Accomplishments

- **The phase is now mechanically possible.** Before this plan the repo contained zero `.js` files and no script invoked `node`; nothing here could read a rendered panel, so every Phase-88 assertion would have degraded into "proving the query instead of the panel" — the exact Phase-87 defect (`87-FINDINGS.md` §12) this phase exists to avoid.
- **Eleven requirements registered and traceable.** DISC-01..07 and HAND-01..04 exist as statements and as `| ID | 88 | Pending |` rows, with the roadmap SC1..SC9 mapping recorded inline and an explicit note that VER-01's two-class rule is a *prerequisite* of this phase, not a deliverable of it. All 16 Phase-87 rows verified untouched (diff is insertions-only: 32 added, 0 deleted).
- **Batch reading, not per-read process launches.** The reader takes a cross product of panel ids x equal-width absolute windows and reads all of it window-major in one browser context, emitting `##PANEL-JSON##` per pair and exactly one trailing `##PANEL-BATCH-END## {"requested":N,"emitted":M}`.
- **Four failure modes are now nameable instead of silent.** `Ok` / `Truncated` / `Unreadable` / `ReaderMissing` from `Invoke-PanelReadBatch`, plus `Rendered` / `NoData` / `Error` per panel. A truncated batch can no longer be banded as if it were complete, and a panel that could not be read is distinguishable from a panel that read a value and did not move.
- **The statistical rules are hermetically proven** before any live run: abutting 60 s windows, mean ± 3σ with a ±10 % floor, an exactly-0..0 band for a zero baseline, and a movement rule that rejects a single-sample excursion (`@(0,1,0)` does not count as moved) while accepting two consecutive (`@(0,1,1)` does).

## Task Commits

1. **Task 1: Register the DISC-* / HAND-* requirement family** — `b59698d` (docs)
2. **Task 2: Author scripts/phase-88-panel-read.js** — `3cc431d` (feat)
3. **Task 3: Author scripts/lib/phase-88-panel-read.ps1** — `1ab7113` (feat)

## Files Created/Modified

- `.planning/REQUIREMENTS.md` — added the `### DISC — Panel Discrimination — Phase 88` and `### HAND — Handoff Readiness — Phase 88` family blocks (statements copied verbatim from `88-RESEARCH.md:100-110`, with the "proposed beyond the criteria" parentheticals on HAND-03/04 converted to trailing supporting-criterion prose), the SC1..SC9 mapping line, and eleven `Pending` traceability rows.
- `scripts/phase-88-panel-read.js` — the batch DOM reader. Env contract (`PANEL_IDS`, `WINDOWS`, `VIEWPORT_W/H`, `LOCATOR_MODE`, `PANEL_TITLES`, `SCREENSHOT_DIR`, `GRAFANA_BASIC_AUTH`, `DASHBOARD_UID`, `GRAFANA_URL`); URL built as `?viewPanel=panel-<id>&from=<ms>&to=<ms>&var-source=All&var-pod=All&kiosk&refresh=`; viewport pinned before the first navigation; loading-bar detach + bounded non-empty-text wait instead of sleeping; `data-testid` selectors only; stat vs table-legend parsing; screenshots as evidence only.
- `scripts/lib/phase-88-panel-read.ps1` — `Get-PropertyNames` (verbatim from `phase-87-dashboards-verify.ps1:244`), `Resolve-PanelReaderSkillDir` (overridable via `PHASE88_PLAYWRIGHT_SKILL_DIR`), `Get-PinnedWindowSeries`, `Invoke-PanelReadBatch`, `Get-PanelSamples`, `Get-PanelStates`, `Get-PanelBand`, `Test-PanelMoved`. Pure, dot-sourceable, no `exit`, no `Push-Location`, no top-level side effects.

## Decisions Made

- **`headerwalk` implemented as a real second mode, not a stub.** `LOCATOR_MODE=headerwalk` navigates once per window to the full dashboard and walks from `data-testid Panel header <title>` to the first following `data-testid panel content`. It is the documented fallback if Wave-0 probe PQ-02 finds `viewPanel=panel-<id>` does not work on the live 12.3.9 instance; having it now means that probe result cannot block the phase.
- **Legend names are recovered from the testid attribute, not only from `innerText`.** `innerText` can normalise away the trailing space in the guard's label-less series (`consumed `), which is precisely panel 2's discrimination signal. The reader also collects `[data-testid^="data-testid VizLegend series"]` attribute values and prefers those when they match a row by trimmed form, carrying both `name` (verbatim) and `nameTrimmed`.
- **`process.exitCode = 1` instead of `process.exit(1)`.** An immediate `process.exit` can truncate buffered stdout, which would destroy the very sentinel lines the caller parses. Setting the exit code and returning yields the same observable exit status with no truncation risk.
- **Sample standard deviation (n−1), not population.** The wider, more conservative choice for the 10–20-sample captures this phase takes.
- **Two additive extensions to the documented contract, both backward-compatible:** an optional `HEADLESS` env var (default headless; `false` is for debugging only), and extra reading fields (`locatorMode`, `legendSeriesNames`, `nameTrimmed`, `error`, `warning`). Nothing in the specified contract was removed or renamed.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] `return ,$array` comma trick made the plan's own acceptance check read 1 instead of 10**
- **Found during:** Task 3 (PowerShell library)
- **Issue:** `Get-PinnedWindowSeries`, `Get-PanelSamples` and `Get-PanelStates` initially returned `, ([object[]]$windows)` to "keep it an array". That emits the array as a SINGLE pipeline object, so the plan's own verification — `$w = @(Get-PinnedWindowSeries ...)`, the repo-standard `@()`-wrap idiom — produced `Count = 1` for a ten-window series. Any caller following the repo convention would have silently banded one nested array instead of ten samples.
- **Fix:** Removed the comma trick from all three functions; they now emit `[object[]]` / `[double[]]` / `[string[]]` unwrapped, with a comment at each return explaining why. `@()`-wrap at the call site is the documented contract.
- **Files modified:** `scripts/lib/phase-88-panel-read.ps1`
- **Verification:** The plan's automated check now returns 10 windows, each 60000 ms wide, entry n's `ToMs` equal to entry n+1's `FromMs`.
- **Committed in:** `1ab7113` (Task 3 commit)

**2. [Rule 3 - Blocking] The library's own guard comment tripped the library's own forbidden-token assertion**
- **Found during:** Task 3 (PowerShell library)
- **Issue:** The `.NOTES` block stated the T-88-05 mitigation by naming the shared forward PID file literally. The plan's acceptance criterion asserts that filename returns NO match anywhere in the file, so the mitigation's own documentation failed the mitigation's own test.
- **Fix:** Reworded the note to describe the shared forward PID file without reproducing the literal, and to cite the plan's `Select-String` guard as the enforcement point. The behaviour was never in question — the library performs no port-forward operation of any kind.
- **Files modified:** `scripts/lib/phase-88-panel-read.ps1`
- **Verification:** Both forbidden-token checks (`.k8s-portforward-pids`, `docker compose`) return no match; the `##PANEL-BATCH-END##` presence check still matches.
- **Committed in:** `1ab7113` (Task 3 commit)

**3. [Rule 1 - Bug] Reverted a state-update pass that marked eight unproven requirements Complete**
- **Found during:** Post-task state updates
- **Issue:** The standard `requirements mark-complete` step (fed from this plan's `requirements` frontmatter) flipped DISC-01, DISC-02, DISC-03, DISC-07 and HAND-01..04 to `[x]` / `Complete`. That asserts falsehoods of exactly the kind this phase exists to eliminate: DISC-01 claims every panel has a recorded baseline band (zero have been captured), DISC-02 claims every panel has been seen moving under its fault (none has), HAND-01 claims a runbook exists (it does not). It also contradicted Task 1's own acceptance criteria, which require each row to read `| ID | 88 | Pending |`.
- **Fix:** `git checkout -- .planning/REQUIREMENTS.md` to restore the committed state. All eleven Phase-88 rows are `Pending`; zero are `Complete`. This plan REGISTERS the family and builds the reading mechanism — the proofs land in 88-04 through 88-11, and each of those plans should mark only what it actually proved.
- **Files modified:** `.planning/REQUIREMENTS.md` (restored to commit `b59698d` content)
- **Verification:** 11 rows match `^\| \S+ \| 88 \| Pending \|`, 0 match `^\| \S+ \| 88 \| Complete \|`; `git status` shows the file clean.
- **Committed in:** n/a — the erroneous change was never committed; the revert restored `b59698d`'s content.

---

**Total deviations:** 3 auto-fixed (2 bugs, 1 blocking)
**Impact on plan:** All three were caught by the plan's own acceptance criteria (two before commit, one before the state commit). No scope change, no new dependency, no cluster mutation.

## Issues Encountered

- **An intended-hermetic check reached the live Grafana.** A supplementary sanity check of `Invoke-PanelReadBatch` (not required by the plan) was written expecting a `ReaderMissing`/`Unreadable` result, but the playwright skill, `node`, and a reachable `127.0.0.1:3000` were all present, so it launched a real headless browser and performed **one read-only panel read** against a 1970-epoch window. It returned `State = Ok` with `requested = 1, emitted = 1` and the env contract correctly cleared afterwards. **Zero cluster mutation** — the reader only navigates and reads the DOM; it scales nothing, sets no env, writes nothing outside the repo. This confirms the invocation contract round-trips end to end (sentinel lines parsed over run.js's banner stdout, counts agreed), but it is explicitly **NOT** an answer to Wave-0 probe questions PQ-02/PQ-04: it does not establish that `viewPanel=panel-<id>` renders the intended panel, nor that `innerText` parses into usable numbers. Plan 88-03 still owns those.
- **Verification note for later plans:** `pwsh -File scripts/phase-87-dashboard-lint.ps1` still exits 0 (24 rules, 2 files, 31 panels, 36 targets) — this plan touched no dashboard JSON, and the gate proves it.

## Known Stubs

None. Both scripts are complete implementations; every branch documented in the plan (including the `headerwalk` fallback mode and the screenshot path) is implemented rather than deferred.

## Threat Flags

None. This plan introduces no network endpoint, no auth path, no schema change, and no package-manager install (T-88-SC holds: the reader consumes only the pre-installed playwright-skill's own `node_modules`; no `package.json` was added to the repo). T-88-08 is satisfied — the reader is authored only at `scripts/phase-88-panel-read.js` and passed by absolute path; `git status --porcelain` shows no path outside the repository. T-88-09 is satisfied — a relative `SCREENSHOT_DIR` is refused with a `warning` field rather than written into the skill directory. T-88-12 is satisfied — per-line sentinel extraction plus the batch-end completeness check, never a whole-stdout parse.

## User Setup Required

None — no external service configuration required. The playwright skill, `node`, and the Grafana port-forward are all pre-existing (an alternate skill install location can be supplied via `PHASE88_PLAYWRIGHT_SKILL_DIR` without a code edit).

## Next Phase Readiness

- **Ready for 88-02** (the runtime-only fault mechanism, `scripts/lib/phase-88-cluster-ops.ps1`) — it is Wave 1 and independent of this plan.
- **Ready for 88-03** (the Wave-0 probe, BLOCKING) — the reader and its PowerShell contract are exactly what PQ-02 (`viewPanel=panel-<id>` locator) and PQ-04 (`innerText` parseability on one stat + one table-legend timeseries) need to exercise. If PQ-02 fails, `LOCATOR_MODE=headerwalk` is already implemented and needs only a `PANEL_TITLES` list.
- **Ready for 88-04** (the DISC-01 baseline capture) — `Get-PinnedWindowSeries -SubWindowSeconds 60 -Count 10` plus one `Invoke-PanelReadBatch` call over all fourteen panel ids is the whole capture, 140 reads in one browser session.
- **Concern to carry forward:** the reader's stat-vs-timeseries classification is heuristic (tab-delimited legend rows vs a single numeric token) and has been exercised only against synthetic text. Plan 88-03's PQ-04 must confirm it against real panel `innerText` from both a stat and a table-legend timeseries before 88-04 captures a baseline on top of it.

## Self-Check: PASSED

- `.planning/REQUIREMENTS.md` — FOUND (11 new ids as statements + traceability rows; 16 Phase-87 rows intact)
- `scripts/phase-88-panel-read.js` — FOUND (`node --check` exits 0)
- `scripts/lib/phase-88-panel-read.ps1` — FOUND (parses, dot-sources clean under StrictMode, all 8 functions defined)
- Commit `b59698d` — FOUND
- Commit `3cc431d` — FOUND
- Commit `1ab7113` — FOUND

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-28*
