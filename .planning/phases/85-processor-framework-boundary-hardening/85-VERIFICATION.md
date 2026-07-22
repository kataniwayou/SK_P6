---
phase: 85-processor-framework-boundary-hardening
verified: 2026-07-22T09:30:00Z
status: passed
score: 8/8 must-haves verified
overrides_applied: 0
---

# Phase 85: Processor-Framework Boundary Hardening Verification Report

**Phase Goal:** Harden the processor↔framework boundary so a fan-out hand-off can never silently drop a seeded execution, and entry-lifecycle ownership lives entirely in the base. Three changes: (1) SpawnToPost becomes fail-loud; (2) the pipeline nack-requeues the entry on a spawn send-exhaustion; (3) entry/source deletion becomes framework-owned on every completion path, and the concrete DeleteEntry API + SampleProcessor call + SeamState.EntryId + EscalateDelete hook are removed.
**Verified:** 2026-07-22T09:30:00Z
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | SpawnToPost throws SpawnSendExhaustedException on a TRANSIENT send-exhaustion instead of swallowing it | VERIFIED | `src/BaseProcessor.Core/Processing/BaseProcessor.cs:118-124` — `if (!IsTransientSendFault(sent.Error)) throw sent.Error!;` (deterministic, unchanged) followed by `s.OnSpawnDropped?.Invoke(...)` then `throw new SpawnSendExhaustedException(spawn.ExecutionId, sent.Error!);` |
| 2 | OnSpawnDropped telemetry fires BEFORE the throw (SC-1 ordering) | VERIFIED | Same block — `Invoke` on line 123, `throw` on line 124, textually ordered; confirmed by hermetic fact `BaseProcessorSeamFacts` asserting `Assert.Single(droppedExecIds)` alongside `Assert.ThrowsAsync<SpawnSendExhaustedException>` |
| 3 | A DETERMINISTIC send fault still throws the RAW exception (poison-safety preserved) | VERIFIED | `BaseProcessor.cs:118` `if (!IsTransientSendFault(sent.Error)) throw sent.Error!;` is untouched; new deterministic-negative fact in `BaseProcessorSeamFacts.cs:78` asserts raw `ArgumentException` surfaces, not the dedicated type |
| 4 | The dedicated exception carries the spawn ExecutionId only (ids-only) | VERIFIED | `SpawnSendExhaustedException.cs:11-16` — primary ctor `(Guid executionId, Exception inner)`, single `public Guid ExecutionId` property, message interpolates only the id |
| 5 | A defeated spawn makes RunAsync PROPAGATE SpawnSendExhaustedException (nack) instead of StepFailed+ack | VERIFIED | `ProcessorPipeline.cs:194` `catch (SpawnSendExhaustedException) { throw; }` sits between `catch (ProcessStatusException e)` (ends ~:185) and `catch (Exception ex)` (:195) |
| 6 | A deterministic seam fault still acks as StepFailed (narrow filter does not widen) | VERIFIED | Generic `catch (Exception ex)` at `ProcessorPipeline.cs:195-210` unchanged — routes to `outputTail.RunAsync(unexpectedDr,...)` + `return`; explicit D-03 negative-control fact in `PrePipelineFacts` (part of 36/36 green run) |
| 7 | Entry/source deletion is framework-owned on BOTH completion paths (Mode-1 tail + Mode-2 null-return), keeper DELETE escalation preserved | VERIFIED | `ProcessorPipeline.cs:219-223` (null path calls `DeleteEntryTail`) and `:238-239` (Mode-1 tail calls `DeleteEntryTail`); shared helper at `:254-260` does `KeyDeleteAsync` → on exhaust `SendKeeper(BuildDelete(d))` |
| 8 | DeleteEntry API / SeamState.EntryId / SeamState.EscalateDelete removed; SampleProcessor no longer calls DeleteEntry | VERIFIED | `BaseProcessor.cs` grep confirms no `DeleteEntry`/`EntryId`/`EscalateDelete` symbols remain (only `SeamState.MessageId/WorkflowId/StepId/CorrelationId/ProcessorId/OnSpawnDropped`); `SampleProcessor.cs:57-74` Mode-2 path is spawn-loop + `return null` with no delete call |

