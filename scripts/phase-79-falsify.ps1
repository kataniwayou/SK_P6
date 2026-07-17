<#
.SYNOPSIS
    Phase 79 falsification driver (negative control) — prove the resilience-sweep gate has TEETH.

.DESCRIPTION
    The standalone meta-test with the INVERTED assertion. It invokes the already-proven capstone
    sweep for a single OUT-OF-BAND scenario:

        pwsh -File scripts/phase-79-falsify.ps1

    ...which runs `scripts/phase-68-sweep.ps1 -ScenarioIds FALSIFY-01` (one live run of the
    negative control through the sweep — the keeper env seam KEEPER_DEFEAT_REINJECT defeats exactly
    one reinject so the execution registers as recoverable-but-lost → binding miss). It then READS
    the already-written artifacts (the phase-68-summary.json roll-up row + the per-scenario analyzer
    report) and asserts, fail-closed, that the gate correctly went RED for the RIGHT reason (D-03/D-04):

        1. the FALSIFY-01 harness exit is EXACTLY 1 (VERDICT_FAIL — NOT 2/INCONCLUSIVE, NOT an
           infra abort 10-70/64) — D-03 #1,
        2. the analyzer report shows Missing >= 1 with a MissingDetail naming the
           recoverable-but-lost → binding miss reason — D-03 #2,
        3. the sweep roll-up row has verdict == "Fail" (class VERDICT_FAIL) AND the sweep wrapper
           exited non-zero — D-04.

    ANTI-PATTERNS (mirrors phase-68-sweep.ps1 — encoded as hard NOTs): this driver NEVER re-scores
    Missing/Duplicates (it only READS + tabulates the analyzer's already-computed values), NEVER
    re-runs the harness directly (the single sweep invocation surfaces BOTH the harness-exit level
    via the summary row AND the sweep-summary level), and dot-sources the SHARED
    scripts/lib/exit-code-resolution.ps1 (Resolve-SweepClass) rather than re-implementing the
    verdict-class map.

    STALE-REPORT GUARD (T-79-09): per-scenario report discovery sorts by LastWriteTime -Descending
    and takes the FRESHEST FALSIFY-01.json (the documented phase-68-sweep stale-Debug-report trap);
    the authoritative verdict is the summary harnessExit/verdict, analyzer-written before its assert.

    EXIT-CODE TABLE (phase-79-falsify — the driver's OWN exit; the assertion is INVERTED vs phase-67):
        0   NEGATIVE CONTROL PASSED — the gate correctly FAILED FALSIFY-01 for the recoverable-but-lost reason:
            sweep wrapper exited non-zero AND summary row verdict=='Fail' AND harnessExit==1 AND report Missing>=1
            AND MissingDetail names "recoverable-but-lost" + "binding miss".
        1   NEGATIVE CONTROL FAILED — silent-green False Negative OR wrong-reason red (harnessExit!=1, verdict!='Fail',
            Missing<1, or MissingDetail missing the binding-miss substrings). This is a GATE-TEETH regression.
        2   INCONCLUSIVE — FALSIFY-01 harnessExit==2 (observability-degraded). Re-run deliberately vs warm ES; NO auto-retry.
        64  BAD_ARG passthrough (harnessExit==64).
        60/other  INFRA_ABORT passthrough (harnessExit 10-70) — infrastructure step failed, operator re-runnable.

    Per D-03 the assertion requires the harness exit be EXACTLY 1 — NOT merely non-zero — so an
    INCONCLUSIVE (2) or an infra abort (10-70/64) can NEVER masquerade as a passing negative control.

.NOTES
    Dev/ops-only tooling. No product source touched. No untrusted input reaches this driver — the only
    id fed to the sweep is the hardcoded 'FALSIFY-01' (no shell/psql interpolation). The keeper seam it
    drives is default-off and env-gated; the sweep exports KEEPER_DEFEAT_REINJECT only for this scenario.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    # Prefixed console-trace helper (mirrors phase-68-sweep.ps1, re-prefixed).
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-79-falsify] $msg" -ForegroundColor $color
    }

    # Dot-source the SHARED exit-code resolution lib (the same one the harness + sweep consume) so the
    # driver reads the SAME verdict-class map (0 PASS / 1 VERDICT_FAIL / 2 INCONCLUSIVE / 64 BAD_ARG /
    # else INFRA_ABORT) instead of re-implementing it. Pure functions, no side effects.
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')

    # 1. Invoke the sweep for the single out-of-band scenario (D-04). A SEPARATE child `pwsh -File`
    #    process, so the sweep's `exit N` surfaces here as $LASTEXITCODE. The default sweep id list
    #    stays byte-unchanged — FALSIFY-01 is passed explicitly, never in the default capstone.
    Write-Phase "invoking phase-68-sweep.ps1 -ScenarioIds FALSIFY-01 (single-scenario negative control)"
    & pwsh -File (Join-Path $PSScriptRoot 'phase-68-sweep.ps1') -ScenarioIds 'FALSIFY-01'
    $sweepExit = $LASTEXITCODE
    Write-Phase "sweep wrapper exit = $sweepExit"

    # 2. Read the sweep roll-up summary and pull the FALSIFY-01 row (harnessExit + verdict + class).
    #    NEVER re-score — the sweep already tabulated the analyzer's computed values.
    $summaryPath = Join-Path $repoRoot 'analyzer-reports/phase-68-summary.json'
    if (-not (Test-Path $summaryPath)) { Write-Phase "sweep summary artifact missing ($summaryPath)." 'Red'; exit 60 }
    $summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
    $row = @($summary) | Where-Object { $_.scenarioId -eq 'FALSIFY-01' } | Select-Object -First 1
    if (-not $row) { Write-Phase "no FALSIFY-01 row in sweep summary." 'Red'; exit 60 }
    $harnessExit = [int]$row.harnessExit
    Write-Phase "FALSIFY-01: harnessExit=$harnessExit, verdict=$($row.verdict), class=$($row.class)"

    # 3. INCONCLUSIVE / infra passthrough BEFORE the pass/fail decision (D-03: exit 1 must be EXACT — a 2
    #    or an infra abort must NOT masquerade as a passing control; fail-closed, never a false green).
    if ($harnessExit -eq 2) { Write-Phase "FALSIFY-01 INCONCLUSIVE (observability-degraded); re-run vs warm ES. NOT a passing negative control." 'Yellow'; exit 2 }
    if ($harnessExit -eq 64) { Write-Phase "FALSIFY-01 BAD_ARG." 'Red'; exit 64 }
    if ($harnessExit -ne 0 -and $harnessExit -ne 1) { Write-Phase "FALSIFY-01 INFRA_ABORT (exit $harnessExit)." 'Red'; exit $harnessExit }

    # 4. Locate the FRESHEST per-scenario analyzer report (T-79-09 stale-report guard: sort on
    #    LastWriteTime -Descending) and read Missing + MissingDetail — the RIGHT-reason evidence (D-03 #2).
    $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter 'FALSIFY-01.json' -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match 'analyzer-reports' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $report) { Write-Phase "analyzer report FALSIFY-01.json not found." 'Red'; exit 60 }
    $json = Get-Content $report.FullName -Raw | ConvertFrom-Json
    $missing = [int]$json.Missing
    $detailText = ($json.MissingDetail -join ' ')
    Write-Phase "report Missing=$missing; MissingDetail='$detailText'"

    # 5. The INVERTED assertion (fail-closed — EVERY clause must hold, else the default exit 1 fires).
    #    Success == the gate flipped FALSIFY-01 to VERDICT_FAIL for the recoverable-but-lost binding-miss reason.
    $harnessFailedRight = ($harnessExit -eq 1)
    $sweepFailed        = ($sweepExit -ne 0)
    $verdictFail        = ("$($row.verdict)" -eq 'Fail') -and ("$($row.class)" -eq 'VERDICT_FAIL')
    $missingCounted     = ($missing -ge 1)
    $rightReason        = ($detailText -like '*recoverable-but-lost*') -and ($detailText -like '*binding miss*')

    if ($harnessFailedRight -and $sweepFailed -and $verdictFail -and $missingCounted -and $rightReason) {
        Write-Phase "NEGATIVE CONTROL PASSED — the gate correctly FAILED FALSIFY-01 (recoverable-but-lost → binding miss). Gate has teeth." 'Green'
        exit 0
    }
    Write-Phase "NEGATIVE CONTROL FAILED — gate-teeth regression. harnessExit==1:$harnessFailedRight sweepNonZero:$sweepFailed verdictFail:$verdictFail Missing>=1:$missingCounted rightReason:$rightReason" 'Red'
    exit 1

} finally {
    Pop-Location
}
