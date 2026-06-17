# Phase 73: Verify & Instrument End-to-End L2 Data Delivery (Fan-In Terminal G) — Specification

**Created:** 2026-06-17
**Amended:** 2026-06-17 (discuss-phase: `sha256(in)` hashing → deterministic incrementing-value tracking)
**Ambiguity score:** 0.15 (gate: ≤ 0.20)
**Requirements:** 7 locked

## Goal

Prove and instrument end-to-end L2 data delivery across the DAG `A→B→C→{D1→E1→F1, D2→E2→F2}→G` — where `G` is **one shared, per-arrival non-joining terminal** reached from both `F1` and `F2` — per `(correlationId, executionId)`, via (a) a **hermetic zero-Docker harness** that proves **fan-in `entryId` handling correctness at `G`** plus **deterministic per-hop value integrity** over the real pipeline classes, and (b) a **live-stack per-`executionId` Elasticsearch auditor** asserting the deterministic value chain (each hop `+1` from fixed seeds `100`/`200`), `Step_G` ×2 fan-in convergence at the expected terminal value, path completeness through `G`'s `completed-terminal`, the persisted terminal `skp:out:` anchor, and per-exec/per-corr trip duration from ES `@timestamp` deltas — accomplished with **one production-code edit** (deterministic seed + increment + value-clarifying log inside `SampleProcessor.ProcessAsync`).

## Background

The DAG runs today as a fan-out over a single `(correlationId, executionId)` "run": each executionId traverses all **9 labels** — `{Step_A, Step_B, Step_C, Step_D1, Step_E1, Step_F1, Step_D2, Step_E2, Step_F2}` (the fan-out is at `C`; both sinks `F1`+`F2` are reached within one run). `SampleProcessor` Mode-2 (`executionId == Guid.Empty`) spawns **2** distinct executionIds per cron fire (2 runs); Mode-1 accumulates `incoming + config.Number` and reuses the inbound executionId (`SampleProcessor.cs:45-76`). Today Mode-2 generates **random** numbers (`baseNumber + Random.Shared.Next(0,100)`) and each step's assignment payload carries a distinct number (`A=1 … F2=9`, `FanOutSeederE2ETests.cs:64-75`) — so the data values are **not trackable** end-to-end.

`PassFailEngine` (`tests/.../Observability/Analysis/PassFailEngine.cs:50-54`) hard-codes the 9-label completeness set (`AllLabels`, `LabelsPerRun = 9`) and renders an **ES-binding** verdict (started = distinct `(correlationId, executionId)` with ≥1 `Step_*` log; complete = full label set; **any** `StepLabel` appearing twice within one run = duplicate → fail-closed) with Prometheus as non-binding corroboration (67-03). There is **no `G`, no value-chain awareness, and no trip-duration measurement** today. The hermetic pipeline facts (`DispatchTestKit.cs`) use per-scenario NSubstitute `IDatabase` fakes — there is **no stateful, dict-backed L2** that survives a multi-hop DAG round-trip.

The processor `entryId`↔`messageId` threading landed in Phase 71 (A1) and always-write + uniform orchestrator pre-pipeline in Phase 72; this phase **verifies** the resulting delivery behavior at a convergent terminal and **instruments** it. Business metrics are in flux (an unscheduled, unimplemented reshape sits in `Import/72-SPEC.md`), so **all metric-conservation assertions are deferred out of this phase**.

## Requirements

1. **Shared convergent terminal `G` (seed/data, not code)**: One `G` step, reachable from both `F1` and `F2`, with no successor.
   - Current: the DAG ends at the two sinks `F1`/`F2`; the seeded workflow + `PassFailEngine.AllLabels` (9 labels) have no convergence point and no `G`
   - Target: a single `G` step (the existing `SampleProcessor`, Mode-1) is seeded so that **both** `F1` and `F2` list `G` as their next step, and `G` itself has no next step (`completed-terminal`); `G` is reached **once per inbound arrival** within each run (from `F1` and from `F2`) → 2 `G` executions per `(correlationId, executionId)`
   - Acceptance: the seeded DAG has the shared `G` reachable from both `F1` and `F2` with no successor; a hermetic full-DAG run shows `Step_G` executing **exactly twice** per run — once per inbound branch — each as a `completed-terminal`, with no waiting for or aggregation of the other arrival

