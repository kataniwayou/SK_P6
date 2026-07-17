<#
.SYNOPSIS
    Phase 80 — dynamic OBSERVABILITY-CRASH negative control (companion to the Phase-79 gate-teeth control).

.DESCRIPTION
    Proves EMPIRICALLY that crashing the observability stack (the OTEL collector) loses telemetry but does NOT
    affect production workflow execution. You cannot witness this with the witness you just killed, so the
    proof uses an OUT-OF-BAND ground-truth oracle: the workflow's terminal EFFECT in Redis L2 —
    `skp:out:{messageId}` blobs, which are the completed Step_G outputs (`{"number":106|206,"label":"Step_G"}`
    for the two seed chains 100+6 / 200+6). Read straight from Redis, independent of ES/Prometheus/OTEL.

    Flow (fail-closed — own exit 0 ONLY if the control genuinely fired):
      A up → B reset → C seed → D activate (204)
      E  BASELINE (collector UP): confirm terminal Step_G blobs are being produced (workflow is live)
      F  CRASH:   docker compose stop otel-collector; assert it is actually down (docker state)
      G  DWELL:   collector stays down across >=2 cron fires (executions run while telemetry is dark)
      H  ORACLE:  NEW terminal Step_G blobs (106/206) appeared in Redis during the outage → execution unaffected
      I  RESTORE: docker compose start otel-collector; wait healthy
      J  GAP:     the ES record count for the outage window is ~0 vs a live pre-crash window → the crash was REAL

    PASS (exit 0) iff: workflow was live AND new completions happened while the collector was down AND the
    collector was confirmed down AND a telemetry gap exists. Any deviation → non-zero (fail-closed).

    NOTE: this proves a bounded, existential claim (a CLEAN collector death, at this load, did not corrupt the
    sampled executions) — a regression tripwire, NOT a universal proof. It does NOT exercise a slow/half-up
    collector (backpressure) — pair with the ObservabilityDecouplingFacts static guard.
#>

$ErrorActionPreference = 'Stop'
function Say([string]$m, [string]$c = 'Cyan') { Write-Host "[phase-80] $m" -ForegroundColor $c }

