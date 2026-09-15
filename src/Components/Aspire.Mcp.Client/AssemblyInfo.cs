// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Mcp.Client;

[assembly: ConfigurationSchema("Aspire:Mcp:Client", typeof(McpClientSettings))]

[assembly: LoggingCategories(
    "ModelContextProtocol.Authentication.ClientOAuthProvider",
    "ModelContextProtocol.Client.AutoDetectingClientSessionTransport",
    "ModelContextProtocol.Client.HttpClientTransport",
    "ModelContextProtocol.Client.McpClient")]
