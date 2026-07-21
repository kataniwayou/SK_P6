# Phase 84: `byte[]` Data-Channel Widening - Research

**Researched:** 2026-07-21
**Domain:** .NET record contract widening (`string` → `byte[]`) across a MassTransit/RabbitMQ + StackExchange.Redis pipeline; System.Text.Json schema validation; sweep-only verification
**Confidence:** HIGH (all findings verified by direct source read of the touched files; no external/training-only claims on the hot path)

## Summary

The change is far smaller and lower-risk than the "record value-equality break" framing suggests, because two ecosystem facts absorb most of the churn: (1) StackExchange.Redis `RedisValue` has **implicit conversions from both `string` and `byte[]`**, so every L2 *write* site (`OutputTail`, `InjectConsumer`, and the orchestrator relocate) compiles unchanged when `DataResult.Data` flips type; and (2) xUnit `Assert.Equal(byte[], byte[])` does **element-wise structural comparison** (byte[] is `IEnumerable<byte>`), so almost every existing test assertion on `.Data` keeps passing. The genuinely-typed edits collapse to ~8 production sites and ~4 test sites.

The single most important scoping finding: **the byte[] boundary stops at the processor↔L2 seam.** The orchestrator's relocate path (`OrchestratorPrePipeline` reads `L2[out:]` as a string → `NextStepHandoff.Data` (string) → `RelocateTail`/`OrchestratorInjectConsumer` write it back) round-trips UTF-8 JSON **byte-for-byte losslessly**, which is exactly the DATA-03 invariant and exactly what the sweep exercises (Processor.Sample only ever emits UTF-8 JSON). `NextStepHandoff.Data` should therefore **stay `string`** in Phase 84 — widening it is out of the minimal blast radius and only matters when *true binary* must relocate *through* the orchestrator (Phase 85 / MANIP, deferred). This aligns with CONTEXT, which lists only the recovery consumers — not `OrchestratorPrePipeline`/`RelocateTail`/`NextStepHandoff` — as touch-points. See Open Question OQ-1; it needs an explicit planner ruling against DATA-05's wording.

The D-04 decode point is elegant: `ProcessorJsonSchemaValidator.TryValidate` already guards schema-absent **before** parsing, and `JsonDocument.Parse(ReadOnlyMemory<byte>)` treats bytes as UTF-8 and throws `JsonException` on **both** invalid-UTF8 and invalid-JSON — so changing its `data` param `string`→`byte[]` and letting the existing `catch (JsonException) → false → Failed` fire satisfies D-04 with no new error surface.

