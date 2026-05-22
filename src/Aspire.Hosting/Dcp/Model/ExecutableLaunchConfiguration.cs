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
/// Launch configuration for browser-based debugging (e.g., Blazor WebAssembly).
/// The IDE receives this and launches a debug-enabled browser navigated to the app URL,
/// then connects a debug proxy (e.g., BrowserDebugProxy for .NET WASM) via CDP.
/// </summary>
internal sealed class BrowserDebugLaunchConfiguration() : ExecutableLaunchConfiguration("browser-debug")
{
    /// <summary>
    /// Absolute path to the WASM client .csproj file.
    /// The IDE uses this to locate assemblies, PDBs, and source files for symbol resolution.
    /// </summary>
    [JsonPropertyName("project_path")]
    public string ProjectPath { get; set; } = string.Empty;

    /// <summary>
    /// URL where the WASM application is served. The IDE navigates the debug browser here.
    /// </summary>
    [JsonPropertyName("app_url")]
    public string AppUrl { get; set; } = string.Empty;
}
