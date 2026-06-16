# Phase 43: Message Contracts & L2 Key Reshape - Research

**Researched:** 2026-06-08
**Domain:** C#/.NET wire-contract reshape (MassTransit + System.Text.Json envelopes; StackExchange.Redis L2 key builders)
**Confidence:** HIGH (every shape, signature, and reference below is read directly from the codebase this session)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Reshape `EntryStepDispatch` / `ExecutionResult` **in place** in Phase 43 (drop wire `H`, `entryId` string→`Guid`). `H`/`flag[H]` CAS dedup (RETIRE-01) and `entryId`-as-64-hex content-addressing (RETIRE-02) are **coupled to the field removal and land in Phase 43** — not deferrable.
- **D-02:** Phase 48 shrinks to RETIRE-03 (reactive `Fault<T>` recovery + `keeper-dlq`) + final remnant sweep.
- **D-03:** Bleed boundary = **"dead-machinery removal + straight-through."** Remove `flag[H]`/CAS, content-addressing, N×M manifest fan-out; adapt processor + orchestrator consumers to the **simplest compile-and-pass** behavior (single result, no dedup, no manifest). Do **NOT** rewrite to v4 behavior — Pre/In/Post pipeline = Phase 44; 5-state recovery consumer + per-item orchestrator consume = Phase 46.
- **D-04:** `EntryStepDispatch` (single) carries exactly the six ids; `H` removed from record + `IExecutionCorrelated`. Golden test pins shape + asserts `H` absent (SC-1).
- **D-05:** `entryId` is a `Guid` everywhere (dispatch, all four result records, `IExecutionCorrelated`). `Guid.Empty` = source-step sentinel.
- **D-06 (REVISED):** Single `ExecutionResult` **REPLACED by four typed records** — `StepCompleted`, `StepFailed`, `StepCancelled`, `StepProcessing`, each `: IStepResult : IExecutionCorrelated`, carrying the six ids. `ExecutionResult.cs` removed. Golden test pins all four + asserts `H` absent.
  - **D-06a:** `entryId` seeding done BY THE CONTRACTS — `StepCompleted` carries the real data-key `EntryId`; `StepFailed`/`StepCancelled`/`StepProcessing` hard-default `EntryId = Guid.Empty`.
  - **D-06b:** `StepFailed` carries `ErrorMessage`; `StepCancelled` carries `CancellationMessage`; `StepCompleted`/`StepProcessing` carry neither.
  - **D-06c:** `public interface IStepResult : IExecutionCorrelated {}` marker — groups the four; existing `InboundExecutionScopeConsumeFilter` (keyed on `IExecutionCorrelated`) covers them unchanged.
  - **D-06d:** `StepOutcome` demoted off the wire (no longer a wire field). Survives as orchestrator-internal advancement vocab + per-type consumer knob. Stays in `Messaging.Contracts`.
  - **D-06e:** `TypedResultConsumer<TMessage>` consumer pattern is **Phase 46** — recorded as rationale, NOT built in 43.
  - **D-06f:** Phase 44 send-side ripple — recorded, NOT built in 43.
- **D-07:** A **single shared predicate** recognizes the source-step sentinel (`Guid.Empty`). Lives in `Messaging.Contracts`.
- **D-08:** Data key — `L2ProjectionKeys.ExecutionData(Guid entryId) => skp:data:{entryId:D}`, **no TTL**. Removes the content-addressed `ExecutionData(string)` + transitional `ExecutionData(Guid)` overloads. Golden test pins it.
- **D-09:** Composite backup key — `L2ProjectionKeys.CompositeBackup(corr, wf, proc, exec) => skp:{correlationId:D}:{workFlowId:D}:{ProcessorId:D}:{executionId:D}`. `skp:`-prefixed (deliberate divergence from the doc's bare notation). Golden test pins it.
- **D-10:** Composite backup TTL — configurable in **days** via new options class (`BackupOptions { TtlDays = 2 }`), bound from appsettings, mirroring `ProbeOptions`. Default 2 days. Crash-backstop only.
- **D-11:** Five Keeper records in `Messaging.Contracts` — `UPDATE` (+`validatedData`), `REINJECT`, `INJECT`, `DELETE`, `CLEANUP`. All five carry `{correlationId, workFlowId, stepId, ProcessorId, executionId}`; `REINJECT`/`DELETE` additionally carry `entryId`; `UPDATE` additionally carries `validatedData`.
- **D-12:** All five implement marker `IKeeperRecoverable` exposing the partition 4-tuple `correlationId / workFlowId / ProcessorId / executionId`. `stepId` rides as a plain property, NOT part of the partition key.
- **D-13:** New queue const `KeeperQueues.Recovery = "keeper-recovery"`. The v3.7 `FaultRecovery`/`DeadLetter` consts are retired in Phases 47/48, **not here**.

### Claude's Discretion
- Exact naming/shape of the D-07 sentinel helper (e.g. static `SourceStep.IsSource(Guid)` vs extension method) — only requirement: single referenced predicate.
- Exact member names on `IKeeperRecoverable`.
- Whether the five Keeper contracts also implement `ICorrelated` (leaning **yes** for log-scope propagation). They cannot implement `IExecutionCorrelated` (it now requires `entryId`, which `UPDATE`/`INJECT`/`CLEANUP` lack).
- The appsettings section name + binding wiring for `BackupOptions`.
- Golden-test file organization.

### Deferred Ideas (OUT OF SCOPE)
- **ROADMAP/REQUIREMENTS reconciliation (doc, not code).** D-01/D-02 move RETIRE-01/02 into Phase 43; ROADMAP Phase 43 ("just vocabulary"), Phase 48 descriptions, and the RETIRE-01/02 traceability rows should be reconciled. Surface during planning; apply as a docs update (user-owned).
- **`keeper-fault-recovery` / `keeper-dlq` queue-const removal** → Phases 47/48, not 43.
- **Durable (non-L2) recovery backup** — milestone-deferred.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| MSG-01 | `EntryStepDispatch` + result contract(s) carry the six ids, no `H` | §"Exact Current Shapes" (EntryStepDispatch, the four `Step*` records), §"Validation Architecture" SC-1 shape-pinning tests |
| MSG-02 | `entryId` is a GUID; `Guid.Empty` = source sentinel (one shared predicate) | §"Exact Current Shapes" (`entryId` string→Guid ripple), §D-07 sentinel helper, §"Validation Architecture" SC-2 |
| MSG-03 | Five Keeper contracts exist, each with its id set | §"New Contracts to Create", §IKeeperRecoverable, §"Validation Architecture" SC-3; L2 key builders D-08/D-09/D-10 for SC-4 |
</phase_requirements>

## Summary

Phase 43 reshapes the **shared wire vocabulary** of the v4.0.0 milestone. There is no new feature behavior here: the change is type-and-field surgery on a small set of `sealed record` contracts in `Messaging.Contracts`, plus the *minimum* "straight-through" adaptation of five consumer/dispatcher files so the solution compiles and the existing happy path still flows. The reshape is mechanically forced — once `H` leaves `IExecutionCorrelated` and `entryId` becomes a `Guid`, every site that reads `.H`, builds a content-addressed `flag[H]` key, hashes a manifest, or treats `EntryId` as a string stops compiling. The planner's job is to make each forced edit a concrete grep-verifiable task and to keep the "dead-machinery removal" (RETIRE-01/02, coupled in per D-01) strictly separate from the "rewrite to v4 behavior" (Phase 44/46, OUT of scope).

The codebase already has every precedent this phase needs: `L2ProjectionKeys` is the established single-source-of-truth key builder with a golden-pinning test (`L2ProjectionKeysTests`, Phase 22); `ProbeOptions` is the exact template for the new `BackupOptions { TtlDays }` appsettings-bound class with a bound-invariant test (`ProbeOptionsBoundTests`); `EntryStepDispatchTests` / `ExecutionResultContractTests` are the templates for contract shape-pinning; and `IExecutionCorrelated : ICorrelated` is the layering the five Keeper contracts + `IStepResult` + `IKeeperRecoverable` follow. The test project is **xUnit v3 under Microsoft.Testing.Platform**; contract/golden tests live in `tests/BaseApi.Tests/` (notably `Contracts/` and `Features/Orchestration/Projection/`).

**Primary recommendation:** Treat this as three independent contract-layer waves (1: interfaces + records + key builders + options; 2: golden/contract tests pinning every new shape; 3: straight-through consumer adaptation) where Wave 3 is pure deletion-of-dead-machinery plus the smallest compile-fix. Pin everything with xUnit v3 golden tests modeled byte-for-byte on `L2ProjectionKeysTests` and `ExecutionResultContractTests`. The single highest-risk ripple is `ExecutionLogScope.BuildState` / `InboundExecutionScopeConsumeFilter`, where `EntryId` flips from a `string` (null/empty skip) to a `Guid` (`Guid.Empty` skip) — get the source-sentinel predicate (D-07) used there so the skip rule lives in one place.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Wire contract shape (six ids, no H, Guid entryId) | `Messaging.Contracts` (shared leaf) | — | Single source of truth consumed by orchestrator + processor + keeper; no project may re-declare the shape |
| Source-step sentinel predicate (D-07) | `Messaging.Contracts` | — | Must be the ONE referenced predicate so every consumer branches off it, not ad-hoc `== Guid.Empty` |
| L2 key strings (data + composite backup) | `Messaging.Contracts.Projections.L2ProjectionKeys` | — | Established Phase 21/22 single-source-of-truth; both the writer (processor) and reader (keeper) forward to it |
| Backup TTL config | `BackupOptions` (Keeper-side or Messaging.Contracts) + appsettings binding | DI in each host's `Program.cs` | Mirrors `ProbeOptions` precedent; TTL is a Keeper-owned knob (composite backup is written by Keeper `UPDATE`) |
| Result routing by type (StepCompleted/Failed/…) | Orchestrator (Phase 46) | — | OUT OF SCOPE in 43 — typed-consumer routing is the *rationale* for the four-record shape, not built here |
| Pre/In/Post processor pipeline | BaseProcessor.Core (Phase 44) | — | OUT OF SCOPE in 43 — processor is adapted straight-through only |

## Standard Stack

This phase adds **no new dependencies**. It uses the libraries already on the contracts + test path.

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MassTransit | (existing CPM pin) | Bus envelope serialization of the records | Already the project's bus; records are MassTransit message contracts (Send, not Publish) |
| System.Text.Json | BCL (net8/9) | Default STJ envelope serialization; golden round-trip in tests | Project convention: **no `[JsonPropertyName]`**, no `JsonStringEnumConverter` (enums serialize as int) |
| StackExchange.Redis | (existing CPM pin) | `Guid`/`RedisKey` typing on the L2 key builders' call sites | Already the L2 client |
| Microsoft.Extensions.Options | BCL | `BackupOptions` binding (`Configure<BackupOptions>(GetSection(...))`) | Exact `ProbeOptions` precedent |
| xunit.v3 / xunit.v3.assert | (existing CPM pin) | Golden/contract pinning tests | The test project's framework (MTP runner) |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Four typed records (D-06) | Single `ExecutionResult(StepOutcome)` (the LOCKED doc's original) | **LOCKED OUT by D-06** — user authorized the four-record divergence on 2026-06-08 to enable no-if/else typed routing. Do NOT propose the single record. |
| `Guid` entryId | `string` 64-hex (v3.x content address) | **LOCKED OUT by D-05/RETIRE-02** — content-addressing is removed, not relocated. |

