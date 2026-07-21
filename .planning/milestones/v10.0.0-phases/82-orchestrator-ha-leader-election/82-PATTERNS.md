# Phase 82: Orchestrator HA — Leader Election - Pattern Map

**Mapped:** 2026-07-18
**Files analyzed:** 9 (5 new, 4 modified) + 3 hermetic test files
**Analogs found:** 11 / 12 (one file — RBAC manifest — has no in-repo analog)

All CONTEXT.md `file:line` references were re-verified against the live codebase. Corrections are noted inline (e.g. the `Program.cs` fanout-endpoint span drifted from the CONTEXT-stated `28-70` to the actual `32-74`). Every other cited line held.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| **NEW** `src/Orchestrator/*/LeaderState.cs` | model (immutable snapshot) | volatile single-writer state | `src/Messaging.Contracts/Projections/LivenessProjection.cs` + swap idiom `WorkflowFireJob.cs:91-95` | role+flow exact |
| **NEW** `src/Orchestrator/*/LeaderElectionService.cs` | hosted service | event-driven (callbacks) | `src/Orchestrator/Hydration/HydrationBackgroundService.cs` | role exact, flow role-match |
| **NEW** `src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs` | provider (OTel processor) | transform (per-record enrich) | `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs` | exact (near-clone) |
| **NEW** `k8s/34-orchestrator-rbac.yaml` | config (RBAC manifest) | n/a (declarative) | **none in repo** — see "No Analog Found" | no analog |
| **NEW** test: gate predicate | test | request-response (job exec) | `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs` | exact |
| **NEW** test: LeaderState transitions | test | state-transition | `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` (stub-driven) | role-match |
| **NEW** test: role enricher | test | transform | `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` | exact (near-clone) |
| **MODIFY** `src/Orchestrator/Scheduling/WorkflowFireJob.cs` | service (Quartz job) | request-response | self (gate the existing `foreach` at 69-87) | in-place |
| **MODIFY** `src/Orchestrator/Program.cs` | config (composition root) | DI wiring | self (mirror existing hosted-service + enricher-registration seams) | in-place |
| **MODIFY** `k8s/31-orchestrator.yaml` | config (Deployment) | declarative | self | in-place |
| **MODIFY** `k8s/kustomization.yaml` | config (aggregator) | declarative | self (append to `resources`) | in-place |
| **MODIFY** `src/Orchestrator/Orchestrator.csproj` | config (project) | n/a | self (add `KubernetesClient` PackageReference) | in-place |

---

## Pattern Assignments

### NEW `LeaderState` (model, immutable volatile snapshot)

**Analog A — record shape:** `src/Messaging.Contracts/Projections/LivenessProjection.cs:11-14`
```csharp
public sealed record LivenessProjection(
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("interval")]  int Interval,
    [property: JsonPropertyName("status")]    string Status);
```
`LeaderState` mirrors this `sealed record` positional shape. Fields per SPEC HA-03 / D-07: `IsLeader` (bool) / `Role` (string) / `CurrentLeaderId` (string). No `[JsonPropertyName]` needed — `LeaderState` is never L2-serialized (it is a live in-process snapshot, unlike `LivenessProjection`). Default/unstarted value MUST be follower per SPEC HA-02 acceptance: `IsLeader == false`, `Role == "follower"`.

**Analog B — the immutable-`with`-swap idiom** to mirror for the volatile-reference swap: `src/Orchestrator/Scheduling/WorkflowFireJob.cs:91-95` (CONTEXT ref confirmed exact):
```csharp
var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
var current = wf.Liveness;
wf.Liveness = current is null
    ? new LivenessProjection(nowUtc, 0, "active")
    : current with { Timestamp = nowUtc };
```
D-07: `LeaderState` is a DI **singleton** holding a `volatile` reference to the immutable record; the writer swaps a whole new record (torn-read-safe, lock-free) exactly as `wf.Liveness` is replaced here. The holder class exposes the current snapshot for readers (`WorkflowFireJob`, the enricher) and a writer surface used ONLY by the election callbacks (D-06 / HA-03 single-writer). Note the analog mutates a field on `WorkflowL1`; `LeaderState` instead wraps its record behind a `volatile` field on a singleton so concurrent readers see a consistent snapshot.

---

### NEW `LeaderElectionService` (hosted BackgroundService, event-driven)

**Analog:** `src/Orchestrator/Hydration/HydrationBackgroundService.cs` — the existing orchestrator `BackgroundService` shape (primary-ctor DI, `sealed`, `ExecuteAsync` override, bounded-backoff `while (!stoppingToken.IsCancellationRequested)` loop).

