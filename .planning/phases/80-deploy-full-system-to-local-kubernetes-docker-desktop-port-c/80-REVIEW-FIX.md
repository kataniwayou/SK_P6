---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
fixed_at: 2026-07-18T00:00:00Z
review_path: .planning/phases/80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c/80-REVIEW.md
iteration: 2
findings_in_scope: 8
fixed: 6
skipped: 2
status: partial
---

# Phase 80: Code Review Fix Report

**Fixed at:** 2026-07-18
**Source review:** .planning/phases/80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c/80-REVIEW.md
**Iteration:** 2

**Summary:**
- Findings in scope: 8 (fix_scope: all — 2 warning + 6 info)
- Fixed: 6 (2 warnings already applied in a prior pass; 4 info this pass)
- Skipped: 2 (intentional design choices — see rationale)

**Post-fix verification:** both `scripts/phase-80-harness.ps1` and `scripts/phase-80-up.ps1`
parse clean under `pwsh` (PowerShell AST parser, zero errors), and
`kubectl apply -k k8s/ --dry-run=client` exits 0 with no deprecation warning.

## Fixed Issues

### WR-01: Kube-context guard is warn-only

**Files modified:** `scripts/phase-80-up.ps1`
**Commit:** bb2e3f5 (prior pass — no change needed this iteration)
**Applied fix:** STEP 2 now hard-fails (`exit 12`) when the current kube context is not
`docker-desktop`, unless the operator opts in via the new `-AllowNonDockerDesktop` switch.
Verified already present in the working tree; not re-touched this pass.

### WR-02: Re-running bring-up does not clear stale port-forwards

**Files modified:** `scripts/phase-80-up.ps1`
**Commit:** abb85ac (prior pass — no change needed this iteration)
**Applied fix:** New STEP 7-pre block reaps any PIDs recorded in a pre-existing
`.k8s-portforward-pids` before binding the new forward set, mirroring the harness STEP Z
teardown. Verified already present in the working tree; not re-touched this pass.

### IN-01: `.Trim()` on raw kubectl output runs before the `$LASTEXITCODE` guard

**Files modified:** `scripts/phase-80-harness.ps1`
**Commit:** d909693
**Applied fix:** At both STEP A2 (`$procCount`) and STEP D (`$wfId`), the kubectl-exec stdout is
now captured raw first, the exit code is checked (STEP D pins `$LASTEXITCODE` into `$wfIdExit`
before the trim), and only then is `("$raw").Trim()` applied. String-casting the raw capture
means the trim can never hit `$null` on a failed exec, so the distinct `exit 30` / `exit 40`
infra codes are preserved instead of dying with a generic exit 1 under
`$ErrorActionPreference='Stop'`. Original messages and exit codes preserved verbatim.

### IN-03: `kustomization.yaml` uses deprecated `commonLabels`

**Files modified:** `k8s/kustomization.yaml`
**Commit:** 9d70388
**Applied fix:** Replaced the deprecated `commonLabels:` block with the modern
`labels:` + `pairs:` + `includeSelectors: false` form. This removes the kustomize v5 deprecation
warning and stops the labels being injected into immutable `spec.selector.matchLabels` / Service
selectors, eliminating the latent delete/recreate foot-gun. Re-validated with
`kubectl apply -k k8s/ --dry-run=client` (exit 0, no warning).

### IN-04: Empty catch swallows all errors on the best-effort stop POST

**Files modified:** `scripts/phase-80-harness.ps1`
**Commit:** a3a8ecf
**Applied fix:** The STEP F.6 `POST /orchestration/stop` `catch { }` now logs at low severity:
`Write-Phase "  stop POST best-effort failed: $($_.Exception.Message)" 'Yellow'`. Still
best-effort (no rethrow), but the signal is no longer silently discarded.

### IN-05: Port-forward teardown kills by raw PID (recycled-PID hazard)

**Files modified:** `scripts/phase-80-harness.ps1`, `scripts/phase-80-up.ps1`
**Commit:** ad78c5f
**Applied fix:** Both kill sites — the harness STEP Z teardown and the up.ps1 STEP 7-pre stale-PID
reap (added by WR-02) — now guard each `Stop-Process -Id` with
`Get-Process -Id $p -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'kubectl' }`,
so a recycled PID belonging to an unrelated process is skipped rather than force-killed. Remains
best-effort (wrapped in try/catch, never throws).

## Skipped Issues

### IN-02: No `securityContext` on any workload

**File:** `k8s/10-postgres.yaml`, `k8s/11-redis.yaml`, `k8s/12-rabbitmq.yaml`,
`k8s/13-elasticsearch.yaml`, `k8s/20-otel-collector.yaml`, `k8s/21-prometheus.yaml`,
`k8s/30-baseapi-service.yaml`, `k8s/31-orchestrator.yaml`, `k8s/32-keeper.yaml`,
`k8s/33-processor-sample.yaml`
**Reason:** Intentionally NOT applied. The review itself classifies this as acceptable for a local
single-node dev deploy and out of compose-parity scope — "noted for awareness" rather than a
defect. Adding `runAsNonRoot` / dropped capabilities / `readOnlyRootFilesystem` to the infra images
(elasticsearch, postgres, rabbitmq) is genuinely risky: those images need specific uid/fsGroup and
data-dir ownership and could break the pods that just passed the live proof. CONTEXT D-* left
security posture to discretion and the goal is compose parity (compose ran without these). This is
the first hardening to add IF the stack is ever lifted to a shared cluster; skipped here to avoid
regressing the validated stack.
**Original issue:** No pod- or container-level securityContext is declared anywhere, so every
container runs with its image default (several as root) and full default capabilities on a writable
root filesystem.

### IN-06: No CPU requests/limits and no liveness probes on infra tiers

**File:** `k8s/10-postgres.yaml`, `k8s/11-redis.yaml`, `k8s/12-rabbitmq.yaml`,
`k8s/13-elasticsearch.yaml`, `k8s/20-otel-collector.yaml`, `k8s/21-prometheus.yaml`
**Reason:** Intentionally NOT applied. The review calls these "reasonable simplifications for a
single-node dev proof" that "match the compose posture … noted only for awareness rather than as a
defect." Adding CPU limits risks throttling; adding liveness probes to stateful backing services
risks restart loops during slow operations. The design goal is compose parity (carry compose values
verbatim), and compose set neither. Skipped as an intentional compose-parity simplification, not a
defect.
**Original issue:** Only memory requests/limits are set (no CPU), so all pods land in Burstable QoS
with unbounded CPU, and the infra tiers rely solely on readiness/startup probes with no liveness
probe.

---

_Fixed: 2026-07-18_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 2_