2. **Fan-in `entryId` handling correctness (hermetic — the core target)**: At `G`, the two converging results carry distinct `entryId`s and are processed independently (no join).
   - Current: no test exercises a convergent step; pipeline facts are single-arrival per scenario over stateless NSubstitute `IDatabase` fakes; `entryId`/`messageId` are framework-owned and **not visible inside `ProcessAsync`**
   - Target: a hermetic harness drives the full DAG (incl. convergent `G`) over the **real pipeline classes** + a **stateful dict-backed `IDatabase` L2** + a `CapturingSendProvider` feedback loop; each arrival at `G` (same `executionId`, distinct `entryId`/`messageId` from `F1` vs `F2`) reads **its own** `entryId` input blob, writes **its own** `out:` blob, and the two arrivals never share mutable state, never join/wait, never drop an arrival. The hermetic harness (which sees the framework layer directly) **owns the `entryId`-distinctness proof**
   - Acceptance: a hermetic test feeds the `F1` and `F2` outputs into `G`; asserts `G` is invoked exactly twice, each invocation's input equals the blob at its own `entryId`, two distinct `out:` blobs are written, both `Completed`; **reversing the arrival order yields identical results** (order-independent, non-joining)

3. **Deterministic per-hop value integrity**: Fixed seeds + uniform `+1` per hop make the L2 data value at every step predictable and assertable per `(correlationId, executionId)`.
   - Current: Mode-2 seeds **random** values; each step's payload `number` differs (`A=1 … F2=9`); the data carried through L2 is not trackable hop-to-hop
   - Target: Mode-2 seeds two **fixed** values **`100`** and **`200`** (one per spawned execution); **every** step's assignment payload `number = 1` so each Mode-1 hop increments by exactly `+1`; the L2 `data:`/`out:` blob value at each hop equals `seed + hop-count` (e.g. exec_a: `B=101, C=102, D=103, E=104, F=105, G→106`; exec_b: `+100`)
   - Acceptance: a hermetic run asserts the exact value at each label per execution (`B=seed+1 … F=seed+5`, terminal `G` produces `seed+6`); both `G` arrivals in an execution carry the **identical** expected terminal value; no value drift mid-chain; no cross-executionId contamination (a value from one execution never appears under the other)

4. **Value-clarifying log in the virtual seam ONLY**: `SampleProcessor.ProcessAsync` seeds the fixed values and logs the per-step values; no other production code changes.
   - Current: `ProcessAsync` (`SampleProcessor.cs:39-76`) logs only `"{StepLabel} had the following numbers: {Numbers}"`; Mode-2 is random
   - Target: `ProcessAsync` (Mode-2) seeds fixed `100`/`200` and logs the seeded values; Mode-1 logs a line clarifying `received → produced` (e.g. `"Step_X received {in} produced {out}"`) with `StepLabel` (+ ambient `ExecutionId`/`CorrelationId` via the existing `ExecutionLogScope`). **No `src/` file other than `SampleProcessor.cs` is modified, and only inside `ProcessAsync`** (`BaseProcessor`, `ProcessorPipeline`, `OutputTail`, metrics classes, orchestrator, keeper all untouched). The logged integers are **synthetic deterministic proof values** — not real/sensitive payloads; no framework-level payload logging is introduced
   - Acceptance: `git diff` of `src/` shows changes **only** in `SampleProcessor.ProcessAsync`; the log carries the `received` + `produced` integer values + `StepLabel` per step; the values are deterministic across runs; 0-warning Debug + Release build

