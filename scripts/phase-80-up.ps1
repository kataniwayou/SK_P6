# Phase 80 k8s bring-up — deploy the full system to local Kubernetes (Docker Desktop)
# Parallels scripts/phase-65-up.ps1 (the compose bring-up analog) — D-15.
# ---------------------------------------------------------------------------
# The single command that turns the k8s/ manifests into a running, harness-reachable stack:
#
#   1. Pitfall-5 guard: `docker compose down` so the compose stack does not hold the host ports
#      (8080/9090/9200/15673/6380/5433/5673/4317) the port-forwards need — compose vs k8s are
#      mutually exclusive on those loopback ports (threat T-80-18).
#   2. `kubectl apply -k k8s/` — creates ns skp, secret, configmaps, and every workload.
#   3. Phased Ready wait via `kubectl -n skp rollout status` — infra tiers then app tiers.
#      rollout status is replica-aware (keeper/processor-sample ×2 handled automatically), so it
#      REPLACES phase-65-up.ps1's hand-rolled NDJSON-per-replica parse entirely (RESEARCH Pattern 4).
#   4. IMAGE CURRENCY (Pitfall 2 / D-05): `kubectl rollout restart` the 4 app Deployments so the
#      freshly built `:local` bits actually run — same tag + IfNotPresent ⇒ no auto-redeploy on rebuild.
#   5. (Task 2) Start the 8 loopback-bound port-forwards + poll baseapi /health/ready before returning.
#
# PRECONDITION: STEP-A0 build (scripts/phase-80-build.ps1) MUST have run BEFORE this script so the
# rollout-restart lands pods on the CURRENT SourceHash (D-05) — the container assembly hash must match
# the seeder's host-built Processor.Sample.dll. Do NOT re-implement the NDJSON per-replica poll —
# `kubectl rollout status` replaces it.
# ---------------------------------------------------------------------------

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')   # repo root — kubectl apply -k k8s/ is repo-relative

