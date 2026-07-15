# Phase 73: Verify & Instrument End-to-End L2 Data Delivery (Fan-In Terminal G) - Pattern Map

**Mapped:** 2026-06-17
**Files analyzed:** 9 (1 src edit, 1 in-test data, 4 extend-in-place auditor, 2 new hermetic kit pieces, 1 new script)
**Analogs found:** 9 / 9 (every new/modified file has a concrete in-repo analog)

> Source of file list: `73-CONTEXT.md` `<canonical_refs>` "Code touchpoints" + `<decisions>` D-01..D-13. No RESEARCH.md (research skipped) — all patterns drawn from the live codebase.

> **Hard constraint reminder for the planner:** exactly ONE `src/` file may change (`SampleProcessor.cs`, inside `ProcessAsync` only). Everything else is test/script code. The real pipeline classes (`ProcessorPipeline`, `OutputTail`, `OrchestratorPrePipeline`) are DRIVEN, never modified.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `src/Processor.Sample/SampleProcessor.cs` (MODIFY `ProcessAsync` `:39-76`) | processor seam | transform | the file itself (extend-in-place) | exact (self) |
| `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` (MODIFY `:64-216`,`:277-330`) | test (seeder data) | CRUD (REST seed) | the file itself (extend-in-place) | exact (self) |
| NEW dict-backed `IDatabase` fake (in `DispatchTestKit.cs` or a new kit) | test double (stateful store) | CRUD / key-value | `tests/BaseApi.Tests/Keeper/FakeRedis.cs` | exact (stateful `IDatabase` fake) |
| NEW hermetic DAG harness driver (new test class) | test (integration driver) | event-driven / feedback-loop | `tests/BaseApi.Tests/Processor/PrePipelineFacts.cs` + `Orchestrator/OrchestratorPrePipelineFacts.cs` | role + flow match (drives the real classes) |
| `tests/BaseApi.Tests/Processor/DispatchTestKit.cs` (`CapturingSendProvider` reuse) | test kit | capture/record | the file itself (`CapturingSendProvider` `:344-390`) | exact (reuse) |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` (MODIFY `:50-54`,`:98-119`) | engine (pure arbiter) | transform | the file itself (extend-in-place) | exact (self) |
| `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` (MODIFY `FromLabels`,`HasAnyDuplicateLabel`) | model (pure) | transform | the file itself (extend-in-place) | exact (self) |
| `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` (ADD fields) | model (DTO) | transform | the file itself (extend-in-place) | exact (self) |
| `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` (MODIFY `BuildRunTraces` `:275-310`, mirror `TryReadSum` `:317-325`) | test fixture (RealStack) | request-response (ES/Prom read) | the file itself (extend-in-place) | exact (self) |
| NEW `scripts/phase-73-sweep.ps1` | script (ops sweep) | batch/orchestration | `scripts/phase-68-sweep.ps1` | exact (sweep-script analog) |

---

## Pattern Assignments

### `src/Processor.Sample/SampleProcessor.cs` (processor seam — the ONLY `src/` edit) — EXTEND IN PLACE

**Analog = the file itself.** The whole class is 77 lines; the edit is confined to `ProcessAsync` `:45-75`. Do NOT change the constructor, the DI remark, the framework calls (`SpawnToPost`/`DeleteEntry`/`NewResult`), or add a second log line (D-03).

**Mode-2 seed — change `:49-54`.** Today (RANDOM, the thing to remove):
```csharp
var numbers = new int[2];
for (var i = 0; i < 2; i++)
    numbers[i] = baseNumber + Random.Shared.Next(0, 100);   // 0..99 inclusive; independent per execution

logger.LogInformation("{StepLabel} had the following numbers: {Numbers}",
    label, string.Join(", ", numbers));
