using System.Text.Json;                  // JsonSerializer — NOT in .NET 8 implicit usings
using BaseProcessor.Core.Configuration;  // ProcessorConfig.SerializerOptions
using BaseProcessor.Core.Processing;
using Messaging.Contracts;               // DataResult / StepOutcome / ExecutionLogScope

namespace Processor.Sample;

/// <summary>
/// The one concrete <see cref="BaseProcessor{SampleConfig}"/> (PROC-02/PROC-03). It overrides ONLY the
/// typed In-Process <c>ProcessAsync</c> seam; the framework owns deserialization (it hands in a typed
/// <see cref="SampleConfig"/>) and the entire Pre/Post/end-delete pipeline.
/// <para>
/// Phase 70 (req 10): the two-mode worked example, keyed on the inbound <c>executionId</c>:
/// <list type="bullet">
///   <item><b>ENTRY/seed</b> (<c>executionId == Guid.Empty</c>, Mode-2): seeds the TWO FIXED values
///   <c>100</c> and <c>200</c> (deterministic — NO random), then SPAWNS two completed
///   <see cref="DataResult"/>s to the Post-Process queue via <c>SpawnToPost</c> with DISTINCT
///   freshly-minted executionIds (D-09; FAIL-LOUD on transient exhaust), and RETURNS NULL. PB-03: the
///   framework owns the entry delete on the null-return path — the author issues no delete (req 3).</item>
///   <item><b>DOWNSTREAM</b> (<c>executionId != Guid.Empty</c>, Mode-1): accumulates ONE number
///   (<c>incomingNumber + baseNumber</c>, deterministic — NO random), and RETURNS ONE completed
///   <see cref="DataResult"/> REUSING the inbound executionId — the framework's inline tail runs
///   (no spawn, no delete).</item>
/// </list>
/// Per D4/LOG-04 the concrete processor emits NO author logs: the framework's per-hop record and
/// result-send record carry the full execution evidence, and the verdict never depends on a
/// concrete-processor log. The author writes NO RetryLoop/keeper/envelope code — <c>SpawnToPost</c> owns
/// spawn resilience (fail-loud on transient exhaust), the framework owns the entry delete (PB-03), and
/// <c>NewResult</c> stamps the ambient ids + carried messageId.
/// </para>
/// </summary>
/// <remarks>
/// DI-registered <c>AddSingleton&lt;BaseProcessor, SampleProcessor&gt;</c> (Program.cs:17, unchanged) —
/// SampleProcessor IS-A BaseProcessor via BaseProcessor&lt;SampleConfig&gt;.
/// </remarks>
public sealed class SampleProcessor : BaseProcessor<SampleConfig>
{
    /// <inheritdoc/>
    protected override async Task<DataResult?> ProcessAsync(
        byte[] validatedData, SampleConfig? config, Guid executionId, CancellationToken ct)
    {
        // TEST-ONLY fault hook (env-gated, DEFAULT OFF). When PROCESSOR_STEP_DELAY_MS > 0, hold each hop
        // mid-consume BEFORE any log/spawn/result so this fast pipeline exposes a catchable in-flight window:
        // the message stays unacknowledged for the delay (RabbitMQ queue depth > 0), and a kill during it
        // freezes the execution mid-hop → started-but-incomplete → recoverable-but-lost FAIL. Unset/0 ⇒ NO
        // delay, behaviour byte-for-byte unchanged. NEVER set in production (only the resilience harness
        // exports it for the TEST-09 negative path).
        if (int.TryParse(Environment.GetEnvironmentVariable("PROCESSOR_STEP_DELAY_MS"), out var stepDelayMs)
            && stepDelayMs > 0)
        {
            await Task.Delay(stepDelayMs, ct);
        }

        var baseNumber = config?.Number ?? 0;            // D-03 null-config default — warning-clean guard (Pitfall 2)
        var label      = config?.Label;                  // D-10 verbatim — already "Step_*", do NOT prepend

        if (executionId == Guid.Empty)
        {
            // ENTRY/seed (Mode-2): seed 2 fixed numbers, spawn 2 to Post (distinct minted execIds,
            // FAIL-LOUD on transient send-exhaust), return null. PB-03: the framework owns the entry delete
            // (pipeline null-return tail) — the author issues NO delete. No author log (D4/LOG-04).
            // D-01/D-02: seed the two FIXED execution values (100, 200). Every step's payload number = 1
            // (seeder data change, Plan 04) so each downstream hop increments by exactly +1, making the L2
            // value at each step deterministically seed + hop-count. Synthetic deterministic proof values
            // (D-03) — no real/sensitive payload is logged.
            var numbers = new[] { 100, 200 };

            foreach (var number in numbers)
            {
                var execId = Guid.NewGuid();   // mint per spawn (D-09)
                var data   = JsonSerializer.Serialize(new { number, label }, ProcessorConfig.SerializerOptions);
                try
                {
                    await this.SpawnToPost(this.NewResult(StepOutcome.Completed, data), execId);
                    // SUCCESS: this spawn is durably enqueued on the -post queue. A Kafka author would
                    // commit THIS message's offset here (per-message exactly-ish), keeping earlier successes.
                }
                catch (SpawnSendExhaustedException ex)
                {
                    // EXPERIMENT (Option B, illustrative — NOT the shipped contract): take per-execution
                    // ownership of the exhaust outcome instead of letting it propagate. ex.ExecutionId names
                    // the one spawn whose hand-off failed; we DROP it and CONTINUE, so the seed acks rather
                    // than nack-requeuing the whole batch. This intentionally REVERTS the PB-01/02 framework
                    // no-loss nack for the Mode-2 fan-out — the dropped execution is LOST unless the source
                    // (here: the scheduler seed, which does NOT re-drive it) commits/redelivers per message.
                    _ = ex.ExecutionId;   // read-only: identifies the failed spawn (no author log — D4/LOG-04)
                }
            }
            return null;   // req 3: spawns handled the fan-out — framework owns the entry delete (PB-03 null-path tail)
        }

        // DOWNSTREAM (Mode-1): REUSE the inbound executionId, accumulate deterministically (NO random).
        using var parsed = JsonDocument.Parse(validatedData);
        var incomingNumber = parsed.RootElement.GetProperty("number").GetInt32();
        var accumulated    = incomingNumber + baseNumber;

        var downstreamData = JsonSerializer.Serialize(
            new { number = accumulated, label }, ProcessorConfig.SerializerOptions);
        return this.NewResult(StepOutcome.Completed, downstreamData) with { ExecutionId = executionId };   // reuse inbound exec
    }
}
