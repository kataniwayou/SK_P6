using BaseConsole.Core.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// HLTH-05 pure-unit proof of <see cref="LatchedReadinessHealthCheck"/>: with failureThreshold=3, three
/// consecutive Unhealthy inner results flip a per-process STICKY latch so the wrapper stays Unhealthy even
/// after the inner check recovers (RESEARCH Pattern 5, Pitfall 4). A single Unhealthy followed by Healthy
/// does NOT latch (the counter resets on any non-Unhealthy result). The inner check is a scripted stub.
///
/// <para>
/// Wave-0 RED: the <see cref="LatchedReadinessHealthCheck"/> skeleton passes straight through to the inner
/// check with NO latch, so the sticky-after-recovery assertion fails RED (the recovered inner flips the
/// wrapper back to Healthy, which the locked behavior forbids). 86-04 fills the counter+latch logic.
/// </para>
/// </summary>
[Trait("Phase", "86")]
public sealed class LatchedReadinessHealthCheckTests
{
    private const int FailureThreshold = 3;

    private static IHealthCheck ScriptedInner(params HealthCheckResult[] scripted)
    {
        var queue = new Queue<HealthCheckResult>(scripted);
        var inner = Substitute.For<IHealthCheck>();
        inner.CheckHealthAsync(Arg.Any<HealthCheckContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(queue.Dequeue()));
        return inner;
    }

    private static Task<HealthCheckResult> EvalAsync(LatchedReadinessHealthCheck sut)
        => sut.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Threshold_Consecutive_Failures_Latch_Sticky_After_Recovery()
    {
        // Inner: Unhealthy, Unhealthy, Unhealthy, then RECOVERS to Healthy.
        var inner = ScriptedInner(
            HealthCheckResult.Unhealthy("down"),
            HealthCheckResult.Unhealthy("down"),
            HealthCheckResult.Unhealthy("down"),
            HealthCheckResult.Healthy("up"));
        var sut = new LatchedReadinessHealthCheck(inner, FailureThreshold);

        await EvalAsync(sut);                       // failure 1
        await EvalAsync(sut);                       // failure 2
        var third = await EvalAsync(sut);           // failure 3 → latch flips
        var afterRecovery = await EvalAsync(sut);   // inner Healthy, but latch is sticky

        Assert.Equal(HealthStatus.Unhealthy, third.Status);
        Assert.Equal(HealthStatus.Unhealthy, afterRecovery.Status);
    }

    [Fact]
    public async Task Single_Failure_Then_Healthy_Does_Not_Latch()
    {
        // Inner: Unhealthy (count=1), then Healthy resets the counter, then Healthy again.
        var inner = ScriptedInner(
            HealthCheckResult.Unhealthy("blip"),
            HealthCheckResult.Healthy("up"),
            HealthCheckResult.Healthy("up"));
        var sut = new LatchedReadinessHealthCheck(inner, FailureThreshold);

        await EvalAsync(sut);                  // transient failure
        await EvalAsync(sut);                  // recovers, counter resets
        var third = await EvalAsync(sut);      // still Healthy — no latch

        Assert.Equal(HealthStatus.Healthy, third.Status);
    }
}
