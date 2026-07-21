# Phase 82: Orchestrator HA — Kubernetes-Lease Leader Election — Specification

**Created:** 2026-07-18
**Ambiguity score:** 0.13 (gate: ≤ 0.20)
**Requirements:** 6 locked

## Goal

The orchestrator gains single-leader mutual exclusion for scheduler-triggered entry-step sends: a `BackgroundService` runs the `KubernetesClient` `LeaderElector` over a `coordination.k8s.io/v1` Lease, its callbacks are the single writer of a volatile `LeaderState`, and `WorkflowFireJob`'s `foreach … DispatchAsync` loop is gated so only the leader fires — with a role log enricher, RBAC, and the Deployment scaled to 3 replicas. Phase 82 closes on build + hermetic + manifest validation; all live leader-election behavior is proven in Phase 83.

## Background

The orchestrator today runs as a single replica (`k8s/31-orchestrator.yaml`: `replicas: 1`, `strategy: Recreate`), a documented SPOF. Its Quartz cron drives `WorkflowFireJob.Execute` (`src/Orchestrator/Scheduling/WorkflowFireJob.cs`), which mints a fresh per-fire `correlationId` (`NewId.NextGuid()`, line 54) and runs a `foreach entryStepId … dispatcher.DispatchAsync(...)` loop (lines 69-87) followed by an L1 liveness refresh + `RescheduleAsync` (lines 89-101). Every replica's in-process scheduler fires the same cron independently and each fire mints a distinct random `correlationId`, so two un-gated fires produce two genuinely-distinct runs that downstream idempotency will NOT collapse — this loop is the sole place horizontal scaling can duplicate work.

No HA machinery exists: no `KubernetesClient` dependency, no `LeaderState`, no election `BackgroundService`, no RBAC manifest for `leases`. The role-enricher pattern to mirror already exists: `ProcessorIdLogEnricher : BaseProcessor<LogRecord>` (`src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs`) stamps `attributes.ProcessorId` via `ConfigureOpenTelemetryLoggerProvider((sp, lp) => lp.AddProcessor(...))`; `OrchestratorRoleLogEnricher` is a near-clone that reads `LeaderState.Role` instead of `IProcessorContext.Id`. The orchestrator wires observability through `AddBaseConsoleObservability` and has no role enricher today.

The historical "two orchestrators can't both run" exclusive-queue constraint (the stated reason for `replicas: 1` + `Recreate`) is **dismissed by this design** — the orchestrator's receive endpoints are already per-replica (`InstanceId`/`Temporary`, `src/Orchestrator/Program.cs:36-56`), so multiple replicas co-exist safely; leader election governs only the fire. The Deployment bumps to 3 replicas for the Phase-83 failover test.

## Requirements

1. **Leader-only scheduler fire** (HA-01): Exactly one replica (the leader) performs entry-step sends; followers run identical `WorkflowFireJob` logic minus the sends.
   - Current: `WorkflowFireJob.Execute` always runs its `foreach … DispatchAsync` loop — no leader concept exists
   - Target: the `foreach … DispatchAsync` loop is gated on `LeaderState.IsLeader`; a follower skips ONLY the sends and still executes the L1 liveness refresh + `RescheduleAsync` (lines 89-101) unchanged
   - Acceptance: a hermetic test drives `WorkflowFireJob.Execute` with `LeaderState` = follower and asserts zero `DispatchAsync` calls occurred AND the L1 refresh + reschedule still ran; with `LeaderState` = leader it asserts the sends fire

2. **LeaderElector hosted in a BackgroundService** (HA-02): Election uses the `KubernetesClient` `LeaderElector` over a `coordination.k8s.io/v1` Lease, hosted in a `BackgroundService`; every replica starts as a follower.
   - Current: no election service, no `KubernetesClient` dependency
   - Target: a `BackgroundService` constructs a `LeaderElector` with `LeaseLock(IKubernetes, "skp", "orchestrator-leader", identity)` and runs `RunAndTryToHoldLeadershipForeverAsync`; `LeaseDuration 15s / RenewDeadline 10s / RetryPeriod 2s`; the initial `LeaderState` is follower before any acquisition
   - Acceptance: the solution compiles with `KubernetesClient` referenced; a hermetic test asserts the default/unstarted `LeaderState` is follower (`IsLeader == false`, `Role == "follower"`)

