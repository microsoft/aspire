// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures a Connector Namespace connection.
/// </summary>
[AspireDto]
public sealed class AzureConnectorNamespaceConnectionOptions
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
public sealed class AzureConnectorNamespaceAccessPolicyOptions
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
/// Specifies the Microsoft Entra principal type authorized to call a managed MCP server.
/// </summary>
public enum AzureConnectorNamespaceMcpAccessPolicyPrincipalType
{
    /// <summary>
    /// Authorizes an individual Microsoft Entra user.
    /// </summary>
    User = 1,

    /// <summary>
    /// Authorizes a Microsoft Entra group.
    /// </summary>
    Group = 2
}

/// <summary>
/// Configures a Microsoft Entra access policy for a managed MCP server configuration.
/// </summary>
[AspireDto]
public sealed class AzureConnectorNamespaceMcpAccessPolicyOptions
{
    /// <summary>
    /// Gets or sets the Microsoft Entra object ID authorized to call the MCP server.
    /// </summary>
    public required string ObjectId { get; set; }

    /// <summary>
    /// Gets or sets the Microsoft Entra tenant ID for <see cref="ObjectId"/>.
    /// </summary>
    public required string TenantId { get; set; }

    /// <summary>
    /// Gets or sets whether <see cref="ObjectId"/> identifies a user or group.
    /// </summary>
    public required AzureConnectorNamespaceMcpAccessPolicyPrincipalType PrincipalType { get; set; }
}

/// <summary>
/// Configures a managed MCP server in a Connector Namespace.
/// </summary>
[AspireDto]
public sealed class AzureConnectorNamespaceMcpServerConfigOptions
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
public sealed class AzureConnectorNamespaceMcpConnectorOptions
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
    public AzureConnectorNamespaceMcpOperationOptions[] Operations { get; set; } = [];
}

/// <summary>
/// Describes a connector operation exposed as an MCP tool.
/// </summary>
[AspireDto]
public sealed class AzureConnectorNamespaceMcpOperationOptions
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
