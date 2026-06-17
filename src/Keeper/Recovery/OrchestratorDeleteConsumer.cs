using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>req 10 (Phase 71): the Orchestrator DELETE state — the orchestrator-side mirror of
/// <see cref="DeleteConsumer"/>. Delete-only: exactly one single-key DEL of <c>L2[out:entryId]</c>
/// (<see cref="L2ProjectionKeys.OutputData"/> — the cross-paired <c>out:</c> namespace, not <c>data:</c>);
/// drop-on-absent (<c>KeyDeleteAsync</c> no-ops on a missing key); NO orchestrator send / NO dispatch. The
/// delete goes through the RetryLoop <see cref="RecoveryConsumerBase{TMessage}.Guard"/> and re-throws on
/// exhaustion to broker nack-requeue (D-04); gating happens at the endpoint (D-04).</summary>
public sealed class OrchestratorDeleteConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions)
    : RecoveryConsumerBase<OrchestratorDelete>(redis, sendProvider, retryOptions)
{
    protected override async Task HandleAsync(OrchestratorDelete m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), ct);
}
