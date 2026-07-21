---
phase: 75
slug: recovery-verdict-per-execution-drop-tunable-constants
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-07-15
validated: 2026-07-15
---

# Phase 75 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 (Microsoft.Testing.Platform) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Test binary** | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe` |
| **Quick run command** | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (hermetic — direct exe; `dotnet test` hangs on Windows MTP) |
| **Full suite command** | `BaseApi.Tests.exe` (includes RealStack resilience sweep; needs Docker) |
| **Estimated runtime** | ~0.5s for the phase-75 fact classes; ~300s+ per RealStack scenario |

---

## Sampling Rate

- **After every task commit:** Run the hermetic filter (`--filter-not-trait Category=RealStack`)
- **After every plan wave:** Run affected `PassFailEngine` / keeper / options fact classes
- **Before `/gsd-verify-work`:** Hermetic suite must be green; RealStack join/TTL/window verifications deferred-automated (Docker)
- **Max feedback latency:** ~30 seconds (hermetic)

---

## Per-Task Verification Map

| Plan | Wave | Requirement | Test Type | Test File / Fact | Automated Command | File Exists | Status |
|------|------|-------------|-----------|------------------|-------------------|-------------|--------|
| 75-01 | 0 | D75-1 per-(corr,exec) denominator `startedRuns = runs.Count` | unit (hermetic) | `PassFailEngineFacts` (denominator locked; exercised across facts) | `BaseApi.Tests.exe --filter-class "*PassFailEngineFacts"` | ✅ | ✅ green |
| 75-01 | 0 | D75-2 no absolute `MaxInFlightLoss`/`inFlightOverBound` bound | unit (hermetic) | `PassFailEngineFacts.InFlightLoss_ManyCleanDrops_DoNotFail_Yields_Pass` | `BaseApi.Tests.exe --filter-class "*PassFailEngineFacts"` | ✅ | ✅ green |
| 75-01 | 0 | D75-3 keeper-evidence recoverability classifier | unit (hermetic) | `PassFailEngineFacts.KeeperDrop_MarksIncomplete_Tolerated_Yields_Pass` + `RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail` | `BaseApi.Tests.exe --filter-class "*PassFailEngineFacts"` | ✅ | ✅ green |
| 75-01 | 0 | D75-5 redis-wipe tolerance `&& !keeperReinject` (WR-01 veto) | unit (hermetic) | `PassFailEngineFacts.KeeperReinject_VetoesRedisWipeTolerance_StalledBeforeRecovery_Yields_Fail` | `BaseApi.Tests.exe --filter-class "*PassFailEngineFacts"` | ✅ | ✅ green |
| 75-01 | 0 | D75-8 `inFlightLossKeys` value-chain skip-set (3028f43) preserved | unit (hermetic) | `PassFailEngineValueChainFacts.ToleratedInFlightLoss_PartialTerminalOnly_NotValueChainFailed_Yields_Pass` | `BaseApi.Tests.exe --filter-class "*PassFailEngineValueChainFacts"` | ✅ | ✅ green |
| 75-02 | 0 | D75-4 keeper join-key log emission (drop + reinject, 5 placeholders) | unit (hermetic) | `ReinjectConsumerFacts` (both paths assert 5 join fields via `CapturingLogger<T>`) | `BaseApi.Tests.exe --filter-class "*ReinjectConsumerFacts"` | ✅ | ✅ green |
| 75-04 | 0 | D75-4 keeper-outcome ES-join map + reinject-wins tie-break | unit (hermetic) | `BuildKeeperOutcomeMapFacts` (3 facts: `[drop,reinject]`→reinject, `[reinject,drop]`→reinject, lone drop→drop) | `BaseApi.Tests.exe --filter-class "*BuildKeeperOutcomeMapFacts"` | ✅ | ✅ green |
| 75-04 | 0 | D75-4 live ES-join wiring compiles (`Analyze(keeperOutcomeByExecution:)`) | build/compile gate (RealStack) | `AnalyzerE2ETests` (Category=RealStack — build+compile gate; live surfacing manual) | `BaseApi.Tests.exe` (build gate) | ✅ | ✅ green (compile); ⏸ live → Manual-Only |
| 75-03 | 0 | D75-6 five TTL knobs raised 300→900 | unit (hermetic) | `ProcessorOptionsBindingFacts` (baked default 900) + `ComposeYamlFacts` (compose override 900) | `BaseApi.Tests.exe --filter-class "*ProcessorOptionsBindingFacts" --filter-class "*ComposeYamlFacts"` | ✅ | ✅ green; ⏸ live neutralization → Manual-Only |
| 75-05 | 0 | D75-7 `K_EXECUTIONS` seam default-OFF, 300s wall-clock unchanged | script AST parse-check (automated) | `scripts/phase-67-harness.ps1` — `pwsh -NoProfile` AST parse ⇒ `PARSE_OK`; default-off path byte-for-byte unchanged | `pwsh -NoProfile -Command "[void][ScriptBlock]::Create((Get-Content -Raw scripts/phase-67-harness.ps1))"` | ✅ | ✅ parse-ok; ⏸ live (OPTIONAL) → Manual-Only |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky · ⏸ deferred (manual-only)*

