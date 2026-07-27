using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Schema;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using BaseApi.Tests.Observability.Helpers;
using Messaging.Contracts.Projections;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// CAPSTONE real-stack round-trip + truthful liveness-gated Start proof (TEST-01 / SC#4) — the only
/// end-to-end test that exercises the WHOLE milestone against real containers.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="CorrelationPropagationE2ETests"/> (it REUSES the same
/// <c>RealStackWebAppFactory</c> host-stack overrides and net-zero teardown discipline) but diverges in
/// two load-bearing ways:
/// </para>
/// <list type="number">
///   <item>
///     <b>Genuine embedded SourceHash (D-08).</b> The Processor DB row is registered with the GENUINE
///     hash reflected off the built <c>Processor.Sample.dll</c> — exactly as the runtime reader / the
///     hermetic <c>SourceHashEmbedFacts</c> reads it (<see cref="AssemblyMetadataAttribute"/> off the
///     assembly). It is NOT a synthetic random hash and it is NOT recomputed. Plan 02
///     PROVED the host-built hash equals the Linux-Docker-built hash the live container runs, so this
///     registration closes the identity loop: the container resolves THIS processor id by querying
///     <c>GetProcessorBySourceHash(hash)</c>.
///   </item>
///   <item>
///     <b>NO synthetic liveness seed (D-07 / Pitfall 3).</b> Unlike the Phase 22 analog, this test does
///     NOT seed a synthetic processor-liveness key. Instead it POLLS host Redis for the REAL
///     <c>processor-sample</c> container's <c>skp:{procId:D}</c> Healthy heartbeat (written only AFTER
///     the container resolves identity, binds <c>queue:{id:D}</c>, and <c>MarkHealthy</c>). Start is
///     POSTed ONLY once that real key is fresh — so the liveness gate passes TRUTHFULLY (a false-green
///     with the container stopped is impossible).
///   </item>
/// </list>
/// <para>
/// The round-trip is then asserted on two clauses (SC#4): (a) the dispatched step produced OUTPUT —
/// a fresh <c>skp:data:*</c> execution-data key appears in host Redis that was NOT present before Start
/// (the <c>EntryStepDispatchConsumer</c> mints a server-side <c>NewId.NextGuid()</c> entryId and writes
/// the output there); and (b) the ORCHESTRATOR ADVANCED — its container's <c>"Start reload for
/// WorkflowId={wfId}"</c> seam log flows via otel → Elasticsearch (the proven
/// <see cref="CorrelationPropagationE2ETests"/> precedent), proving the orchestrator consumed the
/// published <c>StartOrchestration</c> and hydrated+scheduled the workflow whose fire drove the
/// dispatch.
/// </para>
/// <para>
/// The workflow is seeded with a <c>* * * * *</c> (every-minute) cron so the orchestrator's
/// self-rescheduling one-shot Quartz job actually FIRES the dispatch (a null-cron workflow is a
/// business-skip in <c>WorkflowLifecycle.HydrateAndScheduleAsync</c> and would never dispatch). The
/// poll budgets are generous to cover container boot + identity-resolve + the up-to-60s next-minute
/// fire + otel→ES ingest latency.
/// </para>
/// <para>
/// Net-zero teardown (Pitfall 4): the run's fresh <c>skp:data:*</c> keys are registered into the
/// factory's <c>L2KeysToCleanup</c> (drained in <c>DisposeAsync</c>), the L2 root/step keys the Start
/// created are deleted, and the parent-index member is SREMed. The steady-state <c>skp:{procId:D}</c>
/// liveness key is LEFT (the live container keeps refreshing it — it is in BOTH close-gate snapshots).
/// Tagged <c>Category=RealStack</c> so the hermetic filter (<c>Category!=RealStack</c>) excludes it.
/// </para>
/// </remarks>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Collection("Observability")]
public sealed class SampleRoundTripE2ETests
{
    private const string StartReloadMessage = "Start reload for WorkflowId=";