**Installation:** none — no `dotnet add package`.

**Version verification:** N/A — no new packages. All versions resolve from `Directory.Packages.props` (CPM; no `Version=` attributes per Phase 1 D-05/D-06). [VERIFIED: BaseApi.Tests.csproj uses CPM, xunit.v3 3.2.2 under MTP].

## Architecture Patterns

### System Architecture Diagram (the reshaped vocabulary in context)

```
                         Messaging.Contracts (shared leaf — the reshape surface)
                         ┌─────────────────────────────────────────────────────┐
                         │ ICorrelated ─ IExecutionCorrelated (drop H,          │
                         │                  entryId string→Guid)                 │
                         │      ├── EntryStepDispatch (six ids, no H)            │
                         │      └── IStepResult (NEW marker) ──┬─ StepCompleted  │
                         │                                     ├─ StepFailed     │
                         │                                     ├─ StepCancelled  │
                         │                                     └─ StepProcessing │
                         │ IKeeperRecoverable (NEW) ──┬─ UPDATE(+validatedData)  │
                         │   (corr,wf,proc,exec        ├─ REINJECT(+entryId)     │
                         │    partition 4-tuple)       ├─ INJECT                 │
                         │                             ├─ DELETE(+entryId)       │
                         │                             └─ CLEANUP                │
                         │ SourceStep.IsSource(Guid) (NEW D-07 predicate)        │
                         │ StepOutcome (kept, demoted off wire)                  │
                         │ L2ProjectionKeys: ExecutionData(Guid) no-TTL          │
                         │                   CompositeBackup(corr,wf,proc,exec)  │
                         │ KeeperQueues.Recovery = "keeper-recovery"             │
                         └─────────────────────────────────────────────────────┘
                              ▲ Send                ▲ Send (result)        ▲ Send (5 states)
                              │                     │                      │
        Orchestrator ────────┘                     │                Keeper (recovery consumer
        (StepDispatcher,                  Processor ┘                 = Phase 46; const only here)
         ResultConsumer,             (EntryStepDispatchConsumer
         WorkflowFireJob)              — straight-through adapt)

   Phase 43 edits the leaf + the straight-through adapt of the four arrowed senders/consumers.
   The typed routing (4 consumers) + Pre/In/Post pipeline + recovery consumer BODY = Phases 44/46.
```

### Recommended Project Structure (files this phase touches/creates)

```
src/Messaging.Contracts/
├── IExecutionCorrelated.cs          # EDIT: drop H, entryId string→Guid
├── EntryStepDispatch.cs             # EDIT: drop H, EntryId string→Guid (default Guid.Empty)
├── ExecutionResult.cs               # DELETE (replaced by four records)
├── IStepResult.cs                   # NEW: marker : IExecutionCorrelated
├── StepCompleted.cs                 # NEW
├── StepFailed.cs                    # NEW (+ ErrorMessage)
├── StepCancelled.cs                 # NEW (+ CancellationMessage)
├── StepProcessing.cs                # NEW
├── SourceStep.cs                    # NEW: D-07 single sentinel predicate (name = discretion)
├── IKeeperRecoverable.cs            # NEW: D-12 partition-4-tuple marker
├── KeeperUpdate.cs / Reinject / Inject / Delete / Cleanup.cs  # NEW: five records (D-11)
├── StepOutcome.cs                   # UNCHANGED enum (demoted off wire — doc only)
├── ExecutionLogScope.cs             # EDIT: EntryId skip rule string→Guid (use D-07 predicate)
├── KeeperQueues.cs                  # EDIT: add Recovery const
├── Hashing/MessageIdentity.cs       # DELETE (coupled RETIRE-01 — H computation)
└── Projections/L2ProjectionKeys.cs  # EDIT: ExecutionData(Guid) no-TTL single builder;
                                     #       add CompositeBackup(...); remove ExecutionData(string),
                                     #       transitional ExecutionData(Guid) dup, Flag(),
                                     #       KeeperProbe?/KeeperRecoverAttempts? (see Risk note)

src/Keeper/BackupOptions.cs          # NEW: { TtlDays = 2 } (ProbeOptions sibling)

(straight-through consumer adapt — Wave 3)
src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs   # remove flag[H]/CAS + content-addr + manifest
src/Orchestrator/Consumers/ResultConsumer.cs                     # remove dedup + manifest + placeholder Redis read
src/Orchestrator/Dispatch/StepDispatcher.cs                      # remove H compute + flag[H] pre-write
src/Orchestrator/Scheduling/WorkflowFireJob.cs                   # remove MessageIdentity.EntryEntryId; entryId = Guid.Empty
src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs  # covered by ExecutionLogScope edit (no body change)
```

