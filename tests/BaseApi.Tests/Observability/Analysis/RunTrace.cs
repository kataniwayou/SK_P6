namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// Per-execution-instance aggregate built from the per-step COMPLETED-effect ES logs
/// (SampleProcessor — <c>"step completed {StepLabel} sum {Sum}"</c> → ES
/// <c>attributes.StepLabel</c> + <c>attributes.CorrelationId</c> + <c>attributes.ExecutionId</c>). One
/// <see cref="RunTrace"/> represents one execution instance (one <c>(correlationId, executionId)</c> pair —
/// each spawned execution is its own run) and the set of <c>StepLabel</c>s that emitted a COMPLETED log
/// for that instance.
///
/// <para>
/// This is a PURE model — no ES/Prom/host dependency. Both the live RealStack fixture (Plan 03,
/// from parsed ES hits) and the hermetic facts (Plan 01 Task 3, from synthetic label lists) build
/// traces identically through <see cref="FromLabels"/>, so the duplicate/distinct arithmetic is
/// proven once and shared.
/// </para>
///
/// <para>
/// <b>Duplicates are retained in <see cref="Labels"/>.</b> A redelivered/double-effect Step_C
/// appears TWICE in <see cref="Labels"/> but ONCE in <see cref="DistinctLabels"/>; the gap is the
/// fail-closed duplicate signal (<see cref="HasAnyDuplicateLabel"/>). Label comparison is
/// <see cref="StringComparer.Ordinal"/> — the labels are fixed verbatim identifiers (Step_A …
/// Step_F2 … Step_G), never culture-folded.
/// </para>
///
/// <para>
/// <b>Convergent-terminal multiplicity (73, D-10).</b> The DAG gained a single shared convergent
/// terminal <c>Step_G</c> reachable from BOTH sinks (Step_F1, Step_F2), so one
/// <c>(correlationId, executionId)</c> legitimately logs <c>Step_G</c> TWICE (the per-arrival,
/// non-joining fan-in). The blanket "any repeat ⇒ duplicate" rule (<see cref="HasAnyDuplicateLabel"/>)
/// would wrongly fail that legitimate fan-in, so a convergent-aware signal,
/// <see cref="HasIllegitimateDuplicate"/>, is the BINDING one: a label is an illegitimate duplicate iff
/// it is NOT the convergent <c>Step_G</c> and appears &gt; 1×, OR it IS <c>Step_G</c> and appears ≠ 2×
/// (1 = a missing arrival, 3+ = a same-<c>entryId</c> redelivery — both still fail closed). The old
/// <see cref="HasAnyDuplicateLabel"/> member is RETAINED ("any raw repeat") for report-shape stability.
/// </para>
///
/// <para>
/// <b>Per-step value surfacing (73, D-11).</b> <see cref="Values"/> carries each label's surfaced
/// integer value (the live fixture fills it from the ES <c>attributes.Produced</c> field per 73-01;
/// the hermetic facts fill it from synthetic values). For the convergent <c>Step_G</c> (two arrivals,
/// identical value) the map holds the single shared terminal value (<c>seed + 6</c>). The engine asserts
/// the deterministic value chain (<c>value == seed + hop-count</c>) against this map.
/// </para>
/// </summary>
public sealed record RunTrace
{
    /// <summary>
    /// The convergent terminal label whose expected multiplicity is &gt; 1. <c>Step_G</c> is the single
    /// shared, per-arrival non-joining fan-in terminal (reachable from both Step_F1 and Step_F2), so it
    /// legitimately appears <see cref="ConvergentExpectedMultiplicity"/> times per
    /// <c>(correlationId, executionId)</c>. Mirrors the <c>PassFailEngine.AllLabels</c> static-spec style.
    /// </summary>
    public const string ConvergentLabel = "Step_G";

    /// <summary>
    /// The expected number of legitimate arrivals for <see cref="ConvergentLabel"/> per run — exactly 2
    /// (one per symmetric branch F1, F2). A count of 1 (missing arrival) or 3+ (same-<c>entryId</c>
    /// redelivery) is an ILLEGITIMATE duplicate and still fails closed.
    /// </summary>
    public const int ConvergentExpectedMultiplicity = 2;

    /// <summary>The per-fire correlationId this trace aggregates.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>The per-instance executionId this trace aggregates (each spawned execution is its own run).</summary>
    public required string ExecutionId { get; init; }

    /// <summary>
    /// Every StepLabel that emitted a COMPLETED log for this correlationId, duplicates RETAINED
    /// (a double-effect Step_C appears twice). The raw evidence the distinct/duplicate facts derive.
    /// </summary>
    public required IReadOnlyList<string> Labels { get; init; }

