---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
plan: 09
subsystem: infra
tags: [kubernetes, docker-desktop, kubectl, port-forward, powershell, bring-up, rollout-status]

# Dependency graph
requires:
  - phase: 80 (plans 01-07)
    provides: k8s/ manifests (namespace, secret, configmaps, 4 infra StatefulSets, otel/prometheus/baseapi/orchestrator/keeper/processor Deployments) + kustomization + phase-80-build.ps1
provides:
  - "scripts/phase-80-up.ps1 — single-command k8s bring-up: compose-down guard, kubectl apply -k, 10-tier rollout-status Ready gate, 4-app rollout-restart (image currency), 8 loopback port-forwards, baseapi readiness poll"
affects: [80-10, live-smoke, harness-reset-seed-post-sweep]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "kubectl rollout status as the replica-aware Ready gate (replaces phase-65-up.ps1 hand-rolled NDJSON-per-replica parse)"
    - "kubectl rollout restart of :local+IfNotPresent Deployments to force SourceHash currency after a same-tag rebuild"
    - "Start-Process kubectl -PassThru -WindowStyle Hidden background port-forwards, all bound --address 127.0.0.1"

key-files:
  created:
    - scripts/phase-80-up.ps1
  modified:
    - .gitignore

key-decisions:
  - "Provisioned EIGHT port-forwards (D-14 six + rabbitmq AMQP 5673->5672 + otel 4317->4317) because the ~FanOutSeeder in-proc WebApi hard-codes RabbitMq__Port 5673 and OTEL endpoint localhost:4317"
  - "Used per-tier rollout status loops over arrays rather than the compose NDJSON parse — replica-aware and readiness-gated for free"
  - "Set-Location (not Push/Pop) to repo root so exit-on-failure aborts stay clean; fail-loud exit 10 (rollout) / exit 11 (readiness)"

patterns-established:
  - "Pattern: 10-tier phased rollout-status Ready gate (6 infra then 4 app)"
  - "Pattern: rollout restart after build for image/SourceHash currency (Pitfall 2, D-05)"
  - "Pattern: loopback-only (127.0.0.1) port-forward set with PID capture for teardown"

requirements-completed: [GOAL-80-HEALTHY, GOAL-80-ROUNDTRIP]

# Metrics
duration: 3min
completed: 2026-07-18
---

# Phase 80 Plan 09: k8s Bring-Up Script Summary

**`scripts/phase-80-up.ps1` — compose-down guard → `kubectl apply -k k8s/` → 10-tier `rollout status` Ready gate → 4-app `rollout restart` (image currency) → 8 loopback-bound `kubectl port-forward`s → baseapi `/health/ready` poll, all fail-loud.**

## Performance

- **Duration:** 3 min
- **Started:** 2026-07-18T07:02:07Z
- **Completed:** 2026-07-18T07:05:17Z
- **Tasks:** 2
- **Files modified:** 2 (1 created, 1 modified)

