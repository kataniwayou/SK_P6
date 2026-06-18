using System.Text.Json;
using Messaging.Contracts;
using Messaging.Contracts.Projections;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;
using BaseProcessorBase = BaseProcessor.Core.Processing.BaseProcessor;
using ProcessorPipeline = BaseProcessor.Core.Processing.ProcessorPipeline;
using OutputTail = BaseProcessor.Core.Processing.OutputTail;

namespace BaseApi.Tests.Processor;

/// <summary>
/// Phase 73 (D-07/D-08/D-09) — the hermetic zero-Docker fan-in proof. Drives the REAL
/// <see cref="ProcessorPipeline"/> / <see cref="OutputTail"/> AND the REAL (Plan-01-seeded)
/// <see cref="global::Processor.Sample.SampleProcessor"/> (NOT a <c>FakeProcessor</c> — the <c>+1</c> value
/// math and the fixed <c>100</c>/<c>200</c> seeds MUST actually run) across the full DAG
/// <c>A → B → C → {D1 → E1 → F1, D2 → E2 → F2} → G</c>, backed by the stateful
/// <see cref="DictBackedL2Fake"/> so each hop's L2 value survives the round-trip to the next hop.
/// <para>
/// <b>Why this lives at the framework layer (D-07):</b> <c>entryId</c>/<c>messageId</c> is framework-owned and
/// invisible inside <c>ProcessAsync</c>, so the <c>entryId</c>-distinctness proof at the convergent terminal
/// <c>G</c> (exactly TWO non-joining arrivals, each on its OWN <c>entryId</c> blob, order-independent) can
/// only be asserted here, where the harness sees the framework layer directly.
/// </para>
/// <para>
/// <b>Expected value chain</b> (Plan-01 contract: Mode-2 seeds {100, 200}; Mode-1
/// <c>accumulated = incomingNumber + baseNumber</c> with every payload <c>number = 1</c> → <c>+1</c> per hop):
/// exec_a (seed 100): B=101, C=102, D1/D2=103, E1/E2=104, F1/F2=105, G=106; exec_b (seed 200): +100 throughout
/// (B=201 … F=205, G=206).
/// </para>
/// No instrumentation-value assertions appear anywhere (instrumentation deferred — SPEC out-of-scope); the
/// only instrumentation reference is the no-op holder the real pipeline ctor requires.
/// </summary>
public sealed class FanInHermeticHarnessFacts
{
    // ===== expected value oracle (Plan-01 contract, copied verbatim — D-09a) ============================
    // | label | exec_a (seed 100) | exec_b (seed 200) |
    // |-------|-------------------|-------------------|
    // | B     | 101               | 201               |
    // | C     | 102               | 202               |
    // | D1/D2 | 103               | 203               |
    // | E1/E2 | 104               | 204               |
    // | F1/F2 | 105               | 205               |
    // | G     | 106               | 206               |
    private const int SeedA = 100;
    private const int SeedB = 200;
    private const int TerminalA = 106;   // seed 100 + 6 hops
    private const int TerminalB = 206;   // seed 200 + 6 hops

    // ===== harness wiring ==============================================================================

    /// <summary>The exact <c>PrePipelineFacts.Build</c> factory (real OutputTail + real ProcessorPipeline),
    /// but with the stateful dict-backed mux swapped in for the stateless DispatchTestKit muxes.</summary>
    private static ProcessorPipeline BuildPipeline(
        IConnectionMultiplexer redis, FakeProcessorContext context, BaseProcessorBase processor,
        DispatchTestKit.CapturingSendProvider send)
    {
        var tail = new OutputTail(redis, context, send, DispatchTestKit.Retry(3),
            DispatchTestKit.Options(300), DispatchTestKit.Metrics());
        return new ProcessorPipeline(redis, context, processor, send, DispatchTestKit.Retry(3), tail,
            DispatchTestKit.Metrics(), NullLogger<ProcessorPipeline>.Instance);
    }

