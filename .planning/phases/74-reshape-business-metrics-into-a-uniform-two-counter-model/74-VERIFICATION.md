---
phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
verified: 2026-06-18T10:30:00Z
status: passed
score: 6/6 must-haves verified
overrides_applied: 0
re_verification:
  previous_status: none
  previous_score: n/a
gaps: []
deferred:
  - truth: "Live-stack PassFailEngine close gate run on the Docker stack (net-zero sweep) passes (SPEC AC #9)"
    addressed_in: "Deferred-automated (sandbox lacks Docker)"
    evidence: "SPEC AC #9 declares this deferred-automated; verification guidance explicitly instructs NOT to fail the phase for the absence of a live Docker run. Hermetic metric-facts subset + compiling (RealStack-excluded) AnalyzerE2ETests/MetricsRoundTripE2ETests cover the contract; the live close gate runs on the Docker stack."
---

# Phase 74: Reshape Business Metrics to a Uniform Two-Counter Model — Verification Report

**Phase Goal:** Collapse every service's assorted business counters into a uniform pair `{service}_messages_consumed` / `{service}_messages_sent` (snake_case, no `_total` suffix in code — collector appends it), labeled `workflowId`+`processorId` (camelCase). Rewrite OrchestratorMetrics/ProcessorMetrics/KeeperMetrics + removals (ResultDeduped, DispatchDeduped, ReinjectDropped; drop the processor `outcome` label); inject KeeperMetrics into the 4 sending keeper consumers + BitHealthLoop; add label-less `keeper_l2_probe`; rework PassFailEngine + PromCounterSnapshot + the affected metric test files.

**Verified:** 2026-06-18T10:30:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

Truths are mapped to the 6 SPEC requirements (the traceability set — there is no global REQUIREMENTS.md; the locked requirements live in 74-SPEC.md). The PLAN-frontmatter must_haves across the 4 plans were merged with the SPEC acceptance criteria.

| #   | Truth (SPEC req)                                                                                                     | Status     | Evidence |
| --- | ------------------------------------------------------------------------------------------------------------------- | ---------- | -------- |
| 1   | REQ-1: Orchestrator uniform pair `orchestrator_messages_consumed`/`_sent`, camelCase labels; `ResultDeduped` gone   | ✓ VERIFIED | `OrchestratorMetrics.cs:34,43,55-56` exposes MessagesConsumed/MessagesSent; no ResultDeduped member; `TypedResultConsumer.cs:55` consumed increment; `StepDispatcher.cs:44`, `OrchestratorPrePipeline.cs:142,185`, `RelocateTail.cs:104` sent increments — all count-after-success, camelCase workflowId+processorId |
| 2   | REQ-2: Processor uniform pair `processor_messages_consumed`/`_sent`, NO `outcome` label; `DispatchDeduped` gone     | ✓ VERIFIED | `ProcessorMetrics.cs:37,45,56-57` (SpawnDropped kept, no DispatchDeduped); `EntryStepDispatchConsumer.cs:38` + `PostProcessConsumer.cs:46` consumed; `OutputTail.cs:132` sent after `if(!sent.Succeeded) throw` (line 127); comment confirms `outcome` label + ResultOutcome helper removed; no `"outcome"` metric tag in src |
| 3   | REQ-3: Keeper uniform pair at `RecoveryConsumerBase.Consume` choke point + 4 send sites; `ReinjectDropped` gone     | ✓ VERIFIED | `KeeperMetrics.cs:34,40` (no ReinjectDropped); `RecoveryConsumerBase.cs:42` consumed once; `CountSent` helper (line 70) called by `ReinjectConsumer.cs:65`, `OrchestratorReinjectConsumer.cs:73`, `InjectConsumer.cs:63`, `OrchestratorInjectConsumer.cs:63`; both Delete consumers never call it; drop branch (`ReinjectConsumer.cs:42`) skips CountSent |
| 4   | REQ-4: label-less `keeper_l2_probe` increments once per BIT probe tick                                              | ✓ VERIFIED | `KeeperMetrics.cs:45,52` L2Probe counter; `BitHealthLoop.cs:51` `metrics.L2Probe.Add(1)` — unconditional, once per tick, outside the edge guard, label-less |
| 5   | REQ-5: PassFailEngine + PromCounterSnapshot reference new names; no `outcome`/removed counters; metric-facts GREEN  | ✓ VERIFIED | `PassFailEngine.cs:105,257,289` repointed to `OrchestratorMessagesSentDelta`/`OrchestratorMessagesConsumedDelta`; line 278 documents the non-completed outcome WARNING path REMOVED; `PromCounterSnapshot.cs:29,32,35,42` four renamed delta fields, NonCompletedOutcomes dict removed; hermetic metric-facts 45/45 + 22/22 GREEN |
| 6   | REQ-6: snake_case names with NO `_total` in code, camelCase labels, meter names unchanged, KeeperMetrics injected   | ✓ VERIFIED | `_total` appears ONLY in comments, never in any `CreateCounter` name string; all increment sites use camelCase `workflowId`/`processorId`; `AddMeter` registrations reference `MeterName` consts (`Orchestrator`/`BaseProcessor`/`Keeper`) unchanged; KeeperMetrics injected via `RecoveryConsumerBase` ctor (line 27) + `BitHealthLoop` ctor (line 31), registered in `Program.cs:91`; built via IMeterFactory in all three classes; 0-warning Debug + Release |

