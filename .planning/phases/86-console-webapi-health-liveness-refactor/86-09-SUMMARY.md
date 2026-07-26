---
phase: 86-console-webapi-health-liveness-refactor
plan: 09
subsystem: verification
tags: [verification-gate, live-proof, rabbitmq-outage, health-probes, restarts-0]

# Dependency graph
requires:
  - phase: 86-05
    provides: baseapi Redis-readiness + hard latch
  - phase: 86-06
    provides: keeper shared watchdog (top-of-tick beat) + retired keeper watchdog
  - phase: 86-07
    provides: processor shared watchdog (unconditional beat) + identity/schema readiness
  - phase: 86-08
    provides: startupProbe → /health/startup on all four k8s deployments
provides:
  - recorded verdict that an infra (broker) outage never crashes/restarts any of the four services
affects: [milestone-gate, v12.0.0]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Live broker-unreachable proof: kubectl set env RabbitMq__Host=…invalid on all four → observe RESTARTS 0"

key-files:
  created: []
  modified: []

key-decisions:
  - "Live proof deployed the four services under a UNIQUE tag (p86-e24cb5a) via kubectl set image — avoids the :local IfNotPresent stale-bits trap (T-86-19)"
  - "Fresh-SourceHash reseed confound resolved by registering the new processor SourceHash row (schemas unchanged → cloned the existing row's schema FKs) rather than running the full RealStack seeder, which would have re-applied manifests and undone the set-image deploy"
  - "Hermetic gate interpreted as '0 phase-86 regressions + all phase-86 tests green'; the 23 residual failures are all pre-existing/environmental (18 dead ComposeYamlFacts for a deleted compose.yaml + 5 infra tests needing live Postgres/ES/broker), documented and left out of scope per user decision"

patterns-established:
  - "Separate liveness from readiness in the proof: under a broker outage /health/live stays 200 (watchdog keeps beating) while /health/ready goes 503 (NotReady) — no kubelet liveness restart"

requirements-completed: [HLTH-01, HLTH-02, HLTH-03, HLTH-04, HLTH-05, HLTH-06, HLTH-07, HLTH-08]

# Metrics
duration: 90min
completed: 2026-07-26
---

# Phase 86 Plan 09: Terminal Verification Gate — Summary

**Headline outcome PROVEN live: with RabbitMQ made unreachable for 4+ minutes, all four services (baseapi/orchestrator/keeper/processor) stayed at `RESTARTS 0` — broker-dependent services went NotReady (`/health/ready` 503) while `/health/live` stayed 200, broker errors were caught+logged, and recovery came only via new pods. The original crash-loop is gone.**

## Task 1 — Full hermetic suite + startupProbe grep

**Hermetic run** (`tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack`):
- **767 total, 23 failed, 0 skipped.**
- **Zero Phase-86 regressions** — every liveness/heartbeat/watchdog/readiness/latch/identity-schema test is green.
- All 23 failures are pre-existing / environmental, none introduced by Phase 86:

| Failing class | Count | Root cause (predates Phase 86) |
|---|---|---|
| `ComposeYamlFacts` | 18 | Dead tests asserting on root `compose.yaml`, **deleted** in commit `6bb1f83` ("remove Compose handling — k8s-only"). Absent at the phase-86 base `87fa2fc`; the test file was untouched by Phase 86. |
| `LogExportTests` | 2 | OTel/observability E2E — needs live collector/infra |
| `SchemasLogsE2ETests` | 1 | Observability E2E — needs live Postgres/ES |
| `LogLevelFilterTests` | 1 | Timed out on `rabbitmq://rabbitmq/` (45s) — needs live broker |
| `ErrorMappingFacts` | 1 | Integration — needs live infra |

Per user decision, the 18 dead `ComposeYamlFacts` are documented as pre-existing debt and left out of Phase 86's scope (removing them would still leave the 5 infra tests, which cannot pass hermetically in a Docker-less sandbox).

**startupProbe grep** (`grep -c startupProbe`): **1 for each** of `k8s/30-baseapi-service.yaml`, `k8s/31-orchestrator.yaml`, `k8s/32-keeper.yaml`, `k8s/33-processor-sample.yaml`. ✅

## Task 2 — Live broker-unreachable proof (Docker-Desktop k8s, namespace `skp`)

