// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Overrides the run-mode host path for a named volume mount.
/// </summary>
internal sealed class VolumeMountPathResolverAnnotation(
    string volumeName,
    Func<EnvironmentCallbackContext, string> resolver) : IResourceAnnotation
{
    internal string VolumeName { get; } = volumeName;

    internal Func<EnvironmentCallbackContext, string> Resolver { get; } = resolver;
}
