---
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
plan: 02
subsystem: orchestrator
tags: [metrics, meter-listener, step-advancement, select-next, unresolved-ids, observability]

# Dependency graph
requires:
  - phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi
    provides: "OrchestratorPrePipeline (gate/read out: -> SelectNext -> fan-out -> delete out:) + StepAdvancement.SelectNext + OrchestratorMetrics holder"
provides:
  - "SelectNext returns a pure readonly record struct SelectNextResult(Matches, UnresolvedIds) — dangling next-step ids surface in UnresolvedIds instead of being silently dropped"
  - "OrchestratorMetrics exposes a non-null orchestrator_step_unresolved Counter<long> (snake_case, no _total, IMeterFactory, meter name Orchestrator unchanged)"
  - "MeterCollector test seam (zero-dep BCL MeterListener) capturing a named counter's Total/Count/Tags for hermetic metric assertions"
affects: [72-03, orchestrator-pre-pipeline, unresolved-step-metric]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Three-way single-pass classification in a pure helper: dangling -> UnresolvedIds, match -> Matches, condition-mismatch/Never -> nowhere"
    - "Zero-dependency BCL MeterListener counter-capture seam (per-instance IDisposable, no static state) instead of Microsoft.Extensions.Diagnostics.Testing"

key-files:
  created:
    - tests/BaseApi.Tests/Orchestrator/MeterCollectorSeam.cs
  modified:
    - src/Orchestrator/Dispatch/StepAdvancement.cs
    - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
    - src/Orchestrator/Observability/OrchestratorMetrics.cs
    - tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs
    - tests/BaseApi.Tests/Orchestrator/MultiStepHydrationCascadeTests.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs

key-decisions:
  - "SelectNextResult is a public readonly record struct co-located in StepAdvancement.cs (allocation-light, harness-free); SelectNext stays pure — no Redis, no metrics (D-02)"
  - "Dangling next-step id (TryGetValue miss) now lands in UnresolvedIds (D-01 new signal); condition-mismatch/Never(5) collected NOWHERE (D-03 — a deliberate non-match is not an unresolved miss)"
  - "MeterCollector uses the zero-new-dependency BCL MeterListener (A1 Option B) — no Microsoft.Extensions.Diagnostics.Testing package added (REQ-6 hermetic, no new NuGet)"
  - "StepUnresolved counter built via the existing meterFactory.Create(MeterName) — never a static Meter; meter name + AddMeter registration unchanged (T-72-06, REQ-6)"

patterns-established:
  - "No-silent-loss observability seam built foundation-first: the classification result (UnresolvedIds), the counter to increment, and the capture seam are stable contracts Plan 03 wires together"

requirements-completed: [SPEC-4, SPEC-5, SPEC-6]

# Metrics
duration: 13min
completed: 2026-06-17
---

# Phase 72 Plan 02: Orchestrator Pre-Pipeline Foundation Summary

**The orchestrator-side contracts Plan 03 consumes: `SelectNext` now returns a pure `{Matches, UnresolvedIds}` so dangling next-step ids become observable, `OrchestratorMetrics` gains a non-null `orchestrator_step_unresolved` counter, and a zero-dependency BCL `MeterListener` seam (`MeterCollector`) captures that counter's increments + tags for hermetic assertions.**

## Performance

- **Duration:** ~13 min
- **Started:** 2026-06-17T11:13:42Z
- **Completed:** 2026-06-17T11:27:00Z (approx)
- **Tasks:** 3
- **Files modified:** 6 (1 created, 5 modified)

