---
phase: 88
slug: pipeline-dashboard-maintenance-handoff-proof-fault-injection
status: audited
nyquist_compliant: true
wave_0_complete: true
created: 2026-07-28
updated: 2026-07-29
audited: 2026-07-29
automated_gaps: 0
manual_only_outstanding: 1  # the handover conversation only; the live-render issue was resolved 2026-07-29
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
| DISC-04 | panels 2 and 8 non-zero in a real recovery event — **⚠️ unsatisfiable as written, see the amendment below** | live scenario | `-ScenarioId ZERO-02` / `ZERO-03` | 88-07 |
| DISC-05 | seam arm/disarm, stack byte-identical afterwards | live + assertion | `SeamVarsClean`, `ReplicasRestored`, `ImagesUnchanged` claims | 88-02, 88-03, 88-07 |
| DISC-06 | re-baseline after rollout; discontinuity recorded as an expected artifact | live + artifact field | `BaselineRecaptured`, `RolloutOld/NewInstanceIds` non-empty and disjoint | 88-05, 88-06, 88-07 |
| DISC-07 | minimum detectable fault duration measured, per regime | step-ladder | `-ScenarioId LADDER-01 -Mode DurationLadder` | 88-08 |
| HAND-01 | symptom → panel → action runbook, every row traceable | document + assertion | runbook row/citation grep | 88-10, 88-11 |
| HAND-02 | handoff readiness verdict: blocking gaps, misleading-by-default panels | document + assertion | verdict section grep + nine-criterion table | 88-10, 88-11 |
| HAND-03 | six-question practicality rubric, 14 rows, no aggregate score | document + assertion | rubric cell count | 88-09 |
| HAND-04 | accepted-unproven register, one row per unproven panel with a reason | document + assertion | register set equals matrix `AcceptedUnproven` set | 88-09 |
| — | dashboard JSON still lints (no panel silently re-classed) | hermetic | `pwsh -File scripts/phase-87-dashboard-lint.ps1` | every task; gated in 88-10 |
| — | Phase-87 proof still green (no regression) | live | `pwsh -File scripts/phase-87-dashboards-verify.ps1` | 88-10 |

### DISC-04 amendment — 2026-07-29

**This behaviour is unsatisfiable as written, and no future test can cover it. Do not generate one.**

Panel 2 *was* proven — ZERO-02 moved it off an exactly-zero band for four consecutive samples, the
first time panel 2 has moved in this project's history. Panel 8 cannot be, for three independently
measured reasons:

1. The Phase-79 seam calls `CountSent` on **both** branches by design (`ReinjectConsumer.cs:106-116`)
   — the component was built to be telemetry-invisible, so consumed and sent move together even when
   they should diverge.
2. No allow-listed lever reaches the keeper's drop branch.
3. At a 60 s range both `increase()` terms return zero points, so the rendered `0` is purely
   `or vector(0)` — the guard, not a measurement.

A test that made panel 8 "pass" here would be asserting the guard fires, not that the gap is
detectable. DISC-04 is graded `Partial` and blocked; `ZERO-03-asdriven.json` preserves
`OriginalVerdict: Fail` verbatim. The real detection route is the keeper log line
`REINJECT drop {MessageId} drop` (`ReinjectConsumer.cs:77`), which is what the runbook and the
handover briefing tell an operator to search.

### Measurement constraints discovered during execution

These were not in the planning-time map. Any future re-run or added test MUST honour them, or it
measures nothing and reports green while doing so.

