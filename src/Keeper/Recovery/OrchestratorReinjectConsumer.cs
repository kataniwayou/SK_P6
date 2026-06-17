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
/// ack with no throw and no send, incrementing <see cref="KeeperMetrics.ReinjectDropped"/> (the shared drop
/// counter — reused, not a new instrument) + a structured warning (ids only, never the payload). A Redis
/// EXCEPTION on the read is still infra → <see cref="RecoveryConsumerBase{TMessage}.Guard"/> → exhaustion
/// policy, NOT a drop. Pitfall 6: STRLEN (not KeyExists) — 0 covers BOTH a missing key AND an empty value
/// (KeyExists would be WRONG: an empty-string key EXISTS). The original Step* is rebuilt faithfully from the
/// carried <see cref="OrchestratorReinject.Outcome"/> discriminator (+ the diagnostic fields) WITHOUT an L1
/// read.</summary>
public sealed class OrchestratorReinjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    KeeperMetrics metrics, ILogger<OrchestratorReinjectConsumer> logger)
    : RecoveryConsumerBase<OrchestratorReinject>(redis, sendProvider, retryOptions)
{
    protected override async Task HandleAsync(OrchestratorReinject m, CancellationToken ct)
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
            metrics.ReinjectDropped.Add(1);                                                            // reuse the shared drop counter
            logger.LogWarning("Orchestrator REINJECT drop: out: gone EntryId={EntryId}", m.EntryId);   // ids only — never log the relocated blob
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
    }
}
