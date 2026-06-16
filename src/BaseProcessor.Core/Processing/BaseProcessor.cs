using BaseConsole.Core.Resilience;       // RetryLoop / RetryOutcome
using MassTransit;                        // ISendEndpointProvider / Send
using Messaging.Contracts;                // DataResult / StepOutcome
using Messaging.Contracts.Projections;    // L2ProjectionKeys
using StackExchange.Redis;                // IDatabase

namespace BaseProcessor.Core.Processing;

/// <summary>
/// The non-generic processor base (D-01 / BPC-02). It declares the framework-internal
/// <see cref="ExecuteAsync"/> seam that the <c>ProcessorPipeline</c> resolves and calls — the pipeline
/// lives in THIS assembly and references the non-generic type, so the seam stays here and stays
/// signature-stable (string,string,Guid,ct). The typed author transform seam lives on the generic
/// <see cref="BaseProcessor{TConfig}"/>, which supplies this method's body by deserializing the
/// dispatch payload into the author config type before invoking the author's typed <c>ProcessAsync</c>.
/// A concrete <c>Processor.&lt;Purpose&gt;</c> derives from <see cref="BaseProcessor{TConfig}"/> and
/// overrides ONLY the typed transform; it never sees this <c>internal</c> seam (BPC-02).
/// <para>
/// Phase 70 (D-03/D-05/D-06): the seam now returns <see cref="DataResult"/>? (one-or-null), and the
/// base exposes the two protected author helpers <see cref="SpawnToPost"/> (Mode-2 spawn to the
/// <c>-post</c> queue, SWALLOW on send-exhaust — D-07) and <see cref="DeleteEntry"/> (delete L2[entryId],
/// escalate to the DELETE keeper on exhaust — D-08), plus the <see cref="NewResult"/> factory that stamps
/// the ambient ids + carried messageId so the author writes no id/envelope plumbing. The framework
/// populates the per-dispatch state below (via <c>ProcessorPipeline.SetSeamState</c>) BEFORE invoking the
/// seam; the author never sets it.
/// </para>
/// </summary>
public abstract class BaseProcessor
{
    // ---- Framework-populated per-dispatch state (set by ProcessorPipeline before invoking the seam). ----
    // Not author-set. Nullable / default until the pipeline wires them per consume.
    private protected IDatabase? _db;
    private protected ISendEndpointProvider? _sendProvider;
    private protected int _retryLimit;
    private protected Guid _entryId;       // inbound source entryId (DeleteEntry operand)
    private protected Guid _processorId;   // bound processor id (the -post queue name)

    // Ambient ids carried onto NewResult (so the author writes no id/envelope plumbing).
    private protected Guid _messageId;
    private protected Guid _workflowId;
    private protected Guid _stepId;
    private protected Guid _correlationId;

    // Escalation hooks the pipeline supplies (Func<Task> — no sync-over-async).
    private protected Action<Guid>? _onSpawnDropped;   // metric+warn hook (swallow telemetry)
    private protected Func<Task>? _escalateDelete;     // pipeline-provided DELETE escalation (keeper send)

    /// <summary>
    /// Framework-internal invoker (EXEC-04 / D-01). The Phase 44 <c>ProcessorPipeline</c> lives in this
    /// SAME assembly and references the non-generic <see cref="BaseProcessor"/>; this <c>internal abstract</c>
    /// seam is the method it calls. <see cref="BaseProcessor{TConfig}"/> supplies the body (deserialize
    /// the <paramref name="payload"/> into the typed config, then dispatch to the author transform).
    /// <paramref name="executionId"/> carries the inbound dispatch's per-instance id: <c>Guid.Empty</c>
    /// marks an ENTRY/seed dispatch (the author may mint fresh ids per spawned execution), a non-empty
    /// value marks a DOWNSTREAM continuation whose lineage the author reuses unchanged. Returns ONE
    /// <see cref="DataResult"/> (downstream) or <c>null</c> (the author handled spawn+handoff — D-05).
    /// </summary>
    internal abstract Task<DataResult?> ExecuteAsync(
        string validatedData, string payload, Guid executionId, CancellationToken ct);

