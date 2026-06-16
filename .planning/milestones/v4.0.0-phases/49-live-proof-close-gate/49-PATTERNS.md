# Phase 49: Live Proof & Close Gate - Pattern Map

**Mapped:** 2026-06-09
**Files analyzed:** 5 new files (3 RealStack E2E + 1 close script + 1 operator runbook)
**Analogs found:** 5 / 5 (every new file has a strong codebase analog — all named in CONTEXT canonical_refs)

> NO production code change is expected this phase (proof/close-gate only). Every analog below is **read-only source-of-truth** to COPY from, not modify. The only files written are the 5 new artifacts.

## File Classification

| New File | Role | Data Flow | Closest Analog | Match Quality |
|----------|------|-----------|----------------|---------------|
| `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` (SC1, round-trip) | test (RealStack E2E) | request-response + poll | `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` | exact |
| `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` (SC2, 4 recovery states) | test (RealStack E2E) | event-driven (direct-publish) | `MetricsRoundTripE2ETests.cs` (harness) + `RecoveryDeadLetterFacts.cs` (data-gone idiom) + recovery contracts | role-match (harness) + exact (idiom) |
| `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` (SC3, outage → pause/resume) | test (RealStack E2E, non-parallel `[Collection]`) | event-driven + process control | `SampleRoundTripE2ETests.cs` (harness) + `ResumeAllConsumerTests.cs` (TriggerState idiom) + `BitHealthLoop.cs` (edge model) | role-match + exact (idiom) |
| `scripts/phase-49-close.ps1` | config/script (close gate) | batch + transform | `scripts/phase-39-close.ps1` | exact (clone) |
| `.planning/phases/49-live-proof-close-gate/49-HUMAN-UAT.md` | doc (operator runbook) | n/a | (project convention `*-HUMAN-UAT.md`; D-03 deferred-live pattern) | role-match |

---

## Pattern Assignments

### `SC1RoundTripE2ETests.cs` (SC1 — RealStack round-trip; test, request-response+poll)

**Analog:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` — clone almost wholesale. This file ALREADY proves the Pre→In→Post round trip (dispatch consumed → output written to `skp:data:*` → orchestrator advances). SC1 is essentially a re-tag of this file under `[Trait("Phase","49")]`.

**Trait + collection attributes** (lines 69-72) — COPY EXACTLY, add the Phase-49 trait:
```csharp
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]   // hermetic filter (Category!=RealStack) excludes it
[Trait("Phase", "49")]             // ADD — canonical_refs test-runner note (tag new Phase-49 facts)
[Collection("Observability")]
public sealed class SC1RoundTripE2ETests
```

**RealStack host-override factory** (lines 373-451) — COPY the `RealStackWebAppFactory` nested class verbatim. Load-bearing pieces:
- ctor sets `RabbitMq__*` → localhost:5673, `ConnectionStrings__Redis` → `localhost:6380`, `ConnectionStrings__Postgres` → `localhost:5433`, `OTEL_EXPORTER_OTLP_ENDPOINT` → `http://localhost:4317` (lines 386-394).
- `List<RedisKey> L2KeysToCleanup` + `List<RedisValue> ParentIndexMembersToSrem` (lines 428-431).
- `DisposeAsync` net-zero drain (lines 433-450): `db.KeyDeleteAsync(L2KeysToCleanup.ToArray())` + `db.SetRemoveAsync(L2ProjectionKeys.ParentIndex(), ...)`.

**Genuine embedded SourceHash read** (lines 98-100) — the identity-loop closer; COPY:
```csharp
var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .First(a => a.Key == "SourceHash").Value!;
```

**Seed → liveness-poll → Start → output-poll core flow** (lines 104-142) — COPY:
```csharp
var procId = await SeedProcessorAsync(client, hash, ct);   // GET-or-create by source-hash (idempotent)
var stepId = await SeedStepAsync(client, procId, ct);
var wfId   = await SeedWorkflowAsync(client, new List<Guid>{stepId}, cron: "* * * * *", ct);
await PollForHealthyLivenessAsync(procId, ct);             // real container heartbeat — NO synthetic seed
var dataKeysBefore = ScanExecutionDataKeys();
var startResp = await client.PostAsJsonAsync("/api/v1/orchestration/start", new List<Guid>{wfId}, ct);
Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);
factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
factory.L2KeysToCleanup.Add($"skp:{wfId}");
factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");
var newDataKey = await PollForNewExecutionDataKeyAsync(dataKeysBefore, ct);
Assert.NotNull(newDataKey);
factory.L2KeysToCleanup.Add(newDataKey!.Value);            // net-zero: register the minted skp:data:* key
```

