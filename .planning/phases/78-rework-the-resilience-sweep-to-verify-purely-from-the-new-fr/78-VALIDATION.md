---
phase: 78
slug: rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-07-16
---

# Phase 78 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Two-tier: hermetic xUnit facts are the primary sampling grid over the analyzer's decision branches (run every commit); the live RealStack sweep is the terminal acceptance gate (run once, operator-gated, after the mandatory reseed).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (Microsoft.Testing.Platform), net8.0 |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` (run the .exe directly — `dotnet test` hangs on Windows MTP) |
| **Full suite command** | same .exe, no filter (RealStack facts require Docker + reseed; skipped in hermetic runs) |
| **Estimated runtime** | hermetic ~seconds; live sweep = minutes per scenario × 7 |

**Baseline note:** the full hermetic suite carries ~272 pre-existing Docker-less failures. The gate is **zero NEW failures in the changed scope** (analyzer facts), not an absolute green count. See [[hermetic-test-command]].

---

## Sampling Rate

- **After every task commit:** Run the hermetic analyzer facts (`--filter-not-trait Category=RealStack`, optionally `--filter-namespace *Observability.Analysis*`).
- **After every plan wave:** Run the full hermetic subset; confirm 0 new failures in changed scope.
- **Before live sweep:** hermetic analyzer facts fully green (all decision branches covered).
- **Max feedback latency:** hermetic seconds.

---

## Per-Task Verification Map

*Planner fills this from the decision-branch inventory. The sampling grid must cover every surviving `PassFailEngine` decision branch (structural completeness via explicit `expectedStepIdsByExecution`, ANL-03 telemetry-gap path #2, duplicate fail-closed, MG-1/2/3 metric gate, three-class verdict incl. INCONCLUSIVE) with synthetic `RunTrace`/`PromCounterSnapshot` inputs — the Nyquist grid over the branches. Deleted branches (value-chain path #1, WR-02, entry-marker D09) have their facts removed, not left dangling.*

| Task ID | Plan | Wave | Decision branch | Test Type | Automated Command | Status |
|---------|------|------|-----------------|-----------|-------------------|--------|
| _tbd_ | _tbd_ | _tbd_ | _tbd_ | hermetic fact | quick run command | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- Existing infrastructure covers all phase requirements — `BaseApi.Tests` + the analyzer fact suites already exist. No framework install needed. Wave 0 here is the RED step: the migrated `PassFailEngineFacts` (explicit stepId expected-set) and the removal of `PassFailEngineValueChainFacts` / the D09 entry-marker fact land as one atomic change so the tree never goes red mid-way.

---

## Manual-Only Verifications

| Behavior | Why Manual | Test Instructions |
|----------|------------|-------------------|
| Live 7-scenario baseline reproduction (D-04) | RealStack, Docker-gated, requires SourceHash reseed + operator gate; not part of the hermetic sampling grid | Reseed per [[rebuild-sourcehash-reseed-order]] (rebuild both host configs + `docker compose build` → graph-delete → seed → 204), then `pwsh -File scripts/phase-68-sweep.ps1`; assert every scenario reproduces its `git show HEAD:analyzer-reports/phase-68-summary.json` verdict (all 7 PASS). Watch the stale-Debug-report and cold-ES TEST-01 INCONCLUSIVE≠FAIL traps ([[phase-68-sweep-stale-report-read]]). |

---

## Validation Sign-Off

- [ ] Every surviving decision branch has a hermetic fact (sampling grid complete)
- [ ] Deleted branches have their facts removed atomically (no dangling red)
- [ ] Live sweep manual verification documented with reseed order + baseline source (HEAD)
- [ ] No new hermetic failures in changed scope
- [ ] `nyquist_compliant: true` set in frontmatter after planner fills the per-task map

**Approval:** pending
