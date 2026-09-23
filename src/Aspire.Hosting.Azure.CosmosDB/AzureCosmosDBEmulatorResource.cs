// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTIONS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.CosmosDB;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureCosmosDBResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureCosmosDBEmulatorResource(AzureCosmosDBResource innerResource)
    : ContainerResource(innerResource.Name), IResourceWithConnectionString, IContainerProjection<AzureCosmosDBResource, AzureCosmosDBEmulatorResource>
{
    internal AzureCosmosDBResource InnerResource { get; } = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource.Annotations;

    ReferenceExpression IResourceWithConnectionString.ConnectionStringExpression => CreateConnectionString(InnerResource);

    internal static ReferenceExpression CreateConnectionString(AzureCosmosDBResource owner) =>
        AzureCosmosDBEmulatorConnectionString.Create(owner.EmulatorEndpoint, owner.IsVNextEmulator);

    internal static IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties(AzureCosmosDBResource owner)
    {
        yield return new("Uri", ReferenceExpression.Create($"{owner.EmulatorEndpoint.Property(EndpointProperty.Url)}"));
        yield return new("AccountKey", ReferenceExpression.Create($"{CosmosConstants.EmulatorAccountKey}"));
        yield return new("ConnectionString", CreateConnectionString(owner));
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties() =>
        GetConnectionProperties(InnerResource);

    /// <inheritdoc />
    public static AzureCosmosDBEmulatorResource CreateProjection(AzureCosmosDBResource owner) => new(owner);
}
