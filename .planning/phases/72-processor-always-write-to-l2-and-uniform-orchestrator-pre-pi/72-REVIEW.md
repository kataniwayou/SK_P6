---
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
reviewed: 2026-06-17T00:00:00Z
depth: standard
files_reviewed: 17
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/OutputTail.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/Messaging.Contracts/DataResult.cs
  - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
  - src/Orchestrator/Dispatch/StepAdvancement.cs
  - src/Orchestrator/Observability/OrchestratorMetrics.cs
  - tests/BaseApi.Tests/Orchestrator/MeterCollectorSeam.cs
  - tests/BaseApi.Tests/Orchestrator/MultiStepHydrationCascadeTests.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs
  - tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs
  - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
  - tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs
  - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
  - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
  - tests/BaseApi.Tests/Processor/OutputTailFacts.cs
  - tests/BaseApi.Tests/Processor/PrePipelineFacts.cs
findings:
  critical: 0
  warning: 1
  info: 4
  total: 5
status: issues_found
---

# Phase 72: Code Review Report

**Reviewed:** 2026-06-17T00:00:00Z
**Depth:** standard
**Files Reviewed:** 17
**Status:** issues_found

## Summary

This is a recovery-pipeline refactor that (a) makes the processor `OutputTail`
always-write the L2 `out:` blob for every terminal outcome (Completed / Failed /
Cancelled, never Processing) and (b) makes the orchestrator Pre-pipeline
branch-free with a three-way `out:` read, a clean-absent idempotent ack-skip, and
a new `orchestrator_step_unresolved` counter incremented at two stages.

The five reviewer focus areas all hold up under analysis:

- **Three-way read discrimination** (OrchestratorPrePipeline.cs:104-117) is
  correct. The read lambda returns `null` on a clean-absent/empty value and lets a
  `RedisException` propagate out of the lambda; `RetryLoop` converts the
  exhausted exception into `read.Succeeded == false`. So: fault -> `!Succeeded` ->
  REINJECT; clean-absent -> `Succeeded` with `null`/empty value -> ack-skip;
  present -> relocate. The three branches are mutually exclusive and total. The
  facts (`Redis_fault_on_read_produces_one_REINJECT_and_no_fanout`,
  `Clean_absent_out_blob_acks_without_fanout_keeper_or_delete`,
  `Processing_rides_clean_absent_skip_no_fanout`) pin each arm.

- **Metric increment placement** is correct. Stage-1 (line 78) increments then
  `return`s immediately, so the clean-absent and terminal paths below never run
  for that message. Stage-3 (lines 139-146) sits AFTER the clean-absent gate
  (line 110) and after the fan-out, and uses `continue` (never `throw`) — a
  clean-absent message never reaches it, and a normal fully-resolved fan-out has
  an empty `UnresolvedIds` so it never increments. The facts
  (`Normal_multimatch_fanout_does_not_increment`, `Condition_skip_does_not_increment`,
  `Terminal_step_logs_completed_terminal_and_acks`,
  `Dangling_next_step_increments_once...`) confirm the count surface.

- **Always-write gate** (`OutputTail.cs:63`, `result != StepOutcome.Processing`)
  is correct: only the transient `Processing` arm skips the write and keeps
  `EntryId = Guid.Empty` (BuildStep default arm, line 96-97). No terminal business
  path stamps `Guid.Empty` — all three terminal arms stamp `EntryId = dr.MessageId`.

- **WR-03 data-exposure discipline** holds. `validatedData` is written to L2 in the
  three failure paths (ProcessorPipeline.cs:88, 111, 136) but never appears in a log
  argument or a wire field. The unexpected/deserialize catch (line 126-139) emits the
  sanitized constant `"input deserialization failed"` on `ErrorMessage` and logs only
  `ex` to the local logger — pinned by `SeamThrows_Unexpected_Failed` and
  `MalformedPayload_DeserFailure_...` (`Assert.DoesNotContain("boom", ...)`).

- **MeterListener seam** (`MeterCollectorSeam.cs`) is per-instance `IDisposable`
  with a `lock (_gate)` on every read/write of `_measurements`, no static state, and
  a clean `Dispose()` that disposes the listener. No leak or cross-test bleed.

No critical issues. One warning (an empty-string terminal blob is indistinguishable
from clean-absent) and four informational items follow.

## Warnings

### WR-01: An empty-string terminal `out:` blob is silently reclassified as clean-absent

**File:** `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:107,110`
**Issue:** The processor always-write gate writes `dr.Data` verbatim for every
terminal outcome (`OutputTail.cs:66`), and `DataResult.Data` defaults to `""`
(`DataResult.cs:15`). If an author returns a terminal `DataResult` whose `Data`
is an empty string (e.g. `NewResult(StepOutcome.Completed, "")`), the processor
writes an empty-string `out:` blob. On the orchestrator side, the read lambda
collapses empty and absent into the same `null` (`raw.IsNullOrEmpty ? null : ...`)
and the gate `string.IsNullOrEmpty(read.Value)` (line 110) then treats that
present-but-empty blob as clean-absent — taking the idempotent ack-skip and
**dropping the fan-out** for a step that genuinely completed. The trip ends with a
"clean-absent" log line that does not reflect what actually happened (the blob WAS
written), so the silent drop is also hard to diagnose from the logs.