**Primary recommendation:** Flip `DataResult.Data` to `byte[]`; make `validatedData` `byte[]` through `ProcessorPipeline` (read via `(byte[])raw!`); change `TryValidate(..., byte[] data)` + `JsonDocument.Parse(data)`; keep `NewResult(StepOutcome, string)` as a UTF-8 encode-on-write helper (add a `byte[]` overload) and add a `DataAsString` decode-on-read accessor; leave `NextStepHandoff.Data` as `string`; migrate the 4 test compile-breaks; gate solely on `scripts/phase-68-sweep.ps1` all-PASS run detached.

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| DATA-01 | `DataResult.Data` is `byte[]` ground truth; flows L2/seam/recovery without base64/string coercion | `DataResult.cs:15` field flip + `ProcessorPipeline` `validatedData:byte[]` read via `(byte[])raw!`; RedisValue implicit conv makes writes transparent (see Blast Radius) |
| DATA-02 | Schema present → interpret bytes as UTF-8 JSON + validate; no schema → opaque bytes | `ProcessorJsonSchemaValidator.TryValidate` schema-absent guard runs BEFORE parse (`:34`); `JsonDocument.Parse(byte[])` decode only when definition non-empty (Pattern 2) |
| DATA-03 | Existing string/JSON path byte-for-byte equivalent; Processor.Sample stays green | STJ `byte[]`↔base64 wire + Redis UTF-8 storage are lossless for UTF-8 JSON; orchestrator string relocate round-trips identically (Architecture §Data Flow) |
| DATA-04 | The 7-scenario sweep (`phase-68-sweep.ps1`) reproduces all-PASS = SOLE gate | Sweep script confirmed present + parameterized; Validation Architecture §Sweep-as-Gate; run detached per project memory |
| DATA-05 | Recovery carries `byte[]` losslessly (MassTransit transient base64) | `KeeperInject` embeds `DataResult` (→ byte[]); STJ base64 round-trip lossless; `OrchestratorInject` embeds `NextStepHandoff` (string) — see OQ-1 for the DATA-05 wording nuance |
</phase_requirements>

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Replace `string Data` with `byte[] Data` outright — one ground-truth field. NO parallel `byte[]? Binary` + discriminator (rejected type-switch / two sources of truth).
- **D-02:** Add THIN UTF-8 convenience helpers — an encode-on-write path (`NewResult(StepOutcome, string)` overload that UTF-8-encodes) and a decode-on-read UTF-8 string accessor (`DataAsString` or an extension). EDGE CONVERSIONS ONLY; the stored/wire type stays `byte[]`. Keep the helper set minimal.
- **D-03:** Staged waves, but **no temporary string↔byte bridge**. Suggested (planner's discretion on exact waves): wave 1 = contract + helpers; wave 2 = consumers + Processor.Sample + recovery; wave 3 = run the sweep. The LOCKED decision is "no bridge," not the wave count.
- **D-04:** Schema configured + bytes not valid UTF-8/JSON → business `Failed` via the SAME `ProcessorJsonSchemaValidator.cs:46-51` malformed-JSON path (return false → Failed). No new error surface. Only arises UNDER a schema — schema-absent bytes are never decoded.
- **D-05:** SOLE acceptance gate = the existing 7-scenario fault-recovery sweep (`scripts/phase-68-sweep.ps1`) reproducing all-PASS (every `Missing==0`, every `Duplicates==0`). Byte-for-byte-equivalence invariant; Processor.Sample stays green; existing hermetic tests must still compile + pass (via D-02 helpers), but the phase VERDICT is the sweep.

### Claude's Discretion
- Exact wave breakdown within the D-03 staging.
- Exact naming/shape of the D-02 helpers (overload vs. extension vs. static factory), as long as `byte[]` stays the stored type.
- Whether recovery's `byte[]` over the RabbitMQ wire warrants a size note (MassTransit base64 accepted as transient).

### Deferred Ideas (OUT OF SCOPE)
- KafkaImporter (KIMP-01..06) — Phase 85, depends on this byte[] channel.
- File-manipulator step (MANIP-01) and KafkaExporter (KEXP-01) — future milestones.
- Switching MassTransit to a binary serializer to eliminate the transient wire base64 — out of scope.
</user_constraints>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Ground-truth payload type (`byte[]`) | Messaging.Contracts (`DataResult`) | — | The contract is the single spine: seam return + `-post` wire + `KeeperInject` body + L2 value |
| L2 read → `validatedData` | BaseProcessor.Core (`ProcessorPipeline`) | Redis (storage) | Pipeline owns the read/decode-boundary; Redis already stores raw bytes (RedisValue) |
| Schema-gated UTF-8 decode + validate (D-04) | BaseProcessor.Core (`ProcessorJsonSchemaValidator`) | — | Validator is the ONLY place bytes are interpreted as JSON; opaque otherwise |
| L2 write of output blob | BaseProcessor.Core (`OutputTail`) + Keeper (`InjectConsumer`) | Redis | Write sites; RedisValue absorbs the type change |
| Author encode/decode ergonomics (D-02) | BaseProcessor.Core (`BaseProcessor.NewResult` + accessor) | Processor.Sample | Edge helpers keep authors off raw `Encoding.UTF8` |
| Recovery round-trip (DATA-05) | Messaging.Contracts (`KeeperInject`→`DataResult`) + Keeper (`InjectConsumer`) | MassTransit/RabbitMQ | STJ base64s `byte[]` transiently on the wire; L2 hot path stays binary |
| Orchestrator relocate (`NextStepHandoff.Data`) | Orchestrator (`OrchestratorPrePipeline`/`RelocateTail`) | Keeper (`OrchestratorInjectConsumer`) | **Stays `string`** — UTF-8 round-trip is byte-lossless for the sweep; NOT in the minimal blast radius (OQ-1) |

## Standard Stack

No new packages. This phase is a pure type-widening refactor on the existing stack.

### Core (already present — versions from the build; net8.0, LangVersion latest)
| Library | Purpose | Relevance to this phase |
|---------|---------|-------------------------|
| System.Text.Json (in-box, net8.0) | Serialization + `JsonDocument` schema-lens parse | `JsonDocument.Parse(ReadOnlyMemory<byte>)` is the D-04 decode; STJ serializes `byte[]` as base64 by default (DATA-05 wire) `[CITED: learn.microsoft.com JsonSerializer byte[] → Base64]` |
| StackExchange.Redis | L2 store (`IDatabase.StringGet/StringSet`) | `RedisValue` has implicit conv from/to `string` AND `byte[]` — write sites are conversion-transparent `[VERIFIED: source read — writes compile unchanged]` |
| MassTransit (RabbitMQ, JSON serializer) | `-post` + `KeeperInject`/`OrchestratorInject` wire | Uses STJ → `byte[]` base64s transiently; no config change `[CITED: masstransit STJ serializer default]` |
| JsonSchema.Net (`Json.Schema`) | `ProcessorJsonSchemaValidator` | Unchanged; only the `data` param type flips (see Pattern 2) |
| xUnit + FluentAssertions/Assert | Hermetic facts | `Assert.Equal(byte[], byte[])` = structural comparison — most `.Data` asserts survive `[VERIFIED: xUnit Assert.Equal collection semantics]` |

**Installation:** none.

## Package Legitimacy Audit

**Not applicable — this phase installs no external packages.** It is a type change on the existing, already-audited dependency set. No slopcheck/registry verification required.

## Blast Radius — Complete Reader/Writer Inventory of `DataResult.Data`

Legend: **[TYPED]** = a real compile-affecting edit; **[TRANSPARENT]** = compiles unchanged due to RedisValue implicit conv / xUnit structural equality / `JsonDocument.Parse(byte[])`; **[NO CHANGE]** = out of the byte[] boundary.

### Production `src/` — the widening core
| File:Line | Current | Action | Class |
|-----------|---------|--------|-------|
| `Messaging.Contracts/DataResult.cs:15` | `string Data { get; init; } = "";` | → `byte[] Data { get; init; } = Array.Empty<byte>();` | **[TYPED]** |
| `BaseProcessor.Core/Processing/BaseProcessor.cs:82-83` | internal `ExecuteAsync(string validatedData, …)` | → `byte[] validatedData` | **[TYPED]** |
| `BaseProcessor.cs:152,162` | `NewResult(StepOutcome, string data)` → `Data = data` | D-02 encode-on-write: `Data = Encoding.UTF8.GetBytes(data)`; add `NewResult(StepOutcome, byte[])` overload; add `DataAsString` accessor | **[TYPED]** |
| `BaseProcessor.Core/Processing/BaseProcessor`1.cs:20-21,38-39` | internal + abstract `ProcessAsync(string validatedData, …)` | → `byte[] validatedData` (author seam) | **[TYPED]** |
| `BaseProcessor.Core/Processing/ProcessorPipeline.cs:85,88` | `string validatedData` / `= string.Empty` | → `byte[] validatedData` / `= Array.Empty<byte>()` | **[TYPED]** |
| `ProcessorPipeline.cs:127-129` | read lambda `return raw.ToString();` | → `return (byte[])raw!;` (retry outcome becomes `RetryOutcome<byte[]>`) | **[TYPED]** |
| `ProcessorPipeline.cs:135` | `TryValidate(InputDefinition, validatedData, …)` | validator param now `byte[]` — matches | **[TYPED via validator]** |
| `ProcessorPipeline.cs:142,166,195` | `Data = validatedData` (fail/status/unexpected DataResults) | `validatedData` now `byte[]` → assigns to `byte[] Data` | **[TRANSPARENT]** (follows from :85) |
| `BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs:30,46` | `TryValidate(string? definition, string data, …)`; `JsonDocument.Parse(data)` | → `byte[] data`; `JsonDocument.Parse(data)` (UTF-8 bytes; JsonException on bad UTF-8 OR bad JSON); update the `:49` message to "not valid JSON/UTF-8" (D-04) | **[TYPED]** |
| `BaseProcessor.Core/Processing/OutputTail.cs:74` | `TryValidate(OutputDefinition, dr.Data, out _)` | `dr.Data` now `byte[]` → validator `byte[]` overload | **[TRANSPARENT]** |
| `OutputTail.cs:82` | `StringSetAsync(OutputData(dr.MessageId), dr.Data, …)` | `byte[]`→`RedisValue` implicit | **[TRANSPARENT]** |
| `Keeper/Recovery/InjectConsumer.cs:42` | `StringSetAsync(OutputData(dr.MessageId), dr.Data, …)` | `byte[]`→`RedisValue` implicit | **[TRANSPARENT]** |
| `BaseProcessor.Core/Processing/PostProcessConsumer.cs:52` | passes `ctx.Message` (DataResult) to OutputTail | no `.Data` touch | **[NO CHANGE]** |
| `Processor.Sample/SampleProcessor.cs:39-40` | `ProcessAsync(string validatedData, …)` | → `byte[] validatedData` | **[TYPED]** |
| `SampleProcessor.cs:70,83` | `this.NewResult(StepOutcome.Completed, data)` (data = JSON string) | D-02 string overload UTF-8-encodes — no change | **[TRANSPARENT]** |
| `SampleProcessor.cs:77` | `JsonDocument.Parse(validatedData)` | `byte[]` via `ReadOnlyMemory<byte>` overload — no change | **[TRANSPARENT]** |

### Production `src/` — the orchestrator relocate path (`NextStepHandoff.Data`, string) — **[NO CHANGE], see OQ-1**
| File:Line | Why unchanged |
|-----------|---------------|
| `Messaging.Contracts/NextStepHandoff.cs:19` (`string Data`) | Distinct field; carries the *relocated input JSON*. Stays `string` — UTF-8 round-trip is byte-lossless for the sweep |
| `Messaging.Contracts/OrchestratorInject.cs:15` (embeds `NextStepHandoff`) | Embeds the string-Data handoff; unchanged |
| `Orchestrator/Dispatch/OrchestratorPrePipeline.cs:127-128,137,147` | Reads `L2[out:]` as `raw.ToString()` → `NextStepHandoff.Data` (string). Not in the CONTEXT touch-point list |
| `Orchestrator/Dispatch/RelocateTail.cs:54-57` | Writes `h.Data` (string) → RedisValue |
| `Keeper/Recovery/OrchestratorInjectConsumer.cs:45-47` | Writes `h.Data` (string) → RedisValue. In CONTEXT scope but needs NO change |

### Tests `tests/BaseApi.Tests/` — compile-breaks (**[TYPED]**, must edit)
| File:Line | Break | Fix |
|-----------|-------|-----|
| `Processor/DispatchTestKit.cs:97-99` | fake `ProcessAsync(string validatedData, DummyConfig?, …)` | → `byte[] validatedData` |
| `DispatchTestKit.cs:51,61,73` (lambdas) + `:83` `LastInputData {get} : string?` + `:54,64,76` `LastInputData = validatedData` | `validatedData` now `byte[]` | change `LastInputData` to `byte[]?`; any consumer asserting it as string decodes via `DataAsString`/`Encoding.UTF8.GetString` |
| `DispatchTestKit.cs:95` `NewResultPublic(StepOutcome, string data)` | wraps `NewResult(outcome, data)` | keep `string` (D-02 encode overload) — no change once helper exists |
| `Processor/PrePipelineFacts.cs:312-313` | fake `ProcessAsync(string validatedData, DeserConfig?, …)` | → `byte[] validatedData` |
| `Orchestrator/SC2RecoveryPathsE2ETests.cs:204` | `Data = "inject-payload"` on a `new DataResult{…}` | → `Data = Encoding.UTF8.GetBytes("inject-payload")` (or a test helper) |

### Tests — **[TRANSPARENT]** (compile + pass unchanged — verified, do NOT touch reflexively)
- `Contracts/OrchestratorContractTests.cs:32,45,58,84,85` — all on `NextStepHandoff.Data` (string) + whole-`NextStepHandoff` `Assert.Equal` → unaffected.
- `Orchestrator/{ResultAckTests:213, OrchestratorPrePipelineFacts:267/517/549, StopConsumerLifecycleTests:185}` — `Assert.Equal("literal", handoff.Data)` on `NextStepHandoff.Data` (string) → unaffected.
- `Orchestrator/TypedResultConsumerFacts.cs:262` `Assert.Equal(direct, injected)` — on **`StepCompleted`** (no Data field) → unaffected; `:280 Assert.Equal(d.Data, i.Data)` — on `Handoffs` (`NextStepHandoff`, string) → unaffected.
- `Keeper/InjectConsumerFacts.cs:90` `(RedisValue)dr.Data` — `byte[]`→`RedisValue` cast valid.
- `Keeper/OrchestratorInjectConsumerFacts.cs:93` `(RedisValue)h.Data` — string handoff → unaffected.
- `Processor/SampleProcessorFacts.cs:73,101,127` `JsonDocument.Parse(x.Data)` — `byte[]` overload.
- `Processor/FanInHermeticHarnessFacts.cs:130` `ReadNumber(dr.Data)` (`ReadNumber(RedisValue)`) — `byte[]`→`RedisValue` implicit; `.ToString()` inside returns UTF-8 string.
- `Processor/BaseProcessorSeamFacts.cs:80,100,177` `NewResultPublic(StepOutcome.Completed, "{…}")` — string overload.
- `Observability/FrameworkNoPayloadFacts.cs` — token/reflection scan, not a `.Data` value assertion.

## Architecture Patterns

### System Data Flow (traceable primary use case)

```
                       ┌──────────── byte[] boundary (Phase 84) ────────────┐
 EntryStepDispatch ──▶ ProcessorPipeline.RunAsync
                        │  gate L2[data:entryId]  (RedisValue)
                        │  read  L2[data:entryId] ── (byte[])raw! ──▶ validatedData : byte[]
                        │  TryValidate(InputDefinition, validatedData)   ← schema-gated UTF-8 decode (D-04)
                        │  seam: ExecuteAsync(byte[] validatedData) ─▶ ProcessAsync(byte[], config)
                        │                                                │
                        │        author: NewResult(Completed, jsonString) ─ UTF-8 encode ─▶ DataResult.Data : byte[]
                        ▼                                                │
                     OutputTail.RunAsync(dr)                            │
                        │  TryValidate(OutputDefinition, dr.Data)  ← decode-on-validate (D-04)
                        │  write L2[out:messageId] = dr.Data  (byte[]→RedisValue)
                        │  send Step* ──────────────────────────────────┼──▶ queue:orchestrator-result
                        │  (write-exhaust) INJECT: KeeperInject{DataResult} ─── STJ base64 ──▶ RabbitMQ
                        └──────────── byte[] boundary ──────────────────┘
                                                                         │
                       ┌──── string boundary (UNCHANGED, byte-lossless) ─┼───┐
 Step* ──▶ OrchestratorPrePipeline: read L2[out:] = raw.ToString()  (string)  │
            fan-out ─▶ NextStepHandoff.Data : string ─▶ queue:orchestrator-result-post
                       RelocateTail: write L2[data:messageId] = h.Data  (string→RedisValue, UTF-8 bytes)
                       └──────────────────────────────────────────────────────┘
                                                                         ▼
                                          (next hop: processor reads L2[data:] back as byte[] — lossless)
```

The round-trip closes because Redis stores raw bytes either way: a `string` written by the orchestrator is stored as its UTF-8 bytes, and the next processor reads those exact bytes as `byte[]`. **Byte-for-byte equivalence (DATA-03) holds for all UTF-8 JSON content** — which is everything Processor.Sample emits.

### Pattern 1: Type-flip with conversion-transparent write sites
**What:** Change the field type at the source (`DataResult.Data`) and at the *read decode* boundary only; let RedisValue implicit conversions carry every *write*.
**When:** Any StackExchange.Redis payload whose C# type changes between `string` and `byte[]`.
**Example:**
```csharp
// Source: src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:127-132 (read side — the ONE decode edit)
var raw = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(d.EntryId));
if (raw.IsNullOrEmpty) throw new KeyAbsentException();
return (byte[])raw!;              // was: raw.ToString();  RedisValue → byte[] explicit conv
// ... write sites (OutputTail.cs:82, InjectConsumer.cs:42) are UNCHANGED: byte[] → RedisValue is implicit.
```

### Pattern 2: Schema-gated UTF-8 decode via `JsonDocument.Parse(byte[])` (D-04)
**What:** The schema-absent guard stays first (opaque bytes never decoded); when a schema is present, parse the bytes directly as UTF-8 JSON — a single call covers both "bad UTF-8" and "bad JSON".
**Example:**
```csharp
// Source: src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs:30-51 (adapted)
public static bool TryValidate(string? definition, byte[] data, out IReadOnlyList<string> errors)
{
    errors = Array.Empty<string>();
    if (string.IsNullOrWhiteSpace(definition)) return true;   // D-04: schema-absent → bytes NEVER decoded
    // ... FromText(definition) unchanged ...
    JsonDocument doc;
    try { doc = JsonDocument.Parse(data); }                    // byte[] ⇒ ReadOnlyMemory<byte>; UTF-8
    catch (JsonException)
    {
        errors = new[] { "Data is not valid JSON/UTF-8." };    // D-04: bad UTF-8 OR bad JSON → business Failed
        return false;
    }
    // ... Evaluate unchanged ...
}
```
STJ's UTF-8 reader raises `JsonException` on malformed UTF-8 byte sequences, so no separate `Encoding.UTF8.GetString` try/catch is needed — the existing catch is the D-04 path. `[VERIFIED: source read + STJ Utf8JsonReader throws JsonException on invalid UTF-8]`

### Pattern 3: Minimal D-02 helpers (encode-on-write + decode-on-read)
```csharp
// Source: src/BaseProcessor.Core/Processing/BaseProcessor.cs:152 (NewResult) — recommended minimal shape
protected DataResult NewResult(StepOutcome result, string data)             // encode-on-write (keeps SampleProcessor unchanged)
    => NewResult(result, Encoding.UTF8.GetBytes(data));
