// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting;

internal class EndpointsManifest
{
    public EndpointEntry[] Endpoints { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal class EndpointEntry
{
    public string Route { get; set; } = "";
    public string AssetFile { get; set; } = "";
    public EndpointSelector[]? Selectors { get; set; }
    public EndpointResponseHeader[]? ResponseHeaders { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal class EndpointSelector
{
    public string Name { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal class EndpointResponseHeader
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
