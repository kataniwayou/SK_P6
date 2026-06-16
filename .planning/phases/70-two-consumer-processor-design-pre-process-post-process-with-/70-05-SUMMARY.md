---
phase: 70-two-consumer-processor-design-pre-process-post-process-with
plan: 05
subsystem: docs
tags: [documentation, design-spec, superseded-marker, keeper-recovery]

# Dependency graph
requires:
  - phase: 70-SPEC
    provides: "Pinned two-consumer pseudocode designated the sole source of truth (supersedes the slot-array Pre/In/Post + recovery-pass model)"
provides:
  - "docs/design/processor-keeper-recovery-spec.md carries a visible [!WARNING] 'SUPERSEDED by Phase 70' banner pointing readers at 70-SPEC.md"
affects: [70-SPEC, processor-keeper-recovery-spec, two-consumer-design]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Marker (not deletion) supersession: retired design doc keeps its full body for historical context, prepends a top-of-file WARNING banner redirecting to the live source of truth (D-17 discretion)"

key-files:
  created: []
  modified:
    - docs/design/processor-keeper-recovery-spec.md

key-decisions:
  - "D-17 discretion resolved to a header marker (not a rewrite/replacement): the slot-array doc body is retained verbatim for historical context; only a banner is prepended"
  - "Banner placed after the H1 and before the original intro blockquote so it is the first thing a reader sees while the title and body stay intact"

# Metrics
metrics:
  duration: "~2 min"
  completed: 2026-06-16
  tasks: 1
  files: 1
---

# Phase 70 Plan 05: Mark Canonical Recovery Spec Superseded Summary

Added a visible `[!WARNING]` "SUPERSEDED by Phase 70" banner to the head of `docs/design/processor-keeper-recovery-spec.md`, redirecting readers to `70-SPEC.md` (the pinned two-consumer design) as the sole source of truth, while retaining the now-retired slot-array doc body intact for historical context.

## What Was Built

A single doc-only edit (req SPEC-req-12, second half / D-17 discretion = marker, not replacement):

- Inserted a GitHub-flavored `> [!WARNING]` blockquote banner immediately after the `# Processor / Keeper Recovery — Canonical Spec` H1 and before the original intro blockquote.
- The banner names the retired model (single-consumer slot-array Pre/In/Post-Process + recovery-pass; gate on `L2[messageId]` HASH, atomic slot writes, recovery pass), states it is replaced by the two-consumer (Pre-Process + Post-Process) design, and points at the exact path `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-SPEC.md`.
- Explicit "Do not implement against this document — retained for historical context only" instruction.
- The H1 title and the entire doc body are unchanged.

## Key Decisions

- **Marker, not replacement (D-17 discretion):** the slot-array doc is preserved verbatim below the banner rather than rewritten or deleted — historical traceability without an implementation trap.
- **Placement after H1, before intro blockquote:** the warning is the first content a reader encounters, but the canonical title remains the document heading.

## Deviations from Plan

None — plan executed exactly as written. The verbatim banner text and placement from the plan's `<action>` were applied as specified.

## Verification

- Automated grep gate passed: `grep -q "SUPERSEDED by Phase 70"` and `grep -q "70-SPEC.md"` both matched (`echo OK` returned `OK`).
- Banner appears within the first ~12 lines (after line 1 H1, before the original line-3 blockquote).
- Original H1 unchanged; doc body otherwise intact.
- No build/test impact (markdown only).

## Note on Commit Shape

The file `docs/design/processor-keeper-recovery-spec.md` was untracked before this plan (listed as `??` in the starting git status), so the task commit registers as a new file (`create mode 100644`, 184 insertions) rather than a small diff. The banner is the only authored change in this plan; the remaining lines are the pre-existing doc body being committed for the first time. Post-commit deletion check confirmed zero deletions.

## Threat Surface

Documentation-only edit (T-70-13, disposition: accept). No code, no runtime surface, no trust boundary touched. No threat flags raised.

## Self-Check: PASSED

- FOUND: docs/design/processor-keeper-recovery-spec.md (banner present, grep gate OK)
- FOUND: commit f311e33 (docs(70-05): mark canonical recovery spec superseded by Phase 70)
