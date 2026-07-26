using BaseApi.Core.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Api;

/// <summary>
/// HLTH-04/05/08 (Phase 86, plan 05): the WebApi mirror of the Redis-readiness + hard-latch pattern.
/// Proves the sticky readiness latch (<see cref="ApiLatchedReadinessHealthCheck"/>, T-86-10 no self-heal)
/// and the bounded never-throw Redis mapping (<see cref="ApiRedisReadyHealthCheck"/>, T-86-09 no secret
/// leak). Hermetic — no real Redis, no fixture; NOT RealStack.
/// </summary>
[Trait("Phase", "86")]
public sealed class ApiReadinessLatchTests
{
    // ---- Redis readiness mapping (ApiRedisReadyHealthCheck) --------------------------------------

    [Fact]
    public async Task RedisReady_Null_Multiplexer_Is_Unhealthy_Not_Started()
    {
        var ct = TestContext.Current.CancellationToken;

        // Empty outer provider — GetService<IConnectionMultiplexer>() returns null (Redis not started).
        var outer = new ServiceCollection().BuildServiceProvider();
        var sut = new ApiRedisReadyHealthCheck(outer);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Redis not started", result.Description);
    }

    [Fact]
    public async Task RedisReady_Ping_Ok_Is_Healthy()
    {
        var ct = TestContext.Current.CancellationToken;

        var db = Substitute.For<IDatabase>();
        db.PingAsync().Returns(TimeSpan.FromMilliseconds(1));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase().Returns(db);

        var outer = new ServiceCollection().AddSingleton(mux).BuildServiceProvider();
        var sut = new ApiRedisReadyHealthCheck(outer);

        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task RedisReady_Unreachable_Is_Unhealthy_And_Never_Throws_And_Leaks_No_Secret()
    {
        var ct = TestContext.Current.CancellationToken;

        // A throwing multiplexer whose exception message carries a fake secret; the probe must map it to a
        // static Unhealthy literal WITHOUT surfacing the secret (T-86-09 info-disclosure guard).
        const string secret = "password=SUPER_SECRET_TOKEN_42";
        var db = Substitute.For<IDatabase>();
        db.PingAsync().Returns(Task.FromException<TimeSpan>(
            new RedisConnectionException(ConnectionFailureType.UnableToConnect, secret)));
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase().Returns(db);

        var outer = new ServiceCollection().AddSingleton(mux).BuildServiceProvider();
        var sut = new ApiRedisReadyHealthCheck(outer);

        // Must NOT throw out of the check.
        var result = await sut.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Redis unreachable", result.Description);
        Assert.DoesNotContain("SUPER_SECRET_TOKEN_42", result.Description ?? string.Empty);
    }

    // ---- Sticky readiness latch (ApiLatchedReadinessHealthCheck) ---------------------------------

    [Fact]
    public async Task Latch_Stays_Unhealthy_After_Inner_Recovers()
    {
        var ct = TestContext.Current.CancellationToken;

        var inner = new ToggleHealthCheck { Status = HealthStatus.Unhealthy };
        var latch = new ApiLatchedReadinessHealthCheck(inner, failureThreshold: 3);

        // Three consecutive Unhealthy evaluations reach the threshold and latch.
        for (var i = 0; i < 3; i++)
        {
            await latch.CheckHealthAsync(new HealthCheckContext(), ct);
        }

        // Inner recovers — but the latch is sticky (no self-heal, T-86-10).
        inner.Status = HealthStatus.Healthy;
        var result = await latch.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task Latch_Single_Blip_Below_Threshold_Resets_And_Does_Not_Latch()
    {
        var ct = TestContext.Current.CancellationToken;

        var inner = new ToggleHealthCheck { Status = HealthStatus.Unhealthy };
        var latch = new ApiLatchedReadinessHealthCheck(inner, failureThreshold: 3);

        // Two failures — below the threshold of 3.
        await latch.CheckHealthAsync(new HealthCheckContext(), ct);
        await latch.CheckHealthAsync(new HealthCheckContext(), ct);

        // Recovery resets the consecutive-failure counter; the blip did not latch.
        inner.Status = HealthStatus.Healthy;
        var result = await latch.CheckHealthAsync(new HealthCheckContext(), ct);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    /// <summary>Deterministic inner check whose returned status is flipped by the test.</summary>
    private sealed class ToggleHealthCheck : IHealthCheck
    {
        public HealthStatus Status { get; set; } = HealthStatus.Healthy;

        public Task<HealthCheckResult> CheckHealthAsync(
            HealthCheckContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new HealthCheckResult(Status));
    }
}