3. **Single-writer volatile LeaderState gate** (HA-03): The gate is a volatile `LeaderState` snapshot written ONLY by the election callbacks and read by the fire loop; step-advancement stays ungated.
   - Current: no shared leader flag exists
   - Target: `LeaderState` (fields `IsLeader` / `Role` / `CurrentLeaderId`) is mutated ONLY inside the `OnStartedLeading` / `OnStoppedLeading` (and `OnNewLeader`) callbacks; `WorkflowFireJob` reads it; `RelocateTail` / `StepDispatcher` step-advancement contains NO `LeaderState` read
   - Acceptance: grep confirms `LeaderState` is assigned only within the election callback wiring (single writer) and read in `WorkflowFireJob`; `RelocateTail.cs` and the step-advancement path contain no `LeaderState`/`IsLeader` reference; a hermetic test drives the callbacks and asserts the snapshot flips leader↔follower accordingly

4. **Self-demotion fence + skip-on-gap semantics** (HA-04): A demoted leader closes its gate within `RenewDeadline` (< `LeaseDuration`); cron ticks in the election gap are skipped, not backfilled; a bounded paused-leader duplicate is tolerated by design.
   - Current: no demotion or gap semantics exist
   - Target: `OnStoppedLeading` sets `LeaderState` to follower (closing the gate) — relying on `RenewDeadline (10s) < LeaseDuration (15s)` as the self-demotion fence; a follower fire during the gap is simply skipped (no queue/backfill of the missed tick); no fencing token is added (the ≈`RenewDeadline` paused-leader duplicate window is accepted)
   - Acceptance: a hermetic test asserts `OnStoppedLeading` transitions `LeaderState` to follower; code review confirms no backfill/catch-up of skipped ticks and no fencing-token logic (matches the deferred-scope decisions); the `RenewDeadline < LeaseDuration` config values are asserted present

5. **Role log enricher** (HA-05): Every orchestrator log carries `attributes.role = leader|follower`, added by an OTel `LogRecord` enricher reading the live `LeaderState`, dynamic across failover, present from the first boot log defaulting to follower.
   - Current: orchestrator logs carry no `role` attribute; no orchestrator log enricher exists
   - Target: `OrchestratorRoleLogEnricher : BaseProcessor<LogRecord>` appends `attributes.role` from the live `LeaderState.Role`, registered via `ConfigureOpenTelemetryLoggerProvider((sp, lp) => lp.AddProcessor(...))` mirroring `ProcessorIdLogEnricher`; before/without leadership it emits `follower`, never empty
   - Acceptance: a hermetic enricher test asserts a `LogRecord` gains `attributes.role == "follower"` when `LeaderState` is follower and `"leader"` when leader; the enricher reads live state (a post-construction flip is reflected on the next record)

6. **RBAC + KubernetesClient + replica bump** (HA-06): RBAC grants the orchestrator ServiceAccount lease access in `skp`; `KubernetesClient` is added; the Deployment scales to 3 replicas.
   - Current: no RBAC manifest for the orchestrator; `k8s/31-orchestrator.yaml` is `replicas: 1`; no `KubernetesClient` dependency
   - Target: a k8s Role + RoleBinding grant the orchestrator ServiceAccount `get/create/update` on `leases` in `coordination.k8s.io` within `skp`; the orchestrator uses that ServiceAccount; `k8s/31-orchestrator.yaml` `replicas: 3`; `KubernetesClient` added to the Orchestrator project
   - Acceptance: `kubectl apply --dry-run` (or equivalent server/client validation) accepts the RBAC + orchestrator manifests without error; the Role targets `leases`/`coordination.k8s.io` verbs `get,create,update` in `skp`; `replicas: 3` is present; the Orchestrator `.csproj` references `KubernetesClient`

## Boundaries

**In scope:**
- `LeaderState` volatile snapshot type (single-writer, `IsLeader`/`Role`/`CurrentLeaderId`, follower default)
- Election `BackgroundService` running `KubernetesClient` `LeaderElector` over the `skp/orchestrator-leader` Lease
- The leader gate on `WorkflowFireJob`'s `foreach … DispatchAsync` loop only
- `OrchestratorRoleLogEnricher` stamping `attributes.role` on every orchestrator log
- RBAC Role + RoleBinding for `leases` in `skp`; orchestrator ServiceAccount wiring
- `KubernetesClient` dependency added to the Orchestrator project
- `k8s/31-orchestrator.yaml` `replicas: 3`
- Hermetic tests for: the gate predicate, `LeaderState` single-writer transitions, the role enricher
- 0-warning Debug + Release build; manifests validate/apply clean

