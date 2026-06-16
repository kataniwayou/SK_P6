# Phase 55: Live Proof & Close Gate - Pattern Map

**Mapped:** 2026-06-12
**Files analyzed:** 6 (2 CREATE, 3 MODIFY, 1 cross-cutting DELETE)
**Analogs found:** 6 / 6 (this phase is a v4→v5 ADAPTATION — every target has a direct in-repo analog)

> All six targets have an exact or near-exact analog. There is **no "No Analog Found" section** — the phase
> clones proven Phase-49 infrastructure. The planner's job per file is "copy the analog, apply the cited v5 delta",
> NOT "derive a pattern". The RESEARCH.md "Sources" / "Landmines" line citations are the authoritative map; the
> excerpts below are the real text to copy.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `scripts/phase-55-close.ps1` (CREATE) | config / operator-script | batch (snapshot→build→cadence→compare) | `scripts/phase-49-close.ps1` | exact (verbatim clone + 3 deltas) |
| `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` (MODIFY) | test (RealStack E2E) | request-response (forward round-trip) | itself (in-place) / `SC1...` body | exact (in-place adapt) |
| `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` (MODIFY/REWRITE) | test (RealStack E2E) | event-driven (direct-publish per-state) + recovery | itself (in-place) | role+flow match (rewrite STATE 4 + add organic test) |
| `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` (MODIFY) | test (RealStack E2E) | event-driven (BIT edge pause/resume) | itself (in-place) | exact (retag-only + delete dead block) |
| `.planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md` (CREATE) | doc (operator runbook) | n/a | `.planning/phases/49-live-proof-close-gate/49-HUMAN-UAT.md` | exact (structure clone) |
| Composite-sweep teardown block (DELETE ×3) | test (factory `DisposeAsync`) | n/a (dead code removal) | `SC1...:437-448` / `SC2...:462-473` / `SC3...:597-608` | exact (identical block in 3 files) |

---

## Pattern Assignments

### `scripts/phase-55-close.ps1` (config/operator-script, batch) — CREATE

**Analog:** `scripts/phase-49-close.ps1` (clone the ENTIRE file verbatim; apply only the three deltas below).

**Idempotent Processor-row seed** (`phase-49-close.ps1:139-154`) — keep verbatim, version stays `'3.5.0'` (Landmine 1):
```powershell
# v4 seed version: the verified live Processor.Sample value (src/Processor.Sample/appsettings.json:11
# => "Version": "3.5.0").
$body = @{
    name           = 'processor-sample'
    version        = '3.5.0'
    description    = 'Phase 49 close-gate steady-state Processor.Sample row (genuine embedded hash, schema-less).'
    sourceHash     = $sourceHash
    inputSchemaId  = $null
    outputSchemaId = $null
    configSchemaId = $null
} | ConvertTo-Json
```
> v5 change: only the `description` text (`'Phase 49 ...'` → `'Phase 55 ...'`). The `version = '3.5.0'` is
> UNCHANGED (Landmine 1: do NOT invent a `'5.0.0'` seed string — the row is keyed by `uq_processor_source_hash`,
> and the SourceHash is what changed v4→v5). The genuine-hash reflection block (`:98-119`) is unchanged.

**Steady-state exclusions in the triple-SHA** (`phase-49-close.ps1:209` + `:215`) — keep VERBATIM (Landmine 5, Pitfall 4/5):
```powershell
# redis: unfiltered --scan; the ONLY excluded skp: key is the liveness heartbeat (Pitfall 5, GAP-49-10)
$beforeRedis = (docker exec sk-redis redis-cli --scan | Where-Object { $_ -ne "skp:$($procId.ToString().ToLower())" } | Sort-Object -CaseSensitive | Out-String).Trim()
# rabbitmq: name-only SHA, transient _bus_ auto-delete queues excluded (Pitfall 4, GAP-49-9)
$beforeRmq = (docker exec sk-rabbitmq rabbitmqctl -q list_queues name | Where-Object { $_ -notmatch '_bus_' } | Sort-Object -CaseSensitive | Out-String).Trim()
```
> v5 NOTE (D-06b, Landmine 5): the unfiltered `--scan` already folds `skp:data:*` AND `skp:msg:*` into the SHA —
> **no scan-pattern change**. Keep `list_queues name` (NOT `name messages`) for the SHA input.

