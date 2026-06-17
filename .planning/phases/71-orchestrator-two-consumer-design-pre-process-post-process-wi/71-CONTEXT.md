# Phase 71: Orchestrator two-consumer design (Pre-Process + Post-Process) with keeper recovery - Context

**Gathered:** 2026-06-17
**Status:** Ready for planning

<domain>
## Phase Boundary

Reshape the orchestrator's result-advancement path into the Phase-70 two-consumer pattern + runtime data routing, and close the Phase-70-deferred `entryId`↔`messageId` threading (A1):

- A **Pre-Process** side (the existing 4 typed result consumers) gates on `L2[out:entryId]`, reads the upstream output, resolves next steps from L1 by entry-condition match, fans out one **Post-Process** message per matching next step, then deletes `L2[out:entryId]`.
- A new **Post-Process** consumer writes the next step's input `data` to `L2[data:messageId]` (TTL'd) and dispatches `EntryStepDispatch` to the processor with `entryId = messageId`.
- Keeper states `REINJECT`/`INJECT`/`DELETE` redefined (orchestrator-side) with envelope-`MessageId` override.
- **A1 close:** the processor result stamps `EntryId = its output messageId`.

This is a HOW-to-implement discussion. WHAT to build is locked by `71-SPEC.md` (12 requirements, ambiguity 0.16). New capabilities belong in other phases. Code + `dotnet test` only.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**12 requirements are locked.** See `71-SPEC.md` for full requirements, boundaries, constraints, and acceptance criteria.

