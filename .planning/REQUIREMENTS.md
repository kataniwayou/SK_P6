# Requirements: Steps API — v11.0.0 Kafka Binary Import/Export

Milestone goal: add Kafka-sourced binary file import (and later export) to the pipeline, built on a foundational widening of the framework data channel from `string` to `byte[]`.

## v11.0.0 Requirements

### DATA — Framework Data-Channel Widening (byte[])

- [ ] **DATA-01**: `DataResult.Data` carries `byte[]` as its ground-truth type, so binary payloads flow through the framework (L2 write/read, seam, recovery) without base64 encoding or string coercion.
- [ ] **DATA-02**: When a JSON input/output schema is configured, the framework interprets the `byte[]` as UTF-8 JSON and validates it against the schema; when no schema is configured, the bytes are treated as an opaque payload (schema is a lens over the bytes, not a type switch).
- [ ] **DATA-03**: The existing string/JSON processing path remains byte-for-byte equivalent after the change — a value written as a string equals its UTF-8 bytes read back — with no behavioral change for existing processors (`Processor.Sample` stays green).
- [ ] **DATA-04**: The existing 7-scenario fault-recovery sweep (TEST-01..TEST-07, `scripts/phase-68-sweep.ps1`) reproduces its all-PASS baseline (all `Missing==0`, all `Duplicates==0`) — this is the SOLE verification gate for the widening; the sweep reproducing green IS the proof that the change altered nothing observable.
- [ ] **DATA-05**: Recovery (`KeeperInject` / `OrchestratorInject`) carries `byte[]` Data through the recovery backup without loss (MassTransit base64s the field transiently on the RabbitMQ wire only — the L2 hot path stays binary).

### KIMP — Kafka Import

- [ ] **KIMP-01**: A KafkaImporter processor, triggered by the scheduler as a Mode-2 entry step (`executionId == Guid.Empty`), consumes up to N messages per dispatch from a configured topic and consumer group.
- [ ] **KIMP-02**: Each consumed message (a binary file) becomes one execution with a freshly-minted `executionId`; its bytes are written directly to L2 and are never carried on the message bus (the dispatch/hand-off messages remain small JSON — config + coordination ids only).
- [ ] **KIMP-03**: The importer commits each Kafka message's offset ONLY after that file's L2 store and downstream hand-off are confirmed successful (per-message commit, fail-loud — no best-effort swallow); any failure leaves the offset uncommitted so the message and remainder redeliver on the next dispatch.
- [ ] **KIMP-04**: The importer is content-agnostic — it moves bytes from the topic to L2 without inspecting or depending on the file type or content.
- [ ] **KIMP-05**: Kafka runs as a standalone container (not a k8s workload); the importer connects from within the cluster via a configured bootstrap address (`host.docker.internal:9092`), with the broker's advertised listeners set so the in-cluster client can reconnect.
- [ ] **KIMP-06**: The importer's `ProcessorConfig` carries the topic name, consumer group, and batch size N (deriving from the framework `ProcessorConfig` marker, like `SampleConfig`).

## Future Requirements (deferred)

- **MANIP-01**: A file-manipulator step reads the imported binary from L2, transforms it (e.g. unzip → operate on the XML/WAV), and emits its output.
- **KEXP-01**: A KafkaExporter terminal step serializes L2 content back to a ZIP and produces it to a Kafka topic.
- Idempotency key derived from the Kafka message (topic-partition-offset) to enable true exactly-once import dedup.

## Out of Scope

- True exactly-once import dedup — at-least-once is accepted for this milestone; per-message commit minimizes but does not eliminate the crash-between-handoff-and-commit duplicate window, and a redelivered file mints a new `executionId` (not deduped by the existing execution-lineage idempotency).
- Running Kafka as a k8s workload — it is deliberately a standalone container, unlike Postgres/Redis/RabbitMQ/Elasticsearch.
- Ordering guarantees beyond Kafka's native per-partition order.
- The claim-check / external-blob-store pattern — rejected for this milestone in favor of the widened `byte[]` spine (revisit only if file sizes outgrow Redis).

## Traceability

| REQ-ID | Phase | Status |
|--------|-------|--------|
| DATA-01 | 84 | Complete |
| DATA-02 | 84 | Complete |
| DATA-03 | 84 | Complete |
| DATA-04 | 84 | Complete |
| DATA-05 | 84 | Complete |
| KIMP-01 | 85 | Not started |
| KIMP-02 | 85 | Not started |
| KIMP-03 | 85 | Not started |
| KIMP-04 | 85 | Not started |
| KIMP-05 | 85 | Not started |
| KIMP-06 | 85 | Not started |
