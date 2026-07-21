---
phase: 84-byte-data-channel-widening
plan: 02
subsystem: hermetic-tests
tags: [byte-array, data-channel, test-migration, schema-validation, stj-roundtrip, refactor]
requires:
  - "Plan 01 byte[] contract (DataResult.Data:byte[], ProcessorJsonSchemaValidator.TryValidate(string?,byte[],out), BaseProcessor seam byte[] validatedData, D-02 helpers)"
provides:
  - "Hermetic test project compiling + passing against the byte[] contract (DATA-03 fast loop restored)"
  - "A1 VERIFIED — DataResult byte[] Data round-trips losslessly through default STJ (base64 wire) for DATA-05"
  - "A2 VERIFIED — invalid-UTF8 bytes under a schema → business Failed via TryValidate for D-04/DATA-02; schema-absent bytes stay opaque"
affects:
  - "Phase 84 Plan 03 (the 7-scenario sweep is the phase gate; these facts are the fast feedback loop, not the gate — D-05)"
tech-stack:
  added: []
  patterns:
    - "Compiler-driven test migration — fix ONLY flagged sites, leave transparent Assert.Equal(byte[],byte[]) untouched (Pitfall 2)"
    - "UTF-8 byte-wrap at test edges (Encoding.UTF8.GetBytes) for string literals feeding the byte[] seam/contract"
    - "STJ byte[]→base64 round-trip fact via SequenceEqual + Convert.ToBase64String wire assertion"
key-files:
  created:
    - tests/BaseApi.Tests/Contracts/DataResultByteChannelFacts.cs
    - tests/BaseApi.Tests/Processor/SchemaByteDecodeFacts.cs
  modified:
    - tests/BaseApi.Tests/Processor/DispatchTestKit.cs
    - tests/BaseApi.Tests/Processor/PrePipelineFacts.cs
    - tests/BaseApi.Tests/Orchestrator/SC2RecoveryPathsE2ETests.cs
    - tests/BaseApi.Tests/Processor/ProcessorJsonSchemaValidatorFacts.cs
    - tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs
    - tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
decisions:
  - "Pitfall 2 respected: no transparent Assert.Equal(byte[],byte[]) assertion rewritten — only compiler-flagged sites edited"
  - "RESEARCH blast-radius under-inventoried the test breaks; the 3 extra compiler-flagged files were fixed minimally (byte-wrap only) and recorded"
  - "A1/A2 authored as ordinary hermetic facts (no new fixture/base-class/gate) per D-05"
metrics:
  duration: ~30m
  completed: 2026-07-21
---

# Phase 84 Plan 02: `byte[]` Data-Channel Widening (Test Migration + A1/A2 Facts) Summary

Migrated the hermetic test project to compile + pass against the Plan-01 `byte[]` `DataResult.Data` contract (0-warning Debug AND Release, warnings-as-errors), then added the two RESEARCH-recommended facts that convert assumptions A1 (recovery wire round-trip) and A2 (bad-UTF8 → business Failed) from MEDIUM to VERIFIED. The fast per-commit feedback loop is restored; the phase gate remains the Plan-03 sweep (D-05).

## What Was Built

### Task 1 — migrate the byte[] test compile-breaks (commit `241dd6d`)

The plan's three confirmed compile-break files, plus three additional files the compiler flagged (RESEARCH under-inventoried these — see Deviations). All fixes are minimal byte-wraps or seam/param type flips; **no transparent `Assert.Equal(byte[], byte[])` assertion was rewritten** (Pitfall 2).

- **DispatchTestKit.cs** (the shared kit): `_impl` field first type-arg `string`→`byte[]`; `LastInputData` property `string?`→`byte[]?`; fake `ProcessAsync` override param `string`→`byte[] validatedData`; the `Result(...)` helper `Data = data` → `Data = System.Text.Encoding.UTF8.GetBytes(data)` (kept the `string data` param). `NewResultPublic(StepOutcome, string)` left as-is (rides the D-02 encode overload). `LastInputData` is only ever assigned, never read as a string, so no consumer assertion broke.
- **PrePipelineFacts.cs:312-313** — `RealDeserProcessor.ProcessAsync` param `string`→`byte[] validatedData`.
- **SC2RecoveryPathsE2ETests.cs:204** — `Data = "inject-payload"` → `Data = System.Text.Encoding.UTF8.GetBytes("inject-payload")` (this class is `Category=RealStack`, so it is compile-verified only; excluded from the hermetic run).

### Task 2 — the A1 and A2 hermetic facts (commit `a7439d6`)

- **`Contracts/DataResultByteChannelFacts.cs`** (A1 / DATA-05): builds a `DataResult` with `Data = new byte[]{0x00,0xFF,0x10,0x7F}` (deliberately non-UTF8-representable), serializes with default STJ then deserializes, and asserts `round.Data.SequenceEqual(original)` AND `json` contains `Convert.ToBase64String(original)` — proving the byte[] payload survives the MassTransit-JSON wire exactly and the transient wire form is base64.
- **`Processor/SchemaByteDecodeFacts.cs`** (A2 / D-04 / DATA-02): with a non-empty schema `{"type":"object"}`, `TryValidate(schema, new byte[]{0xFF,0xFE,0xFD}, out errs)` returns `false` with an error mentioning JSON/UTF-8 (bad UTF-8 under a schema → business Failed, not a crash). Two controls: (a) valid UTF-8 JSON conforming to the schema → `true`; (b) `null`/whitespace definition with the SAME invalid bytes → `true` WITHOUT decoding (opaque-bytes / DATA-02 / Pitfall 3 — a `true` proves the schema-absent guard short-circuits before any parse).

