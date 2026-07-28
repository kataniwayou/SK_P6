---
phase: 88
slug: pipeline-dashboard-maintenance-handoff-proof-fault-injection
status: planned
nyquist_compliant: true
wave_0_complete: false
created: 2026-07-28
updated: 2026-07-28
---

# Phase 88 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from `88-RESEARCH.md` § Validation Architecture. The Per-Task Verification Map
> was filled by `gsd-planner` on 2026-07-28 against the eleven plans.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | **None applicable.** This phase adds no C# and no hermetic tests. Validation is a live harness, exactly as Phase 87's was. |
| **Config file** | n/a |
| **Quick run command** | `pwsh -File scripts/phase-88-panel-discriminate.ps1 -ScenarioId <id> -Mode Scenario` (single scenario) |
| **Full suite command** | the scenario sweep + `pwsh -File scripts/phase-88-rollup.ps1` → `analyzer-reports/phase-88-discrimination.json` |
| **Static gate** | `[ScriptBlock]::Create((Get-Content <script> -Raw))` parse check + `node --check` for the reader + `Select-String` guard assertions (Phase-87 self-check pattern) |
| **Estimated runtime** | static gate ~5 s · single scenario 10–20 min · duration ladder 60–75 min (run detached) · full phase multi-session |

---

## Sampling Rate

- **After every task commit:** `[ScriptBlock]::Create` parse check (or `node --check`) + `pwsh -File scripts/phase-87-dashboard-lint.ps1` (~5 s — cheap, run always)
- **After every plan wave:** the wave's scenarios re-run to a `Pass` artifact, and the stack re-asserted at rest (replicas 1/2/3/2, zero `DEFEAT`/`REINJECT_DELAY` env vars, images `:tags-const-1544`)
- **Before `/gsd:verify-work`:** `pwsh -File scripts/phase-88-rollup.ps1` exit 0 **and** `pwsh -File scripts/phase-87-dashboards-verify.ps1` exit 0 (proving Phase 88 left the Phase-87 dashboards intact) — plan 88-10 Task 3
- **Max feedback latency:** ~5 s for the per-commit static gate

---

## Requirement → Validation Map

