# Phase 85: Processor↔Framework Boundary Hardening - Pattern Map

**Mapped:** 2026-07-21
**Files analyzed:** 8 (1 new + 4 source-modified + 3 test-modified) + 1 gate script (run-only)
**Analogs found:** 8 / 8 (every new/changed file has an in-repo analog; this is a refactor, so most analogs are the exact file being edited or an adjacent precedent)

> This is a control-flow refactor, not greenfield. "Closest analog" here usually means **the exact precedent shape already in the tree** (the keeper-send-exhaust rethrow, the Mode-1 delete tail, the `ProcessStatusException` family, the existing hermetic facts that assert the pre-hardening behavior). Every excerpt below was read from the current tree this session; line anchors are verified.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs` (NEW) | exception type | nack-path (escape signal) | `ProcessStatusException.cs:8` (family) + `Resilience/KeyAbsentException.cs:7` (Exception-derived sentinel) | exact (two precedents) |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (MODIFY) | framework core | ack→nack (PB-01 throw) + delete-path removals (PB-03) | itself: deterministic-throw at `:115`; `SendKeeper` rethrow `ProcessorPipeline.cs:304` | exact |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (MODIFY) | framework core | nack-path (narrow catch) + delete-path (null-path delete) | itself: `ProcessStatusException` catch `:158`, Mode-1 delete tail `:222-227`, `SendKeeper` rethrow `:304` | exact |
| `src/Processor.Sample/SampleProcessor.cs` (MODIFY) | concrete processor | delete-path (remove author delete) | itself: Mode-2 entry path `:57-74` | exact |
| `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` (MODIFY) | hermetic test | ack→nack invert + delete-fact removals | itself: `SpawnToPost_SendExhaust_Swallows_*` `:63-85`; `SendFaultProvider` `:28-44` (reuse) | exact |
| `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs` (MODIFY) | hermetic test | nack-path (new D-05 fact) + delete-path (D-04) | itself: `SeamReturnsNull_*NoDelete` `:127-144`, `Completed_DeleteExhaust_EscalatesDelete` `:172-187`, `Build` `:27-35` | exact |
| `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` (MODIFY) | hermetic test | delete-path (flip assertion) | itself: `Entry_Spawns_Two_*DeletesEntry*` `:48-80` | exact |
| `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (MODIFY) | test kit / fixture | delete-path plumbing removal | itself: `FakeProcessor.DeleteEntryAsync` `:92`, `SendFaultProvider` pattern | exact |
| `scripts/phase-68-sweep.ps1` (RUN-ONLY, no edit) | sweep harness | SC-4 gate | itself (terminal gate) | n/a |

---

## Pattern Assignments

### `SpawnSendExhaustedException.cs` (NEW — exception type, nack-path signal)

**Analog A (family + pipeline recognition): `ProcessStatusException.cs`**

```csharp
// src/BaseProcessor.Core/Processing/ProcessStatusException.cs:1-8
namespace BaseProcessor.Core.Processing;
/// <summary>D-04: the author throws one of the three concrete subclasses from ProcessAsync ...</summary>
public abstract class ProcessStatusException(string message) : Exception(message);
```

**Analog B (Exception-derived framework sentinel, ids-only ctor): `Resilience/KeyAbsentException.cs`**

```csharp
// src/BaseProcessor.Core/Resilience/KeyAbsentException.cs:1-7
namespace BaseProcessor.Core.Resilience;
/// <summary>A2: thrown by the Pre-read closure ... converts that into a retryable failure.</summary>
internal sealed class KeyAbsentException() : Exception("L2 key absent or empty.");
```

**CRITICAL constraint (RESEARCH Pitfall 1):** derive from `System.Exception` **directly**, NOT from `ProcessStatusException`. `ProcessStatusException` is caught at `ProcessorPipeline.cs:158` and routed to `OutputTail`+**ack** — the exact opposite of the nack this exception must produce. Mirror the *shape* of `ProcessStatusException` (namespace, `public sealed`, primary-ctor) but the *base type* of `KeyAbsentException` (plain `Exception`).