**Score:** 8/8 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs` | Dedicated fail-loud spawn-exhaust exception type | VERIFIED | `public sealed class SpawnSendExhaustedException(Guid executionId, Exception inner) : Exception(...)`; derives directly from `System.Exception`, NOT `ProcessStatusException` (confirmed by inspection — the class has no base other than `Exception`) |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs` | SpawnToPost fail-loud throw; SeamState.EntryId/EscalateDelete removed | VERIFIED | Read in full — matches all PB-01 and PB-03 claims |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` | Narrow catch (SpawnSendExhaustedException){throw;}; DeleteEntryTail on both paths | VERIFIED | Read in full — narrow catch at :194, shared `DeleteEntryTail` helper at :254-260 called from both :222 (null path) and :239 (Mode-1 tail) |
| `src/Processor.Sample/SampleProcessor.cs` | DeleteEntry call removed; only spawns + returns null | VERIFIED | Read in full — Mode-2 branch (:57-74) has no delete call |
| `tests/BaseApi.Tests/Processor/BaseProcessorSeamFacts.cs` | Inverted PB-01 fact + deterministic-negative fact; DeleteEntry_* facts removed | VERIFIED | Grep confirms no `DeleteEntryAsync`/`EscalateDelete`/`EntryId`; `SpawnSendExhaustedException` referenced 3x (throw assertion + deterministic-negative comment) |
| `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` | Public SendFaultProvider lifted from BaseProcessorSeamFacts | VERIFIED | `public sealed class SendFaultProvider(Exception? boom = null) : ISendEndpointProvider` at line 397 |
| `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs` | D-05 nack fact + D-03 negative-control fact; non-source null-path delete facts | VERIFIED | 36/36 tests pass (spot-checked directly, see Behavioral Spot-Checks) |
| `scripts/phase-68-sweep.ps1` reference (85-04) | SC-4 terminal sweep gate | VERIFIED (via k8s substitute) | 85-04 SUMMARY documents a deliberate, disclosed substitution: k8s sweep (`phase-80-harness.ps1 -SkipBringUp` looped TEST-01..07) was used instead of the retired docker-compose `phase-68-sweep.ps1`, matching the same TEST-01..07 contract. `analyzer-reports/phase-85-summary.json` independently confirms 7/7 PASS, all `zeroMissing: true`, all `harnessExit: 0` |

### Key Link Verification

| From | To | Via | Status | Details |
|------|-----|-----|--------|---------|
| `BaseProcessor.SpawnToPost` | `SpawnSendExhaustedException` | throw after `OnSpawnDropped` on transient exhaust | WIRED | `BaseProcessor.cs:123-124` — telemetry then throw, textually adjacent |
| `ProcessorPipeline.RunAsync` try/catch fulcrum | MassTransit RabbitMQ nack-requeue | `catch (SpawnSendExhaustedException) { throw; }` | WIRED | `ProcessorPipeline.cs:194`, positioned correctly between the two existing catches |
| `ProcessorPipeline` null-return branch | L2 KeyDelete → KeeperDelete escalation | `DeleteEntryTail` under `!SourceStep.IsSource` guard | WIRED | `ProcessorPipeline.cs:219-223` calls the shared tail; `:256` guards `SourceStep.IsSource(d.EntryId)` skip |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| PB-01 seam facts (fail-loud throw + deterministic-negative) | `BaseApi.Tests.exe --filter-class "*BaseProcessorSeamFacts*"` | 4/4 passed | PASS (matches SUMMARY claim exactly) |
| PB-02 nack-escape + D-03 negative controls | `BaseApi.Tests.exe --filter-class "*PrePipelineFacts*"` | 36/36 passed | PASS (matches SUMMARY claim exactly) |
| PB-03 source-skip net-effect | `BaseApi.Tests.exe --filter-class "*SampleProcessorFacts*"` | 4/4 passed | PASS (matches SUMMARY claim exactly) |
| BaseProcessor.Core builds clean | `dotnet build src/BaseProcessor.Core -c Release` | Build succeeded, 0 warnings, 0 errors | PASS |
| Processor.Sample builds clean | `dotnet build src/Processor.Sample -c Release` | Build succeeded, 0 warnings, 0 errors | PASS |
| No debt markers in modified files | grep TBD/FIXME/XXX/TODO/HACK/PLACEHOLDER across the 4 core-change files | No matches | PASS |

Note: the full `BaseApi.Tests.exe --filter-not-trait Category=RealStack` suite was intentionally NOT run per the task's environmental note (hangs indefinitely retrying the in-cluster `rabbitmq` DNS name on this host, pre-existing and unrelated to PB-01/02/03). The three targeted fact classes above are the complete set of PB-01/02/03-relevant hermetic coverage and were independently re-run and reproduced the SUMMARY's exact pass counts.

### Probe Execution

Not applicable — this phase's terminal integration proof is the k8s 7-scenario sweep (SC-4), not a `scripts/*/tests/probe-*.sh` script. The sweep result was independently cross-checked against `analyzer-reports/phase-85-summary.json`:

| Scenario | Fault | Verdict | Missing | Duplicates | Exit |
|----------|-------|---------|---------|------------|------|
| TEST-01 | no-fault baseline | Pass | 0 | 0 | 0 |
| TEST-02 | processor whole-tier crash | Pass | 0 | 0 | 0 |
| TEST-03 | orchestrator crash | Pass | 0 | 0 | 0 |
| TEST-04 | keeper crash (both replicas) | Pass | 0 | 0 | 0 |
| TEST-05 | redis crash (L2 wipe) | Pass | 0 | 0 | 0 |
| TEST-06 | rabbitmq crash (nack-requeue redelivery) | Pass | 0 | 0 | 0 |
| TEST-07 | redis + rabbitmq combined crash | Pass | 0 | 0 | 0 |

The JSON file's raw field values (`zeroMissing: true`, `effectOnce: true`, `harnessExit: 0` on all 7 rows) independently corroborate the 85-04-SUMMARY.md table — this is not a trust-the-summary situation, the underlying report was read directly.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| PB-01 | 85-01 | SpawnToPost fail-loud (OnSpawnDropped + propagating exception) | SATISFIED | Truths 1-4 verified above |
| PB-02 | 85-02 | Mode-2 entry acked only when every spawn succeeded; spawn exhaust nack-requeues | SATISFIED | Truths 5-6 verified above |
| PB-03 | 85-03 | Entry/source deletion framework-owned on every completion path; concrete DeleteEntry API removed | SATISFIED | Truths 7-8 verified above |

All three requirement IDs (PB-01, PB-02, PB-03) declared across the phase's plans are accounted for and satisfied with direct code evidence. No orphaned requirements found in REQUIREMENTS.md's Phase 85 section.

**Documentation inconsistency (informational, not a code gap):** `.planning/REQUIREMENTS.md` marks PB-01/02/03 as `[x]` complete in the requirements list (lines 17-19), but the Traceability table at the bottom of the same file (lines 50-52) still shows `Not started` for all three. This is a stale-table issue in planning documentation, not a code correctness issue — all code-level evidence above independently confirms the requirements are implemented and tested. Recommend updating the traceability table to `Complete` as part of phase close-out.

### Anti-Patterns Found

None. Scanned all four core changed files (`SpawnSendExhaustedException.cs`, `BaseProcessor.cs`, `ProcessorPipeline.cs`, `SampleProcessor.cs`) for TBD/FIXME/XXX/TODO/HACK/PLACEHOLDER/"not yet implemented" — zero matches. No empty-implementation stubs (`return null`/`=> {}`) introduced by this phase's changes.

### Human Verification Required

None. The phase's one human-verify checkpoint (85-04 Task 3, operator confirmation of the SC-4 sweep) was already executed and recorded as PASSED in 85-04-SUMMARY.md, and independently cross-checked against the raw `analyzer-reports/phase-85-summary.json` roll-up in this verification (all 7 scenarios `zeroMissing: true`/`harnessExit: 0`). No further human action is needed for this phase.

### Gaps Summary

No gaps found. All three requirement IDs (PB-01, PB-02, PB-03) are implemented with direct code evidence (not just SUMMARY claims): the fail-loud exception type exists and derives correctly from `Exception`; `SpawnToPost` fires telemetry then throws on transient exhaust while preserving the deterministic raw-throw; the pipeline's narrow catch sits correctly between the two pre-existing catches and reproduces the nack-vs-ack fulcrum; entry/source deletion was moved into a shared `DeleteEntryTail` helper called from both completion paths, with the `SourceStep.IsSource` guard preserving the Guid.Empty net-effect; and the entire author-facing `DeleteEntry`/`EntryId`/`EscalateDelete` surface was removed from both source and tests. All targeted hermetic fact classes were independently re-run in this verification and reproduced the SUMMARY's exact pass counts (4/4, 36/36, 4/4). The SC-4 live sweep's raw JSON report independently corroborates the claimed 7/7 all-PASS baseline. The only finding is a stale REQUIREMENTS.md traceability table (cosmetic documentation lag, not a code gap).

---

_Verified: 2026-07-22T09:30:00Z_
_Verifier: Claude (gsd-verifier)_
