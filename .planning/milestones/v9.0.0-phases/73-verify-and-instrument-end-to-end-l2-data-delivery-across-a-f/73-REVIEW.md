---
phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
reviewed: 2026-06-18T00:00:00Z
depth: standard
files_reviewed: 12
files_reviewed_list:
  - src/Processor.Sample/SampleProcessor.cs
  - scripts/phase-73-sweep.ps1
  - tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs
  - tests/BaseApi.Tests/Observability/Analysis/PassFailEngineValueChainFacts.cs
  - tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs
  - tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs
  - tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs
  - tests/BaseApi.Tests/Processor/DictBackedL2Fake.cs
  - tests/BaseApi.Tests/Processor/FanInHermeticHarnessFacts.cs
  - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
findings:
  critical: 0
  warning: 2
  info: 3
  total: 5
status: issues_found
---

# Phase 73: Code Review Report

**Reviewed:** 2026-06-18T00:00:00Z
**Depth:** standard
**Files Reviewed:** 12
**Status:** issues_found

## Summary

Reviewed the sole production change (`SampleProcessor.cs`) plus 11 test/harness/script files for the Phase-73 end-to-end L2-delivery verification work. The production change is small, deterministic, and correct: the Mode-2 entry seeds fixed `{100, 200}`, Mode-1 accumulates `incoming + baseNumber`, and the `received → produced` log surfaces only synthetic integers — no real/sensitive payload, no injection surface, no resource leak (`JsonDocument` is `using`-scoped). The PowerShell sweep is a thin run-and-collect driver with hardcoded filters and a path-validated scenario id; no untrusted-input surface.

No critical issues. Two warnings concern **false-green risk in the live auditor's value-chain check**: (1) the live seed is bootstrapped from `Step_B`'s own surfaced value, making `Step_B`'s chain assertion a tautology and leaving the live terminal-anchor claim without an independent absolute anchor; and (2) the value-chain check silently skips any run whose value map is empty, so a live run that surfaces zero `Produced` attributes passes the value-chain gate vacuously. Both are mitigated by the hermetic harness (`FanInHermeticHarnessFacts`), which proves absolute values independently — so the binding correctness proof exists, but the *live ES-read-only auditor* is weaker than its documentation claims. The remaining items are informational.

## Warnings

### WR-01: Live value-chain seed is self-derived from Step_B, so Step_B's assertion is a tautology and the terminal anchor has no independent absolute reference

**File:** `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs:388-395` (seed oracle) and `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:301-317,325-346` (ResolveSeed / CheckValueChain)

**Issue:**
In the live path, `BuildRunTraces` builds `seedsByExecution` as `Produced[Step_B] - 1`:

```csharp
if (values.TryGetValue("Step_B", out var b))
{
    seedsByExec[$"{key.Corr}|{key.Exec}"] = b - 1;
}
```

`CheckValueChain` then asserts `Values[label] == seed + offset` for every label, including `Step_B` (offset 1). For `Step_B` this reduces to `b == (b - 1) + 1 == b` — always true regardless of the actual surfaced value. The seed is therefore *not* an independent oracle; it is bootstrapped from one of the very values being checked. The engine's internal `ResolveSeed` fallback has the same `Step_B - 1` recovery, so this holds whether or not the explicit map is passed.

The check still meaningfully verifies the *relative* `+1` increments between hops (C..G are checked against the Step_B-derived seed). But the documented "live terminal-anchor proxy" claim — that `Step_G` sits at an absolute `seed + 6` (106/206) — is only as strong as the seed, and the seed is derived from the chain itself. `Step_A` would be the true absolute anchor (offset 0, `Step_A == seed`), but in Mode-2 `Step_A` logs only `"seeded the following numbers"` with no `Produced` attribute, so `Step_A` never enters the live `Values` map. Net: a live run where the *entire* chain is uniformly shifted by a constant (e.g. every hop off by +5 from a mis-seeded entry) would still pass the live value-chain gate, because only inter-hop deltas — not the absolute base — are pinned.

This is a false-green narrowing, not a correctness bug in the engine. Severity is held to Warning because the **hermetic** `FanInHermeticHarnessFacts` independently asserts the absolute values (101..106 / 201..206) against the fixed `100/200` seeds, so the absolute proof exists in the suite — just not in the live ES-read-only auditor that the sweep invokes.

**Fix:** Anchor the live seed independently of the chain it validates. Options: (a) surface a `Produced`/`Seed` attribute on the Mode-2 `Step_A` entry log so `Step_A == seed` (offset 0) provides the absolute anchor, then let `ResolveSeed` prefer `Step_A`; or (b) pass the known scenario seeds (100/200) into `seedsByExecution` from a fixture-level oracle rather than recovering them from `Step_B`. Either makes `Step_B`'s assertion non-tautological and gives the terminal-anchor claim a real absolute reference. If keeping the current design intentionally, narrow the doc comments on `ResolveSeed` / `AnalyzerReport.ValueChainOk` to state the live check validates inter-hop deltas (not absolute values) and that the absolute proof is owned by the hermetic harness.

