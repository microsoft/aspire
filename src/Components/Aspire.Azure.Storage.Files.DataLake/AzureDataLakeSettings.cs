// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Azure.Core;

namespace Aspire.Azure.Storage.Files.DataLake;

/// <summary>
/// Provides the client configuration settings for connecting to Azure Data Lake Storage.
/// </summary>
public class AzureDataLakeSettings : IConnectionStringSettings
{
    /// <summary>
    /// Gets or sets the connection string used to connect to the Azure Data Lake Storage service.
    /// </summary>
    /// <remarks>
    /// If <see cref="ConnectionString" /> is set, it overrides <see cref="ServiceUri" /> and <see cref="Credential" />.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// A <see cref="Uri" /> referencing the data lake service.
    /// This is likely to be similar to "https://{account_name}.dfs.core.windows.net".
    /// </summary>
    /// <remarks>
    /// Must not contain shared access signature.
    /// Used along with <see cref="Credential" /> to establish the connection.
    /// </remarks>
    public Uri? ServiceUri { get; set; }

    /// <summary>
    /// Gets or sets the credential used to authenticate to the Azure Data Lake Storage.
    /// </summary>
    public TokenCredential? Credential { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the Azure Data Lake Storage health check is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false" />.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false" />.
    /// </value>
    public bool DisableTracing { get; set; }

    void IConnectionStringSettings.ParseConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return;
        }

        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            ServiceUri = uri;
        }
        else
        {
            ConnectionString = connectionString;
        }
    }
}
