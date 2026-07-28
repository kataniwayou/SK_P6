<#
.SYNOPSIS
    Phase 88 Wave-0 probe (BLOCKING) — answers, against the live `skp` cluster, the SEVEN questions
    whose answers change the structure of every downstream Phase-88 scenario plan.

.DESCRIPTION
    WHY THIS SCRIPT EXISTS
        Phase 87's transferable lesson was that a proof which does not reproduce the caller's shape
        proves only that the PromQL parses. The equivalent failure here would be locking eight
        scenarios around assumptions A1-A4 and discovering mid-sweep that the keeper never receives
        recovery traffic in this cluster, or that `?viewPanel=panel-<id>` redirects with "Panel not
        found". Measuring first is cheaper than replanning eight scenarios. So this script MEASURES,
        and `88-PROBE-DECISIONS.md` locks each downstream lever to the field that decided it.

    THE SEVEN QUESTIONS AND THE FIELD EACH ONE SETS

        PQ-01  Does a plain `processor-sample` scale-0 during traffic produce
               `keeper_messages_consumed_total` at all in THIS cluster?
               -> KeeperRecoveryTrafficObserved, KeeperConsumedSeriesCount, Panel2LegendNamesAfter
               Decides: panel 2's lever is `scale` (no seam) or `seam` (PROCESSOR_DEFEAT_READ).

        PQ-02  Does `?viewPanel=panel-<id>` work on this Grafana 12.3.9 instance, and does
               `getByTestId('data-testid panel content').innerText()` yield a parseable number for
               BOTH a stat panel (id 8) and a table-legend timeseries panel (id 9)?
               -> ViewPanelUrlWorks, StatPanelParsed, TimeseriesLegendParsed, LocatorModeChosen,
                  Panel8RawText, Panel9RawText
               Decides: the reader's primary DOM locator for the whole phase.

        PQ-03  Is there any WebApi endpoint that returns 5xx on SAFE input?
               -> Safe5xxFound, Safe5xxRoute, ProbedEndpoints[] each {method, path, status}
               Decides: panel 13 is proven by HTTP, or joins the accepted-unproven register.

        PQ-04  Is `$__rate_interval` pinned across a narrow and a wide window at a fixed viewport?
               -> DatasourceTimeIntervalSeconds, RateIntervalNarrowSeconds, RateIntervalWideSeconds,
                  RateIntervalPinned
               Decides: whether ONE RateIntervalSeconds value can be stated for every artifact.

        PQ-05  Which route to an `orchestrator_step_unresolved` increment is reachable without a
               `src/` change?
               -> UnresolvedRouteApiDanglingEdgeStatus, UnresolvedRouteL2StepKeyViable,
                  UnresolvedRouteChosen, ProbeWorkflowId, ProbeWorkflowCleanedUp
               Decides: panel 6 is driven by data, or joins the accepted-unproven register.

        PQ-06  Does `kubectl set env` land `KEEPER_DEFEAT_REINJECT` on a live keeper pod, and does
               the disarm plus the restore assertion leave the stack clean?
               -> SeamArmLanded, SeamDisarmClean, SeamRolloutOldPods[], SeamRolloutNewPods[]
               Decides: whether panel 8's scenario (the only seam scenario) can proceed at all.

        PQ-07  What is the OLDEST Prometheus sample available at run start?
               -> OldestSampleUtc, RetentionHoursObserved
               Decides: nothing. It states the artifact's own evidentiary horizon. Retention is short
               (~12 h measured in Phase 87), so evidence must be captured DURING the run — it cannot
               be re-queried the next day.

    VERDICT SEMANTICS — DELIBERATELY NOT A VERDICT ABOUT THE ANSWERS
        A `false` answer is a MEASUREMENT, not a defect. "Panel 13 cannot be driven" is exactly the
        kind of fact this probe exists to produce, and a script that failed on it would be punishing
        itself for working.
            Pass          all seven questions were ANSWERED and the stack was left clean
            Inconclusive  a question could not be evaluated at all (named in UnevaluableQuestions[])
            Fail          the stack was NOT left clean (Assert-StackRestored returned any false claim)
        Fail beats Inconclusive (the Phase-87 verdict-precedence fix): a claim that was evaluated and
        came back false is positive evidence of a defect, and no quantity of OTHER unevaluated claims
        makes it less true.

    Flow (lettered; every step names the question it answers):

        PRE      precondition gate      docker-desktop context + shared stack forwards + all four
                                        tiers fully Ready + ZERO seam vars at rest (a concurrent
                                        sweep or a seam left armed by an earlier run would confound
                                        every measurement in this file)
        STEP A   grafana forward        this script's OWN loopback tunnel, PID in memory only
        STEP B   PQ-07                  oldest available Prometheus sample = the evidentiary horizon
        STEP C   PQ-04                  datasource timeInterval -> $__rate_interval, narrow vs wide
        STEP D   PQ-02                  batch DOM read of panel 8 (stat) + panel 9 (table legend)
        STEP E   PQ-03                  static safe-input endpoint enumeration for a 5xx
        STEP F   PQ-05                  unresolved-step route enumeration on a SEPARATE workflow
        STEP G   PQ-01                  drive traffic -> processor-sample scale fault -> keeper check
        STEP H   PQ-06                  seam arm / assert / disarm smoke on the keeper
        STEP J   artifact + verdict     write the JSON, THEN resolve the exit code from it
        STEP Z   teardown               disarm unconditionally, delete the probe workflow, stop ONLY
                                        this script's own forward PID (outer finally)

    EXIT-CODE TABLE (the 0/1/2 classes are owned by lib/exit-code-resolution.ps1; every infra abort
    has its OWN distinct code so an abort can never be read as a verdict — the T-87-18 control):
        0   verdict PASS          (Resolve-AnalyzerExitCode Pass)
        1   verdict FAIL          (Resolve-AnalyzerExitCode Fail / unknown fail-closed)
        2   verdict INCONCLUSIVE  (Resolve-AnalyzerExitCode Inconclusive)
        15  the grafana port-forward never carried traffic
        20  grafana /api/health never reported database ok
        50  the traffic drive failed (seed / wf-id lookup / activation != 204)
        60  the scale fault failed as INFRASTRUCTURE
        61  the seam arm or disarm failed
        62  could not read live replicas or images
        63  the panel reader is unavailable (node / run.js / the reader script missing)
        64  usage or precondition error (wrong kube context, shared forwards down, stack not at rest)
        65  the restore assertion failed (the stack was NOT left clean)

