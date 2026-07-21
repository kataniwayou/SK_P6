---
phase: 75-recovery-verdict-per-execution-drop-tunable-constants
reviewed: 2026-07-15T00:00:00Z
depth: standard
files_reviewed: 17
files_reviewed_list:
  - compose.yaml
  - scripts/phase-67-harness.ps1
  - src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs
  - src/Keeper/Recovery/ReinjectConsumer.cs
  - src/Keeper/RecoveryOptions.cs
  - src/Orchestrator/Configuration/OrchestratorOutputOptions.cs
  - src/Processor.BadConfig/appsettings.json
  - src/Processor.Sample/appsettings.json
  - tests/BaseApi.Tests/Composition/ComposeYamlFacts.cs
  - tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs
  - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
  - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
  - tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs
  - tests/BaseApi.Tests/Processor/ProcessorOptionsBindingFacts.cs
findings:
  critical: 0
  warning: 2
  info: 3
  total: 5
status: issues_found
---

# Phase 75: Code Review Report

**Reviewed:** 2026-07-15T00:00:00Z
**Depth:** standard
**Files Reviewed:** 17
**Status:** issues_found

## Summary

Phase 75 reworks the resilience-sweep verdict in `PassFailEngine.cs` into a pure function of
per-`(correlationId, executionId)` recoverability: it drops the tunable `MaxInFlightLoss=4`
firing-rate bound, adds a keeper-evidence recoverability classifier (`keeperOutcomeByExecution`
→ `"drop"` tolerated / else binding), widens the keeper `ReinjectConsumer` telemetry with the
four join keys + a `ReinjectOutcome` discriminator, adds the analyzer ES-join in `AnalyzerE2ETests.cs`
with a `"reinject"`-wins tie-break, and raises the five L2 TTL knobs 300→900.

Overall the change is coherent and well-tested. The core classifier logic (`tolerated =
keeperCleanDrop || redisWipeInFlight`) is correct at its documented boundaries, the tolerated
value-chain skip-set is correctly maintained, the keeper log placeholders are correct
(placeholder-form, no interpolation, `"drop"`/`"reinject"` symmetric), and the TTL knobs are
consistent across all five in-code defaults, both appsettings, and the two compose env overrides.
Hermetic facts exercise every new decision branch (tolerated drop, binding-after-recovery,
redis-wipe-in-flight, many-clean-drops-no-bound).

No critical or blocking issues. The two warnings concern a genuine (if narrow) classification gap
where the keeper's `"reinject"` evidence is not consulted on the timestamp-tolerance path, and a
tie-break asymmetry that depends on ES doc-order for correctness. The info items are minor
robustness/consistency notes.

## Warnings

### WR-01: `"reinject"` keeper evidence is ignored on the redis-wipe timestamp-tolerance path

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:170-178`
**Issue:** For an incomplete run, tolerance is `keeperCleanDrop || redisWipeInFlight`. `keeperCleanDrop`
is true only when `keeperOutcome[key] == "drop"`. If the keeper logged `"reinject"` for that key
(the data WAS recoverable and the keeper confirmed a re-send), the run can still be classified
TOLERATED purely via `redisWipeInFlight` (last hop < recovery AND not first-seen after recovery),
because the `"reinject"` outcome is never consulted to *veto* tolerance. Per Plan 01's own contract
("a recovered execution is a binding requirement, never a tolerated loss" — documented in
`AnalyzerE2ETests.cs:353-358`), a `"reinject"`-classified execution that never completed should be a
binding miss, not tolerated. The timestamp heuristic can silently mask that. This is a narrow window
(requires a reinject whose last observed ES hop still predates RECOVERY_UTC), but it contradicts the
stated invariant that a confirmed reinject is binding.
**Fix:** Make an explicit `"reinject"` outcome suppress timestamp-based tolerance, mirroring the
join-map tie-break:
```csharp
var keeperReinject = keeperOutcome.TryGetValue(key, out var oc2)
    && oc2.Equals("reinject", StringComparison.Ordinal);
