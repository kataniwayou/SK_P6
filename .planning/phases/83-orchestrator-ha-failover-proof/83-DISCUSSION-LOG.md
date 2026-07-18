# Phase 83: Orchestrator HA failover proof — Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-07-18
**Phase:** 83-orchestrator-ha-failover-proof
**Areas discussed:** Harness shape, Kill mechanism, Duplicate-trigger invariant, Proof gate, Tick keying, Recovery bound

---

## Harness shape

| Option | Description | Selected |
|--------|-------------|----------|
| New dedicated phase-83 script | Standalone `phase-83-ha-failover.ps1` reusing phase-80-harness primitives + leader-kill sequencer + HA-specific proof | ✓ |
| Add as TEST-08 to the sweep | Fold into phase-81-sweep.ps1 as an 8th scenario | |
| Minimal standalone proof | Thin scale/kill/assert script, no reuse of seed/start/observe pipeline | |

**User's choice:** New dedicated phase-83 script.
**Notes:** The fault class (kill leader) and invariant (per-tick fire-uniqueness) differ fundamentally from the 7 processor/keeper execution-completeness scenarios; folding in would force the HA invariant through the wrong model.

---

## Kill mechanism

| Option | Description | Selected |
|--------|-------------|----------|
| Force-kill (SIGKILL) — the real proof | `kubectl delete pod <leader> --grace-period=0 --force`; survivor waits out LeaseDuration expiry (~15s) | ✓ |
| Both graceful + force-kill | Two variants: SIGTERM lease-release (fast) + force-kill (expiry) | |
| Graceful delete (SIGTERM) | Plain delete; recovery faster than LeaseDuration, does not exercise the bound | |

**User's choice:** Force-kill (SIGKILL).
**Notes:** Only the force-kill/lease-expiry path exercises "bounded recovery ~LeaseDuration" — the crash path HA-07 targets. Leader targeted deterministically via Lease `holderIdentity`.

---

## Duplicate-trigger invariant

| Option | Description | Selected |
|--------|-------------|----------|
| New fire-level per-tick uniqueness check | Group entry-step SEND records by 30s cron tick; assert ≤1 distinct correlationId per tick (0 = gap skip) | ✓ |
| Reuse existing analyzer StartedRuns count | Assert StartedRuns == leader-up ticks, no extras | |
| Discuss more before deciding | — | |

**User's choice:** New fire-level per-tick uniqueness check.
**Notes:** The existing analyzer is execution-scoped (`(corr,exec)` completeness), cannot express per-tick fire uniqueness. Followers mint+log but produce no send, so only send-producing correlationIds are counted.

---

## Proof gate

| Option | Description | Selected |
|--------|-------------|----------|
| Fully automated dotnet-test verdict (exit 0/1/2) | Analyzer/assert test checking all five HA-07 claims; mirrors phase-81 exit-code model | ✓ |
| Automated core + operator-observed recovery timing | Automate dup/single-leader/role-flip; recovery timing operator-inspected | |
| Discuss the recovery-time threshold | — | |

**User's choice:** Fully automated dotnet-test verdict (exit 0/1/2).
**Notes:** Strongest, most repeatable; harness kills-then-waits, then runs the verdict.

---

## Tick keying

| Option | Description | Selected |
|--------|-------------|----------|
| Bucket send-records by 30s wall-clock boundary | Wall-clock-aligned 30s buckets of send-record @timestamp; no product-code change | ✓ |
| Add ScheduledFireTimeUtc to the fire log | Log Quartz scheduled tick as a structured attribute (product-code change) | |
| Discuss keying robustness first | — | |

**User's choice:** Bucket send-records by 30s wall-clock boundary.
**Notes:** Keeps the phase product-change-free; gap-skip vs miss distinguished structurally (contiguous empty buckets, no backfill).

---

## Recovery bound

| Option | Description | Selected |
|--------|-------------|----------|
| ES role-flip timestamp, ≤ 2×LeaseDuration (30s) | Kill wall-time → first follower→leader role transition in ES; pass ≤ 30s | ✓ |
| Lease acquireTime delta, ≤ LeaseDuration+margin | kubectl Lease `.spec.acquireTime` delta | |
| Both, cross-checked | ES role-flip AND Lease acquireTime, both under threshold | |

**User's choice:** ES role-flip timestamp, ≤ 2×LeaseDuration (30s).
**Notes:** Single source of truth; also satisfies claim #5 (role transition visible). Lease acquireTime cross-check deferred.

## Claude's Discretion

- Observation-window length + kill timing (grounded: kill after ≥1 clean leader fire; window spans pre-kill/gap/post-recovery).
- Harness step numbering, ES query shape, verdict report JSON schema, close/roll-up wiring.

## Deferred Ideas

- Graceful-delete (SIGTERM) failover variant.
- Lease-acquireTime cross-check as a second recovery-time source.
- Logged `ScheduledFireTimeUtc` for skew-proof keying.
