using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using BaseProcessor.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Observability;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 74 (D-14) absence guard, re-founded from the retired Phase-32.1 dedup fixture. The two dormant
/// dedup counters that this fixture once probed — the processor-side <c>processor_dispatch_deduped</c> and
/// the orchestrator-side <c>orchestrator_result_deduped</c> — are REMOVED (REQ-1/REQ-2). This fixture now
/// asserts the uniform two-counter model in their place from a real <see cref="IMeterFactory"/> (the .NET 8
/// blessed pattern — no <c>static Meter</c> field, so no cross-test static leak):
/// <list type="bullet">
///   <item><description>the surviving uniform counters (<c>MessagesConsumed</c>/<c>MessagesSent</c>) are non-null;</description></item>
///   <item><description>the meter-name consts are unchanged (<c>"BaseProcessor"</c> / <c>"Orchestrator"</c>);</description></item>
///   <item><description>D-14 absence: NO instrument named <c>processor_dispatch_deduped</c> or
///   <c>orchestrator_result_deduped</c> is ever published under its meter (the removed counters emit no series);</description></item>
///   <item><description>a recorded <c>processor_messages_sent</c> measurement carries the NEW camelCase
///   <c>workflowId</c>+<c>processorId</c> label keys (the uniform model REQUIRES <c>workflowId</c>, inverting
///   the old cardinality guard).</description></item>
/// </list>
/// Hermetic (default Category) — no real stack.
/// </summary>
public sealed class BreakerMetricsFacts
{
    private static IMeterFactory NewMeterFactory(out ServiceProvider provider)
    {
        provider = new ServiceCollection()
            .AddMetrics()
            .BuildServiceProvider();
        return provider.GetRequiredService<IMeterFactory>();
    }

    [Fact]
    public void Meter_Name_Consts_Are_Unchanged()
    {
        Assert.Equal("BaseProcessor", ProcessorMetrics.MeterName);
        Assert.Equal("Orchestrator", OrchestratorMetrics.MeterName);
    }

    [Fact]
    public void Processor_DispatchDeduped_Removed_EmitsNoSeries()
    {
        // Phase 74 (REQ-2, D-14): processor_dispatch_deduped is REMOVED. Assert the holder constructs with only
        // the uniform counters AND that NO instrument named processor_dispatch_deduped is ever published on the
        // BaseProcessor meter (absence-of-series). Exercise the surviving counters so the meter is live.
        var publishedNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == ProcessorMetrics.MeterName)
                    publishedNames.Add(instrument.Name);
            };
            listener.Start();

            var metrics = new ProcessorMetrics(meterFactory);

            Assert.NotNull(metrics.MessagesConsumed);
            Assert.NotNull(metrics.MessagesSent);
            metrics.MessagesConsumed.Add(1);
            metrics.MessagesSent.Add(1);
        }

        // D-14 absence: the removed dedup counter publishes no series.
        Assert.DoesNotContain("processor_dispatch_deduped", publishedNames);
    }

    [Fact]
    public void Orchestrator_ResultDeduped_Removed_EmitsNoSeries()
    {
        // Phase 74 (REQ-1, D-14): orchestrator_result_deduped is REMOVED. Assert the holder constructs with only
        // the uniform counters AND that NO instrument named orchestrator_result_deduped is ever published on the
        // Orchestrator meter (absence-of-series).
        var publishedNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, _) =>
            {
                if (instrument.Meter.Name == OrchestratorMetrics.MeterName)
                    publishedNames.Add(instrument.Name);
            };
            listener.Start();

            var metrics = new OrchestratorMetrics(meterFactory);

            Assert.NotNull(metrics.MessagesConsumed);
            Assert.NotNull(metrics.MessagesSent);
            metrics.MessagesConsumed.Add(1);
            metrics.MessagesSent.Add(1);
        }

        // D-14 absence: the removed dedup counter publishes no series.
        Assert.DoesNotContain("orchestrator_result_deduped", publishedNames);
    }

    [Fact]
    public void Recorded_Measurement_Carries_Expected_Label_Keys()
    {
        // Phase 74 (REQ-2): the dedup-counter this guard targeted (processor_dispatch_deduped) was REMOVED.
        // The full absence-assert rewrite is owned by Plan 04 (D-14). MINIMAL compile-unblock: repoint the
        // MeterListener probe to a surviving uniform counter (processor_messages_sent) and assert the NEW
        // camelCase label contract — workflowId+processorId present (the uniform model now REQUIRES workflowId,
        // inverting the old cardinality guard). Scope the listener to THIS test's exact instrument instance (by
        // reference identity), NOT by meter name, so the capture is hermetic under parallelism.
        var processorId = new System.Guid("99999999-9999-9999-9999-999999999999").ToString("D");
        var workflowId  = new System.Guid("11111111-1111-1111-1111-111111111111").ToString("D");

        var capturedTagKeySets = new List<string[]>();

        var procFactory = NewMeterFactory(out var procProvider);
        using (procProvider)
        {
            var procMetrics = new ProcessorMetrics(procFactory);

            using var listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (ReferenceEquals(instrument, procMetrics.MessagesSent))
                        l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
            {
                if (ReferenceEquals(instrument, procMetrics.MessagesSent))
                    capturedTagKeySets.Add(tags.ToArray().Select(t => t.Key).ToArray());
            });
            listener.Start();

            procMetrics.MessagesSent.Add(1,
                new KeyValuePair<string, object?>("workflowId", workflowId),
                new KeyValuePair<string, object?>("processorId", processorId));
        }

        var keys = Assert.Single(capturedTagKeySets);
        Assert.Contains("workflowId", keys);
        Assert.Contains("processorId", keys);
    }
}
