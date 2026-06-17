---
phase: 71
slug: orchestrator-two-consumer-design-pre-process-post-process-wi
status: draft
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-17
---

# Phase 71 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 / Microsoft.Testing.Platform (MTP); NSubstitute for doubles |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (OutputType=Exe, `UseMicrosoftTestingPlatformRunner=true`); `tests/BaseApi.Tests/xunit.runner.json` (maxParallelThreads cap) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Estimated runtime** | ~30s for a touched-fact slice; full suite minutes (parallelism capped) |

---

## Sampling Rate

- **After every task commit:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj --filter "FullyQualifiedName~Orchestrator|FullyQualifiedName~OutputTail|FullyQualifiedName~Inject|FullyQualifiedName~Keeper"` (fast hermetic slice)
- **After every plan wave:** full `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj`
- **Before `/gsd-verify-work`:** full suite must be green (REQ-71-12 bar)
- **Max feedback latency:** < 30s for the touched-fact slice

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 71-01-01 | 01 | 1 | REQ-71-07/10 (contracts) | T-71-02 | No log emits Data/Payload (contracts have no logging) | unit | `dotnet test … --filter "FullyQualifiedName~OrchestratorContractTests"` | ❌ W0 (new) | ⬜ pending |
| 71-01-02 | 01 | 1 | REQ-71-07 (A1 both sites) | T-71-01 | A1 stamp uses processor's own envelope id, not external input | unit | `dotnet test … --filter "FullyQualifiedName~OutputTail\|FullyQualifiedName~InjectConsumer"` | ⚠️ UPDATE (InjectConsumerFacts:75, OutputTailFacts) | ⬜ pending |
| 71-01-03 | 01 | 1 | REQ-71-07/08/09/10 (contract facts) | T-71-03 | NextStepHandoff has no MessageId body field (D-11) | unit | `dotnet test … --filter "FullyQualifiedName~OrchestratorContractTests"` | ❌ W0 (new) | ⬜ pending |
| 71-02-01 | 02 | 2 | REQ-71-06/11 (RelocateTail + Post) | T-71-04 | No log emits Data/Payload; data: write TTL'd | unit | `dotnet test … --filter "FullyQualifiedName~OrchestratorPostProcess"` | ❌ W0 (new) | ⬜ pending |
| 71-02-02 | 02 | 2 | REQ-71-01/02/03/04/05/11 (Pre pipeline + metric) | T-71-04 / T-71-05 | Two-reason trip-end (no silent loss); ids-only logs; no envelope override on fan-out | unit | `dotnet test … --filter "FullyQualifiedName~OrchestratorMetrics"` | ⚠️ EXTEND OrchestratorMetricsFacts | ⬜ pending |
| 71-02-03 | 02 | 2 | REQ-71-01/02/03/04/05/11/12 (wiring + migration + Pre facts) | T-71-05 / T-71-07 | executionId equality (not regeneration); duplicate-tolerance preserved | unit | `dotnet test … --filter "FullyQualifiedName~Orchestrator"` | ⚠️ MIGRATE TypedResultConsumerFacts/ResultAckTests + ❌ W0 OrchestratorPrePipelineFacts | ⬜ pending |
| 71-03-01 | 03 | 2 | REQ-71-08/10 (REINJECT + DELETE) | T-71-09 / T-71-10 | STRLEN drop (not KeyExists); ids-only drop log; envelope override | unit | `dotnet test … --filter "FullyQualifiedName~OrchestratorReinject\|FullyQualifiedName~OrchestratorDelete"` | ❌ W0 (new) | ⬜ pending |
| 71-03-02 | 03 | 2 | REQ-71-09/11 (INJECT inline body) | T-71-11 / T-71-12 | No L1 recompute; inline body (firewall); executionId unchanged | unit | `dotnet test … --filter "FullyQualifiedName~OrchestratorInject"` | ❌ W0 (new) | ⬜ pending |
| 71-03-03 | 03 | 2 | REQ-71-11 (binder/program/partition/firewall) | T-71-12 | No Keeper→Orchestrator ProjectReference; no bus retry / no _error | unit | `dotnet test … --filter "FullyQualifiedName~Keeper"` | ⚠️ EXTEND RecoveryPartitionFacts (firewall/host-boot reused) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

The phase keeps suites green wave-to-wave by migrating each test in the SAME plan that changes the behavior it
asserts (rather than a separate up-front Wave-0 plan). New/updated test files per plan:

- [ ] Plan 01: NEW `tests/BaseApi.Tests/Contracts/OrchestratorContractTests.cs`; UPDATE `Keeper/InjectConsumerFacts.cs:75` (Guid.Empty → dr.MessageId) + `Processor/OutputTailFacts.cs` (Completed-EntryId assertion).
- [ ] Plan 02: NEW `Orchestrator/OrchestratorPrePipelineFacts.cs`, `Orchestrator/OrchestratorPostProcessConsumerFacts.cs`; EXTEND `Orchestrator/OrchestratorMetricsFacts.cs` (trip-end counter); MIGRATE `Orchestrator/TypedResultConsumerFacts.cs` + `Orchestrator/ResultAckTests.cs` (two-consumer + relocation model; ExecutionId equality, NOT regeneration — flip `Assert.NotEqual(Guid.Empty, call.ExecutionId)` at line 114).
- [ ] Plan 03: NEW `Keeper/OrchestratorReinjectConsumerFacts.cs`, `Keeper/OrchestratorInjectConsumerFacts.cs`, `Keeper/OrchestratorDeleteConsumerFacts.cs`; EXTEND `Keeper/RecoveryPartitionFacts.cs` (3 new contracts share one partition slot); REUSE `Keeper/KeeperDependencyFirewallTests.cs` + `Keeper/KeeperHostBootTests.cs` (no change, must stay green).
- [ ] Test doubles: REUSE `Orchestrator/OrchestratorTestStubs.cs` (PresentL2/AbsentL2/InfraFaultL2/Context/Metrics) for Pre/Post; REUSE `Keeper/RecoveryTestKit.cs` (Db/Mux/CapturingSendProvider+SentMessageIds/Metrics) for the keeper facts.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| — | — | — | — |

*All phase behaviors have automated verification. Container build/deploy is SPEC out-of-scope — code + `dotnet test` only this phase.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (per-plan, co-located with the behavior change)
- [x] No watch-mode flags
- [x] Feedback latency < 30s (touched-fact slice)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-17
