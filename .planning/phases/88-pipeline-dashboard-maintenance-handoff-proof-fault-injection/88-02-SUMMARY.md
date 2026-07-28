---
phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection
plan: 02
subsystem: testing
tags: [kubernetes, kubectl, powershell, fault-injection, resilience, restore-assertion, disc-05, disc-06]

# Dependency graph
requires:
  - phase: 79-falsification-harness-for-the-resilience-sweep
    provides: "the two committed fault seams the k8s mechanism arms — KEEPER_DEFEAT_REINJECT (ReinjectConsumer.cs:104, commit 7ec9169) and PROCESSOR_DEFEAT_READ (ProcessorPipeline.cs:110)"
  - phase: 80-k8s-deployment-and-harness-port
    provides: "the five-step scale/terminate-wait/dwell/restore/readiness sequencer shape (phase-80-harness.ps1:379-421) — its shape only, never its replica map"
  - phase: 83-orchestrator-ha-failover-proof
    provides: "orchestrator replicas: 3 (HA-06), the live value that makes the phase-80 replica map dangerous to copy"
provides:
  - "scripts/lib/phase-88-cluster-ops.ps1 — the DISC-05 runtime-only seam arm/disarm mechanism (roadmap SC 5 deliverable); nothing equivalent existed"
  - "Get-LiveReplicas / Get-LiveImage — live-read capture that replaces the stale phase-80 replica map"
  - "Get-TierPodNames — running pod names = service_instance_id values, the DISC-06 rollout-discontinuity record"
  - "Invoke-TierScaleFault — whole-tier scale fault that restores to the pre-read count and asserts it"
  - "Assert-StackRestored — the three-boolean restore claim set every Phase-88 scenario artifact carries"
  - "the phase-wide failure-code vocabulary 60/61/62/65 (the library returns them; the caller maps them to exit codes)"
affects: [88-03, 88-04, 88-05, 88-06, 88-07, 88-08, 88-09, 88-10, 88-11]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "one kubectl invocation site with -n skp baked in, so a cluster-wide command is impossible by construction rather than by review"
    - "ordinal allow-list resolution that returns the TABLE'S own string, so a workload name on a command line is never the caller's string"
    - "native-command failure captured as data: the invocation site shadows ErrorActionPreference and PSNativeCommandUseErrorActionPreference locally so a non-zero kubectl exit cannot explode inside a Stop-preference harness"
    - "stderr separated from stdout by ErrorRecord type under 2>&1, so a warning line can never corrupt a jsonpath value read"
    - "restore targets are READ, never tabled; the re-read equality is an explicit boolean claim in the result"

key-files:
  created:
    - scripts/lib/phase-88-cluster-ops.ps1
  modified: []

key-decisions:
  - "Every kubectl call goes through ONE helper whose invocation line is `kubectl -n skp @Arguments` — the namespace is unforgeable, and the plan's line-count equality gate is satisfied structurally rather than by discipline"
  - "The helper is named Invoke-Phase88Ctl, deliberately WITHOUT the substring kubectl, so the -n skp line-equality gate stays meaningful instead of being defeated by function-name mentions"
  - "-Tier and -Name are resolved ORDINALLY against the static tables and the table's own string is what reaches the command line; a case variant is rejected rather than passed through"
  - "The seam removal argument is a static table literal ('KEEPER_DEFEAT_REINJECT-'), not \"$Name + '-'\" assembled at call time — the trailing hyphen IS the disarm mechanism, so it is spelled out once where it is reviewable"
  - "Get-Phase88PodResult carries the command's own Ok flag alongside the names; the terminate-wait uses it, because a FAILED pod read also yields zero names and would otherwise start the dwell while the pods were still serving"
  - "A failed terminate-wait issues a best-effort restore to the pre-read count before returning 60, so an aborted sequencer never leaves a tier at zero replicas"
  - "Assert-StackRestored fails closed on an unreadable tier AND on an unstated expectation — an expectation that was never supplied cannot be satisfied, and a hardcoded default would be the same defect as a hardcoded restore"
  - "Neither DISC-05 nor DISC-06 is marked Complete: this plan builds the mechanism, and both requirements assert things about SCENARIOS that do not exist yet"