```
Target (D-01/D-03): seed the two FIXED values `100` and `200` (drop `Random.Shared`, drop `baseNumber` for the seed), log the seeded values. Keep the `foreach … SpawnToPost(…, Guid.NewGuid()) … DeleteEntry() … return null` spawn body verbatim (`:56-62`).

**Mode-1 increment + log — change `:65-71`.** Today:
```csharp
using var parsed = JsonDocument.Parse(validatedData);
var incomingNumber = parsed.RootElement.GetProperty("number").GetInt32();
var accumulated    = incomingNumber + baseNumber;

logger.LogInformation("{StepLabel} had the following numbers: {Numbers}",
    label, accumulated.ToString());
```
Target (D-02/D-03): keep `accumulated = incomingNumber + baseNumber` (with every seeded payload `number = 1`, `baseNumber` becomes the `+1` per hop — this is the seeder data change, NOT a code change). REPLACE the single log line with the `received → produced` clarifier carrying `StepLabel` + the two integers, e.g. `"{StepLabel} received {Received} produced {Produced}"` with `incomingNumber` and `accumulated`. EXTEND the existing line — do NOT add a second `LogInformation`. The `ExecutionId`/`CorrelationId` ride the ambient `ExecutionLogScope` already (no extra args). The structured field names you pick here are what the live auditor reads back as ES `attributes.*` (see D-11 below — coordinate the names).

**Warning-clean guard** is already present (`:42` null-config default). Preserve it.

---

### NEW stateful dict-backed `IDatabase` fake (D-08) — analog `tests/BaseApi.Tests/Keeper/FakeRedis.cs`

This is the keystone new asset. **The closest existing analog is a STATEFUL fake that already exists** — `FakeRedis` (`tests/BaseApi.Tests/Keeper/FakeRedis.cs`). It is the contrast-and-template: it is `ConcurrentDictionary`-backed and NSubstitute-wires the `IDatabase` ops, but its dictionaries back *counters/TTL state* for a fault model, not a general key→value store. The new fake keeps its **structure** (a `FakeRedis`-style holder exposing `Multiplexer` + `Database`, `ConcurrentDictionary` state, NSubstitute `IDatabase`) but makes the dictionary the **actual L2 blob store** so a write under a key is visible to every later read of that key (the multi-hop round-trip requirement, SPEC Constraint "the dict-backed L2 must be stateful across the full multi-hop DAG").

**Contrast (what NOT to copy): the `DispatchTestKit` muxes** (`DispatchTestKit.cs:191-297`) are STATELESS, single-arrival per scenario — `StubReads` resolves a fixed `IReadOnlyDictionary`, writes return a constant `true` and are never read back. Those prove fault routing; they cannot carry a value hop-to-hop. D-08 explicitly rejects them for the harness.

**Holder shape to copy** (`FakeRedis.cs:33-75`):
```csharp
public sealed class <Name>
{
    private readonly ConcurrentDictionary<RedisKey, RedisValue> _store = new();  // the L2 blob store
    public IConnectionMultiplexer Multiplexer { get; }
    public IDatabase Database { get; }
    public <Name>() { Database = BuildDatabase(); Multiplexer = BuildMultiplexer(Database); }
    // ... BuildMultiplexer is verbatim FakeRedis.cs:208-213
}
```

**Multiplexer wrap — copy verbatim** (`FakeRedis.cs:208-213`, identical to `DispatchTestKit.Wrap` `:143-148`):
```csharp
private static IConnectionMultiplexer BuildMultiplexer(IDatabase db)
{
    var mux = Substitute.For<IConnectionMultiplexer>();
    mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
    return mux;
}
```

**The four stateful ops the pipeline actually calls** (D-08 lists exactly these). The KEYS are built by `L2ProjectionKeys` (`ExecutionData(entryId)` = `skp:data:{guid:D}`, `OutputData(messageId)` = `skp:out:{guid:D}`). Wire each NSubstitute return against `_store`:

- `StringGetAsync` — used by `ProcessorPipeline.cs:88` (read `data:`) and `OrchestratorPrePipeline.cs:109` (read `out:`). Stub the 2-arg real virtual overload (`FakeRedis.cs:141-142` shows the exact signature):
```csharp
db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
    .Returns(ci => Task.FromResult(_store.TryGetValue((RedisKey)ci[0], out var v) ? v : RedisValue.Null));
