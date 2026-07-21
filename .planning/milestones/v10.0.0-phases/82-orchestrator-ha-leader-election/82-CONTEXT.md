# Phase 82: Orchestrator HA — Kubernetes-Lease Leader Election — Context

**Gathered:** 2026-07-18
**Status:** Ready for planning

<domain>
## Phase Boundary

The orchestrator HA **leader-election mechanism**: a `LeaderState` snapshot, an election `BackgroundService` running the `KubernetesClient` `LeaderElector` over a `coordination.k8s.io/v1` Lease, a leader-gated `WorkflowFireJob` send loop, an `attributes.role` log enricher, RBAC, and the Deployment scaled to 3 replicas. Phase 82 closes on **build (0-warning Debug+Release) + hermetic tests + manifest validation** — all live leader-election behavior (bring-up, exactly-one-leader, kill-the-leader, zero-duplicate) is Phase 83.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**6 requirements are locked** (HA-01..HA-06). See `82-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `82-SPEC.md` before planning or implementing — requirements are not duplicated here.

**In scope (from SPEC.md):**
- `LeaderState` volatile snapshot (single-writer, `IsLeader`/`Role`/`CurrentLeaderId`, follower default)
- Election `BackgroundService` running `KubernetesClient` `LeaderElector` over the `skp/orchestrator-leader` Lease
- The leader gate on `WorkflowFireJob`'s `foreach … DispatchAsync` loop only
- `OrchestratorRoleLogEnricher` stamping `attributes.role` on every orchestrator log
- RBAC Role + RoleBinding for `leases` in `skp`; orchestrator ServiceAccount wiring
- `KubernetesClient` dependency added to the Orchestrator project
- `k8s/31-orchestrator.yaml` `replicas: 3`
- Hermetic tests: gate predicate, `LeaderState` single-writer transitions, role enricher
- 0-warning Debug + Release build; manifests validate/apply clean

**Out of scope (from SPEC.md):**
- Live leader-election behavior → Phase 83 (HA-07); nothing live gates 82's close
- Fencing tokens (paused-leader duplicate tolerated by design)
- Cron-tick catch-up/backfill during the election gap (HA-04 chooses skip)
- Gating any send other than the scheduler-fire (`WorkflowFireJob`) — `RelocateTail`/step-advancement stays ungated
- Changing the fanout vs load-balance messaging topology — the historical exclusive-queue constraint is dismissed, not re-engineered
- Multi-namespace / cross-cluster election

</spec_lock>

<decisions>
## Implementation Decisions

### Replica identity (the load-bearing decision)
- **D-01:** Derive **both** the LeaderElector lease-holder identity **and** the MassTransit endpoint `InstanceId` from a downward-API `POD_NAME` env (`valueFrom.fieldRef.fieldPath: metadata.name`). **Drop** the hardcoded `Orchestrator__InstanceId: orchestrator-1` env. In code, `InstanceId` defaults to `POD_NAME`, falling back to `Environment.MachineName` for local/hermetic runs.
- **Why this is mandatory, not cosmetic** — the fanout endpoints are named `orchestrator-{instanceId}` (`Program.cs:28-54`). With the hardcoded id at N>1, all pods declare the **same** queue name → they become competing-consumers → each broadcast reaches only ONE replica. `POD_NAME` gives each pod a distinct queue → true fanout. This is load-bearing in **three** independent ways verified during discussion:
  1. **WebAPI lifecycle broadcast** — `Start`/`Stop`/`Pause`/`Resume` are `Publish`-by-type (WebAPI never names the id) fanned to every replica's `orchestrator-{id}` queue; every replica needs the workflow in its own in-memory L1.
  2. **Shared-result-queue step-advancement + keeper reinject** — results and keeper reinjects `.Send` to the *stable* `queue:orchestrator-result` competing-consumer queue; whichever replica wins must resolve the next step from **its own** L1 (`OrchestratorPrePipeline.cs:73`), an L1 miss silently acks as "completed-unresolved" (step-loss). Correct fanout of `Start` is what keeps every replica's L1 populated to service this.
  3. **Keeper `PauseAll`/`ResumeAll` safety across failover** — `BitHealthLoop` `bus.Publish`es `PauseAll` on the L2-unhealthy edge (`Keeper/Health/BitHealthLoop.cs:82`); every replica (including a **future** leader) must receive it so a failover mid-outage doesn't promote an un-paused replica that fires into a downed L2.

### Election lifecycle & non-cluster behavior
- **D-02:** The election `BackgroundService` runs **only in-cluster**, detected via the `KUBERNETES_SERVICE_HOST` env. **Off-cluster** (compose / local dev — the phase-68/79 sweeps still run on compose), election is disabled and `LeaderState` **defaults to leader** so the lone instance fires normally. **In-cluster**, every replica starts as **follower** until it acquires the Lease. This reconciles with SPEC HA-05's "defaults to follower from first boot": that is the in-cluster pre-acquisition state; off-cluster there is no election, so the single instance is leader.

### Fire gate
- **D-05:** The `WorkflowFireJob` send gate requires **BOTH** `IsLeader` **AND** hydration-complete (read the existing `IStartupGate`/ready flag from `HydrationBackgroundService.MarkReady`). This deterministically closes the **cold-leader lost-fire** edge: a freshly-elected leader whose L1 has not finished hydrating would otherwise skip (lose) cron fires for un-hydrated workflows (`WorkflowFireJob.cs:46-51`, HA-04 skip-on-gap). "Leader fires" ⟺ "leader AND ready."
- **D-04:** Snapshot `IsLeader` **once** at the top of the fire (not per-iteration); gate **only** the `foreach … DispatchAsync` loop. A follower still runs the identical remainder — mint correlationId, open the log scope, L1 liveness refresh, `RescheduleAsync` (`WorkflowFireJob.cs:89-101`) — and emits **one** info log per skipped fire: `"Follower — leader gate closed; skipping entry-step sends for {WorkflowId}"`, ids in the log **scope** not the template (T-18-04). The leader path is byte-unchanged. This makes the leader/follower split visible in ES for the Phase-83 zero-duplicate proof.

### LeaderState & test seam
- **D-07:** `LeaderState` is an **immutable record** (`IsLeader`/`Role`/`CurrentLeaderId`) swapped through a **`volatile` reference** — the existing `LivenessProjection` immutable-swap idiom (torn-read-safe, lock-free). It is a DI singleton whose **sole writer** is the election `OnStartedLeading`/`OnStoppedLeading` (and `OnNewLeader`) callbacks; read by `WorkflowFireJob` and the role enricher.
- **D-06:** The election `BackgroundService` is wired **only in `Program.cs`**; hermetic tests **never start it** and there is **no `IKubernetes` stub**. Tests drive the three SPEC seams directly: manipulate the `LeaderState` singleton to exercise the gate predicate and the enricher; invoke `OnStartedLeading`/`OnStoppedLeading` directly for the transition tests. The `LeaderElector` wiring is **build-only** here, proven live in Phase 83 — matching the SPEC acceptance exactly.

### Rollout strategy
- **D-03:** Switch `k8s/31-orchestrator.yaml` `strategy: Recreate` → **`RollingUpdate`** (default 25% surge/unavailable) — multi-replica is now safe, so zero-downtime deploys are the point of the milestone. A rolling deploy that replaces the leader is just the normal HA-04 election gap. **Rewrite** the stale SPOF / exclusive-queue / "replicas LOCKED at 1" header comments (lines 1-25, 35-45) — they now contradict `replicas: 3`.

### RBAC & role enricher
- **D-08:** New RBAC manifest (e.g. `k8s/34-orchestrator-rbac.yaml`): a **ServiceAccount** for the orchestrator (it currently uses `default` — none is declared in `k8s/`), a **Role** granting `get,create,update` on `leases` in `coordination.k8s.io`, and a **RoleBinding**, all in `skp`. The orchestrator Deployment references the SA. Add the file to `k8s/kustomization.yaml` `resources`.
- **D-09:** `OrchestratorRoleLogEnricher : BaseProcessor<LogRecord>` stamps the **lowercase** `attributes.role` (literal `"role"`, per SPEC) = `leader|follower` from the live `LeaderState.Role`, registered via `ConfigureOpenTelemetryLoggerProvider((sp, lp) => lp.AddProcessor(...))` in `Program.cs` — the exact `ProcessorIdLogEnricher` pattern. Never empty; reads live state so a failover flip shows on the next record.

### Claude's Discretion
- Exact file names/namespaces for the new `LeaderState`, election service, and enricher types; the precise `LeaderElectionConfig` wiring (timings are fixed by SPEC: LeaseDuration 15s / RenewDeadline 10s / RetryPeriod 2s); RBAC manifest filename and SA name; how the `KUBERNETES_SERVICE_HOST` check is surfaced (helper vs inline).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements (read first)
- `.planning/phases/82-orchestrator-ha-leader-election/82-SPEC.md` — the 6 locked requirements, boundaries, acceptance criteria. **MUST read before planning.**
- `.planning/REQUIREMENTS.md` — HA-01..HA-07 + deferred/out-of-scope lists.
- `.planning/ROADMAP.md` §"v10.0.0 Orchestrator High Availability" (Phase 82/83 entries + the 2026-07-18 source-of-truth design conversation, verified `KubernetesClient` API).

### The gate target & result path
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — the `foreach … DispatchAsync` loop (69-87) to gate; L1 refresh + reschedule (89-101) stay ungated; the L1-miss skip (46-51).
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` §L1 read (73-79) — the "completed-unresolved" silent-ack on L1 miss (the step-loss surface that D-01 protects).
- `src/Orchestrator/Program.cs` — `instanceId` resolution (30), MassTransit fanout vs competing-consumer endpoints (28-70), OTel/observability wiring, hosted-service registration (98-103).

