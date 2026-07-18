using BaseConsole.Core.DependencyInjection;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orchestrator.Configuration;
using Orchestrator.Consumers;
using Orchestrator.Dispatch;
using Orchestrator.Election;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Observability;
using Orchestrator.Scheduling;
using OpenTelemetry.Logs;      // ConfigureOpenTelemetryLoggerProvider (role enricher, D-09)
using OpenTelemetry.Metrics;   // ConfigureOpenTelemetryMeterProvider (via OpenTelemetry.Extensions.Hosting)
using Quartz;

// Thin-shell composition root (ORCH-CON-01). Generic Host — Host.CreateApplicationBuilder, NOT
// WebApplication. The base library supplies all infra (observability, Redis soft-dep, embedded
// health, the MassTransit bus + correlation pipeline); this console supplies only its two
// consumers + the per-replica fan-out endpoint.
var builder = Host.CreateApplicationBuilder(args);

builder.AddBaseConsoleObservability(builder.Configuration);   // metrics-only OTel (no tracer — Pitfall 4)
builder.Services.AddBaseConsole(builder.Configuration);       // Redis soft-dep + embedded health

// D-01: the per-replica identity now derives from the k8s downward-API pod name (kubelet-supplied,
// distinct per replica), falling back to the machine name off-cluster. Captured by the closure below so
// EVERY consumer shares the SAME instance id → one temporary/auto-delete fan-out queue
// "orchestrator-{instanceId}" per pod (ORCH-CON-02): a true fan-out broadcast at N>1, not a
// competing-consumer load-balance. This SAME identity feeds the LeaseLock holder in
// LeaderElectionService, so a queue-name collision cannot silently degrade the broadcast (T-82-06).
// (The manifest that supplies POD_NAME + drops Orchestrator__InstanceId lands in Plan 03.)
var instanceId = Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;

// D-02: detect in-cluster once via the kubelet-injected KUBERNETES_SERVICE_HOST. Off-cluster (local
// run + every hermetic test) this is null, so the elector never starts and cannot contend for the
// production Lease (T-82-08). D-07: the LeaderState singleton therefore seeds LEADER off-cluster (the
// lone instance MUST fire) and FOLLOWER in-cluster (pre-acquisition until the elector wins — SPEC HA-02).
var inCluster = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") is not null;
builder.Services.AddSingleton(new LeaderState(startAsLeader: !inCluster));

builder.Services.AddBaseConsoleMessaging(builder.Configuration,
    x =>
    {
        x.AddConsumer<StartOrchestrationConsumer, StartOrchestrationConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<StopOrchestrationConsumer, StopOrchestrationConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        // PAUSE-02/03/04: Pause + Resume share their own dedicated per-replica fan-out endpoint
        // "orchestrator-pauseresume-{instanceId}" (ConcurrentMessageLimit=1, single retry ownership held
        // by the Pause definition) so they don't throttle Start/Stop/Result (RESEARCH §5b).
        x.AddConsumer<PauseWorkflowConsumer, PauseWorkflowConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<ResumeWorkflowConsumer, ResumeWorkflowConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        // ORCH-02 / D-08: global pause/resume on a NEW per-replica fan-out endpoint
        // "orchestrator-global-pauseresume-{instanceId}" (SAME instanceId → one temp fan-out queue per
        // replica; ConcurrentMessageLimit=1, single retry ownership held by the PauseAll definition),
        // independent from "orchestrator-pauseresume" so Phase 48 can drop the old per-workflow endpoint
        // with zero entanglement.
        x.AddConsumer<PauseAllConsumer, PauseAllConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        x.AddConsumer<ResumeAllConsumer, ResumeAllConsumerDefinition>()
            .Endpoint(e => { e.InstanceId = instanceId; e.Temporary = true; });
        // ORCH-01 / D-07: the TypedResultConsumer<T> family — four shared competing-consumers (NO
        // InstanceId/Temporary), the inverse of the Start/Stop fan-out. All four co-locate on the stable
        // "orchestrator-result" endpoint; StepCompletedConsumerDefinition owns the single endpoint-level
        // UseMessageRetry, the other three definitions are intentional no-ops (Pitfall 4). Routing is by
        // message type via each subclass's Outcome knob — no status if/switch. A Keeper-INJECT'd
        // StepCompleted is processed identically to a direct one by StepCompletedConsumer.
        x.AddConsumer<StepCompletedConsumer,  StepCompletedConsumerDefinition>();
        x.AddConsumer<StepFailedConsumer,     StepFailedConsumerDefinition>();
        x.AddConsumer<StepCancelledConsumer,  StepCancelledConsumerDefinition>();
        x.AddConsumer<StepProcessingConsumer, StepProcessingConsumerDefinition>();
        // Phase 71 / D-15: the Post-Process consumer on the static "orchestrator-result-post" queue —
        // startup-bound (RESEARCH Pitfall 5: the post queue name is a static const, unlike the processor's
        // runtime {id:D}), no bus retry (REQ-71-11; the definition is an intentional no-op). The Pre pipeline
        // (inside the typed result consumers) fans out one NextStepHandoff per match to this queue; this
        // consumer relocates the input into L2[data:messageId] and dispatches the next EntryStepDispatch.
        x.AddConsumer<OrchestratorPostProcessConsumer, OrchestratorPostProcessConsumerDefinition>();
        // 24.1 / D-24.1-05: the boot gate + scheduled redelivery are removed, so the delayed message
        // scheduler (AddDelayedMessageScheduler / UseDelayedMessageScheduler) and its
        // rabbitmq_delayed_message_exchange plugin dependency are gone. No configureBus needed.
    });

