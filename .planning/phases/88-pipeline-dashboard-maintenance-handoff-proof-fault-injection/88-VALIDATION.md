---
phase: 88
slug: pipeline-dashboard-maintenance-handoff-proof-fault-injection
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-07-28
---

# Phase 88 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from `88-RESEARCH.md` § Validation Architecture. The Per-Task Verification Map
> is filled by `gsd-planner` once plans exist.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | **None applicable.** This phase adds no C# and no hermetic tests. Validation is a live harness, exactly as Phase 87's was. |
| **Config file** | n/a |
| **Quick run command** | `pwsh -File scripts/phase-88-panel-discriminate.ps1 -Scenario <id>` (single scenario) |
| **Full suite command** | full scenario sweep + roll-up → `analyzer-reports/phase-88-discrimination.json` |
| **Static gate** | `[ScriptBlock]::Create((Get-Content <script> -Raw))` parse check + `Select-String` guard assertions (Phase-87 self-check pattern) |
| **Estimated runtime** | static gate ~5 s · single scenario minutes · full sweep long-running (run detached) |

---

## Sampling Rate

- **After every task commit:** `[ScriptBlock]::Create` parse check + `pwsh -File scripts/phase-87-dashboard-lint.ps1` (~5 s — cheap, run always)
- **After every plan wave:** the wave's scenarios re-run to a `Pass` artifact
- **Before `/gsd:verify-work`:** full scenario sweep green **and** `pwsh -File scripts/phase-87-dashboards-verify.ps1` still exit 0 (proving Phase 88 left the Phase-87 dashboards intact)
- **Max feedback latency:** ~5 s for the per-commit static gate

---

## Requirement → Validation Map (from RESEARCH.md)

| Req | Behaviour | Type | Command | Exists? |
|---|---|---|---|---|
| DISC-01 | baseline band per panel, pinned absolute window | live capture | `-Mode Baseline` | ❌ Wave 0 |
| DISC-02 | panel moved under its fault, asserted from the rendered DOM | live scenario | `-Scenario <id>` | ❌ Wave 0 |
| DISC-03 | cross-talk panels stayed put | live scenario | same run, `CrossTalkPanels[]` claims | ❌ Wave 0 |
| DISC-04 | panels 2 and 8 non-zero in a real recovery event | live scenario | `-Scenario keeper-recovery` / `keeper-loss` | ❌ Wave 0 |
| DISC-05 | seam arm/disarm, stack byte-identical afterwards | live + assertion | `SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` claims | ❌ Wave 0 |
| DISC-06 | re-baseline after rollout; discontinuity recorded as expected artifact | live + artifact field | `RolloutOldInstanceIds` / `RolloutNewInstanceIds` non-empty and distinct | ❌ Wave 0 |
| DISC-07 | minimum detectable fault duration measured | step-ladder | `-Mode DurationLadder` | ❌ Wave 0 |
| HAND-01/02/03/04 | runbook, readiness verdict, practicality rubric, accepted-unproven register | document review | human checkpoint | ❌ Wave 0 |
| — | dashboard JSON still lints (if any panel is recomposed) | hermetic | `pwsh -File scripts/phase-87-dashboard-lint.ps1` | ✅ exists, 23 rules, ~5 s |
| — | Phase-87 proof still green (no regression) | live | `pwsh -File scripts/phase-87-dashboards-verify.ps1` | ✅ exists |

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| _to be filled by gsd-planner_ | | | | | | | | | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `scripts/phase-88-panel-discriminate.ps1` — the scenario driver (arm / trigger / restore / verdict / artifact)
- [ ] A Playwright panel-reader script + a stable invocation contract (env-var in, JSON on stdout)
- [ ] **A Wave-0 probe** answering three blockers *before* the scenarios are locked:
  1. Does a plain `processor-sample` scale-0 produce `keeper_messages_consumed_total` at all? (decides whether panel 2 needs a seam)
  2. Does `?viewPanel=panel-<id>` work on this Grafana 12.3.9 instance, and does `getByTestId('data-testid panel content').innerText()` return a parseable number for **both** a stat and a table-legend timeseries?
  3. Is there any WebApi endpoint that returns 5xx on safe input? (decides panel 13's fate)
- [ ] The DISC-01 baseline capture mode, run once on a settled stack to seed every band
- [ ] `analyzer-reports/phase-88-discrimination.json` roll-up writer

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Symptom → panel → action runbook is usable by a team that did not build the system | HAND-01 | Practicality is a judgement, not an assertion — the rubric scores it, a human signs it off | Review each runbook row against the scenario artifact that proved it; reject any row with no backing scenario |
| Handoff readiness verdict (blocking gaps, panels that mislead by default) | HAND-02 | Verdict is a judgement over the full evidence set | Read the roll-up matrix; confirm every accepted-unproven panel names its reason |
| Panel 7 (`processor_spawn_dropped`) accepted-unproven | HAND-04 | User-locked decision this session — no broker-fault scenario is to be designed | Confirm the findings record + runbook row tell maintenance to corroborate via processor logs |

---

## Validation Sign-Off

- [ ] All tasks have `<automated>` verify or Wave 0 dependencies
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING references
- [ ] No watch-mode flags
- [ ] Feedback latency < 10s for the per-commit static gate
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