5. **Live-stack per-`executionId` ES auditor (extended for `G` + value chain)**: The analyzer asserts, per `(correlationId, executionId)`, the deterministic value chain through `G` + `Step_G` ×2 convergence + terminal anchor.
   - Current: `PassFailEngine.AllLabels` = 9 labels, `LabelsPerRun = 9`; no `G`; any `StepLabel` ×2 within a run is a duplicate → fail-closed (which a correct `G` fan-in would trip); no value awareness
   - Target: extend the completeness set to the **10-label** set including `Step_G`; teach the auditor that the **convergent terminal `Step_G` has expected multiplicity 2** per `(corr, exec)` — the legitimate fan-in, NOT a duplicate-fail; assert each label's logged value equals the expected `seed + hop-count`, both `Step_G` arrivals at the expected terminal value, the path reaches `G`'s `completed-terminal`, the persisted terminal `skp:out:` blob exists and carries the expected value (terminal anchor), and there are zero silent-loss signals. A genuine **same-`entryId` redelivery** still fails closed
   - Acceptance: `PassFailEngine` + `AnalyzerReport` updated; an engine fixture over synthesized ES traces yields **Pass** when every `(corr, exec)` shows the correct incrementing value chain through `Step_G` ×2 (at the expected terminal value) with a terminal `skp:out:` blob present, and **Fail** on a wrong value / missing label / `Step_G` arrival count ≠ 2 / absent terminal blob / a same-`entryId` redelivery; hermetic engine facts GREEN

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
- A hermetic zero-Docker harness (real pipeline classes + stateful dict-backed `IDatabase` L2 + `CapturingSendProvider` loop) proving fan-in `entryId` handling correctness + deterministic per-hop value integrity through `G`
- Exactly one `src/` edit: deterministic seed (`100`/`200`) + uniform `+1` increment behavior + a value-clarifying log, all inside `SampleProcessor.ProcessAsync`
- The seeder data change (every payload `number = 1`; add `Step_G`) in `FanOutSeederE2ETests`
- An extended live ES auditor: 10-label completeness set incl. `Step_G` (×2 convergence multiplicity), deterministic value-chain assertion, persisted terminal `skp:out:` anchor, zero silent-loss
- Per-`executionId` + per-`correlationId` trip duration from ES `@timestamp` deltas
- An automated live auditor script; deferred-automated live run
- Hermetic GREEN + 0-warning Debug & Release

**Out of scope:**
- The hermetic metric-conservation proof and **any** metric-counter assertions — deferred; business metrics are in flux pending the unscheduled `Import/72-SPEC.md` reshape
- The `Import/72-SPEC.md` `*_messages_consumed`/`_sent` + `keeper_l2_probe` reshape itself
- `sha256` / any hash logging — **replaced** by deterministic incrementing-value tracking (the value chain detects a mid-chain mutation directly, which an `in`-only hash could not)
- `orchestrator_trip_duration_ms` histogram + `TripStartedUtc` threading — ES-only this phase
- **Any** `src/` change outside `SampleProcessor.ProcessAsync` — no framework/orchestrator/keeper/metrics edits (the seeder payload/`G` change is in-test data)
- An actual live Docker run as a close gate — deferred-automated (sandbox has no Docker)
- Redesigning the existing upstream DAG / executionId / spawn mechanics — already built and proven in Phase 67/68

## Constraints

- Only **one** `src/` file may change (`Processor.Sample/SampleProcessor.cs`), and only **inside** `ProcessAsync`; the seeder, auditor, and hermetic harness are test/script code.
- The logged values are **synthetic deterministic test integers** (the proof signal) — not real payloads; no framework-level payload logging is introduced.
- `entryId`/`messageId` (the per-arrival discriminator at `G`) is **framework-owned and not visible in `ProcessAsync`** → the `entryId`-distinctness proof lives in the **hermetic harness** (full framework visibility), while the **live auditor** relies on the deterministic value + `Step_G` ×2 multiplicity (a same-`entryId` redelivery still fails closed).
- Trip duration is computed from ES `@timestamp` deltas only — no histogram, no `TripStartedUtc`.
- The hermetic proof uses **zero Docker**; the dict-backed L2 must be **stateful** across the full multi-hop DAG (a write is visible to every later read under the same key).
- `G` is **one shared step** with **non-join** semantics — per-arrival execution, order-independent, no aggregation/wait.
- Build MUST be 0-warning in both Debug and Release; verification is machine-verified (never "human verification").

## Acceptance Criteria

