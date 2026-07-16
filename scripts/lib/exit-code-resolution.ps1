<#
.SYNOPSIS
    Dot-sourceable analyzer-verdict -> exit-code and exit-code -> sweep-class resolution
    (Phase 76, D-02/03/04). The single source of truth the harness (phase-67-harness.ps1) and
    the capstone sweep (phase-68-sweep.ps1) both consume.

.DESCRIPTION
    Two PURE functions with NO top-level side effects, so `. ./scripts/lib/exit-code-resolution.ps1`
    only DEFINES functions (it never executes a script body, prints, or exits). This lets the
    exit-2 (INCONCLUSIVE) mapping be proven HERMETICALLY — a synthetic Verdict==Inconclusive JSON
    resolves to exit 2 and code 2 maps to the INCONCLUSIVE class with the distinct no-auto-retry
    message — BEFORE the live gate ever runs (Phase 76 live-gate prerequisite).

    THREE-CLASS VERDICT (D-02):
        Pass         -> exit 0   (verdict green)
        Fail         -> exit 1   (real data-loss/duplication finding — NEVER auto-retried, D-04)
        Inconclusive -> exit 2   (observability tier blind — deliberate warm-ES re-run, NO auto-retry)

    T-76-11 (tampering guard): Resolve-AnalyzerExitCode sets 2 ONLY when Verdict == 'Inconclusive';
    a real FAIL keeps exit 1. An UNKNOWN/absent verdict is fail-closed to 1 (never a false 0 green,
    never a false 2 that would suppress a real finding).

.NOTES
    Dev/ops-only tooling. No product source touched. No untrusted input reaches these functions —
    the caller passes a parsed report object / an already-classified integer exit code.
#>

function Resolve-AnalyzerExitCode {
    # Report -> analyzer exit code (0 Pass / 1 Fail / 2 Inconclusive). Takes the PARSED report object
    # (ConvertFrom-Json). The JSON artifact is written by the analyzer BEFORE its assert, so its Verdict
    # is authoritative even when the fixture's `assert Verdict==Pass` made $LASTEXITCODE 1 on a non-Pass.
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Report)

    $verdict = "$($Report.Verdict)"
    switch ($verdict) {
        'Inconclusive' { return 2 }   # D-02: observability-degraded — deliberate re-run, no auto-retry
        'Fail'         { return 1 }   # real finding (T-76-11: a FAIL is NEVER mislabelled INCONCLUSIVE)
        'Pass'         { return 0 }
        default        { return 1 }   # unknown/absent verdict is fail-closed (never a false green)
    }
}

function Resolve-SweepClass {
    # Analyzer/harness exit code -> { Class; Message } for the sweep roll-up. 2 -> INCONCLUSIVE carries a
    # DISTINCT instrument-failure message advising a deliberate warm-ES re-run with NO auto-retry (D-03/D-04),
    # so an observability-blind run is sweep-fatal and never silently auto-retried away (T-76-10).
    [CmdletBinding()]
    param([Parameter(Mandatory)][int]$Code)

    switch ($Code) {
        0  { return @{ Class = 'PASS';         Message = 'PASS - verdict green (every started run complete).' } }
        1  { return @{ Class = 'VERDICT_FAIL'; Message = 'VERDICT_FAIL - analyzer found data loss/duplication (a real finding; NEVER auto-retried).' } }
        2  { return @{ Class = 'INCONCLUSIVE'; Message = 'INCONCLUSIVE - observability tier blind (StartedRuns=0, conservation intact); re-run deliberately against warm ES (no auto-retry).' } }
        64 { return @{ Class = 'BAD_ARG';      Message = 'BAD_ARG - unknown scenario id (config-usage error).' } }
        default { return @{ Class = 'INFRA_ABORT'; Message = "INFRA_ABORT - infrastructure step failed (exit $Code); operator re-runnable (no auto-retry)." } }
    }
}