| Constraint | Why | Discovered by |
|---|---|---|
| **Panel 4 must be captured at 120 s sub-windows, never 60 s** | `increase()` needs ≥2 samples in range and the stored resolution is 60 s (the SDK export cadence, not the 15 s scrape), so a 60 s window holds exactly one sample and the panel renders "No data". Every band entry states its own `SubWindowSeconds`; widths must never be silently cross-compared. | 88-04 |
| **Panel 8 must not be read below a 120 s range** | Below that both `increase()` terms return zero points and the `or vector(0)` guard supplies the value outright. | 88-07 |
| **A NoData/decay criterion needs ≥480 s dwell, not 240 s** | `rate(counter[240s])` for a dead or silenced pod keeps producing a shrinking value for ~180 s past its last real sample, so a two-consecutive-NoData criterion is unreachable at 240 s. | 88-06 |
| **Panel 14 is carried solely by `kestrel_active_connections`** | `http_server_active_requests` read exactly 0 at all six export ticks under 2456 sustained requests — a dead instrument for this load class. Panel 14 is two ladder rungs, not one. | 88-05, 88-08 |
| **Panel 12 cannot be scored on legend gain** | A counter series survives as long as its process does, so all six status series are already present-and-zero. Score movement off an exactly-zero band instead. | 88-05 |
| **Prometheus retention is ~9.4 days (oldest sample 225 h), not ~12 hours** | The 12 h figure came from `87-FINDINGS.md §14` and propagated into `88-RESEARCH.md` as a standing "evidence evaporates" constraint. No measurement window should be compressed on that belief. | 88-03 (PQ-07) |

---

## Per-Task Verification Map

