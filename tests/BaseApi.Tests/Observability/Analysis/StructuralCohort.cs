namespace BaseApi.Tests.Observability.Analysis;

/// <summary>
/// One parsed framework log record — the pure, JsonElement-decoupled unit both the live fixture
/// (<c>AnalyzerE2ETests.BuildRunTraces</c>) and the hermetic <c>StructuralCohortFacts</c> feed into
/// <see cref="StructuralCohort.Classify"/>. Tier-1 ids (<see cref="CorrelationId"/>/<see cref="ExecutionId"/>/
/// <see cref="StepId"/>) arrive on EVERY framework log as <c>attributes.*</c> from the ambient
/// InboundExecutionScopeConsumeFilter scope (Phase 77 D1, IncludeScopes). Tier-2 <see cref="MessageId"/> is
/// present only on the send/consume records (<c>attributes.MessageId</c>). <see cref="BodyText"/> is the
/// rendered <c>_source.body.text</c> (e.g. <c>"hop executed &lt;guid&gt; Completed"</c>) — the ONLY axis that
/// discriminates the record KIND (per 77-CONTEXT D1/D3).
/// </summary>
internal readonly record struct FrameworkLogRecord(
    string CorrelationId,
    string ExecutionId,
    string StepId,
    string? MessageId,      // Tier-2, present only on send/consume records
    string BodyText,        // rendered _source.body.text, e.g. "hop executed <guid> Completed"
    DateTimeOffset? Timestamp);

/// <summary>
/// The pure structural-cohort classifier (Phase 78 D-04 gap closure). Phase 77's uniform execution-scope
/// logging + the D3 outbound-<c>{MessageId}</c> capture make the <c>"result sent"</c> and <c>"fan-out"</c>
/// records ALSO carry <c>{StepId, ExecutionId, MessageId}</c>, so they all match the structural ES query
/// (<c>exists StepId</c> + <c>exists ExecutionId</c> + <c>must_not ReinjectOutcome</c>). The ONLY canonical
/// 1-per-genuine-execution record is the processor <c>"hop executed"</c> consume log
/// (<c>ProcessorPipeline.cs:218</c>). Counting ANY other kind over-counts every hop 3–4× and false-fails
/// effect-once (the 78-04 live signature <c>Duplicates == StartedRuns</c>). This classifier isolates the
/// canonical consume record as the SOLE observed did-run hop, routes <c>"terminal reached"</c> to the ANL-03
/// proven set, and IGNORES every other body — WITHOUT collapsing genuine redelivery duplicates, which must
/// still survive to trip <c>HasIllegitimateDuplicate</c>.
/// </summary>
internal static class StructuralCohort
{
    /// <summary>
    /// The processor CONSUME "did-run" record prefix (<c>ProcessorPipeline.cs:218</c>
    /// <c>"hop executed {MessageId} {Outcome}"</c>) — the ONE record that fires EXACTLY ONCE per genuine hop
    /// execution. This is the sole structural evidence anchor; a genuine redelivery emits it TWICE and MUST
    /// trip the duplicate signal (per D-04 gap — the collapse is by record-KIND selection, never by removing
    /// repeats).
    /// </summary>
    internal const string ConsumeRecordPrefix = "hop executed";

    /// <summary>
    /// The orchestrator terminal-reached record prefix (<c>OrchestratorPrePipeline.cs:107</c>
    /// <c>"terminal reached"</c>) — carries no <c>{MessageId}</c>; feeds the ANL-03 orchestrator-redundancy
    /// proven set (NOT the observed did-run set).
    /// </summary>
    internal const string TerminalRecordPrefix = "terminal reached";

