# Phase 80: Deploy full system to local Kubernetes (Docker Desktop) - Pattern Map

**Mapped:** 2026-07-18
**Files analyzed:** 17 new files (13 k8s manifests + 1 kustomization + 3 scripts)
**Analogs found:** 17 / 17

> **Novel-file-type phase.** The repo has NO existing `k8s/` dir and NO `.yaml` manifests. So the "closest analog" for each new file is NOT another manifest - it is the **source-of-truth being ported**: the tier's block in `compose.yaml`, the two bind-mounted config files, `.env`, or the existing bring-up/reset/build script. Each excerpt below is the exact text to translate **field-by-field**. Every architectural choice is already locked by D-01..D-16 (see 80-CONTEXT.md); this map only supplies the copy-from source per file.

---

## File Classification

| New File | Role | Data Flow | Closest Analog (source-of-truth) | Match Quality |
|----------|------|-----------|----------------------------------|---------------|
| `k8s/00-namespace.yaml` | manifest (Namespace) | config | none (k8s boilerplate; namespace `skp` per D-02) | boilerplate |
| `k8s/01-secret.yaml` | secret | config | `.env` (L1-3) + compose rabbitmq env (L168-170) | exact |
| `k8s/02-configmaps.yaml` | configmap | file-I/O | `compose/otel-collector-config.yaml` + `prometheus.yml` (verbatim) | exact |
| `k8s/10-postgres.yaml` | manifest (StatefulSet+PVC+Svc) | CRUD / persistence | compose `postgres` block (L6-22) | exact |
| `k8s/11-redis.yaml` | manifest (StatefulSet no-PVC+Svc) | cache | compose `redis` block (L135-151) | exact |
| `k8s/12-rabbitmq.yaml` | manifest (StatefulSet+PVC+Svc) | pub-sub / broker | compose `rabbitmq` block (L161-176) | exact |
| `k8s/13-elasticsearch.yaml` | manifest (StatefulSet+PVC+Svc) | log sink | compose `elasticsearch` block (L28-49) | exact |
| `k8s/20-otel-collector.yaml` | manifest (Deployment+ClusterIP) | streaming / OTLP | compose `otel-collector` block (L51-79) | exact |
| `k8s/21-prometheus.yaml` | manifest (Deployment+ClusterIP) | scrape / request-response | compose `prometheus` block (L86-114) | exact |
| `k8s/30-baseapi-service.yaml` | manifest (Deployment+ClusterIP) | request-response | compose `baseapi-service` block (L359-398) | exact |
| `k8s/31-orchestrator.yaml` | manifest (Deployment, no Svc) | event-driven | compose `orchestrator` block (L185-218) | exact |
| `k8s/32-keeper.yaml` | manifest (Deployment replicas:2, no Svc) | event-driven | compose `keeper` block (L233-269) | exact |
| `k8s/33-processor-sample.yaml` | manifest (Deployment replicas:2, no Svc) | event-driven | compose `processor-sample` block (L287-320) | exact |
| `k8s/kustomization.yaml` | config (aggregator) | config | RESEARCH Pattern 7 `configMapGenerator` (L339-350) | template |
| `scripts/phase-80-build.ps1` | script (build) | batch | harness STEP A0 (L185-187) + 4 Dockerfile paths | role-match |
| `scripts/phase-80-up.ps1` | script (bring-up) | orchestration | `scripts/phase-65-up.ps1` (full, L1-85) | role-match |
| k8s reset re-target (`phase-80-reset.ps1` **or** param the existing) | script (reset) | CRUD | `scripts/phase-65-reset.ps1` (full) + harness STEP B1/D | role-match |

**Key structural note (RESEARCH L151-165):** orchestrator / keeper / processor-sample are pure *consumers* - nothing connects to them inbound except the kubelet probe (hits pod IP directly). They get **NO Service**. Only backing services + baseapi + otel-collector + prometheus get Services.

---

## Pattern Assignments

### `k8s/10-postgres.yaml` (StatefulSet + PVC + headless Service)

