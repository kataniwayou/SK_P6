<#
.SYNOPSIS
    Dot-sourceable cluster-operations library for Phase 88's live fault scenarios: live-read replica
    capture, rollout pod-identity capture, and the whole-tier scale-fault sequencer. PURE definitions
    with NO top-level side effects beyond three static allow-list tables — dot-sourcing this file only
    DEFINES functions and tables. It never prints, never calls exit, never Push-Location's, and
    mutates nothing at all until one of its functions is CALLED.

.DESCRIPTION
    WHY THIS FILE EXISTS
        Roadmap success criterion 5 names a runtime-only mechanism for driving the Phase-79 fault
        seams on the k8s stack as a phase DELIVERABLE, because none exists. Both seams are committed
        in src/ (KEEPER_DEFEAT_REINJECT at src/Keeper/Recovery/ReinjectConsumer.cs:104, commit
        7ec9169; PROCESSOR_DEFEAT_READ at src/Processor/.../ProcessorPipeline.cs:110) and both
        short-circuit to inert when unset — but no k8s/ manifest carries either one and no live pod
        has them set (the deliberate Phase-80 D-08 omission). The only two scripts that reference
        them (scripts/phase-67-harness.ps1, scripts/phase-79-falsify.ps1) are compose-era: their
        mechanism is host process-env interpolation at container-create time, which has no
        equivalent on a running k8s pod, and neither file issues a single cluster command. Their
        DISCIPLINE transfers (arm before the observation window opens — phase-67-harness.ps1:130-167;
        clear unconditionally in a finally — :617). Their MECHANISM does not.

    THE STALE REPLICA MAP THIS LIBRARY REFUSES TO COPY  (T-88-02)
        The static replica map at scripts/phase-80-harness.ps1:148-150 declares the orchestrator at
        ONE replica. That is stale: k8s/31-orchestrator.yaml:41 sets `replicas: 3` ("HA — leader-elected
        via k8s Lease (HA-06)", raised by Phase 83) and the live deployment confirms 3. A Phase-88
        scenario that restored from that map would take the orchestrator 3 -> 1 and never put it
        back, silently dismantling the HA the previous milestone shipped — silently, because a single
        orchestrator still serves, still fires the cron, and still passes every conservation check, so
        nothing would ever surface the loss. Therefore: every restore in this library scales back to
        the count READ from the live deployment immediately before the fault, and then re-reads and
        asserts equality as an explicit boolean claim. No replica count is hardcoded anywhere in this
        file, and none is ever accepted from a caller.

    NAMESPACE AND WORKLOAD-NAME SAFETY  (T-88-01 / T-88-04)
        Every command this library issues goes through ONE invocation site — `kubectl -n skp <args>`
        inside Invoke-Phase88Ctl — so a cluster-wide or cross-namespace command is impossible by
        construction. A -Tier parameter is never interpolated into a command: it is matched
        ORDINALLY against the static $Phase88Tiers table and the TABLE'S OWN string is what reaches
        the command line. Anything not in the table returns a failure result before any command is
        issued.

    FAILURE CODES (returned in the result object; this library NEVER calls exit — the CALLER maps a
    FailureCode onto its own process exit code, keeping the "distinct exit code per infra abort"
    convention that stops an abort being read as a verdict):
        60  scale / terminate-wait / rollout failed
        61  seam arm or disarm failed
        62  could not read live replicas or image
        65  restore assertion failed

    EXPORTED SURFACE (Task 1)
        Get-LiveReplicas      -Tier                        -> [int]      (-1 on ANY failure)
        Get-LiveImage         -Tier                        -> [string]   ('' on ANY failure)
        Get-TierPodNames      -Tier                        -> [string[]] (running pod names)
        Wait-TierSettled      -Tier [-TimeoutSeconds]      -> [bool]     (never throws)
        Invoke-TierScaleFault -Tier -DwellSeconds          -> result hashtable (see below)

    POD NAMES ARE THE DISC-06 DISCONTINUITY RECORD
        A pod name IS the service_instance_id label value the dashboard's per-pod panels group by.
        Capturing the running set before and after a rollout is what makes the rollout discontinuity
        RECORDABLE (RolloutOldInstanceIds / RolloutNewInstanceIds) instead of being scored as
        movement — the Pitfall-2 trap that would void every before/after assertion in the phase.

.NOTES
    Dev/ops-only tooling. No product source is touched and no manifest is edited: this library exists
    precisely so that arming a seam or moving a replica count is a RUNTIME act with a matching
    disarm, never a committed change. It starts no port-forward and reads no shared forward PID
    registry — the caller owns its own forward lifecycle (T-88-05).
#>

# =================================================================================================
# STATIC ALLOW-LISTS. These are the ONLY workload names this library will ever name on a command
# line. A caller SELECTS a row; nothing is ever derived from a caller-supplied string (T-88-01,
# the T-81-01 precedent). Deliberately NOT a replica map — see the header.
# =================================================================================================
$script:Phase88Tiers = @('baseapi-service', 'keeper', 'orchestrator', 'processor-sample')

$script:Phase88TierKind = @{
    'baseapi-service'  = 'deployment'
    'keeper'           = 'deployment'
    'orchestrator'     = 'deployment'
    'processor-sample' = 'deployment'
}

# (tier -> the seam vars that tier's image actually honours). Populated for the arm/disarm surface;
# the scale sequencer never reads it.
$script:Phase88Seams = @{
    'keeper'           = @('KEEPER_DEFEAT_REINJECT')
    'processor-sample' = @('PROCESSOR_DEFEAT_READ')
}

# -------------------------------------------------------------------------------------------------
# Ordinal resolution of a caller-supplied tier to the TABLE'S OWN string. Returns $null for anything
# not in the allow-list, including $null/empty and a case variant — the canonical string is what the
# command line gets, never the caller's.
# -------------------------------------------------------------------------------------------------
function Resolve-Phase88Tier {
    [CmdletBinding()]
    param([string]$Tier)

    if ([string]::IsNullOrWhiteSpace($Tier)) { return $null }
    foreach ($known in $script:Phase88Tiers) {
        if ([string]::Equals($known, $Tier, [System.StringComparison]::Ordinal)) { return $known }
    }
    return $null
}

# -------------------------------------------------------------------------------------------------
# The ONE invocation site. Two hazards it exists to neutralise:
#   1. PowerShell 7.3+ can make a non-zero NATIVE exit terminating when the caller has set
#      $ErrorActionPreference = 'Stop' (every harness in this repo does). A library whose whole job
#      is to REPORT failure codes must never explode inside the caller's try, so the preference is
#      shadowed locally for the duration of the call and the exit code is returned as data.
#   2. Merged stderr would corrupt a jsonpath value read. With 2>&1 each native stderr line arrives
#      as an ErrorRecord, so stdout and stderr are separable exactly; the value read only ever sees
#      real stdout.
# -------------------------------------------------------------------------------------------------
function Invoke-Phase88Ctl {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string[]]$Arguments)

    # Local shadows only — the caller's preferences are untouched when this returns.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false

    $stdout = ''
    $stderr = ''
    $code = -1
    try {
        $raw = & kubectl -n skp @Arguments 2>&1
        $code = $LASTEXITCODE
        $all = @($raw)
        $outLines = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] })
        $errLines = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] })
        $stdout = (($outLines | ForEach-Object { "$_" }) -join "`n")
        $stderr = (($errLines | ForEach-Object { "$_" }) -join "`n")
    } catch {
        # Command not found / cannot start: report it, never rethrow into the caller's try.
        $code = -1
        $stderr = "$($_.Exception.Message)"
    }

    Write-Verbose "[phase-88-cluster-ops] $($Arguments -join ' ') -> exit $code"
    return @{
        Ok       = ($code -eq 0)
        ExitCode = $code
        Output   = $stdout
        Error    = $stderr
    }
}

