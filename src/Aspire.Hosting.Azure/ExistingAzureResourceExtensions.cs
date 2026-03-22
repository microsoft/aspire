// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for interacting with resources that are not managed by Aspire's provisioning or
/// container management layer.
/// </summary>
public static class ExistingAzureResourceExtensions
{
    /// <summary>
    /// Determines if the resource is an existing resource.
    /// </summary>
    /// <param name="resource">The resource to check.</param>
    /// <returns>True if the resource is an existing resource, otherwise false.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use the Azure resource-specific polyglot surface instead.</remarks>
    [AspireExportIgnore(Reason = "Use the Azure resource-specific polyglot export instead.")]
    public static bool IsExisting(this IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        return resource.Annotations.OfType<ExistingAzureResourceAnnotation>().LastOrDefault() is not null;
    }

    /// <summary>
    /// Determines whether the Azure resource is marked as existing.
    /// </summary>
    /// <param name="resource">The Azure resource to check.</param>
    /// <returns><see langword="true"/> if the resource is marked as existing; otherwise, <see langword="false"/>.</returns>
    [AspireExport("isExisting", Description = "Determines whether a resource is marked as existing")]
    internal static bool IsExistingForPolyglot(this IAzureResource resource)
    {
        return ((IResource)resource).IsExisting();
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("runAsExistingFromParameters", Description = "Marks an Azure resource as existing in run mode by using parameter resources")]
    public static IResourceBuilder<T> RunAsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource>? resourceGroupParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter?.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("runAsExisting", Description = "Marks an Azure resource as existing in run mode")]
    public static IResourceBuilder<T> RunAsExisting<T>(this IResourceBuilder<T> builder, string name, string? resourceGroup)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("publishAsExistingFromParameters", Description = "Marks an Azure resource as existing in publish mode by using parameter resources")]
    public static IResourceBuilder<T> PublishAsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource>? resourceGroupParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter?.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("publishAsExisting", Description = "Marks an Azure resource as existing in publish mode")]
    public static IResourceBuilder<T> PublishAsExisting<T>(this IResourceBuilder<T> builder, string name, string? resourceGroup)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("asExistingFromParameters", MethodName = "asExisting", Description = "Marks an Azure resource as existing in both run and publish modes by using parameter resources")]
    public static IResourceBuilder<T> AsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource>? resourceGroupParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter?.Resource));

        return builder;
    }
}