```
- `StringSetAsync` — used by `OutputTail.cs:66` as the 3-arg `StringSetAsync(key, value, ttl)`. **CRITICAL overload note (from `DispatchTestKit.cs:114-189`):** in SE.Redis 2.13 the 3-arg write binds to a REAL virtual overload; the `TimeSpan?`/`When` shorthands are extension methods NSubstitute cannot intercept. `FakeRedis.cs:144-147` stubs the `(RedisKey, RedisValue, TimeSpan?, When, CommandFlags)` form; `DispatchTestKit.StubWriteOk` `:160-167` stubs the `(RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags)` keepTtl 6-arg form. To be safe, stub BOTH real virtual overloads to write `_store[key] = value` and return `true` (Pitfall 1 / T-70-11: a single-overload stub false-greens). TTL can be ignored (hermetic, no expiry needed).
- `KeyExistsAsync` — used by `ProcessorPipeline.cs:80` (the gate). `_store.ContainsKey(key)`:
```csharp
db.KeyExistsAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
    .Returns(ci => Task.FromResult(_store.ContainsKey((RedisKey)ci[0])));
```
- `KeyDeleteAsync` — used by `ProcessorPipeline.cs:171` (delete `data:` entry) and `OrchestratorPrePipeline` (delete `out:`). `_store.TryRemove(key, out _)` — pattern from `FakeRedis.cs:197-203`.

**Pre-seed the source input.** A SOURCE dispatch (`entryId == Guid.Empty`) skips the gate/read with empty `validatedData` (`ProcessorPipeline.cs:71-74`); downstream hops gate+read `data:`. The driver writes the next hop's `data:` blob into `_store` before dispatching that hop (the feedback loop, below).

---

### NEW hermetic DAG harness driver (D-08/D-09) — analogs `PrePipelineFacts.cs` + `OrchestratorPrePipelineFacts.cs`

**Closest analog for "drive the REAL classes directly":** `PrePipelineFacts.cs` already constructs and runs the real `OutputTail` + `ProcessorPipeline` (no MassTransit harness), and `OrchestratorPrePipelineFacts.cs` does the same for the real `OrchestratorPrePipeline`. The new driver chains them across the DAG with the stateful L2 and `CapturingSendProvider` between hops.

**The `Build` factory to copy** (`PrePipelineFacts.cs:27-35`) — this is the exact wiring of the two real processor classes:
```csharp
private static ProcessorPipeline Build(
    IConnectionMultiplexer redis, IProcessorContext context, BaseProcessorBase processor,
    DispatchTestKit.CapturingSendProvider send)
{
    var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3),
        DispatchTestKit.Options(300), DispatchTestKit.Metrics());
    return new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), tail,
        DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
}
```
- The REAL `SampleProcessor` is the `processor` arg here (not a `FakeProcessor`) — the harness must drive the real seam so the `+1` value math + the seeded `100`/`200` are exercised. Construct it via `new SampleProcessor(NullLogger<SampleProcessor>.Instance)`.
- `IProcessorContext` = `FakeProcessorContext` (`tests/BaseApi.Tests/Processor/FakeProcessorContext.cs`) — set `InputDefinition`/`OutputDefinition` to the sample's schemas (or null to skip validation, as `PrePipelineFacts.Ctx()` does).
- `redis` = the NEW dict-backed mux (NOT the `DispatchTestKit` muxes).
- `send` = `DispatchTestKit.CapturingSendProvider` (reused — see below).

**The orchestrator-side `Build`** — construct the real `OrchestratorPrePipeline` per `OrchestratorPrePipeline.cs:60-67` (needs `IWorkflowL1Store`, `StepAdvancement`, the dict mux, `CapturingSendProvider`, `DispatchTestKit.Retry(3)`, `OrchestratorMetrics`, `NullLogger`). See `OrchestratorPrePipelineFacts.cs:103-110` for the `Wrap` mux + how the facts assemble the L1 store and advancement doubles.

**Dispatch construction** — `DispatchTestKit.Dispatch(entryId, correlationId, payload)` (`DispatchTestKit.cs:326-331`) builds an `EntryStepDispatch`; `payload` carries the step config JSON (`{ number, label }`) the seam reads as `config`.

**The feedback loop (D-08/D-09 core):** drive A (source, `entryId == Guid.Empty`) → it `SpawnToPost`s 2 `DataResult`s captured in `send.SentData` (`CapturingSendProvider.cs:385`) → for each spawned execution, run the chain B…F: for each hop write the captured output value into `_store` under the next `data:` key, then dispatch the next step. At the fan-out (`C`), both `D1` and `F1`-branch + `D2`/`F2`-branch run. At `G`: feed BOTH `F1`'s and `F2`'s outputs in (each a distinct `messageId`/`entryId` → distinct `out:` then relocated `data:` key) and assert `G` runs exactly twice (D-09b), each reading its own `entryId` blob, writing its own `out:` blob, order-independent (run the two arrivals in both orders and compare).

**Assertions (D-09):** (a) per-hop value `B=seed+1 … G→seed+6` per execution; (b) `G` invoked exactly 2× per run, distinct `entryId` blobs, distinct `out:` blobs, no shared state, no join; (c) no cross-`executionId` contamination (a `100`-chain value never appears under the `200` chain). Read the written values back out of the dict store by key to assert.

**Capture-reuse note:** `CapturingSendProvider` records `IStepResult` → `Sent`, `IKeeperRecoverable` → `SentKeeper`, `DataResult` → `SentData`, plus envelope `MessageId`s → `SentMessageIds` (`DispatchTestKit.cs:344-390`). The driver feeds `SentData` (the `-post` spawns) and `Sent` (the `StepCompleted`/`NextStepHandoff` results) back as the next inputs.

---

### `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — EXTEND IN PLACE (D-10/D-11)

