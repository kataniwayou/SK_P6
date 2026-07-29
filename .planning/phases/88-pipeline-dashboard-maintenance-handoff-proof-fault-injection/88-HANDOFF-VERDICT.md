---
phase: 88
slug: pipeline-dashboard-maintenance-handoff-proof-fault-injection
date: 2026-07-29
status: accepted
requirements: [HAND-02]
evidence:
  - analyzer-reports/phase-88-discrimination.json
  - .planning/phases/88-pipeline-dashboard-maintenance-handoff-proof-fault-injection/88-FINDINGS.md
  - docs/runbooks/business-dashboard-runbook.md
verdict: qualified — not ready for an unconditional handoff
---

# Phase 88 — Handoff readiness verdict

## The verdict

**No, the `SKP Business / Pipeline` dashboard is not ready to hand to a maintenance department that
did not build this system — not unconditionally, and not on its own.** It is ready to hand over as
**one instrument among several, accompanied by
[`docs/runbooks/business-dashboard-runbook.md`](../../../docs/runbooks/business-dashboard-runbook.md)
and by this document**, and only once the reachability gap below is closed. Handed over without
them, it would mislead: **thirteen of its fourteen panels mislead by default in at least one specific
way**, and **four of them render a confident green `0` that is indistinguishable from "this counter
has never been emitted at all"**.

That is a qualified verdict, and it is the honest one. The dashboard is not bad — the reverse. Phase
88 moved it a long way:

| | Before Phase 88 (`87-VERIFICATION.md`, *Carried into Phase 88*) | After |
|---|---|---|
| Panels proven to **discriminate** (observed in ≥ 2 states) | **0 of 14** — the dashboards were proven to *render*, never to *discriminate*; ten panels had only ever been seen in a single state | **10 of 14** (ids 1, 2, 3, 4, 5, 9, 10, 11, 12, 14), each with a named proving scenario |
| Panels never observed in more than one state | 10 | **4** (ids 6, 7, 8, 13) — each in the accepted-unproven register with a log route and a statement of what would prove it |
| Panels with a recorded healthy baseline band | none | **14 of 14**, 42 per-series bands over a pinned absolute window |
| Panels with a written next action | none | **14 of 14**, in the runbook |

**There is deliberately no aggregate score in this document, and the practicality rubric it derives
from deliberately has none either.** An average would let a panel that is unreachable *and*
misleading come out as "fine". **The verdict is the blocking-gap list in section (a).** Read that
list; do not look for a number.

Every claim below traces to `analyzer-reports/phase-88-discrimination.json` (the fourteen-row
discrimination matrix, built by `scripts/phase-88-rollup.ps1` from nine scenario artifacts) or to
[`88-FINDINGS.md`](88-FINDINGS.md), whose two derived lists — the panels that mislead by default and
the blocking gaps — **are** sections (a) and (b) here rather than a second opinion about them.

---

## (a) Blocking gaps

Eight, taken from the rubric's question-5 and question-6 failures and from the measured-unsatisfiable
criteria. Stated as gaps, not softened. Each states what would close it and roughly what that costs.

### B1 · The dashboard is not reachable by a maintenance department — **OPEN, and the one that matters most**

`k8s/23-grafana.yaml` ships Grafana as a **ClusterIP Service with no NodePort and no Ingress**, so
the only host reach is a loopback `kubectl port-forward svc/grafana 3000:3000 --address 127.0.0.1`.
Access is **anonymous at org role `Viewer`** (`GF_AUTH_ANONYMOUS_ENABLED=true`,
`GF_AUTH_ANONYMOUS_ORG_ROLE=Viewer`), with **`admin`/`admin` set as plain environment values in the
manifest** (`GF_SECURITY_ADMIN_USER` / `GF_SECURITY_ADMIN_PASSWORD`). There is **no ingress, no TLS
and no SSO**, and Grafana has **no persistent volume** by deliberate design (DASH-03 — the absence of
persistence *is* the requirement).

This is an **accepted development posture**, not an oversight: auth hardening and ingress are
explicitly out of scope per `REQUIREMENTS.md`, it matches the unauthenticated in-cluster Prometheus
and Elasticsearch exactly, the anonymous role cannot mutate anything, and `allowUiUpdates: false`
rejects a provisioned-dashboard save outright. **Fixing it is out of scope for this milestone.**

**It is nonetheless a blocking gap for a genuine handoff, and it will not be softened here.** A
maintenance department cannot watch a dashboard that requires cluster credentials, a `kubectl`
binary, a terminal held open, and physical presence at one workstation. **Rubric question 6 —
"is there a documented access path a maintenance department can actually use?" — is `no` on all
fourteen panels, without a single exception.** That is the only column in the rubric that fails
uniformly.

