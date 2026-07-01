// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Foundry;

/// <summary>
/// Options used when adding an MCP tool definition to a Microsoft Foundry Toolbox from a polyglot
/// AppHost. Mirrors the auth/header configuration available via the .NET <see cref="Action{T}"/>
/// callback overloads of <c>WithMcpTool</c>.
/// </summary>
[AspireDto]
internal sealed class FoundryToolboxMcpToolOptions
{
    /// <summary>
    /// Gets or sets an optional expression that resolves to the bearer token sent to the MCP
    /// server on every request. Use this for MCP servers that require authorization.
    /// </summary>
    /// <remarks>
    /// When set, the resolved value is forwarded to the Foundry data plane and the Foundry agent
    /// runtime sends it as the <c>Authorization</c> header (with the standard <c>Bearer</c> scheme)
    /// when invoking the MCP server.
    /// </remarks>
    public ReferenceExpression? AuthorizationToken { get; set; }

    /// <summary>
    /// Gets the set of HTTP headers the Foundry agent runtime sends to the MCP server on every
    /// request. Header names are matched case-insensitively, mirroring HTTP semantics. Header
    /// entries whose value resolves to <see langword="null"/> or an empty string are silently
    /// omitted.
    /// </summary>
    public IDictionary<string, ReferenceExpression> Headers { get; init; } =
        new Dictionary<string, ReferenceExpression>(StringComparer.OrdinalIgnoreCase);
}
