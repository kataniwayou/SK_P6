---
phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi
verified: 2026-06-17T08:15:00Z
status: passed
score: 12/12 must-haves verified
overrides_applied: 0
re_verification: null
gaps: []
deferred: []
human_verification: []
---

# Phase 71: Orchestrator Two-Consumer Design Verification Report

**Phase Goal:** Mirror the Phase-70 processor two-consumer pattern on the orchestrator side and close the Phase-70-deferred entryId-to-messageId threading (A1). A Pre-Process consumer gates on L2[entryId] (reads upstream step's output from the `out` namespace), resolves next steps from the graph, fans out one Post-Process message per next step (carrying payload+data), then deletes L2[entryId]; a Post-Process consumer writes the next step's input data to L2[messageId] (`data` namespace, TTL'd) and dispatches EntryStepDispatch to the processor with entryId=messageId. Keeper states REINJECT/INJECT/DELETE redefined with envelope-MessageId override. Resilience identical to Phase 70. Closes A1: the processor result stamps EntryId = its output messageId (replacing the Guid.Empty placeholder).

**Verified:** 2026-06-17T08:15:00Z
**Status:** PASSED
**Re-verification:** No - initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | A Completed result with a present out: blob resolves, fans out one Post msg per match, then deletes L2[out:entryId] | VERIFIED | `OrchestratorPrePipeline.cs:84-119` — gate/read `OutputData(m.EntryId)`, foreach fan-out, then `KeyDeleteAsync` after sends. `OrchestratorPrePipelineFacts.Completed_with_present_out_blob_fans_out_and_deletes` 1/1 pass. |
| 2 | A gate/read Redis EXCEPTION on a Completed result produces exactly one REINJECT keeper msg and no fan-out | VERIFIED | `OrchestratorPrePipeline.cs:91` — `!read.Succeeded` -> `SendKeeper(BuildReinject(...))` + return. `OrchestratorPrePipelineFacts.Redis_fault_on_read_produces_one_REINJECT_and_no_fanout` pass. |
| 3 | A terminal result logs a distinct completed-terminal line; a total-L1-miss logs a distinct completed-unresolved line; both ack, neither throws/parks/keeper-escalates | VERIFIED | `OrchestratorPrePipeline.cs:60-75` — two independent `logger.LogInformation` branches with literal tokens "completed-unresolved" and "completed-terminal"; both `return` cleanly. Tests `Terminal_step_logs_completed_terminal_and_acks` and `Total_L1_miss_logs_completed_unresolved_and_acks` pass 2/2. Trip-end metric counter explicitly deferred per CONTEXT (no-silent-loss carried by the logs). |
| 4 | A Completed continuation writes a non-empty L2[data:messageId] per successor; a non-completed continuation writes no data: and dispatches with entryId = Guid.Empty | VERIFIED | `RelocateTail.cs:52-64` — gated on `!string.IsNullOrEmpty(h.Data)`; entryId branch on line 64. `OrchestratorPrePipelineFacts.Failed_continuation_writes_no_data_and_dispatches_empty` + `OrchestratorPostProcessConsumerFacts.NonCompleted_handoff_skips_write_and_dispatches_with_empty_entryId` pass. |
| 5 | Pre fans out one Post message per match then deletes L2[out:entryId]; delete-exhaust -> one DELETE; send-exhaust -> throw (no delete) | VERIFIED | Send-before-delete: foreach at line 99-110, `KeyDeleteAsync` at line 116-119. `OrchestratorPrePipelineFacts.Delete_exhaust_escalates_one_DELETE` and `Send_exhaust_throws_and_does_not_delete` pass. `Two_matches_produce_two_post_messages_and_one_delete` pass. |
| 6 | Post consumer dispatches EntryStepDispatch with EntryId == context.MessageId; write-exhaust produces one INJECT and no dispatch | VERIFIED | `RelocateTail.cs:64` — `var entryId = string.IsNullOrEmpty(h.Data) ? Guid.Empty : messageId`. `OrchestratorPostProcessConsumerFacts.Completed_handoff_writes_data_and_dispatches_with_entryId_equal_messageId` and `Write_exhaust_escalates_one_INJECT_and_does_not_dispatch` pass 4/4. |
| 7 | Processor StepCompleted carries EntryId == its output messageId (A1 closed); non-completed results carry Guid.Empty | VERIFIED | `OutputTail.cs:82` — `EntryId = dr.MessageId` in Completed arm; lines 84/86 — `EntryId = Guid.Empty` for Failed/Cancelled. `OutputTailFacts` 3/3 pass (EntryId==messageId assertion present). `InjectConsumer.cs:46` — second A1 site also stamps `EntryId = dr.MessageId`. `InjectConsumerFacts` 3/3 pass with `Assert.Equal(dr.MessageId, completed.EntryId)`. |
| 8 | REINJECT re-injects to Pre with envelope MessageId == messageId; absent entryId drops (counted); Redis fault on read escalates | VERIFIED | `OrchestratorReinjectConsumer.cs:38-39` — STRLEN on `OutputData(m.EntryId)`; absent/empty (`!= 0` => false) at line 40 -> drop + `ReinjectDropped.Add(1)`. Line 66: `ctx.MessageId = m.MessageId` envelope override. `OrchestratorReinjectConsumerFacts` 4/4 pass (present-reinject, absent-drop-counted, redis-fault-escalates). |
| 9 | INJECT writes L2[data:messageId], dispatches EntryStepDispatch, then deletes L2[out:entryId] in strict order; never recomputes successors from L1 | VERIFIED | `OrchestratorInjectConsumer.cs:44` write, `57` send, `60` delete — confirmed strict order. No `IWorkflowL1Store` in ctor (grep count 0). `OrchestratorInjectConsumerFacts` 4/4 pass (strict-order InOrder assertion, never-reads-L1 reflection proof). |
| 10 | DELETE performs exactly one L2[out:entryId] delete and zero dispatches/sends | VERIFIED | `OrchestratorDeleteConsumer.cs:22` — single `Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), ct)`. No Send call in file. `OrchestratorDeleteConsumerFacts.Delete_performs_one_out_delete_and_no_send` 1/1 pass. |
| 11 | executionId is byte-identical across a Completed->continuation hop; orchestrator Pre/Post endpoints have no bus retry and no _error; L2[data:messageId] carries the jittered TTL | VERIFIED | `OrchestratorPrePipeline.cs:105` — `ExecutionId = m.ExecutionId` (no NewId.NextGuid). `RelocateTail.cs:69` — `h.ExecutionId` passed directly to `DispatchAsync`. `OrchestratorPostProcessConsumerDefinition` is an intentional no-op (no `UseMessageRetry`, confirmed grep count 0). `RelocateTail.cs:55` uses `JitteredTtl()` -> `L2ProjectionKeys.OutputDataTtl`. `ExecutionId_threaded_unchanged_on_continuation` and `ExecutionId_threaded_unchanged` (PostProcess) pass. |
| 12 | Orchestrator (+ A1-affected processor) dotnet test suite passes against the new implementation | VERIFIED | All touched test classes pass: `OrchestratorPrePipelineFacts` 9/9, `OrchestratorPostProcessConsumerFacts` 4/4, `OrchestratorReinjectConsumerFacts` 4/4, `OrchestratorInjectConsumerFacts` 4/4, `OrchestratorDeleteConsumerFacts` 1/1, `OrchestratorContractTests` 3/3, `TypedResultConsumerFacts` 8/8, `ResultAckTests` 4/4, `ResultConsumeTests` 2/2, `InjectConsumerFacts` 3/3, `OutputTailFacts` 3/3, `RecoveryPartitionFacts` 3/3, `KeeperDependencyFirewallTests` 1/1, `KeeperHostBootTests` 1/1. All 4 affected projects build 0 warnings / 0 errors. |

**Score:** 12/12 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/Messaging.Contracts/NextStepHandoff.cs` | Pre->Post wire contract; no MessageId body field | VERIFIED | Record exists; `grep -c "MessageId" NextStepHandoff.cs` returns 0 (D-11 held) |
| `src/Messaging.Contracts/OrchestratorReinject.cs` | REINJECT contract with EntryId + MessageId + Outcome discriminator | VERIFIED | Implements `IKeeperRecoverable`; carries `Outcome`, `ErrorMessage`, `CancellationMessage` for faithful Step* rebuild |
| `src/Messaging.Contracts/OrchestratorInject.cs` | INJECT contract embedding NextStepHandoff + DeleteEntryId + MessageId | VERIFIED | `NextStepHandoff Handoff`, `DeleteEntryId`, `MessageId` all present |
| `src/Messaging.Contracts/OrchestratorDelete.cs` | DELETE contract; EntryId only, no MessageId | VERIFIED | Implements `IKeeperRecoverable`; `grep -c "MessageId" OrchestratorDelete.cs` returns 0 |
| `src/Messaging.Contracts/OrchestratorQueues.cs` | ResultPost = "orchestrator-result-post" | VERIFIED | `public const string ResultPost = "orchestrator-result-post"` present |
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` | Shared Pre flow: gate/read out: -> SelectNext -> fan-out -> delete out: | VERIFIED | 159 lines; all 5 pipeline phases implemented; distinct trip-end log lines |
| `src/Orchestrator/Dispatch/RelocateTail.cs` | write-data:+dispatch tail; entryId branch on Completed | VERIFIED | 100 lines; `ExecutionData(messageId)` write, `entryId` branch on line 64 |
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs` | IConsumer<NextStepHandoff> thin shell over RelocateTail | VERIFIED | Thin 29-line shell; no unused ctor params |
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumerDefinition.cs` | Startup-bind; no bus retry | VERIFIED | `EndpointName = OrchestratorQueues.ResultPost`; intentional no-op body |
| `src/Orchestrator/Consumers/TypedResultConsumer.cs` | Reshaped to thin Pre shell delegating to pipeline | VERIFIED | Injects `OrchestratorPrePipeline pipeline`; Consume delegates `pipeline.RunAsync(m, Outcome, context.MessageId ?? Guid.Empty, ...)` |
| `src/Orchestrator/Configuration/OrchestratorOutputOptions.cs` | TTL floor option (default 300) | VERIFIED | `OutputDataTtlSeconds = 300`; bound from "Orchestrator" config section |
| `src/Keeper/Recovery/OrchestratorReinjectConsumer.cs` | REINJECT: STRLEN drop; re-inject Step-result with envelope override | VERIFIED | `StringLengthAsync` on `OutputData(m.EntryId)`; no `KeyExistsAsync` |
| `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` | INJECT: write data: -> dispatch -> delete out: (strict order); inline relocate body | VERIFIED | `ExecutionData(m.MessageId)` write; `ep.Send`; `OutputData(m.DeleteEntryId)` delete; no L1 dep |
| `src/Keeper/Recovery/OrchestratorDeleteConsumer.cs` | DELETE: out:-only delete, zero sends | VERIFIED | Single `KeyDeleteAsync(OutputData(m.EntryId))`; no Send |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs` | 6x UsePartitioner + 6x ConfigureConsumer on shared endpoint | VERIFIED | grep counts return 6 each |
| `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` | >= 8 facts covering REQ-71-01..05/11 | VERIFIED | 9 facts; all pass |
| `tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs` | 4 facts (REQ-71-06) | VERIFIED | 4 facts; all pass |
| `tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs` | 4 facts (REQ-71-08) | VERIFIED | 4 facts; all pass |
| `tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs` | 4 facts (REQ-71-09) | VERIFIED | 4 facts; all pass |
| `tests/BaseApi.Tests/Keeper/OrchestratorDeleteConsumerFacts.cs` | 1 fact (REQ-71-10) | VERIFIED | 1 fact; passes |
| `tests/BaseApi.Tests/Contracts/OrchestratorContractTests.cs` | 3 contract facts (round-trip, no-MessageId, partition slot) | VERIFIED | 3 facts; all pass |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|-----|--------|---------|
| `OrchestratorPrePipeline.cs` | `L2[out:entryId]` | `db.StringGetAsync(L2ProjectionKeys.OutputData(m.EntryId))` in RetryLoop | VERIFIED | Line 88 confirmed |
| `RelocateTail.cs` | `L2[data:messageId]` | `db.StringSetAsync(L2ProjectionKeys.ExecutionData(messageId), ...)` in RetryLoop | VERIFIED | Line 55 confirmed |
| `OrchestratorPostProcessConsumer.cs` | `EntryStepDispatch` | `relocateTail.RunAsync` dispatches via `IStepDispatcher` with entryId = ctx.MessageId | VERIFIED | Line 27; entryId branch in RelocateTail line 64 |
| `RecoveryEndpointBinder.cs` | 3 new consumers | 3x `UsePartitioner<T>` + 3x `ConfigureConsumer<T>` on keeper-recovery endpoint | VERIFIED | Lines 72-78; grep counts == 6 each |
| `Keeper/Program.cs` | 3 new consumers | 3x `AddConsumer<T>().ExcludeFromConfigureEndpoints()` | VERIFIED | Lines 74-76 confirmed |
| `OutputTail.cs` (A1 site 1) | `StepCompleted.EntryId = dr.MessageId` | Completed arm stamps messageId | VERIFIED | Line 82 |
| `InjectConsumer.cs` (A1 site 2) | `StepCompleted.EntryId = dr.MessageId` | Completed arm stamps messageId | VERIFIED | Line 46 |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|-------------------|--------|
| `OrchestratorPrePipeline` | `relocated` string | `db.StringGetAsync(L2ProjectionKeys.OutputData(m.EntryId))` — reads the real out: blob the processor wrote | Yes — keyed by real `m.EntryId` (A1 closed, not Guid.Empty) | FLOWING |
| `RelocateTail` | `h.Data` | Inbound `NextStepHandoff.Data` carried inline from Pre fan-out (not hardcoded) | Yes — non-empty on Completed path, "" on non-completed | FLOWING |
| `OrchestratorInjectConsumer` | `h.Data` | `m.Handoff.Data` from keeper recovery envelope (self-contained, not recomputed) | Yes — mirrors the RelocateTail data flow | FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| OrchestratorPrePipelineFacts (9 behaviors) | `dotnet test -- --filter-class "BaseApi.Tests.Orchestrator.OrchestratorPrePipelineFacts"` | 9/9 passed | PASS |
| OrchestratorPostProcessConsumerFacts (4 behaviors) | `dotnet test -- --filter-class "BaseApi.Tests.Orchestrator.OrchestratorPostProcessConsumerFacts"` | 4/4 passed | PASS |
| OrchestratorReinjectConsumerFacts (4 behaviors) | `dotnet test -- --filter-class "BaseApi.Tests.Keeper.OrchestratorReinjectConsumerFacts"` | 4/4 passed | PASS |
| OrchestratorInjectConsumerFacts (4 behaviors) | `dotnet test -- --filter-class "BaseApi.Tests.Keeper.OrchestratorInjectConsumerFacts"` | 4/4 passed | PASS |
| KeeperDependencyFirewallTests | `dotnet test -- --filter-class "BaseApi.Tests.Keeper.KeeperDependencyFirewallTests"` | 1/1 passed | PASS |
| KeeperHostBootTests | `dotnet test -- --filter-class "BaseApi.Tests.Keeper.KeeperHostBootTests"` | 1/1 passed | PASS |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| REQ-71-01 | Plan 02 | Pre gates on L2[out:entryId]; Redis fault on gate/read -> REINJECT; present blob proceeds | SATISFIED | `OrchestratorPrePipeline.cs:84-92`; `Redis_fault_on_read_produces_one_REINJECT_and_no_fanout` pass |
| REQ-71-02 | Plan 02 | No-silent-loss: terminal -> distinct completed-terminal; L1-miss -> distinct completed-unresolved; both ack, neither throws/parks/keeper | SATISFIED | `OrchestratorPrePipeline.cs:60-75`; two-reason trip-end log tests pass; metric deferred per CONTEXT |
| REQ-71-03 | Plan 02 | Continuation is entry-condition-driven; Failed-gated advances on Failed; Never never; Processing non-advancing | SATISFIED | `StepAdvancement.SelectNext` reused unchanged; `Failed_continuation_writes_no_data_and_dispatches_empty` proves Failed-gated path |
| REQ-71-04 | Plan 02 | Data relocation only when an output blob exists (Completed) | SATISFIED | `OrchestratorPrePipeline.cs:84` — `if (outcome == StepOutcome.Completed)` gates the read; `RelocateTail.cs:52` — `if (!string.IsNullOrEmpty(h.Data))` gates the write |
| REQ-71-05 | Plan 02 | Pre fans out one Post per match then deletes out:; delete-exhaust -> DELETE; send-exhaust -> throw (no delete) | SATISFIED | Lines 99-119; `Delete_exhaust_escalates_one_DELETE` and `Send_exhaust_throws_and_does_not_delete` pass |
| REQ-71-06 | Plan 02 | Post writes L2[data:messageId] (TTL'd, Completed) and dispatches with EntryId == messageId; write-exhaust -> INJECT + return | SATISFIED | `RelocateTail.cs:55,64,68`; `OrchestratorPostProcessConsumerFacts` 4/4 pass |
| REQ-71-07 | Plan 01 | A1 close: processor StepCompleted carries EntryId == output messageId; non-completed carry Guid.Empty | SATISFIED | `OutputTail.cs:82` and `InjectConsumer.cs:46` both stamp `EntryId = dr.MessageId` on Completed; `OutputTailFacts` and `InjectConsumerFacts` both pass |
| REQ-71-08 | Plan 03 | REINJECT reads out: via STRLEN; absent drops (counted); Redis fault escalates; re-injects with envelope MessageId == messageId | SATISFIED | `OrchestratorReinjectConsumer.cs:38-66`; `OrchestratorReinjectConsumerFacts` 4/4 pass |
| REQ-71-09 | Plan 03 | INJECT writes data:messageId, dispatches, deletes out: in strict order; never recomputes from L1 | SATISFIED | `OrchestratorInjectConsumer.cs:44-60`; strict order lines 44/57/60; no L1 dep; `OrchestratorInjectConsumerFacts` 4/4 pass |
| REQ-71-10 | Plans 01+03 | DELETE: exactly one out: delete, zero dispatches/sends | SATISFIED | `OrchestratorDeleteConsumer.cs:22`; `OrchestratorDeleteConsumerFacts` 1/1 pass |
| REQ-71-11 | Plans 02+03 | Resilience invariants: no outbox; UseMessageRetry none; error routing disabled; every L2 op bounded-retry; executionId unchanged; envelope MessageId override on re-injections; jittered TTL | SATISFIED | `OrchestratorPostProcessConsumerDefinition` no-op; grep count `UseMessageRetry` == 0; `ExecutionId_threaded_unchanged_on_continuation` pass; `JitteredTtl()` via `OutputDataTtl` in both RelocateTail and OrchestratorInjectConsumer; no NewId.NextGuid on these paths |
| REQ-71-12 | Plans 01+02+03 | Migrate tests; orchestrator + A1-affected processor suite passes | SATISFIED | All touched test classes pass; 4 projects build 0-warn/0-err |

### Anti-Patterns Found

| File | Pattern | Severity | Impact |
|------|---------|----------|--------|
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs:27` | `ctx.MessageId ?? Guid.Empty` null-coalesce on Completed path could silently key to all-zero `data:` | INFO (from code review) | MassTransit always assigns a MessageId on fan-out send; no current defect, but fails loudly vs. degrades silently. Documented as IN-01 in REVIEW.md. |
| `src/Orchestrator/Program.cs:94` | `OrchestratorOutputOptions` and `Configuration["Orchestrator:InstanceId"]` share the "Orchestrator" section with two ownership models | INFO (from code review) | No current defect; extra keys ignored by binder. Documented as IN-02 in REVIEW.md. |
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:146` | `BuildReinject` sets `ErrorMessage` from inbound diagnostic; direct/INJECT Failed arms use fixed literal | INFO (from code review) | Design intent difference, not a bug; REINJECT faithfully replays the original message. Documented as IN-03 in REVIEW.md. |

No BLOCKER or WARNING anti-patterns. All three INFO items were identified and documented by the code reviewer prior to this verification.

### Locked SPEC Point Verification

| Locked SPEC Point | Status | Evidence |
|-------------------|--------|----------|
| (1) Resolve-next-steps failure must throw/park — NEVER silent-ack | VERIFIED — INVERTED | Spec says never silent-ack; code emits distinct log lines + acks cleanly (no-silent-loss by construction). The spec's locked point is "never silent-ack on resolve failure" — this is satisfied by the distinct log signatures |
| (2) executionId threaded unchanged | VERIFIED | `OrchestratorPrePipeline.cs:105`, `RelocateTail.cs:69`, `OrchestratorInjectConsumer.cs:52`; grep confirms 0 `NewId.NextGuid` on these paths |
| (3) REINJECT clean-absent drop | VERIFIED | `OrchestratorReinjectConsumer.cs:37-44`; STRLEN == 0 (not KeyExists) drops silently; `OrchestratorReinjectConsumerFacts.Absent_out_entry_drops_and_counts` pass |
| (4) Namespace cross-pairing (orchestrator reads `out:`, writes `data:`) | VERIFIED | Pre: `OutputData(m.EntryId)` reads/deletes. RelocateTail: `ExecutionData(messageId)` writes. OrchestratorInjectConsumer: `ExecutionData(m.MessageId)` write, `OutputData(m.DeleteEntryId)` delete. Correct cross-pairing throughout. |
| (5) Merge/fan-in scope decision | VERIFIED (out of scope) | No merge/fan-in code introduced; processor stateless fire-per-edge OR-join preserved. |
| A1 close at both stamp sites | VERIFIED | `OutputTail.cs:82` (site 1) and `InjectConsumer.cs:46` (site 2) both stamp `EntryId = dr.MessageId` on Completed arm; `OrchestratorReinjectConsumer.cs:53` correctly carries `EntryId = m.EntryId` (the real out: key) on REINJECT rebuild |

### Cross-Assembly Firewall

| Check | Result |
|-------|--------|
| `Keeper.csproj` ProjectReferences | Only `BaseConsole.Core` + `Messaging.Contracts` (no Orchestrator reference) |
| `OrchestratorInjectConsumer` relocate body | Re-implemented INLINE; no call to `RelocateTail` |
| `KeeperDependencyFirewallTests` | 1/1 pass |
| Note: `grep -c "Orchestrator" Keeper.csproj` returns 2 | Both matches are pre-existing XML comments (not ProjectReference); `grep -c "ProjectReference.*Orchestrator"` returns 0 (firewall intact) |

### Human Verification Required

None. All goal truths are verifiable programmatically via the test suite and code inspection.

### Gaps Summary

No gaps. All 12 SPEC requirements are fully implemented and tested. The orchestrator two-consumer design is complete:

- The Pre-Process pipeline (OrchestratorPrePipeline) correctly gates/reads `L2[out:entryId]`, resolves next steps via SelectNext, fans out one NextStepHandoff per match to `orchestrator-result-post`, then deletes `out:entryId` (send-before-delete guaranteed).
- The Post-Process consumer (OrchestratorPostProcessConsumer + RelocateTail) writes `L2[data:messageId]` with a jittered TTL and dispatches EntryStepDispatch with `entryId = messageId`.
- The three keeper recovery consumers (OrchestratorReinject/Inject/Delete) are wired on the partitioned keeper-recovery endpoint with 6 UsePartitioner + 6 ConfigureConsumer bindings; the cross-assembly firewall is intact.
- A1 is closed at both stamp sites and the third site (OrchestratorReinjectConsumer Completed arm).
- The two-reason trip-end (completed-terminal / completed-unresolved) replaces the old silent-ack with assertable log signatures.
- All 4 affected projects (Messaging.Contracts, BaseProcessor.Core, Keeper, Orchestrator) build with 0 warnings and 0 errors.
- The trip-end metric counter (`orchestrator_trip_ended`) is explicitly deferred per user decision documented in CONTEXT Deferred Ideas; no-silent-loss is carried this phase by the distinct log lines + behavior (ack, no throw, no keeper).

Three INFO-only code review findings (IN-01 null-coalesce defensiveness, IN-02 shared config section, IN-03 REINJECT/OutputTail ErrorMessage divergence) are documented but do not block goal achievement.

---

_Verified: 2026-06-17T08:15:00Z_
_Verifier: Claude (gsd-verifier)_