protected DataResult NewResult(StepOutcome result, byte[] data) { /* stamp ambient ids; Data = data */ }
// decode-on-read accessor (extension in Messaging.Contracts, or property on DataResult):
public static string DataAsString(this DataResult dr) => Encoding.UTF8.GetString(dr.Data);
```
Keep both clearly XML-doc'd as EDGE conversions so no future reader mistakes the string view for the canonical type (per CONTEXT §Specific Ideas).

### Anti-Patterns to Avoid
- **Widening `NextStepHandoff.Data` "for consistency."** It is a separate field on a separate contract; the sweep does not require it, CONTEXT scopes it out, and touching `OrchestratorPrePipeline`/`RelocateTail` enlarges the blast radius and risk for zero sweep benefit. Defer to when true binary relocates through the orchestrator (OQ-1).
- **Re-touching [TRANSPARENT] test assertions.** `Assert.Equal(byte[], byte[])` already passes structurally; rewriting them to `SequenceEqual`/`DataAsString` is churn that risks introducing the very equality bug you're trying to avoid.
- **A `byte[]? Binary` + discriminator.** Explicitly rejected by D-01.
- **A temporary string↔byte bridge.** Explicitly rejected by D-03.
- **Decoding bytes when no schema is configured.** Violates D-04/DATA-02 (bytes must stay opaque); keep the schema-absent guard first.
- **Designing a new test suite as the gate.** D-05: the sweep is the sole verdict.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| bytes ↔ RedisValue | manual `Encoding.UTF8.GetBytes` at every write | RedisValue implicit conversions (leave writes untouched) | Redis is already binary-safe; hand-encoding reintroduces string-as-truth |
| bytes → JSON parse + UTF-8 validation | `Encoding.UTF8.GetString` then `JsonDocument.Parse(string)` | `JsonDocument.Parse(byte[])` (one call) | Single call validates UTF-8 AND JSON, one `JsonException` catch; matches D-04 exactly |
| byte[] over the bus | custom base64 field / serializer swap | default STJ (`byte[]` → base64 transparently) | MassTransit-JSON already does it losslessly; D-05 accepts the transient base64 |
| byte[] equality in tests | custom comparer | xUnit `Assert.Equal(byte[], byte[])` (structural) | Already element-wise; a custom comparer is unnecessary surface |

**Key insight:** The two ecosystem behaviors (RedisValue implicit conv + xUnit structural byte[] equality) mean the "record equality break" is almost entirely theoretical *in this codebase* — the only real compile breaks are the seam signature and the two string-literal `Data =` constructions.

## Runtime State Inventory (rename/refactor phase)

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | **L2 (Redis) blobs** at `data:{entryId}` / `out:{messageId}` are already stored as raw bytes (StackExchange.Redis stores UTF-8 bytes of the current strings). A byte[] contract reads/writes the SAME bytes. | None for data-at-rest — the migration exposes bytes Redis already stores. Stale blobs from a prior run are UTF-8 JSON → read back losslessly. The mandatory reseed (below) clears them anyway. |
| Live service config | None — no external service stores the `Data` type as configuration. | None. |
| OS-registered state | None. | None. |
| Secrets/env vars | None reference `DataResult.Data`. The env-gated TEST-ONLY seams (`PROCESSOR_DEFEAT_READ`, `KEEPER_DEFEAT_REINJECT`, `PROCESSOR_STEP_DELAY_MS`) are payload-agnostic and unaffected. | None. |
| Build artifacts | Recompiled DLLs. **SourceHash reseed:** BaseProcessor.Core changes → a new processor SourceHash. Per project memory [[rebuild-sourcehash-reseed-order]], after rebuild the new SourceHash has no processor row → the sweep's reset heal-wait fails until you **graph-delete → seed → then start** (reseed-FIRST runbook). | Follow the reseed-first order in the sweep task; do NOT skip it after the rebuild. |

**The canonical question — after every file is updated, what runtime state still holds the old shape?** Only L2 blobs, and those are byte-identical (UTF-8 JSON) so no data migration is needed; the reseed clears them regardless. **Nothing else found — verified by grep across src/ + tests/ and the recovery/orchestrator consumers.**

## Common Pitfalls

### Pitfall 1: Assuming the orchestrator relocate must also become byte[]
**What goes wrong:** Planner widens `NextStepHandoff.Data` → touches `OrchestratorPrePipeline` (`raw.ToString()` read), `RelocateTail`, `OrchestratorInjectConsumer`, `OrchestratorContractTests` — a large, out-of-scope blast radius.
**Why it happens:** DATA-05 names "OrchestratorInject," and the two `.Data` fields look identical.
**How to avoid:** They are different contracts. For the sweep (UTF-8 JSON), the string relocate is byte-for-byte lossless. Keep it string; record OQ-1 as the explicit deferral.
**Warning signs:** A wave that edits `Orchestrator/Dispatch/*` — CONTEXT lists none of those as touch-points.

### Pitfall 2: Rewriting passing byte[] test assertions
**What goes wrong:** Converting `Assert.Equal(byte[], byte[])` to hand-rolled comparisons introduces bugs and inflates the diff.
**How to avoid:** Only edit the 4 confirmed compile-breaks (DispatchTestKit, PrePipelineFacts, SC2RecoveryPathsE2ETests). Compile the test project; fix ONLY what the compiler flags.
**Warning signs:** Touching `SampleProcessorFacts`, `OrchestratorContractTests`, `FanInHermeticHarnessFacts` — all verified transparent.

### Pitfall 3: Decoding opaque bytes (breaking DATA-02)
**What goes wrong:** Moving/removing the `IsNullOrWhiteSpace(definition)` guard so bytes get parsed even with no schema → a non-JSON payload wrongly fails.
**How to avoid:** Keep the schema-absent guard as the first statement in `TryValidate` (it already is, `:34`).
**Warning signs:** A `JsonDocument.Parse` reachable before the definition guard.

### Pitfall 4: Skipping the SourceHash reseed after the BaseProcessor.Core rebuild
**What goes wrong:** The sweep's reset heal-wait hangs/fails because the new SourceHash has no processor row.
**How to avoid:** graph-delete → seed → start (reseed-FIRST), per [[rebuild-sourcehash-reseed-order]].
**Warning signs:** Reset step times out on heal-wait.

### Pitfall 5: Running the ~2h sweep as a tracked background task
**What goes wrong:** Long sweeps get reaped as tracked bg tasks.
**How to avoid:** Run detached via `Start-Process` + a separate short watcher loop; clear stale reports first; **trust the harness exit code over any stale analyzer report** ([[long-sweep-detached-process]], [[phase-68-sweep-stale-report-read]]). TEST-01 can flake on cold ES — re-run the single id, never auto-retry a verdict FAIL.

## Code Examples

### Encode-on-write author call (unchanged after D-02 helper)
```csharp
// Source: src/Processor.Sample/SampleProcessor.cs:69-70 — stays byte-for-byte identical
var data = JsonSerializer.Serialize(new { number, label }, ProcessorConfig.SerializerOptions);
await this.SpawnToPost(this.NewResult(StepOutcome.Completed, data), Guid.NewGuid());  // NewResult(string) UTF-8-encodes
```

### Downstream schema-lens read (unchanged — byte[] parse)
```csharp
// Source: src/Processor.Sample/SampleProcessor.cs:77 — JsonDocument.Parse accepts byte[]
using var parsed = JsonDocument.Parse(validatedData);   // validatedData : byte[]
var incomingNumber = parsed.RootElement.GetProperty("number").GetInt32();
```

### Recovery round-trip (DATA-05) — no code change, verified by the sweep's fault scenarios
`KeeperInject.DataResult` embeds the whole `DataResult`; STJ base64s `Data` on the RabbitMQ wire and restores the exact bytes on deserialize. `InjectConsumer.cs:42` writes `dr.Data` (byte[]→RedisValue) with no edit. TEST-02..07 drive this path.

## State of the Art

| Old Approach | Current Approach | Impact |
|--------------|------------------|--------|
| `string Data` as the payload spine (UTF-8 JSON only) | `byte[] Data` ground truth; JSON/UTF-8 an optional schema lens | Binary payloads (Phase 85) flow without base64/coercion; string remains a convenience view |
| Base64 on the hot path for any binary | Raw bytes in L2; base64 ONLY transiently on the JSON bus wire | Eliminates hot-path base64 (DATA-01) |

**Deprecated/outdated:** the rejected claim-check / external-blob-store pattern (REQUIREMENTS §Out of Scope) — superseded by the widened `byte[]` spine.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | STJ serializes `byte[]` as base64 by default and round-trips losslessly over MassTransit-JSON | Standard Stack / DATA-05 | If a custom serializer/converter were configured for `DataResult`, recovery could differ — but no such converter exists on `DataResult` (verified: `// default STJ serialization` comment on the record). The sweep's recovery scenarios (TEST-02..07) would catch any wire loss. LOW risk. |
| A2 | `Utf8JsonReader`/`JsonDocument.Parse(byte[])` throws `JsonException` (not a different type) on invalid UTF-8, routing D-04 through the existing catch | Pattern 2 / DATA-04 | If it threw a different exception, bad-UTF8 could crash instead of Failing. Mitigation: the planner should add ONE hermetic fact feeding invalid UTF-8 bytes under a schema and asserting `Failed` (cheap, and it keeps an existing-style hermetic green — not a new gate). MEDIUM until that fact exists. |

**Both assumptions are cheap to convert to VERIFIED** via a single hermetic fact each; neither blocks planning.

## Open Questions (RESOLVED)

1. **OQ-1 — [RESOLVED: keep `NextStepHandoff.Data` as `string`, out of scope for Phase 84]** Ruled by the orchestrator before planning and ratified in all three PLAN.md scope fences: `DataResult.Data` widens to `byte[]`; `NextStepHandoff.Data` (the orchestrator relocate blob) STAYS `string`. DATA-05 is satisfied in the no-loss sense (`KeeperInject` embeds the literal `byte[]` `DataResult`; the UTF-8 relocate is byte-for-byte lossless for the sweep). Literal byte[] widening of the orchestrator relocate is deferred to Phase 85 (when true binary must relocate through the orchestrator). Original question retained below for the record.
   - What we know: `KeeperInject` embeds `DataResult` → literally `byte[]` (DATA-05 satisfied directly). `OrchestratorInject` embeds `NextStepHandoff` (string). For all sweep content (UTF-8 JSON) the string relocate is byte-for-byte lossless, so the sweep passes either way. CONTEXT does NOT list `OrchestratorPrePipeline`/`RelocateTail`/`NextStepHandoff` as touch-points.
   - What's unclear: whether DATA-05's phrasing "OrchestratorInject carries byte[]" is a literal typing requirement or a "no-loss" requirement.
   - **Recommendation:** Keep `NextStepHandoff.Data` as `string` in Phase 84 (minimal blast radius, sweep-lossless, CONTEXT-aligned) and record the literal byte[] widening of the orchestrator relocate as a deferred item for whenever *true binary* must relocate *through* the orchestrator (Phase 85 / MANIP-01). The planner should confirm this ruling explicitly before wave 2, since it draws the exact scope line.

2. **OQ-2 — [RESOLVED: `DataAsString` extension in `Messaging.Contracts` + `NewResult(StepOutcome, byte[])` overload]** Adopted in Plan 01: an extension method (`DataAsString`) in `Messaging.Contracts` keeps `DataResult` a pure data record and signals "edge conversion"; a `NewResult(StepOutcome, byte[])` overload on `BaseProcessor` covers the write side (and the `string` overload UTF-8-encodes).

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| .NET SDK (net8.0) | build | ✓ (assumed — repo builds) | 8.0 | — |
| BaseApi.Tests.exe (hermetic run) | wave verification | ✓ | — | run directly with `--filter-not-trait Category=RealStack` ([[hermetic-test-command]]) |
| Docker + Redis/RabbitMQ/Postgres/ES | the 7-scenario sweep (D-05 gate) | ✗ in this sandbox | — | The sweep must run on a Docker-capable host; ~272 pre-existing RealStack infra failures in the Docker-less sandbox are NOT change-caused (0 analyzer/keeper LOGIC facts among them) |
| `pwsh` (`scripts/phase-68-sweep.ps1`) | D-05 gate | ✓ (script present) | — | — |

**Missing with no fallback:** Docker-backed infra for the sweep — the phase VERDICT cannot be produced in the Docker-less sandbox; the sweep runs on the target host. Hermetic compile+green is the in-sandbox proxy the waves use.

## Validation Architecture

> Framed per the phase mandate: **sweep-as-gate + byte-for-byte-equivalence invariant + keep existing hermetic facts green.** NOT a new coverage suite.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit (Microsoft.Testing.Platform) + FluentAssertions, net8.0 |
| Hermetic run command | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (run the built exe directly — `dotnet test` hangs on Windows MTP, [[hermetic-test-command]]) |
| Phase gate (verdict) | `pwsh -File scripts/phase-68-sweep.ps1` all-PASS (every `Missing==0`, `Duplicates==0`) — run detached |

### Phase Requirements → Verification Map
| Req ID | Behavior | Test Type | Command / Mechanism | Exists? |
|--------|----------|-----------|---------------------|---------|
| DATA-01 | byte[] flows L2/seam/recovery | hermetic + sweep | existing PrePipeline/OutputTail/SampleProcessor facts compile+green; sweep exercises live | ✅ existing (facts migrate via 4 edits) |
| DATA-02 | schema present decodes; absent opaque | hermetic | existing schema-validator + PrePipeline input-fail facts; **Wave 0: add 1 fact — invalid-UTF8-under-schema → Failed (A2)** | ⚠️ 1 new fact |
| DATA-03 | byte-for-byte equivalence; Sample green | hermetic + sweep | `SampleProcessorFacts` (transparent) + the sweep reproducing the all-PASS baseline IS the equivalence proof | ✅ existing |
| DATA-04 | bad UTF-8/JSON under schema → Failed | hermetic | reuses `ProcessorJsonSchemaValidator` malformed path; covered by the DATA-02 new fact | ⚠️ same 1 new fact |
| DATA-05 | recovery carries byte[] losslessly | sweep | `InjectConsumerFacts` (transparent) + TEST-02..07 recovery scenarios; **optional Wave 0: add 1 `DataResult` STJ round-trip fact asserting base64 + structural byte equality (A1)** | ✅ existing (+1 optional) |

### Sampling Rate
- **Per task commit:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (hermetic must stay green; expect ~272 pre-existing RealStack infra failures to be excluded/ignored — 0 logic facts among them).
- **Per wave merge:** full hermetic filtered run.
- **Phase gate:** `scripts/phase-68-sweep.ps1` all-PASS, detached, reseed-FIRST, trust the harness exit code.

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Processor/…` — one fact: invalid-UTF8 bytes under an input/output schema → `StepOutcome.Failed` (closes A2, exercises D-04). Keep it an existing-style hermetic fact, NOT a new gate.
- [ ] (optional) `tests/BaseApi.Tests/Contracts/…` — one fact: `DataResult` STJ serialize→deserialize preserves `Data` bytes (base64 on the wire), closing A1 for DATA-05.
- [ ] Migrate the 4 compile-break test sites (DispatchTestKit, PrePipelineFacts, SC2RecoveryPathsE2ETests) so the hermetic suite compiles.

*(No new test framework or fixtures needed — existing infrastructure covers all phase requirements plus the two small facts above.)*

## Security Domain

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V5 Input Validation | yes | `ProcessorJsonSchemaValidator` — the byte[] decode must stay schema-gated; malformed/hostile bytes → business `Failed`, never a host crash (D-04). The existing SSRF lockdown (`SchemaRegistry.Global.Fetch = null`) is untouched. |
| V6 Cryptography | no | — (no crypto; byte[] is opaque transport, not encrypted content) |
| V2/V3/V4 (authn/session/access) | no | Internal framework contract change; no auth surface. |

### Threat patterns for this change
| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malformed/hostile bytes under a schema (newly possible with `byte[]`) | DoS / Tampering | `JsonDocument.Parse(byte[])` throws `JsonException` → `TryValidate` returns false → business `Failed`; never crashes the host (D-04, verified path). |
| Payload leakage into logs (FW-03) | Information disclosure | The framework never logs `dr.Data`/`validatedData` (verified: all log sites carry ids/outcome only). The byte[] change must NOT add any payload logging — `FrameworkNoPayloadFacts` guards this. |

## Sources

### Primary (HIGH confidence — direct source read this session)
- `src/Messaging.Contracts/{DataResult,NextStepHandoff,KeeperInject,OrchestratorInject}.cs`
- `src/BaseProcessor.Core/Processing/{ProcessorPipeline,OutputTail,BaseProcessor,BaseProcessor`1,PostProcessConsumer}.cs`
- `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs`
- `src/Keeper/Recovery/{InjectConsumer,OrchestratorInjectConsumer}.cs`
- `src/Orchestrator/Dispatch/{OrchestratorPrePipeline,RelocateTail}.cs`
- `src/Processor.Sample/SampleProcessor.cs`
- `tests/BaseApi.Tests/**` — grep inventory of every `.Data`/`DataResult`/`ProcessAsync`/`NewResult` site (Contracts, Processor, Orchestrator, Keeper)
- `scripts/phase-68-sweep.ps1` (header + exit-code table); `.planning/{CONTEXT,REQUIREMENTS,STATE}.md`

### Secondary (MEDIUM confidence — established .NET behavior, cross-checked against source usage)
- StackExchange.Redis `RedisValue` implicit `string`/`byte[]` conversions (confirmed by unchanged-compiling write sites)
- System.Text.Json `byte[]`→base64 default serialization; `JsonDocument.Parse(ReadOnlyMemory<byte>)` UTF-8 + `JsonException` on bad UTF-8
- xUnit `Assert.Equal` structural collection comparison for `byte[]`

## Metadata

**Confidence breakdown:**
- Blast radius / touch-points: HIGH — every reader/writer enumerated by grep + read.
- Equality-break reality: HIGH — verified NO whole-`DataResult` value-equality assertion and NO production `==` dependence exist.
- D-04 decode point: HIGH (mechanism) / MEDIUM (exception type A2 — cheap to verify with 1 fact).
- Recovery wire (DATA-05): MEDIUM-HIGH — STJ base64 is standard; A1 optionally verified with 1 fact; sweep exercises it live.
- Orchestrator boundary (OQ-1): HIGH that string is sweep-lossless; the SCOPE ruling is the planner's.

**Research date:** 2026-07-21
**Valid until:** stable — internal refactor against a pinned stack; re-verify only if `DataResult` gains a custom `JsonConverter` or the MassTransit serializer changes.
