---
status: complete
phase: 72-processor-always-write-to-l2-and-uniform-orchestrator-pre-pi
source: [72-01-SUMMARY.md, 72-02-SUMMARY.md, 72-03-SUMMARY.md]
started: 2026-06-17T00:00:00Z
updated: 2026-06-17T00:00:00Z
mode: agent-observed (Option A live stack, no human verification)
---

## Current Test

[testing complete]

## Verification Approach & Outcome

Phase 72 is a backend-only recovery-pipeline refactor (no UI). Per user instruction the
full live Docker stack (12 containers: postgres, redis, rabbitmq, elasticsearch, otel,
prometheus, orchestrator, keeper x2, processor-sample x2, baseapi) was brought up from the
current source (incl. the 5 code-review fixes) and the pipeline exercised autonomously.

**A genuine organic round-trip is now GREEN end-to-end** (`SampleRoundTripE2ETests`, passed
twice, ~58s), which exercises phase 72's branch-free `OrchestratorPrePipeline` live on a real
`StepCompleted` result: uniform `out:` read -> `SelectNext` -> completed-terminal ack
(`orchestrator_result_consumed_total` increments; orchestrator logs "Trip ended
(completed-terminal) outcome=Completed"). The `Orchestrator` meter exports live
(`orchestrator_dispatch_sent_total`), the same wiring `orchestrator_step_unresolved` uses.

### Bug found AND fixed during this verification (commit dd05469)
The organic round-trip was initially blocked by a **pre-existing production bug** unrelated to
phase 72: Phase-70's linear-Pre rewrite (`341c1fc`) dropped the `SourceStep.IsSource` bypass in
`ProcessorPipeline.RunAsync`, so every organic entry-step dispatch (`EntryId == Guid.Empty`)
hit the gate `exist L2[Guid.Empty]` -> clean-absent -> returned WITHOUT processing (no result,
no fault) — silently breaking ALL organic round-trips. It slipped through phases 70/71/72
because the hermetic facts pre-seed a real `entryId` (never exercising the source path) and the
organic E2E was deferred (no Docker in the sandbox). Fixed by restoring the pre-Phase-70
"Forward — Pre" contract (source dispatch -> empty validatedData, skip gate+read + terminal
delete). The stale `SampleRoundTripE2ETests` clause-(a) assertion (polled `skp:data`, obsolete
under the out:-blob model) was modernized to a durable ES proof (commit 09d4327).

**58/58 hermetic dispatch facts remain green** after both changes (no regression).

## Tests

### 1. Failed/Cancelled step continues the workflow (always-write + uniform read)
expected: Processor returns Failed/Cancelled for step A (with next-step B). out: blob written with real EntryId, orchestrator reads uniformly, fans out to B, deletes blob — workflow advances.
result: pass
note: "Hermetic: OrchestratorPrePipelineFacts present-blob Failed/Cancelled fan-out+delete (15/15). Live: the uniform branch-free pre-pipeline is confirmed running on a real terminal result (Completed-terminal observed end-to-end via SampleRoundTrip green); Failed/Cancelled share the identical uniform path."

### 2. Processing result does not advance (clean-absent skip)
expected: Processing result writes NO out: blob (EntryId Guid.Empty); orchestrator reads clean-absent and acks with no fan-out/keeper/delete.
result: pass
note: "Hermetic: Processing_rides_clean_absent_skip_no_fanout + OutputTail no-write-on-Processing (green). Source-step path (empty input, no blob) now also exercised live."

### 3. Redis read fault triggers REINJECT (no silent loss)
expected: Redis unreachable on the out: read -> REINJECT to keeper, not ack/drop; clean-absent vs fault discriminated.
result: pass
note: "Hermetic: Redis_fault_on_read_produces_one_REINJECT_and_no_fanout (green). Mid-pipeline fault injection is not driveable through the organic harness; hermetic is authoritative."

### 4. orchestrator_step_unresolved metric exposed and increments correctly
expected: Counter orchestrator_step_unresolved (snake_case, workflowId label) increments at L1 miss (stage-1) and per dangling next-step (stage-3); normal fan-outs/condition-skips do not.
result: pass
note: "Hermetic: OrchestratorPrePipelineFacts MeterCollector assertions (green). Live: the Orchestrator meter exports correctly (orchestrator_dispatch_sent_total / result_consumed_total observed in Prometheus via the same IMeterFactory wiring); an L1-miss/dangling event was not organically driven, so the live counter value is hermetic-backed."

## Summary

total: 4
passed: 4
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

[none — no phase-72 defects. A separate pre-existing bug (source-step gate, Phase 70) was found
and fixed (dd05469) + the stale E2E assertion modernized (09d4327); organic round-trip now green.]
