# Phase 78: Rework the resilience sweep to verify purely from the new framework logs - Context

**Gathered:** 2026-07-16
**Status:** Ready for planning
**Source:** In-conversation design discussion (reader-side rework, all four gray areas → aggressive-simplification route)

<domain>
## Phase Boundary

**In scope:** the *reader side* only — adapt the LIVE resilience analyzer to the Phase-77 log shape and re-prove the gate:
1. `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (the ES→RunTrace fixture).
2. `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (the pure verdict engine).
3. The hermetic fact suites that pin the above.
4. Re-run the reseed + 7-scenario live gate (`scripts/phase-68-sweep.ps1` → `scripts/phase-67-harness.ps1`, TEST-01..07) and confirm the verdict reconstructs from framework ES logs alone.

**Out of scope:** any framework *source* change (`BaseProcessor.Core`, `Orchestrator`, `Keeper`, `Processor.Sample`) — that was Phase 77 (already complete). Phase 78 touches TEST + scripts only, EXCEPT the mandatory SourceHash reseed the live run requires (see constraint below) which is an *operational* rebuild, not a source edit.

**Why now:** Phase 77 deliberately changed the framework log *shape* (Tier-1 ids moved to `attributes.*` via scope, empty GUIDs skipped, sample author logs deleted). Those changes temporarily broke the LIVE analyzer (`Category=RealStack`, Docker-gated, so it does NOT run in the hermetic build). This phase fixes the analyzer to the new shape. Sequencing was intentional and user-directed: implement logging first (77), then fix the sweep (78).
</domain>

<decisions>
## Implementation Decisions (LOCKED)

