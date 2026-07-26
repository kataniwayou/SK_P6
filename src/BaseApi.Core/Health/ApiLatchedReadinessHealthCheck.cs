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
    // Static-literal latched message (mirror of BaseConsole.Core LatchedReadinessHealthCheck).
    private const string LatchedMessage =
        "readiness latched (sustained dependency failure — restart required)";

    // Static-literal PRE-latch message (CR-01): the inner check's raw Description/Exception is NEVER
    // forwarded — not even on the 1..threshold-1 consecutive-failure polls before the latch trips. The
    // wrapped baseapi Postgres check is the third-party NpgSqlHealthCheck, which attaches the raw
    // NpgsqlException (host/port/db/auth detail); returning this static literal on every Unhealthy path
    // keeps the info-disclosure guard intact regardless of which inner check is wrapped.
    private const string UnhealthyMessage =
        "readiness dependency unhealthy";

    private readonly IHealthCheck _inner;
    private readonly int _failureThreshold;

    // IN-03 (single-prober assumption): the Interlocked Increment/Exchange on _consecutiveFailures prevent
    // torn reads, but they are NOT a transactional read-modify-write across the whole check. This is exact
    // under the standard single-kubelet-prober model (probes are sequential). If multiple concurrent probers
    // ever poll /health/ready simultaneously, a stale-Healthy Exchange(0) could interleave with a failing
    // Increment and transiently erase progress toward the latch — revisit the counter design if that happens.
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

            // CR-01: NEVER forward the inner result verbatim — its Description/Exception can carry raw
            // driver detail. Return a STATIC pre-latch literal instead (the latch counter math is unchanged).
            return HealthCheckResult.Unhealthy(UnhealthyMessage);
        }

        // Any non-Unhealthy result resets the consecutive counter — a transient blip does not latch.
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        return result;
    }
}
