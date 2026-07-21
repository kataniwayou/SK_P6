# Phase 85: Processor↔Framework Boundary Hardening - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-21
**Phase:** 85-processor-framework-boundary-hardening
**Areas discussed:** PB-01 signal shape, PB-02 nack semantics, PB-03 delete placement, PB-02 hermetic-fact fault seam, deterministic-vs-transient nack-escape

---

## PB-01 — Spawn-exhaust signal shape

| Option | Description | Selected |
|--------|-------------|----------|
| Propagating exception | SpawnToPost fires OnSpawnDropped telemetry then throws a dedicated exception type; propagates through the author's ProcessAsync (no author catch) to the pipeline. Keeps the boundary principle; mirrors WR-01's deterministic-fault throw at BaseProcessor.cs:115. | ✓ |
| Return value the pipeline inspects | SpawnToPost returns a bool/enum. Rejected: the author's seam returns DataResult? and would have to thread the outcome up, pushing ack/nack awareness into the concrete processor. | |

**User's choice:** Propagating exception (recommended).
**Notes:** Author writes no catch — the dedicated type propagates transparently. Only the transient-exhaust branch (BaseProcessor.cs:116 swallow) changes.

---

## PB-02 — Nack + re-fire semantics

| Option | Description | Selected |
|--------|-------------|----------|
| Bare nack, whole-entry re-fire | Catch-all rethrows the dedicated signal → MassTransit default RabbitMQ nack-requeue (same as keeper-send-exhaust at ProcessorPipeline.cs:304). Whole seed re-fires → already-succeeded spawns duplicate. Accepted at-least-once; no new config. | ✓ |
| Nack with redelivery delay/bound | Same escape-to-nack plus a MassTransit redelivery delay/bound to avoid hot-loop. Rejected: diverges from the bare-nack pattern used everywhere else; adds endpoint config. | |

**User's choice:** Bare nack, whole-entry re-fire (recommended).
**Notes:** Duplicate window on partial-spawn re-fire is accepted — exactly-once dedup is explicitly out of scope (REQUIREMENTS.md).

---

## PB-03 — Framework-owned Mode-2 deletion placement

| Option | Description | Selected |
|--------|-------------|----------|
| Reuse Mode-1 tail delete-then-escalate | On the null-return branch (ProcessorPipeline.cs:206), run the same delete L2[entryId] → escalate KeeperDelete on exhaust the Mode-1 tail uses (:222-227), after the spawns. Preserve SourceStep.IsSource skip. Remove DeleteEntry / EntryId / EscalateDelete. | ✓ |
| Separate dedicated null-path delete helper | A distinct framework delete path for the Mode-2 null case. Rejected: duplicates the delete-then-escalate logic in two places. | |

**User's choice:** Reuse the Mode-1 tail delete-then-escalate (recommended).
**Notes:** Delete-only-on-success ordering is naturally enforced by D-01 — a spawn exhaust throws before `return null`, so the delete never runs on a failed fan-out.

---

## PB-02 — Hermetic-fact fault seam

| Option | Description | Selected |
|--------|-------------|----------|
| Faulting sendProvider, assert RunAsync throws | Construct ProcessorPipeline (already a plain object) with a SendEndpointProvider that throws a transient fault to exhaust SpawnToPost's RetryLoop; assert RunAsync propagates the dedicated exception (= nack). No production test seam. | ✓ |
| Env-gated fault seam in SpawnToPost | A PROCESSOR_DEFEAT_SPAWN hook mirroring PROCESSOR_DEFEAT_READ. Rejected: ships a test-only branch in production code for something a constructor-level double already does. | |

**User's choice:** Faulting sendProvider at the constructor seam (recommended).
**Notes:** The injected fault must be classified transient by IsTransientSendFault (BaseProcessor.cs:123-137) so it hits the new throw branch.

---

## Deterministic-vs-transient nack-escape (surfaced by Claude, confirmed)

| Option | Description | Selected |
|--------|-------------|----------|
| Narrow filter — only the dedicated type nacks | Transient send-exhaust → dedicated exception → escapes → nack. Deterministic fault → raw exception → caught → StepFailed+ack (unchanged, poison-safe). | ✓ |

**User's choice:** Ready for context (accepted the narrow-filter watch-out as a locked decision D-03).
**Notes:** A too-broad catch-all filter would nack-loop deterministic poison messages forever. This satisfies "the deterministic-fault throw is preserved" (PB-01).

## Claude's Discretion

- Exact name/namespace of the dedicated spawn-exhaust exception (`SpawnSendExhaustedException` is a placeholder).
- `catch (…) { throw; }` before the generic catch vs a `when` clause — either, as long as it stays narrow.
- Wave breakdown across the three coupled PB changes.
- Whether to unify the Mode-1 and Mode-2 null-path deletes into one helper or share the inline block.

## Deferred Ideas

- KafkaImporter (KIMP-01..06), the standalone Kafka container, NextStepHandoff.Data widening (MANIP-01), KafkaExporter (KEXP-01) — future Kafka milestone; Phase 85 ships the fail-loud primitive they depend on.
- True exactly-once import dedup / (topic-partition-offset) idempotency key — future milestone; at-least-once is the model here.
