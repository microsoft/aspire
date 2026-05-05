// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a compute environment resource.
/// </summary>
public interface IComputeEnvironmentResource : IResource
{
    /// <summary>
    /// Gets a <see cref="ReferenceExpression"/> representing the host address or host name for the specified <see cref="EndpointReference"/>.
    /// </summary>
    /// <param name="endpointReference">The endpoint reference for which to retrieve the host address or host name.</param>
    /// <returns>A <see cref="ReferenceExpression"/> representing the host address or host name (not a full URL).</returns>
    /// <remarks>
    /// The returned value typically contains only the host name or address, without scheme, port, or path information.
    /// </remarks>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    ReferenceExpression GetHostAddressExpression(EndpointReference endpointReference) => throw new NotImplementedException();

    /// <summary>
    /// Gets a <see cref="ReferenceExpression"/> representing a deployed endpoint property for the specified <see cref="EndpointReferenceExpression"/>.
    /// </summary>
    /// <param name="endpointReferenceExpression">The endpoint reference expression for which to retrieve a deployed endpoint property.</param>
    /// <returns>A <see cref="ReferenceExpression"/> representing the deployed endpoint property.</returns>
    /// <remarks>
    /// This method is used by deployment publishers to resolve endpoint properties from deployed compute infrastructure without
    /// requiring a local endpoint allocation. The default implementation composes values from <see cref="GetHostAddressExpression(EndpointReference)"/>,
    /// the endpoint scheme, and endpoint ports. HTTP and HTTPS endpoints use ports 80 and 443 respectively when no explicit port is configured.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="endpointReferenceExpression"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the requested endpoint property is unsupported, or when a non-HTTP/HTTPS endpoint does not specify a port.</exception>
    [Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    ReferenceExpression GetEndpointPropertyExpression(EndpointReferenceExpression endpointReferenceExpression)
    {
        ArgumentNullException.ThrowIfNull(endpointReferenceExpression);

        var endpointReference = endpointReferenceExpression.Endpoint;
        var property = endpointReferenceExpression.Property;
        var endpoint = endpointReference.EndpointAnnotation;
        var scheme = endpoint.UriScheme;
        var port = endpoint.Port ?? GetDefaultPort(scheme, endpoint);
        var host = new Lazy<ReferenceExpression>(() => GetHostAddressExpression(endpointReference));

        return property switch
        {
            EndpointProperty.Url => IsDefaultPort(scheme, port)
                ? ReferenceExpression.Create($"{scheme}://{host.Value}")
                : ReferenceExpression.Create($"{scheme}://{host.Value}:{port.ToString(CultureInfo.InvariantCulture)}"),
            EndpointProperty.Host or EndpointProperty.IPV4Host => host.Value,
            EndpointProperty.Port => ReferenceExpression.Create($"{port.ToString(CultureInfo.InvariantCulture)}"),
            EndpointProperty.TargetPort => endpoint.TargetPort is int targetPort
                ? ReferenceExpression.Create($"{targetPort.ToString(CultureInfo.InvariantCulture)}")
                : ReferenceExpression.Create($"{new ContainerPortReference(endpointReference.Resource)}"),
            EndpointProperty.Scheme => ReferenceExpression.Create($"{scheme}"),
            EndpointProperty.HostAndPort => ReferenceExpression.Create($"{host.Value}:{port.ToString(CultureInfo.InvariantCulture)}"),
            EndpointProperty.TlsEnabled => ReferenceExpression.Create($"{(endpoint.TlsEnabled ? bool.TrueString : bool.FalseString)}"),
            _ => throw new InvalidOperationException($"The property '{property}' is not supported for the endpoint '{endpoint.Name}'.")
        };
    }

    private static int GetDefaultPort(string scheme, EndpointAnnotation endpoint)
    {
        if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            return 443;
        }

        throw new InvalidOperationException($"Endpoint '{endpoint.Name}' must specify a port for scheme '{scheme}'.");
    }

    private static bool IsDefaultPort(string scheme, int port)
    {
        return string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) && port == 80 ||
            string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase) && port == 443;
    }
}
