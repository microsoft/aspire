// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Authentication;
using Aspire.Hosting.ApplicationModel;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Aspire.Hosting.Yarp;

/// <summary>
/// Represents a cluster for YARP routes
/// </summary>
[AspireExport]
public class YarpCluster
{
    // Testing only
    internal YarpCluster(ClusterConfig config, params object[] targets)
    {
        ClusterConfig = config;
        Targets = targets;
    }
    /// <summary>
    /// Construct a new YarpCluster targeting the endpoint in parameter.
    /// </summary>
    /// <param name="endpoint">The endpoint to target.</param>
    internal YarpCluster(EndpointReference endpoint)
        : this(endpoint.Resource.Name, $"{endpoint.Scheme}://_{endpoint.EndpointName}.{endpoint.Resource.Name}")
    {
    }

    /// <summary>
    /// Construct a new YarpCluster targeting the resource in parameter.
    /// </summary>
    /// <param name="resource">The resource to target.</param>
    internal YarpCluster(IResourceWithServiceDiscovery resource)
        : this(resource.Name, BuildEndpointUri(resource))
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="YarpCluster"/> with a specified external service resource.
    /// </summary>
    /// <param name="externalService">The external service.</param>
    internal YarpCluster(ExternalServiceResource externalService)
        : this(externalService.Name, GetAddressFromExternalService(externalService))
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="YarpCluster"/> with a specified list of addresses.
    /// </summary>
    /// <param name="resourceName">The name of the resource.</param>
    /// <param name="targets">The target objects for the cluster (e.g., addresses, URIs, or other endpoint representations).</param>
    internal YarpCluster(string resourceName, params object[] targets)
    {
        ClusterConfig = new()
        {
            ClusterId = $"cluster_{resourceName}",
            Destinations = new Dictionary<string, DestinationConfig>(StringComparer.OrdinalIgnoreCase)
        };
        Targets = targets;
    }

    internal ClusterConfig ClusterConfig { get; private set; }

    internal object[] Targets { get; private set; }

    internal void Configure(Func<ClusterConfig, ClusterConfig> configure)
    {
        ClusterConfig = configure(ClusterConfig);
    }

    private static object BuildEndpointUri(IResourceWithServiceDiscovery resource)
    {
        var resourceName = resource.Name;

        var endpoints = resource.GetEndpoints();
        var hasHttpsEndpoint = endpoints.Any(e => e.Exists && e.IsHttps);
        var hasHttpEndpoint = endpoints.Any(e => e.Exists && e.IsHttp);

        var scheme = (hasHttpsEndpoint, hasHttpEndpoint) switch
        {
            (true, true) => "https+http",
            (true, false) => "https",
            (false, true) => "http",
            _ => throw new ArgumentException("Cannot find a http or https endpoint for this resource.", nameof(resource))
        };

        return $"{scheme}://{resourceName}";
    }

    private static object GetAddressFromExternalService(ExternalServiceResource externalService)
    {
        if (externalService.Uri is not null)
        {
            return externalService.Uri.ToString();
        }
        if (externalService.UrlParameter is not null)
        {
            return externalService.UrlParameter;
        }
        // This shouldn't get to here as the ExternalServiceResource should ensure the URL is a valid absolute URI.
        throw new InvalidOperationException("External service must have either a URI or a URL parameter defined.");
    }
}

/// <summary>
/// Provides extension methods for configuring a YARP cluster.
/// </summary>
public static class YarpClusterExtensions
{
    /// <summary>
    /// Set the ForwarderRequestConfig for the cluster.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the DTO-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "ForwarderRequestConfig is not ATS-compatible. Use the DTO-based overload instead.")]
    public static YarpCluster WithForwarderRequestConfig(this YarpCluster cluster, ForwarderRequestConfig config)
    {
        cluster.Configure(c => c with { HttpRequest = config });
        return cluster;
    }

    /// <summary>
    /// Set the forwarder request configuration for the cluster.
    /// </summary>
    [AspireExport("withForwarderRequestConfig", Description = "Sets the forwarder request configuration for the cluster.")]
    internal static YarpCluster WithForwarderRequestConfig(this YarpCluster cluster, YarpForwarderRequestConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        cluster.Configure(c => c with { HttpRequest = ToForwarderRequestConfig(config) });
        return cluster;
    }

