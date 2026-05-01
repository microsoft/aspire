// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Python;

internal sealed class PythonLaunchConfiguration() : ExecutableLaunchConfiguration("python")
{
    [JsonPropertyName("program_path")]
    public string ProgramPath { get; set; } = string.Empty;

    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyName("interpreter_path")]
    public string InterpreterPath { get; set; } = string.Empty;

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;
}
