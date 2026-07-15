---
phase: 75-recovery-verdict-per-execution-drop-tunable-constants
plan: 04
subsystem: testing
tags: [analyzer, elasticsearch, keeper, recovery, es-join, recoverability-classifier, pass-fail-engine, otel]

# Dependency graph
requires:
  - phase: 75-01
    provides: "PassFailEngine.Analyze(keeperOutcomeByExecution:) classifier param (corr|exec -> drop/reinject) — the seam this join feeds"
  - phase: 75-02
    provides: "Keeper ReinjectConsumer drop/success logs carrying attributes.CorrelationId/ExecutionId/EntryId/MessageId/ReinjectOutcome (drop|reinject) as ES attributes.*"
provides:
  - "AnalyzerE2ETests ES-queries the keeper REINJECT drop/success logs and builds a per-(corr,exec) keeper-outcome map"
  - "The keeper-outcome map is fed into PassFailEngine.Analyze via keeperOutcomeByExecution, closing the D75-4 live join"
  - "EsIndexNames.ReinjectOutcomeFieldPath (attributes.ReinjectOutcome) DIRECT-path const (no .keyword trap)"
  - "reinject-wins tie-break: a recovered execution is never mistaken for a clean drop"
affects: [phase-68-sweep, AnalyzerE2ETests, recovery-verdict]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Second window-bounded static-raw-string ES _search (BuildKeeperOutcomeSearchBody) mirroring BuildStepSearchBody, filtering on exists attributes.ReinjectOutcome over the same window"
    - "Defensive per-(corr,exec) outcome-map builder (BuildKeeperOutcomeMap) mirroring the BuildRunTraces TryGetProperty+ValueKind skip-odd-shaped idiom; ES-read-only (no Redis probe)"
    - "reinject-wins tie-break in a two-valued discriminator map (recovered execution never downgraded to a clean drop)"

key-files:
  created: []
  modified:
    - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
    - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs

key-decisions:
  - "Keeper hits are pulled via a SEPARATE es.SearchAllHits over the SAME [windowStart, snapshot] window and the resulting map is passed INTO BuildRunTraces, keeping BuildRunTraces IO-free (its own 'no new ES query' design) — the one live ES read sits in the test body alongside PollHitsToStableAsync"
  - "No new drain added: the keeper docs ride the same OTLP pipeline as the Step_* docs, so DrainMs=60_000 + poll-to-stable already tolerates the ~60s export skew"
  - "ReinjectOutcomeFieldPath is a DIRECT path (attributes.ReinjectOutcome), never .keyword — same documented ECS-data-stream trap pinned on CorrelationId/ExecutionId/StepLabel consts"
  - "Tie-break prefers reinject over drop: a confirmed reinject proves the data was recoverable, so the classifier must not tolerate that execution as a provably-unrecoverable clean drop"

patterns-established:
  - "ES-only keeper recovery evidence: the recovery outcome reaches the verdict solely through keeper structured logs in ES, never a live Redis probe (fixture ES-read-only invariant preserved)"

requirements-completed: [D75-4]

# Metrics
duration: 25min
completed: 2026-07-15
---

# Phase 75 Plan 04: Keeper reinject-outcome ES-join into the recovery verdict Summary

**Closed the D75-4 live join: the analyzer ES-queries the keeper REINJECT drop/success logs (the Plan-02 `attributes.ReinjectOutcome`/`CorrelationId`/`ExecutionId` fields), builds a per-`(corr,exec)` keeper-outcome map with a `"reinject"`-wins tie-break, and feeds it into the Plan-01 classifier via `keeperOutcomeByExecution` — so a keeper clean-dropped execution is provably-tolerated and a recoverable-but-lost execution is a binding FAIL, from keeper evidence, on the RealStack path.**

## Performance

- **Duration:** 25 min
- **Started:** 2026-07-15T13:30:03Z
- **Completed:** 2026-07-15T13:55:44Z
- **Tasks:** 2 (1 auto executed + committed; 1 checkpoint — automated wiring executed + committed, live E2E verify DEFERRED)
- **Files modified:** 2

