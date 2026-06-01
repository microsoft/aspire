// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring Kubernetes Ingress resources in the Aspire application model.
/// </summary>
public static class KubernetesIngressExtensions
{
    /// <summary>
    /// Adds a Kubernetes Ingress resource to the application model as a child of the specified
    /// Kubernetes environment. The ingress generates a <c>networking.k8s.io/v1 Ingress</c> resource
    /// in the Helm chart output at publish time.
    /// </summary>
    /// <param name="builder">The Kubernetes environment resource builder.</param>
    /// <param name="name">The name of the ingress resource. This is used as the Kubernetes resource name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// After creating the ingress, configure path-based rules using
    /// <see cref="WithPath(IResourceBuilder{KubernetesIngressResource}, string, EndpointReference, IngressPathType)"/>
    /// and optionally set an ingress class with <see cref="WithIngressClass(IResourceBuilder{KubernetesIngressResource}, string)"/>.
    /// </para>
    /// </remarks>
    /// <ats-remarks />
    /// <example>
    /// <code>
    /// var k8s = builder.AddKubernetesEnvironment("k8s");
    /// var ingress = k8s.AddIngress("public")
    ///     .WithIngressClass("nginx");
    ///
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// ingress.WithPath("/api", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesIngressResource> AddIngress(
        this IResourceBuilder<KubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var ingress = new KubernetesIngressResource(name, builder.Resource);

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder.ApplicationBuilder.CreateResourceBuilder(ingress);
        }

        return builder.ApplicationBuilder.AddResource(ingress)
            .ExcludeFromManifest();
    }

    /// <summary>
    /// Sets the Kubernetes ingress class name that selects which ingress controller
    /// handles this ingress resource.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="className">The ingress class name (e.g., <c>"nginx"</c>, <c>"traefik"</c>,
    /// <c>"azure-alb-external"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesIngressResource> WithIngressClass(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string className)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(className);

        builder.Resource.IngressClassName = ReferenceExpression.Create($"{className}");
        return builder;
    }

    /// <summary>
    /// Sets the Kubernetes ingress class name using a parameter that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="className">A parameter resource builder for the ingress class name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withIngressClassParam")]
    public static IResourceBuilder<KubernetesIngressResource> WithIngressClass(
        this IResourceBuilder<KubernetesIngressResource> builder,
        IResourceBuilder<ParameterResource> className)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(className);

        builder.Resource.IngressClassName = ReferenceExpression.Create($"{className.Resource}");
        return builder;
    }

    /// <summary>
    /// Adds a path-based rule to the ingress. The rule matches all hosts and forwards
    /// traffic matching the specified path to the given endpoint's backing Kubernetes service.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="path">The URL path to match (e.g., <c>"/"</c> or <c>"/api"</c>). Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="IngressPathType.Prefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <example>
    /// <code>
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// ingress.WithPath("/api", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport("withIngressPath")]
    public static IResourceBuilder<KubernetesIngressResource> WithPath(
        this IResourceBuilder<KubernetesIngressResource> builder,
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

        builder.Resource.Paths.Add(new IngressPathConfig(
            Host: null,
            Path: path,
            PathType: pathType,
            Endpoint: endpoint));

        return builder;
    }

    /// <summary>
    /// Adds a host-scoped path rule to the ingress. The rule matches traffic for the
    /// specified host and path, forwarding it to the given endpoint's backing Kubernetes service.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="host">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <param name="path">The URL path to match (e.g., <c>"/"</c> or <c>"/api"</c>). Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="IngressPathType.Prefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <example>
    /// <code>
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// ingress.WithPath("api.example.com", "/", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport("withIngressHostAndPath")]
    public static IResourceBuilder<KubernetesIngressResource> WithPath(
        this IResourceBuilder<KubernetesIngressResource> builder,
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

        builder.Resource.Paths.Add(new IngressPathConfig(
            Host: host,
            Path: path,
            PathType: pathType,
            Endpoint: endpoint));

        return builder;
    }

    /// <summary>
    /// Adds a hostname that this ingress matches. Multiple hostnames can be added by calling
    /// this method repeatedly. If no hostnames are configured, the ingress matches all hosts.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="hostname">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <example>
    /// <code>
    /// ingress.WithHostname("api.example.com")
    ///        .WithHostname("www.example.com");
    /// </code>
    /// </example>
    [AspireExport("withIngressHostname", MethodName = "withHostname")]
    public static IResourceBuilder<KubernetesIngressResource> WithHostname(
        this IResourceBuilder<KubernetesIngressResource> builder,
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
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="hostname">A parameter resource builder for the hostname value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withIngressHostnameParam")]
    public static IResourceBuilder<KubernetesIngressResource> WithHostname(
        this IResourceBuilder<KubernetesIngressResource> builder,
        IResourceBuilder<ParameterResource> hostname)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(hostname);

        builder.Resource.Hostnames.Add(ReferenceExpression.Create($"{hostname.Resource}"));
        return builder;
    }

    /// <summary>
    /// Configures TLS termination for the ingress by referencing a Kubernetes TLS secret.
    /// The TLS configuration applies to all hostnames configured via <see cref="WithHostname(IResourceBuilder{KubernetesIngressResource}, string)"/>.
    /// </summary>
    /// <ats-summary>Configures TLS for a Kubernetes Ingress using a K8S secret</ats-summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="secretName">The name of the Kubernetes <c>kubernetes.io/tls</c> Secret.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withIngressTls", MethodName = "withTls")]
    public static IResourceBuilder<KubernetesIngressResource> WithTls(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(secretName);

        builder.Resource.TlsConfigs.Add(new IngressTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName}")));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination using a parameter for the secret name.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="secretName">A parameter resource builder for the secret name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withIngressTlsParam")]
    public static IResourceBuilder<KubernetesIngressResource> WithTls(
        this IResourceBuilder<KubernetesIngressResource> builder,
        IResourceBuilder<ParameterResource> secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(secretName);

        builder.Resource.TlsConfigs.Add(new IngressTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName.Resource}")));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination with an auto-generated secret name derived from the ingress name.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withIngressTlsAuto")]
    public static IResourceBuilder<KubernetesIngressResource> WithTls(
        this IResourceBuilder<KubernetesIngressResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var secretName = $"{builder.Resource.Name}-tls";

        builder.Resource.TlsConfigs.Add(new IngressTlsConfig(
            SecretName: ReferenceExpression.Create($"{secretName}")));

        return builder;
    }

    /// <summary>
    /// Sets the default backend for the ingress. The default backend handles requests that
    /// do not match any of the defined routing rules.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="endpoint">The endpoint reference identifying the default backend service and port.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<KubernetesIngressResource> WithDefaultBackend(
        this IResourceBuilder<KubernetesIngressResource> builder,
        EndpointReference endpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);

        builder.Resource.DefaultBackend = new IngressDefaultBackendConfig(endpoint);
        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation to the generated Ingress resource. These are
    /// key-value pairs in the <c>metadata.annotations</c> field of the K8S Ingress, commonly
    /// used to configure ingress controller-specific behavior.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="key">The annotation key (e.g., <c>"nginx.ingress.kubernetes.io/rewrite-target"</c>).</param>
    /// <param name="value">The annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// This method sets Kubernetes metadata annotations, not Aspire <see cref="ApplicationModel.IResourceAnnotation"/>
    /// instances. Use these for controller-specific features like path rewriting, rate limiting,
    /// CORS configuration, or SSL redirect behavior.
    /// </remarks>
    /// <example>
    /// <code>
    /// ingress.WithIngressAnnotation("nginx.ingress.kubernetes.io/rewrite-target", "/$1");
    /// ingress.WithIngressAnnotation("nginx.ingress.kubernetes.io/ssl-redirect", "true");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesIngressResource> WithIngressAnnotation(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.IngressAnnotations[key] = ReferenceExpression.Create($"{value}");
        return builder;
    }

    /// <summary>
    /// Adds a Kubernetes metadata annotation with a parameter value that will be resolved at deploy time.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="key">The annotation key.</param>
    /// <param name="value">A parameter resource builder for the annotation value.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport("withIngressAnnotationParam")]
    public static IResourceBuilder<KubernetesIngressResource> WithIngressAnnotation(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string key,
        IResourceBuilder<ParameterResource> value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.IngressAnnotations[key] = ReferenceExpression.Create($"{value.Resource}");
        return builder;
    }

    /// <summary>
    /// Converts an <see cref="IngressPathType"/> enum value to the Kubernetes API string representation.
    /// </summary>
    internal static string ToKubernetesString(this IngressPathType pathType)
    {
        return pathType switch
        {
            IngressPathType.Prefix => "Prefix",
            IngressPathType.Exact => "Exact",
            IngressPathType.ImplementationSpecific => "ImplementationSpecific",
            _ => throw new ArgumentOutOfRangeException(nameof(pathType), pathType, "Unknown path type.")
        };
    }
}
