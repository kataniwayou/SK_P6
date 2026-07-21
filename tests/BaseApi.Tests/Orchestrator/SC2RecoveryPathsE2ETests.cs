using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using BaseApi.Service.Features.Processor;
using BaseApi.Service.Features.Step;
using BaseApi.Service.Features.Workflow;
using MassTransit;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// SC2 / TEST-01 (recovery-paths half) — the RealStack proof of each of the THREE v5 Keeper recovery states
/// (REINJECT present/absent, INJECT, DELETE — the v4 UPDATE/CLEANUP states were retired in Phase 53),
/// driven by DIRECT-PUBLISH of the actual state contracts to the gate-open recovery queue
/// (<see cref="KeeperQueues.Recovery"/> = the sole surviving Keeper queue, D-05). Sibling to
/// <see cref="SampleRoundTripE2ETests"/> (SC1) — it REUSES that file's <c>RealStackWebAppFactory</c> host
/// overrides + net-zero teardown discipline (cloned below) but, instead of driving the organic
/// orchestrator → dispatch → processor round trip, it publishes <see cref="KeeperReinject"/> /
/// <see cref="KeeperInject"/> / <see cref="KeeperDelete"/> straight at <c>queue:keeper-recovery</c> and
/// asserts each state's deterministic L2 / re-inject / orchestrator-advance / dead-letter effect.
/// <para>
/// The three direct-publish proofs (effects read from the production recovery consumers):
/// </para>
/// <list type="number">
///   <item><b>REINJECT data-present</b> (<c>ReinjectConsumer</c>) — PRE-SEED <c>skp:data:{entryId}</c> so
///   STRLEN&gt;0 → the consumer re-injects a reconstructed <see cref="EntryStepDispatch"/> (carrying the
///   author <see cref="KeeperReinject.Payload"/>) to <c>queue:{ProcessorId:D}</c>. Asserted on that
///   origin-queue depth.</item>
///   <item><b>REINJECT data-gone</b> (<c>ReinjectConsumer</c>) — do NOT seed the data key (STRLEN==0) →
///   Phase 52 (D-06) makes this a BY-DESIGN silent drop (no throw, no send, no dead-letter). Phase 74
///   REMOVED the legacy <c>keeper_reinject_dropped</c> counter — the drop emits NO drop counter and NO
///   <c>keeper_messages_sent</c>. Asserted on the origin queue staying EMPTY (nothing re-injected) and
///   the DLQ depth NOT incrementing — A18 "accepted silent losses".</item>
///   <item><b>INJECT</b> (<c>InjectConsumer</c>) — Phase 52 (KEEP-02) implements the A18 forward-only body:
///   write <c>L2[m.EntryId]=m.Data</c>, send a reconstructed <see cref="StepCompleted"/> to
///   <c>queue:orchestrator-result</c>, delete <c>m.DeleteEntryId</c>. Asserted on the data key being
///   written and the source key being deleted.</item>
///   <item><b>DELETE</b> (<c>DeleteConsumer</c>, Phase 70 single-key) — the reshaped <see cref="KeeperDelete"/>
///   is DELETE-only (NO MessageId — the two-key/slot-index model is retired, 70-01); the consumer deletes ONLY
///   <c>skp:data:{entryId}</c> and sends nothing. PRE-SEED the data key → assert it is gone after the DEL.</item>
/// </list>
/// <para>
/// Phase 70 retired the live slot-array recovery branch (the pipeline is now the linear Pre flow — there is no
/// <c>if exist L2[messageId]</c> recovery pass), so the former organic-recovery <c>[Fact]</c> has no analog and
/// was removed; the three direct-publish recovery-state proofs above remain.
/// </para>
/// <para>
/// Net-zero (D-04 + D-07): EVERY minted key — the seeded <c>skp:data:*</c> — is
/// registered into <c>factory.L2KeysToCleanup</c>, so a leak surfaces as a close-gate redis SHA mismatch
/// rather than a silent TTL pass. The A19 two-key DEL self-cleans the happy case; registration is the
/// belt-and-suspenders. The data-gone DLQ message is bounded (exactly one) and self-cleaning (drained in
/// teardown). Gate-open precondition: a healthy RealStack keeps the BIT loop
/// from <c>Stop()</c>ing the <c>keeper-recovery</c> endpoint (D-04/D-09, Phase 52). When the endpoint is
/// running, the three recovery consumers (REINJECT, INJECT, DELETE) process at entry with NO Consume-level
/// gate-wait — the per-<c>Consume</c> <c>gate.WaitForOpenAsync</c> was removed in Phase 52; gating is now at
/// the endpoint level via <c>Stop</c>/<c>Start</c>.
/// </para>
/// <para>
/// Tagged <c>Category=RealStack</c> + <c>Phase=55</c>: the hermetic filter (<c>Category!=RealStack</c>)
/// EXCLUDES these facts; they run only against the operator-gated live v5 stack (55-HUMAN-UAT.md). TEST-01
/// stays UNTICKED until that GREEN live run.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "RealStack")]
[Trait("Phase", "62")]
[Collection("Observability")]
public sealed class SC2RecoveryPathsE2ETests
{
    // The recovery consumer awaits the gate then runs its (millisecond) L2 op + Send; allow a generous
    // budget for broker round-trip + redis settle (mirrors SC1's OutputPollTimeoutMs).
    private const int EffectPollTimeoutMs = 120_000;