This is a behavioral edge, not a crash, and the empty-output case may be considered
out of contract — but the current discrimination cannot tell "processor wrote
nothing (Processing)" from "processor wrote an empty terminal payload," and only the
former is intended to skip. Worth an explicit decision.

**Fix:** Decide and encode the contract. Either (a) document/assert that a terminal
`Data` must be non-empty (validate in `OutputTail.RunAsync` before the write and
fail fast), or (b) make the absence signal unambiguous so an empty payload still
relocates — e.g. distinguish key-missing from empty-value on the read:
```csharp
var read = await RetryLoop.ExecuteAsync(async () =>
{
    var raw = await db.StringGetAsync(L2ProjectionKeys.OutputData(m.EntryId));
    return raw.HasValue ? raw.ToString() : null;   // null == key truly absent; "" == present-empty
}, limit, ct);
if (!read.Succeeded) { await SendKeeper(BuildReinject(m, outcome, messageId), limit, ct); return; }
if (read.Value is null)   // only a truly-absent key is the idempotent skip
{ /* clean-absent ack-skip */ return; }
var relocated = read.Value;   // "" relocates normally
```
Note this also requires the processor side to stop unifying empty with absent if
empty is to be a valid payload.

## Info

### IN-01: `selection.UnresolvedIds` is counted even when the present-blob branch was taken, but a clean-absent skip returns before it — verify intent for the dangling-after-skip case

**File:** `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:110-146`
**Issue:** When the `out:` blob is clean-absent the pipeline returns at line 115,
which is correct for fan-out (nothing to relocate) but also means
`orchestrator_step_unresolved` is NOT incremented for any dangling next-step ids on
that message. So a Processing result (or any skipped message) that ALSO has a
dangling edge will not surface that dangling edge in the metric. This appears
intentional (the docstring at lines 136-138 says the stage-3 loop "sits AFTER the
clean-absent gate so a skipped message never counts stage-3"), and the
`Processing_rides_clean_absent_skip_no_fanout` fact relies on it. Flagging only so
the deferral of dangling-edge observability for skipped messages is a conscious
contract rather than an accident — a dangling edge that only ever appears behind
Processing results would never be observed.

**Fix:** No code change required if intended; consider a one-line note in the XML doc
that dangling-edge counting is deliberately suppressed for clean-absent/Processing
messages so it is not "fixed" later by moving the loop above the gate.

### IN-02: Redundant trailing `continue` in the stage-3 loop

**File:** `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:145`
**Issue:** The `continue;` is the last statement of the `foreach` body, so it is a
no-op. It is clearly there to document "graceful business skip — never throw," but
mechanically it does nothing.
**Fix:** Drop the statement and keep the intent in the comment, or leave as-is if the
team prefers the explicit marker. Cosmetic only.

### IN-03: `ResultOutcome` fallback maps an unknown `IStepResult` to `"failed"`

**File:** `src/BaseProcessor.Core/Processing/OutputTail.cs:134-141`
**Issue:** The `_ => "failed"` default arm means any future `IStepResult` subtype
that is not one of the four known records is silently tagged as `outcome=failed` on
the `ResultSent` metric. Today the set is closed (the `switch` above it in `BuildStep`
covers all four), so this is unreachable, but a new result type added later would be
mis-labeled in telemetry without any compiler warning.
**Fix:** Optional — if the result hierarchy is meant to stay closed, leave it; if it
may grow, consider tagging the fallback `"unknown"` so a new type is visible rather
than masquerading as a failure.

### IN-04: `SendKeeper` divergence between OutputTail and ProcessorPipeline (endpoint resolution placement)

**File:** `src/BaseProcessor.Core/Processing/OutputTail.cs:143-153` vs `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:189-195`
**Issue:** `OutputTail.SendKeeper` resolves `GetSendEndpoint` INSIDE the `RetryLoop`
(so a transient endpoint-resolution fault is retried), while `ProcessorPipeline.SendKeeper`
resolves it ONCE outside the loop and retries only the `Send`. The OutputTail comment
(line 145) calls this out as the intended "keeper Guard parity" pattern, so the two
are knowingly different — but the divergence in two near-identical helpers in the same
assembly is an easy source of future confusion (a reader may "fix" one to match the
other and change retry semantics). The orchestrator's `SendKeeper`
(`OrchestratorPrePipeline.cs:157-166`) follows the OutputTail (inside-loop) form, so
`ProcessorPipeline` is the lone outlier.
**Fix:** Optional consistency cleanup — align `ProcessorPipeline.SendKeeper` to resolve
the endpoint inside the `RetryLoop` like the other two, or add a one-line comment on
the ProcessorPipeline version explaining why it deliberately resolves outside.

---

_Reviewed: 2026-06-17T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