    private static FakeProcessorContext Ctx() => new() { InputDefinition = null, OutputDefinition = null };

    /// <summary>The config payload the seam deserializes into a <c>SampleConfig</c>: <c>number = 1</c> drives
    /// the deterministic <c>+1</c> per hop; <c>label</c> rides through verbatim.</summary>
    private static string Config(string label) =>
        JsonSerializer.Serialize(new { number = 1, label });

    /// <summary>The number field the SampleProcessor reads/writes in a data:/out: blob.</summary>
    private static int ReadNumber(RedisValue blob)
    {
        using var doc = JsonDocument.Parse(blob.ToString());
        return doc.RootElement.GetProperty("number").GetInt32();
    }

    // ===== single-hop driver over the REAL pipeline + REAL SampleProcessor =============================

    /// <summary>One completed Mode-1 hop: pre-seed <c>data:entryId</c> with the inbound value, run the REAL
    /// pipeline (which reads it, the REAL seam accumulates <c>+1</c>, the REAL OutputTail writes
    /// <c>out:messageId</c>), then read the produced value back out of the store. Returns the produced value +
    /// the messageId whose <c>out:</c> blob now holds it (the terminal anchor for a fan-in arrival).</summary>
    private static async Task<(int Produced, Guid MessageId, Guid EntryId)> RunHop(
        DictBackedL2Fake l2, BaseProcessorBase processor, DispatchTestKit.CapturingSendProvider send,
        string label, Guid executionId, int inbound, CancellationToken ct)
    {
        var entryId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        // pre-seed the relocated input blob the downstream hop reads (the orchestrator relocate, modelled by
        // writing the inbound value under this hop's own data: key — a distinct entryId per arrival). The
        // 3-arg write binds to a stubbed real virtual overload (the dict-backed fake persists it to _store).
        var inputBlob = JsonSerializer.Serialize(new { number = inbound, label });
        await l2.Database.StringSetAsync(L2ProjectionKeys.ExecutionData(entryId), inputBlob, TimeSpan.FromMinutes(5));

        var dispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Config(label))
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = executionId,   // non-empty → Mode-1 reuse (preserves the instance lineage)
            EntryId = entryId,
        };

        await BuildPipeline(l2.Multiplexer, Ctx(), processor, send).RunAsync(dispatch, messageId, ct);

        // the REAL OutputTail wrote the produced value to out:messageId — read it back (terminal anchor).
        Assert.True(l2.TryGet(L2ProjectionKeys.OutputData(messageId), out var outBlob),
            $"out: blob missing for {label} (messageId {messageId:D})");
        return (ReadNumber(outBlob), messageId, entryId);
    }

    /// <summary>Drives the SOURCE step A (Mode-2): a Guid.Empty entryId dispatch the REAL seam answers by
    /// seeding {100, 200} and spawning two completed DataResults (distinct minted executionIds) to the -post
    /// queue. Returns the two (executionId, seedValue) pairs captured in <c>send.SentData</c>, ascending by
    /// seed so exec_a (100) is index 0 and exec_b (200) is index 1.</summary>
    private static async Task<IReadOnlyList<(Guid ExecutionId, int Seed)>> RunSource(
        DictBackedL2Fake l2, BaseProcessorBase processor, DispatchTestKit.CapturingSendProvider send,
        CancellationToken ct)
    {
        var sourceDispatch = new EntryStepDispatch(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Config("Step_A"))
        {
            CorrelationId = Guid.NewGuid(),
            ExecutionId = Guid.Empty,   // ENTRY/seed → Mode-2
            EntryId = Guid.Empty,       // SourceStep.IsSource → skips gate/read, empty validatedData
        };
        await BuildPipeline(l2.Multiplexer, Ctx(), processor, send).RunAsync(sourceDispatch, Guid.NewGuid(), ct);

        return send.SentData
            .Select(dr => (dr.ExecutionId, Seed: ReadNumber(dr.Data)))
            .OrderBy(x => x.Seed)
            .ToList();
    }

    // ===== the full-DAG run: returns the per-(execution,label) produced values + the G arrivals ==========

    private sealed record GArrival(Guid MessageId, Guid EntryId, int Value);

    private sealed record DagRun(
        IReadOnlyDictionary<(int Seed, string Label), int> Values,   // per-execution per-label produced value
        IReadOnlyList<GArrival> GArrivalsA,                          // exec_a's two G arrivals (F1, F2 → G)
        IReadOnlyList<GArrival> GArrivalsB);                         // exec_b's two G arrivals

    /// <summary>Runs the WHOLE DAG once over fresh real classes + a fresh store. The two G arrivals per
    /// execution are produced by running G once per inbound branch (F1, F2) via <paramref name="gOrder"/> — a
    /// permutation of {0, 1} over the two F outputs — so a caller can run G in arrival order AND reversed and
    /// compare (the non-joining, order-independent proof).</summary>
    private static async Task<DagRun> RunDag(int[] gOrder, CancellationToken ct)
    {
        var l2 = new DictBackedL2Fake();
        var processor = new global::Processor.Sample.SampleProcessor(
            NullLogger<global::Processor.Sample.SampleProcessor>.Instance);
        var send = new DispatchTestKit.CapturingSendProvider();

        var values = new Dictionary<(int, string), int>();
        var gA = new List<GArrival>();
        var gB = new List<GArrival>();

        // SOURCE A (Mode-2): seed 100/200, two spawned executions.
        var executions = await RunSource(l2, processor, send, ct);

        foreach (var (executionId, seed) in executions)
        {
            // linear B → C (one arrival each).
            var b = await RunHop(l2, processor, send, "Step_B", executionId, seed, ct);
            values[(seed, "Step_B")] = b.Produced;
            var c = await RunHop(l2, processor, send, "Step_C", executionId, b.Produced, ct);
            values[(seed, "Step_C")] = c.Produced;

            // fan-out at C: two independent branches D1→E1→F1 and D2→E2→F2, each on its own entryId chain.
            var fOutputs = new (Guid MessageId, Guid EntryId, int Value)[2];
            foreach (var branch in new[] { 1, 2 })
            {
                var d = await RunHop(l2, processor, send, $"Step_D{branch}", executionId, c.Produced, ct);
                values[(seed, $"Step_D{branch}")] = d.Produced;
                var e = await RunHop(l2, processor, send, $"Step_E{branch}", executionId, d.Produced, ct);
                values[(seed, $"Step_E{branch}")] = e.Produced;
                var f = await RunHop(l2, processor, send, $"Step_F{branch}", executionId, e.Produced, ct);
                values[(seed, $"Step_F{branch}")] = f.Produced;
                fOutputs[branch - 1] = (f.MessageId, f.EntryId, f.Produced);
            }

            // CONVERGENT G (D-07/D-09b): exactly TWO non-joining arrivals, one per inbound branch (F1, F2),
            // each a completed-terminal reading its OWN entryId blob and writing its OWN out: blob. Run them in
            // the caller-supplied order — no shared state, no join, no wait.
            var arrivals = new List<GArrival>();
            foreach (var idx in gOrder)
            {
                var g = await RunHop(l2, processor, send, "Step_G", executionId, fOutputs[idx].Value, ct);
                arrivals.Add(new GArrival(g.MessageId, g.EntryId, g.Produced));
            }
            // IN-03: record the shared terminal value, but make the per-hop integrity fact self-contained —
            // assert BOTH arrivals agree HERE rather than trusting index 0 (the two-arrivals-equal invariant is
            // also covered independently by G_invoked_exactly_twice_on_distinct_entryId_blobs).
            Assert.Equal(arrivals[0].Value, arrivals[1].Value);
            values[(seed, "Step_G")] = arrivals[0].Value;   // both arrivals carry the identical terminal value

            (seed == SeedA ? gA : gB).AddRange(arrivals);
        }

        return new DagRun(values, gA, gB);
    }

    // ===== facts =======================================================================================

    /// <summary>D-09a — PER-HOP VALUE INTEGRITY: every label's surfaced L2 value is exactly seed + hop-count
    /// for BOTH executions (exec_a 101→106, exec_b 201→206); no value drifts mid-chain.</summary>
    [Fact]
    public async Task PerHop_value_integrity_both_chains()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await RunDag(new[] { 0, 1 }, ct);

        // exec_a (seed 100): each hop = prior + 1.
        Assert.Equal(101, run.Values[(SeedA, "Step_B")]);
        Assert.Equal(102, run.Values[(SeedA, "Step_C")]);
        Assert.Equal(103, run.Values[(SeedA, "Step_D1")]);
        Assert.Equal(103, run.Values[(SeedA, "Step_D2")]);
        Assert.Equal(104, run.Values[(SeedA, "Step_E1")]);
        Assert.Equal(104, run.Values[(SeedA, "Step_E2")]);
        Assert.Equal(105, run.Values[(SeedA, "Step_F1")]);
        Assert.Equal(105, run.Values[(SeedA, "Step_F2")]);
        Assert.Equal(TerminalA, run.Values[(SeedA, "Step_G")]);   // 106

        // exec_b (seed 200): the same chain + 100.
        Assert.Equal(201, run.Values[(SeedB, "Step_B")]);
        Assert.Equal(202, run.Values[(SeedB, "Step_C")]);
        Assert.Equal(203, run.Values[(SeedB, "Step_D1")]);
        Assert.Equal(203, run.Values[(SeedB, "Step_D2")]);
        Assert.Equal(204, run.Values[(SeedB, "Step_E1")]);
        Assert.Equal(204, run.Values[(SeedB, "Step_E2")]);
        Assert.Equal(205, run.Values[(SeedB, "Step_F1")]);
        Assert.Equal(205, run.Values[(SeedB, "Step_F2")]);
        Assert.Equal(TerminalB, run.Values[(SeedB, "Step_G")]);   // 206
    }

    /// <summary>D-09b — FAN-IN AT G: Step_G is invoked EXACTLY TWICE per execution (once per inbound branch),
    /// each arrival on its OWN entryId blob writing its OWN out: blob, both Completed, both carrying the
    /// IDENTICAL terminal value (seed + 6).</summary>
    [Fact]
    public async Task G_invoked_exactly_twice_on_distinct_entryId_blobs()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await RunDag(new[] { 0, 1 }, ct);

        foreach (var (arrivals, terminal) in new[] { (run.GArrivalsA, TerminalA), (run.GArrivalsB, TerminalB) })
        {
            Assert.Equal(2, arrivals.Count);                                       // exactly twice
            Assert.NotEqual(arrivals[0].EntryId, arrivals[1].EntryId);             // each reads its OWN entryId blob
            Assert.NotEqual(arrivals[0].MessageId, arrivals[1].MessageId);         // each writes its OWN out: blob
            Assert.All(arrivals, a => Assert.Equal(terminal, a.Value));            // identical terminal value (seed+6)
        }
    }

    /// <summary>D-09b — NON-JOINING / ORDER-INDEPENDENT: reversing the arrival order at G yields IDENTICAL
    /// results (same terminal values, same invocation count). Order independence proves G never waits for or
    /// aggregates the other arrival — it is two completed terminals, not a join.</summary>
    [Fact]
    public async Task G_arrival_order_is_irrelevant_non_joining()
    {
        var ct = TestContext.Current.CancellationToken;
        var forward = await RunDag(new[] { 0, 1 }, ct);
        var reversed = await RunDag(new[] { 1, 0 }, ct);

        // same invocation count + same terminal values regardless of the order the two G arrivals ran.
        Assert.Equal(2, forward.GArrivalsA.Count);
        Assert.Equal(2, reversed.GArrivalsA.Count);
        Assert.Equal(TerminalA, forward.Values[(SeedA, "Step_G")]);
        Assert.Equal(TerminalA, reversed.Values[(SeedA, "Step_G")]);
        Assert.Equal(TerminalB, forward.Values[(SeedB, "Step_G")]);
        Assert.Equal(TerminalB, reversed.Values[(SeedB, "Step_G")]);

        // the SET of terminal values both arrivals carry is identical across the two orders.
        Assert.Equal(
            forward.GArrivalsA.Select(a => a.Value).OrderBy(v => v),
            reversed.GArrivalsA.Select(a => a.Value).OrderBy(v => v));
    }

    /// <summary>D-09c — NO CROSS-EXECUTIONID CONTAMINATION: no value from the 100 chain ever appears under a
    /// 200-chain label and vice versa.</summary>
    [Fact]
    public async Task No_cross_executionId_contamination()
    {
        var ct = TestContext.Current.CancellationToken;
        var run = await RunDag(new[] { 0, 1 }, ct);

        var chainA = run.Values.Where(kv => kv.Key.Seed == SeedA).Select(kv => kv.Value).ToHashSet();
        var chainB = run.Values.Where(kv => kv.Key.Seed == SeedB).Select(kv => kv.Value).ToHashSet();

        // the 100 chain holds only 101..106; the 200 chain holds only 201..206 — no overlap.
        Assert.All(chainA, v => Assert.InRange(v, 101, 106));
        Assert.All(chainB, v => Assert.InRange(v, 201, 206));
        Assert.Empty(chainA.Intersect(chainB));
        Assert.DoesNotContain(201, chainA);
        Assert.DoesNotContain(106, chainB);
    }

    /// <summary>D-11 — TERMINAL ANCHOR: exactly two distinct Step_G out: blobs persist per execution in the
    /// store, each readable via <c>L2ProjectionKeys.OutputData(messageId)</c> and holding the terminal value.</summary>
    [Fact]
    public async Task Two_distinct_G_out_blobs_persist_per_execution()
    {
        var ct = TestContext.Current.CancellationToken;

        // re-run the DAG against a store we keep so we can read the persisted out: blobs back by messageId.
        var l2 = new DictBackedL2Fake();
        var processor = new global::Processor.Sample.SampleProcessor(
            NullLogger<global::Processor.Sample.SampleProcessor>.Instance);
        var send = new DispatchTestKit.CapturingSendProvider();
        var executions = await RunSource(l2, processor, send, ct);

        foreach (var (executionId, seed) in executions)
        {
            var b = await RunHop(l2, processor, send, "Step_B", executionId, seed, ct);
            var c = await RunHop(l2, processor, send, "Step_C", executionId, b.Produced, ct);
            var fValues = new int[2];
            foreach (var branch in new[] { 1, 2 })
            {
                var d = await RunHop(l2, processor, send, $"Step_D{branch}", executionId, c.Produced, ct);
                var e = await RunHop(l2, processor, send, $"Step_E{branch}", executionId, d.Produced, ct);
                var f = await RunHop(l2, processor, send, $"Step_F{branch}", executionId, e.Produced, ct);
                fValues[branch - 1] = f.Produced;
            }

            var g1 = await RunHop(l2, processor, send, "Step_G", executionId, fValues[0], ct);
            var g2 = await RunHop(l2, processor, send, "Step_G", executionId, fValues[1], ct);

            Assert.NotEqual(g1.MessageId, g2.MessageId);   // two DISTINCT out: blob keys

            var terminal = seed == SeedA ? TerminalA : TerminalB;
            Assert.True(l2.TryGet(L2ProjectionKeys.OutputData(g1.MessageId), out var blob1));
            Assert.True(l2.TryGet(L2ProjectionKeys.OutputData(g2.MessageId), out var blob2));
            Assert.Equal(terminal, ReadNumber(blob1));
            Assert.Equal(terminal, ReadNumber(blob2));
        }
    }
}
