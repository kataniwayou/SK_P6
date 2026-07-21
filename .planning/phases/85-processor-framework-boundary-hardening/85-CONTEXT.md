# Phase 85: Processor↔Framework Boundary Hardening - Context

**Gathered:** 2026-07-21
**Status:** Ready for planning

<domain>
## Phase Boundary

Harden the processor↔framework boundary so a fan-out hand-off can **never silently drop a seeded execution**, and move **entry-lifecycle ownership fully into the base**. Three changes:

1. **PB-01 — fail-loud `SpawnToPost`:** it signals a transient send-exhaustion to the caller instead of swallowing it as silent success; `OnSpawnDropped` telemetry and the deterministic-fault throw are preserved.
2. **PB-02 — nack-on-spawn-exhaust:** the pipeline acks a Mode-2 entry dispatch ONLY when every spawn succeeded; a spawn send-exhaustion nack-requeues the whole entry (broker redelivery) rather than converting it to `StepFailed`+ack.
3. **PB-03 — framework-owned entry deletion:** entry/source deletion becomes framework-owned on every completion path (Mode-1 inline tail *and* Mode-2 null-return), keeper DELETE escalation preserved; the concrete `DeleteEntry` API, the `SampleProcessor` call, and the seam plumbing that existed solely for it (`SeamState.EntryId`, `EscalateDelete`) are removed.

**Net boundary:** the concrete processor's only job is *interpret input bytes → business logic → return a `DataResult`* (or, for a fan-out source, spawn N + return null). Config-deserialization, schema validation, L2 read/write, `Step*` send, entry deletion, and the ack/nack decision are all framework-owned.

**Framework-only + `Processor.Sample`.** No Kafka — `KafkaImporter`, the Kafka container, and `NextStepHandoff.Data` widening are deferred to a future Kafka milestone. Phase 85 establishes the fail-loud hand-off primitive (PB-01/02) that the future `KIMP-03` per-message commit will build on, but ships no Kafka.

**Requirements are locked** (REQUIREMENTS.md PB-01/02/03). This discussion clarifies HOW to implement, not WHAT.
</domain>

<decisions>
## Implementation Decisions

### D-01 — PB-01 signal shape: propagating exception (not a return value)
`SpawnToPost` fires the `OnSpawnDropped` telemetry hook, then **throws a dedicated exception type** (e.g. `SpawnSendExhaustedException`) on a transient send-exhaustion. It propagates transparently through the author's `ProcessAsync` (the author writes NO catch) up to the pipeline's ack/nack decision.
- **Why not a return value:** the fan-out author (`SampleProcessor`) returns `null` after spawning; a `bool`/enum return would force the author to inspect it and thread ack/nack awareness back up — pushing framework control-flow into the concrete processor and breaking the "business logic only" boundary principle this phase exists to enforce.
- Mirrors the existing WR-01 deterministic-fault throw at `BaseProcessor.cs:115`; only the transient-exhaust branch (`BaseProcessor.cs:116`, the `OnSpawnDropped?.Invoke` swallow) changes from swallow→telemetry-then-throw.