.NOTES
    Dev/ops-only tooling. No product source is touched and no manifest is edited. This script takes
    NO parameters at all: every workload name, endpoint path and query it uses is a static in-script
    literal, so nothing a caller supplies can reach a command line (T-88-13 / the T-81-01 precedent).

    PORT-FORWARD OWNERSHIP (T-88-05) — this script starts its OWN grafana forward on loopback port
    3000 and keeps the PID in an IN-MEMORY VARIABLE ONLY. It never creates, reads or writes the
    shared forward PID registry that scripts/phase-80-up.ps1 owns: that file holds the eight shared
    harness tunnels, so reading it for teardown would kill all eight and break a concurrent sweep.
    Teardown stops exactly one PID, behind a recycled-PID guard, and ONLY when this script was the
    process that started it. The forward binds --address 127.0.0.1 explicitly so the bridged host
    port stays loopback-only (T-88-04).

    NO DEPENDENCY OUTAGE IS DRIVEN. Taking redis or another dependency down is a documented route to
    a WebApi 5xx, and it is deliberately NOT used: the Phase-86 hard readiness latch means a
    dependency outage costs a WebApi pod restart to recover and would perturb every later baseline
    in the phase. This probe does not budget for that (T-88-16).

    WHAT STEP H DOES AND DOES NOT PROVE. STEP H proves only that the seam arm/disarm MECHANISM lands
    on a live pod and clears again, and that the stack reads back clean afterwards. It does NOT
    prove that the deployed keeper:tags-const-1544 image HONOURS `KEEPER_DEFEAT_REINJECT` — only a
    real recovery event can show that, and plan 88-07 owns it. Do not read a green STEP H as
    evidence that panel 8's scenario will work.

    admin/admin is the deliberate dev posture set in k8s/23-grafana.yaml and is reachable only over
    loopback; auth hardening is out of scope for this milestone, so the credential is inline rather
    than parameterised — parameterising it would imply a security property this deployment does not
    have.

    JSON is parsed with ConvertFrom-Json throughout; no external JSON tooling is installed on this
    host. The artifact is written with -Depth 10, not the repo's usual shallower depth: this file's
    nested per-endpoint and per-reading arrays would otherwise serialise as type names instead of
    data.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Declared BEFORE the try so the outer finally can read them under StrictMode even when the
# precondition gate exits before the step that would have set them ever runs.
$gfForwardPid    = 0
$gfForwardOwned  = $false
$seamArmed       = $false
$probeWorkflowId = ''

