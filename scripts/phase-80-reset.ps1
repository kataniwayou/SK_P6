<#
.SYNOPSIS
    Phase 80 clean-state reset — the k8s sibling of phase-65-reset.ps1 (Pitfall-1 re-target).

.DESCRIPTION
    Standalone per-run clean-state reset, run WITH THE K8S STACK UP (namespace `skp`). This is a
    faithful kubectl-exec port of scripts/phase-65-reset.ps1: it preserves the FK-safe DELETE order,
    the 60s heal-wait, the static-literal SQL, and every fail-loud `exit 2` VERBATIM — ONLY the
    docker/compose CLI touch-points are re-targeted to `kubectl`, because the docker CLI CANNOT see
    k8s pods (RESEARCH Pitfall 1: a docker-CLI exec into sk-redis would abort "No such container").

    Executes, in order:

      STEP 0  Pre-flight — assert the stack is up (a postgres pod is Running); fail loud otherwise.
      STEP 1  `kubectl -n skp exec statefulset/redis -- redis-cli FLUSHALL` — wipe the dev Redis
              keyspace (skp:data:*, skp:msg:*, skp:proc:*, parent index, ...).
      STEP 2  HEAL-WAIT (D-07) — poll up to a bounded 60s deadline (6x the 10s heartbeat,
              fail-loud) until >=1 PER-INSTANCE liveness key `skp:proc:{procId:D}:{instanceId}`
              reappears (a live processor-sample replica re-wrote it). This is the exact L2
              state the orchestration-start ProcessorLivenessValidator reads — gating on the
              key (not pod readiness) prevents a subsequent seed+Start 422.
      STEP 3  FK-safe psql DELETE of the 6 workflow-graph tables in migration Down() order, in a
              single transaction. PRESERVES processors + config_schemas (schemas) — idempotent,
              NOT deleted (D-06). Static-literal SQL (no interpolated input — threat T-80-15).
      STEP 3b rabbitmq drain (D-04) — enumerate (`rabbitmqctl list_queues`) then purge every durable
              queue (`rabbitmqctl purge_queue`), fail-soft per queue, so stale messages do not bleed
              across scenarios in the shared-stack sweep (rabbitmq has a PVC — queues survive a crash).
      STEP 4  Processor-set assertion — assert >=1 processor-sample pod is Running (expect 2).
      STEP 5  One-line success summary; exit 0.

    Stack stays UP — NO `kubectl delete`, NO PVC/volume drop (D-06).

    NOTE (Pitfall-1 re-target scope): ONLY the infra touch-points (Redis FLUSHALL/scan, Postgres
    psql, pod-running assertions) are re-targeted to kubectl. The HTTP/metrics touch-points the
    harness uses (seeder, POST /start, analyzer, sweep) are UNCHANGED — they reach the cluster over
    localhost ports covered by `kubectl port-forward` (plan 80-09).

.NOTES
    Dev-only destructive tooling. Must never be pointed at a shared/prod DB or Redis.
    `kubectl -n skp` scopes every op to the local dev `skp` namespace (threat T-80-16).
    k8s object names (k8s/10-postgres.yaml, k8s/11-redis.yaml): StatefulSets `postgres` / `redis`;
    pods carry the `app=postgres` / `app=redis` / `app=processor-sample` labels.
#>

$ErrorActionPreference = 'Stop'

function Write-Phase([string]$msg, [string]$color = 'Cyan') {
    Write-Host "[phase-80-reset] $msg" -ForegroundColor $color
}

# ---------------------------------------------------------------------------
# STEP 0 — pre-flight: assert the stack is up (the reset assumes a running stack, D-06).
#   Re-target: the compose `ps postgres --format json` running-check -> `kubectl -n skp get pods
#   -l app=postgres --field-selector=status.phase=Running -o name` (docker CLI can't see k8s pods).
# ---------------------------------------------------------------------------
Write-Phase 'STEP 0: pre-flight — asserting the stack is up (a postgres pod is Running)...'
$pgPods = @(kubectl -n skp get pods -l app=postgres --field-selector=status.phase=Running -o name 2>$null |
    Where-Object { $_ -match '\S' })
if ($pgPods.Count -eq 0) {
    Write-Phase 'postgres is not running — the stack must be UP before reset. Aborting.' 'Red'
    Write-Phase 'Bring the stack up first: pwsh -File scripts/phase-80-up.ps1' 'Red'
    exit 2
}
Write-Phase "  stack is up (postgres has $($pgPods.Count) running pod(s))." 'Gray'

# ---------------------------------------------------------------------------
# STEP 1 — Redis FLUSHALL.
#   Re-target: the compose `sk-redis` FLUSHALL exec -> `kubectl -n skp exec statefulset/redis
#   -- redis-cli FLUSHALL`. Keep the $LASTEXITCODE fail-loud.
# ---------------------------------------------------------------------------
Write-Phase 'STEP 1: redis-cli FLUSHALL (wiping the dev Redis keyspace)...'
kubectl -n skp exec statefulset/redis -- redis-cli FLUSHALL | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Phase "redis-cli FLUSHALL failed (exit $LASTEXITCODE). Aborting." 'Red'
    exit 2
}
Write-Phase '  FLUSHALL ok.' 'Gray'

