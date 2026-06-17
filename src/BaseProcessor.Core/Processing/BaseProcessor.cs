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
    // CR-01: the author registers this type as a SINGLETON (the documented contract for a stateless
    // transform — BaseProcessorServiceCollectionExtensions:96), but the per-dispatch seam state is NOT
    // stateless: every consume writes the inbound entryId/messageId/ids + the EscalateDelete closure that
    // captures THAT consume's dispatch. Storing these as plain instance fields on the shared singleton races
    // under concurrent consumes (no ConcurrentMessageLimit on the entry/-post endpoints), so consume B could
    // clobber consume A's entryId and A's DeleteEntry/NewResult/SpawnToPost would act on B's lineage
    // (wrong-key write, wrong entry delete — exactly the silent-loss class the SPEC forbids). The fix:
    // hold the per-dispatch state in an AsyncLocal so each consume's async flow sees its OWN isolated copy.
    // The registration stays Singleton and the author-facing API (SetSeamState/SpawnToPost/DeleteEntry/
    // NewResult) is unchanged — only the backing store moved off the shared instance.
    private readonly AsyncLocal<SeamState?> _seam = new();

    /// <summary>CR-01: the per-dispatch seam state held in an <see cref="AsyncLocal{T}"/> so concurrent
    /// consumes on a singleton <see cref="BaseProcessor"/> do not corrupt each other's ids/hooks. Populated
    /// by <see cref="SetSeamState"/> at the top of each consume; read by the protected helpers; never shared
    /// across consumes (each async flow gets its own).</summary>
    private protected sealed class SeamState
    {
        public IDatabase? Db;
        public ISendEndpointProvider? SendProvider;
        public int RetryLimit;
        public Guid EntryId;       // inbound source entryId (DeleteEntry operand)
        public Guid ProcessorId;   // bound processor id (the -post queue name)

        // Ambient ids carried onto NewResult (so the author writes no id/envelope plumbing).
        public Guid MessageId;
        public Guid WorkflowId;
        public Guid StepId;
        public Guid CorrelationId;

        // Escalation hooks the pipeline supplies (Func<Task> — no sync-over-async).
        public Action<Guid>? OnSpawnDropped;   // metric+warn hook (swallow telemetry)
        public Func<Task>? EscalateDelete;     // pipeline-provided DELETE escalation (keeper send)
    }

    /// <summary>The current consume's seam state — throws if a helper is called before
    /// <see cref="SetSeamState"/> ran on this async flow (a framework wiring bug, never an author one).</summary>
    private SeamState Seam =>
        _seam.Value ?? throw new InvalidOperationException(
            "Seam state was not set for this dispatch (SetSeamState must run before the seam helpers).");

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
        var s = Seam;
        var spawn = result with { ExecutionId = executionId };   // author mints execId (D-09)
        var sent = await RetryLoop.ExecuteAsync(async () =>
        {
            // WR-01: resolve GetSendEndpoint INSIDE the RetryLoop (mirror the keeper Guard pattern) so a
            // transient GetSendEndpoint fault (bus not fully started, transport blip) routes through the
            // bounded retry like the send itself, instead of throwing straight out of SpawnToPost.
            var ep = await s.SendProvider!.GetSendEndpoint(new Uri($"queue:{s.ProcessorId:D}-post"));
            // req 11 / Pattern 3: per-send envelope MessageId override (carried messageId). (object) cast
            // is load-bearing (CapturingSendProvider captures the object overload — Pitfall 4).
            await ep.Send((object)spawn, ctx => ctx.MessageId = spawn.MessageId, CancellationToken.None);
            return true;
        }, s.RetryLimit, CancellationToken.None);

        if (sent.Succeeded) return;

        // WR-01: narrow the SWALLOW to the TRANSIENT send-fault class only. The design intent is
        // "swallow on send-EXHAUSTION so the scheduler re-fires the whole entry" (Mode-2 is best-effort).
        // A deterministic programming/serialization fault (ArgumentException, NotSupportedException, …)
        // would fail identically on every re-fire and be silently lost forever, so let it surface →
        // ProcessAsync faults → broker redelivery (and a real stack trace), instead of a warn+counter.
        if (!IsTransientSendFault(sent.Error)) throw sent.Error!;
        s.OnSpawnDropped?.Invoke(spawn.ExecutionId);   // SWALLOW (log+counter); scheduler re-fires
    }

    /// <summary>WR-01: is this an exhausted-send fault we may SWALLOW (transient transport/Redis), as opposed
    /// to a deterministic programming/serialization fault that must surface? Matched by type name so the base
    /// assembly needs no extra package reference; covers the StackExchange.Redis + MassTransit/RabbitMQ
    /// transport families plus the low-level socket/IO/timeout layer they wrap.</summary>
    private static bool IsTransientSendFault(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.IO.IOException or TimeoutException or OperationCanceledException
                or System.Net.Sockets.SocketException)
                return true;
            var n = e.GetType().Name;
            if (n is "RedisConnectionException" or "RedisTimeoutException" or "RedisServerException"
                or "BrokerUnreachableException" or "ConnectionException" or "RabbitMqConnectionException"
                or "MessageNotConfirmedException")
                return true;
        }
        return false;
    }

    /// <summary>D-08: delete L2[entryId] (the inbound source) with a bounded <see cref="RetryLoop"/>,
    /// escalating to the redefined DELETE keeper state (delete-only, no orchestrator send) on exhaust.
    /// The author writes no RetryLoop/keeper code.</summary>
    protected async Task DeleteEntry()
    {
        var s = Seam;
        var del = await RetryLoop.ExecuteAsync(
            () => s.Db!.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(s.EntryId)), s.RetryLimit, CancellationToken.None);
        if (!del.Succeeded && s.EscalateDelete is { } escalate) await escalate();   // → KeeperDelete (delete-only)
    }

    /// <summary>Factory the author uses to build a <see cref="DataResult"/> without threading ids/messageId:
    /// the framework stamps the ambient WorkflowId/StepId/ProcessorId/CorrelationId + the carried MessageId
    /// (the L2[messageId] output key). ExecutionId is left Guid.Empty — Mode-1 callers may
    /// <c>with { ExecutionId = inbound }</c>; Mode-2 spawns pass the minted id to <see cref="SpawnToPost"/>.</summary>
    protected DataResult NewResult(StepOutcome result, string data)
    {
        var s = Seam;
        return new(s.WorkflowId, s.StepId, s.ProcessorId)
        {
            CorrelationId = s.CorrelationId,
            ExecutionId   = Guid.Empty,
            MessageId     = s.MessageId,
            Result        = result,
            Data          = data,
        };
    }

    /// <summary>Framework hook (called by <c>ProcessorPipeline</c> before the seam): populate the
    /// per-dispatch state + escalation hooks the protected helpers read. Not author-callable from the seam
    /// (the author only sees the typed <c>ProcessAsync</c>).</summary>
    internal void SetSeamState(
        IDatabase db, ISendEndpointProvider sendProvider, int retryLimit,
        Guid entryId, Guid processorId, Guid messageId, Guid workflowId, Guid stepId, Guid correlationId,
        Action<Guid> onSpawnDropped, Func<Task> escalateDelete)
    {
        // CR-01: publish a FRESH state object onto the AsyncLocal for THIS consume's async flow. A fresh
        // instance (never mutated in place) means a sibling consume that already captured its own _seam.Value
        // is unaffected — no cross-message bleed on the shared singleton.
        _seam.Value = new SeamState
        {
            Db = db,
            SendProvider = sendProvider,
            RetryLimit = retryLimit,
            EntryId = entryId,
            ProcessorId = processorId,
            MessageId = messageId,
            WorkflowId = workflowId,
            StepId = stepId,
            CorrelationId = correlationId,
            OnSpawnDropped = onSpawnDropped,
            EscalateDelete = escalateDelete,
        };
    }

    /// <summary>CR-01: clear THIS consume's seam state once the seam returns (the pipeline calls this in a
    /// finally). AsyncLocal already isolates concurrent consumes; clearing additionally prevents a stale
    /// captured EscalateDelete closure from outliving its dispatch on a pooled thread.</summary>
    internal void ClearSeamState() => _seam.Value = null;
}