**Separate single-DLQ depth==0 additive check** (`phase-49-close.ps1:373-383`) — keep VERBATIM; this is the SHAPE the new `skp:msg:*` count check parallels:
```powershell
$depthRaw = docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages | Out-String
foreach ($q in @('skp-dlq-1')) {   # ConsolidatedErrorTransportFilter.Dlq1 — sole surviving DLQ (Phase 53)
    $line  = ($depthRaw -split "`n" | Where-Object { $_ -match "^\s*$([regex]::Escape($q))\s+\d+\s*$" })
    $depth = if ($line -match "\s+(\d+)\s*$") { [int]$Matches[1] } else { -1 }
    if ($depth -ne 0) { Write-Host "DLQ depth invariant VIOLATED: $q depth=$depth (expected 0)" -ForegroundColor Red; $allGood = $false }
    else              { Write-Host "DLQ depth invariant HELD: $q depth=0" -ForegroundColor Green }
}
```

**v5 DELTA D-06a — DELETE the composite settle-GC block** (`phase-49-close.ps1:272-306`, the `Settle:` loop) and the composite mentions in the redis-mismatch error text (`:344-347`, the four `Write-Host` lines naming `skp:{corr}:{wf}:{proc}:{exec}` / "2-day TTL"). Model-B is retired (Phases 50/53). The header comment block (`:18-30`, `:44-50`, `:280-288`) describing the composite namespace is also dropped/rewritten. **Do NOT replace the settle loop with a `skp:msg:*` settle-wait** (Anti-Pattern / Pitfall 2 — the 300/600s TTL "cannot be waited out"); net-zero is proven by the A19 active two-key DELETE, asserted additively below.

**v5 DELTA D-06c — ADD an additive `skp:msg:*` count==0 block** (insert AFTER the `skp-dlq-1` loop above, before `if (-not $allGood)` at `:385`), parallel to the DLQ check:
```powershell
# D-06c: additive A19 active-reclaim assertion — parallel to the skp-dlq-1 depth==0 check above.
# Net-zero of the slot-array index is a PRODUCTION property (the two-key DEL in ProcessorPipeline.DeleteTerminalAsync
# :310-315 + DeleteConsumer.cs:19-24), NOT a TTL settle (the 300/600s SlotArrayOptions TTL cannot be waited out).
# A lingering index surfaces here as count>0 AND as a redis SHA mismatch — never a silent TTL pass.
$msgCount = (docker exec sk-redis redis-cli --scan --pattern 'skp:msg:*' | Measure-Object).Count
if ($msgCount -ne 0) {
    Write-Host "skp:msg:* count invariant VIOLATED: $msgCount (expected 0 — A19 active reclaim leaked an index)" -ForegroundColor Red
    $allGood = $false
} else {
    Write-Host "skp:msg:* count invariant HELD: 0 (A19 active two-key DEL reclaimed every index)" -ForegroundColor Green
}
```

**UNCHANGED in the clone (verify, don't edit):** the service list (`:181` already includes `keeper`), the BOTH-config 0-warning build gate (`:226-235`), the N=3 identical-fact-count cadence + Smell-A guard (`:238-270`), the full-suite no-filter `dotnet test` (`:242`, Pitfall 1 — the gate runs everything live; the retag does not affect it), the compose-health pre-flight (`:178-198`, exit 2). Retitle headers/exit-code comments `Phase 49`→`Phase 55` and update the final operator-append path (`:396`) to the `55-HUMAN-UAT.md`.

---

### `tests/BaseApi.Tests/Orchestrator/SC1RoundTripE2ETests.cs` (test, request-response) — MODIFY in place

**Analog:** the existing SC1 body (forward round-trip) + the `ScanExecutionDataKeys` / `PollForNewExecutionDataKeyAsync` precedent.

**Retag** (`SC1...:62`): `[Trait("Phase","49")]` → `[Trait("Phase","55")]` (D-02). Keep `Category=E2E` + `Category=RealStack` + `[Collection("Observability")]`.

**Existing forward-output poll to mirror for the new index assertion** (`SC1...:107-132`):
```csharp
// existing skp:data:* snapshot-before / poll-for-new idiom — the model for the NEW skp:msg:* assertion
var dataKeysBefore = ScanExecutionDataKeys();
// ... POST /api/v1/orchestration/start ...
var newDataKey = await PollForNewExecutionDataKeyAsync(dataKeysBefore, ct);
Assert.NotNull(newDataKey);
factory.L2KeysToCleanup.Add(newDataKey!.Value);   // net-zero: register the minted skp:data:* key
```

**Existing scan helper to clone for `skp:msg:*`** (`SC1...:266-286`):
```csharp
private static HashSet<string> ScanExecutionDataKeys()
{
    using var mux = ConnectionMultiplexer.Connect(HostRedis);
    var keys = new HashSet<string>(StringComparer.Ordinal);
    foreach (var ep in mux.GetEndPoints())
    {
        var server = mux.GetServer(ep);
        if (!server.IsConnected || server.IsReplica) continue;
        foreach (var key in server.Keys(pattern: $"{L2ProjectionKeys.Prefix}data:*"))
            keys.Add(key.ToString());
    }
    return keys;
}
```
> **v5 ADD (D-01):** add a sibling `ScanMessageIndexKeys()` scanning `pattern: $"{L2ProjectionKeys.Prefix}msg:*"`
> (= `L2ProjectionKeys.MessageIndex`, `L2ProjectionKeys.cs:48` = `skp:msg:{messageId:D}`, a Redis HASH). Snapshot
> `msgKeysBefore` before Start; assert a NEW `skp:msg:*` HASH appears, ALLOCATION-BEFORE-DATA. The production order
> is index-FIRST then data (`ProcessorPipeline.cs:261-275` — `HashSetAsync` slot at `:262` precedes `StringSetAsync`
> data at `:275`). Register the minted `skp:msg:*` key into `factory.L2KeysToCleanup` (D-07 belt-and-suspenders).

**v5 ADD (A19 two-key net-zero at end-of-message):** after the orchestrator-advance ES proof, poll until BOTH the
fresh `skp:data:*` AND the fresh `skp:msg:*` are GONE (the production `DeleteTerminalAsync` two-key `DEL`,
`ProcessorPipeline.cs:310-315`). Reuse SC2's `PollForKeyAbsentAsync` shape (see below) — the net-zero is active, not a TTL race.

**Production order under proof** (`ProcessorPipeline.cs:259-275`, allocation-before-data):
```csharp
var entryId = NewId.NextGuid();                     // (1) allocate
var alloc = await RetryLoop.ExecuteAsync(           // (2) ALLOCATION INDEX FIRST (SLOT-01)
    () => db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), slot, entryId.ToString("D")), limit, ct);
