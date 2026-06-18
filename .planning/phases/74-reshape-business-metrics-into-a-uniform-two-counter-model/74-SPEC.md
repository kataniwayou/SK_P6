# Phase 74: Reshape Business Metrics to a Uniform Two-Counter Model — Specification

**Created:** 2026-06-17 (drafted as slug "72"; renumbered to 74 — phases 72/73 already exist)
**Ambiguity score:** 0.12 (gate: ≤ 0.20)
**Requirements:** 6 locked

## Goal

Replace the per-type business counters on the Orchestrator, Processor, and Keeper with a uniform two-counter model — `{service}_messages_consumed` and `{service}_messages_sent`, each labeled `workflowId`+`processorId` (camelCase) — remove the three legacy dedup/drop counters, and add a label-less `keeper_l2_probe` heartbeat that proves the Keeper is actively probing L2.

## Background

Three code-owned meters emit business counters today (all `Counter<long>`, snake_case, collector appends `_total`):

- **Orchestrator** (`OrchestratorMetrics`, meter `"Orchestrator"`): `orchestrator_dispatch_sent` (label `ProcessorId`) at `StepDispatcher.cs:41` + `OrchestratorResultPipeline.cs:298`; `orchestrator_result_consumed` (label `ProcessorId`) at `TypedResultConsumer.cs:62`; `orchestrator_result_deduped` — **dormant, no increment site**.
- **Processor** (`ProcessorMetrics`, meter `"BaseProcessor"`, inherited by every `Processor.*`): `processor_dispatch_consumed` (label `ProcessorId`) at `EntryStepDispatchConsumer.cs:37`; `processor_result_sent` (labels `ProcessorId`+`outcome`) at `ProcessorPipeline.cs:382`; `processor_dispatch_deduped` at the `flag[H]=="Ack"` drop gate.
- **Keeper** (`KeeperMetrics`, meter `"Keeper"`): `keeper_reinject_dropped` (no labels) at `ProcessorReinjectConsumer.cs:38`. The Keeper has **no** consumed/sent counters today.

Label keys are PascalCase (`ProcessorId`) deliberately for `sum by (ProcessorId)`, and `workflowId` was explicitly excluded for cardinality (comments cite D-03/SPEC). The metric names + the `outcome` label are hard-coded in the live-stack analyzer (`PromCounterSnapshot.cs`, `PassFailEngine.cs`) and ~11 metric test files. All three meter **names** stay the same (`Orchestrator`/`BaseProcessor`/`Keeper`), so the `AddMeter(...)` registrations are unchanged — only instrument names, labels, and increment sites change.

Decision context: there are only a few workflows × few processors in the deployment, so `workflowId`+`processorId` cardinality is acceptable for Prometheus. Names changed wholesale, so no existing PromQL/dashboard compatibility needs preserving → camelCase label keys are adopted to match the new convention.

## Requirements

1. **Orchestrator uniform counters**: Replace the orchestrator's two counters with the uniform pair and drop the dormant dedup counter.
   - Current: `orchestrator_dispatch_sent` + `orchestrator_result_consumed` (label `ProcessorId` only) and the dormant `orchestrator_result_deduped`
   - Target: `orchestrator_messages_consumed` (incremented in `TypedResultConsumer.Consume`, every consumed result) and `orchestrator_messages_sent` (incremented after **every** successful outbound broker Send — `StepDispatcher.DispatchAsync`, `OrchestratorResultPipeline.SendDispatch`, `SendResult`, `SendKeeper`); both labeled `workflowId`+`processorId` (camelCase); `orchestrator_result_deduped` removed from `OrchestratorMetrics`
   - Acceptance: Prometheus shows `orchestrator_messages_consumed_total` and `orchestrator_messages_sent_total` carrying `workflowId` and `processorId` labels; no series named `orchestrator_dispatch_sent_*`, `orchestrator_result_consumed_*`, or `orchestrator_result_deduped_*` is emitted; `OrchestratorMetrics` has no `ResultDeduped` member

2. **Processor uniform counters**: Replace the processor framework's two counters with the uniform pair, drop the `outcome` label, and remove the dedup counter.
   - Current: `processor_dispatch_consumed` (label `ProcessorId`) + `processor_result_sent` (labels `ProcessorId`+`outcome`) + `processor_dispatch_deduped`
   - Target: `processor_messages_consumed` (incremented in `EntryStepDispatchConsumer.Consume`) and `processor_messages_sent` (incremented after the successful send in `ProcessorPipeline.SendResult`); both labeled `workflowId`+`processorId` only (the `outcome` label is removed); `processor_dispatch_deduped` removed from `ProcessorMetrics`
   - Acceptance: Prometheus shows `processor_messages_consumed_total` and `processor_messages_sent_total` with `workflowId`+`processorId` and **no** `outcome` label; no series named `processor_dispatch_consumed_*`, `processor_result_sent_*`, or `processor_dispatch_deduped_*` is emitted; `ProcessorMetrics` has no `DispatchDeduped` member

