<#
.SYNOPSIS
    Re-derive a Phase-88 scenario artifact's LegendNames* fields from a re-read of the SAME pinned
    absolute windows, after the by-index legend collector was measured wrong.

.DESCRIPTION
    WHY THIS EXISTS, AND WHY IT IS NOT A RE-ROLLED MEASUREMENT.

    ZERO-02 run 2 measured its panel correctly: the guarded 0..0 band was left for four consecutive
    sub-windows and the legend gained labelled series. The ARTIFACT then mis-recorded the second
    signal, because `Get-Phase88LegendNames` walked `$Reading.Entries`, which is keyed by ROW INDEX
    and takes each row's name from the FIRST sub-window in which that index existed. A legend that
    GAINS rows mid-capture — which is exactly the guarded-panel transition the scenario asserts —
    shifts every later index, so the collector reported a stale name for the row that mattered.
    Panel 2 rendered, verbatim:

        windows 1-2:  consumed 0 ops/s | sent 0 ops/s
        windows 3-6:  consumed 0 | consumed keeper 0.00944 | sent 0 | sent keeper 0.00944

    and the artifact recorded `consumed, sent, sent, sent keeper` — silently dropping
    `consumed keeper`, the one name the assertion is about.

    A re-run would drive a NEW fault over a NEW window and would answer a different question. This
    phase pins ABSOLUTE windows precisely so a reading is reproducible: the windows this script
    re-reads are byte-identical to the ones the run scored, Prometheus retains ~9.4 days, and the
    values are therefore the same values. Re-reading a pinned window with a corrected reader is not
    a second draw — it is the same draw, read again.

    Only the LegendNames* fields are rewritten, and a LegendNamesReDerived provenance block is added
    recording what was replaced, with what, from which windows, and the verbatim rendered text. The
    numeric verdict, the bands, the values, the cross-talk results and every restore claim are left
    exactly as the run wrote them.

