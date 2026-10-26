// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureWebPubSubResource"/> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureWebPubSubEmulatorResource(AzureWebPubSubResource innerResource) : ContainerResource((innerResource ?? throw new ArgumentNullException(nameof(innerResource))).Name), IResource
{
    private readonly AzureWebPubSubResource _innerResource = innerResource;

    /// <inheritdoc/>
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;
}