| Task ID | Plan | Wave | Requirement | Threat Ref | Secure Behavior | Test Type | Automated Command | File Exists | Status |
|---------|------|------|-------------|------------|-----------------|-----------|-------------------|-------------|--------|
| 88-01-T1 | 88-01 | 1 | DISC-01..07, HAND-01..04 | — | requirement family registered before any code cites it | static | `pwsh` ids + traceability grep on `.planning/REQUIREMENTS.md` | ✅ REQUIREMENTS.md | ✅ green |
| 88-01-T2 | 88-01 | 1 | DISC-02 | T-88-08, T-88-09 | reader authored under `scripts/`, never in the skill dir; absolute screenshot dir | static | `node --check scripts/phase-88-panel-read.js` + sentinel/forbidden-token grep | ✅ delivered | ✅ green |
| 88-01-T3 | 88-01 | 1 | DISC-01, DISC-02 | T-88-12, T-88-05 | sentinel + batch-end parsing; Truncated/Unreadable ≠ Fail | static + unit-ish | `[ScriptBlock]::Create` + window/band/movement/NoData assertions | ✅ delivered | ✅ green |
| 88-02-T1 | 88-02 | 1 | DISC-05, DISC-06 | T-88-01, T-88-02 | tier allow-list; live `spec.replicas` read before scale; no `TierReplicas` | static + guard | `[ScriptBlock]::Create` + `redis`/`prometheus` rejection + forbidden-token grep | ✅ delivered | ✅ green |
| 88-02-T2 | 88-02 | 1 | DISC-05 | T-88-03, T-88-15 | seam (tier,var) allow-list; trailing-hyphen disarm; restore assertion | static + guard | `[ScriptBlock]::Create` + mismatched-seam rejection + disarm-path grep | ✅ delivered | ✅ green |
| 88-03-T1 | 88-03 | 2 | DISC-02 | T-88-04, T-88-05, T-88-07 | loopback-only forward; distinct exit code per abort | static | `[ScriptBlock]::Create` + exit-code-table + forbidden-token grep | ✅ delivered | ✅ green |
| 88-03-T2 | 88-03 | 2 | DISC-04, DISC-05, HAND-04 | T-88-06, T-88-11, T-88-13, T-88-16 | static endpoint table; static-literal `psql -c`; no dependency outage | static | `[ScriptBlock]::Create` + PQ-id + Depth-10 + psql-literal grep | ✅ delivered | ✅ green |
| 88-03-T3 | 88-03 | 2 | DISC-02, DISC-04, DISC-05, DISC-06, HAND-04 | T-88-03, T-88-02, T-88-10 | probe leaves stack clean; branches locked from recorded fields | live + assertion | probe artifact answers + independent `kubectl` replica/env read | ✅ delivered | ✅ green |
| 88-04-T1 | 88-04 | 3 | DISC-01, DISC-03 | T-88-13, T-88-01 | static scenario table; unknown/dropped id → exit 64; non-empty cross-talk enforced | static + guard | `-ScenarioId NOPE-99` exits 64 + panel-title/scenario-row grep | ✅ delivered | ✅ green |
| 88-04-T2 | 88-04 | 3 | DISC-01 | T-88-17, T-88-18, T-88-12 | equal-width pinned windows; Depth-10 artifact written before exit resolution | static | `[ScriptBlock]::Create` + write-before-resolve ordering + Depth-10 grep | ✅ delivered | ✅ green |
| 88-04-T3 | 88-04 | 3 | DISC-01 | T-88-17 | band recomputable from the artifact; Regime-B zero bands verified | live + assertion | `phase-88-BASE-01.json` window/viewport/band assertions | ✅ delivered | ✅ green |
| 88-05-T1 | 88-05 | 4 | DISC-02, DISC-03, DISC-06 | T-88-20, T-88-21, T-88-03 | arm → settle ≥150 s → re-baseline → trigger; disarm in `finally` | static | `[ScriptBlock]::Create` + settle/rollout/cross-talk field grep | ✅ delivered | ✅ green |
| 88-05-T2 | 88-05 | 4 | DISC-02, DISC-03 | T-88-19, T-88-12 | sustained (not burst) load; no `/health/` routes | live + assertion | `phase-88-WEB-01.json` panels 10/11/12/14 moved + cross-talk 1/3/9 held | ✅ delivered | ✅ green |
| 88-05-T3 | 88-05 | 4 | DISC-02, DISC-03 | T-88-16 | no dependency outage substituted; degraded row rather than invented fault | live + assertion | `phase-88-WEB-02.json` branch assertions on the probe field | ✅ delivered | ✅ green — **WEB-02 dropped by measurement** (PQ-03: no safe-input 5xx across seven endpoints). The task's actual contract — a degraded row, not an invented fault — is what held: `Inconclusive`, `Moved: false`, `DependencyOutageDriven: false`. Panel 13 routes to HAND-04. |
| 88-06-T1 | 88-06 | 5 | DISC-02, DISC-03, DISC-06 | T-88-02, T-88-20, T-88-22 | live-read replica restore; telemetry path never scaled | live + assertion | `phase-88-SCALE-01.json` panels 3/4/5 + rollout disjoint + replicas 2 | ✅ delivered | ✅ green |
| 88-06-T2 | 88-06 | 5 | DISC-02, DISC-03, DISC-06 | T-88-02 | orchestrator restored to the live-read 3, verified independently | live + assertion | `phase-88-SCALE-02.json` + direct `kubectl get deploy orchestrator` = 3 | ✅ delivered | ✅ green |
| 88-06-T3 | 88-06 | 5 | DISC-02, DISC-03, DISC-06 | T-88-23 | `NoData` modelled as a first-class state, scored as moved | live + assertion | `phase-88-SCALE-03.json` two consecutive `NoData` + screenshot on disk | ✅ delivered | ✅ green |
| 88-07-T1 | 88-07 | 6 | DISC-02, DISC-03 | T-88-11 | separate workflow, stop-before-delete, cleanup asserted (ghost-cron) | live + assertion | *n/a — scenario dropped* | ✅ `ZERO-01.json` (accepted-unproven row) | ⚠️ n/a — **ZERO-01 dropped by measurement** (PQ-05: a dangling edge is refused 422 at step-create, so the fault cannot be produced). Nothing was seeded, mutated or deleted; `v8-fanout-proof` still counts 1. Panel 6 routes to HAND-04 documentation. |
| 88-07-T2 | 88-07 | 6 | DISC-02, DISC-03, DISC-04, DISC-06 | T-88-12, T-88-20 | two independent signals (numeric + legend name); panel 8 held at 0 | live + assertion | `phase-88-ZERO-02.json` moved + legend-name change + cross-talk 8 = 0 | ✅ delivered | ✅ green |
| 88-07-T3 | 88-07 | 6 | DISC-02, DISC-03, DISC-04, DISC-05, DISC-06 | T-88-03, T-88-01, T-88-15, T-88-21 | seam disarmed in `finally`; independent env read; other guarded zeros held at 0 | live + assertion | `phase-88-ZERO-03.json` + independent `kubectl` env/replica/image read | ✅ delivered | ⚠️ **green on the task, PARTIAL on DISC-04.** The seam fired exactly as designed and every restore/cross-talk assertion held; panel 8 still could not be moved (see the DISC-04 amendment). `OriginalVerdict: Fail` is preserved verbatim in `ZERO-03-asdriven.json`. |
| 88-08-T1 | 88-08 | 7 | DISC-07 | T-88-24, T-88-13 | per-rung independent baseline; `-Rungs` validated | static + guard | `[ScriptBlock]::Create` + `-Rungs 999` exits 64 + rung-table grep | ✅ delivered | ✅ green |
| 88-08-T2 | 88-08 | 7 | DISC-07 | T-88-25, T-88-02 | measured vs theoretical dip recorded side by side; per-rung restore | live + assertion | `phase-88-LADDER-01.json` all 8 rungs + both regime answers | ✅ delivered | ✅ green |
| 88-08-T3 | 88-08 | 7 | DISC-02 | T-88-26, T-88-27 | roll-up reads and tabulates only; no `kubectl`; a gap is a row | static + live | `pwsh -File scripts/phase-88-rollup.ps1` exit 0 + 14-row assertions | ✅ delivered | ✅ green |
| 88-09-T1 | 88-09 | 8 | HAND-03, HAND-04 | T-88-28, T-88-31 | register derived from the matrix; rubric fixed at 6 questions, no score | document + assertion | register-set equality + rubric cell count + panel-7 record grep | ✅ delivered | ✅ green |
| 88-09-T2 | 88-09 | 8 | DISC-01..DISC-07 | T-88-29, T-88-30 | DISC-02/DISC-04 status computed from artifacts, not asserted | document + assertion | status computed from the artifacts + 16 Phase-87 rows untouched | ✅ delivered | ✅ green |
| 88-10-T1 | 88-10 | 9 | HAND-01 | T-88-32, T-88-09 | every row cites a proving scenario; no credential beyond the stated posture | document + assertion | all 14 titles + ≥8 scenario citations + header caveats | ✅ delivered | ✅ green |
| 88-10-T2 | 88-10 | 9 | HAND-02 | T-88-33 | reachability stated as a blocking gap, not softened | document + assertion | sections (a)-(d) + nine PASS/PARTIAL/FAIL tokens + HAND-01/02 Complete | ✅ delivered | ✅ green |
| 88-10-T3 | 88-10 | 9 | HAND-01, HAND-02 | T-88-34, T-88-35, T-88-19 | `k8s/` provably unmodified; C3/C7 guard classification intact | live gate | lint exit 0 + `git status --porcelain k8s/` empty + verify exit 0 | ✅ both scripts exist | ✅ green |
| 88-11-T1 | 88-11 | 10 | HAND-01, HAND-02 | T-88-36, T-88-37, T-88-38 | attributed sign-off; findings recorded, never edited away | manual + pre-flight | pre-flight asserts all four reviewer inputs and a complete 14-row matrix | ✅ delivered | ⚠️ **pre-flight green, manual review NOT performed.** The checkpoint was auto-approved by an unattended chain; 3 of 7 checks ran (all mechanical), checks 3/4/6/7 are marked `NOT PERFORMED` by name. T-88-36 and T-88-37 both verified truthful by the security audit. See Manual-Only below. |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [x] `scripts/phase-88-panel-read.js` — the batch DOM reader (plan 88-01 T2)
- [x] `scripts/lib/phase-88-panel-read.ps1` — the reader invocation contract, banding and movement rule (plan 88-01 T3)
- [x] `scripts/lib/phase-88-cluster-ops.ps1` — live-replica capture, scale sequencer, seam arm/disarm, restore assertions (plan 88-02)
- [x] `scripts/phase-88-wave0-probe.ps1` + `analyzer-reports/phase-88-wave0-probe.json` + `88-PROBE-DECISIONS.md` — the seven blocking questions (plan 88-03)
- [x] `scripts/phase-88-panel-discriminate.ps1` — driver frame, `-Mode Baseline`, `-Mode Scenario`, `-Mode DurationLadder` (plans 88-04, 88-05, 88-08)
- [x] `analyzer-reports/phase-88-BASE-01.json` — the DISC-01 bands every later scenario scores against (plan 88-04 T3)
- [x] `scripts/phase-88-rollup.ps1` + `analyzer-reports/phase-88-discrimination.json` — the panel-keyed matrix (plan 88-08 T3)

