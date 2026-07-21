---
phase: 83
slug: orchestrator-ha-failover-proof
status: approved
nyquist_compliant: true
wave_0_complete: false
created: 2026-07-18
---

# Phase 83 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Populate the Per-Task Verification Map from the PLAN.md task list once planning completes.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit + Microsoft.Testing.Platform (MTP) — `BaseApi.Tests.exe` run directly (dotnet test hangs on Windows MTP) |
| **Config file** | existing `tests/BaseApi.Tests/*.csproj` |
| **Quick run command** | `dotnet build` (0-warning Debug+Release) + hermetic: `BaseApi.Tests.exe --filter-not-trait Category=RealStack` |
| **Full suite command** | phase-83 live proof: `pwsh -File scripts/phase-83-ha-failover.ps1` (exit 0 = PASS) |
| **Estimated runtime** | hermetic ~seconds; live proof = a single failover run (~5–8 min incl. bring-up + observation window) |

---

## Sampling Rate

- **After every task commit:** `dotnet build` (Debug+Release 0-warning) + the new hermetic `HaFireBucketScorer` / `HaFailoverAnalyzerE2ETests` unit facts.
- **After the harness/analyzer wave:** run the live `scripts/phase-83-ha-failover.ps1` proof end-to-end.
- **Before `/gsd-verify-work`:** hermetic suite green ×N; one live proof run exits 0 (PASS) — an INCONCLUSIVE (exit 2, e.g. no gap tick observed) is re-runnable, a FAIL (exit 1) is a real finding.
- **Max feedback latency:** hermetic ~seconds; live proof one run.

---

## Per-Task Verification Map

*To be filled from the finalized PLAN.md task list. Structure:*

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 83-XX-XX | XX | X | HA-07 | — | N/A (dev/ops tooling, no product surface) | unit + live | `BaseApi.Tests.exe --filter …` / `pwsh -File scripts/phase-83-ha-failover.ps1` | ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Nyquist / Sampling Adequacy (the load-bearing validation concern)

HA-07's observable claims are **sampled by ES-query buckets**, and the sampling adequacy is the crux:

- **Cron cadence vs election gap.** Entry-step fires every 30s (`*/30 * * * * *`); the force-kill election gap is ~11–17s. A randomly-timed kill lands a tick inside the gap only ~40–57% of the time → claim #3 (gap-skip) would be **unobservable** on most runs (an under-sampled signal).
- **Fix (locked by research):** **phase-align the kill** — force-delete the leader ~3s before a `:00`/`:30` boundary so the boundary tick deterministically lands in the guaranteed-minimum gap. This keeps the locked `*/30` cron + 30s bucketing intact (no product/cron change).
- **Coverage guard:** a run that never observes a gap tick (or whose empty gap bucket does not overlap `[KILL_UTC, roleFlipUtc]`) must self-report **INCONCLUSIVE (exit 2)**, never a vacuous PASS. This is the anti-vacuous-sampling rule.
- **Recovery-bound sampling:** measured from kill-time → first ES `attributes.role: follower→leader` record; threshold ≤ 2×LeaseDuration (30s), generous vs the reasoned ~11–17s bracket.

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Observability/HaFailoverAnalyzerE2ETests.cs` — new live analyzer/verdict test (mirrors `AnalyzerE2ETests` ES fetch/window/drain + `~Analyzer` env seam) — HA-07.
- [ ] A hermetically-testable `HaFireBucketScorer` (pure 30s-bucket scorer) + its unit facts — HA-07 (the ≤1-correlationId-per-bucket + gap-coverage logic, tested without a live stack).
- [ ] `scripts/phase-83-ha-failover.ps1` — the kill sequencer + verdict driver (reuses phase-80-harness STEP primitives + `scripts/lib/exit-code-resolution.ps1`).
- [ ] One `_mapping/field` probe confirming `attributes.role` is `keyword` (term-queryable) before locking the role-transition query.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live k8s failover under force-kill | HA-07 | Requires a running Docker-Desktop k8s cluster with the orchestrator scaled to 3 replicas | Run `pwsh -File scripts/phase-83-ha-failover.ps1`; verify exit 0 (PASS). An INCONCLUSIVE (exit 2) is operator-re-runnable; a FAIL (exit 1) is a real finding to investigate. |

*The hermetic `HaFireBucketScorer` facts + build gates ARE automated; only the end-to-end live failover run needs a live cluster.*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies (plan-checker Dimension 8 pass)
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (new test + scorer + script)
- [x] Gap-coverage anti-vacuous rule encoded (no gap tick → INCONCLUSIVE, never PASS)
- [x] No watch-mode flags
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-07-18 (plan-checker VERIFICATION PASSED, 0 blockers)