patterns-established:
  - "Failure codes are returned as data (60/61/62/65); the library never calls exit, so the caller keeps the repo's distinct-exit-code-per-infra-abort discipline"
  - "Result hashtables are always fully populated (every key present, $null where a step was never reached) so a StrictMode caller can read any field on any path"
  - "Mutating paths capture pod identity before AND after, because arming a seam rolls the pods exactly as a scale does"

# This plan's `requirements` frontmatter names DISC-05 and DISC-06. NEITHER is marked complete — see
# the Decisions section. This plan delivers the MECHANISM both requirements depend on; their
# statements are about what every scenario does, and no scenario exists until 88-05..88-08.
requirements-completed: []

# Metrics
duration: 30min
completed: 2026-07-28
---

# Phase 88 Plan 02: Runtime Fault-Injection Library Summary

**A pure dot-sourceable k8s fault library whose restore target is always READ from the live deployment and then re-read and claimed, whose seams are armed and disarmed at runtime with no manifest edit, and whose namespace and workload names cannot be forged by a caller.**

## Performance

- **Duration:** ~30 min
- **Started:** 2026-07-28 (session)
- **Completed:** 2026-07-28
- **Tasks:** 2 of 2
- **Files modified:** 1 (1 created, 0 modified)

## Accomplishments

- **The roadmap SC 5 deliverable now exists as reviewable code.** Before this plan there was no path at all to arm the Phase-79 seams on k8s: both are committed in `src/`, neither appears in any `k8s/` manifest, neither is set on any live pod, and the only two scripts that reference them (`phase-67-harness.ps1`, `phase-79-falsify.ps1`) reach a container through compose-era interpolation and issue zero cluster commands. `Set-Phase88Seam` / `Clear-Phase88Seam` close that gap at runtime, editing nothing.
- **The stale replica map is unreachable from this library, and provably so.** `Invoke-TierScaleFault` reads `spec.replicas` from the live Deployment immediately before the scale, restores to that value, re-reads, and records `ReplicasRestored` as an explicit claim. The literals `TierReplicas` and `--replicas=1` are both asserted absent from the file, so the `orchestrator = 1` hazard that would have quietly taken the HA tier 3 → 1 cannot be reintroduced by a later edit without tripping the gate.
- **The namespace is structural, not procedural.** All eight exported functions funnel through one helper whose single invocation line is `kubectl -n skp @Arguments`. The plan's gate (every line mentioning `kubectl` also carries `-n skp`) passes because there is only one such call site plus the documented command forms in the help.
- **Disarm-in-`finally` is a supported contract, not advice.** `Clear-Phase88Seam` is safe to call when the seam was never armed (removing an absent variable is a no-op rollout), so a caller can invoke it unconditionally from its outer `finally` without tracking whether the arm succeeded — which is what makes an interrupted run still leave a clean stack.
- **Restore is asserted, never assumed.** `Assert-StackRestored` returns three independent booleans read back from the live deployments and a distinct `FailureCode 65`, so a dirty stack can never be read as a verdict. Exercised live read-only: it returned `Ok=True` against the four tiers' own read values, and a deliberately wrong expectation failed closed with 65 and four named mismatches.
- **Pod identity is captured on BOTH mutating paths.** `PodNamesBefore` / `PodNamesAfter` come back from the scale fault and from the seam arm, so DISC-06's `RolloutOldInstanceIds` / `RolloutNewInstanceIds` pair is producible for the rollout the library itself caused — the Pitfall-2 trap (scoring a restart as movement) is now avoidable with recorded evidence rather than by assertion.

## Task Commits

1. **Task 1: Live-read replica capture, pod identity capture, and the scale-fault sequencer** — `4242981` (feat)
2. **Task 2: Seam arm/disarm and the Assert-StackRestored claim set (DISC-05)** — `912835a` (feat)

## Files Created/Modified

