// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring Kubernetes Gateway API resources in the Aspire application model.
/// </summary>
public static partial class KubernetesGatewayExtensions
{
    /// <summary>
    /// Adds a Kubernetes Gateway API Gateway resource to the application model as a child of the specified
    /// Kubernetes environment. The gateway generates a <c>gateway.networking.k8s.io/v1 Gateway</c> resource
    /// and one or more <c>HTTPRoute</c> resources in the Helm chart output at publish time.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the gateway resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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
            .WithIconName("GlobeArrowForward")
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Sets the GatewayClass name that selects which controller implementation handles this gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="className">The GatewayClass name (e.g., <c>"azure-alb-external"</c>, <c>"istio"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayClassParam")]
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
    /// Adds a path-based routing rule to the gateway. The rule matches each hostname configured
    /// with <see cref="WithHostname(IResourceBuilder{KubernetesGatewayResource}, string)"/>, or all
    /// hosts when no hostname is configured, and routes matching traffic to the endpoint's backing
    /// Kubernetes service. This generates an <c>HTTPRoute</c> resource attached to the Gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="path">The URL path to match (e.g., <c>"/"</c> or <c>"/api"</c>). Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="GatewayPathMatchType.PathPrefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayPathRoute")]
    public static IResourceBuilder<KubernetesGatewayResource> WithRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string path,
        EndpointReference endpoint,
        GatewayPathMatchType pathType = GatewayPathMatchType.PathPrefix)
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
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="GatewayPathMatchType.PathPrefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayHostRoute")]
    public static IResourceBuilder<KubernetesGatewayResource> WithRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string host,
        string path,
        EndpointReference endpoint,
        GatewayPathMatchType pathType = GatewayPathMatchType.PathPrefix)
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
    /// Adds a gRPC routing rule to the gateway.
    /// The rule matches each hostname configured with <see cref="WithHostname(IResourceBuilder{KubernetesGatewayResource}, string)"/>, or all hosts when no hostname is configured, and routes matching gRPC requests to the endpoint's backing Kubernetes service.
    /// This generates a <c>GRPCRoute</c> resource attached to the Gateway.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="endpoint">The endpoint reference identifying the target gRPC service and port.</param>
    /// <param name="service">The fully qualified gRPC service name to match. When <see langword="null"/>, any service matches.</param>
    /// <param name="method">The gRPC method name to match. When <see langword="null"/>, any method matches.</param>
    /// <param name="matchType">How <paramref name="service"/> and <paramref name="method"/> are compared. Defaults to <see cref="GrpcMethodMatchType.Exact"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// When neither <paramref name="service"/> nor <paramref name="method"/> is specified, the rule matches every gRPC request.
    /// The Gateway API only guarantees support for exact matches that specify a service, with or without a method.
    /// Method-only matches and <see cref="GrpcMethodMatchType.RegularExpression"/> are implementation-specific.
    /// </para>
    /// <para>
    /// The Gateway API recommends serving gRPC and non-gRPC HTTP traffic on separate hostnames.
    /// Implementations may reject a <c>GRPCRoute</c> whose hostnames intersect with an <c>HTTPRoute</c> attached to the same listener.
    /// Use <see cref="WithGrpcRoute(IResourceBuilder{KubernetesGatewayResource}, string, EndpointReference, string?, string?, GrpcMethodMatchType)"/> to give gRPC routes a dedicated hostname.
    /// See <see href="https://gateway-api.sigs.k8s.io/reference/api-types/grpcroute/#cross-serving"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var gateway = k8s.AddGateway("public").WithGatewayClass("istio");
    /// var grpc = builder.AddProject&lt;GreeterService&gt;("greeter");
    ///
    /// gateway.WithGrpcRoute(grpc.GetEndpoint("http"), service: "greet.Greeter", method: "SayHello");
    /// </code>
    /// </example>
    [AspireExport("withGatewayGrpcRoute")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGrpcRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        EndpointReference endpoint,
        string? service = null,
        string? method = null,
        GrpcMethodMatchType matchType = GrpcMethodMatchType.Exact)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateGrpcMethodMatch(service, method, matchType);

        builder.Resource.GrpcRoutes.Add(new GatewayGrpcRouteConfig(
            Host: null,
            Service: service,
            Method: method,
            MatchType: matchType,
            Endpoint: endpoint));

        return builder;
    }

    /// <summary>
    /// Adds a host-scoped gRPC routing rule to the gateway.
    /// The rule matches gRPC requests for the specified host, routing them to the given endpoint's backing Kubernetes service.
    /// This generates a <c>GRPCRoute</c> resource with a <c>hostnames</c> filter.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="host">The hostname to match.</param>
    /// <param name="endpoint">The endpoint reference identifying the target gRPC service and port.</param>
    /// <param name="service">The fully qualified gRPC service name to match. When <see langword="null"/>, any service matches.</param>
    /// <param name="method">The gRPC method name to match. When <see langword="null"/>, any method matches.</param>
    /// <param name="matchType">How <paramref name="service"/> and <paramref name="method"/> are compared. Defaults to <see cref="GrpcMethodMatchType.Exact"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayHostGrpcRoute")]
    public static IResourceBuilder<KubernetesGatewayResource> WithGrpcRoute(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string host,
        EndpointReference endpoint,
        string? service = null,
        string? method = null,
        GrpcMethodMatchType matchType = GrpcMethodMatchType.Exact)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(host);
        ArgumentNullException.ThrowIfNull(endpoint);
        ValidateGrpcMethodMatch(service, method, matchType);

        builder.Resource.GrpcRoutes.Add(new GatewayGrpcRouteConfig(
            Host: host,
            Service: service,
            Method: method,
            MatchType: matchType,
            Endpoint: endpoint));

        return builder;
    }

    /// <summary>
    /// Adds a hostname that this gateway's routes match. Multiple hostnames can be added by calling
    /// this method repeatedly. Routes without an explicit host apply to each configured hostname.
    /// Hostnames are used as <c>hostnames</c> in generated <c>HTTPRoute</c> resources and as HTTPS
    /// listener hostnames when TLS is configured.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="hostname">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayHostname", MethodName = "withHostname")]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayHostnameParam")]
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
    /// This does not create a separate route — existing HTTPRoutes serve both HTTP and HTTPS.
    /// The TLS configuration applies to all hostnames configured via <see cref="WithHostname(IResourceBuilder{KubernetesGatewayResource}, string)"/>.
    /// </summary>
    /// <ats-summary>Configures TLS on a Kubernetes Gateway listener</ats-summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="secretName">The name of the Kubernetes <c>kubernetes.io/tls</c> Secret.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayTls", MethodName = "withTls")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        string secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(secretName);

        builder.Resource.TlsConfigs.Add(new GatewayTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName}")));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination using a parameter for the secret name.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="secretName">A parameter resource builder for the secret name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayTlsParam")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder,
        IResourceBuilder<ParameterResource> secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(secretName);

        builder.Resource.TlsConfigs.Add(new GatewayTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName.Resource}")));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination with an auto-generated secret name derived from the gateway name.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayTlsAuto")]
    public static IResourceBuilder<KubernetesGatewayResource> WithTls(
        this IResourceBuilder<KubernetesGatewayResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var secretName = $"{builder.Resource.Name}-tls";

        builder.Resource.TlsConfigs.Add(new GatewayTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName}")));

        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation to the generated Gateway resource.
    /// </summary>
    /// <param name="builder">The gateway resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withGatewayAnnotationParam")]
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

    // Mirrors the GRPCMethodMatch CEL validation in the Gateway API CRD so that invalid values fail when the
    // AppHost is authored rather than when the API server rejects the chart. See
    // https://github.com/kubernetes-sigs/gateway-api/blob/main/apis/v1/grpcroute_types.go (GRPCMethodMatch):
    //   - When a method match is present, one or both of 'service' or 'method' must be specified.
    //   - For Exact matches, service must match ^(?i)\.?[a-z_][a-z_0-9]*(\.[a-z_][a-z_0-9]*)*$
    //     (e.g. "com.example.User", never "/com.example.User/Login") and method must match
    //     ^[A-Za-z_][A-Za-z_0-9]*$ (e.g. "Login").
    // RegularExpression values are passed through untouched because the regex dialect is implementation-specific.
    private static void ValidateGrpcMethodMatch(string? service, string? method, GrpcMethodMatchType matchType)
    {
        if (service is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(service);
        }

        if (method is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(method);
        }

        switch (matchType)
        {
            case GrpcMethodMatchType.Exact:
                if (service is not null && !GrpcServiceNameRegex().IsMatch(service))
                {
                    throw new ArgumentException(
                        $"gRPC service '{service}' is not a valid fully qualified service name (e.g., 'com.example.User'). " +
                        "Exact matches must not contain '/' or a method name.",
                        nameof(service));
                }

                if (method is not null && !GrpcMethodNameRegex().IsMatch(method))
                {
                    throw new ArgumentException(
                        $"gRPC method '{method}' is not a valid method name (e.g., 'Login'). Exact matches must not contain '/' or '.'.",
                        nameof(method));
                }
                break;

            case GrpcMethodMatchType.RegularExpression:
                if (service is null && method is null)
                {
                    throw new ArgumentException(
                        $"A {nameof(GrpcMethodMatchType.RegularExpression)} gRPC match requires a service or method pattern.",
                        nameof(matchType));
                }
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(matchType), matchType, "Unknown gRPC method match type.");
        }
    }

    [GeneratedRegex(@"^\.?[a-z_][a-z_0-9]*(\.[a-z_][a-z_0-9]*)*$", RegexOptions.IgnoreCase)]
    private static partial Regex GrpcServiceNameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z_0-9]*$")]
    private static partial Regex GrpcMethodNameRegex();
}
