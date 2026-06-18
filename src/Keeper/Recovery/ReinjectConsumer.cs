using Keeper.Observability;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-01: the Keeper REINJECT state — reads L2[entryId] to confirm the recovered input data is
/// still present, then re-injects a reconstructed <see cref="EntryStepDispatch"/> (carrying the D-01
/// <see cref="KeeperReinject.Payload"/> step config) to <c>queue:{ProcessorId:D}</c> — the same target a
/// direct dispatch uses. Phase 52 (D-06/D-07): an absent/empty L2[entryId] (STRLEN==0, NO Redis exception)
/// is now a BY-DESIGN silent drop — ack with no throw and no send, emitting a structured warning
/// (A18 "accepted silent losses": the data is genuinely gone, so a replay can't proceed and nothing
/// downstream is lost). Phase 74 (REQ-3): the legacy reinject-drop counter is removed —
/// the only counter on the success path is the shared <see cref="RecoveryConsumerBase{TMessage}.CountSent"/>
/// (<c>keeper_messages_sent</c>), called after the confirmed send and NEVER on this drop branch. A Redis
/// EXCEPTION on the read is still infra → <see cref="RecoveryConsumerBase{TMessage}.Guard"/> → exhaustion
/// policy (D-01), NOT swallowed as a drop. IN-04: STRLEN (not StringGet) returns 0 for BOTH a missing key
/// AND an empty value without pulling the blob.</summary>
public sealed class ReinjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    KeeperMetrics metrics, ILogger<ReinjectConsumer> logger)
    : RecoveryConsumerBase<KeeperReinject>(redis, sendProvider, retryOptions, metrics)
{
    protected override async Task HandleAsync(KeeperReinject m, CancellationToken ct)
    {
        // Guard the READ so a Redis EXCEPTION still routes to the exhaustion policy; absent/empty
        // (STRLEN==0, no exception) is the by-design drop. IN-04: STRLEN, not StringGet — 0 covers
        // a missing key AND an empty value (KeyExists would be WRONG: an empty-string key EXISTS).
        var present = await Guard(
            () => Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)),
            ct) != 0;
        if (!present)
        {
            // Phase 74 (REQ-3): the legacy reinject-drop counter is REMOVED; the by-design-drop
            // structured warning survives (never log the Payload). No keeper_messages_sent here — a drop
            // never sends, so CountSent must NEVER fire on this early-return path (D-02 / T-74-06).
            logger.LogWarning("REINJECT drop: L2 data gone EntryId={EntryId}", m.EntryId);   // structured hole (never log Payload)
            return;                                                                          // D-06 silent ack
        }

        var dispatch = new EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId = m.ExecutionId,
            EntryId = m.EntryId,
        };
        // IN-01: resolve the send endpoint through Guard too, so a transient GetSendEndpoint failure
        // routes through the bounded RetryLoop like every other op.
        var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}")), ct);
        // IN-01: the inner broker Send uses CancellationToken.None to match ProcessorPipeline's send
        // convention ("do not abort a broker send once started"). The outer Guard keeps ct so the
        // bounded RetryLoop still observes bus shutdown between attempts.
        // req 6 (Phase 70): re-inject with the SAME messageId on the outbound envelope — override
        // SendContext.MessageId to the carried m.MessageId (precedent: OutboundCorrelationSendFilter
        // sets SendContext.CorrelationId). No inbox/dedup on this endpoint, so the reused id is safe.
        await Guard(() => ep.Send(dispatch, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);
        // REQ-3 / D-09: count keeper_messages_sent AFTER the confirmed send (Guard re-throws on exhaustion, so
        // a failed/exhausted send never reaches here — T-74-06). NEVER on the absent-data drop branch above.
        CountSent(m.WorkflowId, m.ProcessorId);
    }
}
