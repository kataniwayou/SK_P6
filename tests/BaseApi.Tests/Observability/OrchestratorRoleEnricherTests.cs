using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Orchestrator.Election;
using Orchestrator.Observability;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// HA-05 / D-09: the <see cref="OrchestratorRoleLogEnricher"/> appends <c>attributes.role</c> — read LIVE
/// from the <see cref="LeaderState"/> — to EVERY orchestrator <see cref="LogRecord"/>. UNLIKE the
/// <c>ProcessorIdLogEnricher</c> analog there is NO null-guard: role is never empty (defaults to
/// <c>"follower"</c>, only ever swaps to <c>"leader"</c>), so the tag is ALWAYS present and non-blank.
/// <list type="bullet">
///   <item>Follower state → <c>attributes.role == "follower"</c> (non-empty).</item>
///   <item>Leader state → <c>attributes.role == "leader"</c> (non-empty).</item>
///   <item>A post-construction flip (BecomeLeader) surfaces on the NEXT record with the SAME enricher
///   instance — the value is read live per record, not cached at construction (D-09).</item>
/// </list>
/// Mirrors <c>ProcessorIdEnricherTests</c>: the SUT enricher runs first on a real OTel logger provider,
/// then a downstream in-memory <see cref="CapturingProcessor"/> observes the enriched record. The
/// attribute key is the lowercase literal <c>"role"</c> (NOT an <c>ExecutionLogScope.*</c> const).
/// </summary>
public sealed class OrchestratorRoleEnricherTests
{
    /// <summary>A captured-by-reference in-memory processor: records the latest finished LogRecord's attributes.</summary>
    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        public List<KeyValuePair<string, object?>> LastAttributes { get; } = new();

        public override void OnEnd(LogRecord record)
        {
            LastAttributes.Clear();
            if (record.Attributes is not null)
                LastAttributes.AddRange(record.Attributes);
        }
    }

    /// <summary>
    /// Builds a logger factory whose OTel provider runs the SUT enricher (over <paramref name="state"/>)
    /// first, then the capturing processor, emits ONE log, and returns the capture (mirrors the analog's
    /// EmitOneLog, parameterized on a <see cref="LeaderState"/>).
    /// </summary>
    private static CapturingProcessor EmitOneLog(LeaderState state)
    {
        var capture = new CapturingProcessor();
        using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.ParseStateValues = true;
            o.AddProcessor(new OrchestratorRoleLogEnricher(state));   // SUT — enriches first
            o.AddProcessor(capture);                                  // then capture observes the enriched record
        }));

        factory.CreateLogger("test").LogInformation("enricher probe");
        return capture;
    }

    /// <summary>Assert exactly one non-empty <c>"role"</c> attribute and return its value.</summary>
    private static string RoleOf(CapturingProcessor capture)
    {
        var single = Assert.Single(capture.LastAttributes, kvp => kvp.Key == "role");
        var value = single.Value as string;
        Assert.False(string.IsNullOrEmpty(value), "attributes.role must never be blank (HA-05)");
        return value!;
    }

    [Fact]
    public void Follower_State_Stamps_role_follower()
    {
        var capture = EmitOneLog(new LeaderState());   // default = follower

        Assert.Equal("follower", RoleOf(capture));
    }

    [Fact]
    public void Leader_State_Stamps_role_leader()
    {
        var capture = EmitOneLog(new LeaderState(startAsLeader: true));   // leader seed

        Assert.Equal("leader", RoleOf(capture));
    }

    [Fact]
    public void Live_Read_Reflects_Post_Construction_Flip()
    {
        // ONE enricher instance over a mutable LeaderState; both records share it. The role is read live
        // per record, so a mid-life BecomeLeader flip must surface on the SECOND line (D-09).
        var state = new LeaderState();   // starts follower
        var enricher = new OrchestratorRoleLogEnricher(state);
        var capture = new CapturingProcessor();

        using var factory = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeScopes = true;
            o.ParseStateValues = true;
            o.AddProcessor(enricher);   // SAME instance across both emits
            o.AddProcessor(capture);
        }));
        var logger = factory.CreateLogger("test");

        logger.LogInformation("probe 1");
        Assert.Equal("follower", RoleOf(capture));   // pre-flip: follower

        state.BecomeLeader();                        // failover flip AFTER construction

        logger.LogInformation("probe 2");
        Assert.Equal("leader", RoleOf(capture));     // post-flip: the live read surfaces leader
    }
}
