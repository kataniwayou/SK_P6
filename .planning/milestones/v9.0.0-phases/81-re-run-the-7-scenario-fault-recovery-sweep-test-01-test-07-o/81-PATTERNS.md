# Phase 81: k8s Fault-Recovery Sweep (TEST-01..07 via kubectl scale) - Pattern Map

**Mapped:** 2026-07-18
**Files analyzed:** 3 (2 MODIFY in place, 1 CREATE)
**Analogs found:** 3 / 3 (all exact or near-exact — this phase is a re-target, not greenfield)

## File Classification

| New/Modified File | Op | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|-----|------|-----------|----------------|---------------|
| `scripts/phase-80-harness.ps1` | MODIFY | harness / orchestration script | request-response + event-driven (crash sequencer) | `scripts/phase-67-harness.ps1` | exact (compose sibling; already the k8s port of this file's happy path) |
| `scripts/phase-81-sweep.ps1` | CREATE | sweep driver / roll-up | batch (per-scenario child process + tabulate) | `scripts/phase-68-sweep.ps1` | exact (direct port; only the child-harness name + labels change) |
| `scripts/phase-80-reset.ps1` | MODIFY | reset / clean-state utility | CRUD (destructive infra ops via kubectl-exec) | itself (STEP 1-3 kubectl-exec pattern) + `phase-67 Get-ProcQueueDepth` (mgmt-API) | role-match (new drain STEP added, mirroring existing exec steps) |

**Key insight:** All three targets already have a proven analog in-repo. `phase-80-harness.ps1` is *already* the k8s port of `phase-67-harness.ps1` (happy path only); this phase back-fills the scenario table + crash sequencer that `phase-67` carries but `phase-80` dropped. `phase-81-sweep.ps1` is a near-verbatim copy of `phase-68-sweep.ps1`. The one genuinely new behavior is the rabbitmq drain in the reset (D-04).

---

## Pattern Assignments

### `scripts/phase-80-harness.ps1` (MODIFY — add `-ScenarioId`, scenario table, crash sequencer)

**Analog:** `scripts/phase-67-harness.ps1` (the compose original that already has all three)
**Base to preserve:** `scripts/phase-80-harness.ps1`'s own STEP A0/A/A2/B/B1/C/D/E/F/H structure and exit-code table (do NOT rewrite — graft onto it).

#### Transformation 1 — signature: hardcoded id → `-ScenarioId` param

Current `phase-80-harness.ps1:73` and `:96`:
```powershell
param([switch]$TearDownCluster)
...
$scenarioId = 'TEST-01'
```
Port the `phase-67-harness.ps1:67` param + the `phase-80` switch, keeping TEST-01 as default so Phase 80's proof stays reproducible (SPEC req 1). Recommended merged signature:
```powershell
param(
    [string]$ScenarioId = 'TEST-01',
    [switch]$TearDownCluster
)
```

#### Transformation 2 — scenario table (port `phase-67-harness.ps1:95-119`, capstone rows only)

`phase-67-harness.ps1:95-102` (the 7 capstone rows — carry VERBATIM; drop the TEST-08/09/10 + FALSIFY rows at :103-108, out of scope per SPEC / D-08):
```powershell
$Scenarios = [ordered]@{
    'TEST-01' = @{ targetContainers = @();                   faultType = 'none';       injectAfterNFires = 0; dwellSeconds = 0;  notes = 'no-fault baseline' }
    'TEST-02' = @{ targetContainers = @('processor-sample'); faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'processor whole-tier crash' }
    'TEST-03' = @{ targetContainers = @('orchestrator');     faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'orchestrator crash — RAMJobStore re-hydration from L2 parent index' }
    'TEST-04' = @{ targetContainers = @('keeper');           faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'keeper whole-tier crash (BOTH replicas)' }
    'TEST-05' = @{ targetContainers = @('redis');            faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis crash — L2 wipe (no PVC)' }
    'TEST-06' = @{ targetContainers = @('rabbitmq');         faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'rabbitmq crash — durable queues survive (PVC)' }
    'TEST-07' = @{ targetContainers = @('redis','rabbitmq'); faultType = 'stop-start'; injectAfterNFires = 4; dwellSeconds = 45; notes = 'redis + rabbitmq combined crash' }
}
```
Only `faultType ∈ {none, stop-start}` is in scope (SPEC req 1). `targetContainers` names double as the tier keys for the D-05 maps below — they already match the k8s workload names (`app=<tier>` labels in `k8s/*.yaml`).

#### Transformation 3 — bad-id guard (port `phase-67-harness.ps1:114-119` VERBATIM)

```powershell
if (-not $Scenarios.Contains($ScenarioId)) {
    Write-Phase "unknown scenario '$ScenarioId'. Known: $($Scenarios.Keys -join ', ')" 'Red'
    exit 64
}
$scenario = $Scenarios[$ScenarioId]
Write-Phase "scenario '$ScenarioId' — $($scenario.notes) (faultType=$($scenario.faultType), N=$($scenario.injectAfterNFires), dwell=$($scenario.dwellSeconds)s)"
```
Exit code **64** for a bad id is the config-usage code (SPEC req 1 acceptance: "distinct code"; matches the phase-67/68 exit-code table). Add `64` to the harness header exit-code table (currently `phase-80-harness.ps1:37-46` stops at 50).

#### Transformation 4 — observe-loop + crash sequencer (the core add)

`phase-80-harness.ps1` currently short-circuits at STEP F.2 (`:273-277`, no-fault only). Insert the branch structure from `phase-67-harness.ps1:407-518` **but drop the negative-path forks** (`inject-recovery-*`, `stop-only`, `stop-on-inflight`, `stopstart-on-inflight`) — only `none` and `stop-start` remain. The retained observe-loop is `phase-67-harness.ps1:437-449`:
```powershell
Write-Phase "STEP F.2: observe-loop — waiting for N=$($scenario.injectAfterNFires) fires before inject"
$reachedN = $false
while ((Get-Date) -lt $windowDeadline) {
    $observed = (Get-FireCount) - $fireBaseline
    if ($observed -ge $scenario.injectAfterNFires) { $reachedN = $true; break }
    Start-Sleep -Seconds 5
}
if (-not $reachedN) {
    Write-Phase "baseline never reached N=$($scenario.injectAfterNFires) fires before window close. Aborting." 'Red'; exit 60
}
```
Note: `phase-80-harness.ps1:260` sets `$windowSeconds = 300` but does NOT compute `$windowDeadline` (the no-fault path doesn't need it). The crash branch DOES — add `$windowDeadline = (Get-Date).AddSeconds($windowSeconds)` alongside the `$fireBaseline` read (~`:256`), mirroring `phase-67-harness.ps1:337`.

**The crash sequencer itself — this is the Pitfall-1 re-target (SPEC req 2/3/4).** Compose original `phase-67-harness.ps1:451-514`:
```powershell
# STEP F.3 — crash
foreach ($svc in $scenario.targetContainers) {
    docker compose stop $svc | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "docker compose stop $svc failed." 'Red'; exit 60 }
}
Start-Sleep -Seconds $scenario.dwellSeconds
foreach ($svc in $scenario.targetContainers) {
    docker compose start $svc | Out-Null
    if ($LASTEXITCODE -ne 0) { Write-Phase "docker compose start $svc failed." 'Red'; exit 60 }
}
# STEP F.4 — NDJSON per-replica health-wait (bounded 90s)
foreach ($svc in $scenario.targetContainers) {
    $svcDeadline = (Get-Date).AddSeconds(90)
    $svcHealthy = $false
    do {
        $instances = @(docker compose ps $svc --format json 2>$null | Where-Object { $_ -match '\S' } | ForEach-Object { $_ | ConvertFrom-Json })
        if ($instances.Count -gt 0) {
            $unhealthy = @($instances | Where-Object { $_.Health -ne 'healthy' })
            if ($unhealthy.Count -eq 0) { $svcHealthy = $true }
        }
        if (-not $svcHealthy) {
            if ((Get-Date) -ge $svcDeadline) { Write-Phase "..." 'Red'; exit 60 }
            Start-Sleep -Seconds 2
        }
    } while (-not $svcHealthy)
}
# All crashed tiers confirmed healthy — record the recovery instant.
$recoveryUtc = [DateTimeOffset]::UtcNow
```

**Re-target mapping (SPEC req 2/3/4 — the load-bearing transformation):**

| Compose op | k8s replacement | Source of truth |
|-----------|-----------------|-----------------|
| `docker compose stop <svc>` | `kubectl -n skp scale <kind>/<tier> --replicas=0` | kind from D-05 map below |
| poll `docker compose ps` for gone | poll `kubectl -n skp get pods -l app=<tier> --field-selector=status.phase=Running -o name` until **0** lines (SPEC req 4: "0 running pods") | `phase-80-reset.ps1:54-55` already uses this exact pod-running query |
| `docker compose start <svc>` | `kubectl -n skp scale <kind>/<tier> --replicas=<original>` | count from D-05 map below (NEVER a blanket 1 — SPEC req 3) |
| `docker compose ps … Health=healthy` wait | Deployments: `kubectl -n skp rollout status deployment/<tier> --timeout=120s`; StatefulSets: pod-ready poll (below) | `phase-80-harness.ps1:185` already uses `rollout status` for orchestrator |
| `$recoveryUtc = [DateTimeOffset]::UtcNow` | UNCHANGED — pin only AFTER the readiness gate clears (SPEC req 4) | `phase-67-harness.ps1:512` |

The bounded-poll idiom to reuse for the termination wait is already in this repo at `phase-80-harness.ps1:149-156` (the A2 bootstrap `$bootDeadline = (Get-Date).AddSeconds(120); while ((Get-Date) -lt $bootDeadline){...}`) and `phase-80-reset.ps1:85-95` (the heal-wait). The pod-running probe to invert (wait for **0**) is `phase-80-reset.ps1:54-55`:
```powershell
$pgPods = @(kubectl -n skp get pods -l app=postgres --field-selector=status.phase=Running -o name 2>$null |
    Where-Object { $_ -match '\S' })
```
→ for termination: loop until `@(kubectl -n skp get pods -l app=<tier> --field-selector=status.phase=Running -o name | Where-Object { $_ -match '\S' }).Count -eq 0`.

StatefulSet readiness (redis/rabbitmq) has no `rollout status` equivalent that's as clean as a Deployment's — use a pod-ready poll. The readiness condition query:
```powershell
# Ready when replicas Ready == desired for the StatefulSet
$ready = (kubectl -n skp get statefulset/<tier> -o jsonpath='{.status.readyReplicas}')
# or per-pod: kubectl -n skp get pods -l app=<tier> -o jsonpath='{.items[*].status.conditions[?(@.type=="Ready")].status}'
```
`kubectl -n skp rollout status statefulset/<tier> --timeout=120s` also works for StatefulSets (kubectl supports it) and is the simplest uniform choice — planner discretion (D-05).

#### Transformation 5 — TEST-07 dual-tier crash

`scenario.targetContainers = @('redis','rabbitmq')` — the `foreach ($svc in ...)` loops already handle the multi-target case: crash both → **single** shared `Start-Sleep $dwellSeconds` → restore both → readiness-gate both. This is exactly how `phase-67-harness.ps1:456-510` structures it (crash loop, one dwell, restore loop, wait loop). Preserve that ordering.

#### D-05 static maps to author (from `k8s/*.yaml`, verified below)

```powershell
# tier → workload kind (verified from k8s/*.yaml)
$TierKind = @{
    'processor-sample' = 'deployment'
    'orchestrator'     = 'deployment'
    'keeper'           = 'deployment'
    'redis'            = 'statefulset'
    'rabbitmq'         = 'statefulset'
}
# tier → Phase-80 replica count (restore target — SPEC req 3; NEVER a blanket 1)
$TierReplicas = @{
    'processor-sample' = 2
    'orchestrator'     = 1
    'keeper'           = 2
    'redis'            = 1
    'rabbitmq'         = 1
}
```
**Verified against manifests:** `k8s/33-processor-sample.yaml:33,41` Deployment replicas:2 · `k8s/31-orchestrator.yaml:27,35` Deployment replicas:1 (strategy: Recreate) · `k8s/32-keeper.yaml:28,35` Deployment replicas:2 · `k8s/11-redis.yaml:28,37` StatefulSet replicas:1 · `k8s/12-rabbitmq.yaml:40,49` StatefulSet replicas:1. All pods carry the `app=<tier>` label (matchLabels in each spec).

**Preserve (do NOT re-target) in `phase-80-harness.ps1`:** STEP A0/A/A2/B/B1/C/D/E and the entire STEP F.1/F.5/F.6/H/Z blocks are already k8s-correct and reused verbatim. `RECOVERY_UTC` env plumbing at STEP H (`:336`) already handles the non-null fault-run case. `Get-FireCount`/`Get-PromSum` (`:240-254`) are reused as-is.

---

### `scripts/phase-81-sweep.ps1` (CREATE — k8s capstone sweep)

**Analog:** `scripts/phase-68-sweep.ps1` (near-verbatim copy; 4 concrete edits).

**Port the whole file.** The only changes:

1. **Child harness name** — `phase-68-sweep.ps1:82`:
```powershell
& pwsh -File (Join-Path $PSScriptRoot 'phase-67-harness.ps1') -ScenarioId $id
```
→ change to `'phase-80-harness.ps1'` (the generalized k8s harness).

2. **Labels/prefix** — `phase-68-sweep.ps1:60-61` `Write-Phase` prefix `[phase-68-sweep]` → `[phase-81-sweep]`; summary artifact path `:122` `'phase-68-summary.json'` → `'phase-81-summary.json'`.

3. **D-03 ONE bring-up per sweep** — this is the ONE structural divergence from `phase-68-sweep.ps1` (which relied on the compose harness force-recreating every scenario). Per D-03, the sweep brings the k8s stack up ONCE before the loop, then loops the 7 scenarios sharing that stack. Planner has discretion on the seam (D-03):
   - **Option A (sweep-owned bring-up):** sweep calls `phase-80-build.ps1` + `phase-80-up.ps1` once before the `foreach`, then invokes the harness in a "scenario-only" mode that skips STEP A0/A. Requires a harness `-SkipBringUp` switch (or similar) that jumps to STEP A2/B.
   - **Option B (idempotent harness self-guard):** harness STEP A0/A become no-ops when the stack is already up + reachable (probe `localhost:8080/health/ready` or `kubectl get pods`); sweep just loops the harness unchanged.
   Either is acceptable; the SourceHash-currency guarantee (Phase-80 D-05) must still hold for the FIRST bring-up. The between-scenario clean window is already the harness STEP B/B1 contract (reset + orchestrator rollout-restart) — no new sweep logic needed there.

4. **The classification body is byte-identical** — the exit-code table, `Resolve-SweepClass`, the report-discovery + tabulation (`phase-68-sweep.ps1:88-110`), the roll-up (`:113-132`) all port verbatim. It already reads the analyzer report JSON fields (`Verdict`, `Missing`, `Duplicates`, `StartedRuns`, `CompleteRuns`) with NO re-scoring (SPEC req 5: analyzer/`PassFailEngine` reused byte-for-byte).

**Port VERBATIM (SPEC req 5/6):**
- `param([string[]]$ScenarioIds = @('TEST-01'..'TEST-07'))` (`phase-68-sweep.ps1:48-50`) — default = the 7 capstone ids (NOT TEST-08/09/10/FALSIFY — SPEC acceptance: those are NOT run).
- The dot-source `. (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')` (`:67`) — same shared lib the harness uses; gives `Resolve-SweepClass` (0 PASS / 1 VERDICT_FAIL / 2 INCONCLUSIVE / 64 BAD_ARG / else INFRA_ABORT).
- Run-all + collect, **NO fail-fast** (`:76-111`): every scenario runs even if an earlier one is non-PASS. Final exit 0 IFF all selected PASS (`:126-132`). INCONCLUSIVE (exit 2) is re-runnable, not auto-retried (SPEC req 6, matches D-04 flake policy).

The child-process seam is the key mechanic (`phase-68-sweep.ps1:79-83`): the harness runs as a separate `pwsh -File` process so its `exit N` surfaces as `$LASTEXITCODE` WITHOUT terminating the sweep loop. Preserve this exactly.

---

### `scripts/phase-80-reset.ps1` (MODIFY — add bounded idempotent rabbitmq drain, D-04)

**Analog:** its OWN STEP 1/3 kubectl-exec pattern (`phase-80-reset.ps1:68-73`, `:112-121`) for the exec shape; `phase-67-harness.ps1:323-330` (`Get-ProcQueueDepth`) for the mgmt-API alternative.

**Why (D-04):** rabbitmq has a PVC (`k8s/12-rabbitmq.yaml:97-104`, D-11) so its durable queues + mnesia survive BOTH a scale-0 crash AND a re-apply. The current reset FLUSHALLs redis + graph-DELETEs postgres but leaves rabbitmq untouched — in a shared-stack sweep (esp. after TEST-06/07) stale messages bleed into the next scenario. This is the ONE genuinely new reset behavior in the phase.

**Existing kubectl-exec pattern to mirror** (`phase-80-reset.ps1:68-73`):
```powershell
Write-Phase 'STEP 1: redis-cli FLUSHALL (wiping the dev Redis keyspace)...'
kubectl -n skp exec statefulset/redis -- redis-cli FLUSHALL | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Phase "redis-cli FLUSHALL failed (exit $LASTEXITCODE). Aborting." 'Red'
    exit 2
}
Write-Phase '  FLUSHALL ok.' 'Gray'
```

**New STEP (insert, e.g. STEP 3b after the graph DELETE) — recommended `rabbitmqctl` enumerate-then-purge (planner discretion, D-04):**
```powershell
# STEP 3b — rabbitmq drain (D-04): purge every durable queue so stale messages do not bleed
# across scenarios in the shared-stack sweep. Bounded + idempotent: purging an empty/absent
# queue must NOT abort the reset. Queue names are DYNAMIC (processor dispatch queues are
# '{ProcessorId:D}' + '{ProcessorId:D}-post'; orchestrator has static 'orchestrator',
# 'orchestrator-result-post', etc.) — so ENUMERATE rather than hardcode.
Write-Phase 'STEP 3b: rabbitmq drain — purge all durable queues (idempotent)...'
$queues = @(kubectl -n skp exec statefulset/rabbitmq -- rabbitmqctl list_queues -q name 2>$null |
    Where-Object { $_ -match '\S' })
foreach ($q in $queues) {
    # purge_queue on an absent/empty queue is a no-op success; a genuine failure is logged
    # but does NOT abort (idempotent, fail-soft per-queue) — the whole-reset invariant is the
    # keyspace/graph wipe; a transient purge miss is re-run-tolerant.
    kubectl -n skp exec statefulset/rabbitmq -- rabbitmqctl purge_queue $q 2>$null | Out-Null
}
Write-Phase "  drained $($queues.Count) queue(s)." 'Gray'
```
**Idempotency contract (D-04):** a purge of an empty/absent queue must not abort — so this STEP does NOT `exit 2` on a per-queue purge miss (unlike STEP 1/3 which DO fail-loud on the primary wipe). If the planner prefers fail-loud on the *enumeration* call (list_queues) that is reasonable; the per-queue purge must stay fail-soft.

**Alternative — management API** (mirrors `phase-67-harness.ps1:323-330`, uses the port-forwarded mgmt port `localhost:15673`, guest/guest): `GET /api/queues/%2F` to list, `DELETE /api/queues/%2F/<name>/contents` to purge each. Either mechanism is acceptable (D-04). The `rabbitmqctl` kubectl-exec form is preferred for consistency with the reset's existing exec pattern and to avoid depending on the port-forward being up during a bare reset.

**Preserve:** STEP 0/1/2/3/4/5 unchanged; the new drain slots in without touching the FK-safe DELETE order or the heal-wait. Update the header `.DESCRIPTION` step list (`phase-80-reset.ps1:12-28`) to mention the new drain.

---

## Shared Patterns

### kubectl-exec into a workload (the Pitfall-1 re-target idiom)
**Source:** `scripts/phase-80-reset.ps1:69,91,113,135`
**Apply to:** the harness crash sequencer (scale + poll) and the reset drain.
```powershell
kubectl -n skp exec statefulset/<name> -- <cmd>            # exec into a pod of a workload
kubectl -n skp get pods -l app=<tier> --field-selector=status.phase=Running -o name   # running-pod probe
```
Every k8s infra op is `kubectl -n skp …` (namespace-scoped, threat T-80-16). **Zero** `docker`/`docker compose` in the k8s crash path (SPEC req 2 acceptance grep).

### Bounded fail-loud poll
**Source:** `scripts/phase-80-reset.ps1:85-100` (heal-wait), `scripts/phase-80-harness.ps1:149-157` (bootstrap), `scripts/phase-67-harness.ps1:492-509` (health-wait)
**Apply to:** the crash sequencer's terminate-after-scale-0 and Ready-after-restore waits (SPEC req 4).
```powershell
$deadline = (Get-Date).AddSeconds(<budget>)
$ok = $false
while ((Get-Date) -lt $deadline) {
    if (<condition>) { $ok = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $ok) { Write-Phase "<what> did not <converge> in <budget>s. Aborting." 'Red'; exit 60 }
```

### Exit-code discipline (distinct infra codes vs verdict)
**Source:** `scripts/phase-80-harness.ps1:37-46` header table + `scripts/phase-67-harness.ps1:31-44`
**Apply to:** the harness (add `60` for crash inject/recover failure — already used by phase-67; and `64` for bad `-ScenarioId`) and the sweep (`Resolve-SweepClass`).
Crash-path failures use **exit 60** (mirrors phase-67 STEP F). Bad id uses **exit 64**. The analyzer verdict (0/1/2) is NEVER remapped to an infra code.

### Shared exit-code resolution lib (reused verbatim)
**Source:** `scripts/lib/exit-code-resolution.ps1`, dot-sourced at `phase-80-harness.ps1:94`, `phase-68-sweep.ps1:67`
**Apply to:** both harness (already) and the new sweep — gives `Resolve-AnalyzerExitCode` (report Verdict → 0/1/2) and `Resolve-SweepClass` (code → human class). Do NOT re-implement classification inline.

### RECOVERY_UTC gating (SPEC req 4)
**Source:** `scripts/phase-67-harness.ps1:511-513` (fault path), `scripts/phase-80-harness.ps1:237,336` (declare + env plumb)
`$recoveryUtc` stays `$null` for TEST-01 (no-fault). For a crash run it is pinned `[DateTimeOffset]::UtcNow` ONLY after every crashed tier passes its readiness gate — never before. STEP H already emits `$env:RECOVERY_UTC = if ($recoveryUtc) {...} else {''}` unchanged.

---

## No Analog Found

None. Every target has a proven in-repo analog. The single novel element (the rabbitmq drain) has a strong pattern analog in both the reset's own kubectl-exec steps and the phase-67 mgmt-API helper.

---

## Metadata

**Analog search scope:** `scripts/` (harness/sweep/reset family), `k8s/*.yaml` (workload kind + replica maps), `src/**/*.cs` (queue-naming, for the drain enumeration rationale).
**Files scanned:** `phase-67-harness.ps1`, `phase-80-harness.ps1`, `phase-68-sweep.ps1`, `phase-80-reset.ps1`, `k8s/{11-redis,12-rabbitmq,31-orchestrator,32-keeper,33-processor-sample}.yaml`, plus a queue-naming grep across `src/`.
**Pattern extraction date:** 2026-07-18

---

## PATTERN MAPPING COMPLETE
