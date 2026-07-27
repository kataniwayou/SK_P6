using System.Diagnostics.Metrics;
using BaseConsole.Core.Observability;
using BaseProcessor.Core.Observability;
using Messaging.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Hermetic guard for the <see cref="ProcessorMetrics"/> holder (METRIC-05). Constructs it from a
/// real <see cref="IMeterFactory"/> (the .NET 8 blessed pattern — no <c>static Meter</c> field, so no
/// cross-test static leak in the shared hermetic process) and asserts the two counters are non-null
/// AND the meter-name const equals "BaseProcessor". This catches the D-02 meter-name/const-mismatch bug
/// (the const must equal the <c>AddMeter("BaseProcessor")</c> string) WITHOUT the full stack.
/// </summary>
public sealed class ProcessorMetricsFacts
{
    [Fact]
    public void MeterName_Is_BaseProcessor()
    {
        Assert.Equal("BaseProcessor", ProcessorMetrics.MeterName);
    }

    [Fact]
    public void Constructs_With_NonNull_Counters_From_Real_MeterFactory()
    {
        using var provider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();

        var metrics = new ProcessorMetrics(meterFactory);

        Assert.NotNull(metrics.MessagesConsumed);   // processor_messages_consumed (Phase 74)
        Assert.NotNull(metrics.MessagesSent);       // processor_messages_sent (Phase 74)
        Assert.NotNull(metrics.SpawnDropped);       // IN-03: spawn-drop has its own counter (kept)
    }

    /// <summary>
    /// D-07 regression guard. The cross-tier metric tag names are camelCase and live in ONE place
    /// (<see cref="ConsoleMetricTags"/>, in the assembly orchestrator + keeper + processor all reference),
    /// so a PromQL join on <c>processorId</c>/<c>workflowId</c> reaches every counter in the system.
    /// Commit 0548d02 fixed <c>processor_spawn_dropped</c>, which had been tagged PascalCase
    /// <c>"ProcessorId"</c> and was therefore invisible to exactly that join — and recorded that NO test
    /// asserted the tag name. This is that test. <c>identityName</c> stays processor-owned.
    /// </summary>
    [Fact]
    public void Metric_Tag_Names_Are_CamelCase()
    {
        Assert.Equal("processorId",  ConsoleMetricTags.ProcessorIdTag);
        Assert.Equal("workflowId",   ConsoleMetricTags.WorkflowIdTag);
        Assert.Equal("identityName", ProcessorMetrics.IdentityNameTag);
    }

    /// <summary>
    /// The invariant that actually broke: METRIC tags are camelCase while LOG attributes are PascalCase
    /// (<see cref="ExecutionLogScope"/>), and the spawn-drop counter had drifted onto the log convention.
    /// Pinning the two apart fails loudly if a future edit re-aligns a metric tag onto its log twin.
    /// </summary>
    [Fact]
    public void Metric_Tags_Do_Not_Collide_With_Log_Attribute_Names()
    {
        Assert.NotEqual(ExecutionLogScope.ProcessorId, ConsoleMetricTags.ProcessorIdTag);
        Assert.NotEqual(ExecutionLogScope.WorkflowId,  ConsoleMetricTags.WorkflowIdTag);

        // Convention, not just inequality: the metric tag must START lower-case (the log key starts upper).
        Assert.True(char.IsLower(ConsoleMetricTags.ProcessorIdTag[0]), "metric tags are camelCase");
        Assert.True(char.IsLower(ConsoleMetricTags.WorkflowIdTag[0]),  "metric tags are camelCase");
        Assert.True(char.IsUpper(ExecutionLogScope.ProcessorId[0]),    "log attributes are PascalCase");
    }
}