    [Fact]
    public async Task LiveKeeperRecovery_AllThreeStates_ProduceTheirL2AndReinjectAndDeadLetterEffects()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var factory = new RealStackWebAppFactory();
        await factory.InitializeAsync();

        // D-05: resolve the bus and address the gate-open recovery queue by the CONST name (never the
        // literal queue string). In a healthy RealStack the BIT loop leaves the keeper-recovery endpoint
        // RUNNING, so the three recovery consumers (REINJECT, INJECT, DELETE) process at entry with no
        // Consume-level gate-wait (the per-Consume gate.WaitForOpenAsync was removed in Phase 52, D-04/D-09 —
        // gating is now endpoint Stop/Start).
        var bus = factory.Services.GetRequiredService<IBus>();
        var endpoint = await bus.GetSendEndpoint(new Uri($"queue:{KeeperQueues.Recovery}"));

        await using var mux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
        var db = mux.GetDatabase();

        // =========================================================================================
        // STATE 1 — REINJECT data-present → re-inject EntryStepDispatch to queue:{ProcessorId:D}
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var entryId = Guid.NewGuid();

            // PRE-SEED skp:data:{entryId} so the consumer's STRLEN gate sees data PRESENT (>0) and
            // re-injects (rather than throwing the data-gone terminal). Register for net-zero teardown.
            var dataKey = L2ProjectionKeys.ExecutionData(entryId);
            await db.StringSetAsync(dataKey, "payload-bytes");
            factory.L2KeysToCleanup.Add(dataKey);

