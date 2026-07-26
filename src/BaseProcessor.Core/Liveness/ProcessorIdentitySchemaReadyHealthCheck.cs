using BaseProcessor.Core.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseProcessor.Core.Liveness;

/// <summary>
/// Processor identity+schema READINESS probe (HLTH-04). BaseProcessor-specific readiness signal folded into
/// <c>/health/ready</c>: the processor is only Ready once its identity + all required schema definitions have
/// resolved (the same condition the orchestration-start liveness gate keys off). Mirrors
/// <see cref="BaseConsole.Core.Health.BusReadyHealthCheck"/>: constructed with the OUTER
/// <see cref="IServiceProvider"/> and resolves the singleton <see cref="IProcessorContext"/> AT CHECK TIME
/// (never captured at registration — RESEARCH Pitfall 4).
///
/// <para>
/// <b>LOCKED contract (Pattern 4 / RESEARCH Assumption A1):</b> the one volatile-safe, never-throw signal that
/// "identity + all required definitions resolved" is exactly <see cref="IProcessorContext.IsHealthy"/>
/// (<c>Volatile.Read</c>). The mapping is:
/// <list type="bullet">
///   <item><c>IsHealthy == true</c> ⇒ Healthy ("identity+schema resolved").</item>
///   <item><c>IsHealthy == false</c> ⇒ Unhealthy ("identity+schema not yet resolved").</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Do NOT read <see cref="IProcessorContext.Id"/> / <see cref="IProcessorContext.InputDefinition"/> / etc.
/// directly (WR-03 stale-null hazard).</b> Those identity/definition properties carry no memory-visibility
/// barrier and are only safe to read from another thread AFTER observing <c>IsHealthy == true</c>; a health
/// check runs on an arbitrary listener thread, so it can only trust the one synchronized field.
/// <see cref="IProcessorContext.IsHealthy"/> means precisely the readiness condition, so it is the sole read.
/// </para>
///
/// <para>
/// <b>Info-disclosure guard.</b> Both result descriptions are STATIC literals — no processor id, connection
/// string, or infra detail ever leaks into the readiness body.
/// </para>
///
/// <para>
/// Registered as a <c>ready</c>-tagged <c>HealthCheckDescriptor(Name:"identity-schema-ready", …,
/// Factory: outer =&gt; new ProcessorIdentitySchemaReadyHealthCheck(outer))</c> in a later wave.
/// </para>
/// </summary>
public sealed class ProcessorIdentitySchemaReadyHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;

    /// <param name="outer">
    /// The OUTER host's service provider. The singleton <see cref="IProcessorContext"/> is resolved from here
    /// at check time (it lives in the outer container, not the inner listener container).
    /// </param>
    public ProcessorIdentitySchemaReadyHealthCheck(IServiceProvider outer) => _outer = outer;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // RED skeleton (Wave-0): the readiness mapping is pinned by IdentitySchemaReadyHealthCheckTests but
        // not yet implemented. The real body resolves IProcessorContext from _outer at check time and maps
        // IsHealthy -> Healthy("identity+schema resolved") / false -> Unhealthy("identity+schema not yet
        // resolved"); it never reads Id/InputDefinition/etc (WR-03). Implemented in a later wave.
        _ = _outer;
        return Task.FromResult(HealthCheckResult.Unhealthy("NOT IMPLEMENTED"));
    }
}