    // The branch-free OrchestratorPrePipeline (Phase 72) logs this DISTINCT line when it reads a real
    // terminal out: blob for a step with no matching successor and acks — the durable round-trip-output proof
    // (see clause (a)). Substring match (the full template carries the WorkflowId/StepId/Outcome too).
    private const string CompletedTerminalMessage = "Trip ended (completed-terminal)";

    // ---- Gate-A (CFG-09) compatible config-schema seed primitives (D-09a / D-13) ----
    // Shared by Plan 03's Gate-A CFG-09 E2E and Plan 04's close script so all three reuse the SAME
    // sentinel Name (schemas have NO uniqueness constraint, so a fixed Name is the idempotency key the
    // GET-or-create helper filters on).
    internal const string SampleCompatibleSchemaName = "gateA-sample-compatible";

    // The config schema that Processor.Sample's typed config (SampleConfig(string? Value)) COVERS — so
    // Gate A (ConfigSchemaId ⊨ configType) RUNS AND PASSES (CFG-09), not Gate-A-skipped. An object with a
    // single optional string "value" property, carrying the draft 2020-12 $schema key.
    internal const string SampleCompatibleSchemaDefinition =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"value":{"type":"string"}}}""";

    // The live processor-sample container resolves identity + binds + MarkHealthy after the DB row is
    // seeded (compose start_period 30s + identity-resolve latency); allow a generous budget.
    private const int LivenessPollTimeoutMs = 90_000;

    // otel/log export is async; tolerate flush + ingest latency on the orchestrator-advance ES proof.
    private const int EsPollTimeoutMs = 120_000;

    [Fact]
    public async Task LiveSampleProcessor_RoundTrip_AdvancesOrchestrator_OnTruthfulLivenessGate()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();
        using var client = factory.CreateClient();

        // D-08: read the GENUINE embedded SourceHash off the BUILT Processor.Sample assembly — the same
        // way AssemblyMetadataSourceHashProvider does at runtime. NOT a synthetic random hash, NOT recomputed.
        var hash = typeof(global::Processor.Sample.SampleProcessor).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "SourceHash").Value!;

        // Register the Processor DB row with THAT genuine hash + null schemas (D-05). The WebApi assigns
        // procId at row create; the live container resolves THIS id by GetProcessorBySourceHash(hash).
        var procId = await SeedProcessorAsync(client, hash, ct);
        var stepId = await SeedStepAsync(client, procId, ct);
        // Seed WITH a cron so the orchestrator's one-shot job actually fires the dispatch (null cron is a
        // business-skip in HydrateAndScheduleAsync — the round-trip would never run).
        var wfId = await SeedWorkflowAsync(client, new List<Guid> { stepId }, cron: "* * * * *", ct);

        // Pitfall 3 / D-07: DO NOT seed the liveness key. POLL host Redis for the REAL container's
        // skp:{procId:D} Healthy heartbeat — only proceed once it exists + is fresh (the live container
        // went Healthy). If it never appears, identity diverged (Plan 02's dual-build gate should have
        // caught it) OR the container is down — fail with a clear message.
        await PollForHealthyLivenessAsync(procId, ct);

        // Drive Start. 204 NoContent means the L2 root was written, the body Guid minted + published, and
        // the processor-liveness gate PASSED — and it passed ONLY because the live container's heartbeat
        // is fresh (truthful SC#4 gate; no synthetic seed backs it).
        var startResp = await client.PostAsJsonAsync(
            "/api/v1/orchestration/start", new List<Guid> { wfId }, ct);
        Assert.Equal(HttpStatusCode.NoContent, startResp.StatusCode);

        // Register the L2 root/step keys + parent-index member the Start created for net-zero teardown
        // (drained in DisposeAsync even if an assertion below throws). The steady-state skp:{procId:D}
        // liveness key is NOT registered — the live container keeps refreshing it (both gate snapshots).
        factory.ParentIndexMembersToSrem.Add(wfId.ToString("D"));
        factory.L2KeysToCleanup.Add($"skp:{wfId}");
        factory.L2KeysToCleanup.Add($"skp:{wfId}:{stepId}");

        // ---- Round-trip clause (a): OUTPUT ROUND-TRIPPED + CONSUMED ----
        // Phase 70/72 reshaped the output path: the processor writes its terminal output to the transient
        // out: blob (skp:out:{messageId}), and the branch-free OrchestratorPrePipeline reads it, fans out to
        // any successors, then DELETES it. For THIS single-step (no-successor) workflow nothing is relocated
        // to skp:data, and the out: blob is reclaimed within the same trip — so neither key is observable by a
        // black-box Redis poll (the legacy "poll for a fresh skp:data key" proof is obsolete under the
        // always-write model). The DURABLE proof that output was produced AND consumed is the orchestrator
        // pre-pipeline's terminal trip-end log "Trip ended (completed-terminal): ... outcome=Completed —
        // acking", emitted ONLY when a real terminal out: blob is read for a step with no successor. It
        // carries attributes.WorkflowId (the message template + the IExecutionCorrelated inbound scope), so it
        // round-trips to ES exactly like the clause-(b)/(c) logs.
        using var es = new ElasticsearchTestClient();
        var completedTerminal =
            await PollForOrchestratorLogContainingAsync(es, wfId, CompletedTerminalMessage, ct);
        Assert.NotNull(completedTerminal);   // processor output round-tripped → orchestrator consumed the Completed result

        // ---- Round-trip clause (b): ORCHESTRATOR ADVANCES ----
        // The orchestrator CONTAINER consumed the published StartOrchestration and logged the seam
        // "Start reload for WorkflowId={wfId}" (StartOrchestrationConsumer), proving it hydrated +
        // scheduled the workflow whose fire drove the dispatch we just observed land in L2. Read it back
        // from Elasticsearch via the proven otel→ES precedent (term on the seeded WorkflowId attribute,
        // scoped to the orchestrator service; the distinct message text asserted in C#).
        // Robust against hits[0] aliasing: the orchestrator emits MULTIPLE logs for this WorkflowId (the
        // start-reload seam AND a per-fire completed-terminal trip-end), so a hits[0]-only query can return a
        // terminal log instead of the start-reload one. Search ALL hits for the start-reload fragment.
        var advance = await PollForOrchestratorLogContainingAsync(es, wfId, StartReloadMessage, ct);
        Assert.NotNull(advance);   // orchestrator hydrated + scheduled the workflow (StartOrchestrationConsumer seam)

        // ---- Round-trip clause (c): WorkflowId reached ES FROM A SCOPE on a PROCESSOR-side log (LOG-06 / L1) ----
        // LOG-06 / L1: prove WorkflowId reached ES FROM A SCOPE, not from a template. The processor-sample
        // service consumes EntryStepDispatch (IExecutionCorrelated), so its logs get attributes.WorkflowId
        // ONLY via the new InboundExecutionScopeConsumeFilter — this assertion fails if the scope work is
        // reverted. (The orchestrator hit above is template-sourced — StartOrchestration is NOT
        // IExecutionCorrelated — and would pass even without scopes.)
        var scopeProofQuery = $$"""
          {
            "size": 5,
            "sort": [ { "@timestamp": { "order": "desc" } } ],
            "query": {
              "bool": {
                "must": [
                  { "term": { "attributes.WorkflowId": "{{wfId}}" } },
                  { "term": { "attributes.ProcessorId": "{{procId:D}}" } }
                ]
              }
            }
          }
          """;
        var scopeProof = await es.PollEsForLog(scopeProofQuery, timeoutMs: EsPollTimeoutMs, ct: ct);
        Assert.NotNull(scopeProof);   // WorkflowId round-tripped to ES from the new scope on a processor log

        // NET-ZERO-31 (Phase 31.1): stop the workflow so its self-rescheduling cron fire ceases — left
        // running it mints a fresh per-fire skp:flag:{H} every minute, churning the close-gate redis
        // --scan name-set. Best-effort: a stop hiccup must not fail an otherwise-green E2E assertion.
        try { await client.PostAsJsonAsync("/api/v1/orchestration/stop", new List<Guid> { wfId }, ct); }
        catch { /* best-effort net-zero teardown */ }
    }

    // ---- Liveness poll (Pitfall 3): wait for the REAL container's per-instance Healthy heartbeat ----
    // Phase 61 (GATE-01/02/03, D-06/11): the Phase-60 writer publishes per-replica liveness keys
    // (skp:proc:{procId}:{instanceId}) + an instance-index SET (skp:proc:{procId}); the legacy flat
    // skp:{procId}/ProcessorProjection key was retired (D-11). This poll now mirrors the gate: SMEMBERS
    // the index -> GET each per-instance ProcessorLivenessEntry -> accept on >=1 Healthy + fresh replica.

    internal static async Task PollForHealthyLivenessAsync(Guid procId, CancellationToken ct)
    {
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var index = L2ProjectionKeys.InstanceIndex(procId);

        var deadline = DateTime.UtcNow.AddMilliseconds(LivenessPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var members = await db.SetMembersAsync(index);
            foreach (var member in members)
            {
                var raw = await db.StringGetAsync(L2ProjectionKeys.PerInstance(procId, member.ToString()));
                if (raw.IsNullOrEmpty) continue;
                var entry = JsonSerializer.Deserialize<ProcessorLivenessEntry>(raw!);
                // Freshness: a non-stale Healthy replica means it resolved identity, bound its queue, and
                // MarkHealthy'd. interval is SECONDS — accept within a generous 3× window (mirrors the gate).
                if (entry is { Status: LivenessStatus.Healthy })
                {
                    var age = DateTime.UtcNow - entry.Timestamp.ToUniversalTime();
                    var staleAfter = TimeSpan.FromSeconds(Math.Max(entry.Interval, 1) * 3);
                    if (age <= staleAfter)
                        return; // a REAL replica is Healthy — Start's liveness gate will pass truthfully.
                }
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        Assert.Fail(
            $"The processor-sample container never wrote a fresh Healthy per-instance liveness key under {index} " +
            $"within {LivenessPollTimeoutMs}ms. Either the container is down, or its embedded SourceHash diverges " +
            $"from the host-built hash registered as the DB row (identity never resolved). Ensure the full " +
            $"compose stack incl. processor-sample is up healthy.");
    }

    // ---- Fabricated per-instance liveness seed (Phase 62 D-04 / craft-redis-state, Phase-61 WR-01/WR-02 style) ----
    // Writes a CALLER-built ProcessorLivenessEntry directly into host Redis as a per-instance key
    // (skp:proc:{procId:D}:{instanceId}) + SADDs the instanceId into the instance-index SET
    // (skp:proc:{procId:D}), then registers BOTH for net-zero teardown. This drives the in-process gate
    // (ProcessorLivenessValidator) verdict deterministically — no container, no timing race. The entry is
    // built by the caller via ProcessorLivenessEntry.Create(...) (the ONLY sanctioned construction path)
    // so the wire shape/casing round-trips through the reader's deserialization. The
    // RealStackNetZeroSweepFixture does NOT sweep skp:proc:* — every fabricated key + index member is the
    // test's own cleanup responsibility, so both are registered here (RESEARCH Pitfall 6).
    internal static async Task SeedFabricatedLivenessAsync(
        RealStackWebAppFactory factory,
        Guid procId,
        string instanceId,
        ProcessorLivenessEntry entry,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();
        var key = L2ProjectionKeys.PerInstance(procId, instanceId);
        await db.StringSetAsync(key, JsonSerializer.Serialize(entry), TimeSpan.FromSeconds(60));
        await db.SetAddAsync(L2ProjectionKeys.InstanceIndex(procId), instanceId);
        factory.L2KeysToCleanup.Add(key);                          // net-zero: delete the per-instance key
        factory.InstanceIndexMembersToSrem.Add((procId, instanceId)); // net-zero: SREM the index member
    }

    // ---- Round-trip output proof (clause a): the branch-free OrchestratorPrePipeline logged a terminal
    // trip-end for THIS workflow — the durable signal that the processor produced an out: blob the
    // orchestrator read + consumed. The out: blob (and, in a multi-step flow, the relocated skp:data) are
    // reclaimed within the trip, so neither is reliably observable by a black-box Redis poll under the
    // Phase-70/72 always-write model; the orchestrator's trip-end log is the stable artifact. ----
    private static async Task<JsonElement?> PollForOrchestratorLogContainingAsync(
        ElasticsearchTestClient es, Guid wfId, string messageFragment, CancellationToken ct)
    {
        // otel maps the log message under a nested, non-phrase-searchable "body" object, so we term-filter on
        // the WorkflowId attribute + the orchestrator service and string-match the fragment in C# across ALL
        // hits (the start-reload log shares the same term filter — SearchAllHits returns the full set, so the
        // terminal log is never lost behind hits[0]). Poll-to-stable until the fragment appears or timeout.
        var query = $$"""
          {
            "size": 50,
            "sort": [ { "@timestamp": { "order": "desc" } } ],
            "query": {
              "bool": {
                "must": [
                  { "term": { "attributes.WorkflowId": "{{wfId}}" } },
                  { "term": { "resource.attributes.service.name": "orchestrator" } }
                ]
              }
            }
          }
          """;
        var deadline = DateTime.UtcNow.AddMilliseconds(EsPollTimeoutMs);
        var delay = 1_000;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var hit in await es.SearchAllHits(query, ct: ct))
            {
                if (hit.GetRawText().Contains(messageFragment, StringComparison.Ordinal))
                {
                    return hit;   // the round-trip output reached the orchestrator pre-pipeline and acked terminal
                }
            }

            await Task.Delay(Math.Min(delay, 3_000), ct);
            delay = Math.Min(delay * 2, 3_000);
        }

        Assert.Fail(
            $"No orchestrator log containing \"{messageFragment}\" for WorkflowId={wfId} reached ES within " +
            $"{EsPollTimeoutMs}ms — the live round-trip (fire → dispatch → ProcessAsync → out: blob → " +
            $"orchestrator pre-pipeline consume) did not complete.");
        return null; // unreachable (Assert.Fail throws) — keeps the compiler happy.
    }

    // ---- HTTP seeding helpers (Processor → Step → Workflow) — mirrors CorrelationPropagationE2ETests ----

    internal static async Task<Guid> SeedProcessorAsync(
        HttpClient client, string sourceHash, CancellationToken ct, Guid? configSchemaId = null)
    {
        // D-08: register the GENUINE embedded hash (satisfies the DB ^[a-f0-9]{64}$ validator);
        // D-05: null schema Ids (Processor.Sample runs schema-less).
        //
        // GET-or-create (idempotent): the genuine embedded hash is FIXED, and the processor row is
        // guarded by a unique uq_processor_source_hash constraint that persists in host Postgres across
        // runs. A blind POST collides (23505) on every run after the first. Resolve the existing row by
        // its hash and reuse THAT id — which is exactly the row the live processor-sample container has
        // already resolved + is heartbeating against (the identity loop is stable). Only create when no
        // row exists yet (first run / fresh DB).
        var lookup = await client.GetAsync($"/api/v1/processors/by-source-hash/{sourceHash}", ct);
        if (lookup.StatusCode == HttpStatusCode.OK)
        {
            var existing = await lookup.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
            return existing!.Id;
        }

        var dto = new ProcessorCreateDto(
            Name: $"sample-proc-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            SourceHash: sourceHash,
            InputSchemaId: null,
            OutputSchemaId: null,
            // CFG-09 delta: defaulted null preserves the schema-less seed for existing callers; a non-null
            // compatible-schema Id (from SeedConfigSchemaAsync) flips Gate A from skipped to RUN-AND-PASS.
            ConfigSchemaId: configSchemaId);
        var resp = await client.PostAsJsonAsync("/api/v1/processors", dto, ct);
        resp.EnsureSuccessStatusCode();
        var proc = await resp.Content.ReadFromJsonAsync<ProcessorReadDto>(cancellationToken: ct);
        return proc!.Id;
    }

    /// <summary>
    /// GET-or-create-by-sentinel-Name helper for a config schema (D-09a / D-13 / T-58-04).
    /// Schemas have NO uniqueness constraint (only FK indexes) → a blind POST duplicates every run and
    /// churns the close-gate net-zero snapshot. GET the list, match a fixed sentinel <paramref name="sentinelName"/>,
    /// reuse its Id; POST only if absent. NEVER PUT — a referenced schema's Definition is FROZEN
    /// (PUT → 409, Phase-57 D-06 / SchemaService.cs); this helper is CREATE-IF-ABSENT only.
    /// </summary>
    internal static async Task<Guid> SeedConfigSchemaAsync(
        HttpClient client, string sentinelName, string definition, CancellationToken ct)
    {
        var all = await client.GetFromJsonAsync<List<SchemaReadDto>>("/api/v1/schemas", ct);
        var existing = all!.FirstOrDefault(s => s.Name == sentinelName);
        if (existing is not null)
        {
            return existing.Id;
        }

        // VERIFIED SchemaCreateDto field order (Name, Version, Description, Definition); Definition is a
        // string on this DTO (SchemaDtos.cs) — the meta-schema validation happens server-side on write.
        var dto = new SchemaCreateDto(sentinelName, "1.0.0", null, definition);
        var resp = await client.PostAsJsonAsync("/api/v1/schemas", dto, ct);
        resp.EnsureSuccessStatusCode();
        var created = await resp.Content.ReadFromJsonAsync<SchemaReadDto>(cancellationToken: ct);
        return created!.Id;
    }

    internal static async Task<Guid> SeedStepAsync(HttpClient client, Guid processorId, CancellationToken ct)
    {
        var dto = new StepCreateDto(
            Name: $"sample-step-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            ProcessorId: processorId,
            NextStepIds: null,
            EntryCondition: StepEntryCondition.Always);
        var resp = await client.PostAsJsonAsync("/api/v1/steps", dto, ct);
        resp.EnsureSuccessStatusCode();
        var step = await resp.Content.ReadFromJsonAsync<StepReadDto>(cancellationToken: ct);
        return step!.Id;
    }

    internal static async Task<Guid> SeedWorkflowAsync(
        HttpClient client, List<Guid> entryStepIds, string cron, CancellationToken ct)
    {
        var dto = new WorkflowCreateDto(
            Name: $"sample-wf-{Guid.NewGuid():N}",
            Version: "1.0.0",
            Description: null,
            EntryStepIds: entryStepIds,
            AssignmentIds: null,
            CronExpression: cron);
        var resp = await client.PostAsJsonAsync("/api/v1/workflows", dto, ct);
        resp.EnsureSuccessStatusCode();
        var wf = await resp.Content.ReadFromJsonAsync<WorkflowReadDto>(cancellationToken: ct);
        return wf!.Id;
    }

    private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";

    /// <summary>
    /// Points the in-process WebApi at the REAL host stack (RMQ localhost:5673, Redis localhost:6380,
    /// Postgres localhost:5433, otel localhost:4317) and drains net-zero teardown in
    /// <see cref="DisposeAsync"/>. REUSED WHOLESALE from <see cref="CorrelationPropagationE2ETests"/> —
    /// the env-var-in-ctor host overrides + L2KeysToCleanup / ParentIndexMembersToSrem discipline are
    /// identical, MINUS the synthetic liveness seed helper (omitted by design — Pitfall 3:
    /// the real container heartbeats).
    /// </summary>
    internal sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory
    {
        private readonly Dictionary<string, string?> _prior = new();

        public RealStackWebAppFactory()
            : base(
                skipPostgresFixture: true,
                connectionStringOverride: HostPostgres,
                skipRedisFixture: true,
                redisConnectionStringOverride: HostRedisFull)
        {
            try
            {
                Set("RabbitMq__Host", "localhost");
                Set("RabbitMq__Port", "5673");
                Set("RabbitMq__Username", "guest");
                Set("RabbitMq__Password", "guest");

                Set("ConnectionStrings__Redis", HostRedisFull);
                Set("ConnectionStrings__Postgres", HostPostgres);

                Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
            }
            catch
            {
                Restore();
                throw;
            }
        }

        private const string HostRedisFull = "localhost:6380,abortConnect=false,connectTimeout=5000";
        private const string HostPostgres =
            "Host=localhost;Port=5433;Database=stepsdb;Username=postgres;Password=postgres;Maximum Pool Size=20;Timeout=15";

        private void Set(string key, string value)
        {
            _prior[key] = Environment.GetEnvironmentVariable(key);
            Environment.SetEnvironmentVariable(key, value);
        }

        private void Restore()
        {
            foreach (var kv in _prior)
            {
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — populated
        /// AFTER the real Start projects them + the round-trip mints them. Drained in
        /// <see cref="DisposeAsync"/> so the close-gate <c>redis-cli --scan</c> net-zero invariant holds.
        /// The steady-state <c>skp:{procId:D}</c> liveness key is NOT registered (the live container
        /// keeps it fresh in both snapshots — Plan 04).
        /// </summary>
        public List<RedisKey> L2KeysToCleanup { get; } = new();

        /// <summary>Shared <c>skp:</c> parent-index members this test SADDed (via Start) to SREM on teardown.</summary>
        public List<RedisValue> ParentIndexMembersToSrem { get; } = new();

        /// <summary>
        /// Fabricated per-processor instance-index members (<c>skp:proc:{procId:D}</c>) to SREM on teardown
        /// (Phase 62 D-04). DISTINCT from <see cref="ParentIndexMembersToSrem"/> — that SREMs the BARE
        /// <c>skp:</c> parent index, NOT the per-processor <c>skp:proc:{procId}</c> instance index. The
        /// <see cref="RealStackNetZeroSweepFixture"/> does not sweep <c>skp:proc:*</c>, so every fabricated
        /// index member registered by <see cref="SeedFabricatedLivenessAsync"/> is SREM'd here, drained on
        /// the SAME teardown connection (no second multiplexer) so a fabricated member never pollutes a
        /// later test's gate <c>SMEMBERS</c> (RESEARCH Pitfall 6 / T-62-04).
        /// </summary>
        public List<(Guid ProcId, RedisValue Member)> InstanceIndexMembersToSrem { get; } = new();

        public override async ValueTask DisposeAsync()
        {
            if (L2KeysToCleanup.Count > 0 || ParentIndexMembersToSrem.Count > 0 || InstanceIndexMembersToSrem.Count > 0)
            {
                await using var cleanupMux = await ConnectionMultiplexer.ConnectAsync(HostRedisFull);
                var db = cleanupMux.GetDatabase();
                if (L2KeysToCleanup.Count > 0)
                {
                    await db.KeyDeleteAsync(L2KeysToCleanup.ToArray());
                }
                if (ParentIndexMembersToSrem.Count > 0)
                {
                    await db.SetRemoveAsync(L2ProjectionKeys.ParentIndex(), ParentIndexMembersToSrem.ToArray());
                }
                foreach (var (procId, member) in InstanceIndexMembersToSrem)
                {
                    await db.SetRemoveAsync(L2ProjectionKeys.InstanceIndex(procId), member);
                }
            }
            Restore();
            await base.DisposeAsync();
        }
    }
}
