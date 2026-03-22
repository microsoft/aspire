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
    [AspireExport("publishAsAzureAppServiceWebsite", Description = "Publishes the compute resource as an Azure App Service website or deployment slot")]
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

    /// <summary>
    /// Skips validation for environment variable names that Azure App Service may not support.
    /// </summary>
    /// <remarks>
    /// When running on Azure App Service, environment variable names can't contain hyphens.
    /// This can cause Aspire client integrations that rely on the original environment variable names to fail.
    /// By default, Aspire performs validation to ensure environment variable names are compatible with Azure App Service,
    /// failing to publish if any invalid names are found.
    /// </remarks>
    /// <typeparam name="T">The type of the compute resource.</typeparam>
    /// <param name="builder">The compute resource builder.</param>
    /// <returns>The updated compute resource builder.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the resource is not configured for Azure App Service publishing.</exception>
    [AspireExport("skipEnvironmentVariableNameChecks", Description = "Skips Azure App Service environment variable name validation for the compute resource")]
    public static IResourceBuilder<T> SkipEnvironmentVariableNameChecks<T>(this IResourceBuilder<T> builder)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        if (!builder.Resource.HasAnnotationOfType<AzureAppServiceWebsiteCustomizationAnnotation>() &&
            !builder.Resource.HasAnnotationOfType<AzureAppServiceWebsiteSlotCustomizationAnnotation>())
        {
            throw new InvalidOperationException($"{nameof(SkipEnvironmentVariableNameChecks)} can only be used after PublishAsAzureAppServiceWebsite.");
        }

        return builder.WithAnnotation(new AzureAppServiceIgnoreEnvironmentVariableChecksAnnotation());
    }
}
