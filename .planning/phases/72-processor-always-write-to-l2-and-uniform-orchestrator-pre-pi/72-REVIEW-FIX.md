---
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
fixed_at: 2026-06-17T00:00:00Z
review_path: .planning/phases/72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi/72-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 72: Code Review Fix Report

**Fixed at:** 2026-06-17T00:00:00Z
**Source review:** .planning/phases/72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi/72-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5 (1 Warning + 4 Info; fix_scope=all)
- Fixed: 5
- Skipped: 0

**Verification:** Both affected projects (`Orchestrator`, `BaseProcessor.Core`) build clean — 0 warnings, 0 errors — after all edits (Tier 2 syntax/type verification via `dotnet build`).

## Fixed Issues

### WR-01: An empty-string terminal `out:` blob is silently reclassified as clean-absent

**Files modified:** `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs`
**Commit:** 0833a5c
**Applied fix:** Adopted option (b) from the review — made the absence signal unambiguous. The relocation read lambda now returns `null` ONLY when the Redis key is truly absent (`raw.HasValue ? raw.ToString() : null`) instead of collapsing absent and present-empty together (`raw.IsNullOrEmpty ? null : ...`). The clean-absent gate is now `read.Value is null` instead of `string.IsNullOrEmpty(read.Value)`, so a present-but-empty terminal payload (e.g. `NewResult(StepOutcome.Completed, "")`) relocates and fans out normally rather than being dropped via the idempotent ack-skip. A thrown `RedisException` still propagates out of the lambda, preserving the read-fault arm. The processor side (`OutputTail`) already writes `dr.Data` verbatim for terminal outcomes and does NOT unify empty with absent, so no processor change was required for empty to be a valid payload.

**Note (requires human verification):** This is a behavioral/logic change to the three-way read discrimination. It passed Tier 1 (re-read) and Tier 2 (clean build), but Tier 2 verifies only syntax/types, not semantics. The developer should confirm the new contract (present-empty relocates; only a truly-absent key skips) against the existing facts — in particular `Clean_absent_out_blob_acks_without_fanout_keeper_or_delete` and `Processing_rides_clean_absent_skip_no_fanout`, which rely on a Processing result leaving NO key at all (true absence), and should still pass. Any fact that previously asserted an empty-string blob is treated as clean-absent would now need to be updated to the new relocate behavior.

### IN-01: Dangling-edge counting deliberately suppressed for clean-absent/Processing messages

**Files modified:** `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs`
**Commit:** 0833a5c (co-committed with WR-01 — same file)
**Applied fix:** No behavioral change (none required). Strengthened the stage-3 loop comment to make the deferral an explicit, deliberate contract: dangling-edge counting (`orchestrator_step_unresolved`) is intentionally suppressed for clean-absent / Processing messages because the loop sits AFTER the clean-absent gate, and the comment now warns not to "fix" it later by moving the loop above the gate (which would break `Processing_rides_clean_absent_skip`).

### IN-02: Redundant trailing `continue` in the stage-3 loop

**Files modified:** `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs`
**Commit:** 0833a5c (co-committed with WR-01 — same file)
**Applied fix:** Removed the no-op trailing `continue;` (it was the last statement of the `foreach` body). Replaced it with a comment preserving the documented intent (graceful business skip — never throw, D-02 / T-72-08) so the rationale survives without the mechanical no-op.

### IN-03: `ResultOutcome` fallback maps an unknown `IStepResult` to `"failed"`

**Files modified:** `src/BaseProcessor.Core/Processing/OutputTail.cs`
**Commit:** b194ed5
**Applied fix:** Changed the unreachable default arm of `ResultOutcome` from `_ => "failed"` to `_ => "unknown"` so any future `IStepResult` subtype is visible in the `ResultSent` telemetry under its own tag rather than masquerading as a failure. Added a comment explaining the set is closed today but a new type would otherwise be mis-labeled with no compiler warning.

### IN-04: `SendKeeper` divergence between OutputTail and ProcessorPipeline

**Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`
**Commit:** 37cac75
**Applied fix:** Aligned `ProcessorPipeline.SendKeeper` with the OutputTail / OrchestratorPrePipeline form by resolving `GetSendEndpoint` INSIDE the `RetryLoop` (so a transient endpoint-resolution fault is retried alongside the send) instead of once outside it. This removes the lone outlier among the three near-identical helpers. An exhaust still throws to trigger broker redelivery, preserving existing escalation semantics.

## Notes on commit grouping

WR-01, IN-01, and IN-02 all modify the same file (`OrchestratorPrePipeline.cs`). The `gsd-sdk query commit` path commits whole files, so the three landed together in a single atomic commit (`0833a5c`) under the WR-01 message. All three were verified together by the clean `Orchestrator` build. No partial or uncommitted changes remain.

---

_Fixed: 2026-06-17T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
