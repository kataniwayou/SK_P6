using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseConsole.Core.Health;

/// <summary>
/// Shared "liveness loop" self-watchdog probe (HLTH-01/06). Generalizes the retired
/// <c>LivenessWatchdogHealthCheck</c> (processor) / <c>KeeperLivenessWatchdogHealthCheck</c> — same
/// staleness math but with the grace factor widened from the old <c>×2</c> to <c>×k</c> (k=3, RESEARCH
/// Pattern 1) to avoid a false-trip on one slow tick under the new unconditional-beat model.
///
/// <para>
/// <b>Check-time OUTER resolution (RESEARCH Pitfall 4 / T-61-06):</b> the implementation MUST resolve the
/// singleton <see cref="ILivenessHeartbeat"/> and <see cref="TimeProvider"/> from the outer provider AT
/// CHECK TIME — never capture them at registration. The embedded health listener runs its own inner DI
/// container, so the outer host state is reached exclusively through the injected outer provider (the
/// <c>BusReadyHealthCheck(_outer)</c> idiom). Registered via
/// <c>HealthCheckDescriptor("liveness-watchdog", ["live"], outer =&gt; new LoopLivenessHealthCheck(outer, interval))</c>.
/// </para>
///
/// <para>
/// <b>Intended behavior (86-03 GREEN):</b>
/// <list type="bullet">
///   <item><c>Current == null</c> ⇒ Unhealthy("liveness loop not started") — the loop crashed before its
///   first beat (steady state never sees this; the K8s startupProbe covers boot).</item>
///   <item><c>now &gt;= Current + k×intervalSeconds</c> ⇒ Unhealthy("liveness loop stale") — the loop went
///   silent (strict <c>&gt;=</c> so the exact-boundary instant is stale, agreeing with the retired watchdog
///   math generalized ×2 → ×k).</item>
///   <item>else ⇒ Healthy("live").</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Info-disclosure guard (T-86-01 / T-18-08):</b> all descriptions are STATIC literals — never an
/// instanceId, connection string, or stack trace in the message or Data.
/// </para>
///
/// <para><b>RED skeleton:</b> <see cref="CheckHealthAsync"/> returns a static Unhealthy("NOT IMPLEMENTED")
/// so every behavioral assertion in the Phase-86 test fails RED (behavior, not compile). The
/// null/stale/fresh/boundary logic lands in 86-03; the ctor already pins the locked signature.</para>
/// </summary>
public sealed class LoopLivenessHealthCheck : IHealthCheck
{
    private readonly IServiceProvider _outer;
    private readonly int _intervalSeconds;
    private readonly int _k;

    public LoopLivenessHealthCheck(IServiceProvider outer, int intervalSeconds, int k = 3)
    {
        _outer = outer;
        _intervalSeconds = intervalSeconds;
        _k = k;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // OUTER check-time resolution (RESEARCH Pitfall 4 / T-61-06): the singletons live in the outer host
        // container, not the inner listener container — never captured at registration.
        var heartbeat = _outer.GetRequiredService<ILivenessHeartbeat>();
        var clock = _outer.GetRequiredService<TimeProvider>();

        var current = heartbeat.Current;
        if (current is null)
        {
            // The loop crashed before its first beat — boot coverage is the future K8s startupProbe.
            return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop not started"));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        // Strict >= so the exact boundary instant (Current + k*interval == now) is stale, matching the
        // retired watchdog's boundary discipline generalized ×2 → ×k (k=3, RESEARCH Pattern 1).
        if (now >= current.Value.AddSeconds(_intervalSeconds * _k))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("liveness loop stale"));
        }

        return Task.FromResult(HealthCheckResult.Healthy("live"));
    }
}
