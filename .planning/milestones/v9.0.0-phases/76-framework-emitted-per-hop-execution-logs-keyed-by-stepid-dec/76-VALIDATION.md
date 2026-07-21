---
phase: 76
slug: framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
status: draft
nyquist_compliant: false
wave_0_complete: false
created: 2026-07-16
---

# Phase 76 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Seeded from `76-RESEARCH.md` § Validation Architecture. The "signal" is the verdict-class space
> (PASS / FAIL / INCONCLUSIVE) × the redundancy failure modes (missing-one-record, missing-both-records).
> Nyquist here: **each distinguishable verdict outcome needs at least one positive hermetic fact** — a
> green suite must not be vacuously green.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit.v3 under Microsoft.Testing.Platform (MTP) |
| **Config file** | none (MTP-native; host is `tests/BaseApi.Tests/bin/.../BaseApi.Tests.exe`) |
| **Quick run command** | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (run the exe directly — `dotnet test` hangs on Windows MTP; `--filter` is silently ignored) |
| **Targeted run** | `BaseApi.Tests.exe -- --filter-method "*<FactName>*"` |
| **Full suite command** | `BaseApi.Tests.exe` (hermetic) + `pwsh -File scripts/phase-68-sweep.ps1` (live, phase gate only) |
| **Estimated runtime** | hermetic ~seconds–low minutes; live sweep = 7 scenarios × reset+run |

---

## Sampling Rate

