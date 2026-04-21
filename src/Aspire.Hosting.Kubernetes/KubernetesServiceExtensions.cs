// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for customizing Kubernetes service resources.
/// </summary>
public static class KubernetesServiceExtensions
{
    /// <summary>
    /// Publishes the specified resource as a Kubernetes service.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">The configuration action for the Kubernetes service.</param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// This method checks if the application is in publish mode. If it is, it adds a customization annotation
    /// that will be applied by the infrastructure when generating the Kubernetes service.
    /// <example>
    /// <code>
    /// builder.AddContainer("redis", "redis:alpine").PublishAsKubernetesService((service) =>
    /// {
    ///     service.Name = "redis";
    /// });
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExport(Description = "Publishes the resource as a Kubernetes service")]
    public static IResourceBuilder<T> PublishAsKubernetesService<T>(this IResourceBuilder<T> builder, Action<KubernetesResource> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        builder.ApplicationBuilder.AddKubernetesInfrastructureCore();

        return builder.WithAnnotation(new KubernetesServiceCustomizationAnnotation(configure));
    }

    /// <summary>
    /// Overrides the ingress configuration for this specific resource, ignoring the
    /// environment-level default set by <see cref="KubernetesEnvironmentExtensions.WithIngress(IResourceBuilder{KubernetesEnvironmentResource}, Func{KubernetesIngressContext, Task})"/>.
    /// </summary>
    /// <typeparam name="T">The type of the resource.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="configure">
    /// A callback that configures ingress for this resource. The callback typically adds
    /// <see cref="Kubernetes.Resources.Ingress"/> objects to <see cref="KubernetesResource.AdditionalResources"/>.
    /// </param>
    /// <returns>The updated resource builder.</returns>
    /// <remarks>
    /// This annotation is checked before the environment-level annotation. The last
    /// annotation added wins.
    /// </remarks>
    [AspireExport(Description = "Overrides ingress configuration for a specific Kubernetes resource")]
    public static IResourceBuilder<T> WithKubernetesIngress<T>(
        this IResourceBuilder<T> builder,
        Func<KubernetesIngressContext, Task> configure)
        where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        if (!builder.ApplicationBuilder.ExecutionContext.IsPublishMode)
        {
            return builder;
        }

        builder.ApplicationBuilder.AddKubernetesInfrastructureCore();

        return builder.WithAnnotation(new KubernetesIngressConfigurationAnnotation(configure));
    }
}
