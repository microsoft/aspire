// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureAppConfigurationResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureAppConfigurationEmulatorResource(AzureAppConfigurationResource innerResource)
    : ContainerResource(innerResource.Name), IResource, IContainerProjection<AzureAppConfigurationResource, AzureAppConfigurationEmulatorResource>
{
    private readonly AzureAppConfigurationResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc/>
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;

    /// <inheritdoc/>
    public static AzureAppConfigurationEmulatorResource CreateProjection(AzureAppConfigurationResource owner) => new(owner);
}
