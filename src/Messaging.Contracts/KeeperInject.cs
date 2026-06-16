namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-12/D-13: Keeper INJECT state — embeds the whole self-contained <see cref="Messaging.Contracts.DataResult"/>
/// (write L2[messageId]=data from it, send Step* by its result) plus <see cref="DeleteEntryId"/> (the source
/// entryId to delete). "Never recomputes from input" (req 7) falls out — the data is in-hand on the envelope.
/// CorrelationId/ExecutionId stay on the message for the 4-tuple partition key (sourced from the embedded
/// DataResult's ids for consistency). StepId rides as a record property but is NOT on the partition marker (D-12).</summary>
public sealed record KeeperInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public DataResult DataResult { get; init; } = null!;   // D-13: the whole self-contained result, in-hand
    public Guid DeleteEntryId { get; init; }   // source entryId deleted after the send
}