**Best-effort workflow-stop teardown** (lines 192-196) — COPY (stops the cron so it stops minting per-fire keys that churn the close-gate scan):
```csharp
try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid>{wfId}, ct); }
catch { /* best-effort net-zero teardown */ }
```

**Helper methods to COPY verbatim:** `PollForHealthyLivenessAsync` (199-240), `PollForNewExecutionDataKeyAsync` (244-269), `ScanExecutionDataKeys` (276-296), `SeedProcessorAsync`/`SeedStepAsync`/`SeedWorkflowAsync` (300-361), `HostRedis` const (363).

> Note: SampleRoundTripE2ETests also asserts the orchestrator-advance via Elasticsearch (lines 144-190). For SC1's success criterion ("orchestrator advances on the per-item `ExecutionResult`"), the planner should decide whether to keep the ES advance proof or assert orchestrator advance on the `OrchestratorQueues.Result` consume — but the `skp:data:*` output-write clause (a+teardown) is the load-bearing SC1 proof and is already present.

---

### `SC2RecoveryPathsE2ETests.cs` (SC2 — 4 recovery states; test, event-driven direct-publish)

**Primary harness analog:** `MetricsRoundTripE2ETests.cs` / `SampleRoundTripE2ETests.cs` `RealStackWebAppFactory` (clone for host overrides + net-zero teardown).
**Idiom analog:** `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs` (data-gone → skp-dlq-1) + the Keeper recovery production consumers for per-state effect.

**D-05 — direct-publish the state contracts to the gate-open `queue:keeper-recovery`.** The queue name is the const `KeeperQueues.Recovery` (= `"keeper-recovery"`, `src/Messaging.Contracts/KeeperQueues.cs:12`). Reference the CONST, never the literal. Publish via the bus (`factory.Services.GetRequiredService<IBus>()` / a send-endpoint to `new Uri($"queue:{KeeperQueues.Recovery}")`).

**The 5 state contracts (exact ctors + init props) — `src/Messaging.Contracts/`:**
```csharp
// KeeperReinject.cs:7-13  — REINJECT (data-present → re-inject to queue:{ProcessorId:D}; data-gone → skp-dlq-1)
public sealed record KeeperReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{ Guid CorrelationId; Guid ExecutionId; Guid EntryId; string Payload = ""; }   // EntryId + Payload extra

// KeeperInject.cs:6-10   — INJECT (reads composite backup → mints entryId → writes L2 → StepCompleted → delete)
public sealed record KeeperInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{ Guid CorrelationId; Guid ExecutionId; }

// KeeperDelete.cs:7-12   — DELETE (deletes L2 ExecutionData(EntryId))
public sealed record KeeperDelete(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{ Guid CorrelationId; Guid ExecutionId; Guid EntryId; }

// KeeperUpdate.cs:7-12   — UPDATE (re-writes validated data to L2)
public sealed record KeeperUpdate(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{ Guid CorrelationId; Guid ExecutionId; string ValidatedData = ""; }
```
(Message-construction example to mirror — `RecoveryDeadLetterFacts.cs:103-109`:)
```csharp
var msg = new KeeperReinject(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
{ CorrelationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), EntryId = Guid.NewGuid(), Payload = "step-config" };
```

**Per-state expected EFFECT to assert (from the production consumers — these define what each state does):**

| State | Production consumer (read) | L2 / re-inject / advance effect to assert |
|-------|---------------------------|-------------------------------------------|
| REINJECT data-present | `ReinjectConsumer.cs:23-49` | reads `STRLEN ExecutionData(EntryId)` > 0 → re-injects `EntryStepDispatch(wf,step,proc,Payload)` to `queue:{ProcessorId:D}` (same target as a direct dispatch). Pre-seed `skp:data:{entryId}` so STRLEN>0. |
| REINJECT data-gone | `ReinjectConsumer.cs:31-36` throws `RecoveryDataGoneException` | STRLEN==0 (absent OR empty) → terminal throw → consolidated error transport → **`skp-dlq-1`**. Assert depth on `ConsolidatedErrorTransportFilter.Dlq1`. |
| INJECT | `InjectConsumer.cs:34-64` | reads composite backup `L2ProjectionKeys.CompositeBackup(...)` → mints `entryId` → writes `skp:data:{entryId}` (no TTL) → sends reconstructed `StepCompleted` to `queue:{OrchestratorQueues.Result}` → DELETES the composite. Pre-seed the composite key. |
| DELETE | `DeleteConsumer.cs:20-21` | `Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId))` — assert the `skp:data:{entryId}` key is gone. |

