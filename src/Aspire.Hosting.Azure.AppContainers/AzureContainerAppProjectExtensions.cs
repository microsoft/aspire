// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.AppContainers;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for publishing project resources as container apps in Azure.
/// </summary>
public static class AzureContainerAppProjectExtensions
{
    /// <summary>
    /// Allows configuring the specified project resource as a container app.
    /// </summary>
    /// <typeparam name="T">The type of the project resource.</typeparam>
    /// <param name="project">The project resource builder.</param>
    /// <param name="configure">The configuration action for the container app.</param>
    /// <returns>The updated project resource builder.</returns>
    /// <remarks>
    /// This method adds the necessary infrastructure for container apps to the application builder
    /// and applies the specified configuration to the container app.
    /// <example>
    /// <code>
    /// builder.AddProject&lt;Projects.Api&gt;.PublishAsAzureContainerApp((infrastructure, app) =>
    /// {
    ///     // Configure the container app here
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport("publishProjectAsAzureContainerApp", MethodName = "publishAsAzureContainerApp", Description = "Configures the project resource to be published as an Azure Container App")]
    public static IResourceBuilder<T> PublishAsAzureContainerApp<T>(this IResourceBuilder<T> project, Action<AzureResourceInfrastructure, ContainerApp> configure)
        where T : ProjectResource
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(configure);

        if (!project.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return project;
        }

        project.ApplicationBuilder.AddAzureContainerAppsInfrastructureCore();

        project.WithAnnotation(new AzureContainerAppCustomizationAnnotation(configure));

        return project;
    }
}
