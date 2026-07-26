using System.Net;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// Phase 61 / PROBE-01/02 — the END-TO-END integration proof that <c>AddBaseProcessor</c> surfaces the
/// liveness self-watchdog on the embedded <c>/health/live</c> listener. Ported for Phase 86 (86-07): the
/// processor <c>/health/live</c> is now the SHARED <c>LoopLivenessHealthCheck</c>
/// (<c>AddConsoleLivenessWatchdog</c>) reading the shared <see cref="BaseConsole.Core.Health.ILivenessHeartbeat"/>
/// — NOT the retired <c>LivenessWatchdogHealthCheck</c> that read <c>IProcessorLivenessState</c> and carried
/// a per-schema summary. The new verdict is timestamp-only, so this class drops the summary-body assertions
/// and drives the heartbeat instead of seeding L1:
/// <list type="bullet">
///   <item>un-beaten heartbeat ⇒ the aggregate flips Unhealthy ("liveness loop not started") ⇒ HTTP 503
///   (the default HealthCheckOptions maps an Unhealthy aggregate to ServiceUnavailable).</item>
///   <item>fresh beat ⇒ 200 Healthy ("live").</item>
///   <item>the body leaks no connection-string token or stack frame (T-61-07, mirrors
///   <see cref="ConsoleHealthLiveTests"/>.Live_Body_Has_No_Secrets / T-18-08).</item>
/// </list>
///
/// <para>
/// <b>Staleness verdict math</b> (fresh / stale / null / exact-boundary) is unit-covered deterministically by
/// <c>LoopLivenessHealthCheckTests</c> (86-03) with a <c>FakeTimeProvider</c>; the real embedded host uses
/// <c>TimeProvider.System</c>, so this E2E proves only the wiring path (fresh→200, un-beaten→503).
/// </para>
///
/// <para>
/// <b>Shared-fixture isolation.</b> <c>IClassFixture</c> shares ONE fixture instance across all facts and
/// xUnit runs them in nondeterministic order. A beat is monotonic and irreversible (there is no "un-beat"),
/// so the un-beaten null case lives in its OWN class against a dedicated <see cref="NeverBeatingFixture"/>
/// that is constructed but NEVER beaten — its embedded listener only ever sees <c>Current == null</c>. The
/// fresh/no-secret facts share the beating <see cref="ProcessorConsoleTestHostFixture"/> and each beats
/// immediately before its GET (the watchdog re-resolves <c>Current</c> on every check, so a beat-then-GET
/// within one fact is deterministic regardless of sibling order).
/// </para>
/// </summary>
[Trait("Phase", "61")]
public sealed class ProcessorHealthLiveTests : IClassFixture<ProcessorConsoleTestHostFixture>
{
    private readonly ProcessorConsoleTestHostFixture _fixture;

    public ProcessorHealthLiveTests(ProcessorConsoleTestHostFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Live_Is_Healthy_When_Heartbeat_Fresh()
    {
        var ct = TestContext.Current.CancellationToken;

        _fixture.BeatLiveness();   // fresh beat (real clock) — within the k=3 × 10s = 30s stale window

        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Phase 86: the shared watchdog verdict is timestamp-only "live" (no per-schema summary body).
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Healthy\"", body);
    }

    [Fact]
    public async Task Live_Body_Has_No_Secrets()
    {
        var ct = TestContext.Current.CancellationToken;

        _fixture.BeatLiveness();

        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // T-61-07: status only — no connection-string secret, no stack-trace frame.
        Assert.DoesNotContain("Password=", body);
        Assert.DoesNotContain("abortConnect", body);   // Redis connection-string token
        Assert.DoesNotContain("   at ", body);          // .NET stack-trace frame marker
    }
}

/// <summary>
/// Phase 61 / PROBE-01 — the isolated null-verdict proof, ported for Phase 86 (86-07). Lives in its OWN
/// class against a fixture that is NEVER beaten, so the shared <c>ILivenessHeartbeat.Current</c> stays null
/// and the shared <c>LoopLivenessHealthCheck</c> reports "liveness loop not started" ⇒ Unhealthy ⇒ 503.
/// Isolating the null case in a separate class fixture prevents a beat from a sibling fact leaking into the
/// null state under shared-fixture ordering (a beat cannot be undone).
/// </summary>
[Trait("Phase", "61")]
public sealed class ProcessorHealthLiveNullTests
    : IClassFixture<ProcessorHealthLiveNullTests.NeverBeatingFixture>
{
    private readonly NeverBeatingFixture _fixture;

    public ProcessorHealthLiveNullTests(NeverBeatingFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Live_Is_Unhealthy_When_Heartbeat_Null()
    {
        var ct = TestContext.Current.CancellationToken;

        // Do NOT beat — Current is null (the loop crashed before its first beat).
        var response = await _fixture.HttpClient.GetAsync("/health/live", ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Contains("\"status\":\"Unhealthy\"", body);
    }

    /// <summary>A processor fixture that is constructed but never beaten — Current stays null.</summary>
    public sealed class NeverBeatingFixture : ProcessorConsoleTestHostFixture;
}
