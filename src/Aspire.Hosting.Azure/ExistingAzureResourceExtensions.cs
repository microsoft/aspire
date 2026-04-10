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
    [AspireExport(Description = "Marks an Azure resource as existing in run mode")]
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
    [AspireExport(Description = "Marks an Azure resource as existing in publish mode")]
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

    /// <summary>
    /// Marks the resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> AsExisting<T>(this IResourceBuilder<T> builder, string name, string? resourceGroup)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup));

        return builder;
    }

    // ===== Subscription-scoped existing resource methods =====

    /// <summary>
    /// Marks the resource as an existing resource in a specific subscription when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> RunAsExistingInSubscription<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroup: null, subscriptionParameter.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific subscription when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> RunAsExistingInSubscription<T>(this IResourceBuilder<T> builder, string name, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup: null, subscription));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific subscription when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> PublishAsExistingInSubscription<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroup: null, subscriptionParameter.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific subscription when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> PublishAsExistingInSubscription<T>(this IResourceBuilder<T> builder, string name, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup: null, subscription));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific subscription in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> AsExistingInSubscription<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroup: null, subscriptionParameter.Resource));

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific subscription in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> AsExistingInSubscription<T>(this IResourceBuilder<T> builder, string name, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup: null, subscription));

        return builder;
    }

    // ===== Resource group-scoped existing resource methods (with subscription) =====

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> RunAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier containing the resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> RunAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup, subscription));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> PublishAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier containing the resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> PublishAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup, subscription));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> AsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource));

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier containing the resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> AsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup, subscription));

        return builder;
    }

    // ===== Existing resource methods with tenant context =====

    /// <summary>
    /// Marks the resource as an existing resource with a specific tenant context when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <param name="tenantParameter">The tenant identifier containing the subscription.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> RunAsExistingWithTenant<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter, IResourceBuilder<ParameterResource> tenantParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource, tenantParameter.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource with a specific tenant context when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <param name="tenant">The tenant identifier containing the subscription.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> RunAsExistingWithTenant<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription, string tenant)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup, subscription, tenant));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource with a specific tenant context when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <param name="tenantParameter">The tenant identifier containing the subscription.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> PublishAsExistingWithTenant<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter, IResourceBuilder<ParameterResource> tenantParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource, tenantParameter.Resource));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource with a specific tenant context when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <param name="tenant">The tenant identifier containing the subscription.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> PublishAsExistingWithTenant<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription, string tenant)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup, subscription, tenant));
        }

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource with a specific tenant context in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <param name="tenantParameter">The tenant identifier containing the subscription.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> AsExistingWithTenant<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter, IResourceBuilder<ParameterResource> tenantParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource, tenantParameter.Resource));

        return builder;
    }

    /// <summary>
    /// Marks the resource as an existing resource with a specific tenant context in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <param name="tenant">The tenant identifier containing the subscription.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    public static IResourceBuilder<T> AsExistingWithTenant<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription, string tenant)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new ExistingAzureResourceAnnotation(name, resourceGroup, subscription, tenant));

        return builder;
    }
}
