using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BaseConsole.Core.Health;

/// <summary>
/// Redis readiness probe (HLTH-04/08) — a hand-rolled, bounded, NEVER-throw <see cref="IHealthCheck"/>.
/// Mirrors <see cref="BusReadyHealthCheck"/>: constructed with the OUTER <see cref="IServiceProvider"/> and
/// resolves the singleton <see cref="IConnectionMultiplexer"/> AT CHECK TIME (never captured at
/// registration — RESEARCH Pitfall 4). Wrapped by <see cref="LatchedReadinessHealthCheck"/> in the
/// console/api readiness chain so sustained Redis failure eventually latches (HLTH-05).
///
/// <para>
/// <b>Intended contract (86-04 GREEN):</b>
/// <list type="bullet">
///   <item><c>IConnectionMultiplexer</c> unresolved (null) ⇒ Unhealthy("Redis not started") — readiness
///   never reports a stale-Healthy state.</item>
///   <item>bounded <c>GetDatabase().PingAsync()</c> completes ⇒ Healthy.</item>
///   <item>any exception ⇒ Unhealthy("Redis unreachable"); the check NEVER throws out of
///   <see cref="CheckHealthAsync"/>.</item>
/// </list>
/// The ping is bounded (a linked CTS with a ~2s deadline, or the multiplexer's own connect/sync timeouts)
/// so a dead Redis can never hang the probe.
/// </para>
///
/// <para>
/// <b>Info-disclosure guard (T-86-01 / T-18-08):</b> messages are STATIC literals — the connection string
/// and the raw exception detail are NEVER placed in the result message or Data.
/// </para>
///
/// <para><b>RED skeleton:</b> <see cref="CheckHealthAsync"/> returns a static Unhealthy("NOT IMPLEMENTED")
/// so every behavioral assertion (null / healthy-ping / throwing-mux) fails RED. The resolve-ping-map
/// logic lands in 86-04; the ctor already pins the locked signature.</para>
/// </summary>
public sealed class RedisReadyHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;

    public RedisReadyHealthCheck(IServiceProvider outer) => _outer = outer;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Captured for 86-04 (resolve IConnectionMultiplexer from _outer at check time, bounded PING,
        // any fault → Unhealthy, never throw); not yet consulted in the RED skeleton.
        _ = _outer;
        return Task.FromResult(HealthCheckResult.Unhealthy("NOT IMPLEMENTED"));
    }
}