**Analog = the file itself.** Three precise edits:

1. **`AllLabels` (`:50-51`) → 10 labels incl. `Step_G`.** Add `"Step_G"` to the `HashSet`. Today:
```csharp
private static readonly HashSet<string> AllLabels = new(StringComparer.Ordinal)
{ "Step_A", "Step_B", "Step_C", "Step_D1", "Step_E1", "Step_F1", "Step_D2", "Step_E2", "Step_F2" };
```
2. **`LabelsPerRun` (`:54`)** — the Prom corroboration math (`promImpliedRuns = round(DispatchSentDelta / LabelsPerRun)` `:134`; `spawnReconSlack = CorroborationRunTolerance * LabelsPerRun` `:175`) divides by this. NOTE: `G` runs ×2 so dispatch math is no longer a clean ×labels-per-run multiple — but metrics are DEFERRED out of this phase (SPEC out-of-scope, D-tag "no metric-counter assertions"). Keep the corroboration math intact / inert; do not let `Step_G`'s ×2 break the **binding** verdict. The planner must decide whether `LabelsPerRun` stays 9 (dispatch-count basis) or becomes a separate constant — but the value-chain verdict must NOT depend on Prom.
3. **Convergent-terminal multiplicity (`:99`, `:119`).** Today COMPLETE is `r.DistinctLabels.SetEquals(AllLabels)` (`:99`) and DUPLICATE is `r.HasAnyDuplicateLabel` (`:119`, fail-closed). Teach the engine that `Step_G` has expected multiplicity **2** per `(corr, exec)`: COMPLETE still needs all 10 distinct labels, but the duplicate-fail must NOT trip on `Step_G` appearing exactly twice (the legitimate fan-in) WHILE still failing on (a) any OTHER label appearing twice and (b) `Step_G` appearing ≠ 2 times (1 = missing arrival, 3+ = same-`entryId` redelivery — D-07). The cleanest seam is to compute this in `RunTrace.FromLabels` (below) and expose a convergent-aware flag the engine reads instead of raw `HasAnyDuplicateLabel`.
4. **Value-chain assertion (NEW, D-11).** Add an assertion that each label's surfaced value equals `seed + hop-count`, both `Step_G` arrivals at `seed+6`, and the terminal `skp:out:` blob exists with the expected value. This needs per-step values on `RunTrace` (below). Fold a wrong-value / missing-terminal-blob into the FAIL verdict (`pass = missing == 0 && !dupFail && valueChainOk` at `:191`).

