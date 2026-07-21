---
phase: 70-two-consumer-processor-design-pre-process-post-process-with-
fixed_at: 2026-06-17T00:00:00Z
review_path: .planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-REVIEW.md
iteration: 1
findings_in_scope: 8
fixed: 8
skipped: 0
status: all_fixed
---

# Phase 70: Code Review Fix Report

**Fixed at:** 2026-06-17
**Source review:** `.planning/phases/70-two-consumer-processor-design-pre-process-post-process-with-/70-REVIEW.md`
**Iteration:** 1

**Summary:**
- Findings in scope: 8 (1 Critical + 3 Warning + 4 Info — `fix_scope = all`)
- Fixed: 8
- Skipped: 0

## Per-finding table

| ID | Severity | Status | Disposition |
|----|----------|--------|-------------|
| CR-01 | Critical | fixed (requires human verification) | Per-dispatch seam state moved off the Singleton instance fields into an `AsyncLocal<SeamState>`; Singleton registration + author API unchanged; pipeline clears per-consume; new interleave regression fact added. |
| WR-01 | Warning | fixed | `SpawnToPost` resolves `GetSendEndpoint` inside the bounded `RetryLoop`; the swallow is narrowed to the transient send-fault class (non-transient/programming faults now surface). |
| WR-02 | Warning | fixed | `PostProcessConsumer` provenance guard: a `-post` `DataResult` whose `ProcessorId != context.Id` is dropped (ids-only log). New regression fact. |
| WR-03 | Warning | fixed | Deserialize/unexpected branch emits the sanitized constant `"input deserialization failed"` instead of `ex.Message` (never leak payload bytes); detail kept in the local log. |
| IN-01 | Info | fixed | `OutputTail.SendResult`/`SendKeeper` fold `GetSendEndpoint` into the retried unit (keeper `Guard` parity). |
| IN-02 | Info | fixed | `RetryLoop` XML summary corrected: send-exhaust → broker nack-requeue (no `_error`/D-10). |
| IN-03 | Info | fixed | Dedicated `processor_spawn_dropped` counter added; the spawn-drop hook no longer overloads `DispatchDeduped`. |
| IN-04 | Info | fixed | Jittered-TTL policy hoisted to the shared `L2ProjectionKeys.OutputDataTtl(ttlSeconds)`; `OutputTail` and `InjectConsumer` both call it (no cross-assembly drift). |

## Fixed Issues

### CR-01: Per-dispatch seam state on a Singleton races under concurrent consumes

**Files modified:** `src/BaseProcessor.Core/Processing/BaseProcessor.cs`, `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`, `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs`
**Commits:** `4da9e37` (BaseProcessor + seam fact), `1795b05` (pipeline try/finally wiring)
**Applied fix (preferred Option 1 from the review):** Replaced the eleven mutable instance fields on the Singleton `BaseProcessor` with a private `AsyncLocal<SeamState?>`. `SetSeamState` (identical signature) now publishes a *fresh* `SeamState` object onto the AsyncLocal for the current consume's async flow; `SpawnToPost`/`DeleteEntry`/`NewResult` read it via a `Seam` accessor. Each concurrent consume sees only its own ids/hooks, so the registration stays **Singleton** (the documented contract) and the author-facing API is unchanged — no throughput cap (Option 3's `ConcurrentMessageLimit=1` was *not* needed). Added `ClearSeamState`, which `ProcessorPipeline.RunAsync` calls in a `finally` so a stale captured `EscalateDelete` closure never outlives its dispatch on a pooled thread. New regression fact `ConcurrentConsumes_DoNotBleed_SeamStateIsPerDispatch` interleaves two consumes (different entryId/messageId) through a `Barrier(2)` and asserts each deletes its own entry and stamps its own messageId — it FAILS against the old instance-field design (both flows would observe the last writer) and PASSES with AsyncLocal.
**Human-verify note:** flagged for human verification per the review's logic-bug guidance — AsyncLocal flow-isolation across the author's awaits is a semantic guarantee; the new fact exercises it, but a reviewer should confirm no author code path resumes the seam helpers on a flow that lost the AsyncLocal context.

### WR-01: `SpawnToPost` swallowed every exception type; `GetSendEndpoint` was outside the retry loop

**Files modified:** `src/BaseProcessor.Core/Processing/BaseProcessor.cs`
**Commit:** `4da9e37` (rode in the CR-01 commit — same file; see note below)
**Applied fix:** `GetSendEndpoint` is now resolved INSIDE the `RetryLoop` (mirroring the keeper `Guard` pattern at `InjectConsumer.cs:55` / `ReinjectConsumer.cs:51`), so a transient resolve fault is retried instead of throwing past the author's `return`. The swallow is narrowed via `IsTransientSendFault(sent.Error)`: only the transient transport/Redis family (Redis*Exception, broker/connection exceptions, `IOException`/`TimeoutException`/`OperationCanceledException`/`SocketException`, walking the inner chain) is swallowed (drop hook + scheduler re-fire, preserving best-effort Mode-2 semantics); a deterministic programming/serialization fault now surfaces → `ProcessAsync` faults → broker redelivery, instead of being silently lost on every re-fire.

### WR-02: `-post` accepted an attacker-shaped `DataResult` with no provenance check