**Analog:** compose `postgres` block (`compose.yaml` L6-22)
```yaml
  postgres:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: ${POSTGRES_DB}
      POSTGRES_USER: ${POSTGRES_USER}
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
    ports:
      - "5433:5432"                       # host:container - only 5432 (container) ports over
    volumes:
      - pgdata:/var/lib/postgresql/data   # -> volumeClaimTemplates mount /var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U $$POSTGRES_USER -d $$POSTGRES_DB"]
      interval: 5s                        # -> periodSeconds 5
      timeout: 5s                         # -> timeoutSeconds 5
      retries: 10                         # -> failureThreshold 10
      start_period: 5s                    # -> initialDelaySeconds 5
```
**Fields that port:** `image: postgres:17-alpine` verbatim; the 3 env vars become `secretKeyRef` into `skp-dev-secrets` (D-07); `pgdata` named volume -> `volumeClaimTemplates` name `data`, mount `/var/lib/postgresql/data`, `1Gi` (D-09); healthcheck -> readiness `exec` probe `pg_isready -U postgres -d stepsdb` (RESEARCH L200, note secret-derived user/db resolve to `postgres`/`stepsdb` from `.env`). **Drop** the `5433:5432` host publish (that re-emerges only at port-forward, D-14). Full manifest skeleton: RESEARCH Pattern 1 (L167-214).

---

### `k8s/11-redis.yaml` (StatefulSet, NO PVC, command args)

**Analog:** compose `redis` block (`compose.yaml` L135-151)
```yaml
  redis:
    image: redis:7.4.9-alpine
    container_name: sk-redis                                   # DROP - StatefulSet is pod-named redis-0
    restart: unless-stopped
    ports:
      - "6380:6379"                                            # DROP host publish; container 6379 ports
    command: ["redis-server", "--save", "", "--appendonly", "no"]   # CARRY VERBATIM (D-12 - ephemeral by design)
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]                       # -> readiness exec ["redis-cli","ping"]
      interval: 5s                                             # -> periodSeconds 5
      timeout: 3s                                              # -> timeoutSeconds 3
      retries: 10                                              # -> failureThreshold 10
      start_period: 5s                                         # -> initialDelaySeconds 5
```
**Fields that port:** `image: redis:7.4.9-alpine` verbatim; **`command` array carried byte-for-byte** (D-12); healthcheck -> readiness `exec`. **NO `volumeClaimTemplates`** (ephemeral by design, D-12). Full skeleton: RESEARCH "Redis StatefulSet, no PVC" (L509-540).

---

### `k8s/12-rabbitmq.yaml` (StatefulSet + PVC + headless Service)

**Analog:** compose `rabbitmq` block (`compose.yaml` L161-176)
```yaml
  rabbitmq:
    image: rabbitmq:4.1.8-management-alpine
    container_name: sk-rabbitmq                       # DROP
    restart: unless-stopped
    ports:
      - "5673:5672"     # AMQP     -> Service port 5672 (container)
      - "15673:15672"   # mgmt UI  -> Service port 15672 (container)  (port-forward 15673->15672, D-14)
    environment:
      RABBITMQ_DEFAULT_USER: guest                    # -> secretKeyRef skp-dev-secrets/RABBITMQ_DEFAULT_USER
      RABBITMQ_DEFAULT_PASS: guest                    # -> secretKeyRef skp-dev-secrets/RABBITMQ_DEFAULT_PASS
    healthcheck:
      test: ["CMD", "rabbitmq-diagnostics", "-q", "ping"]   # -> startup + readiness exec
      interval: 10s                                   # -> periodSeconds 10
      timeout: 5s                                      # -> timeoutSeconds 5
      retries: 5                                       # -> readiness failureThreshold 5
      start_period: 40s                                # -> startupProbe (period 10 x failThresh 6 = 60s budget)
```
**Fields that port:** image verbatim; guest/guest -> Secret refs (D-07); headless Service exposes **both** 5672 + 15672; PVC `1Gi` mount `/var/lib/rabbitmq` (D-11, durable mnesia). **`start_period: 40s` -> `startupProbe`** (RESEARCH probe table L411) so the 40s cold start never trips liveness (Pitfall 3).

