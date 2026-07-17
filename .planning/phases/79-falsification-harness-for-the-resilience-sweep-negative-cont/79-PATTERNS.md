# Phase 79: Falsification harness for the resilience sweep (negative-control) - Pattern Map

**Mapped:** 2026-07-17
**Files analyzed:** 5 (1 product-source modify, 1 config modify, 1 script modify, 1 new script, 1 optional test add)
**Analogs found:** 5 / 5 (every file has an in-repo analog named in CONTEXT.md and inlined below)

> NOTE: This is a backend .NET (net8.0) + PowerShell resilience phase. No frontend/UI. All analogs are in-repo. CONTEXT.md pins each analog with file:line — this document inlines the REAL code the executor must replicate so no re-open is needed.

---

## File Classification

| New/Modified File | Kind | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|------|-----------|----------------|---------------|
| `src/Keeper/Recovery/ReinjectConsumer.cs` | modify | product-source (keeper recovery consumer) | event-driven (MassTransit consume → L2 read → re-dispatch) | `src/Processor.Sample/SampleProcessor.cs:42-52` (env-gated fault hook) | exact (same seam idiom, adjacent tier) |
| `compose.yaml` | modify | config (keeper service env) | config plumbing (`${VAR:-default}` shell interpolation into container) | `compose.yaml:291-293` / `:328-330` (`PROCESSOR_STEP_DELAY_MS`) | exact |
| `scripts/phase-67-harness.ps1` (`$Scenarios` row) | modify | script (per-scenario orchestrator) | table-data add (one `[ordered]` row) | `phase-67-harness.ps1:104-106` (TEST-08/09/10 negative rows) | exact |
| `scripts/phase-79-falsify.ps1` | new | script (standalone falsification driver, INVERTED assertion) | request-response over child `pwsh -File` + exit-code classify | `phase-67-harness.ps1` (exit table + STEP H) + `phase-68-sweep.ps1:48-49,101-132` | role-match (mirror + invert) |
| `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` (optional new fact) | optional add | test (hermetic fact) | pure-function synthetic trace → `Analyze` assert | `PassFailEngineFacts.cs:243-261` / `:309-330` | exact |

---

## Pattern Assignments

### 1. `src/Keeper/Recovery/ReinjectConsumer.cs` (product-source, event-driven) — the suppress-send hook

**Analog:** `src/Processor.Sample/SampleProcessor.cs:42-52` (the ONE established env-gated product seam).

**Analog code to replicate (env read + default-off + "NEVER in production" comment style)** — `SampleProcessor.cs:42-52`:
```csharp
// TEST-ONLY fault hook (env-gated, DEFAULT OFF). When PROCESSOR_STEP_DELAY_MS > 0, hold each hop
// mid-consume BEFORE any log/spawn/result so this fast pipeline exposes a catchable in-flight window:
// the message stays unacknowledged for the delay (RabbitMQ queue depth > 0), and a kill during it
// freezes the execution mid-hop → started-but-incomplete → recoverable-but-lost FAIL. Unset/0 ⇒ NO
// delay, behaviour byte-for-byte unchanged. NEVER set in production (only the resilience harness
// exports it for the TEST-09 negative path).
if (int.TryParse(Environment.GetEnvironmentVariable("PROCESSOR_STEP_DELAY_MS"), out var stepDelayMs)
    && stepDelayMs > 0)
{
    await Task.Delay(stepDelayMs, ct);
}
```

**Where the hook lands** — inside `ReinjectConsumer.HandleAsync`, the seam must fire AFTER the L2-presence read (`:45-47`), AFTER `CountSent` (`:81`), and AFTER the `"reinject"` log (`:85`) so telemetry stays byte-identical to a real loss, but must SUPPRESS the `await Guard(() => ep.Send(...))` re-dispatch at `:78`. The existing success path (context — DO NOT delete, the hook gates around the `ep.Send`):

