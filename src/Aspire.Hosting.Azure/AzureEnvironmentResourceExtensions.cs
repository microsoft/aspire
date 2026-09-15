// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Provides extension methods for adding Azure environment resources to the application model.
/// </summary>
public static class AzureEnvironmentResourceExtensions
{
    /// <summary>
    /// Adds an Azure environment resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureEnvironmentResource}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureEnvironmentResource> AddAzureEnvironment(this IDistributedApplicationBuilder builder)
    {
        if (builder.Resources.OfType<AzureEnvironmentResource>().SingleOrDefault() is { } existingResource)
        {
            // If the resource already exists, return the existing builder
            return builder.CreateResourceBuilder(existingResource);
        }

        var resourceName = builder.CreateDefaultAzureEnvironmentName();
        var locationParam = ParameterResourceBuilderExtensions.CreateParameter(builder, "location", false);
        var resourceGroupName = ParameterResourceBuilderExtensions.CreateParameter(builder, "resourceGroupName", false);
        var principalId = ParameterResourceBuilderExtensions.CreateParameter(builder, "principalId", false);

        var resource = new AzureEnvironmentResource(resourceName, locationParam, resourceGroupName, principalId);
        if (builder.ExecutionContext.IsRunMode)
        {
            var resourceBuilder = builder.AddResource(resource)
                .OnInitializeResource(ProvisionAzureResourcesAsync)
                .WithInitialState(new CustomResourceSnapshot
                {
                    ResourceType = nameof(AzureEnvironmentResource),
                    CreationTimeStamp = DateTime.UtcNow,
                    State = KnownResourceStates.NotStarted,
                    Properties = ImmutableArray<ResourcePropertySnapshot>.Empty
                });

            foreach (var command in AzureProvisioningController.EnvironmentCommandDefinitions)
            {
                resourceBuilder.WithCommand(
                    command.Name,
                    command.DisplayName,
                    executeCommand: context => command.ExecuteCommand(context.Services.GetRequiredService<AzureProvisioningController>(), context),
                    commandOptions: new CommandOptions
                    {
                        Description = command.Description,
                        ConfirmationMessage = command.ConfirmationMessage,
                        IconName = command.IconName,
                        IconVariant = command.IconVariant,
                        IsHighlighted = command.IsHighlighted,
                        Arguments = command.Arguments ?? [],
                        ValidateArguments = command.ValidateArguments,
                        UpdateState = context => context.Services.GetRequiredService<AzureProvisioningController>().GetEnvironmentCommandState()
                    });
            }

            return resourceBuilder.ExcludeFromManifest();
        }

        // In publish mode, add the resource to the application model
        // but exclude it from the manifest so that it is not treated
        // as a publishable resource by components that process the manifest
        // for elements.
        // We need to always add the resource because the AzureEnvironmentResource
        // needs to show up in the app model during run mode so that we can discover
        // the pipeline step annotations on it but it needs to be hidden from the end-user.
        return builder.AddResource(resource)
            .ExcludeFromManifest()
            .WithInitialState(new()
            {
                ResourceType = nameof(AzureEnvironmentResource),
                Properties = [],
                IsHidden = true // hidden from the dashboard
            });
    }

    private static async Task ProvisionAzureResourcesAsync(AzureEnvironmentResource resource, InitializeResourceEvent @event, CancellationToken cancellationToken)
    {
        var model = @event.Services.GetRequiredService<DistributedApplicationModel>();
        var provisioner = @event.Services.GetRequiredService<AzureProvisioner>();

        await provisioner.ProvisionResourcesAsync(model, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the location of the Azure environment resource.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{TResource}"/>.</param>
    /// <param name="location">The Azure location.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureEnvironmentResource}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// This method is used to set the location of the Azure environment resource.
    /// The location is used to determine where the resources will be deployed.
    /// </remarks>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureEnvironmentResource> WithLocation(
        this IResourceBuilder<AzureEnvironmentResource> builder,
        IResourceBuilder<ParameterResource> location)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(location);

        builder.Resource.Location = location.Resource;

        return builder;
    }

    /// <summary>
    /// Sets the resource group name of the Azure environment resource.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{TResource}"/>.</param>
    /// <param name="resourceGroup">The Azure resource group name.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureEnvironmentResource}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// This method is used to set the resource group name of the Azure environment resource.
    /// The resource group name is used to determine where the resources will be deployed.
    /// </remarks>
    [AspireExport]
    [Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<AzureEnvironmentResource> WithResourceGroup(
        this IResourceBuilder<AzureEnvironmentResource> builder,
        IResourceBuilder<ParameterResource> resourceGroup)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resourceGroup);

        builder.Resource.ResourceGroupName = resourceGroup.Resource;

        return builder;
    }

    private static string CreateDefaultAzureEnvironmentName(this IDistributedApplicationBuilder builder)
    {
        var name = "azure-environment";
        var index = 2;
        while (builder.Resources.Any(resource => StringComparers.ResourceName.Equals(resource.Name, name)))
        {
            name = $"azure-environment{index++}";
        }

        return name;
    }
}
