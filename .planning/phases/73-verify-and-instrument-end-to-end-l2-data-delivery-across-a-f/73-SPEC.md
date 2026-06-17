# Phase 73: Verify & Instrument End-to-End L2 Data Delivery (Fan-In Terminal G) — Specification

**Created:** 2026-06-17
**Ambiguity score:** 0.17 (gate: ≤ 0.20)
**Requirements:** 7 locked

## Goal

Prove and instrument end-to-end L2 data delivery across the DAG `A→B→C→{D1→E1→F1, D2→E2→F2}→G` — where `G` is **one shared, per-arrival non-joining terminal** reached from both `F1` and `F2` — per `(correlationId, executionId)`, via (a) a **hermetic zero-Docker harness** that proves **fan-in `entryId` handling correctness at `G`** plus per-branch L2 content over the real pipeline classes, and (b) a **live-stack per-`executionId` Elasticsearch auditor** asserting path completeness through `G`'s `completed-terminal`, `sha256(in)` path-presence, and the persisted terminal `skp:out:` anchor, plus **execution-trip duration** from ES `@timestamp` deltas — accomplished with **exactly one production-code edit** (a `sha256(in)` log line inside `SampleProcessor.ProcessAsync`).

## Background

The DAG runs today as a fan-out over a single `(correlationId, executionId)` "run": each executionId traverses all **9 labels** — `{Step_A, Step_B, Step_C, Step_D1, Step_E1, Step_F1, Step_D2, Step_E2, Step_F2}` (the fan-out is at `C`; both sinks `F1`+`F2` are reached within one run). `SampleProcessor` Mode-2 (`executionId == Guid.Empty`) spawns **2** distinct executionIds per cron fire (2 runs); Mode-1 accumulates deterministically and reuses the inbound executionId (`SampleProcessor.cs:45-76`). Steps log only `"{StepLabel} had the following numbers: {Numbers}"` — no hash.

`PassFailEngine` (`tests/.../Observability/Analysis/PassFailEngine.cs:50-54`) hard-codes the 9-label completeness set (`AllLabels`, `LabelsPerRun = 9`) and renders an **ES-binding** verdict (started runs = distinct `(correlationId, executionId)` with ≥1 `Step_*` log; complete = full label set; duplicates fail-closed) with Prometheus as non-binding corroboration (67-03). There is **no `G`, no hash awareness, no convergent/fan-in step, and no trip-duration measurement** today. The hermetic pipeline facts (`DispatchTestKit.cs`) use per-scenario NSubstitute `IDatabase` fakes — there is **no stateful, dict-backed L2** that survives a multi-hop DAG round-trip.

The processor entryId↔messageId threading landed in Phase 71 (A1) and the always-write + uniform orchestrator pre-pipeline in Phase 72; this phase **verifies** the resulting delivery behavior at a convergent terminal and **instruments** it for the live stack. The business metrics are in flux: an unscheduled, unimplemented reshape sits in `Import/72-SPEC.md` (`{service}_messages_consumed`/`_sent` + `keeper_l2_probe`). Today's live counters remain `orchestrator_dispatch_sent` / `processor_result_sent` / etc. — so **all metric-conservation assertions are deferred out of this phase** (they would bind to names about to change).

## Requirements

1. **Shared convergent terminal `G` (seed/data, not code)**: One `G` step, reachable from both `F1` and `F2`, with no successor.
   - Current: the DAG ends at the two sinks `F1`/`F2`; the seeded workflow + `PassFailEngine.AllLabels` (9 labels) have no convergence point and no `G`
   - Target: a single `G` step (the existing `SampleProcessor`, Mode-1) is seeded so that **both** `F1` and `F2` list `G` as their next step, and `G` itself has no next step (`completed-terminal`); `G` is reached **once per inbound arrival** within each run (from `F1` and from `F2`) → 2 `G` executions per `(correlationId, executionId)`
   - Acceptance: the seeded DAG has the shared `G` reachable from both `F1` and `F2` with no successor; a hermetic full-DAG run shows `Step_G` executing **exactly twice** per run — once per inbound branch — each as a `completed-terminal`, with no waiting for or aggregation of the other arrival

