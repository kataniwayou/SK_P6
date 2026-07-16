using System.Text.RegularExpressions;
using BaseApi.Tests.Processor;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Processing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Phase 76 (FW-03 / T-76-01) — the framework per-hop records carry ids + outcome ONLY, never a payload.
/// Two guards:
/// <list type="bullet">
///   <item><b>Grep-clean</b>: no <c>logger.Log*</c> template or argument list in the two framework
///   log-emission sites (<see cref="ProcessorPipeline"/> and the orchestrator pre-pipeline) references
///   <c>validatedData</c>, <c>dr.Data</c>, or <c>d.Payload</c>.</item>
///   <item><b>Sentinel-through-blob</b>: feeding a unique sentinel through a hop's input/output blob leaves
///   that sentinel absent from every captured framework record's structured state values.</item>
/// </list>
/// </summary>
public sealed class FrameworkNoPayloadFacts
{
    private static readonly string[] ForbiddenPayloadTokens = { "validatedData", "dr.Data", "d.Payload" };

    /// <summary>Walk up from the test binary to the repo root (the dir holding <c>SK_P.sln</c>).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);   // the sln must be found — otherwise the source-scan guard is meaningless
        return dir!.FullName;
    }

    /// <summary>Extract every <c>logger.Log...( ... );</c> statement (across line breaks) from source.</summary>
    private static IEnumerable<string> LogStatements(string source) =>
        Regex.Matches(source, @"logger\.Log\w+\(.*?\);", RegexOptions.Singleline).Select(m => m.Value);

    [Fact]
    public void Framework_NoPayload_GrepClean_OnLogTemplates()
    {
        var root = RepoRoot();
        var sources = new[]
        {
            Path.Combine(root, "src", "BaseProcessor.Core", "Processing", "ProcessorPipeline.cs"),
            Path.Combine(root, "src", "Orchestrator", "Dispatch", "OrchestratorPrePipeline.cs"),
        };

        foreach (var path in sources)
        {
            Assert.True(File.Exists(path), $"expected framework log-emission source at {path}");
            var text = File.ReadAllText(path);
            foreach (var logCall in LogStatements(text))
                foreach (var token in ForbiddenPayloadTokens)
                    Assert.False(logCall.Contains(token, StringComparison.Ordinal),
                        $"FW-03 violation: a logger.Log* call in {Path.GetFileName(path)} references '{token}':\n{logCall}");
        }
    }

    [Fact]
    public async Task Framework_NoPayload_SentinelThroughBlob_AbsentFromEveryRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var sentinel = $"SENTINEL-FW03-{Guid.NewGuid():N}";
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        // The sentinel rides BOTH the L2 input blob AND the produced output data — the two payload surfaces the
        // framework record must never echo.
        var redis = DispatchTestKit.ReadWriteDeleteOkL2(
            new Dictionary<string, string>
            { [L2ProjectionKeys.ExecutionData(entryId)] = $"{{\"in\":\"{sentinel}\"}}" }, out _);
        var processor = new DispatchTestKit.FakeProcessor(
            DispatchTestKit.Result(StepOutcome.Completed, $"{{\"out\":\"{sentinel}\"}}"));
        var send = new DispatchTestKit.CapturingSendProvider();
        var log = new CapturingLogger<ProcessorPipeline>();
        var context = new FakeProcessorContext { InputDefinition = null, OutputDefinition = null };
        var d = DispatchTestKit.Dispatch(entryId, correlationId: Guid.NewGuid()) with { ExecutionId = Guid.NewGuid() };

        var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3),
            DispatchTestKit.Options(300), DispatchTestKit.Metrics());
        var pipeline = new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), tail,
            DispatchTestKit.Metrics(), log);

        await pipeline.RunAsync(d, messageId, ct);

        // Sanity: at least one framework record was emitted (the hop executed).
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information && e.State.Any(kv => kv.Key == "Outcome"));
        // FW-03: no captured record value echoes the sentinel that flowed through the blob.
        foreach (var entry in log.Entries)
            foreach (var kv in entry.State)
                Assert.DoesNotContain(sentinel, kv.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Structured-state capturing logger (Pitfall 1: explicit placeholder args only, not scope).</summary>
    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        internal sealed record Entry(LogLevel Level, IReadOnlyList<KeyValuePair<string, object?>> State);

        private readonly List<Entry> _entries = new();
        public IReadOnlyList<Entry> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var pairs = state is IReadOnlyList<KeyValuePair<string, object?>> kvps
                ? kvps.ToList()
                : new List<KeyValuePair<string, object?>>();
            _entries.Add(new Entry(logLevel, pairs));
        }

        private sealed class NullScope : IDisposable
        {
            internal static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
