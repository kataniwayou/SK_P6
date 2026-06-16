# Phase 70: Two-consumer processor design (Pre-Process + Post-Process) with keeper recovery - Context

**Gathered:** 2026-06-16
**Status:** Ready for planning

<domain>
## Phase Boundary

Replace the single-consumer `ProcessorPipeline` (gates on the `L2[messageId]` slot-array HASH, framework owns Post) with the pinned **two-consumer** design:

- A **Pre-Process** consumer gating on `L2[entryId]`, whose `virtualProcess` seam returns one `<data result>` (downstream) **or** spawns N to a new **Post-Process** consumer and returns null (entry).
- A new **Post-Process** consumer (`IConsumer<DataResult>`) running only the output tail.
- Output keyed by `messageId`, written **only** when `result == completed`.
- Keeper states `REINJECT` / `INJECT` / `DELETE` redefined per the SPEC pseudocode.

This is a HOW-to-implement discussion. WHAT to build is locked by `70-SPEC.md` (12 requirements, ambiguity 0.14). New capabilities belong in other phases.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**12 requirements are locked.** See `70-SPEC.md` for full requirements, boundaries, constraints, and acceptance criteria. The SPEC's pinned pseudocode is the **sole source of truth** (it supersedes the slot-array model).

Downstream agents MUST read `70-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- Pre-Process consumer: gate `L2[entryId]`, read, input-validate, deserialize, `virtualProcess`, inline output tail (write `L2[messageId]` on completed, send by result, delete `L2[entryId]`).
- Post-Process consumer (new) + `<data result>` message contract: output-only tail, no `entryId`.
- Redefined keeper states `REINJECT` / `INJECT` / `DELETE` per the pseudocode.
- `BaseProcessor` seam change: `virtualProcess` returns one `<data result>` or null; helpers `SpawnToPost` and `DeleteEntry`.
- Sample two-mode `virtualProcess` (label/number; spawn-2-on-empty / accumulate-1-on-non-empty; `"{label} had the following numbers: …"` log).
- Output keyed by `messageId`; write gated on `result == completed`.
- Replace the existing `ProcessorPipeline` slot-array/inline model in-place; migrate the ~30 Phase-50–68 processor tests; mark `docs/design/processor-keeper-recovery-spec.md` superseded.

**Out of scope (from SPEC.md):**
- Orchestrator design and the downstream `entryId` ↔ `messageId` threading (the "#2 output-keying" decision) — deferred to the orchestrator's own phase.
- Mode-2 partial-spawn reconciliation in the orchestrator (best-effort spawn; orchestrator does not track N; scheduler re-fires) — only the processor-side swallow + scheduler-re-trigger assumption is in scope.
- Building or deploying the container image — code + `dotnet test` only this phase.
- Changing the four result contracts or the TTL policy — reuse current implementation.
- Cleaning up `L2[entryId]` on input-validation/deserialize-failure paths beyond its TTL — left to TTL.

</spec_lock>

<decisions>
## Implementation Decisions

### `<data result>` contract & seam (Area 1)
- **D-01:** **One unified `DataResult` record** lives in `Messaging.Contracts`, serving as BOTH the `ProcessAsync` seam return type AND the Post-Process wire contract (`IConsumer<DataResult>`). The spawn path sends the exact object the author returned. Must be serializable.
- **D-02:** `DataResult` is **self-contained**: it carries `data` (string JSON), `result` (the 4-type outcome), `executionId`, the correlation ids (`correlationId` / `workflowId` / `stepId` / `processorId`), and the carried `messageId`. Post and `INJECT` need nothing from elsewhere — satisfies "INJECT self-contained / never recomputes from input" (req 7).
- **D-03:** Type named **`DataResult`** (matches the SPEC's `<data result>` vocabulary). `ProcessItem` (the retired list-seam type) is removed.
- **D-04:** The `result` field reuses the existing **4-value `StepOutcome`** (`Processing` / `Completed` / `Failed` / `Cancelled`) — the same enum that already maps to the four `Step*` result contracts. No new outcome enum.
- **D-05:** The author seam **keeps the name `ProcessAsync`**; only the return type changes: `Task<List<ProcessItem>>` → `Task<DataResult?>` (one-or-null). `virtualProcess` in the SPEC is pseudocode, not a required identifier.

### Spawn/Delete helpers & swallow semantics (Area 2)
- **D-06:** `SpawnToPost(DataResult, Guid executionId)` and `DeleteEntry()` are exposed as **protected methods on `BaseProcessor`** (author calls `this.SpawnToPost(...)` / `this.DeleteEntry()`). The framework wires the send-provider, `RetryLoop`, and keeper-escalation behind them. No new parameter on the seam signature.
- **D-07:** `SpawnToPost` runs the **bounded `RetryLoop`, then SWALLOWS (drops)** the spawn on exhaustion — no throw, no keeper. The scheduler re-fires the whole entry later (Mode-2 is best-effort; orchestrator does not track N). This resolves the req-9-vs-pseudocode tension: "escalate per spec" = swallow for spawn.
- **D-08:** `DeleteEntry()` runs the **bounded `RetryLoop`, then escalates to the redefined `DELETE` keeper** state (delete-only, no orchestrator send) on exhaustion — exactly as the framework-owned inline-tail delete does. The author writes no `RetryLoop`/keeper code.
- **D-09:** The **author mints** the N distinct `executionId`s and passes each to `SpawnToPost(result, newExecutionId)` (matches the helper signature and req 10). Author owns spawn policy; framework owns resilience.

### L2 output keying & keeper-contract reshape (Area 3)
- **D-10:** Add a dedicated **`L2ProjectionKeys.OutputData(messageId)` → `skp:out:{messageId:D}`** for `write L2[messageId]=data`. Distinct `out` namespace unambiguously separates output blobs (by `messageId`) from input blobs (`skp:data:` by `entryId`). Carries the jittered `messageId` TTL (`random[ExecutionDataTtl, 2×ExecutionDataTtl]`).
- **D-11:** **Delete the slot-array HASH builder `MessageIndex(messageId)` + the `SlotTtl` helper** (the slot model is fully retired — no slots, no recovery pass). **Keep `ExecutionData(entryId)` = `skp:data:{entryId:D}`** (Pre reads `L2[entryId]`; `DELETE`/`INJECT` delete it). Add `OutputData`. One clean `L2ProjectionKeys` source-of-truth update.
- **D-12:** **Reshape the three keeper contracts in-place** (`KeeperReinject` / `KeeperInject` / `KeeperDelete`). Reuses the keeper-recovery queue, partitioner (4-tuple partition key), and `RecoveryConsumerBase`. No parallel model kept.
  - `KeeperInject`: carries the whole `DataResult` + the source `entryId` to delete (writes `L2[messageId]=data`, deletes `L2[entryId]`, sends the result).
  - `KeeperDelete`: **delete-only** — `entryId` only; drop the two-key `MessageId` field/role; no orchestrator send.
  - `KeeperReinject`: stays (read `L2[entryId]`, re-inject the original dispatch with payload, envelope `MessageId` overridden to the carried `messageId`; clean-absent `entryId` drops).
- **D-13:** `KeeperInject` **embeds the whole `DataResult` object** (not flattened fields). Since `DataResult` is already the self-contained wire type (D-01/D-02), `INJECT` reuses it verbatim — "never reads input to recompute" (req 7) falls out naturally, and there's no field-drift between two shapes.

### Code & test structure (Area 4)
- **D-14:** **Rewrite `ProcessorPipeline` in-place** as the Pre-Process flow (gate `L2[entryId]` → read → input-validate → deserialize → `ProcessAsync` → null-return early-exit → inline tail). `EntryStepDispatchConsumer` stays the thin shell delegating to it. The recovery-pass code is **deleted** (no slot array, no redelivery recovery pass). Keeps the consumer/pipeline split the current tests rely on.
- **D-15:** Add a new **`PostProcessConsumer : IConsumer<DataResult>`** in `BaseProcessor.Core`. Factor the output tail (validate output → conditional `write L2[messageId]` on completed, with `INJECT` escalation → send by result) into **one shared helper** (e.g. `OutputTail`) used by BOTH `PostProcessConsumer` and Pre's inline tail — single source of truth for the write-gated-on-completed logic. Pre's tail additionally deletes `L2[entryId]`; **Post never touches `entryId`**.
- **D-16:** Post-Process runs in the **same processor console process** (`BaseProcessor.Core`), bound at runtime like the entry endpoint, on a **distinct per-processor queue `queue:{processorId:D}-post`**. Same endpoint policy as entry: `UseMessageRetry` none, error routing off, send-exhaust → throw → broker redelivery; `ExcludeFromConfigureEndpoints` + bind after identity resolves. `SpawnToPost` sends here with the carried `messageId` overriding the outbound envelope `MessageId`.
- **D-17:** **Test migration:** delete tests with no analog in the new model (`PipelineRecoveryFacts`, slot-allocation bits of `PipelinePostFacts`, `RecoveryPartitionFacts` slot specifics). Rewrite the Pre/Post/keeper facts to the new model (new `PrePipelineFacts` + `PostProcessConsumerFacts`). Reuse the hermetic doubles (`DispatchTestKit` / `FakeRedis` / `CapturingSendProvider`). Suite stays hermetic; **`dotnet test` green is the req-12 bar**.

### Claude's Discretion
- Exact C# property names / nullability on `DataResult` (keep consistent with existing `Step*` records and `ProcessorConfig.SerializerOptions`).
- How `result` maps to the four `Step*` send contracts in the `OutputTail` helper (mechanical switch on `StepOutcome`).
- Input-validation / deserialize-failure → exactly-one `StepFailed` wiring (per req 2; no `entryId` delete on that path — left to TTL).
- The superseded-doc marker mechanism for `docs/design/processor-keeper-recovery-spec.md` (a "superseded by Phase 70" header marker vs replacement with the new design — req 12 allows either; default to a marker pointing at `70-SPEC.md`).
- Naming of the shared `OutputTail` helper and the new test files.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Source of truth (locked requirements + pinned pseudocode)
- `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-SPEC.md` — **Locked requirements — MUST read before planning.** 12 requirements, the pinned two-consumer pseudocode (Pre/Post/KEEPER), boundaries, constraints, acceptance criteria. The pseudocode supersedes the slot-array model.

### Superseded design doc (to be marked superseded — req 12)
- `docs/design/processor-keeper-recovery-spec.md` — the now-**SUPERSEDED** slot-array Pre/In/Post-Process + recovery-pass model (gate `L2[messageId]` HASH, atomic slot writes, recovery pass). Phase 70 replaces this. Mark it superseded-by-Phase-70 (D-17 discretion on the exact marker).

### Code being replaced / modified (paths from codebase scout)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — rewritten in-place as the Pre flow (D-14); recovery-pass + slot tail deleted.
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs` — thin shell, unchanged role; fail-fast on null `MessageId`.
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` + `BaseProcessor`1.cs` — seam return → `Task<DataResult?>`; add protected `SpawnToPost` / `DeleteEntry` (D-05/D-06).
- `src/BaseProcessor.Core/Processing/ProcessItem.cs` + `ProcessOutcome.cs` — `ProcessItem` retired (D-03); `result` reuses `StepOutcome` (D-04).
- `src/Processor.Sample/SampleProcessor.cs` + `SampleConfig.cs` — two-mode `virtualProcess` (req 10): empty `executionId` → 2 spawns to Post + return null; non-empty → 1 completed `DataResult`; `"{label} had the following numbers: …"` log.
- `src/Keeper/Recovery/ReinjectConsumer.cs` / `InjectConsumer.cs` / `DeleteConsumer.cs` + `RecoveryConsumerBase.cs` / `RecoveryEndpointBinder.cs` / `ReinjectConsumerDefinition.cs` — keeper redefinitions (D-12/D-13).
- `src/Messaging.Contracts/KeeperReinject.cs` / `KeeperInject.cs` / `KeeperDelete.cs` + `IKeeperRecoverable.cs` — reshaped in-place (D-12); new `DataResult.cs` contract (D-01).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — add `OutputData(messageId)`, delete `MessageIndex`/`SlotTtl`, keep `ExecutionData` (D-10/D-11).
- `src/Messaging.Contracts/StepCompleted.cs` / `StepFailed.cs` / `StepCancelled.cs` / `StepProcessing.cs` + `StepOutcome.cs` — the four result contracts (reused, not changed); `StepOutcome` is the `DataResult.result` enum.
- `src/BaseConsole.Core/Resilience/RetryLoop.cs` — the bounded L2-op/send retry helper (`Retry:Limit`); framework owns it behind the helpers.
- `tests/BaseApi.Tests/Processor/` + `tests/BaseApi.Tests/Keeper/` — ~30 hermetic xUnit facts; migrate per D-17 (`DispatchTestKit` / `FakeRedis` / `CapturingSendProvider` doubles reused).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`RetryLoop.ExecuteAsync<T>(op, limit, ct)`** (`BaseConsole.Core/Resilience`) — returns `RetryOutcome<T>(Succeeded, Value, Error)`; caller routes on exhaustion (read→REINJECT, write→INJECT, delete→DELETE, send→throw). Drives all the bounded-retry-then-keeper helpers.
- **`RecoveryConsumerBase` + `Guard<T>()`** (`Keeper/Recovery`) — shared keeper RetryLoop wrapper + partitioned endpoint; reuse for the reshaped REINJECT/INJECT/DELETE.
- **`RecoveryEndpointBinder`** — runtime `ConnectReceiveEndpoint(KeeperQueues.Recovery, …)` with `UsePartitioner` on the 4-tuple; the Post endpoint follows the same runtime-bind + `ExcludeFromConfigureEndpoints` pattern.
- **`L2ProjectionKeys`** — single source-of-truth for Redis key formats (`const Prefix="skp:"`); add `OutputData` here.
- **`DispatchTestKit` / `FakeRedis` / `CapturingSendProvider`** — hermetic test doubles; reuse for the new Pre/Post/keeper facts.
- **`StepOutcome` (4 values) + `IStepResult` + the four `Step*` records** — reuse verbatim as `DataResult.result` and the orchestrator-bound sends.

