// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Records registry association provenance independently of whether the association requests publication.
/// </summary>
internal abstract class ContainerImageRegistryAssociationAnnotation(
    IResource registry, IComputeEnvironmentResource? defaultEnvironment) : IResourceAnnotation
{
    internal IResource Registry { get; } = registry;

    internal IComputeEnvironmentResource? DefaultEnvironment { get; } = defaultEnvironment;
}
