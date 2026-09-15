// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTIONS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Wraps an <see cref="AzureCosmosDBResource" /> in a type that exposes container extension methods.
/// </summary>
/// <param name="innerResource">The inner resource used to store annotations.</param>
public class AzureCosmosDBEmulatorResource(AzureCosmosDBResource innerResource)
    : ContainerResource(innerResource.Name), IContainerProjection<AzureCosmosDBResource, AzureCosmosDBEmulatorResource>
{
    internal AzureCosmosDBResource InnerResource { get; } = innerResource ?? throw new ArgumentNullException(nameof(innerResource));

    /// <inheritdoc />
    public override ResourceAnnotationCollection Annotations => InnerResource.Annotations;

    /// <inheritdoc />
    public static AzureCosmosDBEmulatorResource CreateProjection(AzureCosmosDBResource owner) => new(owner);
}
