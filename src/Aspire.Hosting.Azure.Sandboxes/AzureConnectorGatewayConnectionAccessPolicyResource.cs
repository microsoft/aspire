// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

internal sealed class AzureConnectorGatewayConnectionAccessPolicyResource : Resource, IResourceWithParent<AzureConnectorGatewayConnectionResource>
{
    private AzureConnectorGatewayConnectionAccessPolicyResource(
        string name,
        string policyName,
        AzureConnectorGatewayConnectionResource parent,
        AzureConnectorGatewayConnectionAccessPolicyPrincipal principal)
        : base(name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyName);

        PolicyName = policyName;
        Parent = parent ?? throw new ArgumentNullException(nameof(parent));
        Principal = principal;
    }

    public string PolicyName { get; }

    public AzureConnectorGatewayConnectionAccessPolicyPrincipal Principal { get; }

    public AzureConnectorGatewayConnectionResource Parent { get; }

    public static AzureConnectorGatewayConnectionAccessPolicyResource CreateGatewayManagedIdentityPolicy(
        string name,
        string policyName,
        AzureConnectorGatewayConnectionResource parent)
    {
        return new(name, policyName, parent, AzureConnectorGatewayConnectionAccessPolicyPrincipal.GatewayManagedIdentity);
    }
}

internal enum AzureConnectorGatewayConnectionAccessPolicyPrincipal
{
    GatewayManagedIdentity
}