## Verification

- **Build:** test project builds `Build succeeded`, `0 Warning(s) / 0 Error(s)` under `-warnaserror` in BOTH Debug and Release (all 9 src projects + BaseApi.Tests).
- **New + migrated + affected facts (Release, targeted):**
  - `DataResultByteChannelFacts` + `SchemaByteDecodeFacts` + `ProcessorJsonSchemaValidatorFacts` + `SampleProcessorFacts` + `InjectConsumerFacts` → **18/18 passed** (1.8s).
  - `PrePipelineFacts` → **13/13 passed** (0.5s).
- **A1/A2 grep invariants:** `SequenceEqual` present in `DataResultByteChannelFacts.cs`; `TryValidate` present in `SchemaByteDecodeFacts.cs`; `byte[]? LastInputData` present in `DispatchTestKit.cs`.
- **Full filtered run (`BaseApi.Tests.exe --filter-not-trait Category=RealStack`, Release):** `total: 750, succeeded: 720, failed: 30, skipped: 0` (13m 33s). **ZERO new failures attributable to this plan.** All 30 failures fall in exactly **6 classes this plan did NOT touch**, and every one is a pre-existing infra/environment failure (verified by inspecting failure detail):
  - `Orchestrator.ResultAckTests`, `Orchestrator.TypedResultConsumerFacts`, `Orchestrator.StopConsumerLifecycleTests` — reach for `rabbitmq://localhost:5672` (`BrokerUnreachableException`); no broker in the sandbox → fan-out collections empty. (RESEARCH lists all three as transparent — not touched.)
  - `Middleware.ConcurrencyTokenTests`, `Integration.ErrorMappingFacts` — "Database migration failed on startup" (no Postgres).
  - `Composition.ComposeYamlFacts` — "`compose.yaml` not found" (the file was removed by the separate **k8s-only migration** on this branch, commits 6bb1f83/40a0a88 — unrelated to byte[]).
  - This is the pre-existing Docker-less infra baseline the plan describes (the plan estimated ~272; the actual filtered-run failure count in this environment is 30). None involve `DataResult.Data`/`validatedData` byte[] logic; **all classes migrated or created by this plan are in the 720 passed** (independently confirmed by the targeted runs above).
- **Scope discipline:** `git diff` for the two task commits touches ONLY the 5 planned `files_modified` files plus the 3 additional compiler-flagged files (recorded below). Unrelated WIP (`src/Keeper/Recovery/ReinjectConsumer.cs`, `scripts/phase-67-harness.ps1`) left uncommitted and untouched; `SK_P.sln` not touched.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] RESEARCH under-inventoried the test compile-breaks — 3 additional files flagged by the compiler**
- **Found during:** Task 1 build (`dotnet build ... -c Debug -warnaserror`).
- **Issue:** The RESEARCH §Blast-Radius test table listed `ProcessorJsonSchemaValidatorFacts`, `SampleProcessorFacts:63/96/120`, and `InjectConsumerFacts:40` as either transparent or not at all. The compiler flagged 11 genuine `CS1503`/`CS0029` type errors across them — direct consequences of the Plan-01 `TryValidate(...,byte[],...)`, `ExecuteAsync(byte[]...)`, and `DataResult.Data:byte[]` flips. These are hard compile-breaks, not transparent assertions.
- **Fix (minimal, byte-wrap only — no assertion churn):**
  - `ProcessorJsonSchemaValidatorFacts.cs` — 7 `TryValidate(def, "<json>", out …)` data args wrapped in `Encoding.UTF8.GetBytes(...)` (added `using System.Text;`).
  - `SampleProcessorFacts.cs` — 3 `ExecuteAsync("<input>", payload, …)` first args (the `validatedData` seam param, now `byte[]`) wrapped in `System.Text.Encoding.UTF8.GetBytes(...)`. The transparent `JsonDocument.Parse(x.Data)` reads on 73/101/127 were correctly NOT flagged and left untouched.
  - `InjectConsumerFacts.cs` — the `NewDataResult(StepOutcome, string data)` helper's `Data = data` → `Data = System.Text.Encoding.UTF8.GetBytes(data)` (kept the string param, mirroring the plan's `Result` helper pattern).
- **Files modified:** tests/BaseApi.Tests/Processor/ProcessorJsonSchemaValidatorFacts.cs, tests/BaseApi.Tests/Processor/SampleProcessorFacts.cs, tests/BaseApi.Tests/Keeper/InjectConsumerFacts.cs
- **Commit:** 241dd6d

No architectural changes; no package installs; no production code touched.

## Known Stubs

None — both new facts assert real behavior end-to-end (STJ round-trip; schema-gated decode). No placeholder/empty-value patterns introduced.

## Threat Flags

None new. Threat register items held: `SchemaByteDecodeFacts` (A2) positively proves malformed bytes under a schema → business Failed (hardens T-84-01/D-04 into a regression guard); `FrameworkNoPayloadFacts` (T-84-02, FW-03 payload-scan) is untouched and remains in the hermetic set. No new external surface.

## Self-Check: PASSED

- Created files exist on disk: `tests/BaseApi.Tests/Contracts/DataResultByteChannelFacts.cs`, `tests/BaseApi.Tests/Processor/SchemaByteDecodeFacts.cs` — both FOUND.
- Both task commits present in git history: `241dd6d` (Task 1 migration), `a7439d6` (Task 2 A1/A2 facts).
- Unrelated WIP confirmed uncommitted/untouched.
