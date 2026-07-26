---
phase: 86-console-webapi-health-liveness-refactor
plan: 07
subsystem: processor-health
tags: [health-checks, liveness, readiness, watchdog, processor, heartbeat, tdd-green, HLTH-03, HLTH-04, HLTH-06, HLTH-07]

# Dependency graph
requires:
  - phase: 86-02
    provides: ProcessorIdentitySchemaReadyHealthCheck RED skeleton + IdentitySchemaReadyHealthCheckTests + retirement of the two watchdog-only unit test files
  - phase: 86-03
    provides: shared ILivenessHeartbeat + LoopLivenessHealthCheck + AddConsoleLivenessWatchdog opt-in seam (processor=10s, timestamp-only)
  - phase: 86-04
    provides: /health/ready Redis-ready + latch fold-in via EmbeddedHealthEndpointService (inherited by the processor, no processor code)
provides:
  - "Processor integrated onto the shared loop-liveness watchdog: ProcessorLivenessHeartbeat Beat()s ILivenessHeartbeat UNCONDITIONALLY at the top of the loop, ABOVE the only-when-Healthy L2 gate (HLTH-03 / T-86-14 fix)"
  - "Processor first-beat startup readiness via IStartupGate.MarkReady() on the first beat (HLTH-06)"
  - "Processor identity+schema readiness on /health/ready (ProcessorIdentitySchemaReadyHealthCheck reading IProcessorContext.IsHealthy) + Redis/latch inherited from BaseConsole (HLTH-04)"
  - "Retirement of the processor-specific LivenessWatchdogHealthCheck; /health/live is now the shared timestamp-only LoopLivenessHealthCheck; IProcessorLivenessState + ProcessorLivenessWriter kept (they back the separate L2 gate — Pitfall 6)"
affects: [86-08, 86-09, processor-liveness-migration]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Top-of-loop unconditional heartbeat beat BEFORE the only-when-Healthy L2 write (the HLTH-03 fix for the original 'never beats under a down bus -> false restart' bug)"
    - "First-beat IStartupGate.MarkReady() (guarded by a local firstBeat bool) mirroring the 86-06 keeper integration"
    - "Gate splits: the IsHealthy gate now guards ONLY the L2 WriteAsync, never the beat"

key-files:
  created: []
  modified:
    - src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs
    - src/BaseProcessor.Core/Liveness/ProcessorIdentitySchemaReadyHealthCheck.cs
    - src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs
    - tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs
    - tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs
    - tests/BaseApi.Tests/Processor/ClashRefreshFacts.cs
    - tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs
    - tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs
  deleted:
    - src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs

key-decisions:
  - "Beat FIRST, unconditionally, above the IsHealthy gate — the core original-bug fix: the retired heartbeat only wrote (and implicitly 'lived') when Healthy, so a not-yet-Healthy replica booting against a down bus never beat -> /health/live would go stale -> false restart. The beat is now gate-free; only the L2 healthy write stays gated."
  - "First-beat MarkReady() replaces nothing new — it flips /health/startup on the loop actually ticking (HLTH-06), mirroring the 86-06 keeper BitHealthLoop pattern."
  - "Kept IProcessorLivenessState + ProcessorLivenessWriter untouched (Pitfall 6): they back the SEPARATE L2 per-instance healthy-write gate, NOT liveness. Only the LivenessWatchdogHealthCheck (the L1-reading /health/live probe) is retired."
  - "AddConsoleLivenessWatchdog(intervalSeconds:10) -> stale at k=3 x 10s = 30s, matching ProcessorLivenessOptions.IntervalSeconds."
  - "The shared LoopLivenessHealthCheck is timestamp-only (86-03 contract) — the retired watchdog's per-schema summary in the /health/live body is intentionally GONE; readiness (not liveness) now carries the identity+schema signal."

requirements-completed: [HLTH-03, HLTH-04, HLTH-06, HLTH-07, HLTH-08]

# Metrics
duration: ~44min
completed: 2026-07-26
---

# Phase 86 Plan 07: Integrate the Shared Watchdog into the Processor + Fix the Beat-Above-Gate Bug Summary

**Rewired the processor's ProcessorLivenessHeartbeat onto the shared BaseConsole.Core ILivenessHeartbeat — the beat now fires UNCONDITIONALLY at the top of the loop (above the only-when-Healthy L2 gate) so a not-yet-Healthy processor booting against a down bus still keeps /health/live healthy (no false restart), the startup gate is marked on the first beat, the identity+schema readiness body is implemented on /health/ready, the retired processor-specific LivenessWatchdogHealthCheck is deleted (the shared timestamp-only LoopLivenessHealthCheck takes over /health/live), and IProcessorLivenessState + ProcessorLivenessWriter are kept (they back the separate L2 gate) — closing HLTH-03/04/06/07 for the processor.**

