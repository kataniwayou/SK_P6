using BaseConsole.Core.Resilience;       // RetryLoop
using MassTransit;                        // ISendEndpointProvider
using Messaging.Contracts;
using Messaging.Contracts.Configuration;  // RetryOptions
using Messaging.Contracts.Projections;    // L2ProjectionKeys
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.L1;
using Orchestrator.Observability;        // OrchestratorMetrics (StepUnresolved counter)
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
/// <b>The UNIFORM (branch-free) Pre flow</b> (Phase 72 / REQ-71-01..05 + SPEC-4/5/6 / D-05/D-09/D-10): the
/// read → fan-out → delete run for EVERY outcome — there is NO <c>if (outcome == Completed)</c> branch (D-09).
/// Since the processor now ALWAYS writes an <c>out:</c> blob on a terminal result (Phase 72 Plan 01), a
/// Completed/Failed/Cancelled all relocate identically; <c>outcome</c> is consumed ONLY by
/// <see cref="StepAdvancement.SelectNext"/> for entry-condition matching, never for L2 behavior.
/// <list type="number">
///   <item><b>Resolution</b> (REQ-71-02/03, PURE L1 — cannot infra-fail): an L1 miss (workflow or step
///   absent) logs a DISTINCT <c>completed-unresolved</c> trip-end line, increments
///   <c>orchestrator_step_unresolved</c> (stage-1), and acks (NO throw, NO keeper).</item>
///   <item><b>SelectNext</b> (REQ-71-03): a resolved step with NO matches AND NO unresolved ids (a true
///   terminal) logs a DISTINCT <c>completed-terminal</c> trip-end line and acks (NO throw, NO keeper, NO
///   increment — D-03). Empty matches but non-empty <c>UnresolvedIds</c> falls THROUGH so stage-3 counts.</item>
///   <item><b>Relocation (three-way read, D-10)</b>: a bounded read of <c>L2[out:EntryId]</c> runs for every
///   outcome. (a) a Redis EXCEPTION on the read → exactly one REINJECT + return (no fan-out); (b) a
///   clean-absent/empty value (no Redis fault) → an idempotent ack-skip: NO fan-out, NO keeper, NO delete
///   (this is how a <c>Processing</c> result — which wrote no <c>out:</c> blob — stops advancing for free,
///   D-05); (c) a present blob → relocate it.</item>
///   <item><b>Fan-out</b> (REQ-71-05): one <see cref="NextStepHandoff"/> per match to
///   <see cref="OrchestratorQueues.ResultPost"/> (fresh envelope MessageId — NO override, D-11) carrying the
///   read blob as <see cref="NextStepHandoff.Data"/>. Each entry in <c>UnresolvedIds</c> (a dangling next-step
///   edge) logs + increments <c>orchestrator_step_unresolved</c> (stage-3) + <c>continue</c>s — NEVER throws
///   (D-02 graceful business skip).</item>
///   <item><b>Delete</b> (REQ-71-05, send-before-delete): AFTER all sends land, delete <c>L2[out:EntryId]</c>
///   (for every outcome, past the clean-absent gate); a delete-exhaust → one DELETE keeper + return; a
///   send-exhaust THROWS (NO delete).</item>
/// </list>
/// </para>
/// <para>
/// No-silent-loss (REQ-71-02 / D-18) is realized HERE by the two DISTINCT trip-end LOG lines + behavior
/// (ack, no throw, no keeper) AND the <c>orchestrator_step_unresolved</c> counter (Phase-71 D-18 deferral
/// realized): it increments at stage-1 (consumed step/workflow missing from L1) and once per dangling
/// next-step id at stage-3 — labeled <c>workflowId</c> only. A completed-terminal, an
/// entry-condition skip, and a normal fan-out do NOT increment. The <see cref="OrchestratorMetrics"/> holder
/// is constructor-injected (singleton-into-scoped — no Program.cs edit). <see cref="NextStepHandoff.ExecutionId"/>
/// is carried from the inbound result onto each handoff unchanged (D-13). Trip-end / drop logs reference ids
/// only — never the relocated-input or author-payload tokens (T-70-10).
/// </para>
/// </summary>
public sealed class OrchestratorPrePipeline(
    IWorkflowL1Store store,
    StepAdvancement advancement,
    IConnectionMultiplexer redis,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    OrchestratorMetrics metrics,
    ILogger<OrchestratorPrePipeline> logger)
{
    public async Task RunAsync(IStepResult m, StepOutcome outcome, Guid messageId, CancellationToken ct)
    {
        // 1) Resolution (PURE L1, REQ-71-02/03): an L1 miss is the graceful no-silent-loss trip-end —
        //    a DISTINCT "completed-unresolved" line + ack (NO throw, NO keeper — pure L1 cannot infra-fail).
        if (!store.TryGet(m.WorkflowId, out var wf) || !wf.Steps.TryGetValue(m.StepId, out var completed))
        {
            logger.LogInformation(
                "Trip ended (completed-unresolved): no L1 entry — acking (business)");
            metrics.StepUnresolved.Add(1, new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")));
            return;
        }

        // 2) SelectNext (REQ-71-03): a resolved step with NO matches AND NO unresolved ids is the true
        //    terminal — a DISTINCT "completed-terminal" line + ack (NO throw, NO keeper, NO increment, D-03).
        //    If Matches is empty but UnresolvedIds is NOT, do NOT early-return — fall through so stage-3
        //    (Task 2) increments orchestrator_step_unresolved per dangling id.
        var selection = advancement.SelectNext(outcome, completed, wf.Steps);
        if (selection.Matches.Count == 0 && selection.UnresolvedIds.Count == 0)
        {
            // FW-04 (T-76-05): both terminal-branch log calls are try/catch-guarded so a throwing/blocking
            // ILogger can NEVER fail the terminal ack (this branch acks by returning). The existing trip-end
            // line is guarded here too because it now precedes the FW-02 terminal-reached record on the same
            // ack path — a throw from either must be absorbed, never surfaced out of RunAsync.
            try
            {
                logger.LogInformation(
                    "Trip ended (completed-terminal): no matching successor outcome={Outcome} — acking (business)",
                    outcome);
            }
            catch { /* observability must never fail the hop */ }

            // FW-02 / D1 (Phase 77): the terminal-reached causal-edge marker — the orchestrator resolved NO next
            // steps for this (corr,exec). The five Tier-1 ids (CorrelationId/ExecutionId/WorkflowId/EntryId/StepId)
            // now arrive via the ambient MEL execution scope (attributes.*), NOT the template — this is the bare
            // marker. Step_G fans in twice, so it still fires ×2 per (corr,exec), distinguished by the scope's
            // EntryId (= M_N). The Phase-78 analyzer adapts its fan-in/terminal parsing to the attribute-only shape.
            try
            {
                logger.LogInformation("terminal reached");
            }
            catch { /* observability must never fail the hop */ }
            return;
        }

        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;

        // 3) Relocation (UNIFORM, three-way, D-09/D-10): the out: read runs for EVERY outcome (the processor
        //    now always writes a terminal out: blob, Phase 72 Plan 01). WR-01: discriminate key-MISSING from
        //    present-but-EMPTY — the read lambda returns null ONLY when the key is truly absent (HasValue ==
        //    false) so a present-but-empty terminal payload (e.g. NewResult(Completed, "")) still relocates
        //    instead of being silently collapsed into the clean-absent skip and dropping a genuine fan-out. A
        //    thrown RedisException still propagates out of the lambda (Pitfall 2). (a) read-fault (exhausted) ->
        //    REINJECT + return. (b) truly-absent key (no fault, no blob — e.g. a Processing result that wrote
        //    nothing) -> idempotent ack-skip: NO fan-out, NO keeper, NO delete (D-05). (c) present (incl. "")
        //    -> relocate.
        var read = await RetryLoop.ExecuteAsync(async () =>
        {
            var raw = await db.StringGetAsync(L2ProjectionKeys.OutputData(m.EntryId));
            return raw.HasValue ? raw.ToString() : null;   // null == key truly absent; "" == present-empty (relocates)
        }, limit, ct);
        if (!read.Succeeded) { await SendKeeper(BuildReinject(m, outcome, messageId), limit, ct); return; }
        if (read.Value is null)   // only a truly-absent key is the idempotent skip (a present "" relocates)
        {
            logger.LogInformation(
                "Trip ended (clean-absent out: blob): no out: blob — acking idempotent skip");
            return;   // clean-absent -> idempotent skip: NO fan-out, NO keeper, NO delete (D-10)
        }
        var relocated = read.Value!;

        // 4) Fan-out (REQ-71-05): one NextStepHandoff per match to orchestrator-result-post. NO MessageId
        //    override (D-11 — MassTransit assigns a fresh envelope id = the next step's messageId). A
        //    send-exhaust THROWS (broker redelivery) — and because the delete runs AFTER, NO delete on throw.
        var post = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.ResultPost}"));
        foreach (var (stepId, step) in selection.Matches)
        {
            var handoff = new NextStepHandoff(m.WorkflowId, stepId, step.ProcessorId, step.Payload)
            {
                Data          = relocated,             // the relocated out: blob (uniform for every outcome)
                CorrelationId = m.CorrelationId,
                ExecutionId   = m.ExecutionId,         // D-13: threaded UNCHANGED
            };
            var outboundId = Guid.NewGuid();   // D3/LOG-03: mint the outbound envelope id (captures the id D-11 Option C dropped)
            var sent = await RetryLoop.ExecuteAsync(
                async () => { await post.Send((object)handoff, ctx => ctx.MessageId = outboundId, CancellationToken.None); return true; }, limit, ct);
            if (!sent.Succeeded) throw sent.Error!;    // send-exhaust → throw → broker redelivery (NO delete)

            // Phase 74 (REQ-1/D-02): count-AFTER-success, ONCE per successful fan-out post.Send — N matches
            // ⇒ N increments. D-06: the orchestrator-result-post queue recipient is NOT a processor, so use
            // the PRODUCING processor's id off the inbound result `m` (the only processor identity present);
            // workflowId = m.WorkflowId. camelCase labels (D-07); messageId never a label (D-04).
            metrics.MessagesSent.Add(1,
                new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")),
                new KeyValuePair<string, object?>("processorId", m.ProcessorId.ToString("D")));

            // FW-02 / D3 (Phase 77, reverses D-11 Option C): one fan-out edge record per next step, AFTER the
            // send landed. The outbound envelope id is now MINTED + stamped on the send (ctx.MessageId) AND
            // logged as the Tier-2 {MessageId}; the Tier-3 {NextStepId} is the next step. The five Tier-1 ids
            // arrive via the ambient MEL execution scope (attributes.*), NOT the template (FW-03: never the
            // relocated blob). FW-04: try/catch-guarded so a throwing logger cannot fail the fan-out send loop.
            // (Phase-78 analyzer adapts its fan-out parsing to the new {MessageId} {NextStepId} shape.)
            try { logger.LogInformation("fan-out {MessageId} {NextStepId}", outboundId, stepId); }
            catch { /* observability must never fail the hop */ }
        }

        // stage-3: each dangling next-step id in selection.UnresolvedIds logs + increments
        //    orchestrator_step_unresolved (graceful, NEVER throws — D-02). IN-01: this loop sits AFTER the
        //    clean-absent gate DELIBERATELY — dangling-edge counting is intentionally suppressed for
        //    clean-absent / Processing messages (a Processing result that also has a dangling edge does NOT
        //    surface it on the metric). This is a conscious contract, NOT an accident: do NOT "fix" it by
        //    moving the loop above the clean-absent gate (that would break Processing_rides_clean_absent_skip).
        foreach (var unresolvedId in selection.UnresolvedIds)
        {
            logger.LogInformation(
                "Dangling next-step id {NextStepId} — skipping (business)",
                unresolvedId);
            metrics.StepUnresolved.Add(1, new KeyValuePair<string, object?>("workflowId", m.WorkflowId.ToString("D")));
            // IN-02: graceful business skip — never throw (D-02 / T-72-08). No explicit `continue` needed:
            // this is the last statement of the loop body, so the iteration falls through naturally.
        }

        // 5) Delete (REQ-71-05, send-before-delete, UNIFORM): AFTER all sends land, delete L2[out:EntryId]
        //    for every outcome (past the clean-absent gate). A delete-exhaust → one DELETE keeper + return.
        var del = await RetryLoop.ExecuteAsync(
            () => db.KeyDeleteAsync(L2ProjectionKeys.OutputData(m.EntryId)), limit, ct);
        if (!del.Succeeded) { await SendKeeper(BuildDelete(m), limit, ct); return; }
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

        // Phase 74 (REQ-1/D-02): count-AFTER-success — this single SendKeeper covers BOTH the REINJECT
        // escalation (read-fault) AND the DELETE escalation (delete-exhaust). D-06: the keeper-recovery
        // recipient is not a processor → use the producing processor's id off the IKeeperRecoverable `msg`.
        metrics.MessagesSent.Add(1,
            new KeyValuePair<string, object?>("workflowId", msg.WorkflowId.ToString("D")),
            new KeyValuePair<string, object?>("processorId", msg.ProcessorId.ToString("D")));
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
