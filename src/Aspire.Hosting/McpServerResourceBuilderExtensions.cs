// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for configuring MCP (Model Context Protocol) server endpoints on resources.
/// </summary>
public static class McpServerResourceBuilderExtensions
{
    private static readonly string[] s_httpSchemes = ["https", "http"];

    /// <summary>
    /// Marks the resource as hosting a Model Context Protocol (MCP) server on the specified endpoint.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="path">An optional path to append to the endpoint URL when forming the MCP server address. Defaults to <c>"/mcp"</c>.</param>
    /// <param name="endpointName">An optional name of the endpoint that hosts the MCP server. If not specified, defaults to the first HTTPS or HTTP endpoint.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining additional configuration.</returns>
    /// <remarks>
    /// This method adds an <see cref="McpServerEndpointAnnotation"/> to the resource, enabling the Aspire tooling
    /// to discover and proxy the MCP server exposed by the resource.
    /// </remarks>
    /// <example>
    /// Mark a resource as hosting an MCP server using the default endpoint:
    /// <code>
    /// var api = builder.AddProject&lt;Projects.MyApi&gt;("api")
    ///     .WithMcpServer();
    /// </code>
    /// Mark a resource as hosting an MCP server with a custom path and endpoint:
    /// <code>
    /// var api = builder.AddProject&lt;Projects.MyApi&gt;("api")
    ///     .WithMcpServer("/sse", endpointName: "https");
    /// </code>
    /// </example>
    [Experimental("ASPIREMCP001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    [AspireExport("withMcpServer", Description = "Configures an MCP server endpoint on the resource")]
    public static IResourceBuilder<T> WithMcpServer<T>(
        this IResourceBuilder<T> builder,
        string? path = "/mcp",
        [EndpointName] string? endpointName = null)
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(new McpServerEndpointAnnotation(async (resource, cancellationToken) =>
        {
            var endpoints = resource.GetEndpoints();
            EndpointReference? endpoint = null;

            if (endpointName is not null)
            {
                endpoint = endpoints.FirstOrDefault(e => string.Equals(e.EndpointName, endpointName, StringComparisons.EndpointAnnotationName));

                if (endpoint is null)
                {
                    throw new DistributedApplicationException(
                        $"Could not create MCP server for resource '{resource.Name}' as no endpoint was found with name '{endpointName}'.");
                }
            }
            else
            {
                foreach (var scheme in s_httpSchemes)
                {
                    endpoint = endpoints.FirstOrDefault(e => string.Equals(e.EndpointName, scheme, StringComparisons.EndpointAnnotationName));
                    if (endpoint is not null)
                    {
                        break;
                    }
                }

                if (endpoint is null)
                {
                    throw new DistributedApplicationException(
                        $"Could not create MCP server for resource '{resource.Name}' as no endpoint was found matching one of the specified names: {string.Join(", ", s_httpSchemes)}");
                }
            }

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
        }));
    }
}