`ReinjectConsumer.cs:63-85`:
```csharp
var dispatch = new EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, m.Payload)
{
    CorrelationId = m.CorrelationId,
    ExecutionId = m.ExecutionId,
    EntryId = m.EntryId,
};
var ep = await Guard(() => Send.GetSendEndpoint(new Uri($"queue:{m.ProcessorId:D}")), ct);
await Guard(() => ep.Send(dispatch, ctx => ctx.MessageId = m.MessageId, CancellationToken.None), ct);   // <-- SUPPRESS THIS for the one latched execution
CountSent(m.WorkflowId, m.ProcessorId);
logger.LogInformation("REINJECT sent {MessageId} {ReinjectOutcome}", m.MessageId, "reinject");
```

**Critical ordering constraint (from CONTEXT `code_context` / D-01):** the log/scope must still fire so `attributes.ReinjectOutcome == "reinject"` reaches the analyzer and the WR-01 veto trips. The net effect of the seam: keeper logs `"reinject"` + counts sent, but the actual `ep.Send` never reaches the processor → the execution never completes → binding miss. So the executor's hook is the INVERSE of the processor delay: it must gate whether `ep.Send` runs, while leaving the surrounding evidence (L2 read, CountSent, log) intact — this is the deliberate departure from a "clean drop" (which would take the `:48-60` `!present` early-return and log `"drop"` instead).

**One-shot latch (D-02, Claude's discretion):** a `static` `Interlocked` flag defeating the FIRST gated reinject, then inert. Suggested shape (executor's call — `static Interlocked` field vs injected singleton):
```csharp
private static int _defeatedOnce; // 0 = armed, 1 = spent
// inside the gated branch, before ep.Send:
if (envGated && Interlocked.CompareExchange(ref _defeatedOnce, 1, 0) == 0)
{
    // suppress ep.Send for exactly this one execution; still CountSent + log "reinject"
}
```
**Replica caveat (from `compose.yaml:233-234`):** the keeper runs `deploy.replicas: 2` — two SEPARATE processes, so a `static` field latches per-process, not globally. `K_EXECUTIONS=1` (single observed execution) is what makes "exactly one lost" unambiguous end-to-end; whichever replica consumes the single reinject latches it. The planner must ensure the latch + K=1 combination cannot double-defeat across replicas (only one execution exists to defeat).

