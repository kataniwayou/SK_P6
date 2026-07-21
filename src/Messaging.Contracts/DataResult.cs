namespace Messaging.Contracts;

// bus envelope — NO [JsonPropertyName], default STJ serialization (matches Step*/Keeper* convention).
/// <summary>D-01/D-02: the unified DataResult spine — simultaneously the ProcessAsync seam return type,
/// the Post-Process wire contract (IConsumer&lt;DataResult&gt;), and the body embedded in KeeperInject
/// (D-13). Self-contained: Post and INJECT need nothing from elsewhere (req 7). Result reuses the
/// existing 4-value StepOutcome (D-04). MessageId is the carried envelope id — the key for
/// L2[messageId]=data (OutputData).</summary>
public sealed record DataResult(Guid WorkflowId, Guid StepId, Guid ProcessorId)
{
    public Guid CorrelationId { get; init; }
    public Guid ExecutionId   { get; init; }
    public Guid MessageId     { get; init; }   // carried messageId — envelope key for L2[messageId]=data
    public StepOutcome Result { get; init; }   // D-04: existing 4-value enum (Processing/Completed/Failed/Cancelled)
    // D-01 (Phase 84): byte[] is the single ground-truth payload spine. JSON/UTF-8 is an OPTIONAL schema
    // lens over these bytes (decode+validate only when a schema is configured); with no schema the bytes
    // are opaque. Redis already stores these exact bytes — the widening exposes them, it adds no new
    // representation. One field, always bytes — no parallel payload field, no discriminator (D-01). Edge string conversions live
    // in DataResultExtensions.DataAsString (read) and BaseProcessor.NewResult(StepOutcome,string) (write).
    public byte[] Data        { get; init; } = Array.Empty<byte>();   // ground-truth payload (D-01)

    // Phase 72 (D-07): the diagnostic the OutputTail stamps onto the built Step* so the catch/input-fail
    // paths (which now route THROUGH OutputTail, not a direct SendResult) can carry a real message instead
    // of the BuildStep hard-coded constants. Default "" → BuildStep falls back to its existing constant.
    public string ErrorMessage        { get; init; } = "";   // StepFailed diagnostic (Failed arm)
    public string CancellationMessage { get; init; } = "";   // StepCancelled diagnostic (Cancelled arm)
}
