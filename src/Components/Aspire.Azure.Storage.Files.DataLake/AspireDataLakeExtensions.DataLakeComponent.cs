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
    private sealed class DataLakeComponent
        : AzureComponent<AzureDataLakeSettings, DataLakeServiceClient, DataLakeClientOptions>
    {
        protected override IAzureClientBuilder<DataLakeServiceClient, DataLakeClientOptions> AddClient(
            AzureClientFactoryBuilder azureFactoryBuilder,
            AzureDataLakeSettings settings,
            string connectionName,
            string configurationSectionName)
            => ((IAzureClientFactoryBuilderWithCredential)azureFactoryBuilder)
                .RegisterClientFactory<DataLakeServiceClient, DataLakeClientOptions>(
                    (options, cred) =>
                    {
                        var connectionString = settings.ConnectionString;
                        if (string.IsNullOrEmpty(connectionString) && settings.ServiceUri is null)
                        {
                            throw new InvalidOperationException(
                                $"A DataLakeServiceClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or specify a 'ConnectionString' or 'ServiceUri' in the '{configurationSectionName}' configuration section.");
                        }

                        return !string.IsNullOrEmpty(connectionString) ?
                            new DataLakeServiceClient(connectionString, options) :
                            cred is not null ? new DataLakeServiceClient(settings.ServiceUri, cred, options) :
                                new DataLakeServiceClient(settings.ServiceUri, options);
                    },
                    requiresCredential: false);

        protected override bool GetHealthCheckEnabled(AzureDataLakeSettings settings) => !settings.DisableHealthChecks;

        protected override bool GetMetricsEnabled(AzureDataLakeSettings settings) => false;

        protected override bool GetTracingEnabled(AzureDataLakeSettings settings) => !settings.DisableTracing;

        protected override TokenCredential? GetTokenCredential(AzureDataLakeSettings settings) => settings.Credential;

        protected override void BindSettingsToConfiguration(
            AzureDataLakeSettings settings,
            IConfiguration configuration)
        {
            configuration.Bind(settings);
        }

        protected override void BindClientOptionsToConfiguration(
            IAzureClientBuilder<DataLakeServiceClient, DataLakeClientOptions> clientBuilder,
            IConfiguration configuration)
#pragma warning disable IDE0200 // Remove unnecessary lambda expression - needed so the ConfigBinder Source Generator works
            => clientBuilder.ConfigureOptions(options => configuration.Bind(options));
#pragma warning restore IDE0200

        protected override IHealthCheck CreateHealthCheck(DataLakeServiceClient client, AzureDataLakeSettings settings)
            => new AzureDataLakeStorageHealthCheck(client);
    }
}
