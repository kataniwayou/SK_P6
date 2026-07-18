# Requirements: Steps API — v10.0.0 Orchestrator High Availability

Milestone goal: run the orchestrator as N≥2 replicas with single-leader mutual exclusion so horizontal scaling never produces duplicate workflow triggers, with fast failover and role-tagged observability.

## v10.0.0 Requirements

### HA — Orchestrator High Availability

- [x] **HA-01
**: Exactly one orchestrator replica (the leader) performs scheduler-triggered entry-step sends at any instant; followers run all other logic (mint correlationId, refresh L1 liveness, reschedule, and the load-balanced `RelocateTail` step-advancement) but do not fire.
- [x] **HA-02
**: Leader election uses a `coordination.k8s.io/v1` Lease via the `KubernetesClient` `LeaderElector` (`RunAndTryToHoldLeadershipForeverAsync`) hosted in a `BackgroundService`; at startup every replica is a follower until one acquires the lease.
- [x] **HA-03
**: The gate is a single-writer volatile `LeaderState` snapshot (written ONLY by the election `OnStartedLeading`/`OnStoppedLeading` callbacks) read by `WorkflowFireJob`'s entry-step send loop; `RelocateTail`/step-advancement remains ungated so in-flight workflows never stall during an election.
- [x] **HA-04
**: On failover, the demoted leader closes its gate within `RenewDeadline` (< `LeaseDuration` — the self-demotion fence); the new leader opens its gate on acquisition; cron ticks landing in the election gap are skipped (not backfilled); a rare paused-leader duplicate is tolerated and bounded to ≈`RenewDeadline`.
- [x] **HA-05
**: Every orchestrator log carries `attributes.role = leader|follower`, added by an OpenTelemetry `LogRecord` enricher that reads the live `LeaderState` (mirrors `ProcessorIdLogEnricher`), dynamic across failover, present from the first boot log (defaults to follower).
- [x] **HA-06
**: RBAC Role + RoleBinding grant the orchestrator ServiceAccount `get/create/update` on `leases` in `coordination.k8s.io` within the `skp` namespace; the orchestrator Deployment is scaled to N≥2 replicas. `KubernetesClient` is added as a dependency.
- [ ] **HA-07**: Under N≥2 replicas with the leader killed mid-run, the system produces zero duplicate workflow triggers (verified via distinct per-fire correlationIds through the existing analyzer) and re-establishes a single leader within a bounded time.

## Future Requirements (deferred)

- Fencing tokens to fully eliminate the paused-leader duplicate window (explicitly out of scope — HA-04 tolerates it by design).
- Catch-up/backfill of cron ticks missed during the election gap (HA-04 chooses skip; backfill deferred).
- Multi-namespace / cross-cluster leader election.

## Out of Scope

- Changing the fanout vs load-balance messaging topology — already correct and unchanged by this milestone.
- Making duplicate fires idempotent via a deterministic per-`(workflowId, fire-time)` correlationId (option C from design) — deferred; leader election is the chosen mechanism.
- Gating any send other than the scheduler-fire (`WorkflowFireJob`) — step-advancement stays ungated by design.

## Traceability

| REQ-ID | Phase | Status |
|--------|-------|--------|
| HA-01 | 82 | Pending |
| HA-02 | 82 | Pending |
| HA-03 | 82 | Pending |
| HA-04 | 82 | Pending |
| HA-05 | 82 | Pending |
| HA-06 | 82 | Pending |
| HA-07 | 83 | Pending |
