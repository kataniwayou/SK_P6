---
phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont
plan: 01
subsystem: infra
tags: [keeper, reinject, fault-injection, negative-control, compose, env-gated-seam, masstransit]

# Dependency graph
requires:
  - phase: 78-rework-the-resilience-sweep-to-verify-purely-from-the-new-fr
    provides: "analyzer WR-01 reinject-veto classification (keeper 'reinject' outcome → recoverable-but-lost binding miss) that this seam manufactures a True Positive against"
  - phase: 75-recovery-verdict-per-execution-drop-tunable-constants
    provides: "PassFailEngine keeperOutcomeByExecution classifier + K_EXECUTIONS execution-based window seam the harness pins to 1"
provides:
  - "Default-off, env-gated suppress-send fault seam in ReinjectConsumer.HandleAsync (KEEPER_DEFEAT_REINJECT)"
  - "One-shot static Interlocked latch defeating exactly the FIRST gated reinject per process"
  - "compose.yaml keeper-service env plumbing (${KEEPER_DEFEAT_REINJECT:-0}) for the seam"
affects: [79-02, 79-03, 79-04, 79-05]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Env-gated product-code fault seam (default-off, harness-only, NEVER-in-production) — second instance of the PROCESSOR_STEP_DELAY_MS idiom, now on the keeper recovery path"
    - "One-shot static Interlocked.CompareExchange latch for per-process single-defeat with short-circuit inertness"

key-files:
  created: []
  modified:
    - src/Keeper/Recovery/ReinjectConsumer.cs
    - compose.yaml

key-decisions:
  - "Suppress ONLY the ep.Send redispatch — CountSent + the 'reinject' LogInformation stay AFTER the gated block so ES telemetry is byte-identical to a real recoverable-but-lost strand (D-01)"
  - "Static Interlocked latch (per-process) over injected singleton — combined with K_EXECUTIONS=1 + single keeper replica at runtime for unambiguous single-defeat (D-02)"
  - "Short-circuit gate (int.TryParse && d>0 && Interlocked...) guarantees the latch is never consumed when the var is unset ⇒ provably inert (D-06)"

patterns-established:
  - "Keeper-tier env-gated fault seam mirroring the processor PROCESSOR_STEP_DELAY_MS precedent"

requirements-completed: []

# Metrics
duration: 17min
completed: 2026-07-17
---

# Phase 79 Plan 01: Keeper suppress-send fault seam Summary

**Default-off, env-gated one-shot suppress-send seam in `ReinjectConsumer.HandleAsync` (`KEEPER_DEFEAT_REINJECT`) that defeats exactly the first reinject redispatch per process while keeping `CountSent` + the `"reinject"` log byte-identical — manufacturing a True-Positive recoverable-but-lost strand for the gate-teeth negative control.**

## Performance

- **Duration:** 17 min
- **Started:** 2026-07-17T09:35:54Z
- **Completed:** 2026-07-17T09:53:02Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Added a static `_defeatedOnce` Interlocked one-shot latch (0=armed, 1=spent) to `ReinjectConsumer`.
- Gated the `ep.Send` redispatch behind `KEEPER_DEFEAT_REINJECT>0` AND the armed latch; the `else` branch logs a TEST-ONLY defeat warning.
- Kept `CountSent` + `logger.LogInformation("REINJECT sent {MessageId} {ReinjectOutcome}", ..., "reinject")` strictly AFTER the gated block — so `attributes.ReinjectOutcome=="reinject"` still reaches the analyzer and the WR-01 veto fires (byte-identical telemetry, D-01).
- Plumbed `KEEPER_DEFEAT_REINJECT: "${KEEPER_DEFEAT_REINJECT:-0}"` into the keeper service `environment:` map, mirroring the `PROCESSOR_STEP_DELAY_MS` idiom; `deploy.replicas` untouched.
- Proven inert: seam short-circuits before the Interlocked call when the var is unset (`docker compose config` resolves it to `"0"`).

## Task Commits

1. **Task 1: Add the env-gated one-shot suppress-send seam to ReinjectConsumer.HandleAsync** - `7ec9169` (feat)
2. **Task 2: Plumb KEEPER_DEFEAT_REINJECT through the keeper service in compose.yaml** - `4a9f692` (feat)

## Files Created/Modified
- `src/Keeper/Recovery/ReinjectConsumer.cs` - Added static one-shot latch field + gated the redispatch `ep.Send` behind `KEEPER_DEFEAT_REINJECT>0` && armed latch; preserved `CountSent` + `"reinject"` log after the gate.
- `compose.yaml` - Added `KEEPER_DEFEAT_REINJECT: "${KEEPER_DEFEAT_REINJECT:-0}"` to the keeper `environment:` map (after OTEL endpoint, before healthcheck).

## Decisions Made
- Followed the LOCKED D-01/D-02/D-06 decisions exactly: suppress only `ep.Send`, static per-process latch, short-circuit inertness. Env-var name `KEEPER_DEFEAT_REINJECT` (Claude's discretion per PATTERNS §1).

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- **Keeper build:** `dotnet build src/Keeper/Keeper.csproj -c Debug` → Build succeeded, 0 Warning(s), 0 Error(s).
- **Test project build:** `dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug` → Build succeeded, 0 Warning(s), 0 Error(s).
- **Inertness (seam logic):** `*ReinjectConsumerFacts` 8/8 GREEN (var unset ⇒ redispatch runs, latch never consumed).
- **Analyzer gate unaffected:** `*PassFailEngineFacts` 29/29 GREEN.
- **Compose plumbing:** `docker compose config` validates; `KEEPER_DEFEAT_REINJECT` resolves to `"0"` (default-off) with the var unset.
- **Acceptance criteria grep:** `KEEPER_DEFEAT_REINJECT`, `private static int _defeatedOnce`, `Interlocked.CompareExchange(ref _defeatedOnce, 1, 0)`, `if (!defeatReinject)` (enclosing the unchanged `ep.Send`), unchanged `CountSent` + `"reinject"` log after the block, and `NEVER set in production` — all present.

## Issues Encountered

The full `BaseApi.Tests.exe --filter-not-trait Category=RealStack` run reports 282/853 failures, but ALL are the documented Docker-less-sandbox infra baseline (`RabbitMQ.Client ... No such host is known`, Postgres/Redis connection refused) — the same ~272 pre-existing failures noted across phases 68/72/75/78. NONE are analyzer/keeper LOGIC facts (the targeted `*ReinjectConsumerFacts` 8/8 and `*PassFailEngineFacts` 29/29 are GREEN). Logged to `deferred-items.md`; full-suite exit 0 is a Docker-up concern for Plan 79-05's live gate, out of scope here.

## User Setup Required

None - no external service configuration required. The seam is default-off; no env var is set outside the FALSIFY-01 harness run (Plan 79-03).

## Next Phase Readiness
- Plan 79-02 (analyzer branch pin) and Plan 79-03 (harness FALSIFY-01 row + `$env:KEEPER_DEFEAT_REINJECT` export) can now build on this seam.
- Live proof of the seam (rebuild → graph-DELETE → seed → 204 → run) is Plan 79-05; this plan intentionally did not run the live stack (code compiles + hermetic logic green).

## Self-Check: PASSED

- Files verified present: `src/Keeper/Recovery/ReinjectConsumer.cs`, `compose.yaml`, `79-01-SUMMARY.md`.
- Commits verified in git log: `7ec9169` (Task 1), `4a9f692` (Task 2).

---
*Phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont*
*Completed: 2026-07-17*
