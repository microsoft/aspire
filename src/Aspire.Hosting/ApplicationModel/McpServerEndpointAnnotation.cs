// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an annotation that identifies an endpoint on a resource that hosts a Model Context Protocol (MCP) server.
/// </summary>
/// <remarks>
/// This annotation is intended for discovery and proxying scenarios where the Aspire AppHost can act as a mediator
/// between clients (such as the Aspire CLI) and MCP servers exposed by resources.
/// </remarks>
public sealed class McpServerEndpointAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="McpServerEndpointAnnotation"/> class.
    /// </summary>
    /// <param name="endpointUrlResolver">A callback that resolves the MCP server endpoint URL from the resource.</param>
    public McpServerEndpointAnnotation(Func<IResourceWithEndpoints, CancellationToken, Task<Uri?>> endpointUrlResolver)
    {
        ArgumentNullException.ThrowIfNull(endpointUrlResolver);
        EndpointUrlResolver = endpointUrlResolver;
    }

    /// <summary>
    /// Gets the callback that resolves the MCP server endpoint URL from the resource.
    /// </summary>
    public Func<IResourceWithEndpoints, CancellationToken, Task<Uri?>> EndpointUrlResolver { get; }

    /// <summary>
    /// Creates an <see cref="McpServerEndpointAnnotation"/> that resolves the MCP server URL from a named endpoint.
    /// </summary>
    /// <param name="endpointName">The name of the endpoint on the resource that hosts the MCP server.</param>
    /// <param name="path">An optional path to append to the endpoint URL. Defaults to <c>"/mcp"</c>.</param>
    /// <returns>A new <see cref="McpServerEndpointAnnotation"/> instance.</returns>
    public static McpServerEndpointAnnotation FromEndpoint(string endpointName, string? path = "/mcp")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointName);

        return new McpServerEndpointAnnotation(async (resource, cancellationToken) =>
        {
            var endpoint = resource.GetEndpoint(endpointName);
            if (!endpoint.Exists)
            {
                return null;
            }

            var baseUrl = await endpoint.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(baseUrl))
            {
                return null;
            }

            if (string.IsNullOrEmpty(path))
            {
                return new Uri(baseUrl, UriKind.Absolute);
            }

            var normalizedPath = path;
            if (!normalizedPath.StartsWith("/", StringComparison.Ordinal))
            {
                normalizedPath = "/" + normalizedPath;
            }

            var combined = baseUrl.TrimEnd('/') + normalizedPath;
            return new Uri(combined, UriKind.Absolute);
        });
    }
}