# -------------------------------------------------------------------------------------------------
# Live replica count of a tier's Deployment. -1 on ANY failure (unknown tier, command failure, empty
# or non-numeric capture), so a caller that does not check gets an obviously-invalid restore target
# rather than a plausible-but-wrong one.
#
# The exit code is pinned into a variable BEFORE the capture is trimmed: on a failed call stdout is
# empty, and a .Trim() on the raw capture must never be allowed to throw (or to succeed on a blank)
# in a way that bypasses the failure branch. Idiom from phase-87-dashboards-verify.ps1:370-373.
# -------------------------------------------------------------------------------------------------
function Get-LiveReplicas {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tier)

    $t = Resolve-Phase88Tier $Tier
    if ($null -eq $t) { return -1 }

    $r = Invoke-Phase88Ctl -Arguments @('get', 'deploy', $t, '-o', 'jsonpath={.spec.replicas}')
    $code = $r.ExitCode                      # pinned BEFORE the trim
    $txt = ("$($r.Output)").Trim()
    if ($code -ne 0 -or [string]::IsNullOrWhiteSpace($txt)) { return -1 }

    $parsed = 0
    if (-not [int]::TryParse($txt, [ref]$parsed)) { return -1 }
    return $parsed
}

# -------------------------------------------------------------------------------------------------
# Live container image of a tier's Deployment. '' on ANY failure. Same pin-before-trim idiom.
# The images are the :tags-const-1544 set the dashboards were proven against; asserting them
# unchanged is what catches a kustomize re-apply having quietly reverted the stack to the manifests'
# stale pins, whose bits emit no `source` resource attribute and make every correct panel look broken.
# -------------------------------------------------------------------------------------------------
function Get-LiveImage {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tier)

    $t = Resolve-Phase88Tier $Tier
    if ($null -eq $t) { return '' }

    $r = Invoke-Phase88Ctl -Arguments @('get', 'deploy', $t, '-o', 'jsonpath={.spec.template.spec.containers[0].image}')
    $code = $r.ExitCode                      # pinned BEFORE the trim
    $txt = ("$($r.Output)").Trim()
    if ($code -ne 0 -or [string]::IsNullOrWhiteSpace($txt)) { return '' }
    return $txt
}

