// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Represents resource information.
/// </summary>
internal sealed class OtlpResourceJson
{
    /// <summary>
    /// Set of attributes that describe the resource.
    /// </summary>
    [JsonPropertyName("attributes")]
    public OtlpKeyValueJson[]? Attributes { get; set; }

    /// <summary>
    /// The number of dropped attributes.
    /// </summary>
    [JsonPropertyName("droppedAttributesCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public uint DroppedAttributesCount { get; set; }

    /// <summary>
    /// Set of entities that participate in this resource.
    /// </summary>
    [JsonPropertyName("entityRefs")]
    public OtlpEntityRefJson[]? EntityRefs { get; set; }

    /// <summary>
    /// Gets the service.name attribute value from the resource.
    /// </summary>
    public string GetServiceName()
    {
        if (Attributes is null)
        {
            return "unknown";
        }

        foreach (var attr in Attributes)
        {
            if (attr.Key == "service.name" && attr.Value?.StringValue is not null)
            {
                return attr.Value.StringValue;
            }
        }

        return "unknown";
    }

    /// <summary>
    /// Gets the service.instance.id attribute value from the resource.
    /// </summary>
    public string? GetServiceInstanceId()
    {
        if (Attributes is null)
        {
            return null;
        }

        foreach (var attr in Attributes)
        {
            if (attr.Key == "service.instance.id" && attr.Value?.StringValue is not null)
            {
                return attr.Value.StringValue;
            }
        }

        return null;
    }
}
