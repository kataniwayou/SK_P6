using MassTransit;
using Messaging.Contracts;
using Orchestrator.Dispatch;

namespace Orchestrator.Consumers;

/// <summary>
/// D-15/D-16 (Phase 71): the orchestrator-side Post-Process consumer — a thin
/// <c>IConsumer&lt;NextStepHandoff&gt;</c> shell over the shared <see cref="RelocateTail"/>. It receives one
/// <see cref="NextStepHandoff"/> per fan-out target on the <see cref="OrchestratorQueues.ResultPost"/> queue
/// (startup-bound, no bus retry / no error transport) and runs the relocate tail ONLY: write
/// <c>L2[data:messageId]=Data</c> on a Completed continuation (INJECT on write-exhaust) → dispatch the next
/// <see cref="EntryStepDispatch"/> with <c>entryId = ctx.MessageId</c>. A send-exhaust throws out of the tail
/// → RabbitMQ nack-requeue (broker redelivery).
/// <para>
/// Unlike the processor's <c>PostProcessConsumer</c> there is NO per-instance provenance self-check: the
/// orchestrator post queue is a single competing-consumer endpoint with no per-instance identity (no
/// <c>context.Id</c> to compare against). The handoff ids are kept internally consistent and the payload is
/// never logged (T-70-10) — this shell does no logging at all, ids-only by construction.
/// </para>
/// </summary>
public sealed class OrchestratorPostProcessConsumer(RelocateTail relocateTail) : IConsumer<NextStepHandoff>
{
    public async Task Consume(ConsumeContext<NextStepHandoff> ctx) =>
        // D-11: messageId = ctx.MessageId (the fresh envelope id the Pre fan-out let MassTransit assign) —
        // the data: write key + the dispatched entryId.
        await relocateTail.RunAsync(ctx.Message, ctx.MessageId ?? Guid.Empty, ctx.CancellationToken);
}