# ---------------------------------------------------------------------------
# STEP 2 — HEAL-WAIT (D-07): bounded, fail-loud poll for a fresh per-instance liveness key.
#   per-INSTANCE key  skp:proc:{procId:D}:{instanceId}  (SECOND ':' segment after proc:)
#   index SET         skp:proc:{procId:D}               (ONE segment — excluded by the regex)
# Do NOT poll the deprecated flat skp:{procId} key (deleted Phase 61).
#   Re-target: the compose `sk-redis` `redis-cli --scan` exec -> `kubectl -n skp exec statefulset/redis
#   -- redis-cli --scan ...`. The exclusion regex + 60s deadline + `>= 1` condition are VERBATIM.
# ---------------------------------------------------------------------------
Write-Phase 'STEP 2: heal-wait — polling skp:proc:*:* for liveness reconvergence (bounded 60s)...'
$deadline = (Get-Date).AddSeconds(60)   # 6x the 10s heartbeat — generous, fail-loud
$healed = $false
while ((Get-Date) -lt $deadline) {
    # The regex `^skp:proc:[^:]+$` matches the bare index SET (one segment after proc:) and
    # EXCLUDES it; a key with a second ':' segment is the per-instance liveness key written by
    # a live replica.
    $keys = @(kubectl -n skp exec statefulset/redis -- redis-cli --scan --pattern 'skp:proc:*' |
              Where-Object { $_ -notmatch '^skp:proc:[^:]+$' })
    if ($keys.Count -ge 1) { $healed = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $healed) {
    Write-Phase 'Liveness did not reconverge in 60s after FLUSHALL — aborting.' 'Red'
    Write-Phase '  Check `kubectl -n skp logs deployment/processor-sample` — a 422 on a later seed/Start is the symptom of skipping this gate.' 'Red'
    exit 2
}
Write-Phase "  liveness reconverged ($($keys.Count) per-instance key(s) present)." 'Gray'

# ---------------------------------------------------------------------------
# STEP 3 — FK-safe psql DELETE.
#   Re-target: the compose `-T postgres psql` exec -> `kubectl -n skp exec statefulset/postgres
#   -- psql -U postgres -d stepsdb ...`. Single transaction in migration Down() order
#   (20260528074618_InitialCreate.cs:271-288):
#     step_next_steps -> workflow_assignments -> workflow_entry_steps -> assignments -> workflows -> steps
#   PRESERVE processors + config_schemas (schemas) — NOT in the DELETE list (D-06).
#   Statements are STATIC LITERALS carried byte-for-byte (no interpolated input) — see threat T-80-15.
# ---------------------------------------------------------------------------
Write-Phase 'STEP 3: psql DELETE of the 6 workflow-graph tables (FK-safe, single transaction)...'
kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -c "BEGIN;
DELETE FROM step_next_steps; DELETE FROM workflow_assignments; DELETE FROM workflow_entry_steps;
DELETE FROM assignments; DELETE FROM workflows; DELETE FROM steps;
COMMIT;" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Phase "psql DELETE failed (exit $LASTEXITCODE). The transaction rolled back; no rows deleted. Aborting." 'Red'
    exit 2
}
Write-Phase '  graph rows deleted (processors + config_schemas preserved).' 'Gray'

# ---------------------------------------------------------------------------
# STEP 3b — rabbitmq drain (D-04): purge every durable queue so stale messages do not bleed across
# scenarios in the shared-stack sweep. rabbitmq has a PVC (k8s/12-rabbitmq.yaml, Phase-80 D-11) so its
# durable queues + mnesia survive BOTH a scale-0 crash AND a re-apply. Bounded + idempotent: purging an
# empty/absent queue is a no-op success and MUST NOT abort the reset. Queue names are DYNAMIC (processor
# dispatch queues '{ProcessorId:D}' + '{ProcessorId:D}-post'; orchestrator static queues) — ENUMERATE.
# ---------------------------------------------------------------------------
Write-Phase 'STEP 3b: rabbitmq drain — purge all durable queues (idempotent)...'
$queues = @(kubectl -n skp exec statefulset/rabbitmq -- rabbitmqctl list_queues -q name 2>$null |
    Where-Object { $_ -match '\S' })
foreach ($q in $queues) {
    # Per-queue purge is FAIL-SOFT (idempotent): a purge of an empty/absent queue is a no-op success,
    # and a transient per-queue miss is re-run-tolerant — the whole-reset invariant is the keyspace/graph
    # wipe, not any single queue purge. So this does NOT abort (no fail-loud guard) on a per-queue purge
    # miss (unlike STEP 1/3 which fail-loud on the primary wipe).
    kubectl -n skp exec statefulset/rabbitmq -- rabbitmqctl purge_queue $q 2>$null | Out-Null
}
Write-Phase "  drained $($queues.Count) queue(s)." 'Gray'

# ---------------------------------------------------------------------------
# STEP 4 — processor-set assertion.
#   Re-target: the compose `ps processor-sample --format json` check -> `kubectl -n skp get pods
#   -l app=processor-sample ...` (assert >= 1 Running; expect 2 replicas).
#   The compose STEP-4 badconfig orphan removal is OMITTED — no badconfig pod exists in k8s
#   (processor-badconfig excluded, D-04). No `kubectl delete` of any processor-sample pod.
# ---------------------------------------------------------------------------
Write-Phase 'STEP 4: asserting the running processor set == {processor-sample}...'
$sample = @(kubectl -n skp get pods -l app=processor-sample --field-selector=status.phase=Running -o name 2>$null |
    Where-Object { $_ -match '\S' })
if ($sample.Count -eq 0) {
    Write-Phase 'processor-sample has 0 running pods — the processor set is empty. Aborting.' 'Red'
    Write-Phase '  A later seed/Start would 422 on liveness. Bring the stack up: pwsh -File scripts/phase-80-up.ps1' 'Red'
    exit 2
}
Write-Phase "  processor-sample pods present: $($sample.Count) (expected 2)." 'Gray'

# ---------------------------------------------------------------------------
# STEP 5 — success.
# ---------------------------------------------------------------------------
Write-Phase '[phase-80-reset] FLUSHALL ok; liveness reconverged; graph rows deleted (processors+schemas preserved); processor set == {processor-sample}' 'Green'
exit 0
