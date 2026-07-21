namespace Messaging.Contracts;

/// <summary>
/// D-02 (Phase 84) EDGE conversions for <see cref="DataResult"/>. The stored/wire type of
/// <see cref="DataResult.Data"/> is <c>byte[]</c> — the single ground truth. These helpers exist ONLY so
/// authors and existing hermetic tests can view/author the payload as a UTF-8 string without hand-rolling
/// <c>Encoding.UTF8</c> at every edge. The string is a convenience VIEW, NOT the canonical type: do not
/// treat a returned string as authoritative or re-store it as-is expecting round-trip identity beyond the
/// UTF-8 decode.
/// </summary>
public static class DataResultExtensions
{
    /// <summary>D-02 decode-on-read EDGE conversion: interpret the ground-truth <c>byte[]</c>
    /// <see cref="DataResult.Data"/> as a UTF-8 string. Convenience view only — the stored type stays
    /// <c>byte[]</c>. Prefer operating on the bytes directly on the hot path; use this only at an edge
    /// (author ergonomics, test assertions).</summary>
    public static string DataAsString(this DataResult dr) =>
        System.Text.Encoding.UTF8.GetString(dr.Data);
}