- `scripts/lib/phase-88-cluster-ops.ps1` (NEW, 672 lines) — pure dot-sourceable library. Three static allow-list tables (`$Phase88Tiers`, `$Phase88TierKind`, `$Phase88Seams`) plus a fourth static table of seam removal arguments; internal `Resolve-Phase88Tier` / `Resolve-Phase88Seam` / `Invoke-Phase88Ctl` / `Get-Phase88PodResult`; exported `Get-LiveReplicas`, `Get-LiveImage`, `Get-TierPodNames`, `Wait-TierSettled`, `Invoke-TierScaleFault`, `Set-Phase88Seam`, `Clear-Phase88Seam`, `Assert-StackRestored`. No top-level side effect beyond the four tables: no `exit`, no `Push-Location`, no `Write-Host`, no output on dot-source.

## Decisions Made

- **Neither DISC-05 nor DISC-06 is marked Complete.** DISC-05 asserts that "after every scenario the stack is asserted restored"; DISC-06 asserts that "every seam-dependent or scale-dependent scenario re-captures its baseline after the rollout has settled". Both are statements about scenarios, and this plan authors no scenario — the arm path has never been executed against the cluster. The mechanism they depend on now exists; the claims land in 88-05..88-08. This follows 88-01's deviation 3 explicitly: mark only what was actually proven.
- **The `kubectl` wrapper is named `Invoke-Phase88Ctl`.** The obvious name contains the substring `kubectl`, which would have added lines matching the plan's `-n skp` line-equality gate for reasons unrelated to any command being issued — weakening a real control to satisfy a naming habit.
- **`Wait-TierSettled` gates on pod count as well as `rollout status`.** `rollout status` returns as soon as the Deployment reports Available, which on the orchestrator's `RollingUpdate` surge strategy can still leave an old pod terminating. A capture taken at that moment is a capture of a transient, and the whole phase rests on captures taken from settled state.
- **The library survives `$ErrorActionPreference = 'Stop'`.** Every harness in this repo sets it, and PowerShell 7.3+ can make a non-zero *native* exit terminating under that preference — which would have made a library whose entire job is to report failure codes throw inside the caller's `try` instead. The invocation site shadows the two relevant preferences locally (the caller's session is untouched) and returns the exit code as data. Proven by running the live read-only exercise with `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'` set.
- **`Get-LiveImage` is included in the claim set even though no Phase-88 scenario changes an image.** The hazard is a kustomize re-apply having quietly reverted the four app Deployments to the manifests' stale pins, whose bits emit no `source` resource attribute — which makes every correct dashboard look broken. A scenario that ran against a reverted stack would produce a confident, wrong finding, so the images are claimed unchanged alongside the replicas.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing critical functionality] A failed pod read was indistinguishable from a terminated tier**

- **Found during:** Task 1
- **Issue:** The plan's terminate-wait ("block until zero running pods for that tier") is the phase-80 shape, where a failed `get pods` also yields zero names. Taken literally, a transient cluster-read failure during the wait would be read as "the tier is gone", and the dwell would start while the pods were still serving — producing a fault window shorter than the artifact claims, and a wrong answer for the DISC-07 minimum-detectable-duration ladder that is built on top of exactly this sequencer.
- **Fix:** Added the internal `Get-Phase88PodResult`, which carries the command's own `Ok` flag alongside the names. The terminate-wait requires `Ok -and Count -eq 0`; a read that never succeeds simply runs out the 90 s bound and returns FailureCode 60. The public `Get-TierPodNames` keeps the plan's `[string[]]` contract by dropping the flag.
- **Files modified:** `scripts/lib/phase-88-cluster-ops.ps1`
- **Verification:** Task-1 automated gate green; the sequencer's failure path is exercised by the `prometheus` allow-list rejection test (returns before any command).
- **Committed in:** `4242981`

**2. [Rule 2 - Missing critical functionality] A failed terminate-wait would have left the tier at zero replicas**

