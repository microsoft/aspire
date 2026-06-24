// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.Azure.Sandboxes.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure connector namespace backed by a Microsoft.Web/connectorGateways resource.
/// </summary>
[AspireExport(ExposeProperties = true)]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayResource : AzureProvisioningResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureConnectorGatewayResource"/> class.
    /// </summary>
    /// <param name="name">The Aspire resource name.</param>
    /// <param name="configureInfrastructure">The callback that configures Azure provisioning infrastructure.</param>
    public AzureConnectorGatewayResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure)
    {
    }

    /// <summary>
    /// Gets the Azure resource name output reference.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the Azure resource ID output reference.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the connector namespace system-assigned managed identity principal ID output reference.
    /// </summary>
    public BicepOutputReference PrincipalId => new("principalId", this);

    /// <summary>
    /// Gets the connector namespace system-assigned managed identity tenant ID output reference.
    /// </summary>
    public BicepOutputReference TenantId => new("tenantId", this);

    internal List<AzureConnectorGatewayConnectionResource> Connections { get; } = [];

    internal List<AzureConnectorGatewayMcpServerConfigResource> McpServerConfigs { get; } = [];

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var existing = infra.GetProvisionableResources()
            .OfType<ConnectorGateway>()
            .SingleOrDefault(gateway => gateway.BicepIdentifier == bicepIdentifier);

        if (existing is not null)
        {
            return existing;
        }

        var gateway = ConnectorGateway.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(this, infra, gateway))
        {
            gateway.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(gateway);
        return gateway;
    }
}
