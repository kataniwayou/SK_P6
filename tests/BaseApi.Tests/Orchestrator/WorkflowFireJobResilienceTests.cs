using BaseConsole.Core.Health;
using Messaging.Contracts.Projections;
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
/// QUICK-260726-f7x fire-send resilience proof. Drives the REAL <see cref="WorkflowFireJob"/> against a
/// real <see cref="WorkflowL1Store"/>, a real <see cref="WorkflowScheduler"/> over a STANDBY Quartz
/// scheduler, and a <see cref="FakeTimeProvider"/> — the SAME hermetic scaffold as
/// <c>WorkflowFireJobGateTests</c> — but injects a local <see cref="FakeThrowingDispatcher"/> in place of
/// the MassTransit-backed <c>StepDispatcher</c> so an entry-step send fault can be simulated with no broker.
/// <list type="bullet">
///   <item><b>Test A</b> — an infra send fault on the fire path is swallowed-logged-continued: Execute does
///   NOT throw, the L1 liveness refresh AND the self-reschedule STILL run (the schedule chain survives).</item>
///   <item><b>Test B</b> — a host-shutdown cancellation (OperationCanceledException tied to a cancelled
///   context token) is NOT swallowed: Execute propagates it and does NOT reschedule (graceful shutdown).</item>
///   <item><b>Test C</b> — per-entry-step isolation: the FIRST entry step's send fault does not drop the
///   SECOND entry step's send, and the reschedule still runs.</item>
/// </list>
/// <para>
/// D-06 hermetic determinism (mirrored from the gate tests): the Quartz scheduler stays in STANDBY (never
/// <c>Start()</c>ed) so a past-dated one-shot trigger cannot misfire the DI-constructed job on the scheduler
/// thread — the fire is driven ONLY by the direct <c>job.Execute(...)</c> call, and <c>GetTrigger</c> returns
/// the stored, recomputed next-fire time so the reschedule side-effect is inspectable.
/// </para>
/// </summary>
public sealed class WorkflowFireJobResilienceTests
{
    // Fixed clock; cron fires every 5 minutes on the :00/:05/:10 marks.
    private static readonly DateTimeOffset T1200 = new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);
    private const string Cron = "*/5 * * * *";

    /// <summary>
    /// An <see cref="IStepDispatcher"/> test double that throws a caller-chosen exception for selected
    /// stepIds (the <paramref name="faultFor"/> predicate) and records every stepId it actually dispatched
    /// (i.e. did NOT throw for). No broker, no MassTransit — a pure in-memory seam.
    /// </summary>
    private sealed class FakeThrowingDispatcher(Func<Guid, Exception?> faultFor) : IStepDispatcher
    {
        public List<Guid> DispatchedStepIds { get; } = [];

        public Task DispatchAsync(Guid workflowId, Guid stepId, Guid processorId, string payload,
            Guid correlationId, Guid executionId, Guid entryId, CancellationToken ct)
        {
            if (faultFor(stepId) is { } fault)
            {
                throw fault;
            }

            DispatchedStepIds.Add(stepId);
            return Task.CompletedTask;
        }
    }

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

    /// <summary>Seed L1 with one workflow whose entry-step set is <paramref name="stepIds"/> (order preserved).</summary>
    private static void SeedEntries(
        WorkflowL1Store store, Guid workflowId, Guid jobId, IReadOnlyList<Guid> stepIds, Guid processorId,
        DateTime livenessTimestamp)
    {
        var stepMap = new Dictionary<Guid, StepProjection>();
        foreach (var stepId in stepIds)
        {
            stepMap[stepId] = new StepProjection(EntryCondition: 0, ProcessorId: processorId, Payload: "{}", NextStepIds: []);
        }

        var entry = new WorkflowL1(
            EntryStepIds: [.. stepIds],
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
        WorkflowL1Store store, IStepDispatcher dispatcher, WorkflowScheduler scheduler, FakeTimeProvider fakeTime) =>
        new(
            store,
            dispatcher,
            scheduler,
            fakeTime,
            NullLogger<WorkflowFireJob>.Instance,
            new LeaderState(startAsLeader: true),
            OrchestratorTestStubs.ReadyGate());

    // ── Test A: an infra send fault is swallowed — L1 refresh + reschedule STILL run ─────────────────
    [Fact]
    public async Task Fire_Send_Fault_Survives_Schedule_Chain()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var fakeTime = new FakeTimeProvider(T1200);
        var store = new WorkflowL1Store();
        var staleTimestamp = T1200.UtcDateTime.AddMinutes(-10);
        SeedEntries(store, workflowId, jobId, [stepId], processorId, staleTimestamp);

        // Infra send fault (broker-style) — NOT an OperationCanceledException.
        var dispatcher = new FakeThrowingDispatcher(_ => new InvalidOperationException("stub: broker unreachable on Send"));

        var scheduler = await NewStandbySchedulerAsync(ct);
        try
        {
            var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
            // Original trigger at the next occurrence from 12:00 → 12:05.
            await workflowScheduler.ScheduleAsync(workflowId, jobId, Cron, ct);

            // Advance PAST the original 12:05 fire so the reschedule recomputes a DISTINCT next
            // occurrence (12:10) — proving RescheduleAsync ran, not just the pre-existing schedule.
            fakeTime.SetUtcNow(new DateTimeOffset(2026, 5, 31, 12, 6, 0, TimeSpan.Zero));

            var job = BuildJob(store, dispatcher, workflowScheduler, fakeTime);

            // (1) The fire send fault does NOT propagate out of Execute (swallow-log-continue).
            var ex = await Record.ExceptionAsync(() => job.Execute(FireContext(workflowId, ct)));
            Assert.Null(ex);

            // (2) L1 liveness refresh STILL ran — timestamp advanced to fake-now (12:06).
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

    // ── Test B: a host-shutdown cancellation is NOT swallowed-and-rescheduled ─────────────────────────
    [Fact]
    public async Task Host_Shutdown_Cancellation_Is_Not_Swallowed()
    {
        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var fakeTime = new FakeTimeProvider(T1200);
        var store = new WorkflowL1Store();
        SeedEntries(store, workflowId, jobId, [stepId], processorId, T1200.UtcDateTime.AddMinutes(-10));

        // Host shutdown: the context token is already cancelled, and the dispatcher throws an OCE tied to it.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var shutdownToken = cts.Token;
        var dispatcher = new FakeThrowingDispatcher(_ => new OperationCanceledException(shutdownToken));

        var scheduler = await NewStandbySchedulerAsync(CancellationToken.None);
        try
        {
            var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
            // Schedule the original trigger under a NON-cancelled token so setup succeeds (12:00 → 12:05).
            await workflowScheduler.ScheduleAsync(workflowId, jobId, Cron, CancellationToken.None);

            fakeTime.SetUtcNow(new DateTimeOffset(2026, 5, 31, 12, 6, 0, TimeSpan.Zero));

            var job = BuildJob(store, dispatcher, workflowScheduler, fakeTime);

            // (1) The cancellation propagates — graceful shutdown proceeds, no swallow.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => job.Execute(FireContext(workflowId, shutdownToken)));

            // (2) No reschedule ran — the stored trigger's next fire is still the ORIGINAL 12:05.
            var trigger = await scheduler.GetTrigger(new TriggerKey(jobId.ToString("D")), CancellationToken.None);
            Assert.NotNull(trigger);
            var next = trigger!.GetNextFireTimeUtc();
            Assert.NotNull(next);
            Assert.Equal(new DateTimeOffset(2026, 5, 31, 12, 5, 0, TimeSpan.Zero), next!.Value);
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, CancellationToken.None);
        }
    }

    // ── Test C: per-entry-step isolation — first step's fault does not drop the sibling send ─────────
    [Fact]
    public async Task One_Entry_Step_Fault_Does_Not_Drop_The_Sibling()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var firstStepId = Guid.NewGuid();
        var secondStepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        var fakeTime = new FakeTimeProvider(T1200);
        var store = new WorkflowL1Store();
        SeedEntries(store, workflowId, jobId, [firstStepId, secondStepId], processorId, T1200.UtcDateTime.AddMinutes(-10));

        // Only the FIRST entry step faults; the second dispatches normally (and is recorded).
        var dispatcher = new FakeThrowingDispatcher(
            id => id == firstStepId ? new InvalidOperationException("stub: broker unreachable on Send") : null);

        var scheduler = await NewStandbySchedulerAsync(ct);
        try
        {
            var workflowScheduler = new WorkflowScheduler(scheduler, fakeTime);
            await workflowScheduler.ScheduleAsync(workflowId, jobId, Cron, ct);

            fakeTime.SetUtcNow(new DateTimeOffset(2026, 5, 31, 12, 6, 0, TimeSpan.Zero));

            var job = BuildJob(store, dispatcher, workflowScheduler, fakeTime);

            var ex = await Record.ExceptionAsync(() => job.Execute(FireContext(workflowId, ct)));
            Assert.Null(ex);

            // (1) The SECOND entry step was still dispatched despite the first step's fault.
            Assert.Contains(secondStepId, dispatcher.DispatchedStepIds);
            Assert.DoesNotContain(firstStepId, dispatcher.DispatchedStepIds);

            // (2) The reschedule still ran — the stored trigger's next fire moved to 12:10.
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
}
