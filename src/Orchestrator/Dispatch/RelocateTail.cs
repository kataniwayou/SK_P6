using BaseConsole.Core.Resilience;       // RetryLoop
using MassTransit;                        // ISendEndpointProvider
using Messaging.Contracts;
using Messaging.Contracts.Configuration;  // RetryOptions
using Messaging.Contracts.Projections;    // L2ProjectionKeys
using Microsoft.Extensions.Options;
using Orchestrator.Configuration;          // OrchestratorOutputOptions
using Orchestrator.Observability;          // OrchestratorMetrics (MessagesSent counter)
using StackExchange.Redis;

namespace Orchestrator.Dispatch;

/// <summary>
/// D-15 (Phase 71): the orchestrator-side mirror of the processor <c>OutputTail</c> — the shared
/// write-<c>data:</c>-then-dispatch tail consumed by <see cref="Orchestrator.Consumers.OrchestratorPostProcessConsumer"/>.
/// The namespaces cross-pair: the processor's <c>OutputTail</c> writes <c>out:</c> and sends a Step* result;
/// this <c>RelocateTail</c> writes <c>data:</c> (the relocated input the NEXT step reads) and DISPATCHES an
/// <see cref="EntryStepDispatch"/>.
/// <para>
/// Flow (req 6 / OQ-2): when the handoff carries a non-empty <see cref="NextStepHandoff.Data"/> (a Completed
/// continuation), write <c>L2[data:messageId]=Data</c> with the jittered TTL in a bounded
/// <see cref="RetryLoop"/>; a write-exhaust escalates exactly one <see cref="OrchestratorInject"/> keeper
/// message and does NOT dispatch. Then dispatch with <c>entryId = messageId</c> (Completed) or
/// <c>Guid.Empty</c> (non-completed — no <c>data:</c> blob was written). <see cref="NextStepHandoff.ExecutionId"/>
/// is threaded to the dispatch byte-unchanged (D-13 — no execution-id regeneration). The dispatch's own
/// send-exhaust throws INSIDE <see cref="StepDispatcher.DispatchAsync"/> → broker redelivery (no <c>_error</c>).
/// </para>
/// </summary>
public sealed class RelocateTail(
    IConnectionMultiplexer redis,
    IStepDispatcher dispatcher,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    IOptions<OrchestratorOutputOptions> options,
    OrchestratorMetrics metrics)
{
    /// <summary>D-17: the jittered <c>random[OutputDataTtl, 2×OutputDataTtl]</c> TTL on the <c>data:</c>
    /// write — the policy is the single source of truth in <see cref="L2ProjectionKeys.OutputDataTtl"/>;
    /// this only supplies the floor from options.</summary>
    private TimeSpan JitteredTtl()
        => L2ProjectionKeys.OutputDataTtl(options.Value.OutputDataTtlSeconds);

    /// <summary>Write <c>data:messageId</c> on a Completed continuation (write-exhaust → INJECT, no
    /// dispatch) → dispatch <see cref="EntryStepDispatch"/> with <c>entryId = messageId</c> (Completed) or
    /// <c>Guid.Empty</c> (non-completed). <paramref name="messageId"/> is the inbound envelope id (D-11) —
    /// the <c>data:</c> write key AND the dispatched <c>entryId</c> the next processor reads off.</summary>
    public async Task RunAsync(NextStepHandoff h, Guid messageId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;

        // D-13: only a Completed continuation carries a relocated out: blob → write it to data:messageId.
        // A non-completed continuation has Data == "" → skip the write (no blob to relocate).
        if (!string.IsNullOrEmpty(h.Data))
        {
            var write = await RetryLoop.ExecuteAsync(
                () => db.StringSetAsync(L2ProjectionKeys.ExecutionData(messageId), h.Data, JitteredTtl()), limit, ct);
            if (!write.Succeeded)
            {
                await SendKeeper(BuildInject(h, messageId), limit, ct);   // INJECT + return (NO dispatch)
                return;
            }
        }

        // req 6 / OQ-2: entryId = messageId iff the write ran (Completed), else the source sentinel Guid.Empty.
        var entryId = string.IsNullOrEmpty(h.Data) ? Guid.Empty : messageId;

        // D-13: h.ExecutionId threaded UNCHANGED. The dispatch's inner send-exhaust throws inside
        // DispatchAsync → broker redelivery (no _error).
        await dispatcher.DispatchAsync(h.WorkflowId, h.StepId, h.ProcessorId, h.Payload,
            h.CorrelationId, h.ExecutionId, entryId, ct);
    }

    /// <summary>D-02: the INJECT carries the whole <see cref="NextStepHandoff"/> (the keeper re-runs
    /// write-data:+dispatch from it) + the in-body <see cref="OrchestratorInject.MessageId"/>. The Post path
    /// has no source <c>out:</c> entry to delete, so <see cref="OrchestratorInject.DeleteEntryId"/> is
    /// <see cref="Guid.Empty"/> (the keeper's delete no-ops on it); the Pre path's keeper INJECT carries the
    /// real DeleteEntryId.</summary>
    private static OrchestratorInject BuildInject(NextStepHandoff h, Guid messageId) =>
        new(h.WorkflowId, h.StepId, h.ProcessorId)
        {
            CorrelationId = h.CorrelationId,
            ExecutionId   = h.ExecutionId,
            Handoff       = h,
            MessageId     = messageId,
            DeleteEntryId = Guid.Empty,
        };

    /// <summary>Mirror <c>OutputTail.SendKeeper</c>: resolve <c>queue:{KeeperQueues.Recovery}</c> inside the
    /// RetryLoop (Guard parity), send-exhaust throws → broker redelivery.</summary>
    private async Task SendKeeper(IKeeperRecoverable msg, int limit, CancellationToken ct)
    {
        var sent = await RetryLoop.ExecuteAsync(async () =>
        {
            var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));
            await ep.Send((object)msg, CancellationToken.None);
            return true;
        }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery

        // Phase 74 (REQ-1/D-02): count-AFTER-success — the INJECT write-exhaust keeper escalation. D-06: the
        // keeper-recovery recipient is not a processor → use the producing processor's id off the
        // IKeeperRecoverable `msg` (== the BuildInject source's WorkflowId/ProcessorId). camelCase (D-07).
        metrics.MessagesSent.Add(1,
            new KeyValuePair<string, object?>("workflowId", msg.WorkflowId.ToString("D")),
            new KeyValuePair<string, object?>("processorId", msg.ProcessorId.ToString("D")));
    }
}
