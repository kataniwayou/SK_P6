---
phase: 80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c
reviewed: 2026-07-18T00:00:00Z
depth: standard
files_reviewed: 18
files_reviewed_list:
  - k8s/00-namespace.yaml
  - k8s/01-secret.yaml
  - k8s/02-configmaps.yaml
  - k8s/10-postgres.yaml
  - k8s/11-redis.yaml
  - k8s/12-rabbitmq.yaml
  - k8s/13-elasticsearch.yaml
  - k8s/20-otel-collector.yaml
  - k8s/21-prometheus.yaml
  - k8s/30-baseapi-service.yaml
  - k8s/31-orchestrator.yaml
  - k8s/32-keeper.yaml
  - k8s/33-processor-sample.yaml
  - k8s/kustomization.yaml
  - scripts/phase-80-build.ps1
  - scripts/phase-80-up.ps1
  - scripts/phase-80-reset.ps1
  - scripts/phase-80-harness.ps1
findings:
  critical: 0
  warning: 2
  info: 6
  total: 8
status: issues_found
---

# Phase 80: Code Review Report

**Reviewed:** 2026-07-18
**Depth:** standard
**Files Reviewed:** 18
**Status:** issues_found

## Summary

This phase ports a proven docker-compose stack to raw kubectl YAML manifests plus PowerShell
bring-up/reset/harness scripts for a local, single-node Docker Desktop Kubernetes deploy. The
manifests are clean, heavily documented, and internally consistent: probe strategy (decoupled
readiness/liveness, startupProbes budgeting the ES/rabbitmq cold starts), the `$(VAR)`-ordered
dependent-env postgres connection string, memory requests/limits, headless-Service-per-StatefulSet
DNS, and the `:local`+`IfNotPresent`+`rollout restart` image-currency contract are all correct and
match the stated design. The intentionally-committed dev Secret is properly scoped (namespace `skp`,
`sensitivity: dev-only` label, public throwaway creds mirroring `.env`) and does not leak beyond its
documented single-node intent. SQL and `kubectl exec` calls use only static literals — no injection
surface. Port-forwards correctly bind `--address 127.0.0.1` only.

No Critical issues. The findings below are two robustness gaps in `phase-80-up.ps1` (a soft
cluster-context guard, and no cleanup of stale port-forwards on re-run) plus informational hardening
notes. None block the proven happy-path, but two can produce a misleadingly-green "stack UP" report.

The intentionally-scoped design choices called out in the task brief (D-08 omitting the 4 test-only
env seams and shipping 3 literal "900" TTLs, orchestrator `strategy: Recreate` for the exclusive-queue
singleton, `Processor.BadConfig` excluded) were all verified as correct and are NOT flagged.

## Warnings

### WR-01: Kube-context guard is warn-only — a mis-pointed context still receives `kubectl apply`

**File:** `scripts/phase-80-up.ps1:41-47`
**Issue:** STEP 2 checks `kubectl config current-context` and, when it is not `docker-desktop`,
only prints a yellow warning and continues to STEP 3, which unconditionally runs
`kubectl apply -k k8s/`. The whole threat model (T-80-16/T-80-17, and the Secret header's emphatic
"NEVER a real cluster") rests on this stack only ever touching the local single node. A soft warning
scrolls past in automation (the harness invokes this script non-interactively via `pwsh -File`), so
an operator whose kubectl context is pointed at a shared/real cluster would silently create namespace
`skp`, the dev Secret, and every workload there. The dev creds themselves are public throwaway values,
but "apply the whole stack to the wrong cluster" is exactly the outcome the design tries to prevent.
The reset script hard-fails its preconditions; the apply path should be at least as strict.
**Fix:** Hard-fail on a non-`docker-desktop` context unless an explicit override is passed:
```powershell
if ($ctx -ne 'docker-desktop' -and -not $AllowNonDockerDesktop) {
    Write-Phase "kube context is '$ctx', not 'docker-desktop'. Refusing to apply. Re-run with -AllowNonDockerDesktop to override." 'Red'
    exit 10
}
```

### WR-02: Re-running bring-up does not clear stale port-forwards → misleading "stack UP"

**File:** `scripts/phase-80-up.ps1:134-171`
**Issue:** STEP 7 starts eight `kubectl port-forward --address 127.0.0.1 <port>` background processes
but never checks for forwards left alive by a prior run (the PIDs of which live in
`.k8s-portforward-pids`, which is overwritten, not reconciled). On a re-run without teardown, each new
forward whose loopback port is still held by a previous process fails to bind and exits immediately;
`Start-Process -PassThru` still returns a (now-exited) process object whose PID is written to the file.
STEP 8 then polls only `http://localhost:8080/health/ready` — which the *old* baseapi forward still
answers 200 — so the script reports "stack is UP and harness-reachable" while the non-8080 tunnels
(9090/9200/5673/6380/5433/15673/4317) may be dead and the PID file now tracks exited processes,
orphaning the still-live prior forwards. The compose-collision guard (STEP 1 `docker compose down`)
handles compose, but not a prior *k8s* forward set.
**Fix:** Before STEP 7, stop any PIDs recorded in an existing `.k8s-portforward-pids` (mirroring the
harness STEP Z teardown), and/or verify each forward with a bounded readiness probe on its own port
rather than gating on 8080 alone:
```powershell
if (Test-Path $pidFile) {
    Get-Content $pidFile | Where-Object { $_ -match '\S' } |
        ForEach-Object { Stop-Process -Id ([int]$_) -Force -ErrorAction SilentlyContinue }
    Remove-Item $pidFile -ErrorAction SilentlyContinue
}
```

## Info

