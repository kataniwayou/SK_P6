# Phase 48: v3.x Teardown - Pattern Map

**Mapped:** 2026-06-09
**Files analyzed:** 1 net-new + 8 edits + 11 whole-file deletes
**Analogs found:** 1 net-new mapped (exact) / 1 net-new total — all other work items are deletions/member-removals needing no analog, only the surrounding pattern to remove cleanly.

> **Phase shape:** PURE DELETION + remnant sweep. The single net-new file is the Phase-48 negative-guard fact class (§ Net-New). Every other "modified file" is a DELETE (whole-file) or an EDIT (member removal). The high-value output below is the **verbatim excerpts from `AtLeastOnceStructuralFacts.cs`** the planner must hand the executor so the new guard matches the established structural-fact idiom exactly.

---

## File Classification

| Work item | Disposition | Role | Data Flow | Closest Analog | Match Quality |
|-----------|-------------|------|-----------|----------------|---------------|
| `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` | **NET-NEW** | test (structural guard) | reflection + source-scan | `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` | **exact** |
| `src/Keeper/Program.cs` | EDIT (unwire) | config / composition-root | DI registration | self (lines 79–83 v4 block) | — |
| `src/Keeper/Recovery/L2ProbeRecovery.cs` | EDIT (partial-delete) | service (helper) | request-response (probe) | self (`ProbeOnceAsync` kept) | — |
| `src/Messaging.Contracts/KeeperQueues.cs` | EDIT (const removal) | config (const) | — | self (`Recovery` const kept) | — |
| `src/Keeper/ProbeOptions.cs` | EDIT (member removal) | config (options) | — | self | — |
| `src/Keeper/appsettings.json` | EDIT (key removal) | config | — | self | — |
| `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` | EDIT (builder removal) | utility (key builder) | — | self (`KeeperProbe`/`ExecutionData` kept) | — |
| `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` | EDIT (widen scan) | test | source-scan | self | — |
| `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` | EDIT (re-anchor) | test | reflection | self | — |
| `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` | EDIT (drop assertions) | test | const-assert | self | — |
| `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs` | EDIT (drop reactive regs) | test (fixture) | DI registration | self | — |
| `docs/design/2026-06-08-...redesign.md` | EDIT (A17 amendment) | doc | — | Phase 46/47 A15/A16 rows | exact-pattern |
| 5 reactive source files (§ Whole-File Deletes) | DELETE | consumer/handler | event-driven (`Fault<T>`) | — | n/a |
| `src/Keeper/Observability/KeeperMetrics.cs` | DELETE | observability | — | — | n/a |
| 6 orphaned test classes (§ Whole-File Deletes) | DELETE | test | — | — | n/a |

---

## Pattern Assignments

### NET-NEW: `tests/BaseApi.Tests/Resilience/ReactivePathRetiredFacts.cs` (test, reflection + source-scan)

**Analog:** `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` — **exact match** (same role, same two mechanisms, same repo-root anchor). Copy its structure verbatim and substitute the Phase-48 targets. Research §5 specifies FACT 1 (reflection: no `Fault<T>` consumer), FACT 2 (source-scan: no `keeper-fault-recovery`/`keeper-dlq` literal), FACT 3 (const-absence), plus the SC-2 `ExecutionData`-is-Guid-only assertion (§6 option 1).