- **After every task commit:** Run `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (hermetic quick run).
- **After every plan wave:** Full hermetic suite green (analyzer + pipeline + orchestrator fact classes).
- **Before `/gsd-verify-work`:** Full hermetic suite green, THEN the live sweep after the mandatory SourceHash reseed (rebuild BOTH configs → graph DELETE → seed → `POST /start` **204** → `phase-68-sweep.ps1`).
- **Max feedback latency:** hermetic run (seconds) per commit; live sweep reserved for the phase gate (fine-grained branch logic is already sampled hermetically).

---

## Per-Task Verification Map

> Task IDs are assigned by the planner; this map fixes the requirement → behavior → test-type binding
> the planner must honor. `Test Type` "hermetic" = CapturingLogger / engine fact, no live stack.

| Req | Behavior (distinguishable outcome) | Test Type | Automated Command | File Exists | Status |
|-----|-------------------------------------|-----------|-------------------|-------------|--------|
| FW-01 | one record/hop, all six fields, across Completed/Failed/Cancelled | hermetic (CapturingLogger) | `BaseApi.Tests.exe -- --filter-method "*PerHop*"` | ❌ W0 | ⬜ pending |
| FW-01 | processor with NO author log still yields the framework record | hermetic | same class | ❌ W0 | ⬜ pending |
| FW-01 / D-09 | entry step (`executionId == Guid.Empty`) logged as explicit all-zeros marker | hermetic | same class | ❌ W0 | ⬜ pending |
| FW-01 / D-18 | normal-path record logs the OutputTail-resolved outcome (Completed→Failed downgrade honored) | hermetic | same class | ❌ W0 | ⬜ pending |
| FW-02 / D-11 | 2-way fan-out emits exactly 2 edge records, distinct next-`StepId`s, correct inbound `EntryId` | hermetic | `*OrchestratorPrePipeline*FanOut*` | ❌ W0 (extend `OrchestratorPrePipelineFacts`) | ⬜ pending |
| FW-02 / D-12 / D-13 | terminal-reached record ×2 at `Step_G`, distinct inbound `entryId`; none on L1-miss/clean-absent | hermetic | same class | ❌ W0 | ⬜ pending |
| FW-03 | no template/arg references `validatedData` / `dr.Data` / `d.Payload`; sentinel-through-blob absent from framework logs | hermetic + repo grep | `*Framework*NoPayload*` + grep | ❌ W0 | ⬜ pending |
| FW-04 | throwing/blocking `ILogger` neither fails nor delays the hop; export is batch/drop (structural) | hermetic | `*NonBlocking*` / `*ExportShape*` | ❌ W0 | ⬜ pending |
| ANL-01 | completeness from stepId records; `HopLabels` grep-gone; zero `Step_*` present | hermetic (engine) | `*PassFailEngine*StepId*` | ❌ W0 (extend `PassFailEngineFacts`) | ⬜ pending |
| ANL-02 | dispatched-but-never-executed → FAIL (TEST-08 closed); expected set from ES only (grep: no Postgres/graph) | hermetic (engine) + grep | `*ExpectedSet*` / `*Test08*` | ❌ W0 | ⬜ pending |
| ANL-03 | missing hop record but orchestrator consumed M_N → non-binding gap, **no seed oracle** | hermetic (engine) | `*Reconcile*Redundancy*` | ❌ W0 | ⬜ pending |
| ANL-03 | missing **both** hop record and orchestrator record → binding miss | hermetic (engine) | same class | ❌ W0 | ⬜ pending |
| ANL-04a | total trace darkness + self-consistent conservation → **INCONCLUSIVE** (TEST-01 cold-ES shape) | hermetic (engine) | `*Verdict*Inconclusive*` | ❌ W0 | ⬜ pending |
| ANL-04b | partial evidence + real conservation gap → **FAIL** | hermetic (engine) | `*Verdict*Fail*` | ❌ W0 | ⬜ pending |
| ANL-04c | complete evidence → **PASS** | hermetic (engine) | `*Verdict*Pass*` | ❌ W0 | ⬜ pending |
| ANL-05 | frozen counters + absent traces → INCONCLUSIVE (collector-blind) | hermetic (engine) | `*MetricGate*Blind*` | ❌ W0 | ⬜ pending |
| ANL-05 | live counters, genuine `orch_consumed != proc_sent` gap → FAIL | hermetic (engine) | `*MetricGate*` | ❌ W0 | ⬜ pending |
| SMP-01 | correct Pass/Fail/Inconclusive from framework records only (no `Step_*` / `Received` / `Produced`) | hermetic (engine) | `*OracleAbsent*` | ❌ W0 | ⬜ pending |
| SMP-01 | value oracle absent → value-chain degrades to N/A, never FAIL | hermetic (engine) | same class | ❌ W0 | ⬜ pending |
| All (gate) | 7-scenario sweep after reseed + 204; every non-PASS classified (loss / gap / INCONCLUSIVE / blind-spot closure) | RealStack | `pwsh -File scripts/phase-68-sweep.ps1` | ✅ script exists (add INCONCLUSIVE class) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

### Per-class positive-fact floor (explicit Nyquist samples)
- **PASS:** ≥1 — complete framework-record cohort (ANL-04c).
- **FAIL:** ≥3 — dispatched-but-never-executed (ANL-02), partial+real-gap (ANL-04b), live-counter genuine gap (ANL-05).
- **INCONCLUSIVE:** ≥2 — trace-dark + intact conservation (ANL-04a), frozen counters + absent traces (ANL-05 blind).
- **Reconciliation redundancy:** ≥2 — missing-one reconciles non-binding with no seed oracle (ANL-03), missing-both stays binding (ANL-03).

---

## Wave 0 Requirements

- [ ] `tests/BaseApi.Tests/Processor/…PerHopLogFacts.cs` — FW-01 / D-09 / D-18 (new class, CapturingLogger)
- [ ] extend `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` — FW-02 / D-11 / D-12 / D-13
- [ ] `tests/…/Observability/…FrameworkNoPayloadFacts.cs` — FW-03 (+ repo grep assertion)
- [ ] `tests/…/…NonBlockingLogFacts.cs` — FW-04 (throwing `ILogger` + export-shape structural)
- [ ] extend `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — ANL-01/02/03/04/05 + SMP-01 (three-class + reconciliation)
- [ ] add `StepIdFieldPath` const to `EsIndexNames.cs`; new structural + expected-set ES query bodies in the RealStack fixture
- [ ] harness: map analyzer `Inconclusive` verdict → exit code 2; sweep roll-up: add `2→INCONCLUSIVE` class + distinct message

*No framework install needed — the existing xUnit.v3/MTP host covers every fact type. The gap is new fact classes + the analyzer/harness re-key.*

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Live 7-scenario sweep results are examined and each non-PASS verdict is classified | Acceptance (live gate) | Requires a healthy Docker stack + operator judgement on real telemetry; the classification (genuine loss / telemetry gap / observability-degraded / blind-spot closure) is an interpretive act, not an assert | After reseed + `POST /start` 204, run `pwsh -File scripts/phase-68-sweep.ps1`; for each scenario read the analyzer JSON artifact + roll-up class and record the classification |

---

## Validation Sign-Off

- [ ] All tasks have an `<automated>` verify or a Wave 0 dependency
- [ ] Sampling continuity: no 3 consecutive tasks without automated verify
- [ ] Wave 0 covers all MISSING (❌) references
- [ ] No watch-mode flags
- [ ] Every distinguishable verdict outcome has ≥1 positive fact (per-class floor above)
- [ ] `nyquist_compliant: true` set in frontmatter

**Approval:** pending
