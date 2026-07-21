---
phase: 70-two-consumer-processor-design-pre-process-post-process-with
plan: 01
subsystem: api
tags: [csharp, dotnet8, masstransit, redis, messaging-contracts, keeper-recovery]

# Dependency graph
requires:
  - phase: 70-SPEC
    provides: "12 reqs + pinned pseudocode + locked decisions D-01..D-17 (DataResult spine, OutputData key, reshaped keeper contracts)"
provides:
  - "DataResult record — unified seam-return + Post wire + KeeperInject body spine (Messaging.Contracts)"
  - "L2ProjectionKeys.OutputData(messageId) => skp:out:{messageId:D}; MessageIndex removed"
  - "KeeperInject embeds whole DataResult + DeleteEntryId (flattened EntryId/Data dropped)"
  - "KeeperDelete is entryId-only (MessageId field dropped)"
  - "KeeperReinject carries a Guid MessageId field (same-messageId re-inject)"
affects: [70-02, 70-03, 70-04, 70-05, keeper-recovery, processor-pipeline, two-consumer-design]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Unified DataResult spine: one self-contained record is simultaneously the ProcessAsync seam return, the Post-Process IConsumer<DataResult> wire contract, and the KeeperInject embedded body (D-01/D-02/D-13)"
    - "Distinct L2 namespaces: skp:out:{messageId:D} (output blobs by messageId) vs skp:data:{entryId:D} (input blobs by entryId)"

key-files:
  created:
    - src/Messaging.Contracts/DataResult.cs
  modified:
    - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
    - src/Messaging.Contracts/KeeperInject.cs
    - src/Messaging.Contracts/KeeperDelete.cs
    - src/Messaging.Contracts/KeeperReinject.cs

key-decisions:
  - "DataResult reuses the existing 4-value StepOutcome enum verbatim (D-04) rather than introducing a new result type"
  - "DataResult does NOT implement IKeeperRecoverable — it is a plain embeddable/wire record, not a keeper contract"
  - "KeeperInject embeds the whole DataResult (not flattened fields) so 'never recomputes from input' (req 7) falls out structurally — data is in-hand on the envelope"
  - "All three keeper contracts retain the positional (WorkflowId,StepId,ProcessorId):IKeeperRecoverable ctor + CorrelationId/ExecutionId init props to satisfy the 4-tuple partition key (ReinjectConsumerDefinition)"

patterns-established:
  - "Pattern 1: DataResult spine — a single self-contained record threads seam→Post→INJECT, so Post and INJECT need nothing from elsewhere"
  - "Pattern 2: out: vs data: L2 namespace split — output blobs keyed by messageId, input blobs keyed by entryId"

requirements-completed: [SPEC-req-6, SPEC-req-7, SPEC-req-8, SPEC-req-11]

# Metrics
duration: 8min
completed: 2026-06-16
---

# Phase 70 Plan 01: Contract Foundation Summary

**Unified DataResult spine record + skp:out:{messageId:D} OutputData key + three reshaped keeper contracts (Inject embeds DataResult, Delete entryId-only, Reinject gains MessageId) — Messaging.Contracts builds 0-warn/0-err.**

## Performance

- **Duration:** ~8 min
- **Started:** 2026-06-16T21:10:00Z (approx)
- **Completed:** 2026-06-16
- **Tasks:** 2
- **Files modified:** 5 (1 created, 4 modified)

## Accomplishments
- New `DataResult` sealed record: the single spine that is simultaneously the ProcessAsync seam return, the Post-Process `IConsumer<DataResult>` wire contract, and the body embedded in `KeeperInject` (D-01/D-02). Reuses the 4-value `StepOutcome` (D-04); carries `MessageId` as the L2 output key.
- `L2ProjectionKeys`: added `OutputData(messageId) => skp:out:{messageId:D}` (D-10); deleted the retired `MessageIndex` builder + its XML-doc item; `ExecutionData` left verbatim.
- `KeeperInject` reshaped (D-12/D-13): embeds the whole `DataResult` + `DeleteEntryId`; flattened `EntryId`/`Data` fields dropped.
- `KeeperDelete` reshaped (D-12): delete-only `EntryId`; `MessageId` field dropped (two-key/index model retired).
- `KeeperReinject` reshaped (req 6/A3): gained a `Guid MessageId` field for same-messageId re-inject; `Payload` retained.

## Task Commits

Each task was committed atomically:

1. **Task 1: Create DataResult + add OutputData / delete MessageIndex** - `e5b752c` (feat)
2. **Task 2: Reshape the three keeper contracts** - `e149d88` (feat)

**Plan metadata:** (final docs commit — this SUMMARY + STATE + ROADMAP)

## Files Created/Modified
- `src/Messaging.Contracts/DataResult.cs` - NEW: unified DataResult spine record (seam return / Post wire / INJECT body)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` - Added `OutputData`; removed `MessageIndex` builder + doc item; `ExecutionData` unchanged
- `src/Messaging.Contracts/KeeperInject.cs` - Embeds `DataResult` + `DeleteEntryId`; dropped flattened `EntryId`/`Data`
- `src/Messaging.Contracts/KeeperDelete.cs` - Delete-only `EntryId`; dropped `MessageId`
- `src/Messaging.Contracts/KeeperReinject.cs` - Added `Guid MessageId`; kept `Payload`

## Decisions Made
None beyond the plan-pinned decisions — followed the SPEC/CONTEXT pseudocode exactly (D-01/D-02/D-04/D-10/D-12/D-13). See `key-decisions` frontmatter for the locked decisions honoured.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None. Both tasks' acceptance-criteria greps passed and the `Messaging.Contracts` project built 0-warning/0-error in both Debug (Task 1 gate) and Release (Task 2 gate).

## Build Verification Note
By design (build-before-teardown, Pitfall 5), the FULL solution does NOT compile after 70-01 alone — Wave-2 consumers still reference the old keeper shapes (`KeeperInject.EntryId/Data`, `KeeperDelete.MessageId`, `L2ProjectionKeys.MessageIndex`). This is expected and the plan's acceptance criteria scope the build gate to `Messaging.Contracts`. `dotnet build src/Messaging.Contracts/Messaging.Contracts.csproj -c Release` → **Build succeeded, 0 Warning(s), 0 Error(s)**. Full-solution green is deferred to Wave 2/3 (compiler-enforced completeness).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Contract foundation is in place; every other Phase-70 plan (70-02..70-05) compiles against `DataResult`, `OutputData`, and the reshaped keeper contracts.
- Wave-2 consumers must be updated to the new shapes before the full solution builds (expected, by design).

## Self-Check: PASSED

- FOUND: src/Messaging.Contracts/DataResult.cs
- FOUND: commit e5b752c
- FOUND: commit e149d88

---
*Phase: 70-two-consumer-processor-design-pre-process-post-process-with*
*Completed: 2026-06-16*
