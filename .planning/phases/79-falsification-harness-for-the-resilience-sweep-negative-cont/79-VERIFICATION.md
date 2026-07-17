---
phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont
verified: 2026-07-17T00:00:00Z
status: passed
score: 10/10 must-haves verified
overrides_applied: 0
re_verification:
  previous_status: none
  previous_score: n/a
---

# Phase 79: Falsification harness for the resilience sweep (negative-control) — Verification Report

**Phase Goal:** Prove the resilience-sweep pass/fail gate has TEETH — a default-off, env-gated lossy injection seam manufactures exactly one byte-identical recoverable-but-lost strand (keeper logs "reinject" but the redispatch is lost), and a standalone driver asserts the sweep flips that scenario to VERDICT_FAIL for the recoverable-but-lost binding-miss reason (True Positive fires, no silent-green False Negative), while the seam stays provably inert (7/7 capstone unaffected — D-06).

**Verified:** 2026-07-17
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| #  | Truth | Status | Evidence |
| -- | ----- | ------ | -------- |
| 1 | Keeper env-gated one-shot suppress-send seam exists (KEEPER_DEFEAT_REINJECT) that suppresses ep.Send for the first gated reinject while keeping CountSent + "reinject" log byte-identical | ✓ VERIFIED | `ReinjectConsumer.cs:34,91-96` — static `_defeatedOnce` latch; `int.TryParse(...KEEPER_DEFEAT_REINJECT...) && d>0 && Interlocked.CompareExchange(...)==0`; `if (!defeatReinject)` gates `ep.Send`; CountSent + "reinject" log kept after the block |
| 2 | Seam is provably inert when unset (short-circuit: Interlocked never runs when var unset/0) | ✓ VERIFIED | `ReinjectConsumer.cs:91-93` short-circuit `&&`; hermetic evidence `*ReinjectConsumerFacts` 8/8 green (79-05-SUMMARY); compose default `${KEEPER_DEFEAT_REINJECT:-0}` |
| 3 | Processor reinject-TRIGGER seam exists (PROCESSOR_DEFEAT_READ) that faults one L2 read before StringGet (L2 intact) so a KeeperReinject flows — the accepted D-01 extension | ✓ VERIFIED | `ProcessorPipeline.cs:108-125` — reads `PROCESSOR_DEFEAT_READ`, `if (!IsNullOrEmpty && != "0" && payload.Contains(label))`, atomic `KeyDeleteAsync("skp:test:defeat-read-arm")` claim, `throw new KeyAbsentException()` before `StringGetAsync`; short-circuits when unset (`:107-109`) |
| 4 | compose.yaml plumbs both seam env vars default-off | ✓ VERIFIED | `compose.yaml:250` `KEEPER_DEFEAT_REINJECT: "${KEEPER_DEFEAT_REINJECT:-0}"`; `:301` `PROCESSOR_DEFEAT_READ: "${PROCESSOR_DEFEAT_READ:-0}"` |
| 5 | Hermetic fact pins the MissingDetail substring contract ("recoverable-but-lost" AND "binding miss") the driver greps | ✓ VERIFIED | `PassFailEngineFacts.cs:386-409` `KeeperReinject_BindingMiss_DetailNamesRecoverableButLost` asserts `Contains("recoverable-but-lost") && Contains("binding miss")`; 30/30 green (79-05-SUMMARY) |
| 6 | FALSIFY-01 out-of-band scenario wired into phase-67-harness (faultType inject-recovery-loss, no crash, seams+K_EXECUTIONS=1 armed pre-STEP-A, keeper/processor scaled to 1 replica, teardown in finally) | ✓ VERIFIED | `phase-67-harness.ps1:107` row; `:133-143` export branch (KEEPER_DEFEAT_REINJECT=1, PROCESSOR_DEFEAT_READ=Step_C, K_EXECUTIONS=1); `:339-360` no-crash scale-to-1 branch + arm slot; `:584` finally teardown |
| 7 | Default phase-68-sweep.ps1 id list byte-unchanged TEST-01..07 (FALSIFY-01 absent from the capstone) | ✓ VERIFIED | `phase-68-sweep.ps1:49` default `@('TEST-01'..'TEST-07')`; no FALSIFY reference in the default path |
| 8 | Standalone phase-79-falsify.ps1 inverted, fail-closed driver exists — invokes sweep -ScenarioIds FALSIFY-01, asserts harnessExit==1 exactly, Missing>=1, MissingDetail names both substrings, verdict=='Fail'/VERDICT_FAIL, sweep non-zero; own exit 0 == gate has teeth | ✓ VERIFIED | `phase-79-falsify.ps1:75` invokes sweep; `:107` `harnessExit -eq 1`; `:91-93` exact-1 gate (2→exit 2, 64→64, other→passthrough); `:111` both substrings; `:109` verdict/class; `:114-115` exit 0 on pass |
| 9 | LIVE Gate 1 (gate-teeth True Positive): phase-79-falsify.ps1 exits 0 — FALSIFY-01 flipped to verdict=Fail/VERDICT_FAIL/harnessExit=1/Missing=1 for the recoverable-but-lost binding-miss reason | ✓ VERIFIED | Recorded live-run evidence 79-05-SUMMARY: `gate1_driver_exit: 0`, `gate1_missing: 1`; driver printed "NEGATIVE CONTROL PASSED … Gate has teeth"; confirmed by analyzer-reports/phase-68-summary.json during the run |
| 10 | LIVE Gate 2 (inertness / D-06): default TEST-01..07 sweep with seams unset still 7/7 PASS, capstone exit 0 | ✓ VERIFIED | Recorded live-run evidence 79-05-SUMMARY: `gate2_capstone: "7/7 PASS"`, `gate2_sweep_exit: 0`, every row harnessExit=0, seams unset |

