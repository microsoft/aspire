// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents the scope associated with the resource.
/// </summary>
public sealed class AzureBicepResourceScope
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBicepResourceScope"/> class with a resource group scope.
    /// </summary>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    public AzureBicepResourceScope(object resourceGroup)
    {
        ArgumentNullException.ThrowIfNull(resourceGroup);

        ResourceGroup = resourceGroup;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBicepResourceScope"/> class with a resource group scope in a specific subscription.
    /// </summary>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier associated with the resource group.</param>
    public AzureBicepResourceScope(object resourceGroup, object subscription) : this(resourceGroup)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        Subscription = subscription;
    }

    private AzureBicepResourceScope(object? resourceGroup, object? subscription, bool isTenantScope)
    {
        ResourceGroup = resourceGroup;
        Subscription = subscription;
        IsTenantScope = isTenantScope;
    }

    /// <summary>
    /// Creates a scope for subscription-level resources.
    /// </summary>
    /// <param name="subscription">The subscription identifier for subscription-level resources.</param>
    /// <returns>A new <see cref="AzureBicepResourceScope"/> scoped to the subscription.</returns>
    public static AzureBicepResourceScope ForSubscription(object subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return new AzureBicepResourceScope(resourceGroup: null, subscription, isTenantScope: false);
    }

    /// <summary>
    /// Creates a scope for tenant-level resources in the current tenant.
    /// </summary>
    /// <returns>A new <see cref="AzureBicepResourceScope"/> scoped to the current tenant.</returns>
    public static AzureBicepResourceScope ForTenant()
    {
        return new AzureBicepResourceScope(resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Represents the resource group to encode in the scope.
    /// </summary>
    public object? ResourceGroup { get; }

    /// <summary>
    /// Represents the subscription to encode in the scope.
    /// </summary>
    public object? Subscription { get; }

    /// <summary>
    /// Gets a value indicating whether the scope targets the current tenant.
    /// </summary>
    public bool IsTenantScope { get; }

    internal static AzureBicepResourceScope? FromExistingResourceAnnotation(ExistingAzureResourceAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        if (annotation.IsTenantScope)
        {
            return ForTenant();
        }

        return (annotation.ResourceGroup, annotation.Subscription) switch
        {
            ({ } resourceGroup, { } subscription) => new AzureBicepResourceScope(resourceGroup, subscription),
            ({ } resourceGroup, null) => new AzureBicepResourceScope(resourceGroup),
            (null, { } subscription) => ForSubscription(subscription),
            _ => null
        };
    }
}
