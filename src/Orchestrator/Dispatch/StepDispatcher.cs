using MassTransit;
using Messaging.Contracts;
using Orchestrator.Observability;

namespace Orchestrator.Dispatch;

/// <summary>
/// The sole implementation of <see cref="IStepDispatcher"/> (D-01) — the verbatim build-and-Send
/// block extracted from <c>WorkflowFireJob</c>. <c>Send</c> (NOT <c>Publish</c>, D-10) to the
/// per-processor queue <c>queue:{processorId:D}</c>; an infra fault on <c>Send</c> still THROWS
/// (unchanged). How that throw is handled is the CALLER's concern, and the two callers differ: the
/// RelocateTail / result-continuation caller relies on the throw → nack → broker redelivery, while the
/// <c>WorkflowFireJob</c> fire caller CATCHES it (log + continue + self-reschedule) to keep its Quartz
/// schedule chain alive (QUICK-260726-f7x). This dispatcher itself adds no retry and no catch.
/// <para>
/// Phase 43 (D-03): the retired <c>H</c>/flag effect-first dedup machinery is gone. The dispatch is
/// built straight-through with a <c>Guid</c> <c>entryId</c> (the L2 data key; <c>Guid.Empty</c> = the
/// source sentinel) — NO deterministic-identity compute, NO <c>flag[H]="Pending"</c> pre-write, and the
/// consumer no longer depends on Redis. At-least-once: duplicates are tolerated downstream by design.
/// </para>
/// </summary>
public sealed class StepDispatcher(
    ISendEndpointProvider sendProvider,
    OrchestratorMetrics metrics) : IStepDispatcher
{
    /// <inheritdoc />
    public async Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
        Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
    {
        var msg = new EntryStepDispatch(workflowId, stepId, processorId, payload)
        {
            CorrelationId = correlationId,
            ExecutionId = executionId,
            EntryId = entryId,
        };

        // D-10: Send (NOT Publish) to the per-processor queue. An infra fault here THROWS (unchanged) —
        // the result-continuation caller lets it propagate → nack → redelivery; the WorkflowFireJob fire
        // caller catches it (log + reschedule) to preserve its schedule chain (QUICK-260726-f7x).
        var endpoint = await sendProvider.GetSendEndpoint(new Uri($"queue:{processorId:D}"));
        await endpoint.Send(msg, ct);

        // Phase 74 (REQ-1/D-02): SENT = count-AFTER-Send — an infra throw on Send correctly skips this
        // increment (a failed/exhausted send must not count). Tagged camelCase workflowId+processorId (D-07);
        // messageId is never a label (D-04). D-05: processorId here = the dispatch recipient = the target
        // queue's processor (this `processorId` method param). Both label values are in-hand as method params.
        // The "D" format mirrors the queue:{processorId:D} naming. service_instance_id is ambient. This single
        // site covers BOTH the orchestrator's direct forward dispatch AND RelocateTail's post-process dispatch
        // (both call DispatchAsync) — do NOT add a second dispatch increment in RelocateTail (no double-count).
        metrics.MessagesSent.Add(1,
            new KeyValuePair<string, object?>("workflowId", workflowId.ToString("D")),
            new KeyValuePair<string, object?>("processorId", processorId.ToString("D")));
    }
}
