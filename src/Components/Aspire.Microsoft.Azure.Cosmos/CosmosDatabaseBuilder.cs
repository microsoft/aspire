// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Microsoft.Azure.Cosmos;

/// <summary>
/// Represents a builder that can be used to register multiple container
/// instances against the same Cosmos database connection.
/// </summary>
public sealed class CosmosDatabaseBuilder
{
    private readonly IHostApplicationBuilder _hostBuilder;
    private readonly string _connectionName;
    private readonly MicrosoftAzureCosmosSettings _settings;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosDatabaseBuilder"/> class.
    /// </summary>
    /// <param name="hostBuilder">The application builder used to register Cosmos services.</param>
    /// <param name="connectionName">The connection name used to configure the Cosmos client.</param>
    /// <param name="settings">The settings used to configure the Cosmos client.</param>
    /// <param name="clientOptions">The options used to configure the Cosmos client.</param>
    public CosmosDatabaseBuilder(
        IHostApplicationBuilder hostBuilder,
        string connectionName,
        MicrosoftAzureCosmosSettings settings,
        CosmosClientOptions clientOptions)
        : this(
            hostBuilder,
            connectionName,
            settings,
            _ => AspireMicrosoftAzureCosmosExtensions.GetCosmosClient(connectionName, settings, clientOptions))
    {
    }

    internal CosmosDatabaseBuilder(
        IHostApplicationBuilder hostBuilder,
        string connectionName,
        MicrosoftAzureCosmosSettings settings,
        Func<IServiceProvider, CosmosClient> clientFactory)
    {
        _hostBuilder = hostBuilder;
        _connectionName = connectionName;
        _settings = settings;
        _hostBuilder.Services.AddKeyedSingleton<CosmosClient>(this, (serviceProvider, _) => clientFactory(serviceProvider));
    }

    internal CosmosDatabaseBuilder AddDatabase()
    {
        _hostBuilder.Services.AddSingleton(sp =>
        {
            if (string.IsNullOrEmpty(_settings.DatabaseName))
            {
                throw new InvalidOperationException(
                    $"A Database could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{_connectionName}'.");
            }

            return GetClient(sp).GetDatabase(_settings.DatabaseName);
        });

        return this;
    }

    internal CosmosDatabaseBuilder AddKeyedDatabase()
    {
        _hostBuilder.Services.AddKeyedSingleton(_connectionName, (sp, _) =>
        {
            if (string.IsNullOrEmpty(_settings.DatabaseName))
            {
                throw new InvalidOperationException(
                    $"A Database could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{_connectionName}'.");
            }

            return GetClient(sp).GetDatabase(_settings.DatabaseName);
        });

        return this;
    }

    /// <summary>
    /// Register a <see cref="Container"/> against the database managed with <see cref="CosmosDatabaseBuilder"/> as a
    /// keyed singleton.
    /// </summary>
    /// <param name="name">The name of the container to register.</param>
    /// <returns>A <see cref="CosmosDatabaseBuilder"/> that can be used for further chaining.</returns>
    public CosmosDatabaseBuilder AddKeyedContainer(string name)
    {
        var connectionInfo = _hostBuilder.GetCosmosConnectionInfo(name);

        _hostBuilder.Services.AddKeyedSingleton(name, (sp, _) =>
        {
            // If a connection string was provided, check that it contains a valid container name.
            if (connectionInfo is not null && string.IsNullOrEmpty(connectionInfo?.ContainerName))
            {
                throw new InvalidOperationException(
                    $"A Container could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{name}'");
            }

            // Use the container name from the connection string if provided, otherwise use the name
            return GetClient(sp).GetContainer(_settings.DatabaseName, connectionInfo?.ContainerName ?? name);
        });

        return this;
    }

    private CosmosClient GetClient(IServiceProvider serviceProvider)
        => serviceProvider.GetRequiredKeyedService<CosmosClient>(this);
}
