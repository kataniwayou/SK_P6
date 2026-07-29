<#
.SYNOPSIS
    Phase 88 — the PANEL-KEYED discrimination roll-up (DISC-02, and the input every HAND-*
    deliverable reads).

.DESCRIPTION
    Reads every `analyzer-reports/phase-88-*.json` scenario artifact and TABULATES them into a
    fourteen-row matrix, one row per business panel, written to
    `analyzer-reports/phase-88-discrimination.json`.

    IT NEVER RE-SCORES. Not a verdict, not a band, not a movement. Every value in every row is
    COPIED from the artifact the row cites, and `sourceArtifact` names where each came from. That is
    the repo's standing rule for a roll-up (scripts/phase-81-sweep.ps1) and it is what keeps the
    matrix auditable: a roll-up that recomputed a verdict could disagree with the scenario that
    produced it, and then neither would be evidence.

    IT MUTATES NOTHING AND READS NO CLUSTER. Not one cluster command appears anywhere in this file,
    and 88-08's verification asserts that absence by searching for the client binary's name — which
    is why that name is not written here even inside a comment. The roll-up runs long after the
    faults are gone; if it needed the stack it would be measuring a different stack.

    THE ONE DELIBERATE DIVERGENCE FROM THE REPO'S ROLL-UP SHAPE (88-PATTERNS.md § roll-up): this
    array is keyed by PANEL, not by scenario, because the unit of proof here is the panel. One
    scenario can prove several panels, and one panel can appear in several scenarios' cross-talk
    lists — so the matrix is a JOIN, and the "never re-score" rule is kept by having each row cite
    the artifact it read its values from.

    A MISSING PROOF IS A ROW, NEVER AN ABSENCE (the `analyzer-reports/phase-85-summary.json`
    degraded-row precedent). A panel this phase could not drive is a row with status
    `AcceptedUnproven` and a non-empty `reason`; an `AcceptedUnproven` row WITHOUT a reason exits 1,
    because an unexplained gap is a defect in the record rather than a neutral outcome.

    MULTI-SERIES BAND COLLAPSE. `analyzer-reports/phase-88-BASE-01.json` emits one band entry PER
    LEGEND SERIES and nine of the fourteen panels carry two or more, so "the panel's band" is not a
    single value in the source artifact. `baselineBandSeries[]` therefore reproduces EVERY per-series
    entry verbatim so nothing is lost, and `baselineBand` is their WIDEST ENVELOPE (min BandLow, max
    BandHigh, the OR of FloorApplied) carrying `seriesCount` and `collapsedFrom[]` so the collapse is
    auditable from the artifact alone. Choosing a "first" or "primary" series is explicitly REJECTED:
    it would silently discard a series whose band differs, which is exactly the kind of hidden
    evidence loss this matrix exists to prevent. The envelope is a HUMAN SUMMARY for reading the
    matrix and nothing more — every movement decision was already made PER SERIES by the scenario
    harness against the per-series band.

    THE THINGS THAT MUST NOT BE SMOOTHED AWAY, and where they live. The schema is exactly fourteen
    rows with no top-level object, so each row carries its own:
      * `discardedRuns[]`            — the re-rolled runs whose artifacts are committed BESIDE their
                                       replacements, with the defect that forced each discard
      * `measuredUnsatisfiableCriteria[]` — acceptance criteria this phase could not satisfy AS
                                       WRITTEN and corrected by measurement rather than by retrying
      * `escalations[]`              — a requirement that cannot be met under the phase's locked
                                       constraints (DISC-04 on panel 8) — an escalation for 88-09,
                                       not a checkbox left Pending in silence

.PARAMETER None
    The roll-up takes no scenario parameter. It reads the whole phase.

.OUTPUTS
    analyzer-reports/phase-88-discrimination.json — a JSON ARRAY of exactly fourteen rows.

