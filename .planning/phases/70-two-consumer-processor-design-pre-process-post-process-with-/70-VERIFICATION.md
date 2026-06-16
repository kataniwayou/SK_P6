---
phase: 70-two-consumer-processor-design-pre-process-post-process-with-
verified: 2026-06-17T00:00:00Z
status: human_needed
score: 12/12 SPEC requirements verified; 13/13 acceptance criteria code-confirmed; 17/17 D-decisions honored
overrides_applied: 0
quality_findings:
  - id: CR-01
    severity: critical
    summary: "Per-dispatch seam state (_entryId/_messageId/_escalateDelete/…) lives on a SINGLETON BaseProcessor, mutated per-consume with no ConcurrentMessageLimit and no memory barrier; concurrent dispatches on one replica race and can delete the wrong entry / write OutputData under the wrong messageId."
    files:
      - "src/BaseProcessor.Core/Processing/BaseProcessor.cs (mutable instance fields _db/_entryId/_messageId/_escalateDelete/…)"
      - "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs (SetSeamState writes them per consume)"
      - "src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs (documented Singleton contract)"
      - "src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs (both endpoint binds set no ConcurrentMessageLimit)"
    impact: "Real correctness gap under CONCURRENT consumes (wrong-key write / cross-message data swap / leaked-to-TTL entry). The 615 hermetic facts drive the seam SERIALLY on freshly-new'd processors, so the suite cannot surface it. Does NOT negate goal delivery — the two-consumer design + 12 requirements are implemented and serially correct — but should be tracked/closed (suggest scoped follow-up: move seam state to an AsyncLocal/Scoped accessor, or switch the author registration to Scoped + ConcurrentMessageLimit on both endpoints)."
  - id: WR-01
    severity: warning
    summary: "SpawnToPost swallows EVERY exception type on exhaust and resolves GetSendEndpoint OUTSIDE the RetryLoop (asymmetric with the keeper Guard pattern)."
  - id: WR-02
    severity: warning
    summary: "PostProcessConsumer trusts a fully wire-shaped DataResult (arbitrary MessageId + ids) and writes OutputData(messageId)/emits Step* with no provenance check — accepted 'trusted internal bus' posture, but a new ingress; worth an explicit SECURITY note or a dr.ProcessorId == context.Id check."
  - id: WR-03
    severity: warning
    summary: "Deserialize JsonException → BuildFailed(d, ex.Message) puts raw parser text (possible payload bytes) on the StepFailed wire, defeating the never-log-payload discipline used elsewhere; prefer a sanitized constant."
human_verification:
  - test: "Solution build + hermetic suite green (req-12 bar)"
    expected: "`dotnet build SK_P.sln -c Release` 0-warn/0-err; `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait \"Category=RealStack\"` → 615 passed / 0 failed / 0 skipped"
    why_human: "Per the verification context this is already GREEN and need not be re-run; recorded here only so the build/test gate is an explicitly human-confirmed checkpoint (verifier did not re-execute the suite, only spot-checked that the code under test implements the SPEC)."
---

# Phase 70: Two-consumer processor design (Pre-Process + Post-Process) with keeper recovery — Verification Report

**Phase Goal:** Replace the single-consumer `ProcessorPipeline` (gate on the `L2[messageId]` slot-array, framework-owned Post) with the pinned two-consumer design: a Pre-Process consumer gating on `L2[entryId]` whose `virtualProcess` seam returns one `<data result>` (downstream) or spawns N to a new Post-Process consumer and returns null (entry), output keyed by `messageId` and written only when `result == completed`, with keeper states `REINJECT`/`INJECT`/`DELETE` redefined accordingly.

**Verified:** 2026-06-17
**Status:** human_needed (all 12 requirements + 13 acceptance criteria code-confirmed; the only open item is the explicit human-confirmed build/test gate; one critical quality finding tracked, see CR-01)
**Re-verification:** No — initial verification

## Goal Achievement