// --- Runtime wiring (Phase 23 Plan 04): Quartz + L1 store + scheduler + lifecycle + hydration ---
builder.Services.AddQuartz();                                              // default MS-DI job factory + RAMJobStore
builder.Services.AddQuartzHostedService(o => o.WaitForJobsToComplete = true);
builder.Services.AddSingleton<IWorkflowL1Store, WorkflowL1Store>();
builder.Services.AddSingleton<WorkflowScheduler>();
builder.Services.AddSingleton<WorkflowLifecycle>();
builder.Services.AddSingleton<IStepDispatcher, StepDispatcher>();          // Plan 03 dispatch single-owner (result + fire share it)
builder.Services.AddSingleton<StepAdvancement>();                          // Plan 03 pure match helper (pipeline dependency)

// Phase 71 / D-14/D-15: the orchestrator two-consumer Pre/Post core. The Pre pipeline (gate/read out: ->
// fan out -> delete out:) is consumed by the typed result consumers; the RelocateTail (write data: +
// dispatch) is consumed by the Post consumer. BOTH are AddScoped (mirroring the processor's ProcessorPipeline
// / OutputTail registration): each Send must use the CONSUME-scoped ISendEndpointProvider — a root-scope
// singleton would capture a pre-start send pipeline and the fan-out / dispatch would silently no-op.
builder.Services.AddScoped<OrchestratorPrePipeline>();
builder.Services.AddScoped<RelocateTail>();
// D-17: the data: TTL floor (bound from "Orchestrator"); D-10: the retry budget the pipeline + RelocateTail
// RetryLoop consume (mirror Keeper Program.cs — needed for the bounded-op escalation map).
builder.Services.Configure<OrchestratorOutputOptions>(builder.Configuration.GetSection("Orchestrator"));
builder.Services.Configure<Messaging.Contracts.Configuration.RetryOptions>(builder.Configuration.GetSection("Retry"));

// METRIC-04: the code-owned "Orchestrator" meter + its two business counters. The holder is a
// DI-singleton (IMeterFactory pattern); ConfigureOpenTelemetryMeterProvider additively attaches the
// meter to the shared MeterProvider that AddBaseConsoleObservability (line 20) already built — mirrors
// the Phase-29 ConfigureOpenTelemetryLoggerProvider seam, preserving the D-02 MeterName const symmetry.
builder.Services.AddSingleton<OrchestratorMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(OrchestratorMetrics.MeterName));

// HA-05 / D-09: the role log enricher — the logger-provider twin of the meter registration above,
// mirroring BaseProcessor's DI-resolved ConfigureOpenTelemetryLoggerProvider pair. Registered ALWAYS
// (leader off-cluster, follower until the elector wins in-cluster) so EVERY log line carries
// attributes.role, never empty.
builder.Services.AddSingleton<OrchestratorRoleLogEnricher>();
builder.Services.ConfigureOpenTelemetryLoggerProvider((sp, lp) =>
    lp.AddProcessor(sp.GetRequiredService<OrchestratorRoleLogEnricher>()));

builder.Services.AddHostedService<HydrationBackgroundService>();           // D-13 — drives MarkReady (D-12)

// D-02/D-06: the leader-election BackgroundService runs ONLY in-cluster — off-cluster (and under every
// hermetic test, which never sets KUBERNETES_SERVICE_HOST) the elector never starts, so LeaderState
// keeps its off-cluster leader seed and the lone instance fires. In-cluster it contends for the
// skp/orchestrator-leader Lease and its callbacks become the sole LeaderState writer (HA-03).
if (inCluster)
{
    builder.Services.AddHostedService<LeaderElectionService>();
}

// WorkflowScheduler injects a concrete IScheduler — resolve the hosted scheduler from the factory.
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<ISchedulerFactory>().GetScheduler().GetAwaiter().GetResult());

// TimeProvider for the scheduler/lifecycle cron math (idempotent — base library may not register it).
builder.Services.TryAddSingleton(TimeProvider.System);

// D-12: remove the base library's StartupCompletionService so MarkReady fires at hydration-complete,
// NOT bare host start. IStartupGate / StartupHealthCheck / the "self"/"live" check stay untouched.
foreach (var d in builder.Services
             .Where(d => d.ImplementationType == typeof(BaseConsole.Core.Health.StartupCompletionService))
             .ToList())
{
    builder.Services.Remove(d);
}

var host = builder.Build();
await host.RunAsync();
