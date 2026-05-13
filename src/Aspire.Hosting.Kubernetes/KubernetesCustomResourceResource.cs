// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// 
/// </summary>
public interface IKubernetesCustomResourceResource : IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    object Build();
    /// <summary>
    /// 
    /// </summary>
    object GeneratedResource { get; set; }
}

/// <summary>
/// Represents a Custom Resource for deployment along with the compute resources from the app model.
/// </summary>
/// <typeparam name="TSpec">The shape and structure of the custom resource's spec block</typeparam>
/// <param name="name">The name of the resource.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
public sealed class KubernetesCustomResourceResource<TSpec>(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IKubernetesCustomResourceResource
    where TSpec : class, new()
{
    /// <summary>
    /// Gets the parent Kubernetes environment resource.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <inheritdoc /> 
    public string ApiVersion { get; set; } = "";

    /// <inheritdoc /> 
    public string Kind { get; set; } = "";

    /// <inheritdoc /> 
    public TSpec? Spec { get; set; } = new();
    
    /// <summary>
    /// 
    /// </summary>
    public object GeneratedResource { get; set; } = new();

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public object Build()
    {
        var builtResource = new CustomResourceV1<TSpec>(ApiVersion, Kind)
        {
            Metadata =
            {
                Name = Name.ToKubernetesResourceName(),
            },
            Spec = Spec
        };

        this.GeneratedResource = builtResource;

        return builtResource;
    }
}