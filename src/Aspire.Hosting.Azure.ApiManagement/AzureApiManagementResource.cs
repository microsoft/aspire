// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.ApiManagement.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure API Management service.
/// </summary>
/// <remarks>
/// API Management is provisioned in Azure and acts as a gateway for APIs added with
/// <see cref="AzureApiManagementExtensions.AddApi{T}"/>.
/// </remarks>
/// <param name="name">The name of the Aspire resource.</param>
/// <param name="options">The API Management service options.</param>
/// <param name="configureInfrastructure">The callback that configures Azure infrastructure.</param>
[AspireExport]
[Experimental("ASPIREAPIM001", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public class AzureApiManagementResource(
    string name,
    AzureApiManagementOptions options,
    Action<AzureResourceInfrastructure> configureInfrastructure)
    : AzureProvisioningResource(name, configureInfrastructure), IAzurePrivateEndpointTargetNotification
{
    private readonly List<string> _inboundPolicyStatements = [];

    /// <summary>
    /// Gets the configured service options.
    /// </summary>
    public AzureApiManagementOptions Options { get; } = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Gets the APIs provisioned in this API Management service.
    /// </summary>
    internal List<AzureApiManagementApiResource> Apis { get; } = [];

    /// <summary>
    /// Gets the backends provisioned in this API Management service.
    /// </summary>
    internal List<AzureApiManagementBackendResource> Backends { get; } = [];

    /// <summary>
    /// Gets the backend pools provisioned in this API Management service.
    /// </summary>
    internal List<AzureApiManagementBackendPoolResource> BackendPools { get; } = [];

    /// <summary>
    /// Gets the products provisioned in this API Management service.
    /// </summary>
    internal List<AzureApiManagementProductResource> Products { get; } = [];

    /// <summary>
    /// Gets the named values provisioned in this API Management service.
    /// </summary>
    internal List<AzureApiManagementNamedValueResource> NamedValues { get; } = [];

    /// <summary>
    /// Gets the policy fragments provisioned in this API Management service.
    /// </summary>
    internal List<AzureApiManagementPolicyFragmentResource> PolicyFragments { get; } = [];

    /// <summary>
    /// Gets the custom domains configured on this API Management service.
    /// </summary>
    internal List<AzureApiManagementCustomDomain> CustomDomains { get; } = [];

    /// <summary>
    /// Gets or sets the service-level Application Insights diagnostic.
    /// </summary>
    internal AzureApiManagementDiagnostic? Diagnostic { get; set; }

    /// <summary>
    /// Gets the physical service name output reference.
    /// </summary>
    [AspireExportIgnore(Reason = "Bicep output references are not projected to polyglot AppHosts.")]
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the resource ID output reference.
    /// </summary>
    [AspireExportIgnore(Reason = "Bicep output references are not projected to polyglot AppHosts.")]
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the default gateway URL output reference.
    /// </summary>
    [AspireExportIgnore(Reason = "Bicep output references are not projected to polyglot AppHosts.")]
    public BicepOutputReference GatewayUrl => new("gatewayUrl", this);

    /// <summary>
    /// Gets the managed identity principal ID output reference.
    /// </summary>
    [AspireExportIgnore(Reason = "Bicep output references are not projected to polyglot AppHosts.")]
    public BicepOutputReference PrincipalId
    {
        get
        {
            RequiresSystemAssignedIdentity = true;
            return new("principalId", this);
        }
    }

    /// <summary>
    /// Gets the complete service-level policy document, when one has been configured.
    /// </summary>
    internal string? PolicyXml { get; set; }

    /// <summary>
    /// Gets the ordered service-level inbound policy statements.
    /// </summary>
    internal IReadOnlyList<string> InboundPolicyStatements => _inboundPolicyStatements;

    /// <summary>
    /// Gets the classic virtual network configuration.
    /// </summary>
    internal AzureApiManagementVirtualNetworkConfiguration? VirtualNetworkConfiguration { get; set; }

    internal AzureApiManagementPublicNetworkAccessUpdateResource? PublicNetworkAccessUpdate { get; set; }

    internal AzureUserAssignedIdentityResource? KeyVaultIdentity { get; set; }

    internal List<BicepOutputReference> KeyVaultRoleAssignmentDependencies { get; } = [];

    internal bool ExistingSystemAssignedIdentityConfirmed { get; set; }

    internal bool RequiresSystemAssignedIdentity { get; set; }

    /// <summary>
    /// Adds an inbound policy statement.
    /// </summary>
    internal void AddInboundPolicyStatement(string policyXml) => _inboundPolicyStatements.Add(policyXml);

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infrastructure)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var existingService = infrastructure.GetProvisionableResources()
            .OfType<ApiManagementServiceProvisioningResource>()
            .SingleOrDefault(resource => resource.BicepIdentifier == bicepIdentifier);

        if (existingService is not null)
        {
            return existingService;
        }

        ApiManagementServiceProvisioningResource service;
        if (this.IsExisting())
        {
            service = CreateExistingOrNewProvisionableResource(
                infrastructure,
                static (identifier, name) =>
                {
                    var existing = ApiManagementServiceProvisioningResource.FromExisting(identifier);
                    existing.Name = name;
                    return existing;
                },
                static _ => throw new UnreachableException());
        }
        else
        {
            service = ApiManagementServiceProvisioningResource.FromExisting(bicepIdentifier);
            service.Name = NameOutputReference.AsProvisioningParameter(infrastructure);
            infrastructure.Add(service);
        }

        return service;
    }

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["Gateway"];

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateDnsZoneNames() => ["privatelink.azure-api.net"];

    void IAzurePrivateEndpointTargetNotification.OnPrivateEndpointCreated(
        IResourceBuilder<AzurePrivateEndpointResource> privateEndpoint)
    {
        if (PublicNetworkAccessUpdate is not null)
        {
            PublicNetworkAccessUpdate.AddPrivateEndpoint(privateEndpoint.Resource);
            privateEndpoint.ApplicationBuilder.CreateResourceBuilder(PublicNetworkAccessUpdate)
                .WithRelationship(privateEndpoint.Resource, "Private endpoint");
            return;
        }

        PublicNetworkAccessUpdate = new AzureApiManagementPublicNetworkAccessUpdateResource(
            AzureApiManagementExtensions.CreateBoundedIdentifier($"{Name}-disable-public-access", 64),
            this,
            privateEndpoint.Resource);
        privateEndpoint.ApplicationBuilder.AddResource(PublicNetworkAccessUpdate)
            .WithRelationship(this, "Disables public access")
            .WithRelationship(privateEndpoint.Resource, "Private endpoint");
    }
}

internal sealed record AzureApiManagementVirtualNetworkConfiguration(
    AzureSubnetResource Subnet,
    AzureApiManagementVirtualNetworkMode Mode,
    AzureApiManagementVirtualNetworkKind Kind);

internal enum AzureApiManagementVirtualNetworkKind
{
    Classic,
    V2Integration,
    PremiumV2Injection,
}

internal sealed record AzureApiManagementSubnetUsageAnnotation(
    AzureApiManagementResource ApiManagement) : IResourceAnnotation;
