using System.Text.Json;                  // JsonSerializer — NOT in .NET 8 implicit usings
using BaseProcessor.Core.Configuration;  // ProcessorConfig.SerializerOptions
using BaseProcessor.Core.Processing;
using Messaging.Contracts;               // DataResult / StepOutcome / ExecutionLogScope
using Microsoft.Extensions.Logging;

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessor{SampleConfig}"/> (PROC-02/PROC-03). It overrides ONLY the
/// typed In-Process <c>ProcessAsync</c> seam; the framework owns deserialization (it hands in a typed
/// <see cref="SampleConfig"/>) and the entire Pre/Post/end-delete pipeline.
/// <para>
/// Phase 70 (req 10): the two-mode worked example, keyed on the inbound <c>executionId</c>:
/// <list type="bullet">
///   <item><b>ENTRY/seed</b> (<c>executionId == Guid.Empty</c>, Mode-2): generates TWO numbers (each
///   <c>baseNumber + random[0,99]</c>), logs <c>"{label} had the following numbers: …"</c>, then SPAWNS two
///   completed <see cref="DataResult"/>s to the Post-Process queue via <c>SpawnToPost</c>
///   with DISTINCT freshly-minted executionIds (D-09, swallow), DELETES the inbound entry via
///   <c>DeleteEntry</c>, and RETURNS NULL — the framework writes/sends/deletes nothing
///   inline (req 3).</item>
///   <item><b>DOWNSTREAM</b> (<c>executionId != Guid.Empty</c>, Mode-1): accumulates ONE number
///   (<c>incomingNumber + baseNumber</c>, deterministic — NO random), logs the same line, and RETURNS ONE
///   completed <see cref="DataResult"/> REUSING the inbound executionId — the framework's inline tail runs
///   (no spawn, no delete).</item>
/// </list>
/// The author writes NO RetryLoop/keeper/envelope code — <c>SpawnToPost</c> /
/// <c>DeleteEntry</c> own resilience, and <c>NewResult</c> stamps
/// the ambient ids + carried messageId.
/// </para>
/// </summary>
/// <remarks>
/// DI-registered <c>AddSingleton&lt;BaseProcessor, SampleProcessor&gt;</c> (Program.cs:17, unchanged) —
/// SampleProcessor IS-A BaseProcessor via BaseProcessor&lt;SampleConfig&gt;.
/// </remarks>
public sealed class SampleProcessor(ILogger<SampleProcessor> logger) : BaseProcessor<SampleConfig>
{
    /// <inheritdoc/>
    protected override async Task<DataResult?> ProcessAsync(
        string validatedData, SampleConfig? config, Guid executionId, CancellationToken ct)
    {
        var baseNumber = config?.Number ?? 0;            // D-03 null-config default — warning-clean guard (Pitfall 2)
        var label      = config?.Label;                  // D-10 verbatim — already "Step_*", do NOT prepend

        if (executionId == Guid.Empty)
        {
            // ENTRY/seed (Mode-2): generate 2 numbers, log the line, spawn 2 to Post (distinct minted execIds,
            // swallow on exhaust), delete the inbound entry, return null.
            // D-01/D-02: seed the two FIXED execution values (100, 200). Every step's payload number = 1
            // (seeder data change, Plan 04) so each downstream hop increments by exactly +1, making the L2
            // value at each step deterministically seed + hop-count. Synthetic deterministic proof values
            // (D-03) — no real/sensitive payload is logged.
            var numbers = new[] { 100, 200 };

            logger.LogInformation("{StepLabel} seeded the following numbers: {Numbers}",
                label, string.Join(", ", numbers));

            foreach (var number in numbers)
            {
                var data = JsonSerializer.Serialize(new { number, label }, ProcessorConfig.SerializerOptions);
                await this.SpawnToPost(this.NewResult(StepOutcome.Completed, data), Guid.NewGuid());   // mint per spawn (D-09)
            }
            await this.DeleteEntry();
            return null;   // req 3: spawn handled everything — skip the framework inline tail
        }

        // DOWNSTREAM (Mode-1): REUSE the inbound executionId, accumulate deterministically (NO random).
        using var parsed = JsonDocument.Parse(validatedData);
        var incomingNumber = parsed.RootElement.GetProperty("number").GetInt32();
        var accumulated    = incomingNumber + baseNumber;

        logger.LogInformation("{StepLabel} had the following numbers: {Numbers}",
            label, accumulated.ToString());

        var downstreamData = JsonSerializer.Serialize(
            new { number = accumulated, label }, ProcessorConfig.SerializerOptions);
        return this.NewResult(StepOutcome.Completed, downstreamData) with { ExecutionId = executionId };   // reuse inbound exec
    }
}
