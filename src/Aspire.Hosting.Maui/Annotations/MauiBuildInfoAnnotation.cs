// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Maui.Annotations;

/// <summary>
/// Annotation carrying the build parameters for a MAUI platform resource, used by
/// <see cref="Aspire.Hosting.Maui.Lifecycle.MauiBuildQueueEventSubscriber"/> to run the Build target
/// before DCP launches the Run target.
/// </summary>
internal sealed class MauiBuildInfoAnnotation(
    string projectPath,
    string workingDirectory,
    string? targetFramework) : IResourceAnnotation
{
    /// <summary>
    /// Gets the absolute path to the project file.
    /// </summary>
    public string ProjectPath { get; } = projectPath;

    /// <summary>
    /// Gets the working directory for the build process.
    /// </summary>
    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>
    /// Gets the target framework moniker (e.g., net10.0-android).
    /// </summary>
    public string? TargetFramework { get; } = targetFramework;
}