    /// <summary>The distinct StepLabel set for this correlationId (Ordinal). Drives COMPLETE (OBS-01).</summary>
    public required HashSet<string> DistinctLabels { get; init; }

    /// <summary>
    /// True iff any StepLabel appears more than once for this correlationId
    /// (<c>Labels.Count != DistinctLabels.Count</c>). The fail-closed duplicate signal (OBS-02):
    /// there is no live dedupe counter to corroborate a redelivery, so any duplicate FAILS.
    /// </summary>
    public required bool HasAnyDuplicateLabel { get; init; }

    /// <summary>
    /// True iff any label is an ILLEGITIMATE duplicate (73, D-10, BINDING signal): a NON-convergent
    /// label appearing &gt; 1×, OR the convergent <see cref="ConvergentLabel"/> appearing ≠
    /// <see cref="ConvergentExpectedMultiplicity"/> times. The legitimate convergent fan-in
    /// (<c>Step_G</c> ×2) does NOT set this; the engine reads THIS (not <see cref="HasAnyDuplicateLabel"/>)
    /// for the fail-closed duplicate verdict.
    /// </summary>
    public required bool HasIllegitimateDuplicate { get; init; }

    /// <summary>The specific labels that collided ILLEGITIMATELY (a Step_G ×2 is NOT listed; a Step_C ×2 or a Step_G ×3 IS), for report surfacing.</summary>
    public required IReadOnlyList<string> DuplicateLabels { get; init; }

    /// <summary>
    /// Per-label surfaced integer value (73, D-11): label → value. The live fixture fills it from the ES
    /// <c>attributes.Produced</c> field (73-01 contract); the hermetic facts fill it from synthetic values.
    /// For the convergent <see cref="ConvergentLabel"/> (two arrivals, identical value) the map holds the
    /// single shared terminal value. The engine's value-chain assertion checks each entry equals
    /// <c>seed + hop-count</c>. Empty when no values were supplied (legacy callers).
    /// </summary>
    public required IReadOnlyDictionary<string, int> Values { get; init; }

    /// <summary>
    /// Build a <see cref="RunTrace"/> from a (correlationId, executionId) instance + its raw
    /// (duplicate-retaining) label list, with optional per-label <paramref name="values"/>. Computes the
    /// distinct set + duplicate flags (incl. the convergent-aware <see cref="HasIllegitimateDuplicate"/>)
    /// so both the live fixture and the hermetic facts construct traces identically. A duplicate is WITHIN
    /// one instance — a label appearing &gt;1× for the same (correlationId, executionId); the convergent
    /// <see cref="ConvergentLabel"/> is exempt at exactly <see cref="ConvergentExpectedMultiplicity"/>.
    /// </summary>
    public static RunTrace FromLabels(string correlationId, string executionId, IReadOnlyList<string> labels,
                                      IReadOnlyDictionary<string, int>? values = null)
    {
        var distinct = new HashSet<string>(labels, StringComparer.Ordinal);

        // Count occurrences per label (Ordinal) so the convergent-multiplicity rule can be applied exactly.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var label in labels)
        {
            counts[label] = counts.TryGetValue(label, out var c) ? c + 1 : 1;
        }

        // A label is an ILLEGITIMATE duplicate iff:
        //   (it is NOT the convergent label AND count > 1) OR (it IS the convergent label AND count != 2).
        // The legitimate convergent fan-in (Step_G ×2) is exempt; a Step_G ×1 / ×3+ and any other repeat fail.
        var illegitimate = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (label, count) in counts)
        {
            var isConvergent = string.Equals(label, ConvergentLabel, StringComparison.Ordinal);
            var bad = isConvergent ? count != ConvergentExpectedMultiplicity : count > 1;
            if (bad)
            {
                illegitimate.Add(label);
            }
        }

        return new RunTrace
        {
            CorrelationId = correlationId,
            ExecutionId = executionId,
            Labels = labels,
            DistinctLabels = distinct,
            // Legacy "any raw repeat" — retained for report-shape stability; NOT the binding signal anymore.
            HasAnyDuplicateLabel = labels.Count != distinct.Count,
            // BINDING convergent-aware signal (73, D-10).
            HasIllegitimateDuplicate = illegitimate.Count > 0,
            // Sort deterministically (Ordinal) so report diffs and any future snapshot comparisons
            // are stable across runs — HashSet enumeration order is unspecified. (IN-01 fix.)
            DuplicateLabels = illegitimate.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            Values = values ?? new Dictionary<string, int>(StringComparer.Ordinal),
        };
    }
}
