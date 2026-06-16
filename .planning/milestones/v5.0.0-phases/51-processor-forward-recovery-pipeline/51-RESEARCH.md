# Phase 51: Processor Forward + Recovery Pipeline - Research

**Researched:** 2026-06-11
**Domain:** .NET 8 framework-internal pipeline rewrite (`BaseProcessor.Core.Processing.ProcessorPipeline`) — Redis L2 slot-array recovery model (A18), MassTransit send routing, xUnit/NSubstitute hermetic facts
**Confidence:** HIGH (the WHAT is fully locked by A18 + CONTEXT D-01..D-10; every code claim below is read from source this session)

## Summary

Phase 51 rewrites one class — `ProcessorPipeline.cs` (currently 242 lines) — and changes one seam (`EntryStepDispatchConsumer.Consume` → pass `ctx.MessageId!.Value`). The A18 model and the Phase-50 contracts are LOCKED; this is a HOW-only phase whose entire risk surface is mapping two pseudocode blocks (FORWARD lines 146-177, RECOVERY lines 179-203 of the design doc) onto concrete call sites, while preserving the Phase-44 plain-object testability and retiring the WR-01 `finally`-unwind landmine.

The current pipeline is a single `RunAsync(EntryStepDispatch d, CancellationToken ct)` with a `try { Pre → In → Post } finally { end-delete }` structure. **It must split into `RunForwardAsync` / `RunRecoveryAsync`** dispatched by a top-level `exist L2[messageId]` check (D-07), and the `finally` end-delete must become **two explicit inline source-delete tails** (D-08) — one per pass, gated by the `REINJECT`⊻source-delete mutual exclusion. The Phase-44 stub comments at lines 140-141 and 150-151 mark exactly where the slot-array allocation-before-data write lands.

Every reusable seam already exists and is verified: `RetryLoop.ExecuteAsync` (surfaces-not-throws exhaustion), `RetryOutcome.Succeeded` (gates infra routing), `KeyAbsentException` (absent/empty→fault unification), `L2ProjectionKeys.MessageIndex/ExecutionData`, the four `Build*` keeper/result builders (with one needed change — `BuildInject` is **stale**, see Pitfall 1), and the `DispatchTestKit` fixture harness with `CapturingSendProvider`. The slot-array is a Redis HASH (`HashGetAllAsync`/`HashSetAsync`/`KeyExpireAsync`/`KeyExistsAsync` — all standard SE.Redis 2.13.1).

**Primary recommendation:** Split `RunAsync` into a thin dispatcher + `RunForwardAsync`/`RunRecoveryAsync`; add `SlotArrayOptions` mirroring `ProcessorLivenessOptions`; thread `messageId` as a method arg; replace the `finally` tail with two explicit inline tails; extend the existing 4 `Pipeline*Facts` fixtures with a `PipelineForwardFacts` + `PipelineRecoveryFacts` pair reusing `DispatchTestKit`. Fix the stale `BuildInject` to carry the Phase-50 D-08 id-set.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| FORWARD/RECOVERY branch on `exist L2[messageId]` | `ProcessorPipeline` (framework lib) | — | Plain object, no MT harness (Phase-44 Pattern 1); the consumer stays a thin metric+delegate shell |
| messageId acquisition | `EntryStepDispatchConsumer` (MT consume seam) | — | `ConsumeContext.MessageId` is the broker MessageId; only the consumer sees `ConsumeContext` (D-09) |
| Slot-array HASH I/O (`HGETALL`/`HSET`/`EXPIRE`/`EXISTS`) | `ProcessorPipeline` via `IConnectionMultiplexer` | StackExchange.Redis 2.13.1 | Same `db = redis.GetDatabase()` seam the current Post uses |
| Per-op retry + exhaustion routing | `RetryLoop`/`RetryOutcome` (`BaseConsole.Core.Resilience`) | — | Reused verbatim; `Succeeded` gates infra taxonomy |
| Send routing (orchestrator result / keeper state) | `ProcessorPipeline.SendResult`/`SendKeeper` | `ISendEndpointProvider` | Already exists; send-exhaust PROPAGATES (D-10) |
| `SlotArrayOptions` TTL config | `BaseProcessor.Core.Configuration` (new record) | `IConfiguration` "Processor" section | Mirrors `ProcessorLivenessOptions`; NOT the deleted Keeper `BackupOptions` (D-04) |
| Outer dead-letter latch (`UseMessageRetry`) | `ProcessorStartupOrchestrator` (bind site) | MassTransit | A18 "UseMessageRetry = none" reconciliation — see Open Question 1 |

---

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

**executionId provenance (across all dispatch paths)** — the slot HASH stores only `entryId` (Phase-50 D-05); `executionId` is NOT persisted.

- **D-01:** `REINJECT` and `DELETE` carry the **ORIGIN** executionId (inbound `d.ExecutionId`). Matches existing `BuildReinject`/`BuildDelete` (A1) — **no change**.
- **D-02:** `INJECT` (item `infra_entryId`/failed) and `completed` (→ orchestrator) carry a **CREATED** per-item executionId.
- **D-03:** "Created" reconciles per pass:
  - **Forward** — author mints per-item exec in In phase (`item.ExecutionId`); `completed`/`INJECT` carry that id. Matches current `BuildCompleted`/`BuildInject` — **no change** (but see Pitfall 1 re `BuildInject` field set).
  - **Recovery** — no author/item in hand (slot holds only `entryId`); the re-sent `completed` mints a **fresh** `executionId` (`NewId.NextGuid()`) at re-send time. **NEW behavior.** Safe under A16 (orchestrator does not dedup; exec is observability identity only).

