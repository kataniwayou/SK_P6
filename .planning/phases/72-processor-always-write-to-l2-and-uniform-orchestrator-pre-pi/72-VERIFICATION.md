---
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
verified: 2026-06-17T00:00:00Z
status: passed
score: 6/6 must-haves verified
overrides_applied: 0
re_verification:
  previous_status: none
  note: initial verification
---

# Phase 72: Processor Always-Write to L2 + Uniform Orchestrator Pre-Pipeline + Unresolved-Step Metric — Verification Report

**Phase Goal:** Make every business processor result write its `DataResult.Data` blob to L2 (so `StepFailed`/`StepCancelled` carry a real `EntryId` instead of `Guid.Empty`); drop the Completed-only `out:` read/delete branches in `OrchestratorPrePipeline` so all four `TypedResultConsumer<T>` shells run one uniform flow; add the Phase-71-deferred `orchestrator_step_unresolved` counter (label `workflowId` only) incremented at the two L1-resolution misses. 0-warning Debug+Release, hermetic tests.
**Verified:** 2026-06-17
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths (SPEC-1..6)

| #     | Truth (SPEC requirement)                                                                                                   | Status     | Evidence |
| ----- | -------------------------------------------------------------------------------------------------------------------------- | ---------- | -------- |
| SPEC-1 | `OutputTail` writes the `out:` blob for every terminal outcome (Completed/Failed/Cancelled), NOT gated on `== Completed`   | ✓ VERIFIED | `OutputTail.cs:63` gate is `if (result != StepOutcome.Processing)`. The single residual `result == StepOutcome.Completed` (`:57`) is ONLY the output-validate→Failed block (grep count == 1), not a write gate. Write block (StringSetAsync + JitteredTtl + INJECT-on-exhaust) lives inside the `!= Processing` gate (`:65-71`). |
| SPEC-2 | No business `Step*` stamps `EntryId = Guid.Empty` except Processing; Failed/Cancelled stamp `dr.MessageId`                  | ✓ VERIFIED | `OutputTail.BuildStep` (`:86-98`): Completed/Failed/Cancelled arms all set `EntryId = dr.MessageId`; only the `_ => StepProcessing` arm (`:96-97`) sets no `EntryId` (defaults `Guid.Empty`). No `EntryId = Guid.Empty` literal on any business arm. |
| SPEC-3 | Thrown `ExecuteAsync` + both catch blocks + input-validation-fail route through `OutputTail` carrying `Data = validatedData` | ✓ VERIFIED | `ProcessorPipeline.cs`: input-fail (`:85-91`), `ProcessStatusException` catch (`:108-123`), unexpected/deser catch (`:133-138`) each build a `DataResult { Data = validatedData, MessageId = messageId }` and `await outputTail.RunAsync(...)`. `grep -c outputTail.RunAsync` == 5 (success + 4 reroutes incl. the 2 catch arms). Deser path uses sanitized constant `"input deserialization failed"` (`:136`); `ex.Message` count == 0 in wire paths (only a comment at `:128`). Dead `BuildFailed/BuildCancelled/BuildProcessing` removed (only a comment ref at `:199`). |
| SPEC-4 | `orchestrator_step_unresolved` counter (snake_case, no `_total`), `workflowId`-only label, incremented at stage-1 + stage-3; `OrchestratorMetrics` ctor-injected | ✓ VERIFIED | `OrchestratorMetrics.cs:57` creates `meter.CreateCounter<long>("orchestrator_step_unresolved")` (no `_total`). `OrchestratorPrePipeline` ctor (`:60-67`) takes `OrchestratorMetrics metrics`. `metrics.StepUnresolved.Add` count == 2: stage-1 L1-miss (`:78`, before `return`) and stage-3 dangling-id `foreach`/`continue` (`:144`). Both tag `("workflowId", m.WorkflowId.ToString("D"))`. No `orchestrator_step_unresolved_total` literal in source. |
| SPEC-5 | `OrchestratorPrePipeline.RunAsync` has NO `outcome == StepOutcome.Completed` branch; clean-absent → idempotent skip, no keeper | ✓ VERIFIED | `grep -c "outcome == StepOutcome.Completed"` == 0. Read runs for every outcome (`:104-108`), three-way: fault → REINJECT (`:109`); clean-absent (`string.IsNullOrEmpty(read.Value)`, `:110-116`) → log + `return` with NO fan-out/keeper/delete; present → relocate/fan-out/delete. Processing rides clean-absent (no `out:` blob → skip). `ExecutionId = m.ExecutionId` threaded unchanged (`:129`). |
| SPEC-6 | Conventions preserved: snake_case + camelCase label + `IMeterFactory` (no static Meter) + meter name `Orchestrator` unchanged; 0-warning Debug AND Release | ✓ VERIFIED | `OrchestratorMetrics.cs:53` `meterFactory.Create(MeterName)`, no static `Meter` field; `MeterName == "Orchestrator"` (`:25`) unchanged; `Program.cs:102` `AddMeter(OrchestratorMetrics.MeterName)` unchanged. Label key `"workflowId"` camelCase. Solution build `-c Debug -warnaserror` AND `-c Release -warnaserror` → **0 Warning(s), 0 Error(s)**. |

