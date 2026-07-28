<#
.SYNOPSIS
    Dot-sourceable helper library for Phase 88's rendered-panel proof: batch invocation of the
    Grafana DOM reader (scripts/phase-88-panel-read.js), baseline banding, and the "did it move?"
    rule. PURE functions with NO top-level side effects — `. ./scripts/lib/phase-88-panel-read.ps1`
    only DEFINES functions (it never prints, never exits, never Push-Location's, never touches the
    cluster), so every banding and movement rule below is provable hermetically before any live run.

.DESCRIPTION
    SAMPLING VOCABULARY (fixed once for the whole phase)
        SAMPLE  = the value read from ONE 60-second-wide absolute pinned window — a stat panel's big
                  number, or one legend row's Mean for a timeseries series.
        CAPTURE = N contiguous 60-second sub-windows covering an N-minute span. A DISC-01 baseline
                  capture is N = 10; an after-capture is N >= 5 covering the fault.
        60 s is the STORED resolution (the SDK export cadence), so a sub-window narrower than 60 s
        cannot yield an independent sample. Get-PinnedWindowSeries therefore mints abutting windows
        of exactly SubWindowSeconds width — no gaps, no overlap — so "consecutive samples" means
        what it says.

    READER CONTRACT (PowerShell -> node -> Grafana)
        The reader is invoked as `node <skillDir>/run.js <abs path to scripts/phase-88-panel-read.js>`.
        run.js does process.chdir(__dirname) and prints its own BANNER LINES to stdout before the
        script runs, so the capture can NEVER be ConvertFrom-Json'd as a whole. Two sentinels make it
        parseable:
            ##PANEL-JSON##       one line per (panel, window) pair
            ##PANEL-BATCH-END##  exactly one, last, carrying {"requested":N,"emitted":M}
        Invoke-PanelReadBatch extracts the sentinel lines individually and reads the batch-end counts
        so a TRUNCATED batch can never be banded as if it were complete.

    THE VERDICT RULE THIS LIBRARY EXISTS TO KEEP HONEST
        A panel that could not be READ is INCONCLUSIVE material (States ReaderMissing / Unreadable /
        Truncated, and a panelState of Error). A panel that WAS read and did not move in the
        predicted direction is a FAIL. The two must never be collapsed: `Fail` beats `Inconclusive`
        (the Phase-87 verdict-precedence fix), and a read failure must never be laundered into a
        green. `NoData` is a third thing again — a first-class panel STATE, and where it is the
        predicted direction (panel 9 under a keeper outage, which is deliberately unguarded) it
        counts as MOVED, not as an unread panel.

    EXPORTED SURFACE
        Get-PropertyNames           $Object                                  -> string[] (StrictMode-safe)
        Resolve-PanelReaderSkillDir                                          -> string | $null
        Get-PinnedWindowSeries      -EndUtc -SubWindowSeconds -Count         -> window objects, oldest first
        Invoke-PanelReadBatch       -PanelIds -Windows [-DashboardUid] ...   -> @{ Readings; Requested;
                                                                                  Emitted; Complete; State }
        Get-PanelSamples            -Readings -PanelId [-SeriesName]         -> double[] in window order
        Get-PanelStates             -Readings -PanelId                       -> string[] in window order
        Get-PanelBand               -Values                                  -> band object
        Test-PanelMoved             -Band -AfterValues -AfterStates -Direction -MinConsecutive

.NOTES
    Dev/ops-only tooling. No product source touched. This library never starts, stops, or records a
    kubectl port-forward, and it never reads or writes the shared forward PID file that owns the
    eight long-lived stack forwards — the caller owns its own forward lifecycle. The absence of that
    filename anywhere in this file is asserted by a Select-String guard in 88-01-PLAN.md (T-88-05).
#>

