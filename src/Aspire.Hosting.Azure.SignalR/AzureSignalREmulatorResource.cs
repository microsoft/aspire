// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTIONS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureSignalRResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureSignalREmulatorResource(AzureSignalRResource innerResource)
    : ContainerResource(innerResource.Name), IResource, IResourceWithConnectionString, IContainerProjection<AzureSignalRResource, AzureSignalREmulatorResource>
{
    private readonly AzureSignalRResource _innerResource = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc/>
    public override ResourceAnnotationCollection Annotations => _innerResource.Annotations;

    ReferenceExpression IResourceWithConnectionString.ConnectionStringExpression => CreateConnectionString(_innerResource);

    internal static ReferenceExpression CreateConnectionString(AzureSignalRResource owner) =>
        ReferenceExpression.Create($"Endpoint={owner.EmulatorEndpoint.Property(EndpointProperty.Url)};AccessKey=ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGH;Version=1.0;");

    internal static IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties(AzureSignalRResource owner)
    {
        yield return new("Uri", ReferenceExpression.Create($"{owner.EmulatorEndpoint.Property(EndpointProperty.Url)}"));
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        GetConnectionProperties(_innerResource);

    /// <inheritdoc/>
    public static AzureSignalREmulatorResource CreateProjection(AzureSignalRResource owner) => new(owner);
}
