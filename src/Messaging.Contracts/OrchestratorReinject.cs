namespace Messaging.Contracts;

// bus envelope -- NO [JsonPropertyName], default STJ serialization.
/// <summary>D-02: Orchestrator REINJECT state (the orchestrator-side mirror of <see cref="KeeperReinject"/>) --
/// the keeper re-injects the original Step-result (not a dispatch) to the orchestrator result queue with the
/// outbound envelope MessageId overridden to <see cref="MessageId"/> (D-03 -- "same messageId"); the
/// out: blob is re-read by STRLEN off <see cref="EntryId"/>, a clean-absent entry drops. StepId rides as a
/// record property but is NOT on the partition marker (D-12).
/// <para>
/// To let the REINJECT consumer rebuild the exact Step* record faithfully WITHOUT an L1 read, this carries the
/// outcome discriminator (<see cref="Outcome"/>) + the two payload-bearing diagnostic fields
/// (<see cref="ErrorMessage"/>/<see cref="CancellationMessage"/>) the Step* records use.
/// </para></summary>
public sealed record OrchestratorReinject(Guid WorkflowId, Guid StepId, Guid ProcessorId) : IKeeperRecoverable
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid EntryId       { get; init; }   // the out: key the orchestrator re-reads (STRLEN drop)
    public Guid MessageId     { get; init; }   // D-03: re-assert the SAME id on the re-injected result envelope
    public StepOutcome Outcome { get; init; }  // discriminator to pick the Step* type on re-injection
    public string ErrorMessage        { get; init; } = "";   // StepFailed diagnostic (faithful rebuild)
    public string CancellationMessage { get; init; } = "";   // StepCancelled diagnostic (faithful rebuild)
}