- [ ] A single shared `G` step is seeded, reachable from both `F1` and `F2`, with no successor (`completed-terminal`)
- [ ] The hermetic full-DAG harness (real pipeline classes + stateful dict-backed L2 + `CapturingSendProvider`) drives the DAG through `G` and is GREEN
- [ ] At `G`, the two converging arrivals (distinct `entryId`s, same `executionId`) are each processed independently — `G` invoked exactly twice per run, each reading its own `entryId` blob and writing its own `out:` blob; arrival-order-independent; never joins/waits (hermetic)
- [ ] Mode-2 seeds fixed `100`/`200`; every payload `number = 1` so each hop is `+1`; the L2 value at each hop equals `seed + hop-count`; both `G` arrivals carry the identical expected terminal value; no cross-executionId contamination
- [ ] `git diff` of `src/` shows changes **only** in `SampleProcessor.ProcessAsync`; the log carries `received` + `produced` integer values + `StepLabel` per step
- [ ] `PassFailEngine` completeness set is extended to 10 labels incl. `Step_G` with expected ×2 convergence multiplicity (not a duplicate-fail); Pass requires the correct incrementing value chain through `Step_G` ×2 + a persisted terminal `skp:out:` blob; Fail on a wrong value / missing label / `Step_G` count ≠ 2 / absent terminal blob / same-`entryId` redelivery
- [ ] `AnalyzerReport` carries per-exec + per-corr trip duration (ms) from ES `@timestamp` deltas; no `orchestrator_trip_duration_ms` series is emitted
- [ ] No metric-counter assertions appear anywhere in the new harness or auditor (metric conservation deferred)
- [ ] An automated live auditor script exists and is runnable; the live Docker run is deferred-automated (not a phase gate)
- [ ] Hermetic suite GREEN + 0-warning Debug & Release build

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                                       |
|--------------------|-------|------|--------|-----------------------------------------------------------------------------|
| Goal Clarity       | 0.90  | 0.75 | ✓      | Deterministic value tracking + fan-in entryId correctness; one shared G     |
| Boundary Clarity   | 0.86  | 0.70 | ✓      | Metrics deferred, hashing dropped, histogram out, code surface = ProcessAsync |
| Constraint Clarity | 0.82  | 0.65 | ✓      | One src edit, fixed seeds + `+1`, dict L2, entryId-proof split, 0-warning    |
| Acceptance Criteria| 0.78  | 0.70 | ✓      | Value chain, G ×2 convergence, terminal anchor, trip delta, hermetic green   |
| **Ambiguity**      | 0.15  | ≤0.20| ✓      |                                                                             |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                  | Decision locked                                                                 |
|-------|-----------------|---------------------------------------------------|---------------------------------------------------------------------------------|
| 1     | Researcher      | Is `G` a real new step or just a label?           | A real step using the existing `SampleProcessor`, appended after the fan-out    |
| 1     | Researcher      | Is the `*_messages_*` metrics reshape implemented?| No — unscheduled in `Import/`; reshape out of scope                              |
| 1     | Researcher      | Live half: run vs deferred-automated?             | Deferred-automated (no Docker in sandbox); auditor built + automated, not gated |
| 2     | Simplifier      | One shared `G` or two `G1`/`G2`?                   | One shared `G`; goal is to prove fan-in `entryId` handling correctness          |
| 2     | Boundary Keeper | Metric-conservation hermetic assertions?          | Deferred — all metric-counter assertions out of scope (metrics in flux)         |
| 2     | Simplifier      | Trip duration: ES-only or also histogram?         | ES `@timestamp` deltas only; histogram + `TripStartedUtc` out of scope          |

## Amendment — 2026-06-17 (during discuss-phase)

The original SPEC instrumented data travel with a `sha256(in)` log line in the virtual seam. During discuss-phase the owner replaced hashing with **deterministic incrementing-value tracking**: Mode-2 seeds fixed `100`/`200`, every step payload `number = 1` so each hop is `+1`, and `ProcessAsync` logs `received → produced`. This is strictly stronger — the value chain detects a mid-chain mutation directly (which an `in`-only hash could not). `sha256` is now out of scope. The `entryId`-distinctness proof for `G`'s two same-value arrivals moved to the hermetic harness (where the framework layer is visible); the live auditor distinguishes legitimate fan-in via the `Step_G` ×2 expected multiplicity (a same-`entryId` redelivery still fails closed). Requirements 3/4/5 + Boundaries + Constraints + Acceptance Criteria above reflect the amended design.

---

*Phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f*
*Spec created: 2026-06-17 (amended 2026-06-17, discuss-phase)*
*Next step: /gsd-plan-phase 73 — implementation decisions captured in 73-CONTEXT.md*