- **Found during:** Task 1
- **Issue:** On the FailureCode 60 terminate-wait path, the plan returns without restoring — leaving the target tier scaled to zero, which for `keeper` or `orchestrator` means an amputated stack that the next scenario would then measure. The phase's standing hazard is a left-armed seam; a left-scaled-down tier is the same class of poisoning.
- **Fix:** The terminate-wait failure path issues a best-effort restore to the pre-read count before returning 60. Best-effort deliberately: the failure code still reports the abort, and the caller still runs `Assert-StackRestored`, which is the authority on whether the stack is actually clean.
- **Files modified:** `scripts/lib/phase-88-cluster-ops.ps1`
- **Verification:** Task-1 automated gate green (the restore is on an abort path, unreachable without a live scale, which this plan performs none of).
- **Committed in:** `4242981`

**3. [Rule 2 - Missing critical functionality] `-Value` was the one caller string with no allow-list**

- **Found during:** Task 2
- **Issue:** `Set-Phase88Seam -Value` reaches the command line as `<NAME>=<VALUE>`. Tier and seam name are both table-resolved (T-88-01), but `-Value` was specified as a free parameter with a `'1'` default — and `PROCESSOR_DEFEAT_READ` legitimately carries a step label, so it cannot simply be pinned to `1`.
- **Fix:** `-Value` is validated against `^[A-Za-z0-9_.:-]{1,64}$` and rejected with FailureCode 61 and no command issued. That admits `1` and `Step_C` while excluding whitespace, `=`, and every shell/flag metacharacter.
- **Files modified:** `scripts/lib/phase-88-cluster-ops.ps1`
- **Verification:** Task-2 automated gate green; the mismatched-pair rejections (`keeper`/`PROCESSOR_DEFEAT_READ`, `orchestrator`/`KEEPER_DEFEAT_REINJECT`) return 61 with no command, as does an invalid value.
- **Committed in:** `912835a`

**4. [Rule 1 - Bug] The Task-2 commit message claimed `git status --porcelain k8s/` was empty; it is not**

- **Found during:** Task 2 commit
- **Issue:** The plan's verification line is "`git status --porcelain k8s/` is empty — no manifest was edited." It is not empty: `k8s/22-otel-collector-servicemonitor.yaml` is an untracked file that predates this plan (it appears in the session-start working-tree state). The commit message asserted the literal emptiness, which is false.
- **Fix:** Amended the Task-2 commit message to state the accurate claim — no manifest was created, edited, or deleted by this plan, and the single porcelain entry is a pre-existing untracked file left by an earlier phase and untouched here. The underlying property the verification exists to protect (no manifest edit) holds.
- **Files modified:** none (commit message only; `912835a` supersedes the pre-amend `d56b802`)
- **Verification:** `git status --porcelain k8s/` → exactly one line, `?? k8s/22-otel-collector-servicemonitor.yaml`, unchanged from before this plan. `git show --stat 912835a` → one file changed, `scripts/lib/phase-88-cluster-ops.ps1`.
- **Committed in:** `912835a`

---

**Total deviations:** 4 auto-fixed (3 missing critical functionality, 1 bug)
**Impact on plan:** No scope change, no new dependency, no interface removed. Three additions harden paths the plan specified; one corrects a false claim in a commit message. Every added field is additive (`Detail`, `Armed`, `Tier`, `Name`, `Value`, `UnknownTiers`, `CheckedUtc`) — nothing in the `<interfaces>` contract was renamed or dropped.

## Issues Encountered

- **A read-only live exercise was run against the `skp` cluster, and it is worth recording what it proved.** With `Set-StrictMode -Version Latest` and `$ErrorActionPreference = 'Stop'`, the library read all four tiers: `baseapi-service` 1/1 pod, `keeper` 2/2, `orchestrator` 3/3, `processor-sample` 2/2, all four on `:tags-const-1544` — matching the plan's `<interfaces>` restore table exactly, including the `orchestrator = 3` the stale map contradicts. `Assert-StackRestored` returned `Ok=True, SeamVarsClean=True, ReplicasRestored=True, ImagesUnchanged=True` with empty mismatch lists, independently re-confirming the roadmap precondition that **zero seam env vars are present on the live pods**. A negative control with every expected replica count off by seven returned `Ok=False, FailureCode=65` and four named mismatches, so the claim set has teeth. **Zero mutation:** only `get deploy` and `get pods` were issued; nothing was scaled, no env was set, no manifest was read or written. Replica counts and images are exactly as found.
- **The seam ARM path has never been executed.** Everything asserted about `Set-Phase88Seam` beyond its allow-list rejection is static: parse, dot-source, and the pair guard. The first real arm is 88-05's keeper scenario, and it should be treated as a first live use — in particular, the 180 s settle bound for a two-replica keeper rollout is a reasoned bound, not a measured one.

