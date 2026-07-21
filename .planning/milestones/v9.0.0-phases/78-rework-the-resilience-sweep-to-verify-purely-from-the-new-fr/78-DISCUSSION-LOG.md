# Phase 78: Rework the resilience sweep to verify purely from the new framework logs - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-16
**Phase:** 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
**Areas discussed:** Entry-marker rework, Keeper discrimination, Value-oracle drop scope, Live-gate acceptance

---

## Entry-marker rework

| Option | Description | Selected |
|--------|-------------|----------|
| Delete detector entirely | Remove `IsEntryMarker`/`IsEntryMarkerExecution`/`entryMarkerCorrelations`; the `exists attributes.ExecutionId` query filter already drops the marker after 77 | ✓ |
| Adapt-and-keep | Relax query to include ExecutionId-absent records, flip detector to "attribute absent", keep marker tracking | |

**User's choice:** Delete.
**Notes:** Hard verification gate retained (not re-asked): confirm `Step_A`'s producer evidence (`M_1` → first fan-out ANL-03 edge) is genuinely unused before delete lands; fall back to adapt-and-keep only if load-bearing.

---

## Keeper discrimination

| Option | Description | Selected |
|--------|-------------|----------|
| Query-level `must_not exists ReinjectOutcome` | Exclude keeper records from the structural cohort at the ES source; symmetric with the existing keeper-outcome query | ✓ |
| Parse-level routing | Fetch keeper records then discard during `BuildRunTraces` | |

**User's choice:** Delete (exclude at query level).
**Notes:** Keeper records purely excluded from structural completeness — a reinjected hop contributes no "did-run" stepId; recovery proven only via the existing `BuildKeeperOutcomeMap`. Parse-level guard may remain as belt-and-suspenders.

---

## Value-oracle drop scope

| Option | Description | Selected |
|--------|-------------|----------|
| Full deletion | Remove value-oracle query, `CheckValueChain`, `ExpectedHopOffset`, `seedsByExecution`, WR-02 guard, telemetry-gap path #1, and `PassFailEngineValueChainFacts` | ✓ |
| Leave dormant | Keep code, let it degrade to N/A when StepLabel/Produced absent | |

**User's choice:** Delete (full deletion).
**Notes:** ANL-03 framework-redundancy (path #2) becomes the sole non-binding reconciliation. Absolute-value proof stays with `FanInHermeticHarnessFacts`. Hermetic value-chain facts deleted, not skipped.

---

## Live-gate acceptance

| Option | Description | Selected |
|--------|-------------|----------|
| Baseline verdict reproduction | All 7 scenarios (TEST-01..07) reproduce prior verdicts from `phase-68-summary.json` with the value axis dark; hermetic green first | ✓ |
| Allow legitimate verdict drift | Accept changed verdicts and re-baseline | |

**User's choice:** Gate on baseline reproduction.
**Notes:** Reseed first per rebuild→SourceHash→reseed order. A verdict that legitimately shifts is a STOP-and-report finding, not a silent re-baseline. Watch the stale-report + cold-ES-TEST-01 traps.

## Claude's Discretion

- Exact `must_not` ES JSON shape; whether to keep the parse-level keeper guard.
- Fate of `TryReadSum`/`TryReadProduced`/`TryReadTimestamp` (retain if still used by surviving paths).
- Task decomposition + commit granularity.

## Deferred Ideas

- MessageId-graph reconstruction — fallback only if ANL-03 cannot cover a scenario the value oracle used to.
