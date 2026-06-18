using System.Diagnostics.Metrics;
using global::Keeper.Observability;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BaseApi.Tests.Keeper;

/// <summary>
/// Phase 74 (REQ-3/REQ-4/REQ-6): hermetic guard for the rewritten <see cref="KeeperMetrics"/> holder.
/// Constructs it from a real <see cref="IMeterFactory"/> (the .NET 8 blessed pattern — no <c>static Meter</c>,
/// so no cross-test static leak in the shared hermetic process) and asserts the uniform two-counter pair
/// (<c>keeper_messages_consumed</c> / <c>keeper_messages_sent</c>) plus the label-less
/// <c>keeper_l2_probe</c> heartbeat are present and emit under the "Keeper" meter, while the legacy
/// <c>keeper_reinject_dropped</c> drop counter is gone. The full metric-fact rewrite + absence asserts on
/// the consumer side are owned by Plan 04 (D-14); this file proves only the meter-class contract.
/// </summary>
public sealed class KeeperMetricsFacts
{
    [Fact]
    public void MeterName_Is_Keeper()
    {
        Assert.Equal("Keeper", KeeperMetrics.MeterName);
    }

    [Fact]
    public void Constructs_With_NonNull_Uniform_Counters_And_Probe()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();

        var metrics = new KeeperMetrics(meterFactory);

        Assert.NotNull(metrics.MessagesConsumed);   // keeper_messages_consumed (Phase 74 REQ-3)
        Assert.NotNull(metrics.MessagesSent);       // keeper_messages_sent (Phase 74 REQ-3)
        Assert.NotNull(metrics.L2Probe);            // keeper_l2_probe (Phase 74 REQ-4, label-less)
    }

    [Fact]
    public void Emits_The_Three_Uniform_Instrument_Names_And_Not_The_Legacy_Drop()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var meterFactory = provider.GetRequiredService<IMeterFactory>();

        var seen = new HashSet<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == KeeperMetrics.MeterName)
            {
                seen.Add(instrument.Name);
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.Start();

        var metrics = new KeeperMetrics(meterFactory);
        // Touch each instrument so it publishes deterministically.
        metrics.MessagesConsumed.Add(1);
        metrics.MessagesSent.Add(1);
        metrics.L2Probe.Add(1);

        Assert.Contains("keeper_messages_consumed", seen);
        Assert.Contains("keeper_messages_sent", seen);
        Assert.Contains("keeper_l2_probe", seen);
        Assert.DoesNotContain("keeper_reinject_dropped", seen);   // REQ-3: legacy drop counter removed
    }
}