// ...
var write = await RetryLoop.ExecuteAsync(           // (3) DATA SECOND (SLOT-02)
    () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data, executionDataTtl), limit, ct);
```

**DELETE the dead composite-sweep block** (`SC1...:437-448`) — see "Shared Patterns → Dead composite-sweep removal".

---

### `tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs` (test, event-driven + recovery) — REWRITE/EXTEND

**Analog:** the existing SC2 direct-publish per-state body (`SC2...:66-220`) + the `IBus` / `GetSendEndpoint` idiom.

**Retag** (`SC2...:58`): `Phase=49`→`Phase=55`. **Drop the "FOUR states"/"AllFour" naming** (`SC2...:13`, `:67`) — v5 is 3-state (`UPDATE`/`CLEANUP` retired Phase 53).

**Direct-publish shape to REUSE for every state** (`SC2...:79-106`):
```csharp
var bus = factory.Services.GetRequiredService<IBus>();
var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));   // const, never literal
await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
var db = mux.GetDatabase();
// ... STATE n: pre-seed L2, Send the contract, assert the deterministic effect ...
await endpoint.Send(new KeeperReinject(wfId, stepId, procId)
{
    CorrelationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), EntryId = entryId, Payload = "step-config",
}, ct);
```

**STATE 1 REINJECT data-present** (`SC2...:88-118`) — KEEP as-is (STRLEN>0 → re-inject `EntryStepDispatch` to `queue:{procId:D}`; assert origin-queue depth via `PollForQueueDepthAsync`). Production: `ReinjectConsumer.cs:43-55`.

**STATE 2 REINJECT data-gone** (`SC2...:123-151`) — KEEP as-is (silent drop, `keeper_reinject_dropped`; assert origin queue empty AND `skp-dlq-1` does NOT climb). The DLQ-before/after assert idiom:
```csharp
var dlqBefore = await ReadQueueDepthAsync(ConsolidatedErrorTransportFilter.Dlq1, ct);
// ... Send KeeperReinject (no skp:data seeded) ; Task.Delay(5_000) ...
var dlqAfter = await ReadQueueDepthAsync(ConsolidatedErrorTransportFilter.Dlq1, ct);
Assert.True(dlqAfter <= dlqBefore, "...silent drop (no dead-letter)...");
```

**STATE 3 INJECT** (`SC2...:157-191`) — KEEP as-is (write `L2[EntryId]=Data` + send `StepCompleted` + delete `DeleteEntryId`; assert data written + source deleted). Production: `InjectConsumer.cs:22`.

**STATE 4 DELETE — v5 REWRITE (the headline delta, D-04 + Open-Question 1).** The v4 source-only version (`SC2...:196-218`) currently seeds + asserts ONLY the data key and sends `KeeperDelete` WITHOUT `MessageId`:
```csharp
// v4 (source-only) — REPLACE:
var dataKey = L2ProjectionKeys.ExecutionData(entryId);
await db.StringSetAsync(dataKey, "to-be-deleted");
factory.L2KeysToCleanup.Add(dataKey);
await endpoint.Send(new KeeperDelete(wfId, stepId, procId)
{
    CorrelationId = Guid.NewGuid(), ExecutionId = Guid.NewGuid(), EntryId = entryId,
}, ct);
var deleted = await PollForKeyAbsentAsync(db, dataKey, ct);
Assert.True(deleted, $"DELETE: expected {dataKey} to be deleted ...");
```
v5: `KeeperDelete` now carries `MessageId` (`KeeperDelete.cs:13`) and `DeleteConsumer` deletes BOTH keys in ONE `DEL`:
```csharp
// Production target under proof — src/Keeper/Recovery/DeleteConsumer.cs:19-24 (GC-03, both-key)
protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
    => await Guard(() => Db.KeyDeleteAsync(new RedisKey[]
    {
        L2ProjectionKeys.ExecutionData(m.EntryId),
        L2ProjectionKeys.MessageIndex(m.MessageId),   // KeeperDelete CARRIES MessageId now
    }), ct);
