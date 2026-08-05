// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures a Connector Namespace connection.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayConnectionOptions
{
    /// <summary>
    /// Gets or sets the Azure child resource name. The Aspire resource name is used when omitted.
    /// </summary>
    public string? ConnectionName { get; set; }

    /// <summary>
    /// Gets or sets the friendly connection name shown in the Connector Namespace portal.
    /// </summary>
    public string? DisplayName { get; set; }
}

/// <summary>
/// Configures a Microsoft Entra access policy for a Connector Namespace connection.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayAccessPolicyOptions
{
    /// <summary>
    /// Gets or sets the Azure child resource name. The Aspire resource name is used when omitted.
    /// </summary>
    public string? PolicyName { get; set; }

    /// <summary>
    /// Gets or sets the Microsoft Entra object ID authorized to use the connection.
    /// </summary>
    public required string ObjectId { get; set; }

    /// <summary>
    /// Gets or sets the Microsoft Entra tenant ID for <see cref="ObjectId"/>.
    /// </summary>
    public required string TenantId { get; set; }
}

/// <summary>
/// Configures a managed MCP server in a Connector Namespace.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayMcpServerConfigOptions
{
    /// <summary>
    /// Gets or sets the Azure child resource name. The Aspire resource name is used when omitted.
    /// </summary>
    public string? ConfigName { get; set; }

    /// <summary>
    /// Gets or sets the description shown to MCP clients.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Configures a connector route exposed by a managed MCP server.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayMcpConnectorOptions
{
    /// <summary>
    /// Gets or sets the friendly connector name shown to MCP clients.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the connector description shown to MCP clients.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the allow-listed connector operations exposed as MCP tools.
    /// </summary>
    public AzureConnectorGatewayMcpOperationOptions[] Operations { get; set; } = [];
}

/// <summary>
/// Describes a connector operation exposed as an MCP tool.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayMcpOperationOptions
{
    /// <summary>
    /// Gets or sets the connector operation ID.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the friendly operation name shown to MCP clients.
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the operation description shown to MCP clients.
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Configures a Connector Namespace trigger that delivers events to an Azure sandbox endpoint.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayTriggerOptions
{
    /// <summary>
    /// Gets or sets the Azure child resource name. The Aspire resource name is used when omitted.
    /// </summary>
    public string? TriggerName { get; set; }

    /// <summary>
    /// Gets or sets the trigger description shown in the Connector Namespace portal.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the relative callback path appended to the Azure sandbox endpoint.
    /// </summary>
    public string? CallbackPath { get; set; }

    /// <summary>
    /// Gets or sets the connector operation parameters.
    /// </summary>
    public AzureConnectorGatewayTriggerParameter[] Parameters { get; set; } = [];
}
