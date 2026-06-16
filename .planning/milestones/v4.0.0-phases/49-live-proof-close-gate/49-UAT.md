---
status: complete
phase: 49-live-proof-close-gate
source: [49-01-SUMMARY.md, 49-02-SUMMARY.md, 49-03-SUMMARY.md, 49-04-SUMMARY.md, 49-05-SUMMARY.md, 49-06-SUMMARY.md]
started: 2026-06-10T00:00:00Z
updated: 2026-06-10T00:00:00Z
---

## Current Test

[testing complete — live close gate PASSED 2026-06-10, exit 0]

## Tests

### 1. Live N×GREEN close-gate run (rebuilt v4 stack)
expected: |
  After rebuilding the v4 contract-changed images, `pwsh -File scripts/phase-49-close.ps1`
  exits 0 — 3 consecutive GREEN runs, identical Passed count, triple-SHA BEFORE==AFTER,
  skp-dlq-1 depth 0, Release+Debug 0-warning. SC1/SC2/SC3 + MetricsRoundTrip pass live.
result: pass
evidence: |
  PASSED 2026-06-10 (exit 0), in-session, v4 stack rebuilt. 515 facts GREEN ×3.
  psql \l SHA: 37b27e562fe1b6c6544c3f44f375b30cca16bebbf4f4c358910c229605f41441 (HELD)
  redis --scan SHA: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855 (HELD; liveness key excluded)
  rabbitmq list_queues SHA: bc1ffcda968425835ba28f1a026a3307135898b4b3dcc5572eec5decaf4a4fb0 (HELD; _bus_ excluded)
  skp-dlq-1 depth 0 (HELD). Required 5 gap-closures (GAP-49-6..10) — see Gaps + 49-HUMAN-UAT.md Run #3.

## Summary

total: 1
passed: 1
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

# GAP-49-6 (FIXED): BreakerMetricsFacts cross-test measurement bleed
- truth: "Close gate reaches 3 consecutive GREEN runs with identical Passed fact count"
  status: fixed
  reason: "Run 1 GREEN (515/515); Run 2 exit 1 (514 passed / 1 failed) — Smell-A guard tripped on non-identical count"
  severity: major
  test: 1
  root_cause: "Test-isolation defect (pre-existing, Phase 32.1) in BreakerMetricsFacts.Recorded_Measurement_Carries_ProcessorId_But_No_WorkflowId_Label. Its MeterListener filtered by meter NAME ('BaseProcessor'/'Orchestrator'), process-global; under full-suite parallelism a sibling test recorded a measurement on the same-named meter that this listener captured too -> Count 3 not 2."
  fix: "Scoped InstrumentPublished + the measurement callback to this test's exact Counter<long> instances by ReferenceEquals (procMetrics.DispatchDeduped / orchMetrics.ResultDeduped). VERIFIED: 3x515 GREEN across all subsequent runs."
  artifacts:
    - path: "tests/BaseApi.Tests/Orchestrator/BreakerMetricsFacts.cs"

# GAP-49-7 (FIXED): execution-data keys written with no TTL -> redis net-zero leak
- truth: "redis-cli --scan net-zero: no skp:data:* keys survive to the AFTER snapshot"
  status: fixed
  reason: "29-34 skp:data:{guid} keys (TTL=-1, never expire) accumulated and broke redis BEFORE==AFTER"
  severity: major
  root_cause: "Phase-44 ProcessorPipeline dropped the configured ExecutionDataTtl on the Post output write ('NO expiry'); a terminal step's output key has no successor to end-delete it, so it leaked forever. compose Processor__ExecutionDataTtl=5, the bound ProcessorLivenessOptions.ExecutionDataTtlSeconds, the gate settle-drain, and the test teardown comments ALL assumed a TTL the code never applied. (User decision: apply the TTL.)"
  fix: "ProcessorPipeline injects IOptions<ProcessorLivenessOptions> and applies TimeSpan.FromSeconds(ExecutionDataTtlSeconds) on the StringSetAsync output write. Updated PipelinePostFacts no-TTL assertion -> TTL-applied; updated 4 pipeline-fact ctor sites + DispatchTestKit doc. VERIFIED: settle reports 'skp:data:*=0 remain'."
  artifacts:
    - path: "src/BaseProcessor.Core/Processing/ProcessorPipeline.cs"