## Performance

- **Duration:** ~44 min (wall; includes two ~15-min background full-hermetic verification runs)
- **Started:** 2026-07-26T17:18:54Z
- **Completed:** 2026-07-26T18:02:51Z
- **Tasks:** 3 (+1 deviation commit)
- **Files modified:** 8 · **Files deleted:** 1 · **Files created:** 0

## Accomplishments
- **Top-of-loop unconditional beat (HLTH-03 / T-86-14):** `ProcessorLivenessHeartbeat` ctor gains `ILivenessHeartbeat _heartbeat` + `IStartupGate _gate` (resolved via the existing `ActivatorUtilities.CreateInstance` registration). `_heartbeat.Beat()` is now the FIRST statement of the while body, ABOVE the `if (_context.IsHealthy && _context.Id is {} id)` gate; the gate wraps ONLY the L2 `_writer.WriteAsync` block (unchanged, incl. its log-and-continue catch). This is the fix for the original bug — the retired loop only "lived" when Healthy.
- **First-beat startup readiness (HLTH-06):** `_gate.MarkReady()` fires once on the first iteration (local `firstBeat` bool). A not-yet-Healthy replica on a down bus beats every tick → `/health/live` stays healthy.
- **Identity/schema readiness GREEN (HLTH-04):** `ProcessorIdentitySchemaReadyHealthCheck` resolves the singleton `IProcessorContext` from `_outer` at check time and maps `IsHealthy` → Healthy("identity+schema resolved") / false → Unhealthy("identity+schema not yet resolved"); never reads `Id`/definition props (WR-03); static-literal messages (T-86-15). `IdentitySchemaReadyHealthCheckTests` now GREEN.
- **Shared-watchdog registration + retirement (T-86-16):** `BaseProcessorServiceCollectionExtensions` replaces the `"liveness-watchdog"` → `LivenessWatchdogHealthCheck` descriptor with `AddConsoleLivenessWatchdog(intervalSeconds:10)` (shared `ILivenessHeartbeat` + `"live"` `LoopLivenessHealthCheck`) and adds a `"ready"`-tagged `identity-schema-ready` descriptor. `LivenessWatchdogHealthCheck.cs` deleted. `IProcessorLivenessState` + `ProcessorLivenessWriter` kept (L2 gate). `/health/ready` inherits Redis + latch from 86-04 with no processor code.
- **Tests ported + extended:** `LivenessHeartbeatFacts` swaps to the new ctor (real `LivenessHeartbeat`/`StartupGate`) and adds the Phase-86 assertion that a not-Healthy replica STILL beats + first-beat marks ready. Full affected-class run **11/11 green** (IdentitySchemaReadyHealthCheckTests 2, AddBaseProcessorFacts 4, LivenessHeartbeatFacts 2, LivenessResilienceFacts 1, ClashRefreshFacts 2), plus the ported `/health/live` E2E **3/3 green**.

## Task Commits

Each task committed atomically:

1. **Task 1: Beat unconditionally above the gate + first-beat MarkReady** — `c1e0747` (fix)
2. **Task 2: Shared watchdog + identity/schema readiness; delete retired watchdog** — `0d053ea` (feat)
3. **Task 3: Port heartbeat facts to new ctor + assert unconditional beat** — `9ca1f37` (test)
4. **Deviation: Port /health/live E2E to the shared timestamp-only watchdog** — `e29a0c7` (test)