All four gray areas resolved to the **aggressive-simplification route** (delete, don't adapt-and-keep). The user's directive: *"delete all four, gate on baseline reproduction."*

### D-01 — Entry-marker: DELETE the detector entirely (not adapt-and-keep)
The entry marker existed only to *exclude* the Mode-2 all-zeros run from scoring. After Phase 77 the entry record carries **no `attributes.ExecutionId`** (empty GUID skipped by `ExecutionLogScope.BuildState`), so the structural query's existing `exists attributes.ExecutionId` filter (`AnalyzerE2ETests.cs:288`) already drops it before parsing — the exclusion is now free.
- **DELETE** `PassFailEngine.IsEntryMarker` (`:62-63`) and its `scored = runs.Where(!IsEntryMarker)` guard (`:134`) — no scored run can have an empty/absent ExecutionId anymore.
- **DELETE** `AnalyzerE2ETests.IsEntryMarkerExecution` (`:714-715`), the `entryMarkerCorrelations` set (`:543,:587`), and every `IsEntryMarkerExecution(...)` guard in `BuildRunTraces`/dispatch/value loops (`:575,:585,:617,:648`).
- The `exists attributes.ExecutionId` filter stays on all queries — it is now the *sole* entry-marker exclusion mechanism.

**HARD VERIFICATION GATE (researcher/planner MUST confirm before delete lands):** the entry step `Step_A` also produces `M_1`, whose EntryId is the inbound edge on the *first* fan-out record. If `Step_A`'s structural record vanishes (its ExecutionId is absent), `stepIdByMessageId[M_1]` never populates, so the ANL-03 redundancy edge for the first hop cannot resolve. Deleting is correct **ONLY IF** `Step_A` is never a member of any expected set (it is offset-0 seed/entry) AND no downstream reconciliation needs its producer evidence. Verify this holds on the real trace shape; if `Step_A` evidence turns out load-bearing, FALL BACK to adapt-and-keep (relax the query to include `attributes.ExecutionId`-absent records and flip the detector to "attribute absent"). Default is delete.

### D-02 — Keeper discrimination: exclude at the QUERY level (`must_not exists ReinjectOutcome`)
After Phase 77 D2 the keeper opens the execution scope, so keeper REINJECT records gain `attributes.StepId` + `attributes.MessageId` and would enter the structural cohort looking like processor "did-run" hits.
- Add `must_not: [ { "exists": { "field": "attributes.ReinjectOutcome" } } ]` to `BuildStepSearchBody` (`:281`) so keeper records never reach the structural parse. `ReinjectOutcomeFieldPath` already exists (`EsIndexNames.cs:171`).
- Keeper records are **purely excluded** from structural completeness — a reinjected hop does NOT contribute a "did-run" stepId. Recovery is proven separately via the existing `BuildKeeperOutcomeMap` query (its own `exists ReinjectOutcome` filter, `:408`), which feeds `keeperOutcomeByExecution`. The two queries stay symmetric (one requires the field, the other forbids it).
- The parse-level guard may remain as belt-and-suspenders, but the query `must_not` is the real fix.

### D-03 — Value oracle: FULL DELETION (not leave-dormant)
Phase 77 D4 deleted `Processor.Sample`'s author logs, so the value-oracle query (`attributes.StepLabel`/`Produced`) now returns nothing. The phase title says "drop the concrete value oracle"; leaving it dormant is a vacuous-green trap.
- **DELETE** from `AnalyzerE2ETests.cs`: `BuildValueOracleSearchBody` (`:334`), the `valueHits` fetch + `valuesByInstance`/`seedsByExec` construction (`:639-694`), `TryReadProduced`/`TryReadSum` if unused elsewhere, and the value axis of `BuildRunTraces`.
- **DELETE** from `PassFailEngine.cs`: `CheckValueChain` (`:560`), `ExpectedHopOffset` (`:510`), `ResolveSeed` (`:536`), `valueOracleSupplied`/`seedsByExecution` param + the whole value-chain loop (`:337-394`), the WR-02 vacuous-green guard (`:388`), and telemetry-gap **path #1** (value-chain reconciliation, `:243-255`).
- **KEEP** telemetry-gap **path #2** (ANL-03 framework-redundancy, `:264-274`) as the SOLE non-binding reconciliation, alongside structural completeness and the metric gate.
- **DELETE** the hermetic `PassFailEngineValueChainFacts.cs` (375 lines) entirely — not skip. `PassFailEngineFacts.cs` stays (structural/metric branches). The absolute-value terminal-anchor proof stays owned by `FanInHermeticHarnessFacts` (hermetic, reads the durable L2 blob) — that is sufficient; the live analyzer no longer re-proves absolute values.
- Consequence to confirm in D-04: any run previously reconciled *only* by value-chain path #1 must now be caught by ANL-03 path #2 or it becomes a binding miss.

**AMENDMENT (2026-07-16, post-research — `ExpectedHopOffset` coupling, RESEARCH.md Hazard C / Open Q1):** `ExpectedHopOffset` is dual-purpose — beyond the value chain it feeds `valueOracleHopSet` → `ExpectedFor`'s label-fallback, which ~6 SURVIVING hermetic `PassFailEngineFacts` lean on to score a single stalled run as incomplete. Two resolutions were weighed: (A) retain `ExpectedHopOffset` as a structural scaffold; (B) delete it and migrate the ~6 facts to pass an explicit `expectedStepIdsByExecution` set. **DECISION: Option B (delete + migrate).** Rationale: the label-fallback branch is DEAD in production once the value oracle is gone (the live path resolves completeness via the stepId/ANL-02 path, never `DistinctLabels`), so retaining it would leave ~6 facts exercising a never-live branch — the same vacuous-coverage trap D-03 exists to eliminate. Migrating the facts to the explicit stepId expected-set makes them test the SAME mechanism the live gate uses. **The delete + fact-migration is ONE ATOMIC task** (tree never goes red mid-way). This also means `RunTrace.DistinctLabels` and the `r.DistinctLabels.Count > 0` fallback branch in `ExpectedFor` are value-oracle residue and should be removed with it (planner to confirm no other surviving consumer). Also delete the `PassFailEngine_EntryMarker_…_D09` fact — it goes red once the engine stops excluding `Guid.Empty` (D-01).

**Vestigial report fields (RESEARCH.md Open Q2 — Claude's Discretion):** `AnalyzerReport.ValueChainOk`/`ValueChainDetail`/`TelemetryGap`(path-#1-sourced)/`TelemetryGapDetail` become vestigial. The sweep wrapper (`phase-68-sweep.ps1`) reads NEITHER. Planner's discretion: remove them from the report shape if no other consumer reads them, else hardcode. Prefer removal for consistency with the delete-not-dormant posture.

### D-04 — Live-gate acceptance: BASELINE VERDICT REPRODUCTION
- Reseed FIRST per [[rebuild-sourcehash-reseed-order]]: Phase 77 touched `BaseProcessor.Core` production source, so rebuild BOTH host configs + `docker compose build` the images → graph-DELETE → seed → 204 → only then the sweep. (See constraint below.)
- Run `scripts/phase-68-sweep.ps1` over all 7 scenarios (TEST-01..07; run-all + collect, no fail-fast).
- **PASS GATE:** every one of the 7 scenarios reproduces its **prior verdict** from the `analyzer-reports/phase-68-summary.json` baseline, now with the value-oracle axis fully dark. Verdicts must reconstruct from framework ES logs (structural stepId completeness + ANL-03 redundancy + metric gate) ALONE — no `StepLabel`/`Produced` evidence contributes. **Baseline = the committed HEAD version** (`git show HEAD:analyzer-reports/phase-68-summary.json` = all 7 PASS, `startedRuns == completeRuns`); the working-tree copy in `git status` is a STALE single-TEST-07 write — do NOT gate against it. Research confirmed the baseline exercised ZERO tolerance paths, so deleting value-chain path #1 is verdict-neutral by construction.
- The subtle risk this gate catches: a scenario whose incomplete-but-recovered run was previously tolerated via value-chain path #1 must now be tolerated via ANL-03 path #2. ANL-03 was built (Phase 76) precisely to need no seed oracle, so full coverage is the design intent — this gate PROVES it live. If a scenario's verdict legitimately shifts, STOP and treat it as a finding (do not silently re-baseline).
- **Ordering:** hermetic fact suites go GREEN first (0-warning Debug+Release, changed TEST scope has zero NEW failures per the [[hermetic-test-command]] baseline of 272 pre-existing Docker-less failures), THEN the live reseed + sweep.
- Beware the [[phase-68-sweep-stale-report-read]] trap: the roll-up can read a stale Debug analyzer report; trust the harness exit code, and TEST-01 can flake on cold ES (re-run deliberately, INCONCLUSIVE is not FAIL).

### Claude's Discretion
- Exact ES query JSON shape for the `must_not` clause (D-02) and whether to keep the parse-level keeper guard.
- Whether `TryReadSum`/`TryReadProduced`/`TryReadTimestamp` are deleted or retained (retain any still used by the surviving structural/trip-duration path).
- Task decomposition and commit granularity.
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### The analyzer being reworked (primary targets)
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — the ES→RunTrace fixture; ES queries (`:281`/`:308`/`:334`/`:396`), `BuildRunTraces` (`:528`), `IsEntryMarkerExecution` (`:714`), `BuildKeeperOutcomeMap` (`:442`)
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — the pure verdict engine; `IsEntryMarker` (`:62`), value-chain machinery (`:337-394`, `:504-581`), telemetry-gap paths (`:243-274`), metric gate (`:399-429`), verdict (`:431-470`)
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — hermetic structural/metric facts (KEEP)
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs` — hermetic value-chain facts (DELETE per D-03)
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` — field-path constants (`ReinjectOutcomeFieldPath:171`, `ExecutionIdFieldPath:107`, `StepIdFieldPath:129`, `NextStepIdFieldPath:149`, `StepLabelFieldPath:87`)

### Prior context (the log-shape change that drives this rework)
- `.planning/phases/77-*/77-CONTEXT.md` — the Phase-77 logging refactor; the two "Load-bearing couplings — DOCUMENT for Phase 78" items (entry-marker=absent-ExecutionId; keeper discrimination via ReinjectOutcome) are the direct inputs to D-01/D-02
- `.planning/phases/76-*/76-SPEC.md` — the FW-01..04 / ANL-01..05 / SMP-01 record + analyzer-branch definitions
- `.planning/phases/76-*/76-05-SUMMARY.md` — the dispatch-in-degree convergent-terminal live-gate fix this analyzer builds on

### Live gate (reseed + sweep)
- `scripts/phase-68-sweep.ps1` — the 7-scenario roll-up wrapper (TEST-01..07, run-all no-fail-fast); exit 0 iff all PASS
- `scripts/phase-67-harness.ps1` — the per-scenario harness invoked by the sweep (clean→seed→204-gate→observe→crash→health-wait→analyze→teardown)
- `analyzer-reports/phase-68-summary.json` — the BASELINE verdicts the D-04 gate reproduces against
</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **ANL-03 framework-redundancy path** (`PassFailEngine.cs:264-274`): already needs no seed oracle — becomes the sole non-binding reconciliation after D-03. Built in Phase 76 exactly for this.
- **`BuildKeeperOutcomeMap`** (`AnalyzerE2ETests.cs:442`): already reads `attributes.ReinjectOutcome` in its own query — the symmetric partner to the D-02 `must_not` structural exclusion. No new keeper machinery needed.
- **Dispatch in-degree convergent derivation** (`AnalyzerE2ETests.cs:604-637`): the Phase-76 live-gate fix; unaffected by this rework, keep intact.
- **`exists attributes.ExecutionId` filter**: after D-01 this is the entry-marker exclusion — no separate detector.

### Established Patterns
- ES reads are defensive (`TryGetProperty`/`ValueKind` checks, odd-shaped JSON dropped never thrown — T-66-09). Deletions must not break this contract on surviving paths.
- Hermetic facts feed the engine SYNTHETIC `RunTrace`/`PromCounterSnapshot` — deleting the value axis must keep the structural/metric facts building `RunTrace` from explicit values (unaffected by live log-shape).

### Integration Points
- The pure engine (`PassFailEngine`) has NO IO — every deletion is provable hermetically before the live run.
- Live path: fixture parses ES → builds cohort → calls `Analyze` → writes `analyzer-reports/{scenarioId}.json` + `.txt`. Only the parse (fixture) and scoring (engine) change; the report shape and sweep wrapper stay.
</code_context>

<specifics>
## Specific Ideas
- One-line intent: **the verdict now stands on structural stepId completeness + ANL-03 framework-redundancy + the Prometheus metric gate — the concrete value oracle is gone, and the entry marker excludes itself by absent-ExecutionId.**
- The Prometheus metric gate (MG-1/2/3, `PassFailEngine.cs:399-429`) is PRESERVED unchanged as the secondary collector-blind axis — it is a KEEP, not a gray area.
- Test host is **net8.0** (`tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe`); run directly per [[hermetic-test-command]] (`dotnet test` hangs on Windows MTP); `--filter-not-trait Category=RealStack` for the hermetic subset.
- REQUIREMENTS.md does not exist for this milestone; requirements tracked via ROADMAP + this CONTEXT.
</specifics>

<deferred>
## Deferred Ideas
- **MessageId-graph reconstruction** (nodes=MessageId, edges=consumed-EntryId→produced-MessageId) — the Phase-77-deferred fallback if outcome-shaped records + ANL-03 prove insufficient. Only pursue if D-04 baseline reproduction fails in a way ANL-03 cannot cover; not otherwise in scope.

None else — discussion stayed within phase scope.
</deferred>

---

*Phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr*
*Context gathered: 2026-07-16 via in-conversation design discussion*