**Slot-array `TTL(random)` options (SLOT-01 / Phase-50 D-07 deferral)**
- **D-04:** New options record `SlotArrayOptions` in `BaseProcessor.Core.Configuration`, bound from the `"Processor"` config section (mirrors `ProcessorLivenessOptions`' `ConfigurationKeyName` idiom). NOT the deleted Keeper `BackupOptions` location.
- **D-05:** Random TTL range `[300, 600]s` (min/max as two int-seconds config keys). Floor = `ExecutionDataTtl` default (300s) so the `L2[messageId]` marker outlives the data keys it references; ceiling = 2× for jitter.
- **D-06:** One `TTL(random)` applied to the **whole `L2[messageId]` HASH** on each slot write (allocation write + the `guid.empty` retire write both refresh it), per Phase-50 D-05.

**Pipeline restructure (FWD-01..03 / RECOV-01..03 / the REINJECT⊻source-delete invariant)**
- **D-07:** Split into `RunForwardAsync` / `RunRecoveryAsync`, dispatched by a top-level `exist L2[messageId]` existence-check in `RunAsync` (existence-check exhaustion → `REINJECT`; end — FWD-01).
- **D-08:** Explicit inline source-delete tails — drop the try/finally end-delete. Each pass owns its source-delete at the tail, gated by `REINJECT`⊻source-delete mutual exclusion. Resolves the Phase-44 **WR-01** landmine.

**messageId plumbing (the branch key)**
- **D-09:** Change the seam to `RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)`; `EntryStepDispatchConsumer.Consume` passes `ctx.MessageId!.Value`. Hermetic facts construct the pipeline directly and pass a `messageId` Guid (no MT harness).
- **D-10:** Fail-fast throw on a null `MessageId` (`InvalidOperationException`). MassTransit always sets `MessageId` on `Send`/`Publish`.

### Claude's Discretion
- **Slot-index counter semantics** — slot = ordinal of **completed** items only; `infra_messageId`-dropped + business-failed items consume **no** slot (allocation only inside the per-completed-item write block).
- **Recovery temp-list internal representation** (a local list of `(slot, entryId, outcome, errorMessage)` or equivalent).
- **Infra-marker representation** — enum vs string literal on the temp item; the literal strings `"infra_messageId"`/`"infra_entryId"` are locked by INFRA-01/02 as the taxonomy split only, not necessarily a wire field.
- **Random source** — `Random.Shared` (thread-safe) for the `[min,max]` TTL pick.
- Exact hermetic-fact decomposition proving forward + recovery flows (SC-5).

### Deferred Ideas (OUT OF SCOPE)
- 3-state Keeper recovery consumer (gate-open-only apply of `REINJECT`/`INJECT`/`DELETE`, configurable DLQ1-vs-outage exhaustion, gate-closed non-destructive consume) → **Phase 52**. Phase 51 only *sends* these keeper messages.
- Full Model-B teardown + reflection/source remnant sweep (RETIRE-03) → **Phase 53**.
- Live proof + N×GREEN triple-SHA close gate (TEST-01/02) → **Phase 54**.
- Reconciling A18's `UseMessageRetry = none` / `_error`-disabled global rule against the current Phase-44 outer-latch wiring — flagged for the planner; may be Phase-51 touch or Phase-53 teardown (see Open Question 1).
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| SLOT-01 | Post generates `entryId`, writes `L2[messageId][slot]=entryId` (TTL random) **before** data key | New `RunForwardAsync` Post block; `HashSetAsync(MessageIndex(messageId), slot, entryId)` + `KeyExpireAsync(rand)`; allocation FIRST per A18 line 165. `SlotArrayOptions` (D-04/05/06) supplies the TTL. |
| SLOT-02 | Post writes `L2[entryId]=data` after the allocation index write | Existing `StringSetAsync(ExecutionData(entryId), data, executionDataTtl)` — reorder to AFTER the slot write (current Post writes data with no prior slot write). |
| SLOT-03 | Slot retired to `guid.empty` only after `completed` confirmed-sent (send-before-retire) | Recovery pass: `SendResult(completed)` → on success `HashSetAsync(slot, Guid.Empty)` + `KeyExpireAsync(rand)`. A18 lines 191-192. |
| INFRA-01 | Allocation-write exhaust → `error_message="infra_messageId"` → item DROPPED (no send) | `RetryOutcome.Succeeded==false` on the slot write → mark item, no `SendResult`/`SendKeeper`. A18 lines 165-166, 172. |
| INFRA-02 | Data-write exhaust → `error_message="infra_entryId"` → keeper `INJECT(data, deleteEntryId)` | `Succeeded==false` on data write → `SendKeeper(BuildInject(...))`. Requires the **stale `BuildInject` fix** (Pitfall 1). A18 lines 167-168, 171. |
| FWD-01 | `NOT exist L2[messageId]` runs forward; existence-check/source-read exhaust → `REINJECT`, input intact | Top-level `KeyExistsAsync(MessageIndex)` in a `RetryLoop`; exhaust→`SendKeeper(BuildReinject(d))`; Pre-read exhaust already routes to REINJECT (existing). |
| FWD-02 | Forward dispatch per item — non-infra→orchestrator / `infra_entryId`→INJECT / `infra_messageId`→drop | The dispatch loop after the write block; branch on the per-item infra marker. A18 lines 169-172. |
| FWD-03 | Forward happy-path tail deletes source `entryId`; delete exhaust → `DELETE` | Explicit inline tail (D-08) replacing the `finally`; `KeyDeleteAsync(ExecutionData(d.EntryId))` exhaust→`SendKeeper(BuildDelete(d))`. A18 lines 173-174. |
| RECOV-01 | `exist L2[messageId]` runs recovery — read `entryIds[]`, temp list (`exists`→completed/`not-exist`→failed/L2-fail→failed+`infra_entryId`); read/exist exhaust → `REINJECT` | `RunRecoveryAsync`: `HashGetAllAsync(MessageIndex)` in RetryLoop (exhaust→REINJECT); per slot `KeyExistsAsync(ExecutionData(entryId))` in RetryLoop. A18 lines 183-189. |
| RECOV-02 | `completed`→re-send+retire(`guid.empty`); not-exist→drop; `infra_entryId`→leave slot intact | Per-temp-item dispatch loop; SLOT-03 retire on completed; no-op on not-exist; skip retire on infra. A18 lines 190-194. |
| RECOV-03 | Any `infra_entryId` → `REINJECT`, do NOT delete source (mutual exclusion); else delete source (exhaust→`DELETE`) | Recovery tail: `if (anyInfraEntryId) SendKeeper(BuildReinject(d))` else inline source-delete (D-08). A18 lines 196-201. |
</phase_requirements>

---

## Standard Stack

### Core (all already referenced — no new packages)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| StackExchange.Redis | 2.13.1 | L2 HASH + data + delete ops | Already the L2 client; HASH methods are the slot-array surface |
| MassTransit | (solution-pinned) | `ISendEndpointProvider` send routing | Already the send seam; `ConsumeContext.MessageId` is the branch key |
| MassTransit `NewId` | (transitive) | `NewId.NextGuid()` sequential GUIDs | Already used for every framework-minted id (entryId + recovery exec) |
| xUnit + NSubstitute | (solution-pinned) | Hermetic facts | The `DispatchTestKit` + `Pipeline*Facts` harness is built on these |

**No `npm install` / no new NuGet** — Phase 51 adds zero dependencies. `[VERIFIED: Directory.Packages.props line 131 = StackExchange.Redis 2.13.1; Directory.Build.props line 29 = net8.0]`

### StackExchange.Redis HASH surface (the slot-array ops)
| Op | Method | A18 site |
|----|--------|----------|
| existence-check `exist L2[messageId]` | `KeyExistsAsync(MessageIndex(messageId))` → `Task<bool>` | FWD-01 / RECOV-01 branch |
| read slot array (`HGETALL`) | `HashGetAllAsync(MessageIndex(messageId))` → `Task<HashEntry[]>` | RECOV-01 |
| allocation write (`HSET slot=entryId`) | `HashSetAsync(MessageIndex, slot, entryId)` → `Task<bool>` | SLOT-01 |
| retire (`HSET slot=guid.empty`) | `HashSetAsync(MessageIndex, slot, Guid.Empty.ToString())` | SLOT-03 |
| whole-key random TTL | `KeyExpireAsync(MessageIndex, TimeSpan.FromSeconds(rand))` | D-06 |
| per-entry exist-check | `KeyExistsAsync(ExecutionData(entryId))` | RECOV-01 temp-list |
| data write | `StringSetAsync(ExecutionData(entryId), data, executionDataTtl)` | SLOT-02 |
| source delete | `KeyDeleteAsync(ExecutionData(d.EntryId))` | FWD-03 / RECOV-03 |

`[VERIFIED: SE.Redis IDatabase surface]` `HashGetAllAsync(RedisKey, CommandFlags)→HashEntry[]`, `HashSetAsync(RedisKey, RedisValue field, RedisValue value, When, CommandFlags)→bool`, `KeyExistsAsync(RedisKey, CommandFlags)→bool`, `KeyExpireAsync(RedisKey, TimeSpan?, CommandFlags)→bool` are all standard 2.x. `[CITED: docs.dndocs.com StackExchange.Redis IDatabase]`. **Decision for the slot field/value type:** the HASH field is the int slot ordinal; value is `entryId.ToString("D")` (mirror `ExecutionData`'s `:D` format). Recovery parses `HashEntry.Name`(slot)/`.Value`(entryId guid) back.

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `HashGetAllAsync` whole-read | per-slot `HashGetAsync` loop | Whole-read is one round-trip + locked by Phase-50 D-05 ("recovery reads via one HGETALL"). Use HGETALL. |
| `Random.Shared.Next(min,max+1)` | injected `Random` field | `Random.Shared` is thread-safe and locked by Claude's-Discretion. Use it directly. |
| TTL via SE.Redis `expiry:` arg on each write | separate `KeyExpireAsync` | HASH `HashSetAsync` has no expiry param (unlike `StringSetAsync`); the whole-key TTL MUST be a separate `KeyExpireAsync` after the HSET (D-06 "one EXPIRE on the whole key"). |

---

## Architecture Patterns

### System Architecture Diagram (data flow through the rewritten pipeline)

```
EntryStepDispatch (broker)
        │  ctx.MessageId  ──(null? → throw InvalidOperationException, D-10)
        ▼
EntryStepDispatchConsumer.Consume  ── metric DispatchConsumed ─┐
        │  pipeline.RunAsync(d, messageId, ct)                 │ (thin shell; D-09)
        ▼
   ┌────────────────────────────────────────────────────────┐
   │ RunAsync: KeyExistsAsync(MessageIndex(messageId))       │
   │   RetryLoop ── exhaust ──► SendKeeper(REINJECT); end    │  (FWD-01 / RECOV-01)
   └──────┬───────────────────────────────┬─────────────────┘
   NOT exist │                      exist  │
          ▼                                ▼
 ┌─────────────────────┐        ┌──────────────────────────────┐
 │ RunForwardAsync      │        │ RunRecoveryAsync              │
 │  PRE: read L2[entry] │        │  HGETALL MessageIndex         │
 │   exhaust→REINJECT   │        │   exhaust→REINJECT            │
 │  validate input      │        │  per slot: EXISTS L2[entry]   │
 │   invalid→Failed;end │        │   exists→completed            │
 │  IN: author.Process  │        │   not-exist→failed(drop)      │
 │   throw→Step*;end    │        │   L2-fail→failed+infra_entryId│
 │  POST per completed: │        │  per temp item:               │
 │   1 entryId=NewId    │        │   completed→SendResult        │
 │   2 HSET slot=entryId│ALLOC   │     (fresh exec, D-03)        │
 │      +EXPIRE(rand)   │FIRST   │     →HSET slot=guid.empty     │
 │      fail→infra_msgId│        │      +EXPIRE(rand) (SLOT-03)  │
 │   3 SET L2[entry]=dat│DATA    │   not-exist→drop              │
 │      fail→infra_entry│2ND     │   infra_entryId→leave slot    │
 │  DISPATCH per item:  │        │  TAIL (mutual-exclusion):     │
 │   non-infra→Result   │        │   any infra_entryId           │
 │   infra_entryId→INJECT│       │     →REINJECT (NO src delete) │
 │   infra_messageId→drop│       │   else→delete L2[source]      │
 │  TAIL: delete L2[src] │        │     exhaust→DELETE            │
 │   exhaust→DELETE      │        │                              │
 └──────────────────────┘        └──────────────────────────────┘
        │                                  │
        ▼ SendResult → queue:orchestrator-result (StepCompleted/Failed)
        ▼ SendKeeper → queue:keeper-recovery (Reinject/Inject/Delete)  [consumed Phase 52]
```

### Pattern 1: Plain-object testable pipeline (Phase-44 Pattern 1 — PRESERVE)
**What:** `ProcessorPipeline` is a `sealed class` with a primary constructor; hermetic facts construct it directly and call `RunAsync` — no MassTransit harness. The consumer is a thin metric+delegate shell.
**When to use:** Every Phase-51 fact. Pass `messageId` as a method arg (D-09) so the facts stay harness-free.
**Example (current Build helper from `PipelinePostFacts`, extend with messageId):**
```csharp
// Source: tests/BaseApi.Tests/Processor/PipelinePostFacts.cs:29-33
private static ProcessorPipeline Build(
    IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
    DispatchTestKit.CapturingSendProvider send) =>
    new(redis, context, processor, send, DispatchTestKit.Retry(3), DispatchTestKit.Options(300),
        DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
// Phase 51: add a SlotArrayOptions arg to the ctor + Build; pass messageId to RunAsync.
```

### Pattern 2: Per-op retry surfaces-not-throws; `Succeeded` gates routing (RESIL-01)
**What:** Every L2 op + send wraps `RetryLoop.ExecuteAsync(op, limit, ct)`. The returned `RetryOutcome<T>.Succeeded` gates the infra route. A send-exhaust RE-THROWS (D-10).
**Example (current, verbatim — the exact idiom to repeat at each new L2 site):**
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:143-149
var write = await RetryLoop.ExecuteAsync(
    () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), item.Data, executionDataTtl), limit, ct);