    /// <summary>
    /// Classify the parsed framework records into the observed/proven/resolver/timestamp maps that the live
    /// <c>BuildRunTraces</c> previously built inline. The discrimination is purely by <see cref="FrameworkLogRecord.BodyText"/>
    /// Ordinal prefix (per 77-CONTEXT D1/D3):
    /// <list type="bullet">
    /// <item><c>"hop executed"</c> → OBSERVED did-run hop: append <c>StepId</c> to the per-instance list
    /// (duplicates RETAINED — a genuine redelivery MUST survive to trip <c>HasIllegitimateDuplicate</c>),
    /// record the min/max @timestamp span, and (if <c>MessageId</c> non-null) set the ANL-03
    /// <c>stepIdByMessageId</c> resolver. The dispatch record's inbound <c>attributes.EntryId</c> equals the
    /// producer hop's consumed inbound <c>MessageId</c> (<c>OutputTail.BuildStep</c> stamps
    /// <c>StepCompleted.EntryId = dr.MessageId</c>, the same value the producer's <c>"hop executed"</c> logs),
    /// so keying the resolver off the canonical consume record resolves <c>dispatch.EntryId</c> → producer
    /// stepId correctly.</item>
    /// <item><c>"terminal reached"</c> → PROVEN (ANL-03 orchestrator redundancy) — narrowed to genuine
    /// terminal-reached records only.</item>
    /// <item>ANY OTHER body (<c>"result sent"</c>, <c>"fan-out"</c>, <c>"Trip ended …"</c>, business logs) →
    /// IGNORED. This is the fix: the send-side records no longer inflate the observed did-run list. The
    /// fan-out/dispatch evidence (expected set + ANL-03 edge) is supplied by the SEPARATE dispatch query,
    /// unchanged.</item>
    /// </list>
    /// The collapse comes from selecting the ONE canonical record KIND, NOT from removing repeats — genuine
    /// redelivery duplicates are RETAINED (per D-04 gap).
    /// </summary>
    internal static StructuralCohortResult Classify(IReadOnlyList<FrameworkLogRecord> records)
    {
        // OBSERVED processor stepIds per (corr, exec) — duplicates RETAINED (a genuine redelivery repeats,
        // and the convergent terminal fans in ×2). NEVER collapsed: the whole point of the resilience proof
        // is detecting a step that GENUINELY executed twice (per D-04 gap).
        var stepIdsByInstance = new Dictionary<(string Corr, string Exec), List<string>>();
        var spanByInstance = new Dictionary<(string Corr, string Exec), (DateTimeOffset Min, DateTimeOffset Max)>();
        var spanByCorrelation = new Dictionary<string, (DateTimeOffset Min, DateTimeOffset Max)>();
        // MessageId (M_N) → producer StepId, from the canonical "hop executed" records — the ANL-03
        // EntryId→stepId resolver (the dispatch record's inbound EntryId == the producer's consumed MessageId).
        var stepIdByMessageId = new Dictionary<string, string>(StringComparer.Ordinal);
        // Orchestrator-proven "did run" stepIds per (corr,exec): terminal-reached records name the StepId.
        var proven = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var r in records)
        {
            if (r.BodyText.StartsWith(ConsumeRecordPrefix, StringComparison.Ordinal))
            {
                // Canonical CONSUME "did-run" record — the 1-per-genuine-execution anchor. Append the stepId
                // WITHOUT removing repeats so a genuine redelivery (two "hop executed" for one {exec,step})
                // survives to trip HasIllegitimateDuplicate.
                var key = (r.CorrelationId, r.ExecutionId);
                (stepIdsByInstance.TryGetValue(key, out var steps) ? steps : stepIdsByInstance[key] = new()).Add(r.StepId);

                if (r.MessageId is not null)
                {
                    stepIdByMessageId[r.MessageId] = r.StepId;
                }

                if (r.Timestamp is { } ts)
                {
                    spanByInstance[key] = spanByInstance.TryGetValue(key, out var s)
                        ? (s.Min < ts ? s.Min : ts, s.Max > ts ? s.Max : ts) : (ts, ts);
                    spanByCorrelation[r.CorrelationId] = spanByCorrelation.TryGetValue(r.CorrelationId, out var c)
                        ? (c.Min < ts ? c.Min : ts, c.Max > ts ? c.Max : ts) : (ts, ts);
                }
            }
            else if (r.BodyText.StartsWith(TerminalRecordPrefix, StringComparison.Ordinal))
            {
                // Terminal-reached (no MessageId) → ANL-03 orchestrator redundancy, NOT an observed hop.
                var pkey = $"{r.CorrelationId}|{r.ExecutionId}";
                (proven.TryGetValue(pkey, out var pset) ? pset : proven[pkey] = new(StringComparer.Ordinal)).Add(r.StepId);
            }

            // ANY OTHER body ("result sent", "fan-out", "Trip ended …", business logs) → IGNORED for the
            // structural cohort. THIS is the D-04 fix: the send-side records no longer inflate the observed
            // did-run list. (fan-out/dispatch evidence comes from the unchanged dispatch query.)
        }

        return new StructuralCohortResult
        {
            StepIdsByInstance = stepIdsByInstance,
            SpanByInstance = spanByInstance,
            SpanByCorrelation = spanByCorrelation,
            StepIdByMessageId = stepIdByMessageId,
            Proven = proven,
        };
    }
}

/// <summary>
/// The mutable maps <see cref="StructuralCohort.Classify"/> produces from the observed did-run + terminal
/// records — fed into the live <c>BuildRunTraces</c> dispatch loop (which reads
/// <see cref="StepIdByMessageId"/> and augments <see cref="Proven"/>), the convergent derivation, and the
/// <c>RunTrace.FromStepIds</c> construction. Concrete <see cref="Dictionary{TKey,TValue}"/> types (not
/// read-only interfaces) so the dispatch loop can keep mutating <see cref="Proven"/>.
/// </summary>
internal sealed record StructuralCohortResult
{
    /// <summary>OBSERVED processor stepIds per (corr, exec) — duplicates RETAINED (canonical "hop executed" only).</summary>
    public required Dictionary<(string Corr, string Exec), List<string>> StepIdsByInstance { get; init; }

    /// <summary>Per-(corr, exec) min/max @timestamp span over the canonical consume records.</summary>
    public required Dictionary<(string Corr, string Exec), (DateTimeOffset Min, DateTimeOffset Max)> SpanByInstance { get; init; }

    /// <summary>Per-correlation min/max @timestamp span over the canonical consume records.</summary>
    public required Dictionary<string, (DateTimeOffset Min, DateTimeOffset Max)> SpanByCorrelation { get; init; }

    /// <summary>ANL-03 resolver: canonical consume MessageId (M_N) → producer StepId.</summary>
    public required Dictionary<string, string> StepIdByMessageId { get; init; }

    /// <summary>ANL-03 proven set per "corr|exec": stepIds named by terminal-reached records (augmented by the dispatch loop).</summary>
    public required Dictionary<string, HashSet<string>> Proven { get; init; }
}
