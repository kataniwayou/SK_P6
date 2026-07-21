# Phase 85: Processor↔Framework Boundary Hardening - Research

**Researched:** 2026-07-21
**Domain:** In-repo .NET control-flow refactor (MassTransit/RabbitMQ nack semantics, exception-driven ack/nack fulcrum, seam-state removal). NO external libraries.
**Confidence:** HIGH (every claim below is grounded in the actual source read this session; file:line pins verified against current tree)

## Summary

This is a bounded, three-change control-flow refactor of the processor↔framework seam. Requirements (PB-01/02/03) and design (D-01…D-05) are LOCKED in CONTEXT.md; this research does NOT re-open them. Its job is to (a) pin the exact current code shape at every seam so the planner writes grounded `<read_first>`/`<action>` fields, (b) confirm every reuse pattern CONTEXT claims actually exists as described, (c) hand the planner a concrete dedicated-exception design, and (d) enumerate the hermetic facts + the SC-4 sweep gate, plus the specific existing tests that WILL break.

The single most important finding for the planner: **three existing hermetic tests assert the exact behavior this phase inverts** and must be updated in lockstep with the source — they are not incidental. Concretely: `BaseProcessorSeamFacts.SpawnToPost_SendExhaust_Swallows_...` asserts NO throw (PB-01 inverts this to a throw); `SampleProcessorFacts.Entry_Spawns_Two_..._DeletesEntry_...` asserts `db.Received(1).KeyDeleteAsync` at the seam (PB-03 moves the delete out of the author, so at the seam it becomes `DidNotReceive`); `PrePipelineFacts.SeamReturnsNull_...NoDelete` uses a **non-empty** entryId and asserts no delete, but D-04's null-path delete now fires for non-source ids — the test must switch to a `Guid.Empty` (source) entryId to keep matching production Mode-2 semantics.

