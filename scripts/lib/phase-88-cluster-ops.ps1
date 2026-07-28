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

    EXPORTED SURFACE
        Get-LiveReplicas      -Tier                        -> [int]      (-1 on ANY failure)
        Get-LiveImage         -Tier                        -> [string]   ('' on ANY failure)
        Get-TierPodNames      -Tier                        -> [string[]] (running pod names)
        Wait-TierSettled      -Tier [-TimeoutSeconds]      -> [bool]     (never throws)
        Invoke-TierScaleFault -Tier -DwellSeconds          -> result hashtable (see below)
        Set-Phase88Seam       -Tier -Name [-Value '1']     -> result hashtable
        Clear-Phase88Seam     -Tier -Name                  -> result hashtable
        Assert-StackRestored  -Tiers -ExpectedReplicas -ExpectedImages -> claim hashtable

    POD NAMES ARE THE DISC-06 DISCONTINUITY RECORD
        A pod name IS the service_instance_id label value the dashboard's per-pod panels group by.
        Capturing the running set before and after a rollout is what makes the rollout discontinuity
        RECORDABLE (RolloutOldInstanceIds / RolloutNewInstanceIds) instead of being scored as
        movement — the Pitfall-2 trap that would void every before/after assertion in the phase.
        Both mutating paths capture it: the scale fault AND the seam arm, because arming a seam
        rewrites the Deployment env and therefore rolls the pods exactly as a scale does.

    THE SEAM MECHANISM, AND THE CALLER CONTRACT THAT MAKES IT SAFE  (DISC-05 / T-88-03)
        A seam is armed and disarmed at RUNTIME on the live Deployment env — no manifest is edited.
        Editing one would be pointless as well as forbidden: a kustomize re-apply would strip the
        edit, and it would also revert all four app Deployments to the manifests' stale image pins,
        whose bits emit no `source` resource attribute — which makes every correct dashboard look
        broken (87-FINDINGS.md §9, measured). Arming:

            kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT=1

        and the trailing hyphen is what REMOVES the variable again:

            kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT-

        DISARM-IN-FINALLY IS A HARD CONTRACT, NOT A CONVENIENCE. The caller MUST invoke
        Clear-Phase88Seam from its OUTER finally block — never on the happy path only. Clear is
        deliberately idempotent (removing an absent variable is a no-op rollout) precisely so it can
        be called unconditionally, including when the seam was never armed or the run was
        interrupted. A seam left armed poisons every later scenario in this phase AND every later
        resilience sweep, and the failure presents as an inexplicable conservation violation with no
        pointer back to its cause. Assert-StackRestored then re-reads the live env and FAILS on any
        residue: assert, never assume.

        Each arm and each disarm triggers a rollout, so the sequence a scenario must follow is
        arm -> settle -> re-baseline -> trigger -> capture -> restore -> disarm -> assert. The
        re-baseline is not optional (DISC-06): without it the restart and the fault signal are
        indistinguishable and the assertion is void.

    WHAT THIS LIBRARY DELIBERATELY DOES NOT SUPPORT  (T-88-15)
        The reinject-DELAY seam at src/Keeper/Recovery/ReinjectConsumer.cs:55,
        KEEPER_REINJECT_DELAY_MS, is NOT in the seam allow-list and this library will never issue
        it. It is uncommitted, working-tree-only, so the deployed keeper:tags-const-1544 image may
        not contain it at all — a scenario built on it could arm a variable the running bits ignore
        and then score the resulting no-op as a finding. No Phase-88 scenario needs it: panel 8's
        gap requires the keeper to consume and not send, which is exactly what the committed
        suppress-send seam does on its own. Assert-StackRestored still SCANS for it, because
        "we never set it" is not the same claim as "it is not there".

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

# (tier -> the seam vars that tier's image actually honours). A (tier, name) PAIR must be in this
# table: keeper/PROCESSOR_DEFEAT_READ is as invalid as orchestrator/anything, because arming a seam
# on an image that does not read it produces a no-op rollout that a scenario would then score.
$script:Phase88Seams = @{
    'keeper'           = @('KEEPER_DEFEAT_REINJECT')
    'processor-sample' = @('PROCESSOR_DEFEAT_READ')
}

