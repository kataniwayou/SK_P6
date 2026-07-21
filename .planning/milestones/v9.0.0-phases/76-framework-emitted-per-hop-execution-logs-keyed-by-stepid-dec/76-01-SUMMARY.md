---
phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
plan: 01
subsystem: observability
tags: [processor-pipeline, otel, structured-logging, per-hop, stepid, outputtail, mel, otlp]

# Dependency graph
requires:
  - phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
    provides: "OutputTail as the single terminal-outcome tail (write→INJECT→send Step*), the output-schema Completed→Failed downgrade at :56-59"
  - phase: 70-two-consumer-processor-design
    provides: "ProcessorPipeline linear Pre flow with the 5 terminal branches + guarded keeper sends"
provides:
  - "ProcessorPipeline emits one Information per-hop execution record per executed hop (Completed/Failed/Cancelled), keyed by the six ids + resolved outcome, on the 5 emit branches only"
  - "OutputTail.RunAsync returns (bool proceed, StepOutcome resolved) so the pipeline logs the TRUE terminal outcome (D-18 output-schema downgrade honored)"
  - "Mode-2 entry marker logged with an explicit all-zeros ExecutionId (D-09)"
  - "FW-03 ids-only discipline (no payload literal/sentinel in any framework record) + FW-04 non-blocking guarded path (throwing ILogger absorbed; logs OTLP export declared Batch)"
  - "Three hermetic fact classes: PerHopLogFacts, FrameworkNoPayloadFacts, NonBlockingLogFacts"
affects: [phase-76-plan-02-orchestrator-fan-out-records, phase-76-plan-03-analyzer-stepid-rekey, phase-76-plan-04-verdict-class]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Guarded per-hop framework log: placeholder-only {Placeholder} template wrapped in try/catch swallow (FW-04)"
    - "Async tuple return to surface a resolved value the caller cannot recompute (D-18, cannot out in async)"
    - "Structural source-scan hermetic guard: regex-extract logger.Log* statements + assert no payload token (FW-03)"

key-files:
  created:
    - tests/BaseApi.Tests/Processor/PerHopLogFacts.cs
    - tests/BaseApi.Tests/Observability/FrameworkNoPayloadFacts.cs
    - tests/BaseApi.Tests/Processor/NonBlockingLogFacts.cs
  modified:
    - src/BaseProcessor.Core/Processing/OutputTail.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/BaseProcessor.Core/Processing/PostProcessConsumer.cs
    - src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs
    - tests/BaseApi.Tests/Processor/OutputTailFacts.cs

key-decisions:
  - "OutputTail.RunAsync returns Task<(bool proceed, StepOutcome resolved)> (tuple, D-18) — cannot out in async; the resolved field carries the post-output-schema-downgrade outcome"
  - "Per-hop record via a private LogHopExecuted(d, messageId, outcome) helper: placeholder-only, guarded try/catch swallow, d.ExecutionId passed EXPLICITLY (D-09 empty-Guid marker)"
  - "Emit at exactly 5 branches (input-fail, seam-status non-Processing, unexpected, Mode-2 null, normal-completion after !proceed guard); ZERO on clean-absent/reinject/Processing"
  - "Declared logs OTLP ExportProcessorType.Batch explicitly (was framework-default) so FW-04 batch/drop posture is structurally assertable"

patterns-established:
  - "Framework-emitted operability record: 'did this step execute' is platform-level for ANY processor with zero author logs"
  - "HopRecords filter = Information entries whose state carries the Outcome key — distinguishes the framework record from author/status logs"

requirements-completed: [FW-01, FW-03, FW-04]

# Metrics
duration: 25min
completed: 2026-07-16
---

# Phase 76 Plan 01: Framework-Emitted Per-Hop Execution Logs (Processor Side) Summary

