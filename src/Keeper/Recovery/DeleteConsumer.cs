using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>Phase 70 / req 8 (D-12): the Keeper DELETE state — delete-only: a single-key DEL of
/// <c>L2[entryId]</c> (<see cref="L2ProjectionKeys.ExecutionData"/>); drop-on-absent
/// (<c>KeyDeleteAsync</c> no-ops on a missing key); NO orchestrator send. The slot/index model is
/// retired (the both-key DEL + the second slot-index operand are gone — D-11). The delete goes through the
/// RetryLoop Guard and re-throws on exhaustion to broker nack-requeue (D-04); gating happens at the
/// endpoint (D-04).</summary>
public sealed class DeleteConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions)
    : RecoveryConsumerBase<KeeperDelete>(redis, sendProvider, retryOptions)
{
    protected override async Task HandleAsync(KeeperDelete m, CancellationToken ct)
        => await Guard(() => Db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(m.EntryId)), ct);
}