# -------------------------------------------------------------------------------------------------
# Running pod names for a tier, WITH the command's own success flag. The distinction matters in the
# terminate-wait: a failed read also yields zero names, and treating that as "the tier is gone" would
# start the dwell while the pods were still serving. The public Get-TierPodNames drops the flag;
# the sequencer uses this one.
# -------------------------------------------------------------------------------------------------
function Get-Phase88PodResult {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tier)

    $t = Resolve-Phase88Tier $Tier
    if ($null -eq $t) { return @{ Ok = $false; Names = @() } }

    $r = Invoke-Phase88Ctl -Arguments @('get', 'pods', '-l', "app=$t", '--field-selector=status.phase=Running', '-o', 'name')
    if (-not $r.Ok) { return @{ Ok = $false; Names = @() } }

    $names = @()
    foreach ($line in @(("$($r.Output)") -split "`r?`n")) {
        $trimmed = ("$line").Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed)) { continue }
        $names += ($trimmed -replace '^pod/', '')
    }
    return @{ Ok = $true; Names = $names }
}

# -------------------------------------------------------------------------------------------------
# Running pod names for a tier. These names ARE the service_instance_id label values the per-pod
# panels group by, so a before/after pair of these lists is the DISC-06 RolloutOldInstanceIds /
# RolloutNewInstanceIds record. Returned UNWRAPPED (no `return ,$array` comma trick) so the repo's
# standard @()-wrap idiom at the call site counts elements, not one nested array (88-01 deviation 1).
# -------------------------------------------------------------------------------------------------
function Get-TierPodNames {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Tier)

    return (Get-Phase88PodResult -Tier $Tier).Names
}

# -------------------------------------------------------------------------------------------------
# Block until a tier's Deployment has finished rolling AND its running pod count equals its declared
# spec.replicas. `rollout status` alone returns as soon as the Deployment reports Available, which on
# a surge strategy can still leave the old pod terminating — and a capture taken then is a capture of
# a transient. Returns $false rather than throwing: a settle timeout is data the caller records, not
# an exception that unwinds its scenario.
# -------------------------------------------------------------------------------------------------
function Wait-TierSettled {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tier,
        [ValidateRange(1, 3600)][int]$TimeoutSeconds = 120
    )

    $t = Resolve-Phase88Tier $Tier
    if ($null -eq $t) { return $false }

    $r = Invoke-Phase88Ctl -Arguments @('rollout', 'status', "deployment/$t", "--timeout=${TimeoutSeconds}s")
    if (-not $r.Ok) { return $false }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $want = Get-LiveReplicas -Tier $t
        $pods = Get-Phase88PodResult -Tier $t
        if ($want -ge 0 -and $pods.Ok -and (@($pods.Names).Count -eq $want)) { return $true }
        Start-Sleep -Seconds 3
    }
    return $false
}