**Class + DI shape** (lines 24-33):
```csharp
public sealed class HydrationBackgroundService(
    IConnectionMultiplexer redis,
    WorkflowLifecycle lifecycle,
    IStartupGate gate,
    ILogger<HydrationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
```
`LeaderElectionService` mirrors this: primary-ctor DI of the `LeaderState` holder + `ILogger<LeaderElectionService>` (+ the `IKubernetes` client it constructs the `LeaseLock`/`LeaderElector` from). Per SPEC HA-02 the body constructs a `LeaderElector` with `LeaseLock(IKubernetes, "skp", "orchestrator-leader", identity)` and runs `RunAndTryToHoldLeadershipForeverAsync(stoppingToken)`; timings fixed by SPEC/D-specifics: `LeaseDuration 15s / RenewDeadline 10s / RetryPeriod 2s` (do NOT invert `RenewDeadline < LeaseDuration` — it is the self-demotion fence, HA-04).

**Cancellation-aware shutdown** to preserve (lines 76-79):
```csharp
catch (OperationCanceledException)
{
    return; // shutdown requested mid-backoff
}
```

**Single-writer callbacks (D-06 / HA-03):** the `LeaderElector` `OnStartedLeading` / `OnStoppedLeading` / `OnNewLeader` callbacks are the SOLE writer of the `LeaderState` singleton — `OnStartedLeading` → leader, `OnStoppedLeading` → follower (HA-04 gate-close), `OnNewLeader` → update `CurrentLeaderId`. These callbacks are the test seam D-06 exercises directly (no `IKubernetes` stub).

