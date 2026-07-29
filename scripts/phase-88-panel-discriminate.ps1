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
    [switch]$RecordDroppedRow,
    # -Mode DurationLadder only. A comma-separated SUBSET of the declared rung durations, so a
    # ladder that aborted partway can be resumed without re-driving the rungs that already produced
    # an entry. Validated against the STATIC rung list before any cluster access (T-88-13): an
    # unknown value exits 64 rather than silently running nothing, because "no rungs matched" and
    # "the ladder ran and measured nothing" would produce the same empty artifact.
    [string]$Rungs = ''
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
# The out-of-band Redis arm slot (skp:test:defeat-read-arm) is the SECOND half of the processor seam:
# the env var supplies a step label, the slot is what a matching hop CLAIMS. Tracked out here for the
# same reason the seam slots are — an interrupted run must still clear it.
$reinjectArmArmed = $false
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
            # The row supplies its OWN accepted-unproven text. Without this the recorder would emit
            # WEB-02's 5xx/redis narrative under ZERO-01's name — a reason that names the wrong routes
            # is worse than no reason, because it reads as evidence.
            acceptedUnprovenReason = 'ROUTE A — API dangling edge: MEASURED 422 at stage `step-create`. POST /api/v1/steps refuses a nextStepIds entry naming a non-existent step outright, EARLIER than the planning analysis predicted (CycleDetector''s D-08 missing-step gate was expected to reject it at activation instead). The graph can therefore never be brought into the state this route needs. ROUTE B — L2 step-key deletion: NOT VIABLE. On a validly created and activated two-step probe workflow the non-entry step''s key skp:{workflowId}:{stepId} was present after activation, absent after a DEL, and PRESENT AGAIN after one stop/start cycle, because OrchestrationService.StartAsync ends in IRedisProjectionWriter.UpsertAsync, which rewrites the whole snapshot from Postgres — so the hole closes before the orchestrator''s hydration BFS could ever miss the step. Reaching the counter would require a src/ change, which this phase''s locked constraint forbids, and no substitute fault was invented. LOG-CORROBORATION ROUTE FOR MAINTENANCE: a non-zero panel 6 corresponds one-for-one with the orchestrator emitting `Dangling next-step id {NextStepId} — skipping (business)` (src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:184) in the same window — one log line per increment, since stage-3 increments orchestrator_step_unresolved once per entry in selection.UnresolvedIds. Search the orchestrator logs for that string over the panel''s range before trusting a non-zero reading, and treat a green 0 as "either no dangling edge occurred OR the counter has never been emitted at all" — this phase could not tell those apart. The increment site is graceful (never throws, always acks), so a non-zero reading is credible when it does appear.'
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
            # MEASURED IN 88-07: the variable holds a step LABEL, not a boolean (ProcessorPipeline.cs:110-115
            # tests `d.Payload.Contains(defeatLabel)`). Step_C is the LINEAR critical-path hop between Step_B
            # and the D1/D2 fan-out in v8-fanout-proof, and `{"label": "Step_C", "number": 1}` is its verbatim
            # assignment payload — the same target the Phase-79 FALSIFY-01 control uses, for the same reason
            # (losing a post-fan-out hop can be masked by the convergent terminal Step_G).
            seamValue = 'Step_C'
            # ...and the label alone still fires NOTHING. The fault also needs the out-of-band Redis slot
            # `skp:test:defeat-read-arm` to exist so the hop can CLAIM it. That is armed at TRIGGER time only.
            requiresReinjectArm = $true
            triggerSeamTier = ''; triggerSeamVar = ''; triggerSeamValue = ''; dwellSeconds = 90
            panelIds = @('2')
            predictedDirection = @{ '2' = 'nonzero' }
            # Panel 8 is a CROSS-TALK CONTROL here, and it is the DISC-04 half of this scenario: the keeper
            # consumed AND sent, so there is nothing to gap. The plan's own verification requires panel 8 in
            # this list; the 88-05 table omitted it.
            crossTalkPanels = @('8','9','10','12')
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
            seamTier = 'keeper'; seamVar = 'KEEPER_DEFEAT_REINJECT'; seamValue = '1'
            triggerSeamTier = 'processor-sample'; triggerSeamVar = 'PROCESSOR_DEFEAT_READ'
            triggerSeamValue = 'Step_C'
            requiresReinjectArm = $true
            dwellSeconds = 90
            panelIds = @('8')
            predictedDirection = @{ '8' = 'nonzero' }
            crossTalkPanels = @('6','7','13','10','12')
            # OBSERVED, never scored and never a control. The whole ZERO-03 claim is "the keeper CONSUMED and
            # did NOT SEND", and panel 2 is the only rendered evidence that the first half happened at all. A
            # flat panel 8 beside a flat panel 2 means the fault never fired; a flat panel 8 beside a MOVED
            # panel 2 is a statement about the gap arithmetic. Without this reading those two are
            # indistinguishable in the artifact.
            observePanels = @('2')
            # Panel 8's own query terms, so a flat guarded 0 is DECOMPOSED rather than shrugged at.
            # `$__w` is substituted with each width below. The widths matter: 88-04 measured that
            # increase() needs >= 2 samples inside its range and the stored resolution here is 60 s, so
            # a 60 s reading of an increase() panel is structurally incapable of carrying a value —
            # which on a GUARDED panel is invisible, because `or vector(0)` renders the same 0.
            decompositionQueries = @(
                @{ Label = 'consumed increase'; Query = 'sum(increase(keeper_messages_consumed_total[$__w]))' }
                @{ Label = 'sent increase';     Query = 'sum(increase(keeper_messages_sent_total[$__w]))' }
                @{ Label = 'gap (unguarded)';   Query = 'sum(increase(keeper_messages_consumed_total[$__w])) - sum(increase(keeper_messages_sent_total[$__w]))' }
                @{ Label = 'gap (as rendered, guarded)'; Query = 'sum(increase(keeper_messages_consumed_total[$__w])) - sum(increase(keeper_messages_sent_total[$__w])) or vector(0)' }
            )
            decompositionWindows = @(60, 120, 600)
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
    # THE DURATION-LADDER RUNG TABLES — static, declared here beside the scenario table so the
    # -Rungs filter can be validated against them BEFORE any cluster access (T-88-13).
    #
    # COUNTER REGIME (panels 3 and 4, lever `scale` on processor-sample). A counter is cumulative:
    # a fault entirely between two exports still increments it, so the EVENT is never lost and only
    # its TIMING is smeared across the 240 s rate window. A fault of duration D that drops a rate to
    # zero therefore produces a trough of about `baseline x (1 - D/240)`.
    #
    # GAUGE REGIME (panel 14, lever `http`). A gauge is SAMPLED, not accumulated: a condition that
    # begins and ends between two 60 s exports is never recorded at all. The 180 s rung is driven
    # ONLY if all three of the declared gauge rungs fail to move the panel — it is a fallback the
    # research names, not a routine rung, and the artifact says which rungs were actually driven.
    # =========================================================================================
    $LadderCounterRungs      = @(30, 60, 120, 240, 480)
    $LadderGaugeRungs        = @(30, 60, 120)
    $LadderGaugeFallbackRung = 180
    $LadderAllRungDurations  = @(@($LadderCounterRungs) + @($LadderGaugeRungs) + @($LadderGaugeFallbackRung) | Sort-Object -Unique)

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
    # GUARD — the -Rungs RESUME FILTER (T-88-13). Validated against the STATIC rung list before any
    # cluster access. An unknown value exits 64: a silently-empty filter would produce an artifact
    # with no rungs, which is indistinguishable from a ladder that ran and measured nothing.
    # =========================================================================================
    $RungFilter = @()
    if (-not [string]::IsNullOrWhiteSpace($Rungs)) {
        if ($rowMode -ne 'ladder') {
            Write-Phase "-Rungs applies only to the duration-ladder row (LADDER-01); '$canonicalId' is a '$rowMode' row." 'Red'
            exit 64
        }
        $badRungs = @()
        foreach ($tok in @(("$Rungs") -split ',')) {
            $t = ("$tok").Trim()
            if ([string]::IsNullOrWhiteSpace($t)) { continue }
            $n = 0
            if (-not [int]::TryParse($t, [ref]$n) -or ($LadderAllRungDurations -notcontains $n)) { $badRungs += $t; continue }
            if ($RungFilter -notcontains $n) { $RungFilter += $n }
        }
        if (@($badRungs).Count -gt 0 -or @($RungFilter).Count -eq 0) {
            Write-Phase "-Rungs '$Rungs' names $(@($badRungs).Count) duration(s) that are not declared rungs: $(@($badRungs) -join ', ')" 'Red'
            Write-Phase "Declared rungs: counter [$(@($LadderCounterRungs) -join ', ')]s, gauge [$(@($LadderGaugeRungs) -join ', ')]s (+ ${LadderGaugeFallbackRung}s fallback)." 'Yellow'
            Write-Phase "REMEDIATION: pass a comma-separated SUBSET of those durations, or omit -Rungs to drive the whole ladder." 'Yellow'
            exit 64
        }
        Write-Phase "-Rungs filter active: only the [$(@($RungFilter) -join ', ')]s rung(s) will be driven; every other rung is carried forward from the existing artifact if one exists." 'Yellow'
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

    # The same aggregation as Get-ProxyAggregateMean but returning the PER-TIMESTAMP values rather
    # than their mean. A mean collapses "the expression was empty at every step" and "the expression
    # evaluated to zero at every step" into the same number — and on a guarded panel those two are the
    # whole question, because `A - empty` is empty and the `or vector(0)` guard then renders a 0 that
    # nothing measured. An EMPTY result is returned as an empty array so the caller can tell them apart.
    # Long parameter names deliberately: a single-letter typed parameter here is the 88-04 deviation-3
    # trap (PowerShell variable names are case-insensitive, so [long]$S and foreach($s) are one variable).
    function Get-ProxySeriesValues([string]$PromQuery, [long]$StartUnix, [long]$EndUnix, [int]$StepSeconds) {
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
                if (-not [double]::IsFinite($val)) { continue }
                if ($byTs.ContainsKey($ts)) { $byTs[$ts] += $val } else { $byTs[$ts] = $val }
            }
        }
        $out = @()
        foreach ($k in @($byTs.Keys | Sort-Object { [double]$_ })) { $out += [double]$byTs[$k] }
        return [double[]]@($out)
    }

    # Activate the fan-out workflow through the API. The workflow id is resolved ONCE into
    # $TrafficWorkflowId before the first call; a caller that has not resolved it gets $false rather
    # than an exception, because this is invoked from timing loops where a throw would skip a disarm.
    # Returns $true only on the documented 204.
    function Invoke-SeamActivation {
        if ([string]::IsNullOrWhiteSpace($TrafficWorkflowId)) { return $false }
        try {
            $r = Invoke-DriverApi -Method 'POST' -Path '/api/v1/orchestration/start' -Body (ConvertTo-Json @($TrafficWorkflowId))
            return ([int]$r.Status -eq 204)
        } catch { return $false }
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

    # =====================================================================================
    # SHARED FAULT-MODE HELPERS — the capture, banding, scoring and serialisation path that
    # BOTH `-Mode Scenario` (88-05) and `-Mode DurationLadder` (88-08) drive.
    #
    # They lived inside the scenario branch until 88-08. A function defined inside a branch that
    # does not run does not EXIST — PowerShell only sees a function once its definition statement
    # has executed — so the ladder could not have called one of them, and a second copy would have
    # meant the ladder's rungs were measured by different code from the scenarios they are compared
    # against. (Same fix, same reason, as 88-05 deviation 6 on Get-ProxyAggregateMean.)
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
    # THE UNION OF EVERY NAME THE PANEL RENDERED IN ANY SUB-WINDOW, not the by-index name.
    #
    # MEASURED IN ZERO-02 RUN 2. `$Reading.Entries` is built by ROW INDEX, and its Name comes from
    # the FIRST sub-window in which that index existed. A legend that GAINS rows mid-capture — which
    # is precisely the guarded-panel transition ZERO-02 exists to assert — therefore reports stale
    # names for every row after the insertion point. Panel 2 rendered, verbatim:
    #     windows 1-2:  consumed 0 ops/s | sent 0 ops/s
    #     windows 3-6:  consumed 0 | consumed keeper 0.00944 | sent 0 | sent keeper 0.00944
    # and the by-index collector returned `consumed, sent, sent, sent keeper` — silently dropping
    # `consumed keeper`, the exact name the scenario's second signal is about. Walking the readings
    # directly makes LegendNamesAfter a record of what was RENDERED rather than of how rows were
    # indexed. Names are returned in first-seen order and de-duplicated.
    function Get-Phase88LegendNames {
        [CmdletBinding()]
        param([Parameter(Mandatory)]$Reading, $Capture = $null, [string]$PanelId = '')
        $names = @()
        if ($null -ne $Capture -and -not [string]::IsNullOrWhiteSpace($PanelId)) {
            $batch = if ($PanelId -eq '4') { $Capture.Panel4Batch } else { $Capture.Batch }
            if ($null -ne $batch) {
                foreach ($r in @($batch.Readings)) {
                    $rn = @(Get-PropertyNames $r)
                    if ($rn -notcontains 'panelId' -or "$($r.panelId)" -ne $PanelId) { continue }
                    if ($rn -notcontains 'series') { continue }
                    foreach ($s in @($r.series)) {
                        $sn = @(Get-PropertyNames $s)
                        if ($sn -notcontains 'name') { continue }
                        $nm = "$($s.name)"
                        if ($names -notcontains $nm) { $names += $nm }
                    }
                }
            }
        }
        if (@($names).Count -gt 0) { return [string[]]$names }
        foreach ($e in @($Reading.Entries)) { if ($names -notcontains "$($e.Name)") { $names += "$($e.Name)" } }
        return [string[]]$names
    }

    # The legend rows rendered in EACH sub-window, in order, so the transition is auditable window
    # by window rather than as a merged set the reader must take on trust. The plan asks for exactly
    # this: "record both arrays verbatim ... so a reader can see the transition".
    function Get-Phase88LegendNamesPerWindow {
        [CmdletBinding()]
        param([Parameter(Mandatory)]$Capture, [Parameter(Mandatory)][string]$PanelId)
        $out = @()
        $batch = if ($PanelId -eq '4') { $Capture.Panel4Batch } else { $Capture.Batch }
        if ($null -eq $batch) { return @($out) }
        foreach ($r in @($batch.Readings)) {
            $rn = @(Get-PropertyNames $r)
            if ($rn -notcontains 'panelId' -or "$($r.panelId)" -ne $PanelId) { continue }
            $rowNames = @()
            if ($rn -contains 'series') {
                foreach ($s in @($r.series)) {
                    $sn = @(Get-PropertyNames $s)
                    $rowNames += $(if ($sn -contains 'name') { "$($s.name)" } else { '' })
                }
            }
            $out += [pscustomobject]@{
                FromUtc     = $(if ($rn -contains 'fromUtc') { "$($r.fromUtc)" } else { '' })
                ToUtc       = $(if ($rn -contains 'toUtc') { "$($r.toUtc)" } else { '' })
                LegendNames = [string[]]@($rowNames)
            }
        }
        return @($out)
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

        # =====================================================================================
        # -Mode DurationLadder — THE DISC-07 MINIMUM DETECTABLE FAULT DURATION (plan 88-08).
        #
        # THE ANSWER IS TWO NUMBERS, NOT ONE, and stating it as one would mislead maintenance in
        # exactly the direction this phase exists to prevent:
        #
        #   COUNTER REGIME (panels 3 and 4). A counter is CUMULATIVE. A fault occurring entirely
        #   between two exports still increments it, and the increment arrives in the very next
        #   export — so the EVENT is never lost and only its TIMING is smeared across the 240 s
        #   rate window. A fault of duration D that drops a rate to zero produces a trough of about
        #   `baseline x (1 - D/240)`. The smallest rung whose dip clears the Regime-A band for TWO
        #   consecutive samples is the measured answer.
        #
        #   GAUGE REGIME (panel 14). A gauge is SAMPLED, not accumulated. A condition that begins
        #   and ends between two 60 s exports is NEVER RECORDED AT ALL. A rung shorter than the
        #   export cadence can therefore move nothing, whatever the fault does — and that is
        #   recorded as `Moved` false, never retried until it passes.
        #
        #   REGIME B is a THIRD answer and is stated explicitly rather than folded into the other
        #   two: for a zero-floor guarded counter a SINGLE event is detectable at ANY duration,
        #   because the counter goes from 0 to 1 and stays there for the whole `$__range` window.
        #   That is the strongest detection property on this dashboard and maintenance should know
        #   it. It is a stated fact about the dashboard's design, corroborated by the ZERO-*
        #   scenarios, NOT a ladder measurement — see RegimeBDetectionNote.
        #
        # EVERY RUNG IS AN INDEPENDENT MEASUREMENT (T-88-24). Each one re-captures its OWN pre-rung
        # baseline band over ten 60 s sub-windows, and the next rung's baseline window may not begin
        # until the rate window has cleared FULLY (the derived RateIntervalSeconds) plus a settle of
        # at least 150 s past the previous fault's end. A rung measured on the tail of its
        # predecessor would report the predecessor's fault as its own.
        #
        # THE PREDICTION IS RECORDED BESIDE THE MEASUREMENT (T-88-25), never reconciled with it.
        # `DipFraction` (measured) sits next to `TheoreticalDipFraction` (the model), and a
        # divergence is a FINDING rather than a reason to adjust the theory.
        #
        # RESTORATION IS ASSERTED PER RUNG (T-88-02), not only at the end: a rung that fails to
        # restore invalidates every rung after it, so the ladder ABORTS with exit 65 rather than
        # continuing to produce numbers whose baseline is a degraded stack.
        # =====================================================================================

        # ---- the fixed shape of every capture in this mode -----------------------------------
        $SubWindowSeconds          = 60
        # MEASURED in 88-04: panel 4 renders "No data" at a 60 s sub-window (increase() needs >= 2
        # samples inside its range and the stored resolution here is 60 s), so it is read in its OWN
        # batch at 120 s and every entry states its own width.
        $Panel4SubWindowSeconds    = 120
        $ExportTrailSeconds        = 120   # two export cadences, so the LAST sub-window is stored
        $LadderBaselineCount       = 10    # the DISC-01 shape, re-taken PER RUNG
        $LadderBaselinePanel4Count = 5     # 5 x 120 s = the same 600 s span
        $LadderSettleSeconds       = 150   # two export cadences past the rate-window clear
        $MinBandSamples            = 3     # a thinner band cannot falsify a non-move (88-05)
        $LadderMinConsecutive      = 2     # a single 60 s excursion is a sampling artifact

        $CounterPanels       = @('3', '4')
        $CounterPrimaryPanel = '3'         # the RATE panel the counter theory is actually about
        $GaugePanels         = @('14')
        $GaugePrimaryPanel   = '14'
        # DISC-03. The counter rungs control on both declared panels. The GAUGE rungs control on
        # panel 9 ONLY, and panel 12 is deliberately excluded there with its reason stated: panel 12
        # is the WebApi status-code mix, which is causally DOWNSTREAM of the very HTTP load a gauge
        # rung drives — WEB-01 measured its 400 and 404 series moving off an exactly-zero band under
        # precisely this load. A control that the fault is expected to move is not a control; it
        # would report the driver's own footprint as a blast radius (the ZERO-02 run-1 defect).
        $LadderCounterControls = @(@($scenario.crossTalkPanels) | ForEach-Object { "$_" })
        $LadderGaugeControls   = @('9')
        $LadderTier            = "$($scenario.targetTier)"
        $shotDir               = Join-Path $screenshotRoot $canonicalId

        # ---- answer fields, initialised up front so the artifact writer can read any of them on
        # ---- any path under StrictMode.
        $LadderRungs               = @()
        $LadderFindings            = @()
        $LadderScreenshotPaths     = @()
        $LadderCrossTalk           = @()
        $LadderRateInterval        = $null
        $LadderTimeInterval        = $null
        $OldestSampleUtc           = $null
        $ReaderUnavailable         = $false
        $LadderHostLoadRequests    = 0
        $RunInParts                = $false
        $PartRuns                  = @()
        $RungsDriven               = @()
        $RungsCarriedForward       = @()
        $GaugeFallbackDriven       = $false
        $CrossTalkHeld             = $null
        $CrossTalkMeasuredOnRungs  = @()
        $LadderAbortReason         = ''
        $lastFaultEndUtc           = $null

        # =====================================================================================
        # STEP L1 — $__rate_interval and the evidentiary horizon, DERIVED (never assumed). PQ-04
        # measured 240 s and locked it for the phase; it is still derived here and whatever is
        # derived is what the rungs wait for, so the ladder can never wait for a stale constant.
        # =====================================================================================
        Write-Phase "STEP L1: derive `$__rate_interval and the evidentiary horizon"
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
                $LadderTimeInterval = $tiText
                $stepL = Get-QueryStepSeconds -RangeSeconds ($SubWindowSeconds * $LadderBaselineCount) -TimeIntervalSeconds $ti -MaxDataPoints $ViewportWidth
                $LadderRateInterval = Get-RateIntervalSeconds $ti $stepL
            }
        } catch {
            Write-Phase "  the datasource read failed: $($_.Exception.Message)" 'Yellow'
        }
        if ($null -eq $LadderRateInterval -or [int]$LadderRateInterval -lt 60) {
            Write-Phase "could not derive `$__rate_interval from the live datasource — every rung's independence wait is sized from it. Aborting." 'Red'
            exit 50
        }
        $LadderRateClearSeconds = [int]$LadderRateInterval
        Write-Phase "  timeInterval='$LadderTimeInterval' -> rate_interval=${LadderRateClearSeconds}s; each rung waits ${LadderRateClearSeconds}s + ${LadderSettleSeconds}s past its fault before the next baseline window may begin" 'Gray'

        $nowUnixL = ([DateTimeOffset]::UtcNow).ToUnixTimeSeconds()
        try {
            $oldestQ = Invoke-ProxyRangeQuery -Query 'up' -StartUnix ($nowUnixL - (336 * 3600)) -EndUnix $nowUnixL -StepSeconds 3600
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
        # LADDER HELPERS
        # =====================================================================================

        # A bounded, chatty wait. The ladder spends most of its wall time here (a rung's
        # independence discipline costs more than its fault does), so it reports what it is waiting
        # FOR — a silent 16-minute sleep in a detached log is indistinguishable from a hang.
        function Wait-LadderUntil {
            [CmdletBinding()]
            param([Parameter(Mandatory)][datetime]$TargetUtc, [Parameter(Mandatory)][string]$Why)
            while ($true) {
                $left = [int][Math]::Ceiling(($TargetUtc - [datetime]::UtcNow).TotalSeconds)
                if ($left -le 0) { break }
                Write-Phase ("  waiting {0}s ({1}) — {2}" -f $left, $TargetUtc.ToString('HH:mm:ss'), $Why) 'Gray'
                Start-Sleep -Seconds ([Math]::Min($left, 60))
            }
        }

        # Score ONE panel at ONE rung against that rung's OWN pre-rung band.
        #
        # The NoData run is evaluated for EVERY panel regardless of the value direction, because a
        # panel that EMPTIES renders no legend series at all and a series-only scorer would report
        # "nothing moved" for a panel that went completely blank (88-06 deviation 1). Panel 4 is
        # exactly that case: 88-06 measured that `A - empty` is empty, so under a processor outage
        # the stat renders "No data" rather than sloping up.
        function Get-LadderPanelOutcome {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory)]$BaselineCapture,
                [Parameter(Mandatory)]$AfterCapture,
                [Parameter(Mandatory)][string]$PanelId,
                [Parameter(Mandatory)][ValidateSet('down', 'up')][string]$Direction
            )

            $bands = @(New-Phase88BandSet -Capture $BaselineCapture -PanelIdList ([string[]]@($PanelId)))
            $rd    = Get-Phase88PanelReading -Capture $AfterCapture -PanelId $PanelId
            $states = [string[]]@($rd.States)

            $noDataTest = Test-PanelMoved -Band ([pscustomobject]@{ Low = 0.0; High = 0.0 }) `
                            -AfterStates $states -Direction 'nodata' -MinConsecutive $LadderMinConsecutive

            $seriesOut = @()
            $bestIdx   = -1
            $bestRun   = -1
            $bestDip   = $null
            for ($si = 0; $si -lt @($rd.Entries).Count; $si++) {
                $e = @($rd.Entries)[$si]
                $vals = [double[]]@($e.Values)
                $b = Find-Phase88Band -Bands ([object[]]@($bands)) -PanelId ([int]$PanelId) `
                       -SeriesName "$($e.Name)" -SeriesIndex ([int]$e.Index)
                if ($null -eq $b) {
                    $seriesOut += [pscustomobject]@{
                        SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"
                        BandLow = $null; BandHigh = $null; BandMean = $null; BandSampleCount = 0
                        ThinBand = $true; AfterValues = $vals; Moved = $false
                        ConsecutiveSamplesOutside = 0; DipFraction = $null
                        Note = 'this series carried NO pre-rung band — it did not render during the baseline window, so there is no null hypothesis it could have moved away from. Recorded, never scored.'
                    }
                    continue
                }
                $thin = ([int]$b.SampleCount -lt $MinBandSamples)
                $t = Test-PanelMoved -Band ([pscustomobject]@{ Low = [double]$b.BandLow; High = [double]$b.BandHigh }) `
                       -AfterValues $vals -Direction $Direction -MinConsecutive $LadderMinConsecutive
                # The measured fractional change from the band MEAN, in the direction being tested.
                # A band whose mean is zero has no fraction to take — the change from zero is not a
                # ratio — so the zero-mean case is flagged rather than divided, and the panel-level
                # resolution below turns it into the binary Regime-B form of the same question.
                $dip = $null
                $absChange = $null
                $zeroMean = ([Math]::Abs([double]$b.BandMean) -le 1e-9)
                if (@($vals).Count -gt 0) {
                    if ($Direction -eq 'down') {
                        $extreme = ($vals | Measure-Object -Minimum).Minimum
                        $absChange = (([double]$b.BandMean) - [double]$extreme)
                    } else {
                        $extreme = ($vals | Measure-Object -Maximum).Maximum
                        $absChange = ([double]$extreme - ([double]$b.BandMean))
                    }
                    if (-not $zeroMean) { $dip = ([double]$absChange / [double]$b.BandMean) }
                }
                $moved = ([bool]$t.Moved -and -not $thin)
                $seriesOut += [pscustomobject]@{
                    SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"
                    BandLow = [double]$b.BandLow; BandHigh = [double]$b.BandHigh
                    BandMean = [double]$b.BandMean; BandFloorApplied = [bool]$b.FloorApplied
                    BandSampleCount = [int]$b.SampleCount; ThinBand = $thin
                    BaselineValues = [double[]]@($b.Values)
                    AfterValues = $vals; Moved = $moved
                    ConsecutiveSamplesOutside = [int]$t.ConsecutiveSamplesOutside
                    DipFraction = $dip; AbsoluteChange = $absChange; ZeroMeanBand = $zeroMean
                    Note = $(if ($thin) { "band computed from $($b.SampleCount) sample(s) — fewer than $MinBandSamples, so it cannot falsify a non-move and this series is recorded rather than scored" } else { '' })
                }
                # The scored series is the one with the LONGEST run outside its band, tie-broken by
                # the deepest measured excursion. Every series' own outcome is kept, so the
                # selection is auditable rather than asserted.
                $runHere = [int]$t.ConsecutiveSamplesOutside
                if (-not $thin -and ($runHere -gt $bestRun -or ($runHere -eq $bestRun -and $null -ne $dip -and ($null -eq $bestDip -or $dip -gt $bestDip)))) {
                    $bestRun = $runHere; $bestIdx = $seriesOut.Count - 1; $bestDip = $dip
                }
            }

            $scored     = $(if ($bestIdx -ge 0) { $seriesOut[$bestIdx] } else { $null })
            $movedValue = ($null -ne $scored -and [bool]$scored.Moved)
            $movedND    = [bool]$noDataTest.Moved
            $movedBy    = 'none'
            if ($movedValue) { $movedBy = 'value' } elseif ($movedND) { $movedBy = 'nodata' }

            # PANEL-LEVEL DIP RESOLUTION. This number is never null, because "not computable" and
            # "zero" are different statements and a null in the artifact would collapse them into a
            # reader's guess. Each of the three bases is stated on the row that carries it.
            $panelDip = $null
            $panelDipBasis = ''
            if ($null -ne $scored -and $null -ne $scored.DipFraction) {
                $panelDip = [double]$scored.DipFraction
                $panelDipBasis = 'the fractional change of the scored series from its own pre-rung band MEAN, in the direction being tested'
            }
            elseif ($null -ne $scored -and [bool]$scored.ZeroMeanBand) {
                $panelDip = $(if ([bool]$scored.Moved) { 1.0 } else { 0.0 })
                $panelDipBasis = "the scored series' pre-rung band MEAN is exactly zero, so a fractional change from it is not a ratio — there is nothing to take a fraction OF. This records the Regime-B form of the same question instead: 1.0 when the panel left its exactly-zero band at all, 0.0 when it did not. AbsoluteChange on the series row carries the size of the movement in the panel's own units."
            }
            elseif ($null -eq $scored -and [int]$noDataTest.ConsecutiveSamplesOutside -ge 1) {
                $panelDip = 1.0
                $panelDipBasis = 'the panel EMPTIED — it rendered no legend series at all in the after window, which is a total loss of signal rather than a fractional drop. Recorded as 1.0 so that "the panel went blank" is never confused with "the value did not move".'
            }
            else {
                $panelDip = 0.0
                $panelDipBasis = 'no scored series and no NoData run — the panel rendered nothing measurable and nothing measurable changed. Recorded as 0.0 rather than null so that "measured, no change" and "not measured" stay distinguishable (Readable states which).'
            }

            return [pscustomobject]@{
                PanelId          = [int]$PanelId
                PanelTitle       = "$($Panels[$PanelId].Title)"
                Regime           = "$($Panels[$PanelId].Regime)"
                SubWindowSeconds = [int]$rd.SubWindowSeconds
                Direction        = $Direction
                Readable         = [bool]$rd.Readable
                ScoredSeriesIndex = $(if ($null -ne $scored) { [int]$scored.SeriesIndex } else { $null })
                ScoredSeriesName  = $(if ($null -ne $scored) { "$($scored.SeriesName)" } else { '' })
                BandLow          = $(if ($null -ne $scored) { $scored.BandLow } else { $null })
                BandHigh         = $(if ($null -ne $scored) { $scored.BandHigh } else { $null })
                BandMean         = $(if ($null -ne $scored) { $scored.BandMean } else { $null })
                BandFloorApplied = $(if ($null -ne $scored) { $scored.BandFloorApplied } else { $null })
                BandSampleCount  = $(if ($null -ne $scored) { $scored.BandSampleCount } else { 0 })
                BaselineValues   = $(if ($null -ne $scored) { $scored.BaselineValues } else { [double[]]@() })
                AfterValues      = $(if ($null -ne $scored) { $scored.AfterValues } else { [double[]]@() })
                AfterStates      = $states
                Moved            = ($movedValue -or $movedND)
                MovedBy          = $movedBy
                ConsecutiveSamplesOutside = $(if ($movedND -and -not $movedValue) { [int]$noDataTest.ConsecutiveSamplesOutside } elseif ($null -ne $scored) { [int]$scored.ConsecutiveSamplesOutside } else { 0 })
                PanelStateNoDataRun = [int]$noDataTest.ConsecutiveSamplesOutside
                PanelStateNoDataNote = 'computed for EVERY panel regardless of the value direction, so 0 always means "measured, no run" and never "nobody looked". MovedBy states whether the NoData run is what carried the movement.'
                DipFraction      = $panelDip
                DipFractionBasis = $panelDipBasis
                AbsoluteChange   = $(if ($null -ne $scored) { $scored.AbsoluteChange } else { $null })
                SeriesResults    = [object[]]@($seriesOut)
            }
        }

        # A cross-talk control, scored against the SAME rung's pre-rung band and read in the SAME
        # batch and the SAME windows as the claim (DISC-03). StayedPut is STRICT: inside the band
        # for the entire after window. A drift is recorded WITH its MaxExcursion, never
        # re-classified.
        function Get-LadderCrossTalk {
            [CmdletBinding()]
            param(
                [Parameter(Mandatory)]$BaselineCapture,
                [Parameter(Mandatory)]$AfterCapture,
                [Parameter(Mandatory)][string]$PanelId,
                [Parameter(Mandatory)][int]$FaultSeconds,
                [Parameter(Mandatory)][string]$Regime
            )
            $bands  = @(New-Phase88BandSet -Capture $BaselineCapture -PanelIdList ([string[]]@($PanelId)))
            $rd     = Get-Phase88PanelReading -Capture $AfterCapture -PanelId $PanelId
            $stayed = $true
            $maxExc = 0.0
            $applicable = $false
            $sr = @()
            foreach ($e in @($rd.Entries)) {
                $b = Find-Phase88Band -Bands ([object[]]@($bands)) -PanelId ([int]$PanelId) `
                       -SeriesName "$($e.Name)" -SeriesIndex ([int]$e.Index)
                if ($null -eq $b) {
                    $sr += [pscustomobject]@{ SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"; Applicable = $false; AfterValues = [double[]]@($e.Values); MaxExcursion = $null; Note = 'no pre-rung band for this control series' }
                    continue
                }
                $applicable = $true
                $exMax = 0.0
                foreach ($v in @($e.Values)) {
                    $ex = Get-Phase88Excursion -Value ([double]$v) -Low ([double]$b.BandLow) -High ([double]$b.BandHigh)
                    if ($ex -gt $exMax) { $exMax = $ex }
                }
                if ($exMax -gt 0) { $stayed = $false }
                if ($exMax -gt $maxExc) { $maxExc = $exMax }
                $sr += [pscustomobject]@{
                    SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"; Applicable = $true
                    BandLow = [double]$b.BandLow; BandHigh = [double]$b.BandHigh
                    AfterValues = [double[]]@($e.Values); MaxExcursion = $exMax
                    StayedPut = ($exMax -eq 0.0); Note = ''
                }
            }
            return [pscustomobject]@{
                PanelId = [int]$PanelId; PanelTitle = "$($Panels[$PanelId].Title)"
                Regime = $Regime; FaultSeconds = $FaultSeconds
                Applicable = $applicable
                SubWindowSeconds = [int]$rd.SubWindowSeconds
                AfterStates = [string[]]@($rd.States)
                StayedPut = $(if ($applicable) { $stayed } else { $null })
                MaxExcursion = $(if ($applicable) { $maxExc } else { $null })
                BandSource = "the pre-rung baseline of the ${FaultSeconds}s $Regime rung — a LOCAL reference band, because the DISC-01 band was captured hours earlier under a different host load and a comparison against it would report a difference in CONDITIONS as drift"
                SeriesResults = [object[]]@($sr)
            }
        }

        # An independent LIVE re-read of every tier's replica count, so a rung's ReplicasRestored is
        # a fresh reading rather than an echo of the sequencer's own claim (T-88-02). A rung that
        # did not restore invalidates every rung after it.
        function Test-LadderStackRestored {
            [CmdletBinding()]
            param()
            $mismatch = @()
            foreach ($t in $Tiers) {
                $live = [int](Get-LiveReplicas -Tier $t)
                if ($live -ne [int]$preReplicas[$t]) { $mismatch += "${t}: live=$live expected=$($preReplicas[$t])" }
            }
            return [pscustomobject]@{ Ok = (@($mismatch).Count -eq 0); Mismatches = [string[]]@($mismatch) }
        }

        # =====================================================================================
        # STEP L2 — RESUME MERGE. A ladder that aborted partway is re-run with -Rungs, and the
        # rungs it already measured are carried forward VERBATIM from the existing artifact rather
        # than re-driven. RunInParts records that the ladder was run in more than one part, so an
        # artifact assembled from two runs can never be mistaken for one continuous run.
        # =====================================================================================
        $reportPath = Join-Path $reportDir ("phase-88-{0}.json" -f $canonicalId)
        if (@($RungFilter).Count -gt 0 -and (Test-Path -LiteralPath $reportPath)) {
            try {
                $prev = Get-Content $reportPath -Raw | ConvertFrom-Json
                foreach ($pr in @($prev.Rungs)) {
                    if ($RungFilter -contains [int]$pr.FaultSeconds) { continue }
                    $RungsCarriedForward += "$($pr.Regime)/$($pr.FaultSeconds)s"
                    $LadderRungs += $pr
                }
                $PartRuns += "carried $(@($RungsCarriedForward).Count) rung(s) forward from the artifact completed $($prev.CompletedUtc)"
                $RunInParts = $true
                Write-Phase "STEP L2: carried forward [$(@($RungsCarriedForward) -join ', ')] from the existing artifact" 'Yellow'
            } catch {
                Write-Phase "STEP L2: the existing artifact could not be parsed and nothing was carried forward: $($_.Exception.Message)" 'Yellow'
            }
        }

        # =====================================================================================
        # STEP L3 — THE COUNTER LADDER (panels 3 and 4, lever `scale` on processor-sample).
        # =====================================================================================
        $counterMax = ($LadderCounterRungs | Measure-Object -Maximum).Maximum
        foreach ($rungSeconds in @($LadderCounterRungs)) {
            if (@($RungFilter).Count -gt 0 -and $RungFilter -notcontains $rungSeconds) {
                Write-Phase "STEP L3: counter rung ${rungSeconds}s SKIPPED by the -Rungs filter" 'Yellow'
                continue
            }
            Write-Phase "STEP L3: COUNTER rung ${rungSeconds}s (lever scale on '$LadderTier', panels $(@($CounterPanels) -join '/'))"
            $rungStartUtc = [datetime]::UtcNow

            # --- independence (T-88-24): the pre-rung baseline window must lie ENTIRELY after the
            # --- previous rung's rate window cleared and the stack settled.
            if ($null -ne $lastFaultEndUtc) {
                $earliestBaselineStart = ([datetime]$lastFaultEndUtc).AddSeconds($LadderRateClearSeconds + $LadderSettleSeconds)
                $earliestCaptureAt     = $earliestBaselineStart.AddSeconds(($LadderBaselineCount * $SubWindowSeconds) + $ExportTrailSeconds)
                Wait-LadderUntil -TargetUtc $earliestCaptureAt -Why "rung independence: this rung's baseline window may not begin until ${LadderRateClearSeconds}s (the rate window) + ${LadderSettleSeconds}s past the previous fault"
            }

            $isControlRung = ($rungSeconds -eq $counterMax)
            $capPanels = [string[]]@(@($CounterPanels) + $(if ($isControlRung) { @($LadderCounterControls) } else { @() }) | Select-Object -Unique)

            # --- the pre-rung baseline, over the window that has just ELAPSED (offset by the export
            # --- trail so every sample in it is already stored). It costs no wall time.
            $baseEnd = ([datetime]::UtcNow).AddSeconds(-$ExportTrailSeconds)
            $baseCap = Invoke-Phase88Capture -PanelIdList $capPanels -EndUtc $baseEnd `
                         -Count $LadderBaselineCount -Panel4Count $LadderBaselinePanel4Count `
                         -ShotDir $shotDir -Label "counter-${rungSeconds}s-baseline"
            $LadderScreenshotPaths = @(@($LadderScreenshotPaths) + @($baseCap.ScreenshotPaths) | Select-Object -Unique)
            if ("$($baseCap.State)" -ne 'Ok') { $ReaderUnavailable = $true; $LadderFindings += "counter rung ${rungSeconds}s: the baseline batch reported State=$($baseCap.State)" }

            # --- the fault. The restore target is the count READ before the mutation, never a
            # --- tabled one; the outer teardown slots are set so an interrupted run still restores.
            $scaledTier     = $LadderTier
            $replicasBefore = [int](Get-LiveReplicas -Tier $LadderTier)
            $fault = Invoke-TierScaleFault -Tier $LadderTier -DwellSeconds $rungSeconds
            $scaledTier     = ''
            $replicasBefore = -1
            if (-not $fault.Ok) {
                $LadderAbortReason = "counter rung ${rungSeconds}s: the scale fault failed (code $($fault.FailureCode)): $($fault.Detail)"
                Write-Phase $LadderAbortReason 'Red'
                break
            }
            $faultStartUtc = ([datetimeoffset]::Parse("$($fault.FaultStartUtc)")).UtcDateTime
            $faultEndUtc   = ([datetimeoffset]::Parse("$($fault.FaultEndUtc)")).UtcDateTime

            # --- the after window. It ENDS one sub-window past the fault, because a 240 s rate
            # --- lookback is at its deepest exactly at the fault's end and recovers as the window
            # --- slides forward; it reaches back far enough to hold the whole fault plus at least
            # --- one healthy sub-window, and its count is EVEN so panel 4's 120 s batch divides it.
            $afterCount = [int][Math]::Max(6, [Math]::Ceiling(($rungSeconds + 120) / 60.0))
            if ($afterCount % 2 -ne 0) { $afterCount++ }
            $afterEnd = $faultEndUtc.AddSeconds($SubWindowSeconds)

            # --- wait for the rate window to clear FULLY before reading, so the whole after window
            # --- is stored and the NEXT rung is not measured on this one's tail.
            Wait-LadderUntil -TargetUtc ($faultEndUtc.AddSeconds($LadderRateClearSeconds + $LadderSettleSeconds)) `
              -Why "letting the ${LadderRateClearSeconds}s rate window clear fully (+${LadderSettleSeconds}s settle) before the after capture"

            $afterCap = Invoke-Phase88Capture -PanelIdList $capPanels -EndUtc $afterEnd `
                          -Count $afterCount -Panel4Count ($afterCount / 2) `
                          -ShotDir $shotDir -Label "counter-${rungSeconds}s-after"
            $LadderScreenshotPaths = @(@($LadderScreenshotPaths) + @($afterCap.ScreenshotPaths) | Select-Object -Unique)
            if ("$($afterCap.State)" -ne 'Ok') { $ReaderUnavailable = $true; $LadderFindings += "counter rung ${rungSeconds}s: the after batch reported State=$($afterCap.State)" }

            # --- score
            $outcomes = @()
            foreach ($p in @($CounterPanels)) {
                $outcomes += Get-LadderPanelOutcome -BaselineCapture $baseCap -AfterCapture $afterCap -PanelId $p -Direction 'down'
            }
            if ($isControlRung) {
                foreach ($c in @($LadderCounterControls)) {
                    $ct = Get-LadderCrossTalk -BaselineCapture $baseCap -AfterCapture $afterCap -PanelId $c -FaultSeconds $rungSeconds -Regime 'counter'
                    $LadderCrossTalk += $ct
                    if ($null -ne $ct.StayedPut -and -not $ct.StayedPut) {
                        $LadderFindings += "cross-talk control panel $c DRIFTED during the ${rungSeconds}s counter rung (MaxExcursion $([Math]::Round([double]$ct.MaxExcursion, 4)))"
                    }
                }
                $CrossTalkMeasuredOnRungs += "counter/${rungSeconds}s"
            }

            $primary = @($outcomes | Where-Object { [int]$_.PanelId -eq [int]$CounterPrimaryPanel })[0]
            $rungMoved = (@($outcomes | Where-Object { $_.Moved }).Count -gt 0)
            $theoretical = [double]([Math]::Min($rungSeconds, $LadderRateClearSeconds) / [double]$LadderRateClearSeconds)

            $restoreCheck = Test-LadderStackRestored
            $rungEndUtc = [datetime]::UtcNow

            $LadderRungs += [pscustomobject]@{
                Regime           = 'counter'
                PanelIds         = [string[]]@($CounterPanels)
                FaultSeconds     = $rungSeconds
                Lever            = 'scale'
                Tier             = $LadderTier
                PrimaryPanelId   = [int]$CounterPrimaryPanel
                BaselineBand     = [pscustomobject]@{
                    PanelId = [int]$CounterPrimaryPanel; SeriesName = "$($primary.ScoredSeriesName)"
                    Low = $primary.BandLow; High = $primary.BandHigh; Mean = $primary.BandMean
                    FloorApplied = $primary.BandFloorApplied; SampleCount = $primary.BandSampleCount
                    SubWindowSeconds = [int]$primary.SubWindowSeconds
                    WindowStartUtc = "$($baseCap.WindowStartUtc)"; WindowEndUtc = "$($baseCap.WindowEndUtc)"
                }
                BaselineValues   = [double[]]@($primary.BaselineValues)
                AfterValues      = [double[]]@($primary.AfterValues)
                AfterStates      = [string[]]@($primary.AfterStates)
                Moved            = $rungMoved
                MovedPanelIds    = [string[]]@(@($outcomes | Where-Object { $_.Moved } | ForEach-Object { "$($_.PanelId)" }))
                ConsecutiveSamplesOutside = [int]$primary.ConsecutiveSamplesOutside
                DipFraction      = $primary.DipFraction
                DipFractionBasis = "$($primary.DipFractionBasis)"
                DipFractionKind  = "the MEASURED fractional DROP of panel $CounterPrimaryPanel's scored series from its own pre-rung band mean: (bandMean - min(afterValues)) / bandMean. Panel 4 is excluded from this number on purpose — it is an increase() stat that EMPTIES rather than dipping, so a 'dip fraction' for it would not be the same quantity."
                TheoreticalDipFraction = $theoretical
                TheoreticalDipFractionKind = "min(D, rateInterval) / rateInterval — the fractional DROP the smearing model predicts, i.e. the fraction of the ${LadderRateClearSeconds}s rate lookback the fault occupies. NOTE: plan 88-08's text writes this expression as `1 - min(D,240)/240`, which is the RESIDUAL trough as a fraction of baseline (recorded here as TheoreticalTroughFraction) and NOT a drop. Comparing a measured DROP against a modelled RESIDUAL would manufacture a divergence at every rung, so the two quantities are both recorded and neither is silently substituted for the other."
                TheoreticalTroughFraction = [double](1.0 - $theoretical)
                PredictionDivergence = [double]([Math]::Abs([double]$primary.DipFraction - $theoretical))
                PredictionDivergenceComparable = ("$($primary.DipFractionBasis)" -like 'the fractional change*')
                RungStartUtc     = $rungStartUtc.ToString('o')
                RungEndUtc       = $rungEndUtc.ToString('o')
                FaultStartUtc    = $faultStartUtc.ToString('o')
                FaultEndUtc      = $faultEndUtc.ToString('o')
                AfterWindowStart = "$($afterCap.WindowStartUtc)"
                AfterWindowEnd   = "$($afterCap.WindowEndUtc)"
                SubWindowSeconds = $SubWindowSeconds
                SubWindowCount   = $afterCount
                ReplicasBefore   = [int]$fault.ReplicasBefore
                ReplicasAfter    = [int]$fault.ReplicasAfter
                ReplicasRestored = ([bool]$fault.ReplicasRestored -and [bool]$restoreCheck.Ok)
                ReplicasRestoredDetail = $(if ($restoreCheck.Ok) { 'the sequencer claimed the restore AND an independent live re-read of all four tiers agreed' } else { "independent live re-read DISAGREED: $(@($restoreCheck.Mismatches) -join '; ')" })
                CrossTalkMeasured = $isControlRung
                PanelOutcomes    = [object[]]@($outcomes)
            }
            $RungsDriven += "counter/${rungSeconds}s"
            $lastFaultEndUtc = $faultEndUtc

            $div = [Math]::Abs([double]$primary.DipFraction - $theoretical)
            Write-Phase ("  rung {0}s: moved={1} dip={2:N3} theoretical={3:N3} divergence={4:N3} consecutive={5}" -f $rungSeconds, $rungMoved, [double]$primary.DipFraction, $theoretical, $div, $primary.ConsecutiveSamplesOutside) $(if ($rungMoved) { 'Green' } else { 'Yellow' })
            # The divergence is only a comparison of LIKE quantities when the measured number is a
            # fractional change from a non-zero band mean. Where the panel emptied or its band mean
            # was zero, the measured number is a different KIND and the comparison is skipped rather
            # than reported as a disagreement the theory never claimed.
            if ($div -gt (1.0 / 3.0) -and "$($primary.DipFractionBasis)" -like 'the fractional change*') {
                $LadderFindings += ("counter rung ${rungSeconds}s: the MEASURED dip fraction $([Math]::Round([double]$primary.DipFraction,3)) diverges from the theoretical $([Math]::Round($theoretical,3)) by $([Math]::Round($div,3)) — more than a third. Recorded as a finding; the theory is NOT adjusted to fit the measurement.")
            }

            if (-not ([bool]$fault.ReplicasRestored -and [bool]$restoreCheck.Ok)) {
                $LadderAbortReason = "counter rung ${rungSeconds}s did NOT restore ($($restoreCheck.Mismatches -join '; ')) — every rung after it would be measured against a degraded stack, so the ladder aborts here."
                Write-Phase $LadderAbortReason 'Red'
                break
            }
        }

        # =====================================================================================
        # STEP L4 — THE GAUGE LADDER (panel 14, lever `http`).
        #
        # The load runs for the RUNG'S DURATION ONLY. Because the three series are gauges sampled at
        # the 60 s export cadence, a rung shorter than the cadence can supply at most ONE affected
        # sample and the two-consecutive-sample rule is then unsatisfiable BY CONSTRUCTION — that is
        # recorded as Moved false with MaxPossibleConsecutiveSamples stating why, never retried
        # until it passes.
        # =====================================================================================
        if ([string]::IsNullOrWhiteSpace($LadderAbortReason)) {
            $gaugeRungList = @($LadderGaugeRungs)
            $gi = 0
            while ($gi -lt @($gaugeRungList).Count) {
                $rungSeconds = [int]@($gaugeRungList)[$gi]
                $gi++
                if (@($RungFilter).Count -gt 0 -and $RungFilter -notcontains $rungSeconds) {
                    Write-Phase "STEP L4: gauge rung ${rungSeconds}s SKIPPED by the -Rungs filter" 'Yellow'
                    continue
                }
                Write-Phase "STEP L4: GAUGE rung ${rungSeconds}s (lever http, panel $(@($GaugePanels) -join '/'))"
                $rungStartUtc = [datetime]::UtcNow

                if ($null -ne $lastFaultEndUtc) {
                    $earliestBaselineStart = ([datetime]$lastFaultEndUtc).AddSeconds($LadderRateClearSeconds + $LadderSettleSeconds)
                    $earliestCaptureAt     = $earliestBaselineStart.AddSeconds(($LadderBaselineCount * $SubWindowSeconds) + $ExportTrailSeconds)
                    Wait-LadderUntil -TargetUtc $earliestCaptureAt -Why "rung independence: this rung's baseline window may not begin until ${LadderRateClearSeconds}s + ${LadderSettleSeconds}s past the previous fault"
                }

                $isControlRung = ($rungSeconds -eq (@($gaugeRungList) | Measure-Object -Maximum).Maximum)
                $capPanels = [string[]]@(@($GaugePanels) + $(if ($isControlRung) { @($LadderGaugeControls) } else { @() }) | Select-Object -Unique)

                $baseEnd = ([datetime]::UtcNow).AddSeconds(-$ExportTrailSeconds)
                $baseCap = Invoke-Phase88Capture -PanelIdList $capPanels -EndUtc $baseEnd `
                             -Count $LadderBaselineCount -Panel4Count 0 `
                             -ShotDir $shotDir -Label "gauge-${rungSeconds}s-baseline"
                $LadderScreenshotPaths = @(@($LadderScreenshotPaths) + @($baseCap.ScreenshotPaths) | Select-Object -Unique)
                if ("$($baseCap.State)" -ne 'Ok') { $ReaderUnavailable = $true; $LadderFindings += "gauge rung ${rungSeconds}s: the baseline batch reported State=$($baseCap.State)" }

                # --- the fault: the SAME sustained concurrency WEB-01 drove, for this rung only.
                $faultStartUtc = [datetime]::UtcNow
                $loadJobs = @(Start-Phase88HttpLoad -Shape 'sustained' -DurationSeconds $rungSeconds -BaseUri $api)
                Start-Sleep -Seconds $rungSeconds
                $LadderHostLoadRequests += (Stop-Phase88HttpLoad -Jobs $loadJobs)
                $loadJobs = @()
                $faultEndUtc = [datetime]::UtcNow

                $afterCount = 6
                $afterEnd   = $faultEndUtc.AddSeconds($SubWindowSeconds)
                Wait-LadderUntil -TargetUtc ($faultEndUtc.AddSeconds($LadderRateClearSeconds + $LadderSettleSeconds)) `
                  -Why "letting the export trail and the ${LadderRateClearSeconds}s window clear fully (+${LadderSettleSeconds}s settle) before the after capture"

                $afterCap = Invoke-Phase88Capture -PanelIdList $capPanels -EndUtc $afterEnd `
                              -Count $afterCount -Panel4Count 0 `
                              -ShotDir $shotDir -Label "gauge-${rungSeconds}s-after"
                $LadderScreenshotPaths = @(@($LadderScreenshotPaths) + @($afterCap.ScreenshotPaths) | Select-Object -Unique)
                if ("$($afterCap.State)" -ne 'Ok') { $ReaderUnavailable = $true; $LadderFindings += "gauge rung ${rungSeconds}s: the after batch reported State=$($afterCap.State)" }

                $outcomes = @()
                foreach ($p in @($GaugePanels)) {
                    $outcomes += Get-LadderPanelOutcome -BaselineCapture $baseCap -AfterCapture $afterCap -PanelId $p -Direction 'up'
                }
                if ($isControlRung) {
                    foreach ($c in @($LadderGaugeControls)) {
                        $ct = Get-LadderCrossTalk -BaselineCapture $baseCap -AfterCapture $afterCap -PanelId $c -FaultSeconds $rungSeconds -Regime 'gauge'
                        $LadderCrossTalk += $ct
                        if ($null -ne $ct.StayedPut -and -not $ct.StayedPut) {
                            $LadderFindings += "cross-talk control panel $c DRIFTED during the ${rungSeconds}s gauge rung (MaxExcursion $([Math]::Round([double]$ct.MaxExcursion, 4)))"
                        }
                    }
                    $CrossTalkMeasuredOnRungs += "gauge/${rungSeconds}s"
                }

                $primary = @($outcomes | Where-Object { [int]$_.PanelId -eq [int]$GaugePrimaryPanel })[0]
                $rungMoved = (@($outcomes | Where-Object { $_.Moved }).Count -gt 0)
                $maxPossible = [int][Math]::Floor($rungSeconds / [double]$SubWindowSeconds)
                $theoretical = [double]([Math]::Min($maxPossible, $LadderMinConsecutive) / [double]$LadderMinConsecutive)

                $restoreCheck = Test-LadderStackRestored
                $rungEndUtc = [datetime]::UtcNow

                $LadderRungs += [pscustomobject]@{
                    Regime           = 'gauge'
                    PanelIds         = [string[]]@($GaugePanels)
                    FaultSeconds     = $rungSeconds
                    Lever            = 'http'
                    Tier             = ''
                    PrimaryPanelId   = [int]$GaugePrimaryPanel
                    BaselineBand     = [pscustomobject]@{
                        PanelId = [int]$GaugePrimaryPanel; SeriesName = "$($primary.ScoredSeriesName)"
                        Low = $primary.BandLow; High = $primary.BandHigh; Mean = $primary.BandMean
                        FloorApplied = $primary.BandFloorApplied; SampleCount = $primary.BandSampleCount
                        SubWindowSeconds = [int]$primary.SubWindowSeconds
                        WindowStartUtc = "$($baseCap.WindowStartUtc)"; WindowEndUtc = "$($baseCap.WindowEndUtc)"
                    }
                    BaselineValues   = [double[]]@($primary.BaselineValues)
                    AfterValues      = [double[]]@($primary.AfterValues)
                    AfterStates      = [string[]]@($primary.AfterStates)
                    Moved            = $rungMoved
                    MovedPanelIds    = [string[]]@(@($outcomes | Where-Object { $_.Moved } | ForEach-Object { "$($_.PanelId)" }))
                    ConsecutiveSamplesOutside = [int]$primary.ConsecutiveSamplesOutside
                    DipFraction      = $primary.DipFraction
                    DipFractionBasis = "$($primary.DipFractionBasis)"
                    DipFractionKind  = "the MEASURED fractional EXCURSION ABOVE panel $GaugePrimaryPanel's pre-rung band mean: (max(afterValues) - bandMean) / bandMean. The gauge regime moves UPWARD under load, so this is a rise, not a drop — it is NOT comparable with the counter rungs' DipFraction and must never be plotted on the same axis."
                    TheoreticalDipFraction = $theoretical
                    TheoreticalDipFractionKind = "min(floor(D / 60), 2) / 2 — the fraction of the two-consecutive-sample minimum that a fault of this duration can POSSIBLY supply, given that a gauge is sampled once per 60 s export. It is a detectability fraction, not a depth: at 1.0 the rung can in principle be detected, and below 1.0 the two-consecutive rule is unsatisfiable BY CONSTRUCTION however large the perturbation."
                    TheoreticalTroughFraction = [double](1.0 - $theoretical)
                    MaxPossibleConsecutiveSamples = $maxPossible
                    PredictionDivergence = $null
                    RungStartUtc     = $rungStartUtc.ToString('o')
                    RungEndUtc       = $rungEndUtc.ToString('o')
                    FaultStartUtc    = $faultStartUtc.ToString('o')
                    FaultEndUtc      = $faultEndUtc.ToString('o')
                    AfterWindowStart = "$($afterCap.WindowStartUtc)"
                    AfterWindowEnd   = "$($afterCap.WindowEndUtc)"
                    SubWindowSeconds = $SubWindowSeconds
                    SubWindowCount   = $afterCount
                    ReplicasBefore   = [int]$preReplicas['baseapi-service']
                    ReplicasAfter    = [int](Get-LiveReplicas -Tier 'baseapi-service')
                    ReplicasRestored = [bool]$restoreCheck.Ok
                    ReplicasRestoredDetail = $(if ($restoreCheck.Ok) { 'this rung SCALED NOTHING (its lever is host HTTP load); an independent live re-read confirms all four tiers still sit at their pre-run counts' } else { "independent live re-read DISAGREED: $(@($restoreCheck.Mismatches) -join '; ')" })
                    CrossTalkMeasured = $isControlRung
                    PanelOutcomes    = [object[]]@($outcomes)
                }
                $RungsDriven += "gauge/${rungSeconds}s"
                $lastFaultEndUtc = $faultEndUtc
                Write-Phase ("  rung {0}s: moved={1} consecutive={2} maxPossibleConsecutive={3}" -f $rungSeconds, $rungMoved, $primary.ConsecutiveSamplesOutside, $maxPossible) $(if ($rungMoved) { 'Green' } else { 'Yellow' })

                if (-not $restoreCheck.Ok) {
                    $LadderAbortReason = "gauge rung ${rungSeconds}s left the stack off its pre-run replica counts ($($restoreCheck.Mismatches -join '; ')) — the ladder aborts here."
                    Write-Phase $LadderAbortReason 'Red'
                    break
                }

                # The 180 s FALLBACK rung, driven ONLY if all three declared gauge rungs failed to
                # move the panel. The research names it; it is not a routine rung, and the artifact
                # states whether it was driven.
                if ($gi -eq @($gaugeRungList).Count -and -not $GaugeFallbackDriven -and @($RungFilter).Count -eq 0) {
                    $gaugeSoFar = @($LadderRungs | Where-Object { "$($_.Regime)" -eq 'gauge' })
                    if (@($gaugeSoFar).Count -ge 1 -and @($gaugeSoFar | Where-Object { $_.Moved }).Count -eq 0) {
                        Write-Phase "STEP L4: all declared gauge rungs failed to move panel 14 — driving the ${LadderGaugeFallbackRung}s fallback rung the research names" 'Yellow'
                        $gaugeRungList = @(@($gaugeRungList) + @($LadderGaugeFallbackRung))
                        $GaugeFallbackDriven = $true
                    }
                }
            }
        }

        # =====================================================================================
        # STEP L5 — RESOLVE BOTH REGIME ANSWERS, ASSERT THE STACK, WRITE THE ARTIFACT.
        # =====================================================================================
        Write-Phase "STEP L5: resolve both regime answers, assert the stack, write the artifact"
        if (@($loadJobs).Count -gt 0) {
            $LadderHostLoadRequests += (Stop-Phase88HttpLoad -Jobs $loadJobs)
            $loadJobs = @()
        }

        $counterRungs = @($LadderRungs | Where-Object { "$($_.Regime)" -eq 'counter' } | Sort-Object { [int]$_.FaultSeconds })
        $gaugeRungs   = @($LadderRungs | Where-Object { "$($_.Regime)" -eq 'gauge' }   | Sort-Object { [int]$_.FaultSeconds })

        $MinDetectableCounterSeconds = $null
        $MinDetectableCounterNote    = ''
        $movedCounter = @($counterRungs | Where-Object { $_.Moved } | Sort-Object { [int]$_.FaultSeconds })
        if (@($movedCounter).Count -gt 0) {
            $MinDetectableCounterSeconds = [int]$movedCounter[0].FaultSeconds
            $MinDetectableCounterNote = "the smallest COUNTER rung whose panel(s) left the pre-rung band for $LadderMinConsecutive consecutive 60 s sub-windows. Rungs driven: [$(@($counterRungs | ForEach-Object { "$($_.FaultSeconds)s:$(if($_.Moved){'moved'}else{'no'})" }) -join ' ')]."
        } else {
            $MinDetectableCounterNote = "NULL, not the largest rung: no counter rung moved a panel for $LadderMinConsecutive consecutive sub-windows, so this ladder measured NO detectable duration in the counter regime rather than measuring the top of its own range. Rungs driven: [$(@($counterRungs | ForEach-Object { "$($_.FaultSeconds)s:no" }) -join ' ')]."
        }

        $MinDetectableGaugeSeconds = $null
        $MinDetectableGaugeNote    = ''
        $movedGauge = @($gaugeRungs | Where-Object { $_.Moved } | Sort-Object { [int]$_.FaultSeconds })
        if (@($movedGauge).Count -gt 0) {
            $MinDetectableGaugeSeconds = [int]$movedGauge[0].FaultSeconds
            $MinDetectableGaugeNote = "the smallest GAUGE rung whose panel left the pre-rung band for $LadderMinConsecutive consecutive 60 s sub-windows. Rungs driven: [$(@($gaugeRungs | ForEach-Object { "$($_.FaultSeconds)s:$(if($_.Moved){'moved'}else{'no'})" }) -join ' ')]."
        } else {
            $MinDetectableGaugeNote = "NULL, not the largest rung: no gauge rung moved panel 14 for $LadderMinConsecutive consecutive sub-windows. Rungs driven: [$(@($gaugeRungs | ForEach-Object { "$($_.FaultSeconds)s:no" }) -join ' ')]."
        }

        $PredictedCounterSeconds = 120
        $PredictedGaugeSeconds   = 120
        $counterAgrees = ($null -ne $MinDetectableCounterSeconds -and [int]$MinDetectableCounterSeconds -eq $PredictedCounterSeconds)
        $gaugeAgrees   = ($null -ne $MinDetectableGaugeSeconds   -and [int]$MinDetectableGaugeSeconds   -eq $PredictedGaugeSeconds)
        $PredictionAgreesWithMeasurement = ($counterAgrees -and $gaugeAgrees)
        $PredictionAgreementDetail = "counter: predicted ${PredictedCounterSeconds}s, measured $(if($null -eq $MinDetectableCounterSeconds){'null'}else{"$MinDetectableCounterSeconds`s"}) -> $(if($counterAgrees){'AGREES'}else{'DISAGREES'}); gauge: predicted ${PredictedGaugeSeconds}s, measured $(if($null -eq $MinDetectableGaugeSeconds){'null'}else{"$MinDetectableGaugeSeconds`s"}) -> $(if($gaugeAgrees){'AGREES'}else{'DISAGREES'}). Both the prediction and the measurement are recorded so a disagreement is VISIBLE rather than smoothed over, and so a future re-measurement can tell whether it disagrees because the stack changed or because a run was noisy."

        $RegimeBDetectionNote = 'REGIME B IS A THIRD ANSWER AND IS NOT A LADDER MEASUREMENT. For a zero-floor guarded counter — panels 2, 6, 7, 8 and 13, whose DISC-01 bands are all exactly 0..0 — a SINGLE event is detectable at ANY duration, because the counter goes from 0 to 1 and STAYS there for the whole `$__range` window rather than decaying back. There is no minimum duration to measure: duration does not enter the question. That is the strongest detection property on this dashboard and maintenance should know it. It is a stated fact about the dashboard''s design (`sum(increase(counter[$__range])) or vector(0)`), corroborated by the ZERO-* scenarios rather than measured here: ZERO-02 drove a real keeper recovery event and panel 2 left its exactly-0..0 band for four consecutive sub-windows. TWO CAVEATS THAT MATTER MORE THAN THE PROPERTY: (1) the same guard makes a green 0 indistinguishable from "the counter has never been emitted at all", which is why panels 6, 7, 8 and 13 are in the accepted-unproven register; and (2) the property belongs to the increase()/$__range form, NOT to rate() — 88-07 measured that panel 2''s rate() form is BLIND to a recovery burst confined to a single 60 s export interval, because the counter series is born carrying its final value and rate() differences consecutive samples.'

        $restore = Assert-StackRestored -Tiers $Tiers -ExpectedReplicas $preReplicas -ExpectedImages $preImages
        Write-Phase "  restore: SeamVarsClean=$($restore.SeamVarsClean) ReplicasRestored=$($restore.ReplicasRestored) ImagesUnchanged=$($restore.ImagesUnchanged)" 'Gray'

        $allRungsRestored = ((@($LadderRungs).Count -gt 0) -and (@($LadderRungs | Where-Object { -not $_.ReplicasRestored }).Count -eq 0))
        $ctApplicable = @($LadderCrossTalk | Where-Object { $_.Applicable })
        $CrossTalkHeld = $(if (@($ctApplicable).Count -eq 0) { $null } else { (@($ctApplicable | Where-Object { -not $_.StayedPut }).Count -eq 0) })

        # A rung that did NOT move is a MEASUREMENT here, not a discrimination failure: the whole
        # point of a ladder is to find where detection stops, so the scenario engine's "a panel that
        # was read and did not move is a Fail" rule deliberately does NOT apply. Fail is reserved
        # for a dirty stack or a control that drifted — claims that were evaluated and came back
        # false about something other than the measurement itself.
        $verdict = 'Pass'
        if (-not $restore.Ok -or -not $allRungsRestored -or ($null -ne $CrossTalkHeld -and -not $CrossTalkHeld)) { $verdict = 'Fail' }
        elseif ($ReaderUnavailable -or -not [string]::IsNullOrWhiteSpace($LadderAbortReason)) { $verdict = 'Inconclusive' }
        elseif ($null -eq $MinDetectableCounterSeconds -or $null -eq $MinDetectableGaugeSeconds) { $verdict = 'Inconclusive' }
        elseif (@($LadderFindings).Count -gt 0) { $verdict = 'Inconclusive' }

        $human = "phase-88 $canonicalId verdict=${verdict}: DISC-07 measured in TWO regimes — " +
                 "minDetectableCounter=$(if($null -eq $MinDetectableCounterSeconds){'null'}else{"${MinDetectableCounterSeconds}s"}) (predicted ${PredictedCounterSeconds}s), " +
                 "minDetectableGauge=$(if($null -eq $MinDetectableGaugeSeconds){'null'}else{"${MinDetectableGaugeSeconds}s"}) (predicted ${PredictedGaugeSeconds}s), " +
                 "predictionAgrees=$PredictionAgreesWithMeasurement | " +
                 "counter rungs [$(@($counterRungs | ForEach-Object { "$($_.FaultSeconds)s:$(if($_.Moved){'moved'}else{'no'})" }) -join ' ')] | " +
                 "gauge rungs [$(@($gaugeRungs | ForEach-Object { "$($_.FaultSeconds)s:$(if($_.Moved){'moved'}else{'no'})" }) -join ' ')] | " +
                 "regime B: a single event is detectable at ANY duration (stated, not measured) | " +
                 "crossTalkHeld=$CrossTalkHeld measured on [$(@($CrossTalkMeasuredOnRungs) -join ' ')] | " +
                 "every rung carries its OWN pre-rung band and its OWN restore claim; independence wait ${LadderRateClearSeconds}s + ${LadderSettleSeconds}s | " +
                 "${ViewportWidth}x${ViewportHeight}, rate_interval=${LadderRateClearSeconds}s, ~$LadderHostLoadRequests host requests | " +
                 "findings=$(@($LadderFindings).Count) | stack clean: seamVars=$($restore.SeamVarsClean) replicas=$($restore.ReplicasRestored) images=$($restore.ImagesUnchanged)"
        if (-not [string]::IsNullOrWhiteSpace($LadderAbortReason)) { $human += " || ABORTED: $LadderAbortReason" }
        if ($RunInParts) { $human += " || RUN IN PARTS: $(@($PartRuns) -join '; ')" }

        $report = [ordered]@{
            ScenarioId   = $canonicalId
            Verdict      = $verdict
            Status       = 'Locked'
            Lever        = "$($scenario.lever)"
            TargetTier   = $LadderTier

            Rungs        = [object[]]@($LadderRungs)
            RungsDriven  = [string[]]@($RungsDriven)
            RungsCarriedForward = [string[]]@($RungsCarriedForward)
            RunInParts   = $RunInParts
            PartRuns     = [string[]]@($PartRuns)
            RungFilter   = [int[]]@($RungFilter)
            DeclaredCounterRungs = [int[]]@($LadderCounterRungs)
            DeclaredGaugeRungs   = [int[]]@($LadderGaugeRungs)
            GaugeFallbackRung    = $LadderGaugeFallbackRung
            GaugeFallbackDriven  = $GaugeFallbackDriven

            MinDetectableCounterSeconds = $MinDetectableCounterSeconds
            MinDetectableCounterNote    = $MinDetectableCounterNote
            MinDetectableGaugeSeconds   = $MinDetectableGaugeSeconds
            MinDetectableGaugeNote      = $MinDetectableGaugeNote
            RegimeBDetectionNote        = $RegimeBDetectionNote

            PredictedCounterSeconds = $PredictedCounterSeconds
            PredictedGaugeSeconds   = $PredictedGaugeSeconds
            PredictionAgreesWithMeasurement = $PredictionAgreesWithMeasurement
            PredictionAgreementDetail       = $PredictionAgreementDetail
            PredictionSource = '88-RESEARCH.md § Minimum Detectable Fault Duration — 120 s for counter-backed panels (the two-consecutive-samples rule raises a theoretical ~24 s floor to 120 s) and >= 120 s for gauge-backed panels (2 x the 60 s export cadence; anything shorter is a coin flip).'

            IndependenceNote = "Each rung re-captures its OWN pre-rung baseline over $LadderBaselineCount x ${SubWindowSeconds}s sub-windows, and no rung's baseline window may BEGIN until ${LadderRateClearSeconds}s (the derived rate window) plus ${LadderSettleSeconds}s have elapsed since the previous rung's fault ended. Without that, a rung would be measured on the tail of its predecessor and would report the predecessor's fault as its own (T-88-24)."
            ScoringNote = "A rung that did NOT move is a MEASUREMENT, not a discrimination failure — finding where detection stops is the whole point of a ladder — so the scenario engine's 'a panel that was read and did not move is a Fail' rule deliberately does not apply here. Fail is reserved for a stack that did not restore or a cross-talk control that drifted."

            CrossTalkPanels        = [object[]]@($LadderCrossTalk)
            CrossTalkHeld          = $CrossTalkHeld
            CrossTalkMeasuredOnRungs = [string[]]@($CrossTalkMeasuredOnRungs)
            CrossTalkNote = "DISC-03. The controls are measured on the LONGEST rung of each regime, in the SAME batch and the SAME windows as the claim: the blast radius of a fault is monotonic in its duration, so a control that held under the longest outage held under every shorter one. Panels [$(@($LadderCounterControls) -join ',')] control the counter rungs. Only panel [$(@($LadderGaugeControls) -join ',')] controls the GAUGE rungs — panel 12 is deliberately excluded there, because the WebApi status-code mix is causally DOWNSTREAM of the very HTTP load a gauge rung drives (WEB-01 measured its 400 and 404 series moving off an exactly-zero band under precisely this load). A control the fault is expected to move is not a control; it would report the driver's own footprint as a blast radius."

            StackRestored    = [bool]$restore.Ok
            AllRungsRestored = $allRungsRestored
            ReplicasRestored = $restore.ReplicasRestored
            SeamVarsClean    = $restore.SeamVarsClean
            SeamVarsAfter    = @($restore.SeamVarsFound)
            ImagesUnchanged  = $restore.ImagesUnchanged
            ImagesBefore     = $preImages
            ReplicasBefore   = $preReplicas
            ReplicaMismatches = @($restore.ReplicaMismatches)
            ImageMismatches   = @($restore.ImageMismatches)
            UnknownTiers      = @($restore.UnknownTiers)
            ReplicasFieldNote = 'this row drives many faults rather than one, so ReplicasBefore carries the four-tier MAP read at the PRE gate; the scalar before/after pair for the tier a rung scaled lives on that rung''s own entry, together with its own ReplicasRestored claim.'

            SubWindowSeconds       = $SubWindowSeconds
            Panel4SubWindowSeconds = $Panel4SubWindowSeconds
            Panel4SubWindowNote    = 'MEASURED in 88-04: panel 4 renders "No data" at a 60 s sub-window, because increase() needs at least TWO samples inside its range and the stored resolution here is 60 s. Every panel-4 reading in this ladder is taken in its OWN 120 s batch and every band entry states its own width, so a 120 s reading can never be silently compared against a 60 s one.'
            ViewportWidth          = $ViewportWidth
            ViewportHeight         = $ViewportHeight
            LocatorMode            = $LocatorMode
            RateIntervalSeconds    = $LadderRateClearSeconds
            DatasourceTimeInterval = $LadderTimeInterval
            OldestSampleUtc        = $OldestSampleUtc
            HostLoadRequests       = $LadderHostLoadRequests

            AbortReason        = $LadderAbortReason
            ReaderUnavailable  = $ReaderUnavailable
            Findings           = [string[]]@($LadderFindings)
            ScreenshotPaths    = @($LadderScreenshotPaths)
            CompletedUtc       = ([DateTimeOffset]::UtcNow).ToString('o')
            HumanSummary       = $human
        }

        if (-not (Save-Phase88ScenarioArtifact -Report $report -Path $reportPath)) {
            Write-Phase "the written artifact carries a JSON VALUE of 'System.Object-array' — the serialisation depth is too shallow." 'Red'
            exit 66
        }
        Write-Phase "verdict artifact: $reportPath" 'Green'
        Write-Phase $human $(if ($verdict -eq 'Pass') { 'Green' } elseif ($verdict -eq 'Fail') { 'Red' } else { 'Yellow' })
        foreach ($f in $LadderFindings) { Write-Phase "  FINDING: $f" 'Yellow' }

        $exitCode = Resolve-AnalyzerExitCode ([pscustomobject]$report)
        if ($ReaderUnavailable -and $exitCode -eq 0) { $exitCode = 66 }
        if (-not $restore.Ok -or -not $allRungsRestored) { $exitCode = 65 }
        $resolved = Resolve-SweepClass $exitCode
        Write-Phase "class=$($resolved.Class) exit=$exitCode" 'Gray'
        exit $exitCode
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
        # The VALUES the seams were armed with, verbatim. PROCESSOR_DEFEAT_READ is a step LABEL, so
        # "the seam was armed" is not a statement anyone can check without knowing what it was armed TO.
        $SeamValuesArmed            = @()
        # The out-of-band Redis slot that turns the processor seam from a capability into an event.
        $ReinjectArmPasses          = 0
        $ReinjectArmClaimObserved   = $null
        $ReinjectArmCleared         = $null
        $ReinjectArmStillSet        = $null
        $ReinjectArmKey             = 'skp:test:defeat-read-arm'
        # SeamActiveDuringRebaseline's resolution, PROVEN per run rather than argued once.
        $RebaselineInertnessProven  = $null
        $RebaselineInertnessChecks  = @()
        # Row-declared decomposition of the subject panel's own query terms (diagnosis only).
        $DecompositionResults       = @()
        # The seam drive's own HTTP footprint, issued on the SAME cadence in the re-baseline window and
        # the after window so the WebApi cross-talk controls compare like with like (ZERO-02 run 1
        # measured the driver's single activation POST as cross-talk drift on panels 10 and 12).
        $seamActivationEverySeconds = 120
        $seamDriveCycleSeconds      = 120
        $ActivationPostsRebaseline  = 0
        $ActivationPostsAfter       = 0
        $DriveRolledPods            = @()

        $shotDir  = Join-Path $screenshotRoot $canonicalId
        $lever    = "$($scenario.lever)"
        $dwell    = [int]$scenario.dwellSeconds
        $subjects = @($scenario.panelIds       | ForEach-Object { "$_" })
        $controls = @($scenario.crossTalkPanels | ForEach-Object { "$_" })

        # =====================================================================================
        # SCENARIO HELPERS — MOVED to the shared HELPERS section above (plan 88-08).
        #
        # They were defined here, INSIDE `if ($rowMode -eq 'scenario')`, which meant that the
        # duration-ladder mode — dispatched earlier and never entering this block — could not call
        # a single one of them. PowerShell only sees a function once its definition STATEMENT has
        # executed, so a helper defined in a branch that did not run does not exist. Both modes now
        # capture, band, score and serialise through ONE definition, which is the same fix 88-05
        # deviation 6 applied to Get-ProxyAggregateMean and for the same reason: two copies of a
        # capture path would be two things to keep in step, and the ladder's whole claim is that its
        # rungs are measured the way every other scenario in this phase was measured.
        # =====================================================================================
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

            # The declared cross-talk panels are READ, in the SAME batch and the SAME windows as the
            # subject, and scored against a LOCAL reference band captured over the ten windows that
            # immediately precede the observation.
            #
            # Why a local reference rather than the DISC-01 band: BASE-01 was captured while a read-only
            # host load was driving the WebApi, and nothing is driving it here. Scoring panels 10 and 12
            # against a band taken under load, from an observation taken without it, would report drift
            # that is a difference in CONDITIONS rather than anything about the stack. Both windows here
            # have already ELAPSED, so the reference costs no wall time and is taken under conditions
            # identical to the observation by construction — the same trick STEP S4 uses for the pre-arm
            # baseline.
            #
            # These entries stay Applicable=false. Nothing was perturbed, so this is NOT a blast-radius
            # bound; it is a measured statement that the other guarded zeros and the WebApi panels were
            # simultaneously at rest while the subject sat at its guarded zero. Recording StayedPut as a
            # MEASUREMENT is not the fabricated claim 88-05 refused — that would be asserting a control
            # held when nothing was driven, which is what Applicable=false says.
            $dropCapturePanels = [string[]]@(@($subjects) + @($controls) | Select-Object -Unique)
            $obsEnd = ([datetime]::UtcNow).AddSeconds(-$ExportTrailSeconds)
            $refEnd = $obsEnd.AddSeconds(-($SubWindowSeconds * $AfterSubWindowCount))
            $refCap = Invoke-Phase88Capture -PanelIdList $dropCapturePanels -EndUtc $refEnd `
                        -Count $RebaselineSubWindowCount -Panel4Count $Panel4RebaselineCount -ShotDir $shotDir -Label 'observe-reference'
            $dropRefBands = @(New-Phase88BandSet -Capture $refCap -PanelIdList $dropCapturePanels)
            $obsCap = Invoke-Phase88Capture -PanelIdList $dropCapturePanels -EndUtc $obsEnd `
                        -Count $AfterSubWindowCount -Panel4Count $Panel4AfterCount -ShotDir $shotDir -Label 'observe'
            $ScenarioScreenshotPaths = @(@($refCap.ScreenshotPaths) + @($obsCap.ScreenshotPaths) | Select-Object -Unique)

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

            # The declared cross-talk list is carried with Applicable=false — no fault was driven, so
            # there is no blast radius to bound — but each panel is READ and scored against the local
            # reference band, so the row states what those panels were doing while the subject sat at
            # its guarded zero instead of leaving the declaration unmeasured.
            foreach ($c in $controls) {
                $rdC   = Get-Phase88PanelReading -Capture $obsCap -PanelId $c
                $stateC = if (@($rdC.States).Count -gt 0) { (@($rdC.States | Select-Object -Unique) -join '|') } else { 'NoReading' }
                $ctSeriesD = @()
                $panelStayedD = $true
                $maxExcD = 0.0
                $worstD = $null
                foreach ($e in @($rdC.Entries)) {
                    $bandC = Find-Phase88Band -Bands $dropRefBands -PanelId ([int]$c) -SeriesName "$($e.Name)" -SeriesIndex ([int]$e.Index)
                    if ($null -eq $bandC) {
                        $panelStayedD = $false
                        $Findings += "observed panel ${c} ($($Panels[$c].Title)) series '$($e.Name)': no reference band, so its inertness could not be stated"
                        $ctSeriesD += [pscustomobject]@{
                            SeriesIndex = [int]$e.Index; SeriesName = "$($e.Name)"
                            BandLow = $null; BandHigh = $null; AfterValues = [double[]]@($e.Values)
                            StayedPut = $false; MaxExcursion = $null; SamplesOutside = 0
                            Note = 'no reference band for this series'
                        }
                        continue
                    }
                    $loC = [double]$bandC.BandLow; $hiC = [double]$bandC.BandHigh
                    $excC = 0.0; $outC = 0
                    foreach ($v in @($e.Values)) {
                        $x = Get-Phase88Excursion -Value ([double]$v) -Low $loC -High $hiC
                        if ($x -gt 0) { $outC++ }
                        if ($x -gt $excC) { $excC = $x }
                    }
                    if ($outC -ne 0) { $panelStayedD = $false }
                    if ($excC -gt $maxExcD) { $maxExcD = $excC }
                    $entryD = [pscustomobject]@{
                        SeriesIndex    = [int]$e.Index
                        SeriesName     = "$($e.Name)"
                        BandLow        = $loC
                        BandHigh       = $hiC
                        BandSampleCount = [int]$bandC.SampleCount
                        AfterValues    = [double[]]@($e.Values)
                        StayedPut      = ($outC -eq 0)
                        MaxExcursion   = $excC
                        SamplesOutside = $outC
                        Note           = ''
                    }
                    $ctSeriesD += $entryD
                    if ($null -eq $worstD -or $excC -ge [double]$worstD.MaxExcursion) { $worstD = $entryD }
                }
                $CrossTalkResults += [pscustomobject]@{
                    PanelId          = [int]$c
                    PanelTitle       = "$($Panels[$c].Title)"
                    Applicable       = $false
                    BandLow          = if ($null -ne $worstD) { $worstD.BandLow } else { $null }
                    BandHigh         = if ($null -ne $worstD) { $worstD.BandHigh } else { $null }
                    BandSource       = "local reference, 10 x ${SubWindowSeconds}s immediately preceding the observation ($($refCap.WindowStartUtc) -> $($refCap.WindowEndUtc))"
                    SubWindowSeconds = [int]$rdC.SubWindowSeconds
                    AfterValues      = if ($null -ne $worstD) { [double[]]@($worstD.AfterValues) } else { [double[]]@() }
                    StayedPut        = $panelStayedD
                    MaxExcursion     = $maxExcD
                    PanelStateAfter  = $stateC
                    SeriesResults    = @($ctSeriesD)
                    Note             = 'MEASURED INERTNESS, NOT A BLAST-RADIUS BOUND. No fault was driven, so Applicable is false: this cannot say the fault did not reach this panel, because there was no fault. What it does say — as a reading, not an assertion — is that this panel stayed inside a band taken from the ten windows immediately before, while the subject panel sat at its guarded zero. For the other guarded zeros in this list that is the useful half: it records that they were simultaneously at exactly 0.'
                }
                Write-Phase ("  observed control panel {0}: stayedPut={1} maxExcursion={2:N4} state={3}" -f $c, $panelStayedD, $maxExcD, $stateC) 'Gray'
            }

            $restore = Assert-StackRestored -Tiers $Tiers -ExpectedReplicas $preReplicas -ExpectedImages $preImages
            foreach ($t in $Tiers) { $ReplicasAfterMap[$t] = Get-LiveReplicas -Tier $t }

            $endpointLines = @()
            if (@(Get-PropertyNames $probe) -contains 'ProbedEndpoints') {
                foreach ($pe in @($probe.ProbedEndpoints)) {
                    $endpointLines += ("{0} {1} -> {2}" -f "$($pe.Method)", "$($pe.Path)", "$($pe.Status)")
                }
            }
            # A row may carry its OWN enumerated reason. Without this branch every dropped row would be
            # narrated as WEB-02 — the 5xx endpoint sweep and the redis/readiness-latch cost — under a
            # different scenario id, and a reason that names the wrong routes reads as evidence.
            if ($scenario.Contains('acceptedUnprovenReason') -and -not [string]::IsNullOrWhiteSpace("$($scenario.acceptedUnprovenReason)")) {
                $AcceptedUnprovenReason = "$($scenario.statusReason). " + "$($scenario.acceptedUnprovenReason)"
            }
            else {
            $AcceptedUnprovenReason =
                "$($scenario.statusReason). " +
                "Enumerated endpoint probe (PQ-03, $($endpointLines.Count) endpoints, zero 5xx on safe input): " +
                ($endpointLines -join ' | ') + ". " +
                'The dependency-outage route to a 5xx is NOT substituted: redis has no PVC, so scaling it wipes L2, and under the Phase-86 hard readiness latch the WebApi does not self-heal from a dependency outage — recovery costs a WebApi pod restart, which would perturb every later baseline in this phase. ' +
                'MAINTENANCE GUIDANCE: panel 13''s guarded green 0 is indistinguishable from "the 5xx counter has never been emitted at all". The first non-zero reading on panel 13 should be corroborated against the WebApi logs for the same window before it is trusted, and once ONE 5xx has ever occurred the numerator series exists permanently — so a zero on panel 13 after that date means something different from today''s zero.'
            }

            $humanDrop = "phase-88 $canonicalId verdict=Inconclusive: DROPPED row RECORDED, not run. " +
                         "No fault was driven and no cluster mutation was issued. " +
                         "Drop re-confirmed from the wave-0 probe ($dropField='$dropActual'). " +
                         "Panel $(@($subjects) -join ',') observed over $AfterSubWindowCount x ${SubWindowSeconds}s pinned windows " +
                         "($($obsCap.WindowStartUtc) -> $($obsCap.WindowEndUtc)) at ${ViewportWidth}x${ViewportHeight}; " +
                         "state=$(@($PanelResults | ForEach-Object { $_.PanelStateAfter }) -join ','), " +
                         "values=[$(@($PanelResults | ForEach-Object { "$($_.PanelId):$((@($_.AfterValues) -join ','))" }) -join ' ')]. " +
                         "Declared cross-talk panels [$(@($controls) -join ',')] were READ in the same windows and scored against a local reference band " +
                         "($(@($CrossTalkResults | ForEach-Object { "$($_.PanelId):$(if($_.StayedPut){'inert'}else{'MOVED'})" }) -join ' ')) — measured inertness, NOT a blast-radius bound, because no fault was driven. " +
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
                ObserveReferenceWindowStart = "$($refCap.WindowStartUtc)"
                ObserveReferenceWindowEnd   = "$($refCap.WindowEndUtc)"
                ObserveReferenceNote      = "the cross-talk panels are scored against a LOCAL reference band taken over the $RebaselineSubWindowCount x ${SubWindowSeconds}s that immediately precede the observation, not against the DISC-01 band — BASE-01 was captured under a read-only host load and nothing is driving one here, so a DISC-01 comparison would report a difference in CONDITIONS as drift. Both windows had already elapsed when they were read, so the reference cost no wall time and is taken under conditions identical to the observation."
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
        # The fan-out workflow id is resolved HERE rather than at trigger time, because the seam branch
        # now issues its activation POST during the re-baseline hold as well (so the driver's own HTTP
        # footprint is inside the band the WebApi controls are scored against, not only inside the
        # after window). A `seam` row that cannot resolve it has nothing to exercise the seam with.
        if ($lever -eq 'seam') {
            $wfRaw = ''
            try { $wfRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'" } catch { }
            foreach ($line in @(("$wfRaw") -split "`r?`n")) {
                $tw = ("$line").Trim()
                if ($tw -match '^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$') { $TrafficWorkflowId = $tw; break }
            }
            if ([string]::IsNullOrWhiteSpace($TrafficWorkflowId)) {
                Write-Phase "could not resolve the fan-out workflow id — the seam has nothing to be exercised by. Aborting." 'Red'; exit 50
            }
            Write-Phase "  fan-out workflow $TrafficWorkflowId resolved (the seam's workload)" 'Gray'
        }

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
                # The VALUE comes from the row, as a static table string. PROCESSOR_DEFEAT_READ is a step
                # LABEL, not a boolean, so the library's '1' default would arm a variable the pipeline can
                # never match and the scenario would drive nothing while looking armed.
                $seamValue = if ($scenario.Contains('seamValue') -and -not [string]::IsNullOrWhiteSpace("$($scenario.seamValue)")) { "$($scenario.seamValue)" } else { '1' }
                $SeamValuesArmed += "${seamTier}/${seamVar}=${seamValue}"
                $RolloutOldInstanceIds = [string[]]@(Get-TierPodNames -Tier $seamTier)
                Write-Phase "STEP S5: arming '$seamVar=$seamValue' on '$seamTier'"
                $arm = Set-Phase88Seam -Tier $seamTier -Name $seamVar -Value $seamValue
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
                    $seamValue2 = if ($scenario.Contains('triggerSeamValue') -and -not [string]::IsNullOrWhiteSpace("$($scenario.triggerSeamValue)")) { "$($scenario.triggerSeamValue)" } else { '1' }
                    $SeamValuesArmed += "${seamTier2}/${seamVar2}=${seamValue2}"
                    Write-Phase "STEP S5: arming the TRIGGER seam '$seamVar2=$seamValue2' on '$seamTier2'"
                    $arm2 = Set-Phase88Seam -Tier $seamTier2 -Name $seamVar2 -Value $seamValue2
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
            # THE TRIGGER'S OWN HTTP FOOTPRINT MUST BE IN THE BAND, OR THE WEBAPI CONTROLS ARE NOT
            # CONTROLS. Measured in ZERO-02 run 1: the seam trigger activates the workflow with
            # POST /api/v1/orchestration/start, and that single request took panel 10's
            # `.../Orchestration/start` series and panel 12's `204` series off their exactly-0..0
            # re-baseline bands — max excursion 0.0056 on four sub-windows. Both were recorded as
            # cross-talk DRIFT, which is a false statement about the fault's blast radius: the
            # perturbation is the DRIVER's own HTTP call, present in the after window and absent from
            # the band. The activation is issued here on the SAME cadence the drive loop uses, so both
            # windows carry the same footprint by construction and the control measures the FAULT.
            if ($seamActivationEverySeconds -gt 0) {
                $rbDeadline = $rebaseStart.AddSeconds($rebaseHold)
                while ([datetime]::UtcNow -lt $rbDeadline) {
                    $null = Invoke-SeamActivation
                    $ActivationPostsRebaseline++
                    $sleepFor = [Math]::Min($seamActivationEverySeconds, [int][Math]::Max(1, ($rbDeadline - [datetime]::UtcNow).TotalSeconds))
                    Start-Sleep -Seconds $sleepFor
                }
                Write-Phase "  $ActivationPostsRebaseline activation POST(s) issued across the re-baseline window, matching the drive cadence" 'Gray'
            }
            else { Start-Sleep -Seconds $rebaseHold }
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

                # ---- SeamActiveDuringRebaseline: RESOLVED BY MEASUREMENT, NOT BY ARGUMENT -----------
                # 88-05 left the question open: a seam armed BEFORE the re-baseline is still armed DURING
                # it, so is the authoritative band captured with the fault already running?
                #
                # It is not, and the reason is structural rather than lucky. BOTH Phase-88 seams are
                # CAPABILITIES that a separate act must trigger:
                #   * PROCESSOR_DEFEAT_READ short-circuits unless the hop's payload carries the label AND
                #     the Redis slot skp:test:defeat-read-arm exists to be claimed. The slot is armed at
                #     TRIGGER time only (STEP S6), so during the settle and the re-baseline the block makes
                #     no Redis call, claims nothing and faults nothing.
                #   * KEEPER_DEFEAT_REINJECT can only fire inside ReinjectConsumer.HandleAsync, which runs
                #     only when a KeeperReinject arrives — which cannot happen until the processor seam
                #     above has fired.
                # The re-baseline is therefore exactly what DISC-06 needs it to be: a band taken AFTER the
                # rollout the arm caused (which is the real discontinuity) and BEFORE the fault.
                #
                # That reasoning is still an argument, so the run PROVES it: every Regime-B subject panel's
                # re-baseline band must still be exactly 0..0. A contaminated re-baseline would show a
                # non-zero guarded band here, and the scenario would then be comparing fault against fault.
                # Proven, not assumed — a false claim about the null hypothesis voids the whole assertion.
                $inertChecks = @()
                $inertOk = $true
                foreach ($sp in @($subjects)) {
                    if ("$($Panels[$sp].Regime)" -ne 'B') { continue }
                    $spBands = @($rebaseBands | Where-Object { [int]$_.PanelId -eq [int]$sp })
                    if (@($spBands).Count -eq 0) {
                        $inertOk = $false
                        $inertChecks += "panel ${sp}: NO re-baseline band was produced, so the seam-active re-baseline could not be shown inert"
                        continue
                    }
                    foreach ($sb in @($spBands)) {
                        $lo = [double]$sb.BandLow; $hi = [double]$sb.BandHigh
                        if ($lo -ne 0.0 -or $hi -ne 0.0) {
                            $inertOk = $false
                            $inertChecks += ("panel {0} series '{1}': re-baseline band {2:N6}..{3:N6} is NOT the guarded zero — the armed seam was ALREADY firing during the re-baseline, so this band is not a null hypothesis" -f $sp, "$($sb.SeriesName)", $lo, $hi)
                        } else {
                            $inertChecks += ("panel {0} series '{1}': re-baseline band 0..0 with the seam ARMED — the capability was inert until the trigger" -f $sp, "$($sb.SeriesName)")
                        }
                    }
                }
                if (@($inertChecks).Count -gt 0) {
                    $RebaselineInertnessProven = $inertOk
                    $RebaselineInertnessChecks = [string[]]@($inertChecks)
                    if (-not $inertOk) {
                        $Findings += "the seam-active re-baseline is CONTAMINATED: $($inertChecks -join ' | ')"
                        Write-Phase "  re-baseline inertness NOT proven — see findings" 'Red'
                    } else {
                        Write-Phase "  re-baseline inertness PROVEN: every Regime-B subject band is still exactly 0..0 with the seam armed" 'Gray'
                    }
                }
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
            $TriggerKind = "workload drive with the seam armed, in bounded arm/claim/roll cycles"
            Write-Phase "STEP S6: TRIGGER — $TriggerKind"
            if (-not (Invoke-SeamActivation)) {
                Write-Phase "activation gate failed at the trigger. Aborting." 'Red'; exit 50
            }
            $ActivationPostsAfter++
            $faultStart = [datetime]::UtcNow
            $FaultStartUtc = $faultStart.ToString('o')
            $afterStart = $faultStart
            $afterEnd   = $afterStart.AddSeconds($afterSpan)

            # ---- THE ACTUAL FAULT: ARM THE OUT-OF-BAND REDIS SLOT, REPEATEDLY, ACROSS EXPORT TICKS
            # `kubectl set env PROCESSOR_DEFEAT_READ=Step_C` only gives the pipeline a LABEL to match.
            # The fault fires when a matching hop can CLAIM skp:test:defeat-read-arm — a KeyDelete that
            # returns true for exactly ONE caller across replicas. That is where the seam stops being a
            # capability and becomes an event, and it is why the re-baseline above is a valid null
            # hypothesis rather than a second fault window.
            #
            # WHY THE DRIVE IS A CYCLE AND NOT A SINGLE BURST — MEASURED IN ZERO-02 RUN 1.
            # Run 1 armed the slot four times over 90 s, both processor replicas claimed a victim, three
            # KeeperReinjects flowed and the keeper consumed and sent all three. Panel 2 nevertheless
            # rendered an unbroken 0, and so did the diagnostic proxy. The raw samples say why: the
            # counter series were born carrying their FINAL value on their first exported sample
            # (`distinct values ['2']` and `['1']` across every scrape) because the whole burst completed
            # inside one 60 s export interval. `rate()` differences consecutive samples, so a counter that
            # appears at 2 and stays at 2 has a rate of exactly zero forever — the event is invisible to
            # the panel that exists to show it. (`increase()` DOES see it, because a new series' first
            # sample counts as a rise from zero; that asymmetry is why panel 2 and panel 8 behave
            # differently under the same event, and it is HAND-02 material.)
            #
            # A rate can therefore only be rendered by events in at least TWO different export intervals.
            # Each processor pod assigns its `_reinjectTriggerTarget` static exactly once per process, so
            # re-arming alone mints no new victims once every replica has claimed — the tier must be
            # rolled. The drive is therefore: activate, arm, observe the claim, roll ONE replica (never
            # the whole tier, so the pipeline is never out), and repeat. Strictly bounded by cycle count
            # and by one victim per replica per generation: this can never become a hot loop.
            $armPasses = 0
            $anyClaim  = $false
            if ($scenario.Contains('requiresReinjectArm') -and $scenario.requiresReinjectArm) {
                $cycleCount   = [Math]::Max(2, [int][Math]::Floor($afterSpan / [double]$seamDriveCycleSeconds))
                $rollTier     = 'processor-sample'
                Write-Phase "  drive: $cycleCount cycle(s) of ${seamDriveCycleSeconds}s — activate, arm, claim, roll one '$rollTier' replica" 'Gray'
                for ($cy = 0; $cy -lt $cycleCount; $cy++) {
                    $cycleDeadline = [datetime]::UtcNow.AddSeconds($seamDriveCycleSeconds)
                    if ($cy -gt 0) { if (Invoke-SeamActivation) { $ActivationPostsAfter++ } }

                    # Two arm passes 25 s apart: the first lets one replica claim, the second lets the
                    # other. The slot is consumed by the claim, so it must be re-set to offer a second.
                    for ($ap = 0; $ap -lt 2; $ap++) {
                        $ar = Set-Phase88ReinjectArm
                        if ($ar.Ok) { $armPasses++; $reinjectArmArmed = $true }
                        else {
                            $Findings += "the reinject slot could not be armed (cycle $($cy + 1), pass $($ap + 1)): $($ar.Detail)"
                            Write-Phase "  reinject arm FAILED: $($ar.Detail)" 'Yellow'
                        }
                        Start-Sleep -Seconds 25
                        # CHECKED AFTER EVERY PASS, not once at the end. Run 1 checked only after the last
                        # pass and reported claimed=false for a run in which BOTH replicas had demonstrably
                        # claimed — the later passes simply had no unclaimed replica left to take them.
                        $stillArmed = Test-Phase88ReinjectArmed
                        if ($null -ne $stillArmed -and -not $stillArmed) { $anyClaim = $true }
                    }
                    Write-Phase ("  cycle {0}/{1}: {2} arm pass(es) so far, anyClaim={3}" -f ($cy + 1), $cycleCount, $armPasses, $anyClaim) 'Gray'

                    # Roll ONE replica so the next cycle has an unclaimed pod. Never on the last cycle —
                    # a rollout after the final events would only add a discontinuity with nothing to gain.
                    if ($cy -lt ($cycleCount - 1)) {
                        $podNames = @(Get-TierPodNames -Tier $rollTier)
                        if (@($podNames).Count -gt 0) {
                            $victimPod = "$($podNames[0])"
                            Write-Phase "  rolling one '$rollTier' replica ($victimPod) to mint a fresh claim slot" 'Gray'
                            $del = Invoke-Phase88Ctl -Arguments @('delete', 'pod', $victimPod, '--wait=false')
                            if (-not $del.Ok) { $Findings += "could not roll '$victimPod': $($del.Error)" }
                            $null = Wait-TierSettled -Tier $rollTier -TimeoutSeconds 180
                            $DriveRolledPods += $victimPod
                        }
                    }
                    $left = [int]($cycleDeadline - [datetime]::UtcNow).TotalSeconds
                    if ($left -gt 0) { Start-Sleep -Seconds $left }
                }
                $ReinjectArmPasses = $armPasses
                $ReinjectArmClaimObserved = $anyClaim
                Write-Phase "  reinject slot: $armPasses arm pass(es) over $cycleCount cycle(s); a claim was observed at least once = $anyClaim" $(if ($anyClaim) { 'Green' } else { 'Yellow' })
                $left = [int]($afterEnd - [datetime]::UtcNow).TotalSeconds
                if ($left -gt 0) { Start-Sleep -Seconds $left }
                Start-Sleep -Seconds $ExportTrailSeconds
            }
            else {
                Start-Sleep -Seconds ($afterSpan + $ExportTrailSeconds)
            }

            # Clear the slot as soon as the fault window closes, not only in the outer finally. An
            # unclaimed slot left behind would silently pre-arm the NEXT scenario that sets the label.
            $rc = Clear-Phase88ReinjectArm
            $reinjectArmArmed = $false
            $ReinjectArmCleared = [bool]$rc.Ok
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
        # The Redis arm slot is cleared and RE-READ here, beside the env-var restore claim, because
        # Assert-StackRestored only knows about Deployment env and would report a clean stack while the
        # slot still sat armed in Redis.
        $rc2 = Clear-Phase88ReinjectArm
        $reinjectArmArmed = $false
        if ($null -eq $ReinjectArmCleared) { $ReinjectArmCleared = [bool]$rc2.Ok }
        $ReinjectArmStillSet = Test-Phase88ReinjectArmed
        if ($ReinjectArmStillSet) {
            $Findings += "the reinject arm slot '$ReinjectArmKey' is STILL SET after the run — it would pre-arm the next scenario that sets PROCESSOR_DEFEAT_READ"
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
                LegendNamesBaseline       = if ($null -ne $rdBase) { [string[]]@(Get-Phase88LegendNames -Reading $rdBase -Capture $preArmCap -PanelId $p) } else { [string[]]@() }
                LegendNamesAfter          = [string[]]@(Get-Phase88LegendNames -Reading $rdAfter -Capture $afterCap -PanelId $p)
                LegendNamesBaselinePerWindow = @(Get-Phase88LegendNamesPerWindow -Capture $preArmCap -PanelId $p)
                LegendNamesAfterPerWindow    = @(Get-Phase88LegendNamesPerWindow -Capture $afterCap -PanelId $p)
                LegendNamesNote           = 'LegendNames* are the UNION of every row the panel RENDERED in any sub-window, collected from the readings themselves rather than by row index. A legend that GAINS rows mid-capture — the guarded-panel transition itself — shifts every later index, and an index-keyed collector then reports a stale name for exactly the row that matters. The *PerWindow arrays carry the rows window by window so the transition is auditable rather than merged.'
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
        # STEP S10b — ROW-DECLARED DECOMPOSITION QUERIES (diagnosis only, never a verdict).
        #
        # A guarded panel that renders 0 does not say WHY. `sum(increase(A)) - sum(increase(B))
        # or vector(0)` renders exactly the same comfortable 0 when (i) A and B moved together,
        # (ii) neither moved, (iii) A moved but B has no series at all so the subtraction is EMPTY
        # and the guard fires, or (iv) the window is too narrow for increase() to have two samples.
        # Those are four different statements about the stack and a reader cannot tell them apart
        # from the rendered value.
        #
        # A row may therefore declare the query terms of its own panel, evaluated over the SAME
        # after window at SEVERAL range widths, with the per-timestamp values kept. This is
        # DIAGNOSIS: the rendered panel remains the verdict (T-88-12), and nothing here can make a
        # panel Moved. It exists so a flat guarded zero is a decomposed finding rather than a shrug.
        # =====================================================================================
        if ($scenario.Contains('decompositionQueries') -and @($scenario.decompositionQueries).Count -gt 0) {
            Write-Phase "STEP S10b: decomposition of the subject panel's own query terms (diagnosis only)"
            foreach ($dq in @($scenario.decompositionQueries)) {
                foreach ($w in @($scenario.decompositionWindows)) {
                    $qText = ("$($dq.Query)") -replace '\$__w', "${w}s"
                    $vals  = [double[]]@()
                    $err   = ''
                    try { $vals = Get-ProxySeriesValues $qText $afterStartUnix $afterEndUnix $SubWindowSeconds }
                    catch { $err = "$($_.Exception.Message)" }
                    $DecompositionResults += [pscustomobject]@{
                        Label            = "$($dq.Label)"
                        RangeSeconds     = [int]$w
                        Query            = $qText
                        StepSeconds      = $SubWindowSeconds
                        PointCount       = @($vals).Count
                        Values           = $vals
                        AllZero          = ((@($vals).Count -gt 0) -and (@($vals | Where-Object { $_ -ne 0 }).Count -eq 0))
                        Empty            = (@($vals).Count -eq 0)
                        Error            = $err
                    }
                    Write-Phase ("  {0} [{1}s]: {2} point(s) [{3}]{4}" -f "$($dq.Label)", $w, @($vals).Count, ((@($vals) | ForEach-Object { "{0:N4}" -f $_ }) -join ','), $(if ($err) { " ERROR: $err" } else { '' })) 'Gray'
                }
            }
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
            SeamActiveDuringRebaselineResolution = 'RESOLVED IN 88-07 — a seam-active re-baseline IS a valid comparison basis on this stack, and the run proves it rather than arguing it. Both Phase-88 seams are CAPABILITIES that a separate act must trigger: PROCESSOR_DEFEAT_READ short-circuits (no Redis call, no claim, no fault) until the out-of-band slot skp:test:defeat-read-arm exists to be claimed, and that slot is armed at TRIGGER time only; KEEPER_DEFEAT_REINJECT can only fire inside ReinjectConsumer.HandleAsync, which cannot run until the processor seam has already fired. The re-baseline is therefore taken AFTER the rollout the arm caused — which is the real DISC-06 discontinuity — and BEFORE the fault. RebaselineInertnessProven records the per-run PROOF: every Regime-B subject panel''s re-baseline band must still be exactly 0..0 with the seam armed. A contaminated re-baseline would show a non-zero guarded band and the scenario would be comparing fault against fault. The pre-arm capture is NOT used as the scoring band, because it predates the rollout and its series would be compared across a pod-identity change.'
            RebaselineInertnessProven = $RebaselineInertnessProven
            RebaselineInertnessChecks = [string[]]@($RebaselineInertnessChecks)
            SeamValuesArmed           = [string[]]@($SeamValuesArmed)
            ReinjectArmKey            = $ReinjectArmKey
            ReinjectArmPasses         = $ReinjectArmPasses
            ReinjectArmClaimObserved  = $ReinjectArmClaimObserved
            ReinjectArmCleared        = $ReinjectArmCleared
            ReinjectArmStillSet       = $ReinjectArmStillSet
            ActivationPostsRebaseline = $ActivationPostsRebaseline
            ActivationPostsAfter      = $ActivationPostsAfter
            ActivationCadenceNote     = "the seam drive's own POST /api/v1/orchestration/start is issued on the SAME ${seamActivationEverySeconds}s cadence during the re-baseline hold and during the after window, so panels 10 and 12 are scored against a band that already contains the driver's HTTP footprint. ZERO-02 run 1 did not do this and correctly recorded the driver's single activation as cross-talk DRIFT (max excursion 0.0056 on panel 10's `.../Orchestration/start` series and panel 12's `204` series) — a false statement about the fault's blast radius, because the perturbation was the driver's own request."
            DriveRolledPods           = [string[]]@($DriveRolledPods)
            DriveRollNote             = 'Each processor replica assigns its _reinjectTriggerTarget static exactly once per PROCESS, so once every replica has claimed a victim, re-arming the Redis slot mints no further faults however many times it is set. ONE replica is therefore rolled between drive cycles — never the whole tier, so the pipeline is never out — which is what spreads the keeper events across more than one 60 s export interval. That spread is not a nicety: a burst confined to a single export interval is INVISIBLE to rate() (see ReinjectArmNote and the run-1 discard).'
            ReinjectArmNote           = 'MEASURED IN 88-07 and load-bearing for every seam row: `kubectl set env PROCESSOR_DEFEAT_READ=<label>` is NOT the fault. src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:105-128 requires BOTH the hop payload to contain the label AND the Redis slot named above to exist, so the hop''s KeyDelete can atomically CLAIM it (true for exactly ONE caller across replicas). ReinjectArmClaimObserved true means the slot was consumed by a hop — the fault fired. A run that armed the env var and never armed the slot would have driven NOTHING while looking fully armed, and would then have scored the resulting flat panel as evidence about the deployed image.'
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

            DecompositionResults      = @($DecompositionResults)
            DecompositionNote         = 'DIAGNOSIS ONLY — nothing here can make a panel Moved. A guarded panel that renders 0 does not say WHY, and `sum(increase(A)) - sum(increase(B)) or vector(0)` renders the identical comfortable 0 in four different situations: A and B moved together; neither moved; A moved but B has no series at all so the subtraction is EMPTY and the guard supplies the 0; or the sub-window is too narrow for increase() to have two samples inside it. These rows evaluate the panel''s OWN terms over the SAME after window at several range widths, keeping the per-timestamp values, so a reader can tell those four apart. Empty=true means the expression produced no result at all at any step — which is precisely the case the guard hides.'

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

    # The out-of-band Redis ARM SLOT is the other half of the processor seam and needs the same
    # unconditional teardown. On its own an armed slot faults nothing — the env var must also be
    # present — but a slot left SET would silently pre-arm the NEXT scenario that sets the label, and
    # that scenario's very first matching hop would then be faulted before its own trigger. Clearing is
    # a DEL, idempotent on an absent key, so it is issued whenever this run armed it at all.
    if ($reinjectArmArmed) {
        if (Get-Command Clear-Phase88ReinjectArm -ErrorAction SilentlyContinue) {
            Write-Host "[phase-88-panel-discriminate] TEARDOWN: the reinject arm slot may still be set — clearing." -ForegroundColor Yellow
            $null = Clear-Phase88ReinjectArm
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