# -------------------------------------------------------------------------------------------------
# Whole-tier scale fault, following the five-step shape of the phase-80 crash sequencer
# (scripts/phase-80-harness.ps1:379-421) MINUS its replica map:
#
#   1. READ the live replica count (and the running pod set) BEFORE anything is touched.
#   2. Scale to zero.
#   3. TERMINATE-WAIT, bounded 90 s. A dwell that starts before the pods are actually gone measures
#      nothing — the fault would be shorter than it claims and the minimum-detectable-duration
#      ladder built on top of it would be wrong.
#   4. Dwell. FaultStartUtc is pinned when step 3 completed; FaultEndUtc when the restore is issued.
#   5. Restore to the count read in step 1 — never a blanket single replica, never a table lookup —
#      then settle, then pin RestoredUtc, then RE-READ and record ReplicasRestored as an explicit
#      claim. Verify, never assume.
#
# Always returns a fully-populated result hashtable (every key present, $null where a step was never
# reached) so a StrictMode caller can read any field on any path.
# -------------------------------------------------------------------------------------------------
function Invoke-TierScaleFault {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tier,
        [Parameter(Mandatory)][ValidateRange(0, 3600)][int]$DwellSeconds
    )

    $result = @{
        Ok               = $false
        FailureCode      = 0
        Tier             = $Tier
        ReplicasBefore   = -1
        ReplicasAfter    = -1
        ReplicasRestored = $false
        PodNamesBefore   = @()
        PodNamesAfter    = @()
        FaultStartUtc    = $null
        FaultEndUtc      = $null
        RestoredUtc      = $null
        Detail           = ''
    }

    $t = Resolve-Phase88Tier $Tier
    if ($null -eq $t) {
        $result.FailureCode = 62
        $result.Detail = "tier '$Tier' is not in the Phase-88 allow-list ($($script:Phase88Tiers -join ', ')) — no command issued."
        return $result
    }
    $kind = $script:Phase88TierKind[$t]

    # --- step 1: live read BEFORE any mutation -----------------------------------------------------
    $replicasBefore = Get-LiveReplicas -Tier $t
    if ($replicasBefore -lt 1) {
        $result.FailureCode = 62
        $result.Detail = "could not read a usable live replica count for '$t' (got $replicasBefore) — refusing to scale a tier whose restore target is unknown."
        return $result
    }
    $result.ReplicasBefore = $replicasBefore

    $before = Get-Phase88PodResult -Tier $t
    if (-not $before.Ok) {
        $result.FailureCode = 62
        $result.Detail = "could not read the running pod set for '$t' — the DISC-06 discontinuity record would be unproducible."
        return $result
    }
    $result.PodNamesBefore = $before.Names

    # --- step 2: scale to zero ---------------------------------------------------------------------
    $scaleDown = Invoke-Phase88Ctl -Arguments @('scale', "$kind/$t", '--replicas=0')
    if (-not $scaleDown.Ok) {
        $result.FailureCode = 60
        $result.Detail = "scale '$t' to zero failed (exit $($scaleDown.ExitCode)): $($scaleDown.Error)"
        return $result
    }

    # --- step 3: TERMINATE-WAIT (bounded 90 s) -----------------------------------------------------
    $termDeadline = (Get-Date).AddSeconds(90)
    $terminated = $false
    while ((Get-Date) -lt $termDeadline) {
        $live = Get-Phase88PodResult -Tier $t
        if ($live.Ok -and @($live.Names).Count -eq 0) { $terminated = $true; break }
        Start-Sleep -Seconds 2
    }
    if (-not $terminated) {
        $result.FailureCode = 60
        $result.Detail = "tier '$t' still had running pods (or was unreadable) 90s after the scale to zero — the dwell would have measured nothing."
        # Best-effort restore so a failed sequencer does not leave the tier down.
        $null = Invoke-Phase88Ctl -Arguments @('scale', "$kind/$t", "--replicas=$replicasBefore")
        return $result
    }

    # --- step 4: dwell -----------------------------------------------------------------------------
    $faultStart = [DateTimeOffset]::UtcNow
    $result.FaultStartUtc = $faultStart.ToString('o')
    if ($DwellSeconds -gt 0) { Start-Sleep -Seconds $DwellSeconds }
    $faultEnd = [DateTimeOffset]::UtcNow
    $result.FaultEndUtc = $faultEnd.ToString('o')

    # --- step 5: restore to the PRE-READ count, settle, then CLAIM ---------------------------------
    $scaleUp = Invoke-Phase88Ctl -Arguments @('scale', "$kind/$t", "--replicas=$replicasBefore")
    if (-not $scaleUp.Ok) {
        $result.FailureCode = 60
        $result.Detail = "restore of '$t' to $replicasBefore replica(s) failed (exit $($scaleUp.ExitCode)): $($scaleUp.Error)"
        return $result
    }

    $settled = Wait-TierSettled -Tier $t -TimeoutSeconds 120
    if (-not $settled) {
        $result.FailureCode = 60
        $result.Detail = "tier '$t' did not settle at $replicasBefore replica(s) within 120s after the restore."
    } else {
        $result.RestoredUtc = ([DateTimeOffset]::UtcNow).ToString('o')
    }

    $after = Get-LiveReplicas -Tier $t
    $result.ReplicasAfter = $after
    $result.ReplicasRestored = ($after -eq $replicasBefore)     # an explicit CLAIM, not an assumption
    $result.PodNamesAfter = (Get-Phase88PodResult -Tier $t).Names

    if ($result.FailureCode -eq 0 -and -not $result.ReplicasRestored) {
        $result.FailureCode = 60
        $result.Detail = "restore claim failed: '$t' reads $after replica(s), expected $replicasBefore."
    }
    $result.Ok = ($result.FailureCode -eq 0)
    return $result
}