**Primary recommendation:** Ship as 3 tightly-coupled waves in dependency order — (1) PB-01 add `SpawnSendExhaustedException` + flip `SpawnToPost:116` swallow→telemetry-then-throw; (2) PB-02 narrow rethrow at `ProcessorPipeline.cs:185` + the D-05/D-03 hermetic facts; (3) PB-03 remove `DeleteEntry`/`SeamState.EntryId`/`EscalateDelete`, move the delete-then-escalate onto the null-return path, and update every dependent test. Then the SC-4 sweep as the terminal gate.

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| PB-01 | `SpawnToPost` fail-loud: transient send-exhaust signaled by a propagating dedicated exception, not swallowed; `OnSpawnDropped` telemetry + deterministic-fault throw preserved | Exact swallow site confirmed at `BaseProcessor.cs:115-116`; deterministic-throw already at `:115`; `IsTransientSendFault` at `:123-137`. Design in §Dedicated Exception. |
| PB-02 | Mode-2 entry acked only if every spawn succeeded; a spawn send-exhaust nack-requeues the whole entry (broker redelivery) | Reuse mechanism confirmed identical to keeper-send-exhaust at `ProcessorPipeline.cs:304` (`if(!sent.Succeeded) throw sent.Error!`) and `OutputTail.cs:157/178`. Ack/nack fulcrum is the catch-all at `ProcessorPipeline.cs:185`. |
| PB-03 | Framework-owned entry deletion on every completion path (Mode-1 tail + Mode-2 null-return); remove concrete `DeleteEntry`, `SeamState.EntryId`, `EscalateDelete` | Mode-1 delete-then-escalate tail confirmed at `ProcessorPipeline.cs:222-227`; `DeleteEntry` at `BaseProcessor.cs:142-148`; `EntryId` at `:54`; `EscalateDelete` at `:65` wired at `ProcessorPipeline.cs:286`. |
</phase_requirements>

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01 — PB-01 signal shape:** propagating dedicated exception (NOT a return value). `SpawnToPost` fires `OnSpawnDropped` telemetry THEN throws `SpawnSendExhaustedException` (name is Claude's discretion) on a transient send-exhaust. Author writes NO catch — it propagates transparently through `ProcessAsync`. Mirrors the existing deterministic-fault throw at `BaseProcessor.cs:115`; only the transient-exhaust branch at `:116` changes swallow→telemetry-then-throw.
- **D-02 — PB-02 nack semantics:** bare RabbitMQ nack-requeue via the EXACT keeper-send-exhaust mechanism (rethrow → MassTransit → default nack-requeue). NO new redelivery/backoff config. Whole-entry re-fire duplicates already-succeeded spawns — at-least-once by design. **Rejected:** a redelivery delay/bound on the entry endpoint.
- **D-03 (HIGHEST RISK / LOCKED WATCH-OUT) — poison-message safety:** the catch-all rethrow/`when` filter must be NARROW to the dedicated exception type ONLY. Deterministic faults (`ArgumentException`/`NotSupportedException`/`JsonException`) stay caught → `StepFailed`+ack, exactly as today, or they poison-loop forever on redelivery. **The planner must NOT widen the catch-all filter to all exceptions.**
- **D-04 — PB-03 delete placement:** reuse the Mode-1 tail's delete-then-escalate (`ProcessorPipeline.cs:222-227`) on the null-return path, AFTER the succeeded spawns. Preserve the `SourceStep.IsSource(EntryId)` skip so net delete effect == today. Ordering enforced by D-01's throw (exhaust → throw → never reaches `return null` → nack → no delete). Remove `DeleteEntry`, `SampleProcessor` call, `SeamState.EntryId`, `EscalateDelete`. **Rejected:** a separate dedicated null-path delete helper.
- **D-05 — PB-02 hermetic fact:** construct `ProcessorPipeline` directly with an `ISendEndpointProvider` that throws a TRANSIENT fault (classified by `IsTransientSendFault`), assert `RunAsync` propagates the dedicated exception (= nack) rather than returning after a `StepFailed` (= ack). **Rejected:** an env-gated `PROCESSOR_DEFEAT_SPAWN` seam in production code.

### Claude's Discretion
- Exact name + namespace of the dedicated exception (`SpawnSendExhaustedException` placeholder; likely `BaseProcessor.Core.Processing` alongside `ProcessStatusException`).
- Catch shape: `catch (SpawnSendExhaustedException) { throw; }` before the generic catch **or** a `when` clause on the generic catch — either, as long as it is narrow.
- Wave breakdown across PB-01/02/03 (tightly coupled: signal → nack → delete-on-success).
- Whether to unify the Mode-1 tail delete and the new Mode-2 null-path delete into one private helper or inline the shared block.

### Deferred Ideas (OUT OF SCOPE)
- `KafkaImporter` (KIMP-01..06), the standalone Kafka container, `NextStepHandoff.Data` byte[] widening (MANIP-01), `KafkaExporter` (KEXP-01) — future Kafka milestone. Phase 85 ships the fail-loud primitive KIMP-03 will reuse, but NO Kafka.
- True exactly-once import dedup / idempotency key from (topic-partition-offset) — future; at-least-once is the accepted model here.
</user_constraints>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Spawn send + exhaust signal (PB-01) | `BaseProcessor.SpawnToPost` (framework base) | — | The base already owns the RetryLoop; only the exhaust *disposition* changes. Author never sees it. |
| Ack/nack decision on spawn exhaust (PB-02) | `ProcessorPipeline.RunAsync` catch-all (`:154-200`) | MassTransit/RabbitMQ (nack-requeue) | The pipeline is the single ack/nack fulcrum; the broker performs the requeue on an escaped throw. |
| Entry/source deletion (PB-03) | `ProcessorPipeline` (Mode-1 tail + Mode-2 null path) | `Keeper.DeleteConsumer` (escalation on exhaust) | Deletion is framework-owned per the boundary principle; keeper is the escalation target, unchanged. |
| Business transform / spawn-N-return-null | `SampleProcessor.ProcessAsync` (concrete author) | — | After this phase the author does ONLY this — no ack/nack/delete/escalation awareness. |

## Standard Stack

**No external packages are added, upgraded, or removed by this phase.** It is a pure in-repo control-flow edit across `BaseProcessor.Core` + `Processor.Sample` + `BaseApi.Tests`. Existing dependencies in play (all already referenced, versions unchanged):

| Library | Purpose in this phase | Notes |
|---------|----------------------|-------|
| MassTransit | Default RabbitMQ nack-requeue is the PB-02 mechanism — an uncaught throw out of the consumer nack-requeues (no `_error`, no dead-letter) | Already the documented pattern; see `ProcessorPipeline.cs:42-46` class doc. No config change. |
| StackExchange.Redis | `IDatabase.KeyDeleteAsync` for the L2 delete (already used by the Mode-1 tail) | Reused verbatim on the null path. |
| xUnit v3 + NSubstitute | Hermetic facts (construct-directly, no MassTransit harness) | `TestContext.Current.CancellationToken`; run via `BaseApi.Tests.exe` (dotnet test hangs on Windows MTP). |

**Installation:** none.

## Package Legitimacy Audit

**N/A — this phase installs no external packages.** No npm/PyPI/crates/NuGet additions. slopcheck / registry verification not applicable. (Zero packages to audit; the boundary hardening is entirely first-party source.)

## Current-Code Snapshot at Each Seam

> The planner should copy these into `<read_first>` and quote the exact block being changed. Everything below was read from the current tree this session.

### SEAM 1 — `SpawnToPost` swallow → throw (PB-01) — `src/BaseProcessor.Core/Processing/BaseProcessor.cs:92-117`

Current tail of the method (the ONLY lines that change):
```csharp
if (sent.Succeeded) return;

// line 115: deterministic fault → RAW throw (already correct — STAYS, do NOT touch; caught → ack).
if (!IsTransientSendFault(sent.Error)) throw sent.Error!;
// line 116: transient exhaust → SWALLOW (this is what PB-01 replaces).
s.OnSpawnDropped?.Invoke(spawn.ExecutionId);   // SWALLOW (log+counter); scheduler re-fires
```
PB-01 target shape:
```csharp
if (sent.Succeeded) return;

if (!IsTransientSendFault(sent.Error)) throw sent.Error!;   // deterministic → raw throw (unchanged, D-03 preserved)
s.OnSpawnDropped?.Invoke(spawn.ExecutionId);               // telemetry PRESERVED — MUST fire BEFORE the throw (SC-1)
throw new SpawnSendExhaustedException(spawn.ExecutionId, sent.Error!);   // transient → fail-loud (D-01)
```
- `IsTransientSendFault` (`:123-137`) is unchanged — it already classifies the transient transport/Redis family (`TimeoutException`, `BrokerUnreachableException`, `RedisConnectionException`, socket/IO, etc.). The D-05 fault must land in this family.
- **Ordering landmine (D-01 + SC-1):** the `OnSpawnDropped?.Invoke` MUST stay *before* the throw so "the drop telemetry hook is preserved" holds. Do not reorder or drop it.

### SEAM 2 — the ack/nack fulcrum (PB-02/D-03) — `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:154-200`

The seam invoke and its two existing catches:
```csharp
try { dr = await processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct); }
catch (ProcessStatusException e)          // :158 — author status → OutputTail + ack (STAYS)
{ ... _ = await outputTail.RunAsync(statusDr, d.EntryId, ct); ... return; }
catch (Exception ex)                      // :185 — GENERIC: incl. JsonException → StepFailed + ack (STAYS)
{ ... _ = await outputTail.RunAsync(unexpectedDr, d.EntryId, ct); ... return; }
```
PB-02/D-03 target: insert a NARROW rethrow so ONLY the dedicated type escapes. Recommended (clearest, matches the `ProcessStatusException` precedent ordering):
```csharp
catch (ProcessStatusException e) { ... }               // :158 unchanged
catch (SpawnSendExhaustedException) { throw; }         // NEW — escape → MassTransit → nack-requeue (D-02/D-03)
catch (Exception ex) { ... }                            // :185 unchanged — deterministic faults still ack
```
- Equivalent alternative (Claude's discretion): `catch (Exception ex) when (ex is not SpawnSendExhaustedException) { ... }`. Both are narrow; the explicit rethrow-catch reads better against the existing `ProcessStatusException` catch above it.
- **D-03 crux:** do not widen. `JsonException`, `ArgumentException`, `NotSupportedException` MUST remain in the generic `:185` catch → `StepFailed`+ack, or they poison-loop on every redelivery.

### SEAM 3 — the Mode-1 delete-then-escalate tail to reuse (PB-03/D-04) — `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:220-227`

```csharp
// A source step (Guid.Empty) has NO L2 input key to reclaim — skip the delete tail for it
if (!SourceStep.IsSource(d.EntryId))
{
    var del = await RetryLoop.ExecuteAsync(
        () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
    if (!del.Succeeded) await SendKeeper(BuildDelete(d), limit, ct);   // delete-exhaust → DELETE (req 4)
}
```
- This exact block (with its `IsSource` skip) is what D-04 says to run on the null-return branch **after** the spawns. `db`, `limit`, `SendKeeper`, `BuildDelete(d)` are ALL already in scope in `RunAsync` — no new plumbing needed (confirms CONTEXT §Integration-Points).
- The null-return branch is currently `ProcessorPipeline.cs:206`:
  ```csharp
  if (dr is null) { LogHopExecuted(d, messageId, nameof(StepOutcome.Completed)); return; }
  ```
  D-04 target: keep the `LogHopExecuted`, then run the `!SourceStep.IsSource(d.EntryId)` delete-then-escalate block **before** `return`. Discretion: extract lines 222-227 into a private `DeleteEntryTail(EntryStepDispatch d, IDatabase db, int limit, CancellationToken ct)` helper and call it from both the Mode-1 tail and the null path, OR inline. Either satisfies D-04.

### SEAM 4 — PB-03 removals

| Item | Location | Action |
|------|----------|--------|
| `DeleteEntry()` method | `BaseProcessor.cs:139-148` | Remove entirely (was the concrete author API). |
| `SeamState.EntryId` field | `BaseProcessor.cs:54` | Remove — used ONLY by `DeleteEntry` (verified: no other reader). |
| `SeamState.EscalateDelete` field | `BaseProcessor.cs:65` | Remove — used ONLY by `DeleteEntry`. |
| `SetSeamState` params `entryId`, `escalateDelete` | `BaseProcessor.cs:182-204` (signature `:182-185`, assignments `:195`/`:202`) | Remove both params + their `SeamState` assignments. |
| Pipeline `SetSeamState` wiring | `ProcessorPipeline.cs:267-287` — drop the `entryId:` arg (`:271`) and the whole `escalateDelete:` closure (`:286`) | The closure was `() => SendKeeper(BuildDelete(d), ...)`; that capability moves inline to the null-path delete (which already calls `SendKeeper(BuildDelete(d))`). No capability lost. |
| `SampleProcessor` `this.DeleteEntry()` call | `SampleProcessor.cs:72` | Remove. Also update the class doc-comments at `:16-19` (describes author-owned `DeleteEntry`) and `:22-23`/`:27-28` in `BaseProcessor.cs` (describe `SpawnToPost` "SWALLOW" and `DeleteEntry`). |

- **AsyncLocal isolation is NOT affected (CR-01 preserved):** `SeamState` still carries `MessageId`/`WorkflowId`/`StepId`/`CorrelationId`/`ProcessorId` (read by `NewResult`, `BaseProcessor.cs:166-177`). Removing `EntryId`/`EscalateDelete` only shrinks the per-dispatch state; the `AsyncLocal<SeamState?>` model (`:43`), `SetSeamState` fresh-object publish (`:190`), and `ClearSeamState` (`:209`) are unchanged. `NewResult` does NOT read `EntryId`, so it keeps working.
- `SourceStep.IsSource(Guid entryId) => entryId == Guid.Empty` — confirmed at `src/Messaging.Contracts/SourceStep.cs:8`.

## Reuse Patterns — Confirmed to Exist as CONTEXT Claims

| Claim (CONTEXT) | Verified location | Status |
|-----------------|-------------------|--------|
| Keeper-send-exhaust rethrow (`SendKeeper` :293-305) is the exact nack mechanism PB-02 reuses | `ProcessorPipeline.cs:304`: `if (!sent.Succeeded) throw sent.Error!;` — bare throw → broker redelivery. Same pattern in `OutputTail.cs:157` (SendResult) and `:178` (SendKeeper). | ✅ CONFIRMED |
| `ProcessStatusException` precedent + where the pipeline catches it | Family in `src/BaseProcessor.Core/Processing/ProcessStatusException.cs` (`abstract ProcessStatusException : Exception`; `Processing/Failed/Cancelled` subclasses). Caught at `ProcessorPipeline.cs:158`. | ✅ CONFIRMED — but see design note: the new exception must NOT derive from `ProcessStatusException` (that would route to the `:158` ack path). |
| `RetryLoop.ExecuteAsync` → `RetryOutcome` shape `SpawnToPost` consumes | `src/BaseConsole.Core/Resilience/RetryLoop.cs`: `RetryOutcome<T>(bool Succeeded, T? Value, Exception? Error)`; `SpawnToPost` consumes `.Succeeded`/`.Error` at `BaseProcessor.cs:96-108/115`. | ✅ CONFIRMED |
| `ProcessorPipeline` is a plain constructable object (D-05) | `ProcessorPipeline.cs:48-56` primary ctor `(redis, context, processor, sendProvider, retryOptions, outputTail, metrics, logger)`. `PrePipelineFacts.Build` (`PrePipelineFacts.cs:27-35`) already constructs it directly. | ✅ CONFIRMED |
| `ProcessAsync` propagates uncaught faults to the pipeline | `BaseProcessor{TConfig}.ExecuteAsync` (`BaseProcessor`1.cs:20-27`) does NOT catch — a thrown exception (incl. the new one) flows straight to `RunAsync`'s try. | ✅ CONFIRMED |
| A ready-made transient-fault send provider for D-05 already exists | `BaseProcessorSeamFacts.SendFaultProvider` (`BaseProcessorSeamFacts.cs:28-44`) throws `RedisConnectionException` (∈ `IsTransientSendFault`) on every `Send`. **Reuse it verbatim** for the D-05 fact. | ✅ CONFIRMED — reuse, don't rebuild. |

## Dedicated Exception — Recommended Design

- **Name:** `SpawnSendExhaustedException` (the CONTEXT placeholder is fine; it is descriptive and unambiguous).
- **Namespace / file:** `BaseProcessor.Core.Processing`, new file `src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs` — alongside `ProcessStatusException.cs` (mirrors the "framework-recognized exception" precedent's location).
- **Base type:** derive from `System.Exception` directly. **CRITICAL: do NOT derive from `ProcessStatusException`** — that family is caught at `ProcessorPipeline.cs:158` and routed to `OutputTail`+ack, the exact opposite of the nack this exception must produce. (Precedent for a non-status framework exception deriving from `Exception`: `KeyAbsentException`, `src/BaseProcessor.Core/Resilience/KeyAbsentException.cs`.)
- **Payload — IDS ONLY (FW-03 / T-70-10):** carry `Guid ExecutionId` (the spawn's minted execId, for correlation) and the inner transient transport `Exception` (as `innerException` — it carries transport metadata, never business payload). **Never** carry `DataResult`/`Data`/`Payload`. Shape:
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
  (The `{executionId:D}` in the message is an id, not a payload — consistent with the existing `OnSpawnDropped` warn at `ProcessorPipeline.cs:280` which logs `ExecutionId` only.)
- **Visibility:** `public`. `BaseProcessor.Core` already has `InternalsVisibleTo("BaseApi.Tests")` (`BaseProcessor.Core.csproj:38`), so `internal` would also be test-visible — but `public` mirrors `ProcessStatusException` and reads cleaner. Either compiles; recommend `public`.
- **Thread path:** thrown at `BaseProcessor.SpawnToPost` (base) → propagates uncaught through `SampleProcessor.ProcessAsync` (author, no catch) → through `BaseProcessor{TConfig}.ExecuteAsync` (no catch) → into `ProcessorPipeline.RunAsync`'s `try` (`:157`) → filtered by the new narrow catch (`:185`-area) → rethrown → MassTransit consumer boundary → RabbitMQ nack-requeue.

## Runtime State Inventory

> This is a code-only control-flow refactor. It renames no keys, migrates no data, and touches no OS/service registration. Each category verified explicitly.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | None — no L2 key names, collection names, or ids change. The `L2[ExecutionData(entryId)]` key shape is unchanged; only *who* issues the delete moves (author→framework). | None. |
| Live service config | None — no RabbitMQ queue/endpoint config, no redelivery/backoff config is added (D-02 explicitly rejects new endpoint config). The `-post` queue and `queue:{KeeperQueues.Recovery}` URIs are unchanged. | None — verified against `SpawnToPost` (`BaseProcessor.cs:101`) and `SendKeeper` (`ProcessorPipeline.cs:300`). |
| OS-registered state | None — no scheduled tasks, no process names. | None. |
| Secrets / env vars | None added. Note: D-05 explicitly REJECTS a new `PROCESSOR_DEFEAT_SPAWN` env seam. Existing test-only env seams (`PROCESSOR_DEFEAT_READ`, `PROCESSOR_STEP_DELAY_MS`) are untouched. | None. |
| Build artifacts | None — no package/assembly rename; `csproj` files unchanged. | None. |

## Common Pitfalls

### Pitfall 1: The new exception silently routes to the ack path
**What goes wrong:** if `SpawnSendExhaustedException` derives from `ProcessStatusException`, it is caught at `ProcessorPipeline.cs:158` → `OutputTail`+ack — the exact opposite of the intended nack.
**How to avoid:** derive from `Exception` directly (see §Dedicated Exception). Add the narrow rethrow catch ABOVE (before) the generic `:185` catch and BELOW the `:158` `ProcessStatusException` catch.
**Warning sign:** the D-05 hermetic fact would show `RunAsync` returning (a `StepFailed` captured) instead of throwing.

### Pitfall 2 (D-03, HIGHEST RISK): widening the catch-all filter
**What goes wrong:** a `catch (Exception) { throw; }` or too-broad `when` would let deterministic faults (`JsonException` from a malformed payload, `ArgumentException`) escape → nack → poison-loop forever on redelivery.
**How to avoid:** filter to `SpawnSendExhaustedException` ONLY. Keep the generic `:185` catch handling everything else → `StepFailed`+ack.
**Warning sign / test:** the existing `PrePipelineFacts.MalformedPayload_DeserFailure_...` (deser `JsonException`) and `SeamThrows_Unexpected_Failed` (`InvalidOperationException`) MUST still pass unchanged — they prove deterministic faults still ack. Add the explicit D-03 negative-control fact below.

### Pitfall 3: telemetry ordering (SC-1)
**What goes wrong:** replacing the `OnSpawnDropped?.Invoke` line with a bare throw drops the telemetry, violating "the `OnSpawnDropped` telemetry hook is preserved."
**How to avoid:** invoke the hook, THEN throw (two statements, in that order). The `SpawnDropped` counter (`ProcessorMetrics.cs:47-51`, incremented in the `SetSeamState` `onSpawnDropped` closure at `ProcessorPipeline.cs:283`) is preserved automatically because the hook still fires.

### Pitfall 4: the Guid.Empty source net-effect (D-04) — the subtle one
**What goes wrong:** assuming the framework null-path delete changes behavior for the real seed. It does not — and asserting otherwise leaks a false change into the sweep.
**The facts:** the real Mode-2 entry/seed dispatch has `EntryId == Guid.Empty` (a source). Today, `SampleProcessor.DeleteEntry()` deletes `L2[ExecutionData(Guid.Empty)]` — a harmless no-op key. Tomorrow, the framework null-path delete is guarded by `!SourceStep.IsSource(d.EntryId)`, so for `Guid.Empty` it SKIPS. **Net effect identical: both leave nothing.** ✅ Confirmed — the D-04 claim holds for the production seed.
**The catch for the planner:** the *seam-level* test `SampleProcessorFacts.Entry_Spawns_Two_..._DeletesEntry_...` wires a **non-empty** `entryId` (`SampleProcessorFacts.cs:54` `Guid.NewGuid()`) and asserts `db.Received(1).KeyDeleteAsync` (`:79`) because today the author calls `DeleteEntry` on whatever entryId the seam holds. After PB-03 the author no longer deletes → at the seam it becomes `DidNotReceive`. And `PrePipelineFacts.SeamReturnsNull_...NoDelete` (`:127-144`) also uses a non-empty entryId and asserts no delete — but D-04's null-path delete now fires for a *non-source* id, so this test must switch its entryId to `Guid.Empty` to keep asserting "no delete" AND to match real Mode-2 semantics. See §Validation Architecture for the full break-list.

### Pitfall 5: `byte[]` reference-equality in new assertions (carried from Phase 84)
**What goes wrong:** asserting `DataResult.Data` equality by record value-equality — `byte[]` is reference-compared.
**How to avoid:** this phase adds no `Data`-comparison assertions (control-flow only), but if any new fact inspects `Data`, compare with `Assert.Equal(expectedBytes, actual.Data)` (sequence) not record equality. Low risk here.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Nack-requeue on spawn exhaust | A manual RabbitMQ `BasicNack` / a custom redelivery loop / an endpoint retry policy | Let the dedicated exception ESCAPE the consumer → MassTransit's default nack-requeue (`ProcessorPipeline.cs:42-46` doc; same as `SendKeeper` `:304`) | D-02 mandates the bare-nack pattern used everywhere else; new config diverges and is explicitly rejected. |
| Faulting send provider for the hermetic fact | A new test double | Reuse `BaseProcessorSeamFacts.SendFaultProvider` (`:28-44`) — already throws `RedisConnectionException` (transient) | Proven, transient-classified, zero new surface. |
| Null-path delete + escalation | A new `DeleteEntryOnNull` helper with its own keeper send | Reuse the Mode-1 block `ProcessorPipeline.cs:222-227` (`db`, `SendKeeper`, `BuildDelete` in scope) | D-04 explicitly rejects a separate helper; one code path, one behavior. |

## Validation Architecture

> `workflow.nyquist_validation: true` — this section is REQUIRED. Framework mandates run under `BaseApi.Tests.exe` directly (`dotnet test` hangs on Windows MTP), `--filter-not-trait Category=RealStack` for the hermetic set.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit v3 + NSubstitute (existing) |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (existing; no new config) |
| Quick run command | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-class "*BaseProcessorSeamFacts*" --filter-class "*PrePipelineFacts*" --filter-class "*SampleProcessorFacts*"` |
| Full hermetic suite | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` |
| SC-4 sweep gate | `pwsh -File scripts/phase-68-sweep.ps1` (run DETACHED per memory `long-sweep-detached-process`; all-PASS = every scenario `Missing==0`, `Duplicates==0`) |

### Phase Requirements → Test Map
| Req ID | Behavior | Test Type | Automated Command | File Exists? |
|--------|----------|-----------|-------------------|-------------|
| PB-01 | `SpawnToPost` transient exhaust THROWS `SpawnSendExhaustedException` AND fires `OnSpawnDropped` first | unit (seam) | `BaseApi.Tests.exe --filter-method "*SpawnToPost_SendExhaust*"` | ❌ Wave 0 — **INVERT** existing `SpawnToPost_SendExhaust_Swallows_...` (BaseProcessorSeamFacts.cs:63-85) to assert throw + hook-fired |
| PB-01 | `SpawnToPost` DETERMINISTIC send fault still throws the RAW exception (not the dedicated type) | unit (seam) | same class | ❌ Wave 0 — new fact (non-transient provider → `Assert.ThrowsAsync` NOT `SpawnSendExhaustedException`) |
| PB-02 (D-05) | Defeated spawn ⇒ `ProcessorPipeline.RunAsync` PROPAGATES `SpawnSendExhaustedException` (= nack), does NOT return after a `StepFailed` (= ack) | unit (pipeline, construct-direct) | `BaseApi.Tests.exe --filter-class "*PrePipelineFacts*"` | ❌ Wave 0 — new fact; reuse `SendFaultProvider` + a `Guid.Empty` source dispatch + real `SampleProcessor` (or Mode-2 `FakeProcessor`); `await Assert.ThrowsAsync<SpawnSendExhaustedException>(() => pipeline.RunAsync(...))` |
| PB-02/PB-03 (D-04) | On a `Guid.Empty` (source) null-return, NO entry delete fires (net-effect-identical skip) | unit (pipeline) | same class | ❌ Wave 0 — **UPDATE** `SeamReturnsNull_NoWrite_NoSend_NoDelete` (PrePipelineFacts.cs:127-144) to a `Guid.Empty` entryId |
| PB-03 (D-04) | On a NON-source null-return, the framework DOES delete `L2[entryId]` (delete-exhaust → DELETE keeper) | unit (pipeline) | same class | ❌ Wave 0 — new fact proving framework ownership of the delete moved off the author |
| D-03 (neg control) | A deterministic seam fault (`JsonException`/`InvalidOperationException`) still → `StepFailed`+ack (NOT nack) | unit (pipeline) | same class | ✅ EXISTS — `MalformedPayload_DeserFailure_...` + `SeamThrows_Unexpected_Failed` (PrePipelineFacts.cs). Keep green; they ARE the D-03 poison-safety proof. |
| PB-03 | `SampleProcessor` entry path no longer calls `DeleteEntry`; seam-level delete → `DidNotReceive` | unit (author) | `BaseApi.Tests.exe --filter-class "*SampleProcessorFacts*"` | ❌ Wave 0 — **UPDATE** `Entry_Spawns_Two_..._DeletesEntry_ReturnsNull` (SampleProcessorFacts.cs:48-80): rename, flip `Received(1)`→`DidNotReceive` at `:79` |
| PB-03 | `DeleteEntry`/`EntryId`/`EscalateDelete` removed cleanly (compile) | build | `dotnet build BaseProcessor.Core` | ❌ Wave 0 — remove `DispatchTestKit.FakeProcessor.DeleteEntryAsync` (`:92`) + the two `DeleteEntry_*` facts (BaseProcessorSeamFacts.cs:114-132, 201-221) + drop the delete half of `ConcurrentConsumes_DoNotBleed` (`:142-199`, keep the NewResult messageId-stamp isolation half) + drop `escalateDelete`/`entryId` args from all `WireSeam` helpers |
| SC-4 | 7-scenario fault-recovery sweep reproduces all-PASS baseline (no happy-path regression) | integration (RealStack, detached) | `pwsh -File scripts/phase-68-sweep.ps1` | ✅ EXISTS — this is the terminal phase gate, not new |

### Existing tests that WILL break (must change in the PB-03 wave)
1. `BaseProcessorSeamFacts.SpawnToPost_SendExhaust_Swallows_FiresDropHook_NoThrow_NoKeeper` (`:63-85`) — INVERT to assert throw (PB-01).
2. `BaseProcessorSeamFacts.DeleteEntry_Success_NoEscalation` (`:114-132`) + `DeleteEntry_DeleteExhaust_Escalates_ToDeleteHook` (`:201-221`) — REMOVE (DeleteEntry gone). Coverage moves to the pipeline null-path delete facts.
3. `BaseProcessorSeamFacts.ConcurrentConsumes_DoNotBleed_SeamStateIsPerDispatch` (`:142-199`) — uses `DeleteEntryAsync` to prove per-entry isolation. KEEP the CR-01 regression but re-express the isolation via `NewResult`'s messageId stamp only (already half-asserted at `:189-193`); drop the entry-delete half (`:146-152`, `:195-198`).
4. `SampleProcessorFacts.Entry_Spawns_Two_Distinct_ExecIds_DeletesEntry_ReturnsNull` (`:48-80`) — flip the delete assertion to `DidNotReceive` and rename.
5. `PrePipelineFacts.SeamReturnsNull_NoWrite_NoSend_NoDelete` (`:127-144`) — switch entryId to `Guid.Empty` (source) so "no delete" still holds under D-04.
6. `DispatchTestKit.FakeProcessor.DeleteEntryAsync` (`:92`) + every `WireSeam(...)` / `SetSeamState(...)` call site passing `entryId:`/`escalateDelete:` — update signatures after `SetSeamState` params are removed (`BaseProcessor.cs:182-204`). Call sites: `BaseProcessorSeamFacts.WireSeam` (`:46-61`), `SampleProcessorFacts.WireSeam` (`:38-46`), the inline `SetSeamState` in `ConcurrentConsumes` (`:163-172`).

### Sampling Rate
- **Per task commit:** the quick run command above (the 3 affected fact classes) — < 30s.
- **Per wave merge:** full hermetic suite `BaseApi.Tests.exe --filter-not-trait Category=RealStack` — zero new failures vs the pre-phase baseline.
- **Phase gate:** full hermetic suite green, THEN the detached SC-4 sweep all-PASS before `/gsd:verify-work`.

### Wave 0 Gaps
- [ ] `src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs` — new type (§Dedicated Exception).
- [ ] New PB-01 deterministic-fault seam fact (non-transient provider → raw throw, not dedicated).
- [ ] New PB-02/D-05 pipeline nack fact (reuse `SendFaultProvider` + `Guid.Empty` dispatch).
- [ ] New PB-03/D-04 non-source null-path delete fact.
- [ ] Framework install: none — all infra exists.

## Security Domain

> `security_enforcement` absent ⇒ treated as enabled. This phase has essentially no new attack surface (pure internal control-flow; no auth, no network endpoints added, no crypto, no new input parsing).

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | — |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation | no (unchanged) | Input/output schema validation is framework-owned and untouched (`ProcessorJsonSchemaValidator`, `ProcessorPipeline.cs:135`, `OutputTail.cs:73-75`). |
| V6 Cryptography | no | — |
| V7 Error Handling & Logging | **yes** | FW-03 / T-70-10: the new throw path + exception message carry IDS ONLY (`ExecutionId`), never `Payload`/`Data`. Matches the existing `OnSpawnDropped` warn (`ProcessorPipeline.cs:280`) and the WR-03 sanitized-constant discipline (`ProcessorPipeline.cs:189-195`). |

### Known Threat Patterns for this refactor
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Poison-message infinite nack loop (a deterministic fault nacking forever) | Denial of Service | D-03 narrow filter — deterministic faults stay on the ack path (`StepFailed`+ack). This is the phase's central safety invariant; the D-03 negative-control facts guard it. |
| Payload leakage via exception message/log | Information Disclosure | Exception carries `ExecutionId` (a Guid) + inner transport exception only; no `Data`/`Payload`. Enforced by the ids-only rule and the existing sanitized-constant catch (`:189-195`). |

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET 8 SDK + `BaseApi.Tests.exe` | Hermetic facts (all PB verification except SC-4) | ✓ (existing build pipeline) | net8.0 | — |
| Docker + RabbitMQ/Redis/Postgres stack, `pwsh`, `psql` | SC-4 sweep (`phase-68-sweep.ps1`, RealStack) | ✓ (used through Phase 84) | — | — |

- **Note (memory):** run the SC-4 sweep DETACHED via `Start-Process` + a short watcher (memory `long-sweep-detached-process`); clear stale analyzer reports first (memory `phase-68-sweep-stale-report-read` — trust the harness exit code, not a possibly-stale report read); reseed order after a rebuild per memory `rebuild-sourcehash-reseed-order`. These are operational, not blockers.

## State of the Art

| Old Approach (pre-85) | Current Approach (post-85) | Impact |
|-----------------------|----------------------------|--------|
| `SpawnToPost` SWALLOWS transient exhaust (best-effort; "scheduler re-fires") | Fail-loud: throws `SpawnSendExhaustedException` → nack-requeue (at-least-once broker guarantee) | No seeded execution silently dropped; whole-entry re-fire duplicates succeeded spawns (accepted). |
| Author (`SampleProcessor`) calls `this.DeleteEntry()` | Framework-owned delete on every completion path (Mode-1 tail + null path), `IsSource` skip preserved | Author surface shrinks to business-logic-only; net delete effect unchanged for the `Guid.Empty` seed. |

**Deprecated/removed by this phase:** `BaseProcessor.DeleteEntry` (public author API), `SeamState.EntryId`, `SeamState.EscalateDelete`, the `SetSeamState` `entryId`/`escalateDelete` params, and the `ProcessorPipeline.SetSeamState` `escalateDelete` closure. Deletion capability is NOT lost — it moves inline to the pipeline null path.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The real Mode-2 seed dispatch always carries `EntryId == Guid.Empty` (source), so the framework null-path delete SKIPS it and net-effect == today's no-op. | Pitfall 4 / D-04 | LOW. If a scheduler ever seeds a Mode-2 entry with a non-empty `EntryId`, the framework delete would fire (arguably more correct), changing behavior. Verified against `SampleProcessorFacts` (production author only returns null on `executionId == Guid.Empty`) and the pipeline source-bypass at `ProcessorPipeline.cs:86`. Planner: confirm the scheduler's entry dispatch `EntryId` is `Guid.Empty` if in doubt. |
| A2 | Making `SpawnSendExhaustedException` `public` vs `internal` is cosmetic (test assembly sees both via `InternalsVisibleTo`). | Dedicated Exception | NONE — both compile; `public` chosen for `ProcessStatusException` parity. |

## Open Questions

1. **Unify or inline the null-path delete?** (Claude's discretion per D-04.)
   - What we know: `db`/`limit`/`SendKeeper`/`BuildDelete(d)` are all in `RunAsync` scope; lines 222-227 are the exact block.
   - Recommendation: extract a private `DeleteEntryTail(d, db, limit, ct)` and call from both the Mode-1 tail and the null path — one behavior, DRY, and it reads as "framework-owned deletion" at both call sites. Defer to the planner.

2. **Which `FakeProcessor` shape for the D-05 fact — real `SampleProcessor` or a Mode-2 `FakeProcessor`?**
   - What we know: `SampleProcessor` is the real author (spawns 2 then returns null); `FakeProcessor` supports a delegate that calls `SpawnToPost` (`DispatchTestKit.cs:69-89`).
   - Recommendation: use the real `SampleProcessor` for the D-05 nack fact (it exercises the true Mode-2 path) with the `Guid.Empty` dispatch; use a minimal `FakeProcessor` Mode-2 delegate if a tighter single-spawn assertion is wanted. Either proves the escape.

## Sources

### Primary (HIGH confidence) — source read this session
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` (SpawnToPost, IsTransientSendFault, DeleteEntry, SeamState, SetSeamState)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (RunAsync try/catch fulcrum, null path, Mode-1 delete tail, SetSeamState wiring, SendKeeper)
- `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` (ExecuteAsync propagation), `ProcessStatusException.cs`, `OutputTail.cs`, `Observability/ProcessorMetrics.cs`
- `src/Processor.Sample/SampleProcessor.cs` (Mode-2 entry path)
- `src/BaseConsole.Core/Resilience/RetryLoop.cs` (RetryOutcome shape)
- `src/Messaging.Contracts/SourceStep.cs` (IsSource == Guid.Empty), `KeeperDelete.cs`, `src/Keeper/Recovery/DeleteConsumer.cs`
- `tests/BaseApi.Tests/Processor/{BaseProcessorSeamFacts,PrePipelineFacts,SampleProcessorFacts,DispatchTestKit}.cs` (the tests that break + the SendFaultProvider to reuse)
- `.planning/{REQUIREMENTS.md,ROADMAP.md}` (PB-01/02/03, SC-1..4), `85-CONTEXT.md` (D-01..05)
- `scripts/phase-68-sweep.ps1` (SC-4 gate), `.planning/config.json` (nyquist_validation: true)

### Secondary / Tertiary
- None — this research relied entirely on first-party source; no web/Context7 lookups were needed (no external libraries change).

## Metadata

**Confidence breakdown:**
- Seam snapshots + reuse confirmations: HIGH — every file:line read and quoted this session against the current tree.
- Dedicated-exception design: HIGH — grounded in the `ProcessStatusException`/`KeyAbsentException` precedents + `InternalsVisibleTo` + the propagation path.
- Break-list of existing tests: HIGH — each named test read and its exact failing assertion identified.
- D-04 net-effect-identical (Guid.Empty): HIGH for the production seed (verified), MEDIUM-flagged as A1 for a hypothetical non-empty scheduler seed.

**Research date:** 2026-07-21
**Valid until:** stable — internal code shape; re-verify only if `BaseProcessor.cs`/`ProcessorPipeline.cs` change before planning (7-14 days).