*What would close it:* an Ingress (or a NodePort behind an existing reverse proxy) plus real
authentication — at minimum removing anonymous Viewer and moving the admin credential out of the
manifest; ideally SSO against whatever the maintenance department already uses. *Rough cost:* a
manifest-and-secrets phase, not a code phase — small if an ingress controller and an identity
provider already exist in the target environment, and a genuine project if they do not. **It is a
prerequisite for handover, not a nice-to-have.**

### B2 · There was no runbook — **CLOSED by this plan**

When `88-FINDINGS.md` was written, no panel on this dashboard had a written action, and the rubric's
own tests for questions 2 and 4 named a runbook that did not exist. **That gap is now closed:**
`docs/runbooks/business-dashboard-runbook.md` exists, covers all fourteen panels, and every row cites
its proving scenario id and the measured before/after values. It is authored in `docs/` — the repo's
only documentation tree — rather than under `.planning/`, precisely so the runbook does not become
part of B1.

### B3 · Panel 8 cannot show the failure mode its own title names — **OPEN and BLOCKED**

`Keeper consumed - sent gap in range` promises a conservation gap. Requirement **DISC-04 is
`Partial` and blocked**, and this is not outstanding work — it is measured-unsatisfiable under this
milestone's constraints, three independent ways (ZERO-03):

1. The Phase-79 seam is **telemetry-byte-identical by design**: `ReinjectConsumer.cs:106-116` calls
   `CountSent(...)` **unconditionally on both branches**, so the keeper counts a send it did not
   make. Measured over the same window at 120 s: consumed `0, 2.0001, 0, 0, 2.0024` and sent
   `0, 2.0001, 0, 0, 2.0024` — term for term identical while messages were being dropped.
2. The only branch that *does* open a gap — the `ReinjectConsumer` drop path, which returns before
   `CountSent` — is unreachable by any lever this phase was allowed to pull.
3. At a 60 s range **both `increase()` terms return zero points** and the `or vector(0)` guard
   supplies the `0` outright. The green an operator reads at a short range measured nothing.

**Roadmap Success Criterion 4 is not met and cannot be met inside this milestone.**

*What would close it:* allow-listing `KEEPER_REINJECT_DELAY_MS` plus a shortened
`EXECUTION_DATA_TTL` — the exact combination the repo's own FALSIFY-02 control already uses to force
a genuine backup expiry and reach the drop branch (*cheap: no `src/` change, one plan*); or a
**separate, additive** keeper seam that suppresses the redispatch without calling `CountSent` — which
must not be an edit to the existing branch, because moving `CountSent` inside the `if` would destroy
the Phase-79 FALSIFY-01 negative control (*a `src/` change*); or an infrastructure fault that
exhausts a processor/orchestrator Redis DELETE and reaches the delete-class consumers, which never
call `CountSent` at all.

### B4 · Four panels have never been observed non-zero, and their green `0` is ambiguous — **OPEN**

Panels **6, 7, 8 and 13**. All four are `or vector(0)`-guarded, so a `0` is indistinguishable from
"the counter has never been emitted at all". Two are measured-unreachable (6, 13), one is
measured-unreachable three ways (8), and one is **user-locked** (7). **Nothing on the dashboard
distinguishes "healthy" from "never instrumented" for any of them.**

*What would close it:* per panel — 6 needs an orchestrator-side test seam that can inject an
unresolved id after activation (*a `src/` change*); 7 needs a bounded send-endpoint seam inside
`SpawnToPost` **and the user's consent** (*a `src/` change plus a decision*); 8 is B3; 13 needs
either a budgeted dependency-outage route (accepting a WebApi pod restart, an L2 wipe and a full
re-baseline, sequenced last in its phase) or an env-gated faulting diagnostic endpoint (*a `src/`
change*). A cheaper partial mitigation for all four: a companion panel per counter showing
`absent(metric)` or a series-existence indicator, which would separate "zero" from "never emitted"
without any `src/` change — *a dashboards phase, not this one.*

### B5 · Short faults are invisible — **OPEN, and it is arithmetic, not a defect**

Measured on a step-ladder of controlled outages (`analyzer-reports/phase-88-LADDER-01.json`):

- A **processor-tier outage shorter than 240 s does not register on panel 3 at all** — and that
  number is **double** what the research predicted. A queued pipeline hides a short outage from a
  rate panel: the broker buffers while the tier is at zero replicas and the tier drains the backlog
  inside the same 240 s rate window.
- **A 30-second total outage of the processor tier produced a dip of one tenth of one percent.** On
  the dashboard, it did not happen.
