namespace Messaging.Contracts;

// bus envelope -- NO [JsonPropertyName], default STJ serialization.
/// <summary>D-02: Orchestrator DELETE state (the orchestrator-side mirror of <see cref="KeeperDelete"/>) --
/// delete-only. Carries the 4-id partition base (corr/wf/proc/exec) plus the DELETE-only
/// <see cref="EntryId"/> (the source out: entryId). NO message-id (no send; delete-only -- req 10). StepId
/// rides as a record property but is NOT on the partition marker (D-12).</summary>
public sealed record OrchestratorDelete(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // DELETE-only operand -- the source out: entryId (delete-only, req 10)
}
