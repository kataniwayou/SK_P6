using BaseConsole.Core.Health;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Orchestrator.Dispatch;
using Orchestrator.Election;
using Orchestrator.L1;
using Quartz;

namespace Orchestrator.Scheduling;

/// <summary>
/// The per-workflow fire job (ORCH-FIRE-01, D-05/D-10). On each fire it mints a fresh per-fire
/// correlationId (D-05, <c>NewId.NextGuid()</c>), <c>Send</c>s one <see cref="EntryStepDispatch"/>
/// per entry step to <c>queue:{processorId}</c> (Send, NOT Publish — D-10), refreshes the L1 liveness
/// timestamp in-memory (zero L2 writes), and reschedules a fresh trigger for the same job off the next
/// Cronos occurrence.
/// <para>
/// <b>[DisallowConcurrentExecution]</b> guarantees a single jobKey never double-fires (Pitfall 4a).
/// Business cases (workflow gone from L1, entry step missing) are logged + skipped — they NEVER throw.
/// An infra fault on an entry-step Send is ALSO logged + skipped on the FIRE path (QUICK-260726-f7x):
/// it does NOT propagate out of <see cref="Execute"/>, so the L1 liveness refresh and the
/// self-reschedule below still run and the Quartz schedule chain survives a transient broker blip
/// (a self-rescheduling non-durable one-shot would otherwise be auto-purged, silently stopping the
/// workflow). The swallow is PER-ENTRY-STEP (one blip does not drop sibling sends) and NEVER swallows a
/// host-shutdown cancellation from <c>context.CancellationToken</c> — that still propagates so graceful
/// shutdown proceeds. (The continuation-dispatch path — RelocateTail via <c>StepDispatcher</c> — keeps
/// throw → nack → redelivery; only this fire path catches.)
/// </para>
/// <para>
/// Primary-ctor DI mirrors <c>StartOrchestrationConsumer</c>: the body's
/// <see cref="EntryStepDispatch.CorrelationId"/> is the source of truth (D-01) — the bus-wide outbound
/// filter stamps the envelope from the ambient accessor, but the fire path sets the body correlationId
/// directly since it runs outside any inbound consume scope.
/// </para>
/// </summary>
[DisallowConcurrentExecution]
public sealed class WorkflowFireJob(
    IWorkflowL1Store store,
    IStepDispatcher dispatcher,
    WorkflowScheduler scheduler,
    TimeProvider timeProvider,
    ILogger<WorkflowFireJob> logger,
    LeaderState leaderState,
    IStartupGate startupGate) : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var raw = context.MergedJobDataMap.GetString("workflowId");
        if (!Guid.TryParse(raw, out var workflowId))
        {
            logger.LogWarning("WorkflowFireJob fired without a parseable workflowId job-data ({Raw}) — skipping", raw);
            return;
        }

        if (!store.TryGet(workflowId, out var wf))
        {
            // BUSINESS no-op — workflow gone from L1 since the job was scheduled (e.g. stop/reload).
            logger.LogInformation("Workflow {WorkflowId} absent from L1 on fire — skipping (business)", workflowId);
            return;
        }

        // D-05: fresh correlationId per fire, shared by every entry-step dispatch in this fire.
        var correlationId = NewId.NextGuid();

        // D-06 / LOG-05: WorkflowFireJob runs OUTSIDE the consume pipeline (a Quartz job), so neither the
        // correlation filter nor the execution-scope filter ever sees it. Open the scope explicitly here —
        // AFTER the mint, the point where BOTH ids are known — so the fire logs correlate with the
        // round-trip they trigger (CorrelationId via CorrelationKeys.LogScope, the ONE place the job owns
        // it; WorkflowId via ExecutionLogScope.WorkflowId). The early returns above fire BEFORE the mint
        // and are deliberately NOT wrapped (Pattern 6). Ids go ONLY into the scope dictionary, never into a
        // message template (T-18-04 / T-29-08).
        using (logger.BeginScope(new Dictionary<string, object>
        {
            [CorrelationKeys.LogScope]     = correlationId.ToString(),
            [ExecutionLogScope.WorkflowId] = workflowId.ToString(),
        }))
        {
            // D-04/D-05 (HA-01): snapshot the fire gate ONCE at the top of the fire (not per-iteration).
            // Only a leader that is ALSO hydrated may emit entry-step sends — the `&& hydrated` term
            // (IStartupGate.IsReady) fences the cold-leader lost/duplicate-fire edge on an empty L1.
            var fireEnabled = leaderState.IsLeader && startupGate.IsReady;
            if (fireEnabled)
            {
                foreach (var entryStepId in wf.EntryStepIds)
                {
                    if (!wf.Steps.TryGetValue(entryStepId, out var step))
                    {
                        // BUSINESS skip — entry step not in the L1 step map.
                        logger.LogWarning(
                            "Entry step {StepId} of workflow {WorkflowId} missing from L1 steps — skipping (business)",
                            entryStepId, workflowId);
                        continue;
                    }

                    try
                    {
                        // D-01: the build-and-Send shape lives in IStepDispatcher (the single owner). D-03: the
                        // entry-step fire seeds entryId = Guid.Empty — the source-step sentinel (SourceStep.IsSource)
                        // that replaces the retired deterministic hash. The first Guid.Empty is the (unchanged)
                        // executionId lineage; the second is the new entryId sentinel. StepDispatcher.Send still
                        // THROWS on an infra fault (unchanged); the fire path catches it just below.
                        await dispatcher.DispatchAsync(
                            workflowId, entryStepId, step.ProcessorId, step.Payload,
                            correlationId, Guid.Empty, Guid.Empty, context.CancellationToken);
                    }
                    catch (Exception ex) when (
                        !(ex is OperationCanceledException && context.CancellationToken.IsCancellationRequested))
                    {
                        // INFRA send fault (broker blip) — swallow-log-CONTINUE (QUICK-260726-f7x) so the fire
                        // falls through to the L1 liveness refresh + self-reschedule below and the Quartz
                        // schedule chain survives (a non-durable one-shot with no next trigger is auto-purged →
                        // the workflow would silently stop firing on this leader until restart/failover). The
                        // catch is PER-ENTRY-STEP (inside the loop, mirroring the business-skip `continue`
                        // above) so one entry step's blip does NOT drop the OTHER entry-step sends in this fire.
                        // The `when` filter deliberately does NOT catch an OperationCanceledException raised by
                        // host-shutdown of context.CancellationToken — that cancellation propagates so graceful
                        // shutdown proceeds (Task B). No retry is added; publisher confirmation stays ON. Ids
                        // (workflowId/correlationId) ride the ALREADY-OPEN scope — only the non-id entryStepId
                        // and the exception object are template args, which sidesteps CA2017 (T-18-04).
                        logger.LogWarning(ex,
                            "Entry step {StepId} send faulted on fire — logging and continuing so the schedule chain survives (infra)",
                            entryStepId);
                        continue;
                    }
                }
            }
            else
            {
                // HA-01: a follower (or un-hydrated leader) skips ONLY the entry-step sends — it still
                // refreshes L1 liveness and reschedules below. WorkflowId rides the ALREADY-OPEN scope,
                // NOT the message template (T-18-04, ids-in-scope-not-template) — hence NO template arg,
                // so CA2017 (arg/placeholder count) is suppressed for this ONE deliberate scope-only line.
#pragma warning disable CA2017 // WorkflowId is supplied by the open log scope, not a template argument (T-18-04)
                logger.LogInformation("Follower — leader gate closed; skipping entry-step sends for {WorkflowId}");
#pragma warning restore CA2017
            }

            // L1 liveness refresh — in-memory only (NO L2 write). Replace the immutable LivenessProjection
            // record preserving interval/status with an updated timestamp.
            var nowUtc = timeProvider.GetUtcNow().UtcDateTime;
            var current = wf.Liveness;
            wf.Liveness = current is null
                ? new LivenessProjection(nowUtc, 0, "active")
                : current with { Timestamp = nowUtc };

            // Self-reschedule off the next Cronos occurrence (Pitfall 4b — new trigger for the existing job).
            if (CronInterval.NextOccurrence(wf.Cron, nowUtc) is not null)
            {
                await scheduler.RescheduleAsync(workflowId, wf.JobId, wf.Cron, context.CancellationToken);
            }
        }
    }
}
