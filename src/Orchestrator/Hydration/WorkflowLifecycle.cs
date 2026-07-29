using System.Text.Json;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.L1;
using Orchestrator.Messaging;
using Orchestrator.Scheduling;
using Quartz;
using StackExchange.Redis;

namespace Orchestrator.Hydration;

/// <summary>
/// Shared per-workflow lifecycle unit (D-15) reused by the startup
/// <see cref="HydrationBackgroundService"/> AND both consumers (Start = the build-then-commit reload
/// <see cref="HydrateAndScheduleAsync"/>, Stop = <see cref="UnscheduleOnlyAsync"/>, the keep-L1
/// drain). All L2 access is READ-ONLY — this type never issues any string-set /
/// set-add / key-delete mutation against any <c>skp:</c> key (ORCH-STOP-01 / T-23-11).
/// <para>
/// <b>Business vs infra split (ORCH-ACK-01):</b> absent root, malformed JSON, and missing/corrupt
/// steps are BUSINESS outcomes — logged + skipped, never thrown. Redis connection/timeout faults are
/// INFRA — they propagate out of <c>GetDatabase</c>/<c>StringGetAsync</c> so the caller (consumer
/// retry pipeline or the hydration backoff loop) can react. See <see cref="IsBusiness"/> /
/// <see cref="IsInfra"/>, mirroring the consumer ack-split + <c>WorkflowRootNotFoundException</c> idiom.
/// </para>
/// </summary>
public sealed class WorkflowLifecycle(
    IConnectionMultiplexer redis,
    IWorkflowL1Store store,
    WorkflowScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<WorkflowLifecycle> logger)
{
    /// <summary>
    /// Hydrate one workflow from L2 into L1 (root + step entries only — NO processor key, NO
    /// parent-index key) and schedule its one-shot Quartz job. Infra faults propagate; business
    /// outcomes (absent root, malformed step) log + skip.
    /// <para>
    /// <b>BUILD-then-COMMIT, never destroy-then-rebuild.</b> <see cref="BuildAsync"/> completes EVERY
    /// Redis read into a local <see cref="WorkflowL1"/> before <see cref="CommitAsync"/> performs the
    /// first mutation. Two consequences a reload depends on: (1) L1 is never ABSENT — the commit
    /// <c>Upsert</c>s old->new in place, so a concurrent <c>ExecutionResult</c> handled by this pod
    /// mid-reload still resolves (an L1 miss in <c>OrchestratorPrePipeline</c>/<c>WorkflowFireJob</c> is
    /// a silently-acked lost continuation, not an error); (2) an infra fault in the read half leaves the
    /// PRIOR entry and the PRIOR Quartz job untouched, so redelivery retries from a clean state.
    /// </para>
    /// </summary>
    public async Task HydrateAndScheduleAsync(Guid workflowId, CancellationToken ct)
    {
        var entry = await BuildAsync(workflowId, ct);
        if (entry is null)
        {
            return; // BUSINESS skip — read-only so far, so NOTHING has been mutated.
        }

        await CommitAsync(workflowId, entry, ct);
    }

    /// <summary>
    /// READ half — pure. Reads the L2 root plus the full reachable step graph and returns the L1 entry
    /// to commit, or <c>null</c> for a business skip (absent root, malformed root, no cron). Contains
    /// NO <c>store.</c> and NO <c>scheduler.</c> call by design: nothing it can throw is able to leave
    /// L1 or the scheduler partially mutated.
    /// </summary>
    private async Task<WorkflowL1?> BuildAsync(Guid workflowId, CancellationToken ct)
    {
        var db = redis.GetDatabase(); // infra fault THROWS -> propagates (D-02 / ORCH-ACK-01)

        var rootRaw = await db.StringGetAsync(OrchestratorL2Keys.Root(workflowId));
        if (rootRaw.IsNullOrEmpty)
        {
            // BUSINESS — workflow absent from L2 root; log + return (NEVER throw).
            logger.LogWarning("Workflow {WorkflowId} absent from L2 root — skipping hydration (business)", workflowId);
            return null;
        }

        WorkflowRootProjection root;
        try
        {
            root = JsonSerializer.Deserialize<WorkflowRootProjection>(rootRaw!)
                   ?? throw new JsonException("root deserialized to null");
        }
        catch (Exception ex) when (IsBusiness(ex))
        {
            logger.LogWarning(ex, "Workflow {WorkflowId} root is malformed — skipping hydration (business)", workflowId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(root.Cron))
        {
            // BUSINESS — a workflow with no cron cannot be scheduled (D-09 business skip).
            logger.LogWarning("Workflow {WorkflowId} has no cron — skipping hydration (business)", workflowId);
            return null;
        }

        // Follow the FULL reachable step graph into L1 (read-only), BFS from the entry steps along
        // each step's NextStepIds. Hydrating only entry steps would leave downstream steps absent from
        // the L1 map, so StepAdvancement.SelectNext would silently skip every continuation edge
        // (TryGetValue miss) and no multi-step series could advance past its entry step. A
        // corrupt/missing step is a business skip for THAT step — its subtree is simply not enqueued
        // beyond what other paths reach; the rest still hydrate.
        var steps = new Dictionary<Guid, StepProjection>();
        var toVisit = new Queue<Guid>(root.EntryStepIds);
        var seen = new HashSet<Guid>(root.EntryStepIds);
        while (toVisit.Count > 0)
        {
            var stepId = toVisit.Dequeue();
            var stepRaw = await db.StringGetAsync(OrchestratorL2Keys.Step(workflowId, stepId));
            if (stepRaw.IsNullOrEmpty)
            {
                logger.LogWarning(
                    "Step {StepId} of workflow {WorkflowId} absent from L2 — skipping step (business)", stepId, workflowId);
                continue;
            }

            try
            {
                var step = JsonSerializer.Deserialize<StepProjection>(stepRaw!)
                           ?? throw new JsonException("step deserialized to null");
                steps[stepId] = step;
                foreach (var nextId in step.NextStepIds ?? Enumerable.Empty<Guid>())
                    if (seen.Add(nextId))
                        toVisit.Enqueue(nextId);
            }
            catch (Exception ex) when (IsBusiness(ex))
            {
                logger.LogWarning(
                    ex, "Step {StepId} of workflow {WorkflowId} is malformed — skipping step (business)", stepId, workflowId);
            }
        }

        // D-09: liveness interval = whole-seconds between the next two Cronos occurrences. Preserve the
        // stored timestamp/status; override only the computed interval (construct a fresh record).
        var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
        var interval = CronInterval.IntervalSeconds(root.Cron, nowUtc);
        var liveness = root.Liveness is { } l
            ? l with { Interval = interval }
            : new LivenessProjection(nowUtc, interval, "active");

        return new WorkflowL1(root.EntryStepIds, root.Cron, root.JobId, steps)
        {
            Liveness = liveness,
        };
    }

    /// <summary>
    /// COMMIT half — mutate only, no I/O between the three statements. Swaps L1 and the Quartz job to
    /// the freshly-built <paramref name="entry"/>.
    /// <para>
    /// <b>Why unschedule first, and why it is TryGet-guarded:</b> <c>RedisProjectionWriter</c> mints a
    /// FRESH JobId into the L2 root on every Upsert, so the new root's JobId cannot address the OLD
    /// Quartz job — only the L1 entry still knows the old JobId, and without this delete the
    /// fresh-JobId-per-Upsert root would accumulate orphan jobs. It must also run BEFORE
    /// <c>ScheduleAsync</c>, which is a bare ScheduleJob ADD (<c>WorkflowScheduler</c>) that throws
    /// <c>ObjectAlreadyExistsException</c> on a live JobKey — the collision case when the JobId is
    /// unchanged. Doing it before (not after) the <c>Upsert</c> keeps the unscheduled window to two
    /// adjacent in-memory Quartz calls. On boot L1 is empty, so the guard skips it and startup
    /// hydration behaves exactly as before. NOTE: this replaces L1 wholesale — it never REMOVES the
    /// entry, so readers never see a hole (that hole was the lost-continuation defect).
    /// </para>
    /// </summary>
    private async Task CommitAsync(Guid workflowId, WorkflowL1 entry, CancellationToken ct)
    {
        if (store.TryGet(workflowId, out var old))
        {
            await scheduler.UnscheduleAsync(old.JobId, ct); // jobId-addressed DeleteJob (Pitfall 4c)
        }

        store.Upsert(workflowId, entry); // atomic old->new swap; NEVER a Remove
        await scheduler.ScheduleAsync(workflowId, entry.JobId, entry.Cron, ct);
    }

    /// <summary>
    /// Stop path (D-07 — ORCH-STOP-DRAIN-01): resolve the jobId from L1 and
    /// <c>DeleteJob(JobKey(jobId))</c>, but KEEP the L1 entry so late
    /// <c>ExecutionResult</c> messages for the stopped workflow still
    /// resolve in L1 and drain (dispatch their next steps). Unlike the Start reload's commit half
    /// (<see cref="HydrateAndScheduleAsync"/>), which replaces the L1 entry wholesale, Stop leaves L1
    /// exactly as it found it — it never calls <c>store.Upsert</c> or <c>store.Remove</c>. That
    /// distinction IS the drain contract. Absent-from-L1 is a business no-op; ZERO L2 writes.
    /// </summary>
    public async Task UnscheduleOnlyAsync(Guid workflowId, CancellationToken ct)
    {
        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — nothing to unschedule.
            return;
        }

        await scheduler.UnscheduleAsync(wf.JobId, ct); // jobId-addressed DeleteJob — NO store.Remove (keep L1)
    }

    /// <summary>Resume path (D-06/D-09): act ONLY if the trigger is Paused; delete the stale paused
    /// job and schedule a FRESH from-now trigger off L1's cron. None(Stopped)/Normal(Running) ignored.
    /// Guard on == Paused exactly (RESEARCH §4 — Blocked/Error fall through to ignore).</summary>
    public async Task ResumeAsync(Guid workflowId, CancellationToken ct)
    {
        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — nothing to resume.
            return;
        }

        var state = await scheduler.GetTriggerStateAsync(wf.JobId, ct);
        if (state != TriggerState.Paused)
        {
            // None(Stopped)/Normal(Running)/Blocked/Error -> ignore (D-09). Log-only diagnostic so a Resume
            // dropped in the narrow fire window is observable (WR-01 / D-01). NOT a re-arm: None is overloaded
            // with operator-Stopped, so re-arming would resurrect a Stopped workflow (D-02).
            logger.LogInformation(
                "Resume ignored for workflow {WorkflowId} — trigger state is {TriggerState}, not Paused (D-09)",
                workflowId, state);
            return;
        }

        await scheduler.UnscheduleAsync(wf.JobId, ct);                    // DeleteJob (removes stale paused job)
        await scheduler.ScheduleAsync(workflowId, wf.JobId, wf.Cron, ct); // fresh from-now trigger -> Normal (sidesteps misfire)
    }

    /// <summary>
    /// INFRA fault = a Redis connection/timeout fault (or a broker fault during scheduling). These
    /// propagate to the caller's retry/backoff. Everything else (JSON/format/absent) is business.
    /// </summary>
    public static bool IsInfra(Exception ex) =>
        ex is RedisConnectionException or RedisTimeoutException or RedisException;

    /// <summary>
    /// BUSINESS fault = malformed/absent L2 data (JSON deserialization, format, argument). The
    /// inverse of <see cref="IsInfra"/>: a fault we log + skip rather than propagate.
    /// </summary>
    public static bool IsBusiness(Exception ex) => !IsInfra(ex);
}
