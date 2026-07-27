using BaseProcessor.Core.Identity;
using Messaging.Contracts;           // ExecutionLogScope.ProcessorId
using OpenTelemetry;                 // BaseProcessor<T>
using OpenTelemetry.Logs;            // LogRecord

namespace BaseProcessor.Core.Observability;

/// <summary>
/// LOG-04: appends <c>ProcessorId</c> (from the singleton <see cref="IProcessorContext.Id"/>) to
/// EVERY processor LogRecord (startup, heartbeat, consume), so OTel serializes it as
/// <c>attributes.ProcessorId</c>. Null-safe: before identity resolves (<c>Id == null</c>) it adds
/// nothing — never <see cref="System.Guid.Empty"/> (SPEC constraint). Registered ONLY on the
/// processor's logger provider (NOT the shared BaseConsole.Core observability extension — L3). Safe on
/// OTel 1.15.3: reassigning Attributes alone is correct (no State desync — the v1.5-v1.7 bug, L4).
/// </summary>
public sealed class ProcessorIdLogEnricher(IProcessorContext context) : BaseProcessor<LogRecord>
{
    /// <summary>
    /// The resolved DB identity as ONE combined <c>{db.Name}_{db.Version}</c> string — the same value
    /// the metrics resource carries as <c>service_name</c>. NOT named <c>ProcessorName</c>: the value is
    /// name+version, not the processors-table <c>Name</c> column, and mis-naming a composite after one of
    /// its parts is the exact confusion that made <c>service_name</c>/<c>service_version</c> ambiguous.
    /// PascalCase per the log-attribute convention (<see cref="ExecutionLogScope"/>); the metrics side
    /// uses camelCase.
    /// </summary>
    public const string IdentityName = "IdentityName";

    public override void OnEnd(LogRecord record)
    {
        if (context.Id is not { } id) return;

        var attrs = (record.Attributes ?? Array.Empty<KeyValuePair<string, object?>>())
            .Append(new KeyValuePair<string, object?>(ExecutionLogScope.ProcessorId, id.ToString()));

        // Name/Version are unsynchronized auto-properties set alongside Id (WR-03), so a non-null Id
        // does NOT guarantee both are visible on a weak memory model. Guard them INDEPENDENTLY of Id:
        // a torn read then costs this record one attribute instead of throwing inside a log processor.
        if (context.Name is { } name && context.Version is { } version)
        {
            attrs = attrs.Append(new KeyValuePair<string, object?>(IdentityName, $"{name}_{version}"));
        }

        record.Attributes = attrs.ToList();
    }
}
