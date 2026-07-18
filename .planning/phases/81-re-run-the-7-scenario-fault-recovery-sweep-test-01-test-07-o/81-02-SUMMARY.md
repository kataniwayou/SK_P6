---
phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o
plan: 02
subsystem: infra
tags: [powershell, kubectl, rabbitmq, rabbitmqctl, reset, fault-recovery-sweep]

# Dependency graph
requires:
  - phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
    provides: "phase-80-reset.ps1 (k8s clean-state reset) + rabbitmq StatefulSet with a PVC (D-11)"
provides:
  - "STEP 3b rabbitmq queue drain in phase-80-reset.ps1 — enumerate-then-purge every durable queue, fail-soft per queue"
  - "explicit cross-scenario message isolation for the shared-stack k8s sweep (no longer relies on compose force-recreate side-effects)"
affects: [phase-81-sweep, phase-80-harness, shared-stack-fault-recovery-sweep]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "enumerate-then-purge idempotent drain (rabbitmqctl list_queues -q name → per-queue purge_queue, fail-soft)"

key-files:
  created: []
  modified:
    - "scripts/phase-80-reset.ps1 - new STEP 3b rabbitmq drain + .DESCRIPTION step-list entry"

key-decisions:
  - "Per-queue purge is fail-soft (no fail-loud guard) per D-04 — a purge miss is re-run tolerant and never aborts the keyspace/graph wipe invariant"
  - "Enumerate queues dynamically via rabbitmqctl list_queues rather than hardcoding (processor dispatch queues are dynamic {ProcessorId:D}[-post])"
  - "rabbitmqctl kubectl-exec form chosen over the mgmt-API alternative for consistency with the reset's existing exec pattern and to avoid depending on a port-forward"

patterns-established:
  - "Idempotent fail-soft drain: bounded finite foreach over enumerated queues, each a single one-shot exec (2>$null | Out-Null), no polling loop (T-81-05/07 mitigations)"

requirements-completed: [SPEC-5]

# Metrics
duration: 4min
completed: 2026-07-18
---

# Phase 81 Plan 02: rabbitmq Drain in phase-80-reset Summary

**Bounded, idempotent, fail-soft rabbitmq queue drain (STEP 3b) added to phase-80-reset.ps1 — enumerates durable queues via `rabbitmqctl list_queues` and purges each fail-soft so stale messages do not bleed across scenarios in the shared-stack k8s sweep.**

## Performance

- **Duration:** ~4 min
- **Started:** 2026-07-18T09:51:03Z
- **Completed:** 2026-07-18T09:55:00Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments
- New STEP 3b inserted immediately after the STEP 3 FK-safe graph DELETE and before STEP 4, mirroring the existing kubectl-exec pattern.
- Dynamic queue enumeration (`rabbitmqctl list_queues -q name`) — no hardcoded queue names (processor dispatch queues `{ProcessorId:D}` / `{ProcessorId:D}-post` are dynamic).
- Per-queue `purge_queue` is fail-soft: a purge of an empty/absent queue is a no-op success and never aborts the reset (D-04 idempotency contract).
- All pre-existing reset invariants preserved byte-for-byte: redis FLUSHALL, 60s heal-wait, FK-safe DELETE order, and all five `exit 2` fail-loud guards.
- Header `.DESCRIPTION` step list updated with a STEP 3b entry between STEP 3 and STEP 4.

## Task Commits

Each task was committed atomically:

1. **Task 1: Add bounded idempotent rabbitmq drain (STEP 3b)** - `cc6f4ef` (feat)

## Files Created/Modified
- `scripts/phase-80-reset.ps1` - added STEP 3b rabbitmq drain (enumerate + fail-soft purge) after the graph DELETE; documented it in the `.DESCRIPTION` step list.

## Decisions Made
- Reworded the STEP 3b fail-soft comment to say "does NOT abort (no fail-loud guard)" instead of the literal `exit 2` — the plan's `<action>` block comment contained the literal string `exit 2`, which would have bumped `grep -c 'exit 2'` from 6 to 7 and violated the plan's own acceptance criterion ("`exit 2` count unchanged from pre-edit"). The reworded comment preserves the exact meaning while keeping the grep count at 6. (See Deviations.)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Reworded STEP 3b comment to preserve the `exit 2` grep-count invariant**
- **Found during:** Task 1 (Add bounded idempotent rabbitmq drain)
- **Issue:** The plan `<action>` block (lines 82-86) carried a comment containing the literal string `` `exit 2` ``. Pasting it verbatim raised `grep -c 'exit 2' scripts/phase-80-reset.ps1` from the pre-edit 6 to 7, directly violating the plan's own acceptance criterion #4 ("grep -c 'exit 2' is unchanged from the pre-edit count") and verification bullet ("`exit 2` count is unchanged from pre-edit").
- **Fix:** Reworded the fail-soft comment from "does NOT `exit 2` on a per-queue purge miss" to "does NOT abort (no fail-loud guard) on a per-queue purge miss". Same meaning; the drain still adds zero fail-loud guards.
- **Files modified:** scripts/phase-80-reset.ps1
- **Verification:** `grep -c 'exit 2'` = 6 (unchanged); acceptance criteria all pass; PowerShell `Parser::ParseFile` returns PARSE_OK.
- **Committed in:** cc6f4ef (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug — acceptance-criterion compliance)
**Impact on plan:** The reworded comment is semantically identical to the plan's intent (fail-soft, no fail-loud guard) and is required to satisfy the plan's own explicit `exit 2` grep-count acceptance criterion. No scope creep; the actual drain behavior matches the plan exactly.

## Issues Encountered
None.

## Verification Results
- `grep -q "STEP 3b: rabbitmq drain"` → matches (VERIFY_OK for all three: list_queues, purge_queue).
- STEP 3b positioned after STEP 3 (graph DELETE at line 117) and before STEP 4 (line 146).
- `rabbitmqctl list_queues` enumerate call present (dynamic discovery, not hardcoded).
- Per-queue `purge_queue` is NOT followed by any `if ($LASTEXITCODE -ne 0) { ... exit 2 }` guard (fail-soft).
- `grep -c 'exit 2'` = 6, unchanged from pre-edit.
- STEP 3 `DELETE FROM step_next_steps; ...` transaction block byte-unchanged.
- PowerShell AST `Parser::ParseFile` → PARSE_OK.

## Threat Model Compliance
- T-81-05 (DoS, hanging exec): mitigated — bounded finite foreach over enumerated queue count, each a single one-shot exec, no polling loop.
- T-81-06 (Tampering, queue name): accepted — names originate from `rabbitmqctl list_queues` on the same dev pod, namespace-scoped (`kubectl -n skp`), no untrusted interpolation.
- T-81-07 (DoS, purge miss aborting reset): mitigated — per-queue purge is fail-soft, no fail-loud guard.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The reset now drains rabbitmq durable queues, making cross-scenario isolation explicit for the shared-stack sweep (plans 81-01 harness + 81-03/04 sweep driver).
- No blockers. The live drain behavior (actual purge counts) is exercised when the k8s stack is up during the sweep run.

## Self-Check: PASSED

- FOUND: scripts/phase-80-reset.ps1
- FOUND: .planning/phases/81-.../81-02-SUMMARY.md
- FOUND: commit cc6f4ef

---
*Phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o*
*Completed: 2026-07-18*