### Enricher & concurrency patterns to mirror
- `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` — the `BaseProcessor<LogRecord>` enricher to clone for `OrchestratorRoleLogEnricher`.
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:172-174` — the `ConfigureOpenTelemetryLoggerProvider` + `AddProcessor` registration pattern.
- `WorkflowFireJob.cs:91-95` — the `LivenessProjection` immutable-`with`-swap idiom to mirror for `LeaderState`.

### Hydration (the D-05 "hydrated" gate term)
- `src/Orchestrator/Hydration/HydrationBackgroundService.cs` — boot bulk-hydrate from the L2 parent index, `IStartupGate.MarkReady` on completion (the readiness/hydrated flag).
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` — read-only L2→L1 hydrate-one (`HydrateAndScheduleAsync`).

### Keeper (unchanged in 82 — confirms no keeper edits)
- `src/Keeper/Health/BitHealthLoop.cs:72,82` — `bus.Publish(PauseAll/ResumeAll)` on the L2 health edge (fanout by type).
- `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs:81-82` — `.Send` to `queue:orchestrator-result` (stable competing-consumer queue).
- `src/Messaging.Contracts/OrchestratorQueues.cs:16` — `Result = "orchestrator-result"`.

### k8s manifests
- `k8s/31-orchestrator.yaml` — Deployment (replicas → 3, Recreate → RollingUpdate, `POD_NAME` downward env, SA ref, drop hardcoded InstanceId, rewrite stale comments).
- `k8s/kustomization.yaml` — add the new RBAC manifest to `resources`.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`ProcessorIdLogEnricher` + its DI registration** — near-exact template for `OrchestratorRoleLogEnricher` (swap `IProcessorContext.Id` for `LeaderState.Role`, key `"role"`).
- **`LivenessProjection` immutable-swap** (`WorkflowFireJob.cs:91-95`) — the shape idiom for the `LeaderState` volatile-ref swap.
- **`IStartupGate` / `HydrationBackgroundService.MarkReady`** — the existing "ready/hydrated" signal the D-05 gate reads (no new hydration plumbing needed).

