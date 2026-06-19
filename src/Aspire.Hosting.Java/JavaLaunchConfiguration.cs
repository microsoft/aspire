// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.Java;

internal sealed class JavaLaunchConfiguration() : ExecutableLaunchConfiguration("java")
{
    /// <summary>
    /// The debug request type
    /// </summary>
    [JsonPropertyName("request")]
    public string Request { get; set; } = "launch";

    /// <summary>
    /// The working directory of the Java project, used by the IDE debugger extension
    /// to scope main class resolution and set the debug session's working directory.
    /// </summary>
    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;
}
