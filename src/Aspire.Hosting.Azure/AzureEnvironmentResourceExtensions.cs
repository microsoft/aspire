// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

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
    [AspireExport("addAzureEnvironment", Description = "Adds the shared Azure environment resource to the application model")]
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
            // Return a builder that isn't added to the top-level application builder
            // so it doesn't surface as a resource.
            return builder.CreateResourceBuilder(resource);

        }

        // In publish mode, add the resource to the application model
        // but exclude it from the manifest so that it is not treated
        // as a publishable resource by components that process the manifest
        // for elements.
        return builder.AddResource(resource)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Sets the location of the Azure environment resource.
    /// </summary>
    /// <param name="builder">The <see cref="IResourceBuilder{TResource}"/>.</param>
    /// <param name="location">The Azure location.</param>
    /// <returns>The <see cref="IResourceBuilder{AzureEnvironmentResource}"/>.</returns>
    /// <remarks>
    /// This method is used to set the location of the Azure environment resource.
    /// The location is used to determine where the resources will be deployed.
    /// </remarks>
    [AspireExport("withLocation", Description = "Sets the Azure location for the shared Azure environment resource")]
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
    /// <remarks>
    /// This method is used to set the resource group name of the Azure environment resource.
    /// The resource group name is used to determine where the resources will be deployed.
    /// </remarks>
    [AspireExport("withResourceGroup", Description = "Sets the Azure resource group for the shared Azure environment resource")]
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
        // Use ProjectNameSha256 for stable naming across deployments
        var applicationHash = builder.Configuration["AppHost:ProjectNameSha256"]?[..5].ToLowerInvariant();
        return $"azure{applicationHash}";
    }
}
