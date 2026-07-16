---
phase: 77-consistent-framework-logging-model-scope-carried-execution-i
plan: 05
subsystem: infra
tags: [logging, masstransit, correlation, messaging-contracts, execution-scope, keeper, partitioner]

# Dependency graph
requires:
  - phase: 77-consistent-framework-logging-model-scope-carried-execution-i
    provides: "ExecutionLogScope.BuildState 5-key skip rules; IExecutionCorrelated scope filter; IKeeperRecoverable partition 4-tuple marker (D-12)"
provides:
  - "ExecutionLogScope.BuildState(workflowId, stepId, processorId, executionId, entryId) loose-id overload — the shape keeper recovery consumers carry"
  - "IExecutionCorrelated overload now delegates to the loose-id form (single skip-rule implementation)"
  - "IKeeperRecoverable : ICorrelated — keeper records recognized by the bus-wide correlation filter (body->scope on consume) and outbound stamp (ambient->envelope on send)"
  - "Partition 4-tuple (corr:wf:proc:exec) preserved via `new` re-declaration of CorrelationId"
affects: [77-06, phase-78-analyzer-sweep, keeper-recovery, correlation-filter]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Loose-id overload + delegation: one skip-rule implementation shared by the interface form and the positional form"
    - "Interface `new`-redeclaration to extend a base interface while preserving MassTransit UsePartitioner GetProperties() member-set (D-12)"

key-files:
  created:
    - tests/BaseApi.Tests/Console/ExecutionLogScopeKeeperFacts.cs
  modified:
    - src/Messaging.Contracts/ExecutionLogScope.cs
    - src/Messaging.Contracts/IKeeperRecoverable.cs

key-decisions:
  - "BuildState loose-id overload owns the single skip-rule implementation; the IExecutionCorrelated overload delegates to it (never a duplicate skip block, D-07)."
  - "IKeeperRecoverable extends ICorrelated with CorrelationId re-declared `new` — extending fixes correlation body-source/envelope-stamp for keeper sends while GetProperties() still surfaces exactly the 4-tuple (D-12 partition preserved)."

patterns-established:
  - "Delegation parity: an interface-typed builder overload forwards to a positional overload so callers without the interface get byte-identical output."

requirements-completed: [LOG-02]

# Metrics
duration: 5min
completed: 2026-07-16
---

# Phase 77 Plan 05: D2 Keeper Scope Foundation Summary

**Loose-id `ExecutionLogScope.BuildState` overload (delegated from the `IExecutionCorrelated` form) plus `IKeeperRecoverable : ICorrelated` — giving the keeper recovery consumers a buildable execution scope and a correct body-sourced CorrelationId, with the partition 4-tuple byte-for-byte preserved.**

## Performance

- **Duration:** 5 min
- **Started:** 2026-07-16T17:19:50Z
- **Completed:** 2026-07-16T17:24:06Z
- **Tasks:** 3
- **Files modified:** 3 (2 source, 1 test)

## Accomplishments
- Added `BuildState(Guid workflowId, Guid stepId, Guid processorId, Guid executionId, Guid entryId)` with byte-identical skip rules (each `Guid.Empty` skipped; EntryId skipped via `SourceStep.IsSource`); the `IExecutionCorrelated` overload now delegates to it (single skip-rule implementation).
- Made `IKeeperRecoverable` extend `ICorrelated`, re-declaring `CorrelationId` with `new` so keeper records are recognized by the bus-wide correlation filter/outbound stamp (LOG-02 correlation correctness) while the `GetProperties()` 4-tuple (corr:wf:proc:exec, D-12) is preserved. No keeper record file needed editing — their existing `public Guid CorrelationId { get; init; }` satisfies both interfaces.
- Added `ExecutionLogScopeKeeperFacts` locking parity, skip rules, the six keeper records' `ICorrelated` contract, and the partition 4-tuple shape (11 facts).

## Task Commits

Each task was committed atomically:

1. **Task 1: BuildState positional overload; delegate the existing overload** - `624504b` (feat)
2. **Task 2: IKeeperRecoverable : ICorrelated (partition-preserving)** - `4c538f2` (feat)
3. **Task 3: Hermetic fact — overload parity/skip rules + keeper ICorrelated + partition-shape guard** - `25b384e` (test)

_Note: Tasks 1 & 2 are source contract changes verified against Task 3's fact; the plan structured the locking fact as its own task (test task follows the source it pins)._

## Files Created/Modified
- `src/Messaging.Contracts/ExecutionLogScope.cs` - Added the loose-id `BuildState` positional overload; the `IExecutionCorrelated` overload delegates to it.
- `src/Messaging.Contracts/IKeeperRecoverable.cs` - Now `: ICorrelated`, `CorrelationId` re-declared `new` to preserve the partition 4-tuple.
- `tests/BaseApi.Tests/Console/ExecutionLogScopeKeeperFacts.cs` - 11 facts: skip-rule + delegation parity + keeper-`ICorrelated` theory (6 records) + partition-shape guard.

## Verification

- **Debug build:** 0 Warning(s), 0 Error(s).
- **Release build:** 0 Warning(s), 0 Error(s).
- **`*ExecutionLogScopeKeeper*`:** 11/11 passed (exit 0).
- **`*ConsoleExecutionScopeFilter*`:** 5/5 passed (exit 0) — delegation preserved the existing filter behavior.
- **`*RecoveryPartition*`:** 3/3 passed (exit 0) — partition key shape intact after `: ICorrelated`.
- **`*KeeperContract*` (ripple check):** 6/6 passed (exit 0) — the `IKeeperRecoverable_exposes_exactly_the_partition_four_tuple_and_not_StepId` contract still holds; the `new`-redeclaration keeps `GetProperties()` at exactly the 4-tuple with no `StepId`.

## Decisions Made
- Followed the plan's prescribed doc-comment wording verbatim (which mentions `Guid.Empty` in the XML docs), so the literal `rg -c 'Guid.Empty'` count on `ExecutionLogScope.cs` is 7 (4 code checks in the single positional skip block + 3 doc-comment mentions) rather than the AC's literal 4. The substantive AC intent — a single skip block of exactly 4 `!= Guid.Empty` code checks with no duplicated skip logic — is fully satisfied (the delegating overload contains zero skip checks).

## Deviations from Plan

None - plan executed exactly as written. (The one AC-literal nuance — the `Guid.Empty` grep counting doc-comment mentions — is documented under Decisions Made, not a code deviation; the code matches the plan's prescribed action byte-for-byte.)

## Issues Encountered
- The plan's verify commands use `BaseApi.Tests.exe -- --filter-method "*X*"`; the extra `--` routes the flag into MTP help output. Ran `BaseApi.Tests.exe --filter-method "*X*"` (single dash-prefix) instead, which correctly filters and reports pass/fail with exit codes. No impact on results.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- LOG-02 foundation is in place: Plan 06 (keeper scope-open) can now call `ExecutionLogScope.BuildState(m.WorkflowId, m.StepId, m.ProcessorId, m.ExecutionId, m.EntryId)` on the keeper recovery messages and rely on `ICorrelated` body-sourced correlation before stripping the Tier-1 `{CorrelationId}` placeholders from the reinject logs.
- No blockers. This plan changed only the shared `Messaging.Contracts` leaf (Wave 1) — no keeper consumer code changed yet (Plan 06).

## Self-Check: PASSED

All 3 files (2 source + 1 test) and 3 task commits (624504b, 4c538f2, 25b384e) verified present.

---
*Phase: 77-consistent-framework-logging-model-scope-carried-execution-i*
*Completed: 2026-07-16*