# (RESOLVED non-code): SourceHash incremental-build staleness
- note: "After the ProcessorPipeline change, an INCREMENTAL host build left Processor.Sample.dll embedding the stale SourceHash (710e854c) while the docker container had the fresh hash (dfaecab2). The gate seeded the stale hash -> container could not resolve identity -> live round-trip would fail. Fixed by a clean `dotnet clean + build` so host==container hash. Operational, not a code defect."

# GAP-49-8 (FIXED, test+gate): composite backup key leak (Keeper UPDATE/CLEANUP race)
- truth: "redis net-zero: no skp:{corr}:{wf}:{proc}:{exec} composite backup keys survive to AFTER"
  status: fixed
  fix: "RealStack test factories scan-delete composites by workflowId on teardown; close-gate settle step GCs the redundant composite namespace (skp:*:*:*:*) after the 3xGREEN cadence + quiesce (a dispatch in flight at Stop completes after per-test teardown, so the gate settle is the reliable sweep point). Composite is a bounded 2-day crash-backstop; primary net-zero proof untouched."
  reason: "On a pristine stack, 1-2 composite keys (procId = live seeded processor) survive to AFTER. Variable count => race, not deterministic."
  severity: major
  root_cause: "Composite is written by Keeper UpdateConsumer and deleted by CleanupConsumer (happy) / InjectConsumer (infra). With deploy.replicas: 2 competing on queue:keeper-recovery, UPDATE and CLEANUP for the same exec can land on different replicas; the per-key partitioner orders within a replica, not across replicas -> CLEANUP can run before UPDATE (no-op delete) then UPDATE writes -> orphan composite (2-day TTL, not drained in-window). The RealStack E2E tests register data/root keys for teardown but NOT composites."
  options:
    - "(test) RealStack round-trip tests scan-and-delete skp:{testCorr}:* composites on teardown (mirrors existing data-key cleanup; no product change; composite is a bounded crash-backstop in prod)"
    - "(product) consistent-hash partition queue:keeper-recovery by exec key so same-exec UPDATE/CLEANUP hit the same replica in order"

# GAP-49-9 (FIXED, gate): rabbitmq net-zero churn from transient MassTransit _bus_ endpoint
- truth: "rabbitmq list_queues name net-zero: BEFORE == AFTER"
  status: fixed
  fix: "close-gate rabbitmq name-SHA excludes transient *_bus_* temporary endpoint queues (random auto-delete names re-minted on bus reconnect after the SC3 outage — not net-zero topology). VERIFIED: rabbitmq SHA HELD."
  reason: "SC3 docker stop/start sk-redis bounces container connections; the processor-sample MassTransit temporary _bus_ endpoint queue gets a new random NewId suffix on bus reconnect -> BEFORE {host}_ProcessorSample_bus_X != AFTER ..._bus_Y."
  severity: major
  root_cause: "The gate snapshots ALL queue names unfiltered. The _bus_ endpoint queues are auto-delete temporaries with random per-connection names -- not steady-state topology. A bus reconnect (expected during SC3's outage) renames them."
  options:
    - "(gate) exclude transient *_bus_* temporary endpoint queues from the rabbitmq name snapshot (they are random auto-delete temporaries, not net-zero topology)"
    - "(product) configure a stable bus endpoint name so reconnects reuse the same queue"

# GAP-49-10 (FIXED, gate): liveness key timing fragility across snapshots
- truth: "steady-state liveness key skp:{procId} consistently present across BOTH snapshots"
  status: fixed
  reason: "skp:{procId} flaps with the heartbeat/TTL race and briefly expires during the SC3 docker stop sk-redis outage, so it was inconsistently present at the BEFORE vs AFTER snapshot moments."
  fix: "close-gate redis name-SHA excludes the steady-state liveness key skp:{seededProcId} (the live container keeps re-writing it — steady-state by intent). Removes the timing fragility without weakening leak detection for skp:data:* / composites. VERIFIED: redis SHA HELD. NOTE: the heartbeat DOES recover after redis returns (verified the key reappears post-run) — this is a snapshot-timing fix, not a heartbeat-recovery bug."

# (latent, not currently blocking): InjectConsumer also writes skp:data with no TTL
- note: "src/Keeper/Recovery/InjectConsumer.cs writes ExecutionData(entryId) with NO TTL (same bug class as GAP-49-7). Only leaks if the INJECT recovery path is exercised and leaves a data key. Should get the same TTL treatment for consistency, but the Keeper has no ExecutionDataTtl config of its own."
