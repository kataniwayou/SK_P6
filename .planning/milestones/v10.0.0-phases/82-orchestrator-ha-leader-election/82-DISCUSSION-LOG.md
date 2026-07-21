# Phase 82: Orchestrator HA — Leader Election — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-18
**Phase:** 82-orchestrator-ha-leader-election
**Areas discussed:** Replica identity, Rollout strategy, Follower fire observability, Test seam + LeaderState shape, (surfaced) Keeper→orchestrator fanout, Late-joiner L1 gap / hydration, Cold-leader fire gate

---

## Replica identity

| Option | Description | Selected |
|--------|-------------|----------|
| Derive both from `POD_NAME` | Downward-API `metadata.name` drives both lease id and MassTransit InstanceId; drop hardcoded `orchestrator-1` | ✓ |
| Keep `Orchestrator__InstanceId` explicit per-pod, `POD_NAME` for lease only | Two identity sources | |

**User's choice:** Drop the hardcoded `orchestrator-1`; derive both from `POD_NAME`.
**Notes:** User asked whether dropping the hardcode affects WebAPI fanout, then keeper `PauseAll`/`ResumeAll` fanout. Verified in code: WebAPI and keeper both address by message-**type** (`Publish`), never by instance id — so they are unaffected; only the per-replica queue name (`orchestrator-{instanceId}`) matters, and per-pod uniqueness is exactly what fanout needs. Confirmed load-bearing in three ways (lifecycle broadcast, shared-result-queue L1 resolution, PauseAll-across-failover). Decision is mandatory, not cosmetic.

## Rollout strategy

| Option | Description | Selected |
|--------|-------------|----------|
| RollingUpdate | Zero-downtime HA through deploys | ✓ |
| Recreate (keep) | Brief downtime; the old SPOF rationale | |

**User's choice:** RollingUpdate.
**Notes:** Also rewrite the stale SPOF/exclusive-queue header comments in `31-orchestrator.yaml`. A rolling deploy replacing the leader is the normal HA-04 election gap.

## Follower fire observability

| Option | Description | Selected |
|--------|-------------|----------|
| Explicit follower-skip log | One info log per skipped fire; `IsLeader` snapshot once per fire; gate only the DispatchAsync loop | ✓ |
| Silent | Rely on role tag + absence of dispatch logs | |

**User's choice:** Explicit log; follower = leader minus the dispatch sends (L1 refresh + reschedule stay ungated).
**Notes:** Aids the Phase-83 zero-duplicate proof.

## Test seam + LeaderState shape

| Option | Description | Selected |
|--------|-------------|----------|
| Build-only elector + immutable volatile-swap record | No `IKubernetes` stub; test LeaderState/gate/enricher directly; `LeaderState` immutable record swapped via volatile ref | ✓ |
| Stub `IKubernetes` to exercise elector wiring hermetically | Broader hermetic surface | |

**User's choice:** Build-only elector seam; `LeaderState` = immutable record + volatile-ref swap (LivenessProjection idiom), DI singleton, election callbacks sole writer.

## Election lifecycle / non-cluster default (follow-up)

**Decision:** Election runs only in-cluster (detected via `KUBERNETES_SERVICE_HOST`). Off-cluster (compose/local) → election disabled, `LeaderState` defaults to **leader** so the single instance fires. In-cluster → starts follower until lease acquired.
**Notes:** User confirmed detection via `KUBERNETES_SERVICE_HOST` and the off-cluster-defaults-to-leader reconciliation with SPEC HA-05.

## Keeper → orchestrator fanout (surfaced by user)

**Investigation:** Keeper reinjects (`OrchestratorReinjectConsumer`, `InjectConsumer`) `.Send` to the stable `queue:orchestrator-result` competing-consumer queue → one replica, no duplication, unaffected by identity change. Keeper `BitHealthLoop` `bus.Publish`es `PauseAll`/`ResumeAll` by type → fans out to every replica's per-replica queue → requires unique `POD_NAME` to reach all replicas (and thus every future leader). No keeper code changes in Phase 82.

## Late-joiner L1 gap / hydration (investigated on request)

**Investigation:** `HydrationBackgroundService` bulk-hydrates the full L1 from the durable **L2 parent index** on every boot → the steady-state late-joiner gap (a replica that missed a `Start` broadcast) is **CLOSED**. Residual narrow edges (boot-window before hydration completes; PauseAll pause-state not hydrated) are deferred to Phase 83 as watch items.

## Cold-leader fire gate

| Option | Description | Selected |
|--------|-------------|----------|
| Gate fire on leader AND hydrated | Fire gate requires `IsLeader` AND hydration-complete (existing `IStartupGate` flag) — eliminates cold-leader lost-ticks | ✓ |
| Accept as HA-04 skip tolerance | Gate on IsLeader only; document as bounded edge | |
| Defer to Phase 83 | IsLeader-only; measure in the failover proof first | |

**User's choice:** Gate fire on leader AND hydrated (option 1).
**Notes:** A freshly-elected leader could hold the lease before its L1 finishes hydrating and silently skip cron fires (`WorkflowFireJob.cs:46-51`). AND-ing the existing hydration/ready flag into the gate closes this deterministically for a few lines.

## Claude's Discretion

- Exact type/file names for `LeaderState`, the election service, the enricher; `LeaderElectionConfig` wiring (timings fixed by SPEC); RBAC manifest filename + SA name; how the `KUBERNETES_SERVICE_HOST` check is surfaced.

## Deferred Ideas

- Boot-window "completed-unresolved" edge → Phase-83 watch item.
- PauseAll pause-state not hydrated → Phase-83 fault-injection edge.
- Fencing tokens, cron backfill, multi-namespace election → deferred by SPEC.
