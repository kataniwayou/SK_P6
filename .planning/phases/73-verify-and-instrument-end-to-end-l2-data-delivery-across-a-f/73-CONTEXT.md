# Phase 73: Verify & Instrument End-to-End L2 Data Delivery (Fan-In Terminal G) — Context

**Gathered:** 2026-06-17
**Status:** Ready for planning

<domain>
## Phase Boundary

Verify + instrument end-to-end L2 data delivery across the DAG `A→B→C→{D1→E1→F1, D2→E2→F2}→G` (one shared, per-arrival non-joining terminal `G`) per `(correlationId, executionId)`, via a hermetic zero-Docker harness (fan-in `entryId` correctness + deterministic per-hop value integrity) and a live-stack per-`executionId` ES auditor (value chain + `Step_G` ×2 convergence + terminal `skp:out:` anchor + trip duration). This discussion covers HOW to wire it; the WHAT is locked by SPEC.md.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**7 requirements are locked.** See `73-SPEC.md` (amended 2026-06-17 during this discussion: `sha256` → deterministic value tracking) for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `73-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- A single shared convergent terminal `G` (seed/workflow data), reachable from both `F1` and `F2`, per-arrival non-joining
- A hermetic zero-Docker harness (real pipeline classes + stateful dict-backed `IDatabase` L2 + `CapturingSendProvider` loop) proving fan-in `entryId` correctness + deterministic per-hop value integrity through `G`
- Exactly one `src/` edit: deterministic seed (`100`/`200`) + uniform `+1` + value-clarifying log, all inside `SampleProcessor.ProcessAsync`
- The seeder data change (every payload `number = 1`; add `Step_G`) in `FanOutSeederE2ETests`
- An extended live ES auditor: 10-label set incl. `Step_G` (×2 convergence), deterministic value-chain assertion, terminal `skp:out:` anchor, zero silent-loss
- Per-exec + per-corr trip duration from ES `@timestamp` deltas
- An automated live auditor script; deferred-automated live run; hermetic GREEN + 0-warning

**Out of scope (from SPEC.md):**
- Metric-conservation proof + any metric-counter assertions (deferred — metrics in flux pending the `Import/72-SPEC.md` reshape)
- The `Import/72-SPEC.md` metrics reshape itself
- `sha256` / any hash logging — replaced by deterministic value tracking
- `orchestrator_trip_duration_ms` histogram + `TripStartedUtc` threading — ES-only
- Any `src/` change outside `SampleProcessor.ProcessAsync`
- A live Docker run as a close gate — deferred-automated
- Redesigning the existing upstream DAG / executionId / spawn mechanics

</spec_lock>

<decisions>
## Implementation Decisions

### Deterministic value scheme (replaces sha256)
- **D-01:** Mode-2 (`executionId == Guid.Empty`) seeds two **fixed** values `100` and `200` (was `baseNumber + Random.Shared.Next(0,100)`), spawning one execution per seed. Mode-1 keeps `accumulated = incoming + config.Number`; with every step's payload `number = 1`, each hop increments by exactly `+1`. The only `src/` change is inside `ProcessAsync`.
- **D-02:** The value chain per execution: seed → `B=seed+1`, `C=seed+2`, `D=seed+3`, `E=seed+4`, `F=seed+5`, terminal `G` produces `seed+6`. exec_a seed `100` (→ `G` 106), exec_b seed `200` (→ `G` 206).
- **D-03:** `ProcessAsync` logging — Mode-2 logs the seeded values; Mode-1 logs a single line clarifying `received → produced` carrying `StepLabel` + the in/out integers (`ExecutionId`/`CorrelationId` ride the ambient `ExecutionLogScope`). Extend the existing log line, do NOT add a second log. The integers are synthetic deterministic proof values — no real/sensitive payload is logged, no framework-level payload logging is introduced.
- **D-04:** Seeder data change (in `FanOutSeederE2ETests`): every assignment payload `number = 1` (replaces the `A=1 … F2=9` map), and add `Step_G`. Update the self-verification counts accordingly (see D-05). This is in-test data, not a `src/` change.

### Convergent terminal G
- **D-05:** One shared `G` step (existing `SampleProcessor`, Mode-1); both `F1` and `F2` list `G` as next step; `G` has no successor (`completed-terminal`). Reverse-topological seed order means `G` is created first, then `F1`/`F2` point to it. New self-verify shape: **10 steps, 10 edges** (adds `Step_F1->Step_G`, `Step_F2->Step_G`), `G` the lone zero-outgoing sink (`F1`/`F2` no longer sinks), 10 assignments.
- **D-06:** Because both branches are symmetric (3 hops `D→E→F`), `G` is reached **twice per execution** and **both arrivals carry the identical value** (`seed+6`), distinguished only by their `entryId` (the L2 key). `G` runs once per arrival — never waits for or aggregates the other (the "NOT a join" property).

### Fan-in entryId correctness — proof split (key constraint-driven decision)
- **D-07:** `entryId`/`messageId` is framework-owned and **not visible inside `ProcessAsync`** (and the framework is off-limits). Therefore the **`entryId`-distinctness proof lives in the hermetic harness** (which drives the real classes directly and sees the framework layer), NOT in any log. The **live auditor** distinguishes the legitimate fan-in purely by the **`Step_G` ×2 expected multiplicity + matching value**; a genuine **same-`entryId` redelivery still fails closed**.

### Hermetic harness
- **D-08:** Build a NEW **stateful dict-backed `IDatabase`** fake (a `ConcurrentDictionary`-backed implementation of just the ops the pipeline uses — `StringGetAsync`/`StringSetAsync`/`KeyExistsAsync`/`KeyDeleteAsync`), NOT the existing per-scenario `DispatchTestKit` NSubstitute mux fakes (stateless, single-arrival). A driver runs the **real** `ProcessorPipeline` / `OutputTail` / `OrchestratorPrePipeline` classes + `CapturingSendProvider`, feeding captured outputs back as the next step's input across the full DAG including convergent `G`.
- **D-09:** Hermetic assertions: (a) per-hop value integrity (`B=seed+1 … G→seed+6`) per execution; (b) fan-in at `G` — exactly 2 invocations per run, each reading its own `entryId` blob, writing its own `out:` blob, order-independent, no shared state, no join; (c) no cross-executionId contamination.

### Live auditor extension
- **D-10:** Extend `PassFailEngine.AllLabels` to the 10-label set incl. `Step_G`; teach the engine that the **convergent terminal `Step_G` has expected multiplicity 2** per `(corr, exec)` so `RunTrace`'s `HasAnyDuplicateLabel` fail-closed does NOT trip on the legitimate fan-in — while keeping the duplicate-fail for every non-convergent label (and for a same-`entryId` `Step_G` redelivery, per D-07).
- **D-11:** Add a **value-chain assertion**: each label's logged value equals the expected `seed + hop-count` per `(corr, exec)`; both `Step_G` arrivals at the expected terminal value; path reaches `G`'s `completed-terminal`; the persisted terminal `skp:out:` blob exists and carries the expected value (terminal anchor). This requires surfacing the per-step value into `RunTrace` from the ES log (the Mode-1 structured `received`/`produced` fields → an ES `attributes.*` field the auditor reads, mirroring the existing defensive `Sum` read).

### Trip duration
- **D-12:** `AnalyzerReport` gains per-`(corr, exec)` trip duration (ms) = first-step → `G`-terminal `@timestamp` delta (min→max span of the run's ES hits), plus a per-`correlationId` aggregate. Reuse the ES hits the auditor already pulls (`WindowTimestampFieldPath`) — no new ES query, no histogram, no `src/` change.

### Live wiring
- **D-13:** Extend in place (preserve test history): `FanOutSeederE2ETests` (the `G` + payload data), `AnalyzerE2ETests` / `PassFailEngine` / `RunTrace` / `AnalyzerReport` (the auditor). Add a NEW `phase-73` sweep script (analogous to `scripts/phase-68-sweep.ps1`) that seeds the `G`-extended DAG → drives the live round-trip → invokes the auditor end-to-end. The live Docker run is deferred-automated.

### Claude's Discretion
- The exact dict-backed `IDatabase` fake surface + driver-loop mechanics; the precise `AnalyzerReport` field names for trip duration + value chain; the ES `attributes.*` field name carrying the per-step value; the `Step_G`-multiplicity representation in the engine; and the sweep script's stage layout are left to planning/execution.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements
- `.planning/phases/73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f/73-SPEC.md` — Locked requirements, boundaries, acceptance criteria (amended 2026-06-17). MUST read before planning.

### What this phase verifies (the behavior under test)
- `.planning/phases/72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi/72-SPEC.md` + `72-CONTEXT.md` — always-write tail + uniform branch-free `OrchestratorPrePipeline` this phase exercises end-to-end.
- `.planning/phases/71-orchestrator-two-consumer-design-pre-process-post-process-wi/71-SPEC.md` — orchestrator two-consumer + A1 (`StepCompleted.EntryId = output messageId`) threading the fan-in relies on.
- `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-SPEC.md` — processor two-consumer design (Mode-1/Mode-2, `SpawnToPost`, `DeleteEntry`).

### Code touchpoints (full paths)
- `src/Processor.Sample/SampleProcessor.cs` — the ONLY `src/` edit: Mode-2 fixed seeds `100`/`200`, value-clarifying `received→produced` log (`:39-76`). Framework untouched.
- `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` — add `Step_G` (10 nodes/10 edges/`G` sink), set every payload `number = 1`, update self-verify counts (`:64-216`, `:277-330`).
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — extend `AllLabels` → 10 incl. `Step_G`; convergent-terminal ×2 multiplicity; value-chain assertion (`:50-54`, `:98-119`).
- `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` — surface per-step value; convergent-label-aware duplicate handling (`FromLabels`, `HasAnyDuplicateLabel`).
- `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` — add trip-duration (per-exec + per-corr) + value-chain fields.
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — read the per-step value ES attribute (mirror the defensive `Sum` read `:317-325`); compute trip duration from `@timestamp`; feed the extended engine (`BuildRunTraces` `:275-310`).
- `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` — `CapturingSendProvider` (reused by the hermetic driver); the NEW dict-backed `IDatabase` fake lives alongside or in a new kit.
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` + `OutputTail.cs`, `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` — the real classes the hermetic driver wires (NOT modified).
- `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` — `data:`/`out:` key shapes the dict-backed L2 + terminal anchor use.
- `scripts/phase-68-sweep.ps1` — the live sweep-script analog for the new `phase-73` script.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`CapturingSendProvider`** (`DispatchTestKit.cs:344-390`) — records `IStepResult`/`IKeeperRecoverable`/`DataResult` sends + envelope `MessageId`s; the hermetic driver feeds its captured outputs back as next inputs.
- **`PassFailEngine` / `RunTrace` / `AnalyzerReport`** — pure, IO-free models; both the live fixture (real ES hits) and hermetic facts (synthetic) build `RunTrace` via `FromLabels`, so the new value/multiplicity logic is proven once and shared.
- **`FanOutSeederE2ETests.SeedFanOutAsync`** — the idempotent REST seed routine (reverse-topo, sentinel-name idempotent); `Step_G` extends it directly.
- **`AnalyzerE2ETests`** — the RealStack fixture the Phase 67/68 harness invokes (`Category=RealStack&FullyQualifiedName~Analyzer`); reads ES `attributes.*`, windows Prom deltas, writes `analyzer-reports/{scenarioId}.json`. The defensive `TryReadSum` shows the pattern for reading a per-step value attribute.

