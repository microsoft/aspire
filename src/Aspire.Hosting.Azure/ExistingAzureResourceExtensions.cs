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
    [AspireExport("isExisting")]
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
    [AspireExportIgnore(Reason = "Use the polyglot runAsExisting overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource>? resourceGroupParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, nameParameter.Resource, resourceGroupParameter?.Resource);
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExportIgnore(Reason = "Use the polyglot runAsExisting overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExisting<T>(this IResourceBuilder<T> builder, string name, string? resourceGroup)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, name, resourceGroup);
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("runAsExisting")]
    internal static IResourceBuilder<T> RunAsExistingForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object? resourceGroup = null)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(resourceGroup, nameof(resourceGroup), allowNull: true);

        return RunAsExistingCore(builder, name, resourceGroup);
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExisting overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource>? resourceGroupParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, nameParameter.Resource, resourceGroupParameter?.Resource);
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExisting overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExisting<T>(this IResourceBuilder<T> builder, string name, string? resourceGroup)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, name, resourceGroup);
    }

    /// <summary>
    /// Marks the resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("publishAsExisting")]
    internal static IResourceBuilder<T> PublishAsExistingForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object? resourceGroup = null)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(resourceGroup, nameof(resourceGroup), allowNull: true);

        return PublishAsExistingCore(builder, name, resourceGroup);
    }

    /// <summary>
    /// Marks the resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group, or <see langword="null"/> to use the current resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExportIgnore(Reason = "Use the polyglot asExisting overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> AsExisting<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource>? resourceGroupParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AsExistingCore(builder, nameParameter.Resource, resourceGroupParameter?.Resource);
    }

    /// <summary>
    /// Marks the resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="resourceGroup">The name of the existing resource group as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("asExisting")]
    internal static IResourceBuilder<T> AsExistingForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object? resourceGroup = null)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(resourceGroup, nameof(resourceGroup), allowNull: true);

        return AsExistingCore(builder, name, resourceGroup);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Use this only for Azure resources that are deployed at subscription scope. Most Azure services are resource-group scoped and should use <c>RunAsExistingInResourceGroup</c>.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot runAsExistingInSubscription overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExistingInSubscription<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, nameParameter.Resource, resourceGroup: null, subscription: subscriptionParameter.Resource);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Use this only for Azure resources that are deployed at subscription scope. Most Azure services are resource-group scoped and should use <c>RunAsExistingInResourceGroup</c>.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot runAsExistingInSubscription overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExistingInSubscription<T>(this IResourceBuilder<T> builder, string name, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, name, resourceGroup: null, subscription: subscription);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("runAsExistingInSubscription")]
    internal static IResourceBuilder<T> RunAsExistingInSubscriptionForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object subscription)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(subscription, nameof(subscription));

        return RunAsExistingCore(builder, name, resourceGroup: null, subscription: subscription);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Use this only for Azure resources that are deployed at subscription scope. Most Azure services are resource-group scoped and should use <c>PublishAsExistingInResourceGroup</c>.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExistingInSubscription overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExistingInSubscription<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, nameParameter.Resource, resourceGroup: null, subscription: subscriptionParameter.Resource);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Use this only for Azure resources that are deployed at subscription scope. Most Azure services are resource-group scoped and should use <c>PublishAsExistingInResourceGroup</c>.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExistingInSubscription overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExistingInSubscription<T>(this IResourceBuilder<T> builder, string name, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, name, resourceGroup: null, subscription: subscription);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("publishAsExistingInSubscription")]
    internal static IResourceBuilder<T> PublishAsExistingInSubscriptionForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object subscription)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(subscription, nameof(subscription));

        return PublishAsExistingCore(builder, name, resourceGroup: null, subscription: subscription);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Use this only for Azure resources that are deployed at subscription scope. Most Azure services are resource-group scoped and should use <c>AsExistingInResourceGroup</c>.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot asExistingInSubscription overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> AsExistingInSubscription<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AsExistingCore(builder, nameParameter.Resource, resourceGroup: null, subscription: subscriptionParameter.Resource);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Use this only for Azure resources that are deployed at subscription scope. Most Azure services are resource-group scoped and should use <c>AsExistingInResourceGroup</c>.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot asExistingInSubscription overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> AsExistingInSubscription<T>(this IResourceBuilder<T> builder, string name, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AsExistingCore(builder, name, resourceGroup: null, subscription: subscription);
    }

    /// <summary>
    /// Marks the subscription-scoped resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("asExistingInSubscription")]
    internal static IResourceBuilder<T> AsExistingInSubscriptionForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object subscription)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(subscription, nameof(subscription));

        return AsExistingCore(builder, name, resourceGroup: null, subscription: subscription);
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <param name="resourceGroupParameter">The name of the existing resource group.</param>
    /// <param name="subscriptionParameter">The subscription identifier containing the resource group.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExportIgnore(Reason = "Use the polyglot runAsExistingInResourceGroup overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource);
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
    [AspireExportIgnore(Reason = "Use the polyglot runAsExistingInResourceGroup overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, name, resourceGroup, subscription);
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="resourceGroup">The resource group containing the existing resource as a string or parameter resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource group as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("runAsExistingInResourceGroup")]
    internal static IResourceBuilder<T> RunAsExistingInResourceGroupForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object resourceGroup,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object subscription)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(resourceGroup, nameof(resourceGroup));
        ValidateExistingResourceArgument(subscription, nameof(subscription));

        return RunAsExistingCore(builder, name, resourceGroup, subscription);
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
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExistingInResourceGroup overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource);
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
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExistingInResourceGroup overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, name, resourceGroup, subscription);
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="resourceGroup">The resource group containing the existing resource as a string or parameter resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource group as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("publishAsExistingInResourceGroup")]
    internal static IResourceBuilder<T> PublishAsExistingInResourceGroupForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object resourceGroup,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object subscription)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(resourceGroup, nameof(resourceGroup));
        ValidateExistingResourceArgument(subscription, nameof(subscription));

        return PublishAsExistingCore(builder, name, resourceGroup, subscription);
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
    [AspireExportIgnore(Reason = "Use the polyglot asExistingInResourceGroup overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> AsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter, IResourceBuilder<ParameterResource> resourceGroupParameter, IResourceBuilder<ParameterResource> subscriptionParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AsExistingCore(builder, nameParameter.Resource, resourceGroupParameter.Resource, subscriptionParameter.Resource);
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
    [AspireExportIgnore(Reason = "Use the polyglot asExistingInResourceGroup overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> AsExistingInResourceGroup<T>(this IResourceBuilder<T> builder, string name, string resourceGroup, string subscription)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AsExistingCore(builder, name, resourceGroup, subscription);
    }

    /// <summary>
    /// Marks the resource as an existing resource in a specific resource group and subscription in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <param name="resourceGroup">The resource group containing the existing resource as a string or parameter resource.</param>
    /// <param name="subscription">The subscription identifier containing the resource group as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("asExistingInResourceGroup")]
    internal static IResourceBuilder<T> AsExistingInResourceGroupForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object resourceGroup,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object subscription)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));
        ValidateExistingResourceArgument(resourceGroup, nameof(resourceGroup));
        ValidateExistingResourceArgument(subscription, nameof(subscription));

        return AsExistingCore(builder, name, resourceGroup, subscription);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Tenant scope targets the current tenant. Bicep doesn't support selecting a different tenant with the <c>tenant()</c> scope function.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot runAsExistingInTenant overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExistingInTenant<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, nameParameter.Resource, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Tenant scope targets the current tenant. Bicep doesn't support selecting a different tenant with the <c>tenant()</c> scope function.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot runAsExistingInTenant overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> RunAsExistingInTenant<T>(this IResourceBuilder<T> builder, string name)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return RunAsExistingCore(builder, name, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource when the application is running.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("runAsExistingInTenant")]
    internal static IResourceBuilder<T> RunAsExistingInTenantForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));

        return RunAsExistingCore(builder, name, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Tenant scope targets the current tenant. Bicep doesn't support selecting a different tenant with the <c>tenant()</c> scope function.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExistingInTenant overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExistingInTenant<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, nameParameter.Resource, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Tenant scope targets the current tenant. Bicep doesn't support selecting a different tenant with the <c>tenant()</c> scope function.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot publishAsExistingInTenant overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> PublishAsExistingInTenant<T>(this IResourceBuilder<T> builder, string name)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return PublishAsExistingCore(builder, name, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource when the application is deployed.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("publishAsExistingInTenant")]
    internal static IResourceBuilder<T> PublishAsExistingInTenantForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));

        return PublishAsExistingCore(builder, name, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="nameParameter">The name of the existing resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Tenant scope targets the current tenant. Bicep doesn't support selecting a different tenant with the <c>tenant()</c> scope function.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot asExistingInTenant overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> AsExistingInTenant<T>(this IResourceBuilder<T> builder, IResourceBuilder<ParameterResource> nameParameter)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AsExistingCore(builder, nameParameter.Resource, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    /// <remarks>Tenant scope targets the current tenant. Bicep doesn't support selecting a different tenant with the <c>tenant()</c> scope function.</remarks>
    [AspireExportIgnore(Reason = "Use the polyglot asExistingInTenant overload that accepts string or ParameterResource values instead.")]
    public static IResourceBuilder<T> AsExistingInTenant<T>(this IResourceBuilder<T> builder, string name)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return AsExistingCore(builder, name, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    /// <summary>
    /// Marks the current-tenant-scoped resource as an existing resource in both run and publish modes.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the existing resource as a string or parameter resource.</param>
    /// <returns>The resource builder with the existing resource annotation added.</returns>
    [AspireExport("asExistingInTenant")]
    internal static IResourceBuilder<T> AsExistingInTenantForPolyglot<T>(
        this IResourceBuilder<T> builder,
        [AspireUnion(typeof(string), typeof(ParameterResource))] object name)
        where T : IAzureResource
    {
        ValidateExistingResourceArgument(name, nameof(name));

        return AsExistingCore(builder, name, resourceGroup: null, subscription: null, isTenantScope: true);
    }

    private static IResourceBuilder<T> RunAsExistingCore<T>(IResourceBuilder<T> builder, object name, object? resourceGroup, object? subscription = null, bool isTenantScope = false)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(CreateExistingAzureResourceAnnotation(name, resourceGroup, subscription, isTenantScope));
        }

        return builder;
    }

    private static IResourceBuilder<T> PublishAsExistingCore<T>(IResourceBuilder<T> builder, object name, object? resourceGroup, object? subscription = null, bool isTenantScope = false)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            builder.WithAnnotation(CreateExistingAzureResourceAnnotation(name, resourceGroup, subscription, isTenantScope));
        }

        return builder;
    }

    private static IResourceBuilder<T> AsExistingCore<T>(IResourceBuilder<T> builder, object name, object? resourceGroup, object? subscription = null, bool isTenantScope = false)
        where T : IAzureResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(CreateExistingAzureResourceAnnotation(name, resourceGroup, subscription, isTenantScope));

        return builder;
    }

    private static ExistingAzureResourceAnnotation CreateExistingAzureResourceAnnotation(object name, object? resourceGroup, object? subscription, bool isTenantScope)
    {
        if (isTenantScope)
        {
            return new ExistingAzureResourceAnnotation(name, isTenantScope: true);
        }

        if (subscription is not null)
        {
            return new ExistingAzureResourceAnnotation(name, resourceGroup, subscription);
        }

        return new ExistingAzureResourceAnnotation(name, resourceGroup);
    }

    private static void ValidateExistingResourceArgument(object? value, string paramName, bool allowNull = false)
    {
        if (value is null)
        {
            if (allowNull)
            {
                return;
            }

            throw new ArgumentNullException(paramName);
        }

        if (value is not string && value is not ParameterResource)
        {
            throw new ArgumentException("Value must be a string or ParameterResource.", paramName);
        }
    }
}
