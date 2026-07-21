# Phase 74: Reshape Business Metrics to a Uniform Two-Counter Model - Context

**Gathered:** 2026-06-17 (drafted as slug "72"; renumbered to 74)
**Status:** Ready for planning

<domain>
## Phase Boundary

Replace the per-type business counters on the Orchestrator, Processor, and Keeper with a uniform two-counter model — `{service}_messages_consumed` and `{service}_messages_sent` (labels `workflowId`+`processorId`, camelCase) — remove the three legacy dedup/drop counters, and add a label-less `keeper_l2_probe` heartbeat. This discussion covers HOW to wire it; the WHAT is locked by 74-SPEC.md.

</domain>

<spec_lock>
## Requirements (locked via 74-SPEC.md)

**6 requirements are locked.** See `74-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `74-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from 74-SPEC.md):**
- Rewriting `OrchestratorMetrics`, `ProcessorMetrics`, `KeeperMetrics` to the uniform counter pair + removals
- New `keeper_l2_probe` heartbeat counter in `BitHealthLoop`
- Increment sites: orchestrator (every outbound Send), processor (consume + send), keeper (base `Consume` choke point + 4 send sites)
- `workflowId`+`processorId` (camelCase) labels at every consumed/sent increment
- Removing `orchestrator_result_deduped`, `processor_dispatch_deduped`, `keeper_reinject_dropped` and the processor `outcome` label
- Injecting `KeeperMetrics` into the 4 sending keeper consumers + `BitHealthLoop`
- Reworking `PassFailEngine` + `PromCounterSnapshot` + the ~11 affected metric test files

**Out of scope (from 74-SPEC.md):**
- Changing meter names or `AddMeter(...)` registrations (names stay `Orchestrator`/`BaseProcessor`/`Keeper`)
- Infrastructure instrumentation (AspNetCore/HttpClient/Runtime) and the WebAPI (no business counters)
- Resource labels (`service.instance.id`, `service.name`) — unchanged
- Healthy-vs-unhealthy L2 probe breakdown (`keeper_l2_probe` is a label-less heartbeat only)
- Collector/exporter config; dashboards / Grafana / alerting

</spec_lock>

<decisions>
## Implementation Decisions

### Counting unit & increment placement
- **D-01:** The unit of counting is the **actual broker message (its `MessageId`)** — one real message = one increment, taken at the exact send/consume point.
- **D-02:** `{service}_messages_sent` is incremented **right before/around each broker Send**, once per message about to go out, **after the send-success guard** (a failed/exhausted Send does not count). A consume that **fans out to N sends** (e.g. a result selecting multiple next steps → N dispatches) increments **N times**, one per message id.
- **D-03:** `{service}_messages_consumed` is incremented **at the consume entry**, once for the message just consumed.
- **D-04:** `messageId` is the **counting unit, NOT a Prometheus label** — it is unbounded (one per message) and would blow up cardinality. The series labels are **only** `workflowId`+`processorId`. If id-level sent-vs-consumed reconciliation is ever needed, it lives in the **structured logs** (which already carry the execution ids via `ExecutionLogScope`), not the counter.

### Label semantics
- **D-05:** `processorId` on `*_messages_sent` = **the processor the message is being delivered to** (the dispatch recipient): `NextProcessorId` for `OrchestratorInject`, the target queue's processor for direct dispatches (which already equals the message's `ProcessorId`).
- **D-06:** For sends whose recipient is **not a processor** (results → `orchestrator-result` queue; orchestrator → keeper escalation), use the **producing processor's `ProcessorId`** (the only processor identity on the message) — so every `*_messages_sent` series carries a real `processorId`.
- **D-07:** Labels are camelCase (`workflowId`, `processorId`). `IKeeperRecoverable` exposes both `WorkflowId` and `ProcessorId`, and `IExecutionCorrelated` exposes `WorkflowId`/`ProcessorId` — both label values are available at every increment site without new plumbing.

### Keeper wiring
- **D-08:** `keeper_messages_consumed` is incremented **once in `RecoveryConsumerBase.Consume`** — the single choke point all 5 recovery consumers funnel through (REINJECT/INJECT for processor + orchestrator, plus DELETE).
- **D-09:** `keeper_messages_sent` uses a **shared protected helper on `RecoveryConsumerBase`** (e.g. `CountSent(workflowId, processorId)`) that each of the 4 sending consumers calls after a successful `ep.Send`. Centralizes label construction next to the consumed-counter increment; DRY. (`DeleteConsumer` sends nothing → no call.)
- **D-10:** `KeeperMetrics` is injected into `RecoveryConsumerBase` (for both keeper counters) and into `BitHealthLoop` (for `keeper_l2_probe`), following the existing `IMeterFactory` DI pattern — never a static `Meter`.

