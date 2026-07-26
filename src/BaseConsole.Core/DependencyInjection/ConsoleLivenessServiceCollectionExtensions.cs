using BaseConsole.Core.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BaseConsole.Core.DependencyInjection;

/// <summary>
/// Opt-in registration seam for the shared loop-liveness self-watchdog (HLTH-01/06). A console that runs a
/// critical loop calls <see cref="AddConsoleLivenessWatchdog"/> once at startup with its loop interval; this
/// registers the shared <see cref="ILivenessHeartbeat"/> holder (the loop <c>Beat()</c>s it each iteration)
/// plus a <c>"live"</c>-tagged <see cref="HealthCheckDescriptor"/> that
/// <see cref="EmbeddedHealthEndpointService"/> auto-folds onto <c>/health/live</c> — NO change to the embedded
/// listener wiring. Generalizes the two divergent per-service watchdogs (keeper=5s, processor=10s) into one
/// primitive with a k=3 staleness grace.
///
/// <para>
/// <b>Self-only stays self-only.</b> A service that NEVER calls this (the orchestrator) surfaces only the
/// dependency-independent <c>"self"</c> live check registered by <c>AddBaseConsoleHealth</c> — its
/// <c>/health/live</c> never reacts to a stalled loop because it registers no loop watchdog.
/// </para>
///
/// <para>
/// Deliberately a NEW file (not folded into <see cref="ConsoleHealthServiceCollectionExtensions"/>) to avoid a
/// Wave-1 edit conflict with 86-04, which touches the readiness side of that class.
/// </para>
/// </summary>
public static class ConsoleLivenessServiceCollectionExtensions
{
    /// <summary>
    /// Register the shared liveness-watchdog primitive on the OUTER container.
    /// </summary>
    /// <param name="services">the outer Generic-Host service collection.</param>
    /// <param name="intervalSeconds">the critical loop's beat interval, baked at registration (keeper=5, processor=10).</param>
    /// <param name="k">the staleness grace factor: stale iff <c>now &gt;= Current + k*intervalSeconds</c> (default 3).</param>
    public static IServiceCollection AddConsoleLivenessWatchdog(
        this IServiceCollection services, int intervalSeconds, int k = 3)
    {
        // Idempotent — mirrors the keeper/orchestrator TimeProvider.System registration; the single time source
        // for both the heartbeat holder's Beat() and the watchdog's check-time now.
        services.TryAddSingleton(TimeProvider.System);

        // The shared holder the critical loop Beat()s each iteration; the watchdog reads its Current.
        services.AddSingleton<ILivenessHeartbeat, LivenessHeartbeat>();

        // "live"-tagged descriptor: EmbeddedHealthEndpointService folds it onto /health/live automatically
        // (its Predicate = Tags.Contains("live")). Interval + k baked here; the check resolves the holder +
        // clock from the OUTER provider at check time (never captured at registration).
        services.AddSingleton(new HealthCheckDescriptor(
            "liveness-watchdog",
            new[] { "live" },
            outer => new LoopLivenessHealthCheck(outer, intervalSeconds, k)));

        return services;
    }
}
