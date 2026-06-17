using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>req 9 (Phase 71): the Orchestrator INJECT state — the orchestrator-side mirror of
/// <see cref="InjectConsumer"/>. NextStepHandoff-driven and self-contained ("never reads input to recompute
/// successors from L1"). The whole <see cref="NextStepHandoff"/> rides in-hand on the envelope, so INJECT
/// performs the relocate tail INLINE in STRICT order (the source <c>out:</c> delete is the tail AFTER the
/// confirmed dispatch so a completed continuation is never lost by deleting the source before the dispatch
/// lands):
/// <list type="number">
///   <item>write <c>L2[data:messageId]=Handoff.Data</c> (<see cref="L2ProjectionKeys.ExecutionData"/>) with
///   the jittered TTL — gated on a non-empty <c>Handoff.Data</c> (a non-completed continuation has no blob to
///   relocate; the cross-paired <c>data:</c> namespace, not <c>out:</c>);</item>
///   <item>dispatch an <see cref="EntryStepDispatch"/> to <c>queue:{ProcessorId:D}</c> with the outbound
///   envelope <c>MessageId</c> overridden to the carried <see cref="OrchestratorInject.MessageId"/> and
///   <c>entryId = (Data non-empty ? messageId : Guid.Empty)</c>; <c>executionId</c> threaded UNCHANGED;</item>
///   <item>delete <c>L2[out:DeleteEntryId]</c> (<see cref="L2ProjectionKeys.OutputData"/> — the source
///   <c>out:</c> entryId; no-op when empty).</item>
/// </list>
/// Every op goes through the RetryLoop <see cref="RecoveryConsumerBase{TMessage}.Guard"/>; gating happens at
/// the endpoint (D-04). CROSS-ASSEMBLY FIREWALL (Pitfall 4): the relocate body is re-implemented INLINE here —
/// the Keeper assembly references Messaging.Contracts, NOT Orchestrator, so it CANNOT call the orchestrator's
/// RelocateTail; only the shared keys/TTL policy in <see cref="L2ProjectionKeys"/> are reused.</summary>
public sealed class OrchestratorInjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions, IOptions<RecoveryOptions> recoveryOptions)
    : RecoveryConsumerBase<OrchestratorInject>(redis, sendProvider, retryOptions)
{
    private readonly int _executionDataTtlSeconds = recoveryOptions.Value.ExecutionDataTtlSeconds;

    protected override async Task HandleAsync(OrchestratorInject m, CancellationToken ct)
    {
        var h = m.Handoff;   // the self-contained relocate handoff (never read input to recompute successors)

        // 1) write L2[data:messageId]=Handoff.Data — gated on a non-empty Data (Completed continuation only).
        //    The cross-paired data: namespace (orchestrator WRITES data:, processor writes out:).
        if (!string.IsNullOrEmpty(h.Data))
            await Guard(() => Db.StringSetAsync(
                L2ProjectionKeys.ExecutionData(m.MessageId), h.Data, JitteredTtl()), ct);

        // 2) dispatch EntryStepDispatch — entryId branches on whether the data: write ran; executionId
        //    threaded UNCHANGED (no executionId regeneration); envelope MessageId overridden to the carried messageId.
        var entryId = string.IsNullOrEmpty(h.Data) ? Guid.Empty : m.MessageId;
        var dispatch = new EntryStepDispatch(h.WorkflowId, h.StepId, h.ProcessorId, h.Payload)
        {
            CorrelationId = h.CorrelationId,
            ExecutionId = h.ExecutionId,
            EntryId = entryId,
        };
        var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{h.ProcessorId:D}")), ct);
        await Guard(() => ep.Send(dispatch, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);

        // 3) delete L2[out:DeleteEntryId] (the source out: entry) AFTER the confirmed dispatch (strict order).
        await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.DeleteEntryId)), ct);
    }

    /// <summary>The L2[data:messageId] write TTL — jittered <c>random[ExecutionDataTtl, 2×ExecutionDataTtl]</c>
    /// via the shared single source of truth <see cref="L2ProjectionKeys.OutputDataTtl"/> (the same policy the
    /// orchestrator RelocateTail makes), so a keeper-recovered relocation carries the same bounded lifetime as
    /// a directly-relocated one; only the floor differs by option (RecoveryOptions here).</summary>
    private TimeSpan JitteredTtl() => L2ProjectionKeys.OutputDataTtl(_executionDataTtlSeconds);
}