# -------------------------------------------------------------------------------------------------
# Property names of a parsed-JSON object, safely. StrictMode Latest makes a bare access to an
# absent property a terminating error, so every optional field must be membership-tested first —
# but `$o.PSObject.Properties.Name` is itself unsafe: that is MEMBER ENUMERATION over a collection,
# and on an EMPTY collection (a `{}` in the JSON — several panel `options`/`custom` blocks are
# empty) StrictMode throws "The property 'Name' cannot be found on this object". Iterating the
# collection and reading each PSPropertyInfo's own Name cannot hit that.
# (Copied verbatim from scripts/phase-87-dashboards-verify.ps1:244 — this trap has bitten the repo
#  three times; do not re-derive it.)
# -------------------------------------------------------------------------------------------------
function Get-PropertyNames($Object) {
    $names = @()
    if ($null -eq $Object) { return $names }
    foreach ($prop in $Object.PSObject.Properties) { $names += $prop.Name }
    return $names
}

# -------------------------------------------------------------------------------------------------
# Directory of this library, resolved defensively: $PSScriptRoot is empty when the functions are
# pasted into a session rather than dot-sourced from a file, and StrictMode would make that fatal
# at the wrong moment.
# -------------------------------------------------------------------------------------------------
function Get-PanelReadLibDir {
    if (-not [string]::IsNullOrWhiteSpace($PSScriptRoot)) { return $PSScriptRoot }
    return (Get-Location).Path
}

# -------------------------------------------------------------------------------------------------
# The playwright-skill install directory. Overridable so a differently-installed skill (plugin vs
# manual vs project-local) does not require a code edit. Returns $null when run.js is not there,
# which the caller must treat as ReaderMissing -> Inconclusive, never as a Fail.
# -------------------------------------------------------------------------------------------------
function Resolve-PanelReaderSkillDir {
    [CmdletBinding()]
    param()

    $dir = $env:PHASE88_PLAYWRIGHT_SKILL_DIR
    if ([string]::IsNullOrWhiteSpace($dir)) {
        $dir = Join-Path $HOME '.claude/plugins/cache/playwright-skill/playwright-skill/4.1.0/skills/playwright-skill'
    }
    if (-not (Test-Path -LiteralPath $dir)) { return $null }
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'run.js'))) { return $null }
    return (Resolve-Path -LiteralPath $dir).Path
}

# -------------------------------------------------------------------------------------------------
# Count contiguous sub-windows of exactly SubWindowSeconds width ending at EndUtc, OLDEST FIRST.
# Entry n's ToMs equals entry n+1's FromMs exactly: abutting, no gaps, no overlap, so counting
# "2 consecutive samples outside the band" is a statement about contiguous time.
# -------------------------------------------------------------------------------------------------
function Get-PinnedWindowSeries {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][datetime]$EndUtc,
        [Parameter(Mandatory)][int]$SubWindowSeconds,
        [Parameter(Mandatory)][int]$Count
    )

    if ($SubWindowSeconds -le 0) { throw "SubWindowSeconds must be positive (got $SubWindowSeconds)." }
    if ($Count -le 0) { throw "Count must be positive (got $Count)." }

    $end = $EndUtc
    if ($end.Kind -eq [System.DateTimeKind]::Unspecified) {
        $end = [datetime]::SpecifyKind($end, [System.DateTimeKind]::Utc)
    }
    $endMs = [System.DateTimeOffset]::new($end.ToUniversalTime()).ToUnixTimeMilliseconds()
    $widthMs = [int64]$SubWindowSeconds * 1000

    $windows = @()
    for ($i = 0; $i -lt $Count; $i++) {
        $fromMs = $endMs - ([int64]($Count - $i) * $widthMs)
        $toMs = $fromMs + $widthMs
        $windows += [pscustomobject]@{
            FromMs  = $fromMs
            ToMs    = $toMs
            FromUtc = [System.DateTimeOffset]::FromUnixTimeMilliseconds($fromMs).UtcDateTime.ToString('o')
            ToUtc   = [System.DateTimeOffset]::FromUnixTimeMilliseconds($toMs).UtcDateTime.ToString('o')
        }
    }
    # Emitted UNWRAPPED (no `return ,$array` comma trick) so the repo's standard `@(...)`-wrap
    # idiom counts the WINDOWS rather than counting one nested array as a single element.
    return [object[]]$windows
}

