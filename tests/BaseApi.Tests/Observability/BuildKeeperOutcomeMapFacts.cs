using System.Text.Json;
using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Hermetic (NON-RealStack) facts pinning the <c>"reinject"</c>-wins tie-break in
/// <see cref="AnalyzerE2ETests.BuildKeeperOutcomeMap"/> (WR-02). The owning fixture
/// (<see cref="AnalyzerE2ETests"/>) is <c>Category=RealStack</c> and compile-gated in the Docker-less
/// sandbox, so its per-<c>(corr,exec)</c> keeper join map is otherwise proven only indirectly via the
/// engine facts (single-outcome cases). These facts feed SYNTHETIC keeper hit JSON (the same
/// <c>_source.attributes.{CorrelationId,ExecutionId,ReinjectOutcome}</c> shape the ES search returns)
/// and assert the map resolves to <c>"reinject"</c> for BOTH doc orderings — <c>[drop, reinject]</c>
/// AND <c>[reinject, drop]</c>.
///
/// <para>
/// <b>Why order-independence is load-bearing.</b> The engine's binding contract (D75-4/WR-01) tolerates a
/// keeper <c>"drop"</c> as provably-unrecoverable but treats a <c>"reinject"</c> as a recoverable-but-lost
/// BINDING requirement. If the tie-break ever regressed to plain last-write-wins (a natural simplification
/// given the ES ascending-<c>@timestamp</c> sort), the <c>[reinject, drop]</c> ordering would record
/// <c>"drop"</c> and a recovered execution could be FALSELY tolerated. These facts pin the invariant so that
/// regression fails a hermetic gate.
/// </para>
/// </summary>
public sealed class BuildKeeperOutcomeMapFacts
{
    // A synthetic keeper ES hit in the exact shape BuildKeeperOutcomeSearchBody returns and
    // BuildKeeperOutcomeMap defensively reads: _source.attributes.{CorrelationId,ExecutionId,ReinjectOutcome}.
    private static JsonElement KeeperHit(string corr, string exec, string outcome)
    {
        var json = $$"""
          {
            "_source": {
              "attributes": {
                "CorrelationId": "{{corr}}",
                "ExecutionId": "{{exec}}",
                "ReinjectOutcome": "{{outcome}}"
              }
            }
          }
          """;
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void TieBreak_DropThenReinject_Resolves_Reinject()
    {
        // ES ascending-@timestamp order: the keeper dropped first, then a later reinject succeeded.
        var hits = new List<JsonElement>
        {
            KeeperHit("corr-1", "exec-1", "drop"),
            KeeperHit("corr-1", "exec-1", "reinject"),
        };

        var map = AnalyzerE2ETests.BuildKeeperOutcomeMap(hits);

        Assert.Equal("reinject", map["corr-1|exec-1"]);
    }

    [Fact]
    public void TieBreak_ReinjectThenDrop_Resolves_Reinject()
    {
        // Reverse order: a reinject already landed, then a drop doc arrives later. Plain last-write-wins would
        // downgrade this to "drop" (falsely tolerating a recovered execution). The "skip if already reinject"
        // guard must keep "reinject" — this is the ordering the join-map has no other coverage for.
        var hits = new List<JsonElement>
        {
            KeeperHit("corr-1", "exec-1", "reinject"),
            KeeperHit("corr-1", "exec-1", "drop"),
        };

        var map = AnalyzerE2ETests.BuildKeeperOutcomeMap(hits);

        Assert.Equal("reinject", map["corr-1|exec-1"]);
    }

    [Fact]
    public void SingleDrop_Resolves_Drop()
    {
        // Sanity anchor: a lone "drop" (no competing reinject for the key) stays "drop" — the tie-break only
        // promotes to "reinject" when a reinject doc actually exists, so the tolerated clean-drop path is intact.
        var hits = new List<JsonElement> { KeeperHit("corr-2", "exec-2", "drop") };

        var map = AnalyzerE2ETests.BuildKeeperOutcomeMap(hits);

        Assert.Equal("drop", map["corr-2|exec-2"]);
    }
}