**Score:** 6/6 truths verified

### Deferred Items

| # | Item | Addressed In | Evidence |
|---|------|-------------|----------|
| 1 | Live-stack PassFailEngine close gate (net-zero sweep) on Docker stack | Deferred-automated | SPEC AC #9 declares deferred-automated; sandbox lacks Docker. Verification guidance explicitly instructs not to fail the phase for the absent live run. Hermetic facts + compiling RealStack-excluded analyzer tests cover the contract. |

### Required Artifacts

| Artifact | Expected | Status | Details |
| -------- | -------- | ------ | ------- |
| `src/Orchestrator/Observability/OrchestratorMetrics.cs` | MessagesConsumed + MessagesSent; StepUnresolved kept; no ResultDeduped | ✓ VERIFIED | Both counters present, snake_case names, IMeterFactory, no removed member |
| `src/Orchestrator/Consumers/TypedResultConsumer.cs` | consumed increment w/ camelCase labels | ✓ VERIFIED | `MessagesConsumed.Add` at line 55 |
| `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` | MessagesConsumed + MessagesSent; SpawnDropped kept; no DispatchDeduped | ✓ VERIFIED | Confirmed; outcome label gone |
| `src/BaseProcessor.Core/Processing/OutputTail.cs` | sent increment, no outcome; ResultOutcome helper removed | ✓ VERIFIED | `MessagesSent.Add` line 132 after success guard; comment confirms helper deleted |
| `src/Keeper/Observability/KeeperMetrics.cs` | MessagesConsumed + MessagesSent + L2Probe; no ReinjectDropped | ✓ VERIFIED | All three counters; legacy drop counter gone |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs` | Consume choke-point consumed + CountSent helper | ✓ VERIFIED | `MessagesConsumed.Add` line 42, `CountSent` line 70 |
| `src/Keeper/Health/BitHealthLoop.cs` | label-less L2Probe increment per tick | ✓ VERIFIED | `L2Probe.Add(1)` line 51 |
| `tests/.../Analysis/PromCounterSnapshot.cs` | renamed delta fields; removed fields gone | ✓ VERIFIED | 4 uniform delta fields; NonCompletedOutcomes + dedup + reinject-dropped fields removed |
| `tests/.../Analysis/PassFailEngine.cs` | bindings repointed; outcome WARNING deleted | ✓ VERIFIED | Repointed; WARNING block removed (line 278 documents removal) |

### Key Link Verification

| From | To | Via | Status | Details |
| ---- | -- | --- | ------ | ------- |
| StepDispatcher.cs | OrchestratorMetrics.MessagesSent | Add after endpoint.Send | ✓ WIRED | Line 44, after `endpoint.Send` (line 35) |
| OrchestratorPrePipeline.cs | OrchestratorMetrics.MessagesSent | Add per fan-out match + SendKeeper | ✓ WIRED | Lines 142 (fan-out), 185 (SendKeeper) |
| RelocateTail.cs | OrchestratorMetrics.MessagesSent | INJECT escalation SendKeeper | ✓ WIRED | Line 104 |
| EntryStepDispatchConsumer.cs | ProcessorMetrics.MessagesConsumed | Add at consume entry | ✓ WIRED | Line 38 |
| OutputTail.cs | ProcessorMetrics.MessagesSent | Add after send-success guard, no outcome | ✓ WIRED | Line 132, past `if(!sent.Succeeded) throw` |
| RecoveryConsumerBase.cs | KeeperMetrics.MessagesConsumed | Add once in Consume | ✓ WIRED | Line 42 |
| ReinjectConsumer/InjectConsumer/Orchestrator* | RecoveryConsumerBase.CountSent | call after ep.Send (not on drop) | ✓ WIRED | ReinjectConsumer.cs:65, OrchestratorReinjectConsumer.cs:73, InjectConsumer.cs:63, OrchestratorInjectConsumer.cs:63; drop branch skips |
| BitHealthLoop.cs | KeeperMetrics.L2Probe | Add per tick, no labels | ✓ WIRED | Line 51 |
| PassFailEngine.cs | PromCounterSnapshot new delta fields | Expected repointed to processor_messages_sent total | ✓ WIRED | Uses OrchestratorMessagesSentDelta/ConsumedDelta throughout |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
| -------- | ------- | ------ | ------ |
| Debug build 0-warning (-warnaserror) | `dotnet build -c Debug -warnaserror` | Build succeeded, 0 Warning(s), 0 Error(s) | ✓ PASS |
| Release build 0-warning (-warnaserror) | `dotnet build -c Release -warnaserror` | Build succeeded, 0 Warning(s), 0 Error(s) | ✓ PASS |
| Hermetic metric-facts subset (1) | `BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-class *MetricsFacts*/*PassFailEngine*/*BreakerMetricsFacts*/*Reinject*/*KeeperMetricsFacts*/*BitHealthLoop*` | 45 total, 0 failed, 45 succeeded | ✓ PASS |
| Hermetic metric-facts subset (2) | `BaseApi.Tests.exe ... --filter-class *InjectConsumerFacts*/*DeleteConsumerFacts*/*RecoveryDeadLetterFacts*/*OrchestratorMetricsFacts*/*ProcessorMetricsFacts*/*PromCounterSnapshot*` | 22 total, 0 failed, 22 succeeded | ✓ PASS |
| Full hermetic suite (regression) | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` | 668 total, 0 failed, 668 succeeded | ✓ PASS |
| Removed-counter names absent from src | `grep` for orchestrator_result_deduped/processor_dispatch_deduped/keeper_reinject_dropped/ResultDeduped/DispatchDeduped/ReinjectDropped | No matches | ✓ PASS |
| Old per-type counter names absent from src | `grep` for orchestrator_dispatch_sent/orchestrator_result_consumed/processor_dispatch_consumed/processor_result_sent | No matches | ✓ PASS |
| No `_total` in instrument name strings | `grep _total **/*Metrics.cs` | Matches in comments only | ✓ PASS |
| No `messageId` as metric label | `grep "messageId"` in src | Only doc-comment param references, never a tag key | ✓ PASS |

