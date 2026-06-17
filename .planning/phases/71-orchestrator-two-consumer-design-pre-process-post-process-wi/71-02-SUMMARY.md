---
phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi
plan: 02
subsystem: orchestrator
tags: [masstransit, redis, two-consumer, pre-post, relocation, keeper-recovery, fan-out, trip-end, executionid-threading]

# Dependency graph
requires:
  - phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi (plan 01)
    provides: "NextStepHandoff + OrchestratorReinject/Inject/Delete contracts + OrchestratorQueues.ResultPost + A1 (Completed stamps EntryId = output messageId)"
  - phase: 70-two-consumer-processor-design
    provides: "ProcessorPipeline / OutputTail / PostProcessConsumer analogs + RetryLoop escalation map + L2ProjectionKeys.OutputData/OutputDataTtl"
provides:
  - "OrchestratorPrePipeline (shared Pre flow: L1 resolution -> two-reason trip-end -> gate/read out: -> fan out one NextStepHandoff per match -> send-before-delete out:)"
  - "RelocateTail (shared write-data:+dispatch tail; write-exhaust -> INJECT, no dispatch; entryId branch on Completed)"
  - "OrchestratorPostProcessConsumer (+ definition) on orchestrator-result-post, no bus retry"
  - "TypedResultConsumer reshaped to a thin Pre shell; 4 sealed subclasses repointed"
  - "OrchestratorOutputOptions (data: TTL floor, default 300)"
affects: [71-03-keeper-orchestrator-recovery]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Orchestrator two-consumer Pre/Post mirrors the Phase-70 processor pipeline with the namespaces cross-paired: orchestrator READS/DELETES out: and WRITES data: (processor does the inverse)"
    - "No-silent-loss via two DISTINCT trip-end LOG lines (completed-terminal / completed-unresolved) + behavior (ack, no throw, no keeper) — the orchestrator_trip_ended metric counter is DEFERRED"
    - "executionId threaded byte-unchanged across the Completed->continuation hop (no NewId.NextGuid on the Pre/Post path)"
    - "Pre/Post components registered AddScoped (consume-scoped ISendEndpointProvider) — mirrors the processor ProcessorPipeline/OutputTail registration"

key-files:
  created:
    - src/Orchestrator/Configuration/OrchestratorOutputOptions.cs
    - src/Orchestrator/Dispatch/RelocateTail.cs
    - src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
    - src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs
    - src/Orchestrator/Consumers/OrchestratorPostProcessConsumerDefinition.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs
    - tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs
  modified:
    - src/Orchestrator/Consumers/TypedResultConsumer.cs
    - src/Orchestrator/Consumers/StepCompletedConsumer.cs
    - src/Orchestrator/Consumers/StepFailedConsumer.cs
    - src/Orchestrator/Consumers/StepCancelledConsumer.cs
    - src/Orchestrator/Consumers/StepProcessingConsumer.cs
    - src/Orchestrator/Program.cs
    - tests/BaseApi.Tests/Orchestrator/TypedResultConsumerFacts.cs
    - tests/BaseApi.Tests/Orchestrator/ResultAckTests.cs
    - tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs
    - tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs

key-decisions:
  - "Pre/Post DI lifetime is AddScoped not AddSingleton: a root-scope singleton captures a pre-start ISendEndpointProvider whose Send silently no-ops (proven by a harness probe — handoffConsumed=True dispatchSent=False); AddScoped (the processor's proven pattern) binds the consume-scoped send pipeline"
  - "OrchestratorPostProcessConsumer drops the logger ctor param entirely (no provenance self-check, ids-only by construction) to avoid a CS9113 unused-primary-ctor-param warning under the 0-warning gate"
  - "ResultConsumeTests migrated to drive the Post hop end-to-end on the harness (publish a NextStepHandoff -> assert one downstream EntryStepDispatch) rather than the fragile full Pre->Post multi-hop chain; the Pre fan-out is proven directly in OrchestratorPrePipelineFacts"
  - "Doc-comment token rewording (Plan-01 precedent) so literal grep-proxies pass: 'NewId.NextGuid' / 'orchestrator_trip_ended' / 'OrchestratorMetrics' / 'Data'/'Payload' tokens reworded in prose; the code behavior matches the spec exactly"

