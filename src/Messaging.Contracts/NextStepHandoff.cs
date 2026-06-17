namespace Messaging.Contracts;

// bus envelope -- NO [JsonPropertyName], default STJ serialization (matches DataResult/Step*/Keeper* convention).
/// <summary>D-10: the Pre-&gt;Post wire contract -- shaped for the NEXT step (not the completed one),
/// carrying the next step's target ids + author <see cref="Payload"/> + the relocated input
/// <see cref="Data"/>. Distinct from <see cref="DataResult"/> (which carries the COMPLETED step's
/// ids + Result). The orchestrator Pre emits one of these per fan-out target; the Post consumer (and
/// the keeper INJECT) relocate <see cref="Data"/> into L2[data:messageId] and dispatch.
/// <para>
/// D-11: there is NO message-id body field -- the message-id rides the bus envelope (MassTransit assigns a
/// fresh envelope id on the Pre-&gt;Post send = the next step's message-id). D-13: <see cref="ExecutionId"/>
/// is threaded UNCHANGED from the inbound result. D-12: <see cref="Data"/> is the relocated input JSON,
/// carried INLINE; it is "" for a non-completed continuation (which has no out: blob to relocate).
/// </para></summary>
public sealed record NextStepHandoff(Guid WorkflowId, Guid StepId, Guid ProcessorId, string Payload)
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }   // D-13: threaded UNCHANGED from the inbound result
    public string Data        { get; init; } = "";   // D-12: relocated input JSON, INLINE; "" for non-completed
}
