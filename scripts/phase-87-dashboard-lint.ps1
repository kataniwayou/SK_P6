#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    Phase 87 — the HERMETIC Grafana dashboard gate (DASH-04, RTD-01/02/03, BPD-01/02/03,
    VAR-01..04, VER-01). Parses the checked-in dashboard JSON, shape-asserts it against the
    live-verified metric inventory, and builds the whole k8s stack offline. No cluster access.

.DESCRIPTION
    `87-VALIDATION.md` names this script the Wave-0 gap: it is what makes each of the ~30
    Phase-87 panel edits cheap to verify (~5 s, no cluster, no traffic). Every rule below exists
    because a specific failure mode would otherwise ship as a *silently empty panel* — a dashboard
    that renders without error and tells the operator nothing.

    The three allowlists are the live-verified inventory captured in `87-RESEARCH.md`
    (§ "Complete live runtime inventory", § "The nine domain counters", § "WebApi row (BPD-03)").
    A metric name outside them is, by construction, a typo or a family that does not exist in this
    Prometheus — research F-1 is the canonical example: `dotnet_gc_collections_total` DOES exist in
    the index, but only from an unrelated `MCP.Terminal` host process, so a panel written against it
    renders blank forever.

    Deliberately NOT allowlisted:
      * the `messaging_masstransit_*` family — no REQ-ID needs it; excluding it keeps the two
        dashboards scoped to the requirements.
      * the entire `dotnet_*` family — research F-1 (see above). Rule S8 hard-fails on it.

    RULE INVENTORY

        SHAPE (always runs; scoped by -Dashboard)
          S1  file parses as JSON
          S2  top-level identity: uid / title / schemaVersion 39 / editable false / no id / no __inputs
          S3  DASH-04 — the literal `skp-prometheus` appears nowhere
          S4  DASH-04 — every datasource reference is {type:prometheus, uid:${datasource}}
          S5  VAR-01/02/03 — exactly the three template variables, with the F-8 `allValue: null` pin
          S6  panel types restricted; the Angular `graph` panel (removed in Grafana 11) is banned
          S7  VAR-03 — every targets[].expr carries BOTH variable filters
          S8  RTD-01 / F-1 — no expr references the `dotnet_*` family
          S9  T-87-14 — every METRIC token is allowlisted
          S10 runtime.json is runtime-family only
          S11 RTD-03 — every runtime timeseries breaks down per pod in its by(...) clause
          S12 RTD-02 — no panel titled CPU / Uptime / Working Set (an empty "CPU" panel is a defect)
          S13 BPD-03 — the webapi is never pinned with a literal source="webapi"
          S14 T-87-08 — `kubectl kustomize k8s/` exits 0 (offline; the JSON-layer twin of T-80-14)
          S15 T-87-14 — every BARE token is a KNOWN_LABEL (closes S9's position-scoping escape hatch)
          S16 T-87-15 — every non-row panel carries a non-empty operator-facing `description`

        COVERAGE (skipped by -ShapeOnly; scoped by -Dashboard)
          C1  BPD-01 — all nine DOMAIN counters referenced
          C2  BPD-02 — dedicated fault panels exist
          C3  BPD-02 / VER-01 Class B — the four no-live-series counters carry `or vector(0)`
          C4  BPD-03 — the WebApi histogram is used (_count and _bucket)
          C5  VAR-04 / F-6 — processor panels discriminate on identityName, never service_name
          C6  RTD-02 — the four honest substitute titles exist
          C7  VER-01 Class A — the exact inverse of C3: no Class-A counter may carry a guard
          C8  VER-01 Class B (WebApi) — the 5xx ratio MUST carry its guard

    C3 and C7 partition the nine DOMAIN counters on exactly the substring `or vector(0)` that plan
    87-05's STEP F classifies on, and C8 covers the one WebApi expression that is Class B without
    being a domain counter. That is why 87-05's live classifier needs no hand-maintained exception
    list: no expression in `business.json` sits in a third, lint-unenforced category.

    Rule numbering is APPEND-ONLY. S15, S16, C7 and C8 are appended after S14 / C6 rather than inserted
    beside their logical siblings, because plans 87-02/87-03/87-04/87-05 already cite these ids by
    number.

.PARAMETER Dashboard
    runtime | business | all (default all). Narrows both sections to one file.

.PARAMETER ShapeOnly
    Skip the COVERAGE section. Used by every in-flight authoring task before the coverage rules
    are satisfiable.

.PARAMETER SelfTest
    Run ONLY the Get-MetricTokens fixture table and exit. Reads no dashboard file, so it stays
    runnable in every wave — it is the negative control that pins the position scoping described
    on Get-MetricTokens below (threat T-87-19).

.OUTPUTS
    On failure: one block per accumulated failure (rule id + file + panel title + expression in
    Red, remediation in Yellow), then the failure count. Exit 1.
    On success: the counts of files, panels, targets and rules checked. Exit 0.
#>
[CmdletBinding()]
param(
    [ValidateSet('runtime', 'business', 'all')]
    [string]$Dashboard = 'all',

    [switch]$ShapeOnly,

    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Repo root = one level up from this script (scripts/ -> repo root).
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

# ===========================================================================
# CONSTANTS — the live-verified metric inventory (87-RESEARCH.md, 2026-07-27).
# ===========================================================================

# RUNTIME (17) — `process_runtime_dotnet_*`, emitted by OpenTelemetry.Instrumentation.Runtime
# 1.15.0 on net8.0. Research F-1: on net9.0 this whole family renames to `dotnet_*`.
$RuntimeAllowlist = @(
    'process_runtime_dotnet_gc_collections_count_total'
    'process_runtime_dotnet_gc_heap_size_bytes'
    'process_runtime_dotnet_gc_heap_fragmentation_size_bytes'
    'process_runtime_dotnet_gc_committed_memory_size_bytes'
    'process_runtime_dotnet_gc_allocations_size_bytes_total'
    'process_runtime_dotnet_gc_duration_nanoseconds_total'
    'process_runtime_dotnet_gc_objects_size_bytes'
    'process_runtime_dotnet_thread_pool_threads_count'
    'process_runtime_dotnet_thread_pool_queue_length'
    'process_runtime_dotnet_thread_pool_completed_items_count_total'
    'process_runtime_dotnet_exceptions_count_total'
    'process_runtime_dotnet_monitor_lock_contention_count_total'
    'process_runtime_dotnet_assemblies_count'
    'process_runtime_dotnet_timer_count'
    'process_runtime_dotnet_jit_methods_compiled_count_total'
    'process_runtime_dotnet_jit_il_compiled_size_bytes_total'
    'process_runtime_dotnet_jit_compilation_time_nanoseconds_total'
)

# DOMAIN (9) — BPD-01. The `_total` suffix is appended by the collector's Prometheus exporter,
# not by the instrument name (OrchestratorMetrics.cs:17-19), so these names are exactly right.
$DomainAllowlist = @(
    'orchestrator_messages_consumed_total'
    'orchestrator_messages_sent_total'
    'orchestrator_step_unresolved_total'
    'keeper_messages_consumed_total'
    'keeper_messages_sent_total'
    'keeper_l2_probe_total'
    'processor_messages_consumed_total'
    'processor_messages_sent_total'
    'processor_spawn_dropped_total'
)

# WEBAPI (5) — BPD-03. ASP.NET Core request metrics only; the webapi registers no domain meter.
$WebApiAllowlist = @(
    'http_server_request_duration_seconds_count'
    'http_server_request_duration_seconds_bucket'
    'http_server_active_requests'
    'kestrel_active_connections'
    'kestrel_queued_connections'
)

# KNOWN_LABELS (3) — the ONLY PromQL *label* names used anywhere in either dashboard that also
# match one of the metric family regexes below. Research's live-verified label list for
# `http_server_request_duration_seconds_*` is http_route, http_request_method,
# http_response_status_code, url_scheme, network_protocol_version, otel_scope_name — only the three
# `http_`-prefixed entries collide. Every other label the phase groups or filters on (source,
# service_instance_id, generation, le, identityName, processorId, service_name) matches no family
# regex at all; note in particular that `processorId` does NOT match `processor_\w+`, because the
# family regex requires the underscore (pinned by -SelfTest fixture (e)).
# This constant exists so rule S15 can tell a legitimate grouping label from a metric written
# without its mandatory selector.
$KnownLabels = @(
    'http_route'
    'http_request_method'
    'http_response_status_code'
)

$AllMetrics = @($RuntimeAllowlist + $DomainAllowlist + $WebApiAllowlist)

# Metric family regex. The leading negative lookbehind stops a match starting mid-identifier.
# Alternation order matters: `process_runtime_dotnet_\w+` must precede `dotnet_\w+` so the leftmost
# match at a `process_runtime_dotnet_*` name consumes the whole token instead of leaving a bogus
# inner `dotnet_...` match behind.
$FamilyRegex = '(?<![A-Za-z0-9_:])(?:process_runtime_dotnet_\w+|orchestrator_\w+|keeper_\w+|processor_\w+|http_\w+|kestrel_\w+|messaging_\w+|dotnet_\w+)'

# The mandatory selector fragments (VAR-03).
$FilterSource = 'source=~"$source"'
$FilterPod    = 'service_instance_id=~"$pod"'
$Guard        = 'or vector(0)'

# VER-01 Class B domain counters (C3) and Class A domain counters (C7). These two sets partition
# the nine DOMAIN names; the five WEBAPI names are governed by C8.
$ClassBCounters = @(
    'orchestrator_step_unresolved_total'
    'processor_spawn_dropped_total'
    'keeper_messages_consumed_total'
    'keeper_messages_sent_total'
)
$ClassACounters = @(
    'keeper_l2_probe_total'
    'orchestrator_messages_consumed_total'
    'orchestrator_messages_sent_total'
    'processor_messages_consumed_total'
    'processor_messages_sent_total'
)

$DashboardSpecs = @(
    [pscustomobject]@{ Key = 'runtime';  Path = (Join-Path $RepoRoot 'k8s/dashboards/runtime.json');  Uid = 'skp-runtime';  Tag = 'runtime'  }
    [pscustomobject]@{ Key = 'business'; Path = (Join-Path $RepoRoot 'k8s/dashboards/business.json'); Uid = 'skp-business'; Tag = 'business' }
)

# ---------------------------------------------------------------------------
# Failure accumulation. Mechanism: NEVER throw on the first problem — one run must report every
# defect so an authoring task fixes them in a single pass instead of a bisect loop.
# ---------------------------------------------------------------------------
$script:Failures     = [System.Collections.Generic.List[object]]::new()
$script:RulesChecked = [System.Collections.Generic.List[string]]::new()

function Add-Failure {
    param(
        [Parameter(Mandatory)][string]$Rule,
        [string]$File = '',
        [string]$Panel = '',
        [string]$Expr = '',
        [Parameter(Mandatory)][string]$Remediation
    )
    $script:Failures.Add([pscustomobject]@{
        Rule        = $Rule
        File        = $File
        Panel       = $Panel
        Expr        = $Expr
        Remediation = $Remediation
    })
}

function Add-Rule {
    param([Parameter(Mandatory)][string]$Rule)
    if (-not $script:RulesChecked.Contains($Rule)) { $script:RulesChecked.Add($Rule) }
}

# ---------------------------------------------------------------------------
# Strict-mode-safe property access. `Set-StrictMode -Version Latest` throws on a missing property
# of a PSCustomObject, and dashboard JSON is legitimately sparse (a stat panel has no `generation`,
# a row panel has no `targets`), so every read goes through here.
# ---------------------------------------------------------------------------
function Test-HasProp {
    param($Obj, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Obj) { return $false }
    if ($Obj -isnot [psobject]) { return $false }
    return ($Obj.PSObject.Properties.Name -contains $Name)
}

function Get-Prop {
    param($Obj, [Parameter(Mandatory)][string]$Name, $Default = $null)
    if (Test-HasProp $Obj $Name) { return $Obj.$Name }
    return $Default
}

# ---------------------------------------------------------------------------
# Get-MetricTokens — POSITION-SCOPED metric-name extraction.
#
# The metric family regex also matches PromQL *label* names, which are not metric names and must
# not be allowlisted. Plan 87-04's WebApi row makes this concrete and unavoidable: `sum by
# (http_route) (...)`, `sum by (le, http_route) (...)`, `sum by (http_response_status_code) (...)`
# and the in-selector `http_response_status_code=~"5.."` all match `http_\w+` and none is a metric.
# Those four forms are copied verbatim from the live-verified PromQL in 87-RESEARCH.md § "WebApi
# row (BPD-03)", so an unscoped sweep would fail rule S9 on four CORRECT panels (threat T-87-19).
#
# THE ANCHOR: a family-regex match is a METRIC token only when the next character after the match
# — allowing intervening spaces — is `{`. This is complete for these two dashboards because rule
# S7 / VAR-03 already makes `{source=~"$source", service_instance_id=~"$pod"}` mandatory on every
# metric reference, so every metric is written `name{...}`. And no label position can satisfy it:
# a label inside by(...)/without(...)/on(...)/ignoring(...)/group_left(...)/group_right(...) is
# followed by `,` or `)`, and a label inside a selector is followed by `=`, `!=`, `=~` or `!~`.
#
# The rejected alternative — "strip the contents of the by(...)/without(...)/on(...)/group_left(...)
# clauses before running the family regex" — handles Panels 10/11/12 but NOT Panel 13, whose
# colliding `http_response_status_code` sits inside the selector braces rather than in a grouping
# clause, and would miss that same collision anywhere it appears as a filter label.
#
# Both sets are returned, because the residue is itself load-bearing:
#   Metric — matches in metric-selector position. Rules S9 / S10 test these against the allowlists.
#   Bare   — matches NOT in metric-selector position. Rule S15 tests these against KNOWN_LABELS, so
#            the `{`-anchor cannot silently swallow a real metric written without its selector.
# ---------------------------------------------------------------------------
function Get-MetricTokens {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Expr)

    $metric = [System.Collections.Generic.List[string]]::new()
    $bare   = [System.Collections.Generic.List[string]]::new()

    foreach ($m in [regex]::Matches($Expr, $FamilyRegex)) {
        $tail = $Expr.Substring($m.Index + $m.Length)
        # POSITION SCOPE — the load-bearing anchor. Removing it reverts this function to an
        # unscoped family sweep and MUST break -SelfTest on fixtures (a)-(d).
        $isMetricPosition = $tail.TrimStart(' ').StartsWith('{')

        if ($isMetricPosition) {
            if (-not $metric.Contains($m.Value)) { $metric.Add($m.Value) }
        } else {
            if (-not $bare.Contains($m.Value)) { $bare.Add($m.Value) }
        }
    }

    return [pscustomobject]@{ Metric = @($metric); Bare = @($bare) }
}

# S9 predicate — metric tokens outside the union of the three allowlists (T-87-14).
# NOTE on the return shape: these helpers return a PLAIN array and every call site wraps the call in
# @(...). The `return ,@(...)` unary-comma idiom is deliberately NOT used — it survives a scalar
# assignment but emits a one-element array-of-array through `@(func())`, which silently turns "no
# violations" into one violation whose name is the empty string.
function Get-S9Violations {
    param([string[]]$Metric)
    return @(@($Metric) | Where-Object { $AllMetrics -notcontains $_ })
}

# S15 predicate — bare tokens that are not KNOWN_LABELS (T-87-14, the position-scoping counterweight).
function Get-S15Violations {
    param([string[]]$Bare)
    return @(@($Bare) | Where-Object { $KnownLabels -notcontains $_ })
}

# ---------------------------------------------------------------------------
# Get-DashboardPanels — flattens panels[] plus panels[].panels[] so nested `row` children are
# linted exactly like top-level panels.
# ---------------------------------------------------------------------------
function Get-DashboardPanels {
    param([Parameter(Mandatory)]$Dash)
    $flat = @()
    foreach ($p in @(Get-Prop $Dash 'panels' @())) {
        $flat += $p
        foreach ($n in @(Get-Prop $p 'panels' @())) { $flat += $n }
    }
    return @($flat)
}

# ---------------------------------------------------------------------------
# Get-DashboardTargets — one row per targets[] entry, carrying enough context (file, panel title,
# panel type, refId) for the remediation block to name the exact place to edit.
# ---------------------------------------------------------------------------
function Get-DashboardTargets {
    param([Parameter(Mandatory)]$Panels, [Parameter(Mandatory)][string]$File)
    $rows = @()
    for ($i = 0; $i -lt @($Panels).Count; $i++) {
        $p = @($Panels)[$i]
        foreach ($t in @(Get-Prop $p 'targets' @())) {
            $rows += [pscustomobject]@{
                File       = $File
                PanelIndex = $i
                PanelTitle = [string](Get-Prop $p 'title' '(untitled)')
                PanelType  = [string](Get-Prop $p 'type' '(untyped)')
                RefId      = [string](Get-Prop $t 'refId' '?')
                Expr       = [string](Get-Prop $t 'expr' '')
            }
        }
    }
    return @($rows)
}

# Extract the contents of every `by (...)` grouping clause in an expression.
function Get-ByClauses {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Expr)
    return @([regex]::Matches($Expr, '\bby\s*\(([^)]*)\)') | ForEach-Object { $_.Groups[1].Value })
}