# Removal arguments as STATIC LITERALS rather than "$Name + '-'" built at call time. The trailing
# hyphen is the whole disarm mechanism, so it is spelled out once, here, where it is reviewable —
# and, like every other workload/variable token in this file, it is SELECTED from a table rather
# than assembled from a parameter (T-88-01).
$script:Phase88SeamRemoveArg = @{
    'KEEPER_DEFEAT_REINJECT' = 'KEEPER_DEFEAT_REINJECT-'
    'PROCESSOR_DEFEAT_READ'  = 'PROCESSOR_DEFEAT_READ-'
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
# Ordinal resolution of a (tier, seam-name) PAIR to the table's own seam-name string. Returns $null
# unless BOTH the tier is an allow-listed tier AND the name is one this tier's image actually reads.
# -------------------------------------------------------------------------------------------------
function Resolve-Phase88Seam {
    [CmdletBinding()]
    param([string]$Tier, [string]$Name)

    $t = Resolve-Phase88Tier $Tier
    if ($null -eq $t) { return $null }
    if ([string]::IsNullOrWhiteSpace($Name)) { return $null }
    if (-not $script:Phase88Seams.ContainsKey($t)) { return $null }

    foreach ($known in @($script:Phase88Seams[$t])) {
        if ([string]::Equals($known, $Name, [System.StringComparison]::Ordinal)) { return $known }
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

# -------------------------------------------------------------------------------------------------
# ARM a fault seam on the live Deployment env. Runtime only — no manifest is edited (see the header).
#
# The (Tier, Name) PAIR is validated against the static seam table before anything is issued, so a
# mismatched pair (keeper/PROCESSOR_DEFEAT_READ, orchestrator/anything) costs nothing and touches
# nothing. -Value is validated too: it is the one caller-supplied string that reaches a command line,
# and PROCESSOR_DEFEAT_READ legitimately carries a step label rather than a flag.
#
# PodNamesBefore/PodNamesAfter are returned even on partial failure. Arming rewrites the Deployment
# env and therefore ROLLS THE PODS: that discontinuity is a DISC-06 record the caller owes its
# artifact, and a caller that could not obtain it because the arm half-failed would be unable to say
# what it had already caused.
# -------------------------------------------------------------------------------------------------
function Set-Phase88Seam {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tier,
        [Parameter(Mandatory)][string]$Name,
        [string]$Value = '1'
    )

    $result = @{
        Ok             = $false
        FailureCode    = 0
        Tier           = $Tier
        Name           = $Name
        Value          = $Value
        Armed          = $false
        PodNamesBefore = @()
        PodNamesAfter  = @()
        ArmedUtc       = $null
        Detail         = ''
    }

    $t = Resolve-Phase88Tier $Tier
    $seam = Resolve-Phase88Seam -Tier $Tier -Name $Name
    if ($null -eq $t -or $null -eq $seam) {
        $result.FailureCode = 61
        $result.Detail = "('$Tier', '$Name') is not an allow-listed Phase-88 seam pair — no command issued."
        return $result
    }
    if ("$Value" -notmatch '^[A-Za-z0-9_.:-]{1,64}$') {
        $result.FailureCode = 61
        $result.Detail = "seam value '$Value' is not a permitted token — no command issued."
        return $result
    }

    $result.PodNamesBefore = (Get-Phase88PodResult -Tier $t).Names

    $arm = Invoke-Phase88Ctl -Arguments @('set', 'env', "deployment/$t", "$seam=$Value")
    if (-not $arm.Ok) {
        $result.FailureCode = 61
        $result.Detail = "arming '$seam' on '$t' failed (exit $($arm.ExitCode)): $($arm.Error)"
        $result.PodNamesAfter = (Get-Phase88PodResult -Tier $t).Names
        return $result
    }
    $result.Armed = $true

    if (-not (Wait-TierSettled -Tier $t -TimeoutSeconds 180)) {
        $result.FailureCode = 61
        $result.Detail = "'$t' did not settle within 180s after arming '$seam' — the seam IS armed; the caller's finally must still disarm it."
        $result.PodNamesAfter = (Get-Phase88PodResult -Tier $t).Names
        return $result
    }

    $result.PodNamesAfter = (Get-Phase88PodResult -Tier $t).Names
    $result.ArmedUtc = ([DateTimeOffset]::UtcNow).ToString('o')
    $result.Ok = $true
    return $result
}

# -------------------------------------------------------------------------------------------------
# DISARM a fault seam. The trailing hyphen in the removal argument is the mechanism; the argument is
# a static table literal, never assembled here.
#
# SAFE TO CALL WHEN THE SEAM WAS NEVER ARMED — removing an absent variable is a no-op rollout — so a
# caller can and MUST invoke this unconditionally from its outer finally, without tracking whether
# the arm succeeded. That property is the whole point: an interrupted run still disarms.
# -------------------------------------------------------------------------------------------------
function Clear-Phase88Seam {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tier,
        [Parameter(Mandatory)][string]$Name
    )

    $result = @{
        Ok          = $false
        FailureCode = 0
        Tier        = $Tier
        Name        = $Name
        ClearedUtc  = $null
        Detail      = ''
    }

    $t = Resolve-Phase88Tier $Tier
    $seam = Resolve-Phase88Seam -Tier $Tier -Name $Name
    if ($null -eq $t -or $null -eq $seam -or -not $script:Phase88SeamRemoveArg.ContainsKey($seam)) {
        $result.FailureCode = 61
        $result.Detail = "('$Tier', '$Name') is not an allow-listed Phase-88 seam pair — no command issued."
        return $result
    }

    $removeArg = $script:Phase88SeamRemoveArg[$seam]      # static literal: '<NAME>-'
    $clear = Invoke-Phase88Ctl -Arguments @('set', 'env', "deployment/$t", $removeArg)
    if (-not $clear.Ok) {
        $result.FailureCode = 61
        $result.Detail = "disarming '$seam' on '$t' failed (exit $($clear.ExitCode)): $($clear.Error)"
        return $result
    }

    if (-not (Wait-TierSettled -Tier $t -TimeoutSeconds 180)) {
        $result.FailureCode = 61
        $result.Detail = "'$t' did not settle within 180s after disarming '$seam' — verify with Assert-StackRestored before trusting any later capture."
        return $result
    }

    $result.ClearedUtc = ([DateTimeOffset]::UtcNow).ToString('o')
    $result.Ok = $true
    return $result
}

# -------------------------------------------------------------------------------------------------
# The restore CLAIM SET. Three explicit booleans intended to become claim fields in every scenario
# artifact — the alternative is a scenario that assumes it left the stack clean, which is how a
# left-armed seam survives to poison the next run.
#
# -ExpectedReplicas / -ExpectedImages are the values the caller READ from the live deployments
# BEFORE its scenario (Get-LiveReplicas / Get-LiveImage). They are deliberately not defaults and not
# a table in this file: a hardcoded expectation is the same defect as a hardcoded restore.
#
# Fail-closed throughout: a tier that cannot be READ is a failed claim, not an absent one. A missing
# expectation is a failed claim too — an unstated expectation cannot be satisfied.
# -------------------------------------------------------------------------------------------------
function Assert-StackRestored {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Tiers,
        [Parameter(Mandatory)][System.Collections.IDictionary]$ExpectedReplicas,
        [Parameter(Mandatory)][System.Collections.IDictionary]$ExpectedImages
    )

    $result = @{
        Ok                = $false
        FailureCode       = 0
        SeamVarsClean     = $true
        ReplicasRestored  = $true
        ImagesUnchanged   = $true
        SeamVarsFound     = @()
        ReplicaMismatches = @()
        ImageMismatches   = @()
        UnknownTiers      = @()
        CheckedUtc        = ([DateTimeOffset]::UtcNow).ToString('o')
    }

    foreach ($requested in @($Tiers)) {
        $t = Resolve-Phase88Tier $requested
        if ($null -eq $t) {
            $result.UnknownTiers += "$requested"
            $result.SeamVarsClean = $false
            $result.ReplicasRestored = $false
            $result.ImagesUnchanged = $false
            continue
        }

        # --- seam residue: read the live Deployment env and match the residue pattern -------------
        $envRead = Invoke-Phase88Ctl -Arguments @('get', 'deploy', $t, '-o', 'jsonpath={.spec.template.spec.containers[0].env}')
        if (-not $envRead.Ok) {
            $result.SeamVarsClean = $false
            $result.SeamVarsFound += "${t}:env-read-failed"
        } elseif (("$($envRead.Output)") -match 'DEFEAT|REINJECT_DELAY') {
            $result.SeamVarsClean = $false
            $result.SeamVarsFound += $t
        }

        # --- replica restore ----------------------------------------------------------------------
        $liveReplicas = Get-LiveReplicas -Tier $t
        if (-not $ExpectedReplicas.Contains($t)) {
            $result.ReplicasRestored = $false
            $result.ReplicaMismatches += "${t}: no expected replica count supplied (live=$liveReplicas)"
        } else {
            $wantReplicas = [int]$ExpectedReplicas[$t]
            if ($liveReplicas -lt 0) {
                $result.ReplicasRestored = $false
                $result.ReplicaMismatches += "${t}: replica read failed (expected $wantReplicas)"
            } elseif ($liveReplicas -ne $wantReplicas) {
                $result.ReplicasRestored = $false
                $result.ReplicaMismatches += "${t}: live=$liveReplicas expected=$wantReplicas"
            }
        }

        # --- image unchanged ----------------------------------------------------------------------
        $liveImage = Get-LiveImage -Tier $t
        if (-not $ExpectedImages.Contains($t)) {
            $result.ImagesUnchanged = $false
            $result.ImageMismatches += "${t}: no expected image supplied (live='$liveImage')"
        } else {
            $wantImage = "$($ExpectedImages[$t])"
            if ([string]::IsNullOrWhiteSpace($liveImage)) {
                $result.ImagesUnchanged = $false
                $result.ImageMismatches += "${t}: image read failed (expected '$wantImage')"
            } elseif (-not [string]::Equals($liveImage, $wantImage, [System.StringComparison]::Ordinal)) {
                $result.ImagesUnchanged = $false
                $result.ImageMismatches += "${t}: live='$liveImage' expected='$wantImage'"
            }
        }
    }

    $result.Ok = ($result.SeamVarsClean -and $result.ReplicasRestored -and $result.ImagesUnchanged)
    if (-not $result.Ok) { $result.FailureCode = 65 }
    return $result
}
