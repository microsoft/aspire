// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Azure.Provisioning.AppService;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for publishing compute resources as Azure App Service websites.
/// </summary>
public static class AzureAppServiceComputeResourceExtensions
{
    /// <summary>
    /// Publishes the specified compute resource as an Azure App Service or Azure App Service Slot.
    /// </summary>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <param name="configure">The configuration action for the App Service WebSite resource.</param>
    /// <param name="configureSlot">The configuration action for the App Service WebSite Slot resource.</param>
    /// <returns>The updated compute resource builder.</returns>
    /// <remarks>
    /// <example>
    /// <code>
    /// builder.AddProject&lt;Projects.Api&gt;("name").PublishAsAzureAppServiceWebsite((infrastructure, app) =>
    /// {
    ///     // Configure the App Service WebSite resource here
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> PublishAsAzureAppServiceWebsite<T>(this IResourceBuilder<T> builder,
        Action<AzureResourceInfrastructure, WebSite>? configure = null,
        Action<AzureResourceInfrastructure, WebSiteSlot>? configureSlot = null)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (configure == null && configureSlot == null)
        {
            throw new ArgumentException("configure or configureSlot must be provided.");
        }

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        builder.ApplicationBuilder.AddAzureAppServiceInfrastructureCore();

        if (configure != null)
        {
            builder = builder.WithAnnotation(new AzureAppServiceWebsiteCustomizationAnnotation(configure));
        }

        if (configureSlot != null)
        {
            builder = builder.WithAnnotation(new AzureAppServiceWebsiteSlotCustomizationAnnotation(configureSlot));
        }
        return builder;
    }
}
