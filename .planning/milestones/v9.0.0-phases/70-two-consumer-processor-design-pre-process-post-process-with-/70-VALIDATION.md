---
phase: 70
slug: two-consumer-processor-design-pre-process-post-process-with
status: ready
nyquist_compliant: true
wave_0_complete: false
created: 2026-06-16
---

# Phase 70 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> Derived from `70-RESEARCH.md` § Validation Architecture. The suite is fully hermetic
> (NSubstitute `IDatabase` doubles + `CapturingSendProvider` + `FakeProcessor`); no live
> stack is used or needed this phase. Container build is SPEC out-of-scope.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xunit.v3 3.2.2 under Microsoft.Testing.Platform (MTP) |
| **Config file** | `tests/BaseApi.Tests/xunit.runner.json` (copied to output — `BaseApi.Tests.csproj:60`) |
| **Quick run command** | MTP exe scoped: `dotnet run --project tests/BaseApi.Tests -- --filter-class "*<Facts>"` (⚠ `dotnet test --filter` is unreliable on MTP — `STATE.md:636`) |
| **Full suite command** | `dotnet test tests/BaseApi.Tests` (or `dotnet test SK_P.sln`) — the req-12 bar |
| **Build gate** | `dotnet build SK_P.sln -c Release` → 0 warn / 0 err (repo standard close signal) |
| **Estimated runtime** | hermetic suite — seconds to low-minutes; no container spin-up |

---

## Sampling Rate

- **After every task commit:** Run scoped MTP `--filter-class "*<Facts under edit>"`
- **After every plan wave:** Run `dotnet test tests/BaseApi.Tests` (whole suite — the only reliable scope on MTP)
- **Before `/gsd-verify-work`:** Full suite green + `dotnet build SK_P.sln -c Release` 0 warn/0 err
- **Max feedback latency:** ~seconds (scoped) / low-minutes (full suite)

---

## Per-Task Verification Map

> Per-task IDs are assigned by the planner. The req→behavior→test rows below come from the
> research § Phase Requirements → Test Map (one fact per acceptance criterion). The planner
> binds each row to a Task ID and plan/wave.

| SPEC req / AC | Behavior | Test Type | Scoped command | File (status) |
|---------------|----------|-----------|----------------|----------------|
| req 1 | Gate-fault → exactly one REINJECT, no Step* | unit | `*PrePipelineFacts` | ❌ W0 rewrite `PipelinePreFacts` |
| req 1 | clean-absent entry → return, no processing | unit | `*PrePipelineFacts` | ❌ W0 |
| req 2 | input-invalid → 1 StepFailed, **no entry delete** | unit | `*PrePipelineFacts` (assert `DidNotReceive().KeyDeleteAsync`) | ❌ W0 |
| req 2 | read-fault → REINJECT | unit | `*PrePipelineFacts` | ❌ W0 |
| req 3 | `ProcessAsync` null → no write/send/delete | unit | `*PrePipelineFacts` | ❌ W0 |
| req 4 | completed → write `OutputData(messageId)` once + StepCompleted + delete entry | unit | `*OutputTailFacts` / `*PrePipelineFacts` | ❌ W0 rewrite `PipelinePostFacts` |
| req 4 | write-exhaust → INJECT (no send/delete) | unit | `*OutputTailFacts` (new write-fault double) | ❌ W0 |
| req 4 | delete-exhaust → DELETE | unit | `*PrePipelineFacts` | ❌ W0 rewrite `PipelineEndDeleteFacts` |
| req 4 | non-completed → skip write, still send + delete | unit | `*OutputTailFacts` | ❌ W0 |
| req 5 | Post: `data=dr.data`, write-on-completed, send, **no entry read/delete** | unit | `*PostProcessConsumerFacts` | ❌ W0 NEW |
| req 5 | Post write-exhaust → INJECT | unit | `*PostProcessConsumerFacts` | ❌ W0 NEW |
| req 6 | REINJECT envelope `MessageId == messageId`; absent→drop | unit | `*ReinjectConsumerFacts` | ✅ rewrite (add MessageId capture) |
| req 7 | INJECT writes `OutputData(messageId)`, deletes entry, sends by result, never recomputes | unit | `*InjectConsumerFacts` | ✅ rewrite |
| req 8 | DELETE single delete, zero sends | unit | `*DeleteConsumerFacts` | ✅ rewrite |
| req 9 | author uses `SpawnToPost`/`DeleteEntry`; spawn swallows, delete escalates | unit | `*BaseProcessorSeamFacts` / `*SpawnDeleteFacts` | ❌ W0 rewrite |
| req 10 | empty execId → 2 Post msgs distinct execIds, Pre does nothing inline; non-empty → 1 completed via tail; both log `"{label} had the following numbers: …"` | unit | `*SampleProcessorFacts` | ✅ rewrite |
| req 11 | no retry/no error on endpoints; send-exhaust throws; jittered TTL | unit | `*DispatchBindSequenceFacts` + `*OutputTailFacts` | ✅ partial / ❌ W0 (TTL) |
| req 12 | full processor suite green; old doc superseded | suite | `dotnet test tests/BaseApi.Tests` | gate |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

