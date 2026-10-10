// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

internal sealed class AzureConnectorNamespaceMcpConnectorDefinition(
    string name,
    string? displayName,
    string? description,
    AzureConnectorNamespaceConnectionResource connection)
{
    public string Name { get; } = name;

    public string? DisplayName { get; } = displayName;

    public string? Description { get; } = description;

    public AzureConnectorNamespaceConnectionResource Connection { get; } = connection;

    public List<AzureConnectorNamespaceMcpOperationDefinition> Operations { get; } = [];
}
