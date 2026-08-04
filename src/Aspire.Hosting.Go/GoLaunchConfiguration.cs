// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Go;

internal sealed class GoLaunchConfiguration() : ExecutableLaunchConfiguration("go")
{
    /// <summary>
    /// The path to the Go package or main package directory to debug.
    /// Corresponds to the <c>program</c> field in VS Code's Go launch configuration.
    /// </summary>
    [JsonPropertyName("program")]
    public string Program { get; set; } = string.Empty;

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Build flags passed to Delve when VS Code launches the Go debugger.
    /// Corresponds to the <c>buildFlags</c> field in VS Code's Go launch configuration.
    /// </summary>
    [JsonPropertyName("build_flags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BuildFlags { get; set; }
}
