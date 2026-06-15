// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;
using Aspire.Hosting.Dcp.Model;

namespace Aspire.Hosting.JavaScript;

internal sealed class JavaScriptLaunchConfiguration(string type) : ExecutableLaunchConfiguration(type)
{
    /// <summary>
    /// The resource runs a script file directly (e.g., <c>bun index.ts</c> or <c>node app.js</c>).
    /// </summary>
    internal const string LaunchMethodDirect = "direct";

    /// <summary>
    /// The resource runs via a package-manager script (e.g., <c>npm run dev</c> or <c>bun run start</c>).
    /// </summary>
    internal const string LaunchMethodPackageManager = "package-manager";

    [JsonPropertyName("script_path")]
    public string ScriptPath { get; set; } = string.Empty;

    [JsonPropertyName("runtime_executable")]
    public string RuntimeExecutable { get; set; } = string.Empty;

    [JsonPropertyName("working_directory")]
    public string WorkingDirectory { get; set; } = string.Empty;

    [JsonPropertyName("launch_method")]
    public string LaunchMethod { get; set; } = string.Empty;
}
