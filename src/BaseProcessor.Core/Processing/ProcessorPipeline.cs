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
    // TEST-ONLY reinject-TRIGGER seam (Phase 79 negative control) — env-gated, DEFAULT OFF, NEVER set in
    // production. A KeeperReinject only flows when a processor L2 read FAULTS (req 2 exhaustion); a healthy
    // no-crash run never faults, so the keeper's KEEPER_DEFEAT_REINJECT seam would have nothing to defeat.
    // PROCESSOR_DEFEAT_READ holds the TARGET step LABEL (e.g. "Step_B" — a LINEAR critical-path hop before the
    // fan-out, so losing it can't be masked by the convergent terminal Step_G). The fault is ARMED out-of-band
    // by the harness SETting the Redis key skp:test:defeat-read-arm at window-open — so the victim is an
    // after-RECOVERY_UTC execution (in the analyzer cohort). The first matching hop atomically claims the
    // single armed slot (KeyDelete → true for exactly ONE caller across replicas) and then faults ALL its read
    // retries by throwing KeyAbsentException BEFORE StringGet (L2 data left INTACT — nothing deleted), so the
    // RetryLoop exhausts → SendKeeper(BuildReinject) fires and the keeper finds ExecutionData PRESENT → logs
    // "reinject" (recoverable). Paired with KEEPER_DEFEAT_REINJECT (which loses that one redispatch) this
    // manufactures a byte-identical recoverable-but-lost binding miss. Unset/"0" ⇒ the block short-circuits
    // (no Redis touch, no claim) ⇒ behaviour byte-for-byte unchanged.
    private static Guid _reinjectTriggerTarget;  // the one victim EntryId whose read is faulted (per-process)

    public async Task RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)
    {
        var db = redis.GetDatabase();
        var limit = retryOptions.Value.Limit;

        // A SOURCE dispatch (entryId == Guid.Empty, SourceStep.IsSource) has NO upstream L2 input — it skips
        // the gate (req 1) + read (req 2) entirely and runs with EMPTY validatedData (the author seam uses
        // d.Payload for config). NOTE: the Phase-70 linear-Pre rewrite (341c1fc) dropped this source bypass,
        // which made every organic entry-step gate on exist L2[Guid.Empty] → clean-absent → return WITHOUT
        // processing (no result, no fault) — silently breaking every source round-trip. Restored here to the
        // pre-Phase-70 "Forward — Pre" contract ("a SourceStep.IsSource Guid.Empty dispatch skips the L2 read
        // with empty validatedData"). Downstream (non-source) steps still gate+read their relocated L2 input.
        string validatedData;
        if (SourceStep.IsSource(d.EntryId))
        {
            validatedData = string.Empty;
        }
        else
        {
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
                // TEST-ONLY (env-gated, DEFAULT OFF): PROCESSOR_DEFEAT_READ = the target step LABEL. When the
                // hop's payload carries that label AND the harness has armed the Redis slot, atomically claim it
                // (KeyDelete → true for exactly one caller across replicas) and fault ALL retries of the claimed
                // hop (throwing BEFORE StringGet, so L2 is left INTACT) → one recoverable KeeperReinject flows.
                // Short-circuit when unset/"0" ⇒ no Redis touch, no claim ⇒ inert (byte-for-byte unchanged).
                var defeatLabel = Environment.GetEnvironmentVariable("PROCESSOR_DEFEAT_READ");
                if (!string.IsNullOrEmpty(defeatLabel) && defeatLabel != "0"
                    // The label is a UNIQUE token in the payload (e.g. "Step_B" is not a substring of any other
                    // Step_* label), so a formatting-agnostic Contains matches whether the jsonb payload renders
                    // as {"label":"Step_B"} or the normalized {"label": "Step_B", "number": 1}.
                    && d.Payload.Contains(defeatLabel, StringComparison.Ordinal))
                {
                    if (_reinjectTriggerTarget == Guid.Empty
                        && await db.KeyDeleteAsync("skp:test:defeat-read-arm"))
                    {
                        _reinjectTriggerTarget = d.EntryId;   // this hop is the single victim (armed at window-open)
                        logger.LogWarning("TEST-79 reinject-trigger CLAIMED victim entryId={EntryId} label={Label}", d.EntryId, defeatLabel);
                    }
                    if (d.EntryId == _reinjectTriggerTarget)
                    {
                        logger.LogWarning("TEST-79 reinject-trigger FAULTING read for entryId={EntryId} → recoverable KeeperReinject", d.EntryId);
                        throw new KeyAbsentException();   // transient injected fault → RetryLoop exhausts → REINJECT
                    }
                }
                var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(d.EntryId));
                if (raw.IsNullOrEmpty) throw new KeyAbsentException();   // A2: unify absent/empty with a Redis fault
                return raw.ToString();
            }, limit, ct);
            if (!read.Succeeded) { await SendKeeper(BuildReinject(d, messageId), limit, ct); return; }
            validatedData = read.Value!;
        }

        if (!ProcessorJsonSchemaValidator.TryValidate(context.InputDefinition, validatedData, out var inErrs))
        {
            // D-07: route the input-schema failure THROUGH the OutputTail (write L2[out:messageId]=validatedData
            // + send StepFailed with a real EntryId) instead of a bypass SendResult. C-2/D-08: no entry delete.
            var failDr = new DataResult(d.WorkflowId, d.StepId, d.ProcessorId)
            {
                CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, MessageId = messageId,
                Result = StepOutcome.Failed, Data = validatedData, ErrorMessage = string.Join("; ", inErrs),
            };
            _ = await outputTail.RunAsync(failDr, d.EntryId, ct);   // outcome known (Failed); tuple discarded
            LogHopExecuted(d, messageId, nameof(StepOutcome.Failed));   // FW-01: input-schema-fail is an executed hop
            return;   // C-2: NO DeleteTerminalAsync / KeyDeleteAsync here (req 2 — left to TTL)
        }

        // req 3: seam returns one DataResult or null. Wire the framework helper-state onto the processor
        // FIRST (so this.SpawnToPost / this.DeleteEntry / this.NewResult have db/sendProvider/ids/hooks).
        // CR-01: the state lives in a per-consume AsyncLocal on the (Singleton) processor; clear it in a
        // finally so a stale capture never outlives this dispatch on a pooled thread.
        SetSeamState(d, messageId, db, limit);
        try
        {
            DataResult? dr;
            try { dr = await processor.ExecuteAsync(validatedData, d.Payload, d.ExecutionId, ct); }
            catch (ProcessStatusException e)
            {
                // D-07: a seam-thrown status routes THROUGH the OutputTail carrying Data=validatedData (so the
                // terminal write + real EntryId happen uniformly). The Processing case rides the tail's
                // result!=Processing gate → NO blob, EntryId=Guid.Empty (D-04).
                var statusDr = new DataResult(d.WorkflowId, d.StepId, d.ProcessorId)
                {
                    CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, MessageId = messageId,
                    Data = validatedData,
                    Result = e switch
                    {
                        FailedException     => StepOutcome.Failed,
                        CancelledException  => StepOutcome.Cancelled,
                        ProcessingException => StepOutcome.Processing,
                        _                   => StepOutcome.Failed,
                    },
                    ErrorMessage        = e is CancelledException ? "" : e.Message,
                    CancellationMessage = e is CancelledException ? e.Message : "",
                };
                if (e is ProcessingException) logger.LogInformation("ProcessAsync threw processing status: {Msg}", e.Message);
                _ = await outputTail.RunAsync(statusDr, d.EntryId, ct);   // outcome known from statusDr; tuple discarded
                // FW-01: a seam-thrown Failed/Cancelled is an executed hop; the transient Processing status is
                // NOT (D-05 — one record at a terminal outcome), so skip the per-hop record for it.
                if (statusDr.Result != StepOutcome.Processing)
                    LogHopExecuted(d, messageId, statusDr.Result.ToString());
                return;   // C-2 parity: a seam-thrown failure does NOT delete the entry on this phase's model
            }
            catch (Exception ex)   // unexpected (incl. the deserialize JsonException, req 2) ⇒ failed, NO delete
            {
                // WR-03: never put ex.Message on the StepFailed wire — a deserialize JsonException can carry a
                // fragment of the offending payload/config (path, line, token), which defeats the never-log-
                // payload discipline (T-70-10). Emit a sanitized constant; the detail stays in the local log.
                // D-07: route through the OutputTail so the blob (Data=validatedData) + real EntryId are written.
                logger.LogWarning(ex, "ProcessAsync/deserialize faulted; emitting StepFailed (entry left to TTL)");
                var unexpectedDr = new DataResult(d.WorkflowId, d.StepId, d.ProcessorId)
                {
                    CorrelationId = d.CorrelationId, ExecutionId = d.ExecutionId, MessageId = messageId,
                    Result = StepOutcome.Failed, Data = validatedData, ErrorMessage = "input deserialization failed",
                };
                _ = await outputTail.RunAsync(unexpectedDr, d.EntryId, ct);   // outcome known (Failed); tuple discarded
                LogHopExecuted(d, messageId, nameof(StepOutcome.Failed));   // FW-01: an unexpected fault is an executed hop
                return;
            }

            // req 3: Mode-2 spawn handled everything; skip the tail (no write/send/delete). D-09: this IS an
            // executed entry hop — log it with the outcome "Completed" and d.ExecutionId passed EXPLICITLY (it is
            // Guid.Empty for the entry step; the ambient scope OMITS empty GUIDs, so the all-zeros marker must be
            // an explicit placeholder arg to surface in ES).
            if (dr is null) { LogHopExecuted(d, messageId, nameof(StepOutcome.Completed)); return; }

            // req 4: inline tail = shared OutputTail (write completed-only → INJECT on exhaust → send by result),
            // THEN delete L2[entryId] (exhaust → DELETE). Carry the carried messageId onto the DataResult so the
            // output key + INJECT/REINJECT use it.
            var carried = dr with { MessageId = messageId };
            var (proceed, resolvedOutcome) = await outputTail.RunAsync(carried, d.EntryId, ct);
            if (!proceed) return;   // INJECT escalation already ended the round trip (no delete, no per-hop record)

            // FW-01 / D-18: the normal-path hop executed — log the TRUE terminal outcome the OutputTail resolved
            // (a Completed result whose output blob failed the output schema is logged as Failed, matching the
            // Step* wire). Emitted AFTER the !proceed guard so an INJECT-escalated hop does NOT log here.
            LogHopExecuted(d, messageId, resolvedOutcome.ToString());

            // A source step (Guid.Empty) has NO L2 input key to reclaim — skip the delete tail for it
            // (pre-Phase-70 "Forward — source-delete tail … Skipped on a Guid.Empty source step").
            if (!SourceStep.IsSource(d.EntryId))
            {
                var del = await RetryLoop.ExecuteAsync(
                    () => db.KeyDeleteAsync(L2ProjectionKeys.ExecutionData(d.EntryId)), limit, ct);
                if (!del.Succeeded) await SendKeeper(BuildDelete(d), limit, ct);   // delete-exhaust → DELETE (req 4)
            }
        }
        finally { processor.ClearSeamState(); }   // CR-01: per-consume AsyncLocal cleanup
    }

    /// <summary>FW-01/D-05/D-06/D-08/D-10 + D1/LOG-01: emit the framework's own per-hop execution record — ONE
    /// structured <see cref="LogLevel.Information"/> line carrying the Tier-2 <c>{MessageId}</c> (the single
    /// delivery id the ambient scope does NOT carry) + the Tier-3 <c>{Outcome}</c> (D6/LOG-06 — the emitting
    /// component knows the terminal outcome at emit time) as explicit <c>{Placeholder}</c> args (never
    /// <c>$"..."</c>, never a payload arg — FW-03/T-76-01). The five Tier-1 ids
    /// (WorkflowId/StepId/ProcessorId/ExecutionId/EntryId) are NO LONGER restated in the template — they arrive
    /// on the record from the ambient bus-wide <c>InboundExecutionScopeConsumeFilter</c> scope as ES
    /// <c>attributes.*</c> (D1/LOG-01: one uniform pattern, no id ever duplicated in a string the scope already
    /// carries). "Did this step execute" stays platform-level operability for ANY processor with zero
    /// author-written logs.
    /// <para>
    /// PHASE-78 HANDOFF (documented, not fixed here): the Mode-2 entry step has <see cref="Guid.Empty"/>
    /// ExecutionId. With <c>{ExecutionId}</c> no longer an explicit arg, <c>ExecutionLogScope.BuildState</c>
    /// skips the empty GUID, so the entry marker's <c>attributes.ExecutionId</c> becomes ABSENT (not an
    /// all-zeros value). The live analyzer's entry-marker detection (<c>PassFailEngine.IsEntryMarker</c>,
    /// <c>== Guid.Empty</c>) must switch to "attribute absent" — that is Phase 78 (Category=RealStack).
    /// </para>
    /// <para>
    /// FW-04/T-76-02: the call is wrapped in a swallow-guard — a throwing/blocking <see cref="ILogger"/> must
    /// NEVER fail or delay the hop, and the pipeline outcome is unchanged whether the log succeeds or throws.
    /// </para></summary>
    private void LogHopExecuted(EntryStepDispatch d, Guid messageId, string outcome)
    {
        try
        {
            logger.LogInformation("hop executed {MessageId} {Outcome}", messageId, outcome);
        }
        catch { /* FW-04: observability must never fail or delay the hop */ }
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
                // IN-03: a spawn drop is NOT a dispatch dedup — count it on its own SpawnDropped signal so the
                // dedup rate stays readable and the spawn-drop rate is observable under its real name.
                metrics.SpawnDropped.Add(1,
                    new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));
            },
            escalateDelete: () => SendKeeper(BuildDelete(d), limit, CancellationToken.None));
    }

    // ---- Send owners: every send wrapped in RetryLoop; send-exhaustion PROPAGATES (throw → broker redelivery, no _error). ----
    // Phase 72 (D-07): the Step* result send now lives ENTIRELY in OutputTail (every terminal/Processing result
    // routes through outputTail.RunAsync). The pipeline keeps only the keeper send below for REINJECT/DELETE.

    private async Task SendKeeper(IKeeperRecoverable msg, int limit, CancellationToken ct)
    {
        // IN-04: resolve GetSendEndpoint INSIDE the RetryLoop (keeper Guard parity) so a transient
        // endpoint-resolution fault is retried like the send — matching OutputTail.SendKeeper and
        // OrchestratorPrePipeline.SendKeeper. A2: keeper-recovery. An exhaust still throws → broker redelivery.
        var sent = await RetryLoop.ExecuteAsync(async () =>
        {
            var ep = await sendProvider.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));
            await ep.Send((object)msg, CancellationToken.None);
            return true;
        }, limit, ct);
        if (!sent.Succeeded) throw sent.Error!;   // propagate → throw → broker redelivery (Phase-53 D-01)
    }

    // ---- Builders (inherit-ids positional ctor + init; A1 id-sets). ----
    // REINJECT/DELETE carry the INBOUND dispatch ExecutionId (d.ExecutionId — may legitimately be Guid.Empty).
    // Phase 72 (D-07/A2): BuildFailed/BuildCancelled/BuildProcessing are removed — their former callers now
    // build a DataResult{Data=validatedData} and route it through OutputTail (which owns the Step* construction).

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
