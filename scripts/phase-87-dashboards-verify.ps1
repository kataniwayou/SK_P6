<#
.SYNOPSIS
    Phase 87 dashboards live proof (VER-01 panel sweep + VER-02 pod-recreate reproduction).

.DESCRIPTION
    The single-purpose live proof that both checked-in Grafana dashboards render REAL data and that
    the repo JSON is their only source of truth. It is an ASSERTION harness, not an eyeball check:
    every `targets[].expr` in k8s/dashboards/*.json is extracted, variable-interpolated the way
    Grafana would interpolate it, and replayed through Grafana's OWN datasource proxy — which proves
    the datasource wiring (DASH-02) and the PromQL (RTD-01/02, BPD-02) in a single assertion. Then the
    Grafana pod is deleted and recreated to prove the dashboards come from the repo and not from a
    database (VER-02, DASH-03).

    It follows the scripts/phase-83-ha-failover.ps1 skeleton: Push-Location + whole-body try/finally,
    a prefixed Write-Phase trace helper, the dot-sourced exit-code lib, the pin-exit-code-BEFORE-trim
    idiom, bounded polls with an explicit deadline, and a port-forward teardown behind a recycled-PID
    guard. Unlike phase-83 there is no analyzer to READ a report from — this phase adds zero C# — so
    this script AUTHORS the report itself (the scripts/phase-81-sweep.ps1 report-authoring pattern:
    build a pscustomobject, ConvertTo-Json | Set-Content, then resolve the exit code from the SAME
    in-memory object). The artifact shape mirrors analyzer-reports/phase-83-ha.json: one JSON object,
    PascalCase keys, ScenarioId first, Verdict second, one boolean per claim, then measured scalars
    and round-trip ("o") timestamps, then HumanSummary last.

    Flow:

        PRE      precondition gate            docker-desktop context + the SHARED stack forwards live
        STEP A   kubectl rollout status       deployment/grafana Available
        STEP B   own loopback forward         svc/grafana 3000:3000 --address 127.0.0.1 + /api/health poll
        STEP C   datasource                   isDefault + readOnly + datasource health OK
        STEP D   provisioning                 both uids present, meta.provisioned, provisionedExternalId
        STEP E   drive traffic                seed ~FanOutSeeder -> resolve wfId -> POST /start (204) -> hold
        STEP F   VER-01 panel sweep           replay every expr as a RANGE query through the proxy, two-class rule
        STEP G   dropdowns                    VAR-01 four classes, VAR-02 pod superset, VAR-03 chain filter
        STEP H   VER-02 pod delete            canonical spec byte-equality + no PVC + emptyDir
        STEP I   tamper assertion             POST /api/dashboards/db must be REJECTED
        STEP J   report + verdict             write the artifact, THEN resolve the exit code
        STEP Z   teardown                     stop ONLY this script's forward PID (outer finally)

    RANGE QUERIES, NOT INSTANT ONES (Phase-87 amendment). STEP F issues `/api/v1/query_range` with the
    step and `$__rate_interval` DERIVED FROM the datasource's provisioned `timeInterval`, because that
    is what a panel actually issues. The original version used `/api/v1/query` with `$__rate_interval`
    hardcoded to `5m`, and both choices masked a real defect: an instant query evaluates a single
    timestamp, and a 5m window spans five 60s-spaced samples, so `rate()` resolved in the gate while
    the browser's real 60s window returned NO series and three timeseries panels rendered "No data".
    The sweep recorded 30/30 Class A passing against three empty panels. The lesson is general: a
    proof that does not reproduce the caller's query shape proves only that the PromQL parses.
    The instant result is still issued per expression, but ONLY as a recorded diagnostic —
    `InstantOnlyPassExprs` names any expression that resolves instant and fails range, which is the
    exact signature of that false PASS. The report also records QueryMode / QueryStepSeconds /
    RateIntervalSeconds / DatasourceTimeInterval so the artifact states how it verified.

    THE TWO-CLASS RULE (REQUIREMENTS.md VER-01) — the heart of STEP F. Classification is PURELY
    MECHANICAL: an expression containing the substring `or vector(0)` is Class B and must return
    EXACTLY ONE non-empty series (a value of 0 PASSES — it means "this fault has not occurred in this
    window"); every other expression is Class A and must return AT LEAST ONE non-empty series. A range
    result can carry a series whose `values` array is empty — that is an empty panel, so it does not
    count as a hit. There is deliberately NO
    hand-maintained exception list here. That only holds because the authoring side is symmetric:
    dashboard-lint rule C3 FORCES the guard onto every Class-B counter and rule C7 FORBIDS it on every
    Class-A counter, so k8s/dashboards/business.json cannot present an expression this classifier files
    on the wrong side. An exception list would have to be kept in sync BY HAND with a file two other
    plans own — precisely the failure this design avoids.

    Two panels are Class B here BY DESIGN and their 0 is a pass, not a miss: `Keeper consumed - sent
    gap in range` and — less obviously — `Keeper consumed vs sent (conservation)`, whose two targets
    are guarded because keeper_messages_consumed_total / keeper_messages_sent_total increment only
    inside a RECOVERY event (research F-4, Pitfall 6) and STEP E's happy-path seeder drive contains no
    fault scenario. `WebApi 5xx ratio` is Class B by its own guard (lint rule C8). Because a silently
    ADDED guard would silently demote a Class-A signal (T-87-20), the report names EVERY expression the
    classifier put in Class B in a `ClassBExprs` array — a panel that quietly acquired a guard shows up
    as a new entry there instead of as silence.

    EXIT-CODE TABLE (the 0/1/2 classes are owned by lib/exit-code-resolution.ps1; every infra abort has
    its OWN distinct code so it can never be mistaken for a verdict — T-87-18):
        0   verdict PASS          (Resolve-AnalyzerExitCode Pass)
        1   verdict FAIL          (Resolve-AnalyzerExitCode Fail / unknown fail-closed)
        2   verdict INCONCLUSIVE  (Resolve-AnalyzerExitCode Inconclusive — an assertion could not be evaluated at all)
        10  grafana rollout/apply failed
        15  the grafana port-forward never carried traffic
        20  /api/health never reported database ok
        30  datasource health non-OK (or the datasource API call failed)
        40  a dashboard was not provisioned (or the provisioning API call failed)
        50  the traffic drive failed (seed / wf-id lookup / activation != 204)
        60  the VER-02 pod delete or recreate failed AS INFRASTRUCTURE
        64  usage / precondition error (wrong kube context, shared stack forwards down)

.PARAMETER SkipTraffic
    Skip STEP E. The Class-A assertion then cannot be evaluated honestly (the WebApi request panels and
    the processor identityName grouping need driven traffic), so the verdict degrades to Inconclusive
    and the script exits 2. Diagnostic use only — exit 2 is NEVER the phase gate.

.PARAMETER SkipPodDelete
    Skip STEP H. VER-02 then cannot be evaluated and the verdict degrades to Inconclusive (exit 2).
    Provided so the proof can run without disturbing a concurrent resilience sweep (T-87-19); grafana
    is not in the pipeline data path, so the delete is normally safe.

.NOTES
    Dev/ops-only tooling. No product source touched. Fully automated — no interactive prompt anywhere.

    PORT-FORWARD OWNERSHIP (T-87-11) — this script starts its OWN grafana forward on loopback port 3000
    and keeps the PID in an IN-MEMORY VARIABLE ONLY. It never creates, reads or writes the shared
    port-forward PID file that scripts/phase-80-up.ps1 owns: that file holds the eight shared harness
    tunnels, so reading it for teardown would kill all eight and break a concurrent sweep. Teardown
    stops exactly one PID, behind the same recycled-PID guard the repo uses everywhere.

    The forward binds --address 127.0.0.1 explicitly so the bridged host port stays loopback-only
    (T-87-01, extending T-80-17). The live stack is the Docker-Desktop k8s cluster ONLY.

    admin/admin is the deliberate dev posture set in k8s/23-grafana.yaml and is reachable only over
    loopback; auth hardening is out of scope for this milestone (T-87-02), so the credential is inline
    rather than parameterised — parameterising it would imply a security property this deployment does
    not have.

    Expect and IGNORE the benign Grafana log line about a plugin route not being covered by RBAC on
    every datasource-proxy call — those calls still return HTTP 200 with correct data. It is never a
    failure signal.

    JSON is parsed with ConvertFrom-Json throughout; no external JSON tooling is installed on this host.
#>

param(
    [switch]$SkipTraffic,
    [switch]$SkipPodDelete
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
# Declared BEFORE the try so the outer finally can read it under StrictMode even when the precondition
# gate exits before STEP B ever starts a forward.
$gfForwardPid = 0

Push-Location $repoRoot
try {

    # -----------------------------------------------------------------------
    # prefixed console-trace helper (Cyan step banner / Gray sub-detail / Yellow warning /
    # Red fatal-before-exit / Green success — the repo-wide colour convention).
    # -----------------------------------------------------------------------
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-87-dashboards-verify] $msg" -ForegroundColor $color
    }

    # -----------------------------------------------------------------------
    # dot-source the shared exit-code resolution lib (pure functions, no side effects).
    # Resolve-AnalyzerExitCode maps the report Verdict -> 0/1/2; Resolve-SweepClass maps the resolved
    # code -> a human class string for the final trace line. NEVER reimplement either as an inline
    # verdict switch: the fail-closed default (unknown/absent Verdict -> 1) is the T-87-18 control.
    # -----------------------------------------------------------------------
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')

    $gf       = 'http://127.0.0.1:3000'
    $proxy    = "$gf/api/datasources/proxy/uid/skp-prometheus"
    $adminB64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('admin:admin'))
    $auth     = @{ Authorization = "Basic $adminB64" }

    # =======================================================================
    # helpers
    # =======================================================================

    # Start this script's OWN grafana forward and hand back the PID. Bound to loopback explicitly.
    function Start-GrafanaForward {
        $p = Start-Process kubectl -PassThru -WindowStyle Hidden `
               -ArgumentList @('port-forward', 'svc/grafana', '3000:3000', '-n', 'skp', '--address', '127.0.0.1')
        return [int]$p.Id
    }

    # Recycled-PID guard: only kill if the PID is STILL a live kubectl process. A forward may have
    # exited and had its PID recycled by the OS — force-killing it blindly could terminate an unrelated
    # process. Best-effort; never throws.
    function Stop-GrafanaForward([int]$ForwardPid) {
        if ($ForwardPid -le 0) { return }
        try {
            $proc = Get-Process -Id $ForwardPid -ErrorAction SilentlyContinue |
                    Where-Object { $_.ProcessName -eq 'kubectl' }
            if ($proc) { Stop-Process -Id $ForwardPid -Force -ErrorAction SilentlyContinue }
        } catch { }
    }

    # A port-forward reports success on start even before the tunnel carries traffic, so gate on a REAL
    # parsed `database == ok` before proceeding. Returns 0 healthy, 15 the tunnel never carried traffic
    # at all, 20 it carried traffic but Grafana never reported its database ok.
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

    # POST an instant PromQL query through GRAFANA'S OWN datasource proxy — not straight at Prometheus.
    # Going through the proxy is what makes one assertion cover BOTH the datasource wiring and the
    # PromQL. POST (not GET) matches the datasource's provisioned httpMethod and avoids URL-length
    # limits on the longer conservation expressions.
    function Invoke-ProxyQuery([string]$Query) {
        return Invoke-RestMethod -Method Post -Uri "$proxy/api/v1/query" `
                 -Headers $auth -ContentType 'application/x-www-form-urlencoded' `
                 -Body @{ query = $Query } -TimeoutSec 45 -ErrorAction Stop
    }

    # RANGE query through the same proxy — this, not the instant form above, is what a panel
    # actually issues. The distinction is not academic and it cost this phase a false PASS:
    # an instant query evaluates ONE timestamp, so a `rate()` whose window is too narrow for
    # the series' real sample spacing can still resolve, while the same expression stepped
    # across a range returns NO series and the panel renders "No data". That is precisely
    # what happened with timeInterval=15s against a 60s effective resolution (the collector
    # emits explicit timestamps, so Prometheus stores one sample per SDK export, not per
    # scrape) — 30/30 Class A passed here while three timeseries panels were empty in the
    # browser. Class A/B is now decided on THIS call; the instant form is kept only as a
    # recorded diagnostic so a future divergence between the two is visible in the artifact
    # rather than silent.
    function Invoke-ProxyRangeQuery([string]$Query, [int]$StartUnix, [int]$EndUnix, [int]$StepSeconds) {
        return Invoke-RestMethod -Method Post -Uri "$proxy/api/v1/query_range" `
                 -Headers $auth -ContentType 'application/x-www-form-urlencoded' `
                 -Body @{ query = $Query; start = $StartUnix; end = $EndUnix; step = $StepSeconds } `
                 -TimeoutSec 90 -ErrorAction Stop
    }

    # Count series carrying at least one non-null sample. A range result can legitimately
    # contain a series whose `values` array is empty; that is an empty panel, not a hit.
    function Get-NonEmptySeriesCount($RangeResult) {
        $n = 0
        foreach ($s in @($RangeResult.data.result)) {
            $names = @(Get-PropertyNames $s)
            if ($names -contains 'values' -and @($s.values).Count -gt 0) { $n++ }
        }
        return $n
    }

    # Property names of a parsed-JSON object, safely. StrictMode Latest makes a bare access to an
    # absent property a terminating error, so every optional field must be membership-tested first —
    # but `$o.PSObject.Properties.Name` is itself unsafe: that is MEMBER ENUMERATION over a collection,
    # and on an EMPTY collection (a `{}` in the JSON — several panel `options`/`custom` blocks are
    # empty) StrictMode throws "The property 'Name' cannot be found on this object". Iterating the
    # collection and reading each PSPropertyInfo's own Name cannot hit that.
    function Get-PropertyNames($Object) {
        $names = @()
        if ($null -eq $Object) { return $names }
        foreach ($prop in $Object.PSObject.Properties) { $names += $prop.Name }
        return $names
    }

    # Collect every targets[].expr from a dashboard's panels, recursing into nested panels[] (row
    # panels nest their children).
    function Get-PanelTargets {
        param([Parameter(Mandatory)] $Panels, [Parameter(Mandatory)][string]$File)
        $acc = @()
        foreach ($p in @($Panels)) {
            $pNames = @(Get-PropertyNames $p)
            $title  = if ($pNames -contains 'title') { "$($p.title)" } else { '(untitled)' }
            if ($pNames -contains 'targets') {
                foreach ($t in @($p.targets)) {
                    $tNames = @(Get-PropertyNames $t)
                    if ($tNames -contains 'expr' -and -not [string]::IsNullOrWhiteSpace("$($t.expr)")) {
                        $acc += [pscustomobject]@{
                            File  = $File
                            Panel = $title
                            RefId = if ($tNames -contains 'refId') { "$($t.refId)" } else { '' }
                            Expr  = "$($t.expr)"
                        }
                    }
                }
            }
            if ($pNames -contains 'panels') {
                $acc += @(Get-PanelTargets -Panels $p.panels -File $File)
            }
        }
        return $acc
    }

    # Interpolate the dashboard variables the way Grafana would. `$source` renders as the explicit
    # alternation (allValue is deliberately null, so Grafana's getAllValue() expands "All" to the
    # option list — research F-8: an `allValue: ".*"` would also match series where the label is
    # ABSENT). `$pod` renders as `.+`, which requires the label to be present.
    #
    # `$__rate_interval` is NO LONGER hardcoded. It was '5m', which is wider than anything Grafana
    # would ever choose and therefore hid the defect this step exists to catch: a five-minute window
    # spans five 60s-spaced samples, so `rate()` resolved even while the browser's real 60s window
    # resolved to nothing. The caller now passes the value derived from the datasource's provisioned
    # timeInterval, so the assertion runs at the window a panel actually uses.
    #
    # String .Replace() (ordinal, literal) — NOT -replace, whose regex would eat the `$` and the parens.
    function Expand-DashboardExpr {
        param(
            [Parameter(Mandatory)][string]$Expr,
            [Parameter(Mandatory)][string]$RateInterval,   # e.g. '240s' — from the live datasource
            [Parameter(Mandatory)][string]$Range           # e.g. '1h'   — the dashboard's default
        )
        return $Expr.Replace('$__rate_interval', $RateInterval).
                     Replace('$__range', $Range).
                     Replace('$source', '(webapi|orchestrator|keeper|processor)').
                     Replace('$pod', '.+')
    }

    # Reproduce Grafana's own arithmetic rather than assuming a number.
    #   $__interval      floors at the datasource's timeInterval (its "minimum interval")
    #   $__rate_interval = max(4 x scrapeInterval, $__interval + scrapeInterval)
    # With timeInterval 60s this yields 240s. Reading it from the live datasource means the gate
    # tracks the manifest instead of drifting from it: change timeInterval in k8s/23-grafana.yaml
    # and this follows automatically.
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

    # Recursive sorted re-serialise. ConvertFrom-Json does NOT preserve key order, so two independently
    # parsed objects cannot be compared by a naive ConvertTo-Json — sort every object's keys at every
    # depth first. `return ,$array` keeps a one-element array an ARRAY through the return.
    function Get-CanonicalNode {
        param($Node)
        if ($null -eq $Node) { return $null }
        if ($Node -is [System.Management.Automation.PSCustomObject]) {
            $ordered = [ordered]@{}
            foreach ($name in (@(Get-PropertyNames $Node) | Sort-Object -CaseSensitive)) {
                $ordered[$name] = Get-CanonicalNode $Node.$name
            }
            return [pscustomobject]$ordered
        }
        if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [string]) {
            $items = New-Object System.Collections.ArrayList
            foreach ($item in $Node) { [void]$items.Add((Get-CanonicalNode $item)) }
            return ,($items.ToArray())
        }
        return $Node
    }

    # Canonical JSON for the `.dashboard` spec of a GET /api/dashboards/uid/<uid> response.
    #
    # `id` and `version` are dropped BEFORE canonicalising. Both are Grafana DB surrogates that appear
    # in NOTHING the repo authors — k8s/dashboards/*.json deliberately omits `id` (an exported `id`
    # breaks file provisioning) and never sets a DB row version. Grafana's own store is an emptyDir, so
    # a pod delete wipes the sqlite file and the provisioner re-inserts every dashboard from scratch,
    # necessarily minting fresh surrogates. Comparing them would assert a property the requirement does
    # not claim and the design deliberately does not have. Every byte the REPO contributes — uid, title,
    # description, tags, templating, every panel, every target — is compared.
    function Get-DashboardSpecJson($Wrapper) {
        $d = $Wrapper.dashboard
        $stripped = [ordered]@{}
        foreach ($n in @(Get-PropertyNames $d)) {
            if ($n -eq 'id' -or $n -eq 'version') { continue }
            $stripped[$n] = $d.$n
        }
        return (Get-CanonicalNode ([pscustomobject]$stripped) | ConvertTo-Json -Depth 100 -Compress)
    }

    # =======================================================================
    # PRECONDITION GATE (code 64) — usage errors fail with a remediation line, never a verdict.
    # =======================================================================
    Write-Phase "PRE: precondition gate (kube context + the shared stack forwards)"

    $ctxRaw = kubectl config current-context 2>$null
    # Pin the exit code BEFORE the trim: on a failed call stdout is empty, so .Trim() on the raw capture
    # (string-cast, never $null) cannot throw and bypass exit 64.
    $ctxExit = $LASTEXITCODE
    $ctx = ("$ctxRaw").Trim()
    if ($ctxExit -ne 0 -or $ctx -ne 'docker-desktop') {
        Write-Phase "kube context is '$ctx', not 'docker-desktop'. This proof targets the local Docker-Desktop cluster ONLY." 'Red'
        Write-Phase "REMEDIATION: kubectl config use-context docker-desktop" 'Yellow'
        exit 64
    }
    Write-Phase "  kube context = docker-desktop (OK)." 'Gray'

    # STEP E drives traffic over the SHARED harness forwards (baseapi 8080, postgres 5433, and the
    # in-proc seeder's rabbitmq 5673 / otel 4317), which this script deliberately does NOT own. Probe
    # the one that fronts them all rather than discovering the gap halfway through the drive.
    $sharedUp = $false
    try {
        $probe = Invoke-WebRequest -Uri 'http://localhost:8080/health/ready' -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        if ($probe.StatusCode -eq 200) { $sharedUp = $true }
    } catch { }
    if (-not $sharedUp -and -not $SkipTraffic) {
        Write-Phase "the shared stack port-forwards are not carrying traffic (no 200 from http://localhost:8080/health/ready)." 'Red'
        Write-Phase "REMEDIATION: pwsh -File scripts/phase-80-up.ps1   (it owns the eight shared forwards and reaps its own stale PIDs)" 'Yellow'
        exit 64
    }
    Write-Phase "  shared stack forwards live (baseapi /health/ready == 200)." 'Gray'

    # =======================================================================
    # STEP A — GRAFANA AVAILABLE (code 10).
    # =======================================================================
    Write-Phase "STEP A: kubectl -n skp rollout status deployment/grafana --timeout=180s"
    kubectl -n skp rollout status deployment/grafana --timeout=180s
    if ($LASTEXITCODE -ne 0) {
        Write-Phase "grafana did not become Available within 180s (exit $LASTEXITCODE). Aborting." 'Red'; exit 10
    }

    # =======================================================================
    # STEP B — THIS SCRIPT'S OWN LOOPBACK FORWARD (codes 15 then 20).
    # =======================================================================
    Write-Phase "STEP B: port-forward svc/grafana 3000:3000 --address 127.0.0.1 (PID held in memory only)"
    $gfForwardPid = Start-GrafanaForward
    Write-Phase "  forward PID $gfForwardPid — polling $gf/api/health for database == ok (bounded 90s)..." 'Gray'
    $healthCode = Wait-GrafanaHealthy 90
    if ($healthCode -eq 15) {
        Write-Phase "the grafana port-forward never carried traffic within 90s. Aborting." 'Red'; exit 15
    }
    if ($healthCode -ne 0) {
        Write-Phase "grafana /api/health never reported database ok within 90s. Aborting." 'Red'; exit 20
    }
    Write-Phase "  grafana health database == ok." 'Gray'

    # =======================================================================
    # STEP C — DATASOURCE (code 30) — DASH-02 + T-87-03.
    # =======================================================================
    Write-Phase "STEP C: datasource skp-prometheus (isDefault + readOnly + reachable)"
    try {
        $ds  = Invoke-RestMethod -Uri "$gf/api/datasources/uid/skp-prometheus" -Headers $auth -TimeoutSec 20 -ErrorAction Stop
        $dsh = Invoke-RestMethod -Uri "$gf/api/datasources/uid/skp-prometheus/health" -Headers $auth -TimeoutSec 45 -ErrorAction Stop
    } catch {
        Write-Phase "datasource API call failed: $($_.Exception.Message). Aborting." 'Red'; exit 30
    }
    if ("$($dsh.status)" -ne 'OK') {
        Write-Phase "datasource health is '$($dsh.status)', expected OK. Aborting." 'Red'; exit 30
    }
    # readOnly is the provisioning-owned flag (T-87-03: the UI cannot repoint the datasource);
    # isDefault is DASH-02. Both are CLAIMS, not infra — a false here is a Fail verdict, not an abort.
    $DatasourceDefaultReadOnly = ([bool]$ds.isDefault) -and ([bool]$ds.readOnly)
    Write-Phase "  isDefault=$($ds.isDefault) readOnly=$($ds.readOnly) health=$($dsh.status)" 'Gray'

    # =======================================================================
    # STEP D — PROVISIONING (code 40) — DASH-01.
    # =======================================================================
    Write-Phase "STEP D: both dashboards provisioned from the repo files"
    $expectedFiles = [ordered]@{ 'skp-runtime' = 'runtime.json'; 'skp-business' = 'business.json' }
    try {
        # ASSIGN FIRST, THEN WRAP. Invoke-RestMethod emits a JSON array as a SINGLE pipeline item, so
        # `@(Invoke-RestMethod ...)` yields a one-element array-OF-array and every later member access
        # silently returns arrays. This trap has now bitten twice in this phase.
        $searchRaw = Invoke-RestMethod -Uri "$gf/api/search?type=dash-db" -Headers $auth -TimeoutSec 20 -ErrorAction Stop
        $search = @($searchRaw)
    } catch {
        Write-Phase "GET /api/search failed: $($_.Exception.Message). Aborting." 'Red'; exit 40
    }
    $searchUids = @($search | ForEach-Object { "$($_.uid)" })
    $DashboardsProvisioned = $true
    foreach ($uid in $expectedFiles.Keys) {
        if ($searchUids -notcontains $uid) {
            Write-Phase "  uid '$uid' absent from /api/search (found: $($searchUids -join ', '))" 'Yellow'
            $DashboardsProvisioned = $false
            continue
        }
        try {
            $dash = Invoke-RestMethod -Uri "$gf/api/dashboards/uid/$uid" -Headers $auth -TimeoutSec 20 -ErrorAction Stop
        } catch {
            Write-Phase "GET /api/dashboards/uid/$uid failed: $($_.Exception.Message). Aborting." 'Red'; exit 40
        }
        $isProv  = [bool]$dash.meta.provisioned
        $extId   = "$($dash.meta.provisionedExternalId)"
        if (-not $isProv -or $extId -ne $expectedFiles[$uid]) {
            Write-Phase "  $uid provisioned=$isProv provisionedExternalId='$extId' (expected '$($expectedFiles[$uid])')" 'Yellow'
            $DashboardsProvisioned = $false
        } else {
            Write-Phase "  $uid provisioned=true externalId=$extId" 'Gray'
        }
    }

    # =======================================================================
    # STEP E — DRIVE TRAFFIC (code 50).
    # The happy-path seeder + activation path from scripts/phase-80-harness.ps1, reused in shape. It is
    # what makes the WebApi request panels (the collector drops every /health/ route datapoint, so an
    # idle stack shows almost nothing) and the processor identityName grouping assertable at all.
    # =======================================================================
    $trafficDriven = $false
    $windowStart = [DateTimeOffset]::UtcNow
    if ($SkipTraffic) {
        Write-Phase "STEP E: -SkipTraffic — no drive. Class A cannot be honestly evaluated; the verdict will degrade to Inconclusive." 'Yellow'
        $windowEnd = $windowStart
    } else {
        Write-Phase "STEP E: seed (dotnet test ~FanOutSeeder)"
        # MTP-native filter AFTER `--`; xunit.v3 under Microsoft.Testing.Platform SILENTLY IGNORES the
        # VSTest `--filter` syntax.
        dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Phase "seeder failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 50 }

        # Static string literal query, no interpolated input (T-87-12 / the existing T-80-20 form).
        Write-Phase "STEP E: resolve the v8-fanout-proof workflow id (kubectl exec psql)"
        $wfIdRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA `
                  -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"
        $wfIdExit = $LASTEXITCODE
        $wfId = ("$wfIdRaw").Trim()
        if ($wfIdExit -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) {
            Write-Phase "could not resolve the v8-fanout-proof workflow id (psql exit $wfIdExit). Aborting." 'Red'; exit 50
        }
        Write-Phase "  resolved wfId = $wfId" 'Gray'

        Write-Phase "STEP E: activation gate (POST /orchestration/start, require 204)"
        $startBody = ConvertTo-Json @($wfId)   # forces the JSON array even for a single id
        try {
            $startResp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
                           -ContentType 'application/json' -Body $startBody -TimeoutSec 20 -ErrorAction Stop
        } catch { Write-Phase "POST /orchestration/start threw: $($_.Exception.Message). Aborting." 'Red'; exit 50 }
        if ($startResp.StatusCode -ne 204) {
            Write-Phase "activation gate failed — expected 204, got $($startResp.StatusCode). Aborting." 'Red'; exit 50
        }
        Write-Phase "  activation accepted (204)." 'Gray'

        # Hold ~120s: the seeded cron is */30, so the window covers at least two fires plus the OTLP
        # export and the 15s Prometheus scrape — enough for the domain counters and the WebApi request
        # series to be present AND inside the collector's metric_expiration when STEP F replays them.
        $windowStart = [DateTimeOffset]::UtcNow
        Write-Phase "STEP E: observation window open at $($windowStart.ToString('o')) — holding 120s..."
        Start-Sleep -Seconds 120
        $windowEnd = [DateTimeOffset]::UtcNow
        Write-Phase "  window closed at $($windowEnd.ToString('o')) ($([int](($windowEnd - $windowStart).TotalSeconds))s)." 'Gray'
        $trafficDriven = $true
    }

    # =======================================================================
    # STEP F — THE VER-01 PANEL SWEEP.
    # =======================================================================
    Write-Phase "STEP F: VER-01 panel sweep — replay every checked-in expression through the datasource proxy"

    $dashboardFiles = @('k8s/dashboards/runtime.json', 'k8s/dashboards/business.json')
    $allTargets = @()
    foreach ($file in $dashboardFiles) {
        $doc = Get-Content $file -Raw | ConvertFrom-Json
        $allTargets += @(Get-PanelTargets -Panels $doc.panels -File (Split-Path $file -Leaf))
    }
    Write-Phase "  collected $($allTargets.Count) target expression(s) across $($dashboardFiles.Count) dashboard(s)." 'Gray'

    # ---- Derive the query window from the LIVE datasource + the dashboard's own default range ----
    # Everything below is read, not assumed, so the gate cannot drift from the manifest.
    $dsForStep = Invoke-RestMethod -Method Get -Uri "$gf/api/datasources/uid/skp-prometheus" `
                    -Headers $auth -TimeoutSec 30 -ErrorAction Stop
    $dsNames = @(Get-PropertyNames $dsForStep.jsonData)
    $tiText  = if ($dsNames -contains 'timeInterval') { "$($dsForStep.jsonData.timeInterval)" } else { '' }
    $tiSec   = ConvertFrom-GrafanaDuration $tiText
    if ($tiSec -le 0) {
        Write-Phase "STEP F: datasource jsonData.timeInterval is absent or unparseable ('$tiText') — cannot derive the panel query window." 'Red'
        exit 64
    }
    # $__interval floors at the datasource minimum interval, so the step equals timeInterval.
    $stepSec  = $tiSec
    $rateSec  = Get-RateIntervalSeconds -TimeIntervalSeconds $tiSec -StepSeconds $stepSec
    $rangeSec = 3600                                    # both dashboards ship time.from = now-1h
    $endUnix   = [int][DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $startUnix = $endUnix - $rangeSec
    $rateIntervalText = "$($rateSec)s"
    Write-Phase "  window: range=${rangeSec}s step=${stepSec}s `$__rate_interval=$rateIntervalText (datasource timeInterval=$tiText)" 'Gray'

    $classACount   = 0
    $classBCount   = 0
    $emptyClassA   = @()
    $classBExprs   = @()
    $classBOffKey  = @()
    $instantOnlyPasses = @()          # passed instant but failed range — the false-PASS signature
    foreach ($t in $allTargets) {
        $interpolated = Expand-DashboardExpr -Expr $t.Expr -RateInterval $rateIntervalText -Range '1h'
        $sampleCount  = -1
        $queryError   = ''
        $instantCount = -1
        try {
            # AUTHORITATIVE: the range query, i.e. what the panel issues.
            $r = Invoke-ProxyRangeQuery -Query $interpolated -StartUnix $startUnix -EndUnix $endUnix -StepSeconds $stepSec
            $sampleCount = Get-NonEmptySeriesCount $r
        } catch {
            $queryError = "$($_.Exception.Message)"
            $sampleCount = -1
        }
        # DIAGNOSTIC ONLY — never decides the verdict. This deliberately reproduces the SUPERSEDED
        # method (instant query, $__rate_interval hardcoded to the wide 5m) and flags every
        # expression the old gate would have called green while the panel renders empty. Comparing
        # instant-vs-range at the SAME window is worthless — they agree, which is why the first cut
        # of this diagnostic reported 0 against 16 genuine empties. The interesting comparison is
        # OLD METHOD vs REALITY, and a non-empty array here is a standing reminder of what the old
        # sweep could not see.
        try {
            $wide = Expand-DashboardExpr -Expr $t.Expr -RateInterval '5m' -Range '1h'
            $ri = Invoke-ProxyQuery $wide
            # ALWAYS @()-wrap before reading .Count — a single-element PromQL result is not an array in
            # PowerShell (the bug already guarded in scripts/phase-81-sweep.ps1).
            $instantCount = @($ri.data.result).Count
        } catch { $instantCount = -1 }
        if ($instantCount -ge 1 -and $sampleCount -lt 1) {
            $instantOnlyPasses += [pscustomobject]@{
                File = $t.File; Panel = $t.Panel; RefId = $t.RefId
                OldMethodSeries = $instantCount; RangeSeries = $sampleCount
            }
            Write-Phase "  OLD-METHOD FALSE PASS [$($t.File) / $($t.Panel) / $($t.RefId)] instant@5m=$instantCount range@$rateIntervalText=$sampleCount — the retired sweep would have called this green" 'Red'
        }

        # THE ONLY TEST: substring presence. No panel-title list, no expression list, no exceptions.
        $isClassB = $t.Expr.Contains('or vector(0)')
        if ($isClassB) {
            $classBCount++
            $classBExprs += [pscustomobject]@{
                File = $t.File; Panel = $t.Panel; RefId = $t.RefId; Expr = $t.Expr; SampleCount = $sampleCount
            }
            # Class B must return EXACTLY ONE sample. A value of 0 passes — that is the whole point of
            # the guard. Zero samples means the guard is not doing its job; more than one means the
            # guard is masking a real multi-series result.
            if ($sampleCount -ne 1) {
                $classBOffKey += [pscustomobject]@{
                    File = $t.File; Panel = $t.Panel; RefId = $t.RefId; Expr = $t.Expr
                    SampleCount = $sampleCount; QueryError = $queryError
                }
                Write-Phase "  CLASS B OFF-SPEC [$($t.File) / $($t.Panel) / $($t.RefId)] samples=$sampleCount $queryError" 'Yellow'
            }
        } else {
            $classACount++
            if ($sampleCount -lt 1) {
                $emptyClassA += [pscustomobject]@{
                    File = $t.File; Panel = $t.Panel; RefId = $t.RefId; Expr = $t.Expr
                    SampleCount = $sampleCount; QueryError = $queryError
                }
                Write-Phase "  CLASS A EMPTY  [$($t.File) / $($t.Panel) / $($t.RefId)] samples=$sampleCount $queryError" 'Yellow'
            }
        }
    }
    $ClassAPanelsNonEmpty = ($emptyClassA.Count -eq 0)
    $ClassBPanelsGuarded  = ($classBOffKey.Count -eq 0)
    Write-Phase "  Class A $classACount expr(s), $($emptyClassA.Count) empty | Class B $classBCount expr(s), $($classBOffKey.Count) off-spec." `
                $(if ($ClassAPanelsNonEmpty -and $ClassBPanelsGuarded) { 'Green' } else { 'Yellow' })

    # =======================================================================
    # STEP G — DROPDOWNS (VAR-01 / VAR-02 / VAR-03).
    # =======================================================================
    Write-Phase "STEP G: dropdown enumeration"

    # VAR-01 — the source dropdown must be EXACTLY the four service classes. An INSTANT query is used
    # (not label_values) because the TSDB label index over-returns; see the superset note below.
    $AllFourClassesPresent = $false
    $observedSources = @()
    try {
        $sr = Invoke-ProxyQuery 'group by (source) (process_runtime_dotnet_gc_collections_count_total)'
        $observedSources = @(@($sr.data.result) | ForEach-Object { "$($_.metric.source)" }) | Sort-Object
        $AllFourClassesPresent = (($observedSources -join ',') -eq 'keeper,orchestrator,processor,webapi')
    } catch {
        Write-Phase "  source-dropdown query failed: $($_.Exception.Message)" 'Yellow'
    }
    Write-Phase "  source dropdown -> [$($observedSources -join ', ')] (expected exactly keeper, orchestrator, processor, webapi)" `
                $(if ($AllFourClassesPresent) { 'Gray' } else { 'Yellow' })

    # VAR-02 — SUPERSET, deliberately not equality. Grafana's pod dropdown resolves through
    # /api/v1/label/<name>/values, which answers from the TSDB INDEX over overlapping blocks rather
    # than from samples, so it legitimately over-returns recently-dead pods (measured still returning
    # 79-minute-dead pods at a 60-second window — research F-7). That is Prometheus index granularity,
    # not a Grafana setting, so an exact-match assertion here would be wrong. The requirement is that
    # every LIVE pod is selectable.
    $classToLabel = @(
        [pscustomobject]@{ Class = 'webapi';       Label = 'baseapi-service'  },
        [pscustomobject]@{ Class = 'orchestrator'; Label = 'orchestrator'     },
        [pscustomobject]@{ Class = 'keeper';       Label = 'keeper'           },
        [pscustomobject]@{ Class = 'processor';    Label = 'processor-sample' }
    )
    $lvEnd   = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $lvStart = $lvEnd - 1800
    $PodDropdownSuperset = $true
    $KeeperChainFiltered = $false
    $livePodCount = 0
    $dropdownPods = @()
    $missingPods  = @()
    foreach ($c in $classToLabel) {
        $podsRaw = kubectl -n skp get pods -l "app=$($c.Label)" -o jsonpath='{.items[*].metadata.name}'
        $podsExit = $LASTEXITCODE
        $podsStr = ("$podsRaw").Trim()
        if ($podsExit -ne 0) {
            Write-Phase "kubectl get pods -l app=$($c.Label) failed (exit $podsExit). Aborting." 'Red'; exit 10
        }
        $livePods = @($podsStr -split '\s+' | Where-Object { $_ -match '\S' })
        $livePodCount += $livePods.Count

        $match = "process_runtime_dotnet_gc_collections_count_total{source=~`"($($c.Class))`"}"
        $qs = 'match[]=' + [uri]::EscapeDataString($match) + "&start=$lvStart&end=$lvEnd"
        $values = @()
        try {
            $lv = Invoke-RestMethod -Uri "$proxy/api/v1/label/service_instance_id/values?$qs" `
                    -Headers $auth -TimeoutSec 45 -ErrorAction Stop
            $values = @($lv.data)
        } catch {
            Write-Phase "  label_values for source=$($c.Class) failed: $($_.Exception.Message)" 'Yellow'
        }
        $dropdownPods += $values

        $missing = @($livePods | Where-Object { $values -notcontains $_ })
        if ($missing.Count -gt 0) {
            $PodDropdownSuperset = $false
            foreach ($m in $missing) {
                $missingPods += [pscustomobject]@{ Class = $c.Class; Pod = $m }
            }
        }
        Write-Phase "  $($c.Class): $($livePods.Count) live pod(s), dropdown returned $($values.Count), missing $($missing.Count)" `
                    $(if ($missing.Count -eq 0) { 'Gray' } else { 'Yellow' })

        # VAR-03 — the chained pod variable must ACTUALLY filter by the selected class, not merely
        # narrow it. Scoped to keeper, every returned name must be a keeper pod.
        if ($c.Class -eq 'keeper') {
            $KeeperChainFiltered = ($values.Count -gt 0) -and (@($values | Where-Object { $_ -notlike 'keeper-*' }).Count -eq 0)
            Write-Phase "  VAR-03 keeper-scoped chain returns only keeper-* names: $KeeperChainFiltered" `
                        $(if ($KeeperChainFiltered) { 'Gray' } else { 'Yellow' })
        }
    }
    $dropdownPodCount = @($dropdownPods | Sort-Object -Unique).Count

    # =======================================================================
    # STEP H — VER-02: DELETE THE POD, PROVE THE REPO REBUILDS IT (code 60).
    # =======================================================================
    $ReproducedAfterPodDelete = $false
    $NoPvc = $false
    $specDiffs = @()

    # DASH-03's structural half, evaluated regardless of -SkipPodDelete: no PVC in the namespace is
    # attributable to grafana, and the Deployment's grafana-storage volume really is an emptyDir. The
    # ABSENCE of persistence IS the requirement.
    $pvcRaw = kubectl -n skp get pvc -o jsonpath='{.items[*].metadata.name}'
    $pvcExit = $LASTEXITCODE
    $pvcStr = ("$pvcRaw").Trim()
    if ($pvcExit -ne 0) { Write-Phase "kubectl get pvc failed (exit $pvcExit). Aborting." 'Red'; exit 10 }
    $pvcNames = @($pvcStr -split '\s+' | Where-Object { $_ -match '\S' })
    $grafanaPvcs = @($pvcNames | Where-Object { $_ -match 'grafana' })

    $deployRaw = kubectl -n skp get deploy grafana -o json
    $deployExit = $LASTEXITCODE
    if ($deployExit -ne 0) { Write-Phase "kubectl get deploy grafana failed (exit $deployExit). Aborting." 'Red'; exit 10 }
    $deploy = ("$deployRaw") | ConvertFrom-Json
    $storageVol = @($deploy.spec.template.spec.volumes) | Where-Object { $_.name -eq 'grafana-storage' } | Select-Object -First 1
    $storageIsEmptyDir = ($null -ne $storageVol) -and (@(Get-PropertyNames $storageVol) -contains 'emptyDir')
    $NoPvc = ($grafanaPvcs.Count -eq 0) -and $storageIsEmptyDir
    Write-Phase "  grafana-attributable PVCs: $($grafanaPvcs.Count); grafana-storage is emptyDir: $storageIsEmptyDir" `
                $(if ($NoPvc) { 'Gray' } else { 'Yellow' })

    if ($SkipPodDelete) {
        Write-Phase "STEP H: -SkipPodDelete — VER-02 not evaluated; the verdict will degrade to Inconclusive." 'Yellow'
    } else {
        Write-Phase "STEP H: VER-02 — capture both specs, delete the grafana pod, recapture, compare"
        $before = @{}
        foreach ($uid in $expectedFiles.Keys) {
            try {
                $before[$uid] = Get-DashboardSpecJson (Invoke-RestMethod -Uri "$gf/api/dashboards/uid/$uid" -Headers $auth -TimeoutSec 20 -ErrorAction Stop)
            } catch {
                Write-Phase "could not capture the pre-delete spec for $uid : $($_.Exception.Message). Aborting." 'Red'; exit 60
            }
        }
        Write-Phase "  captured pre-delete specs ($(($before.Keys | Sort-Object) -join ', '))." 'Gray'

        kubectl -n skp delete pod -l app=grafana
        if ($LASTEXITCODE -ne 0) { Write-Phase "grafana pod delete failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 60 }
        kubectl -n skp rollout status deployment/grafana --timeout=180s
        if ($LASTEXITCODE -ne 0) { Write-Phase "grafana did not become Available within 180s after the delete. Aborting." 'Red'; exit 60 }

        # The forward dies with the pod it was bound to — stop the dead one and bind a new one.
        Stop-GrafanaForward $gfForwardPid
        $gfForwardPid = Start-GrafanaForward
        Write-Phase "  restarted the forward (PID $gfForwardPid) — re-polling /api/health..." 'Gray'
        $healthCode = Wait-GrafanaHealthy 120
        if ($healthCode -ne 0) {
            Write-Phase "grafana never became healthy again after the pod delete (code $healthCode). Aborting." 'Red'; exit 60
        }

        $ReproducedAfterPodDelete = $true
        foreach ($uid in $expectedFiles.Keys) {
            try {
                $afterResp = Invoke-RestMethod -Uri "$gf/api/dashboards/uid/$uid" -Headers $auth -TimeoutSec 20 -ErrorAction Stop
            } catch {
                Write-Phase "could not recapture the post-delete spec for $uid : $($_.Exception.Message). Aborting." 'Red'; exit 60
            }
            $afterSpec = Get-DashboardSpecJson $afterResp
            $sameSpec  = ($afterSpec -eq $before[$uid])
            $stillProv = ([bool]$afterResp.meta.provisioned) -and ("$($afterResp.meta.provisionedExternalId)" -eq $expectedFiles[$uid])
            if (-not $sameSpec -or -not $stillProv) {
                $ReproducedAfterPodDelete = $false
                $specDiffs += [pscustomobject]@{
                    Uid = $uid; SpecIdentical = $sameSpec; StillProvisioned = $stillProv
                    BeforeLength = $before[$uid].Length; AfterLength = $afterSpec.Length
                }
                Write-Phase "  $uid NOT reproduced (identical=$sameSpec provisioned=$stillProv)" 'Yellow'
            } else {
                Write-Phase "  $uid reproduced byte-identically ($($afterSpec.Length) canonical chars), meta.provisioned still true." 'Gray'
            }
        }
    }

    # =======================================================================
    # STEP I — THE TAMPER ASSERTION (T-87-03 / DASH-03).
    # A 200 here is a HARD FAIL, not an Inconclusive: it would mean the UI can become the source of
    # truth. Grafana answers 400 with a JSON body, so Invoke-WebRequest THROWS — the rejection lives in
    # $_.ErrorDetails.Message, not in a response object.
    # =======================================================================
    Write-Phase "STEP I: tamper assertion — POST /api/dashboards/db must be rejected"
    $tamperBody = '{"dashboard":{"uid":"skp-runtime","title":"tamper","schemaVersion":39,"panels":[]},"overwrite":true}'
    $UiSaveRejected = $false
    $tamperMessage = ''
    try {
        $tamperResp = Invoke-WebRequest -Method Post -Uri "$gf/api/dashboards/db" -Headers $auth `
                        -ContentType 'application/json' -Body $tamperBody -TimeoutSec 20 -ErrorAction Stop
        $tamperMessage = "ACCEPTED with HTTP $($tamperResp.StatusCode)"
        Write-Phase "  the UI save was ACCEPTED (HTTP $($tamperResp.StatusCode)) — the repo is NOT the only source of truth." 'Red'
    } catch {
        try {
            $tamperMessage = "$((("$($_.ErrorDetails.Message)") | ConvertFrom-Json).message)"
        } catch {
            $tamperMessage = "$($_.ErrorDetails.Message)"
        }
        $UiSaveRejected = ($tamperMessage -eq 'Cannot save provisioned dashboard')
        Write-Phase "  rejection message: '$tamperMessage'" $(if ($UiSaveRejected) { 'Gray' } else { 'Yellow' })
    }

    # =======================================================================
    # STEP J — REPORT + VERDICT.
    # The artifact is written BEFORE the exit code is resolved, so the on-disk Verdict is authoritative
    # even if the process is interrupted between the two (T-87-18).
    # =======================================================================
    Write-Phase "STEP J: build the verdict artifact"

    # Inconclusive is reserved for "the assertion could not be EVALUATED AT ALL" — never for a claim
    # that was evaluated and came back false. A false claim is a Fail.
    $unevaluable = @()
    if ($SkipTraffic)    { $unevaluable += 'ClassAPanelsNonEmpty (-SkipTraffic)' }
    if ($SkipPodDelete)  { $unevaluable += 'ReproducedAfterPodDelete (-SkipPodDelete)' }

    $claims = [ordered]@{
        DashboardsProvisioned     = [bool]$DashboardsProvisioned
        DatasourceDefaultReadOnly = [bool]$DatasourceDefaultReadOnly
        NoPvc                     = [bool]$NoPvc
        UiSaveRejected            = [bool]$UiSaveRejected
        AllFourClassesPresent     = [bool]$AllFourClassesPresent
        ClassAPanelsNonEmpty      = [bool]$ClassAPanelsNonEmpty
        ClassBPanelsGuarded       = [bool]$ClassBPanelsGuarded
        PodDropdownSuperset       = [bool]$PodDropdownSuperset
        KeeperChainFiltered       = [bool]$KeeperChainFiltered
        ReproducedAfterPodDelete  = [bool]$ReproducedAfterPodDelete
    }

    # PRECEDENCE: Fail BEATS Inconclusive. A claim that was evaluated and came back FALSE is
    # positive evidence of a defect, and no amount of *other* unevaluated claims makes it less
    # true. The original order tested unevaluable first, so any -Skip switch downgraded a genuine
    # Fail to Inconclusive — caught by the timeInterval negative control, where 16 empty Class A
    # expressions were reported under a verdict of Inconclusive purely because -SkipPodDelete had
    # parked one unrelated claim. Inconclusive now means only "nothing failed, but something could
    # not be checked".
    $verdict = 'Pass'
    if (@($claims.Values | Where-Object { -not $_ }).Count -gt 0) {
        $verdict = 'Fail'
    } elseif ($unevaluable.Count -gt 0) {
        $verdict = 'Inconclusive'
    }

    $failedClaims = @($claims.Keys | Where-Object { -not $claims[$_] })
    $claimDump = (@($claims.Keys | ForEach-Object { "$_=$($claims[$_])" }) -join ', ')
    $headline = switch ($verdict) {
        'Pass'         { "VER-01/VER-02 verdict=Pass: all $($claims.Count) claims green — every one of the $classACount unguarded expressions returned real samples through Grafana's datasource proxy, all $classBCount guarded expressions returned exactly one sample, both dropdowns enumerated correctly, a grafana pod delete reproduced both dashboard specs byte-identically from the repo, and a UI save was rejected." }
        'Inconclusive' { "VER-01/VER-02 verdict=Inconclusive: $($unevaluable -join ' and ') could not be evaluated, so the proof is NOT the phase gate — re-run with no switches." }
        default        { "VER-01/VER-02 verdict=Fail: $($failedClaims.Count) claim(s) false ($($failedClaims -join ', ')); $($emptyClassA.Count) of $classACount unguarded expressions returned no samples and $($classBOffKey.Count) of $classBCount guarded expressions were off-spec." }
    }

    $report = [pscustomobject][ordered]@{
        ScenarioId                = 'phase-87-dashboards'
        Verdict                   = $verdict
        DashboardsProvisioned     = $claims.DashboardsProvisioned
        DatasourceDefaultReadOnly = $claims.DatasourceDefaultReadOnly
        NoPvc                     = $claims.NoPvc
        UiSaveRejected            = $claims.UiSaveRejected
        AllFourClassesPresent     = $claims.AllFourClassesPresent
        ClassAPanelsNonEmpty      = $claims.ClassAPanelsNonEmpty
        ClassBPanelsGuarded       = $claims.ClassBPanelsGuarded
        PodDropdownSuperset       = $claims.PodDropdownSuperset
        KeeperChainFiltered       = $claims.KeeperChainFiltered
        ReproducedAfterPodDelete  = $claims.ReproducedAfterPodDelete
        TrafficDriven             = $trafficDriven
        TargetExprCount           = $allTargets.Count
        ClassAExprCount           = $classACount
        ClassBExprCount           = $classBCount
        # HOW the sweep was evaluated. Recorded because the previous version's silent choices —
        # instant queries with $__rate_interval hardcoded to 5m — are exactly what let three empty
        # panels pass. An artifact that does not state its query mode cannot be audited for this.
        QueryMode                 = 'query_range'
        QueryRangeSeconds         = $rangeSec
        QueryStepSeconds          = $stepSec
        RateIntervalSeconds       = $rateSec
        DatasourceTimeInterval    = $tiText
        # Expressions the RETIRED method (instant query at a hardcoded 5m) calls green while the
        # panel's real range query returns nothing. A non-empty array is the exact false-PASS
        # signature that let 30/30 Class A be reported against three empty panels.
        OldMethodFalsePassExprs   = $instantOnlyPasses
        ObservedSources           = $observedSources
        LivePodCount              = $livePodCount
        DropdownPodCount          = $dropdownPodCount
        GrafanaPvcCount           = $grafanaPvcs.Count
        StorageIsEmptyDir         = $storageIsEmptyDir
        TamperResponseMessage     = $tamperMessage
        WindowStart               = $windowStart.ToString('o')
        WindowEnd                 = $windowEnd.ToString('o')
        UnevaluableClaims         = $unevaluable
        EmptyClassAExprs          = $emptyClassA
        ClassBOffSpecExprs        = $classBOffKey
        ClassBExprs               = $classBExprs
        MissingDropdownPods       = $missingPods
        SpecDiffs                 = $specDiffs
        HumanSummary              = "$headline [$claimDump]"
    }

    $reportDir = Join-Path $repoRoot 'analyzer-reports'
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $reportPath = Join-Path $reportDir 'phase-87-dashboards.json'
    $report | ConvertTo-Json -Depth 5 | Set-Content -Path $reportPath -Encoding utf8
    Write-Phase "verdict artifact: $reportPath" 'Green'

    # Resolve the exit code from the SAME in-memory object the artifact was written from.
    $exitCode = Resolve-AnalyzerExitCode $report
    $verdictClass = (Resolve-SweepClass $exitCode).Class
    Write-Phase $report.HumanSummary $(if ($exitCode -eq 0) { 'Green' } else { 'Yellow' })
    Write-Phase "dashboards verdict exit = $exitCode ($verdictClass; 0=PASS 1=FAIL 2=INCONCLUSIVE)" `
                $(if ($exitCode -eq 0) { 'Green' } else { 'Yellow' })
    exit $exitCode

} finally {
    # -----------------------------------------------------------------------
    # STEP Z — TEARDOWN (NON-FATAL, outer finally). Stop ONLY this script's own grafana forward, behind
    # the recycled-PID guard. The eight shared harness forwards and the k8s stack are left exactly as
    # they were found: this script never touches the shared PID file that owns them (T-87-11).
    # -----------------------------------------------------------------------
    Write-Host "[phase-87-dashboards-verify] STEP Z: teardown — stopping only this script's grafana forward (PID $gfForwardPid)." -ForegroundColor Gray
    if ($gfForwardPid -gt 0) {
        try {
            $zProc = Get-Process -Id $gfForwardPid -ErrorAction SilentlyContinue |
                     Where-Object { $_.ProcessName -eq 'kubectl' }
            if ($zProc) { Stop-Process -Id $gfForwardPid -Force -ErrorAction SilentlyContinue }
        } catch { }
    }
    Pop-Location
}