**Data-gone → skp-dlq-1 assertion idiom — COPY from `RecoveryDeadLetterFacts.cs:113-119`** (adapt to RealStack: the live consolidated transport really lands it in `skp-dlq-1`, so assert the broker queue depth via `docker exec sk-rabbitmq rabbitmqctl ... list_queues name messages` OR poll the live `ConsolidatedErrorTransportFilter.Dlq1` endpoint). Reference the const:
```csharp
// NEVER the literal "skp-dlq-1":
ConsolidatedErrorTransportFilter.Dlq1   // src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs:45
```

**L2 key builders to use for seed + assert (`L2ProjectionKeys.cs`):**
```csharp
L2ProjectionKeys.ExecutionData(entryId)                         // "skp:data:{entryId:D}"  (line 42)
L2ProjectionKeys.CompositeBackup(corr, wf, proc, exec)          // "skp:{corr}:{wf}:{proc}:{exec}" (line 46) — the 2-day-TTL backstop
```

**Net-zero (D-04 + D-07):** register EVERY minted key into `factory.L2KeysToCleanup` — especially the composite backup key (its 2-day TTL CANNOT be waited out; INJECT actively deletes it, but the test must register any composite it seeds so a leak surfaces as a redis SHA mismatch). Mirror the `L2KeysToCleanup.Add(...)` discipline from SampleRoundTripE2ETests:128-142.

**Gate-open precondition:** SC2 requires the L2 health gate OPEN (healthy redis) — the recovery consumers `await gate.WaitForOpenAsync` at Consume entry (`RecoveryConsumerBase.cs:50`). In a healthy RealStack the gate is open; no action needed beyond a healthy stack (contrast SC3 which closes it).

---

### `SC3PauseResumeOutageE2ETests.cs` (SC3 — outage → pause/resume; test, non-parallel collection)

**Harness analog:** `SampleRoundTripE2ETests.cs` `RealStackWebAppFactory` (host overrides + teardown — clone).
**Idiom analog:** `tests/BaseApi.Tests/Orchestrator/Consumers/ResumeAllConsumerTests.cs` (the `TriggerState` pause/resume assertion).
**Driver model:** `src/Keeper/Health/BitHealthLoop.cs` (the edge-triggered probe → Publish(PauseAll)/Publish(ResumeAll)).

**D-02 — non-parallel collection.** This test stops `sk-redis`, which would destabilize sibling RealStack tests. Put it in its OWN `[Collection]` with parallelization disabled (NOT the shared `"Observability"` collection the other two use). Pattern: define a dedicated collection definition class with `[CollectionDefinition("...", DisableParallelization = true)]` and tag the test class with `[Collection("RedisOutageSerial")]` (name is Claude's discretion per D-02). Keep `[Trait("Category","RealStack")]` + `[Trait("Phase","49")]` so the close gate still runs it — just serialized.

**D-01 — true transient outage via docker stop/start `sk-redis`** (shell out from the test). The container name is `sk-redis` (`compose.yaml:137`). Use the `Bash`/`Process` shell-out shape:
```csharp
// docker stop sk-redis  → BitHealthLoop probe throws RedisException → gate.Close() → Publish(PauseAll)
// docker start sk-redis → probe succeeds → gate.Open() → Publish(ResumeAll)
```
The edge model to drive against — `BitHealthLoop.cs:34-49`:
```csharp
if (prevHealthy != healthy) {                 // EDGE: transition only
    if (healthy)   { gate.Open();  await bus.Publish(new ResumeAll{...}); }
    else           { gate.Close(); await bus.Publish(new PauseAll{...}); }
}
```
The probe cadence is `Probe:DelaySeconds` (`BitHealthLoop.cs:28`) — the test's stop/start wait/poll timings must straddle it (timings are Claude's discretion per D-02/D-03).

