using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.Observability;

namespace Orchestrator.Consumers;

/// <summary>
/// Generic per-item result-advancement base (ORCH-01 / D-07). Phase 71 (D-06): reshaped into a THIN SHELL
/// that delegates to <see cref="OrchestratorPrePipeline"/> — the orchestrator-side mirror of the processor's
/// Phase-70 reshape (the straight-through <c>EntryStepDispatchConsumer</c> became a thin shell over
/// <c>ProcessorPipeline</c>). It consumes ONE typed <see cref="IStepResult"/>
/// (<see cref="StepCompleted"/>/<see cref="StepFailed"/>/<see cref="StepCancelled"/>/<see cref="StepProcessing"/>)
/// off the shared competing-consumer queue <c>orchestrator-result</c>, increments the consumed counter, and
/// hands the result + its per-type <see cref="Outcome"/> + the inbound envelope <c>MessageId</c> to the
/// pipeline, which owns the L1 resolution, the <c>out:</c> gate/read, the fan-out to
/// <c>orchestrator-result-post</c>, and the two-reason trip-end.
/// <para>
/// <b>The ONLY per-type knob is <see cref="Outcome"/> (D-07):</b> routing is by message type via the
/// abstract <see cref="StepOutcome"/> each sealed subclass returns — there is deliberately NO status
/// <c>if</c>/<c>switch</c> anywhere. A Keeper-INJECT'd <see cref="StepCompleted"/> is byte-indistinguishable
/// from a direct processor completion — both are the same record, land on the same queue, and are processed
/// by the same <see cref="StepCompletedConsumer"/>.
/// </para>
/// <para>
/// <b>No-silent-loss + business-ack vs infra-throw split now live in the pipeline:</b> an unresolved L1 entry
/// logs a distinct <c>completed-unresolved</c> trip-end line + acks; a terminal step logs a distinct
/// <c>completed-terminal</c> line + acks (REQ-71-02 — the trip-end metric counter is DEFERRED, the distinct
/// logs + behavior carry no-silent-loss). A Redis fault on the <c>out:</c> read escalates one REINJECT; a
/// fan-out send-exhaust throws → bounded retry → broker redelivery.
/// </para>
/// </summary>
/// <typeparam name="TMessage">The typed step-result record this consumer advances on.</typeparam>
public abstract class TypedResultConsumer<TMessage>(
    OrchestratorPrePipeline pipeline,
    OrchestratorMetrics metrics,
    ILogger<TMessage> logger) : IConsumer<TMessage>
    where TMessage : class, IStepResult
{
    /// <summary>The ONLY per-type knob (D-07): the outcome the pipeline's <see cref="StepAdvancement.SelectNext"/>
    /// matches successors against. No status if/switch lives anywhere — each sealed subclass returns its
    /// compile-time constant here.</summary>
    protected abstract StepOutcome Outcome { get; }

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        var m = context.Message;
        _ = logger;   // reserved — the pipeline owns all trip-end / drop logging (ids-only, T-70-10).

        // METRIC-04: count EVERY consumed result at the TOP, BEFORE the pipeline runs, so the graceful
        // trip-end acks inside the pipeline are ALSO counted. Tagged ProcessorId only (cardinality); ambient sid.
        metrics.ResultConsumed.Add(1, new KeyValuePair<string, object?>("ProcessorId", m.ProcessorId.ToString("D")));

        // Delegate to the shared Pre pipeline: L1 resolution -> two-reason trip-end / out: gate-read ->
        // fan-out to orchestrator-result-post -> delete out:. The inbound envelope MessageId is threaded so a
        // REINJECT re-asserts the SAME id. A fan-out send-exhaust propagates → broker redelivery.
        await pipeline.RunAsync(m, Outcome, context.MessageId ?? Guid.Empty, context.CancellationToken);
    }
}
