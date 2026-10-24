// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureStorageResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureStorageEmulatorResource(AzureStorageResource innerResource) : ContainerResource(innerResource.Name), IResource
{
    /// <inheritdoc/>
    public override string ServiceDisplayName => "Azurite";

    private readonly AzureStorageResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc/>
    public override string Name => _innerResource.Name;

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;
}
