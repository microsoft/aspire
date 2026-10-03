// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

internal sealed class ContainerImagePublicationAnnotation(
    IResource registry, string? repository, string tag, IComputeEnvironmentResource? defaultEnvironment)
    : ContainerImageRegistryAssociationAnnotation(registry, defaultEnvironment)
{
    internal string? Repository { get; } = repository;

    internal string Tag { get; } = tag;
}
