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
    string? targetFramework,
    string? configuration = null,
    IReadOnlyList<string>? additionalBuildArguments = null,
    bool releaseBuildLockOnResourceRunning = true) : IResourceAnnotation
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

    /// <summary>
    /// Gets the AppHost build configuration used for the serialized MAUI build.
    /// </summary>
    public string? Configuration { get; } = configuration;

    /// <summary>
    /// Gets the additional MSBuild arguments required to produce the outputs used by the Run target.
    /// </summary>
    public IReadOnlyList<string> AdditionalBuildArguments { get; } = additionalBuildArguments?.ToArray() ?? [];

    /// <summary>
    /// Gets a value indicating whether the build lock can be released when DCP reports the resource as Running.
    /// </summary>
    public bool ReleaseBuildLockOnResourceRunning { get; } = releaseBuildLockOnResourceRunning;
}
