namespace BaseConsole.Core.Health;

/// <summary>
/// Shared liveness-heartbeat contract (HLTH-01). A critical loop stamps a monotonic UTC timestamp via
/// <see cref="Beat"/> at the TOP of each iteration (before any infra call — HLTH-03), and the
/// <see cref="LoopLivenessHealthCheck"/> reads <see cref="Current"/> to decide staleness. Generalizes the
/// two retired per-service watchdog holders (<c>ProcessorLivenessState</c> / <c>KeeperLivenessState</c>)
/// into one BaseConsole.Core primitive that keeper + processor opt into.
///
/// <para>
/// <b>Contract:</b> <see cref="Current"/> is <c>null</c> until the first <see cref="Beat"/> (the
/// "never-beaten" sentinel); after the first beat it returns the UTC instant of the most recent beat.
/// Implementations must be lock-free and safe across the loop-writer thread and the health-probe reader
/// thread (the <see cref="System.Threading.Interlocked"/> long-ticks idiom).
/// </para>
/// </summary>
public interface ILivenessHeartbeat
{
    /// <summary>Stamp the current <see cref="System.TimeProvider"/> instant as the latest liveness beat.</summary>
    void Beat();

    /// <summary>The UTC instant of the most recent <see cref="Beat"/>, or <c>null</c> if never beaten.</summary>
    DateTime? Current { get; }
}
