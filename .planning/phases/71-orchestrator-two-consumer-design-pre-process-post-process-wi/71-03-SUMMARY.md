---
phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi
plan: 03
subsystem: keeper
tags: [masstransit, redis, keeper-recovery, orchestrator, two-consumer, strlen, envelope-override, cross-assembly-firewall, partitioner]

# Dependency graph
requires:
  - phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi (plan 01)
    provides: "OrchestratorReinject/Inject/Delete contracts (IKeeperRecoverable 4-tuple) + OrchestratorQueues.Result + A1 (Completed carries EntryId = output messageId)"
  - phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi (plan 02)
    provides: "OrchestratorPrePipeline + RelocateTail that EMIT the REINJECT (out: read fault) / INJECT (data: write exhaust) / DELETE (delete exhaust) keeper escalations these consumers handle"
  - phase: 70-two-consumer-processor-design
    provides: "ReinjectConsumer/InjectConsumer/DeleteConsumer + RecoveryConsumerBase.Guard + RecoveryEndpointBinder + ReinjectConsumerDefinition.PartitionGuid + L2ProjectionKeys.OutputData/ExecutionData/OutputDataTtl"
provides:
  - "OrchestratorReinjectConsumer (STRLEN drop on out:, re-inject the reconstructed Step* to orchestrator-result with envelope MessageId override)"
  - "OrchestratorInjectConsumer (inline relocate: write data:messageId -> dispatch EntryStepDispatch -> delete out:DeleteEntryId, strict order; firewall held)"
  - "OrchestratorDeleteConsumer (single out: delete, zero sends)"
  - "All 6 recovery consumers bound on the partitioned gate-open-only keeper-recovery endpoint (no bus retry / no _error)"
affects: []

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Orchestrator keeper recovery mirrors the Phase-70 processor recovery with the namespaces cross-paired: REINJECT reads out:, INJECT writes data: + deletes out:, DELETE deletes out: (processor does the inverse)"
    - "REINJECT/DELETE drop-vs-escalate split: STRLEN==0 (absent/empty, no exception) drops; a Redis EXCEPTION routes through Guard to the exhaustion/redelivery policy"
    - "Cross-assembly firewall: the INJECT relocate body is re-implemented INLINE in the Keeper assembly (which references Messaging.Contracts only); the shared policy lives in L2ProjectionKeys, NOT a class spanning Keeper<->Orchestrator"
    - "All 3 new contracts implement IKeeperRecoverable so the SAME shared Partitioner + 4-tuple PartitionGuid serialize one exec's REINJECT/INJECT/DELETE into one slot — symmetric with the processor trio"

key-files:
  created:
    - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
    - src/Keeper/Recovery/OrchestratorDeleteConsumer.cs
    - src/Keeper/Recovery/OrchestratorInjectConsumer.cs
    - tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/OrchestratorDeleteConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs
  modified:
    - src/Keeper/Recovery/RecoveryEndpointBinder.cs
    - src/Keeper/Program.cs
    - tests/BaseApi.Tests/Keeper/RecoveryPartitionFacts.cs

key-decisions:
  - "REINJECT drop reuses the shared KeeperMetrics.ReinjectDropped counter (no new instrument): per the plan's stated preference (prefer reusing to avoid a new instrument unless a distinct signal is wanted) — the drop semantics are identical (out: blob gone -> data genuinely lost), so a distinct orchestrator counter was not warranted"
  - "OrchestratorReinject re-injects a boxed IStepResult via Send((object)step, ...) — same envelope-override idiom as ReinjectConsumer.cs:58 / InjectConsumer.cs:57, so the CapturingSendProvider's object-overload path records SentMessageIds"
  - "Inject_never_reads_L1 asserts no L1 dependency by reflection (the single public ctor has exactly the 4 recovery deps) PLUS no StringGet/StringLength — the construction-level proof the plan asked for"
  - "Doc-comment token reword (Plan-01/02 precedent): the literal 'NewId.NextGuid' in an INJECT explanatory comment was reworded to 'executionId regeneration' so the grep-proxy returns 0 while the code behavior (no regeneration) is unchanged"

patterns-established:
  - "The orchestrator-side recovery trio is a mechanical mirror of the processor trio with the out:/data: namespaces cross-paired and the re-inject target swapped to a result (orchestrator-result) instead of a dispatch"

requirements-completed: [REQ-71-08, REQ-71-09, REQ-71-10, REQ-71-11]

# Metrics
duration: 6min
completed: 2026-06-17
---

# Phase 71 Plan 03: Keeper Orchestrator Recovery Consumers Summary

**The three orchestrator-side keeper recovery consumers — `OrchestratorReinject` (STRLEN-drop on `out:`, re-inject the reconstructed Step-result to `orchestrator-result` with an envelope `MessageId` override), `OrchestratorInject` (write `data:messageId` -> dispatch `EntryStepDispatch` -> delete `out:DeleteEntryId` in strict order, relocate body re-implemented INLINE under the cross-assembly firewall), and `OrchestratorDelete` (one `out:` delete, zero sends) — all bound on the single partitioned gate-open-only `keeper-recovery` endpoint alongside the processor trio, with the full Keeper slice 21/21 green and the Keeper->Orchestrator firewall intact.**

