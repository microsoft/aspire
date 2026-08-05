// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

internal sealed class AzureConnectorGatewayConnectionAccessPolicyResource : Resource, IResourceWithParent<AzureConnectorGatewayConnectionResource>
{
    public AzureConnectorGatewayConnectionAccessPolicyResource(
        string name,
        string policyName,
        AzureConnectorGatewayConnectionResource parent,
        string objectId,
        string tenantId)
        : this(name, policyName, parent, objectId, tenantId, usesGatewayManagedIdentity: false)
    {
    }

    private AzureConnectorGatewayConnectionAccessPolicyResource(
        string name,
        string policyName,
        AzureConnectorGatewayConnectionResource parent,
        string objectId,
        string tenantId,
        bool usesGatewayManagedIdentity,
        AzureUserAssignedIdentityResource? identityResource = null)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (!usesGatewayManagedIdentity && identityResource is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        }

        PolicyName = policyName;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        ObjectId = objectId;
        TenantId = tenantId;
        UsesGatewayManagedIdentity = usesGatewayManagedIdentity;
        IdentityResource = identityResource;
    }

    public string PolicyName { get; }

    public string ObjectId { get; }

    public string TenantId { get; }

    public AzureConnectorGatewayConnectionResource Parent { get; }

    public AzureUserAssignedIdentityResource? IdentityResource { get; }

    public static AzureConnectorGatewayConnectionAccessPolicyResource CreateGatewayManagedIdentityPolicy(
        string name,
        string policyName,
        AzureConnectorGatewayConnectionResource parent)
    {
        return new(name, policyName, parent, objectId: string.Empty, tenantId: string.Empty, usesGatewayManagedIdentity: true);
    }

    public static AzureConnectorGatewayConnectionAccessPolicyResource CreateUserAssignedIdentityPolicy(
        string name,
        string policyName,
        AzureConnectorGatewayConnectionResource parent,
        AzureUserAssignedIdentityResource identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            name,
            policyName,
            parent,
            objectId: string.Empty,
            tenantId: string.Empty,
            usesGatewayManagedIdentity: false,
            identity);
    }

    public bool UsesGatewayManagedIdentity { get; }
}
