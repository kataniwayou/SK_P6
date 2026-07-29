---
phase: 88
slug: pipeline-dashboard-maintenance-handoff-proof-fault-injection
date: 2026-07-29
status: accepted
requirements: [DISC-01, DISC-02, DISC-03, DISC-04, DISC-05, DISC-06, DISC-07, HAND-03, HAND-04]
findings: [F-1, F-2, F-3, F-4, F-5, F-6, F-7, F-8, F-9, F-10, F-11]
evidence: analyzer-reports/phase-88-discrimination.json
---

# Phase 88 — Accepted-Unproven Register and Practicality Rubric

Two records a maintenance handoff is judged on: **what could not be proven and why**, and **how
practical each panel actually is**. Both are derived from one artifact —
`analyzer-reports/phase-88-discrimination.json`, the fourteen-row discrimination matrix built by
`scripts/phase-88-rollup.ps1` from the nine scenario artifacts. The matrix is the single source of
truth; this document is the matrix read for a human, not a second opinion about it.

The genre and the fixed headings are borrowed from
[`87-FINDINGS.md`](../87-grafana-observability-dashboards/87-FINDINGS.md): every record answers
**Asked for → What actually exists → What shipped instead → Why it is legitimate rather than a
shortfall**, and closes by **citing the artifact that measured it**. That discipline is what stops
an accepted limitation from drifting into an unexamined excuse.

## The headline numbers

| | |
|---|---|
| Panels **Proven** to discriminate | **10 of 14** (ids 1, 2, 3, 4, 5, 9, 10, 11, 12, 14) |
| Panels **AcceptedUnproven** | **4 of 14** (ids 6, 7, 8, 13) |
| Panels **misleading by default** | **13 of 14** — every row except panel 1 |
| Rows carrying a **discarded run** | 14 of 14 (three discarded runs, all committed) |
| Rows carrying a **measured-unsatisfiable acceptance criterion** | 6 of 14 |
| Rows carrying an **escalation** | 1 of 14 (DISC-04, on panel 8) |

**The run window.** Wave-0 probe `2026-07-28T19:49:14Z` → LADDER-01 completion
`2026-07-29T02:47Z` — roughly seven hours of live measurement against the `skp` Docker-Desktop
cluster at `:tags-const-1544`, replicas 1/2/3/2, viewport 1920x1080, `LocatorMode: viewpanel`,
`RateIntervalSeconds: 240`, datasource `timeInterval: 60s`. The DISC-01 bands were captured over the
pinned absolute window `2026-07-28T17:41:52Z` → `17:51:52Z`
(`analyzer-reports/phase-88-BASE-01.json`, 42 per-series bands across 14/14 panels).

**Panel 1 is the only row the matrix does not flag as misleading by default, and that single
exception is what stops the flag from being a rubber stamp.**

---

# Section 1 — the HAND-04 accepted-unproven register

Exactly four records, one per matrix row whose `status` is `AcceptedUnproven`. The register and the
`Proven` set are two partitions of the same fourteen; they are derived from the same file so they
cannot drift apart (REQUIREMENTS.md: *"the two cannot drift apart"*).

---

## 1. Panel 6 — `Orchestrator unresolved steps in range`

**Asked for** (DISC-02): observe the panel changing in the predicted direction — a guarded green `0`
going non-zero and red — under a dangling next-step edge, outside its DISC-01 band for ≥ 2
consecutive 60 s samples.

**What actually exists:** one observed state, `zero`. The matrix records
`observedStates: ["zero"]` from three independent readings — the BASE-01 band (`0..0` over ten 60 s
sub-windows), ZERO-01's six observation windows (`0,0,0,0,0,0`), and a ZERO-03 cross-talk control
that held at `MaxExcursion` exactly `0.0000`. The panel has **never been proven to render
non-zero**, because the counter is unreachable from outside `src/`. Both routes were enumerated and
measured, not reasoned about:

