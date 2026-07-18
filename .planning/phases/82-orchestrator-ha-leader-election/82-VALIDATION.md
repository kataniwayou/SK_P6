---
phase: 82
slug: orchestrator-ha-leader-election
status: partial
nyquist_compliant: false
wave_0_complete: true
created: 2026-07-18
---

# Phase 82 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Reconstructed post-execution (State B — no VALIDATION.md was created pre-plan because research/Nyquist artifacts were intentionally skipped; the SPEC's per-requirement hermetic acceptance criteria served as the validation contract).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit.v3 on Microsoft.Testing.Platform (MTP) |
| **Config file** | none — project-based (`tests/BaseApi.Tests/BaseApi.Tests.csproj`) |
| **Quick run command** | `dotnet build tests/BaseApi.Tests -c Debug && tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-class "*LeaderStateTransitionTests" --filter-class "*OrchestratorRoleEnricherTests" --filter-class "*WorkflowFireJobGateTests"` |
| **Full suite command** | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (hermetic subset) |
| **Estimated runtime** | Phase-82 classes ~1–2 s; hermetic subset ~seconds |

> ⚠ Runner convention (project memory): run `BaseApi.Tests.exe` **directly** — `dotnet test` hangs on Windows MTP. `Category=RealStack` is the compose-backed integration subset (fails-loud when Docker is down — the known pre-existing baseline, NOT a phase-82 regression).

---

## Sampling Rate

- **After every task commit:** Run the quick command (the phase-82 test classes).
- **After every plan wave:** Run the hermetic full-suite command.
- **Before `/gsd-verify-work`:** Hermetic subset must be green (it is — 10/10 phase-82 tests pass).
- **Max feedback latency:** ~5 s (hermetic).

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 82-04-03 | 04 | 3 | HA-01 | T-82-05 | Follower/cold-leader emits ZERO `DispatchAsync` sends yet still refreshes L1 + reschedules; hydrated leader dispatches | unit | `BaseApi.Tests.exe --filter-class "*WorkflowFireJobGateTests"` | ✅ | ✅ green |
| 82-04-01 | 04 | 3 | HA-02 | T-82-13 | Default/unstarted `LeaderState` is follower (`IsLeader==false`, `Role=="follower"`) | unit | `BaseApi.Tests.exe --filter-class "*LeaderStateTransitionTests"` | ✅ | ✅ green |
| 82-04-01 | 04 | 3 | HA-03 | T-82-02 | Snapshot flips leader↔follower via the callback writers only (single-writer behavior) | unit | `BaseApi.Tests.exe --filter-class "*LeaderStateTransitionTests"` | ✅ | ✅ green |
| 82-04-01 | 04 | 3 | HA-04 | T-82-05 | `OnStoppedLeading`→follower; `RenewDeadline (10s) < LeaseDuration (15s)` fence asserted via public constants; cold-leader skip (gate test) | unit | `BaseApi.Tests.exe --filter-class "*LeaderStateTransitionTests" --filter-class "*WorkflowFireJobGateTests"` | ✅ | ✅ green |
| 82-04-02 | 04 | 3 | HA-05 | T-82-07 | `LogRecord` gains `attributes.role`=="follower"/"leader" from live state, never empty; post-construction flip shows on the next record | unit | `BaseApi.Tests.exe --filter-class "*OrchestratorRoleEnricherTests"` | ✅ | ✅ green |
| 82-03-01/02/03 | 03 | 1 | HA-06 | T-82-09/10/11 | RBAC least-privilege + `replicas:3` + `KubernetesClient` referenced | manifest/CLI | see Manual-Only | ✅ | ✅ green (CLI) |
| 82-02-02 | 02 | 2 | HA-03 | T-82-02 | `RelocateTail`/step-advancement contains ZERO `LeaderState`/`IsLeader` references (single-writer boundary) | static grep | see Manual-Only | ✅ | ✅ green (grep) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

*Existing infrastructure covers all phase requirements.* The `tests/BaseApi.Tests` project (xUnit.v3/MTP) already existed; phase 82 added three hermetic test classes (`Election/LeaderStateTransitionTests.cs`, `Observability/OrchestratorRoleEnricherTests.cs`, `Orchestrator/WorkflowFireJobGateTests.cs`, 10 tests total) reusing the established `WorkflowFireJobScopeTests` / `ProcessorIdEnricherTests` harness patterns. No new framework install required.

---

## Manual-Only Verifications

These are **deterministic CLI/static checks** (not subjective human judgment) — they were executed during phase verification and passed. They are "manual-only" only in that they are not xUnit tests; each has a repeatable command with a pass/fail exit. Unit-testing YAML manifests / source-text invariants would be brittle and would merely duplicate these checks.

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| RBAC Role grants ONLY `get,create,update` on `leases` in `coordination.k8s.io`, namespaced to `skp`; RoleBinding scoped to the `orchestrator` SA | HA-06 (T-82-09/10/11) | k8s manifest — validated by schema dry-run, not unit-testable in-process | `kubectl apply --dry-run=client -k k8s/` (exit 0); `grep -A3 'resources:' k8s/34-orchestrator-rbac.yaml` shows only leases + the three verbs; confirm no `"*"`, no `ClusterRole` |
| Deployment `replicas: 3`, `RollingUpdate`, `POD_NAME` downward-API env, `serviceAccountName: orchestrator` | HA-06 | k8s manifest | `grep -E 'replicas: 3|RollingUpdate|metadata.name|serviceAccountName: orchestrator' k8s/31-orchestrator.yaml` |
| Orchestrator project references `KubernetesClient` (pinned 18.0.13, non-vulnerable) | HA-06 | build/dependency manifest | `grep KubernetesClient src/Orchestrator/Orchestrator.csproj Directory.Packages.props` |
| `RelocateTail`/step-advancement contains no `LeaderState`/`IsLeader` reference (HA-03 single-writer boundary) | HA-03 | static source invariant — the *absence* of a reference is a grep, not a runtime assertion | `grep -c -E 'LeaderState|IsLeader' src/Orchestrator/**/RelocateTail.cs` returns 0 |
| 0-warning build Debug AND Release (repo-wide `TreatWarningsAsErrors`) | all (build gate) | toolchain gate | `dotnet build SK_P.sln -c Debug` and `-c Release` (0 warnings) |

---

## Validation Sign-Off

- [x] All behavioral tasks (HA-01..HA-05) have `<automated>` xUnit verification — 10/10 green
- [x] Infra/static requirements (HA-06, HA-03 grep) have deterministic CLI/manifest verification (manual-only section)
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (none — existing infra sufficient)
- [x] No watch-mode flags
- [x] Feedback latency < 5s (hermetic)
- [ ] `nyquist_compliant: true` — **NOT set.** HA-06 + the HA-03 boundary invariant are CLI/manifest-validated, not xUnit-automated (by deliberate choice — no brittle YAML/source-text unit tests). Behavioral coverage is complete; infra coverage is deterministic-but-out-of-band.

**Approval:** partial — approved 2026-07-18 (behavioral automated, infra CLI-validated; consistent with the SPEC's build+hermetic+manifest-only phase boundary — live election is Phase 83).
