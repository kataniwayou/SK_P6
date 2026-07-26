using BaseConsole.Core.DependencyInjection;
using BaseConsole.Core.Health;
using MassTransit;
using Messaging.Contracts.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;            // ConfigureOpenTelemetryMeterProvider
using OpenTelemetry.Metrics;    // AddMeter (KeeperMetrics export — D-07)
using Keeper;

// Thin-shell composition root (KEEP-01). Generic Host — Host.CreateApplicationBuilder, NOT
// WebApplication. BaseConsole.Core supplies all infra (metrics-only OTel, Redis soft-dep,
// embedded health, the MassTransit bus + correlation pipeline). Keeper mirrors Orchestrator
// MINUS the scheduler/L1/hydration/metrics runtime block and MINUS the default-readiness-service
// removal — Keeper has no hydration, so readiness flips on bus-start (D-06): the base library's
// default readiness service is KEPT here, not stripped.
var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel (no tracer)
builder.Services.AddBaseConsole(builder.Configuration);       // Redis soft-dep + embedded health + gate/self/startup checks

// HLTH-06 (Phase 86 / D-02 idiom): remove the base library's StartupCompletionService so the keeper marks its
// startup gate ready on the BitHealthLoop's FIRST beat (BitHealthLoop.MarkReady), NOT at bare host start —
// /health/startup now reflects the BIT loop actually ticking. Copied from BaseProcessorServiceCollectionExtensions
// (adapted to builder.Services). IStartupGate + the self/startup checks stay intact.
foreach (var d in builder.Services
             .Where(d => d.ImplementationType == typeof(StartupCompletionService))
             .ToList())
{
    builder.Services.Remove(d);
}

// DLQ-04 / D-09 — bind the shared Immediate(N) retry budget from the "Retry" section (the single
// source of truth the recovery consumer definition's UseMessageRetry reads).
builder.Services.Configure<RetryOptions>(builder.Configuration.GetSection("Retry"));

// PROBE-01 (D-04) — bind the bounded probe-loop knobs. Redis (IConnectionMultiplexer) is ALREADY a
// registered singleton via AddBaseConsole (line above chains the Redis registration) — do NOT add it again.
builder.Services.Configure<ProbeOptions>(builder.Configuration.GetSection("Probe"));

// D-06 — partition count (8), mirrors Probe/Retry. PartitionCount drives the keeper-recovery UsePartitioner
// slot count (the only Recovery knob now that the endpoint is symmetric with the exec path — no bus retry /
// no error transport, so no exhaustion-policy choice).
builder.Services.Configure<RecoveryOptions>(builder.Configuration.GetSection("Recovery"));

// PROBE-01 — the v4 BitHealthLoop's L2 probe helper (stateless; ctor-injects the singleton multiplexer).
builder.Services.AddSingleton<Keeper.Recovery.L2ProbeRecovery>();

// KEEP-03 (D-09): the L2 BIT health gate singleton — written by the BIT loop, read by the Phase-46 consumer.
builder.Services.AddSingleton<Keeper.Health.IL2HealthGate, Keeper.Health.L2HealthGate>();
// KEEP-01/02 (D-06): the proactive BIT loop hosted service (edge-triggered global pause/resume + gate driver).
builder.Services.AddHostedService<Keeper.Health.BitHealthLoop>();

// HLTH-03/06 (Phase 86): opt into the SHARED loop-liveness watchdog (BaseConsole.Core), replacing the retired
// keeper-specific KeeperLivenessWatchdogHealthCheck + IKeeperLivenessState + KeeperLivenessState. Registers the
// singleton ILivenessHeartbeat (BitHealthLoop Beat()s it at the top of each tick) + a "live"-tagged
// HealthCheckDescriptor that EmbeddedHealthEndpointService auto-folds onto /health/live. The watchdog interval
// MUST match the BIT loop's real tick cadence so a silently-stalled BIT loop goes stale at k=3 × interval →
// /health/live Unhealthy. WR-02: derive it from the SAME bound Probe:DelaySeconds the BitHealthLoop itself reads
// (IOptions<ProbeOptions>.DelaySeconds) rather than a separately-maintained literal, so a config override cannot
// desync the staleness math from the tick period. Falls back to the ProbeOptions default (5) when unset.
// IStartupGate is already registered by AddBaseConsole (AddBaseConsoleHealth); TimeProvider.System is TryAdd'd by both.
var probeDelaySeconds = builder.Configuration.GetSection("Probe").Get<ProbeOptions>()?.DelaySeconds
                        ?? new ProbeOptions().DelaySeconds;