# -------------------------------------------------------------------------------------------------
# Invoke the Grafana DOM reader for a cross product of panels x windows, in ONE browser session.
#
# NEVER THROWS. Every failure mode is returned as a State the caller can score as Inconclusive:
#   ReaderMissing  node or the playwright skill or the reader script is unavailable
#   Unreadable     the reader produced no readable line at all (or refused the batch outright)
#   Truncated      the ##PANEL-BATCH-END## counts disagree with what was emitted/parsed
#   Ok             every requested pair came back
# None of these is a Fail: a reading FAILURE is not evidence that a panel failed to move.
# -------------------------------------------------------------------------------------------------
function Invoke-PanelReadBatch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$PanelIds,
        [Parameter(Mandatory)][object[]]$Windows,
        [string]$DashboardUid = 'skp-business',
        [string]$GrafanaUrl = 'http://127.0.0.1:3000',
        [string]$ScreenshotDir = '',
        [ValidateSet('viewpanel', 'headerwalk')][string]$LocatorMode = 'viewpanel',
        [string[]]$PanelTitles = @(),
        [int]$ViewportWidth = 1920,
        [int]$ViewportHeight = 1080,
        [string]$BasicAuthBase64 = ''
    )

    $result = [ordered]@{
        Readings   = @()
        Requested  = 0
        Emitted    = 0
        Complete   = $false
        State      = 'Unreadable'
        Error      = $null
        ExitCode   = $null
        ReaderPath = $null
        SkillDir   = $null
        Stdout     = ''
    }

    # --- resolve the reader as an ABSOLUTE path -------------------------------------------------
    # run.js does process.chdir(__dirname), so cwd is the SKILL directory when the script runs; a
    # relative path could not resolve and would be silently treated as inline code.
    $readerPath = Join-Path (Join-Path (Get-PanelReadLibDir) '..') 'phase-88-panel-read.js'
    if (-not (Test-Path -LiteralPath $readerPath)) {
        $result.State = 'ReaderMissing'
        $result.Error = "reader script not found at $readerPath"
        return $result
    }
    $readerPath = (Resolve-Path -LiteralPath $readerPath).Path
    $result.ReaderPath = $readerPath

    $skillDir = Resolve-PanelReaderSkillDir
    if ($null -eq $skillDir) {
        $result.State = 'ReaderMissing'
        $result.Error = 'playwright skill run.js not found (set PHASE88_PLAYWRIGHT_SKILL_DIR to override the default location)'
        return $result
    }
    $result.SkillDir = $skillDir

    if ($null -eq (Get-Command node -ErrorAction SilentlyContinue)) {
        $result.State = 'ReaderMissing'
        $result.Error = 'node is not on PATH'
        return $result
    }

    # --- build the window list ('fromMs:toMs;...') ----------------------------------------------
    $windowSpecs = @()
    foreach ($w in @($Windows)) {
        if ($w -is [string]) { $windowSpecs += $w; continue }
        $wNames = @(Get-PropertyNames $w)
        if (($wNames -notcontains 'FromMs') -or ($wNames -notcontains 'ToMs')) {
            $result.State = 'ReaderMissing'
            $result.Error = 'a window entry carries neither FromMs/ToMs nor a "fromMs:toMs" string'
            return $result
        }
        $windowSpecs += ("{0}:{1}" -f $w.FromMs, $w.ToMs)
    }
    if (@($windowSpecs).Count -eq 0) {
        $result.State = 'ReaderMissing'
        $result.Error = 'no windows supplied'
        return $result
    }

    $result.Requested = @($PanelIds).Count * @($windowSpecs).Count

    $envNames = @('GRAFANA_URL', 'DASHBOARD_UID', 'PANEL_IDS', 'WINDOWS', 'VIEWPORT_W', 'VIEWPORT_H',
        'LOCATOR_MODE', 'PANEL_TITLES', 'SCREENSHOT_DIR', 'GRAFANA_BASIC_AUTH')

    try {
        $env:GRAFANA_URL = $GrafanaUrl
        $env:DASHBOARD_UID = $DashboardUid
        $env:PANEL_IDS = ($PanelIds -join ',')
        $env:WINDOWS = ($windowSpecs -join ';')
        $env:VIEWPORT_W = "$ViewportWidth"
        $env:VIEWPORT_H = "$ViewportHeight"
        $env:LOCATOR_MODE = $LocatorMode
        $env:PANEL_TITLES = ($PanelTitles -join ',')
        $env:SCREENSHOT_DIR = $ScreenshotDir
        $env:GRAFANA_BASIC_AUTH = $BasicAuthBase64

        $raw = ''
        try {
            $raw = & node (Join-Path $skillDir 'run.js') $readerPath 2>&1 | Out-String
            # Pin $LASTEXITCODE into a variable IMMEDIATELY — any subsequent pipeline or .Trim()
            # would overwrite it (the phase-87 idiom).
            $result.ExitCode = $LASTEXITCODE
        }
        catch {
            $result.State = 'Unreadable'
            $result.Error = "node invocation failed: $($_.Exception.Message)"
            return $result
        }
        $result.Stdout = "$raw"

        # --- sentinel extraction, line by line --------------------------------------------------
        # NEVER ConvertFrom-Json the whole capture: run.js prints banner lines before the script runs
        # and the panel innerText itself is arbitrary text.
        $lines = "$raw" -split "`r?`n"
        $readings = @()
        $badLines = 0
        foreach ($line in $lines) {
            if ($line -notmatch '^##PANEL-JSON##') { continue }
            $payload = $line -replace '^##PANEL-JSON##\s*', ''
            try { $readings += ($payload | ConvertFrom-Json) }
            catch { $badLines++ }
        }
        $result.Readings = $readings

        $endLine = @($lines | Where-Object { $_ -match '^##PANEL-BATCH-END##' } | Select-Object -Last 1)
        $endObj = $null
        if (@($endLine).Count -gt 0) {
            $endPayload = "$($endLine[0])" -replace '^##PANEL-BATCH-END##\s*', ''
            try { $endObj = ($endPayload | ConvertFrom-Json) } catch { $endObj = $null }
        }

        if ($null -ne $endObj) {
            $endNames = @(Get-PropertyNames $endObj)
            if ($endNames -contains 'requested') { $result.Requested = [int]$endObj.requested }
            if ($endNames -contains 'emitted') { $result.Emitted = [int]$endObj.emitted }
            if ($endNames -contains 'error' -and -not [string]::IsNullOrWhiteSpace("$($endObj.error)")) {
                $result.Error = "$($endObj.error)"
            }
        }

        $parsedCount = @($readings).Count
        if ($badLines -gt 0) {
            $result.Error = ("$($result.Error) " + "$badLines reader line(s) failed to parse").Trim()
        }

        if ($null -eq $endObj -and $parsedCount -eq 0) {
            $result.State = 'Unreadable'
            if (-not $result.Error) { $result.Error = 'the reader produced no sentinel line at all' }
        }
        elseif ($parsedCount -eq 0) {
            # The reader refused the batch (unequal windows, missing env) or read nothing at all.
            $result.State = 'Unreadable'
            if (-not $result.Error) { $result.Error = 'the reader emitted a batch-end marker but no readings' }
        }
        elseif ($null -eq $endObj) {
            $result.State = 'Truncated'
            $result.Emitted = $parsedCount
            if (-not $result.Error) { $result.Error = 'no batch-end marker: the reader was cut off mid-batch' }
        }
        elseif (($result.Requested -ne $result.Emitted) -or ($result.Emitted -ne $parsedCount)) {
            $result.State = 'Truncated'
            if (-not $result.Error) {
                $result.Error = "batch incomplete: requested=$($result.Requested) emitted=$($result.Emitted) parsed=$parsedCount"
            }
        }
        else {
            $result.State = 'Ok'
        }

        $result.Complete = ($result.State -eq 'Ok')
        return $result
    }
    catch {
        $result.State = 'Unreadable'
        $result.Error = "unexpected failure: $($_.Exception.Message)"
        return $result
    }
    finally {
        # Clear the contract so a LATER call can never inherit a stale WINDOWS (or PANEL_IDS) value
        # and silently read the wrong time range.
        foreach ($n in $envNames) {
            Remove-Item -LiteralPath ("Env:{0}" -f $n) -ErrorAction SilentlyContinue
        }
    }
}

