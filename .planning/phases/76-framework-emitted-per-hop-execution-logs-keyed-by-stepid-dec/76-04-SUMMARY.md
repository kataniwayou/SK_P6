---
phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
plan: 04
subsystem: testing
tags: [observability, analyzer, verdict, inconclusive, pass-fail-engine, exit-codes, anl-04, anl-05, d-01, d-02, d-03, d-04]

# Dependency graph
requires:
  - phase: 76-03
    provides: "Analyzer structural verdict re-keyed onto attributes.StepId; stepId-keyed completeness (ANL-01/02/03, SMP-01); PassFailEngine.cs / PassFailEngineFacts.cs landed clean for Wave-2"
provides:
  - "Verdict.Inconclusive third class (JsonStringEnumConverter → serializes \"Inconclusive\") — an observability-tier failure is never reported as data loss nor a vacuous green (ANL-04)"
  - "Three-class verdict gate splitting on EVIDENCE SUFFICIENCY: trace-dark (startedRuns==0) + self-consistent conservation → Inconclusive regardless of metricGateOk; genuine gap → Fail; complete → Pass"
  - "Metric-gate inversion (ANL-05): a gate failing because the metrics tier is absent/frozen → Inconclusive; a live orch_consumed!=proc_sent gap → Fail (T-76-12 guard)"
  - "scripts/lib/exit-code-resolution.ps1 — dot-sourceable Resolve-AnalyzerExitCode (report→0/1/2) + Resolve-SweepClass (code→class+message), proven hermetically before the live gate (D-02)"
  - "Harness STEP H resolves the authoritative Verdict class from the JSON artifact → exit 2 for Inconclusive; sweep roll-up adds 2→INCONCLUSIVE, sweep-fatal, no auto-retry, distinct message (D-03/D-04)"
affects: [76-05, analyzer, verdict-class, live-gate, phase-67-harness, phase-68-sweep]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Evidence-sufficiency-first verdict: check trace-darkness + conservation self-consistency BEFORE the Pass/Fail metric-gate test, so absence of evidence resolves to Inconclusive not a false FAIL / vacuous green"
    - "Shared dot-sourceable PowerShell resolution lib (pure functions, zero top-level side effects) consumed by both the harness and the sweep — the exit-2 mapping is unit-testable without running either full script"
    - "JSON-artifact-authoritative verdict class: the analyzer report is written before the fixture assert, so the harness resolves the true class from the artifact and overrides the mirrored dotnet-test exit"

key-files:
  created:
    - "scripts/lib/exit-code-resolution.ps1"
  modified:
    - "tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs"
    - "tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs"
    - "scripts/phase-67-harness.ps1"
    - "scripts/phase-68-sweep.ps1"

key-decisions:
  - "The evidence-sufficiency gate is `startedRuns == 0 && conservationOk` — this single predicate subsumes BOTH ANL-04a (trace-dark + self-consistent conservation) and ANL-05 blind (frozen/absent counters read 0==0 → conservationOk true, probe dead → metricGateOk false), so the inversion needs no separate 'is the collector blind' probe. A genuine gap (conservationOk false) is excluded → stays FAIL (T-76-12)."
  - "Inconclusive is asserted REGARDLESS of metricGateOk by ordering the evidence-sufficiency check first; the blind-case metric-gate failure (probe/keeper reading 0 off an absent collector) is thereby never treated as data loss"
  - "Resolve-AnalyzerExitCode fail-closes an unknown/absent verdict to 1 (never a false 0 green, never a false 2 that would suppress a real finding) — T-76-11"
  - "The harness resolves the verdict from the JSON artifact (written before the fixture assert) and OVERRIDES the mirrored dotnet-test exit, because the fixture asserts Verdict==Pass so a non-Pass mirrors exit 1 — the artifact carries the true Inconclusive class"

patterns-established:
  - "Three-class verdict switch in BuildSummary (Pass / Inconclusive / Fail) with a distinct human driver line per class"
  - "Shared exit-code lib dot-sourced by harness + sweep; the sweep's final '0 IFF all PASS' makes any non-zero class (incl. 2) sweep-fatal with no code change"

