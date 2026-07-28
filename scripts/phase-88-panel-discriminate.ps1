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
    [switch]$SkipTraffic
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
$scaledTier      = ''
$replicasBefore  = -1
$probeWorkflowId = ''
$trafficJob      = $null

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
            mode = 'scenario'; lever = 'scale'; targetTier = 'keeper'; seamTier = ''; seamVar = ''
            triggerSeamTier = ''; triggerSeamVar = ''; dwellSeconds = 240
            panelIds = @('9')
            predictedDirection = @{ '9' = 'nodata' }
            crossTalkPanels = @('1','3','10','12')
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

    if ($scenario.status -ne 'Locked') {
        Write-Phase "scenario '$ScenarioId' is $($scenario.status) and will not be run." 'Red'
        Write-Phase "REASON: $($scenario.statusReason)" 'Yellow'
        Write-Phase "DECIDED BY: $($scenario.decidedBy)" 'Yellow'
        Write-Phase "It remains a ROW in the table on purpose — the roll-up must show it as a dropped row, not as an absence." 'Yellow'
        exit 64
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
    if ($rowMode -ne 'baseline') {
        Write-Phase "mode '$rowMode' is not implemented in this driver yet." 'Red'
        Write-Phase "Plan 88-04 authors the frame, the scenario table and -Mode Baseline (BASE-01)." 'Yellow'
        Write-Phase "  -Mode Scenario       -> plans 88-05 (WEB-01), 88-06 (SCALE-01..03), 88-07 (ZERO-02/03)" 'Yellow'
        Write-Phase "  -Mode DurationLadder -> plan 88-08 (LADDER-01)" 'Yellow'
        exit 64
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

    # NOTE ON THE PARAMETER NAMES — they are deliberately long, and a single-letter name here is a
    # BUG, not a style choice. PowerShell variable names are case-INSENSITIVE, so a `[long]$S`
    # parameter and a `foreach ($s in ...)` loop variable are the SAME variable; the parameter's type
    # constraint is then re-enforced on the loop assignment and every call throws
    # "Cannot convert @{metric=; values=System.Object[]} ... to System.Int64". That is exactly how the
    # first BASE-01 run lost its entire diagnostic cross-check. (Same trap as 88-03 deviation 5, in a
    # form where the type constraint makes it fail loudly instead of silently.)
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

    # Stop ONLY a forward this script actually started, behind the recycled-PID guard. A forward this
    # script found already running belongs to someone else and is left exactly as it was found.
    if ($gfForwardOwned -and $gfForwardPid -gt 0) {
        if (Get-Command Stop-GrafanaForward -ErrorAction SilentlyContinue) {
            Stop-GrafanaForward $gfForwardPid
        }
    }
    Pop-Location
}
