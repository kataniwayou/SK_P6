using Messaging.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Orchestrator.Consumers;
using Orchestrator.Hydration;
using Orchestrator.L1;
using Orchestrator.Scheduling;
using Quartz;
using Quartz.Impl;
using Quartz.Impl.Matchers;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// The Start reload is BUILD-then-COMMIT, not destroy-then-rebuild. Two properties are pinned here,
/// both of which the deleted teardown-first ordering violated:
/// <list type="number">
///   <item><b>No transient L1 hole.</b> The reload never removes the L1 entry, so a concurrent
///   <c>IStepResult</c> handled by the same pod mid-reload always resolves in L1 instead of hitting the
///   <c>OrchestratorPrePipeline</c> miss (silent "completed-unresolved" ack + a lost continuation).</item>
///   <item><b>Non-destructive under fault.</b> A Redis fault anywhere in the READ half leaves the prior
///   L1 entry AND the prior Quartz job intact, and propagates so the message nacks and redelivery can
///   retry cleanly. Under teardown-first, L1 and the job were already destroyed with no rollback.</item>
/// </list>
/// </summary>
public sealed class StartReloadNonDestructiveTests
{
    private static async Task<IScheduler> NewRamSchedulerAsync(CancellationToken ct)
    {
        // Unique instance name per scheduler — StdSchedulerFactory binds schedulers in a SHARED
        // process-wide repository keyed by instance name; the default name collides across parallel
        // test classes. A fresh GUID name isolates each test's RAMJobStore.
        var props = new System.Collections.Specialized.NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"test-{Guid.NewGuid():N}",
        };
        var scheduler = await new StdSchedulerFactory(props).GetScheduler(ct);
        await scheduler.Start(ct);
        return scheduler;
    }

    private static StartOrchestrationConsumer BuildConsumer(
        IConnectionMultiplexer mux, IWorkflowL1Store store, WorkflowScheduler workflowScheduler)
    {
        var lifecycle = new WorkflowLifecycle(
            mux, store, workflowScheduler, TimeProvider.System, NullLogger<WorkflowLifecycle>.Instance);
        return new StartOrchestrationConsumer(lifecycle, NullLogger<StartOrchestrationConsumer>.Instance);
    }

    // ----- Property 1: L1 is continuously resolvable across every Redis round-trip of a reload -----

    [Fact]
    public async Task StartReload_L1ResolvableAtEveryRedisRoundTrip()
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();

        // NOTE: this stub carries a FIXED jobId, so the reload's unschedule + schedule hit the SAME
        // JobKey — precisely the collision case that proves the old job is deleted before the new ADD
        // (WorkflowScheduler.ScheduleAsync is a bare ScheduleJob, which throws on a live JobKey).
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId);

        // The store must exist BEFORE the mux: the probing database closes over it to sample L1 on
        // every Redis round-trip the reload makes. Kept local (not in OrchestratorTestStubs) because it
        // is specific to this proof.
        var store = new WorkflowL1Store();
        var probes = new List<int>();
        var db = Substitute.For<IDatabase>();
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(ci =>
            {
                probes.Add(store.TryGet(workflowId, out var e) ? e.Steps.Count : -1);
                var key = ((RedisKey)ci[0]).ToString();
                return values.TryGetValue(key, out var v) ? (RedisValue)v : RedisValue.Null;
            });
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);
            var consumer = BuildConsumer(mux, store, workflowScheduler);

            // First Start hydrates (L1 legitimately empty during this one — not the property under test).
            await consumer.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));
            Assert.True(store.TryGet(workflowId, out _));
            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            probes.Clear();

            // SECOND Start = the reload. Every Redis round-trip it makes samples L1 first.
            await consumer.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));

            // >= 2 (root + at least one step) so the Assert.All below cannot pass vacuously.
            Assert.True(probes.Count >= 2, $"expected >= 2 Redis round-trips during the reload, saw {probes.Count}");

            // FALSIFICATION VALUE: under the deleted teardown-first ordering (store.Remove before the
            // re-hydrate) the FIRST probe of this second reload returns -1 — the workflow is absent from
            // L1 for 1 + N round-trips and every IStepResult arriving in that window is silently lost.
            Assert.All(probes, p => Assert.True(p > 0, "L1 entry was absent (or empty) during a reload round-trip"));

            // And the reload still lands: one entry carrying the freshly-read steps, exactly one job.
            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(workflowId, out var entry));
            Assert.Contains(stepId, entry.Steps.Keys);
            Assert.True(await scheduler.CheckExists(jobKey, ct));
            Assert.Single(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }

    // ----- Property 2: a fault in the read half mutates nothing and propagates -------------------

    /// <param name="okReads">
    /// 0 = the ROOT read faults; 1 = the root succeeds and the first STEP read faults (mid-BFS — the
    /// case with no coverage before this test). Under the old teardown-first ordering BOTH the L1 entry
    /// and the Quartz job were already gone before the fault was even raised, with no rollback path.
    /// </param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task StartReload_BuildFault_LeavesPriorL1AndScheduleIntact(int okReads)
    {
        var ct = TestContext.Current.CancellationToken;

        var workflowId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var processorId = Guid.NewGuid();
        var values = OrchestratorTestStubs.RootWithStep(workflowId, jobId, stepId, processorId);

        var scheduler = await NewRamSchedulerAsync(ct);
        try
        {
            var store = new WorkflowL1Store();
            var workflowScheduler = new WorkflowScheduler(scheduler, TimeProvider.System);

            // Healthy first Start establishes the prior state.
            var healthy = BuildConsumer(OrchestratorTestStubs.PresentL2(values, out _), store, workflowScheduler);
            await healthy.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct));

            Assert.True(store.TryGet(workflowId, out var priorEntry));
            var jobKey = new JobKey(jobId.ToString("D"));
            Assert.True(await scheduler.CheckExists(jobKey, ct));

            // Same pod (same store + same scheduler), next Start, Redis now faults mid-read.
            var faulting = BuildConsumer(
                OrchestratorTestStubs.FaultAfterNReadsL2(values, okReads, out _), store, workflowScheduler);

            // The fault MUST propagate — that is the nack/redelivery contract (D-02 / ORCH-ACK-01).
            await Assert.ThrowsAsync<RedisConnectionException>(
                () => faulting.Consume(OrchestratorTestStubs.Context(new StartOrchestration([workflowId]), ct)));

            // Nothing was mutated: the prior L1 entry is intact...
            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(workflowId, out var after));
            Assert.Contains(stepId, after.Steps.Keys);
            Assert.Equal(priorEntry.Steps.Count, after.Steps.Count);
            Assert.Equal(priorEntry.JobId, after.JobId);

            // ...and so is the prior Quartz job.
            Assert.True(await scheduler.CheckExists(jobKey, ct));
            Assert.Single(await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup(), ct));
        }
        finally
        {
            await scheduler.Shutdown(waitForJobsToComplete: false, ct);
        }
    }
}