| Req | Behaviour | Type | Command | Delivered by |
|---|---|---|---|---|
| DISC-01 | baseline band per panel, absolute pinned equal-width windows, fixed viewport | live capture | `-ScenarioId BASE-01 -Mode Baseline` | 88-04 |
| DISC-02 | panel moved under its fault, asserted from the rendered DOM | live scenario | `-ScenarioId <id> -Mode Scenario` | 88-05, 88-06, 88-07, 88-08 |
| DISC-03 | cross-talk panels stayed put | live scenario | same run, `CrossTalkPanels[]` claims | 88-05, 88-06, 88-07 |
| DISC-04 | panels 2 and 8 non-zero in a real recovery event | live scenario | `-ScenarioId ZERO-02` / `ZERO-03` | 88-07 |
| DISC-05 | seam arm/disarm, stack byte-identical afterwards | live + assertion | `SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` claims | 88-02, 88-03, 88-07 |
| DISC-06 | re-baseline after rollout; discontinuity recorded as an expected artifact | live + artifact field | `BaselineRecaptured`, `RolloutOld/NewInstanceIds` non-empty and disjoint | 88-05, 88-06, 88-07 |
| DISC-07 | minimum detectable fault duration measured, per regime | step-ladder | `-ScenarioId LADDER-01 -Mode DurationLadder` | 88-08 |
| HAND-01 | symptom → panel → action runbook, every row traceable | document + assertion | runbook row/citation grep | 88-10, 88-11 |
| HAND-02 | handoff readiness verdict: blocking gaps, misleading-by-default panels | document + assertion | verdict section grep + nine-criterion table | 88-10, 88-11 |
| HAND-03 | six-question practicality rubric, 14 rows, no aggregate score | document + assertion | rubric cell count | 88-09 |
| HAND-04 | accepted-unproven register, one row per unproven panel with a reason | document + assertion | register set equals matrix `AcceptedUnproven` set | 88-09 |
| — | dashboard JSON still lints (no panel silently re-classed) | hermetic | `pwsh -File scripts/phase-87-dashboard-lint.ps1` | every task; gated in 88-10 |
| — | Phase-87 proof still green (no regression) | live | `pwsh -File scripts/phase-87-dashboards-verify.ps1` | 88-10 |

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 88-01-T1 | 88-01 | 1 | DISC-01..07, HAND-01..04 | — | requirement family registered before any code cites it | static | `pwsh` ids + traceability grep on `.planning/REQUIREMENTS.md` | ✅ REQUIREMENTS.md | ⬜ pending |
| 88-01-T2 | 88-01 | 1 | DISC-02 | T-88-08, T-88-09 | reader authored under `scripts/`, never in the skill dir; absolute screenshot dir | static | `node --check scripts/phase-88-panel-read.js` + sentinel/forbidden-token grep | ❌ Wave 0 | ⬜ pending |
| 88-01-T3 | 88-01 | 1 | DISC-01, DISC-02 | T-88-12, T-88-05 | sentinel + batch-end parsing; Truncated/Unreadable ≠ Fail | static + unit-ish | `[ScriptBlock]::Create` + window/band/movement/NoData assertions | ❌ Wave 0 | ⬜ pending |
| 88-02-T1 | 88-02 | 1 | DISC-05, DISC-06 | T-88-01, T-88-02 | tier allow-list; live `spec.replicas` read before scale; no `TierReplicas` | static + guard | `[ScriptBlock]::Create` + `redis`/`prometheus` rejection + forbidden-token grep | ❌ Wave 0 | ⬜ pending |
| 88-02-T2 | 88-02 | 1 | DISC-05 | T-88-03, T-88-15 | seam (tier,var) allow-list; trailing-hyphen disarm; restore assertion | static + guard | `[ScriptBlock]::Create` + mismatched-seam rejection + disarm-path grep | ❌ Wave 0 | ⬜ pending |
| 88-03-T1 | 88-03 | 2 | DISC-02 | T-88-04, T-88-05, T-88-07 | loopback-only forward; distinct exit code per abort | static | `[ScriptBlock]::Create` + exit-code-table + forbidden-token grep | ❌ Wave 0 | ⬜ pending |
| 88-03-T2 | 88-03 | 2 | DISC-04, DISC-05, HAND-04 | T-88-06, T-88-11, T-88-13, T-88-16 | static endpoint table; static-literal `psql -c`; no dependency outage | static | `[ScriptBlock]::Create` + PQ-id + Depth-10 + psql-literal grep | ❌ Wave 0 | ⬜ pending |
| 88-03-T3 | 88-03 | 2 | DISC-02, DISC-04, DISC-05, DISC-06, HAND-04 | T-88-03, T-88-02, T-88-10 | probe leaves stack clean; branches locked from recorded fields | live + assertion | probe artifact answers + independent `kubectl` replica/env read | ❌ Wave 0 | ⬜ pending |
| 88-04-T1 | 88-04 | 3 | DISC-01, DISC-03 | T-88-13, T-88-01 | static scenario table; unknown/dropped id → exit 64; non-empty cross-talk enforced | static + guard | `-ScenarioId NOPE-99` exits 64 + panel-title/scenario-row grep | ❌ Wave 0 | ⬜ pending |
| 88-04-T2 | 88-04 | 3 | DISC-01 | T-88-17, T-88-18, T-88-12 | equal-width pinned windows; Depth-10 artifact written before exit resolution | static | `[ScriptBlock]::Create` + write-before-resolve ordering + Depth-10 grep | ❌ Wave 0 | ⬜ pending |
| 88-04-T3 | 88-04 | 3 | DISC-01 | T-88-17 | band recomputable from the artifact; Regime-B zero bands verified | live + assertion | `phase-88-BASE-01.json` window/viewport/band assertions | ❌ Wave 0 | ⬜ pending |
| 88-05-T1 | 88-05 | 4 | DISC-02, DISC-03, DISC-06 | T-88-20, T-88-21, T-88-03 | arm → settle ≥150 s → re-baseline → trigger; disarm in `finally` | static | `[ScriptBlock]::Create` + settle/rollout/cross-talk field grep | ❌ Wave 0 | ⬜ pending |
| 88-05-T2 | 88-05 | 4 | DISC-02, DISC-03 | T-88-19, T-88-12 | sustained (not burst) load; no `/health/` routes | live + assertion | `phase-88-WEB-01.json` panels 10/11/12/14 moved + cross-talk 1/3/9 held | ❌ Wave 0 | ⬜ pending |
| 88-05-T3 | 88-05 | 4 | DISC-02, DISC-03 | T-88-16 | no dependency outage substituted; degraded row rather than invented fault | live + assertion | `phase-88-WEB-02.json` branch assertions on the probe field | ❌ Wave 0 | ⬜ pending |
| 88-06-T1 | 88-06 | 5 | DISC-02, DISC-03, DISC-06 | T-88-02, T-88-20, T-88-22 | live-read replica restore; telemetry path never scaled | live + assertion | `phase-88-SCALE-01.json` panels 3/4/5 + rollout disjoint + replicas 2 | ❌ Wave 0 | ⬜ pending |
| 88-06-T2 | 88-06 | 5 | DISC-02, DISC-03, DISC-06 | T-88-02 | orchestrator restored to the live-read 3, verified independently | live + assertion | `phase-88-SCALE-02.json` + direct `kubectl get deploy orchestrator` = 3 | ❌ Wave 0 | ⬜ pending |
| 88-06-T3 | 88-06 | 5 | DISC-02, DISC-03, DISC-06 | T-88-23 | `NoData` modelled as a first-class state, scored as moved | live + assertion | `phase-88-SCALE-03.json` two consecutive `NoData` + screenshot on disk | ❌ Wave 0 | ⬜ pending |
| 88-07-T1 | 88-07 | 6 | DISC-02, DISC-03 | T-88-11 | separate workflow, stop-before-delete, cleanup asserted (ghost-cron) | live + assertion | `phase-88-ZERO-01.json` + `v8-fanout-proof` row count = 1 | ❌ Wave 0 | ⬜ pending |
| 88-07-T2 | 88-07 | 6 | DISC-02, DISC-03, DISC-04, DISC-06 | T-88-12, T-88-20 | two independent signals (numeric + legend name); panel 8 held at 0 | live + assertion | `phase-88-ZERO-02.json` moved + legend-name change + cross-talk 8 = 0 | ❌ Wave 0 | ⬜ pending |
| 88-07-T3 | 88-07 | 6 | DISC-02, DISC-03, DISC-04, DISC-05, DISC-06 | T-88-03, T-88-01, T-88-15, T-88-21 | seam disarmed in `finally`; independent env read; other guarded zeros held at 0 | live + assertion | `phase-88-ZERO-03.json` + independent `kubectl` env/replica/image read | ❌ Wave 0 | ⬜ pending |
| 88-08-T1 | 88-08 | 7 | DISC-07 | T-88-24, T-88-13 | per-rung independent baseline; `-Rungs` validated | static + guard | `[ScriptBlock]::Create` + `-Rungs 999` exits 64 + rung-table grep | ❌ Wave 0 | ⬜ pending |
| 88-08-T2 | 88-08 | 7 | DISC-07 | T-88-25, T-88-02 | measured vs theoretical dip recorded side by side; per-rung restore | live + assertion | `phase-88-LADDER-01.json` all 8 rungs + both regime answers | ❌ Wave 0 | ⬜ pending |
| 88-08-T3 | 88-08 | 7 | DISC-02 | T-88-26, T-88-27 | roll-up reads and tabulates only; no `kubectl`; a gap is a row | static + live | `pwsh -File scripts/phase-88-rollup.ps1` exit 0 + 14-row assertions | ❌ Wave 0 | ⬜ pending |
| 88-09-T1 | 88-09 | 8 | HAND-03, HAND-04 | T-88-28, T-88-31 | register derived from the matrix; rubric fixed at 6 questions, no score | document + assertion | register-set equality + rubric cell count + panel-7 record grep | ❌ Wave 0 | ⬜ pending |
| 88-09-T2 | 88-09 | 8 | DISC-01..DISC-07 | T-88-29, T-88-30 | DISC-02/DISC-04 status computed from artifacts, not asserted | document + assertion | status computed from the artifacts + 16 Phase-87 rows untouched | ❌ Wave 0 | ⬜ pending |
| 88-10-T1 | 88-10 | 9 | HAND-01 | T-88-32, T-88-09 | every row cites a proving scenario; no credential beyond the stated posture | document + assertion | all 14 titles + ≥8 scenario citations + header caveats | ❌ Wave 0 | ⬜ pending |
| 88-10-T2 | 88-10 | 9 | HAND-02 | T-88-33 | reachability stated as a blocking gap, not softened | document + assertion | sections (a)-(d) + nine PASS/PARTIAL/FAIL tokens + HAND-01/02 Complete | ❌ Wave 0 | ⬜ pending |
| 88-10-T3 | 88-10 | 9 | HAND-01, HAND-02 | T-88-34, T-88-35, T-88-19 | `k8s/` provably unmodified; C3/C7 guard classification intact | live gate | lint exit 0 + `git status --porcelain k8s/` empty + verify exit 0 | ✅ both scripts exist | ⬜ pending |
| 88-11-T1 | 88-11 | 10 | HAND-01, HAND-02 | T-88-36, T-88-37, T-88-38 | attributed sign-off; findings recorded, never edited away | manual + pre-flight | pre-flight asserts all four reviewer inputs and a complete 14-row matrix | ❌ Wave 0 | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `scripts/phase-88-panel-read.js` — the batch DOM reader (plan 88-01 T2)
- [ ] `scripts/lib/phase-88-panel-read.ps1` — the reader invocation contract, banding and movement rule (plan 88-01 T3)
- [ ] `scripts/lib/phase-88-cluster-ops.ps1` — live-replica capture, scale sequencer, seam arm/disarm, restore assertions (plan 88-02)
- [ ] `scripts/phase-88-wave0-probe.ps1` + `analyzer-reports/phase-88-wave0-probe.json` + `88-PROBE-DECISIONS.md` — the seven blocking questions (plan 88-03)
- [ ] `scripts/phase-88-panel-discriminate.ps1` — driver frame, `-Mode Baseline`, `-Mode Scenario`, `-Mode DurationLadder` (plans 88-04, 88-05, 88-08)
- [ ] `analyzer-reports/phase-88-BASE-01.json` — the DISC-01 bands every later scenario scores against (plan 88-04 T3)
- [ ] `scripts/phase-88-rollup.ps1` + `analyzer-reports/phase-88-discrimination.json` — the panel-keyed matrix (plan 88-08 T3)

