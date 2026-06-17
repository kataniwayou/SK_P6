using BaseConsole.Core.Resilience;       // RetryLoop
using BaseProcessor.Core.Configuration;  // ProcessorLivenessOptions
using BaseProcessor.Core.Identity;       // IProcessorContext
using BaseProcessor.Core.Observability;  // ProcessorMetrics
using BaseProcessor.Core.Validation;     // ProcessorJsonSchemaValidator
using MassTransit;                        // ISendEndpointProvider
using Messaging.Contracts;
using Messaging.Contracts.Configuration; // RetryOptions
using Messaging.Contracts.Projections;   // L2ProjectionKeys
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// D-15: the single source of truth for the output tail (validate output → write L2[messageId]=data ONLY
/// when <c>result == Completed</c> → send Step* by result), shared by BOTH the Pre inline tail
/// (<see cref="ProcessorPipeline"/>) and <see cref="PostProcessConsumer"/>. Pre additionally deletes
/// L2[entryId] AFTER a <c>true</c> (non-escalated) return; Post never touches entryId.
/// <para>
/// Routing (req 4/5): output-validate-fail on a Completed result forces <c>Failed</c> (SPEC pseudocode
/// lines 30/39). The Completed write runs in a bounded <see cref="RetryLoop"/>; a write-exhaust escalates to
/// the redefined INJECT keeper state (carrying the whole <see cref="DataResult"/> + the deleteEntryId) and
/// returns <c>false</c> ("stop — the round trip already ended; the caller must NOT send or delete"). The
/// Step* send runs in a bounded RetryLoop; a send-exhaust THROWS (→ broker redelivery, no _error).
/// </para>
/// </summary>
public sealed class OutputTail(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    IOptions<ProcessorLivenessOptions> livenessOptions,
    ProcessorMetrics metrics)
{
    /// <summary>D-10: the jittered <c>random[ExecutionDataTtl, 2×ExecutionDataTtl]</c> TTL carried on the
    /// L2[messageId] write. IN-04: the POLICY now lives in <see cref="L2ProjectionKeys.OutputDataTtl"/> (the
    /// single source of truth shared with the keeper INJECT path); this only supplies the floor from options.</summary>
    private TimeSpan JitteredTtl()
        => L2ProjectionKeys.OutputDataTtl(livenessOptions.Value.ExecutionDataTtlSeconds);

    /// <summary>Validate output → (if Completed) write L2[messageId]=data (write-exhaust → INJECT,
    /// return <c>false</c> = "stop, do not send/delete") → send Step* by result (send-exhaust throws).
    /// Returns <c>true</c> when the caller may proceed to its own tail (Pre's entry delete); <c>false</c>
    /// when an INJECT escalation already ended the round trip. <paramref name="deleteEntryId"/> is the
    /// source entryId the INJECT keeper should delete (Pre passes <c>d.EntryId</c>; Post passes
    /// <c>Guid.Empty</c> — the delete no-ops on an absent operand).</summary>
    public async Task<bool> RunAsync(DataResult dr, Guid deleteEntryId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;

        // output-validate: a Completed result whose data fails the output schema is forced Failed
        // (SPEC pseudocode line 30/39).
        var result = dr.Result;
        if (result == StepOutcome.Completed
            && !ProcessorJsonSchemaValidator.TryValidate(context.OutputDefinition, dr.Data, out _))
            result = StepOutcome.Failed;

        if (result == StepOutcome.Completed)
        {
            var write = await RetryLoop.ExecuteAsync(
                () => db.StringSetAsync(L2ProjectionKeys.OutputData(dr.MessageId), dr.Data, JitteredTtl()), limit, ct);
            if (!write.Succeeded)
            {
                await SendKeeper(BuildInject(dr, deleteEntryId), limit, ct);   // INJECT + return (no send, no delete)
                return false;
            }
        }

        await SendResult(BuildStep(dr, result), limit, ct);   // send-exhaust → throw → broker redelivery
        return true;
    }

    /// <summary>Mechanical switch on the (possibly output-validation-forced) outcome → one of the 4 Step*
    /// records. A1: EntryId carries <see cref="Guid.Empty"/> this phase — output is keyed by messageId
    /// (OutputData), and the orchestrator-side entryId↔messageId threading is SPEC-deferred.</summary>
    private static IStepResult BuildStep(DataResult dr, StepOutcome result) => result switch
    {
        StepOutcome.Completed => new StepCompleted(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = Guid.Empty },
        StepOutcome.Failed => new StepFailed(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = Guid.Empty, ErrorMessage = "output failed schema validation" },
        StepOutcome.Cancelled => new StepCancelled(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId, EntryId = Guid.Empty, CancellationMessage = "" },
        _ => new StepProcessing(dr.WorkflowId, dr.StepId, dr.ProcessorId)
            { CorrelationId = dr.CorrelationId, ExecutionId = dr.ExecutionId },
    };

    /// <summary>D-13: INJECT embeds the whole self-contained <see cref="DataResult"/> (write
    /// L2[messageId]=data from it, send by its result) + the source <paramref name="deleteEntryId"/>.</summary>
    private static KeeperInject BuildInject(DataResult dr, Guid deleteEntryId) =>
        new(dr.WorkflowId, dr.StepId, dr.ProcessorId)
        {
            CorrelationId = dr.CorrelationId,
            ExecutionId   = dr.ExecutionId,
            DataResult    = dr,
            DeleteEntryId = deleteEntryId,
        };

    // ---- Send owners: copied from ProcessorPipeline.SendResult/SendKeeper (object-cast, throw-on-exhaust,
    //      metrics.ResultSent tag). The inline/Post send does NOT override the envelope MessageId — the
    //      inbound envelope already carries it on the Pre path, and Post re-emits to the orchestrator on the
    //      Result queue (parity with the existing SendResult). The keeper INJECT applies the override. ----

    private async Task SendResult(IStepResult result, int limit, CancellationToken ct)
    {
        // IN-01: resolve GetSendEndpoint INSIDE the RetryLoop (mirror the keeper Guard pattern) so a transient
        // GetSendEndpoint fault routes through the bounded retry like the send; an exhaust still throws → broker
        // redelivery (no _error). The inner broker Send uses CancellationToken.None (do not abort a started send).
        var sent = await RetryLoop.ExecuteAsync(async () =>
        {
            var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
            await ep.Send((object)result, CancellationToken.None);
            return true;
        }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery (no _error)

        metrics.ResultSent.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
            new KeyValuePair<string, object?>("outcome", ResultOutcome(result)));
    }

    private static string ResultOutcome(IStepResult result) => result switch
    {
        StepCompleted  => "completed",
        StepFailed     => "failed",
        StepCancelled  => "cancelled",
        StepProcessing => "processing",
        _              => "failed",
    };

    private async Task SendKeeper(IKeeperRecoverable msg, int limit, CancellationToken ct)
    {
        // IN-01: resolve GetSendEndpoint inside the RetryLoop (keeper Guard parity).
        var sent = await RetryLoop.ExecuteAsync(async () =>
        {
            var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));
            await ep.Send((object)msg, CancellationToken.None);
            return true;
        }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery
    }
}