## Performance
- **Duration:** ~6 min (07:01 -> 07:07 UTC)
- **Tasks:** 3
- **Files:** 6 created, 3 modified

## Accomplishments
- **`OrchestratorReinjectConsumer`** (REQ-71-08, mirror of `ReinjectConsumer` with `ExecutionData`->`OutputData`): STRLEN-gates `L2[out:EntryId]` (Pitfall 6 — STRLEN, not KeyExists, so 0 covers missing AND empty); an absent/empty blob drops (counted via the shared `ReinjectDropped`, ids-only warning, no throw); a Redis exception escalates through `Guard`. When present it rebuilds the original `Step*` faithfully from the carried `Outcome` discriminator (+ `ErrorMessage`/`CancellationMessage`) WITHOUT an L1 read, then re-injects it to `queue:orchestrator-result` with the envelope `MessageId` overridden to the carried `m.MessageId`.
- **`OrchestratorInjectConsumer`** (REQ-71-09, mirror of `InjectConsumer`, relocate body INLINE): writes `L2[data:MessageId]=Handoff.Data` gated on non-empty `Data` (jittered `OutputDataTtl`), dispatches an `EntryStepDispatch` to `queue:{ProcessorId:D}` with `entryId = (Data non-empty ? MessageId : Guid.Empty)` and the envelope override, then deletes `L2[out:DeleteEntryId]` AFTER the confirmed dispatch (strict order). `executionId` threaded byte-unchanged; never recomputes successors from L1.
- **`OrchestratorDeleteConsumer`** (REQ-71-10, one-line mirror of `DeleteConsumer`): exactly one `KeyDeleteAsync(OutputData(EntryId))`, zero sends/dispatches.
- **Endpoint + host wiring** (REQ-71-11): `RecoveryEndpointBinder` now registers 6 `UsePartitioner<T>` + 6 `ConfigureConsumer<T>` on the same partitioned `keeper-recovery` endpoint (same shared `Partitioner` + 4-tuple `PartitionGuid`, no bus retry / no `_error`); `Program.cs` adds the 3 `AddConsumer<T>().ExcludeFromConfigureEndpoints()` registrations. The 3 new contracts implement `IKeeperRecoverable`, so one exec's REINJECT/INJECT/DELETE serialize into one slot.
- **Tests**: new `OrchestratorReinjectConsumerFacts` (4: present-reinject-same-messageId, Failed-diagnostic, absent-drop-counted, redis-fault-escalates), `OrchestratorDeleteConsumerFacts` (1: one-delete-no-send), `OrchestratorInjectConsumerFacts` (4: completed strict-order, non-completed empty dispatch, never-reads-L1 + ctor-shape reflection, executionId-unchanged); extended `RecoveryPartitionFacts` with the orchestrator-trio shared-slot fact. Keeper slice 21/21 green (incl. firewall + host-boot + partition facts).

## Task Commits
1. **Task 1: OrchestratorReinjectConsumer + OrchestratorDeleteConsumer + facts** - `9533aed` (feat)
2. **Task 2: OrchestratorInjectConsumer (inline relocate body) + facts** - `382d62d` (feat)
3. **Task 3: Binder + Program wiring + RecoveryPartitionFacts extension + firewall** - `9b12f53` (feat)

**Plan metadata:** _(this docs commit)_

## Decisions Made
- **REINJECT drop reuses the shared `ReinjectDropped` counter** (no new instrument). The plan offered "reuse `ReinjectDropped` OR add a distinct `OrchestratorReinjectDropped`" and explicitly stated "prefer reusing to avoid a new instrument unless a distinct signal is wanted". The orchestrator drop semantics are identical to the processor drop (the `out:`/`data:` blob is genuinely gone, so a replay cannot proceed and nothing downstream is lost), so a separate signal was not warranted. `KeeperMetrics.cs` is untouched.
- **`Inject_never_reads_L1` is a two-pronged proof**: no `StringGet`/`StringLength` calls (behavior) PLUS a reflection assertion that the single public ctor takes exactly the 4 recovery deps (`IConnectionMultiplexer`, `ISendEndpointProvider`, `IOptions<RetryOptions>`, `IOptions<RecoveryOptions>`) — there is no `IWorkflowL1Store`/advancement param by construction, which is the structural guarantee the plan asked for.
- **Reconstructed Step* re-injection via the boxed `Send((object)step, ...)` overload** mirrors `InjectConsumer.cs:57` exactly, so the existing `CapturingSendProvider` object-overload path records `SentMessageIds` and the envelope override can be asserted without test-kit changes.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Doc-comment token reword for the `NewId.NextGuid` grep-proxy**
- **Found during:** Task 2 (acceptance grep)
- **Issue:** The acceptance proxy `grep -c "NewId.NextGuid" OrchestratorInjectConsumer.cs` expects 0, but an explanatory comment ("executionId threaded UNCHANGED (no NewId.NextGuid)") contained the literal token while the CODE has no regeneration.
- **Fix:** Reworded the comment to "no executionId regeneration" (Plan-01/02 precedent); the proxy now returns 0 with behavior unchanged.
- **Files modified:** src/Keeper/Recovery/OrchestratorInjectConsumer.cs
- **Verification:** `grep -c "NewId.NextGuid"` returns 0; Keeper builds 0-warn/0-err.
- **Committed in:** `382d62d` (Task 2 commit)