### Established Patterns
- ES-binding verdict (67-03): distinct `(correlationId, executionId)` with ≥1 `Step_*` log = a started run; full-label set = complete; any within-run duplicate label = fail-closed; Prom is non-binding corroboration. Phase 73 keeps this and adds value-chain + convergent-`G` multiplicity.
- Pure engine + synthetic facts → a green RealStack run is trustworthy, never vacuous.
- Resilience model unchanged: clean-absent L2 ≠ keeper escalation; a Redis fault → REINJECT/INJECT/DELETE.

### Integration Points
- Processor `out:` write → orchestrator `out:` read/relocate → next step's `data:` input → `EntryStepDispatch`. The dict-backed L2 must model this round-trip statefully so values survive hop-to-hop.
- ES log → `BuildRunTraces` → `PassFailEngine.Analyze` → `AnalyzerReport` JSON + exit code. The new value + trip-duration fields flow through this same pipe.

</code_context>

<specifics>
## Specific Ideas

- The deterministic `+1` chain from fixed seeds (`100`/`200`) is the L2-data-travel proof — it catches a mid-chain mutation directly (a wrong value at any hop), which the dropped `in`-only hash could not.
- Symmetric branches mean `G`'s two same-execution arrivals carry the identical value (`seed+6`); the `entryId` (framework-only) distinguishes them — hence the deliberate proof split (D-07): hermetic owns `entryId`-distinctness, live owns value + multiplicity.
- The convergent terminal is the first place in this project where one `(corr, exec)` legitimately logs the same `StepLabel` twice — the existing fail-closed duplicate rule must learn this single exception (D-10) without weakening it elsewhere.

</specifics>

<deferred>
## Deferred Ideas

- `sha256` / hash-based continuity logging — explicitly dropped in favor of deterministic value tracking (this discussion).
- Hermetic metric-conservation proof + any metric-counter assertions — deferred until the `Import/72-SPEC.md` uniform two-counter reshape lands (names in flux).
- `orchestrator_trip_duration_ms` histogram + `TripStartedUtc` threading — ES-`@timestamp`-only this phase.
- The actual live Docker auditor run — deferred-automated (sandbox has no Docker); the sweep script is operator-runnable.

</deferred>

---

*Phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f*
*Context gathered: 2026-06-17*
