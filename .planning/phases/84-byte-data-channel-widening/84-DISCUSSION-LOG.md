# Phase 84: `byte[]` Data-Channel Widening - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-21
**Phase:** 84-byte-data-channel-widening
**Areas discussed:** Data field shape, Author/test ergonomics, Migration sequencing, Non-UTF8 / bad-bytes under a schema

---

## Data field shape

| Option | Description | Selected |
|--------|-------------|----------|
| Outright replace | Replace `string Data` with `byte[] Data` — byte[] is ground truth | ✓ |
| Parallel field + discriminator | Keep `string Data`, add `byte[]? Binary` + a type discriminator | |

**User's choice:** Outright replace (D-01).
**Notes:** Parallel field reintroduces the rejected type-switch and creates two sources of truth (serialization/recovery ambiguity). One field, always bytes.

---

## Author / test ergonomics

| Option | Description | Selected |
|--------|-------------|----------|
| Raw byte[] only | Every author + every hermetic test does `Encoding.UTF8.GetBytes/GetString` | |
| Thin UTF-8 helpers | Encode-on-write overload + decode-on-read string accessor; byte[] stays stored type | ✓ |

**User's choice:** Thin UTF-8 convenience helpers (D-02).
**Notes:** Edge conversions only — not a return to string-as-truth. Minimizes churn across `Processor.Sample` and existing `.Data` test assertions.

---

## Migration sequencing

| Option | Description | Selected |
|--------|-------------|----------|
| Big-bang | Contract + all consumers + Sample + tests in one wave | |
| Staged, no bridge | Contract+helpers → consumers+Sample+recovery → sweep; no throwaway string↔byte bridge | ✓ |

**User's choice:** Staged waves, no temporary bridge (D-03).
**Notes:** A bridge adds complexity that must then be removed and leaves a half-migrated state the byte-for-byte invariant can't cleanly check. Exact wave breakdown left to the planner; the locked decision is "no bridge."

---

## Non-UTF8 / bad-bytes under a schema

| Option | Description | Selected |
|--------|-------------|----------|
| Business Failed (existing path) | Reuse `ProcessorJsonSchemaValidator` malformed-data → `Failed` | ✓ |
| New error handling | Introduce a distinct error surface for non-UTF8 bytes | |

**User's choice:** Business `Failed`, existing path (D-04).
**Notes:** Non-UTF8 under a schema is the same class as today's malformed-JSON (`ProcessorJsonSchemaValidator.cs:46-51`). Try UTF-8 decode; on failure → false → `Failed`. No new error surface. Only arises under a schema — schema-absent bytes are never decoded.

---

## Claude's Discretion

- Exact wave breakdown within the D-03 staging.
- Exact naming/shape of the D-02 helpers (overload vs. extension vs. static factory), byte[] stays stored.
- Whether the recovery byte[]-over-RabbitMQ wire warrants a size note.

## Deferred Ideas

- KafkaImporter (KIMP-01..06) — Phase 85, depends on this.
- File-manipulator (MANIP-01) + KafkaExporter (KEXP-01) — future milestones.
- Switching MassTransit to a binary serializer to remove transient wire base64 — out of scope.
