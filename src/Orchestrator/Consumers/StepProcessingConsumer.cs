using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.Observability;

namespace Orchestrator.Consumers;

/// <summary>
/// The <see cref="StepOutcome.Processing"/> arm of the <see cref="TypedResultConsumer{TMessage}"/> family
/// (D-07 / ORCH-01). Advances only successors gated on <c>PreviousProcessing</c> (or <c>Always</c>); the
/// only per-type knob is <see cref="Outcome"/>, no status if/switch.
/// </summary>
public sealed class StepProcessingConsumer(
    OrchestratorPrePipeline pipeline,
    OrchestratorMetrics metrics,
    ILogger<StepProcessing> logger)
    : TypedResultConsumer<StepProcessing>(pipeline, metrics, logger)
{
    protected override StepOutcome Outcome => StepOutcome.Processing;
}