if (!write.Succeeded)
{
    await SendKeeper(BuildInject(d, item), limit, ct);   // INFRA-02 route
    continue;                                             // batch NOT aborted
}
```

### Pattern 3: `KeyAbsentException` unifies absent/empty with a Redis fault
**What:** A read closure throws `KeyAbsentException` on `IsNullOrEmpty`, so an absent key and a Redis exception both become a `RetryLoop` exhaustion (one route). Reused for the recovery per-entry exist-check: a `not-exist` is a clean `false` (NOT an exception — it's a temp-item drop, not an infra route), while a Redis fault on the exist-check IS the `infra_entryId` route.
**Critical distinction (A18 lines 186-189):** in recovery, `not exist` and `L2 op fail` are DIFFERENT outcomes — do NOT unify them. `not-exist` → `failed` (drop); `L2-fail` (exhaust) → `failed + infra_entryId` (leave slot). `KeyExistsAsync` returns `false` cleanly for not-exist; only a thrown Redis exception inside the `RetryLoop` exhausts → infra. Do **not** reuse the `KeyAbsentException` throw-on-false pattern here (that pattern is Pre-read-specific where absent==fault).
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:86-91 (Pre-read — do NOT copy verbatim to recovery)
var read = await RetryLoop.ExecuteAsync(async () => {
    var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(d.EntryId));
    if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // Pre: absent==fault
    return raw.ToString();
}, limit, ct);
```

