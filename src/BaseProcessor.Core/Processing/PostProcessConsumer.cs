using BaseProcessor.Core.Identity;       // IProcessorContext
using BaseProcessor.Core.Observability;  // ProcessorMetrics
using MassTransit;
using Messaging.Contracts;

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
    ProcessorMetrics metrics) : IConsumer<DataResult>
{
    public async Task Consume(ConsumeContext<DataResult> ctx)
    {
        // Reuse the dispatch-consumed counter for the Post hop (tagged ProcessorId). context.Id is resolved
        // here — the -post endpoint binds only post-identity (mirrors the entry consumer's bang).
        metrics.DispatchConsumed.Add(1,
            new KeyValuePair<string, object?>("ProcessorId", context.Id!.Value.ToString("D")));

        // Post never touches an entry → deleteEntryId = Guid.Empty (the INJECT delete no-ops on it).
        await outputTail.RunAsync(ctx.Message, Guid.Empty, ctx.CancellationToken);
    }
}
