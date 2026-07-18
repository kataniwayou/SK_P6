using BaseConsole.Core.Health;
using MassTransit;
using MassTransit.Testing;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orchestrator.Dispatch;
using Orchestrator.Election;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// HA-01 leader-only fire-gate proof (Phase 82, D-05/D-06). Drives the REAL <see cref="WorkflowFireJob"/>
/// through its EXTENDED ctor (with a <see cref="LeaderState"/> + a real <see cref="StartupGate"/>) against
/// a real <see cref="WorkflowL1Store"/>, an in-memory MassTransit harness behind <see cref="StepDispatcher"/>,
/// and a <see cref="FakeTimeProvider"/> — exactly the analog shape (<c>FireDispatchTests</c> /
/// <c>WorkflowFireJobScopeTests</c>). The fire gate is <c>IsLeader &amp;&amp; IsReady</c>:
/// <list type="bullet">
///   <item>Follower (hydrated) → ZERO entry-step sends, BUT the L1 liveness refresh AND the trigger
///   reschedule STILL run (the tail is ungated for all replicas).</item>
///   <item>Leader AND hydrated → the entry-step dispatch fires.</item>
///   <item>Leader but NOT hydrated (cold leader, D-05) → ZERO sends, while refresh + reschedule still run.</item>
/// </list>
/// <para>
/// D-06 hermetic determinism: the Quartz scheduler is left in STANDBY (never <c>Start()</c>ed) so a
/// past-dated one-shot trigger cannot misfire the (DI-constructed) job on the scheduler thread — the fire
/// is driven ONLY by the direct <c>job.Execute(...)</c> call, and <c>GetTrigger</c> returns the stored,
/// recomputed next-fire time so the reschedule side-effect is inspectable without background firing.
/// </para>
/// </summary>
public sealed class WorkflowFireJobGateTests
{
    // Fixed clock; cron fires every 5 minutes on the :00/:05/:10 marks.
    private static readonly DateTimeOffset T1200 = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
    private const string Cron = "*/5 * * * *";

    /// <summary>
    /// A STANDBY RAM scheduler (scheduled/rescheduled triggers are stored + queryable, but NEVER fired —
    /// no <c>Start()</c>). A fresh GUID instance name isolates this test's RAMJobStore from parallel classes.
    /// </summary>
    private static async Task<IScheduler> NewStandbySchedulerAsync(CancellationToken ct)
    {
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        return await new StdSchedulerFactory(props).GetScheduler(ct);
    }

