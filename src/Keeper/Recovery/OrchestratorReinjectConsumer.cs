using Keeper.Observability;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>req 8 (Phase 71): the Orchestrator REINJECT state — the orchestrator-side mirror of
/// <see cref="ReinjectConsumer"/>. Reads <c>L2[out:entryId]</c> (<see cref="L2ProjectionKeys.OutputData"/>)
/// via STRLEN to confirm the completed step's output blob is still present, then re-injects the original
/// Step-result (NOT a dispatch) to <c>queue:{OrchestratorQueues.Result}</c> with the outbound envelope
/// <c>MessageId</c> overridden to the carried <see cref="OrchestratorReinject.MessageId"/> (D-03 — "same
/// messageId"). An absent/empty <c>out:</c> blob (STRLEN==0, NO Redis exception) is a BY-DESIGN silent drop —
/// ack with no throw and no send, emitting a structured warning (ids only, never the payload). Phase 74
/// (REQ-3): the legacy reinject-drop counter is removed — the only success-path counter
/// is the shared <see cref="RecoveryConsumerBase{TMessage}.CountSent"/> (<c>keeper_messages_sent</c>), called
/// after the confirmed send and NEVER on this drop branch. A Redis
/// EXCEPTION on the read is still infra → <see cref="RecoveryConsumerBase{TMessage}.Guard"/> → exhaustion
/// policy, NOT a drop. Pitfall 6: STRLEN (not KeyExists) — 0 covers BOTH a missing key AND an empty value
/// (KeyExists would be WRONG: an empty-string key EXISTS). The original Step* is rebuilt faithfully from the
/// carried <see cref="OrchestratorReinject.Outcome"/> discriminator (+ the diagnostic fields) WITHOUT an L1
/// read.</summary>
public sealed class OrchestratorReinjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    KeeperMetrics metrics, ILogger<OrchestratorReinjectConsumer> logger)
    : RecoveryConsumerBase<OrchestratorReinject>(redis, sendProvider, retryOptions, metrics)
{
    protected override async Task HandleAsync(OrchestratorReinject m, CancellationToken ct)
    {
        // Phase 77 (D2/LOG-02): symmetric with ReinjectConsumer — the orchestrator-side keeper opens the same
        // 5-id execution scope itself (the bus-wide InboundExecutionScopeConsumeFilter no-ops on keeper records,
        // which are not IExecutionCorrelated). Wrap the whole consume body so every record carries
        // WorkflowId/StepId/ProcessorId/ExecutionId/EntryId as ES attributes.* from the ambient scope, and the
        // Tier-1 join keys are STRIPPED from the strings below (D1/LOG-01). PHASE-78 HANDOFF: keeper records now
        // gain attributes.StepId and enter the analyzer's structural query — discriminate via
        // attributes.ReinjectOutcome (only keeper records carry it).
        using (logger.BeginScope(ExecutionLogScope.BuildState(
            m.WorkflowId, m.StepId, m.ProcessorId, m.ExecutionId, m.EntryId)))
        {
            // Guard the READ so a Redis EXCEPTION still routes to the exhaustion policy; absent/empty
            // (STRLEN==0, no exception) is the by-design drop. Pitfall 6: STRLEN, not KeyExists — 0 covers
            // a missing key AND an empty value (KeyExists would be WRONG: an empty-string key EXISTS). The
            // orchestrator reads out: (not data:) — the cross-paired namespace.
            var present = await Guard(
                () => Db.StringLengthAsync(L2ProjectionKeys.OutputData(m.EntryId)),
                ct) != 0;
            if (!present)
            {
                // Phase 74 (REQ-3): the legacy reinject-drop counter is REMOVED; the by-design-drop structured
                // warning survives. Phase 77 (D1/D2/D6): Tier-1 EntryId is REMOVED from the string (it now rides
                // the ambient execution scope); the record carries ONLY the Tier-2 {MessageId} + the Tier-3
                // {ReinjectOutcome}="drop" discriminator. Never log the relocated blob (FW-03). No
                // keeper_messages_sent here — a drop never sends (D-02 / T-74-06).
                logger.LogWarning("Orchestrator REINJECT drop {MessageId} {ReinjectOutcome}", m.MessageId, "drop");
                return;                                                                                    // silent ack
            }

            // Rebuild the original Step* faithfully from the carried outcome discriminator (no L1 read). The
            // Completed arm carries EntryId = m.EntryId (the real out: key, per A1); Failed/Cancelled carry their
            // diagnostic; Processing carries no EntryId. Mirrors the InjectConsumer result switch shape.
            IStepResult step = m.Outcome switch
            {
                StepOutcome.Completed => new StepCompleted(m.WorkflowId, m.StepId, m.ProcessorId)
                { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = m.EntryId },
                StepOutcome.Failed => new StepFailed(m.WorkflowId, m.StepId, m.ProcessorId)
                { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = Guid.Empty, ErrorMessage = m.ErrorMessage },
                StepOutcome.Cancelled => new StepCancelled(m.WorkflowId, m.StepId, m.ProcessorId)
                { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId, EntryId = Guid.Empty, CancellationMessage = m.CancellationMessage },
                _ => new StepProcessing(m.WorkflowId, m.StepId, m.ProcessorId)
                { CorrelationId = m.CorrelationId, ExecutionId = m.ExecutionId },
            };

            // Resolve the send endpoint through Guard too so a transient GetSendEndpoint failure routes through
            // the bounded RetryLoop like every other op. Re-inject a RESULT (not a dispatch) to the orchestrator
            // result queue with the envelope MessageId overridden to the carried m.MessageId (D-03 — SAME id).
            var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}")), ct);
            await Guard(() => ep.Send((object)step, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);
            // REQ-3 / D-09: count keeper_messages_sent AFTER the confirmed send (Guard re-throws on exhaustion, so
            // a failed/exhausted send never reaches here — T-74-06). NEVER on the absent-out: drop branch above.
            CountSent(m.WorkflowId, m.ProcessorId);
            // Phase 77 (D1/D2/D6): NEW symmetric sent record on the confirmed-send path only — completing the
            // send-carries-{MessageId} pattern. Tier-1 join keys ride the ambient execution scope; the string
            // carries ONLY the Tier-2 {MessageId} + the Tier-3 {ReinjectOutcome}="reinject" discriminator.
            logger.LogInformation("Orchestrator REINJECT sent {MessageId} {ReinjectOutcome}", m.MessageId, "reinject");
        }
    }
}