# -------------------------------------------------------------------------------------------------
# The values for one panel, in window order. A stat panel yields statValue; a timeseries yields the
# Mean of the named series (exact match first, then a trimmed match so the label-less guard series —
# rendered by `or vector(0)` as the literal `consumed ` with a trailing space — is still reachable).
# A NoData (or Error) reading contributes NO value; its position is preserved by the paired
# Get-PanelStates, so the caller can always tell a gap from a zero.
# -------------------------------------------------------------------------------------------------
function Get-PanelSamples {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Readings,
        [Parameter(Mandatory)][string]$PanelId,
        [string]$SeriesName = ''
    )

    $values = @()
    foreach ($r in @($Readings)) {
        $names = @(Get-PropertyNames $r)
        if ($names -notcontains 'panelId') { continue }
        if ("$($r.panelId)" -ne $PanelId) { continue }

        if ([string]::IsNullOrEmpty($SeriesName)) {
            if ($names -contains 'statValue' -and $null -ne $r.statValue) {
                $values += [double]$r.statValue
            }
            continue
        }

        if ($names -notcontains 'series') { continue }
        $hit = $null
        foreach ($s in @($r.series)) {
            $sNames = @(Get-PropertyNames $s)
            if ($sNames -notcontains 'name') { continue }
            if ("$($s.name)" -ceq $SeriesName) { $hit = $s; break }
        }
        if ($null -eq $hit) {
            foreach ($s in @($r.series)) {
                $sNames = @(Get-PropertyNames $s)
                if ($sNames -notcontains 'name') { continue }
                if ("$($s.name)".Trim() -eq $SeriesName.Trim()) { $hit = $s; break }
            }
        }
        if ($null -eq $hit) { continue }
        $hNames = @(Get-PropertyNames $hit)
        if ($hNames -contains 'mean' -and $null -ne $hit.mean) { $values += [double]$hit.mean }
    }
    # Emitted UNWRAPPED — `@()`-wrap at the call site before reading .Count (repo convention).
    return [double[]]$values
}

