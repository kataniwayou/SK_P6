<#
.SYNOPSIS
    Phase 67 fault-injection harness (FAULT-01 / FAULT-02 / FAULT-03).

.DESCRIPTION
    Single self-contained PowerShell orchestrator — a sibling of the 18 phase-NN-close.ps1
    family — that shells the already-proven Phase 65 / Phase 66 artifacts plus docker fault
    ops into one fully-automated scenario run. Invoked once per scenario id:

        pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-01

    Flow (per the resolved D-01..D-16 decisions):

        STEP A0  docker compose build            rebuild images so the container SourceHash matches
                                                 the host-built Processor.Sample (liveness id currency)
        STEP A   compose up --force-recreate
                 + phase-65-up.ps1               bring the minimal stack up (10 svc types healthy)
        STEP B   phase-65-reset.ps1              FLUSHALL + heal-wait + FK-safe graph DELETE
        STEP B1  docker compose restart orchestrator + wait healthy
                                                 GUARANTEE A CLEAN ORCHESTRATOR (no ghost crons)
        STEP C   dotnet test ~FanOutSeeder       seed v8-fanout-proof (idempotent, self-verifying)
        STEP D   psql sentinel lookup            resolve the v8-fanout-proof workflow id
        STEP E   POST /orchestration/start       activation gate — REQUIRE HTTP 204
        STEP F   observe-loop + crash sequencer  poll fire-count to N, crash whole tier, recover, hold window
        STEP H   dotnet test ~Analyzer           verdict (D-16 env seam: SCENARIO_ID / WINDOW_*_UTC)
        STEP Z   docker compose down             teardown (keep volumes + images)

    Final harness exit code == the analyzer verdict (0 = PASS, non-0 = FAIL). Each infra step
    fails loud with a DISTINCT non-zero code so an infra abort is never mistaken for a verdict.

    EXIT-CODE TABLE (D-04):
        0   analyzer PASS (final verdict green)
        1   analyzer FAIL verdict (mirrors the dotnet test exit — the legitimate verdict path)
        10  bring-up (phase-65-up.ps1) failed
        20  reset (phase-65-reset.ps1) failed
        25  orchestrator clean-restart / health-wait failed (clean-window guarantee, STEP B1)
        30  seeder (dotnet test ~FanOutSeeder) failed
        40  wf-id psql lookup failed/empty
        50  activation gate != 204
        60  fault inject / recover / baseline-firing failed
        70  teardown (docker compose down) failed (NON-FATAL — logs loud, still surfaces the verdict)
        64  bad -ScenarioId argument (unknown scenario key — config-usage error, never collides)

    IMPORTANT (clean-window guarantee, STEP B1): phase-65-reset.ps1 FLUSHALLs Redis and deletes
    the workflow-graph rows, but the long-running orchestrator keeps its already-registered Quartz
    crons in its in-process RAMJobStore — so without this step it keeps firing dozens of GHOST
    workflow crons (NULL payloads, no Step_* labels) that pollute the observation window and make
    the analyzer score everything MISSING (verified live in plan 67-01). Restarting the orchestrator
    AFTER the reset (empty L2 parent index) and BEFORE seed+start drops every stale Quartz job; on
    boot HydrationBackgroundService finds an empty index and schedules nothing, so ONLY the freshly
    seeded+started v8-fanout-proof workflow fires in the window. The same dirty state also caused the
    POST /start 422 observed in 67-01 — a clean orchestrator resolves it.

    MTP FILTER NOTE (verified live in plan 67-01): tests/BaseApi.Tests is xunit.v3 under
    Microsoft.Testing.Platform, which SILENTLY IGNORES `dotnet test --filter` (VSTest syntax) and
    runs the entire 638-test suite. This harness MUST use the MTP-native filter passed AFTER `--`:
    `-- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*"` (seed) and
    `-- --filter-method "*Analyze_Window_Yields_Pass*"` (analyzer).

.NOTES
    Dev/ops-only tooling. No product source touched. Teardown keeps volumes + images (never the
    volume-dropping flag) so the proof data survives between runs.
    Fully automated — no interactive prompt anywhere (FAULT-03 / V11).
#>

