// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;

namespace Aspire.Microsoft.Azure.Cosmos;

/// <summary>
/// The settings relevant to accessing Azure Cosmos DB.
/// </summary>
public sealed class MicrosoftAzureCosmosSettings
{
    /// <summary>
    /// Gets or sets the connection string of the Azure Cosmos database to connect to.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the name of the database to connect to.
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Gets or sets the name of the container to connect to.
    /// </summary>
    public string? ContainerName { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the Azure Cosmos DB health check is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableTracing { get; set; }

    /// <summary>
    /// A <see cref="Uri"/> referencing the Azure Cosmos DB Endpoint.
    /// This is likely to be similar to "https://{account_name}.documents.azure.com".
    /// </summary>
    /// <remarks>
    /// Must not contain shared access signature.
    /// Used along with <see cref="Credential"/> to establish the connection.
    /// </remarks>
    public Uri? AccountEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the credential used to authenticate to the Azure Cosmos DB endpoint.
    /// </summary>
    public TokenCredential? Credential { get; set; }
}

