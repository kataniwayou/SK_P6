# SECURITY — Phase 82: Orchestrator HA Leader Election

**ASVS Level:** 1
**block_on:** high
**Threats:** 14 total — 14 CLOSED / 0 OPEN
**Audited:** 2026-07-18
**Scope note:** Phase 82 is intentionally build + hermetic + manifest only. Live leader-election / failover is Phase 83. "No live cluster test" is NOT an open threat here.

## Verdict: SECURED

All 9 `mitigate` controls verified present in shipped code; all 5 `accept` risks confirmed genuinely bounded and recorded below.

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-82-01 | EoP | mitigate | CLOSED | `LeaderElectionService.cs:52-59` — identity from `POD_NAME`/`MachineName`; `KubernetesClientConfiguration.InClusterConfig()` (mounted SA token only); `LeaseNamespace="skp"` + `LeaseName="orchestrator-leader"` are hard-coded consts (`:43,46`), no untrusted input feeds lease coordinates |
| T-82-02 | Tampering | mitigate | CLOSED | Single-writer confirmed by grep: `BecomeLeader`/`BecomeFollower`/`SetLeaderId` callers are ONLY the three election callbacks in `LeaderElectionService.cs:73,78,81`; definitions in `LeaderState.cs:64,71,79`; volatile whole-record swap (`LeaderState.cs:37`). Readers (fire job, enricher) never mutate |
| T-82-05 | EoP | mitigate | CLOSED | `WorkflowFireJob.cs:76` — `var fireEnabled = leaderState.IsLeader && startupGate.IsReady;` snapshotted once; DispatchAsync loop wrapped in `if (fireEnabled)` (`:77-98`); snapshot read-only to the job; `&& hydrated` fences the cold-leader edge |
| T-82-06 | Spoofing | mitigate | CLOSED | `Program.cs:35` — `Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName`; same identity feeds bus InstanceId (`:48-66`) and LeaseLock holder (`LeaderElectionService.cs:52`); MachineName fallback is off-cluster only |
| T-82-08 | Tampering | mitigate | CLOSED | `Program.cs:41` — `inCluster = ...KUBERNETES_SERVICE_HOST is not null`; `Program.cs:130-133` — `AddHostedService<LeaderElectionService>()` gated inside `if (inCluster)`; off-cluster/test the elector never starts |
| T-82-09 | EoP | mitigate | CLOSED | `k8s/34-orchestrator-rbac.yaml:37-41` — single namespaced (skp) Role; ONLY `apiGroups:["coordination.k8s.io"] resources:["leases"] verbs:["get","create","update"]`. No `"*"`, no ClusterRole/ClusterRoleBinding, no list/watch/delete/patch, no other resource |
| T-82-10 | Tampering | mitigate | CLOSED | `k8s/34-orchestrator-rbac.yaml:43-58` — RoleBinding `roleRef` → the leases-only Role; single `subjects` entry scoped to the `orchestrator` SA in `skp`. No other workload SA granted lease access |
| T-82-11 | Spoofing | mitigate | CLOSED | `k8s/31-orchestrator.yaml:59` — `serviceAccountName: orchestrator` (dedicated, was default); SA defined `34-orchestrator-rbac.yaml:20-27`; token kubelet-mounted, not app-supplied |
| T-82-13 | Tampering | mitigate | CLOSED | Tests reference `LeaderElectionService` ONLY for its public timing constants (`LeaderStateTransitionTests.cs:63-68`); grep of `tests/` finds no `KUBERNETES_SERVICE_HOST`, no `InClusterConfig`, no `new Kubernetes(`, no started service. Transitions driven via `LeaderState` writers directly |
| T-82-03 | DoS | accept | CLOSED | Bounded by `RetryPeriod=2s` / `RenewDeadline=10s` (`LeaderElectionService.cs:37,40`); confined to one pod's fire gate. Live behavior = Phase 83. Accepted |
| T-82-04 | Info Disclosure | accept | CLOSED | Election log lines carry only role + pod identity: `LogInformation("Acquired leadership; role=leader (identity {Identity})", identity)` (`LeaderElectionService.cs:74,79`). No secrets/tokens. Accepted |
| T-82-07 | Info Disclosure | accept | CLOSED | Enricher appends only `"role"` = `state.Role` (`OrchestratorRoleLogEnricher.cs:29`) — coarse leader/follower label, no secret content. Deliberately on every record for Phase-83 audit. Accepted |
| T-82-12 | DoS | accept | CLOSED | 3-replica lease contention bounded by fixed election timings + k8s Lease optimistic concurrency. `replicas: 3` (`31-orchestrator.yaml:41`). Live behavior = Phase 83. Accepted |
| T-82-14 | Spoofing | accept | CLOSED | `WorkflowFireJobGateTests.cs:61-71` — fully in-process `AddMassTransitTestHarness` / `UsingInMemory`; `CapturingDispatchConsumer` is the only receiver. No external message surface. Accepted |

## Unregistered Flags

None. SUMMARY.md files carry no `## Threat Flags` section; no new attack surface was declared by the executors beyond the 14 registered threats.

## Notes

- Build integrity: KubernetesClient pinned to 18.0.13 (SUMMARY 82-01 fix-forward off the vulnerable 15.0.1 / GHSA-w7r3-mgwf-4mqq that NuGetAudit promotes to a build error). Not a Phase-82 threat-register item but reinforces T-82-01 supply-chain posture.
- RelocateTail / step-advancement confirmed ungated (0 `LeaderState`/`IsLeader` references) per HA-03 — followers still advance in-flight steps.
