using MassTransit;
using Messaging.Contracts;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="OrchestratorPostProcessConsumer"/> (Phase 71, REQ-71-11). Binds the
/// STABLE single competing-consumer queue <see cref="OrchestratorQueues.ResultPost"/>
/// (<c>"orchestrator-result-post"</c>) — the Pre fan-out target. Mirrors
/// <see cref="StepCompletedConsumerDefinition"/>'s posture exactly: an INTENTIONAL no-op that registers NO
/// bus retry. A send that exhausts the in-code RetryLoop throws → RabbitMQ nack-requeue (broker redelivery);
/// there is no <c>_error</c> and no dead-letter (Phase-53 D-01).
/// </summary>
public sealed class OrchestratorPostProcessConsumerDefinition : ConsumerDefinition<OrchestratorPostProcessConsumer>
{
    public OrchestratorPostProcessConsumerDefinition()
    {
        EndpointName = OrchestratorQueues.ResultPost;   // "orchestrator-result-post"
    }

    // Intentional no-op: no bus retry (Phase-53 D-01 / REQ-71-11) — send-exhaust throws → broker redelivery.
}