    /// <summary>
    /// Set the ForwarderRequestConfig for the cluster.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the DTO-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "HttpClientConfig is not ATS-compatible. Use the DTO-based overload instead.")]
    public static YarpCluster WithHttpClientConfig(this YarpCluster cluster, HttpClientConfig config)
    {
        cluster.Configure(c => c with { HttpClient = config });
        return cluster;
    }

    /// <summary>
    /// Set the HTTP client configuration for the cluster.
    /// </summary>
    [AspireExport("withHttpClientConfig", Description = "Sets the HTTP client configuration for the cluster.")]
    internal static YarpCluster WithHttpClientConfig(this YarpCluster cluster, YarpHttpClientConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        cluster.Configure(c => c with { HttpClient = ToHttpClientConfig(config) });
        return cluster;
    }

    /// <summary>
    /// Set the SessionAffinityConfig for the cluster.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the DTO-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "SessionAffinityConfig is not ATS-compatible. Use the DTO-based overload instead.")]
    public static YarpCluster WithSessionAffinityConfig(this YarpCluster cluster, SessionAffinityConfig config)
    {
        cluster.Configure(c => c with { SessionAffinity = config });
        return cluster;
    }

    /// <summary>
    /// Set the session affinity configuration for the cluster.
    /// </summary>
    [AspireExport("withSessionAffinityConfig", Description = "Sets the session affinity configuration for the cluster.")]
    internal static YarpCluster WithSessionAffinityConfig(this YarpCluster cluster, YarpSessionAffinityConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        cluster.Configure(c => c with { SessionAffinity = ToSessionAffinityConfig(config) });
        return cluster;
    }

    /// <summary>
    /// Set the HealthCheckConfig for the cluster.
    /// </summary>
    /// <remarks>This overload is not available in polyglot app hosts. Use the DTO-based overload instead.</remarks>
    [AspireExportIgnore(Reason = "HealthCheckConfig is not ATS-compatible. Use the DTO-based overload instead.")]
    public static YarpCluster WithHealthCheckConfig(this YarpCluster cluster, HealthCheckConfig config)
    {
        cluster.Configure(c => c with { HealthCheck = config });
        return cluster;
    }

    /// <summary>
    /// Set the health check configuration for the cluster.
    /// </summary>
    [AspireExport("withHealthCheckConfig", Description = "Sets the health check configuration for the cluster.")]
    internal static YarpCluster WithHealthCheckConfig(this YarpCluster cluster, YarpHealthCheckConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        cluster.Configure(c => c with { HealthCheck = ToHealthCheckConfig(config) });
        return cluster;
    }

    /// <summary>
    /// Set the LoadBalancingPolicy for the cluster.
    /// </summary>
    [AspireExport("withLoadBalancingPolicy", Description = "Sets the load balancing policy for the cluster.")]
    public static YarpCluster WithLoadBalancingPolicy(this YarpCluster cluster, string policy)
    {
        cluster.Configure(c => c with { LoadBalancingPolicy = policy });
        return cluster;
    }

    /// <summary>
    /// Set the Metadata for the cluster.
    /// </summary>
    [AspireExport("withClusterMetadata", MethodName = "withMetadata", Description = "Sets metadata for the cluster.")]
    public static YarpCluster WithMetadata(this YarpCluster cluster, IReadOnlyDictionary<string, string> metadata)
    {
        cluster.Configure(c => c with { Metadata = metadata });
        return cluster;
    }

    // These mappings keep the existing public YARP-config overloads for .NET callers while exposing
    // ATS-friendly DTO shapes for polyglot callers. The raw YARP types include nested config objects,
    // Version values, and flags enums that do not round-trip cleanly through ATS as-is.
    private static ForwarderRequestConfig ToForwarderRequestConfig(YarpForwarderRequestConfig config)
    {
        Version? parsedVersion = null;

        if (!string.IsNullOrWhiteSpace(config.Version))
        {
            if (!Version.TryParse(config.Version, out var version))
            {
                throw new ArgumentException(
                    $"The value '{config.Version}' is not a valid HTTP version. Expected format is 'major.minor', for example '1.1' or '2.0'.",
                    nameof(config));
            }

            parsedVersion = version;
        }

        return new ForwarderRequestConfig
        {
            ActivityTimeout = config.ActivityTimeout,
            AllowResponseBuffering = config.AllowResponseBuffering,
            Version = parsedVersion,
            VersionPolicy = config.VersionPolicy,
        };
    }

