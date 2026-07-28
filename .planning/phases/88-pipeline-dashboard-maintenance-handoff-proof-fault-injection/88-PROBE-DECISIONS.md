---
phase: 88
slug: pipeline-dashboard-maintenance-handoff-proof-fault-injection
date: 2026-07-28
status: locked
requirements: [DISC-02, DISC-04, DISC-05, DISC-06, HAND-04]
questions: [PQ-01, PQ-02, PQ-03, PQ-04, PQ-05, PQ-06, PQ-07]
evidence: analyzer-reports/phase-88-wave0-probe.json
---

# Phase 88 — Wave-0 Probe Decisions

Every decision below is derived **mechanically** from a field of
`analyzer-reports/phase-88-wave0-probe.json`. Where a field exists, no judgement was applied: the
branch table in `88-03-PLAN.md` was followed literally, and each record names the field that decided
it. Two decisions carry an addition the branch table did not anticipate; both are flagged as such
and both are grounded in a measured value rather than in a preference.

**The run.** `pwsh -File scripts/phase-88-wave0-probe.ps1` against the live `skp` Docker-Desktop
cluster, completed `2026-07-28T19:49:14Z`, **`Verdict: Pass`, exit 0, `UnevaluableQuestions` empty**
— all seven questions were answered, none was declared unevaluable, and the stack was left clean
(`SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` all `true`, with empty mismatch arrays).

**Evidentiary horizon (PQ-07).** Oldest available Prometheus sample `2026-07-19T07:38:06Z` —
`RetentionHoursObserved: 225` (~9.4 days), `RetentionHorizonLimited: false`. Every number quoted in
this file was captured during that run and is reproducible from the artifact, not from the TSDB.

---

## Record 1 — PQ-01: does a plain processor-tier crash produce keeper recovery traffic here?

**Question.** `keeper_messages_consumed_total` was absent from the entire `__name__` index at
Phase-87 measurement time — not at zero, never emitted. Does a whole-tier `processor-sample` scale-0
during live traffic bring it into existence in **this** cluster?

**Measured answer.** **No.**
`KeeperRecoveryTrafficObserved: false`, `KeeperConsumedSeriesCount: 0`.
The probe drove the fan-out workflow (activation 204), held 120 s so work was genuinely in flight,
ran `Invoke-TierScaleFault -Tier 'processor-sample' -DwellSeconds 90` (`Ok=True`, `ReplicasBefore=2`,
`ReplicasAfter=2`, `ReplicasRestored=True`), settled 180 s (three export cadences), and then found
**zero** non-empty `keeper_messages_consumed_total` series across a 30-minute range query covering
the whole fault.

**Decision.** **Scenario ZERO-02 (panel 2) lever = `seam`.**
`PROCESSOR_DEFEAT_READ` on `processor-sample`, with `KEEPER_DEFEAT_REINJECT` left **UNSET** so the
keeper consumes *and* reinjects — consumed and sent both move, and the panel-8 gap stays 0.
The roadmap's stated preference for driving a panel by system state alone does not survive contact
with this cluster: the preference is honoured wherever it can be, and here it cannot.

**Consequence for the scenario plans.** ZERO-02 gains an **arm rollout plus a re-baseline** (DISC-06):
arm → `rollout status` → settle ≥ 150 s → **re-capture the baseline** → trigger → capture → restore →
disarm → assert. It is therefore no longer the cheap scale scenario the research assumed, and plan
88-07 must budget two rollouts for it rather than none. The seam pair
(`processor-sample`/`PROCESSOR_DEFEAT_READ`) is already in the `phase-88-cluster-ops.ps1` allow-list,
so no library change is required.

---

## Record 2 — PQ-02: what is the reader's primary DOM locator?

**Question.** Does `?viewPanel=panel-<id>` work on this Grafana 12.3.9 instance, and does
`getByTestId('data-testid panel content').innerText()` yield a parseable number for **both** a stat
panel and a table-legend timeseries panel?

**Measured answer.** **Yes, for the locator and for both numeric parses.**
`ViewPanelUrlWorks: true`, `StatPanelParsed: true`, `TimeseriesLegendParsed: true`,
`LocatorModeChosen: "viewpanel"`. Twenty (panel, window) pairs were requested and twenty were
emitted, `State: Ok`, over ten abutting 60 s absolute windows at a pinned 1920x1080 viewport.
`Panel8RawText` is the literal `0`. `Panel9RawText` is the table legend, verbatim:

```
Name	Mean	Max

keeper-99b8c574b-29mzq
	0.200 ops/s	0.200 ops/s

keeper-99b8c574b-x4wqr
	0.200 ops/s	0.200 ops/s
```