### IN-01: `.Trim()` on raw kubectl output runs before the `$LASTEXITCODE` guard (breaks the documented exit-code contract)

**File:** `scripts/phase-80-harness.ps1:135-137` (and `:199-203`)
**Issue:** `$procCount = (kubectl ... -c "SELECT count(*) FROM processors").Trim()` calls `.Trim()`
on the captured stdout *before* the subsequent `if ($LASTEXITCODE -ne 0)` check. If the exec fails
(e.g., transient API-server/psql error) it typically writes to stderr and leaves stdout empty, so the
subexpression evaluates to `$null` and `$null.Trim()` throws a terminating error under
`$ErrorActionPreference='Stop'`. That bypasses the intended clean `exit 30` (and, at line 199, the
`exit 40` path), so the script dies with a generic error and exit code 1 instead of the distinct
infra code its own header (EXIT-CODE TABLE) promises. Same pattern at STEP D for `$wfId`.
**Fix:** Capture first, check `$LASTEXITCODE`, then normalize:
```powershell
$raw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA -c "SELECT count(*) FROM processors"
if ($LASTEXITCODE -ne 0) { Write-Phase "processor-row precheck failed..." 'Red'; exit 30 }
$procCount = ("$raw").Trim()
```

### IN-02: No `securityContext` on any workload (runAsNonRoot / drop capabilities / readOnlyRootFilesystem)

**File:** `k8s/10-postgres.yaml`, `k8s/11-redis.yaml`, `k8s/12-rabbitmq.yaml`, `k8s/13-elasticsearch.yaml`, `k8s/20-otel-collector.yaml`, `k8s/21-prometheus.yaml`, `k8s/30-baseapi-service.yaml`, `k8s/31-orchestrator.yaml`, `k8s/32-keeper.yaml`, `k8s/33-processor-sample.yaml`
**Issue:** No pod- or container-level `securityContext` is declared anywhere, so every container runs
with its image default (several as root) and full default capabilities on a writable root filesystem.
Acceptable for a local single-node dev deploy and out of the compose parity scope, but noting it since
the review brief asked about security context. If any of these manifests are ever lifted toward a
shared cluster, this is the first hardening to add.
**Fix (app tiers, when hardening):**
```yaml
securityContext:
  runAsNonRoot: true
  allowPrivilegeEscalation: false
  capabilities: { drop: ["ALL"] }
```
(Infra images like ES/postgres/rabbitmq need their documented uid/fsGroup handling — apply per-image.)

### IN-03: `kustomization.yaml` uses deprecated `commonLabels` (injects immutable selector labels)

**File:** `k8s/kustomization.yaml:35-37`
**Issue:** `commonLabels` is deprecated in kustomize v5 (bundled with recent kubectl) in favor of
`labels:` with an explicit `includeSelectors:` flag; it will emit deprecation warnings on newer
`kubectl apply -k`. More substantively, `commonLabels` injects `app.kubernetes.io/part-of` and
`managed-by` into every workload's `spec.selector.matchLabels` and Service selectors. This is fine on
the first fresh apply (as the comment notes), but selectors are immutable — any later change to these
label values would force a delete/recreate of the StatefulSets/Deployments. Harmless today; a latent
foot-gun.
**Fix:** Prefer the non-deprecated form and keep selector injection intentional:
```yaml
labels:
  - pairs:
      app.kubernetes.io/part-of: skp
      app.kubernetes.io/managed-by: kustomize
    includeSelectors: false
```

### IN-04: Empty catch swallows all errors on the best-effort stop POST

**File:** `scripts/phase-80-harness.ps1:299-300`
**Issue:** The STEP F.6 `POST /orchestration/stop` is wrapped in `try { ... } catch { }` — a fully
empty catch that discards any exception. It is intentional (stop is best-effort before the drain
loop), but an empty catch hides genuinely useful signal (e.g., the forward died) with zero trace.
**Fix:** Log at low severity instead of swallowing:
```powershell
} catch { Write-Phase "  stop POST best-effort failed: $($_.Exception.Message)" 'Yellow' }
```

### IN-05: Port-forward teardown kills by raw PID (recycled-PID hazard)

**File:** `scripts/phase-80-up.ps1:143` (write) / `scripts/phase-80-harness.ps1:372-374` (kill)
**Issue:** PIDs are persisted to `.k8s-portforward-pids` and later `Stop-Process -Id` is called on
them. If a kubectl forward exits and the OS recycles its PID before teardown, `Stop-Process -Force`
could terminate an unrelated process. Low risk on a dev workstation and the kill is guarded with
`SilentlyContinue`, but a name/commandline check before killing would be safer.
**Fix:** Before killing, confirm the process is still the kubectl forward, e.g.
`Get-Process -Id $p | Where-Object { $_.ProcessName -eq 'kubectl' }`.

### IN-06: No CPU requests/limits and no liveness probes on infra tiers (QoS / hang detection)

**File:** `k8s/10-postgres.yaml`, `k8s/11-redis.yaml`, `k8s/12-rabbitmq.yaml`, `k8s/13-elasticsearch.yaml`, `k8s/20-otel-collector.yaml`, `k8s/21-prometheus.yaml`
**Issue:** Only memory requests/limits are set (no CPU), so all pods land in Burstable QoS with
unbounded CPU, and the infra tiers rely solely on readiness/startup probes with no liveness probe —
a wedged-but-not-crashed backing service (e.g., a hung postgres) would not be auto-restarted. Both are
reasonable simplifications for a single-node dev proof and match the compose posture, so noted only
for awareness rather than as a defect.
**Fix (optional):** Add modest CPU requests for predictable scheduling and consider liveness probes
on the stateful backing services if unattended stability becomes a goal.

---

_Reviewed: 2026-07-18_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_
