// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a backend for a Kubernetes ingress resource.
/// </summary>
/// <remarks>
/// This class defines the configuration of a backend to which ingress traffic is routed.
/// It supports two types of backend targets:
/// 1. A Kubernetes service, encapsulated in the <see cref="Service"/> property.
/// 2. A specific resource, defined using the <see cref="Resource"/> property.
/// </remarks>
[YamlSerializable]
public sealed class IngressBackendV1
{
    /// <summary>
    /// Represents a reference to a Kubernetes resource that is used as a backend for Ingress in the V1 API.
    /// </summary>
    /// <remarks>
    /// This property holds a reference to a specific resource using the TypedLocalObjectReferenceV1 type,
    /// allowing specification of the resource's kind, name, and associated API group.
    /// </remarks>
    [YamlMember(Alias = "resource")]
    public TypedLocalObjectReferenceV1 Resource { get; set; } = null!;

    /// <summary>
    /// Gets or sets the backend service information associated with the ingress resource.
    /// This includes the name of the service and its corresponding port configuration.
    /// </summary>
    [YamlMember(Alias = "service")]
    public IngressServiceBackendV1 Service { get; set; } = null!;
}
