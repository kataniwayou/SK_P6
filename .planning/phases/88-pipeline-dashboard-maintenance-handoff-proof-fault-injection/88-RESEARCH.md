# Phase 88: Pipeline dashboard maintenance-handoff proof — Research

**Researched:** 2026-07-28
**Domain:** Grafana panel discrimination proof via live fault injection on Docker-Desktop k8s, asserted through the rendered DOM (Playwright)
**Confidence:** MEDIUM-HIGH (stack/mechanism HIGH — verified live this session; per-panel fault drivability MEDIUM — three panels need a Wave-0 probe before their scenario can be locked)

---

## Summary

Phase 87 left a specific, narrow gap: the business dashboard is proven to *render* and proven *portable*, but every observation was taken from one healthy steady state. Ten of fourteen panels have been seen in exactly one state. This phase closes that by driving, per panel, the real fault it exists to reveal and asserting the movement **from the rendered panel**.

The good news that dominates the phase's design: **no dashboard change is needed to make every panel machine-readable as text.** All eight `timeseries` panels already ship `legend.displayMode: "table"` with `calcs: ["mean","max"]`, so Grafana renders a numeric Mean and Max per series as DOM text — no canvas reading, no pixel diffing. All six `stat` panels render a big-number text node. Combined with Grafana 12.3.9's stable `data-testid` hooks (`data-testid Panel header <title>`, `data-testid panel content`, `data-testid VizLegend series <name>` — verified against the v12.3.9 source tag), every one of the fourteen panels yields a parseable number through `innerText`. The Playwright assertion is therefore mechanical, not impressionistic.

The hard news is threefold. **(1)** The comparison is only valid if the browser window is pinned: the legend Mean/Max are computed over the *visible* time range, so baseline and after-capture must use **absolute** `from`/`to` epochs of identical width, at a **fixed viewport**, or the numbers are not comparable. **(2)** Only one panel genuinely requires the Phase-79 seams (panel 8, the keeper gap) — most panels are drivable by `kubectl scale` or plain HTTP traffic, which the roadmap explicitly prefers. **(3)** The 14 panels do not share one statistical regime; three regimes exist (jittering rate, zero-floor event counter, monotonically-growing range stat) and one "did it move" rule cannot serve all three.

**Primary recommendation:** Build a `scripts/phase-88-*` harness pair — a PowerShell scenario driver (fault arm/trigger/restore, following the `phase-87-dashboards-verify.ps1` skeleton) and a Playwright panel-reader invoked per capture — that drives eight scenarios, each writing one `phase-83-ha.json`-shaped artifact, rolled up into a per-panel discrimination matrix. Capture baseline and after through **absolute pinned windows via `?viewPanel=panel-<id>&from=<ms>&to=<ms>&kiosk`** at a fixed viewport, and restore replica counts read from the **live deployment**, never from `phase-80-harness.ps1`'s `$TierReplicas` map — which is stale and would silently scale the orchestrator 3 → 1.

---

## User Constraints

There is no CONTEXT.md for this phase **by design** — the ROADMAP.md Phase 88 entry replaces it, and the orchestrator's brief carries four locked constraints from the user's session. Reproduced here verbatim in force:

### Locked Decisions

