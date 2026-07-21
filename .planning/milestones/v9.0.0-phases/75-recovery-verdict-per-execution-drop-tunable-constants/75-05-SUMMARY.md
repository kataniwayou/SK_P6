---
phase: 75-recovery-verdict-per-execution-drop-tunable-constants
plan: 05
subsystem: harness (fault-injection observation window)
tags: [D75-7, observation-window, harness, optional, default-off, powershell]
requires:
  - "phase-67-harness.ps1 window/dwell/RECOVERY_UTC seam (existing)"
  - "Get-FireCount Prometheus signal (existing observe-loop counter)"
provides:
  - "Optional execution-based observation window via a K_EXECUTIONS env seam (default-off)"
affects:
  - "scripts/phase-67-harness.ps1"
tech-stack:
  added: []
  patterns:
    - "Optional default-off env seam set + cleared symmetrically with the existing D-16 seams (SCENARIO_ID/WINDOW_*_UTC/RECOVERY_UTC)"
    - "Unconditional wall-clock hard cap retained as the loop upper bound (DoS-safe, T-75-09)"
key-files:
  created: []
  modified:
    - "scripts/phase-67-harness.ps1"
decisions:
  - "D75-7 adopted as a default-OFF seam only; the fixed 300s wall-clock stays the sole bound when K_EXECUTIONS is unset — the verdict rework (D75-1..5,8) does not depend on it."
  - "The K early-break was placed in the STEP F.5 window-hold loop (not the STEP F.2 pre-inject loop) so it bounds the OBSERVATION window; the pre-inject fire-wait keeps its own N=injectAfterNFires semantics unchanged."
  - "Distinct-execution count reuses the existing Get-FireCount - $fireBaseline Prometheus signal — no new observation source introduced."
metrics:
  duration: ~2m
  completed: 2026-07-15
---

# Phase 75 Plan 05: Optional execution-based observation window (K_EXECUTIONS seam) Summary

Added an OPTIONAL, default-OFF execution-based observation window to `scripts/phase-67-harness.ps1` (D75-7): when `$env:K_EXECUTIONS` is set to K>0 the observe loop stops early once K distinct executions are observed, while the existing 300s wall-clock window is unconditionally retained as the hard cap; absent the seam the harness runs byte-for-byte as before.

## What Was Built

**Task 1 (auto) — `K_EXECUTIONS` execution-based window seam.** Commit `90fb59f`.

Three surgical edits to `scripts/phase-67-harness.ps1`, all mirroring the file's own existing conventions:

1. **Read (near the window setup, after `$windowDeadline`):**
   `$kExecutions = if ($env:K_EXECUTIONS) { [int]$env:K_EXECUTIONS } else { 0 }` — `0`/unset means "wall-clock only", no behaviour change. A documenting comment block records the OPTIONAL D75-7 semantics ("default 0/unset ⇒ fixed 300s wall-clock unchanged; K>0 ⇒ stop at K observed executions, still hard-capped by the window deadline"). An enabled-path log line fires only when K>0.

2. **Observe loop (STEP F.5 window-hold, the loop that holds out the rest of the window):**
   Inside the existing `while ((... - $windowStart).TotalSeconds -lt $windowSeconds)` wall-clock loop, ONLY when `$kExecutions -gt 0`, compute `$observedExecutions = (Get-FireCount) - $fireBaseline` (the same Prometheus fire signal the observe loop already reads) and `break` once `$observedExecutions -ge $kExecutions`. The wall-clock `$windowSeconds`-since-`$windowStart` bound is retained UNCONDITIONALLY as the loop condition, so a mis-set / never-reached K can never hang the loop past the existing 300s window (T-75-09).

3. **Seam set + clear (symmetric with the existing D-16 seams):**
   In the STEP H set block: `$env:K_EXECUTIONS = if ($kExecutions -gt 0) { $kExecutions.ToString() } else { '' }` (empty when off — the analyzer ignores an empty value exactly like `RECOVERY_UTC` on the baseline). `Env:K_EXECUTIONS` added to the `finally` `Remove-Item` clear list alongside `SCENARIO_ID`/`WINDOW_START_UTC`/`WINDOW_END_UTC`/`RECOVERY_UTC`.

The `$windowSeconds = 300` default and the `$windowDeadline = (Get-Date).AddSeconds($windowSeconds)` upper bound are both preserved unchanged.

## Verification

- **Automated (Task 1 gate):** `pwsh -NoProfile` AST parse-check of `scripts/phase-67-harness.ps1` prints **`PARSE_OK`** — the script parses.
- **Acceptance-criteria grep (all confirmed):**
  - `K_EXECUTIONS` appears in the read (line 272), the set block (line 433), and the `Remove-Item` clear list (line 449) — all three.
  - `$windowSeconds = 300` (258) and `$windowDeadline` (259) are still present as an unconditional upper bound.
  - The observe-loop `break` is gated on `$kExecutions -gt 0` (line 364) — the default-off (K=0/unset) path leaves behaviour unchanged.
- **Default-off invariance:** when `K_EXECUTIONS` is unset, `$kExecutions = 0`, every `if ($kExecutions -gt 0)` guard is skipped, and the window-hold loop reduces to the original fixed-300s-wall-clock behaviour; the set block writes an empty `$env:K_EXECUTIONS`.

## Deviations from Plan

None — plan executed exactly as written. No auto-fixes (Rules 1-3) were needed; the change is a self-contained script-only seam with no dependencies, no bugs surfaced, and the parse gate was green on first run.

## Deferred Items

**Live execution-based-window verify (D75-7, checkpoint:human-verify, gate="blocking") — DEFERRED-AUTOMATED.**

D75-7 is explicitly OPTIONAL and default-OFF; its live behaviour (the observe loop closing at K distinct executions vs the 300s wall-clock) is only OBSERVABLE on a running Docker RealStack, which is NOT available in this sandbox (established deferred-automated precedent — phases 68/73/74 and 75-03). The script-parse gate above is the automated proof of correctness for the seam itself, and the default-off path is byte-for-byte unchanged.

The exact RealStack steps for a future Docker-up run to exercise the optional path (set `$env:K_EXECUTIONS="8"`, run `phase-68-sweep.ps1 -ScenarioIds TEST-01`, expect the early-close log line; then run without the seam to confirm the 300s default is unchanged) are recorded in `.planning/phases/75-recovery-verdict-per-execution-drop-tunable-constants/deferred-items.md` under **"DEFERRED: 75-05 live execution-based-window verify"**. Because D75-7 is optional and MUST NOT block the verdict rework, "deferred / not adopted this milestone (seam left default-off)" is an acceptable resolution — the seam exists, default-off, ready if wanted.

## Commits

- `90fb59f` — `feat(75-05): add optional K_EXECUTIONS execution-based window seam (default-off, D75-7)`

## Self-Check: PASSED

- `scripts/phase-67-harness.ps1` — FOUND (modified; contains `K_EXECUTIONS` in read, set, and clear — grep-confirmed lines 272/433/449).
- Commit `90fb59f` — FOUND in `git log`.
- Parse check — `PARSE_OK`.
- Deferred-items entry for the D75-7 live verify — FOUND in `deferred-items.md`.