```
> **v5 STATE 4 rewrite:** mint a `messageId`, pre-seed BOTH `skp:data:{entryId}` (StringSet) AND `skp:msg:{messageId}`
> (e.g. `db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), 0, entryId.ToString("D"))`), register BOTH into
> `L2KeysToCleanup`, `Send` `KeeperDelete{ ..., EntryId = entryId, MessageId = messageId }`, then assert BOTH gone
> via two `PollForKeyAbsentAsync` calls (the existing helper, `SC2...:224-241`):
```csharp
private static async Task<bool> PollForKeyAbsentAsync(IDatabase db, string key, CancellationToken ct)
{
    var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
    var delay = 500;
    while (DateTime.UtcNow < deadline)
    {
        ct.ThrowIfCancellationRequested();
        if (!await db.KeyExistsAsync(key)) return true;
        await Task.Delay(Math.Min(delay, 2_000), ct); delay = Math.Min(delay * 2, 2_000);
    }
    return false;
}
```

**NEW organic recovery-pass test (D-03, Open-Question 2 — prefer option (b)).** A second `[Fact]` in the same class.
Pre-seed a populated slot array + a completed data key, publish an `EntryStepDispatch` carrying a known `MessageId`, then
drive the `if exist L2[messageId]` recovery branch and assert send-before-retire + two-key net-zero. Production branch:
```csharp
// src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:94-105 — the if-exist branch D-03 drives
var exists = await RetryLoop.ExecuteAsync(
    () => db.KeyExistsAsync(L2ProjectionKeys.MessageIndex(messageId)), limit, ct);
