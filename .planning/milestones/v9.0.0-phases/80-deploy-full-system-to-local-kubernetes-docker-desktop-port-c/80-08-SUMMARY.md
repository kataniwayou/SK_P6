---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 08
subsystem: infra
tags: [kubernetes, kubectl, powershell, redis, postgres, clean-state-reset, docker-desktop]

# Dependency graph
requires:
  - phase: 80 (plans 01, 02, 05, 06)
    provides: skp namespace + secret/configmaps + postgres StatefulSet (app=postgres) + redis StatefulSet (app=redis) + processor-sample Deployment (app=processor-sample)
provides:
  - "scripts/phase-80-reset.ps1 — k8s-targeted clean-state reset (kubectl-exec re-target of phase-65-reset.ps1)"
  - "FLUSHALL + 60s heal-wait + FK-safe static-SQL graph DELETE + processor-set assertion, reaching redis-0/postgres-0 via kubectl exec"
affects: [80-09 (port-forward — HTTP/metrics touch-points), 80-10 (harness/round-trip proof that calls this reset)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "kubectl-exec re-target: docker exec/compose exec/compose ps -> kubectl -n skp exec statefulset/<svc> / kubectl -n skp get pods -l app=<svc> (RESEARCH Pitfall 1)"
    - "Namespace-scoped destructive tooling: every op carries -n skp so it can never touch a shared/prod DB (threat T-80-16)"

key-files:
  created:
    - scripts/phase-80-reset.ps1
  modified: []

key-decisions:
  - "STEP-4 badconfig orphan removal OMITTED — no processor-badconfig pod exists in k8s (D-04 excluded); replaced with a one-line comment"
  - "Pre-flight + processor-set running-checks use kubectl get pods with --field-selector=status.phase=Running -o name (the k8s equivalent of the compose --format json Running filter)"
  - "Re-target comments describe the compose source without the literal 'docker exec'/'docker compose' token sequences, so the script contains ZERO docker-CLI references (acceptance criterion)"

patterns-established:
  - "Pattern 1: infra touch-points re-targeted to kubectl; HTTP/metrics touch-points (seeder, POST /start, analyzer, sweep) left on localhost ports covered by port-forward (plan 80-09)"

requirements-completed: [GOAL-80-ROUNDTRIP]

# Metrics
duration: 3min
completed: 2026-07-18
---

# Phase 80 Plan 08: k8s Clean-State Reset (kubectl-exec Re-target) Summary

**scripts/phase-80-reset.ps1 — a faithful kubectl-exec port of phase-65-reset.ps1: FLUSHALL, 60s heal-wait, FK-safe static-SQL graph DELETE and processor-set assertion, reaching redis-0/postgres-0 via `kubectl -n skp exec` with zero docker-CLI coupling.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-07-18T06:56:58Z
- **Completed:** 2026-07-18T06:59:17Z
- **Tasks:** 1
- **Files modified:** 1 (created)

## Accomplishments
- Ported `phase-65-reset.ps1` step-for-step to `scripts/phase-80-reset.ps1`, swapping ONLY the infra touch-points to kubectl (RESEARCH Pitfall 1 — the compose reset's `docker exec sk-redis` / `docker compose exec postgres` cannot see k8s pods).
- Preserved VERBATIM: the FK-safe 6-table DELETE in migration Down() order inside a single BEGIN/COMMIT, the static-literal SQL (no interpolated input, threat T-80-15), the 60s bounded heal-wait poll with the `^skp:proc:[^:]+$` per-instance-key exclusion regex, and all six fail-loud `exit 2` semantics.
- Confirmed the k8s object names/labels the swaps target actually exist in the wave-1 manifests: StatefulSets `postgres`/`redis`, pod labels `app=postgres`/`app=redis`/`app=processor-sample`.

## Task Commits

Each task was committed atomically:

1. **Task 1: phase-80-reset.ps1 — kubectl-exec re-target of the compose reset** - `ecde212` (feat)

**Plan metadata:** _(final docs commit below)_

## Files Created/Modified
- `scripts/phase-80-reset.ps1` - k8s clean-state reset; STEP 0 pre-flight (postgres pod Running), STEP 1 FLUSHALL via `kubectl -n skp exec statefulset/redis`, STEP 2 60s heal-wait scan, STEP 3 FK-safe psql DELETE via `kubectl -n skp exec statefulset/postgres`, STEP 4 processor-set assertion, STEP 5 exit 0.

## Decisions Made
- **STEP-4 badconfig removal omitted** — no `processor-badconfig` pod exists in k8s (D-04 excluded); the compose orphan-removal block is replaced with a one-line comment noting so, and no `kubectl delete` of any processor-sample pod is issued.
- **Running-pod checks** use `kubectl -n skp get pods -l app=<svc> --field-selector=status.phase=Running -o name` — the k8s analog of the compose `ps --format json` Running-instance count.
- **Re-target comments reworded** to reference the compose source (e.g. "the compose `sk-redis` FLUSHALL exec") without the literal `docker exec`/`docker compose` token sequences, satisfying the "contains NO docker-CLI references anywhere" acceptance criterion while preserving the explanatory intent.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
- Initial draft carried the compose command strings verbatim inside the WHY comments, which tripped the `! grep -qE "docker exec|docker compose"` acceptance gate. Resolved by rewording the five re-target comments to describe the compose source without those two-token sequences — no change to the executable logic. (Handled during Task 1, before commit.)

## Verification
- `pwsh` parse: **PARSE-OK** (no parser diagnostics).
- `kubectl -n skp exec statefulset/(redis|postgres)` occurrences: **7**.
- `docker exec` / `docker compose` occurrences: **0** (`no-docker-cli-ok`).
- Acceptance greps: FLUSHALL line, `--scan --pattern 'skp:proc:*'`, `^skp:proc:[^:]+$` exclusion regex, `AddSeconds(60)` deadline, `kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb` all present.
- DELETE order (single transaction): `step_next_steps -> workflow_assignments -> workflow_entry_steps -> assignments -> workflows -> steps` (migration Down() order; processors + config_schemas preserved).
- `exit 2` fail-loud occurrences: **6**.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- The clean-state reset is ready for the plan 80-10 round-trip harness. It assumes the k8s stack is UP (plan 80-06/07 bring-up) and that `kubectl` context targets Docker Desktop.
- Plan 80-09 supplies the port-forward layer the reset deliberately does NOT re-target (seeder, POST /start, analyzer, sweep stay on localhost ports).

## Self-Check: PASSED

- FOUND: scripts/phase-80-reset.ps1
- FOUND: 80-08-SUMMARY.md
- FOUND: commit ecde212

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
