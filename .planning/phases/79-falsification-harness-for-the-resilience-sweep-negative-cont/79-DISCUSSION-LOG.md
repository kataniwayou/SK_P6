# Phase 79: Falsification harness for the resilience sweep (negative-control) - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-17
**Phase:** 79-falsification-harness-for-the-resilience-sweep-negative-cont
**Areas discussed:** Seam location & defeat mechanism, Single-execution targeting, Assertion contract, Packaging & inertness

---

## Seam location & defeat mechanism (D-01)

| Option | Description | Selected |
|--------|-------------|----------|
| Keeper suppress-send | Default-off env hook in `ReinjectConsumer.HandleAsync`: log `"reinject"` but suppress the re-dispatch send for the first targeted execution. Faithful byte-identical recoverable-but-lost; touches keeper product source (inert by default). | ✓ |
| Processor-side drop | Gate the seam in the processor to drop the reinjected hop on arrival. Post-recovery loss, muddier shape, may not cleanly hit the WR-01 veto path. | |
| Ops-only, no product source | Forbid product-source changes; build the control purely from harness/ops manipulation. Risks non-determinism and non-byte-identical telemetry. | |

**User's choice:** Keeper suppress-send.
**Notes:** Confirmed touching keeper product source is acceptable — it follows the established `PROCESSOR_STEP_DELAY_MS` env-gated-product-seam precedent (`SampleProcessor.cs:42-52`), default-off ⇒ inert in normal runs. The keeper still logs `"reinject"` so the analyzer's WR-01 veto fires and the started-but-incomplete run is forced to a binding miss.

---

## Single-execution targeting (D-02)

| Option | Description | Selected |
|--------|-------------|----------|
| One-shot latch + `K_EXECUTIONS=1` | `Interlocked` latch defeats the first reinject then goes inert; `K_EXECUTIONS=1` isolates a single execution in the window. Exactly-one by count, unambiguous end-to-end. | ✓ |
| `K_EXECUTIONS=1` alone | Drop every reinject while gated; since only one execution exists, still exactly one lost. | |
| Target by `correlationId\|executionId` env | Pass the target pair via env — but ids are runtime-generated, needs brittle pre-lookup. | |

**User's choice:** One-shot latch + `K_EXECUTIONS=1` (accepted the recommended default via "go").
**Notes:** Mirrors the ROADMAP's "family of SCENARIO_ID/K_EXECUTIONS" phrasing. Latch must be threadsafe across the two keeper reinject consumers/replicas.

---

## Assertion contract (D-03)

| Option | Description | Selected |
|--------|-------------|----------|
| Exact classification, both levels | Assert harness exit == 1 AND analyzer report Missing>=1 with MissingDetail "recoverable-but-lost → binding miss", AND drive via sweep asserting summary row verdict==Fail / capstone non-zero. Proves the RIGHT reason fired. | ✓ |
| Verdict-level only | Assert VERDICT_FAIL (exit 1) + sweep flip; don't assert the specific MissingDetail. A wrong-reason FAIL could pass the meta-test. | |
| Harness-exit only, skip sweep | Assert only the standalone harness exit 1; don't drive phase-68-sweep. Doesn't literally prove "the sweep flips it." | |

**User's choice:** Exact classification, both levels.
**Notes:** Assertion requires exit **1** specifically (not merely non-zero) so an INCONCLUSIVE (2) or infra abort cannot masquerade as a passing negative control.

---

## Packaging & inertness (D-04, D-05, D-06)

Locked as recommended (no branching menu; user confirmed via "go"):
- New out-of-band scenario **`FALSIFY-01`** in the harness `$Scenarios` table (`faultType='inject-recovery-loss'`), kept OUT of the default `phase-68-sweep` id list (TEST-01..07 stays byte-unchanged), matching the TEST-08/09/10 convention.
- Standalone **`scripts/phase-79-falsify.ps1`** drives the scenario and also invokes `phase-68-sweep.ps1 -ScenarioIds FALSIFY-01` for the sweep-level assertion.
- Scope guarded to the **recoverable-but-lost binding axis only**; missing/duplicate axes deferred.
- New env var **defaults off**; 7-scenario capstone provably unaffected (seam present-but-unset ⇒ 7/7 PASS reproduced).

**Sub-choices confirmed:** scenario id `FALSIFY-01` (over `TEST-11`); one-shot latch + `K_EXECUTIONS=1` (over `K_EXECUTIONS=1` alone).

---

## Claude's Discretion

- Exact env-var name (`KEEPER_*`, default-off, `${VAR:-0/false}` compose-plumbed).
- Latch implementation (`static Interlocked` vs injected singleton state), threadsafe across replicas.
- Whether to add a hermetic fact pinning the latch/suppress-send branch.
- Exact `MissingDetail` string-match strictness.
- Task decomposition, commit granularity, `phase-79-falsify.ps1` exit-code table, `FALSIFY-01` row field values.

## Deferred Ideas

- Missing-count negative-control axis (follow-on).
- Duplicate-count negative-control axis (follow-on).
- Multi-execution / count-parameterized falsification (after single-execution axis proven).