// ...
if (exists.Value)
    await RunRecoveryAsync(d, messageId, db, limit, ct);                       // ← RECOVERY pass
else
    await RunForwardAsync(d, messageId, db, limit, executionDataTtl, ct);
```
```csharp
// :152-171 — send-before-retire (SLOT-03), fresh exec; then the two-key DEL tail (:182 → DeleteTerminalAsync)
foreach (var t in temp)
{
    if (!t.Completed) continue;
    await SendResult(BuildCompleted(d, NewId.NextGuid(), t.EntryId), limit, ct);   // SEND FIRST (fresh exec, D-03)
    var retire = await RetryLoop.ExecuteAsync(
        () => db.HashSetAsync(L2ProjectionKeys.MessageIndex(messageId), t.Slot, RetiredSlot), limit, ct);  // RetiredSlot = Guid.Empty
    // ... TTL refresh ...
}
// RECOV-03 tail: anyInfra → REINJECT (no delete) ⊻ all-clear → DeleteTerminalAsync (two-key DEL net-zero)
```
> **Organic-test recipe (Open-Question 2 rec):** pre-seed `skp:msg:{messageId}` HASH with `{0: entryId}` + `skp:data:{entryId}`,
> publish `EntryStepDispatch` with the broker `MessageId` set, then assert: (1) the completed step re-sent (orchestrator
> advance — recovery re-sends the EXISTING entryId, no NEW data key expected), (2) slot retired to `Guid.Empty`, (3) two-key
> DEL leaves BOTH keys absent. **Planner must grep for any existing test that sets the broker `MessageId` on a `Send`**
> (via `SendContext.MessageId` in a send-pipe/observer) to confirm the in-proc bus exposes it. Per D-03 discretion this
> test may live in the shared `[Collection("Observability")]` (it does NOT stop redis).

**DELETE the dead composite-sweep block** (`SC2...:462-473`) — see "Shared Patterns". The broker-queue teardown
(`BrokerQueuesToPurge`/`BrokerQueuesToDelete`, `SC2...:441-445`, `:476-483`) is UNCHANGED.

---

### `tests/BaseApi.Tests/Orchestrator/SC3PauseResumeOutageE2ETests.cs` (test, event-driven) — MODIFY (retag-only)

**Analog:** itself — behavior is UNCHANGED (Landmine 4: A14 BIT gate + global pause/resume retained verbatim in v5;
`BitHealthLoop.cs:48-68` still `bus.Publish(PauseAll)`/`ResumeAll` on each health edge).

**Only two edits:**
1. **Retag** (`SC3...:92`): `[Trait("Phase","49")]` → `[Trait("Phase","55")]`. Keep the `RedisOutageSerialCollection`
   isolation (`:23-24`, `:93` `[Collection("RedisOutageSerial")]`, `DisableParallelization = true`) VERBATIM — SC3
   stops `sk-redis` and MUST stay serial (Pitfall 3). Update the `Phase=49` mentions in the XML doc comments (`:20`).
2. **DELETE the dead composite-sweep block** (`SC3...:597-608`) — see "Shared Patterns".

No assertion or flow change. The `Global PauseAll`/`Global ResumeAll` ES-seam proofs are correct as-is.

---

### `.planning/phases/55-live-proof-close-gate/55-HUMAN-UAT.md` (doc, runbook) — CREATE

**Analog:** `.planning/phases/49-live-proof-close-gate/49-HUMAN-UAT.md` (clone the section structure).

**Heading skeleton to mirror** (from the analog):
```
# Phase 55 — Live Proof & Close Gate — Operator Runbook (HUMAN-UAT)
## Current Test
## Purpose
## Step 1 — Rebuild the v5 stack (breaking wire contract)
## Step 2 — Invoke the close gate
## Step 3 — Record the GREEN run
## Step 4 — DoD gate (tick TEST-01/02)
## Tests
## Summary
## Gaps
### Live Run #1 / #2 / #3 (record blocks)
```

**Step 1 rebuild command** (analog `49-HUMAN-UAT.md:46`) — the v5 wire contract is BREAKING (slot-array + 3-state + A19), so all four contract-changed services must be rebuilt (D-09):
```
docker compose up -d --build baseapi-service orchestrator processor-sample keeper
```
**Add the v5-specific clean-rebuild caution** (Assumption A1 / Runtime State "Build artifacts"): instruct a clean
`dotnet clean + build` so the host-built `Processor.Sample.dll` embedded `SourceHash` matches the container — a stale
incremental hash bit Phase 49 and false-passes/times-out the liveness gate.

**Step 2** invokes `pwsh -File scripts/phase-55-close.ps1`; **Step 3** records the three SHAs + the Passed fact count +
`skp-dlq-1` depth==0 + the NEW `skp:msg:*` count==0; **Step 4** ticks TEST-01/TEST-02 only after the N=3 GREEN run.

---

## Shared Patterns

### Dead composite-sweep removal (Landmine 3 — applies to ALL THREE SC factories)
**Source (identical block in 3 files):** `SC1...:437-448`, `SC2...:462-473`, `SC3...:597-608`
**Apply to:** every SC factory `DisposeAsync` — DELETE the block entirely.
```csharp
// GAP-49-8: sweep any composite backup keys (skp:{corr}:{wf}:{proc}:{exec}) the live Keeper
// left for this run's workflows. ...
foreach (var srv in cleanupMux.GetEndPoints())
{
    var server = cleanupMux.GetServer(srv);
    foreach (var wfId in ParentIndexMembersToSrem)
        foreach (var compositeKey in server.Keys(pattern: $"skp:*:{wfId}:*"))
            await db.KeyDeleteAsync(compositeKey);
}
```
> Model-B (the composite backup key) was retired in Phases 50/53; `grep src/ CompositeBackup` finds nothing. This sweep
> scans a namespace that never has members — dead code, and its `skp:*:{wfId}:*` glob is a landmine that could match
> future `skp:msg:`-adjacent shapes. Delete it; the `L2KeysToCleanup` + `ParentIndexMembersToSrem` drain ABOVE it stays.
> Replace its intent with explicit `skp:msg:{messageId}` registration into `L2KeysToCleanup` where the tests mint indexes.

### Net-zero teardown via `L2KeysToCleanup` (reuse, +`skp:msg:*` registration)
**Source:** `SC1...:417-435` (the keep-this part of `DisposeAsync`); `factory.L2KeysToCleanup.Add(...)` call sites.
**Apply to:** SC1 (the minted `skp:msg:*`), SC2 STATE 4 (the seeded `skp:msg:*`) + the organic recovery test.
```csharp
public List<RedisKey> L2KeysToCleanup { get; } = new();
public List<RedisValue> ParentIndexMembersToSrem { get; } = new();
public override async ValueTask DisposeAsync()
{
    if (L2KeysToCleanup.Count > 0 || ParentIndexMembersToSrem.Count > 0)
    {
        await using var cleanupMux = await ConnectionMultiplexer.ConnectAsync(HostRedisFull);
        var db = cleanupMux.GetDatabase();
        if (L2KeysToCleanup.Count > 0) await db.KeyDeleteAsync(L2KeysToCleanup.ToArray());
        if (ParentIndexMembersToSrem.Count > 0)
            await db.SetRemoveAsync(L2ProjectionKeys.ParentIndex(), ParentIndexMembersToSrem.ToArray());
    }
    Restore();
    await base.DisposeAsync();
}
```
> D-07: every minted `skp:data:*` / `skp:msg:*` is registered so a leak surfaces at the gate as a SHA mismatch — NO
> gate-side destructive flush, NO prefix filter narrowing the scan. The A19 path self-cleans the happy case; registration
> is the belt-and-suspenders.

### Truthful liveness gate (reuse VERBATIM — SC1 + SC3 + organic test)
**Source:** `SC1...:191-230` (`PollForHealthyLivenessAsync`) + `:88-105` (genuine embedded `SourceHash` reflection).
**Apply to:** any test that drives an organic round-trip. Unchanged for v5.
```csharp
var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
    .GetCustomAttributes<AssemblyMetadataAttribute>()
    .First(a => a.Key == "SourceHash").Value!;