builder.Services.AddConsoleLivenessWatchdog(intervalSeconds: probeDelaySeconds);

// KEEP-04 / D-04 (OQ-1): the keeper-recovery endpoint is RUNTIME-BOUND via ConnectReceiveEndpoint
// (RecoveryEndpointBinder), NOT static AddConsumer auto-config — a statically-configured 8.5.5 endpoint
// cannot be runtime-paused (StopAsync removes it). So the three consumers are registered for DI but
// EXCLUDED from auto endpoint config (mirrors BaseProcessorServiceCollectionExtensions: the dispatch
// consumer is AddConsumer().ExcludeFromConfigureEndpoints() and the {id:D} endpoint is bound at runtime).
// Exactly ONE source (the binder) configures keeper-recovery (Pitfall 1 — no static + connect collision).
// The three ConsumerDefinition shells (only ReinjectConsumerDefinition survives, holding the PartitionKey/
// PartitionGuid statics) no longer drive the endpoint — the retry + 3x partitioner + policy branch
// re-homed into the binder's connect callback.
builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
{
    x.AddConsumer<Keeper.Recovery.ReinjectConsumer>().ExcludeFromConfigureEndpoints();
    x.AddConsumer<Keeper.Recovery.InjectConsumer>().ExcludeFromConfigureEndpoints();
    x.AddConsumer<Keeper.Recovery.DeleteConsumer>().ExcludeFromConfigureEndpoints();

    // Phase 71 (req 11): the orchestrator-side recovery trio — same posture as the processor trio above
    // (registered for DI but EXCLUDED from auto endpoint config; the binder owns the single keeper-recovery
    // endpoint). No new options: RecoveryOptions (ExecutionDataTtlSeconds) + RetryOptions are already bound.
    x.AddConsumer<Keeper.Recovery.OrchestratorReinjectConsumer>().ExcludeFromConfigureEndpoints();
    x.AddConsumer<Keeper.Recovery.OrchestratorInjectConsumer>().ExcludeFromConfigureEndpoints();
    x.AddConsumer<Keeper.Recovery.OrchestratorDeleteConsumer>().ExcludeFromConfigureEndpoints();
});

// D-04 (OQ-1): the singleton holding the connected keeper-recovery handle (set by the binder after
// handle.Ready) so Plan 03's BitHealthLoop can Stop/Start the endpoint on L2-health edges; and the binder
// hosted service that runtime-connects the endpoint on the running bus (the connector resolves on the
// MassTransitHostedService-started bus, exactly the processor precedent).
builder.Services.AddSingleton<Keeper.Recovery.RecoveryEndpointHandle>();
builder.Services.AddHostedService<Keeper.Recovery.RecoveryEndpointBinder>();

// D-07 (Plan 01 handoff): the code-owned Keeper meter consumed by the recovery consumers + BitHealthLoop
// (the uniform keeper_messages_consumed / keeper_messages_sent counters + the keeper_l2_probe heartbeat),
// + its OTel meter-provider registration (mirrors ProcessorMetrics' AddSingleton + AddMeter). Without the
// AddSingleton the consumer cannot resolve KeeperMetrics at runtime; without the AddMeter the counters are
// created but never exported.
builder.Services.AddSingleton<Keeper.Observability.KeeperMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(
    mp => mp.AddMeter(Keeper.Observability.KeeperMetrics.MeterName));

var host = builder.Build();
await host.RunAsync();
