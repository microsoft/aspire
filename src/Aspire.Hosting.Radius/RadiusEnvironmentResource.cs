// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Radius;

/// <summary>
/// Represents a Radius compute environment in the Aspire app model.
/// </summary>
[AspireExport(ExposeProperties = true)]
public class RadiusEnvironmentResource : Resource, IComputeEnvironmentResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RadiusEnvironmentResource"/> class.
    /// </summary>
    /// <param name="name">The name of the Radius environment resource.</param>
    public RadiusEnvironmentResource(string name) : base(name)
    {
    }

    /// <summary>
    /// Gets or sets the Kubernetes namespace for resource deployment.
    /// </summary>
    public string Namespace { get; set; } = "default";

    /// <summary>
    /// When <see langword="true"/>, the publisher emits container workloads as
    /// <c>Applications.Core/containers@2023-10-01-preview</c> (legacy) parented
    /// to the legacy application, instead of <c>Radius.Compute/containers</c>
    /// (UDT). This is a fallback for Radius installs that do not have a recipe
    /// registered for the <c>Radius.Compute/containers</c> UDT, since legacy
    /// containers ship with built-in Kubernetes deployment behaviour and do
    /// not require a recipe. When <see langword="true"/> and there are no UDT
    /// resource-type instances, the UDT environment / application / recipe
    /// pack chain is skipped entirely, producing pure-legacy Bicep that
    /// older Radius installs can deploy without modification. Defaults to
    /// <see langword="false"/>. Set via <c>WithLegacyContainers()</c>.
    /// </summary>
    public bool UseLegacyContainers { get; set; }

    /// <summary>
    /// Gets or sets the parent compute environment this Radius environment is hosted by, when
    /// the Radius env is itself a child of a higher-level compute environment (e.g. an Azure
    /// AKS environment that wraps both Kubernetes and Radius). When set, resources that target
    /// the parent environment are also adopted by this Radius environment during the prepare
    /// step. Defaults to <see langword="null"/> (no parent).
    /// </summary>
    /// <remarks>
    /// Mirrors <c>KubernetesEnvironmentResource.OwningComputeEnvironment</c>. Today this is
    /// always <see langword="null"/> for vanilla Radius; the property exists so an Azure
    /// hosting integration can wrap Radius the same way Azure Kubernetes wraps the K8s
    /// integration without needing a breaking change to this type.
    /// </remarks>
    public IComputeEnvironmentResource? OwningComputeEnvironment { get; set; }

    /// <inheritdoc/>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference)
    {
        var resource = endpointReference.Resource;
        return ReferenceExpression.Create($"{resource.Name}.svc.cluster.local");
    }
}