### Pattern 1: Single-source-of-truth key builder (mirror exactly)
**What:** new key builders live ONLY in `L2ProjectionKeys`; callers forward.
**When to use:** both new keys (data + composite backup).
```csharp
// Source: src/Messaging.Contracts/Projections/L2ProjectionKeys.cs (existing convention)
public const string Prefix = "skp:";
public static string Root(Guid workflowId) => $"{Prefix}{workflowId:D}";
// NEW per D-08 (no-TTL data key — TTL is a caller concern, never baked into the builder):
public static string ExecutionData(Guid entryId) => $"{Prefix}data:{entryId:D}";
// NEW per D-09 (skp:-prefixed composite — deliberate divergence from the doc's bare notation):
public static string CompositeBackup(Guid correlationId, Guid workFlowId, Guid processorId, Guid executionId)
    => $"{Prefix}{correlationId:D}:{workFlowId:D}:{processorId:D}:{executionId:D}";
```

### Pattern 2: appsettings-bound options (mirror `ProbeOptions` exactly)
```csharp
// Source: src/Keeper/ProbeOptions.cs  +  src/Keeper/Program.cs:29
public sealed class BackupOptions { public int TtlDays { get; set; } = 2; }
// Program.cs (Keeper): builder.Services.Configure<BackupOptions>(builder.Configuration.GetSection("Backup"));
//   (section name = discretion; "Backup" mirrors "Probe"/"Retry")
```

### Pattern 3: `sealed record` wire contract with positional ids + `init` extras (mirror `ExecutionResult`)
```csharp
// Source: src/Messaging.Contracts/ExecutionResult.cs (the shape to fork into four)
// NO [JsonPropertyName]; enums serialize int; ids that are not positional are init-only.
public sealed record StepCompleted(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IStepResult
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }            // the REAL data key (D-06a)
}
public sealed record StepFailed(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IStepResult
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; } = Guid.Empty;   // hard-default sentinel (D-06a)
    public string? ErrorMessage { get; init; }               // D-06b
}
// StepCancelled mirrors StepFailed with CancellationMessage; StepProcessing has neither diagnostic field.
```
> Note the existing positional-id convention: `WorkflowId, StepId, ProcessorId` are constructor positionals; `CorrelationId`/`ExecutionId`/`EntryId` are `init`. Keep it byte-identical so the round-trip golden test is a copy-paste of `ExecutionResultContractTests`.

### Anti-Patterns to Avoid
- **Scattered `== Guid.Empty` checks.** D-07 mandates ONE predicate (`SourceStep.IsSource`). The skip in `ExecutionLogScope.BuildState` and any consumer skip MUST call it, not inline the comparison.
- **Building v4 behavior in Wave 3.** Do NOT add the four typed consumers, `TypedResultConsumer<T>`, `UsePartitioner`, the recovery-consumer body, or the Pre/In/Post pipeline. Those are Phase 44/46. Wave 3 is delete-dead-machinery + smallest-compile-fix only (D-03).
- **Baking TTL into the key builder.** The data key is no-TTL; the composite TTL comes from `BackupOptions` at the (Phase 46) write site. Builders return strings only.
- **Re-introducing a discriminator into the composite key.** D-09 is exactly `skp:{corr}:{wf}:{proc}:{exec}` with `:D` GUIDs — no `data:`/`backup:` segment (the four-GUID shape is itself the namespace).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Source-step detection | Inline `id == Guid.Empty` at each call site | ONE `SourceStep.IsSource(Guid)` (D-07) | Single predicate is a locked requirement; scatter = drift risk |
| L2 key string formatting | Per-caller `$"skp:..."` interpolation | `L2ProjectionKeys` builders | Established single-source-of-truth (Phase 21/22); a format change must not desync writer/reader |
| Options binding | Manual `IConfiguration` reads | `Configure<BackupOptions>(GetSection(...))` + `IOptions<>` | `ProbeOptions` precedent; bound-invariant test pattern exists |
| GUID rendering in keys | `.ToString("N")` or custom | `:D` format specifier (hyphenated) | Every existing L2 key uses `:D`; `L2ProjectionKeysTests` pins it |

**Key insight:** This phase is *deletion-heavy*, not build-heavy. The risk is hand-rolling a v4 behavior that belongs in a later phase, not failing to find a library.

## Runtime State Inventory