requirements-completed: [ANL-04, ANL-05]

# Metrics
duration: 23min
completed: 2026-07-16
---

# Phase 76 Plan 04: Three-Class Verdict + Exit-2 Plumbing Summary

**The verdict gained a third class `Inconclusive` that splits on EVIDENCE SUFFICIENCY, not severity: total trace darkness (`startedRuns==0`) with self-consistent conservation is the collector-blind / cold-ES shape and resolves to INCONCLUSIVE regardless of the metric gate (never a false FAIL, never a vacuous green), while a genuine `orch_consumed != proc_sent` gap stays FAIL; the class is plumbed through a dot-sourceable exit-code lib as exit 2 (harness) and a sweep-fatal INCONCLUSIVE roll-up with a distinct no-auto-retry message (sweep) — all proven hermetically before the live gate.**

## Performance

- **Duration:** ~23 min
- **Started:** 2026-07-16T10:27:09Z
- **Completed:** 2026-07-16T10:50:15Z
- **Tasks:** 2 (Task 1 TDD RED→GREEN)
- **Files created:** 1 · **Files modified:** 5

## Accomplishments

- **ANL-04 three classes:** `enum Verdict` gained `Inconclusive` (serializes via the existing `JsonStringEnumConverter`, so the sweep reads `"Inconclusive"` from JSON for free — no new report fields). The `PassFailEngine` verdict gate now checks EVIDENCE SUFFICIENCY first: `traceDark (startedRuns==0) && conservationOk` → `Verdict.Inconclusive`; only when evidence is sufficient does it apply the existing `pass = missing==0 && !dupFail && valueChainOk && metricGateOk` → Pass/Fail.
- **ANL-05 metric-gate inversion:** a metric gate failing *because the metrics tier is absent/frozen* (counters 0==0 → `conservationOk` true, probe dead → `metricGateOk` false) is INCONCLUSIVE, not FAIL — the evidence-sufficiency gate fires regardless of `metricGateOk`. A LIVE genuine gap (`conservationOk` false) is excluded from the gate and stays FAIL (T-76-12: INCONCLUSIVE can never mask a real conservation gap).
- **D-01 preserved:** a reconciled run with a non-zero `TelemetryGap` is already excluded from `missing`, so it stays PASS — the three-class gate never lets a non-binding gap flip the verdict (fact `Verdict_NonZeroTelemetryGap_StaysPass_D01`).
- **D-02/03/04 exit-2 plumbing (hermetic):** new `scripts/lib/exit-code-resolution.ps1` factors the decision into two pure, side-effect-free functions. `Resolve-AnalyzerExitCode` maps Inconclusive→2 / Fail→1 / Pass→0 (unknown→1 fail-closed, T-76-11); `Resolve-SweepClass` maps 2→INCONCLUSIVE with a DISTINCT instrument-failure message advising a deliberate warm-ES re-run with NO auto-retry. Proven hermetically (a synthetic `{"Verdict":"Inconclusive"}` resolves to 2→INCONCLUSIVE with the no-auto-retry message) BEFORE the live gate.
- **Harness + sweep consume the shared lib:** `phase-67-harness.ps1` STEP H dot-sources the lib and resolves the authoritative class from the JSON artifact (overriding the mirrored dotnet-test exit of 1 on a non-Pass), never remapping 2 to an infra code; the exit-code table doc adds `2`. `phase-68-sweep.ps1` dot-sources the lib and replaces the inline class switch with `Resolve-SweepClass`, keeping the final `0 IFF all PASS` so an INCONCLUSIVE run is sweep-fatal (D-03).

## Task Commits

Each task committed atomically:

1. **Task 1 (RED): failing three-class verdict facts + `Verdict.Inconclusive` enum scaffolding** — `d5e74bb` (test)
2. **Task 1 (GREEN): three-class verdict gate + metric-gate inversion (ANL-04, ANL-05)** — `fe9b70c` (feat)
3. **Task 2: dot-sourceable exit-code lib + harness/sweep exit-2 plumbing (D-02/03/04)** — `55eb5ba` (feat)

