# Phase 83: Orchestrator HA failover proof - Pattern Map

**Mapped:** 2026-07-18
**Files analyzed:** 5 (4 new + 1 shared-lib consumer)
**Analogs found:** 5 / 5 (all exact or role-match with a live-verified sibling)

This is a **live-proof / harness phase — NO product-code change**. Every new file clones a
byte-verified in-repo analog and swaps only the scoring/perturbation logic. The three non-obvious
traps (the `@timestamp` epoch-ms-string parser, the `.keyword`-free `EsIndexNames` paths, the
`Resolve-AnalyzerExitCode` dot-source) are each solved verbatim in the analogs below — clone, do not
re-invent.

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `scripts/phase-83-ha-failover.ps1` | harness/script (ops sequencer + verdict driver) | event-driven (perturbation) + request-response | `scripts/phase-80-harness.ps1` | exact (STEP A0..Z primitives) |
| `tests/BaseApi.Tests/Observability/HaFailoverAnalyzerE2ETests.cs` | test (RealStack E2E verdict) | batch / request-response (ES fetch → score → write report → assert) | `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` | exact (same ES/window/report scaffold) |
| `tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorer.cs` | utility (pure scorer) | transform (records → buckets → verdict) | `tests/BaseApi.Tests/Observability/Analysis/StructuralCohort.cs` | exact (pure static classifier idiom) |
| `tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorerFacts.cs` | test (hermetic unit) | transform (synthetic hits → assert) | `tests/BaseApi.Tests/Observability/Analysis/StructuralCohortFacts.cs` | exact (synthetic-record fact class) |
| `scripts/phase-83-rollup.ps1` (+ `analyzer-reports/phase-83-summary.json`) — OPTIONAL, discretionary | harness/script (roll-up) | batch | `scripts/phase-81-sweep.ps1` roll-up + `analyzer-reports/phase-81-summary.json` | role-match |

---

## Pattern Assignments

### `scripts/phase-83-ha-failover.ps1` (harness, perturbation + verdict driver)

**Analog:** `scripts/phase-80-harness.ps1` (STEP primitives, exit-code table, `Write-Phase`, exit-code-resolution dot-source). Reuse STEP A0/A/A2/B/B1/C/D/E **verbatim**; the only new code is the STEP F leader-kill sequencer and the STEP H env seam pointed at the new verdict fact.

**Preamble + Write-Phase + exit-code dot-source** (`phase-80-harness.ps1:93-112`) — copy exactly, change only the log prefix:
```powershell
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-83-ha-failover] $msg" -ForegroundColor $color
    }
    # dot-source the shared verdict->exit resolution lib (pure, no side effects)
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')
    # ... STEP A0..Z ...
} finally {
    Pop-Location
}
```

**Reuse-verbatim bring-up chain** — these blocks are copied unchanged from `phase-80-harness.ps1`:
- STEP A0/A build+up behind `-SkipBringUp` (`:163-180`)
- STEP A2 fresh-DB processor-row bootstrap (`:195-222`) — **keep**, the fresh-PVC heal-wait depends on it
- STEP B reset (`:249-251`)
- STEP B1 clean-orchestrator rollout-restart — **critical for HA**: wipes ghost Quartz crons AND forces a fresh election so the leader is deterministic before STEP F (`:263-268`)
- STEP C seed `~FanOutSeeder` (`:274-276`), STEP D wf-id psql (`:283-293`), STEP E POST /start require 204 (`:299-308`)
- STEP Z port-forward teardown (`:524-552`)

STEP B0 (processor counter-baseline restart, `:237-242`) is **not needed** (no Prom/MG-1 in this proof) — omit per RESEARCH "Recommended harness structure".

