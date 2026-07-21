---
phase: 77-consistent-framework-logging-model-scope-carried-execution-i
plan: 06
subsystem: keeper
tags: [logging, keeper, recovery, execution-scope, correlation, reinject, messaging-contracts]

# Dependency graph
requires:
  - phase: 77-consistent-framework-logging-model-scope-carried-execution-i
    provides: "ExecutionLogScope.BuildState loose-id overload + IKeeperRecoverable : ICorrelated (Plan 05)"
provides:
  - "Keeper reinject consumers (processor-side ReinjectConsumer + orchestrator-side OrchestratorReinjectConsumer) open the 5-id execution scope themselves via logger.BeginScope(ExecutionLogScope.BuildState(...)) (D2/LOG-02)"
  - "REINJECT sent/drop records carry ONLY Tier-2 {MessageId} + Tier-3 {ReinjectOutcome}; Tier-1 join keys stripped, arriving via ambient scope (D1/LOG-01, D6/LOG-06)"
  - "Symmetric processor/orchestrator reinject records: both a sent (reinject) and a drop record, both scope-wrapped; orchestrator gains a NEW sent record on the confirmed-send path"
affects: [phase-78-analyzer-sweep, keeper-recovery, live-analyzer]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Keeper consumer opens its own execution scope (BeginScope wraps the whole HandleAsync body) — the bus-wide InboundExecutionScopeConsumeFilter no-ops on keeper records, so the ids must arrive from the consumer's own scope"
    - "Framework-uniform record shape: Tier-1 ids from ambient scope, Tier-2 {MessageId} on send/consume, Tier-3 {ReinjectOutcome} domain discriminator in the string"
    - "ScopeCapturingLogger<T> test double — records BeginScope states that are IEnumerable<KeyValuePair<string,object>> to prove the scope is opened (mirrors ConsoleExecutionScopeFilterTests)"

key-files:
  created: []
  modified:
    - src/Keeper/Recovery/ReinjectConsumer.cs
    - src/Keeper/Recovery/OrchestratorReinjectConsumer.cs
    - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
    - tests/BaseApi.Tests/Keeper/OrchestratorReinjectConsumerFacts.cs

key-decisions:
  - "Both reinject consumers wrap the ENTIRE HandleAsync body in a single logger.BeginScope(ExecutionLogScope.BuildState(m.WorkflowId, m.StepId, m.ProcessorId, m.ExecutionId, m.EntryId)) so every record (sent + drop) carries the 5 ids as ES attributes.* (D2/LOG-02)."
  - "Tier-1 join keys stripped from the reinject strings; only {MessageId} (the one id the scope does not carry) + {ReinjectOutcome} (drop|reinject discriminator) stay as explicit args (D1/LOG-01, D6/LOG-06)."
  - "OrchestratorReinjectConsumer gains a NEW symmetric sent record on the confirmed-send path (after CountSent), completing the send-carries-{MessageId} pattern; never on the drop path."
  - "m.Payload / the relocated blob is NEVER a log arg (FW-03 / T-77-16)."

patterns-established:
  - "Keeper records now look like every other framework record — Tier-1 from scope, {MessageId} on send/consume, domain extra otherwise."

requirements-completed: [LOG-01, LOG-02, LOG-06]

# Metrics
duration: 15min
completed: 2026-07-16
---

# Phase 77 Plan 06: Keeper Reinject Scope-Carried Execution Summary

**Both keeper reinject consumers now open the 5-id execution scope themselves and carry only Tier-2 `{MessageId}` + Tier-3 `{ReinjectOutcome}` in their strings — the keeper's records look like every other framework record (Tier-1 from scope), completing D2 (LOG-02) and the keeper half of D1 (LOG-01), plus a new symmetric orchestrator-side sent record.**

## Performance

- **Duration:** ~15 min
- **Completed:** 2026-07-16
- **Tasks:** 3
- **Files modified:** 4 (2 source, 2 test)