**Preserve** the entire ES-binding / Prom-corroboration structure (`:122-188`) and the `BuildSummary` shape (`:217-235`) — extend, do not rewrite.

---

### `tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs` — EXTEND IN PLACE (D-10/D-11)

**Analog = the file itself.** The pure model both the live fixture and hermetic facts build via `FromLabels` (`:59-84`). Two extensions:

1. **Convergent-label-aware duplicate handling.** Today `HasAnyDuplicateLabel = labels.Count != distinct.Count` (`:79`) — ANY repeat fails. Add convergent awareness so `Step_G` ×2 is legitimate but `Step_G` ×1 or ×3+ and any other-label repeat still flag. Pattern to follow: the existing `FromLabels` already counts occurrences via the `seen`/`dupes` loop (`:63-71`) — extend that loop to special-case the convergent label set (pass the convergent labels + expected multiplicity in, or hardcode `Step_G → 2` as a static like `AllLabels`). Keep the existing required-member record shape (`:27-51`) and the deterministic `OrderBy(Ordinal)` sort (`:82`).
2. **Per-step value surfacing (D-11).** Add a member carrying each label's logged value (e.g. `IReadOnlyDictionary<string,int>` or a per-label value list, mirroring how `Labels`/`DistinctLabels` are computed in `FromLabels`). `FromLabels`'s signature gains the values alongside `labels`. The live fixture fills this from the ES `attributes.*` value field; the hermetic facts fill it from synthetic values — proven once, shared (the whole point of this pure model).

---

### `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` — ADD FIELDS (D-11/D-12)

**Analog = the file itself.** A `required`-member serializable record (`:63-151`). Copy the existing field idiom (XML doc + `public required <T> <Name> { get; init; }`; enums get `[JsonConverter(typeof(JsonStringEnumConverter))]` as on `Verdict` `:69`). ADD:
- **Trip duration (D-12):** per-`(corr, exec)` trip duration ms + per-`correlationId` aggregate (first-step → `G`-terminal `@timestamp` delta). Field names are Claude's discretion (D-13 note). Mirror the shape of e.g. `StartedRuns`/`CompleteRuns` (`:77-88`).
- **Value-chain fields (D-11):** the per-exec expected-vs-actual value evidence + a pass/fail flag (so the report is trustworthy standalone before the assert, like the existing `Missing`/`Duplicates` evidence).
- Thread the new fields through the `new AnalyzerReport { … }` initializer in `PassFailEngine.Analyze` (`:195-214`) — every `required` member must be set there.

---

### `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — EXTEND IN PLACE (D-11/D-12)

**Analog = the file itself.** The RealStack fixture. Three precise extensions:

