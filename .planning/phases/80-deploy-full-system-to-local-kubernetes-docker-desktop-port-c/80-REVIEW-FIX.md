---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
fixed_at: 2026-07-18T00:00:00Z
review_path: .planning/phases/80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c/80-REVIEW.md
iteration: 1
findings_in_scope: 2
fixed: 2
skipped: 0
status: all_fixed
---

# Phase 80: Code Review Fix Report

**Fixed at:** 2026-07-18
**Source review:** .planning/phases/80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c/80-REVIEW.md
**Iteration:** 1

**Summary:**
- Findings in scope: 2 (0 Critical, 2 Warning — Info findings out of scope)
- Fixed: 2
- Skipped: 0

## Fixed Issues

### WR-01: Kube-context guard is warn-only — a mis-pointed context still receives `kubectl apply`

**Files modified:** `scripts/phase-80-up.ps1`
**Commit:** bb2e3f5
**Applied fix:** Added a `param([switch]$AllowNonDockerDesktop)` block at the top of the script (placed after the header comment block, as the first executable statement, so `param` remains valid). Converted STEP 2 from a soft yellow warning into a HARD-FAIL: when `kubectl config current-context` is not `docker-desktop` and the override switch is not set, the script now writes a red refusal message and `exit 12` (a distinct exit code — STEP 3/4/6 use 10 for apply/rollout failures and STEP 8 uses 11 for readiness timeout). Passing `-AllowNonDockerDesktop` downgrades it to a yellow "proceeding anyway" notice. The harness invokes this script with no args, so the docker-desktop happy path is unchanged; only a non-docker-desktop context without the override now aborts before `kubectl apply -k k8s/` can touch a shared/real cluster.

### WR-02: Re-running bring-up does not clear stale port-forwards → misleading "stack UP"

**Files modified:** `scripts/phase-80-up.ps1`
**Commit:** abb85ac
**Applied fix:** Added a "STEP 7-pre" reap block immediately before the port-forward launch loop. It computes the `.k8s-portforward-pids` path and, if the file exists, best-effort `Stop-Process -Force -ErrorAction SilentlyContinue` for every recorded PID (mirroring the harness STEP Z teardown), then removes the stale PID file. This releases the loopback ports a prior run may still hold so the new forwards can actually bind — preventing the failure mode where a new forward exits immediately on a bind collision, its dead PID gets written to the file, and STEP 8's `localhost:8080` probe is answered 200 by the orphaned OLD baseapi forward, falsely reporting "stack UP" while the non-8080 tunnels are dead. The exact 8-forward set (8080/9090/9200/15673/6380→6379/5433→5432/5673→5672/4317→4317), the `--address 127.0.0.1` binding, and the downstream PID-file persist contract that phase-80-harness.ps1 STEP Z reads for teardown are all preserved unchanged.

## Verification

Both fixes verified with:
- Tier 1: re-read of edited regions — fix text present, port-forward set and PID-file persist contract intact, no corruption.
- Tier 2: full-file parse check under `pwsh` (`[System.Management.Automation.Language.Parser]::ParseFile`) after each edit — PARSE OK both times (no syntax errors).

No manifests or other scripts were modified. No findings were skipped.

---

_Fixed: 2026-07-18_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
