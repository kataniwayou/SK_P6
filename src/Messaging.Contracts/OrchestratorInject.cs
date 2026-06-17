namespace Messaging.Contracts;

// bus envelope -- NO [JsonPropertyName], default STJ serialization.
/// <summary>D-02: Orchestrator INJECT state (the orchestrator-side mirror of <see cref="KeeperInject"/>) --
/// embeds the self-contained <see cref="NextStepHandoff"/> (write L2[data:messageId] from
/// <see cref="NextStepHandoff.Data"/>, then dispatch by it) plus <see cref="DeleteEntryId"/> (the source
/// out: entryId to delete) + the in-body <see cref="MessageId"/> (D-03: the data: write key + the dispatched
/// entryId, re-asserted on the dispatch envelope). Mirrors <see cref="KeeperInject"/> but embeds a
/// <see cref="NextStepHandoff"/> (not a <see cref="DataResult"/>) and the namespaces cross-pair (writes
/// data:, deletes out:). StepId rides as a record property but is NOT on the partition marker (D-12).</summary>
public sealed record OrchestratorInject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public NextStepHandoff Handoff { get; init; } = null!;   // mirror KeeperInject.DataResult -- the relocate payload, in-hand
    public Guid MessageId     { get; init; }   // D-03: the data: write key + the dispatched entryId
    public Guid DeleteEntryId { get; init; }   // the source out: entryId to delete (mirror KeeperInject.DeleteEntryId)
}