- The 120 s rung produced a visible 33 % drop and still missed the two-consecutive-samples rule **by
  exactly one sample**.
- A **WebApi condition shorter than 120 s cannot register on panel 14** by arithmetic — a gauge
  exported every 60 s cannot record an event between two exports.

*What would close it:* nothing, at this export cadence. It is a property of a 60 s metric export and
a 240 s rate window meeting a buffered pipeline. Shortening the SDK export interval would trade
resolution against storage and against every band in this phase. **The correct response is the one
taken: state the numbers in the runbook so nobody reads a clean dashboard as proof that a brief
outage did not occur.**

### B6 · A real recovery event can leave panel 2 at an unbroken zero — **OPEN**

`Keeper consumed vs sent (conservation)` uses `rate()`, which differences consecutive 60 s samples.
ZERO-02 run 1 drove a genuine recovery event — both replicas claimed, three `KeeperReinject`s
flowed, the keeper consumed and sent all three — and **the panel rendered `0` throughout**, because
the counter series is born already carrying its final value. The operator's reliable signal is the
keeper's `REINJECT sent` log line, **not the panel**. (The discarded run is kept on disk as
`analyzer-reports/phase-88-ZERO-02-run1-discarded.json` precisely so this finding stays citable.)

*What would close it:* switching panel 2 from `rate()` to `increase(...[$__range])`, which is the
form the guarded stat panels already use and which does not decay back. *Rough cost:* a one-line
expression change — **but it belongs to a phase that owns `k8s/dashboards/business.json`, not to
this one**, and it would change the panel's units and its band.

### B7 · Two panels go blank under exactly the faults they exist to reveal — **OPEN, recorded and deliberately not fixed**

Panel 4 renders **"No data"**; panel 5 **decays to nothing without ever spiking**. An operator
watching for a number to climb sees a panel disappear. The runbook's rows for both say so in their
first sentence — *blank is the signal* — which is the correct mitigation given that the underlying
expressions are right. See section (b) for why neither was changed.

### B8 · Thirteen Class-A series have no live signal to move away from — **OPEN**

BASE-01 recorded them as findings rather than bands, because they banded at **exactly zero across all
ten sub-windows on a demonstrably healthy stack**: seven of the thirteen route series on panel 10,
four of the six status series on panel 12 (`201`, `400`, `404`, `422`), and two of panel 14's three
series. **A series banded at exactly zero is not a valid null hypothesis** — there is nothing for a
fault to move it away from — so those thirteen series are unproven, and `BASE-01`'s own verdict is
`Inconclusive` because of them (`AllBandsComputed: false`).

*What would close it:* nothing, and nothing should. These are idle routes and unused status codes on
a dev cluster whose pipeline runs on a cron rather than on HTTP — **the zeros are true**. The
limitation is on the evidence, not on the dashboard. It would close itself in an environment that
actually exercises those routes.

---

## (b) Panels that mislead by default

**Thirteen of fourteen.** Panel 1 is the only exclusion and it earns it: its series are unguarded,
they carry a live non-zero level on a healthy stack, and under a real orchestrator outage SCALE-02
measured both legend means **falling visibly** rather than emptying or holding a comfortable value.
Its one caveat is that a 240 s outage did **not** reach "No data" — what an operator sees is a fall,
not a blank.

**Every entry below is RECORDED, NOT FIXED.** No dashboard JSON was changed in this phase, and that
is a decision rather than an omission: a proof-and-handoff phase that edits the artefact it is
grading cannot grade it, and editing a panel to make a finding disappear would have destroyed the
evidence the phase exists to produce. Each entry states its own reason.

