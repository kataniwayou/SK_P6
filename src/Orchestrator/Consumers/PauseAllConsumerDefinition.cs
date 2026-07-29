using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="PauseAllConsumer"/> (ORCH-02 / D-08). The endpoint name is now
/// supplied PER-INSTANCE by <c>Program.cs</c> via
/// <see cref="Orchestrator.Messaging.OrchestratorFanoutEndpoints"/>
/// (<c>orchestrator-global-pauseresume-{instanceId}</c>) — deliberately kept independent from the old
/// per-workflow endpoint (D-08), which has since been REMOVED along with its publisher-less control pair.
/// The literal <c>EndpointName</c> was removed: in MassTransit 8.5.5 it bypassed the InstanceId
/// formatter, so the exclusive/<c>Temporary</c> queues collided on <c>RESOURCE_LOCKED</c> at
/// replicas&gt;1 (HA-07 blocker). <c>ConcurrentMessageLimit = 1</c> serializes Pause/Resume on this
/// replica so a duplicate replays against idempotent Quartz transitions — NO lock, NO stripe.
/// <para>
/// <b>Phase-53 D-01:</b> this endpoint registers NO bus retry. A send that exhausts the in-code
/// RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); no <c>_error</c>, no dead-letter.
/// <c>ConcurrentMessageLimit = 1</c> is serialization, orthogonal to retry, and is KEPT.
/// </para>
/// </summary>
public sealed class PauseAllConsumerDefinition : ConsumerDefinition<PauseAllConsumer>
{
    // Endpoint name supplied per-instance by Program.cs (OrchestratorFanoutEndpoints.GlobalPauseResumeBase)
    // — NO literal EndpointName (it bypassed the InstanceId formatter → RESOURCE_LOCKED at replicas>1, HA-07).

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<PauseAllConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1;   // serialize Pause/Resume on this replica (retry removed, Phase-53 D-01)
    }
}
