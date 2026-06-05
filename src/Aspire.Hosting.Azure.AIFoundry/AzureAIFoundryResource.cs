// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure AI Foundry resource.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="configureInfrastructure">Configures the underlying Azure resource using Azure.Provisioning.</param>
public class AzureAIFoundryResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure) :
    AzureProvisioningResource(name, configureInfrastructure), IResourceWithEndpoints, IResourceWithConnectionString
{
    internal Uri? EmulatorServiceUri { get; set; }

    private readonly List<AzureAIFoundryDeploymentResource> _deployments = [];

    /// <summary>
    /// Gets the "aiFoundryApiEndpoint" output reference from the Azure AI Foundry resource.
    /// </summary>
    public BicepOutputReference AIFoundryApiEndpoint => new("aiFoundryApiEndpoint", this);

    /// <summary>
    /// Gets the "endpoint" output reference from the Azure AI Foundry resource.
    /// </summary>
    public BicepOutputReference Endpoint => new("endpoint", this);

    /// <summary>
    /// Gets the "name" output reference for the resource.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// Gets the connection URI expression for the AI Foundry endpoint.
    /// </summary>
    /// <remarks>
    /// In emulator mode, resolves to the emulator service URI.
    /// In Azure mode, resolves to the Azure AI Foundry endpoint.
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
    /// Gets the list of deployment resources associated with the Azure AI Foundry.
    /// </summary>
    public IReadOnlyList<AzureAIFoundryDeploymentResource> Deployments => _deployments;

    /// <summary>
    /// Gets whether the resource is running in the Foundry Local.
    /// </summary>
    public bool IsEmulator => this.IsEmulator();

    /// <summary>
    /// The API key to access Foundry Local
    /// </summary>
    public string? ApiKey { get; internal set; }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
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

    internal void AddDeployment(AzureAIFoundryDeploymentResource deployment)
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
}
