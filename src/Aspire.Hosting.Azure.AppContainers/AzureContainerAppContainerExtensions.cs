// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for publishing container resources as container apps in Azure.
/// </summary>
public static class AzureContainerAppContainerExtensions
{
    /// <summary>
    /// Publishes the specified container resource as a container app.
    /// </summary>
    /// <typeparam name="T">The type of the container resource.</typeparam>
    /// <param name="container">The container resource builder.</param>
    /// <param name="configure">The configuration action for the container app.</param>
    /// <returns>The updated container resource builder.</returns>
    /// <remarks>
    /// This method checks if the application is in publish mode. If it is, it adds the necessary infrastructure
    /// for container apps and applies the provided configuration action to the container app.
    /// <example>
    /// <code>
    /// builder.AddContainer("name", "image").PublishAsAzureContainerApp((infrastructure, app) =>
    /// {
    ///     // Configure the container app here
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("publishContainerAsAzureContainerApp", MethodName = "publishAsAzureContainerApp", Description = "Configures the container resource to be published as an Azure Container App")]
    public static IResourceBuilder<T> PublishAsAzureContainerApp<T>(this IResourceBuilder<T> container, Action<AzureResourceInfrastructure, ContainerApp> configure)
        where T : ContainerResource
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(configure);

        if (!container.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return container;
        }

        container.ApplicationBuilder.AddAzureContainerAppsInfrastructureCore();

        container.WithAnnotation(new AzureContainerAppCustomizationAnnotation(configure));

        return container;
    }
}