1. **No new metrics, no `src/` instrumentation change.** Panels compose only what is already emitted (the nine domain counters, the five WebApi/Kestrel metrics, the 17 `process_runtime_dotnet_*` names). Fault scenarios change *system state*, never instrumentation. A panel can therefore only be proven by driving its REAL fault condition. *(Panel expressions in `k8s/dashboards/business.json` MAY be recomposed from existing metrics if a scenario exposes a genuinely wrong one — that is the one permitted file change, and it is a dashboard change, not an instrumentation change.)*
2. **Final validation is through the rendered Grafana panels, driven by the Playwright skill** (`playwright-skill:playwright-skill`, installed and working at `C:\Users\UserL\.claude\plugins\cache\playwright-skill\playwright-skill\4.1.0\skills\playwright-skill`, run via `node run.js <script>`). Prometheus (or Grafana's datasource proxy) may be queried **for DIAGNOSIS when a panel disagrees with the data, never as the verdict.**
3. **Panel 7 (`processor_spawn_dropped`) is ACCEPTED UNPROVEN — decided by the user this session.** Do NOT design a broker-fault scenario for it. Instead, produce the findings record and the runbook row telling maintenance to corroborate via processor logs.
4. **Stack is Docker-Desktop k8s (`skp` namespace) ONLY. Never docker-compose.**

### Claude's Discretion

- The per-panel fault mapping (which fault drives which panel), subject to "prefer system state alone over a seam wherever possible" — an explicit roadmap instruction.
- The statistical rule for "moved" vs jitter, and the cross-talk threshold.
- The measurement design for the minimum detectable fault duration.
- The requirement IDs — the roadmap defers derivation to planning and expects a `DISC-*` + `HAND-*` family.
- The maintenance-practicality rubric ("avoid inventing a heavyweight UX framework").
- The artifact schema, within the repo's existing single-proof / roll-up precedent.

### Deferred Ideas (OUT OF SCOPE)

- Any `src/` change, including committing `KEEPER_REINJECT_DELAY_MS` (uncommitted working-tree only at `ReinjectConsumer.cs:55`). The keeper scenario does not need it.
- Any new metric, counter, or label.
- Alerting rules, contact points, notification channels (v13.0.0 Out of Scope).
- Elasticsearch panels (v13.0.0 Out of Scope) — `attributes.ReinjectOutcome` stays the analyzer's domain.
- Grafana auth hardening / ingress / persistence.
- Collector, Prometheus, or exporter config changes.
- The runtime dashboard (`k8s/dashboards/runtime.json`) — this phase is the **business** dashboard only. Its 17 panels are all Class A and all already observed non-empty.

---

## Project Constraints (from CLAUDE.md)

**No `./CLAUDE.md` exists in this repository** (verified: file not found). No `.claude/skills/` or `.agents/skills/` directory exists either (verified: both absent). There are therefore no project-level directives beyond the roadmap, REQUIREMENTS.md, and the standing conventions the repo enforces through its own scripts.

The de-facto conventions the harness MUST follow, extracted from `scripts/phase-87-dashboards-verify.ps1` and `scripts/phase-83-ha-failover.ps1` (these are as binding as a CLAUDE.md would be — the plan-checker will look for them):

| Convention | Where proven |
|---|---|
| `$ErrorActionPreference = 'Stop'` + `Set-StrictMode -Version Latest` | every harness |
| `Push-Location $repoRoot` + whole-body `try`/`finally` teardown | `phase-87-dashboards-verify.ps1:134,924` |
| Comment-based help with a **STEP flow** and an **EXIT-CODE TABLE**, one row per code | `phase-87-dashboards-verify.ps1:24-86` |
| Dot-source `scripts/lib/exit-code-resolution.ps1`; NEVER reimplement the verdict→exit switch | `:151` |
| **Write the report artifact BEFORE resolving the exit code** | `:913` then `:917` |
| Pin `$LASTEXITCODE` into a variable BEFORE any `.Trim()` on the captured stdout | `:372` |
| Own loopback port-forward, PID in memory only, `--address 127.0.0.1`, recycled-PID guard on teardown | `:163-179` |
| Never read or write `.k8s-portforward-pids` (that file owns the eight shared forwards) | `:100-104` |
| Distinct exit code per infra abort, so an abort can never be read as a verdict | `:74-85` |
| Under StrictMode, **never dot into a member collection** (`$o.PSObject.Properties.Name` throws on an empty `{}`) | `87-05-SUMMARY.md` deviation 2 |
| `@()`-wrap before `.Count`; assign-then-wrap for `Invoke-RestMethod` array results | `:447` |
| No `jq`, no external JSON tooling — `ConvertFrom-Json` only | `:118` |
| Static string-literal `psql -c`, never an interpolated one | `:495` |

---

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|---|---|---|---|
| Fault injection (scale, seam env, traffic) | **k8s control plane** (`kubectl`) | — | The locked constraint forbids `src/` changes; system state is the only lever |
| Fault seam arming (`PROCESSOR_DEFEAT_READ`, `KEEPER_DEFEAT_REINJECT`) | **k8s Deployment env, runtime-only** (`kubectl set env`) | — | Seams exist in `src/` but in no manifest (Phase-80 D-08); an `apply -k` would strip them regardless, so a manifest edit is both forbidden and pointless |
| Baseline + after value capture | **Browser DOM** (Playwright over Grafana) | — | Locked constraint 2: the panel is the verdict |
| Diagnosis when panel ≠ data | **Grafana datasource proxy** (`/api/datasources/proxy/uid/skp-prometheus/api/v1/query_range`) | Prometheus direct | Locked constraint 2 permits this for diagnosis only; reuse the proven Phase-87 call, do not learn `/api/ds/query` |
| Scenario orchestration, restore, verdict, artifact | **PowerShell harness** (`scripts/phase-88-*.ps1`) | — | Every repo precedent; owns exit codes and teardown |
| Traffic generation (pipeline) | **`v8-fanout-proof` cron + `POST /api/v1/orchestration/start`** | `dotnet test ~FanOutSeeder` | Already the Phase-87 STEP E drive; reuse in shape |
| Traffic generation (WebApi HTTP) | **Host HTTP loop against `localhost:8080`** | — | Panels 10-14 need real request metrics; the collector drops `/health/` routes |
| Runbook + readiness verdict | **Markdown deliverables in `.planning/phases/88-*/`** | — | Documentation artifacts, not code |

**Tier misassignment to avoid:** do not put the movement verdict in the PowerShell tier by querying Prometheus and calling it proven. That is exactly the Phase-87 defect (`87-FINDINGS.md` §12: "a proof that does not reproduce the caller's query shape proves only that the PromQL parses"), and constraint 2 exists to prevent its recurrence. PowerShell orchestrates and records; the browser decides.

---

## Phase Requirements

The roadmap defers requirement derivation to planning and expects a `DISC-*` (panel discrimination) + `HAND-*` (handoff readiness) family. **REQUIREMENTS.md has no Phase-88 rows yet** — all 16 existing v13.0.0 requirements are mapped to Phase 87 and marked Complete. The following are **proposed**, each written as a testable statement and each mapped to a roadmap success criterion so nothing is invented and nothing is dropped.

| Proposed ID | Statement | Roadmap SC |
|---|---|---|
| **DISC-01** | Every one of the fourteen business panels has a recorded healthy baseline band — captured from a settled stack over an **absolute, pinned** time window at a fixed browser viewport, read from the rendered panel — stated as a numeric interval plus the regime it belongs to (rate / zero-floor / range-cumulative). | 1 |
| **DISC-02** | Each of the fourteen panels has been observed **changing in the predicted direction** under the fault it exists to reveal, asserted from the rendered Grafana panel via Playwright, with the movement outside its DISC-01 band for **≥ 2 consecutive 60 s samples** — or is recorded in an accepted-unproven register naming the reason its fault could not be driven safely. | 2 |
| **DISC-03** | Every scenario carries a cross-talk control: an explicit, pre-declared list of panels the fault should **not** affect, each asserted to have remained inside its DISC-01 band for the whole fault window. A scenario with an empty cross-talk list is a defect. | 3 |
| **DISC-04** | Panels 2 (`Keeper consumed vs sent`) and 8 (`Keeper consumed − sent gap`) have each been observed **non-zero** during a real recovery event — panel 2 with keeper reinject intact (consumed and sent both move, gap stays 0), panel 8 with the reinject suppressed (consumed moves, sent does not, gap goes red). | 4 |
| **DISC-05** | A repeatable, runtime-only mechanism exists for arming and disarming the Phase-79 fault seams on the k8s stack. It edits **no manifest**, and after every scenario the stack is asserted restored: **zero** seam env vars on `keeper` and `processor-sample`, replica counts equal to the values read from the live deployments **before** the scenario, and images unchanged. | 5 |
| **DISC-06** | Every seam-dependent or `kubectl scale`-dependent scenario **re-captures its baseline after** the resulting rollout has settled (all replicas Ready, ≥ 2 export cadences elapsed), and the rollout discontinuity — the old/new `service_instance_id` pair and its timestamp — is recorded in the scenario artifact as an **expected artifact**, never scored as movement. | 6 |
| **DISC-07** | The minimum detectable fault duration is measured by a step-ladder of controlled fault durations and recorded **per regime** — separately for counter-backed panels (where the event survives but its timing is smeared by the 240 s rate window) and gauge-backed panels (where an event between two 60 s exports is lost entirely). | 7 |
| **HAND-01** | A symptom → panel → action runbook exists covering every abnormal state this phase drove, each row citing the scenario id that proved it and the measured before/after values. | 8 |
| **HAND-02** | A handoff readiness verdict is recorded, listing (a) blocking gaps, (b) every panel that misleads by default — at minimum the structural offsets on panels 4 and 5, the axis-zoom on panel 9, and the guarded zeros on 2/6/7/8/13 — and (c) each panel's accepted-unproven status where applicable. | 9 |
| **HAND-03** *(proposed beyond the criteria)* | Each panel is scored against a fixed six-question practicality rubric (nameable / falsifiable / discriminating / actionable / non-misleading / reachable), yes-partial-no, no weights, with the dashboard verdict stated as the blocking-gap list rather than an aggregate score. | — (supports 9) |
| **HAND-04** *(proposed beyond the criteria)* | An accepted-unproven register records panel 7 (`processor_spawn_dropped`, user-decided this session) and any panel a Wave-0 probe shows cannot be driven safely, each with its reason, the log-based corroboration route maintenance should use instead, and what would be needed to prove it in a future phase. | — (supports 2, 9) |

**Note for the planner:** DISC-02's "fourteen panels" and HAND-04's register are the same set partitioned two ways. Keep the register as the single source of truth for what is unproven so the two cannot drift apart.

---

## Per-Panel Fault Mapping

This is the phase's core research product. `[VERIFIED: live cluster]` = confirmed against the running `skp` namespace this session. `[CITED: file:line]` = read from the repo. `[ASSUMED]` = reasoned from design, **needs a Wave-0 probe before the scenario is locked**.

**Legend for "Lever":** `scale` = `kubectl scale` only (preferred per roadmap) · `http` = host HTTP traffic only (cheapest, zero risk) · `data` = a Postgres/API data change, no code · `seam` = `kubectl set env` seam arming (last resort) · `none` = accepted unproven.

| # | Panel | Type | Baseline observed | Lever | Fault to drive | Predicted movement | Confidence |
|---|---|---|---|---|---|---|---|
| 1 | Orchestrator consumed vs sent (conservation) | timeseries | sent 1.32 / consumed 0.709 ops | **scale** | `deployment/orchestrator --replicas=0`, dwell, restore to 3 | both legend Means fall toward 0; line gaps | HIGH |
| 2 | Keeper consumed vs sent (conservation) | timeseries, **guarded** | flat 0, guard firing (unlabelled series) | **scale** (probe) → **seam** (fallback) | a real recovery event: `deployment/processor-sample --replicas=0` mid-flight forces orchestrator `RelocateTail` → `keeper-recovery` | guard stops firing; legend series **gains a label** (`consumed keeper`); Mean > 0 | **MEDIUM — Wave-0 probe required** |
| 3 | Processor consumed vs sent by image | timeseries | 0.759 / 0.725 ops, one `identityName` | **scale** | `deployment/processor-sample --replicas=0`, dwell, restore to 2 | both Means → 0 / No data | HIGH |
| 4 | Orchestrator sends minus processor pickups | stat, `increase[$__range]` | ratio 1.8:1, monotonically growing | **scale** | same processor scale-0 — orchestrator keeps sending, pickups stop | the **slope** jumps (level always grows; see Regime C) | HIGH |
| 5 | Per-processor dispatch gap | timeseries, by `processorId` | 1 series, ~0.60 | **scale** | same processor scale-0 | series **empties** rather than spiking — see the caveat below | HIGH (mechanism), MEDIUM (exact transition) |
| 6 | Orchestrator unresolved steps in range | stat, **guarded** | 0 (green) | **data** | seed a **separate** workflow carrying a dangling `nextStepIds` edge, activate, let it fire, stop, delete | 0 → ≥1, green → red | MEDIUM |
| 7 | Processor dropped spawns in range | stat, **guarded** | 0 (green) | **none** | **ACCEPTED UNPROVEN (user-locked)** | — | n/a |
| 8 | Keeper consumed − sent gap in range | stat, **guarded** | 0 (green) | **seam** (unavoidable) | real recovery event + `KEEPER_DEFEAT_REINJECT=1` on `deployment/keeper` | 0 → ≥1, green → red | MEDIUM-HIGH |
| 9 | Keeper L2 probe heartbeat | timeseries, **unguarded** | flat 0.4001–0.4005 ops | **scale** | `deployment/keeper --replicas=0`, dwell, restore to 2 | line → No data (deliberately unguarded, per §5) | HIGH |
| 10 | WebApi request rate by route | timeseries | idle / near-empty | **http** | drive non-health routes against `localhost:8080` | route series appear, Mean > 0 | HIGH |
| 11 | WebApi p95 request duration by route | timeseries | empty | **http** | same drive; add a deliberately slower route if one exists | empty → a p95 value per route | HIGH |
| 12 | WebApi status-code mix | timeseries | 2xx only / idle | **http** | drive 2xx + a 404 (unknown path) + a 400 (malformed body) | legend gains `404` / `400` series | HIGH |
| 13 | WebApi 5xx ratio | stat, **guarded** | 0 % (green) | **http** (probe) → **scale** (fallback) | find an endpoint that 500s on safe input; else drive a dependency outage | 0 → > 0, green → red | **LOW-MEDIUM — Wave-0 probe required** |
| 14 | WebApi in-flight + Kestrel connections | timeseries, **gauges** | 0 / 0 / 0 | **http** | **sustained** concurrent load spanning ≥ 2 export ticks (≥ 150 s) | in-flight and `kestrel_active_connections` > 0 | MEDIUM (needs sustained load — see DISC-07) |

### Panel-by-panel notes that change the plan

**Panel 2 — the highest-value scenario, and the one needing a probe first.**
`keeper_messages_consumed_total` / `_sent_total` increment only inside a recovery event (`RecoveryConsumerBase.Consume` / `CountSent`) [CITED: 87-FINDINGS.md §6]. Crucially, they were **absent from the entire `__name__` index** at Phase-87 measurement time — not merely at zero, never emitted in the retention window at all. With retention at ~12 h [CITED: 87-FINDINGS.md §14], the prior resilience sweeps that certainly did drive recoveries fall outside the window, so **whether a plain processor-tier crash produces keeper recovery traffic is genuinely unknown in this cluster and must be measured, not assumed** `[ASSUMED]`.

The mechanism argues yes: the orchestrator sends to `keeper-recovery` from two sites, `OrchestratorPrePipeline.cs:213` and `RelocateTail.cs:105` [CITED: 87-FINDINGS.md §13], and a whole-tier processor crash with in-flight work is exactly the condition `RelocateTail` exists for — it is what `TEST-02` in `scripts/phase-80-harness.ps1` drives [CITED: phase-80-harness.ps1:124]. **Wave-0 probe:** during traffic, `kubectl -n skp scale deployment/processor-sample --replicas=0`, dwell 90 s, restore to 2, then check whether `keeper_messages_consumed_total` exists at all. If it does, panel 2 is provable with **no seam** — the roadmap's stated preference. If it does not, fall back to `PROCESSOR_DEFEAT_READ=1` on `processor-sample` (the Phase-79 FALSIFY-01 trigger side) **with `KEEPER_DEFEAT_REINJECT` left unset**, so the keeper consumes *and* reinjects.

**The guard's legend behaviour is itself the cleanest movement signal, and it is free.** `sum by (source)(rate(keeper_messages_consumed_total{…})) or vector(0)` — when the counter does not exist, the left side is empty and `vector(0)` supplies a **label-less** series, which `legendFormat: "consumed {{source}}"` renders as the literal `consumed ` with a trailing space. The moment the counter exists, the left side wins and the legend reads `consumed keeper`. So panel 2's discrimination is assertable as a **string change in the legend row name** as well as a numeric change — two independent signals from one capture. Assert both.

**Panel 5 — a misleading-by-default finding waiting to be recorded (HAND-02).**
The expression is `sum by (processorId)(rate(orch_sent)) - sum by (processorId)(rate(proc_consumed))`. PromQL's binary `-` requires matching label sets on both sides [CITED: 87-FINDINGS.md §10, which measured `identityName` substitution producing **0 series** for exactly this reason]. When processors die and their series eventually expire from the collector's 5-minute `metric_expiration`, the right side vanishes, no label set matches, and the panel goes **empty — not high**. A maintenance operator watching for "the gap climbed" would see the panel disappear instead. There is also a transitional phase: for roughly the first 5 minutes after the pods die, the stale series still exist and `rate()` over 240 s reads 0, so the gap first *spikes to the orchestrator's full send rate* and only then empties. **Both phases should be captured** — the spike is the useful signal, the emptiness is the trap, and the trap is a HAND-02 row.

**Panel 6 — drivable by data, but respect the ghost-cron hazard.**
`orchestrator_step_unresolved` increments once per entry in `selection.UnresolvedIds` — a **dangling next-step edge** [CITED: `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:181-186`, with a second increment site at `:78`]. It never throws and always acks [CITED: `OrchestratorPrePipeline.cs:31-52`], so this fault is **non-destructive by design** — the safest fault in the phase. **Do not mutate `v8-fanout-proof`.** Seed a *separate* workflow with one dangling edge, activate it, let it fire once or twice, then **stop it via the API before deleting any rows**. Deleting workflow rows while the orchestrator is running leaves an orphaned Quartz cron firing forever and breaks later conservation checks — a documented, previously-experienced failure in this repo ([[manual-graph-cleanup-ghost-cron-mg1]]). If cleanup is ever done by hand, flush Redis and restart the orchestrator afterwards.

**Panel 7 — accepted unproven; what to record instead.**
`processor_spawn_dropped_total` is incremented by the `OnSpawnDropped` telemetry hook when `SpawnToPost` exhausts its transient sends. Phase 85 (PB-01/PB-02) made that path **fail-loud**: the exhaustion now throws `SpawnSendExhaustedException`, the pipeline nack-requeues the entry, and the whole seed re-fires [CITED: ROADMAP.md Phase 85 SC 1-2]. Two consequences for the record:
- Driving it requires the broker unreachable *at the instant a fan-out entry spawns* — the riskiest injection in the set, and Phase 86's memory of the unreachable-broker crash-loop is exactly why the user ruled it out.
- **Because the drop is fail-loud, it is well-corroborated in logs.** The runbook row should read: *"Panel 7 non-zero → open the processor logs for the same window and search `SpawnSendExhaustedException`; the entry will have been nack-requeued, so expect a matching re-fire of the same entry. Check broker health in the same window — an exhausted retry chain almost always means RabbitMQ was unreachable. Panel 7 has never been observed non-zero on this stack; treat a first non-zero reading as credible but confirm against the logs before acting."* The findings record should state plainly: **proven to render `0`, never proven to render non-zero; the guard means `0` is indistinguishable from "the counter has never been emitted".**

**Panel 8 — the one panel that genuinely needs a seam.**
Scaling the keeper to 0 does **not** produce a gap: a keeper that is not running consumes nothing, so consumed and sent both stay at 0 and the difference stays 0. The gap requires the keeper to *consume and not send*, which is precisely what `KEEPER_DEFEAT_REINJECT=1` (`src/Keeper/Recovery/ReinjectConsumer.cs:104`, commit `7ec9169`) does. This is why roadmap success criterion 5 exists as a deliverable. Note `src/Keeper/Recovery/ReinjectConsumer.cs` is **modified in the working tree** (git status) and has been since before Phase 87 — verify the deployed `keeper:tags-const-1544` image actually contains the committed seam before relying on it. `KEEPER_REINJECT_DELAY_MS` at `:55` is uncommitted and is **not needed** for this scenario.

**Panel 13 — the second panel needing a Wave-0 probe.**
`[ASSUMED]` A safe route to a genuine 500 is not established. Three candidates, in preference order:
1. **Probe for an endpoint that 500s on safe input.** Most bad input yields 400/404/422, not 500. This must be enumerated empirically against the live WebApi, not guessed.
2. **Dependency outage.** `kubectl scale statefulset/redis --replicas=0` is precedented (`TEST-05` in `phase-80-harness.ps1:128`) and self-recovering, but it **wipes L2 — redis has no PVC** — and Phase 86 introduced a **hard readiness latch** (once a required dep fails for its `failureThreshold` window, `/health/ready` latches Unhealthy until process restart, deliberately no self-heal) [CITED: ROADMAP.md Phase 86 SC 5]. So this route costs a WebApi pod restart to recover and perturbs the pipeline. Heavy but bounded.
3. **Accept unproven** and record it in HAND-04 alongside panel 7.
Recommend probing (1) first; fall to (3) rather than (2) unless the plan explicitly budgets for the restart. Note the guard behaves correctly either way: `sum(rate(…{5..})) / sum(rate(…)) or vector(0)` — the numerator series does not exist until the first 5xx ever occurs, so the whole ratio is empty and the guard supplies 0; after one 5xx the series exists permanently and the ratio computes normally.

**Panel 14 — the phase's natural minimum-detectable-duration exhibit.**
`http_server_active_requests`, `kestrel_active_connections` and `kestrel_queued_connections` are **gauges**, sampled at the SDK's 60 s export cadence. A request that begins and ends between two exports is **invisible** — the value is never recorded as anything but 0. This is categorically different from the counter panels, where the increments survive and only the *timing* is smeared. Driving it therefore requires **sustained** concurrency spanning at least two export ticks (≥ 150 s to be safe), not a burst. Modest concurrency (~10 parallel requesters) is ample on a dev cluster.

### What NOT to drive

- **Do not run `kubectl apply -k k8s/`.** It reverts all four app Deployments from the live `:tags-const-1544` images to the manifests' stale `:local` pins, and **the stale bits do not emit the `source` resource attribute at all** — every dashboard expression filters on `source`, so a stale-image cluster makes correct dashboards look broken [CITED: 87-FINDINGS.md §9, measured]. If the stack must be brought up, use `scripts/phase-80-up.ps1` and be prepared to `kubectl set image` all four back, or use `-SkipBringUp` with manual forwards ([[k8s-local-image-stale-tag-rebuild]]).
- **Do not touch postgres.** Nothing on the business dashboard reads it and a Postgres outage latches readiness across the stack.
- **Do not delete workflow rows while the orchestrator runs** (ghost cron — see panel 6).
- **Do not scale `otel-collector`, `prometheus`, or `grafana`.** Killing the telemetry path makes every panel move, which proves nothing and destroys the baselines.

---

## Asserting "a panel moved" from the rendered DOM

### The enabling fact: every panel is already text

| Panel type | Count | Where the number lives | Already configured? |
|---|---|---|---|
| `timeseries` with table legend + `calcs: ["mean","max"]` | 8 (ids 1,2,3,5,9,10,11,12,14) | legend table row: series name, Mean cell, Max cell — all DOM text | **Yes** — every timeseries panel in `business.json` ships `"legend": { "displayMode": "table", "placement": "bottom", "calcs": ["mean","max"] }` |
| `stat` | 5 (ids 4,6,7,8,13) | the big-number text node inside the panel content | **Yes** — `"textMode": "auto"`, `"graphMode": "none"` |

No dashboard edit is required to make the panels machine-readable. This is the single most important mechanical finding in the phase and it should be stated in the plan so nobody proposes adding testids to the JSON.

### Verified Grafana 12.3.9 selectors

Read from the pinned version tag's own selector definitions `[VERIFIED: raw.githubusercontent.com/grafana/grafana/v12.3.9/packages/grafana-e2e-selectors/src/selectors/components.ts]`:

| Purpose | Selector literal | Since |
|---|---|---|
| Panel by title | `data-testid Panel header ${title}` | 11.0.0 |
| Panel body/viz | `data-testid panel content` | 11.1.0 |
| Legend row by series name | `data-testid VizLegend series ${name}` | 10.3.0 |
| Panel header item (menu, description icon container) | `data-testid Panel header item ${item}` | 10.2.0 |
| Panel error/status | `data-testid Panel status ${status}` (fallback `Panel status`) | 10.2.0 |
| Loading indicator | `Panel loading bar` | 10.0.0 |

These are all `data-testid` **attribute values**, so the Playwright locator is `page.getByTestId('data-testid panel content')` — the string genuinely begins with `data-testid `, which reads oddly but is correct.

### Options, ranked by fragility

| Rank | Approach | Fragility | Verdict |
|---|---|---|---|
| **1 (use this)** | Navigate to **single-panel view** `?viewPanel=panel-<id>` with `&kiosk`, then `getByTestId('data-testid panel content').innerText()` | **Lowest.** Exactly one panel on the page, so no panel-scoping locator is needed at all. Works identically for `stat` and `timeseries` (the legend is inside the panel content). | Primary assertion mechanism |
| 2 | Full dashboard, locate `data-testid Panel header <title>`, walk to its sibling `data-testid panel content` | Medium — the DOM relationship between header and content is not itself a contract | Fallback only if `viewPanel` misbehaves |
| 3 | Legend row by name: `getByTestId('data-testid VizLegend series consumed keeper')` | Medium — the series **name changes** when an `or vector(0)` guard stops firing, which is exactly the movement being measured. Use positional row indexing as well as name matching, and assert the name change explicitly | Use for per-series reads, never as the only locator |
| 4 | Grafana datasource proxy `query_range` cross-check | n/a — accurate but **not the verdict** (locked constraint 2) | Diagnosis only. Record it in the artifact as `DiagnosticQueryValue` beside the `PanelRenderedValue`, and flag disagreement loudly |
| ✗ | Emotion class names (`css-1a2b3c`) | **Build hashes. Never select on these.** Proven this session | Forbidden |
| ✗ | Screenshot pixel diffing | Anti-aliasing, font rendering, axis auto-scale | Forbidden as a verdict; screenshots are evidence only |
| ✗ | The description ⓘ icon by role/name | It is a bare `<span>` inside `[data-testid="title-items-container"]` with **no aria-label**. Proven this session. Locate by container + index if needed | Avoid |

**`viewPanel` URL format caveat `[CITED: grafana.com/docs/grafana/latest/whatsnew/whats-new-in-v11-3/]`:** with the Scenes migration (GA in 11.3, so 12.3.9 is Scenes-based) the view-panel URL uses a `panel-` prefix and repeated-panel clones gain a `-cloneN` suffix (`&viewPanel=panel-3-clone1`). Old bare-`viewPanel=<id>` URLs are documented as redirecting to the dashboard with a "Panel not found" error. None of `business.json`'s 14 panels use `repeat`, so no clone suffix applies — but **confirm the exact working format empirically in Wave 0** against the live 12.3.9 instance before the harness depends on it. `[MEDIUM confidence]`

### The two mechanical requirements that make the numbers comparable at all

These are correctness preconditions, not niceties. Get either wrong and the whole phase's before/after comparison is void.

**1. Pin the time window absolutely.** The legend's Mean and Max are computed **over the visible time range**. With the dashboard's shipped default `time: {from: "now-1h"}` the window slides between captures, so a "baseline" and an "after" would be computed over overlapping and differently-populated data. Every capture must use absolute epoch-millisecond bounds of **identical width**:

```
http://127.0.0.1:3000/d/skp-business/?viewPanel=panel-9
  &from=1785224400000&to=1785225000000
  &var-datasource=<uid>&var-source=All&var-pod=All
  &kiosk&refresh=
```

Baseline window covers only settled pre-fault time; after-window covers only fault time; both the same width. `&refresh=` (empty) disables the 30 s auto-refresh so the render does not move under the assertion.

**2. Pin the viewport.** Grafana derives the query `step` from `max(datasource.timeInterval, range / maxDataPoints)`, and `maxDataPoints` defaults to the panel's **pixel width**. A different browser window gives a different step, hence potentially a different `$__rate_interval`, hence different smearing.

The arithmetic, reproducing Grafana's own [CITED: `phase-87-dashboards-verify.ps1:305-311`]: `$__interval` floors at `timeInterval` (60 s); `$__rate_interval = max(4 × timeInterval, $__interval + timeInterval)` = `max(240, 120)` = **240 s**. Usefully, `range / maxDataPoints` stays below 60 s for any window narrower than roughly `maxDataPoints × 60 s` ≈ 13 h at ~800 px, so **`$__rate_interval` is pinned at 240 s for every window this phase will use**, regardless of window width — provided the viewport is fixed. Set it explicitly (e.g. 1920 × 1080) in every Playwright script and record it in the artifact.

---

## Baseline Banding and the "Did It Move?" Rule

### The panels do not share one statistical regime

A single rule cannot serve all fourteen. Three regimes exist, and the plan must assign every panel to one:

| Regime | Panels | Baseline character | "Moved" rule |
|---|---|---|---|
| **A — jittering rate/level** | 1, 3, 5, 9, 10, 11, 12, 14 | a non-zero value with sampling noise | outside the band, in the predicted direction, for ≥ 2 consecutive samples |
| **B — zero-floor event counter** | 2, 6, 7, 8, 13 | **exactly 0**, guarded by `or vector(0)` | any non-zero reading; detection floor is literally 1 event |
| **C — monotonic range-cumulative stat** | 4 | grows without bound as the window fills (`increase(...[$__range])`) | the **slope** (Δvalue / Δwindow) leaves its band — the *level* is meaningless |

Regime B is the easiest and strongest form of discrimination available: no statistics are needed, the null hypothesis is exactly zero, and a single event falsifies it. Regime C is the subtlest — panel 4's own tooltip already says "read the trend, not the value" [CITED: business.json panel 4 description], and the assertion must honour that. Applying a Regime-A band to panel 4 would produce a guaranteed false positive, because the value grows on a healthy stack too.

### The proposed rule (Regime A)

Defensible without over-engineering:

1. **Capture** N ≥ 10 consecutive 60 s samples during settled steady state — i.e. a ≥ 10-minute absolute window. Read the panel's legend Mean per series.
2. **Band** = `mean ± 3σ`, with a **floor of ±10 % of the mean**. The floor matters: panel 9 measures 0.4001–0.4005 ops [CITED: business.json panel 9 description], so `3σ ≈ 0.0006` — an absurdly tight band that any trivial perturbation would breach. The floor keeps the band honest without weakening it where it matters (panel 9's real fault takes it from 0.4 to No data, which clears any band by miles).
3. **Moved** = the after-window value lies outside the band, in the predicted direction, and stays outside for **≥ 2 consecutive 60 s samples**. The two-sample requirement is the Nyquist-grounded part: at a 60 s sampling interval, a single excursion is not distinguishable from a sampling artifact, and requiring two is the minimum that is.
4. **Stayed put (cross-talk)** = the value remains inside the band for the entire fault window. A cross-talk panel that drifts outside is a finding — either the fault has wider blast radius than designed, or the panel is wired to the wrong metric, which is precisely what the control exists to catch.
5. **"No data" is a state, not a missing measurement.** For a fault that empties a panel (9 under keeper scale-0; 5 under processor scale-0 after expiry), record the state as `NoData` and count it as moved *if that was the predicted direction*. Panel 9 is deliberately unguarded so a dead keeper reads "No data" rather than a comfortable 0 [CITED: 87-FINDINGS.md §5] — so "No data" is the *correct* discrimination signal there, not a measurement failure.

Do not add: rolling z-scores, changepoint detection, seasonal decomposition, or hypothesis tests beyond the above. The sample counts (10–20 per window) do not support them and they would not change any verdict.

### What the band must be captured against

The baseline must be re-captured **after** any rollout, per DISC-06 — not reused from before. Arming a seam or scaling a tier mints new pods with new `service_instance_id` values, so every `by (service_instance_id)` panel (9, 14) gains new series and every aggregate briefly dips while the new pods warm up. Sequence for every scale- or seam-dependent scenario:

```
capture pre-arm baseline (optional, for the record)
  → arm (set env / scale)
  → kubectl rollout status --timeout=<n>s
  → settle ≥ 2 export cadences (≥ 150 s) with traffic flowing
  → RE-CAPTURE the authoritative baseline    ← DISC-01 value comes from HERE
  → trigger the fault
  → capture after
  → restore, disarm, verify clean
```

Record the discontinuity explicitly in the artifact — `RolloutOldInstanceIds`, `RolloutNewInstanceIds`, `RolloutUtc` — so a reader can see it was expected. Without this the restart and the fault signal are indistinguishable and the assertion is void.

---

## Minimum Detectable Fault Duration

The roadmap treats this as one number. **It is two, because counters and gauges fail differently**, and stating it as one would mislead maintenance in exactly the direction the phase exists to prevent.

### The signal chain

fault occurs → SDK `PeriodicExportingMetricReader` exports every **60 s** → collector forwards with **explicit millisecond timestamps** → Prometheus honours the explicit timestamp and discards re-scrapes bearing one it already stored, so the **stored resolution is 60 s, not the 15 s scrape interval** [CITED: 87-FINDINGS.md §11, measured: `count_over_time(<series>[2m]) == 2`, value-change gaps of 60 s] → Grafana `rate()` over **240 s** → panel.

### The two answers

**Counter-backed panels (1–13 except 14):** a counter is cumulative. A fault occurring entirely between two exports still increments the counter, and the increment appears in the very next export. **The event is never lost — only its timing is smeared** across the 240 s rate window. A fault of duration `D` that drops a rate to zero produces a trough of roughly `baseline × (1 − D/240)`. So:
- D = 60 s → ~25 % dip
- D = 120 s → ~50 % dip
- D ≥ 240 s → the rate reads 0

The minimum detectable D is therefore the smallest D whose dip clears the Regime-A band: `D_min ≈ 240 × (band_half_width / baseline)`. With a ±10 % floor that is ≈ 24 s in theory — but the two-consecutive-samples rule raises the practical floor to ≥ 120 s. **Predicted: 120 s for counter-backed panels.** For Regime-B panels the answer is different and much better: a single event is detectable at **any** duration, because the counter goes from 0 to 1 and stays there for the whole `$__range` window.

**Gauge-backed panels (14, and partially 11's histogram):** a gauge is sampled, not accumulated. A condition that begins and ends between two exports **is never recorded**. Worst case a 59 s outage is completely invisible. **Predicted: ≥ 120 s (2 × export cadence) for reliable detection; anything shorter is a coin flip.**

### How to measure it rigorously

A step-ladder on the cheapest lever, with full settle between rungs so each measurement is independent:

| Rung | Fault duration | Lever |
|---|---|---|
| 1 | 30 s | `deployment/processor-sample --replicas=0` → restore 2 |
| 2 | 60 s | same |
| 3 | 120 s | same |
| 4 | 240 s | same |
| 5 | 480 s | same |

For each rung: capture the pinned baseline window, apply the fault, restore, wait for the rate window to fully clear (≥ 240 s + settle), then capture the after-window and evaluate against the Regime-A rule on panels 3 and 4. The **smallest rung that moved** is the measured minimum detectable duration for the counter regime. Repeat rungs 1–3 on panel 14 with an HTTP-load lever to get the gauge-regime number.

Budget: 5 rungs × (D + ~8 min settle and capture) ≈ **60–75 minutes**. This warrants its own plan. Report both numbers, both regimes, and the theory alongside the measurement so a future reader can tell whether a re-measurement disagrees because the stack changed or because the run was noisy.

**Retention constraint:** Prometheus retention was measured at only ~12 h [CITED: 87-FINDINGS.md §14]. The whole phase's evidence must be captured into artifacts **during** the run — screenshots and JSON — because re-querying a day later will find nothing. The harness should record the oldest available sample at run start so the artifact states its own evidentiary horizon.

---

## Scenario Safety and Restore

### Live-verified restore table `[VERIFIED: kubectl -n skp get deploy, 2026-07-28]`

| Workload | Kind | Replicas | Image |
|---|---|---|---|
| `baseapi-service` | Deployment | **1** | `baseapi-service:tags-const-1544` |
| `keeper` | Deployment | **2** | `keeper:tags-const-1544` |
| `orchestrator` | Deployment | **3** | `orchestrator:tags-const-1544` |
| `processor-sample` | Deployment | **2** | `processor-sample:tags-const-1544` |
| `grafana` | Deployment | 1 | `grafana/grafana:12.3.9` |
| `otel-collector` | Deployment | 1 | `otel/opentelemetry-collector-contrib:0.152.0` |
| `prometheus` | Deployment | 1 | `prom/prometheus:v3.11.3` |

### 🔴 A stale map that would silently damage the stack

`scripts/phase-80-harness.ps1:148` declares:

```powershell
$TierReplicas = @{
    'processor-sample' = 2; 'orchestrator' = 1; 'keeper' = 2; 'redis' = 1; 'rabbitmq' = 1
}
```

**`orchestrator = 1` is stale.** `k8s/31-orchestrator.yaml:41` sets `replicas: 3` ("HA — leader-elected via k8s Lease (HA-06)"), raised by Phase 83, and the live deployment confirms 3. Copying this map into a Phase-88 harness would restore the orchestrator to **1 replica and never put it back**, silently dismantling the HA the previous milestone shipped — and it would do so *quietly*, because a single orchestrator still works.

**Mandated pattern for Phase 88:** read the current replica count from the live deployment immediately before scaling and restore to *that*, never to a hardcoded map:

```powershell
$before = kubectl -n skp get deploy $tier -o jsonpath='{.spec.replicas}'
$beforeExit = $LASTEXITCODE          # pin BEFORE the trim
$beforeCount = [int](("$before").Trim())
# ... scale 0, dwell ...
kubectl -n skp scale deployment/$tier --replicas=$beforeCount
```

This is strictly safer, self-maintaining, and it removes the class of bug entirely. The harness should also assert the restored count equals `$beforeCount` and record both in the artifact. **Fix or explicitly avoid the stale map — do not copy it.**

### Precedent to reuse from `scripts/phase-80-harness.ps1`

The crash/restore sequencer at `:360-420` is the shape to follow, minus the replica map:

1. `kubectl -n skp scale <kind>/<tier> --replicas=0` → non-zero exit is an infra abort with its own code (`:383-384`)
2. **Wait for 0 running pods** — bounded 90 s, abort if pods survive (`:395`)
3. Dwell the scenario's fault duration
4. Scale back to the **pre-read** count (never a blanket 1 — the ×2 tiers would be halved)
5. **Readiness gate:** block until all replicas Ready, bounded 120 s, *before* pinning the recovery timestamp (`:410-414`)

`$TierKind` at `:143` is still correct: `processor-sample`/`orchestrator`/`keeper` are Deployments; `redis`/`rabbitmq` are StatefulSets.

### The seam mechanism (DISC-05 deliverable — nothing like it exists today)

`[VERIFIED: repo grep + kubectl get deploy]` The seams are committed in `src/` but present in **no** `k8s/` manifest and on **no** live pod (the deliberate Phase-80 D-08 omission). The only referencing scripts, `phase-67-harness.ps1` and `phase-79-falsify.ps1`, are compose-era; the latter merely *mentions* `KEEPER_DEFEAT_REINJECT` in a doc comment and neither exports it nor calls `kubectl`. So this mechanism is genuinely new:

```powershell
# ARM (triggers a rollout)
kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT=1
kubectl -n skp rollout status deployment/keeper --timeout=180s
# ... settle >= 150s, RE-BASELINE, then trigger ...

# DISARM (triggers a second rollout) — the trailing hyphen removes the var
kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT-
kubectl -n skp rollout status deployment/keeper --timeout=180s
```

**Restoration assertion (DISC-05's "byte-identical afterwards").** Verify, do not assume:

```powershell
kubectl -n skp get deploy keeper           -o jsonpath='{.spec.template.spec.containers[0].env}'
kubectl -n skp get deploy processor-sample -o jsonpath='{.spec.template.spec.containers[0].env}'
```

Both must contain **zero** occurrences of `DEFEAT` or `REINJECT_DELAY`. Assert replicas and images too. Put this in the harness's `finally` block so an interrupted run still disarms — a seam left armed would poison every subsequent scenario in the phase and every later sweep, and the failure would present as an inexplicable conservation violation.

Both seams short-circuit to inert when unset [CITED: ROADMAP.md Phase 88 precondition table], so an accidental empty value is safe — but do not rely on that as the disarm path.

### Standing hazards to carry into the plan

| Hazard | Consequence | Mitigation |
|---|---|---|
| `kubectl apply -k k8s/` reverts images to `:local` | Stale bits emit **no `source` label**; every panel expression filters on it → correct dashboards look broken | Do not apply. If unavoidable, `kubectl set image` all four back to `:tags-const-1544` |
| Deleting workflow rows with the orchestrator running | Orphaned Quartz cron fires forever; conservation checks fail with `Missing=0` | Stop via API first; if hand-cleaning, flush Redis + restart orchestrator |
| Redis scale-0 wipes L2 (no PVC) | Executions in flight are lost; Phase-86 readiness latch requires a restart to recover | Only as a budgeted TEST-05-equivalent; prefer not to |
| `KEEPER_REINJECT_DELAY_MS` uncommitted | Could be lost; the deployed image may not contain it | Not needed for any Phase-88 scenario. Do not use it |
| A concurrent resilience sweep | Would confound every measurement | Confirm nothing else is running against `skp` before starting (the T-87-19 precedent) |
| ~12 h Prometheus retention | Evidence evaporates | Capture artifacts during the run; record oldest-sample-at-start |

---

## Report / Verdict Artifact Shape

Two in-repo precedents, and this phase needs both.

**Single-proof schema — `analyzer-reports/phase-83-ha.json`** `[VERIFIED: file read]`: one JSON object, PascalCase keys, `ScenarioId` first, `Verdict` second, then one boolean per claim, then measured scalars and round-trip (`"o"`) timestamps, `HumanSummary` last. `phase-87-dashboards.json` follows it and extends it with diagnostic arrays (`EmptyClassAExprs`, `ClassBExprs`, `MissingDropdownPods`) so every failure mode is nameable from the artifact alone.

**Roll-up schema — `analyzer-reports/phase-81-summary.json`** `[VERIFIED: file read]`: a JSON **array**, camelCase keys, one row per scenario: `scenarioId`, `verdict`, per-claim booleans, measured counts, `harnessExit`, `class`.

**Recommended for Phase 88 — a hybrid, because the unit of proof is the *panel*, not the scenario:**

1. **Per-scenario:** `analyzer-reports/phase-88-<scenario-id>.json` in the `phase-83-ha.json` single-proof schema. Beyond the standard fields, each must carry: `PanelId`, `PanelTitle`, `Regime` (A/B/C), `BaselineWindowStart`/`End`, `AfterWindowStart`/`End` (all `"o"`), `BaselineValues[]`, `AfterValues[]`, `BandLow`/`BandHigh`, `Moved` (bool), `Direction`, `ConsecutiveSamplesOutside`, `CrossTalkPanels[]` each with its own `StayedPut` bool, `RolloutOldInstanceIds`/`RolloutNewInstanceIds`/`RolloutUtc` (DISC-06), `ReplicasBefore`/`ReplicasAfter`, `SeamVarsAfter` (must be empty), `ViewportWidth`/`Height`, `RateIntervalSeconds`, `DiagnosticQueryValue` + `DiagnosticAgreesWithPanel`, and `ScreenshotPaths[]`.
2. **Roll-up:** `analyzer-reports/phase-88-discrimination.json` — a **14-row array, one row per panel**, not one per scenario: `panelId`, `panelTitle`, `regime`, `baselineBand`, `observedStates[]`, `provenBy` (scenario id or `null`), `status` (`Proven` / `AcceptedUnproven`), `reason`, `misleadingByDefault` (bool + note). This array *is* the DISC-02 answer and the HAND-02 input, and it makes "10 of 14 have only ever been seen in one state" a number the artifact reports rather than a claim the prose makes.

**Preserve the Phase-87 verdict-precedence fix:** `Fail` beats `Inconclusive`. A claim evaluated and returned false is positive evidence of a defect; no quantity of *other* unevaluated claims makes it less true. `Inconclusive` means only "nothing failed, but something could not be checked" [CITED: 87-FINDINGS.md §12, where the original precedence downgraded 16 genuine failures]. `Resolve-AnalyzerExitCode` in `scripts/lib/exit-code-resolution.ps1` already encodes this — dot-source it, never reimplement.

---

## The Maintenance-Practicality Rubric (HAND-03)

Six questions, yes / partial / no, per panel. No weights, no aggregate score — the **blocking-gap list is the verdict**. This is deliberately small: the phase is a discrimination proof, not a UX study.

| # | Question | Test | Evidence source |
|---|---|---|---|
| 1 | **Nameable** — can someone who did not build this say what the panel measures from the title and tooltip alone? | The description states *what it shows*, *what healthy looks like*, and *when to investigate* | Panel `description` (all 31 non-row panels carry one, enforced by lint S16) |
| 2 | **Falsifiable** — does "is this normal?" have an answer? | A DISC-01 baseline band exists and is written into the runbook | DISC-01 output |
| 3 | **Discriminating** — has the panel been seen in ≥ 2 states? | DISC-02 proven, or in the HAND-04 accepted-unproven register | roll-up artifact `observedStates[]` |
| 4 | **Actionable** — is there a named next action for the abnormal state? | A HAND-01 runbook row exists citing the proving scenario | runbook |
| 5 | **Non-misleading** — does the default rendering mean what it appears to mean? | Thresholds, axis auto-scale, and `or vector(0)` guards each checked | known failures: **4** and **5** (structural offset can never read 0), **9** (axis zoom magnifies a 0.0004-wide band into apparent volatility), **2/6/7/8/13** (a guarded green `0` is indistinguishable from "never emitted"), **5** (empties instead of spiking) |
| 6 | **Reachable** — can maintenance actually open it? | A documented access path | **Known gap:** loopback `kubectl port-forward` only, anonymous Viewer, `admin`/`admin` in the manifest — an accepted dev posture, out of scope to fix, but a **blocking gap for a genuine handoff** and it must be stated as one |

Score all fourteen. Expect question 5 to fail or partial on at least six panels and question 6 to fail dashboard-wide — those two lists *are* HAND-02's "blocking gaps and panels that mislead by default", so the rubric feeds the verdict directly rather than sitting beside it.

---

## Common Pitfalls

### Pitfall 1: Comparing a sliding window to a sliding window
**What goes wrong:** baseline and after are both captured at `now-1h`, so they overlap and the "after" number is diluted by pre-fault data — or worse, the baseline capture already contains the fault.
**Root cause:** the dashboard ships `time: {from: "now-1h"}` and the legend Mean is computed over the visible range.
**Avoid:** absolute `&from=<epoch_ms>&to=<epoch_ms>` of identical width on every capture, plus `&refresh=` to stop auto-refresh moving the render mid-assertion.
**Warning sign:** an "after" value that is a suspiciously clean fraction of the baseline.

### Pitfall 2: Reading the restart as the fault
**What goes wrong:** `set env` or `scale` rolls the pods; every panel dips and every `by (service_instance_id)` panel gains series. The dip is scored as discrimination.
**Root cause:** new pods mint new `service_instance_id` values [CITED: 87-FINDINGS.md §2 — a k8s restart never resets a series, it starts a new one].
**Avoid:** arm → rollout status → settle ≥ 150 s → **re-baseline** → trigger. Record the instance-id delta as an expected artifact.
**Warning sign:** the "movement" is present on panels the fault has no causal path to.

### Pitfall 3: Proving the query instead of the panel
**What goes wrong:** the harness queries Prometheus, gets the expected number, and declares the panel discriminating.
**Root cause:** the Phase-87 defect verbatim — 30/30 Class A green against three browser panels rendering "No data" [CITED: 87-FINDINGS.md §12].
**Avoid:** locked constraint 2. The DOM is the verdict; the query is a diagnostic recorded beside it. When they disagree, that disagreement is a **finding**, not an error to reconcile away.
**Warning sign:** the artifact contains a query result and no rendered value.

### Pitfall 4: Reusing `$TierReplicas`
**What goes wrong:** the orchestrator is restored to 1 instead of 3 and stays there.
**Avoid:** read `spec.replicas` live before scaling; restore to the read value; assert it afterwards. See the red-flag section above.
**Warning sign:** `kubectl get deploy orchestrator` reads `1/1` after a run.

### Pitfall 5: A left-armed seam
**What goes wrong:** an interrupted run leaves `KEEPER_DEFEAT_REINJECT=1` on the keeper. Every later scenario and every later resilience sweep silently loses recovery messages.
**Avoid:** disarm in the outer `finally`; assert zero seam vars as an explicit claim in every artifact.
**Warning sign:** unexplained conservation violations after the phase.

### Pitfall 6: Treating "No data" as a measurement failure
**What goes wrong:** panel 9 emptying under a keeper outage is logged as "could not read panel" and the scenario is marked Inconclusive.
**Root cause:** panel 9 is *deliberately* unguarded so a dead keeper reads "No data" rather than a comfortable 0 [CITED: 87-FINDINGS.md §5].
**Avoid:** model `NoData` as a first-class panel state with a predicted direction.

### Pitfall 7: Expecting panel 5 to spike
**What goes wrong:** the assertion waits for a high value; the panel empties instead and the scenario fails.
**Root cause:** PromQL binary `-` needs matching label sets; when the processor side expires there is nothing to subtract from.
**Avoid:** predict *both* phases — an initial spike (stale series still present, rate 0) then emptiness (series expired ~5 min later). Capture both.

### Pitfall 8: A burst of HTTP traffic for panel 14
**What goes wrong:** 500 requests fired in 3 s; `http_server_active_requests` reads 0 at every export tick.
**Root cause:** gauges are sampled at 60 s, not accumulated.
**Avoid:** sustained concurrency spanning ≥ 2 export ticks (≥ 150 s).

### Pitfall 9: StrictMode member enumeration
**What goes wrong:** `$obj.PSObject.Properties.Name` throws `The property 'Name' cannot be found on this object` on an empty `{}` — and `business.json` contains several.
**Avoid:** reuse `Get-PropertyNames` from `phase-87-dashboards-verify.ps1:244`. This trap has bitten this repo three times.

### Pitfall 10: Selecting on emotion class names
**What goes wrong:** `.css-1a2b3c` works today and breaks on the next Grafana patch — they are build hashes.
**Avoid:** `data-testid` only, from the verified table above.

---

## Code Examples

### Pinned-window single-panel capture (Playwright)

```javascript
// Source: Grafana v12.3.9 e2e-selectors (verified) + the pinned-window requirement derived here.
// Run via: cd $SKILL_DIR && node run.js /tmp/playwright-panel-read.js
const GRAFANA = process.env.GRAFANA_URL || 'http://127.0.0.1:3000';
const PANEL_ID = process.env.PANEL_ID;      // e.g. '9'
const FROM_MS  = process.env.FROM_MS;       // absolute epoch ms — NEVER now-1h
const TO_MS    = process.env.TO_MS;         // same width for baseline and after

// Fixed viewport: maxDataPoints derives from panel pixel width, which sets the
// query step, which sets $__rate_interval. A different window = a different smear.
const page = await context.newPage();
await page.setViewportSize({ width: 1920, height: 1080 });

const url = `${GRAFANA}/d/skp-business/?viewPanel=panel-${PANEL_ID}`
          + `&from=${FROM_MS}&to=${TO_MS}`
          + `&var-source=All&var-pod=All&kiosk&refresh=`;
await page.goto(url, { waitUntil: 'networkidle' });

// Wait for the query to finish rather than sleeping.
await page.waitForSelector('[data-testid="Panel loading bar"]', { state: 'detached' })
          .catch(() => {});                 // absent entirely on a fast render

// Exactly one panel is on the page in viewPanel mode, so no panel scoping is needed.
const content = page.getByTestId('data-testid panel content');
const text = await content.innerText();     // stat: the big number
                                            // timeseries: legend rows "name\tMean\tMax"
console.log(JSON.stringify({ panelId: PANEL_ID, from: FROM_MS, to: FROM_MS, raw: text }));
await page.screenshot({ path: `/tmp/phase-88-panel-${PANEL_ID}-${FROM_MS}.png` });
```

### Live-read replica capture and restore (PowerShell)

```powershell
# Source: derived here. REPLACES phase-80-harness.ps1's $TierReplicas map, whose
# 'orchestrator' = 1 is stale against k8s/31-orchestrator.yaml:41 (replicas: 3).
function Get-LiveReplicas([string]$Tier) {
    $raw  = kubectl -n skp get deploy $Tier -o jsonpath='{.spec.replicas}'
    $code = $LASTEXITCODE                       # pin BEFORE the trim (phase-87 idiom)
    $txt  = ("$raw").Trim()
    if ($code -ne 0 -or [string]::IsNullOrWhiteSpace($txt)) { return -1 }
    return [int]$txt
}

$restoreTo = Get-LiveReplicas 'processor-sample'
if ($restoreTo -lt 1) { Write-Phase "could not read replicas. Aborting." 'Red'; exit 62 }
# ... scale 0, wait 0 running pods (bounded), dwell ...
kubectl -n skp scale deployment/processor-sample --replicas=$restoreTo | Out-Null
kubectl -n skp rollout status deployment/processor-sample --timeout=180s
$after = Get-LiveReplicas 'processor-sample'
$ReplicasRestored = ($after -eq $restoreTo)     # an explicit CLAIM in the artifact
```

### Seam arm / disarm with a disarm-in-finally guarantee (PowerShell)

```powershell
# Source: derived here. NO equivalent exists in the repo — this is the DISC-05 deliverable.
$seamArmed = $false
try {
    kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT=1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "seam arm failed." 'Red'; exit 61 }
    $seamArmed = $true
    kubectl -n skp rollout status deployment/keeper --timeout=180s
    Start-Sleep -Seconds 150      # >= 2 export cadences BEFORE re-baselining (DISC-06)
    # ... re-capture baseline, trigger the recovery event, capture after ...
} finally {
    if ($seamArmed) {
        kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT- | Out-Null
        kubectl -n skp rollout status deployment/keeper --timeout=180s
    }
    # ALWAYS assert, never assume — a left-armed seam poisons every later run.
    $envRaw  = kubectl -n skp get deploy keeper -o jsonpath='{.spec.template.spec.containers[0].env}'
    $SeamVarsClean = -not (("$envRaw") -match 'DEFEAT|REINJECT_DELAY')
}
```

### Diagnostic cross-check (never the verdict)

```powershell
# Source: scripts/phase-87-dashboards-verify.ps1:220-225 — reuse verbatim in shape.
# Locked constraint 2: this is DIAGNOSIS. Record it beside the rendered value and
# flag disagreement; it never decides Pass/Fail.
$proxy = 'http://127.0.0.1:3000/api/datasources/proxy/uid/skp-prometheus'
$r = Invoke-RestMethod -Method Post -Uri "$proxy/api/v1/query_range" `
       -Headers $auth -ContentType 'application/x-www-form-urlencoded' `
       -Body @{ query = $interpolated; start = $startUnix; end = $endUnix; step = 60 } `
       -TimeoutSec 90 -ErrorAction Stop
```

---

## Runtime State Inventory

This phase mutates live cluster state without changing files. Every mutation must be inventoried and reversed.

| Category | Items | Action required |
|---|---|---|
| **Stored data** | Postgres `workflows` rows — if panel 6's dangling-edge workflow is seeded, one **new** workflow row plus its steps. Redis L2 — untouched unless redis is scaled (recommended: don't) | Stop the workflow via the API **before** deleting rows; if hand-cleaned, flush Redis + restart the orchestrator ([[manual-graph-cleanup-ghost-cron-mg1]]) |
| **Live service config** | Deployment `spec.replicas` on `processor-sample` / `keeper` / `orchestrator`; Deployment `env` on `keeper` / `processor-sample` (seam vars). Neither is in git for these values | Restore replicas to the **live-read** pre-scenario value; remove seam vars with `set env VAR-`; assert both |
| **OS-registered state** | None. No Windows Task Scheduler entry, no pm2 process, no launchd plist involved. Only transient `kubectl port-forward` child processes | Stop this script's own forward PID behind a recycled-PID guard; **never** touch `.k8s-portforward-pids` (it owns the eight shared forwards) |
| **Secrets / env vars** | No secret is read or written. The seam vars are **not** secrets. Grafana `admin`/`admin` is in the manifest already, unchanged | None |
| **Build artifacts** | None — this phase builds nothing. Images stay at `:tags-const-1544`. **Do not run `apply -k`**, which would revert them to stale `:local` | Assert images unchanged at the end of every scenario |
| **Prometheus TSDB** | New series will be minted for new pod identities on every rollout; the pod dropdown will over-return further (already an accepted superset, §7) | None — expected and documented. Record the instance-id delta |

---

## Environment Availability

| Dependency | Required by | Available | Version | Fallback |
|---|---|---|---|---|
| Docker-Desktop k8s, `skp` namespace | every scenario | ✓ | context `docker-desktop` | none — hard requirement |
| `kubectl` | scale / set env / rollout / exec | ✓ | in PATH | none |
| Grafana | the verdict surface | ✓ | `grafana/grafana:12.3.9`, 1 replica | none |
| Prometheus | diagnosis + retention | ✓ | `prom/prometheus:v3.11.3` | none; **~12 h retention** limits evidence lifetime |
| OTel collector | telemetry path | ✓ | `otel/opentelemetry-collector-contrib:0.152.0` | none |
| App images | fault targets | ✓ | all four on `:tags-const-1544` | none — do not revert to `:local` |
| Playwright skill | the verdict mechanism | ✓ | 4.1.0 at `~/.claude/plugins/cache/playwright-skill/playwright-skill/4.1.0/skills/playwright-skill`, `run.js` + `lib/` + `node_modules/` present | none — locked constraint 2 |
| Node.js | runs the skill | ✓ (implied by the skill's `node_modules`) | — | none |
| PowerShell 7 (`pwsh`) | the harness | ✓ | every repo script | none |
| `scripts/lib/exit-code-resolution.ps1` | verdict → exit mapping | ✓ | present | none — dot-source, do not reimplement |
| Shared 8 port-forwards (`phase-80-up.ps1`) | traffic drive on `localhost:8080` | conditional | started by `phase-80-up.ps1` | precondition-gate on `GET http://localhost:8080/health/ready` → 200, exit 64 with a remediation line (the Phase-87 pattern) |
| `dotnet test ~FanOutSeeder` | seeding, if a reseed is needed | ✓ | `tests/BaseApi.Tests` | the workflow is already seeded; prefer `POST /orchestration/start` on the existing `v8-fanout-proof` |
| Grafana image renderer plugin | `/render/d-solo` PNG API | ✗ | not installed | **not needed** — Playwright renders in a real browser, which is the requirement anyway |

**Missing with no fallback:** none.
**Missing with fallback:** none blocking. The Grafana image renderer is absent but irrelevant.

---

## Validation Architecture

`.planning/config.json` — `workflow.nyquist_validation` not explicitly `false`, so this section applies.

### Test framework

| Property | Value |
|---|---|
| Framework | **None applicable.** This phase adds no C# and no hermetic tests. Its validation is a live harness, exactly as Phase 87's was |
| Config file | n/a |
| Quick run | `pwsh -File scripts/phase-88-<name>.ps1 -Scenario <id>` (single scenario) |
| Full suite | the full scenario sweep + roll-up |
| Static gate | `[ScriptBlock]::Create((Get-Content <script> -Raw))` parse check, plus `Select-String` guard assertions — the Phase-87 self-check pattern (`87-05-SUMMARY.md` Verification Results) |

### Requirement → validation map

| Req | Behaviour | Type | Command | Exists? |
|---|---|---|---|---|
| DISC-01 | baseline band per panel, pinned window | live capture | `-Mode Baseline` | ❌ Wave 0 |
| DISC-02 | panel moved under its fault, from the DOM | live scenario | `-Scenario <id>` | ❌ Wave 0 |
| DISC-03 | cross-talk panels stayed put | live scenario | same run, `CrossTalkPanels[]` claims | ❌ Wave 0 |
| DISC-04 | panels 2 and 8 non-zero in a real recovery | live scenario | `-Scenario keeper-recovery` / `keeper-loss` | ❌ Wave 0 |
| DISC-05 | seam arm/disarm, stack restored | live + assertion | `SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` claims | ❌ Wave 0 |
| DISC-06 | re-baseline after rollout; discontinuity recorded | live + artifact field | `RolloutOldInstanceIds` / `RolloutNewInstanceIds` non-empty and distinct | ❌ Wave 0 |
| DISC-07 | minimum detectable duration measured | step-ladder | `-Mode DurationLadder` | ❌ Wave 0 |
| HAND-01/02/03/04 | runbook, verdict, rubric, register | document review | human checkpoint | ❌ Wave 0 |
| — | dashboard JSON still lints (if any panel is recomposed) | hermetic | `pwsh -File scripts/phase-87-dashboard-lint.ps1` | ✅ exists, 23 rules, ~5 s |
| — | Phase-87 proof still green (no regression) | live | `pwsh -File scripts/phase-87-dashboards-verify.ps1` | ✅ exists |

### Sampling rate

- **Per task commit:** `[ScriptBlock]::Create` parse + `scripts/phase-87-dashboard-lint.ps1` (~5 s). Cheap, run always.
- **Per wave merge:** the wave's scenarios re-run to a Pass artifact.
- **Phase gate:** full scenario sweep green + `phase-87-dashboards-verify.ps1` still exit 0 (proving Phase 88 left the dashboards intact) before `/gsd:verify-work`.

### Wave 0 gaps

- [ ] `scripts/phase-88-panel-discriminate.ps1` — the scenario driver (arm / trigger / restore / verdict / artifact)
- [ ] A Playwright panel-reader script + a stable invocation contract (env-var in, JSON on stdout)
- [ ] **A Wave-0 probe plan** answering three blockers *before* the scenarios are locked:
  1. Does a plain `processor-sample` scale-0 produce `keeper_messages_consumed_total` at all? (decides whether panel 2 needs a seam)
  2. Does `?viewPanel=panel-<id>` work on this 12.3.9 instance, and does `getByTestId('data-testid panel content').innerText()` return a parseable number for **both** a stat and a table-legend timeseries?
  3. Is there any WebApi endpoint that returns 5xx on safe input? (decides panel 13's fate)
- [ ] The DISC-01 baseline capture mode, run once on a settled stack to seed every band
- [ ] `analyzer-reports/phase-88-discrimination.json` roll-up writer

---

## Security Domain

`security_enforcement` is not set to `false`, so this section applies. This phase adds no product code, no schema, no auth path — the surface is narrow but non-empty.

### Applicable ASVS categories

| ASVS category | Applies | Standard control |
|---|---|---|
| V2 Authentication | partial | Grafana `admin`/`admin` over loopback — an **accepted dev posture** (v13.0.0 Out of Scope), reachable only via `--address 127.0.0.1`. Keep the credential inline as Phase 87 did; parameterising it would imply a security property this deployment does not have |
| V3 Session Management | no | no sessions introduced |
| V4 Access Control | partial | `kubectl set env` / `scale` are cluster-mutating. Constrain to the `skp` namespace and to a **static in-script workload list** — never derive a workload name from a parameter (the T-81-01 precedent) |
| V5 Input Validation | yes | Scenario ids validated against a static `[ordered]@{}` table with an unknown-id guard → exit 64 (`phase-80-harness.ps1:130-134`). Any psql must be a static string literal, never interpolated (T-87-12) |
| V6 Cryptography | no | none used |
| V7 Error Handling & Logging | yes | Distinct exit code per infra abort; artifact written before the exit is resolved; disarm in `finally` |
| V12 Files & Resources | partial | Screenshots and JSON written under `analyzer-reports/` and `/tmp` only |
| V13 API & Web Service | partial | One new loopback-only network surface: `kubectl port-forward svc/grafana 3000:3000 --address 127.0.0.1`, torn down behind a recycled-PID guard |

### Threat patterns for this stack

| Pattern | STRIDE | Mitigation |
|---|---|---|
| Workload name injected via a script parameter → scaling an unintended workload | Tampering | Static in-script scenario table; the id only *selects* a fixed row (T-81-01) |
| Blanket `--replicas=1` restoring a ×2 or ×3 tier incorrectly | Tampering / DoS | **Read the live count before scaling**; assert equality after (T-81-02, extended — and see the stale-map red flag above) |
| A left-armed fault seam silently corrupting every later run | Tampering | Disarm in `finally` + assert zero seam vars as an explicit claim |
| Port-forward bound to `0.0.0.0`, exposing Grafana off-host | Information Disclosure | `--address 127.0.0.1` explicitly; assert zero `0.0.0.0` occurrences in the script (T-87-01) |
| Killing the eight shared forwards via the shared PID file | DoS | Never read or write `.k8s-portforward-pids`; keep the forward PID in one in-memory variable (T-87-11) |
| Interpolated `psql -c` | Injection | Static string literal only (T-87-12) |
| An infra abort mistaken for a verdict | Repudiation | One distinct exit code per abort; `Resolve-AnalyzerExitCode` dot-sourced and fail-closed (T-87-18) |
| Playwright script written into the skill directory | Tampering | Skill's own rule: write to `/tmp/playwright-*.js` only, never into the skill dir |

---

## Don't Hand-Roll

| Problem | Don't build | Use instead | Why |
|---|---|---|---|
| Verdict → exit code mapping | an inline `switch ($verdict)` | dot-source `scripts/lib/exit-code-resolution.ps1` | Its fail-closed default (unknown/absent verdict → 1) is the T-87-18 control; an inline switch loses it |
| Reading a Grafana panel value | canvas scraping, pixel diffing, OCR | the existing table legend + `data-testid panel content` `innerText` | Every panel is already text — no dashboard change needed |
| Panel scoping in the DOM | header→content DOM walks, CSS class selectors | `?viewPanel=panel-<id>` single-panel view | One panel on the page eliminates the entire locator-fragility class |
| Replica restore | a hardcoded `$TierReplicas` map | live `kubectl get deploy -o jsonpath='{.spec.replicas}'` before scaling | The existing map is already stale on `orchestrator` and would silently dismantle HA |
| Seam injection | editing `k8s/32-keeper.yaml` | `kubectl set env` at runtime | A manifest edit is forbidden by the constraint **and** would be stripped by any `apply -k` |
| Traffic generation | a new load-testing tool | the existing `POST /api/v1/orchestration/start` drive + a simple host HTTP loop | Phase 87 STEP E already proves this drive works; a new tool is a new dependency for no gain |
| JSON parsing in PowerShell | installing `jq` | `ConvertFrom-Json` + the `Get-PropertyNames` helper | No external JSON tooling is installed on this host, by standing convention |
| Prometheus access | a new direct-to-Prometheus client | the Grafana datasource proxy call already in `phase-87-dashboards-verify.ps1:220` | One call covers datasource wiring and PromQL; and it is diagnosis-only anyway |
| Statistical change detection | changepoint detection, z-score streams, seasonal decomposition | mean ± 3σ with a ±10 % floor, ≥ 2 consecutive samples | 10–20 samples per window cannot support anything heavier, and it would not change a verdict |

**Key insight:** almost everything this phase needs already exists in the repo or in Grafana's own contract. The genuinely new artifacts are exactly two — the **runtime-only seam arm/disarm mechanism** (DISC-05, which the roadmap already calls a deliverable) and the **Playwright panel reader**. Everything else is composition.

---

## State of the Art

| Old approach | Current approach | When changed | Impact here |
|---|---|---|---|
| Prove PromQL parses (instant query, wide hardcoded rate interval) | Reproduce the caller's query shape (`query_range`, datasource-derived `$__rate_interval`) | Phase 87, 2026-07-28 | This phase goes one step further — the **rendered panel** is the verdict, not the query at all |
| Grafana Angular/React dashboards, `viewPanel=<id>` | Scenes-powered dashboards, `viewPanel=panel-<id>` (`-cloneN` for repeats) | Grafana 11.3, GA | Confirm the format empirically in Wave 0 |
| Grafana e2e selectors as bare aria strings | `data-testid`-prefixed selectors | 10.2–11.1 | Use the 12.3.9 table above; `data-testid panel content` requires ≥ 11.1.0 |
| `scrape_interval` as the resolution | **SDK export cadence** as the resolution (explicit timestamps) | Phase 87 §11 | 60 s, so `$__rate_interval` = 240 s. This sets the Nyquist floor for the whole phase |
| Compose-based fault injection (`phase-67-harness.ps1`, `phase-79-falsify.ps1`) | `kubectl`-based (`phase-80-harness.ps1`) | Phase 80 | The compose seam scripts are **dead for this purpose** — they neither export the seams nor call `kubectl` |
| Orchestrator single replica | `replicas: 3`, leader-elected via k8s Lease | Phase 83 (HA-06/07) | Makes `phase-80-harness.ps1`'s `$TierReplicas` stale |
| `SpawnToPost` swallows exhaustion | Fail-loud: throws, entry nack-requeues | Phase 85 (PB-01/02) | Panel 7's fault is now well-logged, which is why the log-corroboration runbook row is credible |
| Infra fault trips liveness → crash-loop | Watchdog-only liveness, hard readiness latch, **no self-heal** | Phase 86 | A dependency-outage fault (panel 13 route 2) needs a **restart** to recover — budget for it or avoid it |

**Deprecated / not to be used here:** `scripts/phase-79-falsify.ps1` and `scripts/phase-67-harness.ps1` as seam-arming references (compose-era, and `phase-79-falsify.ps1` only mentions the var in a doc comment); `phase-80-harness.ps1`'s `$TierReplicas` map (stale on `orchestrator`); `KEEPER_REINJECT_DELAY_MS` (uncommitted, not needed).

---

## Assumptions Log

| # | Claim | Section | Risk if wrong |
|---|---|---|---|
| A1 | A plain `processor-sample` scale-0 produces keeper recovery traffic (`keeper_messages_consumed_total` becomes non-zero) | Per-Panel Mapping, panel 2 | **High.** If wrong, panel 2 needs the `PROCESSOR_DEFEAT_READ` seam too, adding a rollout and a re-baseline to the highest-value scenario. **Wave-0 probe mandatory** |
| A2 | `?viewPanel=panel-<id>` is the working single-panel URL on this Grafana 12.3.9 | Playwright section | Medium. Fallback is the header→content DOM walk (option 2), which is more fragile but workable. **Wave-0 probe** |
| A3 | `getByTestId('data-testid panel content').innerText()` returns a parseable number for both stat and table-legend timeseries panels | Playwright section | Medium. If the legend renders inside a separate container, the reader needs a second locator. **Wave-0 probe** |
| A4 | Some WebApi endpoint returns 5xx on safe input | Per-Panel Mapping, panel 13 | Medium. If not, panel 13 joins panel 7 in the accepted-unproven register (acceptable) or needs a dependency outage (costly). **Wave-0 probe** |
| A5 | Panel 5 spikes before it empties (stale series persist for the collector's ~5 min `metric_expiration`) | Panel 5 note | Low. Affects only what the assertion predicts; capture both phases and record what actually happens |
| A6 | The deployed `keeper:tags-const-1544` image contains the committed `KEEPER_DEFEAT_REINJECT` seam | Panel 8 note | Medium. `ReinjectConsumer.cs` is dirty in the working tree; verify the seam responds before the scenario depends on it |
| A7 | Minimum detectable duration ≈ 120 s for counters and ≥ 120 s for gauges | DISC-07 | Low — these are *predictions to be measured*, explicitly labelled as such. The step-ladder replaces them with numbers |
| A8 | A 404 to an unmatched route still yields a `http_response_status_code` label (ASP.NET Core omits `http.route` for unmatched routes) | Panel 12 | Low. Panel 12 groups by status code, not route, so it holds either way |
| A9 | `$__rate_interval` stays pinned at 240 s for every window this phase uses, given a fixed viewport | Playwright / banding | Low-Medium. Verify once in Wave 0 by comparing a narrow and a wide window at the same viewport |
| A10 | Prometheus retention is still ~12 h | Retention constraint | Low. Measured during Phase 87; re-measure at run start and record it in the artifact |

---

## Open Questions (RESOLVED at planning — 2026-07-28)

> All six were closed when Phase 88 was planned. Four are answered mechanically by the blocking
> Wave-0 probe (plan 88-03) and locked into `88-PROBE-DECISIONS.md` before any downstream plan can
> depend on them; two were decided at planning. Resolution index, for a reader who consults this
> document alone:
>
> | # | Resolved by | Outcome |
> |---|---|---|
> | 1 | **PQ-01** — probe 88-03 Task 2 STEP G, branch locked in Task 3 | `KeeperRecoveryTrafficObserved` true → ZERO-02 lever `scale`; false → lever `seam` (`PROCESSOR_DEFEAT_READ`). Exercised by ZERO-02/ZERO-03 in plan 88-07. |
> | 2 | **PQ-02** — probe 88-03 Task 1 STEP D, locator locked in Task 3 | `ViewPanelUrlWorks` + both parse flags → `LocatorMode viewpanel`, else the header-walk fallback. The reader (plan 88-01) is written against whichever wins. |
> | 3 | **PQ-03** — probe 88-03 Task 2 STEP E | `Safe5xxFound` decides whether panel 13 is proven by WEB-02 (plan 88-05) or joins the accepted-unproven register with panel 7. No dependency outage is budgeted. |
> | 4 | **Decided at planning: RECORD, do not fix.** | `k8s/dashboards/business.json` is modified by no plan; 88-10 Task 3 asserts `git status --porcelain k8s/` is empty. Panel 5's empty-instead-of-spike behaviour is carried as a `misleadingByDefault` note in the 88-09 matrix and as a HAND-02 gap in the 88-10 verdict. |
> | 5 | **Decided at planning.** | Eight scenarios plus the ladder, run strictly sequentially across waves 4–7 (plans 88-05 → 88-08), cheapest-and-safest HTTP scenarios first exactly as recommended. One addition the research did not anticipate: **PQ-05** was added after `CycleDetector.cs:91-95` showed the D-08 missing-step gate rejects a dangling `nextStepIds` edge at activation, so panel 6's proposed lever is likely infeasible as written — the probe enumerates an L2-step-key route instead, and panel 6 falls to the register if neither works. |
> | 6 | **Decided at planning.** | `docs/runbooks/business-dashboard-runbook.md` — the location a maintenance department would actually find — with a non-duplicating pointer at `88-RUNBOOK.md` in the phase directory. Authored in plan 88-10. |
>
> Two further probe questions were added beyond the research's list: **PQ-06** (does `kubectl set env`
> land and clear a seam on a live keeper pod) and **PQ-07** (oldest available Prometheus sample at
> run start, against the ~12 h retention horizon).

1. **Does a tier crash alone drive keeper recovery? (A1 — the phase's biggest unknown)**
   - Known: the orchestrator sends to `keeper-recovery` from `OrchestratorPrePipeline.cs:213` and `RelocateTail.cs:105`; a whole-tier processor crash is what `TEST-02` drives.
   - Unclear: `keeper_messages_consumed_total` was absent from the entire `__name__` index at Phase-87 time, and with ~12 h retention prior sweeps are outside the window.
   - Recommendation: **Wave-0 probe, first task of the phase.** Its answer decides whether the keeper scenarios need one seam or two, which changes the plan structure.

2. **Which of `?viewPanel=panel-<id>` vs the DOM walk is the reader's primary locator? (A2/A3)**
   - Recommendation: probe both in Wave 0 on one stat and one timeseries panel; lock the winner before writing the reader.

3. **Does panel 13 get proven or joined to the register? (A4)**
   - Recommendation: enumerate WebApi endpoints and probe for a safe 5xx. If none, record accepted-unproven with panel 7 rather than paying for a dependency outage — unless the plan explicitly budgets the Phase-86 readiness-latch restart.

4. **Should panel 5's empty-instead-of-spike behaviour be *fixed* or *recorded*?**
   - The locked constraint permits recomposing a panel expression "if a scenario exposes a genuinely wrong one". `sum by (processorId)(rate(orch_sent)) - (sum by (processorId)(rate(proc_consumed)) or 0 × …)` could keep the series alive. But 87-FINDINGS.md §10 establishes the `processorId` grouping is **correct as shipped**, and the empty-on-death behaviour is a *rendering* consequence, not a wiring error.
   - Recommendation: **record it in HAND-02, do not fix it.** A dashboards-hardening change belongs to a phase that owns the dashboard, and Phase 88's job is to discover and document, not to redesign. Flag for the user at planning.

5. **How many scenarios, and how are they waved?**
   - Suggested grouping (7 scenarios + 1 ladder): `webapi-traffic` (10,11,12,14, cheapest — run first), `webapi-5xx` (13, probe-gated), `processor-crash` (1? no — 3,4,5), `orchestrator-crash` (1), `keeper-crash` (9), `keeper-recovery` (2), `keeper-loss` (8, the only seam scenario), plus `duration-ladder` (DISC-07). Order matters: run the zero-risk HTTP scenarios first to shake out the Playwright reader before any destructive fault.

6. **Where does the runbook live?**
   - Recommendation: `.planning/phases/88-*/88-RUNBOOK.md` for authoring, and consider whether a copy belongs somewhere a maintenance department would actually find it — which is itself rubric question 6 and probably a HAND-02 blocking gap.

---

## Sources

### Primary (HIGH confidence)
- **Live cluster** (`kubectl -n skp get deploy`, 2026-07-28) — replica counts, images, workload inventory
- `raw.githubusercontent.com/grafana/grafana/**v12.3.9**/packages/grafana-e2e-selectors/src/selectors/components.ts` — the exact `data-testid` literals for the pinned Grafana version
- `.planning/ROADMAP.md` Phase 88 entry (lines 67-103) — goal, constraints, seam precondition table, rollout-discontinuity consequence, 9 success criteria
- `.planning/phases/87-grafana-observability-dashboards/87-FINDINGS.md` — §5 (no keeper reinject counter), §6 (Class A/B, the six guarded zeros), §10 (`identityName` on cross-service panels), §11 (`timeInterval` = export cadence, 60 s), §12 (the proof verified a shape no panel issues), §13 (panel 4 structural ratio), §14 (~12 h retention), §15 (Grafana 11.1.0 portability)
- `.planning/phases/87-grafana-observability-dashboards/87-VERIFICATION.md` — "Carried into Phase 88"
- `.planning/phases/87-grafana-observability-dashboards/87-05-SUMMARY.md` — the live-proof design and its deviations
- `k8s/dashboards/business.json` — all 14 panel definitions, legend configs, guards, thresholds, descriptions
- `scripts/phase-87-dashboards-verify.ps1` — the harness skeleton, exit-code table, proxy query helpers, `$__rate_interval` arithmetic
- `scripts/phase-80-harness.ps1:110-175, 360-420` — the scale/restore sequencer and the (stale) `$TierReplicas` map
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:31-52, 78, 175-186` — `orchestrator_step_unresolved` increment sites; graceful, never throws
- `analyzer-reports/phase-83-ha.json`, `analyzer-reports/phase-81-summary.json` — the two artifact precedents
- `k8s/31-orchestrator.yaml:41`, `k8s/32-keeper.yaml:35`, `k8s/33-processor-sample.yaml:41`, `k8s/30-baseapi-service.yaml:35` — declared replica counts
- `~/.claude/plugins/cache/playwright-skill/playwright-skill/4.1.0/skills/playwright-skill/SKILL.md` — invocation contract, `/tmp` script rule

### Secondary (MEDIUM confidence)
- [What's new in Grafana v11.3](https://grafana.com/docs/grafana/latest/whatsnew/whats-new-in-v11-3/) — Scenes GA and the `viewPanel=panel-<id>` / `-cloneN` URL change
- [Scenes-powered Dashboards is generally available](https://grafana.com/whats-new/2024-06-25-scenes-powered-dashboards-is-generally-available/) — corroborates the Scenes migration
- `.planning/STATE.md` Roadmap Evolution entry, 2026-07-28 — the seam findings and the open scoping question
- User memory: [[k8s-local-image-stale-tag-rebuild]], [[manual-graph-cleanup-ghost-cron-mg1]], [[stack-bringup-k8s-only]], [[long-sweep-detached-process]], [[hermetic-preexisting-failures]]

### Tertiary (LOW confidence — flagged for validation)
- The four Wave-0 probe assumptions A1-A4. None is asserted as fact; each has an explicit probe and a documented fallback.

---

## Metadata

**Confidence breakdown:**

| Area | Level | Reason |
|---|---|---|
| Grafana DOM selectors | **HIGH** | Read from the v12.3.9 source tag, not from docs or memory |
| Panels are already text-readable | **HIGH** | Read directly from `business.json`'s legend and stat configs |
| Restore table / replica counts | **HIGH** | `kubectl` against the live cluster this session |
| The stale `$TierReplicas` hazard | **HIGH** | Manifest, live cluster, and script read side by side |
| Seam mechanism + the absence of any existing path | **HIGH** | Roadmap precondition table + repo grep, independently corroborated |
| Statistical regimes and the banding rule | **MEDIUM-HIGH** | Derived from measured resolution (60 s) and the panels' own semantics; the ±10 % floor is a judgement call |
| Per-panel fault mapping | **MEDIUM** | 10 of 14 are high-confidence; panels 2, 13 need probes; panel 5's transition is predicted not measured; panel 7 is out by decision |
| Minimum detectable duration | **MEDIUM** | The theory is sound and derived from measured cadence; the numbers are predictions the step-ladder must replace |
| `viewPanel` URL format | **MEDIUM** | Grafana docs confirm the `panel-` prefix; not yet confirmed against this instance |

**Research date:** 2026-07-28
**Valid until:** 2026-08-27 for the Grafana/selector findings (stable, version-pinned). **~12 hours** for anything depending on Prometheus data currently in the TSDB — retention is ~12 h, so any measured baseline in this document is a historical observation, not a live band. The Wave-0 baseline capture must re-measure.