> This is a wire-contract + Redis-key reshape. The classic rename/migration runtime-state risks DO apply to the key-string change (live Redis data written under old key formats), but Phase 43 only *defines* the builders — it does not run against a live stack (that is Phase 44/49 E2E). Inventory below is for completeness.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data (Redis L2 key formats) | v3.x writes `skp:data:{64hex}` (content-addressed) and `skp:flag:{64hex}`. v4 data key becomes `skp:data:{guid:D}`; composite backup `skp:{corr}:{wf}:{proc}:{exec}`. The key STRING shape changes. | Code-level builder change only in 43. No live data migration in 43 (no E2E here). Any residual v3 `skp:flag:*`/`skp:data:{64hex}` keys are orphaned, not read — they TTL out (300s flag TTL) or are swept at close-gate (Phase 49). State explicitly so the planner does not add a migration task. |
| Live service config | None — no n8n/Datadog/external config carries these contract field names. | None. |
| OS-registered state | None — no Task Scheduler / pm2 / systemd references to `H`/`EntryId`. | None — verified by grep (no OS-registration files in repo). |
| Secrets/env vars | None — `H`/`entryId` are message-body fields, not env var or secret key names. | None. |
| Build artifacts | `MessageIdentity.cs` deletion removes a type referenced by `SourceHash.targets`? **No** — `SourceHash.targets` defines its own build-time `Hash` property (unrelated to `MessageIdentity.ComputeH`). Confirmed: the only `MessageIdentity` consumers are `StepDispatcher`, `EntryStepDispatchConsumer`, `WorkflowFireJob` (all reshaped in 43). | Delete `MessageIdentity.cs`; fix its three call sites (all in 43's blast radius). No stale build artifact. |

**The canonical question — after every file is updated, what still references the old shape?** Only the **test project** (`tests/BaseApi.Tests/`) — ~30 test files reference `.H`/`ExecutionResult`/`ManifestFanout`/`EffectFirstDedup`/`content-address`. These must be reshaped or deleted alongside the source (see §Blast Radius — Test Fallout). A green build requires the test project to compile against the new shapes.

## Exact Current Shapes (before → after, for grep-verifiable tasks)

### `EntryStepDispatch.cs` [VERIFIED: read this session]
**Current:**
```csharp
public sealed record EntryStepDispatch(Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload) : IExecutionCorrelated
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId  { get; init; } = Guid.Empty;
    public string EntryId    { get; init; } = "";        // ← string
    public string H          { get; init; } = "";        // ← REMOVE
}
```
**After:** drop `H`; `EntryId` → `public Guid EntryId { get; init; } = Guid.Empty;`. Update the XML-doc (it references "64-hex string" + "H ... Plan 04").

### `ExecutionResult.cs` [VERIFIED] → **DELETE**
Current: positional `(Guid WorkflowId, Guid StepId, Guid ProcessorId, StepOutcome Outcome) : IExecutionCorrelated` with `init` `CorrelationId`, `ExecutionId`, `string EntryId = ""`, `string H = ""`, `string? ErrorMessage`, `string? CancellationMessage`. Replaced by the four records (D-06). The `ErrorMessage`/`CancellationMessage` fields migrate to `StepFailed`/`StepCancelled` respectively (D-06b).

### `IExecutionCorrelated.cs` [VERIFIED]
**Current:** `: ICorrelated`, members `Guid ExecutionId`, `Guid WorkflowId`, `Guid StepId`, `Guid ProcessorId`, `string EntryId`, `string H`.
**After:** drop `string H`; `string EntryId` → `Guid EntryId`. (`ICorrelated` = just `Guid CorrelationId` — UNCHANGED.)

### `StepOutcome.cs` [VERIFIED] — **UNCHANGED enum** (`Processing=0, Completed=1, Failed=2, Cancelled=3`). Only its doc role changes (demoted off wire). Stays in `Messaging.Contracts` (D-06d). No code edit strictly required; an optional doc-comment touch.

### `ExecutionLogScope.cs` [VERIFIED — RISK SITE]
**Current `BuildState`:** skips each Guid when `Guid.Empty`; skips `EntryId` when `string.IsNullOrEmpty`. The `EntryId` line is:
```csharp
if (!string.IsNullOrEmpty(ec.EntryId)) state[EntryId] = ec.EntryId;
```
**After:** `ec.EntryId` is now a `Guid` → use the D-07 predicate:
```csharp
if (!SourceStep.IsSource(ec.EntryId)) state[EntryId] = ec.EntryId.ToString();   // or ec.EntryId != Guid.Empty via the predicate
```
The five `const` key strings (`WorkflowId`…`EntryId`) are UNCHANGED — `ExecutionLogScopeKeyTests` still passes verbatim.

### `KeeperQueues.cs` [VERIFIED]
**Current:** `FaultRecovery = "keeper-fault-recovery"`, `DeadLetter = "keeper-dlq"` (BOTH KEPT — retired Phases 47/48, NOT here).
**After:** add `public const string Recovery = "keeper-recovery";` (D-13).

### `L2ProjectionKeys.cs` [VERIFIED]
**Current builders:** `ParentIndex()`, `Root(Guid)`, `Step(Guid,Guid)`, `Processor(Guid)`, `ExecutionData(string)` (content-addr), `ExecutionData(Guid)` (transitional dup), `Flag(string)`, `KeeperProbe(string)`, `KeeperRecoverAttempts(string)`.
**After (D-08/D-09):**
- Keep `ParentIndex`, `Root`, `Step`, `Processor` (unchanged).
- `ExecutionData`: collapse to the SINGLE `ExecutionData(Guid) => skp:data:{entryId:D}` (already exists as the transitional overload — promote it, delete the `(string)` overload).
- ADD `CompositeBackup(Guid,Guid,Guid,Guid)`.
- REMOVE `Flag(string)` (coupled RETIRE-01).
- **Risk note — `KeeperProbe`/`KeeperRecoverAttempts`:** these are `(string h)`-keyed and used by `L2ProbeRecovery`/`KeeperRecoveryHandler` (the v3.7 reactive path NOT retired until 47/48). They take `string h`; if `H` is gone from `IExecutionCorrelated`, the reactive path's `inner.H` no longer compiles. **See §Blast Radius landmine on the reactive path** — the planner must decide whether to keep `Flag`/`KeeperProbe`/`KeeperRecoverAttempts` + the reactive Keeper path *compiling on a now-orphaned local `H`* or scope them in. This is the one genuine ambiguity (see Open Questions Q1).

### `Hashing/MessageIdentity.cs` [VERIFIED] → **DELETE** (coupled RETIRE-01). Sole non-test consumers: `StepDispatcher` (`ComputeH`), `EntryStepDispatchConsumer` (`HashBlob`/`HashManifest`/`ComputeH`), `WorkflowFireJob` (`EntryEntryId`). All three are in 43's blast radius. Plus `HashHelperGoldenFacts` test (delete).

### Precedent files (mirror, do not edit)
- `ProbeOptions.cs` [VERIFIED] — `public sealed class` with `int` props + defaults. Template for `BackupOptions`.
- `ProbeOptionsBoundTests.cs` [VERIFIED] — instantiate-defaults-and-assert-invariant. Template for a `BackupOptions` default test.
- `L2ProjectionKeysTests.cs` [VERIFIED, `[Trait("Phase","22")]`] — `Assert.Equal("skp:...", builder(...))`. Template for the two new key golden tests.
- `ICorrelated.cs` [VERIFIED] — `{ Guid CorrelationId { get; } }`. The Keeper contracts may add `: ICorrelated` (discretion, leaning yes).
- `OrchestratorQueues.cs` [VERIFIED] — `public const string Result = "orchestrator-result";`. The const-precedent for `KeeperQueues.Recovery`.

## New Contracts to Create (D-06, D-07, D-11, D-12)

| File | Shape | Id set |
|------|-------|--------|
| `IStepResult.cs` | `public interface IStepResult : IExecutionCorrelated {}` | marker (D-06c) |
| `StepCompleted.cs` | `sealed record : IStepResult` | six ids; `EntryId` = real key |
| `StepFailed.cs` | `sealed record : IStepResult` + `string? ErrorMessage` | six ids; `EntryId = Guid.Empty` |
| `StepCancelled.cs` | `sealed record : IStepResult` + `string? CancellationMessage` | six ids; `EntryId = Guid.Empty` |
| `StepProcessing.cs` | `sealed record : IStepResult` | six ids; `EntryId = Guid.Empty` |
| `SourceStep.cs` (name = discretion) | `public static bool IsSource(Guid entryId) => entryId == Guid.Empty;` | D-07 |
| `IKeeperRecoverable.cs` | `public interface IKeeperRecoverable { Guid CorrelationId; Guid WorkflowId; Guid ProcessorId; Guid ExecutionId; }` (member names = discretion) | partition 4-tuple (D-12). May also `: ICorrelated`. |
| `KeeperUpdate.cs` | `sealed record : IKeeperRecoverable` (+`: ICorrelated`?) + `validatedData` | `{corr, wf, step, proc, exec, validatedData}` |
| `KeeperReinject.cs` | `sealed record : IKeeperRecoverable` | `{corr, wf, step, proc, exec, entryId}` |
| `KeeperInject.cs` | `sealed record : IKeeperRecoverable` | `{corr, wf, step, proc, exec}` |
| `KeeperDelete.cs` | `sealed record : IKeeperRecoverable` | `{corr, wf, step, proc, exec, entryId}` |
| `KeeperCleanup.cs` | `sealed record : IKeeperRecoverable` | `{corr, wf, step, proc, exec}` |

> All five carry `stepId` as a plain property (NOT in the partition key — D-12). Record naming (`KeeperUpdate` vs `Update`) is discretion; `Update`/`Delete` collide with common BCL/EF names, so a `Keeper`-prefix is safer. Confirm the `IKeeperRecoverable` member set exposes exactly the 4-tuple the Phase-46 `UsePartitioner` consumes.

## Blast Radius — 37 refs / 13 files

### Source files (must-change, compile-forced) [VERIFIED file:line]

| File:Line | Ref | Why it breaks | Straight-through action (D-03) |
|-----------|-----|---------------|-------------------------------|
| `Orchestrator/Dispatch/StepDispatcher.cs:44,59` | `MessageIdentity.ComputeH`, `L2ProjectionKeys.Flag(h)` | `MessageIdentity` + `Flag` deleted | Remove the H compute + `flag[H]="Pending"` pre-write; build `EntryStepDispatch` without `H`; `entryId` param string→Guid (signature change ripples to callers) |
| `Orchestrator/Dispatch/StepDispatcher.cs:39` | `string entryId` param | entryId is now Guid | Change `DispatchAsync` signature `string entryId` → `Guid entryId` |
| `Orchestrator/Consumers/ResultConsumer.cs:46,48` | `IConsumer<ExecutionResult>` | `ExecutionResult` deleted | Re-target to `IConsumer<StepCompleted>` (the straight-through "single result" consumer; the other three typed consumers + base land Phase 46 per D-06e) |
| `Orchestrator/Consumers/ResultConsumer.cs:65,115` | `Flag(m.H)` dedup gate + Ack flip | `Flag`/`m.H` gone | DELETE the dedup gate (lines ~62-72) + the Ack flip (lines ~109-116) — RETIRE-01 |
| `Orchestrator/Consumers/ResultConsumer.cs:84-93` | manifest unbundle (`ExecutionData(m.EntryId)` read) | content-addr/manifest gone | DELETE the manifest read; result is L1-only. `IConnectionMultiplexer redis` ctor dep can be removed (D-06e: "Redis leaves the consumer") |
| `Orchestrator/Consumers/ResultConsumer.cs:103-107` | N×M fan-out (`foreach item × SelectNext`) | manifest gone | Collapse to `foreach (var (stepId, step) in advancement.SelectNext(...))` — one message = one item (D-06e). `SelectNext` takes `m.Outcome` → in 43 the straight-through `StepCompleted` consumer hardcodes `StepOutcome.Completed` (the per-type knob is Phase 46) |
| `Orchestrator/Consumers/ResultConsumer.cs:107` | `NewId.NextGuid()` executionId on dispatch | (D-06e flags this as a bug to correct in Phase 46) | In 43 straight-through: keep behavior minimal; preserve `m.CorrelationId`; `entryId` now Guid |
| `Orchestrator/Scheduling/WorkflowFireJob.cs:86,89` | `MessageIdentity.EntryEntryId(...)`, `entryId` arg to `DispatchAsync` | `MessageIdentity` gone; entryId now Guid | Entry-step fire seeds `entryId = Guid.Empty` (source sentinel — replaces the hash). Remove `using Messaging.Contracts.Hashing;` |
| `BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:76,81-83` | `Flag(dispatch.H)` dedup gate + `DispatchDeduped` metric | `Flag`/`H` gone | DELETE the effect-first dedup gate (RETIRE-01) |
| `…EntryStepDispatchConsumer.cs:91-93` | `string.IsNullOrEmpty(dispatch.EntryId)` + `ExecutionData(dispatch.EntryId)` | EntryId now Guid | Use `SourceStep.IsSource(dispatch.EntryId)` to skip read; `ExecutionData(Guid)` builder |
| `…EntryStepDispatchConsumer.cs:146-216` | `HashBlob`, content-addr write, manifest assembly, `HashManifest`, `flag[resultH]="Pending"`, CAS flip (225) | RETIRE-01/02 | DELETE manifest + content-addressing + flag pre-write + CAS flip. Straight-through: per-result, mint `entryId` (Guid), write `ExecutionData(entryId)`, send one `StepCompleted`/`StepFailed`/`StepCancelled` (no `H`). The full Pre/In/Post rewrite is Phase 44 — keep 43 to the minimal compile-and-pass |
| `…EntryStepDispatchConsumer.cs:244-306` | `SendResult(ExecutionResult)`, `BuildCompleted/Failed/Cancelled` returning `ExecutionResult` | `ExecutionResult` gone | Re-shape builders to return the matching `Step*` record; `OutcomeLabel(result.Outcome)` (line 250) — `Outcome` no longer on the wire record, so the metric tag must derive from the record TYPE or a passed-in label |
| `BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs:22` | `context.Message is IExecutionCorrelated ec` → `ExecutionLogScope.BuildState(ec)` | interface still exists; only `EntryId` type changed | NO body change — fixed entirely by the `ExecutionLogScope.BuildState` edit. The four `Step*` records implement `IStepResult : IExecutionCorrelated` so they flow through unchanged (D-06c) |

### Reactive-path landmine (NOT retired in 43 — Phases 47/48) [VERIFIED file:line]

| File:Line | Ref | Boundary call |
|-----------|-----|---------------|
| `Keeper/Recovery/KeeperRecoveryHandler.cs:69,71,85,92,98,106,148` | `where T : IExecutionCorrelated`, `inner.H`, `RunAsync(inner.EntryId, inner.H, …)`, `KeeperRecoverAttempts(inner.H)`, `new PauseWorkflow(…, inner.H)` | The generic recovery body is bound on `IExecutionCorrelated` and reads `inner.H` + `inner.EntryId` (string). Removing `H` from the interface + making `EntryId` a Guid **breaks this file**, even though the reactive path itself is retired in 47/48. |
| `Keeper/Recovery/L2ProbeRecovery.cs:24,37` | `RunAsync(string entryId, …)`, `ExecutionData(entryId)` | Takes `string entryId`; the `ExecutionData(string)` builder is removed. |
| `Keeper/Consumers/FaultExecutionResultConsumer.cs`, `Fault*ConsumerDefinition.cs` | `Fault<ExecutionResult>` | `ExecutionResult` deleted ⇒ `Fault<ExecutionResult>` no longer resolves. |
| `Orchestrator/Consumers/PauseWorkflowConsumer.cs:25`, `ResumeWorkflowConsumer.cs:26` | `m.H` | `PauseWorkflow`/`ResumeWorkflow` carry their OWN `string H` positional (NOT `IExecutionCorrelated.H`) — see Q1. These compile fine on their own; they only break via the `KeeperRecoveryHandler` that constructs them with `inner.H`. |

> **This is the phase's central scoping decision (Q1).** The reactive Keeper path (`KeeperRecoveryHandler`, `FaultExecutionResultConsumer`, `L2ProbeRecovery`, `Pause/ResumeWorkflow`'s `H`) is **explicitly retained until Phases 47/48** (D-02/D-13) — yet it structurally depends on `IExecutionCorrelated.H`, `Fault<ExecutionResult>`, and `ExecutionData(string)`, all of which Phase 43 removes. The planner MUST resolve how this path *keeps compiling* through 43-46. See Open Questions Q1 for the options.

