// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning.ContainerRegistry;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure Container Registry resource.
/// </summary>
public class AzureContainerRegistryResource : AzureProvisioningResource, IAzureContainerRegistryResource, IAzurePrivateEndpointTarget
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AzureContainerRegistryResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configureInfrastructure">The callback to configure the Azure infrastructure for this resource.</param>
    public AzureContainerRegistryResource(string name, Action<AzureResourceInfrastructure> configureInfrastructure)
        : base(name, configureInfrastructure)
    {
        Annotations.Add(new PipelineStepAnnotation((factoryContext) =>
        {
            var loginStep = new PipelineStep
            {
                Name = $"login-to-acr-{name}",
                Action = context => AzureContainerRegistryHelpers.LoginToRegistryAsync(this, context),
                Tags = ["acr-login"],
                RequiredBySteps = [WellKnownPipelineSteps.PushPrereq],
                Resource = this
            };
            return [loginStep];
        }));

        Annotations.Add(new PipelineConfigurationAnnotation((context) =>
        {
            var loginSteps = context.GetSteps(this, "acr-login");
            var provisionSteps = context.GetSteps(this, WellKnownPipelineTags.ProvisionInfrastructure);

            loginSteps.DependsOn(provisionSteps);
        }));
    }

    /// <summary>
    /// The name of the Azure Container Registry.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    /// <summary>
    /// The endpoint of the Azure Container Registry.
    /// </summary>
    public BicepOutputReference RegistryEndpoint => new("loginServer", this);

    /// <summary>
    /// Gets the "id" output reference for the Azure Container Registry resource.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <inheritdoc/>
    ReferenceExpression IContainerRegistry.Name => ReferenceExpression.Create($"{NameOutputReference}");

    /// <inheritdoc/>
    ReferenceExpression IContainerRegistry.Endpoint => ReferenceExpression.Create($"{RegistryEndpoint}");

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();

        // Check if a ContainerRegistryService with the same identifier already exists
        var existingStore = resources.OfType<ContainerRegistryService>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);

        if (existingStore is not null)
        {
            return existingStore;
        }

        // Create and add new resource if it doesn't exist
        var store = ContainerRegistryService.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            store))
        {
            store.Name = NameOutputReference.AsProvisioningParameter(infra);
        }

        infra.Add(store);
        return store;
    }

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["registry"];

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateDnsZoneNames() => ["privatelink.azurecr.io"];
}