    /// <summary>D-06/D-07/D-09: send a <see cref="DataResult"/> to the Post-Process queue
    /// (<c>queue:{processorId:D}-post</c>), overriding the outbound envelope MessageId with the result's
    /// MessageId (req 11). The author mints the distinct <paramref name="executionId"/> per spawn (D-09).
    /// Bounded <see cref="RetryLoop"/> then SWALLOW on send-exhaust (no throw, no keeper) — the scheduler
    /// re-fires the whole entry (Mode-2 is best-effort).</summary>
    protected async Task SpawnToPost(DataResult result, Guid executionId)
    {
        var spawn = result with { ExecutionId = executionId };   // author mints execId (D-09)
        var ep = await _sendProvider!.GetSendEndpoint(new Uri($"queue:{_processorId:D}-post"));
        var sent = await RetryLoop.ExecuteAsync(async () =>
        {
            // req 11 / Pattern 3: per-send envelope MessageId override (carried messageId). (object) cast
            // is load-bearing (CapturingSendProvider captures the object overload — Pitfall 4).
            await ep.Send((object)spawn, ctx => ctx.MessageId = spawn.MessageId, CancellationToken.None);
            return true;
        }, _retryLimit, CancellationToken.None);
        if (!sent.Succeeded) _onSpawnDropped?.Invoke(spawn.ExecutionId);   // SWALLOW (log+counter); scheduler re-fires
    }

    /// <summary>D-08: delete L2[entryId] (the inbound source) with a bounded <see cref="RetryLoop"/>,
    /// escalating to the redefined DELETE keeper state (delete-only, no orchestrator send) on exhaust.
    /// The author writes no RetryLoop/keeper code.</summary>
    protected async Task DeleteEntry()
    {
        var del = await RetryLoop.ExecuteAsync(
            () => _db!.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(_entryId)), _retryLimit, CancellationToken.None);
        if (!del.Succeeded && _escalateDelete is { } escalate) await escalate();   // → KeeperDelete (delete-only)
    }

    /// <summary>Factory the author uses to build a <see cref="DataResult"/> without threading ids/messageId:
    /// the framework stamps the ambient WorkflowId/StepId/ProcessorId/CorrelationId + the carried MessageId
    /// (the L2[messageId] output key). ExecutionId is left Guid.Empty — Mode-1 callers may
    /// <c>with { ExecutionId = inbound }</c>; Mode-2 spawns pass the minted id to <see cref="SpawnToPost"/>.</summary>
    protected DataResult NewResult(StepOutcome result, string data) =>
        new(_workflowId, _stepId, _processorId)
        {
            CorrelationId = _correlationId,
            ExecutionId   = Guid.Empty,
            MessageId     = _messageId,
            Result        = result,
            Data          = data,
        };

    /// <summary>Framework hook (called by <c>ProcessorPipeline</c> before the seam): populate the
    /// per-dispatch state + escalation hooks the protected helpers read. Not author-callable from the seam
    /// (the author only sees the typed <c>ProcessAsync</c>).</summary>
    internal void SetSeamState(
        IDatabase db, ISendEndpointProvider sendProvider, int retryLimit,
        Guid entryId, Guid processorId, Guid messageId, Guid workflowId, Guid stepId, Guid correlationId,
        Action<Guid> onSpawnDropped, Func<Task> escalateDelete)
    {
        _db = db;
        _sendProvider = sendProvider;
        _retryLimit = retryLimit;
        _entryId = entryId;
        _processorId = processorId;
        _messageId = messageId;
        _workflowId = workflowId;
        _stepId = stepId;
        _correlationId = correlationId;
        _onSpawnDropped = onSpawnDropped;
        _escalateDelete = escalateDelete;
    }
}
