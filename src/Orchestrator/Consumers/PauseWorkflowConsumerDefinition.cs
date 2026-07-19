using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="PauseWorkflowConsumer"/> (PAUSE-04 / D-07). The endpoint name
/// is now supplied PER-INSTANCE by <c>Program.cs</c> via
/// <see cref="Orchestrator.Messaging.OrchestratorFanoutEndpoints"/>
/// (<c>orchestrator-pauseresume-{instanceId}</c>, NOT "orchestrator") so Pause+Resume own their own
/// <c>ConcurrentMessageLimit</c> and do not throttle Start/Stop/Result (RESEARCH §5b / A3). The literal
/// <c>EndpointName</c> was removed: in MassTransit 8.5.5 it bypassed the InstanceId formatter, so the
/// exclusive/<c>Temporary</c> queues collided on <c>RESOURCE_LOCKED</c> at replicas&gt;1 (HA-07 blocker).
/// <c>ConcurrentMessageLimit = 1</c> serializes delivery so a duplicate Pause/Resume replays against
/// idempotent Quartz transitions — NO lock, NO stripe (D-07).
/// <para>
/// <b>Phase-53 D-01:</b> this endpoint registers NO bus retry. A send that exhausts the in-code
/// RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); no <c>_error</c>, no dead-letter.
/// <c>ConcurrentMessageLimit = 1</c> is serialization, orthogonal to retry, and is KEPT.
/// </para>
/// </summary>
public sealed class PauseWorkflowConsumerDefinition : ConsumerDefinition<PauseWorkflowConsumer>
{
    // Endpoint name supplied per-instance by Program.cs (OrchestratorFanoutEndpoints.PauseResumeBase) —
    // NO literal EndpointName (it bypassed the InstanceId formatter → RESOURCE_LOCKED at replicas>1, HA-07).

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PauseWorkflowConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;   // D-07 serial — no lock/stripe (retry removed, Phase-53 D-01)
    }
}
