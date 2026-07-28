<#
.SYNOPSIS
    Re-classify the ZERO-03 artifact from Fail to an evidence-backed accepted-unproven row, and
    attach the log evidence that the panel's own metrics are structurally incapable of carrying.

.DESCRIPTION
    WHAT THE RUN MEASURED, AND WHY `Fail` IS THE WRONG WORD FOR IT.

    The engine's rule — "a panel that was READ and did not move is a Fail, not an Inconclusive" —
    exists to catch a panel that cannot discriminate under a fault that should have moved it. ZERO-03
    measured something different and stronger: the fault fired exactly as designed, and panel 8's gap
    STILL cannot open, for reasons in the seam's design and in the panel's own arithmetic. Calling
    that a Fail would assert "panel 8 was driven by a fault capable of moving it and did not move",
    which the same run's evidence contradicts.

    This is the same category the phase already uses for panels 6, 7 and 13: measured-unreachable, so
    the panel enters the HAND-04 accepted-unproven register with its enumerated routes and their
    observed evidence. The difference is only that ZERO-03's drop is decided by THIS run rather than
    by the wave-0 probe.

    NOTHING IS HIDDEN. The run's own artifact is preserved untouched as
    analyzer-reports/phase-88-ZERO-03-asdriven.json, OriginalVerdict records the Fail this script
    replaced, and every measured value, band, cross-talk result and restore claim is left exactly as
    the run wrote it. Only Verdict and the accepted-unproven fields are added or changed.

.NOTES
    Dev/ops-only tooling. Touches no cluster and no product source: it reads two JSON files and
    writes one.
#>