Test infra to create/migrate before/with implementation (D-17):

- [ ] **DELETE** `PipelineRecoveryFacts.cs` (no analog — recovery pass removed)
- [ ] **DELETE** slot-allocation facts in `PipelinePostFacts.cs` + `RecoveryPartitionFacts.cs` slot specifics (keep the 4-tuple partition-key pin if `PartitionGuid` survives)
- [ ] **Rewrite** `PipelinePreFacts.cs` → `PrePipelineFacts` (gate `L2[entryId]`, no-delete-on-invalid)
- [ ] **Rewrite** `PipelineForwardFacts.cs` / `PipelineInFacts.cs` / `PipelineEndDeleteFacts.cs` to the linear Pre flow (drop forward/recovery branch assertions)
- [ ] **NEW** `PostProcessConsumerFacts` (req 5)
- [ ] **NEW** `OutputTailFacts` (shared tail — write-gated-on-completed, INJECT escalation, TTL)
- [ ] **Rewrite** `SampleProcessorFacts.cs` for the two-mode `DataResult?` seam (spawn-2 / accumulate-1)
- [ ] **Rewrite** `BaseProcessorSeamFacts.cs` for `Task<DataResult?>` + `SpawnToPost`/`DeleteEntry`
- [ ] **Rewrite** keeper facts: `InjectConsumerFacts` (INJECT→OutputData+DataResult), `DeleteConsumerFacts` (single-key), `ReinjectConsumerFacts` (envelope MessageId)
- [ ] **Extend** `DispatchTestKit`: add an `OutputData(messageId)`-write-fault mux; extend `CapturingSendProvider` to capture the `SendContext.MessageId` set by the override lambda (current capture ignores the context callback — `DispatchTestKit.cs:578` / `RecoveryTestKit.cs:86`); replace `Items(...)`/`FakeProcessor` `List<ProcessItem>` ctors with `DataResult?`-returning shapes
- [ ] Framework install: none (xunit.v3 already wired)

*Existing infra reused as-is:* `FakeProcessorContext`, `RetryLoopFacts`, the `Options`/`Retry`/`Metrics`/`Dispatch` helpers in `DispatchTestKit`, `RecoveryTestKit.Db/Mux/Retry/Metrics`, `FakeRedis`.

---

## Manual-Only Verifications

| Behavior | Requirement | Why Manual | Test Instructions |
|----------|-------------|------------|-------------------|
| Old canonical doc marked superseded | req 12 | Doc-marker edit, not code | Confirm `docs/design/processor-keeper-recovery-spec.md` carries a "superseded by Phase 70" marker pointing at `70-SPEC.md` |

*All processor/keeper behaviors have automated hermetic verification.*

---

## Deliberate Sequencing Note (contracts → consumers → tests)

By design (build-before-teardown, research Pitfall 5), production lands in Waves 1–2 and the
behavioral hermetic facts land in Wave 3 (Plan 70-04). Wave 1 (70-01 contracts) and Wave 2
70-03 (BaseProcessor.Core/Sample) tasks gate on grep + `dotnet build` (structural + compile)
at task time; Wave 2 70-02 (Keeper) tasks are `tdd="true"` and run scoped facts at task time.
The whole-suite `dotnet test tests/BaseApi.Tests` runs at **each wave merge** (Sampling Rate
above), so a regression in the 70-03 pipeline rewrite is caught at the Wave-2→3 merge.

This is an accepted trade-off: pulling a smoke fact into Wave 2 for the Pre/OutputTail core
would require Wave-0 test infra to exist before the seam it tests — which fights the compile
coupling the wave ordering is built to respect. The feedback loop for the most complex code
(70-03) is therefore one wave-merge long, not per-task. Accepted with rationale.

---

## Validation Sign-Off

- [x] All tasks have `<automated>` verify or Wave 0 dependencies (grep/build per task; behavioral facts in Wave 3 + whole-suite at each wave merge)
- [x] Sampling continuity: whole-suite `dotnet test` at every wave merge; no watch-mode gaps (see Deliberate Sequencing Note for the contracts→consumers→tests trade-off)
- [x] Wave 0 covers all MISSING references (migration list above; created in Plan 70-04)
- [x] No watch-mode flags
- [x] Feedback latency acceptable (hermetic suite — seconds scoped / low-minutes full)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-06-16 (plan-checker reconciliation; `wave_0_complete` flips true when Plan 70-04 lands the migrated facts during execution)
