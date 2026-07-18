<#
.SYNOPSIS
    Phase 81 k8s capstone sweep — drive scripts/phase-80-harness.ps1 over all 7 scenarios (TEST-01..07).

.DESCRIPTION
    The single operator command for the k8s capstone proof. A THIN run-all-and-collect driver
    (NOT a re-implementation of any harness step): it loops the generalized Phase 80 k8s harness once
    per scenario id, captures + classifies each child-process exit code against the harness EXIT-CODE
    TABLE, reads each per-scenario analyzer report, and emits one roll-up summary.

        pwsh -File scripts/phase-81-sweep.ps1                 # all 7, numeric order
        pwsh -File scripts/phase-81-sweep.ps1 -ScenarioIds TEST-05   # operator re-run of a subset

    D-03 SHARED-STACK LIFECYCLE (the ONE structural divergence from the compose capstone sweep, which
    relied on the compose harness force-recreating every scenario): the sweep brings the k8s stack up ONCE before
    the loop (STEP 0 — phase-80-build.ps1 + phase-80-up.ps1), then loops the 7 scenarios sharing that
    stack via the child harness `-SkipBringUp` mode, and tears down the port-forwards ONCE after the
    roll-up (STEP Z — keeping the stack + PVCs). Each scenario re-establishes a clean window via the
    harness STEP B reset + STEP B1 orchestrator rollout-restart — no new sweep logic needed there.

    Run-all + collect, NO fail-fast (D-02): every one of the 7 scenarios runs even if an earlier one
    is non-PASS, so a single sweep yields all 7 results. Final exit is 0 IFF all selected scenarios
    PASS (harness exit 0), else non-zero.

    EXIT-CODE TABLE (from phase-80-harness.ps1 — the classification source of truth the shared lib maps
    against):
        0   analyzer PASS (final verdict green)
        1   analyzer FAIL verdict (the legitimate verdict path) — a REAL finding, NEVER auto-retried (D-04)
        2   analyzer INCONCLUSIVE verdict (observability tier blind — deliberate warm-ES re-run, NO auto-retry)
        10  bring-up (phase-80-up.ps1) / one-time build (phase-80-build.ps1) failed
        20  reset (phase-80-reset.ps1) failed
        25  orchestrator clean-restart / health-wait failed (clean-window guarantee, STEP B1)
        30  seeder failed
        40  wf-id lookup failed/empty
        50  activation gate != 204
        60  fault inject / recover / baseline-firing failed
        64  bad -ScenarioId argument (config-usage error)

    FLAKE / RE-RUN POLICY (D-04): a verdict FAIL (exit 1) is a real finding to investigate and is
    NEVER retried away; an INFRA_ABORT (10/20/25/30/40/50/60) is operator-re-runnable by invoking
    the bare harness for that single id. This wrapper does NOT auto-retry anything.

    ANTI-PATTERNS (encoded as hard NOTs): the wrapper NEVER re-scores Missing/Duplicates (it only
    reads + tabulates the analyzer's already-computed values), NEVER reads Prometheus deltas, NEVER
    auto-retries a scenario, and NEVER touches harness machinery (it only invokes
    phase-80-harness.ps1 -ScenarioId <id> -SkipBringUp as a child `pwsh -File` process).

.NOTES
    Dev/ops-only tooling. No product source touched. The harness owns all kubectl/dotnet ops and
    re-validates each -ScenarioId; this wrapper only ever feeds the hardcoded 7 ids (no untrusted
    input). Every k8s infra op is namespace-scoped (kubectl -n skp).
#>

param(
    [string[]]$ScenarioIds = @('TEST-01','TEST-02','TEST-03','TEST-04','TEST-05','TEST-06','TEST-07')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    # FRAME 2 — prefixed console-trace helper (mirrors phase-80-harness.ps1, re-prefixed).
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-81-sweep] $msg" -ForegroundColor $color
    }

    # D-02/D-03 — dot-source the SHARED exit-code resolution lib (the same one the harness consumes), so
    # the roll-up classifies 2 -> INCONCLUSIVE with the distinct instrument-failure / no-auto-retry message
    # instead of an inline switch. Pure functions, no side effects.
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')

    # Scenario-id order seam (D-02 / baseline-first). Defaults to all 7 in numeric order; an operator
    # may pass a subset to re-run. The harness is the source of truth for the keys.
    $Ids = @($ScenarioIds)
    Write-Phase "sweep over $($Ids.Count) scenario(s): $($Ids -join ', ')"

    # -----------------------------------------------------------------------
    # STEP 0 (D-03) — ONE bring-up per sweep (NOT per-scenario). Build the 4 app images :local + bring
    # the k8s stack up ONCE; the 7 scenarios share this stack (each re-establishes a clean window via the
    # harness STEP B reset + STEP B1 orchestrator rollout-restart). SourceHash currency (Phase-80 D-05)
    # holds for THIS build. The child harness runs in -SkipBringUp mode, so the port-forwards this STEP 0
    # starts stay up for every scenario (the harness skips its own STEP A0/A + STEP Z teardown).
    # -----------------------------------------------------------------------
    Write-Phase "STEP 0: one-time build + bring-up (phase-80-build.ps1 + phase-80-up.ps1) — shared across all $($Ids.Count) scenarios"
    & pwsh -File (Join-Path $PSScriptRoot 'phase-80-build.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "one-time image build failed (exit $LASTEXITCODE). Aborting sweep." 'Red'; exit 10 }
    & pwsh -File (Join-Path $PSScriptRoot 'phase-80-up.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "one-time bring-up failed (exit $LASTEXITCODE). Aborting sweep." 'Red'; exit 10 }

    $rows = @()

    foreach ($id in $Ids) {
        Write-Phase "=== scenario $id ==="

        # Invoke the harness as a SEPARATE child process. Because the harness sets
        # $ErrorActionPreference='Stop' inside its OWN process and is launched via `pwsh -File`, its
        # `exit N` surfaces here as $LASTEXITCODE WITHOUT terminating this loop (NO fail-fast, D-02).
        # -SkipBringUp: the sweep owns the ONE-TIME bring-up (STEP 0), so the child harness reuses the
        # already-up stack + port-forwards (D-03) instead of standing its own up per scenario.
        & pwsh -File (Join-Path $PSScriptRoot 'phase-80-harness.ps1') -ScenarioId $id -SkipBringUp
        $code = $LASTEXITCODE

        # Classify the child exit via the shared resolution lib (D-04). Adds 2 -> INCONCLUSIVE with a DISTINCT
        # instrument-failure message advising a deliberate warm-ES re-run — sweep-fatal, NO auto-retry on any
        # class. (0 PASS / 1 VERDICT_FAIL / 2 INCONCLUSIVE / 64 BAD_ARG / else INFRA_ABORT.)
        $resolved = Resolve-SweepClass $code
        $class = $resolved.Class
        Write-Phase "  $id -> harness exit $code ($class): $($resolved.Message)" $(if ($code -eq 0) { 'Green' } else { 'Yellow' })

        # Per-scenario analyzer report discovery. The wrapper only READS + tabulates the analyzer's
        # already-computed values — it NEVER re-scores.
        $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$id.json" -ErrorAction SilentlyContinue |
                  Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
        $json = if ($report) { Get-Content $report.FullName -Raw | ConvertFrom-Json } else { $null }

        # AnalyzerReport fields: Verdict, StartedRuns, CompleteRuns, Missing, Duplicates, TriggerCount,
        # PromImpliedRuns, Reconciliation, CorroborationDetail.
        # zeroMissing = ($json.Missing -eq 0);  effectOnce = (@($json.Duplicates).Count -eq 0)
        $rows += [pscustomobject]@{
            scenarioId   = $id
            verdict      = if ($json) { $json.Verdict } else { 'NO_REPORT' }
            zeroMissing  = if ($json) { ($json.Missing -eq 0) } else { $false }
            effectOnce   = if ($json) { (@($json.Duplicates).Count -eq 0) } else { $false }
            startedRuns  = if ($json) { $json.StartedRuns } else { $null }
            completeRuns = if ($json) { $json.CompleteRuns } else { $null }
            harnessExit  = $code
            class        = $class
        }
    }

    # -----------------------------------------------------------------------
    # ROLL-UP (D-03) — console table + machine artifact. NO new scoring: this only reads + tabulates
    # the 7 per-scenario reports plus each harness exit code.
    # -----------------------------------------------------------------------
    Write-Phase "=== capstone roll-up ==="
    $rows | Format-Table -AutoSize | Out-String | Write-Host

    $summaryDir = Join-Path $repoRoot 'analyzer-reports'
    New-Item -ItemType Directory -Force -Path $summaryDir | Out-Null
    $summaryPath = Join-Path $summaryDir 'phase-81-summary.json'
    $rows | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding utf8
    Write-Phase "roll-up artifact: $summaryPath" 'Green'

    # Single-glance milestone verdict: 7/7 PASS <=> wrapper exit 0 (D-02). Pass == harness exit 0.
    $passCount = (@($rows | Where-Object { $_.harnessExit -eq 0 }).Count)
    $total = $rows.Count
    Write-Phase "CAPSTONE: $passCount/$total PASS" $(if ($passCount -eq $total) { 'Green' } else { 'Red' })

    # -----------------------------------------------------------------------
    # STEP Z (D-03) — one-time port-forward teardown (the sweep owns the shared-stack lifecycle). Runs on
    # BOTH the pass and non-pass exit paths (computed before the final `exit`). Keep the k8s stack + PVCs
    # for inspection; stop only the loopback port-forwards phase-80-up.ps1 recorded, guarded by a
    # recycled-PID check (only kills live `kubectl` processes) — mirrors harness STEP Z.
    # -----------------------------------------------------------------------
    Write-Phase "STEP Z: teardown — stop the kubectl port-forwards (keep the stack + PVCs)"
    $pidFile = Join-Path $repoRoot '.k8s-portforward-pids'
    if (Test-Path $pidFile) {
        $pfPids = @(Get-Content $pidFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '\S' })
        foreach ($p in $pfPids) {
            try {
                $proc = Get-Process -Id ([int]$p) -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'kubectl' }
                if ($proc) { Stop-Process -Id ([int]$p) -Force -ErrorAction SilentlyContinue }
            } catch { }
        }
        Write-Phase "  stopped $($pfPids.Count) port-forward process(es)." 'Gray'
        Remove-Item $pidFile -ErrorAction SilentlyContinue
    } else {
        Write-Phase "  no .k8s-portforward-pids file found — nothing to stop." 'Yellow'
    }

    # Final exit (D-02): 0 iff every selected scenario was a PASS, else non-zero.
    exit ($(if ($passCount -eq $total) { 0 } else { 1 }))

} finally {
    Pop-Location
}