**ProcessorPipeline now emits one guarded, ids-only Information record per executed hop keyed by stepId + resolved outcome, with OutputTail returning the true terminal outcome (D-18) and a non-blocking swallow-guard + explicit batch export (FW-04).**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-07-16T11:40:00Z (approx)
- **Completed:** 2026-07-16T12:05:00Z
- **Tasks:** 3
- **Files modified:** 9 (4 production, 5 test — 3 created, 2 modified)

## Accomplishments
- `OutputTail.RunAsync` returns `(bool proceed, StepOutcome resolved)` — the output-schema `Completed→Failed` downgrade the pipeline could not previously see is now observable (D-18); all 4 pipeline callers + PostProcessConsumer updated.
- `ProcessorPipeline.LogHopExecuted` emits exactly one Information per-hop record at the 5 executed-hop branches (input-schema fail → Failed, seam status → Failed/Cancelled skipping Processing, unexpected → Failed, Mode-2 null → Completed entry marker, normal completion → D-18 resolved outcome), carrying the six ids + outcome as placeholder args only.
- Non-execution branches (clean-absent, gate-fault reinject, Processing) emit ZERO per-hop records — proven hermetically.
- FW-03 (no-payload) enforced by a grep-clean source scan of both framework log-emission files + a sentinel-through-blob absence fact; FW-04 (non-blocking) proven by a ThrowingLogger leaving the hop outcome unchanged + an explicit `ExportProcessorType.Batch` on the logs OTLP exporter with a structural assertion.

## Task Commits

1. **Task 1: OutputTail.RunAsync returns resolved outcome (D-18)** - `7ad4086` (refactor)
2. **Task 2: emit guarded per-hop record at the 5 branches** - `08c968f` (test, RED) → `49caf17` (feat, GREEN)
3. **Task 3: FW-03 no-payload + FW-04 non-blocking facts** - `b93d5e1` (feat)

**Plan metadata:** (this commit)

## Files Created/Modified
- `src/BaseProcessor.Core/Processing/OutputTail.cs` - `RunAsync` returns the resolved `StepOutcome` tuple field (D-18); both return sites yield `result`.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` - `LogHopExecuted` guarded helper + calls at the 5 emit branches; normal path consumes the D-18 resolved outcome after the `!proceed` guard.
- `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs` - deconstruct-and-discard the new tuple (Post logs no per-hop record).
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` - logs OTLP exporter declares `ExportProcessorType.Batch` explicitly (FW-04 posture, structurally assertable).
- `tests/BaseApi.Tests/Processor/OutputTailFacts.cs` - deconstruct tuple at 5 call sites + assert `resolved`; new D-18 output-schema-downgrade fact.
- `tests/BaseApi.Tests/Processor/PerHopLogFacts.cs` (created) - 11 facts: six-field record on each emit branch, no-author-log still-emits, Mode-2 empty-ExecutionId marker, D-18 downgrade, zero-record on clean-absent/reinject/Processing.
- `tests/BaseApi.Tests/Observability/FrameworkNoPayloadFacts.cs` (created) - grep-clean source scan + sentinel-through-blob absence (FW-03).
- `tests/BaseApi.Tests/Processor/NonBlockingLogFacts.cs` (created) - ThrowingLogger outcome-parity + export-shape batch/drop (FW-04).

