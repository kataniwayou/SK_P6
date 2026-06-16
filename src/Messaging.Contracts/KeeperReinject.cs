namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization.
/// <summary>D-12: Keeper REINJECT state — read L2[entryId], re-inject the original dispatch (carrying
/// <see cref="Payload"/>) to Pre with the outbound envelope MessageId overridden to <see cref="MessageId"/>
/// (req 6 — "same messageId"); clean-absent entryId drops. StepId rides as a record property but is NOT on
/// the partition marker (D-12).</summary>
public sealed record KeeperReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }
    public Guid MessageId     { get; init; }   // req 6/A3: carried envelope id — re-inject with the SAME messageId
    public string Payload     { get; init; } = "";   // step config for faithful EntryStepDispatch reconstruction
}
