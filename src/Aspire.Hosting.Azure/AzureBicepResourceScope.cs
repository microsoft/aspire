// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents the scope associated with the resource.
/// </summary>
public sealed class AzureBicepResourceScope
{
    private readonly object? _resourceGroup;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureBicepResourceScope"/> class with a resource group scope.
    /// </summary>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    public AzureBicepResourceScope(object resourceGroup)
    {
        ArgumentNullException.ThrowIfNull(resourceGroup);

        _resourceGroup = resourceGroup;
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

    private AzureBicepResourceScope(ScopeKind scopeKind)
    {
        if (scopeKind is not ScopeKind.Tenant)
        {
            throw new ArgumentOutOfRangeException(nameof(scopeKind));
        }

        IsTenantScope = true;
    }

    private AzureBicepResourceScope(ScopeKind scopeKind, object subscription)
    {
        if (scopeKind is not ScopeKind.Subscription)
        {
            throw new ArgumentOutOfRangeException(nameof(scopeKind));
        }

        ArgumentNullException.ThrowIfNull(subscription);

        Subscription = subscription;
    }

    /// <summary>
    /// Creates a scope for subscription-level resources.
    /// </summary>
    /// <param name="subscription">The subscription identifier for subscription-level resources.</param>
    /// <returns>A new <see cref="AzureBicepResourceScope"/> scoped to the subscription.</returns>
    public static AzureBicepResourceScope CreateForSubscription(object subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        return new AzureBicepResourceScope(ScopeKind.Subscription, subscription);
    }

    /// <summary>
    /// Creates a scope for tenant-level resources in the current tenant.
    /// </summary>
    /// <returns>A new <see cref="AzureBicepResourceScope"/> scoped to the current tenant.</returns>
    public static AzureBicepResourceScope CreateForTenant()
    {
        return new AzureBicepResourceScope(ScopeKind.Tenant);
    }

    /// <summary>
    /// Represents the resource group to encode in the scope.
    /// </summary>
    /// <exception cref="InvalidOperationException">The scope does not target a resource group.</exception>
    public object ResourceGroup => _resourceGroup ?? throw new InvalidOperationException("The Azure Bicep resource scope does not target a resource group.");

    /// <summary>
    /// Represents the subscription to encode in the scope.
    /// </summary>
    public object? Subscription { get; }

    /// <summary>
    /// Gets a value indicating whether the scope targets the current tenant.
    /// </summary>
    public bool IsTenantScope { get; }

    internal bool HasResourceGroup => _resourceGroup is not null;

    internal static AzureBicepResourceScope? FromExistingResourceAnnotation(ExistingAzureResourceAnnotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);

        if (annotation.IsTenantScope)
        {
            return CreateForTenant();
        }

        return (annotation.ResourceGroup, annotation.Subscription) switch
        {
            ({ } resourceGroup, { } subscription) => new AzureBicepResourceScope(resourceGroup, subscription),
            ({ } resourceGroup, null) => new AzureBicepResourceScope(resourceGroup),
            (null, { } subscription) => CreateForSubscription(subscription),
            _ => null
        };
    }

    private enum ScopeKind
    {
        Subscription,
        Tenant
    }
}
