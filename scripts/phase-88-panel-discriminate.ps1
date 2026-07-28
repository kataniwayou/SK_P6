<#
.SYNOPSIS
    Phase 88 panel-discrimination driver — the scenario table, the baseline capture, and the artifact
    writer for the rendered-panel proof. Plan 88-04 authors the frame and `-Mode Baseline` (BASE-01);
    plans 88-05..88-08 add the fault-driving modes.

.DESCRIPTION
    WHAT THIS SCRIPT IS FOR
        The roadmap's first success criterion is that "it moved" must be falsifiable. It is not
        falsifiable without a NULL HYPOTHESIS: a recorded healthy band, per panel, captured from a
        settled stack over absolute pinned windows of identical width at a fixed viewport, read from
        the RENDERED panel. `-Mode Baseline` captures exactly that and writes it to
        `analyzer-reports/phase-88-BASE-01.json`.

        The rendered panel is the VERDICT (locked constraint 2). Prometheus is queried through
        Grafana's own datasource proxy for DIAGNOSIS only, recorded beside each panel value with a
        `DiagnosticAgreesWithPanel` flag. Where the two disagree, the disagreement is a FINDING to
        record loudly — it is the direct control against the Phase-87 defect where 30/30 Class-A
        checks reported green against three panels that were rendering "No data".

    THE THREE STATISTICAL REGIMES (one rule cannot serve fourteen panels)
        A  jittering rate/level    1, 3, 5, 9, 10, 11, 12, 14
           band = mean +/- 3 sigma with a +/-10 % floor; moved = >= 2 consecutive samples outside.
        B  zero-floor event counter 2, 6, 7, 8, 13   (guarded by `or vector(0)`)
           band = EXACTLY 0..0. No statistics are needed and none are wanted: the null hypothesis is
           exactly zero and a SINGLE event falsifies it. This is the strongest discrimination the
           phase has, and widening the band by an epsilon would destroy it.
        C  monotonic range-cumulative stat  4   (`increase(...[$__range])`)
           Its value depends on the VISIBLE window width. At this phase's fixed 60 s sub-window the
           reading is a PER-MINUTE increase and is banded exactly like a Regime-A value. SEPARATELY a
           single wide (1 hour) capture is recorded as RangeCumulativeLevelBaseline — that is the
           number an operator actually sees on the shipped dashboard, it grows on a HEALTHY stack too,
           and it is recorded for the HAND-02 misleading-by-default list. It is NEVER scored as
           movement; applying a Regime-A band to it would be a guaranteed false positive.

    READER AMENDMENT THIS DRIVER DEPENDS ON (88-PROBE-DECISIONS.md Record 3)
        The wave-0 probe measured that legend VALUES parsed while legend NAMES bound to NOTHING, and
        that the `data-testid VizLegend series` recovery path matches ZERO elements on this render.
        `scripts/phase-88-panel-read.js` was amended before this driver was written, and the fix is
        re-provable hermetically at any time with:
            PANEL_READ_SELFTEST=1 node scripts/phase-88-panel-read.js
        Consequences honoured throughout this file: per-series reads are POSITIONAL (-SeriesIndex),
        the series NAME is recorded beside each band rather than used as a selector, and panel 2 is
        asserted NUMERICALLY first with the legend SUFFIX change (`consumed` -> `consumed keeper`) as
        the corroborating second signal. `innerText` normalises the trailing space away entirely, so
        the trailing-space formulation the research proposed does not exist and is never looked for.

    THE -ScenarioId PARAMETER SELECTS A ROW AND NOTHING ELSE (T-88-13)
        No tier, deployment name, env-var name, panel id, path or query is ever DERIVED from the
        parameter. `$Scenarios` is a static in-script [ordered] table; an unknown id, or an id whose
        recorded status is Dropped or Blocked, exits 64 with the reason. A dropped scenario stays
        VISIBLE as a row rather than being deleted, so the phase roll-up can show it as a row rather
        than as an absence.

    Flow (lettered; -Mode Baseline):

        GUARD    scenario resolution    unknown / Dropped / Blocked id -> 64, before anything else
        VALID    table self-validation  every `scenario` row must declare a non-empty cross-talk list
        PRE      precondition gate      docker-desktop context + shared stack forwards + all four
                                        tiers fully Ready at their LIVE-READ counts + ZERO seam vars
        STEP A   grafana forward        loopback-only; reuses an already-live tunnel rather than
                                        starting a second one it would not own
        STEP B   rest-state record      StackCleanAtStart + the pre-run replica/image maps
        STEP C   traffic drive          fan-out activation (204) + steady host HTTP load, held for
                                        the whole capture
        STEP D   rate interval          derived from the LIVE datasource timeInterval, never assumed
        STEP E   pinned capture         ONE batch: 14 panels x 10 abutting 60 s absolute windows
        STEP F   banding                per-regime bands, one entry per legend series
        STEP G   wide capture           the single 1-hour panel-4 range-cumulative level
        STEP H   diagnostic proxy       query_range beside each rendered value (DIAGNOSIS only)
        STEP J   artifact + verdict     write the JSON, THEN resolve the exit code from it
        STEP Z   teardown               disarm unconditionally, stop only a forward this script owns

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
        64  usage or precondition error (unknown/dropped scenario id, wrong kube context, shared
            forwards down, stack not at rest, a scenario row with an empty cross-talk list)
        65  the restore assertion failed (the stack was NOT left clean)
        66  a panel could not be READ AT ALL in baseline mode

.PARAMETER ScenarioId
    A key of the static $Scenarios table. It SELECTS a row; nothing is derived from it.

.PARAMETER Mode
    Optional confirmation of the selected row's own mode. When supplied it must MATCH the row (an
    id whose row says `baseline` cannot be run as a scenario), otherwise 64. Left empty, the row's
    own mode is used — the row, not the caller, is authoritative.

.PARAMETER SkipTraffic
    Capture without driving traffic. The Class-A panels cannot be banded honestly against an idle
    stack, so this DEGRADES the verdict to Inconclusive and exits 2. It exists for reader debugging,
    never for producing a baseline.

.PARAMETER RecordDroppedRow
    Record a DROPPED scenario's accepted-unproven ROW instead of running it. Without this switch a
    Dropped id still exits 64, exactly as plan 88-04 established — the switch does not weaken the
    guard, it names a DIFFERENT act. It drives NO fault, issues NO mutation, and refuses unless the
    wave-0 probe artifact still CONFIRMS the drop (the row names the probe field and the value that
    dropped it). Its output is a degraded row in the ordinary scenario schema: Verdict Inconclusive,
    Moved false, an observed PanelStateAfter, and an enumerated AcceptedUnprovenReason. A missing
    proof must be a ROW, never an absence — the plan 88-08 roll-up reads the file either way.

.NOTES
    Dev/ops-only tooling. No product source is touched and no manifest is edited.

    PORT-FORWARD OWNERSHIP (T-88-05) — this script starts its OWN grafana forward on loopback port
    3000 and keeps the PID in an IN-MEMORY VARIABLE ONLY. It never creates, reads or writes the
    shared forward PID registry that scripts/phase-80-up.ps1 owns: that file holds the eight shared
    harness tunnels, so reading it for teardown would kill all eight and break a concurrent sweep.
    Teardown stops exactly one PID, behind a recycled-PID guard, and ONLY when this script was the
    process that started it. The forward binds --address 127.0.0.1 explicitly so the bridged host
    port stays loopback-only (T-88-04).

    NO STATIC REPLICA MAP (T-88-02). Every restore target is READ from the live Deployment
    immediately before the mutation and re-read afterwards, inside phase-88-cluster-ops.ps1. The
    phase-80 static map is stale — it records the orchestrator at 1, and Phase 83 took it to 3 for
    HA-06 — so copying it would quietly amputate the HA tier. It is asserted absent from this file.

    NO MANIFEST RE-APPLY (T-88-10). A kustomize re-apply would revert the four app Deployments to
    their manifest image pins, whose bits emit no `source` resource attribute, making every correct
    dashboard look broken. Asserted absent.

    admin/admin is the deliberate dev posture set in k8s/23-grafana.yaml and is reachable only over
    loopback; auth hardening is out of scope for this milestone, so the credential is inline rather
    than parameterised — parameterising it would imply a security property this deployment does not
    have.

    Prometheus retention on this stack was MEASURED at 225 h (~9.4 days) by the wave-0 probe
    (`OldestSampleUtc: 2026-07-19T07:38:06Z`, `RetentionHorizonLimited: false`). The ~12 h figure that
    87-FINDINGS.md §14 recorded and 88-RESEARCH.md propagated is wrong by roughly nineteen times. The
    discipline of capturing evidence INTO the artifact during the run stays — it is right for
    reproducibility regardless — but no window here is compressed and no settle is skipped on the
    belief that data is about to disappear.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScenarioId,
    [ValidateSet('', 'Baseline', 'Scenario', 'DurationLadder')][string]$Mode = '',
    [switch]$SkipTraffic,
    [switch]$RecordDroppedRow
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Declared BEFORE the try so the outer finally can read them under StrictMode even when the guard or
# the precondition gate exits before the step that would have set them ever runs.
$gfForwardPid    = 0
$gfForwardOwned  = $false
$seamArmed       = $false
$seamTier        = ''
$seamVar         = ''
# ZERO-03 arms TWO seams (PROCESSOR_DEFEAT_READ to CREATE the recovery event, KEEPER_DEFEAT_REINJECT
# to SUPPRESS the reinject), so the teardown needs a second slot. A single-slot teardown would leave
# the trigger seam armed on an interrupted run — the exact poisoning Pitfall 5 describes.
$seamArmed2      = $false
$seamTier2       = ''
$seamVar2        = ''
$scaledTier      = ''
$replicasBefore  = -1
$probeWorkflowId = ''
$trafficJob      = $null
$loadJobs        = @()