**Decision.** **`LocatorMode = viewpanel` for the whole phase.** No later plan passes
`PANEL_TITLES`, and the header-walk mode stays what it was built to be — an implemented fallback
that this measurement did not need. The fourteen titles are recorded in the appendix anyway so that
a future regression in the `viewPanel` URL costs a config change rather than a rediscovery.

**Consequence for the scenario plans.** BASE-01 and every scenario read use `viewpanel` unmodified.

---

## Record 3 — PQ-02 addendum: the legend row NAMES do not bind (reader amendment for 88-04)

> **This record goes beyond the branch table.** The table anticipated
> `TimeseriesLegendParsed: false` and prescribed a remedy. The measured situation is different and
> worse-shaped: the values parse and the **names do not**, so the defect is invisible to the flag the
> table branches on. It is recorded here, against its own measured field, rather than being allowed
> to reach 88-04 as a surprise.

**Question.** Do the parsed legend rows carry the series names the phase's discrimination depends on?

**Measured answer.** **No.** `TimeseriesLegendNamesBound: false`, `Panel9LegendNames: []`,
`Panel2LegendNamesAfter: []`. Two independent mechanisms both fail:

1. **`innerText` puts the series name on its own line.** As the `Panel9RawText` above shows, the row
   is two lines — a name line (`keeper-99b8c574b-29mzq`) and a values line
   (`\t0.200 ops/s\t0.200 ops/s`). `parsePanelText` splits each line on tabs and requires ≥ 2 cells,
   so the name line is discarded as noise and the values line yields a row whose first cell is the
   empty string. Every parsed row therefore reads `{"name":"","nameTrimmed":"","mean":<number>}`.
2. **The `data-testid VizLegend series <name>` attribute route returns nothing.**
   `legendSeriesNames` came back empty on every reading. The selector that 88-01 designed as the
   verbatim-name recovery path — and that the branch table prescribes as the remedy — matches **no
   elements** on this render. The remedy the plan names is not available on this instance.

**Supplementary measurement (read-only, same day, absolute windows).** A direct two-window read of
panel 2 through the same library returned, for both windows:

```
rawText = Name	Mean	Max /  / consumed / 	0 ops/s	0 ops/s /  / sent / 	0 ops/s	0 ops/s
series  = [{"name":"","nameTrimmed":"","mean":0,"max":0},{"name":"","nameTrimmed":"","mean":0,"max":0}]
legendSeriesNames = (empty)
```

This settles a second question the research left open. The guard **is** firing (two label-less
series), but `innerText` renders the label-less name as the bare word `consumed` — **the trailing
space is normalised away entirely**. So the research's proposed signal, "`consumed ` with a trailing
space becomes `consumed keeper`", is not observable as a trailing-space difference. It remains
observable as a **suffix** difference (`consumed` → `consumed keeper`) — but only once names bind to
rows at all, which today they do not.

**Decision — a required amendment for plan 88-04, before any baseline is captured.**

1. `parsePanelText` must pair a **name line** with the **values line that follows it**, rather than
   requiring both on one line. Positional row indexing is the reliable axis; name matching is not.
2. `Get-PanelSamples -SeriesName` is, as of this measurement, **unusable** on this stack — it matches
   on `name`, and every `name` is empty. Until the amendment lands, per-series reads must be taken by
   **row index**.
3. The `data-testid VizLegend series <name>` recovery path must be treated as **absent**, not as a
   fallback. Keep the code (it is harmless and version-dependent) but never depend on it.
4. Panel 2's discrimination must be asserted on the **numeric** axis as primary (Regime B: a 0..0
   band, any non-zero falsifies it) with the **legend suffix** change as the corroborating second
   signal, contingent on amendment 1. The research's "two independent signals from one capture" holds
   only after 88-04 fixes the parser.

**Consequence for the scenario plans.** BASE-01 (88-04) carries the parser amendment and re-proves
it hermetically before capturing any band. ZERO-02 and ZERO-03 assert numerically first. No scenario
may be written whose *only* movement signal is a legend name.

---

## Record 4 — PQ-03: is there a safe route to a WebApi 5xx?

**Question.** Does any WebApi endpoint return 5xx on safe input?

**Measured answer.** **No.** `Safe5xxFound: false`, `Safe5xxRoute: null`, over **seven** probed
endpoints (`ProbedEndpoints`):

