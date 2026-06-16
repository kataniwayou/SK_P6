namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-12: Keeper DELETE state — delete-only. Carries the 4-id partition base
/// (corr/wf/proc/exec) plus the DELETE-only <see cref="EntryId"/>. NO MessageId (the two-key/index
/// model is retired). No orchestrator send. StepId rides as a record property but is NOT on the
/// partition marker (D-12).</summary>
public sealed record KeeperDelete(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // DELETE-only operand (the source entryId)
}