**Wave 0 is genuinely blocking.** Plan 88-03's seven probe answers change the structure of the
downstream scenario plans: PQ-01 decides whether panel 2 needs a seam, PQ-02 decides the reader's
primary locator, PQ-03 decides panel 13's fate, PQ-05 decides panel 6's fate, and PQ-06 decides
whether the panel-8 seam scenario can exist at all. No scenario lever may be locked before
`88-PROBE-DECISIONS.md` exists. Set `wave_0_complete: true` only after plan 88-03 Task 3 is green.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Symptom → panel → action runbook is usable by a team that did not build the system | HAND-01 | Practicality is a judgement, not an assertion — the rubric scores it, a human signs it off | Review each runbook row against the scenario artifact that proved it; reject any row with no backing scenario (plan 88-11 checks 1-3) |
| Handoff readiness verdict (blocking gaps, panels that mislead by default) | HAND-02 | The verdict is a judgement over the full evidence set | Read the roll-up matrix; confirm every accepted-unproven panel names its reason and that the reachability gap is called blocking, not softened (plan 88-11 checks 4-6) |
| Panel 7 (`processor_spawn_dropped`) accepted-unproven | HAND-04 | User-locked decision this session — no broker-fault scenario is to be designed | Confirm the findings record + runbook row tell maintenance to corroborate via processor logs (`SpawnSendExhaustedException`, nack-requeue re-fire, broker health) |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies — 29/29 tasks carry an `<automated>` command
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 10s for the per-commit static gate
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** planned — pending execution