### Pattern 4: `SlotArrayOptions` mirrors `ProcessorLivenessOptions` (D-04)
**What:** A `sealed class` in `BaseProcessor.Core.Configuration` bound from the `"Processor"` section, two int-seconds props with `[ConfigurationKeyName]` and baked defaults.
**Example (mirror this verbatim shape):**
```csharp
// Source idiom: src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs:16-42
namespace BaseProcessor.Core.Configuration;
public sealed class SlotArrayOptions
{
    [ConfigurationKeyName("SlotArrayTtlMin")] public int SlotArrayTtlMinSeconds { get; set; } = 300;  // D-05 floor = ExecutionDataTtl default
    [ConfigurationKeyName("SlotArrayTtlMax")] public int SlotArrayTtlMaxSeconds { get; set; } = 600;  // D-05 ceiling = 2x
}
// Pick: TimeSpan.FromSeconds(Random.Shared.Next(min, max + 1))  — Claude's-Discretion: Random.Shared.
// Register in BaseProcessorServiceCollectionExtensions next to the existing ProcessorLivenessOptions bind.
```
Note: key names are Claude's discretion — confirm against the exact `"Processor"` section keys at plan time (the bound keys drop the `Seconds` suffix per the `ProcessorLivenessOptions` precedent, e.g. `Interval`/`Ttl`/`ExecutionDataTtl`).

