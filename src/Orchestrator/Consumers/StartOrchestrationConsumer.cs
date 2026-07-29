using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;
using Orchestrator.Hydration;

namespace Orchestrator.Consumers;

/// <summary>
/// Start consumer (ORCH-START-RELOAD-01 — supersedes Phase 23 ORCH-CONSUME-01). Conditionless reload:
/// for each WorkflowId in the body it UNCONDITIONALLY runs the shared
/// <see cref="WorkflowLifecycle.HydrateAndScheduleAsync"/> BUILD-then-COMMIT reload — it reads the
/// current L2 definition IN FULL before mutating anything, then swaps the L1 entry and the Quartz job
/// (unschedule old JobId -> Upsert -> schedule, Pitfall 4) in one commit. There is NO existence skip and
/// NO per-workflow stripe — duplicate-suppression now lives in the WebApi (D-04/D-05), so a Start for a
/// workflow lingering in L1 (e.g. after a Stop drain) re-hydrates and reschedules, reviving its job.
/// <para>
/// Boot gate REMOVED (24.1 / D-24.1-05, supersedes D-06 / ORCH-GATE-01): the consumer runs only after
/// the bus starts; per-workflow state is per-instance (no gate serialization), acceptable under
/// single-replica + the existing per-workflow handling. Business outcomes (absent root/step) are
/// logged + skipped INSIDE <see cref="WorkflowLifecycle"/>. Only INFRA faults propagate out of
/// <c>Consume</c> -> the definition's bounded retry -> <c>_error</c> (D-02); this consumer does NOT
/// catch-all infra.
/// </para>
/// </summary>
public sealed class StartOrchestrationConsumer(
    WorkflowLifecycle lifecycle,
    ILogger<StartOrchestrationConsumer> logger) : IConsumer<StartOrchestration>
{
    public async Task Consume(ConsumeContext<StartOrchestration> context)
    {
        foreach (var workflowId in context.Message.WorkflowIds)
        {
            // Conditionless (D-05): ONE build-then-commit reload from the current L2 definition. No
            // existence skip, no stripe (WebApi dedups). The reload NEVER removes the L1 entry — it
            // Upserts old->new — so a concurrent ExecutionResult on this pod cannot fall into an L1 hole
            // and be silently acked as completed-unresolved. And a Redis fault during the read half
            // leaves the prior entry AND the prior schedule intact, so the throw nacks and redelivery
            // retries from a clean state.
            logger.LogInformation("Start reload for WorkflowId={WorkflowId}", workflowId);
            await lifecycle.HydrateAndScheduleAsync(workflowId, context.CancellationToken);
        }
        // returns normally -> ACK
    }
}