## Accomplishments
- Created `MeterCollectorSeam.cs` — a per-instance `IDisposable` `MeterCollector` over the in-process `Orchestrator` meter, filtering on `instrument.Meter.Name` + `instrument.Name`, accumulating `long` measurements + tags into a thread-safe list, exposing `Total`/`Count`/`Tags`. No new NuGet package (A1 Option B / REQ-6).
- Reshaped `StepAdvancement.SelectNext` from `IEnumerable<(stepId, step)>` to a co-located `public readonly record struct SelectNextResult(Matches, UnresolvedIds)`. A single pass classifies each next-step id three ways: dangling (TryGetValue miss) -> `UnresolvedIds`; condition match or `Always(4)` -> `Matches`; condition mismatch / `Never(5)` -> nowhere. The `?? Enumerable.Empty<Guid>()` terminal guard is preserved; the helper stays pure (no Redis, no metrics).
- Migrated every `SelectNext` call site to `.Matches`: the production `OrchestratorPrePipeline` (the `matches`/`Count == 0`/fan-out path) plus the 6 `StepAdvancementTests` chains and all `MultiStepHydrationCascadeTests` sites (`.ToList()`/`.Single()`/`foreach`/`.Select`). The dangling fact now asserts the id appears in `.UnresolvedIds`; the terminal fact asserts both `.Matches` and `.UnresolvedIds` are empty.
- Added the `StepUnresolved` `Counter<long>` (`orchestrator_step_unresolved`, snake_case, no `_total`) to `OrchestratorMetrics` via the existing `meterFactory.Create(MeterName)` — no static `Meter`, meter name + `AddMeter` unchanged. `OrchestratorMetricsFacts` asserts it non-null from a real `IMeterFactory`.

## Task Commits

Each task was committed atomically:

1. **Task 1: Zero-dep MeterListener counter-capture seam (MeterCollector)** - `c362835` (test)
2. **Task 2: Reshape SelectNext to {Matches, UnresolvedIds} + migrate all call sites** - `5555427` (feat)
3. **Task 3: Add orchestrator_step_unresolved counter to OrchestratorMetrics** - `7d3e390` (feat)

_TDD note (Task 2, `tdd="true"`): the reshape inverted the existing facts in place — the `IEnumerable`-based `.Select(...)`/`.ToList()` chains broke at compile time the instant the return type changed, establishing the RED state; the migration to `.Matches` + the new `UnresolvedIds` assertions made them GREEN. Production change and test migration are one atomic feat commit (the contract reshape and its callers are inseparable for a compiling build)._

