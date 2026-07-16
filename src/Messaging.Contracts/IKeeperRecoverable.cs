namespace Messaging.Contracts;

/// <summary>D-12: marker exposing the partition 4-tuple (corr:wf:proc:exec == the composite-backup
/// key) the Phase-46 MassTransit UsePartitioner consumes for per-key ordering. stepId rides as a
/// plain property on each record, NOT part of the partition key. The four members are declared
/// directly here (not inherited) so the marker reflects exactly the 4-tuple — interface
/// <c>GetProperties()</c> does not surface base-interface members.
/// <para>Extends <see cref="ICorrelated"/> (LOG-02 correlation correctness) so keeper records are
/// recognized by the bus-wide correlation filter — CorrelationId flows body→scope on consume and
/// ambient→envelope on send, exactly like <see cref="IExecutionCorrelated"/>. CorrelationId is
/// re-declared with <c>new</c> (not removed) so it stays a DIRECTLY-declared member and the
/// <c>GetProperties()</c> 4-tuple (corr:wf:proc:exec, D-12) is byte-for-byte preserved.</para></summary>
public interface IKeeperRecoverable : ICorrelated
{
    // Re-declared with `new` (not removed) so MassTransit UsePartitioner's GetProperties() 4-tuple
    // (corr:wf:proc:exec, D-12) is byte-for-byte preserved; extending ICorrelated makes keeper records
    // recognized by the bus-wide correlation filter/outbound stamp (LOG-02 correlation correctness).
    new Guid CorrelationId { get; }
    Guid WorkflowId    { get; }
    Guid ProcessorId   { get; }
    Guid ExecutionId   { get; }
}
