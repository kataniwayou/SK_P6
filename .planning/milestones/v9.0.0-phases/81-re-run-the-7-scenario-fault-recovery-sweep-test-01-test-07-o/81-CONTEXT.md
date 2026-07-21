# Phase 81: k8s Fault-Recovery Sweep (TEST-01..07 via kubectl scale) — Context

**Gathered:** 2026-07-18
**Status:** Ready for planning

<domain>
## Phase Boundary

Re-run the 7-scenario fault-recovery capstone (TEST-01..TEST-07) on the local Kubernetes (Docker Desktop) target and reach **7/7 VERDICT_PASS**, using `kubectl scale --replicas=0` (then scale-back) as the whole-tier crash/recovery injection — the k8s analog of the compose `docker stop`/`start`. Harness/tooling only: NO app source or recovery-architecture change; the analyzer + metric gate are reused verbatim.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**6 requirements are locked.** See `81-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `81-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- Generalize the k8s harness to accept `-ScenarioId` (TEST-01..07) + add the crash sequencer.
- Re-target whole-tier crash injection from docker-compose CLI to `kubectl scale --replicas=0`/restore for all 5 crashable tiers.
- Firm terminate-after-scale-0 and Ready-after-restore waits, gating `RECOVERY_UTC`.
- A k8s capstone sweep driving TEST-01..07, reusing the phase-68 analyzer + metric gate verbatim, emitting a 7/7 roll-up.
- The 7 recovery-capstone scenarios: no-fault baseline + whole-tier crash of processor-sample / orchestrator / keeper / redis / rabbitmq / redis+rabbitmq.

**Out of scope (from SPEC.md):**
- Seam-dependent negative controls TEST-08/09/10 + FALSIFY-01/02 (need the env seams Phase-80 omits, D-08).
- Any change to the analyzer / `PassFailEngine` / metric-gate logic (reused verbatim).
- Any app source / two-consumer recovery-architecture change (harness/tooling only).
- Partial / single-replica crash modes (whole-tier only).
- Multi-node / registry / Helm (inherited Phase-80 out-of-scope).

</spec_lock>

<decisions>
## Implementation Decisions

### Script topology
- **D-01:** **Generalize `scripts/phase-80-harness.ps1` IN PLACE** — add a `-ScenarioId` parameter, the 7-scenario descriptor table (`{ targetContainers, faultType ∈ {none,stop-start}, injectAfterNFires, dwellSeconds }`, mirroring `phase-67-harness.ps1`), and the crash sequencer. `TEST-01` stays the default and its happy-path behavior is unchanged, so Phase 80's proof remains reproducible. This makes `phase-80-harness.ps1` the k8s equivalent of `phase-67-harness.ps1`. Do NOT fork a separate `phase-81-harness.ps1` (avoids ~300 duplicated lines / two harnesses).
- **D-02:** **New deliverable `scripts/phase-81-sweep.ps1`** — the k8s equivalent of `phase-68-sweep.ps1`. Drives TEST-01..07 over the generalized `phase-80-harness.ps1` and rolls up the 7/7 verdict, reusing the phase-68 analyzer (`~Analyze_Window_Yields_Pass`) + `PassFailEngine` metric gate byte-for-byte. Mirror `phase-68-sweep.ps1`'s structure (`-ScenarioIds` default = TEST-01..07, per-scenario child `pwsh` invocation, roll-up + exit-code table).

