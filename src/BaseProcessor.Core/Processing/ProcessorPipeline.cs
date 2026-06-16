using BaseConsole.Core.Resilience;   // D-05: RetryLoop / RetryOutcome relocated here
using BaseProcessor.Core.Configuration;
using BaseProcessor.Core.Identity;
using BaseProcessor.Core.Observability;
using BaseProcessor.Core.Resilience;   // KeyAbsentException stays processor-side (Pre-read sentinel)
using BaseProcessor.Core.Validation;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Configuration;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// D-14 (Phase 70): the linear PRE-PROCESS pipeline runner. Extracted from the old
/// straight-through <see cref="EntryStepDispatchConsumer"/> so the terminals are testable without a
/// MassTransit harness (a plain object the facts construct directly). Replaces the slot-array / recovery-pass
/// model entirely (no <c>RunRecoveryAsync</c>, no <c>RunForwardAsync</c>, no slots, no <c>MessageIndex</c>).
/// <para>
/// <b>The linear Pre flow</b> (SPEC pseudocode lines 20-35 / req 1-4):
/// <list type="number">
///   <item><b>Gate</b> (req 1): branch on <c>exist L2[entryId]</c> via a bounded retry. An existence-check
///   exhaustion routes to <c>REINJECT(messageId, ids, payload)</c> and ENDS the round trip (no result sent,
///   input left intact). A clean-absent entryId returns WITHOUT processing.</item>
///   <item><b>Read + input-validate + deserialize</b> (req 2): a bounded-retry read (absent/empty unified
///   with a Redis fault via <see cref="KeyAbsentException"/>) that exhausts routes to <c>REINJECT</c> and
///   returns; an input-schema failure (or a deserialize <c>JsonException</c> thrown by the seam) sends
///   exactly ONE <c>StepFailed</c> and returns WITH NO entry delete (C-2 / req 2 — the entry is left to its
///   TTL).</item>
///   <item><b>Seam</b> (req 3): the author <c>ProcessAsync</c> returns ONE <see cref="DataResult"/> or
///   <c>null</c>. On <c>null</c> the author already handled spawn+handoff (Mode-2) — the Pre consumer writes,
///   sends, and deletes NOTHING.</item>
///   <item><b>Inline tail</b> (req 4): the shared <see cref="OutputTail"/> (write L2[messageId]=data on
///   Completed → INJECT on write-exhaust → send Step* by result), THEN <c>delete L2[entryId]</c>
///   (delete-exhaust → DELETE keeper).</item>
/// </list>
/// </para>
/// <para>
/// <b>Resilience</b> (req 11): every L2 op and every send is wrapped in <see cref="RetryLoop"/> using
/// <c>Retry:Limit</c>; a send that exhausts PROPAGATES (throws) → with NO bus retry and NO error pipeline on
/// the dispatch endpoint the default is RabbitMQ nack-requeue (broker redelivery) — no dead-letter, no
/// <c>_error</c>. The in-code RetryLoop owns ALL per-op retries.
/// </para>
/// </summary>
public sealed class ProcessorPipeline(
    IConnectionMultiplexer redis,
    IProcessorContext context,
    BaseProcessor processor,
    ISendEndpointProvider sendProvider,
    IOptions<RetryOptions> retryOptions,
    OutputTail outputTail,
    ProcessorMetrics metrics,
    ILogger<ProcessorPipeline> logger)
{
    public async Task RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;

        // req 1: gate on exist L2[entryId]. Exhaust → REINJECT(messageId,…) + return (no source delete,
        // input intact). Clean-absent → return (no processing).
        var exists = await RetryLoop.ExecuteAsync(
            () => db.KeyExistsAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
        if (!exists.Succeeded) { await SendKeeper(BuildReinject(d, messageId), limit, ct); return; }
        if (!exists.Value) return;   // clean-absent → no processing (req 1)

        // req 2: read L2[entryId]. Read-fault → REINJECT + return. Input-invalid/deserialize-fail → ONE
        // StepFailed + return, with NO entry delete (C-2 / Pitfall 3 — left to TTL).
        var read = await RetryLoop.ExecuteAsync(async () =>
        {
            var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(d.EntryId));
            if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // A2: unify absent/empty with a Redis fault
            return raw.ToString();
        }, limit, ct);
        if (!read.Succeeded) { await SendKeeper(BuildReinject(d, messageId), limit, ct); return; }
        var validatedData = read.Value!;

        if (!ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, validatedData, out var inErrs))
        {
            await SendResult(BuildFailed(d, string.Join("; ", inErrs)), limit, ct);
            return;   // C-2: NO DeleteTerminalAsync / KeyDeleteAsync here (req 2 — left to TTL)
        }

        // req 3: seam returns one DataResult or null. Wire the framework helper-state onto the processor
        // FIRST (so this.SpawnToPost / this.DeleteEntry / this.NewResult have db/sendProvider/ids/hooks).
        SetSeamState(d, messageId, db, limit);
        DataResult? dr;
        try { dr = await processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct); }
        catch (ProcessStatusException e)
        {
            IStepResult res = e switch
            {
                FailedException     => BuildFailed(d, e.Message),
                CancelledException  => BuildCancelled(d, e.Message),
                ProcessingException => BuildProcessing(d),
                _                   => BuildFailed(d, e.Message),
            };
            if (e is ProcessingException) logger.LogInformation("ProcessAsync threw processing status: {Msg}", e.Message);
            await SendResult(res, limit, ct);
            return;   // C-2 parity: a seam-thrown failure does NOT delete the entry on this phase's model
        }
        catch (Exception ex)   // unexpected (incl. the deserialize JsonException, req 2) ⇒ failed, NO delete
        {
            await SendResult(BuildFailed(d, ex.Message), limit, ct);
            return;
        }

        if (dr is null) return;   // req 3: Mode-2 spawn handled everything; skip the tail (no write/send/delete)

        // req 4: inline tail = shared OutputTail (write completed-only → INJECT on exhaust → send by result),
        // THEN delete L2[entryId] (exhaust → DELETE). Carry the carried messageId onto the DataResult so the
        // output key + INJECT/REINJECT use it.
        var carried = dr with { MessageId = messageId };
        var proceed = await outputTail.RunAsync(carried, d.EntryId, ct);
        if (!proceed) return;   // INJECT escalation already ended the round trip (no delete)

        var del = await RetryLoop.ExecuteAsync(
            () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
        if (!del.Succeeded) await SendKeeper(BuildDelete(d), limit, ct);   // delete-exhaust → DELETE (req 4)
    }

    /// <summary>Populate the framework-owned per-dispatch state on the <see cref="BaseProcessor"/> the
    /// author calls into (this.SpawnToPost / this.DeleteEntry / this.NewResult). The two escalation hooks are
    /// closures over THIS pipeline's <see cref="SendKeeper"/> / logger / metrics so the author writes no
    /// RetryLoop/keeper code: the spawn-drop hook logs the swallow (executionId only — never the payload,
    /// T-70-10) + counts it; the delete-escalation hook fires the DELETE keeper send.</summary>
    private void SetSeamState(EntryStepDispatch d, Guid messageId, IDatabase db, int limit)
    {
        processor.SetSeamState(
            db, sendProvider, limit,
            entryId: d.EntryId,
            processorId: context.Id!.Value,
            messageId: messageId,
            workflowId: d.WorkflowId,
            stepId: d.StepId,
            correlationId: d.CorrelationId,
            onSpawnDropped: execId =>
            {
                logger.LogWarning(
                    "SpawnToPost drop: send to -post exhausted ExecutionId={ExecutionId}", execId);   // never log Payload (T-70-10)
                metrics.DispatchDeduped.Add(1,
                    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
            },
            escalateDelete: () => SendKeeper(BuildDelete(d), limit, CancellationToken.None));
    }

    // ---- Send owners: every send wrapped in RetryLoop; send-exhaustion PROPAGATES (throw → broker redelivery, no _error). ----

    private async Task SendResult(IStepResult result, int limit, CancellationToken ct)
    {
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{OrchestratorQueues.Result}"));
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)result, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery (no _error, Phase-53 D-01)

        metrics.ResultSent.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")),
            new KeyValuePair<string, object?>("outcome", ResultOutcome(result)));
    }

    /// <summary>Maps the concrete <see cref="IStepResult"/> record to a stable lowercase outcome tag value
    /// (completed/failed/cancelled/processing) for the <c>processor_result_sent_total</c> outcome label.</summary>
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
        var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));   // A2: keeper-recovery
        var sent = await RetryLoop.ExecuteAsync(
            async () => { await ep.Send((object)msg, CancellationToken.None); return true; }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery (Phase-53 D-01)
    }

    // ---- Builders (inherit-ids positional ctor + init; A1 id-sets). ----
    // REINJECT/DELETE carry the INBOUND dispatch ExecutionId (d.ExecutionId — may legitimately be Guid.Empty).

    private static StepFailed     BuildFailed(EntryStepDispatch d, string err) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = NewId.NextGuid(), EntryId = Guid.Empty, ErrorMessage = err };

    private static StepCancelled  BuildCancelled(EntryStepDispatch d, string msg) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = NewId.NextGuid(), EntryId = Guid.Empty, CancellationMessage = msg };

    private static StepProcessing BuildProcessing(EntryStepDispatch d) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId) { CorrelationId = d.CorrelationId, ExecutionId = NewId.NextGuid() };

    // req 6: REINJECT carries the carried messageId (the new KeeperReinject.MessageId field) so the keeper
    // re-injects with the SAME envelope messageId. Plus EntryId/Payload + the inbound ids.
    private static KeeperReinject BuildReinject(EntryStepDispatch d, Guid messageId) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId)
        { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId, MessageId = messageId, Payload = d.Payload };

    // req 8: the reshaped KeeperDelete is delete-only (entryId only — NO MessageId field).
    private static KeeperDelete   BuildDelete(EntryStepDispatch d) =>
        new(d.WorkflowId, d.StepId, d.ProcessorId)
        { CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, EntryId = d.EntryId };
}
