// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;
using System.Text.RegularExpressions;
using Aspire.Azure.Common;

namespace Aspire.Azure.Storage.Files.DataLake;

/// <summary>
/// Provides the client configuration settings for connecting to an Azure DataLake FileSystem.
/// </summary>
public sealed partial class AzureDataLakeFileSystemSettings : AzureDataLakeSettings, IConnectionStringSettings
{
    [GeneratedRegex(@"(?i)FileSystemName\s*=\s*([^;]+);?", RegexOptions.IgnoreCase)]
    private static partial Regex FileSystemNameRegex();

    /// <summary>
    /// Gets or sets the name of the DataLake FileSystem.
    /// </summary>
    public string? FileSystemName { get; set; }

    void IConnectionStringSettings.ParseConnectionString(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return;
        }

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        if (builder.TryGetValue("FileSystemName", out var fileSystemName))
        {
            FileSystemName = fileSystemName.ToString();

            // Remove the FileSystemName property from the connection string as DataLakeServiceClient would fail to parse it.
            connectionString = FileSystemNameRegex().Replace(connectionString, "");

            // NB: we can't remove FileSystemName by using the DbConnectionStringBuilder as it would escape the AccountKey value
            // when the connection string is built and DataLakeServiceClient doesn't support escape sequences. 
        }

        // Connection string built from a URI? e.g., Endpoint=https://{account_name}.dfs.core.windows.net;FileSystemName=...;
        if (builder.TryGetValue("Endpoint", out var endpoint) && endpoint is string)
        {
            if (Uri.TryCreate(endpoint.ToString(), UriKind.Absolute, out var uri))
            {
                ServiceUri = uri;
            }
        }
        else
        {
            // Otherwise preserve the existing connection string
            ConnectionString = connectionString;
        }
    }
}