### D-02 — PB-02 nack semantics: bare RabbitMQ nack-requeue, whole-entry re-fire
The pipeline catch-all (`ProcessorPipeline.cs:185`) must **rethrow the dedicated spawn-exhaust exception** (filter via a `when` clause or a preceding narrow catch) so it escapes to MassTransit → **default RabbitMQ nack-requeue**. No new redelivery/backoff config.
- Reuses the *exact* mechanism the keeper-send-exhaust path already uses (`ProcessorPipeline.cs:304` — "propagate → throw → broker redelivery, no `_error`"; class doc lines 42-45).
- **Re-fire semantics (accepted):** redelivery re-fires the WHOLE seed → already-succeeded spawns duplicate. This is at-least-once by design — exactly-once dedup is explicitly out of scope (REQUIREMENTS.md "Out of Scope"; PB-02's nack minimizes but does not eliminate the crash-between-handoff-and-commit duplicate window).
- **Rejected:** a redelivery delay/bound on the entry endpoint (hot-loop guard) — diverges from the bare-nack pattern used everywhere else and adds endpoint config for a concern the rest of the system already tolerates.

### D-03 — PB-01/02 poison-message safety: nack-escape targets ONLY the dedicated exception (LOCKED WATCH-OUT)
The catch-all's rethrow/`when` filter must be **narrow to the dedicated transient-exhaust exception type**. Deterministic faults (raw `ArgumentException`/`NotSupportedException`/`JsonException`, etc.) continue to be caught → `StepFailed`+ack, exactly as today.
- **Why this matters:** a deterministic serialization fault re-fires identically on every redelivery → if it nacked it would poison-loop forever. Keeping deterministic faults on the ack path is correct poison-message safety AND satisfies "the deterministic-fault throw is preserved" (PB-01).
- Design invariant: **transient send-exhaust → dedicated exception → escapes → nack** vs **deterministic fault → raw exception → caught → StepFailed+ack**. The planner must NOT widen the catch-all filter to all exceptions.

### D-04 — PB-03 delete placement: reuse the Mode-1 tail's delete-then-escalate on the null-return path
On the Mode-2 null-return branch (`ProcessorPipeline.cs:206`), run the **same** `delete L2[entryId] → escalate to KeeperDelete on exhaust` logic the Mode-1 inline tail already uses (`ProcessorPipeline.cs:222-227`), executed AFTER the (already-succeeded) spawns.
- Preserve the `SourceStep.IsSource(d.EntryId)` skip so the net delete effect matches today exactly (the Mode-2 entry/seed is a `Guid.Empty` source — the current `SampleProcessor.DeleteEntry()` deletes `L2[ExecutionData(Guid.Empty)]`, a harmless no-op; framework-owned deletion must reproduce that net effect, not change it).
- **Ordering is naturally enforced by D-01:** if any spawn exhausts, `SpawnToPost` throws → propagates out of `ProcessAsync` → never reaches `return null` → pipeline nacks → **no delete**. The entry is only deleted once the author returned null, i.e. all spawns succeeded.
- Remove: the concrete `DeleteEntry` API (`BaseProcessor.cs:142-148`), the `SampleProcessor` call (`SampleProcessor.cs:72`), `SeamState.EntryId` (`BaseProcessor.cs:54`), and the `EscalateDelete` hook (`BaseProcessor.cs:65`, wired at `ProcessorPipeline.cs:286`). Entry deletion still happens — via the base.
- **Rejected:** a separate dedicated null-path delete helper — duplicates the delete-then-escalate logic in two places for no benefit.

### D-05 — PB-02 hermetic fact: faulting `sendProvider` at the constructor seam (no production test hook)
The "defeated spawn nack-requeues rather than acks" hermetic fact constructs `ProcessorPipeline` directly (it is already a plain constructable object — class doc line 20) with a `ISendEndpointProvider` that throws a transient fault, exhausting `SpawnToPost`'s `RetryLoop`, then asserts `RunAsync` **propagates the dedicated exception** (= nack) rather than returning after emitting a `StepFailed` (= ack).
- **Rejected:** an env-gated `PROCESSOR_DEFEAT_SPAWN` seam inside `SpawnToPost` (mirroring `PROCESSOR_DEFEAT_READ` / `PROCESSOR_STEP_DELAY_MS`). It ships a test-only branch in production code for something a constructor-level test double already achieves cleanly.
- The fault must be classified TRANSIENT by `IsTransientSendFault` (`BaseProcessor.cs:123-137`) so it hits the new throw branch (a matching family: `TimeoutException`, `BrokerUnreachableException`, etc.).

### Claude's Discretion
- Exact name and namespace of the dedicated spawn-exhaust exception type (`SpawnSendExhaustedException` is a placeholder — planner/researcher choose; likely in `BaseProcessor.Core` alongside `ProcessStatusException`).
- Whether the catch-all filter is a `catch (SpawnSendExhaustedException) { throw; }` before the generic catch vs a `when` clause on the generic catch — either satisfies D-03 as long as it is narrow.
- Exact wave breakdown across the three PB changes (they are tightly coupled: PB-01 signal → PB-02 nack → PB-03 delete-on-success; D-01's throw is the prerequisite that makes D-04's delete-only-on-null ordering correct).
- Whether to unify the Mode-1 tail delete and the new Mode-2 null-path delete into one private helper method or inline the shared block (as long as D-04's reuse intent holds).
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements (locked)
- `.planning/REQUIREMENTS.md` — PB-01/PB-02/PB-03 (this phase) + the "Boundary principle" note + "Out of Scope" (at-least-once is the model; exactly-once dedup rejected; Kafka deferred). MUST read before planning.
- `.planning/ROADMAP.md` §"Phase 85: Processor↔Framework Boundary Hardening" (lines 52-61) — goal, depends-on, 4 success criteria (SC-4 = the sweep must still pass).

### The three touch-points (framework contract)
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` — **PB-01**: `SpawnToPost` (lines 92-117, the swallow at :116 → throw), `IsTransientSendFault` (:123-137); **PB-03 removals**: `DeleteEntry` (:142-148), `SeamState.EntryId` (:54), `SeamState.EscalateDelete` (:65), and the `SetSeamState` params that feed them (:182-204).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — **PB-02**: the seam-invoke try/catch (:154-200), the catch-all at :185 (must narrowly rethrow the dedicated type), the null-return branch (:206), and the keeper-send-exhaust-propagates reference pattern (`SendKeeper` :293-305); **PB-03**: the Mode-1 delete-then-escalate tail (:222-227) to reuse on the null path, and the `SetSeamState` closure wiring `onSpawnDropped`/`escalateDelete` (:267-287).
- `src/Processor.Sample/SampleProcessor.cs` — **PB-01/03**: the Mode-2 entry path (:57-74) — `SpawnToPost` loop (:70) now propagates on exhaust, the `this.DeleteEntry()` call (:72) is removed; update the class doc-comment (:16-19, :27-28) that describes SpawnToPost as "swallow" and the author-owned `DeleteEntry`.
- `src/BaseProcessor.Core/Processing/OutputTail.cs` — the shared terminal write/send/INJECT tail (context for how the Mode-1 tail flows before the delete; the delete itself lives in `ProcessorPipeline`, not here).
- `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs` §`SpawnDropped` (:48) — the `OnSpawnDropped` counter that PB-01 preserves (telemetry fires BEFORE the new throw).

### Recovery / keeper (deletion escalation — preserved)
- `src/Keeper/Recovery/DeleteConsumer.cs` + `src/Messaging.Contracts/KeeperDelete.cs` — the delete-only keeper state the escalation targets; framework-owned deletion still escalates here on exhaust (PB-03 "keeper DELETE escalation preserved").

### Verification
- `scripts/phase-68-sweep.ps1` — the 7-scenario fault-recovery sweep (TEST-01..07); SC-4 gate = reproduce the all-PASS baseline (`Missing==0`, `Duplicates==0`) with `Processor.Sample`, alongside the new PB-02 hermetic fact. See memory [[long-sweep-detached-process]] (run detached), [[rebuild-sourcehash-reseed-order]] (reseed-first runbook), [[phase-68-sweep-stale-report-read]] (trust the harness exit code).
- Hermetic test command: run `BaseApi.Tests.exe` directly (`dotnet test` hangs on Windows MTP); `--filter-not-trait Category=RealStack`. See memory [[hermetic-test-command]].

### Prior-phase context (the channel this boundary sits on)
- `.planning/phases/84-byte-data-channel-widening/84-CONTEXT.md` — the shipped `byte[]` channel; `SpawnToPost` already carries `DataResult{ byte[] Data }`, so PB-01/02/03 change control-flow only, never the payload shape.
</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **The keeper-send-exhaust nack pattern (`ProcessorPipeline.SendKeeper`, :293-305):** `if (!sent.Succeeded) throw sent.Error!` → bare RabbitMQ nack-requeue. PB-02's dedicated-exception escape is the *same* mechanism — the design is already proven for keeper sends; PB-02 extends it to spawn sends.
- **The Mode-1 delete-then-escalate tail (`ProcessorPipeline.cs:222-227`):** `RetryLoop(KeyDelete) → SendKeeper(BuildDelete) on exhaust`, with the `SourceStep.IsSource` skip. D-04 reuses this verbatim on the null path.
- **`ProcessStatusException` family + the catch at `:158`:** the precedent for a framework-recognized exception that the pipeline handles specially rather than treating as an unexpected fault. The dedicated spawn-exhaust exception follows this shape — but escapes (nack) instead of being routed to `OutputTail`.
- **`ProcessorPipeline` is a plain constructable object (class doc :20)** — the D-05 hermetic fact builds it directly with a faulting `sendProvider`; no MassTransit harness, no env seam.

### Established Patterns
- **`AsyncLocal<SeamState>` per-consume isolation (CR-01, `BaseProcessor.cs:43`):** removing `EntryId`/`EscalateDelete` from `SeamState` shrinks the per-dispatch state but does not change the isolation model — `MessageId`/`WorkflowId`/`StepId`/`CorrelationId`/`ProcessorId` stay (used by `NewResult`).
- **Never log payloads (FW-03/T-70-10):** the new throw path and any new telemetry must carry ids only, never `Payload`/`Data` — matches the existing `OnSpawnDropped` warn (`ProcessorPipeline.cs:280`, ExecutionId only).
- **`RetryLoop.ExecuteAsync` returns `RetryOutcome` (`.Succeeded`/`.Error`)** — `SpawnToPost` already consumes this (`:96-108`); PB-01 just replaces the `OnSpawnDropped?.Invoke` swallow at :116 with telemetry-then-throw.

### Integration Points
- **The catch-all at `ProcessorPipeline.cs:185` is the single ack/nack fulcrum** for the seam. Every seam outcome funnels through the `try` at :154-200: `ProcessStatusException` → OutputTail+ack; the dedicated spawn-exhaust → **rethrow → nack**; every other `Exception` → StepFailed+ack. Getting the filter narrow (D-03) is the crux of the phase.
- **`SetSeamState` (`ProcessorPipeline.cs:267-287`) wires `escalateDelete` as a closure over `SendKeeper(BuildDelete(d))`** — when `DeleteEntry`/`EscalateDelete` are removed (PB-03), that escalation moves inline into the pipeline's null-path delete (it already has `SendKeeper`/`BuildDelete` in scope), so no capability is lost.

### Watch-outs (for the planner)
- **D-03 is the highest-risk item:** a too-broad catch-all filter would nack-loop deterministic poison messages forever. The filter MUST be narrow to the dedicated type. Add a hermetic fact for the negative case too (a deterministic fault still acks as StepFailed) if cheap.
- **The Mode-2 entry is a `Guid.Empty` source** — the current `DeleteEntry()` deletes `L2[ExecutionData(Guid.Empty)]` (no-op). The framework null-path delete under `SourceStep.IsSource` will *skip* it. Confirm net effect is identical (today's no-op delete vs. tomorrow's skip both leave nothing) so no behavioral change leaks into the sweep. If a scheduler-seeded entry ever carries a real (non-empty) `entryId`, the framework delete must fire — verify against the actual dispatch `EntryId` the scheduler sets.
- **Record value-equality:** unrelated to this phase (no `DataResult.Data` comparison changes), but the same `byte[]` reference-equality caveat from Phase 84 (D-02 helpers) applies to any new test assertions.
</code_context>

<specifics>
## Specific Ideas

- The animating principle (from REQUIREMENTS.md, restated): after this phase the concrete processor is *interpret input bytes → business logic → return `DataResult`* (or spawn N + return null). Anything else — ack/nack, deletion, escalation — is a framework concern. Every removal (`DeleteEntry`, `EntryId`, `EscalateDelete`) is in service of shrinking the author surface to exactly that.
- PB-01/02 deliberately build the fail-loud hand-off primitive that a future `KafkaImporter` (`KIMP-03`, per-message Kafka offset commit) will reuse — the exception-propagates-to-nack shape is the same shape a per-message commit needs (commit only on confirmed store+handoff). Keep the primitive general, not Kafka-specific.
</specifics>

<deferred>
## Deferred Ideas

- **`KafkaImporter` (KIMP-01..06), the standalone Kafka container, `NextStepHandoff.Data` byte[] widening (MANIP-01), `KafkaExporter` (KEXP-01)** — future Kafka milestone. Phase 85 ships the fail-loud primitive they depend on, but no Kafka. Recorded in REQUIREMENTS.md "Future Requirements" — not scope creep, the known forward path.
- **True exactly-once import dedup / idempotency key from (topic-partition-offset)** — future milestone; at-least-once is the accepted model here.

None of the above arose as in-discussion scope creep — they are the documented forward path in REQUIREMENTS.md.
</deferred>

---

*Phase: 85-processor-framework-boundary-hardening*
*Context gathered: 2026-07-21*