1. **Read the per-step value ES attribute (D-11)** — mirror the EXISTING defensive `TryReadSum` (`:317-325`) verbatim as the template; it is the documented pattern for reading a tolerant numeric `attributes.*` field:
```csharp
private static bool TryReadSum(JsonElement attrs, out int sum)
{
    sum = 0;
    if (!attrs.TryGetProperty("Sum", out var sumEl)) return false;
    if (sumEl.ValueKind == JsonValueKind.Number && sumEl.TryGetInt32(out sum)) return true;
    if (sumEl.ValueKind == JsonValueKind.String && int.TryParse(sumEl.GetString(), out sum)) return true;
    return false;
}
```
Add a sibling reader for the new `received`/`produced` attribute (the field name picked in `SampleProcessor` D-03). Today `BuildRunTraces` (`:275-310`) reads `CorrelationId`/`ExecutionId`/`StepLabel` and calls `_ = TryReadSum(attrs, out _)` informationally (`:296`) — change that to capture the value and thread it into the extended `RunTrace.FromLabels`.
2. **Group `Step_G` ×2 correctly.** `BuildRunTraces` already RETAINS duplicate labels in the per-instance list (`:298-304`) — so `Step_G` already lands twice in `Labels`; the engine/RunTrace change (above) interprets it. No structural change to the grouping itself.
3. **Trip duration from `@timestamp` (D-12).** Reuse the hits the fixture already pulls via `WindowTimestampFieldPath` (`BuildStepSearchBody` `:213-228`, sorted asc on that field) — compute the min→max `@timestamp` span per `(corr, exec)` and per `corr`. NO new ES query (D-12). The hits are already time-sorted, so first-hit/last-hit gives the span.

**Preserve** the window-pinning / drain / poll-to-stable / write-then-assert structure (`:84-203`) — extend `BuildRunTraces` + add the trip-duration computation; do not touch the Prom path.

---

### NEW `scripts/phase-73-sweep.ps1` — analog `scripts/phase-68-sweep.ps1`

**Copy the structure verbatim, re-target the stages.** `phase-68-sweep.ps1` is a thin run-all-and-collect driver. Reuse:
- The header doc-comment block + `param([string[]]$ScenarioIds = …)` shape (`:47-49`).
- `$ErrorActionPreference='Stop'`, `Set-StrictMode -Version Latest`, `$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path`, `Push-Location $repoRoot`/`finally { Pop-Location }` (`:51-56`, `:130-132`).
- The `Write-Phase` prefixed console helper (`:59-61`) — re-prefix `[phase-73-sweep]`.
- The child-process invocation idiom `& pwsh -File (Join-Path $PSScriptRoot '…') …; $code = $LASTEXITCODE` + the exit-class `switch` (`:76-86`) — NO fail-fast (D-13 "drives the live round-trip → invokes the auditor end-to-end").
- The analyzer-report discovery + roll-up table + `phase-73-summary.json` artifact write (`:90-128`).

**Re-target the stages (D-13):** the sweep must (a) seed the `G`-extended DAG (invoke `dotnet test --filter "FullyQualifiedName~FanOutSeeder"`), (b) drive the live round-trip, (c) invoke the auditor (`dotnet test --filter "Category=RealStack&FullyQualifiedName~Analyzer"` — the `AnalyzerE2ETests` contract documented at `AnalyzerE2ETests.cs:20-24`). The live Docker run is **deferred-automated** (SPEC req 7 / D-13) — the script must be syntactically runnable but is NOT a phase gate. Keep the "NEVER auto-retries, NEVER re-scores" anti-pattern guards from the analog header (`:36-39`).

---

## Shared Patterns

### NSubstitute `IConnectionMultiplexer` wrap
**Source:** `FakeRedis.cs:208-213` (identical to `DispatchTestKit.cs:143-148` and `OrchestratorPrePipelineFacts.cs:105-110`)
**Apply to:** the new dict-backed `IDatabase` fake + any harness mux.
```csharp
var mux = Substitute.For<IConnectionMultiplexer>();
mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);
```

