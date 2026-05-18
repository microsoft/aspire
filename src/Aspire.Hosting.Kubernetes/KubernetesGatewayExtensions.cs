// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

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

        builder.Resource.GatewayClassName = ReferenceExpression.Create($"{className}");
        return builder;
    }

    /// <summary>
    /// Sets the GatewayClass name using a parameter that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="className">A parameter resource builder for the GatewayClass name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayClassParam", Description = "Sets a parameterized GatewayClass for a Kubernetes Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayClass(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> className)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(className);

        builder.Resource.GatewayClassName = ReferenceExpression.Create($"{className.Resource}");
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
    /// Adds a hostname that this gateway's routes match. Multiple hostnames can be added by calling
    /// this method repeatedly. Hostnames are used as <c>hostnames</c> in generated <c>HTTPRoute</c>
    /// resources and as HTTPS listener hostnames when TLS is configured.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="hostname">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayHostname", MethodName = "withHostname", Description = "Adds a hostname to a Kubernetes Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithHostname(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string hostname)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        builder.Resource.Hostnames.Add(ReferenceExpression.Create($"{hostname}"));
        return builder;
    }

    /// <summary>
    /// Adds a hostname using a parameter that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="hostname">A parameter resource builder for the hostname value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayHostnameParam", Description = "Adds a parameterized hostname to a Kubernetes Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithHostname(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> hostname)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hostname);

        builder.Resource.Hostnames.Add(ReferenceExpression.Create($"{hostname.Resource}"));
        return builder;
    }

    /// <summary>
    /// Configures TLS termination on the gateway by adding an HTTPS listener that references
    /// a Kubernetes TLS secret. The Gateway terminates TLS and forwards plain HTTP to backends.
    /// By default this also emits a <c>301</c> HTTP→HTTPS redirect on the gateway's HTTP listener
    /// and HSTS (<c>Strict-Transport-Security: max-age=31536000</c>) on HTTPS responses; both
    /// behaviors can be tuned via <paramref name="configure"/>.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="secretName">The name of the Kubernetes <c>kubernetes.io/tls</c> Secret.</param>
    /// <param name="configure">Optional callback to customize the TLS posture (HTTP→HTTPS redirect, HSTS).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <remarks>
    /// The TLS configuration applies to all hostnames configured via <see cref="WithHostname(IResourceBuilder{KubernetesGatewayResource}, string)"/>.
    /// Port 80 on a TLS'd gateway exists for ACME HTTP-01 challenges; the cert-manager solver's
    /// HTTPRoute uses <c>path.type: Exact</c> and therefore wins over the redirect's
    /// <c>PathPrefix: /</c> per Gateway API route-precedence rules, so HTTP-01 validation
    /// continues to work with the redirect in place.
    /// </remarks>
    [AspireExport("withGatewayTls", MethodName = "withTls", Description = "Configures TLS on a Kubernetes Gateway listener")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string secretName,
        Action<TlsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(secretName);

        return builder.AddTlsConfig(ReferenceExpression.Create($"{secretName}"), configure);
    }

    /// <summary>
    /// Configures TLS termination using a parameter for the secret name.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="secretName">A parameter resource builder for the secret name.</param>
    /// <param name="configure">Optional callback to customize the TLS posture (HTTP→HTTPS redirect, HSTS).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayTlsParam", Description = "Configures TLS on a Kubernetes Gateway with a parameterized secret")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> secretName,
        Action<TlsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(secretName);

        return builder.AddTlsConfig(ReferenceExpression.Create($"{secretName.Resource}"), configure);
    }

    /// <summary>
    /// Configures TLS termination with an auto-generated secret name derived from the gateway name.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="configure">Optional callback to customize the TLS posture (HTTP→HTTPS redirect, HSTS).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayTlsAuto", Description = "Configures TLS on a Kubernetes Gateway with an auto-generated secret")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        Action<TlsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var secretName = $"{builder.Resource.Name}-tls";

        return builder.AddTlsConfig(ReferenceExpression.Create($"{secretName}"), configure);
    }

    /// <summary>
    /// Shared implementation: builds and registers a <see cref="GatewayTlsConfig"/> from a
    /// resolved secret-name expression and an optional TLS-options callback.
    /// </summary>
    private static IResourceBuilder<KubernetesGatewayResource> AddTlsConfig(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        ReferenceExpression secretName,
        Action<TlsOptions>? configure)
    {
        var options = new TlsOptions();
        configure?.Invoke(options);

        builder.Resource.TlsConfigs.Add(GatewayTlsConfig.FromOptions(secretName, options));

        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation to the generated Gateway resource.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This sets Kubernetes <c>metadata.annotations</c> on the generated K8S Gateway resource,
    /// not Aspire <see cref="ApplicationModel.IResourceAnnotation"/> instances. These are key-value
    /// string pairs used by ingress controllers for provider-specific configuration.
    /// </para>
    /// <para>
    /// For Azure Application Gateway for Containers (AGC), you typically need:
    /// <c>alb.networking.azure.io/alb-name</c> and <c>alb.networking.azure.io/alb-namespace</c>.
    /// </para>
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

        builder.Resource.GatewayAnnotations[key] = ReferenceExpression.Create($"{value}");
        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation with a parameter value that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">A parameter resource builder for the annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    [AspireExport("withGatewayAnnotationParam", Description = "Adds a parameterized Kubernetes metadata annotation to a Gateway")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGatewayAnnotation(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string key,
        IResourceBuilder<ParameterResource> value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.GatewayAnnotations[key] = ReferenceExpression.Create($"{value.Resource}");
        return builder;
    }
}
