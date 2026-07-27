using BaseConsole.Core.Configuration;
using BaseConsole.Core.DependencyInjection;
using BaseConsole.Core.Health;
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Liveness;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Processing;
using BaseProcessor.Core.Startup;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;

namespace BaseProcessor.Core.DependencyInjection;

/// <summary>
/// The processor composition root (BPC-03). A single <c>AddBaseProcessor(cfg)</c> call folds the
/// BaseConsole.Core infra stack (<c>AddBaseConsole</c> — Redis soft-dep + embedded health) and the
/// bus skeleton (<c>AddBaseConsoleMessaging</c> — RabbitMQ + the three correlation filters), then
/// layers the processor's own runtime brain on top:
/// <list type="bullet">
///   <item>the two <c>IRequestClient</c>s targeting the WebApi responder endpoints on the
///   <c>exchange:{name}</c> scheme (RPC-04) — registered in the <c>configureConsumers</c> lambda;</item>
///   <item><see cref="ProcessorLivenessOptions"/> bound from the <c>"Processor"</c> section (CONFIG-01);</item>
///   <item><c>TimeProvider.System</c> (idempotent), <see cref="ISourceHashProvider"/>,
///   <see cref="IProcessorContext"/>, and the <see cref="ProcessorStartupOrchestrator"/> hosted service;</item>
///   <item>and the REMOVAL of the base library's <c>StartupCompletionService</c> so
///   <see cref="Startup.ProcessorStartupOrchestrator"/> is the one that would fire <c>MarkReady</c> when
///   the processor reaches Healthy (identity + definitions resolved), NOT at bare host start (D-02 —
///   mirrors <c>Orchestrator/Program.cs</c>). NOTE (Phase 86 / HLTH-06): the EFFECTIVE first
///   <c>IStartupGate.MarkReady</c> now comes from <see cref="Liveness.ProcessorLivenessHeartbeat"/>'s
///   UNCONDITIONAL first beat (within ms of host start, independent of <c>IsHealthy</c> / bus
///   connectivity), which — running concurrently as a hosted service — wins the race against the
///   orchestrator's own post-Healthy <c>MarkReady</c>. Because <c>MarkReady</c> is a one-way idempotent
///   latch, the orchestrator's calls are now redundant/defensive for <c>/health/startup</c>, not the sole
///   gate. <c>/health/ready</c> is UNAFFECTED — it stays independently gated by the un-latched
///   identity+schema readiness check reading <c>IsHealthy</c>.</item>
/// </list>
///
/// <para>
/// <b>Dispatch consumer (Phase 27 / EXEC-01).</b> The <see cref="EntryStepDispatchConsumer"/> IS
/// registered here for DI (so the runtime <c>ConnectReceiveEndpoint</c> can <c>ConfigureConsumer&lt;T&gt;</c>
/// at bind time), but it is EXCLUDED from the unconditional <c>ConfigureEndpoints(ctx)</c> inside
/// <c>AddBaseConsoleMessaging</c> via <c>.ExcludeFromConfigureEndpoints()</c> — otherwise a wrong-named
/// kebab <c>entry-step-dispatch</c> queue would be auto-bound at bus start (Pitfall 1). The correct
/// durable <c>{id:D}</c> endpoint is bound at runtime by <see cref="ProcessorStartupOrchestrator"/>,
/// AFTER identity resolves and BEFORE <c>MarkHealthy</c> (D-01/D-02/D-03). The
/// <see cref="ProcessorLivenessHeartbeat"/> hosted service IS registered here (step 7b).
/// </para>
///
/// <para>
/// Observability (metrics-only OTel) stays a separate <c>AddBaseConsoleObservability</c> call on
/// <c>IHostApplicationBuilder</c> in the concrete <c>Program.cs</c> (it needs <c>ILoggingBuilder</c>),
/// mirroring the BaseConsole three-call seam.
/// </para>
/// </summary>
public static class BaseProcessorServiceCollectionExtensions
{
    public static IServiceCollection AddBaseProcessor(this IServiceCollection services, IConfiguration cfg)
    {
        // 1. BaseConsole infra: Redis soft-dep multiplexer + embedded minimal-Kestrel health surface.
        services.AddBaseConsole(cfg);

        // 2. Bus skeleton + the two request clients (RPC-04). The clients go in the configureConsumers
        //    lambda; they target exchange:{ProcessorQueues.name} so the request routes to the WebApi's
        //    named ReceiveEndpoint (confirmed Wave 0 — 26-01-SUMMARY). The EntryStepDispatch consumer
        //    is registered here for DI but EXCLUDED from auto-endpoint config (the {id:D} endpoint is
        //    bound at runtime by ProcessorStartupOrchestrator — Phase 27 / EXEC-01). The
        //    ProcessorLivenessHeartbeat hosted service IS registered this phase — step 7b.
        services.AddBaseConsoleMessaging(cfg,
            x =>
            {
                x.AddRequestClient<GetProcessorBySourceHash>(new Uri("exchange:" + ProcessorQueues.IdentityQuery));
                x.AddRequestClient<GetSchemaDefinition>(new Uri("exchange:" + ProcessorQueues.SchemaQuery));

                // EXEC-01 / D-01: register the dispatch consumer for DI (so the runtime
                // ConnectReceiveEndpoint can ConfigureConsumer<T> at bind time) but EXCLUDE it from the
                // UNCONDITIONAL ConfigureEndpoints(ctx) inside AddBaseConsoleMessaging — otherwise a
                // wrong-named kebab "entry-step-dispatch" queue is auto-created at bus start (Pitfall 1).
                // The correct durable {id:D} endpoint is bound at runtime by ProcessorStartupOrchestrator,
                // AFTER identity resolves and BEFORE MarkHealthy (D-02/D-03).
                x.AddConsumer<EntryStepDispatchConsumer>().ExcludeFromConfigureEndpoints();

                // Phase 70 (D-15/D-16): the Post-Process consumer — registered for DI so the runtime
                // ConnectReceiveEndpoint can ConfigureConsumer<PostProcessConsumer> on queue:{id:D}-post,
                // EXCLUDED from auto-endpoint config (the -post endpoint is bound at runtime by
                // ProcessorStartupOrchestrator, AFTER identity resolves and BEFORE MarkHealthy).
                x.AddConsumer<PostProcessConsumer>().ExcludeFromConfigureEndpoints();
            });

        // 2b. PIPE-01 (Phase 44): the Pre→In→Post→end-delete runner the now-thin EntryStepDispatchConsumer
        //     delegates to. Scoped per dispatch consume (mirrors the consumer's per-message resolution); its
        //     collaborators (IConnectionMultiplexer, IProcessorContext, BaseProcessor, ISendEndpointProvider,
        //     IOptions<RetryOptions>, ILogger) are all already registered by the calls above / step 3b.
        //
        // WR-02 — BaseProcessor lifetime contract: the `BaseProcessor` author-transform seam is NOT
        //     registered by AddBaseProcessor; the concrete author's Program.cs MUST register it. Because this
        //     pipeline is Scoped, the author MUST register BaseProcessor as Singleton (the expected choice for
        //     a stateless transform) OR Scoped — NEVER as a stateful Transient/per-call type that holds
        //     per-message state, which would surface a captive-dependency / state-bleed bug at runtime under
        //     the MassTransit consume scope (the hermetic facts `new` the pipeline directly and cannot catch
        //     it). To turn a missing or mis-scoped BaseProcessor registration into a build-time failure rather
        //     than a first-consume crash, the author host SHOULD enable DI scope validation
        //     (ValidateScopes = true / ValidateOnBuild = true) — the .NET Host enables both by default in the
        //     Development environment.
        services.AddScoped<ProcessorPipeline>();

        // 2c. Phase 70 (D-15): the shared output tail (validate → write-gated-on-completed → send-by-result),
        //     resolved by BOTH the ProcessorPipeline inline tail AND the PostProcessConsumer. Scoped, mirroring
        //     the pipeline; its collaborators (IConnectionMultiplexer, IProcessorContext, ISendEndpointProvider,
        //     IOptions<RetryOptions>, IOptions<ProcessorLivenessOptions>, ProcessorMetrics) are all already
        //     registered by the calls above / steps 3/3b/6c.
        services.AddScoped<OutputTail>();

        // 3. Liveness/heartbeat knobs (CONFIG-01) — six independent seconds-ints from the "Processor" section.
        services.Configure<ProcessorLivenessOptions>(cfg.GetSection("Processor"));

        // 3b. D-10: the retry budget, bound per process from the "Retry" section (single source of truth
        //     for the retry Limit consumed by ProcessorStartupOrchestrator's dispatch-endpoint bind, so
        //     Phase 32's final-attempt check cannot desync from UseMessageRetry). Absent section →
        //     RetryOptions defaults (Immediate(3)). Mirrors the ProcessorLivenessOptions bind above.
        services.Configure<RetryOptions>(cfg.GetSection("Retry"));

        // 4. TimeProvider for the backoff/retry clock (idempotent — the base library may not register it;
        //    verbatim Orchestrator/Program.cs:59).
        services.TryAddSingleton(TimeProvider.System);

        // 5. SourceHash seam (IDENT-03) — reflection over the assembly metadata attribute, fail-fast when absent.
        services.AddSingleton<ISourceHashProvider, AssemblyMetadataSourceHashProvider>();

        // 5b. Config-type seam (Phase 57 D-01) — supplies the concrete TConfig type Gate A checks the
        //     fetched config-schema against. The default resolves the author-registered BaseProcessor<TConfig>
        //     ONCE and reads only its generic type argument (process-stable Type; the instance is never held —
        //     RESEARCH Pitfall 4). Singleton, mirrors ISourceHashProvider.
        services.AddSingleton<IConfigTypeProvider, BaseProcessorConfigTypeProvider>();

        // 6. The mutable identity/Healthy holder shared by the orchestrator (writer) and the Phase 03
        //    heartbeat (reader).
        services.AddSingleton<IProcessorContext, ProcessorContext>();

        // 6a. Phase 60 (L1-01 / D-08/09/10): the in-memory L1 liveness holder, updated by BOTH the startup
        //     orchestrator (unhealthy writer) and the heartbeat (healthy writer) every iteration, read by the
        //     Phase-61 self-watchdog probe. Singleton — one volatile-ref-swap record per replica.
        services.AddSingleton<IProcessorLivenessState, ProcessorLivenessState>();

        // 6a''. Phase 86 (HLTH-03/06 / T-86-16): surface liveness on /health/live via the SHARED loop-liveness
        //       watchdog (BaseConsole.Core) instead of the retired processor-specific LivenessWatchdogHealthCheck.
        //       AddConsoleLivenessWatchdog registers the shared ILivenessHeartbeat holder (the Phase-86
        //       ProcessorLivenessHeartbeat Beat()s it every tick) + a "live"-tagged descriptor folding the
        //       LoopLivenessHealthCheck onto /health/live (stale at k=3 × interval). WR-02: derive the interval
        //       from the SAME bound Processor:Interval the ProcessorLivenessHeartbeat itself reads
        //       (IOptions<ProcessorLivenessOptions>.IntervalSeconds) rather than a separately-maintained literal,
        //       so a config override cannot desync the staleness math from the real tick cadence. Falls back to
        //       the ProcessorLivenessOptions default (10) when unset. The retired LivenessWatchdogHealthCheck
        //       descriptor is GONE; IProcessorLivenessState + ProcessorLivenessWriter (6a / 6a' below) are KEPT —
        //       they back the SEPARATE L2 healthy-write gate, NOT liveness (Pitfall 6).
        var livenessIntervalSeconds =
            cfg.GetSection("Processor").Get<ProcessorLivenessOptions>()?.IntervalSeconds
            ?? new ProcessorLivenessOptions().IntervalSeconds;
        services.AddConsoleLivenessWatchdog(intervalSeconds: livenessIntervalSeconds);

        // 6a'''. Phase 86 (HLTH-04): the processor identity+schema READINESS check, folded onto /health/ready via
        //        the generic descriptor seam ("ready" tag). The factory bridges the OUTER provider so the check
        //        resolves the singleton IProcessorContext AT CHECK TIME and maps its one synchronized signal
        //        (IsHealthy) → Healthy/Unhealthy (never reads Id/definition props — WR-03). Redis + latch
        //        readiness are inherited from the 86-04 EmbeddedHealthEndpointService change (no processor code).
        services.AddSingleton(new HealthCheckDescriptor(
            Name: "identity-schema-ready",
            Tags: new[] { "ready" },
            Factory: outer => new ProcessorIdentitySchemaReadyHealthCheck(outer)));

        // 6a'. Phase 60 (LOOP-03/04 / D-09/11/13/15): the single shared liveness write path both loops call
        //      (L2 SET(perInstance, derived TTL) + idempotent index SADD + unconditional L1 Update +
        //      log-and-continue). public sealed → AddSingleton<ProcessorLivenessWriter>() resolves and the
        //      AddBaseProcessorFacts descriptor assert works without InternalsVisibleTo. IConnectionMultiplexer
        //      (its Redis dep) is already registered by AddBaseConsole; IProcessorLivenessState by 6a above.
        services.AddSingleton<ProcessorLivenessWriter>();

        // 6b. LOG-04: the ProcessorId log enricher — a custom OTel BaseProcessor<LogRecord> that appends
        //     ProcessorId from the singleton IProcessorContext.Id to EVERY processor LogRecord (null-safe
        //     — nothing before identity resolves). DI-RESOLVED so it reads the SINGLETON IProcessorContext
        //     (the instance AddProcessor overload cannot resolve DI). Registered ONLY here — processor-side
        //     — never in the shared AddBaseConsoleObservability block (L3: the orchestrator has no
        //     IProcessorContext and would throw at DI resolution). This is purely ADDITIVE: the
        //     IncludeScopes/ParseStateValues/OTLP options stay owned by the unchanged shared block; we only
        //     AddProcessor onto the same logger provider via the DI-resolved AddProcessor<T>() overload.
        services.AddSingleton<ProcessorIdLogEnricher>();
        services.ConfigureOpenTelemetryLoggerProvider((sp, lp) =>
            lp.AddProcessor(sp.GetRequiredService<ProcessorIdLogEnricher>()));

        // 6c. METRIC-05 (Landmine 1 — the compile-firewall fix): the code-owned "BaseProcessor" meter +
        //     its DI-singleton holder. Registered HERE inside AddBaseProcessor — NOT in
        //     BaseConsoleObservabilityExtensions — because BaseConsole.Core has NO project reference to
        //     BaseProcessor.Core (the dependency runs the other way; its only contract ref is
        //     Messaging.Contracts), so it cannot see ProcessorMetrics.MeterName. ConfigureOpenTelemetryMeterProvider
        //     is the exact meter-provider analog of the ConfigureOpenTelemetryLoggerProvider seam above (both
        //     from OpenTelemetry.Extensions.Hosting), attaching the meter additively to the MeterProvider that
        //     the shared AddBaseConsoleObservability built. Every Processor.* inherits this via AddBaseProcessor.
        services.AddSingleton<ProcessorMetrics>();
        services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(ProcessorMetrics.MeterName));