### Recommended Structure (files touched)
```
src/BaseProcessor.Core/
├── Configuration/
│   ├── ProcessorLivenessOptions.cs      # unchanged — precedent + ExecutionDataTtl floor ref
│   └── SlotArrayOptions.cs              # NEW (D-04)
├── Processing/
│   ├── ProcessorPipeline.cs            # REWRITE: split RunForwardAsync/RunRecoveryAsync + 2 inline tails
│   ├── EntryStepDispatchConsumer.cs    # 1-line change: pass ctx.MessageId!.Value (D-09/D-10)
│   ├── BaseProcessor.cs                # unchanged
│   ├── ProcessItem.cs / ProcessOutcome.cs  # unchanged (forward iterates these)
│   └── ...
├── DependencyInjection/
│   └── BaseProcessorServiceCollectionExtensions.cs  # register SlotArrayOptions bind
└── Resilience/KeyAbsentException.cs    # unchanged (reused)

tests/BaseApi.Tests/Processor/
├── DispatchTestKit.cs                 # EXTEND: SlotArray HASH fakes, messageId in Dispatch/Build, exist-check fakes
├── PipelinePreFacts.cs / PipelineInFacts.cs / PipelinePostFacts.cs / PipelineEndDeleteFacts.cs  # adapt to new signature
├── PipelineForwardFacts.cs            # NEW — proves the FWD pass + every exhaustion branch (SC-5)
└── PipelineRecoveryFacts.cs          # NEW — proves the RECOV pass + mutual-exclusion (SC-5)
```

