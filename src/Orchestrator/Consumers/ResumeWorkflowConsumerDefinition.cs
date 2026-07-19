using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="ResumeWorkflowConsumer"/> (PAUSE-04 / D-07). Co-located on the
/// SAME per-instance fan-out endpoint as <see cref="PauseWorkflowConsumerDefinition"/> —
/// <c>orchestrator-pauseresume-{instanceId}</c>, now supplied by <c>Program.cs</c> via
/// <see cref="Orchestrator.Messaging.OrchestratorFanoutEndpoints"/> (the literal <c>EndpointName</c>
/// was removed: in MassTransit 8.5.5 it bypassed the InstanceId formatter → <c>RESOURCE_LOCKED</c> at
/// replicas&gt;1, HA-07) — and sets <c>ConcurrentMessageLimit = 1</c> (serial — NO lock, NO stripe).
/// This endpoint registers NO bus retry (Phase-53 D-01): a send that exhausts the in-code RetryLoop
/// throws → RabbitMQ nack-requeue (broker redelivery), no <c>_error</c>, no dead-letter. No
/// <c>IOptions&lt;RetryOptions&gt;</c> needed here (parameterless ctor).
/// </summary>
public sealed class ResumeWorkflowConsumerDefinition : ConsumerDefinition<ResumeWorkflowConsumer>
{
    // Endpoint name supplied per-instance by Program.cs (OrchestratorFanoutEndpoints.PauseResumeBase,
    // SAME name as the Pause def → co-located) — NO literal EndpointName (it bypassed the InstanceId
    // formatter → RESOURCE_LOCKED at replicas>1, HA-07).

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<ResumeWorkflowConsumer> consumerConfigurator,
        IRegistrationContext context)
    {
        consumerConfigurator.ConcurrentMessageLimit = 1; // D-07 serial; no bus retry on this endpoint (Phase-53 D-01)
    }
}
