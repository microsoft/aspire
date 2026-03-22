// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;

namespace Aspire.Dashboard.Otlp.Model;

[DebuggerDisplay("ResourceName = {ResourceName}, InstanceId = {InstanceId}")]
public class OtlpResource : IOtlpResource
{
    public const string SERVICE_NAME = "service.name";
    public const string SERVICE_INSTANCE_ID = "service.instance.id";
    public const string PROCESS_EXECUTABLE_NAME = "process.executable.name";

    public string ResourceName { get; }
    public string? InstanceId { get; }
    public OtlpContext Context { get; }
    // This flag indicates whether the app was created for an uninstrumented peer.
    // It's used to hide the app on pages that don't use uninstrumented peers.
    // Traces uses uninstrumented peers, structured logs and metrics don't.
    public bool UninstrumentedPeer { get; private set; }

    /// <summary>
    /// Indicates whether this resource has structured logs.
    /// </summary>
    public bool HasLogs { get; internal set; }

    /// <summary>
    /// Indicates whether this resource has traces.
    /// </summary>
    public bool HasTraces { get; internal set; }

    /// <summary>
    /// Indicates whether this resource has metrics.
    /// </summary>
    public bool HasMetrics { get; internal set; }

    public ResourceKey ResourceKey => new ResourceKey(ResourceName, InstanceId);

    private readonly ReaderWriterLockSlim _metricsLock = new();
    private readonly Dictionary<string, OtlpScope> _meters = new();
    private readonly Dictionary<OtlpInstrumentKey, OtlpInstrument> _instruments = new();
    private readonly ConcurrentDictionary<KeyValuePair<string, string>[], OtlpResourceView> _resourceViews = new(ResourceViewKeyComparer.Instance);

    public OtlpResource(string name, string? instanceId, bool uninstrumentedPeer, OtlpContext context)
    {
        ResourceName = name;
        InstanceId = instanceId;
        UninstrumentedPeer = uninstrumentedPeer;
        Context = context;
    }

