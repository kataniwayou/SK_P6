---
phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model
reviewed: 2026-06-18T00:00:00Z
depth: standard
files_reviewed: 40
files_reviewed_list:
  - src/BaseProcessor.Core/Observability/ProcessorMetrics.cs
  - src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs
  - src/BaseProcessor.Core/Processing/OutputTail.cs
  - src/BaseProcessor.Core/Processing/PostProcessConsumer.cs
  - src/Keeper/Health/BitHealthLoop.cs
  - src/Keeper/Observability/KeeperMetrics.cs
  - src/Keeper/Program.cs
  - src/Keeper/Recovery/DeleteConsumer.cs
  - src/Keeper/Recovery/InjectConsumer.cs
  - src/Keeper/Recovery/OrchestratorDeleteConsumer.cs
  - src/Keeper/Recovery/OrchestratorInjectConsumer.cs
  - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
  - src/Keeper/Recovery/RecoveryConsumerBase.cs
  - src/Keeper/Recovery/ReinjectConsumer.cs
  - src/Orchestrator/Consumers/TypedResultConsumer.cs
  - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
  - src/Orchestrator/Dispatch/RelocateTail.cs
  - src/Orchestrator/Dispatch/StepDispatcher.cs
  - src/Orchestrator/Observability/OrchestratorMetrics.cs
  - tests/BaseApi.Tests/Keeper/DeleteConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/Health/BitHealthLoopTests.cs
  - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs
  - tests/BaseApi.Tests/Keeper/OrchestratorDeleteConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/OrchestratorInjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs
  - tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs
  - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs
  - tests/BaseApi.Tests/Orchestrator/MetricsRoundTripE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs
  - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
  - tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs
findings:
  critical: 0
  warning: 0
  info: 2
  total: 2
status: issues_found
---

# Phase 74: Code Review Report

**Reviewed:** 2026-06-18
**Depth:** standard
**Files Reviewed:** 40
**Status:** issues_found (Info only)

## Summary

This phase reshaped business metrics into a uniform two-counter model
(`{service}_messages_consumed` / `{service}_messages_sent`) with camelCase
`workflowId`+`processorId` labels, removed three legacy counters
(`ResultDeduped` / `DispatchDeduped` / `ReinjectDropped`), dropped the processor
`outcome` label, and added a label-less `keeper_l2_probe` heartbeat.

The implementation is correct against every focus area. Counter-increment
placement is precise: every sent-counter fires strictly after a successful broker
Send (past the success/throw guard) and never on a drop/early-return path; fan-out
is counted per-match with no double-counting; labels are camelCase with
`messageId` never appearing as a label; meters are built via `IMeterFactory` with
unchanged names; and the test listeners scope by reference identity where parallel
safety matters. No leftover references to the removed members or the `outcome`
label exist in production code.

Two Info-level findings, both stale doc-comment references to removed counter
names. Neither affects behavior.

### Counter-increment correctness (verified)

- **Processor sent** — `OutputTail.SendResult` increments only after
  `if (!sent.Succeeded) throw sent.Error!` (`OutputTail.cs:127-134`). The
  `SendKeeper` INJECT path on the processor does NOT increment (correct: a
  processor INJECT is the keeper's count to make on consume; the processor only
  counts its own Step* result send).
- **Orchestrator sent** — four count-after-success sites, all confirmed past the
  success guard: `StepDispatcher.cs:35-44` (single dispatch site, covers both
  forward dispatch and `RelocateTail` post-process dispatch — no second increment
  in `RelocateTail.RunAsync`), `OrchestratorPrePipeline.cs:136-144` (fan-out, per
  match, after `if (!sent.Succeeded) throw`), `OrchestratorPrePipeline.cs:180-187`
  (`SendKeeper`, covers both REINJECT and DELETE escalations), and
  `RelocateTail.cs:99-106` (INJECT escalation `SendKeeper`).