2. **Fan-in `entryId` handling correctness (hermetic — the core target)**: At `G`, the two converging results carry distinct `entryId`s and are processed independently (no join).
   - Current: no test exercises a convergent step; pipeline facts are single-arrival per scenario over stateless NSubstitute `IDatabase` fakes
   - Target: a hermetic harness drives the full DAG (incl. convergent `G`) over the **real pipeline classes** + a **stateful dict-backed `IDatabase` L2** + a `CapturingSendProvider` feedback loop; each arrival at `G` (same `executionId`, distinct `entryId`/`messageId` from `F1` vs `F2`) reads **its own** `entryId` input blob, writes **its own** `out:` blob, and the two arrivals never share mutable state, never join/wait, and never drop an arrival
   - Acceptance: a hermetic test feeds the `F1` and `F2` outputs into `G`; asserts `G` is invoked exactly twice, each invocation's input equals the blob at its own `entryId`, two distinct `out:` blobs are written, both `Completed`; **reversing the arrival order yields identical results** (order-independent, non-joining)

3. **Per-branch L2 content correctness (hermetic)**: Each executionId's L2 `data:`/`out:` blobs carry the expected deterministic per-branch values end-to-end through `G`.
   - Current: no hermetic assertion of end-to-end L2 blob content across the multi-hop DAG
   - Target: over the dict-backed L2, assert each hop's `out:` blob content equals the deterministic `SampleProcessor` transform (Mode-1 accumulation), per branch, per executionId, through `G`'s terminal blobs
   - Acceptance: a hermetic run asserts the `data:`/`out:` blob values at each hop equal the expected `SampleProcessor` output; no blob missing; no cross-executionId contamination (a value from one executionId never appears under the other)

4. **`sha256(in)` logging in the virtual seam ONLY**: `SampleProcessor.ProcessAsync` logs the sha256 of its inbound `validatedData`; no other production code changes.
   - Current: `ProcessAsync` (`SampleProcessor.cs:39-76`) logs only the numbers line; no hash; the framework/orchestrator/keeper carry no per-step input-hash log
   - Target: `ProcessAsync` emits a structured log carrying `sha256(validatedData)` (hex), `StepLabel`, `ExecutionId`, `CorrelationId` — **hashes only** (T-70-10); the raw payload is never logged; applies in both Mode-2 and Mode-1. **No `src/` file other than `SampleProcessor.cs` is modified, and only inside `ProcessAsync`** (`BaseProcessor`, `ProcessorPipeline`, `OutputTail`, metrics classes, orchestrator, keeper all untouched)
   - Acceptance: `git diff` of `src/` shows changes **only** in `SampleProcessor.cs`, only within `ProcessAsync`; the new log emits a 64-hex-char sha256 of `validatedData` plus `StepLabel` + `ExecutionId`; no raw payload appears in any log; 0-warning Debug + Release build

5. **Live-stack per-`executionId` ES auditor (extended for `G` + hash path)**: The analyzer asserts, per `(correlationId, executionId)`, path completeness through `G` + `sha256(in)` presence + terminal anchor.
   - Current: `PassFailEngine.AllLabels` = 9 labels, `LabelsPerRun = 9`; no `G`, no hash awareness; verdict = distinct-label completeness + fail-closed duplicates + non-binding Prom corroboration
   - Target: extend the completeness set to the **10-label** set including `Step_G` with **expected multiplicity 2 per run** (the fan-in); the auditor asserts each started `(correlationId, executionId)` logged a `sha256(in)` for every expected label, the path reaches `G`'s `completed-terminal` **twice**, the persisted terminal `skp:out:` blob exists and the auditor's own sha256 of that blob is well-formed (terminal anchor), and there are zero silent-loss signals. "Hash continuity" here = **path-presence + terminal-anchor** (in-only hashing — see Constraints; per-hop output↔input cryptographic equality is explicitly NOT claimed)
   - Acceptance: `PassFailEngine` + `AnalyzerReport` updated; an engine fixture over synthesized ES traces yields **Pass** when every run reaches `G` (`Step_G` ×2) with a `sha256(in)` present for each expected label and a terminal `skp:out:` blob present, and **Fail** on any missing label / missing `G` arrival / absent terminal blob / duplicate `(correlationId, StepLabel, entryId)`; hermetic engine facts GREEN

