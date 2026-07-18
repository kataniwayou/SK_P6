# Phase 83: Orchestrator HA failover proof — Research

**Researched:** 2026-07-18
**Domain:** Live k8s HA-failover proof — leader-kill sequencer (PowerShell/kubectl) + a new fire-level ES-analyzer verdict (dotnet test) proving HA-07's five claims
**Confidence:** HIGH (all mechanics verified against in-repo source + the live-verified ES/analyzer plumbing; one MEDIUM item — the KubernetesClient acquire-timing bound — verified against the upstream `LeaderElector.cs` source)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01 (harness shape):** Build a NEW dedicated `scripts/phase-83-ha-failover.ps1` — NOT an 8th scenario in `phase-81-sweep.ps1`. Reuse the Phase-80 harness primitives (STEP A0/A build+up, STEP B reset, STEP B1 orchestrator clean-window, STEP C seed, STEP D resolve-wfid, STEP E POST `/orchestration/start`); ADD a leader-kill sequencer + a NEW HA-specific verdict. Mirror the Phase-80/81 exit-code discipline + `Write-Phase` conventions; reuse `scripts/lib/exit-code-resolution.ps1`.
- **D-02 (kill mechanism):** Force-kill the leader (SIGKILL) via `kubectl delete pod <leader> -n skp --grace-period=0 --force`. The leader never releases the Lease gracefully; the survivor waits out `LeaseDuration` (~15s) expiry. Graceful SIGTERM/lease-release is deliberately NOT the proof.
- **D-02a (leader targeting):** Identify the leader deterministically by reading the Lease holder BEFORE the kill: `kubectl get lease orchestrator-leader -n skp -o jsonpath='{.spec.holderIdentity}'`, then delete exactly that pod. NEVER `kubectl scale` (k8s picks the victim). Replicas stay at 3.
- **D-03 (zero-duplicate invariant, NEW analyzer):** Select ES records for an actual entry-step SEND (leader's `Step_*` dispatch carrying `attributes.CorrelationId` + `@timestamp`); EXCLUDE follower-skip logs; bucket each send-record by its 30s wall-clock cron boundary (`*/30 * * * * *`); assert ≤1 distinct send-correlationId per bucket. 0 = election-gap skip; ≥2 = duplicate trigger (FAIL).
- **D-03a (tick keying):** Key ticks by wall-clock 30s bucketing of the send-record `@timestamp` — NO product-code change (no logged `ScheduledFireTimeUtc`). Gap-skip is distinguished structurally: gap buckets sit contiguously between the last pre-kill leader fire and the first post-recovery leader fire and stay empty (no later backfill correlationId).
- **D-04 (proof gate):** Fully automated dotnet-test verdict (exit 0/1/2), mirroring the Phase-81 exit-code model. A single verdict test checks all five HA-07 claims; the harness kills-then-waits before running it.
- **D-06 (recovery measurement + threshold):** Measure recovery from leader-kill wall-time → first `attributes.role: follower→leader` transition on a surviving replica in ES. PASS if ≤ 2×LeaseDuration = 30s. This ES role-flip is the single source of truth and also satisfies claim #5.
- **D-07 (claim→evidence mapping):** (1) exactly-one-leader ⇐ D-03 per-tick uniqueness; (2) zero-duplicate ⇐ D-03; (3) gap skipped, not backfilled ⇐ D-03a; (4) bounded recovery ≤30s ⇐ D-06; (5) role transition visible ⇐ D-06 role record.

### Claude's Discretion
- Observation-window length and kill timing (grounded: kill only after ≥1 clean leader fire; hold long enough to capture ≥1 clean pre-kill tick, ≥1 election-gap bucket, ≥1 post-recovery leader tick).
- Exact harness step numbering/labels; how the kill sequencer surfaces (inline vs helper); the ES query shape for the fire-level check; the verdict report/summary JSON schema (mirror `analyzer-reports/phase-81-summary.json`); the phase-83 close/roll-up wiring.
- Whether the verdict lives as a new `~Analyzer`-style dotnet test class or a dedicated HA-verdict test (must exit 0/1/2 per D-04).

### Deferred Ideas (OUT OF SCOPE)
- Graceful-delete (SIGTERM) failover variant.
- Lease-`acquireTime` cross-check as a second recovery-time source (D-06 uses ES role-flip only).
- Logged `ScheduledFireTimeUtc` product-code add (D-03a uses wall-clock bucketing).
- Fencing tokens, cron backfill/catch-up, option-C deterministic correlationId, multi-namespace/cross-cluster election. Gating any send other than `WorkflowFireJob`.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| HA-07 | Under N≥2 replicas with the leader killed mid-run, the system produces zero duplicate workflow triggers (distinct per-fire correlationIds through the analyzer) and re-establishes a single leader within a bounded time. | The whole document. The five HA-07 sub-claims map to: (1)+(2) the D-03 30s-bucket uniqueness over `exists attributes.StepId` correlationIds (Code Example 3); (3) the contiguous-empty-bucket structural check (Code Example 4); (4)+(5) the earliest `attributes.role=leader` record after `KILL_UTC` (Code Example 5). Recovery bound ≤30s justified by the verified LeaderElector acquire timing (Standard Stack → Election Timing). Kill-timing must be phase-aligned to guarantee a gap tick — see Common Pitfall 1 / Validation Architecture. |
</phase_requirements>

## Summary

Phase 83 is a **live-proof / harness** phase, not a product-code phase. Phase 82 already shipped the mechanism (`LeaderState`, `LeaderElectionService` over the `skp/orchestrator-leader` Lease, the `WorkflowFireJob` `IsLeader && IsReady` fire gate, the `OrchestratorRoleLogEnricher` stamping `attributes.role`, RBAC, `replicas: 3`). The work is (a) a new `scripts/phase-83-ha-failover.ps1` that reuses the Phase-80 STEP primitives verbatim and inserts a **leader-kill sequencer**, and (b) a new **fire-level dotnet-test verdict** (mirroring `AnalyzerE2ETests`' ES-query/report-writer/env-seam plumbing) that proves the five HA-07 claims from **two ES streams + one kubectl read**.

The single most important mechanical finding: **the leader's entry-step send emits NO orchestrator-side log.** `StepDispatcher.DispatchAsync` (the leader's `foreach` send target) has no logger — it only `Send`s the `EntryStepDispatch` and bumps a metric. The **only** ES evidence that the leader actually fired for a given tick is the **downstream processor `Step_*` hop-executed record** carrying `attributes.StepId` + `attributes.CorrelationId`. Because `WorkflowFireJob` mints exactly **one correlationId per fire, shared across the whole DAG**, and **followers produce no `StepId` records** (they only log the follower-skip line, which carries no `StepId`), the D-03 check reduces cleanly to: *distinct correlationId among `exists attributes.StepId` records = distinct real leader fire.* Filtering on `exists attributes.StepId` **automatically excludes** the follower-skip logs — no `role` field is needed on the send stream. This is exactly the set the existing `AnalyzerE2ETests.BuildStepSearchBody` already fetches; the new verdict re-buckets it by `min(@timestamp)` per correlationId into 30s wall-clock windows.

The one real tension with the locked decisions is the **Nyquist gap-coverage problem** (flagged in the brief, point 6): the election gap is ~11–17s but the cron period is 30s, so a **randomly-timed** kill has only a ~40–57% chance of placing a scheduled tick inside the gap — meaning the "gap ticks skipped" claim (#3) would be **unobservable on most runs**. The clean, product-change-free fix is to **phase-align the kill**: issue the force-delete a few seconds *before* a wall-clock cron boundary (`:00`/`:30`), so the boundary tick reliably lands in the guaranteed-minimum ~11s gap. This keeps the locked `*/30` cron and the 30s bucketing intact. See Common Pitfall 1.

**Primary recommendation:** Reuse the Phase-80 STEP A0/A/B/B1/C/D/E chain verbatim; add a STEP F "leader-kill sequencer" that (1) reads the Lease holder, (2) waits for ≥1 clean pre-kill fire, (3) sleeps until `nextCronBoundary − 3s`, (4) force-deletes the leader pod and pins `KILL_UTC`, (5) holds ~120s post-kill, then STEP H runs a new `HaFailoverAnalyzerE2ETests` fact that scores the five claims from the `StepId` correlationId buckets + the `role=leader` post-kill record and writes a `phase-83`-shaped report whose `Verdict` maps through `Resolve-AnalyzerExitCode` to 0/1/2.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Elect/hold the single leader | Orchestrator replica (in-cluster election `BackgroundService`) | k8s API (`coordination.k8s.io/v1` Lease) | Mechanism shipped in Phase 82; this proof only observes it. The Lease is the k8s-owned single-writer arbiter. |
| Fire the entry-step send (the gated act) | Orchestrator **leader** only (`WorkflowFireJob` → `IStepDispatcher`) | — | HA-01: exactly one replica sends; followers run everything else ungated. |
| Produce the send-evidence record | **Processor** (`Step_*` hop-executed, `attributes.StepId`) | Orchestrator (fan-out `NextStepId` records, downstream) | The dispatcher emits no log; the processor's execution of the entry step is the binding proof the send happened. |
| Produce the role/failover-transition evidence | Orchestrator (every log, `OrchestratorRoleLogEnricher` → `attributes.role`) | — | HA-05: role stamped live on every record; the acquisition log is `role=leader`. |
| Target + kill the leader | Harness (kubectl) | k8s API + kubelet (SIGKILL) | D-02/D-02a: operator-plane action, no product code. |
| Score the five claims / emit verdict | Test verdict tier (dotnet test `HaFailover…`) reading ES over localhost:9200 | Harness (env seam, exit-code resolution) | D-04: automated 0/1/2 verdict, ES-read-only, mirrors the Phase-66/81 analyzer. |

## Standard Stack

This is an infra-proof phase; the "stack" is the operator toolchain + the existing test/observability plumbing, all already present and version-pinned in the repo. No new packages.

### Core (tools & APIs the phase drives)
| Tool / API | Version (verified in repo) | Purpose | Why standard |
|------------|---------------------------|---------|--------------|
| `kubectl` (Docker-Desktop k8s) | env-provided | Read the Lease holder; force-delete the leader pod; rollout ops | The Phase-80/81 harnesses already drive all k8s ops through `kubectl -n skp` [VERIFIED: scripts/phase-80-harness.ps1]. |
| PowerShell 7 (`pwsh -File`) | env-provided | Harness sequencer + exit-code discipline | Every phase-6x/8x harness is `pwsh` with `$ErrorActionPreference='Stop'` + `Write-Phase` [VERIFIED: scripts/phase-80-harness.ps1:93-105]. |
| `dotnet test` (MTP / xunit.v3) | net`BaseApi.Tests` | Runs the verdict fact; exit code IS the verdict | MTP-native filter `-- --filter-method "*…*"` (VSTest `--filter` is silently ignored) [VERIFIED: phase-80-harness.ps1:62-65,485]. |
| Elasticsearch `_search` (localhost:9200) | ES 8.15.5, data stream `logs-generic.otel-default` | The two evidence streams (send records, role records) | `ElasticsearchTestClient` + `EsIndexNames` are the Wave-0-verified plumbing [VERIFIED: tests/.../Helpers/EsIndexNames.cs, ElasticsearchTestClient.cs]. |
| KubernetesClient `LeaderElector` | (Phase-82 dependency) | The mechanism under test | Fixed timings `LeaseDuration=15s / RenewDeadline=10s / RetryPeriod=2s` [VERIFIED: src/Orchestrator/Election/LeaderElectionService.cs:34-40]. |

### Supporting (in-repo assets reused verbatim / cloned)
| Asset | Purpose | When to use |
|-------|---------|-------------|
| `scripts/phase-80-harness.ps1` STEP A0/A/A2/B/B1/C/D/E | build+up, fresh-DB bootstrap, reset, clean orchestrator, seed, wfid, POST /start | Reuse verbatim as the phase-83 bring-up chain (D-01). |
| `scripts/lib/exit-code-resolution.ps1` | `Resolve-AnalyzerExitCode` (Verdict→0/1/2), `Resolve-SweepClass` | Reuse so phase-83 classifies identically (D-04). |
| `AnalyzerE2ETests.cs` — `BuildStepSearchBody`, `SearchAllHits`, `PollHitsToStableAsync`, `TryReadTimestamp`, write-then-assert, `TryParseUtc` env seam | ES fetch/parse/window/report scaffold | Clone into `HaFailoverAnalyzerE2ETests`; keep the plumbing, replace the scoring with 30s-bucket uniqueness. |
| `FanOutSeederE2ETests` (`v8-fanout-proof`, cron `*/30 * * * * *`) | The workload that fires the ticks the proof buckets | Reuse verbatim via STEP C (D-01). |
| `OrchestratorRoleLogEnricher` (`attributes.role`) | Role-transition evidence | Read-only in the verdict; no change. |

### Election timing (the recovery-bound justification — MEDIUM→HIGH)
The `LeaseDuration=15s / RenewDeadline=10s / RetryPeriod=2s` config feeds `LeaderElector.RunAndTryToHoldLeadershipForeverAsync`. From the upstream `LeaderElector.cs`, a non-holder acquires when `observedTime + LeaseDurationSeconds < now` — where `observedTime` is the **local time the follower last saw the lease record change**, not the lease's stored `renewTime` [CITED: github.com/kubernetes-client/csharp `LeaderElection/LeaderElector.cs`]. A healthy leader renews every ~`RetryPeriod` (2s), so at kill time a survivor's `observedTime` is ≈ kill − (0…~4s). It then acquires at `observedTime + 15s`, polling every 2s ⇒ **gap ≈ kill + [11, 17]s**. This is why "recovery ≈ LeaseDuration (~15s)" is accurate and why the **≤30s (2×LeaseDuration) threshold has comfortable margin** [ASSUMED for the exact 11–17s bracket — the reasoning is sound and matches client-go semantics, but was not measured live in this session]. Because the gap (≤17s) is **less than one cron period (30s)**, at most ONE cron boundary can fall inside it ⇒ at most one skipped tick ⇒ exactly one empty bucket. A surviving, already-hydrated replica wins first, so the D-05 `IsReady` hydration term adds ≈0 to the survivor's first-fire latency (only a cold replacement pod would pay hydration cost, and it loses the race).

## Architecture Patterns

### System flow (the proof, input → verdict)

```
                        cron */30 (wall-clock aligned :00/:30)
                                     │  each replica has its own Quartz (L1-hydrated per POD_NAME)
                                     ▼
   ┌────────────── orchestrator replicas (x3) ──────────────┐
   │  leader (holds Lease)      follower        follower     │
   │      │ IsLeader&&IsReady       │ gate closed    │       │
   │      ▼                         ▼                ▼       │
   │  DispatchAsync (Send)     "Follower — leader gate closed; skipping…"
   │   (NO log emitted)          role=follower log (no StepId)         │
   └──────┼──────────────────────────────────────────────────────────┘
          │ EntryStepDispatch{CorrelationId(per-fire), StepId=A}
          ▼
     processor-sample ── executes hop ──▶ ES: attributes.StepId + attributes.CorrelationId + @timestamp
                                                   │  (THE send-evidence stream — one corrId per fire)
   ┌───────────────────── HARNESS (phase-83-ha-failover.ps1) ─────────────────────┐
   │ read Lease holderIdentity ─┐                                                  │
   │ wait ≥1 clean fire         │  kubectl delete pod <holder> --grace-period=0    │
   │ sleep → nextBoundary−3s ───┴─▶ --force   ⇒ pin KILL_UTC                       │
   │        (phase-align: guarantees a tick lands in the ~11-17s gap)              │
   │ hold ~120s ─────────────────────────────────────────────────────────────────┤
   └──────────────────────────────────────┬──────────────────────────────────────┘
                                           ▼
   STEP H: dotnet test HaFailoverAnalyzer (env seam WINDOW_*_UTC, KILL_UTC, report id)
        ├─ stream 1: exists attributes.StepId  → group by corrId, min(@timestamp) → 30s buckets
        │       claim1/2: assert ≤1 corrId per bucket ; claim3: contiguous empty gap bucket, no backfill
        └─ stream 2: term attributes.role=leader AND @timestamp>KILL_UTC → earliest
                claim4: (earliest − KILL_UTC) ≤ 30s ; claim5: record exists
        → write analyzer-reports/phase-83-*.json {Verdict}; assert Verdict==Pass  (exit 0/1/2)
```

### Pattern 1: Send-evidence = downstream `StepId` records, not an orchestrator send log
**What:** Identify "the leader actually fired tick T" by the presence of processor `attributes.StepId` records sharing a per-fire `attributes.CorrelationId`, NOT by any orchestrator log.
**When to use:** The entire D-03 uniqueness check.
**Why:** `StepDispatcher.DispatchAsync` emits no log [VERIFIED: src/Orchestrator/Dispatch/StepDispatcher.cs — no `ILogger` injected]. The leader branch of `WorkflowFireJob` emits no per-send log either [VERIFIED: WorkflowFireJob.cs:77-98]. One correlationId is minted per fire and shared across the DAG [VERIFIED: WorkflowFireJob.cs:58]. Followers emit only the skip log (no `StepId`) [VERIFIED: WorkflowFireJob.cs:106]. So `exists attributes.StepId` is a clean leader-only filter.

### Pattern 2: Reuse the analyzer's ES/window/report scaffold, swap only the scoring
**What:** Clone `AnalyzerE2ETests`' `SearchAllHits` + `PollHitsToStableAsync` + `TryReadTimestamp` + `TryParseUtc` env seam + write-then-assert; replace the `PassFailEngine` (execution-scoped Missing/Duplicates) with the 30s-bucket uniqueness scorer.
**When to use:** The new `HaFailoverAnalyzerE2ETests`.
**Why:** The existing engine keys on `(CorrelationId, ExecutionId)` and scores round-trip completeness — it cannot express per-tick fire uniqueness [VERIFIED: CONTEXT D-03; AnalyzerE2ETests structure]. But the ES fetch/window/drain plumbing is identical and live-verified. Keep the pure scorer in the shared `Observability.Analysis` namespace so it can be exercised hermetically (mirrors `StructuralCohort` — synthetic hits, no Docker), matching how the repo proves analyzer logic hermetically.

### Pattern 3: Phase-aligned perturbation (kill just before a cron boundary)
**What:** Compute the next `:00`/`:30` UTC boundary and force-delete the leader ~3s before it.
**When to use:** The STEP F kill sequencer.
**Why:** Deterministically forces the boundary tick into the guaranteed-minimum ~11s election gap so claim #3 (gap-skip) is reliably exercised without changing the locked `*/30` cron. See Common Pitfall 1.

### Recommended harness structure (discretionary labels)
```
phase-83-ha-failover.ps1
  STEP A0/A   phase-80-build.ps1 + phase-80-up.ps1        # reuse (skippable via -SkipBringUp)
  STEP A2     fresh-DB processor-row bootstrap             # reuse verbatim
  STEP B      phase-80-reset.ps1                           # reuse
  STEP B1     kubectl rollout restart deployment/orchestrator + rollout status   # reuse — clean Quartz on all 3 pods, fresh election
  STEP C      dotnet test ~FanOutSeeder                    # reuse — seed v8-fanout-proof (*/30)
  STEP D      kubectl exec psql → wfId                     # reuse
  STEP E      POST /orchestration/start (require 204)      # reuse
  STEP F      LEADER-KILL SEQUENCER (new):
     F.0  read leader = kubectl get lease orchestrator-leader -n skp -o jsonpath='{.spec.holderIdentity}'
     F.1  pin WINDOW_START_UTC; observe ES until ≥1 clean pre-kill fire bucket (≥1 corrId with StepId)
     F.2  sleep until nextCronBoundary − 3s
     F.3  kubectl delete pod <leader> -n skp --grace-period=0 --force ; pin KILL_UTC
     F.4  (optional) poll the Lease until holderIdentity changes to a survivor (cross-check only)
     F.5  hold ~120s (capture gap bucket + role flip + ≥1 post-recovery fire) ; pin WINDOW_END_UTC
  STEP H      dotnet test ~HaFailoverAnalyzer with env seam ; Resolve-AnalyzerExitCode → 0/1/2
  STEP Z      stop port-forwards (keep stack + PVCs)       # reuse
```
STEP B0 (processor counter-baseline restart) from phase-80 is **not needed** (no Prom/MG-1 in this proof) — omit or keep harmlessly.

### Anti-Patterns to Avoid
- **Using an orchestrator "leader fired" log as the send oracle:** none exists — the dispatcher is silent. Use `StepId` records.
- **`kubectl scale` to remove the leader:** k8s chooses the victim; may not hit the leader (D-02a).
- **Graceful `kubectl delete` (default grace period):** lets the LeaderElector release the lease → fast path, does NOT exercise the ~15s expiry bound (D-02).
- **Random kill timing:** ~40–57% of runs won't place a tick in the gap → claim #3 silently unproven (Pitfall 1).
- **Counting the follower-skip log as a fire:** it carries a correlationId but no send. `exists attributes.StepId` excludes it; never bucket the `role=follower` skip records.
- **Bucketing on index/export time:** bucket on `@timestamp` (emission/observed time), not on when ES indexed it (~60s OTLP export skew is uniform and irrelevant to `@timestamp`).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Poll ES `_search`, tolerate lazy 404s, clone hits | A bespoke HttpClient loop | `ElasticsearchTestClient.SearchAllHits` + `PollHitsToStableAsync` | Live-verified backoff + 404 tolerance + Clone discipline [VERIFIED: ElasticsearchTestClient.cs]. |
| Parse the live `@timestamp` (epoch-ms numeric string, not ISO) | `DateTimeOffset.Parse` on `@timestamp` | `TryReadTimestamp` (epoch-ms-first, ISO fallback) | The OTLP→ES bridge writes `@timestamp` as a numeric string in `_source`; naive `TryParse` returns empty (the phase-73 bug) [VERIFIED: AnalyzerE2ETests.cs:627-663]. |
| ES field paths / index name | Hardcoded strings | `EsIndexNames` consts (no `.keyword` sub-field) | `.keyword` returns ZERO hits on the ECS-managed data stream — the trap that broke 4 facts at Phase-11 UAT [VERIFIED: EsIndexNames.cs:52-70]. |
| Verdict→exit-code mapping | Inline `switch` in the harness | `Resolve-AnalyzerExitCode` / `Resolve-SweepClass` | Fail-closed unknown→1; Inconclusive→2; hermetically provable [VERIFIED: exit-code-resolution.ps1]. |
| MTP test filter | `dotnet test --filter …` | `dotnet test … -- --filter-method "*…*"` | MTP silently ignores VSTest `--filter` [VERIFIED: phase-80-harness.ps1:62-65]. |
| ES range window bound | epoch-ms math in the query | ISO `{utc:o}` range on the indexed `@timestamp` `date` field | The indexed field is a `date` (ISO-queryable) even though `_source` stores epoch-ms [VERIFIED: EsIndexNames.cs:186-217 + BuildStepSearchBody works live]. |

**Key insight:** Every piece of ES/exit-code plumbing the phase needs already exists, live-verified, in the repo. The ONLY genuinely new code is (a) the ~40-line PowerShell kill sequencer and (b) the ~80-line pure 30s-bucket scorer + its thin ES-fetch fact. Cloning beats re-inventing here because the `@timestamp` and index-name traps are non-obvious and already solved.

## Runtime State Inventory

This is a live-proof phase (product code unchanged), but it DOES manipulate live cluster runtime state, so the inventory is relevant to harness correctness:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (k8s API) | The `coordination.k8s.io/v1` Lease `skp/orchestrator-leader` — holds `holderIdentity` (= a POD_NAME), `renewTime`, `acquireTime` [VERIFIED: LeaderElectionService.cs:43-59]. | Harness READS it (D-02a) to target the kill; force-delete leaves a **stale** holderIdentity until a survivor overwrites it (expected — that staleness IS the ~15s expiry the proof measures). No cleanup: the Lease self-heals on next acquire. |
| Live service config | Quartz cron triggers live **in each replica's in-process RAMJobStore** (not durable), hydrated per-replica from the L2 parent index on boot [VERIFIED: phase-80-harness.ps1:52-60 clean-window note; WorkflowFireJob reschedule]. | STEP B1 rollout-restart wipes ghost crons on all 3 pods before seed+start — reuse verbatim. The killed leader's replacement pod re-hydrates + schedules from L2 (no manual action). |
| OS-registered state | None — no Task Scheduler / systemd / pm2 involvement. | None (verified — this is pure k8s + dotnet test). |
| Secrets/env vars | New **test-only** env seam for the verdict: `WINDOW_START_UTC`, `WINDOW_END_UTC`, `KILL_UTC`, and a report/scenario id — mirroring the D-16 `SCENARIO_ID`/`WINDOW_*_UTC` seam [VERIFIED: phase-80-harness.ps1:477-490]. No product secrets touched. | Harness sets + clears them in a `finally` (mirror the analyzer step). |
| Build artifacts | `orchestrator:local` image must carry the Phase-82 mechanism (already merged) [VERIFIED: 82 commits in git log]. | STEP A0 `phase-80-build.ps1` rebuilds `:local`; rollout-restart forces fresh bits — reuse verbatim (SourceHash currency). |

**Nothing found** requiring product-code data migration — the Lease is the only live-state object and it self-heals.

## Common Pitfalls

### Pitfall 1: 30s cron + 15s gap → a tick may not land in the election gap (Nyquist gap-coverage)
**What goes wrong:** The gap-skip claim (#3) is only exercised if a scheduled tick falls inside the ~11–17s election gap. With cron period 30s and a randomly-timed kill, the boundary tick often lands *after* the gap has closed (leader already re-established) — so **no empty bucket appears** and claim #3 is unproven despite a green run on claims #1/#2/#4/#5.
**Why it happens:** gap (≤17s) < cron period (30s) ⇒ P(a tick in the gap) ≈ gap/period ≈ 40–57%, depending on kill phase.
**How to avoid (RECOMMENDED, product-change-free):** **Phase-align the kill** — compute the next wall-clock `:00`/`:30` boundary and force-delete the leader ~3s before it. The boundary tick then lands ~3s into the guaranteed-minimum ~11s gap → deterministic single skipped tick → exactly one empty bucket contiguously between the last pre-kill fire and the first post-recovery fire.
**Alternatives (worse):** (a) faster cron (`*/5`/`*/10`) during the proof — oversamples the gap but requires re-seeding with a non-locked cron and breaks the D-03a 30s bucket; rejected. (b) Accept gap-skip as best-effort-observed — fragile for an automated verdict; rejected. **Surface this to the planner:** the kill-timing is load-bearing for claim #3 and must be a deterministic pre-boundary kill, not a fixed "kill after N fires" like phase-80's crash sequencer.
**Warning signs:** A run where every bucket has exactly 1 corrId and there is NO empty bucket between kill and recovery — that's a coverage miss, not a pass. The verdict should mark this **INCONCLUSIVE** (claim #3 unobserved), not PASS.

### Pitfall 2: `@timestamp` bucket-edge skew near a boundary
**What goes wrong:** A fire at `:29.8` whose processor `Step_A` record lands at `:30.1` buckets into the *next* 30s window (fire-time is inferred from the downstream record's `@timestamp`, which trails the send by processing latency).
**Why it happens:** D-03a keys on the send-record `@timestamp`, and there is no logged `ScheduledFireTimeUtc` (deferred). Processing latency (sub-second to a few seconds) can cross a 30s boundary for a fire that itself occurred near the boundary.
**How to avoid:** Use `min(@timestamp)` across a correlationId's `StepId` records (the entry step `Step_A` is earliest ⇒ closest to the true fire). Combined with the Pitfall-1 phase-aligned kill (ticks fire *on* the boundary, processing pushes them a little past it — consistently into the correct post-boundary bucket), edge cases are rare. If a boundary-straddling ambiguity is detected (a bucket with a corrId whose min-timestamp is within ~2s of an edge), prefer marking INCONCLUSIVE over a false FAIL.
**Warning signs:** Two corrIds in one bucket where one's min-timestamp is within ~1–2s of the bucket's lower edge — likely a latency-straddle, not a real duplicate.

### Pitfall 3: Host↔pod clock skew in the recovery delta
**What goes wrong:** `KILL_UTC` is the harness host wall-clock at `kubectl delete` return; the role-flip `@timestamp` is the pod clock. A skew inflates/deflates the recovery delta.
**Why it happens:** Two clocks.
**How to avoid:** On Docker-Desktop single-node k8s the pods and host share the VM clock, so skew ≈ 0 — acceptable [ASSUMED: single-node Docker-Desktop shared clock]. Keep the ≤30s threshold (2×LeaseDuration) which absorbs several seconds of skew. Do NOT tighten the threshold below 2×LeaseDuration.
**Warning signs:** A negative recovery delta (role-flip `@timestamp` < `KILL_UTC`) indicates skew or that a pre-kill `role=leader` record leaked past the range filter — fail-closed to INCONCLUSIVE and investigate.

### Pitfall 4: Scoring before the window drains / before the role flip is exported
**What goes wrong:** Scoring immediately after the hold scores an incomplete window (post-recovery fire or the role-flip record not yet exported to ES, ~60s OTLP skew).
**How to avoid:** Reuse the analyzer's `DrainMs` (60s) + `PollHitsToStableAsync` before scoring [VERIFIED: AnalyzerE2ETests.cs:54-59,168,355]. Ensure the hold + drain covers `KILL_UTC + gap(≤17s) + one cron period(30s) + export(~60s)` — a ~120s hold plus the 60s drain is sufficient.

### Pitfall 5: The replacement pod, not a survivor, wins the election
**What goes wrong:** In theory the k8s-spawned replacement pod could win and inflate recovery time (it must pass a 30s readiness probe + hydrate).
**How to avoid:** The two surviving followers are already Ready + hydrated and poll every 2s, so a survivor acquires at ~kill+[11,17]s — long before the replacement is Ready (`initialDelaySeconds: 30` [VERIFIED: k8s/31-orchestrator.yaml:97]). The first `role=leader` after `KILL_UTC` is therefore a survivor. If it ever isn't, the delta simply grows; the ≤30s bound still guards correctness.

## Code Examples

### Example 1 — Read the leader + force-kill (harness, STEP F.0/F.3)
```powershell
# F.0 — deterministic leader identity (holderIdentity == POD_NAME, D-02a)
$leader = (kubectl get lease orchestrator-leader -n skp -o jsonpath='{.spec.holderIdentity}').Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($leader)) {
    Write-Phase "could not read Lease holderIdentity. Aborting." 'Red'; exit 45   # new infra-abort code
}
Write-Phase "current leader = $leader"

# F.2 — phase-align: sleep until ~3s before the next :00/:30 boundary (Pitfall 1)
$now = [DateTimeOffset]::UtcNow
$secIntoHalfMin = ($now.Second % 30) + $now.Millisecond/1000.0
$sleepS = (30 - $secIntoHalfMin) - 3.0
if ($sleepS -lt 0) { $sleepS += 30 }
Start-Sleep -Seconds $sleepS

# F.3 — SIGKILL the leader; NO graceful lease release (D-02)
kubectl delete pod $leader -n skp --grace-period=0 --force | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Phase "force-delete of leader failed. Aborting." 'Red'; exit 55 }
$killUtc = [DateTimeOffset]::UtcNow
Write-Phase "KILL_UTC = $($killUtc.ToString('o')) (force-deleted $leader)"
```

### Example 2 — Send-evidence ES query (leader fires only; excludes follower-skip)
```jsonc
// POST logs-generic.otel-default/_search  — identical in spirit to BuildStepSearchBody.
// `exists attributes.StepId` = processor hop-executed records = real leader sends.
// Follower-skip logs carry no StepId → naturally excluded. No `role` filter needed here.
{
  "size": 2000,
  "query": {
    "bool": {
      "filter": [
        { "exists": { "field": "attributes.StepId" } },
        { "exists": { "field": "attributes.CorrelationId" } },
        { "range": { "@timestamp": { "gte": "<WINDOW_START_UTC:o>", "lte": "<WINDOW_END_UTC:o>" } } }
      ]
    }
  },
  "sort": [ { "@timestamp": "asc" } ]
}
```

### Example 3 — 30s-bucket uniqueness scorer (pure C#, claims #1 + #2)
```csharp
// Group the StepId hits by correlationId, take each fire's earliest @timestamp (≈ Step_A / fire time),
// floor to the 30s wall-clock boundary. Assert ≤1 distinct fire-correlationId per bucket.
// Reuse TryReadTimestamp + defensive attribute reads (T-66-09) from AnalyzerE2ETests.
static long Bucket30s(DateTimeOffset ts) => ts.ToUnixTimeSeconds() - (ts.ToUnixTimeSeconds() % 30);

var fireTimeByCorr = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
foreach (var hit in stepHits)
{
    // defensive reads of _source.attributes.CorrelationId + _source.@timestamp (drop odd-shaped)
    if (!TryReadCorr(hit, out var corr) || !TryReadTimestamp(SourceOf(hit), out var ts)) continue;
    if (!fireTimeByCorr.TryGetValue(corr, out var cur) || ts < cur) fireTimeByCorr[corr] = ts;
}
var byBucket = fireTimeByCorr
    .GroupBy(kv => Bucket30s(kv.Value))
    .ToDictionary(g => g.Key, g => g.Select(x => x.Key).Distinct().Count());

bool zeroDuplicate = byBucket.Values.All(distinctCorrIds => distinctCorrIds <= 1); // claim #1 + #2
```

### Example 4 — gap-skip, not backfilled (claim #3)
```csharp
// A fire bucket exists (≥1 corrId) or is empty (0). The gap is the empty bucket(s) between the last
// pre-kill fire bucket and the first post-recovery fire bucket, overlapping [KILL_UTC, roleFlipUtc].
var fireBuckets = byBucket.Keys.OrderBy(b => b).ToList();
long killBucket = Bucket30s(killUtc);
long lastPreKill  = fireBuckets.Where(b => b <  killBucket).DefaultIfEmpty(long.MinValue).Max();
long firstPostRec = fireBuckets.Where(b => b >= killBucket).DefaultIfEmpty(long.MaxValue).Min();

// enumerate every 30s boundary strictly between the two — each must be EMPTY (no fire), and there
// must be >= 1 of them (the skipped tick). Non-empty here would be a backfilled gap tick (FAIL).
var gapBoundaries = Enumerable.Range(1, (int)((firstPostRec - lastPreKill) / 30) - 1)
                              .Select(i => lastPreKill + 30L * i).ToList();
bool gapObserved   = gapBoundaries.Count >= 1;                              // Pitfall 1 guards this
bool notBackfilled = gapBoundaries.All(b => !byBucket.ContainsKey(b));      // stayed empty forever
// gapObserved==false ⇒ INCONCLUSIVE (coverage miss); notBackfilled==false ⇒ FAIL.
```

### Example 5 — recovery + role-transition (claims #4 + #5)
```jsonc
// POST logs-generic.otel-default/_search — earliest role=leader AFTER the kill = the survivor's flip.
// The killed pod emits nothing post-kill; only the winner flips to leader. attributes.role is a keyword.
{
  "size": 20,
  "query": {
    "bool": {
      "filter": [
        { "term":  { "attributes.role": "leader" } },
        { "range": { "@timestamp": { "gt": "<KILL_UTC:o>" } } }
      ]
    }
  },
  "sort": [ { "@timestamp": "asc" } ]
}
```
```csharp
// claim #5 = at least one such record exists; claim #4 = its @timestamp − killUtc <= 30s.
bool roleFlipVisible = roleHits.Count > 0;                                     // claim #5
var recovery = roleFlipVisible ? firstRoleLeaderTs - killUtc : (TimeSpan?)null;
bool boundedRecovery = recovery is { } r && r >= TimeSpan.Zero && r <= TimeSpan.FromSeconds(30); // claim #4
```

### Example 6 — verdict + exit-code (STEP H, mirrors phase-80-harness:476-513)
```powershell
$env:WINDOW_START_UTC = $windowStart.ToString('o')
$env:WINDOW_END_UTC   = $windowEnd.ToString('o')
$env:KILL_UTC         = $killUtc.ToString('o')
$env:SCENARIO_ID      = 'phase-83-ha'
try {
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release `
      -- --filter-method "*Ha_Failover_Window_Yields_Pass*" 2>&1 | Out-String | Write-Host
    $analyzerExit = $LASTEXITCODE
} finally {
    Remove-Item Env:WINDOW_START_UTC, Env:WINDOW_END_UTC, Env:KILL_UTC, Env:SCENARIO_ID -ErrorAction SilentlyContinue
}
# authoritative verdict from the report JSON (written before the assert), mapped 0/1/2:
$report = Get-ChildItem tests/BaseApi.Tests/bin -Recurse -Filter 'phase-83-ha.json' |
          Where-Object FullName -match 'analyzer-reports' | Select-Object -First 1
$analyzerExit = Resolve-AnalyzerExitCode (Get-Content $report.FullName -Raw | ConvertFrom-Json)
exit $analyzerExit
```

### Suggested exit-code table (mirror phase-80, add HA-specific infra codes)
```
0   HA verdict PASS (all five claims green)
1   HA verdict FAIL (a real finding: bucket >=2, backfilled gap tick, recovery >30s, or no role flip)
2   HA verdict INCONCLUSIVE (observability-blind: no StepId records, or gap tick never landed — coverage miss)
10  bring-up (build/up) failed
20  reset failed
25  orchestrator clean rollout-restart failed
30  seeder failed
40  wf-id lookup failed/empty
45  Lease holderIdentity read failed/empty   (NEW — leader-lookup)
50  activation gate != 204
55  force-delete of leader pod failed        (NEW — kill)
60  pre-kill clean-fire never observed        (NEW — sequencer precondition)
```

## Validation Architecture

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 under Microsoft.Testing.Platform (MTP), project `tests/BaseApi.Tests` [VERIFIED] |
| Config file | none — MTP-native; RealStack facts gated `[Trait("Category","RealStack")]` |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*Ha_Failover_Window_Yields_Pass*"` |
| Full suite command | the phase-83 harness end-to-end: `pwsh -File scripts/phase-83-ha-failover.ps1` (exit 0/1/2) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|--------------|
| HA-07 (#1 exactly-one-leader) | ≤1 fire-corrId per 30s bucket (split-brain ⇒ ≥2) | live E2E verdict | `…--filter-method "*Ha_Failover_Window_Yields_Pass*"` | ❌ Wave 0 — new `HaFailoverAnalyzerE2ETests` |
| HA-07 (#2 zero-duplicate) | ≤1 fire-corrId per bucket across the whole failover window | live E2E verdict | same fact | ❌ Wave 0 |
| HA-07 (#3 gap skipped, not backfilled) | ≥1 contiguous empty bucket between last-pre-kill & first-post-recovery; stays empty | live E2E verdict | same fact | ❌ Wave 0 (needs phase-aligned kill) |
| HA-07 (#4 bounded recovery ≤30s) | earliest `role=leader` after KILL_UTC within 2×LeaseDuration | live E2E verdict | same fact | ❌ Wave 0 |
| HA-07 (#5 role transition visible) | ≥1 `attributes.role=leader` record post-kill from a survivor | live E2E verdict | same fact | ❌ Wave 0 |
| (pure scorer) 30s-bucket uniqueness + gap logic | synthetic-hit unit coverage of the bucketing/gap scorer | hermetic unit | `dotnet test … --filter-not-trait Category=RealStack` (BaseApi.Tests.exe direct on Windows) | ❌ Wave 0 — mirror `StructuralCohortFacts` |

### Sampling Rate (Nyquist)
- **The sampling instrument IS the ES 30s bucketing.** The "which leader fires this tick" signal is sampled once per 30s cron tick.
- **Nyquist concern (the one real tension):** the *event to detect* (the ~11–17s election gap) is **shorter than the 30s sampling interval**. A single, randomly-phased sample (kill) will miss the gap on ~40–57% of runs — claim #3 becomes a coin-flip. **Resolution:** do NOT raise the cron rate (that would break the locked `*/30` bucket, D-03a); instead **phase-align the perturbation** — kill ~3s before a boundary so the sampled tick is guaranteed to fall in the gap (Pitfall 1 / Pattern 3). This converts an unreliable random sample into a deterministic one without changing the sampling rate.
- **Per verdict run:** the full end-to-end harness (single failover, single window). No per-commit sampling (this is a proof, not iterative dev).
- **Phase gate:** `pwsh -File scripts/phase-83-ha-failover.ps1` exits 0; the hermetic scorer facts green under the standard `--filter-not-trait Category=RealStack` run.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Observability/HaFailoverAnalyzerE2ETests.cs` — the RealStack verdict fact (covers HA-07 #1–#5)
- [ ] `tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorer.cs` (+ hermetic `HaFireBucketScorerFacts.cs`) — the pure 30s-bucket + gap scorer, hermetically testable via synthetic hits (mirror `StructuralCohort`/`StructuralCohortFacts`)
- [ ] `scripts/phase-83-ha-failover.ps1` — the sequencer + verdict wiring
- [ ] (optional) a phase-83 close/roll-up + `analyzer-reports/phase-83-summary.json` shape mirroring `phase-81-summary.json`

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Post-kill election gap is ≈ kill + [11,17]s (so recovery ≤30s has margin, and ≤1 boundary falls in the gap). | Standard Stack → Election timing | If the gap can exceed ~28s, a single boundary might not be the only skipped tick, or recovery could approach the 30s threshold. Mitigation: the ≤30s bound is 2×LeaseDuration; measure the actual delta in the report and treat >30s as FAIL (real finding, per D-06). VERIFY by observing the first live run's recovery delta. |
| A2 | On single-node Docker-Desktop k8s, host and pod clocks are effectively identical (recovery-delta skew ≈ 0). | Pitfall 3 | A skew would bias the recovery delta. Low risk (shared VM clock); the 2×LeaseDuration margin absorbs a few seconds. |
| A3 | A surviving (already-Ready, hydrated) follower wins the election before the k8s replacement pod (readiness `initialDelaySeconds:30`). | Pitfall 5 | If the replacement won, recovery time grows but correctness holds; the ≤30s bound still guards. |
| A4 | The processor stays up across the failover (only the orchestrator leader is killed), so every real fire reliably produces a `StepId` record to anchor the bucket. | Pattern 1 | If the processor flaked, a real fire could produce no send-evidence → a false empty bucket. Mitigation: STEP A2/B0 liveness gates ensure processors are Ready; the drain+poll-to-stable tolerates export skew. |
| A5 | `attributes.role` is indexed as a `keyword` (term-queryable) like the other `all_strings_to_keywords` attributes. | Code Example 5 | If it were `text`, the `term` query would miss. Mitigation: Wave-0 confirm with `GET /logs-generic.otel-default/_mapping/field/attributes.role` before locking the query (single-const style check, same as EsIndexNames). |

## Open Questions (RESOLVED)

> All three resolved during planning with a fail-safe in the plans: Q1 → KILL_UTC pinned at kubectl return + scorer overlap-guard self-reports INCONCLUSIVE on a mistimed kill (83-01/83-03); Q2 → `!gapObserved → Inconclusive` fold (83-01); Q3 → Wave-0 `_mapping/field` probe with "value correct regardless" fallback (83-01 Task 1). Plans implement each resolution; none affects whether HA-07 is proven.

1. **Is the pre-boundary kill offset (~3s) large enough given kubectl force-delete latency?**
   - What we know: force-delete returns quickly; the gap minimum is ~11s, so a 3s offset leaves ~8s of margin for the boundary tick to land in the gap.
   - What's unclear: `kubectl delete --force` round-trip latency on Docker-Desktop under load.
   - Recommendation: pin `KILL_UTC` at the moment `kubectl delete` **returns** (not before), and keep the offset in the 2–5s range; the verdict cross-checks that the empty gap bucket actually overlaps `[KILL_UTC, roleFlipUtc]` so a mistimed kill self-reports as INCONCLUSIVE rather than passing vacuously.

2. **Should claim #3 "coverage miss" be INCONCLUSIVE or a hard sequencer abort?**
   - What we know: a phase-aligned kill should always produce a gap tick; its absence means the timing failed, not that the system is broken.
   - Recommendation: **INCONCLUSIVE (exit 2)** — operator-re-runnable, never auto-retried, per the Phase-81 discipline. Do NOT emit PASS on a window that never observed a gap tick.

3. **Verify `attributes.role` field mapping (keyword vs text) at Wave 0** (A5). Single `_mapping/field` probe; update the query const if needed.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Docker-Desktop k8s + `kubectl` | leader read / force-kill / rollout | assumed ✓ (Phase 80/81 ran on it) | env | none — blocking |
| Phase-82 mechanism in `orchestrator:local` | the proof target | ✓ (merged; `git log` shows phase-82 complete) | — | rebuild via STEP A0 |
| ES 8.15.5 @ localhost:9200 (port-forward) | both evidence streams | ✓ (phase-80-up port-forwards 9200) | 8.15.5 | none — blocking |
| `v8-fanout-proof` seeder + POST /start | the firing workload | ✓ (STEP C/E reuse) | — | none — blocking |
| PowerShell 7 + `dotnet` SDK | harness + verdict | ✓ | env | none — blocking |

**Missing dependencies with no fallback:** none identified — all are the same dependencies Phase 80/81 already exercised on this machine. Confirm Docker-Desktop k8s is running before the harness (STEP A/up handles bring-up).

## Security Domain

`security_enforcement` is not set to `false` in config, so this section is included. This phase writes **dev/ops-only tooling** (no product source, no new network surface).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes | The verdict's `SCENARIO_ID`/report-id must pass the existing `^[A-Za-z0-9_-]+$` path-traversal whitelist BEFORE composing any report path (reuse `ScenarioIdPattern` from `AnalyzerE2ETests.cs:68,132`). ES query bodies are **static raw-string templates** with only Wave-0-verified field consts + validated ISO timestamps interpolated (no untrusted concatenation — T-66-08). |
| V4 Access Control | yes | kubectl ops are namespace-scoped (`-n skp`); the orchestrator SA is least-privilege lease-only (Phase-82 RBAC, unchanged). The harness runs with the operator's kubeconfig — no new grants. |
| V6 Cryptography | no | none. |
| V2/V3 Auth/Session | no | no auth surface added. |

### Known Threat Patterns
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Report-path traversal via a crafted scenario/report id | Tampering | `^[A-Za-z0-9_-]+$` whitelist before path compose (reuse existing guard). |
| ES query injection via interpolated window/kill timestamps | Tampering | Interpolate only `DateTimeOffset:o` values validated by `TryParseUtc`; static template body (T-66-08). |
| kubectl targeting the wrong pod | Tampering / DoS (self-inflicted) | Target ONLY the Lease `holderIdentity` string read from the API (D-02a); never scale; namespace-scoped. |
| Verdict false-green suppressing a real finding | Repudiation | Fail-closed exit resolution (unknown/absent verdict → 1); INCONCLUSIVE (2) for coverage/observability gaps, never PASS. |

## Sources

### Primary (HIGH confidence — in-repo, verified this session)
- `src/Orchestrator/Election/LeaderElectionService.cs` — timings (15s/10s/2s), Lease `skp/orchestrator-leader`, identity = `POD_NAME`, callbacks are sole `LeaderState` writer.
- `src/Orchestrator/Election/LeaderState.cs` — role literal `leader`/`follower`, volatile-swap.
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — per-fire correlationId mint (:58), `IsLeader && IsReady` gate (:76), leader branch emits no log, follower-skip log (:106), reschedule-next (no backfill, :119-121).
- `src/Orchestrator/Dispatch/StepDispatcher.cs` — **no logger**; Send + metric only (the reason send-evidence is downstream).
- `src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs` — `attributes.role` on every log, read live.
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — `BuildStepSearchBody`, `SearchAllHits`, `PollHitsToStableAsync`, `TryReadTimestamp` (epoch-ms-string), `TryParseUtc` env seam, write-then-assert, scenario-id path guard.
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` — index `logs-generic.otel-default`, field paths (no `.keyword`), `@timestamp` is `date`.
- `tests/BaseApi.Tests/Observability/Helpers/ElasticsearchTestClient.cs` — ES `_search` plumbing.
- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` — `v8-fanout-proof`, cron `*/30 * * * * *`.
- `scripts/phase-80-harness.ps1`, `scripts/phase-81-sweep.ps1`, `scripts/lib/exit-code-resolution.ps1` — STEP primitives, env seam, exit-code discipline.
- `k8s/31-orchestrator.yaml` — replicas 3, RollingUpdate, `POD_NAME` downward env, readiness `initialDelaySeconds:30`.

### Secondary (MEDIUM confidence)
- kubernetes-client/csharp `src/KubernetesClient/LeaderElection/LeaderElector.cs` — acquire condition `observedTime + LeaseDuration < now` for a non-holder; retries every `RetryPeriod`; `OnStartedLeading` after `AcquireAsync` — the basis for the ~11–17s gap bracket. https://github.com/kubernetes-client/csharp/blob/master/src/KubernetesClient/LeaderElection/LeaderElector.cs

### Tertiary (LOW — background, not load-bearing)
- General k8s leader-election / lease articles (martowen.com, kubernetes.recipes) — corroborate the renew/expiry model.

## Metadata

**Confidence breakdown:**
- Send-evidence mechanics / D-03 query (StepId, no `.keyword`, correlationId-per-fire, follower exclusion): **HIGH** — every claim traced to in-repo source.
- Harness reuse + exit-code discipline: **HIGH** — direct from phase-80/81 scripts.
- Recovery-bound (~11–17s gap, ≤30s threshold justification): **MEDIUM** — reasoned from the verified LeaderElector acquire condition + the fixed timings; the exact bracket is unmeasured live (A1). The ≤30s (2×LeaseDuration) gate is deliberately generous, so the plan is safe even if the bracket shifts.
- Nyquist gap-coverage finding + phase-aligned-kill fix: **HIGH** (the arithmetic is unambiguous) — this is the key design input for the planner and directly resolves the brief's point-6 tension.

**Research date:** 2026-07-18
**Valid until:** ~30 days (stable — depends only on merged Phase-82 code + fixed election timings; re-verify `attributes.role` mapping (A5) at Wave 0).
</content>
</invoke>