- **Keeper sent** — `RecoveryConsumerBase.CountSent` is called only after the
  send Guard (which re-throws on exhaustion) in the four sending consumers
  (`ReinjectConsumer.cs:65`, `OrchestratorReinjectConsumer.cs:73`,
  `InjectConsumer.cs:63`, `OrchestratorInjectConsumer.cs:63`). Both Delete
  consumers (`DeleteConsumer`, `OrchestratorDeleteConsumer`) never call it. Both
  Reinject consumers correctly skip it on the absent-data drop branch
  (`ReinjectConsumer.cs:38-45`, `OrchestratorReinjectConsumer.cs:42-49`).
- **Consumed counters** — incremented once at consume entry in
  `EntryStepDispatchConsumer`, `PostProcessConsumer`, `TypedResultConsumer`, and
  the single `RecoveryConsumerBase.Consume` choke point all six recovery consumers
  funnel through.
- **L2Probe** — `BitHealthLoop.cs:51`: label-less, unconditional, once per tick
  outside the edge guard. Correct.

### Label correctness (verified)

Every consumed/sent series carries camelCase `workflowId`+`processorId`.
`messageId` is never used as a label anywhere (all `messageId` references are L2
keys, record fields, or envelope ids). `keeper_l2_probe` is label-less. The E2E
guard `MetricsRoundTripE2ETests.AssertBusinessLabels` (lines 234-256) asserts
`workflowId`+`processorId` present and explicitly forbids any `outcome` label.

### Removed members & meter construction (verified)

No production references to `ResultDeduped` / `DispatchDeduped` / `ReinjectDropped`
or the `outcome` metric label remain (the `m.Outcome` discriminator in
`OrchestratorReinjectConsumer` and the `outcome` param in `StepAdvancement` are
unrelated routing constructs, not metric labels). All three meter holders build
via `IMeterFactory` (no static `Meter`); meter names (`BaseProcessor` /
`Orchestrator` / `Keeper`) are unchanged and registered via
`AddSingleton` + `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(...))`
keyed on the `MeterName` const.

### Test-listener scoping (verified)

Increment-count assertions scope to the exact instrument by reference identity
(`ReferenceEquals(instrument, metrics.MessagesSent)`) in
`ReinjectConsumerFacts`, `OrchestratorReinjectConsumerFacts`, and
`BreakerMetricsFacts.Recorded_Measurement_Carries_Expected_Label_Keys`. Absence
asserts scope to the instance's meter by reference. This addresses the prior
xUnit-v3 parallel-class double-count bug.

## Info

### IN-01: Stale legacy-counter name in `AnalyzerReport.TriggerCount` doc-comment

**File:** `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs:80-84`
**Issue:** The XML doc on `TriggerCount` still describes its derivation as
`round(orchestrator_dispatch_sent_total delta)` — a counter name removed this
phase. The actual derivation (`PassFailEngine.TriggerCountFrom`, line 105) reads
the new `OrchestratorMessagesSentDelta`. Comment-only drift; no behavior impact,
but it references a series that no longer exists.
**Fix:** Update the doc-comment to name the surviving counter:
```csharp
/// CORROBORATION ONLY (67-03): the dispatch-derived count
/// (round(orchestrator_messages_sent_total delta)). Retained as Prom evidence — the orchestrator
/// emits one dispatch per STEP, so this is ~9x the run count and is NO LONGER the per-run
/// denominator. Feeds <see cref="PromImpliedRuns"/>, never the binding verdict.
```

### IN-02: Stale legacy-counter name in `PromCounterSnapshot.ProcessorMessagesSentDelta` doc-comment

**File:** `tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs:37-42`
**Issue:** The doc on `ProcessorMessagesSentDelta` reads
`processor_result_sent{outcome="completed"}` to explain the repoint. This is
intentional historical context (it documents what the field was repointed FROM),
but the `{outcome="completed"}` selector references the dropped `outcome` label,
which could mislead a future reader into thinking the label still exists. Lines
19-22 of the same file already enumerate the removed names as "GONE", so the
forward-looking contract is unambiguous; this is the only spot that embeds the old
selector syntax inline.
**Fix:** Optional. If kept for provenance, prefix with "(legacy, removed)" to make
the historical framing explicit, e.g.
`repointed from the legacy (removed) processor_result_sent{outcome="completed"}`.

---

_Reviewed: 2026-06-18_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
