# Phase 83: Orchestrator HA failover proof — Context

**Gathered:** 2026-07-18
**Status:** Ready for planning

<domain>
## Phase Boundary

A **live k8s proof of HA-07** on top of the leader-election mechanism built in Phase 82. Scale the orchestrator to N≥2 replicas on Docker-Desktop k8s, **kill the leader mid-run**, and prove the five HA-07 claims with an automated verdict:

1. **Exactly one leader** holds the Lease at any instant (no split-brain).
2. **Zero duplicate workflow triggers** across the failover — distinct per-fire correlationIds per scheduled cron tick.
3. **Election-gap ticks are skipped**, not duplicated and not backfilled.
4. **A single leader is re-established within a bounded time** (~LeaseDuration).
5. **`attributes.role` in ES logs shows the failover transition** (follower→leader on the survivor).

**No product-code change is expected** — Phase 82 shipped the mechanism (LeaderState, LeaderElectionService, WorkflowFireJob gate, role enricher, RBAC, replicas:3, RollingUpdate). Phase 83 is **harness + proof** work: a new leader-kill scenario driver plus a new fire-level analyzer/verdict.

**Out of scope (carried from the milestone):** fencing tokens (paused-leader duplicate tolerated by design, HA-04), cron backfill of gap-missed ticks (skip-on-gap chosen), option-C deterministic per-fire correlationId, multi-namespace/cross-cluster election, and any gate on sends other than `WorkflowFireJob` (`RelocateTail`/step-advancement stays ungated).

</domain>

<decisions>
## Implementation Decisions

### Harness shape
- **D-01:** Build a **new dedicated `scripts/phase-83-ha-failover.ps1`** — NOT an 8th scenario in `phase-81-sweep.ps1`. It **reuses the Phase-80 harness primitives** (STEP A0/A build+up, STEP B reset, STEP B1 orchestrator clean-window, STEP C seed, STEP D resolve-wfid, STEP E POST `/orchestration/start`) but adds a **leader-kill sequencer** and a **new HA-specific verdict**. Rationale: the fault class (kill the leader) and the invariant (per-tick fire-uniqueness) are fundamentally different from the 7 processor/keeper **execution-completeness** fault-recovery scenarios; folding it into the sweep would force the HA invariant through the wrong (Missing/Duplicates-of-executions) model. Mirror the Phase-80/81 exit-code discipline and the console-trace/`Write-Phase` conventions; reuse `scripts/lib/exit-code-resolution.ps1` for verdict→exit mapping.

### Kill mechanism & leader targeting
- **D-02:** **Force-kill the leader (SIGKILL):** `kubectl delete pod <leader> -n skp --grace-period=0 --force`. The leader never releases the Lease gracefully, so the surviving replica must **wait out `LeaseDuration` expiry (~15s)** before acquiring — this is the true test of "bounded recovery ~LeaseDuration" and the crash path HA-07 targets. (Graceful SIGTERM / lease-release is the fast rolling-deploy path and does NOT exercise the expiry bound — deliberately not the proof here.)
- **D-02a:** **Identify the leader deterministically** by reading the Lease holder before the kill: `kubectl get lease orchestrator-leader -n skp -o jsonpath='{.spec.holderIdentity}'`, then delete exactly that pod. Do not `kubectl scale` (k8s picks the victim — may not be the leader). Replicas stay at **3** (locked in Phase 82): killing the leader leaves 2 running survivors that contend immediately, plus a replacement pod that spins up.

