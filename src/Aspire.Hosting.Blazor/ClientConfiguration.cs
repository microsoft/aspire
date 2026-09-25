// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting;

internal sealed class ClientConfiguration
{
    [JsonPropertyName("webAssembly")]
    public WebAssemblyConfiguration? WebAssembly { get; set; }
}

internal sealed class WebAssemblyConfiguration
{
    [JsonPropertyName("environment")]
    public Dictionary<string, string>? Environment { get; set; }
}