Task 1 followed the TDD RED→GREEN cycle: the two Inconclusive-asserting facts failed RED (currently a vacuous `Pass` / a false `Fail`), the gate re-thread turned them GREEN. No REFACTOR commit was needed. Task 2 is not TDD-split — its proof is the hermetic acceptance command, run and green before commit.

## Files Created/Modified

- `scripts/lib/exit-code-resolution.ps1` **(created)** — `Resolve-AnalyzerExitCode([object]$Report)` (report→0/1/2, unknown→1 fail-closed) + `Resolve-SweepClass([int]$Code)` (code→`@{Class;Message}`, 2→INCONCLUSIVE with the distinct no-auto-retry message). Pure functions, NO top-level side effects, so dot-sourcing only defines them.
- `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` — added the `Inconclusive` enum member with an XML doc mirroring the existing members; updated the `Verdict` field doc to `"Pass"/"Fail"/"Inconclusive"`.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — re-threaded the verdict gate: `traceDark && conservationOk` short-circuits to `Verdict.Inconclusive` (with a blind-case `CorroborationDetail` line) BEFORE the Pass/Fail test; `recon` recomputed after; `BuildSummary` driver switched to a three-class expression.
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — added 7 facts: `Verdict_TraceDark_ConservationIntact_MetricGateFalse_Yields_Inconclusive` (ANL-04a, metricGateOk false), `..._MetricGateHolds_Yields_Inconclusive_NotVacuousPass` (the vacuous-green fix), `Verdict_PartialEvidence_RealConservationGap_Yields_Fail` (ANL-04b), `Verdict_CompleteCohort_Yields_Pass` (ANL-04c), `Verdict_NonZeroTelemetryGap_StaysPass_D01` (D-01), `MetricGate_CollectorBlind_FrozenCounters_TraceDark_Yields_Inconclusive` (ANL-05 blind), `MetricGate_LiveCounters_GenuineGap_NotInconclusive_Yields_Fail` (ANL-05 live / T-76-12).
- `scripts/phase-67-harness.ps1` — dot-sources the lib (FRAME 2.1); exit-code table adds `2`; STEP H parses the report JSON and sets `$analyzerExit = Resolve-AnalyzerExitCode $reportObj` (overrides the mirrored exit); verdict echo shows the class label via `Resolve-SweepClass`.
- `scripts/phase-68-sweep.ps1` — dot-sources the lib; exit-code doc adds `2`; the inline class switch replaced by `Resolve-SweepClass $code` (2→INCONCLUSIVE + distinct message); final `0 IFF all PASS` unchanged (INCONCLUSIVE sweep-fatal).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `net8.0` verify host instead of the plan's `net9.0`**
- **Found during:** Task 1 (verification)
- **Issue:** The plan's `<verify>`/`<acceptance>` reference `bin/Debug/net9.0/BaseApi.Tests.exe`; the test project targets **net8.0** (on disk + prior-wave context).
- **Fix:** Used `tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe` throughout. Verification-only, no source change.
- **Committed in:** n/a.

**2. [Note] Added a second INCONCLUSIVE fact for the vacuous-green core**
- **Found during:** Task 1 (GREEN)
- **Issue:** The plan's ANL-04a acceptance names the `metricGateOk == false` variant. The actual TEST-01 cold-ES bug is the `metricGateOk == TRUE` case (a clean stack, cold ES) that OLD logic scored a vacuous PASS.
- **Fix:** Kept the required metricGateOk-false fact AND added `..._MetricGateHolds_Yields_Inconclusive_NotVacuousPass` so both directions of "regardless of metricGateOk" are proven. INCONCLUSIVE×3 (exceeds the ×2 floor). No scope creep.
- **Committed in:** `d5e74bb` / `fe9b70c`.

---

**Total deviations:** 2 (1 blocking verify-host, 1 additive test-coverage note). No architectural changes (no Rule 4). Plan executed as written.

## Threat Surface

