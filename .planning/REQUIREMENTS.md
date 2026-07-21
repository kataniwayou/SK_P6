# Requirements: Steps API — v11.0.0 Framework Data-Channel & Processor Boundary Hardening

Milestone goal: widen the framework data channel from `string` to `byte[]` (Phase 84, shipped), then harden the processor↔framework boundary so a fan-out hand-off can never silently drop a seeded execution and entry-lifecycle ownership lives entirely in the base (Phase 85). Kafka-sourced binary import/export is **deferred to a future milestone** — the byte[] channel is its prerequisite, now in place.

## v11.0.0 Requirements

### DATA — Framework Data-Channel Widening (byte[]) — Phase 84 (SHIPPED)

- [x] **DATA-01**: `DataResult.Data` carries `byte[]` as its ground-truth type, so binary payloads flow through the framework (L2 write/read, seam, recovery) without base64 encoding or string coercion.
- [x] **DATA-02**: When a JSON input/output schema is configured, the framework interprets the `byte[]` as UTF-8 JSON and validates it against the schema; when no schema is configured, the bytes are treated as an opaque payload (schema is a lens over the bytes, not a type switch).
- [x] **DATA-03**: The existing string/JSON processing path remains byte-for-byte equivalent after the change — a value written as a string equals its UTF-8 bytes read back — with no behavioral change for existing processors (`Processor.Sample` stays green).
- [x] **DATA-04**: The existing 7-scenario fault-recovery sweep (TEST-01..TEST-07) reproduces its all-PASS baseline (all `Missing==0`, all `Duplicates==0`) — the SOLE verification gate for the widening.
- [x] **DATA-05**: Recovery (`KeeperInject` / `OrchestratorInject`) carries `byte[]` Data through the recovery backup without loss (MassTransit base64s the field transiently on the RabbitMQ wire only — the L2 hot path stays binary).

### PB — Processor↔Framework Boundary Hardening — Phase 85

- [x] **PB-01**: `SpawnToPost` is **fail-loud** — it signals its spawn outcome to the caller (a transient send-exhaustion is reported via return value or a propagating exception), instead of swallowing the exhaustion as silent success. The drop telemetry hook (`OnSpawnDropped`) and the deterministic-fault throw are preserved.
- [x] **PB-02**: A Mode-2 fan-out entry is acked **only when every spawn succeeded**; a spawn send-exhaustion nack-requeues the entry dispatch (broker redelivery) rather than acking, so no seeded execution is silently lost. This replaces the old best-effort "the scheduler re-fires the whole entry" swallow with an at-least-once broker guarantee.
- [x] **PB-03**: Entry/source deletion is owned by the **framework on every completion path** (the Mode-1 inline tail *and* the Mode-2 null-return path), with the keeper DELETE escalation preserved. The concrete-processor `DeleteEntry` API, the `SampleProcessor` call to it, and the seam plumbing that existed solely for it (`SeamState.EntryId`, the `EscalateDelete` hook) are removed — the concrete processor no longer deletes entries.

**Boundary principle enforced by PB-01..03:** the concrete processor's only responsibility becomes *interpret input bytes → business logic → return a `DataResult`* (or, for a fan-out source, spawn N and return null). Config-deserialization, input/output schema validation, L2 read/write, `Step*` send, entry deletion, and the ack/nack decision are all framework-owned.

## Future Requirements (deferred — future Kafka milestone)

- **KIMP-01**: A KafkaImporter processor, triggered by the scheduler as a Mode-2 entry step (`executionId == Guid.Empty`), consumes up to N messages per dispatch from a configured topic and consumer group.
- **KIMP-02**: Each consumed message (a binary file) becomes one execution with a freshly-minted `executionId`; its bytes are written directly to L2 and never carried on the message bus.
- **KIMP-03**: The importer commits each Kafka message's offset ONLY after that file's L2 store + hand-off are confirmed (per-message, fail-loud); a failure leaves the offset uncommitted for redelivery. *(Built on PB-01/02 — the fail-loud hand-off primitive Phase 85 establishes.)*
- **KIMP-04**: The importer is content-agnostic — moves bytes topic→L2 without inspecting file type/content.
- **KIMP-05**: Kafka runs as a standalone container (not a k8s workload); the importer connects via a configured bootstrap (`host.docker.internal:9092`), advertised listeners set for in-cluster reconnect.
- **KIMP-06**: The importer's `ProcessorConfig` carries topic, consumer group, and batch size N.
- **MANIP-01**: A file-manipulator step reads the imported binary from L2, transforms it (e.g. unzip → operate on the XML/WAV), and emits its output. *(Needs `NextStepHandoff.Data` widened to `byte[]` so real binary can relocate through the orchestrator — the Phase-84 OQ-1 scope fence deferred this.)*
- **KEXP-01**: A KafkaExporter terminal step serializes L2 content back to a ZIP and produces it to a Kafka topic.
- Idempotency key derived from the Kafka message (topic-partition-offset) for true exactly-once import dedup.

## Out of Scope (v11.0.0)

- **KafkaImporter, the Kafka container, and `NextStepHandoff.Data` widening** — deferred to a future Kafka milestone (see Future Requirements). Phase 85 establishes the fail-loud hand-off primitive (PB-01/02) that KIMP-03 will later build on, but ships no Kafka.
- True exactly-once import dedup — at-least-once is the model; PB-02's nack-requeue minimizes but does not eliminate the crash-between-handoff-and-commit duplicate window.
- The claim-check / external-blob-store pattern — rejected in favor of the widened `byte[]` spine (revisit only if file sizes outgrow Redis).

## Traceability

| REQ-ID | Phase | Status |
|--------|-------|--------|
| DATA-01 | 84 | Complete |
| DATA-02 | 84 | Complete |
| DATA-03 | 84 | Complete |
| DATA-04 | 84 | Complete |
| DATA-05 | 84 | Complete |
| PB-01 | 85 | Not started |
| PB-02 | 85 | Not started |
| PB-03 | 85 | Not started |
