<#
.SYNOPSIS
    Phase 80 image builder — the k8s analog of the compose STEP-A0 build.

.DESCRIPTION
    Replaces the compose bring-up's single `docker compose build` (phase-67-harness.ps1
    STEP A0) with FOUR explicit `docker build -t <name>:local` invocations so the
    Docker Desktop kubelet — which shares the same local image store the host daemon
    writes — can run them with `imagePullPolicy: IfNotPresent` and NO registry pull
    (D-04). The `:local` tag (never `:latest`) avoids Always-pull semantics.

    SourceHash currency (D-05): building the images here BEFORE the reset+seed runs
    guarantees the container's assembly-embedded SourceHash equals the seeder's
    host-built `Processor.Sample.dll` SourceHash. A stale image → different processor
    id → `ProcessorLivenessValidator` 422s the `POST /orchestration/start`. Because
    the `:local` tag never changes, the bring-up script (phase-80-up.ps1) must
    `kubectl rollout restart` after this build to force pods onto the fresh bits.

    Build context = repo root for every image (the Dockerfiles COPY `src/` from root).

    Fail-loud contract (ported from STEP A0): $ErrorActionPreference = 'Stop' plus an
    explicit `$LASTEXITCODE -ne 0` check after each build → `exit 10`, so an image
    build failure aborts the whole bring-up rather than deploying a stale/missing image.

    Usage:
        pwsh -File scripts/phase-80-build.ps1
#>

$ErrorActionPreference = 'Stop'

# Repo root = parent of scripts/. All docker build contexts are the repo root.
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
    Write-Host "[phase-80-build] building 4 app images :local (SourceHash currency, D-04/D-05)" -ForegroundColor Cyan
    Write-Host "[phase-80-build] context = $repoRoot" -ForegroundColor Cyan

    # --- baseapi-service (webapi) — root Dockerfile ---
    docker build -t baseapi-service:local  -f Dockerfile .
    if ($LASTEXITCODE -ne 0) { Write-Host "build baseapi-service failed" -ForegroundColor Red; exit 10 }

    # --- orchestrator (console) ---
    docker build -t orchestrator:local     -f src/Orchestrator/Dockerfile .
    if ($LASTEXITCODE -ne 0) { Write-Host "build orchestrator failed" -ForegroundColor Red; exit 10 }

    # --- keeper (console) ---
    docker build -t keeper:local           -f src/Keeper/Dockerfile .
    if ($LASTEXITCODE -ne 0) { Write-Host "build keeper failed" -ForegroundColor Red; exit 10 }

    # --- processor-sample (console; carries the assembly-embedded SourceHash) ---
    docker build -t processor-sample:local -f src/Processor.Sample/Dockerfile .
    if ($LASTEXITCODE -ne 0) { Write-Host "build processor-sample failed" -ForegroundColor Red; exit 10 }

    # NOTE: src/Processor.BadConfig/Dockerfile EXISTS but is intentionally NOT built (D-04).
    # It is the profile-gated badconfig image — OUT of the default k8s proof, mirroring
    # compose's `profiles: ["badconfig"]` default exclusion. Do not add a 5th build here.

    Write-Host "[phase-80-build] all 4 app images built :local — OK" -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}