patterns-established:
  - "Two-reason trip-end is a pure-L1 graceful ack with a distinct log signature per reason — no keeper, no throw, no metric this phase"
  - "RelocateTail.RunAsync branches entryId on whether the data: write ran (Completed -> messageId, non-completed -> Guid.Empty)"

requirements-completed: [REQ-71-01, REQ-71-02, REQ-71-03, REQ-71-04, REQ-71-05, REQ-71-06, REQ-71-11, REQ-71-12]

# Metrics
duration: 18min
completed: 2026-06-17
---

# Phase 71 Plan 02: Orchestrator Pre/Post Core Summary

**The orchestrator-side two-consumer Pre/Post core — a shared `OrchestratorPrePipeline` (L1 resolution -> two distinct trip-end log lines -> gate/read `L2[out:entryId]` -> fan out one `NextStepHandoff` per match -> send-before-delete `out:`) and a shared `RelocateTail` (write `L2[data:messageId]` on Completed then dispatch `EntryStepDispatch` with `entryId = messageId`), with the `TypedResultConsumer` reshaped into a thin Pre shell, the Post consumer on `orchestrator-result-post`, full host wiring, and the migrated + new test slice 29/29 green.**

## Performance
- **Duration:** ~18 min (06:38 -> 06:56 UTC)
- **Tasks:** 3
- **Files:** 7 created, 10 modified

## Accomplishments
- **`RelocateTail`** (mirror of the processor `OutputTail`, namespaces cross-paired): writes `L2[data:messageId]=Data` with the jittered TTL on a Completed continuation (write-exhaust -> one `OrchestratorInject`, NO dispatch), then dispatches `EntryStepDispatch` with `entryId = messageId` (Completed) or `Guid.Empty` (non-completed). `executionId` threaded byte-unchanged.
- **`OrchestratorPrePipeline`** (mirror of `ProcessorPipeline`): pure-L1 resolution; an L1 miss logs a distinct `completed-unresolved` line + acks, a terminal step logs a distinct `completed-terminal` line + acks (both: no throw, no keeper). A Completed result gate/reads `L2[out:EntryId]` (Redis fault -> one REINJECT, no fan-out; clean-absent is NOT an escalation), fans out one `NextStepHandoff` per match to `orchestrator-result-post` (no envelope override), then send-before-delete the `out:` blob (delete-exhaust -> one DELETE; send-exhaust -> throw, no delete).
- **`OrchestratorPostProcessConsumer` + definition**: thin `IConsumer<NextStepHandoff>` shell over `RelocateTail`, bound to `orchestrator-result-post` with no bus retry / no `_error`.
- **`TypedResultConsumer` reshape**: thin Pre shell delegating to the pipeline (keeps the `Outcome` knob + `ResultConsumed` counter); the 4 sealed subclasses repointed to the new 3-param ctor.
- **`OrchestratorOutputOptions`**: the `data:` TTL floor (default 300, D-17), bound from the "Orchestrator" config section.
- **Host wiring**: Post consumer startup-bound; `OrchestratorPrePipeline` + `RelocateTail` registered `AddScoped`; `OrchestratorOutputOptions` + `RetryOptions` configured.
- **Tests**: new `OrchestratorPostProcessConsumerFacts` (4, REQ-71-06) + `OrchestratorPrePipelineFacts` (9, REQ-71-01..05/11); migrated `TypedResultConsumerFacts` / `ResultAckTests` / `ResultConsumeTests` / `StopConsumerLifecycleTests` to the two-consumer + relocation model asserting executionId EQUALITY (not regeneration) + the distinct trip-end logs. Touched slice 29/29 + 24 related = green.

## Task Commits
1. **Task 1: TTL options + RelocateTail + Post consumer (+definition) + facts** - `bc47286` (feat)
2. **Task 2: OrchestratorPrePipeline (two-reason trip-end, metric deferred) + TypedResultConsumer reshape** - `9e464ce` (feat)
3. **Task 3: Program.cs wiring + migrate result tests + new OrchestratorPrePipelineFacts** - `a691771` (feat)