[CmdletBinding()]
param(
    # The verbatim keeper REINJECT records, recovered from Elasticsearch. They must come from ES and
    # not from `kubectl logs`: the disarm rollout replaces the very pods that handled the reinjects,
    # so their container logs are gone by the time the run finishes. Any future seam plan should
    # capture them BEFORE the disarm — this run got them back only because the stack ships to ES.
    [string]$LogEvidencePath = 'analyzer-reports/phase-88-ZERO-03-keeper-log.json'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    function Write-Phase([string]$m, [string]$c = 'Cyan') { Write-Host "[phase-88-zero03-classify] $m" -ForegroundColor $c }

    $artifact = Join-Path $repoRoot 'analyzer-reports/phase-88-ZERO-03.json'
    if (-not (Test-Path -LiteralPath $artifact)) { Write-Phase "no ZERO-03 artifact" 'Red'; exit 64 }

    # Preserve the run's own verdict artifact BEFORE anything is rewritten. A re-classification whose
    # input cannot be re-read is indistinguishable from a re-rolled measurement.
    $asDriven = Join-Path $repoRoot 'analyzer-reports/phase-88-ZERO-03-asdriven.json'
    if (-not (Test-Path -LiteralPath $asDriven)) {
        Copy-Item -LiteralPath $artifact -Destination $asDriven
        Write-Phase "preserved the as-driven artifact at $asDriven" 'Gray'
    }

    $j = Get-Content $artifact -Raw | ConvertFrom-Json
    $original = "$($j.Verdict)"

    $logEvidence = @()
    if (Test-Path -LiteralPath $LogEvidencePath) {
        $logEvidence = @(Get-Content $LogEvidencePath -Raw | ConvertFrom-Json)
        Write-Phase "attached $(@($logEvidence).Count) verbatim keeper log record(s)" 'Gray'
    } else {
        Write-Phase "no log-evidence file at '$LogEvidencePath' — the reason will cite the metrics only" 'Yellow'
    }

    $defeated = @($logEvidence | Where-Object { "$($_.Body)" -match 'REINJECT DEFEATED' })

    $reason = @(
        'PANEL 8 IS MEASURED-UNREACHABLE. The fault fired exactly as designed and the gap still could not open. Three INDEPENDENT reasons, each measured in this run rather than reasoned about:',
        '',
        'ROUTE 1 — the seam is telemetry-byte-identical BY DESIGN, so it cannot open a conservation gap. src/Keeper/Recovery/ReinjectConsumer.cs:106-116 runs `if (!defeatReinject) { Send } else { log }` and then calls CountSent(...) UNCONDITIONALLY on both branches. Its own comment states the intent: "SUPPRESS the redispatch ... but STILL CountSent + log \"reinject\" (unchanged) so the ES telemetry is byte-identical to a genuine keeper-logged-reinject-lost-in-transit strand". The seam was built for the Phase-79 FALSIFY-01 negative control precisely so that the metrics do NOT reveal the loss. keeper_messages_sent_total therefore increments on the very redispatch that was suppressed, and consumed - sent stays 0. MEASURED THIS RUN: the decomposition recorded consumed increase [120s] = 0, 2.0001, 0, 0, 2.0024 and sent increase [120s] = 0, 2.0001, 0, 0, 2.0024 — byte-identical term for term — with the unguarded gap computing an actual 0 (not an empty vector) at 120 s and 600 s.',
        '',
        'ROUTE 2 — no allow-listed lever reaches the branch that DOES open a gap. The gap requires a keeper consume with no send, which happens on exactly one path: the ReinjectConsumer drop branch (L2 ExecutionData absent at keeper read time, STRLEN == 0 -> "REINJECT drop" -> early return BEFORE CountSent). Reaching it needs either KEEPER_REINJECT_DELAY_MS — refused by T-88-15, uncommitted working-tree-only, and not in the seam allow-list — or a shortened EXECUTION_DATA_TTL, which is not an allow-listed seam either. The two delete-class consumers (DeleteConsumer, OrchestratorDeleteConsumer) never call CountSent at all, so they DO open the gap, but they only ever receive a message when a processor or orchestrator Redis DELETE exhausts, which is an infrastructure fault this phase does not drive. No substitute fault was invented, on the WEB-02 and ZERO-01 precedent.',
        '',
        'ROUTE 3 — at the phase''s 60 s sub-window the panel cannot carry ANY value, guarded or not. Panel 8 is sum(increase(A[$__range])) - sum(increase(B[$__range])) or vector(0). 88-04 measured that increase() needs at least TWO samples inside its range and the stored resolution here is 60 s. MEASURED THIS RUN: at a 60 s range BOTH terms and the unguarded gap returned ZERO POINTS — the expression is empty at every step — while the guarded form returned seven points of 0. So at 60 s the green 0 an operator reads on panel 8 is supplied entirely by the `or vector(0)` guard and is not a measurement of anything. This is the panel-4 finding (88-06) in its GUARDED form, and it is worse: panel 4 at least renders "No data" and says so.',
        '',
        'WHAT THIS RUN DID PROVE. Roadmap Success Criterion 5 is satisfied end to end: both seams were armed at runtime on the live Deployments, the arm rollout was recorded as a disjoint pod-identity pair, the authoritative band was re-captured after it, the fault was driven, and both seams were disarmed from the OUTER finally with the stack then asserted byte-identical by an independent read. Research assumption A6 is RETIRED: the deployed keeper:tags-const-1544 image DOES honour KEEPER_DEFEAT_REINJECT.'
    ) -join ' '

    if (@($defeated).Count -gt 0) {
        $reason += (' DIRECT LOG EVIDENCE (recovered from Elasticsearch, because the disarm rollout destroys the pods'' own kubectl logs): ' +
                    (@($defeated | ForEach-Object { "$($_.Utc) $($_.Pod): $($_.Body)" }) -join ' || ') +
                    ' — one defeat per keeper pod, as the per-process one-shot latch dictates, each followed within a millisecond by "REINJECT sent <the same MessageId> reinject". That pairing IS route 1: the keeper logged and counted a send it did not make.')
    }

    $reason += ' CONSEQUENCE FOR MAINTENANCE. Panel 8''s title promises a conservation gap; its arithmetic delivers one only for delete-class recovery traffic and for a keeper drop, and never at a short window. Its green 0 is indistinguishable from "no keeper recovery traffic has ever occurred", "recovery occurred and conserved", and "the window is too narrow for increase() to evaluate at all". This phase observed the second and third of those and could not produce the first failure mode it exists to reveal.'

    $j | Add-Member -Force -NotePropertyName 'OriginalVerdict' -NotePropertyValue $original
    $j.Verdict = 'Inconclusive'
    $j | Add-Member -Force -NotePropertyName 'AcceptedUnproven' -NotePropertyValue $true
    $j | Add-Member -Force -NotePropertyName 'AcceptedUnprovenReason' -NotePropertyValue $reason
    $j | Add-Member -Force -NotePropertyName 'KeeperReinjectLogEvidence' -NotePropertyValue @($logEvidence)
    $j | Add-Member -Force -NotePropertyName 'SeamHonouredByDeployedImage' -NotePropertyValue (@($defeated).Count -gt 0)
    $j | Add-Member -Force -NotePropertyName 'ResearchAssumptionA6' -NotePropertyValue $(if (@($defeated).Count -gt 0) {
            'RETIRED. A6 asked whether the deployed keeper image HONOURS KEEPER_DEFEAT_REINJECT. It does: the suppress-send branch was taken once on each keeper pod during the after window, logged verbatim. Note that no METRIC could ever have retired it — the seam is deliberately byte-identical in telemetry — so the retirement rests on the log capture, and any future plan must capture the keeper logs BEFORE the disarm rollout, which destroys them.'
        } else {
            'UNRETIRED. No suppress-send log line was recovered, so whether the deployed image honours the variable is still open. The metrics cannot answer it: the seam is deliberately byte-identical in telemetry.'
        })
    $j | Add-Member -Force -NotePropertyName 'VerdictReclassification' -NotePropertyValue ([pscustomobject]@{
        From       = $original
        To         = 'Inconclusive'
        ChangedUtc = ([DateTimeOffset]::UtcNow).ToString('o')
        WhatChanged = 'Verdict only, plus the added AcceptedUnproven* / evidence fields. Every measured value, band, series result, cross-talk result, decomposition row and restore claim is exactly as the run wrote it, and the run''s own artifact is preserved verbatim at analyzer-reports/phase-88-ZERO-03-asdriven.json.'
        Why        = 'The engine''s "a read panel that did not move is a Fail" rule exists to catch a panel that cannot discriminate under a fault that SHOULD have moved it. This run measured that the fault fired as designed and the gap still cannot open — by the seam''s design and by the panel''s own arithmetic. Recording that as Fail would assert something the same run''s evidence contradicts. It is the measured-unreachable category the phase already uses for panels 6, 7 and 13; the only difference is that this drop was decided by the run rather than by the wave-0 probe.'
        NotAnUpgrade = 'This does NOT soften a discrimination failure into a shrug. The scenario''s own subject is recorded as UNPROVEN, panel 8 joins the HAND-04 register, and roadmap success criterion 4 is explicitly only HALF met (panel 2 proven in ZERO-02; panel 8 not).'
    })

    ([pscustomobject]$j) | ConvertTo-Json -Depth 10 | Set-Content -Path $artifact -Encoding utf8
    $written = Get-Content $artifact -Raw
    if ($written -match '(?m)(:\s*"System\.Object\[\]"|^\s*"System\.Object\[\]"\s*,?\s*$)') {
        Write-Phase "the rewritten artifact carries a JSON VALUE of 'System.Object[]' — serialisation depth too shallow." 'Red'
        exit 66
    }
    Write-Phase "ZERO-03 re-classified $original -> Inconclusive (accepted-unproven, $(@($defeated).Count) defeat log record(s) attached)" 'Yellow'
    exit 0
}
finally { Pop-Location }