| Method | Path | Status |
|---|---|---|
| GET | `/api/v1/workflows/00000000-0000-0000-0000-000000000001` | 404 |
| GET | `/api/v1/workflows/not-a-guid` | 404 |
| GET | `/api/v1/schemas/00000000-0000-0000-0000-000000000002` | 404 |
| GET | `/api/v1/processors/by-source-hash/000…000` (64 zeros) | 404 |
| GET | `/api/v1/__phase88_probe_unmatched` | 404 |
| POST | `/api/v1/orchestration/start` with body `[]` | 400 |
| POST | `/api/v1/steps` with a dangling `nextStepIds` (PQ-05 route A) | 422 |

The WebApi's error handling is, on this evidence, uniformly well-behaved: every malformed or absent
reference resolves to a 4xx. That is a *good* result about the product and a *blocking* one for
panel 13.

**Decision.** **Scenario WEB-02 is DROPPED. Panel 13 (`WebApi 5xx ratio`) enters the HAND-04
accepted-unproven register**, with the reason recorded verbatim:

> no endpoint returns 5xx on safe input; the dependency-outage route costs a WebApi pod restart under
> the Phase-86 hard readiness latch and is out of budget

The redis-outage route is **not** substituted. It was excluded by design and remains excluded.

**Consequence for the scenario plans.** 88-05 authors WEB-01 only. 88-09's register gains panel 13
alongside panel 7 (user-locked) and panel 6 (Record 5). Panel 13's runbook row must state plainly
that its guarded green `0` is indistinguishable from "the counter has never been emitted".

**Bonus, and it is load-bearing for WEB-01.** Two status-code drivers panel 12 needs were confirmed
in the same enumeration and cost nothing: `GET /api/v1/__phase88_probe_unmatched` → **404**, and
`POST /api/v1/orchestration/start` with an empty JSON array → **400**. WEB-01 should drive exactly
these two rather than inventing new ones — both are inert, both are already proven to produce the
status they claim, and neither creates or mutates a row.

---

## Record 5 — PQ-05: which route reaches an `orchestrator_step_unresolved` increment?

**Question.** Which route to a dangling next-step edge is reachable without a `src/` change?

**Measured answer.** **Neither.** `UnresolvedRouteChosen: "none"`.

- **Route A — API dangling edge.** `UnresolvedRouteApiDanglingEdgeStatus: 422`,
  `UnresolvedRouteApiDanglingEdgeStage: "step-create"`. The route is refused **earlier than the
  planning analysis predicted**: `CycleDetector`'s D-08 missing-step gate was expected to reject it
  at activation, but `POST /api/v1/steps` itself returns 422 for a `nextStepIds` entry that names a
  non-existent step. The graph can never be brought into the state the route needs.
- **Route B — L2 step-key deletion.** `UnresolvedRouteL2StepKeyViable: false`. On a validly created
  and activated two-step probe workflow, the non-entry step's key `skp:{workflowId}:{stepId}` was
  present after activation, absent after a `DEL`, and **present again after one stop/start cycle**.
  `OrchestrationService.StartAsync` ends in `IRedisProjectionWriter.UpsertAsync`, which rewrites the
  whole snapshot from Postgres — so the hole closes before the orchestrator's hydration BFS could
  ever miss the step.

**Decision.** **Scenario ZERO-01 is DROPPED. Panel 6 (`Orchestrator unresolved steps in range`)
enters the HAND-04 accepted-unproven register**, carrying both enumerated routes and their observed
statuses exactly as above. The counter is genuinely unreachable from outside `src/`, and the locked
constraint forbids reaching inside it.

**Consequence for the scenario plans.** 88-07 authors ZERO-02 and ZERO-03 only. Panel 6's runbook
row should record what the research already established about its behaviour — the increment is
graceful and always acks, so a non-zero reading is credible — while stating that this phase never
observed it non-zero and could not.

**Safety, for the record.** The probe workflow was created on a **separate** workflow (never
`v8-fanout-proof`), carried a daily 04:00 cron so it could not fire during the run, and was **stopped
through the API before anything was deleted** — the orphaned-Quartz-cron hazard was not merely
avoided but designed around. `ProbeWorkflowId: c3b4bad3-bea9-4cc5-8991-0c5cc15c5e79`,
`ProbeWorkflowCleanedUp: true`. Independently re-verified after the run: `workflows=1`, `steps=10`,
`assignments=10`, `step_next_steps=10` — byte-identical to the pre-run counts — and a Redis scan for
`skp:c3b4bad3*` returns nothing.

---

## Record 6 — PQ-04: is `$__rate_interval` pinned?

**Question.** Does `$__rate_interval` resolve to the same value for a narrow and a wide window at a
fixed viewport?

