// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Common;
using Aspire.Azure.Storage.Files.DataLake;
using Azure.Core;
using Azure.Core.Extensions;
using Azure.Storage.Files.DataLake;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

public static partial class AspireDataLakeExtensions
{
    private sealed class DataLakeFileSystemComponent
        : AzureComponent<AzureDataLakeFileSystemSettings, DataLakeFileSystemClient, DataLakeClientOptions>
    {
        protected override IAzureClientBuilder<DataLakeFileSystemClient, DataLakeClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder,
            AzureDataLakeFileSystemSettings settings,
            string connectionName,
            string configurationSectionName)
            => ((IAzureClientFactoryBuilderWithCredential)azureFactoryBuilder)
                .RegisterClientFactory<DataLakeFileSystemClient, DataLakeClientOptions>(
                    (options, cred) =>
                    {
                        if (string.IsNullOrEmpty(settings.FileSystemName))
                        {
                            throw new InvalidOperationException(
                                $"The connection string '{connectionName}' does not exist or is missing the file system name.");
                        }

                        var connectionString = settings.ConnectionString;
                        if (string.IsNullOrEmpty(connectionString) && settings.ServiceUri is null)
                        {
                            throw new InvalidOperationException(
                                $"A DataLakeServiceClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify a 'ConnectionString' or 'ServiceUri' in the '{configurationSectionName}' configuration section.");
                        }

                        var dataLakeServiceClient = !string.IsNullOrEmpty(connectionString) ?
                            new DataLakeServiceClient(connectionString, options) :
                            cred is not null ? new DataLakeServiceClient(settings.ServiceUri, cred, options) :
                                new DataLakeServiceClient(settings.ServiceUri, options);

                        var fileSystemClient = dataLakeServiceClient.GetFileSystemClient(settings.FileSystemName);

                        return fileSystemClient;
                    },
                    requiresCredential: false);

        protected override bool GetHealthCheckEnabled(AzureDataLakeFileSystemSettings settings)
            => !settings.DisableHealthChecks;

        protected override bool GetMetricsEnabled(AzureDataLakeFileSystemSettings settings) => false;

        protected override bool GetTracingEnabled(AzureDataLakeFileSystemSettings settings) => !settings.DisableTracing;

        protected override TokenCredential? GetTokenCredential(AzureDataLakeFileSystemSettings settings)
            => settings.Credential;

        protected override void BindSettingsToConfiguration(
            AzureDataLakeFileSystemSettings settings,
            IConfiguration configuration)
        {
            configuration.Bind(settings);
        }

        protected override void BindClientOptionsToConfiguration(
            IAzureClientBuilder<DataLakeFileSystemClient, DataLakeClientOptions> clientBuilder,
            IConfiguration configuration)
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            => clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200

        protected override IHealthCheck CreateHealthCheck(
            DataLakeFileSystemClient client,
            AzureDataLakeFileSystemSettings settings)
            => new AzureDataLakeFileSystemHealthCheck(client);
    }
}