**Wave 0 is genuinely blocking.** Plan 88-03's seven probe answers change the structure of the
downstream scenario plans: PQ-01 decides whether panel 2 needs a seam, PQ-02 decides the reader's
primary locator, PQ-03 decides panel 13's fate, PQ-05 decides panel 6's fate, and PQ-06 decides
whether the panel-8 seam scenario can exist at all. No scenario lever may be locked before
`88-PROBE-DECISIONS.md` exists. Set `wave_0_complete: true` only after plan 88-03 Task 3 is green. **Done 2026-07-28** — `88-PROBE-DECISIONS.md` exists, probe verdict `Pass`, all seven questions answered with no `UnevaluableQuestions`. Two of the seven answers dropped a scenario outright (PQ-03 → WEB-02, PQ-05 → ZERO-01), which is the blocking gate doing exactly its job.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions | Status 2026-07-29 |
|----------|-------------|------------|-------------------|-------------------|
| Symptom → panel → action runbook is usable by a team that did not build the system | HAND-01 | Practicality is a judgement, not an assertion — the rubric scores it, a human signs it off | Review each runbook row against the scenario artifact that proved it; reject any row with no backing scenario (plan 88-11 checks 1-3) | ✅ **covered by UAT item 1** — user read the runbook as a stranger and passed it (header reachability, retention horizon, invisible-outage warning). Mechanically: 33 scenario citations across all 15 rows. |
| Handoff readiness verdict (blocking gaps, panels that mislead by default) | HAND-02 | The verdict is a judgement over the full evidence set | Read the roll-up matrix; confirm every accepted-unproven panel names its reason and that the reachability gap is called blocking, not softened (plan 88-11 checks 4-6) | ✅ **covered by UAT items 2 and 3** — user judged the eight blocking gaps unsoftened (incl. B1 reachability) and confirmed SC1 PARTIAL / SC4 FAIL are named rather than rounded up. |
| Panel 7 (`processor_spawn_dropped`) accepted-unproven | HAND-04 | User-locked decision this session — no broker-fault scenario is to be designed | Confirm the findings record + runbook row tell maintenance to corroborate via processor logs (`SpawnSendExhaustedException`, nack-requeue re-fire, broker health) | ✅ mechanically verified — panel 7's register record carries all four named actions (921-char reason); its row states the user decision in the first sentence and notes the motivating hazard was resolved in v12.0.0 Phase 86. |
| Rendered panel matches the runbook's stated bands | DISC-01, HAND-01 | Requires a fresh live render, not an artifact re-read | Open Grafana and walk 2-3 panels against the runbook bands and the scenario artifacts | ✅ **resolved 2026-07-29** — run automatically (read-only, 12/12 reads `Rendered`). Panel 9 matched exactly on both keeper pods (0.2000 ×6). Panel 14: `in-flight` and `kestrel queued` matched at exactly 0; `kestrel active` read 0.0000 ×6 against a stated healthy mean of 0.5. Root cause: BASE-01 captured under the phase's own drive traffic, so 0.5 was residual, not resting. **Runbook corrected** — healthy value now states exactly 0, symptom reworded from "climbs above its usual half-connection" to "rises off zero and stays there", and a reading of 0 explicitly called not-a-fault. |
| Receiver acknowledges a green `0` means "no evidence", not "healthy" | HAND-02 (verdict condition 5) | Only a conversation can put the warning in a person's head | A named receiver says back the five points in `docs/runbooks/business-dashboard-handover-briefing.md` §7 | ❌ **OUTSTANDING** — no conversation held, no receiver named. Plan 88-11 was auto-approved by an unattended chain and explicitly records condition 5 as unsatisfied. Briefing material prepared 2026-07-29; a briefing is not a handover. This is the phase's one genuinely uncovered verification. |

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies — 29/29 tasks carry an `<automated>` command
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references
- [x] No watch-mode flags
- [x] Feedback latency < 10s for the per-commit static gate
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** planned — pending execution