**Imports / file-header pattern** (analog lines 1–6):
```csharp
using System.Reflection;
using System.Runtime.CompilerServices;
using Messaging.Contracts;
using Xunit;

namespace BaseApi.Tests.Resilience;
```
> For Phase 48 add `using MassTransit;` (for `IConsumer<>` / `Fault<>`), `using Messaging.Contracts.Projections;` (for `L2ProjectionKeys` SC-2 assertion). Keep `namespace BaseApi.Tests.Resilience;` to co-locate with the analog (Claude's-discretion name; `ReactivePathRetiredFacts` is the research-suggested name).

**Assembly-anchor pattern** (analog lines 23–28) — anchor on a **surviving public sealed class**, exactly as the analog anchors on execution-path types. The reactive `Fault*` types are gone, so anchor the Keeper assembly on a kept type (research §5: `BitHealthLoop`):
```csharp
// ── Execution-path assembly anchors (public sealed classes — verified loadable, firewall-test parity). ──
private static readonly Assembly Orchestrator =
    typeof(global::Orchestrator.Dispatch.StepDispatcher).Assembly;

private static readonly Assembly BaseProcessorCore =
    typeof(global::BaseProcessor.Core.Processing.ProcessorPipeline).Assembly;
```
> Phase-48 equivalent: `private static readonly Assembly Keeper = typeof(global::Keeper.Health.BitHealthLoop).Assembly;` — NOT a `Consumers.Fault*` type (deleted). This mirrors the same re-anchor the firewall test must do (below).

**Reflection-FACT idiom** (analog lines 43–65) — iterate `asm.GetTypes()`, assert absence by type-name and by member-name; `[Fact]` + `[Trait]` shape:
```csharp
[Fact]
[Trait("Phase", "47")]
public void No_dedup_machinery_on_execution_path()
{
    var assemblies = new[] { Orchestrator, BaseProcessorCore };

    foreach (var asm in assemblies)
    {
        var types = asm.GetTypes();

        // No MessageIdentity TYPE survives (Phase-43 RETIRE-01 deleted it).
        Assert.DoesNotContain(types, t => t.Name == "MessageIdentity");

        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        foreach (var type in types)
        {
            Assert.DoesNotContain(type.GetProperties(flags), p => p.Name == "MessageIdentity");
            Assert.DoesNotContain(type.GetFields(flags), f => f.Name == "MessageIdentity");
        }
    }
}
```
> Phase-48 FACT 1 reuses this exact iterate-and-`Assert.DoesNotContain` shape but tag `[Trait("Phase", "48")]`. Two checks per §5: (a) `Assert.DoesNotContain(types, t => t.Name is "FaultEntryStepDispatchConsumer" or "FaultExecutionResultConsumer" or "KeeperRecoveryHandler")`; (b) the stronger interface-shape check — for each type, `type.GetInterfaces()`, assert none is `IConsumer<>` whose single generic arg is a closed `MassTransit.Fault<>` (e.g. `i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IConsumer<>) && i.GetGenericArguments()[0].IsGenericType && i.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Fault<>)`).
> Phase-48 FACT 3 (const-absence) reuses the field-reflection idiom against `typeof(KeeperQueues).GetFields(...)`: assert no field named `FaultRecovery`/`DeadLetter`, assert a field named `Recovery` exists.
> Phase-48 SC-2 assertion: reflect `typeof(L2ProjectionKeys).GetMethods()` filtered to `Name == "ExecutionData"`, assert exactly one overload and its single parameter type is `typeof(Guid)`; assert no execution-path type name contains `"Manifest"`.

**Source-scan FACT idiom** (analog lines 83–111) — `RepoRoot()` anchor → `Path.Combine` scoped dir → **fail-loud `Directory.Exists` guard (T-47-01)** → `Directory.EnumerateFiles(..., "*.cs")` → `.Where(text-contains)` → `Assert.True(offenders.Count == 0, ...)`:
```csharp
[Fact]
[Trait("Phase", "47")]
public void No_v4_give_up_path_references_keeper_dlq()
{
    var repoRoot = RepoRoot();

    var processingDir = Path.Combine(repoRoot, "src", "BaseProcessor.Core", "Processing");
    var recoveryDir = Path.Combine(repoRoot, "src", "Keeper", "Recovery");

    // T-47-01 — fail loudly if the anchor resolved wrong (a silently-empty scan is a false pass).
    Assert.True(Directory.Exists(processingDir), $"Scoped dir not found (bad repo-root anchor?): {processingDir}");
    Assert.True(Directory.Exists(recoveryDir), $"Scoped dir not found (bad repo-root anchor?): {recoveryDir}");

    var offenders = Directory.EnumerateFiles(processingDir, "*.cs")
        .Concat(Directory.EnumerateFiles(recoveryDir, "*.cs"))
        .Where(f => Path.GetFileName(f) != "KeeperRecoveryHandler.cs")   // <-- Phase-48 REMOVES this line (file deleted)
        .Where(f =>
        {
            var text = File.ReadAllText(f);
            return text.Contains("KeeperQueues.DeadLetter") || text.Contains("keeper-dlq");
        })
        .ToList();

    Assert.True(
        offenders.Count == 0,
        "v4 give-up path(s) reference the retired keeper-dlq (RESIL-02 violation): "
            + string.Join(", ", offenders));
}
```
> Phase-48 FACT 2 reuses this idiom but scans **all of `src/Keeper/`** recursively (`Directory.EnumerateFiles(keeperDir, "*.cs", SearchOption.AllDirectories)`), with the same `Directory.Exists` fail-loud guard, and the offender literals are `"keeper-fault-recovery"`, `"keeper-dlq"`, `"KeeperQueues.FaultRecovery"`, `"KeeperQueues.DeadLetter"`. **No `KeeperRecoveryHandler.cs` exclusion** (the file is deleted — §5).

**Repo-root anchor pattern** (analog lines 113–125) — copy verbatim; it is the `[CallerFilePath]` walk-up-to-`SK_P.sln` helper that makes the scan path-independent and is the T-47-01 false-pass guard:
```csharp
private static string RepoRoot([CallerFilePath] string thisFile = "")
{
    var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SK_P.sln")))
        dir = dir.Parent;

    Assert.NotNull(dir); // SK_P.sln must be found by walking up from this test source file.
    return dir!.FullName;
}
```
> Copy this method into the new class unchanged (it self-anchors via `[CallerFilePath]` to the new file's own path).

---

### EDIT: `src/Keeper/Program.cs` (config / composition-root, DI registration)

**Removal targets** (research §3) — the executor removes these registration statements; the surrounding `AddSingleton`/`AddConsumer` idiom shows what a clean removal looks like:

**Reactive handler singleton to REMOVE** (line 48–49):
```csharp
// KHARD-03 — the shared recovery body both fault consumers delegate to (ctor-injects L2ProbeRecovery + KeeperMetrics).
builder.Services.AddSingleton<Keeper.Recovery.KeeperRecoveryHandler>();
```

**Metrics wiring to REMOVE** (lines 51–58, the whole `KMET-01` block incl. the comment, the `AddSingleton<KeeperMetrics>`, and the `ConfigureOpenTelemetryMeterProvider` line):
```csharp
builder.Services.AddSingleton<KeeperMetrics>();
builder.Services.ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(KeeperMetrics.MeterName));
```

**Reactive consumer registration to REMOVE** (line 71, inside the `AddBaseConsoleMessaging` lambda) — this is the MassTransit consumer-registration pattern; the executor deletes ONLY this line, leaving the five v4 lines below it:
```csharp
x.AddConsumer<FaultEntryStepDispatchConsumer, FaultEntryStepDispatchConsumerDefinition>();
```

**KEEP block — the v4 registration pattern that must survive verbatim** (lines 79–83):
```csharp
x.AddConsumer<Keeper.Recovery.UpdateConsumer,   Keeper.Recovery.UpdateConsumerDefinition>();
x.AddConsumer<Keeper.Recovery.ReinjectConsumer, Keeper.Recovery.ReinjectConsumerDefinition>();
x.AddConsumer<Keeper.Recovery.InjectConsumer,   Keeper.Recovery.InjectConsumerDefinition>();
x.AddConsumer<Keeper.Recovery.DeleteConsumer,   Keeper.Recovery.DeleteConsumerDefinition>();
x.AddConsumer<Keeper.Recovery.CleanupConsumer,  Keeper.Recovery.CleanupConsumerDefinition>();
```

**Usings to scrub** (lines 8–10) once last use is gone: `using Keeper.Consumers;`, `using Keeper.Observability;`, `using OpenTelemetry.Metrics;`.

**KEEP usings/registrations** (do not touch): line 41 `AddSingleton<...L2ProbeRecovery>()` (BitHealthLoop dep — ctor now `IConnectionMultiplexer`-only), line 44 `IL2HealthGate/L2HealthGate`, line 46 `AddHostedService<BitHealthLoop>()`, lines 25/29/33/37 the `Configure<RetryOptions/ProbeOptions/BackupOptions/RecoveryOptions>` binds. **Reword** the line 39–40 comment "Both fault consumers depend on it" → "the v4 BitHealthLoop's L2 probe helper."

---

### EDIT: `src/Keeper/Recovery/L2ProbeRecovery.cs` (service helper, partial-delete)

**REMOVE** (per kill-list §1B + boundary §2):
- `public enum ProbeOutcome { Recovered, GaveUp }` (line 10)
- `RunAsync(Guid entryId, string h, string procId, CancellationToken ct)` (lines 25–45 — the whole method)
- The `IOptions<ProbeOptions> opts` AND `KeeperMetrics metrics` ctor params (line 20)
- `using Keeper.Observability;` (line 3), `using Microsoft.Extensions.Options;` (line 5) once `opts`/`metrics` are gone

**KEEP — the surviving ctor + method must read** (lines 56–71 unchanged, ctor reduced to one param):
```csharp
public sealed class L2ProbeRecovery(IConnectionMultiplexer redis)
{
    private static readonly Guid BitProbeEntryId = Guid.Empty;
    private const string BitProbeH = "bit";

    public async Task<bool> ProbeOnceAsync(CancellationToken ct, Guid? entryId = null, string? h = null)
    {
        var db = redis.GetDatabase();
        try
        {
            _ = await db.StringGetAsync(L2ProjectionKeys.ExecutionData(entryId ?? BitProbeEntryId));
            var scratch = (RedisKey)L2ProjectionKeys.KeeperProbe(h ?? BitProbeH);
            await db.StringSetAsync(scratch, "1", expiry: TimeSpan.FromSeconds(30));
            await db.KeyDeleteAsync(scratch);
            return true;
        }
        catch (RedisException) { return false; }
    }
}
```
> **Build-order trap (§2):** the ctor signature change (`IConnectionMultiplexer`-only) MUST land in the SAME commit as the `KeeperMetrics.cs` deletion and the Program.cs `AddSingleton<KeeperMetrics>` removal — DI resolves `L2ProbeRecovery` by the surviving multiplexer singleton. KEEP `using Messaging.Contracts.Projections;` + `using StackExchange.Redis;`. Reword the class doc-comment (lines 12–19) to the BIT-only role (drop the "awaited inside Consume … RunAsync passes the real fault-context values" prose — §1D).

---

### EDIT: `src/Messaging.Contracts/KeeperQueues.cs` (config const, const removal)

**REMOVE** `FaultRecovery` (line 15 + its doc-comment lines 9–14) and `DeadLetter` (line 25 + its doc-comment lines 21–24). **KEEP** `Recovery = "keeper-recovery"` (line 19) but **fix its dangling `<see cref>`**: the line-18 doc-comment references `<see cref="FaultRecovery"/>` ("Distinct from the reactive `FaultRecovery` queue (retired 47/48)") which will not compile once `FaultRecovery` is removed — reword to drop the cref. The kept file should read:
```csharp
public static class KeeperQueues
{
    /// <summary>Gate-open-only recovery consumer queue — Phase 46 binds the Keeper-state consumers here.</summary>
    public const string Recovery = "keeper-recovery";
}
```
> Atomic-commit constraint (§7): this removal MUST share a commit with the `KeeperDlqConsolidationTests` assertion drop (the last referencer of `DeadLetter`) — sub-commit 2.

---

### EDIT: `src/Keeper/ProbeOptions.cs` (config options, member removal)

**REMOVE** `RecoverAttemptCap` (line 14 + its line-13 doc-comment). **KEEP** `DelaySeconds` + `MaxAttempts` (read by BitHealthLoop + `ProbeOptionsBoundTests`). Surviving class:
```csharp
public sealed class ProbeOptions
{
    public int DelaySeconds { get; set; } = 5;
    public int MaxAttempts  { get; set; } = 12;
}
```
> Paired edit (same sub-commit 2): remove `"RecoverAttemptCap": 3` under `"Probe"` in `src/Keeper/appsettings.json`.

---

### EDIT: `src/Messaging.Contracts/Projections/L2ProjectionKeys.cs` (utility key-builder, builder removal)

**REMOVE** the `KeeperRecoverAttempts` builder (lines 52–53, `skp:keeper:attempts:{h}`) + its doc-comment — referenced ONLY by the deleted `KeeperRecoveryHandler`. **KEEP** `KeeperProbe` (line 50, used by `ProbeOnceAsync`) and `ExecutionData(Guid)` (the GUID-only data builder the SC-2 assertion targets). `[research §1D — VERIFIED]`

---

### EDIT: `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs` (test, widen scan)

**REMOVE** the `KeeperRecoveryHandler.cs` exclusion (line 99): `.Where(f => Path.GetFileName(f) != "KeeperRecoveryHandler.cs")`. Update the FACT B doc-comment (lines 67–99) to drop the "retired Phase 48 … excluded" prose. After the edit the `src/Keeper/Recovery/` scan is unconditional. Re-run `--filter-trait "Phase=47"` to confirm still-green (the file it excluded is now deleted, so nothing references `keeper-dlq` under `Recovery/`). `[research §6]`

---

### EDIT: `tests/BaseApi.Tests/Keeper/KeeperDependencyFirewallTests.cs` (test, re-anchor)

**Current anchor (line 27–28) will not compile** once the type is deleted:
```csharp
private static readonly Assembly KeeperAssembly =
    typeof(global::Keeper.Consumers.FaultEntryStepDispatchConsumer).Assembly;
```
**Re-anchor on a surviving public Keeper type** (research §4 suggests `Keeper.Health.BitHealthLoop` or `Keeper.Recovery.ReinjectConsumer`):
```csharp
private static readonly Assembly KeeperAssembly =
    typeof(global::Keeper.Health.BitHealthLoop).Assembly;
```
> Same re-anchor decision as the new guard's FACT 1 — use the same surviving type in both for consistency.

---

### EDIT: `tests/BaseApi.Tests/Keeper/KeeperDlqConsolidationTests.cs` (test, drop assertions)

**REMOVE** the two `KeeperQueues.DeadLetter` assertions inside `Dlq_TopologyArgs` (lines 200, 203) — they reference the just-deleted const and stop compiling. Scrub the line-189 doc-comment mention of "DLQ-2 (keeper-dlq)". The remaining assertions (line 197 `Dlq1` const, line 207 TTL math) and the other facts (`Dlq1_Consolidated`, `Keeper_SendFault_RetriesToDlq1`, `ProcessorSendExhaustion_RoutesToDlq1`) are v4 and MUST stay green. Lines to remove:
```csharp
Assert.Equal("keeper-dlq", KeeperQueues.DeadLetter);
Assert.NotEqual(ConsolidatedErrorTransportFilter.Dlq1, KeeperQueues.DeadLetter);
```
> Atomic-commit (§7): same commit as the `KeeperQueues.DeadLetter` const removal.

---

### EDIT: `tests/BaseApi.Tests/Keeper/KeeperHostBootFixture.cs` (test fixture, drop reactive regs)

**REMOVE** (research §4, lines ~39–54) the two reactive consumer registrations + the `KeeperRecoveryHandler` singleton. Minimal edit = drop the `AddConsumer<Fault...>` + `AddSingleton<KeeperRecoveryHandler>()`; keep the `AddBaseConsole*` seam + `ProbeOptions`/`RetryOptions` binding + `L2ProbeRecovery`. `KeeperHostBootTests` only resolves `IBusControl` so either form re-runs green (Open Question 1 — planner's discretion). Mirrors the Program.cs unwiring above.

---

### EDIT: `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (doc, A17 amendment)

**Analog:** the Phase-46 (A15) / Phase-47 (A16) additive-amendment rows in the same doc's locked-decisions table. Add a top-of-doc `**Amended 2026-06-09 (A17):**` line + an `| A17 | reactive Fault<T> path + keeper-dlq retired in Phase 48; proven by 48-TEARDOWN-AUDIT.md |` row. Additive only — do not edit existing locked text (research §Open Question 2). Wording is executor's discretion (D-04).

---

## Whole-File Deletes (no analog needed — confirm the list is complete for `files_modified`)

**Reactive source (RETIRE-03, kill-list §1A + §1C):**
1. `src/Keeper/Consumers/FaultEntryStepDispatchConsumer.cs`
2. `src/Keeper/Consumers/FaultEntryStepDispatchConsumerDefinition.cs`
3. `src/Keeper/Consumers/FaultExecutionResultConsumer.cs`
4. `src/Keeper/Consumers/FaultExecutionResultConsumerDefinition.cs`
5. `src/Keeper/Recovery/KeeperRecoveryHandler.cs`
6. `src/Keeper/Observability/KeeperMetrics.cs` (the `KeeperMetrics` class **and** `KeeperMetricTags` in the same file — research §1C / A1: whole-file delete, no v4 meter usage exists)

**Orphaned test classes (D-01 / research §4 — all DELETE WHOLE):**
7. `tests/BaseApi.Tests/Keeper/KeeperFaultConsumerScopeTests.cs`
8. `tests/BaseApi.Tests/Keeper/KeeperRecoverCapTests.cs`
9. `tests/BaseApi.Tests/Keeper/KeeperRoundRobinTests.cs`
10. `tests/BaseApi.Tests/Keeper/KeeperPausePublishTests.cs` (not in 48-CONTEXT's named list — research §4 adds it)
11. `tests/BaseApi.Tests/Keeper/KeeperProbeLoopTests.cs` (no `ProbeOnceAsync`-only coverage to salvage — research §4)
12. `tests/BaseApi.Tests/Keeper/KeeperMetricsFacts.cs` (orphaned by the `KeeperMetrics.cs` delete — research §4 adds it)

> **Note on count:** 11 numbered files above = 5 reactive source + 1 observability + 6 test classes. (The §"Files analyzed" header counts the observability file within the 5 reactive-source bucket per research §1A/§1C grouping; the authoritative delete list is the 12 numbered entries here.)

---

## Shared Patterns

### Structural-guard idiom (reflection + source-scan)
**Source:** `tests/BaseApi.Tests/Resilience/AtLeastOnceStructuralFacts.cs`
**Apply to:** the net-new `ReactivePathRetiredFacts.cs` (verbatim), and the firewall re-anchor.
Three reusable elements: (1) assembly-anchor via `typeof(<surviving public sealed type>).Assembly`; (2) reflection absence-assertions via `asm.GetTypes()` + `Assert.DoesNotContain`; (3) source-scan via `RepoRoot()` + `Directory.Exists` fail-loud guard + `Directory.EnumerateFiles` + offender-list `Assert.True(count == 0, msg)`. Tag every new fact `[Trait("Phase", "48")]`.

### Fail-loud anchor (T-47-01 false-pass guard)
**Source:** `AtLeastOnceStructuralFacts.cs` lines 92–94 (`Assert.True(Directory.Exists(dir), ...)`) + lines 117–125 (`RepoRoot()` `[CallerFilePath]` walk).
**Apply to:** FACT 2 of the new guard. A mis-anchored scan silently finds zero offenders → false green; the existence guard prevents it. Copy `RepoRoot()` unchanged.

### MassTransit consumer-registration block
**Source:** `src/Keeper/Program.cs` lines 69–84 (`AddBaseConsoleMessaging(..., x => { x.AddConsumer<TConsumer, TDefinition>(); ... })`).
**Apply to:** the Program.cs unwiring (remove line 71 only) and the `KeeperHostBootFixture` edit (remove the matching reactive `AddConsumer<Fault...>`). The five v4 `keeper-recovery` lines are the keep-template.

### Additive design-doc amendment
**Source:** the A15/A16 rows in `docs/design/2026-06-08-processor-keeper-recovery-redesign.md` (Phase 46/47 precedent).
**Apply to:** the A17 retirement amendment — append a table row + top-of-doc amended-date line; never rewrite locked text.

### Audit-ledger layout
**Source:** `.planning/phases/47-dlq-consolidation-at-least-once-semantics/47-DLQ-AUDIT.md`.
**Apply to:** the D-04 `48-TEARDOWN-AUDIT.md` — one row per RETIRE-01/02/03 + SC-1..SC-4 → named proving guard test / source-scan (columns are Claude's discretion).

---

## No Analog Found

None requiring a fallback to RESEARCH.md patterns. The single net-new file has an exact in-repo analog (`AtLeastOnceStructuralFacts.cs`). All deletes need no analog; all edits operate on existing files whose surrounding pattern is captured above.

---

## Metadata

**Analog search scope:** `tests/BaseApi.Tests/Resilience/`, `tests/BaseApi.Tests/Keeper/`, `src/Keeper/`, `src/Messaging.Contracts/`, `docs/design/`
**Files read for excerpts:** `AtLeastOnceStructuralFacts.cs` (full), `Program.cs` (full), `L2ProbeRecovery.cs` (full), `KeeperQueues.cs` (full), `ProbeOptions.cs` (full), `KeeperDependencyFirewallTests.cs` (anchor), `KeeperDlqConsolidationTests.cs` (lines 185–209)
**Pattern extraction date:** 2026-06-09