| Route | Measured outcome |
|---|---|
| **A — API dangling edge** | `POST /api/v1/steps` returns **422 at stage `step-create`** for a `nextStepIds` entry naming a non-existent step. Refused *earlier* than the planning analysis predicted (`CycleDetector`'s D-08 missing-step gate was expected to reject it at activation). The graph can never be brought into the state this route needs. |
| **B — L2 step-key deletion** | The non-entry step's key `skp:{workflowId}:{stepId}` was **present after activation, absent after a `DEL`, and present again after one stop/start cycle**. `OrchestrationService.StartAsync` ends in `IRedisProjectionWriter.UpsertAsync`, which rewrites the whole snapshot from Postgres, so the hole closes before the orchestrator's hydration BFS could ever miss the step. |

**What shipped instead — the log-corroboration route maintenance should use.** A non-zero panel 6
corresponds **one-for-one** with the orchestrator emitting

```
Dangling next-step id {NextStepId} — skipping (business)
```

(`src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:184`) in the same window — one log line per
increment, because stage 3 increments `orchestrator_step_unresolved` once per entry in
`selection.UnresolvedIds`. Maintenance should:

1. Search the orchestrator logs for that string over the panel's range **before** trusting a
   non-zero reading.
2. Read a green `0` as **"either no dangling edge occurred OR the counter has never been emitted at
   all"** — this phase could not tell those apart.
3. Treat a non-zero reading as **credible**: the increment site is graceful (never throws, always
   acks), so nothing suppresses it once it does fire.

**Why it is legitimate rather than a shortfall.** The counter is genuinely unreachable without a
`src/` change, and this phase's locked constraint forbids one. **No substitute fault was invented**,
on the WEB-02 precedent — inventing a different fault and scoring the panel against it would have
produced a proof of something other than what the requirement asks for.

**What a future phase would need to prove it.** An orchestrator-side test seam analogous to the
Phase-79 `PROCESSOR_DEFEAT_READ` / `KEEPER_DEFEAT_REINJECT` pair — env-gated, default off — that can
inject an unresolved id into `selection.UnresolvedIds` after activation. That is a `src/` change and
therefore a decision for a milestone that admits one. Route B cannot be salvaged by timing: the hole
must exist between the last `UpsertAsync` and the hydration BFS, and nothing outside `src/` can hold
it open.

**Measured evidence:** `analyzer-reports/phase-88-ZERO-01.json` (`Verdict: Inconclusive`,
`Moved: false`, `AcceptedUnprovenReason` carrying both routes verbatim);
`analyzer-reports/phase-88-wave0-probe.json` PQ-05 (`UnresolvedRouteChosen: "none"`,
`UnresolvedRouteApiDanglingEdgeStatus: 422`, `UnresolvedRouteApiDanglingEdgeStage: "step-create"`,
`UnresolvedRouteL2StepKeyViable: false`); matrix row `panelId: 6`
(`analyzer-reports/phase-88-discrimination.json`), `reasonSource: ZERO-01`.

---

## 2. Panel 7 — `Processor dropped spawns in range`

> **This record is mandatory regardless of any measurement, because it records a decision rather
> than an absence of evidence.**

**Asked for** (DISC-02, and HAND-04 by name): observe `processor_spawn_dropped_total` going non-zero
under a dropped fan-out spawn.

**What actually exists:** one observed state, `zero`. **Panel 7 is accepted-unproven BY USER
DECISION, NOT BY MEASUREMENT** — the user locked it at phase level this session, it is recorded in
`88-PROBE-DECISIONS.md` and in every plan since, and the matrix row states it in its first sentence
(`reasonSource: "user decision (locked at phase level, ...)"`). **No broker-fault scenario was
designed for it, deliberately** — and that deliberateness is the decision itself, not a gap in the
evidence.

The zero is genuine rather than merely unobserved: panel 7 was read as an **exactly-zero cross-talk
control during ZERO-03**, at exactly `0` across every sub-window, **while a real fault was firing
elsewhere on the stack**. So the panel is **proven to render `0`** and has **never been proven to
render non-zero**.

Because its expression is `sum(increase(processor_spawn_dropped_total[$__range])) or vector(0)`, its
**guarded green `0` is indistinguishable from "the counter has never been emitted at all"** — the
same defect this phase measured on panels 6, 8 and 13. **Maintenance must treat a green panel 7 as
"no evidence", not as "no dropped spawns".**

**Why driving it was ruled out.** Reaching the counter needs a spawn to be **dropped**, which on this
stack means the broker unreachable **at the instant a fan-out entry spawns** — the riskiest injection
in the set. The keeper/processor crash-loop under an unreachable broker was the whole reason the
v12.0.0 milestone existed, and that memory is why the user ruled it out.

**What shipped instead — the log-corroboration route maintenance should use.** Phase 85 (PB-01/PB-02)
made the spawn-exhaustion path **fail-loud**: `BaseProcessor.cs:123-124` fires the `OnSpawnDropped`
telemetry hook and then **throws `SpawnSendExhaustedException`**, the pipeline nack-requeues the
entry, and the whole seed re-fires. A real drop is therefore **well-corroborated in logs**. The
runbook row reads:

> **Panel 7 non-zero →** open the **processor logs for the same window** and search
> **`SpawnSendExhaustedException`**. The hook that increments the counter logs
> `SpawnToPost drop: send to -post exhausted ExecutionId={ExecutionId}`
> (`src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:309`) immediately before the increment and
> the throw — **one log line per increment**. The entry will have been **nack-requeued**, so expect a
> **matching re-fire of the same entry**. **Check broker health in the same window** — an exhausted
> retry chain almost always means RabbitMQ was unreachable. Panel 7 has never been observed non-zero
> on this stack: **treat a first non-zero reading as credible, but confirm against the logs before
> acting.**

**Why it is legitimate rather than a shortfall.** A user decision made with the hazard understood is
a legitimate scope boundary, and the fail-loud path means the axis is not actually unobservable — it
is observable **in logs**, which is where the strongest evidence for it lives anyway. What is *not*
legitimate is letting the green `0` read as an assurance, which is why the ambiguity is stated three
times in this record.

**What a future phase would need to prove it.** Two things, and the first is cheaper than it was:

1. **A bounded, targeted fault.** Taking RabbitMQ down moves every panel and proves nothing. What is
   needed is a send-endpoint seam inside `SpawnToPost`, env-gated and default-off, in the shape of
   the Phase-79 seams — a `src/` change.
2. **The user's consent.** This is a locked decision, not a technical block, and this phase does not
   reopen it. Worth recording for whoever does: **the hazard that motivated the decision was resolved
   in v12.0.0 Phase 86** — the shared `BaseConsole` watchdog now beats above infra I/O, and an
   unreachable broker was proven live to leave all four tiers at **RESTARTS 0**. The cost of a future
   attempt is therefore lower than it was when the decision was taken. The decision still stands
   until the user changes it.

**Measured evidence:** matrix row `panelId: 7` (`analyzer-reports/phase-88-discrimination.json`,
`status: AcceptedUnproven`, `provenBy: null`); the exactly-zero cross-talk reading in
`analyzer-reports/phase-88-ZERO-03.json`; the `0..0` band in
`analyzer-reports/phase-88-BASE-01.json`; the decision in `88-PROBE-DECISIONS.md`.

---

## 3. Panel 8 — `Keeper consumed - sent gap in range`

**Asked for** (DISC-04, verbatim): *"panels 2 and 8 have each been observed **non-zero** during a real
recovery event — … panel 8 with the reinject suppressed (consumed moves, sent does not, gap goes
red)."*

**What actually exists:** one observed state, `zero`. **The fault fired exactly as designed and the
gap still could not open.** This is the phase's strongest negative result, and it is
**measured-unreachable** rather than merely undriven. Three **independent** reasons, each measured in
ZERO-03:

**Route 1 — the seam is telemetry-byte-identical BY DESIGN, so it cannot open a conservation gap.**
`src/Keeper/Recovery/ReinjectConsumer.cs:106-116` runs `if (!defeatReinject) { Send } else { log }`
and then calls `CountSent(...)` **unconditionally on both branches**. Its own comment states the
intent: *"SUPPRESS the redispatch … but STILL CountSent + log \"reinject\" (unchanged) so the ES
telemetry is byte-identical to a genuine keeper-logged-reinject-lost-in-transit strand."* The seam was
built for the Phase-79 FALSIFY-01 negative control **precisely so that the metrics do not reveal the
loss**. Measured this run — the decomposition over the same after window:

```
consumed increase [120s]   0, 2.0001, 0, 0, 2.0024
sent     increase [120s]   0, 2.0001, 0, 0, 2.0024      <- byte-identical, term for term
gap (unguarded)  [120s]    0, 0, 0, 0, 0                <- an actual 0, not an empty vector
```

**Route 2 — no allow-listed lever reaches the branch that DOES open a gap.** The gap requires a keeper
consume with no send, which happens on exactly one path: the `ReinjectConsumer` **drop branch** (L2
`ExecutionData` absent at keeper read time, `STRLEN == 0` → `REINJECT drop` → early return **before**
`CountSent`, `ReinjectConsumer.cs:61-77`). Reaching it needs either `KEEPER_REINJECT_DELAY_MS`
(refused by T-88-15, uncommitted working-tree-only, not in the seam allow-list) or a shortened
`EXECUTION_DATA_TTL` (not an allow-listed seam either). The two delete-class consumers
(`DeleteConsumer`, `OrchestratorDeleteConsumer`) never call `CountSent` at all and therefore **do**
open the gap — but they only receive a message when a processor or orchestrator Redis DELETE
exhausts, an infrastructure fault this phase does not drive.

**Route 3 — at a 60 s window the panel cannot carry ANY value, guarded or not.** Panel 8 is
`sum(increase(A[$__range])) - sum(increase(B[$__range])) or vector(0)`. `increase()` needs at least
**two** samples inside its range and the stored resolution here is **60 s**. Measured this run: at a
60 s range **both terms and the unguarded gap returned ZERO POINTS** — the expression is empty at
every step — **while the guarded form returned seven points of `0`**. So at a short range the green
`0` an operator reads on panel 8 is **supplied entirely by `or vector(0)` and is not a measurement of
anything.** This is the panel-4 emptiness finding in its **guarded** form, and it is **worse**: panel
4 at least renders "No data" and says so.

**What shipped instead — the log-corroboration route maintenance should use.** The gap panel cannot
show the event; the keeper's own logs can, and they discriminate the three cases the panel collapses:

| Log line (keeper) | Source | What it means |
|---|---|---|
| `REINJECT sent {MessageId} reinject` | `ReinjectConsumer.cs:120` | a recovery message was consumed **and** redispatched — conserved, gap correctly 0 |
| `REINJECT drop {MessageId} drop` | `ReinjectConsumer.cs:77` | **the failure panel 8's title promises** — consumed, never sent, `CountSent` never fired. This is the line to search for. |
| *(no keeper lines at all in the window)* | — | no recovery traffic occurred; the green 0 means "nothing happened", not "nothing was lost" |

Maintenance should search the keeper logs for **`REINJECT drop`** over the panel's range and treat
that, not panel 8, as the authoritative signal. Panel 9 (`Keeper L2 probe heartbeat`) is the
unguarded liveness companion that says whether the keeper was alive at all.

**Why it is legitimate rather than a shortfall.** The fault was driven correctly and the phase
recorded what it measured rather than what the requirement wanted. **No substitute fault was
invented**, on the WEB-02 and ZERO-01 precedent. And the run **did** prove what it could:

- **Roadmap Success Criterion 5 is satisfied end to end** — both seams armed at runtime on the live
  Deployments, the arm rollout recorded as a disjoint pod-identity pair, the authoritative band
  re-captured after it, the fault driven, both seams disarmed from the **outer `finally`**, and the
  stack then asserted byte-identical by an independent read.
- **Research assumption A6 is RETIRED**: the deployed `keeper:tags-const-1544` image **does** honour
  `KEEPER_DEFEAT_REINJECT`. Direct log evidence, recovered from Elasticsearch because the disarm
  rollout destroys the pods' own `kubectl logs` — one defeat per keeper pod, as the per-process
  one-shot latch dictates, each followed within a millisecond by `REINJECT sent <the same MessageId>
  reinject`. **That pairing IS route 1**: the keeper logged and counted a send it did not make.
- Note that **no metric could ever have retired A6**, precisely because the seam is byte-identical in
  telemetry.

**Consequence for maintenance, stated plainly.** Panel 8's title promises a conservation gap; its
arithmetic delivers one only for **delete-class** recovery traffic and for a **keeper drop**, and
**never at a short window**. Its green `0` is indistinguishable from all three of "no keeper recovery
traffic has ever occurred", "recovery occurred and conserved", and "the window is too narrow for
`increase()` to evaluate at all". This phase observed the second and third and **could not produce
the first failure mode the panel exists to reveal**.

**What a future phase would need to prove it.** Three routes, cheapest first:

1. **`KEEPER_REINJECT_DELAY_MS` + a shortened `EXECUTION_DATA_TTL`.** This is exactly the combination
   the repo's own **FALSIFY-02** control already uses to force a genuine backup expiry, and it drives
   the real `REINJECT drop` branch — the one that opens the gap. It was refused here by T-88-15 and by
   the seam allow-list, **not because it does not exist**. A future phase need only allow-list the two
   variables and budget the TTL change.
2. **A separate, additive keeper seam** that suppresses the redispatch **without** calling
   `CountSent`. This must **not** be an edit to the existing branch: moving `CountSent` inside
   `if (!defeatReinject)` would destroy the Phase-79 FALSIFY-01 negative control, which depends on the
   telemetry being byte-identical. A `src/` change.
3. **An infrastructure fault that exhausts a processor or orchestrator Redis DELETE**, reaching
   `DeleteConsumer` / `OrchestratorDeleteConsumer`, neither of which calls `CountSent`.

Route 3 additionally requires reading panel 8 at a range of **at least 120 s** — at 60 s it renders
guard, whatever the stack is doing.

**Measured evidence:** `analyzer-reports/phase-88-ZERO-03.json` (`Verdict: Inconclusive`,
`OriginalVerdict: Fail`, `SeamHonouredByDeployedImage: true`, the decomposition rows at 60/120/600 s);
`analyzer-reports/phase-88-ZERO-03-asdriven.json` (the run's own `Fail` artifact, preserved verbatim);
`analyzer-reports/phase-88-ZERO-03-keeper-log.json` (11 verbatim records, 2 `REINJECT DEFEATED`, one
per keeper pod); matrix row `panelId: 8`, `reasonSource: ZERO-03`, carrying the DISC-04 entries in
both `measuredUnsatisfiableCriteria[]` and `escalations[]`.

---

## 4. Panel 13 — `WebApi 5xx ratio`

**Asked for** (DISC-02): observe the guarded green `0` going above the `0.0001` red threshold under a
server-side fault.

**What actually exists:** one observed state, `zero`. The panel was observed at exactly `0` under
real traffic **including 404s and 400s** and has never been seen non-zero. The reason is
**measured-unreachable and is a good result about the product**: PQ-03 enumerated **seven** endpoints
and found **no 5xx on safe input**.

| Method | Path | Status |
|---|---|---|
| GET | `/api/v1/workflows/00000000-0000-0000-0000-000000000001` | 404 |
| GET | `/api/v1/workflows/not-a-guid` | 404 |
| GET | `/api/v1/schemas/00000000-0000-0000-0000-000000000002` | 404 |
| GET | `/api/v1/processors/by-source-hash/000…000` (64 zeros) | 404 |
| GET | `/api/v1/__phase88_probe_unmatched` | 404 |
| POST | `/api/v1/orchestration/start` (body `[]`) | 400 |
| POST | `/api/v1/steps` (dangling `nextStepIds`) | 422 |

The WebApi's error handling is, on this evidence, uniformly well-behaved: every malformed or absent
reference resolves to a 4xx. **The dependency-outage route was NOT substituted.** Redis has no PVC,
so scaling it wipes L2; and under the **Phase-86 hard readiness latch** the WebApi does not self-heal
from a dependency outage — recovery costs a WebApi pod restart, which would perturb every later
baseline in this phase.

**What shipped instead — the log-corroboration route maintenance should use.** Panel 13's guarded
green `0` is indistinguishable from **"the 5xx counter has never been emitted at all"**. The first
non-zero reading should be corroborated against the **WebApi logs for the same window** before it is
trusted, and panel 12 (`WebApi status-code mix`) read beside it to see which code. One property is
worth stating because it changes over time: **once ONE 5xx has ever occurred the numerator series
exists permanently**, so a zero on panel 13 after that date means something *different* from today's
zero — after the first 5xx the guard stops carrying the panel and the ratio computes normally. The
panel is honest **once the counter exists**; nothing on it tells an operator whether it does.

**Why it is legitimate rather than a shortfall.** The blocker is the product being well-behaved. The
alternative — restarting the WebApi pod to manufacture a 5xx — would have invalidated the DISC-01
bands every later scenario scores against, trading a documented gap for a corrupted phase.

**What a future phase would need to prove it.** Either (a) budget the dependency-outage route
explicitly, accepting a WebApi pod restart, an L2 wipe and a full re-baseline of every band captured
before it; or (b) add a deliberately-faulting diagnostic endpoint behind an env gate — a `src/`
change. Option (a) needs no code but must be sequenced **last** in any phase that uses it.

**Measured evidence:** `analyzer-reports/phase-88-WEB-02.json` (`Verdict: Inconclusive`, the dropped
row with its `AcceptedUnprovenReason`); `analyzer-reports/phase-88-wave0-probe.json` PQ-03
(`Safe5xxFound: false`, `Safe5xxRoute: null`, `ProbedEndpoints`); the exactly-`0` cross-talk readings
in `analyzer-reports/phase-88-ZERO-01.json` and `analyzer-reports/phase-88-ZERO-03.json`; matrix row
`panelId: 13`.

---

# Section 2 — the HAND-03 practicality rubric

Six fixed questions, answered **yes / partial / no**, per panel. **No weights and no aggregate
score** — the blocking-gap list below is the verdict, and plan 88-10 states it. An aggregate would
let a panel that is unreachable and misleading average out to "fine".

**How each question was scored, stated before the table so the cells can be audited:**

| # | Question | Test applied | Evidence source |
|---|---|---|---|
| 1 | **Nameable** | Does the panel `description` state *what it shows*, *what healthy looks like*, and *when to investigate* — **and is what it says it shows what it actually shows?** | `k8s/dashboards/business.json` panel `description` (all 31 non-row panels carry one, enforced by lint rule **S16**) |
| 2 | **Falsifiable** | Does a DISC-01 baseline band exist **and can it serve as a null hypothesis** — i.e. is it non-degenerate and does it hold at the width the panel is read at? | `analyzer-reports/phase-88-BASE-01.json` (42 bands, `UnevaluablePanels[]`, `Panel4BandedAtWiderWindow`) |
| 3 | **Discriminating** | Has the panel been observed in **≥ 2 states**? Scored strictly from `observedStates[]` — register membership does **not** earn a pass, or the column would be a rubber stamp. | the matrix's `observedStates[]` |
| 4 | **Actionable** | Is there a named next action for the abnormal state, with a proving scenario or a log route behind it? | the matrix's `provenBy` / `reason`; the log routes in Section 1 |
| 5 | **Non-misleading** | Thresholds, axis auto-scale and `or vector(0)` guards each checked. **`yes` is reserved for the rows the matrix does not flag.** `no` = the default rendering asserts something false; `partial` = the values are true but the rendering is easy to misread. | the matrix's `misleadingByDefault` |
| 6 | **Reachable** | Is there a documented access path a maintenance department can actually use? | `k8s/23-grafana.yaml` |

> **Two scoring amendments, stated rather than applied silently.** The plan's tests for questions 2
> and 4 both cite *"the runbook authored in plan 88-10"* as their evidence. **That runbook does not
> exist yet — this rubric is its input.** Scoring all fourteen rows `no` on both questions would have
> recorded a scheduling artifact as a property of the dashboard. Q2 is therefore scored on **band
> existence and band usability**, and Q4 on **whether the material a runbook row needs exists** (a
> proving scenario with measured before/after values, or a named log route). **The missing runbook is
> carried in the blocking-gap list instead**, where it belongs.

## The table

| # | Panel | 1 Nameable | 2 Falsifiable | 3 Discriminating | 4 Actionable | 5 Non-misleading | 6 Reachable | Evidence |
|---|---|---|---|---|---|---|---|---|
| 1 | `Orchestrator consumed vs sent (conservation)` | yes | yes | yes | yes | yes | no | SCALE-02: means fell 0.734→0.139 / 1.370→0.257; band 0.645..1.477; matrix flag **false** |
| 2 | `Keeper consumed vs sent (conservation)` | yes | partial | yes | partial | no | no | ZERO-02: `Moved`, 4 consecutive off a `0..0` band; 88-07 measured `rate()` blind to a one-interval burst |
| 3 | `Processor consumed vs sent by image (conservation)` | yes | yes | yes | yes | partial | no | SCALE-01 + LADDER-01 (5 rungs); band 0.645..0.825; the fall is a 4-minute slope, not a cliff |
| 4 | `Orchestrator sends minus processor pickups (structural - read the trend)` | yes | partial | yes | partial | no | no | SCALE-01: renders "No data" under the fault it reveals; banded only at 120 s; wide level 2.11 never scored |
| 5 | `Per-processor dispatch gap` | yes | yes | yes | partial | no | no | SCALE-01: 0.63→0.588→0.4→0.234→0.183 then "No data" — a decay, never a spike |
| 6 | `Orchestrator unresolved steps in range` | yes | partial | no | partial | no | no | Register §1; ZERO-01; band `0..0`; log route `Dangling next-step id` |
| 7 | `Processor dropped spawns in range` | yes | partial | no | partial | no | no | Register §2; user-locked; exactly `0` as a ZERO-03 control; log route `SpawnSendExhaustedException` |
| 8 | `Keeper consumed - sent gap in range` | partial | no | no | no | no | no | Register §3; ZERO-03: both `increase()` terms **zero points** at 60 s, guard supplies the 0 |
| 9 | `Keeper L2 probe heartbeat` | partial | yes | yes | yes | partial | no | SCALE-03: faded 0.197→0.0644 over ~3 min then "No data"; band 0.18..0.22 (`floorApplied`); description says 0.4, the panel renders 0.2 per pod |
| 10 | `WebApi request rate by route` | partial | partial | yes | yes | partial | no | WEB-01; 7 of 13 series banded at exactly zero; the moving row is rendered `Value` |
| 11 | `WebApi p95 request duration by route` | partial | partial | yes | partial | partial | no | WEB-01: 22.0 ms / 19.7 ms at onset, back inside band (4.8 ms) within 3 min; one band −5.06..21.83 |
| 12 | `WebApi status-code mix` | yes | partial | yes | yes | partial | no | WEB-01: two exactly-`0..0` bands non-zero for 6 consecutive samples; the six series never leave the legend |
| 13 | `WebApi 5xx ratio` | yes | partial | no | partial | no | no | Register §4; PQ-03 seven endpoints, zero 5xx on safe input; band `0..0` |
| 14 | `WebApi in-flight requests and Kestrel connections` | partial | partial | yes | partial | partial | no | WEB-01 + LADDER-01; `http_server_active_requests` read exactly 0 under 2456 requests |

**84 cells: 14 rows x 6 questions.** Column totals —
Q1 **9 yes / 5 partial / 0 no** · Q2 **4 / 9 / 1** · Q3 **10 / 0 / 4** · Q4 **5 / 8 / 1** ·
Q5 **1 / 6 / 7** · Q6 **0 / 0 / 14**.

Question 5 is `no` or `partial` on **thirteen** panels, which is more than the six the rubric
expected. Question 6 is `no` **dashboard-wide**, without exception.

**The three cells most likely to be argued with, and why they read as they do:**

- **Panel 9, Q1 `partial`.** The description says *"Typical: a flat 0.4 probes/sec … measured between
  0.4001 and 0.4005."* The panel groups `by (service_instance_id)`, and BASE-01 measured **each of
  the two pod rows at 0.2000**. The description quotes the two-pod **sum** while the panel renders
  **per pod**, so a reader comparing tooltip to panel sees half the stated number and has no way to
  know why. The text is not false; it is not about what is rendered.
- **Panel 14, Q1 `partial`.** The description attributes the flat zero to idleness — *"since this
  WebApi is idle unless something is driving it"* — but WEB-01 measured `http_server_active_requests`
  at exactly `0` at **every one of six export ticks while ten parallel requesters issued 2456
  requests over 480 s**. The cause is the 60 s gauge export cadence, not idleness, and the
  description's advice (*"Investigate in-flight requests staying above zero"*) is about a series that
  essentially cannot be sampled above zero.
- **Panel 8, Q4 `no`.** The register names a log route, so the *axis* is actionable. The **panel** is
  not: no reading of it has ever been produced that would trigger an action, and its green `0` at a
  short range is guard, not measurement. A next action that requires ignoring the panel is not the
  panel being actionable.

## Derived list A — the panels that mislead by default

**Thirteen of fourteen.** Panel 1 is the only exclusion, and it earns it: its series are unguarded,
they carry a live non-zero level on a healthy stack, and under a real orchestrator outage SCALE-02
measured both legend means **falling visibly** rather than emptying or holding a comfortable value.
Its one caveat is that a 240 s outage did **not** reach "No data" — what an operator sees is a fall,
not a blank.

| Panel | How it misleads, in one line | Measured in |
|---|---|---|
| 2 | The `or vector(0)` guard makes a green `0` ≡ "never emitted"; **and** the `rate()` form is blind to a recovery burst confined to one 60 s export interval — a real recovery may leave the panel at zero; **and** the guard row does not disappear when the real series arrives, so a phantom `consumed` at 0 renders beside the real `consumed keeper` with nothing to say which is which | ZERO-02 (both runs) |
| 3 | The fall is a **four-minute slope, not a cliff**, and its first in-fault sample still reads inside the healthy band: 0.737→0.632→0.447→0.169→0 over five minutes of a **total** processor-tier outage, because the 240 s rate lookback smears the fault across four windows | SCALE-01, LADDER-01 |
| 4 | A **structural offset that can never read 0** (wide-window level 2.11 on a demonstrably healthy stack) — **and it goes blank under exactly the fault it exists to reveal**, because `A - empty` is empty; the stat whose own title says "read the trend" renders "No data" | BASE-01, SCALE-01 |
| 5 | A structural offset **and** it **decays and empties rather than spiking**: the research model assumes an independent producer, but this pipeline is request/response, so the orchestrator's own send rate falls with the processors and the gap shrinks before the panel blanks | SCALE-01 |
| 6 | Guarded green `0` ≡ "`orchestrator_step_unresolved_total` has never been emitted" | ZERO-01 |
| 7 | Guarded green `0` ≡ "`processor_spawn_dropped_total` has never been emitted" | ZERO-03 (as control) |
| 8 | **The worst of the fourteen, measured three ways:** the guard supplies the 0 from an expression returning zero points at 60 s; the gap arithmetic cannot show a suppressed redispatch at all; the only branch that opens a gap is unreachable | ZERO-03 |
| 9 | **The axis lies about the variance.** A metronome (mean 0.2000, unfloored 3σ ≈ 0.0006 wide) auto-scaled to fill the panel renders as apparent volatility; **and** under a dead keeper it fades for ~3 minutes before "No data" rather than dropping out | BASE-01, SCALE-03 |
| 10 | The row that moves most cleanly is rendered **`Value`** (an unmatched path carries no `http_route` label) with nothing on the panel to say what it is; **and** 7 of 13 route series are permanently flat at zero | BASE-01, WEB-01 |
| 11 | **The signal is the leading edge, not the level** — the p95 spikes at load onset and decays back **inside** the healthy band within three minutes; a reader watching for a sustained shift sees nothing. The band is also wide and partly negative | WEB-01 |
| 12 | **The status series never leave the legend.** All six were present and rendering zero before anything drove them; a code that has occurred once never disappears, so the legend cannot be read as "this code is happening" — only the value can | BASE-01, WEB-01 |
| 13 | Guarded green `0` ≡ "no 5xx counter has ever been emitted"; the panel becomes honest only **after** the first 5xx ever occurs | WEB-02, PQ-03 |
| 14 | **One of its three series is a dead instrument under real load.** The only readable series is `kestrel_active_connections`, which is a **connection** count kept alive by HTTP keep-alive — a different kind of quantity from the title's "in-flight requests" | WEB-01 |

## Derived list B — the blocking gaps

Stated as gaps, not softened. These are 88-10's HAND-02 input.

**B1. The dashboard is not reachable by a maintenance department.** `k8s/23-grafana.yaml` ships a
**ClusterIP with no NodePort and no Ingress**, so the only host reach is a loopback
`kubectl port-forward`; access is **anonymous at org role `Viewer`**
(`GF_AUTH_ANONYMOUS_ENABLED=true`, `GF_AUTH_ANONYMOUS_ORG_ROLE=Viewer`) with
**`admin`/`admin` as plain env values in the manifest** (`GF_SECURITY_ADMIN_USER` /
`GF_SECURITY_ADMIN_PASSWORD`). This is an **accepted dev posture** — auth hardening and ingress are
explicitly out of scope per REQUIREMENTS.md, it matches the unauthenticated in-cluster Prometheus and
Elasticsearch exactly, and the anonymous role cannot mutate anything (`allowUiUpdates: false`
additionally rejects a provisioned-dashboard save outright). **It is nonetheless a blocking gap for a
genuine handoff**, and question 6 fails on all fourteen panels because of it. Out of scope to fix; not
out of scope to state.

**B2. There is no runbook.** HAND-01 (symptom → panel → action) and HAND-02 (the readiness verdict)
are plan 88-10's deliverables and do not exist as this is written. Rubric questions 2 and 4 both
name the runbook as their evidence; both were scored on the underlying material instead (see the
scoring amendment above). **Until 88-10 lands, no panel on this dashboard has a written action.**

**B3. Panel 8 cannot show the failure mode its own title names.** DISC-04 as written is
**measured-unsatisfiable** under this phase's locked constraints. See register §3 and the traceability
table.

**B4. Four panels have never been observed non-zero, and their guarded green `0` is
indistinguishable from "the counter has never been emitted at all".** Panels **6, 7, 8, 13**. Two are
measured-unreachable (6, 13), one is measured-unreachable three ways (8), one is user-locked (7).
Nothing on the dashboard distinguishes "healthy" from "never instrumented" for any of them.

**B5. Short faults are invisible.** A **processor-tier outage shorter than 240 s does not register on
panel 3 at all** — measured, and the number is **double** what the research predicted. A **WebApi
condition shorter than 120 s cannot register on panel 14** by arithmetic. A **30 s total outage of
the processor tier produced a dip of one tenth of one percent**: on the dashboard, it did not happen.

**B6. A real recovery event can leave panel 2 at an unbroken zero.** ZERO-02 run 1 drove a genuine
event — both replicas claimed, three `KeeperReinject`s flowed, the keeper consumed and sent all three
— and the panel rendered `0` throughout, because the counter series is born carrying its final value
and `rate()` differences consecutive samples. The operator's reliable signal is the keeper's
`REINJECT sent` log line, **not the panel**.

**B7. Two panels go blank under exactly the faults they exist to reveal.** Panel 4 renders "No data";
panel 5 decays to nothing without ever spiking. An operator watching for a number to climb sees a
panel disappear.

**B8. Thirteen Class-A series have no live signal to move away from.** BASE-01 recorded them as
findings rather than bands: seven route series on panel 10, four status series on panel 12
(`201`, `400`, `404`, `422`), and two of panel 14's three series. A series banded at exactly zero on
a healthy stack is not a valid null hypothesis, and a scenario cannot discriminate against it.

---

# Section 3 — the phase's own accepted limitations

Same five headings. These are limitations of the **evidence**, not of any one panel.

---

## 5. The Prometheus retention premise this phase started with was wrong by a factor of nineteen

**Asked for / assumed:** `87-FINDINGS.md §14` measured retention at **~12 h**, and `88-RESEARCH.md`
propagated that into a standing constraint — *"evidence evaporates; capture during the run"* — which
made every band a run-time observation rather than a durable fact.

**What actually exists:** PQ-07 measured `OldestSampleUtc: 2026-07-19T07:38:06Z`,
**`RetentionHoursObserved: 225`** (~9.4 days), probed over a 336-hour horizon with
`RetentionHorizonLimited: false` — so it is a measurement of Prometheus, not of the probe. The real
figure is **roughly nineteen times larger** than the premise.

**What shipped instead.** The discipline of capturing evidence into artifacts during the run was
**kept** — it is right for reproducibility regardless — but the *urgency* framing was retired, and no
plan after 88-03 compressed a measurement window or skipped a settle on the belief that data was
about to disappear. The longer horizon is what made the ~161-minute LADDER-01 affordable, and it let
88-06 size SCALE-03's dwell by re-reading the Wave-0 probe's **own** recorded keeper rollout as a
free, read-only experiment.

**Why the bands are still run-time observations rather than durable facts — for a different reason.**
Retention is not the binding constraint; **conditions** are. Every band in this phase was captured
under a specific host load, a specific set of pod identities and a specific traffic pattern, and the
phase measured directly that comparing across those conditions reports a difference in *conditions*
as drift — which is why ZERO-01 and LADDER-01 score their cross-talk controls against a **local**
reference band rather than the DISC-01 band captured hours earlier. **Anyone tuning an alert off a
number in `phase-88-BASE-01.json` should re-measure first.** Whether Phase 87's number was
mis-measured or retention was changed since is not established here and was not worth a phase's time;
it should be re-read rather than trusted a month from now.

**Measured evidence:** `analyzer-reports/phase-88-wave0-probe.json` (PQ-07);
`88-PROBE-DECISIONS.md` Record 8; `analyzer-reports/phase-88-BASE-01.json`
(`OldestSampleUtc: 2026-07-19T07:53:52Z`).

---

## 6. The minimum detectable fault duration — measured, and one half of it falsifies the research

**Asked for** (DISC-07): the minimum detectable fault duration, measured by a step-ladder of
controlled durations and recorded **per regime**.

**What actually exists — two measured numbers and one stated property.**

| Regime | Predicted | **Measured** | Agreement |
|---|---|---|---|
| **Counter-backed** (panels 3, 4 — the event survives, its timing is smeared by the 240 s rate window) | 120 s | **240 s** | **DISAGREES** |
| **Gauge-backed** (panel 14 — an event between two 60 s exports is lost entirely) | 120 s | **120 s** | **AGREES** |
| **Regime B** (zero-floor guarded counters — panels 2, 6, 7, 8, 13, all banded exactly `0..0`) | — | **any duration** | *stated, not measured* |

Rung by rung, from `analyzer-reports/phase-88-LADDER-01.json`:

```
counter   30s: no    60s: no    120s: no    240s: MOVED    480s: MOVED
gauge     30s: no    60s: no    120s: MOVED
```

**Why the counter answer is double the prediction — the mechanism, and it is invisible from the
panel.** The trough model carried since the research assumes the fault **destroys** throughput for
its duration. It does not. **A queued pipeline hides a short outage from a rate panel:** the broker
buffers the work while `processor-sample` sits at zero replicas, and the tier **drains the backlog
the moment it comes back, inside the same 240 s rate window**. The counter is cumulative, so the
window's total is largely restored and the rate barely moves. Measured ratio of observed dip to
modelled dip, rung by rung: **0.004, 0.40, 0.66, 0.50, 0.74** — the model over-predicts at **every
single rung**. **A 30 s total outage of the processor tier produced a dip of one tenth of one
percent.**

The 120 s rung — the one the prediction named — missed by **exactly one sample**:

```
band   0.4989 .. 0.9017   (mean 0.7003, 10 x 60 s, floor applied)
after  0.767  0.767  0.767  0.783  0.681  0.470
                                          ^^^^^  one sample below the band. One.
```

A 33 % dip is not subtle; the panel visibly dropped. What it did not do is stay outside its band for
**two consecutive** 60 s samples. The two-consecutive rule is right — a single 60 s excursion is not
distinguishable from a sampling artifact — but **it costs a factor of two in detectable duration
here**, and maintenance should know that.

**Why the gauge answer agreeing is stronger than a coincidence.** The measured
consecutive-samples-outside equalled the theoretical maximum `floor(D/60)` at **every one of the
three rungs** — 0, 1, 2. At 30 s the panel *cannot* be detected by arithmetic; at 60 s exactly one
sample can fall inside the fault, and exactly one did.

**What length of outage the dashboard therefore cannot show.**

- **Counter-backed panels (1, 3, 4, 5, 9, 10, 11, 12):** a fault shorter than **240 s** on this stack.
  This number is **stack-specific** — the mechanism is broker buffering plus backlog drain, so it
  would change with queue depth, consumer prefetch or a different fault class.
- **Gauge-backed panel (14):** anything shorter than **120 s**, and that limit is arithmetic, not
  noise.
- **Regime B (guarded counters 2, 6, 7, 8, 13):** the story **inverts** — a single event is detectable
  at *any* duration, because the counter goes 0 → 1 and **stays** there for the whole `$__range`
  window rather than decaying back. **This is the one place the dashboard is better than an operator
  would guess.** Two caveats matter more than the property: the same guard makes a green `0`
  indistinguishable from "never emitted" (which is why 6, 7, 8 and 13 are in the register above), and
  **the property belongs to the `increase()`/`$__range` form, not to `rate()`** — 88-07 measured
  panel 2's `rate()` form blind to a burst confined to one 60 s export interval.

**Why the disagreement is legitimate rather than a shortfall.** Both the prediction and the
measurement are recorded side by side in the artifact
(`PredictedCounterSeconds: 120`, `MinDetectableCounterSeconds: 240`,
`PredictionAgreesWithMeasurement: false`, plus a per-regime detail string), with the measured
`DipFraction` and the modelled `TheoreticalDipFraction` carrying their **kinds** so a drop is never
silently compared against a residual. **The theory was not adjusted to fit the measurement** — the
240 s rung's divergence of 0.501 is recorded as a `Findings` entry and is the sole reason LADDER-01's
verdict is `Inconclusive`. The Inconclusive says the *model* was wrong, not that the measurement was.

**Measured evidence:** `analyzer-reports/phase-88-LADDER-01.json`
(`MinDetectableCounterSeconds: 240`, `MinDetectableGaugeSeconds: 120`, `RegimeBDetectionNote`,
`RungsDriven`, `IndependenceNote`, `ScoringNote`); matrix rows `panelId: 3`, `4`, `14`
(`ladderRungs[]`).

---

## 7. The dispatch gap empties instead of spiking — RECORDED AND DELIBERATELY NOT FIXED (panel 5)

**Asked for:** the research model for `Per-processor dispatch gap` predicted that when processors
die, the orchestrator keeps sending while pickups stop, so **the gap spikes to the full send rate**
and only afterwards empties as the stale series expire. Both phases were to be captured — the spike
being the useful signal and the emptiness the trap.

**What actually exists: half the model held.** SCALE-01 measured panel 5 under a **total** processor
outage:

```
0.63 → 0.588 → 0.4 → 0.234 → 0.183 → "No data"
```

That is a **decay, not a spike. Panel 5 emptied without ever spiking.** The model assumes an
**independent producer**; this pipeline is **request/response**, so with the processors gone the
orchestrator receives no completions and **its own send rate falls too**. The gap shrinks before the
panel goes blank. An operator watching for "the gap climbed" would see the panel disappear instead.

**This is a rendering consequence, not a wiring error.** The panel groups by `processorId` because
`identityName` is **absent on the orchestrator side** — `orchestrator_messages_sent_total` carries no
such label, and `87-FINDINGS.md §10` measured that substituting it does not degrade the panel, it
**empties** it (1 series → 0 series, because PromQL's binary `-` finds no matching label set). The
`processorId` grouping is therefore **correct as shipped**, and `87-FINDINGS.md §10` says so
explicitly.

**What shipped instead: the finding, and nothing else.** **The decision is that this is RECORDED AND
NOT FIXED.** Three reasons, in order of weight:

1. The grouping the emptiness follows from is **correct** per `87-FINDINGS.md §10`, and the two
   alternatives it enumerates are both out of scope — either the orchestrator learns the processor's
   name and version and stamps `identityName` on its send counter (a `src/` change, resolving an
   identity it deliberately does not hold), or Grafana maps `processorId` to a friendly name through
   a transformation (which would hard-code database ids into the dashboard JSON and break DASH-04
   portability).
2. The emptiness is a **rendering consequence** of a correct expression meeting a request/response
   pipeline, not a defect in the wiring.
3. **A dashboards-hardening change belongs to a phase that owns the dashboard.** Phase 88 is a
   proof-and-handoff phase: it changed **no** dashboard JSON, and `pwsh -File
   scripts/phase-87-dashboard-lint.ps1` exits 0 unchanged. Editing a panel to make a finding
   disappear would have destroyed the very evidence this phase exists to produce.

**Why it is legitimate rather than a shortfall.** The behaviour is now measured, named and written
down where the runbook can consume it — which is strictly better than a silent fix that a future
reader would have to rediscover. **The correct runbook row is "panel 5 going blank IS the signal",
not "watch for the gap to climb".**

**Measured evidence:** `analyzer-reports/phase-88-SCALE-01.json`; matrix row `panelId: 5`
(`measuredUnsatisfiableCriteria[0]`: *"HALF HELD. Panel 5 emptied WITHOUT EVER SPIKING …"*,
`correctedIn: 88-06`); `87-FINDINGS.md §10`.

---

## 8. The one place a diagnostic proxy disagreed with the rendered value (panel 4)

**Asked for:** every scenario cross-checks its rendered reading against an independent PromQL proxy
issued through the Grafana datasource, under a standing rule: **the rendered panel is the verdict;
the proxy is diagnosis only, and a disagreement is a finding to record, never an error to reconcile
away** (T-88-12).

**What actually exists:** across all nine committed scenario artifacts, **exactly one** panel
disagreed, and it disagreed **twice** — in SCALE-01 run 1 and again in the kept SCALE-01 run:

```
SCALE-01 (kept)      panel 4: rendered series-mean sum 58    vs proxy aggregate mean 80.886  (tolerance 11.6)
SCALE-01 (run 1)     panel 4: rendered series-mean sum 132.2 vs proxy aggregate mean 33.677  (tolerance 26.44)
```

`DiagnosticAgreesWithPanel: false` on both. Every other comparable panel in every other scenario
agreed — BASE-01 (13/14 comparable, all agreeing), SCALE-02, SCALE-03, WEB-01, ZERO-02, ZERO-03.

**What shipped instead: the rendered value, with the disagreement recorded beside it.** The verdict
was taken from the panel in both runs and the proxy value preserved in
`DiagnosticDisagreements[]`. **The proxy was not used to "correct" the panel and the panel was not
re-read to chase agreement.**

**Why it is legitimate rather than a shortfall — and why panel 4 in particular.** Panel 4 is
`sum(increase(A[$__range])) - sum(increase(B[$__range]))`, whose value **depends on the visible window
width**; the proxy is an aggregate over a range query at a chosen step. Those are not the same
quantity when the panel is emptying and re-filling across window boundaries, which is exactly what
SCALE-01 drove it to do. The disagreement is therefore **consistent with the panel-4 findings already
recorded** — the structural offset, the `$__range` dependence, and the emptying under fault — rather
than being an unexplained anomaly. It is recorded here because a single unexplained disagreement in a
document that claims fourteen panels were read correctly **must** be visible.

**Measured evidence:** `analyzer-reports/phase-88-SCALE-01.json` and
`analyzer-reports/phase-88-SCALE-01-run1-discarded.json` (`DiagnosticAgreesWithPanel: false`,
`DiagnosticDisagreements[]`, `DiagnosticNote`); `analyzer-reports/phase-88-BASE-01.json`
(`RangeCumulativeLevelBaseline: 2.11`, `RangeCumulativeNote`).

---

## 9. Thirteen Class-A series have no live signal to move away from

**Asked for** (DISC-01): a recorded healthy baseline band for every one of the fourteen panels.

**What actually exists:** 42 per-series bands across 14/14 panels — **and 13 series recorded as
`UnevaluablePanels[]` findings rather than as bands**, because they banded at **exactly zero across
all ten sub-windows** on a demonstrably healthy stack:

| Panel | Series banded at exactly zero |
|---|---|
| 10 `WebApi request rate by route` | `Value`, `Assignments/{id:guid}`, `Orchestration/stop`, `Processors/by-source-hash/{sourceHash}`, `Schemas/{id:guid}`, `Steps/{id:guid}`, `Workflows/{id:guid}` — **7 of 13** |
| 12 `WebApi status-code mix` | `201`, `400`, `404`, `422` — **4 of 6** |
| 14 `WebApi in-flight requests and Kestrel connections` | `in-flight …`, `kestrel queued …` — **2 of 3** |

**What shipped instead.** They are carried as findings, not bands, and BASE-01's verdict is
`Inconclusive` **because of them** (`AllBandsComputed: false`). The reason matters: **a Class-A
series with no live signal is not a valid null hypothesis** — there is nothing for a fault to move it
away from, so a scenario scoring against it cannot discriminate. Where the substantive claim could
still be made, it was made in a **stronger** form: WEB-01 proved panel 12 by driving two
exactly-`0..0` bands non-zero for six consecutive samples, rather than by a legend gain.

**Why it is legitimate rather than a shortfall.** These are idle routes and unused status codes on a
dev cluster whose pipeline runs on a cron rather than on HTTP — the zeros are **true**. The
limitation is on the *evidence*, not on the dashboard: it means those thirteen series are
**unproven**, and the honest thing is to say so rather than to record a `0..0` interval as if it were
a measured band. Note the asymmetry with the guarded Regime-B panels: **a `0..0` band on a guarded
counter is a usable null hypothesis** (any non-zero falsifies it, and ZERO-02 proved exactly that on
panel 2), whereas a `0..0` band on an **unguarded Class-A** series is a series that was never alive.

**Measured evidence:** `analyzer-reports/phase-88-BASE-01.json` (`UnevaluablePanels[]`, 13 entries;
`AllBandsComputed: false`; `Verdict: Inconclusive`); matrix row `panelId: 12`
(`measuredUnsatisfiableCriteria[0]`, `correctedIn: 88-05`).

---

## 10. Six acceptance criteria were measured-unsatisfiable, and three runs were discarded for defects

**Asked for:** the plans' acceptance criteria as written, and one artifact per scenario.

**What actually exists — six criteria the measurement contradicted**, each carried on the matrix row
it belongs to rather than smoothed away:

| # | Criterion (as written) | What the measurement said | Panel | Corrected in |
|---|---|---|---|---|
| 1 | panel 4 is banded on its per-minute increase at the fixed **60 s** sub-window | panel 4 renders "No data" at 60 s — `increase()` needs ≥ 2 samples and the stored resolution is 60 s. Banded at **120 s**, with every band entry stating its own width | 4 | 88-04 |
| 2 | panel 4 **slopes up** under a processor outage | it goes **blank**: `A - empty` is empty. SCALE-01's verdict is `Inconclusive` because of it — a wrong model is never absorbed into a Pass | 4 | 88-06 |
| 3 | the per-processor dispatch gap **spikes** before it empties | half held — it emptied **without ever spiking** (see §7) | 5 | 88-06 |
| 4 | SCALE-03 dwell **240 s** | `rate(counter[240s])` for a dead pod keeps producing a decaying value for ~180 s past its last real sample, so at 240 s the two-consecutive-NoData criterion was unreachable **by construction**. Dwell raised to **480 s** — corrected *before* the run, on a read-only measurement rather than a hunch | 9 | 88-06 |
| 5 | panel 12's `LegendNamesAfter[]` must contain a series name absent from `LegendNamesBaseline[]` (a **legend gain**) | measured-unsatisfiable: Prometheus retains a counter series for as long as the emitting process lives and the WebApi pod has been up since Phase 80, so all six codes were present **and zero** in both windows. **No legend row can be gained.** Proven in the stronger numeric form instead, and WEB-01 was **not re-run to chase it** | 12 | 88-05 |
| 6 | **DISC-04 as written**: panel 8 must be observed **non-zero** with the keeper reinject suppressed | measured-unsatisfiable under the phase's locked constraints — the product deliberately prevents it (register §3) | 8 | 88-07 |
| — | panel 14 is **one** gauge rung | it is **two**: `http_server_active_requests` read exactly 0 under 2456 sustained requests; only `kestrel_active_connections` moves | 14 | 88-05 |

**And three runs discarded, each for defects in scoring or drive code, never for a worse number.**
All three are **committed on disk beside their replacements**:

| Discarded artifact | Defect that forced it |
|---|---|
| `phase-88-BASE-01-run1-discarded.json` | lost its **entire** diagnostic cross-check to a single-letter typed parameter (`[long]$S` and `foreach($s)` are the same case-insensitive variable, so the type constraint threw on every call), and its depth guard then tripped on its own diagnostic text |
| `phase-88-SCALE-01-run1-discarded.json` | stated a spike its own recorded values contradict (segment behaviour **inferred** from "some series moved"), an alt-direction match **appended** a duplicate series entry so the diagnostic aggregate double-counted panel 4, and `PanelStateNoDataRun` was left unmeasured for panels whose prediction did not admit NoData |
| `phase-88-ZERO-02-run1-discarded.json` | drove a **genuine** recovery event that panel 2 rendered as an unbroken `0` (the burst finished inside one 60 s export interval), and recorded the **driver's own** activation POST as the fault's blast radius on panels 10 and 12 |

**Why it is legitimate rather than a shortfall.** A criterion that measurement contradicts is
evidence, and the phase's rule was to **amend the criterion with its evidence rather than reword it
to match the outcome**. The ZERO-02 discard deserves a specific note: **it remains a better
measurement of what an operator will actually see than its replacement is**, and the matrix says so
on the row. Keeping it is what makes the `rate()`-blindness finding (blocking gap B6) citable.

**Measured evidence:** the seven `measuredUnsatisfiableCriteria[]` entries and the
`discardedRuns[]` arrays in `analyzer-reports/phase-88-discrimination.json`; the three discarded
artifacts themselves.

---

## 11. Deliberate non-changes — the fences this phase was given, and that held

**Asked for:** a proof-and-handoff phase that changes nothing it is measuring.

**What actually exists.**

- **Zero `src/` changes.** No plan in phase 88 created, modified or deleted any file under `src/`.
  (`src/Keeper/Recovery/ReinjectConsumer.cs` is modified in the working tree and **has been since
  before Phase 87** — it appears in the session-start git snapshot and is documented identically in
  the Phase-87 summaries. The tree was already dirty there when this phase began.)
- **Zero dashboard JSON changes.** `pwsh -File scripts/phase-87-dashboard-lint.ps1` exits **0**
  unchanged after every plan, and `git status --porcelain k8s/` is empty.
- **Zero manifest edits.** Every seam was armed and disarmed at **runtime** via `kubectl set env`
  against a static `(tier, variable)` allow-list, with the disarm in an **outer `finally`** and the
  stack asserted restored afterwards by an **independent** read.
- **`kubectl apply -k k8s/` was never run.** It reverts all four app Deployments from the live
  `:tags-const-1544` images to the manifests' stale `:local` pins, and the stale bits **do not emit
  the `source` resource attribute at all** — every dashboard expression filters on `source`, so a
  stale-image cluster makes correct dashboards look broken.
- **No substitute fault was ever invented.** Where a lever was refused (`KEEPER_REINJECT_DELAY_MS`,
  T-88-15) or a route was measured closed (panels 6, 13), the panel went into the register rather
  than being scored against a different fault that happened to move it.

**Why it is legitimate rather than a shortfall.** A phase that edits the artefact it is grading
cannot grade it. The stack was left exactly as found after every scenario — replicas 1/2/3/2, all
four images at `:tags-const-1544`, zero `DEFEAT` / `REINJECT_DELAY` env vars on `keeper` and
`processor-sample`, the Redis arm slot cleared, and the pre-existing Grafana port-forward reused and
left running.

**Measured evidence:** `SeamVarsClean` / `ReplicasRestored` / `ImagesUnchanged` all `true` with empty
mismatch arrays on every scenario artifact; the independent `kubectl` re-reads recorded in the 88-04
through 88-08 summaries.

---

# Requirement traceability

| Requirement | What satisfies it | Artifact | Status |
|---|---|---|---|
| **DISC-01** | A recorded healthy baseline band for all fourteen panels — 42 per-series bands over a pinned absolute window at 1920x1080, read from the rendered panel, each stating its regime and its own sub-window width. Panel 4 is banded at **120 s** because it cannot be banded at 60 s (§10, criterion 1). 13 zero-signal Class-A series are recorded as findings rather than bands (§9). | `analyzer-reports/phase-88-BASE-01.json` | **Complete** |
| **DISC-02** | All fourteen panels accounted for in one machine-checkable artifact: **10 Proven** each with a `provenBy` scenario, **4 AcceptedUnproven** each with a non-empty reason and a register entry (Section 1). The requirement's own "or is recorded in an accepted-unproven register" clause is what carries the four. | `analyzer-reports/phase-88-discrimination.json` + Section 1 of this file | **Complete** |
| **DISC-03** | Every scenario carries a non-empty, pre-declared cross-talk list, every control asserted held: **16/16** across the ZERO-* and SCALE-* / WEB-* scenarios and **3/3** on LADDER-01, all at `MaxExcursion` exactly **0.0000**. *Amended:* LADDER-01 and ZERO-01 score their controls against a **local** pre-rung reference band rather than the DISC-01 band, and LADDER-01 measures them on the **longest rung of each regime** rather than every rung — both deliberate, both stated in the artifacts, and both grounded in §5. | every scenario artifact's `CrossTalkPanels[]`; `LADDER-01.CrossTalkNote` | **Complete** *(amended)* |
| **DISC-04** | **HALF MET, and the other half is MEASURED-UNSATISFIABLE.** Panel 2 **proven non-zero** in a real keeper recovery event with the gap held at exactly 0 (ZERO-02, 4 consecutive sub-windows off a `0..0` band). Panel 8 **proven undrivable** off its guarded zero by any lever this phase is allowed to pull, three independent ways (ZERO-03). Satisfying the requirement as written needs a `src/` change (forbidden by the phase's locked constraint) or `KEEPER_REINJECT_DELAY_MS` (refused by T-88-15). **Roadmap Success Criterion 5 is met; Success Criterion 4 is not.** See register §3. | `analyzer-reports/phase-88-ZERO-02.json`, `analyzer-reports/phase-88-ZERO-03.json`, matrix `panelId: 8` `escalations[]` | **Partial — blocked** |
| **DISC-05** | A repeatable runtime-only arm/disarm mechanism, editing **no manifest**, exercised across every seam scenario and every ladder rung, with the stack asserted restored after each: zero seam env vars on `keeper` and `processor-sample`, replicas equal to the pre-scenario live read, images unchanged. Disarm from the **outer `finally`**; the Redis arm slot given the same unconditional teardown and independently re-read. | `scripts/lib/phase-88-cluster-ops.ps1`; `SeamVarsClean` / `ReplicasRestored` / `ImagesUnchanged` on every artifact | **Complete** |
| **DISC-06** | Every seam- and scale-dependent scenario re-captures its baseline **after** the rollout settled, and records the rollout discontinuity — the old/new `service_instance_id` pair and its timestamp — as an **expected artifact**, never scored as movement. Strengthened beyond the requirement by `RebaselineInertnessProven`, which asserts **per run** that every Regime-B subject band is still exactly `0..0` with the seam armed, so a contaminated re-baseline could not pass unnoticed. | `BaselineRecaptured` / `SeamRolloutOldPods` / `SeamRolloutNewPods` / `RebaselineInertnessProven` on the SCALE-*, ZERO-* and LADDER-01 artifacts | **Complete** |
| **DISC-07** | The minimum detectable fault duration **measured per regime** from eight real rungs: **240 s** counter (predicted 120 s — **disagrees**), **120 s** gauge (predicted 120 s — agrees), plus the stated Regime-B any-duration property. Both predictions recorded beside both measurements; the divergence recorded as a finding rather than reconciled. See §6. | `analyzer-reports/phase-88-LADDER-01.json` | **Complete** |
| **HAND-01** | Not delivered by this plan — the symptom → panel → action runbook is plan 88-10's. Blocking gap **B2**. | — | Pending (88-10) |
| **HAND-02** | Not delivered by this plan — the readiness verdict is plan 88-10's. Its three inputs are ready: derived list **A** (misleads by default), derived list **B** (blocking gaps), and Section 1 (accepted-unproven status per panel). | — | Pending (88-10) |
| **HAND-03** | The fourteen-by-six practicality rubric — nameable / falsifiable / discriminating / actionable / non-misleading / reachable, yes-partial-no, **no weights and no aggregate score**, with the verdict stated as the blocking-gap list. Two scoring amendments stated in the open. | Section 2 of this file | **Complete** |
| **HAND-04** | The accepted-unproven register: panel 7 (`processor_spawn_dropped_total`, **user-decided this session**) plus the three panels a Wave-0 probe or a live measurement showed cannot be driven — 6, 8 and 13 — each with its reason, its log-based corroboration route, and what a future phase would need to prove it. Derived from the matrix so it and the `Proven` set cannot drift apart. | Section 1 of this file, derived from `analyzer-reports/phase-88-discrimination.json` | **Complete** |

---

*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Written: 2026-07-29 · Derived from `analyzer-reports/phase-88-discrimination.json`*
