using System.Diagnostics.Metrics;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Zero-new-dependency BCL <see cref="MeterListener"/>-based counter-capture seam (Phase 72 Wave-0 / A1
/// Option B). Subscribes to the in-process <c>Orchestrator</c> meter and accumulates every
/// <c>long</c> measurement of a single named instrument (default <c>orchestrator_step_unresolved</c>)
/// together with its tags — so a hermetic fact can assert a counter incremented AND carried its
/// <c>workflowId</c> tag WITHOUT a live Prometheus collector or the
/// <c>Microsoft.Extensions.Diagnostics.Testing</c> package (REQ-6 hermetic isolation; no new NuGet).
/// <para>
/// Per-instance <see cref="IDisposable"/> — no static state, so no cross-test leak in the shared
/// hermetic process (T-72-06). Construct it BEFORE the metric is incremented, exercise, then read
/// <see cref="Total"/> / <see cref="Count"/> / <see cref="Tags"/>:
/// <code>
/// using var mc = new MeterCollector("Orchestrator", "orchestrator_step_unresolved");
/// // ... exercise the pipeline ...
/// Assert.Equal(1, mc.Total);
/// Assert.Contains(mc.Tags[0], t => t.Key == "workflowId");
/// </code>
/// </para>
/// </summary>
public sealed class MeterCollector : IDisposable
{
    private readonly MeterListener _listener;
    private readonly object _gate = new();
    private readonly List<(long Value, KeyValuePair<string, object?>[] Tags)> _measurements = new();

    public MeterCollector(string meterName = "Orchestrator", string instrumentName = "orchestrator_step_unresolved")
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == meterName && instrument.Name == instrumentName)
                    listener.EnableMeasurementEvents(instrument);
            },
        };

        _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            lock (_gate)
                _measurements.Add((measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    /// <summary>Sum of every captured measurement value (the counter's total increment).</summary>
    public long Total
    {
        get { lock (_gate) return _measurements.Sum(m => m.Value); }
    }

    /// <summary>Number of distinct measurements recorded (one per <c>.Add(...)</c> increment).</summary>
    public int Count
    {
        get { lock (_gate) return _measurements.Count; }
    }

    /// <summary>The per-measurement tag arrays, in capture order (e.g. assert a <c>workflowId</c> tag).</summary>
    public IReadOnlyList<KeyValuePair<string, object?>[]> Tags
    {
        get { lock (_gate) return _measurements.Select(m => m.Tags).ToList(); }
    }

    public void Dispose() => _listener.Dispose();
}