# -------------------------------------------------------------------------------------------------
# The per-window panelState array for one panel, same ordering as Get-PanelSamples' windows.
# 'NoData' here is a STATE, not a missing measurement.
# -------------------------------------------------------------------------------------------------
function Get-PanelStates {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Readings,
        [Parameter(Mandatory)][string]$PanelId
    )

    $states = @()
    foreach ($r in @($Readings)) {
        $names = @(Get-PropertyNames $r)
        if ($names -notcontains 'panelId') { continue }
        if ("$($r.panelId)" -ne $PanelId) { continue }
        if ($names -contains 'panelState') { $states += "$($r.panelState)" }
        else { $states += 'Error' }
    }
    # Emitted UNWRAPPED — `@()`-wrap at the call site before reading .Count (repo convention).
    return [string[]]$states
}

# -------------------------------------------------------------------------------------------------
# Baseline band = mean +/- 3 sigma, with a FLOOR of +/-10 % of |mean| applied when 3 sigma is
# narrower. The floor is not a softening: panel 9 measures 0.4001-0.4005 ops, so its 3 sigma is
# roughly 0.0006 — an absurdly tight band that any trivial perturbation would breach, producing a
# guaranteed false positive. The floor keeps the band honest where it matters and costs nothing
# where the real fault is large (panel 9's real fault takes it from 0.4 to No data).
#
# When every value is exactly 0 the band is exactly 0..0 with FloorApplied = $false: that is
# Regime B (zero-floor event counter), whose null hypothesis IS exactly zero and which a SINGLE
# event falsifies. Widening it by a percentage of zero would be meaningless, and widening it by an
# absolute epsilon would destroy the strongest discrimination available in the phase.
#
# FloorApplied is reported so the artifact can state which rule bound the band.
# -------------------------------------------------------------------------------------------------
function Get-PanelBand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][double[]]$Values
    )

    $vals = @($Values)
    $n = $vals.Count
    if ($n -eq 0) {
        return [pscustomobject]@{
            Low = 0.0; High = 0.0; Mean = 0.0; Sigma = 0.0
            FloorApplied = $false; SampleCount = 0; HalfWidth = 0.0
        }
    }

    $sum = 0.0
    foreach ($v in $vals) { $sum += [double]$v }
    $mean = $sum / $n

    $sigma = 0.0
    if ($n -gt 1) {
        $ss = 0.0
        foreach ($v in $vals) { $d = ([double]$v) - $mean; $ss += ($d * $d) }
        $sigma = [Math]::Sqrt($ss / ($n - 1))   # sample stddev: the wider, more conservative choice
    }

    $threeSigma = 3.0 * $sigma
    $floor = 0.10 * [Math]::Abs($mean)
    $floorApplied = ($floor -gt $threeSigma)
    $half = if ($floorApplied) { $floor } else { $threeSigma }

    return [pscustomobject]@{
        Low          = $mean - $half
        High         = $mean + $half
        Mean         = $mean
        Sigma        = $sigma
        HalfWidth    = $half
        FloorApplied = $floorApplied
        SampleCount  = $n
    }
}

