// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.ConnectorNamespace.Provisioning;

internal static class ConnectorNamespaceResourceVersions
{
    // Connector Namespace is the product name, while the preview ARM contract retains
    // the Microsoft.Web/connectorGateways resource type.
    // https://learn.microsoft.com/azure/connector-namespace/connector-namespace-overview
    public const string ConnectorGateway = "2026-05-01-preview";
}
