---
phase: 74
slug: reshape-business-metrics-into-a-uniform-two-counter-model
status: validated
nyquist_compliant: true
wave_0_complete: true
created: 2026-06-18
---

# Phase 74 — Validation Strategy

> Per-phase validation contract, reconstructed post-execution from PLAN/SUMMARY/VERIFICATION artifacts (State B). All hermetic requirements are covered by existing GREEN tests; the live-stack close gate is a documented manual-only/deferred item (no Docker in sandbox).

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 / Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait "Category=RealStack"` |
| **Full suite command** | `tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-not-trait "Category=RealStack"` |
| **Estimated runtime** | ~30–60 seconds (hermetic subset); full hermetic suite 668 tests |

> **MTP note:** run `BaseApi.Tests.exe` DIRECTLY — `dotnet test` hangs on Windows MTP. `Category=RealStack` tests require the live Docker stack and are excluded from the hermetic gate.

---

## Sampling Rate

- **After every task commit:** Run the quick (Debug) hermetic command scoped to the touched fact class (e.g. `--filter-class "*OrchestratorMetricsFacts"`).
- **After every plan wave:** Run the full hermetic suite (`--filter-not-trait "Category=RealStack"`).
- **Before `/gsd-verify-work`:** Full hermetic suite green + 0-warning Debug & Release build.
- **Max feedback latency:** ~60 seconds (hermetic).

---

## Per-Task Verification Map

| Plan | Wave | Requirement | Threat Ref | Secure / Verified Behavior | Test Type | Automated Command (filter-class) | File Exists | Status |
|------|------|-------------|------------|-----------------------------|-----------|----------------------------------|-------------|--------|
| 74-01 | 1 | REQ-1, REQ-6 | T-74-01/02 | `orchestrator_messages_consumed`/`_sent` exist (snake_case, camelCase `workflowId`+`processorId`); fan-out + keeper-escalation sends counted; `ResultDeduped`/old series absent | unit | `*OrchestratorMetricsFacts` | ✅ | ✅ green |
| 74-01 | 1 | REQ-1 | T-74-01 | orchestrator removed-counter absence (`orchestrator_result_deduped` no series) | unit | `*BreakerMetricsFacts` | ✅ | ✅ green |
| 74-02 | 1 | REQ-2, REQ-6 | T-74-03/04 | `processor_messages_consumed`/`_sent` exist with bounded labels; `outcome` label + `DispatchDeduped` removed | unit | `*ProcessorMetricsFacts` | ✅ | ✅ green |
| 74-03 | 1 | REQ-3, REQ-6 | T-74-05/06/07 | `keeper_messages_consumed`/`_sent`; `CountSent` after success only, never on Reinject drop path; `ReinjectDropped` absent | unit | `*KeeperMetricsFacts`, `*ReinjectConsumerFacts`, `*OrchestratorReinjectConsumerFacts`, `*InjectConsumerFacts`, `*OrchestratorInjectConsumerFacts` | ✅ | ✅ green |
| 74-03 | 1 | REQ-4 | — | `keeper_l2_probe` label-less, once per `BitHealthLoop` tick | unit | `*BitHealthLoopTests`, `*KeeperMetricsFacts` | ✅ | ✅ green |
| 74-04 | 2 | REQ-5, REQ-6 | T-74-08/09 | `PromCounterSnapshot`/`PassFailEngine` repointed to new `*_messages_sent` total; `outcome` WARNING path dropped; verdict math intact | unit | `*PassFailEngineFacts`, `*PassFailEngineValueChainFacts` | ✅ | ✅ green |
| 74-04 | 2 | REQ-1,2,3 | T-74-01/03/05 | three removed-counter absence asserts; processor `outcome` label gone | unit | `*BreakerMetricsFacts`, `*ProcessorMetricsFacts`, `*KeeperMetricsFacts` | ✅ | ✅ green |
| 74-04 | 2 | REQ-6 | — | inverted `workflowId` cardinality guard (now REQUIRED), `outcome` forbidden | unit (RealStack-shaped) | `*MetricsRoundTripE2ETests` (`AssertBusinessLabels`) | ✅ | ✅ green (compiles; assertion logic hermetic-verified, live exec deferred) |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

Evidence (74-VERIFICATION.md): hermetic metric-facts subsets **45/45** and **22/22** GREEN; full hermetic suite **668/668** GREEN; 0-warning Debug **and** Release.

---

## Wave 0 Requirements

Existing infrastructure covers all phase requirements. This was a rename/move refactor of the existing `BaseApi.Tests` metric-facts suite (D-14 rename-in-place + added absence asserts) — no new framework or fixtures required.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live-stack `PassFailEngine` close gate (net-zero all-complete sweep) reflects the new `*_messages_*` names end-to-end against Prometheus | REQ-5 (SPEC AC #9) | Requires the live Docker stack (Prometheus + ES + full service mesh); deferred-automated per SPEC — the sandbox lacks Docker | Bring up the compose stack, run the close-gate sweep, confirm `PassFailEngine` verdict PASS reading `orchestrator_/processor_/keeper_messages_*` series with `workflowId`+`processorId` labels and `keeper_l2_probe_total` advancing |
| `AnalyzerE2ETests` / `MetricsRoundTripE2ETests` / `SC2RecoveryPathsE2ETests` (`Category=RealStack`) live execution | REQ-1/2/3/5 | `Category=RealStack` — needs live Redis/RabbitMQ/Prometheus/ES; excluded from the hermetic gate | Run `BaseApi.Tests.exe` WITHOUT the `--filter-not-trait "Category=RealStack"` exclusion against a running stack |

> These tests **exist and compile**; only their live execution is deferred. Their pass/fail *logic* is hermetically exercised (e.g. `MetricsRoundTripE2ETests.AssertBusinessLabels` logic verified during 74-04). This is a documented deferral, not a missing test.

---

## Validation Sign-Off

- [x] All requirements (REQ-1..REQ-6) have automated hermetic verification
- [x] Sampling continuity: every plan/wave has at least one automated metric-facts assertion
- [x] Wave 0 covers all MISSING references (none — existing infra)
- [x] No watch-mode flags (MTP EXE-direct, single-shot)
- [x] Feedback latency < 60s (hermetic)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-18 (hermetic surface fully automated; live-stack close gate documented manual-only/deferred per SPEC)

---

## Validation Audit 2026-06-18

| Metric | Count |
|--------|-------|
| Requirements audited | 6 |
| Covered (automated, GREEN) | 6 |
| Missing (tests generated) | 0 |
| Manual-only / deferred | 1 (live-stack close gate, SPEC AC #9) |
