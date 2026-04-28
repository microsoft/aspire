// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides extension methods for configuring Kubernetes Gateway API resources in the Aspire application model.
/// </summary>
public static class KubernetesGatewayExtensions
{
    /// <summary>
    /// Adds a Kubernetes Gateway API Gateway resource to the application model as a child of the specified
    /// Kubernetes environment. The gateway generates a <c>gateway.networking.k8s.io/v1 Gateway</c> resource
    /// and one or more <c>HTTPRoute</c> resources in the Helm chart output at publish time.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the gateway resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
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
    [AspireExport(Description = "Adds a Kubernetes Gateway API Gateway resource")]
    public static IResourceBuilder<KubernetesGatewayResource> AddGateway(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var gateway = new KubernetesGatewayResource(name, builder.Resource);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(gateway);
        }

        return builder.ApplicationBuilder.AddResource(gateway)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Sets the GatewayClass name that selects which controller implementation handles this gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="className">The GatewayClass name (e.g., <c>"azure-alb-external"</c>, <c>"istio"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport(Description = "Sets the GatewayClass for a Kubernetes Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayClass(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string className)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(className);

        builder.Resource.GatewayClassName = className;
        return builder;
    }

    /// <summary>
    /// Adds a path-based routing rule to the gateway. The rule matches all hosts and routes
    /// traffic matching the specified path to the given endpoint's backing Kubernetes service.
    /// This generates an <c>HTTPRoute</c> resource attached to the Gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="path">The URL path to match (e.g., <c>"/"</c> or <c>"/api"</c>). Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="IngressPathType.Prefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayPathRoute", Description = "Adds a path-based route to a Kubernetes Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string path,
        EndpointReference endpoint,
        IngressPathType pathType = IngressPathType.Prefix)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Path must start with '/'.", nameof(path));
        }

        builder.Resource.Routes.Add(new GatewayRouteConfig(
            Host: null,
            Path: path,
            PathType: pathType,
            Endpoint: endpoint));

        return builder;
    }

    /// <summary>
    /// Adds a host-and-path-based routing rule to the gateway. The rule matches traffic for
    /// the specified host and path, routing it to the given endpoint's backing Kubernetes service.
    /// This generates an <c>HTTPRoute</c> resource with a <c>hostnames</c> filter.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="host">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <param name="path">The URL path to match. Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="IngressPathType.Prefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayHostRoute", Description = "Adds a host-and-path route to a Kubernetes Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string host,
        string path,
        EndpointReference endpoint,
        IngressPathType pathType = IngressPathType.Prefix)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!path.StartsWith('/'))
        {
            throw new ArgumentException("Path must start with '/'.", nameof(path));
        }

        builder.Resource.Routes.Add(new GatewayRouteConfig(
            Host: host,
            Path: path,
            PathType: pathType,
            Endpoint: endpoint));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination on the gateway by adding an HTTPS listener that references
    /// a Kubernetes TLS secret. The Gateway terminates TLS and forwards plain HTTP to backends.
    /// This does not create a separate route ΓÇö existing HTTPRoutes serve both HTTP and HTTPS.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="secretName">The name of the Kubernetes <c>kubernetes.io/tls</c> Secret.</param>
    /// <param name="hosts">One or more hostnames that the TLS certificate covers.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport(Description = "Configures TLS on a Kubernetes Gateway listener")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string secretName,
        params string[] hosts)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(secretName);
        ArgumentNullException.ThrowIfNull(hosts);

        if (hosts.Length == 0)
        {
            throw new ArgumentException("At least one host must be specified for TLS.", nameof(hosts));
        }

        builder.Resource.TlsConfigs.Add(new GatewayTlsConfig(
            SecretName: secretName,
            Hosts: [.. hosts]));

        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation to the generated Gateway resource. These are
    /// key-value pairs in the <c>metadata.annotations</c> field of the K8S Gateway, commonly
    /// used for controller-specific configuration (e.g., AGC's <c>alb.networking.azure.io/alb-name</c>).
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <remarks>
    /// This method sets Kubernetes metadata annotations, not Aspire <see cref="ApplicationModel.IResourceAnnotation"/>
    /// instances. For Azure Application Gateway for Containers (AGC), you typically need:
    /// <c>alb.networking.azure.io/alb-name</c> and <c>alb.networking.azure.io/alb-namespace</c>.
    /// </remarks>
    [AspireExport(Description = "Adds a Kubernetes metadata annotation to a Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayAnnotation(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.GatewayAnnotations[key] = value;
        return builder;
    }
}
