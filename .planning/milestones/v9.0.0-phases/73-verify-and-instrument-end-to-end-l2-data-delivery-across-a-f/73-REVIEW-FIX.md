---
phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
fixed_at: 2026-06-18T00:00:00Z
review_path: .planning/phases/73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f/73-REVIEW.md
iteration: 1
findings_in_scope: 5
fixed: 5
skipped: 0
status: all_fixed
---

# Phase 73: Code Review Fix Report

**Fixed at:** 2026-06-18T00:00:00Z
**Source review:** .planning/phases/73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f/73-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 5 (WR-01, WR-02, IN-01, IN-02, IN-03 — fix_scope "all")
- Fixed: 5
- Skipped: 0

**Hard constraints respected:**
1. NO `src/` modification. All fixes confined to test/harness/script files (`git diff src/` across the 5 commits is empty). WR-01 used the test-only documentation alternative (option (b)/doc), never option (a) which would have touched `SampleProcessor.cs`.
2. Hermetic suite stays GREEN, build 0-warning (see Verification below).

## Verification

Build (warnings-as-errors), before and after fixes:

```
dotnet build tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug -warnaserror
→ Build succeeded. 0 Warning(s) 0 Error(s)
```

Required hermetic facts, via the native Microsoft.Testing.Platform `--filter-class` passthrough (the MSBuild `--filter` is ignored under MTP):

```
dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --no-build -- --filter-class "BaseApi.Tests.Observability.Analysis.PassFailEngineValueChainFacts"
→ Passed! Failed: 0, Passed: 10, Skipped: 0  (was 8 — +2 new WR-02 facts)

dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --no-build -- --filter-class "BaseApi.Tests.Observability.Analysis.PassFailEngineFacts"
→ Passed! Failed: 0, Passed: 9, Skipped: 0  (legacy completeness-only callers still green — WR-02 gate holds)

dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj -c Debug --no-build -- --filter-class "BaseApi.Tests.Processor.FanInHermeticHarnessFacts"
→ Passed! Failed: 0, Passed: 5, Skipped: 0  (IN-03 self-contained assert added, still green)
```

PowerShell sweep parse-validation (IN-02 — not executed, only parsed):

```
[System.Management.Automation.Language.Parser]::ParseFile("scripts/phase-73-sweep.ps1", ...)
→ PARSE_OK (0 parse errors)
```

## Fixed Issues