## Accomplishments
- `ReinjectConsumer.HandleAsync` body is now wrapped in `logger.BeginScope(ExecutionLogScope.BuildState(m.WorkflowId, m.StepId, m.ProcessorId, m.ExecutionId, m.EntryId))`; the drop template is `REINJECT drop {MessageId} {ReinjectOutcome}` and the sent template `REINJECT sent {MessageId} {ReinjectOutcome}` — the Tier-1 `{CorrelationId} {ExecutionId} {EntryId}` args are stripped (they now arrive via the ambient scope).
- `OrchestratorReinjectConsumer.HandleAsync` is scope-wrapped symmetrically; its drop record stripped from `EntryId={EntryId}` to `Orchestrator REINJECT drop {MessageId} {ReinjectOutcome}`, and a NEW `Orchestrator REINJECT sent {MessageId} {ReinjectOutcome}` record added on the confirmed-send path (after `CountSent`, never on the drop path).
- Behavior otherwise unchanged: absent/empty L2 (STRLEN==0) → by-design silent-ack drop (no send, no `CountSent`); Redis exception → Guard exhaustion escalation; present → reinject dispatch/result with the carried `m.MessageId` + `CountSent`.
- Keeper facts updated: `AssertJoinFields` now asserts the explicit state carries only `MessageId` + `ReinjectOutcome` and `DoesNotContain` CorrelationId/ExecutionId/EntryId; a new `ScopeCapturingLogger<T>` double + `Reinject_present_opens_execution_scope_with_five_ids` fact proves the 5 ids arrive via `BeginScope`; a new `Reinject_present_emits_sent_record` fact covers the orchestrator sent record.
- Phase-78 handoff documented inline in both consumers: keeper records now gain `attributes.StepId` and enter the analyzer's structural query — discriminate via `attributes.ReinjectOutcome` (only keeper records carry it).

## Task Commits

Each task was committed atomically:

1. **Task 1: Open the execution scope and strip Tier-1 in ReinjectConsumer** — `b407f89` (feat)
2. **Task 2: Symmetric scope + strip (and add the sent record) in OrchestratorReinjectConsumer** — `9f471b1` (feat)
3. **Task 3: Update keeper facts — stripped explicit state, 5-id scope fact, orchestrator sent record** — `52ebb64` (test)

## Verification

- `dotnet build SK_P.sln -c Debug` and `-c Release` both report **0 Warning(s) / 0 Error(s)**.
- Hermetic `*Reinject*` subset (`--filter-not-trait Category=RealStack`): **18/18 PASS**.
- Grep acceptance criteria for all three tasks satisfied (stripped templates present, Tier-1 join-key strings gone, `BeginScope(ExecutionLogScope.BuildState(` present in both consumers, no `m.Payload` in any log call).

## Deviations from Plan

None — plan executed exactly as written. The plan's TDD tasks were structured as source-first (Tasks 1–2, grep+build gated) then test-update (Task 3, test-run gated); this order was followed, and the build stayed 0-warning throughout with the affected facts green at Task 3.

## Deferred / Out-of-Scope

- **Live-stack RealStack test** `SC2RecoveryPathsE2ETests.LiveKeeperRecovery_AllThreeStates_ProduceTheirL2AndReinjectAndDeadLetterEffects` fails only on Postgres (5433) / RabbitMQ (5673) connection refusal — the Docker-less sandbox has no live stack. This matches the documented deferred-live-stack precedent (phases 68/73/74/75). NOT caused by this plan's changes.
- The full hermetic run surfaces the same pre-existing infra-dependent failures (Integration / Swagger / Composition / ResultAck tests that connect to Postgres/RabbitMQ without the RealStack trait). None involve the keeper reinject consumers or their facts; all are environmental (no DB/broker), and independent of this plan.

## Threat Surface

- **T-77-16 (Information disclosure)** — mitigated: reinject templates carry only `{MessageId}` + `{ReinjectOutcome}`; `m.Payload` / the relocated blob is never a log arg (grep-asserted, FW-03).
- **T-77-17 (DoS)** — mitigated: the scope `using` + log calls add no unbounded work; the by-design silent-drop ack path is preserved (no throw).
- **T-77-18 (Tampering, Phase-78 handoff)** — accepted/documented: keeper records now gain `attributes.StepId`; the live analyzer discriminates them via `attributes.ReinjectOutcome` (Phase 78). Hermetic facts build from explicit values → no hermetic regression.

No new threat surface beyond the plan's threat model.

## Self-Check: PASSED

All 4 modified files present; SUMMARY.md present; all 3 task commits (b407f89, 9f471b1, 52ebb64) found in git history.
