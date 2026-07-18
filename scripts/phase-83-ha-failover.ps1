<#
.SYNOPSIS
    Phase 83 HA-failover proof harness (HA-07 — the leader-kill sequencer + verdict driver).

.DESCRIPTION
    The operator-plane sequencer that stages the LIVE proof of orchestrator HA failover (HA-07).
    It reuses the Phase-80 k8s STEP bring-up primitives VERBATIM (build+up, fresh-DB bootstrap,
    reset, orchestrator clean rollout-restart, seed FanOut, resolve wfId, POST /start require 204),
    then inserts the NEW phase-aligned LEADER-KILL sequencer (STEP F) and drives the Plan-02 verdict
    fact (STEP H), mapping its report through Resolve-AnalyzerExitCode to exit 0/1/2:

        pwsh -File scripts/phase-83-ha-failover.ps1

    Flow:

        STEP A0  phase-80-build.ps1                  build the 4 app images :local (SourceHash currency)
        STEP A   phase-80-up.ps1                     kubectl apply -k + rollout + port-forwards + readiness
        STEP A2  fresh-DB processor-row bootstrap     seed once to register the processor row on a fresh PVC
        STEP B   phase-80-reset.ps1                   kubectl-exec FLUSHALL + heal-wait + graph DELETE
        STEP B1  kubectl rollout restart orchestrator drop ghost Quartz crons AND force a fresh election
        STEP C   dotnet test ~FanOutSeeder            seed v8-fanout-proof (cron */30)
        STEP D   kubectl exec psql                    resolve the v8-fanout-proof workflow id
        STEP E   POST /orchestration/start            activation gate — REQUIRE HTTP 204
        STEP F   LEADER-KILL SEQUENCER (NEW)          read Lease holder, wait >=1 clean fire, phase-align,
                                                      force-delete the leader, hold, pin the window + KILL_UTC
        STEP H   dotnet test ~HaFailoverAnalyzer      the D-04 verdict (env seam; Resolve-AnalyzerExitCode 0/1/2)
        STEP Z   stop port-forwards                   teardown in the outer finally (keep the stack + PVCs)

    The phase-aligned kill (STEP F) resolves the Nyquist gap-coverage problem: a randomly-timed kill
    lands a cron tick inside the ~11-17s election gap only ~40-57% of the time, so claim #3 (gap-skip)
    would be silently unproven. Killing ~3s before a :00/:30 cron boundary makes the skipped tick
    deterministic WITHOUT changing the locked */30 cron.

    Final harness exit == the HA verdict (0 = PASS, non-0 = FAIL/INCONCLUSIVE). Each infra step fails
    loud with a DISTINCT non-zero code so an infra abort is never mistaken for a verdict.

    EXIT-CODE TABLE (mirrors phase-80-harness.ps1; the 0/1/2 classes are owned by exit-code-resolution.ps1):
        0   HA verdict PASS         (Resolve-AnalyzerExitCode Pass)
        1   HA verdict FAIL         (Resolve-AnalyzerExitCode Fail / unknown fail-closed)
        2   HA verdict INCONCLUSIVE (Resolve-AnalyzerExitCode Inconclusive - e.g. gap tick never landed)
        10  bring-up failed
        20  reset failed
        25  orchestrator clean rollout-restart failed
        30  seeder / fresh-DB bootstrap failed
        40  wf-id lookup failed/empty
        45  Lease holderIdentity read failed/empty   (NEW)
        50  activation gate != 204
        55  force-delete of leader pod failed        (NEW)
        60  pre-kill clean-fire never observed       (NEW)

    MTP FILTER NOTE: tests/BaseApi.Tests is xunit.v3 under Microsoft.Testing.Platform, which SILENTLY
    IGNORES `dotnet test --filter` (VSTest syntax). This harness MUST use the MTP-native filter passed
    AFTER `--`: `-- --filter-method "*FanOutSeeder*"` (seed) and
    `-- --filter-method "*Ha_Failover_Window_Yields_Pass*"` (verdict).

.PARAMETER SkipBringUp
    When set, STEP A0 (build) + STEP A (up + port-forwards) + the STEP Z port-forward teardown are
    skipped — for reusing an already-up stack. STEP A2 (fresh-DB bootstrap) is NOT gated: its warm-DB
    guard self-skips.

