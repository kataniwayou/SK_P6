using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BaseApi.Tests.Observability.Analysis;
using BaseApi.Tests.Observability.Helpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// HA-07 — the self-verifying RealStack failover-verdict fixture (Phase 83 Plan 02, Wave 2). The thin
/// integration shell that gathers REAL telemetry over the captured failover window, feeds the pure
/// <see cref="HaFireBucketScorer"/> (Plan 01), writes the JSON report, and asserts the verdict. It proves
/// all five HA-07 claims from TWO ES evidence streams: the leader send-evidence stream (processor
/// <c>attributes.StepId</c> hop-executed records — the leader's dispatch itself emits NO log, so the
/// downstream <c>StepId</c> record is the clean leader-only send oracle, and follower-skip logs, which
/// carry no <c>StepId</c>, fall out automatically) and the role-flip stream (the earliest
/// <c>attributes.role=leader</c> record after <c>KILL_UTC</c> — the survivor's follower→leader flip).
///
/// <remarks>
/// <para>
/// <b>Harness contract.</b> This is the D-04 verdict fact the <c>scripts/phase-83-ha-failover.ps1</c>
/// sequencer invokes via <c>dotnet test -- --filter-method "*Ha_Failover_Window_Yields_Pass*"</c>, reading
/// the process EXIT CODE (a Fail/Inconclusive verdict ⇒ failed assert ⇒ non-zero exit) PLUS the
/// <c>analyzer-reports/{scenarioId}.json</c> artifact (whose string <c>Verdict</c> the harness maps 0/1/2
/// through <c>Resolve-AnalyzerExitCode</c>). The harness pins <c>WINDOW_START_UTC</c>/<c>WINDOW_END_UTC</c>/
/// <c>KILL_UTC</c> around the phase-aligned leader kill.
/// </para>
/// <para>
/// <b>RealStack, NOT hermetic.</b> Tagged <c>Category=RealStack</c> so the hermetic filter
/// (<c>Category!=RealStack</c>) excludes it. <c>[Collection("Observability")]</c> serializes against the
/// shared ES backend. PRECONDITION: the full k8s stack must be up (orchestrator ×3 replicas electing a
/// leader; processor-sample producing <c>StepId</c> hop records; the <c>v8-fanout-proof</c> workflow seeded
/// + firing on the <c>*/30</c> cron; the leader force-killed mid-run). The pure scoring logic
/// (<see cref="HaFireBucketScorer"/>) is exercised Docker-less by <c>HaFireBucketScorerFacts</c>.
/// </para>
/// <para>
/// <b>Read-only.</b> The verdict writes NO Redis/ES state — the harness owns the workload; the fixture only
/// reads the two evidence streams over <c>_search</c> and writes the JSON report file.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class HaFailoverAnalyzerE2ETests
{
    // D-05 drain: bounded settle after the observation window closes so the post-recovery fire AND the
    // role-flip record finish exporting to ES before scoring (Pitfall 4 — the hold + drain must cover
    // KILL_UTC + gap(<=17s) + one cron period(30s) + export(~60s)).
    private const int DrainMs = 60_000;
    private const int PollToStableBudgetMs = 60_000;   // poll-to-stable budget, separate from DrainMs
    private const int PollToStableMs = 5_000;          // re-poll interval; snapshot when the ES hit count is
                                                       //   unchanged across two consecutive polls

    // The default scenario id for the phase fixture. The phase-83 harness passes its own per-run id.
    private const string DefaultScenarioId = "phase-83-ha";

    // V5 / T-83-01 — scenario-id whitelist. Validated BEFORE composing any filesystem path so a
    // caller-supplied id can never traverse out of the fixed reports dir (no '/', '\', '.', etc.).
    private static readonly Regex ScenarioIdPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    // Env-var seam — try to parse a harness-supplied round-trip ("o"-format) UTC timestamp, reporting
    // SUCCESS/FAILURE. Used for WINDOW_START_UTC, WINDOW_END_UTC, and KILL_UTC. AssumeUniversal|
    // AdjustToUniversal normalizes the PowerShell-emitted ISO-8601 offset form to UTC; a null/empty/malformed
    // value yields false (the RealStack fact then asserts the harness pinned them).
    private static bool TryParseUtc(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);

    /// <summary>
    /// Build the window-bounded SEND-EVIDENCE <c>_search</c> body (RESEARCH Code Example 2): a STATIC
    /// raw-string template (T-83-02) filtering on <c>exists attributes.StepId</c> + <c>exists
    /// attributes.CorrelationId</c> over the <c>[windowStart, windowEnd]</c> range. <c>exists StepId</c> is
    /// the clean leader-only filter — the processor hop-executed record proves the leader actually fired for
    /// a tick, and follower-skip logs (no StepId) are naturally excluded. Only the Wave-0-verified
    /// <see cref="EsIndexNames"/> field consts (direct keyword paths, never a sub-field suffix) and the window
    /// timestamps are interpolated, so there is no injection surface. Size-bounded to 2000, sorted ascending
    /// on the window timestamp field.
    /// </summary>
    private static string BuildSendEvidenceBody(DateTimeOffset windowStart, DateTimeOffset windowEnd) => $$"""
      {
        "size": 2000,
        "query": {
          "bool": {
            "filter": [
              { "exists": { "field": "{{EsIndexNames.StepIdFieldPath}}" } },
              { "exists": { "field": "{{EsIndexNames.CorrelationIdFieldPath}}" } },
              { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": {
                  "gte": "{{windowStart:o}}", "lte": "{{windowEnd:o}}" } } }
            ]
          }
        },
        "sort": [ { "{{EsIndexNames.WindowTimestampFieldPath}}": "asc" } ]
      }
      """;

    /// <summary>
    /// Build the ROLE-FLIP <c>_search</c> body (RESEARCH Code Example 5, claims #4+#5): a STATIC raw-string
    /// template (T-83-02) with a <c>term</c> on <see cref="EsIndexNames.RoleFieldPath"/> == <c>"leader"</c>
    /// (a keyword, direct path with no sub-field suffix) + a <c>range</c> <c>gt</c> <c>{killUtc:o}</c>
    /// on the window timestamp field. The earliest post-kill <c>role=leader</c> record is the survivor's
    /// follower→leader flip — the single source of truth for bounded-recovery (claim #4) AND
    /// role-transition-visible (claim #5). Size-bounded to 20 (the survivor flips once), sorted ascending on
    /// the window timestamp field.
    /// </summary>
    private static string BuildRoleFlipBody(DateTimeOffset killUtc) => $$"""
      {
        "size": 20,
        "query": {
          "bool": {
            "filter": [
              { "term": { "{{EsIndexNames.RoleFieldPath}}": "leader" } },
              { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": { "gt": "{{killUtc:o}}" } } }
            ]
          }
        },
        "sort": [ { "{{EsIndexNames.WindowTimestampFieldPath}}": "asc" } ]
      }
      """;

    /// <summary>
    /// Poll <see cref="ElasticsearchTestClient.SearchAllHits"/> over the given <paramref name="body"/> until
    /// the returned hit count is unchanged across two consecutive polls AND the count is non-zero (D-05
    /// poll-to-stable). This prevents scoring an in-flight window before the post-recovery fire / role-flip
    /// record has fully exported. A genuinely empty stream (e.g. no role flip) exhausts the budget and returns
    /// the empty list — the scorer's fail-closed fold then classifies it (never a vacuous Pass). Bounded by
    /// <see cref="PollToStableBudgetMs"/> total wall-clock (separate from <see cref="DrainMs"/>).
    /// </summary>
    private static async Task<List<JsonElement>> PollHitsToStableAsync(
        ElasticsearchTestClient es, string body, CancellationToken ct)
    {
        var last = await es.SearchAllHits(body, ct: ct);
        var deadline = DateTime.UtcNow.AddMilliseconds(PollToStableBudgetMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollToStableMs, ct);
            var current = await es.SearchAllHits(body, ct: ct);
            if (current.Count == last.Count && current.Count > 0)
            {
                // stable across two polls AND we actually have hits — safe to score. Requiring Count > 0
                // prevents a transient empty ES result (404 lazy-index, backend blip) from being accepted
                // as "stable" too early.
                return current;
            }
            last = current;
        }
        return last;
    }

    /// <summary>
    /// Defensive read of a hit's <c>_source.attributes.CorrelationId</c> (T-66-09): navigate the
    /// <c>_source → attributes → CorrelationId</c> chain, requiring each step present and the value a JSON
    /// string; an odd-shaped hit returns false (dropped by the caller, never thrown on).
    /// </summary>
    private static bool TryReadCorr(JsonElement hit, out string corr)
    {
        corr = string.Empty;
        if (!hit.TryGetProperty("_source", out var source)) return false;
        if (!source.TryGetProperty("attributes", out var attrs)) return false;
        if (!attrs.TryGetProperty("CorrelationId", out var corrEl) || corrEl.ValueKind != JsonValueKind.String)
            return false;
        corr = corrEl.GetString()!;
        return true;
    }

    /// <summary>
    /// Defensive top-level <c>_source.@timestamp</c> read — COPIED VERBATIM from
    /// <c>AnalyzerE2ETests.TryReadTimestamp</c> (the phase-73 trap). The OTLP→ES bridge serializes
    /// <c>@timestamp</c> in <c>_source</c> as epoch MILLISECONDS rendered as a NUMERIC STRING with a sub-ms
    /// fraction (e.g. <c>"1781754330089.184100"</c>), NOT an ISO-8601 datetime — a naive
    /// <c>DateTimeOffset.TryParse</c> REJECTS that bare numeric string and returns empty. Parse it as epoch-ms
    /// FIRST (<c>FromUnixTimeMilliseconds</c>); a genuine ISO-8601 string is non-numeric and falls through to
    /// the ISO fallback. An odd-shaped/missing/unparseable value is dropped (returns false), never thrown on.
    /// </summary>
    private static bool TryReadTimestamp(JsonElement source, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!source.TryGetProperty("@timestamp", out var tsEl)) return false;

        // Epoch-millis rendered as a bare JSON Number (defensive — some ES mappings).
        if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetDouble(out var epochMsNum))
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMsNum);
            return true;
        }

        if (tsEl.ValueKind == JsonValueKind.String)
        {
            var raw = tsEl.GetString();
            // LIVE shape: epoch MILLISECONDS as a NUMERIC STRING with sub-ms fraction — parse as epoch-ms FIRST.
            if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var epochMsStr))
            {
                timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMsStr);
                return true;
            }
            // Fallback: a genuine ISO-8601 string @timestamp (defensive — other ES mappings / standalone setups).
            return DateTimeOffset.TryParse(
                raw,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out timestamp);
        }
        return false;
    }

    /// <summary>
    /// Parse the raw send-evidence ES hits into <see cref="SendEvidenceRecord"/>s for
    /// <see cref="HaFireBucketScorer.Score"/>. For each hit: read <c>_source</c>, <see cref="TryReadCorr"/> +
    /// <see cref="TryReadTimestamp"/>; on success append <c>new SendEvidenceRecord(corr, ts)</c>; an
    /// odd-shaped hit is dropped with <c>continue</c> (T-66-09 — never thrown on). The scorer takes each
    /// correlationId's EARLIEST timestamp as the fire time, so multiple <c>StepId</c> records per fire collapse
    /// correctly.
    /// </summary>
    private static List<SendEvidenceRecord> ParseSendRecords(IReadOnlyList<JsonElement> hits)
    {
        var records = new List<SendEvidenceRecord>(hits.Count);
        foreach (var hit in hits)
        {
            if (!hit.TryGetProperty("_source", out var source)) continue;
            if (!TryReadCorr(hit, out var corr)) continue;
            if (!TryReadTimestamp(source, out var ts)) continue;
            records.Add(new SendEvidenceRecord(corr, ts));
        }
        return records;
    }

    /// <summary>
    /// The serializable HA-07 failover verdict report (mirrors the <c>AnalyzerReport</c> field style so the
    /// <c>phase-83-ha.json</c> shape is recognizable). The top-level <see cref="Verdict"/> is serialized as
    /// its STRING name (<c>[JsonConverter(typeof(JsonStringEnumConverter))]</c>, reusing the shared
    /// <see cref="Verdict"/> enum) so <c>Resolve-AnalyzerExitCode</c> maps it 0/1/2 authoritatively — the
    /// written report is the harness's source of truth even on a red run (write-then-assert, T-83-04). The
    /// five per-claim flags + the measured recovery let a red report self-explain.
    /// </summary>
    private sealed record HaFailoverReport
    {
        public required string ScenarioId { get; init; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public required Verdict Verdict { get; init; }

        /// <summary>Claim #1+#2: every 30s bucket held ≤1 distinct send-correlationId (no split-brain / dup).</summary>
        public required bool ZeroDuplicate { get; init; }
        /// <summary>Claim #3 (half): ≥1 contiguous EMPTY election-gap tick observed.</summary>
        public required bool GapObserved { get; init; }
        /// <summary>Claim #3 (half): no election-gap tick was later backfilled with a fire.</summary>
        public required bool NotBackfilled { get; init; }
        /// <summary>Claim #5: an <c>attributes.role=leader</c> record appeared after the kill (the survivor's flip).</summary>
        public required bool RoleFlipVisible { get; init; }
        /// <summary>Claim #4: the role flip landed within 2×LeaseDuration (30s) of the kill.</summary>
        public required bool BoundedRecovery { get; init; }

        /// <summary>The measured recovery delta (roleFlipUtc − killUtc) in seconds, or null when no flip observed.</summary>
        public double? RecoverySeconds { get; init; }

        public required DateTimeOffset KillUtc { get; init; }
        public required DateTimeOffset WindowStart { get; init; }
        public required DateTimeOffset WindowEnd { get; init; }

        /// <summary>Distinct send-correlationIds observed in the window (the leader-fire count).</summary>
        public required int SendCorrIdCount { get; init; }
        /// <summary>Distinct 30s wall-clock buckets that held a fire.</summary>
        public required int BucketCount { get; init; }

        /// <summary>Human-readable per-flag reasoning from the scorer, so a red report is self-explaining.</summary>
        public required string HumanSummary { get; init; }
    }

    /// <summary>
    /// The single HA-07 verdict fact. Fetches the two ES evidence streams (send-evidence + role-flip) over the
    /// harness-pinned window, drains + polls-to-stable, feeds the parsed records to the pure
    /// <see cref="HaFireBucketScorer.Score"/>, writes the <c>phase-83-ha.json</c> report (string Verdict)
    /// BEFORE asserting, then asserts <c>Verdict == Pass</c>. A Fail/Inconclusive yields a non-zero process
    /// exit; the harness reads the JSON for the exact 0/1/2.
    /// </summary>
    [Fact]
    public async Task Ha_Failover_Window_Yields_Pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenarioId = Environment.GetEnvironmentVariable("SCENARIO_ID") ?? DefaultScenarioId;

        // ── 1. SCENARIO ID + PATH-TRAVERSAL GUARD (Security V5 / T-83-01) ─────────────────────────────
        //    Validate against the ^[A-Za-z0-9_-]+$ whitelist BEFORE composing any path.
        Assert.True(
            ScenarioIdPattern.IsMatch(scenarioId),
            $"scenarioId '{scenarioId}' must match ^[A-Za-z0-9_-]+$ (path-traversal guard).");

        // ── 2. WINDOW + KILL SEAM (RealStack: the harness MUST have pinned all three) ─────────────────
        var windowPinned =
            TryParseUtc(Environment.GetEnvironmentVariable("WINDOW_START_UTC"), out var windowStart)
            & TryParseUtc(Environment.GetEnvironmentVariable("WINDOW_END_UTC"), out var windowEnd);
        var killPinned = TryParseUtc(Environment.GetEnvironmentVariable("KILL_UTC"), out var killUtc);

        Assert.True(windowPinned,
            "WINDOW_START_UTC and WINDOW_END_UTC must be pinned by the phase-83 harness (RealStack fact).");
        Assert.True(killPinned,
            "KILL_UTC must be pinned by the phase-83 harness at kubectl-delete return (RealStack fact).");

        using var es = new ElasticsearchTestClient();

        // ── 3. DRAIN (Pitfall 4) — let the post-recovery fire + the role-flip record finish exporting ──
        await Task.Delay(DrainMs, ct);

        // ── 4. FETCH BOTH EVIDENCE STREAMS (poll-to-stable so no in-flight export is scored) ──────────
        var stepHits = await PollHitsToStableAsync(es, BuildSendEvidenceBody(windowStart, windowEnd), ct);
        var roleHits = await PollHitsToStableAsync(es, BuildRoleFlipBody(killUtc), ct);

        // roleFlipUtc = the EARLIEST @timestamp among the post-kill role=leader records, or null if none.
        DateTimeOffset? roleFlipUtc = null;
        foreach (var hit in roleHits)
        {
            if (!hit.TryGetProperty("_source", out var source)) continue;
            if (!TryReadTimestamp(source, out var ts)) continue;
            if (roleFlipUtc is null || ts < roleFlipUtc) roleFlipUtc = ts;
        }

        // ── 5. PARSE + SCORE (pure — no IO) ───────────────────────────────────────────────────────────
        var sends = ParseSendRecords(stepHits);
        var v = HaFireBucketScorer.Score(sends, killUtc, roleFlipUtc);

        // Report-only evidence counts (the scorer folds these internally; surfaced here for a self-explaining
        // report). Each fire's EARLIEST timestamp per correlationId, floored to its 30s wall-clock bucket.
        var fireTimeByCorr = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var r in sends)
        {
            if (!fireTimeByCorr.TryGetValue(r.CorrelationId, out var cur) || r.Timestamp < cur)
                fireTimeByCorr[r.CorrelationId] = r.Timestamp;
        }
        var bucketCount = fireTimeByCorr.Values.Select(HaFireBucketScorer.Bucket30s).Distinct().Count();

        // ── 6. BUILD REPORT (string Verdict for Resolve-AnalyzerExitCode) ─────────────────────────────
        var report = new HaFailoverReport
        {
            ScenarioId = scenarioId,
            Verdict = v.Verdict,
            ZeroDuplicate = v.ZeroDuplicate,
            GapObserved = v.GapObserved,
            NotBackfilled = v.NotBackfilled,
            RoleFlipVisible = v.RoleFlipVisible,
            BoundedRecovery = v.BoundedRecovery,
            RecoverySeconds = v.Recovery?.TotalSeconds,
            KillUtc = killUtc,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            SendCorrIdCount = fireTimeByCorr.Count,
            BucketCount = bucketCount,
            HumanSummary = v.HumanSummary,
        };

        // ── 7. WRITE-THEN-ASSERT (order is load-bearing — T-83-04) ────────────────────────────────────
        //    Serialize + write the JSON report FIRST so the artifact exists even on a red run, and the
        //    persisted report + the exit code always reflect the SAME verdict.
        var reportsDir = Path.Combine(AppContext.BaseDirectory, "analyzer-reports");
        Directory.CreateDirectory(reportsDir);
        var reportPath = Path.Combine(reportsDir, $"{scenarioId}.json");

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json, ct);                  // FIRST — exists on red

        Assert.True(report.Verdict == Verdict.Pass, report.HumanSummary);    // THEN — FAIL ⇒ non-zero exit
    }
}