**Copy-ready target shape** (from RESEARCH §Dedicated Exception; namespace `BaseProcessor.Core.Processing`, alongside `ProcessStatusException.cs`):

```csharp
namespace BaseProcessor.Core.Processing;
/// <summary>PB-01/D-01: a Mode-2 SpawnToPost send exhausted its bounded RetryLoop on a TRANSIENT
/// transport fault. Thrown (not swallowed) so it propagates through the author's ProcessAsync to the
/// ProcessorPipeline catch-all, which rethrows it → MassTransit default RabbitMQ nack-requeue (PB-02).
/// Carries the spawn ExecutionId ONLY (FW-03: never a payload). NOT a ProcessStatusException — it must
/// ESCAPE the pipeline (nack), not route to OutputTail+ack.</summary>
public sealed class SpawnSendExhaustedException(Guid executionId, Exception inner)
    : Exception($"Mode-2 spawn send exhausted for execution {executionId:D}.", inner)
{
    public Guid ExecutionId { get; } = executionId;
}
```

- **IDs-only rule (FW-03/T-70-10):** carry `Guid ExecutionId` + the inner transport `Exception`. Never a `DataResult`/`Data`/`Payload`. The `{executionId:D}` message string is an id, matching the existing `OnSpawnDropped` warn at `ProcessorPipeline.cs:280` which logs `ExecutionId` only.
- **Visibility:** `public` (mirrors `ProcessStatusException`). `internal` would also compile (`InternalsVisibleTo("BaseApi.Tests")`), but `public` is the parity choice.

---

### `BaseProcessor.cs` (MODIFY — framework core; PB-01 throw + PB-03 removals)

**Analog (PB-01): the swallow site to invert — its own deterministic-throw sibling one line above.**

```csharp
// src/BaseProcessor.Core/Processing/BaseProcessor.cs:108-117 (CURRENT — the ONLY lines PB-01 changes)
        if (sent.Succeeded) return;

        // WR-01: narrow the SWALLOW to the TRANSIENT send-fault class only. ...
        if (!IsTransientSendFault(sent.Error)) throw sent.Error!;   // :115 deterministic → RAW throw (STAYS, do NOT touch)
        s.OnSpawnDropped?.Invoke(spawn.ExecutionId);   // :116 SWALLOW (log+counter); scheduler re-fires  ← PB-01 target
```

**PB-01 target shape** (telemetry PRESERVED, fires BEFORE the throw — SC-1 ordering landmine):

```csharp
        if (sent.Succeeded) return;

        if (!IsTransientSendFault(sent.Error)) throw sent.Error!;   // deterministic → raw throw (unchanged, D-03 preserved)
        s.OnSpawnDropped?.Invoke(spawn.ExecutionId);               // telemetry PRESERVED — MUST fire BEFORE the throw
        throw new SpawnSendExhaustedException(spawn.ExecutionId, sent.Error!);   // transient → fail-loud (D-01)
```

- `IsTransientSendFault` (`:123-137`) is UNCHANGED — it already classifies the transient family (`IOException`, `TimeoutException`, `OperationCanceledException`, `SocketException`, `RedisConnectionException`, `BrokerUnreachableException`, …). The D-05 test fault (`RedisConnectionException`) lands in this family.
- **Do NOT reorder or drop** the `OnSpawnDropped?.Invoke` line — dropping it violates "the `OnSpawnDropped` telemetry hook is preserved" (PB-01/SC-1).

**PB-03 removals in this file (each with its exact current anchor + ripple):**

