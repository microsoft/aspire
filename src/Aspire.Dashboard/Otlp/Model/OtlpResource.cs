// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;

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

    private readonly ConcurrentDictionary<KeyValuePair<string, string>[], OtlpResourceView> _resourceViews = new(ResourceViewKeyComparer.Instance);

    public OtlpResource(string name, string? instanceId, bool uninstrumentedPeer, OtlpContext context)
    {
        ResourceName = name;
        InstanceId = instanceId;
        UninstrumentedPeer = uninstrumentedPeer;
        Context = context;
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
        return GetView(new OtlpResourceView(this, attributes));
    }

    internal OtlpResourceView GetViewFromProperties(KeyValuePair<string, string>[] properties)
    {
        return GetView(new OtlpResourceView(this, properties));
    }

    private OtlpResourceView GetView(OtlpResourceView view)
    {
        // Inefficient to create this to possibly throw it away.

        if (_resourceViews.TryGetValue(view.Properties, out var resourceView))
        {
            return resourceView;
        }

        if (_resourceViews.Count >= TelemetryRepositoryLimits.MaxResourceViewCount)
        {
            throw new InvalidOperationException($"Resource view limit of {TelemetryRepositoryLimits.MaxResourceViewCount} reached.");
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

/// <summary>
/// Compares resources by their resource key.
/// </summary>
internal sealed class OtlpResourceEqualityComparer : IEqualityComparer<OtlpResource>
{
    public static readonly OtlpResourceEqualityComparer Instance = new();

    private OtlpResourceEqualityComparer()
    {
    }

    public bool Equals(OtlpResource? x, OtlpResource? y) =>
        ReferenceEquals(x, y) || x is not null && y is not null && x.ResourceKey == y.ResourceKey;

    public int GetHashCode(OtlpResource obj) => obj.ResourceKey.GetHashCode();
}