            await endpoint.Send(new KeeperReinject(wfId, stepId, procId)
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = entryId,
                Payload = "step-config",
            }, ct);

            // EFFECT: ReinjectConsumer re-injects a reconstructed EntryStepDispatch to the origin queue
            // queue:{ProcessorId:D} (same target a direct dispatch uses). Assert that origin queue's depth
            // climbs to >=1 on the live broker (no consumer is bound to this fresh procId queue, so the
            // re-injected message parks there observably). Register the broker queue for teardown cleanup.
            var originQueue = procId.ToString("D");
            var depth = await PollForQueueDepthAsync(originQueue, minDepth: 1, ct);
            Assert.True(depth >= 1,
                $"REINJECT data-present: expected the re-injected EntryStepDispatch to land on " +
                $"queue:{originQueue}, but its depth stayed {depth}.");
            factory.BrokerQueuesToDelete.Add(originQueue);
        }

        // =========================================================================================
        // STATE 2 — REINJECT data-gone → Phase 52 (D-06) BY-DESIGN silent drop (no re-inject, no dead-letter)
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var entryId = Guid.NewGuid();   // its skp:data:{entryId} is DELIBERATELY absent (STRLEN==0)

            // Read the DLQ depth BEFORE so we can assert it does NOT increment (data-gone is a drop, D-06).
            var dlqBefore = await ReadQueueDepthAsync("skp-dlq-1", ct);

            await endpoint.Send(new KeeperReinject(wfId, stepId, procId)
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = entryId,
                Payload = "step-config",
            }, ct);

            // EFFECT (D-06): STRLEN==0 → silent drop — ack with no throw, no re-inject, no dead-letter, and
            // NO drop counter (Phase 74 removed keeper_reinject_dropped) and NO keeper_messages_sent. Allow a
            // settle window, then assert the origin queue stayed EMPTY (nothing re-injected) and DLQ did NOT climb.
            await Task.Delay(5_000, ct);
            var originQueue = procId.ToString("D");
            var originDepth = await ReadQueueDepthAsync(originQueue, ct);
            Assert.Equal(0, originDepth);   // dropped, not re-injected
            var dlqAfter = await ReadQueueDepthAsync("skp-dlq-1", ct);
            Assert.True(dlqAfter <= dlqBefore,
                $"REINJECT data-gone: expected a silent drop (no dead-letter), but " +
                $"skp-dlq-1 depth climbed {dlqBefore} -> {dlqAfter}.");

            // WR-01: defensively register skp-dlq-1 for purge-on-teardown. Today the data-gone path is a
            // BY-DESIGN silent drop (D-06) so this purge is a no-op (nothing lands in the DLQ). But wiring it
            // here makes the teardown self-healing: if a future contract change makes data-gone throw →
            // dead-letter, the parked message is drained to net-zero locally instead of leaking to the
            // close gate's skp-dlq-1 depth==0 invariant (~50min later) with no test-local signal.
            factory.BrokerQueuesToPurge.Add("skp-dlq-1");
        }

        // =========================================================================================
        // STATE 3 — INJECT (Phase 70, D-13) — DataResult-driven forward-only: the KeeperInject embeds the whole
        // self-contained DataResult (write L2[OutputData(messageId)]=data on Completed, send Step* by result),
        // plus the source DeleteEntryId; it deletes L2[ExecutionData(DeleteEntryId)] and never recomputes from
        // input. Assert the OUTPUT blob is written (by messageId) and the source key is deleted.
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var corr = Guid.NewGuid();
            var execId = Guid.NewGuid();
            var messageId = NewId.NextGuid();
            var deleteEntryId = Guid.NewGuid();

            // PRE-SEED the source key so its post-INJECT deletion is observable; register both the output blob
            // (written by INJECT, keyed by messageId) and the source delete key for net-zero teardown.
            var outputKey = L2ProjectionKeys.OutputData(messageId);
            var deleteKey = L2ProjectionKeys.ExecutionData(deleteEntryId);
            await db.StringSetAsync(deleteKey, "source-to-delete");
            factory.L2KeysToCleanup.Add(outputKey);
            factory.L2KeysToCleanup.Add(deleteKey);

            var dataResult = new DataResult(wfId, stepId, procId)
            {
                CorrelationId = corr,
                ExecutionId = execId,
                MessageId = messageId,
                Result = StepOutcome.Completed,
                Data = System.Text.Encoding.UTF8.GetBytes("inject-payload"),
            };
            await endpoint.Send(new KeeperInject(wfId, stepId, procId)
            {
                CorrelationId = corr,
                ExecutionId = execId,
                DataResult = dataResult,
                DeleteEntryId = deleteEntryId,
            }, ct);

            // EFFECT: the OUTPUT blob is written with dr.Data (keyed by messageId), and the source key is deleted
            // (the StepCompleted send to queue:orchestrator-result is exercised end-to-end by SC1's round-trip).
            var written = await PollForKeyValueAsync(db, outputKey, "inject-payload", ct);
            Assert.True(written,
                $"INJECT: expected {outputKey} to be written with the injected Data, but it was not.");
            var sourceDeleted = await PollForKeyAbsentAsync(db, deleteKey, ct);
            Assert.True(sourceDeleted,
                $"INJECT: expected the source key {deleteKey} to be deleted after the send, but it remains.");
        }

        // =========================================================================================
        // STATE 4 — DELETE (Phase 70 single-key, req 8): the reshaped KeeperDelete is DELETE-ONLY — it carries
        // NO MessageId (the two-key/slot-index model is retired, 70-01); DeleteConsumer deletes ONLY
        // skp:data:{entryId} and sends nothing. PRE-SEED just the execution-data key → assert it goes absent.
        // =========================================================================================
        {
            var wfId = Guid.NewGuid();
            var stepId = Guid.NewGuid();
            var procId = Guid.NewGuid();
            var entryId = NewId.NextGuid();

            // PRE-SEED the sole DELETE operand: the execution-data key. Register for net-zero teardown
            // (belt-and-suspenders, D-07 — the DEL self-cleans the happy case below).
            var dataKey = L2ProjectionKeys.ExecutionData(entryId);    // skp:data:{entryId:D}
            await db.StringSetAsync(dataKey, "to-be-deleted");
            factory.L2KeysToCleanup.Add(dataKey);

            await endpoint.Send(new KeeperDelete(wfId, stepId, procId)
            {
                CorrelationId = Guid.NewGuid(),
                ExecutionId = Guid.NewGuid(),
                EntryId = entryId,   // Phase 70 (req 8): DELETE-only operand — no MessageId, no index key.
            }, ct);

            // EFFECT: DeleteConsumer issues ONE KeyDeleteAsync(ExecutionData(entryId)) — the source data key.
            // Assert it goes ABSENT (zero orchestrator sends — verified separately by the hermetic DeleteConsumerFacts).
            Assert.True(await PollForKeyAbsentAsync(db, dataKey, ct),
                $"DELETE: expected {dataKey} (skp:data) to be deleted by the single-key DEL, but it still exists.");
        }
    }

    // ---- L2 polls (mirror SampleRoundTripE2ETests' scan/poll shapes) -------------------------------

    private static async Task<bool> PollForKeyAbsentAsync(IDatabase db, string key, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!await db.KeyExistsAsync(key))
            {
                return true;
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        return false;
    }

    private static async Task<bool> PollForKeyValueAsync(IDatabase db, string key, string expected, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
        var delay = 500;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var value = await db.StringGetAsync(key);
            if (value.HasValue && value.ToString() == expected)
            {
                return true;
            }

            await Task.Delay(Math.Min(delay, 2_000), ct);
            delay = Math.Min(delay * 2, 2_000);
        }

        return false;
    }

    // ---- Broker queue-depth helpers (live RabbitMQ via docker exec rabbitmqctl) --------------------
    // Mirrors the RecoveryDeadLetterFacts depth-assertion idiom adapted to the RealStack: the live
    // consolidated transport really lands the data-gone give-up in skp-dlq-1, and the re-inject really
    // lands an EntryStepDispatch on queue:{procId:D}. Read depth off the live broker.

    private static async Task<long> PollForQueueDepthAsync(string queue, long minDepth, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(EffectPollTimeoutMs);
        var delay = 1_000;
        long depth = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            depth = await ReadQueueDepthAsync(queue, ct);
            if (depth >= minDepth)
            {
                return depth;
            }

            await Task.Delay(Math.Min(delay, 3_000), ct);
            delay = Math.Min(delay * 2, 3_000);
        }

        return depth;
    }

    /// <summary>
    /// Read the message count of a single broker queue via
    /// <c>docker exec sk-rabbitmq rabbitmqctl -q list_queues name messages</c>, matching the row whose name
    /// equals <paramref name="queue"/>. Returns 0 when the queue does not exist yet (no row).
    /// </summary>
    private static async Task<long> ReadQueueDepthAsync(string queue, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "exec", "sk-rabbitmq", "rabbitmqctl", "-q", "list_queues", "name", "messages" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 'docker exec sk-rabbitmq rabbitmqctl'.");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // rabbitmqctl -q emits TAB-separated "name<TAB>messages" rows.
            var cols = line.Split('\t', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cols.Length >= 2 && string.Equals(cols[0], queue, StringComparison.Ordinal)
                && long.TryParse(cols[1], out var count))
            {
                return count;
            }
        }

        return 0;   // queue not present (or empty) → depth 0.
    }

    /// <summary>Purge a broker queue via <c>docker exec sk-rabbitmq rabbitmqctl purge_queue {queue}</c>.</summary>
    private static async Task PurgeQueueAsync(string queue)
    {
        await RunRabbitCtlAsync(new[] { "purge_queue", queue });
    }

    /// <summary>Delete a broker queue via <c>docker exec sk-rabbitmq rabbitmqctl delete_queue {queue}</c>.</summary>
    private static async Task DeleteQueueAsync(string queue)
    {
        await RunRabbitCtlAsync(new[] { "delete_queue", queue });
    }

    private static async Task RunRabbitCtlAsync(string[] ctlArgs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("sk-rabbitmq");
        psi.ArgumentList.Add("rabbitmqctl");
        psi.ArgumentList.Add("-q");
        foreach (var arg in ctlArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi);
        if (proc is null)
        {
            return;   // best-effort teardown — a docker hiccup must not fail an otherwise-green E2E.
        }
        _ = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
    }

    private const string HostRedis = "localhost:6380,abortConnect=false,connectTimeout=5000";

    /// <summary>
    /// Points the in-process WebApi at the REAL host stack (RMQ localhost:5673, Redis localhost:6380,
    /// Postgres localhost:5433, otel localhost:4317) and drains net-zero teardown in
    /// <see cref="DisposeAsync"/>. CLONED from <see cref="SampleRoundTripE2ETests"/>'s
    /// <c>RealStackWebAppFactory</c> — the env-var-in-ctor host overrides + L2KeysToCleanup /
    /// ParentIndexMembersToSrem discipline are identical, EXTENDED with broker-queue teardown
    /// (<see cref="BrokerQueuesToPurge"/> / <see cref="BrokerQueuesToDelete"/>) so the bounded data-gone
    /// DLQ message + the parked re-inject queue are cleaned to net-zero before the close gate's
    /// skp-dlq-1 depth==0 + rabbitmq name-SHA invariants.
    /// </summary>
    private sealed class RealStackWebAppFactory : Composition.Phase8WebAppFactory
    {
        private readonly Dictionary<string, string?> _prior = new();

        public RealStackWebAppFactory()
            : base(
                skipPostgresFixture: true,
                connectionStringOverride: HostPostgres,
                skipRedisFixture: true,
                redisConnectionStringOverride: HostRedis)
        {
            try
            {
                Set("RabbitMq__Host", "localhost");
                Set("RabbitMq__Port", "5673");
                Set("RabbitMq__Username", "guest");
                Set("RabbitMq__Password", "guest");

                Set("ConnectionStrings__Redis", HostRedis);
                Set("ConnectionStrings__Postgres", HostPostgres);

                Set("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317");
            }
            catch
            {
                Restore();
                throw;
            }
        }

        // IN-04: Redis connection string is the outer-class HostRedis (same file, private const is
        // visible to this nested class) — single source of truth, no shadowing HostRedisFull duplicate.
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
        /// L2 keys (production "skp:" prefix) the test registers for deletion on teardown — EVERY minted
        /// key, INCLUDING the composite backup whose 2-day TTL cannot be waited out (D-07). Drained in
        /// <see cref="DisposeAsync"/> so the close-gate <c>redis-cli --scan</c> net-zero invariant holds.
        /// </summary>
        public List<RedisKey> L2KeysToCleanup { get; } = new();

        /// <summary>Shared <c>skp:</c> parent-index members this test SADDed to SREM on teardown.</summary>
        public List<RedisValue> ParentIndexMembersToSrem { get; } = new();

        /// <summary>Broker queues to PURGE on teardown (the bounded data-gone skp-dlq-1 message) so the
        /// close gate's depth==0 holds.</summary>
        public List<string> BrokerQueuesToPurge { get; } = new();

        /// <summary>Broker queues to DELETE on teardown (the per-procId re-inject queue the test created by
        /// re-injecting to a fresh queue:{procId:D}) so the close gate's rabbitmq name-SHA holds.</summary>
        public List<string> BrokerQueuesToDelete { get; } = new();

        public override async ValueTask DisposeAsync()
        {
            if (L2KeysToCleanup.Count > 0 || ParentIndexMembersToSrem.Count > 0)
            {
                await using var cleanupMux = await ConnectionMultiplexer.ConnectAsync(HostRedis);
                var db = cleanupMux.GetDatabase();
                if (L2KeysToCleanup.Count > 0)
                {
                    await db.KeyDeleteAsync(L2KeysToCleanup.ToArray());
                }
                if (ParentIndexMembersToSrem.Count > 0)
                {
                    await db.SetRemoveAsync(L2ProjectionKeys.ParentIndex(), ParentIndexMembersToSrem.ToArray());
                }
            }

            foreach (var q in BrokerQueuesToPurge)
            {
                await PurgeQueueAsync(q);
            }
            foreach (var q in BrokerQueuesToDelete)
            {
                await DeleteQueueAsync(q);
            }

            Restore();
            await base.DisposeAsync();
        }
    }
}
