# Roadmap: Steps API

## Milestones

- ✅ **v3.2.0 Steps API MVP** — Phases 1-11 (shipped 2026-05-28) — see [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md)
- ✅ **v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline** — Phases 12-16 (shipped 2026-05-29) — see [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md)
- ✅ **v3.4.0 BaseConsole + Orchestrator Messaging** — Phases 17-24 + 24.1 (shipped 2026-06-01) — see [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md)
- ✅ **v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip** — Phases 25-30 (shipped 2026-06-02)
- ✅ **v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip** — Phases 31-32.1 (shipped 2026-06-05) — see [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md)
- ✅ **v3.7.0 Keeper — L2-Outage Dead-Letter Recovery & Workflow Pause/Resume** — Phases 33-42 (shipped 2026-06-07) — see [milestones/v3.7.0-ROADMAP.md](milestones/v3.7.0-ROADMAP.md)
- ✅ **v4.0.0 Processor Pre/In/Post-Process + Keeper Recovery Redesign** — Phases 43-49 (shipped 2026-06-11) — see [milestones/v4.0.0-ROADMAP.md](milestones/v4.0.0-ROADMAP.md)
- ✅ **v5.0.0 Recovery Re-architecture — messageId slot-array + 3-state keeper** — Phases 50-55 (shipped 2026-06-12) — supersedes v4.0.0 Model-B recovery; source of truth [`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`](../docs/design/2026-06-08-processor-keeper-recovery-redesign.md) A18 + A19
- ✅ **v6.0.0 Config & Payload Validation Hardening** — Phases 56-58 (shipped 2026-06-13) — typed base-config seam on `BaseProcessor` + startup config-schema compatibility **Gate A** (withholds processor *Healthy* on config-type↔config-schema mismatch); complements the shipped WebAPI **Gate B** (`PayloadConfigSchemaValidator`); 10/10 CFG requirements, Phase-58 live close gate N=3 GREEN; see [milestones/v6.0.0-ROADMAP.md](milestones/v6.0.0-ROADMAP.md)
- ✅ **v7.0.0 Per-Replica Processor Liveness & Self-Watchdog** — Phases 59-62 + 62.1 (closed 2026-06-14, audit-override) — per-instance L2 liveness keys `skp:proc:{processorId}:{instanceId}` + instance-index SET (replacing single `skp:{processorId}`), two-state `status`+per-schema `summary` written by both startup+heartbeat loops, in-memory L1 record, WebAPI ≥1-healthy orchestration-start gate, self-watchdog probe; 17 KEY/STATE/LOOP/L1/GATE/PROBE reqs implemented & hermetically green; **Phase-62 live close gate NOT run (deferred — superseded by v8.0.0)**; see [milestones/v7.0.0-ROADMAP.md](milestones/v7.0.0-ROADMAP.md)
- ✅ **v8.0.0 E2E Resilience Proof** — Phases 63-68 (shipped 2026-06-15) — whole-system live recovery proof of the fan-out workflow `A→B→C→{D1→E1→F1, D2→E2→F2}` (9 steps, one shared `processor-sample`, cron `*/30 * * * * *`) under 7 sustained 5-minute fault scenarios; **zero-missing + effect-once** verified **solely from Prometheus + Elasticsearch** (NOT the prior triple-SHA infra net-zero close gate); 7-scenario capstone **7/7 PASS** (TEST-06 clean after a config-only TTL fix — `Processor__ExecutionDataTtl` 5→300); supersedes v7.0.0's deferred Phase-62 live proof; 23/23 CRON/PROC/WF/ENV/OBS/FAULT/TEST reqs; see [milestones/v8.0.0-ROADMAP.md](milestones/v8.0.0-ROADMAP.md)
- ⚠️ **v9.0.0 Canonical Two-Consumer Recovery & L2 Delivery Proof** — Phases 69-81 (complete 2026-07-18) — **unarchived (documented debt):** its `REQUIREMENTS.md` was overwritten when v10.0.0 began, so there is no `milestones/v9.0.0-*` archive; phases 69-81 live in git history + the v10.0.0-ROADMAP snapshot. Reconstruct/close separately if a formal archive is later needed.
- ✅ **v10.0.0 Orchestrator High Availability** — Phases 82-83 (shipped 2026-07-19) — orchestrator runs N≥2 replicas on k8s with Kubernetes-Lease single-leader mutual exclusion; leader-gated scheduler fire, `attributes.role` observability, **HA-07 proven LIVE** (leader force-killed at replicas:3 → verdict Pass, zero duplicate triggers, recovery 15.44s); HA-01…HA-07; see [milestones/v10.0.0-ROADMAP.md](milestones/v10.0.0-ROADMAP.md)
- ✅ **v11.0.0 Framework Data-Channel & Processor Boundary Hardening** — Phases 84-85 (COMPLETE 2026-07-22 — ready for `/gsd-complete-milestone`) — foundational widening of the framework data channel from `string` to `byte[]` (Phase 84 ✅ shipped, verified SOLELY by the 7-scenario sweep reproducing all-PASS), then processor↔framework boundary hardening (Phase 85): fail-loud `SpawnToPost` + nack-on-spawn-exhaust (ack only when all spawns succeed) + framework-owned entry deletion with the concrete `DeleteEntry` surface removed; DATA-01…05, PB-01…03. **Kafka import/export (KIMP-01…06, MANIP-01, KEXP-01) deferred to a future milestone** — the byte[] channel is its prerequisite, now shipped.
- ✅ **v12.0.0 Resilience & Health-Probe Hardening** — Phase 86 (complete 2026-07-26) — refactor console + webapi health probes so infrastructure failures (Postgres/Redis/RabbitMQ) never crash or restart pods (only a genuine watchdog-loop stall restarts), via one shared `BaseConsole` liveness-watchdog primitive + a hard readiness latch (no self-heal) + Redis-on-readiness + processor identity/schema-on-readiness + new k8s startupProbes. Design spec: [docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md](../docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md)

## ✅ v12.0.0 Resilience & Health-Probe Hardening (Complete — 2026-07-26)

**Milestone Goal:** Make every service tolerate infrastructure outages without crash-looping. A live experiment proved keeper + processor **crash-loop** under an unreachable RabbitMQ because their self-watchdog is tagged `"live"` — an infra fault trips *liveness* (restart) instead of only *readiness*. This milestone corrects the category error: `/health/live` answers only "is my watchdog loop still turning?" (restart is the sole remedy for a silently-dead loop); `/health/ready` answers "are my required infra deps reachable + am I bootstrapped?" (NotReady, never restart); and recovery from a sustained outage is by **operator restart, not self-heal** (a hard readiness latch). One shared `BaseConsole.Core` primitive replaces the two divergent per-service watchdogs (orchestrator stays self-only — it has no beatable loop). The WebApi keeps its own `BaseApi.Core` pattern.

**Source of truth:** the approved design spec [docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md](../docs/superpowers/specs/2026-07-26-console-health-liveness-refactor-design.md) (produced via a full brainstorming session, 2026-07-26 — its locked decisions are authoritative and not to be re-litigated). Phases continue at **86** (v11.0.0 ended at 85).

### Phases

- [x] **Phase 86: Console & WebApi Health/Liveness Refactor** ✅ 2026-07-26 — `/health/live` = watchdog-loop liveness only (restart on silent stall); infra failures (pg/redis/rabbitmq) stay on `/health/ready`, never crash or restart, always logged; one shared opt-in `BaseConsole` liveness primitive (keeper + processor beat, orchestrator self-only); a hard readiness latch (no self-heal, migration one-shot); Redis added to readiness; processor identity+schema on readiness; k8s startupProbes added to all four.

### Phase Details

#### Phase 86: Console & WebApi Health/Liveness Refactor

**Goal**: Console (orchestrator/keeper/processor) and WebApi health probes are refactored so an infrastructure outage (Postgres/Redis/RabbitMQ) never crashes the process nor restarts the pod — only a genuinely stalled watchdog loop restarts. `/health/live` checks only watchdog-loop liveness via one shared `BaseConsole.Core` primitive (`ILivenessHeartbeat` + `LoopLivenessHealthCheck`, opt-in — keeper + processor beat at the top of their loop before any infra I/O; orchestrator stays `self`-only, no watchdog); the two divergent per-service watchdogs are retired. `/health/ready` reflects each service's REQUIRED infra deps plus, for the processor, identity+schema resolution — NotReady but never restart, with Redis newly included and the baseapi bus staying capped at Degraded (publish-only/soft). "No self-heal" is enforced by a hard readiness latch (once a required dep fails for its failureThreshold window, `/health/ready` latches Unhealthy until process restart; migration stays one-shot). Infra exceptions are always caught + logged (never crash), including `ProcessorStartupOrchestrator`'s identity/schema loop. A `startupProbe → /health/startup` is added to all four k8s deployments (currently absent), with startup-gate marking per service (orchestrator=hydration, keeper/processor=first beat, baseapi=migrations).
**Depends on**: — (first phase of v12.0.0; builds on the shipped v10.0.0 k8s deployment + the v7.0.0 per-replica liveness watchdog it supersedes)
**Requirements**: HLTH-01, HLTH-02, HLTH-03, HLTH-04, HLTH-05, HLTH-06, HLTH-07, HLTH-08
**Success Criteria** (what must be TRUE):

  1. `/health/live` returns healthy iff the service's watchdog loop has beaten within `k × interval` (default k=3); a genuinely stalled/dead loop goes stale → restart. An infra outage does NOT trip `/health/live`. (HLTH-01)
  2. A Postgres/Redis/RabbitMQ outage never crashes the process nor restarts the pod: the fault is caught + logged on every loop — including `ProcessorStartupOrchestrator`'s identity/schema loop — and a live broker-outage leaves all four services at `RESTARTS 0`. (HLTH-02)
  3. One shared `BaseConsole.Core` primitive (`ILivenessHeartbeat` + `LoopLivenessHealthCheck`) is opt-in per service; keeper + processor beat at the top of their loop before any infra I/O (keeper's `BitHealthLoop` edge bus-ops no longer block the beat); orchestrator stays `self`-only. `KeeperLivenessWatchdogHealthCheck` + `BaseProcessor` `LivenessWatchdogHealthCheck` are retired; the processor beats unconditionally (the "only when Healthy" gate stays only on the L2 write). (HLTH-03, HLTH-06)
  4. `/health/ready` = each service's required infra deps plus, for the processor, identity+schema resolved; NotReady never restarts; Redis is included for all services; the baseapi bus stays capped at Degraded (publish-only) and is NOT latched. (HLTH-04, HLTH-08)
  5. No self-heal: once a required dep fails for its readiness `failureThreshold` window, `/health/ready` latches Unhealthy until process restart; migration stays one-shot. (HLTH-05)
  6. Startup-gate marking is per service (orchestrator=hydration complete, keeper/processor=first watchdog beat, baseapi=migrations) and a `startupProbe → /health/startup` is added to all four k8s deployments (30/31/32/33-*.yaml). (HLTH-07)
  7. Live proof: repeat the broker-unreachable experiment — all four services stay running (`RESTARTS 0`), go NotReady, and show logs; recovery happens only on restart. [verification gate]

**Plans**: 9 plans (waves 0-4)

  - [x] 86-01-PLAN.md — Wave 0 RED: BaseConsole.Core primitive skeletons + failing hermetic tests (liveness heartbeat/loop check, Redis-ready, latch)
  - [x] 86-02-PLAN.md — Wave 0 RED: processor identity/schema readiness skeleton + HLTH-02 resilience lock + retire the two watchdog-only test files
  - [x] 86-03-PLAN.md — Wave 1: implement shared liveness watchdog (ILivenessHeartbeat + LoopLivenessHealthCheck, k=3) + AddConsoleLivenessWatchdog opt-in seam
  - [x] 86-04-PLAN.md — Wave 1: implement Redis-ready + sticky latch; wire latched bus+Redis readiness into the embedded listener for all consoles
  - [x] 86-05-PLAN.md — Wave 1: mirror Redis-ready + latch into BaseApi.Core (Postgres+Redis latched; bus stays soft), fix stale D-06 comment
  - [x] 86-06-PLAN.md — Wave 2: keeper — top-of-tick beat + bounded edge bus-ops + first-beat MarkReady; delete keeper watchdog+state
  - [x] 86-07-PLAN.md — Wave 2: processor — unconditional beat above the gate + first-beat MarkReady + identity/schema readiness; delete BaseProcessor watchdog
  - [x] 86-08-PLAN.md — Wave 3: add startupProbe -> /health/startup to all four k8s manifests
  - [x] 86-09-PLAN.md — Wave 4: terminal gate — full hermetic green + startupProbe grep + live broker-unreachable proof (RESTARTS 0)

## 🚧 v11.0.0 Kafka Binary Import/Export (In Progress — started 2026-07-21)

**Milestone Goal:** Add Kafka-sourced binary file import to the pipeline, built on a foundational widening of the framework data channel from `string` to `byte[]`. Two phases, dependency-ordered: a **framework-only `byte[]` widening** (Phase 84) makes `DataResult.Data` carry raw bytes as the ground-truth type — JSON/UTF-8 becomes an optional schema-driven lens (schema present → decode UTF-8 + validate as JSON; schema absent → opaque bytes), base64 eliminated end-to-end — decoupled from Kafka and touching `Messaging.Contracts` + `BaseProcessor.Core` + Keeper recovery. Its verification gate is **ONLY the existing 7-scenario fault-recovery sweep (TEST-01..TEST-07, `scripts/phase-68-sweep.ps1`) reproducing its all-PASS baseline** (every `Missing==0`, every `Duplicates==0`) — byte-for-byte equivalence of the existing string/JSON path proven by the green sweep IS the whole acceptance bar; no new test suite gates the widening. Then a **`KafkaImporter` processor** (Phase 85): a Mode-2 entry step triggered by the scheduler with `executionId == Guid.Empty` that pull-consumes ≤N binary files per dispatch from a configured topic + consumer group, mints an `executionId` per file, writes the bytes **directly to L2** (data never rides the bus — hand-off messages stay small JSON), and **commits each Kafka offset per-message only after that file's store + hand-off is confirmed** (fail-loud, replacing `SpawnToPost`'s best-effort swallow). Kafka runs as a **standalone container** (not a k8s workload), reached from within the cluster via `host.docker.internal:9092`.

**Source of truth:** This milestone's planning conversation (2026-07-20) + [PROJECT.md](PROJECT.md) + [REQUIREMENTS.md](REQUIREMENTS.md) (5 DATA + 6 KIMP requirements). Builds on the v10.0.0 k8s deployment. Phases continue at **84** (v10.0.0 ended at 83).

**Build order (locked, dependency-driven — the byte[] channel is the prerequisite the importer writes into):** **84** `byte[]` data-channel widening (framework contract change, verified by the existing sweep) → **85** KafkaImporter processor (Mode-2 pull-per-dispatch entry step over the widened channel).

**Deferred to a FUTURE milestone (NOT phases here):** a **file-manipulator step** (MANIP-01 — read the imported binary from L2, transform e.g. unzip → operate on the XML/WAV, emit output) and a **KafkaExporter terminal step** (KEXP-01 — L2 → ZIP → produce to a topic), plus a Kafka topic-partition-offset idempotency key for true exactly-once import dedup. This milestone is import-only; at-least-once is accepted (per-message commit minimizes but does not eliminate the crash-between-handoff-and-commit duplicate window).

### Phases

- [x] **Phase 84: `byte[]` Data-Channel Widening** — Widen `DataResult.Data` from `string` to `byte[]` as the ground-truth type (JSON/UTF-8 an optional schema-driven lens, base64 gone); framework-only, decoupled from Kafka; acceptance = the existing 7-scenario fault-recovery sweep reproducing its all-PASS baseline. **✅ COMPLETE 2026-07-21 — sweep 7/7 PASS live on k8s (byte84 images), DATA-01…05 satisfied, verification 8/8.**
- [x] **Phase 85: Processor↔Framework Boundary Hardening** — ✅ COMPLETE 2026-07-22 (SC-4 sweep 7/7 PASS live on k8s, verification 8/8). Make the fan-out hand-off fail-loud so a seeded execution is never silently dropped, and move entry-lifecycle ownership fully into the base: `SpawnToPost` signals its outcome; the pipeline nack-requeues the entry on a spawn send-exhaustion (ack only when all spawns succeed); entry deletion becomes framework-owned on all paths and the concrete `DeleteEntry` surface is removed. Framework-only + `Processor.Sample`; **no Kafka** (KafkaImporter/container deferred).

### Phase Details

#### Phase 84: `byte[]` Data-Channel Widening

**Goal**: The framework data channel carries `byte[]` as its ground-truth type end-to-end — `DataResult.Data` becomes `byte[]` so binary payloads flow through L2 write/read, the processor seam, and Keeper recovery without base64 encoding or string coercion; a configured JSON input/output schema is a *lens* over those bytes (decode UTF-8 + validate as JSON when present, opaque pass-through when absent), not a type switch. This is a foundational framework-contract change in `Messaging.Contracts` + `BaseProcessor.Core` + Keeper recovery, deliberately decoupled from Kafka. **Its acceptance gate is SOLELY the existing 7-scenario fault-recovery sweep reproducing its all-PASS baseline** — byte-for-byte equivalence of the existing string/JSON path proven by the green sweep IS the proof the change altered nothing observable; no new test suite is the phase's acceptance gate.
**Depends on**: — (first phase of v11.0.0; builds on the shipped v10.0.0 framework + the v8.0.0 fan-out sweep harness)
**Requirements**: DATA-01, DATA-02, DATA-03, DATA-04, DATA-05
**Success Criteria** (what must be TRUE):

  1. `DataResult.Data` carries `byte[]` as its ground-truth type — binary payloads flow through the framework (L2 write/read, the processor seam, recovery) with no base64 encoding or string coercion anywhere on the hot path. (DATA-01)
  2. When a JSON input/output schema is configured the framework interprets the `byte[]` as UTF-8 JSON and validates it against the schema; when no schema is configured the bytes are treated as an opaque payload — schema is a lens over the bytes, not a type switch. (DATA-02)
  3. The existing string/JSON processing path is byte-for-byte equivalent after the change — a value written as a string equals its UTF-8 bytes read back — with no author-visible behavioral change (`Processor.Sample` stays green). (DATA-03)
  4. **The existing 7-scenario fault-recovery sweep (TEST-01..TEST-07, `scripts/phase-68-sweep.ps1`) reproduces its all-PASS baseline — every `Missing==0`, every `Duplicates==0` — and this green sweep is the SOLE verification gate for the widening (no new test suite gates the phase).** (DATA-04)
  5. Recovery (`KeeperInject` / `OrchestratorInject`) carries `byte[]` Data through the recovery backup without loss — MassTransit base64s the field transiently on the RabbitMQ wire only, while the L2 hot path stays binary. (DATA-05)

**Plans**: 3 plans (waves 1-3)

  - [ ] 84-01-PLAN.md — Flip DataResult.Data to byte[] + D-02 edge helpers; migrate production consumers (seam, pipeline read, schema decode, both concrete processors) to a green src build
  - [ ] 84-02-PLAN.md — Migrate the hermetic test compile-breaks + add A1 (STJ byte[] round-trip) & A2 (bad-UTF8-under-schema → Failed) facts; zero new failures vs baseline
  - [ ] 84-03-PLAN.md — Terminal gate: rebuild + run the 7-scenario fault-recovery sweep detached, reproduce all-PASS (SOLE phase verdict)

#### Phase 85: Processor↔Framework Boundary Hardening