### Sweep bring-up & cross-scenario state isolation
- **D-03:** **ONE bring-up per sweep, NOT per-scenario.** `phase-81-sweep.ps1` brings the k8s stack up ONCE (build + `phase-80-up.ps1`), then loops the 7 scenarios sharing that stack — between scenarios it re-establishes a clean window via the reset + orchestrator rollout-restart (the existing `phase-80-harness` STEP B/B1 clean-window guarantee). This halves wall-clock vs per-scenario bring-up (k8s bring-up is ~4 min: ES cold-start + rollout; ×7 ≈ 30 min of redundant bring-up) and is safe because images do not change mid-sweep. **Divergence from the compose model** (`phase-67-harness` force-recreates every scenario) — deliberate, justified by k8s bring-up cost. **Planner discretion on the seam:** either the sweep owns bring-up once + invokes a "scenario-only" harness mode, OR the harness self-guards bring-up idempotently (skip STEP A0/A when the stack is already up + reachable). Either is acceptable; the SourceHash-currency guarantee (D-05 Phase 80) must still hold for the first bring-up.
- **D-04:** **Extend `scripts/phase-80-reset.ps1` to ALSO purge rabbitmq.** rabbitmq has a PVC (Phase-80 D-11), so its durable queues/mnesia survive BOTH a scale-0 crash AND a re-apply — the current reset only FLUSHALLs redis + FK-safe graph-DELETEs postgres, leaving rabbitmq untouched. In a shared-stack multi-scenario sweep (esp. after TEST-06 rabbitmq / TEST-07 combined), stale messages could bleed into the next scenario. Add a bounded rabbitmq queue drain (e.g. `kubectl -n skp exec statefulset/rabbitmq -- rabbitmqctl purge_queue <q>` per workflow queue, or a management-API drain) to the reset so cross-scenario isolation is explicit rather than relying on force-recreate side-effects. Keep it fail-loud and idempotent (a purge of an empty/absent queue must not abort the reset).

### Crash injection mechanics
- **D-05:** **Planner's discretion** on the exact scale/wait implementation. Recommended approach (from discussion): a static `tier → kind` map (processor-sample / orchestrator / keeper → `deployment/`; redis / rabbitmq → `statefulset/`); crash = `kubectl -n skp scale <kind>/<tier> --replicas=0`; wait for actual termination (poll `kubectl -n skp get pods -l app=<tier>` until 0 running); dwell `dwellSeconds`; restore = `--replicas=<original>` from a static count map (orchestrator 1, keeper 2, processor-sample 2, redis 1, rabbitmq 1 — never a blanket 1 for the ×2 tiers, per SPEC req 3); wait Ready (`kubectl rollout status deployment/<tier>` for Deployments; a pod-ready poll for StatefulSets); pin `RECOVERY_UTC` only after the readiness gate clears (SPEC req 4). TEST-07 crashes both redis+rabbitmq (crash both → single dwell → restore both).

### Claude's / Planner's Discretion
- Scale-target resolution + termination/readiness poll mechanics (D-05).
- The "bring up once, run scenarios N times" seam (D-03) — sweep-owned bring-up + scenario-only harness mode vs idempotent harness self-guard.
- rabbitmq drain mechanism (`rabbitmqctl purge_queue` vs mgmt-API) and which queues to target (D-04).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements (read FIRST)
- `.planning/phases/81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o/81-SPEC.md` — the 6 locked requirements, boundaries, and acceptance criteria. MUST read before planning.

### The scripts being generalized / extended / created (Phase 80 deliverables)
- `scripts/phase-80-harness.ps1` — the harness to GENERALIZE in place (currently hardcodes `$scenarioId='TEST-01'` at line 96; STEP A0/A/B/B1/C/D/E/F/H structure + exit-code table to preserve). Add `-ScenarioId`, the scenario table, and the crash sequencer.
- `scripts/phase-80-reset.ps1` — the reset to EXTEND with a rabbitmq drain (D-04); currently FLUSHALLs redis + FK-safe graph-DELETEs postgres via `kubectl exec`.
- `scripts/phase-80-up.ps1` — the k8s bring-up the sweep calls ONCE (D-03); apply -k + rollout + rollout-restart + 8 port-forwards + baseapi readiness gate.

### The compose originals to mirror/port
- `scripts/phase-67-harness.ps1` — the compose scenario table (lines ~96-107) + crash sequencer (STEP F.3: `docker compose stop <svc>` → dwell → `docker compose start <svc>` → NDJSON per-replica health-wait) to re-target to `kubectl scale`. The 7-scenario descriptor shape is the template for D-01.
- `scripts/phase-68-sweep.ps1` — the compose sweep driver to mirror as `phase-81-sweep.ps1` (D-02): `-ScenarioIds` default TEST-01..07, per-scenario child `pwsh -File … -ScenarioId <id>`, roll-up + exit-code classification.