---

### `k8s/13-elasticsearch.yaml` (StatefulSet + PVC + startupProbe)

**Analog:** compose `elasticsearch` block (`compose.yaml` L28-49)
```yaml
  elasticsearch:
    image: docker.elastic.co/elasticsearch/elasticsearch:8.15.5
    container_name: sk-elasticsearch                  # DROP
    environment:
      - discovery.type=single-node                    # CARRY VERBATIM (skips bootstrap checks - Pitfall 7)
      - xpack.security.enabled=false                  # CARRY VERBATIM
      - xpack.security.enrollment.enabled=false       # CARRY VERBATIM
      - ES_JAVA_OPTS=-Xms512m -Xmx512m                # CARRY VERBATIM
    ports:
      - "9200:9200"                                   # container 9200; port-forward 9200->9200
    healthcheck:
      test: ["CMD-SHELL", "curl -fs 'http://localhost:9200/_cluster/health?wait_for_status=yellow&timeout=5s' || exit 1"]
      interval: 10s                                   # -> periodSeconds 10
      timeout: 5s                                     # -> timeoutSeconds 5
      retries: 5                                       # -> readiness failureThreshold 5
      start_period: 60s                                # -> startupProbe (period 10 x failThresh 12 = 120s)
```
**Fields that port:** image verbatim; **all 4 env vars carried verbatim** (list-form `- key=value` becomes `env: [{name,value}]`); healthcheck curl becomes `httpGet` on `/_cluster/health?wait_for_status=yellow&timeout=5s` :9200 (kubelet-side, no in-container curl needed) - **`wait_for_status=yellow` is load-bearing** (single-node ES never reaches green). PVC `2Gi` mount `/usr/share/elasticsearch/data` (D-10, run-evidence stability). Full skeleton: RESEARCH "elasticsearch StatefulSet + PVC + startupProbe" (L542-577).

---

### `k8s/20-otel-collector.yaml` (Deployment + ClusterIP Service + ConfigMap mount)

**Analog:** compose `otel-collector` block (`compose.yaml` L51-79)
```yaml
  otel-collector:
    image: otel/opentelemetry-collector-contrib:0.152.0
    container_name: sk-otel-collector                 # DROP
    command: ["--config=/etc/otel-collector-config.yaml"]     # CARRY VERBATIM (args)
    volumes:
      - ./compose/otel-collector-config.yaml:/etc/otel-collector-config.yaml:ro   # -> ConfigMap subPath mount
    ports:
      - "4317:4317"   # OTLP gRPC     -> Service 4317 (in-cluster: http://otel-collector:4317)
      - "4318:4318"   # OTLP HTTP     -> Service 4318
      - "8889:8889"   # Prom scrape   -> Service 8889 (prometheus scrapes this)
      - "13133:13133" # health_check  -> Service 13133 + httpGet readiness probe
    # NO healthcheck in compose (distroless, no shell)
```
**Fields that port:** image + `command` verbatim; bind-mount -> ConfigMap `otel-collector-config` mounted via **`subPath`** at `/etc/otel-collector-config.yaml` (RESEARCH Pattern 7 L324-337); ClusterIP Service exposes 4317/4318/8889/13133. **Probe upgrade:** compose had NO healthcheck (distroless workaround); k8s uses `httpGet: {path: /, port: 13133}` readiness (kubelet-side, works on distroless - RESEARCH Pattern 6 L313-322). `imagePullPolicy` default (public image, not `:local`).

---

### `k8s/21-prometheus.yaml` (Deployment + ClusterIP Service + ConfigMap mount)