**kubectl house style — namespace-scoped `-n skp`, jsonpath read, exit-code-pinned-before-trim** (mirror the STEP D psql idiom at `phase-80-harness.ps1:284-293` and STEP A2 `:196-202`). The new STEP F.0 leader read:
```powershell
# F.0 — deterministic leader identity (holderIdentity == POD_NAME, D-02a)
$leaderRaw = kubectl get lease orchestrator-leader -n skp -o jsonpath='{.spec.holderIdentity}'
$leaderExit = $LASTEXITCODE                 # pin BEFORE the trim (STEP D/A2 fix: failed exec => empty stdout,
$leader = ("$leaderRaw").Trim()             #   .Trim() on the string-cast can't throw and bypass the exit code)
if ($leaderExit -ne 0 -or [string]::IsNullOrWhiteSpace($leader)) {
    Write-Phase "could not read Lease holderIdentity (kubectl exit $leaderExit). Aborting." 'Red'; exit 45
}
Write-Phase "current leader = $leader"
```
Note the crash sequencer at `phase-80-harness.ps1:380-421` uses `kubectl -n skp scale` and `kubectl -n skp get pods -l app=$tier` / `rollout status ... --timeout=120s` — the same `-n skp` namespace scoping and bounded-wait discipline the STEP F kill sequencer must follow. **But do NOT `kubectl scale`** (D-02a anti-pattern — k8s picks the victim); target the exact `holderIdentity` pod.

**STEP F kill sequencer (NEW)** — the only genuinely new PowerShell. Phase-aligned kill (RESEARCH Code Example 1 / Pitfall 1):
```powershell
# F.2 — phase-align: sleep to ~3s before the next :00/:30 boundary so the boundary tick lands in the gap
$now = [DateTimeOffset]::UtcNow
$secIntoHalfMin = ($now.Second % 30) + $now.Millisecond/1000.0
$sleepS = (30 - $secIntoHalfMin) - 3.0
if ($sleepS -lt 0) { $sleepS += 30 }
Start-Sleep -Seconds $sleepS

# F.3 — SIGKILL the leader; NO graceful lease release (D-02). Pin KILL_UTC at kubectl RETURN (Open Q1).
kubectl delete pod $leader -n skp --grace-period=0 --force | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Phase "force-delete of leader failed. Aborting." 'Red'; exit 55 }
$killUtc = [DateTimeOffset]::UtcNow
Write-Phase "KILL_UTC = $($killUtc.ToString('o')) (force-deleted $leader)"
```
The F.1 "wait for >=1 clean pre-kill fire" precondition (before the phase-aligned sleep) fails to `exit 60` if never observed — mirror the STEP F.2 observe-loop bounded-wait shape at `phase-80-harness.ps1:371-376`.

**STEP H verdict + exit-code resolution (env seam)** — clone `phase-80-harness.ps1:476-513` (STEP H) exactly, adding `KILL_UTC`, dropping `RECOVERY_UTC`/`K_EXECUTIONS`, and retargeting the MTP filter:
```powershell
$env:SCENARIO_ID      = 'phase-83-ha'
$env:WINDOW_START_UTC = $windowStart.ToString('o')
$env:WINDOW_END_UTC   = $windowEnd.ToString('o')
$env:KILL_UTC         = $killUtc.ToString('o')
try {
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release `
      -- --filter-method "*Ha_Failover_Window_Yields_Pass*" 2>&1 | Out-String | Write-Host
    $analyzerExit = $LASTEXITCODE
} finally {
    Remove-Item Env:SCENARIO_ID, Env:WINDOW_START_UTC, Env:WINDOW_END_UTC, Env:KILL_UTC -ErrorAction SilentlyContinue
}
# authoritative verdict from the report JSON (written BEFORE the assert), mapped 0/1/2
$report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter 'phase-83-ha.json' `
          -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
if ($report) {
    $analyzerExit = Resolve-AnalyzerExitCode (Get-Content $report.FullName -Raw | ConvertFrom-Json)
}
$verdictClass = (Resolve-SweepClass $analyzerExit).Class
Write-Phase "HA verdict exit = $analyzerExit ($verdictClass; 0=PASS 1=FAIL 2=INCONCLUSIVE)" $(if ($analyzerExit -eq 0) { 'Green' } else { 'Yellow' })
exit $analyzerExit
```