### Established Patterns
- **Fanout (`InstanceId`+`Temporary`) vs competing-consumer (stable queue) endpoints** (`Program.cs:28-70`) — the topology is correct and unchanged; only per-pod `InstanceId` uniqueness (D-01) makes fanout a true broadcast at N>1.
- **Every replica self-hydrates its full L1 from the durable L2 parent index on boot** — the steady-state late-joiner gap is CLOSED; a replica does not depend on having received a `Start` broadcast.
- **Business-vs-infra ack split** and **ids-in-scope-not-template (T-18-04)** logging conventions carry to the new follower-skip log.

### Integration Points
- `WorkflowFireJob` — add the `IsLeader && hydrated` gate around the dispatch loop + the follower-skip log.
- `Program.cs` — register `LeaderState` (singleton), the election `BackgroundService` (in-cluster only), and the role enricher.
- `k8s/31-orchestrator.yaml` + new RBAC manifest + `kustomization.yaml` — the deploy-surface changes.
- **Keeper: no changes** — it addresses the orchestrator by message-type (`Publish`) or stable queue name (`.Send`), never by per-replica identity.

</code_context>

<specifics>
## Specific Ideas

- `POD_NAME` via the Kubernetes **downward API** (`fieldRef: metadata.name`) is the single identity source for both the lease and the bus endpoint.
- Fixed election timings (SPEC): LeaseDuration 15s, RenewDeadline 10s (< LeaseDuration = self-demotion fence), RetryPeriod 2s.
- Lease: `coordination.k8s.io/v1`, name `orchestrator-leader`, namespace `skp`.
- Follower-skip log text: `"Follower — leader gate closed; skipping entry-step sends for {WorkflowId}"` (WorkflowId in scope).

</specifics>

<deferred>
## Deferred Ideas

- **Boot-window edge** — the MassTransit bus consumes `orchestrator-result` from host-start while L1 finishes hydrating a beat later; a result load-balanced into that sub-second window for a not-yet-hydrated workflow silently acks as "completed-unresolved." Pre-existing at single-replica restart; `RollingUpdate` makes it more frequent. **Out of 82 scope; Phase-83 watch item** (measure under the failover kill test).
- **PauseAll pause-state not hydrated** — pause is transient Quartz trigger state, not a durable L2 flag; a pod booting mid-L2-outage misses the `PauseAll` edge and hydrates *active*. Largely self-neutralizes (during a real Redis outage hydration can't complete → gate stays closed → empty L1 → fire skips). **Narrow fault-injection edge; Phase-83 territory.**
- **Fencing tokens** to eliminate the paused-leader duplicate window — deferred by SPEC (HA-04 tolerates it).
- **Cron-tick backfill/catch-up** across the election gap — deferred by SPEC (HA-04 chooses skip).
- **Multi-namespace / cross-cluster** leader election — deferred by SPEC.

</deferred>

---

*Phase: 82-orchestrator-ha-leader-election*
*Context gathered: 2026-07-18*
*Next step: /gsd-plan-phase 82 — implementation planning (task breakdown for what's decided above)*
