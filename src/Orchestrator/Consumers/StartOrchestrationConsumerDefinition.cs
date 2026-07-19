using MassTransit;

namespace Orchestrator.Consumers;

/// <summary>
/// Endpoint config seam for <see cref="StartOrchestrationConsumer"/>
/// (MSG-ACK-03/04). The endpoint name is now supplied PER-INSTANCE by <c>Program.cs</c> via
/// <see cref="Orchestrator.Messaging.OrchestratorFanoutEndpoints"/> (<c>orchestrator-{instanceId}</c>,
/// SAME name as the Stop definition → co-located on one per-replica fan-out endpoint). The literal
/// <c>EndpointName</c> was removed: in MassTransit 8.5.5 it bypassed the InstanceId formatter, so the
/// exclusive/<c>Temporary</c> queues collided on <c>RESOURCE_LOCKED</c> at replicas&gt;1 (HA-07 blocker).
/// <para>
/// <b>Phase-53 D-01:</b> NO bus retry. A send that exhausts the in-code RetryLoop throws →
/// RabbitMQ nack-requeue (broker redelivery); there is no <c>_error</c> and no dead-letter on this
/// endpoint. <c>WorkflowRootNotFoundException</c> is never thrown (D-07 — the dead
/// <c>Ignore&lt;WorkflowRootNotFoundException&gt;</c> guarded a throw that does not exist and was
/// removed with the retry block; <c>WorkflowLifecycle</c> handles an absent root as a logged
/// no-op/ACK).
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumerDefinition : ConsumerDefinition<StartOrchestrationConsumer>
{
    // Endpoint name supplied per-instance by Program.cs (OrchestratorFanoutEndpoints) — NO literal
    // EndpointName (the literal bypassed the InstanceId formatter → RESOURCE_LOCKED at replicas>1, HA-07).
    // Intentional no-op: no bus retry (Phase-53 D-01) — send-exhaust throws → broker redelivery.
}
