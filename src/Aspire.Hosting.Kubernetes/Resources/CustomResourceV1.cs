// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a CustomResource that can be published with the Kubernetes manifest.
/// </summary>
/// <typeparam name="TSpec">The type of the Spec block.</typeparam>
/// <param name="apiVersion">The API Version the CRD uses.</param>
/// <param name="kind">The kind of CRD being applied.</param>
[YamlSerializable]
public sealed class CustomResourceV1<TSpec>(string apiVersion, string kind) : BaseKubernetesResource(apiVersion, kind)
where TSpec : class, new()
{
    /// <summary>
    /// Gets or sets the spec for a Kubernetes resource.
    /// </summary>
    /// <remarks>
    /// The exact shape and structure of spec block is defined by the CRD. To apply a custom resource to 
    /// your Kubernetes manifests, you must first define the shape of the spec block using a <typeparamref name="TSpec"/>.
    /// </remarks>
    [YamlMember(Alias = "spec")]
    public TSpec? Spec { get; set; } = new();
}