### Test fallout (must compile/pass for green) [VERIFIED via grep — ~30 files]
Tests referencing the removed shapes split into three buckets:
- **DELETE (test the removed machinery):** `Processor/EffectFirstDedupFacts.cs`, `Orchestrator/ResultCheckAndDropFacts.cs`, `Processor/CheckAndDropFacts.cs`, `Orchestrator/ManifestFanoutFacts.cs`, `Orchestrator/MergeCollapseFacts.cs`, `Orchestrator/IdempotentExactlyOnceE2ETests.cs`, `Contracts/HashHelperGoldenFacts.cs`, `Processor/DispatchOutputWriteFacts.cs` (content-addr write). These test `H`/`flag[H]`/manifest/content-addressing — the very things RETIRE-01/02 remove.
- **RESHAPE (test surviving behavior on the new shape):** `Orchestrator/ExecutionResultContractTests.cs` → split into four `Step*` contract tests; `Orchestrator/EntryStepDispatchTests.cs` → assert no `H`, Guid `EntryId`; `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` → update `ExecutionData` expectation + add `CompositeBackup`; `Contracts/ExecutionLogScopeKeyTests.cs` (unchanged keys — verify still green); `Processor/Dispatch*Facts.cs`, `Orchestrator/ResultConsume*Tests.cs` (re-target to `StepCompleted`).
- **VERIFY-ONLY (compile-forced via shared harness):** `Orchestrator/OrchestratorTestStubs.cs`, `Processor/DispatchTestKit.cs` — shared stubs that construct `ExecutionResult`/set `H`; update once.

