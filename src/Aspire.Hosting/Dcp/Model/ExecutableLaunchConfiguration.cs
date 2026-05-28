// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model;

internal static class ExecutableLaunchMode
{
    public const string Debug = "Debug";
    public const string NoDebug = "NoDebug";
}

/// <summary>
/// Base properties for all executable launch configurations.
/// </summary>
/// <param name="type">Launch configuration type indicator.</param>
internal class ExecutableLaunchConfiguration(string type)
{
    /// <summary>
    /// The launch configuration type indicator.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = type;

    /// <summary>
    /// Specifies the launch mode. Currently supported modes are Debug (run the project under the debugger) and NoDebug (run the project without debugging).
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = System.Diagnostics.Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug;
}

internal sealed class ProjectLaunchConfiguration() : ExecutableLaunchConfiguration("project")
{
    [JsonPropertyName("launch_profile")]
    public string LaunchProfile { get; set; } = string.Empty;

    [JsonPropertyName("disable_launch_profile")]
    public bool DisableLaunchProfile { get; set; } = false;

    [JsonPropertyName("project_path")]
    public string ProjectPath { get; set; } = string.Empty;
}

/// <summary>
/// Launch configuration for browser-based debugging.
/// The IDE receives this via PUT /run_session, launches a browser navigated to the URL,
/// and attaches a debug adapter (determined by the <see cref="Browser"/> field).
/// </summary>
internal sealed class BrowserLaunchConfiguration() : ExecutableLaunchConfiguration("browser")
{
    /// <summary>
    /// URL where the application is served. The IDE navigates the debug browser here.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Root path for source resolution.
    /// For JS apps this is the web root directory; for Blazor WASM it is the .csproj path.
    /// </summary>
    [JsonPropertyName("web_root")]
    public string WebRoot { get; set; } = string.Empty;

    /// <summary>
    /// Browser/debug adapter type. The IDE extension maps this to a VS Code debug adapter.
    /// Standard values: "msedge", "chrome". For Blazor WASM debugging use "blazor-webassembly"
    /// (requires extension support). Defaults to "msedge".
    /// </summary>
    [JsonPropertyName("browser")]
    public string Browser { get; set; } = "msedge";
}
