// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIREEXTENSION001 // Launch configuration types are experimental.

namespace Aspire.Hosting.JavaScript;

internal sealed class BrowserLaunchConfiguration() : ExecutableLaunchConfiguration("browser")
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("web_root")]
    public string WebRoot { get; set; } = string.Empty;

    [JsonPropertyName("browser")]
    public string Browser { get; set; } = "msedge";
}
