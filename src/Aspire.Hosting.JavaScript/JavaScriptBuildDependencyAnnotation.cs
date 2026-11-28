// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Declares a workspace member that must be built before the annotated workspace application's own build
/// step in publish mode.
/// </summary>
/// <remarks>
/// A resource can carry several of these annotations; they are built in the order they were added. When at
/// least one is present, the declared list replaces the package manager's automatic dependency build
/// (see <see cref="JavaScriptWorkspaceResource.GetBuildDependenciesCommand"/>).
/// </remarks>
/// <param name="workspaceProjectName">The name (package.json <c>name</c>) of the workspace member to build.</param>
/// <param name="scriptName">
/// The package.json script to run in the dependency, or <see langword="null"/> to use the application's own
/// build script name (or <c>build</c> when the application has no build script).
/// </param>
public sealed class JavaScriptBuildDependencyAnnotation(string workspaceProjectName, string? scriptName) : IResourceAnnotation
{
    /// <summary>
    /// The script used when neither the dependency nor the application specifies one.
    /// </summary>
    internal const string DefaultScriptName = "build";

    /// <summary>
    /// Gets the name (package.json <c>name</c>) of the workspace member to build.
    /// </summary>
    public string WorkspaceProjectName { get; } = workspaceProjectName;

    /// <summary>
    /// Gets the script to run in the dependency, or <see langword="null"/> to inherit the application's build
    /// script name.
    /// </summary>
    public string? ScriptName { get; } = scriptName;

    /// <summary>
    /// Resolves the script to run for this dependency of <paramref name="app"/>. The dependency inherits the
    /// application's build script name by default because workspaces conventionally use the same script name
    /// (typically <c>build</c>) across members.
    /// </summary>
    internal string ResolveScriptName(IResource app)
        => ScriptName
            ?? (app.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildScript) ? buildScript.ScriptName : null)
            ?? DefaultScriptName;
}
