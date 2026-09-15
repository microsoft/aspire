// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Maui;

internal sealed class MauiLaunchConfiguration
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = MauiPlatformHelper.MauiLaunchConfigurationType;

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "NoDebug";

    [JsonPropertyName("project_path")]
    public string ProjectPath { get; set; } = string.Empty;

    [JsonPropertyName("target_framework")]
    public string TargetFramework { get; set; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = string.Empty;

    [JsonPropertyName("target_kind")]
    public string TargetKind { get; set; } = string.Empty;

    [JsonPropertyName("device")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Device { get; set; }

    [JsonPropertyName("runtime_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeIdentifier { get; set; }

    [JsonPropertyName("msbuild_properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? MsBuildProperties { get; set; }
}
