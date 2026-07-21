using System.Text.Json;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Contracts;

/// <summary>
/// Phase 84 (Plan 02) — closes assumption A1 for DATA-05: a <see cref="DataResult"/> whose <c>Data</c> is a
/// raw (non-UTF8-representable) <c>byte[]</c> round-trips losslessly through the default System.Text.Json
/// serializer that MassTransit uses on the RabbitMQ wire. STJ encodes <c>byte[]</c> as base64 transiently on
/// the wire and restores the EXACT bytes on deserialize — proving the recovery hand-off carries arbitrary
/// bytes with no loss and no string coercion. Pure record/serialization fact (hermetic — no broker/harness).
/// </summary>
[Trait("Phase", "84")]
public sealed class DataResultByteChannelFacts
{
    [Fact]
    public void DataResult_ByteArray_RoundTrips_Losslessly_Through_Stj_As_Base64()
    {
        // Bytes that are deliberately NOT valid UTF-8 / not a printable string — proves the channel carries
        // arbitrary binary, not a UTF-8 view. 0xFF/0x00 would corrupt under any string coercion.
        var original = new byte[] { 0x00, 0xFF, 0x10, 0x7F };

        var dr = new DataResult(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId   = Guid.NewGuid(),
            MessageId     = Guid.NewGuid(),
            Result        = StepOutcome.Completed,
            Data          = original,
        };

        // Serialize with the default STJ options (the MassTransit-JSON wire form) then deserialize back.
        var json  = JsonSerializer.Serialize(dr);
        var round = JsonSerializer.Deserialize<DataResult>(json);

        Assert.NotNull(round);
        // The bytes survive the wire EXACTLY (SequenceEqual — byte[] is reference-typed inside the record).
        Assert.True(round!.Data.SequenceEqual(original));
        // And the transient wire form is base64 of those bytes (proves STJ's byte[] → base64 wire encoding).
        Assert.Contains(Convert.ToBase64String(original), json);
    }
}
