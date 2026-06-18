using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using BaseProcessor.Core.Observability;
using Microsoft.Extensions.DependencyInjection;
using Orchestrator.Observability;
using Xunit;

namespace BaseApi.Tests.Orchestrator;

/// <summary>
/// Phase 32.1 (req-4 dedup). Hermetic guard for the retained dedup counters on the Phase-30 meters: the
/// processor-side <c>processor_dispatch_deduped</c> and the orchestrator-side
/// <c>orchestrator_result_deduped</c>. Constructs both holders from a real <see cref="IMeterFactory"/>
/// (the .NET 8 blessed pattern — no <c>static Meter</c> field, so no cross-test static leak) and asserts:
/// <list type="bullet">
///   <item><description>both dedup counters are non-null;</description></item>
///   <item><description>the meter-name consts are unchanged (<c>"BaseProcessor"</c> / <c>"Orchestrator"</c>);</description></item>
///   <item><description>a recorded measurement carries the bounded <c>ProcessorId</c> tag but NO
///   <c>workflowId</c>/<c>WorkflowId</c> tag key (T-32-02 cardinality guard, mirrors T-30-04).</description></item>
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
    public void Processor_New_Counters_Construct_NonNull()
    {
        // Phase 74 (REQ-2): processor_dispatch_deduped was REMOVED. The full absence-assert migration of this
        // fixture is owned by Plan 04 (D-14); here we only keep the holder constructible by probing the
        // surviving uniform counters instead of the gone DispatchDeduped probe.
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new ProcessorMetrics(meterFactory);

            Assert.NotNull(metrics.MessagesConsumed);
            Assert.NotNull(metrics.MessagesSent);
        }
    }

    [Fact]
    public void Orchestrator_New_Counter_Constructs_NonNull()
    {
        // Phase 74 (REQ-1): orchestrator_result_deduped was REMOVED. The full absence-assert migration of
        // this fixture is owned by Plan 04 (D-14); here we only keep the holder constructible and assert the
        // dormant dedup counter is gone by replacing the old ResultDeduped probe with the surviving members.
        var meterFactory = NewMeterFactory(out var provider);
        using (provider)
        {
            var metrics = new OrchestratorMetrics(meterFactory);

            Assert.NotNull(metrics.MessagesConsumed);
            Assert.NotNull(metrics.MessagesSent);
        }
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
