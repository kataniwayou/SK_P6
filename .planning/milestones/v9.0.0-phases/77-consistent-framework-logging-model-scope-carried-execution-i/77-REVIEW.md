---
phase: 77-consistent-framework-logging-model-scope-carried-execution-i
reviewed: 2026-07-16T00:00:00Z
depth: standard
files_reviewed: 18
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/OutputTail.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
  - src/Keeper/Recovery/ReinjectConsumer.cs
  - src/Messaging.Contracts/ExecutionLogScope.cs
  - src/Messaging.Contracts/IKeeperRecoverable.cs
  - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
  - src/Processor.Sample/SampleProcessor.cs
  - tests/BaseApi.Tests/Console/ConsoleExecutionScopeFilterTests.cs
  - tests/BaseApi.Tests/Console/ExecutionLogScopeKeeperFacts.cs
  - tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs
  - tests/BaseApi.Tests/Processor/FanInHermeticHarnessFacts.cs
  - tests/BaseApi.Tests/Processor/PerHopLogFacts.cs
  - tests/BaseApi.Tests/Processor/ResultSendLogFacts.cs
  - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
findings:
  critical: 0
  warning: 0
  info: 3
  total: 3
status: issues_found
---

# Phase 77: Code Review Report

**Reviewed:** 2026-07-16T00:00:00Z
**Depth:** standard
**Files Reviewed:** 18
**Status:** issues_found (info-only)

## Summary

Phase 77 implements the "consistent framework logging model" — Tier-1 execution ids
(WorkflowId/StepId/ProcessorId/ExecutionId/EntryId) are stripped from framework log strings and
carried instead via an ambient MEL execution scope, so every framework record surfaces them as ES
`attributes.*`. The keeper recovery consumers (`ReinjectConsumer`, `OrchestratorReinjectConsumer`)
now open that scope themselves (the bus-wide `InboundExecutionScopeConsumeFilter` no-ops on keeper
records, which are `ICorrelated` but not `IExecutionCorrelated`); the processor `OutputTail` gains a
`result sent {MessageId} {Outcome}` record; the orchestrator `OrchestratorPrePipeline` mints and
stamps outbound fan-out MessageIds and logs `fan-out`/`terminal reached` markers.

The change is correct and internally consistent. Key invariants verified:

- **No scope-key / template-placeholder collision.** The scope carries the five Tier-1 ids only;
  the stripped log templates emit `{MessageId}` + `{ReinjectOutcome}` / `{Outcome}` / `{NextStepId}`,
  none of which overlap the scope keys — so no id is double-emitted (D1/LOG-01).
- **Scope-open coverage on every emit path.** Keeper consumers wrap the entire `HandleAsync` body
  (both the drop early-return and the confirmed-send path) in `BeginScope`. Processor/orchestrator
  paths rely on the bus-wide filter for `IExecutionCorrelated` messages. `ExecutionLogScope.BuildState`
  applies byte-identical skip rules across both overloads (each `Guid.Empty` skipped; EntryId skipped
  via `SourceStep.IsSource`), and the `IExecutionCorrelated` overload delegates to the loose-id form.
- **FW-04 resilience preserved.** Every new framework log call is wrapped in a swallow-guard so a
  throwing/blocking logger cannot fail or delay the hop/send.
- **Send/metric/log ordering is safe.** `result sent` (OutputTail), `fan-out` (orchestrator), and the
  keeper `sent` record all fire strictly AFTER the confirmed send; a send-exhaust throws first, and the
  write-fail INJECT-escalation returns before any `result sent` record — so no record is emitted for an
  un-sent message.
- **Test coverage is strong and matches the implementation** — scope-open, placeholder-stripping,
  minted-outbound-id equality, and drop-vs-send discrimination are each asserted hermetically.

No correctness, security, or resilience defects found. Three low-value Info observations follow.

## Info

### IN-01: Stale reference in `using` comment after author-log removal

**File:** `src/Processor.Sample/SampleProcessor.cs:4`
**Issue:** The import comment reads
`using Messaging.Contracts; // DataResult / StepOutcome / ExecutionLogScope`, but Phase 77 removed all
author logs from this processor (D4/LOG-04) and `ExecutionLogScope` is no longer referenced anywhere
in the file. The `using` itself is still required (for `DataResult` / `StepOutcome`), so this is purely
a documentation staleness, not an unused import.
**Fix:** Trim the trailing token from the comment:
```csharp
using Messaging.Contracts;               // DataResult / StepOutcome
```

### IN-02: Broad `catch { }` guards swallow every exception type

**File:** `src/BaseProcessor.Core/Processing/OutputTail.cs:95`; `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:220`; `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:98,109,171`
**Issue:** The FW-04 observability guards use a parameterless `catch { }` that absorbs *all*
exceptions (including `OperationCanceledException`) around the logging call. This is intentional and
documented ("observability must never fail or delay the hop"), and is safe here because the guarded
body is a single synchronous `ILogger` call that does not observe the cancellation token — so there is
no real cancellation signal to hide. Noted only for completeness; no change required unless the guarded
region ever grows to include cancellable work.
**Fix:** None required. If the guarded block is ever expanded, narrow to `catch (Exception)` and
consider re-checking `ct` after the guard so a genuine cancellation is not masked.

### IN-03: `result sent` record is emitted for the transient `Processing` outcome

**File:** `src/BaseProcessor.Core/Processing/OutputTail.cs:79-96`
**Issue:** A seam-thrown `Processing` status routes through `OutputTail.RunAsync`, which skips the
out-blob write (`result != Processing` gate) but still sends a `StepProcessing` and therefore logs
`result sent {MessageId} Processing`. Meanwhile `ProcessorPipeline` deliberately does NOT emit a
`hop executed` record for `Processing` (it is not a terminal hop, D-05). The result is an intentional
asymmetry: a `Processing` message produces a `result sent` record but no `hop executed` record. This
matches the send behavior (a StepProcessing *is* sent) and the Phase-78 analyzer handoff is documented,
so it is a conscious contract rather than a defect. Flagged so the downstream analyzer author is aware
the `result sent` stream includes `Processing`.
**Fix:** None required — verify the Phase-78 analyzer discriminates `Outcome="Processing"` when
correlating `result sent` records, so a transient status is not counted as a terminal send.

---

_Reviewed: 2026-07-16T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