### keeper_l2_probe
- **D-11:** Incremented **once per `BitHealthLoop` tick** (one per `probe.ProbeOnceAsync` call, regardless of healthy/unhealthy result), no labels. `rate(keeper_l2_probe_total[5m]) > 0` proves the Keeper is actively probing L2.

### Analyzer rebinding (dropped `outcome`)
- **D-12:** Repoint `PassFailEngine`'s binding from `processor_result_sent{outcome="completed"}` to **`processor_messages_sent` total**. The close-gate sweep is an all-complete fixture, so total == completed and the `Expected = COMPLETE runs × 9` math still holds.
- **D-13:** **Drop the non-completed corroboration WARNING** path in `PassFailEngine` (`failed`/`cancelled`/`processing` outcomes) — the `outcome` label no longer exists. `PromCounterSnapshot` fields are renamed to the new metric names and the `outcome`-keyed field is removed.

### Test migration
- **D-14:** **Rename-in-place** the ~11 affected metric fact files to the new names/labels, and **add explicit assertions that the three removed counters emit no series** (absence checks). Preserves test history and structure.

### Claude's Discretion
- Exact helper signature/name on `RecoveryConsumerBase`, the precise local-variable plumbing for enumerating fan-out message ids at the orchestrator/processor pipeline send sites, and the per-file mechanics of the test rename are left to planning/execution.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/74-reshape-business-metrics-into-a-uniform-two-counter-model/74-SPEC.md` — Locked requirements, boundaries, and acceptance criteria. MUST read before planning.

### Recovery pipeline (send/consume sites being instrumented)
- `docs/design/processor-keeper-recovery-spec.md` — canonical recovery spec for the INJECT/REINJECT/DELETE flows whose send sites the keeper/orchestrator counters instrument.

### Code touchpoints (full paths)
- `src/Orchestrator/Observability/OrchestratorMetrics.cs`, `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs`, `src/Keeper/Observability/KeeperMetrics.cs` — the three meter classes to rewrite
- `src/Orchestrator/Dispatch/StepDispatcher.cs`, `src/Orchestrator/Recovery/OrchestratorResultPipeline.cs`, `src/Orchestrator/Consumers/TypedResultConsumer.cs` — orchestrator increment sites
- `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs`, `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — processor increment sites
- `src/Keeper/Recovery/RecoveryConsumerBase.cs` (+ `ProcessorReinjectConsumer`, `OrchestratorInjectConsumer`, `ProcessorInjectConsumer`, `OrchestratorReinjectConsumer`, `DeleteConsumer`), `src/Keeper/Health/BitHealthLoop.cs` — keeper increment sites
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`, `tests/BaseApi.Tests/Observability/Analysis/PromCounterSnapshot.cs` — analyzer contract to rebind
- `src/Messaging.Contracts/IKeeperRecoverable.cs`, `src/Messaging.Contracts/IExecutionCorrelated.cs` — label sources (`WorkflowId`/`ProcessorId`)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`IMeterFactory` DI pattern** — all three meter classes already build via `meterFactory.Create(MeterName)`; the new counters/helpers follow the same constructor shape (never a static `Meter`).
- **`RecoveryConsumerBase.Consume`** — single choke point for all 5 keeper recovery consumers; one increment site covers `keeper_messages_consumed`, and a protected `CountSent` helper there serves all 4 sending consumers.
- **`ExecutionLogScope`** (`Messaging.Contracts`) — already emits `WorkflowId`/`StepId`/`ProcessorId` into structured logs; this is the home for any id-level (messageId) reconciliation, keeping it out of the metric labels.

### Established Patterns
- Instrument names are snake_case with **no `_total` suffix in code** — the collector's prometheus exporter appends `_total`. Label keys were PascalCase (`ProcessorId`); this phase moves to camelCase (`workflowId`/`processorId`) since all metric names change anyway (no existing PromQL to preserve).
- Meter **names** stay `Orchestrator`/`BaseProcessor`/`Keeper` → `AddMeter(...)` registrations are untouched; only instrument names, labels, and increment sites change.

### Integration Points
- `PassFailEngine`/`PromCounterSnapshot` consume these metric names on the live stack — the binding (`Expected = COMPLETE runs × 9`) repoints to `processor_messages_sent` total.
- The live-stack close-gate sweep (all-complete fixture, net-zero) is the deferred-automated acceptance vehicle.

</code_context>

<specifics>
## Specific Ideas

- Count by **actual message id** at the precise send/consume point — fan-out (one consume → N sends) counts each message. The id is the *unit*, not a label.

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope. (A healthy-vs-unhealthy L2 probe gauge was noted out of scope in SPEC.md; `keeper_l2_probe` stays a label-less heartbeat.)

</deferred>

---

*Phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model*
*Context gathered: 2026-06-17 (imported from drafted slug "72")*
