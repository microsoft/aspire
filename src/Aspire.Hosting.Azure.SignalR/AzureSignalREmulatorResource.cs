// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureSignalRResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureSignalREmulatorResource(AzureSignalRResource innerResource)
    : ContainerResource(innerResource.Name), IResource, IContainerProjection<AzureSignalRResource, AzureSignalREmulatorResource>
{
    private readonly AzureSignalRResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc/>
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;

    /// <inheritdoc/>
    public static AzureSignalREmulatorResource CreateProjection(AzureSignalRResource owner) => new(owner);
}
