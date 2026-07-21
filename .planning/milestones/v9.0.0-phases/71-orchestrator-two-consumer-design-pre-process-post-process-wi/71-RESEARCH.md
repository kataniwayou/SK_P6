# Phase 71: Orchestrator two-consumer design (Pre-Process + Post-Process) with keeper recovery — Research

**Researched:** 2026-06-17
**Domain:** .NET 8 / MassTransit 8.5.5 / StackExchange.Redis messaging subsystem — orchestrator result-advancement reshape mirroring the shipped Phase-70 processor two-consumer pattern
**Confidence:** HIGH (every claim below is `[VERIFIED: codebase]` against the real Phase-70 source; nothing is assumed)

## Summary

Phase 71 is a **pure mechanical mirror** of the already-shipped Phase-70 processor two-consumer design, applied to the orchestrator's result-advancement path, plus the one-line A1 close on the processor side. There is essentially **zero novel architecture to discover** — every pattern (shared relocate-tail helper, `-post` runtime-bound queue, `RetryLoop`-escalates-to-keeper map, envelope-`MessageId` override, self-contained keeper contracts, `RecoveryConsumerBase`+`Guard`, partitioned gate-open-only recovery endpoint) already exists in the repo and was read in full during this research. The planner's job is to *replicate these shapes faithfully* on the orchestrator, honoring the two genuine asymmetries the CONTEXT locked: (a) the orchestrator's keeper ops re-inject a **result** and write **`data:`** (new contracts, not reuse), and (b) no-silent-loss is realized by a **two-reason trip-end metric/log**, not a keeper/park.

The data-flow cross-pairing is the single mental model to hold: the **processor** reads input from `skp:data:{entryId}` and writes output to `skp:out:{messageId}`; the **orchestrator** now reads the upstream output from `skp:out:{entryId}` (where `entryId` = the upstream output messageId, courtesy of the A1 close) and writes the next step's input to `skp:data:{messageId}` (where `messageId` = the fresh Pre→Post envelope id). The orchestrator already has `IConnectionMultiplexer` injected (for hydration), so no Redis is *added* — only new call sites on the result path.

**Primary recommendation:** Build a shared `RelocateTail` helper (the structural analog of `BaseProcessor.Core/Processing/OutputTail.cs`) living in the Orchestrator assembly, used by BOTH the new Post-Process consumer AND the orchestrator keeper `INJECT` consumer. Turn the 4 typed result consumers into thin shells over a new `OrchestratorPrePipeline` class (analog of `ProcessorPipeline`). Add 3 new orchestrator-specific keeper contracts + 3 new recovery consumers in the Keeper service that subclass the existing `RecoveryConsumerBase`. Copy `OutputTail`'s `RetryLoop`→keeper escalation idiom verbatim. The A1 close is a one-line edit to `OutputTail.BuildStep` (Completed arm: `EntryId = dr.MessageId`).

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**Area 1 — Keeper recovery realization**
- **D-01:** New orchestrator-specific keeper contracts (e.g. `OrchestratorReinject` / `OrchestratorInject` / `OrchestratorDelete`) — NOT reuse of the processor's `KeeperReinject`/`KeeperInject`/`KeeperDelete`. The orchestrator's keeper ops do DIFFERENT work (re-inject a Step-result to the orchestrator Pre, not a dispatch to a processor; INJECT writes `data:` + dispatches `EntryStepDispatch`; DELETE deletes the `out:` blob).
- **D-02:** Each orchestrator keeper contract is self-contained: `REINJECT` carries the original Step-result (+ `messageId`); `INJECT` carries the next-step target + relocated data (the `NextStepHandoff` payload) + `messageId`; `DELETE` carries the `out:` `entryId`.
- **D-03:** `messageId` is carried IN-BODY on the keeper escalation contracts (unlike the live Pre→Post handoff where it rides the envelope). The Keeper overrides the outbound envelope `MessageId` when re-injecting / dispatching. Same processor→keeper→processor behavior as Phase 70.
- **D-04:** Orchestrator keeper recovery runs in the central Keeper service via new recovery consumers, sharing `RecoveryConsumerBase` + `Guard` + the partitioned, gate-open-only recovery queue. NOT in-process in the orchestrator.
- **D-05:** `REINJECT` clean-absent drop is preserved (mirror `ReinjectConsumer`): absent/empty `out:entryId` (STRLEN == 0, no Redis exception) = by-design silent ack-drop with a counted structured warning; a Redis exception on the read is infra → `Guard`/exhaustion.

**Area 2 — Pre/Post consumer + queue structure**
- **D-06:** Keep the 4 typed result consumers (`StepCompletedConsumer`/`StepFailedConsumer`/`StepCancelledConsumer`/`StepProcessingConsumer`) on the shared `orchestrator-result` queue as the Pre-Process front — each still supplies its compile-time `Outcome` knob (no status if/switch). They become thin shells delegating to a shared Orchestrator Pre-pipeline.
- **D-07:** New Orchestrator Post-Process consumer on a distinct `orchestrator-result-post` queue (same endpoint policy as the result queue: `UseMessageRetry` none, error routing off, send-exhaust → throw → broker redelivery; runtime-bind pattern).
- **D-08:** Shared relocate-tail helper (analog of Phase-70 `OutputTail`) implementing "write `L2[data:messageId]` (TTL'd, Completed path) → dispatch `EntryStepDispatch`", used by BOTH the Post consumer AND the orchestrator keeper `INJECT` path.
- **D-09:** `StepProcessing` remains non-advancing (no fan-out).

**Area 3 — Fan-out (Pre→Post) message contract**
- **D-10:** New self-contained record (e.g. `NextStepHandoff`) in `Messaging.Contracts` for the Pre→Post handoff. Carries `{ WorkflowId, next StepId, next ProcessorId, next Payload, relocated Data (string JSON), CorrelationId, ExecutionId }`. NOT a reuse of `DataResult`.
- **D-11:** `messageId` is the MassTransit envelope `MessageId`, NOT a body field on the live handoff. Each Pre `Send` gets a fresh envelope `MessageId`; the Post consumer reads `context.MessageId` and uses it as both the `L2[data:messageId]` key AND the dispatched `entryId`.
- **D-12:** Relocated data rides INLINE as a string-JSON field; Post writes it verbatim to `L2[data:context.MessageId]`. NOT by reference.
- **D-13:** `ExecutionId` is threaded UNCHANGED from the inbound result. A non-completed continuation carries empty `Data` (no `out:` blob exists) and dispatches with `entryId = Guid.Empty`.

**Area 4 — Orchestrator L2 access + trip-end signal**
- **D-14:** The orchestrator already has Redis (`IConnectionMultiplexer` injected today for hydration). Phase 71 reuses the existing connection for the new result-path data-relocation ops. It does NOT add Redis to the orchestrator.
- **D-15:** L1 stays the in-memory high-performance orchestration layer. Next-step resolution remains L1-only / pure in-memory (`SelectNext` against `wf.Steps`) — no Redis on the resolve path. Only the data-relocation tail touches L2.
- **D-16:** All new L2 ops run through `BaseConsole.Core`'s `RetryLoop`: read→`REINJECT`, write→`INJECT`, delete→`DELETE`, send→throw.
- **D-17:** The `data:` write carries the jittered `L2ProjectionKeys.OutputDataTtl` policy (single source of truth), floor from the orchestrator's own option (default 300, consistent with `ProcessorLivenessOptions`/`RecoveryOptions`).
- **D-18:** Trip-end signal = a counter on the existing `OrchestratorMetrics` with two reasons (`completed-terminal` vs `completed-unresolved`), each paired with a distinct structured log line. No new bus event.

