---
phase: 82-orchestrator-ha-leader-election
reviewed: 2026-07-18T00:00:00Z
depth: standard
files_reviewed: 14
files_reviewed_list:
  - src/Orchestrator/Election/LeaderState.cs
  - src/Orchestrator/Election/LeaderElectionService.cs
  - src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs
  - src/Orchestrator/Scheduling/WorkflowFireJob.cs
  - src/Orchestrator/Program.cs
  - src/Orchestrator/Orchestrator.csproj
  - Directory.Packages.props
  - k8s/34-orchestrator-rbac.yaml
  - k8s/31-orchestrator.yaml
  - k8s/kustomization.yaml
  - tests/BaseApi.Tests/Election/LeaderStateTransitionTests.cs
  - tests/BaseApi.Tests/Observability/OrchestratorRoleEnricherTests.cs
  - tests/BaseApi.Tests/Orchestrator/WorkflowFireJobGateTests.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorTestStubs.cs
findings:
  critical: 0
  warning: 2
  info: 3
  total: 5
status: issues_found
---

# Phase 82: Code Review Report

**Reviewed:** 2026-07-18
**Depth:** standard
**Files Reviewed:** 14
**Status:** issues_found

## Summary

Phase 82 adds Kubernetes leader election to the orchestrator so exactly one of three
replicas fires the Quartz cron while followers stand by. The core invariants the phase set
out to establish are all met:

- **HA-03 single-writer / torn-read safety (LeaderState):** correct. `_snapshot` is a
  `volatile` reference and every writer publishes a *whole* new immutable `LeaderSnapshot`,
  so a cross-thread reader always observes a fully-constructed, self-consistent record. There
  is no torn read and the fire gate/enricher are read-only. The one subtlety (a non-atomic
  read-modify-write inside the writers) is safe under the documented single-serialized-writer
  invariant — see IN-01.
- **WorkflowFireJob gate:** correct. `leaderState.IsLeader && startupGate.IsReady` is snapshotted
  once at the top of the fire, wraps ONLY the `DispatchAsync` loop, and the ungated tail (L1
  liveness refresh + Quartz reschedule) still runs for followers and cold leaders. The three
  hermetic tests pin exactly this behavior.
- **In-cluster detection (Program.cs):** correct. `KUBERNETES_SERVICE_HOST` gates both the
  elector registration and the `startAsLeader: !inCluster` seed, so off-cluster/hermetic runs
  never contend for the production Lease and the lone instance still fires.
- **RBAC least-privilege (34-orchestrator-rbac.yaml):** correct and minimal — `get/create/update`
  on `leases` in `coordination.k8s.io`, namespaced to `skp`, bound to a single dedicated SA. No
  wildcards, no cluster scope, no `list/watch/delete`. This matches exactly what `LeaseLock`
  needs; no over-grant.

Two warnings concern the election `BackgroundService` resource/exception lifetime, and three
info items note a documented-but-non-atomic writer, a background-service robustness gap, and a
stale manifest count comment. No critical issues. Per the phase scope, the absence of live
election tests (Phase 83) is not flagged.

## Warnings

### WR-01: `IKubernetes` client is created but never disposed

**File:** `src/Orchestrator/Election/LeaderElectionService.cs:56`
**Issue:** `elector` is correctly scoped with `using var elector = new LeaderElector(config);`
(line 68), but the `Kubernetes` client on line 56 is assigned to a plain local and never
disposed. `Kubernetes` (via `ServiceClientCredentials`/`HttpClient`) is `IDisposable`. On
graceful shutdown `ExecuteAsync` returns after catching `OperationCanceledException`, and the
client's underlying HTTP handler/connection is left to the finalizer/GC rather than being
released deterministically. The inconsistency with the adjacent `using var elector` strongly
suggests an oversight rather than intent. Practical impact is low (single instance, process
lifetime), but it is a genuine resource-lifetime defect in exactly the area flagged for review.
**Fix:**
```csharp
using IKubernetes kubernetes = new Kubernetes(KubernetesClientConfiguration.InClusterConfig());
```

