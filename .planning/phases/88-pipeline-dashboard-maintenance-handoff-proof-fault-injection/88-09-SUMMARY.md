---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 09
subsystem: docs
tags: [grafana, handoff, accepted-unproven-register, practicality-rubric, requirements-traceability, hand-03, hand-04, disc-04-blocked, wave-8]

# Dependency graph
requires:
  - phase: 88-08
    provides: "analyzer-reports/phase-88-discrimination.json — the fourteen-row matrix this register is DERIVED from, plus analyzer-reports/phase-88-LADDER-01.json's two measured durations"
  - phase: 88-07
    provides: "the DISC-04 escalation, panel 8's three measured unreachability routes, and panel 6's blocked routes"
  - phase: 88-06
    provides: "the panel-5 empties-without-spiking measurement and the panel-4 blank-under-fault finding"
  - phase: 88-05
    provides: "the panel-12 legend-gain and panel-14 two-rung measured-unsatisfiable criteria"
  - phase: 88-04
    provides: "analyzer-reports/phase-88-BASE-01.json — the 42 bands, the panel-4 120 s width, and the 13 zero-signal Class-A series"
  - phase: 88-03
    provides: "88-PROBE-DECISIONS.md — PQ-01/PQ-03/PQ-05/PQ-07, the probe results behind panels 6 and 13 and behind the retention correction"
  - phase: 87
    provides: "87-FINDINGS.md — the genre, the five fixed headings, the citation discipline, and §10's ruling that panel 5's processorId grouping is correct as shipped"
provides:
  - ".planning/phases/88-.../88-FINDINGS.md — the HAND-04 accepted-unproven register (4 records) and the HAND-03 practicality rubric (14 x 6 = 84 cells)"
  - "the two derived lists that ARE plan 88-10's HAND-02 input: 13 misleading-by-default panels and 8 blocking gaps"
  - "a log-corroboration route for panel 8, derived from source because the matrix supplies none: `REINJECT drop {MessageId} drop` (ReinjectConsumer.cs:77) is the branch that opens the gap and never calls CountSent"
  - "the one-per-increment log line for panel 7: `SpawnToPost drop: send to -post exhausted ExecutionId={ExecutionId}` (ProcessorPipeline.cs:309)"
  - "two NEW findings produced by reading the shipped panel descriptions against the measurements: panel 9's tooltip quotes 0.4 while the panel renders 0.2 per pod, and panel 14's tooltip blames idleness for a zero that WEB-01 measured flat under 2456 requests"
  - ".planning/REQUIREMENTS.md — the DISC-*/HAND-* rows graded against the artifacts, with DISC-04 recorded PARTIAL and BLOCKED"
