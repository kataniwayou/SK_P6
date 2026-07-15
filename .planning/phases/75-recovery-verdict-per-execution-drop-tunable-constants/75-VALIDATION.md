---
phase: 75
slug: recovery-verdict-per-execution-drop-tunable-constants
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-07-15
---

# Phase 75 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (Microsoft.Testing.Platform) |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` |
| **Quick run command** | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (hermetic — direct exe; `dotnet test` hangs on Windows MTP) |
| **Full suite command** | `BaseApi.Tests.exe` (includes RealStack resilience sweep; needs Docker) |
| **Estimated runtime** | ~seconds hermetic; ~300s+ per RealStack scenario |

---

## Sampling Rate

- **After every task commit:** Run the hermetic filter (`--filter-not-trait Category=RealStack`)
- **After every plan wave:** Run affected `PassFailEngine` / analyzer unit tests
- **Before `/gsd-verify-work`:** Hermetic suite must be green; RealStack join/TTL neutralization deferred-automated (Docker)
- **Max feedback latency:** ~30 seconds (hermetic)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _to be filled by planner_ | | | D75-{n} | — | N/A | unit | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` | ✅ | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

*Existing infrastructure covers all phase requirements — the verdict logic (`PassFailEngine`) and analyzer classification are hermetically testable via synthetic `RunTrace` facts. No new framework install needed.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live keeper→ES join surfacing new `entryId`/`messageId` log fields | D75-4 | Requires running Docker RealStack + OTLP export; not hermetic | Run RealStack sweep, query `AnalyzerE2ETests` ES for keeper reinject/drop attributes |
| TTL neutralization outlasting recovery for non-redis scenarios | D75-6 | Requires live L2 + timed recovery dwell | Run TEST-01..04/06 with raised TTLs, confirm no TTL-manufactured loss |

*Redis-crash scenarios (TEST-05/07) are the genuine in-flight-at-wipe durability boundary — TTL moot.*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 30s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