param([Parameter(Mandatory)][string]$ScenarioId)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    # -----------------------------------------------------------------------
    # FRAME 2 — prefixed console-trace helper.
    # -----------------------------------------------------------------------
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-67-harness] $msg" -ForegroundColor $color
    }

    # -----------------------------------------------------------------------
    # FRAME 10 / D-12 — in-script scenario table (the Phase 68 "just data" seam).
    # [ordered] preserves TEST-01-first (D-10). Phase 68 adds rows 03-07 with the
    # SAME shape — { targetContainers, faultType, injectAfterNFires, dwellSeconds, notes }.
    # -----------------------------------------------------------------------
    $Scenarios = [ordered]@{
        'TEST-01' = @{ targetContainers = @();                   faultType = 'none';       injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
        'TEST-02' = @{ targetContainers = @('processor-sample'); faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
        'TEST-03' = @{ targetContainers = @('orchestrator');        faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'orchestrator crash — RAMJobStore re-hydration from L2 parent index' }
        'TEST-04' = @{ targetContainers = @('keeper');              faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'keeper whole-tier crash (BOTH replicas — total liveness blackout)' }
        'TEST-05' = @{ targetContainers = @('redis');               faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis crash — L2 slot-array + liveness + BIT probe' }
        'TEST-06' = @{ targetContainers = @('rabbitmq');            faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'rabbitmq crash — nack-requeue redelivery on reconnect' }
        'TEST-07' = @{ targetContainers = @('redis','rabbitmq');    faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis + rabbitmq combined crash' }
        # NEGATIVE-PATH scenarios (NOT in the default phase-68 sweep — run explicitly by id):
        'TEST-08' = @{ targetContainers = @('processor-sample'); faultType = 'stop-only';       injectAfterNFires = 4; dwellSeconds = 0; notes = 'NEGATIVE (blind-spot demo): processor crash, NO recovery — dispatched-but-never-processed work is INVISIBLE (no Step_A) → PASS, proving the verdict cannot see fully-dead loss' }
        'TEST-09' = @{ targetContainers = @('processor-sample'); faultType = 'stop-on-inflight'; injectAfterNFires = 3; dwellSeconds = 0; notes = 'NEGATIVE (RMQ-timed FAIL): with the 3s per-hop delay hook on, kill the processor once its dispatch queue backlog >= 3 (executions visibly mid-flight, no recovery) — strands recoverable-but-lost runs → binding miss → FAIL' }
        'TEST-10' = @{ targetContainers = @('processor-sample'); faultType = 'stopstart-on-inflight'; injectAfterNFires = 3; dwellSeconds = 300; notes = 'OUTAGE-OUTLASTS-WINDOW: 3s delay hook on; kill on in-flight backlog then keep the processor down 300s (> the 300s observation window) and RESTART — does recovery complete within window+drain (PASS) or exceed the budget (FAIL)?' }
    }

    # Validate the requested id against the table BEFORE any docker/psql op (T-67-02).
    # Use a dedicated config-usage code (64) so a bad argument never collides with an
    # infra abort (10-70) or the analyzer FAIL verdict (1).
    if (-not $Scenarios.Contains($ScenarioId)) {
        Write-Phase "unknown scenario '$ScenarioId'. Known: $($Scenarios.Keys -join ', ')" 'Red'
        exit 64
    }
    $scenario = $Scenarios[$ScenarioId]
    Write-Phase "scenario '$ScenarioId' — $($scenario.notes) (faultType=$($scenario.faultType), N=$($scenario.injectAfterNFires), dwell=$($scenario.dwellSeconds)s)"

    # TEST-ONLY: for the RMQ-timed negative path (TEST-09), export PROCESSOR_STEP_DELAY_MS BEFORE STEP A so
    # `docker compose up` interpolates it (${PROCESSOR_STEP_DELAY_MS:-0}) into the processor-sample container.
    # A 3s per-hop delay holds executions VISIBLY in-flight so the queue-depth trigger can catch one and the
    # kill can strand it (the fast default pipeline exposes no catchable in-flight window). Cleared in finally.
    if ($scenario.faultType -in @('stop-on-inflight','stopstart-on-inflight')) {
        $env:PROCESSOR_STEP_DELAY_MS = '3000'
        Write-Phase "  TEST-ONLY hook: PROCESSOR_STEP_DELAY_MS=3000 exported (baked into processor-sample at compose-up)." 'Yellow'
    }

    # -----------------------------------------------------------------------
    # STEP A0 — IMAGE REBUILD (code 10) — SourceHash currency guarantee.
    # The processor self-registers its L2 liveness under a processor id derived from its
    # assembly-embedded SourceHash; the seeder (running on the TEST HOST) resolves the
    # workflow's processor by reflecting the host-built Processor.Sample.dll SourceHash. If
    # the running CONTAINER image is stale (built from older source than the working tree),
    # the container registers under a DIFFERENT SourceHash → a DIFFERENT processor id than
    # the one the seeded v8-fanout-proof workflow binds, and the ProcessorLivenessValidator
    # gate correctly 422s the POST /start (0 replicas for the seeded processor id). This was
    # observed live in plan 67-03: container hash 536d0868… (proc 2f6f59b0…) vs host build
    # a67a3ed8… (proc 3cf7023b…). `phase-65-up.ps1` does a plain `docker compose up -d` with
    # NO rebuild, so a stale image silently survives. Building here (and force-recreating in
    # STEP A) guarantees the container SourceHash == the seeder's host-build SourceHash, so
    # the seeded workflow's processor id matches a live replica. Build is no-op fast when the
    # image is already current (BuildKit layer cache).
    # -----------------------------------------------------------------------
    Write-Phase "STEP A0: build images (SourceHash currency — container must match host-built Processor.Sample)"
    docker compose build 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "image build failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }

    # -----------------------------------------------------------------------
    # STEP A — BRING-UP (FRAME 3; code 10).
    # Force-recreate so the just-built image is the one actually running (a stale container
    # from a prior run would otherwise keep its old SourceHash even after the image rebuild).
    # -----------------------------------------------------------------------
    Write-Phase "STEP A: bring-up (docker compose up -d --force-recreate, then phase-65-up.ps1 health gate)"
    docker compose up -d --force-recreate 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "force-recreate up failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }
    pwsh -File (Join-Path $PSScriptRoot 'phase-65-up.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "bring-up failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }

    # =======================================================================
    # PER-RUN body for the single requested $ScenarioId. Phase 67 invokes this
    # script once per id (TEST-01 then TEST-02 = two separate invocations per
    # Plan 03 / D-10); the multi-id loop is NOT in this script.
    # =======================================================================

    # -----------------------------------------------------------------------
    # STEP B — RESET (FRAME 3; code 20).
    # -----------------------------------------------------------------------
    Write-Phase "STEP B: reset (phase-65-reset.ps1)"
    pwsh -File (Join-Path $PSScriptRoot 'phase-65-reset.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "reset failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 20 }

    # -----------------------------------------------------------------------
    # STEP B1 — CLEAN-ORCHESTRATOR GUARANTEE (code 25).
    # Restart the orchestrator AFTER the reset (empty L2 parent index + clean DB)
    # and BEFORE seed+start so its in-process Quartz RAMJobStore is wiped of all
    # ghost crons. On boot HydrationBackgroundService SMEMBERS the (now empty)
    # parent index and schedules nothing; the orchestrator healthcheck
    # (/health/ready, gated on initial-hydration-complete) only returns healthy
    # once that empty hydration finished. Waiting for Health=healthy is therefore
    # the proof the orchestrator is clean. ONLY the freshly seeded+started
    # v8-fanout-proof workflow will fire in the window after this.
    # -----------------------------------------------------------------------
    Write-Phase "STEP B1: clean orchestrator (docker compose restart orchestrator) — drop ghost Quartz crons"
    docker compose restart orchestrator | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "orchestrator restart failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 25 }

    # Post-restart NDJSON-per-replica health-wait (FRAME 9). orchestrator is a single
    # named instance (sk-orchestrator) WITH a healthcheck — require it healthy before
    # seed/start so the POST /start does not 422 against a still-hydrating orchestrator.
    $orchDeadline = (Get-Date).AddSeconds(90)
    $orchHealthy = $false
    do {
        $orchInstances = @(docker compose ps orchestrator --format json 2>$null |
            Where-Object { $_ -match '\S' } |
            ForEach-Object { $_ | ConvertFrom-Json })
        if ($orchInstances.Count -gt 0) {
            $orchUnhealthy = @($orchInstances | Where-Object { $_.Health -ne 'healthy' })
            if ($orchUnhealthy.Count -eq 0) { $orchHealthy = $true }
        }
        if (-not $orchHealthy) {
            if ((Get-Date) -ge $orchDeadline) {
                Write-Phase "orchestrator did not return healthy within 90s after restart. Aborting." 'Red'; exit 25
            }
            Start-Sleep -Seconds 2
        }
    } while (-not $orchHealthy)
    Write-Phase "  orchestrator healthy after clean restart (empty hydration — no ghost crons)." 'Gray'

    # -----------------------------------------------------------------------
    # STEP C — SEED (FRAME 4; code 30).
    # MTP-native filter after `--` (NOT VSTest `--filter` — silently ignored under MTP).
    # -----------------------------------------------------------------------
    Write-Phase "STEP C: seed (dotnet test ~FanOutSeeder)"
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "seeder failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 30 }

    # -----------------------------------------------------------------------
    # STEP D — WF-ID (FRAME 5 / D-02; code 40) — static-literal psql, no interpolated input (T-67-01).
    # -----------------------------------------------------------------------
    Write-Phase "STEP D: resolve v8-fanout-proof workflow id (psql)"
    $wfId = (docker compose exec -T postgres psql -U postgres -d stepsdb -tA `
              -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'").Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) {
        Write-Phase "could not resolve v8-fanout-proof workflow id (psql exit $LASTEXITCODE). Aborting." 'Red'; exit 40
    }
    Write-Phase "resolved wfId = $wfId"

    # -----------------------------------------------------------------------
    # STEP E — ACTIVATION 204 (FRAME 6 / D-03; code 50).
    # ConvertTo-Json @($wfId) forces the JSON array even for a single id (Pitfall 4).
    # -----------------------------------------------------------------------
    Write-Phase "STEP E: activation gate (POST /orchestration/start, require 204)"
    $startBody = ConvertTo-Json @($wfId)
    try {
        $resp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
                  -ContentType 'application/json' -Body $startBody -TimeoutSec 15 -ErrorAction Stop
    } catch { Write-Phase "POST /orchestration/start threw: $($_.Exception.Message)" 'Red'; exit 50 }
    if ($resp.StatusCode -ne 204) {
        Write-Phase "activation gate failed — expected 204, got $($resp.StatusCode). Aborting." 'Red'; exit 50
    }
    Write-Phase "  activation accepted (204)." 'Gray'

    # -----------------------------------------------------------------------
    # STEP F.1 — RECORD WINDOW START + read the baseline Prometheus fire counter (D-13/D-07).
    # Fire signal = orchestrator_messages_sent_total summed across label series — the SAME
    # counter the analyzer's trigger denominator uses (AnalyzerE2ETests.cs:119-123). The
    # harness does NO Prom correctness logic beyond this fire count (Pitfall 5): it does NOT
    # inspect Prom deltas and does NOT abort on a counter discontinuity from the restart;
    # scoring is the analyzer's job (ES-primary).
    # -----------------------------------------------------------------------
    $windowStart = [DateTimeOffset]::UtcNow
    # RECOVERY_UTC seam (Plan 3 / B-criterion): the instant ALL crashed tiers returned healthy in STEP F.4.
    # Stays $null for the no-fault baseline (TEST-01). Declared here so it is in scope in STEP H even on the
    # no-fault path. The analyzer uses it to classify a started-but-incomplete run as a TOLERATED in-flight-at-
    # wipe loss (last hop < recovery) vs a BINDING post-recovery miss.
    $recoveryUtc = $null
    Write-Phase "STEP F: window open at $($windowStart.ToString('o'))"

    function Get-FireCount {
        $r = Invoke-RestMethod -Uri 'http://localhost:9090/api/v1/query?query=orchestrator_messages_sent_total' -TimeoutSec 10
        if (-not $r.data.result) { return 0 }
        # [long] (not [int]) — orchestrator_messages_sent_total summed across all label series on a
        # long-lived stack could exceed Int32.MaxValue and overflow to a negative baseline, breaking the
        # `observed -ge N` loop. A 64-bit cast is safe for a monotonic counter (IN-02).
        return [long][double]($r.data.result | ForEach-Object { [double]$_.value[1] } | Measure-Object -Sum).Sum
    }

    # Sum a Prometheus counter across all label series as a double. Used by the STEP F.6 conservation-settle
    # drain to compare orchestrator_messages_consumed against processor_messages_sent (the MG-1 pair).
    function Get-PromSum([string]$metric) {
        $r = Invoke-RestMethod -Uri "http://localhost:9090/api/v1/query?query=$metric" -TimeoutSec 10
        if (-not $r.data.result) { return [double]0 }
        return [double]($r.data.result | ForEach-Object { [double]$_.value[1] } | Measure-Object -Sum).Sum
    }
    # RMQ-timed negative-path helper (TEST-09): read the REAL-TIME depth of the processor's dispatch queue
    # (named by the ProcessorId GUID — the reinject targets queue:{ProcessorId:D}) via the RabbitMQ management
    # API (host 15673, guest/guest, vhost '/'). messages = ready + unacknowledged. depth >= 1 ⇒ hop dispatches
    # are queued/being-processed RIGHT NOW ⇒ executions are actively mid-round-trip. Unlike ES (batched log
    # export hides partials — started==terminal always), the broker queue exposes in-flight state instantly, so
    # a kill here strands a genuinely in-flight execution: those already carrying ≥1 visible hop are reinjected
    # by the keeper (data present) and, never completing on the dead processor, become recoverable-but-lost FAILs.
    function Get-ProcQueueDepth([string]$queueName) {
        $b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('guest:guest'))
        try {
            $r = Invoke-RestMethod -Uri "http://localhost:15673/api/queues/%2F/$queueName" `
                -Headers @{ Authorization = "Basic $b64" } -TimeoutSec 5
            return [int]$r.messages
        } catch { return 0 }
    }

    $fireBaseline = Get-FireCount
    Write-Phase "  baseline fire count = $fireBaseline" 'Gray'

    # 5-minute observation window (300s). Poll cadence ~5s.
    $windowSeconds = 300
    $windowDeadline = (Get-Date).AddSeconds($windowSeconds)

    # -----------------------------------------------------------------------
    # OPTIONAL (D75-7) — EXECUTION-BASED OBSERVATION WINDOW (default-OFF).
    # The ROADMAP marks this "Optionally": the core per-execution verdict rework
    # (D75-1..5,8) does NOT depend on it. When $env:K_EXECUTIONS is unset or 0 the
    # harness runs EXACTLY as before — the fixed 300s wall-clock window above is the
    # sole bound. When K > 0 the observe loop ALSO stops early once K distinct
    # executions (fires) have been observed, but the $windowDeadline wall-clock cap
    # is ALWAYS retained as an unconditional upper bound (T-75-09: a mis-set K that
    # is never reached can never hang the loop past the existing 300s window).
    #   default 0/unset ⇒ fixed 300s wall-clock unchanged
    #   K>0            ⇒ stop at K observed executions, still hard-capped by $windowDeadline
    $kExecutions = if ($env:K_EXECUTIONS) { [int]$env:K_EXECUTIONS } else { 0 }
    if ($kExecutions -gt 0) {
        Write-Phase "  OPTIONAL execution-based window ENABLED: K_EXECUTIONS=$kExecutions (hard-capped by the ${windowSeconds}s wall-clock)." 'Gray'
    }

    # -----------------------------------------------------------------------
    # STEP F.2 — OBSERVE LOOP until N observed fires (D-07; N from $scenario.injectAfterNFires),
    # bounded by the window deadline. For TEST-01 (N=0, faultType='none') the inject is skipped
    # entirely — fall straight through to the window hold. For a crash run, poll until
    # current - baseline >= N (proves the cron is ACTUALLY firing — V6). If the window elapses
    # without reaching N, abort loud (exit 60).
    # -----------------------------------------------------------------------
    if ($scenario.faultType -ne 'none') {
        # -------------------------------------------------------------------
        # STEP F.2 — TRIGGER. 'stop-start'/'stop-only': wait for N observed fires (proves the cron is
        # firing — V6). 'stop-on-inflight' (ES-timed negative path): poll ES FAST until a started-but-not-
        # yet-terminal execution EXISTS in-window (Step_A exec-cardinality > Step_G exec-cardinality), then
        # kill immediately. This defeats the invisibility blind spot: a fire-timed kill of this fast, low-
        # concurrency pipeline lands either before Step_A (invisible/fully-dead → PASS) or after Step_G
        # (already complete → PASS); reacting to real ES state lands the kill on a VISIBLE in-flight run.
        # -------------------------------------------------------------------
        if ($scenario.faultType -in @('stop-on-inflight','stopstart-on-inflight')) {
            # Resolve the processor's dispatch queue name = its ProcessorId GUID (steps.processor_id, FK
            # fk_step_processor_id). After STEP B reset + STEP C seed, the only steps are the v8-fanout-proof's,
            # so one distinct processor_id — the queue the two processor-sample replicas consume.
            $procId = (docker compose exec -T postgres psql -U postgres -d stepsdb -t -A -c "SELECT processor_id FROM steps LIMIT 1;" 2>$null | Where-Object { $_ -match '\S' } | Select-Object -First 1)
            $procId = "$procId".Trim()
            if (-not $procId) { Write-Phase "could not resolve processor queue id (steps.processor_id empty). Aborting." 'Red'; exit 60 }
            Write-Phase "STEP F.2: RMQ-timed trigger — polling processor queue '$procId' depth (real-time in-flight) before inject"
            $triggered = $false
            while ((Get-Date) -lt $windowDeadline) {
                $depth = Get-ProcQueueDepth $procId
                if ($depth -ge $scenario.injectAfterNFires) {
                    Write-Phase "  processor queue '$procId' depth=$depth (>= $($scenario.injectAfterNFires)) — in-flight dispatch(es) present; injecting fault NOW." 'Gray'
                    $triggered = $true; break
                }
                Start-Sleep -Milliseconds 300
            }
            if (-not $triggered) {
                Write-Phase "processor queue never showed in-flight depth before window close. Aborting." 'Red'; exit 60
            }
        }
        else {
            Write-Phase "STEP F.2: observe-loop — waiting for N=$($scenario.injectAfterNFires) fires before inject"
            $reachedN = $false
            while ((Get-Date) -lt $windowDeadline) {
                $observed = (Get-FireCount) - $fireBaseline
                if ($observed -ge $scenario.injectAfterNFires) { $reachedN = $true; break }
                Start-Sleep -Seconds 5
            }
            if (-not $reachedN) {
                Write-Phase "baseline never reached N=$($scenario.injectAfterNFires) fires before window close. Aborting." 'Red'; exit 60
            }
            Write-Phase "  reached N=$($scenario.injectAfterNFires) observed fires — injecting fault." 'Gray'
        }

        # -------------------------------------------------------------------
        # STEP F.3 — CRASH SEQUENCER (FRAME 8 / D-05/06/08; code 60). Whole-tier stop via the
        # compose SERVICE name (Pitfall 2 — never a generated/literal container name). NOT `docker kill`
        # (restart:unless-stopped would auto-resurrect).
        # -------------------------------------------------------------------
        foreach ($svc in $scenario.targetContainers) {
            Write-Phase "STEP F.3: crashing whole tier '$svc' (docker compose stop)"
            docker compose stop $svc | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Phase "docker compose stop $svc failed." 'Red'; exit 60 }
        }

        if ($scenario.faultType -in @('stop-only','stop-on-inflight')) {
            # -------------------------------------------------------------------
            # NEGATIVE PATH (no recovery). Leave the tier DOWN: no dwell, no restart, no health-wait. Pin
            # RECOVERY_UTC at WINDOW START so the analyzer treats the whole window as post-recovery — every
            # started-but-incomplete execution is then a BINDING miss (startedAfterRecovery ⇒ the redis-wipe
            # in-flight tolerance never applies; nothing was wiped). A VISIBLE incomplete execution (Step_A,
            # no terminal Step_G) whose L2 data is still present (900s TTL) is recoverable-but-lost ⇒ FAIL
            # (also vetoed by the keeper "reinject" log per WR-01). Tier stays down until STEP Z teardown.
            # -------------------------------------------------------------------
            $recoveryUtc = $windowStart
            Write-Phase "  NEGATIVE PATH ($($scenario.faultType)): '$($scenario.targetContainers -join ',')' left DOWN (no restart); RECOVERY_UTC pinned at window start $($recoveryUtc.ToString('o'))." 'Yellow'
        }
        else {
            Write-Phase "  dwell $($scenario.dwellSeconds)s (tier down)..."
            Start-Sleep -Seconds $scenario.dwellSeconds
            foreach ($svc in $scenario.targetContainers) {
                Write-Phase "STEP F.3: restarting tier '$svc' (docker compose start)"
                docker compose start $svc | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Phase "docker compose start $svc failed." 'Red'; exit 60 }
            }

            # -------------------------------------------------------------------
            # STEP F.4 — POST-START HEALTH-WAIT (FRAME 9 / Pitfall 3; code 60). For each crashed
            # service, require ALL instances Health=healthy before proceeding — so the NEXT run's
            # phase-65-reset (Plan 03 between-runs) does not abort on 0 replicas. NDJSON-per-replica
            # parse copied verbatim from phase-65-up.ps1:37-73 (processor-sample always has a
            # healthcheck — the otel no-healthcheck branch is N/A here). Bounded 90s deadline.
            # -------------------------------------------------------------------
            foreach ($svc in $scenario.targetContainers) {
                Write-Phase "STEP F.4: waiting for crashed tier '$svc' to return healthy (bounded 90s)"
                $svcDeadline = (Get-Date).AddSeconds(90)
                $svcHealthy = $false
                do {
                    $instances = @(docker compose ps $svc --format json 2>$null |
                        Where-Object { $_ -match '\S' } |
                        ForEach-Object { $_ | ConvertFrom-Json })
                    if ($instances.Count -gt 0) {
                        $unhealthy = @($instances | Where-Object { $_.Health -ne 'healthy' })
                        if ($unhealthy.Count -eq 0) { $svcHealthy = $true }
                    }
                    if (-not $svcHealthy) {
                        if ((Get-Date) -ge $svcDeadline) {
                            Write-Phase "crashed tier '$svc' did not return healthy before deadline. Aborting." 'Red'; exit 60
                        }
                        Start-Sleep -Seconds 2
                    }
                } while (-not $svcHealthy)
                Write-Phase "  tier '$svc' healthy again ($($instances.Count) instance(s))." 'Gray'
            }
            # All crashed tiers confirmed healthy — record the recovery instant (B-criterion seam).
            $recoveryUtc = [DateTimeOffset]::UtcNow
            Write-Phase "  RECOVERY_UTC = $($recoveryUtc.ToString('o')) (all crashed tiers healthy)." 'Gray'
        }
    }
    else {
        Write-Phase "STEP F.2: no-fault baseline — no injection (faultType='$($scenario.faultType)')"
    }

    # -----------------------------------------------------------------------
    # STEP F.5 — HOLD OUT THE REST OF THE 5-MIN WINDOW, then record windowEnd. For TEST-01 this
    # is the whole post-activation wait; for TEST-02 it is the remainder after recovery.
    #
    # OPTIONAL (D75-7): when $kExecutions -gt 0, ALSO stop early once K distinct executions have
    # been observed (from the SAME Get-FireCount Prometheus signal the observe loop already reads:
    # current - $fireBaseline = distinct executions fired this window). The wall-clock bound
    # ($windowSeconds since $windowStart) is retained UNCONDITIONALLY as the hard cap, so the loop
    # can never run past the existing 300s window regardless of K (T-75-09). Default-off
    # (K=0/unset) leaves this loop byte-for-byte the fixed wall-clock hold.
    # -----------------------------------------------------------------------
    while (([DateTimeOffset]::UtcNow - $windowStart).TotalSeconds -lt $windowSeconds) {
        if ($kExecutions -gt 0) {
            $observedExecutions = (Get-FireCount) - $fireBaseline
            if ($observedExecutions -ge $kExecutions) {
                Write-Phase "  OPTIONAL execution-based window: observed $observedExecutions >= K=$kExecutions distinct executions — closing window early (wall-clock cap not reached)." 'Gray'
                break
            }
        }
        Start-Sleep -Seconds 5
    }
    $windowCloseUtc = [DateTimeOffset]::UtcNow
    Write-Phase "STEP F: observation window closed at $($windowCloseUtc.ToString('o')) ($([int](($windowCloseUtc - $windowStart).TotalSeconds))s)"

    # STEP F.6 — DRAIN THE RESULT PIPELINE TO QUIESCENCE, then pin windowEnd POST-DRAIN. MG-1 result
    # conservation (orchestrator_messages_consumed == processor_messages_sent) is a windowed delta the analyzer
    # reads TIME-PINNED to windowEnd, so windowEnd MUST be a SETTLED state. Waiting for orchestrator_messages_sent
    # (dispatches) to merely flatten is INSUFFICIENT: after a broker crash the orchestrator still has a
    # redelivered RESULT backlog to consume, so consumed LAGS sent (observed live on TEST-06: proc_sent 188 vs
    # orch_consumed 166 under the old sent-flat drain). So poll until BOTH orchestrator_messages_consumed AND
    # processor_messages_sent are flat over one interval — the pipeline has settled, nothing in flight — then
    # pin windowEnd. The workflow is stopped first so no new fires occur; the ES [windowStart, windowEnd] cohort
    # is unchanged. For MG-1 reporting-only scenarios (processor/orchestrator crash counter resets, redis-wipe
    # loss) the counters settle with a residual gap that MG-1 ignores; the both-flat break still fires (and the
    # deadline bounds it regardless).
    Write-Phase "STEP F.6: stop workflow + drain result pipeline to quiescence (conservation settle)"
    $stopBody = ConvertTo-Json @($wfId)
    try { Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/stop' `
            -ContentType 'application/json' -Body $stopBody -TimeoutSec 15 -ErrorAction Stop | Out-Null } catch { }
    # Break on GAP CONVERGENCE, not "both flat": the orchestrator and processor export their counters
    # INDEPENDENTLY on a ~60s OTLP cadence, so a counter can read flat for up to 60s BETWEEN exports even
    # while its true value differs from the other service's. A "both flat over 15s" break therefore fires
    # during an inter-export gap and pins windowEnd with a stale skew (observed live on TEST-04: orch_consumed
    # @end 178 vs proc_sent 200, gap 22, despite ES 18/18 clean). Instead wait until |consumed - sent| <= 1 —
    # for a binding scenario with no real loss the gap converges to 0 once both services' final values export
    # (within one ~60s interval). A reporting-only scenario (counter reset / wipe loss) never converges; once
    # both counters are flat across >=100s (> one export interval, so the flatness is REAL, not skew) stop
    # waiting — MG-1 ignores that residual gap. The 180s deadline bounds both paths.
    $prevC = -1; $prevS = -1; $polls = 0; $drainDeadline = (Get-Date).AddSeconds(180)
    do {
        Start-Sleep -Seconds 20
        $polls++
        $c = Get-PromSum 'orchestrator_messages_consumed_total'
        $s = Get-PromSum 'processor_messages_sent_total'
        $gap = [math]::Abs($c - $s)
        Write-Phase "  drain: orch_consumed=$c proc_sent=$s gap=$gap" 'Gray'
        if ($gap -le 1) { break }                                              # converged (binding scenarios)
        if ($c -eq $prevC -and $s -eq $prevS -and $polls -ge 5) { break }      # real flat past one export interval (reporting-only)
        $prevC = $c; $prevS = $s
    } while ((Get-Date) -lt $drainDeadline)
    $windowEnd = [DateTimeOffset]::UtcNow
    Write-Phase "  drained (orch_consumed=$c, proc_sent=$s, gap=$([math]::Abs($c - $s))); windowEnd pinned post-drain at $($windowEnd.ToString('o'))." 'Gray'

    # -----------------------------------------------------------------------
    # STEP H — DRAIN + ANALYZE (FRAME 4 / D-04 / D-16; VERDICT — do NOT remap to an infra code).
    # Set the D-16 env seam (SCENARIO_ID / WINDOW_START_UTC / WINDOW_END_UTC) from the recorded
    # window + scenario id, then invoke the analyzer via the MTP-native filter. The fixture's
    # internal DrainMs (60s) + poll-to-stable (60s) provide the settle, so no extra harness sleep
    # is needed beyond the window close. The analyzer's exit IS the harness verdict.
    # -----------------------------------------------------------------------
    Write-Phase "STEP H: analyze (dotnet test ~Analyzer) for scenario $ScenarioId"
    $env:SCENARIO_ID      = $ScenarioId
    $env:WINDOW_START_UTC = $windowStart.ToString('o')
    $env:WINDOW_END_UTC   = $windowEnd.ToString('o')
    # RECOVERY_UTC (B-criterion): the all-tiers-healthy instant for a fault run, or '' for the no-fault
    # baseline (TEST-01). An empty value parses as no-recovery in the analyzer (all incompletes are binding).
    $env:RECOVERY_UTC     = if ($recoveryUtc) { $recoveryUtc.ToString('o') } else { '' }
    # OPTIONAL (D75-7) K_EXECUTIONS seam, set + cleared symmetrically with the other D-16 seams.
    # Empty when the execution-based window is off (K=0/unset) — the analyzer ignores an empty
    # value exactly like RECOVERY_UTC on the no-fault baseline (no behaviour change unless adopted
    # downstream). Carries only a single integer count; no secrets/PII (T-75-10).
    $env:K_EXECUTIONS     = if ($kExecutions -gt 0) { $kExecutions.ToString() } else { '' }
    # IN-03: clear the D-16 env seam in a `finally` so a terminating error inside the analyze block
    # ($ErrorActionPreference='Stop') cannot leak SCENARIO_ID / WINDOW_*_UTC into the parent shell
    # (matters when the body is dot-sourced / run interactively; harmless for the one-shot `pwsh -File`).
    try {
        # IN-04: this --filter-method targets the fixture named "*Analyze_Window_Yields_Pass*" for EVERY
        # scenario, including fault runs (TEST-02..07). The capstone (Phase 68) requires a RECOVERED fault
        # run to assert PASS: zero-missing + effect-once hold once the crashed tier rejoins, so exit 0 is
        # what every scenario must produce. A non-zero $analyzerExit is the legitimate verdict FAIL
        # (exit code 1 per the D-04 table) — a REAL finding to investigate (D-01b: a stateful tier whose
        # recovery exceeds the window), NOT an infra error and NOT a normal result. The exit code
        # mirrors the verdict either way. The fixture method and this --filter-method literal MUST stay in
        # sync (renamed together — an out-of-sync literal silently runs the whole 638-test suite).
        dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*Analyze_Window_Yields_Pass*" 2>&1 | Out-String | Write-Host
        $analyzerExit = $LASTEXITCODE
    } finally {
        Remove-Item Env:SCENARIO_ID, Env:WINDOW_START_UTC, Env:WINDOW_END_UTC, Env:RECOVERY_UTC, Env:K_EXECUTIONS, Env:PROCESSOR_STEP_DELAY_MS -ErrorAction SilentlyContinue
    }

    # Locate + echo the analyzer report path (D-04 requires printing it).
    $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$ScenarioId.json" -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
    if ($report) { Write-Phase "analyzer report: $($report.FullName)" 'Green' }
    else { Write-Phase "WARNING: analyzer-reports/$ScenarioId.json not found" 'Yellow' }
    Write-Phase "analyzer verdict exit = $analyzerExit (0=PASS, non-0=FAIL)" $(if ($analyzerExit -eq 0) { 'Green' } else { 'Yellow' })

    # -----------------------------------------------------------------------
    # STEP Z — TEARDOWN (FRAME 11 / D-15; code 70 NON-FATAL). `docker compose down` keeps volumes
    # + images (NEVER `-v`). A down failure logs loud but the harness STILL surfaces the analyzer
    # verdict — the FINAL exit mirrors the analyzer (D-04), never the teardown result.
    # -----------------------------------------------------------------------
    # DIAGNOSTIC (opt-in via $env:CAPTURE_LOGS) — dump container logs BEFORE teardown removes them, so a
    # post-mortem can trace WHY specific executions were lost (keeper reinject/drop decisions per execution).
    # Gated so it never affects normal runs; writes logs-<svc>-<scenario>.txt in the repo root.
    if ($env:CAPTURE_LOGS) {
        Write-Phase "CAPTURE_LOGS: dumping keeper/processor/orchestrator logs before teardown" 'Yellow'
        docker compose logs keeper --no-color --timestamps          > "logs-keeper-$ScenarioId.txt" 2>&1
        docker compose logs processor-sample --no-color --timestamps > "logs-processor-$ScenarioId.txt" 2>&1
        docker compose logs orchestrator --no-color --timestamps     > "logs-orchestrator-$ScenarioId.txt" 2>&1
    }

    Write-Phase "STEP Z: teardown (docker compose down — keep volumes + images)"
    docker compose down | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "teardown failed (would be exit 70) — surfacing analyzer verdict regardless." 'Yellow' }

    exit $analyzerExit

} finally {
    Pop-Location
}
