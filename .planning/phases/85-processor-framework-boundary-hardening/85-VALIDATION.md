---
phase: 85
slug: processor-framework-boundary-hardening
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-07-21
---

# Phase 85 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Full validation architecture (hermetic facts, negative control, existing-test break-list, SC-4 sweep gate) lives in `85-RESEARCH.md` § "Validation Architecture". This file is the sampling contract the planner maps tasks onto.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit via Microsoft Testing Platform (MTP) — `BaseApi.Tests.exe` |
| **Config file** | existing (`tests/BaseApi.Tests`) — no Wave 0 install |
| **Quick run command** | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (hermetic only) |
| **Full suite command** | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` + detached `scripts/phase-68-sweep.ps1` (SC-4 gate) |
| **Estimated runtime** | hermetic ~seconds; SC-4 sweep ~hours (detached, see [[long-sweep-detached-process]]) |

> **Windows caveat:** run `BaseApi.Tests.exe` directly — `dotnet test` hangs on Windows MTP (memory [[hermetic-test-command]]).

---

## Sampling Rate

- **After every task commit:** Run hermetic `BaseApi.Tests.exe --filter-not-trait Category=RealStack`
- **After every plan wave:** Full hermetic suite green
- **Before `/gsd:verify-work`:** Hermetic suite green AND SC-4 sweep reproduces the all-PASS baseline (`Missing==0`, `Duplicates==0`)
- **Max feedback latency:** hermetic < 60s

---

## Per-Task Verification Map

> Filled by the planner. Every PB-* task maps to a hermetic fact or the SC-4 sweep. Source: `85-RESEARCH.md` § Validation Architecture.

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 85-XX-XX | XX | X | PB-01 | — | fail-loud: transient spawn-exhaust throws the dedicated exception (telemetry fires first) | unit (hermetic) | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` | ✅ | ⬜ pending |
| 85-XX-XX | XX | X | PB-02 | — | defeated spawn ⇒ `RunAsync` propagates the dedicated exception (nack), not `StepFailed`+ack | unit (hermetic, D-05 fact) | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` | ✅ | ⬜ pending |
| 85-XX-XX | XX | X | PB-02/PB-03 | — | **negative control:** deterministic fault still acks as `StepFailed` (D-03 poison-safety — filter stays narrow) | unit (hermetic) | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` | ✅ | ⬜ pending |
| 85-XX-XX | XX | X | PB-03 | — | entry deletion framework-owned on both completion paths; `Guid.Empty` source net-effect unchanged | unit (hermetic) | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` | ✅ | ⬜ pending |
| 85-SC4 | — | final | SC-4 | — | 7-scenario sweep reproduces all-PASS baseline (`Missing==0`, `Duplicates==0`) with `Processor.Sample` | integration | `scripts/phase-68-sweep.ps1` (detached) | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

*Existing infrastructure covers all phase requirements — `BaseApi.Tests` and `scripts/phase-68-sweep.ps1` already exist. No new framework install. The existing hermetic facts that assert the pre-hardening behavior (swallow / author-owned DeleteEntry) must be inverted in lockstep — see `85-RESEARCH.md` break-list.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| A1: scheduler seeds `Guid.Empty` for the Mode-2 entry (net-effect assumption behind D-04's source-skip) | PB-03 | Confirms the framework null-path delete's `SourceStep.IsSource` skip reproduces today's no-op exactly | Inspect the dispatch `EntryId` the scheduler sets for a Mode-2 seed; confirm `Guid.Empty` (a real non-empty entryId would require the framework delete to fire) |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 60s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
