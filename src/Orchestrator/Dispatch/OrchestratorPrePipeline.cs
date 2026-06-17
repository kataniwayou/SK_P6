using BaseConsole.Core.Resilience;       // RetryLoop
using MassTransit;                        // ISendEndpointProvider
using Messaging.Contracts;
using Messaging.Contracts.Configuration;  // RetryOptions
using Messaging.Contracts.Projections;    // L2ProjectionKeys
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.L1;
using StackExchange.Redis;

namespace Orchestrator.Dispatch;

/// <summary>
/// D-14 (Phase 71): the linear PRE-PROCESS pipeline runner for the orchestrator — the symmetric mirror of
/// the processor's <c>ProcessorPipeline</c>. Extracted from the old straight-through L1-only
/// <see cref="Orchestrator.Consumers.TypedResultConsumer{T}"/> body so the terminals are testable without a
/// MassTransit harness. The namespaces cross-pair: the processor reads/writes <c>data:</c> and reads
/// <c>out:</c>; this orchestrator Pre READS/DELETES <c>out:</c> (the upstream output blob via A1) and the
/// downstream <c>RelocateTail</c> WRITES <c>data:</c>.
/// <para>
/// <b>The linear Pre flow</b> (REQ-71-01..05):
/// <list type="number">
///   <item><b>Resolution</b> (REQ-71-02/03, PURE L1 — cannot infra-fail): an L1 miss (workflow or step
///   absent) logs a DISTINCT <c>completed-unresolved</c> trip-end line and acks (NO throw, NO keeper).</item>
///   <item><b>SelectNext</b> (REQ-71-03): a resolved step with no matching successor (terminal) logs a
///   DISTINCT <c>completed-terminal</c> trip-end line and acks (NO throw, NO keeper).</item>
///   <item><b>Relocation gate</b> (REQ-71-01/04): only a Completed result has an <c>out:</c> blob. A bounded
///   read of <c>L2[out:EntryId]</c>; a Redis EXCEPTION on the read → exactly one REINJECT + return (no
///   fan-out). A clean-absent/empty value is NOT an escalation — proceed with empty relocated data.</item>
///   <item><b>Fan-out</b> (REQ-71-05): one <see cref="NextStepHandoff"/> per match to
///   <see cref="OrchestratorQueues.ResultPost"/> (fresh envelope MessageId — NO override, D-11); Completed
///   carries the read blob as <see cref="NextStepHandoff.Data"/>, non-completed carries "".</item>
///   <item><b>Delete</b> (REQ-71-05, send-before-delete): AFTER all sends land, delete <c>L2[out:EntryId]</c>
///   (Completed only); a delete-exhaust → one DELETE keeper + return; a send-exhaust THROWS (NO delete).</item>
/// </list>
/// </para>
/// <para>
/// No-silent-loss (REQ-71-02 / D-18) is realized HERE by the two DISTINCT trip-end LOG lines + behavior
/// (ack, no throw, no keeper). The two-reason trip-end metric counter is DEFERRED to a later phase (per the
/// user) — so this pipeline injects NO orchestrator-metrics holder (an unused primary-ctor param would trip
/// CS9113 and the 0-warning build gate). <see cref="NextStepHandoff.ExecutionId"/> is carried from the
/// inbound result onto each handoff unchanged (D-13). Trip-end / drop logs reference ids only — never the
/// relocated-input or author-payload tokens (T-70-10).
/// </para>
/// </summary>
public sealed class OrchestratorPrePipeline(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IConnectionMultiplexer redis,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    ILogger<OrchestratorPrePipeline> logger)
{
    public async Task RunAsync(IStepResult m, StepOutcome outcome, Guid messageId, CancellationToken ct)
    {
        // 1) Resolution (PURE L1, REQ-71-02/03): an L1 miss is the graceful no-silent-loss trip-end —
        //    a DISTINCT "completed-unresolved" line + ack (NO throw, NO keeper — pure L1 cannot infra-fail).
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
        {
            logger.LogInformation(
                "Trip ended (completed-unresolved): no L1 entry for ({WorkflowId}, {StepId}) — acking (business)",
                m.WorkflowId, m.StepId);
            return;
        }

        // 2) SelectNext (REQ-71-03): a resolved-but-terminal step (empty match set) is the other graceful
        //    trip-end — a DISTINCT "completed-terminal" line + ack (NO throw, NO keeper).
        var matches = advancement.SelectNext(outcome, completed, wf.Steps).Matches;
        if (matches.Count == 0)
        {
            logger.LogInformation(
                "Trip ended (completed-terminal): no matching successor for ({WorkflowId}, {StepId}) outcome={Outcome} — acking (business)",
                m.WorkflowId, m.StepId, outcome);
            return;
        }

        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;

        // 3) Relocation gate (REQ-71-01/04): only a Completed result wrote an out: blob (A1). A Redis
        //    EXCEPTION on the read -> REINJECT (the only escalation here). A clean-absent/empty value is NOT
        //    an escalation (RESEARCH Pattern 1 asymmetry) — proceed with empty relocated data.
        string relocated = "";
        if (outcome == StepOutcome.Completed)
        {
            var read = await RetryLoop.ExecuteAsync(async () =>
            {
                var raw = await db.StringGetAsync(L2ProjectionKeys.OutputData(m.EntryId));
                return raw.IsNullOrEmpty ? "" : raw.ToString();
            }, limit, ct);
            if (!read.Succeeded) { await SendKeeper(BuildReinject(m, outcome, messageId), limit, ct); return; }
            relocated = read.Value!;
        }

        // 4) Fan-out (REQ-71-05): one NextStepHandoff per match to orchestrator-result-post. NO MessageId
        //    override (D-11 — MassTransit assigns a fresh envelope id = the next step's messageId). A
        //    send-exhaust THROWS (broker redelivery) — and because the delete runs AFTER, NO delete on throw.
        var post = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.ResultPost}"));
        foreach (var (stepId, step) in matches)
        {
            var handoff = new NextStepHandoff(m.WorkflowId, stepId, step.ProcessorId, step.Payload)
            {
                Data          = relocated,             // "" for a non-completed continuation
                CorrelationId = m.CorrelationId,
                ExecutionId   = m.ExecutionId,         // D-13: threaded UNCHANGED
            };
            var sent = await RetryLoop.ExecuteAsync(
                async () => { await post.Send((object)handoff, CancellationToken.None); return true; }, limit, ct);
            if (!sent.Succeeded) throw sent.Error!;    // send-exhaust → throw → broker redelivery (NO delete)
        }

