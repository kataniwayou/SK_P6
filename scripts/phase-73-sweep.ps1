<#
.SYNOPSIS
    Phase 73 live sweep — seed the G-extended DAG, drive the live round-trip, invoke the RealStack
    auditor end-to-end, and roll up one phase-73-summary.json. Operator-runnable; DEFERRED-AUTOMATED.

.DESCRIPTION
    The single operator command for the Phase 73 end-to-end L2-delivery proof on the LIVE stack. A THIN
    run-the-stages-and-collect driver (NOT a re-implementation of any auditor step): it runs the three
    phase-73 stages in order, captures + classifies each child-process exit code, reads the per-scenario
    analyzer report the auditor already wrote, and emits one roll-up summary.

        pwsh -File scripts/phase-73-sweep.ps1                  # default scenario (TEST-01)
        pwsh -File scripts/phase-73-sweep.ps1 -ScenarioIds TEST-01   # operator re-run of a subset

    This phase has NO 7-scenario fault matrix — the goal is the value-chain + fan-in proof on the happy
    path, so the default is a SINGLE scenario (TEST-01). Run-all + collect, NO fail-fast: every selected
    scenario runs even if an earlier one is non-PASS, so a single sweep yields all results. Final exit is
    0 IFF all selected scenarios PASS (auditor exit 0), else non-zero.

    STAGES (D-13), per scenario:
        1  SEED   — seed the G-extended DAG (10 steps / 10 edges / 10 assignments, Step_G the lone sink,
                    every payload number=1) via `dotnet test --filter "FullyQualifiedName~FanOutSeeder"`.
        2  ROUNDTRIP — drive the live cron-fired DAG over the live stack end-to-end.
                    *** DEFERRED-AUTOMATED *** — the sandbox has NO Docker, so this stage is a documented
                    no-op placeholder operators wire to their live bring-up (phase-65-up / cron fire). The
                    phase does NOT close on this stage; it closes on hermetic GREEN (Plans 02/03) + 0-warning.
        3  AUDIT  — invoke the ES-read-only auditor via
                    `dotnet test --filter "Category=RealStack&FullyQualifiedName~Analyzer"` (the
                    AnalyzerE2ETests contract). The auditor reads attributes.Produced + the @timestamp
                    trip-duration span, asserts the value chain through Step_G x2 (the Step_G-at-seed+6 ES
                    log = the live terminal-anchor proxy), and writes analyzer-reports/{scenarioId}.json.

    EXIT-CODE CLASSIFICATION (the switch below maps each child `dotnet test` exit against this):
        0   stage PASS (green)
        1   stage FAIL (a REAL finding — the auditor verdict path or a seed assert) — NEVER auto-retried
        64  bad -ScenarioId argument (config-usage error)
        *   any other non-zero — INFRA_ABORT (bring-up / backend unreachable) — operator re-runnable

    FLAKE / RE-RUN POLICY: a verdict/seed FAIL (exit 1) is a real finding to investigate and is NEVER
    retried away; an INFRA_ABORT is operator-re-runnable by invoking the bare stage for that single id.
    This wrapper does NOT auto-retry anything.

    ANTI-PATTERNS (encoded as hard NOTs): the wrapper NEVER re-scores Missing/Duplicates/value-chain (it
    only reads + tabulates the analyzer's already-computed values), NEVER reads Prometheus deltas, NEVER
    auto-retries a stage, and NEVER touches the auditor/seeder machinery (it only invokes `dotnet test`
    with the hardcoded filters as child processes).

.NOTES
    Dev/ops-only tooling. No product source touched. DEFERRED-AUTOMATED: this script is syntactically
    runnable (parses) but is NOT a phase gate — the live Docker round-trip is not exercised in the sandbox.
    All filters are hardcoded; the only -ScenarioId values fed are the operator-supplied ids, which name
    only the per-scenario report file (no shell/psql/sql interpolation, no untrusted surface — T-73-08).
#>

param(
    [string[]]$ScenarioIds = @('TEST-01')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    # Prefixed console-trace helper (mirrors phase-68-sweep.ps1, re-prefixed).
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-73-sweep] $msg" -ForegroundColor $color
    }

    # Classify a child `dotnet test` exit code with no auto-retry on any class.
    function Get-ExitClass([int]$code) {
        switch ($code) {
            0       { 'PASS' }
            1       { 'FAIL' }          # real finding — NEVER auto-retried
            64      { 'BAD_ARG' }
            default { 'INFRA_ABORT' }   # bring-up / backend unreachable — operator re-runnable
        }
    }

    $Ids = @($ScenarioIds)
    Write-Phase "sweep over $($Ids.Count) scenario(s): $($Ids -join ', ')"

    $rows = @()

    foreach ($id in $Ids) {
        Write-Phase "=== scenario $id ==="

        # ---- STAGE 1: SEED the G-extended DAG (10/10/10, Step_G sink, number=1). ----
        # Invoked as a SEPARATE child process; its non-zero exit surfaces as $LASTEXITCODE WITHOUT
        # terminating this loop (NO fail-fast).
        Write-Phase "  [1/3] seed G-extended DAG (FanOutSeeder)"
        & dotnet test (Join-Path $repoRoot 'tests/BaseApi.Tests') --filter "FullyQualifiedName~FanOutSeeder"
        $seedCode = $LASTEXITCODE
        $seedClass = Get-ExitClass $seedCode
        Write-Phase "    seed -> exit $seedCode ($seedClass)" $(if ($seedCode -eq 0) { 'Green' } else { 'Yellow' })

        # ---- STAGE 2: drive the live round-trip (cron-fired DAG over the live stack). ----
        # *** DEFERRED-AUTOMATED *** — the sandbox has NO Docker, so this stage is a documented no-op
        # placeholder. Operators wire this to their live bring-up + cron fire; the phase does NOT close
        # on this stage (it closes on hermetic GREEN + 0-warning). NOT a phase gate.
        Write-Phase "  [2/3] live round-trip — DEFERRED-AUTOMATED (no Docker in sandbox; operator wires live bring-up)" 'DarkYellow'
        $roundTripClass = 'DEFERRED'

        # ---- STAGE 3: AUDIT — invoke the ES-read-only RealStack auditor. ----
        Write-Phase "  [3/3] audit (RealStack analyzer)"
        & dotnet test (Join-Path $repoRoot 'tests/BaseApi.Tests') --filter "Category=RealStack&FullyQualifiedName~Analyzer"
        $auditCode = $LASTEXITCODE
        $auditClass = Get-ExitClass $auditCode
        Write-Phase "    audit -> exit $auditCode ($auditClass)" $(if ($auditCode -eq 0) { 'Green' } else { 'Yellow' })

        # Per-scenario analyzer report discovery. The wrapper only READS + tabulates the analyzer's
        # already-computed values (Verdict / Missing / Duplicates / ValueChainOk / trip duration) — it
        # NEVER re-scores anything.
        $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$id.json" -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
        $json = if ($report) { Get-Content $report.FullName -Raw | ConvertFrom-Json } else { $null }

        # The scenario PASSES iff the seed and the audit both exited 0 (the round-trip stage is deferred).
        $scenarioPass = ($seedCode -eq 0) -and ($auditCode -eq 0)

        # AnalyzerReport fields surfaced: Verdict, Missing, Duplicates, ValueChainOk, TripDurationMsBy*.
        $rows += [pscustomobject]@{
            scenarioId   = $id
            seedExit     = $seedCode
            roundTrip    = $roundTripClass
            auditExit    = $auditCode
            verdict      = if ($json) { $json.Verdict } else { 'NO_REPORT' }
            zeroMissing  = if ($json) { ($json.Missing -eq 0) } else { $false }
            effectOnce   = if ($json) { (@($json.Duplicates).Count -eq 0) } else { $false }
            valueChainOk = if ($json -and $null -ne $json.ValueChainOk) { $json.ValueChainOk } else { $null }
            pass         = $scenarioPass
        }
    }

    # -----------------------------------------------------------------------
    # ROLL-UP — console table + machine artifact. NO new scoring: this only reads + tabulates each
    # per-scenario report plus the seed/audit exit codes.
    # -----------------------------------------------------------------------
    Write-Phase "=== phase-73 roll-up ==="
    $rows | Format-Table -AutoSize | Out-String | Write-Host

    $summaryDir = Join-Path $repoRoot 'analyzer-reports'
    New-Item -ItemType Directory -Force -Path $summaryDir | Out-Null
    $summaryPath = Join-Path $summaryDir 'phase-73-summary.json'
    $rows | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding utf8
    Write-Phase "roll-up artifact: $summaryPath" 'Green'

    # Single-glance verdict: all selected scenarios PASS <=> wrapper exit 0.
    $passCount = (@($rows | Where-Object { $_.pass }).Count)
    $total = $rows.Count
    Write-Phase "PHASE-73 SWEEP: $passCount/$total PASS" $(if ($passCount -eq $total) { 'Green' } else { 'Red' })

    # Final exit: 0 iff every selected scenario passed seed + audit, else non-zero.
    exit ($(if ($passCount -eq $total) { 0 } else { 1 }))

} finally {
    Pop-Location
}