function Test-SetEqual {
    param([string[]]$A, [string[]]$B)
    $x = @(@($A) | Sort-Object -Unique)
    $y = @(@($B) | Sort-Object -Unique)
    if ($x.Count -ne $y.Count) { return $false }
    for ($i = 0; $i -lt $x.Count; $i++) { if ($x[$i] -ne $y[$i]) { return $false } }
    return $true
}

# ===========================================================================
# (0) -SelfTest — the Get-MetricTokens fixture table. Reads NO dashboard file.
# ===========================================================================
# These six rows are the label-vs-metric collision cases and their controls. (a)-(d) are the four
# WebApi expressions plan 87-04 authors as Panels 10-13; (e) proves `processorId` does not match
# `processor_\w+`; (f) is the S15 control — a metric written without its mandatory selector must
# land in Bare and be REJECTED there, so the `{`-anchor cannot become an escape hatch.
if ($SelfTest) {
    Write-Host "==> Phase 87 dashboard lint — SELF TEST (Get-MetricTokens position scoping)" -ForegroundColor Cyan
    Write-Host ""

    $fixtures = @(
        [pscustomobject]@{
            Id     = 'a'
            Note   = 'sum by (http_route) — grouping-label collision'
            Expr   = 'sum by (http_route) (rate(http_server_request_duration_seconds_count{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval]))'
            Metric = @('http_server_request_duration_seconds_count')
            Bare   = @('http_route')
            S15Rejects = $false
        }
        [pscustomobject]@{
            Id     = 'b'
            Note   = 'sum by (le, http_route) — histogram_quantile grouping collision'
            Expr   = 'histogram_quantile(0.95, sum by (le, http_route) (rate(http_server_request_duration_seconds_bucket{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval])))'
            Metric = @('http_server_request_duration_seconds_bucket')
            Bare   = @('http_route')
            S15Rejects = $false
        }
        [pscustomobject]@{
            Id     = 'c'
            Note   = 'sum by (http_response_status_code) — grouping-label collision'
            Expr   = 'sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval]))'
            Metric = @('http_server_request_duration_seconds_count')
            Bare   = @('http_response_status_code')
            S15Rejects = $false
        }
        [pscustomobject]@{
            Id     = 'd'
            Note   = 'in-SELECTOR collision — the case the strip-clauses approach would miss'
            Expr   = 'sum(rate(http_server_request_duration_seconds_count{source=~"$source", service_instance_id=~"$pod", http_response_status_code=~"5.."}[$__rate_interval])) or vector(0)'
            Metric = @('http_server_request_duration_seconds_count')
            Bare   = @('http_response_status_code')
            S15Rejects = $false
        }
        [pscustomobject]@{
            Id     = 'e'
            Note   = 'processorId does NOT match processor_\w+ (the underscore is required)'
            Expr   = 'sum by (processorId) (rate(orchestrator_messages_sent_total{source=~"$source", service_instance_id=~"$pod"}[$__rate_interval]))'
            Metric = @('orchestrator_messages_sent_total')
            Bare   = @()
            S15Rejects = $false
        }
        [pscustomobject]@{
            Id     = 'f'
            Note   = 'S15 control — a metric with NO selector lands in Bare and must be rejected'
            Expr   = 'sum(rate(orchestrator_messages_sent_total[$__rate_interval]))'
            Metric = @()
            Bare   = @('orchestrator_messages_sent_total')
            S15Rejects = $true
        }
    )

    $selfFail = 0
    foreach ($f in $fixtures) {
        $tok  = Get-MetricTokens -Expr $f.Expr
        $s9   = @(Get-S9Violations  -Metric $tok.Metric)
        $s15  = @(Get-S15Violations -Bare   $tok.Bare)

        $problems = @()
        if (-not (Test-SetEqual $tok.Metric $f.Metric)) {
            $problems += "Metric expected {$($f.Metric -join ', ')} got {$($tok.Metric -join ', ')}"
        }
        if (-not (Test-SetEqual $tok.Bare $f.Bare)) {
            $problems += "Bare expected {$($f.Bare -join ', ')} got {$($tok.Bare -join ', ')}"
        }
        # Rows (a)-(e) must pass BOTH predicates. Row (f) must be REJECTED by S15.
        if (@($s9).Count -gt 0) {
            $problems += "S9 rejected metric tokens outside the allowlists: {$(@($s9) -join ', ')}"
        }
        if ($f.S15Rejects) {
            if (@($s15).Count -eq 0) { $problems += "S15 was expected to REJECT this row but accepted it" }
        } else {
            if (@($s15).Count -gt 0) { $problems += "S15 rejected bare tokens outside KNOWN_LABELS: {$(@($s15) -join ', ')}" }
        }

        if ($problems.Count -eq 0) {
            Write-Host ("  ({0}) PASS  {1}" -f $f.Id, $f.Note) -ForegroundColor Green
        } else {
            $selfFail++
            Write-Host ("  ({0}) FAIL  {1}" -f $f.Id, $f.Note) -ForegroundColor Red
            Write-Host ("        expr: {0}" -f $f.Expr) -ForegroundColor Red
            foreach ($p in $problems) { Write-Host ("        {0}" -f $p) -ForegroundColor Yellow }
        }
    }

    Write-Host ""
    if ($selfFail -eq 0) {
        Write-Host "SELF TEST PASSED — 6/6 fixtures; Get-MetricTokens position scoping is intact." -ForegroundColor Green
        exit 0
    }
    Write-Host "SELF TEST FAILED — $selfFail/6 fixture rows mismatched." -ForegroundColor Red
    Write-Host "Remediation: Get-MetricTokens must treat a family-regex match as a METRIC token ONLY" -ForegroundColor Yellow
    Write-Host "when the next non-space character is '{' (rule S9), and every non-metric match must" -ForegroundColor Yellow
    Write-Host "be checked against KNOWN_LABELS (rule S15). Restore the '{'-anchor; do NOT widen the" -ForegroundColor Yellow
    Write-Host "allowlists to admit PromQL label names (threat T-87-19)." -ForegroundColor Yellow
    exit 1
}

