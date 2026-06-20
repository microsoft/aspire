// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Resources;

namespace Aspire.Hosting.Azure;

internal static class AzureResourceProperties
{
    public const string SubscriptionId = "azure.subscription.id";
    public const string ResourceGroup = "azure.resource.group";
    public const string Location = "azure.location";
    public const string TenantId = "azure.tenant.id";
    public const string TenantDomain = "azure.tenant.domain";

    public static ImmutableArray<ResourcePropertySnapshot> CreateContextProperties(
        string? subscriptionId,
        string? resourceGroup,
        string? tenantId,
        string? tenantDomain,
        string? location,
        bool includeEmptyProperties = false)
    {
        var properties = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();

        Add(properties, CreateSubscriptionId, subscriptionId, includeEmptyProperties);
        Add(properties, CreateResourceGroup, resourceGroup, includeEmptyProperties);
        Add(properties, CreateTenantId, tenantId, includeEmptyProperties);
        Add(properties, CreateTenantDomain, tenantDomain, includeEmptyProperties);
        Add(properties, CreateLocation, location, includeEmptyProperties);

        return properties.ToImmutable();
    }

    public static ResourcePropertySnapshot CreateSubscriptionId(string? value) =>
        CreateHighlighted(SubscriptionId, value, AzureProvisioningStrings.ContextPropertySubscriptionIdDisplayName);

    public static ResourcePropertySnapshot CreateResourceGroup(string? value) =>
        CreateHighlighted(ResourceGroup, value, AzureProvisioningStrings.ContextPropertyResourceGroupDisplayName);

    public static ResourcePropertySnapshot CreateLocation(string? value) =>
        CreateHighlighted(Location, value, AzureProvisioningStrings.ContextPropertyLocationDisplayName);

    public static ResourcePropertySnapshot CreateTenantId(string? value) =>
        CreateHighlighted(TenantId, value, AzureProvisioningStrings.ContextPropertyTenantIdDisplayName);

    public static ResourcePropertySnapshot CreateTenantDomain(string? value) =>
        CreateHighlighted(TenantDomain, value, AzureProvisioningStrings.ContextPropertyTenantDomainDisplayName);

    private static ResourcePropertySnapshot CreateHighlighted(string name, string? value, string displayName) =>
        new(name, value)
        {
            DisplayName = displayName,
            IsHighlighted = !string.IsNullOrEmpty(value)
        };

    private static void Add(ImmutableArray<ResourcePropertySnapshot>.Builder properties, Func<string?, ResourcePropertySnapshot> createProperty, string? value, bool includeEmptyProperties)
    {
        if (includeEmptyProperties || !string.IsNullOrEmpty(value))
        {
            properties.Add(createProperty(value));
        }
    }
}
