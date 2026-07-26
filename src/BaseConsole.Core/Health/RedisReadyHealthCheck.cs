using Microsoft.Extensions.DependencyInjection;
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
    // Bounded-ping deadline (T-86-08): a dead/hung Redis can never freeze /health/ready — the PING is
    // capped so the probe returns Unhealthy within this window even if the multiplexer never responds.
    private static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(2);

    private readonly IServiceProvider _outer;

    public RedisReadyHealthCheck(IServiceProvider outer) => _outer = outer;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // ONE bounded budget for the WHOLE check (WR-05 / T-86-08): both the first-time multiplexer
            // RESOLUTION and the ping share this deadline. The IConnectionMultiplexer singleton factory does a
            // SYNCHRONOUS, blocking ConnectionMultiplexer.Connect on first resolution (no pre-warm hosted
            // service — D-17). If this check is the first component in the process to resolve it, that connect
            // could block PAST this window before the ping deadline even started; bounding the resolution too
            // (offloaded via Task.Run so the blocking connect can never hang the probe) closes that gap.
            using var timeout = new CancellationTokenSource(PingTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            // Resolve the multiplexer from the OUTER provider AT CHECK TIME (never captured at registration —
            // RESEARCH Pitfall 4). A not-yet-connected / absent Redis reads as null → Unhealthy, so readiness
            // never reports a stale-Healthy state (HLTH-04).
            var mux = await Task.Run(() => _outer.GetService<IConnectionMultiplexer>())
                .WaitAsync(linked.Token).ConfigureAwait(false);
            if (mux is null)
            {
                return HealthCheckResult.Unhealthy("Redis not started");
            }

            // StackExchange.Redis 2.13.1 PingAsync has no CancellationToken overload — bound it via the same
            // linked CTS + WaitAsync so a hung Redis cannot hang the probe (T-86-08).
            await mux.GetDatabase().PingAsync().WaitAsync(linked.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Genuine shutdown cancellation is a pass-through per the existing never-throw idiom — a bounded
            // resolution/ping timeout (the linked timeout token) is NOT this branch and falls through to Unhealthy.
            throw;
        }
        catch
        {
            // ANY fault (resolution connect fault/timeout, ping timeout, Redis exception) → Unhealthy, never
            // thrown out. Info-disclosure guard (T-86-06): STATIC literal only — the connection string and the
            // raw exception detail are NEVER placed in the message or Data.
            return HealthCheckResult.Unhealthy("Redis unreachable");
        }
    }
}