| Item | Current location | Action | Ripple |
|------|-----------------|--------|--------|
| `SeamState.EntryId` field | `:54` (`public Guid EntryId; // inbound source entryId (DeleteEntry operand)`) | Remove | only reader is `DeleteEntry` (`:146`) — gone with it |
| `SeamState.EscalateDelete` field | `:65` (`public Func<Task>? EscalateDelete;`) | Remove | only reader is `DeleteEntry` (`:147`) |
| `DeleteEntry()` method | `:139-148` (whole method) | Remove entirely | callers: `SampleProcessor.cs:72`, `DispatchTestKit.FakeProcessor.DeleteEntryAsync :92` |
| `SetSeamState` params `entryId`, `escalateDelete` | signature `:182-185`; assignments `EntryId = entryId` (`:195`), `EscalateDelete = escalateDelete` (`:202`) | Remove both params + both assignments | callers: `ProcessorPipeline.SetSeamState :269-286`, `BaseProcessorSeamFacts.WireSeam :50-59` + inline `:163-172`, `SampleProcessorFacts.WireSeam :37-46` |
| Class doc-comment | `:22-23` ("SWALLOW on send-exhaust — D-07") + `:27-28`/`:21-23` (describe `DeleteEntry`) | Update wording to fail-loud + framework-owned delete | doc only |

- **CR-01 isolation is NOT affected:** `SeamState` keeps `MessageId`/`WorkflowId`/`StepId`/`CorrelationId`/`ProcessorId` (read by `NewResult` `:166-177`, which does NOT read `EntryId`). The `AsyncLocal<SeamState?>` model (`:43`), `SetSeamState` fresh-object publish (`:190`), `ClearSeamState` (`:209`) stay. Keep the `ConcurrentConsumes` regression test alive via the messageId-stamp path (see test section).

---

### `ProcessorPipeline.cs` (MODIFY — framework core; PB-02 narrow catch + PB-03 null-path delete)

**Analog 1 (PB-02/D-03 — the ack/nack fulcrum): the two existing catches to insert between.**

```csharp
// src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:157-200 (CURRENT)
            try { dr = await processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct); }
            catch (ProcessStatusException e)          // :158 — author status → OutputTail + ack (STAYS)
            { ... _ = await outputTail.RunAsync(statusDr, d.EntryId, ct); ... return; }   // :178/:183
            catch (Exception ex)                      // :185 — GENERIC: incl. deserialize JsonException → StepFailed + ack (STAYS)
            { ... _ = await outputTail.RunAsync(unexpectedDr, d.EntryId, ct); ... return; }   // :197/:199
```

**PB-02/D-03 target — insert a NARROW rethrow between `:184` and `:185` (recommended shape, matches the `ProcessStatusException` catch ordering above it):**

```csharp
            catch (ProcessStatusException e) { ... }               // :158 unchanged
            catch (SpawnSendExhaustedException) { throw; }         // NEW — escape → MassTransit → nack-requeue (D-02/D-03)
            catch (Exception ex) { ... }                            // :185 unchanged — deterministic faults still ack
```

