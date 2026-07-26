using System.Collections.Concurrent;
using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Liveness;
using BaseProcessor.Core.Startup;
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// HLTH-02 regression LOCK (T-86-02): the <see cref="ProcessorStartupOrchestrator"/> identity/schema loop
/// treats a broker-unreachable-style infra fault (a real <see cref="RedisConnectionException"/>, explicitly
/// NOT <see cref="OperationCanceledException"/>) as caught-logged-retried and NEVER lets it escape
/// <c>ExecuteAsync</c> and take the host down. This pins the ALREADY-compliant broad catch
/// (<c>catch (Exception ex) when (ex is not OperationCanceledException)</c>, ProcessorStartupOrchestrator.cs
/// L141-147 / L195-201) — it is a behavior-preservation lock, NOT a RED-then-GREEN scaffold, so it PASSES
/// immediately. The self-inflicted-crash DoS (a down broker crash-looping the pod) is the mitigated threat.
///
/// <para>
/// Mirrors <see cref="IdentityResolutionFacts"/>'s harness shape: the Loop-A identity RPC is driven through a
/// substituted <see cref="IRequestClient{TRequest}"/> whose <c>Create</c> seam (the interface method the
/// two-response <c>GetResponse&lt;T1,T2&gt;</c> extension delegates to) throws the infra fault on every attempt,
/// so identity never resolves and the loop backs off + retries forever. A <see cref="FakeTimeProvider"/>
/// releases the bounded-backoff delays so the test never sleeps in real time; a CancellationTokenSource
/// timeout fails a hang fast. No Redis/RMQ is touched — the fault is injected, not real infra.
/// </para>
/// </summary>
[Trait("Phase", "86")]
public sealed class StartupOrchestratorResilienceFacts
{
    [Fact]
    public async Task BrokerDown_IdentityLoop_IsCaught_Logged_Retried_AndNeverThrowsOut()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        var faultCalls = 0;
        var identityClient = Substitute.For<IRequestClient<GetProcessorBySourceHash>>();
        // The Loop-A identity RPC faults on EVERY attempt with a broker/store-unreachable infra exception that
        // is deliberately NOT OperationCanceledException (the one intended pass-through) — the orchestrator's
        // `await ...GetResponse(...)` surfaces it inside its try, exercising the broad catch under test.
        identityClient
            .GetResponse<ProcessorIdentityFound, ProcessorIdentityNotFound>(
                Arg.Any<GetProcessorBySourceHash>(), Arg.Any<CancellationToken>(), Arg.Any<RequestTimeout>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref faultCalls);
                return Task.FromException<Response<ProcessorIdentityFound, ProcessorIdentityNotFound>>(
                    new RedisConnectionException(
                        ConnectionFailureType.UnableToConnect, "broker unreachable (simulated)"));
            });

        var schemaClient = Substitute.For<IRequestClient<GetSchemaDefinition>>();
        var sourceHash = Substitute.For<ISourceHashProvider>();
        sourceHash.Get().Returns(new string('a', 64));

        var context = new ProcessorContext();
        var gate = new StartupGate();
        var clock = new FakeTimeProvider();
        var options = Options.Create(new ProcessorLivenessOptions
        {
            IntervalSeconds = 10,
            TtlSeconds = 30,
            RequestTimeoutSeconds = 8,
            BackoffCapSeconds = 30,
        });
        var logger = new CapturingLogger<ProcessorStartupOrchestrator>();

        var orchestrator = new ProcessorStartupOrchestrator(
            identityClient, schemaClient, sourceHash, context, gate,
            IdentityResolutionFacts.StubConnector(), IdentityResolutionFacts.StubMeterProviderHolder(),
            IdentityResolutionFacts.StubConfigTypeProvider(), IdentityResolutionFacts.StubLivenessWriter(),
            "pod-resilience", options, clock, logger);

        await orchestrator.StartAsync(cts.Token); // returns once ExecuteAsync first yields at the backoff await

        // Pump the fake clock through several backoff cycles; each release re-enters Loop A and faults again —
        // proving the broad catch swallowed the prior fault and the loop kept turning (never resolving).
        var warnings = 0;
        for (var i = 0; i < 10 && warnings < 2; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(60)); // > BackoffCap releases any pending backoff Task.Delay
            await Task.Delay(20, cts.Token);         // let the orchestrator continuation run
            warnings = logger.Entries.Count(e => e.Level == LogLevel.Warning);
        }

        // (a) caught + logged + retried: the broker-down fault produced repeated warnings (loop re-entered),
        //     never resolving — the invariant's positive proof.
        Assert.True(warnings >= 2, $"expected >= 2 retry warnings under a down broker, got {warnings}");
        Assert.True(Volatile.Read(ref faultCalls) >= 2,
            $"expected the identity loop to re-attempt (>=2), got {Volatile.Read(ref faultCalls)}");

        // (b) NOTHING escaped ExecuteAsync — the BackgroundService task is not faulted (host stays up).
        Assert.True(orchestrator.ExecuteTask is null || !orchestrator.ExecuteTask.IsFaulted);

        // (c) the orchestrator never falsely reached Healthy / Ready while the broker is down.
        Assert.False(context.IsHealthy);
        Assert.False(gate.IsReady);

        // (d) a clean StopAsync surfaces no faulted task — the swallowed fault leaves the loop cancel-clean,
        //     and the shutdown OperationCanceledException is the deliberate pass-through (returns, not faults).
        await orchestrator.StopAsync(cts.Token);
        Assert.True(orchestrator.ExecuteTask is null || !orchestrator.ExecuteTask.IsFaulted);
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }
}
