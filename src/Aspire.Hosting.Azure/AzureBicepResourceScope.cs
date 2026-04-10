// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents the scope associated with the resource.
/// </summary>
public sealed class AzureBicepResourceScope
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBicepResourceScope"/> class with a resource group.
    /// </summary>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    public AzureBicepResourceScope(object resourceGroup)
    {
        ArgumentNullException.ThrowIfNull(resourceGroup);
        ResourceGroup = resourceGroup;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBicepResourceScope"/> class with both resource group and subscription.
    /// </summary>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier associated with the resource group.</param>
    public AzureBicepResourceScope(object resourceGroup, object subscription) : this(resourceGroup)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        Subscription = subscription;
    }

    // Private constructor for factory methods
    private AzureBicepResourceScope()
    {
    }

    /// <summary>
    /// Creates a scope for subscription-level resources.
    /// </summary>
    /// <param name="subscription">The subscription identifier for subscription-level resources.</param>
    /// <returns>A new <see cref="AzureBicepResourceScope"/> scoped to the subscription.</returns>
    public static AzureBicepResourceScope ForSubscription(object subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new AzureBicepResourceScope { Subscription = subscription };
    }

    /// <summary>
    /// Creates a scope for tenant-level resources.
    /// </summary>
    /// <param name="tenant">The tenant identifier for tenant-level resources.</param>
    /// <returns>A new <see cref="AzureBicepResourceScope"/> scoped to the tenant.</returns>
    public static AzureBicepResourceScope ForTenant(object tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        return new AzureBicepResourceScope { Tenant = tenant };
    }

    /// <summary>
    /// Represents the resource group to encode in the scope.
    /// </summary>
    public object? ResourceGroup { get; private init; }

    /// <summary>
    /// Represents the subscription to encode in the scope.
    /// </summary>
    public object? Subscription { get; private init; }

    /// <summary>
    /// Represents the tenant to encode in the scope.
    /// </summary>
    public object? Tenant { get; private init; }
}
