using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.Health;

/// <summary>
/// WebApi readiness latch decorator (HLTH-05) — the BaseApi.Core mirror of
/// <c>BaseConsole.Core.Health.LatchedReadinessHealthCheck</c>. A thin <see cref="IHealthCheck"/> wrapping a
/// required-dependency check; it counts consecutive Unhealthy evaluations and, once the count reaches
/// <c>failureThreshold</c>, flips a per-process STICKY latch that returns Unhealthy forever (until process
/// restart) — even if the inner check later recovers (RESEARCH Pattern 5, Pitfall 4 sticky-after-recovery).
/// A single non-Unhealthy result before the threshold resets the counter (a transient blip does not latch).
///
/// <para>
/// <b>Keys off consecutive failed evaluations</b> — it rides the kubelet's own
/// <c>periodSeconds × failureThreshold</c> polling cadence. The latch instance MUST be per-process
/// (registered as a singleton) so its state persists across probe polls — do NOT <c>new</c> it per check.
/// Wrap the baseapi Postgres + Redis readiness checks; do NOT wrap the soft/Degraded bus check (it stays
/// unlatched — T-86-11 accepted, publish-only soft dep).
/// </para>
/// </summary>
public sealed class ApiLatchedReadinessHealthCheck : IHealthCheck
{
    private readonly IHealthCheck _inner;
    private readonly int _failureThreshold;
    private int _consecutiveFailures;
    private volatile bool _latched;

    public ApiLatchedReadinessHealthCheck(IHealthCheck inner, int failureThreshold)
    {
        _inner = inner;
        _failureThreshold = failureThreshold;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Once latched, stay Unhealthy forever (restart-only recovery — no self-heal, T-86-10).
        if (_latched)
        {
            return HealthCheckResult.Unhealthy("readiness latched (sustained dependency failure — restart required)");
        }

        var result = await _inner.CheckHealthAsync(context, cancellationToken);

        if (result.Status == HealthStatus.Unhealthy)
        {
            if (Interlocked.Increment(ref _consecutiveFailures) >= _failureThreshold)
            {
                _latched = true;
            }
        }
        else
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0); // a transient blip resets the counter
        }

        return _latched
            ? HealthCheckResult.Unhealthy("readiness latched (sustained dependency failure — restart required)")
            : result;
    }
}