**Score:** 6/6 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
| -------- | -------- | ------ | ------- |
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | Always-write tail (`!= Processing`) + real EntryId on Failed/Cancelled | ✓ VERIFIED | Gate at `:63`; EntryId stamping at `:88-95`; INJECT-on-write-exhaust preserved (`:67-71`). |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | Catch + input-fail route through OutputTail carrying validatedData; dead builders removed | ✓ VERIFIED | 4 reroutes through `outputTail.RunAsync`; dead `BuildFailed/Cancelled/Processing` + orphaned `SendResult/ResultOutcome` removed (kept `SendKeeper`, `BuildReinject`, `BuildDelete`). |
| `src/Messaging.Contracts/DataResult.cs` | Carries diagnostics through the tail | ✓ VERIFIED | Added `ErrorMessage`/`CancellationMessage` (`:20-21`, default `""`) so rerouted catch paths preserve diagnostics (Rule-3 auto-fix, documented in SUMMARY 01). |
| `src/Orchestrator/Dispatch/StepAdvancement.cs` | `SelectNext` → pure `SelectNextResult(Matches, UnresolvedIds)` | ✓ VERIFIED | `readonly record struct SelectNextResult` (`:72-74`); three-way single-pass classification (`:53-60`); no Redis/metrics reference (purity preserved); terminal guard `?? Enumerable.Empty<Guid>()` kept. |
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | `StepUnresolved` counter `orchestrator_step_unresolved` via IMeterFactory | ✓ VERIFIED | `public Counter<long> StepUnresolved` (`:49`); created `:57`; no static Meter; meter name unchanged. |
| `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` | Branch-free pipeline + metrics injection + stage-1/3 increments + clean-absent skip | ✓ VERIFIED | 0 `outcome == Completed` branches; 2 `StepUnresolved.Add`; clean-absent `IsNullOrEmpty` gate with no keeper; ExecutionId threaded. |
| `tests/BaseApi.Tests/Orchestrator/MeterCollectorSeam.cs` | Zero-dep BCL MeterListener counter-capture seam | ✓ VERIFIED | Exists and consumed by `OrchestratorPrePipelineFacts` (`MeterCollector(OrchestratorMetrics.MeterName, "orchestrator_step_unresolved")`). No new NuGet package. |

### Key Link Verification

