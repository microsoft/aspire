// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Extensions;
using Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Custom Resource for deployment. This will most commonly be a custom object that conforms to the schema
/// of a CRD that already exists on the Kubernetes cluster.
/// </summary>
public interface IKubernetesCustomResourceResource : IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Builds a <see cref="CustomResourceV1"/> from the <see cref="IKubernetesCustomResourceResource"/> and
    /// assigns the result to <see cref="GeneratedResource"/>.
    /// </summary>
    /// <returns>A fully configured <see cref="CustomResourceV1"/> ready for publishing.</returns>
    object Build();

    /// <summary>
    /// Gets or sets the publisher-ready resource.
    /// </summary>
    object? GeneratedResource { get; set; }
}

/// <summary>
/// Represents a Custom Resource for deployment along with the compute resources from the app model.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
public sealed class KubernetesCustomResourceResource(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IKubernetesCustomResourceResource
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
    public object Spec { get; set; } = new();
    
    /// <inheritdoc />
    public object? GeneratedResource { get; set; }

    /// <inheritdoc />
    public object Build()
    {
        var builtResource = new CustomResourceV1(ApiVersion, Kind)
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