# Phase 88: Pipeline dashboard maintenance-handoff proof — Pattern Map

**Mapped:** 2026-07-28
**Files analyzed:** 10 (4 new authored, 2 generated artifacts, 2 new documents, 2 conditional modifies)
**Analogs found:** 8 / 10 (1 has no analog anywhere in the repo, 1 is partial)

**Project instructions:** no `./CLAUDE.md`, no `.claude/skills/`, no `.agents/skills/` — re-verified this session, matching `88-RESEARCH.md:55`. Every convention below is derived from the repo's own scripts, which the plan-checker treats as binding.

**Source of the file list:** `88-RESEARCH.md` § Validation Architecture "Wave 0 gaps" (:673-681), § Scenario Safety and Restore (:326-411), § Report / Verdict Artifact Shape (:415-428), § Code Examples (:504-594), § Open Questions 6 (:788-789), § Phase Requirements (:96-112).

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|---|---|---|---|---|
| `scripts/phase-88-panel-discriminate.ps1` (NEW) | test / live harness | orchestration + request-response | `scripts/phase-87-dashboards-verify.ps1` (939 lines, read in full) | **exact** for the frame, helpers, artifact, exit-code discipline |
| ↳ its fault sequencer section | — | event-driven (scale / arm → dwell → restore) | `scripts/phase-80-harness.ps1:358-422` | **role-match** — reuse the sequencer, **reject** its `$TierReplicas` map (`:148-150`) |
| ↳ its seam arm/disarm section | — | config mutation | `scripts/phase-67-harness.ps1:130-167` + `:617` | **partial** — the *discipline* (arm before the window, clear in `finally`) transfers; the *mechanism* (process env → `docker compose up`) does **not** (0 `kubectl` calls in that file) |
| `scripts/phase-88-wave0-probe.ps1` (NEW) | test / live probe | request-response | `scripts/phase-87-dashboards-verify.ps1:364-418, 199-225` (PRE gate + forward + proxy helpers) | **role-match**; small fail-closed single-purpose shape: `scripts/phase-80-collector-crash.ps1:1-27` |
| `scripts/phase-88-panel-read.js` (NEW) | utility / DOM reader | transform (rendered DOM → JSON on stdout) | *(none — the repo contains **zero** `.js`/`.ts` files and no script invokes `node`; verified by grep)* | **no analog** → use `88-RESEARCH.md:508-536` + the `run.js` contract below |
| `scripts/phase-88-sweep.ps1` (NEW, or a `-Mode RollUp` on the driver) | orchestration / roll-up | batch | `scripts/phase-81-sweep.ps1:93-150` | **exact** (child-process-per-scenario, no re-scoring, array artifact) |
| `analyzer-reports/phase-88-<scenario-id>.json` (GENERATED) | data artifact / single proof | — | `analyzer-reports/phase-83-ha.json` | **exact** schema; extension precedent = `phase-87-dashboards.json` (diagnostic arrays) |
| `analyzer-reports/phase-88-discrimination.json` (GENERATED) | data artifact / roll-up | — | `analyzer-reports/phase-81-summary.json` | **role-match** — same camelCase-array shape, but **14 rows keyed by panel**, not one per scenario |
| `.planning/phases/88-*/88-FINDINGS.md` (NEW) | documentation / accepted-limitations register | — | `.planning/phases/87-grafana-observability-dashboards/87-FINDINGS.md` | **exact** (frontmatter → numbered records → measured evidence → traceability table) |
| `.planning/phases/88-*/88-RUNBOOK.md` (NEW) | documentation / operator runbook | — | *(no runbook exists in this repo)* | **partial** — borrow `87-FINDINGS.md`'s frontmatter + evidence-citation discipline only |
| `.planning/REQUIREMENTS.md` (MODIFY) | config / requirements register | — | itself — `:24-28` (family block), `:67-86` (traceability table) | **exact** (the file pre-authorises the shape of the change) |
| `k8s/dashboards/business.json` (CONDITIONAL MODIFY) | data artifact / dashboard | transform (query → render) | itself; gated by `scripts/phase-87-dashboard-lint.ps1` | **exact** — see the caution under Shared Patterns |

---

## Pattern Assignments

### `scripts/phase-88-panel-discriminate.ps1` (test / live harness, orchestration + request-response)

**Analog:** `scripts/phase-87-dashboards-verify.ps1` — the same milestone, the same Grafana, the same cluster, the same artifact family. Copy its frame verbatim and change only what the phase requires.

**Comment-based help: STEP flow + EXIT-CODE TABLE** (`:24-86`). Every abort gets its **own** code so an infra abort can never be read as a verdict:

```powershell
    Flow:

        PRE      precondition gate            docker-desktop context + the SHARED stack forwards live
        STEP A   kubectl rollout status       deployment/grafana Available
        ...
    EXIT-CODE TABLE (the 0/1/2 classes are owned by lib/exit-code-resolution.ps1; every infra abort has
    its OWN distinct code so it can never be mistaken for a verdict — T-87-18):
        0   verdict PASS          (Resolve-AnalyzerExitCode Pass)
        1   verdict FAIL          (Resolve-AnalyzerExitCode Fail / unknown fail-closed)
        2   verdict INCONCLUSIVE  (Resolve-AnalyzerExitCode Inconclusive — an assertion could not be evaluated at all)
        10  grafana rollout/apply failed
        15  the grafana port-forward never carried traffic
        ...
        64  usage / precondition error (wrong kube context, shared stack forwards down)
```

Phase-88 additions to that table (research §Code Examples): `61` seam arm failed, `62` could not read live replicas. Keep `64` for the unknown-scenario-id guard.