Downstream agents MUST read `71-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- Orchestrator **Pre-Process**: gate/read `L2[out:entryId]`, resolve next steps by entry-condition match, fan out one Post message per match (payload + relocated data), delete `L2[out:entryId]`.
- Orchestrator **Post-Process** (new) + its fan-out message contract: write `L2[data:messageId]` (TTL'd) on the Completed path, dispatch `EntryStepDispatch` with `entryId = messageId`.
- Trip-end completion semantics: structured log + orchestration-completed metric with `completed-terminal` / `completed-unresolved` reasons (no-silent-loss by construction).
- Orchestrator-side keeper states `REINJECT`/`INJECT`/`DELETE` (envelope-`MessageId` override; `REINJECT` clean-absent drop).
- **A1 close:** processor `OutputTail.BuildStep` stamps `Completed` `EntryId = output messageId`.
- Namespace cross-pairing: orchestrator reads `out:`, writes `data:`.
- `executionId` threaded unchanged across the hop.
- Migrate the orchestrator advancement/dispatch tests (+ the A1-affected processor test).

**Out of scope (from SPEC.md):**
- Fan-in / merge / AND-join — processor is stateless; OR-join / fire-per-edge stands. Future phase.
- Parking/throwing/keeper-escalating on next-step *resolution* failure — resolution failure is always a clean trip-completion with a proper message; keeper/throw only for infra (L2-op exhaustion, send fault).
- Changing the four step-result contracts, entry-condition semantics, or the L1 hydration model.
- Container build / deploy — code + `dotnet test` only.
- Relocating data for non-completed outcomes (no `out:` blob exists).

</spec_lock>

<decisions>
## Implementation Decisions

### Area 1 — Keeper recovery realization
- **D-01:** **New orchestrator-specific keeper contracts** (e.g. `OrchestratorReinject` / `OrchestratorInject` / `OrchestratorDelete`) rather than reusing/overloading the processor's `KeeperReinject`/`KeeperInject`/`KeeperDelete`. The orchestrator's keeper ops do DIFFERENT work: orchestrator `REINJECT` re-injects a **Step-result** to the orchestrator Pre (not a dispatch to a processor); orchestrator `INJECT` writes `L2[data:messageId]` + dispatches `EntryStepDispatch`; orchestrator `DELETE` deletes the `out:` blob. Separate contracts keep each side's keeper body self-contained and avoid dual-meaning fields.
- **D-02:** Each orchestrator keeper contract is **self-contained**, carrying exactly what its recovery needs: `REINJECT` carries the original Step-result (+ `messageId`); `INJECT` carries the next-step target + relocated data (the `NextStepHandoff` payload) + `messageId`; `DELETE` carries the `out:` `entryId`.
- **D-03:** **`messageId` is carried IN-BODY on the keeper escalation contracts** (unlike the live Pre→Post handoff where it rides the envelope). The Keeper needs it to **override the outbound envelope `MessageId`** when it re-injects the result / dispatches the `EntryStepDispatch`, preserving the same id through recovery. This is the same processor→keeper→processor behavior as Phase 70 (`KeeperReinject`/`KeeperInject` carry `messageId`; envelope override on re-injection — see `ReinjectConsumer`).
- **D-04:** Orchestrator keeper recovery runs in the **central Keeper service** via **new recovery consumers**, sharing `RecoveryConsumerBase` + `Guard` + the partitioned, gate-open-only recovery queue. Symmetric with the processor; the BIT health gate governs orchestrator recovery too. (NOT in-process in the orchestrator.)
- **D-05:** `REINJECT` clean-absent drop is preserved (mirror `ReinjectConsumer`): an absent/empty `out:entryId` (STRLEN == 0, no Redis exception) is a by-design silent ack-drop with a counted structured warning; a Redis exception on the read is infra → `Guard`/exhaustion policy.

### Area 2 — Pre/Post consumer + queue structure
- **D-06:** **Keep the 4 typed result consumers** (`StepCompletedConsumer`/`StepFailedConsumer`/`StepCancelledConsumer`/`StepProcessingConsumer`) on the shared `orchestrator-result` queue as the **Pre-Process** front — each still supplies its compile-time `Outcome` knob (no status if/switch; preserves D-07 from the v4 work and the existing typed-consumer tests). They become **thin shells delegating to a shared Orchestrator Pre-pipeline** (gate/read `out:` → resolve via `StepAdvancement.SelectNext` → fan-out per match → delete `out:`). Mirrors Phase-70 D-14 (thin consumer → pipeline class).
- **D-07:** **New Orchestrator Post-Process consumer** on a **distinct `orchestrator-result-post` queue** (same endpoint policy as the result queue: `UseMessageRetry` none, error routing off, send-exhaust → throw → broker redelivery; runtime-bind pattern). Mirrors Phase-70 D-16.
- **D-08:** **Shared relocate-tail helper** (analog of Phase-70 `OutputTail`) implementing "write `L2[data:messageId]` (TTL'd, Completed path) → dispatch `EntryStepDispatch`", used by BOTH the Post consumer AND the orchestrator keeper `INJECT` path — single source of truth, avoids the cross-assembly desync D-15 was created to prevent.
- **D-09:** `StepProcessing` remains **non-advancing** (no fan-out) as today.

### Area 3 — Fan-out (Pre→Post) message contract
- **D-10:** **New self-contained record** in `Messaging.Contracts` (e.g. `NextStepHandoff`) for the Pre→Post handoff — analogous to the `DataResult` spine but shaped for the **next-step target**. Carries `{ WorkflowId, next StepId, next ProcessorId, next Payload, relocated Data (string JSON), CorrelationId, ExecutionId }`. NOT a reuse of `DataResult` (wrong shape — `DataResult` carries the *completed* step's ids + `Result`).
- **D-11:** **`messageId` is the MassTransit envelope `MessageId`, NOT a body field on the live handoff.** Each Pre `Send` gets a fresh envelope `MessageId` from MassTransit — that IS the next step's `messageId`. The Post consumer reads `context.MessageId` and uses it as both the `L2[data:messageId]` key and the dispatched `entryId`. (Per the user correction: `messageId` only appears in a body where a message must re-assert an id across a hop — i.e. the keeper contracts, D-03.)
- **D-12:** **Relocated data rides INLINE** as a string-JSON field; Post writes it verbatim to `L2[data:context.MessageId]`. NOT carried by reference (a by-reference re-read in Post would add a second L2 read + race Pre's end-delete — an ordering hole).
- **D-13:** `ExecutionId` is threaded **UNCHANGED** from the inbound result (SPEC req 11). A **non-completed** continuation carries **empty `Data`** (no `out:` blob exists) and dispatches with `entryId = Guid.Empty`.

### Area 4 — Orchestrator L2 access + trip-end signal
- **D-14:** The orchestrator **already has Redis** — `IConnectionMultiplexer` is injected today for hydration (`HydrationBackgroundService`, `WorkflowLifecycle` read L2 projections via `db.StringGetAsync`). Phase 71 **reuses the existing connection** for the new result-path data-relocation ops (read `out:`, write `data:`, delete `out:`) — it does NOT add Redis to the orchestrator.
- **D-15:** **L1 stays the in-memory high-performance orchestration layer.** Next-step **resolution remains L1-only / pure in-memory** (`SelectNext` against `wf.Steps`) — no Redis on the resolve path (SPEC constraint). Only the data-relocation tail touches L2.
- **D-16:** All new L2 ops run through **`BaseConsole.Core`'s `RetryLoop`** (the same bounded helper the processor + Keeper use): read→`REINJECT`, write→`INJECT`, delete→`DELETE`, send→throw.
- **D-17:** The `data:` write carries the jittered **`L2ProjectionKeys.OutputDataTtl`** policy (single source of truth), floor from the orchestrator's own option (default 300, consistent with `ProcessorLivenessOptions`/`RecoveryOptions`).
- **D-18:** **Trip-end signal** = a counter on the existing `OrchestratorMetrics` with **two reasons** (`completed-terminal` vs `completed-unresolved`), each paired with a **distinct structured log line** (consistent with `TypedResultConsumer`'s current business-ack log + the v8.0.0-verified metric style). No new bus event. This makes the no-silent-loss property falsifiable (every trip-end is counted/traced).

### Claude's Discretion
- Exact C# names/namespaces/nullability for `NextStepHandoff`, the orchestrator keeper contracts, the Pre-pipeline class, the relocate-tail helper, and the Post consumer/definition (keep consistent with existing `Step*`/`DataResult`/`OutputTail` conventions).
- The exact orchestrator metric instrument name + tag key for the two trip-end reasons (keep consistent with `OrchestratorMetrics.DispatchSent`/`ResultConsumed` PascalCase-tag convention).
- A1 stamp wiring detail in `OutputTail.BuildStep` (Completed arm: `EntryId = dr.MessageId`; other arms keep `Guid.Empty`).
- The orchestrator's own `OutputDataTtl` option type/binding (new options class vs reuse an existing one).
- Whether the orchestrator Post endpoint binds at startup like the result queue or follows the runtime-bind pattern — planner's call from the existing Orchestrator `Program.cs` wiring.
- Test-double reuse vs new fakes for the orchestrator Pre/Post/keeper facts.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Source of truth (locked requirements)
- `.planning/phases/71-orchestrator-two-consumer-design-pre-process-post-process-wi/71-SPEC.md` — **Locked requirements — MUST read before planning.** 12 requirements, boundaries, constraints, acceptance criteria, the no-silent-loss + namespace-cross-pairing + A1-close decisions.

### The mirror (Phase 70 — the implementation template)
- `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-SPEC.md` — the processor two-consumer pseudocode (Pre/Post/KEEPER) this phase mirrors on the orchestrator side.
- `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-CONTEXT.md` — Phase-70 implementation decisions (D-01..D-17): `DataResult` spine, `OutputTail` shared helper, `-post` queue, keeper reshape, envelope-`MessageId` override, `RetryLoop` escalation map. Phase 71 follows these patterns.

### Orchestrator code being reshaped (paths from codebase scout)
- `src/Orchestrator/Consumers/TypedResultConsumer.cs` — the L1-only advancement base; becomes the thin Pre shell delegating to the new Pre-pipeline (D-06).
- `src/Orchestrator/Consumers/Step{Completed,Failed,Cancelled,Processing}Consumer.cs` + their `*Definition.cs` — the 4 typed Pre consumers (kept, each carrying its `Outcome`); + new Post consumer + definition (D-06/D-07).
- `src/Orchestrator/Dispatch/StepDispatcher.cs` + `IStepDispatcher.cs` — the build-and-`Send` `EntryStepDispatch`; the Post tail dispatches with `entryId = messageId` (D-08/D-13).
- `src/Orchestrator/Dispatch/StepAdvancement.cs` — pure L1 `SelectNext` (entry-condition match); resolution stays L1-only (D-15).
- `src/Orchestrator/Messaging/OrchestratorL2Keys.cs` — reader shim → `L2ProjectionKeys`; add `out:`/`data:` access on the result path (D-14).
- `src/Orchestrator/Hydration/WorkflowLifecycle.cs` + `HydrationBackgroundService.cs` — existing `IConnectionMultiplexer` injection point (reused, D-14).
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` — add the two-reason trip-end counter (D-18).
- `src/Orchestrator/Program.cs` — endpoint/registration wiring for the new Post queue + L2/RetryLoop services.