**Env var name (Claude's discretion):** `KEEPER_DEFEAT_REINJECT` or similar `KEEPER_*`; must be default-off. Mirror the processor's `int.TryParse(...) > 0` OR a `bool`/`1` check — either is fine provided unset/empty ⇒ inert.

---

### 2. `compose.yaml` (config) — keeper env var plumbing

**Analog:** `compose.yaml:291-293` (and identical `:328-330` on `processor-badconfig`).

**Analog code to replicate** — `compose.yaml:291-293` under `processor-sample.environment`:
```yaml
      # TEST-ONLY fault hook (env-gated, DEFAULT 0 = OFF). Interpolated from the harness shell only for the
      # TEST-09 negative path (stop-on-inflight); unset everywhere else ⇒ 0 ⇒ SampleProcessor adds no delay.
      PROCESSOR_STEP_DELAY_MS: "${PROCESSOR_STEP_DELAY_MS:-0}"
```

**Where it attaches (the keeper service block):** `compose.yaml`, service `keeper:` (`:229`), `environment:` map at `:241-246`. Add the new key right after the `OTEL_EXPORTER_OTLP_ENDPOINT` line (`:246`), mirroring the processor block exactly. Current keeper env block for placement reference:
```yaml
  keeper:                                    # :229
    ...
    deploy:
      replicas: 2                            # :233-234 — TWO processes; see latch caveat above
    ...
    environment:                             # :241
      RabbitMq__Host: rabbitmq
      RabbitMq__Username: guest
      RabbitMq__Password: guest
      ConnectionStrings__Redis: "redis:6379,abortConnect=false,connectTimeout=5000"
      OTEL_EXPORTER_OTLP_ENDPOINT: "http://otel-collector:4317"   # :246  <-- add KEEPER_* line after this
```
New line to add (name is Claude's discretion; must be `${VAR:-<off-default>}`):
```yaml
      # TEST-ONLY reinject-defeat hook (env-gated, DEFAULT OFF). Interpolated from phase-79-falsify.ps1 only
      # for FALSIFY-01; unset everywhere else ⇒ keeper re-dispatches normally (byte-for-byte unchanged).
      KEEPER_DEFEAT_REINJECT: "${KEEPER_DEFEAT_REINJECT:-0}"
```
**Inertness (D-06):** default `:-0`/`:-false` ⇒ a clean `phase-68-sweep.ps1` (TEST-01..07) with the seam present-but-unset must still reproduce 7/7 PASS.

---

### 3. `scripts/phase-67-harness.ps1` — new `FALSIFY-01` scenario row

**Analog:** the TEST-08/09/10 negative-path rows kept out of the default sweep — `phase-67-harness.ps1:103-106`.

**Analog code to replicate (exact column shape of an `[ordered]` row):** `phase-67-harness.ps1:104-106`:
```powershell
# NEGATIVE-PATH scenarios (NOT in the default phase-68 sweep — run explicitly by id):
'TEST-08' = @{ targetContainers = @('processor-sample'); faultType = 'stop-only';       injectAfterNFires = 4; dwellSeconds = 0; notes = 'NEGATIVE (blind-spot demo): processor crash, NO recovery — ...' }
'TEST-09' = @{ targetContainers = @('processor-sample'); faultType = 'stop-on-inflight'; injectAfterNFires = 3; dwellSeconds = 0; notes = 'NEGATIVE (RMQ-timed FAIL): ...' }
'TEST-10' = @{ targetContainers = @('processor-sample'); faultType = 'stopstart-on-inflight'; injectAfterNFires = 3; dwellSeconds = 300; notes = 'OUTAGE-OUTLASTS-WINDOW: ...' }
```
Row columns (fixed shape): `targetContainers` (array), `faultType` (string), `injectAfterNFires` (int), `dwellSeconds` (int), `notes` (string).

**New row to add** (after TEST-10, `:106`; field values `notes`/`dwellSeconds`/`injectAfterNFires` are Claude's discretion per D-04):
```powershell
'FALSIFY-01' = @{ targetContainers = @(); faultType = 'inject-recovery-loss'; injectAfterNFires = 0; dwellSeconds = 0; notes = 'NEGATIVE CONTROL (gate-teeth): keeper env seam KEEPER_DEFEAT_REINJECT defeats exactly one reinject (K_EXECUTIONS=1) → keeper logs "reinject" but no redispatch → recoverable-but-lost binding miss → the sweep MUST flip this scenario to VERDICT_FAIL. Not in the default TEST-01..07 capstone.' }
```
**Faithful integration (matches how `PROCESSOR_STEP_DELAY_MS` is exported at `:123-126` and cleared in the STEP-H `finally` `:531`):** the new `faultType = 'inject-recovery-loss'` needs a branch analogous to `:123-126` that exports the keeper env var + `K_EXECUTIONS=1` BEFORE STEP A so `docker compose up` interpolates it into the keeper container. Existing export precedent — `phase-67-harness.ps1:123-126`:
```powershell
if ($scenario.faultType -in @('stop-on-inflight','stopstart-on-inflight')) {
    $env:PROCESSOR_STEP_DELAY_MS = '3000'
    Write-Phase "  TEST-ONLY hook: PROCESSOR_STEP_DELAY_MS=3000 exported (baked into processor-sample at compose-up)." 'Yellow'
}
```
And it is torn down in the STEP-H `finally` at `:531` (add the new var to this `Remove-Item` list):
```powershell
Remove-Item Env:SCENARIO_ID, Env:WINDOW_START_UTC, Env:WINDOW_END_UTC, Env:RECOVERY_UTC, Env:K_EXECUTIONS, Env:PROCESSOR_STEP_DELAY_MS -ErrorAction SilentlyContinue
```
**K_EXECUTIONS=1 seam already exists** (`:309`, hold-loop `:445-454`) — no new harness machinery; the row just needs the faultType branch to set `$env:K_EXECUTIONS = '1'` (and export the keeper var) at export time. For `FALSIFY-01`, `faultType='inject-recovery-loss'` should NOT crash any container — it takes the `else`/no-crash observe path; the loss comes entirely from the keeper seam, not a `docker compose stop`. So `targetContainers = @()` and the STEP-F crash sequencer is skipped (guard: it only runs when `faultType -ne 'none'` AND has target containers — planner must ensure `inject-recovery-loss` does not trip the `stop-*` branches at `:370-428`; simplest: treat it like the no-crash observe path but with `RECOVERY_UTC` still pinned so the miss is post-recovery/binding, mirroring the `:385-386` `$recoveryUtc = $windowStart` idiom).

---

### 4. `scripts/phase-79-falsify.ps1` (new script) — standalone driver with INVERTED assertion

**Analogs:** `phase-67-harness.ps1` (exit-code table `:31-44`, STEP-H analyzer invocation + report resolution `:504-557`) AND `phase-68-sweep.ps1` (`-ScenarioIds` param `:48-49`, per-row verdict/class `:101-110`, capstone exit `:126-132`). Also the shared lib `scripts/lib/exit-code-resolution.ps1`.

**(a) Exit-code table to mirror (INVERTED meaning)** — `phase-67-harness.ps1:31-44`. The falsification harness's OWN exit 0 means "the gate correctly went RED":
```
EXIT-CODE TABLE (D-04, harness):
    0   analyzer PASS (final verdict green)
    1   analyzer FAIL verdict (mirrors the dotnet test exit — the legitimate verdict path)
    2   analyzer INCONCLUSIVE verdict (observability-degraded — deliberate warm-ES re-run, NO auto-retry; D-02)
    10  bring-up failed ... 20 reset ... 25 orch restart ... 30 seeder ... 40 wf-id ... 50 activation ...
    60  fault inject/recover/baseline-firing failed
    70  teardown failed (NON-FATAL)
    64  bad -ScenarioId argument
```
Inverted table for `phase-79-falsify.ps1` (its own exit code): exit **0 == the injected scenario correctly FAILED for the recoverable-but-lost reason** (harness returned 1 AND report `Missing >= 1` with the binding-miss `MissingDetail`); non-zero == the negative control did NOT fire (a silent-green False Negative, or wrong-reason red, or infra abort). Per D-03, the assertion must require the harness exit be **exactly 1** — NOT merely non-zero — so an INCONCLUSIVE (2) or infra abort (10-70/64) cannot masquerade as a passing negative control.

**(b) `-ScenarioIds` param + child-process invoke to mirror** — `phase-68-sweep.ps1:48-49` and `:82-83`:
```powershell
param(
    [string[]]$ScenarioIds = @('TEST-01','TEST-02','TEST-03','TEST-04','TEST-05','TEST-06','TEST-07')
)
...
& pwsh -File (Join-Path $PSScriptRoot 'phase-67-harness.ps1') -ScenarioId $id
$code = $LASTEXITCODE
```
Per D-04, `phase-79-falsify.ps1` invokes `phase-68-sweep.ps1 -ScenarioIds FALSIFY-01` and asserts the roll-up row `verdict == "Fail"` (class `VERDICT_FAIL`) AND the wrapper exits non-zero. The default `phase-68-sweep.ps1` id list stays byte-unchanged (`FALSIFY-01` is passed explicitly, never in the default capstone).

**(c) Per-row verdict/class tabulation to mirror** — `phase-68-sweep.ps1:88-110` (read the report, resolve class, tabulate — NEVER re-score):
```powershell
$resolved = Resolve-SweepClass $code
$class = $resolved.Class
...
$report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter "$id.json" -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
$json = if ($report) { Get-Content $report.FullName -Raw | ConvertFrom-Json } else { $null }
$rows += [pscustomobject]@{
    scenarioId   = $id
    verdict      = if ($json) { $json.Verdict } else { 'NO_REPORT' }
    zeroMissing  = if ($json) { ($json.Missing -eq 0) } else { $false }
    ...
    harnessExit  = $code
    class        = $class
}
```
**Assertion-specific read (D-03):** the falsify driver additionally asserts `$json.Missing >= 1` and that `$json.MissingDetail` contains the binding-miss substring. The engine emits that string at `PassFailEngine.cs:255-256`:
```csharp
missingDetail.Add(
    $"[{key}] started-but-incomplete, recoverable-but-lost (no keeper clean-drop, not in-flight-at-wipe) → binding miss.");
```
So the exact-vs-contains match (Claude's discretion) should target `"recoverable-but-lost"` / `"binding miss"`. `MissingDetail` is a report field (`AnalyzerReport.cs:135`, `IReadOnlyList<string>`).

**(d) Capstone exit to mirror (INVERT)** — `phase-68-sweep.ps1:126-132`:
```powershell
$passCount = (@($rows | Where-Object { $_.harnessExit -eq 0 }).Count)
$total = $rows.Count
Write-Phase "CAPSTONE: $passCount/$total PASS" $(if ($passCount -eq $total) { 'Green' } else { 'Red' })
exit ($(if ($passCount -eq $total) { 0 } else { 1 }))
```
For `phase-79-falsify.ps1` the sense flips: success is `harnessExit -eq 1 AND report Missing >= 1 AND MissingDetail matches`; the driver's `exit 0` means "the gate correctly failed the injected scenario for the right reason".

**(e) Shared lib to dot-source** — `. (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')` (as at `phase-67-harness.ps1:88` / `phase-68-sweep.ps1:67`), then use `Resolve-SweepClass`/`Resolve-AnalyzerExitCode` (`exit-code-resolution.ps1:28-58`) — the assertion reads `Resolve-SweepClass 1 → { Class = 'VERDICT_FAIL' }`.

**(f) Skeleton conventions to copy** from both scripts: `$ErrorActionPreference = 'Stop'` + `Set-StrictMode -Version Latest`, `$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path` + `Push-Location`/`finally { Pop-Location }`, and the `Write-Phase` prefixed trace helper (re-prefixed `[phase-79-falsify]`).

---

### 5. `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` (OPTIONAL hermetic pin)

**Analog:** the recoverable-but-lost binding facts — `PassFailEngineFacts.cs:243-261` (`Incomplete_StartedAfterRecovery_Yields_Fail`) and `:309-330` (`RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail`). A new fact would pin the WR-01 `keeperReinject`-veto branch specifically (started-after-recovery + `keeperOutcome="reinject"` → binding miss).

**Analog fact to replicate (shape)** — `PassFailEngineFacts.cs:309-330`:
```csharp
[Fact]
public void RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail()
{
    var stalled  = RunTrace.FromStepIds("corr-9", "exec-9", StalledHopStepIds, convergentStepId: ConvergentStepId);
    var expected = Expect(("corr-9|exec-9", DistinctHopStepIds()));
    var recovery = DateTimeOffset.Parse("2026-06-18T10:00:30Z");
    var lastHop  = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:10Z") };
    var firstHop = new Dictionary<string, DateTimeOffset> { ["corr-9|exec-9"] = DateTimeOffset.Parse("2026-06-18T10:01:05Z") };
    var snap = ConservingSnapshot(results: 9);

    var report = new PassFailEngine().Analyze(new[] { stalled }, snap, "TEST-04",
        recoveryUtc: recovery, firstHopUtcByExecution: firstHop, lastHopUtcByExecution: lastHop,
        keeperOutcomeByExecution: new Dictionary<string, string>(StringComparer.Ordinal),
        expectedStepIdsByExecution: expected);

    Assert.Equal(1, report.Missing);
    Assert.Equal(Verdict.Fail, report.Verdict);
}
```

**What a new FALSIFY pin adds (the WR-01 veto — the EXACT branch the seam exploits):** set `keeperOutcomeByExecution["corr-9|exec-9"] = "reinject"` and make the run **stalled-BEFORE-recovery** (so `redisWipeInFlight` would otherwise tolerate it), proving the `"reinject"` veto forces a binding miss anyway. This directly pins `PassFailEngine.cs:230-234`:
```csharp
// WR-01: a confirmed keeper "reinject" (data WAS recoverable) VETOES the redis-wipe timestamp
// tolerance — a reinjected-but-still-incomplete execution is recoverable-but-lost → binding miss.
var keeperReinject = keeperOutcome.TryGetValue(key, out var oc2)
    && oc2.Equals("reinject", StringComparison.Ordinal);
var redisWipeInFlight = stalledBeforeRecovery && !startedAfterRecovery && !keeperReinject;
```
Contrast with the tolerated sibling at `:333` (`RedisWipe_StalledBeforeRecovery_NoKeeperDrop_Yields_Pass`) — same timestamps, NO `"reinject"` outcome → Pass. The new fact would flip that to Fail purely by adding the `"reinject"` outcome, isolating the veto.

**Test conventions (from the class):** `public sealed class PassFailEngineFacts` (`:30`), no Docker/ES — pure synthetic `RunTrace` + `PromCounterSnapshot` → `new PassFailEngine().Analyze(...)`, sub-second (`:5-11`). Helpers already present: `RunTrace.FromStepIds`, `Expect(...)`, `StalledHopStepIds`, `ConvergentStepId`, `DistinctHopStepIds()`, `ConservingSnapshot(...)`. Run via `BaseApi.Tests.exe` directly with `--filter-not-trait Category=RealStack` (net8.0; `dotnet test` hangs on Windows MTP — see hermetic-test-command memory). This is a `Category != RealStack` hermetic fact, so it stays green independent of the Docker seam (D-06).

---

## Shared Patterns

### Env-gated product seam (default-OFF, harness-only)
**Source:** `src/Processor.Sample/SampleProcessor.cs:42-52` + `compose.yaml:291-293` + harness export `phase-67-harness.ps1:123-126` and `finally` teardown `:531`.
**Apply to:** the new keeper seam (file 1), its compose plumbing (file 2), and its harness export/teardown (file 3). The four-part idiom is non-negotiable: (1) `Environment.GetEnvironmentVariable` read + default-off guard in code, (2) `${VAR:-<off>}` compose interpolation on the service, (3) harness `$env:VAR = ...` export BEFORE `docker compose up`, (4) `Remove-Item Env:VAR` in the STEP-H `finally`. Comment must say TEST-ONLY / NEVER production.

### Exit-code resolution + no-re-score (verdict classification)
**Source:** `scripts/lib/exit-code-resolution.ps1:28-58` (`Resolve-AnalyzerExitCode`, `Resolve-SweepClass`).
**Apply to:** the new driver (file 4). Dot-source the lib; NEVER re-score Missing/Duplicates — read the analyzer's already-written `analyzer-reports/<id>.json` and tabulate. Class map: `0→PASS`, `1→VERDICT_FAIL`, `2→INCONCLUSIVE`, `64→BAD_ARG`, else `INFRA_ABORT`.

### PowerShell driver skeleton
**Source:** `phase-67-harness.ps1:67-73` / `phase-68-sweep.ps1:52-62`.
**Apply to:** file 4. `param(...)`, `$ErrorActionPreference='Stop'`, `Set-StrictMode -Version Latest`, `$repoRoot`/`Push-Location`+`finally{Pop-Location}`, `Write-Phase` prefixed helper, child harness invoked as `& pwsh -File ... -ScenarioId <id>` capturing `$LASTEXITCODE`.

### Byte-identical telemetry ordering (the core correctness constraint)
**Source:** `ReinjectConsumer.cs:39-85` (MEL execution scope wraps the whole consume body; the `"reinject"` log + `CountSent` fire on the success path only).
**Apply to:** file 1. The suppress-send hook MUST fire AFTER the L2 read, `CountSent`, and `"reinject"` log so ES `attributes.ReinjectOutcome == "reinject"` is emitted (WR-01 fires) — the ONLY delta from a real recoverable-but-lost strand is the missing `ep.Send`.

---

## No Analog Found

None. Every file has a strong in-repo analog (all named in CONTEXT.md `<canonical_refs>` and inlined above). RESEARCH.md fallback is not needed for this phase.

---

## Metadata

**Analog search scope:** `src/Keeper/Recovery/`, `src/Processor.Sample/`, `scripts/`, `scripts/lib/`, `compose.yaml`, `tests/BaseApi.Tests/Observability/Analysis/`.
**Files scanned (read, non-overlapping):** `ReinjectConsumer.cs` (full), `SampleProcessor.cs` (full), `phase-67-harness.ps1` (full), `phase-68-sweep.ps1` (full), `exit-code-resolution.ps1` (full), `compose.yaml:229-349`, `PassFailEngine.cs:180-264` + `:325-349`, `PassFailEngineFacts.cs:1-60` + `:195-333`, `AnalyzerReport.cs` (MissingDetail/UnrecoverableLossDetail fields).
**Pattern extraction date:** 2026-07-17

## PATTERN MAPPING COMPLETE