---

## Validation Audit 2026-07-29

| Metric | Count |
|--------|-------|
| Gaps found | 0 automated · 1 manual-only |
| Resolved | 0 (nothing to generate) |
| Escalated | 1 (handover conversation — see Manual-Only) |

**Verdict: NYQUIST-COMPLIANT (automated) / PARTIAL (manual).**

### Gates re-executed during this audit

All commands the map cites were run rather than assumed. Nothing was generated, because nothing
was missing.

| Gate | Result |
|---|---|
| 10 Wave-0 artifacts present on disk | ✅ all |
| `node --check scripts/phase-88-panel-read.js` | ✅ parse OK |
| `[ScriptBlock]::Create` × 5 phase-88 scripts | ✅ all parse OK |
| `pwsh -File scripts/phase-88-rollup.ps1` | ✅ **exit 0** — `class=PASS`, 10/14 Proven, 4/14 AcceptedUnproven, 13/14 misleading-by-default |
| `-ScenarioId NOPE-99` unknown-id guard | ✅ **exit 64** |
| `-Rungs 999` rung guard | ✅ **exit 64** |
| `pwsh -File scripts/phase-87-dashboard-lint.ps1` | ✅ exit 0 — 24 rules incl. C3/C7, 31 panels |
| 13 scenario artifacts present (incl. 3 discarded runs kept) | ✅ all |