var procId = await SeedProcessorAsync(client, hash, ct);   // GET-or-create by source-hash (idempotent)
// ...
await PollForHealthyLivenessAsync(procId, ct);   // poll the REAL container's skp:{procId:D} heartbeat (no synthetic seed)
```

### Live broker queue-depth read (reuse VERBATIM — SC2)
**Source:** `SC2...:294-326` (`ReadQueueDepthAsync` via `docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages`,
TAB-parsed). Matches the gate's depth mechanism. Apply to all SC2 states + the organic test's queue-effect asserts.

### MTP filter discipline (Pitfall 1 — runbook + ad-hoc, NOT the gate cadence)
**Source:** `BaseApi.Tests.csproj:40,52` (`UseMicrosoftTestingPlatformRunner` + `TestingPlatformDotnetTestSupport`).
- Hermetic (D-08 compile/exclude): `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait Category=RealStack`
- Phase-scoped live: `dotnet run --project tests/BaseApi.Tests -- --filter-trait "Phase=55"`
- The close gate's cadence runs the FULL suite with NO filter (`phase-49-close.ps1:242`) — the retag does not affect it.

---

## No Analog Found

None. Every Phase-55 target is a direct adaptation of proven Phase-49 infrastructure (the SC suite, the close script,
the runbook). The only genuinely v5-new MECHANICS (not files) are: the `skp:msg:*` index assertions (SC1), the both-key
DELETE proof (SC2 STATE 4), and the organic recovery pass (SC2 new `[Fact]`) — all have a production-code analog cited
above (`ProcessorPipeline.cs` / `DeleteConsumer.cs`).

## Metadata

**Analog search scope:** `scripts/`, `tests/BaseApi.Tests/Orchestrator/`, `src/BaseProcessor.Core/Processing/`,
`src/Keeper/Recovery/`, `src/Messaging.Contracts/Projections/`, `.planning/phases/49-live-proof-close-gate/`
**Files read this session:** `scripts/phase-49-close.ps1`, `SC1/SC2/SC3 *E2ETests.cs`, `ProcessorPipeline.cs` (forward/recovery/tail), `DeleteConsumer.cs`, `KeeperDelete.cs`, `L2ProjectionKeys.cs`, `49-HUMAN-UAT.md` (headings)
**Pattern extraction date:** 2026-06-12
