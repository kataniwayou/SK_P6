---
phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
plan: 05
subsystem: testing
tags: [analyzer, resilience-sweep, D-04, gap-closure, structural-cohort, effect-once, duplicates, canonical-record, hermetic-regression, pass-fail-engine]

# Dependency graph
requires:
  - phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
    plan: 04
    provides: "D-04 BLOCKING FINDING + high-confidence root cause: the reworked structural step query over-counts each hop 3-4x because Phase-77 emits multiple {stepId,executionId,messageId}-scoped framework records per hop (hop executed / result sent / fan-out), tripping Duplicates==StartedRuns on every run"
provides:
  - "D-04 code fix: a pure StructuralCohort classifier that selects ONLY the canonical 'hop executed' consume record as the observed did-run hop — result-sent / fan-out records no longer inflate the observed stepId list, collapsing the 3-4x over-count to 1-per-genuine-execution"
  - "FrameworkLogRecord seam: BuildRunTraces now parses ES hits (incl. _source.body.text) into a JsonElement-decoupled record type and delegates observed/proven/resolver classification to StructuralCohort — the shared seam both the live fixture AND the hermetic facts consume (the seam the 78-04 gap lacked)"
  - "Hermetic regression facts (StructuralCohortFacts): reproduce the live multi-record-per-hop shape (30 records / 10 stepIds) and pin the collapse->Pass, AND pin that a genuine redelivery (two 'hop executed' for one {exec,step}) STILL trips Duplicates->Fail — the D-04 class of bug is now provable WITHOUT a live stack"
  - "Genuine effect-once/duplicate detection preserved: the naive (executionId,stepId) de-dup was explicitly rejected; the collapse is by canonical-record-KIND selection, so genuine duplicates survive"
affects: [78, resilience-sweep, D-04, pass-fail-engine, structural-cohort, live-gate-78-06]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Canonical-record selection over naive de-dup: when uniform execution-scope logging makes multiple framework records share one {stepId,executionId,messageId}, isolate the ONE 1-per-genuine-execution record kind (Ordinal body.text prefix) rather than distinct-ing the list — collapse the over-count without blinding genuine-duplicate detection"
    - "Shared classifier seam: extract the live fixture's inline discrimination into a pure, namespace-shared classifier so the hermetic facts exercise the EXACT live path — the direct fix for the 78-04 'invisible hermetically' gap"

key-files:
  created:
    - "tests/BaseApi.Tests/Observability/Analysis/StructuralCohort.cs - pure FrameworkLogRecord type + canonical-record classifier (Classify) + StructuralCohortResult; the shared seam"
    - "tests/BaseApi.Tests/Observability/Analysis/StructuralCohortFacts.cs - 3 hermetic regression facts reproducing the multi-record-per-hop shape + pinning collapse->Pass and genuine-redelivery->Fail"
  modified:
    - "tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs - BuildRunTraces parses hits into FrameworkLogRecords (new ParseStructuralRecords helper reads _source.body.text) + delegates to StructuralCohort.Classify; D-01/D-02 query filters unchanged"

key-decisions:
  - "The canonical record is the processor 'hop executed' consume log (ProcessorPipeline.cs:218) — the ONE record that fires exactly once per genuine hop execution. Discriminated by Ordinal body.text StartsWith, not by attribute presence (hasMessageId no longer isolates it post-Phase-77)."
  - "The forbidden naive fix (de-dup by (executionId, stepId) before counting) was explicitly REJECTED — it collapses to presence-only and blinds genuine effect-once/duplicate detection. The collapse comes purely from record-KIND selection; genuine redelivery duplicates are RETAINED and still trip HasIllegitimateDuplicate."
  - "ANL-03 resolver (stepIdByMessageId) keyed off the canonical 'hop executed' MessageId: the dispatch record's inbound attributes.EntryId equals the producer hop's consumed MessageId (OutputTail.BuildStep stamps StepCompleted.EntryId = dr.MessageId, the same value the producer's 'hop executed' logs), so the EntryId->producer-stepId join resolves correctly from the canonical record."
  - "proven set narrowed to genuine 'terminal reached' records only (was previously any no-MessageId record incl. fan-out/Trip-ended/author logs) — over-population of proven can only mask loss, never manufacture the false FAIL this fix targets, so the narrowing is verdict-safe."

