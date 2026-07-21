# Phase 84: `byte[]` Data-Channel Widening - Context

**Gathered:** 2026-07-21
**Status:** Ready for planning

<domain>
## Phase Boundary

Widen the framework's data-carrying contract `DataResult.Data` from `string` to `byte[]`, making `byte[]` the ground-truth type of every step's payload. Framework-only change (Messaging.Contracts + BaseProcessor.Core + Keeper recovery + the one concrete `Processor.Sample`); **no Kafka, no KafkaImporter** — that is Phase 85, which depends on this. A JSON schema, when configured, is an optional UTF-8/JSON *lens* over the bytes (decode + validate); with no schema the bytes are opaque. Base64 is eliminated on the hot path.

**In scope:** the `byte[]` contract change and every consumer that reads/writes `DataResult.Data` or the seam's `validatedData` — `DataResult.cs`, `BaseProcessor`1.cs`/`BaseProcessor.cs` (seam), `ProcessorPipeline` (L2 read), `OutputTail` (L2 write), `ProcessorJsonSchemaValidator` (schema-gated decode), `InjectConsumer`/`OrchestratorInjectConsumer` (recovery), `Processor.Sample` (migrated to stay green), and the hermetic tests that assert on `.Data`.

**Out of scope:** anything Kafka/importer (Phase 85); switching the MassTransit serializer away from JSON (byte[] base64s transiently on the RabbitMQ wire — accepted); any new test suite as the acceptance gate.
</domain>

<decisions>
## Implementation Decisions

### Data field shape
- **D-01:** Replace `string Data` with `byte[] Data` outright — `byte[]` is the single ground-truth field. Do NOT add a parallel `byte[]? Binary` field + discriminator: that reintroduces the rejected type-switch and creates two sources of truth (serialization/recovery ambiguity). One field, always bytes.

### Author / test ergonomics
- **D-02:** Add THIN UTF-8 convenience helpers so authors and existing hermetic tests are not forced into raw `Encoding.UTF8.GetBytes/GetString` everywhere: an encode-on-write path (e.g. a `NewResult(StepOutcome, string)` overload that UTF-8-encodes) and a decode-on-read UTF-8 string accessor (e.g. `DataAsString` or an extension). These are EDGE CONVERSIONS ONLY — the stored/wire type stays `byte[]`; string is a convenience view, NOT a return to string-as-truth. Keep the helper set minimal.

### Migration sequencing
- **D-03:** Staged waves, but **no temporary string↔byte bridge**. A throwaway bridge adds complexity that must then be removed and leaves a half-migrated state the byte-for-byte invariant can't cleanly check. Suggested (planner's discretion on exact waves): wave 1 = contract + convenience helpers; wave 2 = consumers + `Processor.Sample` + recovery; wave 3 = run the sweep. The LOCKED decision is "no bridge," not the specific wave count.

### Non-UTF8 / bad-bytes under a schema
- **D-04:** When a schema IS configured but the bytes are not valid UTF-8/JSON (newly possible with `byte[]`), treat it as a business `Failed` — the SAME path today's malformed-JSON already takes (`ProcessorJsonSchemaValidator.cs:46-51` returns false → `Failed`). Try UTF-8 decode; on failure return false with a "not valid JSON/UTF-8" message → `Failed`. No new error surface. This can only arise UNDER a schema — schema-absent bytes are never decoded.

### Verification (locked upstream — restated for the planner)
- **D-05:** The SOLE acceptance gate is the existing 7-scenario fault-recovery sweep (TEST-01..TEST-07, `scripts/phase-68-sweep.ps1`) reproducing its all-PASS baseline (every `Missing==0`, every `Duplicates==0`). No new test suite is the gate. The safety invariant is byte-for-byte equivalence of the existing string/JSON path: a value written as a string equals its UTF-8 bytes read back; `Processor.Sample` stays green. Existing hermetic tests must still compile and pass (via D-02 helpers), but the *phase verdict* is the sweep.

