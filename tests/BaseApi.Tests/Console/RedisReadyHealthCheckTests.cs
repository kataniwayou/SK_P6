using BaseConsole.Core.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// HLTH-04/08 pure-unit proof of <see cref="RedisReadyHealthCheck"/>: resolves
/// <see cref="IConnectionMultiplexer"/> from the OUTER provider at check time and reports null → Unhealthy
/// "Redis not started", a completing PING → Healthy, and a throwing multiplexer → Unhealthy "Redis
/// unreachable" WITHOUT letting the exception escape. Info-disclosure guard (T-86-01): no secret token in
/// the result surface. No real Redis — the multiplexer + database are substituted.
///
/// <para>
/// Wave-0 RED: the <see cref="RedisReadyHealthCheck"/> skeleton returns a static Unhealthy("NOT
/// IMPLEMENTED"), so every behavioral assertion fails on behavior (not compile). 86-04 fills the
/// resolve-ping-map logic to turn these GREEN.
/// </para>
/// </summary>
[Trait("Phase", "86")]
public sealed class RedisReadyHealthCheckTests
{
    private static IServiceProvider ProviderWith(IConnectionMultiplexer? mux)
    {
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IConnectionMultiplexer)).Returns(mux);
        return sp;
    }

    [Fact]
    public async Task Null_Multiplexer_Reports_Unhealthy_NotStarted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new RedisReadyHealthCheck(ProviderWith(null));

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Redis not started", result.Description);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task Completing_Ping_Reports_Healthy()
    {
        var ct = TestContext.Current.CancellationToken;

        var db = Substitute.For<IDatabase>();
        db.PingAsync(Arg.Any<CommandFlags>()).Returns(TimeSpan.Zero);
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var sut = new RedisReadyHealthCheck(ProviderWith(mux));

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        AssertNoSecretLeak(result);
    }

    [Fact]
    public async Task Throwing_Multiplexer_Reports_Unhealthy_Unreachable_Without_Throwing()
    {
        var ct = TestContext.Current.CancellationToken;

        var db = Substitute.For<IDatabase>();
        db.PingAsync(Arg.Any<CommandFlags>())
            .Returns<Task<TimeSpan>>(_ => throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "abortConnect=false; Password=secret"));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(db);

        var sut = new RedisReadyHealthCheck(ProviderWith(mux));

        // Must NOT throw out — the check maps any fault to Unhealthy.
        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Redis unreachable", result.Description);
        AssertNoSecretLeak(result);
    }

    private static void AssertNoSecretLeak(HealthCheckResult result)
    {
        // T-86-01 / T-18-08: the connection string + raw exception detail must never reach the surface.
        var surfaces = result.Data.Values
            .Select(v => v?.ToString() ?? string.Empty)
            .Append(result.Description ?? string.Empty);
        foreach (var s in surfaces)
        {
            Assert.DoesNotContain("Password=", s);
            Assert.DoesNotContain("abortConnect", s);
            Assert.DoesNotContain("   at ", s);
        }
    }
}