## Files Created/Modified
- `tests/BaseApi.Tests/Orchestrator/MeterCollectorSeam.cs` *(created)* - BCL `MeterListener` `MeterCollector` helper; `Total`/`Count`/`Tags` for hermetic counter assertions.
- `src/Orchestrator/Dispatch/StepAdvancement.cs` - `SelectNext` returns `SelectNextResult`; three-way single-pass classification; `SelectNextResult` record struct co-located.
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` - call site migrated to `.Matches` (the only production caller).
- `src/Orchestrator/Observability/OrchestratorMetrics.cs` - `StepUnresolved` counter (`orchestrator_step_unresolved`) added via existing `IMeterFactory`.
- `tests/BaseApi.Tests/Orchestrator/StepAdvancementTests.cs` - 6 chains migrated to `.Matches`; dangling fact renamed + asserts `.UnresolvedIds`; terminal fact asserts both empty.
- `tests/BaseApi.Tests/Orchestrator/MultiStepHydrationCascadeTests.cs` - all cascade `SelectNext` sites migrated to `.Matches`.
- `tests/BaseApi.Tests/Orchestrator/OrchestratorMetricsFacts.cs` - asserts `metrics.StepUnresolved` non-null.

## Decisions Made
- Co-located `SelectNextResult` as a `public readonly record struct` in `StepAdvancement.cs` (allocation-light, harness-free) — the increment off `UnresolvedIds` is deferred to Plan 03's pipeline, keeping `SelectNext` pure (D-02).
- A condition mismatch / `Never(5)` is collected NOWHERE (not in `UnresolvedIds`) — a deliberate non-match is a business no-op, not an L1-resolution miss, so it must NOT be counted (D-03).
- `MeterCollector` uses the BCL `MeterListener` (A1 Option B) over `Microsoft.Extensions.Diagnostics.Testing` to honor the no-new-package constraint; it is per-instance `IDisposable` with no static state (T-72-06 cross-test isolation).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Migrated additional SelectNext call sites beyond the 6 the plan listed**
- **Found during:** Task 2 (reshaping the return type)
- **Issue:** The plan's `<interfaces>` enumerated only the 6 `StepAdvancementTests` chains and "the pipeline" generically. Changing `SelectNext`'s return type from `IEnumerable<(Guid, StepProjection)>` to `SelectNextResult` breaks EVERY caller. A solution-wide search surfaced two additional caller groups: the production `OrchestratorPrePipeline.cs:68` (`.ToList()` on the result) and `MultiStepHydrationCascadeTests.cs` (~16 sites: `.ToList()`/`.Single()`/`foreach`/`.Select(...)`). These had to be migrated to `.Matches` or the 0-warning Debug + Release build gate could not hold.
- **Fix:** Migrated `OrchestratorPrePipeline` (`var matches = ...SelectNext(...).Matches;` — `IReadOnlyList`, so the downstream `.Count == 0` + `foreach` are unchanged) and every `MultiStepHydrationCascadeTests` site to `.Matches`. No behavior change — `.Matches` is the exact set the old `IEnumerable` yielded.
- **Files modified:** src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs, tests/BaseApi.Tests/Orchestrator/MultiStepHydrationCascadeTests.cs
- **Verification:** Full-solution `dotnet build -c Debug -warnaserror` AND `-c Release -warnaserror` → 0 warnings; StepAdvancementTests 15/15 GREEN.
- **Committed in:** 5555427 (Task 2 commit — inseparable from the contract reshape)

---

**Total deviations:** 1 auto-fixed (Rule 3 - blocking)
**Impact on plan:** Required to keep the build compiling once the return type changed; stays within the plan's stated touchpoints (the `SelectNext` contract + its callers). No new scope — `OrchestratorPrePipeline` was already in the plan's wiring narrative as the Plan-03 consumer, and `MultiStepHydrationCascadeTests` are pre-existing advancement-cascade guards that simply call the same helper. No production behavior change beyond the new `UnresolvedIds` surface.

## Issues Encountered
- The xUnit v3 / Microsoft.Testing.Platform runner ignores VSTest-style `--filter`; the correct invocation is `--filter-class "*StepAdvancementTests"` passed after `--` (per the Plan-01 SUMMARY note). The targeted runs (`StepAdvancementTests` 15, `OrchestratorMetricsFacts` 2, combined 17) all GREEN.
- The `MultiStepHydrationCascadeTests` are infra-flavored (RAM scheduler) and were verified at compile/build level (0-warning) rather than executed in this hermetic run; the migration is a pure mechanical `.Matches` substitution with no behavior change.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Plan 03 can wire the three contracts: inject `OrchestratorMetrics` into `OrchestratorPrePipeline`, increment `StepUnresolved` (tagged `workflowId`) once per id in `SelectNext(...).UnresolvedIds`, and assert it via `using var mc = new MeterCollector("Orchestrator", "orchestrator_step_unresolved"); ... Assert.Equal(N, mc.Total); Assert.Contains(mc.Tags[0], t => t.Key == "workflowId");`.
- The Plan-03 uniform pre-pipeline can now distinguish a resolved-terminal (empty `Matches`, empty `UnresolvedIds`) from a dangling-edge miss (non-empty `UnresolvedIds`) — the foundation for the two-reason trip-end signal.

## Self-Check: PASSED

All 6 files present (1 created, 5 modified); all three task commits (`c362835`, `5555427`, `7d3e390`) exist in git history. Affected hermetic tests GREEN (StepAdvancementTests 15/15 + OrchestratorMetricsFacts 2/2 = 17/17); full solution builds 0-warning Debug + Release; no new package added to BaseApi.Tests.csproj.

---
*Phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi*
*Completed: 2026-06-17*
