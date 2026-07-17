# Phase 80: Deploy full system to local Kubernetes (Docker Desktop) — Context

**Gathered:** 2026-07-18
**Status:** Ready for planning

<domain>
## Phase Boundary

Port the existing 11-service docker-compose topology (`compose.yaml`) to Kubernetes manifests, run the full stack **Healthy** on a local Docker Desktop cluster, and prove **one workflow round-trips end-to-end** — a k8s deployment-target proof paralleling the compose stack, verified from the **same Prometheus + Elasticsearch signals** as the compose sweep.

**Fixed by the roadmap goal (NOT open to discussion — these are the phase's invariants):**
- Replica topology: orchestrator ×1 (documented SPOF — in-memory L1 pause/resume + Quartz scheduling; keep at 1), keeper ×2, processor-sample ×2, baseapi-service ×1, otel-collector ×1, prometheus ×1, elasticsearch ×1, redis ×1, postgres ×1, rabbitmq ×1.
- App tiers → **Deployments**; stateful backing services (postgres / redis / elasticsearch / rabbitmq) → **StatefulSets**.
- **NO change** to the two-consumer recovery architecture or any app source code. This is a packaging/deployment port only.
- Preserve: OTLP→collector wiring; Redis/RabbitMQ/Postgres connection strings via k8s Service DNS; compose healthchecks → readiness/liveness probes; the processor SourceHash reseed step (graph-delete → seed → 204); ALL test-only env seams shipped DISABLED / default-off.

</domain>

<decisions>
## Implementation Decisions

### Manifest tooling & layout
- **D-01:** Use **raw kubectl YAML manifests** — one file per tier — under a new `k8s/` directory at the repo root. NOT Helm, NOT Kustomize overlays. Rationale: exactly one deploy target (local Docker Desktop); overlay/chart indirection pays off across environments that don't exist here, and the proof's value is being transparent and diff-able against `compose.yaml`.
- **D-02:** Deploy everything into a dedicated namespace **`skp`** (mirrors the `sk-` container-name cross-stack uniqueness convention).
- **D-03:** Optionally include a single `kustomization.yaml` that **only lists resources + stamps the namespace/common labels** (no templating) as an ordered-apply convenience (`kubectl apply -k k8s/`). This is a convenience aggregator, not an overlay structure.

### Image build & SourceHash currency
- **D-04:** Build the 5 app images locally with `docker build` using an explicit **`:local` tag** (NOT `:latest` — avoids Always-pull semantics) and set **`imagePullPolicy: IfNotPresent`** on all app Deployments. Docker Desktop's kubelet reads the same local image store the daemon writes — no registry required. App images: `Dockerfile` (baseapi-service), `src/Orchestrator/Dockerfile`, `src/Keeper/Dockerfile`, `src/Processor.Sample/Dockerfile`. (Processor.BadConfig is profile-gated and OUT of the default proof — exclude it, mirroring the compose default profile.)
- **D-05:** **Preserve the build-before-seed ordering (compose STEP A0).** A new build step must `docker build` all app images BEFORE the reset+seed runs. The container's assembly-embedded SourceHash must equal the seeder's host-built `Processor.Sample.dll` SourceHash; a stale image → different processor id → `ProcessorLivenessValidator` 422s the `POST /start`. The guarantee is identical to compose — only the build command changes (`docker build` per image instead of `docker compose build`).

### Config & secrets
- **D-06:** `compose/otel-collector-config.yaml` and `prometheus.yml` (bind-mounted in compose) → **ConfigMaps**, mounted as files at the same in-container paths. Drop-in replacement for the bind-mounts.
- **D-07:** Postgres creds (`POSTGRES_DB`/`USER`/`PASSWORD` from `.env`) + RabbitMQ `guest/guest` → a **committed dev-only Kubernetes Secret** manifest, clearly labeled dev-only. Connection strings reference it via `secretKeyRef`. Rationale: these are the same dev creds already in `.env`/`compose.yaml`; the compose comments explicitly anticipate the k8s-Secret path (T-19-broker-creds, T-28-06, T-34-07); a committed Secret keeps the deploy one-command and reproducible.
- **D-08:** The 7 test-only env seams (`KEEPER_DEFEAT_REINJECT`, `PROCESSOR_DEFEAT_READ`, `KEEPER_REINJECT_DELAY_MS`, `ORCH_OUTPUT_TTL`, `KEEPER_RECOVERY_TTL`, `PROCESSOR_STEP_DELAY_MS`, `K_EXECUTIONS`) are **omitted entirely** from the committed manifests. Absent ⇒ the code's `:-0` / `:-900` default ⇒ OFF. This is the cleanest expression of "shipped disabled / default-off." The load-bearing production defaults that MUST be preserved as explicit env (they bind over appsettings at runtime): `Processor__ExecutionDataTtl: "900"`, `Orchestrator__OutputDataTtlSeconds: "900"` (default), `Recovery__ExecutionDataTtlSeconds: "900"` (default) — carry these as literals, same as compose.

### Persistence posture
Reconciles "StatefulSets with persistence" (roadmap) with the compose reality. A StatefulSet grants stable identity/DNS; a PVC is a **separate per-service choice**:
- **D-09:** **postgres** → StatefulSet **+ PVC (~1Gi)**. Real L3 state; mirrors the compose `pgdata` named volume.
- **D-10:** **elasticsearch** → StatefulSet **+ PVC (~2Gi)**. Stable data path; protects the run's log evidence across a pod reschedule (the verification reads ES). Deliberate divergence from compose's ephemeral ES, justified by run-evidence stability.
- **D-11:** **rabbitmq** → StatefulSet **+ PVC (~1Gi)**. Durable queues/mnesia survive a pod reschedule. Deliberate divergence from compose's ephemeral broker.
- **D-12:** **redis** → StatefulSet **WITH NO PVC** (ephemeral). Persistence is off *by design* (compose `--save "" --appendonly no`, Phase 12 D-03 — L2 is rebuildable from L3 on demand; RDB/AOF are dead weight). Carry the `--save "" --appendonly no` command args into the StatefulSet. Honoring the documented intent, NOT blindly applying "with persistence."
- **D-13:** **prometheus** and **otel-collector** → **Deployments**, ephemeral (no PVC). Not in the roadmap's StatefulSet list; app-ish singletons; compose ran them without persistence.

### Reseed + end-to-end proof mechanism
- **D-14:** **Reuse the existing host PowerShell harness unchanged.** Expose the k8s services via **`kubectl port-forward` at the exact `localhost` ports the harness already hard-codes**: WebApi `8080`, Prometheus `9090`, ES `9200`, RabbitMQ-mgmt `15673`, plus redis `6380`→6379 and postgres `5433`→5432 (so `phase-65-reset.ps1`'s FLUSHALL + FK-safe graph DELETE reach them). Services stay **`ClusterIP`**. Rationale: port-forward hits the ports the harness expects with zero URL edits and no Service-type changes; NodePort would force port remapping + harness edits.
- **D-15:** The **new deliverable is a k8s bring-up script paralleling `scripts/phase-65-up.ps1`** (apply manifests → wait all service types Healthy/Ready → start the port-forwards). Then the proven chain runs AS-IS: `phase-65-reset.ps1` → `~FanOutSeeder` test (seed) → `POST /orchestration/start` requiring 204 → `phase-68-sweep.ps1` (verify from Prometheus + ES). Phase 80 is a deployment-target swap, NOT a harness rewrite.
- **D-16:** **Proof scope = happy-path only.** The Phase 80 bar is: full stack reports Healthy on Docker Desktop k8s AND one workflow round-trips end-to-end (seed → 204 → sweep PASS from Prometheus + ES). Fault-injection scenarios stay a compose concern (already proven in v8.0.0). No fault re-run required on k8s for this phase.

### Claude's Discretion
- Exact probe wiring per tier (readiness/liveness `httpGet` on the health ports the compose healthchecks already use: baseapi 8080, orchestrator 8081, processor-sample 8082, keeper 8083, prometheus 9090 `/-/healthy`, postgres/redis/rabbitmq via exec probes matching the compose healthcheck commands). **otel-collector** has no in-container HTTP probe (distroless, no shell — compose dropped its healthcheck); use a **TCP-socket readiness probe** on `:4317` (or `:8889`/`:13133`) or treat `running` as ready, mirroring the harness's `phase-65-up.ps1` handling.
- Probe timing (initialDelay/period) translated from compose `start_period`/`interval`/`retries` — notably ES needs a generous `initialDelaySeconds` (~60s cold-start, RESEARCH Pitfall 6) and rabbitmq ~40s.
- PVC sizes (the ~1–2Gi figures are starting points), storageClass (default Docker Desktop `hostpath`), resource requests/limits, and label/selector conventions.
- File naming/grouping within `k8s/` and the exact structure of the bring-up + build scripts.
- Whether the ES/Prometheus/otel config env (`ES_JAVA_OPTS`, `discovery.type=single-node`, `xpack.security.enabled=false`, etc.) lives inline in the manifest or a ConfigMap — carry the compose values verbatim.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Source-of-truth topology (the thing being ported)
- `compose.yaml` — the complete 11-service docker-compose topology. Every service's image, env, ports, healthcheck, depends_on, command args, and the inline design-rationale comments are the authoritative spec for the k8s port. Read in full.
- `.env` — dev creds (`POSTGRES_DB`/`USER`/`PASSWORD`) referenced by compose interpolation; source for the k8s Secret.

### Config files (→ ConfigMaps)
- `compose/otel-collector-config.yaml` — otel-collector pipeline config (OTLP receivers, ES + Prometheus exporters, `mapping.mode`); bind-mounted at `/etc/otel-collector-config.yaml` in compose → ConfigMap.
- `prometheus.yml` — Prometheus scrape config (scrapes `otel-collector:8889` every 15s); bind-mounted at `/etc/prometheus/prometheus.yml` in compose → ConfigMap.

### App image Dockerfiles (built locally, referenced by tag)
- `Dockerfile` — baseapi-service (webapi) multi-stage image.
- `src/Orchestrator/Dockerfile` — orchestrator console (aspnet:8.0 runtime; embedded Kestrel health on :8081).
- `src/Keeper/Dockerfile` — keeper console (health on :8083).
- `src/Processor.Sample/Dockerfile` — processor-sample console (health on :8082; carries the assembly-embedded SourceHash).

### Reseed + verification harness (reused as-is; the k8s bring-up parallels these)
- `scripts/phase-65-up.ps1` — compose bring-up + health-wait model to parallel for k8s (10 service types, replica-aware NDJSON parse, otel-collector `running`=ready special-case).
- `scripts/phase-65-reset.ps1` — FLUSHALL Redis + heal-wait + FK-safe Postgres graph DELETE (the "graph-delete" step; hits redis 6380 + postgres 5433).
- `scripts/phase-67-harness.ps1` — the reseed→activation harness: STEP A0 build (SourceHash currency) → STEP B reset → STEP C seed (`~FanOutSeeder`) → STEP E `POST /orchestration/start` require 204. The ordering and exit-code contract to preserve.
- `scripts/phase-68-sweep.ps1` — the Prometheus + ES verification sweep that emits the PASS/FAIL verdict.

### Architecture (do NOT modify — port only)
- `.planning/ROADMAP.md` §"Phase 80" — the phase goal + fixed replica topology.
- `src/Keeper/Recovery/ReinjectConsumer.cs` and the two-consumer recovery design — read-only context; NO source changes in this phase.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **The entire compose sweep harness is reusable unchanged** (D-14/D-15): `phase-65-reset.ps1`, `phase-67-harness.ps1`, `phase-68-sweep.ps1`, and the `~FanOutSeeder` test. Only the bring-up (`phase-65-up.ps1`) needs a k8s sibling.
- **Health-wait pattern** from `phase-65-up.ps1` (180s deadline, per-replica NDJSON parse, otel-collector `running`=ready) translates to a `kubectl`/`kubectl wait` equivalent.
- **5 Dockerfiles already exist and are unchanged** — the k8s port builds the same images.

### Established Patterns
- **Service-DNS connection strings** — compose uses bare service hostnames (`redis`, `rabbitmq`, `postgres`, `otel-collector`) which resolve via compose DNS. In k8s these resolve identically via Service DNS (same short names within the `skp` namespace) — the connection strings port over BYTE-FOR-BYTE (`redis:6379`, `http://otel-collector:4317`, `Host=postgres;Port=5432`, `RabbitMq__Host: rabbitmq`).
- **Ports are container-internal** — compose publishes host ports (5433, 6380, 8080, etc.) but the *internal* container ports (5432, 6379, 8080…) are what k8s Services target. The host-port collision-avoidance (6380/5433/5673) is a compose-only concern; in k8s it re-emerges only at the `kubectl port-forward` layer (D-14).
- **Multi-replica services** (keeper ×2, processor-sample ×2) have NO `container_name` in compose (a named container can't scale) — trivially a Deployment `replicas: 2` in k8s.
- **Test-only seams are `${VAR:-default}` gated** — absent env ⇒ safe default (D-08).

### Integration Points
- **OTLP telemetry:** every app sets `OTEL_EXPORTER_OTLP_ENDPOINT: http://otel-collector:4317` → the collector exports to ES (`elasticsearch:9200`) + Prometheus scrapes the collector's `:8889`. This chain must survive the port verbatim.
- **Host verification surface:** the sweep reads WebApi `:8080`, Prometheus `:9090`, ES `:9200`, RabbitMQ-mgmt `:15673` — exposed via port-forward at the same localhost ports (D-14).

</code_context>

<specifics>
## Specific Ideas

- Namespace confirmed: **`skp`**.
- ES + RabbitMQ **do** get PVCs (user confirmed run-evidence/queue stability over faithful-ephemeral parity).
- Proof bar confirmed: **happy-path round-trip only** — no fault scenario re-run on k8s.
- `processor-badconfig` is EXCLUDED from the default k8s proof (mirrors compose's `profiles: ["badconfig"]` default exclusion).

</specifics>

<deferred>
## Deferred Ideas

- **Fault-scenario re-run on k8s** — proving the 7 fault scenarios (processor/orchestrator/keeper/redis/rabbitmq/redis+rabbitmq crash) recover on the k8s target. Out of scope for Phase 80 (happy-path bar); already proven on compose in v8.0.0. Could be a follow-up phase if k8s becomes a first-class target.
- **Registry-based image distribution / multi-node cluster** — this phase targets single-node Docker Desktop with local image store only.
- **Overlay/Helm packaging for multiple environments** — raw manifests chosen deliberately (D-01); revisit only if a second deploy target appears.

</deferred>

---

*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Context gathered: 2026-07-18*
