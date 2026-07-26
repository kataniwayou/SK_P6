namespace BaseConsole.Core.Health;

/// <summary>
/// Shared <see cref="ILivenessHeartbeat"/> implementation (HLTH-01). <c>public sealed</c> so
/// <c>services.AddSingleton&lt;ILivenessHeartbeat, LivenessHeartbeat&gt;()</c> resolves across the assembly
/// boundary without <c>InternalsVisibleTo</c> (same rationale as the retired <c>KeeperLivenessState</c>).
///
/// <para>
/// <b>Intended implementation (86-03 GREEN):</b> mirror <c>KeeperLivenessState</c> exactly — a
/// <c>private long _ticks</c> holder with <c>0</c> = "never beaten" sentinel; <see cref="Beat"/> does
/// <c>Interlocked.Exchange(ref _ticks, clock.GetUtcNow().UtcDateTime.Ticks)</c>; <see cref="Current"/> does
/// <c>Interlocked.Read(ref _ticks)</c> returning <c>null</c> at <c>0</c> else the reconstructed UTC instant.
/// A <c>DateTime?</c> cannot be <c>volatile</c>, hence the long-ticks idiom. The injected clock is the
/// single time source (test-swappable via <c>FakeTimeProvider</c>).
/// </para>
///
/// <para><b>RED skeleton:</b> both members throw <see cref="NotImplementedException"/> so the Phase-86
/// behavioral tests fail on behavior (not compile). The Interlocked logic lands in 86-03.</para>
/// </summary>
public sealed class LivenessHeartbeat : ILivenessHeartbeat
{
    private readonly TimeProvider _clock;

    // 0 = "never beaten" sentinel. Beat() only ever stamps a 2024+ UTC instant (DateTime.Ticks far from 0),
    // so 0 is unambiguously "no beat yet" — no real tick produces 0. Mirrors the retired KeeperLivenessState.
    private long _ticks;

    public LivenessHeartbeat(TimeProvider clock) => _clock = clock;

    public void Beat()
        => Interlocked.Exchange(ref _ticks, _clock.GetUtcNow().UtcDateTime.Ticks);

    public DateTime? Current
    {
        get
        {
            var t = Interlocked.Read(ref _ticks);
            return t == 0 ? null : new DateTime(t, DateTimeKind.Utc);
        }
    }
}