### What this audit changed

1. **Statuses were stale.** All 29 task rows read `⬜ pending` and every Wave-0 box was unchecked,
   despite the phase being executed, verified and marked complete. Updated to measured outcomes.
2. **DISC-04's stated behaviour is unachievable** and the map presented it as an ordinary pending
   verification. A future `/gsd-validate-phase` run reading the old text would have tried to generate
   a panel-8 test, and any test that passed would have been asserting the `or vector(0)` guard fires —
   the exact false-green this phase exists to eliminate. Now carries an explicit do-not-generate
   amendment.
3. **Six measurement constraints discovered during execution were absent from the map** (panel 4's
   120 s floor, panel 8's 120 s floor, the 480 s dwell, panel 14's dead instrument, panel 12's
   un-gainable legend, the corrected 225 h retention). Any of these violated by a future re-run
   produces a green result that measured nothing. Added as a standing section.
4. **Two dropped scenarios were recorded as ordinary rows.** WEB-02 (PQ-03) and ZERO-01 (PQ-05) are
   measured-unreachable, not skipped; their task rows now say so rather than implying a run that
   never happened.
5. **Manual-only coverage was overstated.** The table credited plan 88-11's human sign-off for
   HAND-01 and HAND-02, but 88-11 was auto-approved with 4 of 7 checks `NOT PERFORMED`. Those two
   rows are now covered by the 2026-07-29 UAT session instead — which is real coverage, by the user,
   just not the source the map claimed. Two further manual rows were added for verifications the
   planning-time map did not anticipate.

### The one outstanding item

The handover conversation (verdict condition 5). Not automatable — its whole purpose is to put the
"a green `0` can mean nothing was measured" warning into a person's head, and four panels can render
exactly that zero. Tracked in `88-HUMAN-UAT.md` item 5; briefing material at
`docs/runbooks/business-dashboard-handover-briefing.md` §7.