        // 7 / 7b. Phase 60 (D-01/02 / KEY-03): resolve the per-replica instanceId ONCE (env-precedence SoT,
        //         available from boot) and pass the SAME value to BOTH grown ctors so the two loops write the
        //         IDENTICAL {instanceId} per-instance key (one replica identity). The instanceId is a plain
        //         string — NOT container-resolvable — so each background service is registered as a CONCRETE
        //         singleton via an ActivatorUtilities factory that resolves the shared writer (6a') + the rest
        //         from DI and supplies this instanceId, then surfaced as an IHostedService by resolving that
        //         singleton. The concrete-type singleton descriptor (asserted by AddBaseProcessorFacts) keeps
        //         the registration observable even though the factory carries no ImplementationType. This closes
        //         the cross-plan DI gap the writer/heartbeat ctors left.
        var instanceId = InstanceId.Resolve();

        // 7. The two-loop startup orchestrator (identity-by-SourceHash + per-non-null-schema definition). Its
        //    grown ctor now takes the shared ProcessorLivenessWriter + the caller-resolved instanceId, so it
        //    writes the inline `unhealthy` per-instance entry per resolution iteration (STATE-03 / LOOP-01).
        services.AddSingleton(sp =>
            ActivatorUtilities.CreateInstance<ProcessorStartupOrchestrator>(sp, instanceId));
        services.AddHostedService(sp => sp.GetRequiredService<ProcessorStartupOrchestrator>());

        // 7b. The only-when-Healthy liveness heartbeat (LIVE-01..06 / LOOP-02 / D-14): the frozen-healthy beat
        //     now writes the per-instance ProcessorLivenessEntry via the same shared writer (old flat skp:{id}
        //     write gone); registered via the SAME concrete-singleton + hosted-service shape so writer +
        //     instanceId resolve (DI gap closed).
        services.AddSingleton(sp =>
            ActivatorUtilities.CreateInstance<ProcessorLivenessHeartbeat>(sp, instanceId));
        services.AddHostedService(sp => sp.GetRequiredService<ProcessorLivenessHeartbeat>());

        // 8. D-02: remove the base library's StartupCompletionService so MarkReady fires when the
        //    processor reaches Healthy (orchestrator completion), NOT at bare host start. The removal
        //    operates on IServiceCollection so it belongs HERE in the composition root (keeps the
        //    concrete Program.cs minimal — BPC-03). IStartupGate / the self/startup checks stay intact.
        //    COPIED VERBATIM from Orchestrator/Program.cs:63-68 (adapted to `services`).
        foreach (var d in services
                     .Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService))
                     .ToList())
        {
            services.Remove(d);
        }

        return services;
    }

}