**Exit-code table** (mirror `phase-80-harness.ps1:39-51` header comment; the 0/1/2 classes are OWNED by `exit-code-resolution.ps1` — see Shared Patterns). New infra codes per RESEARCH "Suggested exit-code table":
```
0   HA verdict PASS      (Resolve-AnalyzerExitCode Pass)
1   HA verdict FAIL      (Resolve-AnalyzerExitCode Fail / unknown fail-closed)
2   HA verdict INCONCLUSIVE (Resolve-AnalyzerExitCode Inconclusive — e.g. gap tick never landed)
10  bring-up failed        25  orchestrator clean rollout-restart failed
20  reset failed           30  seeder failed        40  wf-id lookup failed/empty
45  Lease holderIdentity read failed/empty   (NEW)
50  activation gate != 204
55  force-delete of leader pod failed        (NEW)
60  pre-kill clean-fire never observed       (NEW)
```

---

### `tests/BaseApi.Tests/Observability/HaFailoverAnalyzerE2ETests.cs` (test, RealStack E2E verdict)

**Analog:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`. Keep the ES-fetch/window/drain/report plumbing byte-for-byte; **swap only the scoring** (feed the new `HaFireBucketScorer` instead of `PassFailEngine`).

**Traits + collection** (`AnalyzerE2ETests.cs:49-52`) — copy exactly (MTP `Category=RealStack` gating):
```csharp
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class HaFailoverAnalyzerE2ETests
```
MTP filter to invoke it: `dotnet test ... -- --filter-method "*Ha_Failover_Window_Yields_Pass*"` (VSTest `--filter` is silently ignored under MTP — `phase-80-harness.ps1:62-65`).

**Scenario-id path-traversal guard** (`AnalyzerE2ETests.cs:66-68,132-138`) — reuse the exact whitelist BEFORE composing any report path (ASVS V5 / T-66-07):
```csharp
private static readonly Regex ScenarioIdPattern = new(@"^[A-Za-z0-9_-]+$", RegexOptions.Compiled);
// ...
Assert.True(ScenarioIdPattern.IsMatch(scenarioId), $"scenarioId '{scenarioId}' must match ^[A-Za-z0-9_-]+$ ...");
var reportsDir = Path.Combine(AppContext.BaseDirectory, "analyzer-reports");
Directory.CreateDirectory(reportsDir);
var reportPath = Path.Combine(reportsDir, $"{scenarioId}.json");
```

**Env-seam parser (`TryParseUtc`)** (`AnalyzerE2ETests.cs:116-121`) — copy exactly; use it for `WINDOW_START_UTC`, `WINDOW_END_UTC`, and the new `KILL_UTC`:
```csharp
private static bool TryParseUtc(string? value, out DateTimeOffset parsed) =>
    DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
        out parsed);
