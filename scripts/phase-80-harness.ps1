<#
.SYNOPSIS
    Phase 80 k8s happy-path harness (TEST-01 baseline only — the D-16 proof runner).

.DESCRIPTION
    The Kubernetes (Docker Desktop) sibling of scripts/phase-67-harness.ps1. It chains the
    already-proven Phase 80 k8s artifacts (phase-80-build.ps1 / phase-80-up.ps1 /
    phase-80-reset.ps1) plus the VERBATIM-reused seeder + POST /start + analyzer into a single
    fully-automated happy-path run:

        pwsh -File scripts/phase-80-harness.ps1

    Flow (TEST-01, faultType='none' — NO fault seams, NO crash sequencer):

        STEP A0  phase-80-build.ps1                  build the 4 app images :local (SourceHash currency)
        STEP A   phase-80-up.ps1                     kubectl apply -k + rollout + rollout-restart +
                                                     8 loopback port-forwards + baseapi /health/ready gate
        STEP B0  kubectl rollout restart processor-sample + rollout status
                                                     reset proc_sent counter baseline per scenario (MG-1 fix)
        STEP B   phase-80-reset.ps1                  kubectl-exec FLUSHALL + heal-wait + FK-safe graph DELETE
        STEP B1  kubectl rollout restart orchestrator + rollout status
                                                     GUARANTEE A CLEAN ORCHESTRATOR (no ghost Quartz crons)
        STEP C   dotnet test ~FanOutSeeder           seed v8-fanout-proof (VERBATIM reuse — localhost ports)
        STEP D   kubectl exec psql sentinel lookup   resolve the v8-fanout-proof workflow id
        STEP E   POST /orchestration/start           activation gate — REQUIRE HTTP 204 (VERBATIM reuse)
        STEP F   hold the 300s observation window     no-fault baseline — no inject; then drain to quiescence
        STEP H   dotnet test ~Analyzer               verdict (D-16 env seam; VERBATIM reuse — localhost ports)
        STEP Z   stop port-forwards                   teardown (keep the stack + PVCs; -TearDownCluster to delete)

    This completes the SECOND half of the RESEARCH Pitfall-1 re-target: phase-67-harness.ps1
    STEP B1 (the compose orchestrator-restart -> `kubectl rollout restart`) and STEP D
    (the compose postgres-psql wf-id -> `kubectl exec`). The seeder (STEP C), POST /start
    (STEP E) and analyzer (STEP H) are reused BYTE-FOR-BYTE — they use localhost ports covered by the
    plan-80-09 port-forwards (8080/9090/9200/5673/6380/5433/15673/4317).

    Final harness exit code == the analyzer verdict (0 = PASS, non-0 = FAIL). Each infra step fails
    loud with a DISTINCT non-zero code so an infra abort is never mistaken for a verdict.

    EXIT-CODE TABLE (mirrors phase-67-harness.ps1 D-04):
        0   analyzer PASS (final verdict green)
        1   analyzer FAIL verdict (mirrors the dotnet test exit — the legitimate verdict path)
        2   analyzer INCONCLUSIVE verdict (observability-degraded — deliberate warm-ES re-run, NO auto-retry)
        10  bring-up (phase-80-build.ps1 / phase-80-up.ps1) failed
        20  reset (phase-80-reset.ps1) failed
        25  orchestrator clean rollout-restart / rollout-status failed (clean-window guarantee, STEP B1)
        30  seeder (dotnet test ~FanOutSeeder) failed
        40  wf-id kubectl-exec psql lookup failed/empty
        50  activation gate != 204
        60  fault inject / recover / firing failed (crash sequencer — STEP F.2/F.3/F.4)
        64  bad -ScenarioId argument (config-usage error)

    CLEAN-WINDOW GUARANTEE (STEP B1): phase-80-reset.ps1 FLUSHALLs Redis and deletes the workflow-graph
    rows, but the long-running orchestrator keeps its already-registered Quartz crons in its in-process
    RAMJobStore — so without this step it keeps firing dozens of GHOST workflow crons (NULL payloads, no
    Step_* labels) that pollute the observation window and make the analyzer score everything MISSING.
    `kubectl rollout restart deployment/orchestrator` recreates the orchestrator pod AFTER the reset
    (empty L2 parent index) and BEFORE seed+start; on boot HydrationBackgroundService re-runs against an
    empty parent index and schedules nothing, so ONLY the freshly seeded+started v8-fanout-proof workflow
    fires in the window. `rollout status` waits for the readiness-gated fresh pod to be Available (the
    same dirty state also caused the POST /start 422 — a clean orchestrator resolves it).

    MTP FILTER NOTE: tests/BaseApi.Tests is xunit.v3 under Microsoft.Testing.Platform, which SILENTLY
    IGNORES `dotnet test --filter` (VSTest syntax). This harness MUST use the MTP-native filter passed
    AFTER `--`: `-- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*"` (seed) and
    `-- --filter-method "*Analyze_Window_Yields_Pass*"` (analyzer) — BYTE-FOR-BYTE as phase-67.