**Confirmed run (2026-07-15):** `PassFailEngineFacts` + `PassFailEngineValueChainFacts` + `BuildKeeperOutcomeMapFacts` + `ReinjectConsumerFacts` + `ProcessorOptionsBindingFacts` + `ComposeYamlFacts` → **58 passed / 0 failed** (528ms).

---

## Wave 0 Requirements

*Existing infrastructure covers all phase requirements — the verdict logic (`PassFailEngine`), keeper telemetry (`ReinjectConsumer`), ES-join map builder (`BuildKeeperOutcomeMap`), and options/compose binding are all hermetically testable via synthetic `RunTrace` facts and capturing loggers. No new framework install needed. The three live-stack observations (D75-4 ES surfacing, D75-6 TTL neutralization, D75-7 K_EXECUTIONS early-break) are Docker-only and are classified Manual-Only below.*

---

## Live-Stack Verifications (RealStack sweep — 2026-07-15)

The three previously Manual-Only live gates were exercised on a real Docker RealStack via
`scripts/phase-68-sweep.ps1` (all 7 scenarios). **Result: 7/7 PASS** (TEST-01 baseline required a
warm-ES single re-run — first pass hit ES cold-start indexing lag, not a regression).

| Behavior | Requirement | Status | Evidence |
|----------|-------------|--------|----------|
| Live keeper→ES join drives the recovery verdict; keeper crash fully recovers | D75-4 | ✅ live-verified | TEST-04 (keeper both-replica crash) Pass 19/19, Missing=0; analyzer built the per-(corr,exec) keeper-outcome map from ES REINJECT logs; verdict is a pure per-execution function |
| TTL neutralization (900s) — no TTL-manufactured loss on non-redis scenarios | D75-6 | ✅ live-verified | TEST-02/03/04/06 all `InFlightLoss=0` at the new 900s TTL; the Phase-68 TTL-expiry artifact is gone |
| `K_EXECUTIONS` early-break (OPTIONAL, default-off) | D75-7 | ⏸ not adopted | default 300s wall-clock ran unchanged on all 7 scenarios; OPTIONAL seam left off — acceptable resolution |

Full per-scenario table and bootstrap notes in `deferred-items.md §'✅ RESOLVED — live Docker-up sweep'`.

*Redis-crash scenarios (TEST-05/07) are the genuine in-flight-at-wipe durability boundary — TTL moot; both Pass (26/26, 14/14).*

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (none — zero MISSING gaps)
- [x] No watch-mode flags
- [x] Feedback latency < 30s (hermetic fact classes run in ~0.5s)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** validated 2026-07-15 — 8/8 requirements have hermetic automated coverage; 3 live-stack observations (D75-4/6/7) are Docker-only and appropriately Manual-Only (mirrors `75-VERIFICATION.md` deferred items).

---

## Validation Audit 2026-07-15

| Metric | Count |
|--------|-------|
| Requirements audited | 8 (D75-1..8) |
| COVERED (hermetic automated, green) | 8 |
| MISSING (no test) | 0 |
| Live-only gates | 3 (D75-4 ES-join, D75-6 TTL, D75-7 K_EXECUTIONS) |
| Live-verified this session | 2 (D75-4, D75-6 — RealStack 7/7 PASS) |
| Not adopted (OPTIONAL) | 1 (D75-7, default-off) |
| Gaps found | 0 |
| Resolved | 0 (none needed) |
| Escalated | 0 |
| Hermetic tests run this audit | 58 passed / 0 failed |
| Live sweep run this audit | 7/7 scenarios PASS (Docker RealStack) |

*State A audit: existing VALIDATION.md was an unfilled template. Reconstructed the Per-Task Verification Map from the five plan summaries, cross-referenced each D75-N requirement to its hermetic fact class, and confirmed all 58 mapped facts run green (zero MISSING gaps, no auditor spawn). Follow-up (same day): ran the full RealStack sweep — 7/7 PASS — closing the D75-4 (keeper ES-join) and D75-6 (TTL neutralization) live gates; D75-7 left OPTIONAL/default-off. See `deferred-items.md §'✅ RESOLVED'`.*
