# Phase 72: Processor Always-Write to L2 + Uniform Orchestrator Pre-Pipeline + Unresolved-Step Metric — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-06-17
**Phase:** 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
**Areas discussed:** Stage-3 unresolved detection, StepProcessing handling, OutputTail write API, Test migration mechanics

---

## Stage-3 unresolved detection

| Option | Description | Selected |
|--------|-------------|----------|
| SelectNext returns `{matches, unresolvedIds}` (stays pure) | Iterate NextStepIds once, classify 3-way; pipeline increments per unresolvedId + fans out matches | ✓ |
| Pipeline re-checks NextStepIds vs L1 itself | Duplicates the lookup; more logic in pipeline | |
| SelectNext becomes impure (touches metrics) | Rejected — violates the harness-free pure-function design (T-24-06) | |

**User's choice:** SelectNext returns a structured result; pure; condition-filtered case is not metered (only L1-miss).
**Notes:** Three-way classification — missing-from-L1 (unresolved) vs condition-mismatch (silent filter, no metric) vs match (fan out). `matches`==0 with no unresolvedIds = legit `completed-terminal` (log only).

---

## StepProcessing handling

| Option | Description | Selected |
|--------|-------------|----------|
| (A) Full uniform — Processing also writes a blob + real EntryId + fans out | Simplest code, zero special-case | |
| (B) Processing stays a no-blob status; orchestrator clean-absent skip prevents it advancing | Only Completed/Failed/Cancelled always-write | ✓ |

**User's choice:** (B).
**Notes:** Always-write covers the three terminal outcomes only. A `Processing` result has no `out:` blob → orchestrator clean-absent idempotent skip prevents fan-out (a still-processing step must not advance). Narrows SPEC req 1's "all four" to the three terminal outcomes by design.

---

## OutputTail write API

| Option | Description | Selected |
|--------|-------------|----------|
| Catch/fail build DataResult{Data=validatedData} → outputTail.RunAsync (no new param) | OutputTail just loses the Completed-only write gate | ✓ |
| OutputTail gains a validatedData fallback param | More surface on OutputTail | |

**User's choice:** Former + keep input-left-to-TTL on failure.
**Notes:** Catch blocks (`:96-117`) + input-validation-fail (`:83`) route through OutputTail (write + send); thrown ExecuteAsync → DataResult{failed, Data=validatedData}. Failed/thrown path does NOT delete the input `data:` blob (C-2 model, TTL reaps it) — input-delete-on-failure deferred.

---

## Test migration mechanics

| Option | Description | Selected |
|--------|-------------|----------|
| Rename/extend in-place + add always-write/uniform-gate/metric assertions | Preserves history | ✓ |
| New test files | Loses continuity | |

**User's choice:** Rename/extend in-place.
**Notes:** OutputTailFacts + processor Pre/Post facts → always-write + real EntryId; Phase-71 orchestrator Pre facts → uniform gate + clean-absent skip + Processing-skips; add `orchestrator_step_unresolved` emission assertions (workflowId; stage-1 & stage-3; not on terminal/condition-skip/normal-fanout). Per-file details left to planner.

## Claude's Discretion

- Exact `SelectNext` return type name/shape, validatedData→DataResult plumbing, `OutputTail.RunAsync` signature, per-file test rename details.

## Deferred Ideas

- Deleting the input `data:` blob on failure (full uniform input lifecycle).
- The imported parallel-project Phase-72 uniform two-counter metrics reshape — separate `/gsd-import`.