The planner should make "test project compiles + green" an explicit phase-gate task — the 37-ref count is source-side; the *test*-side fallout is larger and is where a "compiles but red" state hides.

## Common Pitfalls

### Pitfall 1: `Guid.Empty` serialization vs the string-empty sentinel
**What goes wrong:** v3.x distinguished "source step" via `EntryId == ""` (string). v4 uses `Guid.Empty` (`00000000-0000-0000-0000-000000000000`). STJ serializes `Guid.Empty` as the all-zero hyphenated string — it round-trips fine, but it is NOT null/absent on the wire (the field is always present).
**Why it happens:** developers expect a sentinel to be "missing"; here it's an explicit all-zero GUID.
**How to avoid:** the D-07 predicate (`IsSource`) is the only check. Golden test must assert `default(StepFailed).EntryId == Guid.Empty` AND that it serializes to `"00000000-0000-0000-0000-000000000000"`, so a future reader knows the sentinel is a present zero-GUID, not an omitted field.
**Warning sign:** any code doing `EntryId is null` or `EntryId == default` outside the predicate.

### Pitfall 2: `ExecutionLogScope` string→Guid skip ripple
**What goes wrong:** `BuildState` currently skips `EntryId` on `string.IsNullOrEmpty`. After the change, an unconverted `if (!string.IsNullOrEmpty(ec.EntryId))` won't compile (Guid), or a naive `ec.EntryId.ToString()` would now EMIT the all-zero GUID into the log scope for every source step (noise + a behavior change that "compiles silently").
**How to avoid:** route the skip through `SourceStep.IsSource(ec.EntryId)`; only set the scope value when NOT a source step; serialize via `.ToString()`. Confirm `ConsoleExecutionScopeFilterTests` / `ExecutionLogScopeKeyTests` still green.
**Warning sign:** source-step log records suddenly carrying `EntryId=00000000-...`.

### Pitfall 3: The reactive Keeper path compiling-but-orphaned (the big one)
**What goes wrong:** removing `IExecutionCorrelated.H` and `Fault<ExecutionResult>` cascades into `KeeperRecoveryHandler` (bound `where T : IExecutionCorrelated`, reads `inner.H`) and `FaultExecutionResultConsumer`. If the planner "fixes the compile" by quietly deleting these, it has done RETIRE-03 three phases early; if it leaves them, they don't build.
**How to avoid:** treat this as a scoped decision (Q1), not an incidental compile-fix. The minimal compile-preserving move is to give the reactive path a *local* identity source (e.g. a per-message field or a no-op stand-in) without removing the path — or to confirm with the user that the reactive path can go dark in 43. Do NOT silently delete it under cover of "straight-through."
**Warning sign:** `KeeperRecoveryHandler.cs` / `FaultExecutionResultConsumer.cs` disappearing from the diff with no D-row authorizing it.

