using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseConsole.Core.Health;

/// <summary>
/// Readiness latch decorator (HLTH-05). A thin <see cref="IHealthCheck"/> wrapping a required-dependency
/// check; it counts consecutive Unhealthy evaluations and, once the count reaches
/// <c>failureThreshold</c>, flips a per-process STICKY latch that returns Unhealthy forever (until process
/// restart) — even if the inner check later recovers (RESEARCH Pattern 5, Pitfall 4 sticky-after-recovery).
/// A single non-Unhealthy result before the threshold resets the counter (transient blips do not latch).
///
/// <para>
/// <b>Keys off consecutive failed evaluations</b> (spec §6) — it rides the kubelet's own
/// <c>periodSeconds × failureThreshold</c> polling cadence. The latch instance MUST be per-process
/// (constructed once in the descriptor factory / registered as a singleton) so its state persists across
/// probe polls — do NOT <c>new</c> it per check. Wrap the console bus-ready + Redis checks (and the api
/// Postgres + Redis checks); do NOT wrap the soft/Degraded bus check on baseapi.
/// </para>
///
/// <para>
/// <b>Intended implementation (86-04 GREEN):</b> an <c>int _consecutiveFailures</c> +
/// <c>volatile bool _latched</c>; short-circuit Unhealthy when latched; on an inner Unhealthy
/// <c>Interlocked.Increment</c> and set <c>_latched</c> at the threshold; on any non-Unhealthy
/// <c>Interlocked.Exchange(ref _consecutiveFailures, 0)</c>.
/// </para>
///
/// <para><b>RED skeleton:</b> <see cref="CheckHealthAsync"/> passes straight through to the inner check
/// with NO latch, so the sticky-after-recovery test fails RED (the inner recovering flips the wrapper back
/// to Healthy, which the locked behavior forbids). The latch logic lands in 86-04; the ctor already pins
/// the locked signature.</para>
/// </summary>
public sealed class LatchedReadinessHealthCheck : IHealthCheck
{
    // Static-literal latched message (T-86-06): no dependency detail is ever surfaced.
    private const string LatchedMessage =
        "readiness latched (sustained dependency failure — restart required)";

    private readonly IHealthCheck _inner;
    private readonly int _failureThreshold;

    // Per-process sticky state: MUST persist across probe polls, so this instance is a per-process
    // singleton constructed ONCE by the caller (never per request — RESEARCH Pitfall 4).
    private int _consecutiveFailures;
    private volatile bool _latched;

    public LatchedReadinessHealthCheck(IHealthCheck inner, int failureThreshold)
    {
        _inner = inner;
        _failureThreshold = failureThreshold;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Once latched, never call the inner check again — the pod stays NotReady until an operator
        // restart, defeating MassTransit/Redis auto-reconnect self-heal (HLTH-05, T-86-07).
        if (_latched)
        {
            return HealthCheckResult.Unhealthy(LatchedMessage);
        }

        var result = await _inner.CheckHealthAsync(context, cancellationToken).ConfigureAwait(false);

        if (result.Status == HealthStatus.Unhealthy)
        {
            // Count CONSECUTIVE failed evaluations; latch once the threshold is reached.
            if (Interlocked.Increment(ref _consecutiveFailures) >= _failureThreshold)
            {
                _latched = true;
                return HealthCheckResult.Unhealthy(LatchedMessage);
            }

            return result;
        }

        // Any non-Unhealthy result resets the consecutive counter — a transient blip does not latch.
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        return result;
    }
}
