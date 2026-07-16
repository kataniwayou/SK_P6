---
phase: 77-consistent-framework-logging-model-scope-carried-execution-i
plan: 04
subsystem: infra
tags: [logging, observability, processor, sample-processor, opentelemetry, three-tier-logging]

# Dependency graph
requires:
  - phase: 77 (plans 01-02)
    provides: framework per-hop record + result-send record now carry the full execution evidence, so concrete-author logs are redundant; bus-wide execution-scope filter attaches Tier-1 attributes.* to any log
provides:
  - "SampleProcessor emits ZERO author logs and takes no ILogger dependency (D4/LOG-04)"
  - "SampleProcessorFacts proves Mode-2/Mode-1 behaviour purely via send.SentData / dr.Data (no log assertions)"
  - "D5/LOG-05 operator-freedom guard: a bare placeholder-free log still carries all five Tier-1 ids via the unconditional bus-wide filter"
affects: [phase-78-sweep-rework, analyzer, value-oracle, PassFailEngine]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Concrete processors emit no author logs — the verdict depends only on framework-owned records; behaviour is proven via captured sends, never via log assertions"
    - "Operator freedom (D5): the bus-wide InboundExecutionScopeConsumeFilter stays registered unconditionally, so any operator string (placeholder-free included) receives Tier-1 attributes.*"

key-files:
  created:
    - .planning/phases/77-consistent-framework-logging-model-scope-carried-execution-i/77-04-SUMMARY.md
  modified:
    - src/Processor.Sample/SampleProcessor.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    - tests/BaseApi.Tests/Processor/FanInHermeticHarnessFacts.cs
    - tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs

key-decisions:
  - "Deleted both author logs (Mode-2 seed line + Mode-1 value line) and dropped the ILogger<SampleProcessor> primary-ctor param so the 0-warning gate holds; the framework per-hop + result-send records carry the execution evidence"
  - "Placed the D5 operator-freedom guard in ConsoleExecutionScopeFilterTests (reusing its MassTransit harness) rather than duplicating that harness inside SampleProcessorFacts — the plan sanctioned either location"
  - "D5 invariant left byte-for-byte unchanged: the bus-wide execution-scope filter registration in MessagingServiceCollectionExtensions was verified, not modified"

patterns-established:
  - "The framework verdict never depends on a concrete-processor log — authors contribute zero verdict-relied-upon logs (D4)"

requirements-completed: [LOG-04, LOG-05]

# Metrics
duration: 13min
completed: 2026-07-16
---

# Phase 77 Plan 04: SampleProcessor author-log removal + D5 operator-freedom guard Summary

**Deleted every concrete-processor author log (the Mode-2 seed line and Mode-1 value line) and the now-unused `ILogger<SampleProcessor>` dependency from `Processor.Sample`, re-proving the two-mode behaviour purely through `send.SentData`/`dr.Data`, and added a runtime D5 guard proving the bus-wide execution-scope filter still attaches all five Tier-1 ids to a placeholder-free operator log.**

## Performance

- **Duration:** ~13 min
- **Started:** 2026-07-16T17:03:13Z
- **Completed:** 2026-07-16T17:16:57Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- `SampleProcessor` now emits NO author logs and takes no `ILogger` — the class declaration dropped its primary-ctor logger param and the `Microsoft.Extensions.Logging` using; Debug **and** Release build 0-warning (D4/LOG-04, full SMP-01).
- `SampleProcessorFacts` re-proves Mode-2 (two distinct-execId spawns seeding 100/200 + entry delete) and Mode-1 (one reused-exec Completed producing 7+3=10) entirely via `send.SentData` / `dr.Data`; the `CapturingLogger` and all three `Assert.Single(logger.Entries)` / two `Assert.Contains(logger.Entries, …)` author-log assertions are gone.
- Added `OperatorFreedom_ExecutionScopeFilter_Registered_Unconditionally` to `ConsoleExecutionScopeFilterTests` — a bare placeholder-free consumer log still captures all five Tier-1 ids from the ambient scope the unconditional bus-wide filter opened (D5/LOG-05).
- The D5 invariant (`c.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx)`) was verified unchanged in `MessagingServiceCollectionExtensions` — the scope filter stays bus-wide and unconditional (grep == 1).

## Task Commits

Each task was committed atomically:

1. **Task 1: Delete the two author logs and remove the logger dependency** - `aece668` (refactor)
2. **Task 2: Behavior-only SampleProcessorFacts + D5 operator-freedom guard** - `fcd3bc5` (test)

