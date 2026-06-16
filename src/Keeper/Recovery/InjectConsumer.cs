using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>Phase 70 / req 7 (D-13): the Keeper INJECT state — DataResult-driven and self-contained
/// ("never reads input to recompute"). The whole <see cref="DataResult"/> rides in-hand on the envelope,
/// so INJECT performs the tail in STRICT order (Pitfall 5 — the source delete is the tail AFTER the
/// confirmed send so a completed result is never lost by deleting the source before the send lands):
/// <list type="number">
///   <item>write <c>L2[messageId]=data</c> (<see cref="L2ProjectionKeys.OutputData"/>) with the jittered
///   TTL — gated on <c>dr.Result == StepOutcome.Completed</c> (A2; consistent with the Pre/Post inline
///   tail's write-on-completed);</item>
///   <item>send the <c>Step*</c> matching <c>dr.Result</c> to <c>queue:orchestrator-result</c> with the
///   outbound envelope <c>MessageId</c> overridden to the carried <c>dr.MessageId</c> (req 6/11);</item>
///   <item>delete <c>L2[entryId]</c> (the source <see cref="KeeperInject.DeleteEntryId"/>; no-op when empty).</item>
/// </list>
/// Every op goes through the RetryLoop <see cref="RecoveryConsumerBase{TMessage}.Guard"/>; gating happens
/// at the endpoint (D-04). The <c>Step*</c> <c>EntryId</c> is the <c>Guid.Empty</c> placeholder (A1 —
/// output is keyed by messageId; orchestrator entryId↔messageId threading is SPEC-deferred).</summary>
public sealed class InjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
    : RecoveryConsumerBase<KeeperInject>(redis, sendProvider, retryOptions)
{
    private readonly int _executionDataTtlSeconds = recoveryOptions.Value.ExecutionDataTtlSeconds;

    protected override async Task HandleAsync(KeeperInject m, CancellationToken ct)
    {
        var dr = m.DataResult;   // D-13: the whole self-contained result (req 7 — never read input to recompute)

        // 1) write L2[messageId]=data — gated on Completed (A2; consistent with the Pre/Post inline tail).
        if (dr.Result == StepOutcome.Completed)
            await Guard(() => Db.StringSetAsync(
                L2ProjectionKeys.OutputData(dr.MessageId), dr.Data, JitteredTtl()), ct);

        // 2) send Step* by result, envelope MessageId overridden to the carried messageId (req 6/11).
        IStepResult step = dr.Result switch
        {
            StepOutcome.Completed => new StepCompleted(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = Guid.Empty },
            StepOutcome.Failed => new StepFailed(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = Guid.Empty, ErrorMessage = "output failed schema validation" },
            StepOutcome.Cancelled => new StepCancelled(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = Guid.Empty, CancellationMessage = "" },
            _ => new StepProcessing(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId },
        };
        // IN-01: resolve the send endpoint through Guard too, so a transient GetSendEndpoint failure
        // (e.g. bus not yet fully started) routes through the bounded RetryLoop like every other op.
        var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
        await Guard(() => ep.Send((object)step, ctx => ctx.MessageId = dr.MessageId, CancellationToken.None), ct);

        // 3) delete L2[entryId] (the source DeleteEntryId) — AFTER the confirmed send (Pitfall 5 order).
        await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.DeleteEntryId)), ct);
    }

    /// <summary>Phase 70 (req 11): the L2[messageId] output-blob TTL — jittered
    /// <c>random[ExecutionDataTtl, 2×ExecutionDataTtl]</c> (identical policy to the Pre/Post inline tail).</summary>
    private TimeSpan JitteredTtl()
        => TimeSpan.FromSeconds(Random.Shared.Next(_executionDataTtlSeconds, 2 * _executionDataTtlSeconds + 1));
}
