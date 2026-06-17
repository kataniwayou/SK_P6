using BaseProcessor.Core.Identity;       // IProcessorContext
using BaseProcessor.Core.Observability;  // ProcessorMetrics
using MassTransit;
using Messaging.Contracts;
using Microsoft.Extensions.Logging;

namespace BaseProcessor.Core.Processing;

/// <summary>
/// D-15/D-16: the Post-Process consumer — receives a <see cref="DataResult"/> on
/// <c>queue:{processorId:D}-post</c> (bound at runtime like the entry endpoint, same posture: no bus retry,
/// no error transport) and runs the shared <see cref="OutputTail"/> ONLY (validate output → write
/// L2[messageId]=data on Completed → INJECT on write-exhaust → send Step* by result). It NEVER reads or
/// deletes L2[entryId] (req 5) — it passes <see cref="Guid.Empty"/> as the deleteEntryId, and the
/// <see cref="OutputTail"/> return value is ignored (Post has no entry-delete tail). A send-exhaust throws
/// out of the tail → RabbitMQ nack-requeue (broker redelivery).
/// </summary>
public sealed class PostProcessConsumer(
    OutputTail outputTail,
    IProcessorContext context,
    ProcessorMetrics metrics,
    ILogger<PostProcessConsumer> logger) : IConsumer<DataResult>
{
    public async Task Consume(ConsumeContext<DataResult> ctx)
    {
        var self = context.Id!.Value;   // resolved here — the -post endpoint binds only post-identity.

        // WR-02: provenance guard. The -post endpoint is a NEW ingress that carries the OUTPUT ids + the
        // output write key (dr.MessageId) directly in the body and performs an L2 write + a result emit from
        // them. A legitimate -post message for THIS processor must carry THIS processor's id (the Mode-2 spawn
        // stamps dr.ProcessorId = the processor's id via NewResult). A DataResult whose ProcessorId is not us
        // would forge a step outcome / write an arbitrary blob for another processor's lineage — drop it
        // (never log the payload — T-70-10; ids only).
        if (ctx.Message.ProcessorId != self)
        {
            logger.LogWarning(
                "Post drop: DataResult ProcessorId={Carried} does not match this processor {Self}; dropping",
                ctx.Message.ProcessorId, self);
            return;
        }

        // Reuse the dispatch-consumed counter for the Post hop (tagged ProcessorId). context.Id is resolved
        // here — the -post endpoint binds only post-identity (mirrors the entry consumer's bang).
        metrics.DispatchConsumed.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", self.ToString("D")));

        // Post never touches an entry → deleteEntryId = Guid.Empty (the INJECT delete no-ops on it).
        await outputTail.RunAsync(ctx.Message, Guid.Empty, ctx.CancellationToken);
    }
}
