using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.Observability;

namespace Orchestrator.Consumers;

/// <summary>
/// The <see cref="StepOutcome.Cancelled"/> arm of the <see cref="TypedResultConsumer{TMessage}"/> family
/// (D-07 / ORCH-01). Advances only successors gated on <c>PreviousCancelled</c> (or <c>Always</c>); the
/// only per-type knob is <see cref="Outcome"/>, no status if/switch.
/// </summary>
public sealed class StepCancelledConsumer(
    OrchestratorPrePipeline pipeline,
    OrchestratorMetrics metrics,
    ILogger<StepCancelled> logger)
    : TypedResultConsumer<StepCancelled>(pipeline, metrics, logger)
{
    protected override StepOutcome Outcome => StepOutcome.Cancelled;
}
