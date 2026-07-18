using Orchestrator.Election;
using OpenTelemetry;                 // BaseProcessor<T>
using OpenTelemetry.Logs;            // LogRecord

namespace Orchestrator.Observability;

/// <summary>
/// HA-05 / D-09: appends <c>attributes.role</c> (from the live <see cref="LeaderState.Role"/>) to
/// EVERY orchestrator LogRecord (startup, fire, consume), so OTel serializes it as the lowercase
/// literal field <c>attributes.role</c> = <c>leader</c> | <c>follower</c> — the per-record tag the
/// Phase-83 zero-duplicate/failover audit greps.
/// <para>
/// UNLIKE the <c>ProcessorIdLogEnricher</c> analog there is NO
/// null-guard early return: <see cref="LeaderState.Role"/> is NEVER empty (it defaults to
/// <c>"follower"</c> and only ever swaps to <c>"leader"</c>), so the role is ALWAYS appended. The
/// value is read LIVE on every record, so a post-construction failover flip (BecomeLeader /
/// BecomeFollower) surfaces on the very next log line.
/// </para>
/// <para>
/// Registered ONLY on the orchestrator's logger provider (Program.cs), mirroring the ProcessorId
/// enricher's DI-resolved <c>ConfigureOpenTelemetryLoggerProvider</c> pair. Safe on OTel 1.15.3:
/// reassigning <c>Attributes</c> alone is correct (no State desync — the v1.5-v1.7 bug does not apply).
/// </para>
/// </summary>
public sealed class OrchestratorRoleLogEnricher(LeaderState state) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord record) =>
        record.Attributes = (record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
            .Append(new KeyValuePair<string, object?>("role", state.Role))
            .ToList();
}