### Claude's Discretion
- Exact C# names/namespaces/nullability for `NextStepHandoff`, the orchestrator keeper contracts, the Pre-pipeline class, the relocate-tail helper, and the Post consumer/definition (keep consistent with existing `Step*`/`DataResult`/`OutputTail` conventions).
- The exact orchestrator metric instrument name + tag key for the two trip-end reasons (keep consistent with `OrchestratorMetrics.DispatchSent`/`ResultConsumed` PascalCase-tag convention).
- A1 stamp wiring detail in `OutputTail.BuildStep` (Completed arm: `EntryId = dr.MessageId`; other arms keep `Guid.Empty`).
- The orchestrator's own `OutputDataTtl` option type/binding (new options class vs reuse an existing one).
- Whether the orchestrator Post endpoint binds at startup like the result queue or follows the runtime-bind pattern — planner's call from the existing Orchestrator `Program.cs` wiring.
- Test-double reuse vs new fakes for the orchestrator Pre/Post/keeper facts.

### Deferred Ideas (OUT OF SCOPE)
None — discussion stayed within phase scope. (Fan-in / merge / AND-join is SPEC-declared out of scope, owned by a future phase; the processor's statelessness makes OR-join / fire-per-edge the model.)
</user_constraints>

<phase_requirements>
## Phase Requirements

The ROADMAP shows "TBD"; the SPEC is authoritative. Requirements 1–12 are from `71-SPEC.md`.

| ID | Description | Research Support |
|----|-------------|------------------|
| 1 | Orchestrator Pre-Process consumer gates on `L2[out:entryId]`; gate/read exhaustion → one `REINJECT` + return | `ProcessorPipeline.RunAsync` gate idiom (KeyExists in `RetryLoop` → `SendKeeper(BuildReinject)`) — copy verbatim, swap `ExecutionData`→`OutputData` |
| 2 | No-silent-loss next-step resolution (terminal OR unresolvable → complete with proper message + metric) | Today's silent ack at `TypedResultConsumer.cs:68–75`; D-18 two-reason counter on `OrchestratorMetrics`; `OrchestratorMetrics` instrument convention |
| 3 | Continuation is entry-condition-driven and outcome-agnostic | `StepAdvancement.SelectNext` already does exactly this (`== (int)outcome \|\| Always`; `Never` excluded) — reuse unchanged |
| 4 | Data relocation only when an output blob exists (Completed) | `OutputTail.RunAsync` writes only when `result == Completed`; orchestrator gates relocation on the `out:` read |
| 5 | Pre fan-out + entry delete (one Post msg per match, then delete `L2[out:entryId]`) | `ProcessorPipeline` post-tail delete idiom (`KeyDelete` in `RetryLoop` → `BuildDelete`); fan-out send loop mirrors `SelectNext` foreach |
| 6 | Orchestrator Post-Process consumer (write `L2[data:messageId]` TTL'd + dispatch `entryId = messageId`) | `PostProcessConsumer` shell over `OutputTail`; `StepDispatcher.DispatchAsync` for the dispatch tail |
| 7 | A1 close — processor stamps `EntryId = output messageId` | `OutputTail.BuildStep` Completed arm (`OutputTail.cs:80-81`): change `EntryId = Guid.Empty` → `EntryId = dr.MessageId` |
| 8 | `REINJECT` redefined (orchestrator side) — read `L2[out:entryId]`, re-inject Step-result, same messageId, clean-absent drop | `ReinjectConsumer` (STRLEN drop + envelope override) — mirror with new contract |
| 9 | `INJECT` redefined (orchestrator side) — write `L2[data:messageId]`, delete `L2[out:entryId]`, dispatch | `InjectConsumer` strict-order idiom — mirror with the shared relocate-tail |
| 10 | `DELETE` redefined (orchestrator side) — delete `L2[out:entryId]` only | `DeleteConsumer` (one-line `KeyDeleteAsync` in `Guard`) — mirror with `OutputData` key |
| 11 | Resilience invariants preserved (= Phase 70) | `RecoveryEndpointBinder` + `*ConsumerDefinition` no-retry/no-error posture; `ProcessorStartupOrchestrator` `-post` runtime-bind |
| 12 | Migrate tests | `TypedResultConsumerFacts` / `ResultAckTests` (L1-only model); reuse `OrchestratorTestStubs` / `RecoveryTestKit` doubles; processor A1 assertion at `InjectConsumerFacts.cs:75` |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Result consume + outcome routing (Pre front) | Orchestrator console (`orchestrator-result` queue) | — | The 4 typed consumers already own this; D-06 keeps them as thin Pre shells |
| Next-step resolution (entry-condition match) | Orchestrator **L1 in-memory** (`StepAdvancement.SelectNext`) | — | D-15: pure in-memory, cannot infra-fail; resolution failure → trip-complete, never keeper |
| Gate/read upstream output `L2[out:entryId]` | Orchestrator → Redis (reuse `IConnectionMultiplexer`) | Keeper `REINJECT` on exhaustion | D-14/D-16: read op bounded by `RetryLoop`; read-exhaust escalates |
| Fan-out Pre→Post (`NextStepHandoff`) | Orchestrator (`Send` to `orchestrator-result-post`) | — | D-10/D-11: one msg per match, fresh envelope MessageId = next step's messageId |
| Write next-step input `L2[data:messageId]` (Post) | Orchestrator → Redis | Keeper `INJECT` on exhaustion | D-08/D-16: shared relocate-tail; write-exhaust escalates |
| Dispatch `EntryStepDispatch` to processor (`entryId = messageId`) | Orchestrator (`StepDispatcher`, `Send` to `queue:{processorId:D}`) | Keeper `INJECT` (re-dispatch) | D-08/D-13: dispatch tail of the relocate helper |
| Delete `L2[out:entryId]` after fan-out | Orchestrator → Redis | Keeper `DELETE` on exhaustion | D-16: delete-exhaust escalates; send-before-delete ordering |
| Keeper recovery (REINJECT/INJECT/DELETE) | **Central Keeper service** (gate-open-only partitioned queue) | — | D-04: symmetric with processor; BIT health gate governs orchestrator recovery too |
| A1 output-key stamp (`EntryId = messageId`) | **Processor** (`OutputTail.BuildStep` Completed arm) | — | Req 7: processor change so the orchestrator Pre can gate `L2[out:EntryId]` off it |
| Trip-end no-silent-loss signal | Orchestrator `OrchestratorMetrics` + structured log | — | D-18: two-reason counter, no bus event |

## Standard Stack

No new packages. Phase 71 is built entirely on the existing pinned stack (all `[VERIFIED: codebase]`).

### Core (already referenced — no install)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | 8.5.5 | Bus, `IConsumer<T>`, `ISendEndpointProvider`, `IReceiveEndpointConnector`, envelope `MessageId` override via `ctx.MessageId = …`, `Partitioner`+`Murmur3UnsafeHashGenerator` (`MassTransit.Middleware`) | The entire messaging substrate; the `-post` runtime-bind + partitioned recovery endpoint are already on this version |
| StackExchange.Redis | (CPM-pinned) | `IConnectionMultiplexer.GetDatabase()` → `StringGetAsync`/`StringSetAsync`/`KeyExistsAsync`/`KeyDeleteAsync`/`StringLengthAsync` | The L2 store; orchestrator already injects the multiplexer for hydration (D-14) |
| `BaseConsole.Core.Resilience.RetryLoop` | in-repo | Bounded immediate-retry helper returning `RetryOutcome<T>(Succeeded, Value, Error)` | D-16 single source of the escalation map; the processor + keeper both use it |
| OpenTelemetry / `System.Diagnostics.Metrics` | (CPM-pinned) | `IMeterFactory` → `Meter` → `Counter<long>` for the D-18 trip-end metric | `OrchestratorMetrics` is already built this way (`MeterName="Orchestrator"`) |

### Supporting (in-repo assets reused verbatim)
| Asset | Path | Purpose |
|-------|------|---------|
| `OutputTail` | `src/BaseProcessor.Core/Processing/OutputTail.cs` | The relocate-tail STRUCTURE to mirror; also the A1 stamp site |
| `RecoveryConsumerBase<T>` + `Guard`/`Guard<T>` | `src/Keeper/Recovery/RecoveryConsumerBase.cs` | Base for the 3 new orchestrator keeper consumers (D-04) |
| `RecoveryEndpointBinder` | `src/Keeper/Recovery/RecoveryEndpointBinder.cs` | Runtime-binds the partitioned gate-open recovery endpoint; the new consumers register here |
| `ReinjectConsumerDefinition.PartitionKey/PartitionGuid` | `src/Keeper/Recovery/ReinjectConsumerDefinition.cs` | The 4-tuple partition helpers the binder's `UsePartitioner<T>` consume |
| `StepAdvancement.SelectNext` | `src/Orchestrator/Dispatch/StepAdvancement.cs` | Pure L1 entry-condition match — reuse unchanged (req 3) |
| `StepDispatcher.DispatchAsync` | `src/Orchestrator/Dispatch/StepDispatcher.cs` | Build-and-`Send` `EntryStepDispatch`; the Post tail dispatch |
| `L2ProjectionKeys` | `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | `OutputData(messageId)=skp:out:`, `ExecutionData(entryId)=skp:data:`, `OutputDataTtl` policy |
| `RetryOptions` | `src/Messaging.Contracts/Configuration/RetryOptions.cs` | `Limit` (default 3) — the `RetryLoop` budget |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| New `NextStepHandoff` (D-10) | Reuse `DataResult` | REJECTED by CONTEXT: `DataResult` carries the *completed* step's ids + `Result`; the handoff needs the *next-step target* (next StepId/ProcessorId/Payload). Wrong shape. |
| New orchestrator keeper contracts (D-01) | Reuse `KeeperReinject`/`KeeperInject`/`KeeperDelete` | REJECTED by CONTEXT: orchestrator keeper ops re-inject a result (not a dispatch) and write `data:` (not `out:`). Dual-meaning fields. |
| Orchestrator-side in-process keeper | Central Keeper service consumers (D-04) | REJECTED by CONTEXT: the BIT health gate + partitioned gate-open-only queue must govern orchestrator recovery symmetrically. |

**Installation:** None. Verify the repo builds: `dotnet build SK_P6.sln` (or the relevant `.csproj` set).

**Version verification:** No new packages to verify. MassTransit 8.5.5 and the in-repo helpers are pinned and in active use across Phases 50–70.

## Architecture Patterns

### System Architecture Diagram

```
                          PROCESSOR (Phase 70, done)                         ORCHESTRATOR (Phase 71, this work)
                          ────────────────────────                          ──────────────────────────────────

  EntryStepDispatch ──► [Pre: ProcessorPipeline]                     ┌──► [Pre: 4 typed result consumers]  ◄── StepCompleted/Failed/
   (queue:{procId:D})      gate/read L2[data:entryId]                │       (orchestrator-result queue)        Cancelled/Processing
                           ProcessAsync (seam)                       │       │  thin shell → OrchestratorPrePipeline
                           │                                         │       ▼
                  ┌────────┴───────┐                                 │   metrics.ResultConsumed++ (top, before read)
                  ▼                ▼                                 │       │
            inline tail      SpawnToPost ──► [Post:                  │       ▼  REQ 1: gate/read L2[out:entryId]  ──(read-exhaust)──► REINJECT keeper
            (OutputTail)      (queue:{procId:D}-post)                │       │       (entryId = upstream output messageId, via A1)
                  │           PostProcessConsumer                    │       ▼  REQ 3: SelectNext(Outcome, completed, wf.Steps)   [PURE L1 — no Redis]
                  ▼            → OutputTail                           │       │
        write L2[out:msgId]   write L2[out:msgId]                    │   ┌───┴────────────────┐
        (Completed only)      (Completed only)                      │   ▼ no match / L1 miss  ▼ ≥1 match
                  │                │                                 │  REQ 2: trip-end        REQ 4/5: per match →
                  ▼                ▼                                 │  completed-terminal /   Send NextStepHandoff (fresh envelope
        send Step* to        send Step* to                          │  completed-unresolved   MessageId = next msgId, INLINE data)
        orchestrator-result  orchestrator-result ──────────────────►┘  metric + log + ACK     to orchestrator-result-post
                  │                                                          │                       │
                  ▼  A1 (REQ 7): StepCompleted.EntryId = output msgId        ▼                       ▼
        delete L2[data:entryId]                                      then delete L2[out:entryId]  [Post: OrchestratorPostProcessConsumer]
        (delete-exhaust → DELETE keeper)                            (delete-exhaust → DELETE     write L2[data:context.MessageId] (TTL'd, Completed)
                                                                     keeper; send-exhaust →       (write-exhaust → INJECT keeper)
                                                                     throw)                            │
                                                                                                      ▼
                                                                              dispatch EntryStepDispatch to queue:{nextProcId:D}
                                                                              { EntryId = context.MessageId, ExecutionId unchanged }
                                                                                                      │
  KEEPER (central, gate-open-only partitioned recovery queue) ◄──────────────────────────────────────┘
   RecoveryConsumerBase + Guard:
     OrchestratorReinject: read L2[out:entryId] (STRLEN drop) → re-inject Step-result to orchestrator-result, envelope MessageId = carried msgId
     OrchestratorInject:   write L2[data:msgId] → delete L2[out:entryId] → dispatch EntryStepDispatch (via shared RelocateTail), envelope override
     OrchestratorDelete:   delete L2[out:entryId] ONLY
```

A reader can trace the primary `Completed`-continuation use case: processor writes `out:{msgId}` and stamps `StepCompleted.EntryId = msgId` (A1) → orchestrator Pre reads `out:{EntryId}`, resolves one match, fans out one `NextStepHandoff` (envelope MessageId = a fresh `nextMsgId`) → Pre deletes `out:{EntryId}` → Post writes `data:{nextMsgId}` and dispatches `EntryStepDispatch{ EntryId = nextMsgId }` → the next processor reads `data:{nextMsgId}`.

### Recommended Project Structure

New files (names at planner's discretion per CONTEXT; suggested to match existing conventions):

```
src/Messaging.Contracts/
├── NextStepHandoff.cs              # D-10 Pre→Post wire contract (new)
├── OrchestratorReinject.cs         # D-01 keeper contract (new; : IKeeperRecoverable)
├── OrchestratorInject.cs           # D-01 keeper contract (new; embeds NextStepHandoff + messageId)
└── OrchestratorDelete.cs           # D-01 keeper contract (new; out: entryId only)

src/Orchestrator/
├── Dispatch/
│   ├── OrchestratorPrePipeline.cs  # D-06 shared Pre flow (gate/read out: → SelectNext → fan-out → delete out:)
│   └── RelocateTail.cs             # D-08 shared write-data:+dispatch helper (Post AND keeper INJECT use it)
├── Consumers/
│   ├── TypedResultConsumer.cs      # MODIFIED → thin shell delegating to OrchestratorPrePipeline
│   ├── Step{Completed,Failed,Cancelled,Processing}Consumer.cs  # unchanged (still supply Outcome knob)
│   ├── OrchestratorPostProcessConsumer.cs  # D-07 new (IConsumer<NextStepHandoff>)
│   └── OrchestratorPostProcessConsumerDefinition.cs (if startup-bind) OR runtime-bind in Program.cs (D-07 discretion)
├── Observability/
│   └── OrchestratorMetrics.cs      # MODIFIED → add the D-18 two-reason trip-end counter
├── Messaging/
│   └── OrchestratorL2Keys.cs       # OPTIONAL → add out:/data: forwarders (or call L2ProjectionKeys directly)
└── Program.cs                      # MODIFIED → register pipeline/relocate-tail/RetryOptions/metrics; wire -post queue

src/Keeper/Recovery/
├── OrchestratorReinjectConsumer.cs # D-04 : RecoveryConsumerBase<OrchestratorReinject>
├── OrchestratorInjectConsumer.cs   # D-04 : RecoveryConsumerBase<OrchestratorInject> (uses RelocateTail logic)
└── OrchestratorDeleteConsumer.cs   # D-04 : RecoveryConsumerBase<OrchestratorDelete>
src/Keeper/Recovery/RecoveryEndpointBinder.cs  # MODIFIED → add 3 UsePartitioner<T> + 3 ConfigureConsumer<T>
src/Keeper/Program.cs              # MODIFIED → AddConsumer<…>().ExcludeFromConfigureEndpoints() for the 3 new

src/BaseProcessor.Core/Processing/OutputTail.cs  # MODIFIED → A1: Completed arm EntryId = dr.MessageId (req 7)
```

> **Cross-assembly note (gotcha):** `RelocateTail` and the Keeper `OrchestratorInjectConsumer` both need the "write `data:` → dispatch `EntryStepDispatch`" logic. `OutputTail` solved the identical processor problem by sharing the TTL *policy* (`L2ProjectionKeys.OutputDataTtl`) across assemblies while each consumer kept its own thin op body (see `InjectConsumer` re-implements the write+send inline but calls the shared `OutputDataTtl`). The Keeper assembly does NOT reference `Orchestrator`, so the literal `RelocateTail` class cannot be shared into Keeper. **Mirror Phase 70's resolution:** put the relocate *policy/keys* in `Messaging.Contracts` (already done — `L2ProjectionKeys`) and let the Keeper `OrchestratorInjectConsumer` re-implement the small write+dispatch body (exactly as `InjectConsumer` re-implements the write+send body rather than calling `OutputTail`). D-08's "single source of truth" is satisfied at the *policy* level, not by a shared class spanning Keeper↔Orchestrator.

### Pattern 1: Gate/read with RetryLoop → keeper escalation (req 1, the Pre front)
**What:** Branch on `KeyExists` then `StringGet`, each bounded by `RetryLoop`; an exhaustion routes to the keeper and returns.
**When to use:** The Pre-pipeline gate/read of `L2[out:entryId]`.
**Example (copy from `ProcessorPipeline.RunAsync`, swap `ExecutionData`→`OutputData`):**
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:65-79
var exists = await RetryLoop.ExecuteAsync(
    () => db.KeyExistsAsync(L2ProjectionKeys.OutputData(result.EntryId)), limit, ct);
if (!exists.Succeeded) { await SendKeeper(BuildReinject(result), limit, ct); return; }   // read-exhaust → REINJECT
if (!exists.Value) { /* clean-absent: REQ-4 non-completed has no out: blob — proceed with empty data */ }

var read = await RetryLoop.ExecuteAsync(async () =>
{
    var raw = await db.StringGetAsync(L2ProjectionKeys.OutputData(result.EntryId));
    if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // unify absent/empty with a fault on the READ path
    return raw.ToString();
}, limit, ct);
if (!read.Succeeded) { await SendKeeper(BuildReinject(result), limit, ct); return; }
```
> **Asymmetry vs processor:** on the processor a clean-absent `entryId` *returns* (req 1). On the orchestrator, req 4 says a **non-completed** continuation has no `out:` blob by design — the gate finding nothing must NOT escalate; only the entry-condition match drives the fan-out with empty data. So the orchestrator Pre treats "Completed + present blob" as the relocate path and "non-Completed / absent blob" as the empty-data path, NOT a keeper escalation. Only a *Redis exception* (not absence) on a Completed read escalates to REINJECT.

### Pattern 2: Shared relocate-tail (req 6, D-08) — analog of `OutputTail.RunAsync`
**What:** "(Completed) write `L2[data:messageId]` in `RetryLoop` → on exhaust escalate `INJECT` and stop → else dispatch `EntryStepDispatch{ EntryId = messageId }`."
**When to use:** The Post consumer AND (re-implemented inline) the keeper `INJECT`.
**Example (structure mirrors `OutputTail.RunAsync`):**
```csharp
// Analog of src/BaseProcessor.Core/Processing/OutputTail.cs:48-73
public async Task RunAsync(NextStepHandoff h, Guid messageId, CancellationToken ct)
{
    var db = redis.GetDatabase();
    var limit = retryOptions.Value.Limit;

    if (!string.IsNullOrEmpty(h.Data))   // D-13: Completed path has relocated data; non-completed skips the write
    {
        var write = await RetryLoop.ExecuteAsync(
            () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(messageId), h.Data, JitteredTtl()), limit, ct);
        if (!write.Succeeded) { await SendKeeper(BuildInject(h, messageId), limit, ct); return; }   // write-exhaust → INJECT (no dispatch)
    }
    // dispatch EntryStepDispatch with entryId = messageId (D-13; non-completed → messageId is still the key but Data empty,
    //   OR entryId = Guid.Empty per req 6 when no data was written — planner picks the exact arm per SPEC req 6 line)
    await dispatcher.DispatchAsync(h.WorkflowId, h.StepId, h.ProcessorId, h.Payload,
        h.CorrelationId, h.ExecutionId, entryId: messageId, ct);   // send-exhaust inside DispatchAsync throws → redelivery
}
private TimeSpan JitteredTtl() => L2ProjectionKeys.OutputDataTtl(options.Value.OutputDataTtlSeconds);   // D-17
```
> **Req 6 nuance:** SPEC req 6 says "a relocated-empty (non-completed) continuation skips the `data:` write and dispatches with `entryId = Guid.Empty`." So `entryId` is `messageId` ONLY when a `data:` write happened (Completed); for the empty path dispatch `entryId = Guid.Empty` (the source sentinel — `SourceStep.IsSource`). The planner must branch the dispatch `entryId` on whether the write ran.

### Pattern 3: Fan-out one Post message per match, then delete (req 5)
**What:** `foreach` over `SelectNext`, `Send` a `NextStepHandoff` with a fresh envelope MessageId per match; after all sends, delete `L2[out:entryId]`.
**Example:**
```csharp
// SelectNext is unchanged (src/Orchestrator/Dispatch/StepAdvancement.cs)
foreach (var (stepId, step) in advancement.SelectNext(Outcome, completed, wf.Steps))
{
    var handoff = new NextStepHandoff(m.WorkflowId, stepId, step.ProcessorId, step.Payload)
        { Data = relocatedData /* "" for non-completed */, CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId };
    var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.ResultPost}"));   // new queue const
    // D-11: do NOT override MessageId here — let MassTransit assign a fresh envelope id; THAT is the next step's messageId.
    await RetryLoop wrapped Send … ;   // send-exhaust → throw (broker redelivery) — NO delete on throw (req 5)
}
// after the fan-out sends: delete L2[out:entryId] (delete-exhaust → DELETE keeper)
var del = await RetryLoop.ExecuteAsync(() => db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), limit, ct);
if (!del.Succeeded) await SendKeeper(BuildDelete(m), limit, ct);
```
> **Critical ordering (constraint: no outbox):** the fan-out `Send`s happen BEFORE the `out:` delete (send-before-delete). A send-exhaust throws *before* the delete runs — the whole result redelivers and re-fans-out (at-least-once, no dedup). A delete-exhaust after a successful send escalates `DELETE` (the sends already landed). This is the same invariant `ProcessorPipeline` honors (`OutputTail.RunAsync` returns `true`, THEN the pipeline deletes the entry).

### Pattern 4: Two-reason trip-end metric/log (req 2, D-18)
**What:** Replace the silent `log + return` at `TypedResultConsumer.cs:68–75` with a counted, distinctly-logged trip-end. Both the terminal arm (match set empty) and the unresolved arm (L1 miss) emit.
**Example (extend `OrchestratorMetrics`):**
```csharp
// Source convention: src/Orchestrator/Observability/OrchestratorMetrics.cs:44-50
// add in ctor:
TripEnded = meter.CreateCounter<long>("orchestrator_trip_ended");   // collector appends _total suffix (D-03 convention)
// increment site (tag key PascalCase to match DispatchSent/ResultConsumed):
metrics.TripEnded.Add(1,
    new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")),
    new KeyValuePair<string, object?>("Reason", "completed-unresolved"));   // or "completed-terminal"
logger.LogInformation("Trip ended ({Reason}) for ({WorkflowId},{StepId})", "completed-unresolved", m.WorkflowId, m.StepId);
```
> The existing instruments use snake_case names with NO Prometheus suffix (collector's `add_metric_suffixes` appends `_total`) and PascalCase tag keys (`ProcessorId`). Match both exactly. The `Reason` tag carries the two values; alternatively use two counters — discretion, but one counter + a `Reason` tag matches the lowest-cardinality convention.

### Anti-Patterns to Avoid
- **Putting `messageId` in the `NextStepHandoff` body.** D-11: it rides the envelope on the live handoff. Only the *keeper* contracts carry it in-body (D-03), because the keeper must re-assert it across a recovery hop.
- **Reusing `DataResult` for the handoff.** Wrong shape (D-10).
- **Escalating to a keeper on resolution failure.** Resolution is pure L1 — it cannot infra-fail; a no-match / L1-miss is always a clean trip-completion (req 2, boundary). Keeper/throw only for infra (L2-op exhaustion, send fault).
- **Re-reading data in Post.** D-12: data rides inline; a by-reference re-read would add a second L2 read and race Pre's end-delete.
- **Adding a second `IConnectionMultiplexer`.** D-14: reuse the one already injected for hydration.
- **Regenerating `executionId`.** D-13 / req 11: threaded byte-unchanged. (Note: the *current* `StepDispatcher`/`ProcessorPipeline` regenerate `ExecutionId` on continuation — `NewId.NextGuid()` in `BuildFailed` etc. and `RecordingDispatcher` asserts `NotEqual(Guid.Empty)`. Phase 71's continuation must thread the inbound `ExecutionId` UNCHANGED. This is a behavior change the migrated tests must encode.)
- **Adding `UseMessageRetry` or `_error` on the `-post`/keeper endpoints.** Req 11: no bus retry, no error transport; in-code `RetryLoop` only; send-exhaust → throw → broker nack-requeue.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Bounded retry + exhaustion routing | A custom retry loop per op | `RetryLoop.ExecuteAsync` (`BaseConsole.Core`) | Single source of the A3 semantics; returns `RetryOutcome<T>` the caller routes (read→REINJECT, write→INJECT, delete→DELETE, send→throw) |
| Keeper consumer scaffolding (gate-open queue, partitioner, Guard) | New base + endpoint wiring | `RecoveryConsumerBase<T>` + `RecoveryEndpointBinder` + `ReinjectConsumerDefinition.PartitionGuid` | The partitioned, gate-open-only, no-retry/no-error endpoint already exists; new consumers just `AddConsumer().ExcludeFromConfigureEndpoints()` + 3 lines in the binder |
| Entry-condition match / terminal detection | New traversal | `StepAdvancement.SelectNext` | Pure, harness-free, already handles `Always`/`Never`/null-`NextStepIds` (terminal) — reuse unchanged (req 3) |
| Build-and-Send `EntryStepDispatch` | Inline `GetSendEndpoint`+`Send` | `StepDispatcher.DispatchAsync` | Single dispatch owner; the Post tail and keeper INJECT both call it (or its shape) |
| Jittered output TTL | New `random[ttl,2ttl]` formula | `L2ProjectionKeys.OutputDataTtl(floor)` | The ONE place the policy lives (D-17); prevents the cross-assembly desync `OutputDataTtl` was created to fix |
| L2 key formats | String interpolation | `L2ProjectionKeys.OutputData` / `.ExecutionData` | Single source of truth (`skp:out:` / `skp:data:`); a format drift silently breaks reader↔writer |
| Envelope `MessageId` override | Custom send filter | `ep.Send(msg, ctx => ctx.MessageId = carriedId, CancellationToken.None)` | The exact idiom `ReinjectConsumer`/`InjectConsumer` use; `CapturingSendProvider` already records it for tests |

**Key insight:** Phase 71 has essentially no "deceptively complex" greenfield problem — every hard part (recovery partitioning, gate-open pause/resume, envelope override, TTL jitter, retry-then-escalate) was already solved in Phase 70 and read in full here. The risk is *divergence from the template*, not *missing infrastructure*.

## Runtime State Inventory

This is a code-reshape phase (new consumers/contracts + a one-line processor edit), not a rename/migration. Still, two runtime-state categories matter because the design touches L2 keys and bus topology:

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (L2 keys) | The `skp:out:{messageId}` blobs the processor writes (already live post-Phase-70) become the orchestrator Pre's READ source via the A1 stamp. No key *format* change; the A1 stamp changes which GUID lands in `StepCompleted.EntryId`. No data migration — only new readers. | Code edit only (A1 stamp + new readers). No backfill: in-flight pre-A1 `StepCompleted` records carry `EntryId = Guid.Empty` and will gate-miss `out:` → treated as non-completed/empty-data continuation. Acceptable (at-least-once; the trip-end metric counts it). |
| Live service config (bus topology) | A NEW durable queue `orchestrator-result-post` is declared at runtime (or startup). The central Keeper's `keeper-recovery` queue gains 3 new partitioned message types (`OrchestratorReinject/Inject/Delete`) on the SAME endpoint. | Endpoint/binder wiring (Keeper `RecoveryEndpointBinder` + `Program.cs`; Orchestrator `Program.cs`). Code-only this phase (no container deploy — SPEC boundary). |
| OS-registered state | None — verified: no Task Scheduler/systemd/pm2 references in scope. | None |
| Secrets/env vars | None new. `Retry:Limit`, `Recovery:PartitionCount`, and an orchestrator `OutputDataTtl` floor (default 300) are config knobs bound via `IOptions`, not secrets. | Bind the orchestrator's own `OutputDataTtl` floor option (D-17 — new options class or reuse). |
| Build artifacts | None — verified: no egg-info/compiled-binary analogs in this .NET source-rename-free phase. | None |

**The canonical question — after every file is updated, what runtime systems still hold old state?** Only in-flight bus messages: pre-A1 `StepCompleted` records already on the `orchestrator-result` queue will carry `Guid.Empty` EntryId. They resolve as non-completed-shaped continuations (empty data). This is by-design tolerable under the at-least-once/no-dedup posture and is *counted* by the trip-end metric — no silent loss.

## Common Pitfalls

### Pitfall 1: Escalating to a keeper on next-step resolution failure
**What goes wrong:** Treating an L1 miss / no-match as a REINJECT or a throw.
**Why it happens:** The processor Pre *does* escalate on a `out:` gate fault, so the instinct is to escalate symmetrically.
**How to avoid:** Resolution is pure L1 (D-15) and CANNOT infra-fail. Every resolution outcome is either continue (≥1 match) or complete-the-trip (terminal/unresolved) with a metric+log (req 2). Keeper/throw exist ONLY for L2-op exhaustion and send fault.
**Warning signs:** A keeper send or a throw on the `store.TryGet`-miss branch.

### Pitfall 2: Overriding the envelope MessageId on the live Pre→Post fan-out
**What goes wrong:** Calling `ctx.MessageId = …` on the `NextStepHandoff` send.
**Why it happens:** The keeper paths DO override (D-03), so it's easy to over-apply.
**How to avoid:** D-11 — the live handoff gets a FRESH envelope MessageId from MassTransit; that fresh id IS the next step's messageId, which Post reads via `context.MessageId`. Only keeper re-injections override (they must re-assert the carried id across the recovery hop).
**Warning signs:** Post's `data:` key not matching the dispatched `entryId`; tests asserting a body `MessageId` on `NextStepHandoff`.

### Pitfall 3: Send-after-delete ordering (silent loss)
**What goes wrong:** Deleting `L2[out:entryId]` before the fan-out sends land.
**Why it happens:** Reading the pseudocode top-to-bottom without honoring the no-outbox constraint.
**How to avoid:** Send the fan-out FIRST; delete `out:` only after all sends succeed. A send-exhaust throws BEFORE the delete (whole result redelivers). A delete-exhaust after successful sends → `DELETE` keeper (sends already landed). `OutputTail.RunAsync` returning `true` *before* `ProcessorPipeline` deletes the entry is the exact precedent.
**Warning signs:** A `KeyDelete` call ordered before the `Send` loop; a delete that runs even when a send threw.

### Pitfall 4: Cross-assembly relocate-tail sharing (Keeper can't see Orchestrator)
**What goes wrong:** Trying to literally inject the Orchestrator `RelocateTail` class into the Keeper `OrchestratorInjectConsumer`.
**Why it happens:** D-08 says "single source of truth used by BOTH the Post consumer AND the keeper INJECT path."
**How to avoid:** The Keeper assembly references `Messaging.Contracts`, NOT `Orchestrator`. Phase 70 resolved the identical problem by sharing the *policy/keys* (`L2ProjectionKeys.OutputDataTtl`, `OutputData`/`ExecutionData`) in `Messaging.Contracts` while `InjectConsumer` re-implements the small write+send body. Do the same: `OrchestratorInjectConsumer` re-implements write-`data:` + delete-`out:` + dispatch inline, calling the shared keys/TTL policy. "Single source of truth" holds at the policy level.
**Warning signs:** A new `ProjectReference` from `Keeper` → `Orchestrator` (firewall violation; `KeeperDependencyFirewallTests` will fail).

### Pitfall 5: Duplicate / static-vs-runtime endpoint binding for `-post`
**What goes wrong:** A `ConsumerDefinition` with `EndpointName = "orchestrator-result-post"` PLUS a runtime `ConnectReceiveEndpoint`, or an auto-configured kebab queue.
**Why it happens:** The 4 typed result consumers use startup `ConsumerDefinition` (`StepCompletedConsumerDefinition` sets `EndpointName`); the processor `-post` uses runtime `ConnectReceiveEndpoint`. Two valid patterns coexist in the repo.
**How to avoid (planner's call per D-07):** Pick ONE. The orchestrator result queue is startup-bound via `ConsumerDefinition` (no instanceId — competing-consumer). The simplest mirror is a `OrchestratorPostProcessConsumerDefinition` with `EndpointName = OrchestratorQueues.ResultPost` and no bus retry (mirroring `StepCompletedConsumerDefinition`), registered in `Program.cs` `AddConsumer<…>()`. The processor's runtime-bind exists because the processor queue name depends on a *runtime-resolved* `{id:D}`; the orchestrator's post queue name is a static const, so startup-bind is simpler and matches the result queue. If runtime-bind is chosen instead, register `.ExcludeFromConfigureEndpoints()` + `ConnectReceiveEndpoint` (mirror `ProcessorStartupOrchestrator.cs:283-288`).
**Warning signs:** A wrong-named auto kebab queue (`next-step-handoff`); a static+connect collision throw.

### Pitfall 6: The `OutputData` read uses `StringGet`/`KeyExists` but `REINJECT` drop uses `StringLength`
**What goes wrong:** Using `KeyExists` for the keeper REINJECT clean-absent check.
**Why it happens:** The Pre gate uses `KeyExistsAsync`; the keeper drop check is different.
**How to avoid:** `ReinjectConsumer` uses `StringLengthAsync` (STRLEN) because STRLEN returns 0 for BOTH a missing key AND an empty value, whereas `KeyExists` returns true for an empty-string key. The orchestrator `OrchestratorReinjectConsumer` must use the same `StringLengthAsync(...) != 0` test (D-05). A Redis EXCEPTION (not a 0 length) is the infra → `Guard`/exhaustion path.
**Warning signs:** `KeyExistsAsync` in the orchestrator REINJECT; an empty `out:` value re-injecting instead of dropping.

## Code Examples

### A1 close (req 7) — the one-line processor edit
```csharp
// Source: src/BaseProcessor.Core/Processing/OutputTail.cs:78-88 (BuildStep)
// CURRENT (A1 placeholder):
StepOutcome.Completed => new StepCompleted(dr.WorkflowId, dr.StepId, dr.ProcessorId)
    { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = Guid.Empty },
// TARGET (req 7):
StepOutcome.Completed => new StepCompleted(dr.WorkflowId, dr.StepId, dr.ProcessorId)
    { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = dr.MessageId },
// Failed/Cancelled/Processing arms KEEP EntryId = Guid.Empty (no out: blob exists).
```
> **Mirror the same change in the keeper `InjectConsumer`** (`src/Keeper/Recovery/InjectConsumer.cs:44-45`): its `StepOutcome.Completed` arm also stamps `EntryId = Guid.Empty` today. For A1 consistency, a keeper-INJECT'd completion must ALSO carry `EntryId = dr.MessageId` (it has `dr.MessageId` in hand). Otherwise a keeper-recovered completion would gate-miss `out:` on the orchestrator. This is a SECOND A1 site the planner must not miss.

### Keeper recovery consumer shell (D-04) — mirror of `DeleteConsumer`
```csharp
// Source: src/Keeper/Recovery/DeleteConsumer.cs (the delete-only mirror)
public sealed class OrchestratorDeleteConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider, IOptions<RetryOptions> retryOptions)
    : RecoveryConsumerBase<OrchestratorDelete>(redis, sendProvider, retryOptions)
{
    protected override async Task HandleAsync(OrchestratorDelete m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), ct);   // out: not data:
}
```

### Binder edit (req 11) — add 3 partitioners + 3 consumers
```csharp
// Source: src/Keeper/Recovery/RecoveryEndpointBinder.cs:60-66 — add alongside the existing 3:
cfg.UsePartitioner<OrchestratorReinject>(partition, p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.UsePartitioner<OrchestratorInject>(partition,   p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.UsePartitioner<OrchestratorDelete>(partition,   p => ReinjectConsumerDefinition.PartitionGuid(p.Message));
cfg.ConfigureConsumer<OrchestratorReinjectConsumer>(ctx);
cfg.ConfigureConsumer<OrchestratorInjectConsumer>(ctx);
cfg.ConfigureConsumer<OrchestratorDeleteConsumer>(ctx);
```
> The 3 new contracts must implement `IKeeperRecoverable` (the 4-tuple `CorrelationId/WorkflowId/ProcessorId/ExecutionId`) so `PartitionGuid` works unchanged. For orchestrator REINJECT (a re-injected *result*), `ProcessorId` is the completed step's processor; for INJECT/DELETE it is the next/source processor — any consistent choice that keeps a single exec's ops on one slot is fine (StepId is excluded from the key by design).

## State of the Art

| Old Approach (current orchestrator) | Current Approach (Phase 71 target) | When Changed | Impact |
|-------------------------------------|------------------------------------|--------------|--------|
| L1-only straight-through advancement, no L2, no data passing | Pre gates/reads `out:`, fans out, deletes; Post writes `data:`, dispatches | Phase 71 | Steps are no longer data-isolated; `Guid.Empty` placeholder retired (A1) |
| `EntryId = Guid.Empty` flows through every continuation | `EntryId = messageId` (Completed); non-completed keeps `Guid.Empty` | Req 7 (A1) | Processor + keeper INJECT both stamp the real output key |
| `ExecutionId` REGENERATED per continuation (`NewId.NextGuid()` in `ProcessorPipeline.BuildFailed` etc.; `TypedResultConsumerFacts` asserts `NotEqual(Guid.Empty)`) | `ExecutionId` threaded UNCHANGED end-to-end | Req 11 / D-13 | **Behavior change** — migrated tests must assert equality, not regeneration, on the continuation hop |
| L1 miss / no-match = silent business ack (`TypedResultConsumer.cs:68-75`, log + return) | Trip-end metric (`completed-terminal`/`completed-unresolved`) + distinct log + ack | Req 2 / D-18 | No-silent-loss becomes falsifiable/countable |
| Keeper has 3 processor recovery states | Keeper has 6 states (3 processor + 3 orchestrator) on the same partitioned endpoint | D-01/D-04 | New contracts, new consumers, binder +6 lines |

**Deprecated/outdated:** Within scope, nothing new is deprecated — the slot-array/`MessageIndex` model was already retired in Phase 70 (`L2ProjectionKeys` has only `ExecutionData`/`OutputData`). The `Guid.Empty` A1 placeholder is the one thing being retired (req 7), and only on the Completed arm.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| — | (none) | — | All claims are `[VERIFIED: codebase]` against the read source. The only open judgments (Pattern 5 startup-vs-runtime bind; one-counter-with-Reason-tag vs two counters; whether the empty-data dispatch uses `entryId = Guid.Empty`) are explicitly CONTEXT-delegated to planner discretion (D-07, D-18, req 6), not assumptions about external facts. |

**This table is intentionally empty of external assumptions** — the research was conducted entirely against the in-repo Phase-70 implementation, which is the locked pattern of record. No library capability, version, or compliance fact was assumed.

## Open Questions (RESOLVED)

All three judgments below were CONTEXT-delegated discretion items (not external unknowns). They are resolved here and implemented by the Phase-71 plans.

1. **`ExecutionId` regeneration vs threading on the continuation hop.**
   - What we know: req 11 / D-13 mandate `ExecutionId` threaded UNCHANGED. The CURRENT `ProcessorPipeline.BuildFailed/Cancelled/Processing` and the orchestrator's dispatch path regenerate via `NewId.NextGuid()`, and `TypedResultConsumerFacts`/`ResultAckTests` assert `NotEqual(Guid.Empty)` / regeneration on the dispatched `ExecutionId`.
   - What's unclear: exactly which current call sites regenerate, and whether req 11's "unchanged" applies only to the new Pre→Post→dispatch continuation or retroactively to the existing dispatch builders.
   - **RESOLVED:** Thread the inbound result's `ExecutionId` byte-UNCHANGED on the Pre→Post→`EntryStepDispatch` continuation (SPEC acceptance: "`executionId` is byte-identical across a `Completed`→continuation hop"). Migrated tests FLIP from asserting regeneration to asserting equality. Implemented by Plan 02 Task 3 (test flip + `grep -c "NewId.NextGuid"` == 0 on the continuation path) and Plan 02 Task 1 (`RelocateTail` carries `ExecutionId` unchanged).

2. **`entryId` value on the non-completed (empty-data) dispatch.**
   - What we know: req 6 says "a relocated-empty (non-completed) continuation skips the `data:` write and dispatches with `entryId = Guid.Empty`." The Completed path dispatches `entryId = messageId`.
   - **RESOLVED:** Branch `entryId = string.IsNullOrEmpty(h.Data) ? Guid.Empty : messageId` in the shared relocate-tail. Implemented by Plan 02 Task 1.

3. **One trip-end counter with a `Reason` tag vs two counters (D-18 discretion).**
   - What we know: D-18 wants two independently-countable reasons; existing instruments favor one counter + a low-cardinality tag.
   - **RESOLVED:** One `orchestrator_trip_ended` counter + a `Reason` tag (`completed-terminal`/`completed-unresolved`) — lowest cardinality, matches `ResultConsumed`/`DispatchSent` style. Implemented by Plan 02 Task 2.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build + `dotnet test` | ✓ (repo targets net8.0) | 8.0 | — |
| MassTransit 8.5.5 | bus, partitioner, envelope override | ✓ (in active use) | 8.5.5 | — |
| StackExchange.Redis | L2 ops | ✓ (injected in orchestrator + keeper) | CPM-pinned | — |
| RabbitMQ broker | runtime only (NOT this phase) | n/a | — | Hermetic tests use the in-memory MassTransit harness; no real broker needed for `dotnet test` |
| Redis server | runtime only (NOT this phase) | n/a | — | Hermetic facts use NSubstitute `IDatabase`/`IConnectionMultiplexer` doubles (`OrchestratorTestStubs`, `RecoveryTestKit`) |

**Missing dependencies with no fallback:** None. The SPEC boundary is "code + `dotnet test` only this phase (stack is busy)" — matching Phase 70. No container build/deploy. All facts are hermetic.

## Validation Architecture

`workflow.nyquist_validation` is `true` in `.planning/config.json` — this section is required.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit **v3** (3.2.x) under Microsoft.Testing.Platform (MTP); NSubstitute for doubles |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (OutputType=Exe, `UseMicrosoftTestingPlatformRunner=true`, `TestingPlatformDotnetTestSupport=true`); `tests/BaseApi.Tests/xunit.runner.json` (maxParallelThreads cap) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator"` (orchestrator slice) |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

### Phase Requirements → Test Map
| Req | Behavior | Test Type | Automated Command | File Exists? |
|-----|----------|-----------|-------------------|-------------|
| 1 | Pre gate/read `out:`; read-exhaust → 1 REINJECT, no fan-out | unit (hermetic) | `dotnet test … --filter "FullyQualifiedName~OrchestratorPrePipeline"` | ❌ Wave 0 (new `OrchestratorPrePipelineFacts`, model on `PrePipelineFacts`) |
| 2 | terminal → `completed-terminal`; L1-miss → `completed-unresolved`; both ack, no throw/keeper | unit | same filter | ❌ Wave 0 (extend `TypedResultConsumerFacts`/`ResultAckTests` + `OrchestratorMetricsFacts`) |
| 3 | entry-condition match (`==(int)outcome\|\|Always`); Failed-gated advances on Failed; Never never; Processing no fan-out | unit | `--filter "FullyQualifiedName~StepAdvancement"` + new | ✅ `StepAdvancementTests` / `TypedResultConsumerFacts` (reuse; SelectNext unchanged) |
| 4 | Completed → non-empty `data:` per successor; non-completed → no `data:` write, empty dispatch | unit | PrePipeline + Post filter | ❌ Wave 0 |
| 5 | 2 matches → 2 Post msgs + 1 `out:` delete; delete-exhaust → 1 DELETE; send-exhaust → throw (no delete) | unit | PrePipeline filter | ❌ Wave 0 |
| 6 | Post writes `data:messageId` + dispatches `EntryId==messageId`; write-exhaust → 1 INJECT, no dispatch | unit | `--filter "FullyQualifiedName~OrchestratorPostProcess"` | ❌ Wave 0 (model on `PostProcessConsumerFacts`) |
| 7 | A1: `StepCompleted.EntryId == output messageId`; non-completed `Guid.Empty` | unit | `--filter "FullyQualifiedName~OutputTail\|InjectConsumer"` | ⚠️ UPDATE `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs:75` (`Assert.Equal(Guid.Empty, completed.EntryId)` → `dr.MessageId`) + any `OutputTailFacts` Completed assertion |
| 8 | REINJECT envelope `MessageId==messageId`; absent `out:entryId` (STRLEN==0) drops (counted); Redis fault escalates | unit | `--filter "FullyQualifiedName~OrchestratorReinject"` | ❌ Wave 0 (model on `ReinjectConsumer` facts; reuse `RecoveryTestKit.CapturingSendProvider.SentMessageIds`) |
| 9 | INJECT writes `data:`, deletes `out:`, dispatches; no L1 recompute | unit | `--filter "FullyQualifiedName~OrchestratorInject"` | ❌ Wave 0 (model on `InjectConsumerFacts`, `Received.InOrder` write<delete) |
| 10 | DELETE = 1 delete, 0 dispatches/sends | unit | `--filter "FullyQualifiedName~OrchestratorDelete"` | ❌ Wave 0 (model on `DeleteConsumerFacts`) |
| 11 | no bus retry / no `_error`; send-exhaust throws; `executionId` byte-identical; `data:` jittered TTL | unit | endpoint-policy + dispatch filter | ❌ Wave 0 (extend; assert `ExecutionId` equality, NOT regeneration) |
| 12 | suite passes against the new model | suite | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` | ⚠️ MIGRATE `TypedResultConsumerFacts`, `ResultAckTests` (L1-only straight-through; `RecordingDispatcher` asserts `NotEqual(Guid.Empty)` on ExecutionId — flip) |

### Sampling Rate
- **Per task commit:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator|FullyQualifiedName~OutputTail|FullyQualifiedName~Inject"` (fast hermetic slice; < 30s for the touched facts)
- **Per wave merge:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`
- **Phase gate:** full suite green before `/gsd-verify-work` (req 12 bar; note the 3x close-gate run can saturate cores — `xunit.runner.json` already caps parallelism)

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` — covers req 1/3/4/5 (gate/read `out:`, SelectNext fan-out, `data:`-vs-empty, fan-out+delete, exhaustion routing). Model on `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs`.
- [ ] `tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs` — covers req 6. Model on `PostProcessConsumerFacts.cs`.
- [ ] `tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs` / `…InjectConsumerFacts.cs` / `…DeleteConsumerFacts.cs` — covers req 8/9/10. Model on the existing `Reinject/Inject/DeleteConsumerFacts`; reuse `RecoveryTestKit`.
- [ ] Extend `OrchestratorMetricsFacts.cs` — covers the D-18 two-reason trip-end counter (req 2).
- [ ] UPDATE `tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs` line ~75 + any `OutputTailFacts` Completed-EntryId assertion — req 7 (A1: `Guid.Empty` → `dr.MessageId`).
- [ ] MIGRATE `TypedResultConsumerFacts.cs` + `ResultAckTests.cs` — req 12 (two-consumer + relocation model; `ExecutionId` threaded UNCHANGED, not regenerated).
- [ ] Test doubles: REUSE `OrchestratorTestStubs` (`PresentL2`/`AbsentL2`/`InfraFaultL2`/`Context`/`Metrics`) for Pre/Post; REUSE `RecoveryTestKit` (`Db`/`Mux`/`CapturingSendProvider`+`SentMessageIds`/`Metrics`) for keeper facts. The `DispatchTestKit` (processor side) is the model for the relocate-tail facts. Framework install: none (xUnit v3 already wired).

## Security Domain

`security_enforcement` is absent from `.planning/config.json` → enabled (default). This is an internal back-end messaging subsystem with no new external attack surface (no HTTP endpoint, no auth boundary, no user input); the relevant categories are input-trust and data integrity on the bus/L2.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No auth boundary on internal bus/L2 ops; transport trust is the broker/Redis network (out of phase scope) |
| V3 Session Management | no | Stateless message processing |
| V4 Access Control | yes (provenance) | The processor `-post` consumer already does a provenance guard (`PostProcessConsumer.cs:34` drops a `DataResult` whose `ProcessorId != self`). The orchestrator Post consumer SHOULD apply an analogous guard if a `NextStepHandoff` can be mis-routed — but the orchestrator post queue is a single competing-consumer endpoint (no per-instance identity), so the analog is "the handoff's ids must be internally consistent," not a self-check. Low risk; mirror the never-log-payload discipline. |
| V5 Input Validation | yes | The relocated `Data` (string JSON) is written verbatim to `L2[data:]` and later read by the processor, which validates against its input schema (existing `ProcessorJsonSchemaValidator`). The orchestrator does NOT re-validate (it's a transport relay) — consistent with Phase 70. |
| V6 Cryptography | no | No crypto in scope; partition `PartitionGuid` uses SHA-256 only as a non-security hash for slot derivation |
| V7 Error Handling / Logging | yes | **Never log payloads/data** (the `T-70-10` discipline, enforced throughout Phase 70: `ProcessorPipeline.cs:114`, `ReinjectConsumer.cs:39` "never log Payload"). The new trip-end log (D-18) and keeper drop logs MUST log ids only, never `NextStepHandoff.Data` or `Payload`. |

### Known Threat Patterns for this stack
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Forged/mis-routed handoff or keeper message writing an arbitrary L2 blob for another lineage | Spoofing/Tampering | Self-contained contracts carry only their own ids; the processor `-post` provenance guard is the precedent (`ProcessorId != self` drop). Orchestrator-side: keep ids internally consistent; no cross-lineage write key is derivable from attacker-controlled fields beyond the envelope MessageId (which MassTransit assigns). |
| Payload leakage in logs (deserialize fragments, data JSON) | Information Disclosure | Sanitized constant log messages + ids only — verbatim Phase-70 discipline (`ProcessorPipeline.cs:113-115` emits "input deserialization failed", not `ex.Message`). |
| Silent message loss masquerading as success | Repudiation | The whole point of req 2 / D-18: the two-reason trip-end metric+log makes every trip-end falsifiable/auditable (no silent ack). |
| At-least-once duplicate replay | Tampering/DoS | By design tolerated (no dedup, no inbox) — duplicates reproduce the effect idempotently at the data layer (TTL'd writes, same key). `TypedResultConsumerFacts.Duplicate_StepCompleted_reproduces_effect_no_collapse` is the precedent invariant to preserve. |

## Sources

### Primary (HIGH confidence — read in full this session)
- `.planning/phases/71-…/71-SPEC.md` — 12 locked requirements, boundaries, constraints, acceptance criteria
- `.planning/phases/71-…/71-CONTEXT.md` — D-01..D-18 locked decisions
- `.planning/phases/70-…/70-SPEC.md` + `70-CONTEXT.md` — the processor two-consumer pattern of record
- `src/BaseProcessor.Core/Processing/OutputTail.cs` — relocate-tail structure + A1 stamp site
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — gate/read/tail/escalation idioms
- `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs` — Post shell + provenance guard
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` — `-post` runtime-bind precedent (lines 283-288)
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — `AddConsumer().ExcludeFromConfigureEndpoints()` + `AddScoped<OutputTail>` precedent
- `src/Keeper/Recovery/{RecoveryConsumerBase,Reinject,Inject,Delete}Consumer.cs`, `RecoveryEndpointBinder.cs`, `ReinjectConsumerDefinition.cs` — keeper recovery pattern (Guard, partitioner, STRLEN drop, envelope override)
- `src/Keeper/Program.cs` — keeper consumer registration + binder wiring
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` + `StepCompletedConsumer{,Definition}.cs` — the Pre front to reshape; silent-ack site (lines 68-75)
- `src/Orchestrator/Dispatch/{StepDispatcher,IStepDispatcher,StepAdvancement}.cs` — dispatch owner + pure L1 match
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` — existing `IConnectionMultiplexer` injection point (D-14)
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` — metric/tag convention for D-18
- `src/Orchestrator/Program.cs` — orchestrator wiring (result-queue competing-consumer registration)
- `src/Messaging.Contracts/{DataResult,EntryStepDispatch,IStepResult,StepCompleted,KeeperReinject,KeeperInject,KeeperDelete,IKeeperRecoverable,SourceStep}.cs` + `Projections/{L2ProjectionKeys,StepProjection}.cs` + `Configuration/RetryOptions.cs` — contracts + keys + options
- `src/BaseConsole.Core/Resilience/RetryLoop.cs` — the escalation-map helper
- `tests/BaseApi.Tests/Orchestrator/{TypedResultConsumerFacts,ResultAckTests,OrchestratorTestStubs}.cs` — migration surface + doubles
- `tests/BaseApi.Tests/Keeper/RecoveryTestKit.cs`, `Keeper/InjectConsumerFacts.cs`, `Processor/{PostProcessConsumerFacts,OutputTailFacts,PrePipelineFacts}.cs`, `Contracts/StepResultContractTests.cs` — test models + A1 assertion site
- `tests/BaseApi.Tests/BaseApi.Tests.csproj`, `.planning/config.json` — test framework + workflow flags

### Secondary / Tertiary
- None — all findings verified directly against in-repo source; no web search required (the pattern of record is the Phase-70 codebase).

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; every asset read and in active use
- Architecture: HIGH — a faithful mirror of the fully-read Phase-70 implementation; the two asymmetries (D-01/D-04 new keeper, D-18 metric) are explicitly locked
- Pitfalls: HIGH — each pitfall is a concrete divergence-from-template risk grounded in a specific read source line (STRLEN vs KeyExists, send-before-delete, cross-assembly firewall, envelope override scope, ExecutionId regeneration)

**Research date:** 2026-06-17
**Valid until:** ~2026-07-17 (30 days — stable internal codebase; no fast-moving external deps). Re-verify only if Phase 70 source or `L2ProjectionKeys`/`RetryLoop`/keeper recovery shapes change before planning.
