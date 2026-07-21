---
phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
plan: 02
subsystem: testing
tags: [observability, structured-logging, mel, orchestrator, fan-out, telemetry, fw-04]

# Dependency graph
requires:
  - phase: 76-01
    provides: "FrameworkNoPayloadFacts grep guard scans OrchestratorPrePipeline.cs; ThrowingLogger<ProcessorPipeline> FW-04 pattern to mirror; OutputTail resolved-outcome shape"
provides:
  - "Orchestrator fan-out edge record (CorrelationId, ExecutionId, WorkflowId, inbound EntryId, next StepId) — D-11 Option C, no outbound MessageId — one per next step"
  - "Orchestrator terminal-reached record (ids only, no MessageId) fired x2 at Step_G double fan-in by distinct inbound EntryId (D-12/D-13)"
  - "attributes.NextStepId ES surface for the analyzer's ANL-02 expected set + ANL-03 M_N redundancy proof"
  - "FW-04 non-blocking guarantee on both new orchestrator records + the pre-existing terminal ack, proven by ThrowingLogger<OrchestratorPrePipeline> facts"
affects: [76-03, 76-04, 76-05, analyzer-re-key, ANL-02, ANL-03]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Guarded placeholder-only per-record log: try { logger.LogInformation(\"template {Placeholder}\", ids...); } catch { } — ids only, no payload (FW-03), throw absorbed (FW-04)"
    - "Structured-State CapturingLogger<T> capturing IReadOnlyList<KeyValuePair<string,object?>> State plus a Messages back-compat projection"
    - "Record filter helpers keyed by placeholder-key presence (EdgeRecords / TerminalReachedRecords) to disambiguate co-located log shapes"

key-files:
  created:
    - "tests/BaseApi.Tests/Orchestrator/OrchestratorNonBlockingLogFacts.cs"
  modified:
    - "src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs"
    - "tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs"

key-decisions:
  - "D-11 Option C honored: the fan-out edge record omits the outbound MessageId (not in hand at the Pre loop) — Phase-72 'no override' Send is left intact, no NewId minted"
  - "The pre-existing 'Trip ended (completed-terminal)' log was wrapped in try/catch alongside the new terminal-reached record so the terminal ack survives a throwing logger (required by FW-04/T-76-05 + Task 3's terminal fact)"
  - "FW-02 record filters key on placeholder presence (NextStepId+CorrelationId for edges; CorrelationId+EntryId+StepId and no-NextStepId for terminal) to exclude the dangling-id log and the legacy trip-end line"

patterns-established:
  - "Pattern 1: two independent try/catch guards on the terminal branch so a throw from either log call is absorbed independently, never surfacing out of RunAsync"
  - "Pattern 2: self-contained hermetic harness in the FW-04 fact file (mirrors OrchestratorPrePipelineFacts) so the throwing-logger facts do not couple to the sibling fixture's private doubles"

requirements-completed: [FW-02, FW-04]

# Metrics
duration: 5min
completed: 2026-07-16
---

# Phase 76 Plan 02: Orchestrator Causal-Edge Telemetry Summary

**Guarded orchestrator fan-out edge records (inbound EntryId → next StepId, D-11 Option C — no outbound MessageId) and terminal-reached records (x2 at Step_G by distinct inbound EntryId), both non-blocking (FW-04) and ids-only (FW-03), giving the analyzer its ANL-02 expected set and ANL-03 redundancy edge.**

## Performance

- **Duration:** ~5 min
- **Started:** 2026-07-16T09:33:02Z
- **Completed:** 2026-07-16T09:38:15Z
- **Tasks:** 3
- **Files modified:** 3 (1 created, 2 modified)

## Accomplishments
- Upgraded `OrchestratorPrePipelineFacts`' `CapturingLogger<T>` to structured-State capture (retaining the `Messages` projection so all existing trip-end facts still assert) and added `EdgeRecords`/`TerminalReachedRecords`/`StateValue` filter helpers.
- Emitted the guarded fan-out edge record (one per next step, after a successful send) and the guarded terminal-reached record (x2 at Step_G) in `OrchestratorPrePipeline`, on the correct branches only (NOT L1-miss, NOT clean-absent).
- Proved FW-04 for both the fan-out send loop and the terminal ack with a new `ThrowingLogger<OrchestratorPrePipeline>` fact class.

## Task Commits

Each task was committed atomically (TDD: RED test → GREEN feat):

1. **Task 1: structured-State capture + RED FW-02 facts** - `706582d` (test)
2. **Task 2: emit guarded fan-out edge + terminal-reached records** - `07cdd00` (feat)
3. **Task 3: FW-04 ThrowingLogger<OrchestratorPrePipeline> facts** - `8412a8c` (test)

## Files Created/Modified
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` - Added the guarded fan-out edge record in the :126-145 loop (after `metrics.MessagesSent.Add`) and the guarded terminal-reached record in the true-terminal branch; wrapped the pre-existing terminal log in try/catch for FW-04 ack survival.
- `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` - Structured-State `CapturingLogger<T>`, filter helpers, and 4 new facts (fan-out x2 distinct, terminal x2 by EntryId, + 2 negative-branch guards).
- `tests/BaseApi.Tests/Orchestrator/OrchestratorNonBlockingLogFacts.cs` - New FW-04 fact class with a self-contained harness and `ThrowingLogger<OrchestratorPrePipeline>`.

## Decisions Made
- **Option C confirmed in code:** the fan-out edge record carries five fields and omits the outbound MessageId; no `NewId`/`ctx.MessageId` override was added (verified by grep). Phase-72's "no override" Send is untouched.
- **Terminal filter disambiguation:** used placeholder-key presence rather than message text so the filters are robust to wording.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Guarded the pre-existing "Trip ended (completed-terminal)" log**
- **Found during:** Task 2/Task 3 (terminal-reached emission + FW-04 terminal fact)
- **Issue:** The plan's Task 2 wording guards only the NEW terminal-reached record, but the pre-existing "Trip ended (completed-terminal)" `LogInformation` sits first on the same terminal ack path. Under Task 3's `ThrowingLogger<OrchestratorPrePipeline>` that unguarded call would throw and propagate out of `RunAsync`, failing the terminal ack — making the terminal FW-04 fact impossible to pass and violating threat T-76-05 (a throwing exporter must not fail the ack).
- **Fix:** Wrapped the pre-existing terminal log in its own `try/catch` alongside the new record (two independent guards). Under a normal logger both still emit (existing `Terminal_step_logs_completed_terminal_and_acks` still asserts "completed-terminal"); under a throwing logger both are absorbed and the ack survives.
- **Files modified:** src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
- **Verification:** `OrchestratorPrePipeline_NonBlocking_Terminal_ack_survives_a_throwing_logger` green; all 19 `OrchestratorPrePipelineFacts` still green.
- **Committed in:** `07cdd00` (Task 2 commit)

**2. [Rule 3 - Blocking] Added `using System.Diagnostics.Metrics;` to the new fact file**
- **Found during:** Task 3
- **Issue:** `IMeterFactory` (used by the self-contained `NewMetrics()` harness) was unresolved → CS0246 build error.
- **Fix:** Added the missing using directive.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/OrchestratorNonBlockingLogFacts.cs
- **Verification:** Test project builds 0-warning; both FW-04 facts green.
- **Committed in:** `8412a8c` (Task 3 commit)

---

**Total deviations:** 2 auto-fixed (1 missing-critical, 1 blocking)
**Impact on plan:** Deviation 1 was required for the FW-04 terminal correctness guarantee (T-76-05) and Task 3's acceptance; deviation 2 was a trivial build fix. No scope creep.

## Issues Encountered
- The documented `BaseApi.Tests.exe -- --filter-method` form dumped the help text (the leading `--` separator). Running the exe directly with `--filter-method`/`--filter-class` (no separator) works; middle wildcards are unsupported (begin/end only), so `*Framework*NoPayload*` was replaced with `--filter-class "*FrameworkNoPayload*"`.
- The plan's verify commands reference `bin/Debug/net9.0/...`; the test project actually targets `net8.0` (per prior-wave context). Used the `net8.0` host throughout.

## Verification
- `dotnet build SK_P.sln -c Debug` and `-c Release`: both 0-warning, 0-error.
- `BaseApi.Tests.exe --filter-method "*OrchestratorPrePipeline*"`: 21/21 green (19 fixture facts + 2 FW-04 facts).
- Full hermetic Orchestrator namespace: 113/113 green.
- FW-03 no-payload grep guard (`*FrameworkNoPayload*`): 2/2 green; source grep shows no `NewId`/`MessageId` override and no payload token in any log template.

## Next Phase Readiness
- `attributes.NextStepId` (fan-out) and the terminal-reached record are now emitted, ready for the analyzer re-key (ANL-01/ANL-02/ANL-03) in the downstream 76 plans.
- Live-stack surfacing of these records into ES is a phase-gate concern (SourceHash reseed is NOT triggered by this plan — the edit is in the `Orchestrator` project, not `BaseProcessor.Core`).

---
*Phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec*
*Completed: 2026-07-16*

## Self-Check: PASSED

All created/modified files exist on disk; all three task commits (706582d, 07cdd00, 8412a8c) present in git history.