```
Window-seam detection idiom (`AnalyzerE2ETests.cs:154-158`): `windowPinned = TryParseUtc(WINDOW_START_UTC,...) & TryParseUtc(WINDOW_END_UTC,...)`.

**Drain + poll-to-stable** (`AnalyzerE2ETests.cs:54-59,168,355-377`) — reuse `DrainMs = 60_000` + `PollHitsToStableAsync` before scoring (Pitfall 4). The hold+drain must cover `KILL_UTC + gap(<=17s) + one cron period(30s) + export(~60s)`.

**ES query builder — STATIC raw-string template, `EsIndexNames` consts, NO `.keyword`** (`AnalyzerE2ETests.cs:294-312` `BuildStepSearchBody`). Clone the shape for the **send-evidence** query (RESEARCH Code Example 2). The existing `BuildStepSearchBody` ALREADY fetches exactly the leader-fire set (`exists attributes.StepId` + `exists attributes.ExecutionId`), so the send stream can reuse it directly, or a trimmed variant keyed on `StepIdFieldPath` + `CorrelationIdFieldPath`:
```csharp
private static string BuildSendEvidenceBody(DateTimeOffset windowStart, DateTimeOffset windowEnd) => $$"""
  {
    "size": 2000,
    "query": { "bool": { "filter": [
      { "exists": { "field": "{{EsIndexNames.StepIdFieldPath}}" } },
      { "exists": { "field": "{{EsIndexNames.CorrelationIdFieldPath}}" } },
      { "range": { "{{EsIndexNames.WindowTimestampFieldPath}}": {
          "gte": "{{windowStart:o}}", "lte": "{{windowEnd:o}}" } } }
    ] } },
    "sort": [ { "{{EsIndexNames.WindowTimestampFieldPath}}": "asc" } ]
  }
  """;
```
The **role-flip** query (RESEARCH Code Example 5, claims #4/#5) — a `term` on `attributes.role` (a keyword, verified below) + a `range` gt on `KILL_UTC`. **Add a `RoleFieldPath` const to `EsIndexNames`** (single-const edit, matching the file's own "single-const edit" convention) — value `"attributes.role"`, NO `.keyword`. Wave-0 confirm the mapping is `keyword` (RESEARCH A5 / Open Q3) with `GET /logs-generic.otel-default/_mapping/field/attributes.role`.

**ES fetch call** (`AnalyzerE2ETests.cs:195-206`) uses `es.SearchAllHits(body, ct: ct)` — signature `Task<List<JsonElement>> SearchAllHits(string queryBody, string? indexPath = null, CancellationToken ct = default)` (`ElasticsearchTestClient.cs:135`). 404 lazy-index returns an empty list (`:145`) — that is why the caller polls to stable.

**`@timestamp` epoch-ms-string parser (`TryReadTimestamp`)** (`AnalyzerE2ETests.cs:627-663`) — **COPY VERBATIM**. This is the phase-73 trap RESEARCH flags: the OTLP→ES bridge writes `@timestamp` in `_source` as a numeric string like `"1781754330089.184100"`, which `DateTimeOffset.TryParse` REJECTS. The parser must try epoch-ms FIRST, ISO fallback second:
```csharp
if (tsEl.ValueKind == JsonValueKind.String)
{
    var raw = tsEl.GetString();
    // LIVE shape: epoch MILLISECONDS as a NUMERIC STRING with sub-ms fraction — parse as epoch-ms FIRST
    if (double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var epochMsStr))
    {
        timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)epochMsStr);
        return true;
    }
    return DateTimeOffset.TryParse(raw, /* ISO fallback */ ...);
}
```
Note: the ES `range` query filter interpolates ISO `{utc:o}` bounds on the INDEXED `@timestamp` (a `date` field, ES-queryable) — but the `_source` VALUE read back is the epoch-ms string, so the read path needs this parser even though the write/range path uses ISO (`EsIndexNames.cs:186-217`, RESEARCH "Don't Hand-Roll").

**Defensive attribute reads (T-66-09)** (`AnalyzerE2ETests.cs:436-459` `BuildKeeperOutcomeMap` / `674-701` `ParseStructuralRecords`) — the `hit -> _source -> attributes -> TryGetProperty(... ValueKind == String) else continue` idiom. The new fact's `TryReadCorr` reads `attributes.CorrelationId` the same way.

**Write-then-assert** (`AnalyzerE2ETests.cs:271-279`) — serialize+write the report JSON FIRST (so the artifact exists on a red run and the exit code matches it), THEN assert:
```csharp
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
await File.WriteAllTextAsync(reportPath, json, ct);              // FIRST — exists on red
Assert.True(report.Verdict == Verdict.Pass, report.HumanSummary); // THEN — FAIL => non-zero exit
```
The report object MUST carry a top-level `Verdict` serialized as its string name so `Resolve-AnalyzerExitCode` (see Shared Patterns) maps it — reuse the `Verdict` enum + `[JsonConverter(typeof(JsonStringEnumConverter))]` from `AnalyzerReport.cs:24-57,109-110`.

---

### `tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorer.cs` (utility, pure scorer)

**Analog:** `tests/BaseApi.Tests/Observability/Analysis/StructuralCohort.cs` — an `internal static class` with a single pure `Classify(...)` entry point over a small parsed-record struct, returning an immutable result record, living in the SHARED `BaseApi.Tests.Observability.Analysis` namespace so the live fixture AND the hermetic facts exercise the identical logic (RESEARCH Pattern 2). This is the exact seam to copy.

**Pure-classifier shape** (`StructuralCohort.cs:33-74,123-131`):
```csharp
namespace BaseApi.Tests.Observability.Analysis;

