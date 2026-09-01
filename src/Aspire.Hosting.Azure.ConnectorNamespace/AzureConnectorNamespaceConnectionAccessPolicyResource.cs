// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

internal sealed class AzureConnectorNamespaceConnectionAccessPolicyResource : Resource, IResourceWithParent<AzureConnectorNamespaceConnectionResource>
{
    public AzureConnectorNamespaceConnectionAccessPolicyResource(
        string name,
        string policyName,
        AzureConnectorNamespaceConnectionResource parent,
        string objectId,
        string tenantId)
        : this(name, policyName, parent, objectId, tenantId, identityResource: null)
    {
    }

    private AzureConnectorNamespaceConnectionAccessPolicyResource(
        string name,
        string policyName,
        AzureConnectorNamespaceConnectionResource parent,
        string objectId,
        string tenantId,
        AzureUserAssignedIdentityResource? identityResource)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);
        if (identityResource is null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
            ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        }

        PolicyName = policyName;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        ObjectId = objectId;
        TenantId = tenantId;
        IdentityResource = identityResource;
        BicepIdentifier = name;
    }

    public string PolicyName { get; }

    public string ObjectId { get; }

    public string TenantId { get; }

    public AzureConnectorNamespaceConnectionResource Parent { get; }

    public AzureUserAssignedIdentityResource? IdentityResource { get; }

    public string BicepIdentifier { get; }

    public static AzureConnectorNamespaceConnectionAccessPolicyResource CreateUserAssignedIdentityPolicy(
        string name,
        string policyName,
        AzureConnectorNamespaceConnectionResource parent,
        AzureUserAssignedIdentityResource identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            name,
            policyName,
            parent,
            objectId: string.Empty,
            tenantId: string.Empty,
            identity);
    }
}