## Decisions Made
- **Tuple over `out` for D-18:** `RunAsync` is async so `out` is impossible; `(bool proceed, StepOutcome resolved)` cleanly carries the post-downgrade outcome. Even the INJECT-escalation `return (false, result)` reports the resolved outcome for symmetry.
- **`HopRecords` filter keys on the `Outcome` state key** so the framework per-hop record is distinguishable from the pre-existing `ProcessAsync threw processing status: {Msg}` Information log — avoids a false "zero records" assertion collision on the Processing branch.
- **Seam-status branch emits only when `statusDr.Result != Processing`** — the transient Processing status is not a terminal executed hop (D-05), consistent with the OutputTail write gate.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Declared logs OTLP `ExportProcessorType.Batch` explicitly**
- **Found during:** Task 3 (FW-04 export-shape fact)
- **Issue:** The logs OTLP exporter used `o.AddOtlpExporter()` with the framework default, so the FW-04 batch/drop backpressure guarantee (T-76-02) was implicit and not structurally assertable. The plan's Task 3 action explicitly authorizes: "if it does not [declare Batch], add the explicit `ExportProcessorType.Batch` there and note it in the SUMMARY."
- **Fix:** Added `o.AddOtlpExporter(exp => exp.ExportProcessorType = ExportProcessorType.Batch);` + `using OpenTelemetry;`. The per-hop record now fires on every executed hop at real volume; batch/drop ensures a stalled/throwing exporter can never apply backpressure into the consume path (belt-and-braces with the pipeline swallow-guard).
- **Files modified:** src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs
- **Verification:** `NonBlockingLogFacts.ExportShape_LogsOtlp_IsBatchNotSimple` asserts the wiring contains `ExportProcessorType.Batch` and no `ExportProcessorType.Simple`; Debug + Release build 0-warning.
- **Committed in:** `b93d5e1` (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (1 missing-critical, explicitly plan-authorized)
**Impact on plan:** The explicit Batch declaration is the plan-sanctioned resolution of the FW-04 export-shape acceptance criterion. No scope creep. Task 1's OutputTailFacts call-site updates (tuple deconstruction) are a required consequence of the D-18 signature change, not a separate deviation.

## TDD Gate Compliance
- **Task 1** (signature refactor): executed as a `refactor` — a pure return-shape change whose behavior is verified by the existing OutputTail/PrePipeline suite (unchanged proceed semantics) plus a new D-18 downgrade fact in the same commit. No independent RED gate (nothing to fail before the signature exists).
- **Task 2** (per-hop emission): full RED → GREEN gate — `08c968f` (test, 7 emit-branch facts failing) → `49caf17` (feat, all 11 green). No refactor commit needed.
- **Task 3** (FW-03/FW-04 facts): the FW-04 swallow-guard was implemented in Task 2's `LogHopExecuted`; Task 3's `NonBlockingLogFacts` verify it (guard-then-verify, not RED-first). The FW-03 grep + sentinel facts and the export-shape fact are new guards committed with their production support (`b93d5e1`).

## Issues Encountered
- Test project targets **net8.0**, not the `net9.0` path the plan's `<verify>` blocks reference — the actual binary is `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe` (stale path in the plan; used the correct one).
- Full hermetic suite (`--filter-not-trait Category=RealStack`) shows **272 failures — identical to the documented baseline** (phases 68/73/74/75), all pre-existing infra-dependent E2E/Integration tests (Postgres/ES/RabbitMQ absent in the Docker-less sandbox: HealthEndpoints/MetricsExport/LogExport/Schemas E2E). Zero new failures in the changed scope; all processor/observability logic facts green (PerHop 11/11, NoPayload 2/2, NonBlocking 2/2, OutputTail 6/6, PrePipeline 28/28, PostProcess 7/7).

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The framework per-hop record (Information, `attributes.StepId/ExecutionId/CorrelationId/EntryId/MessageId/Outcome`) is the evidence base the re-keyed analyzer (plans 03/04) binds to.
- Plan 02 (orchestrator fan-out + terminal-reached records) mirrors this pattern on `OrchestratorPrePipeline`; the `FrameworkNoPayloadFacts` grep guard already scans that file and will guard the plan-02 records.
- **Live surfacing** of the per-hop records in ES at real volume (checkpoint:human-verify) is not covered here — hermetic facts prove emission shape only; a Docker-up sweep would confirm ES `attributes.*` binding (precedent: deferred-automated in the Docker-less sandbox).

## Self-Check: PASSED

All created files exist on disk; all task commits (`7ad4086`, `08c968f`, `49caf17`, `b93d5e1`) present in git history.

---
*Phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec*
*Completed: 2026-07-16*