internal static class HaFireBucketScorer
{
    // Group send-records by correlationId, take each fire's earliest @timestamp, floor to 30s wall-clock.
    internal static long Bucket30s(DateTimeOffset ts) => ts.ToUnixTimeSeconds() - (ts.ToUnixTimeSeconds() % 30);

    internal static HaFireVerdict Score(
        IReadOnlyList<SendEvidenceRecord> sends,   // (CorrelationId, Timestamp) — from exists-StepId hits
        DateTimeOffset killUtc,
        DateTimeOffset? roleFlipUtc)                // earliest attributes.role=leader after killUtc
    { /* ... buckets + gap + recovery ... */ }
}
```

**Parsed-record struct** — mirror `FrameworkLogRecord` (`StructuralCohort.cs:13-19`): a decoupled `readonly record struct SendEvidenceRecord(string CorrelationId, DateTimeOffset Timestamp)` so both the live fixture (parses `JsonElement` hits) and the hermetic facts (synthetic) feed the same `Score`.

**Scoring bodies** — RESEARCH Code Examples 3+4+5 (already C#):
```csharp
// claims #1+#2: each fire's earliest @timestamp per corrId, floor to 30s, assert <=1 distinct corrId/bucket
var fireTimeByCorr = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
foreach (var r in sends)
    if (!fireTimeByCorr.TryGetValue(r.CorrelationId, out var cur) || r.Timestamp < cur)
        fireTimeByCorr[r.CorrelationId] = r.Timestamp;
var byBucket = fireTimeByCorr.GroupBy(kv => Bucket30s(kv.Value))
    .ToDictionary(g => g.Key, g => g.Select(x => x.Key).Distinct().Count());