### Shared assets + keeper (reused)
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `ExecutionData` (`skp:data:`), `OutputData` (`skp:out:`), `OutputDataTtl` policy (D-12/D-17).
- `src/Messaging.Contracts/DataResult.cs` + `EntryStepDispatch.cs` + `IStepResult.cs` + `Step*.cs` — existing contracts; new `NextStepHandoff` + orchestrator keeper contracts land alongside (D-10/D-01).
- `src/BaseProcessor.Core/Processing/OutputTail.cs` — A1 stamp site (`BuildStep` Completed arm → `EntryId = dr.MessageId`) (req 7).
- `src/BaseConsole.Core/Resilience/RetryLoop.cs` — the bounded L2-op/send helper reused by the orchestrator (D-16).
- `src/Keeper/Recovery/{Reinject,Inject,Delete}Consumer.cs` + `RecoveryConsumerBase.cs` + `RecoveryEndpointBinder.cs` — the pattern the new orchestrator keeper consumers follow (D-04/D-05).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`IConnectionMultiplexer` (already injected in Orchestrator)** — `HydrationBackgroundService`/`WorkflowLifecycle` use `redis.GetDatabase()` + `StringGetAsync`. Reuse for the result-path `out:`/`data:` ops (D-14).
- **`StepAdvancement.SelectNext(outcome, completed, steps)`** — pure in-memory entry-condition match (`== (int)outcome || Always`; `Never` excluded; null `NextStepIds` = terminal). Stays L1-only (D-15).
- **`TypedResultConsumer<T>` + the 4 sealed arms** — the per-type `Outcome` knob pattern (no status switch); kept as the Pre front (D-06).
- **`RetryLoop.ExecuteAsync<T>` (`BaseConsole.Core`)** — bounded retry → caller routes exhaustion (read→REINJECT, write→INJECT, delete→DELETE, send→throw) (D-16).
- **`RecoveryConsumerBase` + `Guard` + partitioned gate-open recovery queue (`Keeper`)** — the template for the new orchestrator keeper consumers (D-04/D-05).
- **`L2ProjectionKeys` (`OutputData`/`ExecutionData`/`OutputDataTtl`)** — single source of truth for `out:`/`data:` keys + the jittered TTL policy (D-12/D-17).
- **`OutputTail` (`BaseProcessor.Core`)** — the relocate-tail pattern to mirror; also the A1 stamp site (req 7).