**Pause/Resume assertion idiom — COPY the `TriggerState` shape from `ResumeAllConsumerTests.cs:74-82`:**
```csharp
Assert.Equal(TriggerState.Paused, await scheduler.GetTriggerState(new TriggerKey(jobId.ToString("D")), ct));
// ... after ResumeAll ...
Assert.Equal(TriggerState.Normal, await scheduler.GetTriggerState(new TriggerKey(jobId.ToString("D")), ct));
```
> In the LIVE RealStack the orchestrator container owns the scheduler, so SC3 cannot read Quartz `TriggerState` in-process. Two assertion options for the planner: (a) drive a workflow then prove its cron fires (output lands) BEFORE outage, STOPS during pause, RESUMES after — an observable round-trip-presence proof; or (b) assert via the orchestrator's pause/resume seam logs in ES (the `PauseAllConsumer.LogWarning("Global PauseAll CorrelationId={CorrelationId}")` at `PauseAllConsumer.cs:23` / `ResumeAllConsumer.LogInformation("Global ResumeAll ...")` at `ResumeAllConsumer.cs:31`) using the `ElasticsearchTestClient.PollEsForLog` precedent from `SampleRoundTripE2ETests.cs:150-167`. The `TriggerState` idiom is the canonical assertion SHAPE to mirror; the live read mechanism is discretion.

**Control contracts (`src/Messaging.Contracts/`):** `PauseAll.cs:4` / `ResumeAll.cs:4` — both `: ICorrelated` with `Guid CorrelationId { get; init; }`. The orchestrator binds them on endpoint `"orchestrator-global-pauseresume"` (`PauseAllConsumerDefinition.cs:27`).

**D-02 blocking teardown:** the test MUST block before returning on: `docker start sk-redis` → redis healthy → steady-state re-established (the `skp:{procId:D}` liveness heartbeat re-written AND the health gate re-opened). Reuse the `PollForHealthyLivenessAsync` shape (`SampleRoundTripE2ETests.cs:199-240`) to confirm the liveness re-write. Net-zero teardown (`L2KeysToCleanup`) identical to SC1.

---

### `scripts/phase-49-close.ps1` (close gate; script, batch+transform)

**Analog:** `scripts/phase-39-close.ps1` — CLONE the entire triple-SHA protocol. D-06: keep the protocol IDENTICAL; update only the v4 namespaces / service list / single-DLQ.

**Structure to clone (phase-39-close.ps1 line map):**
1. **Header + exit codes** (1-74) — rewrite the comment for v4 (drop `keeper-dlq`, single `skp-dlq-1`, composite `corr:wf:proc:exec` + GUID data keys captured by the unfiltered scan, no composite TTL settle-wait).
2. **Pre-flight Processor-row seed** (84-167) — COPY verbatim: read genuine embedded SourceHash off `Processor.Sample.dll` via `AssemblyMetadataAttribute` reflection (104-112), GET-or-create the row over `http://localhost:8080/api/v1/processors` (117-145), wait for `processor-sample` healthy (149-167). **UPDATE the seed version string** (`version = '3.7.0'` at line 134) — D-03 discretion: verify against the live v4 `Processor.Sample` version (appsettings boot placeholder is `"3.5.0"`, `src/Processor.Sample/appsettings.json:11`; the Phase-39 `'3.7.0'` is stale).
3. **Compose-health pre-flight** (169-189) — UPDATE `$services` to the v4 list (DROP `keeper-dlq` was never here; keeper IS). **Canonical v4 service list** (verified against `compose.yaml`):
   ```powershell
   $services = @('postgres','redis','rabbitmq','otel-collector','elasticsearch','prometheus','orchestrator','processor-sample','baseapi-service','keeper')
   ```
   Keep the per-line NDJSON multi-replica parse (177-184; keeper has `replicas: 2`).
4. **BEFORE triple-SHA snapshots** (191-210) — COPY verbatim:
   ```powershell
   # psql \l   : docker compose exec -T postgres psql -U postgres -lqt | SHA256
   # redis     : docker exec sk-redis redis-cli --scan | Sort-Object -CaseSensitive | SHA256   (UNFILTERED — captures composite + GUID data keys)
   # rabbitmq  : docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Sort-Object -CaseSensitive | SHA256   (name only, NOT name messages)
   ```
   The unfiltered `redis-cli --scan` already captures the v4 composite `skp:{corr}:{wf}:{proc}:{exec}` namespace + GUID data keys `skp:data:*` — NO new prefix filter needed (D-07).