| Panel | How it misleads | Measured in | Decision |
|---|---|---|---|
| 2 · `Keeper consumed vs sent (conservation)` | The `or vector(0)` guard makes a green `0` ≡ "never emitted"; the `rate()` form is **blind to a recovery burst confined to one 60 s export interval**, so a real recovery may leave the panel at zero; and the guard row does not disappear when the real series arrives, so a phantom `consumed` at 0 renders beside the real `consumed keeper` | ZERO-02 (both runs) | **Recorded, not fixed** — the `rate()`→`increase()` change is a dashboard-owning phase's call (gap B6) |
| 3 · `Processor consumed vs sent by image (conservation)` | The fall is a **four-minute slope, not a cliff**, and its first in-fault sample still reads inside the healthy band: 0.737 → 0.632 → 0.447 → 0.169 → 0 across five minutes of a *total* outage, because the 240 s rate lookback smears the fault | SCALE-01, LADDER-01 | **Recorded, not fixed** — the lookback is derived from the datasource `timeInterval` and correcting it downward would empty every rate panel (the Phase-87 defect) |
| 4 · `Orchestrator sends minus processor pickups (structural - read the trend)` | A **structural offset that can never read 0** (recorded at a wide-window level of 2.11 over a 3600 s window on a healthy stack) — **and it goes blank under exactly the fault it exists to reveal**, because `A − empty` is empty. The stat whose own title says "read the trend" renders "No data". It also cannot be evaluated at all at a 60 s range | BASE-01, SCALE-01 | **Recorded, not fixed** — the expression is correct; making it read zero needs the orchestrator to label its send destinations, a `src/` change the panel's own description already names as out of scope |
| 5 · `Per-processor dispatch gap` | A structural offset **and** it **decays and empties rather than spiking**: 0.63 → 0.588 → 0.4 → 0.234 → 0.183 → "No data". The research model assumes an independent producer; this pipeline is request/response, so the orchestrator's own send rate falls with the processors and the gap shrinks before the panel blanks | SCALE-01 | **RECORDED, NOT FIXED — the phase's most deliberate non-change.** `87-FINDINGS.md` §10 establishes the `processorId` grouping is **correct as shipped** and measured that substituting `identityName` does not degrade the panel, it **empties** it (1 series → 0, because PromQL's binary `−` finds no matching label set). The emptiness is a rendering consequence of a correct expression meeting a request/response pipeline, not a wiring error. Both alternatives are out of scope: the orchestrator stamping `identityName` on its send counter is a `src/` change, and a Grafana `processorId`→name transformation would hard-code database ids into the JSON and break DASH-04 portability. The runbook row reads *"panel 5 going blank IS the signal"* |
| 6 · `Orchestrator unresolved steps in range` | Guarded green `0` ≡ "`orchestrator_step_unresolved_total` has never been emitted" | ZERO-01 | **Recorded, not fixed** — log route `Dangling next-step id` given instead |
| 7 · `Processor dropped spawns in range` | Guarded green `0` ≡ "`processor_spawn_dropped_total` has never been emitted" | ZERO-03 (as an exactly-zero cross-talk control) | **Recorded, not fixed** — log route `SpawnSendExhaustedException` given instead |
| 8 · `Keeper consumed - sent gap in range` | **The worst of the fourteen, measured three ways:** the guard supplies the `0` from an expression returning zero points at 60 s; the gap arithmetic cannot show a suppressed redispatch at all; the only branch that opens a gap is unreachable | ZERO-03 | **Recorded, not fixed** — see gap B3; log route `REINJECT drop` given instead |
| 9 · `Keeper L2 probe heartbeat` | **The axis lies about the variance.** A metronome (mean exactly 0.2000 per pod, true spread ≈ 0.0006 wide) auto-scaled to fill the panel renders as apparent volatility. Under a dead keeper it **fades for ~3 minutes** before "No data" rather than dropping out. Its description quotes *"a flat 0.4 probes/sec"* — the **two-pod sum** — while the panel renders **per pod at 0.2** | BASE-01, SCALE-03 | **Recorded, not fixed** — the description defect is queued for whichever phase next owns `business.json`; the runbook states both numbers |
| 10 · `WebApi request rate by route` | The row that moves most cleanly is rendered **`Value`** (an unmatched path carries no `http_route` label) with nothing on the panel to say what it is; and 7 of 13 route series are permanently flat at zero | BASE-01, WEB-01 | **Recorded, not fixed** — the label genuinely does not exist for an unmatched path |
| 11 · `WebApi p95 request duration by route` | **The signal is the leading edge, not the level** — p95 spikes at load onset and decays back **inside** the healthy band within three minutes. A reader watching for a sustained shift sees nothing. The band is also wide and partly negative (one series −5.06..21.83 ms) | WEB-01 | **Recorded, not fixed** — the API is genuinely fast once warm; the panel is honest, the reading habit is the trap |
| 12 · `WebApi status-code mix` | **The status series never leave the legend.** All six were present and rendering zero before anything drove them; a code that has occurred once never disappears, so the legend cannot be read as "this code is happening" — only the value can | BASE-01, WEB-01 | **Recorded, not fixed** — Prometheus retains a counter series for the life of the emitting process |
| 13 · `WebApi 5xx ratio` | Guarded green `0` ≡ "no 5xx counter has ever been emitted"; the panel becomes honest only **after** the first 5xx ever occurs, and nothing on it says which side of that date you are on | WEB-02, wave-0 probe PQ-03 | **Recorded, not fixed** — the blocker is the product being well-behaved across seven enumerated endpoints |
| 14 · `WebApi in-flight requests and Kestrel connections` | **One of its three series is a dead instrument under real load.** `http_server_active_requests` read exactly 0 at every one of six export ticks while ten parallel requesters issued 2456 requests over 480 s. Its description blames idleness; **the cause is the 60 s gauge export cadence**. `kestrel queued` is likewise flat at zero. The only readable series is `kestrel active` — a **connection** count kept alive by HTTP keep-alive, a different kind of quantity from the title's "in-flight requests" | WEB-01 | **Recorded, not fixed** — the description defect is queued for whichever phase next owns `business.json` |

---

## (c) Accepted-unproven status, per panel

Copied from `analyzer-reports/phase-88-discrimination.json`. `Proven` means the panel was observed in
**two or more states** with at least one of them driven by a real fault and asserted from the
rendered panel.

| Panel | Status | Proven by / reason source | Observed states |
|---|---|---|---|
| 1 · `Orchestrator consumed vs sent (conservation)` | **Proven** | SCALE-02 | steady, depressed |
| 2 · `Keeper consumed vs sent (conservation)` | **Proven** | ZERO-02 | zero, nonzero |
| 3 · `Processor consumed vs sent by image (conservation)` | **Proven** | SCALE-01 | steady, depressed, NoData |
| 4 · `Orchestrator sends minus processor pickups (structural - read the trend)` | **Proven** | SCALE-01 | steady, NoData, depressed |
| 5 · `Per-processor dispatch gap` | **Proven** | SCALE-01 | steady, NoData |
| 6 · `Orchestrator unresolved steps in range` | **AcceptedUnproven** | ZERO-01 — both routes **measured** closed: the API refuses a dangling `nextStepIds` entry with 422 at stage `step-create`, and the L2 step key is rewritten from Postgres by any stop/start cycle | zero only |
| 7 · `Processor dropped spawns in range` | **AcceptedUnproven — BY USER DECISION, NOT BY MEASUREMENT** | **user decision, locked at phase level**, recorded in `88-PROBE-DECISIONS.md` and in every plan since. **No broker-fault scenario was designed for it, deliberately** — that deliberateness *is* the decision, not a gap in the evidence. Its zero is genuine: it was read at exactly `0` as a cross-talk control during ZERO-03 while a real fault fired elsewhere. Worth recording for whoever revisits it: the hazard that motivated the lock — the keeper/processor crash-loop under an unreachable broker — **was resolved in v12.0.0 Phase 86** and proven live at RESTARTS 0, so a future attempt is cheaper than it was. **The decision stands until the user changes it, and this phase does not reopen it** | zero only |
| 8 · `Keeper consumed - sent gap in range` | **AcceptedUnproven** | ZERO-03 — measured-unreachable three independent ways; the fault fired exactly as designed and the gap still could not open. Carries the phase's only escalation | zero only |
| 9 · `Keeper L2 probe heartbeat` | **Proven** | SCALE-03 | steady, NoData |
| 10 · `WebApi request rate by route` | **Proven** | WEB-01 | steady, elevated, zero |
| 11 · `WebApi p95 request duration by route` | **Proven** | WEB-01 | steady, elevated |
| 12 · `WebApi status-code mix` | **Proven** | WEB-01 | steady, elevated, zero |
| 13 · `WebApi 5xx ratio` | **AcceptedUnproven** | WEB-02 — seven endpoints enumerated, **no 5xx on safe input** (five 404, one 400, one 422). A good result about the product and a blocking one for this panel; the dependency-outage route was **not** substituted because it costs a WebApi pod restart under the Phase-86 hard readiness latch and would have invalidated every later band | zero only |
| 14 · `WebApi in-flight requests and Kestrel connections` | **Proven** | WEB-01 | steady, elevated, zero |

**10 Proven · 4 AcceptedUnproven.** The two sets are partitions of the same fourteen, derived from
one file, so they cannot drift apart.

---

## (d) The nine roadmap success criteria

| # | Criterion (abridged) | Verdict | Evidence, and what is missing where it is not a PASS |
|---|---|---|---|
| 1 | Every panel has a recorded healthy baseline band from a settled stack | **PARTIAL** | `analyzer-reports/phase-88-BASE-01.json` — 42 per-series bands across **14/14 panels** over the pinned absolute window `17:41:52Z`→`17:51:52Z` at 1920x1080. **Missing:** thirteen Class-A series band at exactly zero and are recorded as `UnevaluablePanels[]` findings rather than bands (gap B8), and **panel 4 cannot be banded at 60 s at all** — `increase()` needs ≥ 2 samples at a 60 s stored resolution — so it is banded at 120 s. The artifact's own verdict is `Inconclusive` with `AllBandsComputed: false`. Rounding this to PASS would hide that a fifth of the series carry no usable null hypothesis |
| 2 | Each of the fourteen panels observed changing under its fault, **or** recorded accepted-unproven with a reason | **PASS** | `analyzer-reports/phase-88-discrimination.json` — **10 Proven** each with a `provenBy` scenario, **4 AcceptedUnproven** each with a non-empty reason and a register entry in `88-FINDINGS.md` §1. Noted rather than glossed: the criterion's escape clause says "could not be driven **safely**", and three of the four reasons are stronger than that — panels 6, 8 and 13 could not be driven **at all**, which was measured rather than assumed |
| 3 | Each scenario carries a cross-talk control asserting the panels it should not affect | **PASS** | **16/16** controls across the SCALE-*/WEB-*/ZERO-* scenarios and **3/3** on LADDER-01, every one at `MaxExcursion` exactly **0.0000**; no scenario shipped an empty list. Amended in the open: ZERO-01 and LADDER-01 score their controls against a **local** reference band rather than the DISC-01 band, because the DISC-01 bands were captured hours earlier under a host load that was not running and the comparison would report a change in *conditions* as drift (`BandSource`, `CrossTalkNote`) |
| 4 | Panels 2 **and** 8 seen non-zero during a real recovery event | **FAIL** | Half met, and the other half is **measured-unsatisfiable**. `analyzer-reports/phase-88-ZERO-02.json`: **panel 2 proven non-zero** in a real keeper recovery event, 4 consecutive sub-windows off a `0..0` band. `analyzer-reports/phase-88-ZERO-03.json`: **panel 8 could not be moved** — the seam is telemetry-byte-identical by design, no allow-listed lever reaches the branch that opens a gap, and at 60 s the guard supplies the value. **The criterion as written asks for something the product deliberately prevents.** Requirement DISC-04 is recorded `Partial — blocked`. See gap B3 |
| 5 | A repeatable runtime-only seam arm/disarm mechanism, editing no manifest, leaving the stack byte-identical | **PASS** | `scripts/lib/phase-88-cluster-ops.ps1` with a static `(tier, variable)` allow-list; disarm from the **outer `finally`**; `SeamVarsClean` / `ReplicasRestored` / `ImagesUnchanged` all `true` with empty mismatch arrays on **every** scenario artifact and every ladder rung, corroborated by independent `kubectl` re-reads. Exercised end to end in ZERO-03 — armed, re-baselined, fault driven, disarmed, stack re-asserted. `kubectl apply -k k8s/` was never run and no manifest was edited |
| 6 | Every seam- or scale-dependent scenario re-captures its baseline after the rollout settles, discontinuity recorded as an expected artifact | **PASS** | `BaselineRecaptured` / `SeamRolloutOldPods` / `SeamRolloutNewPods` on SCALE-01/02/03, ZERO-02, ZERO-03 and all eight ladder rungs, each with a non-empty **disjoint** pod-identity pair. Strengthened past the criterion by `RebaselineInertnessProven`, which asserts per run that every Regime-B subject band is still exactly `0..0` **with the seam armed**, so a contaminated re-baseline could not pass unnoticed |
| 7 | The minimum detectable fault duration is measured and recorded | **PASS** | `analyzer-reports/phase-88-LADDER-01.json` — eight real rungs: **counter 240 s**, **gauge 120 s**, plus the stated Regime-B any-duration property. The research predicted 120 s for both; the gauge prediction holds and **the counter prediction is falsified**. Both predictions are recorded beside both measurements (`PredictionAgreesWithMeasurement: false`) and the theory was **not** adjusted to fit — the 240 s rung's divergence is carried as a finding and is the sole reason the artifact's verdict is `Inconclusive`. The Inconclusive says the *model* was wrong, not the measurement |
| 8 | A symptom → panel → action runbook exists, every row traceable to the scenario that proved it | **PASS** | `docs/runbooks/business-dashboard-runbook.md` — all fourteen panels, ten rows proved by a driven fault and four accepted-unproven rows naming the log line to search instead. Every row cites its scenario id and the measured before/after values; the header states the reachability, retention, pod-dropdown-superset and minimum-duration caveats. A row with no backing scenario would have been rejected |
| 9 | A handoff readiness verdict is recorded, listing blocking gaps and every panel that misleads by default | **PASS** | This document — section (a) eight blocking gaps, section (b) thirteen misleading-by-default panels each with its measuring scenario and its decision, section (c) per-panel accepted-unproven status. Both lists are the rubric's own derived output in `88-FINDINGS.md`, not a second judgement about it |

**7 PASS · 1 PARTIAL · 1 FAIL.** Criterion 4 is the FAIL, it is blocked rather than outstanding, and
it will not close inside this milestone.

**What the score does not say, and should:** three runs were discarded for defects in scoring or
drive code — never for producing a worse number — and all three are kept on disk beside their
replacements. Six acceptance criteria across the phase were contradicted by measurement and were
**amended with their evidence rather than reworded to match the outcome**. Four of the nine scenario
artifacts carry a verdict of `Inconclusive`. **This is what a phase that measured honestly looks
like; it is not what a phase that passed cleanly looks like, and the difference is the point.**

---

## Conditions for handoff

Concrete, in the order they bind. Each is either satisfied by this phase or named as outstanding.

1. **The dashboard is reachable without a `kubectl port-forward`.** — **OUTSTANDING (B1).** This is
   the hard prerequisite. Everything else on this list is worth doing only if a maintenance
   department can open the page.
2. **Anonymous Viewer and the in-manifest `admin`/`admin` are replaced by real authentication.** —
   **OUTSTANDING (B1).** Acceptable on loopback; not acceptable the moment condition 1 is met.
3. **The runbook is handed over with the dashboard, not after it.** — **SATISFIED.**
   `docs/runbooks/business-dashboard-runbook.md`. A green panel on this dashboard is not
   self-explanatory and the runbook is what makes it readable.
4. **This verdict is handed over with the runbook.** — **SATISFIED.** An operator who reads only the
   runbook will not know that panel 8 should be ignored or that DISC-04 is blocked.
5. **Whoever receives it is told, in the handover conversation and not only in a document, that a
   green `0` on panels 6, 7, 8 and 13 means "no evidence", not "no failures".** — **SATISFIED in
   writing** (runbook §5.2, §6; this document sections (a) B4, (b), (c)); **the conversation is
   outstanding** and is what plan 88-11's sign-off exists for.
6. **Whoever receives it is told that a fault shorter than 240 s (counters) or 120 s (gauges) may not
   appear at all.** — **SATISFIED in writing** (runbook §4, gap B5). Nothing can close the underlying
   arithmetic; only the expectation can be set.
7. **Panel 8 is treated as non-authoritative until DISC-04 is closed.** — **OUTSTANDING (B3).** The
   keeper's `REINJECT drop` log line is the authoritative signal in the meantime, and the runbook
   says so.
8. **A dashboards-owning phase picks up the queued panel-description defects** — panel 9's tooltip
   quoting the two-pod sum while the panel renders per pod, and panel 14's tooltip blaming idleness
   for a zero the export cadence causes. — **OUTSTANDING.** Both are documented; neither was fixed
   here, because a proof phase that edits the artefact it grades cannot grade it.
9. **Nobody quotes a band from `phase-88-BASE-01.json` as an alert threshold without re-measuring.**
   — **SATISFIED in writing** (runbook §3). Every band was captured under specific conditions, and
   this phase measured directly that comparing across conditions reports a change in conditions as
   drift.

---

## Sign-off

**Date:** 2026-07-29
**Signed by:** the GSD continuous-execution chain — **an automated agent, not a person** — acting on
the user's standing instruction that the remaining waves of Phase 88 run unattended.
**Class of sign-off:** **machine-approved / unattended.** This is a **procedural** record that the
checkpoint was cleared so the phase could close. **It is not a human judgement about the runbook or
about this verdict, and it must never be quoted as one.**

### What this sign-off is not

Phase 87 carried forward a note that its human checkpoint was signed off by instruction rather than
by a reviewer walking the panels. Plan 88-11 exists so Phase 88 does not repeat that silently. It is
repeated here, and stated more plainly than Phase 87 stated it:

- **No human read `docs/runbooks/business-dashboard-runbook.md` or this document before this section
  was written.** The approval was issued by an automated continuous-execution chain, on a standing
  instruction given before either deliverable existed. **A standing instruction to keep running is
  not a reading of what was produced.**
- **A human review of both deliverables remains OUTSTANDING.** This section does not supersede it —
  this section *is* the record that it has not happened.
- **An unattended approval is not evidence about the dashboard.** Nothing above is upgraded,
  softened or re-graded by it. The verdict stands exactly as written: **qualified — not ready for an
  unconditional handoff**, 8 blocking gaps, 13 of 14 panels misleading by default, 10 Proven /
  4 AcceptedUnproven, 7 PASS / 1 PARTIAL / 1 FAIL, DISC-04 `Partial` and blocked.
- **No requirement is marked `Complete` on the basis of this approval.** HAND-01 and HAND-02 were
  graded `Complete` by plan 88-10, on the stated grounds that the documents exist and the verdict is
  honest — not that the dashboard is handover-ready. `REQUIREMENTS.md` is unchanged by plan 88-11.
- **Condition 5 in the list above is NOT satisfied by this section.** It requires that whoever
  receives the dashboard is told *in the handover conversation* that a green `0` on panels 6, 7, 8
  and 13 means "no evidence". No conversation took place. Four of the nine conditions for handoff
  (1, 2, 7, 8) remain outstanding, and the two that matter most — an ingress and real
  authentication — are out of scope for this milestone.

### Which of the seven checks were actually performed

Plan 88-11's checks 1–7 ask for a reviewer's judgement. This table records what was executed, by
what, and what was not.

| # | Check | Performed? | By what, with what result |
|---|---|---|---|
| 1 | Every runbook row's *Proving scenario* cell names a scenario id; reject any row with none | **Partially — mechanically** | All **15** runbook table rows (11 in §5.1, 4 in §5.2) carry either a scenario id matching `(BASE\|WEB\|SCALE\|ZERO\|LADDER)-\d\d` or an explicit accepted-unproven citation. **Nothing was rejected because nothing was rejectable.** Whether a cited scenario genuinely *proves* its row was **not** judged |
| 2 | Open two rows' cited artifacts and confirm the before/after numbers match | **Yes — mechanically, two rows** | Panel 9 against `analyzer-reports/phase-88-SCALE-03.json`: baseline `0.2` ×6, after `0.197, 0.203, 0.168, 0.129, 0.0644`, band `0.18–0.22`, 3 consecutive samples outside, 3 trailing `NoData` — **matches the runbook exactly**. Panel 14 against `analyzer-reports/phase-88-WEB-01.json`: `kestrel active` `2.5, 4.5, 5, 5, 3.5, 4.5` with `in-flight` and `kestrel queued` at `0,0,0,0,0,0`, envelope `−0.207..1.207`, 6 consecutive samples outside — **matches the runbook exactly**. Caveat: the two rows were chosen by the agent, not drawn at random by a reviewer |
| 3 | Could someone who did not build this system reach the dashboard, know the data horizon, and know what outage length it cannot show — from the header alone? | **NO — NOT PERFORMED** | A judgement that requires a reader who did not build the system. The agent that parsed the header is not that reader, and it cannot stand in for one |
| 4 | Is section (a) plain or softened — is the reachability gap called a blocking gap rather than an accepted posture? | **NO — NOT PERFORMED** | A judgement about tone and honesty. The agent that would grade it is the same chain that wrote the section; that is not an independent read |
| 5 | Every `AcceptedUnproven` panel names a reason, and panel 7's entry tells maintenance what to do instead | **Yes — mechanically** | All four `AcceptedUnproven` rows carry a non-empty `reason` in `analyzer-reports/phase-88-discrimination.json` (panel 6: 1893 chars, 7: 921, 8: 4538, 13: 1336); the ten `Proven` rows carry none, as designed. Panel 7's runbook row contains all four actions the check names — **processor logs**, **`SpawnSendExhaustedException`**, **nack-requeue re-fire**, **broker health**. Whether that is *actionable enough for a maintenance department* was **not** judged |
| 6 | For each PARTIAL or FAIL criterion, is the shortfall named rather than rounded up? | **NO — NOT PERFORMED** | A judgement call, and the one most likely to catch a self-serving grade |
| 7 | Open Grafana and walk two or three panels against the runbook's healthy bands | **NO — NOT PERFORMED** | Out of scope: plan 88-11 is documentation-only and the executing agent was instructed not to drive the live cluster |

**Three of seven performed, and all three are the mechanical ones. The four not performed — 3, 4, 6
and 7 — are precisely the judgements plan 88-11 exists to obtain.** The pre-flight gate passed
(`checkpoint pre-flight ok`): all four reviewer inputs exist and the matrix is fourteen complete rows
with no unexplained accepted-unproven entry.

### Reservations

**None were raised, because no reviewer was present to raise any.** That is not "no reservations" —
it is **an absence of review**, and the two are not interchangeable. No finding was rejected, no
runbook row was sent back, and no document under review was edited by this plan: the only change plan
88-11 made to any file under review is this Sign-off section itself.

### What would close this

A **named human** reading `docs/runbooks/business-dashboard-runbook.md` and this document, performing
checks 3, 4 and 6 — and ideally 7 — and appending a second, attributed sign-off **below** this one
with their findings, including anything they reject. Until that exists, the honest state of the
record is: **the two deliverables are complete, internally consistent and mechanically verified
against their artifacts; nobody has yet judged whether they are usable by a team that did not build
this system.**

---

*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection · Written: 2026-07-29*
*Derived from `analyzer-reports/phase-88-discrimination.json` and `88-FINDINGS.md` §2's two derived
lists. Phase 88 changed no `src/` file, no manifest and no dashboard JSON.*