bool zeroDuplicate = byBucket.Values.All(c => c <= 1);
// claim #3: contiguous EMPTY bucket(s) between last-pre-kill and first-post-recovery; gapObserved>=1 or INCONCLUSIVE
// claims #4+#5: roleFlipVisible = roleFlipUtc is not null; recovery = roleFlipUtc - killUtc; boundedRecovery = recovery in [0, 30s]
```

**Result record + Verdict fold** — mirror `StructuralCohortResult` (`StructuralCohort.cs:141-157`) for the maps, but the top-level `HaFireVerdict` must expose a `Verdict` (`Pass`/`Fail`/`Inconclusive` — reuse the enum from `AnalyzerReport.cs:24-57`). Fail-closed folding: `>=2` per bucket OR backfilled gap OR recovery > 30s OR no role flip => `Fail`; gap tick never observed OR zero send records => `Inconclusive`; else `Pass`. (RESEARCH Common Pitfall 1/4 warning signs → Inconclusive, never a false Pass.)

---

### `tests/BaseApi.Tests/Observability/Analysis/HaFireBucketScorerFacts.cs` (test, hermetic unit)

**Analog:** `tests/BaseApi.Tests/Observability/Analysis/StructuralCohortFacts.cs` — a `public sealed class` of `[Fact]`s that synthesize the record struct directly (no ES, no Docker) and assert the pure classifier + the verdict fold. Runs under the standard hermetic filter `--filter-not-trait Category=RealStack` (NOT tagged RealStack).

**Synthetic-record builder idiom** (`StructuralCohortFacts.cs:32-49`) — small private factory helpers over fixed constants:
```csharp
private const string Corr = "corr-1";
private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;
private static SendEvidenceRecord Send(string corr, DateTimeOffset ts) => new(corr, ts);
```

**Facts to pin** (mirror the three `StructuralCohortFacts` cases — one happy-path collapse, one genuine-defect trip, one boundary/routing case):
- `PhaseAlignedKill_OneGapBucket_AllUnique_Pass` — one pre-kill bucket, one EMPTY gap bucket, one post-recovery bucket, role flip <=30s ⇒ `Verdict.Pass` (the mirror of `NoFault_MultiRecordPerHop_Collapses_..._Pass`).
- `SplitBrain_TwoCorrIds_OneBucket_TripsDuplicate` — two distinct send-correlationIds in one 30s bucket ⇒ `Verdict.Fail` (the mirror of `GenuineRedelivery_..._TripsDuplicate`).
- `NoGapBucketObserved_IsInconclusive_NotPass` — every bucket has exactly one corrId, no empty gap ⇒ `Verdict.Inconclusive` (Pitfall 1 warning sign — coverage miss must NOT be Pass).
- (optional) `BackfilledGapTick_IsFail` and `RecoveryOver30s_IsFail`.

Assertion style (`StructuralCohortFacts.cs:104-111`): `Assert.Equal(Verdict.Pass, report.Verdict)` etc.

---

## Shared Patterns

### Verdict → exit-code resolution (0/1/2)
**Source:** `scripts/lib/exit-code-resolution.ps1` (`Resolve-AnalyzerExitCode` `:28-42`, `Resolve-SweepClass` `:44-58`)
**Apply to:** `scripts/phase-83-ha-failover.ps1` (dot-source + consume, STEP H), and — transitively — the HA report JSON must expose a top-level string `Verdict`.
- These are PURE functions with no top-level side effects; `. ./scripts/lib/exit-code-resolution.ps1` only DEFINES them.
- `Resolve-AnalyzerExitCode` keys on `"$($Report.Verdict)"`: `Inconclusive→2`, `Fail→1`, `Pass→0`, **default (unknown/absent) → 1 fail-closed** (never a false green). This is why the new `HaFireVerdict`/report JSON reuses the `Verdict` enum name strings verbatim.
- Do NOT hand-roll a `switch` in the harness (RESEARCH "Don't Hand-Roll").

### ES index/field constants — NO `.keyword`
**Source:** `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs`
**Apply to:** every ES query in `HaFailoverAnalyzerE2ETests.cs`.
- `LogsDataStream = "logs-generic.otel-default"` (`:40`); query URL is `$"{LogsDataStream}/_search"`.
- Field paths are DIRECT keyword paths — `StepIdFieldPath` (`:129`), `CorrelationIdFieldPath` (`:71`), `ExecutionIdFieldPath` (`:107`), `WindowTimestampFieldPath = "@timestamp"` (`:217`). **Adding a `.keyword` sub-field returns ZERO hits** (the trap that broke 4 facts at Phase-11 UAT).
- NEW: add `RoleFieldPath = "attributes.role"` here (single-const edit) for the claim-4/5 role-flip query; Wave-0 confirm `keyword` mapping (A5).

### The `@timestamp` epoch-ms-string read trap
**Source:** `AnalyzerE2ETests.TryReadTimestamp` (`:627-663`)
**Apply to:** every `_source.@timestamp` read in the new verdict fact and its send-record parse.
- `_source.@timestamp` is a numeric epoch-ms STRING (with sub-ms fraction), NOT ISO. Parse epoch-ms FIRST (`double.TryParse` → `FromUnixTimeMilliseconds((long)…)`), ISO `TryParse` only as fallback. Naive `DateTimeOffset.TryParse` returns empty — the phase-73 bug.

### Static ES query template (injection guard)
**Source:** `AnalyzerE2ETests.Build*SearchBody` (`:294-339,392-406`)
**Apply to:** all new query builders.
- STATIC raw-string (`$$"""..."""`) templates; interpolate ONLY `EsIndexNames.*` consts and validated `DateTimeOffset:o` values (T-66-08 / ASVS V5). No untrusted concatenation.

### kubectl house style
**Source:** `phase-80-harness.ps1` (STEP D `:284-293`, crash sequencer `:380-421`)
**Apply to:** STEP F of the new harness.
- Namespace-scoped `-n skp` on every call; jsonpath reads via `-o jsonpath='{...}'`; **pin `$LASTEXITCODE` BEFORE any `.Trim()`** (a failed exec yields empty stdout — trimming the string-cast can't throw and bypass the distinct `exit` code); bounded waits via `rollout status ... --timeout=120s` or a `(Get-Date).AddSeconds(N)` deadline loop with `Start-Sleep`.

### Roll-up summary JSON shape (optional roll-up)
**Source:** `scripts/phase-81-sweep.ps1` roll-up (`:138-150`) + `analyzer-reports/phase-81-summary.json`
**Apply to:** optional `scripts/phase-83-rollup.ps1` / `analyzer-reports/phase-83-summary.json`.
- Array of `{ scenarioId, verdict, ..., harnessExit, class }`; `class` from `Resolve-SweepClass`. For a single HA run this collapses to a one-element array (or per-claim rows) — discretionary.

---

## Mechanism-under-test references (read-only evidence sources, NOT files to create)

These confirm the analyzer's evidence semantics; the phase writes no product code:
- `src/Orchestrator/Scheduling/WorkflowFireJob.cs` — per-fire correlationId mint (`:58`), `leaderState.IsLeader && startupGate.IsReady` gate (`:76`), leader `foreach … dispatcher.DispatchAsync` (`:79-97`, emits NO log), follower-skip `"Follower — leader gate closed; skipping entry-step sends for {WorkflowId}"` (`:106`, carries no `StepId`). Basis for D-03: `exists attributes.StepId` is a clean leader-only filter.
- `src/Orchestrator/Observability/OrchestratorRoleLogEnricher.cs` — appends `attributes.role` = `leader`|`follower` on every record (`:29` `new KeyValuePair<string,object?>("role", state.Role)`). The claim-4/5 evidence stream.
- Lease object: `orchestrator-leader` in namespace `skp` — the D-02a `holderIdentity` read + the D-06 flip observation.

## No Analog Found

None. Every new file has an exact or role-match in-repo analog (the codebase already contains a
live-verified analyzer E2E fixture, a pure-classifier + hermetic-facts pair, a k8s single-scenario
harness, and the shared exit-code lib).

## Metadata

**Analog search scope:** `scripts/`, `scripts/lib/`, `tests/BaseApi.Tests/Observability/`,
`tests/BaseApi.Tests/Observability/Analysis/`, `tests/BaseApi.Tests/Observability/Helpers/`,
`src/Orchestrator/{Scheduling,Election,Observability}/`, `analyzer-reports/`.
**Files scanned (read in full or targeted):** phase-80-harness.ps1, phase-81-sweep.ps1,
exit-code-resolution.ps1, AnalyzerE2ETests.cs, StructuralCohort.cs, StructuralCohortFacts.cs,
EsIndexNames.cs, AnalyzerReport.cs, ElasticsearchTestClient.cs (signature), WorkflowFireJob.cs,
OrchestratorRoleLogEnricher.cs, phase-81-summary.json.
**Pattern extraction date:** 2026-07-18
</content>
</invoke>
