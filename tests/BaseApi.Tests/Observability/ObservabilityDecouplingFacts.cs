using System.Reflection;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Architecture guard (Phase 79 follow-up) — the OBSERVABILITY stack (Elasticsearch, Prometheus, the OTEL
/// collector) MUST remain a pure fire-and-forget SINK for production execution. It must never become a
/// dependency the workflow can READ from or BLOCK on, because either would let an observability outage stall
/// (not merely blind) production orchestration. Pure reflection + source scan; no host boot, no Docker.
///
/// <para>
/// The stall lever exists — the Keeper BIT health gate publishes <c>PauseAll</c> on a health edge — but it is
/// wired to <b>Redis/L2</b> (<see cref="global::Keeper.Health.BitHealthLoop"/> probes L2, RedisException →
/// unhealthy), NOT to the observability stack. These facts keep that separation true across future edits:
/// the day someone adds an ES/Prometheus read into the execution path, or swaps the non-blocking log exporter
/// for a synchronous one, <c>dotnet test</c> goes red instead of a dashboard going quietly wrong (and the
/// dashboard is exactly what is down during an observability outage).
/// </para>
///
/// <para>
/// Two invariants:
/// <list type="number">
///   <item>READ-PATH decoupling — no production execution assembly references an ES/Prometheus CLIENT, and no
///   production source queries an observability read endpoint (<c>:9200</c>/<c>:9090</c>/<c>_search</c>).
///   Execution reads its state from Redis/RabbitMQ/Postgres only; the analyzer/tests read ES via a TEST-only
///   client (<c>ElasticsearchTestClient</c>).</item>
///   <item>NON-BLOCKING export — no production code uses a SYNCHRONOUS OTEL export processor
///   (<c>ExportProcessorType.Simple</c> / <c>Simple*ExportProcessor</c>); the log exporter is
///   <c>ExportProcessorType.Batch</c> (bounded queue, drop-on-full) so a dead collector can never apply
///   backpressure into the consume/hop path.</item>
/// </list>
/// </para>
///
/// <para>
/// NOTE — DIRECT references only for the assembly scan (<see cref="Assembly.GetReferencedAssemblies"/> reads a
/// manifest, not the transitive closure), mirroring <c>KeeperDependencyFirewallTests</c>. The source scan
/// complements it by catching a raw-<c>HttpClient</c> read against a sink port that carries no client-library
/// reference at all.
/// </para>
/// </summary>
public sealed class ObservabilityDecouplingFacts
{
    // Anchor on a real type in each production EXECUTION assembly so the guard reflects the actual manifests.
    private static readonly Assembly[] ExecutionAssemblies =
    [
        typeof(global::Orchestrator.Dispatch.StepDispatcher).Assembly,
        typeof(global::Keeper.Health.BitHealthLoop).Assembly,
        typeof(global::BaseProcessor.Core.Processing.ProcessorPipeline).Assembly,
        typeof(global::BaseConsole.Core.DependencyInjection.BaseConsoleObservabilityExtensions).Assembly,
        typeof(global::Messaging.Contracts.EntryStepDispatch).Assembly,
    ];

    // Observability CLIENT libraries — a direct reference from execution code would create a read/query path
    // into the sinks (the OTLP EXPORTER packages are NOT clients — they only write, and are intentionally
    // absent from this list).
    private static readonly string[] ForbiddenClientPrefixes =
    [
        "Elastic",        // Elastic.Clients.Elasticsearch / Elastic.Transport
        "NEST",
        "Elasticsearch",
        "Prometheus",     // prometheus-net (scrape/query client)
    ];

    [Fact]
    public void Execution_Assemblies_Reference_No_Observability_Client()
    {
        var violations = new List<string>();
        foreach (var asm in ExecutionAssemblies)
        {
            var bad = asm.GetReferencedAssemblies()
                .Select(a => a.Name ?? string.Empty)
                .Where(n => ForbiddenClientPrefixes.Any(p =>
                    n.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
            foreach (var b in bad)
                violations.Add($"{asm.GetName().Name} → {b}");
        }

        Assert.True(violations.Count == 0,
            "Production execution assemblies must not reference an Elasticsearch/Prometheus client — that " +
            "would give the workflow a read/query dependency on the observability stack (an outage could then " +
            "stall or block it). Offenders: " + string.Join("; ", violations));
    }

    [Fact]
    public void Production_Source_Never_Reads_Or_Blocks_On_Observability()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");
        Assert.True(Directory.Exists(srcRoot), $"src/ not found at {srcRoot}");

        var files = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();
        Assert.NotEmpty(files);

        // READ-PATH tokens: an observability SINK read endpoint. The OTLP WRITE endpoint (:4317 /
        // OTEL_EXPORTER_OTLP_ENDPOINT / otel-collector) is legitimate export and is deliberately NOT listed.
        string[] readTokens = [":9200", ":9090", "/_search", "ElasticsearchTestClient"];
        // SYNCHRONOUS export processors that would let a dead collector backpressure the hop.
        string[] blockingTokens =
            ["ExportProcessorType.Simple", "SimpleLogRecordExportProcessor", "SimpleActivityExportProcessor"];

        var readViolations = new List<string>();
        var blockViolations = new List<string>();
        var sawBatch = false;

        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            if (text.Contains("ExportProcessorType.Batch", StringComparison.Ordinal))
                sawBatch = true;
            foreach (var t in readTokens)
                if (text.Contains(t, StringComparison.Ordinal))
                    readViolations.Add($"{Rel(srcRoot, f)}: '{t}'");
            foreach (var t in blockingTokens)
                if (text.Contains(t, StringComparison.Ordinal))
                    blockViolations.Add($"{Rel(srcRoot, f)}: '{t}'");
        }

        Assert.True(readViolations.Count == 0,
            "Production execution code must never READ from the observability sinks (ES/Prometheus) — that " +
            "would let an outage stall or blind the workflow. Offenders: " + string.Join("; ", readViolations));
        Assert.True(blockViolations.Count == 0,
            "Production code must not use a SYNCHRONOUS OTEL export processor — a dead collector would then " +
            "backpressure the hop. Use ExportProcessorType.Batch. Offenders: " + string.Join("; ", blockViolations));
        Assert.True(sawBatch,
            "Expected at least one ExportProcessorType.Batch in production source (the non-blocking log-export " +
            "posture that keeps a dead collector from backpressuring the hop).");
    }

    private static string Rel(string root, string full) =>
        full.StartsWith(root, StringComparison.Ordinal) ? full[(root.Length + 1)..] : full;

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate SK_P.sln walking up from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