### WR-02: Value-chain check skips runs with an empty value map, so a live run surfacing zero `Produced` attributes passes the binding gate vacuously

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs:159-172`

**Issue:**
```csharp
foreach (var run in runs)
{
    if (run.Values.Count == 0)
    {
        continue; // no surfaced values (legacy caller) → not value-chain-checked
    }
    ...
}
```

`valueChainOk` starts `true` and is only set `false` by an *explicit* mismatch. A started, complete run (all 10 distinct labels present) whose `Produced` attribute never surfaced — e.g. the ES mapping for `attributes.Produced` is absent/odd-shaped, so `TryReadProduced` returns false for every hit (`AnalyzerE2ETests.cs:432-440`) — yields `run.Values.Count == 0`, is skipped, and contributes nothing to `valueChainOk`. The run then passes on completeness + duplicate alone, and `valueChainOk` stays `true`. The binding value-chain assertion is silently a no-op for that run.

This is the classic vacuous-green shape: the live auditor can report `ValueChainOk = true` having checked *nothing*, because the field it depends on (`Produced`) is exactly the field most likely to be unmapped on a fresh stack (the existing `TryReadSum` doc at `AnalyzerE2ETests.cs:411-419` notes the analogous `Sum` field was "unmapped" in the Wave-0 probe). The hermetic value-chain facts always pass a populated map, so they never exercise the empty-map skip on a *complete* run — the gap is live-only.

**Fix:** For a run that is COMPLETE (or STARTED) but surfaced zero values, treat the value-chain as unproven rather than silently OK. Minimal change: track whether any complete run was value-chain-checked, and if `complete.Count > 0` but no complete run carried values, either set `valueChainOk = false` with a `ValueChainDetail` line ("N complete run(s) surfaced no Produced value — value chain unverified") or add a loud precondition assert in the fixture (mirroring the `triggerCount > 0` guard at `AnalyzerE2ETests.cs:185-187`) that at least one complete run surfaced a `Produced` value. The `continue` for genuinely legacy callers (hermetic completeness-only facts) can be preserved by gating the new rule on `seedsByExecution`/value-map being supplied by the caller.

## Info

### IN-01: `Math.Round` banker's-rounding on `triggerCount` is documented but the duplicated rounding helper diverges in nothing — confirm intent

**File:** `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs:65`, `PassFailEngineValueChainFacts.cs:81`, `AnalyzerE2ETests.cs:178`

**Issue:** `TriggerCountOf` / inline `(int)Math.Round(...DispatchSentDelta)` is defined identically in three places with the same `MidpointRounding.ToEven` semantics. The engine already documents (PassFailEngine.cs:186-188) that ToEven is intentionally accepted because the ±1-run tolerance absorbs wobble. No bug — the rounding is harmless for corroboration-only math — but the triplicated helper is mild duplication that could drift if one copy is later "fixed" to `MidpointRounding.AwayFromZero` while others are not.

**Fix:** Optionally hoist a single shared `TriggerCountOf` test helper (or a `PassFailEngine.TriggerCountFrom(PromCounterSnapshot)` static) so the three call sites cannot diverge. Cosmetic.

### IN-02: PowerShell report discovery globs the `bin` tree by filename and may match a stale per-scenario report from a prior build

**File:** `scripts/phase-73-sweep.ps1:115-117`

**Issue:**
```powershell
$report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$id.json" ...
          | Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
```
The auditor writes `analyzer-reports/{scenarioId}.json` under `AppContext.BaseDirectory` (the test bin output). A multi-TFM or multi-config build tree can contain more than one `{id}.json` under `bin/**/analyzer-reports/`; `Select-Object -First 1` takes whichever `Get-ChildItem` enumerates first, which is not guaranteed to be the freshest. On a clean reset this is harmless (only one exists), and the wrapper's PASS/FAIL is anchored on the `dotnet test` exit codes, not on the report contents (`$scenarioPass = seedCode==0 && auditCode==0`), so a stale report only mis-tabulates the *informational* `verdict`/`zeroMissing`/`valueChainOk` columns — never the gate. Noted because the script is DEFERRED-AUTOMATED and operator-run, where stale bin artifacts are plausible.

**Fix:** Sort by `LastWriteTime` descending before `Select-Object -First 1`, or scope the search to the specific TFM/config output dir the audit stage actually ran from. Low priority given the gate does not depend on it.

### IN-03: `FanInHermeticHarnessFacts.RunDag` records `values[(seed, "Step_G")]` from `arrivals[0]` only — relies on both arrivals being equal, which is asserted elsewhere but not here

**File:** `tests/BaseApi.Tests/Processor/FanInHermeticHarnessFacts.cs:192`

**Issue:** `values[(seed, "Step_G")] = arrivals[0].Value;` stores only the first G arrival's value into the per-label map that `PerHop_value_integrity_both_chains` asserts against `TerminalA`/`TerminalB`. The comment ("both arrivals carry the identical terminal value") is correct and the two-arrivals-equal invariant *is* independently asserted in `G_invoked_exactly_twice_on_distinct_entryId_blobs` (`Assert.All(arrivals, a => Assert.Equal(terminal, a.Value))`). So coverage exists, but the per-hop integrity fact alone would not catch a regression where `arrivals[1]` diverged from `arrivals[0]` — it trusts index 0. This is acceptable test factoring (each fact isolates one property), just worth noting that the per-hop fact's `Step_G` assertion is not self-contained.

**Fix:** Optional — assert `arrivals[0].Value == arrivals[1].Value` at the point of recording (or store/assert both) so the per-hop integrity fact is self-contained. Not required given the dedicated fan-in fact covers it.

---

_Reviewed: 2026-06-18T00:00:00Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
