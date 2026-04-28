// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

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
    /// <remarks>
    /// <para>
    /// After creating the ingress, configure routes using
    /// <see cref="WithRoute(IResourceBuilder{KubernetesIngressResource}, string, EndpointReference, IngressPathType)"/>
    /// and optionally set an ingress class with <see cref="WithIngressClass"/>.
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
    [AspireExport(Description = "Adds a Kubernetes Ingress resource for HTTP routing")]
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
    [AspireExport(Description = "Sets the ingress class for a Kubernetes Ingress")]
    public static IResourceBuilder<KubernetesIngressResource> WithIngressClass(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string className)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(className);

        builder.Resource.IngressClassName = className;
        return builder;
    }

    /// <summary>
    /// Adds a path-based routing rule to the ingress. The rule matches all hosts and routes
    /// traffic matching the specified path to the given endpoint's backing Kubernetes service.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="path">The URL path to match (e.g., <c>"/"</c> or <c>"/api"</c>). Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="IngressPathType.Prefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// ingress.WithRoute("/api", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport("withIngressPathRoute", Description = "Adds a path-based route to a Kubernetes Ingress")]
    public static IResourceBuilder<KubernetesIngressResource> WithRoute(
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

        builder.Resource.Routes.Add(new IngressRouteConfig(
            Host: null,
            Path: path,
            PathType: pathType,
            Endpoint: endpoint));

        return builder;
    }

    /// <summary>
    /// Adds a host-and-path-based routing rule to the ingress. The rule matches traffic for
    /// the specified host and path, routing it to the given endpoint's backing Kubernetes service.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="host">The hostname to match (e.g., <c>"api.example.com"</c>).</param>
    /// <param name="path">The URL path to match (e.g., <c>"/"</c> or <c>"/api"</c>). Must start with <c>/</c>.</param>
    /// <param name="endpoint">The endpoint reference identifying the target service and port.</param>
    /// <param name="pathType">The path matching strategy. Defaults to <see cref="IngressPathType.Prefix"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// ingress.WithRoute("api.example.com", "/", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport("withIngressHostRoute", Description = "Adds a host-and-path route to a Kubernetes Ingress")]
    public static IResourceBuilder<KubernetesIngressResource> WithRoute(
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

        builder.Resource.Routes.Add(new IngressRouteConfig(
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
    /// <example>
    /// <code>
    /// ingress.WithHostname("api.example.com")
    ///        .WithHostname("www.example.com");
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a hostname to a Kubernetes Ingress")]
    public static IResourceBuilder<KubernetesIngressResource> WithHostname(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string hostname)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(hostname);

        builder.Resource.Hostnames.Add(hostname);
        return builder;
    }

    /// <summary>
    /// Configures TLS termination for the ingress by referencing a Kubernetes TLS secret.
    /// The secret must contain <c>tls.crt</c> and <c>tls.key</c> entries and exist in the
    /// same namespace as the ingress. The TLS configuration applies to all hostnames
    /// configured via <see cref="WithHostname"/>.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="secretName">The name of the Kubernetes <c>kubernetes.io/tls</c> Secret containing
    /// the TLS certificate and private key.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// ingress.WithHostname("api.example.com")
    ///        .WithTls("my-tls-secret");
    /// </code>
    /// </example>
    [AspireExport(Description = "Configures TLS for a Kubernetes Ingress using a K8S secret")]
    public static IResourceBuilder<KubernetesIngressResource> WithTls(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(secretName);

        builder.Resource.TlsConfigs.Add(new IngressTlsConfig(
            SecretName: secretName,
            Hosts: [.. builder.Resource.Hostnames]));

        return builder;
    }

    /// <summary>
    /// Configures TLS termination for the ingress with an auto-generated secret name
    /// derived from the ingress resource name. The TLS configuration applies to all
    /// hostnames configured via <see cref="WithHostname"/>.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    [AspireExport("withIngressTlsAuto", Description = "Configures TLS for a Kubernetes Ingress with an auto-generated secret")]
    public static IResourceBuilder<KubernetesIngressResource> WithTls(
        this IResourceBuilder<KubernetesIngressResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var secretName = $"{builder.Resource.Name}-tls";

        builder.Resource.TlsConfigs.Add(new IngressTlsConfig(
            SecretName: secretName,
            Hosts: [.. builder.Resource.Hostnames]));

        return builder;
    }

    /// <summary>
    /// Sets the default backend for the ingress. The default backend handles requests that
    /// do not match any of the defined routing rules.
    /// </summary>
    /// <param name="builder">The ingress resource builder.</param>
    /// <param name="endpoint">The endpoint reference identifying the default backend service and port.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    [AspireExport(Description = "Sets the default backend for a Kubernetes Ingress")]
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
    [AspireExport(Description = "Adds a Kubernetes metadata annotation to a Kubernetes Ingress")]
    public static IResourceBuilder<KubernetesIngressResource> WithIngressAnnotation(
        this IResourceBuilder<KubernetesIngressResource> builder,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.IngressAnnotations[key] = value;
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
