using BaseApi.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace BaseApi.Core.DependencyInjection;

/// <summary>
/// Phase 12 Redis L2 client wiring: <see cref="IConnectionMultiplexer"/> Singleton
/// (INFRA-COMP-02) + <see cref="RedisProjectionOptions"/> binding (INFRA-COMP-04).
/// <see cref="IDatabase"/> is NOT registered — consumers (Phase 15 RedisProjectionWriter)
/// call <c>multiplexer.GetDatabase()</c> per operation per INFRA-COMP-03 (canonical
/// StackExchange.Redis pattern; IDatabase is a lightweight pass-thru that does not
/// need to be stored).
///
/// <para>
/// CONTEXT D-14: Synchronous <see cref="ConnectionMultiplexer.Connect(string, System.IO.TextWriter)"/> is safe
/// inside the Singleton factory closure because the locked connection string carries
/// <c>abortConnect=false</c> (INFRA-REDIS-04, enforced in appsettings by Plan 12-03) —
/// boot never crashes on a dead Redis. The multiplexer materializes even if Redis is
/// unreachable; subsequent operations throw <c>RedisConnectionException</c> at
/// SetAsync/KeyExistsAsync call sites (ORCH-START-04 / ORCH-STOP-07; Phase 15 wires).
/// </para>
///
/// <para>
/// CONTEXT D-05: This extension does NOT probe Redis at startup. PERSIST-10 +
/// HEALTH-01..05 contracts preserved verbatim; <c>StartupCompletionService</c> is not
/// extended by this plan.
/// </para>
///
/// <para>
/// CONTEXT D-06 (SUPERSEDED by Phase 86 — HLTH-04/05/08): the old claim that "this extension does
/// NOT register a Redis health check" and "Redis down ⇒ /health/ready 200" no longer holds. Redis is
/// now a REQUIRED, LATCHED readiness dependency: <c>AddBaseApiHealth</c> registers
/// <see cref="Health.ApiRedisReadyHealthCheck"/> (tagged "ready") wrapped in a per-process
/// <see cref="Health.ApiLatchedReadinessHealthCheck"/>, so a sustained Redis outage flips
/// <c>/health/ready</c> to Unhealthy (503) and stays latched until restart (no self-heal).
/// <c>/health/live</c> is unaffected. This extension itself still only wires the multiplexer +
/// options; the readiness check lives in <c>HealthServiceCollectionExtensions</c>.
/// </para>
///
/// <para>
/// CONTEXT D-17: <see cref="ConnectionMultiplexer.Connect(string, System.IO.TextWriter)"/> fires lazily at
/// first <see cref="IConnectionMultiplexer"/> resolution — no pre-warm IHostedService.
/// </para>
/// </summary>
internal static class RedisServiceCollectionExtensions
{
    internal static IServiceCollection AddBaseApiRedis(
        this IServiceCollection services, IConfiguration cfg)
    {
        // WR-03 fail-fast helper (RequireConnectionString from BaseApi.Core/Configuration/
        // RequiredConfig.cs) — mirrors AddBaseApiPersistence and AddBaseApiHealth.
        var connStr = cfg.RequireConnectionString("Redis");

        // D-14 / INFRA-COMP-02: Singleton lifetime is the StackExchange.Redis
        // maintainer-blessed pattern. ConnectionMultiplexer is thread-safe and designed
        // for long-lived reuse; per-request construction defeats the multiplexing model
        // (PITFALLS P1 — connection storm).
        // D-17: Lazy first-resolution — no pre-warm IHostedService.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(connStr));

        // D-16 / INFRA-COMP-04: bind Redis:* section to RedisProjectionOptions.
        // KeyPrefix and Serialization.JsonOptions are the only fields; YAGNI-pruned
        // per D-15 (no Database int, no CommandFlags, no ConnectionString property).
        services.Configure<RedisProjectionOptions>(cfg.GetSection("Redis"));

        return services;
    }
}