# ===========================================================================
# (1) Load the dashboards in scope.  [S1]
# ===========================================================================
$selected = @($DashboardSpecs | Where-Object { $Dashboard -eq 'all' -or $_.Key -eq $Dashboard })

Write-Host "==> Phase 87 dashboard lint — $($selected.Count) file(s), scope '$Dashboard'$(if ($ShapeOnly) { ', SHAPE ONLY' })" -ForegroundColor Cyan

$docs = @()
Add-Rule 'S1'
foreach ($spec in $selected) {
    $rel = $spec.Path.Substring($RepoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
    if (-not (Test-Path $spec.Path)) {
        Add-Failure -Rule 'S1' -File $rel -Remediation "file does not exist — plan 87-01 Task 2 authors k8s/dashboards/$($spec.Key).json"
        continue
    }
    $raw = Get-Content -Path $spec.Path -Raw
    $json = $null
    try {
        $json = $raw | ConvertFrom-Json
    } catch {
        Add-Failure -Rule 'S1' -File $rel -Remediation "not valid JSON: $($_.Exception.Message)"
        continue
    }
    $panels = @(Get-DashboardPanels $json)
    $docs += [pscustomobject]@{
        Key     = $spec.Key
        Uid     = $spec.Uid
        Rel     = $rel
        Raw     = $raw
        Json    = $json
        Panels  = $panels
        Targets = @(Get-DashboardTargets -Panels $panels -File $rel)
    }
}

# ===========================================================================
# (2) SHAPE section — always runs.
# ===========================================================================
foreach ($d in $docs) {

    # --- S2: top-level identity. `id` is DB-assigned and a stale value can collide; `__inputs` /
    #     `__requires` belong to the share/export format and BREAK file provisioning.
    Add-Rule 'S2'
    if ([string](Get-Prop $d.Json 'uid' '') -ne $d.Uid) {
        Add-Failure -Rule 'S2' -File $d.Rel -Remediation "uid must be exactly '$($d.Uid)' (the /d/<uid> URL and every API assertion key on it)"
    }
    if ([string]::IsNullOrWhiteSpace([string](Get-Prop $d.Json 'title' ''))) {
        Add-Failure -Rule 'S2' -File $d.Rel -Remediation "title must be non-empty"
    }
    if ([int](Get-Prop $d.Json 'schemaVersion' -1) -ne 39) {
        Add-Failure -Rule 'S2' -File $d.Rel -Remediation "schemaVersion must be 39 (verified to provision unmodified on grafana 12.3.9 AND 13.1.1)"
    }
    if ([bool](Get-Prop $d.Json 'editable' $true) -ne $false) {
        Add-Failure -Rule 'S2' -File $d.Rel -Remediation 'editable must be false (pairs with the provider''s allowUiUpdates:false — DASH-03)'
    }
    foreach ($banned in @('id', '__inputs', '__requires')) {
        if (Test-HasProp $d.Json $banned) {
            Add-Failure -Rule 'S2' -File $d.Rel -Remediation "remove the top-level '$banned' property (id is DB-assigned and can collide; __inputs/__requires are the share/export format and break file provisioning)"
        }
    }

    # --- S3: DASH-04 — no hardcoded datasource UID anywhere in the raw text.
    Add-Rule 'S3'
    if ($d.Raw | Select-String -SimpleMatch 'skp-prometheus' -Quiet) {
        Add-Failure -Rule 'S3' -File $d.Rel -Remediation 'the literal skp-prometheus must not appear — reference the datasource only as ${datasource} (DASH-04); the stable uid is a property of the datasource YAML, not of the dashboard'
    }

    # --- S4: DASH-04 — every datasource reference is the object form bound to the variable.
    Add-Rule 'S4'
    $dsRefs = @()
    foreach ($p in @($d.Panels)) {
        if (Test-HasProp $p 'datasource') { $dsRefs += [pscustomobject]@{ Where = "panel '$(Get-Prop $p 'title' '(untitled)')'"; Ref = $p.datasource } }
        foreach ($t in @(Get-Prop $p 'targets' @())) {
            if (Test-HasProp $t 'datasource') { $dsRefs += [pscustomobject]@{ Where = "panel '$(Get-Prop $p 'title' '(untitled)')' target $(Get-Prop $t 'refId' '?')"; Ref = $t.datasource } }
        }
    }
    foreach ($v in @(Get-Prop (Get-Prop $d.Json 'templating' $null) 'list' @())) {
        if ([string](Get-Prop $v 'type' '') -eq 'query' -and (Test-HasProp $v 'datasource')) {
            $dsRefs += [pscustomobject]@{ Where = "template variable '$(Get-Prop $v 'name' '?')'"; Ref = $v.datasource }
        }
    }
    foreach ($r in $dsRefs) {
        $uid = [string](Get-Prop $r.Ref 'uid' '')
        $typ = [string](Get-Prop $r.Ref 'type' '')
        if ($uid -ne '${datasource}' -or $typ -ne 'prometheus') {
            Add-Failure -Rule 'S4' -File $d.Rel -Panel $r.Where `
                -Remediation 'datasource must be the object form {"type":"prometheus","uid":"${datasource}"} (DASH-04) — got type=''' + $typ + ''', uid=''' + $uid + ''''
        }
    }

    # --- S5: VAR-01/02/03 — exactly the three template variables, with the F-8 allValue pin.
    Add-Rule 'S5'
    $vars = @(Get-Prop (Get-Prop $d.Json 'templating' $null) 'list' @())
    $varNames = @($vars | ForEach-Object { [string](Get-Prop $_ 'name' '') })
    if (-not (Test-SetEqual $varNames @('datasource', 'source', 'pod'))) {
        Add-Failure -Rule 'S5' -File $d.Rel -Remediation "templating.list must carry exactly the three variables datasource, source, pod (got: $($varNames -join ', ')) — the names are load-bearing, every panel expression and this lint reference them"
    }

    $vDs     = @($vars | Where-Object { [string](Get-Prop $_ 'name' '') -eq 'datasource' }) | Select-Object -First 1
    $vSource = @($vars | Where-Object { [string](Get-Prop $_ 'name' '') -eq 'source' })     | Select-Object -First 1
    $vPod    = @($vars | Where-Object { [string](Get-Prop $_ 'name' '') -eq 'pod' })        | Select-Object -First 1

    if ($null -ne $vDs) {
        if ([string](Get-Prop $vDs 'type' '') -ne 'datasource') {
            Add-Failure -Rule 'S5' -File $d.Rel -Panel 'variable datasource' -Remediation "type must be 'datasource' (DASH-04)"
        }
        if ([string](Get-Prop $vDs 'query' '') -ne 'prometheus') {
            Add-Failure -Rule 'S5' -File $d.Rel -Panel 'variable datasource' -Remediation "query must be the plugin id 'prometheus', not a PromQL string (DASH-04)"
        }
    }

    # Shared query-variable shape. allValue MUST be null: research F-8 measured allValue ".*" at
    # 1044 series / 268 pods (PromQL label=~".*" also matches series where the label is ABSENT)
    # versus 159 / 51 for the explicit alternation Grafana builds from a null allValue (T-87-13).
    function Test-QueryVariable {
        param($Var, [string]$Label, [string]$Rel, [string[]]$MustContain, [string]$Req)
        if ($null -eq $Var) { return }
        if ([string](Get-Prop $Var 'type' '') -ne 'query') {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation "type must be 'query' ($Req)"
        }
        $q = [string](Get-Prop $Var 'query' '')
        if (-not $q.StartsWith('label_values(')) {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation "query must be the metric-scoped label_values(...) form ($Req) — the bare one-argument label_values(source) returns 15 days of unscoped history including the hermetic suite's console-test emitter (research F-8)"
        }
        foreach ($frag in $MustContain) {
            if (-not $q.Contains($frag)) {
                Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Expr $q -Remediation "query must contain '$frag' ($Req)"
            }
        }
        if ([string](Get-Prop $Var 'definition' '') -ne $q) {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation "definition must equal query ($Req)"
        }
        if ([bool](Get-Prop $Var 'multi' $false) -ne $true) {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation "multi must be true ($Req)"
        }
        if ([bool](Get-Prop $Var 'includeAll' $false) -ne $true) {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation "includeAll must be true ($Req)"
        }
        if (-not (Test-HasProp $Var 'allValue') -or $null -ne $Var.allValue) {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation 'allValue must be present and null (F-8 / T-87-13): a custom ".*" short-circuits getAllValue() and sweeps in 1044 series / 268 pods including series with no source label at all'
        }
        if ([int](Get-Prop $Var 'refresh' -1) -ne 2) {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation "refresh must be 2 (on time-range change) so the dropdown tracks the dashboard window ($Req)"
        }
        if ([int](Get-Prop $Var 'sort' -1) -ne 1) {
            Add-Failure -Rule 'S5' -File $Rel -Panel "variable $Label" -Remediation "sort must be 1 (alphabetical ascending) ($Req)"
        }
    }

    Test-QueryVariable -Var $vSource -Label 'source' -Rel $d.Rel -Req 'VAR-01' `
        -MustContain @('process_runtime_dotnet_gc_collections_count_total', ', source)')
    Test-QueryVariable -Var $vPod -Label 'pod' -Rel $d.Rel -Req 'VAR-02/VAR-03' `
        -MustContain @('source=~"$source"', ', service_instance_id)')

    # --- S6: panel types. The Angular `graph` panel was REMOVED in Grafana 11.
    Add-Rule 'S6'
    $allowedTypes = @('timeseries', 'stat', 'table', 'bargauge', 'row')
    foreach ($p in @($d.Panels)) {
        $pt = [string](Get-Prop $p 'type' '')
        if ($allowedTypes -notcontains $pt) {
            Add-Failure -Rule 'S6' -File $d.Rel -Panel ([string](Get-Prop $p 'title' '(untitled)')) `
                -Remediation "panel type '$pt' is not one of $($allowedTypes -join '/')"
        }
    }
    if ($d.Raw | Select-String -SimpleMatch '"type": "graph"' -Quiet) {
        Add-Failure -Rule 'S6' -File $d.Rel -Remediation 'the Angular "graph" panel was removed in Grafana 11 — use "timeseries"'
    }

    # --- S16 T-87-15: every non-row panel carries a non-empty `description`.
    # The description IS the operator-facing surface — it renders as the panel's info
    # tooltip and is the only place a reader learns what "normal" looks like and when to
    # act. RTD-02 already required one on the four substitution panels so a proxy metric
    # could never be mistaken for the real measurement; this rule generalises that duty to
    # every panel, because a bare panel silently pushes the same interpretation burden onto
    # the reader. Rows are exempt: a row is a layout divider with no data and no tooltip.
    # Numbering note: appended after S14/S15 per the append-only convention (plans
    # 87-02/03/04/05 already cite the earlier rule ids by number).
    Add-Rule 'S16'
    foreach ($p in @($d.Panels)) {
        if ([string](Get-Prop $p 'type' '') -eq 'row') { continue }
        if ([string]::IsNullOrWhiteSpace([string](Get-Prop $p 'description' ''))) {
            Add-Failure -Rule 'S16' -File $d.Rel -Panel ([string](Get-Prop $p 'title' '(untitled)')) `
                -Remediation 'add a description: what the panel shows, what healthy looks like, and when to investigate (RTD-02 generalised — the tooltip is the operator''s only in-context guidance)'
        }
    }

    # --- S7..S10, S15: per-target expression rules.
    Add-Rule 'S7'; Add-Rule 'S8'; Add-Rule 'S9'; Add-Rule 'S15'
    if ($d.Key -eq 'runtime') { Add-Rule 'S10' }
    foreach ($t in @($d.Targets)) {
        $e = $t.Expr
        if ([string]::IsNullOrWhiteSpace($e)) {
            Add-Failure -Rule 'S7' -File $t.File -Panel $t.PanelTitle -Expr '(empty)' `
                -Remediation "target $($t.RefId) has no expr"
            continue
        }

        # S7 — VAR-03: both variable filters on every expression.
        if (-not $e.Contains($FilterSource)) {
            Add-Failure -Rule 'S7' -File $t.File -Panel $t.PanelTitle -Expr $e `
                -Remediation 'add source=~"$source" to the selector (VAR-03)'
        }
        if (-not $e.Contains($FilterPod)) {
            Add-Failure -Rule 'S7' -File $t.File -Panel $t.PanelTitle -Expr $e `
                -Remediation 'add service_instance_id=~"$pod" to the selector (VAR-03)'
        }

        # S8 — RTD-01 / F-1. Runs on the RAW expr, independent of Get-MetricTokens, so it fires
        # whether or not the offending name carries a selector. `_` is a word character, so \b
        # does NOT match inside process_runtime_dotnet_*.
        if ($e -match '\bdotnet_') {
            Add-Failure -Rule 'S8' -File $t.File -Panel $t.PanelTitle -Expr $e `
                -Remediation 'process_runtime_dotnet_gc_collections_count_total, not dotnet_gc_collections_total (research F-1): the dotnet_* series in this Prometheus come from an unrelated MCP.Terminal host process, so a panel written against them renders blank'
        }

        $tok = Get-MetricTokens -Expr $e

        # S9 — every METRIC token is allowlisted (T-87-14). Grouping and filter labels are never
        # metric tokens and are never tested here (see the Get-MetricTokens block comment).
        foreach ($bad in @(Get-S9Violations -Metric $tok.Metric)) {
            Add-Failure -Rule 'S9' -File $t.File -Panel $t.PanelTitle -Expr $e `
                -Remediation "'$bad' is not in the live-verified metric allowlist (31 names). Fix the name, or — if it is genuinely a new live-verified metric — add it to the RUNTIME/DOMAIN/WEBAPI constant with evidence"
        }

        # S10 — the runtime dashboard is runtime-family only.
        if ($d.Key -eq 'runtime') {
            foreach ($m in @($tok.Metric)) {
                if ($RuntimeAllowlist -notcontains $m) {
                    Add-Failure -Rule 'S10' -File $t.File -Panel $t.PanelTitle -Expr $e `
                        -Remediation "'$m' is not a process_runtime_dotnet_* metric — runtime.json is runtime-family only (RTD-01); domain and WebApi counters belong on business.json"
                }
            }
        }

        # S15 — every BARE token is a KNOWN_LABEL. Counterweight to S9's position scoping.
        foreach ($bad in @(Get-S15Violations -Bare $tok.Bare)) {
            Add-Failure -Rule 'S15' -File $t.File -Panel $t.PanelTitle -Expr $e `
                -Remediation "$bad is either a metric name missing its {source=~`"`$source`", service_instance_id=~`"`$pod`"} selector (VAR-03) or a new grouping label that must be added to the KNOWN_LABELS constant"
        }
    }

    # --- S11: RTD-03 — runtime timeseries panels break down per pod.
    if ($d.Key -eq 'runtime') {
        Add-Rule 'S11'
        for ($i = 0; $i -lt @($d.Panels).Count; $i++) {
            $p = @($d.Panels)[$i]
            if ([string](Get-Prop $p 'type' '') -ne 'timeseries') { continue }   # stat panels are exempt (a per-class count is the point), but NOT from S7
            $rows = @($d.Targets | Where-Object { $_.PanelIndex -eq $i })
            $ok = $false
            foreach ($t in $rows) {
                foreach ($c in @(Get-ByClauses -Expr $t.Expr)) {
                    if ($c -match 'service_instance_id') { $ok = $true }
                }
            }
            if (-not $ok) {
                Add-Failure -Rule 'S11' -File $d.Rel -Panel ([string](Get-Prop $p 'title' '(untitled)')) `
                    -Remediation 'add service_instance_id to the by (...) clause so a single misbehaving replica is visually separable (RTD-03)'
            }
        }

        # --- S12: RTD-02 amendment — an empty panel titled "CPU" is a defect, not a substitution.
        Add-Rule 'S12'
        foreach ($p in @($d.Panels)) {
            $title = [string](Get-Prop $p 'title' '')
            if ($title -match '(?i)\b(cpu|uptime|working set)\b') {
                Add-Failure -Rule 'S12' -File $d.Rel -Panel $title `
                    -Remediation 'the instrumentation supplies no process CPU, uptime or working set (research F-2) — title the honest substitute ("GC committed memory", "GC time per second", "Process restarts in range", "Pods reporting"), never the axis it cannot measure (RTD-02)'
            }
        }
    }

    # --- S13: BPD-03 — the webapi is selected through $source like every other class.
    if ($d.Key -eq 'business') {
        Add-Rule 'S13'
        foreach ($t in @($d.Targets)) {
            if ($t.Expr.Contains('source="webapi"') -or $t.Expr.Contains('source=~"webapi"')) {
                Add-Failure -Rule 'S13' -File $t.File -Panel $t.PanelTitle -Expr $t.Expr `
                    -Remediation 'never pin the webapi with a literal source selector — select it through the shared $source variable like every other class (BPD-03/VAR-03)'
            }
        }
    }
}

# --- S14: T-87-08 — the whole aggregated stack still builds, offline.
Add-Rule 'S14'
$null = & kubectl kustomize (Join-Path $RepoRoot 'k8s') 2>&1
$kustExit = $LASTEXITCODE
if ($kustExit -ne 0) {
    Add-Failure -Rule 'S14' -File 'k8s/' `
        -Remediation "kubectl kustomize k8s/ exited $kustExit — the aggregated stack no longer builds; run it directly to see the parse error (T-87-08, the JSON-layer twin of the T-80-14 malformed-manifest guard)"
}

# ===========================================================================
# (3) COVERAGE section — skipped by -ShapeOnly, scoped by -Dashboard.
# ===========================================================================
if (-not $ShapeOnly) {

    $biz = @($docs | Where-Object { $_.Key -eq 'business' }) | Select-Object -First 1
    $rt  = @($docs | Where-Object { $_.Key -eq 'runtime'  }) | Select-Object -First 1

    if ($null -ne $biz) {
        $bizExprs = @($biz.Targets)

        # --- C1: BPD-01 — all nine domain counters referenced.
        Add-Rule 'C1'
        foreach ($m in $DomainAllowlist) {
            $hit = @($bizExprs | Where-Object { (Get-MetricTokens -Expr $_.Expr).Metric -contains $m })
            if ($hit.Count -eq 0) {
                Add-Failure -Rule 'C1' -File $biz.Rel `
                    -Remediation "domain counter '$m' is referenced by no panel (BPD-01 requires all nine)"
            }
        }

        # --- C2: BPD-02 — dedicated fault panels. "at least one", never "exactly one": the two
        #     keeper counters legitimately appear on TWO panels (the dedicated gap `stat` this rule
        #     matches, and the guarded conservation timeseries plan 87-04 Task 1 authors), so an
        #     exactly-one form would fail on a correct dashboard. The `stat` type qualifier is what
        #     disambiguates the dedicated fault panel from the conservation overlay.
        Add-Rule 'C2'
        $panelTokens = @()
        for ($i = 0; $i -lt @($biz.Panels).Count; $i++) {
            $rows = @($biz.Targets | Where-Object { $_.PanelIndex -eq $i })
            $tok = @()
            foreach ($r in $rows) { $tok += (Get-MetricTokens -Expr $r.Expr).Metric }
            $panelTokens += [pscustomobject]@{
                Index  = $i
                Title  = [string](Get-Prop (@($biz.Panels)[$i]) 'title' '(untitled)')
                Type   = [string](Get-Prop (@($biz.Panels)[$i]) 'type' '')
                Tokens = @($tok | Sort-Object -Unique)
            }
        }
        $c2Claims = @(
            [pscustomobject]@{ Set = @('orchestrator_step_unresolved_total'); Type = $null;  What = 'orchestrator_step_unresolved_total' }
            [pscustomobject]@{ Set = @('processor_spawn_dropped_total');      Type = $null;  What = 'processor_spawn_dropped_total' }
            [pscustomobject]@{ Set = @('keeper_messages_consumed_total', 'keeper_messages_sent_total'); Type = 'stat'; What = 'the keeper consumed-minus-sent gap' }
        )
        foreach ($c in $c2Claims) {
            $match = @($panelTokens | Where-Object {
                (Test-SetEqual $_.Tokens $c.Set) -and ($null -eq $c.Type -or $_.Type -eq $c.Type)
            })
            if ($match.Count -eq 0) {
                Add-Failure -Rule 'C2' -File $biz.Rel `
                    -Remediation "BPD-02 requires a DEDICATED panel$(if ($c.Type) { " of type $($c.Type)" }) whose targets reference exactly $($c.What) and nothing else"
            }
        }

        # --- C3: BPD-02 / VER-01 Class B guard. Token match, never a raw substring match, so
        #     `keeper_l2_probe_total` is never caught by the `keeper_` prefix. Deliberately
        #     UNSCOPED across the whole file: these four counters are exactly the set VER-01 places
        #     in Class B ("the fault indicators ... and any counter with no live series on a healthy
        #     stack"). Research F-4 measured zero live series for all four, and Pitfall 6 records
        #     that the two keeper counters are absent from the entire 15-day __name__ index because
        #     they increment only inside RecoveryConsumerBase.Consume / CountSent.
        Add-Rule 'C3'
        foreach ($t in $bizExprs) {
            $tok = (Get-MetricTokens -Expr $t.Expr).Metric
            $hits = @($tok | Where-Object { $ClassBCounters -contains $_ })
            if ($hits.Count -gt 0 -and -not $t.Expr.Contains($Guard)) {
                Add-Failure -Rule 'C3' -File $t.File -Panel $t.PanelTitle -Expr $t.Expr `
                    -Remediation "add 'or vector(0)' — $($hits -join ', ') has no live series on a healthy stack (F-4/Pitfall 6), so an unguarded expression renders 'No data' and a reviewer reads that as 'no faults' (VER-01 Class B, T-87-16)"
            }
        }

        # --- C4: BPD-03 — the ASP.NET Core histogram is actually used.
        Add-Rule 'C4'
        foreach ($m in @('http_server_request_duration_seconds_count', 'http_server_request_duration_seconds_bucket')) {
            $hit = @($bizExprs | Where-Object { (Get-MetricTokens -Expr $_.Expr).Metric -contains $m })
            if ($hit.Count -eq 0) {
                Add-Failure -Rule 'C4' -File $biz.Rel `
                    -Remediation "BPD-03 requires the WebApi row to reference '$m' (ASP.NET Core request metrics are the webapi's ONLY representation — it registers no domain meter)"
            }
        }

        # --- C5: VAR-04 / F-6 — processor panels discriminate on identityName. A group-by on
        #     service_name in a processor panel is itself a failure: every processor series carries
        #     the unresolved_0.0.0 MLBL-03 sentinel, so service_name cannot discriminate them.
        Add-Rule 'C5'
        $procMetrics = @('processor_messages_consumed_total', 'processor_messages_sent_total')
        $c5 = @($bizExprs | Where-Object {
            $tk = (Get-MetricTokens -Expr $_.Expr).Metric
            $shared = @($tk | Where-Object { $procMetrics -contains $_ })
            if ($shared.Count -eq 0) { return $false }
            $by = @(Get-ByClauses -Expr $_.Expr)
            return (@($by | Where-Object { $_ -match 'identityName' }).Count -gt 0)
        })
        if ($c5.Count -eq 0) {
            Add-Failure -Rule 'C5' -File $biz.Rel `
                -Remediation 'VAR-04 (corrected by research F-6): at least one processor_messages_* panel must group by identityName — service_name is degenerate for processors (every series carries the unresolved_0.0.0 MLBL-03 sentinel)'
        }

        # --- C7: VER-01 Class A protection — the EXACT INVERSE of C3. Because 87-05's classifier is
        #     purely mechanical, adding a guard to one of these silently demotes it to Class B, and a
        #     dead keeper or a fully stalled pipeline would then render `0` and PASS the live proof.
        Add-Rule 'C7'
        foreach ($t in $bizExprs) {
            $tok = (Get-MetricTokens -Expr $t.Expr).Metric
            $hits = @($tok | Where-Object { $ClassACounters -contains $_ })
            if ($hits.Count -gt 0 -and $t.Expr.Contains($Guard)) {
                Add-Failure -Rule 'C7' -File $t.File -Panel $t.PanelTitle -Expr $t.Expr `
                    -Remediation "remove the guard — this is a Class A signal that must return a real sample (VER-01); guarded: $($hits -join ', ') (T-87-20)"
            }
        }

        # --- C8: VER-01 Class B WebApi exception. Exactly one expression in the phase: plan 87-04's
        #     `WebApi 5xx ratio` stat. Class B without being one of C3's four domain counters, so it
        #     gets its own rule instead of an exception inside C3 or C7 — without C8 the WebApi
        #     tokens would sit in a third, lint-unenforced category.
        Add-Rule 'C8'
        foreach ($t in $bizExprs) {
            if ($t.Expr.Contains('http_response_status_code=~"5.."') -and -not $t.Expr.Contains($Guard)) {
                Add-Failure -Rule 'C8' -File $t.File -Panel $t.PanelTitle -Expr $t.Expr `
                    -Remediation 'keep the guard — the 5xx numerator has no series on a healthy stack, so an unguarded ratio returns no samples and fails VER-01 Class A (T-87-21)'
            }
        }
    }

    if ($null -ne $rt) {
        # --- C6: RTD-02 — the four honest substitute titles (F-2: no CPU, no uptime, no working set).
        Add-Rule 'C6'
        $titles = @(@($rt.Panels) | ForEach-Object { [string](Get-Prop $_ 'title' '') })
        foreach ($needed in @('GC committed memory', 'GC time per second', 'Process restarts in range', 'Pods reporting')) {
            if ($titles -notcontains $needed) {
                Add-Failure -Rule 'C6' -File $rt.Rel `
                    -Remediation "RTD-02 requires the honest substitute panel titled '$needed' (research F-2: the instrumentation supplies no process CPU / uptime / working set)"
            }
        }
    }
}

# ===========================================================================
# (4) Verdict.
# ===========================================================================
Write-Host ""
if ($script:Failures.Count -gt 0) {
    foreach ($f in $script:Failures) {
        Write-Host ("[{0}] {1}" -f $f.Rule, $f.File) -ForegroundColor Red
        if ($f.Panel) { Write-Host ("      panel: {0}" -f $f.Panel) -ForegroundColor Red }
        if ($f.Expr)  { Write-Host ("      expr : {0}" -f $f.Expr)  -ForegroundColor Red }
        Write-Host ("      fix  : {0}" -f $f.Remediation) -ForegroundColor Yellow
        Write-Host ""
    }
    Write-Host "DASHBOARD LINT FAILED — $($script:Failures.Count) failure(s) across $($docs.Count) file(s)." -ForegroundColor Red
    Write-Host "If a rule fails, fix the dashboard, never the rule: every allowlisted name and every" -ForegroundColor Yellow
    Write-Host "shape assertion above was verified LIVE against the running skp cluster (87-RESEARCH.md)." -ForegroundColor Yellow
    exit 1
}

$panelCount  = (@($docs) | ForEach-Object { @($_.Panels).Count }  | Measure-Object -Sum).Sum
$targetCount = (@($docs) | ForEach-Object { @($_.Targets).Count } | Measure-Object -Sum).Sum
if ($null -eq $panelCount)  { $panelCount = 0 }
if ($null -eq $targetCount) { $targetCount = 0 }

Write-Host "DASHBOARD LINT PASSED" -ForegroundColor Green
Write-Host ("  files   : {0} ({1})" -f $docs.Count, (@($docs | ForEach-Object { $_.Rel }) -join ', ')) -ForegroundColor Green
Write-Host ("  panels  : {0}" -f $panelCount)  -ForegroundColor Green
Write-Host ("  targets : {0}" -f $targetCount) -ForegroundColor Green
Write-Host ("  rules   : {0} ({1})" -f $script:RulesChecked.Count, (@($script:RulesChecked) -join ' ')) -ForegroundColor Green
exit 0