function Write-Phase {
    param([string]$Message, [string]$Color = 'Cyan')
    Write-Host "[phase-80-up] $Message" -ForegroundColor $Color
}

    # ---- STEP 1: Pitfall-5 guard — tear the compose stack DOWN so it releases the forward ports ----
    # compose publishes 8080/9090/9200/15673/6380/5433/5673/4317 on the host; a live compose stack
    # would collide with the k8s port-forwards below (threat T-80-18). `down` is idempotent — a no-op
    # when nothing is up. Non-fatal if compose isn't installed/running (guarded).
    Write-Phase "STEP 1: docker compose down (Pitfall-5 host-port collision guard)..."
    docker compose down 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Phase "docker compose down returned $LASTEXITCODE (continuing — likely already down)." 'Yellow'
    }

    # ---- STEP 2: Assert the kube context is docker-desktop (warn loudly, do not hard-fail) ----
    $ctx = (kubectl config current-context 2>$null | Out-String).Trim()
    if ($ctx -ne 'docker-desktop') {
        Write-Phase "kube context is '$ctx', expected 'docker-desktop'. Verify you are NOT pointed at a real cluster before continuing." 'Yellow'
    } else {
        Write-Phase "kube context = docker-desktop (OK)."
    }

    # ---- STEP 3: Apply the manifests (kustomize) — fail loud on non-zero (exit 10) ----
    Write-Phase "STEP 3: kubectl apply -k k8s/ ..."
    kubectl apply -k k8s/ 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Phase "kubectl apply -k k8s/ failed (exit $LASTEXITCODE). Aborting." 'Red'
        exit 10
    }

    # ---- STEP 4: Phased INFRA Ready wait — rollout status is readiness-gated + replica-aware ----
    # Per-tier --timeout budgets absorb the cold starts (ES 240s, rabbitmq 180s — Pattern 4 / Pitfall 3).
    Write-Phase "STEP 4: waiting for INFRA tiers Ready (rollout status)..."
    $infra = @(
        @{ target = 'statefulset/postgres';      timeout = '180s' },
        @{ target = 'statefulset/redis';         timeout = '120s' },
        @{ target = 'statefulset/rabbitmq';      timeout = '180s' },   # 40s cold start
        @{ target = 'statefulset/elasticsearch'; timeout = '240s' },   # 60s cold start
        @{ target = 'deployment/otel-collector'; timeout = '120s' },
        @{ target = 'deployment/prometheus';     timeout = '120s' }
    )
    foreach ($t in $infra) {
        Write-Phase "  rollout status $($t.target) (--timeout=$($t.timeout))..."
        kubectl -n skp rollout status $t.target --timeout=$($t.timeout)
        if ($LASTEXITCODE -ne 0) {
            Write-Phase "$($t.target) did not become Ready within $($t.timeout) (exit $LASTEXITCODE). Aborting." 'Red'
            exit 10
        }
    }
    Write-Phase "INFRA tiers Ready." 'Green'

    # ---- STEP 5: IMAGE CURRENCY (Pitfall 2 / D-05) — force the 4 app Deployments onto fresh :local bits ----
    # `:local` + imagePullPolicy IfNotPresent means the SAME tag never triggers a redeploy after a rebuild.
    # A single `rollout restart` of all 4 app Deployments recreates their pods against the just-built images,
    # guaranteeing the running SourceHash == the seeder's host-built Processor.Sample.dll (threat T-80-19).
    Write-Phase "STEP 5: kubectl rollout restart the 4 app Deployments (image/SourceHash currency)..."
    kubectl -n skp rollout restart deployment/baseapi-service deployment/orchestrator deployment/keeper deployment/processor-sample
    if ($LASTEXITCODE -ne 0) {
        Write-Phase "rollout restart of the app Deployments failed (exit $LASTEXITCODE). Aborting." 'Red'
        exit 10
    }

    # ---- STEP 6: Phased APP Ready wait — rollout status waits for the RESTARTED pods to be Available ----
    # keeper/processor-sample are replicas:2 — rollout status requires ALL replicas Available automatically.
    Write-Phase "STEP 6: waiting for APP tiers Ready after restart (rollout status)..."
    $apps = @(
        @{ target = 'deployment/baseapi-service';  timeout = '180s' },
        @{ target = 'deployment/orchestrator';     timeout = '120s' },
        @{ target = 'deployment/keeper';           timeout = '120s' },
        @{ target = 'deployment/processor-sample'; timeout = '120s' }
    )
    foreach ($t in $apps) {
        Write-Phase "  rollout status $($t.target) (--timeout=$($t.timeout))..."
        kubectl -n skp rollout status $t.target --timeout=$($t.timeout)
        if ($LASTEXITCODE -ne 0) {
            Write-Phase "$($t.target) did not become Ready within $($t.timeout) (exit $LASTEXITCODE). Aborting." 'Red'
            exit 10
        }
    }
    Write-Phase "All 10 tiers Ready (6 infra + 4 app), app pods on fresh :local bits." 'Green'

