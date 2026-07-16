# Phase 76: Framework-Emitted Per-Hop Execution Logs Keyed by stepId — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-16
**Phase:** 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
**Areas discussed:** Verdict class & exit codes, Framework log shape & level, Orchestrator record shape, Sample Step_* coexistence

---

## Verdict class & exit codes

| Option | Description | Selected |
|--------|-------------|----------|
| Three classes split on evidence sufficiency; exit 2; sweep-fatal; no auto-retry | PASS (complete; TelemetryGap non-binding) / FAIL (loss proven) / INCONCLUSIVE (can't see). Exit 2 keeps 0/1/2 as verdict codes, ≥10 as infra aborts. Non-zero final exit. D-04 no-auto-retry discipline held; roll-up flags instrument-failure + deliberate re-run. | ✓ |
| INCONCLUSIVE non-fatal (exit 0 + warning) | Sweep only goes red on real findings; observability blindness is a warning | |
| Auto-retry-once on INCONCLUSIVE | Cold-ES precedent (TEST-01 warm re-run → 18/18) suggests one retry is defensible | |

**User's choice:** Three classes on evidence sufficiency; exit 2; sweep-fatal; no auto-retry (confirm).
**Notes:** Rejected auto-retry because a silent green would hide a persistent observability defect — the very thing the phase exists to surface. The TEST-01 cold-ES first pass (today mislabeled `VERDICT_FAIL`) is the canonical INCONCLUSIVE case. INCONCLUSIVE stays narrow: total darkness + self-consistent conservation only, never "something imperfect."

---

## Framework log shape & level

| Option | Description | Selected |
|--------|-------------|----------|
| One record per hop at completion; Information; no bespoke knob; emit in ProcessorPipeline; log Step_A as entry marker | Orchestrator dispatch = "should run" already covers mid-hop crash, so no start record. Information survives default export. MEL category filter is the opt-out. ProcessorPipeline sees both modes; OutputTail does not. | ✓ |
| Start + end record pair | Mid-hop crash forensics — but double volume to re-prove what the orchestrator dispatch record already proves | |
| Debug level | Lower default noise — but filtered out by default, violates default-on/survives-export | |
| Custom volume toggle | Explicit knob — but duplicates MEL category filter and adds a second silent-disable path | |
| Emit in OutputTail | Single choke point for fault paths — but Mode-2 (entry/seed) skips the tail entirely | |

**User's choice:** One record per hop at completion; Information; no bespoke knob; ProcessorPipeline; Step_A logged as entry marker (confirm).
**Notes:** `OutputTail` looked like the natural choke point but the pipeline comment "spawn handled everything — skip the framework inline tail" confirms Mode-2 bypasses it. `Step_A` runs with `executionId == Guid.Empty`; logged for no-blind-spot, but analyzer treats empty-executionId records as an entry marker (not in any run's expected set), generalizing today's HopLabels-excludes-Step_A.

---

## Orchestrator record shape

| Option | Description | Selected |
|--------|-------------|----------|
| One record per fan-out EDGE + a terminal-reached record | Per-edge preserves the causal entryId→messageId one-to-one (per-run would flatten it). Terminal-reached gives Step_G the two-ways-to-prove redundancy it otherwise lacks. Step_G fans in ×2 → 2 terminal records per (corr,exec). | ✓ |
| One record per pre-pipeline run (all next steps listed) | Fewer records — but flattens edges and loses the entryId→messageId mapping ANL-03 needs | |
| Per-edge only; analyzer exempts terminal from two-ways requirement | One fewer record type — but leaves the convergence point (what zero-missing measures) with single-record evidence | |

**User's choice:** Per fan-out edge + terminal-reached record (confirm).
**Notes:** Terminal hole was the real decision — `Step_G` has no next steps so no fan-out record, collapsing redundancy exactly where it matters most. Terminal-reached record `(corr, exec, wf, inbound entryId, stepId)` (no outbound id) fixes it and ES-evidences run closure from the orchestrator side. Multiplicity 2 mirrors Phase 73 D-06/D-10. Emit in OrchestratorPrePipeline/RelocateTail.

---

## Sample Step_* coexistence

| Option | Description | Selected |
|--------|-------------|----------|
| Clean break for completeness (framework-only, stepId-keyed); keep StepLabel only for the layered value oracle | Framework records carry everything structural; HopLabels deleted, one stepId-keyed structural path. Value chain stays StepLabel-keyed as a separate correctness axis, degradable to N/A. | ✓ |
| True transitional coexistence (both completeness paths live, cross-checked) | Safety during migration — but both halves ship together on the same rebuilt stack, so nothing to transition from; would reintroduce two-sources-of-truth for `missing` | |

**User's choice:** Clean break for completeness; StepLabel retained only for the value oracle (confirm).
**Notes:** The value path is not a second completeness path — it answers "was the work correct" not "did it run", so it is a separate axis, not a single-source violation. `EsIndexNames`: add `StepIdFieldPath`, keep `StepLabelFieldPath`/`Received`/`Produced`; structural queries drop `StepLabelFieldPath`. Absent oracle → not-applicable, never FAIL (SMP-01).

## Claude's Discretion

- Message-template wording for the three record types.
- `attributes.*` field names for the new orchestrator records (follow `ExecutionLogScope` convention).
- Internal analyzer factoring of structural reader vs value reader (given single-structural-source + degrade-to-N/A hold).
- INCONCLUSIVE roll-up message format (one line or short block), provided it names the instrument-failure cause + deliberate-re-run guidance.

## Deferred Ideas

- Prometheus scraping each service's `/metrics` directly (true metrics-axis independence from the collector) — architectural, out of scope.
- Framework-level correctness/value telemetry — irreducibly domain knowledge, left to concrete-processor enrichment; noted only to mark the boundary.