### `IDatabase.StringSetAsync` overload trap (Pitfall 1 / T-70-11)
**Source:** `DispatchTestKit.cs:114-189` (the authoritative comment block) + `FakeRedis.cs:144-147`
**Apply to:** the dict-backed fake's write op. The 3-arg `StringSetAsync(key, value, ttl)` the production `OutputTail` calls binds to a REAL virtual overload; the `TimeSpan?`/`When` shorthands are EXTENSION methods NSubstitute cannot intercept (stubbing them leaks `RedundantArgumentMatcherException`). Stub BOTH real virtual overloads (the keepTtl 6-arg AND the 2.13 `Expiration`/`ValueCondition` 5-arg form) so a single-overload stub does not false-green.

### L2 key shapes (the round-trip contract)
**Source:** `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs:55,62`
**Apply to:** the dict store keys + the terminal anchor assertion.
- Input blob: `ExecutionData(entryId)` → `skp:data:{entryId:D}` (read by processor Pre `ProcessorPipeline.cs:88`).
- Output blob: `OutputData(messageId)` → `skp:out:{messageId:D}` (written by `OutputTail.cs:66`, read/relocated by orchestrator `OrchestratorPrePipeline.cs:109`). The persisted terminal `skp:out:` blob is the D-11 "terminal anchor" the auditor + hermetic harness assert.

### Pure-model shared construction (`RunTrace.FromLabels`)
**Source:** `RunTrace.cs:59-84`
**Apply to:** both the live fixture (`AnalyzerE2ETests.BuildRunTraces` `:307-309`) and the hermetic engine facts. The new value + convergent-multiplicity logic is added ONCE in `FromLabels` and proven by synthetic facts, so a green RealStack run is trustworthy not vacuous (the documented design intent, `PassFailEngine.cs:6-8`).

### Real-pipeline-class driver factory
**Source:** `PrePipelineFacts.cs:27-35` (processor side) + `OrchestratorPrePipelineFacts.cs:103-110` (orchestrator side)
**Apply to:** the new hermetic harness driver. Construct `OutputTail` then `ProcessorPipeline` then `OrchestratorPrePipeline` with `DispatchTestKit.Retry(3)` / `.Options(300)` / `.Metrics()` and `NullLogger<T>.Instance`, swapping the stateless muxes for the dict-backed one and the `FakeProcessor` for the REAL `SampleProcessor`.

### Thin sweep-script driver
**Source:** `scripts/phase-68-sweep.ps1` (whole file)
**Apply to:** `scripts/phase-73-sweep.ps1`. Run-all-collect, no fail-fast, child `pwsh -File` invocation, exit-class switch, roll-up JSON artifact.

---

## No Analog Found

None. Every new/modified file has a concrete in-repo analog. The one item that initially looked analog-less — the stateful dict-backed `IDatabase` (D-08) — is directly templated by the pre-existing stateful `tests/BaseApi.Tests/Keeper/FakeRedis.cs` (a `ConcurrentDictionary`-backed NSubstitute `IDatabase`/`IConnectionMultiplexer` pair), which the planner should treat as the structural starting point.

## Metadata

**Analog search scope:** `src/Processor.Sample`, `src/BaseProcessor.Core/Processing`, `src/Orchestrator/Dispatch`, `src/Messaging.Contracts/Projections`, `tests/BaseApi.Tests/{Processor,Orchestrator,Observability,Keeper}`, `scripts/`
**Files read in full or in part:** `SampleProcessor.cs`, `DispatchTestKit.cs`, `PassFailEngine.cs`, `RunTrace.cs`, `AnalyzerReport.cs`, `AnalyzerE2ETests.cs`, `phase-68-sweep.ps1`, `FanOutSeederE2ETests.cs`, `L2ProjectionKeys.cs`, `ProcessorPipeline.cs`, `OutputTail.cs`, `OrchestratorPrePipeline.cs`, `PrePipelineFacts.cs`, `OrchestratorPrePipelineFacts.cs`, `FakeProcessorContext.cs`, `Keeper/FakeRedis.cs`
**Project conventions:** no `CLAUDE.md`, no `.claude/skills`/`.agents/skills` directory present — patterns drawn purely from codebase precedent.
**Pattern extraction date:** 2026-06-17