The two-consumer design is delivered against the actual codebase, not merely claimed. The single-consumer slot-array / recovery-pass model is fully retired (no `MessageIndex`, `SlotTtl`, `RunRecoveryAsync`, `RunForwardAsync`, `RetiredSlot`, `ProcessItem`, `ProcessOutcome` in live code — the one residual mention is a doc comment recording their removal). Every SPEC requirement maps to verified, substantive, wired code.

### SPEC Requirements (the 12-requirement coverage unit)

| # | Requirement | Status | Evidence |
|---|-------------|--------|----------|
| 1 | Pre gates on `exist L2[entryId]`; gate-exhaust → REINJECT; clean-absent → return | ✓ VERIFIED | `ProcessorPipeline.cs:65-68`: `KeyExistsAsync(L2ProjectionKeys.ExecutionData(d.EntryId))`, exhaust → `SendKeeper(BuildReinject(d,messageId))`, `!exists.Value → return`. Old slot-array gate gone. |
| 2 | Read + input-validate + deserialize; read-fault → REINJECT; invalid/deserialize-fail → ONE StepFailed, NO entry delete | ✓ VERIFIED | `ProcessorPipeline.cs:72-85,105-109`: read via RetryLoop (KeyAbsentException unifies absent/empty), read-fault → REINJECT; input-invalid → one `BuildFailed` send + `return` with NO KeyDeleteAsync (C-2). Deserialize JsonException caught at the catch-all → one StepFailed, no delete. |
| 3 | Seam returns one `DataResult` or null; null short-circuits before write/send/delete | ✓ VERIFIED | `BaseProcessor.cs:58` seam `Task<DataResult?> ExecuteAsync`; `BaseProcessor`1.cs:20-27,38` author `Task<DataResult?> ProcessAsync`; `ProcessorPipeline.cs:111` `if (dr is null) return;` BEFORE the tail. |
| 4 | Pre inline tail: output-validate, write `L2[messageId]=data` only when completed (INJECT on exhaust), send by result, delete `L2[entryId]` (DELETE on exhaust) | ✓ VERIFIED | `ProcessorPipeline.cs:116-122` carries messageId, calls shared `OutputTail.RunAsync` then `KeyDeleteAsync(ExecutionData)` with delete-exhaust → `BuildDelete`. `OutputTail.cs:62-74` write gated on Completed, write-exhaust → INJECT + return false. |
| 5 | New Post-Process consumer: `data = dr.data`, validate, write on completed, send by result, NEVER touch entryId | ✓ VERIFIED | `PostProcessConsumer.cs:17-31` `IConsumer<DataResult>`, calls `outputTail.RunAsync(ctx.Message, Guid.Empty, …)`; no entryId read/delete. Bound on `queue:{id:D}-post` (`ProcessorStartupOrchestrator.cs:283-288`). |
| 6 | REINJECT redefined: read L2[entryId], re-inject to Pre with envelope `MessageId == messageId`; clean-absent drops | ✓ VERIFIED | `ReinjectConsumer.cs:33-58`: STRLEN presence-read, absent → drop (ReinjectDropped), present → `ep.Send(dispatch, ctx => ctx.MessageId = m.MessageId, …)`. `KeeperReinject.cs:13` carries `MessageId`. |
| 7 | INJECT redefined self-contained: write `L2[messageId]=data` from carried DataResult, delete entry, send by result, never recompute | ✓ VERIFIED | `InjectConsumer.cs:32-59`: `var dr = m.DataResult`, write `OutputData(dr.MessageId)` completed-only, send by result w/ `ctx.MessageId = dr.MessageId`, delete `ExecutionData(m.DeleteEntryId)`. No StringGet/StringLength on input. `KeeperInject.cs:13` embeds whole `DataResult`. |
| 8 | DELETE redefined delete-only: delete `L2[entryId]` only, no orchestrator send | ✓ VERIFIED | `DeleteConsumer.cs:21-22`: single `KeyDeleteAsync(ExecutionData(m.EntryId))`, no send. `KeeperDelete.cs` has no `MessageId`, no RedisKey[] both-key DEL. |
| 9 | Seam helpers SpawnToPost (swallow) + DeleteEntry (escalate DELETE) | ✓ VERIFIED | `BaseProcessor.cs:66-88`: SpawnToPost bounded RetryLoop → SWALLOW (`_onSpawnDropped`) on exhaust (no throw/keeper); DeleteEntry bounded RetryLoop → `_escalateDelete` (KeeperDelete) on exhaust. |
| 10 | Sample two-mode virtualProcess (label/number; spawn-2-on-empty / accumulate-1-on-non-empty; log line) | ✓ VERIFIED | `SampleProcessor.cs:45-76`: empty execId → 2 numbers, log `"{StepLabel} had the following numbers: …"`, 2× `SpawnToPost(NewResult(...), Guid.NewGuid())` (distinct minted execIds), `DeleteEntry()`, `return null`; non-empty → 1 accumulated completed `NewResult … with { ExecutionId = executionId }`, same log. |
| 11 | Resilience invariants preserved (no outbox, UseMessageRetry none, error routing off, bounded RetryLoop→keeper, send-exhaust→throw, jittered messageId TTL, envelope override) | ✓ VERIFIED | Entry + `-post` binds set only `ConfigureConsumer` (no UseMessageRetry/ConfigureError) — `ProcessorStartupOrchestrator.cs:267-288`; every L2 op/send via RetryLoop with per-op escalation; send-exhaust `throw sent.Error!`; jittered `random[ttl,2×ttl]` TTL in `OutputTail.JitteredTtl` + `InjectConsumer.JitteredTtl`; envelope MessageId override on spawn/INJECT/REINJECT. |
| 12 | Migrate tests + supersede old spec doc | ✓ VERIFIED | Recovery/slot facts deleted (`PipelineRecoveryFacts.cs` absent); new `PrePipelineFacts`/`PostProcessConsumerFacts`/`OutputTailFacts`/`SampleProcessorFacts`/`BaseProcessorSeamFacts` present + keeper facts migrated; suite GREEN (615/0/0 per context). `docs/design/processor-keeper-recovery-spec.md:3-10` carries the `> [!WARNING] SUPERSEDED by Phase 70` banner pointing at `70-SPEC.md`. |

**Score:** 12/12 SPEC requirements verified.

### Acceptance Criteria (the 13 SPEC pass/fail checks)

| # | Criterion | Status | Evidence |
|---|-----------|--------|----------|
| 1 | Pre gates on L2[entryId]; gate-fault → REINJECT; clean-absent → return | ✓ | `ProcessorPipeline.cs:65-68`; `PrePipelineFacts` gate-fault + clean-absent facts |
| 2 | Input/deserialize fail → exactly one StepFailed, entry NOT deleted | ✓ | `ProcessorPipeline.cs:81-85,105-109`; `PrePipelineFacts.cs:137,198,219,302` DidNotReceive().KeyDeleteAsync |
| 3 | null seam return → write/send/delete nothing; non-null → tail | ✓ | `ProcessorPipeline.cs:111`; `PrePipelineFacts` null-return fact |
| 4 | Completed: one OutputData write, StepCompleted, entry delete; write-exhaust → INJECT; delete-exhaust → DELETE | ✓ | `OutputTail.cs:62-74` + `ProcessorPipeline.cs:116-122`; `PrePipelineFacts`/`OutputTailFacts` |
| 5 | Non-completed: write skipped, result still sent, entry deleted | ✓ | `OutputTail.cs:57-73` (Failed path skips write, still sends) |
| 6 | Post takes dr.data, writes on completed, sends, no entry read/delete | ✓ | `PostProcessConsumer.cs:30`; `PostProcessConsumerFacts.cs:53-73` DidNotReceive StringGet/KeyDelete |
| 7 | REINJECT envelope MessageId == messageId; drops on absent | ✓ | `ReinjectConsumer.cs:36-58`; `ReinjectConsumerFacts` |
| 8 | INJECT writes OutputData, deletes entry, sends; never recomputes | ✓ | `InjectConsumer.cs:34-59`; `InjectConsumerFacts` |
| 9 | DELETE one delete, zero sends | ✓ | `DeleteConsumer.cs:21-22`; `DeleteConsumerFacts` Assert.Empty(send.Sent) |
| 10 | Seam exposes SpawnToPost + DeleteEntry; sample uses them without hand-rolled retry/keeper | ✓ | `BaseProcessor.cs:66-88`; `SampleProcessor.cs:59-61` |
| 11 | empty-execId → 2 distinct-execId Post msgs, Pre inline nothing; non-empty → 1 completed via tail; both log line | ✓ | `SampleProcessor.cs:45-76`; `SampleProcessorFacts` (2-spawn / 1-completed / log) |
| 12 | No-outbox, no bus retry, no error routing, send-exhaust throw, jittered TTL | ✓ | `ProcessorStartupOrchestrator.cs:267-288`; `OutputTail.cs:38-42,113`; `DispatchBindSequenceFacts` |
| 13 | Processor suite passes; old doc superseded | ✓ (suite green per context) | `docs/design/processor-keeper-recovery-spec.md:3-10`; 615/0/0 |

### Locked Decisions D-01..D-17

All 17 honored. Spot-confirmed: D-01/D-02 unified `DataResult` (single spine, self-contained) — `DataResult.cs`; D-03 `ProcessItem` removed (file absent); D-04 reuses `StepOutcome`; D-05 seam renamed return only; D-06/D-07/D-08/D-09 helpers + swallow/escalate asymmetry — `BaseProcessor.cs`; D-10 `OutputData(messageId)=skp:out:{messageId:D}`; D-11 `MessageIndex`/`SlotTtl` deleted, `ExecutionData` kept — `L2ProjectionKeys.cs`; D-12/D-13 keeper reshape + `KeeperInject` embeds whole `DataResult`; D-14 `ProcessorPipeline` rewritten in-place, recovery deleted; D-15 shared `OutputTail` used by Pre tail + Post; D-16 `PostProcessConsumer` on `queue:{id:D}-post`, bound before MarkHealthy; D-17 test migration (no-analog facts deleted, new facts added).

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/DataResult.cs` | Unified spine record | ✓ VERIFIED | sealed record, StepOutcome Result, MessageId, no IKeeperRecoverable |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | OutputData added, MessageIndex removed | ✓ VERIFIED | `OutputData → skp:out:{messageId:D}`; no MessageIndex; ExecutionData kept |
| `src/Messaging.Contracts/Keeper{Inject,Delete,Reinject}.cs` | Reshaped contracts | ✓ VERIFIED | Inject embeds DataResult+DeleteEntryId; Delete entryId-only; Reinject gains MessageId |
| `src/Keeper/Recovery/{Inject,Delete,Reinject}Consumer.cs` | Redefined keeper states | ✓ VERIFIED | match req 6/7/8 exactly |
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | Shared write-gated tail | ✓ VERIFIED | write-on-completed → INJECT → send; jittered TTL |
| `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs` | Output-only consumer | ✓ VERIFIED | IConsumer<DataResult>, Guid.Empty deleteEntryId |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | Linear Pre flow | ✓ VERIFIED | gate L2[entryId], no recovery/slot code |
| `src/BaseProcessor.Core/Processing/BaseProcessor{,`1}.cs` | Seam → DataResult? + helpers | ✓ VERIFIED | SpawnToPost/DeleteEntry/NewResult |
| `src/Processor.Sample/SampleProcessor.cs` | Two-mode example | ✓ VERIFIED | spawn-2 / accumulate-1 / log line |
| `src/BaseProcessor.Core/Processing/ProcessItem.cs`,`ProcessOutcome.cs` | Deleted from disk | ✓ VERIFIED | both absent |
| `docs/design/processor-keeper-recovery-spec.md` | Superseded marker | ✓ VERIFIED | WARNING banner pointing at 70-SPEC.md |

### Key Link Verification

| From | To | Via | Status |
|------|-----|-----|--------|
| ProcessorPipeline | OutputTail | shared tail after non-null seam return | ✓ WIRED (`:117 outputTail.RunAsync`) |
| ProcessorPipeline | BaseProcessor seam | SetSeamState then ExecuteAsync | ✓ WIRED (`:89-91`) |
| SampleProcessor | Post queue | this.SpawnToPost + this.DeleteEntry | ✓ WIRED (`:59-61`) |
| StartupOrchestrator | queue:{id:D}-post | second ConnectReceiveEndpoint before MarkHealthy | ✓ WIRED (`:283-288`) |
| DI extensions | PostProcessConsumer + OutputTail | AddConsumer + AddScoped | ✓ WIRED (`:87,112`) |
| InjectConsumer | L2[messageId] | StringSetAsync(OutputData) gated on Completed | ✓ WIRED |
| ReinjectConsumer | Pre queue | Send with ctx.MessageId override | ✓ WIRED |
| KeeperInject | DataResult | embedded init property | ✓ WIRED |

### Anti-Patterns Found

No goal-blocking stubs. No placeholders, no `return null` short-circuits that defeat the design (the `return null` paths are the SPEC-mandated Mode-2 / null-seam semantics). The `EntryId = Guid.Empty` on Step* sends is the documented A1 placeholder (orchestrator entryId↔messageId threading SPEC-deferred), not a stub. Quality findings (CR-01 critical + WR-01/02/03) are recorded in frontmatter and below.

### Quality Findings (from 70-REVIEW.md, tracked here)

- **CR-01 (critical, tracked — does NOT block goal):** Per-dispatch seam state on a SINGLETON `BaseProcessor`, mutated per-consume, no `ConcurrentMessageLimit`, no memory barrier. Under concurrent consumes on one replica, consume A can read consume B's clobbered `_entryId`/`_messageId`/`_escalateDelete` → wrong-key OutputData write, cross-message data swap, wrong/leaked entry delete. The 615 hermetic facts construct a fresh `FakeProcessor` per test and drive the seam serially, so the suite cannot surface it. **Verdict impact:** the two-consumer design and all 12 requirements ARE implemented and serially correct; this is a concurrency correctness gap the tests don't cover and should be closed in a scoped follow-up (preferred fix: move seam state to a Scoped/AsyncLocal accessor, keeping the author API identical; alt: Scoped registration + `ConcurrentMessageLimit=1` on both endpoints).
- **WR-01/WR-02/WR-03 (warnings):** SpawnToPost swallow-scope/GetSendEndpoint-outside-RetryLoop; PostProcessConsumer provenance/trust-boundary on the new `-post` ingress; deserialize `ex.Message` leaking payload bytes onto the StepFailed wire. None blocks the goal.

### Human Verification Required

#### 1. Build + hermetic suite green (req-12 bar)

**Test:** `dotnet build SK_P.sln -c Release` and `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait "Category=RealStack"`
**Expected:** 0-warn/0-err build; 615 passed / 0 failed / 0 skipped.
**Why human:** Per the verification context this gate is already GREEN and was not re-run by the verifier; recorded so the build/test checkpoint is an explicit human-confirmed sign-off. The verifier confirmed by code inspection that the code under test implements all 12 SPEC requirements (not merely that tests pass).

### Gaps Summary

No goal gaps. The two-consumer design — Pre-Process gating on `L2[entryId]`, the `DataResult?` seam, the new `PostProcessConsumer`, output keyed by `messageId` written only on `Completed`, and the redefined `REINJECT`/`INJECT`/`DELETE` keeper states — is fully implemented in the actual codebase and matches the pinned SPEC pseudocode line-for-line. The slot-array/recovery-pass model is fully retired. The single open item is the explicitly-human-confirmed build/test gate (already green per context). CR-01 is a tracked critical quality finding (concurrency correctness under concurrent consumes) that does not negate goal delivery but warrants a scoped follow-up.

---

_Verified: 2026-06-17_
_Verifier: Claude (gsd-verifier)_