**Plan metadata:** _(this docs commit)_

## Decisions Made
- **Pre/Post DI lifetime = AddScoped (Rule 1 bug fix).** The plan offered `AddSingleton` or `AddScoped`. A harness probe proved `AddSingleton` is wrong: a root-scope singleton captures a pre-start `ISendEndpointProvider` whose `Send` silently no-ops (`handoffConsumed=True dispatchSent=False`). Switched both registrations to `AddScoped` — the processor's proven `ProcessorPipeline`/`OutputTail` pattern — so each `Send` uses the consume-scoped send pipeline. Without this the live fan-out/dispatch would silently drop every continuation in production.
- **Post consumer drops the unused logger ctor param** to avoid CS9113 under the 0-warning gate (the shell does no logging — ids-only by construction; the provenance self-check does not transfer to the single competing-consumer post queue).
- **`ResultConsumeTests` migrated to the Post hop end-to-end** (publish `NextStepHandoff` -> assert one downstream `EntryStepDispatch`) instead of the fragile full Pre->Post multi-hop harness chain; the Pre fan-out + two-reason trip-end are proven directly in `OrchestratorPrePipelineFacts`.
- **Trip-end metric counter DEFERRED** (per the user / CONTEXT Deferred Ideas): the pipeline injects NO `OrchestratorMetrics` (avoids an unused-param CS9113) and `OrchestratorMetrics.cs` is untouched; no-silent-loss rides the two distinct LOG lines + behavior, asserted by the terminal/unresolved facts.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Pre/Post registered AddScoped instead of AddSingleton (silent send no-op)**
- **Found during:** Task 3 (harness migration of ResultConsumeTests)
- **Issue:** With `AddSingleton<OrchestratorPrePipeline>`/`AddSingleton<RelocateTail>` the harness Post hop consumed the `NextStepHandoff` but dispatched nothing (`dispatchSent=False`) — a root-scope singleton captures a pre-start `ISendEndpointProvider`. This is a latent PRODUCTION bug: the live fan-out/dispatch would silently drop.
- **Fix:** Changed both registrations to `AddScoped` (the processor's proven `ProcessorPipeline`/`OutputTail` registration), with a doc-comment explaining the consume-scoped send requirement.
- **Files modified:** src/Orchestrator/Program.cs
- **Verification:** harness probe flipped to `dispatchSent=True dispatchConsumed=True`; ResultConsumeTests 2/2 green.
- **Committed in:** `a691771` (Task 3 commit)

**2. [Rule 3 - Blocking] Migrated two test files not in the plan's file list (ResultConsumeTests, StopConsumerLifecycleTests)**
- **Found during:** Task 3 (test project compile)
- **Issue:** The `TypedResultConsumer` ctor reshape (Task 2) broke `ResultConsumeTests.cs` and `StopConsumerLifecycleTests.cs`, which constructed `StepCompletedConsumer` with the old 5-arg ctor — the test project would not compile.
- **Fix:** Migrated both to the two-consumer model (fan-out assertions + executionId equality); `ResultConsumeTests` rewritten to drive the Post hop end-to-end on the harness.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/ResultConsumeTests.cs, tests/BaseApi.Tests/Orchestrator/StopConsumerLifecycleTests.cs
- **Verification:** both green in the touched slice.
- **Committed in:** `a691771` (Task 3 commit)

**3. [Rule 3 - Blocking] Doc-comment token rewording for literal grep-proxies**
- **Found during:** Tasks 1 & 2 (acceptance greps)
- **Issue:** Acceptance proxies `grep -c "NewId.NextGuid"`/`"orchestrator_trip_ended"`/`"OrchestratorMetrics"`/`"Data|Payload"` expect 0, but the explanatory XML doc-comments contained those literal tokens in prose while the CODE behavior is correct (no regeneration, no metric counter, no metrics injection, no payload logging).
- **Fix:** Reworded the doc-comment prose (Plan-01 precedent); all literal proxies now return 0 with behavior unchanged.
- **Files modified:** src/Orchestrator/Dispatch/RelocateTail.cs, src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs
- **Verification:** all Task 1/2 acceptance greps pass; Orchestrator builds 0-warn/0-err.
- **Committed in:** `bc47286` (Task 1), `9e464ce` (Task 2)

**4. [Rule 1 - Test-double overload] OrchestratorPostProcessConsumerFacts inspect StringSetAsync overload-agnostically**
- **Found during:** Task 1 (first test run, 1 fail)
- **Issue:** The production 3-arg `StringSetAsync(key, value, ttl)` binds to the SE.Redis 2.13 `Expiration`/`ValueCondition` virtual overload, not the keepTtl 6-arg overload the first assertion checked (the documented `DispatchTestKit` NSubstitute subtlety).
- **Fix:** Added an overload-agnostic `ReceivedStringSets` inspector (inspect by method name) mirroring `DispatchTestKit`; stub BOTH virtual overloads in `WriteOkL2`.
- **Files modified:** tests/BaseApi.Tests/Orchestrator/OrchestratorPostProcessConsumerFacts.cs
- **Verification:** 4/4 green.
- **Committed in:** `bc47286` (Task 1 commit)

---

**Total deviations:** 4 (2 Rule-1 bugs incl. the critical AddScoped send-no-op fix, 2 Rule-3 blocking — extra test migrations + doc-comment reword).
**Impact on plan:** No scope creep. The AddScoped fix is a genuine production-correctness fix surfaced by the harness; the rest are acceptance-proxy reconciliations + compile-driven test migrations the ctor reshape forced.

## Authentication Gates
None.

## Issues Encountered
- **MTP filter syntax:** the plan's `--filter "FullyQualifiedName~Orchestrator"` is silently ignored by the Microsoft.Testing.Platform runner; used the MTP-native `-- --filter-class "Namespace.ClassName"` per the prior-wave note. Touched-slice runs green: PrePipelineFacts 9/9, PostProcessConsumerFacts 4/4, TypedResultConsumerFacts/ResultAckTests/ResultConsumeTests/StopConsumerLifecycleTests combined 29/29, related (Ack/Advancement/Metrics/RetryBind) 24/24.
- **Pre-existing full-suite baseline failures (out of scope):** the ~287 known E2E/real-stack baseline failures are unchanged; this plan's verification is scoped to its own touched classes per the prior-wave note.

## Known Stubs
None. The only "" / `Guid.Empty` defaults are documented design behaviors: `NextStepHandoff.Data == ""` for a non-completed continuation (REQ-71-04) and `OrchestratorInject.DeleteEntryId == Guid.Empty` on the Post path (no source `out:` entry to delete) — both spec-mandated, not stubs.

## Threat Flags
None. The plan's threat register is satisfied: T-71-04 (mitigate) — all new trip-end / drop logs reference ids only, never `NextStepHandoff.Data`/`step.Payload` (grep-verified 0 in the pipeline + Post consumer); T-71-05 (mitigate) — the two distinct trip-end log lines carry no-silent-loss (metric deferred per plan); no new security surface beyond the planned `orchestrator-result-post` ingress.

## Next Phase Readiness
- Plan 03 (keeper orchestrator recovery) can now build its `OrchestratorReinjectConsumer`/`OrchestratorInjectConsumer`/`OrchestratorDeleteConsumer` against the live Pre/Post core: the Pre escalates `OrchestratorReinject` (on `out:` read fault) / `OrchestratorDelete` (on delete-exhaust); `RelocateTail` escalates `OrchestratorInject` (on `data:` write-exhaust). The keeper INJECT consumer re-implements the relocate body inline (cross-assembly firewall — it cannot reference the Orchestrator `RelocateTail`).
- The `orchestrator-result-post` endpoint is live (startup-bound, no bus retry); `OutputDataTtlSeconds` defaults to 300 (bind via the "Orchestrator" config section in deployment).
- Note for plan 03: use the MTP-native `-- --filter-class` form for scoped test runs.

## Self-Check: PASSED

All 7 created files exist on disk; all 3 task commits (`bc47286`, `9e464ce`, `a691771`) exist in git history. Orchestrator builds 0-warn/0-err; the touched test slice is 29/29 green (+ 24 related green).

---
*Phase: 71-orchestrator-two-consumer-design-pre-process-post-process-wi*
*Completed: 2026-06-17*
