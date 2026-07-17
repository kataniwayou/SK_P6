using Keeper.Observability;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Keeper.Recovery;

/// <summary>KEEP-01: the Keeper REINJECT state — reads L2[entryId] to confirm the recovered input data is
/// still present, then re-injects a reconstructed <see cref="EntryStepDispatch"/> (carrying the D-01
/// <see cref="KeeperReinject.Payload"/> step config) to <c>queue:{ProcessorId:D}</c> — the same target a
/// direct dispatch uses. Phase 52 (D-06/D-07): an absent/empty L2[entryId] (STRLEN==0, NO Redis exception)
/// is now a BY-DESIGN silent drop — ack with no throw and no send, emitting a structured warning
/// (A18 "accepted silent losses": the data is genuinely gone, so a replay can't proceed and nothing
/// downstream is lost). Phase 74 (REQ-3): the legacy reinject-drop counter is removed —
/// the only counter on the success path is the shared <see cref="RecoveryConsumerBase{TMessage}.CountSent"/>
/// (<c>keeper_messages_sent</c>), called after the confirmed send and NEVER on this drop branch. A Redis
/// EXCEPTION on the read is still infra → <see cref="RecoveryConsumerBase{TMessage}.Guard"/> → exhaustion
/// policy (D-01), NOT swallowed as a drop. IN-04: STRLEN (not StringGet) returns 0 for BOTH a missing key
/// AND an empty value without pulling the blob.</summary>
public sealed class ReinjectConsumer(
    IConnectionMultiplexer redis, ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    KeeperMetrics metrics, ILogger<ReinjectConsumer> logger)
    : RecoveryConsumerBase<KeeperReinject>(redis, sendProvider, retryOptions, metrics)
{
    // TEST-ONLY reinject-defeat latch (Phase 79 negative control). 0 = armed, 1 = spent. Static ⇒ per-process;
    // combined with K_EXECUTIONS=1 AND keeper scaled to a SINGLE replica for the FALSIFY-01 run (harness), so
    // exactly one execution is ever defeated. Only consumed when KEEPER_DEFEAT_REINJECT>0 (short-circuit below
    // means the Interlocked call NEVER runs when the seam is unset ⇒ inert).
    private static int _defeatedOnce;

    protected override async Task HandleAsync(KeeperReinject m, CancellationToken ct)
    {
        // Phase 77 (D2/LOG-02): the keeper now OPENS the 5-id execution scope itself. The bus-wide
        // InboundExecutionScopeConsumeFilter no-ops on keeper records (they are not IExecutionCorrelated),
        // so wrap the whole consume body in a MEL scope built from the loose-id BuildState overload (Plan 05).
        // Every record emitted inside now carries WorkflowId/StepId/ProcessorId/ExecutionId/EntryId as ES
        // attributes.* from the ambient scope — identical to the processor/orchestrator — so the Tier-1 join
        // keys are STRIPPED from the reinject strings below (D1/LOG-01). ExecutionLogScope lives in
        // Messaging.Contracts (already imported).
        using (logger.BeginScope(ExecutionLogScope.BuildState(
            m.WorkflowId, m.StepId, m.ProcessorId, m.ExecutionId, m.EntryId)))
        {
            // Guard the READ so a Redis EXCEPTION still routes to the exhaustion policy; absent/empty
            // (STRLEN==0, no exception) is the by-design drop. IN-04: STRLEN, not StringGet — 0 covers
            // a missing key AND an empty value (KeyExists would be WRONG: an empty-string key EXISTS).
            var present = await Guard(
                () => Db.StringLengthAsync(L2ProjectionKeys.ExecutionData(m.EntryId)),
                ct) != 0;
            if (!present)
            {
                // Phase 74 (REQ-3): the legacy reinject-drop counter is REMOVED; the by-design-drop
                // structured warning survives (never log the Payload). No keeper_messages_sent here — a drop
                // never sends, so CountSent must NEVER fire on this early-return path (D-02 / T-74-06).
                // Phase 77 (D1/D2): the Tier-1 join keys (StepId/ExecutionId/EntryId/…) now arrive via the
                // ambient execution scope opened at the top of HandleAsync (D2), so the string carries ONLY the
                // Tier-2 {MessageId} (the one id the scope does NOT carry) + the Tier-3 {ReinjectOutcome}
                // discriminator (D6/LOG-06). Still NEVER log m.Payload (FW-03). PHASE-78 HANDOFF: keeper records
                // now gain attributes.StepId and enter the analyzer's structural query — discriminate them from
                // processor "did-run" records via attributes.ReinjectOutcome (only keeper records carry it).
                logger.LogWarning("REINJECT drop {MessageId} {ReinjectOutcome}", m.MessageId, "drop");
                return;                                                                          // D-06 silent ack
            }

            var dispatch = new EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload)
            {
                CorrelationId = m.CorrelationId,
                ExecutionId = m.ExecutionId,
                EntryId = m.EntryId,
            };
            // IN-01: resolve the send endpoint through Guard too, so a transient GetSendEndpoint failure
            // routes through the bounded RetryLoop like every other op.
            var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}")), ct);
            // IN-01: the inner broker Send uses CancellationToken.None to match ProcessorPipeline's send
            // convention ("do not abort a broker send once started"). The outer Guard keeps ct so the
            // bounded RetryLoop still observes bus shutdown between attempts.
            // req 6 (Phase 70): re-inject with the SAME messageId on the outbound envelope — override
            // SendContext.MessageId to the carried m.MessageId (precedent: OutboundCorrelationSendFilter
            // sets SendContext.CorrelationId). No inbox/dedup on this endpoint, so the reused id is safe.
            // TEST-ONLY reinject-defeat hook (env-gated, DEFAULT OFF — NEVER set in production). Only
            // scripts/phase-79-falsify.ps1 exports KEEPER_DEFEAT_REINJECT>0 for the FALSIFY-01 negative control
            // (gate-teeth proof). When gated AND the one-shot latch is still armed, SUPPRESS the redispatch below
            // for exactly this ONE execution — but STILL CountSent + log "reinject" (unchanged) so the ES telemetry
            // is byte-identical to a genuine "keeper logged reinject, redispatch lost in transit" recoverable-but-lost
            // strand (WR-01 veto → binding miss). Unset/empty/0 ⇒ int.TryParse fails or d<=0 ⇒ the Interlocked call is
            // never reached (short-circuit) ⇒ the redispatch runs normally ⇒ behaviour byte-for-byte unchanged (D-06).
            var defeatReinject =
                int.TryParse(Environment.GetEnvironmentVariable("KEEPER_DEFEAT_REINJECT"), out var d) && d > 0
                && Interlocked.CompareExchange(ref _defeatedOnce, 1, 0) == 0;
            if (!defeatReinject)
            {
                await Guard(() => ep.Send(dispatch, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);
            }
            else
            {
                logger.LogWarning("REINJECT DEFEATED (TEST-ONLY seam) {MessageId} — redispatch suppressed for the FALSIFY-01 negative control; keeper still logs \"reinject\".", m.MessageId);
            }
            // REQ-3 / D-09: count keeper_messages_sent AFTER the confirmed send (Guard re-throws on exhaustion, so
            // a failed/exhausted send never reaches here — T-74-06). NEVER on the absent-data drop branch above.
            CountSent(m.WorkflowId, m.ProcessorId);
            // Phase 77 (D1/D2/D6): symmetric structured success log AFTER the confirmed CountSent (never on the
            // drop early-return above). Tier-1 join keys ride the ambient execution scope (D2); the string carries
            // ONLY the Tier-2 {MessageId} + the Tier-3 {ReinjectOutcome}="reinject" discriminator. Never the Payload.
            logger.LogInformation("REINJECT sent {MessageId} {ReinjectOutcome}", m.MessageId, "reinject");
        }
    }
}
