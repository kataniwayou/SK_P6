# Offline Setup & Deploy Runbook (SK_P6)

Build the sources, build the Docker images, and deploy the full stack to Kubernetes on a
**disconnected** Windows machine that has **Visual Studio** and **Docker Desktop** (Kubernetes
enabled, Linux containers). Every command is copy-paste. Run them in order.

The repo already carries everything needed for an offline restore:
- `nugets/` — 134 `.nupkg` (the complete transitive closure, Windows + Linux)
- `NuGet.config` — nuget.org removed; restores from `nugets/` only
- `packages.lock.json` (per project) — pins the exact package set
- Rewired `Dockerfiles` — offline restore, no `# syntax=` frontend, no `apt-get`

The out-of-band **`offline-bundle/`** (NOT in git — ship it alongside the repo) carries:
- `offline-bundle/sdk/dotnet-sdk-8.0.423-win-x64.exe` — the pinned .NET SDK
- `offline-bundle/images/images.tar` — all base + infra + testcontainer images (~1.5 GB)
- `offline-bundle/images/IMAGES.txt` — the image manifest

---

## 0. One-time prerequisites (offline machine)

```powershell
# Confirm Docker Desktop is running with Kubernetes enabled and Linux containers:
docker version
kubectl config use-context docker-desktop
kubectl get nodes            # expect one Ready node

# Skip online certificate-revocation checks during NuGet restore (offline):
setx NUGET_CERT_REVOCATION_MODE offline
# (open a NEW terminal after setx so it takes effect, or for the current session:)
$env:NUGET_CERT_REVOCATION_MODE = "offline"
```

## 1. Install the pinned .NET SDK (8.0.423)

`global.json` requires an 8.0.4xx SDK. Install the bundled one (idempotent — skips if already present):

```powershell
& "offline-bundle\sdk\dotnet-sdk-8.0.423-win-x64.exe" /install /quiet /norestart
dotnet --list-sdks          # expect an 8.0.4xx entry
```

## 2. Load all Docker images (base + infra + testcontainers)

```powershell
docker load -i offline-bundle\images\images.tar
docker images                # verify sdk:8.0-bookworm-slim, aspnet:8.0-bookworm-slim, postgres:17-alpine, etc.
```

## 3. Compile all sources (offline, from `nugets/`)

```powershell
# From the repo root:
dotnet restore SK_P.sln --locked-mode      # restores from nugets/ only; fails loudly if anything is missing
dotnet build   SK_P.sln -c Release         # expect: 0 Warning(s), 0 Error(s)
```

In **Visual Studio**: open `SK_P.sln`, then Build ▸ Rebuild Solution. It uses the same `NuGet.config`
+ `nugets/` feed automatically (no online restore).

## 4. Build the 4 app Docker images (offline)

Each build restores from the copied `nugets/` inside the container — no registry access.

```powershell
docker build -t baseapi-service:local  -f Dockerfile .
docker build -t orchestrator:local     -f src/Orchestrator/Dockerfile .
docker build -t keeper:local           -f src/Keeper/Dockerfile .
docker build -t processor-sample:local -f src/Processor.Sample/Dockerfile .
```

> The build context is the repo root for every image (the Dockerfiles `COPY nugets/`). The
> `.dockerignore` excludes `offline-bundle/`, so the multi-GB bundle is never sent to the daemon.

## 5. Tear down any existing deployment (including the namespace)

```powershell
kubectl delete namespace skp --ignore-not-found
kubectl wait --for=delete namespace/skp --timeout=120s   # wait for full teardown
```

## 6. Deploy the full stack to Kubernetes

```powershell
kubectl apply -k k8s/
# Wait for the infra tiers, then the app tiers:
kubectl -n skp rollout status statefulset/postgres    --timeout=180s
kubectl -n skp rollout status statefulset/redis       --timeout=120s
kubectl -n skp rollout status statefulset/rabbitmq    --timeout=180s
kubectl -n skp rollout status statefulset/elasticsearch --timeout=240s
kubectl -n skp rollout status deployment/otel-collector --timeout=120s
kubectl -n skp rollout status deployment/prometheus     --timeout=120s
kubectl -n skp rollout status deployment/baseapi-service --timeout=180s
kubectl -n skp rollout status deployment/orchestrator    --timeout=180s   # replicas: 3
kubectl -n skp rollout status deployment/keeper          --timeout=180s
kubectl -n skp rollout status deployment/processor-sample --timeout=180s
```

## 7. Verify

```powershell
kubectl -n skp get pods                    # all Running / Ready
kubectl -n skp get deploy orchestrator     # 3/3 READY
```

---

## Notes & caveats

- **Rebuild-then-redeploy on Docker Desktop k8s:** the first deploy of a `:local` tag imports fine.
  But rebuilding the SAME `:local` tag and re-deploying can serve the STALE cached image (containerd
  imports a docker-built image into the `k8s.io` namespace only on first deploy). If you change code
  and rebuild, deploy under a unique tag instead:
  ```powershell
  docker tag orchestrator:local orchestrator:local-2
  kubectl -n skp set image deployment/orchestrator orchestrator=orchestrator:local-2
  ```
- **Health checks:** Kubernetes uses `httpGet` readiness/liveness probes (no in-container tool). The
  Compose stack (optional) uses the app's own dotnet self-probe: `dotnet <App>.dll --healthcheck`.
- **Tests (optional):** the RealStack test suite uses Testcontainers; `testcontainers/ryuk:0.11.0`
  and `postgres:17-alpine` are in `images.tar`. If ryuk misbehaves offline, set
  `TESTCONTAINERS_RYUK_DISABLED=true`. Hermetic tests need no Docker:
  `dotnet test SK_P.sln` (or run `BaseApi.Tests.exe --filter-not-trait Category=RealStack`).
- **SourceHash currency:** `dotnet build` prints `SourceHash (<project>): <hash>` — it must equal the
  DB `processors.source_hash` for the processor liveness gate; reseed if it diverges after a code change.
