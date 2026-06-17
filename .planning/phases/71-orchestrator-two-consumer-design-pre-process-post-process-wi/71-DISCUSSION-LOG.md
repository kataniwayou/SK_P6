# Phase 71: Orchestrator two-consumer design (Pre-Process + Post-Process) with keeper recovery - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-17
**Phase:** 71-orchestrator-two-consumer-design-pre-process-post-process-wi
**Areas discussed:** Keeper recovery realization, Pre/Post consumer + queue structure, Fan-out (Pre→Post) message contract, Orchestrator L2 access + trip-end signal

---

## Area 1 — Keeper recovery realization

### 1a — Keeper contracts

| Option | Description | Selected |
|--------|-------------|----------|
| New orchestrator-specific contracts | New `OrchestratorReinject`/`Inject`/`Delete`; each side's keeper body self-contained | ✓ |
| Reuse/extend processor keeper contracts | Overload `KeeperReinject`/`Inject`/`Delete` with discriminator/extra fields | |

**User's choice:** New orchestrator-specific contracts (Recommended).

### 1b — Keeper locus

| Option | Description | Selected |
|--------|-------------|----------|
| Central Keeper service | New recovery consumers; shared `RecoveryConsumerBase` + `Guard` + partitioned gate-open queue | ✓ |
| In-process in the Orchestrator | Orchestrator handles its own L2-op-exhaustion recovery | |

**User's choice:** Central Keeper service (Recommended).
**Notes:** Orchestrator keeper ops do DIFFERENT work than the processor's (REINJECT re-injects a Step-result to orchestrator Pre, not a dispatch; INJECT writes `data:` + dispatches `EntryStepDispatch`; DELETE deletes the `out:` blob) — hence separate contracts. BIT health gate governs orchestrator recovery too.

---

## Area 2 — Pre/Post consumer + queue structure

### 2a — Pre structure

| Option | Description | Selected |
|--------|-------------|----------|
| Keep 4 typed Pre consumers → shared pipeline | Each supplies its Outcome; thin shells → shared Pre-pipeline (mirror D-14) | ✓ |
| Collapse to one unified Pre consumer | One consumer switching on runtime type | |

**User's choice:** Keep 4 typed Pre consumers → shared pipeline (Recommended).

### 2b — Post + tail

| Option | Description | Selected |
|--------|-------------|----------|
| New Post consumer + shared relocate-tail helper | New Post on `orchestrator-result-post`; tail shared with keeper INJECT (mirror D-15/D-16) | ✓ |
| Inline the tail in the Post consumer only | Keeper INJECT reimplements independently | |

**User's choice:** New Post consumer + shared relocate-tail helper (Recommended).

---

## Area 3 — Fan-out (Pre→Post) message contract

### 3a — Handoff type

| Option | Description | Selected |
|--------|-------------|----------|
| New self-contained record, messageId on envelope | New `NextStepHandoff` carrying next-step target + relocated data; no MessageId body field | ✓ |
| Reuse DataResult | Wrong shape (completed step's ids + body MessageId) | |

**User's choice:** New self-contained record (Recommended), with correction.

### 3b — Data + lineage

| Option | Description | Selected |
|--------|-------------|----------|
| Inline data, executionId unchanged | Data rides inline string-JSON; Post writes verbatim; executionId threaded unchanged; non-completed → empty data + entryId=Guid.Empty | ✓ |
| Data by reference (re-read in Post) | Carry only the `out:` key; Post re-reads — second read + end-delete race | |

**User's choice:** Inline data, executionId unchanged (Recommended).
**Notes (user corrections):**
- `messageId` is the **MassTransit envelope `MessageId`**, NOT a handoff body field. Post reads `context.MessageId`.
- The **keeper escalation contracts additionally carry `messageId` in-body** — the keeper needs it to override the future (outbound) envelope `MessageId` on re-injection/dispatch. This is the same processor→keeper→processor behavior as Phase 70.

---

## Area 4 — Orchestrator L2 access + trip-end signal

| Option | Description | Selected |
|--------|-------------|----------|
| Reuse existing orchestrator Redis + BaseConsole.Core RetryLoop; L1 stays perf layer; two-reason metric | (Presented conversationally) | ✓ |

**User's choice:** Confirmed, with correction.
**Notes (user correction):** "No Redis" was misleading — the orchestrator **already has `IConnectionMultiplexer`** (hydration reads L2 projections via `HydrationBackgroundService`/`WorkflowLifecycle`). **L1 is purely an in-memory high-performance orchestration layer.** Phase 71 reuses the existing connection for the result-path data-relocation ops; next-step resolution stays L1-only / pure in-memory. `data:` write carries the jittered `OutputDataTtl` policy. Trip-end = `OrchestratorMetrics` counter with `completed-terminal` / `completed-unresolved` reasons + distinct structured log lines; no new bus event.

---

## Claude's Discretion

- Exact C# names/namespaces/nullability for new types (`NextStepHandoff`, orchestrator keeper contracts, Pre-pipeline, relocate-tail helper, Post consumer/definition).
- Orchestrator trip-end metric instrument name + tag key.
- A1 stamp wiring in `OutputTail.BuildStep` (Completed arm → `EntryId = dr.MessageId`).
- Orchestrator `OutputDataTtl` option type/binding.
- Post endpoint startup-bind vs runtime-bind (planner's call from existing `Program.cs`).
- Test-double reuse vs new fakes.

## Deferred Ideas

None — discussion stayed within phase scope. (Fan-in/merge/AND-join is SPEC-declared out of scope; the processor's statelessness makes OR-join / fire-per-edge the model.)
