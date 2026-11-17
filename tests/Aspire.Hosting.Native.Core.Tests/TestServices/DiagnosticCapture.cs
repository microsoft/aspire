// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Aspire.Hosting.Native.Diagnostics;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal sealed class DiagnosticCapture : IDisposable
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;
    private readonly OperationEventSubscription _subscription;
    private readonly ConcurrentQueue<OperationEvent> _events = [];

    public DiagnosticCapture(bool sampleActivities, params string[] additionalSources)
    {
        Root = new Activity("native.diagnostics.test")
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
            .SetIdFormat(ActivityIdFormat.W3C).Start();
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NativeDiagnostics.SourceName || additionalSources.Contains(source.Name, StringComparer.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) => sampleActivities && options.TraceId == Root.TraceId
                ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == Root.TraceId)
                {
                    Activities.Enqueue(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(_activities);
        _meters = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == NativeDiagnostics.SourceName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
        _meters.Start();
        // Captures drain at assertion boundaries rather than on a scheduler.
        // A value filter isolates parallel traces without installing a producer
        // callback. Retained capture history still has an explicit upper bound.
        _subscription = NativeDiagnostics.Events.Subscribe(capacity: 65536, traceId: Root.TraceId);
    }

    public Activity Root { get; }
    public ConcurrentQueue<Activity> Activities { get; } = [];
    public ConcurrentQueue<OperationEvent> Events
    {
        get
        {
            // Producer writes are complete when a synchronous operation returns.
            // Drain the channel directly for deterministic assertions, not a
            // background consumer that might not yet have been scheduled.
            while (_subscription.Reader.TryRead(out var value))
            {
                if (value.TraceId == Root.TraceId.ToString())
                {
                    _events.Enqueue(value);
                }
            }
            if (_subscription.Reader.Completion.IsCompleted)
            {
                _subscription.Reader.Completion.GetAwaiter().GetResult();
            }

            return _events;
        }
    }
    public ConcurrentQueue<Measurement> Measurements { get; } = [];

    public void Dispose()
    {
        _subscription.Dispose();
        _meters.Dispose();
        _activities.Dispose();
        Root.Dispose();
    }

    private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        if (Activity.Current?.TraceId == Root.TraceId)
        {
            Measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray()));
        }
    }
}

internal sealed record Measurement(string Name, double Value, KeyValuePair<string, object?>[] Tags);
