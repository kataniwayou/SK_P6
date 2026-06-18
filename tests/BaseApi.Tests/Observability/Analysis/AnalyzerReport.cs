using System.Text.Json.Serialization;

namespace BaseApi.Tests.Observability.Analysis;

/// <summary>The single per-scenario correctness verdict.</summary>
public enum Verdict
{
    /// <summary>
    /// ES-binding green: every STARTED run (distinct correlationId with ≥1 Step_* log) is COMPLETE
    /// (all 9 labels incl. both sinks) AND no duplicate (correlationId, StepLabel). Prometheus is
    /// corroborating only — a Prom corroboration warning does NOT flip a green ES verdict (67-03).
    /// </summary>
    Pass,

    /// <summary>
    /// ES-binding red: at least one started-but-incomplete run (1–8 labels) OR any duplicate
    /// (fail-closed). Prometheus corroboration is non-binding and never the sole cause of a Fail.
    /// </summary>
    Fail,
}

/// <summary>
/// The outcome of the OBS-03 Prometheus CORROBORATION step (67-03: demoted from binding arbiter to
/// a non-fatal cross-check). Prometheus no longer gates the verdict; it corroborates the ES-binding
/// completeness count over the same window.
/// </summary>
public enum ReconciliationOutcome
{
    /// <summary>
    /// The Prom counter deltas corroborate the ES-binding count within the boundary tolerance
    /// (±1 run) — no warning. (Reconciled is retained as the enum name for report-shape stability.)
    /// </summary>
    Reconciled,

    /// <summary>
    /// The Prom counter deltas DISAGREE with the ES-binding count beyond tolerance — a corroboration
    /// WARNING (e.g. round(OrchestratorMessagesSentDelta / 9) implies more runs than ES distinct-correlationId
    /// count: a fully-dead run dispatched but never logging Step_A). NON-FATAL by default (67-03):
    /// surfaced in the report + HumanSummary, but does NOT flip a green ES verdict.
    /// </summary>
    Unreconciled,

    /// <summary>Reserved: no live counter series available to corroborate (not used for the live set today).</summary>
    Absent,
}

/// <summary>
/// JSON-serializable analyzer report (D-09 schema; 67-03 ES-binding re-foundation). Produced by
/// <see cref="PassFailEngine.Analyze"/> BEFORE any assert — a plain serializable record carrying the
/// full evidence set so the report is trustworthy on its own (the fixture in Plan 03 writes it to
/// disk; the engine does NO IO).
///
/// <para>
/// <b>Verdict drivers (ES-binding):</b> <see cref="StartedRuns"/> is the binding denominator —
/// distinct correlationIds with ≥1 Step_* log (runs that started). <see cref="CompleteRuns"/> /
/// <see cref="Missing"/> account against THAT denominator, and the loud <see cref="Duplicates"/>
/// stay fail-closed. <b>Prom corroboration (non-binding):</b> <see cref="TriggerCount"/> (the
/// dispatch-derived denominator) and the full <see cref="Prom"/> snapshot feed the corroboration
/// math only — <see cref="PromImpliedRuns"/> + <see cref="Reconciliation"/> +
/// <see cref="CorroborationDetail"/> surface a warning without gating the verdict.
/// </para>
/// </summary>
public sealed record AnalyzerReport
{
    /// <summary>The scenario under analysis (e.g. "TEST-01-happy-path" or "unit-test").</summary>
    public required string ScenarioId { get; init; }

    /// <summary>The single correctness verdict. Serializes as its string name ("Pass"/"Fail").</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required Verdict Verdict { get; init; }

    /// <summary>
    /// ES-BINDING DENOMINATOR (67-03): the number of distinct (correlationId, executionId) instances
    /// carrying ≥1 Step_* log — runs that STARTED (each spawned execution is its own run). Completeness/
    /// missing accounting is founded on this, NOT on the Prom dispatch count. The verdict driver.
    /// </summary>
    public required int StartedRuns { get; init; }

    /// <summary>
    /// CORROBORATION ONLY (67-03): the dispatch-derived count
    /// (round(orchestrator_dispatch_sent_total delta)). Retained as Prom evidence — the orchestrator
    /// emits one dispatch per STEP, so this is ~9× the run count and is NO LONGER the per-run
    /// denominator. Feeds <see cref="PromImpliedRuns"/>, never the binding verdict.
    /// </summary>
    public required int TriggerCount { get; init; }

    /// <summary>The number of started runs whose distinct StepLabel set equals the full 9-label set (OBS-01).</summary>
    public required int CompleteRuns { get; init; }

    /// <summary>
    /// StartedRuns − CompleteRuns (ES-binding): started-but-incomplete runs (1–8 labels). Missing &gt; 0
    /// forces Verdict.Fail (OBS-02). A fully-DEAD run (dispatched but never logging Step_A) is NOT in
    /// this count — it never started in ES; it surfaces via the Prom corroboration warning instead.
    /// </summary>
    public required int Missing { get; init; }

    /// <summary>
    /// Human notes on the missing COUNT plus the unrecoverable-identity caveat: the SPECIFIC missing
    /// correlationId for a fully-dead run is NOT recoverable from existing telemetry (no per-fire
    /// correlationId log — research item #1), so the analyzer reports the count, never names the run.
    /// </summary>
    public required IReadOnlyList<string> MissingDetail { get; init; }

    /// <summary>The traces that carried any duplicate (correlationId, StepLabel) — the fail-closed evidence (OBS-02).</summary>
    public required IReadOnlyList<RunTrace> Duplicates { get; init; }