    private static ServiceProvider BuildHarness(Guid processorId) =>
        new ServiceCollection()
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<CapturingDispatchConsumer>();
                x.UsingInMemory((ctx, cfg) =>
                {
                    // queue:{processorId:D} <-> ReceiveEndpoint("{processorId:D}")
                    cfg.ReceiveEndpoint($"{processorId:D}", e => e.ConfigureConsumer<CapturingDispatchConsumer>(ctx));
                    cfg.ConfigureEndpoints(ctx);
                });
            })
            .BuildServiceProvider(true);

    private static void SeedEntry(
        WorkflowL1Store store, Guid workflowId, Guid jobId, Guid stepId, Guid processorId, DateTime livenessTimestamp)
    {
        var stepMap = new Dictionary<Guid, StepProjection>
        {
            [stepId] = new StepProjection(EntryCondition: 0, ProcessorId: processorId, Payload: "{}", NextStepIds: []),
        };
        var entry = new WorkflowL1(
            EntryStepIds: [stepId],
            Cron: Cron,
            JobId: jobId,
            Steps: stepMap)
        {
            Liveness = new LivenessProjection(livenessTimestamp, Interval: 300, Status: "active"),
        };
        store.Upsert(workflowId, entry);
    }

    private static IJobExecutionContext FireContext(Guid workflowId, CancellationToken ct)
    {
        var map = new JobDataMap { { "workflowId", workflowId.ToString("D") } };
        var context = Substitute.For<IJobExecutionContext>();
        context.MergedJobDataMap.Returns(map);
        context.CancellationToken.Returns(ct);
        return context;
    }

    private static WorkflowFireJob BuildJob(
        WorkflowL1Store store, ITestHarness harness, WorkflowScheduler scheduler, FakeTimeProvider fakeTime,
        LeaderState leaderState, IStartupGate startupGate) =>
        new(
            store,
            new StepDispatcher(harness.Bus, OrchestratorTestStubs.Metrics()),
            scheduler,
            fakeTime,
            NullLogger<WorkflowFireJob>.Instance,
            leaderState,
            startupGate);

    // ── HA-01: follower fires nothing but still refreshes L1 + reschedules ──────────────────────────
    [Fact]
    public async Task Follower_Fire_Sends_Nothing_But_Still_Refreshes_And_Reschedules()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var fakeTime = new FakeTimeProvider(T1200);
        var store = new WorkflowL1Store();
        var staleTimestamp = T1200.UtcDateTime.AddMinutes(-10);
        SeedEntry(store, workflowId, jobId, stepId, processorId, staleTimestamp);

        await using var provider = BuildHarness(processorId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var scheduler = await NewStandbySchedulerAsync(ct);
            try
            {
                var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
                // Original trigger at the next occurrence from 12:00 → 12:05.
                await workflowScheduler.ScheduleAsync(workflowId, jobId, Cron, ct);

                // Advance PAST the original 12:05 fire so the reschedule recomputes a DISTINCT next
                // occurrence (12:10) — proving RescheduleAsync ran, not just the pre-existing schedule.
                fakeTime.SetUtcNow(new DateTimeOffset(2026, 5, 31, 12, 6, 0, TimeSpan.Zero));

                // Follower (default) + hydrated gate: only the leader term is false, so ONLY sends skip.
                var job = BuildJob(store, harness, workflowScheduler, fakeTime,
                    new LeaderState(), OrchestratorTestStubs.ReadyGate());

                await job.Execute(FireContext(workflowId, ct));

                // (1) ZERO entry-step sends — the gate closed the DispatchAsync loop.
                Assert.False(await harness.Consumed.Any<EntryStepDispatch>(ct), "a follower must send nothing");

                // (2) L1 liveness refresh STILL ran (ungated tail) — timestamp advanced to fake-now (12:06).
                Assert.True(store.TryGet(workflowId, out var after));
                Assert.Equal(fakeTime.GetUtcNow().UtcDateTime, after.Liveness.Timestamp);
                Assert.True(after.Liveness.Timestamp > staleTimestamp);

                // (3) Reschedule STILL ran — the stored trigger's next fire moved from 12:05 to 12:10.
                var trigger = await scheduler.GetTrigger(new TriggerKey(jobId.ToString("D")), ct);
                Assert.NotNull(trigger);
                var next = trigger!.GetNextFireTimeUtc();
                Assert.NotNull(next);
                Assert.Equal(new DateTimeOffset(2026, 5, 31, 12, 10, 0, TimeSpan.Zero), next!.Value);
            }
            finally
            {
                await scheduler.Shutdown(waitForJobsToComplete: false, ct);
            }
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    // ── HA-01: a leader AND hydrated replica fires the entry-step sends ─────────────────────────────
    [Fact]
    public async Task Leader_Fire_Sends_Entry_Steps()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var fakeTime = new FakeTimeProvider(T1200);
        var store = new WorkflowL1Store();
        SeedEntry(store, workflowId, jobId, stepId, processorId, T1200.UtcDateTime.AddMinutes(-10));

        await using var provider = BuildHarness(processorId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var scheduler = await NewStandbySchedulerAsync(ct);
            try
            {
                var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
                await workflowScheduler.ScheduleAsync(workflowId, jobId, Cron, ct);

                // Leader AND hydrated — both gate terms true, so the sends fire.
                var job = BuildJob(store, harness, workflowScheduler, fakeTime,
                    new LeaderState(startAsLeader: true), OrchestratorTestStubs.ReadyGate());

                await job.Execute(FireContext(workflowId, ct));

                Assert.True(await harness.Consumed.Any<EntryStepDispatch>(ct), "a hydrated leader must send");
                var dispatched = harness.Consumed.Select<EntryStepDispatch>(ct)
                    .Select(c => c.Context.Message)
                    .ToList();

                var msg = Assert.Single(dispatched);
                Assert.Equal(workflowId, msg.WorkflowId);
                Assert.Equal(stepId, msg.StepId);
                Assert.Equal(processorId, msg.ProcessorId);
            }
            finally
            {
                await scheduler.Shutdown(waitForJobsToComplete: false, ct);
            }
        }
        finally
        {
            await harness.Stop(ct);
        }
    }

    // ── D-05: a cold leader (leader but NOT hydrated) closes the gate too ───────────────────────────
    [Fact]
    public async Task Unhydrated_Leader_Sends_Nothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var fakeTime = new FakeTimeProvider(T1200);
        var store = new WorkflowL1Store();
        var staleTimestamp = T1200.UtcDateTime.AddMinutes(-10);
        SeedEntry(store, workflowId, jobId, stepId, processorId, staleTimestamp);

        await using var provider = BuildHarness(processorId);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var scheduler = await NewStandbySchedulerAsync(ct);
            try
            {
                var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
                await workflowScheduler.ScheduleAsync(workflowId, jobId, Cron, ct);

                // Leader but a NON-hydrated gate (no MarkReady) → the && hydrated term fences the send.
                var job = BuildJob(store, harness, workflowScheduler, fakeTime,
                    new LeaderState(startAsLeader: true), new StartupGate());

                await job.Execute(FireContext(workflowId, ct));

                // ZERO sends — the cold-leader edge is closed (D-05).
                Assert.False(await harness.Consumed.Any<EntryStepDispatch>(ct), "a cold (un-hydrated) leader must send nothing");

                // The ungated tail STILL runs — L1 liveness refreshed to fake-now.
                Assert.True(store.TryGet(workflowId, out var after));
                Assert.Equal(fakeTime.GetUtcNow().UtcDateTime, after.Liveness.Timestamp);
                Assert.True(after.Liveness.Timestamp > staleTimestamp);
            }
            finally
            {
                await scheduler.Shutdown(waitForJobsToComplete: false, ct);
            }
        }
        finally
        {
            await harness.Stop(ct);
        }
    }
}