    private static HttpClientConfig ToHttpClientConfig(YarpHttpClientConfig config)
    {
        return new HttpClientConfig
        {
            DangerousAcceptAnyServerCertificate = config.DangerousAcceptAnyServerCertificate,
            EnableMultipleHttp2Connections = config.EnableMultipleHttp2Connections,
            MaxConnectionsPerServer = config.MaxConnectionsPerServer,
            RequestHeaderEncoding = config.RequestHeaderEncoding,
            ResponseHeaderEncoding = config.ResponseHeaderEncoding,
            SslProtocols = MapSslProtocols(config.SslProtocols),
            WebProxy = config.WebProxy is null ? null : ToWebProxyConfig(config.WebProxy),
        };
    }

    private static WebProxyConfig ToWebProxyConfig(YarpWebProxyConfig config)
    {
        return new WebProxyConfig
        {
            Address = config.Address,
            BypassOnLocal = config.BypassOnLocal,
            UseDefaultCredentials = config.UseDefaultCredentials,
        };
    }

    private static SessionAffinityConfig ToSessionAffinityConfig(YarpSessionAffinityConfig config)
    {
        return new SessionAffinityConfig
        {
            AffinityKeyName = config.AffinityKeyName ?? string.Empty,
            Cookie = config.Cookie is null ? null : ToSessionAffinityCookieConfig(config.Cookie),
            Enabled = config.Enabled,
            FailurePolicy = config.FailurePolicy,
            Policy = config.Policy,
        };
    }

    private static SessionAffinityCookieConfig ToSessionAffinityCookieConfig(YarpSessionAffinityCookieConfig config)
    {
        return new SessionAffinityCookieConfig
        {
            Domain = config.Domain,
            Expiration = config.Expiration,
            HttpOnly = config.HttpOnly,
            IsEssential = config.IsEssential,
            MaxAge = config.MaxAge,
            Path = config.Path,
            SameSite = config.SameSite,
            SecurePolicy = config.SecurePolicy,
        };
    }

    private static HealthCheckConfig ToHealthCheckConfig(YarpHealthCheckConfig config)
    {
        return new HealthCheckConfig
        {
            Active = config.Active is null ? null : ToActiveHealthCheckConfig(config.Active),
            AvailableDestinationsPolicy = config.AvailableDestinationsPolicy,
            Passive = config.Passive is null ? null : ToPassiveHealthCheckConfig(config.Passive),
        };
    }

    private static ActiveHealthCheckConfig ToActiveHealthCheckConfig(YarpActiveHealthCheckConfig config)
    {
        return new ActiveHealthCheckConfig
        {
            Enabled = config.Enabled,
            Interval = config.Interval,
            Path = config.Path,
            Policy = config.Policy,
            Query = config.Query,
            Timeout = config.Timeout,
        };
    }

    private static PassiveHealthCheckConfig ToPassiveHealthCheckConfig(YarpPassiveHealthCheckConfig config)
    {
        return new PassiveHealthCheckConfig
        {
            Enabled = config.Enabled,
            Policy = config.Policy,
            ReactivationPeriod = config.ReactivationPeriod,
        };
    }

    private static SslProtocols? MapSslProtocols(IReadOnlyList<YarpSslProtocol>? protocols)
    {
        if (protocols is null)
        {
            return null;
        }

        var result = SslProtocols.None;
        foreach (var protocol in protocols)
        {
            result |= protocol switch
            {
                YarpSslProtocol.None => SslProtocols.None,
                YarpSslProtocol.Tls12 => SslProtocols.Tls12,
                YarpSslProtocol.Tls13 => SslProtocols.Tls13,
                _ => throw new ArgumentOutOfRangeException(nameof(protocols), protocol, $"Unsupported {nameof(YarpSslProtocol)} value."),
            };
        }

        return result;
    }
}