**Files modified:** `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs`, `tests/BaseApi.Tests/Processor/PostProcessConsumerFacts.cs`
**Commit:** `0b37851`
**Applied fix:** Added the guard `if (ctx.Message.ProcessorId != context.Id!.Value) { log (ids only) + return; }` before the metric/tail (the project's identity-check equivalent — a legitimate Mode-2 spawn stamps `dr.ProcessorId` via `NewResult`). New regression fact `ForeignProcessorId_IsDropped_NoWrite_NoSend` proves a mismatched id produces no L2 write and no send. The two legitimate facts now pass the `DataResult`'s own `ProcessorId` as the bound processor id so the guard admits them. (Chose the guard over the documentation-only option because it keeps the suite green and closes the forge/poisoning vector the new `-post` ingress opened.)

### WR-03: deserialize-failure `StepFailed` leaked `ex.Message` (possible payload bytes)

**Files modified:** `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`, `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs`
**Commit:** `1795b05`
**Applied fix:** The unexpected/deserialize `catch` now logs `ex` locally (detail retained) and sends `BuildFailed(d, "input deserialization failed")` — a sanitized constant, mirroring `"output failed schema validation"` — so no payload fragment (path/line/token) rides the result wire (T-70-10 discipline). `PrePipelineFacts.SeamThrows_Unexpected_Failed` updated to assert the sanitized message and that the raw text does not leak.

### IN-01: `OutputTail` resolved `GetSendEndpoint` outside the retry loop

**Files modified:** `src/BaseProcessor.Core/Processing/OutputTail.cs`
**Commit:** `4e57cf6`
**Applied fix:** Folded `GetSendEndpoint` into the retried unit in both `SendResult` and `SendKeeper` (keeper `Guard` parity); an exhaust still throws → broker redelivery.

### IN-02: stale `_error`/D-10 comment in `RetryLoop`

**Files modified:** `src/BaseConsole.Core/Resilience/RetryLoop.cs`
**Commit:** `29e3ef2`
**Applied fix:** XML summary now reads "send-exhaust → re-throw → broker nack-requeue (no `_error`, no dead-letter — the retired Phase-53 D-01 model)".

### IN-03: spawn-drop telemetry overloaded `DispatchDeduped`

**Files modified:** `src/BaseProcessor.Core/Observability/ProcessorMetrics.cs`, `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`, `tests/BaseApi.Tests/Processor/ProcessorMetricsFacts.cs`
**Commits:** `3a04b6c` (counter definition + fact), `1795b05` (increment site)
**Applied fix:** Added a dedicated `SpawnDropped` counter (`processor_spawn_dropped`, tagged `ProcessorId`); the pipeline spawn-drop hook increments it instead of `DispatchDeduped`, so the dedup rate stays readable and the spawn-drop rate is observable under its own name.

### IN-04: jittered-TTL policy duplicated across two assemblies

**Files modified:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs`, `src/Keeper/Recovery/InjectConsumer.cs`, `src/BaseProcessor.Core/Processing/OutputTail.cs`
**Commit:** `4e57cf6`
**Applied fix:** Hoisted the `random[ttl, 2×ttl]` policy into the single source of truth `L2ProjectionKeys.OutputDataTtl(ttlSeconds)` (next to `OutputData`, where the SoT comment already claimed TTL is a caller concern). Both `OutputTail.JitteredTtl` and `InjectConsumer.JitteredTtl` now delegate to it; each still supplies its own ttl floor from its own option type, so the policy cannot desynchronize.

## Commit-attribution notes (per-finding atomicity)

Three source files were each touched by more than one finding, so a few commits carry more than one finding's edits (the hunks are non-conflicting and every commit builds + the suite stays green):

- `BaseProcessor.cs` carries **CR-01** (AsyncLocal seam state) and **WR-01** (GetSendEndpoint-in-loop + narrowed swallow). Both landed in commit `4da9e37`; the CR-01 message is the primary subject.
- `ProcessorPipeline.cs` carries **CR-01** (try/finally + `ClearSeamState`), **WR-03** (sanitized message), and **IN-03** (use-site) in commit `1795b05`.
- `OutputTail.cs` carries **IN-01** (send-endpoint-in-loop) and **IN-04** (shared TTL helper) in commit `4e57cf6`.

## SPEC-invariant check

No finding required violating a locked Phase-70 SPEC invariant. All preserved: no `UseMessageRetry`, no error transport, send-exhaust→throw (→ broker nack-requeue), write→send→delete order, OutputData jittered TTL, envelope MessageId override, Post never touches `entryId`. The Singleton author-registration contract is preserved (CR-01 fixed the race without switching to Scoped).

## Gate results (mandatory)

**Build:** `dotnet build SK_P.sln -c Release`

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Hermetic suite:** `dotnet run --project tests/BaseApi.Tests -c Release -- --filter-not-trait "Category=RealStack"`

```
succeeded: 617
failed:    0
skipped:   0
total:     617
```

Suite grew 615 → 617: the two added regression facts
(`ConcurrentConsumes_DoNotBleed_SeamStateIsPerDispatch` for CR-01,
`ForeignProcessorId_IsDropped_NoWrite_NoSend` for WR-02). No assertions were
weakened or deleted. The `rabbitmq://rabbitmq/` connection warnings in the run
log are environmental (the compose hostname is not resolvable from the host) and
are not test failures — the Phase-70 facts are fully hermetic.

---

_Fixed: 2026-06-17_
_Fixer: Claude (gsd-code-fixer)_
_Iteration: 1_
