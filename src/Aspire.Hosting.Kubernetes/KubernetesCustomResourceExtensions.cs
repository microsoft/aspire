// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using YamlDotNet.Serialization;

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
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the custom resource.</param>
    /// <param name="apiVersion">The API version the CRD uses.</param>
    /// <param name="kind">The kind or label of the CRD.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesCustomResourceResouce}"/> for chaining.</returns>
    [AspireExport(Description = "Adds a custom resource to the Kubernetes manifest.")]
    public static IResourceBuilder<KubernetesCustomResourceResource> AddCustomResource(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name, string apiVersion, string kind)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(apiVersion);
        ArgumentException.ThrowIfNullOrEmpty(kind);

        var crd = new KubernetesCustomResourceResource(name, builder.Resource)
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
    /// <param name="builder">The custom resource builder.</param>
    /// <param name="spec">The spec to publish with the manifests.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesCustomResourceResource}"/> for chaining.</returns>
    /// <remarks>
    /// In order to ensure proper serialization, the TSpec class must be annotated with <see cref="YamlSerializableAttribute"/>,
    /// and members must be annotated with the <see cref="YamlMemberAttribute"/>. Refer to the example below.
    /// </remarks>
    /// <example>
    /// <code>
    /// [YamlSerializable]
    /// public class MyCustomResourceSpec 
    /// {
    ///     [YamlMember(Alias = "myMember")]
    ///     public string MyMember { get; set; }
    ///     
    ///     [YamlMember(Alias = "myArray")]
    ///     public string[] MyArray { get; set; }
    /// 
    ///     [YamlMember(Alias = "myNestedObject")]
    ///     public MyObjectV1 MyNestedObject { get; set; }
    /// }
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a spec file to a CRD resource.")]
    public static IResourceBuilder<KubernetesCustomResourceResource> WithSpec(
        this IResourceBuilder<KubernetesCustomResourceResource> builder, 
        object spec)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.Spec = spec;

        return builder;
    }
}