    /// <summary>
    /// CORROBORATION ONLY (67-03): round(OrchestratorMessagesSentDelta / 9) — the run count IMPLIED by the Prom
    /// dispatch counter (9 dispatches per run). Compared against <see cref="StartedRuns"/> within the
    /// boundary tolerance; a positive excess (implied &gt; started, beyond tolerance) is the dead-run
    /// corroboration warning. Informational — never gates the verdict.
    /// </summary>
    public required int PromImpliedRuns { get; init; }

    /// <summary>
    /// CORROBORATION ONLY (spawn-aware OBS-03): the number of EXTRA results the entry fan-out emits beyond
    /// the dispatch count = entry-dispatch count = distinct correlationIds (derived from data, never
    /// hard-coded). The expected result count is <see cref="Prom"/>.OrchestratorMessagesSentDelta + this. Informational.
    /// </summary>
    public required int SpawnExtra { get; init; }

    /// <summary>
    /// CORROBORATION ONLY (spawn-aware OBS-03): the reconciled expectation for the result counter —
    /// <c>OrchestratorMessagesSentDelta + <see cref="SpawnExtra"/></c>. OrchestratorMessagesConsumedDelta is reconciled against THIS
    /// (within ±1-run result slack); a mismatch beyond slack is a non-fatal warning. Informational.
    /// </summary>
    public required double ExpectedResultConsumed { get; init; }

    /// <summary>
    /// The Prometheus CORROBORATION outcome (OBS-03; 67-03 non-binding). Reconciled = within ±1-run
    /// tolerance; Unreconciled = a non-fatal corroboration WARNING. Serializes as its string name.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required ReconciliationOutcome Reconciliation { get; init; }

    /// <summary>
    /// Human notes on the Prom corroboration: empty when corroboration is clean; otherwise the
    /// non-fatal warning text (e.g. dead-run implication, terminal non-completed outcomes). These are
    /// WARNINGS — they appear in the report regardless of the (ES-binding) verdict.
    /// </summary>
    public required IReadOnlyList<string> CorroborationDetail { get; init; }

    /// <summary>The full counter snapshot under analysis (live deltas + dormant/absent dedupe deltas) — corroboration evidence.</summary>
    public required PromCounterSnapshot Prom { get; init; }

    /// <summary>Every per-correlationId trace fed to the engine.</summary>
    public required IReadOnlyList<RunTrace> Traces { get; init; }

    /// <summary>
    /// BINDING (73, D-11): true iff EVERY checked run's deterministic value chain holds —
    /// <c>Values[label] == seed + hop-count</c> per <c>(corr, exec)</c>, with both <c>Step_G</c> arrivals
    /// at the terminal <c>seed + 6</c>. A wrong mid-chain or terminal value sets this false and folds into
    /// Verdict.Fail (<c>pass = … &amp;&amp; ValueChainOk</c>).
    /// <para>
    /// WR-01 — what the LIVE auditor pins. On the live ES-read-only path the seed is recovered from the chain
    /// itself (<c>Step_B - 1</c>), so this field binds the inter-hop <c>+1</c> DELTAS (and the <c>Step_G</c> ×2
    /// agreement at the shared terminal <c>seed + 6</c>), NOT the ABSOLUTE base value — a uniform constant shift
    /// of the whole live chain would still pass. The ABSOLUTE-value terminal-anchor proof (<c>Step_G</c> at 106
    /// for exec_a/seed 100, 206 for exec_b/seed 200) is owned by the hermetic harness (Plan 02), which reads the
    /// durable <c>skp:out:</c> L2 blob; the ES-read-only auditor cannot read Redis and does NOT re-prove it here.
    /// </para>
    /// <para>
    /// WR-02 — when a value oracle is supplied (live fixture), a COMPLETE run that surfaced ZERO <c>Produced</c>
    /// values sets this false (UNVERIFIED), so a complete-but-unmapped cohort can no longer pass vacuously.
    /// </para>
    /// </summary>
    public required bool ValueChainOk { get; init; }

    /// <summary>
    /// Per-exec expected-vs-actual value-chain evidence (73, D-11): one line per run describing the
    /// surfaced values vs the expected <c>seed + hop-count</c> chain (so a value-chain failure is
    /// trustworthy standalone in the report, like <see cref="MissingDetail"/>). Empty when no started run
    /// carries any surfaced value (e.g. legacy callers that pass no value map).
    /// </summary>
    public required IReadOnlyList<string> ValueChainDetail { get; init; }

    /// <summary>
    /// Per-<c>(corr, exec)</c> trip duration in milliseconds (73, D-12), keyed by <c>"correlationId|executionId"</c>:
    /// the first-step → <c>Step_G</c>-terminal ES <c>@timestamp</c> delta (min→max span of the run's hits). The
    /// engine does NOT read ES timestamps; the live fixture (Plan 04) computes + passes this map, the hermetic
    /// facts pass synthetic/empty. No histogram, no <c>src/</c> change.
    /// </summary>
    public required IReadOnlyDictionary<string, double> TripDurationMsByExecution { get; init; }

    /// <summary>
    /// Per-<c>correlationId</c> aggregate trip duration in milliseconds (73, D-12): the min→max ES
    /// <c>@timestamp</c> span across all of a correlationId's runs. Supplied by the live fixture (Plan 04);
    /// synthetic/empty in the hermetic facts.
    /// </summary>
    public required IReadOnlyDictionary<string, double> TripDurationMsByCorrelation { get; init; }

    /// <summary>A human-readable one-line summary of the verdict and its drivers.</summary>
    public required string HumanSummary { get; init; }
}
