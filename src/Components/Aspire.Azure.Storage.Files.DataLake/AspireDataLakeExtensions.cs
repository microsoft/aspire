// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Azure.Storage.Files.DataLake;
using Azure.Core.Extensions;
using Azure.Storage.Files.DataLake;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for registering <see cref="DataLakeServiceClient" /> as a singleton in the services provided by the <see cref="IHostApplicationBuilder" />.
/// </summary>
public static partial class AspireDataLakeExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Azure:Storage:Files:DataLake";

    /// <summary>
    /// Registers <see cref="DataLakeServiceClient" /> as a singleton in the services provided by the <paramref name="builder" />.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="AzureDataLakeSettings" />. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientBuilder">An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}" />.</param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Storage:Files:DataLake" section.</remarks>
    /// <exception cref="InvalidOperationException">
    /// Neither <see cref="AzureDataLakeSettings.ConnectionString" /> nor <see cref="AzureDataLakeSettings.ServiceUri" /> is provided.
    /// </exception>
    public static void AddAzureDataLakeServiceClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureDataLakeSettings>? configureSettings = null,
        Action<IAzureClientBuilder<DataLakeServiceClient, DataLakeClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        new DataLakeComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            connectionName,
            serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="DataLakeServiceClient" /> as a singleton for given <paramref name="name" /> in the services provided by the <paramref name="builder" />.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">
    /// The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey" /> of the service and also to retrieve
    /// the connection string from the ConnectionStrings configuration section.
    /// </param>
    /// <param name="configureSettings">
    /// An optional method that can be used for customizing the <see cref="AzureDataLakeSettings" />.
    /// It's invoked after the settings are read from the configuration.
    /// </param>
    /// <param name="configureClientBuilder">
    /// An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}" />.
    /// </param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Storage:Files:DataLake:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">
    /// Neither <see cref="AzureDataLakeSettings.ConnectionString" /> nor <see cref="AzureDataLakeSettings.ServiceUri" /> is provided.
    /// </exception>
    public static void AddKeyedAzureDataLakeServiceClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureDataLakeSettings>? configureSettings = null,
        Action<IAzureClientBuilder<DataLakeServiceClient, DataLakeClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        new DataLakeComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            connectionName: name,
            serviceKey: name);
    }

    /// <summary>
    /// Registers <see cref="DataLakeFileSystemClient" /> as a singleton in the services provided by the <paramref name="builder" />.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">
    /// An optional method that can be used for customizing the <see cref="AzureDataLakeFileSystemSettings" />.
    /// It's invoked after the settings are read from the configuration.
    /// </param>
    /// <param name="configureClientBuilder">
    /// An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}" />.
    /// </param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Storage:Files:DataLake" section.</remarks>
    /// <exception cref="InvalidOperationException">
    /// Neither <see cref="AzureDataLakeSettings.ConnectionString" /> nor <see cref="AzureDataLakeSettings.ServiceUri" /> is provided.
    /// - or -
    /// <see cref="AzureDataLakeFileSystemSettings.FileSystemName" /> is not provided in the configuration section.
    /// </exception>
    public static void AddAzureDataLakeFileSystemClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<AzureDataLakeFileSystemSettings>? configureSettings = null,
        Action<IAzureClientBuilder<DataLakeFileSystemClient, DataLakeClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);
        new DataLakeFileSystemComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            connectionName,
            serviceKey: null);
    }

    /// <summary>
    /// Registers <see cref="DataLakeFileSystemClient" /> as a singleton in the services provided by the <paramref name="builder" />.
    /// Enables retries, corresponding health check, logging and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">
    /// An optional method that can be used for customizing the <see cref="AzureDataLakeFileSystemSettings" />.
    /// It's invoked after the settings are read from the configuration.
    /// </param>
    /// <param name="configureClientBuilder">
    /// An optional method that can be used for customizing the <see cref="IAzureClientBuilder{TClient, TOptions}" />.
    /// </param>
    /// <remarks>Reads the configuration from "Aspire:Azure:Storage:Files:DataLake" section.</remarks>
    /// <exception cref="InvalidOperationException">
    /// Neither <see cref="AzureDataLakeSettings.ConnectionString" /> nor <see cref="AzureDataLakeSettings.ServiceUri" /> is provided.
    /// - or -
    /// <see cref="AzureDataLakeFileSystemSettings.FileSystemName" /> is not provided in the configuration section.
    /// </exception>
    public static void AddKeyedAzureDataLakeFileSystemClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<AzureDataLakeFileSystemSettings>? configureSettings = null,
        Action<IAzureClientBuilder<DataLakeFileSystemClient, DataLakeClientOptions>>? configureClientBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        new DataLakeFileSystemComponent().AddClient(
            builder,
            DefaultConfigSectionName,
            configureSettings,
            configureClientBuilder,
            connectionName: name,
            serviceKey: name);
    }
}
