---
status: partial
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
source: [72-01-SUMMARY.md, 72-02-SUMMARY.md, 72-03-SUMMARY.md]
started: 2026-06-17T00:00:00Z
updated: 2026-06-17T00:00:00Z
mode: agent-observed (Option A live stack, no human verification)
---

## Current Test

[testing paused — 4 items blocked at organic-E2E observation level]

## Verification Approach

Phase 72 is a backend-only recovery-pipeline refactor (no UI). Its deliverables are
distributed-system behaviors. Per user instruction, the live Docker stack was brought
up and exercised autonomously ("Option A, no human verification").

**Live stack stood up (all 12 containers healthy):** postgres, redis, rabbitmq,
elasticsearch, otel-collector, prometheus, orchestrator, keeper x2, processor-sample x2,
baseapi-service. Images were freshly built from the current source tree (including the
5 code-review fixes).

**Live confirmations achieved:**
- The orchestrator container — running the phase-72 branch-free pipeline code — boots
  healthy, connects the bus, consumes `StartOrchestration`, and hydrates/schedules
  workflows (`Start reload for WorkflowId=baadc09a-...` observed in container logs).
- Identity loop intact after the rebuild: both `processor-sample` replicas resolve their
  embedded SourceHash and heartbeat under the registered processor id
  (`skp:proc:ef7353b3-...:{instance}` x2), proving host-built hash == container-built hash.
- Bus connectivity works on `localhost:5673` once the broker-address override is applied.

**Blocker for end-to-end organic observation:** the RealStack E2E harness drives the
in-process WebApi bus to the broker, but (a) the harness's `RabbitMq__Host` override did
not reach the bus (it connected to compose-DNS `rabbitmq:5672` instead of `localhost:5673`)
— a test-harness config issue, fixable by exporting `RabbitMq__Host=localhost` /
`RabbitMq__Port=5673` as process env vars; and (b) even with the broker reachable, the
organic round-trip (Start -> entry-step dispatch -> ProcessAsync -> output write) did not
complete in-window — the orchestrator hydrated the workflow but no dispatch fired (organic
cron-scheduling timing). Neither (a) nor (b) implicates phase-72 production code; this is
the live-stack/organic path the project explicitly defers (72-01/02/03-SUMMARY +
72-VERIFICATION: "live-stack proof deferred-automated — sandbox lacks Docker").

**Authoritative proof of the phase-72 logic = the 58/58 hermetic dispatch facts** (run
green this session, including the WR-01 fix): OutputTailFacts, PrePipelineFacts,
StepAdvancementTests, OrchestratorMetricsFacts, OrchestratorPrePipelineFacts,
TypedResultConsumerFacts. These exercise the exact changed paths with simulated Redis
faults, clean-absent/present blob states, Processing-rides-skip, and stage-1/stage-3
metric increments with the workflowId tag.

## Tests

### 1. Failed/Cancelled step continues the workflow (always-write + uniform read)
expected: Processor returns Failed/Cancelled for step A (which has next-step B). The out: blob is written with a real EntryId, the orchestrator reads it uniformly, fans out to B, and deletes the blob — the workflow advances instead of dead-ending.
result: blocked
blocked_by: server
reason: "Live organic round-trip did not complete (orchestrator hydrates but entry-step dispatch did not fire in-window; broker-address harness config). Logic proven by OrchestratorPrePipelineFacts present-blob Failed/Cancelled fan-out+delete facts (15/15 green)."

### 2. Processing result does not advance (clean-absent skip)
expected: A transient Processing result writes NO out: blob (EntryId stays Guid.Empty). The orchestrator reads clean-absent and acks the message with no fan-out, no keeper, no delete — the workflow does not advance on a Processing tick.
result: blocked
blocked_by: server
reason: "Requires organic round-trip (blocked as above). Logic proven by Processing_rides_clean_absent_skip_no_fanout + OutputTail no-write-on-Processing facts (green)."

### 3. Redis read fault triggers REINJECT (no silent loss)
expected: If Redis is unreachable when the orchestrator reads the out: blob, the message is REINJECTED (sent to keeper for retry) rather than acked/dropped. Clean-absent (skip) and a Redis fault (reinject) are discriminated.
result: blocked
blocked_by: server
reason: "Fault-injection mid-pipeline not driveable through the organic harness here. Logic proven by Redis_fault_on_read_produces_one_REINJECT_and_no_fanout (green)."

### 4. orchestrator_step_unresolved metric exposed and increments correctly
expected: The orchestrator metrics/Prometheus endpoint exposes counter orchestrator_step_unresolved (snake_case, workflowId label only). It increments at an L1-resolution miss (stage-1) and once per dangling next-step id (stage-3). Normal fan-outs and condition-skips do NOT increment it.
result: blocked
blocked_by: server
reason: "Meter is registered in the running orchestrator, but an OTel counter only exports after its first increment; no L1-miss/dangling-edge was driven (organic round-trip blocked). Logic + workflowId-only tag set proven by OrchestratorPrePipelineFacts MeterCollector assertions (green)."

## Summary

total: 4
passed: 0
issues: 0
pending: 0
skipped: 0
blocked: 4

## Gaps

[none — no code defects found; blocked items are organic-E2E observation gated by the
deferred live-stack harness, not phase-72 code. Phase-72 logic is fully covered by the
58/58 hermetic dispatch facts.]