**Harness frame** (`:126-156`) — copy exactly:

```powershell
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Declared BEFORE the try so the outer finally can read it under StrictMode even when the precondition
# gate exits before STEP B ever starts a forward.
$gfForwardPid = 0

Push-Location $repoRoot
try {
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-87-dashboards-verify] $msg" -ForegroundColor $color
    }
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')

    $gf       = 'http://127.0.0.1:3000'
    $proxy    = "$gf/api/datasources/proxy/uid/skp-prometheus"
    $adminB64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('admin:admin'))
    $auth     = @{ Authorization = "Basic $adminB64" }
```

Phase 88 needs **two** flags declared before the `try` for the same StrictMode reason: `$gfForwardPid = 0` **and** `$seamArmed = $false` (the outer `finally` disarms).

**Precondition gate** (`:367-394`) — pin `$LASTEXITCODE` before any `.Trim()`, and probe the shared forwards rather than discovering the gap mid-run:

```powershell
    $ctxRaw = kubectl config current-context 2>$null
    # Pin the exit code BEFORE the trim: on a failed call stdout is empty, so .Trim() on the raw capture
    # (string-cast, never $null) cannot throw and bypass exit 64.
    $ctxExit = $LASTEXITCODE
    $ctx = ("$ctxRaw").Trim()
    if ($ctxExit -ne 0 -or $ctx -ne 'docker-desktop') { ... exit 64 }

    $sharedUp = $false
    try {
        $probe = Invoke-WebRequest -Uri 'http://localhost:8080/health/ready' -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        if ($probe.StatusCode -eq 200) { $sharedUp = $true }
    } catch { }
    if (-not $sharedUp -and -not $SkipTraffic) {
        Write-Phase "REMEDIATION: pwsh -File scripts/phase-80-up.ps1   (it owns the eight shared forwards and reaps its own stale PIDs)" 'Yellow'
        exit 64
    }
```

Phase 88 must add to this gate: **"is anything else running against `skp`?"** (research hazard T-87-19 — a concurrent sweep confounds every measurement) and the **oldest-Prometheus-sample** read (research :322 — the artifact states its own evidentiary horizon).

**Own port-forward + recycled-PID guard** (`:163-179`, teardown at `:924-939`) — copy verbatim, including the comment about **never** touching `.k8s-portforward-pids`:

```powershell
    function Start-GrafanaForward {
        $p = Start-Process kubectl -PassThru -WindowStyle Hidden `
               -ArgumentList @('port-forward', 'svc/grafana', '3000:3000', '-n', 'skp', '--address', '127.0.0.1')
        return [int]$p.Id
    }
    function Stop-GrafanaForward([int]$ForwardPid) {
        if ($ForwardPid -le 0) { return }
        try {
            $proc = Get-Process -Id $ForwardPid -ErrorAction SilentlyContinue |
                    Where-Object { $_.ProcessName -eq 'kubectl' }
            if ($proc) { Stop-Process -Id $ForwardPid -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
```

**Static scenario table + bad-id guard** — from `scripts/phase-80-harness.ps1:121-136`. The id only *selects* a fixed row; nothing is ever derived from the parameter (threat T-81-01):

```powershell
    $Scenarios = [ordered]@{
        'TEST-01' = @{ targetContainers = @();                    faultType = 'none';       injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
        'TEST-02' = @{ targetContainers = @('processor-sample');  faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
        ...
    }
    if (-not $Scenarios.Contains($ScenarioId)) {
        Write-Phase "unknown scenario '$ScenarioId'. Known: $($Scenarios.Keys -join ', ')" 'Red'
        exit 64
    }
    $scenario = $Scenarios[$ScenarioId]
```

Phase-88 rows carry, per scenario: `panelId`, `regime` (A/B/C), `lever` (scale/http/data/seam), `targetTier`, `dwellSeconds`, `crossTalkPanels[]`, `predictedDirection`. Keep `$TierKind` (`:144-147` — still correct: the three app tiers are Deployments, `redis`/`rabbitmq` are StatefulSets). **Do not keep `$TierReplicas`** — see § Anti-Patterns.

**Fault sequencer** — `scripts/phase-80-harness.ps1:379-421`, the exact five-step shape (scale-0 → terminate-wait → dwell → restore → readiness gate → pin the timestamp only afterwards):

```powershell
        foreach ($tier in $scenario.targetContainers) {
            kubectl -n skp scale "$kind/$tier" --replicas=0 | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Phase "scale '$tier' to 0 failed (exit $LASTEXITCODE)." 'Red'; exit 60 }
        }
        # TERMINATE-WAIT: block until 0 running pods per crashed tier (bounded 90s).
        foreach ($tier in $scenario.targetContainers) {
            $termDeadline = (Get-Date).AddSeconds(90); $terminated = $false
            while ((Get-Date) -lt $termDeadline) {
                $running = @(kubectl -n skp get pods -l app=$tier --field-selector=status.phase=Running -o name 2>$null |
                            Where-Object { $_ -match '\S' })
                if ($running.Count -eq 0) { $terminated = $true; break }
                Start-Sleep -Seconds 2
            }
            if (-not $terminated) { Write-Phase "tier '$tier' still had running pods 90s after scale-0. Aborting." 'Red'; exit 60 }
        }
        Start-Sleep -Seconds $scenario.dwellSeconds
        # RESTORE: NEVER a blanket 1 — see the replica pattern below.
        # READINESS GATE before pinning the recovery timestamp:
        kubectl -n skp rollout status "$kind/$tier" --timeout=120s
        if ($LASTEXITCODE -ne 0) { ... exit 60 }
        $recoveryUtc = [DateTimeOffset]::UtcNow
```

**Live-read replica capture** — replaces the stale map; shape from `88-RESEARCH.md:543-557`, idiom (`$code` pinned before the trim) from `phase-87-dashboards-verify.ps1:372`:

```powershell
function Get-LiveReplicas([string]$Tier) {
    $raw  = kubectl -n skp get deploy $Tier -o jsonpath='{.spec.replicas}'
    $code = $LASTEXITCODE                       # pin BEFORE the trim (phase-87 idiom)
    $txt  = ("$raw").Trim()
    if ($code -ne 0 -or [string]::IsNullOrWhiteSpace($txt)) { return -1 }
    return [int]$txt
}
$restoreTo = Get-LiveReplicas 'processor-sample'
if ($restoreTo -lt 1) { Write-Phase "could not read replicas. Aborting." 'Red'; exit 62 }
# ... scale 0, terminate-wait, dwell ...
kubectl -n skp scale deployment/processor-sample --replicas=$restoreTo | Out-Null
$ReplicasRestored = ((Get-LiveReplicas 'processor-sample') -eq $restoreTo)   # an explicit CLAIM
```

**Seam arm/disarm.** No `kubectl`-based analog exists (`grep -c kubectl scripts/phase-79-falsify.ps1 scripts/phase-67-harness.ps1` → **0** and **0**). The transferable half is `phase-67-harness.ps1`'s *discipline* — arm **before** the observation window opens, and clear unconditionally in a `finally`:

```powershell
# scripts/phase-67-harness.ps1:134-144 — arm BEFORE the window (compose bakes it at up-time)
    if ($scenario.faultType -eq 'inject-recovery-loss') {
        $env:KEEPER_DEFEAT_REINJECT = '1'
        $env:PROCESSOR_DEFEAT_READ  = 'Step_C'
        $env:K_EXECUTIONS           = '1'
        Write-Phase "  TEST-ONLY hook: ... (baked into processor-sample + keeper at compose-up)." 'Yellow'
    }

# scripts/phase-67-harness.ps1:616-618 — cleared in a finally, never on the happy path only
    } finally {
        Remove-Item Env:SCENARIO_ID, ..., Env:KEEPER_DEFEAT_REINJECT, Env:PROCESSOR_DEFEAT_READ, ... -ErrorAction SilentlyContinue
    }
```

> **Correction to `88-RESEARCH.md:378`** (which states neither script *exports* the seams): `phase-67-harness.ps1` **does** export both, at `:135` and `:142`, and clears them at `:617`. What it does **not** do — and what makes it unusable as-is — is call `kubectl`: the seam reaches the container through `docker compose up` interpolation (`${KEEPER_DEFEAT_REINJECT:-0}`), which has no k8s equivalent. `phase-79-falsify.ps1` genuinely only mentions the var in doc comments (`:12`, `:51`). The research's conclusion (the mechanism is new) stands; its premise needed narrowing, and the plan should cite `:130-167` + `:617` as the discipline precedent rather than claiming nothing exists.

The k8s mechanism itself (`88-RESEARCH.md:562-580`) — arm, rollout, settle ≥150 s, **re-baseline**, trigger; disarm in `finally`; then **assert**, never assume:

```powershell
$seamArmed = $false
try {
    kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT=1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "seam arm failed." 'Red'; exit 61 }
    $seamArmed = $true
    kubectl -n skp rollout status deployment/keeper --timeout=180s
    Start-Sleep -Seconds 150      # >= 2 export cadences BEFORE re-baselining (DISC-06)
} finally {
    if ($seamArmed) {
        kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT- | Out-Null
        kubectl -n skp rollout status deployment/keeper --timeout=180s
    }
    $envRaw  = kubectl -n skp get deploy keeper -o jsonpath='{.spec.template.spec.containers[0].env}'
    $SeamVarsClean = -not (("$envRaw") -match 'DEFEAT|REINJECT_DELAY')
}
```

**Traffic drive** — `phase-87-dashboards-verify.ps1:487-523`, reused in shape (seed → resolve wfId via a **static-literal** psql → `POST /start` requiring 204 → hold):

```powershell
        dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Phase "seeder failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 50 }

        # Static string literal query, no interpolated input (T-87-12 / the existing T-80-20 form).
        $wfIdRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA `
                  -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"
        $wfIdExit = $LASTEXITCODE
        $wfId = ("$wfIdRaw").Trim()
        if ($wfIdExit -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) { ... exit 50 }

        $startBody = ConvertTo-Json @($wfId)   # forces the JSON array even for a single id
        $startResp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
                       -ContentType 'application/json' -Body $startBody -TimeoutSec 20 -ErrorAction Stop
        if ($startResp.StatusCode -ne 204) { ... exit 50 }
```

**Diagnostic-only proxy query** — `:220-225`, reused verbatim in shape. Record the value beside the rendered one; it never decides the verdict (locked constraint 2):

```powershell
    function Invoke-ProxyRangeQuery([string]$Query, [int]$StartUnix, [int]$EndUnix, [int]$StepSeconds) {
        return Invoke-RestMethod -Method Post -Uri "$proxy/api/v1/query_range" `
                 -Headers $auth -ContentType 'application/x-www-form-urlencoded' `
                 -Body @{ query = $Query; start = $StartUnix; end = $EndUnix; step = $StepSeconds } `
                 -TimeoutSec 90 -ErrorAction Stop
    }
```

**`$__rate_interval` derivation** — `:303-322`, read from the live datasource, never hardcoded. Phase 88 needs the same number to state its smearing window in the artifact (`RateIntervalSeconds`):

```powershell
    function Get-RateIntervalSeconds([int]$TimeIntervalSeconds, [int]$StepSeconds) {
        return [Math]::Max(4 * $TimeIntervalSeconds, $StepSeconds + $TimeIntervalSeconds)
    }
    function ConvertFrom-GrafanaDuration([string]$Text) {
        if ([string]::IsNullOrWhiteSpace($Text)) { return 0 }
        if ($Text -match '^\s*(\d+)\s*([smh]?)\s*$') {
            $n = [int]$Matches[1]
            switch ($Matches[2]) { 'm' { return $n * 60 } 'h' { return $n * 3600 } default { return $n } }
        }
        return 0
    }
```

**Verdict precedence + artifact-before-exit** (`:822-922`) — the single most important pattern to preserve. `Fail` beats `Inconclusive`; the artifact is written **before** the exit code is resolved; the exit code comes from the **same in-memory object**:

```powershell
    $unevaluable = @()
    if ($SkipTraffic)    { $unevaluable += 'ClassAPanelsNonEmpty (-SkipTraffic)' }

    $claims = [ordered]@{ ... }     # one bool per claim

    # PRECEDENCE: Fail BEATS Inconclusive. A claim that was evaluated and came back FALSE is
    # positive evidence of a defect ... Inconclusive now means only "nothing failed, but something
    # could not be checked".
    $verdict = 'Pass'
    if (@($claims.Values | Where-Object { -not $_ }).Count -gt 0) {
        $verdict = 'Fail'
    } elseif ($unevaluable.Count -gt 0) {
        $verdict = 'Inconclusive'
    }
    ...
    $report | ConvertTo-Json -Depth 5 | Set-Content -Path $reportPath -Encoding utf8
    Write-Phase "verdict artifact: $reportPath" 'Green'
    # Resolve the exit code from the SAME in-memory object the artifact was written from.
    $exitCode = Resolve-AnalyzerExitCode $report
    $verdictClass = (Resolve-SweepClass $exitCode).Class
    exit $exitCode
```

Phase-88 note: a panel that could not be **read** (`NoData` where the prediction was a number, or the reader threw) is `Inconclusive`. A panel read successfully whose value did **not** move in the predicted direction is `Fail`. Pitfall 6 (`88-RESEARCH.md:479-482`) makes this sharp: `NoData` is a *state*, and where it is the predicted direction it counts as **moved**, not as an unread panel.

---

### `scripts/phase-88-panel-read.js` (utility / DOM reader, transform) — **NO ANALOG**

The repo contains **zero** JavaScript files and no script invokes `node` (verified: `grep -rn "playwright\|node run.js\|npx " scripts/` → no matches). Build from `88-RESEARCH.md:508-536` plus the skill's own contract, which I read this session at `C:\Users\UserL\.claude\plugins\cache\playwright-skill\playwright-skill\4.1.0\skills\playwright-skill\run.js`. Four load-bearing facts the planner must design around:

1. **`run.js` prints banners to stdout before the script runs** — `🎭 Playwright Skill - Universal Executor`, `📄 Executing file: <path>`, `🚀 Starting automation...`. A PowerShell caller therefore **cannot** `ConvertFrom-Json` the whole stdout. Emit a sentinel-delimited single line and extract it:
   ```powershell
   $raw  = & node (Join-Path $skillDir 'run.js') $scriptPath 2>&1 | Out-String
   $line = ($raw -split "`n" | Where-Object { $_ -match '^##PANEL-JSON##' } | Select-Object -First 1)
   if (-not $line) { ... }   # reader produced nothing -> Inconclusive, not Fail
   $panel = ($line -replace '^##PANEL-JSON##\s*','') | ConvertFrom-Json
   ```
2. **`run.js` accepts any existing file path** (`if (args.length > 0 && fs.existsSync(args[0]))`), so the reader can live in the repo as the source of truth. It copies the code into `.temp-execution-<ts>.js` **inside the skill dir** and `require()`s it — that is the tool's own doing, not ours. The repo rule (research § Security, "Playwright script written into the skill directory → Tampering") is satisfied as long as *we* never author into the skill dir. Decide explicitly in the plan: keep `scripts/phase-88-panel-read.js` checked in and pass its absolute path, or stage a copy under `$env:TEMP`. The checked-in copy is required for repeatability; a `/tmp`-only script cannot be re-run by a future reader.
3. **`run.js` does not wrap a script that already has `require(` + an async IIFE** — write the reader as a complete script so it runs unmodified, and let `process.exit(1)` propagate on failure.
4. **Env vars pass straight through** (same process env, `process.chdir(__dirname)` only changes cwd) — so the `GRAFANA_URL` / `PANEL_ID` / `FROM_MS` / `TO_MS` in-contract is sound, but **screenshot paths must be absolute** because cwd is the skill dir, not the repo.

The reader body, from `88-RESEARCH.md:511-535` (verified selectors from the Grafana v12.3.9 source tag):

```javascript
const GRAFANA = process.env.GRAFANA_URL || 'http://127.0.0.1:3000';
const PANEL_ID = process.env.PANEL_ID;      // e.g. '9'
const FROM_MS  = process.env.FROM_MS;       // absolute epoch ms — NEVER now-1h
const TO_MS    = process.env.TO_MS;         // same width for baseline and after

const page = await context.newPage();
await page.setViewportSize({ width: 1920, height: 1080 });   // pins maxDataPoints -> step -> $__rate_interval

const url = `${GRAFANA}/d/skp-business/?viewPanel=panel-${PANEL_ID}`
          + `&from=${FROM_MS}&to=${TO_MS}`
          + `&var-source=All&var-pod=All&kiosk&refresh=`;
await page.goto(url, { waitUntil: 'networkidle' });
await page.waitForSelector('[data-testid="Panel loading bar"]', { state: 'detached' }).catch(() => {});
const content = page.getByTestId('data-testid panel content');
const text = await content.innerText();     // stat: the big number
                                            // timeseries: legend rows "name\tMean\tMax"
```

Dashboard facts the reader depends on, re-verified against `k8s/dashboards/business.json` this session: `"uid": "skp-business"` (`:2`), `"refresh": "30s"` (`:11`) and `"time": { "from": "now-1h", "to": "now" }` (`:12`) — hence `&refresh=` and absolute `from`/`to` are mandatory, not stylistic. All eight timeseries panels carry `"legend": { "displayMode": "table", "placement": "bottom", "calcs": ["mean", "max"] }` (`:74, 105, 136, 205, 345, 368, 391, 414`) and all six stats carry `"textMode": "auto"` + `"graphMode": "none"` (`:179-181, 241-243, 280-282, 319-321`) — **no dashboard edit is needed to make the panels machine-readable**.

---

### `scripts/phase-88-wave0-probe.ps1` (test / live probe, request-response)

**Analog:** `scripts/phase-87-dashboards-verify.ps1` for everything it touches (PRE gate `:367-394`, forward `:163-197`, proxy `:203-225`), plus `scripts/phase-80-harness.ps1:379-397` for the scale-0 + terminate-wait it needs for probe A1.

**Shape precedent for a small, fail-closed, single-purpose live control:** `scripts/phase-80-collector-crash.ps1:12-27` — a lettered flow in the header and an explicit statement of what a PASS does and does **not** prove:

```powershell
    Flow (fail-closed — own exit 0 ONLY if the control genuinely fired):
      A up → B reset → C seed → D activate (204)
      E  BASELINE (collector UP): confirm terminal Step_G blobs are being produced (workflow is live)
      ...
    NOTE: this proves a bounded, existential claim (a CLEAN collector death, at this load, did not corrupt the
    sampled executions) — a regression tripwire, NOT a universal proof.
```

The probe answers four questions and must record **each answer independently** (A1 keeper recovery traffic, A2 `viewPanel=panel-<id>`, A3 `innerText` parseability on one stat + one table-legend timeseries, A4 a safe WebApi 5xx). A single Pass/Fail verdict would collapse four independent decisions; emit a `phase-88-wave0-probe.json` with one field per question and let the plan branch on the fields.

---

### `analyzer-reports/phase-88-<scenario-id>.json` (data artifact / single proof)

**Analog:** `analyzer-reports/phase-83-ha.json` — the exact schema: one object, PascalCase keys, `ScenarioId` first, `Verdict` second, then one boolean per claim, then measured scalars and round-trip (`"o"`) timestamps, `HumanSummary` last:

```json
{
  "ScenarioId": "phase-83-ha",
  "Verdict": "Pass",
  "ZeroDuplicate": true,
  "GapObserved": true,
  "NotBackfilled": true,
  "RoleFlipVisible": true,
  "BoundedRecovery": true,
  "RecoverySeconds": 15.4390265,
  "KillUtc": "2026-07-19T05:25:27.1969735+00:00",
  "WindowStart": "2026-07-19T05:24:35.3778727+00:00",
  "WindowEnd": "2026-07-19T05:27:27.2129457+00:00",
  "SendCorrIdCount": 4,
  "BucketCount": 4,
  "HumanSummary": "HA-07 verdict=Pass: all five HA-07 claims green ... [zeroDuplicate=True, ...]"
}
```

**Extension precedent** — `phase-87-dashboards-verify.ps1:864-908` shows how the same schema carries diagnostic arrays so every failure mode is nameable from the artifact alone (`EmptyClassAExprs`, `ClassBExprs`, `MissingDropdownPods`, `OldMethodFalsePassExprs`), plus the "how it verified" block (`QueryMode`, `QueryStepSeconds`, `RateIntervalSeconds`, `DatasourceTimeInterval`). Phase 88's equivalent "how it verified" block is `ViewportWidth`/`Height` + `RateIntervalSeconds` + `BaselineWindowStart`/`End` + `AfterWindowStart`/`End`; its diagnostic arrays are `BaselineValues[]`, `AfterValues[]`, `CrossTalkPanels[]`, `RolloutOldInstanceIds`/`RolloutNewInstanceIds`. Full field list at `88-RESEARCH.md:425`.

Writing pattern (`:910-914`) — `New-Item -Force` the directory, `ConvertTo-Json -Depth 5`, `Set-Content -Encoding utf8`:

```powershell
    $reportDir = Join-Path $repoRoot 'analyzer-reports'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $reportPath = Join-Path $reportDir 'phase-87-dashboards.json'
    $report | ConvertTo-Json -Depth 5 | Set-Content -Path $reportPath -Encoding utf8
```

**Depth caution:** `-Depth 5` suffices for phase-87's flat arrays. Phase 88's `CrossTalkPanels[]` (objects inside an array inside the report) and `BaselineValues[]` per series push past that — use `-Depth 10` and verify no `System.Object[]` string appears in the written file.

---

### `analyzer-reports/phase-88-discrimination.json` (data artifact / roll-up)

**Analog:** `analyzer-reports/phase-81-summary.json` — a JSON **array**, camelCase keys, one row per unit of proof, `verdict`/claims/measured counts/`harnessExit`/`class`:

```json
[
  { "scenarioId": "TEST-01", "verdict": "Pass", "zeroMissing": true, "effectOnce": true,
    "startedRuns": 18, "completeRuns": 18, "harnessExit": 0, "class": "PASS" },
  ...
]
```

`phase-85-summary.json` shows the degraded row shape worth copying for an unproven panel: `"verdict": "NO_REPORT"`, claims `false`, counts `null`, `"class": "VERDICT_FAIL"` — i.e. a missing proof is a *row*, never an absence.

**The one deliberate divergence:** phase-88's roll-up is keyed by **panel** (14 rows), not by scenario — `panelId`, `panelTitle`, `regime`, `baselineBand`, `observedStates[]`, `provenBy` (scenario id or `null`), `status` (`Proven` / `AcceptedUnproven`), `reason`, `misleadingByDefault`. This array *is* the DISC-02 answer and the HAND-02 input.

**Writer pattern:** `scripts/phase-81-sweep.ps1:95-150` — invoke each scenario as a **separate child process** so its `exit N` surfaces as `$LASTEXITCODE` without fail-fasting the loop; classify through the shared lib; **never re-score**:

```powershell
    foreach ($id in $Ids) {
        & pwsh -File (Join-Path $PSScriptRoot 'phase-80-harness.ps1') -ScenarioId $id -SkipBringUp
        $code = $LASTEXITCODE
        $resolved = Resolve-SweepClass $code
        # The wrapper only READS + tabulates the analyzer's already-computed values — it NEVER re-scores.
        $rows += [pscustomobject]@{
            scenarioId = $id; verdict = if ($json) { $json.Verdict } else { 'NO_REPORT' }
            ...; harnessExit = $code; class = $resolved.Class
        }
    }
    $rows | Format-Table -AutoSize | Out-String | Write-Host
    $rows | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding utf8
    $passCount = (@($rows | Where-Object { $_.harnessExit -eq 0 }).Count)
    Write-Phase "CAPSTONE: $passCount/$total PASS" $(if ($passCount -eq $total) { 'Green' } else { 'Red' })
```

Phase-88 caveat: the panel-keyed roll-up **is** a join (one scenario can prove several panels; one panel can be touched by several scenarios' cross-talk lists). Keep the "never re-score" rule by having each row cite the scenario artifact it read its values from — a `provenBy` id plus the values copied, never recomputed.

---

### `.planning/phases/88-*/88-FINDINGS.md` (documentation / accepted-limitations register)

**Analog:** `.planning/phases/87-grafana-observability-dashboards/87-FINDINGS.md` — exact structure, and the register HAND-04 needs is the same genre.

Frontmatter + evidence preamble (`:1-25`):

```markdown
---
phase: 87
slug: grafana-observability-dashboards
date: 2026-07-27
updated: 2026-07-28
status: accepted
requirements: [RTD-01, RTD-02, BPD-02, DASH-04, VAR-02, VAR-04, VER-01]
findings: [F-1, F-2, F-3, F-4, F-5, F-6, F-7]
evidence: analyzer-reports/phase-87-dashboards.json
---

# Phase 87 — Accepted Limitations

Measured evidence throughout is the live run of `scripts/phase-87-dashboards-verify.ps1`
against the `skp` Docker-Desktop cluster, window `2026-07-27T19:18:03Z` → `19:20:03Z`,
recorded in `analyzer-reports/phase-87-dashboards.json`: **Verdict `Pass`, all ten claim
booleans true, 36 target expressions = 30 Class A + 6 Class B, `EmptyClassAExprs` empty.**
```

Per-record shape (`:28-72`) — four fixed headings, each answering one question: **Asked for** → **What actually exists** → **What shipped instead** → **Why it is legitimate rather than a shortfall**, closing with **Measured evidence:** citing the artifact. Phase 88's register rows map onto it directly: *Asked for* = the panel's DISC-02 obligation; *What actually exists* = the observed states; *What shipped instead* = the log-corroboration route; *Why legitimate* = the user lock (panel 7) or the probe result (panel 13). The file closes with a **Requirement traceability** section (`:706`).

---

### `.planning/phases/88-*/88-RUNBOOK.md` (documentation / operator runbook) — **PARTIAL ANALOG**

No runbook exists in this repo (`grep -rln "runbook" docs .planning` returns only planning prose, no operator document). Borrow only `87-FINDINGS.md`'s frontmatter and its evidence-citation discipline; the table shape (symptom → panel → action → proving scenario → measured before/after) is new. `88-RESEARCH.md:157` already contains a fully-drafted panel-7 row that should be lifted verbatim as the format exemplar — it demonstrates the required tone: what to open, what to search, what to expect, and an explicit credibility caveat.

Open question the planner must resolve (research OQ 6, `:788-789`): the runbook lives under `.planning/` for authoring, but a maintenance department would never find it there — that is itself rubric question 6 and probably a HAND-02 blocking gap. Decide the destination in the plan, not during execution.

---

### `.planning/REQUIREMENTS.md` (MODIFY — config / requirements register)

**Analog:** itself. A new family block in the style of `:24-28`, then rows appended to the traceability table at `:67-86`:

```markdown
### BPD — Business / Pipeline Dashboard — Phase 87

- [x] **BPD-01**: One dashboard covers the three pipeline services' domain counters: ...
```

```markdown
## Traceability

| REQ-ID | Phase | Status |
|--------|-------|--------|
| VER-01 | 87 | Complete |
| VER-02 | 87 | Complete |
```

Phase 88 adds `### DISC — Panel Discrimination — Phase 88` and `### HAND — Handoff Readiness — Phase 88`, with `- [ ]` boxes and one traceability row per id. All eleven proposed statements are pre-written at `88-RESEARCH.md:98-110` — copy them rather than re-deriving. Amendment style, if a probe falsifies a premise: the italic inline `*(amended <date>, OQ-n / research F-n)*` marker used at `:27` and `:35`, plus a dated note at the top of the file (`:7`).

---

### `k8s/dashboards/business.json` (CONDITIONAL MODIFY — the only permitted product-file change)

**Analog:** itself. Permitted **only** if a scenario exposes a genuinely wrong expression (locked decision 1). The research's own recommendation for the one known candidate — panel 5's empty-instead-of-spike behaviour — is **record it in HAND-02, do not fix it** (`88-RESEARCH.md:781-783`), because `87-FINDINGS.md §10` establishes the `processorId` grouping is correct as shipped and the emptiness is a rendering consequence, not a wiring error.

If the change is made, the gate is `pwsh -File scripts/phase-87-dashboard-lint.ps1` (23 rules, ~5 s, hermetic) — in particular rules **C3** (forces `or vector(0)` on every Class-B counter) and **C7** (forbids it on every Class-A counter), which are what let `phase-87-dashboards-verify.ps1`'s classifier work with no exception list. Adding or removing a guard silently re-classes a panel; the lint gate is the control.

---

## Shared Patterns

### Every harness in this repo

**Source:** `scripts/phase-87-dashboards-verify.ps1`, `scripts/phase-83-ha-failover.ps1`, `scripts/phase-80-harness.ps1`, `scripts/phase-81-sweep.ps1`
**Apply to:** `phase-88-panel-discriminate.ps1`, `phase-88-wave0-probe.ps1`, `phase-88-sweep.ps1`

| Convention | Where proven |
|---|---|
| `$ErrorActionPreference = 'Stop'` + `Set-StrictMode -Version Latest` | `phase-87-dashboards-verify.ps1:126-127` |
| `Push-Location $repoRoot` + whole-body `try`/`finally` teardown | `:134` / `:924-939` |
| Prefixed `Write-Phase` trace helper, repo colour convention | `:141-143`, `phase-80-harness.ps1:103-105`, `phase-81-sweep.ps1:66-68` |
| Dot-source `scripts/lib/exit-code-resolution.ps1`; never an inline verdict switch | `:151`, `phase-80-harness.ps1:112`, `phase-81-sweep.ps1:73` |
| Any variable the outer `finally` reads is declared **before** the `try` | `:130-132` |
| Pin `$LASTEXITCODE` into a variable **before** any `.Trim()` | `:370-373`, `phase-80-harness.ps1:198-202` |
| `@()`-wrap before `.Count`; **assign then wrap** for `Invoke-RestMethod` array results | `:444-448`, `:589-590` |
| No `jq`, no external JSON tooling — `ConvertFrom-Json` only | `:118` |
| Static string-literal `psql -c`, never interpolated | `:495` |
| Distinct exit code per infra abort, documented one row per code | `:73-85` |

### Exit-code resolution (never reimplemented)

**Source:** `scripts/lib/exit-code-resolution.ps1:28-58`
**Apply to:** every Phase-88 script that emits a verdict

```powershell
function Resolve-AnalyzerExitCode {
    param([Parameter(Mandatory)] $Report)
    $verdict = "$($Report.Verdict)"
    switch ($verdict) {
        'Inconclusive' { return 2 }
        'Fail'         { return 1 }   # a FAIL is NEVER mislabelled INCONCLUSIVE
        'Pass'         { return 0 }
        default        { return 1 }   # unknown/absent verdict is fail-closed (never a false green)
    }
}
```

The fail-closed `default` is the T-87-18 control. An inline `switch ($verdict)` loses it.

### StrictMode-safe property access

**Source:** `scripts/phase-87-dashboards-verify.ps1:244-249`
**Apply to:** any Phase-88 code that parses `business.json` or a Playwright JSON payload

```powershell
    # `$o.PSObject.Properties.Name` is itself unsafe: that is MEMBER ENUMERATION over a collection,
    # and on an EMPTY collection (a `{}` in the JSON — several panel `options`/`custom` blocks are
    # empty) StrictMode throws "The property 'Name' cannot be found on this object".
    function Get-PropertyNames($Object) {
        $names = @()
        if ($null -eq $Object) { return $names }
        foreach ($prop in $Object.PSObject.Properties) { $names += $prop.Name }
        return $names
    }
```

This trap has bitten the repo three times. Copy the helper; do not re-derive it.

### Per-commit static gate

**Source:** `.planning/phases/87-grafana-observability-dashboards/87-05-PLAN.md:140-147`, results at `87-05-SUMMARY.md:113-114`
**Apply to:** every task that touches a Phase-88 script

```powershell
pwsh -NoProfile -Command "$null = [ScriptBlock]::Create((Get-Content scripts/phase-88-panel-discriminate.ps1 -Raw)); 'parse ok'"
```

with `Select-String` guard assertions in the acceptance criteria:

```
- Select-String -Path <script> -SimpleMatch '.k8s-portforward-pids'  returns NO match
- Select-String -Path <script> -SimpleMatch '--address'              MATCHES
- Select-String -Path <script> -SimpleMatch '0.0.0.0'                returns NO match
- Select-String -Path <script> -SimpleMatch 'docker compose'         returns NO match
```

Phase-88 additions, each mapping to a hazard the research named: `-SimpleMatch 'TierReplicas'` returns no match (Pitfall 4); `-SimpleMatch 'apply -k'` returns no match (stale-image hazard); `-SimpleMatch 'now-1h'` returns no match in the reader (Pitfall 1); `-SimpleMatch 'KEEPER_DEFEAT_REINJECT-'` matches (the disarm path exists at all); `-SimpleMatch 'css-'` returns no match (Pitfall 10).

---

## Anti-Patterns — verified in the repo, do not copy

### 🔴 `$TierReplicas` — a stale map that would silently dismantle HA

**Source:** `scripts/phase-80-harness.ps1:148-150`

```powershell
    $TierReplicas = @{
        'processor-sample' = 2; 'orchestrator' = 1; 'keeper' = 2; 'redis' = 1; 'rabbitmq' = 1
    }
```

`orchestrator = 1` is stale: `k8s/31-orchestrator.yaml:41` sets `replicas: 3` (raised by Phase 83, HA-06) and the live deployment confirms 3. Copying this map restores the orchestrator to **1 and never puts it back**, quietly undoing the previous milestone's HA — quietly, because a single orchestrator still works. Use `Get-LiveReplicas` (above) and assert the restored count as an explicit claim. `$TierKind` at `:144-147` is still correct and may be reused.

### 🔴 Compose-era seam scripts as an *arming* reference

`scripts/phase-79-falsify.ps1` mentions `KEEPER_DEFEAT_REINJECT` only in doc comments (`:12`, `:51`). `scripts/phase-67-harness.ps1` really does export both seams (`:135`, `:142`) — but **both files call `kubectl` zero times** (verified). Their mechanism is `docker compose up` interpolation, which cannot reach a k8s pod. Take the *discipline* (`:130-167`, `:617`), never the mechanism, and never `docker compose` anything ([[stack-bringup-k8s-only]]).

### 🔴 `kubectl apply -k k8s/`

Reverts all four app Deployments from the live `:tags-const-1544` images to the manifests' `:local` pins, and the stale bits emit **no `source` resource attribute** — every dashboard expression filters on `source`, so a stale-image cluster makes correct dashboards look broken (`87-FINDINGS.md §9`, measured). Never in this phase.

### 🔴 Proving the query instead of the panel

`phase-87-dashboards-verify.ps1:39-50` is the repo's own confession: an instant query with `$__rate_interval` hardcoded to `5m` reported 30/30 Class A green against three panels rendering "No data". Phase 88's locked constraint 2 exists to stop the recurrence — the DOM is the verdict; the proxy value is recorded beside it as `DiagnosticQueryValue` with a `DiagnosticAgreesWithPanel` flag, and a disagreement is a **finding**, not something to reconcile away.

### 🔴 Emotion class selectors and pixel diffing

`.css-1a2b3c` are build hashes; screenshot diffing is defeated by anti-aliasing and axis auto-scale. `data-testid` only, from the v12.3.9 table at `88-RESEARCH.md:196-203`. Screenshots are evidence, never a verdict.

---

## No Analog Found

| File | Role | Data Flow | Reason |
|---|---|---|---|
| `scripts/phase-88-panel-read.js` | utility / DOM reader | transform | The repo contains no JavaScript at all and no script invokes `node`. Build from `88-RESEARCH.md:508-536` + the `run.js` contract documented above (banner stdout, `require()`-into-skill-dir, env pass-through, absolute screenshot paths) |
| `.planning/phases/88-*/88-RUNBOOK.md` | documentation | — | No operator runbook exists in this repo. Only the frontmatter + evidence-citation discipline of `87-FINDINGS.md` transfers; the symptom → panel → action table shape is new. Exemplar row already drafted at `88-RESEARCH.md:157` |
| the k8s seam arm/disarm mechanism | config mutation | — | Genuinely new (roadmap SC 5 calls it a deliverable). Only the *discipline* has a precedent (`phase-67-harness.ps1:130-167, :617`); the `kubectl set env` mechanism is authored from `88-RESEARCH.md:562-580` |

---

## Notes for the Planner

1. **Sequencing.** The research's own recommendation (OQ 5) is to run the zero-risk HTTP scenarios first so the Playwright reader is shaken out before any destructive fault. That makes the reader's analog-free status the phase's schedule risk, not the seam's.
2. **Wave 0 is genuinely blocking.** Three of the four probe answers change the plan structure: A1 decides whether the panel-2 scenario needs one seam or two (an extra rollout + re-baseline in the highest-value scenario); A2/A3 decide the reader's primary locator; A4 decides whether panel 13 is proven or joins the register. Do not lock scenario plans before the probe artifact exists.
3. **One correction to carry forward.** `88-RESEARCH.md:378` and `:742` state that neither compose-era script exports the seams. `phase-67-harness.ps1:135,142` does. The operative fact — no `kubectl` path exists — is unchanged, but the plan should cite `:130-167` + `:617` as the arm-early / clear-in-`finally` precedent rather than asserting no precedent exists.
4. **`-Depth 5` will not survive phase 88's artifact.** Every existing writer uses it; phase-88's nested `CrossTalkPanels[]` and per-series value arrays need `-Depth 10`, with a written-file check that no `System.Object[]` literal appears.

## Metadata

**Analog search scope:** `scripts/`, `scripts/lib/`, `analyzer-reports/`, `k8s/`, `k8s/dashboards/`, `.planning/phases/87-*/`, `.planning/REQUIREMENTS.md`, the Playwright skill at `C:\Users\UserL\.claude\plugins\cache\playwright-skill\playwright-skill\4.1.0\skills\playwright-skill`
**Files read in full:** `scripts/phase-87-dashboards-verify.ps1` (939), `scripts/lib/exit-code-resolution.ps1` (58), `analyzer-reports/phase-83-ha.json`, the skill's `run.js`
**Files read in targeted ranges:** `scripts/phase-80-harness.ps1` (:95-205, :358-432), `scripts/phase-81-sweep.ps1` (:60-170), `scripts/phase-67-harness.ps1` (:128-169, :606-625), `scripts/phase-80-collector-crash.ps1` (:1-70), `87-FINDINGS.md` (:1-75), `87-PATTERNS.md` (:1-30), `.planning/REQUIREMENTS.md` (:24-43, :67-88)
**Pattern extraction date:** 2026-07-28
