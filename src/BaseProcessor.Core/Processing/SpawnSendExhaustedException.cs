namespace BaseProcessor.Core.Processing;

/// <summary>PB-01/D-01: a Mode-2 <c>SpawnToPost</c> send exhausted its bounded <c>RetryLoop</c> on a
/// TRANSIENT transport fault. Thrown (not swallowed) so it propagates through the author's
/// <c>ProcessAsync</c> to the <c>ProcessorPipeline</c> catch-all, which (Plan 02) rethrows it →
/// MassTransit default RabbitMQ nack-requeue (broker redelivery — no dead-letter, no <c>_error</c>).
/// Carries the spawn <see cref="ExecutionId"/> ONLY (FW-03 / T-70-10: never a <c>DataResult</c>/
/// <c>Data</c>/<c>Payload</c>) plus the inner transient transport exception. NOT a
/// <see cref="ProcessStatusException"/> — that family is caught at <c>ProcessorPipeline</c> and routed to
/// <c>OutputTail</c>+ack (the exact opposite of the intended nack); this exception must ESCAPE the pipeline.</summary>
public sealed class SpawnSendExhaustedException(Guid executionId, Exception inner)
    : Exception($"Mode-2 spawn send exhausted for execution {executionId:D}.", inner)
{
    /// <summary>The minted spawn execution id (ids-only telemetry — never a payload).</summary>
    public Guid ExecutionId { get; } = executionId;
}