# -------------------------------------------------------------------------------------------------
# "Did it move?" — TRUE only when MinConsecutive CONSECUTIVE samples lie outside the band in the
# stated direction. At a 60 s sampling interval a single excursion is not distinguishable from a
# sampling artifact; two is the minimum that is (the Nyquist-grounded part of the rule).
#
# Direction:
#   up       value > Band.High
#   down     value < Band.Low
#   nonzero  value outside the band on either side (Regime B's form: a 0..0 band means any real
#            event qualifies)
#   nodata   MinConsecutive consecutive AfterStates entries equal to 'NoData'. "No data" is a
#            first-class panel STATE, and on panel 9 — deliberately unguarded so a dead keeper
#            renders "No data" rather than a comfortable 0 — it is the CORRECT discrimination
#            signal, not a measurement failure.
#
# Note on pairing: AfterValues carries only the windows that actually rendered a value (a NoData
# window contributes no number), so a run of consecutive VALUES is a run of consecutive rendered
# samples. Score a fault that empties a panel with Direction 'nodata' against AfterStates, not with
# a value direction against a silently shortened value array.
# -------------------------------------------------------------------------------------------------
function Test-PanelMoved {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Band,
        [Parameter()][AllowEmptyCollection()][double[]]$AfterValues = @(),
        [Parameter()][AllowEmptyCollection()][string[]]$AfterStates = @(),
        [Parameter(Mandatory)][ValidateSet('up', 'down', 'nonzero', 'nodata')][string]$Direction,
        [int]$MinConsecutive = 2
    )

    if ($MinConsecutive -lt 1) { $MinConsecutive = 1 }

    $run = 0
    $best = 0

    if ($Direction -eq 'nodata') {
        foreach ($s in @($AfterStates)) {
            if ("$s" -eq 'NoData') { $run++; if ($run -gt $best) { $best = $run } }
            else { $run = 0 }
        }
        return [pscustomobject]@{
            Moved                     = ($best -ge $MinConsecutive)
            ConsecutiveSamplesOutside = $best
            Direction                 = $Direction
            SamplesEvaluated          = @($AfterStates).Count
            MinConsecutive            = $MinConsecutive
        }
    }

    $bNames = @(Get-PropertyNames $Band)
    if (($bNames -notcontains 'Low') -or ($bNames -notcontains 'High')) {
        throw "Band must carry Low and High (produce it with Get-PanelBand)."
    }
    $low = [double]$Band.Low
    $high = [double]$Band.High

    foreach ($v in @($AfterValues)) {
        $x = [double]$v
        $outside = switch ($Direction) {
            'up' { $x -gt $high }
            'down' { $x -lt $low }
            'nonzero' { ($x -gt $high) -or ($x -lt $low) }
            default { $false }
        }
        if ($outside) { $run++; if ($run -gt $best) { $best = $run } }
        else { $run = 0 }
    }

    return [pscustomobject]@{
        Moved                     = ($best -ge $MinConsecutive)
        ConsecutiveSamplesOutside = $best
        Direction                 = $Direction
        SamplesEvaluated          = @($AfterValues).Count
        MinConsecutive            = $MinConsecutive
    }
}
