// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting;

/// <summary>
/// Typed representation of the JSON output from
/// <c>dotnet msbuild -getProperty:Prop1 -getProperty:Prop2</c>.
/// </summary>
internal class MSBuildPropertiesOutput
{
    public MSBuildManifestProperties Properties { get; set; } = new();
}

internal class MSBuildManifestProperties
{
    public string StaticWebAssetEndpointsBuildManifestPath { get; set; } = "";
    public string StaticWebAssetDevelopmentManifestPath { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, System.Text.Json.JsonElement>? ExtensionData { get; set; }
}
