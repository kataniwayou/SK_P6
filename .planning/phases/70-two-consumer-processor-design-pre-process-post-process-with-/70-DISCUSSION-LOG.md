# Phase 70: Two-consumer processor design - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-16
**Phase:** 70-two-consumer-processor-design-pre-process-post-process-with-
**Areas discussed:** `<data result>` contract & seam, Spawn/Delete helpers & swallow, L2 keying & keeper reshape, Code & test structure

---

## `<data result>` contract & seam

| Option | Description | Selected |
|--------|-------------|----------|
| One unified record | Single DataResult serves seam-return + Post wire contract | ✓ |
| Two types | Separate in-process DTO + serializable wire contract | |

| Option | Description | Selected |
|--------|-------------|----------|
| Self-contained: data + result + all ids | Carries data, result, executionId, correlation ids, carried messageId | ✓ |
| Minimal: data + result + executionId | Correlation ids flow via ConsumeContext/envelope | |

| Option | Description | Selected |
|--------|-------------|----------|
| DataResult | Matches SPEC `<data result>`; retire ProcessItem | ✓ |
| Reuse/rename ProcessItem | Keep ProcessItem name, evolve it | |
| ProcessResult | Neutral alternative | |

| Option | Description | Selected |
|--------|-------------|----------|
| Keep ProcessAsync | Only the return type changes (List<ProcessItem> → DataResult?) | ✓ |
| Rename to VirtualProcess | Match SPEC pseudocode literally | |

**User's choice:** One self-contained `DataResult` record reused as seam-return + wire contract; named `DataResult`; seam keeps name `ProcessAsync`.
**Notes:** `result` reuses the existing 4-value `StepOutcome` enum.

---

## Spawn/Delete helpers & swallow

| Option | Description | Selected |
|--------|-------------|----------|
| Protected methods on BaseProcessor | this.SpawnToPost / this.DeleteEntry; framework wires resilience | ✓ |
| Context object passed to the seam | ProcessAsync(..., IProcessContext ctx) | |

| Option | Description | Selected |
|--------|-------------|----------|
| Bounded retry, then swallow/drop | Scheduler re-fires; resolves req-9-vs-pseudocode tension | ✓ |
| Bounded retry, then throw | Redelivery re-spawns ALL N (partial double-spawn) | |
| Fire-and-forget | Single attempt, no RetryLoop | |

| Option | Description | Selected |
|--------|-------------|----------|
| Bounded retry, then escalate to DELETE keeper | Same as framework-owned inline-tail delete | ✓ |
| Swallow (no-op for source steps) | Inconsistent with the L2-op→keeper invariant | |

| Option | Description | Selected |
|--------|-------------|----------|
| Author mints, passes to SpawnToPost | Matches SpawnToPost(dataResult, executionId) + req 10 | ✓ |
| SpawnToPost mints internally | Drops the executionId param | |

**User's choice:** Protected `SpawnToPost`/`DeleteEntry`; spawn bounded-retry-then-swallow; delete bounded-retry-then-escalate-DELETE; author mints the N executionIds.
**Notes:** The swallow-vs-escalate asymmetry is intentional — spawn loss recovered by scheduler re-fire, entry-delete loss recovered by keeper.

---

## L2 keying & keeper reshape

| Option | Description | Selected |
|--------|-------------|----------|
| New OutputData builder: skp:out:{messageId} | Distinct namespace from input blobs + retired slot HASH | ✓ |
| Reuse the freed skp:msg: namespace | Repurpose MessageIndex from HASH to string | |
| Reuse skp:data: keyed by messageId | Blurs input vs output blobs under one prefix | |

| Option | Description | Selected |
|--------|-------------|----------|
| Delete MessageIndex, keep ExecutionData | Retire slot HASH + SlotTtl; keep entryId data key; add OutputData | ✓ |
| Keep MessageIndex for back-compat | Leaves dead/misleading key code | |

| Option | Description | Selected |
|--------|-------------|----------|
| Reshape the three in-place | Reuse keeper queue, partitioner, RecoveryConsumerBase | ✓ |
| New contracts alongside old | Doubles the contract + wiring surface during transition | |

| Option | Description | Selected |
|--------|-------------|----------|
| Embed the whole DataResult | INJECT reuses the self-contained wire type verbatim | ✓ |
| Flatten the needed fields | Duplicates DataResult fields; drift risk | |

**User's choice:** New `OutputData(messageId)=skp:out:{messageId}` (jittered TTL); delete `MessageIndex`/`SlotTtl`, keep `ExecutionData`; reshape the three keeper contracts in-place; `KeeperInject` embeds the whole `DataResult` + source `entryId`.
**Notes:** `KeeperDelete` becomes delete-only (drops two-key MessageId role).

---

## Code & test structure

| Option | Description | Selected |
|--------|-------------|----------|
| Rewrite ProcessorPipeline in-place as Pre flow | Consumer stays thin; recovery-pass code deleted | ✓ |
| Collapse into the consumer | Loses the unit-testable pipeline seam | |

| Option | Description | Selected |
|--------|-------------|----------|
| PostProcessConsumer + shared OutputTail helper | Single source of truth for write-gated-on-completed | ✓ |
| PostProcessConsumer with its own inline tail | Two copies of write/send/INJECT logic | |

| Option | Description | Selected |
|--------|-------------|----------|
| Same console, new per-processor queue | queue:{processorId:D}-post; entry-endpoint policy | ✓ |
| Same console, shared post queue | Loses per-processor isolation | |

| Option | Description | Selected |
|--------|-------------|----------|
| Delete obsolete, rewrite the rest, reuse doubles | dotnet test green is the req-12 bar | ✓ |
| Rewrite every file in-place | Awkward rewrites of slot/recovery tests with no subject | |

**User's choice:** Rewrite `ProcessorPipeline` in-place as the Pre flow; new `PostProcessConsumer` + shared `OutputTail`; Post on same console, new `queue:{processorId:D}-post`; delete obsolete slot/recovery tests, rewrite Pre/Post/keeper facts, reuse hermetic doubles.
**Notes:** —

## Claude's Discretion

- Exact `DataResult` property names/nullability; `StepOutcome` → four `Step*` contract mapping in `OutputTail`.
- Input-validation/deserialize-failure → exactly-one `StepFailed` wiring (no `entryId` delete; left to TTL).
- Superseded-doc marker mechanism for `docs/design/processor-keeper-recovery-spec.md`.
- Naming of the `OutputTail` helper and new test files.

## Deferred Ideas

None — orchestrator-side `entryId`↔`messageId` threading and Mode-2 N-reconciliation are SPEC-declared out of scope.
