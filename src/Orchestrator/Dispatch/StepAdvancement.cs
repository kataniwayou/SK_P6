using Messaging.Contracts;
using Messaging.Contracts.Projections;

namespace Orchestrator.Dispatch;

/// <summary>
/// The pure outcome->entry-condition match + <c>NextStepIds</c> traversal (SPEC req 3 / D-02).
/// Given a completed step's reported <see cref="StepOutcome"/> and the in-memory L1 step map, selects
/// exactly the next steps whose <c>EntryCondition</c> equals <c>(int)outcome</c> (0-3) OR equals
/// <see cref="Always"/> (4). <c>Never</c> (5) falls out of the predicate so a <c>Never</c>-gated step
/// can never be auto-advanced (T-24-07).
/// <para>
/// No I/O: the step map is passed in as an argument (no Redis, no store reference), so the match is a
/// pure function unit-testable without a harness. A <c>NextStepIds</c> id absent from the map is
/// SKIPPED via the <c>TryGetValue</c> guard — a dangling edge is a graceful business skip, never a
/// throw (T-24-06). <c>Always</c>/<c>Never</c> live here as orchestrator-side int constants; this type
/// does NOT reference <c>BaseApi.Service.StepEntryCondition</c> (Orchestrator references only
/// <c>Messaging.Contracts</c>).
/// </para>
/// </summary>
public sealed class StepAdvancement
{
    private const int Always = 4; // Never = 5 is never selected (it falls out of the predicate)

    /// <summary>
    /// Classifies <paramref name="completed"/>'s <c>NextStepIds</c> in a single pure pass into a
    /// <see cref="SelectNextResult"/> three ways (72 / D-01 / D-02):
    /// <list type="bullet">
    ///   <item>a next-step id ABSENT from <paramref name="steps"/> (a dangling edge) lands in
    ///   <see cref="SelectNextResult.UnresolvedIds"/> — the new stage-3 observability signal (Plan 03
    ///   increments <c>orchestrator_step_unresolved</c> off it); previously the <c>TryGetValue</c> guard
    ///   dropped it SILENTLY.</item>
    ///   <item>a resolved id whose <c>EntryCondition</c> matches <paramref name="outcome"/> or is
    ///   <see cref="Always"/> (4) lands in <see cref="SelectNextResult.Matches"/>.</item>
    ///   <item>a resolved id whose <c>EntryCondition</c> is neither (a condition mismatch incl.
    ///   <c>Never</c> = 5) is collected NOWHERE — neither Matches nor UnresolvedIds (D-03: a deliberate
    ///   non-match is not an unresolved miss, so it is NOT counted).</item>
    /// </list>
    /// <para>
    /// PURE — no I/O: the step map is passed in as an argument (no Redis, no store, no metrics holder), so
    /// the classification is unit-testable without a harness. WR-02 (24.1 / D-24.1-06): a <c>null</c> (or
    /// empty) <c>NextStepIds</c> is the contract-defined TERMINAL step — it yields empty Matches AND empty
    /// UnresolvedIds (terminal ≠ unresolved) WITHOUT an NRE; the <c>?? Enumerable.Empty&lt;Guid&gt;()</c>
    /// terminal guard closes the normal end-of-branch case that previously NRE'd and dead-lettered.
    /// </para>
    /// </summary>
    public SelectNextResult SelectNext(
        StepOutcome outcome, StepProjection completed, IReadOnlyDictionary<Guid, StepProjection> steps)
    {
        var matches = new List<(Guid stepId, StepProjection step)>();
        var unresolvedIds = new List<Guid>();

        foreach (var nextId in completed.NextStepIds ?? Enumerable.Empty<Guid>())
        {
            if (!steps.TryGetValue(nextId, out var next))
                unresolvedIds.Add(nextId);                                 // case a — dangling edge (new signal)
            else if (next.EntryCondition == (int)outcome || next.EntryCondition == Always)
                matches.Add((nextId, next));                               // case c — matched successor
            // else: case b — condition mismatch / Never(5) → collected NOWHERE (D-03, no metric)
        }

        return new SelectNextResult(matches, unresolvedIds);
    }
}

/// <summary>
/// The pure result of <see cref="StepAdvancement.SelectNext"/> (72 / D-02): the matched successors to
/// fan out, plus the dangling next-step ids that resolved to nothing in the L1 step map. Allocation-light,
/// harness-free — carries NO metrics/Redis reference (the <c>UnresolvedIds</c> increment lands in Plan 03's
/// pipeline). An empty <see cref="UnresolvedIds"/> means every next-step id resolved (or there were none).
/// </summary>
public readonly record struct SelectNextResult(
    IReadOnlyList<(Guid stepId, StepProjection step)> Matches,
    IReadOnlyList<Guid> UnresolvedIds);