## Files Created/Modified
- `src/Processor.Sample/SampleProcessor.cs` - removed the Mode-2 seed log and Mode-1 value log, dropped the `ILogger<SampleProcessor>` primary-ctor param + MEL using, updated the class XML doc to note D4/LOG-04 (concrete processor emits no author logs).
- `tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs` - deleted `CapturingLogger`, the logger construction, and all author-log assertions; constructs `new SampleProcessor()`; behaviour stays proven via `send.SentData` / `dr.Data`.
- `tests/BaseApi.Tests/Processor/FanInHermeticHarnessFacts.cs` - updated its two `new SampleProcessor(NullLogger…)` constructions to the parameterless ctor (Rule 3 blocking build fix; file not listed in the plan but referenced the removed ctor).
- `tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs` - added the D5 operator-freedom guard fact reusing the existing MassTransit harness + `ExecProbeConsumer` (its `"exec-probe consumed"` literal is placeholder-free).

## Decisions Made
- **D5 guard placed in `ConsoleExecutionScopeFilterTests`** — the plan sanctioned either location and preferred the one that avoids harness duplication; that file already owns the MassTransit test harness + scope-capturing logger, and its `ExecProbeConsumer` logs a bare placeholder-free string, so the guard reuses the harness with zero duplication.
- **Scope filter verified, not modified** — the D5/LOG-05 invariant is preserved exactly; the only D5 change is a new *test*, never a production edit.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] FanInHermeticHarnessFacts referenced the removed 1-arg SampleProcessor ctor**
- **Found during:** Task 1 (Debug build after removing the logger param)
- **Issue:** `FanInHermeticHarnessFacts.cs` (not listed in the plan's `files_modified`) constructed `new SampleProcessor(NullLogger<SampleProcessor>.Instance)` at two call sites, so the whole-solution build failed CS1729 once the primary-ctor param was dropped.
- **Fix:** Replaced both with `new SampleProcessor()`. The `NullLogger` import stays (still used for `NullLogger<ProcessorPipeline>` at line 62), so no unused-using warning.
- **Files modified:** tests/BaseApi.Tests/Processor/FanInHermeticHarnessFacts.cs
- **Verification:** `*FanInHermeticHarnessFacts*` 5/5 GREEN; Debug + Release build 0-warning.
- **Committed in:** `fcd3bc5` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** The single deviation was required for the solution to compile after the ctor change — a direct downstream consequence of this plan's own Task 1 removal. No scope creep (confined to updating one test call site to the new ctor).

## TDD Gate Compliance
Plan `type: execute`; Task 2 carries `tdd="true"` in the refactor/removal sense. Task 1's production removal intentionally breaks the pre-existing facts that assert the OLD log shape / construct with a logger (RED — the solution build fails CS1729 and the log-assertions would fail), and Task 2 re-keys the facts to behaviour-only + adds the D5 guard (GREEN). Commit sequence: `refactor` (Task 1) → `test` (Task 2). Because the removal deletes behaviour rather than adding it, a strict test-first RED commit is not meaningful; the RED signal is the compile break Task 1 produces against the old tests, resolved GREEN in Task 2.

## Issues Encountered
- The `--filter-method "*SampleProcessor*"` run reported 2 failures out of 6 matched — but both were the RealStack E2E tests `SampleRoundTripE2ETests.LiveSampleProcessor_*` and `SC1RoundTripE2ETests.LiveSampleProcessor_*` (matched by the `LiveSampleProcessor_*` method name), failing with 500 / connection-refused because no live Postgres/RabbitMQ stack runs in this Docker-less sandbox. Re-running with `--filter-not-trait Category=RealStack` yields 4/4 hermetic `SampleProcessorFacts` GREEN. The full hermetic subset run (852 tests) shows 282 failures, ALL of which are live-fixture `InitializeAsync` connection errors (PostgresFixture / WebAppFactory / Redis / RabbitMQ) — the documented Docker-less-sandbox baseline (phases 68/73/74/75 precedent), none referencing this plan's touched files. Zero NEW failures introduced.

## Threat Flags
None — no new network endpoints, auth paths, file access, or schema surface introduced. The plan's threat register is satisfied: T-77-10 (Tampering) mitigated — the author now writes zero logs the verdict can depend on (grep-asserted absence of both log strings); T-77-11 (Information disclosure) mitigated — the deleted lines logged only synthetic proof integers, so removal strictly reduces the log surface; T-77-12 (Repudiation / operator freedom) accepted — the bus-wide scope filter stays unconditional (verified, not modified) and the new D5 guard proves a placeholder-free operator log still carries Tier-1 attributes.

## Known Stubs
None.

## Next Phase Readiness
- The concrete processor is now author-log-free; the platform-level "did this step run" evidence is fully framework-owned. Per the plan's PHASE-78 note, the live value-oracle already degrades to N/A when these logs are absent, and Phase 78 drops the oracle entirely.
- Remaining Phase-77 plans (77-05, 77-06) continue the framework-log consistency work on the other components.

## Self-Check: PASSED

---
*Phase: 77-consistent-framework-logging-model-scope-carried-execution-i*
*Completed: 2026-07-16*