# ---- STEP 7: Start the EIGHT loopback port-forwards (D-14 + seeder finding) ----------------------
# `kubectl port-forward` is a blocking foreground process — run each as a tracked BACKGROUND process
# (Start-Process -PassThru -WindowStyle Hidden, RESEARCH Pattern 5) so the script can return to the
# harness with the forwards live.
#
# EVERY forward binds --address 127.0.0.1 explicitly — never 0.0.0.0 — so the bridged host ports stay
# loopback-only (threat T-80-17: a 0.0.0.0 bind would expose the cluster to the LAN). Services stay
# ClusterIP (no NodePort/ingress); port-forward is the ONLY host->cluster ingress.
#
# The forward set maps each harness-hard-coded localhost port to its Service's container port. D-14
# enumerated SIX (baseapi/prometheus/elasticsearch/rabbitmq-mgmt/redis/postgres). The ~FanOutSeeder
# proof runs an in-proc WebApi (RealStackWebAppFactory, SampleRoundTripE2ETests.cs L417-458) that ALSO
# hard-codes RabbitMq__Port 5673 (AMQP) and OTEL_EXPORTER_OTLP_ENDPOINT http://localhost:4317 — so we
# add the rabbitmq AMQP 5673->5672 and otel 4317->4317 forwards. This COMPLETES D-14's stated rationale
# ("expose the ports the harness hard-codes") — the seeder IS part of the harness (planner finding).
$forwards = @(
    @{ svc = 'svc/baseapi-service'; map = '8080:8080'   },   # WebApi — the only HTTP tier the harness talks to
    @{ svc = 'svc/prometheus';      map = '9090:9090'   },   # analyzer counter scrape
    @{ svc = 'svc/elasticsearch';   map = '9200:9200'   },   # analyzer log queries
    @{ svc = 'svc/rabbitmq';        map = '15673:15672' },   # mgmt UI — harness Get-ProcQueueDepth / mgmt reads
    @{ svc = 'svc/redis';           map = '6380:6379'   },   # reset FLUSHALL / seeder L2 (localhost:6380)
    @{ svc = 'svc/postgres';        map = '5433:5432'   },   # reset/seed SQL (localhost:5433)
    @{ svc = 'svc/rabbitmq';        map = '5673:5672'   },   # AMQP — REQUIRED by the ~FanOutSeeder in-proc WebApi (RabbitMq__Port 5673)
    @{ svc = 'svc/otel-collector';  map = '4317:4317'   }    # OTLP — REQUIRED by the ~FanOutSeeder in-proc WebApi OTEL endpoint
)

Write-Phase "STEP 7: starting $($forwards.Count) kubectl port-forwards (all bound --address 127.0.0.1)..."
$pfProcs = foreach ($f in $forwards) {
    Write-Phase "  port-forward $($f.svc) $($f.map) --address 127.0.0.1"
    Start-Process kubectl -PassThru -WindowStyle Hidden `
        -ArgumentList @('port-forward', $f.svc, $f.map, '-n', 'skp', '--address', '127.0.0.1')
}

# Persist the PIDs so plan 80-10's harness teardown can stop the forwards cleanly.
$pidFile = Join-Path (Get-Location) '.k8s-portforward-pids'
$pfProcs | ForEach-Object { $_.Id } | Set-Content -Path $pidFile -Encoding ascii
Write-Phase "port-forward PIDs ($(($pfProcs | ForEach-Object { $_.Id }) -join ', ')) written to $pidFile"
Write-Phase "TEARDOWN HINT: Get-Content '$pidFile' | ForEach-Object { Stop-Process -Id \$_ -Force -ErrorAction SilentlyContinue }" 'Yellow'

# ---- STEP 8: Bounded baseapi readiness poll — confirms the forward is live before hand-off ----------
# A port-forward reports success on start even before the tunnel carries traffic (Pattern 5 reliability
# caveat), so gate on a REAL 200 from baseapi /health/ready before returning. This gives reset/seed/POST
# a clean hand-off. Bounded ~60s; fail loud (exit 11) on timeout.
Write-Phase "STEP 8: polling http://localhost:8080/health/ready (bounded ~60s)..."
$deadline = (Get-Date).AddSeconds(60)
$ready = $false
do {
    try {
        $resp = Invoke-WebRequest -Uri 'http://localhost:8080/health/ready' -UseBasicParsing -TimeoutSec 5
        if ($resp.StatusCode -eq 200) { $ready = $true }
    } catch {
        # forward not carrying traffic yet / app not ready — retry until the deadline
    }
    if (-not $ready) {
        if ((Get-Date) -ge $deadline) {
            Write-Phase "baseapi /health/ready did not return 200 within 60s. The stack is up but not harness-reachable. Aborting." 'Red'
            exit 11
        }
        Start-Sleep -Seconds 2
    }
} while (-not $ready)

Write-Phase "baseapi /health/ready == 200 — stack is UP and harness-reachable. Hand off to reset/seed/POST/sweep." 'Green'
exit 0
