using System.Text;
using BaseProcessor.Core.Validation;
using Xunit;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 84 (Plan 02) — closes assumption A2 for D-04/DATA-02: with a schema CONFIGURED,
/// <see cref="ProcessorJsonSchemaValidator.TryValidate(string?, byte[], out System.Collections.Generic.IReadOnlyList{string})"/>
/// decodes the bytes as UTF-8 JSON via <c>JsonDocument.Parse(byte[])</c>; invalid-UTF8 bytes raise a
/// <c>JsonException</c> that the existing catch turns into a business <c>false</c> (→ Failed, never a crash) —
/// the same malformed-JSON path, no new error surface. The schema-ABSENT control proves opaque bytes are
/// NEVER decoded (DATA-02 / Pitfall 3). Hermetic — no broker/harness.
/// </summary>
[Trait("Phase", "84")]
public sealed class SchemaByteDecodeFacts
{
    private const string ObjectSchema = "{\"type\":\"object\"}";

    [Fact]
    public void InvalidUtf8_Under_Schema_Returns_False_With_Json_Utf8_Error()
    {
        // 0xFF 0xFE 0xFD is not a valid UTF-8 sequence — under a schema it must decode-and-fail (D-04).
        var invalidUtf8 = new byte[] { 0xFF, 0xFE, 0xFD };

        var ok = ProcessorJsonSchemaValidator.TryValidate(ObjectSchema, invalidUtf8, out var errors);

        Assert.False(ok);                 // malformed UTF-8 under a schema → business Failed, not a host crash
        Assert.NotEmpty(errors);
        // The surfaced message names the JSON/UTF-8 failure (the D-04 malformed-data path).
        Assert.Contains(errors, e => e.Contains("JSON", StringComparison.OrdinalIgnoreCase)
                                  || e.Contains("UTF-8", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidJson_Satisfying_Schema_Returns_True()
    {
        // Control: valid UTF-8 JSON that conforms to the schema decodes + validates → true.
        var validJson = Encoding.UTF8.GetBytes("{\"a\":1}");

        var ok = ProcessorJsonSchemaValidator.TryValidate(ObjectSchema, validJson, out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
    }

    [Fact]
    public void No_Schema_Never_Decodes_Opaque_Bytes()
    {
        // DATA-02 / Pitfall 3: with NO schema the same invalid-UTF8 bytes are NEVER decoded — they stay
        // opaque and TryValidate short-circuits to true BEFORE any JsonDocument.Parse. If the guard were
        // removed, decoding these bytes would throw/return false — so a `true` proves opacity.
        var invalidUtf8 = new byte[] { 0xFF, 0xFE, 0xFD };

        Assert.True(ProcessorJsonSchemaValidator.TryValidate(null, invalidUtf8, out var nullErrors));
        Assert.Empty(nullErrors);

        Assert.True(ProcessorJsonSchemaValidator.TryValidate("   ", invalidUtf8, out var wsErrors));
        Assert.Empty(wsErrors);
    }
}
