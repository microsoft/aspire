// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Mcp.Client;

/// <summary>
/// Provides configuration settings for <see cref="ModelContextProtocol.Client.McpClient"/> registrations.
/// </summary>
public sealed class McpClientSettings
{
    /// <summary>
    /// Gets or sets the explicit MCP endpoint URI.
    /// </summary>
    /// <remarks>
    /// When not configured, the endpoint defaults to <c>https://{connectionName}/mcp</c>, or
    /// <c>http://{connectionName}/mcp</c> when service discovery only provides HTTP endpoints.
    /// </remarks>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether health checks are disabled.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    internal void ParseConnectionString(string? connectionString)
    {
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme is "http" or "https" &&
            !string.IsNullOrEmpty(endpoint.Host))
        {
            Endpoint = endpoint;
            return;
        }

        throw new FormatException("The MCP client connection string must be an absolute HTTP or HTTPS URI.");
    }
}