**Score:** 10/10 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
| -------- | -------- | ------ | ------- |
| `src/Keeper/Recovery/ReinjectConsumer.cs` | Env-gated one-shot suppress-send seam | ✓ VERIFIED | Latch `:34`, gated `ep.Send` `:91-96`; substantive + wired (compose + harness export) |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | Reinject-trigger seam (accepted D-01 extension) | ✓ VERIFIED | `:108-125`; substantive + wired (compose + harness arm) |
| `compose.yaml` | Both seam env vars default-off | ✓ VERIFIED | `:250`, `:301` — `${VAR:-0}` interpolation |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` | Hermetic MissingDetail substring pin | ✓ VERIFIED | `:386-409`; 30/30 green |
| `scripts/phase-67-harness.ps1` | FALSIFY-01 row + inject-recovery-loss branch | ✓ VERIFIED | `:107,133-143,339-360,584` |
| `scripts/phase-79-falsify.ps1` | Inverted fail-closed driver | ✓ VERIFIED | Full assertion contract present; own exit 0 == gate RED for right reason |
| `scripts/phase-68-sweep.ps1` | Default id list unchanged | ✓ VERIFIED | `:49` TEST-01..07, FALSIFY-01 absent |

### Key Link Verification

| From | To | Via | Status | Details |
| ---- | -- | --- | ------ | ------- |
| compose keeper env | ReinjectConsumer.HandleAsync | KEEPER_DEFEAT_REINJECT | ✓ WIRED | `compose.yaml:250` → `ReinjectConsumer.cs:92` GetEnvironmentVariable |
| compose processor env | ProcessorPipeline read loop | PROCESSOR_DEFEAT_READ | ✓ WIRED | `compose.yaml:301` → `ProcessorPipeline.cs:108` GetEnvironmentVariable |
| harness export/arm | seams | $env exports + redis-cli SET defeat-read-arm | ✓ WIRED | `phase-67-harness.ps1:134,141,345` |
| suppressed path | analyzer WR-01 veto | CountSent + "reinject" log after the gate | ✓ WIRED | `ReinjectConsumer.cs` log kept after gated block; pinned hermetically `PassFailEngineFacts.cs:386-409` |
| phase-79-falsify.ps1 | phase-68-sweep.ps1 -ScenarioIds FALSIFY-01 | & pwsh -File, $LASTEXITCODE | ✓ WIRED | `phase-79-falsify.ps1:75` |
| driver assertions | analyzer report + summary | reads Missing/MissingDetail + verdict/harnessExit (never re-scores) | ✓ WIRED | `phase-79-falsify.ps1:84-111` |
| keeper seam armed | VERDICT_FAIL | live phase-79-falsify.ps1 exit 0 | ✓ WIRED | Live Gate 1 evidence (79-05-SUMMARY) |
| seam present-but-unset | 7/7 capstone PASS | live phase-68-sweep.ps1 exit 0 | ✓ WIRED | Live Gate 2 evidence (79-05-SUMMARY) |

### Behavioral Spot-Checks

The two behavioral proofs (Gate 1 gate-teeth, Gate 2 inertness) require the live Docker stack and were executed during Plan 79-05. They are recorded with explicit metrics in 79-05-SUMMARY.md frontmatter (`gate1_driver_exit: 0`, `gate1_missing: 1`, `gate2_capstone: "7/7 PASS"`, `gate2_sweep_exit: 0`). Per the verification scope, this recorded live-run evidence is treated as authoritative — the verifier does not re-run the Docker stack. Static structural verification (above) confirms the code paths those live runs exercised are present and correctly wired.

| Behavior | Source | Result | Status |
| -------- | ------ | ------ | ------ |
| Referenced commits exist in git | git log fd47fbe/7ec9169/4a9f692/da0206e/c537164/92b94ad | all resolve to the described commits | ✓ PASS |
| Live Gate 1 driver exit 0 (True Positive) | 79-05-SUMMARY metrics | exit 0, Missing=1 | ✓ PASS (recorded) |
| Live Gate 2 capstone 7/7 (inertness) | 79-05-SUMMARY metrics | 7/7 PASS, exit 0 | ✓ PASS (recorded) |

### Requirements Coverage

n/a — no REQUIREMENTS.md for this milestone (phase_req_ids = null). Tracked via ROADMAP goal + 79-CONTEXT.md D-01..D-06. All six locked decisions verified:
- D-01 (suppress-send seam): ✓ present, EXTENDED with an accepted processor reinject-trigger (commit `fd47fbe`, user-approved) — see Accepted Deviation below.
- D-02 (one execution): ✓ static one-shot latch + K_EXECUTIONS=1 + scale-to-1 replica.
- D-03 (right-reason assertion): ✓ exact harnessExit==1 + MissingDetail substrings.
- D-04 (out-of-band scenario driven through sweep): ✓ FALSIFY-01 row + driver invokes sweep, dual-level assertion.
- D-05 (recoverable-but-lost axis only): ✓ missing/duplicate axes deferred (deferred-items.md).
- D-06 (inertness): ✓ Gate 2 7/7 PASS with seams unset; hermetic facts green.

### Accepted Deviation (does not affect goal achievement)

The locked D-01 mechanism (keeper suppress-send ALONE) was found unworkable during execution: a healthy no-crash run never faults, so a KeeperReinject only flows when a processor L2 read faults — the keeper seam had nothing to defeat (first live run scored Missing=0/PASS). Per user approval ("extend mechanism, finish it"), a second default-off env-gated seam — a processor reinject TRIGGER (`PROCESSOR_DEFEAT_READ`) in `ProcessorPipeline.cs`, commit `fd47fbe` — manufactures one transient L2 read fault (data left intact) so a recoverable KeeperReinject flows; the keeper seam then loses that one redispatch → byte-identical recoverable-but-lost binding miss. Target is Step_C (a scored, pre-fan-out linear hop) so the run is "started" yet the convergent terminal Step_G cannot complete via a sibling branch. This is a documented, accepted deviation (79-05-SUMMARY "Deviation" section). The phase GOAL (gate proven to have teeth + seam inert) is fully met; the deviation only changes the injection mechanism, not the outcome or the inertness guarantee.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
| ---- | ---- | ------- | -------- | ------ |
| `scripts/phase-67-harness.ps1` | 347 | Stale comment says "first post-window Step_B hop" while `PROCESSOR_DEFEAT_READ='Step_C'` (`:141`) | ℹ️ Info | Cosmetic comment drift only — the exported value and the Step_C rationale (scored hop, pre-fan-out) are correct per 79-05-SUMMARY; no functional impact |

No blocker or warning anti-patterns. The TEST-ONLY seams are explicitly commented "DEFAULT OFF — NEVER set in production" and short-circuit when unset (verified inert both hermetically and via the live Gate 2 7/7 capstone).

### Human Verification Required

None. The two behavioral gates that would normally require human/live testing were already executed live during Plan 79-05 (a `checkpoint:human-verify` blocking task) and their results are recorded with explicit metrics. All remaining verification is static/structural and passed.

### Gaps Summary

No gaps. All 10 observable truths verified, all 7 artifacts exist and are substantive + wired, all 8 key links connected, both live gates recorded as PASS, and all six locked decisions (D-01..D-06) satisfied — with the D-01 mechanism extension being a documented, user-approved accepted deviation that does not reduce goal achievement. The single anti-pattern is a cosmetic stale comment (Info severity). The phase goal — proving the resilience-sweep pass/fail gate has teeth (True Positive fires, no silent-green False Negative) while the injection seam stays provably inert — is achieved.

---

_Verified: 2026-07-17_
_Verifier: Claude (gsd-verifier)_
