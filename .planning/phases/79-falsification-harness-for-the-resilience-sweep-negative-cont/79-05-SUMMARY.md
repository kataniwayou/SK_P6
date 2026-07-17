---
phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont
plan: 05
subsystem: resilience-sweep-negative-control
tags: [gate-teeth, negative-control, live-proof, inertness, D-06]
key-files:
  modified:
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - compose.yaml
    - scripts/phase-67-harness.ps1
metrics:
  gate1_driver_exit: 0
  gate1_missing: 1
  gate2_capstone: "7/7 PASS"
  gate2_sweep_exit: 0
---

# Plan 79-05 — Live gate-teeth proof + inertness — SUMMARY

## Outcome

**Both live gates PASS on the Docker stack.** The resilience-sweep pass/fail gate is proven to have teeth: it fails a genuinely recoverable-but-lost execution for the right reason, and it stays green when the injection seams are off.

| Gate | Result | Evidence |
|------|--------|----------|
| 1 — Gate-teeth (True Positive) | **PASS** | `scripts/phase-79-falsify.ps1` exit **0**; FALSIFY-01 → `verdict=Fail`, `class=VERDICT_FAIL`, `harnessExit=1`, `Missing=1`, `MissingDetail` = "…started-but-incomplete, recoverable-but-lost (no keeper clean-drop, not in-flight-at-wipe) → binding miss." Driver printed "NEGATIVE CONTROL PASSED — the gate correctly FAILED FALSIFY-01 … Gate has teeth." |
| 2 — Inertness (D-06) | **PASS** | Default `scripts/phase-68-sweep.ps1` (TEST-01..07) → **CAPSTONE: 7/7 PASS**, `SWEEP_EXIT=0`, every row `harnessExit=0`, with `KEEPER_DEFEAT_REINJECT`/`PROCESSOR_DEFEAT_READ` unset. All fault families covered (no-fault, processor/orchestrator/keeper/rabbitmq crash, redis restart, multi-tier). |

Hermetic: `PassFailEngineFacts` (30/30) and `ReinjectConsumerFacts` (8/8) green via `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (the seam is inert when unset at the unit level).

## Deviation from locked plan — D-01 mechanism extended (accepted, user-approved)

**The locked D-01 mechanism (keeper suppress-send ALONE) could not fire.** Execution proved that a `KeeperReinject` is produced *only* by a processor L2 read-fault (`ProcessorPipeline.cs:81/92`); a healthy no-crash run never faults, so the keeper's `KEEPER_DEFEAT_REINJECT` seam (Plan 79-01) had nothing to defeat — the first live run scored `Missing=0, PASS` (no reinject at all).

The mechanism was extended (user approved "extend mechanism, finish it") with a **second default-off, env-gated seam** — a processor reinject **trigger** (commit `fd47fbe`):

- **`PROCESSOR_DEFEAT_READ`** (a target step LABEL) in `ProcessorPipeline`. Armed out-of-band by the harness SETting the Redis slot `skp:test:defeat-read-arm` at window-open; the first matching hop atomically claims it (`KeyDelete` → true for exactly one caller across replicas) and faults ALL its read retries by throwing `KeyAbsentException` **before** `StringGet` (L2 data left intact). The processor sends a `KeeperReinject`; the keeper finds data present → logs "reinject" → the keeper seam loses the redispatch → **byte-identical recoverable-but-lost binding miss**.
- **Target Step_C, not Step_B.** The victim must be a *scored* run (≥1 framework hop record). `Step_A` is a dropped source-entry marker (Phase-78 D-01), so faulting the first real hop (`Step_B`) leaves the execution with zero records → invisible to the analyzer → not a binding miss. Faulting `Step_C` keeps `Step_B`'s record (the run is "started") and drops `C→G` (incomplete). `Step_C` is still pre-fan-out (linear), so the convergent terminal `Step_G` cannot complete via a sibling branch.
- WR-01 (keeper "reinject") vetoes the redis-wipe tolerance, so cohort timing (`startedAfterRecovery`) is not load-bearing for the classification.

## Operational notes surfaced during execution

- **SourceHash reseed order** ([[rebuild-sourcehash-reseed-order]]): changing processor product source mints a new assembly `SourceHash`; the harness reset heal-wait deadlocks for a brand-new hash (no processor row until the seeder runs, but the seeder runs after reset). Bootstrap once: graph-DELETE → `FanOutSeeder` seed → the container registers its liveness row (persists across resets). A stale `tests/bin` copy from a broken incremental build can make the seeder register the *wrong* hash — force a clean rebuild of the processor chain + tests so host and container `SourceHash` agree.
- The seam emits `TEST-79 reinject-trigger CLAIMED/FAULTING` (processor) and `REINJECT DEFEATED (TEST-ONLY seam)` (keeper) log lines when armed — inert (never emitted) when unset.

## Self-Check: PASSED

- [x] Gate 1: `phase-79-falsify.ps1` exit 0 with FALSIFY-01 `verdict=Fail`, `harnessExit=1`, `Missing≥1`, right MissingDetail.
- [x] Gate 2: default TEST-01..07 sweep 7/7 PASS with seams unset (D-06 inertness).
- [x] Hermetic seam facts green; seam short-circuits when unset.
- [x] Default `phase-68-sweep.ps1` id list byte-unchanged (TEST-01..07; FALSIFY-01 absent from the capstone).
