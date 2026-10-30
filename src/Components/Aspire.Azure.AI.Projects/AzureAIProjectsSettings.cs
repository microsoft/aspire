// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Azure.Core;

namespace Aspire.Azure.AI.Projects;

/// <summary>
/// Configures the connection and tracing for an Azure AI Projects client.
/// </summary>
public sealed class AzureAIProjectsSettings : IConnectionStringSettings
{
    /// <summary>
    /// Gets or sets the HTTPS Foundry project endpoint, such as <c>https://account.services.ai.azure.com/api/projects/project</c>.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the Microsoft Entra credential. Uses managed identity in Azure and a development credential locally by default.
    /// </summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Gets or sets whether tracing is disabled. The default is <see langword="false"/>.
    /// </summary>
    public bool DisableTracing { get; set; }

    void IConnectionStringSettings.ParseConnectionString(string? connectionString)
    {
        Endpoint = AzureAIProjectConnectionString.ParseEndpoint(connectionString);
    }
}
