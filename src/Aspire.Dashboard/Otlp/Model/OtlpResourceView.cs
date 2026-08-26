// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Storage;
using Google.Protobuf.Collections;
using OpenTelemetry.Proto.Common.V1;

namespace Aspire.Dashboard.Otlp.Model;

[DebuggerDisplay("Resource = {Resource}, Properties = {Properties.Length}")]
public class OtlpResourceView
{
    public ResourceKey ResourceKey => Resource.ResourceKey;
    public OtlpResource Resource { get; }
    public KeyValuePair<string, string>[] Properties { get; }

    public OtlpResourceView(OtlpResource resource, RepeatedField<KeyValue> attributes)
    {
        Resource = resource;

        var properties = attributes.ToKeyValuePairs(resource.Context, filter: attribute =>
        {
            switch (attribute.Key)
            {
                case OtlpResource.SERVICE_NAME:
                case OtlpResource.SERVICE_INSTANCE_ID:
                    // Explicitly ignore these
                    return false;
                default:
                    return true;
            }
        });

        // Sort so keys are in a consistent order for equality check.
        Array.Sort(properties, (p1, p2) => string.Compare(p1.Key, p2.Key, StringComparisons.OtlpAttribute));

        Properties = properties;
    }

    internal OtlpResourceView(OtlpResource resource, KeyValuePair<string, string>[] properties)
    {
        Resource = resource;
        Properties = properties;
    }

    public List<OtlpDisplayField> AllProperties()
    {
        var props = new List<OtlpDisplayField>
        {
            new OtlpDisplayField { DisplayName = "service.name", Key = KnownResourceFields.ServiceNameField, Value = Resource.ResourceName }
        };

        if (Resource.InstanceId is { } instanceId)
        {
            props.Add(new OtlpDisplayField { DisplayName = "service.instance.id", Key = KnownResourceFields.ServiceInstanceIdField, Value = instanceId });
        }

        foreach (var kv in Properties)
        {
            props.Add(new OtlpDisplayField { DisplayName = kv.Key, Key = $"unknown-{kv.Key}", Value = kv.Value });
        }

        return props;
    }
}
