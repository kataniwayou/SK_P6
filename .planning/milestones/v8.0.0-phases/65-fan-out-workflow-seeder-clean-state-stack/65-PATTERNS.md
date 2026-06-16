# Phase 65: Fan-Out Workflow Seeder & Clean-State Stack - Pattern Map

**Mapped:** 2026-06-14
**Files analyzed:** 3 net-new artifacts (no product-code changes)
**Analogs found:** 3 / 3 (all exact or strong role-matches)

This is a backend/infra phase. All three artifacts are *composition of existing seams* — no new API surface, no compose edits, no processor changes (RESEARCH §"Key insight", line 205). Every excerpt below is grounded at file:line.

## File Classification

| New File | Role | Data Flow | Closest Analog | Match Quality |
|----------|------|-----------|----------------|---------------|
| `tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs` (new fixture; or extend `SampleRoundTripE2ETests.cs`) | test (RealStack E2E seeder + self-verify) | request-response (REST POST) + DB read-back | `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs` | exact (same fixture template, reuses its `internal static` helpers) |
| `scripts/phase-65-reset.ps1` | infra script (destructive clean-state reset) | batch / event-driven (FLUSHALL → heal-wait poll → psql DELETE → docker assert) | `scripts/phase-62-close.ps1` (docker/redis-cli/psql shapes + health pre-flight) | role-match (close scripts SHA-compare only; the destructive DELETE half is net-new) |
| `scripts/phase-65-up.ps1` | infra script (minimal-stack bring-up) | batch (compose up → health-wait → assert 0 badconfig) | `scripts/phase-62-close.ps1:289-309` (compose-health pre-flight loop) + `compose.yaml:299-300` | role-match (the health-wait loop is reused; `compose up -d` + badconfig assert are net-new) |

---

## Pattern Assignments

### `FanOutSeederE2ETests.cs` (test, request-response + DB read-back)

**Analog:** `tests/BaseApi.Tests/Orchestrator/SampleRoundTripE2ETests.cs`

**Fixture shell + traits + ctor** (mirror `SampleRoundTripE2ETests.cs:70-107`):
```csharp
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]   // hermetic filter Category!=RealStack excludes it (:67)
[Collection("Observability")]
public sealed class FanOutSeederE2ETests
{
    [Fact]                          // filtered live by ~FanOutSeeder (RESEARCH §Validation, line 366)
    public async Task ...()
    {
        var ct = TestContext.Current.CancellationToken;   // xUnit v3
        await using var factory = new RealStackWebAppFactory();   // REUSE this nested class verbatim (:443-536)
        await factory.InitializeAsync();
        using var client = factory.CreateClient();
        // ...
    }
}
```
`RealStackWebAppFactory` (`:443-536`) is the reusable host-override class: it points the in-proc WebApi at the host stack (RMQ `localhost:5673`, Redis `localhost:6380`, Postgres `localhost:5433`, otel `localhost:4317`) via env-var sets in ctor (`:456-465`) and runs net-zero teardown in `DisposeAsync` (`:514-535`). The seeder writes only graph rows (no L2 round-trip), so it likely does **not** need `L2KeysToCleanup`/`ParentIndexMembersToSrem` registration — those are for the Start round-trip's minted `skp:data:*` keys.

**Reuse-as-is `internal static` seed helpers** (`:341-431`) — these already encode the REST contracts + GET-or-create idempotency:
- `SeedProcessorAsync(client, sourceHash, ct, configSchemaId=null)` (`:341-374`) — by-source-hash GET-or-create. Resolve the genuine embedded hash exactly as `:111-113`:
  ```csharp
  var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
      .GetCustomAttributes<AssemblyMetadataAttribute>()
      .First(a => a.Key == "SourceHash").Value!;
  var procId = await SeedProcessorAsync(client, hash, ct);   // the ONE shared processor-sample id
  ```
- `SeedStepAsync` (`:402-415`) — **must be extended/cloned** to accept `NextStepIds` + a custom Name (the existing one hardcodes `NextStepIds: null` and `EntryCondition.Always`). The DTO shape to POST (`StepDtos.cs:13-19`):
  ```csharp
  new StepCreateDto(Name, Version:"1.0.0", Description:null, ProcessorId:procId,
                    NextStepIds: <list-or-null>, EntryCondition: StepEntryCondition.Always);
  // POST /api/v1/steps → read.Id   (StepReadDto.NextStepIds is null on read — verify via Npgsql)
  ```
  `StepEntryCondition.Always = 4`, `PreviousCompleted = 1` (`StepEntryCondition.cs:17,20`).
