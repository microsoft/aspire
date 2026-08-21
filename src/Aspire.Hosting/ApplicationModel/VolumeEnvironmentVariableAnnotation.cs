// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Records that a named volume mount publishes its run-mode path through an environment variable.
/// </summary>
/// <remarks>
/// The environment variable itself is written by an <see cref="EnvironmentCallbackAnnotation"/> whose
/// closure captures the name, which makes the intent invisible to anything inspecting the model. This
/// annotation restates it declaratively so compute environments can tell whether a host process will
/// materialize a local backing store for the volume, without having to observe the callback running.
/// </remarks>
internal sealed class VolumeEnvironmentVariableAnnotation(
    string volumeName,
    string environmentVariableName) : IResourceAnnotation
{
    internal string VolumeName { get; } = volumeName;

    internal string EnvironmentVariableName { get; } = environmentVariableName;
}
