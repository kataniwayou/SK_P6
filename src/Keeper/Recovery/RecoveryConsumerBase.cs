using BaseConsole.Core.Resilience;
using Keeper.Observability;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>D-02: shared base for the three Keeper recovery-state consumers (REINJECT/INJECT/DELETE).
/// Owns the one surviving cross-cutting concern so each subclass body is just its distinct state op:
/// <list type="number">
///   <item>D-04 RetryLoop — the <see cref="Guard{T}"/>/<see cref="Guard"/> helpers wrap each L2 op + Send
///   in the relocated <see cref="RetryLoop"/> and re-throw <c>.Error</c> on exhaustion. The endpoint carries
///   NO bus retry / NO ConfigureError (symmetric with the exec path), so a give-up throw falls out of
///   <c>Consume</c> to RabbitMQ nack-requeue (broker redelivery) — no dead-letter, no skp-dlq-1.</item>
/// </list>
/// Phase 52 (D-04/D-09): the bounded once-at-entry gate-wait was REMOVED — gating now happens at the
/// keeper-recovery ENDPOINT (pause/resume driven by the BIT loop), so <c>Consume</c> dispatches straight
/// to <see cref="HandleAsync"/>. <see cref="IConnectionMultiplexer"/> is the SAME DI singleton AddBaseConsole
/// already registers (mirrors <c>L2ProbeRecovery</c>) — do NOT register a new one.</summary>
public abstract class RecoveryConsumerBase<TMessage>(
    IConnectionMultiplexer redis,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    KeeperMetrics metrics) : IConsumer<TMessage>
    where TMessage : class, IKeeperRecoverable
{
    // IN-02: resolve the logical database once per consumer lifetime (redis is a DI singleton, so the
    // GetDatabase() wrapper is stable) rather than re-invoking GetDatabase() on every property access.
    protected IDatabase Db { get; } = redis.GetDatabase();
    protected ISendEndpointProvider Send => sendProvider;
    protected int RetryLimit => retryOptions.Value.Limit;

    public Task Consume(ConsumeContext<TMessage> context)
    {
        var m = context.Message;
        // D-08 (REQ-3): count keeper_messages_consumed ONCE here — the single choke point all six recovery
        // consumers funnel through (REINJECT/INJECT/DELETE for both processor and orchestrator). Labels are
        // the camelCase workflowId+processorId from IKeeperRecoverable.
        metrics.MessagesConsumed.Add(1,
            new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")),
            new KeyValuePair<string, object?>("processorId", m.ProcessorId.ToString("D")));
        return HandleAsync(m, context.CancellationToken);   // gate now enforced at the ENDPOINT (D-04)
    }

    /// <summary>The per-state body (REINJECT/INJECT/DELETE). Every L2 op + Send inside it should go
    /// through <see cref="Guard"/>/<see cref="Guard{T}"/>.</summary>
    protected abstract Task HandleAsync(TMessage m, CancellationToken ct);

    /// <summary>D-04: run <paramref name="op"/> through the bounded <see cref="RetryLoop"/> and re-throw the
    /// surfaced exhaustion error so the give-up falls out of <c>Consume</c> to broker nack-requeue.</summary>
    protected async Task<T> Guard<T>(Func<Task<T>> op, CancellationToken ct)
    {
        var outcome = await RetryLoop.ExecuteAsync(op, RetryLimit, ct);
        if (!outcome.Succeeded) throw outcome.Error!;
        return outcome.Value!;
    }

    /// <summary>Void-op overload of <see cref="Guard{T}"/> for Sends and deletes whose result is ignored.</summary>
    protected Task Guard(Func<Task> op, CancellationToken ct)
        => Guard(async () => { await op(); return true; }, ct);

    /// <summary>D-09 (REQ-3): the shared <c>keeper_messages_sent</c> increment. Each of the four SENDING
    /// consumers calls this AFTER a successful <c>ep.Send</c> (never on the Reinject drop/early-return path —
    /// a dropped or exhausted send does not count, T-74-06); the two Delete consumers send nothing and never
    /// call it. Centralizes the camelCase <c>workflowId</c>+<c>processorId</c> label construction next to the
    /// consumed-counter increment (DRY).</summary>
    protected void CountSent(Guid workflowId, Guid processorId) =>
        metrics.MessagesSent.Add(1,
            new KeyValuePair<string, object?>("workflowId", workflowId.ToString("D")),
            new KeyValuePair<string, object?>("processorId", processorId.ToString("D")));
}
