// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE001

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Authorizes a Connector Namespace managed identity to call a specific Azure sandbox endpoint.
/// </summary>
internal sealed class AzureConnectorGatewayEndpointAuthorizationAnnotation(
    string endpointName,
    AzureConnectorGatewayResource connectorGateway) : IResourceAnnotation
{
    public string EndpointName { get; } = endpointName;

    public AzureConnectorGatewayResource ConnectorGateway { get; } = connectorGateway;
}
