using BaseConsole.Core.Health;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BaseApi.Tests.Console;

/// <summary>
/// HLTH-01 pure-unit proof of <see cref="LivenessHeartbeat"/>: a fresh holder reads <c>null</c> (the
/// never-beaten sentinel); after <see cref="LivenessHeartbeat.Beat"/> it returns the injected clock's
/// current instant; a later advance + beat updates it. Mirrors the retired <c>KeeperLivenessState</c>
/// long-ticks idiom, now on a swappable <see cref="TimeProvider"/> (test-driven via FakeTimeProvider).
///
/// <para>
/// Wave-0 RED: the <see cref="LivenessHeartbeat"/> skeleton throws <see cref="NotImplementedException"/>
/// from both members, so each test fails on behavior (a thrown exception, not a compile error). 86-03
/// fills the Interlocked long-ticks logic to turn these GREEN.
/// </para>
/// </summary>
[Trait("Phase", "86")]
public sealed class LivenessHeartbeatTests
{
    private static readonly DateTime T = new(2026, 7, 26, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Fresh_Holder_Current_Is_Null()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(T));

        var sut = new LivenessHeartbeat(clock);

        Assert.Null(sut.Current);
    }

    [Fact]
    public void After_Beat_Current_Is_Clock_Instant()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(T));

        var sut = new LivenessHeartbeat(clock);
        sut.Beat();

        Assert.Equal(T, sut.Current);
    }

    [Fact]
    public void Second_Beat_After_Advance_Updates_Current()
    {
        var clock = new FakeTimeProvider();
        clock.SetUtcNow(new DateTimeOffset(T));

        var sut = new LivenessHeartbeat(clock);
        sut.Beat();

        clock.Advance(TimeSpan.FromSeconds(42));
        sut.Beat();

        Assert.Equal(T.AddSeconds(42), sut.Current);
    }
}
