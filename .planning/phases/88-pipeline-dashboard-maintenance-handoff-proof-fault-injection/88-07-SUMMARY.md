---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 07
subsystem: testing
tags: [grafana, prometheus, kubernetes, powershell, fault-injection, fault-seam, guarded-zero, keeper-recovery, cross-talk, disc-02, disc-04, disc-05, disc-06, wave-6]

# Dependency graph
requires:
  - phase: 88-01
    provides: "the batch Grafana panel DOM reader — Invoke-PanelReadBatch, Get-PinnedWindowSeries, Get-PanelSamples/States/SeriesCount/SeriesNameAt, Test-PanelMoved, Get-PanelBand"
  - phase: 88-02
    provides: "the runtime-only cluster-ops library — Set-/Clear-Phase88Seam with its static (tier, var) allow-list, Wait-TierSettled, Get-TierPodNames, Assert-StackRestored and the 60/61/62/65 failure-code vocabulary"
  - phase: 88-03
    provides: "the locked levers — LocatorMode viewpanel, RateIntervalSeconds 240, ZERO-01 Dropped on UnresolvedRouteChosen=none, ZERO-02/ZERO-03 both on the `seam` lever, and PQ-06's proof that the arm lands and clears"
  - phase: 88-04
    provides: "analyzer-reports/phase-88-BASE-01.json — panels 2 and 8 both banded at exactly 0..0, so a single event falsifies either; and the measured fact that increase() cannot be evaluated at a 60 s sub-window"
  - phase: 88-05
    provides: "-Mode Scenario, -RecordDroppedRow, the two-seam teardown slots, and the SeamActiveDuringRebaseline question this plan owns"
  - phase: 88-06
    provides: "the first three exercises of the re-baseline path, the ~180 s rate-survival measurement, and the scalar ReplicasBefore/After field shape"
provides:
  - "analyzer-reports/phase-88-ZERO-02.json — panel 2 PROVEN non-zero in a real recovery event, on both its numeric and its legend-name signal, with panel 8's gap held at exactly 0"
  - "analyzer-reports/phase-88-ZERO-03.json — the seam mechanism exercised end to end (SC-5) and panel 8 recorded as MEASURED-UNREACHABLE with three independent enumerated reasons"
  - "analyzer-reports/phase-88-ZERO-01.json — panel 6's accepted-unproven row with both blocked routes, their observed statuses and the log-corroboration path"
  - "the measured fact that `kubectl set env PROCESSOR_DEFEAT_READ=<label>` is NOT the fault — the out-of-band Redis slot skp:test:defeat-read-arm is, and without it a seam scenario drives nothing while looking fully armed"
  - "SeamActiveDuringRebaseline RESOLVED and proven per run via RebaselineInertnessProven"
  - "the measured fact that rate() is BLIND to a recovery burst confined to one 60 s export interval, while increase() sees it — the asymmetry that makes panels 2 and 8 behave differently under the identical event"
  - "the measured fact that panel 8 at a 60 s sub-window renders a guard-supplied 0 for an expression that returns ZERO POINTS"
  - "Set-/Clear-/Test-Phase88ReinjectArm — the Redis arm primitive, with the key as a static literal and a finally-guarded teardown"
  - "research assumption A6 RETIRED: the deployed keeper:tags-const-1544 image honours KEEPER_DEFEAT_REINJECT, proven from Elasticsearch log records"
