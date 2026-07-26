using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace BaseApi.Core.Health;

/// <summary>
/// WebApi Redis readiness probe (HLTH-04/08) — a hand-rolled, bounded, NEVER-throw
/// <see cref="IHealthCheck"/>. The BaseApi.Core mirror of
/// <c>BaseConsole.Core.Health.RedisReadyHealthCheck</c>; kept in this assembly because baseapi owns its
/// OWN health pattern and does NOT reference BaseConsole.Core.
///
/// <para>
/// Constructed with the OUTER <see cref="IServiceProvider"/> and resolves the singleton
/// <see cref="IConnectionMultiplexer"/> AT CHECK TIME (never captured at registration — RESEARCH
/// Pitfall 4). Wrapped by <see cref="ApiLatchedReadinessHealthCheck"/> in the readiness chain so a
/// sustained Redis failure eventually latches (HLTH-05). Before this plan Redis was on NO probe
/// (superseded CONTEXT D-06 "Redis down ⇒ /health/ready 200").
/// </para>
///
/// <para>
/// <b>Contract:</b>
/// <list type="bullet">
///   <item><c>IConnectionMultiplexer</c> unresolved (null) ⇒ Unhealthy("Redis not started") — readiness
///   never reports a stale-Healthy state.</item>
///   <item>bounded <c>GetDatabase().PingAsync()</c> completes ⇒ Healthy.</item>
///   <item>any exception ⇒ Unhealthy("Redis unreachable"); the check NEVER throws out of
///   <see cref="CheckHealthAsync"/>.</item>
/// </list>
/// The ping is bounded by a linked CTS with a ~2s deadline so a dead Redis can never hang the probe.
/// </para>
///
/// <para>
/// <b>Info-disclosure guard (T-86-09 / T-18-08):</b> messages are STATIC literals — the connection string
/// and the raw exception detail are NEVER placed in the result message or Data.
/// </para>
/// </summary>
public sealed class ApiRedisReadyHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;

    public ApiRedisReadyHealthCheck(IServiceProvider outer) => _outer = outer;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Resolve the multiplexer AT CHECK TIME (never captured at registration — Pitfall 4). Unresolved
        // (null) ⇒ Unhealthy, so readiness never reports a stale-Healthy state before Redis is up.
        // NOTE (WR-05 reverted): resolution is intentionally OUTSIDE the ping budget. The singleton factory's
        // first-time synchronous ConnectionMultiplexer.Connect can legitimately take longer than the 2s ping
        // window against a REACHABLE-but-cold Redis; bounding it into that window turned a slow-but-successful
        // connect into a false NotReady (and risked latching). The ping itself stays bounded below.
        var mux = _outer.GetService<IConnectionMultiplexer>();
        if (mux is null)
        {
            return HealthCheckResult.Unhealthy("Redis not started");
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(2)); // bounded — a dead Redis can never hang the probe
            await mux.GetDatabase().PingAsync().WaitAsync(cts.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // STATIC literal only — the connection string and raw exception detail NEVER leak (T-86-09).
            return HealthCheckResult.Unhealthy("Redis unreachable");
        }
    }
}