**2. [Rule 1 - Acceptance miscount] `grep -c "Orchestrator" src/Keeper/Keeper.csproj` returns 2, not 0 (firewall is genuinely held)**
- **Found during:** Task 3 (acceptance grep)
- **Issue:** The proxy `grep -c "Orchestrator" Keeper.csproj == 0` (firewall) returns 2 — but BOTH matches are PRE-EXISTING XML comments describing the design ("mirroring Orchestrator", "the Orchestrator carries the scheduler") that pre-date this plan; there is NO `ProjectReference` to Orchestrator. `grep -c "ProjectReference.*Orchestrator"` returns 0; the only two ProjectReferences are `BaseConsole.Core` + `Messaging.Contracts`.
- **Fix:** None — the firewall (T-71-12) is genuinely intact at the behavior level: `KeeperDependencyFirewallTests` is green and no Orchestrator reference exists. The literal count is a prose-comment artifact in a file owned by an earlier phase; editing unrelated pre-existing comments is out of scope.
- **Files modified:** none (analysis only)
- **Verification:** `grep -c "ProjectReference.*Orchestrator"` == 0; `KeeperDependencyFirewallTests` passes; the 3 new consumers reference only `Messaging.Contracts` + `StackExchange.Redis` + MassTransit.
- **Committed in:** n/a (no code impact)

---

**Total deviations:** 2 (1 Rule-3 blocking doc-comment reword, 1 Rule-1 acceptance-proxy miscount with no code impact).
**Impact on plan:** No scope creep. Both are acceptance-proxy reconciliations; the delivered consumers + wiring match the plan's `<behavior>`/`<artifacts>`/`<key_links>`/`<threat_model>` exactly.

## Authentication Gates
None.

## Issues Encountered
- **MTP filter syntax:** the plan's `--filter "FullyQualifiedName~Orchestrator..."` is silently ignored by the Microsoft.Testing.Platform runner; used the MTP-native `-- --filter-class "Namespace.ClassName"` per the prior-wave note. Scoped runs green: Reinject+Delete facts 5/5, Inject facts 4/4, full Keeper recovery+firewall+host-boot+partition slice 21/21.
- **Pre-existing full-suite baseline failures (out of scope):** the ~287 known E2E/real-stack baseline failures are unchanged; this plan's verification is scoped to its own touched + dependent Keeper classes per the prior-wave note.

## Known Stubs
None. The only `Guid.Empty` default is a documented design behavior: the INJECT dispatch's `entryId = Guid.Empty` on a non-completed continuation (empty `Handoff.Data` — no `data:` blob was written), which is spec-mandated (REQ-71-09), not a stub.

## Threat Flags
None. The plan's threat register is satisfied: T-71-09 (mitigate) — the REINJECT drop log references `EntryId` only (grep-verified 0 `Log.*Data`/`Log.*Payload` in all 3 consumers), and INJECT logs nothing about the relocated blob; T-71-10 (accept) — the envelope `MessageId` override mirrors the shipped Phase-70 keeper override; T-71-11 (accept) — the `data:` key derives from the orchestrator-assigned `MessageId`, gate-open-only partitioned endpoint; T-71-12 (mitigate) — the INJECT relocate body is INLINE, no Keeper->Orchestrator ProjectReference (`KeeperDependencyFirewallTests` green). No new security surface beyond the planned 3 keeper-recovery consumer bindings.

## Next Phase Readiness
- The orchestrator two-consumer recovery loop is now closed end-to-end: Plan 02's `OrchestratorPrePipeline`/`RelocateTail` escalate REINJECT (out: read fault) / INJECT (data: write exhaust) / DELETE (delete exhaust) to `keeper-recovery`, and these 3 consumers handle them symmetric with the Phase-70 processor recovery. The BIT health gate governs orchestrator recovery via the same partitioned endpoint pause/resume.
- All 6 recovery consumers (3 processor + 3 orchestrator) share one partitioned gate-open-only endpoint with no bus retry / no `_error`; one exec's ops serialize into one slot.
- Note for any follow-up: use the MTP-native `-- --filter-class` form for scoped test runs.

## Self-Check: PASSED

All 6 created files exist on disk; all 3 task commits (`9533aed`, `382d62d`, `9b12f53`) exist in git history. Keeper builds 0-warn/0-err (Release); the Keeper recovery+firewall+host-boot+partition slice is 21/21 green; the cross-assembly firewall holds (no Orchestrator ProjectReference; firewall test green).

---
*Phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi*
*Completed: 2026-06-17*