### Established Patterns
- **Endpoint policy:** durable per-process queue, `UseMessageRetry` none, no error/dead-letter transport, in-code `RetryLoop` only, send-exhaust → throw → nack-requeue. The new `orchestrator-result-post` queue mirrors the result queue exactly (D-07).
- **Envelope `MessageId` override:** fan-out rides a fresh MassTransit envelope id; keeper re-injections override the outbound `MessageId` with the carried `messageId`; no inbox/dedup (D-03/D-11).
- **Thin consumer → pipeline class** — `TypedResultConsumer` → new Pre-pipeline (D-06); preserves unit-testability + existing typed-consumer tests.
- **Self-contained recovery body** — orchestrator keeper contracts carry everything their op needs (D-02), mirroring `DataResult`-embedded `KeeperInject`.

### Integration Points
- Pre (4 typed consumers) reads/deletes `L2[out:entryId]` (`skp:out:`) → fan-out `NextStepHandoff` → `orchestrator-result-post`.
- Post writes `L2[data:messageId]` (`skp:data:`) → dispatches `EntryStepDispatch` to `queue:{processorId:D}` with `entryId = context.MessageId`.
- Orchestrator keeper escalations (`REINJECT`/`INJECT`/`DELETE`) → central Keeper recovery queue (partitioned, gate-open-only).
- A1: processor `OutputTail.BuildStep` Completed arm stamps `EntryId = output messageId` → orchestrator Pre gates/reads `L2[out:EntryId]` off it.

</code_context>

<specifics>
## Specific Ideas

- The design converges on the **symmetric, fewest-moving-parts mirror of Phase 70** — the user steered all four areas toward the recommended symmetric path, with two precise corrections:
  1. **`messageId` is the MassTransit envelope id, not a handoff body field** (D-11). It appears in a body only on the keeper contracts, where the keeper must re-assert it across a hop (D-03) — the processor→keeper→processor behavior.
  2. **The orchestrator already has Redis; L1 is purely an in-memory high-performance layer** (D-14/D-15). Phase 71 reuses the connection; resolution stays L1-only.
- The two genuine asymmetries vs Phase 70 to honour: (a) orchestrator keeper ops re-inject a **result** (not a dispatch) and write **`data:`** (not `out:`) — hence new contracts (D-01); (b) the no-silent-loss guarantee is realized by an explicit **two-reason trip-end metric/log** rather than a keeper/park (D-18).

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (Fan-in / merge / AND-join is already SPEC-declared out of scope, owned by a future phase; the processor's statelessness makes OR-join / fire-per-edge the model.)

</deferred>

---

*Phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi*
*Context gathered: 2026-06-17*
