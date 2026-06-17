// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a resource that is not managed by Aspire's provisioning or
/// container management layer.
/// </summary>
public sealed class ExistingAzureResourceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ExistingAzureResourceAnnotation"/> class.
    /// </summary>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    public ExistingAzureResourceAnnotation(object name, object? resourceGroup = null)
    {
        Name = name;
        ResourceGroup = resourceGroup;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ExistingAzureResourceAnnotation"/> class.
    /// </summary>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <param name="subscription">The subscription identifier associated with the resource group.</param>
    public ExistingAzureResourceAnnotation(object name, object? resourceGroup, object subscription) : this(name, resourceGroup)
    {
        Subscription = subscription;
    }

    internal ExistingAzureResourceAnnotation(object name, bool isTenantScope) : this(name, resourceGroup: null)
    {
        IsTenantScope = isTenantScope;
    }

    /// <summary>
    /// Gets the name of the existing resource.
    /// </summary>
    /// <remarks>
    /// Supports a <see cref="string"/>, <see cref="ParameterResource"/>, or a <see cref="BicepOutputReference"/> via runtime validation.
    /// </remarks>
    public object Name { get; }

    /// <summary>
    /// Gets the name of the existing resource group. If <see langword="null"/>, use the current resource group.
    /// </summary>
    /// <remarks>
    /// Supports a <see cref="string"/> or a <see cref="ParameterResource"/> via runtime validation.
    /// </remarks>
    public object? ResourceGroup { get; }

    /// <summary>
    /// Gets the subscription identifier associated with the resource group or subscription-scoped resource.
    /// </summary>
    /// <remarks>
    /// Supports a <see cref="string"/> or a <see cref="ParameterResource"/> via runtime validation.
    /// </remarks>
    public object? Subscription { get; }

    /// <summary>
    /// Gets a value indicating whether the resource is scoped to the current tenant.
    /// </summary>
    public bool IsTenantScope { get; }
}