**Out of scope:**
- Live leader-election behavior — bringing up N replicas, observing exactly-one-leader, killing the leader, zero-duplicate proof — that is **Phase 83** (HA-07); nothing live gates 82's close
- Fencing tokens to eliminate the paused-leader duplicate window — deferred (HA-04 tolerates it by design)
- Catch-up / backfill of cron ticks missed during the election gap — deferred (HA-04 chooses skip)
- Gating any send other than the scheduler-fire (`WorkflowFireJob`) — `RelocateTail`/step-advancement stays ungated so in-flight workflows never stall on failover
- Changing the fanout vs load-balance messaging topology — already correct and unchanged; the historical exclusive-queue constraint is dismissed, not re-engineered
- Multi-namespace / cross-cluster election — deferred

## Constraints

- New dependency: `KubernetesClient` NuGet (Orchestrator project only).
- Election timings are fixed to the verified design: `LeaseDuration 15s`, `RenewDeadline 10s`, `RetryPeriod 2s` (the `RenewDeadline < LeaseDuration` inequality is the self-demotion fence — do not invert).
- Lease identity: `coordination.k8s.io/v1` Lease named `orchestrator-leader` in namespace `skp`.
- `LeaderState` must be written by exactly one writer (the election callbacks) and read as a `volatile` snapshot — no locks, no second writer.
- The gate wraps the `DispatchAsync` send loop ONLY; the L1 liveness refresh, `RescheduleAsync`, and `RelocateTail` step-advancement remain ungated for all replicas.
- OTel `LogRecord` enricher must follow the `ProcessorIdLogEnricher` registration pattern (`ConfigureOpenTelemetryLoggerProvider` + `AddProcessor`) on OTel 1.15.3 (reassign `Attributes` only — the v1.5–v1.7 State-desync bug does not apply).
- k8s cluster access is NOT assumed available in the build/CI sandbox; manifest acceptance is validation (`--dry-run`/schema), not a live apply — consistent with this project's deferred-live-gate precedent.

## Acceptance Criteria

- [ ] `WorkflowFireJob.Execute` with `LeaderState` = follower performs ZERO `DispatchAsync` sends but still runs the L1 liveness refresh + `RescheduleAsync`; with leader it performs the sends (hermetic test)
- [ ] Default/unstarted `LeaderState` is follower (`IsLeader == false`, `Role == "follower"`) (hermetic test)
- [ ] `LeaderState` is assigned ONLY within the election callback wiring (single writer); `RelocateTail`/step-advancement contains no `LeaderState`/`IsLeader` reference (grep + review)
- [ ] `OnStoppedLeading` transitions `LeaderState` to follower; no backfill of skipped ticks and no fencing-token logic exists (hermetic test + review)
- [ ] `OrchestratorRoleLogEnricher` stamps `attributes.role` = `"follower"`/`"leader"` matching live `LeaderState`, never empty (hermetic test)
- [ ] RBAC Role targets `leases` in `coordination.k8s.io` with verbs `get,create,update` in `skp`; RoleBinding binds the orchestrator ServiceAccount; manifests pass `kubectl apply --dry-run`
- [ ] `k8s/31-orchestrator.yaml` has `replicas: 3`
- [ ] The Orchestrator project references `KubernetesClient`
- [ ] Solution builds 0-warning in Debug AND Release; all new hermetic tests green

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.90  | 0.75 | ✓      | Mechanism, gate target, lease name, timings all named        |
| Boundary Clarity   | 0.92  | 0.70 | ✓      | 82/83 split locked; deferrals + exclusive-queue dismissal explicit |
| Constraint Clarity | 0.80  | 0.65 | ✓      | Timings, dep, single-writer, k8s-sandbox posture fixed       |
| Acceptance Criteria| 0.82  | 0.70 | ✓      | All checks hermetic/build/manifest — no live gate on 82      |
| **Ambiguity**      | 0.13  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective            | Question summary                              | Decision locked                                                        |
|-------|------------------------|-----------------------------------------------|-----------------------------------------------------------------------|
| 1     | Researcher / Boundary  | What must 82 prove vs defer to 83?            | 82 stops at build + hermetic + manifests-apply-clean; all live → 83   |
| 1     | Boundary Keeper        | Exclusive-queue collision — resolve in 82?    | Dismissed; design runs safely multi-replica; deploy 3 replicas for 83 |
| 1     | Researcher             | Follower behavior on the gate?                | Follower = leader minus the dispatch sends (L1 refresh + reschedule stay) |

---

*Phase: 82-orchestrator-ha-leader-election*
*Spec created: 2026-07-18*
*Next step: /gsd-discuss-phase 82 — implementation decisions (how to build what's specified above)*