**Goal**: Harden the processor↔framework boundary so a fan-out hand-off can never silently drop a seeded execution, and entry-lifecycle ownership lives entirely in the base. Three changes: **(1)** `SpawnToPost` becomes **fail-loud** — it signals its spawn outcome to the caller (a transient send-exhaustion is reported via return value or a propagating exception) instead of swallowing the exhaustion as silent success (`OnSpawnDropped` telemetry + the deterministic-fault throw preserved); **(2)** the pipeline **nack-requeues** the entry dispatch on a spawn send-exhaustion, so a Mode-2 entry is acked ONLY when every spawn succeeded — replacing the best-effort "the scheduler re-fires the whole entry" swallow with an at-least-once broker guarantee (the pipeline's `ExecuteAsync` catch-all must let a spawn-exhaust signal escape as a nack instead of converting it to `StepFailed`+ack); **(3)** entry/source deletion becomes **framework-owned on every completion path** (the Mode-1 inline tail *and* the Mode-2 null-return path, keeper DELETE escalation preserved), and the concrete `DeleteEntry` API + the `SampleProcessor` call + the seam plumbing that existed solely for it (`SeamState.EntryId`, the `EscalateDelete` hook) are removed. Net boundary: the concrete processor's only job is *interpret input → business logic → return `DataResult`* (or spawn N + return null for a fan-out source). Framework-only + `Processor.Sample`; **no Kafka** (KafkaImporter/container/`NextStepHandoff` widening deferred to a future Kafka milestone).
**Depends on**: Phase 84 (the byte[] channel; and the entry/fan-out mechanics the boundary cleanup targets)
**Requirements**: PB-01, PB-02, PB-03
**Success Criteria** (what must be TRUE):

  1. `SpawnToPost` is fail-loud — a transient send-exhaustion is signaled to the caller (return value or propagating exception), not swallowed as success; the `OnSpawnDropped` telemetry hook and the deterministic-fault throw are preserved. (PB-01)
  2. A Mode-2 entry step acks its dispatch only when every spawn succeeded; a spawn send-exhaustion nack-requeues the entry (broker redelivery) so the whole seed re-fires — no seeded execution is silently dropped. A hermetic fact proves a defeated spawn nack-requeues rather than acks. (PB-02)
  3. Entry/source deletion is framework-owned on every completion path (Mode-1 + Mode-2 null-return) with the keeper DELETE escalation preserved; the concrete `DeleteEntry` API, its `SampleProcessor` call, and its seam plumbing (`EntryId`, `EscalateDelete`) are removed — entry deletion still happens, via the base. (PB-03)
  4. The existing 7-scenario fault-recovery sweep still reproduces its all-PASS baseline (no happy-path regression — `Processor.Sample` is what the sweep drives). [verification gate, alongside the PB-02 fail-loud hermetic fact]

**Plans**: 4 plans (waves 1-4)
Plans:
**Wave 1**

- [x] 85-01-PLAN.md — PB-01 fail-loud SpawnToPost: add SpawnSendExhaustedException + flip transient-exhaust swallow→telemetry-then-throw; invert the seam fact + deterministic-negative fact (PB-01)

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 85-02-PLAN.md — PB-02 nack: narrow `catch (SpawnSendExhaustedException){throw;}` between the ProcessStatusException and generic catches + D-05 nack fact + explicit D-03 negative control (PB-02)

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 85-03-PLAN.md — PB-03 framework-owned delete: remove DeleteEntry/EntryId/EscalateDelete + SampleProcessor call; move delete-then-escalate onto the null path (IsSource skip); flip/remove/re-express the DeleteEntry tests in lockstep (PB-03)

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 85-04-PLAN.md — SC-4 terminal gate: full hermetic suite green + detached 7-scenario phase-68 sweep reproducing all-PASS (Missing==0, Duplicates==0); operator-confirmed (PB-01, PB-02, PB-03)

## ✅ v7.0.0 Per-Replica Processor Liveness & Self-Watchdog (CLOSED 2026-06-14 — audit-override; full record [milestones/v7.0.0-ROADMAP.md](milestones/v7.0.0-ROADMAP.md))

> **Closed 2026-06-14 (audit-override).** Phases 59–61 + 62.1 implemented & hermetically green (17 functional reqs). **Phase 62 (live proof + triple-SHA close gate) was authored but NOT run** — deferred and superseded by **v8.0.0 (E2E Resilience Proof)**, whose broader live fault-injection E2E subsumes it. Detail below retained as the v7.0.0 design record.

**Milestone Goal:** Make processor liveness multi-replica-accurate and self-healing. Replace the single last-write-wins L2 liveness key `skp:{processorId}` with **per-instance keys** `skp:proc:{processorId}:{instanceId}` under a per-processor **instance-index SET** `skp:proc:{processorId}` (TTL = source of truth; index = discovery hint, lazily `SREM`'d when stale). The liveness value gains a **two-state `status`** (`healthy`/`unhealthy`) + a per-schema **`summary`** (`{inputSchema, outputSchema, configSchema ∈ SUCCESS|FAIL}`; any FAIL ⇒ unhealthy; `configSchema` reuses v6.0.0 Gate A's outcome) and is written by **BOTH** the startup loop (from its first iteration — so L2 reflects a *restarting* replica as `unhealthy`, never absent) and the heartbeat loop (timestamp refresh only; health frozen `healthy` once heartbeat starts, monotonic-within-process, reset-on-restart). An **in-memory L1 liveness record** is added (updated by both loops). The WebAPI orchestration-start gate becomes a **≥1-healthy-and-fresh** check across replicas (`SMEMBERS` the index → `GET` each per-instance key → pass iff ≥1 present + `status=healthy` + non-stale `timestamp + interval×2 > now`; present-but-unhealthy/stale fails *that* replica; lazy-`SREM` absent members). A **self-watchdog liveness probe** reads the L1 record's staleness (active-interval ×2 grace) so a silently-crashed loop ⇒ stale L1 ⇒ probe fails ⇒ (future) K8s pod restart. The `inputDefinition`/`outputDefinition` write-only dead weight is **dropped from L2** (the per-instance value is liveness-only). **Breaking change** to the processor liveness contract (keyspace + wire shape + WebAPI gate logic). Phases continue at **59**; builds directly on v6.0.0 Gate A.

**Source of truth:** This milestone's planning conversation (2026-06-13), design confirmed point-by-point — per-instance keys + index, definitions dropped, unhealthy-is-written, frozen-healthy, self-watchdog. Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (17 reqs across KEY/STATE/LOOP/L1/GATE/PROBE).

**Build order (locked, dependency-driven — the keyspace/value reshape is the prerequisite foundation):** 59 (L2 per-instance keyspace + two-state value reshape) → 60 (dual-loop writer + in-memory L1 + unhealthy-is-written) → 61 (WebAPI ≥1-healthy gate + self-watchdog probe) → 62 (live proof + close gate).

### Phases

- [ ] **Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value** — Reshape the L2 liveness contract to per-instance keys + an instance-index SET, with a two-state `status` + per-schema `summary` value (definitions dropped).
- [x] **Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record** — Startup + heartbeat loops both write the (per-instance, TTL'd) entry to L2 + L1 each iteration; startup writes `unhealthy`; split startup/heartbeat intervals; frozen-healthy. ✓ All 4 plans complete; dual-loop writer + L1 holder wired + DI gap closed; Phase=60 17/17 GREEN.
- [x] **Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe** — WebAPI gate iterates the instance index and admits iff ≥1 replica is healthy-and-fresh (lazy-prune stale); the processor's liveness probe fails on stale L1.
- [ ] **Phase 62: Live Proof & Close Gate** — Real-stack E2E proof of the reshaped per-replica liveness + the triple-SHA `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` net-zero close gate (N=3 GREEN).

### Phase Details

#### Phase 59: Per-Instance L2 Keyspace & Two-State Liveness Value

**Goal**: The L2 processor-liveness contract is reshaped from the single last-write-wins key `skp:{processorId}` to **per-instance keys** `skp:proc:{processorId}:{instanceId}` discovered through a per-processor **instance-index Redis SET** `skp:proc:{processorId}`, and the per-instance value becomes a **liveness-only** record carrying a two-state `status` (`healthy`/`unhealthy`) + a per-schema `summary` (`{inputSchema, outputSchema, configSchema ∈ SUCCESS|FAIL}`; any FAIL ⇒ unhealthy; `configSchema` sourced from v6.0.0 Gate A, null-is-skip). `instanceId` is the existing pod identity (`POD_NAME → HOSTNAME → MachineName → GUID`). `inputDefinition`/`outputDefinition` are dropped from L2. This is the prerequisite foundation the dual-loop writer (Phase 60) and the WebAPI gate (Phase 61) both consume.
**Depends on**: — (first phase of v7.0.0; builds on the shipped v6.0.0 Gate A outcome + the v5.0.0 `L2ProjectionKeys`)
**Requirements**: KEY-01, KEY-02, KEY-03, KEY-04, STATE-01, STATE-02
**Success Criteria** (what must be TRUE):

  1. `L2ProjectionKeys` exposes a per-instance liveness key builder `skp:proc:{processorId}:{instanceId}` plus the per-processor instance-index SET key `skp:proc:{processorId}` (golden-test-pinned), replacing the single `skp:{processorId}` key.
  2. `instanceId` resolves from the existing `POD_NAME → HOSTNAME → MachineName → GUID` chain (reused, no new mechanism), and is the discriminator that makes two replicas of the same processor write to two distinct keys (no cross-replica overwrite).
  3. The per-instance liveness value is liveness-only — it carries `status ∈ {healthy, unhealthy}` and a `summary` `{inputSchema, outputSchema, configSchema}` each `SUCCESS|FAIL` (any FAIL ⇒ `status=unhealthy`; null schema id ⇒ not-failing); `inputDefinition`/`outputDefinition` no longer appear in the L2 value (a serialization/shape test confirms their absence).
  4. The `configSchema` summary field is derived from the v6.0.0 Gate A startup config-compat outcome (not recomputed), and a null `ConfigSchemaId` is treated as not-failing (null-is-skip), consistent with input/output schemas.
  5. Solution builds 0-warning (Release + Debug); the hermetic suite is green against the reshaped contract.

**Plans**: 2 plans (Wave 1, parallel - zero file overlap)

- [x] 59-01-PLAN.md - Value contract: LivenessStatus.Unhealthy + SchemaOutcome SoT + ProcessorLivenessEntry record + Create factory + shape/invariant tests (KEY-04, STATE-01, STATE-02)
- [x] 59-02-PLAN.md - Keyspace + identity: PerInstance/InstanceIndex key builders + shared InstanceId.Resolve() SoT + golden/resolver tests (KEY-01, KEY-02, KEY-03)

#### Phase 60: Dual-Loop Writer + In-Memory L1 Liveness Record

**Goal**: The processor's startup loop and heartbeat loop **both** write the per-instance liveness entry to L2 **and** update an in-memory **L1 liveness record** on every iteration, and the startup loop writes its entry as `unhealthy` from its first iteration (so a starting/failed replica is visible in L2 as `unhealthy`, never absent). Each replica `SADD`s its own `instanceId` to the instance-index SET on its first liveness write; each per-instance key carries a TTL (the source-of-truth liveness signal). Liveness intervals split into `startup_interval` (startup cadence) and `heartbeat_interval` (heartbeat cadence), each entry records its active interval, and health is **frozen `healthy`** once the heartbeat loop starts (timestamp-only refresh thereafter — monotonic within a process, reset on restart). Builds on the Phase 59 keyspace/value shape.
**Depends on**: Phase 59 (the per-instance key + two-state value shape must exist before the loops can write to it)
**Requirements**: STATE-03, LOOP-01, LOOP-02, LOOP-03, LOOP-04, L1-01
**Success Criteria** (what must be TRUE):

  1. The startup loop writes the replica's liveness entry to **both** L2 (the per-instance key) and the in-memory L1 record on every iteration, with `status=unhealthy` and a `summary` reflecting current schema-resolution progress (stays `unhealthy` until identity + all non-null schemas resolve) — a restarting replica is observable in L2 as `unhealthy`, never absent.
  2. On its first liveness write each replica `SADD`s its own `instanceId` to `skp:proc:{processorId}` (mirroring the Phase-22 workflow parent-index discipline), and each per-instance key is written with a TTL so a dead replica's key TTL-expires (the index SET is only a discovery hint).
  3. On startup success the heartbeat loop starts; each heartbeat iteration refreshes the entry's timestamp in **both** L2 and L1, with health **frozen `healthy`** (no mid-life re-validation — monotonic within a process, reset on restart).
  4. Liveness intervals are split into a `startup_interval` and a `heartbeat_interval` (the `Ttl` knob retained), and each liveness entry records its active interval so downstream staleness math can adapt to which loop wrote it.
  5. The in-memory L1 liveness record (`timestamp`, active `interval`, `status`, `summary`) is updated by BOTH loops on every iteration and is the single source the self-watchdog probe (Phase 61) reads.

**Plans**: 4 plans

- [x] 60-01-PLAN.md - Config interval split (StartupInterval=30, Interval/Ttl retained) + lock-free L1 holder + binding/state tests (LOOP-03, L1-01)
- [x] 60-02-PLAN.md - Shared internal writer: L2 SET(perInstance, TTL=max(interval*2,Ttl)) + index SADD + L1 Update + log-and-continue; writer facts (LOOP-03, LOOP-04) ✓ ProcessorLivenessWriter (public sealed) + 5 ProcessorLivenessWriterFacts; Phase=60 12/12 GREEN, Release+Debug 0/0 warnaserror; LOOP-03/04; commits 70d0ff0, 9d8cdb8
- [x] 60-03-PLAN.md - Heartbeat swap to per-instance frozen-healthy entry via shared writer; remove old ProcessorProjection write; re-point heartbeat facts (LOOP-02, LOOP-04) ✓ frozen-healthy beat via ProcessorLivenessWriter, old flat-key dual-write removed (D-05); heartbeat facts re-pointed to PerInstance+entry+index+L1; Phase=60 15/15 GREEN, Release+Debug 0/0 warnaserror; LOOP-02/04; commits 348bf89, 421823f
- [x] 60-04-PLAN.md - Orchestrator inline unhealthy write per iteration + DI registration + 3 ctor-site facts + AddBaseProcessor descriptors + StartupUnhealthyWriteFacts (STATE-03, LOOP-01) ✓ WriteUnhealthyAsync at Loop A/B/Gate-A-clash via shared writer (D-02 guarded, interval 30); L1 holder + writer singletons; orchestrator+heartbeat concrete-singleton hosted services (instanceId via ActivatorUtilities, DI gap closed); StartupUnhealthyWriteFacts 2/2; Phase=60 17/17 GREEN, Release+Debug 0/0 warnaserror; STATE-03/LOOP-01; commits 6f9270b, 48f2024, 199d2da

#### Phase 61: ≥1-Healthy Orchestration-Start Gate + Self-Watchdog Probe

**Goal**: Two reader-side consumers of the reshaped keyspace land. (a) The **WebAPI orchestration-start gate** discovers a processor's replicas via `SMEMBERS skp:proc:{processorId}`, `GET`s each per-instance key, and admits the processor iff **≥1** replica is **present AND `status=healthy` AND non-stale** (`timestamp + interval×2 > now`) — a present-but-unhealthy or stale replica fails *that* replica (presence no longer implies live); when none qualify, orchestration start is blocked **422 + RFC 7807**, and an absent/TTL-expired index member is skipped and lazily `SREM`'d (self-healing). (b) The **self-watchdog liveness probe** reads the in-memory L1 record and reports `unhealthy` when the L1 timestamp is stale beyond the active-interval ×2 grace (detecting a silently-crashed startup/heartbeat loop while the host stays up), returning the per-schema `summary` in its body. K8s probe wiring is future; this delivers the probe semantics.
**Depends on**: Phase 60 (the gate reads the per-instance keys + index the loops populate; the probe reads the in-memory L1 record the loops maintain)
**Requirements**: GATE-01, GATE-02, GATE-03, PROBE-01, PROBE-02
**Success Criteria** (what must be TRUE):

  1. The orchestration-start validator discovers replicas by `SMEMBERS skp:proc:{processorId}` and reads each per-instance key with no prior knowledge of instanceIds (replacing the single-key `present ⟺ live` read).
  2. A processor passes the gate iff **≥1** replica is present AND `status=healthy` AND non-stale (`timestamp + interval×2 > now`); a present-but-`unhealthy` replica and a stale replica each fail *that* replica (a single healthy-and-fresh replica admits the workflow even when siblings are unhealthy/stale).
  3. When no replica satisfies the gate, orchestration start is blocked with **422 + RFC 7807** (genuine Redis faults still surface as 500, not 422); an absent/TTL-expired index member is skipped and lazily `SREM`'d from the index.
  4. The processor's liveness probe reads the in-memory L1 record and reports `unhealthy` when the L1 timestamp is stale beyond the active-interval ×2 grace — a silently-crashed startup or heartbeat loop makes the probe fail while the host process stays up.
  5. The probe returns the per-schema `summary` in its response body (so the future K8s restart trigger has the diagnostic it needs).

**Plans**: 3 plans

- [x] 61-01-PLAN.md - Swap validator to SMEMBERS->GET-each >=1-healthy gate + aggregate 422 reason + D-11 legacy teardown + re-point gate tests (GATE-01/02/03) ✓ per-replica gate live; ProcessorProjection/Processor builder+forwarder deleted (LivenessProjection untouched); Phase=61 unit + re-pointed real-Redis facts green; Debug+Release 0-warning; commits 35dec7e, 6b62128, 222cc35
- [x] 61-02-PLAN.md - Self-watchdog LivenessWatchdogHealthCheck + generic HealthCheckDescriptor seam + AddBaseProcessor registration + unit test (PROBE-01/02) ✓ watchdog reads L1 (null/stale->Unhealthy, summary in Data); generic HealthCheckDescriptor seam folded by embedded listener; Phase=61 unit green; Debug+Release 0-warning; commits d750465, 5483150
- [x] 61-03-PLAN.md - Processor /health/live Generic-Host integration proof: null/stale Unhealthy, fresh Healthy + summary in body (PROBE-01/02) ✓ AddBaseProcessor surfaces watchdog on embedded /health/live end-to-end (null/stale->503, fresh->200+summary, no-secrets); Phase=61 integration 4/4 green; Release 0-warning; commits 3001dec, 47f256c

**UI hint**: yes

#### Phase 62: Live Proof & Close Gate

**Goal**: A real-stack end-to-end proof that the reshaped per-replica liveness works live — two replicas of a processor each self-register a per-instance key under the instance index; a restarting replica is visible as `unhealthy`; orchestration start admits a workflow when ≥1 replica is healthy-and-fresh and blocks 422 when none are; the self-watchdog probe fails on a silently-stale L1 — sealed behind the milestone close gate (triple-SHA `psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` BEFORE==AFTER net-zero, N=3 consecutive GREEN), mirroring shipped Phases 49/55/58.
**Depends on**: Phase 61 (the full per-replica liveness + gate + probe path must exist before it can be proven live and closed)
**Requirements**: TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):

  1. RealStack E2E: two replicas of one processor each write a distinct per-instance key `skp:proc:{processorId}:{instanceId}` and `SADD` themselves to the instance-index SET; a starting/failed replica is observable in L2 as `unhealthy` (never absent), and a dead replica's key TTL-expires + is lazily `SREM`'d. (TEST-01)
  2. RealStack E2E: orchestration start admits a workflow when **≥1** required-processor replica is healthy-and-fresh (even with an unhealthy/stale sibling) and is blocked **422 + RFC 7807** when no replica qualifies; the self-watchdog probe returns `unhealthy` + the per-schema `summary` when the in-memory L1 record is stale beyond the active-interval ×2 grace. (TEST-02)
  3. The milestone close gate holds — N=3 consecutive GREEN + triple-SHA (psql `\l` / redis-cli `--scan` / rabbitmqctl `list_queues`) BEFORE==AFTER net-zero, DLQ depth 0 — at Release + Debug 0-warning. (TEST-03)

**Plans**: 3 plans (2 waves)
Plans:

- [ ] 62-01-PLAN.md — compose processor-sample → deploy.replicas:2 (D-01) + retag SC1/SC2/SC3 + GateAComposition to Phase 62 (D-10) (TEST-01, TEST-03)
- [ ] 62-02-PLAN.md — fabricated-key gate-verdict RealStack tests (≥1-healthy admit / 422-when-none / malformed→422) + SeedFabricatedLivenessAsync helper (D-04) (TEST-01, TEST-02)
- [ ] 62-03-PLAN.md — clone phase-62-close.ps1 (D-09 ^skp:proc: prefix exclusion) + 62-HUMAN-UAT.md runbook + autonomous build gate; operator-gated live N=3×GREEN close run (D-08/12/14/15) (TEST-01, TEST-02, TEST-03)

## ✅ v6.0.0 Config & Payload Validation Hardening (SHIPPED 2026-06-13)

**Milestone Goal:** Guarantee that any processor which reaches *Healthy* can deserialize every orchestration-admitted payload — by giving authors a **typed base-config seam** on `BaseProcessor` (the framework deserializes the dispatch `payload` into the author's typed config, replacing the raw-string `ProcessAsync(string validatedData, string payload)` seam at `BaseProcessor.cs:29`) and gating processor health on a startup **config-schema↔config-type compatibility check (Gate A)**. On a Gate A incompatibility the processor withholds `MarkHealthy` (the latch is one-way — `ProcessorContext.cs:83-89`), so its liveness heartbeat no-ops (`ProcessorLivenessHeartbeat.cs:70`), no `skp:{id}` L2 key is written, and the existing orchestration-start `ProcessorLivenessValidator` blocks any workflow using that processor (422). Config incompatibility is **terminal** (permanent, not retry-transient like a missing definition); a null `ConfigSchemaId` skips Gate A (null-is-skip). Complements the already-shipped WebAPI **Gate B** (`PayloadConfigSchemaValidator`, `payload ⊨ ConfigSchemaId` at orchestration start, RETAINED UNCHANGED). Net effect: `payload ⊨ ConfigSchemaId` (Gate B) ∧ `ConfigSchemaId ⊨ configType` (Gate A) ⟹ the delivered payload always deserializes — runtime payload-deserialization exceptions become impossible except for in-transit payload mutation. **Breaking change** to the `BaseProcessor` author contract (like v4.0.0's Pre/In/Post seam break). Phases continue at **56**.

**Source of truth:** Locked Gate A / Gate B analysis (this milestone's planning conversation, 2026-06-12). Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (10 CFG reqs). Three open decisions are deliberately deferred to `/gsd-spec-phase`: (1) Gate A compatibility direction/fidelity; (2) terminal-unhealthy mechanics (the latch is binary — likely "withhold Healthy + a separate diagnostic"); (3) TOCTOU policy (CFG-10 — immutability vs re-validate-on-change).

**Build order (locked, dependency-driven — the config-seam is the prerequisite that makes Gate A possible):** 56 (typed base-config seam) → 57 (startup config-schema fetch + Gate A + TOCTOU policy) → 58 (orchestration-gate integration proof + live close).

### Phase 62.1: Decouple liveness refresh from IsHealthy (INSERTED)

**Goal:** Decouple the processor liveness-timestamp refresh from `IsHealthy` so an alive-but-unhealthy replica (Gate-A clash) stays observably Unhealthy (never absent) and is not falsely restarted by the self-watchdog. Concretely: replace the Gate-A-clash terminal `return;` in `ProcessorStartupOrchestrator` with a cancellation-safe refresh loop that re-stamps the same static unhealthy entry (config=Fail) with a fresh timestamp every IntervalSeconds (10s) until shutdown. Fixes gap G-62-01; restores STATE-03 (observable-unhealthy-not-absent) and PROBE-02 (no false self-watchdog restart). Hermetic test coverage only — the live TEST-01b/TEST-01c re-proof is handed back to a Phase-62 close-gate re-run (D-04).
**Requirements**: G-62-01 (restores STATE-03, PROBE-02, LOOP-02; re-enables TEST-01)
**Depends on:** Phase 62
**Plans:** 2 plans (2 waves)

Plans:

- [x] 62.1-01-PLAN.md — Product fix: parameterize WriteUnhealthyAsync recorded-interval + replace the Gate-A-clash `return;` with a cancellation-safe IntervalSeconds-cadence unhealthy-refresh loop (G-62-01, STATE-03, PROBE-02) ✅ 2026-06-13 (b1ede90, 35e6d71)
- [x] 62.1-02-PLAN.md — Hermetic ClashRefreshFacts: re-SET-each-interval + TTL-reset + L1-advance, watchdog-verdict-unchanged (reads "live"), clean shutdown (G-62-01, STATE-03, PROBE-02, TEST-01) ✅ 2026-06-14 (1458e80, 083945e)

#### Phase 56: Typed Base-Config Seam

**Goal**: Processor authors declare configuration as a typed class inheriting a framework-provided base config; the framework deserializes the dispatch `payload` into that typed config and supplies it to the author's transform — replacing the raw-string `payload` parameter. `Processor.Sample` is the migrated worked example. This is the prerequisite seam that makes a config-type↔config-schema compatibility check (Gate A, Phase 57) possible.
**Depends on**: — (first phase of v6.0.0; builds on the shipped v5.0.0 `BaseProcessor` pipeline)
**Requirements**: CFG-01, CFG-02
**Success Criteria** (what must be TRUE):

  1. A processor author can declare its configuration as a typed class inheriting a framework-provided base config, and override the transform to receive that typed config instance (no raw `payload` string in the author seam).
  2. The framework deserializes the dispatch `payload` into the author's typed config before invoking the transform; a payload that does not deserialize into the config type surfaces as a deterministic failure (not a silent corruption).
  3. `Processor.Sample` is migrated to the typed-config seam as the worked example — the old raw-string `ProcessAsync(string validatedData, string payload)` deserialize is removed (clean break), and `Processor.Sample` still completes a round-trip.
  4. Solution builds 0-warning (Release + Debug); the hermetic suite is green with the new seam.

**Plans**: 2 plans (2 waves)

- [x] 56-01-PLAN.md — Framework typed-config seam (empty marker + shared options + generic deserialize layer) + Processor.Sample clean-break migration (CFG-01, CFG-02)
- [x] 56-02-PLAN.md — Hermetic suite migration to the typed seam + deser-failure StepFailed fact + 0-warning dual-config build & full-suite gate (CFG-01, CFG-02)

#### Phase 57: Startup Config-Schema Fetch + Gate A

**Goal**: At startup the processor fetches the `ConfigSchemaId` definition (extending Loop B at `ProcessorStartupOrchestrator.cs:124`, lifting the D-05 "never read the config schema id" carve-out) and stores it on `ProcessorContext`; Gate A then validates that the concrete config type *covers* that definition. On incompatibility the processor never reaches Healthy (withholds `MarkHealthy`, terminal — not retried); a missing definition stays transient (retry, boot-before-register); a null `ConfigSchemaId` skips Gate A. The spec-locked TOCTOU policy (immutability or re-validate-on-change) closes the schema-mutation window between this startup check and a later orchestration-start Gate B check.
**Depends on**: Phase 56 (the typed config seam must exist before its type can be compared against the config-schema definition)
**Requirements**: CFG-03, CFG-04, CFG-05, CFG-06, CFG-07, CFG-10
**Success Criteria** (what must be TRUE):

  1. When `ConfigSchemaId` is non-null, startup fetches the config-schema definition over the bus and stores it on the processor context; a missing definition is transient (the startup loop retries on `SchemaDefinitionNotFound`/timeout exactly as for input/output definitions — boot-before-register tolerated).
  2. Gate A validates that the concrete config type covers the fetched config-schema definition (every payload valid under `ConfigSchemaId` deserializes into the config type — direction/fidelity locked during spec).
  3. On a Gate A incompatibility the processor never reaches Healthy — `MarkHealthy` is withheld, the heartbeat no-ops (`ProcessorLivenessHeartbeat.cs:70`), no `skp:{id}` L2 key is written, the reason is logged, and the incompatibility is terminal (not retried like a missing definition).
  4. A processor with a null `ConfigSchemaId` skips Gate A entirely and reaches Healthy on identity + input/output definitions alone (null-is-skip, matching `ProcessorStartupOrchestrator.cs:127-128` and `PayloadConfigSchemaValidator.cs:42`).
  5. The config-schema definition mutation window between the startup Gate A check and a later orchestration-start Gate B check is closed by the spec-locked TOCTOU policy (immutability or re-validate-on-change), with a test recording the chosen mechanism.

**Plans**: 4 plans
Plans:

- [x] 57-01-PLAN.md — Wave 0 (BLOCKING): STJ rule-table spike + RED test scaffolds (covers facts, freeze integration, inverted/extended harness facts)
- [x] 57-02-PLAN.md — Gate A covers-checker `ConfigSchemaCoverageCheck.Evaluate` (CFG-05/07)
- [x] 57-03-PLAN.md — Wire Gate A: Loop B config fetch + ConfigDefinition + decoupled MarkReady/MarkHealthy (CFG-03/04/06/07)
- [x] 57-04-PLAN.md — Frozen-once-referenced schema Definition + 409 handler (CFG-10)

#### Phase 58: Orchestration-Gate Integration Proof & Close

**Goal**: A real-stack end-to-end proof that Gate A composes with the existing orchestration-start liveness gate — a config-incompatible (never-Healthy) processor blocks orchestration start with 422 via `ProcessorLivenessValidator` ("absent"), while a config-compatible processor reaches Healthy, writes its L2 liveness, and its orchestrations start normally (Gate A is not a false-positive blocker) — sealed behind the milestone close gate.
**Depends on**: Phase 57 (Gate A must withhold/grant Healthy before the orchestration-start gate integration can be proven end-to-end)
**Requirements**: CFG-08, CFG-09
**Success Criteria** (what must be TRUE):

  1. RealStack E2E: an orchestration whose graph includes a config-incompatible (never-Healthy) processor is blocked at orchestration start with 422 via the existing `ProcessorLivenessValidator` ("absent").
  2. RealStack E2E: a config-compatible processor reaches Healthy, writes its L2 liveness, and its orchestrations start normally — proving Gate A is not a false-positive blocker (the negative-control).
  3. The milestone close gate holds — N-consecutive-GREEN + triple-SHA (psql/redis/rabbitmq) BEFORE==AFTER net-zero — at Release + Debug 0-warning.

**Plans**: 5 plans (3 waves)

- [x] 58-01-PLAN.md - Processor.BadConfig project (clashing TConfig, distinct SourceHash) + SK_P.sln (CFG-08)
- [x] 58-02-PLAN.md - Two-schema GET-or-create seed helpers + compatible Sample seed path + SC1/2/3 retag Phase 58 (CFG-09)
- [x] 58-03-PLAN.md - Profile-gated processor-badconfig compose service + Gate-A composition E2E (CFG-08 clash-log+absent+422 / CFG-09 204) (CFG-08, CFG-09)
- [x] 58-04-PLAN.md - phase-58-close.ps1 (verbatim phase-55 triple-SHA + two-schema/two-processor CREATE-IF-ABSENT seed) (CFG-08, CFG-09) (completed 2026-06-13 — dual embedded-hash read Sample+BadConfig, two-schema/two-processor CREATE-IF-ABSENT seed never PUT, no badconfig SHA exclusion/liveness pre-flight, triple-SHA block verbatim; AST PARSE OK; 5e283bb)
- [x] 58-05-PLAN.md - 58-HUMAN-UAT.md operator runbook + live N=3 GREEN close gate (ticks CFG-08, CFG-09) (CFG-08, CFG-09) (completed 2026-06-13 — live N=3 close gate exit 0: triple-SHA BEFORE==AFTER psql ed52e389/redis e3b0c442/rabbitmq 88000972, 568 facts x3, skp-dlq-1 depth 0, skp:msg:* 0; CFG-08 three-signal 422 + CFG-09 204 both GREEN; mid-run fix bfa5a65 corrected the Gate-A clash-log ES query; 6faa169, bfa5a65, 52f2f81)

## 🚧 v5.0.0 Recovery Re-architecture — messageId slot-array + 3-state keeper (In Progress — started 2026-06-11)

**Milestone Goal:** Replace v4.0.0's keeper-owned composite-backup recovery (Model B — `UPDATE`/`CLEANUP`, 5 states) with a **processor-owned `messageId` slot-array** recovery model + a **3-state keeper** (`REINJECT`/`INJECT`/`DELETE`). Per-message `L2[messageId][x]=entryId` allocation index (allocation-before-data), retired to `guid.empty` only **after** a confirmed orchestrator send; split infra taxonomy (`infra_messageId`→drop / `infra_entryId`→`INJECT`); a recovery branch (`if exist L2[messageId]`) that re-sends completed + `REINJECT`s-without-deleting-source on any unverifiable entry; configurable **DLQ1-vs-sustained-outage** keeper exhaustion; **gate-closed non-destructive consume**. Retains the v4.0.0 BIT gate + global pause/resume (A14), four typed `Step*` records (A15), at-least-once/no-dedup (A16), single `skp-dlq-1` (A4). Breaking — supersedes the v4.0.0 recovery core.

**Source of truth:** [`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`](../docs/design/2026-06-08-processor-keeper-recovery-redesign.md) → "Recovery Re-architecture (A18)" (LOCKED 2026-06-11) + "Active Index GC (A19)" (LOCKED 2026-06-12 — active terminal index delete + atomic both-key keeper GC). Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (24 reqs).

**Build order (locked, build-before-teardown):** 50 (contracts + slot-array key reshape) → 51 (processor forward + recovery pipeline) → 52 (3-state keeper) → 53 (Model-B teardown) → 54 (terminal index delete + atomic keeper GC) → 55 (live proof + close gate).

#### Phase 50: Contracts & Slot-Array L2 Key Reshape

**Goal**: The new recovery vocabulary exists — the `L2[messageId][x]=entryId` slot-array allocation-index key builder is defined, the three surviving Keeper-state contracts (`REINJECT`/`INJECT`/`DELETE`) carry their A18 id sets (`INJECT` carries `data`+`deleteEntryId`, `REINJECT` carries source `entryId`+`Payload`, `DELETE` carries `entryId`), and the Model-B contracts (`UPDATE`/`CLEANUP` + composite backup key + `BackupOptions`) are removed at the contract level — solution buildable.
**Depends on**: — (first phase of v5.0.0; builds on shipped v4.0.0 contracts)
**Requirements**: RETIRE-01, RETIRE-02 (contract-level)
**Success Criteria**:

  1. `L2ProjectionKeys` exposes a slot-array index builder `L2[messageId][slot]` (golden-test-pinned) alongside the retained no-TTL GUID data key.
  2. `UPDATE`/`CLEANUP` contracts + the composite backup key `corr:wf:proc:exec` + `BackupOptions` are deleted; a source/reflection scan finds no references.
  3. `INJECT`/`REINJECT`/`DELETE` carry their A18 id sets in `Messaging.Contracts`.
  4. Solution builds 0-warning (Release + Debug); hermetic suite green.

**Plans**: 2 plans (2 waves)

- [x] 50-01-PLAN.md — Additive contract surface: MessageIndex slot-array key builder + golden pin + KeeperInject A18 id-set (RETIRE-02; no deletions)
- [x] 50-02-PLAN.md — Atomic Model-B teardown: delete UPDATE/CLEANUP/BackupOptions/composite key, re-home single-owner endpoint, stub survivors, reconcile test surface + reflection guard (RETIRE-01, RETIRE-02)

#### Phase 51: Processor Forward + Recovery Pipeline

**Goal**: `ProcessorPipeline` runs the slot-array forward pass (allocation-before-data, split infra, per-item dispatch, source-delete tail) and the `if exist L2[messageId]` recovery pass (temp-list, send-before-retire, `REINJECT`-no-source-delete), replacing the Model-B Post-Process backup/cleanup mechanics.
**Depends on**: Phase 50
**Requirements**: SLOT-01, SLOT-02, SLOT-03, INFRA-01, INFRA-02, FWD-01, FWD-02, FWD-03, RECOV-01, RECOV-02, RECOV-03
**Success Criteria**:

  1. Forward Post writes the allocation index before the data key; allocation-write exhaustion → `infra_messageId`→drop, data-write exhaustion → `infra_entryId`→keeper `INJECT`.
  2. Forward dispatch routes per item (non-infra→orchestrator / `infra_entryId`→`INJECT` / `infra_messageId`→drop); happy-path tail deletes the source `entryId` (exhaustion→`DELETE`).
  3. `NOT exist L2[messageId]` runs the forward pass; existence-check/source-read exhaustion → `REINJECT` (input intact).
  4. `exist L2[messageId]` runs the recovery pass — temp-list per slot, `completed`→re-send+retire(`guid.empty`), not-exist→drop, `infra_entryId`→preserve slot; any `infra_entryId`→`REINJECT` without deleting source, else delete source.
  5. Hermetic facts prove forward + recovery flows; solution 0-warning.

**Plans**: 3 plans

- [x] 51-01-PLAN.md — SlotArrayOptions (TTL random) record + DI bind + bind facts (Wave 0) ✓ SlotArrayOptions sealed class (300/600, D-04/D-05) + Configure<SlotArrayOptions>(Processor section) + 2 bind facts; ProcessorOptionsBindingFacts 4/4 GREEN, SK_P.sln Release 0/0; SLOT-01; commits 6433c27, 368b5e3
- [x] 51-02-PLAN.md — Pipeline dispatcher + FORWARD pass, BuildInject fix, WR-01 finally removal, messageId seam, forward facts (Wave 1) ✓ A18 thin dispatcher (exist L2[messageId] branch) + RunForwardAsync (allocation-before-data, split infra infra_messageId/infra_entryId, inline source-delete tail; WR-01 finally RETIRED) + BuildInject id-set fix + consumer messageId null fail-fast; Pipeline facts 26/26 GREEN, SK_P.sln Release+Debug 0/0; UseMessageRetry=keep-latch (none deferred to Phase 53); SLOT-01/02, INFRA-01/02, FWD-01/02/03; commits 5412ca6, c4df040, ec8567e
- [x] 51-03-PLAN.md — RECOVERY pass (temp-list, send-before-retire, REINJECT-no-source-delete), recovery + consumer facts (Wave 2)

#### Phase 52: 3-State Keeper

**Goal**: The Keeper recovery consumer applies the three surviving states gate-open-only — `REINJECT` (read source / re-inject with payload), `INJECT` (forward-only write→send→delete), `DELETE` — with gate-closed non-destructive consume and a configurable DLQ1-vs-sustained-outage exhaustion policy.
**Depends on**: Phase 51
**Requirements**: KEEP-01, KEEP-02, KEEP-03, KEEP-04, KEEP-05
**Success Criteria**:

  1. `REINJECT` reads source `entryId` (drops if absent) and re-injects a reconstructed `EntryStepDispatch` carrying `Payload`; `INJECT` writes `L2[entryId]=data`→sends `StepCompleted`→deletes `deleteEntryId`; `DELETE` deletes the key (drops if absent).
  2. The keeper performs an L2 op only when the BIT gate is open; gate-closed does not dequeue-and-drop (consumption pauses / requeues without ack).
  3. The exhaustion policy is config-driven: DLQ1 mode dead-letters to `skp-dlq-1`; sustained-outage mode holds/requeues for L2 recovery.
  4. Hermetic facts prove each state + the gate-closed and exhaustion-policy behaviors; solution 0-warning.

**Plans**: 3 plans

- [x] 52-01-PLAN.md — Three recovery-state bodies (REINJECT drop-flip, INJECT forward-only, DELETE verify) + base gate-wait strip + KeeperMetrics drop counter (Wave 1) ✓ completed 2026-06-11; KEEP-01/02/03; 16/16 Keeper facts, 518/518 hermetic, Release 0/0
- [x] 52-02-PLAN.md — keeper-recovery static→ConnectReceiveEndpoint conversion + handle singleton + configurable exhaustion policy (Dlq1 vs SustainedOutage) + integration facts (Wave 2) ✓ completed 2026-06-11; KEEP-04/05; 18/18 Keeper facts, 520/520 hermetic, Release 0/0; commits fd72d9d, 266b80c, 5f24bb9
- [x] 52-03-PLAN.md — BitHealthLoop endpoint Stop/Start driver on BIT health edges + driver facts + full keeper-suite green gate (Wave 3) ✓ completed 2026-06-11; KEEP-04 (end-to-end); BitHealthLoop now Stops the recovery endpoint on the unhealthy edge / Starts it on the healthy edge (additive to gate.Open/Close + PauseAll/ResumeAll, under WR-01); BitHealthLoopTests 8/8, Keeper namespace 32/32, solution Debug 0/0; commits 63bff7a, fed352e

#### Phase 53: Model-B Teardown

**Goal**: The v4.0.0 Model-B recovery surface is fully removed — composite backup key, `UPDATE`/`CLEANUP` consumers, and the 5-state consumer collapsed to the 3 surviving states — leaving the system buildable on the slot-array path alone.
**Depends on**: Phase 52
**Requirements**: RETIRE-03 (RETIRE-01/02 contract removal lands in Phase 50; remnant-verified here)
**Success Criteria**:

  1. The composite backup key + its TTL are gone (remnant-verified); `UPDATE`/`CLEANUP` consumers + definitions deleted.
  2. The recovery consumer registers exactly 3 states (`REINJECT`/`INJECT`/`DELETE`); a reflection/source sweep finds no Model-B remnant.
  3. Full hermetic suite green; solution 0-warning Release + Debug.

**Plans**: 3 plans (Wave 0 guards, then 2 parallel removal plans)

- [x] 53-01-PLAN.md — Standing guards FIRST (5→3 reflection + D-01/D-03/D-07 source-scan facts; land RED) (Wave 0)
- [x] 53-02-PLAN.md — Orchestrator retry teardown (strip UseMessageRetry + dead IOptions/Ignore<> from all 5 owners) (Wave 1)
- [x] 53-03-PLAN.md — Processor keep-latch removal + comment reconcile + D-03 ConfigureError→keeper move + SC-3 gate (Wave 1)

#### Phase 54: Terminal Index Delete + Atomic Keeper GC

**Goal**: The processor actively reclaims the `L2[messageId]` allocation index at end-of-message — the happy-path tail (forward + recovery all-clear) deletes BOTH the source `L2[entryId]` and the `L2[messageId]` index in one atomic multi-key `DEL`, escalating a delete exhaustion to a keeper `DELETE` that now carries `{messageId, entryId}` and deletes both keys atomically (persisting the index's TTL on escalate). Restores the v4 CLEANUP-grade deterministic net-zero that A18's TTL-only index reclaim gave up.
**Depends on**: Phase 53
**Requirements**: GC-01, GC-02, GC-03
**Design**: [`docs/design/2026-06-08-processor-keeper-recovery-redesign.md`](../docs/design/2026-06-08-processor-keeper-recovery-redesign.md) → "Active Index GC (A19)" (LOCKED 2026-06-12)
**Success Criteria**:

  1. The forward + recovery all-clear tails delete the source `entryId` AND the `messageId` index as a single atomic Redis multi-key `DEL`; the index is no longer left to its random TTL on a non-crash path.
  2. The terminal two-key delete is mutually exclusive with `REINJECT` — on any `infra_entryId`/`REINJECT` neither key is deleted (the index survives for the replay's recovery pass).
  3. A terminal-delete exhaustion `PERSIST`s the `L2[messageId]` index (cancels its TTL) and escalates to keeper `DELETE` carrying `{messageId, entryId}`; the `DELETE` consumer deletes both keys atomically (drop-on-absent).
  4. Hermetic facts prove the atomic two-key delete, the `REINJECT`-mutual-exclusion preservation, the persist-on-escalate, and the both-key keeper `DELETE`; solution 0-warning (Release + Debug).

**Plans**: 4 plans (3 waves)

- [x] 54-01-PLAN.md — Test-kit mock scaffolding: array `KeyDeleteAsync(RedisKey[])` overload + `KeyPersistAsync` stubs on every tail mux, array When/Do on fault muxes, new `ReadOkDeleteAndPersistFaultL2` persist-exhaust sibling (Wave 1) (GC-01/02/03 enabling)
- [x] 54-02-PLAN.md — Contract: `KeeperDelete.MessageId` init prop (D-05, additive) (Wave 1) (GC-03)
- [x] 54-03-PLAN.md — Production: unified `DeleteTerminalAsync` (atomic two-key DEL + best-effort persist-on-escalate + `KeeperDelete{messageId}` handoff, D-01/02/03/06) rewiring both tails + `BuildDelete(d, messageId)` + `DeleteConsumer` both-key DEL (Wave 2) (GC-01/02/03)
- [x] 54-04-PLAN.md — Facts: invert/harden `PipelineEndDeleteFacts` + `PipelineRecoveryFacts` + `DeleteConsumerFacts` to the atomic array shape (ONE array call + scalar `DidNotReceive`) + persist/MessageId asserts + new `EndDelete_PersistExhaust_StillSendsKeeper` + 0-warning dual-config gate (Wave 3) (GC-01/02/03, AC-1..10)

#### Phase 55: Live Proof & Close Gate

**Goal**: A real-stack E2E proves the slot-array forward + recovery passes, each keeper state, and the A19 active two-key index delete, sealed behind an N-consecutive-GREEN triple-SHA net-zero close gate.
**Depends on**: Phase 54
**Requirements**: TEST-01, TEST-02
**Success Criteria**:

  1. RealStack E2E proves the forward pass (dispatch→slot-array write→orchestrator advance) and the recovery pass.
  2. RealStack E2E proves each keeper state: `REINJECT` (source-present re-inject / source-absent drop), `INJECT`, `DELETE`.
  3. Close gate N×GREEN + triple-SHA (psql/redis/rabbitmq) BEFORE==AFTER net-zero — slot-array index + data keys leak-free, proven by the A19 active delete (not a TTL settle) — at Release+Debug 0-warning.

**Plans**: 4 plans
Plans:

- [x] 55-01-PLAN.md — clone phase-49 close gate → phase-55-close.ps1 (composite removed, skp:msg:* count==0 added) + operator runbook (TEST-02)
- [x] 55-02-PLAN.md — adapt SC1 (slot-array index + A19 net-zero), retag SC3, delete composite sweeps (TEST-01)
- [x] 55-03-PLAN.md — rewrite SC2 (3-state + both-key DELETE) + organic recovery test (TEST-01)
- [x] 55-04-PLAN.md — autonomous build gate (D-08) + operator-gated live N=3×GREEN close run (D-09)

## ✅ v3.7.0 Keeper — L2-Outage Dead-Letter Recovery & Workflow Pause/Resume (SHIPPED 2026-06-07)

Archived: [milestones/v3.7.0-ROADMAP.md](milestones/v3.7.0-ROADMAP.md) · [REQUIREMENTS](milestones/v3.7.0-REQUIREMENTS.md) · [AUDIT](milestones/v3.7.0-MILESTONE-AUDIT.md). 10 phases / 32 plans / 37 requirements satisfied + live-proven (Phase-39 close gate: 3×500 GREEN, triple-SHA net-zero; audit `tech_debt`, 0 functional blockers). Full milestone detail below (collapsed).

<details>
<summary>✅ v3.7.0 Keeper (Phases 33-42) — SHIPPED 2026-06-07</summary>

**Milestone Goal:** Make the autonomous execution loop (cron fire → dispatch → process → result → fan-out) **self-heal through transient L2 (Redis) outages without operator intervention.** A new multi-replica `Keeper` console reacts to the `Fault<EntryStepDispatch>` / `Fault<ExecutionResult>` events that the execution-path consumers publish on retry-budget exhaustion, probes L2 health on a bounded loop, pauses the affected workflow's cron (via the single-replica orchestrator's in-memory L1) so the outage stops spreading, re-injects recovered work to its origin (riding the receiver's existing `flag[H]` idempotency), resumes when no recoveries remain pending, and parks the genuinely unrecoverable in `keeper-dlq` for operator triage. Keeper is the automated operator for v3.6.0's accepted "an infra-faulting workflow keeps dead-lettering until an operator intervenes" gap. Operator commands (Start/Stop) are out of scope — a failed Start/Stop is simply re-issued.

**Build order (locked):** 33 (spike — de-risk the `Fault<T>`→reinject→`flag[H]`-collapse round-trip) → 34 (Keeper console foundation) → 35 (fault intake + correlation) → 36 (L2 probe loop + two DLQs) → 37 (orchestrator pause/resume) → 38 (metrics + real-stack E2E + close gate).

- [x] **Phase 33: Fault-Recovery Spike (de-risk)** — Prove `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` consumption via pub/sub, inner-message + 6-id correlation extraction, re-inject to origin, and receiver `flag[H]` collapse — before building anything. (INTAKE-01, INTAKE-02, INTAKE-04, PROBE-06) (completed 2026-06-05 — LIVE-PROVEN: spike GREEN + close gate GATE_EXIT=0, 453 facts ×3, triple-SHA net-zero held; 2 trip recipes corrected, c2d6ea6)
- [x] **Phase 34: Keeper Console Foundation** — Runnable multi-replica `Keeper` on `BaseConsole.Core`; builds, containerizes, joins compose healthy; competing-consumer load-balancing. (KEEP-01, KEEP-02, KEEP-03) (completed 2026-06-05 — hermetic 4/4: round-robin test consumed==1, ComposeYamlFacts shape guards, docker build green, 0-warning Release+Debug, 454-pass suite; live multi-replica + compose-health smokes operator-pending, 34-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 35: Fault Intake & Correlation** — Production intake of the two `Fault<T>` events; extract 6-id tuple + `H`; open execution log-scope; `_error` → TTL'd forensic DLQ-1 only. (INTAKE-03, KMET-04) (completed 2026-06-05 — hermetic: BuildState byte-identical refactor (6 scope-guard classes GREEN), two real `Fault<T>` consumers on `keeper-fault-recovery` with manual CorrelationId scope + KeeperFaultConsumerScopeTests 3/3 (SC2), 0-warning Release; INTAKE-03 separation slice + KMET-04 hermetic-proven; SC3 live ES-correlation operator-pending, 35-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 36: L2 Health-Probe Recovery Loop & DLQs** — Bounded crash-survivable L2 read+write probe loop; re-inject on success, give-up to `keeper-dlq` (DLQ-2); ack-after-loop; two DLQs split by exhaustion mechanism (Immediate(N) → DLQ-1, probe → DLQ-2); shared `Immediate(N)` from appsettings across all consumers. (PROBE-01..05, DLQ-01..04) (completed 2026-06-06 — hermetic 5/5 SC verified against source: `L2ProbeRecovery` bounded loop (RedisException-only, 5×12=60s<1800s), both consumers re-inject-by-type | park-original-to-`keeper-dlq`, ack-after-loop, consolidated `skp-dlq-1` (7d TTL) wired once in `BaseConsole.Core` keeping `GenerateFaultFilter`; 467-pass hermetic suite, 0-warning Release; code review 0 critical / 3 warning; live recover/give-up + kill-mid-loop operator-pending, 36-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 37: Orchestrator Pause/Resume Coordination** — New `PauseWorkflow`/`ResumeWorkflow` contracts + orchestrator consumers; per-workflow pending-recovery set keyed by `H` in single-replica L1; idempotent; stays paused on give-up. *(Only phase touching the Orchestrator project.)* (PAUSE-01..05) (completed 2026-06-06 — hermetic 5/5 SC verified against source: deterministic-TriggerKey three-state model (Quartz `PauseJob`/`GetTriggerState`, no L1 state field), `ConcurrentMessageLimit=1` consumers on dedicated fan-out endpoint, Keeper `Publish` Pause-at-intake + Resume-on-Recovered (GaveUp parks, no Resume); 477-pass hermetic suite (`--filter-not-trait Category=RealStack`), 0-warning Release; **caught + fixed a load-bearing 37-02 self-reschedule regression** (`RescheduleAsync` `ScheduleJob`→`RescheduleJob` replace, `571498f`) the 37-04 run had mis-filed as pre-existing; code review 0 critical / 2 warning; live pause↔resume bus round-trip operator-pending, 37-HUMAN-UAT.md, Phase-39 live gate)
- [x] **Phase 38: Uniform `service_name` + Instance Labels Across All Metrics** — `service_name={name}_{version}` + `service_instance_id` on every metric series (runtime/HTTP/business); processor name+version sourced from the DB (not appsettings); logs `service.name` unchanged; Prometheus query consumers updated. (MLBL-01..05) (completed 2026-06-06 — verified 5/5 must-haves: combined `service_name={name}_{version}` on the metrics resource for all 4 consoles + non-empty `service_instance_id` across runtime/HTTP/business; processor name/version DB-sourced via the `MeterProviderHolder` swap after identity Loop A; LOGS `service.name` stays bare (LogsResourceBareNameFacts); PromQL consumers reconciled (0 bare literals). Hermetic 479-pass / 0-warning Release; live RealStack `MetricsRoundTripE2ETests` 1/1 GREEN after container rebuild — :9090 scrape proved sk-api_3.2.0 / orchestrator_3.4.0 / DB-sourced sample-proc-…_1.0.0 (placeholder count=0); no live operator step. Code review 0 critical / 2 warning (MeterProviderHolder lifecycle, advisory))
- [x] **Phase 39: Keeper Observability + Real-Stack E2E + Close Gate** (2026-06-06) — `Keeper` meter + counters/histograms; E2E proving recover-both-paths + give-up; 3×GREEN triple-SHA net-zero close gate (both DLQs + scratch-key scan-clean). (KMET-01/02/03, TEST-01/02/03)

### Gap-Closure Phases (v3.7.0 audit — 2026-06-06)

- [x] **Phase 40: Keeper Recovery Hardening** (2026-06-06 — KHARD-01/02/03 verified 9/9; live 3×-GREEN close-gate Manual-Only, tracked in 40-HUMAN-UAT.md) — Bound the recover→reinject cycle with a config attempt cap (persistent fault parks instead of flooding the stack); make the keeper-dlq give-up-park drain deterministic (poll-until-stably-empty teardown → close-gate `keeper-dlq depth==0` holds); extract the shared fault-consumer recovery logic so the cap lands in one place. (KHARD-01, KHARD-02, KHARD-03)
- [x] **Phase 41: Orchestrator Pause/Resume Diagnostics** — Log on the `ResumeAsync` silent-ignore path (dropped Resume becomes diagnosable); harden `WorkflowScheduler.RescheduleAsync` fallback against a purged non-durable job. (closes 37-REVIEW WR-01, WR-02) (completed 2026-06-07 — 2/2 plans, verifier 2/2 SC, code review clean; LogInformation on ResumeAsync ignore branch (log-only, D-01/D-02), RescheduleAsync threaded workflowId + null-fallback re-creates job+trigger (D-04), RescheduleSchedulingTests asserts re-establishment (D-06); hermetic 505/0, Release 0-warning)
- [x] **Phase 42: v3.7.0 Docs & Traceability Reconciliation** — Flip stale REQUIREMENTS.md checkboxes `[ ]→[x]` for satisfied INTAKE/PROBE/DLQ/PAUSE/KMET-04 + fix their traceability rows; add missing MLBL-01..05 rows + correct the footer count; fix ROADMAP Phase-38 progress row; backfill `39-VERIFICATION.md`. (doc-only) (completed 2026-06-07 — 3/3 plans, verifier 4/4 SC: SC1 20 checkboxes + 16 traceability rows, SC2 MLBL-01..05 + 34/34 footer, SC3 Phase-38 row 4/4, SC4 39-VERIFICATION.md backfilled; encoding clean, close-gate NOT re-run)

</details>

## Phases (shipped milestones)

<details>
<summary>✅ v3.2.0 Steps API MVP (Phases 1-11) — SHIPPED 2026-05-28</summary>

11 phases / 41 plans / 142 integration facts GREEN × 3 consecutive runs. Full phase details, decisions, and execution narrative archived to [milestones/v3.2.0-ROADMAP.md](milestones/v3.2.0-ROADMAP.md).

- [x] Phase 1: Repository Scaffold (3/3 plans) — 2026-05-26
- [x] Phase 2: Postgres + Docker Compose (2/2 plans) — 2026-05-26
- [x] Phase 3: EF Core Persistence Base (2/2 plans) — 2026-05-27
- [x] Phase 4: Cross-Cutting Middleware + Error Handling (2/2 plans) — 2026-05-27
- [x] Phase 5: Observability + Health Probes (2/2 plans) — 2026-05-27
- [x] Phase 6: Validation + Mapping Base (2/2 plans) — 2026-05-27
- [x] Phase 7: Generic HTTP Base + Composition Root (2/2 plans) — 2026-05-27
- [x] Phase 8: Entity Build-Out + Migrations + Docker Runtime + Tests (8/8 plans) — 2026-05-28
- [x] Phase 9: Processor.GetBySourceHash + Orchestration Start/Stop (3/3 plans) — 2026-05-28
- [x] Phase 10: Remove SchemaId on AssignmentEntity, add ConfigSchemaId on ProcessorEntity (5/5 plans) — 2026-05-28
- [x] Phase 11: Migrate Prometheus + Elasticsearch from compose stack sk2_1 to sk_p (10/10 plans) — 2026-05-28

</details>

<details>
<summary>✅ v3.3.0 Orchestration L3 → L1 → L2 Build Pipeline (Phases 12-16) — SHIPPED 2026-05-29</summary>

5 phases / 26 plans / 235 integration facts GREEN × 3 consecutive runs, dual-SHA (`psql \l` + `redis-cli --scan`) BEFORE=AFTER held. 64/64 requirements satisfied (audit PASSED). Full phase details, success criteria, and decisions archived to [milestones/v3.3.0-ROADMAP.md](milestones/v3.3.0-ROADMAP.md).

- [x] Phase 12: Redis infra + composition + healthcheck + DI registration (8/8 plans) — 2026-05-29
- [x] Phase 13: OrchestrationService split + L3 fetch + L1 build (3/3 plans) — 2026-05-29
- [x] Phase 14: Validation gates (DFS + schema-edge + payload-config-schema) (5/5 plans) — 2026-05-29
- [x] Phase 15: L2 Redis projection write + Stop existence check (5/5 plans) — 2026-05-29
- [x] Phase 16: Idempotency + concurrency + L1 cleanup + 3-GREEN closeout (5/5 plans) — 2026-05-29

</details>

<details>
<summary>✅ v3.4.0 BaseConsole + Orchestrator Messaging (Phases 17-24 + 24.1) — SHIPPED 2026-06-01</summary>

9 phases / 31 plans. A reusable `BaseConsole.Core` Generic-Host library + a runnable `Orchestrator` console connected to the WebApi over MassTransit/RabbitMQ, with body-carried CorrelationId proven end-to-end (HTTP → Redis L2 → fan-out → orchestrator log in Elasticsearch), the full orchestrator lifecycle (L1 hydration, Quartz scheduling, entry-step dispatch, stop teardown), the processor→orchestrator result round-trip + L1-only step advancement, and a gating redesign (L2-existence dedup, boot-gate/plugin removal, atomic Stop). Final clean-build suite 335/335 GREEN (real-stack E2E live), Release 0 warnings. 70/70 requirements (ORCH-GATE-01 superseded by 24.1). Milestone audit PASSED. Full phase details archived to [milestones/v3.4.0-ROADMAP.md](milestones/v3.4.0-ROADMAP.md).

- [x] Phase 17: Messaging.Contracts + Shared L2 Root Extract (2/2 plans) — 2026-05-30
- [x] Phase 18: BaseConsole.Core Library (4/4 plans) — 2026-05-30
- [x] Phase 19: Orchestrator Console + WebApi Bus Wiring + RabbitMQ Tier (4/4 plans) — 2026-05-30
- [x] Phase 20: Correlation Propagation Proof + Synthetic Harness + Triple-SHA Closeout (4/4 plans) — 2026-05-31
- [x] Phase 21: v3.4.0 Closeout Hygiene — shared L2ProjectionKeys (1/1 plan) — 2026-05-31
- [x] Phase 22: L2 Root-Parent Restructure + Processor Self-Registration (5/5 plans) — 2026-05-31
- [x] Phase 23: Orchestrator Lifecycle — L1 Hydration, Quartz Scheduling, Entry-Step Dispatch & Stop Teardown (5/5 plans) — 2026-05-31
- [x] Phase 24: Orchestrator Result-Consume & Step Advancement (5/5 plans) — 2026-06-01
- [x] Phase 24.1: Gating Redesign — L2-dedup + Gate Removal (gap-closure) (1/1 plan) — 2026-06-01

</details>

<details>
<summary>✅ v3.6.0 Idempotent Execution — Exactly-Once-Effect Round-Trip (Phases 31-32.1) — SHIPPED 2026-06-05</summary>

4 phases / 9 active plans. The orchestrator↔processor round-trip became exactly-once-effect: deterministic identity `H = SHA-256(correlationId, workflowId, stepId, processorId, EntryId)` (executionId excluded) + effect-first `flag[H]` CAS dedup at both hops → each step's downstream effect happens exactly once, no lost branch, `ProcessAsync` needs no idempotency logic. Content-addressed two-level L2 (blobs + manifest), per-edge merge (content-collapse), N×M manifest fan-out, configurable retry budget. A late course-correction reverted the planned cancelled circuit-breaker (Phase 32 → 32.1) to plain dead-lettering on exhaustion, preserving the Phase-31 idempotency layer. 14/14 active requirements satisfied + live-verified (Phase-32's 8 retired); audit tech_debt with 0 functional blockers; 32.1 close gate `GATE_EXIT=0` (3×GREEN=452, triple-SHA BEFORE==AFTER held). Full phase details, decisions, and the breaker-revert rationale archived to [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md).

- [x] Phase 31: Idempotent Execution Round-Trip (Exactly-Once-Effect) (6/6 plans) — 2026-06-04
- [x] Phase 31.1: Close-Gate Redis Net-Zero (gap closure) (1/1 plan) — 2026-06-04
- [x] Phase 32: Cancelled Circuit-Breaker (5/6 plans) — **superseded by 32.1** (breaker reverted)
- [x] Phase 32.1: Dead-Letter on Exhaustion (Breaker Reverted) (2/2 plans) — 2026-06-05

</details>

## 🚧 v3.5.0 Processor Console — Self-Registration, Liveness & Execution Round-Trip (In Progress)

**Milestone Goal:** Stand up a reusable `BaseProcessor.Core` library + a first concrete `Processor.Sample` console (the processor-side mirror of `BaseConsole.Core`/`Orchestrator`) that self-identifies via an assembly-embedded SourceHash, self-registers its liveness into Redis L2 (only-when-Healthy, lock-free), and runs the full live orchestrator→processor→orchestrator execution round-trip — with the actual transform isolated to one minimal `abstract ProcessAsync` seam. The v3.4.0 `Orchestrator`, the `EntryStepDispatch`/`ExecutionResult` wire contracts, and `ProcessorLivenessValidator` are all reused **unchanged**.

**Build order (locked, mirrors the v3.4.0 leaf→base→wiring→concrete→proof cadence):**
25 (leaf shared contracts + WebApi responders) → 26 (`BaseProcessor.Core`: library + identity + two-loop startup + liveness worker) → 27 (execution round-trip) → 28 (SourceHash MSBuild identity + `Processor.Sample` + real-stack E2E closeout).

- [x] **Phase 25: Shared Contracts + WebApi Responders** — Leaf contract extracts (`ProcessorProjection` public, `ExecutionData` key, `"Healthy"` constant, 2 request/response pairs) + relax the WebApi publish-only firewall to host `GetProcessorBySourceHash` + `GetSchemaDefinition` responders. (completed 2026-06-01)
- [x] **Phase 26: BaseProcessor.Core — Library, Identity & Liveness** — Reusable Generic-Host scaffold on `BaseConsole.Core`; two-loop startup (identity-by-SourceHash + schema-definition resolution via `IRequestClient` with retry); only-when-Healthy liveness heartbeat worker into Redis L2. (completed 2026-06-01)
- [x] **Phase 27: Execution Round-Trip** — Durable `queue:{processorId:D}` consumer bound at Healthy; L2 input resolution + input validation; the `abstract ProcessAsync` seam; per-result output validation + L2 data write + result minting + one-by-one `ExecutionResult` sends; ack-after-send / business-ack / infra-throw; inherited correlation.
- [x] **Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout** — MSBuild SourceHash target (SHA-256, lowercase 64-hex, LF-normalized, folded over base+concrete `.cs`) + assembly-metadata embed; first concrete `Processor.Sample` (dummy `ProcessAsync` + multistage Dockerfile + compose tier); real-stack E2E round-trip proof + 3-GREEN/triple-SHA close gate.
- [x] **Phase 29: Structured Execution-Scope Logging** — Ambient structured-attribute logs (CorrelationId, WorkflowId, StepId, ProcessorId, ExecutionId, EntryId) via MEL log scopes serialized by OTel `IncludeScopes` to Elasticsearch: unchanged `InboundCorrelationConsumeFilter` + new bus-wide `InboundExecutionScopeConsumeFilter` (execution id-set for `IExecutionCorrelated`, both consoles), shared `ExecutionLogScope` keys, skip `Guid.Empty`, per-result inner scope for minted ExecutionId/output EntryId, process-wide ProcessorId enricher from `IProcessorContext`, explicit scope in the Quartz `WorkflowFireJob`. (completed 2026-06-02)
- [x] **Phase 30: Runtime & Business Metrics** — Code-defined runtime + business metrics carrying a per-replica `service_instance_id` label (the pod name in k8s), set as a resource attribute in the base libs; new orchestrator + processor send/consume counters (labelled by `ProcessorId`, processor adds `outcome`) registered via `AddMeter` — enabling PromQL rate/diff analysis of orchestrator→processor dispatch throughput and per-processor outcome bottlenecks across replicas, with no high-cardinality `workflowId` label and no collector-side metric config. (planned 2026-06-02) (completed 2026-06-02)

## Phase Details

<details>
<summary>✅ v3.5.0 Phase Details (Phases 25-30) — shipped 2026-06-02</summary>

### Phase 25: Shared Contracts + WebApi Responders

**Goal**: The leaf shared-contract vocabulary both sides depend on exists in `Messaging.Contracts`, and the WebApi can answer identity + schema-definition bus requests — so the processor (built later) has something to query.
**Depends on**: Phase 24.1 (v3.4.0 close — `Messaging.Contracts`, `L2ProjectionKeys`, `ProcessorService.GetBySourceHashAsync`, and the publish-only `AddBaseApiMessaging` firewall all in place)
**Requirements**: CONTRACT-01, CONTRACT-02, CONTRACT-03, RPC-01, RPC-02, RPC-03
**Success Criteria** (what must be TRUE):

  1. `ProcessorProjection` is public in `Messaging.Contracts.Projections` and is the single shared type used by both WebApi and (later) the processor — no duplicate definition.
  2. `L2ProjectionKeys.ExecutionData(Guid entryId)` returns `skp:data:{entryId:D}`, distinct from the existing `root`/`step`/`processor` key builders, with a golden test pinning the exact string.
  3. The liveness `status` value `"Healthy"` is a shared constant in `Messaging.Contracts` (single source of truth — writer and reader cannot desync), and the two `GetProcessorBySourceHash` / `GetSchemaDefinition` request/response record pairs are defined there.
  4. The WebApi bus join, extended from publish-only, answers a `GetProcessorBySourceHash` request with `{ Id, InputSchemaId?, OutputSchemaId?, ConfigSchemaId? }` (or a not-found response) backed by `ProcessorService.GetBySourceHashAsync`, and a `GetSchemaDefinition(schemaId)` request with `{ Definition }` (or not-found) backed by the existing schema read.
  5. The CRUD surface is unaffected — existing WebApi HTTP behavior and the v3.4.0 publish path are unchanged (no regression in the existing suite).

**Plans**: 2 plans

- [x] 25-01-PLAN.md — Shared contract extracts in Messaging.Contracts (ProcessorProjection move, ExecutionData key, Healthy const, request/response record pairs + queue constants)
- [x] 25-02-PLAN.md — WebApi responder host (two-hook bus join extension + GetProcessorBySourceHash / GetSchemaDefinition dual-response consumers, firewall + Degraded-cap preserved)

### Phase 26: BaseProcessor.Core — Library, Identity & Liveness

**Goal**: A reusable `BaseProcessor.Core` library exists on which a concrete processor self-identifies via its embedded SourceHash, resolves its identity + schema definitions over the bus (retrying through boot-before-register), and self-registers liveness into Redis L2 — only while Healthy, lock-free, in the exact shape the v3.4.0 `ProcessorLivenessValidator` reads.
**Depends on**: Phase 25 (the request/response contracts + WebApi responders must exist before the processor can resolve identity/definitions)
**Requirements**: BPC-01, BPC-02, BPC-03, IDENT-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02, LIVE-01, LIVE-02, LIVE-03, LIVE-04, LIVE-05, LIVE-06, CONFIG-01
**Success Criteria** (what must be TRUE):

  1. `BaseProcessor.Core` is a reusable Generic-Host library built on `BaseConsole.Core` (inheriting soft-dep Redis, embedded health probes, metrics-only OTel, MassTransit/RabbitMQ, inbound/outbound correlation filters), and `AddBaseProcessor` wires the startup orchestration so a concrete `Program.cs` stays minimal.
  2. At runtime the processor reads its SourceHash from assembly metadata via reflection, then resolves its identity (`Id` + three nullable schema Ids) by issuing a `GetProcessorBySourceHash` `IRequestClient` query, retrying on timeout/not-found until it succeeds (booting before the DB row exists is tolerated).
  3. For each non-null (input, output) schema Id the processor resolves the definition via a `GetSchemaDefinition` `IRequestClient` query (retry until resolved); null/optional schema Ids are skipped by design and never cause failure (the config schema is not resolved).
  4. A background heartbeat worker writes/refreshes `skp:{processorId:D}` every `Interval` seconds with `{ inputDefinition, outputDefinition, liveness{ timestamp, interval, status: "Healthy" } }`, re-applying the configured `Ttl` expiry each beat (sliding) — written **only once Healthy** (identity + all required definitions resolved); a starting/restarting/unhealthy replica does not write (orchestrator sees `absent`), and the written `interval` equals the configured delay so `timestamp + interval×2` staleness holds.
  5. The written L2 value shape exactly matches what the v3.4.0 `ProcessorLivenessValidator` reads (reused unchanged — presence+freshness ⟺ "≥1 replica healthy"), and multi-replica writes are lock-free: the shared liveness key is a blind whole-value `SET` of equivalent only-when-Healthy content (last-write-wins, no synchronization).

**Plans**: 3 plans

  - [x] 26-01-PLAN.md — Project skeleton + identity/seam/options contracts + Wave 0 test scaffold + exchange: request-client confirmation (BPC-01, BPC-02, IDENT-03, CONFIG-01) — completed 2026-06-01
  - [x] 26-02-PLAN.md — AddBaseProcessor composition root + two-loop startup orchestrator (BPC-03, IDENT-04, RPC-04, SCHEMA-01, SCHEMA-02) — completed 2026-06-01
  - [x] 26-03-PLAN.md — Only-when-Healthy liveness heartbeat worker + closed reader round-trip (LIVE-01..06) — completed 2026-06-01

### Phase 27: Execution Round-Trip

**Goal**: A Healthy processor consumes a real `EntryStepDispatch`, resolves + validates its input from L2, runs the `abstract ProcessAsync` transform, validates + writes each output to L2, mints results, and sends `ExecutionResult`s back to the orchestrator one-by-one — with the framework owning all id-minting, validation, L2 I/O, and sending so a concrete overrides only `ProcessAsync`.
**Depends on**: Phase 26 (the processor must resolve identity + definitions and be Healthy before it can bind the dispatch queue and validate input/output)
**Requirements**: EXEC-01, EXEC-02, EXEC-03, EXEC-04, EXEC-05, EXEC-06, EXEC-07, EXEC-08, EXEC-09, EXEC-10, CONFIG-02
**Success Criteria** (what must be TRUE):

  1. The processor consumes `EntryStepDispatch` on a **durable** `queue:{processorId:D}` competing-consumer endpoint that is bound only once Healthy (and `Healthy` is written to L2 only after the bind), so the orchestrator never sends to a non-existent queue; a restarting/unhealthy processor leaves dispatches queued (not lost, not processed) until it recovers.
  2. Input data is read only from `L2[data(entryId)]` (existence-checked; `Payload` is config, never input) and validated against `inputDefinition` when present — an empty definition skips validation; a non-empty definition with missing/empty `entryId` yields a `Failed` result with an error message.
  3. The sole transform seam is `abstract Task<IReadOnlyList<ProcessResult>> ProcessAsync(string inputData, string config, CancellationToken ct)` (`config` = dispatch `Payload`); for each result the framework validates output vs `outputDefinition` (empty = valid), mints a new `entryId` + writes the output to `L2[data(newEntryId)]` with the configured execution-data TTL on success (nothing written on output-validation failure → `Failed`), and mints a per-result `executionId` + stamps the shared Ids.
  4. Results are sent to `queue:orchestrator-result` one-by-one (never a batched list); an empty result list with no exception/cancellation acks only (no message), while `Failed` (incl. caught exceptions, with an error message) and `Cancelled` are always sent.
  5. The dispatch is acked only after all sends complete; infra faults throw and retry (`Immediate(3)`) (business-ack / infra-throw, mirroring the orchestrator), and the body `CorrelationId` is inherited from `BaseConsole.Core` — flowing from the dispatch into the log scope and onto every published `ExecutionResult`.

**Plans**: 3 plans

- [x] 27-01-PLAN.md — Foundation: firm ProcessResult + BaseProcessor internal invoker, port SSRF-locked Json.Schema validator, add CONFIG-02 ExecutionDataTtlSeconds (EXEC-03/04/05, CONFIG-02) — completed 2026-06-01
- [x] 27-02-PLAN.md — EntryStepDispatchConsumer: L2 input read/validate, ProcessAsync invoke, per-result output-validate/mint/write, one-by-one ExecutionResult send, business-ack/infra-throw (EXEC-02/04/05/06/07/08/09/10) — completed 2026-06-01
- [x] 27-03-PLAN.md — Wiring: register consumer (ExcludeFromConfigureEndpoints) + runtime ConnectReceiveEndpoint bind-then-MarkHealthy (EXEC-01) — completed 2026-06-02

### Phase 28: SourceHash Identity + Processor.Sample + E2E Closeout

**Goal**: The deterministic build-time SourceHash identity is embedded into the assembly, the first concrete `Processor.Sample` exists and joins the compose stack, and a real-stack E2E proves the live orchestrator→Processor.Sample→orchestrator round-trip and the liveness-gated Start — all behind the 3-GREEN / triple-SHA close gate.
**Depends on**: Phase 27 (the full framework round-trip must exist before a concrete + E2E can exercise it end-to-end)
**Requirements**: IDENT-01, IDENT-02, SAMPLE-01, SAMPLE-02, TEST-01, TEST-02
**Success Criteria** (what must be TRUE):

  1. An MSBuild target (`BeforeTargets=CoreCompile`) computes the SourceHash — SHA-256, lowercase 64-hex, LF-normalized, per-file hashes folded deterministically (ordinal path sort) over `BaseProcessor.Core` + the concrete's `.cs` (excluding generated files, `BaseConsole.Core`, `Messaging.Contracts`) — emits it as `[assembly: AssemblyMetadata("SourceHash", …)]`, and re-runs on implementation source change (no stale hash on incremental builds).
  2. `Processor.Sample` is the first concrete console (family convention `Processor.<Purpose>`), implementing `ProcessAsync` with a minimal POC dummy result list and carrying no infrastructure/id/L2/bus code.
  3. `Processor.Sample` ships a multistage Dockerfile and joins the compose stack (mirroring the Orchestrator tier), and its built binary's embedded SourceHash (lowercase 64-hex, satisfying the DB `^[a-f0-9]{64}$` validator) is the value registered as the Processor DB row via CRUD.
  4. A real-stack E2E proves the live round-trip — a dispatch is consumed, output is written to L2, and the orchestrator advances on the returned `ExecutionResult` — and proves the liveness-gated Start path (a live `Processor.Sample` heartbeat lets orchestration Start pass).
  5. The phase-close gate holds: 3-consecutive-GREEN cadence + triple-SHA (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues`) BEFORE=AFTER, with scan-clean teardown covering the new processor-liveness and execution-data keys.

**Plans**: 4 plans

- [x] 28-01-PLAN.md — SourceHash.targets (inline RoslynCodeTaskFactory + two-target emit) + Processor.Sample project skeleton + hermetic reflection/unit facts (IDENT-01/02, SAMPLE-01) — complete 2026-06-02
- [x] 28-02-PLAN.md — Multistage Dockerfile + processor-sample compose tier + ComposeYamlFacts + cross-OS dual-build hash-reproducibility gate (SAMPLE-02; IDENT-02 reproducibility proven cross-OS) — complete 2026-06-02
- [x] 28-03-PLAN.md — Real-stack SampleRoundTripE2ETests (genuine embedded hash, no synthetic liveness seed, truthful liveness-gated Start) (TEST-01) — complete 2026-06-02
- [x] 28-04-PLAN.md — phase-28-close.ps1 (3-GREEN + triple-SHA, steady-state processor-id pre-flight seed) (TEST-02) — complete 2026-06-02 (gate exit 0: 395 facts GREEN x3 + triple-SHA BEFORE==AFTER held)

### Phase 29: Structured Execution-Scope Logging

**Goal**: Every project emits logs as structured attributes only (CorrelationId, WorkflowId, StepId, ProcessorId, ExecutionId, EntryId), carried ambiently via MEL log scopes and serialized by OTel `IncludeScopes` into Elasticsearch — so the full orchestrator→processor→orchestrator round-trip is queryable by any id without interpolating ids into message templates or threading them through method signatures.
**Depends on**: Phase 28 (the full round-trip + both consoles + the `IExecutionCorrelated` contracts must exist to scope)
**Requirements**: LOG-01, LOG-02, LOG-03, LOG-04, LOG-05, LOG-06 (proposed — formalized at spec)
**Success Criteria** (what must be TRUE):

  1. All ids appear as Elasticsearch attributes (`attributes.CorrelationId` / `WorkflowId` / `StepId` / `ProcessorId` / `ExecutionId` / `EntryId`) sourced from log SCOPES (values under fixed keys, never interpolated into message text — T-18-04), via the existing `IncludeScopes` + `ParseStateValues` OTel bridge.
  2. `InboundCorrelationConsumeFilter` is unchanged (still scopes `CorrelationId` for all messages); a new bus-wide open-generic `InboundExecutionScopeConsumeFilter` scopes the execution id-set for `IExecutionCorrelated` messages and passes through all others, registered in `AddBaseConsoleMessaging` so BOTH the orchestrator (`ResultConsumer` ← `ExecutionResult`) and the processor (`EntryStepDispatchConsumer` ← `EntryStepDispatch`) are covered with no per-console wiring.
  3. A shared `ExecutionLogScope` keys class in `Messaging.Contracts` is the single source of truth, its key strings equal to the structured-param names (`{WorkflowId}` …) so scope-derived and param-derived attributes coincide on the same field; `Guid.Empty` values are skipped (no zero-guid noise attributes).
  4. The processor's per-result minted `ExecutionId` + output `EntryId` are captured via a nested `BeginScope` in `EntryStepDispatchConsumer` (overriding the inbound values for the write/send lines), and `ProcessorId` enriches ALL processor logs (startup, heartbeat, consume) via an OTel `LogRecord` enricher reading `IProcessorContext.Id` (null-safe before identity resolves).
  5. `WorkflowFireJob` (a Quartz job, outside the consume pipeline) opens an explicit `BeginScope(CorrelationId + WorkflowId)` in `Execute` so its fire logs correlate with the round-trip it triggers; the full hermetic + real-stack suite stays GREEN with no log-shape regression and the close-gate triple-SHA still holds.

**Plans**: 5 plans

- [x] 29-01-PLAN.md - ExecutionLogScope keys class in Messaging.Contracts + key-pin test (LOG-03) — 2026-06-02
- [x] 29-02-PLAN.md - InboundExecutionScopeConsumeFilter (5 ids, Guid.Empty-skip, non-IExecutionCorrelated no-op) + bus-wide registration + hermetic probe test (LOG-01/02/03) — 2026-06-02
- [x] 29-03-PLAN.md - ProcessorId LogRecord enricher (null-safe, processor-side only) + nested BeginScope for minted ExecutionId+EntryId in EntryStepDispatchConsumer + tests (LOG-01/04) — 2026-06-02
- [x] 29-04-PLAN.md - WorkflowFireJob explicit BeginScope(CorrelationId + WorkflowId) + hermetic test (LOG-01/05)
- [x] 29-05-PLAN.md - Real-stack E2E scope-sourced processor-side proof (L1 trap closed) + scripts/phase-29-close.ps1 close gate (LOG-01/06)

### Phase 30: Runtime & Business Metrics

**Goal**: Every service emits code-defined metrics carrying a per-replica `service_instance_id` label so that, across multiple orchestrator/processor replicas, PromQL can measure the rate of orchestrator→processor dispatch *sending* vs processor *consuming* (the per-processor bottleneck) and per-processor outcome rates — without high-cardinality workflow labels and without collector-side metric config.
**Depends on**: Phase 29 (the full round-trip + both consoles in place; the new counters instrument the same send/consume sites the logging phase scoped)
**Requirements**: METRIC-01, METRIC-02, METRIC-03, METRIC-04, METRIC-05, METRIC-06, METRIC-07 (proposed — formalized at spec)
**Success Criteria** (what must be TRUE):

  1. A `service.instance.id` resource attribute is set **in code** in BOTH base libs (`BaseApi.Core` `ObservabilityServiceCollectionExtensions` + `BaseConsole.Core` `BaseConsoleObservabilityExtensions`) from the pod identity (`POD_NAME`/`HOSTNAME` env, GUID fallback off-cluster); every emitted metric (runtime, HTTP, business) carries a uniform `service_instance_id` Prometheus label per replica — and the `otel-collector` metrics pipeline is NOT modified to add it (the existing generic `resource_to_telemetry_conversion` forwards it).
  2. All three process types (WebApi, Orchestrator, every `Processor.*`) emit .NET runtime metrics (existing `AddRuntimeInstrumentation`), and the WebApi emits ASP.NET Core HTTP server metrics (existing `AddAspNetCoreInstrumentation`) — all now carrying `service_instance_id`.
  3. The Orchestrator defines a code-owned `Meter` with two monotonic counters — `orchestrator_dispatch_sent_total` (at the `EntryStepDispatch` send) and `orchestrator_result_consumed_total` (in `ResultConsumer`) — each labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter`; **no** `workflowId` label.
  4. `BaseProcessor.Core` defines a code-owned `Meter` with `processor_dispatch_consumed_total` (on consuming `EntryStepDispatch`) and `processor_result_sent_total` (per `ExecutionResult` sent, labelled by `outcome` ∈ {completed, failed, cancelled}) — both labelled by `ProcessorId` (+ ambient `service_instance_id`), registered via `AddMeter` so every `Processor.*` inherits them; **no** `workflowId` label; the in-flight "processing" outcome is deferred.
  5. The counters align by `ProcessorId` so PromQL `sum by (ProcessorId)(rate(orchestrator_dispatch_sent_total[…])) − sum by (ProcessorId)(rate(processor_dispatch_consumed_total[…]))` quantifies per-processor dispatch backlog across replicas, and per-outcome rates are queryable — proven by a real-stack assertion that the new series appear in Prometheus with the expected `ProcessorId` / `outcome` / `service_instance_id` labels after a live round-trip.

**Plans**: 4 plans (2 waves)

- [x] 30-01-PLAN.md — service.instance.id resource attr in both base libs + PrometheusTestClient/ResolveInstanceId scaffolding (METRIC-01/02/03/07) — 2026-06-02
- [x] 30-02-PLAN.md — Orchestrator counters: dispatch_sent + result_consumed keyed by ProcessorId (METRIC-04) — 2026-06-02
- [x] 30-03-PLAN.md — BaseProcessor.Core counters: dispatch_consumed + result_sent{outcome} via firewall-correct meter seam (METRIC-05) — 2026-06-02
- [x] 30-04-PLAN.md — RealStack MetricsRoundTripE2ETests: Prometheus series + by-ProcessorId bottleneck PromQL (METRIC-06; live proof of 01..05; 07 gate) — 2026-06-02

</details>

### Phases 31-32.1 (v3.6.0 — SHIPPED 2026-06-05)

Full phase details (31, 31.1, 32→32.1), success criteria, plans, decisions, and the cancelled-circuit-breaker revert rationale are archived to [milestones/v3.6.0-ROADMAP.md](milestones/v3.6.0-ROADMAP.md). Requirements: [milestones/v3.6.0-REQUIREMENTS.md](milestones/v3.6.0-REQUIREMENTS.md). Audit: [milestones/v3.6.0-MILESTONE-AUDIT.md](milestones/v3.6.0-MILESTONE-AUDIT.md).

<details>
<summary>v3.7.0 Phase Details (Phases 33-42) — SHIPPED, archived to milestones/v3.7.0-ROADMAP.md</summary>

### Phase 33: Fault-Recovery Spike (De-Risk)

**Goal**: Prove the load-bearing assumption of the whole milestone — that a published `Fault<EntryStepDispatch>` and `Fault<ExecutionResult>` event can be consumed by an external subscriber, the original message + correlation extracted from `Fault<T>.Message`, re-injected to its origin endpoint by type, and silently collapsed by the receiver's surviving Phase-31 `flag[H]` dedup — before committing to the full Keeper build.
**Depends on**: — (first phase of the milestone)
**Requirements**: INTAKE-01, INTAKE-02, INTAKE-04, PROBE-06
**Success Criteria** (what must be TRUE):

  1. A spike consumer binding `IConsumer<Fault<EntryStepDispatch>>` and `IConsumer<Fault<ExecutionResult>>` receives the fault events the processor/orchestrator publish on retry-budget exhaustion — via pub/sub, no per-`{procId}_error`-queue binding — while `Fault<StartOrchestration>`/`Fault<StopOrchestration>` are demonstrably NOT delivered.
  2. From `Fault<T>.Message` the spike extracts the original message + full 6-id `IExecutionCorrelated` tuple (correlationId, workflowId, stepId, processorId, entryId, executionId) + `H`, proving the inner-message shape is reachable even though `Fault<T>` is not itself `IExecutionCorrelated`.
  3. The extracted message is re-injected directly to its origin endpoint by type (`queue:{processorId:D}` for dispatch, `queue:orchestrator-result` for result) — no orchestrator round-trip — and the downstream effect happens exactly once.
  4. A deliberately duplicated re-inject collapses at the receiver via its existing `flag[H]` gate with no second downstream effect (Keeper needs no dedup of its own). The `_error`-retention decision (TTL'd-forensic vs suppress) is recorded.

**Plans**: 2 plans

- [x] 33-01-PLAN.md — Author FaultRecoverySpikeE2ETests (clone rig + 6 grafts: dual Fault<T> capture, double-.Message unwrap, WRONGTYPE dispatch+result trips, a-priori H, verbatim re-inject x2, negative command-fault proof); hermetic compile + zero-regression gate [autonomous] ✓ Release 0/0, hermetic 447/0 RealStack-excluded
- [~] 33-02-PLAN.md — phase-33-close.ps1 (clone phase-32.1-close) + record D-10 _error decision + operator runbook for the live trip/recover/re-inject/collapse + close gate [autonomous:false] — AUTHORED + committed (`26e174a`); LIVE close gate pending operator (GATE_EXIT=0)

### Phase 34: Keeper Console Foundation

**Goal**: Stand up a runnable, multi-replica `Keeper` console on `BaseConsole.Core` (mirroring `Orchestrator`) that builds, containerizes, and joins the compose stack as a healthy tier with work load-balanced across replicas.
**Depends on**: Phase 33
**Requirements**: KEEP-01, KEEP-02, KEEP-03
**Success Criteria** (what must be TRUE):

  1. A `Keeper` console exists on `BaseConsole.Core` (Generic-Host, metrics-only OTel, soft-dep Redis, embedded health probes, MassTransit/RabbitMQ, inherited correlation filters) with a minimal `Program.cs` mirroring `Orchestrator`.
  2. Keeper runs multi-replica with fault work bound to a shared competing-consumer endpoint (not instance-unique fan-out); RabbitMQ round-robins fault events across replicas.
  3. Keeper builds clean (Release+Debug, 0 warnings) and containerizes via a multi-stage Dockerfile.
  4. Keeper joins the compose stack as a new healthy tier alongside `orchestrator` / `processor-sample` (health probes report ready live).

**Plans**: 3 plans

  - [x] 34-01-PLAN.md — KeeperQueues const + Keeper.csproj + SK_P.sln registration (KEEP-01 foundation)
  - [x] 34-02-PLAN.md — Keeper console body: placeholder message/consumer/definition + Program.cs + appsettings + Dockerfile (KEEP-01/02)
  - [x] 34-03-PLAN.md — compose keeper tier + ComposeYamlFacts + 4 hermetic Keeper tests + live operator smoke (KEEP-01/02/03)

### Phase 35: Fault Intake & Correlation

**Goal**: Wire the production fault-intake path — consuming the two execution-path `Fault<T>` events, extracting the inner message + 6-id correlation + `H`, opening the propagated execution log-scope, and confirming the `_error` record consolidates into the TTL'd forensic DLQ-1 (never Keeper's worklist).
**Depends on**: Phase 34
**Requirements**: INTAKE-03, KMET-04
**Success Criteria** (what must be TRUE):

  1. The transport-exhaustion record consolidates into DLQ-1 (TTL'd forensic) — Keeper recovers off the `Fault<T>` pub/sub events, never reads the error/DLQ-1 queue, and recovered work is never double-processed from it.
  2. On every consumed fault, Keeper opens the execution log-scope from the extracted inner message so its OTel logs carry the propagated correlationId + execution-scope ids (consistent with the other consoles).
  3. A faulted message processed by Keeper produces an Elasticsearch log correlated to the original execution by correlationId + ids (observable end-to-end).

**Plans**: 3 plans

- [x] 35-01-PLAN.md — Shared ExecutionLogScope.BuildState refactor (D-07), byte-identical + regression-guarded
- [x] 35-02-PLAN.md — Two real Fault<T> consumers + definitions + Program.cs swap + placeholder deletion + hermetic scope proof (completed 2026-06-05 — KeeperFaultConsumerScopeTests 3/3 GREEN proving CorrelationId + 5 exec ids; both defs on keeper-fault-recovery, single retry owner; 3 placeholders deleted; SK_P.sln 0/0 Release; hermetic suite 457/0; b71233c, 418bc3f)
- [x] 35-03-PLAN.md — RealStack SC3: running-Keeper-container correlated-ES-log proof (authored — KeeperFaultIntakeE2ETests sibling clone of the Phase-33 spike: WRONGTYPE live-trip → running-Keeper-container correlated ES log on service.name=keeper + attributes.CorrelationId == tripped correlationId + attributes.StepId + body.text ~ "keeper fault intake"; net-zero teardown; no DLQ-1/TTL scope creep; SK_P.sln 0/0 Release; hermetic suite unchanged; 1b64143. **LIVE SC3 run OPERATOR-PENDING** — not observed this session; runbook in 35-03-SUMMARY; INTAKE-03/KMET-04 stay unticked until the operator's GREEN live run)

### Phase 36: L2 Health-Probe Recovery Loop & DLQs

**Goal**: Implement the core recovery engine — a bounded, crash-survivable L2 read+write probe loop that re-injects to origin on first success or parks the unrecoverable in `keeper-dlq` (DLQ-2) on give-up — plus the two-DLQ topology (Immediate(N) exhaustion → DLQ-1; probe exhaustion → DLQ-2) and the shared `Immediate(N)` policy across all consumers.
**Depends on**: Phase 35
**Requirements**: PROBE-01, PROBE-02, PROBE-03, PROBE-04, PROBE-05, DLQ-01, DLQ-02, DLQ-03, DLQ-04
**Success Criteria** (what must be TRUE):

  1. On intake Keeper runs a recovery loop with config-driven inter-attempt delay (s) and max-attempts, each iteration probing L2 by reading `skp:data:{entryId}` AND write-then-deleting a scratch key (read + write, not mere connectivity); `delay × attempts` is documented as bounded under RabbitMQ's delivery-ack timeout.
  2. On the first successful probe Keeper exits and triggers recovery (re-inject + resume); when max-attempts exhaust it parks the original message in `keeper-dlq` (DLQ-2) and exits.
  3. The fault message is acked only after the loop exits (success or give-up); killing Keeper mid-loop leaves it un-acked → redelivered → loop restarts, with no lost message (at-least-once recovery observable).
  4. Two DLQs exist split by mechanism: DLQ-1 (all `Immediate(N)` transport exhaustions, consolidated across processor/orchestrator/Keeper, TTL'd forensic) and DLQ-2 `keeper-dlq` (probe give-ups); `keeper-dlq` depth is the primary Prometheus alert; on give-up the workflow stays paused (no auto-resume).
  5. All consumers (processor dispatch, orchestrator result/start/stop, Keeper) use the same `Immediate(N)` bound from the shared `RetryOptions` appsettings, routed uniformly to DLQ-1 (same pattern across consoles).

**Plans**: 4 plans

- [x] 36-01-PLAN.md — Contracts (keeper-dlq const + KeeperProbe key) + ProbeOptions + Wave-0 FakeRedis/bound test (completed 2026-06-05 — KeeperQueues.DeadLetter + L2ProjectionKeys.KeeperProbe; Keeper-local ProbeOptions 5×12 bound+appsettings; FakeRedis down/half-open/up double + ProbeOptions_Bound test; SK_P.sln 0/0 Release; hermetic suite 458/0; 52d2b67, 85c526a, 8f386c6)
- [x] 36-02-PLAN.md — L2ProbeRecovery loop helper + consumer re-inject/park bodies (PROBE-01..05) (completed 2026-06-05 — bounded read+write-then-delete loop, catch RedisException only; both consumers re-inject verbatim inner to origin on Recovered / park original Fault<T> to keeper-dlq on GaveUp; 6 facts GREEN, SK_P.sln 0/0 Release, hermetic 464/0; 399570f, cf627b9)
- [x] 36-03-PLAN.md — Consolidated DLQ-1 error transport in BaseConsole.Core (DLQ-01/02/04, all 3 consoles) (completed 2026-06-06 — mechanism-a custom IFilter<ExceptionReceiveContext> confirmed against MT 8.5.5 assemblies + in-mem spike; ConsolidatedErrorTransportFilter moves Immediate(N) exhaustion to ONE skp-dlq-1 (x-message-ttl 7d) across processor/orchestrator/Keeper, GenerateFaultFilter retained; typed ConsolidatedFault forensic envelope; 3 hermetic facts GREEN, SK_P.sln 0/0 Release, Keeper ns 16/16, hermetic 465/467 (2 reds = documented cross-ns in-mem-MT flake, GREEN in isolation); live broker-arg/move/drain = Plan 04/Phase 39; edc4787, 28d528e)
- [x] 36-04-PLAN.md — RealStack recover-both-paths + give-up E2E (operator-gated live half) (authored 2026-06-06 — KeeperRecoveryE2ETests sibling of FaultRecoverySpikeE2ETests: KeeperRecovery_RecoversBothPaths dispatch+result re-inject exactly-once CountEsHitsAsync==1 / KeeperRecovery_GivesUp_ParksToDlq probe-data poison → keeper-dlq park caught+ack-drained; skp:keeper:probe:* net-zero scan; SK_P.sln 0/0 Release, hermetic 467/0 unchanged, RealStack adds 0 hermetic tests; 1b0d7d9; live GREEN operator-gated, PROBE-03/04/05 unticked, Phase-39 authoritative)

### Phase 37: Orchestrator Pause/Resume Coordination

**Goal**: Add the `PauseWorkflow`/`ResumeWorkflow` contracts and the orchestrator-side consumers that halt a workflow's cron via Quartz `PauseJob` (D-08) and reschedule it from L1 on any successful recovery (D-09), with **Quartz `GetTriggerState` as the single source of truth** for the Running/Paused/Stopped state (no L1 state field, no pending-recovery set); idempotent under duplicate/concurrent signals via `ConcurrentMessageLimit = 1` + idempotent transitions + redelivery (D-07).
**Depends on**: Phase 36 (re-inject lives in Keeper per INTAKE-04; this is the cron-scheduling half)
**Requirements**: PAUSE-01, PAUSE-02, PAUSE-03, PAUSE-04, PAUSE-05
**Success Criteria** (what must be TRUE):

  1. New `PauseWorkflow` and `ResumeWorkflow` contracts exist in `Messaging.Contracts`, fanned from Keeper to the orchestrator (cron scheduling only; re-injection stays in Keeper).
  2. (Revised by **D-08**) On Keeper's pause signal the orchestrator halts the workflow's future cron fires via Quartz **`PauseJob`** (L1 preserved); the Paused state is owned by Quartz and read via `GetTriggerState(TriggerKey(jobId))` — **no separate L1 state field and no in-memory pending-recovery set**. (The original criterion's `UnscheduleOnlyAsync` + pending-recovery-set wording is superseded.)
  3. When no recoveries remain pending for a workflow, the orchestrator reschedules it from L1 (`ScheduleAsync` with the L1 root's `jobId` + `cron`) and future cron fires resume.
  4. (Revised by **D-07**) Duplicate/concurrent pause/resume signals (Keeper crash/redelivery) are absorbed by `ConcurrentMessageLimit = 1` serial consume + idempotent Quartz transitions + redelivery-on-crash — **no dedicated lock, no per-`workflowId` semaphore, no reference-counting set**. `H` is correlation/observability only (D-02), so PAUSE-04's do-not-double-count holds trivially.
  5. (Revised by **D-09**) The orchestrator resumes a workflow on **any** successful recovery regardless of sibling outcomes; a given-up message is parked to `keeper-dlq` and publishes nothing (does NOT re-pin paused). A workflow stays paused only if **no** recovery ever succeeds. Resume acts only when `GetTriggerState == Paused` (None=operator-Stopped and Normal=already-Running are ignored).

**Plans**: 4 plans

  - [x] 37-01-PLAN.md — Wave 0: four RED test files (contracts, Keeper publish, scheduling Pause/Resume/ignore, consumer idempotency) (completed 2026-06-06 — deliberate RED; build fails only on the 6 missing production symbols plans 02/03 create)
  - [x] 37-02-PLAN.md — Contracts + load-bearing deterministic TriggerKey stamping + PauseAsync/GetTriggerStateAsync (completed 2026-06-06 — PauseResumeContractTests 4/4 + PauseResumeSchedulingTests 3/3 GREEN; SchedulingTests no regression; consumer/Keeper tests remain RED for plans 03/04)
  - [x] 37-03-PLAN.md — Orchestrator Pause/Resume consumers + lifecycle seams + ConcurrentMessageLimit=1 definitions + Program wiring (completed 2026-06-06 — PauseResumeConsumerTests 1/1 GREEN; scheduling/contract tests no regression; Keeper tests remain RED for plan 04)
  - [x] 37-04-PLAN.md — Keeper publish sites: PauseWorkflow at intake + ResumeWorkflow on Recovered (GaveUp unchanged) (completed 2026-06-06 — KeeperPausePublishTests 2/2 GREEN; full phase-37 assembly compiles end-to-end; Keeper Release 0/0; live round-trip operator-pending)

### Phase 38: Uniform `service_name` + Instance Labels Across All Metrics

**Goal**: Every Prometheus metric series (runtime, HTTP, and business instruments) for all four consoles carries a human-distinguishable `service_name = {name}_{version}` label plus a non-empty `service_instance_id` label — where the processor's `{name}_{version}` is sourced from the **database** (the single source of truth), not appsettings. No live operator verification required (hermetic + scrape-assertion provable).
**Depends on**: Phase 30 (metrics foundation) + Phase 26 (processor identity round-trip). Independent of Phases 36/37; sequenced BEFORE the Phase 39 close gate so the gate seals the final metric-label contract.
**Requirements**: MLBL-01, MLBL-02, MLBL-03, MLBL-04, MLBL-05 (locked in `38-SPEC.md`)
**Success Criteria** (what must be TRUE):

  1. Every metric series (runtime / HTTP / business) for each console carries `service_name = {name}_{version}` (e.g. `keeper_3.7.0`, `orchestrator_3.4.0`, `sk-api_3.2.0`, and the processor's DB `{Name}_{Version}`); no series carries a bare `service_name` lacking the version suffix.
  2. Every metric series carries a non-empty `service_instance_id`, verified present on all three instrument families — not only the Phase-30 business counters.
  3. The processor's steady-state name+version are sourced from the DB: `ProcessorIdentityFound` is extended with `Name`+`Version`, the responder + `IProcessorContext` carry them; the processor's appsettings `Service:Name`/`Service:Version` are **retained** as the boot-window placeholder (GA-3 amendment - supersedes the original "removed / `processor-pending`" framing), and metrics before identity-resolution carry the appsettings `{name}_{version}` (e.g. `processor-sample_3.5.0`), then the MeterProvider is swapped to the DB-sourced resource once identity resolves.
  4. The logs' `service.name` stays the bare identity (metrics-only version suffix) — the Phase-35 ES assertion `service.name="keeper"` still passes.
  5. All in-repo Prometheus query consumers (incl. the Phase-11 `service_name="sk-api"` round-trip assertion) are updated to the combined label and pass; no high-cardinality labels introduced.

**Plans**: 4 plans

  - [x] 38-01-PLAN.md - Processor identity round-trip: extend ProcessorIdentityFound + responder + IProcessorContext with Name/Version; update 3 IProcessorContext fakes (CS0535 firewall) (MLBL-03) (completed 2026-06-06 — ProcessorResponderTests 2/2 GREEN; SK_P.sln 0/0 Debug+Release; commits 1ccae71, 867edaf)
  - [x] 38-02-PLAN.md - Combine service_name={name}_{version} on the metrics resource (both base libs); keep logs bare + hermetic guard; reconcile PromQL literals to sk-api_3.2.0 (MLBL-01/04/05) (completed 2026-06-06 — 26/26 hermetic Observability GREEN; LogsResourceBareNameFacts 1/1; SK_P.sln 0/0 Release; 0 bare service_name literals; commits 013bc0a, 39792b3, 4d67977)
  - [x] 38-03-PLAN.md - MeterProviderHolder swap (Model A1): placeholder->DB service.name on identity-resolve in Loop A; hermetic MeterProviderHolderFacts (MLBL-03) (completed 2026-06-06 — MeterProviderHolderFacts 1/1 GREEN; orchestrator-drive tests 5/5 GREEN; 27/27 hermetic Observability GREEN; SK_P.sln 0/0 Debug+Release; commits 69bbd55, ef209d0, e41a475)
  - [x] 38-04-PLAN.md - RealStack scrape gate: combined service_name + non-empty service_instance_id across runtime/HTTP/business; DB-sourced processor series; appsettings-retained + MLBL-05 inventory (MLBL-01/02/03/05) (completed 2026-06-06 — RealStack MetricsRoundTripE2ETests 1/1 GREEN after container rebuild; scrape proof sk-api_3.2.0/orchestrator_3.4.0 + DB-sourced sample-proc-..._1.0.0; 479/479 hermetic GREEN; SK_P.sln 0/0 Release; 0 bare service_name literals; commit 30a23d7)

### Phase 39: Keeper Observability + Real-Stack E2E + Close Gate

**Goal**: Register the Keeper meter + throughput/saturation instruments, then prove the full recover-and-give-up behavior live against the real stack and lock a 3×GREEN triple-SHA net-zero close gate.
**Depends on**: Phase 38 (the final metric-label contract the gate seals) + Phase 37
**Requirements**: KMET-01, KMET-02, KMET-03, TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):

  1. A code-defined `Keeper` meter is registered per the house pattern (snake_case, no `_total` suffix, inherited `service_instance_id`).
  2. Throughput/outcome counters (`keeper_fault_consumed`, `keeper_recovered`, `keeper_dlq_pushed{reason}`, `keeper_workflow_paused`, `keeper_workflow_resumed`, `keeper_l2_probe_failed`) and saturation/latency signals (`keeper_in_flight` UpDownCounter, `keeper_recovery_duration` histogram) are emitted and Prometheus-scrapable, labeled by `processorId` where meaningful with no high-cardinality `workflowId`.
  3. A real-stack E2E induces an L2 outage that dead-letters both an `EntryStepDispatch` and an `ExecutionResult`, then proves Keeper pauses the workflow, recovers on L2 return, resumes, and re-injects each to origin with exactly-once downstream effect (no duplicate).
  4. A real-stack E2E proves the give-up path: L2 stays down past max-attempts → message in `keeper-dlq`, workflow stays paused, `keeper_dlq_pushed` increments.
  5. The close gate runs 3× consecutive GREEN with triple-SHA (psql `\l` / redis `--scan` / rabbitmqctl `list_queues`) BEFORE==AFTER — including both DLQs + probe scratch-key scan-clean (net-zero) — at Release+Debug 0-warning.

**Plans**: 4 plans (4 waves)

  - [x] 39-01-PLAN.md — KeeperMetrics meter (6 counters + UpDownCounter + Histogram) + Program.cs AddMeter symmetry + Wave-0 DiagnosticSource version gate (KMET-01/03)
  - [x] 39-02-PLAN.md — Instrument both fault consumers + L2ProbeRecovery + hermetic KeeperMetricsFacts (KMET-01/02/03) (completed 2026-06-06 — 6 increment sites/consumer + in_flight ++/finally-- + l2_probe_failed; RunAsync threads procId; 11/11 hermetic GREEN via MeterListener; SK_P 0/0 Release; 047531d, 7fe4db4, f347dc8)
  - [x] 39-03-PLAN.md — Extend the two KeeperRecovery RealStack facts with keeper_* Prometheus scrape assertions + Wave-0 histogram-suffix gate (TEST-01/02) (completed 2026-06-06 — recover fact: fault_consumed/recovered/workflow_paused/workflow_resumed/recovery_duration_seconds_count{recovered}; give-up fact: dlq_pushed{probe_exhausted,result}/recovery_duration_seconds_count{gave_up}/l2_probe_failed; all ProcessorId-filtered via PollPromForQuery + non-empty service_instance_id + no-workflowId ban; PromPollTimeoutMs=120_000; Wave-0 suffix written _seconds per Plan 01, live-confirm on 39-04 gate; test build 0/0 Release; 9e938eb)
  - [x] 39-04-PLAN.md — Clone phase-39-close.ps1 (triple-SHA + keeper rebuild + both-DLQ depth==0) + live 3xGREEN gate (TEST-03)

### Phase 40: Keeper Recovery Hardening

**Goal**: A persistent (non-transient) fault can no longer flood the stack, the give-up-park drain is deterministic so the close gate's `keeper-dlq depth==0` invariant holds every run, and the two Keeper fault consumers share one recovery body so future recovery changes land in a single place.
**Depends on**: Phase 39 (close gate + KeeperMetrics in place)
**Requirements**: KHARD-01, KHARD-02, KHARD-03
**Gap Closure**: Closes the two functional tech-debt items + IN-01 from `.planning/v3.7.0-MILESTONE-AUDIT.md`.
**Success Criteria** (what must be TRUE):

  1. The recover→reinject cycle is bounded by a config attempt cap; when the cap is reached for a given `H`, Keeper parks the original `Fault<T>` to `keeper-dlq` (give-up) instead of reinjecting again — a persistent fault converges to a single park, not an unbounded reinject loop. A hermetic test proves the cap is honored (no reinject after cap; exactly one park).
  2. The give-up RealStack E2E teardown drains `keeper-dlq` with a poll-until-stably-empty strategy (bounded), so `scripts/phase-39-close.ps1` (or the Phase-40 gate) yields `keeper-dlq depth==0` deterministically across the 3× cadence — no late give-up park races the AFTER snapshot.
  3. The recover/probe/re-inject/park/pause/resume logic shared by `FaultEntryStepDispatchConsumer` and `FaultExecutionResultConsumer` is extracted into one shared helper/base; both consumers delegate to it (no near-total duplication); KHARD-01's cap exists in exactly one place. Hermetic suite stays GREEN, Release 0-warning.

**Plans**: 3 plans (2 waves)
Plans:

- [x] 40-01-PLAN.md -- KHARD-03: extract one shared KeeperRecoveryHandler; both consumers delegate (keystone, Wave 1)
- [x] 40-02-PLAN.md -- KHARD-01: per-H recover-attempt cap + hermetic cap test (Wave 2)
- [x] 40-03-PLAN.md -- KHARD-02: poll-until-stably-empty keeper-dlq drain in the give-up E2E teardown (Wave 2)

### Phase 41: Orchestrator Pause/Resume Diagnostics

**Goal**: A Resume dropped during the narrow fire window is diagnosable, and the scheduler's reschedule fallback cannot throw on a purged non-durable job.
**Depends on**: Phase 37
**Requirements**: (closes 37-REVIEW WR-01, WR-02 — code-quality, no new REQ-IDs)
**Gap Closure**: Closes the Phase-37 code-quality warnings from the v3.7.0 audit.
**Success Criteria** (what must be TRUE):

  1. `WorkflowLifecycle.ResumeAsync` emits an informational log on the `state != TriggerState.Paused` ignore branch (WorkflowId + observed state), so a Resume that arrives mid-fire and is dropped is observable in logs.
  2. `WorkflowScheduler.RescheduleAsync` no longer assumes the non-durable job still exists on the `RescheduleJob`-returns-null fallback path — it either re-creates the job+trigger or fails loudly with a clear message rather than an opaque Quartz throw. Hermetic test covers the fallback path.

**Plans:** 2/2 plans complete

- [x] 41-01-PLAN.md — WR-01: informational log on the ResumeAsync non-Paused ignore branch (WorkflowId + observed TriggerState; log-only, no re-arm) (Wave 1) (completed 2026-06-07 — 9e14eeb; LogInformation on ignore branch, no behavioral re-arm per D-01/D-02; hermetic 504/0, Release 0-warning)
- [x] 41-02-PLAN.md — WR-02: thread workflowId into RescheduleAsync + re-create full job+trigger in the purged-job fallback so it cannot throw; hermetic fallback test (Wave 1) (completed 2026-06-07 — de0cec0/0edeb53; 4-arg RescheduleAsync re-creates job+trigger in null-fallback per D-04, RescheduleSchedulingTests asserts re-establishment per D-06; hermetic 505/0, Release 0-warning)

### Phase 42: v3.7.0 Docs & Traceability Reconciliation

**Goal**: REQUIREMENTS.md and ROADMAP.md tell the truth about v3.7.0 before archival — every satisfied requirement is checked, MLBL is in the traceability table, counts are correct, and the close-gate phase has a VERIFICATION.md.
**Depends on**: Phases 40, 41 (so the doc pass reflects final state)
**Requirements**: (doc-only — no new REQ-IDs)
**Gap Closure**: Closes the documentation-drift tech-debt items from the v3.7.0 audit.
**Success Criteria** (what must be TRUE):

  1. REQUIREMENTS.md checkboxes for all satisfied v3.7.0 reqs read `[x]` (INTAKE-01..04, PROBE-01..06, DLQ-01..04, PAUSE-01..05, KMET-01..04) and their traceability rows reflect "Complete (Phase-39 live gate)" rather than "Not started".
  2. MLBL-01..05 rows exist in the REQUIREMENTS.md traceability table mapped to Phase 38; the coverage footer reads the correct totals (34 requirements across phases 33-39, plus the gap-closure KHARD set) — no stale "29 / 6 phases / 33-38".
  3. The ROADMAP.md progress table Phase-38 row reads its true plan count + "Complete" (not "0/? Not started").
  4. `39-VERIFICATION.md` exists for the close-gate phase, recording the 3×500 GREEN triple-SHA result and the accepted keeper-dlq drain-timing follow-up.

**Plans:** 3/3 plans complete

- [x] 42-01-PLAN.md - REQUIREMENTS.md: flip satisfied checkboxes [x] + reconcile traceability rows + add MLBL-01..05 + correct coverage footer (SC1, SC2)
- [x] 42-02-PLAN.md - ROADMAP.md: fix Progress-table Phase-38 row to 4/4 Complete (SC3)
- [x] 42-03-PLAN.md - backfill 39-VERIFICATION.md from close-gate evidence (SC4)

</details>

## Progress

**Execution Order:**
Phases execute in numeric order: 25 → 26 → 27 → 28 → 29 → 30 → 31 → 31.1 → 32 → 32.1 → 33 → 34 → 35 → 36 → 37 → 38 → 39 → 40 → 41 → 42 → 43 → 44 → 45 → 46 → 47 → 48 → 49 → 50 → 51 → 52 → 53 → 54 → 55 → 56 → 57 → 58

| Phase | Milestone | Plans Complete | Status   | Completed  |
| ----- | --------- | -------------- | -------- | ---------- |
| 1-11  | v3.2.0    | 41/41          | Complete | 2026-05-28 |
| 12-16 | v3.3.0    | 26/26          | Complete | 2026-05-29 |
| 17    | v3.4.0    | 2/2            | Complete | 2026-05-30 |
| 18    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 19    | v3.4.0    | 4/4            | Complete | 2026-05-30 |
| 20    | v3.4.0    | 4/4            | Complete | 2026-05-31 |
| 21    | v3.4.0    | 1/1            | Complete | 2026-05-31 |
| 22    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 23    | v3.4.0    | 5/5            | Complete | 2026-05-31 |
| 24    | v3.4.0    | 5/5            | Complete | 2026-06-01 |
| 24.1  | v3.4.0    | 1/1            | Complete | 2026-06-01 |
| 25. Shared Contracts + WebApi Responders | v3.5.0 | 2/2 | Complete    | 2026-06-01 |
| 26. BaseProcessor.Core — Library, Identity & Liveness | v3.5.0 | 3/3 | Complete    | 2026-06-01 |
| 27. Execution Round-Trip | v3.5.0 | 3/3 | Complete | 2026-06-02 |
| 28. SourceHash Identity + Processor.Sample + E2E Closeout | v3.5.0 | 4/4 | Complete    | 2026-06-02 |
| 29. Structured Execution-Scope Logging | v3.5.0 | 5/5 | Complete    | 2026-06-02 |
| 30. Runtime & Business Metrics | v3.5.0 | 4/4 | Complete    | 2026-06-02 |
| 31. Idempotent Execution Round-Trip (Exactly-Once-Effect) | v3.6.0 | 6/6 | Complete    | 2026-06-04 |
| 31.1 Close-Gate Redis Net-Zero (gap closure) | v3.6.0 | 1/1 | Complete | 2026-06-04 |
| 32. Cancelled Circuit-Breaker | v3.6.0 | 5/6 | Superseded by 32.1 | — |
| 32.1 Dead-Letter on Exhaustion (Breaker Reverted) | v3.6.0 | 2/2 | Complete    | 2026-06-05 |
| 33. Fault-Recovery Spike (De-Risk) | v3.7.0 | 2/2 | Complete    | 2026-06-05 |
| 34. Keeper Console Foundation | v3.7.0 | 3/3 | Complete    | 2026-06-05 |
| 35. Fault Intake & Correlation | v3.7.0 | 3/3 | Complete    | 2026-06-05 |
| 36. L2 Health-Probe Recovery Loop & DLQs | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 37. Orchestrator Pause/Resume Coordination | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 38. Uniform `service_name` + Instance Labels Across All Metrics | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 39. Keeper Observability + Real-Stack E2E + Close Gate | v3.7.0 | 4/4 | Complete    | 2026-06-06 |
| 40. Keeper Recovery Hardening (gap closure) | v3.7.0 | 3/3 | Complete (live gate Manual-Only) | 2026-06-06 |
| 41. Orchestrator Pause/Resume Diagnostics (gap closure) | v3.7.0 | 2/2 | Complete    | 2026-06-06 |
| 42. v3.7.0 Docs & Traceability Reconciliation (gap closure) | v3.7.0 | 3/3 | Complete    | 2026-06-07 |
| 43. Message Contracts & L2 Key Reshape | v4.0.0 | 5/5 | Complete | 2026-06-08 |
| 44. Pre/In/Post-Process Pipeline | v4.0.0 | 3/3 | Complete | 2026-06-08 |
| 45. Keeper BIT Health Gate + Global Pause/Resume | v4.0.0 | 3/3 | Complete | 2026-06-08 |
| 46. Keeper 5-State Recovery + Orchestrator Per-Item Consume | v4.0.0 | 4/4 | Complete | 2026-06-09 |
| 47. DLQ Consolidation + At-Least-Once Semantics | v4.0.0 | 3/3 | Complete | 2026-06-09 |
| 48. v3.x Teardown | v4.0.0 | 3/3 | Complete | 2026-06-09 |
| 49. Live-Proof Close Gate | v4.0.0 | 6/6 | In Progress (live gate operator-gated) | — |
| 50. Contracts & Slot-Array L2 Key Reshape | v5.0.0 | 2/2 | Complete    | 2026-06-11 |
| 51. Processor Forward + Recovery Pipeline | v5.0.0 | 3/3 | Complete    | 2026-06-11 |
| 52. 3-State Keeper | v5.0.0 | 3/3 | Complete    | 2026-06-11 |
| 53. Model-B Teardown | v5.0.0 | 3/3 | Complete    | 2026-06-11 |
| 54. Live Proof & Close Gate | v5.0.0 | 4/4 | Complete    | 2026-06-11 |
| 55. Live Proof & Close Gate | v5.0.0 | 4/4 | Complete    | 2026-06-12 |
| 56. Typed Base-Config Seam | v6.0.0 | 2/2 | Complete    | 2026-06-12 |
| 57. Startup Config-Schema Fetch + Gate A | v6.0.0 | 4/4 | Complete    | 2026-06-12 |
| 58. Orchestration-Gate Integration Proof & Close | v6.0.0 | 5/5 | Complete    | 2026-06-12 |

## ⚠️ v9.0.0 Canonical Two-Consumer Recovery & L2 Delivery Proof — COMPLETE 2026-07-18 (Phases 69-81), UNARCHIVED (documented debt — REQUIREMENTS.md overwritten by v10.0.0; no milestones/v9.0.0-* archive)

> Phases 69–74. Align the processor/orchestrator pipeline to the canonical two-consumer (Pre-Process + Post-Process) recovery design, make the processor always-write to L2 under a uniform branch-free orchestrator pre-pipeline, prove + instrument end-to-end L2 data delivery across the fan-out DAG (`A→B→C→{D1→E1→F1, D2→E2→F2}→G`), and reshape the business metrics into a uniform two-counter model. (Milestone declared retroactively 2026-06-18 — phases 69–74 predate the heading; see STATE Roadmap Evolution.)

### Phase 69: Align processor pipeline to canonical recovery spec: atomic index+data write with single INJECT (close INFRA-01 drop) and gated forward cleanup

**Goal:** [To be planned]
**Requirements**: TBD
**Depends on:** Phase 68
**Plans:** 5 plans

Plans:

- [ ] TBD (run /gsd-plan-phase 69 to break down)

### Phase 70: Two-consumer processor design (Pre-Process + Post-Process) with keeper recovery

**Goal:** Replace the single-consumer `ProcessorPipeline` slot-array model in-place with the pinned two-consumer design — a Pre-Process consumer gating on `L2[entryId]` whose `ProcessAsync` seam returns one `DataResult` (downstream) or spawns N to a new Post-Process consumer and returns null (entry), output keyed by `messageId` and written only when `result == completed`, with keeper states `REINJECT`/`INJECT`/`DELETE` redefined accordingly. (Source of truth: `70-SPEC.md` — 12 requirements + pinned pseudocode; D-01..D-17.)
**Requirements**: SPEC-req-1 .. SPEC-req-12 (the 12 numbered requirements in 70-SPEC.md)
**Depends on:** Phase 69
**Plans:** 5/5 plans complete

Plans:

- [x] 70-01-PLAN.md — Contract foundation: DataResult record + L2ProjectionKeys.OutputData (delete MessageIndex) + reshape KeeperInject/Delete/Reinject (SPEC-req-6/7/8/11) [Wave 1] ✓ DataResult.cs (new) + OutputData/MessageIndex-removed + KeeperInject embeds DataResult + KeeperDelete entryId-only + KeeperReinject gains MessageId; Messaging.Contracts 0/0 Release+Debug; commits e5b752c, e149d88
- [x] 70-05-PLAN.md — Mark `docs/design/processor-keeper-recovery-spec.md` superseded-by-Phase-70 (SPEC-req-12) [Wave 1]
- [x] 70-02-PLAN.md — Reshape keeper consumers (INJECT DataResult-driven + OutputData write; DELETE single-key; REINJECT envelope MessageId override) + keeper facts (SPEC-req-6/7/8/11/12) [Wave 2]
- [x] 70-03-PLAN.md — Processor core rewrite: seam->DataResult? + SpawnToPost/DeleteEntry + shared OutputTail + PostProcessConsumer + linear Pre flow (gate L2[entryId], no-delete-on-invalid) + -post bind + two-mode Sample + delete ProcessItem/ProcessOutcome (SPEC-req-1/2/3/4/5/9/10/11) [Wave 2]
- [x] 70-04-PLAN.md — Processor test migration: DataResult? doubles + MessageId-capture + Pre/Post/OutputTail/Seam/Sample facts; whole-suite green + 0-warning dual-config (SPEC-req-1..12) [Wave 3]

### Phase 71: Orchestrator two-consumer design (Pre-Process + Post-Process) with keeper recovery

**Goal:** Mirror the Phase-70 processor two-consumer pattern on the orchestrator side and close the Phase-70-deferred `entryId`↔`messageId` threading (A1). A **Pre-Process** consumer gates on `L2[entryId]` (reads the upstream step's output from the `out` namespace), resolves next steps from the graph, fans out one **Post-Process** message per next step (carrying `payload`+`data`), then deletes `L2[entryId]`; a **Post-Process** consumer writes the next step's input `data` to `L2[messageId]` (`data` namespace, TTL'd) and dispatches `EntryStepDispatch` to the processor with `entryId=messageId`. Keeper states `REINJECT`/`INJECT`/`DELETE` redefined with envelope-`MessageId` override. Resilience identical to Phase 70 (no `_error`, `UseMessageRetry` none, send→bounded-retry→throw→broker redelivery, every L2 op→keeper on exhaustion). **Closes A1:** the processor result stamps `EntryId = its output messageId` (replacing the `Guid.Empty` placeholder). Open points to lock in SPEC: (1) resolve-next-steps failure must throw/park — **never silent-ack** (the one no-silent-loss hole); (2) `executionId` threaded unchanged; (3) REINJECT clean-absent drop; (4) namespace cross-pairing (orchestrator reads `out`, writes `data`); (5) merge/fan-in scope decision.
**Requirements**: REQ-71-01..REQ-71-12 (locked via 71-SPEC.md — 12 requirements; SPEC is authoritative, mapped 1:1 to SPEC requirements 1-12)
**Depends on:** Phase 70
**Plans:** 3/3 plans complete

Plans:

- [x] 71-01-PLAN.md — Shared contracts (NextStepHandoff + 3 orchestrator keeper contracts + ResultPost queue const) + A1 close at both stamp sites (OutputTail + keeper InjectConsumer) ✅ 2026-06-17 (44da475, a0a5631, c39d0e3) — REQ-71-07, REQ-71-10; touched slice 9/9 green
- [x] 71-02-PLAN.md — Orchestrator Pre/Post core: OrchestratorPrePipeline (gate/read out: -> SelectNext -> fan-out -> delete out:) + RelocateTail + Post consumer + two distinct trip-end LOG lines (metric DEFERRED) + TypedResultConsumer reshape + test migration ✅ 2026-06-17 (bc47286, 9e464ce, a691771) — REQ-71-01..06, REQ-71-11, REQ-71-12; touched slice 29/29 green
- [x] 71-03-PLAN.md — Keeper orchestrator recovery consumers (REINJECT/INJECT/DELETE) + binder/Program wiring + partition/firewall facts ✅ 2026-06-17 (9533aed, 382d62d, 9b12f53) — REQ-71-08, REQ-71-09, REQ-71-10, REQ-71-11; Keeper slice 21/21 green, firewall held

### Phase 72: Processor always-write to L2 and uniform orchestrator pre-pipeline with unresolved-step metric

**Goal:** Make every *business* processor result write its `DataResult.Data` blob to L2 — output-definition validation only flips `result`→`Failed`, it never gates the write (`OutputTail.cs:60`); a thrown `ExecuteAsync` is caught into a `DataResult{failed}` carrying the read `validatedData` (`ProcessorPipeline.cs:79`) as fallback — so `StepFailed`/`StepCancelled` carry a real `EntryId` instead of `Guid.Empty` (`:199,202`). With every outcome guaranteed a blob, drop the Completed-only `out:` read/delete branches in `OrchestratorPrePipeline` (`:84,114`) so all four `TypedResultConsumer<T>` shells are uniform: exists-gate `L2[entryId]` → read → resolve `L1[stepId]` → terminal check → per next step (read `L1[nextStepId]` + entry-condition + send handoff) → delete `L2[entryId]`; clean-absent L2 → idempotent skip; `ExecutionId` threading preserved (D-13). Folds in the Phase-71-deferred trip-end metric: a new `orchestrator_step_unresolved` counter (label `workflowId` only) incremented at the two L1-resolution misses (stage-1 `L1[stepId]` miss → return; stage-3 `L1[nextStepId]` miss → continue), injecting `OrchestratorMetrics` into `OrchestratorPrePipeline`. Distinct from the imported Phase-72 metrics SPEC (uniform two-counter reshape) — that lands separately via /gsd-import.
**Requirements**: SPEC-1..6 (see 72-SPEC.md) - always-write tail; real EntryId; thrown-ExecuteAsync->validatedData; orchestrator_step_unresolved counter; uniform pre-pipeline; naming/DI conventions
**Depends on:** Phase 71
**Plans:** 3/3 plans complete

Plans:

- [x] 72-01-PLAN.md - Processor always-write: OutputTail writes the out: blob for all 3 terminal outcomes + real EntryId on Failed/Cancelled; ProcessorPipeline catch+input-fail paths route through OutputTail carrying validatedData (SPEC-1,2,3,6) [wave 1] ✅ 2026-06-17 (c7fdbc0, e2076f2) — gate widened to `result != Processing`, Failed/Cancelled stamp `EntryId = dr.MessageId`, DataResult gains ErrorMessage/CancellationMessage, dead builders removed; 27/27 hermetic GREEN, 0-warning Debug+Release
- [x] 72-02-PLAN.md - Orchestrator foundation: Wave-0 MeterListener capture seam + SelectNext->{Matches,UnresolvedIds} reshape + orchestrator_step_unresolved counter definition (SPEC-4,5,6) [wave 1] ✅ 2026-06-17 (c362835, 5555427, 7d3e390) — zero-dep MeterCollector seam, SelectNext returns pure SelectNextResult (dangling ids surface in UnresolvedIds), StepUnresolved counter via existing IMeterFactory; all SelectNext call sites migrated to .Matches; 17/17 hermetic GREEN, 0-warning Debug+Release
- [x] 72-03-PLAN.md - Uniform branch-free OrchestratorPrePipeline + clean-absent skip + stage-1/stage-3 metric increments (SPEC-4,5,6) [wave 2, depends 72-02] ✅ 2026-06-17 (b501557, 88dbd3f) — dropped both `outcome == StepOutcome.Completed` gates (read/fan-out/delete uniform for every outcome, D-09); three-way read (present/clean-absent ack-skip/fault REINJECT, Processing rides the skip, D-05/D-10); OrchestratorMetrics ctor-injected + stage-1/stage-3 orchestrator_step_unresolved increments (workflowId label, continue never throws); OrchestratorPrePipelineFacts 15/15 + affected hermetic 40/40 GREEN, 0-warning Debug+Release. **Phase 72 COMPLETE (3/3 plans).**

### Phase 73: Verify and instrument end-to-end L2 data delivery across a fan-out workflow DAG per correlationId and executionId, with a hermetic zero-Docker metric-conservation proof and a live-stack per-executionId ES auditor, plus execution-trip duration measurement

**Goal:** Prove and instrument end-to-end L2 data delivery across the DAG `A→B→C→{D1→E1→F1, D2→E2→F2}→G` (G = one shared, per-arrival non-joining terminal) per `(correlationId, executionId)` via (a) a hermetic zero-Docker harness proving fan-in `entryId` correctness + deterministic per-hop value integrity, and (b) a live-stack per-`executionId` ES auditor asserting the deterministic value chain (each hop +1 from fixed seeds 100/200), `Step_G` ×2 convergence at the expected terminal value, terminal `skp:out:` anchor, and per-exec/per-corr trip duration — with one production-code edit (deterministic seed + value-clarifying log inside `SampleProcessor.ProcessAsync`). Locked in 73-SPEC.md (7 requirements; sha256 → deterministic value tracking; metrics deferred; live Docker run deferred-automated).
**Requirements**: SPEC-R1..SPEC-R7 (locked in 73-SPEC.md)
**Depends on:** Phase 72
**Plans:** 4/4 plans complete

Plans:

- [x] 73-01-PLAN.md — Wave 1: the single src/ edit — SampleProcessor.ProcessAsync deterministic seed (100/200) + Mode-1 received→produced value log (SPEC-R3, R4) — complete 2026-06-17; fixed 100/200 seed (random removed), Mode-1 `{StepLabel} received {Received} produced {Produced}` ES-attribute contract locked, Debug+Release 0-warning. Commits f8dbcfa, 60e4200, 5285a5f.
- [x] 73-02-PLAN.md — Wave 2: hermetic harness — stateful dict-backed IDatabase L2 fake + full-DAG driver over the real classes + fan-in entryId + value-integrity assertions through convergent G (SPEC-R1, R2, R3) — complete 2026-06-17; DictBackedL2Fake (ConcurrentDictionary L2 store, real virtual StringSetAsync overload) + FanInHermeticHarnessFacts drive the REAL pipeline + REAL SampleProcessor over A→B→C→{D1→E1→F1,D2→E2→F2}→G; 5/5 facts GREEN (per-hop 101→106/201→206, G ×2 non-joining order-independent on distinct entryId/out: blobs, no cross-exec contamination), Debug+Release 0-warning. Commits 8afe5ac, d80315e, c9c0050.
- [x] 73-03-PLAN.md — Wave 2: auditor pure-model core — RunTrace/PassFailEngine/AnalyzerReport (10-label set, Step_G ×2 multiplicity, value-chain verdict, trip-duration fields) + synthetic engine facts (SPEC-R5, R6) — complete 2026-06-17; RunTrace HasIllegitimateDuplicate + Values map, PassFailEngine 10-label AllLabels + value-chain folded into pass (Step_G terminal seed+6 anchor proxy), AnalyzerReport ValueChainOk/ValueChainDetail/TripDurationMsBy{Execution,Correlation}; 8 new PassFailEngineValueChainFacts (green + every fail mode) + 9 migrated PassFailEngineFacts, 17/17 GREEN, Debug+Release 0-warning. Commits 6d81532, 0451e76, bf0eb9f.
- [x] 73-04-PLAN.md — Wave 3: live wiring — seeder G + number=1 (10/10/10), RealStack fixture value-attribute read + trip duration, phase-73-sweep.ps1 (SPEC-R1, R3, R5, R6, R7) — complete 2026-06-17; seeder G-extended DAG (10 steps/10 edges/10 assignments, Step_G lone zero-outgoing sink reachable from F1+F2, every payload number=1), RealStack auditor reads attributes.Produced into the per-label Values map + computes per-(corr,exec)/per-corr trip duration from the ES @timestamp min→max span (no new query) → feeds the Plan-03 extended FromLabels/Analyze (Step_G ×2 retained, seed+6 ES log = live terminal-anchor proxy, ES-read-only/no Redis read/no metric assertion), scripts/phase-73-sweep.ps1 operator-runnable seed→round-trip→audit sweep (live Docker deferred-automated, not a phase gate, PARSE_OK), Debug+Release 0-warning. Commits a7f14cc, 5b89e2a, d5caff6.

### Phase 74: Reshape business metrics into a uniform two-counter model

**Goal:** Collapse every service's assorted business counters into a uniform pair `{service}_messages_consumed` / `{service}_messages_sent` (snake_case, no `_total` suffix in code — collector appends it), labeled `workflowId`+`processorId` (camelCase). Rewrite `OrchestratorMetrics` / `ProcessorMetrics` / `KeeperMetrics` to the pair + removals (`ResultDeduped`, `DispatchDeduped`, `ReinjectDropped`; drop the processor `outcome` label); inject `KeeperMetrics` into the 4 sending keeper consumers + `BitHealthLoop` via the existing `IMeterFactory` DI pattern; rework `PassFailEngine` + `PromCounterSnapshot` + the ~11 affected metric test files to the new names (drop the `outcome` corroboration WARNING path). (Source of truth: drafted spec at `Import/72-SPEC.md` + `Import/72-CONTEXT.md` — slugged "72" but renumbered here to 74 since phases 72/73 already exist.)
**Requirements**: REQ-1, REQ-2, REQ-3, REQ-4, REQ-5, REQ-6 (locked in 74-SPEC.md)
**Depends on:** Phase 73
**Plans:** 4/4 plans complete

Plans:

- [x] 74-01-PLAN.md — Wave 1: Orchestrator uniform counters (rewrite OrchestratorMetrics; rebind StepDispatcher/PrePipeline/RelocateTail/TypedResultConsumer; count every outbound send) (REQ-1, REQ-6) — COMPLETE (`a51740c`/`0da5350`; 10/10 hermetic facts; 0-warning Debug+Release)
- [x] 74-02-PLAN.md — Wave 1: Processor uniform counters (rewrite ProcessorMetrics; drop outcome label + ResultOutcome helper; rebind EntryStepDispatchConsumer/OutputTail) (REQ-2, REQ-6) — COMPLETE (`e4e7944`/`3a441fb`; PostProcessConsumer consume site also rebound; 6/6 hermetic facts; 0-warning Debug+Release)
- [x] 74-03-PLAN.md — Wave 1: Keeper uniform counters + keeper_l2_probe (rewrite KeeperMetrics; base Consume choke + CountSent; wire 6 consumers; BitHealthLoop heartbeat) (REQ-3, REQ-4, REQ-6) — COMPLETE (`78267e6`/`b6fe38d`/`890522e`; RED test `be0086c`; KeeperMetricsFacts 3/3 + 6 consumer fact classes + BitHealthLoopTests green; full hermetic 668/668; 0-warning Debug+Release)
- [x] 74-04-PLAN.md — Wave 2 (converging): analyzer rebind (PromCounterSnapshot/PassFailEngine, drop outcome WARNING) + migrate ~11 metric test files + 3 absence asserts + invert workflowId guard + hermetic & 0-warning gate (REQ-1, REQ-2, REQ-3, REQ-5, REQ-6) — COMPLETE (`43b7460`/`a7dc9b4`/`6b7b6d3`; metric-facts subset 38/38 + full hermetic 668/668 green; 0-warning Debug+Release; src/ clean of removed counter names; live-stack close gate deferred-automated — Docker unavailable)

### Phase 75: Per-execution recovery verdict — decouple the sweep from tunable constants

**Goal:** Rework the resilience sweep so the PASS/FAIL verdict is a pure function of per-started-execution recovery, decoupled from every tunable constant (cron rate, window seconds, absolute in-flight bound). A green result must reflect real recovery — never a benign firing rate or time slot. (1) Keep per-`(correlationId, executionId)` as the started-execution denominator (already in `PassFailEngine`). (2) Drop the absolute `MaxInFlightLoss=4` bound from `PassFailEngine` — it is the cron-rate coupling. (3) Replace it with a **recoverability classifier**: each started-but-incomplete execution is classified from the recovery machinery's own evidence — *recoverable-but-not-recovered* → binding FAIL; *provably-unrecoverable* (data physically gone: L2 wiped/expired, no outbox) → reported + cause-labeled, not a pass/fail lever. (4) Derive "unrecoverable" from keeper actions per `entryId` (REINJECT vs the `ReinjectConsumer` silent-DROP on `STRLEN L2[entryId]==0`) — requires keeper→Elasticsearch instrumentation keyed by `entryId`/`messageId` so the analyzer can join keeper outcomes to lost executions. (5) **Spec ruling (approved):** in-flight-at-wipe loss (data only in L2 when L2 wiped, given no outbox) is accepted/tolerated; everything recoverable-but-lost is a binding FAIL. (6) Neutralize the TTL confounder: raise the test-env L2 TTLs (`ExecutionDataTtl`, `OutputDataTtl`, unified slot-array index TTL) to outlast each tested module's full recovery (dwell + return-to-healthy + keeper reinject + drain, ~300s+) so TTL-expiry cannot manufacture loss for the non-redis-crash scenarios (processor/orchestrator/keeper/rabbitmq); for redis-crash scenarios (TEST-05/07) TTL is moot (redis is the wiped store) — that is the genuine in-flight-at-wipe durability boundary. (7) Optionally make the observation window execution-based (run until K executions observed) rather than a fixed 300s wall-clock; `ConservationTol=±1` is already non-binding + drain-settled. (8) Preserve the committed value-chain in-flight-loss fix (`3028f43`).
**Requirements**: D75-1, D75-2, D75-3, D75-4, D75-5, D75-6, D75-7, D75-8 (the 8 numbered ROADMAP goal decisions)
**Depends on:** Phase 74
**Plans:** 5/5 plans complete

Plans:

- [x] 75-01-PLAN.md — Wave 1: verdict rework — drop MaxInFlightLoss bound + keeper-evidence recoverability classifier + preserve 3028f43 skip-set + lock per-(corr,exec) denominator (D75-1, D75-2, D75-3, D75-5, D75-8) ✅ COMPLETE 2026-07-15 (28/28 engine facts GREEN, 0-warning Debug+Release)
- [x] 75-02-PLAN.md — Wave 1: keeper→ES instrumentation — widen REINJECT drop log + add reinject-success log with (CorrelationId, ExecutionId, EntryId, MessageId, ReinjectOutcome) join keys (D75-4) ✅ COMPLETE 2026-07-15 (6/6 ReinjectConsumerFacts GREEN, 0-warning Debug+Release; commits 5517a58, b00b702)
- [x] 75-03-PLAN.md — Wave 1: neutralize TTL confounder — raise the five L2 ExecutionDataTtl/OutputDataTtl knobs 300→900 in lock-step (D75-6) [live neutralization deferred-automated] ✅ COMPLETE 2026-07-15 (all TTL knobs 300→900 incl. compose env override; 0-warning; live neutralization deferred-automated; commit e8e9732)
- [x] 75-04-PLAN.md — Wave 2 (depends 75-01, 75-02): analyzer live join — ES-read keeper outcomes → per-(corr,exec) map → feed classifier via keeperOutcomeByExecution (D75-4) [live join deferred-automated] ✅ COMPLETE 2026-07-15 (ReinjectOutcomeFieldPath const + BuildKeeperOutcomeSearchBody + BuildKeeperOutcomeMap w/ reinject-wins tie-break + TraceCohort.KeeperOutcomeByExecution + Analyze wiring; 34/34 analyzer/keeper facts GREEN, 0-warning Debug+Release; live E2E join deferred-automated — Docker-less; commit 2aa63e5)
- [x] 75-05-PLAN.md — Wave 1: OPTIONAL execution-based observation window — default-off K_EXECUTIONS harness seam, wall-clock hard cap preserved (D75-7) [optional/deferrable] ✅ COMPLETE 2026-07-15 (K_EXECUTIONS seam default-off, 300s wall-clock hard cap retained; PARSE_OK; live-verify deferred-automated; commit 90fb59f)

### Phase 76: Framework-emitted per-hop execution logs keyed by stepId — decouple the resilience verdict from the concrete processor

**Goal:** Move the resilience verdict's evidence base from author-written logs onto framework-emitted structural telemetry, so the sweep proves recovery for **any** processor rather than only `Processor.Sample`. Today the binding half of the verdict (`missing == 0`, `dupFail`, `valueChainOk`) is derived entirely from one `LogInformation` call the author wrote in `SampleProcessor.ProcessAsync` (`src/Processor.Sample/SampleProcessor.cs:89`), whose `Step_*` label is a *config-payload string* the analyzer then set-compares against a hardcoded `HopLabels` list (`PassFailEngine.cs:65`). The framework emits **no happy-path per-hop log at all** (`BaseProcessor.Core/Processing/` carries only fault-path warnings) — so "did this step execute" depends on an author remembering to log it. Two layers result: **(a) Framework** — `ProcessorPipeline` emits ONE per-hop log keyed by `StepId` + `ExecutionId` + `CorrelationId` + `EntryId`/`MessageId` + outcome, identities only, reusing the existing `ExecutionLogScope.StepId` → `attributes.StepId` bridge (every id is already in hand at the `ProcessAsync` call site: `d.StepId`/`d.WorkflowId`/`d.ProcessorId`/`d.ExecutionId`/`d.EntryId`). The analyzer's completeness check re-keys onto `stepId` — the real graph identity — and `HopLabels` is DELETED, ending the naming-convention-as-identity coupling. **(b) Sample** — KEEPS its `received/produced` line as the domain value oracle: the framework sees `validatedData`/`dr.Data` as opaque JSON and cannot log `Received=105 Produced=106` semantically, and that oracle is what the committed TELEMETRY-GAP reconciliation (`6e90216`) uses to prove a trace-incomplete run completed (terminal `Step_G == seed + 6`). Stretch: an orchestrator per-dispatch log makes *expected* = stepIds dispatched and *actual* = stepIds completed, **both read from ES** — closing the TEST-08 blind spot (untouched work is currently invisible) while preserving the v8.0.0 "truth = Prometheus + ES only" constraint (resolving the expected set from Postgres would violate it).
**Requirements**: FW-01, FW-02, FW-03, FW-04, ANL-01, ANL-02, ANL-03, ANL-04, ANL-05, SMP-01 (locked via 76-SPEC.md)
**Depends on:** Phase 75

**Constraints (locked in discussion 2026-07-16):**

- **NO raw payload logging from the framework.** `SampleProcessor` is safe only *because* its values are synthetic (D-03, "no real/sensitive payload is logged"). A framework logging every input/output blob for every processor is a production data-protection defect. Blob hash or length is acceptable; the blob is not.
- **The value chain cannot go generic** — only the concrete processor knows what its numbers mean. Stripping the sample's `received/produced` log would regress TEST-10 back to a false FAIL.
- **Fan-out shape must survive the re-keying:** `Step_G` logs TWICE per correlationId (per-arrival fan-in, collapsed by DISTINCT) and `Step_A` is the shared Mode-2 entry carrying an EMPTY `executionId` (deliberately excluded from `HopLabels` today).
- **Touches production source** (`BaseProcessor.Core`) — this is a phase, not a quick task. Framework per-hop logging is justified on its own operability merits, not merely to serve the analyzer.

**⚠ SourceHash reseed is MANDATORY before the live sweep (operator-flagged 2026-07-16):** `SourceHash.targets:82` folds `@(ImplFiles)` = `$(MSBuildThisFileDirectory)**\*.cs` (**BaseProcessor.Core's OWN sources**) **+** `$(MSBuildProjectDirectory)\**\*.cs` — so editing `ProcessorPipeline.cs` alone **changes `Processor.Sample`'s embedded SourceHash**. The rebuilt image then registers under a NEW processor id with no `processors` DB row, and `phase-65-reset.ps1` aborts at STEP 2 heal-wait (exit 20). Required order after compiling, BEFORE `phase-68-sweep.ps1`:

1. Clean-rebuild **BOTH** configs (`-c Debug` alone leaves a stale Release dll with the OLD embedded hash → seeder binds the wrong processor row → `POST /start` **422** even when heal-wait passes): `rm -rf src/Processor.Sample/obj src/Processor.Sample/bin/Release && dotnet build SK_P.sln -c Release --no-incremental`.
2. FK-safe graph DELETE of the 6 workflow tables (the idempotent `FanOutSeeder` GET-matches by name and would NOT rebind the stale workflow otherwise).
3. Seed: `BaseApi.Tests.exe --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*"` — `SeedProcessorAsync(hostHash)` creates the processor row for the new hash.
4. Verify liveness keys (`skp:proc:*:*`, ~10s) → `docker compose restart orchestrator` → `POST /api/v1/orchestration/start` must return **204**. The 204 IS the proof of SourceHash currency (container hash == seeder host-build hash); a 422 means the hash still diverged.
5. Only then run `scripts/phase-68-sweep.ps1`.

**Plans:** 5 plans

Plans:

- [x] 76-01-PLAN.md — Processor per-hop framework log (FW-01/03/04) + OutputTail resolved-outcome (D-18) [wave 1] ✅ 2026-07-16 (7ad4086, 08c968f, 49caf17, b93d5e1) — OutputTail.RunAsync → (bool proceed, StepOutcome resolved); ProcessorPipeline.LogHopExecuted guarded placeholder-only Information record at the 5 emit branches (Failed/Cancelled/Completed, D-18 resolved outcome, D-09 explicit empty-ExecutionId entry marker), zero on clean-absent/reinject/Processing; FW-03 grep-clean + sentinel-through-blob; FW-04 ThrowingLogger outcome-parity + explicit logs OTLP ExportProcessorType.Batch; PerHop 11/11 + NoPayload 2/2 + NonBlocking 2/2 + OutputTail 6/6 GREEN, 0-warning Debug+Release
- [ ] 76-02-PLAN.md — Orchestrator fan-out + terminal-reached records (FW-02, D-11 Option C/D-12/D-13)
- [x] 76-03-PLAN.md — Analyzer structural re-key to stepId + ES expected-set + framework-redundancy reconciliation (ANL-01/02/03, SMP-01) [wave 1] ✅ 2026-07-16 (0e1e92b, 16fde90, a003609) — HopLabels deleted (grep-clean), completeness stepId-keyed on RunTrace.DistinctStepIds vs ES-derived FW-02 NextStepId expected set (TEST-08 closed), framework-redundancy reconciliation with NO seed oracle (ANL-03), SMP-01 value-chain degrade-to-N/A (valueOracleSupplied non-empty-seed-gated), D-09 entry-marker exclusion; EsIndexNames.StepIdFieldPath/NextStepIdFieldPath, RunTrace.FromStepIds, 3-query ES reader; Analysis facts 37/37 GREEN (ValueChainFacts unchanged), 0-warning Debug+Release; live surfacing deferred-automated (Docker-less)
- [x] 76-04-PLAN.md — Three-class verdict + metric-gate inversion + exit-2 harness/sweep plumbing (ANL-04/05) [wave 2] ✅ 2026-07-16 (d5e74bb, fe9b70c, 55eb5ba) — Verdict.Inconclusive third class (JsonStringEnumConverter); verdict gate splits on EVIDENCE SUFFICIENCY first (traceDark startedRuns==0 && conservationOk → Inconclusive regardless of metricGateOk, the TEST-01 cold-ES / collector-blind shape), genuine orch_consumed!=proc_sent gap stays FAIL (T-76-12), D-01 non-zero TelemetryGap stays PASS; scripts/lib/exit-code-resolution.ps1 dot-sourceable Resolve-AnalyzerExitCode (Inconclusive→2/Fail→1/Pass→0, unknown→1 fail-closed T-76-11) + Resolve-SweepClass (2→INCONCLUSIVE, distinct no-auto-retry message, sweep-fatal); harness STEP H resolves the class from the JSON artifact + overrides the mirrored exit, sweep roll-up consumes the shared lib; Verdict 6/6 + MetricGate 10/10 + Analysis 44/44 GREEN, hermetic exit-code acceptance green, both scripts parse, 0-warning Debug+Release; live exit-2 surfacing deferred-automated (Docker-less)
- [ ] 76-05-PLAN.md — Live gate: mandatory SourceHash reseed → 204 → 7-scenario sweep, classify every non-PASS (autonomous:false)

### Phase 77: Consistent framework logging model — scope-carried execution ids, messageId on send/consume, keeper symmetric, drop concrete-processor logs

**Goal:** One uniform framework logging model — Tier-1 execution ids arrive from the ambient consume scope (attributes.*), Tier-2 {MessageId} rides send/consume records, Tier-3 domain extras ({Outcome}/{NextStepId}/{ReinjectOutcome}) stay in the string; no id is ever restated in a string the scope already carries, the keeper opens the scope symmetrically, and concrete-processor logs are dropped so the verdict never depends on them.
**Requirements**: LOG-01 (strip Tier-1 placeholders), LOG-02 (keeper opens the execution scope), LOG-03 (MessageId on send/consume incl. outbound-id capture), LOG-04 (remove concrete-processor logs), LOG-05 (operator freedom preserved), LOG-06 (keep outcome-shaped records)
**Depends on:** Phase 76
**Plans:** 6/6 plans complete

Plans:

- [x] 77-01-PLAN.md — Strip Tier-1 from the processor per-hop record (ProcessorPipeline.LogHopExecuted) [wave 1] ✅ 2026-07-16 (a7e9131, 83d061f) — LOG-01, LOG-06; template now `hop executed {MessageId} {Outcome}`, five Tier-1 ids via ambient scope only; PerHop facts 10/10 green, Debug+Release 0-warning
- [x] 77-02-PLAN.md — Capture+log the outbound MessageId on the processor result send (OutputTail, D3) [wave 1] ✅ 2026-07-16 (e4ba610, 18ca4b7) — LOG-03; SendResult mints/stamps/returns the outbound envelope MessageId, RunAsync logs FW-04-guarded `result sent {MessageId} {Outcome}` (no payload); optional ILogger ctor param keeps 8 test sites compiling; ResultSendLogFacts 1/1 + OutputTail 6/6 + PerHop 11/11 green, Debug+Release 0-warning
- [x] 77-03-PLAN.md — Strip Tier-1 from the orchestrator execution records + fan-out outbound MessageId capture [wave 1] ✅ 2026-07-16 (b2c397f, 27f70d8, 47f8829) — LOG-01, LOG-03, LOG-06; terminal-reached now the bare marker, four trip-end lines drop {WorkflowId}/{StepId} (keep outcome/NextStepId), fan-out mints/stamps/logs `fan-out {MessageId} {NextStepId}` (reverses D-11 Option C); OrchestratorPrePipeline facts 21/21 green, Debug+Release 0-warning
- [x] 77-04-PLAN.md — Delete concrete-processor author logs (SampleProcessor, D4) + preserve operator freedom (D5) [wave 1] ✅ 2026-07-16 (aece668, fcd3bc5) — LOG-04, LOG-05; SampleProcessor emits ZERO author logs and takes no ILogger (both Mode-2 seed line + Mode-1 value line gone), behaviour re-proven via send.SentData/dr.Data; D5 guard OperatorFreedom_ExecutionScopeFilter_Registered_Unconditionally added to ConsoleExecutionScopeFilterTests (bus-wide filter unchanged/unconditional); SampleProcessorFacts 4/4 + ConsoleExecutionScope 5/5 + FanIn 5/5 green, Debug+Release 0-warning
- [x] 77-05-PLAN.md — Scope foundation: ExecutionLogScope loose-id overload + IKeeperRecoverable : ICorrelated [wave 1] ✅ 2026-07-16 (624504b, 4c538f2, 25b384e) — LOG-02; ExecutionLogScope.BuildState loose-id positional overload added with the IExecutionCorrelated form delegating to it (single skip-rule impl, D-07), IKeeperRecoverable : ICorrelated (CorrelationId re-declared new) so keeper records are recognized by the bus-wide correlation filter while the 4-tuple partition shape stays byte-preserved; ExecutionLogScopeKeeper 11/11 + ConsoleExecutionScope 5/5 + RecoveryPartition 3/3 green, Debug+Release 0-warning
- [x] 77-06-PLAN.md — Keeper reinject consumers open the execution scope + strip Tier-1 (D2, depends on 05) [wave 2] ✅ 2026-07-16 (b407f89, 9f471b1, 52ebb64) — LOG-01, LOG-02, LOG-06; both ReinjectConsumer + OrchestratorReinjectConsumer wrap HandleAsync in logger.BeginScope(ExecutionLogScope.BuildState(5 ids)), sent/drop templates stripped to `REINJECT {sent|drop} {MessageId} {ReinjectOutcome}`, orchestrator gains a NEW symmetric sent record on the confirmed-send path; no payload (FW-03); ScopeCapturingLogger + 5-id scope fact + orchestrator sent fact added; hermetic *Reinject* 18/18 green, Debug+Release 0-warning

### Phase 78: Rework the resilience sweep to verify purely from the new framework ES logs — adapt the analyzer to the consistent-logging model (entry-marker = absent ExecutionId not Guid.Empty; discriminate keeper reinject records via attributes.ReinjectOutcome; drop the concrete value oracle), keep Prometheus as the secondary collector-blind axis, then re-run the reseed + 7-scenario live gate and confirm the verdict reconstructs from framework logs alone

**Goal:** The resilience verdict reconstructs purely from Phase-77 framework ES logs — the entry marker self-excludes by absent `attributes.ExecutionId`, keeper reinject records are query-excluded from structural completeness via `must_not exists attributes.ReinjectOutcome`, and the concrete value oracle is fully deleted (fixture + engine + `PassFailEngineValueChainFacts.cs`) — leaving structural stepId completeness + ANL-03 framework-redundancy + the Prometheus metric gate as the sole axes; the hermetic analyzer facts are green (zero new failures), and all 7 live scenarios reproduce their committed-HEAD baseline verdict.
**Requirements**: D-01, D-02, D-03, D-04 (tracked via 78-CONTEXT.md — no REQUIREMENTS.md for this milestone)
**Depends on:** Phase 77
**Plans:** 6/6 plans complete

Plans:

- [x] 78-01-PLAN.md — D-01 delete entry-marker detector + D-02 keeper `must_not` on the structural query [wave 1] — COMPLETE (`1daa8f6`/`02cd16d`; 41/41 analyzer facts green; 0-warning Debug+Release; entry-marker predicate gone, structural query forbids attributes.ReinjectOutcome)
- [x] 78-02-PLAN.md — D-03 delete the value oracle (fixture query/parse + engine value-chain machinery + `PassFailEngineValueChainFacts.cs`), keep the ExpectedHopOffset scaffold [wave 2] — COMPLETE (`4dfefdb`/`9a2b696`; value axis dark from fixture + engine, ValueChainOk/ValueChainDetail removed, ANL-03 sole non-binding path, ExpectedHopOffset retained; PassFailEngineFacts 29/29 green; 0-warning Debug+Release)
- [x] 78-03-PLAN.md — D-03 Hazard C atomic: delete ExpectedHopOffset residue + migrate the ~6 label-fallback facts to the stepId path, then the hermetic green gate [wave 3] — COMPLETE (`5cca3a5`; ExpectedHopOffset/valueOracleHopSet/DistinctLabels label-fallback + RunTrace.DistinctLabels deleted, 6 facts migrated to explicit expectedStepIdsByExecution with unchanged assertions; analyzer facts green — Analysis 29/29, BuildKeeperOutcomeMap 3/3, FanInHermetic 5/5; 0 new failures vs Docker-less baseline; 0-warning Debug+Release)
- [x] 78-04-PLAN.md — D-04 live gate: SourceHash reseed + 7-scenario sweep + HEAD-baseline reproduction (operator-gated) [wave 4] — EXECUTED 2026-07-17: **BLOCKING FINDING, D-04 NOT reproduced.** Reseed ran (heal-wait trap hit + resolved via graph-DELETE→seed→204). Fresh live sweep flips ALL scenarios PASS→FAIL incl. the no-fault baseline TEST-01 (`Duplicates==StartedRuns`, 100% effect-once violations, `Missing=0`, value axis dark). Root cause: Phase-77 uniform execution-scope logging emits multiple StepId-scoped framework records per hop; the reworked structural step query counts each as a distinct step execution. NOT re-baselined. Needs a follow-up fix plan (collapse multi-record hops per `{executionId,stepId}`). See `78-04-SUMMARY.md`. (TEST-07 indeterminate — analyze killed; on-disk copy stale.) **→ RESOLVED by 78-05 (fix) + 78-06 (clean 7/7 re-gate); D-04 CLOSED.**
- [x] 78-05-PLAN.md — GAP (D-04 fix): canonical-record fix — select ONLY the `"hop executed"` processor consume record as the observed did-run hop (via body.text discrimination in a testable StructuralCohort classifier), route `"terminal reached"` → proven, ignore `"result sent"`/`"fan-out"`; + hermetic StructuralCohortFacts reproducing multi-record-per-hop (collapse→Pass; genuine redelivery→Fail); hermetic gate green, 0-warning Debug+Release [wave 5] (gap_closure) — COMPLETE (`a1068ee`/`e470428`; StructuralCohort classifier + 3 regression facts; Analysis 32/32 green; 0-warning Debug+Release)
- [x] 78-06-PLAN.md — GAP (D-04 re-gate): re-run the reseed-first SourceHash + 7-scenario live sweep after the 78-05 fix [wave 6] (gap_closure) — COMPLETE 2026-07-17: **CLEAN 7/7 PASS — D-04 CLOSED.** Reseed-first live sweep reproduced the genuine da91d32 all-PASS baseline on real Phase-77 data (TEST-01 19/19, 02 17/17, 03 18/18, 04 17/17, 05 23/23, 06 14/14, 07 25/25 — all Missing==0, all Duplicates==0), value axis dark. **The 78-04 over-count (`Duplicates==StartedRuns`) is GONE — no-fault TEST-01 Duplicates==0 (was 19/19).** BASELINE-ORACLE CORRECTION (T-78-13): gated against `git show da91d32:...` (7/7 PASS), NOT the clobbered HEAD (commit `5096dcd` → 0 Pass). TEST-07 analyze re-invoked against its completed ES window (killed mid-STEP-H, same 78-04 trap; no stale copy read). Roll-up regenerated to 7/7 PASS, restoring the tracked baseline. See `78-06-SUMMARY.md`.

### Phase 79: Falsification harness for the resilience sweep — negative-control proving the pass/fail gate has teeth: a toggleable env-gated lossy injection seam (family of SCENARIO_ID/K_EXECUTIONS) that defeats recovery for exactly one {correlationId,executionId} so it registers as recoverable-but-lost (not a clean keeper-drop, not tolerated in-flight-at-wipe), with the ES telemetry shape byte-identical to a real loss; a one-scenario harness (mirroring phase-67-harness.ps1) runs it and asserts phase-68-sweep.ps1 flips that scenario to VERDICT_FAIL, confirming a True Positive fires and ruling out the False Negative (silent-green) risk; start with the recoverable-but-lost binding axis (missing/duplicate axes are follow-on); validates the observer/gate, not the system recovery

**Goal:** Prove the resilience-sweep pass/fail gate has TEETH — a default-off, env-gated keeper suppress-send seam manufactures exactly one byte-identical recoverable-but-lost strand (keeper logs "reinject" but suppresses the redispatch), and a standalone driver asserts the sweep flips that scenario to VERDICT_FAIL for the recoverable-but-lost binding-miss reason (True Positive fires, no silent-green False Negative), while the seam stays provably inert (7/7 capstone unaffected).
**Requirements**: n/a (no REQUIREMENTS.md for this milestone; tracked via ROADMAP + 79-CONTEXT.md D-01..D-06)
**Depends on:** Phase 78
**Plans:** 5/5 plans complete

Plans:

- [x] 79-01-PLAN.md — Keeper env-gated one-shot suppress-send seam (ReinjectConsumer) + compose plumbing (default-off) (completed 2026-07-17 — commits `7ec9169`/`4a9f692`; static `_defeatedOnce` Interlocked latch gates `ep.Send`, CountSent + "reinject" log unchanged after the gate for byte-identical telemetry (D-01); `${KEEPER_DEFEAT_REINJECT:-0}` compose plumbing; Keeper+tests 0-warning; `*ReinjectConsumerFacts` 8/8 + `*PassFailEngineFacts` 29/29 GREEN; seam provably inert when unset (D-06))
- [x] 79-02-PLAN.md — Hermetic pin of the MissingDetail binding-miss string contract the falsify driver depends on
- [x] 79-03-PLAN.md — FALSIFY-01 scenario row + inject-recovery-loss harness branch (keeper scaled to 1 replica + K_EXECUTIONS=1)
- [x] 79-04-PLAN.md — Standalone phase-79-falsify.ps1 driver with the inverted, fail-closed assertion (D-03 + D-04)
- [x] 79-05-PLAN.md — Live gate-teeth run (exit 0) + inertness regression gate (7/7 capstone with the seam unset)

### Phase 80: Deploy full system to local Kubernetes (Docker Desktop) — port compose topology to k8s manifests and prove end-to-end

**Goal:** Port the existing docker-compose topology to Kubernetes manifests and run the full stack healthy on a local Docker Desktop cluster, then prove a workflow executes end-to-end — a k8s deployment-target proof paralleling the compose stack, with NO change to the two-consumer recovery architecture or app source. Replica topology: **orchestrator ×1** (single-owner by design — in-memory L1 pause/resume state + Quartz scheduling is a documented SPOF; keep at 1), **keeper ×2**, **processor-sample ×2**, **baseapi-service (webapi) ×1**, **otel-collector ×1**, **prometheus ×1**, **elasticsearch ×1**, **redis ×1**, **postgres ×1**, **rabbitmq ×1**. App tiers as Deployments; stateful backing services (postgres / redis / elasticsearch / rabbitmq) as StatefulSets with persistence. Preserve: OTLP→collector wiring; Redis/RabbitMQ/Postgres connection strings via k8s Service DNS; compose healthchecks → readiness/liveness probes; the processor SourceHash reseed step (graph-delete → seed → 204); and ALL test-only env seams shipped DISABLED / default-off (`KEEPER_DEFEAT_REINJECT`, `PROCESSOR_DEFEAT_READ`, `KEEPER_REINJECT_DELAY_MS`, `ORCH_OUTPUT_TTL`, `KEEPER_RECOVERY_TTL`, `PROCESSOR_STEP_DELAY_MS`, `K_EXECUTIONS`).
**Requirements**: n/a (no REQUIREMENTS.md for this milestone). Success = full stack reports Healthy on Docker Desktop k8s AND one workflow round-trips end-to-end, verifiable from the same Prometheus + ES signals as the compose sweep.
**Depends on:** Phase 79
**Plans:** 10/10 plans complete

Plans:

- [x] 80-01-PLAN.md — Wave 1: namespace + dev-only Secret + otel/prometheus ConfigMaps (D-02/06/07)
- [x] 80-02-PLAN.md — Wave 1: postgres (SS+1Gi PVC) + redis (SS no-PVC, persistence-off) StatefulSets (D-09/12)
- [x] 80-03-PLAN.md — Wave 1: rabbitmq (SS+1Gi PVC) + elasticsearch (SS+2Gi PVC) StatefulSets + startupProbes (D-10/11)
- [x] 80-04-PLAN.md — Wave 1: otel-collector + prometheus Deployments + ClusterIP Services + ConfigMap subPath mounts (D-06/13)
- [x] 80-05-PLAN.md — Wave 1: baseapi-service (Deploy+Svc 8080, $(VAR) Postgres) + orchestrator (Deploy x1, no Svc, literal 900) (D-04/08)
- [x] 80-06-PLAN.md — Wave 1: keeper (Deploy x2) + processor-sample (Deploy x2) — literal 900 TTL, all fault seams omitted, badconfig excluded (D-04/08)
- [x] 80-07-PLAN.md — Wave 2: phase-80-build.ps1 (5 :local images) + kustomization.yaml aggregator + whole-stack dry-run gate (D-03/04/05)
- [x] 80-08-PLAN.md — Wave 2: phase-80-reset.ps1 — kubectl-exec re-target of the compose reset (Pitfall 1, first half)
- [x] 80-09-PLAN.md — Wave 3: phase-80-up.ps1 — apply + rollout status + rollout restart + 8 loopback port-forwards (D-14/15, Pitfalls 2/5)
- [x] 80-10-PLAN.md — Wave 4: phase-80-harness.ps1 (STEP B1/D re-target) + live happy-path proof checkpoint (D-16)

### Phase 81: Re-run the 7-scenario fault-recovery sweep (TEST-01..TEST-07) on the k8s Docker Desktop target — crash injection via kubectl scale --replicas=0 then scale-back as the analog of the compose docker stop/start. Generalize the Phase-80 k8s harness (phase-80-harness.ps1, currently TEST-01-only) to accept a scenario id/list and re-target the crash sequencer from docker-compose CLI to kubectl scale (third Pitfall-1 CLI re-target), preserving each tier's replica count on restore (orchestrator 1, keeper 2, processor-sample 2, redis 1, rabbitmq 1) and waiting for actual terminate on scale-0 and Ready on restore before pinning RECOVERY_UTC. Reuse the phase-68 sweep analyzer + conservation/recovery metric gate verbatim. Scope = the 7 recovery-capstone scenarios only (processor/orchestrator/keeper/redis/rabbitmq/redis+rabbitmq whole-tier crash + no-fault baseline); the seam-dependent negative controls (TEST-08/09/10, FALSIFY-01/02) stay out of scope because the Phase-80 manifests omit the test-only env seams they require. Proves the two-consumer recovery architecture recovers on k8s the same as on compose.

**Goal:** The k8s harness runs any of TEST-01..07 by id and reaches 7/7 VERDICT_PASS on the Docker Desktop target — crash injection re-targeted from `docker compose stop/start` to `kubectl -n skp scale --replicas=0`/restore for all 5 crashable tiers (replica counts preserved on restore; RECOVERY_UTC pinned only after terminate + Ready gates), driven by a k8s sweep that reuses the phase-68 analyzer + conservation/recovery metric gate byte-for-byte.
**Requirements**: SPEC-1, SPEC-2, SPEC-3, SPEC-4, SPEC-5, SPEC-6 (81-SPEC.md — 6 locked requirements + 8 acceptance criteria)
**Depends on:** Phase 80
**Plans:** 4/4 plans complete

Plans:

- [x] 81-01-PLAN.md — Wave 1: generalize phase-80-harness.ps1 — -ScenarioId + 7-row scenario table + bad-id guard (exit 64) + tier maps + kubectl-scale crash sequencer (terminate/Ready gates) + -SkipBringUp (SPEC-1..4) ✅ 2026-07-18 (9f581e6, 7592750) — -ScenarioId/-SkipBringUp params + 7-row [ordered] capstone table (TEST-01..07 only, bad-id exits 64) + static $TierKind/$TierReplicas maps (keeper=2, processor-sample=2 never a blanket 1) + kubectl -n skp scale --replicas=0/restore crash sequencer with 0-running-pods terminate-wait + rollout status Ready gate before pinning RECOVERY_UTC; zero docker compose calls; PARSE_OK + all acceptance greps green
- [x] 81-02-PLAN.md — Wave 1: extend phase-80-reset.ps1 with a bounded idempotent rabbitmq queue drain (D-04, cross-scenario isolation) (SPEC-5) ✅ 2026-07-18 (cc6f4ef) — STEP 3b dynamic list_queues + per-queue purge (fail-soft)
- [x] 81-03-PLAN.md — Wave 2: create phase-81-sweep.ps1 — mirror phase-68-sweep + D-03 one-time bring-up, child -SkipBringUp harness loop, verbatim verdict roll-up (SPEC-5, SPEC-6) ✅ 2026-07-18 (ef80670, 25e3fa9) — sweep driver + one-time bring-up/teardown, exit 0 IFF 7/7
- [x] 81-04-PLAN.md — Wave 3: capstone run — reused-verbatim guard + drive the sweep to 7/7 VERDICT_PASS (INCONCLUSIVE re-runnable; VERDICT_FAIL fails) + operator confirm (SPEC-5, SPEC-6) ✅ 2026-07-18 — **7/7 VERDICT_PASS** (all Missing=0, effect-once); two gap-closure fixes: STEP B0 processor-restart counter-baseline (425d30c) + SPEC-5 exception marking TEST-06 rabbitmq MG-1 non-binding (a57f80b, user-authorized — broker-PVC redelivery counter artifact, not loss). Human-verify waived by user; self-verified.

## ✅ v10.0.0 Orchestrator High Availability — SHIPPED 2026-07-19 (Phases 82-83) — [archive](milestones/v10.0.0-ROADMAP.md)

**Milestone Goal:** Run the orchestrator as N≥2 replicas with single-leader mutual exclusion so horizontal scaling never produces duplicate workflow triggers, with failover in seconds and role-tagged observability. Only the elected **leader** performs scheduler-triggered entry-step sends — the gate is a single in-memory role flag read by `WorkflowFireJob`'s entry-step send loop; followers run all other logic (mint correlationId, refresh L1 liveness, reschedule, and the load-balanced `RelocateTail` step-advancement) **unchanged**. This is the ONLY place duplication can arise: every replica's in-process Quartz scheduler fires the same cron independently, and each fire mints a fresh random correlationId, so two un-gated fires create two genuinely-distinct runs that downstream idempotency will NOT collapse. Leader election uses a `coordination.k8s.io/v1` **Lease** via the `KubernetesClient` `LeaderElector` (`RunAndTryToHoldLeadershipForeverAsync`, `RenewDeadline` < `LeaseDuration` self-demotion fence) hosted in a processor-style `BackgroundService`; its `OnStartedLeading`/`OnStoppedLeading` callbacks are the **single writer** of a volatile `LeaderState` snapshot. Every orchestrator log gains `attributes.role = leader|follower` via an OTel `LogRecord` enricher mirroring `ProcessorIdLogEnricher`. Failover **skips** any cron tick in the election gap (no backfill) and tolerates a rare, bounded paused-leader duplicate. The fanout (per-replica `InstanceId`/`Temporary` lifecycle + pause/resume) vs load-balance (shared `orchestrator-result` step-results) messaging topology is already correct and **unchanged**. Phases continue at **82**; builds on the v9.0.0 k8s deployment (phases 80-81).

**Source of truth:** This milestone's design conversation (2026-07-18), confirmed point-by-point — leader-gated scheduler-fire ONLY (not the shared `StepDispatcher`, so in-flight step-advancement never stalls during failover); mechanism resolved to the `KubernetesClient` `LeaderElector` wrapped in a `BackgroundService` (verified API: `LeaseLock(IKubernetes, ns, name, identity)` on `V1Lease`; `LeaderElectionConfig` LeaseDuration 15s/RenewDeadline 10s/RetryPeriod 2s; events `OnStartedLeading`/`OnStoppedLeading`/`OnNewLeader`); single-writer volatile `LeaderState`; role log enricher; skip-on-gap failover. New dependency: `KubernetesClient` NuGet. Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (7 reqs, HA-01..HA-07).

**Build order (locked):** 82 (leader election service + `LeaderState` + `WorkflowFireJob` gate + role log enricher + RBAC + replica bump) → 83 (failover proof).

### Phase 82: Orchestrator HA — Kubernetes-Lease leader election with a leader-gated scheduler fire and role-tagged logging

**Goal:** The orchestrator runs as N≥2 replicas on k8s with exactly one leader at any instant performing scheduler-triggered entry-step sends. A `BackgroundService` runs `KubernetesClient` `LeaderElector.RunAndTryToHoldLeadershipForeverAsync` over a `coordination.k8s.io/v1` Lease (`skp/orchestrator-leader`); its callbacks are the single writer of a volatile `LeaderState` snapshot (`IsLeader`/`Role`/`CurrentLeaderId`, defaulting to follower). The gate wraps `WorkflowFireJob`'s entry-step `foreach … DispatchAsync` loop ONLY (followers skip the send but still refresh L1 + reschedule); `RelocateTail` step-advancement stays ungated. An OTel `LogRecord` enricher stamps `attributes.role` on every orchestrator log. RBAC Role/RoleBinding grants the orchestrator ServiceAccount `get/create/update` on `leases` in `skp`; the orchestrator Deployment scales to N≥2. All hermetic + build gates green; `KubernetesClient` added.
**Requirements**: HA-01, HA-02, HA-03, HA-04, HA-05, HA-06
**Depends on:** Phase 81 (k8s deployment + resilience baseline)
**Plans:** 4/4 plans complete

- [x] 82-01-PLAN.md — KubernetesClient dependency + single-writer volatile LeaderState + LeaderElectionService (election core, build-only) ✅ 2026-07-18 (d6fd3e2, c2d06a9, 29cff1c) — HA-02/HA-03/HA-04/HA-06; KubernetesClient pinned 18.0.13 (fix-forward off 15.0.1 advisory GHSA-w7r3-mgwf-4mqq), LeaderState volatile single-writer holder (follower default), LeaderElectionService build-only BackgroundService over skp/orchestrator-leader Lease; Debug+Release 0-warning, single-writer grep-clean
- [x] 82-02-PLAN.md — WorkflowFireJob leader gate (IsLeader && hydrated) + OrchestratorRoleLogEnricher + Program.cs wiring (POD_NAME, in-cluster-gated election) ✅ 2026-07-18 (65e086c, e4c6bea, 05d8f68) — HA-01/HA-04/HA-05/HA-06; fire gate snapshots IsLeader && IsReady ONCE (follower skips only sends, still refreshes L1 + reschedules), role enricher stamps attributes.role on every log, POD_NAME→MachineName identity feeds bus InstanceId + LeaseLock, election hosted service in-cluster only (KUBERNETES_SERVICE_HOST); Debug+Release 0-warning, RelocateTail ungated (HA-03), 4/4 affected fire tests green
- [x] 82-03-PLAN.md — least-privilege RBAC (SA/Role/RoleBinding for leases) + Deployment replicas:3/RollingUpdate/POD_NAME
- [x] 82-04-PLAN.md — hermetic tests: leader-only fire gate, LeaderState single-writer transitions + timing fence, role enricher ✅ 2026-07-18 (620168c, cd4fe68, 36a10bb) — HA-01/HA-02/HA-03/HA-04/HA-05; 3 test classes / 10 tests all green (D-06: drive LeaderState/enricher/WorkflowFireJob directly, no IKubernetes stub, no started election service): follower/cold-leader send ZERO yet still refresh L1 + reschedule while hydrated leader sends (HA-01), default-follower + BecomeLeader↔BecomeFollower flip + RenewDeadline<LeaseDuration fence (HA-02/03/04), attributes.role stamped follower/leader never-empty with live post-flip read (HA-05); standby Quartz scheduler for deterministic reschedule inspection; Debug+Release 0-warning

### Phase 83: Orchestrator HA failover proof — scale to N replicas, kill the leader mid-run, prove zero duplicate workflow triggers + bounded single-leader recovery

**Goal:** Under the orchestrator scaled to N≥2 replicas on k8s, with the current leader killed (`kubectl scale`/delete) mid-run, prove: exactly one leader holds the Lease at any instant; ZERO duplicate workflow triggers across the failover (verified via distinct per-fire correlationIds through the existing phase-68/81 analyzer — a duplicate trigger would surface as an extra correlationId for the same scheduled tick); cron ticks landing in the election gap are skipped (not duplicated, not backfilled); a single leader is re-established within a bounded time (~`LeaseDuration`); and `attributes.role` in ES logs shows the failover transition. Likely an orchestrator-leader-kill scenario added to the existing sweep harness.
**Requirements**: HA-07
**Depends on:** Phase 82
**Plans:** 5/5 plans complete

- [x] 83-01-PLAN.md — HaFireBucketScorer pure 30s-bucket + gap + recovery scorer (fail-closed fold) + hermetic facts + EsIndexNames.RoleFieldPath const [Wave 1]
- [x] 83-02-PLAN.md — HaFailoverAnalyzerE2ETests RealStack verdict fact (two ES streams -> scorer -> write-then-assert report, exit 0/1/2) [Wave 2]
- [x] 83-03-PLAN.md — scripts/phase-83-ha-failover.ps1 (reuse phase-80 bring-up + phase-aligned leader-kill sequencer + verdict driver) [Wave 3]
- [x] 83-04-PLAN.md — live end-to-end HA-07 failover proof (checkpoint: harness exit 0 / Verdict=Pass) [Wave 4]

*v3.2.0 shipped 2026-05-28 (11 phases). v3.3.0 shipped 2026-05-29 (5 phases, Orchestration L3→L1→L2 build pipeline). v3.4.0 shipped 2026-06-01 (9 phases 17-24+24.1, BaseConsole + Orchestrator Messaging). v3.5.0 shipped 2026-06-02 (6 phases 25-30, Processor Console — `BaseProcessor.Core` + `Processor.Sample`, assembly-embedded SourceHash, WebApi bus responders, L2 liveness self-registration, live execution round-trip + runtime/business metrics) — note: formal archival (ROADMAP/MILESTONES/tag) deferred. v3.6.0 shipped 2026-06-05 (4 phases 31-32.1, Idempotent Execution — exactly-once-effect round-trip via deterministic `H` + effect-first `flag[H]` dedup at both hops; cancelled circuit-breaker built then reverted to plain dead-lettering). Next milestone planning begins with `/gsd-new-milestone`.*

## ✅ Milestone v4.0.0 — Processor Pre/In/Post-Process + Keeper Recovery Redesign (Phases 43-49) — SHIPPED 2026-06-11

**Status:** 🚧 Planning (started 2026-06-08). **Breaking** successor to the v3.x execution model. Phases continue at **43**.
**Source of truth:** `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (LOCKED 2026-06-08).
**Posture:** at-least-once; **no dedup / idempotency key**; duplicate effects tolerated. Recovery assumes a **transient** L2 outage (the recovery backup lives in L2 itself).

**Milestone Goal:** Replace v3.x's effect-first exactly-once model with (a) an explicit three-stage **Pre / In / Post-Process** processor pipeline (author owns per-item outcomes + mints `executionId`), (b) a proactive Keeper **BIT health gate** driving **global pause-all / resume-all** plus a gate-open-only **5-state** recovery consumer (`UPDATE`/`REINJECT`/`INJECT`/`DELETE`/`CLEANUP`, **partitioned by exec** for per-key ordering), and (c) a single consolidated `_DLQ1` — while tearing down the `H`/`flag[H]` dedup + CAS, content-addressing + result manifest + N×M fan-out, and the reactive `Fault<T>` recovery path + `keeper-dlq`.

**Build order (locked, build-before-teardown so every intermediate stays buildable/testable):**
43 (message-contract + L2-key reshape) → 44 (processor Pre/In/Post pipeline + retry loops) → 45 (Keeper BIT gate + global pause-all/resume-all + orchestrator idempotent pause) → 46 (Keeper 5-state recovery consumer, per-key ordered + orchestrator per-item result consume) → 47 (`_DLQ1` consolidation + at-least-once semantics) → 48 (v3.x teardown) → 49 (live proof + close gate).

### Phase Table

| Phase | Name | Plans | Status | Completed |
| ----- | ---- | ----- | ------ | --------- |
| 43 | Message Contracts & L2 Key Reshape | 5/5 | Complete    | 2026-06-08 |
| 44 | Processor Pre/In/Post-Process Pipeline | 3/3 | Complete    | 2026-06-08 |
| 45 | Keeper BIT Health Gate + Global Pause/Resume | 3/3 | Complete    | 2026-06-08 |
| 46 | Keeper 5-State Recovery + Orchestrator Per-Item Consume | 4/4 | Complete    | 2026-06-08 |
| 47 | DLQ Consolidation + At-Least-Once Semantics | 3/3 | Complete    | 2026-06-09 |
| 48 | v3.x Teardown | 3/3 | Complete    | 2026-06-09 |
| 49 | Live Proof & Close Gate | 6/6 | In Progress (live gate operator-gated) | — |

### Phase Details

#### Phase 43: Message Contracts & L2 Key Reshape

**Goal**: The reshaped wire vocabulary exists — `EntryStepDispatch`/`ExecutionResult` carry the six ids and no longer carry `H`, `entryId` is a GUID with `Guid.Empty` as the explicit source-step sentinel, the five Keeper-state contracts exist, and the two L2 key schemes (GUID data key with no TTL + composite backup key `corr:wf:proc:exec` with a configurable 2-day TTL) are defined in one place. This phase is NOT "just vocabulary": removing `H` from the wire and retyping `entryId` to a GUID is compile-forced to drop the `H`/`flag[H]`/CAS dedup machinery (RETIRE-01) and the content-addressing + result manifest + N×M fan-out (RETIRE-02) — so RETIRE-01/02 are coupled INTO Phase 43 (D-01), landing alongside MSG-01/02/03 rather than in Phase 48.
**Depends on**: — (first phase of the milestone; builds on the existing `Messaging.Contracts` + `L2ProjectionKeys`)
**Requirements**: MSG-01, MSG-02, MSG-03, RETIRE-01, RETIRE-02 (the last two coupled per D-01/D-02)
**Success Criteria** (what must be TRUE):

  1. `EntryStepDispatch` and `ExecutionResult` carry exactly the six ids (`correlationId, workFlowId, stepId, ProcessorId, executionId, entryId`) and no longer carry `H` — a golden/contract test pins the shape and asserts `H` is absent.
  2. `entryId` is a `Guid`; `Guid.Empty` is recognized as the source-step sentinel by a shared helper (so consumers can branch "skip read / skip end-delete" off one predicate, not an ad-hoc check).
  3. Five Keeper message contracts exist — `UPDATE` (carrying validated data), `REINJECT`, `INJECT`, `DELETE`, `CLEANUP` — each carrying its specified id set, defined in `Messaging.Contracts`.
  4. `L2ProjectionKeys` exposes both schemes as single-source-of-truth builders: the per-item GUID **data key** (no TTL) and the composite **backup key** `correlationId:workFlowId:ProcessorId:executionId` (TTL = 2 days, configurable in days); golden tests pin both key strings.

**Plans**: 5 plans (4 waves)

- [x] 43-01-PLAN.md — Wave 0: contract/golden/predicate/options test stubs (RED) + delete RETIRE-01/02 machinery tests (MSG-01/02/03)
- [x] 43-02-PLAN.md — Wave 1: Messaging.Contracts core reshape — drop H, Guid entryId, four Step* records, SourceStep, five Keeper contracts + IKeeperRecoverable, L2 key builders, BackupOptions; delete ExecutionResult + MessageIdentity (MSG-01/02/03)
- [x] 43-03-PLAN.md — Wave 2: straight-through Orchestrator + BaseProcessor consumer adaptation (remove flag[H]/CAS/manifest; Guid entryId) (MSG-01/02)
- [x] 43-04-PLAN.md — Wave 2: D-14 dark reactive-path retarget (KeeperRecoveryHandler off inner.H, L2ProbeRecovery to Guid, neutralize Fault<ExecutionResult>, retain Fault<EntryStepDispatch>) (MSG-01/02)
- [x] 43-05-PLAN.md — Wave 3: reshape surviving tests + FULL-SUITE-GREEN phase gate + ROADMAP/REQUIREMENTS RETIRE-01/02 reconciliation (MSG-01/02/03)

#### Phase 44: Processor Pre/In/Post-Process Pipeline

**Goal**: `BaseProcessor` consumes a dispatch and runs an explicit Pre → In → Post pipeline — Pre reads + validates input (skipping on `Guid.Empty`), In is the author-overridden per-item transform that may throw a status-carrying exception, Post validates/writes/routes each item, and a `finally` end-delete reclaims `L2[entryId]` on every read-succeeded path — with a bounded retry loop wrapping every L2 op and every send.
**Depends on**: Phase 43 (the reshaped contracts, the `Guid.Empty` sentinel, the Keeper-state contracts, and both L2 key schemes must exist first)
**Requirements**: PIPE-01, PIPE-02, PIPE-03, PIPE-04, PIPE-05, PIPE-06, PIPE-07, PIPE-08, RESIL-01
**Success Criteria** (what must be TRUE):

  1. Pre-Process reads `L2[entryId]` through a bounded retry loop where a Redis exception **or** an absent/empty key counts as failure → after exhaustion `infra(READ)` sends Keeper `REINJECT` and ends the round trip (input left intact); `entryId == Guid.Empty` skips the read with empty validated data; a read-data input-schema validation failure is a business `Failed` (orchestrator result then end-delete), not infra.
  2. In-Process is an author-overridden abstract method `(validatedData, payload) → List<Item>` where each `Item = { result: completed|failed, data, executionId }` with an author-minted `executionId`; it is wrapped in try/catch so any thrown status (`processing`/`failed`/`cancelled`, an unexpected exception ⇒ `failed`) sends exactly one orchestrator result and aborts the batch (no Post-Process), then runs end-delete.
  3. Post-Process per `completed` item validates output against the output schema, sends Keeper `UPDATE` (validated data → composite backup), generates a GUID `entryId`, and writes `L2[entryId]` (no TTL) through a bounded retry loop whose exhaustion downgrades the item to `failed (infra)`; on a successful write it sends Keeper `CLEANUP` to delete the now-redundant composite backup.
  4. Post-Process routes each item: not-infra (`completed` ∪ business-`failed`) → one orchestrator result (a `completed` result carries `entryId` + `executionId`); infra → Keeper `INJECT`; N completed items produce N separate per-item orchestrator results (no manifest).
  5. End-delete runs in a `finally` over every read-succeeded path (happy, pre-process business-fail, In-Process exception), is skipped only on `infra(READ)`/`REINJECT` and `Guid.Empty` source steps, deletes `L2[entryId]` through a bounded retry loop, and on exhaustion sends Keeper `DELETE` without altering any result already sent; every L2 op and every send uses the shared `Retry:Limit` immediate-attempt loop.

**Plans**: 3 plans (3 waves — interface-first; clean break in the final wave)

- [x] 44-01-PLAN.md — Wave 0 foundation: ProcessOutcome/ProcessItem/ProcessStatusException + RetryLoop/KeyAbsentException + RetryLoopFacts (RESIL-01, PIPE-04/05 type-level)
- [x] 44-02-PLAN.md — ProcessorPipeline (Pre→In→Post→end-delete) + thin consumer + Retry reconcile + four pipeline Wave-0 fact files (PIPE-01..08, RESIL-01)
- [x] 44-03-PLAN.md — Clean break: migrate Processor.Sample to the new seam + delete ProcessResult.cs + Retry appsettings + full-suite green gate (PIPE-04, RESIL-01) ✓ Release 0/0, hermetic 488/0 RealStack-excluded

#### Phase 45: Keeper BIT Health Gate + Global Pause/Resume

**Goal**: The Keeper runs a suppressed background BIT loop that probes L2 (read + write-then-delete) on a configurable delay and broadcasts a global pause-all (unhealthy) / resume-all (healthy) decision to all orchestrators, and the orchestrator's pause-all/resume-all is idempotent per job via Quartz `TriggerState`.
**Depends on**: Phase 44 (the processor now emits the five Keeper-state messages; the gate is the precondition for the Phase-46 recovery consumer, so the gate + pause/resume land first)
**Requirements**: KEEP-01, KEEP-02, KEEP-03, ORCH-02
**Success Criteria** (what must be TRUE):

  1. A background `while` loop runs a BIT against L2 (read + write-then-delete probe) on a configurable `Probe:DelaySeconds` delay, and BIT exceptions are suppressed so a probe failure never crashes the loop (it simply reports unhealthy).
  2. Each BIT result fans out a **global** broadcast to all orchestrators — unhealthy → pause all jobs, healthy → resume all jobs.
  3. The recovery consumer's gate is wired: an L2 op is permitted only while the BIT gate is open, and a gate-closed consumer waits for the gate bounded under the broker consumer timeout (the wait mechanism exists and is honored, exercised by the Phase-46 ops).
  4. Orchestrator pause-all/resume-all is idempotent per job via Quartz `TriggerState` — pause only if Running, resume only if Paused — so a repeated broadcast is a no-op and no job is double-paused or spuriously resumed.

**Plans**: 3 plans (2 waves — Wave 0 contracts+test scaffold; Wave 1 Keeper + Orchestrator in parallel)

- [x] 45-00-PLAN.md — Wave 0: PauseAll/ResumeAll no-H contracts + 5 failing test stubs (KEEP-01/02/03, ORCH-02 scaffold) (completed 2026-06-08 — 2 contracts + 18 RED stubs; SK_P.sln 0/0 Release)
- [x] 45-01-PLAN.md — Keeper: IL2HealthGate (Toub gate) + BitHealthLoop edge-trigger BackgroundService + ProbeOnceAsync extraction (KEEP-01/02/03) (completed 2026-06-08 — 12/12 Keeper Health GREEN; SK_P.sln 0/0 Release)
- [x] 45-02-PLAN.md — Orchestrator: PauseAllConsumer/ResumeAllConsumer + defs on orchestrator-global-pauseresume + PauseAllAsync seam (ORCH-02) (completed 2026-06-08 — 6/6 Orchestrator consumer tests GREEN incl. the no-burst negative; full hermetic suite 506/506; SK_P.sln 0/0 Release)

#### Phase 46: Keeper 5-State Recovery + Orchestrator Per-Item Consume

**Goal**: The Keeper recovery consumer — **partitioned by `corr:wf:ProcessorId:executionId`** so each exec's messages process in order — applies the five states gate-open-only: `UPDATE` writes the composite backup (TTL = crash-backstop), `REINJECT` re-injects a reconstructed dispatch (or terminates at `_DLQ1` if the data is gone), `INJECT` reconstructs a `Completed` `ExecutionResult` to the orchestrator **then deletes the composite copy**, `DELETE` reclaims the data key, and `CLEANUP` deletes the redundant composite copy on the happy path — and the orchestrator advances workflow steps off per-item `ExecutionResult` messages with no manifest fan-out, a Keeper-`INJECT`'d completion being indistinguishable from a direct one.
**Depends on**: Phase 45 (the BIT gate must exist before the gate-open-only consumer can apply any op)
**Requirements**: KEEP-04, KEEP-05, KEEP-06, KEEP-07, KEEP-08, KEEP-09, ORCH-01
**Success Criteria** (what must be TRUE):

  1. The recovery consumer is **partitioned by `corr:wf:ProcessorId:executionId`** (per-key ordering): same-exec messages process in arrival order so `UPDATE` always precedes that exec's `CLEANUP`/`INJECT`, while different execs run in parallel.
  2. `UPDATE` writes `validatedData` to `L2[corr:wf:ProcessorId:executionId]` with the configurable TTL (default 2 days, a crash-backstop only), only while the gate is open.
  3. `REINJECT` reads `L2[entryId]`: if present (transient outage, data survived) it re-injects a reconstructed `EntryStepDispatch` to `queue:{ProcessorId}`; if absent/empty (data truly gone) the read fails → retry loop → `_DLQ1`.
  4. `INJECT` reads the composite copy, generates a new `entryId`, writes `L2[entryId]` (no TTL), injects a reconstructed `ExecutionResult(Completed, carrying entryId + executionId)` to the orchestrator result queue, and **deletes the composite copy**; `DELETE` deletes `L2[entryId]` (GC only); `CLEANUP` deletes the redundant composite copy on the happy path.
  5. After a happy-path or recovery completion the composite copy is gone (deleted by `CLEANUP`/`INJECT`, not left to its 2-day TTL) — a multi-item run leaves no composite keys behind.
  6. The orchestrator consumes per-item `ExecutionResult` messages (no manifest fan-out) and advances workflow steps accordingly; a Keeper-`INJECT`'d `Completed` result is processed identically to a direct processor completion (carries the same `entryId` + `executionId`).

**Plans**: 4 plans (3 waves — Wave 0 foundation: RetryLoop relocation + D-01 Payload ripple + RED test stubs; Wave 1 Keeper bodies + Orchestrator typed consumers in parallel; Wave 2 Keeper endpoint partitioner + registration)

- [x] 46-01-PLAN.md — Wave 0: RetryLoop→BaseConsole.Core (D-05) + KeeperReinject.Payload ripple (D-01) + 8 RED Phase=46 test stubs (KEEP-04..09, ORCH-01 scaffold) — completed 2026-06-08
- [x] 46-02-PLAN.md — Wave 1: Keeper 5-state recovery base + UPDATE/REINJECT/INJECT/DELETE/CLEANUP bodies + gate-wait/RetryLoop + RecoveryOptions + data-gone marker (KEEP-04/05/06/07/08) — completed 2026-06-08
- [x] 46-04-PLAN.md — Wave 1: Orchestrator TypedResultConsumer<T> base + 4 typed subclasses + defs + Program swap (ORCH-01) — completed 2026-06-08 (abstract Outcome knob replaces hardcoded StepOutcome.Completed; no status if/switch; single-owner UseMessageRetry on shared orchestrator-result endpoint; ResultConsumer(Definition) deleted; TypedResultConsumerFacts 7/7 incl. ORCH-01 indistinguishability; SK_P.sln 0/0; a9d387a, c5b56cb, 4425ef8)
- [x] 46-03-PLAN.md — Wave 2: 5 recovery ConsumerDefinitions (single-owner UsePartitioner on 4-tuple + retry) + Program/appsettings registration + partition/dead-letter facts (KEEP-09) — completed 2026-06-09 (UpdateConsumerDefinition owns UseMessageRetry + five shared-Partitioner UsePartitioner; four siblings no-op; 8.5.5 types in MassTransit.Middleware, Guid-keyed endpoint overload → PartitionGuid=SHA256(4-tuple); RecoveryOptions bound; five consumers registered additively; Phase=46 18/18; SK_P.sln 0/0; c4e429c, df65065, 114bbd8)

#### Phase 47: DLQ Consolidation + At-Least-Once Semantics

**Goal**: Every terminal give-up across the processor and the Keeper (a send exception with its retry loop exhausted; a Keeper L2 op exhausted) routes to a single consolidated `_DLQ1`, and the whole execution path is at-least-once with no dedup/idempotency key — duplicate effects are tolerated downstream by construction.
**Depends on**: Phase 46 (both the processor give-up paths and all five Keeper recovery ops must exist before their give-ups can be consolidated into one queue)
**Requirements**: RESIL-02, RESIL-03
**Success Criteria** (what must be TRUE):

  1. A single consolidated `_DLQ1` receives every terminal send/L2 give-up from the processor and the Keeper — there is no separate `keeper-dlq`, and the routing is wired once across the consoles (same pattern everywhere).
  2. The execution path carries no dedup/idempotency key (no `H`, no `flag[H]` gate); a redelivered or re-injected message reproduces its effect rather than being collapsed, and the system tolerates the duplicate (no lost branch, no crash).
  3. The `REINJECT`-data-gone case (redelivery after end-delete, or genuinely missing input) terminates deterministically at `_DLQ1` for operator triage rather than looping.

**Plans**: 3 plans (2 waves — Wave 1: two independent test plans in parallel [DLQ-consolidation structural guards; at-least-once duplicate-delivery + R2 re-tag]; Wave 2: audit doc + design-doc amendment, gated on Wave-1 facts being green)

- [x] 47-01-PLAN.md — Wave 1: processor send-exhaustion -> skp-dlq-1 fact + AtLeastOnceStructuralFacts (reflection no-dedup R4 + source-scan no-keeper-dlq R1) [RESIL-02, RESIL-03] ✓ 3/3 Phase=47 facts GREEN, 528/528 hermetic, SK_P.sln 0/0 Release; commits 43e4855, 4c771f3
- [x] 47-02-PLAN.md — Wave 1: duplicate-delivery no-collapse facts (StepCompleted + KeeperReinject, R3) + Phase-47 re-tag of the data-gone fact (R2 cited) [RESIL-02, RESIL-03] ✓ 6/6 Phase=47 facts GREEN, 530/530 hermetic, SK_P.sln 0/0; commits 29002c9, f6139d7
- [x] 47-03-PLAN.md — Wave 2: 47-DLQ-AUDIT.md traceability ledger (R5) + design-doc at-least-once amendment bundling the Phase-46 KeeperReinject.Payload note (D-02) [RESIL-02, RESIL-03] ✓ 8-row ledger (all rows green); design-doc A16 additive (1 insertion); Phase=47 6/6 GREEN, SK_P.sln 0/0; doc-only (git diff --stat = 2 doc files); commits 651d644, 61e9f3e

#### Phase 48: v3.x Teardown

**Goal**: The reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path + the `keeper-dlq` queue are removed (RETIRE-03), plus a final remnant sweep, leaving the system buildable and running on the v4 path alone. NOTE (D-02): the `H`/`flag[H]`/CAS dedup machinery (RETIRE-01) and the content-addressing + result manifest + N×M fan-out (RETIRE-02) were already removed in Phase 43 — coupled to the field/type reshape (compile-forced per D-01) — so this phase shrinks to RETIRE-03 + the remnant sweep.
**Depends on**: Phase 47 (the full v4 path — pipeline, BIT gate, 5-state recovery, consolidated `_DLQ1` — must be in place and the new give-up routing live before the old reactive recovery path is deleted, so no intermediate is non-buildable)
**Requirements**: RETIRE-03 (RETIRE-01/02 coupled-forward to Phase 43 per D-01/D-02)
**Success Criteria** (what must be TRUE):

  1. (RETIRE-01 — landed in Phase 43, remnant-sweep verify) The `H` identity, the `flag[H]` dedup gate, and the CAS `Pending→Ack` flips are removed from the processor and the orchestrator — no `MessageIdentity`/`flag[H]` references remain on the execution path, and the solution builds 0-warning.
  2. (RETIRE-02 — landed in Phase 43, remnant-sweep verify) Content-addressed L2 data, the result manifest, and the N×M manifest fan-out are removed — the orchestrator no longer fans a manifest, and L2 data is the GUID `entryId` scheme only.
  3. The reactive `Fault<EntryStepDispatch>`/`Fault<ExecutionResult>` Keeper recovery path and the `keeper-dlq` queue are removed — Keeper recovers solely via the BIT gate + the four state messages, and there is no `Fault<T>` consumer and no `keeper-dlq` topology left.
  4. The full hermetic suite is GREEN and the solution builds clean (Release + Debug, 0 warnings) on the v4 path with all retired machinery gone (no dead `Ignore<>`/binding/key remnants).

**Plans**: 3 plans (3 waves — Wave 1: atomic teardown [reactive source+metrics delete, L2ProbeRecovery partial-delete, Program.cs unwire, const/config/key sweep, all coupled test deletes/edits — every intermediate builds per research §7]; Wave 2: Phase-48 negative-guard facts + widen the Phase-47 keeper-dlq scan, gated on the deletes landing; Wave 3: D-04 reconciliation [48-TEARDOWN-AUDIT.md + REQUIREMENTS flips + design-doc A17] + the SC-4 hermetic ×3-green + 0-warning close gate)

- [x] 48-01-PLAN.md — Wave 1: atomic teardown of the reactive Fault<T> path + KeeperMetrics + const/config/key sweep + coupled test deletes/edits (RETIRE-03; SC-3 + SC-4 build half) — complete 2026-06-09; Debug build 0-warning, hermetic suite 503/503 (RealStack-excluded). Commits 5f0e210, 384bde5.
- [x] 48-02-PLAN.md — Wave 2: ReactivePathRetiredFacts negative-guard (no Fault<T> consumer / no keeper-dlq literal / const absence / SC-2 ExecutionData-Guid-only) + widen the Phase-47 keeper-dlq scan (RETIRE-03, RETIRE-01/02 remnant-verify; SC-1/SC-2/SC-3) — complete 2026-06-09; Phase=48 trait facts 4/4 GREEN, widened Phase-47 scan GREEN, Debug build 0-warning. Commits 7bf0f18, 3ef4248.
- [x] 48-03-PLAN.md — Wave 3: 48-TEARDOWN-AUDIT.md ledger + REQUIREMENTS RETIRE-01/02/03 satisfied + design-doc A17 amendment + SC-4 close gate (hermetic ×3 GREEN + Release/Debug 0-warning) (RETIRE-01/02/03; SC-4) — complete 2026-06-09; SC-4 met (507/507 ×3, Release+Debug 0-warning). Commits c0df0bc, 8033773.

#### Phase 49: Live Proof & Close Gate

**Goal**: A real-stack E2E proves the full Pre/In/Post round trip plus each recovery path and the BIT-gate global pause-all/resume-all across a transient L2 outage, all sealed behind an N-consecutive-GREEN triple-SHA (psql / redis / rabbitmq) net-zero close gate matching prior-milestone discipline.
**Depends on**: Phase 48 (the gate seals the final v4-only contract, after teardown)
**Requirements**: TEST-01, TEST-02, TEST-03
**Success Criteria** (what must be TRUE):

  1. A real-stack E2E proves the full Pre → In → Post round trip end to end (dispatch consumed → output written to L2 → orchestrator advances on the per-item `ExecutionResult`).
  2. A real-stack E2E proves each recovery path: `REINJECT` data-present (re-injected to `queue:{ProcessorId}`), `REINJECT` data-gone → `_DLQ1`, `INJECT` (reconstructed `Completed` → orchestrator), and `DELETE`.
  3. A real-stack E2E proves the BIT-gate global pause-all/resume-all across a transient L2 outage (outage → pause all → L2 recovers → resume all), with pause/resume idempotent per job.
  4. The close gate runs N consecutive GREEN with triple-SHA (psql `\l` / redis `--scan` / rabbitmq `list_queues`) BEFORE==AFTER net-zero — including the composite backup key (proven cleaned by `CLEANUP`/`INJECT`, not lingering on its 2-day TTL), the GUID data keys, and `_DLQ1` — at Release + Debug 0-warning.

**Plans**: 6 plans (4 + 2 gap-closure: 49-05 GAP-49-2, 49-06 GAP-49-3/4/5 — code closed; live N×GREEN close gate operator-gated)

  - [x] 49-01-PLAN.md — SC1 RealStack round-trip E2E (Pre->In->Post; output-to-L2; orchestrator-advance)
  - [x] 49-02-PLAN.md — SC2 RealStack recovery-paths E2E (REINJECT present/gone, INJECT, DELETE via keeper-recovery direct-publish)
  - [x] 49-03-PLAN.md — SC3 RealStack pause-resume-outage E2E (docker stop/start sk-redis; non-parallel collection)
  - [x] 49-04-PLAN.md — phase-49-close.ps1 triple-SHA close gate + 49-HUMAN-UAT.md operator runbook
  - [ ] 49-05-PLAN.md ΓÇö GAP-49-2 fix: ResumeAll clears pausedTriggerGroups after per-job loop (group-resume wrapper + no-burst/ordering regression)

### Progress (v4.0.0)

| Phase | Plans Complete | Status | Completed |
| ----- | -------------- | ------ | --------- |
| 43. Message Contracts & L2 Key Reshape | 5/5 | Complete | 2026-06-08 |
| 44. Processor Pre/In/Post-Process Pipeline | 3/3 | Complete | 2026-06-08 |
| 45. Keeper BIT Health Gate + Global Pause/Resume | 3/3 | Complete | 2026-06-08 |
| 46. Keeper 5-State Recovery + Orchestrator Per-Item Consume | 4/4 | Complete | 2026-06-09 |
| 47. DLQ Consolidation + At-Least-Once Semantics | 3/3 | Complete | 47-01 ✓ (RESIL-02, RESIL-03 structural guards); 47-02 ✓ (R3 no-collapse facts + R2 Phase-47 re-tag); 47-03 ✓ (47-DLQ-AUDIT.md ledger + design-doc A16 amendment) |
| 48. v3.x Teardown | 3/3 | Complete | 2026-06-09 |
| 49. Live Proof & Close Gate | 6/6 | In Progress (live gate operator-gated) | — |

## ✅ v8.0.0 E2E Resilience Proof (SHIPPED 2026-06-15; full record [milestones/v8.0.0-ROADMAP.md](milestones/v8.0.0-ROADMAP.md))

> **Shipped 2026-06-15.** Whole-system **live recovery proof under faults**. 6 phases (63–68), 15 plans; 7-scenario live capstone **7/7 PASS** across all fault classes (initial sweep 6/7; TEST-06 rabbitmq re-ran clean after a **config-only** TTL fix — `Processor__ExecutionDataTtl` 5→300, a leftover v6.0.0 close-gate hack obsolete once v8.0.0 retired the triple-SHA net-zero gate; zero production source touched). Truth = Prometheus + Elasticsearch only. Supersedes v7.0.0's deferred Phase-62 live proof. Detail below retained as the v8.0.0 design record.

**Milestone Goal:** Prove perfect (**zero-missing**, **effect-once**) recovery of a fan-out orchestrated workflow under 7 sustained 5-minute fault scenarios, verified **solely** from Prometheus metrics and Elasticsearch logs, fully automated — no human verification.

**Workflow under test:** a single seeded definition `A → B → C → { D1 → E1 → F1, D2 → E2 → F2 }` (9 steps, entry A, fan-out at C, two sinks F1/F2), all steps backed by **one** shared `processor-sample`, cron `*/30 * * * * *`, each step's assignment payload `{ number:int, label:"Step_*" }`. Step identity comes from the payload label (`Step_*`), **not** the processor.

**Pass bar (per scenario):** every triggered correlationId reaches **both** sinks F1+F2 (**zero-missing**) AND each step's COMPLETED effect lands **exactly once** per correlationId (**effect-once**); message-level redelivery during a crash is **reported, not failed** (matches the documented exactly-once-effect guarantee).

**Source of truth:** **Prometheus + Elasticsearch ONLY.** The prior triple-SHA infra net-zero close gate (`psql \l` / `redis-cli --scan` / `rabbitmqctl list_queues` BEFORE==AFTER) is **explicitly out of scope** for v8.0.0 and is **not** a pass criterion.

**Scope discipline (locked):** This milestone **PROVES the existing recovery machinery** (keeper, exactly-once-effect, slot-array, per-replica liveness) — it adds **no new recovery logic**. The **only product code changes** are: (a) enable 6-field seconds-cron (`CronInterval` + the 5-field validator), and (b) the processor int+string payload + random-add + `Step_*` structured logging. Everything else is **test harness + seeder + analyzer**. All 9 steps share one `processor-sample`; `processor-badconfig` is dropped from this stack.

**Build order (locked, dependency-driven):**

1. **63** seconds-cron enablement [CRON] — product change, independent.
2. **64** processor int+string payload + `Step_*` logging [PROC] — product change, independent (could run parallel to 63).
3. **65** 9-step fan-out workflow seeder + minimal clean-state stack [WF, ENV] — depends on **63 + 64**.
4. **66** Prometheus + ES analyzer / per-test report + PASS/FAIL engine [OBS] — depends on **64** (the processor logging shape it parses).
5. **67** 7-scenario fault-injection harness [FAULT] — depends on **65 + 66**.
6. **68** Live Resilience Proof — 7 scenarios capstone [TEST] — depends on **67** (runs the 7 proofs through the harness; mirrors prior milestones' final live-proof phase).

**Source of truth (planning):** This milestone's planning conversation (2026-06-14), scope confirmed point-by-point. Requirements: [REQUIREMENTS.md](REQUIREMENTS.md) (23 reqs across CRON/PROC/WF/ENV/OBS/FAULT/TEST).

### Phases

- [x] **Phase 63: Seconds-Granularity Cron** — Enable 6-field seconds-cron (`*/30 * * * * *`) end-to-end: `CronInterval` next-occurrence/interval math sub-minute (UTC) + the create/update validator accepts the 6-field form (5-field still accepted). ✓ completed 2026-06-14 (3 plans; CRON-01 + CRON-02; shared CronFieldForm detector consumed by both scheduler + validators).
- [x] **Phase 64: Processor Work & Structured Logging** — `SampleConfig` carries an int + string; `ProcessAsync` random-adds to the int and emits the sum; a `Step_<label>` structured log carries correlationId + stepId so ES can aggregate a run.
- [x] **Phase 65: Fan-Out Workflow Seeder & Clean-State Stack** — Idempotent seeder for the 9-step fan-out workflow (one shared processor, seconds-cron, `Step_*` payloads) + a minimal clean-state stack (single `processor-sample`, full infra+observability; flush/reset between runs). (completed 2026-06-14)
- [x] **Phase 66: Prometheus + ES Analyzer & PASS/FAIL Engine** — Aggregate ES logs by correlationId into per-run traces (all 9 steps + both sinks?), detect MISSING/DUPLICATE vs total triggers, cross-check Prometheus counters, and emit a per-test report + automated PASS/FAIL verdict (Prom + ES only).
 (completed 2026-06-14)

- [x] **Phase 67: Fault-Injection Harness** — Activate via `POST /api/v1/orchestration/start`, run a 5-minute/30s-cron window, inject each scenario's mid-run fault (container kill/restart) and let the system recover — fully automated end-to-end (clean → seed → activate → inject → observe → analyze → tear down), no human step.
 (completed 2026-06-14 — harness proven live on TEST-01 baseline + TEST-02 processor-crash recovery, both Pass 10/10; FAULT-01/02/03 met)

- [x] **Phase 68: Live Resilience Proof — 7 Scenarios (Capstone)** — Run all 7 proofs (happy path + processor / orchestrator / keeper / redis / rabbitmq / redis+rabbitmq crash) through the harness; each PASSES iff zero-missing + effect-once hold over its window; redelivery during the fault reported, not failed.
 (completed 2026-06-15)

### Phase Details

#### Phase 63: Seconds-Granularity Cron

**Goal**: The orchestrator can fire a workflow on a 6-field seconds-granularity cron expression (`*/30 * * * * *` → every 30 seconds). `CronInterval`'s next-occurrence + interval math computes sub-minute intervals correctly in UTC, and the workflow create/update cron validator accepts the 6-field seconds form (previously rejected as non-5-field-standard) while still accepting the 5-field form. This is a product code change (lifting today's `CronFormat.Standard` 1-minute floor) and a prerequisite for the 30s-cadence fan-out workflow the rest of the milestone observes.
**Depends on**: — (first phase of v8.0.0; independent product change — can run parallel to Phase 64)
**Requirements**: CRON-01, CRON-02
**Success Criteria** (what must be TRUE):

  1. A workflow scheduled with `*/30 * * * * *` fires every 30 seconds — `CronInterval` next-occurrence/interval math yields the correct sub-minute fire times in UTC (proven by a unit/hermetic fact and observable as ~10 triggers over a 5-minute window).
  2. The create/update cron validator accepts the 6-field seconds form (a previously-rejected `*/30 * * * * *` now passes validation).
  3. The 5-field standard cron form is still accepted (no regression — both forms validate and schedule).
  4. Solution builds 0-warning (Release + Debug); the hermetic suite is green against the seconds-cron change.

**Plans**: 3 plans

  - [x] 63-01-PLAN.md — CronFieldForm shared detector + unit test (Wave 1) ✓ completed 2026-06-14; pure token-count detector `CronFieldForm` (IsSecondsForm/IsValidFieldCount) hoisted into `Messaging.Contracts.Projections` (zero Cronos dep, contracts leaf parser-free); 8/8 detector facts green; commits 0e48293, ee2b12d
  - [x] 63-02-PLAN.md — CronInterval rewire + */30 sub-minute fact (Wave 2) ✓ completed 2026-06-14; both NextOccurrence + IntervalSeconds resolve CronFormat via the shared `CronFieldForm` detector (IncludeSeconds 6-field / Standard 5-field), lifting the 1-minute floor; `IntervalSeconds("*/30 * * * * *") == 30` + 6-field NextOccurrence strictly-future/Kind=Utc proven, 5-field facts retained (5/5 CronInterval green); D-08 firewall + UTC contract intact, 0-warning build; CRON-01 satisfied scheduler-side; commits e6c9d42, 35e3ccc
  - [x] 63-03-PLAN.md — Both validators rewire + message update + tests (Wave 2) ✓ completed 2026-06-14; both WorkflowCreate/UpdateDtoValidator `BeValidStandardCron` route through the shared `CronFieldForm` detector (reject non-5/6 up front, no exception-as-control-flow D-02; one guarded `CronExpression.Parse(expr, format)` with IncludeSeconds/Standard), 6-field `*/30 * * * * *` accepted + 5-field regression-guarded + malformed/wrong-count rejected; both user-facing messages → "5- or 6-field" (D-11); no interval floor (D-06); WorkflowCronValidatorTests 8/8 green against BOTH validators; CRON-02 satisfied validation-side; commits 4fec1a6, 34a9059

#### Phase 64: Processor Work & Structured Logging

**Goal**: The shared `processor-sample` does observable, correlatable work. Its config (`SampleConfig`) carries an **integer** and a **string** (the framework deserializes the assignment payload into the typed config exposing both fields); `ProcessAsync` generates a random number, adds it to the payload integer, and produces the sum as the step's completed result; and it emits a **structured log entry** tagged with the payload string `Step_<label>` and the computed sum, carrying `correlationId` + `stepId` (plus `workflowId`/`processorId`) so Elasticsearch can aggregate a whole run by correlationId and identify each step. This is the second product code change and the data shape the analyzer (Phase 66) parses.
**Depends on**: — (independent product change — can run parallel to Phase 63; builds on the v6.0.0 typed base-config seam)
**Requirements**: PROC-01, PROC-02, PROC-03
**Success Criteria** (what must be TRUE):

  1. A step's assignment payload carries an integer and a string, and the framework deserializes it into the typed config (`SampleConfig`) exposing both fields (proven by a deserialization fact).
  2. `ProcessAsync` generates a random number, adds it to the payload integer, and the sum is the step's completed result (the result is non-deterministic across fires by the random addend, deterministic in structure).
  3. `ProcessAsync` emits exactly one structured log entry per execution tagged `Step_<label>` with the computed sum and carrying `correlationId` + `stepId` (+ `workflowId`/`processorId`), so an Elasticsearch query by `correlationId` returns one identifiable entry per step.
  4. Solution builds 0-warning (Release + Debug); the hermetic suite is green against the reshaped processor work + logging.

**Plans**: 1 plan

  - [x] 64-01-PLAN.md — SampleConfig int+string reshape, sum transform + single Step_* structured log, 3-fact hermetic rewrite (Wave 1) ✓ SampleConfig(int Number, string? Label); ProcessAsync sum=(config?.Number ?? 0)+Random.Shared.Next(0,100) → {number,label} JSON Data + single LogInformation {StepLabel}+{Sum} (T-64-01 mitigated); CapturingLogger state-KVP capture; SampleProcessorFacts 3/3 GREEN, Debug+Release 0/0; PROC-01/02/03; commits b323286, ddbfd8b, 4fe6142

#### Phase 65: Fan-Out Workflow Seeder & Clean-State Stack

**Goal**: An idempotent seeder creates the fan-out workflow `A→B→C→{D1→E1→F1, D2→E2→F2}` (9 steps, entry A, fan-out at C, sinks F1+F2) with every step referencing **one** shared `processor-sample`, the `*/30 * * * * *` cron, and a `{ number, label:"Step_*" }` assignment payload per step — re-runnable without duplicating workflow/step/assignment rows — and the proof runs on a **minimal clean-state stack**: a single `processor-sample` (the redundant `processor-badconfig` excluded) alongside the full infra + observability tiers (postgres, redis, rabbitmq, otel-collector, elasticsearch, prometheus, orchestrator, keeper, baseapi-service), with each test started from clean state (Redis flushed, Postgres workflow/step/assignment rows reset, leftover/redundant processor containers removed) so a run's metrics and logs are attributable to that run only.
**Depends on**: Phase 63 (the seconds-cron must be schedulable + validator-accepted before the seeder can attach `*/30 * * * * *`) and Phase 64 (each step's assignment carries the int+string `Step_*` payload the processor consumes)
**Requirements**: WF-01, WF-02, ENV-01, ENV-02
**Success Criteria** (what must be TRUE):

  1. Running the seeder creates the 9-step fan-out workflow (entry A, fan-out at C, sinks F1+F2) with every step referencing the one shared `processor-sample` and the `*/30 * * * * *` cron.
  2. Each of the 9 steps has an assignment carrying a `{ number, label:"Step_*" }` payload, and re-running the seeder produces no duplicate workflow/step/assignment rows (idempotent).
  3. The stack brings up exactly one `processor-sample` (no `processor-badconfig`) alongside the full infra + observability tiers, all healthy.
  4. The clean-state routine leaves a deterministic baseline before each run — Redis flushed, the workflow/step/assignment rows reset, and any leftover/redundant processor containers removed — so a subsequent run's metrics and logs are attributable to that run only.

**Plans**: 3 plans

- [x] 65-01-PLAN.md — Self-verifying RealStack fan-out seeder fixture (WF-01, WF-02) ✓ completed 2026-06-14 — FanOutSeederE2ETests: idempotent reverse-topo build (9 steps/8 edges/9 assignments, 1 shared processor-sample, `*/30 * * * * *` cron, sentinel `v8-fanout-proof`) + inline Npgsql self-verify (1/9/9/8/1, distinct proc=1, workflow_assignments=9, exact 8-edge set, F1/F2 zero-outgoing, 9 distinct `{int number, Step_* label}` payloads) + run-twice idempotency; live 1/1 GREEN; commits c9cc582, c16d5fe
- [x] 65-02-PLAN.md — Clean-state reset script: FLUSHALL + heal-wait + FK-safe psql DELETE + processor-set assert (ENV-02) ✓ completed 2026-06-14 — scripts/phase-65-reset.ps1: FLUSHALL → bounded fail-loud heal-wait on per-instance `skp:proc:*:*` liveness key (index-SET excluded) → FK-safe transactional psql DELETE of the 6 workflow-graph tables via `compose exec -d stepsdb` (processors+config_schemas preserved) → processor-set assert (badconfig-by-exact-name only, stack stays UP); AST parse-clean; commit e9fa3a6. NOTE: Task 2 live reset→seed cycle operator-WAIVED ("no human verification required") — outstanding manual acceptance step, not an executed/passed live check
- [x] 65-03-PLAN.md — Minimal-stack bring-up script: compose up + 10-service health wait + zero-badconfig assert (ENV-01)

#### Phase 66: Prometheus + ES Analyzer & PASS/FAIL Engine

**Goal**: An analyzer determines a run's correctness **solely** from Prometheus + Elasticsearch. It aggregates all Elasticsearch logs sharing a `correlationId` into a per-run trace and decides whether all 9 steps and **both** sinks (F1, F2) completed; against the total number of cron triggers it detects **MISSING** runs/steps (a triggered correlationId that did not complete all steps/both sinks) and **DUPLICATE** step effects (a step's COMPLETED effect recorded more than once per correlationId); it queries Prometheus counters (`orchestrator_dispatch_sent`, `orchestrator_result_consumed`, `processor_dispatch_consumed`, `processor_result_sent{outcome}`, dedupe + keeper counters) and cross-checks dispatched vs completed vs deduped against the trigger count; and it emits a complete per-test smoke report (correlationId-aggregated log trace + metric summary) and an automated **PASS/FAIL** verdict derived **only** from Prometheus + Elasticsearch.
**Depends on**: Phase 64 (the analyzer parses the `Step_<label>` + correlationId/stepId log shape and the processor/orchestrator counters those executions emit)
**Requirements**: OBS-01, OBS-02, OBS-03, OBS-04
**Success Criteria** (what must be TRUE):

  1. Given a `correlationId`, the analyzer aggregates its Elasticsearch logs into one per-run trace and reports whether all 9 steps and both sinks (F1, F2) completed.
  2. Against the total trigger count, the analyzer flags MISSING runs/steps (an incomplete triggered correlationId) and DUPLICATE step effects (a step's COMPLETED effect recorded more than once per correlationId).
  3. The analyzer queries the named Prometheus counters and cross-checks dispatched vs completed vs deduped against the total trigger count, surfacing any imbalance.
  4. Each run emits a per-test smoke report (correlationId-aggregated trace + metric summary) and an automated PASS/FAIL verdict derived solely from Prometheus + Elasticsearch (no human inspection, no infra-SHA net-zero input).

**Plans**: 3 plans

  - [x] 66-01-PLAN.md — PassFailEngine + models + hermetic decision-branch facts (pure core, OBS-01/02/03) (Wave 1) ✓ completed 2026-06-14; OBS-01/02/03; 6/6 PassFailEngineFacts green (441ms); commits cd5424c, 178eea6, e7fe63d
  - [x] 66-02-PLAN.md — SearchAllHits multi-hit ES extension + EsIndexNames Step consts + grouping facts (item #3, OBS-01) (Wave 1) ✓ completed 2026-06-14; OBS-01; 4/4 ElasticsearchTestClientFacts green; commits 5047f3c, 3c39f25
  - [x] 66-03-PLAN.md — AnalyzerE2ETests RealStack fixture: Wave-0 mapping/windowing probes + gather ES+Prom → engine → write-then-assert report (OBS-04) (Wave 2) ✓ completed 2026-06-14; OBS-01/02/03/04; build succeeded 0 warnings; @timestamp Wave-0-probed type:date, phase-65-reset confirmed FLUSHALL+heal (no restart → windowed delta); commits 0514d91, e6ea260

#### Phase 67: Fault-Injection Harness

**Goal**: A fully-automated harness drives one scenario end-to-end: it activates the workflow via `POST /api/v1/orchestration/start` and lets the cron drive it for a **5-minute** observation window (~10 triggers, fresh correlationId per fire); mid-run it injects the scenario's fault (container kill/restart of the targeted tier) and allows the system to recover within the same window; and the whole sequence — clean → seed → activate → inject fault → observe → analyze → tear down — runs with **no human verification step**, wiring together the Phase 65 seeder/clean-stack and the Phase 66 analyzer.
**Depends on**: Phase 65 (clean-state stack + seeder) and Phase 66 (analyzer + PASS/FAIL engine the harness invokes to score each run)
**Requirements**: FAULT-01, FAULT-02, FAULT-03
**Success Criteria** (what must be TRUE):

  1. The harness activates the workflow via `POST /api/v1/orchestration/start` and observes a 5-minute window during which the cron fires ~10 times, each with a fresh correlationId.
  2. The harness injects a scenario's fault mid-run (container kill/restart of the targeted tier) and the system continues/recovers within the same window.
  3. A single scenario runs fully automated end-to-end (clean → seed → activate → inject fault → observe → analyze → tear down) with no human verification step, producing the analyzer's report + verdict.

**Plans**: 3 plans (2 waves)
Plans:

- [x] 67-01-PLAN.md — D-16 env-var seam in AnalyzerE2ETests.cs (SCENARIO_ID/WINDOW_START_UTC/WINDOW_END_UTC, const/UtcNow fallback); Phase 66 standalone stays green (FAULT-01, FAULT-03) (completed 2026-06-14 — seam in place + Release 0/0 -warnaserror; commit 55b2fef. Task-2 checkpoint resolved BY CONSTRUCTION: fallback proven live (no-env run wrote analyzer-reports/TEST-01.json via const default), but the full standalone analyzer-GREEN verdict was NOT achieved this session — DEFERRED to 67-03 under a controlled window. Three environmental findings handed to 67-02/67-03: MTP ignores `dotnet test --filter` → use `-- --filter-method`; phase-65-reset does not stop ghost orchestrator crons (NULL-payload fires, zero Step_* docs); `POST /orchestration/start` returned 422 for the fresh workflow)
- [x] 67-02-PLAN.md — Author scripts/phase-67-harness.ps1: scenario table (TEST-01/TEST-02) + clean->seed->psql-wf-id->204-gate->observe->whole-tier-crash->health-wait->analyze->teardown, exit mirrors analyzer verdict (FAULT-01, FAULT-02, FAULT-03) (completed 2026-06-14 — 330-line harness, +STEP B1 clean-orchestrator restart + MTP --filter-method; commits 6ca3f0d, 79a7137, 78fc508)
- [x] 67-03-PLAN.md — Two reference runs: TEST-01 baseline-first (TriggerCount~10) then TEST-02 processor crash; each fully automated, produces a correctly-named verdict report (FAULT-01, FAULT-02, FAULT-03) (completed 2026-06-14 — both runs Pass 10/10 (zero-missing, effect-once); TEST-02 whole-tier crash + in-window recovery proven; OBS-04 verdict re-founded on ES-binding arbiter (Prom→corroboration) absorbing the mid-window counter reset — corrects a Phase-66 per-run-denominator defect, spec-owner-approved; deviations d2445fb, a9e42e0, 574739a)

#### Phase 68: Live Resilience Proof — 7 Scenarios (Capstone)

**Goal**: Run all **7** resilience proofs through the harness and produce a passing automated verdict for each. Each scenario runs its 5-minute/30s-cron window and PASSES iff **zero-missing** (every triggered correlationId reaches both sinks F1+F2) AND **effect-once** (each step's COMPLETED effect once per correlationId) hold; message-level redelivery during the fault is **reported, not failed**. This is the milestone capstone (mirroring prior milestones' final live-proof phase), proving the existing recovery machinery survives each fault class. Truth = Prometheus + Elasticsearch only.
**Depends on**: Phase 67 (the harness that runs each scenario and the analyzer that scores it)
**Requirements**: TEST-01, TEST-02, TEST-03, TEST-04, TEST-05, TEST-06, TEST-07
**Success Criteria** (what must be TRUE):

  1. **TEST-01 Happy path** — no fault injected: zero-missing + effect-once both hold (baseline PASS).
  2. **TEST-02/03/04 single-service crashes** — processor (02), orchestrator (03), and keeper (04) each crash mid-run and recovery is proven (zero-missing + effect-once hold; redelivery reported, not failed).
  3. **TEST-05/06 infra crashes** — Redis (05) and RabbitMQ (06) each crash mid-run and recovery is proven (zero-missing + effect-once hold).
  4. **TEST-07 combined infra crash** — Redis + RabbitMQ crash together mid-run and recovery is proven (zero-missing + effect-once hold).
  5. All 7 scenarios produce an automated PASS verdict derived solely from Prometheus + Elasticsearch — no human verification, no triple-SHA infra net-zero gate.

**Plans**: 2 plans

  - [x] 68-01-PLAN.md — Author the 5 scenario rows (TEST-03..07) + the phase-68-sweep.ps1 wrapper + cosmetic fixture-rename/literal-sync (Wave 1, static-verified) (completed 2026-06-15 — harness $Scenarios now 7 rows; scripts/phase-68-sweep.ps1 run-all+collect exit-0-iff-7/7; fixture renamed Analyze_Window_Yields_Pass + both --filter-method literals synced; test project 0-warning; 3f359f2, f39530d, 7556ee4)
  - [x] 68-02-PLAN.md — Run the 7-scenario live sweep; prove 7/7 PASS roll-up; investigate-first on any verdict FAIL (Wave 2, live-empirical, checkpoint) (completed 2026-06-15 — initial sweep 6/7 PASS [TEST-01..05 + TEST-07 zero-missing + effect-once], wrapper exit 1; TEST-06 rabbitmq VERDICT_FAIL [MISSING:2] traced to the by-design ReinjectConsumer.cs:37 silent-DROP — the leftover 5s ExecutionDataTtl self-expired across the 45s outage, KeeperReinjectDroppedDelta:2 == Missing:2; corroborated by TEST-07 superset PASS [no recovery defect]; 098f36a. **Resolution (post-checkpoint, spec-owner-directed):** config-only fix — `Processor__ExecutionDataTtl` raised 5→300 in compose.yaml [the "5" was an obsolete v6.0.0 close-gate hack]; TEST-06 re-ran **clean PASS** [Missing:0, KeeperReinjectDroppedDelta 2→0]; **capstone now 7/7** across all fault classes, zero production source touched; da91d32. TTL later unified to the const + SlotArrayOptions removed via quick-task 260615-dbf.)

### Progress (v8.0.0)

| Phase | Name | Plans | Status | Completed |
| ----- | ---- | ----- | ------ | --------- |
| 63 | Seconds-Granularity Cron | 3/3 | Complete    | 2026-06-14 |
| 64 | Processor Work & Structured Logging | 1/1 | Complete    | 2026-06-14 |
| 65 | Fan-Out Workflow Seeder & Clean-State Stack | 3/3 | Complete    | 2026-06-14 |
| 66 | Prometheus + ES Analyzer & PASS/FAIL Engine | 3/3 | Complete    | 2026-06-14 |
| 67 | Fault-Injection Harness | 3/3 | Complete    | 2026-06-14 |
| 68 | Live Resilience Proof — 7 Scenarios (Capstone) | 2/2 | Complete    | 2026-06-15 |

**Coverage:** 23/23 v8.0.0 requirements mapped (CRON-01/02 → 63 · PROC-01/02/03 → 64 · WF-01/02 + ENV-01/02 → 65 · OBS-01/02/03/04 → 66 · FAULT-01/02/03 → 67 · TEST-01..07 → 68). No orphans, no duplicates.