.NOTES
    Dev/ops-only tooling. Read-only against the cluster: it renders panels through Grafana and issues
    no mutation of any kind. No product source and no manifest is touched.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScenarioId,
    [Parameter(Mandatory)][int]$PanelId
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    function Write-Phase([string]$m, [string]$c = 'Cyan') { Write-Host "[phase-88-relegend] $m" -ForegroundColor $c }

    . (Join-Path $PSScriptRoot 'lib/phase-88-panel-read.ps1')

    $artifact = Join-Path $repoRoot ("analyzer-reports/phase-88-{0}.json" -f $ScenarioId)
    if (-not (Test-Path -LiteralPath $artifact)) { Write-Phase "no artifact at $artifact" 'Red'; exit 64 }
    $j = Get-Content $artifact -Raw | ConvertFrom-Json

    $entry = @($j.PanelResults | Where-Object { [int]$_.PanelId -eq $PanelId })
    if (@($entry).Count -eq 0) { Write-Phase "artifact has no PanelResults entry for panel $PanelId" 'Red'; exit 64 }
    $entry = $entry[0]

    $adminB64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('admin:admin'))
    $subWidth = [int]$entry.SubWindowSeconds

    # Read BOTH windows the run itself recorded — the pre-arm window (which the run used as the
    # baseline legend) and the after window. Absolute, pinned, and stated in the artifact, so nothing
    # here is chosen by this script.
    function Read-Names([string]$EndUtcText, [int]$Count, [string]$Label) {
        # ConvertFrom-Json renders a JSON date as a LOCAL DateTime and ToString() then drops the kind,
        # so the artifact text can arrive as '07/28/2026 22:32:43' with no offset. Parsing it with the
        # INVARIANT culture and AssumeUniversal keeps the recorded instant, which is the whole point of
        # pinning absolute windows — a window re-read an hour off is a different measurement.
        $end = [datetime]::Parse($EndUtcText, [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
        $wins = @(Get-PinnedWindowSeries -EndUtc $end -SubWindowSeconds $subWidth -Count $Count)
        $shot = Join-Path $repoRoot ("analyzer-reports/phase-88-screenshots/{0}" -f $ScenarioId)
        $b = Invoke-PanelReadBatch -PanelIds @("$PanelId") -Windows $wins -ScreenshotDir $shot `
               -LocatorMode 'viewpanel' -ViewportWidth 1920 -ViewportHeight 1080 -BasicAuthBase64 $adminB64
        $union = @()
        $perWindow = @()
        foreach ($r in @($b.Readings)) {
            $rn = @(Get-PropertyNames $r)
            if ($rn -notcontains 'panelId' -or "$($r.panelId)" -ne "$PanelId") { continue }
            $rowNames = @()
            if ($rn -contains 'series') {
                foreach ($s in @($r.series)) {
                    $sn = @(Get-PropertyNames $s)
                    $nm = $(if ($sn -contains 'name') { "$($s.name)" } else { '' })
                    $rowNames += $nm
                    if ($union -notcontains $nm) { $union += $nm }
                }
            }
            $perWindow += [pscustomobject]@{
                FromUtc     = $(if ($rn -contains 'fromUtc') { "$($r.fromUtc)" } else { '' })
                ToUtc       = $(if ($rn -contains 'toUtc') { "$($r.toUtc)" } else { '' })
                LegendNames = [string[]]@($rowNames)
                RawText     = $(if ($rn -contains 'rawText') { (("$($r.rawText)") -replace "`r?`n", ' / ') } else { '' })
            }
        }
        Write-Phase "  [$Label] union = $((@($union | ForEach-Object { "[$_]" }) -join ' '))" 'Gray'
        return [pscustomobject]@{ Union = [string[]]@($union); PerWindow = @($perWindow); State = "$($b.State)" }
    }

    Write-Phase "re-reading panel $PanelId over the artifact's own pinned windows (read-only; no mutation)"
    $baseRead  = Read-Names -EndUtcText "$($j.PreArmWindowEnd)"   -Count ([int]$j.SubWindowCount) -Label 'pre-arm'
    $afterRead = Read-Names -EndUtcText "$($j.AfterWindowEnd)"    -Count ([int]$j.SubWindowCount) -Label 'after'

    $oldBase  = [string[]]@($entry.LegendNamesBaseline)
    $oldAfter = [string[]]@($entry.LegendNamesAfter)

    $entry.LegendNamesBaseline = [string[]]@($baseRead.Union)
    $entry.LegendNamesAfter    = [string[]]@($afterRead.Union)
    $entry | Add-Member -Force -NotePropertyName 'LegendNamesBaselinePerWindow' -NotePropertyValue @($baseRead.PerWindow)
    $entry | Add-Member -Force -NotePropertyName 'LegendNamesAfterPerWindow'    -NotePropertyValue @($afterRead.PerWindow)

    $j | Add-Member -Force -NotePropertyName 'LegendNamesReDerived' -NotePropertyValue ([pscustomobject]@{
        PanelId              = $PanelId
        ReDerivedUtc         = ([DateTimeOffset]::UtcNow).ToString('o')
        Reason               = 'the run''s by-index legend collector takes each row''s name from the FIRST sub-window in which that index existed, so a legend that GAINS rows mid-capture reports a stale name for every row after the insertion point — dropping exactly the name the guarded-panel assertion is about'
        ReplacedBaseline     = $oldBase
        ReplacedAfter        = $oldAfter
        BaselineWindowEnd    = "$($j.PreArmWindowEnd)"
        AfterWindowEnd       = "$($j.AfterWindowEnd)"
        SubWindowSeconds     = $subWidth
        SubWindowCount       = [int]$j.SubWindowCount
        BaselineBatchState   = "$($baseRead.State)"
        AfterBatchState      = "$($afterRead.State)"
        NotARerun            = 'The windows re-read here are the ABSOLUTE, pinned windows the run itself recorded, and Prometheus retains ~9.4 days — so these are the same samples the run scored, read again with a corrected collector. No fault was driven, no cluster mutation was issued, and no numeric value, band, cross-talk result or restore claim was altered: only the LegendNames* fields, whose previous contents are preserved above.'
    })

    ([pscustomobject]$j) | ConvertTo-Json -Depth 10 | Set-Content -Path $artifact -Encoding utf8
    $written = Get-Content $artifact -Raw
    if ($written -match '(?m)(:\s*"System\.Object\[\]"|^\s*"System\.Object\[\]"\s*,?\s*$)') {
        Write-Phase "the rewritten artifact carries a JSON VALUE of 'System.Object[]' — serialisation depth too shallow." 'Red'
        exit 66
    }
    Write-Phase "re-derived: baseline $((@($oldBase) -join ',')) -> $((@($baseRead.Union) -join ','))" 'Green'
    Write-Phase "re-derived: after    $((@($oldAfter) -join ',')) -> $((@($afterRead.Union) -join ','))" 'Green'
    exit 0
}
finally { Pop-Location }