affects: [88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "a register DERIVED from a machine-readable matrix rather than restated from it — the two partitions of the same fourteen cannot drift apart because only one of them is authored"
    - "a rubric question whose test would pass every row is a rubber stamp, not a measurement: register membership is not allowed to earn a discrimination pass"
    - "when a rubric's own evidence source does not exist yet, score on the underlying material and carry the missing source in the blocking-gap list — never record a scheduling artifact as a property of the thing being graded"
    - "a status of Partial with a named shortfall is a stronger record than either a generous Complete or a silent Pending"
    - "amend a requirement WITH its evidence (inline italic marker + dated top-of-file note); never reword it to match its outcome"
    - "section headings inside one document must not share the shape of the headings that define a partition, or a reader and a checker will both mis-read the set"

key-files:
  created:
    - .planning/phases/88-pipeline-dashboard-maintenance-handoff-proof-fault-injection/88-FINDINGS.md
  modified:
    - .planning/REQUIREMENTS.md

key-decisions:
  - "DISC-04 is recorded PARTIAL and BLOCKED, with the shortfall named in three places (the amended statement, a note under the traceability table, and register record 3). It is NOT marked Complete and NOT left Pending in silence — the requirement as written asks for something the product deliberately prevents, and an honest 'cannot be satisfied as written' is the correct outcome"
  - "Rubric question 3 is scored STRICTLY from observedStates[], not from the plan's literal test. The literal test ('DISC-02 proven, OR present in the HAND-04 register') would have made every row pass, which is exactly the rubber stamp T-88-31 exists to prevent"
  - "Rubric questions 2 and 4 cite a runbook that does not exist yet — this rubric is its input. Scored on band usability and on whether the material a runbook row needs exists; the missing runbook is carried as blocking gap B2 rather than scored as 28 silent failures"
  - "The plan's 'roughly twelve-hour Prometheus retention' premise is FALSIFIED (PQ-07 measured 225 h) and the correction is recorded rather than the stale figure repeated — with the honest replacement reason for why the bands are still run-time observations: conditions, not retention"
  - "Panel 8's log-corroboration route was derived from source, because the matrix's 4538-character reason names no log line and the plan's own must_have requires one for every unproven panel"
  - "Panel 7's record states that the hazard motivating the user lock was RESOLVED in v12.0.0 Phase 86 (RESTARTS 0, proven live) — recorded because it lowers the cost of a future attempt — while stating that the decision stands until the user changes it and that this phase does not reopen it"
  - "DISC-03 is marked Complete WITH an inline italic amendment marker naming the local-reference-band departure 88-08 flagged, rather than either quietly passing it or contradicting the plan"
  - "No dashboard JSON was touched to make a finding disappear. Panel 5's empty-instead-of-spike behaviour is RECORDED AND NOT FIXED, because 87-FINDINGS.md §10 rules the processorId grouping correct as shipped and a dashboards-hardening change belongs to a phase that owns the dashboard"

patterns-established:
  - "Read the shipped tooltip against the measured band: two panel descriptions were found to disagree with what their panels render, and neither disagreement was visible from the matrix alone"
  - "Grade a requirement against its literal statement, then record every departure as an amendment with its evidence — the register a maintenance department relies on is only as good as its worst unstated softening"

requirements-completed: [DISC-01, DISC-02, DISC-03, DISC-05, DISC-06, HAND-03, HAND-04]

# Metrics
duration: 45min
completed: 2026-07-29
---

# Phase 88 Plan 09: Accepted-Unproven Register and Practicality Rubric Summary

**Fourteen panels graded against a fixed six-question rubric with no aggregate score, four of them
recorded as accepted-unproven with a log route each — and DISC-04 recorded as `Partial` and blocked,
because the requirement as written asks for something the product deliberately prevents.**

## Performance

- **Duration:** ~45 min (documentation and grading; the live cluster was not driven)
- **Completed:** 2026-07-29
- **Tasks:** 2 of 2
- **Files:** 1 created (825 lines), 1 modified

## Accomplishments

- **`88-FINDINGS.md` exists**, in the `87-FINDINGS.md` genre: eleven numbered records, each answering
  the same five fixed headings and closing by citing the artifact that measured it.
- **The HAND-04 register covers exactly the four `AcceptedUnproven` matrix rows** — panels 6, 7, 8
  and 13 — verified mechanically against `analyzer-reports/phase-88-discrimination.json`, with **no
  `Proven` panel wrongly included and none omitted**. It is *derived* from the matrix, so the two
  partitions of the same fourteen cannot drift apart.
- **Every unproven panel has a reason, a log-corroboration route, and a statement of what a future
  phase would need to prove it.** Two of the four routes are new work: panel 8's was **derived from
  source** because the matrix supplies none, and panel 7's gained the exact one-per-increment search
  string.
- **The rubric is 14 rows x 6 questions = 84 cells**, every cell `yes`/`partial`/`no`, **no weights
  and no aggregate score**. Question 5 is non-`yes` on **thirteen** panels (the rubric expected six);
  question 6 is `no` **dashboard-wide** and is stated as a blocking gap rather than softened.
- **Both derived lists are delivered for 88-10:** 13 misleading-by-default panels with the mechanism
  each, and **8 blocking gaps**.
- **`.planning/REQUIREMENTS.md` tells the truth**: six DISC-* and two HAND-* rows moved to `Complete`
  where the evidence satisfies the literal statement, **DISC-04 to `Partial` with the shortfall named
  three times**, HAND-01/HAND-02 left `Pending` for 88-10, and **all 16 Phase-87 rows, statements and
  checkboxes untouched**.
- **The fences held.** No `src/` file, no manifest, no dashboard JSON, no analyzer artifact was
  modified. `pwsh -File scripts/phase-87-dashboard-lint.ps1` exits **0**.

## Task Commits

| # | Task | Commit | Type |
|---|------|--------|------|
| 1 | `88-FINDINGS.md` — the HAND-04 register and the HAND-03 rubric | `a816f0b` | docs |
| 2 | `.planning/REQUIREMENTS.md` — DISC-*/HAND-* graded against the artifacts | `1fe5117` | docs |

## How The Fourteen Panels Scored

```
                                        Q1   Q2   Q3   Q4   Q5   Q6
 1 Orchestrator consumed vs sent        yes  yes  yes  yes  yes  no
 2 Keeper consumed vs sent              yes  par  yes  par  NO   no
 3 Processor consumed vs sent by image  yes  yes  yes  yes  par  no
 4 Orchestrator sends minus pickups     yes  par  yes  par  NO   no
 5 Per-processor dispatch gap           yes  yes  yes  par  NO   no
 6 Orchestrator unresolved steps        yes  par  NO   par  NO   no
 7 Processor dropped spawns             yes  par  NO   par  NO   no
 8 Keeper consumed - sent gap           par  NO   NO   NO   NO   no
 9 Keeper L2 probe heartbeat            par  yes  yes  yes  par  no
10 WebApi request rate by route         par  par  yes  yes  par  no
11 WebApi p95 request duration          par  par  yes  par  par  no
12 WebApi status-code mix               yes  par  yes  yes  par  no
13 WebApi 5xx ratio                     yes  par  NO   par  NO   no
14 WebApi in-flight + Kestrel           par  par  yes  par  par  no

totals   Q1 9/5/0   Q2 4/9/1   Q3 10/0/4   Q4 5/8/1   Q5 1/6/7   Q6 0/0/14
```

**Panel 8 is the only row that is not `yes` on a single question.** Panel 1 is the only row that is
`yes` on five of six — and its one `no` is question 6, which nothing on the dashboard can pass.

## Findings Worth Carrying Forward

**1. Two shipped panel descriptions disagree with what their panels render — and neither disagreement
was visible from the matrix.** Both were found by reading `k8s/dashboards/business.json` against the
BASE-01 and WEB-01 measurements, which is work the rubric's question 1 forced and nothing earlier in
the phase had done.

- **Panel 9** — the description says *"Typical: a flat 0.4 probes/sec … measured between 0.4001 and
  0.4005."* The panel groups `by (service_instance_id)` and BASE-01 measured **each of the two pod
  rows at 0.2000**. The tooltip quotes the two-pod **sum**; the panel renders **per pod**. A reader
  comparing the two sees half the stated number with nothing to explain why.
- **Panel 14** — the description attributes the flat zero to idleness, *"since this WebApi is idle
  unless something is driving it."* WEB-01 measured `http_server_active_requests` at exactly `0` at
  **every one of six export ticks while ten parallel requesters issued 2456 requests over 480 s**.
  The cause is the **60 s gauge export cadence**, not idleness, and the description's advice
  (*"Investigate in-flight requests staying above zero"*) is about a series that essentially cannot
  be sampled above zero.

Both belong in the panel `description` at any future dashboard revision. Neither was fixed here — a
dashboards change belongs to a phase that owns the dashboard.

**2. Panel 8 had no log-corroboration route until this plan derived one.** Its matrix reason is 4538
characters and names none. Reading `src/Keeper/Recovery/ReinjectConsumer.cs` supplies a route that
**discriminates the three cases the panel collapses into one green `0`**: `REINJECT sent {MessageId}
reinject` (`:120`) means consumed **and** redispatched — conserved; `REINJECT drop {MessageId} drop`
(`:77`) is **the failure the panel's title promises**, taken on the branch that returns early
**before** `CountSent`; and no keeper lines at all means no recovery traffic occurred. That is
strictly more than the panel can say at any range.

**3. The rubric's own test for question 3 would have made it a rubber stamp.** As written it passes a
panel that is "DISC-02 proven **OR** present in the HAND-04 register" — which is all fourteen by
construction, since those are the two partitions. Scored strictly from `observedStates[]` instead, it
separates cleanly: **10 panels seen in ≥ 2 states, 4 seen only in `zero`.** T-88-31 exists to stop
the rubric inflating into a score; this is the same hazard one question lower down.

**4. The plan's retention premise was nineteen times wrong, and the honest replacement reason is
different in kind.** The plan asks for a record of "the roughly twelve-hour Prometheus retention".
PQ-07 measured **225 hours (~9.4 days)**, `RetentionHorizonLimited: false`. The bands *are* still
run-time observations rather than durable facts — but because **conditions** change (host load, pod
identity, traffic pattern), not because the data evaporates. This phase measured that directly: it is
exactly why ZERO-01 and LADDER-01 score their cross-talk controls against a local reference band.

**5. Panel 7's user lock was taken against a hazard that has since been fixed.** The decision was
made because the keeper/processor crash-loop under an unreachable broker was the whole reason the
v12.0.0 milestone existed. **v12.0.0 Phase 86 resolved it** — proven live with all four tiers at
`RESTARTS 0`. The record states this because it materially lowers the cost of a future attempt, and
states equally plainly that **the decision stands until the user changes it** and that this phase
does not reopen it.

**6. Question 6 fails on all fourteen panels, and it is the gap most likely to be waved away.** The
Grafana posture is a ClusterIP with no NodePort and no Ingress, reachable only by a loopback
`kubectl port-forward`, anonymous at role `Viewer`, with `admin`/`admin` as plain env values in the
manifest. It is a legitimate accepted dev posture, explicitly out of scope to fix, and it matches the
unauthenticated in-cluster Prometheus and Elasticsearch exactly. **It is nonetheless a blocking gap
for a genuine handoff**, because a maintenance department cannot open a dashboard it cannot reach.

## Decisions Made

- **DISC-04 is `Partial`, not `Complete` and not `Pending`.** 88-07 escalated it as a blocker and this
  plan grades it as one. Panel 2 is met; panel 8 is measured-unsatisfiable because
  `ReinjectConsumer.cs:106-116` calls `CountSent` on **both** branches by design — the Phase-79
  control was built so the metrics do not reveal the loss, and its own comment says so. The shortfall
  is named in the amended statement, in a note under the traceability table, and in register record 3,
  and the split verdict is stated in roadmap terms: **SC-5 met, SC-4 not met**.
- **Six requirements were marked `Complete` only where the evidence satisfies the literal statement**,
  each citing its artifact in the statement itself. The early-phase incident in which an automatic
  `requirements mark-complete` flipped eight rows for things that did not exist is exactly what this
  discipline exists to prevent; nothing here is marked on optimism.
- **DISC-03 is `Complete` with an amendment marker.** 88-08 left it `Pending` because the ladder
  scores its controls against a local pre-rung band rather than the requirement's literal "its DISC-01
  band". The substantive property — every scenario carries a non-empty pre-declared control list, all
  held, 16/16 and 3/3 at `MaxExcursion` exactly `0.0000` — is satisfied, and the band-source departure
  is a **methodological amendment with a measured reason**, recorded inline rather than passed over.
- **DISC-01 and DISC-05/06/07 carry clarifying notes but no amendment marker**, because no probe
  falsified their premises. Adding markers where nothing was contradicted would dilute the three that
  matter.
- **Panel 5's empty-instead-of-spike behaviour is RECORDED AND NOT FIXED**, for three stated reasons
  in priority order: `87-FINDINGS.md §10` rules the `processorId` grouping **correct as shipped** and
  both alternatives out of scope; the emptiness is a rendering consequence of a correct expression
  meeting a request/response pipeline, not a wiring error; and a dashboards-hardening change belongs
  to a phase that owns the dashboard. Editing a panel to make a finding disappear would destroy the
  evidence this phase exists to produce.
- **The register's four records are exactly the matrix's four `AcceptedUnproven` rows**, asserted
  mechanically rather than by eye.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Rubric question 3's stated test passes every one of the fourteen rows**

- **Found during:** Task 1, while scoring
- **Issue:** The plan defines Q3 as *"DISC-02 proven, **or** present in the HAND-04 register"*. Those
  two sets are the complete partition of the fourteen panels, so the test is a tautology and the
  column would have read `yes` fourteen times — the exact rubber stamp T-88-31 is written to prevent,
  one question below where it was expected.
- **Fix:** Q3 is scored **strictly from the matrix's `observedStates[]`**: `yes` only where the panel
  was observed in ≥ 2 states. Result **10 yes / 4 no**, and the four `no`s are precisely the register
  panels. The scoring rule is stated in the table above the rubric so the cells are auditable.
- **Files modified:** `88-FINDINGS.md`
- **Committed in:** `a816f0b`

**2. [Rule 2 - Missing critical functionality] Rubric questions 2 and 4 cite evidence that does not exist yet**

- **Found during:** Task 1, before scoring
- **Issue:** Q2's test requires the band to be *"written into the runbook"* and Q4's evidence is
  *"the runbook authored in plan 88-10"*. **That runbook does not exist — this rubric is its input.**
  Scoring literally would have recorded 28 cells of `no` describing the phase's own plan ordering
  rather than any property of the dashboard, and would have made both columns uninformative.
- **Fix:** Q2 scored on **band existence and band usability**, Q4 on **whether the material a runbook
  row needs exists** (a proving scenario with measured values, or a named log route). **The missing
  runbook is carried as blocking gap B2**, where it is visible instead of diffused across 28 cells.
  The amendment is stated in a call-out immediately above the table, not applied silently.
- **Committed in:** `a816f0b`

**3. [Rule 1 - Bug] The plan's Section 3 instruction repeats a retention figure that a probe falsified**

- **Found during:** Task 1
- **Issue:** The plan asks for a record of *"the roughly twelve-hour Prometheus retention that makes
  every band a run-time observation rather than a durable fact"*. That figure comes from
  `87-FINDINGS.md §14` and **PQ-07 measured it wrong by a factor of nineteen** —
  `RetentionHoursObserved: 225`, `RetentionHorizonLimited: false`, so it is a measurement of
  Prometheus rather than of the probe. Writing the plan's figure into a maintenance document would
  have published a known-false number.
- **Fix:** Record §5 states the **correction** and then supplies the honest replacement reason for the
  conclusion the plan wanted, which survives on different grounds: the bands are run-time
  observations because **conditions** change, not because data evaporates — measured directly by this
  phase, and the reason two scenarios use a local reference band.
- **Committed in:** `a816f0b`

**4. [Rule 2 - Missing critical functionality] Panel 8 had no log-corroboration route, which a must_have requires**

- **Found during:** Task 1
- **Issue:** The plan's must_have truth is that **every** panel that could not be driven names *"the
  log-based corroboration route maintenance should use instead"*. The matrix supplies one for panels
  6, 7 and 13 — but panel 8's 4538-character reason names **no log line at all**, ending instead at
  "this phase could not produce the failure mode it exists to reveal". The register would have shipped
  its worst panel with no alternative.
- **Fix:** Derived from `src/Keeper/Recovery/ReinjectConsumer.cs`: a three-row table discriminating
  `REINJECT sent … reinject` (`:120`, conserved), **`REINJECT drop {MessageId} drop`** (`:77`, the
  failure the title promises, on the branch that returns before `CountSent`), and no keeper lines at
  all (no recovery traffic). Panel 9 named as the unguarded liveness companion.
- **Committed in:** `a816f0b`

**5. [Rule 2 - Missing critical functionality] Panel 7's corroboration route named an exception type but not the search string**

- **Found during:** Task 1
- **Issue:** The plan's lifted text names `SpawnSendExhaustedException`, which is a **throw**, not
  necessarily a logged string an operator can grep. A maintenance instruction to "search the logs for
  an exception type" may find nothing.
- **Fix:** Added the exact line the counter's own hook emits **immediately before** the increment and
  the throw — `SpawnToPost drop: send to -post exhausted ExecutionId={ExecutionId}`
  (`src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:309`) — which is **one log line per
  increment**, i.e. one-for-one with the panel. `SpawnSendExhaustedException` is retained as the
  second search term.
- **Committed in:** `a816f0b`

**6. [Rule 1 - Bug] Two Section 3 headings shared the shape of a register record**

- **Found during:** Task 1, structural self-check
- **Issue:** `## 8. Panel 4 — …` and `## 7. Panel 5 …` matched the same heading shape as the four
  Section 1 register records, so a structural check read the register set as `{4,6,7,8,13}` instead of
  `{6,7,8,13}`. A human skimming would have made the same mistake, and the acceptance criterion is
  precisely that the register set equals the matrix's `AcceptedUnproven` set.
- **Fix:** Renamed to `## 7. The dispatch gap empties instead of spiking … (panel 5)` and
  `## 8. The one place a diagnostic proxy disagreed with the rendered value (panel 4)`. The register
  partition is now unambiguous to a reader and to any future checker; re-verified mechanically.
- **Committed in:** `a816f0b`

**7. [Rule 2 - Missing critical functionality] DISC-03 was left Pending by 88-08 for a stated reason the plan does not address**

- **Found during:** Task 2
- **Issue:** The plan instructs DISC-03 → `Complete` and its verification asserts `Complete`, but
  88-08 deliberately left it `Pending` because the ladder scores its controls against a **local**
  pre-rung band rather than the requirement's literal *"its DISC-01 band"*, and on the longest rung of
  each regime rather than every rung. Marking it `Complete` in silence would have hidden a departure a
  previous plan went out of its way to flag.
- **Fix:** `Complete` **with an inline italic amendment marker** naming both departures, their measured
  reason (the DISC-01 bands were captured hours earlier under a host load that was not running, so the
  comparison would report a difference in *conditions* as drift) and the artifact fields that record
  them (`BandSource`, `CrossTalkNote`), with a strikethrough on the amended clause. The substantive
  property is satisfied and the amendment is visible.
- **Files modified:** `.planning/REQUIREMENTS.md`
- **Committed in:** `1fe5117`

**8. [Rule 2 - Missing critical functionality] Panel 7's motivating hazard has been fixed since the decision was taken**

- **Found during:** Task 1
- **Issue:** Panel 7's record must state what a future phase would need to prove it. The stated reason
  for the user lock is the unreachable-broker crash-loop — **which v12.0.0 Phase 86 resolved**,
  proven live with all four tiers at `RESTARTS 0`. Omitting that would leave a future reader pricing
  the attempt against a hazard that no longer exists.
- **Fix:** Recorded, with the boundary stated equally plainly: **the decision is the user's and stands
  until the user changes it; this phase does not reopen it.** What a future phase still needs is a
  bounded send-endpoint seam (a `src/` change) rather than taking the broker down, because a
  whole-broker outage moves every panel and proves nothing.
- **Committed in:** `a816f0b`

---

**Total deviations:** 8 auto-fixed (3 bugs, 5 missing critical functionality)
**Impact on plan:** Two of the plan's own rubric tests are **corrected for internal consistency**
(deviations 1 and 2), both with the amendment stated in the delivered document rather than applied
silently; one instructed figure is **replaced by the measurement that falsified it** (deviation 3);
and three register entries gained material the plan required but its inputs did not contain
(deviations 4, 5, 8). No requirement was marked `Complete` that the evidence does not support, no
artifact was regenerated, no dashboard JSON was touched, and no `src/` file was changed.

## Issues Encountered

- **DISC-04 remains blocked and will stay blocked.** It cannot be closed without a `src/` change or
  the allow-listing of `KEEPER_REINJECT_DELAY_MS` plus a shortened `EXECUTION_DATA_TTL` — the
  combination the repo's own FALSIFY-02 control already uses to reach the `REINJECT drop` branch. It
  is recorded as `Partial`, and 88-10's readiness verdict must carry it as a blocking gap rather than
  as outstanding work.
- **Question 6 fails dashboard-wide and cannot be fixed inside this milestone.** Auth hardening and
  ingress are explicitly out of scope per REQUIREMENTS.md. It is stated as a blocking gap anyway.
- **The rubric has no aggregate score and deliberately cannot be summarised as one.** Anyone wanting a
  single number from it will have to read the eight blocking gaps instead — which is the point.
- **Two panel descriptions are now known to be wrong about their own panels** (findings 1). Nothing was
  changed to fix them; they are carried for whichever phase next owns `k8s/dashboards/business.json`.
- **Three untracked files in `k8s/` and `src/` remain**, all pre-existing and present in the
  session-start snapshot (`k8s/22-otel-collector-servicemonitor.yaml`,
  `src/Keeper/Recovery/ReinjectConsumer.cs` modified, `src/BaseApi.Service/Properties/launchSettings.json`).
  None is this plan's and none was staged.

## Known Stubs

None. Both deliverables are complete documents; nothing is placeholdered and no section defers its
content to a later plan. HAND-01 and HAND-02 are **not** stubs here — they are 88-10's deliverables
and are recorded as `Pending` with their inputs handed over.

## Threat Flags

None. No product code, no network endpoint, no auth path, no schema change, no package install
(**T-88-SC** holds — this plan writes documentation only). The register's own threats were exercised:

- **T-88-28** (register / matrix drift) — the register is **derived** from
  `analyzer-reports/phase-88-discrimination.json`, and a mechanical check asserts the register's panel
  set **equals** the matrix's `AcceptedUnproven` set and contains **no** `Proven` panel. The matrix
  stays the single source of truth.
- **T-88-29** (a requirement marked Complete on optimism) — DISC-02's and DISC-04's statuses were
  **computed from the artifacts** in the verification rather than asserted: DISC-02 `Complete` because
  every non-`Proven` row carries a non-empty reason, DISC-04 `Partial` because
  `ZERO-03.PanelResults[PanelId=8].Moved` is `False`. The shortfall is named in three places.
- **T-88-30** (rewriting a requirement to match the outcome) — three inline italic amendment markers
  (DISC-02, DISC-03, DISC-04) plus a dated top-of-file note in the existing 2026-07-27 style; DISC-03's
  amended clause carries a strikethrough so the original text is still readable. **All 16 Phase-87
  rows, statements and checkboxes are byte-unchanged**, asserted by classifying every line of
  `git diff .planning/REQUIREMENTS.md` by requirement id.
- **T-88-31** (a rubric that inflates into a score) — six fixed questions, `yes`/`partial`/`no`, **no
  weights and no aggregate**, verdict = the blocking-gap list. Question 6 fails dashboard-wide and is
  stated as blocking. Deviation 1 removed the one test that would have inflated a column to all-`yes`.

## Requirement Traceability

| ID | What this plan contributed | Status |
|---|---|---|
| DISC-01 | Graded against the artifact: 42 bands across 14/14 panels, with panel 4's 120 s width and the 13 zero-signal Class-A series recorded as qualifications rather than waived. | **Complete** |
| DISC-02 | The register that the requirement's "or is recorded in an accepted-unproven register" clause depends on. 10 Proven + 4 registered = 14/14, and the register is derived from the matrix so they cannot drift. Amended for the probe-driven growth from one panel to four. | **Complete** |
| DISC-03 | Marked with an amendment marker naming the local-reference-band and longest-rung departures 88-08 flagged. 16/16 + 3/3 controls, all at `MaxExcursion` 0.0000. | **Complete** *(amended)* |
| DISC-04 | **Recorded as `Partial` and BLOCKED**, with the measured reason, the two routes that could close it, and the SC-5-met / SC-4-not-met split. The 88-07 escalation is answered rather than carried further. | **Partial — blocked** |
| DISC-05 | Graded: runtime-only mechanism, no manifest edits, three restore claims true on every scenario and every rung, plus the measured Redis-slot addition the requirement did not anticipate. | **Complete** |
| DISC-06 | Graded: re-baseline after every rollout with a disjoint pod-identity pair, strengthened by `RebaselineInertnessProven`. | **Complete** |
| DISC-07 | Already Complete from 88-08. Recorded both measured numbers (240 s / 120 s), the falsified counter prediction and the stated Regime-B property in the statement and in findings §6. | Complete (unchanged) |
| HAND-01 | Not this plan's. Its input — a proving scenario or a log route per panel — is in the register and the rubric. | Pending (88-10) |
| HAND-02 | Not this plan's. **Its three inputs are delivered**: derived list A (13 misleading-by-default panels), derived list B (8 blocking gaps), and the per-panel accepted-unproven status. | Pending (88-10) |
| HAND-03 | **Delivered.** 84 cells, no weights, no aggregate, verdict = the blocking-gap list; two scoring amendments stated in the open. | **Complete** |
| HAND-04 | **Delivered.** Four records, each with reason + log-corroboration route + what a future phase would need. Panel 7 recorded as a user decision in its first sentence. | **Complete** |

## Next Phase Readiness

- **88-10 (HAND-01/HAND-02) has everything it needs.** `88-FINDINGS.md` §2 ends in the two lists the
  readiness verdict is made of, and §1 supplies the accepted-unproven status per panel. The runbook's
  hardest rows are pre-written: panels 6, 7, 8 and 13 each carry the log line to search.
- **Four things 88-10 must NOT soften.** (a) **DISC-04 is blocked**, not outstanding — SC-4 is not met
  and cannot be met inside this milestone. (b) **Question 6 fails on all fourteen panels**; a handoff
  verdict that does not lead with reachability is not honest. (c) **Panel 8 is the only panel that is
  not `yes` on any question**, and its green `0` at a short range is guard, not measurement. (d) **A
  processor-tier outage under 240 s and a WebApi condition under 120 s are invisible** — the operator's
  threshold for "the dashboard would have told me" is not what the research assumed.
- **Three runbook rows that invert the obvious advice.** Panel 5 going **blank** is the signal, not a
  gap climbing. Panel 4 renders **"No data"** under exactly the fault it exists to reveal. Panel 2 can
  render an unbroken **0** through a real recovery event — the reliable signal is the keeper's
  `REINJECT sent` log line, not the panel.
- **Two dashboard-description defects are queued for whichever phase next owns `business.json`**:
  panel 9's tooltip quotes the two-pod sum while the panel renders per pod, and panel 14's tooltip
  blames idleness for a zero the export cadence causes.

## Self-Check: PASSED

- `.planning/phases/88-.../88-FINDINGS.md` — **FOUND** (825 lines). The plan's own automated
  verification prints **"findings and rubric ok"**: the roll-up artifact is cited, every
  `AcceptedUnproven` panel title appears verbatim, panel 7's record names
  `processor_spawn_dropped_total` + `SpawnSendExhaustedException` + the user decision, panel 5's
  record states "NOT FIXED" and cites `87-FINDINGS`, both measured durations (240, 120) are stated,
  all nine requirement ids appear, and the yes/partial/no token count clears 84.
- **Structural re-check, independently:** rubric = **exactly 14 data rows x 6 columns = 84 cells**,
  every cell one of `yes`/`partial`/`no`, ids `1..14` in order; **Q6 is `no` on every row**; **Q5 is
  non-`yes` on 13 rows** (≥ 6 required); register panel set `{6,7,8,13}` **equals** the matrix's
  `AcceptedUnproven` set and includes **no** `Proven` panel; derived list A has 13 panel rows and
  derived list B has 8 entries.
- `.planning/REQUIREMENTS.md` — **FOUND**. The plan's automated verification prints **"requirements
  traceability ok"**: exactly one phase-88 row per id; DISC-01/03/05/06/07 + HAND-03/04 `Complete`;
  HAND-01/02 `Pending`; **DISC-02 `Complete` computed from the artifacts** (no `AcceptedUnproven` row
  lacks a reason); **DISC-04 `Partial` computed from the artifacts**
  (`ZERO-02` panel 2 `Moved=True`, `ZERO-03` panel 8 `Moved=False`); **16 Phase-87 `Complete` rows
  unchanged**.
- **Checkbox / row consistency asserted for all eleven Phase-88 requirements** — DISC-04 is `[ ]` with
  row `Partial`, HAND-01/02 are `[ ]` with row `Pending`, the other eight are `[x]` with row
  `Complete`. Phase-87: 16 checked boxes and 16 `Complete` rows, both unchanged.
- **No Phase-87 line was modified** — every line of `git diff .planning/REQUIREMENTS.md` classified by
  requirement id: only DISC-01..07, HAND-03, HAND-04 statements, their phase-88 traceability rows, and
  three new note lines.
- Commits `a816f0b`, `1fe5117` — both **FOUND**; neither commit deleted a tracked file.
- `pwsh -File scripts/phase-87-dashboard-lint.ps1` — **exit 0** (no dashboard JSON touched);
  `git status --porcelain k8s/` and `src/` contain only pre-existing entries, none staged.
- The live cluster was **not driven** by this plan — no port-forward started, no seam armed, no
  replica scaled, no analyzer artifact regenerated.

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-29*