**Analog:** compose `prometheus` block (`compose.yaml` L86-114)
```yaml
  prometheus:
    image: prom/prometheus:v3.11.3
    container_name: sk-prometheus                     # DROP
    command:
      - "--config.file=/etc/prometheus/prometheus.yml"        # CARRY VERBATIM
      - "--web.enable-lifecycle"                              # CARRY VERBATIM
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml:ro    # -> ConfigMap subPath mount
    ports:
      - "9090:9090"                                   # -> Service 9090; port-forward 9090->9090
    depends_on: { otel-collector: {condition: service_started} }   # DROP - k8s has no depends_on (phased bring-up)
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:9090/-/healthy"]   # -> httpGet /-/healthy :9090
      interval: 10s                                   # -> periodSeconds 10
      timeout: 5s                                     # -> timeoutSeconds 5
      retries: 3                                       # -> failureThreshold 3
      start_period: 10s                                # -> initialDelaySeconds 10
```
**Fields that port:** image + both `command` flags verbatim; bind-mount -> ConfigMap `prometheus-config` via `subPath` at `/etc/prometheus/prometheus.yml`; healthcheck -> `httpGet: {path: /-/healthy, port: 9090}` readiness. `depends_on` **dropped** (ordering moves to the bring-up script, RESEARCH Pattern 4).

---

### `k8s/30-baseapi-service.yaml` (Deployment ×1 + ClusterIP Service 8080)

**Analog:** compose `baseapi-service` block (`compose.yaml` L359-398)
```yaml
  baseapi-service:
    build: { context: ., dockerfile: Dockerfile }    # -> image: baseapi-service:local + IfNotPresent (D-04)
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}"
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"    # CARRY VERBATIM
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"                         # CARRY VERBATIM
      RabbitMq__Host: rabbitmq                        # CARRY VERBATIM (Service DNS)
      RabbitMq__Username: guest                       # -> secretKeyRef
      RabbitMq__Password: guest                       # -> secretKeyRef
    ports:
      - "8080:8080"                                   # container 8080; port-forward 8080->8080
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8080/health/ready"]    # -> httpGet /health/ready :8080
      interval: 10s / timeout: 3s / retries: 5 / start_period: 30s
    depends_on: [postgres, otel-collector, elasticsearch, prometheus, redis, rabbitmq]  # DROP - phased bring-up
```
**Fields that port:** `build:` -> `image: baseapi-service:local` + `imagePullPolicy: IfNotPresent` (D-04); **`ConnectionStrings__Postgres` uses the `$(VAR)` dependent-env pattern** - the 3 POSTGRES_* Secret refs MUST be listed *before* the composed connection string (RESEARCH Pattern 3 L256-267); `ConnectionStrings__Redis`, `OTEL_*`, `RabbitMq__Host` carried byte-for-byte (Service DNS resolves identically); guest/guest -> Secret refs. Readiness `httpGet /health/ready :8080` + `tcpSocket :8080` liveness (Pitfall 3). Only tier the harness talks HTTP to.

---

### `k8s/31-orchestrator.yaml` (Deployment ×1, NO Service - SPOF, locked at 1)

**Analog:** compose `orchestrator` block (`compose.yaml` L185-218)
```yaml
  orchestrator:
    build: { context: ., dockerfile: src/Orchestrator/Dockerfile }   # -> image: orchestrator:local + IfNotPresent
    environment:
      RabbitMq__Host: rabbitmq                        # CARRY VERBATIM
      RabbitMq__Username: guest / RabbitMq__Password: guest           # -> secretKeyRef
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"   # CARRY VERBATIM
      Orchestrator__InstanceId: orchestrator-1        # CARRY VERBATIM (orchestrator-only)
      Orchestrator__OutputDataTtlSeconds: "${ORCH_OUTPUT_TTL:-900}"   # -> LITERAL "900" (D-08 - drop the ${} seam)
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"       # CARRY VERBATIM
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8081/health/ready"]   # -> httpGet /health/ready :8081
      interval: 10s / timeout: 3s / retries: 5 / start_period: 30s
    depends_on: [rabbitmq, redis]                     # DROP
```
**D-08 CRITICAL:** `Orchestrator__OutputDataTtlSeconds: "${ORCH_OUTPUT_TTL:-900}"` -> ship as the **literal `"900"`** (the `${...:-900}` interpolation is a test-only seam; absent env == default 900). `Orchestrator__InstanceId: orchestrator-1` carried. **NO Service** (pure consumer). Readiness `httpGet /health/ready :8081` gates Ready; liveness `tcpSocket :8081` (Kestrel binds before broker - Pitfall 3). `replicas: 1` (documented SPOF, locked).

