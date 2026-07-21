---
phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi
audited: 2026-06-17
asvs_level: none
block_on: none
result: SECURED
threats_total: 12
threats_closed: 12
threats_open: 0
---

# Phase 71 Security Audit

## Summary

All 5 `mitigate` threats verified CLOSED by grep and code read.
All 7 `accept` threats recorded CLOSED as documented accepted risks.
No unregistered threat flags in any of the three SUMMARY.md ## Threat Flags sections.

---

## Threat Verification

| Threat ID | Category | Plan | Disposition | Status | Evidence |
|-----------|----------|------|-------------|--------|----------|
| T-71-01 | Tampering | 01 | accept | CLOSED | Documented accepted risk: dr.MessageId is the MassTransit-assigned envelope id, not attacker input. No new external surface introduced. |
| T-71-02 | Information Disclosure | 01 | mitigate | CLOSED | src/Messaging.Contracts/NextStepHandoff.cs — zero Log* calls in the file. src/Messaging.Contracts/OrchestratorInject.cs — zero Log* calls. Grep `Log[A-Za-z]*.*\.(Data|Payload)` returns no matches in either contract file. |
| T-71-03 | Spoofing | 01 | accept | CLOSED | Documented accepted risk: mirrors the shipped Phase-70 keeper in-body messageId override; at-least-once posture, no dedup claimed. |
| T-71-04 | Information Disclosure | 02 | mitigate | CLOSED | src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:60-63 (LogInformation "completed-unresolved") and :71-74 (LogInformation "completed-terminal") — both log only WorkflowId, StepId, Outcome (ids). src/Orchestrator/Dispatch/RelocateTail.cs — zero Log* calls. src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs:22-28 — zero Log* calls (shell does no logging). Grep `Log[A-Za-z]*.*\.(Data|Payload)` returns no matches in any of the three files. |
| T-71-05 | Repudiation | 02 | mitigate | CLOSED | src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs:61 — `LogInformation("Trip ended (completed-unresolved): ...")` is the DISTINCT structured log for L1-miss; :72 — `LogInformation("Trip ended (completed-terminal): ...")` is the DISTINCT structured log for terminal (no-match). Both ack (return; no throw, no keeper). The literal tokens "completed-unresolved" and "completed-terminal" both appear in actual LogInformation calls (not doc-comments). Metric counter deferred by design; falsifiability carried by the two distinct log signatures as declared. |
| T-71-06 | Tampering | 02 | accept | CLOSED | Documented accepted risk: fan-out target and data: key derive from MassTransit-assigned envelope MessageId; no per-instance identity on the single competing-consumer Post queue. Internal bus, Phase-70 posture. |
| T-71-07 | Denial of Service / Tampering | 02 | accept | CLOSED | Documented accepted risk: by-design at-least-once duplicate tolerance; a redelivered result re-fans-out and re-writes the same data: key idempotently. |
| T-71-08 | Tampering | 02 | accept | CLOSED | Documented accepted risk: orchestrator is a transport relay; downstream processor validates input against schema on read (ProcessorJsonSchemaValidator). No new validation gap. |
| T-71-09 | Information Disclosure | 03 | mitigate | CLOSED | src/Keeper/Recovery/OrchestratorReinjectConsumer.cs:43 — LogWarning("Orchestrator REINJECT drop: out: gone EntryId={EntryId}", m.EntryId) — logs EntryId only, no Data/Payload. src/Keeper/Recovery/OrchestratorInjectConsumer.cs — zero Log* calls (grep confirmed: no matches). src/Keeper/Recovery/OrchestratorDeleteConsumer.cs — zero Log* calls (grep confirmed: no matches). Grep `Log[A-Za-z]*.*\.(Data|Payload)` returns no matches in all three consumers. |
| T-71-10 | Spoofing | 03 | accept | CLOSED | Documented accepted risk: mirrors the shipped Phase-70 keeper envelope override (ReinjectConsumer.cs:58, InjectConsumer.cs:57); re-asserts the carried recovery id across the recovery hop — the documented mechanism. |
| T-71-11 | Tampering | 03 | accept | CLOSED | Documented accepted risk: data: key derives from the orchestrator-assigned MessageId; gate-open-only partitioned endpoint governed by BIT health gate; downstream processor re-validates input on read. |
| T-71-12 | Elevation of Privilege | 03 | mitigate | CLOSED | src/Keeper/Keeper.csproj — ProjectReference entries are `BaseConsole.Core` and `Messaging.Contracts` only. Grep `ProjectReference.*Orchestrator` returns no matches. The two "Orchestrator" literal occurrences in Keeper.csproj are pre-existing prose comments, not ProjectReferences (grep `ProjectReference.*Orchestrator` == 0 confirmed). KeeperDependencyFirewallTests green per 71-03-SUMMARY.md self-check (21/21 Keeper slice). OrchestratorInjectConsumer.cs implements the relocate body INLINE (src/Keeper/Recovery/OrchestratorInjectConsumer.cs:43-60). |

---

## Unregistered Threat Flags

None. All three SUMMARY.md `## Threat Flags` sections explicitly state "None" with a brief disposition note confirming the plan's registered threats were satisfied in code. No unregistered flags to record.

---

## Accepted Risks Log

The following 7 threats are accepted risks documented in the phase threat models. They require no mitigation in code and no re-scan.

| Threat ID | Category | Rationale |
|-----------|----------|-----------|
| T-71-01 | Tampering | A1 stamp swaps a placeholder for the real key (MassTransit envelope id, not attacker input). No new external surface. Phase-70 posture. |
| T-71-03 | Spoofing | In-body messageId override mirrors the shipped Phase-70 keeper mechanism. At-least-once posture; no inbox/dedup claimed. |
| T-71-06 | Tampering | Fan-out target and data: key derive from MassTransit-assigned envelope MessageId. Single competing-consumer Post queue has no per-instance identity so the processor self-check does not transfer. Internal bus. |
| T-71-07 | Denial of Service / Tampering | At-least-once duplicate replay tolerated by design. A redelivered result re-fans-out idempotently (same data: key, TTL'd). |
| T-71-08 | Tampering | Orchestrator is a transport relay; downstream processor validates input on read. No new validation gap. |
| T-71-10 | Spoofing | Envelope MessageId override on REINJECT/INJECT mirrors the shipped Phase-70 keeper override; re-asserts the carried recovery id. Not a spoofing vector. |
| T-71-11 | Tampering | data: key derives from the orchestrator-assigned MessageId. Gate-open-only partitioned endpoint governed by BIT health gate. Downstream processor re-validates. |