**In-cluster-only gating (D-02):** this service is registered/started ONLY when `Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST")` is present (no existing usage in the repo — new check; Claude's discretion whether helper or inline). Off-cluster (compose/hermetic), the service never runs and `LeaderState` stays at its leader-by-default off-cluster value so the lone instance fires (reconcile with HA-05: follower-default is the *in-cluster pre-acquisition* state).

---

### NEW `OrchestratorRoleLogEnricher` (OTel LogRecord processor, transform)

**Analog:** `src/BaseProcessor.Core/Observability/ProcessorIdLogEnricher.cs:16-25` — near-exact clone (swap `IProcessorContext.Id` for the live `LeaderState.Role`, key `"role"`):
```csharp
public sealed class ProcessorIdLogEnricher(IProcessorContext context) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record)
    {
        if (context.Id is not { } id) return;
        record.Attributes = (record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
            .Append(new KeyValuePair<string, object?>(ExecutionLogScope.ProcessorId, id.ToString()))
            .ToList();
    }
}
```
`OrchestratorRoleLogEnricher(LeaderState state) : BaseProcessor<LogRecord>` reads `state.Role` (live snapshot). **Key difference from the analog:** the null-guard early-return does NOT apply — per SPEC HA-05 / D-09 the role is NEVER empty (defaults `"follower"`), so the enricher always appends. Attribute key is the **lowercase literal `"role"`** (D-09, SPEC) — NOT a keyword-mapped `ExecutionLogScope.*` const (those are PascalCase `"ProcessorId"`/`"WorkflowId"`, `src/Messaging.Contracts/ExecutionLogScope.cs:12,14`). Reassign `Attributes` only (safe on OTel 1.15.3 — the v1.5-v1.7 State-desync bug does not apply, per SPEC constraint). Reads live state so a failover flip shows on the next record.

**DI registration analog:** `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:172-174` (CONTEXT ref confirmed exact):
```csharp
services.AddSingleton<ProcessorIdLogEnricher>();
services.ConfigureOpenTelemetryLoggerProvider((sp, lp) =>
    lp.AddProcessor(sp.GetRequiredService<ProcessorIdLogEnricher>()));
```
Add the identical DI-resolved pair to `Program.cs` (so the enricher resolves the singleton `LeaderState`). The instance `AddProcessor` overload cannot resolve DI — use the `(sp, lp) => lp.AddProcessor(sp.GetRequiredService<...>())` seam, exactly as the analog does. This is additive onto the shared logger provider that `AddBaseConsoleObservability` (`Program.cs:23`) already built.

---

### NEW `k8s/34-orchestrator-rbac.yaml` (RBAC manifest) — NO in-repo analog

No `ServiceAccount`, `Role`, `RoleBinding`, or `ClusterRole` exists anywhere under `k8s/` (grep for `kind: ServiceAccount|Role|RoleBinding|ClusterRole` returned zero matches — the orchestrator currently runs under the `default` SA, confirming D-08). Planner must author this from the k8s API spec, not a codebase analog. Requirements (SPEC HA-06 / D-08):
- `ServiceAccount` `<name>` in `namespace: skp`.
- `Role` in `coordination.k8s.io` granting verbs `get,create,update` on resource `leases`, in `skp`.
- `RoleBinding` binding the SA to the Role.
- Mirror the house manifest conventions visible in `k8s/31-orchestrator.yaml`: `namespace: skp` hardcoded on every object; `app.kubernetes.io/part-of: skp` label; a leading `# ===`-ruled header comment block explaining intent.

---

### MODIFY `src/Orchestrator/Scheduling/WorkflowFireJob.cs` (gate the dispatch loop)

**The exact loop to gate** (lines 69-87, CONTEXT ref confirmed exact):
```csharp
foreach (var entryStepId in wf.EntryStepIds)
{
    if (!wf.Steps.TryGetValue(entryStepId, out var step))
    {
        logger.LogWarning(
            "Entry step {StepId} of workflow {WorkflowId} missing from L1 steps — skipping (business)",
            entryStepId, workflowId);
        continue;
    }
    await dispatcher.DispatchAsync(
        workflowId, entryStepId, step.ProcessorId, step.Payload,
        correlationId, Guid.Empty, Guid.Empty, context.CancellationToken);
}
```
**D-04:** snapshot `IsLeader` ONCE at the top of the fire (inject the `LeaderState` singleton + read the `IStartupGate` ready flag), gate ONLY this `foreach`. **D-05:** the gate predicate is `IsLeader && hydrated` (read the existing readiness signal — `IStartupGate` / `HydrationBackgroundService.MarkReady`, `HydrationBackgroundService.cs:64` — NO new hydration plumbing). A follower (or un-hydrated leader) skips the loop and emits ONE info log:
`"Follower — leader gate closed; skipping entry-step sends for {WorkflowId}"` — `WorkflowId` in the **scope**, not the template (T-18-04; the scope is already open at line 63-67). The remainder (L1 liveness refresh + `RescheduleAsync`, lines 89-101) stays UNGATED and byte-unchanged for all replicas (SPEC HA-01 acceptance). The two early returns (46-51 workflow-absent, 42-44 unparseable) fire before the mint and are NOT touched.

**Constructor to extend** (lines 30-35) — add the `LeaderState` singleton + the readiness gate to the existing primary-ctor DI list:
```csharp
public sealed class WorkflowFireJob(
    IWorkflowL1Store store,
    IStepDispatcher dispatcher,
    WorkflowScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<WorkflowFireJob> logger) : IJob
```

---

### MODIFY `src/Orchestrator/Program.cs` (register the three new singletons + POD_NAME identity)

**InstanceId resolution to change (D-01)** — line 30 (CONTEXT ref confirmed exact):
```csharp
var instanceId = builder.Configuration["Orchestrator:InstanceId"] ?? Guid.NewGuid().ToString("N");
```
Change the fallback chain to `POD_NAME` → `Environment.MachineName` (D-01: the hardcoded `Orchestrator__InstanceId` env is dropped from the manifest, so at N>1 each pod gets a distinct downward-API `POD_NAME` → distinct `orchestrator-{instanceId}` fanout queue → true broadcast). This same identity feeds the `LeaseLock` holder identity.

**Fanout vs competing-consumer endpoints** — `Program.cs:32-74` (CONTEXT stated `28-70`; **corrected** to the actual span). Unchanged topology; only the per-pod `instanceId` uniqueness matters. The `.Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; })` fanout endpoints (lines 36-54) are the broadcast surface D-01 protects; the stable `orchestrator-result` competing-consumers (lines 61-70) are unchanged.

**Hosted-service + additive-provider registration seams to mirror** — lines 101-103:
```csharp
builder.Services.AddSingleton<OrchestratorMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName));
builder.Services.AddHostedService<HydrationBackgroundService>();           // D-13 — drives MarkReady (D-12)
```
Add here (D-06 / D-09):
1. `AddSingleton<LeaderState>()` (the volatile-snapshot holder).
2. `OrchestratorRoleLogEnricher` via the `AddSingleton` + `ConfigureOpenTelemetryLoggerProvider((sp, lp) => lp.AddProcessor(...))` pair (the `ProcessorIdLogEnricher` registration analog above) — this is the logger-provider twin of the existing `ConfigureOpenTelemetryMeterProvider` line 102.
3. `AddHostedService<LeaderElectionService>()` — **gated on `KUBERNETES_SERVICE_HOST`** (D-02: only registered/started in-cluster; hermetic tests never start it, D-06).

---

### MODIFY `k8s/31-orchestrator.yaml` (replicas 3, RollingUpdate, POD_NAME, SA, drop InstanceId)

Current load-bearing lines to change:
- **line 35** `replicas: 1` → `replicas: 3` (D-03, SPEC HA-06).
- **lines 44-45** `strategy: type: Recreate` → `RollingUpdate` (default 25% surge/unavailable) (D-03).
- **lines 73-74** — DROP the `Orchestrator__InstanceId: orchestrator-1` env entirely (D-01).
- **env block** — ADD a `POD_NAME` downward-API env: `valueFrom.fieldRef.fieldPath: metadata.name` (D-01, "Specific Ideas").
- **pod spec** — ADD `serviceAccountName: <name>` referencing the new RBAC SA (D-08).
- **header comments** — REWRITE the stale SPOF / exclusive-queue / "replicas LOCKED at 1" prose (lines 1-9, 35-45, 73) — they now contradict `replicas: 3` (D-03). The current text explicitly says `replicas: 1 is LOCKED` and describes the `Recreate`/exclusive-queue rationale that this design dismisses.

---

### MODIFY `k8s/kustomization.yaml` (register the RBAC manifest)

Append the new file to the `resources:` list (lines 46-59). Current tail:
```yaml
resources:
  - 00-namespace.yaml
  ...
  - 31-orchestrator.yaml
  - 32-keeper.yaml
  - 33-processor-sample.yaml
```
Add `- 34-orchestrator-rbac.yaml` (D-08). Kind-sort at apply time places RBAC before the Deployment regardless of list order, but list it in tier order for human diff-ability (the file's own stated convention, header lines 44-45).

---

### MODIFY `src/Orchestrator/Orchestrator.csproj` (add KubernetesClient)

Add `<PackageReference Include="KubernetesClient" />` to the existing `<ItemGroup>` at lines 34-43 (alongside `MassTransit`, `Quartz.Extensions.Hosting`, `Cronos`). **NO `Version=` attribute** — this repo uses Central Package Management (CPM); the version must be pinned in `Directory.Packages.props` (SPEC HA-06 requires the new dependency; `KubernetesClient` is currently absent everywhere except planning docs — grep confirmed no source reference exists yet).

---

### NEW hermetic tests (3 — under `tests/BaseApi.Tests/`)

Single test project: `tests/BaseApi.Tests/BaseApi.Tests.csproj`. Namespace convention `BaseApi.Tests.<Area>`; `sealed class ...Tests`, xUnit `[Fact]`, `TestContext.Current.CancellationToken`.

**Test 1 — gate predicate.** Analog: `tests/BaseApi.Tests/Orchestrator/WorkflowFireJobScopeTests.cs` (drives the REAL `WorkflowFireJob.Execute` with a real `WorkflowL1Store`, an in-memory MassTransit test harness behind `StepDispatcher`, `FakeTimeProvider`, `Substitute.For<IJobExecutionContext>()`, and a `CapturingDispatchConsumer`). Reuse its `BuildHarness` / `SeedEntry` / `FireContext` helpers wholesale. Per SPEC HA-01 acceptance: construct the job with `LeaderState` = follower → assert ZERO `DispatchAsync` (the harness `CapturingDispatchConsumer` receives nothing) AND the L1 liveness refresh + `RescheduleAsync` STILL ran; with `LeaderState` = leader → assert the sends fire. The existing test at line 146-149 shows exactly how to construct + `Execute` the job:
```csharp
var job = new WorkflowFireJob(
    store, new StepDispatcher(harness.Bus, OrchestratorTestStubs.Metrics()), workflowScheduler, fakeTime, logger);
await job.Execute(FireContext(workflowId, ct));
```
(extend the ctor call with the `LeaderState` + readiness-gate args once added).

**Test 2 — LeaderState single-writer transitions.** Analog: the stub-driven style of `ProcessorIdEnricherTests` (below) + D-06 seam. Drive the election callbacks DIRECTLY (`OnStartedLeading` / `OnStoppedLeading` / `OnNewLeader`) — NO `IKubernetes` stub, NO started election service. Assert: default/unstarted snapshot is follower (`IsLeader == false`, `Role == "follower"`, SPEC HA-02); `OnStartedLeading` → leader; `OnStoppedLeading` → follower (SPEC HA-04); the `RenewDeadline(10s) < LeaseDuration(15s)` config constants are present (SPEC HA-04 acceptance).

**Test 3 — role enricher.** Analog: `tests/BaseApi.Tests/Observability/ProcessorIdEnricherTests.cs` — near-clone. Reuse its `CapturingProcessor : BaseProcessor<LogRecord>` and its `EmitOneLog` real-OTel-provider harness verbatim:
```csharp
using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
{
    o.IncludeScopes = true;
    o.ParseStateValues = true;
    o.AddProcessor(new OrchestratorRoleLogEnricher(leaderState));  // SUT — enriches first
    o.AddProcessor(capture);                                       // then capture observes
}));
factory.CreateLogger("test").LogInformation("enricher probe");
```
Swap the `StubContext`-drives-`Id` seam for a `LeaderState` set to follower vs leader. Per SPEC HA-05: assert `attributes.role == "follower"` when follower and `"leader"` when leader, and (unlike the ProcessorId Case-B) that it is NEVER empty. Assert the live-read property: flip `LeaderState` after construction and confirm the NEXT record reflects the new role.

---

## Shared Patterns

### Single-writer volatile snapshot (LeaderState core invariant)
**Source idiom:** `WorkflowFireJob.cs:91-95` (immutable `with`-swap).
**Apply to:** `LeaderState` holder (written only by election callbacks, read by `WorkflowFireJob` + enricher).
Whole-record swap behind a `volatile` reference — no locks, no second writer (SPEC HA-03). Grep-verifiable single writer: the assignment appears ONLY in the election-callback wiring; `RelocateTail.cs` and the step-advancement path contain NO `LeaderState`/`IsLeader` reference (SPEC HA-03 acceptance).

### Additive OTel provider registration (DI-resolved)
**Source:** `BaseProcessorServiceCollectionExtensions.cs:172-174` (logger provider) mirrored by `Program.cs:102` (meter provider).
**Apply to:** the role enricher registration in `Program.cs`.
`AddSingleton<T>()` then `ConfigureOpenTelemetry{Logger|Meter}Provider((sp, p) => p.Add...(sp.GetRequiredService<T>()))` — the ONLY way to attach a DI-dependent processor (the instance overload cannot resolve DI). Purely additive; the shared `AddBaseConsoleObservability` block keeps ownership of `IncludeScopes`/`ParseStateValues`/OTLP.

### ids-in-scope-not-template logging (T-18-04)
**Source:** `WorkflowFireJob.cs:63-67` (the open scope) and the house convention.
**Apply to:** the new follower-skip log — `WorkflowId` goes in the already-open scope dictionary, never in the message template.

### BackgroundService cancellation discipline
**Source:** `HydrationBackgroundService.cs:76-79`.
**Apply to:** `LeaderElectionService` — honor `stoppingToken`; catch `OperationCanceledException` on shutdown and return cleanly.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `k8s/34-orchestrator-rbac.yaml` | config (RBAC) | declarative | No `ServiceAccount`/`Role`/`RoleBinding`/`ClusterRole` exists anywhere under `k8s/` (orchestrator runs under `default` SA today). Author from the k8s API spec per SPEC HA-06 / D-08; mirror only the house manifest conventions (hardcoded `namespace: skp`, part-of label, ruled header comment) visible in `k8s/31-orchestrator.yaml`. |

Partial-analog note: `LeaderElectionService`'s **shape** is well-covered by `HydrationBackgroundService`, but the `KubernetesClient` `LeaderElector` / `LeaseLock` API itself has NO in-repo precedent (first use of `KubernetesClient`). The election-body specifics come from SPEC HA-02 + the ROADMAP source-of-truth design conversation, not a codebase analog. D-06 makes this wiring build-only in Phase 82 (proven live in Phase 83).

---

## Metadata

**Analog search scope:** `src/Orchestrator/`, `src/BaseProcessor.Core/`, `src/Messaging.Contracts/`, `k8s/`, `tests/BaseApi.Tests/`
**Files scanned/read:** 12 (2 CONTEXT/SPEC + 10 source/test/manifest analogs)
**Line-reference verification:** all CONTEXT.md refs re-checked live; one correction (`Program.cs` fanout span `28-70` → `32-74`); all others confirmed exact.
**Pattern extraction date:** 2026-07-18