### Pitfall 4: `StepOutcome` off the wire but still needed by `SelectNext`
**What goes wrong:** D-06d demotes `StepOutcome` off the wire, but `StepAdvancement.SelectNext(StepOutcome outcome, …)` still takes it. In 43's straight-through `StepCompleted` consumer there is no `m.Outcome` to pass.
**How to avoid:** the straight-through consumer passes a hardcoded `StepOutcome.Completed` to `SelectNext` (the per-type knob is Phase 46's `TypedResultConsumer.Outcome`). `StepAdvancement` itself does NOT change.
**Warning sign:** trying to read `.Outcome` off `StepCompleted` (it isn't there by design).

### Pitfall 5: Removing `IConnectionMultiplexer` from `ResultConsumer` ctor
**What goes wrong:** D-06e says the result path is L1-only and Redis "leaves the consumer." Dropping the ctor param ripples to DI registration + every test that constructs `ResultConsumer` with a redis mock.
**How to avoid:** make the ctor-param removal + DI/test updates one atomic task; grep `new ResultConsumer(` and the DI `AddConsumer`/`RegisterConsumer` site.
**Warning sign:** test harness still injecting `IConnectionMultiplexer` into a consumer that no longer reads it.

## Code Examples

### Composite backup key golden (the SC-4 proof) — mirror `L2ProjectionKeysTests`
```csharp
// Source pattern: tests/BaseApi.Tests/Features/Orchestration/Projection/L2ProjectionKeysTests.cs
[Fact]
public void CompositeBackup_Produces_Prefix_Four_HyphenatedGuids_Colon_Joined()
{
    var corr = Guid.Parse("11111111-1111-1111-1111-111111111111");
    var wf   = Guid.Parse("22222222-2222-2222-2222-222222222222");
    var proc = Guid.Parse("33333333-3333-3333-3333-333333333333");
    var exec = Guid.Parse("44444444-4444-4444-4444-444444444444");
    Assert.Equal(
        "skp:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222:" +
        "33333333-3333-3333-3333-333333333333:44444444-4444-4444-4444-444444444444",
        L2ProjectionKeys.CompositeBackup(corr, wf, proc, exec));
}

[Fact]
public void ExecutionData_Now_Takes_Guid_No_TTL_Concern()
    => Assert.Equal("skp:data:55555555-5555-5555-5555-555555555555",
        L2ProjectionKeys.ExecutionData(Guid.Parse("55555555-5555-5555-5555-555555555555")));
```

### Shape-pinning a `Step*` record (the SC-1 / no-`H` proof) — mirror `ExecutionResultContractTests`
```csharp
[Fact]
public void StepCompleted_carries_six_ids_and_has_no_H_property()
{
    Assert.Null(typeof(StepCompleted).GetProperty("H"));   // H absent (SC-1)
    var ids = typeof(StepCompleted).GetProperties().Select(p => p.Name).ToHashSet();
    foreach (var id in new[] { "CorrelationId","WorkflowId","StepId","ProcessorId","ExecutionId","EntryId" })
        Assert.Contains(id, ids);
    Assert.True(typeof(IStepResult).IsAssignableFrom(typeof(StepCompleted)));
    Assert.True(typeof(IExecutionCorrelated).IsAssignableFrom(typeof(StepCompleted)));
}

[Fact]
public void StepFailed_defaults_EntryId_to_Guid_Empty_sentinel()   // D-06a + SC-2
    => Assert.Equal(Guid.Empty, new StepFailed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()).EntryId);
```

### Source-sentinel predicate proof (SC-2)
```csharp
[Fact] public void IsSource_true_only_for_Guid_Empty()
{
    Assert.True(SourceStep.IsSource(Guid.Empty));
    Assert.False(SourceStep.IsSource(Guid.NewGuid()));
}
```

## State of the Art

| Old Approach (v3.x) | Current Approach (v4.0.0) | When Changed | Impact |
|---------------------|---------------------------|--------------|--------|
| `H` deterministic effect identity + `flag[H]` CAS dedup | No dedup key; at-least-once, duplicates tolerated | Phase 43 (RETIRE-01, coupled) | `MessageIdentity`, `Flag` builder, both dedup gates deleted |
| `entryId` = 64-hex content address (`string`) | `entryId` = `Guid`; data key `skp:data:{guid:D}` | Phase 43 (RETIRE-02, coupled) | `EntryStepDispatch.EntryId`, `IExecutionCorrelated.EntryId`, `ExecutionData` all change |
| Single `ExecutionResult(StepOutcome)` | Four typed records `: IStepResult` | Phase 43 (D-06, user divergence from doc) | `ExecutionResult.cs` deleted; routing by type (Phase 46) |
| Result manifest + N×M fan-out | One result = one item; M-successor fan-out only | Phase 43 (RETIRE-02) | `HashManifest`, manifest read, item-loop deleted |
| Reactive `Fault<T>` Keeper recovery + `keeper-dlq` | Proactive 5-state recovery consumer + `_DLQ1` | Phases 46/47/48 (NOT 43) | `KeeperQueues.Recovery` const added in 43; old path retired later |

**Deprecated/outdated:** the LOCKED design doc's single `ExecutionResult(Outcome)` shape — superseded by Amendment A15 (four records). Read every `ExecutionResult(Completed, …)` in the doc as "the matching `Step*` record."

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | `SourceHash.targets`' build-time `Hash` property is unrelated to `MessageIdentity.ComputeH` and deleting `MessageIdentity.cs` leaves no stale build artifact | Runtime State Inventory | Low — grep shows `SourceHash.targets` declares its own `Hash`; verified the only `MessageIdentity` consumers are the three reshaped consumers |
| A2 | `BackupOptions` should live Keeper-side (sibling to `ProbeOptions`) since the composite backup is written by Keeper `UPDATE` | Architectural Responsibility Map / Pattern 2 | Low — could alternatively live in `Messaging.Contracts` if the processor also needs `TtlDays`; planner confirms. The TTL is only *applied* at the Keeper `UPDATE` write (Phase 46), so Keeper-side is the natural home |
| A3 | The reactive Keeper path must be kept *compiling* (not deleted) through 43-46, per D-02/D-13 which retire it only in 47/48 | Blast Radius / Pitfall 3 / Q1 | **Medium-High** — if the user intends the reactive path to go dark in 43, the scope is smaller; if it must stay live, a local `H`/identity stand-in is needed. This is the one decision the planner must escalate (Q1) |
| A4 | The straight-through `ResultConsumer` re-targets to `IConsumer<StepCompleted>` (single result) in 43; the other three typed consumers + `TypedResultConsumer<T>` are Phase 46 | Blast Radius (ResultConsumer) | Low — directly from D-06e ("its stub becomes `StepCompletedConsumer`; the four typed consumers + base land in Phase 46") |
| A5 | Keeper record names take a `Keeper`-prefix (`KeeperUpdate` etc.) to avoid BCL/EF `Update`/`Delete` collisions | New Contracts table | Low — pure naming; discretion per CONTEXT |

## Open Questions

1. **How does the reactive Keeper recovery path keep compiling through Phases 43-46?** (THE central scoping question.)
   - What we know: D-02/D-13 explicitly retire `Fault<ExecutionResult>`, `keeper-fault-recovery`, `keeper-dlq`, and the reactive `KeeperRecoveryHandler` in **Phases 47/48, not 43**. But that path is bound on `IExecutionCorrelated` and reads `inner.H` (`KeeperRecoveryHandler.cs:69,85,98,106,148`), consumes `Fault<ExecutionResult>` (`FaultExecutionResultConsumer.cs`), and reads `ExecutionData(string)` (`L2ProbeRecovery.cs:37`) — all removed in 43.
   - What's unclear: whether the user expects (a) the reactive path to remain *functionally live* (needs a local `H` source + `Fault<EntryStepDispatch>`-only binding + a Guid-aware `L2ProbeRecovery`), or (b) the reactive path to be *minimally stubbed/dark* but still present (compiles, doesn't run), or (c) RETIRE-03 to in fact pull forward.
   - Recommendation: **escalate to the user before planning Wave 3.** The cleanest compile-preserving option is (b): keep the files, bind `KeeperRecoveryHandler<T>` on `Fault<EntryStepDispatch>` only (drop the `ExecutionResult` consumer), and give it a local identity (e.g. derive a string key from the four-tuple, or carry a per-message field) so it still builds — without deleting the path. Tag the chosen approach as a new D-row.

2. **Section name for `BackupOptions` binding + which host(s) bind it.** Discretion (CONTEXT). Recommendation: `"Backup"` section in the Keeper appsettings (mirrors `"Probe"`/`"Retry"`), bound only in `Keeper/Program.cs` (the composite backup is written by Keeper `UPDATE`).

3. **Does `StepOutcome` stay in `Messaging.Contracts` or move orchestrator-side?** D-06d says "stays in `Messaging.Contracts` (Claude's discretion whether it later moves)." Recommendation: leave it in `Messaging.Contracts` for Phase 43 (zero churn); revisit in Phase 46.

## Environment Availability

> Phase 43 is pure code/config — contract records, key builders, an options class, and tests. No external tools/services/runtimes are exercised (the real-stack E2E is Phase 49). The build + `dotnet test` run locally.

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK (net8/9) | build + test | ✓ (assumed — project builds today) | per global.json | — |
| xUnit v3 / MTP runner | golden/contract tests | ✓ | 3.2.2 (CPM) | — |

No missing dependencies; no Redis/RabbitMQ/Postgres needed for the contract-layer reshape or its unit/golden tests.

## Validation Architecture

> nyquist_validation = true (`.planning/config.json`). This section is REQUIRED and triggers VALIDATION.md downstream.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 (`xunit.v3` 3.2.2) under Microsoft.Testing.Platform (MTP runner) [VERIFIED: BaseApi.Tests.csproj] |
| Config file | `tests/BaseApi.Tests/xunit.runner.json` (maxParallelThreads cap, Phase 39) |
| Quick run command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Contracts|FullyQualifiedName~Projection"` |
| Full suite command | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |

> Golden/contract tests live in `tests/BaseApi.Tests/Contracts/` (e.g. `ExecutionLogScopeKeyTests`, `HashHelperGoldenFacts`) and `tests/BaseApi.Tests/Features/Orchestration/Projection/` (`L2ProjectionKeysTests`). New Phase 43 contract tests should co-locate there (organization = discretion). Tests are plain STJ + reflection — **no harness, no broker, no Redis** for the shape/key/predicate proofs.

### Phase Requirements → Test Map
| Req / SC | Behavior | Test Type | Automated Command | File Exists? |
|----------|----------|-----------|-------------------|-------------|
| SC-1 / MSG-01 | `EntryStepDispatch` carries six ids, no `H` | unit (reflection + round-trip) | `dotnet test … --filter "FullyQualifiedName~EntryStepDispatch"` | ⚠️ reshape `Orchestrator/EntryStepDispatchTests.cs` (currently asserts `H==""`) |
| SC-1 / MSG-01 | All four `Step*` records carry six ids, no `H`, `: IStepResult` | unit (reflection + round-trip) | `dotnet test … --filter "FullyQualifiedName~StepResult"` (new) | ❌ Wave 0 — `Contracts/StepResultContractTests.cs` |
| SC-2 / MSG-02 | `entryId` is `Guid`; `StepFailed/Cancelled/Processing` default `Guid.Empty`; `StepCompleted` carries real key | unit | `dotnet test … --filter "FullyQualifiedName~StepResult"` | ❌ Wave 0 |
| SC-2 / MSG-02 | `SourceStep.IsSource(Guid.Empty)==true`, else false; single predicate | unit | `dotnet test … --filter "FullyQualifiedName~SourceStep"` (new) | ❌ Wave 0 — `Contracts/SourceStepTests.cs` |
| SC-3 / MSG-03 | Five Keeper records exist, each with its id set; all `: IKeeperRecoverable` exposing the 4-tuple; `UPDATE` has `validatedData`; `REINJECT`/`DELETE` have `entryId` | unit (reflection) | `dotnet test … --filter "FullyQualifiedName~Keeper.*Contract"` (new) | ❌ Wave 0 — `Contracts/KeeperContractTests.cs` |
| SC-4 / MSG-03 | `ExecutionData(Guid) == "skp:data:{guid:D}"` | unit (golden) | `dotnet test … --filter "FullyQualifiedName~L2ProjectionKeys"` | ⚠️ update `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` |
| SC-4 / MSG-03 | `CompositeBackup(...) == "skp:{corr}:{wf}:{proc}:{exec}"` (`:D` GUIDs, skp-prefixed) | unit (golden) | same | ❌ Wave 0 (add to `L2ProjectionKeysTests`) |
| D-10 | `BackupOptions` defaults `TtlDays == 2`; binds from config | unit (mirror `ProbeOptionsBoundTests`) | `dotnet test … --filter "FullyQualifiedName~BackupOptions"` (new) | ❌ Wave 0 — `Keeper/BackupOptionsBoundTests.cs` |
| build-gate | Solution + test project compile against new shapes (straight-through) | build | `dotnet build` then full `dotnet test` | n/a (gate) |

### Sampling Rate
- **Per task commit:** the quick run (`--filter "Contracts|Projection|SourceStep|BackupOptions"`) — sub-30s, no I/O.
- **Per wave merge:** full suite (`dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`) — catches the test-project compile/reshape fallout (the large bucket).
- **Phase gate:** full suite green before `/gsd-verify-work`. Because Wave 3 deletes machinery covered by ~8 test files, "green" requires the DELETE/RESHAPE bucket (see §Test fallout) to be actioned — a partial reshape will leave the project red even though source compiles.

### Wave 0 Gaps
- [ ] `Contracts/StepResultContractTests.cs` — covers SC-1/SC-2 for the four `Step*` records (six ids, no `H`, `IStepResult`, `Guid.Empty` defaults, `ErrorMessage`/`CancellationMessage` placement).
- [ ] `Contracts/SourceStepTests.cs` — covers SC-2 single predicate.
- [ ] `Contracts/KeeperContractTests.cs` — covers SC-3 (five records, id sets, `IKeeperRecoverable` 4-tuple).
- [ ] `Keeper/BackupOptionsBoundTests.cs` — covers D-10 (default 2, bound invariant) — mirror `ProbeOptionsBoundTests.cs`.
- [ ] Extend `Features/Orchestration/Projection/L2ProjectionKeysTests.cs` — add `CompositeBackup` golden + update `ExecutionData(Guid)` expectation.
- [ ] Reshape/split `Orchestrator/ExecutionResultContractTests.cs` → four `Step*` contract tests (or fold into `StepResultContractTests`).
- [ ] DELETE the removed-machinery tests (`EffectFirstDedupFacts`, `ManifestFanoutFacts`, `MergeCollapseFacts`, `ResultCheckAndDropFacts`, `CheckAndDropFacts`, `IdempotentExactlyOnceE2ETests`, `HashHelperGoldenFacts`, content-addr write facts) — these assert RETIRE-01/02 machinery and cannot survive.
- [ ] Framework install: none — xUnit v3 already wired.

## Project Constraints (from CLAUDE.md)
No `./CLAUDE.md` and no `.claude/skills/` or `.agents/skills/` present in the repo [VERIFIED this session]. Project conventions are instead enforced by code precedent: `sealed record` wire contracts, **no `[JsonPropertyName]`**, **no `JsonStringEnumConverter`** (enums serialize int), `init`-only non-positional ids, `:D` GUID rendering in L2 keys, CPM (no `Version=` on `PackageReference`), single-source-of-truth static key/queue classes. Planner should treat these as binding.

## Sources

### Primary (HIGH confidence — read directly this session)
- `src/Messaging.Contracts/` — `EntryStepDispatch.cs`, `ExecutionResult.cs`, `IExecutionCorrelated.cs`, `ICorrelated.cs`, `StepOutcome.cs`, `ExecutionLogScope.cs`, `KeeperQueues.cs`, `OrchestratorQueues.cs`, `Projections/L2ProjectionKeys.cs`, `Hashing/MessageIdentity.cs`, `PauseWorkflow.cs`, `ResumeWorkflow.cs`
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`; `src/Orchestrator/Consumers/ResultConsumer.cs`; `src/Orchestrator/Dispatch/StepDispatcher.cs`, `StepAdvancement.cs`; `src/Orchestrator/Scheduling/WorkflowFireJob.cs`; `src/Orchestrator/Hydration/WorkflowLifecycle.cs`
- `src/Keeper/Recovery/KeeperRecoveryHandler.cs`, `L2ProbeRecovery.cs`; `src/Keeper/ProbeOptions.cs`; `src/Keeper/Program.cs` (binding pattern)
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs`
- `tests/BaseApi.Tests/` — `Features/Orchestration/Projection/L2ProjectionKeysTests.cs`, `Orchestrator/ExecutionResultContractTests.cs`, `Orchestrator/EntryStepDispatchTests.cs`, `Contracts/ExecutionLogScopeKeyTests.cs`, `Keeper/ProbeOptionsBoundTests.cs`, `BaseApi.Tests.csproj`
- `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (LOCKED + Amendment A15)
- `.planning/phases/43-message-contracts-l2-key-reshape/43-CONTEXT.md` (D-01..D-13); `.planning/REQUIREMENTS.md`; `.planning/config.json`
- Blast-radius grep (`.H`/`MessageIdentity`/`ExecutionData`/`manifest`/`Flag`/`ComputeH` across `src` + 69-file `**/*.cs` sweep)

### Secondary / Tertiary
- None — every claim is sourced from the codebase or the locked planning docs read this session. No web/training-data reliance.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new deps; all versions CPM-pinned and verified in `.csproj`.
- Exact shapes / blast radius: HIGH — every file read directly; file:line citations for each reshape site.
- Architecture / patterns: HIGH — mirrors existing in-repo precedents (`L2ProjectionKeys`, `ProbeOptions`, `ExecutionResultContractTests`).
- Reactive-path scoping (Q1): MEDIUM — the *facts* are HIGH (the path structurally depends on removed members), but the *intended resolution* needs user confirmation (A3/Q1).
- Validation Architecture: HIGH — framework + existing golden-test conventions verified.

**Research date:** 2026-06-08
**Valid until:** 2026-07-08 (stable internal contract surface; re-verify only if `Messaging.Contracts` or the consumers change before planning).

## RESEARCH COMPLETE
