// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
namespace Aspire.Hosting.Azure;

internal sealed class AzureConnectorNamespaceMcpAccessPolicyResource : Resource, IResourceWithParent<AzureConnectorNamespaceMcpServerConfigResource>
{
    public AzureConnectorNamespaceMcpAccessPolicyResource(
        string name,
        AzureConnectorNamespaceMcpServerConfigResource parent,
        string objectId,
        string tenantId,
        AzureConnectorNamespaceMcpAccessPolicyPrincipalType principalType)
        : base(name)
    {
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        ObjectId = objectId;
        TenantId = tenantId;
        PrincipalType = principalType;
        BicepIdentifier = name;
    }

    public AzureConnectorNamespaceMcpServerConfigResource Parent { get; }

    public string ObjectId { get; }

    public string TenantId { get; }

    public AzureConnectorNamespaceMcpAccessPolicyPrincipalType PrincipalType { get; }

    public string BicepIdentifier { get; }
}