### Anti-Patterns to Avoid
- **Keeping the `finally` end-delete (WR-01 landmine).** A18 + D-08 require explicit inline tails. The `finally` at `ProcessorPipeline.cs:167-177` fires during a send-exhaustion unwind, which could delete the input before the bus replays it. **Verified the try/finally still exists** (lines 76, 167-177) — it MUST be removed.
- **Unifying recovery `not-exist` with `L2-fail`.** They route differently (Pattern 3). Do not copy the Pre-read `KeyAbsentException` idiom into recovery.
- **Allocating a slot for non-completed items.** Slot = completed-item ordinal only (Claude's-Discretion). `infra_messageId`-dropped + business-failed items consume no slot.
- **Writing data before the slot.** SLOT-01 mandates allocation-before-data; the current Post writes data with no slot — reorder.
- **Applying TTL via the HASH set call.** `HashSetAsync` has no expiry arg; use a separate `KeyExpireAsync` (D-06).

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-op bounded retry + exhaustion surfacing | A custom try/catch loop per L2 site | `RetryLoop.ExecuteAsync` / `RetryOutcome` | One A3-semantics place; `Succeeded` gates routing; already used at every existing site |
| Sequential GUID minting | `Guid.NewGuid()` | `NewId.NextGuid()` | Project convention (DB-friendly sequential); used for every framework-minted id |
| L2 key strings | String interpolation | `L2ProjectionKeys.MessageIndex` / `.ExecutionData` | Single source of truth, golden-pinned (Phase 50) |
| Keeper/result envelopes + id-sets | New record construction inline | `BuildReinject`/`BuildInject`/`BuildDelete`/`BuildCompleted` | A1 id-sets already encoded; reuse (fix `BuildInject`, Pitfall 1) |
| Send capture in tests | A bespoke mock | `DispatchTestKit.CapturingSendProvider` | Already splits `IStepResult` (`Sent`) vs `IKeeperRecoverable` (`SentKeeper`), order-preserving |
| Options binding | Manual `IConfiguration.GetValue` | `[ConfigurationKeyName]` record + `services.Configure` | `ProcessorLivenessOptions` precedent |

**Key insight:** Phase 51 builds NO new infrastructure. The entire phase is re-sequencing existing seams (`RetryLoop` + `L2ProjectionKeys` + `Build*` + `Send*`) into the two A18 passes plus one new options record. Every reusable seam is verified present this session.

---

## Runtime State Inventory

> This is a framework-internal pipeline rewrite, not a rename/migration. No stored data, OS-registered state, secrets, or build artifacts carry a renamed string.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | The slot-array HASH `skp:msg:{messageId}` is NEW (written by this phase). No pre-existing data uses it (key builder shipped Phase 50, unused). The data key `skp:data:{entryId}` is unchanged. | None — net-new write path |
| Live service config | The `"Processor"` config section gains two optional keys (`SlotArrayTtlMin/Max`) with baked defaults — absent config is tolerated. compose/appsettings override is optional (close-gate concern, Phase 54). | None blocking; defaults work |
| OS-registered state | None — verified: no Task Scheduler / systemd / pm2 references in `BaseProcessor.Core`. | None |
| Secrets/env vars | None — no secret key references the pipeline. | None |
| Build artifacts | None — no egg-info / compiled-name coupling in .NET; the rewrite is source-only within `BaseProcessor.Core`. | None |

---

## Common Pitfalls

### Pitfall 1: `BuildInject` is STALE — does not carry the Phase-50 D-08 id-set
**What goes wrong:** `KeeperInject` (Phase 50) carries `EntryId` + `Data` + `DeleteEntryId` (verified `src/Messaging.Contracts/KeeperInject.cs:12-14`), but the current `BuildInject` (`ProcessorPipeline.cs:240-241`) only sets `CorrelationId` + `ExecutionId` — it never populates the three new fields. INFRA-02 requires `INJECT` to carry `(data, deleteEntryId)`.
**Why it happens:** Phase 50 reshaped the contract but stubbed the pipeline; the builder was left compile-only.
**How to avoid:** Rewrite `BuildInject(d, item, entryId, sourceEntryId)` to set `EntryId = entryId` (the allocation), `Data = item.Data`, `DeleteEntryId = d.EntryId` (source), `ExecutionId = item.ExecutionId` (D-02/D-03 created exec). This is **new behavior the planner must task explicitly** — not a "no change."
**Warning signs:** A `KeeperInject` arriving at the Phase-52 keeper with `EntryId == Guid.Empty` / `Data == ""`.

### Pitfall 2: The `finally` end-delete (WR-01) must be removed, not refactored in place
**What goes wrong:** Leaving the `try { } finally { if (readSucceeded) delete }` (lines 76, 167-177) means the source delete fires on EVERY read-succeeded exit — including a send-exhaustion unwind — which is exactly the WR-01 race (delete input before bus replay).
**Why it happens:** It's the load-bearing structure of the current pipeline; a "split the method" refactor that keeps the `finally` silently re-introduces the bug.
**How to avoid:** D-08 — each pass ends with an EXPLICIT inline source-delete tail reached ONLY on the no-REINJECT happy path. Forward: after the dispatch loop. Recovery: in the `else` of the mutual-exclusion. A REINJECT path `return`s BEFORE the tail (never deletes). **Confirmed the try/finally still exists this session** — it is the thing to delete.
**Warning signs:** any `finally` block left in `RunForwardAsync`/`RunRecoveryAsync`.

### Pitfall 3: Allocation-before-data ordering inverts the current Post
**What goes wrong:** The current Post does `entryId = NewId.NextGuid(); StringSetAsync(data)` with NO slot write (lines 142-144). Naively adding the slot write AFTER the data write violates SLOT-01 (allocation-before-data) and re-opens the orphan-leak the A18 ordering prevents.
**Why it happens:** The slot write is net-new; the obvious insertion point (after the existing data write) is wrong.
**How to avoid:** Order = (1) `entryId = NewId.NextGuid()`; (2) `HashSetAsync(slot=entryId)` + `KeyExpireAsync(rand)` — exhaust→`infra_messageId`→drop+`continue`; (3) `StringSetAsync(data)` — exhaust→`infra_entryId`→INJECT. A18 lines 164-168.
**Warning signs:** a data write with no preceding slot HSET in the same iteration.

### Pitfall 4: Recovery `completed` mints a FRESH executionId (D-03) — the one provenance change
**What goes wrong:** Reusing `d.ExecutionId` (origin) or a stored exec for the recovery re-sent `completed`. The slot holds only `entryId` (no exec persisted), so recovery has no origin per-item exec.
**Why it happens:** Forward `completed` carries `item.ExecutionId` (author-minted) — copying that pattern fails because recovery has no `item`.
**How to avoid:** Recovery `completed` → `BuildCompleted(d, NewId.NextGuid(), entryId)` (fresh exec at re-send time). Safe under A16 (orchestrator does not dedup; exec is observability identity only). This is the ONLY id-provenance behavior change in the phase.
**Warning signs:** recovery `StepCompleted.ExecutionId == d.ExecutionId` or `== Guid.Empty`.

### Pitfall 5: `MessageId` is `Guid?` on `ConsumeContext` — D-10 fail-fast
**What goes wrong:** `ctx.MessageId` is nullable; a `.Value` without a guard could NRE, or worse, a silent `Guid.Empty` synthesizes a bogus slot-array key colliding across messages.
**How to avoid:** D-10 — throw `InvalidOperationException` on null `MessageId` in `Consume` (MassTransit always sets it on Send/Publish, so null is a contract violation). Pass `ctx.MessageId!.Value` only after the guard, OR let the guard be the `!`.
**Warning signs:** `skp:msg:00000000-0000-0000-0000-000000000000` in Redis.

### Pitfall 6: HASH retire write must refresh the whole-key TTL too (D-06)
**What goes wrong:** The `guid.empty` retire HSET lands but no `KeyExpireAsync` follows, so the marker key reverts to its earlier (or no) expiry — drifting the close-gate net-zero (Phase 54).
**How to avoid:** Both the allocation write AND the retire write are followed by `KeyExpireAsync(MessageIndex, rand)` (D-06 "both refresh it").
**Warning signs:** a `skp:msg:*` key with no TTL surviving the close-gate scan.

---

## Validation Architecture

> nyquist_validation: treated as ENABLED (no `.planning/config.json` override found disabling it). Hermetic facts are the Phase-51 gate; live proof is Phase 54 (TEST-01/02, OUT OF SCOPE here).

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit + NSubstitute (`tests/BaseApi.Tests`) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (existing) |
| Quick run command | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~Pipeline" -c Debug` |
| Full suite command | `dotnet test tests/BaseApi.Tests --filter-not-trait "Category=RealStack" -c Release` |
| Build gate | `dotnet build SK_P.sln -c Release` AND `-c Debug` (TreatWarningsAsErrors=true globally — 0-warning is a hard gate, `Directory.Build.props:35`) |

### Phase Requirements → Test Map
| Req | Behavior | Test Type | Hermetic assertion | File |
|-----|----------|-----------|--------------------|------|
| FWD-01 | `NOT exist L2[messageId]` → forward; exist-check exhaust→REINJECT | unit | exist-check fake returns false → forward runs; fault fake → `Single(SentKeeper.OfType<KeeperReinject>())` | PipelineForwardFacts ❌ Wave 0 |
| SLOT-01/02 | slot write BEFORE data; ordering | unit | assert `db.Received()` call order: `HashSetAsync` before `StringSetAsync` (NSubstitute `Received.InOrder`) | PipelineForwardFacts ❌ |
| SLOT-03 | retire only after send | unit | recovery completed: assert `SendResult` recorded AND `HashSetAsync(slot, guid.empty)` received; send-fail fake → no retire | PipelineRecoveryFacts ❌ |
| INFRA-01 | alloc-write exhaust→drop | unit | slot-write fault fake → no `SentKeeper`, no `Sent` for that item (`infra_messageId` dropped) | PipelineForwardFacts ❌ |
| INFRA-02 | data-write exhaust→INJECT(data,deleteEntryId) | unit | data-write fault → `Single(SentKeeper.OfType<KeeperInject>())` with `Data!=""` && `DeleteEntryId==d.EntryId` | PipelineForwardFacts ❌ |
| FWD-02 | per-item dispatch routing | unit | mixed items → assert each lands on the right channel (Result / INJECT / dropped) | PipelineForwardFacts ❌ |
| FWD-03 | happy tail deletes source; exhaust→DELETE | unit | delete-ok fake → `db.Received().KeyDeleteAsync(ExecutionData(d.EntryId))`; delete-fault → `Single(OfType<KeeperDelete>())` | PipelineForwardFacts ❌ |
| RECOV-01 | exist→recovery; temp-list outcomes; read exhaust→REINJECT | unit | HGETALL fake with entries → per-slot exist routing; HGETALL fault → REINJECT | PipelineRecoveryFacts ❌ |
| RECOV-02 | completed→send+retire / not-exist→drop / infra→leave slot | unit | 3-entry HASH (exists/absent/fault) → 1 SendResult+retire, 0 for absent, slot intact for fault | PipelineRecoveryFacts ❌ |
| RECOV-03 | any infra→REINJECT no-delete; else delete | unit | with-infra fake → `Single(OfType<KeeperReinject>())` && `DidNotReceive().KeyDeleteAsync`; no-infra → delete received | PipelineRecoveryFacts ❌ |
| D-09/D-10 | messageId plumbing + null fail-fast | unit | consumer-level: null MessageId → `Assert.ThrowsAsync<InvalidOperationException>` | a consumer fact ❌ |
| D-03 | recovery completed mints fresh exec | unit | recovery completed `StepCompleted.ExecutionId != Guid.Empty && != d.ExecutionId` | PipelineRecoveryFacts ❌ |
| D-04/05/06 | SlotArrayOptions bind + TTL on writes | unit | options bind golden + `db.Received().KeyExpireAsync(MessageIndex, In(300..600))` on both writes | a SlotArray fact ❌ |

### Sampling Rate
- **Per task commit:** `dotnet test --filter "FullyQualifiedName~Pipeline" -c Debug` + `dotnet build -c Debug` (0-warning)
- **Per wave merge:** full hermetic suite (`--filter-not-trait Category=RealStack`) -c Release
- **Phase gate:** full suite green + Release+Debug 0-warning (SC-5). Live proof deferred to Phase 54.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Processor/PipelineForwardFacts.cs` — NEW; covers FWD-01/02/03, SLOT-01/02, INFRA-01/02
- [ ] `tests/BaseApi.Tests/Processor/PipelineRecoveryFacts.cs` — NEW; covers RECOV-01/02/03, SLOT-03, D-03
- [ ] `DispatchTestKit.cs` extensions — slot-array HASH fakes (`HashGetAllAsync`/`HashSetAsync`/`KeyExistsAsync` returns + fault variants), a `messageId` param on `Build`/`Dispatch`, an exist/absent/fault exist-check fake matrix
- [ ] A `SlotArrayOptions` bind/golden fact (mirrors the liveness-options bind test if one exists)
- [ ] A consumer-level D-10 null-MessageId fact (the only fact needing a `ConsumeContext` — use an NSubstitute `ConsumeContext` with `MessageId` returning null)
- [ ] Adapt the existing `PipelinePre/In/Post/EndDeleteFacts` to the new `RunAsync(d, messageId, ct)` signature + the removed `finally` (the EndDelete facts move into the forward-tail facts; some assertions change from "finally runs" to "inline tail runs")

---

## State of the Art

| Old (Model B / Phase-44) | New (A18 / Phase-51) | Impact |
|--------------------------|----------------------|--------|
| Single `RunAsync` Pre→In→Post + `finally` end-delete | `RunForwardAsync`/`RunRecoveryAsync` split + 2 inline tails | WR-01 retired; recovery branch added |
| Post: `entryId=NewId; SET data` (no slot) | Post: `entryId; HSET slot; EXPIRE; SET data` (alloc-first) | SLOT-01/02; orphan-leak closed |
| `finally { if(readSucceeded) delete }` | explicit inline source-delete, REINJECT⊻delete | D-08; mutual exclusion |
| `BuildInject(d, item)` carries only exec | `BuildInject` carries `EntryId`/`Data`/`DeleteEntryId` | Pitfall 1 — stale builder fixed |
| No messageId in pipeline | `messageId` method arg (D-09) | branch key plumbed |

**Deprecated/outdated (already removed in Phase 50, do not re-introduce):** `KeeperUpdate`/`KeeperCleanup`, the composite backup key, `BackupOptions`. The Phase-50 stub comments at `ProcessorPipeline.cs:140-141,150-151` are the markers — replace them, don't leave them.

---

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | HASH field = int slot ordinal; value = `entryId.ToString("D")`; retire value = `Guid.Empty.ToString()` | Standard Stack | LOW — wire format is internal to the processor (write + recovery-read are both in this class); Phase 52 keeper never reads the HASH. Confirm `Guid.Empty` representation choice at plan time. |
| A2 | `SlotArrayOptions` config keys are `SlotArrayTtlMin`/`SlotArrayTtlMax` (drop-`Seconds` precedent) | Pattern 4 | LOW — Claude's-Discretion; planner picks final names; only consistency with `ProcessorLivenessOptions` matters. |
| A3 | The existing `PipelineEndDeleteFacts` assertions fold into the new forward/recovery tail facts | Validation | LOW — test reorg, not behavior; planner decides keep-vs-merge. |
| A4 | A18 "UseMessageRetry = none" is NOT applied in Phase 51 (left to planner per CONTEXT deferred) | Open Q1 | MEDIUM — if the planner applies it here, the outer dead-letter latch at `ProcessorStartupOrchestrator.cs:180` changes; see Open Question 1. |

---

## Open Questions

1. **A18 `UseMessageRetry = none` / `_error`-disabled global rule vs the current Phase-44 outer latch.**
   - What we know: A18 (design line 142) says `_error` routing disabled, `UseMessageRetry = none` for the v5 path. The current processor binds `cfg.UseMessageRetry(r => r.Immediate(retryLimit))` as the OUTER dead-letter latch (`ProcessorStartupOrchestrator.cs:180`), and `MessagingServiceCollectionExtensions` wires the shared `skp-dlq-1` consolidated `_error` move + `GenerateFaultFilter`. The send-exhaust PROPAGATE (D-10) currently RELIES on this latch to dead-letter.
   - What's unclear: whether removing the outer `UseMessageRetry` (and the `_error` move) is a Phase-51 pipeline touch or a Phase-53 teardown item. CONTEXT explicitly flags this as planner-discretion ("may be a Phase-51 touch or a Phase-53 teardown item").
   - Recommendation: **Keep the outer latch in Phase 51** (do NOT remove `UseMessageRetry`). Rationale: (a) the send-exhaust PROPAGATE semantics (D-10) still need a dead-letter sink; (b) A18's "UseMessageRetry = none" describes the END-STATE after the whole v5 path lands, and removing it now would orphan exhausted sends with no `_error` target before Phase 52/53; (c) the Keeper's whole recovery model still rides the `Fault<T>` pub/sub stream (the `GenerateFaultFilter` comment warns removing it breaks Phases 33-35, though those are v3.7 — verify relevance under v5). Flag for the planner to confirm with the user; treat as `[ASSUMED]`.

2. **Slot-array HASH field ordering on recovery read.**
   - What we know: `HashGetAllAsync` returns `HashEntry[]` in unspecified order; the temp-list is built per slot, and the slot ordinal is the HASH field name.
   - What's unclear: whether recovery dispatch must preserve slot order (it iterates a temp list; A18 does not require ordered re-send — orchestrator advances off the step graph + entryId per A16, not order).
   - Recommendation: order-independent is safe (A16). No sort needed. LOW risk.

---

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK | build/test | ✓ (solution targets net8.0) | net8.0 | — |
| StackExchange.Redis | L2 HASH ops | ✓ (referenced) | 2.13.1 | — |
| MassTransit / NewId | send + id mint | ✓ (referenced) | solution-pinned | — |
| xUnit / NSubstitute | hermetic facts | ✓ (referenced) | solution-pinned | — |
| Redis server | hermetic facts | NOT NEEDED | — | NSubstitute `IConnectionMultiplexer` (no live Redis — Phase-44 pattern) |
| RabbitMQ | hermetic facts | NOT NEEDED | — | `CapturingSendProvider` (no live broker) |

**No missing dependencies.** Phase 51 is fully hermetic-testable with zero live infrastructure (the Phase-44 plain-object pattern). Live infra is a Phase-54 concern.

## Project Constraints (from build config — no CLAUDE.md present)

- **0-warning hard gate:** `TreatWarningsAsErrors=true` SOLUTION-WIDE (`Directory.Build.props:35`); `WarningsAsErrors` promotes RMG007/012/020/089. Build must pass Release AND Debug. `[VERIFIED: Directory.Build.props]`
- **Nullable enabled** (`Directory.Build.props:30`) — the `ctx.MessageId!.Value` D-10 guard must satisfy the nullable analyzer (throw-then-`!` or a null-check that the flow-analysis understands).
- **No CLAUDE.md** found at repo root or any parent. No project-skill `rules/*.md` directories (`.claude/skills`, `.agents/skills`) found. `[VERIFIED: Glob no matches]`

## Sources

### Primary (HIGH confidence — read this session)
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` lines 120-227 — A18 LOCKED source of truth (FORWARD 146-177, RECOVERY 179-203, Invariants 223-227)
- `.planning/phases/51-processor-forward-recovery-pipeline/51-CONTEXT.md` — D-01..D-10 locked decisions
- `.planning/phases/50-contracts-slot-array-l2-key-reshape/50-CONTEXT.md` — Phase-50 contract decisions (D-04/05/08)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — the rewrite target (current Pre/In/Post/finally + Build* + Send*)
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — the seam (line 35 `RunAsync` call)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `MessageIndex`/`ExecutionData` (lines 42, 48)
- `src/Messaging.Contracts/KeeperInject.cs` — D-08 id-set (lines 12-14) — proves `BuildInject` is stale
- `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` — `SlotArrayOptions` precedent + TTL floor
- `src/BaseConsole.Core/Resilience/RetryLoop.cs` — `RetryLoop`/`RetryOutcome`
- `src/BaseProcessor.Core/Resilience/KeyAbsentException.cs` — absent/empty sentinel
- `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs` lines 170-185 — `UseMessageRetry` outer latch
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs` — `skp-dlq-1` / `GenerateFaultFilter` wiring
- `tests/BaseApi.Tests/Processor/{DispatchTestKit,PipelinePre,PipelinePost,PipelineEndDelete}Facts.cs` — the fixture harness to extend
- `.planning/REQUIREMENTS.md` — SLOT/INFRA/FWD/RECOV req text
- `.planning/ROADMAP.md` Phase 51 — the 5 success criteria
- `Directory.Build.props` / `Directory.Packages.props` — net8.0, 0-warning, SE.Redis 2.13.1

### Secondary (MEDIUM confidence)
- [StackExchange.Redis IDatabase API (DNDocs)](https://docs.dndocs.com/n/StackExchange.Redis/2.2.88/api/StackExchange.Redis.IDatabase.html) — HASH/key method signatures (2.x stable surface)
- [StackExchange.Redis Basics](https://stackexchange.github.io/StackExchange.Redis/Basics.html) — HashEntry/HashSet usage

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — zero new deps; every seam read from source; SE.Redis HASH surface confirmed
- Architecture (the two passes): HIGH — A18 + CONTEXT fully lock the WHAT; pseudocode mapped line-by-line to call sites
- Pitfalls: HIGH — Pitfall 1 (stale `BuildInject`) and Pitfall 2 (finally still present) verified against current source this session
- UseMessageRetry reconciliation: MEDIUM — deliberately left as Open Question 1 per CONTEXT planner-discretion flag

**Research date:** 2026-06-11
**Valid until:** 2026-07-11 (stable internal codebase; A18 LOCKED). Re-verify only if Phase 50/the contract layer changes.