patterns-established:
  - "A live-fixture discrimination that a hermetic fact cannot reach is a gap surface: extract it into a pure classifier in the shared *Observability.Analysis* namespace so the [[hermetic-test-command]] namespace filter picks up regression facts that exercise the identical code path."

requirements-completed: []

# Metrics
duration: ~1h
completed: 2026-07-17
---

# Phase 78 Plan 05: D-04 gap closure — structural cohort over-count fix Summary

**Closed the D-04 blocking finding by making the live structural cohort count each genuine hop exactly ONCE.** Phase 77's uniform execution-scope logging + the D3 outbound-`{MessageId}` capture made the processor `"result sent"` (OutputTail.cs:94) and orchestrator `"fan-out"` (OrchestratorPrePipeline.cs:170) records ALSO carry `{StepId, ExecutionId, MessageId}`, so all three record kinds matched the structural ES query and the old `hasMessageId` discriminator counted each hop 3-4× → `Duplicates == StartedRuns` false-failed every live run (incl. the no-fault TEST-01). The fix extracts a pure `StructuralCohort` classifier that selects ONLY the canonical `"hop executed"` consume record (`ProcessorPipeline.cs:218`) — the one record that fires exactly once per genuine execution — as the observed did-run hop, routes `"terminal reached"` to the ANL-03 proven set, and ignores `"result sent"` / `"fan-out"`. Crucially it does NOT naively de-dup by `(executionId, stepId)`: genuine redelivery duplicates are retained and still trip `HasIllegitimateDuplicate`, preserving the effect-once detection that is the whole point of the resilience proof. A new hermetic `StructuralCohortFacts` suite reproduces the exact live multi-record-per-hop shape (30 records / 10 stepIds, 3 per hop) and pins both the collapse→Pass AND the genuine-redelivery→Fail — so the class of bug that was invisible hermetically under the one-record-per-stepId `RunTrace.FromStepIds` helper is now provable WITHOUT a live stack.

## What changed

### Task 1 — canonical-record classifier + fixture wiring (commit `a1068ee`)
- **New `tests/BaseApi.Tests/Observability/Analysis/StructuralCohort.cs`** (namespace `BaseApi.Tests.Observability.Analysis`, shared by the live fixture AND the hermetic facts — the seam the 78-04 gap lacked):
  - `internal readonly record struct FrameworkLogRecord(CorrelationId, ExecutionId, StepId, MessageId?, BodyText, Timestamp?)` — one parsed framework log record, JsonElement-decoupled.
  - `ConsumeRecordPrefix = "hop executed"` and `TerminalRecordPrefix = "terminal reached"` — the Ordinal `StartsWith` discriminators (the sole structural evidence rule).
  - `StructuralCohort.Classify(records)` → `StructuralCohortResult`: `"hop executed"` → observed did-run hop (duplicates RETAINED, min/max @timestamp span, `stepIdByMessageId` ANL-03 resolver from the canonical MessageId); `"terminal reached"` → proven (ANL-03); any other body (`"result sent"`, `"fan-out"`, `"Trip ended …"`, business logs) → IGNORED.
- **`AnalyzerE2ETests.cs` BuildRunTraces**: replaced the inline `hasMessageId` loop with a new `ParseStructuralRecords(...)` helper (defensively reads `_source.attributes.{CorrelationId,ExecutionId,StepId,MessageId}` + `_source.body.text` + `@timestamp`, drops odd-shaped hits per T-66-09) + a single `StructuralCohort.Classify(...)` call. The dispatch loop (ANL-02 expected set + ANL-03 EntryId→producer edge + convergent in-degree) and the `RunTrace.FromStepIds(...)` construction are UNCHANGED. **D-01 (`exists ExecutionId`) and D-02 (`must_not ReinjectOutcome`) query filters unchanged.**

