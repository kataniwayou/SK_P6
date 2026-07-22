---
phase: 85-processor-framework-boundary-hardening
reviewed: 2026-07-22T00:00:00Z
depth: deep
files_reviewed: 4
files_reviewed_list:
  - src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs
  - src/BaseProcessor.Core/Processing/BaseProcessor.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/Processor.Sample/SampleProcessor.cs
findings:
  critical: 0
  warning: 2
  info: 2
  total: 4
status: issues_found
---

# Phase 85: Code Review Report

**Reviewed:** 2026-07-22T00:00:00Z
**Depth:** deep (cross-file trace: BaseProcessor.SpawnToPost → ProcessorPipeline.RunAsync catch chain → DeleteEntryTail; diffed against pre-phase baseline `e220c9e`)
**Files Reviewed:** 4
**Status:** issues_found

## Summary

Reviewed the PB-01/PB-02/PB-03 diff (fail-loud `SpawnToPost`, narrow `SpawnSendExhaustedException` nack-escape, framework-owned entry deletion) against the pre-phase baseline. The three locked invariants called out in the task brief all hold under direct code trace:

1. **Narrow nack catch sits above the generic catch-all** (`ProcessorPipeline.cs:194` `catch (SpawnSendExhaustedException) { throw; }` precedes the `catch (Exception ex)` at `:195`) and the generic catch is otherwise untouched — a deterministic seam fault still routes to `StepFailed`+ack, not a nack-loop. Confirmed by the D-03 negative-control fact (`DeterministicSeamFault_DoesNotEscape_OneStepFailed_Ack`).
2. **The Mode-2 null-path framework delete is guarded** by `SourceStep.IsSource(d.EntryId)` (`Guid.Empty` check, `ProcessorPipeline.cs:256`), matching the old author `DeleteEntry` no-op on a source seed. Confirmed by `SeamReturnsNull_Source_NoWrite_NoSend_NoDelete`.
3. **`OnSpawnDropped` fires before the throw** — `BaseProcessor.cs:123-124` invokes the hook, then throws `SpawnSendExhaustedException` on the next line; the deterministic-fault path (`!IsTransientSendFault`) still `throw sent.Error!` raw at `:118`, unchanged.

No blocking correctness/security defect was found in the reviewed diff. Two warnings below concern an undocumented failure-semantics change on the Mode-2 delete tail and an under-tested accepted trade-off (duplicate spawns on partial-fan-out redelivery); two info items are documentation/naming drift with no runtime effect.

## Warnings