5. **Zero-warning build gate, BOTH configs** (212-225) — COPY verbatim (`dotnet clean` then `dotnet build SK_P.sln -c Release` + `-c Debug`, fatal on non-zero `$LASTEXITCODE`).
6. **3-GREEN cadence** (227-260) — COPY verbatim: N=3 (D-06), parse `Passed:\s+(\d+)` (Smell-A identical-fact-count guard, 254-258). Runner note: the test project uses Microsoft.Testing.Platform — `dotnet test ... --no-build` as phase-39 does is fine for the full suite.
7. **Settle-drain** (262-283) — phase-39 drained short-TTL `skp:flag:*`/`skp:data:*` to the BEFORE baseline. **D-07 CHANGE:** the v4 composite key has a 2-day TTL that CANNOT be waited out — do NOT add a composite TTL settle-wait. Net-zero is proven by the E2E teardown registering every composite into `L2KeysToCleanup` (+ INJECT/CLEANUP actively deleting it), so a lingering composite surfaces as a redis SHA mismatch. The planner may keep a short settle for any short-TTL `skp:data:*` round-trip keys but must NOT wait on the composite.
8. **AFTER triple-SHA snapshots** (285-303) — COPY verbatim (same three commands).
9. **Triple invariant assertions** (305-341) — COPY verbatim (psql / redis / rmq BEFORE==AFTER, exit 1 on mismatch).
10. **DLQ depth==0 assertion** (343-359) — **D-07 CHANGE:** phase-39 looped over `@('keeper-dlq','skp-dlq-1')`; v4 is SINGLE DLQ. Change to:
    ```powershell
    foreach ($q in @('skp-dlq-1')) { ... }   # ConsolidatedErrorTransportFilter.Dlq1 — sole surviving DLQ (keeper-dlq retired Phase 48)
    ```
    Keep the `list_queues name messages` depth read (kept OUT of the name-SHA per Pitfall 4).
11. **PASS summary + operator append note** (361-377) — COPY, update DLQ line to single `skp-dlq-1=0`.

**D-06 contract-change note:** the v4 wire contract is breaking — the rebuild set in the header comment must be `docker compose up -d --build baseapi-service orchestrator processor-sample keeper` (carry the phase-39 DELTA-1 note forward; v4 is a fresh breaking change per CONTEXT D-03).

---

### `49-HUMAN-UAT.md` (operator runbook; doc)

**Analog:** project convention `*-HUMAN-UAT.md` + the recurring "authored-hermetic + operator-gated-live" close pattern (D-03; Phase 39/35/36/33 all deferred the live run to an operator gate). No single source file to clone — author per the D-03 / D-06 / D-07 contract.

