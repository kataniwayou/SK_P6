# Processor / Keeper Recovery — Canonical Spec

> [!WARNING]
> **SUPERSEDED by Phase 70 (2026-06-16).** This document describes the now-retired
> single-consumer slot-array Pre/In/Post-Process + recovery-pass model
> (gate on `L2[messageId]` HASH, atomic slot writes, recovery pass). It has been
> replaced by the **two-consumer (Pre-Process + Post-Process) design** whose pinned
> pseudocode is the sole source of truth:
> `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-SPEC.md`.
> Do not implement against this document — it is retained for historical context only.

> Normative description of the processor round trip and the keeper recovery
> state machine. This is the design contract; it is independent of any
> particular implementation. A non-normative "Relationship to current
> implementation" section at the end cross-references the code.

## 1. Purpose & scope

A **processor** consumes one dispatch from the orchestrator, runs three
pipelines (Pre-Process → In-Process → Post-Process), emits per-item results
back to the orchestrator, and cleans up its L2 state. Every infrastructure
failure is either retried in place or escalated to the **keeper** for
out-of-band recovery. There is no broker-level retry and no error/dead-letter
queue — a send that cannot complete is thrown, and the broker redelivers.

## 2. Data model & terminology

| Term | Meaning |
|------|---------|
| `messageId` | Broker message id of the inbound dispatch. The idempotency / branch key. |
| **index** = `L2[messageId]` | A hash (slot array). Field = slot position `x`; value = an `outputEntryId` **or** `guid.empty` (retired slot). Carries a TTL. Doubles as the "this message has been seen" record. |
| `inputEntryId` | The entryId this step **consumed** (the upstream step's output). Its blob lives at `L2[inputEntryId]`. Referred to elsewhere as the *origin* entryId. |
| `outputEntryId` | An entryId **generated** by this step for one output item. Its blob lives at `L2[outputEntryId]`. Carries a TTL. |
| `validatedData` | The input blob after passing input-schema validation; fed to In-Process. |
| `data` | One output item's payload (the value written to `L2[outputEntryId]`). |
| **round trip** | One consume of one dispatch: gate → Forward or Recovery → cleanup → done. |

**TTL.** Every data key (`L2[*EntryId]`) carries a TTL. The index
(`L2[messageId]`) also carries a **random** TTL, deliberately ≥ the data TTL so
the index outlives the data it points at, with jitter to avoid a synchronized
expiry herd.

## 3. Round-trip overview

On consume, evaluate the idempotency gate **once**:

```
exists := L2[messageId] exists?      // bounded-retry L2 op; exhausted → REINJECT, end round trip
  not exists → FORWARD   (Pre-Process → In-Process → Post-Process forward)
  exists     → RECOVERY  (Post-Process recovery; Pre/In are skipped)
```

`exists == true` means the same broker message is being **redelivered** after a
prior partial or fully-completed attempt.

## 4. Forward path

### 4.1 Pre-Process

1. (gate already evaluated — see §3)
2. **Read** `L2[inputEntryId]`.
   *L2 op exhausted →* `REINJECT`; **end round trip.**
   *(Skip for a source step that has no input.)*
3. **Validate** `validatedData` against the input schema.
   *Invalid →* send a **failed** result to the orchestrator; **end round trip.**

> "end round trip" = return immediately. No cleanup tail runs on these
> early-exit paths; the input key is left to its owner / TTL, and (on the
> REINJECT path) must remain readable for re-injection.

### 4.2 In-Process

- Abstract method, overridden by the author.
- Input: `validatedData` + the delivered payload.
- Output: `List<(result, data, error_message?)>`, one entry per produced item.
  Per-item `result` ∈ { completed, failed, processing, cancelled }.
- The author deserializes the payload into a config object.
- The method may instead **throw** a status (completed / failed / processing /
  cancelled). On throw: send **one** result to the orchestrator; **end round
  trip** (no Post-Process).

### 4.3 Post-Process (forward)

For each item, in order:

1. **Validate** `data` against the output definition.
   *Not "completed" →* send the result to the orchestrator; **continue** (skip
   the remaining steps for this item).
2. Generate `outputEntryId`.
3. **Atomic write** — in **one atomic operation** set both keys: the index slot
   `L2[messageId][x] = outputEntryId` (with the index's random TTL) **and**
   `L2[outputEntryId] = data` (with the data TTL).
   *L2 op exhausted →* `INJECT(messageId, x, outputEntryId, data, inputEntryId)`;
   **continue.**
4. **Send** the completed result (carrying `outputEntryId`) to the orchestrator.
5. **Retire** the slot: `L2[messageId][x] = guid.empty`.

Then, **only if no item escalated to the keeper** (`INJECT`) during this pass,
run the **cleanup tail** (§6). If any item escalated, **skip cleanup** — the
index and input keys are left intact for the keeper to complete the escalated
item and for a later Recovery pass (redelivery) or TTL to reclaim them. This
eliminates the processor/keeper race on the index key.

## 5. Recovery path (redelivery)

The index already exists, so the produced outputs (or some of them) may already
be in L2. Re-emit idempotently rather than reprocessing.

1. **Read** the index slots `L2[messageId]`.
   *L2 op exhausted →* `REINJECT`; **end round trip.**
2. For each slot `x`:
   - If the slot value is **not** `guid.empty`:
     - If `L2[slotEntryId]` **exists** → **send** the result to the orchestrator.
     - **Retire** the slot: `L2[messageId][x] = guid.empty`.

Then run the **cleanup tail** (§6).

## 6. Cleanup tail (shared by Forward and Recovery)

```
delete L2[messageId] AND L2[inputEntryId]      // one atomic multi-key delete
  L2 op exhausted → DELETE(messageId, inputEntryId); end
```

Both keys are removed in a **single atomic operation**, so the system never
observes an "index deleted, input still alive" intermediate state. That
intermediate is the only window in which a redelivery would re-enter Forward
while the input is still readable and reprocess it — emitting duplicate results
under freshly generated `outputEntryId`s that downstream entryId-dedup cannot
absorb. The atomic delete closes that window; once both keys are gone a
redelivery dead-ends at `REINJECT` (input absent → drop). On exhaustion the
keeper's `DELETE` reclaims both keys.

## 7. Resilience invariants

- **L2 operations.** Every read/write/delete runs in a bounded try/catch retry
  loop. On exhaustion the caller routes to the keeper (or to a business result),
  per the rules above. No backoff is required between attempts.
- **Sends.** Every send (to orchestrator or keeper) runs in a bounded retry
  loop; on exhaustion it **throws**. With error routing disabled and message
  retry set to none, the throw surfaces to the broker as nack-requeue → the
  dispatch is **redelivered** (which re-enters via the Recovery path).
- **No broker retry, no error/dead-letter queue.** In-code retry is the only
  retry mechanism.

## 8. Keeper states

The keeper consumes recovery messages and performs the escalated work. It is
gated on L2 health: while L2 is unhealthy it does not dequeue (messages
accumulate on the broker; no dequeue-and-drop).

| State | Trigger | Action |
|-------|---------|--------|
| `REINJECT` | Pre-Process / gate L2 op exhausted | Read the (still-present) `inputEntryId`, reconstruct the original dispatch **including its payload**, and re-inject it to the processor. |
| `INJECT` | Forward Post step 3 (atomic write) exhausted | Atomically write the index slot `L2[messageId][x] = outputEntryId` **and** `L2[outputEntryId] = data`, then send the completed result to the orchestrator. |
| `DELETE` | Cleanup tail, atomic delete exhausted | Delete **both** `L2[messageId]` and `L2[inputEntryId]` (a single atomic multi-key delete). |

## 9. Under-specified points (need a decision)

These follow from the rules above but are not pinned by the spec text:

1. **Slot retirement after `INJECT`.** When an item escalates to the keeper, the
   processor `continue`s and never reaches the retire step (5) for that item, so
   its slot stays un-retired. A subsequent Recovery pass re-sends it. This is
   idempotent-by-design only if the orchestrator deduplicates re-sends.

## 10. Relationship to current implementation (non-normative)

As of this writing the code diverges from this spec on three points:

- **Writes are not atomic; index-write failure drops.** The code performs the
  index-slot write, the hash TTL, and the data write as three separate ops
  (`ProcessorPipeline.cs:270-291`). An exhausted **data** write escalates to a
  single `INJECT` (matching this spec), but an exhausted **index** write
  **drops** the item (`INFRA-01`) — it is lost on infra failure. This spec's
  atomic write makes both failure modes one `INJECT`, so no drop path exists.
- **Forward cleanup is unconditional in the code.** `DeleteTerminalAsync` runs
  at the end of `RunForwardAsync` even when an item escalated to the keeper, so
  the code still has the processor/keeper race on the index key. This spec gates
  the forward cleanup on "no item escalated to the keeper."
- **In-Process contract.** The code's per-item outcome is `Completed`/`Failed`
  only (`processing`/`cancelled` arrive solely via thrown status and abort the
  batch), the item record carries an `executionId` rather than `error_message`,
  and `executionId` is threaded through the processor seam for multi-execution.