- Equivalent (Claude's discretion): `catch (Exception ex) when (ex is not SpawnSendExhaustedException) { ... }` on the `:185` catch. Both are narrow. The explicit rethrow-catch reads better against the `ProcessStatusException` precedent.
- **D-03 CRUX (highest risk):** do NOT widen. `JsonException`/`ArgumentException`/`NotSupportedException`/`InvalidOperationException` MUST stay in the `:185` generic catch → `StepFailed`+ack, or they poison-loop forever on redelivery. Guarded by the existing negative-control facts (`MalformedPayload_DeserFailure_*`, `SeamThrows_Unexpected_Failed`).

**Analog 2 (PB-02 nack MECHANISM — the exact rethrow to reuse): `SendKeeper`.**

```csharp
// src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:298-305 (CURRENT — the proven nack shape)
        var sent = await RetryLoop.ExecuteAsync(async () => { ... return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // :304 propagate → throw → broker redelivery (Phase-53 D-01)
```

The pipeline class doc `:41-46` documents the guarantee: *"a send that exhausts PROPAGATES (throws) → … the default is RabbitMQ nack-requeue (broker redelivery) — no dead-letter, no `_error`."* PB-02's `catch (SpawnSendExhaustedException) { throw; }` reuses this exact escape.

**Analog 3 (PB-03/D-04 — the Mode-1 delete-then-escalate tail to reuse on the null path):**

```csharp
// src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:220-227 (CURRENT — the block D-04 reuses verbatim)
            // A source step (Guid.Empty) has NO L2 input key to reclaim — skip the delete tail for it
            if (!SourceStep.IsSource(d.EntryId))
            {
                var del = await RetryLoop.ExecuteAsync(
                    () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
                if (!del.Succeeded) await SendKeeper(BuildDelete(d), limit, ct);   // delete-exhaust → DELETE (req 4)
            }
```

**The null-return branch to graft it onto:**

```csharp
// src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:206 (CURRENT)
            if (dr is null) { LogHopExecuted(d, messageId, nameof(StepOutcome.Completed)); return; }
```

**D-04 target:** keep `LogHopExecuted`, then run the `!SourceStep.IsSource(d.EntryId)` delete-then-escalate block (`:222-227`) **before** `return` — AFTER the succeeded spawns. `db`, `limit`, `SendKeeper`, `BuildDelete(d)` are ALL already in `RunAsync` scope (confirmed) — no new plumbing.

- **Discretion (D-04 / Open Question 1):** extract `:222-227` into a private `DeleteEntryTail(EntryStepDispatch d, IDatabase db, int limit, CancellationToken ct)` and call from BOTH the Mode-1 tail and the null path (RESEARCH recommends this — DRY, reads as "framework-owned deletion" at both sites), OR inline. Either satisfies D-04.
- **Ordering is enforced by D-01:** if any spawn exhausts, `SpawnToPost` throws → propagates out of `ProcessAsync` → never reaches `return null` → the new narrow catch rethrows → nack → NO delete. The entry is deleted only once the author returned null (all spawns succeeded).
- **Guid.Empty net-effect (Pitfall 4):** the real Mode-2 seed dispatch has `EntryId == Guid.Empty` (source) → the `!IsSource` guard SKIPS the delete. Today's `SampleProcessor.DeleteEntry()` deletes `L2[ExecutionData(Guid.Empty)]` (harmless no-op). Both leave nothing — net effect identical, no sweep drift.

**PB-03 wiring removal in this file:**

```csharp
// src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:267-287 (CURRENT — SetSeamState closure)
    private void SetSeamState(EntryStepDispatch d, Guid messageId, IDatabase db, int limit)
    {
        processor.SetSeamState(
            db, sendProvider, limit,
            entryId: d.EntryId,                     // :271  ← REMOVE this arg
            processorId: context.Id!.Value,
            ...
            onSpawnDropped: execId => { ... metrics.SpawnDropped.Add(1, ...); },   // :277-285 KEEP (telemetry preserved)
            escalateDelete: () => SendKeeper(BuildDelete(d), limit, CancellationToken.None));   // :286  ← REMOVE this closure
    }
```

- Drop the `entryId:` arg (`:271`) and the whole `escalateDelete:` closure (`:286`). The DELETE-escalation capability is NOT lost — it moves inline to the null-path delete block (which already calls `SendKeeper(BuildDelete(d))`). **KEEP** the `onSpawnDropped` closure (`:277-285`) verbatim: it is the preserved PB-01 telemetry (logs `ExecutionId` only `:280`, increments `metrics.SpawnDropped` `:283`).
- `BuildDelete(d)` (`:319-321`) stays — still called by the delete tail(s).

---

### `SampleProcessor.cs` (MODIFY — concrete processor; remove author delete)

**Analog: its own Mode-2 entry path.**

```csharp
// src/Processor.Sample/SampleProcessor.cs:57-74 (CURRENT)
        if (executionId == Guid.Empty)
        {
            var numbers = new[] { 100, 200 };
            foreach (var number in numbers)
            {
                var data = JsonSerializer.Serialize(new { number, label }, ProcessorConfig.SerializerOptions);
                await this.SpawnToPost(this.NewResult(StepOutcome.Completed, data), Guid.NewGuid());   // :70 now PROPAGATES on exhaust
            }
            await this.DeleteEntry();   // :72  ← REMOVE (framework owns the delete now)
            return null;                // :73  ← STAYS
        }
```

- **Remove line 72** (`await this.DeleteEntry();`) only. The `SpawnToPost` loop (`:70`) is unchanged at the call site — its exhaust behavior changes inside the base (now throws), which propagates transparently through this author (author writes NO catch — D-01). `return null` stays.
- **Update doc-comments:** `:16-19` (describes ENTRY as calling author-owned `DeleteEntry` + "swallow") and `:27-28` (`SpawnToPost`/`DeleteEntry` own resilience) — rewrite to "framework owns the entry delete; `SpawnToPost` fails loud on transient exhaust."

---

### `BaseProcessorSeamFacts.cs` (MODIFY — hermetic tests; invert + remove)

**Reusable asset to KEEP (do NOT rebuild — RESEARCH "Don't Hand-Roll"): the transient-fault send double.**

```csharp
// tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs:28-44 (REUSE verbatim for PB-01 + D-05)
private sealed class SendFaultProvider : ISendEndpointProvider
{
    public Task<ISendEndpoint> GetSendEndpoint(Uri address)
    {
        var endpoint = Substitute.For<ISendEndpoint>();
        var boom = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stub: -post send unreachable");
        endpoint.Send(Arg.Any<object>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw boom);
        endpoint.Send(Arg.Any<object>(), Arg.Any<IPipe<SendContext>>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw boom);
        return Task.FromResult(endpoint);
    }
    public ConnectHandle ConnectSendObserver(ISendObserver observer) => throw new NotSupportedException();
}
```

`RedisConnectionException` ∈ `IsTransientSendFault` → lands on the new throw branch. Reuse for BOTH the inverted PB-01 seam fact AND the D-05 pipeline nack fact.

**Fact 1 — INVERT (`:63-85`):** `SpawnToPost_SendExhaust_Swallows_FiresDropHook_NoThrow_NoKeeper` currently asserts NO throw:

```csharp
// CURRENT :78-84 — asserts swallow
        // SWALLOW: a send-exhaust must NOT throw out of SpawnToPost.
        await processor.SpawnToPostAsync(processor.NewResultPublic(StepOutcome.Completed, "{\"n\":1}"), spawnExec);
        var dropped = Assert.Single(droppedExecIds);
        Assert.Equal(spawnExec, dropped);          // drop hook carries the minted spawn execId
        Assert.False(escalated);
```

INVERT to: `await Assert.ThrowsAsync<SpawnSendExhaustedException>(() => processor.SpawnToPostAsync(...))`; assert the drop hook STILL fired first (`Assert.Single(droppedExecIds)` + `Assert.Equal(spawnExec, dropped)` — proves SC-1 telemetry-before-throw); assert `ex.ExecutionId == spawnExec`. Rename (drop `Swallows`/`NoThrow`).

**Fact 2 — NEW (PB-01 deterministic negative):** a NON-transient send fault (e.g. an `ArgumentException` from the send double) still throws the RAW exception, NOT `SpawnSendExhaustedException`. Clone `SendFaultProvider` with a non-transient `boom` → `await Assert.ThrowsAsync<ArgumentException>(...)` (or `Assert.IsNotType<SpawnSendExhaustedException>`).

**Facts to REMOVE (DeleteEntry gone):**
- `DeleteEntry_Success_NoEscalation` (`:114-132`) — remove.
- `DeleteEntry_DeleteExhaust_Escalates_ToDeleteHook` (`:201-221`) — remove. (Delete coverage moves to the pipeline null-path facts in `PrePipelineFacts`.)

**Fact to KEEP-BUT-RE-EXPRESS — `ConcurrentConsumes_DoNotBleed_SeamStateIsPerDispatch` (`:142-199`):** it currently proves CR-01 isolation via BOTH the messageId stamp AND `DeleteEntryAsync` (`:178`, `:195-198`). Keep the CR-01 regression; drop the entry-delete half (the `deletedKeys`/`KeyDeleteAsync` plumbing `:146-152`, the `await processor.DeleteEntryAsync()` `:178`, and the `Assert.Contains(...deletedKeys)` `:195-198`), re-express isolation via the NewResult messageId stamp only (already asserted at `:189-193`).

**`WireSeam` helper (`:46-61`):** drop the `entryId`/`escalateDelete` params + the two `SetSeamState` args once `BaseProcessor.SetSeamState` signature loses them.

---

### `PrePipelineFacts.cs` (MODIFY — hermetic tests; D-04 update + D-05/D-04-new/D-03 facts)

**Reusable build harness (RESEARCH-confirmed plain-construct — D-05 anchor):**

```csharp
// tests/BaseApi.Tests/Processor/PrePipelineFacts.cs:27-35 (REUSE for the D-05 fact)
private static ProcessorPipeline Build(
    IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
    DispatchTestKit.CapturingSendProvider send)
{
    var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300), DispatchTestKit.Metrics());
    return new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), tail,
        DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
}
```

Note the D-05 nack fact needs a `send` provider that FAULTS the spawn — the `Build` signature takes `CapturingSendProvider`; either overload `Build` to accept a plain `ISendEndpointProvider` or construct the `ProcessorPipeline` inline with `SendFaultProvider` for that one fact (the ctor is public — `ProcessorPipeline.cs:48-56`).

**Fact — UPDATE (D-04): `SeamReturnsNull_NoWrite_NoSend_NoDelete` (`:127-144`).** Currently uses a NON-empty `entryId = Guid.NewGuid()` (`:130`) and asserts `db.DidNotReceive().KeyDeleteAsync(...)` (`:143`). After D-04 the null-path delete FIRES for a non-source id — so this test must switch `entryId` to **`Guid.Empty`** (source) to keep asserting "no delete" AND match real Mode-2 semantics.

```csharp
// CURRENT :130 → change to Guid.Empty
        var entryId = Guid.NewGuid();   // ← change to: var entryId = Guid.Empty;
        ...
        await db.DidNotReceive().KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // :143 still holds under IsSource skip
```

**Fact — NEW (PB-03/D-04 non-source delete): framework DOES delete on a non-source null-return.** Analog is the existing delete-escalate fact structure:

```csharp
// tests/BaseApi.Tests/Processor/PrePipelineFacts.cs:172-187 — Completed_DeleteExhaust_EscalatesDelete (STRUCTURE to mirror)
        var redis = DispatchTestKit.ReadOkDeleteFaultL2(new Dictionary<string,string>{ [L2ProjectionKeys.ExecutionData(entryId)] = "{}" }, out _);
        ...
        Assert.Single(send.SentKeeper.OfType<KeeperDelete>());      // delete-exhaust → one DELETE
```

New fact: a Mode-2 `FakeProcessor((DataResult?)null)` (returns null) + a NON-empty `entryId` + `ReadWriteDeleteOkL2` → assert `db.Received(1).KeyDeleteAsync((RedisKey)L2ProjectionKeys.ExecutionData(entryId), Arg.Any<CommandFlags>())` (mirror the happy-path delete assertion at `:167-168`). Optionally a delete-exhaust variant with `ReadOkDeleteFaultL2` asserting one `KeeperDelete` (mirror `:172-187`) proves the escalation moved onto the null path.

**Fact — NEW (PB-02/D-05 nack): defeated spawn propagates the dedicated exception.** Reuse `SendFaultProvider` (from `BaseProcessorSeamFacts`, or lift to `DispatchTestKit`) + a `Guid.Empty` source dispatch + the real `SampleProcessor` (or a Mode-2 `FakeProcessor` delegate that calls `SpawnToPost`):

```csharp
await Assert.ThrowsAsync<SpawnSendExhaustedException>(() => pipeline.RunAsync(
    DispatchTestKit.Dispatch(entryId: Guid.Empty, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct));
```

Assert `send.Sent` (Step*) is EMPTY (no `StepFailed` ⇒ NOT the ack path). RESEARCH Open-Q 2 recommends the real `SampleProcessor` for the true Mode-2 path; a `FakeProcessor(Func<FakeProcessor,Guid,Task<DataResult?>>)` Mode-2 delegate (`DispatchTestKit.cs:72-79`) works for a tighter single-spawn assertion.

**Facts to KEEP GREEN (D-03 negative controls — they ARE the poison-safety proof):**
- `MalformedPayload_DeserFailure_OneStepFailed_WritesOutputData_NoEntryDelete` (`:317-340`) — a real `JsonException` → `StepFailed`+ack.
- `SeamThrows_Unexpected_Failed` (`:278-303`) — an `InvalidOperationException` → `StepFailed`+ack.
Both MUST pass unchanged — they prove deterministic faults still ack (do NOT nack). If cheap, add one explicit D-03 fact asserting a deterministic seam throw does NOT propagate out of `RunAsync`.

---

### `SampleProcessorFacts.cs` (MODIFY — flip the delete assertion)

**Analog: its own entry fact.**

```csharp
// tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs:48-80
public async Task Entry_Spawns_Two_Distinct_ExecIds_DeletesEntry_ReturnsNull()
{
    ...
    var entryId = Guid.NewGuid();   // :54 (a NON-empty entryId at the seam level)
    ...
    // the inbound entry was deleted (Mode-2 DeleteEntry).
    await db.Received(1).KeyDeleteAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>());   // :79  ← FLIP
}
```

- **Flip `:79`** `Received(1)` → `DidNotReceive()`: after PB-03 the author no longer calls `DeleteEntry`, so at the seam (author-only, no pipeline) NO delete fires. Rename the fact (drop `DeletesEntry`). Keep every other assertion (two spawns, distinct execIds, `{100,200}`, returns null) unchanged.
- `WireSeam` (`:34-46`): drop `entryId`/`escalateDelete` args after `SetSeamState` loses them (keep the `entryId`/`processorId` local it passes for `processorId`; the `entryId:` arg to `SetSeamState` goes away).

---

### `DispatchTestKit.cs` (MODIFY — remove delete plumbing)

**Anchor: the author-callable delete wrapper on the fake.**

```csharp
// tests/BaseApi.Tests/Processor/DispatchTestKit.cs:91-92
        /// <summary>Author-callable wrapper over the protected <c>DeleteEntry</c>.</summary>
        public Task DeleteEntryAsync() => DeleteEntry();   // ← REMOVE (DeleteEntry gone from BaseProcessor)
```

- Remove `DeleteEntryAsync` (`:92`). Keep `SpawnToPostAsync` (`:89`) and `NewResultPublic` (`:95`) — still used.
- The `FakeProcessor` Mode-2 delegate ctor (`:72-79`) doc mentions "`DeleteEntry`" — update the comment; the delegate itself still calls `SpawnToPost`/`NewResult`.
- The Redis fakes `ReadWriteDeleteOkL2` (`:197-206`) and `ReadOkDeleteFaultL2` (`:286-297`) STAY — the pipeline null-path delete (PB-03) now exercises them, so they remain the delete/delete-fault surfaces for the new `PrePipelineFacts` facts.

---

## Shared Patterns

### Nack-by-rethrow (the PB-02 mechanism)
**Source:** `ProcessorPipeline.cs:304` (`if (!sent.Succeeded) throw sent.Error!;`) + class doc `:41-46`; siblings at `OutputTail.cs:~157` (SendResult) and `~178` (SendKeeper).
**Apply to:** PB-02's `catch (SpawnSendExhaustedException) { throw; }`. An uncaught throw out of the consumer → MassTransit default RabbitMQ nack-requeue (no `_error`, no dead-letter). No new config (D-02).

### IDs-only on any new throw/log path (FW-03 / T-70-10)
**Source:** `ProcessorPipeline.cs:280` (`SpawnToPost drop: ... ExecutionId={ExecutionId}` — ExecutionId only) + WR-03 sanitized-constant catch `:187-195`.
**Apply to:** `SpawnSendExhaustedException` message (`{executionId:D}` only) and any new fact assertions — never `Data`/`Payload`.

### Narrow exception recognition between two catches
**Source:** the `catch (ProcessStatusException e)` at `ProcessorPipeline.cs:158` sitting ABOVE the generic `catch (Exception ex)` at `:185`.
**Apply to:** insert `catch (SpawnSendExhaustedException) { throw; }` between them (D-03) — a third recognized type, but one that ESCAPES rather than routes to `OutputTail`.

### Delete-then-escalate tail (framework-owned deletion)
**Source:** `ProcessorPipeline.cs:222-227` (RetryLoop KeyDelete → `SendKeeper(BuildDelete)` on exhaust, guarded by `!SourceStep.IsSource`).
**Apply to:** the Mode-2 null path (`:206`) AND the existing Mode-1 tail — ideally unified into one `DeleteEntryTail` helper (D-04 discretion).

### Plain-construct pipeline for hermetic facts (no MassTransit harness)
**Source:** `PrePipelineFacts.Build` (`:27-35`) + `ProcessorPipeline` public ctor (`:48-56`).
**Apply to:** the D-05 nack fact — construct directly with a faulting `ISendEndpointProvider` (`SendFaultProvider`).

### Transient-fault send double (reuse, don't rebuild)
**Source:** `BaseProcessorSeamFacts.SendFaultProvider` (`:28-44`) — throws `RedisConnectionException` (∈ `IsTransientSendFault`) on both Send overloads.
**Apply to:** PB-01 inverted seam fact + D-05 pipeline nack fact. Consider lifting into `DispatchTestKit` if both classes need it.

---

## No Analog Found

None. Every file has an in-repo analog (usually itself or an adjacent precedent). This is a control-flow refactor, so there is no greenfield surface requiring RESEARCH.md fallback patterns.

---

## Removals Ripple Map (PB-03 — every place a removal touches)

| Removed symbol | Definition site | Call/read sites that must change |
|----------------|-----------------|----------------------------------|
| `BaseProcessor.DeleteEntry()` | `BaseProcessor.cs:139-148` | `SampleProcessor.cs:72` (remove call); `DispatchTestKit.cs:92` (remove wrapper); `BaseProcessorSeamFacts.cs:129,178,218` (facts removed/re-expressed) |
| `SeamState.EntryId` | `BaseProcessor.cs:54` | read only in `DeleteEntry :146` (gone); assigned in `SetSeamState :195` |
| `SeamState.EscalateDelete` | `BaseProcessor.cs:65` | read only in `DeleteEntry :147` (gone); assigned in `SetSeamState :202` |
| `SetSeamState` params `entryId`, `escalateDelete` | `BaseProcessor.cs:182-185` (sig), `:195`/`:202` (assign) | callers: `ProcessorPipeline.cs:271`(entryId)/`:286`(escalateDelete); `BaseProcessorSeamFacts.cs:52,59` + inline `:164,172`; `SampleProcessorFacts.cs:39,46` |
| Pipeline `escalateDelete:` closure | `ProcessorPipeline.cs:286` | capability moves inline to the null-path delete (`SendKeeper(BuildDelete(d))` — already in scope) |

---

## Metadata

**Analog search scope:** `src/BaseProcessor.Core/Processing`, `src/BaseProcessor.Core/Resilience`, `src/BaseProcessor.Core/Observability`, `src/Processor.Sample`, `tests/BaseApi.Tests/Processor`.
**Files scanned (read this session):** `BaseProcessor.cs`, `ProcessorPipeline.cs`, `SampleProcessor.cs`, `ProcessStatusException.cs`, `KeyAbsentException.cs`, `ProcessorMetrics.cs`, `BaseProcessorSeamFacts.cs`, `PrePipelineFacts.cs`, `SampleProcessorFacts.cs`, `DispatchTestKit.cs` (10 files) + CONTEXT/RESEARCH.
**Pattern extraction date:** 2026-07-21
**Note for planner:** all line anchors verified against the current tree this session. `OutputTail.cs:~157/178` (sibling rethrow) and `Keeper/Recovery/DeleteConsumer.cs` + `Messaging.Contracts/KeeperDelete.cs` (escalation target, unchanged) were NOT re-read this session — RESEARCH confirms them and they are preserved, not modified.
