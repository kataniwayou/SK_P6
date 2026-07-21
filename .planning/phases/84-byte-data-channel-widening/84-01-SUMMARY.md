---
phase: 84-byte-data-channel-widening
plan: 01
subsystem: framework-contract
tags: [byte-array, data-channel, dataresult, processor-seam, schema-validation, refactor]
requires:
  - "StackExchange.Redis RedisValue↔byte[] implicit conversions (write sites transparent)"
  - "System.Text.Json JsonDocument.Parse(ReadOnlyMemory<byte>) UTF-8 overload"
provides:
  - "DataResult.Data : byte[] — the single ground-truth payload spine"
  - "D-02 edge helpers: NewResult(StepOutcome,string) encode-on-write + NewResult(StepOutcome,byte[]) + DataResultExtensions.DataAsString"
  - "Schema-gated byte[] UTF-8 decode in ProcessorJsonSchemaValidator (D-04)"
  - "byte[] flowing the processor seam, pipeline L2 read, and both concrete processors"
affects:
  - "Phase 85 KafkaImporter (writes binary files into this byte[] channel)"
  - "tests/BaseApi.Tests (4 compile-break sites migrate in Plan 02)"
tech-stack:
  added: []
  patterns:
    - "Type-flip with conversion-transparent write sites (RedisValue absorbs byte[])"
    - "Schema-gated UTF-8 decode via single JsonDocument.Parse(byte[]) covering bad-UTF8 AND bad-JSON"
    - "Edge-only string convenience helpers (encode-on-write / decode-on-read) over a byte[] ground truth"
key-files:
  created:
    - src/Messaging.Contracts/DataResultExtensions.cs
  modified:
    - src/Messaging.Contracts/DataResult.cs
    - src/BaseProcessor.Core/Processing/BaseProcessor.cs
    - src/BaseProcessor.Core/Processing/BaseProcessor`1.cs
    - src/BaseProcessor.Core/Processing/ProcessorPipeline.cs
    - src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs
    - src/Processor.Sample/SampleProcessor.cs
    - src/Processor.BadConfig/BadConfigProcessor.cs
decisions:
  - "D-01 honored: single byte[] Data field, no parallel Binary field, no discriminator"
  - "D-02 honored: three thin edge helpers, XML-doc'd as edge-only conversions; byte[] stays the stored type"
  - "D-04 honored: bad UTF-8/JSON under a schema routes through the existing JsonException catch → business Failed, message widened to 'Data is not valid JSON/UTF-8.'"
  - "OQ-1 scope fence held: NextStepHandoff.Data stays string; no Orchestrator/Dispatch, RelocateTail, NextStepHandoff, or OrchestratorInject* files touched"
metrics:
  duration: ~15m
  completed: 2026-07-21
---

# Phase 84 Plan 01: `byte[]` Data-Channel Widening Summary

Flipped the framework payload spine `DataResult.Data` from `string` to `byte[]` (the single ground-truth type) and migrated every typed production consumer in the same wave, reaching a 0-warning `src/` build in Debug AND Release. Base64 leaves the hot path; UTF-8/JSON becomes an optional schema lens over opaque bytes. The orchestrator relocate path was deliberately left on `string` per the OQ-1 scope fence.

## What Was Built

### Task 1 — contract flip + D-02 edge helpers (commit `dedc839`)
- `DataResult.Data` is now `public byte[] Data { get; init; } = Array.Empty<byte>();` — the ground-truth payload field. No parallel `Binary` field and no discriminator (D-01); the XML-doc now states byte[] is the ground truth and JSON is a schema lens.
- New `src/Messaging.Contracts/DataResultExtensions.cs` — `public static class DataResultExtensions` with `DataAsString(this DataResult dr) => Encoding.UTF8.GetString(dr.Data)`, XML-doc'd as an EDGE conversion only (D-02).
- `BaseProcessor.NewResult` refactored into a two-overload D-02 pair: `NewResult(StepOutcome, string) => NewResult(result, Encoding.UTF8.GetBytes(data))` (encode-on-write edge, keeps existing string callers byte-identical) delegating to a new `NewResult(StepOutcome, byte[])` that stamps the ambient ids/messageId and stores the bytes verbatim. Added `using System.Text;`.

### Task 2 — consumer migration + build close (commit `e220c9e`)
- Every typed seam signature flipped `string validatedData` → `byte[] validatedData`: `BaseProcessor.ExecuteAsync`, `BaseProcessor`1.ExecuteAsync` override + abstract `ProcessAsync`, `SampleProcessor.ProcessAsync`, `BadConfigProcessor.ProcessAsync`.
- `ProcessorPipeline`: `byte[] validatedData;` with `Array.Empty<byte>()` source default; the L2 read lambda's `return raw.ToString();` became `return (byte[])raw!;` (RedisValue→byte[] explicit; the retry outcome is now `RetryOutcome<byte[]>`). The three `new DataResult { … Data = validatedData … }` constructions needed no edit (byte[]→byte[]).
- `ProcessorJsonSchemaValidator.TryValidate(string?, byte[], out …)`: the schema-absent `IsNullOrWhiteSpace(definition)` guard remains the FIRST statement (opaque bytes never decoded — D-04); `JsonDocument.Parse(data)` now binds the UTF-8 `ReadOnlyMemory<byte>` overload; the malformed-data message widened to `"Data is not valid JSON/UTF-8."`. No separate `Encoding.UTF8.GetString` try/catch — the single Parse + existing `catch (JsonException)` covers bad UTF-8 and bad JSON both.
- `SampleProcessor.cs:77` `JsonDocument.Parse(validatedData)` and `:70,:83` `NewResult(…, data)` compile unchanged (byte[] parse overload + string encode overload). `BadConfigProcessor` `NewResult(…, "processor-badconfig-ok")` uses the string encode overload unchanged.
- Transparent sites confirmed compiling with no edit: `OutputTail.cs` (validator byte[] overload + byte[]→RedisValue write) and `Keeper/Recovery/InjectConsumer.cs` (byte[]→RedisValue write).

