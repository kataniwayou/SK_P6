using Xunit;

namespace BaseApi.Tests.Observability;

/// <summary>
/// Drift guard (phase-74 post-mortem): the orchestrator fire-count metric name is hard-coded in the
/// PowerShell harness (Get-FireCount) AND consumed by the analyzer. A rename that updates one but not
/// the other silently aborts the fault suite. This fact fails at build time if the harness script no
/// longer references the LiveMetricNames token the analyzer expects.
/// </summary>
public sealed class HarnessMetricDriftFacts
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "scripts", "phase-67-harness.ps1")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.True(dir is not null, "Could not locate repo root (scripts/phase-67-harness.ps1) above the test bin.");
        return dir!;
    }

    [Fact]
    public void Harness_FireGate_References_The_Orchestrator_Sent_Metric_Name()
    {
        var harness = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "phase-67-harness.ps1"));
        Assert.Contains(LiveMetricNames.OrchestratorMessagesSentTotal, harness);
    }

    [Fact]
    public void Analyzer_Fixture_References_The_Orchestrator_Sent_Metric_Name()
    {
        var analyzer = File.ReadAllText(Path.Combine(
            RepoRoot(), "tests", "BaseApi.Tests", "Observability", "AnalyzerE2ETests.cs"));
        Assert.Contains(LiveMetricNames.OrchestratorMessagesSentTotal, analyzer);
    }
}