---

### `k8s/32-keeper.yaml` (Deployment replicas:2, NO Service)

**Analog:** compose `keeper` block (`compose.yaml` L233-269)
```yaml
  keeper:
    build: { context: ., dockerfile: src/Keeper/Dockerfile }   # -> image: keeper:local + IfNotPresent
    deploy: { replicas: 2 }                           # -> Deployment spec.replicas: 2 (trivial 1:1)
    environment:
      RabbitMq__Host: rabbitmq                        # CARRY VERBATIM
      RabbitMq__Username: guest / Password: guest      # -> secretKeyRef
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"   # CARRY VERBATIM
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"    # CARRY VERBATIM
      KEEPER_DEFEAT_REINJECT: "${KEEPER_DEFEAT_REINJECT:-0}"       # OMIT ENTIRELY (D-08 test seam - absent=0=off)
      KEEPER_REINJECT_DELAY_MS: "${KEEPER_REINJECT_DELAY_MS:-0}"   # OMIT ENTIRELY (D-08)
      Recovery__ExecutionDataTtlSeconds: "${KEEPER_RECOVERY_TTL:-900}"   # -> LITERAL "900" (D-08 keep prod default)
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8083/health/ready"]   # -> httpGet /health/ready :8083
```
**D-08 CRITICAL:** OMIT `KEEPER_DEFEAT_REINJECT` and `KEEPER_REINJECT_DELAY_MS` entirely (absent => code `:-0` default => OFF). Carry `Recovery__ExecutionDataTtlSeconds` as **literal `"900"`** (drop the `${KEEPER_RECOVERY_TTL:-900}` seam, keep the production default). `deploy.replicas: 2` -> `spec.replicas: 2`. **NO Service, NO container_name** (already unnamed in compose - can't scale a named container, L100 CONTEXT). Full skeleton: RESEARCH Pattern 2 (L216-252).

---

### `k8s/33-processor-sample.yaml` (Deployment replicas:2, NO Service)

**Analog:** compose `processor-sample` block (`compose.yaml` L287-320)
```yaml
  processor-sample:
    build: { context: ., dockerfile: src/Processor.Sample/Dockerfile }   # -> image: processor-sample:local + IfNotPresent
    deploy: { replicas: 2 }                           # -> spec.replicas: 2
    environment:
      RabbitMq__Host: rabbitmq / Username: guest / Password: guest       # host verbatim; creds -> secretKeyRef
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"   # CARRY VERBATIM
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"          # CARRY VERBATIM
      Processor__ExecutionDataTtl: "${EXECUTION_DATA_TTL:-900}"          # -> LITERAL "900" (D-08 - load-bearing default)
      PROCESSOR_STEP_DELAY_MS: "${PROCESSOR_STEP_DELAY_MS:-0}"           # OMIT ENTIRELY (D-08 test seam)
      PROCESSOR_DEFEAT_READ: "${PROCESSOR_DEFEAT_READ:-0}"               # OMIT ENTIRELY (D-08 test seam)
    healthcheck:
      test: ["CMD", "wget", "--spider", "-q", "http://localhost:8082/health/ready"]   # -> httpGet /health/ready :8082
```
**D-08 CRITICAL:** carry `Processor__ExecutionDataTtl` as **literal `"900"`** (drop `${EXECUTION_DATA_TTL:-900}` - this is the load-bearing default per compose L281-284, the jittered [900,1800] floor must outlast the recovery window); OMIT both `PROCESSOR_STEP_DELAY_MS` and `PROCESSOR_DEFEAT_READ`. `replicas: 2`, NO Service. **Its compose `depends_on` adds `baseapi-service`** (it resolves identity over the bus from the WebApi) - in k8s this is dropped (phased bring-up); the app self-heals via readiness. **`processor-badconfig` (compose L328-357, `profiles: ["badconfig"]`) is EXCLUDED** - not built, no manifest (D-04, mirrors compose default-profile exclusion).

---

### `k8s/01-secret.yaml` (dev-only Secret)

**Analog:** `.env` (L1-3) + compose rabbitmq env (L168-170)
```
# .env
POSTGRES_DB=stepsdb
POSTGRES_USER=postgres
POSTGRES_PASSWORD=postgres
# compose.yaml L168-170
      RABBITMQ_DEFAULT_USER: guest
      RABBITMQ_DEFAULT_PASS: guest
```
**Port:** `stringData` keys `POSTGRES_DB/USER/PASSWORD` + `RABBITMQ_DEFAULT_USER/PASS` verbatim from `.env`/compose (D-07). Use `stringData` not `data` (readable, diff-able against `.env`; k8s base64-encodes on write). Name `skp-dev-secrets`, add bold `# DEV ONLY` header + `sensitivity: dev-only` label. Full skeleton: RESEARCH Pattern 8 (L352-369).

---

### `k8s/02-configmaps.yaml` (2 ConfigMaps)

**Analog:** `compose/otel-collector-config.yaml` + `prometheus.yml` - **carry contents verbatim** (D-06). These are the exact files bind-mounted in compose (otel L63, prom L96). Preferred mechanism: kustomize `configMapGenerator` with `disableNameSuffixHash: true` (RESEARCH Pattern 7 L338-350) so they stay in sync with the repo files and the Deployment `configMap.name` references don't break. Names: `otel-collector-config`, `prometheus-config`. If `../` path traversal is unsupported (Open Q4), fallback = inline the file contents under `data:`. In-container mount paths (must match compose): `/etc/otel-collector-config.yaml`, `/etc/prometheus/prometheus.yml`.

---

### `k8s/kustomization.yaml` (aggregator - D-03)

**Analog:** RESEARCH Pattern 7 kustomization skeleton (L339-350). Only lists resources + stamps `namespace: skp` + common labels + `configMapGenerator` for the 2 config files. NO templating/overlays. `generatorOptions: {disableNameSuffixHash: true}`. Note (RESEARCH L149): `kubectl apply -k` sorts by *kind* not filename, so infra/app apply-ordering must live in the **bring-up script**, not file order.

---

### `scripts/phase-80-build.ps1` (NEW - 5 image builds)

**Analog:** harness STEP A0 (`scripts/phase-67-harness.ps1` L185-187)
```powershell
    Write-Phase "STEP A0: build images (SourceHash currency — container must match host-built Processor.Sample)"
    docker compose build 2>&1 | Out-String | Write-Host
    if ($LASTEXITCODE -ne 0) { Write-Phase "image build failed (exit $LASTEXITCODE). Aborting." 'Red'; exit 10 }
```
**Translation:** replace the single `docker compose build` with 4 explicit `docker build -t <name>:local -f <Dockerfile> .` (build context = repo root; Dockerfiles COPY `src/` from root). Confirmed Dockerfile paths (`Glob **/Dockerfile`):
```powershell
$ErrorActionPreference = 'Stop'
docker build -t baseapi-service:local  -f Dockerfile .
docker build -t orchestrator:local     -f src/Orchestrator/Dockerfile .
docker build -t keeper:local           -f src/Keeper/Dockerfile .
docker build -t processor-sample:local -f src/Processor.Sample/Dockerfile .
# src/Processor.BadConfig/Dockerfile EXISTS but is intentionally NOT built (D-04 excluded)
```
Preserve STEP A0's fail-loud `$LASTEXITCODE` check + `$ErrorActionPreference = 'Stop'` idiom after each build. This preserves the SourceHash-currency guarantee (D-05): container assembly hash == seeder's host-built `Processor.Sample.dll`. Ref: RESEARCH "Build all 5 app images" (L497-506).

---

### `scripts/phase-80-up.ps1` (NEW - k8s bring-up, parallels phase-65-up.ps1)

**Analog:** `scripts/phase-65-up.ps1` (full, L1-85). **The port DELETES the hand-rolled NDJSON parse** and replaces it with `kubectl rollout status` (RESEARCH "Don't Hand-Roll" L384). Structure to carry from the analog:
- The `$ErrorActionPreference = 'Stop'` + `Write-Host`-with-color + fail-loud `exit 2` idiom (L19, throughout).
- The **canonical service list** (L26-27) - reuse the same 10 tiers, but wait via `kubectl -n skp rollout status`.
- The 180s deadline concept (L31) -> per-tier `--timeout=` values (ES 240s, rabbitmq 180s cold starts).
- The otel-collector special-case (L57-61: "treat running as ready") is **eliminated** - it now gets a real `httpGet :13133` readiness probe so `rollout status` is meaningful.

**Analog excerpt (the loop the port replaces):**
```powershell
# phase-65-up.ps1 L37-73 - per-service NDJSON-per-replica health poll -> REPLACED BY kubectl rollout status
foreach ($svc in $services) {
    ...
    $instances = @(docker compose ps $svc --format json 2>$null | ... | ForEach-Object { $_ | ConvertFrom-Json })
    # keeper/processor replicas:2 => require ALL healthy   -> rollout status is replica-aware automatically
    ...
}
```
**New structure (RESEARCH Pattern 4, L269-311):** (1) `docker compose down` first - Pitfall 5 host-port mutual exclusion; (2) `kubectl apply -k k8s/`; (3) phased `kubectl -n skp rollout status statefulset/... deployment/...` infra then apps; (4) **`kubectl rollout restart` after build** - Pitfall 2 SourceHash currency (`:local`+IfNotPresent won't redeploy on same tag); (5) start 6 `kubectl port-forward` background procs bound to `127.0.0.1` at the exact harness ports (RESEARCH Pattern 5 L293-308): `8080->8080, 9090->9090, 9200->9200, 15673->15672, 6380->6379, 5433->5432`.

---

### k8s reset re-target - `scripts/phase-80-reset.ps1` (NEW sibling) OR parameterize the existing

**Analog:** `scripts/phase-65-reset.ps1` (full) + harness STEP B1 (L224-226) + STEP D (L262-263). **This is the single largest landmine (RESEARCH Pitfall 1, L426-447):** the compose reset reaches Postgres/Redis via the **docker/compose CLI**, which cannot see k8s pods. Every `docker exec`/`docker compose exec` touch-point must become `kubectl exec`. Concrete 1:1 swaps:

| compose analog (file:line) | exact line | k8s re-target |
|----------------------------|------------|---------------|
| reset STEP 1 (L59) | `docker exec sk-redis redis-cli FLUSHALL` | `kubectl -n skp exec statefulset/redis -- redis-cli FLUSHALL` |
| reset STEP 2 (L79) | `docker exec sk-redis redis-cli --scan --pattern 'skp:proc:*'` | `kubectl -n skp exec statefulset/redis -- redis-cli --scan --pattern 'skp:proc:*'` |
| reset STEP 3 (L99-102) | `docker compose exec -T postgres psql -U postgres -d stepsdb -c "BEGIN; DELETE FROM step_next_steps; ... COMMIT;"` | `kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -c "BEGIN; ...same static SQL... COMMIT;"` |
| reset STEP 0/4 (L45,L115) | `docker compose ps postgres/processor-sample --format json` | `kubectl -n skp get pods -l app=postgres` / `-l app=processor-sample` |
| reset STEP 4 (L126-129) | `docker ps --filter name=sk-processor-badconfig` + `docker rm` | **omit** - no badconfig pod exists in k8s (excluded, D-04) |
| harness STEP B1 (L225) | `docker compose restart orchestrator` | `kubectl -n skp rollout restart deployment/orchestrator` + `kubectl -n skp rollout status deployment/orchestrator --timeout=120s` (preserves the ghost-cron clean-window: HydrationBackgroundService re-runs on the fresh pod against an empty parent index) |
| harness STEP D (L262-263) | `docker compose exec -T postgres psql ... "SELECT id FROM workflows WHERE name='v8-fanout-proof'"` | `kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"` |

**Preserve verbatim:** the FK-safe DELETE order (L99-102, migration Down() order), the 60s heal-wait bounded poll (L72-89), the static-literal SQL (threat T-65-06/T-67-01 - no interpolated input), all fail-loud `exit 2` semantics. The seeder (`~FanOutSeeder`, harness STEP C L255) + analyzer + `POST /start` (STEP E) already use `localhost` ports and are covered by port-forward - **no change**.

---

## Shared Patterns

### Service-DNS connection strings (port BYTE-FOR-BYTE)
**Source:** compose env blocks (orchestrator L197-212, keeper L246-250, processor L302-306, baseapi L366-376)
**Apply to:** all 4 app Deployments
```
RabbitMq__Host: rabbitmq
ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"
ConnectionStrings__Postgres: "Host=postgres;Port=5432;Database=...;..."
```
Bare compose service names resolve identically via k8s Service DNS within the `skp` namespace (CONTEXT L98). Copy the literal strings; do NOT rewrite host or port.

### Dev-only Secret refs (D-07)
**Source:** `.env` + compose guest/guest env
**Apply to:** postgres SS, rabbitmq SS, and all 4 app Deployments (creds via `secretKeyRef: {name: skp-dev-secrets, ...}`). baseapi's Postgres string uses the `$(VAR)` dependent-env pattern (RESEARCH Pattern 3) - 3 Secret refs listed *before* the composed value.

### Test-only seam handling (D-08)
**Apply to:** keeper, processor-sample, orchestrator. Two rules:
1. **OMIT ENTIRELY** (absent => code `:-0` default => OFF): `KEEPER_DEFEAT_REINJECT`, `PROCESSOR_DEFEAT_READ`, `KEEPER_REINJECT_DELAY_MS`, `PROCESSOR_STEP_DELAY_MS` (+ `ORCH_OUTPUT_TTL`, `KEEPER_RECOVERY_TTL`, `K_EXECUTIONS` as bare seams).
2. **Carry the resolved default as a LITERAL** (these bind over appsettings at runtime): `Processor__ExecutionDataTtl: "900"`, `Orchestrator__OutputDataTtlSeconds: "900"`, `Recovery__ExecutionDataTtlSeconds: "900"`.

### Probe decoupling on HARD-on-broker apps (Pitfall 3)
**Apply to:** orchestrator, keeper, processor-sample, baseapi. Readiness = `httpGet /health/ready :<8081/8083/8082/8080>` (gates traffic); liveness = `tcpSocket :<port>` with generous `initialDelaySeconds` >=45 (Kestrel binds before the broker, so a slow 40s rabbitmq never CrashLoops the pod). Probe timing per RESEARCH arithmetic table (L407-418).

### Image + rollout currency (D-04/D-05, Pitfall 2)
**Apply to:** all 4 app Deployments. `image: <name>:local` + `imagePullPolicy: IfNotPresent` (NOT `:latest` => avoids ImagePullBackOff). Because the tag never changes, the bring-up MUST `kubectl rollout restart` after `phase-80-build.ps1` to force pods onto fresh bits.

### Ordering / no depends_on (RESEARCH Pattern 4)
**Apply to:** everything. Compose `depends_on: {condition: service_healthy}` blocks have NO k8s equivalent - **drop them all**; recreate the ordering intent by phasing the apply + `rollout status` in `phase-80-up.ps1`.

---

## No Analog Found

| File | Role | Reason |
|------|------|--------|
| `k8s/00-namespace.yaml` | Namespace | Pure k8s boilerplate (namespace `skp`, D-02) - no compose analog; copy from RESEARCH L136. |

Everything else has an exact source-of-truth analog in `compose.yaml`, `.env`, the two config files, or an existing script.

---

## Metadata

**Analog search scope:** `compose.yaml` (full, 11 services), `.env`, `prometheus.yml`, `compose/otel-collector-config.yaml`, `scripts/phase-65-up.ps1`, `scripts/phase-65-reset.ps1`, `scripts/phase-67-harness.ps1` (STEP A0/A/B/B1/C/D), `**/Dockerfile` (5 found, 4 built).
**Files scanned:** 8 source-of-truth files + 5 Dockerfiles.
**No `k8s/` dir or `.yaml` manifests pre-exist** (confirmed) - this is a novel-file-type port; analogs are the ported-from sources, not prior manifests.
**Pattern extraction date:** 2026-07-18
</content>
</invoke>
