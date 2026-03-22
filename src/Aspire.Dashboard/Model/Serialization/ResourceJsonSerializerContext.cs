// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Shared.Model.Serialization;

namespace Aspire.Dashboard.Model.Serialization;

/// <summary>
/// Source-generated JSON serializer context for Resource types.
/// Provides AOT-compatible serialization for resource JSON types.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ResourceJson))]
[JsonSerializable(typeof(ResourceUrlJson))]
[JsonSerializable(typeof(ResourceVolumeJson))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, ResourceHealthReportJson>))]
[JsonSerializable(typeof(ResourceRelationshipJson))]
[JsonSerializable(typeof(Dictionary<string, ResourceCommandJson>))]
internal sealed partial class ResourceJsonSerializerContext : JsonSerializerContext
{
    /// <summary>
    /// Gets the serializer options for resource JSON serialization with indented output.
    /// </summary>
    public static JsonSerializerOptions IndentedOptions { get; } = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = Default
    };
}
