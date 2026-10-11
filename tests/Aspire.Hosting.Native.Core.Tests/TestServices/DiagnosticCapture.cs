// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using Aspire.Hosting.Native.Diagnostics;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal sealed class DiagnosticCapture : IDisposable, IObserver<OperationEvent>
{
    private readonly ActivityListener _activities;
    private readonly MeterListener _meters;
    private readonly IDisposable _subscription;

    public DiagnosticCapture(bool sampleActivities)
    {
        Root = new Activity("native.diagnostics.test")
            .SetParentId(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded)
            .SetIdFormat(ActivityIdFormat.W3C).Start();
        _activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NativeDiagnostics.SourceName,
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
        _subscription = NativeDiagnostics.Events.Subscribe(this);
    }

    public Activity Root { get; }
    public ConcurrentQueue<Activity> Activities { get; } = [];
    public ConcurrentQueue<OperationEvent> Events { get; } = [];
    public ConcurrentQueue<Measurement> Measurements { get; } = [];

    public void OnNext(OperationEvent value)
    {
        if (value.TraceId == Root.TraceId.ToString())
        {
            Events.Enqueue(value);
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) => ExceptionDispatchInfo.Throw(error);

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