**Required contents (from D-03 + Claude's-Discretion line 45):**
- **Stack rebuild set** (breaking v4 contract): `docker compose up -d --build baseapi-service orchestrator processor-sample keeper` + bring up the full stack (postgres, redis, rabbitmq, otel-collector, elasticsearch, prometheus + the 4 rebuilt).
- **Gate invocation:** `pwsh -File scripts/phase-49-close.ps1`.
- **Values to record on a GREEN run** (mirror phase-39-close.ps1's operator-append line 372): the three SHA-256 values (psql `\l`, redis `--scan`, rabbitmq `list_queues`), the `Passed` fact count, and the `skp-dlq-1` depth (==0).
- **DoD gate:** TEST-01/02/03 stay UNTICKED until the operator's GREEN live N×GREEN run; record the run in this file.

---

## Shared Patterns

### Net-zero teardown registration (`L2KeysToCleanup` discipline)
**Source:** `SampleRoundTripE2ETests.cs:428-450` (`RealStackWebAppFactory.L2KeysToCleanup` / `ParentIndexMembersToSrem` + `DisposeAsync` drain).
**Apply to:** ALL THREE E2E files. Every minted key (`skp:{wfId}`, `skp:{wfId}:{stepId}`, `skp:data:{entryId}`, and for SC2 the composite `skp:{corr}:{wf}:{proc}:{exec}`) registers into `L2KeysToCleanup`; parent-index members SREM via `ParentIndexMembersToSrem`. This is what makes the close-gate redis SHA hold (a leak = SHA mismatch = exit 1).
```csharp
factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
factory.L2KeysToCleanup.Add($"skp:{wfId}");
factory.L2KeysToCleanup.Add(newDataKey!.Value);
// DisposeAsync: db.KeyDeleteAsync(L2KeysToCleanup.ToArray()); db.SetRemoveAsync(L2ProjectionKeys.ParentIndex(), ...);
```

### RealStack trait + filter discipline
**Source:** `SampleRoundTripE2ETests.cs:69-72`, CONTEXT canonical_refs test-runner note.
**Apply to:** ALL THREE E2E files. `[Trait("Category","RealStack")]` + `[Trait("Phase","49")]`. The runner is Microsoft.Testing.Platform — scope via `dotnet run --project tests/BaseApi.Tests -- --filter-trait "..."` (NOT `dotnet test --filter`). The hermetic suite excludes `Category=RealStack`.

### Reference the DLQ const, never the literal
**Source:** `src/BaseConsole.Core/Messaging/ConsolidatedErrorTransportFilter.cs:45` — `public const string Dlq1 = "skp-dlq-1";`
**Apply to:** SC2 (data-gone assertion) + the close script's DLQ-depth check (CONTEXT specifics line 109). In C#, use `ConsolidatedErrorTransportFilter.Dlq1`. The close script is PowerShell so it uses the literal `'skp-dlq-1'` — annotate it with a comment citing the const so a future rename is traceable.

### L2 key builders (single source of truth)
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`.
**Apply to:** SC1/SC2 (seed + assert) — use the builders, never hand-format key strings:
```csharp
L2ProjectionKeys.Root(wfId)                            // "skp:{wfId:D}"
L2ProjectionKeys.Step(wfId, stepId)                    // "skp:{wfId}:{stepId}"
L2ProjectionKeys.Processor(procId)                     // "skp:{procId}"  (liveness key)
L2ProjectionKeys.ExecutionData(entryId)                // "skp:data:{entryId:D}"
L2ProjectionKeys.CompositeBackup(corr,wf,proc,exec)    // "skp:{corr}:{wf}:{proc}:{exec}"  (2-day TTL)
L2ProjectionKeys.ParentIndex()                         // "skp:"  (parent-index SET key)
```

### Triple-SHA net-zero close protocol
**Source:** `scripts/phase-39-close.ps1` (whole file).
**Apply to:** `phase-49-close.ps1`. Idempotent steady-state Processor-row seed (keeps procId — hence `skp:{procId}` liveness key + `{procId}` dispatch queue — stable across all 3 runs) → compose-health pre-flight → BOTH-config 0-warning build gate → 3-GREEN identical-fact-count loop → settle-drain (NO composite TTL wait) → unfiltered triple-SHA BEFORE==AFTER → separate single-`skp-dlq-1` depth==0.

---

## No Analog Found

None. Every new file has a strong, named codebase analog (CONTEXT canonical_refs pre-pinned them). The only NEW authoring (vs. clone) is:
- The SC2 direct-publish-to-`keeper-recovery` helper (no existing RealStack direct-publish helper found — D-05 leaves "reuse existing kit vs new RealStack helper" to discretion; the message construction shape is in `RecoveryDeadLetterFacts.cs:103-109`, the harness in `SampleRoundTripE2ETests`).
- The SC3 `docker stop/start sk-redis` shell-out (no existing test shells out to docker for an outage; the wait/poll detection reuses `PollForHealthyLivenessAsync`).
- `49-HUMAN-UAT.md` content (convention-following doc, no file-clone source).

## Metadata

**Analog search scope:** `tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Keeper/`, `tests/BaseApi.Tests/Composition/`, `scripts/`, `src/Keeper/Recovery/`, `src/Keeper/Health/`, `src/Orchestrator/Consumers/`, `src/Messaging.Contracts/`, `src/BaseConsole.Core/Messaging/`, `compose.yaml`.
**Files read in full:** SampleRoundTripE2ETests.cs, MetricsRoundTripE2ETests.cs, phase-39-close.ps1, ResumeAllConsumerTests.cs, KeeperDlqConsolidationTests.cs, RecoveryDeadLetterFacts.cs, L2ProjectionKeys.cs, KeeperQueues.cs, ConsolidatedErrorTransportFilter.cs, ComposeYamlFacts.cs, the 4 Keeper{Reinject,Inject,Delete,Update} contracts, ReinjectConsumer.cs, InjectConsumer.cs, DeleteConsumer.cs, RecoveryConsumerBase.cs, BitHealthLoop.cs, PauseAllConsumer.cs, ResumeAllConsumer.cs, PauseAll.cs, PauseAllConsumerDefinition.cs, ReinjectConsumerDefinition.cs, Processor.Sample.csproj.
**Pattern extraction date:** 2026-06-09
