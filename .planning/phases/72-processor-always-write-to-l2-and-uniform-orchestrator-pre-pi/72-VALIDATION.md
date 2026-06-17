---
phase: 72
slug: processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
status: approved
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-17
---

# Phase 72 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Source: 72-RESEARCH.md "Validation Architecture" (all seams verified against live code).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit **v3** (`TestContext.Current.CancellationToken`) + NSubstitute |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (single test project: processor + orchestrator facts) |
| **Quick run command** | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OutputTailFacts\|FullyQualifiedName~PrePipelineFacts\|FullyQualifiedName~OrchestratorPrePipelineFacts\|FullyQualifiedName~StepAdvancementTests\|FullyQualifiedName~OrchestratorMetrics"` |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` |
| **Build gate** | `dotnet build -c Debug -warnaserror` AND `dotnet build -c Release -warnaserror` |
| **Estimated runtime** | ~30–60 seconds (hermetic; no Redis/Docker) |

---

## Sampling Rate

- **After every task commit:** Run the quick-run filter (the 5 affected fact classes).
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests` (full suite) + `dotnet build -c Release -warnaserror`.
- **Before `/gsd-verify-work`:** Full suite GREEN + 0-warning Debug & Release.
- **Max feedback latency:** ~60 seconds.

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 72-01-T1 | 01 | 1 | SPEC-1, SPEC-2 | — | always-writes terminal blob; no payload in logs (WR-03) | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OutputTailFacts"` | ✅ | ⬜ pending |
| 72-01-T2 | 01 | 1 | SPEC-2, SPEC-3 | T-72 data-exposure | catch→OutputTail carries `validatedData`; sanitized error constant preserved | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~PrePipelineFacts"` | ✅ | ⬜ pending |
| 72-02-T1 | 02 | 1 (Wave-0 seam) | SPEC-4 (infra) | — | metric-capture helper (`MeterListener`, zero new deps) | infra/unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OrchestratorPrePipelineFacts"` | ❌ W0 | ⬜ pending |
| 72-02-T2 | 02 | 1 | SPEC-5 | — | `SelectNext` pure no-I/O → `{Matches, UnresolvedIds}`; call-sites migrated | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~StepAdvancementTests"` | ✅ | ⬜ pending |
| 72-02-T3 | 02 | 1 | SPEC-4, SPEC-6 | T-72 label-cardinality | `orchestrator_step_unresolved` snake_case, `workflowId`-only label, `IMeterFactory` | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OrchestratorMetrics"` | ✅ | ⬜ pending |
| 72-03-T1 | 03 | 2 | SPEC-5 | T-72 clean-absent vs fault | branch-free read: present→fan-out+delete / clean-absent→ack-skip-no-keeper / fault→REINJECT; ExecutionId threaded | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OrchestratorPrePipelineFacts"` | ✅ | ⬜ pending |
| 72-03-T2 | 03 | 2 | SPEC-4, SPEC-6 | T-72 label-cardinality | metrics ctor-injected; stage-1 (return) + stage-3 (continue) increments; terminal/condition-skip/fan-out do NOT increment | unit | `dotnet test tests/BaseApi.Tests --filter "FullyQualifiedName~OrchestratorPrePipelineFacts"` | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] **Metric-capture seam** — a `MeterListener` test helper in `tests/BaseApi.Tests/Orchestrator/` subscribing to `instrument.Meter.Name == "Orchestrator" && instrument.Name == "orchestrator_step_unresolved"`, accumulating `(value, tags)` (zero new package deps — RESEARCH A1 Option B; the planner selected this in 72-02 Task 1). Covers SPEC-4 metric assertions consumed by 72-03 T2.
- [ ] (If reshaping `SelectNext`) — no new file; `StepAdvancementTests` migrates in-place to `.Matches`/`.UnresolvedIds` (72-02 T2).

*All other phase requirements are covered by existing fact classes + the existing `DispatchTestKit` / `OrchestratorPrePipelineFacts` doubles — extend in-place per D-12.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live-stack proof (uniform pipeline + always-write + counter emission to Prometheus) | SPEC AC #10 (line 92) | Sandbox lacks Docker — deferred-automated | Bring up the live stack, drive `Failed`/`Cancelled` results through a real workflow, scrape `orchestrator_step_unresolved_total` and confirm the `workflowId` label + stage-1/stage-3 increments |

*All hermetic phase behaviors have automated verification; only the live-stack close-gate is deferred.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (the single metric-capture seam)
- [x] No watch-mode flags
- [x] Feedback latency < 60s
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-17 (per-task map populated from RESEARCH Validation Architecture; `wave_0_complete` flips true once 72-02 Task 1 lands the MeterListener seam)
