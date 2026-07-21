---
phase: 70-two-consumer-processor-design-pre-process-post-process-with-
reviewed: 2026-06-17T00:00:00Z
depth: standard
files_reviewed: 18
files_reviewed_list:
  - src/Messaging.Contracts/DataResult.cs
  - src/Messaging.Contracts/KeeperInject.cs
  - src/Messaging.Contracts/KeeperDelete.cs
  - src/Messaging.Contracts/KeeperReinject.cs
  - src/Messaging.Contracts/Projections/L2ProjectionKeys.cs
  - src/BaseProcessor.Core/Processing/BaseProcessor.cs
  - src/BaseProcessor.Core/Processing/BaseProcessor`1.cs
  - src/BaseProcessor.Core/Processing/OutputTail.cs
  - src/BaseProcessor.Core/Processing/PostProcessConsumer.cs
  - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
  - src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs
  - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
  - src/Keeper/Recovery/InjectConsumer.cs
  - src/Keeper/Recovery/DeleteConsumer.cs
  - src/Keeper/Recovery/ReinjectConsumer.cs
  - src/Keeper/RecoveryOptions.cs
  - src/Processor.Sample/SampleProcessor.cs
  - src/Processor.BadConfig/BadConfigProcessor.cs
findings:
  critical: 1
  warning: 3
  info: 4
  total: 8
status: issues_found
---

# Phase 70: Code Review Report

**Reviewed:** 2026-06-17
**Depth:** standard
**Files Reviewed:** 18
**Status:** issues_found

## Summary

The two-consumer (Pre-Process / Post-Process) design is implemented cleanly and the
resilience invariants the SPEC pins are, for the most part, faithfully realized:
no `UseMessageRetry`, no error transport, every L2 op is wrapped in a bounded
`RetryLoop` with the correct per-op escalation (read→REINJECT, write→INJECT,
delete→DELETE), send-exhaust propagates to broker redelivery, the
write→send→delete order is respected in both `OutputTail` and `InjectConsumer`,
the `SpawnToPost`-swallow vs `DeleteEntry`-escalate asymmetry matches the design,
Post never reads/deletes `entryId` (passes `Guid.Empty`), REINJECT/INJECT apply
the per-send envelope `MessageId` override via the `(object)` cast, and REINJECT
uses `STRLEN` for the clean-absent silent drop. The TTL is the jittered
`random[ttl, 2×ttl]` on every output write.

The one serious problem is **not** in the per-message flow logic the 615 tests
exercise — it is in the *lifetime/threading contract* between the Scoped
`ProcessorPipeline` and the **Singleton** `BaseProcessor`. The per-dispatch seam
state (`_entryId`, `_messageId`, `_escalateDelete`, …) is stored as plain mutable
instance fields on the shared singleton and the dispatch/-post endpoints set no
`ConcurrentMessageLimit`, so concurrent consumes on a single replica race and can
delete the wrong entry / write the wrong output key. The hermetic facts drive the
seam serially on freshly-`new`'d processors, so they cannot surface this. Details
below, plus three warnings and four info items.

## Critical Issues

### CR-01: Per-dispatch seam state lives on a SINGLETON `BaseProcessor` mutated per-consume with no concurrency guard — concurrent dispatches corrupt each other's entryId/messageId

**File:** `src/BaseProcessor.Core/Processing/BaseProcessor.cs:32-46,107-123`
(set), `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:89,130-148`
(caller), `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:95-104`
(documented Singleton contract), `src/Processor.Sample/Program.cs:17`
(`AddSingleton<BaseProcessor, SampleProcessor>()`),
`src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:267-288`
(endpoint binds set no `ConcurrentMessageLimit`).

**Issue:**
`ProcessorPipeline` is Scoped (one per consume) but it calls into a **Singleton**
`BaseProcessor`. The DI comment at `BaseProcessorServiceCollectionExtensions.cs:96-100`
explicitly directs the author to register the processor as **Singleton** ("the
expected choice for a stateless transform"), and `Processor.Sample/Program.cs:17`
does exactly that. The problem is that the seam is **not** stateless: per dispatch,
`ProcessorPipeline.SetSeamState` (`ProcessorPipeline.cs:89`) writes eleven mutable
instance fields onto that one shared object — `_db`, `_entryId`, `_processorId`,
`_messageId`, `_workflowId`, `_stepId`, `_correlationId`, `_retryLimit`,
`_sendProvider`, `_onSpawnDropped`, and the `_escalateDelete` **closure** (which is
captured over `EntryStepDispatch d` of *that* consume). The author's `ProcessAsync`
then reads them back via `NewResult` / `SpawnToPost` / `DeleteEntry`.

Neither the dispatch endpoint nor the `-post` endpoint sets a
`ConcurrentMessageLimit` (the two `ConnectReceiveEndpoint` callbacks at
`ProcessorStartupOrchestrator.cs:267` and `:284` configure *only*
`ConfigureConsumer`), so MassTransit/RabbitMQ delivers multiple messages
concurrently to one replica. With concurrent consumes A and B:

1. Consume A: `SetSeamState(entryId=A, escalateDelete=delete(A))`.
2. Consume B (interleaves before A's `ProcessAsync` finishes):
   `SetSeamState(entryId=B, escalateDelete=delete(B))` — clobbers the singleton's
   fields.
3. Consume A's author calls `this.DeleteEntry()` → reads `_entryId == B` →
   **deletes the WRONG entry**, and `_escalateDelete` now escalates `delete(B)`.
   Likewise `NewResult` / the Mode-2 `SpawnToPost` stamp `_messageId == B` →
   `OutputData(messageId=B)` is written/keyed for A's data — a **wrong-key write**
   and a cross-message data swap.

This is exactly the class of bug the SPEC warns against (wrong-key writes,
silent loss). It reopens silent loss: A's real entry is never deleted (leaked to
TTL) while B's entry may be deleted twice / escalated under A's lineage, and the
output blob for A lands under B's messageId.

The fields are also plain (non-`volatile`) and written/read across `await`
boundaries with no memory barrier, so even the "torn read of a stale value"
visibility hazard is present on top of the logical race.

The hermetic facts (`BaseProcessorSeamFacts`, `PrePipelineFacts`) construct a
fresh `FakeProcessor` per test and drive the seam serially, so the 615-test suite
cannot exercise this — it is a concurrency hole the tests do not cover.

**Fix:** The seam state must be per-dispatch, not per-singleton. Options, in
order of preference:

1. **Move the state off `BaseProcessor` into an `AsyncLocal`/scoped context object**
   the helpers read — e.g. a Scoped `SeamStateAccessor` (DI-scoped per consume)
   that `ProcessorPipeline` populates and `SpawnToPost`/`DeleteEntry`/`NewResult`
   read. This keeps the author API identical and removes shared mutable state
   entirely.
2. **Pass the state as explicit parameters** to the helpers
   (`SpawnToPost(result, executionId, in SeamState s)` etc.) so nothing is stored
   on the instance.
3. If neither is feasible short-term, **serialize the dispatch and -post endpoints**
   with `cfg.ConcurrentMessageLimit = 1` in both `ConnectReceiveEndpoint` callbacks
   (`ProcessorStartupOrchestrator.cs:267,284`) AND change the documented author
   registration contract from Singleton to **Scoped** so each consume gets its own
   `BaseProcessor` instance. Option 3 caps throughput to one in-flight dispatch per
   replica and still requires the Scoped switch to be safe — it is a mitigation, not
   the clean fix.

```csharp
// Sketch of option 1 — scoped accessor, author API unchanged:
internal sealed class SeamState { public Guid EntryId; public Guid MessageId; /* … */ public Func<Task> EscalateDelete = () => Task.CompletedTask; /* … */ }
// registered AddScoped<SeamState>(); ProcessorPipeline writes it; BaseProcessor reads it via injected accessor.
```

## Warnings

### WR-01: `SpawnToPost` swallows EVERY exception type on send-exhaust, masking non-transient/programming faults (and an Out-of-loop GetSendEndpoint throw is never retried)

**File:** `src/BaseProcessor.Core/Processing/BaseProcessor.cs:66-78`

**Issue:** Two related concerns on the swallow scope:

1. `RetryLoop.ExecuteAsync` (`RetryLoop.cs:14-21`) catches `catch (Exception ex)`
   — every exception type. So the `if (!sent.Succeeded) _onSpawnDropped?.Invoke(...)`
   swallow at `BaseProcessor.cs:77` absorbs not just transient broker/Redis faults
   but also programming errors thrown from inside the send pipeline
   (`ArgumentException`, `NullReferenceException`, serialization/`NotSupportedException`
   from a bad `DataResult`, etc.). The design intent is "swallow on *send-exhaustion*
   so the scheduler re-fires"; swallowing a deterministic `NotSupportedException`
   means every re-fire also fails the same way and the spawn is silently and
   permanently lost with only a warn+counter. Consider narrowing the swallow to
   transient transport/Redis exception types (matching whatever the other escalation
   sites treat as "infra"), or at minimum logging at a level/with detail that makes a
   non-transient repeat-failure diagnosable (currently only the `executionId` is
   logged — by design for T-70-10, but a deterministic fault then has no fingerprint).

2. `var ep = await _sendProvider!.GetSendEndpoint(...)` at `BaseProcessor.cs:69` is
   **outside** the `RetryLoop`. If `GetSendEndpoint` throws transiently (bus not
   fully started, transport blip), that throw propagates straight out of
   `SpawnToPost` — it is neither retried nor swallowed, so it bubbles past the
   author's `return null` and out of `ProcessAsync` → the whole Pre consume faults →
   broker redelivery. Note the keeper consumers explicitly fixed this exact asymmetry
   (`InjectConsumer.cs:53-55` and `ReinjectConsumer.cs:49-51` wrap `GetSendEndpoint`
   in `Guard` "so a transient GetSendEndpoint failure routes through the bounded
   RetryLoop like every other op"). `SpawnToPost` did not get that treatment, so its
   resilience posture is inconsistent with the keeper path and with the swallow
   contract.

**Fix:** Move `GetSendEndpoint` inside the `RetryLoop` (resolve-then-send as one
retried unit, mirroring the keeper `Guard` pattern), and consider narrowing the
swallowed exception set to transient transport/Redis types so a deterministic
programming/serialization fault is not silently dropped on every re-fire.

```csharp
var sent = await RetryLoop.ExecuteAsync(async () =>
{
    var ep = await _sendProvider!.GetSendEndpoint(new Uri($"queue:{_processorId:D}-post"));
    await ep.Send((object)spawn, ctx => ctx.MessageId = spawn.MessageId, CancellationToken.None);
    return true;
}, _retryLimit, CancellationToken.None);
if (!sent.Succeeded) _onSpawnDropped?.Invoke(spawn.ExecutionId);
```

### WR-02: `PostProcessConsumer` accepts a fully attacker-shaped `DataResult` (incl. arbitrary `MessageId`) and writes `OutputData(messageId)=data` with no provenance check

**File:** `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs:22-31`,
`src/BaseProcessor.Core/Processing/OutputTail.cs:50-75`

**Issue:** The `-post` endpoint deserializes a `DataResult` straight off the wire
and hands it to `OutputTail.RunAsync` with no validation of the carried ids. The
tail then writes `L2[OutputData(dr.MessageId)] = dr.Data` (gated only on
`Result==Completed`, output-schema validated) and sends a `Step*` whose
`WorkflowId/StepId/ProcessorId/CorrelationId/ExecutionId` are **all taken verbatim
from the message body**. Any sender with access to the `{id:D}-post` queue can:
(a) write an arbitrary blob under any `messageId` key it chooses (subject to output
schema), and (b) emit a `StepCompleted/Failed/Cancelled` to the orchestrator result
queue for an arbitrary `(workflowId, stepId, executionId)` it did not own — i.e.
forge a step outcome for another workflow/execution. The output-schema validation at
`OutputTail.cs:58-60` constrains `Data` shape but does nothing about the id fields,
and the `messageId` write key is entirely caller-controlled.

This is the standard "trusted internal bus" posture the rest of the system relies
on, so it may be an accepted trust boundary — but Phase 70 *adds a new ingress*
(`{id:D}-post`) that, unlike the entry dispatch, carries the *output* ids and the
output *write key* directly in the body and performs an L2 write + a result emit
from them. If the bus is not a hard trust boundary, this is a forge/poisoning
vector worth an explicit decision.

**Fix:** If the bus is trusted, document that `-post` is an internal-only endpoint
and that the trust boundary is the broker ACL (as a `// SECURITY:` note next to the
bind at `ProcessorStartupOrchestrator.cs:283-288` and on `PostProcessConsumer`). If
not, constrain the Post path: verify `dr.ProcessorId == context.Id!.Value` before
acting (a -post message for *this* processor must carry this processor's id), and
consider deriving/validating the `messageId` rather than trusting an arbitrary
caller-supplied key.

### WR-03: `ProcessorPipeline` re-deserializes the seam-thrown `JsonException` as a generic `Failed` but the SPEC routes deserialize-failure as an *input* failure — verify the message is not leaking parser internals and the path is the intended "one StepFailed, no delete"

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:105-109`,
`src/BaseProcessor.Core/Processing/BaseProcessor`1.cs:23-26`

**Issue:** The flow is correct on the control-flow axis (a deserialize
`JsonException` thrown by `BaseProcessor<TConfig>.ExecuteAsync` is caught by the
catch-all at `ProcessorPipeline.cs:105`, emits exactly one `StepFailed`, and does
NOT delete `L2[entryId]` — matching SPEC req 2 / C-2). Two sub-points to confirm
rather than outright bugs:

1. `BuildFailed(d, ex.Message)` at `:107` puts the raw exception message into the
   `StepFailed.ErrorMessage` that travels to the orchestrator. For a `JsonException`
   that message can include a fragment of the offending payload / config text
   (path, line, token). T-70-10 elsewhere is careful to *never* log the payload
   (e.g. `ProcessorPipeline.cs:143`, `ReinjectConsumer.cs:39`); emitting
   `ex.Message` here can defeat that intent by carrying payload bytes into the
   result message. Consider a sanitized constant ("config/input deserialization
   failed") for the `JsonException` branch, mirroring the constant
   `"output failed schema validation"` used in `OutputTail`/`InjectConsumer`.

2. The broad `catch (Exception ex)` at `:105` also catches genuinely unexpected
   author exceptions (e.g. an NRE in `ProcessAsync`) and converts them to a clean
   `StepFailed` with no delete and no redelivery. That is a deliberate "any
   unexpected → failed, left to TTL" choice, but it means an author bug is reported
   as a business `Failed` and the entry is silently left to expire rather than
   retried. Confirm that is intended (it diverges from the send-exhaust→redelivery
   posture used everywhere else for infra faults).

**Fix:** For (1), use a sanitized message for the deserialize/unexpected branch:

```csharp
catch (Exception ex)   // unexpected (incl. deserialize JsonException, req 2) ⇒ failed, NO delete
{
    logger.LogWarning(ex, "ProcessAsync/deserialize faulted; emitting StepFailed (entry left to TTL)"); // detail stays in logs
    await SendResult(BuildFailed(d, "processing failed"), limit, ct);   // do NOT put ex.Message (payload bytes) on the wire
    return;
}
```

## Info

### IN-01: `OutputTail.SendResult` / `SendKeeper` resolve `GetSendEndpoint` outside the `RetryLoop` — minor inconsistency with the keeper `Guard` pattern

**File:** `src/BaseProcessor.Core/Processing/OutputTail.cs:108-118,129-135`

**Issue:** Same shape as WR-01.2 but lower impact: `await sendProvider.GetSendEndpoint(...)`
at `:110` and `:131` is outside the `RetryLoop`, so only the `ep.Send` is retried.
A transient `GetSendEndpoint` fault throws out of the tail. On the Pre path that is
acceptable (it becomes broker redelivery, the entry is intact), and on the Post path
it is also acceptable (nack-requeue). The keeper consumers deliberately wrapped
`GetSendEndpoint` in `Guard` for symmetry; `OutputTail` did not. Not a correctness
bug — the throw lands on the intended redelivery path — but the resilience surface is
inconsistent across the three send owners (pipeline/tail/keeper).

**Fix:** For consistency, fold `GetSendEndpoint` into the retried unit as the keeper
consumers do, or add a one-line comment noting the intentional difference.

### IN-02: Comments in `RetryLoop` / `BaseProcessor` reference a retired `_error`/`D-10` posture

**File:** `src/BaseConsole.Core/Resilience/RetryLoop.cs:6` ("send-exhaust →
re-throw (→ bus _error, D-10)")

**Issue:** The Phase-53/Phase-70 end-state is explicitly "no `_error`, no
dead-letter — send-exhaust → broker nack-requeue" (documented at
`ProcessorPipeline.cs:43-45,156-157`, `EntryStepDispatchConsumer.cs:21-23`,
`RecoveryConsumerBase.cs:13-16`). The `RetryLoop` summary still says exhaustion
re-throws "→ bus `_error`, D-10", which is the retired model and contradicts the
authoritative comments elsewhere. Stale doc only — no behavior impact — but it can
mislead a future reader into thinking an `_error` transport exists.

**Fix:** Update the `RetryLoop` XML summary to say "send-exhaust → re-throw → broker
nack-requeue (no `_error`)".

### IN-03: `SpawnToPost` drop telemetry is counted on the `DispatchDeduped` counter, overloading an unrelated metric

**File:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:144-145`

**Issue:** The spawn-drop hook increments `metrics.DispatchDeduped` ("dispatch
deduped") to record a `-post` send-exhaustion swallow. A spawn drop is not a dedup;
reusing this counter conflates two distinct events and will make the
dispatch-dedup rate unreadable and the spawn-drop rate invisible under its real
name. (Similarly `PostProcessConsumer.cs:26` reuses `DispatchConsumed` for the Post
hop — that one is at least documented as intentional.)

**Fix:** Add a dedicated `SpawnDropped` counter to `ProcessorMetrics` and increment
it here, tagged by `ProcessorId`, so the swallow has its own observable signal.

### IN-04: `OutputTail.JitteredTtl` and `InjectConsumer.JitteredTtl` duplicate the same TTL policy in two assemblies

**File:** `src/BaseProcessor.Core/Processing/OutputTail.cs:38-42`,
`src/Keeper/Recovery/InjectConsumer.cs:62-65`

**Issue:** The jittered `random[ttl, 2×ttl]` TTL is computed by two independent copies
(`Random.Shared.Next(ttl, 2*ttl + 1)`), reading two different option types
(`ProcessorLivenessOptions.ExecutionDataTtlSeconds` vs
`RecoveryOptions.ExecutionDataTtlSeconds`, defaulted to 300 in each). The comments
assert they are "identical policy", but nothing enforces it: if one default or
formula is changed, a keeper-completed result silently gets a different lifetime than
a directly-completed one. Pure duplication / drift risk — both copies are correct
today.

**Fix:** Hoist the jitter helper next to `L2ProjectionKeys.OutputData` (the SoT
comment there already claims the TTL is a caller concern) or into a shared
`Messaging.Contracts` helper both writers call, so the policy cannot desynchronize.

---

_Reviewed: 2026-06-17_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