### WR-02: Value-chain check skipped runs with an empty value map (vacuous green) — HARDENED

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`, `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs`
**Commit:** ba9c8d2
**Status:** fixed: requires human verification (it is a binding-logic change — re-confirm the gating condition matches the intended live/legacy split)

**Applied fix:** In `PassFailEngine.Analyze`, the value-chain loop now records whether any COMPLETE run carried surfaced values (`completeRunsWithValues`). After the loop, when a value oracle is supplied (`seedsByExecution is not null`) AND `complete.Count > 0` AND `completeRunsWithValues == 0`, `valueChainOk` is set `false` and a `ValueChainDetail` line is appended: `"{N} complete run(s) surfaced no Produced value — value chain unverified (...)"`. The new rule is GATED on `valueOracleSupplied` so the migrated legacy completeness-only callers (`PassFailEngineFacts`, which pass no value map and no `seedsByExecution`) are unaffected and still pass. Two synthetic facts were added to `PassFailEngineValueChainFacts`:
- `Fail_CompleteRun_ZeroSurfacedValues_WithValueOracle_Yields_Fail` — complete run, empty value map, oracle supplied → `ValueChainOk == false`, `Verdict.Fail`, detail contains "no Produced value" + "unverified".
- `Pass_CompleteRun_ZeroSurfacedValues_WithoutValueOracle_StaysGreen` — same shape but NO oracle → stays `Pass` (proves the legacy gate is preserved).

### WR-01: Live value-chain seed is self-derived from Step_B (tautology / no absolute anchor) — DOCUMENTED ACCURATELY

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (value-chain block comment + `ResolveSeed` doc), `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` (`ValueChainOk` doc), `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (seed-recovery comment)
**Commits:** ba9c8d2 (the `PassFailEngine.cs` doc lines, carried with WR-02's shared file), 8b627ca (`AnalyzerReport.cs` + `AnalyzerE2ETests.cs`)
**Status:** fixed

**Applied fix:** Took the review's documentation alternative rather than option (b)'s fixture-level absolute oracle. Option (b) is NOT cleanly feasible: the live `executionId` is a framework-generated GUID with no independent executionId→seed mapping available to the ES-read-only auditor (Mode-2 `Step_A` surfaces no `Produced` attribute, so `Step_A` never enters `Values`), so any live seed must be recovered from the chain itself. The doc comments now state plainly that the LIVE check pins the inter-hop `+1` DELTAS (and the `Step_G` ×2 agreement at the shared terminal `seed+6`), NOT the ABSOLUTE base value — a uniform constant shift of the whole live chain would still pass — and that the ABSOLUTE-value terminal-anchor proof (101..106 / 201..206 against the fixed 100/200 seeds) is owned by the hermetic `FanInHermeticHarnessFacts`, which reads the durable L2 blob. The documentation no longer overclaims a live absolute terminal anchor. The hermetic value-chain facts DO pass an explicit seed oracle, so their absolute anchor is noted as real.

### IN-01: Triplicated `Math.Round` trigger-count helper could drift — HOISTED

**Files modified:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (new shared helper), `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs` + `AnalyzerE2ETests.cs` (delegate), `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` (delegate)
**Commits:** ba9c8d2, 8b627ca, 1e99506 (the delegate edits landed in their owning shared-file commits; `PassFailEngineFacts.cs` is the standalone IN-01 commit)
**Status:** fixed

**Applied fix:** Added `public static int PassFailEngine.TriggerCountFrom(PromCounterSnapshot)` = `(int)Math.Round(prom.DispatchSentDelta)` as the single canonical derivation. The three former call sites — `PassFailEngineFacts.TriggerCountOf`, `PassFailEngineValueChainFacts.TriggerCountOf`, and the inline `var triggerCount = ...` in `AnalyzerE2ETests` — now all delegate to it, so the rounding semantics cannot diverge.

### IN-02: PowerShell report glob may pick a stale per-scenario report — SORTED BY LastWriteTime

**Files modified:** `scripts/phase-73-sweep.ps1`
**Commit:** bded2cf
**Status:** fixed

**Applied fix:** Inserted `Sort-Object LastWriteTime -Descending` before `Select-Object -First 1` in the analyzer-report discovery pipeline so the freshest `analyzer-reports/{id}.json` is taken across a multi-TFM/multi-config bin tree, never an enumeration-order artifact. Script remains parse-valid (verified, not executed).

### IN-03: Per-hop integrity fact records only `arrivals[0]` — MADE SELF-CONTAINED

**Files modified:** `tests/BaseApi.Tests/Processor/FanInHermeticHarnessFacts.cs`
**Commit:** 7d1c66f
**Status:** fixed

**Applied fix:** Added `Assert.Equal(arrivals[0].Value, arrivals[1].Value)` immediately before `values[(seed, "Step_G")] = arrivals[0].Value`, so the per-hop integrity fact independently verifies both convergent `Step_G` arrivals agree rather than trusting index 0 (the invariant remains independently covered by `G_invoked_exactly_twice_on_distinct_entryId_blobs`).

## Commit-grouping note (shared files)

Several findings touch the same files at line-disjoint but file-overlapping positions (e.g. `PassFailEngine.cs` carries WR-02 logic + WR-01 docs + the IN-01 helper). Interactive hunk staging (`git add -p`) is unavailable in this environment, so to avoid any risk of a partial/corrupt patch each file was committed exactly once under its dominant finding, with the secondary findings' shared-file edits explicitly called out in that commit's body and here:

- `ba9c8d2` (WR-02): `PassFailEngine.cs` + `PassFailEngineValueChainFacts.cs` — also carries WR-01 doc narrowing in `PassFailEngine.cs` and the IN-01 helper + one delegate.
- `8b627ca` (WR-01): `AnalyzerReport.cs` + `AnalyzerE2ETests.cs` — also carries one IN-01 delegate in the fixture.
- `1e99506` (IN-01): `PassFailEngineFacts.cs` (standalone).
- `bded2cf` (IN-02): `scripts/phase-73-sweep.ps1` (standalone).
- `7d1c66f` (IN-03): `FanInHermeticHarnessFacts.cs` (standalone).

## Skipped Issues

None — all 5 in-scope findings were fixed.

---

_Fixed: 2026-06-18T00:00:00Z_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