.PARAMETER ScenarioId
    Which capstone scenario to run (TEST-01..07). Default TEST-01 (the no-fault happy-path baseline —
    Phase 80's D-16 proof stays reproducible). TEST-02..07 crash a whole tier via `kubectl scale`
    (the crash sequencer, STEP F.2/F.3/F.4). An out-of-range id exits 64 (config-usage) before any op.

.PARAMETER SkipBringUp
    When set, STEP A0 (build) + STEP A (up + port-forwards) + the STEP Z port-forward teardown are
    skipped — for the plan 81-03 sweep that owns a ONE-TIME bring-up and shares the stack across the
    7 scenarios. STEP A2 (fresh-DB bootstrap) is NOT gated: its warm-DB guard self-skips.

.PARAMETER TearDownCluster
    When set, STEP Z ALSO runs `kubectl delete -k k8s/` after stopping the port-forwards. Default OFF —
    the stack + PVCs are kept for inspection between runs (mirrors phase-67's volume-preserving teardown).

.NOTES
    Dev/ops-only tooling. No product source touched. TEST-01 is the no-fault baseline (D-16,
    faultType='none'); TEST-02..07 crash a whole tier via `kubectl -n skp scale --replicas=0`/restore.
    Fully automated — no interactive prompt anywhere.
#>

param(
    [string]$ScenarioId = 'TEST-01',
    [switch]$SkipBringUp,
    [switch]$TearDownCluster
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {

    # -----------------------------------------------------------------------
    # prefixed console-trace helper.
    # -----------------------------------------------------------------------
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-80-harness] $msg" -ForegroundColor $color
    }

    # -----------------------------------------------------------------------
    # dot-source the shared exit-code resolution lib (pure functions, no side effects).
    # Resolve-AnalyzerExitCode maps the analyzer report Verdict -> 0/1/2 (STEP H); Resolve-SweepClass
    # maps the resolved code -> a human-readable class for the final trace line.
    # -----------------------------------------------------------------------
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')

    # -----------------------------------------------------------------------
    # SCENARIO TABLE + BAD-ID GUARD (code 64) — the k8s port of phase-67-harness.ps1's
    # 7 capstone rows ONLY (the extended + negative-control rows carried by the compose harness are
    # out of scope per SPEC / D-08). TEST-01 stays the default so Phase 80's no-fault proof stays reproducible
    # (SPEC req 1 / D-01). `targetContainers` names double as tier keys for the D-05 maps below —
    # they already match the k8s workload names (`app=<tier>` labels in k8s/*.yaml).
    # -----------------------------------------------------------------------
    $Scenarios = [ordered]@{
        'TEST-01' = @{ targetContainers = @();                    faultType = 'none';       injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
        'TEST-02' = @{ targetContainers = @('processor-sample');  faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
        'TEST-03' = @{ targetContainers = @('orchestrator');      faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'orchestrator crash — RAMJobStore re-hydration from L2 parent index' }
        'TEST-04' = @{ targetContainers = @('keeper');            faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'keeper whole-tier crash (BOTH replicas)' }
        'TEST-05' = @{ targetContainers = @('redis');             faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis crash — L2 wipe (no PVC)' }
        'TEST-06' = @{ targetContainers = @('rabbitmq');          faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'rabbitmq crash — durable queues survive (PVC)' }
        'TEST-07' = @{ targetContainers = @('redis','rabbitmq');  faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis + rabbitmq combined crash' }
    }
    if (-not $Scenarios.Contains($ScenarioId)) {
        Write-Phase "unknown scenario '$ScenarioId'. Known: $($Scenarios.Keys -join ', ')" 'Red'
        exit 64
    }
    $scenario = $Scenarios[$ScenarioId]
    $scenarioId = $ScenarioId   # keep the lowercase name used by STEP F.1/H (:332,:351,:366) unchanged
    Write-Phase "scenario '$ScenarioId' — $($scenario.notes) (faultType=$($scenario.faultType), N=$($scenario.injectAfterNFires), dwell=$($scenario.dwellSeconds)s)"

    # -----------------------------------------------------------------------
    # D-05 STATIC TIER MAPS (verified against k8s/*.yaml — see 81-PATTERNS.md). The crash
    # sequencer NEVER derives kind/replica from the -ScenarioId argument (threat T-81-01): the id
    # only selects a fixed table row; these maps are static in-script. NEVER a blanket --replicas=1
    # for the ×2 tiers (keeper=2, processor-sample=2 — SPEC req 3 / T-81-02).
    # -----------------------------------------------------------------------
    $TierKind = @{
        'processor-sample' = 'deployment'; 'orchestrator' = 'deployment'; 'keeper' = 'deployment'
        'redis' = 'statefulset'; 'rabbitmq' = 'statefulset'
    }
    $TierReplicas = @{
        'processor-sample' = 2; 'orchestrator' = 1; 'keeper' = 2; 'redis' = 1; 'rabbitmq' = 1
    }

    # -----------------------------------------------------------------------
    # STEP A0 — IMAGE BUILD (code 10) — SourceHash currency guarantee.
    # phase-80-build.ps1 builds the 4 app images :local so the container assembly-embedded SourceHash
    # matches the seeder's host-built Processor.Sample.dll. A stale image registers under a DIFFERENT
    # processor id than the seeded v8-fanout-proof binds → the ProcessorLivenessValidator 422s the
    # POST /start. Building here (and rollout-restarting in STEP A) guarantees currency.
    # -----------------------------------------------------------------------
    # -SkipBringUp (SPEC req 7 / D-03): when the sweep (plan 81-03) owns a ONE-TIME bring-up and
    # shares the stack across the 7 scenarios, STEP A0 (build) + STEP A (up + port-forwards) are
    # skipped. STEP A2 below is deliberately NOT gated — its warm-DB guard self-skips on a warm DB
    # but MUST still bootstrap the processor row on the first (fresh-PVC) sweep scenario.
    if (-not $SkipBringUp) {
        Write-Phase "STEP A0: build the 4 app images :local (SourceHash currency — phase-80-build.ps1)"
        pwsh -File (Join-Path $PSScriptRoot 'phase-80-build.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Phase "image build failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }

        # -------------------------------------------------------------------
        # STEP A — BRING-UP (code 10).
        # phase-80-up.ps1: compose-down (port-collision guard) → kubectl apply -k k8s/ →
        # phased rollout status (6 infra + 4 app) → rollout restart the 4 app Deployments onto fresh
        # :local bits → start the 8 loopback port-forwards → poll baseapi /health/ready. On return the
        # stack is UP and harness-reachable over localhost.
        # -------------------------------------------------------------------
        Write-Phase "STEP A: bring-up (phase-80-up.ps1 — kubectl apply -k + rollout + port-forwards + readiness gate)"
        pwsh -File (Join-Path $PSScriptRoot 'phase-80-up.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Phase "bring-up failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }
    } else {
        Write-Phase "STEP A0/A: -SkipBringUp — reusing the already-up stack + port-forwards (sweep-owned bring-up)." 'Gray'
    }

    # -----------------------------------------------------------------------
    # STEP A2 — FIRST-RUN BOOTSTRAP (code 30) — fresh-DB processor-row registration.
    # On a FRESH k8s PVC the `processors` table is empty, so no processor row exists for the current
    # SourceHash. The processor boots-before-register (ProcessorStartupOrchestrator): it polls for its
    # row and its liveness watchdog stays "not started" until the row exists — so the STEP B heal-wait
    # can NEVER converge on a fresh cluster (observed: "Liveness did not reconverge in 60s"). The compose
    # harness never hit this because its persistent DB always had a prior processor row (reset preserves
    # the `processors` table). Bootstrap-seed ONCE here (the seeder GET-or-creates the processor row via
    # SeedProcessorAsync + seeds a throwaway workflow the STEP B graph-DELETE wipes; its self-verify is a
    # DB-graph count, NOT a liveness check, so it succeeds with no live processor), then wait for
    # per-instance liveness to converge so STEP B has live processors to observe. GUARDED to the fresh-DB
    # case: a warm re-run (processor row already present, e.g. a second proof on the same PVC) skips
    # straight to the proven reset→seed→start path — byte-identical to the compose flow.
    Write-Phase "STEP A2: first-run bootstrap check (processors table empty => seed once to register the row)"
    $procCountRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA `
                    -c "SELECT count(*) FROM processors"
    if ($LASTEXITCODE -ne 0) { Write-Phase "processor-row precheck failed (psql exit $LASTEXITCODE). Aborting." 'Red'; exit 30 }
    # Normalize AFTER the exit-code guard — on a failed exec stdout is empty, so trimming the raw
    # capture (never $null once cast to string) can no longer throw a terminating error under
    # $ErrorActionPreference='Stop' and bypass the distinct `exit 30`.
    $procCount = ("$procCountRaw").Trim()
    if ($procCount -eq '0') {
        Write-Phase "  fresh DB (0 processor rows) — bootstrapping the processor row via one seed..." 'Yellow'
        dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Phase "bootstrap seed failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 30 }
        # Wait for per-instance liveness to converge: processors resolve identity (<=30s retry) then
        # heartbeat every 10s. Same per-instance key shape + exclusion regex as the STEP B heal-wait.
        Write-Phase "  waiting for per-replica liveness to converge after bootstrap (bounded 120s)..." 'Gray'
        $bootDeadline = (Get-Date).AddSeconds(120)
        $bootHealed = $false
        while ((Get-Date) -lt $bootDeadline) {
            $bk = @(kubectl -n skp exec statefulset/redis -- redis-cli --scan --pattern 'skp:proc:*' |
                    Where-Object { $_ -notmatch '^skp:proc:[^:]+$' })
            if ($bk.Count -ge 1) { $bootHealed = $true; break }
            Start-Sleep -Seconds 5
        }
        if (-not $bootHealed) { Write-Phase "bootstrap liveness did not converge within 120s. Aborting." 'Red'; exit 30 }
        Write-Phase "  bootstrap liveness converged ($($bk.Count) per-instance key(s)) — processor row registered." 'Gray'
    } else {
        Write-Phase "  warm DB ($procCount processor row(s) present) — skipping bootstrap (proven reset->seed->start path)." 'Gray'
    }

    # -----------------------------------------------------------------------
    # STEP B0 — PROCESSOR COUNTER-BASELINE RESET (code 20) — 81-04 gap fix (MG-1 conservation).
    # The shared-stack k8s sweep (D-03) restarts ONLY the orchestrator per scenario (STEP B1, for the
    # Quartz clean-window), so `orchestrator_consumed` resets each scenario while `processor_sent`
    # accumulates across the whole sweep. MG-1 compares the two counters at ABSOLUTE @end quiescence
    # (PassFailEngine ConservationTol, reused verbatim — analyzer UNCHANGED per SPEC req 5), so the two
    # binding crash scenarios (TEST-04 keeper, TEST-06 rabbitmq) fail on a false conservation gap despite
    # Missing=0 / zero-dup. The compose capstone avoided this by recreating the WHOLE stack per scenario.
    # Restart the processor-sample tier here too so BOTH conservation-counter-owning tiers share a
    # per-scenario baseline — matching the compose whole-stack recreate. Placed BEFORE the reset so the
    # STEP B heal-wait (polls skp:proc:*:* for liveness reconvergence, fail-loud) gates the fresh pods
    # before STEP C seeds (avoids the ProcessorLivenessValidator 422 on POST /start).
    # -----------------------------------------------------------------------
    Write-Phase "STEP B0: reset processor counter baseline (kubectl rollout restart deployment/processor-sample) — align proc_sent with orch_consumed for MG-1"
    kubectl -n skp rollout restart deployment/processor-sample | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "processor-sample rollout restart failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 20 }
    kubectl -n skp rollout status deployment/processor-sample --timeout=120s
    if ($LASTEXITCODE -ne 0) { Write-Phase "processor-sample did not become Available within 120s after rollout restart. Aborting." 'Red'; exit 20 }
    Write-Phase "  processor-sample restarted (fresh per-scenario counter baseline); STEP B heal-wait confirms liveness reconvergence." 'Gray'

    # -----------------------------------------------------------------------
    # STEP B — RESET (code 20).
    # phase-80-reset.ps1: kubectl-exec FLUSHALL + 60s heal-wait + FK-safe graph DELETE (processors +
    # config_schemas preserved). Stack stays UP.
    # -----------------------------------------------------------------------
    Write-Phase "STEP B: reset (phase-80-reset.ps1 — kubectl-exec FLUSHALL + heal-wait + graph DELETE)"
    pwsh -File (Join-Path $PSScriptRoot 'phase-80-reset.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "reset failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 20 }

    # -----------------------------------------------------------------------
    # STEP B1 — CLEAN-ORCHESTRATOR GUARANTEE (code 25) — Pitfall-1 re-target (second half).
    # Re-target of phase-67 STEP B1 (the compose orchestrator-restart) → kubectl. Recreate the
    # orchestrator pod AFTER the reset (empty L2 parent index + clean DB) and BEFORE seed+start so its
    # in-process Quartz RAMJobStore is wiped of all ghost crons. On boot HydrationBackgroundService
    # SMEMBERS the (now empty) parent index and schedules nothing; `rollout status` blocks until the
    # readiness-gated fresh pod is Available — the proof the orchestrator is clean. ONLY the freshly
    # seeded+started v8-fanout-proof workflow will fire in the window after this. The same dirty state
    # also caused the POST /start 422 — a clean orchestrator resolves it.
    # -----------------------------------------------------------------------
    Write-Phase "STEP B1: clean orchestrator (kubectl rollout restart deployment/orchestrator) — drop ghost Quartz crons"
    kubectl -n skp rollout restart deployment/orchestrator | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "orchestrator rollout restart failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 25 }
    kubectl -n skp rollout status deployment/orchestrator --timeout=120s
    if ($LASTEXITCODE -ne 0) { Write-Phase "orchestrator did not become Available within 120s after rollout restart. Aborting." 'Red'; exit 25 }
    Write-Phase "  orchestrator Available after clean rollout restart (empty hydration — no ghost crons)." 'Gray'

    # -----------------------------------------------------------------------
    # STEP C — SEED (code 30) — VERBATIM reuse (localhost ports via port-forward).
    # MTP-native filter after `--` (NOT VSTest `--filter` — silently ignored under MTP).
    # -----------------------------------------------------------------------
    Write-Phase "STEP C: seed (dotnet test ~FanOutSeeder)"
    dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*" 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "seeder failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 30 }

    # -----------------------------------------------------------------------
    # STEP D — WF-ID (code 40) — Pitfall-1 re-target (second half). Static-literal psql, no
    # interpolated input (threat T-80-20). Re-target of phase-67 STEP D
    # (the compose `-T postgres psql` exec) → `kubectl -n skp exec statefulset/postgres -- psql`.
    # -----------------------------------------------------------------------
    Write-Phase "STEP D: resolve v8-fanout-proof workflow id (kubectl exec psql)"
    $wfIdRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA `
              -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"
    # Pin the exit code BEFORE the (safe) trim — same fix as STEP A2: on a failed exec stdout is
    # empty, so `.Trim()` on the raw capture (string-cast, never $null) cannot throw and bypass exit 40.
    $wfIdExit = $LASTEXITCODE
    $wfId = ("$wfIdRaw").Trim()
    if ($wfIdExit -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) {
        Write-Phase "could not resolve v8-fanout-proof workflow id (psql exit $wfIdExit). Aborting." 'Red'; exit 40
    }
    Write-Phase "resolved wfId = $wfId"

    # -----------------------------------------------------------------------
    # STEP E — ACTIVATION 204 (code 50) — VERBATIM reuse (localhost:8080 via port-forward).
    # ConvertTo-Json @($wfId) forces the JSON array even for a single id.
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
    # STEP F.1 — RECORD WINDOW START (D-13/D-07). Fire signal = orchestrator_messages_sent_total summed
    # across label series — the SAME counter the analyzer's trigger denominator uses. The harness does NO
    # Prom correctness logic beyond this fire count; scoring is the analyzer's job (ES-primary).
    # RECOVERY_UTC stays $null for the no-fault baseline (TEST-01) — declared here so it is in scope in
    # STEP H. The analyzer parses an empty RECOVERY_UTC as no-recovery (every incomplete is binding).
    # -----------------------------------------------------------------------
    $windowStart = [DateTimeOffset]::UtcNow
    $recoveryUtc = $null
    Write-Phase "STEP F: window open at $($windowStart.ToString('o'))"

    function Get-FireCount {
        $r = Invoke-RestMethod -Uri 'http://localhost:9090/api/v1/query?query=orchestrator_messages_sent_total' -TimeoutSec 10
        if (-not $r.data.result) { return 0 }
        # [long] (not [int]) — a monotonic counter summed across all label series could exceed
        # Int32.MaxValue on a long-lived stack; a 64-bit cast avoids overflow to a negative baseline.
        return [long][double]($r.data.result | ForEach-Object { [double]$_.value[1] } | Measure-Object -Sum).Sum
    }

    # Sum a Prometheus counter across all label series as a double — used by the STEP F.6
    # conservation-settle drain to compare orchestrator_messages_consumed vs processor_messages_sent.
    function Get-PromSum([string]$metric) {
        $r = Invoke-RestMethod -Uri "http://localhost:9090/api/v1/query?query=$metric" -TimeoutSec 10
        if (-not $r.data.result) { return [double]0 }
        return [double]($r.data.result | ForEach-Object { [double]$_.value[1] } | Measure-Object -Sum).Sum
    }

    # 5-minute observation window (300s). Poll cadence ~5s. Set BEFORE the baseline read so the crash
    # branch's $windowDeadline (the observe-loop upper bound) can be computed alongside it.
    $windowSeconds = 300
    $fireBaseline = Get-FireCount
    # Absolute deadline for the STEP F.2 observe-loop (crash branch only; the no-fault path uses the
    # $windowStart-relative STEP F.5 hold). Mirrors phase-67-harness.ps1:337.
    $windowDeadline = (Get-Date).AddSeconds($windowSeconds)
    Write-Phase "  baseline fire count = $fireBaseline" 'Gray'

    # -----------------------------------------------------------------------
    # OPTIONAL execution-based observation window (default-OFF). When $env:K_EXECUTIONS is unset or 0
    # the harness runs EXACTLY as the fixed 300s wall-clock hold. When K > 0 the hold ALSO stops early
    # once K distinct executions (fires) have been observed, but the wall-clock cap is ALWAYS retained
    # as an unconditional upper bound (a mis-set K that is never reached can never hang the loop).
    # -----------------------------------------------------------------------
    $kExecutions = if ($env:K_EXECUTIONS) { [int]$env:K_EXECUTIONS } else { 0 }
    if ($kExecutions -gt 0) {
        Write-Phase "  OPTIONAL execution-based window ENABLED: K_EXECUTIONS=$kExecutions (hard-capped by the ${windowSeconds}s wall-clock)." 'Gray'
    }

    # -----------------------------------------------------------------------
    # STEP F.2/F.3/F.4 — FAULT BRANCH (code 60). The 'none' path (TEST-01) falls straight through to
    # the STEP F.5 window hold — unchanged. The 'stop-start' path (TEST-02..07) is the Pitfall-1
    # re-target of phase-67's compose crash sequencer: observe until N fires → scale each target tier
    # to 0 (kubectl) → wait 0 running pods → dwell → scale back to the exact Phase-80 count → wait
    # Ready → pin RECOVERY_UTC. Crash is purely kubectl-scale — no compose CLI (SPEC req 2). TEST-07
    # crashes BOTH redis+rabbitmq: crash loop → ONE shared dwell → restore loop → Ready loop.
    # -----------------------------------------------------------------------
    if ($scenario.faultType -eq 'none') {
        Write-Phase "STEP F.2: no-fault baseline — no injection (faultType='none')"
    } else {
        # STEP F.2 — OBSERVE until N fires (proves the cron is actually firing before we inject).
        Write-Phase "STEP F.2: observe-loop — waiting for N=$($scenario.injectAfterNFires) fires before inject"
        $reachedN = $false
        while ((Get-Date) -lt $windowDeadline) {
            $observed = (Get-FireCount) - $fireBaseline
            if ($observed -ge $scenario.injectAfterNFires) { $reachedN = $true; break }
            Start-Sleep -Seconds 5
        }
        if (-not $reachedN) { Write-Phase "baseline never reached N=$($scenario.injectAfterNFires) fires before window close. Aborting." 'Red'; exit 60 }
        Write-Phase "  reached N=$($scenario.injectAfterNFires) observed fires — injecting fault (kubectl scale)." 'Gray'

        # STEP F.3 — CRASH: scale each target tier to 0 (whole-tier). TEST-07 crashes BOTH redis+rabbitmq.
        foreach ($tier in $scenario.targetContainers) {
            $kind = $TierKind[$tier]
            Write-Phase "STEP F.3: crashing tier '$tier' ($kind) — kubectl -n skp scale $kind/$tier --replicas=0"
            kubectl -n skp scale "$kind/$tier" --replicas=0 | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Phase "scale '$tier' to 0 failed (exit $LASTEXITCODE)." 'Red'; exit 60 }
        }
        # STEP F.3 — TERMINATE-WAIT (SPEC req 4): block until 0 running pods per crashed tier (bounded 90s).
        foreach ($tier in $scenario.targetContainers) {
            $termDeadline = (Get-Date).AddSeconds(90); $terminated = $false
            while ((Get-Date) -lt $termDeadline) {
                $running = @(kubectl -n skp get pods -l app=$tier --field-selector=status.phase=Running -o name 2>$null |
                            Where-Object { $_ -match '\S' })
                if ($running.Count -eq 0) { $terminated = $true; break }
                Start-Sleep -Seconds 2
            }
            if (-not $terminated) { Write-Phase "tier '$tier' still had running pods 90s after scale-0. Aborting." 'Red'; exit 60 }
            Write-Phase "  tier '$tier' fully terminated (0 running pods)." 'Gray'
        }

        # DWELL — single shared dwell for the whole crashed set (TEST-07 both down together).
        Write-Phase "  dwell $($scenario.dwellSeconds)s (tier(s) down)..."
        Start-Sleep -Seconds $scenario.dwellSeconds

        # STEP F.3 — RESTORE: scale each tier back to its Phase-80 count (NEVER a blanket 1 — SPEC req 3).
        foreach ($tier in $scenario.targetContainers) {
            $kind = $TierKind[$tier]; $rep = $TierReplicas[$tier]
            Write-Phase "STEP F.3: restoring tier '$tier' — kubectl -n skp scale $kind/$tier --replicas=$rep"
            kubectl -n skp scale "$kind/$tier" --replicas=$rep | Out-Null
            if ($LASTEXITCODE -ne 0) { Write-Phase "scale '$tier' back to $rep failed (exit $LASTEXITCODE)." 'Red'; exit 60 }
        }
        # STEP F.4 — READINESS GATE (SPEC req 4): block until all replicas Ready BEFORE pinning RECOVERY_UTC.
        # rollout status works uniformly for Deployments AND StatefulSets (D-05 discretion — simplest choice).
        foreach ($tier in $scenario.targetContainers) {
            $kind = $TierKind[$tier]
            Write-Phase "STEP F.4: waiting for tier '$tier' ($kind) all replicas Ready (bounded 120s)"
            kubectl -n skp rollout status "$kind/$tier" --timeout=120s
            if ($LASTEXITCODE -ne 0) { Write-Phase "tier '$tier' did not become Ready within 120s after restore. Aborting." 'Red'; exit 60 }
            Write-Phase "  tier '$tier' Ready again." 'Gray'
        }
        # Pin RECOVERY_UTC ONLY now — after every crashed tier passed terminate + Ready gates (SPEC req 4).
        $recoveryUtc = [DateTimeOffset]::UtcNow
        Write-Phase "  RECOVERY_UTC = $($recoveryUtc.ToString('o')) (all crashed tiers Ready)." 'Gray'
    }

    # -----------------------------------------------------------------------
    # STEP F.5 — HOLD OUT THE 5-MIN WINDOW, then record windowEnd. For TEST-01 this is the whole
    # post-activation wait. Optional early close when K_EXECUTIONS > 0; the wall-clock bound is the hard
    # cap (the loop can never run past the 300s window regardless of K).
    # -----------------------------------------------------------------------
    while (([DateTimeOffset]::UtcNow - $windowStart).TotalSeconds -lt $windowSeconds) {
        if ($kExecutions -gt 0) {
            $observedExecutions = (Get-FireCount) - $fireBaseline
            if ($observedExecutions -ge $kExecutions) {
                Write-Phase "  OPTIONAL execution-based window: observed $observedExecutions >= K=$kExecutions distinct executions — closing window early." 'Gray'
                break
            }
        }
        Start-Sleep -Seconds 5
    }
    $windowCloseUtc = [DateTimeOffset]::UtcNow
    Write-Phase "STEP F: observation window closed at $($windowCloseUtc.ToString('o')) ($([int](($windowCloseUtc - $windowStart).TotalSeconds))s)"

    # -----------------------------------------------------------------------
    # STEP F.6 — STOP THE WORKFLOW + DRAIN THE RESULT PIPELINE TO QUIESCENCE, then pin windowEnd
    # POST-DRAIN. MG-1 result conservation (orchestrator_messages_consumed == processor_messages_sent) is
    # a windowed delta the analyzer reads TIME-PINNED to windowEnd, so windowEnd MUST be a SETTLED state.
    # Break on GAP CONVERGENCE (|consumed - sent| <= 1) for a binding scenario with no real loss; a
    # reporting-only residual gap that never converges is broken out of once both counters are flat past
    # one ~60s export interval. The 180s deadline bounds both paths. (Verbatim shape from phase-67 F.6.)
    # -----------------------------------------------------------------------
    Write-Phase "STEP F.6: stop workflow + drain result pipeline to quiescence (conservation settle)"
    $stopBody = ConvertTo-Json @($wfId)
    try { Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/stop' `
            -ContentType 'application/json' -Body $stopBody -TimeoutSec 15 -ErrorAction Stop | Out-Null }
    catch { Write-Phase "  stop POST best-effort failed: $($_.Exception.Message)" 'Yellow' }
    $prevC = -1; $prevS = -1; $polls = 0; $drainDeadline = (Get-Date).AddSeconds(180)
    do {
        Start-Sleep -Seconds 20
        $polls++
        $c = Get-PromSum 'orchestrator_messages_consumed_total'
        $s = Get-PromSum 'processor_messages_sent_total'
        $gap = [math]::Abs($c - $s)
        Write-Phase "  drain: orch_consumed=$c proc_sent=$s gap=$gap" 'Gray'
        if ($gap -le 1) { break }                                              # converged (binding scenarios)
        if ($c -eq $prevC -and $s -eq $prevS -and $polls -ge 5) { break }      # real flat past one export interval
        $prevC = $c; $prevS = $s
    } while ((Get-Date) -lt $drainDeadline)
    $windowEnd = [DateTimeOffset]::UtcNow
    Write-Phase "  drained (orch_consumed=$c, proc_sent=$s, gap=$([math]::Abs($c - $s))); windowEnd pinned post-drain at $($windowEnd.ToString('o'))." 'Gray'

    # -----------------------------------------------------------------------
    # STEP H — ANALYZE (VERDICT — do NOT remap to an infra code) — VERBATIM reuse (localhost:9090/9200
    # via port-forward). Set the D-16 env seam (SCENARIO_ID / WINDOW_*_UTC), invoke the analyzer via the
    # MTP-native filter, then resolve the authoritative verdict class from the report JSON. The analyzer's
    # exit IS the harness verdict. The env seam is cleared in a `finally`.
    # -----------------------------------------------------------------------
    Write-Phase "STEP H: analyze (dotnet test ~Analyzer) for scenario $scenarioId"
    $env:SCENARIO_ID      = $scenarioId
    $env:WINDOW_START_UTC = $windowStart.ToString('o')
    $env:WINDOW_END_UTC   = $windowEnd.ToString('o')
    # RECOVERY_UTC: '' for the no-fault baseline (TEST-01) — parses as no-recovery (all incompletes binding).
    $env:RECOVERY_UTC     = if ($recoveryUtc) { $recoveryUtc.ToString('o') } else { '' }
    # OPTIONAL K_EXECUTIONS seam, set + cleared symmetrically. Empty when the execution-based window is off.
    $env:K_EXECUTIONS     = if ($kExecutions -gt 0) { $kExecutions.ToString() } else { '' }
    try {
        dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Release -- --filter-method "*Analyze_Window_Yields_Pass*" 2>&1 | Out-String | Write-Host
        $analyzerExit = $LASTEXITCODE
    } finally {
        # Clear the D-16 env seam so a terminating error inside the analyze block cannot leak the seam
        # into the parent shell (matters when dot-sourced / run interactively; harmless for `pwsh -File`).
        Remove-Item Env:SCENARIO_ID, Env:WINDOW_START_UTC, Env:WINDOW_END_UTC, Env:RECOVERY_UTC, Env:K_EXECUTIONS -ErrorAction SilentlyContinue
    }

    # Locate + echo the analyzer report path, then resolve the AUTHORITATIVE verdict class from the JSON
    # artifact (written BEFORE the fixture's assert, so its Verdict is authoritative even when a non-Pass
    # verdict made $LASTEXITCODE mirror 1). Resolve-AnalyzerExitCode maps Inconclusive->2, Fail->1, Pass->0.
    $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$scenarioId.json" -ErrorAction SilentlyContinue |
              Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
    if ($report) {
        Write-Phase "analyzer report: $($report.FullName)" 'Green'
        try {
            $reportObj = Get-Content $report.FullName -Raw | ConvertFrom-Json
            $resolved = Resolve-AnalyzerExitCode $reportObj
            if ($resolved -ne $analyzerExit) {
                Write-Phase "  report Verdict='$($reportObj.Verdict)' resolves exit $resolved (overrides mirrored dotnet-test exit $analyzerExit)." 'Gray'
            }
            $analyzerExit = $resolved
        } catch {
            Write-Phase "  WARNING: could not parse analyzer report for exit-code resolution: $($_.Exception.Message)" 'Yellow'
        }
    }
    else { Write-Phase "WARNING: analyzer-reports/$scenarioId.json not found" 'Yellow' }
    $verdictClass = (Resolve-SweepClass $analyzerExit).Class
    Write-Phase "analyzer verdict exit = $analyzerExit ($verdictClass; 0=PASS 1=FAIL 2=INCONCLUSIVE)" $(if ($analyzerExit -eq 0) { 'Green' } else { 'Yellow' })

    # -----------------------------------------------------------------------
    # STEP Z — TEARDOWN (NON-FATAL). Stop the port-forward background processes recorded by
    # phase-80-up.ps1 in .k8s-portforward-pids. By DEFAULT keep the k8s stack + PVCs for inspection
    # (NO `kubectl delete`); the -TearDownCluster switch runs `kubectl delete -k k8s/`. A teardown
    # failure logs loud but the harness STILL surfaces the analyzer verdict (the FINAL exit mirrors the
    # analyzer, never the teardown result).
    # -----------------------------------------------------------------------
    # -SkipBringUp (SPEC req 7 / D-03): when the sweep owns the shared stack, leave the port-forwards
    # UP so the next scenario can reuse them — the sweep tears them down once at the very end.
    if (-not $SkipBringUp) {
        Write-Phase "STEP Z: teardown — stop the kubectl port-forwards (keep the stack + PVCs)"
        $pidFile = Join-Path $repoRoot '.k8s-portforward-pids'
        if (Test-Path $pidFile) {
            $pfPids = @(Get-Content $pidFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '\S' })
            foreach ($p in $pfPids) {
                # Recycled-PID guard: only kill if the PID is STILL a live kubectl process. If the forward
                # already exited and the OS recycled its PID, this skips it rather than force-killing an
                # unrelated process. Best-effort — never throws.
                try {
                    $proc = Get-Process -Id ([int]$p) -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'kubectl' }
                    if ($proc) { Stop-Process -Id ([int]$p) -Force -ErrorAction SilentlyContinue }
                } catch { }
            }
            Write-Phase "  stopped $($pfPids.Count) port-forward process(es) (PIDs: $($pfPids -join ', '))." 'Gray'
            Remove-Item $pidFile -ErrorAction SilentlyContinue
        } else {
            Write-Phase "  no .k8s-portforward-pids file found — nothing to stop (forwards may already be down)." 'Yellow'
        }
    } else {
        Write-Phase "STEP Z: -SkipBringUp — leaving port-forwards up for the sweep." 'Gray'
    }
    if ($TearDownCluster) {
        Write-Phase "STEP Z: -TearDownCluster set — kubectl delete -k k8s/ (dropping the stack + PVCs)" 'Yellow'
        kubectl delete -k k8s/ 2>&1 | Out-String | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Phase "kubectl delete -k k8s/ failed — surfacing analyzer verdict regardless." 'Yellow' }
    } else {
        Write-Phase "  k8s stack + PVCs KEPT (pass -TearDownCluster to delete)." 'Gray'
    }

    exit $analyzerExit

} finally {
    Pop-Location
}
