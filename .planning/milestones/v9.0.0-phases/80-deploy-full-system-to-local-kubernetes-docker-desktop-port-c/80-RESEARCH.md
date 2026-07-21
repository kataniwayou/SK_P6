# Phase 80: Deploy full system to local Kubernetes (Docker Desktop) — Research

**Researched:** 2026-07-18
**Domain:** Kubernetes manifest authoring (Docker Desktop single-node) — compose→k8s topology port
**Confidence:** HIGH (k8s mechanics verified against current docs + the repo's own source-of-truth files)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-16 — INVARIANTS, do NOT re-decide)
- **D-01:** Raw kubectl YAML manifests, one file per tier, under a new `k8s/` dir at repo root. NOT Helm, NOT Kustomize overlays.
- **D-02:** Everything into namespace **`skp`**.
- **D-03:** Optionally a single `kustomization.yaml` that ONLY lists resources + stamps namespace/common labels (no templating) for ordered `kubectl apply -k k8s/`. Convenience aggregator, not an overlay.
- **D-04:** Build the 5 app images locally with `docker build` using an explicit **`:local` tag** (NOT `:latest`); set **`imagePullPolicy: IfNotPresent`** on all app Deployments. No registry. App images: `Dockerfile` (baseapi-service), `src/Orchestrator/Dockerfile`, `src/Keeper/Dockerfile`, `src/Processor.Sample/Dockerfile`. Processor.BadConfig EXCLUDED.
- **D-05:** Preserve build-before-seed ordering (compose STEP A0). Build all app images BEFORE reset+seed so the container's assembly-embedded SourceHash equals the seeder's host-built `Processor.Sample.dll` SourceHash. Stale image → different processor id → `ProcessorLivenessValidator` 422s the `POST /start`.
- **D-06:** `compose/otel-collector-config.yaml` + `prometheus.yml` → ConfigMaps, mounted as files at the same in-container paths.
- **D-07:** Postgres creds + RabbitMQ guest/guest → a committed **dev-only** Kubernetes Secret; connection strings reference via `secretKeyRef`.
- **D-08:** The 7 test-only env seams omitted entirely (absent ⇒ `:-0`/`:-900` default ⇒ OFF). Load-bearing production defaults carried as explicit literals: `Processor__ExecutionDataTtl: "900"`, `Orchestrator__OutputDataTtlSeconds: "900"`, `Recovery__ExecutionDataTtlSeconds: "900"`.
- **D-09:** postgres → StatefulSet + PVC (~1Gi).
- **D-10:** elasticsearch → StatefulSet + PVC (~2Gi). Deliberate divergence from compose's ephemeral ES (protects run-evidence).
- **D-11:** rabbitmq → StatefulSet + PVC (~1Gi). Deliberate divergence from compose's ephemeral broker.
- **D-12:** redis → StatefulSet WITH NO PVC (ephemeral by design; carry `--save "" --appendonly no`).
- **D-13:** prometheus + otel-collector → Deployments, ephemeral (no PVC).
- **D-14:** Reuse the existing host PowerShell harness. Expose k8s services via `kubectl port-forward` at the exact localhost ports the harness hard-codes: WebApi `8080`, Prometheus `9090`, ES `9200`, RabbitMQ-mgmt `15673`, redis `6380`→6379, postgres `5433`→5432. Services stay **ClusterIP**.
- **D-15:** New deliverable = a k8s bring-up script paralleling `phase-65-up.ps1` (apply → wait Ready → start port-forwards). Then the proven chain runs: reset → `~FanOutSeeder` → `POST /start` 204 → `phase-68-sweep.ps1`.
- **D-16:** Proof scope = **happy-path only**. Bar: full stack Healthy on Docker Desktop k8s AND one workflow round-trips (seed → 204 → sweep PASS from Prometheus + ES). No fault re-run.

### Claude's Discretion
- Exact probe wiring per tier (httpGet on health ports: baseapi 8080, orchestrator 8081, processor-sample 8082, keeper 8083, prometheus 9090 `/-/healthy`; postgres/redis/rabbitmq via exec probes matching compose commands). otel-collector: no in-container HTTP client (distroless) → TCP-socket probe on `:4317`, or httpGet on `:13133` (health_check extension), or treat running=ready.
- Probe timing translated from compose `start_period`/`interval`/`retries` — ES ~60s cold-start, rabbitmq ~40s.
- PVC sizes (~1–2Gi are starting points), storageClass (default Docker Desktop `hostpath`), resource requests/limits, label/selector conventions.
- File naming/grouping within `k8s/`, structure of bring-up + build scripts.
- Whether ES/Prometheus/otel config env lives inline or in a ConfigMap — carry compose values verbatim.

### Deferred Ideas (OUT OF SCOPE)
- Fault-scenario re-run on k8s (7 scenarios). Already proven on compose v8.0.0.
- Registry-based image distribution / multi-node cluster.
- Overlay/Helm packaging for multiple environments.
</user_constraints>

## Summary

This is a **packaging port**, not a design task: every architectural decision is locked (D-01..D-16). The research below supplies the concrete k8s syntax, translation arithmetic, and Docker-Desktop-specific landmines so the planner can turn the 11-service `compose.yaml` into correct manifests + a bring-up script on the first try.

Three findings dominate risk and must shape the plan:

1. **"Reuse the harness unchanged" (D-14/D-15) is only partially literal.** `phase-65-reset.ps1`, and harness STEPs B1/D, reach Postgres/Redis via **`docker exec sk-redis`, `docker compose exec postgres`, `docker compose ps`, and `docker compose restart orchestrator`** — NOT via the localhost TCP ports. Port-forward exposes TCP (localhost:6380→redis, 5433→postgres), which supports a *TCP client* (`redis-cli -h localhost -p 6380`, `psql -h localhost -p 5433`), but the scripts don't use TCP clients for those steps — they use the docker/compose CLI, which cannot see k8s pods. The seeder + analyzer (`dotnet test`) *do* use localhost ports and port-forward covers them. **The plan must translate the docker/compose-CLI touch-points to `kubectl exec` / `kubectl rollout restart` (either by parameterizing the scripts or writing thin k8s siblings).** This is the single largest planning landmine — see Pitfall 1.

2. **`:local` + `IfNotPresent` needs an explicit rollout-restart after every rebuild.** Because the tag never changes, a rebuilt image does NOT redeploy pods automatically (unlike compose `--force-recreate`). The build step must be followed by `kubectl rollout restart deployment/<app>` to force pods onto the freshly built image — this is what preserves SourceHash currency (D-05). See Pitfall 2.

3. **k8s has no `depends_on`.** The bring-up script must phase the apply: infra (StatefulSets + Services + ConfigMaps + Secret) → `kubectl rollout status` → app Deployments → `kubectl rollout status`. Apps self-heal via restart + readiness probes, but ordering the apply avoids CrashLoopBackOff noise and matches the compose `service_healthy` gating intent.

**Primary recommendation:** Author 3 StatefulSets (postgres+PVC, elasticsearch+PVC, rabbitmq+PVC) + 1 StatefulSet no-PVC (redis), each with a **headless Service** (`clusterIP: None`) named identically to the compose service; 6 Deployments (baseapi-service, orchestrator, keeper×2, processor-sample×2, otel-collector, prometheus) with ClusterIP Services only where inbound access is needed (baseapi-service, otel-collector, prometheus); 2 ConfigMaps, 1 dev-only Secret; use `httpGet`/`exec`/`tcpSocket` probes translated per the arithmetic table below; and a `phase-80-up.ps1` bring-up + `phase-80-build.ps1` (5 `docker build`) pair. Model compose `start_period` with k8s **`startupProbe`** for ES and rabbitmq.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Workflow orchestration / Quartz cron | orchestrator Deployment (×1, SPOF) | — | In-memory L1 pause/resume + RAMJobStore; must stay ×1 (locked). |
| Fault recovery (two-consumer reinject) | keeper Deployment (×2) | rabbitmq (shared durable queue) | Both replicas round-robin one queue; no source change. |
| Step execution | processor-sample Deployment (×2) | rabbitmq, redis, baseapi (identity) | Consumers; no inbound Service needed. |
| HTTP control plane (start/stop, health, identity responder) | baseapi-service Deployment (×1) | postgres (L3), redis (L2), rabbitmq | Only tier the harness talks HTTP to (localhost:8080). |
| L3 persistence | postgres StatefulSet + PVC | — | Stable identity + durable graph state. |
| L2 cache / liveness / execution data | redis StatefulSet (no PVC) | — | Rebuildable from L3; persistence off by design (D-12). |
| Message broker | rabbitmq StatefulSet + PVC | — | Durable queues survive reschedule (D-11). |
| Log sink / metrics scrape source | elasticsearch StatefulSet + PVC; otel-collector Deployment; prometheus Deployment | — | Telemetry chain OTLP→collector→ES + Prom scrape. |
| Host verification surface | kubectl port-forward (localhost ports) | — | Bridges ClusterIP services to the harness's hard-coded ports (D-14). |

## Standard Stack

### Core (no libraries — this phase is manifests + scripts against tooling already installed)
| Tool | Version to verify | Purpose | Why Standard |
|------|-------------------|---------|--------------|
| `kubectl` | ships with Docker Desktop | apply manifests, wait/rollout status, port-forward, exec | The only client needed for raw manifests (D-01). |
| Docker Desktop Kubernetes | single-node, built-in | the target cluster | Locked target; shares local image store with the docker daemon. |
| `kustomize` (built into `kubectl -k`) | built-in | ordered multi-file apply + namespace/label stamping (D-03) | `kubectl apply -k` needs no separate binary. |

**Verify before planning (run once):**
```bash
kubectl version --client            # confirm kubectl present
kubectl config current-context      # MUST be "docker-desktop"
kubectl get storageclass            # confirm default is "hostpath" (docker.io/hostpath)
docker images | grep ':local'       # after build step — all 5 app images present
```

**No npm/pip packages.** The only "installs" are the 5 locally-built app images (D-04) and, if the reset/wf-id steps are translated to host TCP clients rather than `kubectl exec`, a host `psql` + `redis-cli` on PATH (see Pitfall 1 for the recommended `kubectl exec` alternative that avoids new host deps).

### Alternatives Considered
| Instead of | Could Use | Tradeoff / Why rejected |
|------------|-----------|-------------------------|
| Headless Service per backing SS | Regular ClusterIP Service | ClusterIP works for a single-replica backing service, but a StatefulSet's `serviceName` MUST reference a headless (`clusterIP: None`) Service to get stable pod DNS. Use headless — it doubles as the app-facing name (`redis`, `postgres`, …). |
| `kubectl port-forward` | NodePort Services | D-14 locks port-forward (zero URL edits). NodePort would force 3xxxx remapping + harness edits and is explicitly rejected. |
| `startupProbe` for ES/rabbitmq cold start | huge `initialDelaySeconds` on readiness | Both work; `startupProbe` is the modern, precise analog of compose `start_period` (disables liveness/readiness until it passes). Recommended. |
| `kubectl exec` for reset/wf-id | host `psql`/`redis-cli` on forwarded ports | `kubectl exec` needs no new host deps and mirrors the current `docker exec` shape 1:1. Recommended over adding host clients. |

## Architecture Patterns

### System Architecture Diagram (data flow — k8s topology in namespace `skp`)

```
  HOST (Windows PowerShell harness)
    │  builds 5 images (docker build -t <name>:local)  ── shared with kubelet ──┐
    │                                                                            │
    │  kubectl apply -k k8s/  ┐                                                  │
    │  kubectl rollout status │  (phase-80-up.ps1)                              │
    │  kubectl port-forward   │                                                  ▼
    │    localhost:8080 ─────────────────────────► svc/baseapi-service:8080 ─► [baseapi-service pod] ─┐
    │    localhost:9090 ─────────────────────────► svc/prometheus:9090      ─► [prometheus pod]       │
    │    localhost:9200 ─────────────────────────► svc/elasticsearch:9200   ─► [elasticsearch-0]      │
    │    localhost:15673 ────────────────────────► svc/rabbitmq:15672       ─► [rabbitmq-0]           │
    │    localhost:6380  ────────────────────────► svc/redis:6379           ─► [redis-0]              │
    │    localhost:5433  ────────────────────────► svc/postgres:5432        ─► [postgres-0]           │
    │                                                                                                  │
    │  seed (dotnet ~FanOutSeeder) → localhost:5433 (postgres) ─────────────────────────────────────┘
    │  POST /start → localhost:8080 (204)
    │  sweep/analyzer → localhost:9090 (Prom) + localhost:9200 (ES)
    │  reset / wf-id / clean-orchestrator → kubectl exec / kubectl rollout restart  (NOT docker exec)
    │
    ▼  IN-CLUSTER data flow (bare short-name Service DNS within skp):
       orchestrator ──amqp──► rabbitmq ◄──amqp── keeper ×2
            │                     ▲                  │
            └── redis:6379 ◄──────┼──────────────────┘   (L2 liveness / execution data)
       processor-sample ×2 ──amqp──► rabbitmq ; ──http──► baseapi (identity) ; ──redis──► redis
       every app ──OTLP grpc──► otel-collector:4317 ──logs──► elasticsearch:9200
                                        └──/metrics :8889 ◄──scrape── prometheus:9090
       baseapi ──► postgres:5432 (L3)
```

A reader can trace the proof: build → apply → Ready → port-forward → seed (host→5433→postgres) → POST 204 (host→8080→baseapi) → workflow fires through rabbitmq/redis/processors → telemetry to collector → ES + Prom → sweep reads localhost:9090/:9200.

### Recommended `k8s/` structure (D-01 one-file-per-tier, D-03 aggregator)
```
k8s/
├── kustomization.yaml            # D-03: namespace: skp + commonLabels + ordered resource list
├── 00-namespace.yaml             # Namespace skp
├── 01-secret.yaml                # dev-only Secret (postgres + rabbitmq creds) — labeled DEV ONLY
├── 02-configmaps.yaml            # otel-collector-config + prometheus.yml ConfigMaps
├── 10-postgres.yaml              # StatefulSet + PVC + headless Service
├── 11-redis.yaml                 # StatefulSet (no PVC) + headless Service
├── 12-rabbitmq.yaml              # StatefulSet + PVC + headless Service
├── 13-elasticsearch.yaml         # StatefulSet + PVC + headless Service
├── 20-otel-collector.yaml        # Deployment + ClusterIP Service
├── 21-prometheus.yaml            # Deployment + ClusterIP Service
├── 30-baseapi-service.yaml       # Deployment + ClusterIP Service (8080)
├── 31-orchestrator.yaml          # Deployment (no Service — no inbound)
├── 32-keeper.yaml                # Deployment replicas:2 (no Service)
└── 33-processor-sample.yaml      # Deployment replicas:2 (no Service)
```
The numeric prefixes are documentation only — `kubectl apply -k` sorts by *kind* (Namespace, Secret, ConfigMap, Service, then workloads), not filename, so apply-order gating between infra and apps must be done by the **bring-up script**, not by file order (see Pattern 4).

### Services matrix (which tiers need a Service, and what kind)
| Tier | Service? | Kind | Ports | serviceName (SS) |
|------|----------|------|-------|------------------|
| postgres | yes | headless (`clusterIP: None`) | 5432 | `postgres` |
| redis | yes | headless | 6379 | `redis` |
| rabbitmq | yes | headless | 5672, 15672 | `rabbitmq` |
| elasticsearch | yes | headless | 9200 (9300 not needed) | `elasticsearch` |
| otel-collector | yes | ClusterIP | 4317, 4318, 8889, 13133 | — |
| prometheus | yes | ClusterIP | 9090 | — |
| baseapi-service | yes | ClusterIP | 8080 | — |
| orchestrator | **no** | — | — (health 8081 probed pod-local by kubelet) | — |
| keeper | **no** | — | — (health 8083 probed pod-local) | — |
| processor-sample | **no** | — | — (health 8082 probed pod-local) | — |

**Key insight:** orchestrator/keeper/processor are pure *consumers* — nothing connects to them inbound except the kubelet's probes, which hit the pod IP directly (no Service required). This trims the manifest surface. Only backing services + the two things the harness/collector reach by name (baseapi HTTP, otel-collector, prometheus) need Services.

### Pattern 1: StatefulSet + headless Service (postgres, with PVC)
**What:** A StatefulSet gives stable pod identity (`postgres-0`) and, paired with a headless Service of the same name, stable DNS. `volumeClaimTemplates` provisions a per-pod PVC.
**When:** postgres, elasticsearch, rabbitmq (all + PVC); redis (same shape, **omit** `volumeClaimTemplates`).
```yaml
# Source: kubernetes.io/docs/concepts/workloads/controllers/statefulset
apiVersion: v1
kind: Service
metadata: { name: postgres, namespace: skp }
spec:
  clusterIP: None            # headless — required for StatefulSet stable DNS
  selector: { app: postgres }
  ports: [{ port: 5432, targetPort: 5432 }]
---
apiVersion: apps/v1
kind: StatefulSet
metadata: { name: postgres, namespace: skp }
spec:
  serviceName: postgres      # MUST match the headless Service name
  replicas: 1
  selector: { matchLabels: { app: postgres } }
  template:
    metadata: { labels: { app: postgres } }
    spec:
      containers:
        - name: postgres
          image: postgres:17-alpine
          env:
            - { name: POSTGRES_DB,       valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: POSTGRES_DB } } }
            - { name: POSTGRES_USER,     valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: POSTGRES_USER } } }
            - { name: POSTGRES_PASSWORD, valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: POSTGRES_PASSWORD } } }
          ports: [{ containerPort: 5432 }]
          volumeMounts: [{ name: data, mountPath: /var/lib/postgresql/data }]
          readinessProbe:
            exec: { command: ["pg_isready", "-U", "postgres", "-d", "stepsdb"] }
            initialDelaySeconds: 5
            periodSeconds: 5
            timeoutSeconds: 5
            failureThreshold: 10
  volumeClaimTemplates:
    - metadata: { name: data }
      spec:
        accessModes: ["ReadWriteOnce"]
        resources: { requests: { storage: 1Gi } }
        # storageClassName omitted → Docker Desktop default "hostpath"
```
- App-facing DNS: `postgres:5432` resolves via the headless Service to `postgres-0`'s IP (byte-for-byte port of the compose connection string).
- PVC created is named `data-postgres-0`. It **survives pod reschedule** but NOT a Kubernetes reset in Docker Desktop settings (Pitfall 8).
- redis (D-12): identical shape, **no `volumeClaimTemplates`**, and `command: ["redis-server", "--save", "", "--appendonly", "no"]`.

### Pattern 2: Deployment + ClusterIP Service (app tier / collector / prometheus)
**What:** Stateless replicas behind a load-balanced ClusterIP. `replicas: 2` trivially ports compose `deploy.replicas: 2` (keeper, processor-sample).
```yaml
# Source: kubernetes.io/docs/concepts/workloads/controllers/deployment
apiVersion: apps/v1
kind: Deployment
metadata: { name: keeper, namespace: skp }
spec:
  replicas: 2
  selector: { matchLabels: { app: keeper } }
  template:
    metadata: { labels: { app: keeper } }
    spec:
      containers:
        - name: keeper
          image: keeper:local
          imagePullPolicy: IfNotPresent      # D-04 — local image, never pull
          env:
            - { name: RabbitMq__Host, value: rabbitmq }
            - { name: RabbitMq__Username, valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: RABBITMQ_DEFAULT_USER } } }
            - { name: RabbitMq__Password, valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: RABBITMQ_DEFAULT_PASS } } }
            - { name: ConnectionStrings__Redis, value: "redis:6379,abortConnect=false,connectTimeout=5000" }
            - { name: OTEL_EXPORTER_OTLP_ENDPOINT, value: "http://otel-collector:4317" }
            - { name: Recovery__ExecutionDataTtlSeconds, value: "900" }   # D-08 explicit literal
          # NO KEEPER_DEFEAT_REINJECT / KEEPER_REINJECT_DELAY_MS / KEEPER_RECOVERY_TTL — omitted ⇒ default-off (D-08)
          readinessProbe:
            httpGet: { path: /health/ready, port: 8083 }
            initialDelaySeconds: 30
            periodSeconds: 10
            timeoutSeconds: 3
            failureThreshold: 5
          livenessProbe:
            tcpSocket: { port: 8083 }         # process-alive, decoupled from broker readiness
            initialDelaySeconds: 45
            periodSeconds: 15
            failureThreshold: 6
```
- **httpGet probes are run by the kubelet from outside the container** — so the compose `wget --spider` in-container hack is unnecessary; k8s does not need `wget` in the image (it's harmless if present). This is a *cleaner* port than compose.
- keeper/processor/orchestrator are HARD-on-broker for `/health/ready`. Use `/health/ready` for **readiness** (gates Ready) but a **`tcpSocket` liveness** (Kestrel binds at process start, before the broker) so a slow rabbitmq (40s start) never triggers a liveness kill / CrashLoop. See Pitfall 3.

### Pattern 3: Secret-composed connection string via `$(VAR)` dependent env
**What:** baseapi's `ConnectionStrings__Postgres` embeds the password mid-string. k8s supports `$(VAR)` interpolation referencing env vars **defined earlier in the same container's env list**, so the compose interpolation ports exactly:
```yaml
# Source: kubernetes.io/docs/tasks/inject-data-application/define-interdependent-environment-variables
env:
  - { name: POSTGRES_DB,       valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: POSTGRES_DB } } }
  - { name: POSTGRES_USER,     valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: POSTGRES_USER } } }
  - { name: POSTGRES_PASSWORD, valueFrom: { secretKeyRef: { name: skp-dev-secrets, key: POSTGRES_PASSWORD } } }
  - name: ConnectionStrings__Postgres
    value: "Host=postgres;Port=5432;Database=$(POSTGRES_DB);Username=$(POSTGRES_USER);Password=$(POSTGRES_PASSWORD)"
```
The three source vars MUST appear before the composed var in the list. This is the byte-for-byte analog of compose `${POSTGRES_PASSWORD}` interpolation. (A literal `$(VAR)` where `VAR` is undefined is left unexpanded — verify the ordering.)

### Pattern 4: Phased bring-up (no `depends_on`) — `phase-80-up.ps1`
**What:** k8s has no `depends_on`. Apps self-heal (restartPolicy Always + readiness), but to mirror compose `service_healthy` gating and avoid CrashLoop noise, phase the apply.
```
1. (build step already ran: 5x docker build -t <name>:local)  # D-04/D-05 build-before-anything
2. kubectl apply -k k8s/                                        # creates ns, secret, configmaps, all workloads
   # OR split: kubectl apply -f 00..13 (infra) ; wait ; kubectl apply -f 20..33 (apps)
3. Wait infra Ready (StatefulSets + collector + prometheus):
     kubectl -n skp rollout status statefulset/postgres      --timeout=180s
     kubectl -n skp rollout status statefulset/redis         --timeout=120s
     kubectl -n skp rollout status statefulset/rabbitmq      --timeout=180s   # 40s cold start
     kubectl -n skp rollout status statefulset/elasticsearch --timeout=240s   # 60s cold start
     kubectl -n skp rollout status deployment/otel-collector --timeout=120s
     kubectl -n skp rollout status deployment/prometheus     --timeout=120s
4. Wait app tiers Ready (rollout status is replica-aware — keeper/processor ×2 handled automatically):
     kubectl -n skp rollout status deployment/baseapi-service  --timeout=180s
     kubectl -n skp rollout status deployment/orchestrator     --timeout=120s
     kubectl -n skp rollout status deployment/keeper           --timeout=120s
     kubectl -n skp rollout status deployment/processor-sample --timeout=120s
5. Start the 6 port-forwards as background processes (Pattern 5), record PIDs for teardown.
```
`kubectl rollout status` returns only when the desired number of replicas are **Available** (readiness-passing), so it *replaces* phase-65-up.ps1's per-replica NDJSON parse cleanly, including the ×2 tiers. **otel-collector no longer needs a running=ready special case** if it gets an httpGet readiness probe on :13133 (Pattern 6).

### Pattern 5: Multiple `kubectl port-forward` from PowerShell (lifecycle)
**What:** `kubectl port-forward` is a blocking foreground process; run 6 as tracked background processes.
```powershell
# Source: kubernetes.io/docs/tasks/access-application-cluster/port-forward-access-application-in-a-cluster
$forwards = @(
  @{ svc='svc/baseapi-service'; map='8080:8080'  },
  @{ svc='svc/prometheus';      map='9090:9090'  },
  @{ svc='svc/elasticsearch';   map='9200:9200'  },
  @{ svc='svc/rabbitmq';        map='15673:15672'},
  @{ svc='svc/redis';           map='6380:6379'  },
  @{ svc='svc/postgres';        map='5433:5432'  }
)
$pfProcs = foreach ($f in $forwards) {
  Start-Process kubectl -PassThru -WindowStyle Hidden `
    -ArgumentList @('port-forward', $f.svc, $f.map, '-n', 'skp', '--address', '127.0.0.1')
}
# teardown: $pfProcs | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
```
- port-forward binds `127.0.0.1` by default → matches the harness `localhost` URLs.
- Each forward targets a Service; for single-replica backing services it lands on the one pod. For baseapi (×1) fine.
- **Reliability caveat:** a port-forward dies if its target pod restarts and does not auto-reconnect. The happy path (D-16) has no crashes, so risk is low — but harness STEP B1 does `restart orchestrator` (no forward on orchestrator → unaffected). Add a short readiness retry loop after starting forwards (e.g. poll `http://localhost:8080/health/ready` until 200) before handing off to reset/seed.

### Pattern 6: otel-collector probe (distroless — httpGet, not exec)
The distroless "no shell/wget/curl" constraint only blocks **exec** probes (which run a binary *inside* the container). **httpGet and tcpSocket probes are executed by the kubelet from outside the container**, so they work on distroless images. The collector already runs the `health_check` extension serving HTTP `/` on `:13133`:
```yaml
readinessProbe:
  httpGet: { path: /, port: 13133 }
  initialDelaySeconds: 5
  periodSeconds: 10
  failureThreshold: 5
```
This is strictly better than the compose "no healthcheck, treat running=ready" workaround and lets `kubectl rollout status` be meaningful for the collector. (Alternative if 13133 is not wired: `tcpSocket: { port: 4317 }`.)

### Pattern 7: ConfigMap file-mount via subPath
```yaml
# otel-collector Deployment
volumeMounts:
  - name: otel-config
    mountPath: /etc/otel-collector-config.yaml
    subPath: otel-collector-config.yaml     # single-file mount into a system dir
volumes:
  - name: otel-config
    configMap: { name: otel-collector-config }
# prometheus: mountPath /etc/prometheus/prometheus.yml, subPath prometheus.yml
```
- **Use `subPath`** — mounting the whole ConfigMap as a directory at `/etc/prometheus` would hide the image's console libs; at `/etc/otel-collector-config.yaml` you need a single *file* not a dir. subPath is the correct primitive.
- **Caveat (documented, acceptable here):** subPath mounts do **not** receive live ConfigMap updates — a `kubectl edit configmap` won't propagate without a pod restart. Fine for this phase (static config; bring-up restarts pods anyway).
- Build the ConfigMaps either by hand (paste file contents under `data:`) or, cleaner, via kustomize `configMapGenerator` (keeps them in sync with the repo files):
```yaml
# kustomization.yaml (D-03)
namespace: skp
configMapGenerator:
  - name: otel-collector-config
    files: [../compose/otel-collector-config.yaml]
  - name: prometheus-config
    files: [../prometheus.yml]
generatorOptions: { disableNameSuffixHash: true }   # stable name so manifests can reference it
resources: [00-namespace.yaml, 01-secret.yaml, 10-postgres.yaml, ...]
```
`disableNameSuffixHash: true` is important — otherwise kustomize appends a content hash to the ConfigMap name and the Deployment's `configMap.name` reference breaks.

### Pattern 8: dev-only Secret (stringData)
```yaml
# Source: kubernetes.io/docs/concepts/configuration/secret
apiVersion: v1
kind: Secret
metadata:
  name: skp-dev-secrets
  namespace: skp
  labels: { app.kubernetes.io/part-of: skp, sensitivity: dev-only }
type: Opaque
stringData:                 # human-readable; k8s base64-encodes on write (no manual base64)
  POSTGRES_DB: stepsdb
  POSTGRES_USER: postgres
  POSTGRES_PASSWORD: postgres        # DEV ONLY — mirrors committed .env; NEVER a real cluster
  RABBITMQ_DEFAULT_USER: guest
  RABBITMQ_DEFAULT_PASS: guest
```
Use `stringData` (not `data`) so the values stay readable/diff-able against `.env` — k8s base64-encodes them automatically. Add a bold `# DEV ONLY` header comment (D-07 rationale).

### Anti-Patterns to Avoid
- **`image: <name>:latest` for app images** — auto-sets `imagePullPolicy: Always` → k8s tries to pull from a registry that doesn't exist → `ImagePullBackOff`. Use `:local` + `IfNotPresent` (D-04).
- **Rebuilding an image and expecting pods to update** — same `:local` tag ⇒ no redeploy. Must `kubectl rollout restart` (Pitfall 2).
- **Whole-directory ConfigMap mount at `/etc/prometheus`** — hides image files. Use subPath.
- **`kustomize` name-suffix hash on ConfigMaps referenced by name** — breaks the volume reference. `disableNameSuffixHash: true`.
- **Using `docker exec` / `docker compose` in the k8s harness path** — cannot see k8s pods (Pitfall 1).
- **A single tight probe used for both liveness and readiness on HARD-on-broker apps** — CrashLoops the orchestrator/keeper while rabbitmq (40s) starts. Decouple (Pitfall 3).
- **`hostPath` volumes hand-rolled instead of the default `hostpath` StorageClass PVC** — the default dynamic provisioner already does this; don't manually pin host paths.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Wait-for-all-replicas-healthy | Reimplement phase-65-up's NDJSON parse in kubectl | `kubectl rollout status statefulset/… deployment/…` | Replica-aware, readiness-gated, built-in; handles ×2 tiers automatically. |
| start_period cold-start budget | Huge sleeps in the bring-up script | `startupProbe` (failureThreshold × periodSeconds) | Native; disables liveness/readiness until app is up (ES/rabbitmq). |
| Local image distribution | `docker save`/`load`, private registry | Docker Desktop shared image store + `IfNotPresent` | kubelet reads the daemon's store directly (D-04). |
| Composed connection string with secret | Two Secrets or an initContainer templating | `$(VAR)` dependent env expansion | Native k8s env interpolation = compose parity. |
| ConfigMap ↔ repo-file drift | Hand-copy file contents into `data:` | kustomize `configMapGenerator files:` | Single source of truth; regenerates on apply. |
| Per-pod stable storage | Manual PV + hostPath + nodeAffinity | `volumeClaimTemplates` + default `hostpath` SC | Dynamic provisioning on Docker Desktop is automatic. |
| Redis/postgres shell access for reset | New TCP client deps on host | `kubectl exec <pod> -- redis-cli … / psql …` | 1:1 shape swap for the current `docker exec`; no new host deps. |

**Key insight:** Almost every "wait/health/order" concern the compose harness hand-rolled has a first-class k8s primitive (`rollout status`, `startupProbe`, readiness). The port should *delete* hand-rolled logic, not translate it line-by-line.

## Probe Translation Arithmetic (compose healthcheck → k8s probe)

General mapping:
| compose field | k8s probe field | Note |
|---------------|-----------------|------|
| `test: CMD-SHELL "…"` | `exec.command` (or `httpGet`/`tcpSocket` if an HTTP/port check) | httpGet preferred where an HTTP endpoint exists (kubelet-side, no in-container client). |
| `interval` | `periodSeconds` | 1:1. |
| `timeout` | `timeoutSeconds` | 1:1. |
| `retries` | `failureThreshold` | 1:1. |
| `start_period` | `startupProbe` (period × failureThreshold ≥ start_period) OR `initialDelaySeconds` | `startupProbe` is the precise analog; disables readiness/liveness until it passes. |

Per-tier recommended probes:

| Tier | Probe kind | Endpoint / command | period | timeout | failThresh | startup / initialDelay |
|------|-----------|--------------------|--------|---------|-----------|------------------------|
| postgres | readiness `exec` | `pg_isready -U postgres -d stepsdb` | 5 | 5 | 10 | initialDelay 5 |
| redis | readiness `exec` | `redis-cli ping` | 5 | 3 | 10 | initialDelay 5 |
| rabbitmq | startup `exec` + readiness `exec` | `rabbitmq-diagnostics -q ping` | 10 | 5 | startup 6 (=60s) / ready 5 | startupProbe covers 40s cold start |
| elasticsearch | startup `httpGet` + readiness `httpGet` | `/_cluster/health?wait_for_status=yellow&timeout=5s` :9200 | 10 | 5 | startup 12 (=120s) / ready 5 | startupProbe covers 60s cold start; 200 at yellow |
| otel-collector | readiness `httpGet` | `/` :13133 | 10 | — | 5 | initialDelay 5 |
| prometheus | readiness `httpGet` | `/-/healthy` :9090 | 10 | 5 | 3 | initialDelay 10 |
| baseapi-service | readiness `httpGet` + liveness `tcpSocket` | `/health/ready` :8080 ; tcp :8080 | 10 | 3 | 5 | initialDelay 30 |
| orchestrator | readiness `httpGet` + liveness `tcpSocket` | `/health/ready` :8081 ; tcp :8081 | 10 | 3 | 5 | initialDelay 30 (ready) / 45 (live) |
| keeper | readiness `httpGet` + liveness `tcpSocket` | `/health/ready` :8083 ; tcp :8083 | 10 | 3 | 5 | initialDelay 30/45 |
| processor-sample | readiness `httpGet` + liveness `tcpSocket` | `/health/ready` :8082 ; tcp :8082 | 10 | 3 | 5 | initialDelay 30/45 |

- **ES `wait_for_status=yellow`** is essential: single-node ES never reaches green (replicas unassignable) — the readiness must accept yellow, exactly as the compose curl did.
- **ES bootstrap checks are skipped under `discovery.type=single-node`** — this is why `vm.max_map_count` does not block startup here (Pitfall 7). Carry the compose env verbatim: `discovery.type=single-node`, `xpack.security.enabled=false`, `xpack.security.enrollment.enabled=false`, `ES_JAVA_OPTS=-Xms512m -Xmx512m`.
- **Liveness = tcpSocket** on HARD-on-broker apps so a slow broker never kills the pod (Pitfall 3). Confirm during planning whether the apps expose a lightweight `/health/live` (BaseConsole) — if so, prefer that for liveness; otherwise tcpSocket is the safe default.

## Common Pitfalls

### Pitfall 1: The harness reaches Postgres/Redis via `docker exec`, NOT localhost ports
**What goes wrong:** D-14/D-15 say "reuse the harness unchanged via port-forward," but `phase-65-reset.ps1` and harness STEPs B1/D use the docker/compose CLI bound to compose container/service names, which cannot address k8s pods:
- `docker exec sk-redis redis-cli FLUSHALL` (reset STEP 1)
- `docker exec sk-redis redis-cli --scan --pattern 'skp:proc:*'` (reset STEP 2 heal-wait)
- `docker compose exec -T postgres psql -U postgres -d stepsdb -c "…DELETE…"` (reset STEP 3)
- `docker compose ps postgres|processor-sample` (reset STEP 0/4 liveness/count)
- `docker ps --filter name=sk-processor-badconfig` + `docker rm` (reset STEP 4)
- `docker compose restart orchestrator` + `docker compose ps orchestrator` (harness STEP B1 clean-orchestrator)
- `docker compose exec -T postgres psql … SELECT id FROM workflows …` (harness STEP D wf-id)
- `docker compose build` / `up --force-recreate` / `down` (STEP A0/A/Z)

Only the HTTP/metrics touch-points (`localhost:8080` POST, `localhost:9090` Prom, `localhost:9200` ES, `localhost:15673` RMQ mgmt) and the host `dotnet test` seeder/analyzer actually use ports → those are covered by port-forward.
**Why it happens:** the harness was authored for compose; "reuse unchanged" is aspirational for the DB/Redis/lifecycle steps.
**How to avoid:** the plan MUST translate the docker/compose touch-points. Two viable strategies (recommend a thin k8s sibling of reset + a mechanism switch, NOT host TCP clients):
- **`kubectl exec` (recommended — no new host deps, 1:1 shape):**
  - `docker exec sk-redis redis-cli …` → `kubectl -n skp exec statefulset/redis -- redis-cli …` (or `exec redis-0`)
  - `docker compose exec -T postgres psql …` → `kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -c "…"`
  - `docker compose restart orchestrator` → `kubectl -n skp rollout restart deployment/orchestrator` + `kubectl -n skp rollout status deployment/orchestrator --timeout=120s` (preserves the ghost-cron clean-window guarantee — HydrationBackgroundService re-runs on the fresh pod against an empty parent index).
  - `docker compose ps <svc>` liveness/count → `kubectl -n skp get pods -l app=<svc>` or the redis heal-wait `--scan` via `kubectl exec`.
- **Host TCP clients over forwarded ports (alt):** `redis-cli -h localhost -p 6380 …`, `psql -h localhost -p 5433 …` — requires `redis-cli` + `psql` on the host PATH (new deps; matches D-14's port-forward-6380/5433 rationale literally). Less clean.

**Warning signs:** reset aborts immediately (`docker exec sk-redis` → "No such container"); wf-id lookup returns empty; STEP B1 never restarts. **Decide this explicitly in the plan** — it is the difference between a working proof and a first-run failure.

### Pitfall 2: `:local` + `IfNotPresent` does not redeploy on rebuild (SourceHash currency)
**What goes wrong:** rebuilding `processor-sample:local` produces new bits under the same tag; running pods keep the OLD image → stale SourceHash → `ProcessorLivenessValidator` 422s the `POST /start` (D-05). Compose sidesteps this with `up --force-recreate`.
**Why it happens:** k8s only pulls/recreates when the image *reference* changes; with `IfNotPresent` and an unchanged tag it does nothing.
**How to avoid:** after the 5 `docker build -t <name>:local`, the bring-up must force pod recreation:
```
kubectl -n skp rollout restart deployment/processor-sample deployment/baseapi-service \
                                deployment/orchestrator deployment/keeper
kubectl -n skp rollout status  deployment/processor-sample --timeout=180s   # (each)
```
Ordering (D-05): **build all images → apply/rollout-restart → wait Ready → reset → seed**. The seeder's host-built `Processor.Sample.dll` SourceHash must equal the container's (the Dockerfile publishes on Linux; `scripts/verify-sourcehash-reproducible.ps1` already asserts host==docker equality).
**Warning signs:** `POST /orchestration/start` returns 422; `kubectl logs` shows the processor self-registering under a different processor id than the seeded workflow binds.

### Pitfall 3: HARD-on-broker readiness used as liveness → CrashLoopBackOff
**What goes wrong:** orchestrator/keeper/processor `/health/ready` is INVERSE-of-soft (fails until the bus is up). If that same check is the liveness probe with tight timing, k8s kills the pod during the 40s rabbitmq cold start → CrashLoopBackOff → never converges.
**Why it happens:** compose only has a single healthcheck concept; k8s separates liveness (restart) from readiness (traffic gate).
**How to avoid:** readiness = `httpGet /health/ready`; liveness = `tcpSocket` on the health port (green as soon as Kestrel binds, before the broker) with generous `initialDelaySeconds` (≥45) and `failureThreshold` (≥6). Or omit liveness entirely (restartPolicy Always still restarts a truly-crashed process).
**Warning signs:** `kubectl get pods` shows `CrashLoopBackOff` on orchestrator/keeper while rabbitmq is still starting; restart count climbs.

### Pitfall 4: Local image not visible to the kubelet (containerd image-store toggle)
**What goes wrong:** With `IfNotPresent` and a `:local` tag, if the kubelet can't see the daemon-built image it errors `ErrImagePull`/`ImagePullBackOff` (tries a nonexistent registry) — usually because Docker Desktop's *"Use containerd for pulling and storing images"* setting puts built images in a store the k8s node namespace doesn't read, or the build tagged the wrong name.
**Why it happens:** Docker Desktop's k8s normally shares the daemon image store, but the containerd-image-store toggle can split them.
**How to avoid:** verify `docker images | grep :local` shows all 5 exact names; ensure the manifest `image:` matches byte-for-byte; if pods show ImagePullBackOff despite the image existing, either disable the containerd image-store toggle OR build into the shared store. Confirm with `kubectl -n skp describe pod <p>` (Events show the exact image name it looked for).
**Warning signs:** `ImagePullBackOff` / `ErrImagePull` on app pods; describe shows `Failed to pull image "processor-sample:local"`.

### Pitfall 5: Host-port collision between a running compose stack and the k8s port-forwards
**What goes wrong:** port-forward binds localhost 8080/9090/9200/15673/6380/5433. If the compose stack is simultaneously UP (it publishes those exact host ports), the forwards fail to bind ("address already in use") — or worse, the harness talks to the compose stack while thinking it's k8s.
**Why it happens:** both stacks target the same host ports (deliberately, so the harness is portable — D-14).
**How to avoid:** the k8s bring-up must ensure the compose stack is DOWN first (`docker compose down`) — the two stacks are mutually exclusive on the host ports, same spirit as the sk-*/sk2_1 mutual-exclusion convention. Fail loud if a forward can't bind.
**Warning signs:** `Unable to listen on port 8080: bind: address already in use`; sweep passes against stale compose data.

### Pitfall 6: Docker Desktop VM memory / single-node scheduling pressure
**What goes wrong:** 11 pods including ES (512m heap → ~1Gi pod), rabbitmq, postgres on one node. If Docker Desktop's VM memory is too low, ES gets OOMKilled or pods stay `Pending` (Insufficient memory).
**How to avoid:** set memory `requests`/`limits` conservatively (ES limit ~1Gi to hold the 512m heap + overhead; others 128–256Mi requests). Recommend Docker Desktop allocated ≥8GB. Keep requests modest so the scheduler fits all pods on the single node.
**Warning signs:** `kubectl get pods` shows `Pending` with `describe` event "Insufficient memory"; ES pod `OOMKilled` / restarts.

### Pitfall 7: elasticsearch bootstrap check `vm.max_map_count` (mostly moot under single-node)
**What goes wrong:** production ES enforces `vm.max_map_count >= 262144` as a bootstrap check and refuses to start if unmet.
**Why it's mostly moot here:** `discovery.type=single-node` **skips the bootstrap checks**, so ES 8.15 starts on Docker Desktop without tuning `vm.max_map_count` — the compose stack already proves this on the same machine. Carry `discovery.type=single-node` verbatim.
**Fallback if ES still fails to start (mmap errors in logs):** set the WSL2 sysctl once — `wsl -d docker-desktop -u root sysctl -w vm.max_map_count=262144` — or persist via `%USERPROFILE%\.wslconfig` `[wsl2] kernelCommandLine = "sysctl.vm.max_map_count=262144"`. Do NOT add a privileged initContainer for this on Docker Desktop unless needed.
**Warning signs:** ES pod logs `max virtual memory areas vm.max_map_count [65530] is too low`.

### Pitfall 8: PVC binding / persistence lifecycle on Docker Desktop
**What goes wrong:** (a) A PVC may sit `Pending` briefly until its consuming pod schedules (the `hostpath` provisioner binds on first consumer). (b) `hostpath` PVC data survives pod reschedule but **not** a "Reset Kubernetes Cluster" in Docker Desktop settings — an operator expecting durable evidence across a cluster reset will lose it.
**How to avoid:** don't gate the bring-up on PVC `Bound` before the pod exists; `rollout status` on the StatefulSet is the right wait. Document that ES/postgres/rabbitmq PVCs are per-cluster-lifetime. For a truly clean run, the reset harness FLUSHALLs Redis + graph-DELETEs Postgres (data-level), which is orthogonal to PVC lifecycle.
**Warning signs:** PVC `Pending` with no pod; evidence gone after a Docker Desktop k8s reset.

## Code Examples

### Build all 5 app images with `:local` (D-04/D-05) — `phase-80-build.ps1`
```powershell
# Build context is repo root for all (Dockerfiles COPY src/ from root).
$ErrorActionPreference = 'Stop'
docker build -t baseapi-service:local  -f Dockerfile .
docker build -t orchestrator:local     -f src/Orchestrator/Dockerfile .
docker build -t keeper:local           -f src/Keeper/Dockerfile .
docker build -t processor-sample:local -f src/Processor.Sample/Dockerfile .
# Processor.BadConfig intentionally NOT built (D-04 — excluded from the default proof).
```

### Redis StatefulSet, no PVC, command args (D-12)
```yaml
apiVersion: v1
kind: Service
metadata: { name: redis, namespace: skp }
spec:
  clusterIP: None
  selector: { app: redis }
  ports: [{ port: 6379, targetPort: 6379 }]
---
apiVersion: apps/v1
kind: StatefulSet
metadata: { name: redis, namespace: skp }
spec:
  serviceName: redis
  replicas: 1
  selector: { matchLabels: { app: redis } }
  template:
    metadata: { labels: { app: redis } }
    spec:
      containers:
        - name: redis
          image: redis:7.4.9-alpine
          command: ["redis-server", "--save", "", "--appendonly", "no"]   # D-12 verbatim
          ports: [{ containerPort: 6379 }]
          readinessProbe:
            exec: { command: ["redis-cli", "ping"] }
            initialDelaySeconds: 5
            periodSeconds: 5
            timeoutSeconds: 3
            failureThreshold: 10
      # NO volumeClaimTemplates — ephemeral by design (D-12)
```

### elasticsearch StatefulSet + PVC + startupProbe (D-10)
```yaml
apiVersion: apps/v1
kind: StatefulSet
metadata: { name: elasticsearch, namespace: skp }
spec:
  serviceName: elasticsearch
  replicas: 1
  selector: { matchLabels: { app: elasticsearch } }
  template:
    metadata: { labels: { app: elasticsearch } }
    spec:
      containers:
        - name: elasticsearch
          image: docker.elastic.co/elasticsearch/elasticsearch:8.15.5
          env:
            - { name: discovery.type, value: single-node }
            - { name: xpack.security.enabled, value: "false" }
            - { name: xpack.security.enrollment.enabled, value: "false" }
            - { name: ES_JAVA_OPTS, value: "-Xms512m -Xmx512m" }
          ports: [{ containerPort: 9200 }]
          volumeMounts: [{ name: data, mountPath: /usr/share/elasticsearch/data }]
          startupProbe:
            httpGet: { path: "/_cluster/health?wait_for_status=yellow&timeout=5s", port: 9200 }
            periodSeconds: 10
            failureThreshold: 12          # ~120s budget for the ~60s cold start
          readinessProbe:
            httpGet: { path: "/_cluster/health?wait_for_status=yellow&timeout=5s", port: 9200 }
            periodSeconds: 10
            timeoutSeconds: 5
            failureThreshold: 5
          resources: { limits: { memory: "1Gi" }, requests: { memory: "700Mi" } }
  volumeClaimTemplates:
    - metadata: { name: data }
      spec: { accessModes: ["ReadWriteOnce"], resources: { requests: { storage: 2Gi } } }
```

## State of the Art

| Old (compose) approach | Current (k8s) approach | Impact |
|------------------------|------------------------|--------|
| `healthcheck` single concept | `readinessProbe` + `livenessProbe` + `startupProbe` (3 separate) | Decouple restart vs traffic-gate vs cold-start; fixes the HARD-on-broker CrashLoop risk. |
| in-container `wget --spider` probe (needed wget baked into image) | `httpGet` probe run kubelet-side | No in-container client needed; distroless collector becomes probeable on :13133. |
| `depends_on: service_healthy` | phased apply + `rollout status`; probes + restart | Ordering moves into the bring-up script; apps self-heal. |
| `deploy.replicas: 2` | `Deployment.spec.replicas: 2` | Trivial 1:1. |
| named volume `pgdata` | `volumeClaimTemplates` + default `hostpath` SC | Dynamic per-pod PVC. |
| `${VAR}` interpolation from `.env` | Secret `secretKeyRef` + `$(VAR)` dependent env | Same runtime result; creds via committed dev-only Secret. |
| `docker compose up --force-recreate` after build | `docker build :local` + `kubectl rollout restart` | Same-tag images require an explicit restart to redeploy (SourceHash currency). |

**Deprecated/outdated:** none of the compose images change. otel-collector-contrib 0.152.0, ES 8.15.5, prom v3.11.3, redis 7.4.9-alpine, rabbitmq 4.1.8-management-alpine, postgres 17-alpine — all carried verbatim.

## Runtime State Inventory

> This is a deployment-target *port* touching shared host resources (image store, host ports, PVCs). Included accordingly.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | Postgres L3 graph (workflows/steps/…) + processors/schemas; Redis L2 (`skp:*`) — created at seed time, not pre-existing. New k8s PVCs `data-postgres-0` (1Gi), `data-elasticsearch-0` (2Gi), `data-rabbitmq-0` (1Gi); redis ephemeral. | The reset harness (FLUSHALL + FK-safe DELETE) manages data cleanliness — must be re-targeted to `kubectl exec` (Pitfall 1). PVCs are new, empty at first apply. |
| Live service config | None external. All config is in-repo: `compose/otel-collector-config.yaml`, `prometheus.yml` → ConfigMaps (D-06). | Generate ConfigMaps from the repo files (kustomize `configMapGenerator`). |
| OS-registered state | Docker Desktop host ports 8080/9090/9200/15673/6380/5433 are shared between the compose stack and the k8s port-forwards (mutually exclusive — Pitfall 5). | Bring-up must `docker compose down` before starting forwards; fail loud on bind collision. |
| Secrets/env vars | `.env` dev creds (POSTGRES_*), rabbitmq guest/guest → committed dev-only Secret `skp-dev-secrets` (D-07). The 7 test-only seams intentionally ABSENT (D-08). | Author the Secret; carry the 3 production TTL literals explicitly (`Processor__ExecutionDataTtl:900`, `Orchestrator__OutputDataTtlSeconds:900`, `Recovery__ExecutionDataTtlSeconds:900`). |
| Build artifacts / installed images | 5 app images tagged `:local` in the Docker Desktop store (new); shared with kubelet (D-04). Rebuild does NOT auto-redeploy (Pitfall 2). | `docker build` all 5 → `kubectl rollout restart` to force pods onto fresh bits (SourceHash currency, D-05). |

## Validation Architecture

> `workflow.nyquist_validation: true` in config → section included. Note: this phase's deliverables are YAML manifests + PowerShell scripts, so "tests" are structural (manifest lint) + behavioral (the health gate + round-trip proof), NOT new C# unit tests. The C# seeder/analyzer already exist and are reused.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xunit.v3 under Microsoft.Testing.Platform (`tests/BaseApi.Tests`) — reused for `~FanOutSeeder` (seed) + `~Analyze_Window_Yields_Pass` (analyzer). No new tests authored this phase. |
| Config file | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| Quick run (manifest lint) | `kubectl apply -k k8s/ --dry-run=client -o yaml` (client-side schema validate) then `--dry-run=server` (API-server validate) |
| Full proof | `pwsh -File scripts/phase-80-up.ps1` (bring-up + Ready gate) → `pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-01` (happy-path round-trip) |

### Phase Requirements → Test Map (no REQUIREMENTS.md; success bar from ROADMAP §80 / D-16)
| Req (success bar) | Behavior | Test Type | Automated Command | Exists? |
|-------------------|----------|-----------|-------------------|---------|
| Manifests are valid k8s | Schema-valid, apply-able | lint | `kubectl apply -k k8s/ --dry-run=server` | ❌ Wave 0 (manifests + kustomization) |
| Full stack Healthy | All 10 tiers Ready (incl. keeper×2, processor×2) | smoke | `kubectl -n skp rollout status statefulset/… deployment/… --timeout=…` (in `phase-80-up.ps1`) | ❌ Wave 0 (bring-up script) |
| Images current (SourceHash) | Container hash == host build | smoke | `pwsh scripts/verify-sourcehash-reproducible.ps1` + build-before-seed order | ✅ (verify script exists) |
| Reset reaches k8s | FLUSHALL + FK-safe DELETE hit k8s pods | smoke | k8s reset via `kubectl exec` (Pitfall 1) | ❌ Wave 0 (reset re-target) |
| Round-trip end-to-end | seed → `POST /start` == 204 | integration | `~FanOutSeeder` + activation gate in harness | ✅ (harness/seeder exist; DB access needs re-target) |
| Verifiable from Prometheus | `orchestrator_messages_sent_total` present (fires); MG-1 conservation | integration | `phase-68-sweep.ps1` reads `localhost:9090/api/v1/query` (via port-forward) | ✅ (sweep exists) |
| Verifiable from ES | log docs present in-window (analyzer verdict PASS) | integration | analyzer `~Analyze_Window_Yields_Pass` reads `localhost:9200` (via port-forward) | ✅ (analyzer exists) |

### Sampling Rate
- **Per task commit:** `kubectl apply -k k8s/ --dry-run=server` (manifests validate) + `kubectl -n skp get pods` sanity.
- **Per wave merge:** `pwsh scripts/phase-80-up.ps1` reaches all-Ready.
- **Phase gate:** full happy-path `phase-80-up.ps1` → reset(k8s) → seed → 204 → `phase-68-sweep.ps1 -ScenarioIds TEST-01` PASS, all signals green.

### Wave 0 Gaps
- [ ] `k8s/*.yaml` (12 manifests) + `k8s/kustomization.yaml` — the port itself.
- [ ] `scripts/phase-80-build.ps1` — 5× `docker build -t <name>:local`.
- [ ] `scripts/phase-80-up.ps1` — apply → phased `rollout status` → `rollout restart` (image currency) → port-forwards.
- [ ] k8s reset path — re-target `phase-65-reset.ps1`'s `docker exec`/`docker compose exec` to `kubectl exec` (or a `phase-80-reset.ps1` sibling); re-target harness STEP B1 (`docker compose restart orchestrator` → `kubectl rollout restart`) and STEP D wf-id (`docker compose exec postgres psql` → `kubectl exec`).
- [ ] Decide the reset/wf-id mechanism (kubectl exec vs host TCP client) — Pitfall 1 (blocking design choice).

## Security Domain

> `security_enforcement` absent from config (= enabled). This is a local, single-node, dev-only deployment port with NO new application code and NO new endpoints, so most ASVS categories are N/A by construction; the material item is the committed dev Secret.

### Applicable ASVS Categories
| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | No new auth surface; dev creds unchanged from `.env`/compose. |
| V3 Session Management | no | N/A. |
| V4 Access Control | no | Local single-node; no ingress; ClusterIP only. |
| V5 Input Validation | no (no new code) | reset/wf-id psql use static literals (existing threat model T-65-06/T-67-01); if translating to `kubectl exec`, keep the same static-literal SQL — no interpolated input. |
| V6 Cryptography | no | Dev creds in plaintext by explicit decision (D-07). No secrets management library — a committed dev-only Secret is the deliberate posture. |

### Known Threat Patterns for this port
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Committed dev Secret mistaken for prod-safe | Information Disclosure | Bold `# DEV ONLY` header + `sensitivity: dev-only` label; document "never a real cluster" (D-07). Guest/guest + postgres/postgres are already public in `.env`/compose — no new exposure. |
| Port-forward binds 0.0.0.0 exposing services to LAN | Info Disclosure | Bind `--address 127.0.0.1` explicitly (Pattern 5) — do not use `--address 0.0.0.0`. |
| Stale image runs unexpected code | Tampering | SourceHash currency gate (D-05) + `verify-sourcehash-reproducible.ps1`; rollout-restart after build (Pitfall 2). |
| `kubectl exec` with interpolated shell input | Injection | Keep reset/wf-id SQL + redis commands as static literals (as the compose scripts already do); never interpolate untrusted input into `kubectl exec -- …`. |

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Apps expose only `/health/ready` (no separate `/health/live`); tcpSocket is the safe liveness | Probe table, Pitfall 3 | LOW — if a `/health/live` exists, prefer it; tcpSocket still works either way. Verify BaseConsole.Core health endpoints during planning. |
| A2 | Docker Desktop k8s shares the docker image store so `:local` + IfNotPresent resolves without a registry | D-04, Pitfall 4 | MEDIUM — the containerd-image-store toggle can split them; verified as the documented common approach but confirm on this machine (`kubectl describe pod` on first apply). |
| A3 | ES image ships `pg_isready`-style clients is N/A; ES readiness uses httpGet (kubelet-side), so no in-container curl needed | ES manifest, probe table | LOW — httpGet is kubelet-side; independent of in-container tooling. |
| A4 | rabbitmq/postgres/redis images contain `rabbitmq-diagnostics`/`pg_isready`/`redis-cli` for exec probes | probe table | LOW — these ship in the official images (compose already execs them in healthchecks). |
| A5 | The seeder + analyzer (host `dotnet test`) connect to Postgres/ES via localhost:5433/9200 (host ports), covered by port-forward | Validation Architecture | MEDIUM — if the seeder hard-codes a different host/port or uses `docker compose exec`, the port-forward assumption breaks. Verify the seeder/analyzer connection config during planning. |
| A6 | `discovery.type=single-node` skips ES bootstrap checks, so vm.max_map_count needs no tuning on this machine | Pitfall 7 | LOW — confirmed by ES docs + the working compose stack on the same host; fallback documented. |

## Open Questions (RESOLVED)

> All four resolved during planning (Phase 80 plans 80-01..80-10) and reflected in concrete plan tasks. Markers added 2026-07-18.

1. **Reset/wf-id/clean-orchestrator mechanism: `kubectl exec` vs host TCP clients (Pitfall 1).**
   - What we know: the current scripts use `docker exec`/`docker compose exec`/`docker compose restart`, which can't address k8s pods; D-14 forwards redis 6380/postgres 5433.
   - What's unclear: whether to (a) re-target to `kubectl exec`/`kubectl rollout restart` (no new host deps, recommended) or (b) add host `psql`/`redis-cli` on forwarded ports (literal D-14 reading).
   - Recommendation: `kubectl exec` sibling reset + rollout-restart for STEP B1 + `kubectl exec` for wf-id. Parameterize or fork; decide before Wave 1.
   - **RESOLVED:** adopted (a) `kubectl exec` / `kubectl rollout restart`, no new host deps. Owned by plan **80-08** (`phase-80-reset.ps1` reset re-target) and **80-10** (harness STEP B1 rollout-restart + STEP D wf-id via `kubectl exec … psql`).

2. **Does BaseConsole.Core expose a lightweight liveness endpoint (A1)?**
   - What we know: compose only ever probes `/health/ready`.
   - What's unclear: whether a `/health/live` (always-200) exists.
   - Recommendation: grep `src/BaseConsole.Core` for health endpoint routes during planning; default to `tcpSocket` liveness if absent.
   - **RESOLVED:** `/health/live`, `/health/ready`, `/health/startup` all confirmed present in `BaseConsole.Core/Health/EmbeddedHealthEndpointService.cs` + `BaseApi.Core/DependencyInjection/BaseApiApplicationBuilderExtensions.cs`. Liveness uses `httpGet /health/live` (decoupled from broker readiness) in plans **80-05/80-06** — the tcpSocket fallback is not needed.

3. **Seeder/analyzer host-side connection config (A5).**
   - What we know: analyzer reads ES; seeder writes Postgres; both run as host `dotnet test`.
   - What's unclear: exact host/port each uses (localhost:5433 / :9200 assumed).
   - Recommendation: inspect the test fixtures' connection strings; ensure they resolve to the forwarded ports (or make them env-driven).
   - **RESOLVED:** the `~FanOutSeeder` in-proc WebApi (`RealStackWebAppFactory`, `SampleRoundTripE2ETests.cs:417-458`) hard-codes **8** host endpoints (not 6) — additionally RabbitMQ AMQP `localhost:5673` + otel `localhost:4317`. Plan **80-09** provisions all 8 port-forwards; a documented completion of D-14's own rationale.

4. **kustomize `configMapGenerator` relative path traversal.**
   - What we know: config files live at `compose/otel-collector-config.yaml` and repo-root `prometheus.yml`, one level up from `k8s/`.
   - What's unclear: whether `files: [../compose/…]` traversal is acceptable in the target kustomize version (some versions restrict `..`).
   - Recommendation: prefer `configMapGenerator` with `../` paths; fallback = copy the two files into `k8s/` or inline `data:`.
   - **RESOLVED:** sidestepped entirely — plans **80-01/80-07** hand-author inline ConfigMaps (`data:` with the config carried verbatim), so no `..` traversal and no configMapGenerator dependency.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| kubectl | all manifest/exec/forward ops | ✓ (ships w/ Docker Desktop) | verify `kubectl version --client` | — |
| Docker Desktop Kubernetes | the cluster | ✓ (enable in settings; context `docker-desktop`) | single-node | — |
| Docker daemon / image store | `:local` image build + share to kubelet | ✓ | — | containerd toggle off if split (Pitfall 4) |
| host `dotnet` SDK 8 | seeder + analyzer + SourceHash host build | ✓ (already used by compose harness) | net8.0 | — |
| host `psql` / `redis-cli` | ONLY if reset uses host TCP clients (alt to kubectl exec) | ? verify | — | `kubectl exec` (no host deps) — recommended |
| WSL2 `vm.max_map_count` tuning | ES only if bootstrap checks fire | not needed | — | `wsl -d docker-desktop -u root sysctl -w vm.max_map_count=262144` |

**Missing dependencies with no fallback:** none identified — the cluster + docker + dotnet are all already in use by the compose harness.
**Missing dependencies with fallback:** host `psql`/`redis-cli` avoidable via `kubectl exec`; ES sysctl avoidable via single-node discovery.

## Sources

### Primary (HIGH confidence)
- Repo source-of-truth (read in full this session): `compose.yaml`, `.env`, `compose/otel-collector-config.yaml`, `prometheus.yml`, `Dockerfile`, `src/{Orchestrator,Keeper,Processor.Sample}/Dockerfile`, `scripts/phase-65-up.ps1`, `scripts/phase-65-reset.ps1`, `scripts/phase-67-harness.ps1`, `scripts/phase-68-sweep.ps1`, `80-CONTEXT.md`, `.planning/config.json`.
- kubernetes.io/docs — StatefulSet (serviceName/headless/volumeClaimTemplates), Deployment, Probes (liveness/readiness/startup), Secrets (stringData), interdependent env `$(VAR)`, port-forward, Images (imagePullPolicy IfNotPresent/latest default).

### Secondary (MEDIUM confidence, web-verified this session)
- Docker Desktop default StorageClass `hostpath` + WaitForFirstConsumer/Immediate binding semantics — kubernetes.io storage-classes + Docker Desktop local-k8s guides (2026).
- ES `discovery.type=single-node` skips bootstrap checks; WSL2 `vm.max_map_count` tuning — Elastic docs + docker/for-win#5202.
- Local image `:local` + IfNotPresent on Docker Desktop, `ErrImageNeverPull`/`ImagePullBackOff` semantics — kubernetes.io Images + dev.to Docker Desktop local-image guide.

### Tertiary (LOW confidence — validate during planning)
- Exact BaseConsole.Core health endpoints (A1/Q2); seeder/analyzer connection config (A5/Q3); kustomize `..` path support in the installed version (Q4).

## Metadata

**Confidence breakdown:**
- Standard stack / manifest mechanics: HIGH — verified against current k8s docs + the repo's own files.
- compose→k8s translation (probes, DNS, secrets, configmaps, images): HIGH — direct 1:1 mappings with cited primitives.
- Harness reuse reality (Pitfall 1): HIGH — read the actual scripts; the docker/compose CLI coupling is unambiguous.
- Docker-Desktop-specific gotchas (image store, PVC, memory, vm.max_map_count): MEDIUM — depend on this machine's Docker Desktop config; verification steps provided.
- Seeder/analyzer connection assumptions: MEDIUM — inferred, flagged as A5/Q3.

**Research date:** 2026-07-18
**Valid until:** ~2026-08-17 (k8s manifest APIs stable; image tags pinned). Re-verify only if Docker Desktop's k8s image-store behavior changes.