**Measured answer.** **Yes.** `DatasourceTimeIntervalSeconds: 60` (read from the live datasource, not
assumed), `RateIntervalNarrowSeconds: 240` (10-minute window), `RateIntervalWideSeconds: 240`
(2-hour window), `RateIntervalPinned: true`, at `ViewportWidth: 1920` / `ViewportHeight: 1080`.
Both windows floor their step at the datasource `timeInterval`, so
`max(4 x 60, 60 + 60) = 240` either way.

**Decision.** **One `RateIntervalSeconds` value — `240` — is stated in every Phase-88 artifact.**
The equal-window-width rule remains mandatory for a different reason (a timeseries legend Mean is
computed over the visible range), but it is no longer load-bearing for the smearing window.

**Consequence for the scenario plans.** Every artifact from 88-04 onward carries
`RateIntervalSeconds: 240` and `ViewportWidth`/`ViewportHeight` `1920`/`1080` as a fixed "how it
verified" block. A narrow after-window and a wider baseline are directly comparable in smearing
terms, which frees the duration ladder (88-08) from re-deriving the value per rung.

---

## Record 7 — PQ-06: does the seam mechanism land and clear on a live pod?

**Question.** Does `kubectl set env` land `KEEPER_DEFEAT_REINJECT` on a live keeper pod, and does the
disarm plus the restore assertion leave the stack clean?

**Measured answer.** **Yes, both.** `SeamArmLanded: true`, `SeamDisarmClean: true`.
This is the **first live execution of the arm path** — everything asserted about it before now was
static (parse, dot-source, allow-list rejection). The rollout is recorded as the DISC-06
discontinuity it is:

| | pods |
|---|---|
| `SeamRolloutOldPods` | `keeper-99b8c574b-29mzq`, `keeper-99b8c574b-x4wqr` |
| `SeamRolloutNewPods` | `keeper-75dcb7cb47-sf8pl`, `keeper-75dcb7cb47-zzdmx` |

Both rollouts settled inside the library's 180 s bound, so that bound is now measured rather than
merely reasoned. After the disarm, `Assert-StackRestored` returned `SeamVarsClean`,
`ReplicasRestored` and `ImagesUnchanged` all `true` with empty mismatch arrays.

**Decision.** **Scenario ZERO-03 (panel 8) proceeds.** The roadmap SC-5 deliverable is exercised end
to end and is not a risk to escalate.

**Consequence for the scenario plans.** 88-07 may arm `KEEPER_DEFEAT_REINJECT` on the keeper with the
mechanism proven. **What this does NOT license:** PQ-06 proves the variable lands and clears. It does
**not** prove the deployed `keeper:tags-const-1544` image *honours* it — only a real recovery event
can show that, and Record 1 established that this cluster produces no recovery traffic without a
trigger seam. ZERO-03 must therefore arm **both** seams (`PROCESSOR_DEFEAT_READ` to create the
recovery event, `KEEPER_DEFEAT_REINJECT` to suppress the reinject) and must treat a flat panel 8 as
an open question about the image, not as a proof that the gap cannot open. Research assumption A6
remains **unretired**.

---

## Record 8 — PQ-07: the evidentiary horizon

**Question.** What is the oldest Prometheus sample available at run start?

**Measured answer.** `OldestSampleUtc: 2026-07-19T07:38:06Z`, `RetentionHoursObserved: 225`
(~9.4 days), probed over a `RetentionHorizonHours: 336` window with
`RetentionHorizonLimited: false` — the probe did not hit its own edge, so this is a measurement of
Prometheus and not of the probe.

**Decision.** Nothing branches on this. It is recorded so the artifact states its own horizon.

