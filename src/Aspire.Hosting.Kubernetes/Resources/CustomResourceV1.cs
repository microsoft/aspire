// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a CustomResource that can be published with the Kubernetes manifest.
/// </summary>
/// <param name="apiVersion">The API Version the CRD uses.</param>
/// <param name="kind">The kind of CRD being applied.</param>
[YamlSerializable]
public sealed class CustomResourceV1(string apiVersion, string kind) : BaseKubernetesResource(apiVersion, kind)
{
    /// <summary>
    /// Gets or sets the spec for a Kubernetes resource.
    /// </summary>
    [YamlMember(Alias = "spec")]
    public object Spec { get; set; } = new();
}