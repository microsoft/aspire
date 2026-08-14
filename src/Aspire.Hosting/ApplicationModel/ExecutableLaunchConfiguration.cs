// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Well-known launch modes that can be requested when an IDE or extension host launches a resource.
/// </summary>
/// <remarks>
/// The value is serialized as the <c>mode</c> field of a launch configuration and is interpreted by
/// the IDE (Visual Studio, VS Code) that owns the debug session.
/// </remarks>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class ExecutableLaunchMode
{
    /// <summary>
    /// Launch the resource under the debugger.
    /// </summary>
    public const string Debug = "Debug";

    /// <summary>
    /// Launch the resource without debugging.
    /// </summary>
    public const string NoDebug = "NoDebug";
}

/// <summary>
/// Well-known launch configuration type identifiers.
/// </summary>
/// <remarks>
/// The launch configuration type tells the IDE which launcher to use for a resource. Integrations
/// are free to define their own identifiers (for example <c>"go"</c> or <c>"python"</c>); only the
/// identifiers that Aspire itself gives special meaning to are listed here.
/// </remarks>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class KnownLaunchConfigurationTypes
{
    /// <summary>
    /// The .NET project launch configuration type.
    /// </summary>
    /// <remarks>
    /// This type is reserved for resources that carry <see cref="IProjectMetadata"/>. Aspire hands the
    /// project path (and launch profile) to the IDE, which owns building and launching the project, so
    /// no process fallback is offered for resources using this type.
    /// </remarks>
    public const string Project = "project";
}

/// <summary>
/// Base properties shared by all launch configurations handed to an IDE or extension host.
/// </summary>
/// <remarks>
/// <para>
/// A launch configuration describes how a resource should be started when Aspire is running inside an
/// IDE debug session. It is serialized to JSON and attached to the underlying orchestrator object, so
/// the property names below (and any added by derived types) are part of the IDE contract.
/// </para>
/// <para>
/// Integrations create a derived type and supply it through
/// one of the <c>WithDebugSupport</c> overloads on <see cref="ResourceBuilderExtensions"/>.
/// </para>
/// </remarks>
/// <param name="type">The launch configuration type identifier, for example <see cref="KnownLaunchConfigurationTypes.Project"/>.</param>
/// <example>
/// A launch configuration for a hypothetical language integration:
/// <code lang="csharp">
/// internal sealed class ContosoLaunchConfiguration() : ExecutableLaunchConfiguration("contoso")
/// {
///     [JsonPropertyName("script_path")]
///     public string ScriptPath { get; set; } = string.Empty;
/// }
/// </code>
/// </example>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class ExecutableLaunchConfiguration(string type)
{
    /// <summary>
    /// Gets or sets the launch configuration type identifier.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = type;

    /// <summary>
    /// Gets or sets the launch mode, one of the values on <see cref="ExecutableLaunchMode"/>.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="ExecutableLaunchMode.Debug"/> when a debugger is attached to the app host
    /// and <see cref="ExecutableLaunchMode.NoDebug"/> otherwise. The mode requested by the IDE for the
    /// current debug session is passed directly to mode-based producers and is available to context-based
    /// producers through <see cref="LaunchConfigurationCallbackContext.Mode"/>.
    /// </remarks>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = System.Diagnostics.Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug;
}

/// <summary>
/// The launch configuration used for .NET projects and file-based C# apps.
/// </summary>
/// <remarks>
/// The IDE builds and launches the project itself, so resources using this launch configuration do not
/// get a process fallback. The resource must carry <see cref="IProjectMetadata"/>.
/// </remarks>
[Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ProjectLaunchConfiguration() : ExecutableLaunchConfiguration(KnownLaunchConfigurationTypes.Project)
{
    /// <summary>
    /// Gets or sets the name of the launch profile the IDE should apply. Empty means the IDE picks the
    /// effective profile itself.
    /// </summary>
    [JsonPropertyName("launch_profile")]
    public string LaunchProfile { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether launch profile handling should be suppressed entirely.
    /// </summary>
    [JsonPropertyName("disable_launch_profile")]
    public bool DisableLaunchProfile { get; set; }

    /// <summary>
    /// Gets or sets the fully-qualified path to the project file or file-based app to launch.
    /// </summary>
    [JsonPropertyName("project_path")]
    public required string ProjectPath { get; set; }
}