### Phase-80 locked context (manifest posture that governs crash fidelity)
- `.planning/phases/80-deploy-full-system-to-local-kubernetes-docker-desktop-port-c/80-CONTEXT.md` — esp. D-11 (rabbitmq StatefulSet +PVC → durable across crash, the D-04 driver here), D-12 (redis StatefulSet no-PVC → L2 wiped on crash), D-14 (port-forward ports the analyzer/reset use), D-08 (the omitted test-only seams that keep the negative controls out of scope).
- `k8s/*.yaml` — the manifests; source of truth for each tier's workload kind (Deployment vs StatefulSet) and replica count (used by D-05's static maps).

### Reused verbatim (do NOT modify)
- The analyzer test `~Analyze_Window_Yields_Pass` + `PassFailEngine` conservation/recovery metric gate (in `tests/BaseApi.Tests`) — reused byte-for-byte per SPEC req 5.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`phase-80-harness.ps1` STEP A0/A/B/B1/C/D/E/F/H** — all reusable; the phase adds only (i) the scenario table + `-ScenarioId`, (ii) the crash sequencer (a k8s STEP F.3), and (iii) the crash-branch of STEP F (observe-to-N-fires then inject) that already exists in `phase-67-harness.ps1` and can be ported.
- **`phase-80-harness.ps1` STEP A2** — the fresh-DB processor-row bootstrap already exists; on a shared-stack sweep it runs once at first bring-up (warm-DB guard skips it thereafter).
- **`phase-68-sweep.ps1`** — its `foreach ($id in $Ids) { & pwsh -File phase-67-harness.ps1 -ScenarioId $id }` + roll-up + exit-code table is directly portable to `phase-81-sweep.ps1`.
- **The analyzer + `PassFailEngine`** — reused verbatim; they already read Prometheus (`localhost:9090`) + ES (`localhost:9200`) over the Phase-80 port-forwards.

### Established Patterns
- **Scenario descriptor shape** `{ targetContainers, faultType, injectAfterNFires, dwellSeconds }` (phase-67-harness lines ~96-107) — carry the 7 capstone rows verbatim; only `faultType ∈ {none, stop-start}` is in scope.
- **Crash sequencer mapping:** compose `docker compose stop <svc>` → `kubectl scale <kind>/<tier> --replicas=0`; `docker compose start <svc>` → `--replicas=<original>`; the NDJSON per-replica health-wait → `kubectl rollout status` / pod-ready poll. This is the 3rd instance of the RESEARCH Pitfall-1 CLI-coupling re-target.
- **Clean-window between scenarios:** `phase-80-reset.ps1` (redis FLUSHALL + postgres graph-DELETE + the new rabbitmq drain) + orchestrator rollout-restart (drops ghost Quartz crons) — already the STEP B/B1 contract.

### Integration Points
- `phase-81-sweep.ps1` reads `localhost:9090` (Prometheus) + `localhost:9200` (ES) via `phase-80-up.ps1`'s 8 port-forwards.
- The reset + wf-id + crash sequencer reach redis / postgres / rabbitmq / workloads via `kubectl -n skp exec` / `kubectl -n skp scale` (no docker CLI in the k8s crash path — SPEC req 2).

</code_context>

<specifics>
## Specific Ideas

- Mirror the compose harness/sweep split exactly: one scenario-capable harness (`phase-80-harness.ps1`, the k8s phase-67) + one sweep driver (`phase-81-sweep.ps1`, the k8s phase-68).
- The rabbitmq drain (D-04) is the one genuinely NEW reset behavior this phase introduces — everything else is a re-target or a driver.

</specifics>

<deferred>
## Deferred Ideas

- **Seam-dependent negative controls on k8s** (TEST-08/09/10 stop-only/in-flight variants + FALSIFY-01/02) — out of scope (SPEC); they require the test-only env seams the Phase-80 manifests deliberately omit (D-08). Running them on k8s first needs seam injection added to the manifests/harness — a separate future phase.
- **Per-scenario full bring-up isolation** — rejected in favor of shared bring-up + reset-between (D-03) for wall-clock; revisit only if cross-scenario state bleed proves un-resettable.

</deferred>

---

*Phase: 81-re-run-the-7-scenario-fault-recovery-sweep-test-01-test-07-o*
*Context gathered: 2026-07-18*