### WR-02: Only `OperationCanceledException` is caught — any other elector fault stops the host

**File:** `src/Orchestrator/Election/LeaderElectionService.cs:83-90`
**Issue:** `RunAndTryToHoldLeadershipForeverAsync` is awaited with a catch for
`OperationCanceledException` only. Any other exception (e.g. a non-transient API/auth error the
library surfaces instead of retrying) propagates out of `ExecuteAsync`, faulting the
`BackgroundService`. Under the .NET 8 default `BackgroundServiceExceptionBehavior.StopHost`,
that tears down the whole orchestrator pod — including a *follower* that was otherwise healthy
and serving bus/health traffic. In an HA design an election-thread hiccup should not
necessarily take down a working replica. The library is designed to retry transient errors
internally, so this is a defensive-robustness gap rather than a live bug, but the blast radius
(entire pod) warrants a deliberate decision.
**Fix:** Wrap the run in a broader guard that logs and either (a) exits cleanly so the host
survives as a follower, or (b) intentionally re-throws if pod-restart-on-election-failure is
the desired policy — and document which. For example:
```csharp
catch (OperationCanceledException)
{
    return; // shutdown requested — clean stop
}
catch (Exception ex)
{
    logger.LogError(ex, "Leader election terminated unexpectedly (identity {Identity})", identity);
    // Decide policy: return here to keep running as follower, or re-throw to restart the pod.
    return;
}
```

## Info

### IN-01: `LeaderState` writers do a non-atomic read-modify-write (safe only under the single-writer invariant)

**File:** `src/Orchestrator/Election/LeaderState.cs:64-83`
**Issue:** `BecomeLeader`, `BecomeFollower`, and `SetLeaderId` each read the current snapshot
and then publish a new one to preserve a field (`CurrentLeaderId` / `IsLeader` / `Role`). The
read and the write are not a single atomic operation, so *if* two writers ever ran concurrently
a lost update could occur (e.g. a concurrent `SetLeaderId` overwritten by `BecomeLeader`
carrying a stale id). The volatile whole-record swap makes each write torn-read-safe for
*readers*, which is the stated HA-03 goal; this note is strictly about *writer* atomicity. It is
correct today because the sole caller is `LeaderElectionService`'s elector callbacks, which the
KubernetesClient `LeaderElector` invokes from a single serialized loop, and because the only
field exposed to a lost update (`CurrentLeaderId`) is observational (the fire gate reads only
`IsLeader`). Worth a one-line comment or, if the single-loop guarantee is ever in doubt, an
`Interlocked.CompareExchange` retry loop.
**Fix:** No change required while the single-writer invariant holds; document the dependency on
serialized callback invocation, or harden `SetLeaderId` with a CAS loop if that assumption may
weaken.

### IN-02: Role enricher rebuilds the full attribute list on every LogRecord

**File:** `src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs:27-30`
**Issue:** `OnEnd` does `(record.Attributes ?? Array.Empty<...>()).Append(...).ToList()` on
every record, allocating a fresh `List` on the hot logging path. This is correct (no torn state,
role read live) and mirrors the `ProcessorIdLogEnricher` analog, so it is consistent by design.
Pure allocation/perf is out of v1 review scope — noted only for awareness since it runs on
"EVERY orchestrator LogRecord."
**Fix:** None required for correctness. If allocation is ever a concern, append in place when
`record.Attributes` is already a mutable `List<KeyValuePair<string, object?>>`.

### IN-03: Stale manifest count in kustomization comment

**File:** `k8s/kustomization.yaml:44`
**Issue:** The comment reads "All 13 manifests, in tier order" but the `resources:` list now
contains 14 entries — the count was not updated when `34-orchestrator-rbac.yaml` was added this
phase. Purely a documentation drift; the resource list itself is complete and correct.
**Fix:** Update the comment to "All 14 manifests, in tier order".

---

_Reviewed: 2026-07-18_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
