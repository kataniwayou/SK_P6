# Run-plan — resilience matrix with L2 TTL *below* the fault break

**Status:** staged, NOT yet run. Run in a fresh session (~1h). Author handoff 2026-07-17.

## Objective

Stress the recovery model's load-bearing assumption — *"recovery assumes a transient L2 outage; the
recovery backup lives in L2 itself."* Today `Processor__ExecutionDataTtl = 900s` ≫ the fault dwell (~45s),
so the L2 backup always outlives the outage and recovery always finds its data. Set the TTL **below the
break** so the backup **expires while the tier is down**, and observe how the gate classifies the resulting
genuine losses.

## Staged change (already committed, default-preserving)

`compose.yaml` — both `Processor__ExecutionDataTtl` entries are now `"${EXECUTION_DATA_TTL:-900}"`.
Unset ⇒ 900 (baseline unchanged; all existing tests behave identically). A run overrides it per-invocation.

**Effective TTL is jittered `random[ttl, 2×ttl]`** (`OutputTail` / keeper INJECT). To guarantee expiry even
at the jitter max, pick `ttl < break/2`. With dwell 45s + recovery latency ~15s ≈ 60s break → use
`EXECUTION_DATA_TTL=15` (effective TTL 15–30s ≪ 60s).

## SUCCESS CRITERION — read this first

The bar is **NOT** "7/7 still PASS." If the TTL truly expires the backup during the break, some crash
scenarios *should* register losses — that is the point. The run confirms the gate **classifies** them
correctly:
- backup physically gone at recovery → keeper reads `STRLEN==0` → logs `"drop"` → `keeperCleanDrop` →
  analyzer **tolerates** (path a) → scenario may still verdict PASS (a *tolerated unrecoverable* loss);
- data was present + reinject logged but hop never lands → **binding miss → FAIL**.
The deliverable is the **verdict diff vs the TTL=900 baseline**, not a clean sweep.

## THE NO-OP TRAP — mandatory pre-check

If you run TTL<break and every verdict is unchanged, that is a RED FLAG, not a success — it means the TTL
was not actually exercised (recovery beat the expiry, or the completing path didn't depend on the expired
key). **Before trusting the matrix, prove the backup genuinely expires during the break:** run one crash
scenario at `EXECUTION_DATA_TTL=15` and confirm from `docker compose logs keeper` a `REINJECT drop` (or an
absent/`STRLEN==0` read) occurring *during* the break, and/or a measurable drop in `skp:*` keys mid-dwell.
No expiry evidence ⇒ the run is a no-op (same lesson as the phase-80 collector-crash falsification).

## Steps

1. **Baseline (already have it — TTL=900):** capstone 7/7 PASS; FALSIFY-01 driver exit 0; phase-80 exit 0.
   Keep these as the diff reference.
2. **Expiry pre-check (the no-op guard):** `EXECUTION_DATA_TTL=15` + one crash scenario
   (`pwsh scripts/phase-67-harness.ps1 -ScenarioId TEST-05`), confirm keeper `REINJECT drop` / `STRLEN==0`
   during the break. Adjust ttl/break until expiry is observed. If never ⇒ investigate (data path may not
   depend on the expired key) before proceeding.
3. **Positive capstone at low TTL:** `EXECUTION_DATA_TTL=15 pwsh scripts/phase-68-sweep.ps1`. Record every
   scenario's verdict + `startedRuns/completeRuns/Missing`. Diff vs baseline. Expect some scenarios to shift
   to tolerated-unrecoverable (still PASS) and/or to FAIL — interpret each against the tolerance model
   (`PassFailEngine.cs:186-258`, keeperCleanDrop / redisWipeInFlight / WR-01).
4. **Gate-teeth at low TTL:** `EXECUTION_DATA_TTL=15 pwsh scripts/phase-79-falsify.ps1`. The manufactured
   recoverable-but-lost strand should still FAIL for the right reason — BUT the low TTL may change the shape
   (the seam leaves data intact; if the TTL expires it mid-flight, the keeper could log `"drop"` instead of
   `"reinject"` → the loss becomes *tolerated* → driver exit 1 as a false regression). Check the analyzer
   detail: `"reinject"` (binding miss, exit 0) vs `"drop"` (tolerated — needs a longer TTL for this control).

## Caveats / gotchas

- **Keeper's own TTL is NOT compose-overridable here.** `Keeper RecoveryOptions.ExecutionDataTtlSeconds`
  (the INJECT re-write TTL) reads the appsettings default 900. The *primary* backup that expires during the
  break is the processor's `ExecutionData` write, which the `EXECUTION_DATA_TTL` knob governs — so the test
  is valid — but for full fidelity also lower the keeper's value (add a `Recovery__ExecutionDataTtl` env to
  the keeper service, or edit its appsettings) so INJECT-re-materialized data expires on the same clock.
- **phase-80 (collector-crash) is ORTHOGONAL to TTL — run it at baseline 900, NOT low.** Its Redis oracle
  reads `skp:out:*` blobs ~50–130s after they are written; at TTL[15,30] those blobs EXPIRE before the
  driver reads them → the count under-reports and the driver would false-FAIL. Low TTL breaks the oracle,
  not the property. Keep phase-80 on the TTL=900 baseline.
- **Restore is automatic:** unset `EXECUTION_DATA_TTL` (or just don't pass it) ⇒ compose interpolates 900 ⇒
  baseline restored. No revert commit needed.

## One-line commands

```
# baseline reference (already captured)
pwsh scripts/phase-68-sweep.ps1                          # 7/7 PASS @ TTL 900

# TTL-below-break run
$env:EXECUTION_DATA_TTL='15'
pwsh scripts/phase-67-harness.ps1 -ScenarioId TEST-05    # pre-check: confirm keeper 'drop' during break
pwsh scripts/phase-68-sweep.ps1                          # positive matrix — DIFF verdicts vs baseline
pwsh scripts/phase-79-falsify.ps1                        # gate-teeth — check 'reinject' vs 'drop' detail
Remove-Item Env:EXECUTION_DATA_TTL                       # restore baseline (→ 900)
# phase-80 stays at TTL 900 (oracle would expire under low TTL)
```

See [[phase-79-negative-control-mechanism]] and [[rebuild-sourcehash-reseed-order]] for the harness/reseed
context, and `tests/BaseApi.Tests/Observability/ObservabilityDecouplingFacts.cs` +
`scripts/phase-80-collector-crash.ps1` for the observability controls this extends.
