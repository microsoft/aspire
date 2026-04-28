// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a Kubernetes Ingress as a first-class resource in the Aspire application model.
/// An Ingress defines HTTP routing rules that direct external traffic to services in the cluster.
/// </summary>
/// <param name="name">The name of the ingress resource.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
/// <remarks>
/// <para>
/// Create an ingress using <see cref="KubernetesIngressExtensions.AddIngress"/> and configure
/// routes using <see cref="KubernetesIngressExtensions.WithRoute(IResourceBuilder{KubernetesIngressResource}, string, EndpointReference, IngressPathType)"/>.
/// </para>
/// <para>
/// At publish time, the ingress generates a Kubernetes <c>networking.k8s.io/v1 Ingress</c> resource
/// in the Helm chart output with rules derived from the configured routes.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var k8s = builder.AddKubernetesEnvironment("k8s");
/// var ingress = k8s.AddIngress("public")
///     .WithIngressClass("nginx");
///
/// var api = builder.AddProject&lt;MyApi&gt;("api");
/// ingress.WithRoute("/api", api.GetEndpoint("http"));
/// </code>
/// </example>
[AspireExport]
public class KubernetesIngressResource(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Gets the parent Kubernetes environment resource.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Gets or sets the Kubernetes ingress class name that selects which ingress controller
    /// will handle this ingress resource.
    /// </summary>
    /// <remarks>
    /// Common values include <c>"nginx"</c>, <c>"traefik"</c>, <c>"azure-alb-external"</c> (for AKS with AGC),
    /// or controller-specific class names. If not set, the cluster's default ingress class is used.
    /// </remarks>
    public ReferenceExpression? IngressClassName { get; set; }

    /// <summary>
    /// Gets the list of hostnames this ingress matches. If empty, the ingress matches all hosts.
    /// </summary>
    internal List<ReferenceExpression> Hostnames { get; } = [];

    /// <summary>
    /// Gets the list of routing rules configured for this ingress.
    /// </summary>
    internal List<IngressRouteConfig> Routes { get; } = [];

    /// <summary>
    /// Gets the list of TLS configurations for this ingress.
    /// </summary>
    internal List<IngressTlsConfig> TlsConfigs { get; } = [];

    /// <summary>
    /// Gets the Kubernetes metadata annotations to add to the generated Ingress resource.
    /// These are key-value pairs placed in the <c>metadata.annotations</c> field of the K8S Ingress,
    /// not Aspire <see cref="IResourceAnnotation"/> instances.
    /// </summary>
    internal Dictionary<string, ReferenceExpression> IngressAnnotations { get; } = [];

    /// <summary>
    /// Gets or sets the default backend configuration for unmatched requests.
    /// </summary>
    internal IngressDefaultBackendConfig? DefaultBackend { get; set; }

    /// <summary>
    /// Gets the generated K8S Ingress object, populated during infrastructure processing.
    /// </summary>
    internal Resources.Ingress? GeneratedIngress { get; set; }
}

/// <summary>
/// Specifies the type of path matching used in a Kubernetes Ingress rule.
/// </summary>
public enum IngressPathType
{
    /// <summary>
    /// Matches based on a URL path prefix split by <c>/</c>. Matching is case-sensitive
    /// and done element-by-element. For example, <c>/api</c> matches <c>/api</c>, <c>/api/</c>,
    /// and <c>/api/v1</c> but not <c>/apiv1</c>.
    /// </summary>
    Prefix,

    /// <summary>
    /// Matches the URL path exactly and with case sensitivity.
    /// </summary>
    Exact,

    /// <summary>
    /// Matching is delegated to the ingress controller. Check the controller's documentation
    /// for the supported matching semantics.
    /// </summary>
    ImplementationSpecific
}

/// <summary>
/// Stores a single routing rule for a <see cref="KubernetesIngressResource"/>.
/// </summary>
internal sealed record IngressRouteConfig(
    string? Host,
    string Path,
    IngressPathType PathType,
    EndpointReference Endpoint);

/// <summary>
/// Stores TLS configuration for a <see cref="KubernetesIngressResource"/>.
/// </summary>
internal sealed record IngressTlsConfig(
    ReferenceExpression SecretName,
    List<ReferenceExpression> Hosts);

/// <summary>
/// Stores the default backend configuration for a <see cref="KubernetesIngressResource"/>.
/// </summary>
internal sealed record IngressDefaultBackendConfig(EndpointReference Endpoint);
