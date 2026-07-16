using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// LOG-02 (D2) foundation facts for the keeper scope-open (Plan 06):
/// <list type="bullet">
///   <item>The loose-id <see cref="ExecutionLogScope.BuildState(Guid,Guid,Guid,Guid,Guid)"/> overload
///         applies byte-identical skip rules to the <see cref="IExecutionCorrelated"/> form (each
///         <c>Guid.Empty</c> id absent; EntryId absent when <see cref="SourceStep.IsSource"/>).</item>
///   <item>The <see cref="IExecutionCorrelated"/> overload DELEGATES to the loose-id form (single skip
///         impl) — proven by deep-equal parity for a random id set.</item>
///   <item>All six keeper recovery records are <see cref="ICorrelated"/>, so the bus-wide correlation
///         filter sources CorrelationId from the message BODY on a keeper consume (LOG-02 correctness).</item>
///   <item>The <see cref="IKeeperRecoverable"/> <c>GetProperties()</c> 4-tuple (corr:wf:proc:exec, D-12)
///         is preserved — the partition key shape is unchanged.</item>
/// </list>
/// Pure-contract facts — no bus harness needed (the overload/interface are POCO leaf types).
/// </summary>
public sealed class ExecutionLogScopeKeeperFacts
{
    /// <summary>A minimal <see cref="IExecutionCorrelated"/> probe carrying the five execution ids
    /// (mirrors <see cref="ConsoleExecutionScopeFilterTests.ExecProbeMessage"/>) — used for the
    /// delegation parity check against the loose-id overload.</summary>
    private sealed record ExecProbe(
        Guid CorrelationId, Guid WorkflowId, Guid StepId, Guid ProcessorId, Guid ExecutionId, Guid EntryId)
        : IExecutionCorrelated;

    // ── skip-rule facts on the loose-id overload (mirror ConsoleExecutionScopeFilterTests A/B/D) ──

    [Fact]
    public void AllFiveNonEmpty_YieldsFiveKeysWithToStringValues()
    {
        var w = Guid.NewGuid();
        var s = Guid.NewGuid();
        var p = Guid.NewGuid();
        var e = Guid.NewGuid();
        var entry = Guid.NewGuid();

        var state = ExecutionLogScope.BuildState(w, s, p, e, entry);

        Assert.Equal(5, state.Count);
        Assert.Equal(w.ToString(), state[ExecutionLogScope.WorkflowId]);
        Assert.Equal(s.ToString(), state[ExecutionLogScope.StepId]);
        Assert.Equal(p.ToString(), state[ExecutionLogScope.ProcessorId]);
        Assert.Equal(e.ToString(), state[ExecutionLogScope.ExecutionId]);
        Assert.Equal(entry.ToString(), state[ExecutionLogScope.EntryId]);
    }

    [Fact]
    public void ExecutionIdEmpty_IsSkipped_FourKeysRemain()
    {
        var w = Guid.NewGuid();
        var s = Guid.NewGuid();
        var p = Guid.NewGuid();
        var entry = Guid.NewGuid();

        var state = ExecutionLogScope.BuildState(w, s, p, Guid.Empty, entry);

        Assert.False(state.ContainsKey(ExecutionLogScope.ExecutionId));   // Guid.Empty → absent
        Assert.Equal(4, state.Count);
        Assert.Equal(w.ToString(), state[ExecutionLogScope.WorkflowId]);
        Assert.Equal(s.ToString(), state[ExecutionLogScope.StepId]);
        Assert.Equal(p.ToString(), state[ExecutionLogScope.ProcessorId]);
        Assert.Equal(entry.ToString(), state[ExecutionLogScope.EntryId]);
    }

    [Fact]
    public void EntryIdSourceSentinel_IsSkipped_FourKeysRemain()
    {
        var w = Guid.NewGuid();
        var s = Guid.NewGuid();
        var p = Guid.NewGuid();
        var e = Guid.NewGuid();

        // EntryId == Guid.Empty is the source-step sentinel (SourceStep.IsSource) → key absent.
        var state = ExecutionLogScope.BuildState(w, s, p, e, Guid.Empty);

        Assert.False(state.ContainsKey(ExecutionLogScope.EntryId));
        Assert.Equal(4, state.Count);
        Assert.Equal(e.ToString(), state[ExecutionLogScope.ExecutionId]);
    }

    // ── delegation parity: BuildState(ec) == BuildState(w,s,p,e,entry) for any id set ──

    [Fact]
    public void IExecutionCorrelatedOverload_DeepEquals_LooseIdOverload()
    {
        var probe = new ExecProbe(
            CorrelationId: Guid.NewGuid(),   // never scoped (D-01) — proves the overload ignores it too
            WorkflowId: Guid.NewGuid(),
            StepId: Guid.NewGuid(),
            ProcessorId: Guid.NewGuid(),
            ExecutionId: Guid.NewGuid(),
            EntryId: Guid.NewGuid());

        var fromInterface = ExecutionLogScope.BuildState(probe);
        var fromLooseIds = ExecutionLogScope.BuildState(
            probe.WorkflowId, probe.StepId, probe.ProcessorId, probe.ExecutionId, probe.EntryId);

        Assert.Equal(fromLooseIds, fromInterface);   // Dictionary value-equality (deep-equal)
    }

    // ── every keeper record is ICorrelated (LOG-02: body-sourced correlation on consume) ──

    [Theory]
    [InlineData(typeof(KeeperReinject))]
    [InlineData(typeof(KeeperInject))]
    [InlineData(typeof(KeeperDelete))]
    [InlineData(typeof(OrchestratorReinject))]
    [InlineData(typeof(OrchestratorInject))]
    [InlineData(typeof(OrchestratorDelete))]
    public void KeeperRecord_IsICorrelated(Type keeperRecord)
    {
        Assert.True(typeof(ICorrelated).IsAssignableFrom(keeperRecord),
            $"{keeperRecord.Name} must be ICorrelated so the bus-wide correlation filter sources CorrelationId from the body.");
        // Transitively also IKeeperRecoverable (the partition marker) — sanity.
        Assert.True(typeof(IKeeperRecoverable).IsAssignableFrom(keeperRecord));
    }

    // ── partition-shape guard: GetProperties() surfaces EXACTLY the 4-tuple (D-12) ──

    [Fact]
    public void IKeeperRecoverable_GetProperties_Is_The_Four_Tuple()
    {
        var expected = new[] { "CorrelationId", "ProcessorId", "WorkflowId", "ExecutionId" }
            .OrderBy(x => x);
        var actual = typeof(IKeeperRecoverable).GetProperties()
            .Select(pi => pi.Name)
            .OrderBy(x => x);

        Assert.Equal(expected, actual);
    }
}
