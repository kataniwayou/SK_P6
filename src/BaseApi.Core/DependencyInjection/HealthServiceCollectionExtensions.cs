using BaseApi.Core.Configuration;
using BaseApi.Core.Health;
using HealthChecks.NpgSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 5 health wiring: IStartupGate singleton + AddHealthChecks chain (self/live, startup,
/// ready) + StartupCompletionService hosted service that flips the gate on host start.
///
/// <para>
/// <b>Phase 86 (HLTH-04/05/08):</b> the "ready" tag now carries the Postgres AND Redis required
/// dependencies, each wrapped in a per-process singleton <see cref="ApiLatchedReadinessHealthCheck"/>
/// (a sticky no-self-heal latch — sustained failure ⇒ /health/ready Unhealthy until restart, never
/// a self-healed leak of a dead pod back to Ready). Redis was previously on NO probe (superseded
/// CONTEXT D-06). The bus check (MessagingServiceCollectionExtensions,
/// <c>MinimalFailureStatus=Degraded</c>) is a soft publish-only dep and is deliberately NOT latched.
/// </para>
/// </summary>
internal static class HealthServiceCollectionExtensions
{
    // Default readiness failure threshold — matches the k8s baseapi readinessProbe.failureThreshold
    // (k8s/30-baseapi-service.yaml). The latch flips after this many consecutive Unhealthy evaluations,
    // riding the kubelet's own periodSeconds × failureThreshold cadence.
    private const int DefaultReadinessFailureThreshold = 5;

    private const string PostgresLatchKey = "baseapi-ready-postgres";
    private const string RedisLatchKey = "baseapi-ready-redis";

    internal static IServiceCollection AddBaseApiHealth(
        this IServiceCollection services, IConfiguration cfg)
    {
        services.AddSingleton<IStartupGate, StartupGate>();

        // WR-03: fail fast with a clear "connection string missing" message rather than letting null
        // propagate into the NpgSql check → health-check library NRE.
        var postgresConnStr = cfg.RequireConnectionString("Postgres");
        var failureThreshold =
            cfg.GetValue<int?>("Health:ReadinessFailureThreshold") ?? DefaultReadinessFailureThreshold;

        // Redis readiness check — resolves IConnectionMultiplexer from the ROOT provider at check time
        // (Pitfall 4); registered as a singleton so the reference is stable and never disposed.
        services.AddSingleton(sp => new ApiRedisReadyHealthCheck(sp));

        // Per-process singleton latches (Pitfall 4: the latch state MUST persist across probe polls, so
        // it is NEVER new'd per check). Keyed by dependency so the two wrappers of the same type each get
        // their own cached instance.
        services.AddKeyedSingleton(PostgresLatchKey, (sp, _) => new ApiLatchedReadinessHealthCheck(
            new NpgSqlHealthCheck(new NpgSqlHealthCheckOptions(postgresConnStr)),
            failureThreshold));
        services.AddKeyedSingleton(RedisLatchKey, (sp, _) => new ApiLatchedReadinessHealthCheck(
            sp.GetRequiredService<ApiRedisReadyHealthCheck>(),
            failureThreshold));

        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
            .AddCheck<StartupHealthCheck>("startup", tags: new[] { "startup", "ready" })
            // Postgres readiness — latched (HLTH-05). The factory resolves the per-process singleton latch,
            // so the sticky state survives every probe poll. Name kept as "npgsql" (the prior AddNpgSql
            // default) to preserve the /health/ready body contract asserted by HealthEndpointsTests.
            .Add(new HealthCheckRegistration(
                "npgsql",
                sp => sp.GetRequiredKeyedService<ApiLatchedReadinessHealthCheck>(PostgresLatchKey),
                failureStatus: null,
                tags: new[] { "ready" }))
            // Redis readiness — latched (HLTH-04/05/08). Redis is now a required readiness dependency.
            .Add(new HealthCheckRegistration(
                "redis",
                sp => sp.GetRequiredKeyedService<ApiLatchedReadinessHealthCheck>(RedisLatchKey),
                failureStatus: null,
                tags: new[] { "ready" }));

        services.AddHostedService<StartupCompletionService>();
        return services;
    }
}