.NOTES
    EXIT CODES
      0   every row present, every AcceptedUnproven row carries a reason
      1   a row is missing, or an AcceptedUnproven row has no reason — a defect in the record
      64  a required input artifact is absent (usage/state error, remediation printed)
      66  the written artifact carries a JSON VALUE of 'System.Object[]' (serialisation truncation)

    Dev/ops-only tooling. No product source is touched, no cluster is read, no manifest is edited.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-88-rollup] $msg" -ForegroundColor $color
    }

    # Resolve-SweepClass is dot-sourced, never reimplemented: its fail-closed default is the control
    # that stops an unknown exit code from being read as green.
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')

    function Get-PropertyNameList($Object) {
        if ($null -eq $Object) { return @() }
        return @($Object.PSObject.Properties | ForEach-Object { $_.Name })
    }
    function Test-HasProperty($Object, [string]$Name) {
        return ((Get-PropertyNameList $Object) -contains $Name)
    }
    # A DEFENSIVE read. The artifacts this roll-up joins were written by four different plans as the
    # engine grew, and their shapes legitimately differ — 88-06 changed ReplicasBefore/After from a
    # map to a scalar and left a ReplicasFieldNote saying so, the WEB-* cross-talk entries carry a
    # panel-level BandLow while the LADDER-01 ones carry theirs per series, and the older subject
    # rows have no ObservedDirection at all. Under Set-StrictMode a bare property access on an
    # absent member THROWS, so every cross-artifact read goes through here and an absent value
    # becomes a stated default rather than an abort. This is a READ, not a repair: nothing is
    # invented, and a field that is absent stays absent in the row.
    function Get-Prop($Object, [string]$Name, $Default = $null) {
        if ($null -eq $Object) { return $Default }
        if (-not (Test-HasProperty $Object $Name)) { return $Default }
        $v = $Object.$Name
        if ($null -eq $v) { return $Default }
        return $v
    }

    $reportDir = Join-Path $repoRoot 'analyzer-reports'
    $dashPath  = Join-Path $repoRoot 'k8s/dashboards/business.json'

    # =========================================================================================
    # INPUTS. Each scenario artifact is named STATICALLY here — nothing is globbed — so an artifact
    # that was never produced is a NAMED absence with a remediation line rather than a silently
    # shorter matrix.
    # =========================================================================================
    $ArtifactFiles = [ordered]@{
        'BASE-01'   = 'phase-88-BASE-01.json'
        'WEB-01'    = 'phase-88-WEB-01.json'
        'WEB-02'    = 'phase-88-WEB-02.json'
        'SCALE-01'  = 'phase-88-SCALE-01.json'
        'SCALE-02'  = 'phase-88-SCALE-02.json'
        'SCALE-03'  = 'phase-88-SCALE-03.json'
        'ZERO-01'   = 'phase-88-ZERO-01.json'
        'ZERO-02'   = 'phase-88-ZERO-02.json'
        'ZERO-03'   = 'phase-88-ZERO-03.json'
        'LADDER-01' = 'phase-88-LADDER-01.json'
    }
    # The order a proof is attributed in. It is the phase's own wave order, so `provenBy` names the
    # scenario that FIRST proved a panel and `alsoProvenBy` names every later corroboration.
    $ScenarioOrder = @('WEB-01', 'WEB-02', 'SCALE-01', 'SCALE-02', 'SCALE-03', 'ZERO-01', 'ZERO-02', 'ZERO-03', 'LADDER-01')

    Write-Phase "reading the phase's artifacts (read-only; this roll-up mutates nothing and reads no cluster)"
    $missing = @()
    $A = @{}
    foreach ($k in $ArtifactFiles.Keys) {
        $p = Join-Path $reportDir $ArtifactFiles[$k]
        if (-not (Test-Path -LiteralPath $p)) { $missing += "$k ($($ArtifactFiles[$k]))"; continue }
        $A[$k] = (Get-Content $p -Raw | ConvertFrom-Json)
    }
    if (-not (Test-Path -LiteralPath $dashPath)) { $missing += "business.json (k8s/dashboards/business.json)" }
    if (@($missing).Count -gt 0) {
        Write-Phase "required input artifact(s) absent: $(@($missing) -join ', ')" 'Red'
        Write-Phase "REMEDIATION: run the plan that owns each — 88-04 BASE-01, 88-05 WEB-01/WEB-02, 88-06 SCALE-01/02/03, 88-07 ZERO-01/02/03, 88-08 LADDER-01." 'Yellow'
        Write-Phase "The matrix is NOT written from a partial phase: a row whose evidence was never produced would be indistinguishable from a row whose panel could not be driven." 'Yellow'
        exit 64
    }
    $dash = Get-Content $dashPath -Raw | ConvertFrom-Json

    # The DISCARDED runs, committed BESIDE their replacements. Each names the defect that forced the
    # discard and the panels whose evidence it touched, so a re-roll can never look like a first run.
    $DiscardedRuns = @(
        @{ File = 'analyzer-reports/phase-88-BASE-01-run1-discarded.json'; Scenario = 'BASE-01'; Panels = @(1..14);
           Defect = 'run 1 lost its ENTIRE diagnostic cross-check to a single-letter typed parameter (a [long]$S parameter and a foreach($s) loop variable are the same case-insensitive variable, and the type constraint threw on every call), and its depth guard then tripped on its own diagnostic text. Discarded for DEFECTS, not for a better number; both artifacts are committed.' }
        @{ File = 'analyzer-reports/phase-88-SCALE-01-run1-discarded.json'; Scenario = 'SCALE-01'; Panels = @(3, 4, 5, 9, 10, 12, 14);
           Defect = 'run 1 stated a spike its own recorded values contradict (the segment behaviour was INFERRED from "some series moved" while that series had been scored by the NoData half of its compound token), an alt-direction match APPENDED a duplicate series entry so the diagnostic aggregate double-counted panel 4, and PanelStateNoDataRun was left unmeasured for panels whose prediction did not admit NoData. Discarded for DEFECTS; both artifacts are committed.' }
        @{ File = 'analyzer-reports/phase-88-ZERO-02-run1-discarded.json'; Scenario = 'ZERO-02'; Panels = @(2, 8, 9, 10, 12);
           Defect = 'run 1 drove a GENUINE recovery event that panel 2 rendered as an unbroken 0, because the whole burst finished inside one 60 s export interval and rate() differences consecutive samples. It also recorded the DRIVER''s own activation POST as the fault''s blast radius on panels 10 and 12. Discarded for DEFECTS in the drive code — and it remains a BETTER measurement of what an operator will actually see than run 2 is. Both artifacts are committed.' }
    )

    # ACCEPTANCE CRITERIA THIS PHASE COULD NOT SATISFY AS WRITTEN, corrected by measurement rather
    # than by re-running until they passed. They are recorded on the row they are about, because the
    # schema is exactly fourteen rows and there is no top-level object for them to hide in.
    $UnsatisfiableCriteria = @(
        @{ PanelId = 4;  Criterion = '88-04 must_have: "panel 4 is banded on its per-minute increase at the fixed 60 s sub-window"';
           Measured = 'panel 4 renders "No data" at a 60 s sub-window. increase() needs at least TWO samples inside its range and the stored resolution on this stack is 60 s (the SDK export cadence, not the 15 s scrape), so a 60 s window contains exactly one. Panel 4 is banded at 120 s instead and every band entry states its own SubWindowSeconds, so widths can never be silently cross-compared.'; CorrectedIn = '88-04' }
        @{ PanelId = 12; Criterion = '88-05 task 2: LegendNamesAfter[] must contain a series name absent from LegendNamesBaseline[] (a legend GAIN)';
           Measured = 'MEASURED-UNSATISFIABLE on this stack: panel 12 rendered exactly 200|201|204|400|404|422 in BOTH windows. Prometheus retains a counter series for as long as the emitting process lives and the WebApi pod has been up since Phase 80, so a status code that has occurred once never disappears — the series are present AND ZERO before the drive rather than absent. The substantive claim was proven in the STRONGER numeric form instead (two exactly-0..0 bands going non-zero for six consecutive samples) and WEB-01 was NOT re-run to chase it.'; CorrectedIn = '88-05' }
        @{ PanelId = 14; Criterion = '88-05/88-08 assumption: panel 14 is ONE gauge rung';
           Measured = 'panel 14 is TWO rungs. http_server_active_requests read exactly 0 at every one of six export ticks under 2456 sustained requests from ten parallel requesters; only kestrel_active_connections moves. Two of the panel''s three series cannot be moved by this class of load at all.'; CorrectedIn = '88-05' }
        @{ PanelId = 9;  Criterion = '88-06 table: SCALE-03 dwell 240 s';
           Measured = 'CORRECTED BEFORE THE RUN, on a read-only measurement rather than a hunch: rate(counter[240s]) for a dead pod keeps producing a DECAYING value for roughly 180 s past its last real sample, so at a 240 s dwell the panel would still have been rendering and the plan''s own two-consecutive-NoData criterion would have been unreachable by construction. The dwell was raised to 480 s.'; CorrectedIn = '88-06' }
        @{ PanelId = 4;  Criterion = '88-06 prediction: panel 4 slopes UP under a processor outage';
           Measured = 'PREDICTION CORRECTED BY MEASUREMENT: sum(increase(proc_consumed[120s])) over a window with no real samples is an EMPTY vector and `A - empty` is empty, so the stat whose own title says "read the trend" goes BLANK under exactly the fault it exists to reveal. SCALE-01''s verdict is Inconclusive because of it — a wrong model is never absorbed into a Pass.'; CorrectedIn = '88-06' }
        @{ PanelId = 5;  Criterion = '88-06 prediction: the per-processor dispatch gap SPIKES before it empties';
           Measured = 'HALF HELD. Panel 5 emptied WITHOUT EVER SPIKING (0.63 -> 0.588 -> 0.4 -> 0.234 -> 0.183 is a decay). The research model assumes an independent producer; this pipeline is request/response, so with the processors gone the orchestrator receives no completions and its own send rate falls too.'; CorrectedIn = '88-06' }
        @{ PanelId = 8;  Criterion = 'DISC-04 (as written): panel 8 must be observed NON-ZERO with the keeper reinject suppressed';
           Measured = 'MEASURED-UNSATISFIABLE UNDER THIS PHASE''S LOCKED CONSTRAINTS. ReinjectConsumer.cs:106-116 runs `if (!defeatReinject) { Send } else { log }` and then calls CountSent(...) UNCONDITIONALLY — the Phase-79 control was built so the metrics do not reveal the loss, and its own comment says so. The requirement therefore asks for something the product deliberately prevents. Measured three independent ways: identical consumed/sent increase terms, no allow-listed lever reaching the drop branch, and both increase() terms returning ZERO POINTS at 60 s so the green 0 is supplied entirely by `or vector(0)`.'; CorrectedIn = '88-07' }
    )

    $Escalations = @(
        @{ PanelId = 8; Requirement = 'DISC-04'; Text = 'DISC-04 is HALF MET and its other half is MEASURED-UNREACHABLE. Panel 2 is proven non-zero in a real keeper recovery event with the gap held at exactly 0 (ZERO-02); panel 8 is proven undrivable off its guarded zero by any lever this phase is allowed to pull (ZERO-03). Satisfying the requirement as written would need either a src/ change (forbidden by the phase''s locked constraint) or KEEPER_REINJECT_DELAY_MS (refused by T-88-15). 88-09 must record DISC-04 as half met WITH the measured reason rather than leaving it Pending in silence.' }
    )

    # =========================================================================================
    # THE DASHBOARD FACTS. Titles are read from the shipped dashboard so a row can never carry a
    # title that has drifted from what an operator sees; the guard and the thresholds come from the
    # same read, because "the panel renders a comfortable 0" is a property of its EXPRESSION.
    # =========================================================================================
    $PanelFacts = @{}
    foreach ($pn in @($dash.panels)) {
        if ("$($pn.type)" -eq 'row') { continue }
        $id = [int]$pn.id
        $exprs = @()
        if (Test-HasProperty $pn 'targets') {
            foreach ($t in @($pn.targets)) { if (Test-HasProperty $t 'expr') { $exprs += "$($t.expr)" } }
        }
        $guarded = (@($exprs | Where-Object { $_ -match 'or\s+vector\(0\)' }).Count -gt 0)
        $steps = @()
        if ((Test-HasProperty $pn 'fieldConfig') -and (Test-HasProperty $pn.fieldConfig 'defaults') -and (Test-HasProperty $pn.fieldConfig.defaults 'thresholds')) {
            foreach ($st in @($pn.fieldConfig.defaults.thresholds.steps)) {
                $steps += ("{0}@{1}" -f "$($st.color)", $(if ($null -eq $st.value) { 'base' } else { "$($st.value)" }))
            }
        }
        $PanelFacts[$id] = @{
            Title = "$($pn.title)"; Type = "$($pn.type)"; Guarded = $guarded
            Queries = [string[]]@($exprs); Thresholds = [string[]]@($steps)
        }
    }
    if (@($PanelFacts.Keys).Count -ne 14) {
        Write-Phase "the dashboard declares $(@($PanelFacts.Keys).Count) non-row panels, not 14 — the matrix shape is defined by the dashboard, so it refuses to guess." 'Red'
        exit 64
    }

    # =========================================================================================
    # HELPERS — every one of them READS. None computes a verdict, a band or a movement.
    # =========================================================================================

    # The subject entry for a panel in a scenario artifact, or $null.
    function Get-SubjectEntry($Artifact, [int]$PanelId) {
        if (-not (Test-HasProperty $Artifact 'PanelResults')) { return $null }
        $hit = @($Artifact.PanelResults | Where-Object { [int]$_.PanelId -eq $PanelId })
        if ($hit.Count -eq 0) { return $null }
        return $hit[0]
    }
    function Get-ControlEntry($Artifact, [int]$PanelId) {
        if (-not (Test-HasProperty $Artifact 'CrossTalkPanels')) { return $null }
        $hit = @($Artifact.CrossTalkPanels | Where-Object { [int]$_.PanelId -eq $PanelId })
        if ($hit.Count -eq 0) { return $null }
        return $hit[0]
    }
    function Get-ObservedEntry($Artifact, [int]$PanelId) {
        if (-not (Test-HasProperty $Artifact 'ObservedPanels')) { return $null }
        $hit = @($Artifact.ObservedPanels | Where-Object { [int]$_.PanelId -eq $PanelId })
        if ($hit.Count -eq 0) { return $null }
        return $hit[0]
    }

    # A recorded direction token -> the state vocabulary this matrix uses. The mapping is fixed here
    # so no row invents its own word for the same observation.
    #   steady     inside its healthy band for the whole window
    #   zero       a band of exactly 0..0, read as 0 (the guarded/Regime-B resting state)
    #   nonzero    a 0..0 band LEFT — the Regime-B discrimination
    #   elevated   above the band
    #   depressed  below the band
    #   NoData     the panel rendered "No data" (a first-class panel STATE, not a failed read)
    #   drifted    a cross-talk control that did NOT stay put (recorded, never re-classified)
    function ConvertTo-StateToken([string]$Direction) {
        switch ("$Direction") {
            'up'             { return 'elevated' }
            'down'           { return 'depressed' }
            'nonzero'        { return 'nonzero' }
            'nodata'         { return 'NoData' }
            'slope-up'       { return 'elevated' }
            'up-then-nodata' { return 'NoData' }
            default          { return '' }
        }
    }

    # =========================================================================================
    # BUILD THE FOURTEEN ROWS.
    # =========================================================================================
    $rows = @()
    $defects = @()

    foreach ($pid14 in 1..14) {
        $facts = $PanelFacts[$pid14]

        # ---- the band, per series and verbatim, then collapsed to its WIDEST ENVELOPE ------------
        $srcBands = @($A['BASE-01'].BaselineBands | Where-Object { [int]$_.PanelId -eq $pid14 })
        $bandSeries = @()
        foreach ($b in $srcBands) {
            $bandSeries += [pscustomobject]@{
                seriesIndex      = [int]$b.SeriesIndex
                seriesName       = "$($b.SeriesName)"
                bandLow          = [double]$b.BandLow
                bandHigh         = [double]$b.BandHigh
                bandMean         = [double]$b.BandMean
                bandSigma        = [double]$b.BandSigma
                floorApplied     = [bool]$b.FloorApplied
                sampleCount      = [int]$b.SampleCount
                subWindowSeconds = [int]$b.SubWindowSeconds
                values           = [double[]]@($b.Values)
                states           = [string[]]@($b.States)
            }
        }
        $regime = $(if (@($srcBands).Count -gt 0) { "$($srcBands[0].Regime)" } else { '' })

        if (@($bandSeries).Count -gt 0) {
            $envelope = [pscustomobject]@{
                low          = ([double](@($bandSeries | Measure-Object -Property bandLow  -Minimum).Minimum))
                high         = ([double](@($bandSeries | Measure-Object -Property bandHigh -Maximum).Maximum))
                floorApplied = ((@($bandSeries | Where-Object { $_.floorApplied }).Count -gt 0))
                seriesCount  = [int]@($bandSeries).Count
                collapsedFrom = [string[]]@(@($bandSeries | ForEach-Object { "$($_.seriesName)" }))
            }
        } else {
            $envelope = [pscustomobject]@{ low = $null; high = $null; floorApplied = $false; seriesCount = 0; collapsedFrom = [string[]]@() }
        }

        # BASE-01 records a Class-A SERIES with no live signal as an unevaluable FINDING rather than
        # as a band. Those series still appear in BaselineBands (banded at exactly zero) and are
        # copied above; the finding text is carried so the row states why the band is degenerate.
        $unevaluable = @()
        foreach ($u in @($A['BASE-01'].UnevaluablePanels)) {
            if ("$u" -match ("^panel {0} \(" -f $pid14)) { $unevaluable += "$u" }
        }

        # ---- the observed states, accumulated from EVERY artifact the panel appears in -----------
        $states = @()
        $stateRecords = @()
        $crossTalk = @()
        $assertedIn = @()
        $observedIn = @()
        $provenBy = $null
        $alsoProvenBy = @()
        $sources = @('analyzer-reports/phase-88-BASE-01.json')
        $sourceMap = @("baselineBandSeries/baselineBand/regime <- analyzer-reports/phase-88-BASE-01.json")
        $reason = ''
        $reasonFrom = ''

        # the healthy resting state, from the DISC-01 band itself
        if (@($bandSeries).Count -gt 0) {
            $restState = $(if (([double]$envelope.low -eq 0.0) -and ([double]$envelope.high -eq 0.0)) { 'zero' } else { 'steady' })
            $states += $restState
            $stateRecords += [pscustomobject]@{ state = $restState; scenarioId = 'BASE-01'; role = 'baseline'; evidence = "the DISC-01 band envelope is $($envelope.low)..$($envelope.high) over $($envelope.seriesCount) legend series, 10 x 60 s sub-windows on a settled stack" }
        }

        foreach ($sid in $ScenarioOrder) {
            $art = $A[$sid]

            $subj = Get-SubjectEntry $art $pid14
            if ($null -ne $subj) {
                $assertedIn += $sid
                $sources += "analyzer-reports/$($ArtifactFiles[$sid])"
                $moved = [bool](Get-Prop $subj 'Moved' $false)
                $sLow  = Get-Prop $subj 'BandLow'
                $sHigh = Get-Prop $subj 'BandHigh'
                $dirTok = ''
                if (-not [string]::IsNullOrWhiteSpace("$(Get-Prop $subj 'ObservedDirection' '')")) {
                    $dirTok = ConvertTo-StateToken "$(Get-Prop $subj 'ObservedDirection' '')"
                } else {
                    $dirTok = ConvertTo-StateToken "$(Get-Prop $subj 'PredictedDirection' '')"
                }
                if ($moved) {
                    if (-not [string]::IsNullOrWhiteSpace($dirTok)) {
                        $states += $dirTok
                        $stateRecords += [pscustomobject]@{ state = $dirTok; scenarioId = $sid; role = 'asserted subject'; evidence = "Moved=true over $(Get-Prop $subj 'ConsecutiveSamplesOutside' 0) consecutive sub-window(s) against a band of ${sLow}..${sHigh} — copied, not recomputed" }
                    }
                    if ($null -eq $provenBy) {
                        $provenBy = $sid
                        $sourceMap += "provenBy/observedStates(moved) <- analyzer-reports/$($ArtifactFiles[$sid])"
                    } else {
                        $alsoProvenBy += $sid
                    }
                } else {
                    $notMoved = $(if (($null -ne $sLow) -and ([double]$sLow -eq 0.0) -and ([double]$sHigh -eq 0.0)) { 'zero' } else { 'steady' })
                    $states += $notMoved
                    $stateRecords += [pscustomobject]@{ state = $notMoved; scenarioId = $sid; role = 'asserted subject'; evidence = "Moved=false against a band of ${sLow}..${sHigh}; AfterValues $(@(Get-Prop $subj 'AfterValues' @()) -join ',')" }
                }
                if (@(@(Get-Prop $subj 'AfterStates' @()) | Where-Object { "$_" -eq 'NoData' }).Count -gt 0) {
                    $states += 'NoData'
                    $stateRecords += [pscustomobject]@{ state = 'NoData'; scenarioId = $sid; role = 'asserted subject'; evidence = "AfterStates $(@(Get-Prop $subj 'AfterStates' @()) -join ',')" }
                }
                # the accepted-unproven reason belongs to the scenario that FAILED to prove it
                if (-not $moved -and -not [string]::IsNullOrWhiteSpace("$(Get-Prop $art 'AcceptedUnprovenReason' '')")) {
                    $reason = "$(Get-Prop $art 'AcceptedUnprovenReason' '')"
                    $reasonFrom = $sid
                }
            }

            $ctl = Get-ControlEntry $art $pid14
            if ($null -ne $ctl) {
                $crossTalk += $sid
                $sources += "analyzer-reports/$($ArtifactFiles[$sid])"
                if ([bool](Get-Prop $ctl 'Applicable' $false)) {
                    $cLow  = Get-Prop $ctl 'BandLow'
                    $cHigh = Get-Prop $ctl 'BandHigh'
                    $cExc  = Get-Prop $ctl 'MaxExcursion'
                    if ([bool](Get-Prop $ctl 'StayedPut' $false)) {
                        $ctState = $(if (($null -ne $cLow) -and ([double]$cLow -eq 0.0) -and ([double]$cHigh -eq 0.0)) { 'zero' } else { 'steady' })
                        $states += $ctState
                        $stateRecords += [pscustomobject]@{ state = $ctState; scenarioId = $sid; role = 'cross-talk control'; evidence = "StayedPut=true, MaxExcursion=${cExc} against ${cLow}..${cHigh}" }
                    } else {
                        $states += 'drifted'
                        $stateRecords += [pscustomobject]@{ state = 'drifted'; scenarioId = $sid; role = 'cross-talk control'; evidence = "StayedPut=false, MaxExcursion=${cExc} — recorded as a finding by the scenario, never re-classified here" }
                    }
                }
            }

            $obs = Get-ObservedEntry $art $pid14
            if ($null -ne $obs) {
                $observedIn += $sid
                $sources += "analyzer-reports/$($ArtifactFiles[$sid])"
                $allVals = @()
                foreach ($sr in @(Get-Prop $obs 'SeriesResults' @())) {
                    foreach ($v in @(Get-Prop $sr 'AfterValues' @())) { $allVals += [double]$v }
                }
                $obsState = $(if (@($allVals).Count -eq 0) { '' } elseif (@($allVals | Where-Object { $_ -ne 0.0 }).Count -eq 0) { 'zero' } else { 'nonzero' })
                if (-not [string]::IsNullOrWhiteSpace($obsState)) {
                    $states += $obsState
                    $stateRecords += [pscustomobject]@{ state = $obsState; scenarioId = $sid; role = 'observed only'; evidence = "values $(@($allVals) -join ',') — recorded by the scenario, never scored" }
                }
                if (@(@(Get-Prop $obs 'AfterStates' @()) | Where-Object { "$_" -eq 'NoData' }).Count -gt 0) {
                    $states += 'NoData'
                    $stateRecords += [pscustomobject]@{ state = 'NoData'; scenarioId = $sid; role = 'observed only'; evidence = "AfterStates $(@(Get-Prop $obs 'AfterStates' @()) -join ',')" }
                }
            }
        }

        # ---- the duration ladder. Its rungs carry per-panel outcomes rather than PanelResults, so
        # ---- it is read on its own terms. A rung that moved a panel is a CORROBORATION of that
        # ---- panel's discrimination at a stated fault duration, recorded in alsoProvenBy.
        $ladderRungs = @()
        foreach ($rg in @(Get-Prop $A['LADDER-01'] 'Rungs' @())) {
            foreach ($po in @(Get-Prop $rg 'PanelOutcomes' @())) {
                if ([int](Get-Prop $po 'PanelId' -1) -ne $pid14) { continue }
                $poMoved = [bool](Get-Prop $po 'Moved' $false)
                $poBy    = "$(Get-Prop $po 'MovedBy' '')"
                $poRun   = [int](Get-Prop $po 'ConsecutiveSamplesOutside' 0)
                $ladderRungs += [pscustomobject]@{
                    regime = "$(Get-Prop $rg 'Regime' '')"; faultSeconds = [int](Get-Prop $rg 'FaultSeconds' 0)
                    moved = $poMoved; movedBy = $poBy
                    consecutiveSamplesOutside = $poRun
                    dipFraction = (Get-Prop $po 'DipFraction')
                    theoreticalDipFraction = (Get-Prop $rg 'TheoreticalDipFraction')
                }
                $tok = $(if ($poMoved) { $(if ($poBy -eq 'nodata') { 'NoData' } elseif ("$(Get-Prop $rg 'Regime' '')" -eq 'gauge') { 'elevated' } else { 'depressed' }) } else { 'steady' })
                $states += $tok
                $stateRecords += [pscustomobject]@{ state = $tok; scenarioId = 'LADDER-01'; role = "$(Get-Prop $rg 'Regime' '') rung $(Get-Prop $rg 'FaultSeconds' 0)s"; evidence = "Moved=$poMoved movedBy=$poBy consecutive=$poRun" }
            }
        }
        if (@($ladderRungs).Count -gt 0) {
            $sources += 'analyzer-reports/phase-88-LADDER-01.json'
            if (@($ladderRungs | Where-Object { $_.moved }).Count -gt 0) {
                if ($null -eq $provenBy) { $provenBy = 'LADDER-01' } elseif ($alsoProvenBy -notcontains 'LADDER-01') { $alsoProvenBy += 'LADDER-01' }
            }
        }
        $ladderControls = @(@(Get-Prop $A['LADDER-01'] 'CrossTalkPanels' @()) | Where-Object { [int](Get-Prop $_ 'PanelId' -1) -eq $pid14 })
        if (@($ladderControls).Count -gt 0) {
            if ($crossTalk -notcontains 'LADDER-01') { $crossTalk += 'LADDER-01' }
            $sources += 'analyzer-reports/phase-88-LADDER-01.json'
            foreach ($lct in $ladderControls) {
                if (-not [bool](Get-Prop $lct 'Applicable' $false)) { continue }
                $lStayed = [bool](Get-Prop $lct 'StayedPut' $false)
                $tok = $(if ($lStayed) { 'steady' } else { 'drifted' })
                $states += $tok
                $stateRecords += [pscustomobject]@{ state = $tok; scenarioId = 'LADDER-01'; role = "cross-talk control ($(Get-Prop $lct 'Regime' '') rung $(Get-Prop $lct 'FaultSeconds' 0)s)"; evidence = "StayedPut=$lStayed, MaxExcursion=$(Get-Prop $lct 'MaxExcursion')" }
            }
        }

        $status = $(if ($null -ne $provenBy) { 'Proven' } else { 'AcceptedUnproven' })

        # ---- panel 7 is AcceptedUnproven BY USER DECISION, not by measurement -------------------
        if ($pid14 -eq 7) {
            $status = 'AcceptedUnproven'
            $provenBy = $null
            $reasonFrom = 'user decision (locked at phase level, recorded in 88-PROBE-DECISIONS.md and every plan since)'
            $reason = 'ACCEPTED-UNPROVEN BY USER DECISION, NOT BY MEASUREMENT. No broker-fault scenario was designed for `processor_spawn_dropped_total` — deliberately, and that is the decision itself rather than a gap in the evidence. Reaching the counter needs a spawn to be DROPPED, which on this stack means a broker or dispatch fault this phase chose not to drive. Panel 7 is therefore proven to render `0` (it was read as an exactly-zero cross-talk control during ZERO-03, at exactly 0 across every sub-window, while a real fault was firing elsewhere) and has NEVER been proven to render non-zero. Because its expression is `sum(increase(processor_spawn_dropped_total[$__range])) or vector(0)`, its guarded green `0` is INDISTINGUISHABLE from "the counter has never been emitted at all" — the same defect the phase measured on panels 6, 8 and 13. Maintenance must treat a green panel 7 as "no evidence" rather than as "no dropped spawns".'
        }

        if ($status -eq 'AcceptedUnproven' -and [string]::IsNullOrWhiteSpace($reason)) {
            $defects += "panel ${pid14} is AcceptedUnproven with NO reason — an unexplained gap is a defect in the record, not a neutral outcome"
        }

        # ---- misleading by default, each note grounded in what a scenario MEASURED ---------------
        $mislead = @{ flag = $false; note = '' }
        switch ($pid14) {
            1 { $mislead = @{ flag = $false; note = 'NOT misleading by default, and it is the only one of the fourteen that is not. Its series are unguarded, they carry a live non-zero level on a healthy stack, and under a real orchestrator outage SCALE-02 measured both legend Means FALLING visibly (consumed 0.734 -> 0.139, sent 1.370 -> 0.257) rather than emptying or holding a comfortable value. The one caveat worth stating is that a 240 s outage did NOT reach "No data": what an operator actually sees is a fall, not a blank.' } }
            2 { $mislead = @{ flag = $true;  note = 'TWO independent ways. (1) The `or vector(0)` guard makes a green 0 indistinguishable from "keeper_messages_consumed_total has never been emitted at all" — which was literally true of this cluster at Phase-87 measurement time. (2) MEASURED IN 88-07: the rate() form is BLIND to a recovery burst confined to a single 60 s export interval. ZERO-02 run 1 drove a genuine event — both replicas claimed, three KeeperReinjects flowed, the keeper consumed and sent all three — and the panel rendered an UNBROKEN 0, because the counter series is born carrying its final value and rate() differences consecutive samples. A real recovery event may therefore leave this panel at zero. A further artifact of the guard, measured in ZERO-02 run 2: when the real series arrives the guard row does NOT disappear, so the panel renders a permanent phantom `consumed` at 0 beside the real `consumed keeper`, with nothing on the panel to say which is which.' } }
            3 { $mislead = @{ flag = $true;  note = 'The fall is a four-minute SLOPE, not a cliff, and its first in-fault sample still reads inside the healthy band. SCALE-01 measured 0.737 -> 0.632 -> 0.447 -> 0.169 -> 0 across five minutes of a TOTAL processor-tier outage, because the 240 s rate lookback smears the fault across four windows. An operator watching for a cliff will miss the outage; the signal is the slope over several minutes. LADDER-01 sharpens this into a duration: see this row''s ladderRungs.' } }
            4 { $mislead = @{ flag = $true;  note = 'TWO ways, both measured. (1) STRUCTURAL OFFSET THAT CAN NEVER READ 0: BASE-01 recorded a wide-window level of 2.11 on a demonstrably healthy stack, so the number an operator sees is always non-zero and growing — applying a "should be zero" reading to it is a guaranteed false positive, which is why the wide level is RECORDED AND NEVER SCORED. (2) IT GOES BLANK UNDER THE FAULT IT EXISTS TO REVEAL: sum(increase(proc_consumed[$__range])) over a window with no real samples is an EMPTY vector and `A - empty` is empty, so SCALE-01 measured the stat whose own title says "read the trend" rendering "No data" rather than sloping up. It also cannot be read at all at a 60 s range (increase() needs two samples and the stored resolution is 60 s).' } }
            5 { $mislead = @{ flag = $true;  note = 'TWO ways, both measured. (1) STRUCTURAL OFFSET: the gap between orchestrator sends and processor pickups carries a permanent non-zero level on a healthy stack. (2) IT DECAYS AND EMPTIES RATHER THAN SPIKING: SCALE-01 measured 0.63 -> 0.588 -> 0.4 -> 0.234 -> 0.183 and then "No data" under a total processor outage. The research model (the orchestrator keeps sending while pickups stop, so the gap spikes to the full send rate) assumes an INDEPENDENT producer; this pipeline is request/response, so the orchestrator''s own send rate falls with the processors and the gap shrinks before the panel goes blank.' } }
            6 { $mislead = @{ flag = $true;  note = 'A guarded green `0` is indistinguishable from "orchestrator_step_unresolved_total has never been emitted". ZERO-01 established that the counter is unreachable from outside src/ — the API dangling-edge route is refused 422 at step-create and the L2 step key is rewritten from Postgres by any stop/start cycle — so this phase never observed the panel non-zero and could not. Maintenance must corroborate a non-zero reading against the orchestrator log line `Dangling next-step id {NextStepId} - skipping (business)` (one line per increment) and must read a green 0 as "either no dangling edge occurred OR the counter has never been emitted at all".' } }
            7 { $mislead = @{ flag = $true;  note = 'A guarded green `0` is indistinguishable from "processor_spawn_dropped_total has never been emitted". Panel 7 was read at exactly 0 across every sub-window of ZERO-03 while a real fault fired elsewhere, so the zero is genuine — but no scenario has ever driven it non-zero, by user decision, so nothing on this dashboard distinguishes "no dropped spawns" from "this counter has never existed".' } }
            8 { $mislead = @{ flag = $true;  note = 'THE WORST OF THE FOURTEEN, measured three ways in ZERO-03. (1) At a 60 s range BOTH increase() terms return ZERO POINTS and the guarded expression returns seven points of 0 — the green an operator reads at a short range is supplied ENTIRELY by `or vector(0)` and nothing measured it. This is panel 4''s emptiness in its GUARDED form, and it is worse, because panel 4 at least renders "No data" and says so. (2) The gap arithmetic cannot show a suppressed redispatch AT ALL: the Phase-79 seam calls CountSent(...) on BOTH branches by design, so consumed and sent increase term-for-term identically while a message is being dropped. (3) The only branch that does open a gap (the ReinjectConsumer drop path) is unreachable by any lever this phase is allowed to pull.' } }
            9 { $mislead = @{ flag = $true;  note = 'THE AXIS LIES ABOUT THE VARIANCE. The L2 probe heartbeat is a metronome: BASE-01 measured a mean of 0.2000 with a sample sigma so small that its unfloored 3-sigma band would have been roughly 0.0006 wide — which is why FloorApplied is true and the recorded band is the +/-10 % floor 0.1800..0.2200. Grafana auto-scales the y-axis to the data, so a band roughly 0.0004 wide is magnified to fill the panel and a metronome renders as apparent volatility. Second measured property: under a dead keeper the panel does NOT go blank immediately — SCALE-03 measured it FADING through 0.197, 0.203, 0.168, 0.129, 0.0644 for roughly three minutes before "No data", because rate(counter[240s]) keeps producing a shrinking value until fewer than two samples remain inside the lookback.' } }
            10 { $mislead = @{ flag = $true; note = 'The panel renders an UNLABELLED series called `Value`. The 404 driver hits an unmatched path, which carries no http_route label, so Grafana names its row `Value` — and WEB-01 measured that this is the row that moves most cleanly, with an operator having no way to tell from the panel what it is. Separately, seven of its thirteen route series band at EXACTLY zero on a healthy stack (routes that are never called), so most of the legend is permanently flat and carries no information.' } }
            11 { $mislead = @{ flag = $true; note = 'THE SIGNAL IS THE LEADING EDGE, NOT THE LEVEL. WEB-01 measured the p95 spiking at load ONSET (22.0 ms, 19.7 ms) and DECAYING BACK INSIDE the healthy band (to 4.8 ms) within three minutes of sustained load — the API is genuinely fast once warm. A maintenance reader watching this panel for a sustained latency shift would see nothing. Its band is also very wide and partly negative (one BASE-01 series banded -5.06..21.83 around a mean of 8.38, and one yielded only 2 of 10 samples), so only a large excursion can clear it.' } }
            12 { $mislead = @{ flag = $true; note = 'THE STATUS SERIES NEVER LEAVE THE LEGEND. All six (200/201/204/400/404/422) were present AND RENDERING ZERO before WEB-01 drove any of them and remained the same six afterwards: Prometheus retains a counter series for as long as the emitting process lives, and the WebApi pod has been up since Phase 80. A status code that has occurred ONCE therefore never disappears, so an operator cannot read "this code is happening" from the legend — only from the value. The discrimination is numeric (an exactly-0..0 band going non-zero), never a legend gain.' } }
            13 { $mislead = @{ flag = $true; note = 'A guarded green `0` is indistinguishable from "no 5xx counter has ever been emitted". WEB-02 established over SEVEN enumerated endpoints that no WebApi route returns 5xx on safe input (five 404, one 400, one 422) — which is a GOOD result about the product and a blocking one for this panel — so the ratio was observed at exactly 0 under real traffic including 404s and 400s, and has never been seen non-zero. Its threshold turns red at 0.0001, so the panel is honest ONCE the counter exists; nothing tells an operator whether it does.' } }
            14 { $mislead = @{ flag = $true; note = 'ONE OF ITS THREE SERIES IS A DEAD INSTRUMENT UNDER REAL LOAD. WEB-01 measured http_server_active_requests reading EXACTLY 0 at every one of six export ticks while ten parallel requesters issued 2456 requests over 480 s — each request completes in single-digit milliseconds and the gauge is instantaneous at a 60 s export cadence, so in-flight work is essentially never sampled. kestrel_queued_connections likewise banded at exactly zero. The panel''s only readable series is kestrel_active_connections, which is a CONNECTION count kept alive by HTTP keep-alive and therefore a different kind of quantity from the title''s "in-flight requests".' } }
        }

        # ---- the things that must not be smoothed away, carried on the row they are about --------
        $rowDiscards = @()
        foreach ($d in $DiscardedRuns) {
            if (@($d.Panels) -notcontains $pid14) { continue }
            $rowDiscards += [pscustomobject]@{ file = "$($d.File)"; scenarioId = "$($d.Scenario)"; defect = "$($d.Defect)" }
        }
        $rowCriteria = @()
        foreach ($c in $UnsatisfiableCriteria) {
            if ([int]$c.PanelId -ne $pid14) { continue }
            $rowCriteria += [pscustomobject]@{ criterion = "$($c.Criterion)"; measured = "$($c.Measured)"; correctedIn = "$($c.CorrectedIn)" }
        }
        $rowEscalations = @()
        foreach ($e in $Escalations) {
            if ([int]$e.PanelId -ne $pid14) { continue }
            $rowEscalations += [pscustomobject]@{ requirement = "$($e.Requirement)"; text = "$($e.Text)" }
        }

        if ($status -eq 'Proven') { $sourceMap += "status=Proven <- the PanelResults[] entry in analyzer-reports/$($ArtifactFiles[$provenBy])" }
        elseif ($pid14 -eq 7) { $sourceMap += 'status=AcceptedUnproven, reason <- user decision; the exactly-zero reading <- analyzer-reports/phase-88-ZERO-03.json (cross-talk control)' }
        elseif (-not [string]::IsNullOrWhiteSpace($reasonFrom)) { $sourceMap += "status=AcceptedUnproven, reason <- analyzer-reports/$($ArtifactFiles[$reasonFrom])" }

        $rows += [pscustomobject]@{
            panelId             = $pid14
            panelTitle          = "$($facts.Title)"
            panelType           = "$($facts.Type)"
            regime              = $regime
            guarded             = [bool]$facts.Guarded
            thresholds          = [string[]]@($facts.Thresholds)
            queries             = [string[]]@($facts.Queries)

            baselineBandSeries  = [object[]]@($bandSeries)
            baselineBand        = $envelope
            baselineBandNote    = 'baselineBandSeries[] reproduces EVERY per-series band entry from BASE-01 verbatim; baselineBand is their WIDEST ENVELOPE (min low, max high, the OR of floorApplied) and is a HUMAN SUMMARY for reading this matrix, nothing more. No scenario scores against the envelope — every movement decision was already made PER SERIES by the scenario harness against the per-series band. Choosing a "primary" series was explicitly rejected: it would silently discard a series whose band differs.'
            unevaluableSeries   = [string[]]@($unevaluable)

            observedStates      = [string[]]@(@($states) | Select-Object -Unique)
            observedStateRecords = [object[]]@($stateRecords)
            observedStatesNote  = 'Accumulated from EVERY artifact in which the panel appears — as an asserted subject, as a cross-talk control, as an observed-only reading, or as a ladder rung. Each token is derived from a value the scenario RECORDED (Moved, ObservedDirection, StayedPut, AfterStates), never from a re-evaluation of the panel.'

            provenBy            = $provenBy
            alsoProvenBy        = [string[]]@(@($alsoProvenBy) | Select-Object -Unique)
            status              = $status
            reason              = $reason
            reasonSource        = $reasonFrom
            assertedIn          = [string[]]@($assertedIn)
            observedIn          = [string[]]@($observedIn)
            crossTalkAppearances = [string[]]@(@($crossTalk) | Select-Object -Unique)
            ladderRungs         = [object[]]@($ladderRungs)

            misleadingByDefault = [pscustomobject]@{ flag = [bool]$mislead.flag; note = "$($mislead.note)" }

            discardedRuns       = [object[]]@($rowDiscards)
            measuredUnsatisfiableCriteria = [object[]]@($rowCriteria)
            escalations         = [object[]]@($rowEscalations)

            sourceArtifact      = (@($sourceMap) -join ' | ')
            sourceArtifacts     = [string[]]@(@($sources) | Select-Object -Unique)
        }
    }

    # =========================================================================================
    # WRITE, PRINT, CAPSTONE.
    # =========================================================================================
    if (@($rows).Count -ne 14) {
        $defects += "the matrix carries $(@($rows).Count) rows, not 14"
    }

    $outPath = Join-Path $reportDir 'phase-88-discrimination.json'
    # -Depth 10, never shallower: the rows carry objects inside arrays inside the row
    # (baselineBandSeries[].values[], observedStateRecords[], ladderRungs[]) and a shallower
    # serialisation writes them as the literal type name, destroying the evidence that makes every
    # value in this matrix traceable back to the artifact it came from (T-88-18).
    ([object[]]$rows) | ConvertTo-Json -Depth 10 | Set-Content -Path $outPath -Encoding utf8
    $written = Get-Content $outPath -Raw
    if ($written -match '(?m)(:\s*"System\.Object\[\]"|^\s*"System\.Object\[\]"\s*,?\s*$)') {
        Write-Phase "the written matrix carries a JSON VALUE of 'System.Object-array' — the serialisation depth is too shallow." 'Red'
        exit 66
    }

    @($rows | Select-Object `
        @{ n = 'panel'; e = { $_.panelId } }, `
        @{ n = 'title'; e = { if ("$($_.panelTitle)".Length -gt 46) { "$($_.panelTitle)".Substring(0, 45) + '~' } else { "$($_.panelTitle)" } } }, `
        @{ n = 'reg'; e = { $_.regime } }, `
        @{ n = 'grd'; e = { if ($_.guarded) { 'Y' } else { '-' } } }, `
        @{ n = 'band'; e = { if ($null -eq $_.baselineBand.low) { 'none' } else { "{0:N4}..{1:N4}" -f [double]$_.baselineBand.low, [double]$_.baselineBand.high } } }, `
        @{ n = 'ser'; e = { $_.baselineBand.seriesCount } }, `
        @{ n = 'states'; e = { (@($_.observedStates) -join ',') } }, `
        @{ n = 'provenBy'; e = { if ($null -eq $_.provenBy) { '-' } else { $_.provenBy } } }, `
        @{ n = 'status'; e = { $_.status } }, `
        @{ n = 'misl'; e = { if ($_.misleadingByDefault.flag) { 'Y' } else { '-' } } }) |
      Format-Table -AutoSize | Out-String -Width 240 | Write-Host

    $proven = @($rows | Where-Object { "$($_.status)" -eq 'Proven' }).Count
    $unproven = @($rows | Where-Object { "$($_.status)" -eq 'AcceptedUnproven' }).Count
    $misleading = @($rows | Where-Object { $_.misleadingByDefault.flag }).Count
    $withDiscards = @($rows | Where-Object { @($_.discardedRuns).Count -gt 0 }).Count
    $withCriteria = @($rows | Where-Object { @($_.measuredUnsatisfiableCriteria).Count -gt 0 }).Count
    $withEscalations = @($rows | Where-Object { @($_.escalations).Count -gt 0 }).Count

    Write-Phase "matrix: $outPath" 'Green'
    Write-Phase "  $misleading/14 panels are MISLEADING BY DEFAULT with a measured note" 'Yellow'
    Write-Phase "  $withDiscards/14 rows carry a discarded run; $withCriteria/14 carry a measured-unsatisfiable acceptance criterion; $withEscalations/14 carry an escalation" 'Yellow'

    if (@($defects).Count -gt 0) {
        foreach ($d in $defects) { Write-Phase "DEFECT: $d" 'Red' }
        Write-Phase "CAPSTONE: $proven/14 Proven, $unproven/14 AcceptedUnproven — WITH $(@($defects).Count) DEFECT(S) IN THE RECORD" 'Red'
        $resolved = Resolve-SweepClass 1
        Write-Phase "class=$($resolved.Class) exit=1" 'Gray'
        exit 1
    }

    Write-Phase "CAPSTONE: $proven/14 Proven, $unproven/14 AcceptedUnproven" $(if ($proven + $unproven -eq 14) { 'Green' } else { 'Red' })
    $resolved = Resolve-SweepClass 0
    Write-Phase "class=$($resolved.Class) exit=0" 'Gray'
    exit 0
}
finally {
    Pop-Location
}