    public void AddMetrics(AddContext context, RepeatedField<ScopeMetrics> scopeMetrics)
    {
        _metricsLock.EnterWriteLock();

        try
        {
            // Temporary attributes array to use when adding metrics to the instruments.
            KeyValuePair<string, string>[]? tempAttributes = null;

            foreach (var sm in scopeMetrics)
            {
                if (!OtlpHelpers.TryGetOrAddScope(_meters, sm.Scope, Context, TelemetryType.Metrics, out var scope))
                {
                    context.FailureCount += sm.Metrics.Count;
                    continue;
                }

                foreach (var metric in sm.Metrics)
                {
                    OtlpInstrument instrument;

                    try
                    {
                        if (string.IsNullOrEmpty(metric.Name))
                        {
                            throw new InvalidOperationException("Instrument name is required.");
                        }

                        var instrumentKey = new OtlpInstrumentKey(scope.Name, metric.Name);
                        ref var instrumentRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_instruments, instrumentKey, out _);
                        if (instrumentRef == null)
                        {
                            // Adds to dictionary if not present.
                            instrumentRef = new OtlpInstrument
                            {
                                Summary = new OtlpInstrumentSummary
                                {
                                    Name = metric.Name,
                                    Description = metric.Description,
                                    Unit = metric.Unit,
                                    Type = MapMetricType(metric.DataCase),
                                    AggregationTemporality = MapAggregationTemporality(metric),
                                    Parent = scope
                                },
                                Context = Context
                            };

                            Context.Logger.LogTrace("Added metric instrument '{InstrumentName}' for scope '{ScopeName}'.", instrumentRef.Summary.Name, scope.Name);
                        }

                        instrument = instrumentRef;
                    }
                    catch (Exception ex)
                    {
                        // If we can't create the instrument then all data points for it are failures.
                        context.FailureCount += GetMetricDataPointCount(metric);
                        Context.Logger.LogInformation(ex, "Error adding metric instrument {MetricName}.", metric.Name);
                        continue;
                    }

                    AddMetrics(instrument, metric, context, ref tempAttributes);
                }
            }
        }
        finally
        {
            _metricsLock.ExitWriteLock();
        }
    }

    private static int GetMetricDataPointCount(Metric metric)
    {
        return metric.DataCase switch
        {
            Metric.DataOneofCase.Gauge => metric.Gauge.DataPoints.Count,
            Metric.DataOneofCase.Sum => metric.Sum.DataPoints.Count,
            Metric.DataOneofCase.Histogram => metric.Histogram.DataPoints.Count,
            Metric.DataOneofCase.Summary => metric.Summary.DataPoints.Count,
            Metric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram.DataPoints.Count,
            _ => 0,
        };
    }

    private void AddMetrics(OtlpInstrument instrument, Metric metric, AddContext context, ref KeyValuePair<string, string>[]? tempAttributes)
    {
        switch (metric.DataCase)
        {
            case Metric.DataOneofCase.Gauge:
                foreach (var d in metric.Gauge.DataPoints)
                {
                    try
                    {
                        instrument.FindScope(d.Attributes, ref tempAttributes).AddPointValue(d, Context);
                        context.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        context.FailureCount++;
                        Context.Logger.LogInformation(ex, "Error adding metric.");
                    }
                }
                break;
            case Metric.DataOneofCase.Sum:
                foreach (var d in metric.Sum.DataPoints)
                {
                    try
                    {
                        instrument.FindScope(d.Attributes, ref tempAttributes).AddPointValue(d, Context);
                        context.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        context.FailureCount++;
                        Context.Logger.LogInformation(ex, "Error adding metric.");
                    }
                }
                break;
            case Metric.DataOneofCase.Histogram:
                foreach (var d in metric.Histogram.DataPoints)
                {
                    try
                    {
                        instrument.FindScope(d.Attributes, ref tempAttributes).AddHistogramValue(d, Context);
                        context.SuccessCount++;
                    }
                    catch (Exception ex)
                    {
                        context.FailureCount++;
                        Context.Logger.LogInformation(ex, "Error adding metric.");
                    }
                }
                break;
            case Metric.DataOneofCase.Summary:
                context.FailureCount += metric.Summary.DataPoints.Count;
                Context.Logger.LogInformation("Error adding summary metrics. Summary is not supported.");
                break;
            case Metric.DataOneofCase.ExponentialHistogram:
                context.FailureCount += metric.ExponentialHistogram.DataPoints.Count;
                Context.Logger.LogInformation("Error adding exponential histogram metrics. Exponential histogram is not supported.");
                break;
        }
    }

    public void ClearMetrics()
    {
        _metricsLock.EnterWriteLock();

        try
        {
            _instruments.Clear();
        }
        finally
        {
            _metricsLock.ExitWriteLock();
        }
    }

    private static OtlpInstrumentType MapMetricType(Metric.DataOneofCase data)
    {
        return data switch
        {
            Metric.DataOneofCase.Gauge => OtlpInstrumentType.Gauge,
            Metric.DataOneofCase.Sum => OtlpInstrumentType.Sum,
            Metric.DataOneofCase.Histogram => OtlpInstrumentType.Histogram,
            _ => OtlpInstrumentType.Unsupported
        };
    }

    private static OtlpAggregationTemporality MapAggregationTemporality(Metric metric)
    {
        return metric.DataCase switch
        {
            Metric.DataOneofCase.Sum => (OtlpAggregationTemporality)metric.Sum.AggregationTemporality,
            Metric.DataOneofCase.Histogram => (OtlpAggregationTemporality)metric.Histogram.AggregationTemporality,
            Metric.DataOneofCase.ExponentialHistogram => (OtlpAggregationTemporality)metric.ExponentialHistogram.AggregationTemporality,
            _ => OtlpAggregationTemporality.Unspecified
        };
    }

    public OtlpInstrument? GetInstrument(string meterName, string instrumentName, DateTime? valuesStart, DateTime? valuesEnd)
    {
        _metricsLock.EnterReadLock();

        try
        {
            if (!_instruments.TryGetValue(new OtlpInstrumentKey(meterName, instrumentName), out var instrument))
            {
                return null;
            }

            return OtlpInstrument.Clone(instrument, cloneData: true, valuesStart: valuesStart, valuesEnd: valuesEnd);
        }
        finally
        {
            _metricsLock.ExitReadLock();
        }
    }

    public List<OtlpInstrumentSummary> GetInstrumentsSummary()
    {
        _metricsLock.EnterReadLock();

        try
        {
            var instruments = new List<OtlpInstrumentSummary>(_instruments.Count);
            foreach (var instrument in _instruments)
            {
                instruments.Add(instrument.Value.Summary);
            }
            return instruments;
        }
        finally
        {
            _metricsLock.ExitReadLock();
        }
    }

    public static Dictionary<string, List<OtlpResource>> GetReplicasByResourceName(IEnumerable<OtlpResource> allResources)
    {
        return allResources
            .GroupBy(resource => resource.ResourceName, StringComparers.ResourceName)
            .ToDictionary(grouping => grouping.Key, grouping => grouping.ToList());
    }

    public static string GetResourceName(OtlpResourceView resource, IReadOnlyList<IOtlpResource> allResources) =>
        OtlpHelpers.GetResourceName(resource.Resource, allResources);

    internal List<OtlpResourceView> GetViews() => _resourceViews.Values.ToList();

    internal OtlpResourceView GetView(RepeatedField<KeyValue> attributes)
    {
        // Inefficient to create this to possibly throw it away.
        var view = new OtlpResourceView(this, attributes);

        if (_resourceViews.TryGetValue(view.Properties, out var resourceView))
        {
            return resourceView;
        }

        return _resourceViews.GetOrAdd(view.Properties, view);
    }

    internal void SetUninstrumentedPeer(bool uninstrumentedPeer)
    {
        // An app could initially be created for an uninstrumented peer and then telemetry is received from it.
        // This method "upgrades" the resource to not be for an uninstrumented peer when appropriate.
        if (UninstrumentedPeer && !uninstrumentedPeer)
        {
            UninstrumentedPeer = uninstrumentedPeer;
        }
    }

    /// <summary>
    /// Resource views are equal when all properties are equal.
    /// </summary>
    private sealed class ResourceViewKeyComparer : IEqualityComparer<KeyValuePair<string, string>[]>
    {
        public static readonly ResourceViewKeyComparer Instance = new();

        public bool Equals(KeyValuePair<string, string>[]? x, KeyValuePair<string, string>[]? y)
        {
            if (x == y)
            {
                return true;
            }
            if (x == null || y == null)
            {
                return false;
            }
            if (x.Length != y.Length)
            {
                return false;
            }

            for (var i = 0; i < x.Length; i++)
            {
                if (!string.Equals(x[i].Key, y[i].Key, StringComparisons.OtlpAttribute))
                {
                    return false;
                }
                if (!string.Equals(x[i].Value, y[i].Value, StringComparisons.OtlpAttribute))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode([DisallowNull] KeyValuePair<string, string>[] obj)
        {
            var hashCode = new HashCode();
            for (var i = 0; i < obj.Length; i++)
            {
                hashCode.Add(StringComparers.OtlpAttribute.GetHashCode(obj[i].Key));
                hashCode.Add(StringComparers.OtlpAttribute.GetHashCode(obj[i].Value));
            }

            return hashCode.ToHashCode();
        }
    }
}