| From | To | Via | Status | Details |
| ---- | -- | --- | ------ | ------- |
| ProcessorPipeline catch/input-fail | OutputTail.RunAsync | build `DataResult{Data=validatedData}` then `await outputTail.RunAsync` | ✓ WIRED | 4 call sites confirmed live. |
| OutputTail write gate | `L2[out:dr.MessageId]` | `StringSetAsync` under `result != Processing` | ✓ WIRED | `:65-66`. |
| OrchestratorPrePipeline stage-1/stage-3 | `metrics.StepUnresolved` counter | `.Add(1, KeyValuePair "workflowId")` | ✓ WIRED | `:78` + `:144`. |
| OrchestratorPrePipeline read | clean-absent skip vs REINJECT fault | `raw.IsNullOrEmpty ? null` + `string.IsNullOrEmpty(read.Value)` skip / `!read.Succeeded` REINJECT | ✓ WIRED | `:104-116`. |
| OrchestratorMetrics ctor | counter | `meter.CreateCounter<long>("orchestrator_step_unresolved")` | ✓ WIRED | `:57`. |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
| -------- | ------------- | ------ | ------------------ | ------ |
| OrchestratorPrePipeline fan-out | `relocated` (NextStepHandoff.Data) | `db.StringGetAsync(L2[out:EntryId])` — the blob Plan-01's processor always-writes | Yes (real Redis read; clean-absent short-circuits) | ✓ FLOWING |
| StepFailed/StepCancelled | `EntryId` | `dr.MessageId` (= the blob key just written by OutputTail) | Yes | ✓ FLOWING |
| `orchestrator_step_unresolved` increment | tag `workflowId` | `m.WorkflowId.ToString("D")` from inbound result | Yes | ✓ FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
| -------- | ------- | ------ | ------ |
| 0-warning Debug build (SPEC-6) | `dotnet build -c Debug -warnaserror` | 0 Warning(s), 0 Error(s) | ✓ PASS |
| 0-warning Release build (SPEC-6) | `dotnet build -c Release -warnaserror` | 0 Warning(s), 0 Error(s) | ✓ PASS |
| Hermetic dispatch suite | `dotnet test --filter-class OutputTailFacts/PrePipelineFacts/StepAdvancementTests/OrchestratorMetricsFacts/OrchestratorPrePipelineFacts/TypedResultConsumerFacts` | Passed 58, Failed 0, Skipped 0 | ✓ PASS |

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
| ----------- | ----------- | ----------- | ------ | -------- |
| SPEC-1 | 72-01 | Processor writes a blob for every business outcome | ✓ SATISFIED | OutputTail `!= Processing` gate + OutputTailFacts. |
| SPEC-2 | 72-01 | Non-completed results carry a real EntryId + data | ✓ SATISFIED | BuildStep EntryId = dr.MessageId + facts. |
| SPEC-3 | 72-01 | Thrown ExecuteAsync caught → Failed carrying validatedData | ✓ SATISFIED | 4 reroutes through OutputTail + PrePipelineFacts. |
| SPEC-4 | 72-02, 72-03 | `orchestrator_step_unresolved` counter at the two misses | ✓ SATISFIED | Counter + 2 increments + MeterCollector facts. |
| SPEC-5 | 72-02, 72-03 | Uniform pre-pipeline (no Completed-only branch) | ✓ SATISFIED | 0 outcome==Completed; clean-absent skip; uniform Failed/Cancelled facts. |
| SPEC-6 | 72-01, 72-02, 72-03 | Naming + DI conventions preserved; clean build | ✓ SATISFIED | snake_case/camelCase, IMeterFactory, meter name unchanged, 0-warning Debug+Release. |

All 6 SPEC requirements (tracked inline in 72-SPEC.md, no top-level REQUIREMENTS.md for this project) are accounted for and satisfied. No orphaned requirements.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
| ---- | ---- | ------- | -------- | ------ |
| — | — | None blocking | — | The `result == StepOutcome.Completed` and `ex.Message` greps resolve to a legitimate output-validate block and a WR-03 sanitization comment respectively — not stubs or bypasses. No TODO/FIXME/placeholder/empty-return stubs in the phase's modified production files. |

### Human Verification Required

None for the hermetic deliverable. The live-stack Prometheus scrape of `orchestrator_step_unresolved_total` (SPEC Acceptance Criterion #10, `72-SPEC.md:92`) is explicitly **deferred-automated** in the SPEC where the sandbox lacks Docker (and is the imported-metrics-reshape's territory). Per the verification emphasis, the phase is NOT failed on it; all hermetic behaviors are machine-verified above.

### Gaps Summary

No gaps. All six SPEC requirements are machine-verified against live source: the processor always-write gate (`!= Processing`), real `EntryId` stamping on Failed/Cancelled, the three rerouted catch/input-fail paths carrying `validatedData`, the branch-free orchestrator pipeline (zero `outcome == Completed`), the clean-absent idempotent skip, and the `orchestrator_step_unresolved` counter (snake_case, `workflowId`-only label, IMeterFactory-built, ctor-injected, incremented at stage-1 and stage-3). Both Debug and Release builds are 0-warning, and the 58-test hermetic dispatch surface is fully green. The only deferred item (live-stack Prometheus scrape) is sanctioned by the SPEC as out-of-band for the Docker-less sandbox.

---

_Verified: 2026-06-17_
_Verifier: Claude (gsd-verifier)_