## Files Created/Modified
- `src/BaseProcessor.Core/Liveness/ProcessorLivenessHeartbeat.cs` — modified: shared-heartbeat + startup-gate ctor deps; top-of-loop unconditional `Beat()`; first-beat `MarkReady()`; `IsHealthy` now gates ONLY the L2 write.
- `src/BaseProcessor.Core/Liveness/ProcessorIdentitySchemaReadyHealthCheck.cs` — modified: RED skeleton → GREEN body (OUTER-resolved `IProcessorContext.IsHealthy` mapping, static messages, WR-03).
- `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs` — modified: `AddConsoleLivenessWatchdog(intervalSeconds:10)` + `identity-schema-ready` `"ready"` descriptor replace the retired `liveness-watchdog` descriptor; `IProcessorLivenessState` + `ProcessorLivenessWriter` kept.
- `src/BaseProcessor.Core/Liveness/LivenessWatchdogHealthCheck.cs` — DELETED (retired processor-specific L1-reading /health/live probe).
- `tests/BaseApi.Tests/Processor/LivenessHeartbeatFacts.cs` — modified: new ctor deps (real holder + gate) + Phase-86 not-Healthy-still-beats / first-beat-MarkReady assertions.
- `tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs` — modified: ctor call updated for the two new deps (dead-Redis still beats).
- `tests/BaseApi.Tests/Processor/ClashRefreshFacts.cs` — modified: retired Fact D + its `BuildWatchdogProvider` (exercised the deleted `LivenessWatchdogHealthCheck`); Facts A/B/C/E (kept L1/L2 refresh behavior) unchanged.
- `tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs` — modified: ported the /health/live E2E to the shared timestamp-only watchdog (drive the heartbeat, drop summary-body asserts).
- `tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs` — modified: `SeedLiveness(L1)` → `BeatLiveness()` (drives the shared `ILivenessHeartbeat`).

## Decisions Made
- **Beat above the gate, unconditionally:** the whole point of the plan. Proven by `LivenessHeartbeatFacts.NotHealthy_Writes_No_PerInstance_Key` now additionally asserting `heartbeat.Current != null` and `gate.IsReady` despite `IsHealthy == false` and no L2 write.
- **Kept the L2 state (Pitfall 6):** only the `LivenessWatchdogHealthCheck` (the L1-reading /health/live probe) is retired; `IProcessorLivenessState` + `ProcessorLivenessWriter` back the separate L2 per-instance healthy-write gate and are untouched. `AddBaseProcessorFacts` still asserts both are singletons.
- **interval = 10s:** matches `ProcessorLivenessOptions.IntervalSeconds` so a silently-stalled loop goes stale at 30s (k=3), preserving the retired watchdog's DelaySeconds×2 intent generalized to ×k.
- **Timestamp-only /health/live:** the retired watchdog's per-schema summary body is intentionally gone (86-03 contract); the identity+schema signal now lives on readiness, not liveness.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] LivenessResilienceFacts ctor break**
- **Found during:** Task 3 (test-project build)
- **Issue:** `LivenessResilienceFacts` constructs `ProcessorLivenessHeartbeat` directly with the old 6-arg ctor; the Task-1 ctor growth (two new deps) broke its compile.
- **Fix:** supplied `new LivenessHeartbeat(clock), new StartupGate()` before the instanceId; added `using BaseConsole.Core.Health;`. Behavior unchanged (dead-Redis still-beats, still resilient).
- **Files modified:** tests/BaseApi.Tests/Processor/LivenessResilienceFacts.cs
- **Commit:** `9ca1f37`

**2. [Rule 3 - Blocking] ClashRefreshFacts references the deleted type**
- **Found during:** Task 2 (deleting `LivenessWatchdogHealthCheck` broke the test-project compile)
- **Issue:** `ClashRefreshFacts` Fact D constructs a real `LivenessWatchdogHealthCheck` (via `BuildWatchdogProvider`) to regression-guard the L1-reading /health/live verdict — a type Task 2 deletes.
- **Fix:** removed Fact D + `BuildWatchdogProvider` + the now-unused `Microsoft.Extensions.Diagnostics.HealthChecks` using. The guard it provided ("refresh doesn't re-introduce false-restart on /health/live") is now structurally impossible (the shared watchdog beats unconditionally) and is re-locked by `LivenessHeartbeatFacts`. Facts A/B/C/E (the kept L1/L2 refresh behavior) are unchanged.
- **Files modified:** tests/BaseApi.Tests/Processor/ClashRefreshFacts.cs
- **Commit:** `0d053ea`

**3. [Rule 1 - Bug/Contract] /health/live E2E asserted the retired watchdog contract**
- **Found during:** post-Task-3 full hermetic run
- **Issue:** `ProcessorHealthLiveTests` (Phase-61 E2E) drives `AddBaseProcessor`'s embedded `/health/live` and asserted the OLD contract: it seeded `IProcessorLivenessState` (L1) and asserted a per-schema summary in the body. After Task 2, `/health/live` is the shared timestamp-only `LoopLivenessHealthCheck` reading `ILivenessHeartbeat` — so `Live_Is_Healthy_And_Carries_Summary_When_L1_Fresh` failed (no summary; L1 no longer read).
- **Fix:** ported the fixture (`SeedLiveness(L1)` → `BeatLiveness()` driving the shared heartbeat) and the test class (fresh beat → 200 Healthy; un-beaten → 503; dropped the summary-body asserts). The stale-verdict E2E fact was dropped — the real embedded host uses `TimeProvider.System`, so staleness is deterministically unit-covered by `LoopLivenessHealthCheckTests` (86-03); the 503 wiring path stays E2E-proven by the null case.
- **Files modified:** tests/BaseApi.Tests/Console/ProcessorHealthLiveTests.cs, tests/BaseApi.Tests/Console/ProcessorConsoleTestHostFixture.cs
- **Commit:** `e29a0c7`