**Deploy of new bits (T-86-19 guard):** rebuilt all four images under a unique tag `p86-e24cb5a` (HEAD `e24cb5a`) and `kubectl set image` — no `:local` IfNotPresent stale-bits risk. Confirmed all pods running the new image before the proof.

**Fresh-SourceHash reseed confound:** the rebuilt processor image computes a new SourceHash (`3ee59bf5…`, was `2424735…`) with no registered `processors` row, so `identity-schema-ready` was Unhealthy → NotReady (a rebuild artifact, not a defect). Registered the new SourceHash row (schemas unchanged → cloned the existing row's schema FKs). The processor then resolved identity → Ready, giving a clean 4/4-Ready baseline.

### Baseline (18:45:46Z) — all new bits, all Ready, RESTARTS 0
| Service | READY | RESTARTS | Image |
|---|---|---|---|
| baseapi-service | true | 0 | baseapi-service:p86-e24cb5a |
| orchestrator | true | 0 | orchestrator:p86-e24cb5a |
| keeper (×2) | true | 0 | keeper:p86-e24cb5a |
| processor-sample (×2) | true | 0 | processor-sample:p86-e24cb5a |

### Outage induced 18:46:12Z — `RabbitMq__Host=rabbitmq-unreachable.invalid` on all four

**Final under-outage snapshot (18:50:30Z, T+4m18s)** — outage-generation pods:
| Pod (outage generation) | READY | RESTARTS | Probe results (from logs) |
|---|---|---|---|
| baseapi-service-64bd77b9b6 | **true** | **0** | Ready — bus is soft/Degraded (Postgres+Redis up), never latched |
| keeper-58fb664846 | false | **0** | `/health/live` 200, `/health/ready` 503 |
| orchestrator-9fdfb6569 | false | **0** | `/health/live` 200 (×4), `/health/ready` 503 (×6) |
| processor-sample-58b96cccb7 | false | **0** | `/health/live` 200 (×8), `/health/ready` 503 (×12) |

**Caught+logged infra errors (not crashes)** — keeper log excerpt under outage:
```
BIT transition broadcast failed — will retry next tick
Connection Failed: rabbitmq://rabbitmq-unreachable.invalid/
RabbitMQ.Client.Exceptions.BrokerUnreachableException: None of the specified endpoints were reachable
```

### Recovery — `RabbitMq__Host=rabbitmq` reverted 18:50:43Z
All four rolled out to Ready on new bits, RESTARTS 0 (18:52:00Z). Recovery came via fresh pods booting against the healthy broker — consistent with the "no self-heal" design (a latched-NotReady pod recovers on restart, not by self-healing).

## Verdict

| Gate | Result |
|---|---|
| Full hermetic green (0 phase-86 regressions, all phase-86 tests pass) | **PASS** |
| startupProbe grep = 1 on all four manifests | **PASS** |
| Live proof: RESTARTS 0 under 4-min broker outage, NotReady + logs, recovery on restart | **PASS** |

All eight HLTH requirements (HLTH-01..08) demonstrably satisfied. **Phase 86 goal achieved: an infrastructure outage never crashes the process nor restarts the pod; only a genuinely stalled watchdog loop would.**

## Self-Check: PASSED
- Hermetic: 767 total / 23 failed, all 23 pre-existing/environmental, zero Phase-86 failures (verified by listing the failing classes).
- startupProbe grep: 1 per manifest (verified).
- Live proof: RESTARTS 0 across all four outage-generation pods over 4+ minutes; `/health/live` 200 + `/health/ready` 503 confirmed from pod logs; broker error caught+logged; recovery confirmed.

## Notes for the orchestrator
- **DB side-effect (intentional, benign):** a second `processors` row was registered for the new Phase-86 SourceHash (`3ee59bf5…`, name `sample-proc-p86-e24cb5a`). The old-SourceHash row (`2424735…`) is now orphaned (no pods run old bits) and harmless. This is the correct registration for the deployed bits.
- **Cluster left healthy:** all four services on `p86-e24cb5a`, Ready, RESTARTS 0, `RabbitMq__Host=rabbitmq`.
- **Requirements tracking:** HLTH-01..08 recorded in `requirements-completed` frontmatter; `requirements mark-complete` is a no-op because the tracked `.planning/REQUIREMENTS.md` is still the v11.0.0 file (known milestone-numbering quirk, same as 86-01..08).
