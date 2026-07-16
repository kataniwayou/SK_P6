using System.Text.Json;
using System.Text.RegularExpressions;
using BaseApi.Tests.Observability.Analysis;
using BaseApi.Tests.Observability.Helpers;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// OBS-04 — the self-verifying RealStack analyzer fixture (Phase 66 Plan 03, Wave 2). The thin
/// integration shell that gathers REAL telemetry, feeds the pure <see cref="PassFailEngine"/>
/// (Plan 01), writes the JSON report, and asserts the verdict. It exercises OBS-01 (per-run traces
/// from <c>Step_*</c> ES hits), OBS-02 (zero-missing + fail-closed duplicate, via the engine), and
/// OBS-03 (live Prometheus counters reconciled as WINDOWED DELTAS) end-to-end against the live stack.
///
/// <remarks>
/// <para>
/// <b>Harness contract.</b> This is the OBS-04 fixture the Phase 67/68 fault-injection harness invokes
/// via <c>dotnet test --filter "Category=RealStack&amp;FullyQualifiedName~Analyzer"</c>, reading the
/// process EXIT CODE (a FAIL verdict ⇒ failed assert ⇒ non-zero exit) PLUS the
/// <c>analyzer-reports/{scenarioId}.json</c> artifact. The harness parameterizes the scenario id +
/// window timing; the single fact proves the analyzer pipeline produces a green verdict against a
/// recovered live window (a happy-path baseline OR a post-fault recovery — zero-missing + effect-once).
/// </para>
/// <para>
/// <b>RealStack, NOT hermetic.</b> Tagged <c>Category=RealStack</c> so the hermetic filter
/// (<c>Category!=RealStack</c>) excludes it (D-01, Pitfall 6). <c>[Collection("Observability")]</c>
/// serializes against the shared ES/Prom backends. PRECONDITION: the full compose stack must be up
/// healthy (collector → Prometheus scraping; orchestrator + processor-sample producing increments;
/// the fan-out workflow seeded + firing per Phase 65 bring-up). Fails LOUD (never silently passes) if
/// a backend is unreachable — <see cref="PrometheusTestClient.QueryPrometheus"/> Assert.Fails on
/// non-success.
/// </para>
/// <para>
/// <b>Prom windowing (A3 / Pitfall 3).</b> Task 1 confirmed <c>scripts/phase-65-reset.ps1</c> does
/// FLUSHALL + heal with NO container restart, so the counters are process-lifetime CUMULATIVE. This
/// fixture therefore reads each counter as a WINDOWED DELTA (snapshot-before − snapshot-after), never
/// raw cumulative. (Phase 67 open question: a crashed+restarted tier mid-window resets its counters,
/// breaking delta continuity — which is WHY ES-primary completeness, counter-independent, is the binding
/// arbiter and Prom reconciliation is corroborating only. The harness/Phase 67 resolves that; here the
/// happy-path window has no restart.)
/// </para>
/// <para>
/// <b>Read-only.</b> The analyzer writes NO Redis state (the seeder owns those writes), so this factory
/// OMITS the <c>L2KeysToCleanup</c> / parent-index / composite-key net-zero sweep that
/// <see cref="Orchestrator.MetricsRoundTripE2ETests"/> needs. Its only write is the JSON report file.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class AnalyzerE2ETests
{
    // D-05 drain: bounded settle after the observation window closes so in-flight runs finish their
    // 9-step traversal before scoring (Pitfall 4 — scoring mid-traversal would mis-flag a run MISSING).
    private const int DrainMs = 60_000;                // > worst-case 9-step traversal
    private const int PollToStableBudgetMs = 60_000;   // poll-to-stable budget, separate from DrainMs
                                                       //   (WR-03 fix — 66-REVIEW.md). Total worst-case
                                                       //   wall-clock = DrainMs + PollToStableBudgetMs = 120 s.
    private const int PollToStableMs = 5_000;          // re-poll interval; snapshot when the ES hit count is
                                                       //   unchanged across two consecutive polls

    // The default scenario id for the phase fixture. The Phase 67/68 harness passes its own per-run id.
    private const string DefaultScenarioId = "TEST-01";

    // V5 / T-66-07 — scenario-id whitelist. Validated BEFORE composing any filesystem path so a
    // caller-supplied id can never traverse out of the fixed reports dir (no '/', '\', '.', etc.).
    private static readonly Regex ScenarioIdPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);

    // MG-2 calibration (DONE, evidence-based): which scenarios recover THROUGH the keeper. The reporting-only
    // sweep (2026-06-18) showed keeper_messages_consumed/sent == 0 in ALL of TEST-01..07 — none of these
    // whole-tier crash scenarios exercise the keeper's recovery consumers (recovery is by broker redelivery +
    // retry, NOT keeper REINJECT/INJECT/DELETE). So every scenario stays false; MG-2 is dormant for this
    // scenario set and the keeper's contribution is proven instead by MG-3 (keeper_l2_probe liveness, which
    // passes everywhere). Set a scenario true only if a future fault drives non-zero keeper_messages_* deltas.
    private static readonly IReadOnlyDictionary<string, bool> ExpectsKeeperActivity =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEST-01"] = false, ["TEST-02"] = false, ["TEST-03"] = false, ["TEST-04"] = false,
            ["TEST-05"] = false, ["TEST-06"] = false, ["TEST-07"] = false,
        };

    // MG-1 binding per scenario: conservation (a windowed Prom delta) is a VALID measure only where no
    // conservation-counter-owning tier restarts (a crash resets that process's counters) and no L2 wipe
    // occurs (a wipe causes tolerated in-flight loss). Binding on the clean scenarios; reporting-only on the
    // processor/orchestrator crashes (counter reset) and the redis-wipe scenarios. Default true (TEST-01-like).
    private static readonly IReadOnlyDictionary<string, bool> Mg1Binding =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEST-01"] = true,  ["TEST-02"] = false, ["TEST-03"] = false, ["TEST-04"] = true,
            ["TEST-05"] = false, ["TEST-06"] = true,  ["TEST-07"] = false,
        };

    // D-16 env-var seam — try to parse a harness-supplied round-trip ("o"-format) UTC timestamp,
    // reporting SUCCESS/FAILURE. The fixture uses the result to decide whether the D-16 window seam is
    // genuinely PRESENT (both WINDOW_*_UTC parsed) and so whether to time-pin the Prom counter reads
    // (67-03 / OBS-04 denominator fix) vs. fall back to the standalone live before/after snapshots.
    // AssumeUniversal|AdjustToUniversal normalizes the PowerShell-emitted ISO-8601 offset form to UTC;
    // a null/empty/malformed value yields false ⇒ the standalone live-snapshot path (T-67-04: a bad
    // WINDOW_*_UTC never crashes, it reverts to the self-window default).
    private static bool TryParseUtc(string? value, out DateTimeOffset parsed) =>
        DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out parsed);

    [Fact]
    public async Task Analyze_Window_Yields_Pass()
    {
        var ct = TestContext.Current.CancellationToken;
        var scenarioId = Environment.GetEnvironmentVariable("SCENARIO_ID") ?? DefaultScenarioId;

        // ── 1. SCENARIO ID + PATH-TRAVERSAL GUARD (Security V5 / T-66-07) ────────────────────────────
        //    Validate against the ^[A-Za-z0-9_-]+$ whitelist BEFORE composing any path. Compose the
        //    report path ONLY under a fixed reports dir — no caller-controlled directory component.
        Assert.True(
            ScenarioIdPattern.IsMatch(scenarioId),
            $"scenarioId '{scenarioId}' must match ^[A-Za-z0-9_-]+$ (path-traversal guard).");

        var reportsDir = Path.Combine(AppContext.BaseDirectory, "analyzer-reports");
        Directory.CreateDirectory(reportsDir);
        var reportPath = Path.Combine(reportsDir, $"{scenarioId}.json");

        using var es = new ElasticsearchTestClient();
        using var prom = new PrometheusTestClient();

        // ── 2. WINDOW SEAM DETECTION + PROM BEFORE-SNAPSHOT (windowing, A3 / Pitfall 3) ───────────────
        //    The D-16 window seam is PRESENT only when BOTH WINDOW_*_UTC env vars parse (harness mode).
        //    In that mode we TIME-PIN the Prom reads (step 5) to the recorded window bounds — the fixture
        //    runs at window CLOSE, so a live "now" before-snapshot would already include all ~10 in-window
        //    fires, collapsing OrchestratorMessagesSentDelta to a ~60 s tail and under-counting the trigger denominator
        //    (67-03 / OBS-04). Pinning gives delta = counter@windowEnd − counter@windowStart, matching the
        //    ES [windowStart, windowEnd] cohort.
        //
        //    When the seam is ABSENT (standalone Phase 66 run — no env vars), windowPinned is false and the
        //    path below is byte-for-byte the original behavior: ES window defaults to UtcNow, and the Prom
        //    BEFORE snapshot is a live "now" read taken HERE (pre-drain), the AFTER a live read post-poll.
        var windowPinned =
            TryParseUtc(Environment.GetEnvironmentVariable("WINDOW_START_UTC"), out var pinnedWindowStart)
            & TryParseUtc(Environment.GetEnvironmentVariable("WINDOW_END_UTC"), out var pinnedWindowEnd);

        var windowStartUtc = windowPinned ? pinnedWindowStart : DateTimeOffset.UtcNow;

        // Standalone (seam absent): read the live BEFORE counter set NOW (window start). In window-pinned
        // mode this live read is SKIPPED — both counter sets are time-pinned reads taken in step 5.
        var before = windowPinned ? null : await ReadCounterSetAsync(prom, ct);

        // ── 3. DRAIN (D-05 / Pitfall 4) + POLL-TO-STABLE ─────────────────────────────────────────────
        //    After the observation window closes (harness-controlled wall-clock; here a bounded delay),
        //    poll SearchAllHits until the hit count is unchanged across two consecutive polls so no
        //    in-flight run is scored MISSING. (Kept for ES COMPLETENESS in BOTH modes — correct as-is.)
        await Task.Delay(DrainMs, ct);

        // snapshotUtc is the ES range upper bound. In window-pinned mode it is the recorded windowEnd
        // (so the ES range exactly matches the time-pinned Prom delta cohort). In standalone mode it is
        // captured HERE — before poll-to-stable — so the ES window is bounded by a stable timestamp that
        // does not shift during polling.
        var snapshotUtc = windowPinned ? pinnedWindowEnd : DateTimeOffset.UtcNow;

        // WR-02 fix (standalone only): take the live AFTER counter read NOW — at the SAME instant
        // snapshotUtc is captured, BEFORE poll-to-stable — so the Prom delta cohort matches the ES
        // [windowStart, snapshotUtc] range exactly. Previously the AFTER read happened post-poll, so a
        // run dispatched in the tail gap between snapshotUtc and the AFTER read was counted in
        // OrchestratorMessagesSentDelta yet excluded from the ES range → a spurious dead-run corroboration warning
        // (under the old binding model, a spurious MISSING). Aligning the read closes that gap. Window-
        // pinned mode is UNAFFECTED — both its Prom reads are pinned to the recorded bounds in step 5.
        var standaloneAfter = windowPinned ? null : await ReadCounterSetAsync(prom, ct);

        var stepHits = await PollHitsToStableAsync(es, windowStartUtc, snapshotUtc, ct);

        // ── 4. ES READ (OBS-01 + 73 D-11/D-12) — group Step_* hits into per-run RunTraces by
        //    attributes.CorrelationId, capturing per-label attributes.Produced values + the @timestamp
        //    trip-duration spans (all from the same hits, no new ES query).
        // D75-4 live join: read the keeper REINJECT drop/success docs (attributes.ReinjectOutcome) over the
        // SAME window and build the per-(corr,exec) keeper-outcome map. The keeper docs ride the same OTLP
        // pipeline as the Step_* docs, so the DrainMs + poll-to-stable above already tolerated the ~60 s
        // export skew — no new drain is needed. ES-read-only preserved: the join comes from keeper LOGS in
        // ES, never a live Redis probe.
        var keeperHits = await es.SearchAllHits(
            BuildKeeperOutcomeSearchBody(windowStartUtc, snapshotUtc), ct: ct);
        var keeperOutcomeByExecution = BuildKeeperOutcomeMap(keeperHits);

        // Phase 76 ANL-02/ANL-03: the FW-02 orchestrator dispatch records (attributes.NextStepId) supply the
        // ES-derived expected set + the redundancy edge. They ride the same OTLP pipeline as the structural
        // StepId records, so the DrainMs + poll-to-stable above already tolerated the ~60 s export skew — no
        // new drain needed. (D-03/Phase 78: the SEPARATE value-oracle query is deleted — the sample author
        // logs are gone post-Phase-77, so the verdict stands on structural completeness + ANL-03 + the metric
        // gate alone; no StepLabel/Produced value evidence is fetched.)
        var dispatchHits = await es.SearchAllHits(
            BuildDispatchSearchBody(windowStartUtc, snapshotUtc), ct: ct);

        var cohort = BuildRunTraces(stepHits, dispatchHits, keeperOutcomeByExecution);
        var traces = cohort.Traces;

        // ── 5. PROM SNAPSHOTS + WINDOWED DELTAS (OBS-03) ─────────────────────────────────────────────
        //    Window-pinned (harness): instant-query BOTH counter sets AT the recorded window bounds.
        //    Standalone (seam absent): keep the live BEFORE (step 2) and the live AFTER captured at
        //    snapshotUtc (pre-poll, WR-02) — NOT a fresh post-poll read, so the Prom delta cohort
        //    aligns with the ES [windowStart, snapshotUtc] range.
        var (beforeSet, afterSet) = windowPinned
            ? (await ReadCounterSetAsync(prom, ct, pinnedWindowStart),
               await ReadCounterSetAsync(prom, ct, pinnedWindowEnd))
            : (before!, standaloneAfter!);
        var promSnapshot = BuildSnapshot(beforeSet, afterSet);

        // ── 6. FAN-OUT FIRE PRECONDITION (ES-binding) ────────────────────────────────────────────────
        //    The fan-out must have actually fired in the window. The BINDING evidence of a fire is the ES
        //    started-run count (distinct correlationIds with >=1 Step_* log), NOT the raw Prom counter
        //    delta. A force-recreate / orchestrator restart resets orchestrator_messages_sent_total AND
        //    leaves the pre-restart container's series lingering in Prom's ~5min instant-query lookback, so
        //    the windowed delta can sum to a NEGATIVE value (counter reset) even when the DAG fired normally
        //    — observed live (phase-73 harness run): OrchestratorMessagesSentDelta=-2337 with 20 ES started
        //    runs. Per this guard's own documented intent ("a zero OrchestratorMessagesSentDelta AND zero ES
        //    traces would let the verdict pass vacuously"), the broken precondition is ONLY a true no-fire:
        //    no Prom delta AND no ES traces. Phase 73 is ES-primary and explicitly defers metric
        //    assertions, so a Prom-only counter-reset artifact must never abort the ES value-chain audit.
        //    (WR-04 + phase-73 live fix.)
        Assert.True(promSnapshot.OrchestratorMessagesSentDelta != 0 || traces.Count > 0,
            $"No fan-out fire observed in the window: OrchestratorMessagesSentDelta={promSnapshot.OrchestratorMessagesSentDelta} " +
            $"AND zero ES Step_* traces (started runs={traces.Count}); the precondition is not satisfied.");

        // ── 7. RUN THE ENGINE (pure — no IO) ─────────────────────────────────────────────────────────
        // TRIP-DURATION FEED (73, D-12): pass the @timestamp trip-duration maps into the engine; they land in
        // the report. (D-03/Phase 78: the concrete value oracle is deleted — no per-execution seed map / value
        // chain is fed; the verdict stands on structural stepId completeness + ANL-03 redundancy + the metric
        // gate.) NO metric-counter assertion is added; NO Redis skp:out: blob is read (ES-read-only, D-11).
        // B-CRITERION FEED (Plan 3): parse the harness RECOVERY_UTC seam (the all-tiers-healthy instant). A
        // null/empty/malformed value (no-fault baseline TEST-01) yields false ⇒ pass recoveryUtc:null, so the
        // engine treats every started-but-incomplete run as a binding miss (correct — a no-fault run has none).
        // The first/last hop maps let the engine classify a tolerated in-flight-at-wipe loss (last hop <
        // recovery) vs a binding post-recovery miss for a redis-wipe scenario.
        DateTimeOffset? recoveryUtc =
            TryParseUtc(Environment.GetEnvironmentVariable("RECOVERY_UTC"), out var parsedRecovery)
                ? parsedRecovery
                : null;

        var report = new PassFailEngine().Analyze(
            traces, promSnapshot, scenarioId,
            tripDurationMsByExecution: cohort.TripDurationMsByExecution,
            tripDurationMsByCorrelation: cohort.TripDurationMsByCorrelation,
            expectsKeeperActivity: ExpectsKeeperActivity.GetValueOrDefault(scenarioId, false),
            mg1Binding: Mg1Binding.GetValueOrDefault(scenarioId, true),
            recoveryUtc: recoveryUtc,
            firstHopUtcByExecution: cohort.FirstHopUtcByExecution,
            lastHopUtcByExecution: cohort.LastHopUtcByExecution,
            keeperOutcomeByExecution: cohort.KeeperOutcomeByExecution,
            // Phase 76 ANL-02: the ES-derived FW-02 expected set gates stepId-keyed completeness (a dispatched-
            // but-never-executed step is a binding miss — TEST-08 closed). Derived from ES records only (T-76-07).
            expectedStepIdsByExecution: cohort.ExpectedStepIdsByExecution,
            // Phase 76 ANL-03: the orchestrator-redundancy proven set reconciles a dropped processor record whose
            // output EntryId was consumed by an orchestrator fan-out/terminal record as a NON-binding telemetry
            // gap — WITH NO seed oracle. Missing from BOTH records stays a binding miss.
            orchestratorConsumedStepIdsByExecution: cohort.OrchestratorConsumedStepIdsByExecution);

        // ── 8. WRITE-THEN-ASSERT (D-02 / OBS-04 / T-66-11) ───────────────────────────────────────────
        //    Serialize + write the JSON report FIRST so the artifact exists even on a red run, and the
        //    persisted report + the exit code always reflect the SAME report object.
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(reportPath, json, ct);                 // FIRST — exists on red
        await File.WriteAllTextAsync(
            Path.Combine(reportsDir, $"{scenarioId}.txt"), report.HumanSummary, ct);

        Assert.True(report.Verdict == Verdict.Pass, report.HumanSummary);   // THEN — FAIL ⇒ non-zero exit
    }

    // ── ES → RunTrace grouping (OBS-01) ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Build the window-bounded STRUCTURAL <c>_search</c> body (Phase 76, ANL-01/D-15): a STATIC raw-string
    /// template (T-66-08) filtering on the framework per-hop record's <see cref="EsIndexNames.StepIdFieldPath"/>
    /// (<c>attributes.StepId</c>) — the single stepId-keyed source of structural completeness that REPLACES the
    /// deleted <c>StepLabel</c> set-comparison. NO <c>Step_*</c> literal, NO <c>.keyword</c> sub-field (Pitfall
    /// 2). The query also returns the orchestrator terminal-reached records (they carry <c>attributes.StepId</c>
    /// too); <see cref="BuildRunTraces"/> discriminates the processor "did-run" record (has
    /// <c>attributes.MessageId</c>) from the terminal-reached redundancy record (has <c>attributes.WorkflowId</c>,
    /// no MessageId). Size-bounded to 2000, sorted ascending on the window timestamp field.
    /// </summary>
    private static string BuildStepSearchBody(DateTimeOffset windowStart, DateTimeOffset snapshot) => $$"""
      {
        "size": 2000,
        "query": {
          "bool": {
            "filter": [
              { "exists": { "field": "{{EsIndexNames.StepIdFieldPath}}" } },
              { "exists": { "field": "{{EsIndexNames.ExecutionIdFieldPath}}" } },
              { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": {
                  "gte": "{{windowStart:o}}", "lte": "{{snapshot:o}}" } } }
            ],
            "must_not": [
              { "exists": { "field": "{{EsIndexNames.ReinjectOutcomeFieldPath}}" } }
            ]
          }
        },
        "sort": [ { "{{EsIndexNames.WindowTimestampFieldPath}}": "asc" } ]
      }
      """;

    /// <summary>
    /// Build the window-bounded FW-02 DISPATCH <c>_search</c> body (Phase 76, ANL-02/ANL-03): a STATIC
    /// raw-string template filtering on the orchestrator fan-out edge record's
    /// <see cref="EsIndexNames.NextStepIdFieldPath"/> (<c>attributes.NextStepId</c>) PLUS
    /// <c>exists attributes.ExecutionId</c> — the latter excludes the "Dangling next-step id" diagnostic log
    /// (which carries <c>{NextStepId}</c> but no <c>{ExecutionId}</c>/<c>{CorrelationId}</c>). The set of
    /// <c>NextStepId</c>s per <c>(corr, exec)</c> is the ES-derived EXPECTED set (ANL-02); each record's inbound
    /// <c>attributes.EntryId</c> (= M_N) is the ANL-03 framework-redundancy evidence. NO <c>.keyword</c>
    /// (Pitfall 2). Size-bounded to 2000, sorted ascending on the window timestamp field.
    /// </summary>
    private static string BuildDispatchSearchBody(DateTimeOffset windowStart, DateTimeOffset snapshot) => $$"""
      {
        "size": 2000,
        "query": {
          "bool": {
            "filter": [
              { "exists": { "field": "{{EsIndexNames.NextStepIdFieldPath}}" } },
              { "exists": { "field": "{{EsIndexNames.ExecutionIdFieldPath}}" } },
              { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": {
                  "gte": "{{windowStart:o}}", "lte": "{{snapshot:o}}" } } }
            ]
          }
        },
        "sort": [ { "{{EsIndexNames.WindowTimestampFieldPath}}": "asc" } ]
      }
      """;

    /// <summary>
    /// Poll <see cref="ElasticsearchTestClient.SearchAllHits"/> over the window until the returned hit
    /// count is unchanged across two consecutive polls AND the count is non-zero (D-05 poll-to-stable).
    /// This prevents scoring an in-flight run as MISSING.
    /// <para>
    /// Stability requires a non-zero count: a transient empty result (ES 404 lazy-index, backend blip)
    /// returning <c>0 == 0</c> across two polls would be incorrectly accepted as stable, scoring an empty
    /// cohort → a spurious Fail on a backend hiccup rather than a real defect.
    /// If the window genuinely contains zero runs (e.g. no dispatches fired), the loop exhausts its
    /// budget and returns the empty list — the precondition assert (WR-04) then surfaces the root cause.
    /// </para>
    /// <para>Bounded by <see cref="PollToStableBudgetMs"/> total wall-clock (separate from
    /// <see cref="DrainMs"/> — see WR-03).</para>
    /// </summary>
    private static async Task<List<JsonElement>> PollHitsToStableAsync(
        ElasticsearchTestClient es, DateTimeOffset windowStart, DateTimeOffset snapshot, CancellationToken ct)
    {
        var body = BuildStepSearchBody(windowStart, snapshot);
        var last = await es.SearchAllHits(body, ct: ct);
        var deadline = DateTime.UtcNow.AddMilliseconds(PollToStableBudgetMs);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(PollToStableMs, ct);
            var current = await es.SearchAllHits(body, ct: ct);
            if (current.Count == last.Count && current.Count > 0)
            {
                // stable across two polls AND we actually have hits — safe to score.
                // Requiring Count > 0 prevents a transient empty ES result (404 lazy-index,
                // backend blip) from being accepted as "stable" and producing a spurious
                // FAIL on an empty cohort. (WR-02 fix — 66-REVIEW.md.)
                return current;
            }
            last = current;
        }
        return last;
    }

    // ── Keeper-outcome ES read → per-(corr,exec) map (D75-4 live join) ───────────────────────────────

    /// <summary>
    /// Build the window-bounded keeper-REINJECT-outcome <c>_search</c> body (D75-4): a STATIC raw-string
    /// template (T-66-08 / T-75-07) mirroring <see cref="BuildStepSearchBody"/> — only the Wave-0-verified
    /// field paths from <see cref="EsIndexNames"/> consts (NEVER a <c>.keyword</c> sub-field) and the
    /// validated window timestamps are interpolated, so there is no injection surface. Filters on the
    /// EXISTENCE of the Plan-02 discriminator <see cref="EsIndexNames.ReinjectOutcomeFieldPath"/>
    /// (<c>attributes.ReinjectOutcome</c> ∈ {<c>"drop"</c>,<c>"reinject"</c>}) PLUS the same
    /// <c>[windowStart, snapshot]</c> range as the Step_* query, size-bounded to 2000 and sorted ascending
    /// on the window timestamp field. The keeper docs ride the SAME OTLP pipeline as the Step_* docs, so the
    /// existing <see cref="DrainMs"/> + poll-to-stable already tolerates the ~60 s export skew.
    /// </summary>
    private static string BuildKeeperOutcomeSearchBody(DateTimeOffset windowStart, DateTimeOffset snapshot) => $$"""
      {
        "size": 2000,
        "query": {
          "bool": {
            "filter": [
              { "exists": { "field": "{{EsIndexNames.ReinjectOutcomeFieldPath}}" } },
              { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": {
                  "gte": "{{windowStart:o}}", "lte": "{{snapshot:o}}" } } }
            ]
          }
        },
        "sort": [ { "{{EsIndexNames.WindowTimestampFieldPath}}": "asc" } ]
      }
      """;

    /// <summary>
    /// Build the per-<c>(correlationId, executionId)</c> → keeper-outcome map from raw keeper ES hits
    /// (D75-4), mirroring the <see cref="BuildRunTraces"/> defensive-read idiom (T-66-09 / T-75-07): each
    /// attribute is read via <c>attrs.TryGetProperty(... ValueKind == String)</c> and an odd-shaped hit is
    /// skipped with <c>continue</c> — never thrown on. The output dict is keyed <c>$"{corr}|{exec}"</c>
    /// (StringComparer.Ordinal — the same key shape as <see cref="TraceCohort.FirstHopUtcByExecution"/> and
    /// the engine's <c>keeperOutcomeByExecution</c> param) → the <c>attributes.ReinjectOutcome</c> string.
    /// <para>
    /// <b>Tie-break: <c>"reinject"</c> wins.</b> If BOTH a <c>"drop"</c> and a <c>"reinject"</c> doc exist for
    /// the same key (the keeper dropped, then a later reinject succeeded — or vice versa), the map keeps
    /// <c>"reinject"</c>: a confirmed reinject means the data WAS recoverable, so the classifier must NOT
    /// tolerate that execution as a provably-unrecoverable clean drop. This is the join half of Plan 01's
    /// classifier contract (a recovered execution is a binding requirement, never a tolerated loss).
    /// </para>
    /// <para>
    /// <b>ES-read-only (fixture invariant, :44-47).</b> The map is derived SOLELY from keeper logs in ES —
    /// there is NO live Redis probe (<c>IDatabase</c>/<c>StringLength</c>) here; keeper recovery evidence
    /// reaches the verdict only through the keeper's own structured logs.
    /// </para>
    /// </summary>
    // WR-02: internal (not private) so the hermetic BuildKeeperOutcomeMapFacts can pin the "reinject"-wins
    // tie-break for BOTH doc orderings by feeding synthetic hit JSON — this class is Category=RealStack
    // (compile-gated in the Docker-less sandbox), so its order-independence invariant is proven hermetically
    // in a separate non-RealStack fact class rather than only implicitly via the engine facts.
    internal static IReadOnlyDictionary<string, string> BuildKeeperOutcomeMap(List<JsonElement> hits)
    {
        var outcomeByExecution = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var hit in hits)
        {
            if (!hit.TryGetProperty("_source", out var source)) continue;
            if (!source.TryGetProperty("attributes", out var attrs)) continue;

            if (!attrs.TryGetProperty("CorrelationId", out var corrEl)
                || corrEl.ValueKind != JsonValueKind.String) continue;
            if (!attrs.TryGetProperty("ExecutionId", out var execEl)
                || execEl.ValueKind != JsonValueKind.String) continue;
            if (!attrs.TryGetProperty("ReinjectOutcome", out var outcomeEl)
                || outcomeEl.ValueKind != JsonValueKind.String) continue;

            var key = $"{corrEl.GetString()!}|{execEl.GetString()!}";
            var outcome = outcomeEl.GetString()!;

            // Tie-break — "reinject" wins over "drop": a recovered execution must never be mistaken for a
            // clean drop (D75-4). Once a key is "reinject" it is never downgraded; a "drop" only lands if the
            // key is absent or already "drop".
            if (outcomeByExecution.TryGetValue(key, out var existing) && existing == "reinject") continue;
            outcomeByExecution[key] = outcome;
        }

        return outcomeByExecution;
    }

    /// <summary>
    /// The per-window trace cohort + the value-chain/trip-duration evidence the engine needs (73, D-11/D-12):
    /// the per-instance <see cref="RunTrace"/>s (each carrying its per-label <c>attributes.Produced</c>
    /// values), plus the trip-duration maps computed from the ES <c>@timestamp</c> span and the per-execution
    /// seed map recovered from the chain. All derived from the SAME already-pulled, time-sorted hits — NO new
    /// ES query (D-12).
    /// </summary>
    private sealed record TraceCohort
    {
        public required IReadOnlyList<RunTrace> Traces { get; init; }
        public required IReadOnlyDictionary<string, double> TripDurationMsByExecution { get; init; } // keyed "corr|exec"
        public required IReadOnlyDictionary<string, double> TripDurationMsByCorrelation { get; init; } // keyed "corr"

        // B-criterion (Plan 3): per-(corr,exec) first/last hop ES @timestamp, keyed "corr|exec" — the MIN and
        // MAX of the same trip-duration span. The engine uses these vs RECOVERY_UTC to classify a started-but-
        // incomplete run as a tolerated in-flight-at-wipe loss (last hop < recovery) vs a binding post-recovery
        // miss. Derived from the SAME hits as the trip-duration span (no new ES query).
        public required IReadOnlyDictionary<string, DateTimeOffset> FirstHopUtcByExecution { get; init; } // keyed "corr|exec"
        public required IReadOnlyDictionary<string, DateTimeOffset> LastHopUtcByExecution { get; init; }  // keyed "corr|exec"

        // D75-4 live join: per-(corr,exec) keeper REINJECT outcome ("drop"|"reinject"), keyed "corr|exec" —
        // built from the keeper's structured drop/success logs in ES (attributes.ReinjectOutcome), NEVER a
        // live Redis probe. Fed into PassFailEngine.Analyze(keeperOutcomeByExecution:) so a keeper clean-
        // absent DROP is tolerated (provably-unrecoverable) and a recoverable-but-lost execution is a
        // binding FAIL. "reinject" wins any tie (see BuildKeeperOutcomeMap).
        public required IReadOnlyDictionary<string, string> KeeperOutcomeByExecution { get; init; } // keyed "corr|exec"

        // Phase 76 ANL-02: the ES-derived EXPECTED stepId set per (corr,exec), keyed "corr|exec" — the set of
        // orchestrator FW-02 fan-out NextStepIds dispatched for the execution. A dispatched stepId absent from
        // the observed processor records (DistinctStepIds) is a binding miss (dispatched-but-never-executed,
        // TEST-08). Derived from ES FW-02 records ONLY — no Postgres/graph read (T-76-07).
        public required IReadOnlyDictionary<string, IReadOnlySet<string>> ExpectedStepIdsByExecution { get; init; }

        // Phase 76 ANL-03: the stepIds proven-to-have-run by orchestrator redundancy per (corr,exec), keyed
        // "corr|exec" — a missing processor record is reconciled as a NON-binding telemetry gap (no seed oracle)
        // when the hop is in this set. Populated from the terminal-reached records (which name the terminal
        // StepId directly) plus the fan-out records whose inbound EntryId (M_N) resolves to a producer stepId via
        // the present processor records' MessageId→StepId map. Derived from ES framework records ONLY.
        public required IReadOnlyDictionary<string, IReadOnlySet<string>> OrchestratorConsumedStepIdsByExecution { get; init; }
    }

    /// <summary>
    /// Re-keyed onto the framework <c>attributes.StepId</c> records (Phase 76, ANL-01/D-15). Groups the
    /// STRUCTURAL hits by the <c>(CorrelationId, ExecutionId)</c> composite into per-INSTANCE
    /// <see cref="RunTrace"/>s whose completeness set is <see cref="RunTrace.DistinctStepIds"/> (the processor
    /// "did-run" records, discriminated by the presence of <c>attributes.MessageId</c>). The co-located
    /// orchestrator terminal-reached records (<c>attributes.StepId</c> + <c>attributes.WorkflowId</c>, NO
    /// MessageId) are NOT counted as observed hops — they feed the ANL-03 orchestrator-redundancy proven set and
    /// supply the convergent terminal stepId. The FW-02 <paramref name="dispatchHits"/> supply the ES-derived
    /// expected set (ANL-02) + the inbound-EntryId redundancy edge (ANL-03). (D-03/Phase 78: the concrete value
    /// oracle is deleted — no <c>StepLabel</c>/<c>Produced</c> value axis is built; <c>RunTrace.FromStepIds</c>
    /// defaults the value/label maps empty, so the value chain is permanently N/A.) Trip-duration/first/last-hop
    /// spans come from the structural hits' <c>@timestamp</c>. Every read is defensive (T-66-09 — odd-shaped
    /// JSON is dropped, never thrown).
    /// </summary>
    private static TraceCohort BuildRunTraces(
        List<JsonElement> structuralHits, List<JsonElement> dispatchHits,
        IReadOnlyDictionary<string, string> keeperOutcomeByExecution)
    {
        // ── STRUCTURAL: observed processor stepIds + terminal-reached redundancy, keyed (corr, exec) ──
        var stepIdsByInstance = new Dictionary<(string Corr, string Exec), List<string>>();
        var spanByInstance = new Dictionary<(string Corr, string Exec), (DateTimeOffset Min, DateTimeOffset Max)>();
        var spanByCorrelation = new Dictionary<string, (DateTimeOffset Min, DateTimeOffset Max)>();
        // MessageId (M_N) → producer StepId, from PRESENT processor records — the ANL-03 EntryId→stepId resolver.
        var stepIdByMessageId = new Dictionary<string, string>(StringComparer.Ordinal);
        // Orchestrator-proven "did run" stepIds per (corr,exec): terminal-reached records name the StepId directly.
        var proven = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // The convergent terminal stepId is derived from the FW-02 DISPATCH edges below (dispatch in-degree),
        // NOT from a terminal-reached structural bucket — see BuildRunTraces' convergent derivation and the
        // note in the no-MessageId branch for why the structural bucket cannot be a clean terminal-reached set.

        foreach (var hit in structuralHits)
        {
            if (!hit.TryGetProperty("_source", out var source)) continue;
            if (!source.TryGetProperty("attributes", out var attrs)) continue;
            if (!attrs.TryGetProperty("CorrelationId", out var corrEl) || corrEl.ValueKind != JsonValueKind.String) continue;
            if (!attrs.TryGetProperty("ExecutionId", out var execEl) || execEl.ValueKind != JsonValueKind.String) continue;
            if (!attrs.TryGetProperty("StepId", out var stepEl) || stepEl.ValueKind != JsonValueKind.String) continue;

            var correlationId = corrEl.GetString()!;
            var executionId = execEl.GetString()!;
            var stepId = stepEl.GetString()!;

            // Discriminate the two co-located StepId shapes: the processor per-hop "did-run" record carries
            // attributes.MessageId; the orchestrator terminal-reached record carries attributes.WorkflowId (no
            // MessageId). Only the former is an OBSERVED hop; the latter is orchestrator redundancy evidence.
            var hasMessageId = attrs.TryGetProperty("MessageId", out var msgEl) && msgEl.ValueKind == JsonValueKind.String;

            if (!hasMessageId)
            {
                // Terminal-reached record (D-12): the named StepId provably reached/ran → ANL-03 redundancy.
                // NOTE (Phase 76 live-gate fix): OTel IncludeScopes stamps attributes.StepId (the CONSUMED step,
                // from the InboundExecutionScopeConsumeFilter) onto EVERY log emitted during a consume — so this
                // no-MessageId bucket is NOT a clean terminal-reached set: it also contains the orchestrator
                // fan-out records (which additionally carry NextStepId), the sample's author value logs
                // ("Step_X received/produced", which carry StepLabel), and orchestrator business logs
                // ("Trip ended ..."). The convergent-terminal derivation therefore no longer reads this bucket
                // (it uses the dispatch in-degree below). The proven-set contribution is retained UNCHANGED:
                // over-population of `proven` can only reconcile a would-be-missing hop as non-binding — it can
                // mask loss (a separate, fault-scenario-verified concern), never manufacture it, so it cannot
                // produce the false FAIL this fix targets.
                var pkey = $"{correlationId}|{executionId}";
                (proven.TryGetValue(pkey, out var pset) ? pset : proven[pkey] = new(StringComparer.Ordinal)).Add(stepId);
                continue;
            }

            var key = (correlationId, executionId);
            (stepIdsByInstance.TryGetValue(key, out var steps) ? steps : stepIdsByInstance[key] = new()).Add(stepId);
            stepIdByMessageId[msgEl.GetString()!] = stepId;

            if (TryReadTimestamp(source, out var ts))
            {
                spanByInstance[key] = spanByInstance.TryGetValue(key, out var s)
                    ? (s.Min < ts ? s.Min : ts, s.Max > ts ? s.Max : ts) : (ts, ts);
                spanByCorrelation[correlationId] = spanByCorrelation.TryGetValue(correlationId, out var c)
                    ? (c.Min < ts ? c.Min : ts, c.Max > ts ? c.Max : ts) : (ts, ts);
            }
        }

        // ── FW-02 DISPATCH: expected set (NextStepId) + orchestrator-redundancy edge (inbound EntryId → stepId) ──
        var expectedByExec = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        // Phase 76 live-gate fix: per-(corr,exec) dispatch in-degree per NextStepId. The convergent fan-in
        // terminal is the unique NextStepId dispatched-to more than once (F1→G and F2→G), derived from these
        // positively-keyed fan-out records instead of the scope-polluted terminal-reached structural bucket.
        var nextTargetCountByInstance = new Dictionary<(string Corr, string Exec), Dictionary<string, int>>();
        foreach (var hit in dispatchHits)
        {
            if (!hit.TryGetProperty("_source", out var source)) continue;
            if (!source.TryGetProperty("attributes", out var attrs)) continue;
            if (!attrs.TryGetProperty("CorrelationId", out var corrEl) || corrEl.ValueKind != JsonValueKind.String) continue;
            if (!attrs.TryGetProperty("ExecutionId", out var execEl) || execEl.ValueKind != JsonValueKind.String) continue;
            if (!attrs.TryGetProperty("NextStepId", out var nextEl) || nextEl.ValueKind != JsonValueKind.String) continue;

            var key = $"{corrEl.GetString()!}|{execEl.GetString()!}";
            // ANL-02: the dispatched next step is EXPECTED to run.
            (expectedByExec.TryGetValue(key, out var eset) ? eset : expectedByExec[key] = new(StringComparer.Ordinal)).Add(nextEl.GetString()!);

            // Convergent-terminal in-degree: count how many fan-out edges TARGET each NextStepId per (corr,exec).
            var ikey = (corrEl.GetString()!, execEl.GetString()!);
            var next = nextEl.GetString()!;
            var counts = nextTargetCountByInstance.TryGetValue(ikey, out var cm) ? cm : (nextTargetCountByInstance[ikey] = new(StringComparer.Ordinal));
            counts[next] = counts.TryGetValue(next, out var cc) ? cc + 1 : 1;

            // ANL-03: the inbound EntryId (M_N) proves the PRODUCER hop ran. Resolve M_N → producer stepId via
            // the present processor records' MessageId→StepId map (best-effort — a fully-dropped producer whose
            // MessageId never surfaced cannot be resolved here; that hop stays a binding miss, which is correct).
            if (attrs.TryGetProperty("EntryId", out var entryEl) && entryEl.ValueKind == JsonValueKind.String
                && stepIdByMessageId.TryGetValue(entryEl.GetString()!, out var producerStep))
            {
                (proven.TryGetValue(key, out var pset) ? pset : proven[key] = new(StringComparer.Ordinal)).Add(producerStep);
            }
        }

        // ── Build the per-instance RunTraces (structural stepIds only) ──
        // D-03/Phase 78: the value-oracle axis is deleted — no StepLabel→Produced value map is built. Each
        // RunTrace carries only its structural stepIds; RunTrace.FromStepIds defaults its value/label maps
        // empty, so the value chain is permanently not-applicable.
        var traces = stepIdsByInstance.Select(kv =>
        {
            // Convergent fan-in terminal = the unique NextStepId dispatched-to more than once for this
            // (corr,exec) (the ×2 per-arrival fan-in target, e.g. Step_G reached from both Step_F1 and Step_F2).
            // This equals the processor's per-arrival ×2 stepId, so RunTrace.FromStepIds exempts exactly that
            // stepId at ConvergentExpectedMultiplicity. Not exactly one such target ⇒ null (fail-closed).
            string? convergent = null;
            if (nextTargetCountByInstance.TryGetValue(kv.Key, out var targetCounts))
            {
                var fanIn = targetCounts.Where(t => t.Value > 1).Select(t => t.Key).ToList();
                convergent = fanIn.Count == 1 ? fanIn[0] : null;
            }
            return RunTrace.FromStepIds(kv.Key.Corr, kv.Key.Exec, kv.Value, convergentStepId: convergent);
        }).ToList();

        var tripByExec = spanByInstance.ToDictionary(
            kv => $"{kv.Key.Corr}|{kv.Key.Exec}", kv => (kv.Value.Max - kv.Value.Min).TotalMilliseconds, StringComparer.Ordinal);
        var tripByCorr = spanByCorrelation.ToDictionary(
            kv => kv.Key, kv => (kv.Value.Max - kv.Value.Min).TotalMilliseconds, StringComparer.Ordinal);
        var firstHopByExec = spanByInstance.ToDictionary(
            kv => $"{kv.Key.Corr}|{kv.Key.Exec}", kv => kv.Value.Min, StringComparer.Ordinal);
        var lastHopByExec = spanByInstance.ToDictionary(
            kv => $"{kv.Key.Corr}|{kv.Key.Exec}", kv => kv.Value.Max, StringComparer.Ordinal);

        return new TraceCohort
        {
            Traces = traces,
            TripDurationMsByExecution = tripByExec,
            TripDurationMsByCorrelation = tripByCorr,
            FirstHopUtcByExecution = firstHopByExec,
            LastHopUtcByExecution = lastHopByExec,
            KeeperOutcomeByExecution = keeperOutcomeByExecution,
            ExpectedStepIdsByExecution = expectedByExec.ToDictionary(
                kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
            OrchestratorConsumedStepIdsByExecution = proven.ToDictionary(
                kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Defensive top-level <c>_source.@timestamp</c> read (73, D-12; T-73-07) for the trip-duration span.
    /// The Step_* search sorts ASC on <see cref="EsIndexNames.WindowTimestampFieldPath"/> (<c>@timestamp</c>),
    /// so reusing the same field here keeps the trip-duration cohort identical to the scored hits — NO new ES
    /// query. Parses as a UTC <see cref="DateTimeOffset"/>; an odd-shaped/missing/unparseable value is dropped
    /// (returns false), never thrown on.
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
            // LIVE shape (phase-73 harness, verified against the live ES _source): the OTLP -> ES bridge
            // serializes @timestamp as epoch MILLISECONDS rendered as a NUMERIC STRING with a sub-ms fraction
            // (e.g. "1781754330089.184100") — NOT an ISO-8601 datetime. DateTimeOffset.TryParse REJECTS that
            // bare numeric string, which is why the trip-duration maps came back empty on the live run. Parse
            // it as epoch-ms FIRST; the fractional sub-millisecond part is irrelevant to a min->max span and
            // is truncated by the (long) cast. (A genuine ISO-8601 string is non-numeric, so it falls through
            // to the TryParse fallback below.)
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

    // ── Prometheus windowed-delta counter set (OBS-03) ───────────────────────────────────────────────

    /// <summary>
    /// The raw counter values read at one snapshot point. The fixture takes two (before/after) and
    /// subtracts to get the WINDOWED DELTAS the engine reconciles (A3 — counters are cumulative).
    /// Phase 74 uniform two-counter model: only the four <c>{service}_messages_consumed/_sent</c> totals
    /// (the legacy per-type names, the three removed dedup/drop counters, and the processor <c>outcome</c>
    /// breakdown are gone).
    /// </summary>
    private sealed record CounterSet
    {
        public required double OrchestratorMessagesSent { get; init; }
        public required double OrchestratorMessagesConsumed { get; init; }
        public required double ProcessorMessagesConsumed { get; init; }
        public required double ProcessorMessagesSent { get; init; }

        // Phase 74+ metric gate (MG-2): keeper consumed/sent totals, read as windowed deltas (after − before)
        // exactly like the four uniform counters above.
        public required double KeeperMessagesConsumed { get; init; }
        public required double KeeperMessagesSent { get; init; }

        // Phase 74+ metric gate (MG-3): the keeper BIT probe CADENCE as an INSTANT rate(...[2m]) sample read at
        // this snapshot instant (NOT a windowed delta — it is already a per-second rate). The before-set's value
        // is unused; BuildSnapshot takes the after-set's (windowEnd) rate.
        public required double KeeperL2ProbeRate { get; init; }
    }

    /// <summary>
    /// Read the counter set once. Counters sum across all label combinations (no processorId filter —
    /// the analyzer reconciles the whole window). Phase 74 (D-13): only the four uniform
    /// <c>{service}_messages_consumed/_sent</c> totals are read; the processor <c>outcome</c> breakdown and
    /// the three removed dedup/drop counters are gone.
    /// <para>
    /// <paramref name="evalTime"/> (67-03 / OBS-04): when non-null every counter is read via an INSTANT
    /// query pinned to that instant (delta@windowEnd − delta@windowStart aligns the trigger denominator
    /// with the ES cohort). When <c>null</c> (standalone Phase 66) every read is the live "now" query,
    /// byte-for-byte the original behavior.
    /// </para>
    /// </summary>
    private static async Task<CounterSet> ReadCounterSetAsync(
        PrometheusTestClient prom, CancellationToken ct, DateTimeOffset? evalTime = null)
    {
        return new CounterSet
        {
            OrchestratorMessagesSent = await SumOrZeroAsync(prom, LiveMetricNames.OrchestratorMessagesSentTotal, ct, evalTime),
            OrchestratorMessagesConsumed = await SumOrZeroAsync(prom, LiveMetricNames.OrchestratorMessagesConsumedTotal, ct, evalTime),
            ProcessorMessagesConsumed = await SumOrZeroAsync(prom, LiveMetricNames.ProcessorMessagesConsumedTotal, ct, evalTime),
            ProcessorMessagesSent = await SumOrZeroAsync(prom, LiveMetricNames.ProcessorMessagesSentTotal, ct, evalTime),

            // MG-2: keeper consumed/sent totals — same windowed-delta pattern as the four uniform counters.
            KeeperMessagesConsumed = await SumOrZeroAsync(prom, LiveMetricNames.KeeperMessagesConsumedTotal, ct, evalTime),
            KeeperMessagesSent = await SumOrZeroAsync(prom, LiveMetricNames.KeeperMessagesSentTotal, ct, evalTime),

            // MG-3: keeper L2 probe CADENCE as an instant rate(...[2m]) query, pinned to this snapshot instant
            // (evalTime) via the existing PrometheusTestClient.QueryPrometheus time= seam — NOT a before/after
            // delta (it is already a per-second rate). A 2m window comfortably spans the keeper's probe cadence.
            // SumOrZeroAsync sums the rate vector (one series per keeper instance) and yields 0 for an absent
            // series (probe dead / keeper down).
            KeeperL2ProbeRate = await SumOrZeroAsync(
                prom, $"rate({LiveMetricNames.KeeperL2ProbeTotal}[2m])", ct, evalTime),
        };
    }

    /// <summary>Sum the series value (0 for an absent/empty vector — a counter just hasn't moved). When
    /// <paramref name="evalTime"/> is non-null the value is read as of that instant (67-03 / OBS-04).</summary>
    private static async Task<double> SumOrZeroAsync(
        PrometheusTestClient prom, string promql, CancellationToken ct, DateTimeOffset? evalTime = null)
        => PrometheusTestClient.SumSampleValues(await prom.QueryPrometheus(promql, ct, evalTime));

    /// <summary>
    /// Build the <see cref="PromCounterSnapshot"/> from the before/after counter sets as WINDOWED DELTAS
    /// (after − before) for the four uniform Phase-74 counters (D-12: processor_messages_sent total is the
    /// all-complete close-gate proxy for the old completed breakdown).
    /// </summary>
    private static PromCounterSnapshot BuildSnapshot(CounterSet before, CounterSet after)
        => new PromCounterSnapshot
        {
            OrchestratorMessagesSentDelta = after.OrchestratorMessagesSent - before.OrchestratorMessagesSent,
            OrchestratorMessagesConsumedDelta = after.OrchestratorMessagesConsumed - before.OrchestratorMessagesConsumed,
            ProcessorMessagesConsumedDelta = after.ProcessorMessagesConsumed - before.ProcessorMessagesConsumed,
            ProcessorMessagesSentDelta = after.ProcessorMessagesSent - before.ProcessorMessagesSent,

            // MG-1: the CLEAN windowEnd-cumulative reads MG-1 conservation uses (the windowed-delta baseline is
            // corrupted by --force-recreate lingering pre-recreate series in Prom's ~5-min lookback).
            OrchestratorMessagesConsumedAtEnd = after.OrchestratorMessagesConsumed,
            ProcessorMessagesSentAtEnd = after.ProcessorMessagesSent,

            // MG-2: keeper consumed/sent as windowed deltas (after − before), same shape as the four above.
            KeeperMessagesConsumedDelta = after.KeeperMessagesConsumed - before.KeeperMessagesConsumed,
            KeeperMessagesSentDelta = after.KeeperMessagesSent - before.KeeperMessagesSent,

            // MG-3: probe rate is an INSTANT cadence, not a delta — take the after-set's (windowEnd) rate sample.
            KeeperL2ProbeRate = after.KeeperL2ProbeRate,
        };
}