## Verification

- **Build:** all 9 `src/` projects build `Build succeeded` with `-warnaserror` in BOTH Debug and Release (BaseProcessor.Core, Processor.Sample, Processor.BadConfig, Keeper, Orchestrator, Messaging.Contracts, BaseApi.Core, BaseConsole.Core, BaseApi.Service).
- **Grep invariants (all pass):** `public byte[] Data` in DataResult.cs; `DataAsString` in DataResultExtensions.cs; `byte[] validatedData` in BaseProcessor.cs / BaseProcessor`1.cs / SampleProcessor.cs / BadConfigProcessor.cs; `(byte[])raw` in ProcessorPipeline.cs; `TryValidate(string? definition, byte[] data` + `"Data is not valid JSON/UTF-8."` in the validator; validator schema-absent guard is the first statement.
- **Scope fence (OQ-1) intact:** `git diff --name-only` includes NONE of NextStepHandoff.cs, OrchestratorInject.cs, OrchestratorPrePipeline.cs, RelocateTail.cs, OrchestratorInjectConsumer.cs, or any `Orchestrator/Dispatch/*`.
- **No bridge / no discriminator (D-01/D-03):** no `byte[]? Binary` field, no parallel payload field anywhere.

Hermetic + the 7-scenario sweep are deferred to Plans 02/03 (the test project is migrated in Plan 02) and require Docker-backed infra unavailable in this environment.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Ambiguous `<see cref="NewResult"/>` after adding the overload**
- **Found during:** Task 2 build.
- **Issue:** Adding the second `NewResult` overload made the pre-existing class-level XML-doc `<see cref="NewResult"/>` ambiguous, which failed under `-warnaserror` (CS0419) — directly caused by this plan's D-02 overload.
- **Fix:** Qualified the cref to `<see cref="NewResult(StepOutcome, byte[])"/>` and added a reference to its `<see cref="NewResult(StepOutcome, string)"/>` encode-on-write overload.
- **Files modified:** src/BaseProcessor.Core/Processing/BaseProcessor.cs
- **Commit:** e220c9e

No other deviations — the migration matched the RESEARCH.md blast-radius inventory exactly (the transparent write sites and JsonDocument.Parse byte[] overloads compiled with no edit as predicted). No consumer surfaced beyond the inventory; the compiler flagged only the documented sites.

## Notes for Plan 02

- The 4 test compile-break sites remain to be migrated (per RESEARCH.md §Tests): `Processor/DispatchTestKit.cs` (fake `ProcessAsync` + `LastInputData`), `Processor/PrePipelineFacts.cs:312-313` fake `ProcessAsync`, `Orchestrator/SC2RecoveryPathsE2ETests.cs:204` `Data = "inject-payload"` → `Encoding.UTF8.GetBytes(...)`. Plus the optional Wave-0 facts (invalid-UTF8-under-schema → Failed for D-04; DataResult STJ byte round-trip for DATA-05).
- `SK_P.sln` was auto-modified by `dotnet build` (added `NestedProjects` entries); it is NOT in this plan's scope and was left uncommitted/untouched.
- Pre-existing unrelated WIP (`src/Keeper/Recovery/ReinjectConsumer.cs`, `scripts/phase-67-harness.ps1`) left uncommitted and untouched as instructed.

## Known Stubs

None — no stub patterns introduced; all payload flow is wired end-to-end through the byte[] channel.

## Threat Flags

None — no new external attack surface. The only security-relevant path (byte[] decode under a schema) routes hostile/malformed bytes through the existing `JsonException` catch → business `Failed`, matching threat register T-84-01. No new payload logging (FW-03 / T-84-02).

## Self-Check: PASSED

All created files exist on disk (DataResultExtensions.cs, 84-01-SUMMARY.md); both task commits (dedc839, e220c9e) present in git history.
