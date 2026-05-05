// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Represents the annotation for the JavaScript package manager's build script.
/// </summary>
/// <param name="scriptName">The name of the JavaScript package manager's build script.</param>
/// <param name="args">The command line arguments for the JavaScript package manager's build script.</param>
public sealed class JavaScriptBuildScriptAnnotation(string scriptName, string[]? args) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the script used to build.
    /// </summary>
    public string ScriptName { get; } = scriptName;

    /// <summary>
    /// Gets the command-line arguments supplied to the build script.
    /// </summary>
    public string[] Args { get; } = args ?? [];

    /// <summary>
    /// Gets a value indicating whether the build script should also be executed
    /// for the target package's workspace dependencies (in topological order).
    /// </summary>
    /// <remarks>
    /// When the resource is configured with <see cref="JavaScriptWorkspaceExtensions.WithWorkspaceRoot{T}"/>
    /// and the workspace's package manager supports topological filtering (for example pnpm), enabling this
    /// causes the generated Dockerfile to invoke the build script across the target package and every
    /// workspace package it depends on. This is required when consumed workspace packages produce build
    /// outputs (for example a TypeScript library compiled to <c>dist/</c>) that the target needs at runtime.
    /// </remarks>
    public bool IncludeWorkspaceDependencies { get; init; }
}