affects: [88-08, 88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "a fault seam is a CAPABILITY plus a TRIGGER: the env var only makes a fault expressible, and a separate out-of-band act fires it — which is exactly what makes a seam-active re-baseline a valid null hypothesis"
    - "the inertness of an armed-but-untriggered seam is PROVEN per run (the guarded band must still be exactly 0..0), never argued once in a comment"
    - "a legend-name list is the UNION of every rendered row, collected from the readings; a by-index collector reports a stale name for exactly the row a gaining legend inserts"
    - "the driver's own HTTP footprint is issued on the SAME cadence in the band window and the fault window, or the WebApi cross-talk panels measure the driver instead of the fault"
    - "a guarded panel that renders 0 is DECOMPOSED into its own query terms at several range widths, because `A - empty` and `A - B == 0` and `too few samples` all render the identical comfortable 0"
    - "a verdict re-classification preserves the as-driven artifact verbatim and records OriginalVerdict, so nothing is upgraded silently"

key-files:
  created:
    - analyzer-reports/phase-88-ZERO-01.json
    - analyzer-reports/phase-88-ZERO-02.json
    - analyzer-reports/phase-88-ZERO-02-run1-discarded.json
    - analyzer-reports/phase-88-ZERO-03.json
    - analyzer-reports/phase-88-ZERO-03-asdriven.json
    - analyzer-reports/phase-88-ZERO-03-keeper-log.json
    - scripts/phase-88-relegend.ps1
    - scripts/phase-88-zero03-classify.ps1
    - analyzer-reports/phase-88-screenshots/ZERO-01/ (80 PNGs, 7 committed)
    - analyzer-reports/phase-88-screenshots/ZERO-02/ (232 PNGs, 3 committed)
    - analyzer-reports/phase-88-screenshots/ZERO-03/ (154 PNGs, 7 committed)
  modified:
    - scripts/phase-88-panel-discriminate.ps1
    - scripts/lib/phase-88-cluster-ops.ps1

key-decisions:
  - "SeamActiveDuringRebaseline is RESOLVED as: a seam-active re-baseline IS a valid comparison basis, and the pre-arm capture is NOT used instead. Both seams are capabilities that a separate act must trigger, so the re-baseline sits after the arm rollout (the real DISC-06 discontinuity) and before the fault. The reasoning is not trusted — RebaselineInertnessProven asserts per run that every Regime-B subject band is still exactly 0..0 with the seam armed"
  - "`kubectl set env PROCESSOR_DEFEAT_READ=1` drives NOTHING: the variable holds a step LABEL and the fault additionally requires the out-of-band Redis slot skp:test:defeat-read-arm to exist for a hop to claim. Both ZERO rows would have scored a flat panel as evidence about the deployed image"
  - "ZERO-02 run 1 was discarded for two defects in this plan's own drive code and is committed beside run 2. It measured a genuine recovery event that panel 2 rendered as an unbroken 0, because rate() is blind to a burst confined to one export interval"
  - "ZERO-03 is recorded as accepted-unproven, NOT as a Fail and NOT as a Pass. The fault fired as designed and the gap still cannot open — the seam was built in Phase 79 to be telemetry-byte-identical, and CountSent runs on BOTH branches of the defeat"
  - "No substitute fault was invented for panel 8, on the WEB-02 and ZERO-01 precedent. The routes that WOULD open the gap need a seam T-88-15 forbids or an infrastructure fault this phase does not drive"
  - "The activation POST is issued on the same cadence in the re-baseline window and the after window, because run 1 correctly recorded the DRIVER's own HTTP call as cross-talk drift on panels 10 and 12"
  - "No requirement is marked Complete. DISC-04 is HALF met and its other half is measured-unreachable — that is an escalation for 88-09, not a checkbox"

patterns-established:
  - "Read the seam's source before driving it: PROCESSOR_DEFEAT_READ's label-plus-Redis-slot contract and KEEPER_DEFEAT_REINJECT's unconditional CountSent are both invisible from the outside and both decide whether the scenario means anything"
  - "Pre-flight a long scenario with a cheap end-to-end mechanism check; ~8 minutes de-risked two ~50-minute runs and produced the first evidence that consumed and sent move in lockstep"
  - "A pinned ABSOLUTE window can be re-read with a corrected reader — that is the same draw read again, not a second draw — provided the re-derivation is scripted, scoped to the defective field, and records what it replaced"
  - "Keeper logs must be captured BEFORE the disarm rollout; the rollout destroys the pods that handled the fault. This run recovered them only because the stack ships to Elasticsearch"

# This plan's `requirements` frontmatter names DISC-02, DISC-03, DISC-04, DISC-05 and DISC-06.
# NONE is marked complete — see Decisions and the Requirement Traceability section. Panel 2 is
# proven and panel 8 is measured-unreachable, so DISC-04 is half met and can never be fully met
# under this phase's locked constraints; the rest are statements about EVERY panel or EVERY
# scenario and LADDER-01 has not run.
requirements-completed: []

# Metrics
duration: 235min
completed: 2026-07-29
---

# Phase 88 Plan 07: Guarded-Zero Scenarios Summary

**Panel 2 has now been seen non-zero during a real keeper recovery event — the first time in this project's history — and panel 8 has been proven, three independent ways, that it cannot be seen non-zero at all under any lever this phase is allowed to pull; and the reason the whole class of seam scenarios nearly measured nothing is that `kubectl set env PROCESSOR_DEFEAT_READ=1` is not the fault.**

## Performance

- **Duration:** ~235 min (three live scenarios, one discarded run, one pre-flight)
- **Completed:** 2026-07-29
- **Tasks:** 3 of 3, plus a blocking prerequisite the task list did not contain
- **Files modified:** 2 scripts modified, 2 created, 6 artifacts, 466 screenshots on disk (17 committed)

## Accomplishments

- **ZERO-02 is `Pass` (exit 0).** Panel 2's guarded `0..0` band was left for **four consecutive** 60 s sub-windows on the rendered panel, on **both** required signals — and panel 8's gap held at exactly 0 throughout, which is the DISC-04 half this scenario owns.
- **ZERO-03 exercised the runtime-only seam mechanism end to end (roadmap SC-5).** Both seams armed on live Deployments, a disjoint pod-identity pair recorded, the band re-captured after the rollout, the fault driven, both seams disarmed from the **outer `finally`**, and the stack independently verified byte-identical: zero `DEFEAT`/`REINJECT_DELAY`, replicas 1/2/3/2, all four images at `:tags-const-1544`, Redis arm slot cleared.
- **Research assumption A6 is RETIRED.** The deployed `keeper:tags-const-1544` image *does* honour `KEEPER_DEFEAT_REINJECT` — one suppress-send per keeper pod, logged verbatim.
- **16/16 cross-talk controls held, every one at `MaxExcursion` exactly 0.0000** (4 in ZERO-02, 5 in ZERO-03, 4 measured-inert in ZERO-01, plus panel 8's control role). The three guarded zeros not under test (6, 7, 13) stayed at exactly 0 during ZERO-03, so panel 8's flat 0 is its own and not something global.
- **`SeamActiveDuringRebaseline` is resolved and *proven*, not argued** — `RebaselineInertnessProven: true` in both seam artifacts.
- **All fourteen business panels are now accounted for**: ten proven to discriminate (panels 1, 2, 3, 4, 5, 9, 10, 11, 12, 14) and four with evidence-backed accepted-unproven records (6, 7, 8, 13).
- **`pwsh -File scripts/phase-87-dashboard-lint.ps1` still exits 0** — no dashboard JSON was touched, no `src/` file changed, no manifest edited.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 0 | Seam mechanism prerequisite — the Redis arm slot (not in the task list) | `7120550` | fix |
| 1 | ZERO-01 accepted-unproven row | `43ec2e5` | feat |
| 2a | ZERO-02 run 1 discarded + drive fix | `b0159d4` | fix |
| 2b | ZERO-02 — panel 2 proven | `96a4f54` | feat |
| 3 | ZERO-03 — seam proven end to end, panel 8 unreachable | `1ab9314` | feat |

## What The Three Scenarios Measured

| Scenario | Lever | Subject | Predicted | **Observed** | Consecutive | Verdict |
|---|---|---|---|---|---|---|
| ZERO-01 | none (Dropped) | 6 | nonzero | **0,0,0,0,0,0** — not attempted, recorded | — | Inconclusive (accepted-unproven) |
| ZERO-02 | `seam` PROCESSOR_DEFEAT_READ=Step_C | 2 | nonzero | **0.00944 → 0.00895 ops/s** off a 0..0 band | **4** | **Pass** |
| ZERO-03 | `seam` both | 8 | nonzero | **0,0,0,0,0,0** — measured-unreachable | 0 | Inconclusive (accepted-unproven) |

Panel 2's legend transition, recorded window by window and rendered verbatim by Grafana:

```
w1  consumed 0 ops/s | sent 0 ops/s
w2  consumed 0 ops/s | sent 0 ops/s
w3  consumed 0 | consumed keeper 0.00944 | sent 0 | sent keeper 0.00944
w4  consumed 0 | consumed keeper 0.00812 | sent 0 | sent keeper 0.00812
w5  consumed 0 | consumed keeper 0.00958 | sent 0 | sent keeper 0.00958
w6  consumed 0 | consumed keeper 0.00895 | sent 0 | sent keeper 0.00895
```

Panel 8's decomposition, over the same after window at three range widths:

```
consumed increase [60s]    0 points (EMPTY)
consumed increase [120s]   0, 2.0001, 0, 0, 2.0024
consumed increase [600s]   0, 1.3754, 1.2503, 1.1879, 2.4364
sent     increase [60s]    0 points (EMPTY)
sent     increase [120s]   0, 2.0001, 0, 0, 2.0024      <- byte-identical to consumed
sent     increase [600s]   0, 1.3754, 1.2503, 1.1879, 2.4364
gap (unguarded)  [60s]     0 points (EMPTY)
gap (unguarded)  [120s]    0, 0, 0, 0, 0
gap (as rendered, guarded) [60s]  0,0,0,0,0,0,0   <- seven points of guard, from an empty expression
```

## Findings Worth Carrying Forward

**1. `kubectl set env PROCESSOR_DEFEAT_READ=1` drives nothing at all — and it looks fully armed while doing it.** The variable holds a step **LABEL** (`ProcessorPipeline.cs:110-115` tests `d.Payload.Contains(defeatLabel)`), and the fault *additionally* requires the out-of-band Redis slot `skp:test:defeat-read-arm` to exist so a matching hop can atomically claim it. The library's `-Value` default is `'1'` and nothing set the slot. Both ZERO rows would have completed cleanly, rendered flat panels, and been recorded as evidence about the deployed image. This is the single most dangerous defect the plan contained, and it was invisible from outside the seam's source.

**2. `rate()` is BLIND to a recovery burst confined to one export interval; `increase()` is not.** ZERO-02 run 1 produced a genuine event — both replicas claimed, three `KeeperReinject`s flowed, the keeper consumed and sent all three — and panel 2 rendered an unbroken 0. The raw samples say why: both counter series were **born carrying their final value** (`distinct values ['2']` and `['1']` across every scrape), because the SDK does not export a counter until it has been incremented and the whole burst finished inside one 60 s interval. `rate()` differences consecutive samples, so a counter that appears at 2 and stays at 2 has a rate of exactly zero **forever**. A recovery event *is* a handful of messages resolved in seconds — so **panel 2's green 0 does not distinguish "no recovery has ever happened" from "a recovery happened and completed within a minute"**. HAND-02 material of the first order, and the strongest argument in the phase for the log-corroboration route.

**3. Panel 8 cannot be driven off its guarded zero by anything this phase is allowed to do.** Three independent reasons, each measured:
   - **The seam is telemetry-byte-identical by design.** `ReinjectConsumer.cs:106-116` runs `if (!defeatReinject) { Send } else { log }` and then calls `CountSent(...)` **unconditionally**. Its own comment says so: the Phase-79 control was built so the metrics do *not* reveal the loss. Measured: `consumed increase` and `sent increase` identical term for term, and the ES log shows `REINJECT DEFEATED … <MessageId>` followed one millisecond later by `REINJECT sent <the same MessageId> reinject`.
   - **No allow-listed lever reaches the branch that does open a gap.** The gap needs a keeper consume with no send, which is only the `ReinjectConsumer` drop branch (L2 absent, `STRLEN == 0`, early return before `CountSent`). Reaching it needs `KEEPER_REINJECT_DELAY_MS` (refused by T-88-15) or a shortened TTL (not an allow-listed seam). The two **delete-class** consumers never call `CountSent` at all and therefore *do* open the gap — but they only receive a message when a Redis DELETE exhausts, an infrastructure fault this phase does not drive.
   - **At 60 s the panel carries nothing.** Both `increase()` terms returned **zero points** and the guarded form returned seven points of 0. The green 0 an operator reads at a short range is supplied entirely by `or vector(0)`. This is 88-06's panel-4 finding in its **guarded** form, and it is worse: panel 4 at least renders "No data" and says so.

**4. The `or vector(0)` guard row does not disappear when the real series arrives.** Panel 2 rendered **four** legend rows after the fault: `consumed` at 0 and `consumed keeper` at 0.00944, side by side. An operator sees a permanent phantom pair sitting at zero next to the real data, with nothing on the panel to say which is which.

**5. The driver's own HTTP call was correctly caught as cross-talk drift.** Run 1's single `POST /api/v1/orchestration/start` took panel 10's `.../Orchestration/start` series and panel 12's `204` series off their exactly-`0..0` bands (max excursion 0.0056, four sub-windows). The control did its job — it reported a perturbation that was real but was *the driver's*, not the fault's. The fix is to put the driver's footprint in the band, not to loosen the control; with the activation issued on the same cadence in both windows, all five controls came back at 0.0000.

**6. A by-index legend collector reports a stale name for exactly the row a gaining legend inserts.** `Entries` is keyed by row index and takes each name from the first sub-window in which that index existed. Panel 2 plainly rendered `consumed keeper`; the artifact said `consumed, sent, sent, sent keeper`. The one name the scenario's second signal is about was the one dropped.

**7. Keeper logs do not survive the disarm.** `kubectl logs -l app=keeper` returned nothing after the run, because disarming rolls the very pods that handled the reinjects. The A6 retirement rests entirely on Elasticsearch. Any future seam plan must capture the logs **before** the disarm — and note that no *metric* could ever have retired A6, precisely because the seam is byte-identical in telemetry.

## Decisions Made

- **`SeamActiveDuringRebaseline` is resolved as: the seam-active re-baseline IS valid, and the pre-arm capture is NOT substituted for it.** The reasoning is structural rather than lucky — both seams are capabilities that a separate act must trigger (`PROCESSOR_DEFEAT_READ` short-circuits until the Redis slot exists; `KEEPER_DEFEAT_REINJECT` cannot fire until a `KeeperReinject` arrives, which needs the processor seam to have fired first) — so the re-baseline sits *after* the arm rollout, which is the real DISC-06 discontinuity, and *before* the fault. Using the pre-arm capture instead would have been worse: it predates the rollout, so its series would be compared across a pod-identity change. **The reasoning is not trusted:** `RebaselineInertnessProven` asserts per run that every Regime-B subject band is still exactly `0..0` with the seam armed, and it came back `true` in both seam artifacts. A contaminated re-baseline would have shown a non-zero guarded band and the scenario would have been comparing fault against fault.
- **ZERO-01 was not attempted and no substitute fault was invented**, exactly as instructed. Its row carries both enumerated routes, the observed 422 at `step-create`, why the L2 route is not viable, and the log line maintenance should search instead.
- **ZERO-03 is recorded as accepted-unproven rather than as a `Fail`.** The engine's "a read panel that did not move is a Fail" rule exists to catch a panel that cannot discriminate under a fault that *should* have moved it; this run measured that the fault fired as designed and the gap *still* cannot open. Recording that as `Fail` would assert something the same run's evidence contradicts. The run's own artifact is preserved verbatim as `phase-88-ZERO-03-asdriven.json`, `OriginalVerdict` records the `Fail`, and the re-classification states exactly what changed and why.
- **ZERO-02 run 1 was discarded for defects, not for a better number, and is committed beside run 2** — the 88-04 and 88-06 precedent. It is also a *better* measurement of what an operator will see than run 2 is, and it is cited as such in finding 2.
- **The keeper pods were recycled before ZERO-02 run 2** (a plain pod delete; no spec change, ReplicaSet recreates). Once a counter series exists it renders permanently, so a pre-arm window carrying a previous run's series makes the guard→labelled transition unobservable. This is a genuine precondition of the measurement, stated rather than hidden.
- **The screenshot policy was applied as locked.** 466 captures on disk (~27 MB, gitignored); **17 committed**: every panel-8 after-window capture from ZERO-03 (the panel scored Inconclusive), all six panel-6 observation captures from ZERO-01 (likewise), and one representative per scenario — for ZERO-02 a *pair*, because the claim is a transition and one frame cannot show it.
- **No requirement is marked Complete.** See the traceability section — DISC-04 is half met and its other half is measured-unreachable, which is an escalation rather than a checkbox.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] The `seam` lever could not fire any fault as written**

- **Found during:** Task 1 (before any scenario ran, from reading the seam's source)
- **Issue:** `PROCESSOR_DEFEAT_READ` holds a step **LABEL**, not a boolean, and `Set-Phase88Seam` defaults `-Value '1'`. Worse, the label alone is inert: the fault also requires the out-of-band Redis slot `skp:test:defeat-read-arm` to exist so a matching hop can atomically claim it (`KeyDelete` true for exactly one caller across replicas). The engine never set it. Both ZERO rows would have completed cleanly, rendered flat panels and been recorded as evidence about the deployed image — the exact unattributable outcome the plan's own ZERO-03 note warns against.
- **Fix:** Static `seamValue`/`triggerSeamValue` per row (`Step_C`, the linear critical-path hop whose verbatim assignment payload is `{"label": "Step_C", "number": 1}`); `Set-/Clear-/Test-Phase88ReinjectArm` added to the cluster-ops library with the key as a static literal; the slot armed at **trigger time only**, cleared at window close, again in `STEP S7` beside the env-var restore claim, and again unconditionally in the outer `finally`; `ReinjectArmStillSet` re-read independently.
- **Files modified:** `scripts/lib/phase-88-cluster-ops.ps1`, `scripts/phase-88-panel-discriminate.ps1`
- **Committed in:** `7120550`

**2. [Rule 2 - Missing critical functionality] ZERO-02's cross-talk list omitted panel 8, and ZERO-03 observed nothing that could show the keeper had consumed**

- **Found during:** Task 1
- **Issue:** The plan's own ZERO-02 verification requires `CrossTalkPanels[]` to cover panel 8 — it is the DISC-04 half that scenario owns — but the 88-05 table declared only `9,10,12`. Separately, ZERO-03's entire claim is "the keeper **consumed** and did not send", and nothing in its panel list could show the first half: a flat panel 8 beside a flat panel 2 means the fault never fired, while a flat panel 8 beside a *moved* panel 2 is a statement about the gap arithmetic, and the artifact could not have told those apart.
- **Fix:** Panel 8 added to ZERO-02's cross-talk list; panel 2 added to ZERO-03's `observePanels`. Panel 2 duly moved during ZERO-03 (`sent keeper` `0, 0.00288, 0.00566, 0.00556`).
- **Committed in:** `7120550`

**3. [Rule 1 - Bug] A single burst cannot render a rate, so the drive had to span export intervals**

- **Found during:** Task 2 (run 1 verification)
- **Issue:** See finding 2. The counter series are born at their final value, so `rate()` reads exactly 0 for a real event.
- **Fix:** The drive is now bounded arm/claim/roll cycles. Each processor replica assigns its `_reinjectTriggerTarget` static once per **process**, so re-arming mints no new victims once every replica has claimed — one replica is rolled between cycles (never the whole tier, so the pipeline is never out). Strictly bounded by cycle count and one victim per replica per generation; this can never become a hot loop. Run 2's counters rose across intervals (`distinct values ['1','2']` and `['1','2','3']`) and panel 2 moved on four consecutive samples.
- **Committed in:** `b0159d4`

**4. [Rule 1 - Bug] The claim check ran once and reported `claimed=false` for a run in which both replicas had claimed**

- **Found during:** Task 2 (run 1 verification)
- **Issue:** `ReinjectArmClaimObserved` was evaluated only after the **last** arm pass. Once every replica has claimed, later passes leave the slot set — so the field reported "the fault never fired" for a run whose processor logs showed two `TEST-79 reinject-trigger CLAIMED` lines.
- **Fix:** Checked after **every** pass and accumulated.
- **Committed in:** `b0159d4`

**5. [Rule 1 - Bug] The driver's own activation POST was recorded as the fault's blast radius**

- **Found during:** Task 2 (run 1 verification)
- **Issue:** See finding 5. `CrossTalkHeld` was false, and the artifact stated a blast radius that belonged to the driver's HTTP call.
- **Fix:** The activation is issued on the same 120 s cadence during the re-baseline hold and during the after window, so both carry the footprint by construction. The workflow id is resolved before `STEP S4` rather than at trigger time to make that possible. Run 2: all four controls at `MaxExcursion` 0.0000.
- **Committed in:** `b0159d4`

**6. [Rule 1 - Bug] The legend-name collector dropped the one name the assertion is about**

- **Found during:** Task 2 (run 2 verification)
- **Issue:** See finding 6.
- **Fix:** `LegendNames*` are the **union of every rendered row**, collected from the readings; `LegendNames*PerWindow` carries the rows window by window so the transition is auditable rather than merged. `scripts/phase-88-relegend.ps1` re-derived that one field for the completed run from a re-read of the artifact's **own pinned absolute windows** — read-only, no fault, no mutation, with the replaced values preserved in a provenance block. A pinned absolute window re-read is the same draw read again, not a second draw; that is the whole reason this phase pins them.
- **Committed in:** `96a4f54`

**7. [Rule 2 - Missing critical functionality] A guarded panel that renders 0 does not say why**

- **Found during:** Task 3 (before the run)
- **Issue:** `sum(increase(A)) - sum(increase(B)) or vector(0)` renders the identical comfortable 0 when both terms moved together, when neither moved, when one term is EMPTY so the subtraction vanishes into the guard, and when the window is too narrow for `increase()` to have two samples. Those are four different statements about the stack, and ZERO-03's whole outcome depends on which one it is.
- **Fix:** Row-declared `decompositionQueries` evaluated over the same after window at 60/120/600 s, keeping the **per-timestamp** values (a mean would collapse "empty at every step" and "zero at every step" into the same number). This is what turned "panel 8 stayed 0" from a shrug into three enumerated, falsifiable reasons.
- **Committed in:** `b0159d4`

**8. [Rule 2 - Missing critical functionality] The dropped-row recorder could not satisfy ZERO-01's own acceptance criteria**

- **Found during:** Task 1
- **Issue:** Two gaps. Its `AcceptedUnprovenReason` was hard-coded to WEB-02's 5xx endpoint sweep and the redis/readiness-latch narrative — under ZERO-01's name that would read as evidence for the wrong routes. And its cross-talk entries carried `StayedPut = $null`, while the plan's verification requires panels 9, 10, 12 and 13 to be measured.
- **Fix:** A row may carry its own static `acceptedUnprovenReason`. The recorder now READS its declared cross-talk panels in the same batch and scores them against a **local reference band** from the ten windows immediately preceding the observation — BASE-01 was banded under a host load that is not running here, so a DISC-01 comparison would report a difference in *conditions* as drift. Both windows had already elapsed, so the reference cost no wall time. The entries stay `Applicable = false` with a note distinguishing **measured inertness** from a blast-radius bound; this is not the fabricated claim 88-05 refused.
- **Committed in:** `7120550`

**9. [Rule 3 - Blocking, out of task order] A pre-flight was run before the first live scenario**

- **Found during:** Task 2 preparation
- **Issue:** Deviation 1 was a source reading. Committing ~50 minutes of live time to an unverified mechanism would have been the analysis-without-measurement the phase refuses.
- **Fix:** An ~8-minute arm → Redis-slot → observe → disarm pre-flight, finally-guarded, which proved the mechanism end to end (both replicas claimed, four keeper events) and produced this plan's first evidence that consumed and sent move in lockstep. The keeper pods were recycled afterwards so the counter series it created could not contaminate ZERO-02's guard.
- **Files modified:** none (a scratchpad script, deliberately not committed — the capability it exercises is the committed library)

---

**Total deviations:** 9 auto-fixed (5 bugs, 2 missing critical functionality, 2 blocking)
**Impact on plan:** The plan's ZERO-02 outcome is **achieved**; its ZERO-03 outcome is **corrected by measurement** — the mechanism the plan names cannot produce the effect it predicts, and the plan's own "treat a flat panel 8 as an open question about the image" instruction is answered definitively in the other direction (the image honours the seam; the *arithmetic* is what holds). ZERO-01 is recorded exactly as instructed. No src/ file was changed, no manifest edited, no substitute fault invented.

## Issues Encountered

- **DISC-04 cannot be fully satisfied under this phase's locked constraints, and that is an escalation for 88-09.** Its text requires panel 8 non-zero with the reinject suppressed. The suppression seam increments `keeper_messages_sent_total` on the suppressed branch **by design**, so the requirement as written asks for something the product deliberately prevents. Proving it would need either a `src/` change (forbidden) or `KEEPER_REINJECT_DELAY_MS` (forbidden by T-88-15). **88-09 must record DISC-04 as half met with the reason, and HAND-04 must carry panel 8.**
- **The plan's task-3 automated verification has an unsatisfiable branch.** It selects "blocked" on the probe's `SeamArmLanded`/`SeamDisarmClean`, both `true`, and then demands `Verdict = Pass` with panel 8 moved. Those probe fields are about whether the *variable lands and clears* — which it does — not about whether the gap can open. Every other clause of that verification passes against the delivered artifact; the verdict clause is the one the measurement corrects.
- **`ReinjectArmClaimObserved` is a weak positive.** It says a claim was observed at least once, which is true and useful, but the authoritative evidence that the fault fired is the processor's `TEST-79 reinject-trigger CLAIMED` log line and the keeper's counter movement. Both were checked live for both runs.
- **The pre-arm baseline of a seam scenario is not comparable across runs.** A counter series, once created, renders permanently — so a pre-arm window that overlaps a previous run's events shows the labelled series and the guard→labelled transition becomes unobservable. This bit run 1 (whose pre-arm carried the pre-flight's series) and is why the keeper pods were recycled before run 2.
- **466 screenshots (~27 MB) sit on disk, gitignored.** 17 are committed. Anyone re-verifying visually from a fresh clone has every not-clean panel capture plus a representative per scenario, and must re-run to see the rest.

## Known Stubs

None in what this plan owns. `-Mode DurationLadder` still exits 64 naming plan 88-08 — the phase's own division of work, not a deferral.

## Threat Flags

None. No product code, no network endpoint, no auth path, no schema change, no package install (T-88-SC holds). Every threat in the plan's register was exercised:

- **T-88-03** — both seams disarmed with the trailing-hyphen form from the **outer `finally`**, then `Assert-StackRestored`, then an independent `kubectl` env read on both seam-capable tiers finding zero `DEFEAT`/`REINJECT_DELAY`. ZERO-03 ran last, so a leak could not confound anything. The Redis arm slot was given the same unconditional teardown and re-read afterwards (`ReinjectArmStillSet: false`).
- **T-88-01** — every (tier, var) pair validated against the static `$Phase88Seams` allow-list; the seam *values* are static table strings; the Redis key is a static literal in the library; the rolled tier is a static string. Nothing is derived from `-ScenarioId`.
- **T-88-15** — `KEEPER_REINJECT_DELAY_MS` was not used and is not in the allow-list. It is now also *named* in panel 8's accepted-unproven reason as one of the two routes that would open the gap, so the refusal is documented rather than silent.
- **T-88-11** — ZERO-01 seeded nothing, mutated nothing and deleted nothing; `v8-fanout-proof` still counts 1 and was never modified by any of the three scenarios.
- **T-88-20** — both seam scenarios recorded non-empty, disjoint pod-identity pairs, `BaselineRecaptured: true`, and scored against the post-settle re-capture. `RebaselineInertnessProven: true` additionally proves the band was taken before the fault, not merely after the rollout.
- **T-88-21** — 9 pre-declared controls across the two live scenarios, all held at `MaxExcursion` 0.0000, each measured in the **same batch and same windows** as the claim. Panel 7 in particular stayed at exactly 0 during ZERO-03, so the user-locked decision not to drive it is not contradicted.
- **T-88-12** — the rendered panel decided every verdict; `DiagnosticQueryValue`/`DiagnosticAgreesWithPanel` recorded beside it (ZERO-02 proxy `0.01254` vs rendered `0.01504`, agreeing). The decomposition rows are explicitly diagnosis and cannot make a panel `Moved`.
- **T-88-02 / T-88-10** — all restores through the live-read path; replicas re-read at 1/2/3/2 and images at `:tags-const-1544` after every run.
- **T-88-18** — `-Depth 10` present, the shallower depth asserted absent, and the structural guard clean on all six written artifacts including both re-written ones.
- **T-88-04 / T-88-05** — the pre-existing Grafana forward was reused and left running; this plan started none.

## Requirement Traceability

| ID | What this plan contributed | Status |
|---|---|---|
| DISC-02 | Panel 2 proven (tenth of fourteen). Panels 6 and 8 recorded accepted-unproven with enumerated routes. All fourteen are now proven or registered — but the register is authored in 88-09. | Pending |
| DISC-03 | Cross-talk ran on two more scenarios, 9 controls at 0.0000, plus four measured-inert on the dropped row. LADDER-01 remains. | Pending |
| DISC-04 | **Half met and half measured-unreachable.** Panel 2 proven non-zero in a real recovery event with the gap held at 0; panel 8 proven undrivable under any allowed lever. **Escalated.** | Pending |
| DISC-05 | The mechanism exercised end to end for the first time, both seams, disarmed from `finally`, stack byte-identical by independent read. The statement is about *every* scenario and LADDER-01 remains. | Pending |
| DISC-06 | Two more re-baselines, both with disjoint pod-identity pairs, plus the new `RebaselineInertnessProven` proof that the band predates the fault. LADDER-01 remains. | Pending |
| HAND-04 | The register gains panel 6 (both routes, observed 422, the log line) and panel 8 (three enumerated reasons with measured evidence). | Pending |

## Next Phase Readiness

- **Ready for 88-08 (LADDER-01).** The `seam` branch is now proven end to end and the arm/claim/roll drive is reusable. Two inputs: `rate()` cannot see a burst confined to one 60 s export interval — a ladder rung shorter than an export cadence will move **nothing** on a counter panel, whatever the fault; and `increase()` panels cannot be evaluated at 60 s at all, so panel 4's rungs must stay at 120 s.
- **Input for 88-09 (HAND-02/HAND-04) — four substantial new entries.** Panel 2's `rate()` blindness to a real recovery event; panel 8's three-way unreachability and its guard-supplied zero at short ranges; the phantom `or vector(0)` legend rows that persist beside real data; and panel 6's log-corroboration route. **HAND-04 now carries four panels (6, 7, 8, 13), not three.**
- **Escalation for 88-09.** DISC-04's text cannot be satisfied as written. It must be recorded as half met with the measured reason, not quietly left Pending.
- **Input for 88-10/88-11 (runbook).** Panel 2's row must say that a real recovery event may leave the panel at zero and that the operator's reliable signal is the keeper's `REINJECT sent` log line, not the panel. Panel 8's row must say its green 0 is the guard at any short range and that its arithmetic cannot show a suppressed redispatch at all.
- **Operational note for any future seam plan.** Capture keeper logs **before** the disarm — the rollout destroys them — and recycle the keeper pods before a run whose claim depends on the `or vector(0)` guard being in its label-less state.

## Self-Check: PASSED

- `analyzer-reports/phase-88-ZERO-01.json` — FOUND (parses; `Verdict: Inconclusive`; `Moved: false`; `AcceptedUnprovenReason` names both routes, the 422 and the `Dangling next-step id` log line; cross-talk 9/10/12/13 all `StayedPut` with panel 13 at exactly 0; three restore claims true; no `System.Object[]` value) — the plan's own verification prints **"ZERO-01 resolved"**, including its independent `psql` count of `v8-fanout-proof` = 1
- `analyzer-reports/phase-88-ZERO-02.json` — FOUND (parses; `Verdict: Pass`; panel 2 band `0..0`, `Moved`, 4 consecutive; `LegendNamesAfter` gains `consumed keeper` and `sent keeper`; cross-talk 8/9/10/12 held with panel 8 at exactly 0; `BaselineRecaptured` true with a disjoint rollout pair; `RebaselineInertnessProven` true; `SeamVarsAfter` empty) — the plan's verification prints **"ZERO-02 proven — panel 2 non-zero with the reinject intact"**
- `analyzer-reports/phase-88-ZERO-02-run1-discarded.json` — FOUND (the discarded run, kept deliberately)
- `analyzer-reports/phase-88-ZERO-03.json` — FOUND (parses; `Verdict: Inconclusive`, `OriginalVerdict: Fail`; `AcceptedUnprovenReason` non-empty with three enumerated routes; cross-talk 6/7/13/10/12 all held with 6/7/13 at exactly 0; `BaselineRecaptured` true, rollout pair disjoint; `SeamVarsAfter` empty; `SeamHonouredByDeployedImage: true`) — the task-3 verification's restore, cross-talk, seam-residue, replica and image clauses all pass; the verdict clause is corrected by measurement and stated above
- `analyzer-reports/phase-88-ZERO-03-asdriven.json` — FOUND (the run's own `Fail` artifact, preserved verbatim)
- `analyzer-reports/phase-88-ZERO-03-keeper-log.json` — FOUND (11 verbatim records; 2 `REINJECT DEFEATED`, one per keeper pod)
- `scripts/phase-88-panel-discriminate.ps1` — FOUND (parses via `[ScriptBlock]::Create`; forbidden-token gate clean including `TierReplicas`, `apply -k`, `0.0.0.0`, `-Depth 5`, `scale statefulset`; `NOPE-99`, `ZERO-01` without the switch and a `-Mode` mismatch each still exit 64)
- `scripts/lib/phase-88-cluster-ops.ps1` — FOUND (parses; the Redis arm primitives smoke-tested set → EXISTS → DEL → EXISTS)
- `scripts/phase-88-relegend.ps1`, `scripts/phase-88-zero03-classify.ps1` — FOUND (both ran to exit 0)
- Commits `7120550`, `43ec2e5`, `b0159d4`, `96a4f54`, `1ab9314` — all FOUND
- `pwsh -File scripts/phase-87-dashboard-lint.ps1` — exit 0 (no dashboard JSON was touched)
- Live stack re-verified after all three runs by an independent `Assert-StackRestored`: `Ok=True`, replicas 1/2/3/2, all four images `:tags-const-1544`, zero `DEFEAT`/`REINJECT_DELAY`, Redis arm slot absent, zero background jobs; the pre-existing Grafana forward left running and none started

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-29*