### Task 2 — hermetic regression facts (commit `e470428`)
- **New `tests/BaseApi.Tests/Observability/Analysis/StructuralCohortFacts.cs`** (3 facts, hermetic — pure synthetic `FrameworkLogRecord` lists, no ES/Prom/Docker):
  - `NoFault_MultiRecordPerHop_Collapses_ToOneObservedHop_Pass` — 10 distinct stepIds, each emitting `"hop executed"` + `"result sent"` + `"fan-out"` (30 records, the 78-04 signature). Classify collapses to 10 observed hops (one per stepId), `HasIllegitimateDuplicate == false`, `Duplicates` empty, `Verdict == Pass`. **This fact FAILS against pre-Task-1 code (3× count → false duplicate) and PASSES after.**
  - `GenuineRedelivery_TwoConsumeRecords_ForOneStep_TripsDuplicate` — same run + one extra `"hop executed"` for `hop-c` (a genuine second consume). Classify RETAINS the duplicate → `HasIllegitimateDuplicate == true`, `Duplicates` non-empty, `Verdict == Fail`. **Proves the fix did NOT blind duplicate detection (the forbidden collapse-to-presence-only outcome).**
  - `TerminalReached_Record_FeedsProven_NotObserved` — a `"terminal reached"` record (no MessageId) is in `proven`, NOT in the observed did-run list.

### Task 3 — hermetic gate re-prove (verification-only, no commit)
- 0-warning **Debug + Release** build (`BaseApi.Tests.csproj`).
- Full `*Observability.Analysis*` subset: **32 total, 0 failed** (the prior 29 facts + the 3 new `StructuralCohortFacts`).

## Root-cause → fix mapping (78-04 signature now absent)

The 78-04 finding: one no-fault TEST-01 execution had **30 StepId records over 10 distinct stepIds, each hop 3-4×** → `RunTrace.FromStepIds` set `HasIllegitimateDuplicate` on every hop → `Duplicates == StartedRuns` on every run, value axis dark. The over-count came from `BuildRunTraces` treating `hasMessageId` as the processor-consume discriminator, which post-Phase-77 also matches `"result sent"` and `"fan-out"`. The fix selects the ONE canonical `"hop executed"` record by Ordinal body.text prefix, so 30 records over 10 stepIds now yield exactly 10 observed hops — the `Duplicates == StartedRuns` signature can no longer arise from framework send/consume multiplicity, while a genuinely twice-consumed step still produces a duplicate. `StructuralCohortFacts.NoFault_MultiRecordPerHop_Collapses_ToOneObservedHop_Pass` reproduces the exact 30/10/3× shape hermetically and asserts the collapse — the regression that was invisible under the old one-record-per-stepId facts is now pinned.

## Verification

| Check | Command | Result |
|-------|---------|--------|
| Debug build 0-warning | `dotnet build …csproj -c Debug` | Build succeeded, 0 warnings, 0 errors |
| Release build 0-warning | `dotnet build …csproj -c Release` | Build succeeded, 0 warnings, 0 errors |
| New regression facts green | `BaseApi.Tests.exe … --filter-class "*StructuralCohortFacts*"` | total 3, failed 0 |
| Full analyzer subset green | `BaseApi.Tests.exe … --filter-namespace "*Observability.Analysis*"` | total 32, failed 0 (prior 29 + 3 new) |

The ~282 pre-existing Docker-less broker/Postgres/Redis/ES failures are the expected baseline (0 analyzer facts among them); this plan's targeted analyzer scope is fully green with ZERO new failures.

## Deviations from Plan

None — plan executed exactly as written. The ANL-03 join-invariant confirmation (Task 1 `read_first`) held as documented: `OutputTail.BuildStep` stamps `StepCompleted.EntryId = dr.MessageId` and the producer's `"hop executed {MessageId}"` logs that same inbound MessageId, so keying `stepIdByMessageId` off the canonical consume record resolves `dispatch.EntryId → producer stepId` correctly (no need to also populate from result-sent/fan-out MessageIds).

## Scope note

This plan closes the HERMETIC half of D-04 (the fix is proven without a live stack). The full D-04 acceptance also requires the live re-gate (reseed + 7-scenario sweep reproducing the HEAD baseline), which is **78-06 (operator-gated)**. Requirement D-04 therefore remains OPEN pending 78-06; no requirement is marked complete here.

## Self-Check: PASSED

- tests/BaseApi.Tests/Observability/Analysis/StructuralCohort.cs — FOUND
- tests/BaseApi.Tests/Observability/Analysis/StructuralCohortFacts.cs — FOUND
- tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs — FOUND (modified)
- Commit a1068ee (Task 1) — FOUND
- Commit e470428 (Task 2) — FOUND