- `SeedWorkflowAsync` (`:417-431`) — **extend** to take `AssignmentIds` and a sentinel Name (existing one passes `AssignmentIds: null` and a random Name). DTO (`WorkflowDtos.cs:20-26`):
  ```csharp
  new WorkflowCreateDto(Name:"v8-fanout-proof", Version:"1.0.0", Description:null,
                        EntryStepIds: new List<Guid>{ aStepId },
                        AssignmentIds: allNineIds,
                        CronExpression: "*/30 * * * * *");   // 6-field — NOT the :121 "* * * * *"
  ```

**Idempotency by sentinel workflow name** — mirror `SeedConfigSchemaAsync` GET-list-match (`:383-400`); workflows have no unique-name constraint:
```csharp
var all = await client.GetFromJsonAsync<List<WorkflowReadDto>>("/api/v1/workflows", ct);
var existing = all!.FirstOrDefault(w => w.Name == "v8-fanout-proof");
if (existing is not null) return existing.Id;   // 2nd run no-ops; id stable (WF-02 / D-04)
```

**New helper to add — `SeedAssignmentAsync`** (Pattern 1; DTO `AssignmentDtos.cs:11-16`). Each node needs a step-bound assignment, then ALL 9 ids passed as `WorkflowCreateDto.AssignmentIds`:
```csharp
internal static async Task<Guid> SeedAssignmentAsync(
    HttpClient client, Guid stepId, int number, string label, CancellationToken ct)
{
    var payload = JsonSerializer.Serialize(new { number, label }); // {"number":1,"label":"Step_A"}
    var dto = new AssignmentCreateDto(
        Name: $"asg-{label}-{Guid.NewGuid():N}", Version: "1.0.0",
        Description: null, StepId: stepId, Payload: payload);
    var resp = await client.PostAsJsonAsync("/api/v1/assignments", dto, ct);
    resp.EnsureSuccessStatusCode();
    var read = await resp.Content.ReadFromJsonAsync<AssignmentReadDto>(cancellationToken: ct);
    return read!.Id;
}
```
Controller route prefix `/api/v1/assignments` confirmed (`AssignmentController.cs:9`).

**Reverse-topological create order** (RESEARCH Pattern 2) — sinks first so every `NextStepId` already exists (both `step_next_steps` FKs are `OnDelete(Restrict)`):
```
F1, F2  (Next: null)  →  E1(Next:[F1]),E2(Next:[F2])  →  D1(Next:[E1]),D2(Next:[E2])
   →  C(Next:[D1,D2])  →  B(Next:[C])  →  A(Next:[B], entry)
```
Yields exactly 8 edges. Label→number: A=1,B=2,C=3,D1=4,E1=5,F1=6,D2=7,E2=8,F2=9 (D-08).

**Self-verification via direct Npgsql read** (Pattern 4) — analog `StepsIntegrationTests.cs:71-79`; REST read DTOs return `null` for junctions so query snake_case tables directly:
```csharp
// Source: StepsIntegrationTests.cs:73-79 — NpgsqlConnection + NpgsqlCommand "SELECT count(*)"
await using var conn = new NpgsqlConnection(HostPostgres);  // const at :474-475 (Database=stepsdb)
await conn.OpenAsync(ct);
await using var cmd = new NpgsqlCommand("SELECT count(*) FROM step_next_steps", conn);
var n = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));   // expect 8
```
Verification queries (snake_case, per RESEARCH lines 177-183):
```sql
SELECT count(*) FROM workflows WHERE cron_expression = '*/30 * * * * *'   -- 1
SELECT count(*) FROM steps                                                -- 9 (reset-clean DB)
SELECT count(DISTINCT processor_id) FROM steps                            -- 1
SELECT count(*) FROM step_next_steps                                      -- 8
SELECT count(*) FROM workflow_entry_steps                                 -- 1 (step A)
SELECT count(*) FROM assignments                                          -- 9
SELECT count(*) FROM workflow_assignments                                 -- 9 (Pitfall 2)
```
Plus per-node payload regex `^Step_(A|B|C|D1|E1|F1|D2|E2|F2)$` (9 distinct) and sink zero-outgoing `WHERE step_id = <F1>`/`<F2>` = 0.

**Run-twice idempotency** (D-04 / Open Q2) — call the seed routine twice in ONE `[Fact]`, capture both workflow ids, `Assert.Equal`, re-verify 1/9/9/8. Simpler than two `dotnet test` invocations.

**Anti-patterns to avoid** (RESEARCH lines 189-193): no raw SQL inserts (seed via REST only); snake_case `steps` NOT PascalCase `"Steps"`; 6-field `*/30 * * * * *` NOT 5-field `* * * * *` (`:121` is the stale linear-seed cron).

