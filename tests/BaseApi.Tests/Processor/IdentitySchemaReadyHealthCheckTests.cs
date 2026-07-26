using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Liveness;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// RED scaffold (Wave-0) pinning the LOCKED <see cref="ProcessorIdentitySchemaReadyHealthCheck"/> readiness
/// mapping (HLTH-04 / RESEARCH Pattern 4): the check resolves <see cref="IProcessorContext"/> from the OUTER
/// provider at check time and maps its ONE synchronized signal — <see cref="IProcessorContext.IsHealthy"/> —
/// onto readiness:
/// <list type="bullet">
///   <item><c>IsHealthy == false</c> ⇒ Unhealthy ("identity+schema not yet resolved").</item>
///   <item><c>IsHealthy == true</c> ⇒ Healthy ("identity+schema resolved").</item>
/// </list>
/// The skeleton returns Unhealthy("NOT IMPLEMENTED") for both, so the Healthy case + the description asserts
/// FAIL (RED). No Redis/RMQ touched — the context is a caller-settable <see cref="FakeProcessorContext"/>
/// bridged through a stub provider (GetRequiredService&lt;T&gt; ⇒ GetService(typeof(T)) under the hood).
/// </summary>
[Trait("Phase", "86")]
public sealed class IdentitySchemaReadyHealthCheckTests
{
    private static Task<HealthCheckResult> RunAsync(bool isHealthy)
    {
        var context = new FakeProcessorContext { IsHealthy = isHealthy };
        var sp = Substitute.For<IServiceProvider>();
        sp.GetService(typeof(IProcessorContext)).Returns(context);
        return new ProcessorIdentitySchemaReadyHealthCheck(sp)
            .CheckHealthAsync(new HealthCheckContext());
    }

    [Fact]
    public async Task NotHealthy_Reports_Unhealthy_IdentitySchemaNotResolved()
    {
        var result = await RunAsync(isHealthy: false);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("identity+schema not yet resolved", result.Description);
    }

    [Fact]
    public async Task Healthy_Reports_Healthy_IdentitySchemaResolved()
    {
        var result = await RunAsync(isHealthy: true);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("identity+schema resolved", result.Description);
    }
}