**Consequence for the scenario plans — and a correction.** `87-FINDINGS.md §14` measured retention at
**~12 h**, and `88-RESEARCH.md` propagated that into a standing constraint ("evidence evaporates;
capture during the run"). The measured value is **roughly nineteen times larger**. The discipline of
capturing evidence into artifacts during the run stays — it is right for reproducibility regardless —
but the *urgency* framing is wrong, and no later plan should compress a measurement window or skip a
settle on the belief that data is about to disappear. Whether Phase 87's number was mis-measured or
retention was changed since is not established here and is not worth a phase's time; the current
value is what this file records, and it should be re-read rather than trusted a month from now.

---

## Scenario status — locked

Each row cites the probe field that decided it. `Dropped` means the scenario will not be authored and
its panel enters the HAND-04 register; `Locked` means the scenario proceeds with the stated lever.

| Scenario | Panels | Status | Lever | Decided by |
|---|---|---|---|---|
| BASE-01 | all 14 (baseline) | **Locked** | none (capture only) | PQ-02 `LocatorModeChosen: viewpanel` + PQ-04 `RateIntervalPinned: true` |
| WEB-01 | 10, 11, 12, 14 | **Locked** | `http` — sustained load ≥ 150 s, plus the confirmed 404 and 400 drivers | PQ-03 `ProbedEndpoints` (404 and 400 observed) |
| WEB-02 | 13 | **Dropped** | — | PQ-03 `Safe5xxFound: false` |
| SCALE-01 | 3, 4, 5 | **Locked** | `scale` — `processor-sample` to 0, restore to the live-read count | PQ-01 scale fault `Ok: true`, `ReplicasRestored: true` |
| SCALE-02 | 1 | **Locked** | `scale` — `orchestrator` to 0, restore to **3** | PQ-01 sequencer proven + `ReplicasBefore` records orchestrator 3 |
| SCALE-03 | 9 | **Locked** | `scale` — `keeper` to 0; predicted direction `nodata` | PQ-02 `TimeseriesLegendParsed: true` (panel 9 is the PQ-02 subject) |
| ZERO-01 | 6 | **Dropped** | — | PQ-05 `UnresolvedRouteChosen: none` |
| ZERO-02 | 2 | **Locked** | `seam` — `PROCESSOR_DEFEAT_READ` set, `KEEPER_DEFEAT_REINJECT` **unset**; arm → settle → re-baseline → trigger | PQ-01 `KeeperRecoveryTrafficObserved: false` |
| ZERO-03 | 8 | **Locked** | `seam` — **both** seams; arm → settle → re-baseline → trigger | PQ-06 `SeamArmLanded: true` + `SeamDisarmClean: true` |
| LADDER-01 | 3, 4 (counter) + 14 (gauge) | **Locked** | `scale` rungs + `http` rungs | PQ-04 `RateIntervalPinned: true` + PQ-01 sequencer proven |

**Net effect on the phase.** Two of the ten scenarios are dropped and their panels (6, 13) join panel
7 in the accepted-unproven register — so **three of fourteen panels will be documented rather than
proven**, and the roadmap's success criterion 2 is met via its "or explicitly recorded as
accepted-unproven with the reason" clause for those three. One scenario (ZERO-02) is more expensive
than planned. Nothing is blocked.

---

## Appendix — the fourteen business panels, verbatim

Recorded so that a future regression in `?viewPanel=panel-<id>` costs a `PANEL_TITLES` list rather
than a rediscovery. Titles are copied byte-for-byte from `k8s/dashboards/business.json`.

| id | title | type |
|---|---|---|
| 1 | `Orchestrator consumed vs sent (conservation)` | timeseries |
| 2 | `Keeper consumed vs sent (conservation)` | timeseries |
| 3 | `Processor consumed vs sent by image (conservation)` | timeseries |
| 4 | `Orchestrator sends minus processor pickups (structural - read the trend)` | stat |
| 5 | `Per-processor dispatch gap` | timeseries |
| 6 | `Orchestrator unresolved steps in range` | stat |
| 7 | `Processor dropped spawns in range` | stat |
| 8 | `Keeper consumed - sent gap in range` | stat |
| 9 | `Keeper L2 probe heartbeat` | timeseries |
| 10 | `WebApi request rate by route` | timeseries |
| 11 | `WebApi p95 request duration by route` | timeseries |
| 12 | `WebApi status-code mix` | timeseries |
| 13 | `WebApi 5xx ratio` | stat |
| 14 | `WebApi in-flight requests and Kestrel connections` | timeseries |

---

## Requirement traceability

No Phase-88 requirement is marked Complete by this plan, and that is deliberate — it follows the
precedent 88-01 set (deviation 3) and 88-02 restated: mark only what was actually proven.

| ID | What this plan contributed | Status after this plan |
|---|---|---|
| DISC-02 | Decided which panels *can* be observed changing, and locked the lever for each. No panel has yet been observed changing. | Pending |
| DISC-04 | Established that panels 2 and 8 need **both** seams, not a bare scale. Neither has been seen non-zero. | Pending |
| DISC-05 | First live exercise of the arm/disarm mechanism, with all three restore claims true. The statement is about *every scenario*, and no scenario exists. | Pending |
| DISC-06 | Recorded a real rollout discontinuity (`SeamRolloutOldPods` → `SeamRolloutNewPods`) and fixed the re-baseline obligation for ZERO-02 and ZERO-03. | Pending |
| HAND-04 | Produced the register's contents: panels 6 and 13, each with its enumerated blocked routes and observed statuses. The register itself is authored in 88-09. | Pending |