### Requirements Coverage

| Requirement | Source Plan(s) | Description | Status | Evidence |
| ----------- | -------------- | ----------- | ------ | -------- |
| REQ-1 | 74-01, 74-04 | Orchestrator uniform counters + drop dormant dedup | ✓ SATISFIED | Truth 1 + artifacts/links above |
| REQ-2 | 74-02, 74-04 | Processor uniform counters, drop outcome label + dedup | ✓ SATISFIED | Truth 2 |
| REQ-3 | 74-03, 74-04 | Keeper uniform counters at choke point + 4 send sites, drop legacy | ✓ SATISFIED | Truth 3 |
| REQ-4 | 74-03 | Keeper label-less L2-probe heartbeat | ✓ SATISFIED | Truth 4 |
| REQ-5 | 74-04 | Analyzer + test contract rework | ✓ SATISFIED | Truth 5; hermetic facts GREEN |
| REQ-6 | 74-01,02,03,04 | snake_case/camelCase convention, meter wiring preserved, KeeperMetrics injected | ✓ SATISFIED | Truth 6; 0-warning builds |

No orphaned requirements — all 6 SPEC requirements are claimed across the plans' `requirements` frontmatter (01:[1,6], 02:[2,6], 03:[3,4,6], 04:[1,2,3,5,6]) and all are satisfied.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
| ---- | ---- | ------- | -------- | ------ |
| tests/.../Analysis/AnalyzerReport.cs | 80-84 | Stale doc-comment names removed `orchestrator_dispatch_sent_total` | ℹ️ Info | Comment-only drift; actual derivation reads `OrchestratorMessagesSentDelta`. No behavior impact (per 74-REVIEW IN-01) |
| tests/.../Analysis/PromCounterSnapshot.cs | 37-42 | Doc-comment embeds legacy `processor_result_sent{outcome="completed"}` selector | ℹ️ Info | Intentional historical provenance; lines 19-22 enumerate removed names as GONE. No behavior impact (per 74-REVIEW IN-02) |

No blocker or warning anti-patterns. Both Info findings are stale doc-comments in test files documenting what was repointed FROM; neither references a live code path.

### Human Verification Required

None. Verification is fully automated (SPEC constraint: "Verification is fully automated, never labeled human verification"). All acceptance criteria machine-verified via grep/build/test.

### Gaps Summary

No gaps. All 6 SPEC requirements are substantively implemented in live code and proven by GREEN hermetic tests at 0-warning Debug + Release:

- The three metric classes (`OrchestratorMetrics`/`ProcessorMetrics`/`KeeperMetrics`) expose only the uniform two-counter pair (+ kept StepUnresolved/SpawnDropped + new L2Probe), built via IMeterFactory, meter names unchanged.
- All increment sites count-after-success with camelCase `workflowId`+`processorId`; `messageId` is never a metric label; `keeper_l2_probe` is label-less.
- The three removed counters (`ResultDeduped`/`DispatchDeduped`/`ReinjectDropped` and their snake_case series) and the processor `outcome` label are absent from `src/` and asserted absent in tests.
- `_total` never appears in any instrument name string (collector appends it).
- The analyzer (`PassFailEngine`/`PromCounterSnapshot`) is rebound to the new names with the non-completed outcome WARNING path removed.
- KeeperMetrics is injected via the `RecoveryConsumerBase` choke point (covering the 4 sending consumers) + `BitHealthLoop`, registered as a singleton.

The single deferred item — the live-stack Docker close gate (SPEC AC #9) — is deferred-automated by design (sandbox lacks Docker) and per explicit verification guidance is NOT a gap.

---

_Verified: 2026-06-18T10:30:00Z_
_Verifier: Claude (gsd-verifier)_
