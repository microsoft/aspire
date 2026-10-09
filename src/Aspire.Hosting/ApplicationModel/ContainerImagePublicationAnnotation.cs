// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

namespace Aspire.Hosting.ApplicationModel;

internal sealed class ContainerImagePublicationAnnotation(
    DestinationImageResource destination, IComputeEnvironmentResource? defaultEnvironment)
    : ContainerImageRegistryAssociationAnnotation(destination.Parent, defaultEnvironment)
{
    internal DestinationImageResource Destination { get; } = destination;
}
