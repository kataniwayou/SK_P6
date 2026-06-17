using System.Text.Json;
using global::Keeper.Recovery;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// Phase 71 (Plan 01): the new orchestrator two-consumer contracts.
/// <list type="bullet">
///   <item><b>NextStepHandoff</b> round-trips through System.Text.Json with all 7 logical fields preserved
///   AND carries NO "MessageId" property on the wire (D-11 — the message-id rides the envelope).</item>
///   <item><b>OrchestratorInject</b> round-trips with its embedded <c>NextStepHandoff</c> intact (nested
///   record serialization) and DeleteEntryId/MessageId preserved.</item>
///   <item>The three orchestrator keeper contracts (Reinject/Inject/Delete) share ONE partition slot for a
///   given 4-tuple (corr/wf/proc/exec) — StepId excluded (D-12), via
///   <see cref="ReinjectConsumerDefinition.PartitionGuid"/>.</item>
/// </list>
/// Pure record/serialization facts — no harness/double. Models on StepResultContractTests (round-trip) +
/// RecoveryPartitionFacts (partition stability).
/// </summary>
[Trait("Phase", "71")]
public sealed class OrchestratorContractTests
{
    [Fact]
    public void NextStepHandoff_round_trips()
    {
        var original = new NextStepHandoff(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":\"x\"}")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Data = "{\"k\":1}",
        };

        var json = JsonSerializer.Serialize(original);
        var round = JsonSerializer.Deserialize<NextStepHandoff>(json);

        Assert.NotNull(round);
        Assert.Equal(original.WorkflowId, round!.WorkflowId);
        Assert.Equal(original.StepId, round.StepId);
        Assert.Equal(original.ProcessorId, round.ProcessorId);
        Assert.Equal(original.Payload, round.Payload);
        Assert.Equal(original.CorrelationId, round.CorrelationId);
        Assert.Equal(original.ExecutionId, round.ExecutionId);
        Assert.Equal(original.Data, round.Data);

        // D-11: NO MessageId on the wire — the message-id rides the bus envelope, not the body.
        Assert.DoesNotContain("MessageId", json);
    }

    [Fact]
    public void OrchestratorInject_round_trips_with_embedded_handoff()
    {
        var handoff = new NextStepHandoff(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "{\"cfg\":42}")
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.NewGuid(),
            Data = "{\"out\":7}",
        };
        var original = new OrchestratorInject(handoff.WorkflowId, handoff.StepId, handoff.ProcessorId)
        {
            CorrelationId = handoff.CorrelationId,
            ExecutionId = handoff.ExecutionId,
            Handoff = handoff,
            MessageId = Guid.NewGuid(),
            DeleteEntryId = Guid.NewGuid(),
        };

        var json = JsonSerializer.Serialize(original);
        var round = JsonSerializer.Deserialize<OrchestratorInject>(json);

        Assert.NotNull(round);
        Assert.Equal(original.MessageId, round!.MessageId);
        Assert.Equal(original.DeleteEntryId, round.DeleteEntryId);

        // The embedded handoff survives nested serialization with its Data intact.
        Assert.NotNull(round.Handoff);
        Assert.Equal(handoff.WorkflowId, round.Handoff.WorkflowId);
        Assert.Equal(handoff.StepId, round.Handoff.StepId);
        Assert.Equal(handoff.ProcessorId, round.Handoff.ProcessorId);
        Assert.Equal(handoff.Payload, round.Handoff.Payload);
        Assert.Equal(handoff.CorrelationId, round.Handoff.CorrelationId);
        Assert.Equal(handoff.ExecutionId, round.Handoff.ExecutionId);
        Assert.Equal(handoff.Data, round.Handoff.Data);
        Assert.Equal(handoff, round.Handoff);   // value equality on the record
    }

    [Fact]
    public void Orchestrator_keeper_contracts_share_one_partition_slot()
    {
        var corr = Guid.NewGuid();
        var wf = Guid.NewGuid();
        var proc = Guid.NewGuid();
        var exec = Guid.NewGuid();

        // SAME 4-tuple (corr/wf/proc/exec), DIFFERENT StepId on each — StepId is excluded from the slot (D-12).
        var reinject = new OrchestratorReinject(wf, Guid.NewGuid(), proc)
            { CorrelationId = corr, ExecutionId = exec, EntryId = Guid.NewGuid(), MessageId = Guid.NewGuid() };
        var inject = new OrchestratorInject(wf, Guid.NewGuid(), proc)
            { CorrelationId = corr, ExecutionId = exec, MessageId = Guid.NewGuid(), DeleteEntryId = Guid.NewGuid() };
        var delete = new OrchestratorDelete(wf, Guid.NewGuid(), proc)
            { CorrelationId = corr, ExecutionId = exec, EntryId = Guid.NewGuid() };

        var slotR = ReinjectConsumerDefinition.PartitionGuid(reinject);
        var slotI = ReinjectConsumerDefinition.PartitionGuid(inject);
        var slotD = ReinjectConsumerDefinition.PartitionGuid(delete);

        Assert.Equal(slotR, slotI);
        Assert.Equal(slotI, slotD);

        // Guard: the StepIds really do differ (so the equality above is StepId-exclusion, not coincidence).
        Assert.NotEqual(reinject.StepId, inject.StepId);
        Assert.NotEqual(inject.StepId, delete.StepId);
    }
}
