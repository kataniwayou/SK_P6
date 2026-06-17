---
phase: 71
slug: orchestrator-two-consumer-design-pre-process-post-process-wi
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-06-17
---

# Phase 71 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit v3 / Microsoft.Testing.Platform (MTP) |
| **Config file** | {planner: per-project .csproj test settings} |
| **Quick run command** | `dotnet test --filter "FullyQualifiedName~Orchestrator"` |
| **Full suite command** | `dotnet test` |
| **Estimated runtime** | ~{N} seconds (planner to confirm) |

---

## Sampling Rate

- **After every task commit:** Run `dotnet test --filter` for the touched suite
- **After every plan wave:** Run `dotnet test` (full suite)
- **Before `/gsd-verify-work`:** Full suite must be green
- **Max feedback latency:** {N} seconds (planner to confirm)

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| {N}-01-01 | 01 | 1 | REQ-{XX} | T-{N}-01 / — | {expected secure behavior or "N/A"} | unit | `{command}` | ✅ / ❌ W0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

*Planner: populate from RESEARCH.md "Validation Architecture" — one row per task covering SPEC reqs 1–12.*

---

## Wave 0 Requirements

- [ ] Test-gap stubs for the new Pre-pipeline / Post consumer / orchestrator keeper paths (see RESEARCH.md Wave 0 test-gap list)
- [ ] ExecutionId thread-unchanged assertion flip (regeneration → equality) in the orchestrator result-consumer facts
- [ ] A1 stamp test update (Guid.Empty → output messageId) for the processor

*If none: "Existing infrastructure covers all phase requirements."*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| {behavior} | REQ-{XX} | {reason} | {steps} |

*If none: "All phase behaviors have automated verification." (Container build/deploy is SPEC out-of-scope — code + `dotnet test` only.)*

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < {N}s
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** {pending / approved YYYY-MM-DD}