        // 5) Delete (REQ-71-05, send-before-delete): ONLY when there was an out: blob to delete (Completed).
        //    A delete-exhaust → one DELETE keeper + return.
        if (outcome == StepOutcome.Completed)
        {
            var del = await RetryLoop.ExecuteAsync(
                () => db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), limit, ct);
            if (!del.Succeeded) { await SendKeeper(BuildDelete(m), limit, ct); return; }
        }
    }

    /// <summary>Mirror <c>ProcessorPipeline.SendKeeper</c>: resolve <c>queue:{KeeperQueues.Recovery}</c>
    /// inside the RetryLoop, send-exhaust throws → broker redelivery.</summary>
    private async Task SendKeeper(IKeeperRecoverable msg, int limit, CancellationToken ct)
    {
        var sent = await RetryLoop.ExecuteAsync(async () =>
        {
            var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));
            await ep.Send((object)msg, CancellationToken.None);
            return true;
        }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;
    }

    /// <summary>D-02/D-03: the REINJECT re-asserts the SAME inbound envelope <paramref name="messageId"/> on
    /// the re-injected result so the keeper rebuilds the EXACT Step* record. Carries the outcome discriminator
    /// + the two diagnostic fields (ErrorMessage/CancellationMessage) off the concrete inbound result.</summary>
    private static OrchestratorReinject BuildReinject(IStepResult m, StepOutcome outcome, Guid messageId) =>
        new(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId       = m.CorrelationId,
            ExecutionId         = m.ExecutionId,
            EntryId             = m.EntryId,
            MessageId           = messageId,
            Outcome             = outcome,
            ErrorMessage        = (m as StepFailed)?.ErrorMessage ?? "",
            CancellationMessage = (m as StepCancelled)?.CancellationMessage ?? "",
        };

    /// <summary>D-02: the DELETE-only keeper op carries the source <c>out:</c> entryId.</summary>
    private static OrchestratorDelete BuildDelete(IStepResult m) =>
        new(m.WorkflowId, m.StepId, m.ProcessorId)
        {
            CorrelationId = m.CorrelationId,
            ExecutionId   = m.ExecutionId,
            EntryId       = m.EntryId,
        };
}