function Get-TerminalBlobCount {
    # Count skp:out:* blobs that decode to a terminal Step_G value (106 or 206) — one per completed execution.
    $keys = @(docker compose exec -T redis redis-cli --scan --pattern 'skp:out:*' 2>$null |
              ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $n = 0
    foreach ($k in $keys) {
        $v = (docker compose exec -T redis redis-cli GET $k 2>$null) -join ''
        if ($v -match '"label":"Step_G"' -and ($v -match '"number":106' -or $v -match '"number":206')) { $n++ }
    }
    $n
}
function Now-Ms { [long][DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
function Es-Count([long]$fromMs, [long]$toMs) {
    $body = "{""query"":{""range"":{""@timestamp"":{""gte"":$fromMs,""lt"":$toMs}}}}"
    try {
        $r = Invoke-RestMethod -Method Post -Uri 'http://localhost:9200/_count' `
                -ContentType 'application/json' -Body $body -TimeoutSec 15 -ErrorAction Stop
        [int]$r.count
    } catch { -1 }
}

$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    # ---- A: bring-up (images already current; no rebuild) -------------------------------------------------
    Say "STEP A: bring-up (docker compose up -d + health gate)"
    docker compose up -d 2>&1 | Out-Null
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'phase-65-up.ps1')
    if ($LASTEXITCODE -ne 0) { Say "bring-up failed." 'Red'; exit 10 }

    # ---- B: reset (FLUSHALL + heal-wait + graph delete) --------------------------------------------------
    Say "STEP B: reset"
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'phase-65-reset.ps1')
    if ($LASTEXITCODE -ne 0) { Say "reset failed." 'Red'; exit 20 }

    # ---- C: seed -----------------------------------------------------------------------------------------
    Say "STEP C: seed (FanOutSeeder)"
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Say "seed failed." 'Red'; exit 30 }

    # ---- D: clean orchestrator + activate ----------------------------------------------------------------
    Say "STEP D: restart orchestrator + activate (204)"
    docker compose restart orchestrator 2>&1 | Out-Null
    Start-Sleep -Seconds 8
    $wfId = (docker compose exec -T postgres psql -U postgres -d stepsdb -tA `
                -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'").Trim()
    if ([string]::IsNullOrWhiteSpace($wfId)) { Say "could not resolve wfId." 'Red'; exit 40 }
    $startBody = ConvertTo-Json @($wfId)
    $resp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
                -ContentType 'application/json' -Body $startBody -TimeoutSec 15 -ErrorAction Stop
    if ($resp.StatusCode -ne 204) { Say "activation expected 204, got $($resp.StatusCode)." 'Red'; exit 50 }
    $activateMs = Now-Ms

    # ---- E: BASELINE (collector UP) — confirm the workflow is actually completing executions -------------
    Say "STEP E: baseline window (collector UP, 50s) — confirm terminal Step_G output is being produced"
    Start-Sleep -Seconds 50
    $baselineOut = Get-TerminalBlobCount
    Say "  baseline terminal Step_G blobs = $baselineOut"
    if ($baselineOut -lt 1) { Say "workflow produced NO terminal output with the collector UP — cannot run the control." 'Red'; exit 60 }

    # ---- F: CRASH the collector, assert it is actually down (docker control plane) -----------------------
    Say "STEP F: CRASH — docker compose stop otel-collector"
    $crashMs = Now-Ms
    docker compose stop otel-collector 2>&1 | Out-Null
    Start-Sleep -Seconds 3
    $running = @(docker compose ps otel-collector --format json 2>$null |
                 Where-Object { $_ -match '\S' } | ForEach-Object { $_ | ConvertFrom-Json } |
                 Where-Object { $_.State -eq 'running' })
    if ($running.Count -ne 0) { Say "otel-collector did NOT stop — control invalid." 'Red'; exit 61 }
    Say "  otel-collector confirmed DOWN (docker state)." 'Yellow'

    # ---- G: DWELL (collector down across >=2 cron fires @ */30) -------------------------------------------
    Say "STEP G: dwell 80s with the collector DOWN (executions run while telemetry is dark)"
    Start-Sleep -Seconds 80

    # ---- H: ORACLE — new terminal completions during the blind window (read from Redis, not telemetry) ---
    $treatmentOut = Get-TerminalBlobCount
    $newDuringOutage = $treatmentOut - $baselineOut
    Say "STEP H: terminal Step_G blobs now = $treatmentOut (new during outage = $newDuringOutage)"
    if ($newDuringOutage -lt 1) { Say "NO execution completed while the collector was down — execution WAS affected (or stalled). FAIL." 'Red'; $verdictEffect = $false }
    else { $verdictEffect = $true }

    # ---- I: RESTORE ---------------------------------------------------------------------------------------
    Say "STEP I: restore — docker compose start otel-collector"
    $restoreMs = Now-Ms
    docker compose start otel-collector 2>&1 | Out-Null
    Start-Sleep -Seconds 25   # let it come back + one scrape/export cycle land in ES

    # ---- J: TELEMETRY GAP — prove the crash was real (ES count during outage ~0 vs a live pre-crash win) --
    $esBaseline = Es-Count $activateMs $crashMs
    $esOutage   = Es-Count ($crashMs + 2000) ($restoreMs - 2000)
    $baseDur    = [double]($crashMs - $activateMs)
    $outDur     = [double]($restoreMs - $crashMs)
    $expected   = if ($baseDur -gt 0) { $esBaseline * ($outDur / $baseDur) } else { 0 }
    Say "STEP J: ES records — pre-crash window=$esBaseline, outage window=$esOutage (expected-if-no-gap ~$([int]$expected))"
    $verdictGap = ($esBaseline -gt 0) -and ($esOutage -ge 0) -and ($esOutage -lt [Math]::Max(5.0, $expected * 0.15))
    if (-not $verdictGap) { Say "no telemetry GAP during the outage — the crash was a no-op OR ES query failed. FAIL." 'Red' }

    # ---- VERDICT (fail-closed) ---------------------------------------------------------------------------
    if ($verdictEffect -and $verdictGap) {
        Say "OBSERVABILITY-CRASH CONTROL PASSED — collector was DOWN and telemetry GAPPED, yet $newDuringOutage execution(s) completed correctly during the blind window (Redis Step_G 106/206). Observability crash did NOT affect production." 'Green'
        exit 0
    } else {
        Say "OBSERVABILITY-CRASH CONTROL FAILED — effect:$verdictEffect gap:$verdictGap." 'Red'
        exit 1
    }
}
finally {
    # Always leave the collector running, whatever happened.
    docker compose start otel-collector 2>&1 | Out-Null
    Pop-Location
}