var redisWipeInFlight = stalledBeforeRecovery && !startedAfterRecovery && !keeperReinject;
var tolerated = keeperCleanDrop || redisWipeInFlight;
```
Add a hermetic fact: incomplete run + `keeperOutcome["k"]="reinject"` + stalled-before-recovery ⇒
`Missing==1`, Fail.

### WR-02: `"reinject"`-wins tie-break relies on ES ascending sort but is documented as order-independent

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:384-388`
**Issue:** The tie-break keeps `"reinject"` over `"drop"`: `if (existing == "reinject") continue;`
then `outcomeByExecution[key] = outcome;`. This is correct for the drop-then-reinject and
reinject-then-drop orderings *only because* a key that is already `"reinject"` is never downgraded
and a `"drop"` arriving after a `"reinject"` is skipped. However, the doc-comment claims the outcome
is chosen regardless of order ("the keeper dropped, then a later reinject succeeded — or vice
versa"), and the search body sorts ascending on `@timestamp` (line 341). The logic is in fact
*correct* for both orderings, but its correctness is not obvious and there is no test proving the
reinject-then-drop ordering (only the join-map's single-outcome cases are exercised indirectly via
the engine facts). A future refactor that changes the "skip if already reinject" guard to a plain
last-write-wins (a natural simplification given the ascending sort) would silently break the
reinject-then-drop case and let a recovered execution be recorded as a `"drop"` → falsely tolerated.
**Fix:** Add a hermetic fact for `BuildKeeperOutcomeMap` (extract it to a testable seam or feed
synthetic hit JSON) asserting both orderings — `[drop, reinject]` and `[reinject, drop]` — resolve to
`"reinject"`. This pins the invariant the engine's binding contract depends on.

## Info

### IN-01: `stalledBeforeRecovery` uses strict `<` — a hop exactly at RECOVERY_UTC is treated as post-recovery

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:172-173`
**Issue:** `lh < rec2` (and symmetrically `fh > rec` for `startedAfterRecovery`) uses strict
inequality. A last hop whose `@timestamp` equals RECOVERY_UTC to the tick is classified as NOT
stalled-before-recovery → not redis-wipe-tolerated → binding miss. Given RECOVERY_UTC is a
wall-clock `DateTimeOffset::UtcNow` captured in the harness and ES timestamps are epoch-ms, an exact
tie is astronomically unlikely, so this is not a real bug — just a boundary-semantics note. The
strict-`<` choice is defensible (a hop AT the recovery instant is arguably post-recovery), but it is
undocumented.
**Fix:** None required; optionally add a one-line comment stating the boundary is exclusive (hop == recovery ⇒ treated as post-recovery / binding), so the intent is explicit.

### IN-02: `KeeperReinject` positional-record ctor arg order is not self-documenting at call sites

**File:** `src/Keeper/Recovery/ReinjectConsumer.cs:49-51,78-80`
**Issue:** The drop and success logs pass `m.CorrelationId, m.ExecutionId, m.EntryId, m.MessageId,
"drop"/"reinject"` positionally, matched to the message-template placeholders
`{CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}`. The order is correct and
`ReinjectConsumerFacts.AssertJoinFields` pins each key→value pair, so a positional transposition
(e.g. swapping ExecutionId and EntryId) WOULD be caught by the fact (it asserts
`("ExecutionId", m.ExecutionId)` etc.). No defect — noting only that the five-positional-arg log is
easy to mis-order and its correctness rests entirely on that one fact. The test coverage is adequate.
**Fix:** None required; coverage is sufficient.

### IN-03: Redis-wipe `InFlightLossDetail` line interpolates `recoveryUtc` (nullable) rather than the unwrapped value

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:192-193`
**Issue:** The detail string uses `{recoveryUtc:o}` (the nullable `DateTimeOffset?` parameter) rather
than the unwrapped `rec2`. This branch is only reached when `stalledBeforeRecovery` is true, which
requires `recoveryUtc is { } rec2`, so `recoveryUtc` is non-null here and the `:o` format renders
correctly — no `null` can print. Purely cosmetic consistency: the code already has `rec2` in scope
one line up and using it would make the non-null guarantee syntactically local.
**Fix:** Optional — interpolate the unwrapped value for locality:
```csharp
$"[{key}] in-flight loss: last hop {lastHop[key]:o} < recovery {rec2:o} (tolerated).");
```

---

_Reviewed: 2026-07-15T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
