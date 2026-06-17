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
    public const string OperationName = "azure.operation.name";
    public const string OperationPhase = "azure.operation.phase";
    public const string OperationStatus = "azure.operation.status";
    public const string OperationTargetLocation = "azure.operation.target.location";
    public const string OperationStartedAt = "azure.operation.started_at";

    private static readonly string[] s_activeOperationProperties =
    [
        OperationName,
        OperationPhase,
        OperationStatus,
        OperationTargetLocation,
        OperationStartedAt
    ];

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

    public static ImmutableArray<ResourcePropertySnapshot> CreateActiveOperationProperties(
        string operationName,
        string phase,
        string status,
        string? targetLocation,
        DateTimeOffset startedAt)
    {
        var properties = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();
        properties.Add(CreateHighlighted(OperationName, operationName, AzureProvisioningStrings.OperationPropertyNameDisplayName));
        properties.Add(CreateHighlighted(OperationPhase, phase, AzureProvisioningStrings.OperationPropertyPhaseDisplayName));
        properties.Add(CreateHighlighted(OperationStatus, status, AzureProvisioningStrings.OperationPropertyStatusDisplayName));
        Add(properties, value => CreateHighlighted(OperationTargetLocation, value, AzureProvisioningStrings.OperationPropertyTargetLocationDisplayName), targetLocation, includeEmptyProperties: false);
        properties.Add(CreateHighlighted(OperationStartedAt, startedAt.ToString("O"), AzureProvisioningStrings.OperationPropertyStartedAtDisplayName));

        return properties.ToImmutable();
    }

    public static ImmutableArray<ResourcePropertySnapshot> WithoutActiveOperationProperties(this ImmutableArray<ResourcePropertySnapshot> properties)
    {
        if (properties.IsDefaultOrEmpty ||
            !properties.Any(static property => s_activeOperationProperties.Contains(property.Name, StringComparer.Ordinal)))
        {
            return properties;
        }

        return [.. properties.Where(static property => !s_activeOperationProperties.Contains(property.Name, StringComparer.Ordinal))];
    }

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