.PARAMETER TearDownCluster
    When set, STEP Z ALSO runs `kubectl delete -k k8s/` after stopping the port-forwards. Default OFF —
    the stack + PVCs are kept for inspection between runs.

.NOTES
    Dev/ops-only tooling. No product source touched. Fully automated — no interactive prompt anywhere.
    Kills ONLY the Lease holderIdentity pod read from the k8s API (D-02a); replicas stay at 3.
#>

param(
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
        Write-Host "[phase-83-ha-failover] $msg" -ForegroundColor $color
    }

    # -----------------------------------------------------------------------
    # dot-source the shared exit-code resolution lib (pure functions, no side effects).
    # Resolve-AnalyzerExitCode maps the HA verdict report Verdict -> 0/1/2 (STEP H); Resolve-SweepClass
    # maps the resolved code -> a human-readable class for the final trace line.
    # -----------------------------------------------------------------------
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')

    # -----------------------------------------------------------------------
    # STEP A0 — IMAGE BUILD (code 10) — SourceHash currency guarantee.
    # phase-80-build.ps1 builds the 4 app images :local so the container assembly-embedded SourceHash
    # matches the seeder's host-built Processor.Sample.dll. A stale image 422s the POST /start. Building
    # here (and rollout-restarting in STEP A) guarantees currency.
    # -SkipBringUp: when reusing an already-up stack, STEP A0 (build) + STEP A (up + port-forwards) are
    # skipped. STEP A2 below is deliberately NOT gated — its warm-DB guard self-skips on a warm DB but
    # MUST still bootstrap the processor row on the first (fresh-PVC) run.
    # -----------------------------------------------------------------------
    if (-not $SkipBringUp) {
        Write-Phase "STEP A0: build the 4 app images :local (SourceHash currency — phase-80-build.ps1)"
        pwsh -File (Join-Path $PSScriptRoot 'phase-80-build.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Phase "image build failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }

        # -------------------------------------------------------------------
        # STEP A — BRING-UP (code 10).
        # phase-80-up.ps1: compose-down (port-collision guard) -> kubectl apply -k k8s/ -> phased rollout
        # status (6 infra + 4 app) -> rollout restart the 4 app Deployments onto fresh :local bits -> start
        # the 8 loopback port-forwards -> poll baseapi /health/ready. On return the stack is UP and
        # harness-reachable over localhost.
        # -------------------------------------------------------------------
        Write-Phase "STEP A: bring-up (phase-80-up.ps1 — kubectl apply -k + rollout + port-forwards + readiness gate)"
        pwsh -File (Join-Path $PSScriptRoot 'phase-80-up.ps1')
        if ($LASTEXITCODE -ne 0) { Write-Phase "bring-up failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }
    } else {
        Write-Phase "STEP A0/A: -SkipBringUp — reusing the already-up stack + port-forwards." 'Gray'
    }

    # -----------------------------------------------------------------------
    # STEP A2 — FIRST-RUN BOOTSTRAP (code 30) — fresh-DB processor-row registration.
    # On a FRESH k8s PVC the `processors` table is empty, so no processor row exists for the current
    # SourceHash. The processor boots-before-register: its liveness watchdog stays "not started" until the
    # row exists — so the STEP B heal-wait can NEVER converge on a fresh cluster. Bootstrap-seed ONCE here
    # (the seeder GET-or-creates the processor row + seeds a throwaway workflow the STEP B graph-DELETE
    # wipes), then wait for per-instance liveness to converge so STEP B has live processors to observe.
    # GUARDED to the fresh-DB case: a warm re-run skips straight to the proven reset->seed->start path.
    # -----------------------------------------------------------------------
    Write-Phase "STEP A2: first-run bootstrap check (processors table empty => seed once to register the row)"
    $procCountRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA `
                    -c "SELECT count(*) FROM processors"
    if ($LASTEXITCODE -ne 0) { Write-Phase "processor-row precheck failed (psql exit $LASTEXITCODE). Aborting." 'Red'; exit 30 }
    # Normalize AFTER the exit-code guard — on a failed exec stdout is empty, so trimming the raw capture
    # (never $null once cast to string) can no longer throw a terminating error and bypass the exit 30.
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
    # STEP B — RESET (code 20).
    # phase-80-reset.ps1: kubectl-exec FLUSHALL + 60s heal-wait + FK-safe graph DELETE (processors +
    # config_schemas preserved). Stack stays UP.
    # -----------------------------------------------------------------------
    Write-Phase "STEP B: reset (phase-80-reset.ps1 — kubectl-exec FLUSHALL + heal-wait + graph DELETE)"
    pwsh -File (Join-Path $PSScriptRoot 'phase-80-reset.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Phase "reset failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 20 }

    # -----------------------------------------------------------------------
    # STEP B1 — CLEAN-ORCHESTRATOR GUARANTEE (code 25) — CRITICAL FOR HA.
    # Recreate the orchestrator pods AFTER the reset (empty L2 parent index + clean DB) and BEFORE
    # seed+start so their in-process Quartz RAMJobStores are wiped of all ghost crons AND a FRESH election
    # runs so the leader is deterministic before STEP F. On boot HydrationBackgroundService SMEMBERS the
    # (now empty) parent index and schedules nothing; `rollout status` blocks until the readiness-gated
    # fresh pods are Available. ONLY the freshly seeded+started v8-fanout-proof workflow fires after this.
    # -----------------------------------------------------------------------
    Write-Phase "STEP B1: clean orchestrator (kubectl rollout restart deployment/orchestrator) — drop ghost Quartz crons + fresh election"
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
    # STEP D — WF-ID (code 40). Static-literal psql, no interpolated input (threat T-80-20).
    # `kubectl -n skp exec statefulset/postgres -- psql`. Pin $LASTEXITCODE BEFORE the (safe) trim.
    # -----------------------------------------------------------------------
    Write-Phase "STEP D: resolve v8-fanout-proof workflow id (kubectl exec psql)"
    $wfIdRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA `
              -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"
    # Pin the exit code BEFORE the trim: on a failed exec stdout is empty, so `.Trim()` on the raw capture
    # (string-cast, never $null) cannot throw and bypass exit 40.
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

    # STEP F: TODO Task 2 — phase-aligned leader-kill sequencer

    # STEP H: TODO Task 2 — verdict driver (exit 0/1/2)

} finally {
    # -----------------------------------------------------------------------
    # STEP Z — TEARDOWN (NON-FATAL, outer finally). Stop the port-forward background processes recorded by
    # phase-80-up.ps1 in .k8s-portforward-pids. By DEFAULT keep the k8s stack + PVCs for inspection (NO
    # `kubectl delete`); the -TearDownCluster switch runs `kubectl delete -k k8s/`. Running in the outer
    # finally guarantees port-forward teardown even when STEP F/H aborts with a distinct infra code.
    # -SkipBringUp: when the caller owns the shared stack, leave the port-forwards up.
    # -----------------------------------------------------------------------
    if (-not $SkipBringUp) {
        Write-Phase "STEP Z: teardown — stop the kubectl port-forwards (keep the stack + PVCs)"
        $pidFile = Join-Path $repoRoot '.k8s-portforward-pids'
        if (Test-Path $pidFile) {
            $pfPids = @(Get-Content $pidFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '\S' })
            foreach ($p in $pfPids) {
                # Recycled-PID guard: only kill if the PID is STILL a live kubectl process. Best-effort — never throws.
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
        Write-Phase "STEP Z: -SkipBringUp — leaving port-forwards up for the shared stack." 'Gray'
    }
    if ($TearDownCluster) {
        Write-Phase "STEP Z: -TearDownCluster set — kubectl delete -k k8s/ (dropping the stack + PVCs)" 'Yellow'
        kubectl delete -k k8s/ 2>&1 | Out-String | Write-Host
        if ($LASTEXITCODE -ne 0) { Write-Phase "kubectl delete -k k8s/ failed — surfacing the HA verdict regardless." 'Yellow' }
    } else {
        Write-Phase "  k8s stack + PVCs KEPT (pass -TearDownCluster to delete)." 'Gray'
    }
    Pop-Location
}
