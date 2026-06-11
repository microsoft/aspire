// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting;

/// <summary>
/// Resolves OTLP endpoint configuration (URL, scheme, port, and protocol) from configuration.
/// </summary>
internal static class OtlpEndpointResolver
{
    private const int DashboardOtlpUrlDefaultPort = 18889;
    private static readonly string s_dashboardOtlpUrlDefaultValue = $"http://localhost:{DashboardOtlpUrlDefaultPort}";

    /// <summary>
    /// Resolves the OTLP endpoint URL and protocol from configuration.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="requiredProtocol">The required protocol, or null to use preference logic (gRPC preferred over HTTP).</param>
    /// <returns>A tuple containing the endpoint URL and protocol string ("grpc" or "http/protobuf").</returns>
    /// <exception cref="InvalidOperationException">Thrown when <paramref name="requiredProtocol"/> requires HTTP but no HTTP endpoint is configured.</exception>
    public static (string Url, string Protocol) ResolveOtlpEndpoint(IConfiguration configuration, OtlpProtocol? requiredProtocol = null)
    {
        var dashboardOtlpGrpcUrl = configuration.GetString(KnownConfigNames.DashboardOtlpGrpcEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpGrpcEndpointUrl);
        var dashboardOtlpHttpUrl = configuration.GetString(KnownConfigNames.DashboardOtlpHttpEndpointUrl, KnownConfigNames.Legacy.DashboardOtlpHttpEndpointUrl);

        // Check if a specific protocol is required
        if (requiredProtocol is OtlpProtocol.Grpc)
        {
            return (dashboardOtlpGrpcUrl ?? s_dashboardOtlpUrlDefaultValue, "grpc");
        }
        else if (requiredProtocol is OtlpProtocol.HttpProtobuf)
        {
            if (dashboardOtlpHttpUrl is null)
            {
                throw new InvalidOperationException("OtlpExporter is configured to require http/protobuf, but no endpoint was configured for ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL");
            }
            return (dashboardOtlpHttpUrl, "http/protobuf");
        }
        else if (requiredProtocol is OtlpProtocol.HttpJson)
        {
            if (dashboardOtlpHttpUrl is null)
            {
                throw new InvalidOperationException("OtlpExporter is configured to require http/json, but no endpoint was configured for ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL");
            }
            return (dashboardOtlpHttpUrl, "http/json");
        }
        else
        {
            // No specific protocol required, use the existing preference logic
            // The dashboard can support OTLP/gRPC and OTLP/HTTP endpoints at the same time, but it can
            // only tell resources about one of the endpoints via environment variables.
            // If both OTLP/gRPC and OTLP/HTTP are available then prefer gRPC.
            if (dashboardOtlpGrpcUrl is not null)
            {
                return (dashboardOtlpGrpcUrl, "grpc");
            }
            else if (dashboardOtlpHttpUrl is not null)
            {
                return (dashboardOtlpHttpUrl, "http/protobuf");
            }
            else
            {
                // No endpoints provided to host. Use default value for URL.
                return (s_dashboardOtlpUrlDefaultValue, "grpc");
            }
        }
    }

    /// <summary>
    /// Resolves the OTLP endpoint scheme and port from configuration.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <returns>A tuple of (scheme, port) for the OTLP endpoint.</returns>
    public static (string Scheme, int Port) ResolveSchemeAndPort(IConfiguration configuration)
    {
        var (url, _) = ResolveOtlpEndpoint(configuration);

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return (uri.Scheme, uri.Port);
        }

        // Fallback to default (should not normally reach here as ResolveOtlpEndpoint always returns a valid URL)
        return ("http", DashboardOtlpUrlDefaultPort);
    }
}