6. **Execution-trip duration from ES timestamps (ES-only)**: Per-`executionId` and per-`correlationId` trip duration computed from ES `@timestamp` deltas.
   - Current: no trip-duration measurement exists; no `orchestrator_trip_duration_ms` histogram
   - Target: the auditor computes, per `(correlationId, executionId)`, the `@timestamp` delta from the run's first step log to `G`'s terminal log, plus a per-`correlationId` aggregate; surfaced in `AnalyzerReport`. **No histogram, no `TripStartedUtc` threading, no `src/` change for this**
   - Acceptance: `AnalyzerReport` carries a per-exec and per-corr trip duration (ms) derived from the ES `@timestamp` min→max span of the run; a fixture asserts the computed delta equals the synthesized timestamp span; no `orchestrator_trip_duration_ms` series is emitted and no `src/` change is made for trip duration

7. **Automated, deferred live run + clean build**: The live auditor is packaged as a one-command automated script; the phase closes on hermetic green without a live Docker run.
   - Current: prior live proofs (Phase 68, 72) were deferred-automated because the sandbox has no Docker
   - Target: a single script (analogous to `scripts/phase-68-sweep.ps1`) seeds the `G`-extended DAG, drives the live round-trip, and invokes the auditor end-to-end; the hermetic harness + engine facts run green at 0-warning Debug + Release; the **live Docker run is deferred-automated** (operator-runnable, not a phase gate)
   - Acceptance: the automated live script exists and is syntactically runnable; the hermetic suite is GREEN; 0-warning Debug + Release; the live Docker run is documented as deferred-automated (not blocking phase close)

## Boundaries

**In scope:**
- A single shared convergent terminal `G` (seed/workflow data), reachable from both `F1` and `F2`, with per-arrival non-joining semantics
- A hermetic zero-Docker harness (real pipeline classes + stateful dict-backed `IDatabase` L2 + `CapturingSendProvider` loop) proving fan-in `entryId` handling correctness + per-branch L2 content through `G`
- Exactly one `src/` edit: a `sha256(in)` log line inside `SampleProcessor.ProcessAsync`
- An extended live ES auditor: 10-label completeness set incl. `Step_G` (×2/run), `sha256(in)` path-presence, persisted terminal `skp:out:` anchor, zero silent-loss
- Per-`executionId` + per-`correlationId` trip duration from ES `@timestamp` deltas
- An automated live auditor script; deferred-automated live run
- Hermetic GREEN + 0-warning Debug & Release

**Out of scope:**
- The hermetic metric-conservation proof and **any** metric-counter assertions — deferred; the business metrics are in flux pending the unscheduled `Import/72-SPEC.md` reshape, so binding to today's names is fragile
- The `Import/72-SPEC.md` `*_messages_consumed`/`_sent` + `keeper_l2_probe` reshape itself — separate, unscheduled
- `sha256(out)` logging — **in-hash only**; consequently per-hop output↔input cryptographic equality (mid-chain mutation detection) is NOT proven, only path-presence + terminal-anchor
- `orchestrator_trip_duration_ms` histogram + `TripStartedUtc` threading — ES-only this phase
- **Any** `src/` change outside `SampleProcessor.ProcessAsync` — no framework/orchestrator/keeper/metrics edits
- An actual live Docker run as a close gate — deferred-automated (sandbox has no Docker)
- Redesigning the existing upstream DAG / executionId / spawn mechanics — already built and proven in Phase 67/68

## Constraints

- Only **one** `src/` file may change (`Processor.Sample/SampleProcessor.cs`), and only **inside** `ProcessAsync`; everything else is test, script, or seed code.
- Logs carry **hashes only** (T-70-10) — raw payloads are never logged.
- Trip duration is computed from ES `@timestamp` deltas only — no histogram, no `TripStartedUtc`.
- The hermetic proof uses **zero Docker**; the dict-backed L2 must be **stateful** across the full multi-hop DAG (a write is visible to every later read under the same key).
- `G` is **one shared step** with **non-join** semantics — per-arrival execution, order-independent, no aggregation/wait.
- Build MUST be 0-warning in both Debug and Release; verification is machine-verified (never "human verification").