No NEW security-relevant surface introduced. The three registered threats are mitigated in code:
- **T-76-10 (repudiation — a blind run silently greened by auto-retry):** `Resolve-SweepClass 2` is INCONCLUSIVE with a distinct no-auto-retry message; the sweep's `0 IFF all PASS` makes it sweep-fatal.
- **T-76-11 (tampering — a FAIL mislabelled INCONCLUSIVE):** `Resolve-AnalyzerExitCode` sets 2 ONLY for `Verdict == 'Inconclusive'`; Fail→1, unknown→1 (hermetically asserted).
- **T-76-12 (elevation — INCONCLUSIVE masking a real gap):** the evidence-sufficiency gate requires `conservationOk`; a live `orch_consumed != proc_sent` gap has `conservationOk == false` → excluded → stays FAIL (fact `MetricGate_LiveCounters_GenuineGap_NotInconclusive_Yields_Fail`).

## Known Stubs

None. No hardcoded empty/placeholder values were introduced; the engine change is pure logic and the lib is fully wired into both scripts.

## Verification

- `dotnet build SK_P.sln -c Debug`: **0 Warning, 0 Error**; `-c Release`: **0 Warning, 0 Error**.
- `BaseApi.Tests.exe -- --filter-method "*Verdict*"`: **6/6 green**; `-- --filter-method "*MetricGate*"`: **10/10 green**.
- Full `Observability.Analysis` namespace (changed scope): **44/44 green** (37 prior + 7 new).
- Hermetic exit-code-resolution acceptance: `Inconclusive→2→INCONCLUSIVE` class with the `no auto-retry` message (exit 0); `Fail→1`, `Pass→0` (not mislabelled). Both PowerShell scripts parse via `[ScriptBlock]::Create` with no syntax error and dot-source the shared lib (`rg Resolve-AnalyzerExitCode` harness = 3, `rg Resolve-SweepClass|INCONCLUSIVE` sweep = 5). INCONCLUSIVE roll-up is sweep-fatal (simulated: class INCONCLUSIVE, sweep exit non-zero).
- **Full hermetic suite:** the Docker-less baseline is 272 pre-existing infra-dependent failures (Postgres/RabbitMQ/Redis-connection). Changes are confined to the `Observability.Analysis` namespace + non-compiled PowerShell — no product code touched — so the 44/44 in-scope namespace run is authoritative: **zero NEW failures in changed scope.** (The background-runner tail retained only an infra stack trace; the in-scope namespace check stands in for the full-suite count per the prior-wave guidance.)

## TDD Gate Compliance

Task 1 gate sequence present in git log: `test(76-04)` RED (`d5e74bb`) → `feat(76-04)` GREEN (`fe9b70c`). RED failed on assertion (Inconclusive expected, got Pass/Fail) — not a compile error masking a passing test; the enum scaffolding was intentionally inert. No REFACTOR commit (none needed).

## Next Phase Readiness

- The verdict is now three-class and the exit-2 mapping is proven hermetically, so **76-05 (live gate)** can drive the harness/sweep against a warm live stack and trust that a cold-ES / collector-blind window surfaces INCONCLUSIVE (exit 2, sweep-fatal, deliberate re-run) rather than a false FAIL or a vacuous green.
- **Live surfacing is deferred-automated (Docker-less sandbox):** the harness STEP H override and the sweep roll-up are exercised here only by parse + the hermetic lib acceptance; the end-to-end exit-2 on a real Inconclusive report requires a live stack (precedent phases 68/73/74/75).

## ROADMAP Note

`roadmap.update-plan-progress` reports "no matching checkbox" for phase 76 (per prior-wave guidance). The ROADMAP 76-04 checkbox was marked `[x]` in the established phase style where present; requirements ANL-04/ANL-05 are tracked via ROADMAP + SPEC frontmatter (REQUIREMENTS.md does not exist in this project).

---
*Phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec*
*Completed: 2026-07-16*

## Self-Check: PASSED

All 7 files (1 created, 5 modified, 1 SUMMARY) exist on disk; all three task commits (d5e74bb, fe9b70c, 55eb5ba) present in git history; changed-scope namespace 44/44 green; both builds 0-warning; hermetic exit-code acceptances green.