## Known Stubs

None. All eight exported functions are complete implementations; every branch the plan specifies is implemented, including both failure paths of every mutating step. The library is deliberately non-executing at dot-source time — that is the specified design (a pure library), not a deferral.

## Threat Flags

None. This plan introduces no network endpoint, no auth path, and no schema change. Every threat in the plan's register is mitigated and asserted:

- **T-88-01** — `redis`, `prometheus`, `keeper`/`PROCESSOR_DEFEAT_READ` and `orchestrator`/`KEEPER_DEFEAT_REINJECT` are all rejected before any command; the string that reaches the command line is always the table's own.
- **T-88-02** — `TierReplicas` and `--replicas=1` asserted absent; `spec.replicas` asserted present; `ReplicasRestored` is an explicit claim.
- **T-88-03** — `Clear-Phase88Seam` is idempotent and finally-safe; `Assert-StackRestored` matches `DEFEAT|REINJECT_DELAY` on the live env and returns the distinct FailureCode 65.
- **T-88-10** — `apply -k` asserted absent; images are claimed unchanged against pre-read values.
- **T-88-04** — one invocation site, `-n skp` baked in; the line-equality gate passes.
- **T-88-05** — the shared forward PID registry is asserted absent from the file; this library starts no forward.
- **T-88-15** — `KEEPER_REINJECT_DELAY_MS` appears only in the help text explaining why it is unsupported, and no `set env` line references it (asserted).
- **T-88-SC** — no package-manager install occurred and no dependency was added.

## User Setup Required

None. `kubectl` and the `docker-desktop` context are pre-existing; the library performs no bring-up and owns no port-forward.

## Next Phase Readiness

- **Ready for 88-03** (the Wave-0 probe, BLOCKING) — probe A1 (does a whole-tier processor crash produce keeper recovery traffic?) is now one `Invoke-TierScaleFault -Tier 'processor-sample' -DwellSeconds 90` call, with the restore, the settle gate and the `ReplicasRestored` claim already inside it.
- **Ready for 88-05..88-08** (the fault scenarios) — the contract each one owes its artifact is already the library's return shape: `ReplicasBefore` / `ReplicasAfter` / `PodNamesBefore` / `PodNamesAfter` / `FaultStartUtc` / `FaultEndUtc` / `RestoredUtc`, plus `Assert-StackRestored`'s three booleans and its three named mismatch lists.
- **Contract every caller must honour:** `Clear-Phase88Seam` belongs in the OUTER `finally`, unconditionally, and `Assert-StackRestored` must run after it with the values read BEFORE the scenario. A scenario that calls either only on its happy path re-opens the exact hazard this library was built to close.
- **Concern to carry forward:** the 90 s terminate-wait and the 120 s / 180 s settle bounds are reasoned from the phase-80 precedent, not measured on this cluster under seam-arm rollouts. If 88-05 sees a 61 with "did not settle within 180s", the bound is the suspect before the mechanism is.

## Self-Check: PASSED

- `scripts/lib/phase-88-cluster-ops.ps1` — FOUND (parses via `[ScriptBlock]::Create`; dot-sources clean under `Set-StrictMode -Version Latest`; all eight exported functions defined)
- Commit `4242981` — FOUND
- Commit `912835a` — FOUND
- Forbidden-token gate — clean (`TierReplicas`, `apply -k`, `docker compose`, `--replicas=1`, shared-forward PID registry all absent)
- `-n skp` line-equality gate — PASSED (every line mentioning `kubectl` carries `-n skp`)
- No `k8s/` manifest created, edited or deleted by this plan

---
*Phase: 88-pipeline-dashboard-maintenance-handoff-proof-fault-injection*
*Completed: 2026-07-28*