3. **Keeper uniform counters**: Add the uniform pair to the Keeper at the single consume choke point + each send site, and remove the legacy drop counter.
   - Current: only `keeper_reinject_dropped` (no labels); no consumed/sent counters
   - Target: `keeper_messages_consumed` incremented once in `RecoveryConsumerBase.Consume` (the single method all 5 recovery consumers funnel through — REINJECT/INJECT for both processor and orchestrator, plus DELETE) and `keeper_messages_sent` incremented after **every** successful outbound Send across the 4 sending consumers (`ProcessorReinjectConsumer`, `OrchestratorInjectConsumer`, `ProcessorInjectConsumer`, `OrchestratorReinjectConsumer`; DELETE sends nothing); both labeled `workflowId`+`processorId` (from `IKeeperRecoverable`); `keeper_reinject_dropped` removed from `KeeperMetrics`
   - Acceptance: Prometheus shows `keeper_messages_consumed_total` and `keeper_messages_sent_total` with `workflowId`+`processorId`; a recovery flow that consumes 5 messages and sends 4 reflects those counts; no series named `keeper_reinject_dropped_*` is emitted; `KeeperMetrics` has no `ReinjectDropped` member

4. **Keeper L2-probe heartbeat**: Add a label-less counter that increments once per BIT probe tick.
   - Current: no metric reflects whether the Keeper's `BitHealthLoop` is probing L2 (only logs on health-edge transitions)
   - Target: `keeper_l2_probe` (no labels) incremented once per loop iteration in `BitHealthLoop.ExecuteAsync` (one increment per `probe.ProbeOnceAsync` tick, regardless of healthy/unhealthy result)
   - Acceptance: `rate(keeper_l2_probe_total[5m]) > 0` while the Keeper runs; the counter advances every `Probe:DelaySeconds` tick and carries no labels

5. **Analyzer + test contract rework**: Update the live-stack analyzer and metric tests to the new names and the dropped `outcome` label.
   - Current: `PromCounterSnapshot.cs` + `PassFailEngine.cs` and ~11 metric test files key on the old metric names and the `processor_result_sent{outcome="completed"}` breakdown
   - Target: analyzer snapshot fields + pass/fail logic reference the new `*_messages_consumed`/`*_messages_sent` names; the completed-vs-non-completed `outcome` split is removed/reworked (the corroboration WARNING path is dropped or repointed); all affected metric facts updated to assert the new names/labels and the absence of the three removed counters
   - Acceptance: the metric-facts test subset is GREEN; `PassFailEngine` compiles and evaluates against the new snapshot with no reference to `outcome` or the removed counter names

6. **Naming + casing convention**: Apply the uniform snake_case/camelCase convention consistently and preserve the existing meter wiring.
   - Current: PascalCase label key `ProcessorId`; per-type instrument names; meter names `Orchestrator`/`BaseProcessor`/`Keeper`
   - Target: instrument names are `{service}_messages_consumed`/`{service}_messages_sent` (snake_case, **no** `_total` suffix in code — collector appends it); label keys are camelCase `workflowId`/`processorId`; meter names and `AddMeter(...)` registrations are unchanged; `KeeperMetrics` is injected into the 4 sending consumers + `BitHealthLoop` (ctor additions) following the existing `IMeterFactory` DI pattern (never a static `Meter`)
   - Acceptance: every new instrument name is snake_case with no embedded `_total`; every label key is camelCase; meter names remain `Orchestrator`/`BaseProcessor`/`Keeper`; 0-warning Debug + Release build

## Boundaries

**In scope:**
- Rewriting the three metric classes (`OrchestratorMetrics`, `ProcessorMetrics`, `KeeperMetrics`) to the uniform counter pair + removals
- New `keeper_l2_probe` heartbeat counter in `BitHealthLoop`
- Moving/adding increment sites: orchestrator (every outbound Send), processor (consume + send), keeper (base `Consume` choke point + 4 send sites)
- Adding `workflowId`+`processorId` (camelCase) labels at every consumed/sent increment
- Removing `orchestrator_result_deduped`, `processor_dispatch_deduped`, `keeper_reinject_dropped` and the processor `outcome` label
- Injecting `KeeperMetrics` into the 4 sending keeper consumers + `BitHealthLoop`
- Reworking `PassFailEngine` + `PromCounterSnapshot` + the ~11 affected metric test files

