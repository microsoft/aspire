// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.CognitiveServices;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Represents a Microsoft Foundry resource. This corresponds to the Azure Cognitive Services account
/// with Microsoft Foundry capabilities enabled.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Configures the underlying Azure resource using Azure.Provisioning.</param>
[AspireExport]
public class FoundryResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure) :
    AzureProvisioningResource(name, configureInfrastructure), IResourceWithEndpoints, IResourceWithConnectionString, IAzurePrivateEndpointTarget, IAzureNspAssociationTarget
{
    internal Uri? EmulatorServiceUri { get; set; }

    private readonly List<FoundryDeploymentResource> _deployments = [];

    /// <summary>
    /// Gets the "aiFoundryApiEndpoint" output reference from the Microsoft Foundry resource.
    /// </summary>
    public BicepOutputReference AIFoundryApiEndpoint => new("aiFoundryApiEndpoint", this);

    /// <summary>
    /// Gets the "endpoint" output reference from the Microsoft Foundry resource.
    /// </summary>
    public BicepOutputReference Endpoint => new("endpoint", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the "id" output reference for the resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// Gets the connection URI expression for the Microsoft Foundry endpoint.
    /// </summary>
    /// <remarks>
    /// In emulator mode, resolves to the emulator service URI.
    /// In Azure mode, resolves to the Microsoft Foundry endpoint.
    /// </remarks>
    public ReferenceExpression UriExpression =>
        IsEmulator ?
            EmulatorServiceUri is not null ?
                ReferenceExpression.Create($"{EmulatorServiceUri.ToString()}") :
                ReferenceExpression.Empty :
            ReferenceExpression.Create($"{AIFoundryApiEndpoint}");

    /// <summary>
    /// Gets the connection string template for the manifest for the resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        IsEmulator
        ? ReferenceExpression.Create($"Endpoint={EmulatorServiceUri?.ToString()};Key={ApiKey}")
        : ReferenceExpression.Create($"Endpoint={Endpoint};EndpointAIInference={AIFoundryApiEndpoint}models");

    /// <summary>
    /// Gets the list of deployment resources associated with the Microsoft Foundry.
    /// </summary>
    public IReadOnlyList<FoundryDeploymentResource> Deployments => _deployments;

    /// <summary>
    /// The capability host associated with this project, if any.
    ///
    /// If none is set, we provision a default capability host for hosted agents.
    /// </summary>
    public CognitiveServicesCapabilityHost? CapabilityHost { get; set; }

    /// <summary>
    /// Gets whether the resource is running in Foundry Local.
    /// </summary>
    public bool IsEmulator => this.IsEmulator();

    /// <summary>
    /// The API key to access Foundry Local.
    /// </summary>
    public string? ApiKey { get; internal set; }

    /// <inheritdoc/>
    public override CognitiveServicesAccount AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a CognitiveServicesAccount with the same identifier already exists
        var existingCsa = resources.OfType<CognitiveServicesAccount>().SingleOrDefault(csa => csa.BicepIdentifier == bicepIdentifier);

        if (existingCsa is not null)
        {
            return existingCsa;
        }

        // Create and add new resource if it doesn't exist
        var csa = CognitiveServicesAccount.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            csa))
        {
            csa.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(csa);
        return csa;
    }

    internal void AddDeployment(FoundryDeploymentResource deployment)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        _deployments.Add(deployment);
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", UriExpression);

        if (IsEmulator)
        {
            yield return new("Key", ReferenceExpression.Create($"{ApiKey}"));
        }
    }

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["account"];

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateDnsZoneNames() =>
    [
        "privatelink.services.ai.azure.com",
        "privatelink.openai.azure.com",
        "privatelink.cognitiveservices.azure.com"
    ];
}

/// <summary>
/// The properties for a public hosting environment for Cognitive Services capabilities.
/// </summary>
public class PublicHostingCognitiveServicesCapabilityHostProperties : CognitiveServicesCapabilityHostProperties
{
    /// <inheritdoc/>
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        DefineProperty<bool>("enablePublicHostingEnvironment", ["enablePublicHostingEnvironment"], defaultValue: true);
    }
}
