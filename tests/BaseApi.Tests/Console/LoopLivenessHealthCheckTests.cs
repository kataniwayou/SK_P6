using BaseConsole.Core.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// HLTH-01/06 pure-unit proof of <see cref="LoopLivenessHealthCheck"/>: the shared loop-liveness watchdog
/// reads the in-process <see cref="ILivenessHeartbeat"/> via the OUTER provider at check time and reports
/// null/stale as Unhealthy, fresh as Healthy — with the grace factor generalized to <b>k=3</b> (was ×2).
/// No Redis/RMQ touched — the heartbeat holder + clock are fabricated and bridged through a stub provider.
/// Info-disclosure guard (T-86-01): no message leaks a secret token.
///
/// <para>
/// These are the Wave-0 RED tests: the <see cref="LoopLivenessHealthCheck"/> skeleton returns a static
/// Unhealthy("NOT IMPLEMENTED"), so every behavioral assertion below fails on BEHAVIOR (not compile).
/// 86-03 fills the null/stale/fresh/boundary logic to turn them GREEN.
/// </para>
/// </summary>
[Trait("Phase", "86")]
public sealed class LoopLivenessHealthCheckTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
    private const int IntervalSeconds = 10;
    private const int K = 3;

    private static IServiceProvider BuildProvider(DateTime? current)
    {
        var heartbeat = Substitute.For<ILivenessHeartbeat>();
        heartbeat.Current.Returns(current);

        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(Now));

        // GetRequiredService<T>() calls GetService(typeof(T)) under the hood — stub both resolutions.
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(ILivenessHeartbeat)).Returns(heartbeat);
        sp.GetService(typeof(TimeProvider)).Returns(clock);
        return sp;
    }

    private static Task<HealthCheckResult> RunAsync(DateTime? current)
        => new LoopLivenessHealthCheck(BuildProvider(current), IntervalSeconds, K)
            .CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Null_Heartbeat_Reports_Unhealthy_LoopNotStarted()
    {
        var result = await RunAsync(null);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("liveness loop not started", result.Description);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task Fresh_Beat_Reports_Healthy_Live()
    {
        var result = await RunAsync(Now.AddSeconds(-1));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("live", result.Description);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task Stale_Beat_Reports_Unhealthy_Stale()
    {
        // now - (k*interval) - 1s = well past the k=3 grace deadline.
        var result = await RunAsync(Now.AddSeconds(-(K * IntervalSeconds) - 1));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("liveness loop stale", result.Description);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task ExactBoundary_NowEqualsCurrentPlusKInterval_Reports_Unhealthy_Stale()
    {
        // deadline == Now exactly (Current + 3*interval == Now) ⇒ stale (strict >=).
        var result = await RunAsync(Now.AddSeconds(-(K * IntervalSeconds)));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("liveness loop stale", result.Description);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task OneTickBeforeBoundary_StrictlyFresh_Reports_Healthy()
    {
        // one tick before the boundary ⇒ strictly fresh ⇒ Healthy.
        var result = await RunAsync(Now.AddSeconds(-(K * IntervalSeconds)).AddTicks(1));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("live", result.Description);
        AssertNoSecretLeak(result);
    }

    private static void AssertNoSecretLeak(HealthCheckResult result)
    {
        // T-86-01 info-disclosure guard: no message/Data surface leaks a secret token.
        var surfaces = result.Data.Values
            .Select(v => v?.ToString() ?? string.Empty)
            .Append(result.Description ?? string.Empty);
        foreach (var s in surfaces)
        {
            Assert.DoesNotContain("Password=", s);
            Assert.DoesNotContain("abortConnect", s);   // Redis connection-string token
            Assert.DoesNotContain("   at ", s);          // .NET stack-trace frame marker
        }
    }
}