**Out of scope:**
- Changing meter names or `AddMeter(...)` registrations — meter names stay `Orchestrator`/`BaseProcessor`/`Keeper` (the registrations already cover the renamed instruments)
- Infrastructure instrumentation (AspNetCore/HttpClient/Runtime) and the WebAPI — it has no business counters and gains none this phase
- Resource labels (`service.instance.id`, `service.name={name}_{version}`) — unchanged
- Healthy-vs-unhealthy L2 probe breakdown — `keeper_l2_probe` is a label-less heartbeat only (a status gauge is a possible future item, deliberately excluded since the counter must be label-less)
- Collector/exporter config changes — the prometheus exporter's `_total` suffix behavior is relied on as-is
- Dashboards / Grafana / alerting rules — not maintained in this repo

## Constraints

- Instrument names MUST be snake_case with NO `_total` suffix in code (the collector's prometheus exporter appends it); label keys MUST be camelCase (`workflowId`, `processorId`).
- Meters MUST be built via `IMeterFactory` (existing DI pattern) — never a static `Meter` (hermetic test isolation).
- `workflowId`+`processorId` cardinality is accepted as bounded (few workflows × few processors); no cardinality guard is added.
- All four meter-name `AddMeter(...)` registrations remain unchanged.
- Build MUST be 0-warning in both Debug and Release.
- Verification is fully automated (machine-verified), never labeled "human verification."

## Acceptance Criteria

- [ ] `orchestrator_messages_consumed_total` + `orchestrator_messages_sent_total` emitted with `workflowId`+`processorId`; `orchestrator_dispatch_sent_*`/`orchestrator_result_consumed_*`/`orchestrator_result_deduped_*` absent
- [ ] `processor_messages_consumed_total` + `processor_messages_sent_total` emitted with `workflowId`+`processorId` and no `outcome` label; `processor_dispatch_consumed_*`/`processor_result_sent_*`/`processor_dispatch_deduped_*` absent
- [ ] `keeper_messages_consumed_total` + `keeper_messages_sent_total` emitted with `workflowId`+`processorId`; `keeper_reinject_dropped_*` absent
- [ ] `keeper_messages_sent` increments on **every** outbound Send (orchestrator forward dispatch + result re-send + keeper escalation; keeper's 4 inject/reinject sends), not just primary-path sends
- [ ] `keeper_l2_probe_total` increments once per BIT probe tick and carries no labels
- [ ] `OrchestratorMetrics`/`ProcessorMetrics`/`KeeperMetrics` contain only the new members (no `ResultDeduped`/`DispatchDeduped`/`ReinjectDropped`)
- [ ] `PassFailEngine` + `PromCounterSnapshot` reference the new names and no longer key on `outcome` or the removed counters
- [ ] Hermetic metric-facts test subset GREEN + 0-warning Debug & Release build
- [ ] Live-stack `PassFailEngine` close gate run on the Docker stack (net-zero sweep) passes — deferred-automated where the sandbox lacks Docker

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                        |
|--------------------|-------|------|--------|--------------------------------------------------------------|
| Goal Clarity       | 0.93  | 0.75 | ✓      | Exact counters, names, labels, removals, probe all decided   |
| Boundary Clarity   | 0.88  | 0.70 | ✓      | "Every outbound Send" scope locked; out-of-scope explicit    |
| Constraint Clarity | 0.82  | 0.65 | ✓      | snake_case/camelCase, cardinality accepted, meter names kept |
| Acceptance Criteria| 0.85  | 0.70 | ✓      | Live-stack close gate (deferred-automated) mechanism locked  |
| **Ambiguity**      | 0.12  | ≤0.20| ✓      |                                                              |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                          | Decision locked                                                      |
|-------|-----------------|-------------------------------------------|---------------------------------------------------------------------|
| 0     | (prior convo)   | Processor/Keeper counter naming?          | Direction-only uniform names `*_messages_consumed`/`*_messages_sent` |
| 0     | (prior convo)   | `workflowId` cardinality + label casing?  | Add both labels, camelCase (cardinality bounded, names all change)   |
| 0     | (prior convo)   | What does the "BIT" counter measure?      | Label-less `keeper_l2_probe` heartbeat — proves Keeper is probing L2 |
| 1     | Boundary Keeper | `*_messages_sent` scope?                  | Every successful outbound broker Send (incl. recovery/escalation)    |
| 1     | Failure Analyst | Acceptance verification mechanism?        | Live-stack `PassFailEngine` close gate, deferred-automated           |

---

*Phase: 74-reshape-business-metrics-into-a-uniform-two-counter-model*
*Spec created: 2026-06-17 (imported from drafted slug "72")*