---

### `scripts/phase-65-reset.ps1` (infra script, batch/event-driven destructive reset)

**Analog:** `scripts/phase-62-close.ps1` (docker/redis-cli/psql invocation shapes). NOTE: close scripts only SHA-compare; the destructive FLUSHALL + DELETE half is net-new (RESEARCH State-of-the-Art line 317).

**Step 1 — Redis FLUSHALL** (named container `sk-redis`, same invocation family as `:320`):
```powershell
docker exec sk-redis redis-cli FLUSHALL | Out-Null
```

**Step 2 — heal-wait poll on liveness-key reappearance** (D-07; key shape `L2ProjectionKeys.cs:44` `skp:proc:{procId:D}:{instanceId}`; heartbeat 10s default → 60s budget, fail loud — RESEARCH Pitfall 1 + Code Example lines 277-291):
```powershell
$deadline = (Get-Date).AddSeconds(60)   # 6x the 10s heartbeat — generous, fail-loud
$healed = $false
while ((Get-Date) -lt $deadline) {
    # per-INSTANCE key has a 2nd ':' segment after proc: (skp:proc:{id}:{instanceId});
    # bare index SET skp:proc:{id} is excluded by the regex
    $keys = @(docker exec sk-redis redis-cli --scan --pattern 'skp:proc:*' |
              Where-Object { $_ -notmatch '^skp:proc:[^:]+$' })
    if ($keys.Count -ge 1) { $healed = $true; break }
    Start-Sleep -Seconds 2
}
if (-not $healed) { Write-Host "Liveness did not reconverge in 60s after FLUSHALL — aborting." -ForegroundColor Red; exit 2 }
```
Rationale: this is the exact L2 state `ProcessorLivenessValidator` reads at orchestration-start — gating on the key (not container readiness) prevents a subsequent seed+Start 422. Do NOT poll the bare flat `skp:{procId}` (deleted Phase 61 — RESEARCH line 321).

**Step 3 — psql DELETE in FK-safe order** (CRITICAL Pitfall 3: close scripts use `psql -U postgres -lqt` DB-LIST at `:314` which would no-op for row deletes — the reset MUST add `-d stepsdb`; postgres has NO `container_name` so use `docker compose exec`, NOT `docker exec sk-postgres`):
```powershell
docker compose exec -T postgres psql -U postgres -d stepsdb -c "BEGIN;
DELETE FROM step_next_steps; DELETE FROM workflow_assignments; DELETE FROM workflow_entry_steps;
DELETE FROM assignments; DELETE FROM workflows; DELETE FROM steps;
COMMIT;"
# PRESERVE processors + config_schemas (schemas) — idempotent, NOT deleted (D-06)
```
Order = migration `20260528074618_InitialCreate.cs` `Down()` drop order (`:271-288`) — RESEARCH "Don't Hand-Roll" line 200.

**Step 4 — processor-set assertion + orphan removal** (Pitfall 4: never `docker rm` the 2 unnamed `processor-sample` replicas; `processor-badconfig` IS named `sk-processor-badconfig` at `compose.yaml:304` so target it by exact name). Health-instance enumeration mirrors `phase-62-close.ps1:265-273`:
```powershell
$instances = @(docker compose ps processor-sample --format json 2>$null |
    Where-Object { $_ -match '\S' } | ForEach-Object { $_ | ConvertFrom-Json })
# assert 2 healthy replicas; then remove only OTHER processor containers (badconfig by exact name):
$bad = @(docker ps --filter 'name=sk-processor-badconfig' --format '{{.Names}}')
if ($bad.Count -gt 0) { docker rm -f sk-processor-badconfig | Out-Null }
```

**Forbidden** (D-06 / RESEARCH line 192): NO `docker compose down`, NO `-v`, NO DB/volume drop. Stack stays up.

---

### `scripts/phase-65-up.ps1` (infra script, batch bring-up)

**Analog:** `scripts/phase-62-close.ps1:289-309` (the compose-health pre-flight loop) + `compose.yaml:299-300` (badconfig profile gate).

**Compose up (default profile auto-excludes badconfig — NO compose edit; `profiles: ["badconfig"]` at `compose.yaml:300`):**
```powershell
docker compose up -d | Out-Null   # default profile — processor-badconfig excluded by its profile gate
```

