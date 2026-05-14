// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Custom Resource for deployment along with the compute resources from the app model.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
public sealed class KubernetesCustomResourceResource(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Gets the parent Kubernetes environment resource.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <inheritdoc /> 
    public ReferenceExpression ApiVersion { get; set; } = ReferenceExpression.Empty;

    /// <inheritdoc /> 
    public ReferenceExpression Kind { get; set; } = ReferenceExpression.Empty;

    /// <summary>
    /// Gets or sets the metadata that will be applied at the top-level of the manifest.
    /// </summary>
    public ObjectMetaV1? Metadata { get; set; }

    /// <inheritdoc /> 
    public CustomResourceSpecV1? Spec { get; set; }
    
    /// <inheritdoc />
    public CustomResourceV1? GeneratedResource { get; set; }
}