Push-Location $repoRoot
try {

    # -----------------------------------------------------------------------------------------
    # prefixed console-trace helper (Cyan step banner / Gray sub-detail / Yellow warning /
    # Red fatal-before-exit / Green success — the repo-wide colour convention).
    # -----------------------------------------------------------------------------------------
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-88-panel-discriminate] $msg" -ForegroundColor $color
    }

    # -----------------------------------------------------------------------------------------
    # Dot-source the three shared libraries. exit-code-resolution.ps1 owns the verdict -> exit
    # mapping: Resolve-AnalyzerExitCode is NEVER reimplemented as an inline verdict switch here,
    # because its fail-closed default (unknown/absent verdict -> 1) is the T-87-18 control.
    # phase-88-panel-read.ps1 owns the browser read and the banding; phase-88-cluster-ops.ps1 owns
    # every cluster mutation and the restore claim set.
    # -----------------------------------------------------------------------------------------
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')
    . (Join-Path $PSScriptRoot 'lib/phase-88-panel-read.ps1')
    . (Join-Path $PSScriptRoot 'lib/phase-88-cluster-ops.ps1')

    $gf       = 'http://127.0.0.1:3000'
    $proxy    = "$gf/api/datasources/proxy/uid/skp-prometheus"
    $adminB64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('admin:admin'))
    $auth     = @{ Authorization = "Basic $adminB64" }
    $api      = 'http://localhost:8080'

    # Pinned viewport for every browser read in this phase. maxDataPoints derives from the panel's
    # PIXEL WIDTH, which sets the query step, which sets $__rate_interval — so an unpinned viewport
    # would make two captures incomparable even with identical time windows. 1920x1080 is the value
    # the wave-0 probe recorded, and every Phase-88 artifact states the same pair.
    $ViewportWidth  = 1920
    $ViewportHeight = 1080

    # LOCKED BY PQ-02 (`LocatorModeChosen: viewpanel`, 20/20 pairs read, State Ok). No plan in this
    # phase passes PANEL_TITLES; the header-walk mode stays an implemented fallback this measurement
    # did not need. The verbatim titles live in $Panels anyway, so a future regression in the
    # `?viewPanel=panel-<id>` URL costs a config change rather than a rediscovery.
    $LocatorMode = 'viewpanel'

    # STATIC tier list, in the restore-claim order every Phase-88 artifact uses.
    $Tiers = @('baseapi-service', 'keeper', 'orchestrator', 'processor-sample')

    $screenshotRoot = Join-Path $repoRoot 'analyzer-reports/phase-88-screenshots'
    $reportDir      = Join-Path $repoRoot 'analyzer-reports'

    # =========================================================================================
    # THE FOURTEEN BUSINESS PANELS — static, keyed by panel id.
    #
    # Titles are copied BYTE-FOR-BYTE from k8s/dashboards/business.json. That is not fussiness: the
    # header-walk locator finds a panel by its rendered header text, so a title that drifts from the
    # dashboard would fail to locate the panel SILENTLY rather than loudly.
    #
    # Guarded = the target expression ends in `or vector(0)`, so the panel renders a comfortable 0
    # instead of "No data" when its counter has never been emitted. That is exactly why a guarded
    # green is NOT evidence of health, and it is the property the HAND-02 misleading-by-default list
    # exists to record. Panel 9 is deliberately UNGUARDED so a dead keeper renders "No data".
    # =========================================================================================
    $Panels = [ordered]@{
        '1'  = @{ Title = 'Orchestrator consumed vs sent (conservation)';                     Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = 'consumed {{source}} / sent {{source}}';                                                   Notes = 'two series per source label' }
        '2'  = @{ Title = 'Keeper consumed vs sent (conservation)';                           Type = 'timeseries'; Regime = 'B'; Guarded = $true;  SeriesName = 'consumed {{source}} / sent {{source}}';                                                   Notes = 'guard renders the label-less series as the bare word `consumed`; the suffix change to `consumed keeper` is the corroborating signal' }
        '3'  = @{ Title = 'Processor consumed vs sent by image (conservation)';               Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = 'consumed {{identityName}} / sent {{identityName}}';                                       Notes = 'two series per processor image' }
        '4'  = @{ Title = 'Orchestrator sends minus processor pickups (structural - read the trend)'; Type = 'stat'; Regime = 'C'; Guarded = $false; SeriesName = 'gap';                                                                                  Notes = 'increase(...[$__range]) — banded on its per-minute value; the wide-window level is recorded separately and NEVER scored' }
        '5'  = @{ Title = 'Per-processor dispatch gap';                                       Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = '{{processorId}}';                                                                        Notes = 'one series per processor id' }
        '6'  = @{ Title = 'Orchestrator unresolved steps in range';                           Type = 'stat';       Regime = 'B'; Guarded = $true;  SeriesName = 'unresolved';                                                                             Notes = 'ZERO-01 dropped (PQ-05) — accepted-unproven, HAND-04' }
        '7'  = @{ Title = 'Processor dropped spawns in range';                                Type = 'stat';       Regime = 'B'; Guarded = $true;  SeriesName = 'dropped spawns';                                                                         Notes = 'accepted unproven, user-locked — no scenario drives it' }
        '8'  = @{ Title = 'Keeper consumed - sent gap in range';                              Type = 'stat';       Regime = 'B'; Guarded = $true;  SeriesName = 'gap';                                                                                    Notes = 'ZERO-03 subject' }
        '9'  = @{ Title = 'Keeper L2 probe heartbeat';                                        Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = '{{service_instance_id}}';                                                                Notes = 'UNGUARDED BY DESIGN — a dead keeper renders "No data", which IS the discrimination signal' }
        '10' = @{ Title = 'WebApi request rate by route';                                     Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = '{{http_route}}';                                                                         Notes = 'one series per route' }
        '11' = @{ Title = 'WebApi p95 request duration by route';                             Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = '{{http_route}}';                                                                         Notes = 'histogram_quantile — partially gauge-like' }
        '12' = @{ Title = 'WebApi status-code mix';                                           Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = '{{http_response_status_code}}';                                                          Notes = 'one series per status code' }
        '13' = @{ Title = 'WebApi 5xx ratio';                                                 Type = 'stat';       Regime = 'B'; Guarded = $true;  SeriesName = '5xx ratio';                                                                              Notes = 'WEB-02 dropped (PQ-03) — accepted-unproven, HAND-04' }
        '14' = @{ Title = 'WebApi in-flight requests and Kestrel connections';                Type = 'timeseries'; Regime = 'A'; Guarded = $false; SeriesName = 'in-flight {{sid}} / kestrel active {{sid}} / kestrel queued {{sid}}';                    Notes = 'GAUGES — a condition that starts and ends between two exports is never recorded' }
    }

    # =========================================================================================
    # PREDICTED-DIRECTION VOCABULARY. Recorded here so the scoring plans (88-05..88-08) map each
    # token the same way rather than each re-deciding what "up-then-nodata" means.
    #   up            Test-PanelMoved -Direction up        value above Band.High
    #   down          Test-PanelMoved -Direction down      value below Band.Low
    #   nonzero       Test-PanelMoved -Direction nonzero   Regime B's form: a 0..0 band, so ANY event
    #   nodata        Test-PanelMoved -Direction nodata    >= 2 consecutive AfterStates of 'NoData'
    #   slope-up      Regime C: the PER-MINUTE band is asserted 'up'; the wide-window level is NEVER
    #                 scored, only recorded for the HAND-02 list
    #   up-then-nodata  two assertions in sequence over one after-capture: 'up' across the early
    #                 sub-windows, then 'nodata' once the series expires. Panel 5 only.
    #   n/a           no scenario drives this panel; it goes to the HAND-04 register with its reason
    # =========================================================================================
    $DirectionVocabulary = @('up', 'down', 'nonzero', 'nodata', 'slope-up', 'up-then-nodata', 'n/a')

    # =========================================================================================
    # THE STATIC SCENARIO TABLE — every scenario this phase will ever run, authored once, extended
    # by no one. Levers marked [PQ-nn] are set from the value RECORDED in 88-PROBE-DECISIONS.md, not
    # assumed; each carries the probe field that decided it.
    #
    # A Dropped scenario stays in the table with its reason. Deleting it would make the phase roll-up
    # show an ABSENCE where it should show a row — and an absence is exactly what the HAND-04
    # accepted-unproven register exists to prevent.
    # =========================================================================================
    $Scenarios = [ordered]@{
        'BASE-01' = @{
            mode = 'baseline'; lever = 'none'; targetTier = ''; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 0
            panelIds = @('1','2','3','4','5','6','7','8','9','10','11','12','13','14')
            predictedDirection = @{}
            crossTalkPanels = @()          # baseline mode has no fault, so cross-talk is not defined
            requiresRebaseline = $false; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-02 LocatorModeChosen=viewpanel + PQ-04 RateIntervalPinned=true'
            notes = 'the DISC-01 null hypothesis every later scenario is tested against'
        }
        'WEB-01' = @{
            mode = 'scenario'; lever = 'http'; targetTier = ''; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 180
            panelIds = @('10','11','12','14')
            predictedDirection = @{ '10' = 'up'; '11' = 'up'; '12' = 'up'; '14' = 'up' }
            crossTalkPanels = @('1','3','9')
            requiresRebaseline = $false; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-03 ProbedEndpoints — GET /api/v1/__phase88_probe_unmatched -> 404 and POST /api/v1/orchestration/start [] -> 400 both observed'
            notes = 'sustained host HTTP load >= 150 s, driving the two CONFIRMED inert status codes rather than inventing new ones'
        }
        'WEB-02' = @{
            mode = 'scenario'; lever = 'http'; targetTier = ''; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 120
            panelIds = @('13')
            predictedDirection = @{ '13' = 'nonzero' }
            crossTalkPanels = @('1','3','9','10')
            requiresRebaseline = $false; status = 'Dropped'
            statusReason = 'no endpoint returns 5xx on safe input; the dependency-outage route costs a WebApi pod restart under the Phase-86 hard readiness latch and is out of budget'
            # -RecordDroppedRow re-checks the drop against the probe artifact before recording it, so
            # the accepted-unproven row can never outlive the measurement that justified it. Both
            # names are STATIC table strings — nothing is derived from the -ScenarioId parameter.
            droppedProbeField = 'Safe5xxFound'; droppedProbeExpect = 'False'
            decidedBy = 'PQ-03 Safe5xxFound=false over seven probed endpoints (five 404, one 400, one 422)'
            notes = 'panel 13 goes to the HAND-04 accepted-unproven register; the redis-outage route was deliberately NOT substituted'
        }
        'SCALE-01' = @{
            mode = 'scenario'; lever = 'scale'; targetTier = 'processor-sample'; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 420
            panelIds = @('3','4','5')
            predictedDirection = @{ '3' = 'down'; '4' = 'slope-up'; '5' = 'up-then-nodata' }
            crossTalkPanels = @('9','10','12','14')
            requiresRebaseline = $true; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-01 scale fault Ok=true, ReplicasRestored=true'
            notes = 'restore is to the LIVE-READ count, never a tabled one'
        }
        'SCALE-02' = @{
            mode = 'scenario'; lever = 'scale'; targetTier = 'orchestrator'; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 240
            panelIds = @('1')
            predictedDirection = @{ '1' = 'down' }
            crossTalkPanels = @('9','10','12')
            requiresRebaseline = $true; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-01 sequencer proven live; ReplicasBefore recorded the orchestrator at 3'
            notes = 'the HA tier is 3 replicas — the stale phase-80 map says 1, which is why no map is used'
        }
        'SCALE-03' = @{
            # DWELL MEASURED, NOT ASSUMED (88-06 deviation). Plan 88-06 tables 240 s. A read-only
            # query against the wave-0 probe's OWN recorded rollout (keeper-99b8c574b-29mzq, whose
            # last real sample was 16:47:30Z) measured that `rate(counter[240s])` for a dead pod
            # survives roughly 180 s past that last sample — the 240 s lookback keeps producing a
            # (decaying) value until fewer than two samples remain inside it. At a 240 s dwell the
            # panel would therefore still be RENDERING for most of the outage and could not produce
            # the TWO CONSECUTIVE NoData sub-windows the plan's own acceptance criterion requires.
            # 480 s leaves ~4 clean NoData sub-windows at the end of the after window.
            mode = 'scenario'; lever = 'scale'; targetTier = 'keeper'; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 480
            panelIds = @('9')
            predictedDirection = @{ '9' = 'nodata' }
            crossTalkPanels = @('1','3','10','12')
            # OBSERVED, never scored and never a control: a keeper that is not running consumes
            # nothing, so panels 2 and 8 cannot open a gap under THIS fault. The claim is measured
            # here rather than asserted, so a later reader cannot conclude the scenario should have
            # moved them (that is what the ZERO-03 seam scenario in 88-07 exists for).
            observePanels = @('2','8')
            humanSummarySuffix = 'panels 2 and 8 remained at 0 throughout this outage, and that is CORRECT rather than a missed signal: a keeper scaled to zero consumes nothing, so no keeper consumed-minus-sent gap can open from a keeper outage. Panel 8 needs the ZERO-03 seam scenario (plan 88-07), which arms PROCESSOR_DEFEAT_READ to CREATE a recovery event and KEEPER_DEFEAT_REINJECT to suppress the reinject.'
            requiresRebaseline = $true; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-02 TimeseriesLegendParsed=true (panel 9 was the PQ-02 subject)'
            notes = 'panel 9 is unguarded, so the predicted direction is NoData rather than a value change'
        }
        'ZERO-01' = @{
            mode = 'scenario'; lever = 'data'; targetTier = ''; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 120
            panelIds = @('6')
            predictedDirection = @{ '6' = 'nonzero' }
            crossTalkPanels = @('9','10','12','13')
            requiresRebaseline = $false; status = 'Dropped'
            statusReason = 'neither route reaches an orchestrator_step_unresolved increment: the API dangling edge is refused 422 at step-create, and the L2 step key is rewritten from Postgres by any stop/start cycle'
            droppedProbeField = 'UnresolvedRouteChosen'; droppedProbeExpect = 'none'
            decidedBy = 'PQ-05 UnresolvedRouteChosen=none (danglingEdge 422 @ step-create, l2StepKeyViable=false)'
            notes = 'panel 6 goes to the HAND-04 register; the counter is unreachable from outside src/ and the locked constraint forbids reaching inside it'
        }
        'ZERO-02' = @{
            # [PQ-01] The lever is `seam`, NOT `scale`. The roadmap preferred driving a panel by
            # system state alone; that preference does not survive contact with this cluster, where a
            # real 90 s processor-tier crash produced ZERO keeper recovery series over a 30-minute
            # range query covering the whole fault.
            mode = 'scenario'; lever = 'seam'; targetTier = ''
            seamTier = 'processor-sample'; seamVar = 'PROCESSOR_DEFEAT_READ'
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 90
            panelIds = @('2')
            predictedDirection = @{ '2' = 'nonzero' }
            crossTalkPanels = @('9','10','12')
            requiresRebaseline = $true; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-01 KeeperRecoveryTrafficObserved=false, KeeperConsumedSeriesCount=0'
            notes = 'KEEPER_DEFEAT_REINJECT is left UNSET so the keeper consumes AND reinjects: consumed and sent both move and the panel-8 gap stays 0. Arm -> rollout -> settle >= 150 s -> RE-BASELINE -> trigger (DISC-06), so this row costs two rollouts, not none.'
        }
        'ZERO-03' = @{
            # [PQ-06] The seam MECHANISM is proven to land and clear. What that does NOT license: it
            # does not prove the deployed keeper image HONOURS the variable — only a real recovery
            # event can show that, and PQ-01 established this cluster produces no recovery traffic
            # without a trigger seam. So BOTH seams are armed: PROCESSOR_DEFEAT_READ to CREATE the
            # recovery event, KEEPER_DEFEAT_REINJECT to SUPPRESS the reinject. A flat panel 8 is then
            # an open question about the image, NOT a proof that the gap cannot open. Research
            # assumption A6 remains unretired.
            mode = 'scenario'; lever = 'seam'; targetTier = ''
            seamTier = 'keeper'; seamVar = 'KEEPER_DEFEAT_REINJECT'
            triggerSeamTier = 'processor-sample'; triggerSeamVar = 'PROCESSOR_DEFEAT_READ'
            dwellSeconds = 90
            panelIds = @('8')
            predictedDirection = @{ '8' = 'nonzero' }
            crossTalkPanels = @('6','7','13','10','12')
            requiresRebaseline = $true; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-06 SeamArmLanded=true + SeamDisarmClean=true; both seams required per Record 7'
            notes = 'arm both -> rollout -> settle >= 150 s -> RE-BASELINE -> trigger -> capture -> restore -> disarm -> assert'
        }
        'LADDER-01' = @{
            mode = 'ladder'; lever = 'scale+http'; targetTier = 'processor-sample'; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 0
            panelIds = @('3','4','14')
            predictedDirection = @{ '3' = 'down'; '4' = 'slope-up'; '14' = 'up' }
            crossTalkPanels = @('9','12')
            requiresRebaseline = $true; status = 'Locked'; statusReason = ''
            decidedBy = 'PQ-04 RateIntervalPinned=true + PQ-01 sequencer proven'
            notes = 'the DISC-07 minimum-detectable-duration ladder. It is TWO answers, not one: counters smear (never lost) while gauges are sampled (a condition entirely between two exports is never recorded), so panel 14 rungs are scored separately from 3 and 4.'
        }
    }

    # =========================================================================================
    # GUARD — the id SELECTS a row and nothing else (T-88-13). Runs before ANY cluster access, so a
    # bad id costs nothing and touches nothing.
    # =========================================================================================
    if (-not $Scenarios.Contains($ScenarioId)) {
        Write-Phase "unknown scenario '$ScenarioId'." 'Red'
        Write-Phase "Known scenarios: $($Scenarios.Keys -join ', ')" 'Yellow'
        exit 64
    }
    $scenario = $Scenarios[$ScenarioId]

    # The TABLE'S OWN key is what reaches a file path or an artifact field — never the caller's
    # string. An [ordered] hashtable matches keys case-INSENSITIVELY, so `-ScenarioId web-01` would
    # otherwise select the WEB-01 row and then write `phase-88-web-01.json`, an artifact the roll-up
    # would never find. (T-88-13: the id selects a row and nothing else.)
    $canonicalId = @($Scenarios.Keys | Where-Object {
            [string]::Equals("$_", $ScenarioId, [System.StringComparison]::OrdinalIgnoreCase)
        })[0]

    $recordingDroppedRow = $false
    if ($scenario.status -ne 'Locked') {
        if ($RecordDroppedRow -and "$($scenario.status)" -eq 'Dropped' -and "$($scenario.mode)" -eq 'scenario') {
            # NOT a weakening of the guard — a DIFFERENT act. Nothing is driven, nothing is mutated,
            # and the drop is re-checked against the probe artifact before anything is written.
            $recordingDroppedRow = $true
            Write-Phase "scenario '$canonicalId' is Dropped — recording its ACCEPTED-UNPROVEN row (no fault will be driven)." 'Yellow'
            Write-Phase "REASON: $($scenario.statusReason)" 'Yellow'
            Write-Phase "DECIDED BY: $($scenario.decidedBy)" 'Yellow'
        }
        else {
            Write-Phase "scenario '$canonicalId' is $($scenario.status) and will not be run." 'Red'
            Write-Phase "REASON: $($scenario.statusReason)" 'Yellow'
            Write-Phase "DECIDED BY: $($scenario.decidedBy)" 'Yellow'
            Write-Phase "It remains a ROW in the table on purpose — the roll-up must show it as a dropped row, not as an absence." 'Yellow'
            Write-Phase "To RECORD that row as accepted-unproven (no fault driven), re-run with -RecordDroppedRow." 'Yellow'
            exit 64
        }
    }

    # -Mode is a CONFIRMATION, never a selector. The row is authoritative about what it is; a caller
    # that believes otherwise is corrected rather than obeyed.
    $rowMode = "$($scenario.mode)"
    $modeAlias = @{ 'baseline' = 'Baseline'; 'scenario' = 'Scenario'; 'ladder' = 'DurationLadder' }
    $expectedMode = $modeAlias[$rowMode]
    if (-not [string]::IsNullOrWhiteSpace($Mode) -and $Mode -ne $expectedMode) {
        Write-Phase "scenario '$ScenarioId' is a '$rowMode' row (-Mode $expectedMode); -Mode $Mode was requested." 'Red'
        Write-Phase "The row decides its own mode; the parameter may only confirm it." 'Yellow'
        exit 64
    }

    # =========================================================================================
    # VALID — TABLE SELF-VALIDATION (DISC-03). A scenario with an EMPTY cross-talk list is a defect,
    # not an omission: without a pre-declared list of panels that must NOT move, "the panel moved" is
    # consistent with "everything moved", and the fault's blast radius is unmeasured. The list must be
    # declared BEFORE the run, which is why it is validated at startup rather than at scoring time.
    #
    # Every predicted direction is also checked against the fixed vocabulary, so a typo in a later
    # edit ('nodta') fails here rather than silently scoring as "did not move".
    # =========================================================================================
    $tableErrors = @()
    foreach ($sid in $Scenarios.Keys) {
        $row = $Scenarios[$sid]
        if ("$($row.mode)" -eq 'scenario' -and @($row.crossTalkPanels).Count -eq 0) {
            $tableErrors += "scenario row '$sid' declares an EMPTY crossTalkPanels list (DISC-03 requires a non-empty, pre-declared list)"
        }
        foreach ($pid_ in @($row.panelIds)) {
            if (-not $Panels.Contains("$pid_")) {
                $tableErrors += "scenario row '$sid' names panel '$pid_', which is not one of the fourteen business panels"
            }
        }
        foreach ($pid_ in @($row.crossTalkPanels)) {
            if (-not $Panels.Contains("$pid_")) {
                $tableErrors += "scenario row '$sid' names cross-talk panel '$pid_', which is not one of the fourteen business panels"
            }
            if (@($row.panelIds) -contains "$pid_") {
                $tableErrors += "scenario row '$sid' lists panel '$pid_' as BOTH a subject and a cross-talk control"
            }
        }
        foreach ($k in @($row.predictedDirection.Keys)) {
            $dir = "$($row.predictedDirection[$k])"
            if ($DirectionVocabulary -notcontains $dir) {
                $tableErrors += "scenario row '$sid' predicts direction '$dir' for panel '$k', which is not in the fixed vocabulary ($($DirectionVocabulary -join ', '))"
            }
        }
    }
    if (@($tableErrors).Count -gt 0) {
        Write-Phase "the static scenario table is INVALID — refusing to run:" 'Red'
        foreach ($e in $tableErrors) { Write-Phase "  $e" 'Red' }
        exit 64
    }

    Write-Phase "scenario '$ScenarioId' ($rowMode, lever=$($scenario.lever)) — $($scenario.notes)"
    Write-Phase "  decided by: $($scenario.decidedBy)" 'Gray'
    Write-Phase "  panels [$(@($scenario.panelIds) -join ',')] cross-talk [$(@($scenario.crossTalkPanels) -join ',')]" 'Gray'

    # =========================================================================================
    # HELPERS
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
    # loopback 3000. Starting a second one would bind-fail and exit immediately, the health poll would
    # pass anyway THROUGH THE STRAY, and teardown would then either be a silent no-op or act on a
    # recycled PID. A forward found already running belongs to someone else and is left alone.
    function Test-GrafanaAlreadyUp {
        try {
            $h = Invoke-RestMethod -Uri "$gf/api/health" -TimeoutSec 4 -ErrorAction Stop
            return ("$($h.database)" -eq 'ok')
        } catch { return $false }
    }

    # RANGE query through GRAFANA'S OWN datasource proxy — the DIAGNOSIS channel the locked constraint
    # permits. It is NEVER the verdict for a panel. Where it disagrees with the rendered value, the
    # disagreement is recorded as a finding rather than reconciled away.
    function Invoke-ProxyRangeQuery([string]$Query, [long]$StartUnix, [long]$EndUnix, [int]$StepSeconds) {
        return Invoke-RestMethod -Method Post -Uri "$proxy/api/v1/query_range" `
                 -Headers $auth -ContentType 'application/x-www-form-urlencoded' `
                 -Body @{ query = $Query; start = $StartUnix; end = $EndUnix; step = $StepSeconds } `
                 -TimeoutSec 90 -ErrorAction Stop
    }

    # Reproduce Grafana's own arithmetic rather than assuming a number.
    #   $__interval      floors at the datasource's timeInterval (its "minimum interval")
    #   $__rate_interval = max(4 x timeInterval, $__interval + timeInterval)
    # Nothing here is hardcoded: the timeInterval is READ from the live datasource, so this tracks the
    # manifest instead of drifting from it. PQ-04 measured the same 240 s for a 10-minute and a 2-hour
    # window at this viewport, but the value is still DERIVED and whatever is derived is recorded.
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
    # maxDataPoints defaults to the panel's pixel width. In single-panel (kiosk) view the panel spans
    # the viewport, so the pinned viewport width IS the maxDataPoints estimate.
    function Get-QueryStepSeconds([int]$RangeSeconds, [int]$TimeIntervalSeconds, [int]$MaxDataPoints) {
        if ($MaxDataPoints -le 0) { return $TimeIntervalSeconds }
        $raw = [int][Math]::Ceiling($RangeSeconds / [double]$MaxDataPoints)
        return [Math]::Max($TimeIntervalSeconds, $raw)
    }

    # ---- one place that issues an HTTP call and returns the status as DATA. -SkipHttpErrorCheck
    # ---- keeps a 4xx/5xx a RESULT rather than an exception.
    function Invoke-DriverApi {
        param(
            [Parameter(Mandatory)][ValidateSet('GET', 'POST', 'PUT', 'DELETE')][string]$Method,
            [Parameter(Mandatory)][string]$Path,
            [string]$Body = $null
        )
        $out = @{ Method = $Method; Path = $Path; Status = 0; Body = ''; Json = $null; Error = '' }
        try {
            $req = @{
                Method             = $Method
                Uri                = "$api$Path"
                TimeoutSec         = 30
                SkipHttpErrorCheck = $true
                UseBasicParsing    = $true
                ErrorAction        = 'Stop'
            }
            if ($null -ne $Body) { $req['Body'] = $Body; $req['ContentType'] = 'application/json' }
            $resp = Invoke-WebRequest @req
            $out.Status = [int]$resp.StatusCode
            $out.Body = "$($resp.Content)"
            if (-not [string]::IsNullOrWhiteSpace($out.Body)) {
                try { $out.Json = ($out.Body | ConvertFrom-Json) } catch { $out.Json = $null }
            }
        } catch {
            $out.Status = -1
            $out.Error = "$($_.Exception.Message)"
        }
        return $out
    }

    # The proxy AGGREGATE over a window: every series summed per timestamp, then averaged over the
    # timestamps. DIAGNOSIS ONLY — it is recorded beside the rendered value with an agreement flag and
    # is never the verdict. Defined here (rather than beside its first use) because BOTH the baseline
    # mode and the scenario engine cross-check with it and PowerShell only sees a function AFTER its
    # definition statement has run.
    #
    # NOTE ON THE PARAMETER NAMES — they are deliberately long, and a single-letter name here is a
    # BUG, not a style choice. PowerShell variable names are case-INSENSITIVE, so a `[long]$S`
    # parameter and a `foreach ($s in ...)` loop variable are the SAME variable; the parameter's type
    # constraint is then re-enforced on the loop assignment and every call throws
    # "Cannot convert @{metric=; values=System.Object-array} ... to System.Int64". That is exactly how
    # the first BASE-01 run lost its entire diagnostic cross-check. (Same trap as 88-03 deviation 5,
    # in a form where the type constraint makes it fail loudly instead of silently.)
    function Get-ProxyAggregateMean([string]$PromQuery, [long]$StartUnix, [long]$EndUnix, [int]$StepSeconds) {
        $res = Invoke-ProxyRangeQuery -Query $PromQuery -StartUnix $StartUnix -EndUnix $EndUnix -StepSeconds $StepSeconds
        $byTs = @{}
        foreach ($series in @($res.data.result)) {
            $sn = @(Get-PropertyNames $series)
            if ($sn -notcontains 'values') { continue }
            foreach ($v in @($series.values)) {
                $ts = "$($v[0])"
                $raw = "$($v[1])"
                $val = 0.0
                if (-not [double]::TryParse($raw, [ref]$val)) { continue }
                if (-not [double]::IsFinite($val)) { continue }   # histogram_quantile yields NaN on empty buckets
                if ($byTs.ContainsKey($ts)) { $byTs[$ts] += $val } else { $byTs[$ts] = $val }
            }
        }
        if ($byTs.Count -eq 0) { return $null }
        $sum = 0.0
        foreach ($k in $byTs.Keys) { $sum += $byTs[$k] }
        return ($sum / $byTs.Count)
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
    # armed by an earlier run would confound EVERY measurement below, and the confusion would surface
    # later as an inexplicable result rather than as an abort here. A baseline taken while a seam is
    # armed or a tier is degraded is worse than no baseline at all — it is a WRONG null hypothesis
    # that every later scenario would then be scored against.
    # =========================================================================================
    Write-Phase "PRE: precondition gate (kube context + shared forwards + stack at rest)"

    $ctxRaw  = ''
    $ctxExit = 1
    try {
        $ctxRaw  = kubectl config current-context 2>$null
        # Pin the exit code BEFORE the trim: on a failed call stdout is empty, so a .Trim() on the raw
        # capture (string-cast, never $null) must not be allowed to bypass the failure branch.
        $ctxExit = $LASTEXITCODE
    } catch { $ctxExit = 1 }
    $ctx = ("$ctxRaw").Trim()
    if ($ctxExit -ne 0 -or $ctx -ne 'docker-desktop') {
        Write-Phase "kube context is '$ctx', not 'docker-desktop'. This driver targets the local Docker-Desktop cluster ONLY." 'Red'
        Write-Phase "REMEDIATION: kubectl config use-context docker-desktop" 'Yellow'
        exit 64
    }
    Write-Phase "  kube context = docker-desktop (OK)." 'Gray'

    $sharedUp = $false
    try {
        $probeResp = Invoke-WebRequest -Uri "$api/health/ready" -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop
        if ($probeResp.StatusCode -eq 200) { $sharedUp = $true }
    } catch { }
    if (-not $sharedUp) {
        Write-Phase "the shared stack port-forwards are not carrying traffic (no 200 from $api/health/ready)." 'Red'
        Write-Phase "REMEDIATION: pwsh -File scripts/phase-80-up.ps1   (it owns the eight shared forwards and reaps its own stale PIDs)" 'Yellow'
        exit 64
    }
    Write-Phase "  shared stack forwards live (baseapi /health/ready == 200)." 'Gray'

    # All four app tiers must be fully Ready at their LIVE-READ counts AND carry zero seam residue
    # before anything is measured. Both reads go through the cluster-ops invocation site, whose single
    # command line bakes in the namespace and whose local preference shadow keeps a non-zero kubectl
    # exit from exploding inside this Stop-preference harness.
    $preReplicas = @{}
    $preImages   = @{}
    $preSeamVars = @()
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
            $preSeamVars += $tier
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
    $StackCleanAtStart = $true
    Write-Phase "  all four tiers fully Ready at their live-read counts, zero seam vars at rest." 'Gray'

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
    # MODE DISPATCH.
    # Plan 88-04 owns `-Mode Baseline` only. The fault-driving modes are authored by the plans that
    # own their scenarios, so that each arrives with the arm/settle/re-baseline/trigger/restore
    # sequence its own lever needs rather than a generic one written before any fault was driven.
    # =========================================================================================
    if ($rowMode -eq 'ladder') {
        Write-Phase "mode 'ladder' is not implemented in this driver yet." 'Red'
        Write-Phase "Plan 88-08 authors -Mode DurationLadder (LADDER-01)." 'Yellow'
        exit 64
    }

    if ($rowMode -eq 'scenario') {

        # =====================================================================================
        # -Mode Scenario — THE SCENARIO ENGINE (plan 88-05).
        #
        # THE MANDATORY SEQUENCE, IN THIS ORDER, AND WHY SKIPPING ANY OF IT VOIDS THE ASSERTION
        # (roadmap "Design consequence", 88-RESEARCH.md Pitfall 2, DISC-06):
        #
        #     capture the PRE-ARM baseline    recorded, never authoritative
        #       -> ARM                        set env / (scale rows arm nothing) / start the load
        #       -> rollout status             Wait-TierSettled on the affected tier
        #       -> SETTLE >= 150 s            two export cadences, with traffic flowing
        #       -> RE-CAPTURE the baseline    <- the DISC-01 band a rollout scenario is SCORED against
        #       -> TRIGGER the fault
        #       -> capture AFTER
        #       -> restore, disarm, ASSERT clean
        #
        # `kubectl set env` and `kubectl scale` both mint NEW pods with NEW service_instance_id
        # values, and a k8s restart never RESETS a series — it starts a new one. So every
        # `by (service_instance_id)` panel gains series at that moment and every aggregate briefly
        # dips while the new pods warm up. Scored against a band captured BEFORE the rollout, that
        # dip is indistinguishable from the fault. The discontinuity is therefore recorded as an
        # EXPECTED artifact (RolloutOldInstanceIds / RolloutNewInstanceIds / RolloutUtc) and the
        # scoring band comes from the post-rollout re-capture.
        #
        # A row whose lever causes NO rollout (WEB-01's `http`) legitimately has
        # BaselineRecaptured = $false and is scored against the DISC-01 bands directly. The field is
        # STATED rather than omitted: "no rollout happened" and "nobody checked" must not look alike.
        #
        # SCORING RULES (fixed here; no row may redefine them):
        #   Moved      the after value lies outside the band, in the predicted direction, for >= 2
        #              CONSECUTIVE 60 s samples. At a 60 s sampling interval a single excursion is
        #              indistinguishable from a sampling artifact.
        #   NoData     a first-class panel STATE. Where it is the predicted direction it counts as
        #              MOVED, not as an unread panel.
        #   StayedPut  the cross-talk value stayed inside its own band for the ENTIRE fault window.
        #              A drifting control is a FINDING, recorded with its MaxExcursion, never
        #              re-classified.
        #   Verdict    Fail BEATS Inconclusive. A panel that was READ and did not move is Fail. A
        #              panel that could not be READ is Inconclusive. A false restore claim is Fail
        #              and exits 65, so a dirty stack can never be read as a scenario verdict.
        #
        # A MULTI-SERIES PANEL moved when AT LEAST ONE of its series moved in the predicted
        # direction. Panel 10 renders one series per route and only the routes actually driven can
        # move; requiring all of them would make the assertion unsatisfiable by construction. Every
        # series' own outcome is recorded in SeriesResults[], and the panel entry NAMES the series
        # that carried the movement, so the selection is auditable rather than asserted.
        # =====================================================================================

        # ---- the fixed shape of every capture in this mode -----------------------------------
        $SubWindowSeconds          = 60
        $AfterSubWindowCount       = 6     # 360 s: >= 2 gauge export ticks AND an exact multiple of 120
        $RebaselineSubWindowCount  = 10    # the DISC-01 shape, re-taken after an arm rollout
        # MEASURED in 88-04: panel 4 renders "No data" at a 60 s sub-window, because it is
        # increase(counter[$__range]), increase() needs >= 2 samples inside its range, and the stored
        # resolution here is 60 s (the SDK export cadence, not the 15 s scrape). Panel 4 is therefore
        # read in its OWN batch at 120 s and every entry STATES its own width, so a 120 s reading can
        # never be silently compared against a 60 s one.
        $Panel4SubWindowSeconds    = 120
        $Panel4AfterCount          = 3     # 3 x 120 = the same 360 s span
        $Panel4RebaselineCount     = 5     # 5 x 120 = the same 600 s span
        $ExportTrailSeconds        = 120   # two export cadences, so the LAST sub-window is fully exported
        $LoadRampSeconds           = 30
        $SettleSecondsAfterRollout = 150   # two export cadences (DISC-06)
        # A band computed from fewer than this many samples cannot falsify a non-move: with n=2 the
        # sample stddev is nearly meaningless. 88-04 measured one such series on panel 11
        # (`Workflows`, 2/10 samples), and the caution it carried forward is enforced here in code
        # rather than left to a reader's judgement.
        $MinBandSamples            = 3

        # ---- scenario answer fields, initialised up front so the artifact writer can read any of
        # ---- them on any path under StrictMode.
        #
        # NAMING TRAP, DELIBERATELY AVOIDED: PowerShell variable names are case-INSENSITIVE, so a
        # `$ReplicasBefore` here would be THE SAME VARIABLE as the `$replicasBefore` the outer
        # teardown reads to restore a scaled tier — and it would be a hashtable when the teardown
        # expects an int. The maps are therefore named *Map. (88-04 deviation 3 and 88-03
        # deviation 5 are this same trap in two other forms.)
        $ReplicasBeforeMap        = @{}
        $ReplicasAfterMap         = @{}
        $PanelResults             = @()
        $CrossTalkResults         = @()
        $UnevaluablePanels        = @()
        $ScenarioScreenshotPaths  = @()
        $PreArmBaselineValues     = @()
        $RolloutOldInstanceIds    = @()
        $RolloutNewInstanceIds    = @()
        $RolloutUtc               = $null
        $BaselineRecaptured       = $false
        $BaselineRecaptureNote    = ''
        $ScenarioRateInterval     = $null
        $ScenarioTimeInterval     = $null
        $OldestSampleUtc          = $null
        $DiagnosticEntries        = @()
        $DiagnosticQueryValue     = $null
        $DiagnosticAgreesWithPanel = $null
        $DiagnosticDisagreements  = @()
        $HostLoadRequests         = 0
        $LoadShape                = 'none'
        $LoadActualSeconds        = 0
        $FaultStartUtc            = $null
        $FaultEndUtc              = $null
        $ScaleFaultResult         = $null
        $ReaderUnavailable        = $false
        $NewSeriesAfter           = @()
        $Findings                 = @()
        $rebaseCap                = $null
        $TrafficWorkflowId        = ''
        # The tier this row SCALED, read live by the sequencer before and re-read after. Kept as a
        # SCALAR beside the per-tier maps because the phase's whole T-88-02 control is "this tier went
        # down at N and came back at N" — a map does not state that, and the acceptance criteria of
        # every scale scenario assert the scalar directly.
        $ScaledTierName           = ''
        $ScaledReplicasBefore = $null
        $ScaledReplicasAfter  = $null
        # Regime C, recorded and NEVER scored (the wide-window level an operator actually sees).
        $RangeCumulativeLevelAfter    = $null
        $RangeCumulativeWindowSeconds = 3600
        # Panels captured for the RECORD only: not asserted, not a cross-talk control.
        $ObservedPanels           = @()
        # A prediction the MEASUREMENT corrected. The panel still discriminated (its rendered reading
        # left its healthy band, or the panel emptied), but the direction the plan predicted is not
        # the direction the stack produced. That is a finding, not a pass, so it degrades the verdict.
        $PredictionsCorrected     = @()
        # Did pipeline traffic actually resume after the tier came back? Without this, a post-fault
        # reading measures an IDLE stack rather than a RECOVERED one and nobody can tell.
        $TrafficResumedAfterRestore = $null
        $TrafficResumeDetail        = ''

        $shotDir  = Join-Path $screenshotRoot $canonicalId
        $lever    = "$($scenario.lever)"
        $dwell    = [int]$scenario.dwellSeconds
        $subjects = @($scenario.panelIds       | ForEach-Object { "$_" })
        $controls = @($scenario.crossTalkPanels | ForEach-Object { "$_" })

        # =====================================================================================
        # SCENARIO HELPERS
        # =====================================================================================

        # ---- HTTP LOAD DRIVER (T-88-19) ------------------------------------------------------
        # Every route is a STATIC literal in this function; nothing is derived from a parameter.
        # NO /health/ ROUTE IS EVER DRIVEN: the collector drops those, so they would add load to the
        # cluster and contribute nothing at all to panels 10-14.
        # Concurrency is fixed at about ten requesters against a dev cluster and bounded by the
        # caller's duration; the jobs are reaped by Stop-Phase88HttpLoad and again by the outer
        # finally, so a load job can never outlive the run and pollute a LATER capture.
        function Start-Phase88HttpLoad {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory)][ValidateSet('background', 'sustained')][string]$Shape,
                [Parameter(Mandatory)][int]$DurationSeconds,
                [Parameter(Mandatory)][string]$BaseUri
            )

            # READ-ONLY. Nothing here creates, mutates or deletes a row, so the load cannot perturb
            # the pipeline signal the conservation panels are banding.
            $readOnlyCsv = '/api/v1/workflows,/api/v1/processors,/api/v1/schemas,/api/v1/steps,/api/v1/assignments'
            # The two status drivers PQ-03 ENUMERATED and observed, rather than invented ones:
            #   GET /api/v1/__phase88_probe_unmatched            -> 404 (unmatched path, inert)
            #   POST /api/v1/orchestration/start with body `[]`  -> 400 (empty activation, inert)
            $unmatchedCsv = '/api/v1/__phase88_probe_unmatched'
            $badBodyCsv   = '/api/v1/orchestration/start'

            # Routes cross the job boundary as ONE comma-joined string: -ArgumentList maps each
            # element to a positional parameter, and an array element there is a standing ambiguity.
            $worker = {
                param($BaseUri, $DurationSeconds, $RoutesCsv, $PauseMs, $Method, $Body)
                $routes = @(("$RoutesCsv") -split ',' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                $deadline = (Get-Date).AddSeconds($DurationSeconds)
                $n = 0
                while ((Get-Date) -lt $deadline) {
                    foreach ($r in $routes) {
                        try {
                            $req = @{
                                Uri                = "$BaseUri$r"
                                Method             = $Method
                                UseBasicParsing    = $true
                                TimeoutSec         = 15
                                SkipHttpErrorCheck = $true
                                ErrorAction        = 'Stop'
                            }
                            if (-not [string]::IsNullOrEmpty($Body)) {
                                $req['Body'] = $Body
                                $req['ContentType'] = 'application/json'
                            }
                            $null = Invoke-WebRequest @req
                            $n++
                        } catch { }
                        if ($PauseMs -gt 0) { Start-Sleep -Milliseconds $PauseMs }
                        if ((Get-Date) -ge $deadline) { break }
                    }
                }
                return $n
            }

            $jobs = @()
            if ($Shape -eq 'background') {
                # The BASE-01 shape (~2.5 req/s, read-only): enough that the WebApi panels have a
                # live level for a NON-http scenario to control against, small enough that it cannot
                # itself be mistaken for a fault.
                $jobs += Start-Job -ScriptBlock $worker -ArgumentList $BaseUri, $DurationSeconds, $readOnlyCsv, 400, 'GET', ''
            }
            else {
                # SUSTAINED, not a burst. http_server_active_requests / kestrel_active_connections /
                # kestrel_queued_connections are GAUGES sampled at the 60 s export cadence: a request
                # that begins and ends between two exports is never recorded at all, so a burst would
                # leave panel 14 reading 0 at every tick (88-RESEARCH.md Pitfall 8). Eight tight-loop
                # readers keep ~10 connections open and requests genuinely in flight across ticks.
                for ($w = 0; $w -lt 8; $w++) {
                    $jobs += Start-Job -ScriptBlock $worker -ArgumentList $BaseUri, $DurationSeconds, $readOnlyCsv, 0, 'GET', ''
                }
                # ~4 req/s each is ample to lift a 0..0 status-code band well clear of zero; hammering
                # them harder would only add log noise for no extra signal.
                $jobs += Start-Job -ScriptBlock $worker -ArgumentList $BaseUri, $DurationSeconds, $unmatchedCsv, 250, 'GET', ''
                $jobs += Start-Job -ScriptBlock $worker -ArgumentList $BaseUri, $DurationSeconds, $badBodyCsv,   250, 'POST', '[]'
            }
            return [object[]]$jobs
        }

        # Reap the load and return the total request count. Called on the happy path; the outer
        # finally repeats the reap so an interrupted run cannot leave a job hitting the WebApi.
        function Stop-Phase88HttpLoad {
            [CmdletBinding()]
            param([AllowEmptyCollection()][object[]]$Jobs)
            $total = 0
            foreach ($j in @($Jobs)) {
                try { $null = Wait-Job -Job $j -Timeout 30 } catch { }
                try {
                    $out = @(Receive-Job -Job $j -ErrorAction SilentlyContinue)
                    if ($out.Count -gt 0) {
                        $parsed = 0
                        if ([int]::TryParse("$($out[-1])", [ref]$parsed)) { $total += $parsed }
                    }
                } catch { }
                try { Stop-Job -Job $j -ErrorAction SilentlyContinue } catch { }
                try { Remove-Job -Job $j -Force -ErrorAction SilentlyContinue } catch { }
            }
            return $total
        }

        # ---- ONE CAPTURE: every requested panel over Count abutting absolute windows ending at
        # ---- EndUtc, in ONE browser session, so a claim and its control are measured in the SAME
        # ---- windows. Panel 4, when requested, gets a SECOND batch at its own 120 s width.
        function Invoke-Phase88Capture {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory)][string[]]$PanelIdList,
                [Parameter(Mandatory)][datetime]$EndUtc,
                [Parameter(Mandatory)][int]$Count,
                [Parameter(Mandatory)][int]$Panel4Count,
                [Parameter(Mandatory)][string]$ShotDir,
                [Parameter(Mandatory)][string]$Label
            )

            $sixty = @($PanelIdList | Where-Object { "$_" -ne '4' })
            $wantsPanel4 = (@($PanelIdList | Where-Object { "$_" -eq '4' }).Count -gt 0)

            $out = [ordered]@{
                Label            = $Label
                Batch            = $null
                Windows          = @()
                Panel4Batch      = $null
                Panel4Windows    = @()
                WindowStartUtc   = $null
                WindowEndUtc     = $null
                State            = 'NotRun'
                Panel4State      = 'NotRun'
                ScreenshotPaths  = @()
            }

            if (@($sixty).Count -gt 0) {
                $wins = @(Get-PinnedWindowSeries -EndUtc $EndUtc -SubWindowSeconds 60 -Count $Count)
                $mintedWidth = [int](($wins[0].ToMs - $wins[0].FromMs) / 1000)
                if ($mintedWidth -ne 60) { throw "the minted sub-window is ${mintedWidth}s, not the 60s this capture records." }
                $out.Windows = $wins
                $out.WindowStartUtc = $wins[0].FromUtc
                $out.WindowEndUtc   = $wins[-1].ToUtc
                Write-Phase "  [$Label] reading $(@($sixty).Count) panel(s) x $Count x 60s : $($wins[0].FromUtc) -> $($wins[-1].ToUtc)" 'Gray'
                $b = Invoke-PanelReadBatch -PanelIds ([string[]]$sixty) -Windows $wins -ScreenshotDir $ShotDir `
                       -LocatorMode $LocatorMode -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight `
                       -BasicAuthBase64 $adminB64
                $out.Batch = $b
                $out.State = "$($b.State)"
                $out.ScreenshotPaths = @(Get-ScreenshotPaths $b.Readings)
                Write-Phase "  [$Label] batch State=$($b.State) requested=$($b.Requested) emitted=$($b.Emitted)" 'Gray'
            }

            if ($wantsPanel4) {
                $p4Wins = @(Get-PinnedWindowSeries -EndUtc $EndUtc -SubWindowSeconds 120 -Count $Panel4Count)
                $out.Panel4Windows = $p4Wins
                if ($null -eq $out.WindowStartUtc) {
                    $out.WindowStartUtc = $p4Wins[0].FromUtc
                    $out.WindowEndUtc   = $p4Wins[-1].ToUtc
                }
                Write-Phase "  [$Label] panel 4 at its OWN width: $Panel4Count x 120s (88-04 measured that 60s renders No data)" 'Gray'
                $b4 = Invoke-PanelReadBatch -PanelIds @('4') -Windows $p4Wins -ScreenshotDir $ShotDir `
                        -LocatorMode $LocatorMode -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight `
                        -BasicAuthBase64 $adminB64
                $out.Panel4Batch = $b4
                $out.Panel4State = "$($b4.State)"
                $out.ScreenshotPaths = @(@($out.ScreenshotPaths) + @(Get-ScreenshotPaths $b4.Readings) | Select-Object -Unique)
            }

            return [pscustomobject]$out
        }

        # The per-series readings for ONE panel out of a capture, plus the width they were taken at
        # and the per-window panel states. A stat panel yields a single entry at index -1.
        function Get-Phase88PanelReading {
            [CmdletBinding()]
            param([Parameter(Mandatory)]$Capture, [Parameter(Mandatory)][string]$PanelId)

            $panel = $Panels[$PanelId]
            $isP4  = ($PanelId -eq '4')
            $batch = if ($isP4) { $Capture.Panel4Batch } else { $Capture.Batch }
            $width = if ($isP4) { $Panel4SubWindowSeconds } else { $SubWindowSeconds }

            $result = [ordered]@{
                PanelId          = $PanelId
                SubWindowSeconds = $width
                Entries          = @()
                States           = @()
                Readable         = $false
            }
            if ($null -eq $batch) { return [pscustomobject]$result }

            $readings = @($batch.Readings)
            $states = @(Get-PanelStates -Readings $readings -PanelId $PanelId)
            $result.States = [string[]]$states
            if (@($states).Count -eq 0) { return [pscustomobject]$result }
            $result.Readable = $true

            $entries = @()
            if ("$($panel.Type)" -eq 'stat') {
                $entries += [pscustomobject]@{
                    Index  = -1
                    Name   = "$($panel.SeriesName)"
                    Values = [double[]]@(Get-PanelSamples -Readings $readings -PanelId $PanelId)
                }
            }
            else {
                $n = Get-PanelSeriesCount -Readings $readings -PanelId $PanelId
                for ($i = 0; $i -lt $n; $i++) {
                    $nm = Get-PanelSeriesNameAt -Readings $readings -PanelId $PanelId -SeriesIndex $i
                    $entries += [pscustomobject]@{
                        Index  = $i
                        Name   = "$($nm.Name)"
                        Values = [double[]]@(Get-PanelSamples -Readings $readings -PanelId $PanelId -SeriesIndex $i)
                    }
                }
            }
            $result.Entries = [object[]]$entries
            return [pscustomobject]$result
        }

        # Legend names, verbatim and in row order. A legend series NAME change is an INDEPENDENT
        # discrimination signal — when an `or vector(0)` guard stops firing, the label-less series is
        # replaced by a labelled one — so it is recorded for every timeseries panel even when the
        # numeric assertion already passed.
        function Get-Phase88LegendNames {
            [CmdletBinding()]
            param([Parameter(Mandatory)]$Reading)
            $names = @()
            foreach ($e in @($Reading.Entries)) { $names += "$($e.Name)" }
            return [string[]]$names
        }

        # Band lookup: BY NAME FIRST, falling back to the row index. 88-04 measured that a new series
        # shifts every later row index (panel 12 gains 400/404 the moment WEB-01 drives them), so an
        # index-only match would silently score one series against another series' band.
        function Find-Phase88Band {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Bands,
                [Parameter(Mandatory)][int]$PanelId,
                [Parameter(Mandatory)][AllowEmptyString()][string]$SeriesName,
                [Parameter(Mandatory)][int]$SeriesIndex
            )
            $cand = @($Bands | Where-Object { [int]$_.PanelId -eq $PanelId })
            if ($cand.Count -eq 0) { return $null }
            if (-not [string]::IsNullOrWhiteSpace($SeriesName)) {
                $byName = @($cand | Where-Object {
                        [string]::Equals("$($_.SeriesName)", $SeriesName, [System.StringComparison]::Ordinal)
                    })
                if ($byName.Count -ge 1) { return $byName[0] }
            }
            $byIdx = @($cand | Where-Object { [int]$_.SeriesIndex -eq $SeriesIndex })
            if ($byIdx.Count -ge 1) { return $byIdx[0] }
            return $null
        }

        # How far outside its band a value sits, 0 when inside. Used for MaxExcursion (a cross-talk
        # drift is recorded WITH its size, so a hair's-breadth wobble and a real blast radius are
        # distinguishable in the artifact rather than collapsed into one boolean).
        function Get-Phase88Excursion {
            [CmdletBinding()]
            param([double]$Value, [double]$Low, [double]$High)
            if ($Value -gt $High) { return ($Value - $High) }
            if ($Value -lt $Low)  { return ($Low - $Value) }
            return 0.0
        }

        # Turn a capture into band rows in the SAME shape as BASE-01's BaselineBands, so the scorer
        # cannot tell a re-captured band from a DISC-01 one and therefore cannot treat them differently.
        function New-Phase88BandSet {
            [CmdletBinding()]
            param([Parameter(Mandatory)]$Capture, [Parameter(Mandatory)][string[]]$PanelIdList)
            $bands = @()
            foreach ($p in @($PanelIdList)) {
                $rd = Get-Phase88PanelReading -Capture $Capture -PanelId $p
                foreach ($e in @($rd.Entries)) {
                    $vals = @($e.Values)
                    if ($vals.Count -eq 0) { continue }
                    $b = Get-PanelBand -Values ([double[]]$vals)
                    $bands += [pscustomobject]@{
                        PanelId          = [int]$p
                        PanelTitle       = "$($Panels[$p].Title)"
                        Regime           = "$($Panels[$p].Regime)"
                        SeriesIndex      = [int]$e.Index
                        SeriesName       = "$($e.Name)"
                        SubWindowSeconds = [int]$rd.SubWindowSeconds
                        Values           = [double[]]$vals
                        States           = [string[]]@($rd.States)
                        BandLow          = [double]$b.Low
                        BandHigh         = [double]$b.High
                        BandMean         = [double]$b.Mean
                        BandSigma        = [double]$b.Sigma
                        FloorApplied     = [bool]$b.FloorApplied
                        SampleCount      = [int]$b.SampleCount
                    }
                }
            }
            return [object[]]$bands
        }


        # Write the artifact and assert its own serialisation depth. -Depth 10, NOT a shallower
        # depth: PanelResults[] and CrossTalkPanels[] are OBJECTS INSIDE ARRAYS carrying nested
        # Values[]/States[], which a shallower serialisation writes as the literal type name —
        # destroying the evidence that makes every verdict here recomputable (T-88-18). The guard is
        # STRUCTURAL (a JSON VALUE equal to that string), not a substring search, because a captured
        # diagnostic message may legitimately MENTION the type and a guard that cries wolf on its own
        # error text eventually gets disabled.
        function Save-Phase88ScenarioArtifact {
            [CmdletBinding()]
            param([Parameter(Mandatory)]$Report, [Parameter(Mandatory)][string]$Path)
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
            ([pscustomobject]$Report) | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding utf8
            $written = Get-Content $Path -Raw
            return (-not ($written -match '(?m)(:\s*"System\.Object\[\]"|^\s*"System\.Object\[\]"\s*,?\s*$)'))
        }

        # =====================================================================================
        # STEP S1 — THE DISC-01 BANDS. Loaded as the starting reference for every scenario, and
        # AUTHORITATIVE only where the row's lever caused no rollout (see STEP S4).
        # =====================================================================================
        Write-Phase "STEP S1: load the DISC-01 bands"
        $disc01Path = Join-Path $reportDir 'phase-88-BASE-01.json'
        if (-not (Test-Path -LiteralPath $disc01Path)) {
            Write-Phase "the DISC-01 baseline artifact is missing: $disc01Path" 'Red'
            Write-Phase "REMEDIATION: pwsh -File scripts/phase-88-panel-discriminate.ps1 -ScenarioId BASE-01 -Mode Baseline" 'Yellow'
            exit 64
        }
        $disc01 = Get-Content $disc01Path -Raw | ConvertFrom-Json
        $Disc01Bands = @($disc01.BaselineBands)
        if (@($Disc01Bands).Count -eq 0) {
            Write-Phase "the DISC-01 artifact carries no bands — there is no null hypothesis to test against." 'Red'
            exit 64
        }
        Write-Phase "  $(@($Disc01Bands).Count) DISC-01 band(s), captured $($disc01.BaselineWindowStart) -> $($disc01.BaselineWindowEnd)" 'Gray'
        $ReplicasBeforeMap = $preReplicas

        # =====================================================================================
        # STEP S2 — $__rate_interval and the evidentiary horizon, DERIVED (never assumed), so the
        # artifact states its own "how it verified" block exactly as BASE-01 does.
        # =====================================================================================
        try {
            $ds = Invoke-RestMethod -Uri "$gf/api/datasources/uid/skp-prometheus" -Headers $auth -TimeoutSec 20 -ErrorAction Stop
            $tiText  = ''
            $dsNames = @(Get-PropertyNames $ds)
            if ($dsNames -contains 'jsonData') {
                $jdNames = @(Get-PropertyNames $ds.jsonData)
                if ($jdNames -contains 'timeInterval') { $tiText = "$($ds.jsonData.timeInterval)" }
            }
            $ti = ConvertFrom-GrafanaDuration $tiText
            if ($ti -gt 0) {
                $ScenarioTimeInterval = $tiText
                $stepS = Get-QueryStepSeconds -RangeSeconds ($SubWindowSeconds * $AfterSubWindowCount) -TimeIntervalSeconds $ti -MaxDataPoints $ViewportWidth
                $ScenarioRateInterval = Get-RateIntervalSeconds $ti $stepS
                Write-Phase "STEP S2: timeInterval='$tiText' -> rate_interval=${ScenarioRateInterval}s" 'Gray'
            }
        } catch {
            Write-Phase "STEP S2: the datasource read failed: $($_.Exception.Message)" 'Yellow'
        }
        $nowUnixS = ([DateTimeOffset]::UtcNow).ToUnixTimeSeconds()
        try {
            $oldestQ = Invoke-ProxyRangeQuery -Query 'up' -StartUnix ($nowUnixS - (336 * 3600)) -EndUnix $nowUnixS -StepSeconds 3600
            $oldestUnix = $null
            foreach ($sOld in @($oldestQ.data.result)) {
                $snOld = @(Get-PropertyNames $sOld)
                if ($snOld -notcontains 'values') { continue }
                $valsOld = @($sOld.values)
                if ($valsOld.Count -eq 0) { continue }
                $tsOld = [double]($valsOld[0][0])
                if ($null -eq $oldestUnix -or $tsOld -lt $oldestUnix) { $oldestUnix = $tsOld }
            }
            if ($null -ne $oldestUnix) {
                $OldestSampleUtc = ([System.DateTimeOffset]::FromUnixTimeSeconds([long]$oldestUnix)).UtcDateTime.ToString('o')
            }
        } catch { }

        # =====================================================================================
        # STEP S3 — THE DROPPED-ROW RECORDER (-RecordDroppedRow).
        #
        # A missing proof is a ROW, never an absence. This path drives NO fault and issues NO
        # mutation: it re-checks the probe field that dropped the row, OBSERVES the row's panel over
        # ordinary pinned windows so PanelStateAfter is a measurement rather than an assertion, and
        # writes a degraded row in the same schema with the enumerated accepted-unproven reason.
        # =====================================================================================
        if ($recordingDroppedRow) {
            Write-Phase "STEP S3: recording the ACCEPTED-UNPROVEN row for '$canonicalId' — no fault, no mutation"

            $probePath = Join-Path $reportDir 'phase-88-wave0-probe.json'
            if (-not (Test-Path -LiteralPath $probePath)) {
                Write-Phase "the wave-0 probe artifact is missing: $probePath — the drop cannot be re-confirmed." 'Red'
                exit 64
            }
            $probe = Get-Content $probePath -Raw | ConvertFrom-Json
            if (-not $scenario.Contains('droppedProbeField')) {
                Write-Phase "row '$canonicalId' declares no droppedProbeField — refusing to record a drop nothing measured." 'Red'
                exit 64
            }
            $dropField  = "$($scenario.droppedProbeField)"
            $dropExpect = "$($scenario.droppedProbeExpect)"
            $probeNames = @(Get-PropertyNames $probe)
            if ($probeNames -notcontains $dropField) {
                Write-Phase "the probe artifact carries no '$dropField' — the drop cannot be re-confirmed." 'Red'
                exit 64
            }
            $dropActual = "$($probe.$dropField)"
            if (-not [string]::Equals($dropActual, $dropExpect, [System.StringComparison]::OrdinalIgnoreCase)) {
                Write-Phase "the probe now reports $dropField='$dropActual' but the row was dropped on '$dropExpect'." 'Red'
                Write-Phase "The measurement that justified the drop no longer holds — re-run the scenario instead of recording it unproven." 'Yellow'
                exit 64
            }
            Write-Phase "  drop re-confirmed: probe $dropField = '$dropActual'." 'Gray'

            $obsEnd = ([datetime]::UtcNow).AddSeconds(-$ExportTrailSeconds)
            $obsCap = Invoke-Phase88Capture -PanelIdList ([string[]]$subjects) -EndUtc $obsEnd `
                        -Count $AfterSubWindowCount -Panel4Count $Panel4AfterCount -ShotDir $shotDir -Label 'observe'
            $ScenarioScreenshotPaths = @($obsCap.ScreenshotPaths)

            $dropPanelResults = @()
            foreach ($p in $subjects) {
                $rd = Get-Phase88PanelReading -Capture $obsCap -PanelId $p
                $stateAfter = if (@($rd.States).Count -gt 0) { (@($rd.States | Select-Object -Unique) -join '|') } else { 'NoReading' }
                if (-not $rd.Readable) {
                    $UnevaluablePanels += "panel ${p} ($($Panels[$p].Title)): the reader returned NO reading at all during the accepted-unproven observation"
                    $ReaderUnavailable = $true
                }
                $obsEntry = @($rd.Entries)
                $obsVals  = if (@($obsEntry).Count -gt 0) { [double[]]@($obsEntry[0].Values) } else { [double[]]@() }
                $bandRow  = Find-Phase88Band -Bands $Disc01Bands -PanelId ([int]$p) `
                              -SeriesName "$($Panels[$p].SeriesName)" -SeriesIndex -1
                $dropPanelResults += [pscustomobject]@{
                    PanelId                   = [int]$p
                    PanelTitle                = "$($Panels[$p].Title)"
                    Regime                    = "$($Panels[$p].Regime)"
                    SeriesName                = "$($Panels[$p].SeriesName)"
                    PredictedDirection        = "$($scenario.predictedDirection[$p])"
                    BandLow                   = if ($null -ne $bandRow) { [double]$bandRow.BandLow } else { $null }
                    BandHigh                  = if ($null -ne $bandRow) { [double]$bandRow.BandHigh } else { $null }
                    BandSource                = 'BASE-01 (DISC-01)'
                    SubWindowSeconds          = [int]$rd.SubWindowSeconds
                    BaselineValues            = if ($null -ne $bandRow) { [double[]]@($bandRow.Values) } else { [double[]]@() }
                    BaselineStates            = if ($null -ne $bandRow) { [string[]]@($bandRow.States) } else { [string[]]@() }
                    AfterValues               = $obsVals
                    AfterStates               = [string[]]@($rd.States)
                    Moved                     = $false
                    ConsecutiveSamplesOutside = 0
                    PanelStateAfter           = $stateAfter
                    LegendNamesBaseline       = [string[]]@()
                    LegendNamesAfter          = [string[]]@(Get-Phase88LegendNames -Reading $rd)
                    Note                      = 'OBSERVED, not driven. No fault was applied, so this is the panel''s ordinary guarded reading — it is recorded so the accepted-unproven row states what the panel actually shows today rather than asserting it.'
                }
            }
            $PanelResults = @($dropPanelResults)

            # The declared cross-talk list is carried, with Applicable=false: no fault was driven, so
            # there is no blast radius to bound. Claiming a control HELD when nothing was perturbed
            # would be a fabricated claim, and an empty list would hide the declaration.
            foreach ($c in $controls) {
                $CrossTalkResults += [pscustomobject]@{
                    PanelId     = [int]$c
                    PanelTitle  = "$($Panels[$c].Title)"
                    Applicable  = $false
                    BandLow     = $null
                    BandHigh    = $null
                    AfterValues = [double[]]@()
                    StayedPut   = $null
                    MaxExcursion = $null
                    Note        = 'no fault was driven, so there is no blast radius for this control to bound; not measured rather than claimed'
                }
            }

            $restore = Assert-StackRestored -Tiers $Tiers -ExpectedReplicas $preReplicas -ExpectedImages $preImages
            foreach ($t in $Tiers) { $ReplicasAfterMap[$t] = Get-LiveReplicas -Tier $t }

            $endpointLines = @()
            if (@(Get-PropertyNames $probe) -contains 'ProbedEndpoints') {
                foreach ($pe in @($probe.ProbedEndpoints)) {
                    $endpointLines += ("{0} {1} -> {2}" -f "$($pe.Method)", "$($pe.Path)", "$($pe.Status)")
                }
            }
            $AcceptedUnprovenReason =
                "$($scenario.statusReason). " +
                "Enumerated endpoint probe (PQ-03, $($endpointLines.Count) endpoints, zero 5xx on safe input): " +
                ($endpointLines -join ' | ') + ". " +
                'The dependency-outage route to a 5xx is NOT substituted: redis has no PVC, so scaling it wipes L2, and under the Phase-86 hard readiness latch the WebApi does not self-heal from a dependency outage — recovery costs a WebApi pod restart, which would perturb every later baseline in this phase. ' +
                'MAINTENANCE GUIDANCE: panel 13''s guarded green 0 is indistinguishable from "the 5xx counter has never been emitted at all". The first non-zero reading on panel 13 should be corroborated against the WebApi logs for the same window before it is trusted, and once ONE 5xx has ever occurred the numerator series exists permanently — so a zero on panel 13 after that date means something different from today''s zero.'

            $humanDrop = "phase-88 $canonicalId verdict=Inconclusive: DROPPED row RECORDED, not run. " +
                         "No fault was driven and no cluster mutation was issued. " +
                         "Drop re-confirmed from the wave-0 probe ($dropField='$dropActual'). " +
                         "Panel $(@($subjects) -join ',') observed over $AfterSubWindowCount x ${SubWindowSeconds}s pinned windows " +
                         "($($obsCap.WindowStartUtc) -> $($obsCap.WindowEndUtc)) at ${ViewportWidth}x${ViewportHeight}; " +
                         "state=$(@($PanelResults | ForEach-Object { $_.PanelStateAfter }) -join ','). " +
                         "stack clean: seamVars=$($restore.SeamVarsClean) replicas=$($restore.ReplicasRestored) images=$($restore.ImagesUnchanged)."

            $dropReport = [ordered]@{
                ScenarioId                = $canonicalId
                Verdict                   = 'Inconclusive'
                Status                    = 'Dropped'
                Lever                     = 'none (dropped)'
                TargetTier                = ''
                Moved                     = $false
                CrossTalkHeld             = $null
                BaselineRecaptured        = $false
                BaselineRecaptureNote     = 'no rollout occurred because no fault was driven; the DISC-01 bands are carried for reference only'
                StackRestored             = [bool]$restore.Ok
                AcceptedUnproven          = $true
                AcceptedUnprovenReason    = $AcceptedUnprovenReason
                StatusReason              = "$($scenario.statusReason)"
                DecidedBy                 = "$($scenario.decidedBy)"
                DroppedProbeField         = $dropField
                DroppedProbeValue         = $dropActual
                ProbedEndpoints           = [string[]]$endpointLines
                DependencyOutageDriven    = $false
                PanelResults              = @($PanelResults)
                CrossTalkPanels           = @($CrossTalkResults)
                PreArmBaselineValues      = @()
                RolloutOldInstanceIds     = [string[]]@()
                RolloutNewInstanceIds     = [string[]]@()
                RolloutUtc                = $null
                RolloutNote               = 'no rollout: this row was recorded, not run'
                ReplicasBefore            = $ReplicasBeforeMap
                ReplicasAfter             = $ReplicasAfterMap
                ReplicasRestored          = $restore.ReplicasRestored
                SeamVarsAfter             = @($restore.SeamVarsFound)
                SeamVarsClean             = $restore.SeamVarsClean
                ImagesUnchanged           = $restore.ImagesUnchanged
                ImageMismatches           = @($restore.ImageMismatches)
                ReplicaMismatches         = @($restore.ReplicaMismatches)
                BaselineWindowStart       = "$($disc01.BaselineWindowStart)"
                BaselineWindowEnd         = "$($disc01.BaselineWindowEnd)"
                AfterWindowStart          = "$($obsCap.WindowStartUtc)"
                AfterWindowEnd            = "$($obsCap.WindowEndUtc)"
                SubWindowSeconds          = $SubWindowSeconds
                SubWindowCount            = $AfterSubWindowCount
                ViewportWidth             = $ViewportWidth
                ViewportHeight            = $ViewportHeight
                LocatorMode               = $LocatorMode
                RateIntervalSeconds       = $ScenarioRateInterval
                DatasourceTimeInterval    = $ScenarioTimeInterval
                OldestSampleUtc           = $OldestSampleUtc
                DiagnosticQueryValue      = $null
                DiagnosticAgreesWithPanel = $null
                DiagnosticNote            = 'no diagnostic cross-check: nothing was driven, so there is no claim for a proxy value to corroborate'
                ScreenshotPaths           = @($ScenarioScreenshotPaths)
                UnevaluablePanels         = @($UnevaluablePanels)
                CompletedUtc              = ([DateTimeOffset]::UtcNow).ToString('o')
                HumanSummary              = $humanDrop
            }

            $dropPath = Join-Path $reportDir ("phase-88-{0}.json" -f $canonicalId)
            if (-not (Save-Phase88ScenarioArtifact -Report $dropReport -Path $dropPath)) {
                Write-Phase "the written artifact carries a JSON VALUE of 'System.Object-array' — the serialisation depth is too shallow." 'Red'
                exit 66
            }
            Write-Phase "verdict artifact: $dropPath" 'Green'
            Write-Phase $humanDrop 'Yellow'

            $dropExit = Resolve-AnalyzerExitCode ([pscustomobject]$dropReport)
            if (-not $restore.Ok) { $dropExit = 65 }
            $resolvedDrop = Resolve-SweepClass $dropExit
            Write-Phase "class=$($resolvedDrop.Class) exit=$dropExit" 'Gray'
            exit $dropExit
        }

        # =====================================================================================
        # STEP S4 — PRE-ARM BASELINE (recorded, NEVER authoritative).
        #
        # Taken over the window that has just ELAPSED, offset by the export trail so every sample in
        # it is already stored. It costs no wall time and it is exactly the state immediately before
        # the fault; its bounds are recorded so a reader can see which window it was.
        # =====================================================================================
        Write-Phase "STEP S4: pre-arm baseline (recorded, not authoritative)"
        # Observed-only panels ride along in the SAME batch as the claim and its controls, so their
        # reading is taken in the same windows rather than adjacent to them. They are recorded and
        # never scored — see $ObservedPanels.
        $observeOnly = @()
        if ($scenario.Contains('observePanels')) {
            $observeOnly = @(@($scenario.observePanels) | ForEach-Object { "$_" } |
                Where-Object { (@($subjects) -notcontains "$_") -and (@($controls) -notcontains "$_") })
        }
        $capturePanels = [string[]]@(@($subjects) + @($controls) + @($observeOnly) | Select-Object -Unique)
        $preArmEnd = ([datetime]::UtcNow).AddSeconds(-$ExportTrailSeconds)
        $preArmCap = Invoke-Phase88Capture -PanelIdList $capturePanels -EndUtc $preArmEnd `
                       -Count $AfterSubWindowCount -Panel4Count $Panel4AfterCount -ShotDir $shotDir -Label 'pre-arm'
        $ScenarioScreenshotPaths = @($preArmCap.ScreenshotPaths)

        $preArmReadings = @{}
        foreach ($p in $capturePanels) {
            $rd = Get-Phase88PanelReading -Capture $preArmCap -PanelId $p
            $preArmReadings[$p] = $rd
            foreach ($e in @($rd.Entries)) {
                $PreArmBaselineValues += [pscustomobject]@{
                    PanelId          = [int]$p
                    SeriesIndex      = [int]$e.Index
                    SeriesName       = "$($e.Name)"
                    SubWindowSeconds = [int]$rd.SubWindowSeconds
                    Values           = [double[]]@($e.Values)
                    States           = [string[]]@($rd.States)
                }
            }
        }
        Write-Phase "  $(@($PreArmBaselineValues).Count) pre-arm series recorded over $($preArmCap.WindowStartUtc) -> $($preArmCap.WindowEndUtc)" 'Gray'

        # =====================================================================================
        # STEP S5 — ARM, ROLLOUT, SETTLE, RE-BASELINE.
        #
        # `seam` rows arm before the fault and therefore ROLL THE PODS before it. `scale` rows arm
        # nothing (their rollout IS the fault, at trigger time) but still take a fresh band, because
        # scoring an hours-old DISC-01 band is a weaker claim than scoring one taken minutes earlier
        # under the same conditions. `http` rows neither arm nor roll: BaselineRecaptured stays false
        # and the DISC-01 bands are used directly — STATED, not omitted.
        # =====================================================================================
        $ScoringBands   = @($Disc01Bands)
        $ScoringBandSource = 'BASE-01 (DISC-01)'
        $affectedTier   = ''
        $SeamActiveDuringRebaseline = $false

        if ($lever -eq 'http') {
            $BaselineRecaptureNote = 'lever `http` mutates no workload, so NO rollout occurs, no pod identity changes, and there is nothing to re-baseline against. The DISC-01 bands are therefore authoritative unchanged. This field is stated rather than omitted so that "no rollout happened" and "nobody checked" cannot look alike.'
            Write-Phase "STEP S5: lever 'http' — no arm, no rollout, no re-baseline (DISC-01 bands stay authoritative)"
        }
        else {
            # A low, steady read-only load runs for the whole of a non-http scenario so the WebApi
            # cross-talk panels have the same kind of live level BASE-01 banded them against.
            $bgSeconds = $SettleSecondsAfterRollout + ($SubWindowSeconds * $RebaselineSubWindowCount) + $ExportTrailSeconds + $dwell + ($SubWindowSeconds * $AfterSubWindowCount) + 900
            $loadJobs  = @(Start-Phase88HttpLoad -Shape 'background' -DurationSeconds $bgSeconds -BaseUri $api)
            $LoadShape = 'background'
            Write-Phase "STEP S5: background read-only load started for ${bgSeconds}s so the WebApi controls have a live level" 'Gray'

            if ($lever -eq 'seam') {
                $seamTier = "$($scenario.seamTier)"
                $seamVar  = "$($scenario.seamVar)"
                $RolloutOldInstanceIds = [string[]]@(Get-TierPodNames -Tier $seamTier)
                Write-Phase "STEP S5: arming '$seamVar' on '$seamTier'"
                $arm = Set-Phase88Seam -Tier $seamTier -Name $seamVar
                $seamArmed = [bool]$arm.Armed
                if (-not $arm.Ok) {
                    Write-Phase "the seam arm failed: $($arm.Detail)" 'Red'; exit 61
                }
                $RolloutNewInstanceIds = [string[]]@($arm.PodNamesAfter)
                $RolloutUtc = "$($arm.ArmedUtc)"
                $affectedTier = $seamTier
                $SeamActiveDuringRebaseline = $true

                if (-not [string]::IsNullOrWhiteSpace("$($scenario.triggerSeamTier)")) {
                    $seamTier2 = "$($scenario.triggerSeamTier)"
                    $seamVar2  = "$($scenario.triggerSeamVar)"
                    Write-Phase "STEP S5: arming the TRIGGER seam '$seamVar2' on '$seamTier2'"
                    $arm2 = Set-Phase88Seam -Tier $seamTier2 -Name $seamVar2
                    $seamArmed2 = [bool]$arm2.Armed
                    if (-not $arm2.Ok) { Write-Phase "the trigger seam arm failed: $($arm2.Detail)" 'Red'; exit 61 }
                    $RolloutOldInstanceIds = [string[]]@(@($RolloutOldInstanceIds) + @($arm2.PodNamesBefore))
                    $RolloutNewInstanceIds = [string[]]@(@($RolloutNewInstanceIds) + @($arm2.PodNamesAfter))
                }
            }
            else {
                $affectedTier = "$($scenario.targetTier)"
                $BaselineRecaptureNote = 'lever `scale` arms nothing: its rollout IS the fault and happens at TRIGGER time, so there is no pre-fault rollout to re-baseline against. The band is nevertheless re-captured here, minutes before the fault and under the same conditions, which is a stronger null hypothesis than an hours-old DISC-01 band. The discontinuity itself is recorded from the sequencer''s own pod sets.'
            }

            if (-not [string]::IsNullOrWhiteSpace($affectedTier)) {
                if (-not (Wait-TierSettled -Tier $affectedTier -TimeoutSeconds 180)) {
                    Write-Phase "'$affectedTier' did not settle after the arm — refusing to re-baseline against a rolling tier." 'Red'
                    exit 60
                }
            }

            Write-Phase "STEP S5: settling ${SettleSecondsAfterRollout}s (two export cadences) with traffic flowing before the re-baseline..."
            Start-Sleep -Seconds 150

            $rebaseStart = [datetime]::UtcNow
            $rebaseHold  = ($SubWindowSeconds * $RebaselineSubWindowCount) + $ExportTrailSeconds
            Write-Phase "STEP S5: holding ${rebaseHold}s so the authoritative re-baseline window is fully exported..."
            Start-Sleep -Seconds $rebaseHold
            $rebaseEnd = $rebaseStart.AddSeconds($SubWindowSeconds * $RebaselineSubWindowCount)

            $rebaseCap = Invoke-Phase88Capture -PanelIdList $capturePanels -EndUtc $rebaseEnd `
                           -Count $RebaselineSubWindowCount -Panel4Count $Panel4RebaselineCount -ShotDir $shotDir -Label 're-baseline'
            $ScenarioScreenshotPaths = @(@($ScenarioScreenshotPaths) + @($rebaseCap.ScreenshotPaths) | Select-Object -Unique)
            $rebaseBands = @(New-Phase88BandSet -Capture $rebaseCap -PanelIdList $capturePanels)
            if (@($rebaseBands).Count -eq 0) {
                Write-Phase "the re-baseline capture produced no band at all — refusing to score against a band that does not exist." 'Red'
                $ReaderUnavailable = $true
            }
            else {
                $ScoringBands = $rebaseBands
                $ScoringBandSource = "re-captured after the arm rollout ($($rebaseCap.WindowStartUtc) -> $($rebaseCap.WindowEndUtc))"
                $BaselineRecaptured = $true
                Write-Phase "  re-baseline: $(@($rebaseBands).Count) band(s) — THIS is what the scenario is scored against." 'Gray'
            }
        }

        # =====================================================================================
        # STEP S6 — TRIGGER THE FAULT, then capture the AFTER window.
        # =====================================================================================
        $afterCount = [Math]::Max($AfterSubWindowCount, [int][Math]::Ceiling($dwell / [double]$SubWindowSeconds))
        $afterSpan  = $afterCount * $SubWindowSeconds
        $TriggerKind = ''

        if ($lever -eq 'http') {
            $TriggerKind = 'sustained host HTTP load (about 10 parallel requesters, non-health routes)'
            $LoadActualSeconds = $LoadRampSeconds + $afterSpan + $ExportTrailSeconds
            Write-Phase "STEP S6: TRIGGER — sustained HTTP load for ${LoadActualSeconds}s (ramp ${LoadRampSeconds}s + window ${afterSpan}s + export trail ${ExportTrailSeconds}s)"
            Write-Phase "  the load spans the WHOLE after window plus its trail, because a burst is invisible to a 60 s-sampled gauge." 'Gray'
            $loadJobs = @(Start-Phase88HttpLoad -Shape 'sustained' -DurationSeconds $LoadActualSeconds -BaseUri $api)
            $LoadShape = 'sustained'
            $faultStart = [datetime]::UtcNow
            $FaultStartUtc = $faultStart.ToString('o')
            Start-Sleep -Seconds $LoadRampSeconds
            $afterStart = [datetime]::UtcNow
            $afterEnd   = $afterStart.AddSeconds($afterSpan)
            Write-Phase "  after window $($afterStart.ToString('o')) -> $($afterEnd.ToString('o')); holding the load across it plus ${ExportTrailSeconds}s..." 'Gray'
            Start-Sleep -Seconds ($afterSpan + $ExportTrailSeconds)
            $HostLoadRequests = Stop-Phase88HttpLoad -Jobs $loadJobs
            $loadJobs = @()
            $FaultEndUtc = ([datetime]::UtcNow).ToString('o')
            Write-Phase "  load stopped; ~$HostLoadRequests requests issued." 'Gray'
        }
        elseif ($lever -eq 'scale') {
            $TriggerKind = "whole-tier scale-0 of '$($scenario.targetTier)' for ${dwell}s"
            Write-Phase "STEP S6: TRIGGER — $TriggerKind"
            $scaledTier = "$($scenario.targetTier)"
            $replicasBefore = Get-LiveReplicas -Tier $scaledTier
            $sf = Invoke-TierScaleFault -Tier $scaledTier -DwellSeconds $dwell
            $ScaleFaultResult = $sf
            $RolloutOldInstanceIds = [string[]]@(@($RolloutOldInstanceIds) + @($sf.PodNamesBefore))
            $RolloutNewInstanceIds = [string[]]@(@($RolloutNewInstanceIds) + @($sf.PodNamesAfter))
            if ([string]::IsNullOrWhiteSpace("$RolloutUtc")) { $RolloutUtc = "$($sf.RestoredUtc)" }
            $FaultStartUtc = "$($sf.FaultStartUtc)"
            $FaultEndUtc   = "$($sf.FaultEndUtc)"
            if (-not $sf.Ok) {
                Write-Phase "the scale fault failed as INFRASTRUCTURE: $($sf.Detail)" 'Red'
                exit 60
            }
            $ScaledTierName           = "$($sf.Tier)"
            $ScaledReplicasBefore = [int]$sf.ReplicasBefore
            $ScaledReplicasAfter  = [int]$sf.ReplicasAfter
            $scaledTier = ''   # the sequencer restored it and CLAIMED the restore; no teardown scale is owed
            $afterEnd   = [datetime]::Parse("$($sf.FaultEndUtc)", $null, [System.Globalization.DateTimeStyles]::AdjustToUniversal -bor [System.Globalization.DateTimeStyles]::AssumeUniversal)
            $afterStart = $afterEnd.AddSeconds(-$afterSpan)
            Write-Phase "  waiting ${ExportTrailSeconds}s so the fault window is fully exported..." 'Gray'
            Start-Sleep -Seconds $ExportTrailSeconds

            # ---- DID THE PIPELINE ACTUALLY COME BACK? -------------------------------------------
            # Scaling the orchestrator to zero also stops the Quartz cron that drives v8-fanout-proof.
            # If the cron does not resume, every later reading measures an IDLE stack rather than a
            # RECOVERED one, and nothing in the artifact would say so. Measured through the SAME
            # datasource proxy the panels read, over the window that has just elapsed since restore.
            try {
                $resumeEnd   = ([DateTimeOffset]::UtcNow).ToUnixTimeSeconds()
                $resumeStart = $resumeEnd - $ExportTrailSeconds
                $resumeMean  = Get-ProxyAggregateMean 'sum(rate(orchestrator_messages_sent_total[240s]))' $resumeStart $resumeEnd 60
                if ($null -ne $resumeMean -and [double]$resumeMean -gt 0) {
                    $TrafficResumedAfterRestore = $true
                    $TrafficResumeDetail = ("pipeline traffic CONFIRMED resumed after the restore: sum(rate(orchestrator_messages_sent_total[240s])) averaged {0:N4} ops/s over the {1}s export trail that followed the restore, measured through the same datasource proxy the panels read. Without this check the post-fault state could not be told apart from an idle stack." -f [double]$resumeMean, $ExportTrailSeconds)
                } else {
                    $TrafficResumedAfterRestore = $false
                    $TrafficResumeDetail = "pipeline traffic did NOT measurably resume in the ${ExportTrailSeconds}s after the restore (orchestrator send rate read '$resumeMean'). Recorded as a finding rather than assumed away."
                    $Findings += $TrafficResumeDetail
                }
            } catch {
                $TrafficResumedAfterRestore = $null
                $TrafficResumeDetail = "the traffic-resumption check could not be evaluated: $($_.Exception.Message)"
            }
            Write-Phase "  $TrafficResumeDetail" $(if ($TrafficResumedAfterRestore) { 'Gray' } else { 'Yellow' })
        }
        else {
            # `seam` — the seam is already armed, so the trigger is the WORKLOAD that exercises it.
            $TriggerKind = "workload drive with the seam armed for ${dwell}s"
            Write-Phase "STEP S6: TRIGGER — $TriggerKind"
            $wfRaw = ''
            try { $wfRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'" } catch { }
            foreach ($line in @(("$wfRaw") -split "`r?`n")) {
                $tw = ("$line").Trim()
                if ($tw -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') { $TrafficWorkflowId = $tw; break }
            }
            if ([string]::IsNullOrWhiteSpace($TrafficWorkflowId)) {
                Write-Phase "could not resolve the fan-out workflow id — the seam has nothing to be exercised by. Aborting." 'Red'; exit 50
            }
            $startResp = Invoke-DriverApi -Method 'POST' -Path '/api/v1/orchestration/start' -Body (ConvertTo-Json @($TrafficWorkflowId))
            if ($startResp.Status -ne 204) {
                Write-Phase "activation gate failed — expected 204, got $($startResp.Status). Aborting." 'Red'; exit 50
            }
            $faultStart = [datetime]::UtcNow
            $FaultStartUtc = $faultStart.ToString('o')
            $afterStart = $faultStart
            $afterEnd   = $afterStart.AddSeconds($afterSpan)
            Start-Sleep -Seconds ($afterSpan + $ExportTrailSeconds)
            $FaultEndUtc = ([datetime]::UtcNow).ToString('o')
        }

        Write-Phase "STEP S6: after capture"
        $afterCap = Invoke-Phase88Capture -PanelIdList $capturePanels -EndUtc $afterEnd `
                      -Count $afterCount -Panel4Count ([Math]::Max(1, [int][Math]::Floor($afterSpan / $Panel4SubWindowSeconds))) `
                      -ShotDir $shotDir -Label 'after'
        $ScenarioScreenshotPaths = @(@($ScenarioScreenshotPaths) + @($afterCap.ScreenshotPaths) | Select-Object -Unique)
        if ("$($afterCap.State)" -eq 'ReaderMissing') {
            $readerErr = if ($null -ne $afterCap.Batch) { "$($afterCap.Batch.Error)" } else { 'no batch was produced' }
            Write-Phase "the panel reader is unavailable: $readerErr. Aborting." 'Red'; exit 63
        }
        if ("$($afterCap.State)" -ne 'Ok') {
            Write-Phase "  the after batch did not complete cleanly ($($afterCap.State)) — panels without a full sample set are declared unevaluable." 'Yellow'
            $ReaderUnavailable = $true
        }

        # ---- STEP S6b — PANEL 4's WIDE-WINDOW LEVEL, RECORDED AND NEVER SCORED ---------------
        # Panel 4 is `increase(...[$__range])`, so its value depends on the VISIBLE window width.
        # The number scored above is a per-120 s increase against a 120 s band. THIS is the number an
        # operator actually sees on the shipped dashboard at its default range — and it grows on a
        # HEALTHY stack too, which is precisely why applying a Regime-A band to it would be a
        # guaranteed false positive. It is recorded for the HAND-02 misleading-by-default list and
        # takes no part in any Moved computation.
        if (@($capturePanels) -contains '4') {
            Write-Phase "STEP S6b: panel 4 wide-window range-cumulative level (${RangeCumulativeWindowSeconds}s, recorded NOT scored)"
            try {
                $wideWins  = @(Get-PinnedWindowSeries -EndUtc $afterEnd -SubWindowSeconds $RangeCumulativeWindowSeconds -Count 1)
                $wideBatch = Invoke-PanelReadBatch -PanelIds @('4') -Windows $wideWins -ScreenshotDir $shotDir `
                               -LocatorMode $LocatorMode -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight `
                               -BasicAuthBase64 $adminB64
                $wideVals = @(Get-PanelSamples -Readings @($wideBatch.Readings) -PanelId '4')
                if (@($wideVals).Count -gt 0) {
                    $RangeCumulativeLevelAfter = [double]$wideVals[0]
                    Write-Phase "  panel 4 wide level = $RangeCumulativeLevelAfter over $($wideWins[0].FromUtc) -> $($wideWins[0].ToUtc) (NOT scored)" 'Gray'
                } else {
                    Write-Phase "  panel 4 wide capture produced no numeric value (state recorded)." 'Yellow'
                }
                $ScenarioScreenshotPaths = @(@($ScenarioScreenshotPaths) + @(Get-ScreenshotPaths $wideBatch.Readings) | Select-Object -Unique)
            } catch {
                Write-Phase "  the wide panel-4 capture failed: $($_.Exception.Message)" 'Yellow'
            }
        }

        # ---- STEP S6c — OBSERVED-ONLY PANELS -------------------------------------------------
        # Recorded so a claim ABOUT them is a measurement. Never scored, never a control.
        foreach ($o in @($observeOnly)) {
            $rdObs = Get-Phase88PanelReading -Capture $afterCap -PanelId $o
            $obsSeries = @()
            foreach ($e in @($rdObs.Entries)) {
                $obsSeries += [pscustomobject]@{
                    SeriesIndex = [int]$e.Index
                    SeriesName  = "$($e.Name)"
                    AfterValues = [double[]]@($e.Values)
                }
            }
            $ObservedPanels += [pscustomobject]@{
                PanelId          = [int]$o
                PanelTitle       = "$($Panels[$o].Title)"
                Regime           = "$($Panels[$o].Regime)"
                SubWindowSeconds = [int]$rdObs.SubWindowSeconds
                AfterStates      = [string[]]@($rdObs.States)
                SeriesResults    = @($obsSeries)
                Scored           = $false
                Note             = 'OBSERVED ONLY — recorded so the scenario can state what this panel did without asserting it. It is neither a subject nor a cross-talk control.'
            }
            Write-Phase ("  observed panel {0}: [{1}]" -f $o, (@($obsSeries | ForEach-Object { "$($_.SeriesName)=$((@($_.AfterValues) -join ','))" }) -join ' | ')) 'Gray'
        }

        # =====================================================================================
        # STEP S7 — RESTORE AND DISARM, THEN ASSERT. Done BEFORE scoring so the stack is never held
        # in a faulted state while numbers are crunched.
        # =====================================================================================
        Write-Phase "STEP S7: restore, disarm, assert"
        if ($LoadShape -ne 'none' -and @($loadJobs).Count -gt 0) {
            $HostLoadRequests += Stop-Phase88HttpLoad -Jobs $loadJobs
            $loadJobs = @()
        }
        if ($seamArmed2) {
            $null = Clear-Phase88Seam -Tier $seamTier2 -Name $seamVar2
            $seamArmed2 = $false
        }
        if ($seamArmed) {
            $null = Clear-Phase88Seam -Tier $seamTier -Name $seamVar
            $seamArmed = $false
        }
        $restore = Assert-StackRestored -Tiers $Tiers -ExpectedReplicas $preReplicas -ExpectedImages $preImages
        foreach ($t in $Tiers) { $ReplicasAfterMap[$t] = Get-LiveReplicas -Tier $t }
        # The scaled tier's count is re-read HERE, independently of the sequencer's own claim, so the
        # artifact's scalar ReplicasAfter is a fresh live read rather than an echo of the value the
        # mutation reported. This is the T-88-02 control against the stale phase-80 replica map, whose
        # `orchestrator = 1` would silently amputate the Phase-83 HA tier.
        if (-not [string]::IsNullOrWhiteSpace($ScaledTierName)) {
            $ScaledReplicasAfter = [int](Get-LiveReplicas -Tier $ScaledTierName)
            Write-Phase "  scaled tier '$ScaledTierName': before=$ScaledReplicasBefore after=$ScaledReplicasAfter (re-read live)" 'Gray'
        }
        Write-Phase "  restore: SeamVarsClean=$($restore.SeamVarsClean) ReplicasRestored=$($restore.ReplicasRestored) ImagesUnchanged=$($restore.ImagesUnchanged)" 'Gray'

        # =====================================================================================
        # STEP S8 — SCORE THE ASSERTED PANELS.
        # =====================================================================================
        Write-Phase "STEP S8: scoring $(@($subjects).Count) asserted panel(s) against $ScoringBandSource"
        $AnyPanelFailed = $false
        foreach ($p in $subjects) {
            $panel = $Panels[$p]
            $rdAfter = Get-Phase88PanelReading -Capture $afterCap -PanelId $p
            $rdBase  = $preArmReadings[$p]
            $stateAfter = if (@($rdAfter.States).Count -gt 0) { (@($rdAfter.States | Select-Object -Unique) -join '|') } else { 'NoReading' }
            $dirToken = "$($scenario.predictedDirection[$p])"

            if (-not $rdAfter.Readable) {
                $UnevaluablePanels += "panel ${p} ($($panel.Title)): the reader returned NO reading at all in the after window — an UNEVALUATED assertion, not a failed one"
                $ReaderUnavailable = $true
                Write-Phase "  panel $p : NO READING" 'Red'
                continue
            }

            # 'slope-up' is Regime C's form of 'up' on the per-window band; 'up-then-nodata' is TWO
            # assertions over one capture (panel 5 spikes while the stale series is still present,
            # then empties when it expires) and either satisfies it.
            #
            # 'down' INCLUDES 'nodata' by the plan's own definition. 88-06 states it verbatim for
            # SCALE-02 — "both legend Means fall below their DISC-01 band ... OR that the panel
            # reaches NoData — EITHER is the predicted `down` direction" — and again for SCALE-01's
            # panel 3, "down (toward 0 / NoData)". A rate panel whose tier is gone stops rendering
            # once fewer than two samples remain inside the 240 s lookback; refusing to score that as
            # a fall would be the phase's sixth pitfall applied to a panel other than 9.
            $valueDirections = switch ($dirToken) {
                'up'             { @('up') }
                'down'           { @('down', 'nodata') }
                'nonzero'        { @('nonzero') }
                'slope-up'       { @('up') }
                'up-then-nodata' { @('up', 'nodata') }
                'nodata'         { @('nodata') }
                default          { @() }
            }
            if (@($valueDirections).Count -eq 0) {
                $UnevaluablePanels += "panel ${p} ($($panel.Title)): predicted direction '$dirToken' has no scoring rule"
                continue
            }

            $seriesResults = @()
            $anySeriesMoved = $false
            $anyFalsifiable = $false
            foreach ($e in @($rdAfter.Entries)) {
                $band = Find-Phase88Band -Bands $ScoringBands -PanelId ([int]$p) -SeriesName "$($e.Name)" -SeriesIndex ([int]$e.Index)
                if ($null -eq $band) {
                    # A series that did not exist at baseline. It is a RECORDED finding — a new
                    # legend row is itself a discrimination signal — but it is never scored as
                    # movement, because there is no null hypothesis it could have moved away from.
                    $NewSeriesAfter += "panel ${p} series '$($e.Name)' (row $($e.Index)) has NO baseline band — new series, recorded as a finding, not scored"
                    $seriesResults += [pscustomobject]@{
                        SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"
                        BandLow = $null; BandHigh = $null; BandSampleCount = 0
                        AfterValues = [double[]]@($e.Values)
                        Moved = $false; ConsecutiveSamplesOutside = 0
                        Falsifiable = $false
                        Note = 'no baseline band: this series did not exist in the scoring baseline'
                    }
                    continue
                }
                $bandObj = [pscustomobject]@{ Low = [double]$band.BandLow; High = [double]$band.BandHigh }
                $thin = ([int]$band.SampleCount -lt $MinBandSamples)
                $best = $null
                # WHICH direction carried it must be recorded. A compound token like 'up-then-nodata'
                # is satisfied by EITHER half, and an artifact that echoes the compound token back as
                # the "observed" direction would hide which half actually happened — exactly the
                # question panel 5 exists to answer.
                $bestDir = ''
                foreach ($d in $valueDirections) {
                    $m = Test-PanelMoved -Band $bandObj -AfterValues ([double[]]@($e.Values)) `
                           -AfterStates ([string[]]@($rdAfter.States)) -Direction $d -MinConsecutive 2
                    if ($null -eq $best -or [int]$m.ConsecutiveSamplesOutside -gt [int]$best.ConsecutiveSamplesOutside) { $best = $m; $bestDir = $d }
                    if ($m.Moved) { $best = $m; $bestDir = $d; break }
                }
                if (-not $thin) { $anyFalsifiable = $true }
                if ($best.Moved) { $anySeriesMoved = $true }
                $seriesResults += [pscustomobject]@{
                    SeriesIndex               = [int]$e.Index
                    SeriesName                = "$($e.Name)"
                    BandLow                   = [double]$band.BandLow
                    BandHigh                  = [double]$band.BandHigh
                    BandSampleCount           = [int]$band.SampleCount
                    BandSubWindowSeconds      = [int]$band.SubWindowSeconds
                    AfterValues               = [double[]]@($e.Values)
                    Moved                     = [bool]$best.Moved
                    MovedDirection            = if ($best.Moved) { $bestDir } else { '' }
                    ConsecutiveSamplesOutside = [int]$best.ConsecutiveSamplesOutside
                    Falsifiable               = (-not $thin)
                    Note                      = if ($thin) { "band computed from only $($band.SampleCount) sample(s) — too thin to falsify a non-move" }
                                                elseif ($best.Moved -and $bestDir -eq 'nodata') { 'carried by the PANEL-LEVEL NoData state, not by this series'' values — the panel emptied' }
                                                else { '' }
                }
            }

            # ---- PANEL-LEVEL NoData ---------------------------------------------------------
            # A panel that renders "No data" has NO legend series, so the per-series loop above never
            # executes and a purely series-based scorer would report "nothing moved" for a panel that
            # emptied completely. That is the phase's sixth pitfall in its most damaging form: on
            # panel 9 — deliberately UNGUARDED so a dead keeper is visible rather than comfortable —
            # NoData IS the discrimination signal. It is therefore evaluated against the panel's own
            # STATES, independently of any band, whenever the predicted direction admits it.
            # Computed for EVERY panel, whatever its predicted direction, so PanelStateNoDataRun is
            # always a stated measurement rather than a zero that could mean "no run" or "not checked".
            $PanelStateMove = Test-PanelMoved -Band ([pscustomobject]@{ Low = 0.0; High = 0.0 }) `
                                -AfterValues ([double[]]@()) -AfterStates ([string[]]@($rdAfter.States)) `
                                -Direction 'nodata' -MinConsecutive 2
            $PanelStateMoveApplied = $false
            if (@($valueDirections) -contains 'nodata') {
                if ($PanelStateMove.Moved) {
                    $PanelStateMoveApplied = $true
                    $anySeriesMoved = $true
                    $anyFalsifiable = $true
                    $seriesResults += [pscustomobject]@{
                        SeriesIndex               = -2
                        SeriesName                = '(panel state)'
                        BandLow                   = $null
                        BandHigh                  = $null
                        BandSampleCount           = 0
                        BandSubWindowSeconds      = [int]$rdAfter.SubWindowSeconds
                        AfterValues               = [double[]]@()
                        Moved                     = $true
                        ConsecutiveSamplesOutside = [int]$PanelStateMove.ConsecutiveSamplesOutside
                        Falsifiable               = $true
                        Note                      = "PANEL-LEVEL NoData: the panel rendered no series at all for $($PanelStateMove.ConsecutiveSamplesOutside) consecutive sub-window(s). The discrimination signal is the panel STATE, not a series value — scored as MOVED, not as a read failure."
                    }
                }
            }

            # ---- DID THE PREDICTION HOLD? ----------------------------------------------------
            # When the predicted direction produced nothing, the OTHER directions are evaluated too —
            # not to manufacture a pass, but because "the panel left its healthy band under this
            # fault" and "it left it the way we guessed" are two different claims and the artifact
            # must be able to state them separately. A panel that moved the other way HAS
            # discriminated (DISC-02), and the wrong prediction is a loud FINDING that degrades the
            # verdict to Inconclusive rather than being quietly absorbed into a Pass.
            # The observed direction is the one that ACTUALLY carried the movement — the winning
            # per-series direction, or 'nodata' when the panel-level state carried it — never the
            # compound token echoed back. For 'up-then-nodata' this is the whole point: the token is
            # satisfied by either half, and only this field says which half the stack produced.
            $ObservedDirection = ''
            if ($anySeriesMoved) {
                $winners = @($seriesResults | Where-Object { $_.Moved } |
                    Sort-Object -Property ConsecutiveSamplesOutside -Descending)
                if (@($winners).Count -gt 0 -and @(Get-PropertyNames $winners[0]) -contains 'MovedDirection' -and
                    -not [string]::IsNullOrWhiteSpace("$($winners[0].MovedDirection)")) {
                    $ObservedDirection = "$($winners[0].MovedDirection)"
                } elseif ($PanelStateMoveApplied) {
                    $ObservedDirection = 'nodata'
                } else {
                    $ObservedDirection = $dirToken
                }
            }
            # A COMPOUND token is satisfied by either half — the plan says so explicitly for panel 5
            # ("counts as moved if EITHER segment shows the predicted behaviour; record which") — so
            # observing only one half is NOT a corrected prediction. It is recorded, not penalised.
            $PredictionHeld = [bool]$anySeriesMoved
            if (-not $anySeriesMoved) {
                # Panel-level NoData first: a panel that emptied has no series for the loop below.
                if ((@($valueDirections) -notcontains 'nodata') -and $PanelStateMove.Moved) {
                    $ObservedDirection = 'nodata'
                    $anySeriesMoved    = $true
                    $anyFalsifiable    = $true
                    $seriesResults += [pscustomobject]@{
                        SeriesIndex               = -2
                        SeriesName                = '(panel state)'
                        BandLow                   = $null
                        BandHigh                  = $null
                        BandSampleCount           = 0
                        BandSubWindowSeconds      = [int]$rdAfter.SubWindowSeconds
                        AfterValues               = [double[]]@()
                        Moved                     = $true
                        MovedDirection            = 'nodata'
                        ConsecutiveSamplesOutside = [int]$PanelStateMove.ConsecutiveSamplesOutside
                        Falsifiable               = $true
                        Note                      = "MEASURED DIRECTION 'nodata', which is NOT the predicted '$dirToken': the panel EMPTIED for $($PanelStateMove.ConsecutiveSamplesOutside) consecutive sub-window(s) instead of moving its value. The panel discriminated; the prediction did not hold."
                    }
                }
                foreach ($alt in @('down', 'up', 'nonzero')) {
                    if ($anySeriesMoved) { break }
                    if (@($valueDirections) -contains $alt) { continue }
                    foreach ($e in @($rdAfter.Entries)) {
                        $band = Find-Phase88Band -Bands $ScoringBands -PanelId ([int]$p) -SeriesName "$($e.Name)" -SeriesIndex ([int]$e.Index)
                        if ($null -eq $band) { continue }
                        if ([int]$band.SampleCount -lt $MinBandSamples) { continue }
                        $altM = Test-PanelMoved -Band ([pscustomobject]@{ Low = [double]$band.BandLow; High = [double]$band.BandHigh }) `
                                  -AfterValues ([double[]]@($e.Values)) -AfterStates ([string[]]@($rdAfter.States)) `
                                  -Direction $alt -MinConsecutive 2
                        if (-not $altM.Moved) { continue }
                        $ObservedDirection = $alt
                        $anySeriesMoved    = $true
                        $anyFalsifiable    = $true
                        $altEntry = [pscustomobject]@{
                            SeriesIndex               = [int]$e.Index
                            SeriesName                = "$($e.Name)"
                            BandLow                   = [double]$band.BandLow
                            BandHigh                  = [double]$band.BandHigh
                            BandSampleCount           = [int]$band.SampleCount
                            BandSubWindowSeconds      = [int]$band.SubWindowSeconds
                            AfterValues               = [double[]]@($e.Values)
                            Moved                     = $true
                            MovedDirection            = $alt
                            ConsecutiveSamplesOutside = [int]$altM.ConsecutiveSamplesOutside
                            Falsifiable               = $true
                            Note                      = "MEASURED DIRECTION '$alt', which is NOT the predicted '$dirToken'. The panel discriminated; the prediction did not hold."
                        }
                        # REPLACED IN PLACE, never appended. A second entry for the same series would
                        # be counted twice by the STEP S10 diagnostic aggregate — which sums every
                        # SeriesResults[] row's mean — and would manufacture a proxy disagreement out
                        # of nothing but double counting.
                        $repl = @(); $done = $false
                        foreach ($sr in @($seriesResults)) {
                            if (-not $done -and [int]$sr.SeriesIndex -eq [int]$e.Index) { $repl += $altEntry; $done = $true }
                            else { $repl += $sr }
                        }
                        if (-not $done) { $repl += $altEntry }
                        $seriesResults = @($repl)
                        break
                    }
                }
                if ($anySeriesMoved) {
                    $PredictionHeld = $false
                    $corr = "panel ${p} ($($panel.Title)): predicted '$dirToken' but MEASURED '$ObservedDirection'. The panel DID discriminate — its rendered reading left the healthy band for at least 2 consecutive sub-windows — but the direction the plan predicted is not the direction this stack produced. Recorded verbatim; the verdict is degraded to Inconclusive rather than absorbing a wrong model into a Pass."
                    $PredictionsCorrected += $corr
                    $Findings += $corr
                    Write-Phase "  panel $p : MOVED but AGAINST the prediction ($dirToken -> $ObservedDirection)" 'Yellow'
                }
            }

            # ---- PANEL 5's TWO AFTER-SEGMENTS -------------------------------------------------
            # 'up-then-nodata' is TWO behaviours over ONE capture and both must be recorded: the gap
            # SPIKES while the dead tier's stale series still satisfy the binary `-`'s label match,
            # then the panel EMPTIES once fewer than two samples remain inside the lookback and no
            # label set matches at all. The spike is the useful signal and the emptiness is the trap;
            # the trap is a HAND-02 row. Segments are cut from the SAME sub-window series, so no extra
            # capture is taken and the two segments are directly comparable by construction.
            $EarlySegment = $null
            $LateSegment  = $null
            $SegmentBehaviourObserved = ''
            if ($dirToken -eq 'up-then-nodata') {
                $w = [int]$rdAfter.SubWindowSeconds
                if ($w -lt 1) { $w = $SubWindowSeconds }
                $earlyCount = [int][Math]::Min(@($rdAfter.States).Count, [Math]::Ceiling(240.0 / $w))
                $lateStart  = [int][Math]::Min(@($rdAfter.States).Count, [Math]::Floor(300.0 / $w))
                $earlyStates = [string[]]@(@($rdAfter.States) | Select-Object -First $earlyCount)
                $lateStates  = [string[]]@(@($rdAfter.States) | Select-Object -Skip $lateStart)
                $earlySeries = @(); $lateSeries = @()
                foreach ($e in @($rdAfter.Entries)) {
                    $ev = @($e.Values)
                    $earlySeries += [pscustomobject]@{ SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"; Values = [double[]]@(@($ev) | Select-Object -First $earlyCount) }
                    $lateSeries  += [pscustomobject]@{ SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"; Values = [double[]]@(@($ev) | Select-Object -Skip $lateStart) }
                }
                $earlyNoData = (@($earlyStates | Where-Object { "$_" -eq 'NoData' }).Count -gt 0)
                $lateNoData  = (@($lateStates  | Where-Object { "$_" -eq 'NoData' }).Count -gt 0)
                $EarlySegment = [pscustomobject]@{
                    Label            = 'early (the first ~240 s of the outage — the stale series still exist, so the binary `-` still matches and the gap should SPIKE to the sending tier''s full rate)'
                    SubWindowSeconds = $w
                    SubWindowCount   = $earlyCount
                    States           = $earlyStates
                    SeriesValues     = @($earlySeries)
                    ContainsNoData   = $earlyNoData
                }
                $LateSegment = [pscustomobject]@{
                    Label            = 'late (after roughly 300 s — fewer than two samples remain inside the lookback, no label set matches, and the panel should be EMPTY rather than high)'
                    SubWindowSeconds = $w
                    SubWindowCount   = @($lateStates).Count
                    States           = $lateStates
                    SeriesValues     = @($lateSeries)
                    PanelStateAfter  = if (@($lateStates).Count -gt 0) { (@($lateStates | Select-Object -Unique) -join '|') } else { 'NoReading' }
                    ContainsNoData   = $lateNoData
                }
                # THE SPIKE IS TESTED, NOT INFERRED. "Some series moved" is not "the gap spiked":
                # a series scored MOVED by the panel-level NoData half of this very token would
                # otherwise be read back as evidence of a spike, and the artifact would then claim a
                # rise that the recorded values plainly contradict. The test is an explicit UP move,
                # over the EARLY segment's values only, against the same band the panel is scored on.
                $spiked = $false
                $spikeConsecutive = 0
                $spikeSeries = ''
                foreach ($es in @($earlySeries)) {
                    $band = Find-Phase88Band -Bands $ScoringBands -PanelId ([int]$p) -SeriesName "$($es.SeriesName)" -SeriesIndex ([int]$es.SeriesIndex)
                    if ($null -eq $band) { continue }
                    $upM = Test-PanelMoved -Band ([pscustomobject]@{ Low = [double]$band.BandLow; High = [double]$band.BandHigh }) `
                             -AfterValues ([double[]]@($es.Values)) -AfterStates ([string[]]@($earlyStates)) `
                             -Direction 'up' -MinConsecutive 2
                    if ([int]$upM.ConsecutiveSamplesOutside -gt $spikeConsecutive) {
                        $spikeConsecutive = [int]$upM.ConsecutiveSamplesOutside
                        $spikeSeries = "$($es.SeriesName)"
                    }
                    if ($upM.Moved) { $spiked = $true }
                }
                $EarlySegment | Add-Member -NotePropertyName 'SpikeObserved'    -NotePropertyValue $spiked
                $EarlySegment | Add-Member -NotePropertyName 'SpikeConsecutive' -NotePropertyValue $spikeConsecutive
                $EarlySegment | Add-Member -NotePropertyName 'SpikeSeries'      -NotePropertyValue $spikeSeries
                $EarlySegment | Add-Member -NotePropertyName 'SpikeTest'        -NotePropertyValue 'an explicit Direction=up test over the EARLY segment values only, against the same scoring band — never inferred from "some series moved"'
                $SegmentBehaviourObserved =
                    if ($spiked -and $lateNoData) { 'BOTH: the gap spiked while the stale series persisted AND the panel emptied afterwards — the predicted spike-then-empty transition was observed end to end.' }
                    elseif ($spiked)              { 'SPIKE ONLY: the gap rose out of its band while the stale series persisted, and the panel had not emptied by the end of the after window.' }
                    elseif ($lateNoData)          { 'EMPTY WITHOUT SPIKING: the panel went to No data without ever rising out of its band. This is a MEASUREMENT, not a failure — it is carried to the HAND-02 misleading-by-default list verbatim.' }
                    else                          { 'NEITHER: no spike and no emptying were observed in this after window.' }
                Write-Phase "  panel $p segments: $SegmentBehaviourObserved" 'Gray'
            }

            # The panel's SCORED series: the mover with the longest consecutive run, or — when
            # nothing moved — the strongest candidate, so the artifact shows the best evidence there
            # was rather than an arbitrary row.
            $scored = $null
            $movers = @($seriesResults | Where-Object { $_.Moved })
            if (@($movers).Count -gt 0) {
                $scored = @($movers | Sort-Object -Property ConsecutiveSamplesOutside -Descending)[0]
            }
            elseif (@($seriesResults).Count -gt 0) {
                $scored = @($seriesResults | Sort-Object -Property ConsecutiveSamplesOutside -Descending)[0]
            }

            $baseVals   = [double[]]@()
            $baseStates = [string[]]@()
            if ($null -ne $rdBase) {
                $baseStates = [string[]]@($rdBase.States)
                if ($null -ne $scored) {
                    $bm = @($rdBase.Entries | Where-Object { [string]::Equals("$($_.Name)", "$($scored.SeriesName)", [System.StringComparison]::Ordinal) })
                    if (@($bm).Count -eq 0) { $bm = @($rdBase.Entries | Where-Object { [int]$_.Index -eq [int]$scored.SeriesIndex }) }
                    if (@($bm).Count -gt 0) { $baseVals = [double[]]@($bm[0].Values) }
                }
            }

            $panelMoved = $anySeriesMoved
            if (-not $panelMoved -and -not $anyFalsifiable) {
                $UnevaluablePanels += "panel ${p} ($($panel.Title)): every series' band was computed from fewer than $MinBandSamples samples, so a non-move here cannot falsify the prediction — INCONCLUSIVE, not a discrimination failure"
                Write-Phase "  panel $p : no falsifiable band — Inconclusive, not Fail" 'Yellow'
            }
            elseif (-not $panelMoved) {
                $AnyPanelFailed = $true
                $Findings += "panel ${p} ($($panel.Title)): READ successfully but did NOT move '$dirToken' for 2 consecutive samples in any series — a read panel that did not move is a FAIL, not an Inconclusive"
                Write-Phase "  panel $p : DID NOT MOVE ($dirToken)" 'Red'
            }
            else {
                Write-Phase ("  panel {0}: MOVED '{1}' on series '{2}' — {3} consecutive sample(s) outside {4:N4}..{5:N4}" -f `
                    $p, $dirToken, $scored.SeriesName, $scored.ConsecutiveSamplesOutside, $scored.BandLow, $scored.BandHigh) 'Green'
            }

            $PanelResults += [pscustomobject]@{
                PanelId                   = [int]$p
                PanelTitle                = "$($panel.Title)"
                Regime                    = "$($panel.Regime)"
                SeriesName                = if ($null -ne $scored) { "$($scored.SeriesName)" } else { '' }
                ScoredSeriesIndex         = if ($null -ne $scored) { [int]$scored.SeriesIndex } else { -99 }
                PredictedDirection        = $dirToken
                ObservedDirection         = $ObservedDirection
                PredictionHeld            = $PredictionHeld
                PanelStateMoveApplied     = [bool]$PanelStateMoveApplied
                PanelStateNoDataRun       = if ($null -ne $PanelStateMove) { [int]$PanelStateMove.ConsecutiveSamplesOutside } else { 0 }
                PanelStateNoDataNote      = 'PanelStateNoDataRun is measured for EVERY panel regardless of its predicted direction, so a 0 here means "no consecutive NoData run was observed" and never "nobody checked". PanelStateMoveApplied says whether that run is what carried the movement.'
                EarlyAfterSegment         = $EarlySegment
                LateAfterSegment          = $LateSegment
                SegmentBehaviourObserved  = $SegmentBehaviourObserved
                BandLow                   = if ($null -ne $scored) { $scored.BandLow } else { $null }
                BandHigh                  = if ($null -ne $scored) { $scored.BandHigh } else { $null }
                BandSource                = $ScoringBandSource
                SubWindowSeconds          = [int]$rdAfter.SubWindowSeconds
                BaselineValues            = $baseVals
                BaselineStates            = $baseStates
                AfterValues               = if ($null -ne $scored) { [double[]]@($scored.AfterValues) } else { [double[]]@() }
                AfterStates               = [string[]]@($rdAfter.States)
                Moved                     = [bool]$panelMoved
                ConsecutiveSamplesOutside = if ($null -ne $scored) { [int]$scored.ConsecutiveSamplesOutside } else { 0 }
                PanelStateAfter           = $stateAfter
                LegendNamesBaseline       = if ($null -ne $rdBase) { [string[]]@(Get-Phase88LegendNames -Reading $rdBase) } else { [string[]]@() }
                LegendNamesAfter          = [string[]]@(Get-Phase88LegendNames -Reading $rdAfter)
                SeriesResults             = @($seriesResults)
                SeriesMovedRule           = 'a multi-series panel MOVED when at least one series moved in the predicted direction for >= 2 consecutive samples; every series'' own outcome is in SeriesResults[] so the selection is auditable'
            }
        }

        # =====================================================================================
        # STEP S9 — SCORE THE CROSS-TALK CONTROLS, in the SAME windows as the claim (DISC-03).
        #
        # StayedPut is STRICT: inside the band for the ENTIRE window, not "mostly". A drifting
        # control is a FINDING — either the fault has a wider blast radius than designed or a panel
        # is wired to the wrong metric, which is exactly what the control exists to catch — and it is
        # recorded WITH its MaxExcursion and its pre-fault reading, so a wobble and a real blast
        # radius are distinguishable rather than collapsed into one boolean.
        # =====================================================================================
        Write-Phase "STEP S9: cross-talk controls [$(@($controls) -join ',')]"
        $CrossTalkHeld = $true
        foreach ($c in $controls) {
            $rdAfter = Get-Phase88PanelReading -Capture $afterCap -PanelId $c
            $rdBase  = $preArmReadings[$c]
            $stateAfter = if (@($rdAfter.States).Count -gt 0) { (@($rdAfter.States | Select-Object -Unique) -join '|') } else { 'NoReading' }

            $ctSeries = @()
            $panelStayed = $true
            $maxExc = 0.0
            $worst = $null
            $preStayed = $true
            foreach ($e in @($rdAfter.Entries)) {
                $band = Find-Phase88Band -Bands $ScoringBands -PanelId ([int]$c) -SeriesName "$($e.Name)" -SeriesIndex ([int]$e.Index)
                if ($null -eq $band) {
                    $panelStayed = $false
                    $Findings += "cross-talk panel ${c} ($($Panels[$c].Title)) series '$($e.Name)': NO baseline band — an unbanded control cannot be claimed to have held"
                    $ctSeries += [pscustomobject]@{
                        SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"
                        BandLow = $null; BandHigh = $null; AfterValues = [double[]]@($e.Values)
                        StayedPut = $false; MaxExcursion = $null; SamplesOutside = 0
                        Note = 'no baseline band for this series'
                    }
                    continue
                }
                $lo = [double]$band.BandLow; $hi = [double]$band.BandHigh
                $sExc = 0.0; $sOut = 0; $sRun = 0; $sBestRun = 0
                foreach ($v in @($e.Values)) {
                    $x = Get-Phase88Excursion -Value ([double]$v) -Low $lo -High $hi
                    if ($x -gt 0) { $sOut++; $sRun++; if ($sRun -gt $sBestRun) { $sBestRun = $sRun } } else { $sRun = 0 }
                    if ($x -gt $sExc) { $sExc = $x }
                }
                $sStayed = ($sOut -eq 0)
                if (-not $sStayed) { $panelStayed = $false }
                if ($sExc -gt $maxExc) { $maxExc = $sExc }
                $entry = [pscustomobject]@{
                    SeriesIndex               = [int]$e.Index
                    SeriesName                = "$($e.Name)"
                    BandLow                   = $lo
                    BandHigh                  = $hi
                    BandSampleCount           = [int]$band.SampleCount
                    AfterValues               = [double[]]@($e.Values)
                    StayedPut                 = $sStayed
                    MaxExcursion              = $sExc
                    SamplesOutside            = $sOut
                    ConsecutiveSamplesOutside = $sBestRun
                    Note                      = ''
                }
                $ctSeries += $entry
                if ($null -eq $worst -or $sExc -ge [double]$worst.MaxExcursion) { $worst = $entry }

                # The SAME series read before the fault. A control that had already drifted before
                # anything was driven is a BASELINE-DRIFT finding, not a blast-radius one, and the
                # artifact must let a reader tell those two apart.
                if ($null -ne $rdBase) {
                    $bm = @($rdBase.Entries | Where-Object { [string]::Equals("$($_.Name)", "$($e.Name)", [System.StringComparison]::Ordinal) })
                    foreach ($bv in @($bm)) {
                        foreach ($v in @($bv.Values)) {
                            if ((Get-Phase88Excursion -Value ([double]$v) -Low $lo -High $hi) -gt 0) { $preStayed = $false }
                        }
                    }
                }
            }
            if (-not $panelStayed) {
                $CrossTalkHeld = $false
                $Findings += ("cross-talk panel {0} ({1}) DRIFTED outside its band (max excursion {2:N4}); pre-fault reading of the same panel {3} — recorded as a finding, not suppressed" -f `
                    $c, $Panels[$c].Title, $maxExc, $(if ($preStayed) { 'was INSIDE its band, so the drift coincides with the fault window' } else { 'was ALREADY outside its band, so this is baseline drift rather than blast radius' }))
                Write-Phase ("  panel {0}: DRIFTED (max excursion {1:N4})" -f $c, $maxExc) 'Red'
            }
            else {
                Write-Phase ("  panel {0}: stayed put (max excursion {1:N4})" -f $c, $maxExc) 'Gray'
            }

            $CrossTalkResults += [pscustomobject]@{
                PanelId          = [int]$c
                PanelTitle       = "$($Panels[$c].Title)"
                Applicable       = $true
                BandLow          = if ($null -ne $worst) { $worst.BandLow } else { $null }
                BandHigh         = if ($null -ne $worst) { $worst.BandHigh } else { $null }
                BandSource       = $ScoringBandSource
                SubWindowSeconds = [int]$rdAfter.SubWindowSeconds
                AfterValues      = if ($null -ne $worst) { [double[]]@($worst.AfterValues) } else { [double[]]@() }
                StayedPut        = $panelStayed
                MaxExcursion     = $maxExc
                PreFaultStayedPut = $preStayed
                PanelStateAfter  = $stateAfter
                SeriesResults    = @($ctSeries)
            }
        }

        # =====================================================================================
        # STEP S10 — THE DIAGNOSTIC PROXY VALUE, recorded BESIDE the rendered value (T-88-12).
        # The rendered panel remains the verdict; a disagreement is a FINDING to record loudly, not
        # an error to reconcile away. This is the direct control against the Phase-87 defect where
        # 30/30 query checks reported green against three panels rendering "No data".
        # =====================================================================================
        Write-Phase "STEP S10: diagnostic proxy cross-check (diagnosis only)"
        $afterStartUnix = ([System.DateTimeOffset]::new($afterStart.ToUniversalTime(), [TimeSpan]::Zero)).ToUnixTimeSeconds()
        $afterEndUnix   = ([System.DateTimeOffset]::new($afterEnd.ToUniversalTime(), [TimeSpan]::Zero)).ToUnixTimeSeconds()
        $riScenario     = if ($null -ne $ScenarioRateInterval) { [int]$ScenarioRateInterval } else { 240 }
        try {
            $dashJson = Get-Content (Join-Path $repoRoot 'k8s/dashboards/business.json') -Raw | ConvertFrom-Json
            foreach ($dp in @($dashJson.panels)) {
                $dpNames = @(Get-PropertyNames $dp)
                if ($dpNames -notcontains 'id' -or $dpNames -notcontains 'targets') { continue }
                $dpId = "$($dp.id)"
                if (@($subjects) -notcontains $dpId) { continue }
                $comparable = $true; $reason = ''; $queries = @(); $proxyMean = $null
                # $__range must be substituted with the width the PANEL was actually read at. Panel 4
                # is read in its own 120 s batch (88-04 measured that it renders "No data" at 60 s),
                # so substituting the 60 s sub-window here would compare the rendered value against a
                # proxy computed over half its window and manufacture a false disagreement.
                $dpRangeSeconds = if ($dpId -eq '4') { $Panel4SubWindowSeconds } else { $SubWindowSeconds }
                foreach ($tg in @($dp.targets)) {
                    $expr = "$($tg.expr)"
                    if ($expr -match 'histogram_quantile') {
                        $comparable = $false
                        $reason = 'histogram_quantile is a per-series quantile; summing across series is not a meaningful aggregate, so no agreement is asserted'
                    }
                    $q = $expr.Replace('$__rate_interval', "${riScenario}s").Replace('$__range', "${dpRangeSeconds}s")
                    $q = $q.Replace('$source', '.*').Replace('$pod', '.*')
                    $queries += $q
                    try {
                        $m = Get-ProxyAggregateMean $q $afterStartUnix $afterEndUnix $SubWindowSeconds
                        if ($null -ne $m) {
                            if ($null -eq $proxyMean) { $proxyMean = 0.0 }
                            $proxyMean += [double]$m
                        }
                    } catch {
                        $reason = "proxy query failed: $($_.Exception.Message)"
                        $comparable = $false
                    }
                }
                # The rendered side of the comparison: the SUM ACROSS SERIES of each series' own mean
                # over the after window — the same shape the proxy aggregate produces (all series
                # summed per timestamp, then averaged over timestamps).
                $panelSum = $null
                $mine = @($PanelResults | Where-Object { [int]$_.PanelId -eq [int]$dpId })
                if (@($mine).Count -gt 0) {
                    $panelSum = 0.0
                    foreach ($sr in @($mine[0].SeriesResults)) {
                        $sv = @($sr.AfterValues)
                        if ($sv.Count -eq 0) { continue }
                        $acc = 0.0
                        foreach ($v in $sv) { $acc += [double]$v }
                        $panelSum += ($acc / $sv.Count)
                    }
                } else {
                    $comparable = $false
                    $reason = (("$reason " + 'the panel produced no scored result, so there is nothing to compare the proxy value against').Trim())
                }
                $agrees = $null
                if ($comparable -and $null -ne $proxyMean -and $null -ne $panelSum) {
                    $tol = [Math]::Max(0.20 * [Math]::Abs([double]$panelSum), 0.05)
                    $agrees = ([Math]::Abs([double]$proxyMean - [double]$panelSum) -le $tol)
                    if (-not $agrees) {
                        $DiagnosticDisagreements += "panel ${dpId} ($($Panels[$dpId].Title)): rendered series-mean sum $panelSum vs proxy aggregate mean $proxyMean (tolerance $tol) — the RENDERED value remains the verdict; recorded as a finding"
                    }
                }
                $DiagnosticEntries += [pscustomobject]@{
                    PanelId      = [int]$dpId
                    TargetCount  = @($dp.targets).Count
                    Queries      = [string[]]$queries
                    ProxyMean    = $proxyMean
                    PanelMeanSum = $panelSum
                    Comparable   = [bool]$comparable
                    Agrees       = $agrees
                    Reason       = ("$reason" -replace 'System\.Object\[\]', 'System.Object-array')
                }
                if ($null -eq $DiagnosticQueryValue -and $null -ne $proxyMean) { $DiagnosticQueryValue = [double]$proxyMean }
                Write-Phase ("  panel {0}: proxy={1} panel={2} comparable={3} agrees={4}" -f $dpId, $proxyMean, $panelSum, $comparable, $agrees) 'Gray'
            }
        } catch {
            Write-Phase "  the diagnostic cross-check could not be built: $($_.Exception.Message)" 'Yellow'
        }
        $cmpEntries = @($DiagnosticEntries | Where-Object { $_.Comparable -and $null -ne $_.Agrees })
        if (@($cmpEntries).Count -gt 0) {
            $DiagnosticAgreesWithPanel = (@($cmpEntries | Where-Object { -not $_.Agrees }).Count -eq 0)
        }

        # =====================================================================================
        # STEP S11 — VERDICT AND ARTIFACT. The artifact is written FIRST and the exit code is
        # resolved from the SAME in-memory object, so an artifact can never disagree with the code
        # the process returned.
        # =====================================================================================
        $subjectsScored = @($PanelResults | ForEach-Object { "$($_.PanelId)" })
        foreach ($p in $subjects) {
            if ($subjectsScored -contains "$p") { continue }
            if (@($UnevaluablePanels | Where-Object { "$_" -match "^panel $p[ :(]" }).Count -gt 0) { continue }
            $UnevaluablePanels += "panel ${p} ($($Panels[$p].Title)): neither scored nor declared unevaluable"
        }

        $AllMoved = ((@($PanelResults).Count -gt 0) -and (@($PanelResults | Where-Object { -not $_.Moved }).Count -eq 0))

        # PRECEDENCE: Fail BEATS Inconclusive. A dirty stack, a panel that was read and did not move,
        # and a control that drifted are all claims that were EVALUATED and came back false — positive
        # evidence — and no quantity of UNEVALUATED claims makes them less true.
        $verdict = 'Pass'
        if (-not $restore.Ok -or $AnyPanelFailed -or -not $CrossTalkHeld) { $verdict = 'Fail' }
        elseif (@($UnevaluablePanels).Count -gt 0 -or $ReaderUnavailable -or -not $AllMoved) { $verdict = 'Inconclusive' }
        # A prediction the measurement corrected is a FINDING, not a pass. The panel discriminated —
        # which is what DISC-02 asks — but the phase's model of it was wrong, and a Pass here would
        # bury that. Degrade, never upgrade: a Fail already established above is never softened.
        elseif (@($PredictionsCorrected).Count -gt 0) { $verdict = 'Inconclusive' }

        $human = "phase-88 $canonicalId verdict=${verdict}: lever=$lever trigger='$TriggerKind' | " +
                 "$(@($PanelResults | Where-Object { $_.Moved }).Count)/$(@($subjects).Count) asserted panel(s) moved " +
                 "[$(@($PanelResults | ForEach-Object { "$($_.PanelId):$(if($_.Moved){'moved'}else{'no'})" }) -join ' ')] | " +
                 "crossTalkHeld=$CrossTalkHeld [$(@($CrossTalkResults | ForEach-Object { "$($_.PanelId):$(if($_.StayedPut){'held'}else{'DRIFTED'})" }) -join ' ')] | " +
                 "baselineRecaptured=$BaselineRecaptured band=$ScoringBandSource | " +
                 "after $($afterStart.ToString('o')) -> $($afterEnd.ToString('o')) at $afterCount x ${SubWindowSeconds}s, " +
                 "${ViewportWidth}x${ViewportHeight}, rate_interval=${ScenarioRateInterval}s | " +
                 "load=$LoadShape ~$HostLoadRequests req | diagnosticAgrees=$DiagnosticAgreesWithPanel | " +
                 "unevaluable=$(@($UnevaluablePanels).Count) findings=$(@($Findings).Count) | " +
                 "stack clean: seamVars=$($restore.SeamVarsClean) replicas=$($restore.ReplicasRestored) images=$($restore.ImagesUnchanged)"

        if (-not [string]::IsNullOrWhiteSpace($ScaledTierName)) {
            $human += " | scaled tier '$ScaledTierName' $ScaledReplicasBefore -> 0 -> $ScaledReplicasAfter (both read LIVE, never from a table)"
        }
        if ($null -ne $TrafficResumedAfterRestore) {
            $human += " | trafficResumedAfterRestore=$TrafficResumedAfterRestore"
        }
        if (@($PredictionsCorrected).Count -gt 0) {
            $human += " | PREDICTION CORRECTED BY MEASUREMENT on $(@($PredictionsCorrected).Count) panel(s) — see PredictionsCorrectedByMeasurement[]"
        }
        # A row may append a statement it is REQUIRED to make about panels it observed but did not
        # assert, so a later reader cannot conclude the scenario should have moved them.
        if ($scenario.Contains('humanSummarySuffix') -and -not [string]::IsNullOrWhiteSpace("$($scenario.humanSummarySuffix)")) {
            $human += " || $($scenario.humanSummarySuffix)"
        }

        $report = [ordered]@{
            ScenarioId                = $canonicalId
            Verdict                   = $verdict
            Status                    = 'Locked'
            Lever                     = $lever
            TargetTier                = "$($scenario.targetTier)"
            TriggerKind               = $TriggerKind
            DwellSeconds              = $dwell

            Moved                     = $AllMoved
            CrossTalkHeld             = $CrossTalkHeld
            BaselineRecaptured        = $BaselineRecaptured
            BaselineRecaptureNote     = $BaselineRecaptureNote
            SeamActiveDuringRebaseline = $SeamActiveDuringRebaseline
            StackRestored             = [bool]$restore.Ok
            BandSource                = $ScoringBandSource

            PanelResults              = @($PanelResults)
            CrossTalkPanels           = @($CrossTalkResults)
            PreArmBaselineValues      = @($PreArmBaselineValues)
            PreArmWindowStart         = "$($preArmCap.WindowStartUtc)"
            PreArmWindowEnd           = "$($preArmCap.WindowEndUtc)"

            RolloutOldInstanceIds     = [string[]]@($RolloutOldInstanceIds)
            RolloutNewInstanceIds     = [string[]]@($RolloutNewInstanceIds)
            RolloutUtc                = $RolloutUtc
            RolloutNote               = 'EXPECTED ARTIFACT, not a result. `kubectl set env` and `kubectl scale` mint NEW pods with NEW service_instance_id values, and a k8s restart never RESETS a series — it starts a new one. Every by(service_instance_id) panel therefore gains series at this moment and every aggregate briefly dips; that dip must NOT be scored, which is why the scoring band for any rollout scenario is re-captured afterwards.'

            # SCALAR for a row whose lever SCALED a tier — that tier's live-read count before the
            # mutation and its independently re-read count afterwards. The per-tier maps are kept
            # alongside under *ByTier. A row that scaled nothing keeps the map in the scalar slot, and
            # ReplicasFieldNote states which shape this artifact carries so the 88-08 roll-up can
            # never mistake one for the other.
            ReplicasBefore            = if ($null -ne $ScaledReplicasBefore) { [int]$ScaledReplicasBefore } else { $ReplicasBeforeMap }
            ReplicasAfter             = if ($null -ne $ScaledReplicasAfter)  { [int]$ScaledReplicasAfter }  else { $ReplicasAfterMap }
            ScaledTier                = $ScaledTierName
            ReplicasBeforeByTier      = $ReplicasBeforeMap
            ReplicasAfterByTier       = $ReplicasAfterMap
            ReplicasFieldNote         = if ($null -ne $ScaledReplicasBefore) { "this row SCALED '$ScaledTierName', so ReplicasBefore/ReplicasAfter are SCALARS for that tier (read live before the mutation, re-read live after the restore); the full four-tier maps are in ReplicasBeforeByTier/ReplicasAfterByTier" } else { 'this row scaled no tier, so ReplicasBefore/ReplicasAfter carry the four-tier MAPS (identical to ReplicasBeforeByTier/ReplicasAfterByTier)' }
            ReplicasRestored          = $restore.ReplicasRestored
            SeamVarsAfter             = @($restore.SeamVarsFound)
            SeamVarsClean             = $restore.SeamVarsClean
            ImagesUnchanged           = $restore.ImagesUnchanged
            ImagesBefore              = $preImages
            ReplicaMismatches         = @($restore.ReplicaMismatches)
            ImageMismatches           = @($restore.ImageMismatches)
            UnknownTiers              = @($restore.UnknownTiers)
            DependencyOutageDriven    = $false

            BaselineWindowStart       = if ($BaselineRecaptured -and $null -ne $rebaseCap) { "$($rebaseCap.WindowStartUtc)" } else { "$($disc01.BaselineWindowStart)" }
            BaselineWindowEnd         = if ($BaselineRecaptured -and $null -ne $rebaseCap) { "$($rebaseCap.WindowEndUtc)" } else { "$($disc01.BaselineWindowEnd)" }
            AfterWindowStart          = $afterStart.ToString('o')
            AfterWindowEnd            = $afterEnd.ToString('o')
            SubWindowSeconds          = $SubWindowSeconds
            SubWindowCount            = $afterCount
            Panel4SubWindowSeconds    = $Panel4SubWindowSeconds
            Panel4SubWindowNote       = 'MEASURED in 88-04: panel 4 renders "No data" at a 60 s sub-window, because increase() needs at least TWO samples inside its range and the stored resolution here is 60 s (the SDK export cadence, not the 15 s scrape). Panel 4 is therefore read in its OWN batch at 120 s whenever a scenario asserts or controls on it, and EVERY entry states its own SubWindowSeconds so widths can never be silently cross-compared.'
            ViewportWidth             = $ViewportWidth
            ViewportHeight            = $ViewportHeight
            LocatorMode               = $LocatorMode
            RateIntervalSeconds       = $ScenarioRateInterval
            DatasourceTimeInterval    = $ScenarioTimeInterval
            OldestSampleUtc           = $OldestSampleUtc

            FaultStartUtc             = $FaultStartUtc
            FaultEndUtc               = $FaultEndUtc
            LoadShape                 = $LoadShape
            LoadActualSeconds         = $LoadActualSeconds
            HostLoadRequests          = $HostLoadRequests

            DiagnosticQueryValue      = $DiagnosticQueryValue
            DiagnosticQueryValues     = @($DiagnosticEntries)
            DiagnosticAgreesWithPanel = $DiagnosticAgreesWithPanel
            DiagnosticDisagreements   = @($DiagnosticDisagreements)
            DiagnosticNote            = 'The RENDERED panel is the verdict. These proxy query_range values are DIAGNOSIS only; a disagreement is a finding to record, never an error to reconcile away.'

            RangeCumulativeLevelAfter    = $RangeCumulativeLevelAfter
            RangeCumulativeWindowSeconds = $RangeCumulativeWindowSeconds
            RangeCumulativeNote          = 'Panel 4 (Regime C) at its WIDE default range — the number an operator actually sees on the shipped dashboard. RECORDED AND NEVER SCORED: it grows on a demonstrably healthy stack too, so applying a Regime-A band to it would be a guaranteed false positive. Panel 4''s Moved computation uses only the per-120 s value scored against a 120 s band.'

            ObservedPanels            = @($ObservedPanels)
            ObservedPanelsNote        = 'Panels captured for the RECORD only — neither asserted nor used as a cross-talk control. They exist so a statement the scenario must make about them is a measurement rather than an assertion.'

            TrafficResumedAfterRestore = $TrafficResumedAfterRestore
            TrafficResumeDetail        = $TrafficResumeDetail

            PredictionsCorrectedByMeasurement = [string[]]@($PredictionsCorrected)
            PredictionNote            = 'A panel whose PredictionHeld is false MOVED — it left its healthy band, or emptied, for at least two consecutive sub-windows — but not in the direction this plan predicted. DISC-02 asks whether the panel discriminates, and it does; the wrong prediction is a finding carried to the HAND-02 misleading-by-default list, and it degrades the verdict to Inconclusive so that a wrong model can never be absorbed into a Pass.'

            NewSeriesAfter            = [string[]]@($NewSeriesAfter)
            Findings                  = [string[]]@($Findings)
            UnevaluablePanels         = @($UnevaluablePanels)
            ScreenshotPaths           = @($ScenarioScreenshotPaths)
            BatchStateAfter           = "$($afterCap.State)"
            CompletedUtc              = ([DateTimeOffset]::UtcNow).ToString('o')
            HumanSummary              = $human
        }

        $reportPath = Join-Path $reportDir ("phase-88-{0}.json" -f $canonicalId)
        if (-not (Save-Phase88ScenarioArtifact -Report $report -Path $reportPath)) {
            Write-Phase "the written artifact carries a JSON VALUE of 'System.Object-array' — the serialisation depth is too shallow." 'Red'
            exit 66
        }
        Write-Phase "verdict artifact: $reportPath" 'Green'
        Write-Phase $human $(if ($verdict -eq 'Pass') { 'Green' } elseif ($verdict -eq 'Fail') { 'Red' } else { 'Yellow' })
        foreach ($f in $Findings) { Write-Phase "  FINDING: $f" 'Yellow' }

        $exitCode = Resolve-AnalyzerExitCode ([pscustomobject]$report)
        if ($ReaderUnavailable -and $exitCode -eq 0) { $exitCode = 66 }
        if (-not $restore.Ok) { $exitCode = 65 }
        $resolved = Resolve-SweepClass $exitCode
        Write-Phase "class=$($resolved.Class) exit=$exitCode" 'Gray'
        exit $exitCode
    }

    # =========================================================================================
    # BASELINE ANSWER FIELDS — initialised up front so the artifact writer can read any of them on
    # any path under StrictMode, and so an unmeasured quantity is $null rather than absent.
    # =========================================================================================
    $BaselineBands              = @()
    $UnevaluablePanels          = @()
    $RegimeBNonZeroBands        = @()
    $DiagnosticQueryValues      = @()
    $DiagnosticDisagreements    = @()
    $DiagnosticAgreesWithPanel  = $null
    $ScreenshotPaths            = @()
    $RateIntervalSeconds        = $null
    $DatasourceTimeInterval     = $null
    $OldestSampleUtc            = $null
    $RangeCumulativeLevelBaseline = $null
    $RangeCumulativeWindowSeconds = 3600
    $TrafficWorkflowId          = ''
    $TrafficActivationStatus    = $null
    $HostLoadRequests           = 0
    $AllPanelsRead              = $false
    $AllBandsComputed           = $false
    $BatchState                 = 'NotRun'
    $Panel9FloorApplied         = $null
    $LegendNamesBound           = $null
    $ReaderUnavailable          = $false

    $SubWindowSeconds = 60
    $SubWindowCount   = 10

    # =========================================================================================
    # STEP B — REST STATE. The PRE gate already refused to continue unless all four tiers were fully
    # Ready at their live-read counts with zero seam residue, so StackCleanAtStart is a RECORD of an
    # already-enforced precondition rather than a claim taken on trust.
    # =========================================================================================
    Write-Phase "STEP B: rest state recorded — StackCleanAtStart=$StackCleanAtStart"
    foreach ($t in $Tiers) { Write-Phase "  $t : $($preReplicas[$t]) replicas, $($preImages[$t])" 'Gray' }

    # =========================================================================================
    # STEP C — TRAFFIC. A band captured against an IDLE stack is not a null hypothesis for a running
    # one: every Class-A panel would band at zero and "it moved" would be trivially true afterwards.
    # Two independent drives run for the WHOLE capture:
    #   1. the fan-out workflow, which produces the orchestrator/processor/keeper pipeline signal
    #      (panels 1-9);
    #   2. a light, steady host HTTP load against non-health WebApi routes, so panels 10-14 band
    #      against a real idle-plus-traffic level rather than an empty one.
    #
    # The host load deliberately drives only READ-ONLY 2xx routes. The two CONFIRMED inert non-2xx
    # drivers (a 404 on an unmatched path, a 400 on an empty activation array) are left to WEB-01, so
    # that scenario's status-mix movement is an unambiguous new signal rather than an increase in a
    # rate this baseline already contained.
    # =========================================================================================
    if ($SkipTraffic) {
        Write-Phase "STEP C: -SkipTraffic was given." 'Yellow'
        Write-Phase "  The Class-A panels cannot be banded honestly against an idle stack, so this run" 'Yellow'
        Write-Phase "  produces NO baseline and degrades to Inconclusive by construction." 'Yellow'
        Write-Phase "  -SkipTraffic exists for reader debugging, never for capturing a band." 'Yellow'
        exit 2
    }

    Write-Phase "STEP C: drive the fan-out workflow and a steady host HTTP load"

    $guidPattern = '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$'
    $wfIdRaw  = ''
    $wfIdExit = 1
    try {
        $wfIdRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"
        $wfIdExit = $LASTEXITCODE
    } catch { $wfIdExit = 1 }
    $wfId = ''
    foreach ($line in @(("$wfIdRaw") -split "`r?`n")) {
        $t = ("$line").Trim()
        if ($t -match $guidPattern) { $wfId = $t; break }
    }

    if ([string]::IsNullOrWhiteSpace($wfId)) {
        # The seeder EXISTS to create this workflow. It is idempotent and GET-matches its sentinel
        # name, so running it when the row is already present is a multi-minute no-op — hence it is
        # invoked only when the lookup actually came back empty.
        Write-Phase "  the fan-out workflow is absent — running the idempotent seeder." 'Yellow'
        dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Phase "seeder failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 50 }
        try {
            $wfIdRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"
            $wfIdExit = $LASTEXITCODE
        } catch { $wfIdExit = 1 }
        foreach ($line in @(("$wfIdRaw") -split "`r?`n")) {
            $t = ("$line").Trim()
            if ($t -match $guidPattern) { $wfId = $t; break }
        }
    }
    if ([string]::IsNullOrWhiteSpace($wfId)) {
        Write-Phase "could not resolve the fan-out workflow id (psql exit $wfIdExit). Aborting." 'Red'; exit 50
    }
    $TrafficWorkflowId = $wfId
    Write-Phase "  traffic workflow id = $wfId" 'Gray'

    $startResp = Invoke-DriverApi -Method 'POST' -Path '/api/v1/orchestration/start' -Body (ConvertTo-Json @($wfId))
    $TrafficActivationStatus = [int]$startResp.Status
    if ($startResp.Status -ne 204) {
        Write-Phase "activation gate failed — expected 204, got $($startResp.Status). Aborting." 'Red'; exit 50
    }
    Write-Phase "  activation accepted (204)." 'Gray'

    # Settle two export cadences before the capture window opens, then hold for the whole window plus
    # two more cadences so the LAST sub-window is over fully exported data rather than over the gap
    # between the current moment and the next 60 s export tick.
    $settleSeconds   = 150
    $captureSeconds  = $SubWindowSeconds * $SubWindowCount
    $trailSeconds    = 120
    $trafficSeconds  = $settleSeconds + $captureSeconds + $trailSeconds + 60

    $trafficStartUtc = [datetime]::UtcNow
    $trafficJob = Start-Job -ScriptBlock {
        param($BaseUri, $DurationSeconds)
        # READ-ONLY routes only. Nothing here creates, mutates or deletes a row, so the load cannot
        # perturb the pipeline signal the other panels are banding.
        $routes = @('/api/v1/workflows', '/api/v1/processors', '/api/v1/schemas', '/api/v1/steps', '/api/v1/assignments')
        $deadline = (Get-Date).AddSeconds($DurationSeconds)
        $n = 0
        while ((Get-Date) -lt $deadline) {
            foreach ($r in $routes) {
                try {
                    $null = Invoke-WebRequest -Uri "$BaseUri$r" -UseBasicParsing -TimeoutSec 10 `
                                -SkipHttpErrorCheck -ErrorAction Stop
                    $n++
                } catch { }
                Start-Sleep -Milliseconds 400
                if ((Get-Date) -ge $deadline) { break }
            }
        }
        return $n
    } -ArgumentList $api, $trafficSeconds

    Write-Phase "  host HTTP load started (read-only routes, ~2.5 req/s, ${trafficSeconds}s budget)." 'Gray'
    Write-Phase "  settling ${settleSeconds}s (2 export cadences) before the capture window opens..." 'Gray'
    Start-Sleep -Seconds $settleSeconds

    # ABSOLUTE window bounds, minted ONCE and reused by every read below. The capture window opens
    # only after the settle, so it contains no warm-up transient.
    $BaselineWindowStartUtc = [datetime]::UtcNow
    $BaselineWindowEndUtc   = $BaselineWindowStartUtc.AddSeconds($captureSeconds)
    Write-Phase "  capture window $($BaselineWindowStartUtc.ToString('o')) -> $($BaselineWindowEndUtc.ToString('o'))" 'Gray'
    Write-Phase "  holding ${captureSeconds}s of traffic across the window, then ${trailSeconds}s so the last sub-window is fully exported..." 'Gray'
    Start-Sleep -Seconds ($captureSeconds + $trailSeconds)

    # =========================================================================================
    # STEP D — $__rate_interval, DERIVED from the live datasource timeInterval. 240 is never
    # hardcoded: PQ-04 measured the same value for a 10-minute and a 2-hour window at this viewport,
    # but whatever is DERIVED here is what the artifact records. The evidentiary horizon is recorded
    # beside it so the artifact states its own limits.
    # =========================================================================================
    Write-Phase "STEP D: derive `$__rate_interval from the live datasource timeInterval"
    try {
        $ds = Invoke-RestMethod -Uri "$gf/api/datasources/uid/skp-prometheus" -Headers $auth -TimeoutSec 20 -ErrorAction Stop
        $tiText  = ''
        $dsNames = @(Get-PropertyNames $ds)
        if ($dsNames -contains 'jsonData') {
            $jdNames = @(Get-PropertyNames $ds.jsonData)
            if ($jdNames -contains 'timeInterval') { $tiText = "$($ds.jsonData.timeInterval)" }
        }
        $ti = ConvertFrom-GrafanaDuration $tiText
        if ($ti -le 0) {
            Write-Phase "  datasource timeInterval is '$tiText' — the rate interval cannot be derived." 'Yellow'
        } else {
            $DatasourceTimeInterval = $tiText
            $step = Get-QueryStepSeconds -RangeSeconds $captureSeconds -TimeIntervalSeconds $ti -MaxDataPoints $ViewportWidth
            $RateIntervalSeconds = Get-RateIntervalSeconds $ti $step
            Write-Phase "  timeInterval='$tiText' (${ti}s) step=${step}s -> rate_interval=${RateIntervalSeconds}s" 'Gray'
        }
    } catch {
        Write-Phase "  the datasource read failed: $($_.Exception.Message)" 'Yellow'
    }

    $nowUnix = ([DateTimeOffset]::UtcNow).ToUnixTimeSeconds()
    try {
        $oldestQ = Invoke-ProxyRangeQuery -Query 'up' -StartUnix ($nowUnix - (336 * 3600)) -EndUnix $nowUnix -StepSeconds 3600
        $oldestUnix = $null
        foreach ($s in @($oldestQ.data.result)) {
            $sn = @(Get-PropertyNames $s)
            if ($sn -notcontains 'values') { continue }
            $vals = @($s.values)
            if ($vals.Count -eq 0) { continue }
            $ts = [double]($vals[0][0])
            if ($null -eq $oldestUnix -or $ts -lt $oldestUnix) { $oldestUnix = $ts }
        }
        if ($null -ne $oldestUnix) {
            $OldestSampleUtc = ([System.DateTimeOffset]::FromUnixTimeSeconds([long]$oldestUnix)).UtcDateTime.ToString('o')
            Write-Phase "  evidentiary horizon: oldest sample $OldestSampleUtc" 'Gray'
        }
    } catch {
        Write-Phase "  the horizon query failed: $($_.Exception.Message)" 'Yellow'
    }

    # =========================================================================================
    # STEP E — THE PINNED CAPTURE. ONE batch, all fourteen panels x ten abutting 60 s absolute
    # windows, in a single browser session.
    #
    # EVERY SUB-WINDOW IS THE SAME WIDTH. This is the phase's first pitfall and it is not a tidiness
    # rule: a timeseries legend Mean is computed over the VISIBLE range, so a sliding or unequal-width
    # window would make the baseline and every later after-capture incomparable — and the reader
    # refuses such a batch outright rather than emitting numbers that look comparable and are not.
    # =========================================================================================
    Write-Phase "STEP E: batch read — 14 panels x $SubWindowCount abutting ${SubWindowSeconds}s windows at ${ViewportWidth}x${ViewportHeight}"
    $shotDir = Join-Path $screenshotRoot 'BASE-01'
    New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

    # The width and the count are LITERALS at the call site, not variables, so the shape of the
    # capture is legible where it happens. The assertion below then ties the values the ARTIFACT
    # records to the windows this call actually minted — a later edit that changes one without the
    # other aborts here instead of writing an artifact whose stated shape is not the shape it used.
    $wins = @(Get-PinnedWindowSeries -EndUtc $BaselineWindowEndUtc -SubWindowSeconds 60 -Count 10)
    $mintedWidth = [int](($wins[0].ToMs - $wins[0].FromMs) / 1000)
    if ($mintedWidth -ne $SubWindowSeconds -or @($wins).Count -ne $SubWindowCount) {
        Write-Phase "the minted windows ($(@($wins).Count) x ${mintedWidth}s) do not match the values this run would record ($SubWindowCount x ${SubWindowSeconds}s)." 'Red'
        exit 64
    }
    Write-Phase "  windows $($wins[0].FromUtc) -> $($wins[-1].ToUtc)" 'Gray'

    $panelIdList = @($Panels.Keys)
    $batch = Invoke-PanelReadBatch -PanelIds $panelIdList -Windows $wins -ScreenshotDir $shotDir `
               -LocatorMode $LocatorMode -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight `
               -BasicAuthBase64 $adminB64
    $BatchState = "$($batch.State)"
    Write-Phase "  batch: State=$BatchState requested=$($batch.Requested) emitted=$($batch.Emitted)" 'Gray'

    if ($batch.State -eq 'ReaderMissing') {
        # An INFRA ABORT, never a verdict: node, the playwright skill, or the reader script is absent,
        # so nothing about the panels was measured at all.
        Write-Phase "the panel reader is unavailable: $($batch.Error). Aborting." 'Red'
        Write-Phase "REMEDIATION: install node and the playwright skill, or point PHASE88_PLAYWRIGHT_SKILL_DIR at it." 'Yellow'
        exit 63
    }
    if ($batch.State -ne 'Ok') {
        # Truncated / Unreadable. Do NOT band on a partial set — a band computed from an unknown
        # subset of the intended windows is not the band the artifact would claim it is.
        Write-Phase "  the batch did not complete cleanly ($BatchState): $($batch.Error)" 'Yellow'
        Write-Phase "  bands will be computed only where a full sample set exists; the rest are declared unevaluable." 'Yellow'
        $ReaderUnavailable = $true
    }
    $ScreenshotPaths = @(Get-ScreenshotPaths $batch.Readings)

    # =========================================================================================
    # STEP F — PER-REGIME BANDING.
    #
    # A stat panel contributes ONE band from its big number. A timeseries contributes one band PER
    # LEGEND SERIES, selected POSITIONALLY (the Record-3 amendment) with the series NAME recorded
    # beside it — so a later capture can match by name where names are stable and fall back to the
    # row index where a new series has appeared.
    #
    # Two outcomes are FINDINGS rather than bands, and both are recorded as such:
    #   * a Class-A panel that bands at exactly zero across all ten sub-windows has no live signal to
    #     move away from, so its scenario cannot discriminate. That is not a valid null hypothesis and
    #     it must not be treated as one.
    #   * a Regime-B panel that bands NON-zero contradicts the guarded-zero assumption its whole
    #     discrimination rule rests on.
    # =========================================================================================
    Write-Phase "STEP F: banding"
    foreach ($pid_ in $panelIdList) {
        $panel  = $Panels[$pid_]
        $states = @(Get-PanelStates -Readings @($batch.Readings) -PanelId $pid_)
        $stateSummary = if (@($states).Count -gt 0) { (@($states | Select-Object -Unique) -join '|') } else { 'NoReading' }

        if (@($states).Count -eq 0) {
            $UnevaluablePanels += "panel ${pid_} ($($panel.Title)): the reader returned NO reading at all for this panel"
            Write-Phase "  panel $pid_ : NO READING" 'Red'
            $ReaderUnavailable = $true
            continue
        }

        $entries = @()
        if ("$($panel.Type)" -eq 'stat') {
            $vals = @(Get-PanelSamples -Readings @($batch.Readings) -PanelId $pid_)
            $entries += @{ Index = -1; Name = "$($panel.SeriesName)"; NameStable = $true; NameSource = 'staticLegend'; Values = $vals }
        }
        else {
            $seriesCount = Get-PanelSeriesCount -Readings @($batch.Readings) -PanelId $pid_
            if ($seriesCount -eq 0) {
                $UnevaluablePanels += "panel ${pid_} ($($panel.Title)): rendered $stateSummary with ZERO legend series across all $SubWindowCount sub-windows — nothing to band"
                Write-Phase "  panel $pid_ : zero legend series ($stateSummary)" 'Yellow'
                continue
            }
            for ($i = 0; $i -lt $seriesCount; $i++) {
                $nm   = Get-PanelSeriesNameAt -Readings @($batch.Readings) -PanelId $pid_ -SeriesIndex $i
                $vals = @(Get-PanelSamples -Readings @($batch.Readings) -PanelId $pid_ -SeriesIndex $i)
                $entries += @{ Index = $i; Name = "$($nm.Name)"; NameStable = [bool]$nm.Stable; NameSource = "$($nm.Source)"; Values = $vals }
                if (-not [string]::IsNullOrWhiteSpace("$($nm.Name)")) { $LegendNamesBound = $true }
            }
        }

        foreach ($e in $entries) {
            $vals = @($e.Values)
            if ($vals.Count -eq 0) {
                $UnevaluablePanels += "panel ${pid_} ($($panel.Title)) series '$($e.Name)': rendered $stateSummary but produced NO numeric sample"
                Write-Phase "  panel $pid_ series '$($e.Name)': no numeric sample ($stateSummary)" 'Yellow'
                continue
            }
            if ($vals.Count -ne $SubWindowCount) {
                # A short sample set is recorded, not silently banded as if it were whole: the band's
                # own SampleCount states what it was computed from.
                Write-Phase "  panel $pid_ series '$($e.Name)': $($vals.Count)/$SubWindowCount samples" 'Yellow'
            }

            $band = Get-PanelBand -Values ([double[]]$vals)
            $allZero = $true
            foreach ($v in $vals) { if ([double]$v -ne 0.0) { $allZero = $false; break } }

            if ("$($panel.Regime)" -eq 'A' -and $allZero) {
                $UnevaluablePanels += "panel ${pid_} ($($panel.Title)) series '$($e.Name)': Class-A panel banded at EXACTLY zero across all $SubWindowCount sub-windows — it has no live signal to move away from, so its scenario cannot discriminate. This is a finding, not a band (carry to the HAND-04 register)."
                Write-Phase "  panel $pid_ series '$($e.Name)': Class-A ALL-ZERO — recorded as a finding, not a band" 'Red'
            }
            if ("$($panel.Regime)" -eq 'B' -and -not $allZero) {
                $RegimeBNonZeroBands += "panel ${pid_} ($($panel.Title)) series '$($e.Name)': Regime-B panel did NOT band at zero (values $(@($vals) -join ', ')) — the guarded-zero null hypothesis its discrimination rule rests on does not hold on this stack"
                Write-Phase "  panel $pid_ series '$($e.Name)': Regime-B NON-ZERO baseline — recorded as a finding" 'Red'
            }
            if ($pid_ -eq '9') { $Panel9FloorApplied = [bool]$band.FloorApplied }

            $BaselineBands += [pscustomobject]@{
                PanelId      = [int]$pid_
                PanelTitle   = "$($panel.Title)"
                PanelType    = "$($panel.Type)"
                Regime       = "$($panel.Regime)"
                Guarded      = [bool]$panel.Guarded
                SeriesIndex  = [int]$e.Index
                SeriesName   = "$($e.Name)"
                SeriesNameStable = [bool]$e.NameStable
                SeriesNameSource = "$($e.NameSource)"
                SubWindowSeconds = $SubWindowSeconds
                Values       = [double[]]$vals
                States       = [string[]]$states
                BandLow      = [double]$band.Low
                BandHigh     = [double]$band.High
                BandMean     = [double]$band.Mean
                BandSigma    = [double]$band.Sigma
                BandHalfWidth = [double]$band.HalfWidth
                FloorApplied = [bool]$band.FloorApplied
                SampleCount  = [int]$band.SampleCount
                PanelState   = $stateSummary
            }
            Write-Phase ("  panel {0} [{1}] '{2}': band {3:N4}..{4:N4} mean {5:N4} floor={6} n={7} ({8})" -f `
                $pid_, $panel.Regime, $e.Name, $band.Low, $band.High, $band.Mean, $band.FloorApplied, $band.SampleCount, $stateSummary) 'Gray'
        }
    }

    # =========================================================================================
    # STEP F2 — PANEL 4's BAND AT A WIDER SUB-WINDOW (a measured correction to the plan's assumption).
    #
    # The plan expected panel 4 to band on its per-minute increase at the fixed 60 s sub-window. It
    # cannot, and the reason is structural rather than incidental: panel 4 renders
    # `increase(counter[$__range])`, the stored resolution on this stack is 60 s (the SDK export
    # cadence, not the 15 s scrape interval), and `increase()` needs at least TWO samples inside its
    # range. A 60 s window contains exactly one, so the panel renders "No data" — which the first
    # BASE-01 run measured directly.
    #
    # Panel 4 is therefore banded over FIVE 120 s sub-windows across the SAME settled 600 s window:
    # equal width within its own batch (the reader refuses anything else), absolute, same viewport,
    # entirely inside the traffic window. The band is a per-TWO-minute increase and every entry states
    # its own SubWindowSeconds, so it can never be silently compared against a 60 s band.
    # =========================================================================================
    $Panel4BandSubWindowSeconds = 120
    $Panel4BandSubWindowCount   = 5
    $Panel4BandedAtWiderWindow  = $false
    if (@($BaselineBands | Where-Object { $_.PanelId -eq 4 }).Count -eq 0) {
        Write-Phase "STEP F2: panel 4 did not band at ${SubWindowSeconds}s — retrying at ${Panel4BandSubWindowSeconds}s x ${Panel4BandSubWindowCount} inside the same window"
        $p4Wins = @(Get-PinnedWindowSeries -EndUtc $BaselineWindowEndUtc -SubWindowSeconds 120 -Count 5)
        $p4Batch = Invoke-PanelReadBatch -PanelIds @('4') -Windows $p4Wins -ScreenshotDir $shotDir `
                     -LocatorMode $LocatorMode -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight `
                     -BasicAuthBase64 $adminB64
        if ($p4Batch.State -eq 'ReaderMissing') {
            Write-Phase "the panel reader became unavailable before the panel-4 retry: $($p4Batch.Error). Aborting." 'Red'; exit 63
        }
        $p4Vals   = @(Get-PanelSamples -Readings @($p4Batch.Readings) -PanelId '4')
        $p4States = @(Get-PanelStates  -Readings @($p4Batch.Readings) -PanelId '4')
        $p4State  = if (@($p4States).Count -gt 0) { (@($p4States | Select-Object -Unique) -join '|') } else { 'NoReading' }
        if (@($p4Vals).Count -gt 0) {
            $p4Band = Get-PanelBand -Values ([double[]]$p4Vals)
            $BaselineBands += [pscustomobject]@{
                PanelId      = 4
                PanelTitle   = "$($Panels['4'].Title)"
                PanelType    = "$($Panels['4'].Type)"
                Regime       = 'C'
                Guarded      = $false
                SeriesIndex  = -1
                SeriesName   = "$($Panels['4'].SeriesName)"
                SeriesNameStable = $true
                SeriesNameSource = 'staticLegend'
                SubWindowSeconds = $Panel4BandSubWindowSeconds
                Values       = [double[]]$p4Vals
                States       = [string[]]$p4States
                BandLow      = [double]$p4Band.Low
                BandHigh     = [double]$p4Band.High
                BandMean     = [double]$p4Band.Mean
                BandSigma    = [double]$p4Band.Sigma
                BandHalfWidth = [double]$p4Band.HalfWidth
                FloorApplied = [bool]$p4Band.FloorApplied
                SampleCount  = [int]$p4Band.SampleCount
                PanelState   = $p4State
            }
            $Panel4BandedAtWiderWindow = $true
            # The 60 s finding is REPLACED, not merely supplemented: panel 4 now has a band, so it is
            # no longer unevaluable. The reason it could not band at 60 s is preserved in
            # Panel4SubWindowNote, which is where a reader should look for it.
            $UnevaluablePanels = @($UnevaluablePanels | Where-Object { "$_" -notmatch '^panel 4[ :(]' })
            Write-Phase ("  panel 4 [C] band {0:N4}..{1:N4} mean {2:N4} floor={3} n={4} at {5}s ({6})" -f `
                $p4Band.Low, $p4Band.High, $p4Band.Mean, $p4Band.FloorApplied, $p4Band.SampleCount, $Panel4BandSubWindowSeconds, $p4State) 'Gray'
        } else {
            Write-Phase "  panel 4 produced no numeric sample at ${Panel4BandSubWindowSeconds}s either ($p4State)." 'Yellow'
        }
        $ScreenshotPaths = @(@($ScreenshotPaths) + @(Get-ScreenshotPaths $p4Batch.Readings) | Select-Object -Unique)
    }

    # =========================================================================================
    # STEP G — THE PANEL-4 WIDE-WINDOW LEVEL (Regime C, recorded but NEVER scored).
    #
    # Panel 4's expression is `increase(...[$__range])`, so its value depends on the VISIBLE window
    # width. Its banded value above is a PER-MINUTE increase over the 60 s sub-window. THIS capture is
    # the number an operator actually sees on the shipped dashboard at its default range — it grows on
    # a healthy stack too, which is exactly why it is recorded for the HAND-02 misleading-by-default
    # list and never compared against a band. Applying a Regime-A band to it would be a guaranteed
    # false positive.
    # =========================================================================================
    Write-Phase "STEP G: panel 4 wide-window range-cumulative level (${RangeCumulativeWindowSeconds}s, recorded NOT scored)"
    $wideWins = @(Get-PinnedWindowSeries -EndUtc $BaselineWindowEndUtc -SubWindowSeconds $RangeCumulativeWindowSeconds -Count 1)
    $wideBatch = Invoke-PanelReadBatch -PanelIds @('4') -Windows $wideWins -ScreenshotDir $shotDir `
                   -LocatorMode $LocatorMode -ViewportWidth $ViewportWidth -ViewportHeight $ViewportHeight `
                   -BasicAuthBase64 $adminB64
    if ($wideBatch.State -eq 'ReaderMissing') {
        Write-Phase "the panel reader became unavailable before the wide capture: $($wideBatch.Error). Aborting." 'Red'; exit 63
    }
    $wideVals = @(Get-PanelSamples -Readings @($wideBatch.Readings) -PanelId '4')
    if (@($wideVals).Count -gt 0) {
        $RangeCumulativeLevelBaseline = [double]$wideVals[0]
        Write-Phase "  panel 4 wide level = $RangeCumulativeLevelBaseline over $($wideWins[0].FromUtc) -> $($wideWins[0].ToUtc)" 'Gray'
    } else {
        Write-Phase "  panel 4 wide capture produced no numeric value." 'Yellow'
    }
    $ScreenshotPaths = @(@($ScreenshotPaths) + @(Get-ScreenshotPaths $wideBatch.Readings) | Select-Object -Unique)

    # =========================================================================================
    # STEP H — DIAGNOSTIC PROXY CROSS-CHECK. DIAGNOSIS ONLY, NEVER THE VERDICT.
    #
    # Each panel's own target expression is read from k8s/dashboards/business.json (so it tracks the
    # dashboard rather than drifting from a copy), its Grafana macros are substituted with the values
    # this run derived, and the aggregate is queried through Grafana's own datasource proxy over the
    # SAME absolute window. It is recorded beside the rendered value with an agreement flag.
    #
    # This is the direct control against the Phase-87 defect where 30/30 Class-A checks reported green
    # against three panels that were rendering "No data": the query was right and the panel was blank,
    # and nothing in the artifact could show it. Here a disagreement is a FINDING to record loudly,
    # not an error to reconcile away — the RENDERED panel remains the verdict either way.
    # =========================================================================================
    Write-Phase "STEP H: diagnostic proxy cross-check (diagnosis only — the rendered panel is the verdict)"

    # Get-ProxyAggregateMean is defined once, in the HELPERS section above, because the scenario
    # engine cross-checks with the same function.

    $winStartUnix = ([System.DateTimeOffset]::new($BaselineWindowStartUtc, [TimeSpan]::Zero)).ToUnixTimeSeconds()
    $winEndUnix   = ([System.DateTimeOffset]::new($BaselineWindowEndUtc, [TimeSpan]::Zero)).ToUnixTimeSeconds()
    $riForQuery   = if ($null -ne $RateIntervalSeconds) { [int]$RateIntervalSeconds } else { 240 }

    try {
        $dashJson = Get-Content (Join-Path $repoRoot 'k8s/dashboards/business.json') -Raw | ConvertFrom-Json
        foreach ($dp in @($dashJson.panels)) {
            $dpNames = @(Get-PropertyNames $dp)
            if ($dpNames -notcontains 'id' -or $dpNames -notcontains 'targets') { continue }
            $dpId = "$($dp.id)"
            if (-not $Panels.Contains($dpId)) { continue }
            $tgts = @($dp.targets)
            if ($tgts.Count -eq 0) { continue }

            # EVERY target is queried, not just the first. The rendered side of the comparison is the
            # sum of ALL the panel's banded series means, so comparing it against one target of a
            # multi-target panel would guarantee a mismatch and make the control useless on exactly
            # the four conservation panels it matters most for.
            $comparable = $true
            $reason = ''
            $queries = @()
            $proxyMean = $null

            foreach ($tg in $tgts) {
                $expr = "$($tg.expr)"
                if ($expr -match 'histogram_quantile') {
                    $comparable = $false
                    $reason = 'histogram_quantile is a per-series quantile; summing across series is not a meaningful aggregate, so no agreement is asserted'
                }
                # Substitute Grafana's macros with what THIS run derived. $__range becomes the
                # sub-window width, because that is the range the banded reading was taken over.
                $q = $expr.Replace('$__rate_interval', "${riForQuery}s").Replace('$__range', "${SubWindowSeconds}s")
                $q = $q.Replace('$source', '.*').Replace('$pod', '.*')
                $queries += $q

                try {
                    $m = Get-ProxyAggregateMean $q $winStartUnix $winEndUnix $SubWindowSeconds
                    if ($null -ne $m) {
                        if ($null -eq $proxyMean) { $proxyMean = 0.0 }
                        $proxyMean += [double]$m
                    }
                }
                catch {
                    # The message is kept verbatim — it is the diagnosis. It is SANITISED of the
                    # literal below so a captured error can never be mistaken by the depth guard for
                    # a truncated serialisation.
                    $reason = "proxy query failed: $($_.Exception.Message)"
                    $comparable = $false
                }
            }

            $panelSum = $null
            $mine = @($BaselineBands | Where-Object { $_.PanelId -eq [int]$dpId })
            if (@($mine).Count -gt 0) {
                $panelSum = 0.0
                foreach ($m2 in $mine) { $panelSum += [double]$m2.BandMean }
            } else {
                $comparable = $false
                $reason = (("$reason " + 'the panel produced no band, so there is nothing to compare the proxy value against').Trim())
            }

            $agrees = $null
            if ($comparable -and $null -ne $proxyMean -and $null -ne $panelSum) {
                $tol = [Math]::Max(0.10 * [Math]::Abs([double]$panelSum), 0.01)
                $agrees = ([Math]::Abs([double]$proxyMean - [double]$panelSum) -le $tol)
                if (-not $agrees) {
                    $DiagnosticDisagreements += "panel ${dpId} ($($Panels[$dpId].Title)): rendered band-mean sum $panelSum vs proxy aggregate mean $proxyMean (tolerance $tol) — the RENDERED value remains the verdict; this disagreement is recorded as a finding"
                }
            }

            $DiagnosticQueryValues += [pscustomobject]@{
                PanelId      = [int]$dpId
                TargetCount  = @($tgts).Count
                Queries      = [string[]]$queries
                ProxyMean    = $proxyMean
                PanelMeanSum = $panelSum
                Comparable   = [bool]$comparable
                Agrees       = $agrees
                Reason       = ("$reason" -replace 'System\.Object\[\]', 'System.Object-array')
            }
            Write-Phase ("  panel {0}: proxy={1} panel={2} comparable={3} agrees={4}" -f $dpId, $proxyMean, $panelSum, $comparable, $agrees) 'Gray'
        }
    } catch {
        Write-Phase "  the diagnostic cross-check could not be built: $($_.Exception.Message)" 'Yellow'
    }

    $comparableEntries = @($DiagnosticQueryValues | Where-Object { $_.Comparable -and $null -ne $_.Agrees })
    if (@($comparableEntries).Count -gt 0) {
        $DiagnosticAgreesWithPanel = (@($comparableEntries | Where-Object { -not $_.Agrees }).Count -eq 0)
    }

    # =========================================================================================
    # STEP J — RESTORE ASSERTION, ARTIFACT, VERDICT.
    # The artifact is written FIRST and the exit code is resolved from the SAME in-memory object, so
    # an artifact can never disagree with the code the process returned.
    # =========================================================================================
    Write-Phase "STEP J: restore assertion, artifact, verdict"

    if ($null -ne $trafficJob) {
        try {
            $null = Wait-Job -Job $trafficJob -Timeout 30
            $HostLoadRequests = [int](@(Receive-Job -Job $trafficJob -ErrorAction SilentlyContinue) | Select-Object -Last 1)
        } catch { }
        try { Remove-Job -Job $trafficJob -Force -ErrorAction SilentlyContinue } catch { }
        $trafficJob = $null
        Write-Phase "  host HTTP load issued ~$HostLoadRequests requests." 'Gray'
    }

    $restore = Assert-StackRestored -Tiers $Tiers -ExpectedReplicas $preReplicas -ExpectedImages $preImages
    Write-Phase "  restore: SeamVarsClean=$($restore.SeamVarsClean) ReplicasRestored=$($restore.ReplicasRestored) ImagesUnchanged=$($restore.ImagesUnchanged)" 'Gray'

    # Every panel must be either BANDED or explicitly declared unevaluable. A panel that is neither is
    # a silent gap, which is the one outcome this artifact must never contain.
    $bandedIds = @($BaselineBands | ForEach-Object { "$($_.PanelId)" } | Select-Object -Unique)
    $missing = @()
    foreach ($pid_ in $panelIdList) {
        if ($bandedIds -contains "$pid_") { continue }
        if (@($UnevaluablePanels | Where-Object { "$_" -match "^panel $pid_[ :(]" }).Count -gt 0) { continue }
        $missing += "panel ${pid_} ($($Panels[$pid_].Title)): neither banded nor declared unevaluable"
    }
    foreach ($m in $missing) { $UnevaluablePanels += $m }

    $AllPanelsRead     = (($BatchState -eq 'Ok') -and -not $ReaderUnavailable)
    $AllBandsComputed  = (@($UnevaluablePanels).Count -eq 0)

    # PRECEDENCE: Fail BEATS Inconclusive (the Phase-87 verdict-precedence fix). A dirty stack is a
    # claim that was EVALUATED and came back false — positive evidence of a defect — and no quantity of
    # other UNEVALUATED claims makes it less true. A panel that could not be READ is an unevaluated
    # assertion, not a defect, so it is Inconclusive material and never a Fail.
    $verdict = 'Pass'
    if (-not $restore.Ok) { $verdict = 'Fail' }
    elseif (-not $AllBandsComputed -or -not $AllPanelsRead) { $verdict = 'Inconclusive' }

    $human = "phase-88 BASE-01 verdict=${verdict}: " +
             "$(@($BaselineBands).Count) band(s) across $(@($bandedIds).Count)/14 panels over $SubWindowCount x ${SubWindowSeconds}s pinned windows " +
             "($($BaselineWindowStartUtc.ToString('o')) -> $($BaselineWindowEndUtc.ToString('o'))) at ${ViewportWidth}x${ViewportHeight}, " +
             "rate_interval=${RateIntervalSeconds}s (timeInterval=$DatasourceTimeInterval) | " +
             "batch=$BatchState allPanelsRead=$AllPanelsRead allBandsComputed=$AllBandsComputed | " +
             "panel4 wide level=$RangeCumulativeLevelBaseline (RECORDED, never scored) | " +
             "panel9 floorApplied=$Panel9FloorApplied | legendNamesBound=$LegendNamesBound | " +
             "regimeB non-zero=$(@($RegimeBNonZeroBands).Count) | diagnosticAgrees=$DiagnosticAgreesWithPanel " +
             "(disagreements=$(@($DiagnosticDisagreements).Count)) | unevaluable=$(@($UnevaluablePanels).Count) | " +
             "stack clean: seamVars=$($restore.SeamVarsClean) replicas=$($restore.ReplicasRestored) images=$($restore.ImagesUnchanged)"

    $report = [ordered]@{
        ScenarioId                   = 'BASE-01'
        Verdict                      = $verdict

        AllPanelsRead                = $AllPanelsRead
        AllBandsComputed             = $AllBandsComputed
        StackCleanAtStart            = $StackCleanAtStart

        BaselineWindowStart          = $BaselineWindowStartUtc.ToString('o')
        BaselineWindowEnd            = $BaselineWindowEndUtc.ToString('o')
        SubWindowSeconds             = $SubWindowSeconds
        SubWindowCount               = $SubWindowCount
        ViewportWidth                = $ViewportWidth
        ViewportHeight               = $ViewportHeight
        LocatorMode                  = $LocatorMode
        RateIntervalSeconds          = $RateIntervalSeconds
        DatasourceTimeInterval       = $DatasourceTimeInterval
        OldestSampleUtc              = $OldestSampleUtc
        BatchState                   = $BatchState
        BatchRequested               = [int]$batch.Requested
        BatchEmitted                 = [int]$batch.Emitted

        BaselineBands                = @($BaselineBands)

        Panel4BandSubWindowSeconds   = $Panel4BandSubWindowSeconds
        Panel4BandSubWindowCount     = $Panel4BandSubWindowCount
        Panel4BandedAtWiderWindow    = $Panel4BandedAtWiderWindow
        Panel4SubWindowNote          = 'MEASURED, and it corrects the plan''s assumption: panel 4 renders "No data" at a 60 s sub-window. increase() requires at least TWO samples inside its range, and the stored resolution on this stack is 60 s (the SDK export cadence, not the 15 s scrape interval), so a 60 s window contains exactly one. Panel 4 is therefore banded over five 120 s sub-windows inside the SAME settled 600 s window, and every band entry states its own SubWindowSeconds so a 120 s band can never be silently compared against a 60 s one.'

        RangeCumulativeLevelBaseline = $RangeCumulativeLevelBaseline
        RangeCumulativeWindowSeconds = $RangeCumulativeWindowSeconds
        RangeCumulativeNote          = 'Panel 4 renders increase(...[$__range]), so its value depends on the VISIBLE window width. This wide-window level is what an operator sees on the shipped dashboard; it grows on a HEALTHY stack too. It is recorded for the HAND-02 misleading-by-default list and is NEVER scored as movement — panel 4 is scored on its banded per-minute value instead.'

        DiagnosticQueryValues        = @($DiagnosticQueryValues)
        DiagnosticAgreesWithPanel    = $DiagnosticAgreesWithPanel
        DiagnosticDisagreements      = @($DiagnosticDisagreements)
        DiagnosticNote               = 'The RENDERED panel is the verdict. These proxy query_range values are DIAGNOSIS only; a disagreement is a finding to record, never an error to reconcile away.'

        RegimeBNonZeroBands          = @($RegimeBNonZeroBands)
        Panel9FloorApplied           = $Panel9FloorApplied
        LegendNamesBound             = $LegendNamesBound
        UnevaluablePanels            = @($UnevaluablePanels)
        ScreenshotPaths              = @($ScreenshotPaths)

        TrafficWorkflowId            = $TrafficWorkflowId
        TrafficActivationStatus      = $TrafficActivationStatus
        TrafficStartUtc              = $trafficStartUtc.ToString('o')
        TrafficLeftActive            = $true
        HostLoadRequests             = $HostLoadRequests

        SeamVarsClean                = $restore.SeamVarsClean
        ReplicasRestored             = $restore.ReplicasRestored
        ImagesUnchanged              = $restore.ImagesUnchanged
        SeamVarsFound                = @($restore.SeamVarsFound)
        ReplicaMismatches            = @($restore.ReplicaMismatches)
        ImageMismatches              = @($restore.ImageMismatches)
        UnknownTiers                 = @($restore.UnknownTiers)
        ReplicasBefore               = $preReplicas
        ImagesBefore                 = $preImages

        ReaderAmendmentApplied       = 'legend name-line pairing (88-PROBE-DECISIONS.md Record 3); re-provable with PANEL_READ_SELFTEST=1 node scripts/phase-88-panel-read.js'
        CompletedUtc                 = ([DateTimeOffset]::UtcNow).ToString('o')

        HumanSummary                 = $human
    }

    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $reportPath = Join-Path $reportDir 'phase-88-BASE-01.json'
    # -Depth 10, NOT the repo's usual shallower depth: BaselineBands[] carries nested Values[] and
    # States[] arrays that a shallower serialisation would silently write as the literal
    # 'System.Object[]', destroying the very evidence that makes the band recomputable (T-88-18).
    ([pscustomobject]$report) | ConvertTo-Json -Depth 10 | Set-Content -Path $reportPath -Encoding utf8

    # The depth guard (T-88-18) asserts that no JSON VALUE is the bare string 'System.Object[]', which
    # is the signature of a nested array serialised as its type name instead of its contents. It is
    # deliberately STRUCTURAL rather than a substring search: the first BASE-01 run tripped a
    # substring form on a captured exception MESSAGE that merely mentioned the type, which is a false
    # positive — the message is evidence, not truncation. A guard that cries wolf on its own diagnostic
    # text would eventually be disabled, and then the real truncation it exists to catch would ship.
    $written = Get-Content $reportPath -Raw
    if ($written -match '(?m)(:\s*"System\.Object\[\]"|^\s*"System\.Object\[\]"\s*,?\s*$)') {
        Write-Phase "the written artifact carries a JSON VALUE of 'System.Object[]' — the serialisation depth is too shallow." 'Red'
        exit 66
    }
    Write-Phase "verdict artifact: $reportPath" 'Green'
    Write-Phase $human $(if ($verdict -eq 'Pass') { 'Green' } elseif ($verdict -eq 'Fail') { 'Red' } else { 'Yellow' })

    # Resolve the exit code from the SAME in-memory object the artifact was written from. 65 and 66
    # are strictly MORE SPECIFIC renderings of the same finding — the artifact's Verdict stays
    # authoritative, and a dirty stack or an unread panel keeps its own distinct code so it can never
    # be mistaken for an ordinary assertion failure by a caller reading only the exit status.
    $exitCode = Resolve-AnalyzerExitCode ([pscustomobject]$report)
    if ($ReaderUnavailable) { $exitCode = 66 }
    if (-not $restore.Ok) { $exitCode = 65 }
    $resolved = Resolve-SweepClass $exitCode
    Write-Phase "class=$($resolved.Class) exit=$exitCode" 'Gray'
    exit $exitCode
}
finally {
    # ---- STEP Z — TEARDOWN ----------------------------------------------------------------------
    # Everything here runs on EVERY path, including an interrupted run. The hazard it closes is the
    # phase's standing one: a seam left armed silently poisons every later scenario AND every later
    # resilience sweep, and the failure presents as an inexplicable conservation violation with no
    # pointer back to its cause.
    #
    # DISARM UNCONDITIONALLY where a seam was armed. Clear-Phase88Seam is idempotent by design —
    # removing an absent variable is a no-op rollout — so it is safe to issue even when the arm never
    # landed. The $seamArmed guard exists only to avoid a needless rollout wait on the clean path.
    if ($seamArmed -and -not [string]::IsNullOrWhiteSpace($seamTier) -and -not [string]::IsNullOrWhiteSpace($seamVar)) {
        if (Get-Command Clear-Phase88Seam -ErrorAction SilentlyContinue) {
            Write-Host "[phase-88-panel-discriminate] TEARDOWN: '$seamVar' is still armed on '$seamTier' — disarming." -ForegroundColor Yellow
            $null = Clear-Phase88Seam -Tier $seamTier -Name $seamVar
        }
    }
    # The SECOND seam slot (ZERO-03 arms a trigger seam as well as a suppression seam). A teardown
    # that disarmed only the first would leave the other armed on an interrupted run.
    if ($seamArmed2 -and -not [string]::IsNullOrWhiteSpace($seamTier2) -and -not [string]::IsNullOrWhiteSpace($seamVar2)) {
        if (Get-Command Clear-Phase88Seam -ErrorAction SilentlyContinue) {
            Write-Host "[phase-88-panel-discriminate] TEARDOWN: '$seamVar2' is still armed on '$seamTier2' — disarming." -ForegroundColor Yellow
            $null = Clear-Phase88Seam -Tier $seamTier2 -Name $seamVar2
        }
    }

    # A tier this script scaled and did not restore is the same class of poisoning as a live seam.
    # The restore target is the count READ before the scale, never a tabled one.
    if (-not [string]::IsNullOrWhiteSpace($scaledTier) -and $replicasBefore -ge 0) {
        if (Get-Command Invoke-Phase88Ctl -ErrorAction SilentlyContinue) {
            Write-Host "[phase-88-panel-discriminate] TEARDOWN: restoring '$scaledTier' to its pre-read $replicasBefore replicas." -ForegroundColor Yellow
            $null = Invoke-Phase88Ctl -Arguments @('scale', "deployment/$scaledTier", "--replicas=$replicasBefore")
        }
    }

    # A background load job that outlived the run would keep hitting the WebApi after this process
    # returned, polluting the very panels a LATER capture bands.
    if ($null -ne $trafficJob) {
        Write-Host "[phase-88-panel-discriminate] TEARDOWN: stopping the host HTTP load job." -ForegroundColor Yellow
        try { Stop-Job -Job $trafficJob -ErrorAction SilentlyContinue } catch { }
        try { Remove-Job -Job $trafficJob -Force -ErrorAction SilentlyContinue } catch { }
    }
    if (@($loadJobs).Count -gt 0) {
        Write-Host "[phase-88-panel-discriminate] TEARDOWN: stopping $(@($loadJobs).Count) scenario load job(s)." -ForegroundColor Yellow
        foreach ($lj in @($loadJobs)) {
            try { Stop-Job -Job $lj -ErrorAction SilentlyContinue } catch { }
            try { Remove-Job -Job $lj -Force -ErrorAction SilentlyContinue } catch { }
        }
    }

    # Stop ONLY a forward this script actually started, behind the recycled-PID guard. A forward this
    # script found already running belongs to someone else and is left exactly as it was found.
    if ($gfForwardOwned -and $gfForwardPid -gt 0) {
        if (Get-Command Stop-GrafanaForward -ErrorAction SilentlyContinue) {
            Stop-GrafanaForward $gfForwardPid
        }
    }
    Pop-Location
}