## Accomplishments
- Authored the single k8s bring-up command that turns the manifests into a running, harness-reachable stack (parallels `phase-65-up.ps1`, D-15).
- Replaced the hand-rolled NDJSON-per-replica health parse with `kubectl -n skp rollout status` across 6 infra + 4 app tiers — replica-aware (keeper/processor ×2 handled automatically), readiness-gated, per-tier `--timeout` budgets (ES 240s, rabbitmq 180s cold starts).
- Wired the Pitfall-2 `kubectl rollout restart` of the 4 app Deployments so freshly built `:local` bits actually run (same tag + `IfNotPresent` ⇒ no auto-redeploy; SourceHash currency, D-05, threat T-80-19).
- Provisioned all 8 required `127.0.0.1` port-forwards (D-14 six + the seeder's AMQP 5673 + otel 4317), captured PIDs to `.k8s-portforward-pids` for plan 80-10 teardown, and gated the hand-off on a real 200 from `http://localhost:8080/health/ready`.

## Task Commits

Each task was committed atomically:

1. **Task 1: apply + phased rollout-status Ready gate + rollout-restart** - `8fb2af4` (feat)
2. **Task 2: start the 8 port-forwards (127.0.0.1) + baseapi readiness poll** - `b17f9be` (feat)
3. **gitignore runtime PID file** - `a90446c` (chore, deviation Rule 2)

**Plan metadata:** _(final docs commit)_

## Files Created/Modified
- `scripts/phase-80-up.ps1` - k8s bring-up + 10-tier health gate + 4-app image-currency restart + 8 loopback port-forwards + readiness poll (171 lines)
- `.gitignore` - ignore `.k8s-portforward-pids` runtime output

## Decisions Made
- **Eight port-forwards, not six:** the `<interfaces>` planner finding is load-bearing — the `~FanOutSeeder` proof runs an in-proc WebApi (`RealStackWebAppFactory`, `SampleRoundTripE2ETests.cs` L417-458) that hard-codes `RabbitMq__Port 5673` (AMQP) and `OTEL_EXPORTER_OTLP_ENDPOINT http://localhost:4317`. Confirmed both host overrides in-source before adding the `svc/rabbitmq 5673:5672` and `svc/otel-collector 4317:4317` forwards, completing D-14's stated rationale.
- **Array-driven rollout-status loops:** the plan lists 10 tiers with distinct timeouts; expressing them as `$infra`/`$apps` arrays keeps the fail-loud `exit 10` handling DRY while still naming every StatefulSet/Deployment target literally.
- **`Set-Location` over `Push/Pop-Location`:** the script aborts via `exit` (process terminates) so a `finally { Pop-Location }` wrapper added no value and complicated the two-task append — dropped it.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] gitignore the runtime PID file**
- **Found during:** Task 2 (port-forward PID capture)
- **Issue:** The script writes `.k8s-portforward-pids` at runtime; without a gitignore entry it would surface as an untracked file and risk being committed as noise.
- **Fix:** Appended `.k8s-portforward-pids` to `.gitignore` with a rationale comment.
- **Files modified:** `.gitignore`
- **Verification:** Entry present; runtime output now ignored.
- **Committed in:** `a90446c`

---

**Total deviations:** 1 auto-fixed (1 missing-critical hygiene)
**Impact on plan:** Minor hygiene; no scope creep. Core script matches the plan exactly.

## Issues Encountered
None. The script parses under pwsh with 0 errors; all Task 1 and Task 2 acceptance greps pass (apply -k present, `kubectl -n skp rollout status` present, single 4-app `rollout restart`, `docker compose down` guard, 8 forwards incl. `5673:5672` + `4317:4317`, every forward `--address 127.0.0.1`, bounded `localhost:8080/health/ready` poll, PIDs captured).

## Threat Surface
All three plan threat-register mitigations are implemented and verified:
- **T-80-17** (0.0.0.0 LAN exposure) → every `kubectl port-forward` passes `--address 127.0.0.1`.
- **T-80-18** (stale compose holds forward ports) → `docker compose down` Pitfall-5 guard at STEP 1.
- **T-80-19** (stale image after rebuild) → `kubectl rollout restart` of the 4 app Deployments at STEP 5.

No new security surface introduced beyond the plan's threat model.

## User Setup Required
None - no external service configuration required. (Live smoke against Docker Desktop k8s is deferred to plan 80-10 per the plan's verification note.)

## Next Phase Readiness
- `scripts/phase-80-up.ps1` is ready for plan 80-10 to invoke as the bring-up step ahead of reset/seed/POST/sweep.
- The `.k8s-portforward-pids` contract is in place for 80-10's harness teardown.
- Live smoke (all-Ready + baseapi 200) requires a running Docker Desktop k8s cluster — sandbox-deferred to 80-10.

## Self-Check: PASSED

- FOUND: `scripts/phase-80-up.ps1`
- FOUND: `.planning/phases/80-.../80-09-SUMMARY.md`
- FOUND commits: `8fb2af4`, `b17f9be`, `a90446c`

---
*Phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c*
*Completed: 2026-07-18*