**Health-wait loop — REUSE the proven NDJSON-per-replica parse** (`phase-62-close.ps1:291-309`; canonical 10-service list at `:291`; otel-collector has no in-container healthcheck so treat 'running' as ready):
```powershell
$services = @('postgres','redis','rabbitmq','otel-collector','elasticsearch','prometheus',
              'orchestrator','keeper','baseapi-service','processor-sample')
foreach ($svc in $services) {
    $instances = @(docker compose ps $svc --format json 2>$null |
        Where-Object { $_ -match '\S' } | ForEach-Object { $_ | ConvertFrom-Json })
    $health = if ($instances.Count -eq 0) { 'not-running' }
              else { $u = @($instances | Where-Object { $_.Health -ne 'healthy' });
                     if ($u.Count -gt 0) { "$($u[0].Health)" } else { 'healthy' } }
    if ($health -ne 'healthy' -and $svc -ne 'otel-collector') { <# keep polling; fail loud after deadline #> }
}
```
`keeper` and `processor-sample` are `deploy.replicas: 2` (multi-line NDJSON — require ALL instances healthy, per the `:294-296` comment).

**Assert 0 badconfig (ENV-01):**
```powershell
$bad = @(docker ps --filter 'name=sk-processor-badconfig' --format '{{.Names}}')
if ($bad.Count -ne 0) { Write-Host "badconfig container running — ENV-01 violated." -ForegroundColor Red; exit 2 }
```

---

## Shared Patterns

### docker / redis-cli / psql invocation shapes
**Source:** `scripts/phase-62-close.ps1` — `docker exec sk-redis redis-cli ...` (`:320`), `docker compose exec -T postgres psql -U postgres ...` (`:314`, but add `-d stepsdb`), `docker compose ps <svc> --format json` NDJSON parse (`:297-303`).
**Apply to:** both `phase-65-reset.ps1` and `phase-65-up.ps1`.
**Container naming fact:** `sk-redis`/`sk-rabbitmq`/`sk-processor-badconfig` are named (`docker exec`); `postgres`/`keeper`/`processor-sample`/`baseapi-service` are unnamed (`docker compose exec`/`ps`).

### Compose-health convergence poll (all-instances-healthy)
**Source:** `phase-62-close.ps1:291-309`.
**Apply to:** the reset's processor-set check AND the bring-up's 10-service wait. Treat `otel-collector` 'running' as ready (no in-container healthcheck).

### GET-or-create idempotency (seeder)
**Source:** `SampleRoundTripE2ETests.cs` — processor by-source-hash (`:341-374`, `uq_processor_source_hash`-guarded), schema/workflow by-sentinel-Name GET-list-match (`:383-400`).
**Apply to:** the seeder's processor resolve (reuse verbatim) and the new workflow sentinel idempotency (clone the schema pattern with Name `v8-fanout-proof`).

### Two-sided assignment binding
**Source:** `AssignmentDtos.cs:11-16` (`StepId` FK) + `WorkflowDtos.cs:20-26` (`AssignmentIds` junction).
**Apply to:** every node — `POST /assignments` with `StepId` AND include all 9 ids in the single workflow's `AssignmentIds` (Pitfall 2: forgetting the 2nd leaves `workflow_assignments` empty).

---

## No Analog Found

| Concern | Role | Reason | Planner guidance |
|---------|------|--------|------------------|
| Destructive psql `DELETE` half of the reset | infra script | No existing script deletes rows — all 18 `phase-NN-close.ps1` scripts SHA-compare only (RESEARCH line 317) | Use migration `Down()` order (`InitialCreate.cs:271-288`) as the FK-safe DELETE sequence; static literal statements only (no interpolated input) |
| Heal-wait reconvergence poll | infra script | No precedent — close scripts assume steady-state liveness already present | Net-new; key shape + 60s budget grounded in `L2ProjectionKeys.cs:44` + `ProcessorLivenessOptions.cs:21` (10s heartbeat) |
| Fan-out multi-node topology in a fixture | test | First multi-node topology in the codebase (existing seed is 1-step linear, `:117-121`) | Reverse-topo create with `NextStepIds`; no template exists but the per-call DTO shapes are exact |

---

## Metadata

**Analog search scope:** `tests/BaseApi.Tests/Orchestrator/`, `tests/BaseApi.Tests/Integration/`, `scripts/`, `src/BaseApi.Service/Features/{Assignment,Step,Workflow}/`, `src/Messaging.Contracts/Projections/`, `compose.yaml`.
**Files read for excerpts:** `SampleRoundTripE2ETests.cs` (1-135, 335-537), `StepsIntegrationTests.cs` (1-90), `AssignmentDtos.cs`, `StepDtos.cs`, `WorkflowDtos.cs`, `phase-62-close.ps1` (1-50, 250-339), `compose.yaml` (290-319), `L2ProjectionKeys.cs` (31-65), `StepEntryCondition.cs` (grep).
**Pattern extraction date:** 2026-06-14
