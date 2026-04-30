// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning;
using Azure.Provisioning.ContainerRegistry;

namespace Aspire.Hosting;

/// <summary>
/// Shared infrastructure configuration for Azure Container Registry resources.
/// </summary>
internal static class ContainerRegistryInfrastructure
{
    internal static void ConfigureContainerRegistry(AzureResourceInfrastructure infrastructure)
        => ConfigureContainerRegistry(infrastructure, null);

    /// <summary>
    /// Configures a <see cref="ContainerRegistryService"/> in the given infrastructure,
    /// creating a new or referencing an existing resource as appropriate.
    /// Emits the standard "name", "loginServer", and "id" outputs.
    /// </summary>
    /// <param name="infrastructure">The Azure resource infrastructure to configure.</param>
    /// <param name="configureNewRegistry">Optional callback invoked only when a new registry is being created (not for existing resources).
    /// Use this to customize the newly created <see cref="ContainerRegistryService"/> before it is added to the infrastructure.</param>
    internal static void ConfigureContainerRegistry(
        AzureResourceInfrastructure infrastructure,
        Action<ContainerRegistryService, AzureResourceInfrastructure>? configureNewRegistry)
    {
        var azureResource = (AzureContainerRegistryResource)infrastructure.AspireResource;

        // Check if this Container Registry has a private endpoint (via annotation)
        var hasPrivateEndpoint = azureResource.HasAnnotationOfType<PrivateEndpointTargetAnnotation>();

        var registry = AzureProvisioningResource.CreateExistingOrNewProvisionableResource(infrastructure,
            (identifier, name) =>
            {
                var resource = ContainerRegistryService.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infra) =>
            {
                var svc = new ContainerRegistryService(infra.AspireResource.GetBicepIdentifier())
                {
                    // Private endpoints require Premium SKU.
                    Sku = new() { Name = hasPrivateEndpoint ? ContainerRegistrySkuName.Premium : ContainerRegistrySkuName.Basic },
                    Tags = { { "aspire-resource-name", infra.AspireResource.Name } }
                };

                // When using private endpoints, disable public network access.
                if (hasPrivateEndpoint)
                {
                    svc.PublicNetworkAccess = ContainerRegistryPublicNetworkAccess.Disabled;
                }

                configureNewRegistry?.Invoke(svc, infrastructure);

                return svc;
            });

        infrastructure.Add(registry);
        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = registry.Name.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput("loginServer", typeof(string)) { Value = registry.LoginServer.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = registry.Id.ToBicepExpression() });
    }
}