## Accomplishments
- Added `EsIndexNames.ReinjectOutcomeFieldPath = "attributes.ReinjectOutcome"` as a DIRECT-path const (with a doc-comment mirroring the `.keyword`-trap rationale of `CorrelationIdFieldPath`/`ExecutionIdFieldPath`) — the Plan-02 keeper drop/reinject discriminator the analyzer partitions on.
- Added `BuildKeeperOutcomeSearchBody(windowStart, snapshot)`: a static raw-string `_search` body (`size 2000`, sort asc) filtering on `exists attributes.ReinjectOutcome` + the same `[windowStart, snapshot]` range as `BuildStepSearchBody` — no injection surface (only validated window timestamps interpolated).
- Added `BuildKeeperOutcomeMap(hits)`: the defensive per-`(corr,exec)` map builder mirroring `BuildRunTraces` (`TryGetProperty` + `ValueKind == String`, skip odd-shaped hits with `continue`, never throw — T-66-09/T-75-07), keyed `$"{corr}|{exec}"` (StringComparer.Ordinal) → the `attributes.ReinjectOutcome` string, with `"reinject"` winning any `drop`/`reinject` tie.
- Extended the `TraceCohort` record with `KeeperOutcomeByExecution` and populated it: the test body reads keeper hits via `es.SearchAllHits(BuildKeeperOutcomeSearchBody(...))` over the SAME window and threads the map through `BuildRunTraces` (kept IO-free).
- Wired `keeperOutcomeByExecution: cohort.KeeperOutcomeByExecution` into the `PassFailEngine().Analyze(...)` call (the checkpoint task's compile-safe additive named arg), closing the D75-4 loop end-to-end on the RealStack path.
- ES-read-only invariant preserved: the join is derived solely from keeper LOGS in ES — no `IDatabase`/`redis`/`StringLength` call (grep-verified; the only such tokens are in disclaiming doc-comments).

## Task Commits

1. **Task 1 + checkpoint wiring: ES field-path const + keeper-outcome search body + defensive map + TraceCohort field + Analyze wiring** - `2aa63e5` (feat)

_Note: the plan's second task is a `checkpoint:human-verify`. Its compile-safe wiring half (the additive `keeperOutcomeByExecution:` named arg) was executed and committed together with Task 1 (they touch the same `Analyze` call site and cohort); the LIVE E2E ES-join surfacing is DEFERRED (Docker unavailable) — see Deviations + Deferred Issues._

## Files Created/Modified
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` - Added `ReinjectOutcomeFieldPath = "attributes.ReinjectOutcome"` DIRECT-path const with the `.keyword`-trap doc-comment.
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` - Added `BuildKeeperOutcomeSearchBody` + `BuildKeeperOutcomeMap`; extended `TraceCohort` with `KeeperOutcomeByExecution`; `BuildRunTraces` now takes the map param and emits it; the test body reads keeper hits over the window and builds the map; `keeperOutcomeByExecution: cohort.KeeperOutcomeByExecution` added to the `Analyze(...)` call.

## Decisions Made
- Pulled keeper hits via a SEPARATE `es.SearchAllHits` in the test body (next to `PollHitsToStableAsync`) and passed the built map INTO `BuildRunTraces`, keeping `BuildRunTraces` IO-free per its own "no new ES query" design — the live ES read stays in the orchestrating test method, not the pure grouping helper.
- No new drain: the keeper docs ride the same OTLP pipeline as `Step_*`, so the existing `DrainMs=60_000` + poll-to-stable already covers the ~60s export skew (plan action item 4 explicitly authorizes this).
- Tie-break prefers `"reinject"`: once a key is `reinject` it is never downgraded to `drop` — a confirmed reinject proves the data was recoverable, so the execution must not be tolerated as a clean drop (the join half of Plan 01's classifier contract).

## Deviations from Plan

None - plan executed exactly as written. The `checkpoint:human-verify` (Task 2) live E2E verification is recorded as DEFERRED (Docker unavailable), per the checkpoint's own "deferred-automated (phases 68/73/74)" clause and the plan's `<how-to-verify>` fallback — not a deviation.

## Deferred Issues

**D75-4 live keeper ES-join verification (checkpoint:human-verify, gate=blocking) — DEFERRED (Docker unavailable).**
- The keeper→analyzer ES-join is only OBSERVABLE on a running Docker RealStack (elasticsearch carrying real keeper REINJECT drop/success logs). Docker is not available in this sandbox — established deferred-automated precedent (phases 68/73/74; 75-03/75-05 live gates).
- The AUTOMATED half is fully done + committed (`2aa63e5`); the exact RealStack close steps (Open-Question-1 level-filter probe → rebuild+up → `phase-68-sweep.ps1` → recoverable-but-lost FAIL vs clean-drop tolerated) are logged in the phase `deferred-items.md` under "DEFERRED: 75-04 live keeper ES-join verify".

## Verification
- `grep keeperOutcomeByExecution:` in `AnalyzerE2ETests.cs`: **present** (the wired named arg at the `Analyze` call site).
- `EsIndexNames.cs` contains `ReinjectOutcomeFieldPath = "attributes.ReinjectOutcome"` (no `.keyword`): **present**.
- `AnalyzerE2ETests.cs` contains `BuildKeeperOutcomeSearchBody` + `BuildKeeperOutcomeMap` + `TraceCohort.KeeperOutcomeByExecution`: **present**.
- No `IDatabase`/`redis`/`StringLength` CALL in the new code (ES-read-only preserved): **grep-verified** — the only such tokens are in disclaiming doc-comments.
- Debug build: **0 warning / 0 error**. Release build: **0 warning / 0 error**.
- Fact classes `*PassFailEngineFacts` + `*PassFailEngineValueChainFacts` + `*ReinjectConsumerFacts`: **34/34 GREEN** (run in isolation).
- Full hermetic suite (`--filter-not-trait Category=RealStack`): 538 passed / 272 failed — the 272 are ALL pre-existing broker/Postgres/Redis/ES-connection failures in the Docker-less sandbox (identical baseline to plans 01/02/03); ZERO analyzer/keeper facts among them (grep-confirmed). Judged per the checkpoint note: success = new facts green + clean build, not the global count.
- `AnalyzerE2ETests` itself is `Category=RealStack` (hermetic-excluded), so this is a build+compile gate for the fixture — the live join is deferred-automated.

## Threat Surface
No new threat surface beyond the plan's `<threat_model>`. T-75-07 (malformed input) mitigated: every attribute read in `BuildKeeperOutcomeMap` is guarded (`TryGetProperty` + `ValueKind == JsonValueKind.String`, odd-shaped hits `continue`, never throw); the `_search` body is a static raw-string with only validated window timestamps interpolated (no injection surface). T-75-08 (info disclosure) accepted: the map carries only `correlationId|executionId` GUID keys + a `"drop"`/`"reinject"` label — no secrets/PII, no new external surface.

## Next Phase Readiness
- The D75-4 join is wired and hermetically/compile-proven; the only open item is the live E2E surfacing (deferred-automated, Docker). With this plan, all 5 plans of phase 75 have SUMMARYs; the phase's automated deliverables (D75-1/2/3/5/8 pure verdict, D75-4 keeper telemetry + join, D75-6 TTL neutralization, D75-7 optional window seam) are complete, with the three live close gates (D75-4/D75-6/D75-7) parked in `deferred-items.md` for a Docker-up run.

## Self-Check: PASSED

- Files verified present: `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`, `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs`, `75-04-SUMMARY.md`.
- Commit verified present: `2aa63e5` (feat).

---
*Phase: 75-recovery-verdict-per-execution-drop-tunable-constants*
*Completed: 2026-07-15*
