---
phase: 78
slug: rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
status: ready
nyquist_compliant: true
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
| 78-01 T1 | 01 | 1 | D-01 entry-marker excluded by ES query, NOT engine (D09 fact removed) | compile/hermetic | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` (analyzer subset green; D09 fact absent) | ⬜ pending |
| 78-01 T2 | 01 | 1 | D-02 keeper record kept out of structural cohort (`must_not exists ReinjectOutcome`) | compile + BuildKeeperOutcomeMapFacts (KEEP, symmetric) | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` | ⬜ pending |
| 78-02 T1 | 02 | 2 | D-03 value-oracle query/parse removed from fixture | compile (RealStack fixture) | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` (build 0-warning) | ⬜ pending |
| 78-02 T2 | 02 | 2 | D-03 value-chain machinery gone; ANL-03 path #2 sole reconciliation; ValueChainOk/Detail + OracleAbsent fact + PassFailEngineValueChainFacts.cs deleted | hermetic facts (deleted branches removed, not dangling) | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` (PassFailEngineValueChainFacts gone; *PassFailEngineFacts green) | ⬜ pending |
| 78-03 T1 | 03 | 3 | D-03 Hazard C: ExpectedHopOffset residue gone; ~6 label facts migrated to explicit expectedStepIdsByExecution (live stepId path) | hermetic facts (atomic) | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack --filter-namespace "*Observability.Analysis*"` | ⬜ pending |
| 78-03 T2 | 03 | 3 | Full sampling-grid gate: structural completeness + ANL-03 path #2 + MG-1/2/3 + three-class INCONCLUSIVE all covered green | hermetic gate | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack` (zero NEW failures vs ~272 baseline; 0-warning Debug+Release) | ⬜ pending |
| 78-04 T2 | 04 | 4 | D-04 live baseline reproduction — verdict from framework ES logs alone | manual/RealStack (operator-gated) | `pwsh -File scripts/phase-68-sweep.ps1` after reseed; compare to `git show HEAD:analyzer-reports/phase-68-summary.json` | ⬜ pending |

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

- [x] Every surviving decision branch has a hermetic fact (sampling grid complete — see Per-Task Verification Map)
- [x] Deleted branches have their facts removed atomically (value-chain path #1, WR-02, entry-marker D09, OracleAbsent, PassFailEngineValueChainFacts.cs — no dangling red)
- [x] Live sweep manual verification documented with reseed order + baseline source (HEAD) — Plan 78-04
- [x] No new hermetic failures in changed scope (gate = zero NEW vs ~272 baseline; enforced by 78-03 T2)
- [x] `nyquist_compliant: true` set in frontmatter (per-task map filled)

**Approval:** ready (planner filled the per-task map 2026-07-16)