### Established Patterns
- **Endpoint policy (entry path):** durable `queue:{processorId:D}`, `UseMessageRetry` none, no error/dead-letter transport, in-code `RetryLoop` only, send-exhaust → throw → RabbitMQ nack-requeue. The new `queue:{processorId:D}-post` mirrors this exactly.
- **Envelope `MessageId` override:** spawn sends + keeper re-injections override the outbound `MessageId` with the carried `messageId`; **no** MassTransit inbox/`MessageId` dedup on these endpoints.
- **`ExcludeFromConfigureEndpoints` + runtime bind after identity resolves, before `MarkHealthy`** — how processor consumers attach their durable per-processor queue.
- **Consumer = thin shell → pipeline class** — `EntryStepDispatchConsumer` delegates to `ProcessorPipeline`; keep this split for unit-testability.

### Integration Points
- `SpawnToPost` → new `queue:{processorId:D}-post` (same process, carried-`messageId` envelope override).
- Pre inline tail + `PostProcessConsumer` → shared `OutputTail` helper → `queue:{OrchestratorQueues.Result}` (the four `Step*` sends).
- Keeper escalations (`REINJECT`/`INJECT`/`DELETE`) → `KeeperQueues.Recovery` (partitioned, gate-open-only).
- Pre reads/deletes `L2[entryId]` (`skp:data:`); Pre/Post write `L2[messageId]` (`skp:out:`); both via `RetryLoop` → keeper on exhaustion.

</code_context>

<specifics>
## Specific Ideas

- The user accepted every recommended option across all four areas — the design converges on the "fewest-moving-parts, faithful-to-SPEC, replace-in-place" path.
- `DataResult` is deliberately the single spine: seam return ⇒ Post wire message ⇒ `INJECT` body. This is what makes "INJECT self-contained / never recomputes" (req 7) and the spawn handoff fall out without mapping code.
- `SpawnToPost` swallow (D-07) vs `DeleteEntry` escalate (D-08) is the one asymmetry to honour precisely: spawn loss is recovered by the scheduler re-fire; entry-delete loss is recovered by the keeper.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (Orchestrator-side `entryId`↔`messageId` threading and Mode-2 N-reconciliation are already SPEC-declared out of scope, owned by the orchestrator's own phase.)

</deferred>

---

*Phase: 70-two-consumer-processor-design-pre-process-post-process-with-*
*Context gathered: 2026-06-16*
