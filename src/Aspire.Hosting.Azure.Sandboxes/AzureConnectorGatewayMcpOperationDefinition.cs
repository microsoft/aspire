// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

internal sealed class AzureConnectorGatewayMcpOperationDefinition(string name, string? displayName, string? description)
{
    public string Name { get; } = name;

    public string? DisplayName { get; } = displayName;

    public string? Description { get; } = description;
}