Push-Location $repoRoot
try {

    # -----------------------------------------------------------------------------------------
    # prefixed console-trace helper (Cyan step banner / Gray sub-detail / Yellow warning /
    # Red fatal-before-exit / Green success — the repo-wide colour convention).
    # -----------------------------------------------------------------------------------------
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-88-wave0-probe] $msg" -ForegroundColor $color
    }

    # -----------------------------------------------------------------------------------------
    # Dot-source the three shared libraries. exit-code-resolution.ps1 owns the verdict -> exit
    # mapping: Resolve-AnalyzerExitCode is NEVER reimplemented as an inline verdict switch here,
    # because its fail-closed default (unknown/absent verdict -> 1) is the T-87-18 control.
    # phase-88-panel-read.ps1 owns the browser read; phase-88-cluster-ops.ps1 owns every cluster
    # mutation and the restore claim set.
    # -----------------------------------------------------------------------------------------
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')
    . (Join-Path $PSScriptRoot 'lib/phase-88-panel-read.ps1')
    . (Join-Path $PSScriptRoot 'lib/phase-88-cluster-ops.ps1')

    $gf       = 'http://127.0.0.1:3000'
    $proxy    = "$gf/api/datasources/proxy/uid/skp-prometheus"
    $adminB64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('admin:admin'))
    $auth     = @{ Authorization = "Basic $adminB64" }
    $api      = 'http://localhost:8080'

    # Pinned viewport for every browser read in this run. maxDataPoints derives from the panel's
    # PIXEL WIDTH, which sets the query step, which sets $__rate_interval — so an unpinned viewport
    # would make two captures incomparable even with identical time windows.
    $ViewportWidth  = 1920
    $ViewportHeight = 1080

    # STATIC tier list. This script has no parameters, so no workload name can be caller-derived.
    $Tiers = @('baseapi-service', 'keeper', 'orchestrator', 'processor-sample')

    # The two PQ-02 subjects, by id and by verbatim title. The titles are needed only if PQ-02
    # selects the header-walk fallback, which locates a panel by its rendered header text.
    $Panel8Id    = '8'
    $Panel9Id    = '9'
    $Panel8Title = 'Keeper consumed - sent gap in range'
    $Panel9Title = 'Keeper L2 probe heartbeat'
    $Panel2Id    = '2'

    $screenshotDir = Join-Path $repoRoot 'analyzer-reports/phase-88-screenshots'
    $reportDir     = Join-Path $repoRoot 'analyzer-reports'
    $reportPath    = Join-Path $reportDir 'phase-88-wave0-probe.json'

    # -----------------------------------------------------------------------------------------
    # ANSWER FIELDS — every one initialised up front so the artifact writer can read any of them
    # on any path under StrictMode, and so an unanswered question is $null rather than absent.
    # -----------------------------------------------------------------------------------------
    $UnevaluableQuestions = @()

    # PQ-01
    $KeeperRecoveryTrafficObserved = $null
    $KeeperConsumedSeriesCount     = $null
    $Panel2LegendNamesAfter        = @()
    # PQ-02
    $ViewPanelUrlWorks             = $null
    $StatPanelParsed               = $null
    $TimeseriesLegendParsed        = $null
    $TimeseriesLegendNamesBound    = $null
    $Panel9LegendNames             = @()
    $LocatorModeChosen             = $null
    $Panel8RawText                 = $null
    $Panel9RawText                 = $null
    $ScreenshotPaths               = @()
    # PQ-03
    $Safe5xxFound                  = $null
    $Safe5xxRoute                  = $null
    $ProbedEndpoints               = @()
    # PQ-04
    $DatasourceTimeIntervalSeconds = $null
    $RateIntervalNarrowSeconds     = $null
    $RateIntervalWideSeconds       = $null
    $RateIntervalPinned            = $null
    # PQ-05
    $UnresolvedRouteApiDanglingEdgeStatus = $null
    $UnresolvedRouteApiDanglingEdgeStage  = $null
    $UnresolvedRouteL2StepKeyViable       = $null
    $UnresolvedRouteChosen                = $null
    $ProbeWorkflowCleanedUp               = $null
    # PQ-06
    $SeamArmLanded                 = $null
    $SeamDisarmClean               = $null
    $SeamRolloutOldPods            = @()
    $SeamRolloutNewPods            = @()
    # PQ-07
    $OldestSampleUtc               = $null
    $RetentionHoursObserved        = $null

    # =========================================================================================
    # helpers
    # =========================================================================================

    # Start this script's OWN grafana forward and hand back the PID. Bound to loopback explicitly.
    function Start-GrafanaForward {
        $p = Start-Process kubectl -PassThru -WindowStyle Hidden `
               -ArgumentList @('port-forward', 'svc/grafana', '3000:3000', '-n', 'skp', '--address', '127.0.0.1')
        return [int]$p.Id
    }

    # Recycled-PID guard: only kill if the PID is STILL a live kubectl process. A forward may have
    # exited and had its PID recycled by the OS — force-killing it blindly could terminate an
    # unrelated process. Best-effort; never throws.
    function Stop-GrafanaForward([int]$ForwardPid) {
        if ($ForwardPid -le 0) { return }
        try {
            $proc = Get-Process -Id $ForwardPid -ErrorAction SilentlyContinue |
                    Where-Object { $_.ProcessName -eq 'kubectl' }
            if ($proc) { Stop-Process -Id $ForwardPid -Force -ErrorAction SilentlyContinue }
        } catch { }
    }

    # A port-forward reports success on start even before the tunnel carries traffic, so gate on a
    # REAL parsed `database == ok`. 0 healthy / 15 the tunnel never carried traffic at all / 20 it
    # carried traffic but Grafana never reported its database ok.
    function Wait-GrafanaHealthy([int]$TimeoutSeconds = 90) {
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        $sawHttp  = $false
        while ((Get-Date) -lt $deadline) {
            try {
                $h = Invoke-RestMethod -Uri "$gf/api/health" -TimeoutSec 5 -ErrorAction Stop
                $sawHttp = $true
                if ("$($h.database)" -eq 'ok') { return 0 }
            } catch { }
            Start-Sleep -Seconds 3
        }
        if (-not $sawHttp) { return 15 }
        return 20
    }

    # Is the tunnel ALREADY carrying traffic? A forward this script did not start may already hold
    # loopback 3000 (grafana is not one of the eight shared tunnels, but an earlier session can have
    # left one). Starting a second one would bind-fail and exit immediately, and tearing "it" down
    # would then either be a no-op or, worse, kill a forward this script does not own.
    function Test-GrafanaAlreadyUp {
        try {
            $h = Invoke-RestMethod -Uri "$gf/api/health" -TimeoutSec 4 -ErrorAction Stop
            return ("$($h.database)" -eq 'ok')
        } catch { return $false }
    }

    # RANGE query through GRAFANA'S OWN datasource proxy — the diagnosis channel the locked
    # constraint permits. It is never the verdict for a panel; here it answers PQ-07 (what data
    # exists at all) and cross-checks PQ-01 beside the rendered panel-2 read.
    function Invoke-ProxyRangeQuery([string]$Query, [long]$StartUnix, [long]$EndUnix, [int]$StepSeconds) {
        return Invoke-RestMethod -Method Post -Uri "$proxy/api/v1/query_range" `
                 -Headers $auth -ContentType 'application/x-www-form-urlencoded' `
                 -Body @{ query = $Query; start = $StartUnix; end = $EndUnix; step = $StepSeconds } `
                 -TimeoutSec 90 -ErrorAction Stop
    }

    # Reproduce Grafana's own arithmetic rather than assuming a number.
    #   $__interval      floors at the datasource's timeInterval (its "minimum interval")
    #   $__rate_interval = max(4 x timeInterval, $__interval + timeInterval)
    # Nothing here is hardcoded: the timeInterval is READ from the live datasource, so this tracks
    # the manifest instead of drifting from it.
    function Get-RateIntervalSeconds([int]$TimeIntervalSeconds, [int]$StepSeconds) {
        return [Math]::Max(4 * $TimeIntervalSeconds, $StepSeconds + $TimeIntervalSeconds)
    }

    # Parse a Grafana duration string ('15s', '60s', '1m') to seconds. Returns 0 when absent so the
    # caller can fail loudly rather than silently assume a default.
    function ConvertFrom-GrafanaDuration([string]$Text) {
        if ([string]::IsNullOrWhiteSpace($Text)) { return 0 }
        if ($Text -match '^\s*(\d+)\s*([smh]?)\s*$') {
            $n = [int]$Matches[1]
            switch ($Matches[2]) { 'm' { return $n * 60 } 'h' { return $n * 3600 } default { return $n } }
        }
        return 0
    }

    # Grafana's own step choice: max(datasource timeInterval, range / maxDataPoints), where
    # maxDataPoints defaults to the panel's pixel width. In single-panel (kiosk) view the panel
    # spans the viewport, so the pinned viewport width IS the maxDataPoints estimate.
    function Get-QueryStepSeconds([int]$RangeSeconds, [int]$TimeIntervalSeconds, [int]$MaxDataPoints) {
        if ($MaxDataPoints -le 0) { return $TimeIntervalSeconds }
        $raw = [int][Math]::Ceiling($RangeSeconds / [double]$MaxDataPoints)
        return [Math]::Max($TimeIntervalSeconds, $raw)
    }

    # ---- batch-reading accessors (StrictMode-safe; the reader's optional fields are membership
    # ---- tested, never dotted into blindly) --------------------------------------------------

    function Get-FirstRawText($Readings, [string]$PanelId) {
        foreach ($r in @($Readings)) {
            $n = @(Get-PropertyNames $r)
            if ($n -notcontains 'panelId') { continue }
            if ("$($r.panelId)" -ne $PanelId) { continue }
            if ($n -contains 'rawText' -and $null -ne $r.rawText -and "$($r.rawText)".Length -gt 0) {
                return "$($r.rawText)"
            }
        }
        return ''
    }

    function Test-StatParsed($Readings, [string]$PanelId) {
        return (@(Get-PanelSamples -Readings @($Readings) -PanelId $PanelId).Count -gt 0)
    }

    function Test-LegendParsed($Readings, [string]$PanelId) {
        foreach ($r in @($Readings)) {
            $n = @(Get-PropertyNames $r)
            if ($n -notcontains 'panelId') { continue }
            if ("$($r.panelId)" -ne $PanelId) { continue }
            if ($n -notcontains 'series') { continue }
            foreach ($s in @($r.series)) {
                $sn = @(Get-PropertyNames $s)
                if ($sn -contains 'mean' -and $null -ne $s.mean) { return $true }
            }
        }
        return $false
    }

    # Series names VERBATIM — never trimmed. Panel 2's discrimination signal IS a trailing-space
    # legend name: while the counter does not exist the `or vector(0)` guard supplies a label-less
    # series that `legendFormat: "consumed {{source}}"` renders as the literal `consumed ` with a
    # trailing space, and the moment the counter exists the same row reads `consumed keeper`.
    function Get-LegendNames($Readings, [string]$PanelId) {
        $names = @()
        foreach ($r in @($Readings)) {
            $n = @(Get-PropertyNames $r)
            if ($n -notcontains 'panelId') { continue }
            if ("$($r.panelId)" -ne $PanelId) { continue }
            if ($n -contains 'series') {
                foreach ($s in @($r.series)) {
                    $sn = @(Get-PropertyNames $s)
                    if ($sn -contains 'name' -and $names -notcontains "$($s.name)") { $names += "$($s.name)" }
                }
            }
            if ($n -contains 'legendSeriesNames') {
                foreach ($nm in @($r.legendSeriesNames)) {
                    if ($names -notcontains "$nm") { $names += "$nm" }
                }
            }
        }
        return [string[]]$names
    }

    # Does every PARSED legend row carry a non-empty name? A numeric Mean is only half of what the
    # phase needs: panel 2's discrimination signal is a legend NAME change, so a reader that binds
    # values to rows but leaves the row names blank would read panel 2 as "unchanged" through the
    # exact transition the scenario exists to catch. This is reported separately from
    # TimeseriesLegendParsed precisely so the two cannot be confused for one another.
    function Test-LegendNamesBound($Readings, [string]$PanelId) {
        $sawRow = $false
        foreach ($r in @($Readings)) {
            $n = @(Get-PropertyNames $r)
            if ($n -notcontains 'panelId') { continue }
            if ("$($r.panelId)" -ne $PanelId) { continue }
            if ($n -notcontains 'series') { continue }
            foreach ($s in @($r.series)) {
                $sn = @(Get-PropertyNames $s)
                if ($sn -notcontains 'mean' -or $null -eq $s.mean) { continue }
                $sawRow = $true
                $nm = if ($sn -contains 'name') { "$($s.name)" } else { '' }
                if ([string]::IsNullOrWhiteSpace($nm)) { return $false }
            }
        }
        return $sawRow
    }

    # True when the panel could not be located or rendered at all: an Error state, or Grafana's own
    # "Panel not found" text — which is exactly what a bad single-panel URL renders after its
    # redirect back to the dashboard.
    function Test-PanelUnlocatable($Readings, [string]$PanelId) {
        $sawAny = $false
        foreach ($r in @($Readings)) {
            $n = @(Get-PropertyNames $r)
            if ($n -notcontains 'panelId') { continue }
            if ("$($r.panelId)" -ne $PanelId) { continue }
            $sawAny = $true
            $state = if ($n -contains 'panelState') { "$($r.panelState)" } else { 'Error' }
            $text  = if ($n -contains 'rawText') { "$($r.rawText)" } else { '' }
            if ($state -eq 'Error') { return $true }
            if ($text -match 'Panel not found') { return $true }
        }
        return (-not $sawAny)
    }

    function Get-ScreenshotPaths($Readings) {
        $paths = @()
        foreach ($r in @($Readings)) {
            $n = @(Get-PropertyNames $r)
            if ($n -notcontains 'screenshotPath') { continue }
            if ($null -eq $r.screenshotPath) { continue }
            $p = "$($r.screenshotPath)"
            if ([string]::IsNullOrWhiteSpace($p)) { continue }
            if ($paths -notcontains $p) { $paths += $p }
        }
        return [string[]]$paths
    }

    # =========================================================================================
    # PRE — PRECONDITION GATE (code 64). Usage/state errors fail with a remediation line, never a
    # verdict. The stack-at-rest half is specific to this phase: a concurrent sweep or a seam left
    # armed by an earlier run would confound EVERY measurement below, and the confusion would
    # surface later as an inexplicable result rather than as an abort here.
    # =========================================================================================
    Write-Phase "PRE: precondition gate (kube context + shared forwards + stack at rest)"

    $ctxRaw  = ''
    $ctxExit = 1
    try {
        $ctxRaw  = kubectl config current-context 2>$null
        # Pin the exit code BEFORE the trim: on a failed call stdout is empty, so a .Trim() on the
        # raw capture (string-cast, never $null) must not be allowed to bypass the failure branch.
        $ctxExit = $LASTEXITCODE
    } catch { $ctxExit = 1 }
    $ctx = ("$ctxRaw").Trim()
    if ($ctxExit -ne 0 -or $ctx -ne 'docker-desktop') {
        Write-Phase "kube context is '$ctx', not 'docker-desktop'. This probe targets the local Docker-Desktop cluster ONLY." 'Red'
        Write-Phase "REMEDIATION: kubectl config use-context docker-desktop" 'Yellow'
        exit 64
    }
    Write-Phase "  kube context = docker-desktop (OK)." 'Gray'

    $sharedUp = $false
    try {
        $probe = Invoke-WebRequest -Uri "$api/health/ready" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        if ($probe.StatusCode -eq 200) { $sharedUp = $true }
    } catch { }
    if (-not $sharedUp) {
        Write-Phase "the shared stack port-forwards are not carrying traffic (no 200 from $api/health/ready)." 'Red'
        Write-Phase "REMEDIATION: pwsh -File scripts/phase-80-up.ps1   (it owns the eight shared forwards and reaps its own stale PIDs)" 'Yellow'
        exit 64
    }
    Write-Phase "  shared stack forwards live (baseapi /health/ready == 200)." 'Gray'

    # All four app tiers must be fully Ready AND carry zero seam residue BEFORE anything is measured.
    # Both reads go through the cluster-ops invocation site, whose single command line bakes in the
    # namespace and whose local preference shadow keeps a non-zero kubectl exit from exploding inside
    # this Stop-preference harness.
    $preReplicas = @{}
    $preImages   = @{}
    $atRest      = $true
    foreach ($tier in $Tiers) {
        $want = Get-LiveReplicas -Tier $tier
        $img  = Get-LiveImage -Tier $tier
        if ($want -lt 1 -or [string]::IsNullOrWhiteSpace($img)) {
            Write-Phase "  could not read a usable replica count / image for '$tier' (replicas=$want image='$img')." 'Red'
            $atRest = $false
            continue
        }
        $preReplicas[$tier] = $want
        $preImages[$tier]   = $img

        $readyRaw = Invoke-Phase88Ctl -Arguments @('get', 'deploy', $tier, '-o', 'jsonpath={.status.readyReplicas}')
        $readyTxt = ("$($readyRaw.Output)").Trim()
        $ready    = 0
        if (-not $readyRaw.Ok -or -not [int]::TryParse($readyTxt, [ref]$ready)) { $ready = -1 }
        if ($ready -ne $want) {
            Write-Phase "  '$tier' is $ready/$want Ready — the stack is not at rest." 'Red'
            $atRest = $false
        }

        $envRead = Invoke-Phase88Ctl -Arguments @('get', 'deploy', $tier, '-o', 'jsonpath={.spec.template.spec.containers[0].env}')
        if (-not $envRead.Ok) {
            Write-Phase "  could not read the live env of '$tier'." 'Red'
            $atRest = $false
        } elseif (("$($envRead.Output)") -match 'DEFEAT|REINJECT_DELAY') {
            Write-Phase "  '$tier' carries a fault-seam env var AT REST — an earlier run left it armed." 'Red'
            $atRest = $false
        }
        Write-Phase "    $tier ${ready}/${want} Ready, image $img" 'Gray'
    }
    if (-not $atRest) {
        Write-Phase "the stack is not at rest (a tier is not fully Ready, unreadable, or carries a seam var)." 'Red'
        Write-Phase "REMEDIATION: wait for any concurrent sweep to finish, then clear residue with" 'Yellow'
        Write-Phase "             kubectl -n skp set env deployment/keeper KEEPER_DEFEAT_REINJECT-" 'Yellow'
        Write-Phase "             kubectl -n skp set env deployment/processor-sample PROCESSOR_DEFEAT_READ-" 'Yellow'
        exit 64
    }
    Write-Phase "  all four tiers fully Ready, zero seam vars at rest." 'Gray'

    # =========================================================================================
    # STEP A — THIS SCRIPT'S OWN LOOPBACK FORWARD (codes 15 then 20).
    # The PID lives in ONE in-memory variable. The shared forward PID registry is neither read nor
    # written: it owns the eight long-lived stack tunnels, and touching it would break a concurrent
    # sweep (T-88-05).
    # =========================================================================================
    Write-Phase "STEP A: grafana loopback forward (svc/grafana 3000:3000 --address 127.0.0.1)"
    if (Test-GrafanaAlreadyUp) {
        Write-Phase "  loopback 3000 is ALREADY carrying grafana traffic — reusing it and starting none." 'Yellow'
        Write-Phase "  this script therefore owns no forward and will tear none down." 'Gray'
        $gfForwardOwned = $false
    } else {
        $gfForwardPid   = Start-GrafanaForward
        $gfForwardOwned = $true
        Write-Phase "  forward PID $gfForwardPid — polling $gf/api/health for database == ok (bounded 90s)..." 'Gray'
        $healthCode = Wait-GrafanaHealthy 90
        if ($healthCode -eq 15) {
            Write-Phase "the grafana port-forward never carried traffic within 90s. Aborting." 'Red'; exit 15
        }
        if ($healthCode -ne 0) {
            Write-Phase "grafana /api/health never reported database ok within 90s. Aborting." 'Red'; exit 20
        }
    }
    Write-Phase "  grafana health database == ok." 'Gray'

    # =========================================================================================
    # STEP B — PQ-07: THE RUN'S EVIDENTIARY HORIZON.
    # Prometheus retention on this stack was measured at roughly twelve hours in Phase 87. That is
    # not a footnote: it means every measurement in this phase must be captured INTO an artifact
    # during the run, because re-querying tomorrow will find nothing. The artifact therefore states
    # its own horizon rather than leaving a future reader to guess it.
    # =========================================================================================
    Write-Phase "STEP B: PQ-07 — oldest available Prometheus sample (the run's evidentiary horizon)"
    # The horizon is deliberately far wider than the ~12 h Phase 87 measured. A horizon that is
    # NARROWER than the real retention silently reports the horizon back as the retention, which is
    # a measurement of this script rather than of Prometheus — so the probe also records whether the
    # oldest sample it found sits at the edge of what it looked at.
    $nowUnix       = ([DateTimeOffset]::UtcNow).ToUnixTimeSeconds()
    $horizonHours  = 336
    $horizonStart  = $nowUnix - ($horizonHours * 3600)
    $RetentionHorizonHours   = $horizonHours
    $RetentionHorizonLimited = $null
    try {
        $oldestQ = Invoke-ProxyRangeQuery -Query 'up' -StartUnix $horizonStart -EndUnix $nowUnix -StepSeconds 3600
        $oldestUnix = $null
        foreach ($s in @($oldestQ.data.result)) {
            $sn = @(Get-PropertyNames $s)
            if ($sn -notcontains 'values') { continue }
            $vals = @($s.values)
            if ($vals.Count -eq 0) { continue }
            $ts = [double]($vals[0][0])
            if ($null -eq $oldestUnix -or $ts -lt $oldestUnix) { $oldestUnix = $ts }
        }
        if ($null -eq $oldestUnix) {
            $UnevaluableQuestions += 'PQ-07 OldestSampleUtc (no "up" series in the probed horizon)'
            Write-Phase "  no 'up' series inside the ${horizonHours}h probe horizon — PQ-07 unevaluable." 'Yellow'
        } else {
            $OldestSampleUtc        = ([System.DateTimeOffset]::FromUnixTimeSeconds([long]$oldestUnix)).UtcDateTime.ToString('o')
            $RetentionHoursObserved = [Math]::Round((($nowUnix - $oldestUnix) / 3600.0), 2)
            # Within one step of the horizon start means the probe hit its OWN edge, so the number is
            # a lower bound on retention, not a measurement of it.
            $RetentionHorizonLimited = (($oldestUnix - $horizonStart) -le 3600)
            Write-Phase "  oldest sample $OldestSampleUtc — $RetentionHoursObserved h of history available (horizon ${horizonHours}h, horizon-limited=$RetentionHorizonLimited)." 'Gray'
        }
    } catch {
        $UnevaluableQuestions += "PQ-07 OldestSampleUtc (proxy query failed: $($_.Exception.Message))"
        Write-Phase "  the horizon query failed: $($_.Exception.Message)" 'Yellow'
    }

    # =========================================================================================
    # STEP C — PQ-04: IS $__rate_interval PINNED?
    # If it is, one RateIntervalSeconds value can be stated in every artifact this phase writes and
    # a narrow after-window is directly comparable with a wider baseline. If it is not, each
    # artifact must carry its own measured value and the equal-window-width rule becomes explicitly
    # load-bearing rather than merely tidy. 240 is NOT hardcoded here — it is derived, and whatever
    # is derived is what gets recorded.
    # =========================================================================================
    Write-Phase "STEP C: PQ-04 — derive `$__rate_interval for a narrow and a wide window at ${ViewportWidth}x${ViewportHeight}"
    try {
        $ds = Invoke-RestMethod -Uri "$gf/api/datasources/uid/skp-prometheus" -Headers $auth -TimeoutSec 20 -ErrorAction Stop
        $tiText = ''
        $dsNames = @(Get-PropertyNames $ds)
        if ($dsNames -contains 'jsonData') {
            $jdNames = @(Get-PropertyNames $ds.jsonData)
            if ($jdNames -contains 'timeInterval') { $tiText = "$($ds.jsonData.timeInterval)" }
        }
        $ti = ConvertFrom-GrafanaDuration $tiText
        if ($ti -le 0) {
            $UnevaluableQuestions += "PQ-04 RateIntervalPinned (datasource timeInterval absent or unparseable: '$tiText')"
            Write-Phase "  datasource timeInterval is '$tiText' — PQ-04 unevaluable." 'Yellow'
        } else {
            $DatasourceTimeIntervalSeconds = $ti
            $narrowRange = 10 * 60          # 10 minutes — the narrowest window this phase will use
            $wideRange   = 2 * 60 * 60      # 2 hours    — the widest
            $narrowStep  = Get-QueryStepSeconds -RangeSeconds $narrowRange -TimeIntervalSeconds $ti -MaxDataPoints $ViewportWidth
            $wideStep    = Get-QueryStepSeconds -RangeSeconds $wideRange   -TimeIntervalSeconds $ti -MaxDataPoints $ViewportWidth
            $RateIntervalNarrowSeconds = Get-RateIntervalSeconds $ti $narrowStep
            $RateIntervalWideSeconds   = Get-RateIntervalSeconds $ti $wideStep
            $RateIntervalPinned        = ($RateIntervalNarrowSeconds -eq $RateIntervalWideSeconds)
            Write-Phase "  timeInterval=${ti}s -> narrow step ${narrowStep}s rate_interval ${RateIntervalNarrowSeconds}s / wide step ${wideStep}s rate_interval ${RateIntervalWideSeconds}s (pinned=$RateIntervalPinned)" 'Gray'
        }
    } catch {
        $UnevaluableQuestions += "PQ-04 RateIntervalPinned (datasource read failed: $($_.Exception.Message))"
        Write-Phase "  the datasource read failed: $($_.Exception.Message)" 'Yellow'
    }

    # =========================================================================================
    # STEP D — PQ-02: THE READER'S PRIMARY DOM LOCATOR.
    # Two independent subjects, because they exercise two different parses: panel 8 is a `stat` (one
    # big number) and panel 9 is a `timeseries` with a TABLE legend (rows of name / Mean / Max). The
    # reader's stat-vs-legend classification is heuristic and, until this step, has only ever been
    # exercised against synthetic text.
    #
    # A negative result here does NOT block the phase: the reader already implements a header-walk
    # fallback as a real second mode, so the branch below retries with it and records which mode won.
    # =========================================================================================
    Write-Phase "STEP D: PQ-02 — batch DOM read of panel $Panel8Id (stat) and panel $Panel9Id (table legend)"
    New-Item -ItemType Directory -Force -Path $screenshotDir | Out-Null

    # End the series 120 s in the past so the LAST sub-window is over fully exported data rather than
    # over the gap between the current moment and the next 60 s export tick.
    $pq2End   = [datetime]::UtcNow.AddSeconds(-120)
    $pq2Wins  = @(Get-PinnedWindowSeries -EndUtc $pq2End -SubWindowSeconds 60 -Count 10)
    Write-Phase "  10 abutting 60s windows, $($pq2Wins[0].FromUtc) -> $($pq2Wins[-1].ToUtc)" 'Gray'

    $batch = Invoke-PanelReadBatch -PanelIds @($Panel8Id, $Panel9Id) -Windows $pq2Wins `
               -ScreenshotDir $screenshotDir -LocatorMode 'viewpanel' `
               -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight -BasicAuthBase64 $adminB64

    if ($batch.State -eq 'ReaderMissing') {
        # An INFRA ABORT, never a verdict: node, the playwright skill, or the reader script is
        # absent, so nothing about the panels was measured at all.
        Write-Phase "the panel reader is unavailable: $($batch.Error). Aborting." 'Red'
        Write-Phase "REMEDIATION: install node and the playwright skill, or point PHASE88_PLAYWRIGHT_SKILL_DIR at it." 'Yellow'
        exit 63
    }
    Write-Phase "  viewpanel batch: State=$($batch.State) requested=$($batch.Requested) emitted=$($batch.Emitted)" 'Gray'

    $viewpanelBroken = (Test-PanelUnlocatable $batch.Readings $Panel8Id) -or (Test-PanelUnlocatable $batch.Readings $Panel9Id)
    $ViewPanelUrlWorks = (-not $viewpanelBroken)
    $chosen = $batch

    if ($viewpanelBroken) {
        Write-Phase "  '?viewPanel=panel-<id>' did not render the intended panel — retrying with the header-walk fallback." 'Yellow'
        $walk = Invoke-PanelReadBatch -PanelIds @($Panel8Id, $Panel9Id) -Windows $pq2Wins `
                  -ScreenshotDir $screenshotDir -LocatorMode 'headerwalk' -PanelTitles @($Panel8Title, $Panel9Title) `
                  -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight -BasicAuthBase64 $adminB64
        if ($walk.State -eq 'ReaderMissing') {
            Write-Phase "the panel reader became unavailable mid-probe: $($walk.Error). Aborting." 'Red'; exit 63
        }
        Write-Phase "  headerwalk batch: State=$($walk.State) requested=$($walk.Requested) emitted=$($walk.Emitted)" 'Gray'
        $walkBroken = (Test-PanelUnlocatable $walk.Readings $Panel8Id) -or (Test-PanelUnlocatable $walk.Readings $Panel9Id)
        if (-not $walkBroken) { $chosen = $walk; $LocatorModeChosen = 'headerwalk' }
        else                  { $chosen = $walk; $LocatorModeChosen = $null }
    } else {
        $LocatorModeChosen = 'viewpanel'
    }

    $StatPanelParsed            = Test-StatParsed       $chosen.Readings $Panel8Id
    $TimeseriesLegendParsed     = Test-LegendParsed     $chosen.Readings $Panel9Id
    $TimeseriesLegendNamesBound = Test-LegendNamesBound $chosen.Readings $Panel9Id
    $Panel9LegendNames          = @(Get-LegendNames     $chosen.Readings $Panel9Id)
    $Panel8RawText              = Get-FirstRawText      $chosen.Readings $Panel8Id
    $Panel9RawText              = Get-FirstRawText      $chosen.Readings $Panel9Id
    $ScreenshotPaths            = @(Get-ScreenshotPaths $chosen.Readings)

    if ($null -eq $LocatorModeChosen) {
        $UnevaluableQuestions += 'PQ-02 LocatorModeChosen (neither viewpanel nor headerwalk located both panels)'
    }
    Write-Phase "  ViewPanelUrlWorks=$ViewPanelUrlWorks StatPanelParsed=$StatPanelParsed TimeseriesLegendParsed=$TimeseriesLegendParsed LocatorModeChosen=$LocatorModeChosen" 'Gray'
    Write-Phase "  TimeseriesLegendNamesBound=$TimeseriesLegendNamesBound legendNames=[$($Panel9LegendNames -join ' | ')]" 'Gray'
    Write-Phase "  panel $Panel8Id rawText: $($Panel8RawText -replace "`n", ' | ')" 'Gray'
    Write-Phase "  panel $Panel9Id rawText: $($Panel9RawText -replace "`n", ' | ')" 'Gray'
    Write-Phase "  $(@($ScreenshotPaths).Count) screenshot(s) under analyzer-reports/phase-88-screenshots/" 'Gray'

    # =========================================================================================
    # STEPs E-H and STEP J (PQ-01, PQ-03, PQ-05, PQ-06 and the artifact writer) are added by task 2
    # of plan 88-03. Until then this build answers PQ-02, PQ-04 and PQ-07 only, so it exits 2
    # (INCONCLUSIVE) — it must never exit 0 while four of the seven questions are unanswered.
    # =========================================================================================
    Write-Phase "PQ-01 / PQ-03 / PQ-05 / PQ-06 are not implemented in this build — exiting 2 (Inconclusive)." 'Yellow'
    exit 2
}
finally {
    # ---- STEP Z — TEARDOWN ----------------------------------------------------------------------
    # Stop ONLY a forward this script actually started, behind the recycled-PID guard. A forward this
    # script found already running belongs to someone else and is left exactly as it was found.
    if ($gfForwardOwned -and $gfForwardPid -gt 0) {
        if (Get-Command Stop-GrafanaForward -ErrorAction SilentlyContinue) {
            Stop-GrafanaForward $gfForwardPid
        }
    }
    Pop-Location
}