### WR-01: PB-03 silently changes Mode-2 delete-tail failure semantics from "contained ack" to "uncaught nack" on two axes

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:219-224` (Mode-2 null-path call site) and `:254-260` (`DeleteEntryTail`)

**Issue:** Before this phase, the Mode-2 entry delete ran *inside* the author's `ProcessAsync` (`BaseProcessor.cs` old `DeleteEntry()`), which used `CancellationToken.None` for both the `KeyDeleteAsync` retry and the keeper-escalation send. Any failure there (including a keeper-send exhaustion, since `SendKeeper` throws on exhaust) was caught by `ProcessorPipeline`'s generic `catch (Exception ex)` (the seam call was wrapped in that try) and degraded gracefully to one `StepFailed` + ack — no redelivery.

After PB-03, `DeleteEntryTail` is called from `RunAsync` *after* the inner try/catch has already returned (`:219-224` and `:238-239`), using the request `ct` (not `CancellationToken.None`) for both the delete and the escalation send. This has two concrete new failure modes, neither exercised by any fact:

- If `ct` is cancelled while `DeleteEntryTail` is retrying (e.g. cooperative shutdown), `RetryLoop.ExecuteAsync`'s `ct.ThrowIfCancellationRequested()` throws `OperationCanceledException` straight out of `RunAsync`, uncaught — the message is left unacked and the whole entry (already fully fanned-out in Mode-2) will be redelivered/re-spawned.
- If the delete AND the keeper-escalation send both exhaust their retries, `SendKeeper` throws (`ProcessorPipeline.cs:334`, unchanged), and that exception now also propagates uncaught out of `RunAsync` instead of being absorbed by the generic catch as before.

Both cases mean an already-completed Mode-2 fan-out (both spawns sent successfully) can now trigger a full-entry nack-requeue where it previously would have degraded to a StepFailed ack with no redelivery. This is a real, undocumented behavior change (not mentioned in 85-03-PLAN.md, 85-03-SUMMARY.md, or 85-PATTERNS.md) and the existing `SeamReturnsNull_NonSource_DeleteExhaust_EscalatesDelete` fact only proves the keeper send *succeeds* when the delete itself fails — it does not cover the double-exhaust or cancellation cases.

**Fix:** Either (a) explicitly document this as an accepted consequence of unifying the two tails (consistent with what Mode-1 already did), or (b) add a hermetic fact that drives both the delete AND the keeper-send to exhaustion on the Mode-2 null path and asserts the resulting exception propagates (so the behavior is pinned, not accidental), e.g.:
```csharp
[Fact]
public async Task SeamReturnsNull_NonSource_DeleteAndKeeperExhaust_PropagatesUncaught()
{
    // db: delete always faults; send: keeper endpoint also faults
    var ex = await Record.ExceptionAsync(() => Build(redisBothFaulting, Ctx(), processor, faultingSend)
        .RunAsync(DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()), Guid.NewGuid(), ct));
    Assert.NotNull(ex);   // pin the new "uncaught propagate" contract
}
```

### WR-02: The accepted "duplicate spawns on partial fan-out redelivery" trade-off is not exercised by any test or the SC-4 gate

**File:** `src/Processor.Sample/SampleProcessor.cs:66-73`, `src/BaseProcessor.Core/Processing/BaseProcessor.cs:95-124`

**Issue:** `85-PATTERNS.md` (lines 30, 306) explicitly accepts that a Mode-2 fan-out where spawn *N* of *M* fails-loud will nack-requeue the **whole entry**, causing redelivery to re-run `ProcessAsync` from scratch and re-spawn *all M* items — including the ones that already sent successfully before the fault (each with a freshly minted `Guid.NewGuid()` executionId, so they are not de-duplicable by execution id). This is a deliberate, locked design decision, not a bug in itself.

However, nothing in the current test suite or the SC-4 sweep actually exercises this *partial*-failure interleaving:
- `DispatchTestKit.SendFaultProvider` (used by `SpawnExhaust_Propagates_SpawnSendExhaustedException_Nack_NoStepFailed`) fails **every** send, so with `SampleProcessor`'s 2-item loop the very first spawn already throws — the "first spawn succeeded, second spawn failed" interleaving that actually produces a duplicate is never reached.
- The SC-4 live sweep's TEST-06 (rabbitmq crash) exercises broker-level redelivery *before* any send in the entry has been attempted, not a mid-loop partial success, and reports `Duplicates==0` — which does not prove the gate would tolerate (or even detect) a genuine partial-fan-out duplicate if one occurred.

**Fix:** Add a `SendFaultProvider`-style double that succeeds on the Nth-1 send and faults on the Nth (e.g. an `int` counter), drive it through `SampleProcessor`'s real 2-item loop, and assert that (a) the first spawn's message was actually sent once, (b) `SpawnSendExhaustedException` still propagates for the second, and (c) document/confirm whether the analyzer/gate's `Duplicates` metric would in fact flag the resulting redelivery-induced re-spawn of the first item as a duplicate (and if so, whether that's tolerated the same way `FALSIFY-02`'s `UnrecoverableLoss` is tolerated).

## Info

### IN-01: Stale comment references the removed `DeleteEntry` helper

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:151`

**Issue:** `// FIRST (so this.SpawnToPost / this.DeleteEntry / this.NewResult have db/sendProvider/ids/hooks).` still names `this.DeleteEntry`, which PB-03 removed from `BaseProcessor` entirely (confirmed: no remaining declaration in `src/`). Harmless (plain `//` comment, not a `<see cref>` that would fail to compile/build docs), but it will confuse the next reader of this exact hardening boundary.

**Fix:**
```csharp
// FIRST (so this.SpawnToPost / this.NewResult have db/sendProvider/ids/hooks).
```

### IN-02: `OnSpawnDropped`/`SpawnDropped` naming and doc comments still describe "swallow" semantics after PB-01 changed the outcome

**File:** `src/BaseProcessor.Core/Processing/BaseProcessor.cs:64-67`, `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:292-296,306-314`

**Issue:** The hook/metric is still named `OnSpawnDropped` / `metrics.SpawnDropped`, and the field-level comment still calls it "spawn-drop telemetry" / "metric+warn hook". Before PB-01 this accurately meant "silently lost, scheduler will re-fire the whole entry independently". After PB-01, the same hook now fires immediately before a `throw` that forces a nack-requeue of the entry — the send is not silently "dropped", it is escalated into an explicit at-least-once retry (which may itself succeed, partially duplicate, or loop indefinitely on a persistent transient fault). An operator dashboarding on `SpawnDropped` counts may reasonably (and now incorrectly) read them as "permanently lost executions" rather than "a nack-requeue was triggered by a transient send fault".

**Fix:** Consider renaming the metric/hook (e.g. `SpawnSendExhausted`) or, at minimum, updating the metric's own doc/description (not just the surrounding prose, which was already updated) to state it now correlates with a full-entry nack-requeue rather than a silent, isolated loss.

---

_Reviewed: 2026-07-22T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_
