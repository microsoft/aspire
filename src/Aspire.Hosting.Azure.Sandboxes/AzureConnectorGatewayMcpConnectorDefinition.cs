// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001

namespace Aspire.Hosting.Azure;

internal sealed class AzureConnectorGatewayMcpConnectorDefinition(string name, AzureConnectorGatewayConnectionResource connection)
{
    public string Name { get; } = name;

    public AzureConnectorGatewayConnectionResource Connection { get; } = connection;

    public List<AzureConnectorGatewayMcpOperationDefinition> Operations { get; } = [];
}