### Claude's Discretion
- Exact wave breakdown within the D-03 staging.
- Exact naming/shape of the D-02 helpers (overload vs. extension vs. static factory), as long as byte[] stays the stored type.
- Whether recovery's byte[] over the RabbitMQ wire warrants any size note (MassTransit base64 is accepted as transient).
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements
- `.planning/REQUIREMENTS.md` — DATA-01..DATA-05 (this phase's requirements) + Out of Scope (claim-check rejected in favor of the widened spine).

### Framework contract + consumers (the touch-points)
- `src/Messaging.Contracts/DataResult.cs` §`Data` (line ~15) — the field to change `string`→`byte[]`; note it is described as the unified spine (ProcessAsync return + `-post` wire contract + `KeeperInject` body + L2 value).
- `src/BaseProcessor.Core/Processing/BaseProcessor`1.cs` — the `ProcessAsync`/`ExecuteAsync` seam `validatedData` param.
- `src/BaseProcessor.Core/Processing/BaseProcessor.cs` — non-generic `ExecuteAsync` signature; `NewResult` factory (helper site for D-02).
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` §127/132 — L2 read into `validatedData`.
- `src/BaseProcessor.Core/Processing/OutputTail.cs` §82 — L2 write of `dr.Data`.
- `src/BaseProcessor.Core/Validation/ProcessorJsonSchemaValidator.cs` §34-51 — schema-absent skip + malformed-data→`Failed` path (the D-04 reuse point).
- `src/Keeper/Recovery/InjectConsumer.cs` §41-42 + `src/Keeper/Recovery/OrchestratorInjectConsumer.cs` — recovery writes `dr.Data` (must carry `byte[]`).
- `src/Processor.Sample/SampleProcessor.cs` — the one concrete author; migrate its JSON output to `byte[]` via D-02 helper, must stay green.

### Verification
- `scripts/phase-68-sweep.ps1` — the 7-scenario sweep (TEST-01..07); the sole acceptance gate (D-05). See memory [[long-sweep-detached-process]] (run ~2h sweeps detached) and [[rebuild-sourcehash-reseed-order]] (reseed-first runbook) and [[phase-68-sweep-stale-report-read]] (trust harness exit code).
</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `ProcessorJsonSchemaValidator.TryValidate` already returns bool (skip on empty schema; false→`Failed` on malformed) — D-04 reuses this exact contract, just adding a UTF-8 decode step ahead of the JSON parse.
- `DataResult.NewResult` factory + `ProcessorConfig.SerializerOptions` — the natural home for the D-02 encode-on-write helper.
- Redis (`StackExchange.Redis`) `StringGet/StringSet` are already binary-safe (`RedisValue` ↔ `byte[]`) — the storage layer needs no change; only the C# types that currently decode to `string` do.

### Established Patterns
- `DataResult` is `sealed record` with `init` props — the field change is a one-line type change plus consumer fixups; records give value-equality that the hermetic tests rely on (byte[] equality is REFERENCE-based, so tests comparing `.Data` may need `SequenceEqual` or the D-02 string accessor — flag for the planner).
- The framework never logs payloads (FW-03) — the byte[] change must not introduce any payload logging.

### Integration Points
- The `-post` queue hand-off and `KeeperInject` carry `DataResult` over RabbitMQ (MassTransit JSON) — a `byte[]` field base64s automatically on that wire; no code change needed, but recovery round-trip must be exercised by the sweep (TEST-* fault scenarios drive it).

### Watch-out (from D-02 / record equality)
- Changing `Data` to `byte[]` silently breaks value-equality-based test assertions and any `dr with { Data = ... }` comparisons: `byte[]` uses reference equality inside the record, so two records with equal bytes are no longer `==`. The planner must account for this in the test migration (use the D-02 string accessor or `SequenceEqual`).
</code_context>

<specifics>
## Specific Ideas

- byte[] is ground truth; "string was always just the UTF-8 view of the same bytes" — the migration exposes bytes Redis already stored, it does not add a new representation.
- Keep D-02 helpers minimal and clearly edge-only, so a future reader never mistakes the string accessor for the canonical type.
</specifics>

<deferred>
## Deferred Ideas

- KafkaImporter (KIMP-01..06) — Phase 85, depends on this byte[] channel.
- File-manipulator step (MANIP-01) and KafkaExporter (KEXP-01) — future milestones.
- Switching MassTransit to a binary serializer to eliminate even the transient wire base64 — out of scope; bus-wide change not warranted by this phase.

None of the above were scope creep in-discussion — they are the known forward path recorded in REQUIREMENTS.md.
</deferred>

---

*Phase: 84-byte-data-channel-widening*
*Context gathered: 2026-07-21*
