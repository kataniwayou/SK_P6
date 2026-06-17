---
phase: 72
slug: processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
status: verified
threats_open: 0
asvs_level: 1
created: 2026-06-17
---

# Phase 72 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| author `ExecuteAsync` → pipeline | An author exception (incl. a deserialize `JsonException` carrying payload fragments) crosses into the failure-result builder | exception detail (sensitive — may echo payload) |
| pipeline → L2 (Redis) | `validatedData` and failure-path `dr.Data` are written to `L2[out:messageId]` on more outcomes than before | already-validated input blob (same sensitivity as existing `L2[data:entryId]`) |
| pipeline → orchestrator-result queue | The `Step*` wire record carries `ErrorMessage` to the orchestrator | error diagnostic (must be sanitized constant, not `ex.Message`) |
| inbound result `WorkflowId` → metric label | `orchestrator_step_unresolved` `workflowId` tag value derives from the inbound message's `WorkflowId` (a Guid) | bounded authored-workflow Guid (cardinality source) |
| orchestrator → Redis (L2 read/delete) | The `out:` blob read/delete now runs for every outcome; a fault must escalate (REINJECT), a clean-absent must not | relocated blob; keeper-escalation control flow |
| orchestrator → result-post queue | `NextStepHandoff` carries `Data` (the relocated blob) + `ExecutionId` downstream | relocated blob + execution id |
| in-process meter → MeterListener (test) | The test seam subscribes to the in-process `Orchestrator` meter only (hermetic, no network) | metric measurements (test-only) |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-72-01 | Information Disclosure | ProcessorPipeline unexpected/deser catch | mitigate | Sanitized constant `"input deserialization failed"` on the `ErrorMessage` wire field; `ex` only to local `LogWarning`. Verified `ProcessorPipeline.cs:136` (wire), `:132` (log), `DataResult.cs:20`, `OutputTail.cs:92`. No `ex.Message` on the wire. | closed |
| T-72-02 | Information Disclosure | always-write blob on failure paths | accept | `dr.Data = validatedData` is the already-validated input already in `L2[data:entryId]`; written to `L2[out:messageId]` under the existing jittered `OutputDataTtl`. Verified `ProcessorPipeline.cs:88,111,136` + `OutputTail.cs:66,40-41`. | closed |
| T-72-03 | Denial of Service | blob-write amplification (Failed/Cancelled now write) | accept | Widened gate `result != Processing` adds one bounded `out:` blob per failure, reaped by the unchanged jittered TTL; no new external trigger. Verified `OutputTail.cs:63,66`. | closed |
| T-72-04 | Tampering | INJECT-on-write-exhaust on the new failure-write path | mitigate | The widened gate retains the verbatim `RetryLoop.ExecuteAsync(StringSetAsync)` → `if (!write.Succeeded) SendKeeper(BuildInject(...))` escalation; failure-path write-exhaust → keeper INJECT identically to the former Completed path (no silent loss). Verified `OutputTail.cs:63-71`. | closed |
| T-72-05 | Denial of Service | `orchestrator_step_unresolved` label cardinality | accept | Single label `workflowId` = `m.WorkflowId.ToString("D")` — a bounded authored-workflow Guid set, no free-text/`reason`/`stage` label (label-minimal by spec). Verified `OrchestratorPrePipeline.cs:78,144`. | closed |
| T-72-06 | Spoofing/Tampering | counter built via static `Meter` leaking across hermetic tests | mitigate | Built via `meterFactory.Create(MeterName)` → `CreateCounter<long>("orchestrator_step_unresolved")`; no static `Meter` field (only the doc-comment warning). Verified `OrchestratorMetrics.cs:51-58`. MeterListener seam is per-test `IDisposable`. | closed |
| T-72-07 | Denial of Service | `orchestrator_step_unresolved` label cardinality (stage-3 site) | accept | Duplicate of T-72-05; same single `workflowId` label. Verified `OrchestratorPrePipeline.cs:78,144`. | closed |
| T-72-08 | Denial of Service | stage-3 dangling-id loop | mitigate | `foreach (var unresolvedId in selection.UnresolvedIds) { log; StepUnresolved.Add(...); continue; }` — uses `continue`, contains no `throw`; a malformed graph cannot wedge the consumer or trigger redelivery storms (preserves T-24-06 no-throw). Verified `OrchestratorPrePipeline.cs:139-146`. | closed |
| T-72-09 | Tampering / Repudiation | clean-absent mistaken for fault (or vice-versa) | mitigate | Read lambda returns `null` on `raw.IsNullOrEmpty`; thrown `RedisException` → loop-exhaust → `SendKeeper(BuildReinject(...))` + return; clean-absent (`IsNullOrEmpty(read.Value)`) → log + bare `return`, NO keeper. Prevents spurious REINJECT amplification AND silent loss. Verified `OrchestratorPrePipeline.cs:104-116`. | closed |
| T-72-10 | Information Disclosure | trip-end / dangling-id logs | mitigate | All new logs reference ids only (`WorkflowId`/`StepId`/`NextStepId`) — never `relocated`/`read.Value`/`Data` or author payload. Verified `OrchestratorPrePipeline.cs:112-114,141-143,75-77,89-91`. | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-72-01 | T-72-02 | `dr.Data = validatedData` is the already-validated input that already lives in `L2[data:entryId]`; writing it to `L2[out:messageId]` exposes nothing new and carries the existing jittered `OutputDataTtl`. Logs still reference ids only (T-70-10). | User (gsd-secure-phase) | 2026-06-17 |
| AR-72-02 | T-72-03 | Failure paths now write one extra bounded `out:` blob each (previously zero); volume bounded by existing message throughput and reaped by the unchanged jittered TTL — no unbounded growth, no new external trigger. | User (gsd-secure-phase) | 2026-06-17 |
| AR-72-03 | T-72-05 | The single metric label is `workflowId` (a bounded set of authored workflow Guids), not free-form/untrusted; label-minimal by spec (no `reason`/`stage`). Increment volume bounded by L1-resolution-miss frequency. | User (gsd-secure-phase) | 2026-06-17 |
| AR-72-04 | T-72-07 | Duplicate of AR-72-03 — same single `workflowId` label at the stage-3 increment site; label-minimal, bounded cardinality. | User (gsd-secure-phase) | 2026-06-17 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-06-17 | 10 | 10 | 0 | gsd-security-auditor (ASVS L1, block_on: high) |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-06-17