### Zero-duplicate-trigger invariant (fire-level, NEW analyzer)
- **D-03:** Verify zero duplicate triggers with a **new fire-level per-tick uniqueness check** — the existing phase-68/81 analyzer is **execution-scoped** (keys on `(CorrelationId, ExecutionId)`, scores Missing/Duplicates of processor round-trips) and does **not** group fires by scheduled tick, so it cannot express this invariant. The new check:
  - Selects ES records representing an **actual entry-step SEND** (the leader's `Step_*` entry-step dispatch, carrying `attributes.CorrelationId` + `@timestamp`).
  - **Excludes follower-skip logs** — every replica (leader + both followers) mints a correlationId and logs each tick, but only the leader produces a send; the `"Follower — leader gate closed; skipping entry-step sends"` info records produced NO send and must not be counted.
  - **Buckets each send-record by its 30s wall-clock cron boundary** (`*/30 * * * * *` fires wall-clock-aligned at `:00`/`:30`).
  - **Asserts ≤ 1 distinct send-correlationId per bucket** across the whole window including the failover. A bucket with **0** send-correlationIds is a legitimate **election-gap skip**; a bucket with **≥ 2** is a duplicate trigger (FAIL).
- **D-03a (tick keying):** Key ticks by **wall-clock 30s bucketing of the send-record `@timestamp`** — NO product-code change (do not add a logged `ScheduledFireTimeUtc`). Gap-skip vs genuine miss is distinguished structurally: gap buckets sit **contiguously between** the last pre-kill leader fire and the first post-recovery leader fire and stay empty (no later backfill correlationId ever appears for a gap tick's fire-time).

### Proof gate & recovery bound
- **D-04:** The pass gate is a **fully automated dotnet-test verdict (exit 0/1/2)**, mirroring the Phase-81 exit-code model (0 = PASS, 1 = FAIL verdict/real finding never auto-retried, 2 = INCONCLUSIVE/observability-blind re-runnable; infra-abort codes for bring-up/reset/seed/kill failures). The single verdict test checks **all five HA-07 claims** against the captured window and the harness kills-then-waits before running it.
- **D-06 (recovery measurement + threshold):** Measure bounded recovery from **leader-kill wall-time → the first `attributes.role: follower→leader` transition on a surviving replica in ES**. **Pass if ≤ 2×LeaseDuration = 30s** (covers ~15s lease-expiry + acquire + hydration margin). This ES role-flip is the single source of truth — it also satisfies claim #5 (role transition visible), so #4 and #5 share one evidence stream.

### Claim → evidence mapping (for the verdict test)
- **D-07:** The five HA-07 claims map to evidence as:
  1. **Exactly-one-leader** — the Lease is single-holder by construction; operationally proven by the **D-03 per-tick uniqueness** (two simultaneous leaders ⇒ two send-correlationIds in one bucket). No separate split-brain probe needed beyond D-03.
  2. **Zero duplicate triggers** — D-03 (≤1 send-correlationId per 30s bucket across the failover).
  3. **Gap ticks skipped, not backfilled** — D-03a (contiguous empty buckets in the election window; no backfill correlationId for a gap tick).
  4. **Bounded recovery ≤ 30s** — D-06 ES role-flip delta.
  5. **Role transition visible** — the same D-06 `attributes.role` follower→leader record.

### Claude's Discretion
- Observation-window length and kill timing, grounded by: kill **only after** at least one clean leader fire is observed (a non-empty pre-kill bucket), and hold the window long enough to capture ≥1 clean pre-kill tick, ≥1 election-gap bucket, and ≥1 post-recovery leader tick (with 30s cron + 15s lease, a multi-minute window).
- Exact harness step numbering/labels and how the kill sequencer surfaces (inline vs helper), the ES query shape for the new fire-level check, the verdict report/summary JSON schema (mirror `analyzer-reports/phase-81-summary.json` shape), and the phase-83 close/roll-up script wiring.
- Whether the verdict lives as a new `~Analyzer`-style dotnet test class or a dedicated HA-verdict test (author's call — must exit 0/1/2 per D-04).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirement & milestone framing (read first)
- `.planning/REQUIREMENTS.md` — **HA-07** (the single requirement this phase closes) + the deferred/out-of-scope lists (fencing tokens, backfill, option-C).
- `.planning/ROADMAP.md` §"🚧 v10.0.0 Orchestrator High Availability" — the Phase 83 goal line + the milestone crux paragraph (why duplication can only arise at the un-gated scheduler fire; the locked election timings LeaseDuration 15s / RenewDeadline 10s / RetryPeriod 2s).
- `.planning/phases/82-orchestrator-ha-leader-election/82-CONTEXT.md` and `82-SPEC.md` — the Phase-82 mechanism decisions (D-01 POD_NAME identity, D-02 in-cluster-only election, D-04/D-05 fire gate, D-09 role enricher) this proof exercises live.

### The mechanism under test (Phase 82 source)
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — the fire path: per-fire correlationId mint (~55), the `IsLeader && startupGate.IsReady` gate snapshot (~77), the leader `foreach … DispatchAsync` send loop, and the follower-skip `"Follower — leader gate closed; skipping entry-step sends"` log (~104). **Confirms fires carry no logged scheduledFireTime and that followers mint+log but do not send — the basis for D-03/D-03a.**
- `src/Orchestrator/Election/LeaderState.cs`, `src/Orchestrator/Election/LeaderElectionService.cs` — volatile single-writer LeaderState + the `LeaderElector` over the `skp/orchestrator-leader` Lease (the object D-02a queries and D-06 observes flipping).
- `src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs` — stamps `attributes.role = leader|follower` on every log (the D-06/claim-5 evidence source).

### Existing harness to reuse (D-01)
- `scripts/phase-80-harness.ps1` — the single-scenario k8s harness whose STEP A0/A/B/B1/C/D/E/H/Z primitives Phase 83 reuses; the exit-code table to mirror.
- `scripts/phase-81-sweep.ps1` — the roll-up/exit-code discipline and `Write-Phase` conventions to mirror (NOT extended).
- `scripts/phase-80-build.ps1`, `scripts/phase-80-up.ps1`, `scripts/phase-80-reset.ps1` — build/bring-up/reset primitives invoked by the harness.
- `scripts/lib/exit-code-resolution.ps1` — shared verdict→exit-code resolution lib.

### The workload & the existing analyzer (contrast for D-03)
- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` — seeds the `v8-fanout-proof` workflow; **cron `*/30 * * * * *`** (`FanOutCron`, line 58) — the 30s tick that D-03a buckets against.
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — the existing **execution-scoped** analyzer (keys on `(CorrelationId, ExecutionId)`, Missing/Duplicates of round-trips). Read to understand what the NEW fire-level check must do *differently*, and to mirror its ES-query/report-writer plumbing and `~Analyzer` env-seam pattern.
- `analyzer-reports/phase-81-summary.json` — roll-up summary JSON shape to mirror for the phase-83 verdict report.

### k8s manifests (already at target state from Phase 82)
- `k8s/31-orchestrator.yaml` — `replicas: 3`, RollingUpdate, `POD_NAME` downward env, SA ref (the Deployment the proof scales/kills against).
- `k8s/34-orchestrator-rbac.yaml` — SA/Role/RoleBinding granting `get/create/update` on `leases` in `skp` (the RBAC that lets election run live).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`phase-80-harness.ps1` STEP primitives** — build/up/reset/orchestrator-restart/seed/resolve-wfid/POST-start are reused verbatim; only a leader-kill sequencer + HA verdict are new (D-01).
- **`scripts/lib/exit-code-resolution.ps1`** — verdict→exit-code mapping reused so the phase-83 script classifies 0/1/2 identically (D-04).
- **`AnalyzerE2ETests.cs` ES-query + report-writer plumbing** — the fetch/parse/write-JSON scaffold to clone for the new fire-level verdict (the query body and scoring differ; the plumbing does not).
- **`OrchestratorRoleLogEnricher` + `attributes.role`** — already emits the follower/leader evidence D-06/claim-5 read; no new logging needed.

### Established Patterns
- **Phase-81 exit-code discipline** (0 PASS / 1 real-FAIL-never-retried / 2 INCONCLUSIVE-re-runnable / infra-abort codes) — the phase-83 harness follows the same contract.
- **ids-in-scope-not-template (T-18-04)** — the leader fire's correlationId lives in the ES log **scope** (`attributes.CorrelationId`), which is exactly what the D-03 bucketing queries.
- **Detached long-run + short watcher** ([[long-sweep-detached-process]]) — if the proof window runs long, drive it detached via `Start-Process` with a separate watcher loop; clear stale analyzer reports first.

### Integration Points
- **Lease object `skp/orchestrator-leader`** — the D-02a leader-identity read (`jsonpath .spec.holderIdentity`) and the k8s primitive backing claim #1.
- **ES `Step_*` entry-step dispatch records** — the send-evidence stream D-03 buckets; produced only by the leader.
- **ES `attributes.role` stream** — the failover-transition evidence for D-06/claim-5.

</code_context>

<specifics>
## Specific Ideas

- The five HA-07 claims are proven from **two ES evidence streams** (entry-step SEND records + `attributes.role` records) plus **one kubectl read** (Lease holderIdentity to target the kill). No Prometheus deltas, no execution-completeness Missing/Duplicates scoring — those belong to the processor/keeper sweep, not this HA proof.
- The whole proof is a **single failover run** (one leader force-kill), not a sweep — the invariant is checked across one continuous observation window spanning pre-kill, election-gap, and post-recovery.

</specifics>

<deferred>
## Deferred Ideas

- **Graceful-delete (SIGTERM) failover variant** — would prove the fast lease-release/rolling-deploy path; explicitly not the HA-07 bound and deferred (D-02 chose force-kill as the real proof). Could be a future add if the rolling-deploy path needs its own proof.
- **Lease-acquireTime cross-check** — a second recovery-time evidence source (`kubectl` Lease `.spec.acquireTime` delta) alongside the ES role-flip; deferred, D-06 uses the ES role-flip as the single source of truth.
- **Logged `ScheduledFireTimeUtc`** — a small product-code add for skew-proof tick keying; deferred in favor of wall-clock bucketing (D-03a) to keep the phase product-change-free.

</deferred>

---

*Phase: 83-orchestrator-ha-failover-proof*
*Context gathered: 2026-07-18*
