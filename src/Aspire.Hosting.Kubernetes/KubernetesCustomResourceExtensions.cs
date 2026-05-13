// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring Kubernetes Custom Resource in the Aspire application model.
/// </summary>
public static class KubernetesCustomResourceExtensions
{
    /// <summary>
    /// Adds a custom Kubernetes resource to the application model as a child of the specified Kubernetes environment.
    /// This will generate a single yaml file in the Helm charts at publish time.
    /// </summary>
    /// <typeparam name="TSpec">The shape and structure of the custom resource's <c>spec</c> block.</typeparam>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the custom resource.</param>
    /// <param name="apiVersion">The API version the CRD uses.</param>
    /// <param name="kind">The kind or label of the CRD.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesCustomResourceResouce}"/> for chaining.</returns>
    [AspireExport(Description = "Adds a custom resource to the Kubernetes manifest.")]
    public static IResourceBuilder<KubernetesCustomResourceResource<TSpec>> AddCustomResource<TSpec>(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name, string apiVersion, string kind) where TSpec : class, new()
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(apiVersion);
        ArgumentException.ThrowIfNullOrEmpty(kind);

        var crd = new KubernetesCustomResourceResource<TSpec>(name, builder.Resource)
        {
            ApiVersion = apiVersion,
            Kind = kind
        };
        
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(crd);
        }

        return builder.ApplicationBuilder.AddResource(crd)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Sets the spec to use on the custom resource.
    /// </summary>
    /// <typeparam name="TSpec">The shape and structure of the custom resource's spec.</typeparam>
    /// <param name="builder">The custom resource builder.</param>
    /// <param name="spec">The spec to publish with the manifests.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesCustomResourceResource}"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "The method requires a custom-typed spec defintion, which is not supported by the exporter.")]
    public static IResourceBuilder<KubernetesCustomResourceResource<TSpec>> WithSpec<TSpec>(
        this IResourceBuilder<KubernetesCustomResourceResource<TSpec>> builder, 
        TSpec spec)
    where TSpec : class, new()
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Spec = spec;

        return builder;
    }

    internal static bool IsCustomResource(this IResource resource)
    {
        var type = resource.GetType();
        while (type != null)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KubernetesCustomResourceResource<>))
            {
                return true;
            }

            type = type.BaseType;
        }

        return false;
    }
}