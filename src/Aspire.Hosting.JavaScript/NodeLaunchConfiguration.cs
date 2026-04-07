// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.JavaScript;

internal sealed class NodeLaunchConfiguration() : ExecutableLaunchConfiguration("node")
{
    [JsonPropertyName("script_path")]
    public string ScriptPath { get; set; } = string.Empty;

    [JsonPropertyName("runtime_executable")]
    public string RuntimeExecutable { get; set; } = string.Empty;

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;
}
