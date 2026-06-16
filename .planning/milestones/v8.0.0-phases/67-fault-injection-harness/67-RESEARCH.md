# Phase 67: Fault-Injection Harness - Research

**Researched:** 2026-06-14
**Domain:** PowerShell ops/test orchestration (NO new product code) — wiring Phase 65 (clean stack + seeder) + Phase 66 (analyzer) + docker fault ops into one automated scenario run
**Confidence:** HIGH (all findings are direct repo file/line refs; the only MEDIUM items are the two derived numeric values N-fires + dwell, justified from cadence math)

## Summary

Phase 67 is a single new PowerShell sibling, `scripts/phase-67-harness.ps1`, that shells already-built artifacts in sequence: `phase-65-up` → (per run: `phase-65-reset` → `dotnet test ~FanOutSeeder` → psql wf-id → `POST /orchestration/start` require-204 → observe/inject → drain → `dotnet test ~Analyzer` → record verdict) → `docker compose down`. Every infra primitive it needs (docker compose exec psql, redis-cli, NDJSON health parse, `Invoke-RestMethod` against `http://localhost:8080`, exit-code-as-verdict) is already proven in `scripts/phase-62-close.ps1` / `phase-65-reset.ps1` / `phase-65-up.ps1`. There is zero new tooling and zero product-source change.

**One blocking finding the planner MUST resolve first (Open Question 1):** the Phase 66 analyzer fixture `AnalyzerE2ETests.cs` does **NOT** currently accept a caller-supplied `-ScenarioId` or window start/end. It hardwires `const DefaultScenarioId = "TEST-01"` (line 64) and computes its window internally as `windowStartUtc = DateTimeOffset.UtcNow` at test start (line 93), with the window closing after an internal `DrainMs`/`PollToStable` cycle. The CONTEXT D-04/D-13 narrative ("the harness passes `-ScenarioId` and window start/end timestamps to the analyzer") describes a contract the Phase 66 code does not yet implement. The harness cannot honor D-13 as written without either (a) a small parameterization change to the Phase 66 fixture, or (b) accepting the fixture's self-windowing and sequencing the harness around it. This is a real scope decision, not a detail.

**Primary recommendation:** Build the PS orchestrator with an in-script scenario hashtable (D-12). For the analyzer handoff, adopt option (b) as the cheapest path that touches no product code and minimal test code: run the analyzer fixture **immediately after** the observe-window's wall-clock close so its internal `windowStartUtc` ≈ the harness window start, OR add a thin env-var read (`SCENARIO_ID`, `WINDOW_START_UTC`) to the fixture — flag this for discuss-phase as the one decision that determines whether Phase 67 is pure-ops or ops+one-test-edit.

## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-15 — research these, do NOT re-decide)
- **D-01** PowerShell orchestrator `scripts/phase-67-harness.ps1` that shells existing pieces (C# orchestrator rejected).
- **D-02** Seed→activate wf-id handoff = psql sentinel lookup `SELECT id FROM workflows WHERE name='v8-fanout-proof'`. Seeder stays a pure seeder.
- **D-03** Activation = `POST /api/v1/orchestration/start` with `[<wf-id>]`, hard-gate on `204`.
- **D-04** Final exit code == analyzer verdict (non-zero = FAIL). Infra-step aborts use distinct non-zero codes.
- **D-05** Crash mechanism = `docker stop → dwell → docker start` (NOT kill — every container is `restart: unless-stopped`).
- **D-06** Whole-tier crash — stop ALL replicas of the 2-replica `processor-sample` tier.
- **D-07** Inject after N observed cron fires (~window midpoint). Exact N + observation mechanism = discretion (resolved below).
- **D-08** Dwell ≥ one 30s cron interval. Exact value = discretion (resolved below).
- **D-09** Canonical fault proof = processor crash (TEST-02-shaped).
- **D-10** Run TEST-01 no-fault baseline first, then the crash run.
- **D-11** Acceptance = end-to-end + produces verdict, no human step. Expect PASS; non-PASS = real finding, not a harness defect.
- **D-12** Scenario-config table seam: `scenarioId → {targetContainers, faultType, injectAfterNFires, dwellSeconds, notes}`. Phase 67 ships TEST-01 + TEST-02; Phase 68 adds 03–07. Format = discretion (default: in-script hashtable).
- **D-13** Observe→analyze cohort = window-by-timestamps; `phase-65-reset` (next run) halts fires by deleting workflow rows. No `orchestration/stop`. (See Open Question 1 — the handoff mechanism is not yet implemented in Phase 66.)
- **D-14** Between-runs = `phase-65-reset`, stack stays up.
- **D-15** Final teardown = `docker compose down` (keep volumes + images), once at the very end.

### Claude's Discretion (resolved with evidence in this doc)
- N-observed-fires threshold + per-fire observation mechanism (D-07) → resolved below.
- Dwell duration (D-08) → resolved below.
- Scenario-config table format (D-12) → in-script hashtable recommended.
- psql connection/auth invocation (D-02) → exact command below.
- Distinct non-zero exit-code numbering (D-04) → scheme proposed below.
- Per-step operator trace + artifact location → recommended below.

### Deferred Ideas (OUT OF SCOPE)
- 7-scenario sweep + all-7 formal PASS assertions → Phase 68 (this harness's consumer; "just data" rows 03–07).
- Seeder/reset/up artifacts → Phase 65 (complete; harness calls them).
- Analyzer / PASS-FAIL engine → Phase 66 (complete; harness invokes it).
- No new product code / product log / product metric (v8.0.0 scope discipline). Sources of truth = Prometheus + ES only.

## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| FAULT-01 | Harness activates via `POST /api/v1/orchestration/start` + lets cron drive a 5-min window (~10 fires, fresh correlationId/fire) | Activation pattern (D-03) + observe-window + per-fire observation signal (D-07) all resolved below. Controller confirmed: `OrchestrationController.cs:43-53` accepts bare `List<Guid>`, returns 204. |
| FAULT-02 | Harness injects fault mid-run (container kill/restart of targeted tier), system recovers within window | Whole-tier `docker stop`/`docker start` pattern + container naming resolved below (D-05/06). Counter-reset hazard already handled by analyzer (ES-primary). |
| FAULT-03 | Each scenario runs fully automated clean→seed→activate→inject→observe→analyze→tear down, no human step | Full step sequence + each step's proven primitive resolved below; exit-code-as-verdict (D-04). |

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Stack bring-up / teardown | Harness (PS) → `docker compose` | — | `phase-65-up.ps1` already owns bring-up; harness adds final `compose down` |
| Clean state per run | Harness (PS) → `phase-65-reset.ps1` | Redis + Postgres | reset owns FLUSHALL + heal-wait + row-scoped DELETE |
| Workflow seed | `dotnet test ~FanOutSeeder` (in-proc WebApi → host stack) | Postgres via REST | seeder is a pure self-verifying RealStack fixture |
| Wf-id resolution | Harness (PS) → psql | Postgres | seeder does not surface the id; harness reads it (D-02) |
| Activation gate | Harness (PS) → HTTP `POST /start` | baseapi-service:8080 | 204 proves seed + liveness heal (D-03) |
| Fault injection | Harness (PS) → `docker stop`/`start` | docker engine | deterministic outage the system must survive (D-05/06) |
| Fire observation | Harness (PS) → Prometheus/ES poll | prometheus:9090 / ES:9200 | cheapest host-pollable per-fire signal (D-07, below) |
| Verdict / scoring | `dotnet test ~Analyzer` | Prometheus + ES | Phase 66 is the single source of truth (D-04/D-11) |

## Standard Stack

This is an ops phase — the "stack" is the set of already-pinned tools the harness shells. No new packages.

| Tool | Where pinned | Purpose in harness | Proven-by |
|------|-------------|--------------------|-----------|
| PowerShell (`pwsh`) | repo convention (18 `phase-NN-close.ps1`) | the orchestrator language | every `scripts/*.ps1` |
| `docker compose` v2 | `compose.yaml` | bring-up, exec, ps, stop/start, down | `phase-65-up.ps1`, `phase-62-close.ps1` |
| `docker` (engine CLI) | — | `docker exec sk-redis ...`, `docker ps --filter name=` | `phase-65-reset.ps1:59,126` |
| psql (in `postgres` container) | `compose.yaml:6` (postgres:17-alpine) | sentinel wf-id lookup (D-02) | `phase-65-reset.ps1:99` |
| `dotnet test --filter` | repo test project | seeder + analyzer invocation | `phase-62-close.ps1:353` |
| `Invoke-RestMethod` / `Invoke-WebRequest` | PS built-in | `POST /start`, Prom/ES polling | `phase-62-close.ps1:181,193,217,241` |

**Host endpoints the harness can reach directly (verified):**
- baseapi-service WebApi: `http://localhost:8080` (`compose.yaml:345-346`; `$baseApi='http://localhost:8080'` at `phase-62-close.ps1:170`)
- Prometheus HTTP API: `http://localhost:9090` (`compose.yaml:97-98`; `PrometheusTestClient.cs:47`)
- Elasticsearch: `http://localhost:9200` (`compose.yaml:37-38`; `ElasticsearchTestClient.cs:39`)
- Postgres host port: `localhost:5433` (`compose.yaml:13-14`) — but for the sentinel lookup use `docker compose exec postgres psql` (container-side, NO host-port/auth string needed; see D-02 below)

**No `npm install` / no package step.** Installation = none.

## Architecture: The Harness Flow (per CONTEXT D-01)

```
phase-67-harness.ps1 -ScenarioId <id>
  │
  ├─ STEP A  phase-65-up.ps1                         ── bring stack up (10 svc types healthy)        [abort code 10]
  │
  └─ FOR EACH run in scenario order (TEST-01 then TEST-02 — D-10):
        ├─ STEP B  phase-65-reset.ps1                ── FLUSHALL + heal-wait + row DELETE           [abort code 20]
        ├─ STEP C  dotnet test --filter "Category=RealStack&FullyQualifiedName~FanOutSeeder"
        │                                            ── seed v8-fanout-proof (idempotent)           [abort code 30]
        ├─ STEP D  docker compose exec -T postgres psql ... SELECT id  ── resolve wf-id (D-02)      [abort code 40]
        ├─ STEP E  POST /api/v1/orchestration/start  [<wf-id>]  REQUIRE 204 (D-03)                  [abort code 50]
        ├─ STEP F  record windowStart = now ; observe loop:
        │            poll fire-count until N observed fires (D-07)
        │            ── crash runs only: docker stop <tier replicas> ; sleep dwell ; docker start  [abort code 60]
        │            continue until 5-min window elapsed → record windowEnd = now
        ├─ STEP G  allow analyzer drain (DrainMs 60s + PollToStable 60s already internal to fixture)
        ├─ STEP H  dotnet test --filter "Category=RealStack&FullyQualifiedName~Analyzer"
        │                                            ── verdict via EXIT CODE + analyzer-reports/{id}.json
        └─ record verdict (mirror analyzer exit; D-04)
  │
  └─ STEP Z  docker compose down  (keep volumes + images — D-15)                                    [always, at end]

Final harness exit = analyzer exit of the last run (D-04). Infra aborts (codes 10–60) are distinct + loud.
```

### Recommended structure (single self-contained script, close-script convention)
- One file `scripts/phase-67-harness.ps1` (matches the 18-sibling convention; `$ErrorActionPreference='Stop'`, `Set-StrictMode -Version Latest`, `Push-Location $repoRoot ... finally { Pop-Location }` — copy the frame verbatim from `phase-62-close.ps1:108-113,473-475`).
- In-script scenario hashtable (D-12 default), e.g.:
  ```powershell
  $Scenarios = [ordered]@{
    'TEST-01' = @{ targetContainers = @();                 faultType = 'none';      injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
    'TEST-02' = @{ targetContainers = @('processor-sample'); faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
  }
  ```
  Phase 68 adds rows 03–07 with the same shape — "just data" (D-12). In-script over `.psd1`/JSON keeps the close-script self-contained style and avoids a new file-parse dependency.

## Discretion Items — Resolved With Evidence

### D-07 — injection-timing trigger (per-fire observation signal + N)

**Cadence math:** cron is `*/30 * * * * *` (6-field seconds cron; `FanOutSeederE2ETests.cs:55`, confirmed `ROADMAP.md:11`). That is one fire every 30s → **~10 fires in a 5-min (300s) window**. Window midpoint ≈ fire #5.

**Recommended N = 4.** Inject after the **4th observed fire** (~120s into the window). Justification: (1) ≥3 healthy baseline fires confirm the pipeline is actually firing before perturbing it (D-07's intent); (2) leaves ~180s (≥ half the window) after injection for the dwell + `docker start` + redelivery + drained completion; (3) near the midpoint, satisfying "≈ window midpoint." Triggering off *observed* fires (not wall-clock) guarantees the fault lands during real activity even if startup was slow.

**Observation mechanism — recommended: poll Prometheus `orchestrator_dispatch_sent_total` (cheapest).** This is the single cheapest host-pollable per-fire signal a PowerShell harness can use:
```powershell
# host-side, no auth, integer counter — one GET per poll
$resp = Invoke-RestMethod -Uri 'http://localhost:9090/api/v1/query?query=orchestrator_dispatch_sent_total' -TimeoutSec 10
$fires = [int][double]($resp.data.result | Measure-Object -Property { $_.value[1] } -Sum).Sum  # sum across label series
```
Rationale: the analyzer itself derives its trigger denominator from exactly this counter's windowed delta (`AnalyzerE2ETests.cs:119-123`, `DispatchSent = orchestrator_dispatch_sent_total`, `triggerCount = Round(DispatchSentDelta)`). One counter, summed across label combinations (no ProcessorId filter), incremented once per dispatch/fire. The harness records the counter value at `windowStart`, then polls until `current - start >= N`. This reuses the analyzer's own fire-accounting basis, so "N observed fires" means the same thing to harness and analyzer.

**Alternate (if Prom proves noisy): poll ES `Step_A`-label distinct-correlationId count.** Each fire emits one `Step_A` COMPLETED log (entry step; `FanOutSeederE2ETests.cs:64` Step_A is the entry node, `Step_<label>` is the per-execution signal the analyzer parses, CONTEXT canonical-ref §64). Host-side `_count` over the data stream `logs-generic.otel-default` (`EsIndexNames.cs:40`), field `attributes.StepLabel` = `Step_A` (`EsIndexNames.cs:87` — DIRECT path, NO `.keyword`), windowed by `@timestamp` (`EsIndexNames.cs:133`):
```powershell
$body = '{"query":{"bool":{"filter":[{"term":{"attributes.StepLabel":"Step_A"}},{"range":{"@timestamp":{"gte":"<windowStart:o>"}}}]}}}'
$resp = Invoke-RestMethod -Method Post -Uri 'http://localhost:9200/logs-generic.otel-default/_count' -ContentType 'application/json' -Body $body -TimeoutSec 10
$fires = [int]$resp.count   # ~1 Step_A per fire
```
Prefer Prometheus: it is a single integer counter (no JSON body, no index-lazy 404 race, no per-doc indexing lag). ES is the fallback if dispatch_sent ever lags the actual fire. `[CITED: AnalyzerE2ETests.cs:119-123, EsIndexNames.cs:40,87,133]`

### D-08 — dwell duration

**Recommended dwell = 45 seconds.** Justification: (1) > one full 30s cron interval, so **at least one complete fire happens entirely while the tier is dead** (D-08's locked constraint — guarantees a genuinely disrupted run, exercising keeper re-inject / redelivery); (2) < half the 300s window (45 ≪ 150); (3) sits inside the analyzer's settle budget — after `docker start`, the recovered tier has the remainder of the window plus the analyzer's `DrainMs=60_000` + `PollToStableBudgetMs=60_000` (120s worst-case settle after window close; `AnalyzerE2ETests.cs:56-59`) to complete the redelivered 9-step traversal before scoring. With N=4 (~120s) + dwell 45s, recovery starts ~165s in, leaving ~135s of window + 120s drain = ample. 45s is the low end of CONTEXT's 45–60s suggestion; pick 45 to maximize post-recovery window. `[CITED: AnalyzerE2ETests.cs:56-59]` `[ASSUMED: 45s sufficient for one full disrupted fire — verify on the TEST-02 reference run]`

### D-02 — psql sentinel lookup (exact command)

Reuse the proven container-side pattern from `phase-65-reset.ps1:99` and `phase-62-close.ps1:314` (`docker compose exec -T postgres psql -U postgres -d stepsdb`). No host-port/auth connection string needed — exec runs inside the container as the `postgres` superuser. Use `-tA` for tuples-only, unaligned output so the id comes back bare:
```powershell
# D-02: resolve the activation target by the stable sentinel name (Phase 65 D-04)
$wfId = (docker compose exec -T postgres psql -U postgres -d stepsdb -tA `
          -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'").Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) {
    Write-Host "[harness] could not resolve v8-fanout-proof workflow id (psql exit $LASTEXITCODE). Aborting." -ForegroundColor Red
    exit 40
}
# $wfId is a bare GUID string, e.g. 3f2a...; guard it is a single row (reset guarantees a clean DB → exactly 1)
```
Notes: db is `stepsdb`, user `postgres` (verified `phase-65-reset.ps1:99`). The statement is a static literal with no interpolated input (matches `phase-65-reset.ps1` threat T-65-06 discipline). `-tA` is the standard psql flag pair for capturing a scalar in a shell var. `[CITED: phase-65-reset.ps1:99, phase-62-close.ps1:314]`

### D-05/D-06 — docker fault op (whole-tier stop/start)

**Container naming (verified `compose.yaml`):**
- `processor-sample` (`compose.yaml:265`) has **`deploy.replicas: 2`** (`:269-270`) and **NO `container_name`** → docker compose generates names like `sk_p4-processor-sample-1`, `sk_p4-processor-sample-2` (project-prefix + service + replica index). It must be targeted by **compose service name**, not a fixed container name.
- `keeper` (`compose.yaml:229`) likewise: `replicas: 2`, no `container_name` (`:233-234`). Same handling. (Phase 68 row.)
- `sk-redis` (`compose.yaml:137`), `sk-rabbitmq` (`:163`), `sk-orchestrator` (`:189`) ARE named (single instance). (Phase 68 rows.)
- `phase-65-reset.ps1` NOTES (`:30-32`) confirms: "sk-redis is NAMED (docker exec); postgres + processor-sample are UNNAMED (docker compose exec / ps)."

**Proven whole-tier stop/start = `docker compose stop/start <service>`** (operates on ALL replicas of the service in one call — exactly the D-06 whole-tier requirement):
```powershell
# D-05/D-06: crash the WHOLE processor-sample tier (both replicas) deterministically
foreach ($svc in $scenario.targetContainers) {        # e.g. @('processor-sample')
    docker compose stop $svc | Out-Null               # stops ALL replicas of the service
    if ($LASTEXITCODE -ne 0) { Write-Host "[harness] docker compose stop $svc failed." -ForegroundColor Red; exit 60 }
}
Start-Sleep -Seconds $scenario.dwellSeconds           # dwell ≥ one 30s cron interval (D-08)
foreach ($svc in $scenario.targetContainers) {
    docker compose start $svc | Out-Null              # bring ALL replicas back; restart:unless-stopped is irrelevant after explicit stop
    if ($LASTEXITCODE -ne 0) { Write-Host "[harness] docker compose start $svc failed." -ForegroundColor Red; exit 60 }
}
```
Why `compose stop`, not `docker stop`: `docker stop` requires the per-replica generated container names (fragile — project prefix varies, replica indices). `docker compose stop <service>` addresses the service and stops every replica with one stable command — the clean whole-tier primitive. Why `stop` not `kill` (D-05): every container is `restart: unless-stopped` (`compose.yaml`, e.g. `:271`); `docker kill` would auto-resurrect in ~1-2s (non-deterministic, near-zero outage). `compose stop` yields the deterministic harness-controlled outage; `compose start` owns recovery timing. For the named single-instance tiers (Phase 68: redis/rabbitmq/orchestrator) the SAME `docker compose stop/start <service>` works — so the harness needs no naming special-case at all; it always uses the compose service name from `targetContainers`. `[CITED: compose.yaml:137,189,229,233-234,265,269-271; phase-65-reset.ps1:30-32]`

> **Reset interaction (verify in plan):** `phase-65-reset.ps1` STEP 4 (`:114-132`) asserts `processor-sample` has running replicas and aborts if 0. The harness does `docker compose start` *within* the crash run (D-05/D-14: "crashed tiers are already restarted within their run, so the stack is whole before each reset"). The harness must `docker compose start` the crashed tier AND wait for it healthy **before** the run ends / before the next `phase-65-reset`. Reuse the NDJSON-per-replica health-wait loop verbatim from `phase-65-up.ps1:37-73` / `phase-62-close.ps1:260-282` to confirm both replicas are `Health=healthy` post-`start`.

### D-03 — activation gate (exact request shape)

`OrchestrationController.cs:43-53`: `[HttpPost("start")]` → `public async Task<IActionResult> Start([FromBody] List<Guid> workflowIds, ...)` → `return NoContent()` (204). Route `[Route("api/v{version:apiVersion}/[controller]")]` + `[ApiVersion("1.0")]` → `POST /api/v1/orchestration/start`. Body is a **bare JSON array of GUIDs**, e.g. `["<wf-id>"]`. v1 is validation-only / no side-effect (`:38-41`, "No orchestration side-effects… amended Acceptance Criteria 2026-05-28") — the 204 is the gate that seed + liveness heal succeeded (no 422 from `ProcessorLivenessValidator`); the cron is what actually drives the fires (CONTEXT D-03).

PowerShell pattern (base URL `http://localhost:8080` per `compose.yaml:345-346` + `phase-62-close.ps1:170`; use `Invoke-WebRequest` to read `StatusCode` for the hard 204 gate):
```powershell
# D-03: activation — hard-gate on 204
$startBody = ConvertTo-Json @($wfId)         # bare array: ["<guid>"]
try {
    $resp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
              -ContentType 'application/json' -Body $startBody -TimeoutSec 15 -ErrorAction Stop
} catch {
    Write-Host "[harness] POST /orchestration/start threw: $($_.Exception.Message)" -ForegroundColor Red; exit 50
}
if ($resp.StatusCode -ne 204) {
    Write-Host "[harness] activation gate failed — expected 204, got $($resp.StatusCode) (seed/liveness not healthy?). Aborting." -ForegroundColor Red
    exit 50
}
```
Note `ConvertTo-Json @($wfId)` — the `@()` forces a JSON array even for the single-element case (PowerShell would otherwise serialize a bare string). `[CITED: OrchestrationController.cs:43-53, compose.yaml:345-346, phase-62-close.ps1:170]`

### D-13 — window→analyzer handoff (CRITICAL GAP — see Open Question 1)

**What the analyzer fixture actually does (verified `AnalyzerE2ETests.cs`):**
- It does **NOT** read `-ScenarioId` from env/args. `scenarioId = DefaultScenarioId` where `const DefaultScenarioId = "TEST-01"` (`:64,74`). Grep of the whole `Observability/` tree for `GetEnvironmentVariable` / `TestRunParameters` / `RUNSETTINGS` found NO scenario-id or window-timestamp read in `AnalyzerE2ETests.cs` (only unrelated env reads in other fixtures).
- It does **NOT** receive window start/end. It sets `windowStartUtc = DateTimeOffset.UtcNow` at test start (`:93`), takes the Prom BEFORE snapshot, then `await Task.Delay(DrainMs)` (`:100`, 60s), captures `snapshotUtc = UtcNow` (`:108`) as the ES upper bound, polls ES to stable, takes Prom AFTER, scores. So the analyzer's window is `[testStart, testStart+~60s]` plus poll-to-stable — entirely self-determined.
- It writes `analyzer-reports/{scenarioId}.json` + `{scenarioId}.txt` (`:83-85,139-141`) and asserts `Verdict==Pass` last (`:143`) → a FAIL ⇒ failed assert ⇒ non-zero `dotnet test` exit. Report shape: `AnalyzerReport` record with `Verdict` (`Pass`/`Fail` string), `TriggerCount`, `CompleteRuns`, `Missing`, `Duplicates`, `Reconciliation`, `HumanSummary` (`AnalyzerReport.cs:40-80`).

**Consequence for the harness:** D-13/D-04's "harness records window start/end and passes them (+ scenario id) to the analyzer" is **not supported by the Phase 66 fixture as shipped.** The planner has three options (this is Open Question 1 — surface to discuss-phase):

1. **Sequence-around (zero test edit, pure ops).** The harness does NOT pass timestamps; it instead *invokes the analyzer fixture at the moment the observation window closes*, so the fixture's internal `windowStartUtc` ≈ the harness window end and its self-window covers the drain tail. PROBLEM: the fixture's window is only ~60s wide (`DrainMs`), not the harness's 5-min window — it would score only the last ~60-120s of fires, not the whole ~10-fire cohort. This likely under-counts the cohort and is NOT faithful to "score the cohort fired within the 5-min window." Not recommended without confirming the fixture's window semantics against the 5-min requirement.

2. **Add a thin env-var read to the fixture (one small test edit, NOT product code).** Have `AnalyzerE2ETests` read `SCENARIO_ID`, `WINDOW_START_UTC`, `WINDOW_END_UTC` from `Environment.GetEnvironmentVariable(...)` (falling back to the current `const`/`UtcNow` defaults so the standalone Phase 66 fact still passes). The harness then sets those env vars before `dotnet test`. This makes D-04 (`-ScenarioId`) + D-13 (window-by-timestamps) real with ~10 lines of test-only change, no product touch, no new metric/log. **Recommended** — it is the smallest change that honors the locked decisions and keeps the analyzer the single source of truth. Confirm with discuss-phase that "edit the Phase 66 *test* fixture" is in-scope for Phase 67 (CONTEXT says Phase 66 is complete and the harness only *invokes* it — but the locked D-13 contract requires this seam to exist somewhere).

3. **Pass via `dotnet test -- ` RunSettings `TestRunParameters`.** xUnit v3 does not natively surface MSTest `TestContext.Properties`; env vars (option 2) are the simplest cross-framework channel. Prefer option 2's env-var read over RunSettings.

**Recommendation to planner:** plan for option 2 and flag it as the one decision needing user confirmation (does Phase 67 get to add a ~10-line env-var seam to the Phase 66 analyzer fixture?). If the user insists Phase 66 stays frozen, fall back to scoping a tiny *new* harness-owned analyzer-invoker that passes window+id — but that risks re-implementing Phase 66 logic, which CONTEXT explicitly rejects. `[VERIFIED: AnalyzerE2ETests.cs:56-143 + grep of Observability/ for env reads]`

### Counter-reset hazard — analyzer ALREADY handles it (no harness special-casing)

Confirmed: `AnalyzerE2ETests.cs:36-41` header states the crashed+restarted tier resets its Prom counters mid-window, breaking delta continuity, "which is WHY ES-primary completeness, counter-independent, is the binding arbiter and Prom reconciliation is corroborating only." The completeness verdict (CompleteRuns / Missing) is computed from ES `Step_*` traces grouped by CorrelationId (`:209-244`), NOT from Prom counters. Prom deltas only feed the corroborating `Reconciliation` step + the trigger denominator. **The harness needs NO special handling for the counter reset** — it must simply NOT misread a Prom counter discontinuity as a fault, and let the analyzer score. One caveat the planner should note: a tier restart can make the Prom *windowed delta* go negative or short (after−before with a reset in between), which could push `Reconciliation=Unreconciled` → `Verdict=Fail` if the engine treats a short delta as fail-closed. Whether the Phase 66 engine already tolerates this for a mid-window restart is the substance of D-11 ("non-PASS = real finding, investigate") — the harness reports it, does not paper over it. `[CITED: AnalyzerE2ETests.cs:36-41,119-130; AnalyzerReport.cs:21-22]`

### D-04 exit-code scheme (discretion)

Proposed distinct non-zero infra-abort codes (so an infra abort is never mistaken for an analyzer FAIL):

| Code | Meaning |
|------|---------|
| 0 | analyzer PASS (final run green) |
| 1 (or analyzer's exit) | analyzer FAIL verdict — the legitimate verdict path (D-04: mirror analyzer exit) |
| 10 | bring-up (`phase-65-up`) failed |
| 20 | reset (`phase-65-reset`) failed |
| 30 | seeder `dotnet test ~FanOutSeeder` failed |
| 40 | wf-id psql lookup failed/empty |
| 50 | activation gate ≠ 204 |
| 60 | fault inject/recover (`compose stop/start` or post-start health-wait) failed |
| 70 | teardown (`compose down`) failed (non-fatal — log loud, still surface prior verdict) |

Note the collision risk: `dotnet test` itself exits 1 on a FAIL. Keep the analyzer-FAIL path as exit 1 and start infra aborts at 10 so they never overlap (D-04 "distinct non-zero codes"). The harness's *final* exit must be the analyzer's exit for the verdict-bearing run (D-04).

### Artifacts / operator trace (discretion)

- Console: prefix every line `[phase-67-harness]` + per-step status (mirror `phase-65-reset.ps1` `Write-Phase` helper, `:37-39`). Print `windowStart`/`windowEnd`, observed-fire counts, the injected fault, and the `analyzer-reports/{scenarioId}.json` path (D-04 requires printing the report path).
- Reports land where the fixture already writes them: `analyzer-reports/{scenarioId}.json` + `.txt`, under the test `AppContext.BaseDirectory` (`AnalyzerE2ETests.cs:83`) — i.e. `tests/BaseApi.Tests/bin/.../analyzer-reports/`. The harness should echo the resolved absolute path. Consider copying the per-run report to a stable `.planning/phases/67-fault-injection-harness/reports/{scenarioId}.json` for the UAT record (optional, ops-only).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Clean state per run | custom FLUSHALL + DELETE + heal logic | `phase-65-reset.ps1` | already encodes heal-wait (avoids 422), FK-safe DELETE order, processor-set assertion |
| Stack bring-up + health gate | custom compose-up + readiness poll | `phase-65-up.ps1` | NDJSON-per-replica parse, otel-no-healthcheck special-case, badconfig exclusion all solved |
| Seeding the workflow | raw SQL inserts / new seeder | `dotnet test ~FanOutSeeder` | idempotent, self-verifying, reverse-topo edge wiring, two-sided binding all proven |
| PASS/FAIL scoring | any harness-side correctness logic | `dotnet test ~Analyzer` | Phase 66 is the SINGLE source of truth (D-04/D-11); harness-side scoring would duplicate + diverge |
| Per-replica health parse | ad-hoc `docker compose ps` parse | the verbatim NDJSON loop (`phase-65-up.ps1:37-73`) | multi-replica services emit NDJSON, not single JSON — naive `ConvertFrom-Json` breaks |
| Whole-tier crash | per-replica `docker stop <generated-name>` | `docker compose stop <service>` | one stable command stops all replicas; generated names are fragile |

**Key insight:** Phase 67 is almost entirely composition. Every primitive already exists and is battle-tested in the close-script family. The only genuinely new logic is (a) the observe-loop fire-counter (D-07), (b) the crash sequencer (D-05/06/08), and (c) the analyzer window/scenario-id handoff seam (D-13 — the one real design decision, Open Question 1).

## Common Pitfalls

### Pitfall 1: Assuming the analyzer accepts `-ScenarioId` / window timestamps
**What goes wrong:** Planner writes the harness to pass `-ScenarioId TEST-02` and window bounds to `dotnet test`, expecting the report to be `TEST-02.json` over the harness window. The fixture ignores all of it — writes `TEST-01.json` over its own internal ~60s window.
**How to avoid:** Resolve Open Question 1 first (option 2: env-var seam). Verify `analyzer-reports/TEST-02.json` actually appears with the harness's scenario id before trusting any run.
**Warning sign:** report filename is `TEST-01.json` regardless of `-ScenarioId`.

### Pitfall 2: `docker stop` by generated container name
**What goes wrong:** `docker stop sk_p4-processor-sample-1` — project prefix differs per checkout dir; replica indices not guaranteed; misses the 2nd replica → partial degradation, not a tier outage (violates D-06).
**How to avoid:** Always `docker compose stop <service>` from `targetContainers`.

### Pitfall 3: Not waiting for the crashed tier to heal before reset
**What goes wrong:** `phase-65-reset.ps1:118-122` aborts (exit 2) if `processor-sample` has 0 running replicas — or the heal-wait (`:72-89`) times out at 60s if the just-`start`ed replicas haven't re-written their liveness key yet. The harness's *next* run's reset fails.
**How to avoid:** After `docker compose start`, run the NDJSON health-wait (both replicas `Health=healthy`) before ending the run (reuse `phase-62-close.ps1:260-282`).

### Pitfall 4: Single-element JSON body serializes as a string
**What goes wrong:** `ConvertTo-Json $wfId` (no `@()`) yields `"3f2a..."` not `["3f2a..."]` → `[FromBody] List<Guid>` 400s → activation gate fails for the wrong reason.
**How to avoid:** `ConvertTo-Json @($wfId)`.

### Pitfall 5: Treating a mid-window Prom counter reset as a fault
**What goes wrong:** Harness inspects Prom deltas itself and aborts on a negative/short delta from the restart.
**How to avoid:** The harness does NO Prom correctness logic — it only polls `orchestrator_dispatch_sent_total` for the *fire count* (monotonic within the pre-injection baseline) and hands scoring to the analyzer, which is ES-primary by design (`AnalyzerE2ETests.cs:36-41`).

## Validation Architecture (harness self-checks — Nyquist Dimension-8 seed)

The harness's own infra-step self-checks. Each step must prove success before the next proceeds (fail-loud, distinct exit code). This is the source for a VALIDATION.md / Dimension-8 strategy.

| # | Step | Success signal (how the harness KNOWS it worked) | Source | Abort code |
|---|------|--------------------------------------------------|--------|-----------|
| V1 | Bring-up | all 10 service types `Health=healthy` (otel = `running`); 0 badconfig | `phase-65-up.ps1:37-83` | 10 |
| V2 | Reset | FLUSHALL exit 0 + ≥1 `skp:proc:*:*` liveness key reappears within 60s + graph DELETE exit 0 + processor-sample replicas ≥1 | `phase-65-reset.ps1:58-132` | 20 |
| V3 | Seed | `dotnet test ~FanOutSeeder` exit 0 (fixture self-verifies 1 wf / 9 steps / 8 edges / 9 assignments + idempotency) | `FanOutSeederE2ETests.cs:104-216` | 30 |
| V4 | Wf-id | psql returns exactly one non-empty GUID | D-02 cmd above | 40 |
| V5 | Activation gate | `POST /start` returns HTTP **204** | `OrchestrationController.cs:43-53` | 50 |
| V6 | Baseline firing | `orchestrator_dispatch_sent_total` delta reaches N(=4) within the pre-injection window (proves cron is actually firing) | `AnalyzerE2ETests.cs:119-123` | 60 |
| V7 | Fault injected | `docker compose stop <svc>` exit 0; optional `docker compose ps <svc>` shows 0 running during dwell | D-05 cmd | 60 |
| V8 | Recovery | `docker compose start <svc>` exit 0; both replicas back to `Health=healthy` before window close | `phase-62-close.ps1:260-282` | 60 |
| V9 | Verdict produced | `analyzer-reports/{scenarioId}.json` exists AND `dotnet test ~Analyzer` exit code captured (0=PASS, non-0=FAIL) | `AnalyzerE2ETests.cs:139-143` | (verdict, not abort) |
| V10 | Teardown | `docker compose down` exit 0; no lingering containers | D-15 | 70 (non-fatal) |
| V11 | End-to-end automation | the whole sequence ran with NO `Read-Host` / no human prompt (FAULT-03) | harness design | — |

**Sampling note:** the two reference runs (TEST-01 baseline-first then TEST-02 crash, D-10) ARE the phase's validation cohort. TEST-01 green isolates harness-wiring bugs; TEST-02 then isolates fault-injection bugs (D-10). Per-run wall-clock ≈ 5-min window + ~2-min drain + bring-up/reset overhead → budget ~8-10 min/run, ~20 min for both + teardown. (Consistent with the memory note that close-gate runs are ~50 min — this harness is lighter, two runs not three-GREEN-cadence.)

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| docker + docker compose v2 | every step | assumed ✓ (entire repo depends on it) | — | none — blocking |
| pwsh | the harness | assumed ✓ (18 sibling scripts) | — | none |
| .NET SDK (`dotnet test`) | seed + analyze | assumed ✓ (repo is net8.0) | net8.0 | none |
| Prometheus @ localhost:9090 | D-07 fire poll | up once stack is up | v3.11.3 (`compose.yaml:87`) | ES `_count` fallback (D-07 alt) |
| Elasticsearch @ localhost:9200 | D-07 fallback + analyzer | up once stack is up | 8.15.5 (`compose.yaml:29`) | none for analyzer (it is ES-primary) |

*(Not probed live — the prior research attempt died on a transient API socket; these are config-verified, not runtime-verified. The harness's own V1 bring-up gate is the runtime check.)*

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | N=4 observed fires lands the injection near midpoint with ≥half-window recovery headroom | D-07 | Inject too early/late → less representative proof; tune on TEST-02 run |
| A2 | Dwell=45s holds the tier down through ≥1 complete fire | D-08 | <1 disrupted fire → proof doesn't exercise redelivery; bump toward 60s |
| A3 | `orchestrator_dispatch_sent_total` increments ~once per fire and is host-pollable pre-injection | D-07 | Fire-count miscounts → mis-timed injection; ES `Step_A` fallback exists |
| A4 | Editing the Phase 66 analyzer *test* fixture (env-var seam) is in-scope for Phase 67 | D-13 / OQ1 | If frozen, D-13 window-by-timestamps cannot be honored without re-implementing scoring |
| A5 | Phase 66 engine tolerates a mid-window Prom counter reset without spuriously FAILing reconciliation | counter-reset | A restart-induced short delta could FAIL the verdict — but D-11 treats non-PASS as a finding, not a harness bug |

## Open Questions

1. **The analyzer does not accept `-ScenarioId` or window timestamps (D-04/D-13 gap).** — DETAILED in the D-13 section. What we know: `AnalyzerE2ETests.cs` hardwires `DefaultScenarioId="TEST-01"` (`:64`) and self-computes its window (`:93,108`); no env/arg read exists. What's unclear: whether Phase 67 may add a ~10-line env-var seam to the Phase 66 *test* fixture (not product code). Recommendation: plan for the env-var seam (option 2) and confirm scope with discuss-phase. This is the single decision that gates whether Phase 67 is pure-ops or ops+one-test-edit.

2. **Does the analyzer's self-window (~60-120s) actually cover the 5-min ~10-fire cohort?** Even with option-2 timestamps passed in, confirm the fixture's `BuildStepSearchBody` range honors the supplied `windowStart..windowEnd` (it interpolates `windowStartUtc..snapshotUtc`, `:154-168`) rather than only the drain tail. If the env-var seam feeds those two fields, the cohort bounding is correct; verify on the TEST-01 run that `TriggerCount ≈ 10`.

3. **Will the TEST-02 processor-crash run actually PASS?** D-11 expects PASS but treats non-PASS as a real finding. The harness's correctness does not hinge on the verdict — but the planner should ensure the run *produces* a verdict either way and surfaces the report path. (No action needed beyond V9.)

## Sources

### Primary (HIGH confidence — direct repo files, this session)
- `.planning/phases/67-fault-injection-harness/67-CONTEXT.md` — locked D-01..D-15
- `compose.yaml` — service names, replicas, ports, restart policy (lines cited inline)
- `src/BaseApi.Service/Features/Orchestration/OrchestrationController.cs:43-53` — start endpoint shape
- `scripts/phase-65-up.ps1` / `scripts/phase-65-reset.ps1` — bring-up + reset primitives + NDJSON health parse
- `scripts/phase-62-close.ps1` — psql exec, Invoke-RestMethod, health-wait, exit-code patterns
- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` — seeder fixture + sentinel name + cron
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — analyzer window/scenarioId/drain mechanics (the OQ1 gap)
- `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` — verdict + report shape
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` / `PrometheusTestClient.cs` — ES data stream + field paths + Prom endpoint
- `.planning/REQUIREMENTS.md` — FAULT-01/02/03 + TEST pass bar
- `.planning/ROADMAP.md:907-927` — Phase 67/68 goal + success criteria

### Verification performed
- Grep of `tests/BaseApi.Tests/Observability/` for `GetEnvironmentVariable|TestRunParameters|RUNSETTINGS|SCENARIO_ID|WINDOW_START` → confirmed NO scenario-id/window env read in `AnalyzerE2ETests.cs` (the OQ1 finding).

## Metadata

**Confidence breakdown:**
- Harness flow + primitives (psql/docker/HTTP/health): HIGH — all from proven sibling scripts, exact line refs
- Container naming / whole-tier crash: HIGH — verified against `compose.yaml`
- D-07 N + observation signal: MEDIUM (HIGH on mechanism, MEDIUM on N=4 — derived, tune on run)
- D-08 dwell: MEDIUM — derived from cadence + drain budget, verify on TEST-02
- D-13 analyzer handoff: HIGH on the *gap* (verified the code), the *resolution* is an open decision (OQ1)

**Research date:** 2026-06-14
**Valid until:** stable until Phase 66 fixtures or `compose.yaml` change (no external/fast-moving deps) — ~30 days

## RESEARCH COMPLETE
