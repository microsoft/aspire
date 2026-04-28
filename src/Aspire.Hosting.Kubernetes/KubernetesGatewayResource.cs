// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a Kubernetes Gateway API Gateway as a first-class resource in the Aspire application model.
/// A Gateway defines listeners (ports, protocols, TLS) and HTTPRoutes attach to it for routing.
/// </summary>
/// <param name="name">The name of the gateway resource.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
/// <remarks>
/// <para>
/// Create a gateway using <see cref="KubernetesGatewayExtensions.AddGateway"/> and configure
/// routes using <see cref="KubernetesGatewayExtensions.WithRoute(IResourceBuilder{KubernetesGatewayResource}, string, EndpointReference, IngressPathType)"/>.
/// </para>
/// <para>
/// At publish time, the gateway generates a <c>gateway.networking.k8s.io/v1 Gateway</c> resource
/// with auto-inferred listeners and one or more <c>HTTPRoute</c> resources in the Helm chart output.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var k8s = builder.AddKubernetesEnvironment("k8s");
/// var gateway = k8s.AddGateway("public")
///     .WithGatewayClass("azure-alb-external");
///
/// var api = builder.AddProject&lt;MyApi&gt;("api");
/// gateway.WithRoute("/api", api.GetEndpoint("http"));
/// </code>
/// </example>
[AspireExport]
public class KubernetesGatewayResource(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Gets the parent Kubernetes environment resource.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Gets or sets the GatewayClass name that selects which controller implementation
    /// handles this gateway.
    /// </summary>
    /// <remarks>
    /// Common values include <c>"azure-alb-external"</c> (for AKS with AGC),
    /// <c>"istio"</c>, or controller-specific class names.
    /// </remarks>
    public ReferenceExpression? GatewayClassName { get; set; }

    /// <summary>
    /// Gets the list of hostnames this gateway matches.
    /// </summary>
    internal List<ReferenceExpression> Hostnames { get; } = [];

    /// <summary>
    /// Gets the list of routing rules configured for this gateway.
    /// </summary>
    internal List<GatewayRouteConfig> Routes { get; } = [];

    /// <summary>
    /// Gets the list of TLS configurations. Each creates an HTTPS listener on the gateway.
    /// </summary>
    internal List<GatewayTlsConfig> TlsConfigs { get; } = [];

    /// <summary>
    /// Gets the Kubernetes metadata annotations to add to the generated Gateway resource.
    /// </summary>
    internal Dictionary<string, ReferenceExpression> GatewayAnnotations { get; } = [];

    /// <summary>
    /// Gets the generated K8S Gateway object, populated during infrastructure processing.
    /// </summary>
    internal Resources.GatewayV1? GeneratedGateway { get; set; }

    /// <summary>
    /// Gets the generated K8S HTTPRoute objects, populated during infrastructure processing.
    /// </summary>
    internal List<Resources.HttpRouteV1> GeneratedHttpRoutes { get; } = [];
}

/// <summary>
/// Stores a single routing rule for a <see cref="KubernetesGatewayResource"/>.
/// </summary>
internal sealed record GatewayRouteConfig(
    string? Host,
    string Path,
    IngressPathType PathType,
    EndpointReference Endpoint);

/// <summary>
/// Stores TLS configuration for a <see cref="KubernetesGatewayResource"/>.
/// Configures an HTTPS listener on the Gateway with TLS termination.
/// </summary>
internal sealed record GatewayTlsConfig(
    ReferenceExpression SecretName,
    List<ReferenceExpression> Hosts);