## Acceptance Criteria

- [ ] A single shared `G` step is seeded, reachable from both `F1` and `F2`, with no successor (`completed-terminal`)
- [ ] The hermetic full-DAG harness (real pipeline classes + stateful dict-backed L2 + `CapturingSendProvider`) drives the DAG through `G` and is GREEN
- [ ] At `G`, the two converging arrivals (distinct `entryId`s, same `executionId`) are each processed independently — `G` invoked exactly twice per run, each reading its own `entryId` blob and writing its own `out:` blob; arrival-order-independent; never joins/waits
- [ ] Per-branch L2 `data:`/`out:` content matches the deterministic `SampleProcessor` transform end-to-end through `G`; no cross-executionId contamination
- [ ] `git diff` of `src/` shows changes **only** in `SampleProcessor.ProcessAsync`; a `sha256(in)` (64-hex) + `StepLabel` + `ExecutionId` log line is emitted in both modes; no raw payload is logged
- [ ] `PassFailEngine` completeness set is extended to 10 labels incl. `Step_G` (expected ×2/run); Pass requires `sha256(in)` present per label + path to `G`'s `completed-terminal` + persisted `skp:out:` terminal blob; Fail on missing label / missing `G` arrival / absent terminal blob / duplicate
- [ ] `AnalyzerReport` carries per-exec + per-corr trip duration (ms) from ES `@timestamp` deltas; no `orchestrator_trip_duration_ms` series is emitted
- [ ] No metric-counter assertions appear anywhere in the new harness or auditor (metric conservation deferred)
- [ ] An automated live auditor script exists and is runnable; the live Docker run is deferred-automated (not a phase gate)
- [ ] Hermetic suite GREEN + 0-warning Debug & Release build

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                                       |
|--------------------|-------|------|--------|-----------------------------------------------------------------------------|
| Goal Clarity       | 0.88  | 0.75 | ✓      | Proof re-pointed to fan-in entryId correctness; one shared G; ES-only trip  |
| Boundary Clarity   | 0.85  | 0.70 | ✓      | Metrics deferred, reshape out, histogram out, code surface = ProcessAsync   |
| Constraint Clarity | 0.80  | 0.65 | ✓      | One src edit, hash-only, stateful dict L2, non-join, 0-warning              |
| Acceptance Criteria| 0.75  | 0.70 | ✓      | Fan-in correctness, hash-path + terminal anchor, trip delta, hermetic green |
| **Ambiguity**      | 0.17  | ≤0.20| ✓      |                                                                             |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                  | Decision locked                                                                 |
|-------|-----------------|---------------------------------------------------|---------------------------------------------------------------------------------|
| 1     | Researcher      | Is `G` a real new step or just a label?           | A real step using the existing `SampleProcessor`, appended after the fan-out    |
| 1     | Researcher      | Is the `*_messages_*` metrics reshape implemented?| No — unscheduled in `Import/`; 73 uses today's names, reshape out of scope      |
| 1     | Researcher      | Live half: run vs deferred-automated?             | Deferred-automated (no Docker in sandbox); auditor built + automated, not gated |
| 2     | Simplifier      | One shared `G` or two `G1`/`G2`?                   | One shared `G`; goal is to prove fan-in `entryId` handling correctness          |
| 2     | Boundary Keeper | Metric-conservation hermetic assertions?          | Deferred — all metric-counter assertions out of scope (metrics in flux)         |
| 2     | Boundary Keeper | Where does `sha256` logging live + which side?    | `sha256(in)` ONLY, inside the virtual `ProcessAsync`; no other `src/` change    |
| 2     | Simplifier      | Trip duration: ES-only or also histogram?         | ES `@timestamp` deltas only; histogram + `TripStartedUtc` out of scope          |

---

*Phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f*
*Spec created: 2026-06-17*
*Next step: /gsd-discuss-phase 73 — implementation decisions (how to build the hermetic harness, the dict-backed L2, the auditor extension, and the live script)*