### Sequencing note
Task 2's verify (`dotnet build tests/BaseApi.Tests/...` + run) required the test project to compile, but `LivenessHeartbeatFacts` (Task 3's file) and `LivenessResilienceFacts` still called the old ctor. So the product changes (Task 2) were verified against `dotnet build src/BaseProcessor.Core/...` (0-warning), and the full test-project build + the Task-2 test-class runs (`IdentitySchemaReadyHealthCheckTests`, `AddBaseProcessorFacts`) were run together after the Task-3 ctor port. No scope change — the same commands ran, one wave later.

## Known Stubs
None — the heartbeat is fully rewired, the identity/schema readiness body is implemented, the retired file is deleted, and every behavioral test (incl. the new unconditional-beat assertion) is green.

## Threat Flags
None — no new network endpoint, auth path, or trust-boundary surface. `/health/live` reuses the existing `EmbeddedHealthEndpointService` `"live"` predicate via the shared `AddConsoleLivenessWatchdog` descriptor (86-03). All three plan threats are mitigated and test-locked:
- **T-86-14 (DoS / false restart on cold boot):** beat moved above the `IsHealthy` gate; `LivenessHeartbeatFacts` asserts the loop beats + marks ready when not-Healthy.
- **T-86-15 (info disclosure, readiness body):** `ProcessorIdentitySchemaReadyHealthCheck` uses static-literal messages, reads only `IsHealthy`; `IdentitySchemaReadyHealthCheckTests` asserts no secret token.
- **T-86-16 (retired-type residue / wrong deletion):** `LivenessWatchdogHealthCheck.cs` deleted; `grep -rn` over `src/` shows zero remaining *code* references (only historical doc-comment prose); `IProcessorLivenessState` + `ProcessorLivenessWriter` registrations retained (asserted by `AddBaseProcessorFacts`).

## Verification
- **BaseProcessor.Core build:** `dotnet build src/BaseProcessor.Core/BaseProcessor.Core.csproj -c Debug` → **0 warnings, 0 errors** (after Task 1 and Task 2).
- **Affected-class run:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` over `IdentitySchemaReadyHealthCheckTests`, `AddBaseProcessorFacts`, `LivenessHeartbeatFacts`, `LivenessResilienceFacts`, `ClashRefreshFacts` → **11/11 passed**.
- **Ported /health/live E2E:** `ProcessorHealthLiveTests` + `ProcessorHealthLiveNullTests` → **3/3 passed**.
- **No residual watchdog refs:** `grep -rn LivenessWatchdogHealthCheck src/` → only historical doc-comment prose (LoopLivenessHealthCheck / BaseProcessorServiceCollectionExtensions / Keeper/Program.cs); zero code references.
- **Full hermetic suite:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` → **767 total, 744 passed, 23 failed**. All 23 failures are **pre-existing / out-of-scope**: 18 are `ComposeYamlFacts` (Phase-86 Wave-0 docker-compose RED, GREENed by a later compose wave — unrelated to processor liveness) and 5 are external-infra integration tests requiring live Postgres / Elasticsearch (`ErrorMappingFacts`, `LogExportTests` ×2, `LogLevelFilterTests`, `SchemasLogsE2ETests`) — the same buckets 86-02 documented (down from that plan's 38 RED + infra as waves 03–07 GREENed the shared liveness/readiness scaffolds). **Zero failures in any file this plan touched — no new failures introduced.** Logged the infra buckets to `deferred-items.md` scope-boundary tracking (pre-existing).

## Next Phase Readiness
- The processor now mirrors the shared liveness contract established by 86-03 and the keeper integration (86-06). The console fleet (keeper, processor) is on the shared watchdog; orchestrator stays self-only by not calling the seam.
- `/health/ready` Redis + latch inheritance (86-04) is exercised transitively; identity+schema readiness is now wired.
- No blockers.

## Self-Check: PASSED

---
*Phase: 86-console-webapi-health-liveness-refactor*
*Completed: 2026-07-26*
