# Phase 73: Verify & Instrument End-to-End L2 Data Delivery — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-17
**Phase:** 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
**Areas discussed:** Convergent-G duplicate semantics, instrumentation mechanism, hermetic harness, trip duration, live wiring

---

## Convergent-`G` duplicate semantics (fan-in correctness)

| Option | Description | Selected |
|--------|-------------|----------|
| `entryId`-keyed duplicate detection | Auditor keys duplicates on `(StepLabel, entryId)`; `G`'s two distinct-entryId arrivals not flagged | partial |
| `Step_G` expected ×2 multiplicity | Special-case the convergent terminal's expected count = inbound edges | ✓ (live) |
| Hermetic owns entryId proof | `entryId` not visible in `ProcessAsync`; prove distinctness in the hermetic harness | ✓ (hermetic) |

**User's choice:** Split proof — hermetic harness owns the `entryId`-distinctness (full framework visibility); the live auditor uses `Step_G` ×2 multiplicity + value (a same-`entryId` redelivery still fails closed). Driven by the constraint that `entryId` is framework-owned and not visible inside `ProcessAsync`.

## Instrumentation mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| `sha256(in)` log in virtual seam | Hash the inbound payload; auditor checks hash presence + terminal anchor | (rejected) |
| Deterministic incrementing values | Fixed seeds `100`/`200`, every step `+1`, log `received→produced` | ✓ |

**User's choice:** Remove `sha256` entirely. Use deterministic values: Mode-2 seeds `100`/`200`, every payload `number = 1` so each hop `+1`, log clarifies the values. Rationale (owner): "this way we can track the L2 data travel" — and it catches mid-chain mutation, which `in`-only hashing could not.

## Hermetic harness

| Option | Description | Selected |
|--------|-------------|----------|
| Extend `DispatchTestKit` mux fakes | Reuse per-scenario NSubstitute `IDatabase` | (rejected — stateless, single-arrival) |
| New stateful dict-backed `IDatabase` | `ConcurrentDictionary` fake + driver running the real pipeline classes | ✓ |

**User's choice:** Confirmed the new dict-backed stateful L2 + real-pipeline driver feeding `CapturingSendProvider` outputs back across the full DAG incl. convergent `G`.

## Trip duration

| Option | Description | Selected |
|--------|-------------|----------|
| ES `@timestamp` deltas only | Per-exec + per-corr span from existing ES hits | ✓ |
| Add `orchestrator_trip_duration_ms` histogram | Thread `TripStartedUtc` through dispatch→result (code change) | (rejected) |

**User's choice:** ES-only; histogram + `TripStartedUtc` out of scope.

## Live wiring

| Option | Description | Selected |
|--------|-------------|----------|
| Extend seeder/auditor in place + new sweep script | Add `Step_G`, extend `PassFailEngine`/`RunTrace`, new `phase-73` script | ✓ |
| Net-new auditor | (not chosen) | |

**User's choice:** Extend in place (preserve history); new `phase-73` sweep script analogous to `phase-68-sweep.ps1`. Live Docker run deferred-automated.

## Claude's Discretion

- Dict-backed `IDatabase` fake surface + driver-loop mechanics.
- `AnalyzerReport` field names for trip duration + value chain; the ES `attributes.*` field name carrying the per-step value.
- `Step_G`-multiplicity representation in the engine; sweep-script stage layout.

## Deferred Ideas

- `sha256` / hash-based continuity logging — dropped for deterministic value tracking.
- Hermetic metric-conservation proof + metric-counter assertions — deferred (metrics in flux pending `Import/72-SPEC.md`).
- `orchestrator_trip_duration_ms` histogram + `TripStartedUtc`.
- Actual live Docker auditor run — deferred-automated.

---

*Note: SPEC.md was amended during this discussion (sha256 → deterministic value tracking) to keep the locked requirements consistent with the chosen mechanism.*
