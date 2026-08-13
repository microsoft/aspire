// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Hosting.Azure.CosmosDB;
using Aspire.Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Azure Cosmos DB extension
/// </summary>
public static class AspireMicrosoftAzureCosmosExtensions
{
    private const string DefaultConfigSectionName = "Aspire:Microsoft:Azure:Cosmos";

    /// <summary>
    /// Registers <see cref="CosmosClient" /> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Configures logging and telemetry for the <see cref="CosmosClient" />.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">The connection name to use to find a connection string.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MicrosoftAzureCosmosSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientOptions">An optional method that can be used for customizing the <see cref="CosmosClientOptions"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Azure:Cosmos" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddAzureCosmosClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings = null,
        Action<CosmosClientOptions>? configureClientOptions = null)
        => AddCosmosClient(builder, connectionName, serviceKey: null, configureSettings, Wrap(configureClientOptions));

    /// <inheritdoc cref="AddAzureCosmosClient(IHostApplicationBuilder, string, Action{MicrosoftAzureCosmosSettings}?, Action{CosmosClientOptions}?)"/>
    /// <remarks>The client options callback receives the service provider used to create the client.</remarks>
    public static void AddAzureCosmosClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
        => AddCosmosClient(builder, connectionName, serviceKey: null, configureSettings, configureClientOptions);

    /// <summary>
    /// Registers the <see cref="Container"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">The connection name to use to find a connection string.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MicrosoftAzureCosmosSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientOptions">An optional method that can be used for customizing the <see cref="CosmosClientOptions"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Azure:Cosmos" section.</remarks>
    /// <remarks>
    /// The <see cref="Container"/> is registered as a singleton in the services provided by
    /// the <paramref name="builder"/> and does not reuse any existing <see cref="CosmosClient"/>
    /// instances in the DI container. The connection string associated with the <paramref name="connectionName"/>
    /// must contain the database name and container name or be set in the <paramref name="configureSettings" />
    /// callback. To interact with multiple containers against the same database, use
    /// <see cref="CosmosDatabaseBuilder"/> to register the database and then call
    /// <see cref="CosmosDatabaseBuilder.AddKeyedContainer(string)"/> for each container.
    /// </remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddAzureCosmosContainer(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings = null,
        Action<CosmosClientOptions>? configureClientOptions = null)
        => AddCosmosContainer(builder, connectionName, serviceKey: null, configureSettings, Wrap(configureClientOptions));

    /// <inheritdoc cref="AddAzureCosmosContainer(IHostApplicationBuilder, string, Action{MicrosoftAzureCosmosSettings}?, Action{CosmosClientOptions}?)"/>
    /// <remarks>The client options callback receives the service provider used to create the client.</remarks>
    public static void AddAzureCosmosContainer(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
        => AddCosmosContainer(builder, connectionName, serviceKey: null, configureSettings, configureClientOptions);

    /// <summary>
    /// Registers the <see cref="CosmosClient" /> as a singleton for given <paramref name="name" /> in the services provided by the <paramref name="builder"/>.
    /// Configures logging and telemetry for the <see cref="CosmosClient" />.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MicrosoftAzureCosmosSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientOptions">An optional method that can be used for customizing the <see cref="CosmosClientOptions"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Azure:Cosmos:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddKeyedAzureCosmosClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<MicrosoftAzureCosmosSettings>? configureSettings = null,
        Action<CosmosClientOptions>? configureClientOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddCosmosClient(builder, name, name, configureSettings, Wrap(configureClientOptions));
    }

    /// <inheritdoc cref="AddKeyedAzureCosmosClient(IHostApplicationBuilder, string, Action{MicrosoftAzureCosmosSettings}?, Action{CosmosClientOptions}?)"/>
    /// <remarks>The client options callback receives the service provider used to create the client.</remarks>
    public static void AddKeyedAzureCosmosClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        AddCosmosClient(builder, name, name, configureSettings, configureClientOptions);
    }

    /// <summary>
    /// Registers the <see cref="Container"/> as a singleton for given <paramref name="name" /> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MicrosoftAzureCosmosSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientOptions">An optional method that can be used for customizing the <see cref="CosmosClientOptions"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Azure:Cosmos:{name}" section.</remarks>
    /// <remarks>
    /// The <see cref="Container"/> is registered as a singleton in the services provided by
    /// the <paramref name="builder"/> and does not reuse any existing <see cref="CosmosClient"/>
    /// instances in the DI container. The connection string associated with the <paramref name="name"/>
    /// must contain the database name and container name or be set in the <paramref name="configureSettings" />
    /// callback. To interact with multiple containers against the same database, use
    /// <see cref="CosmosDatabaseBuilder"/> to register the database and then call
    /// <see cref="CosmosDatabaseBuilder.AddKeyedContainer(string)"/> for each container.
    /// </remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static void AddKeyedAzureCosmosContainer(
        this IHostApplicationBuilder builder,
        string name,
        Action<MicrosoftAzureCosmosSettings>? configureSettings = null,
        Action<CosmosClientOptions>? configureClientOptions = null)
        => AddCosmosContainer(builder, name, name, configureSettings, Wrap(configureClientOptions));

    /// <inheritdoc cref="AddKeyedAzureCosmosContainer(IHostApplicationBuilder, string, Action{MicrosoftAzureCosmosSettings}?, Action{CosmosClientOptions}?)"/>
    /// <remarks>The client options callback receives the service provider used to create the client.</remarks>
    public static void AddKeyedAzureCosmosContainer(
        this IHostApplicationBuilder builder,
        string name,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
        => AddCosmosContainer(builder, name, name, configureSettings, configureClientOptions);

    /// <summary>
    /// Registers the <see cref="Database"/> as a singleton the services provided by the <paramref name="builder"/>
    /// and returns a <see cref="CosmosDatabaseBuilder"/> to support chaining multiple container registrations against the same database.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="connectionName">The connection name to use to find a connection string.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MicrosoftAzureCosmosSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientOptions">An optional method that can be used for customizing the <see cref="CosmosClientOptions"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Azure:Cosmos:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static CosmosDatabaseBuilder AddAzureCosmosDatabase(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings = null,
        Action<CosmosClientOptions>? configureClientOptions = null)
        => AddCosmosDatabase(builder, connectionName, serviceKey: null, configureSettings, Wrap(configureClientOptions));

    /// <inheritdoc cref="AddAzureCosmosDatabase(IHostApplicationBuilder, string, Action{MicrosoftAzureCosmosSettings}?, Action{CosmosClientOptions}?)"/>
    /// <remarks>The client options callback receives the service provider used to create the client.</remarks>
    public static CosmosDatabaseBuilder AddAzureCosmosDatabase(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
        => AddCosmosDatabase(builder, connectionName, serviceKey: null, configureSettings, configureClientOptions);

    /// <summary>
    /// Registers the <see cref="Database"/> as a singleton for given <paramref name="name" /> in the services provided by the <paramref name="builder"/>
    /// and returns a <see cref="CosmosDatabaseBuilder"/> to support chaining multiple container registrations against the same database.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder" /> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="MicrosoftAzureCosmosSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureClientOptions">An optional method that can be used for customizing the <see cref="CosmosClientOptions"/>.</param>
    /// <remarks>Reads the configuration from "Aspire:Microsoft:Azure:Cosmos:{name}" section.</remarks>
    /// <exception cref="InvalidOperationException">If required ConnectionString is not provided in configuration section</exception>
    public static CosmosDatabaseBuilder AddKeyedAzureCosmosDatabase(
       this IHostApplicationBuilder builder,
       string name,
       Action<MicrosoftAzureCosmosSettings>? configureSettings = null,
       Action<CosmosClientOptions>? configureClientOptions = null)
        => AddCosmosDatabase(builder, name, name, configureSettings, Wrap(configureClientOptions));

    /// <inheritdoc cref="AddKeyedAzureCosmosDatabase(IHostApplicationBuilder, string, Action{MicrosoftAzureCosmosSettings}?, Action{CosmosClientOptions}?)"/>
    /// <remarks>The client options callback receives the service provider used to create the client.</remarks>
    public static CosmosDatabaseBuilder AddKeyedAzureCosmosDatabase(
        this IHostApplicationBuilder builder,
        string name,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
        => AddCosmosDatabase(builder, name, name, configureSettings, configureClientOptions);

    private static void AddCosmosClient(
        IHostApplicationBuilder builder,
        string connectionName,
        string? serviceKey,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
    {
        var (_, clientFactory) = GetClientRegistration(builder, connectionName, configureSettings, configureClientOptions);

        if (serviceKey is null)
        {
            builder.Services.AddSingleton(clientFactory);
        }
        else
        {
            builder.Services.AddKeyedSingleton<CosmosClient>(serviceKey, (serviceProvider, _) => clientFactory(serviceProvider));
        }
    }

    private static void AddCosmosContainer(
        IHostApplicationBuilder builder,
        string connectionName,
        string? serviceKey,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
    {
        var (settings, clientFactory) = GetClientRegistration(builder, connectionName, configureSettings, configureClientOptions);

        Container Factory(IServiceProvider serviceProvider)
        {
            if (string.IsNullOrEmpty(settings.ContainerName) || string.IsNullOrEmpty(settings.DatabaseName))
            {
                throw new InvalidOperationException($"The connection string '{connectionName}' does not exist or is missing the container name or database name.");
            }

            return clientFactory(serviceProvider).GetContainer(settings.DatabaseName, settings.ContainerName);
        }

        if (serviceKey is null)
        {
            builder.Services.AddSingleton(Factory);
        }
        else
        {
            builder.Services.AddKeyedSingleton<Container>(serviceKey, (serviceProvider, _) => Factory(serviceProvider));
        }
    }

    private static CosmosDatabaseBuilder AddCosmosDatabase(
        IHostApplicationBuilder builder,
        string connectionName,
        string? serviceKey,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
    {
        var (settings, clientFactory) = GetClientRegistration(builder, connectionName, configureSettings, configureClientOptions);
        var cosmosDatabaseBuilder = new CosmosDatabaseBuilder(
            builder,
            connectionName,
            settings,
            clientFactory);

        return serviceKey is null
            ? cosmosDatabaseBuilder.AddDatabase()
            : cosmosDatabaseBuilder.AddKeyedDatabase();
    }

    private static (MicrosoftAzureCosmosSettings Settings, Func<IServiceProvider, CosmosClient> ClientFactory) GetClientRegistration(
        IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings,
        Action<IServiceProvider, CosmosClientOptions>? configureClientOptions)
    {
        var settings = builder.GetSettings(connectionName, configureSettings);
        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry().WithTracing(tracerProviderBuilder =>
            {
                tracerProviderBuilder.AddSource("Azure.Cosmos.Operation");
            });
        }

        return (settings, serviceProvider =>
        {
            var clientOptions = new CosmosClientOptions();
            // Needs to be enabled for either logging or tracing to work.
            clientOptions.CosmosClientTelemetryOptions.DisableDistributedTracing = false;

            if (CosmosUtils.IsEmulatorConnectionString(settings.ConnectionString))
            {
                clientOptions.ConnectionMode = ConnectionMode.Gateway;
                clientOptions.LimitToEndpoint = true;
            }

            configureClientOptions?.Invoke(serviceProvider, clientOptions);

            var cosmosApplicationName = CosmosConstants.CosmosApplicationName;
            if (!string.IsNullOrEmpty(clientOptions.ApplicationName))
            {
                cosmosApplicationName = $"{cosmosApplicationName}/{clientOptions.ApplicationName}";
            }

            clientOptions.ApplicationName = cosmosApplicationName;

            return GetCosmosClient(connectionName, settings, clientOptions);
        });
    }

    internal static CosmosConnectionInfo? GetCosmosConnectionInfo(this IHostApplicationBuilder builder, string connectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var connectionString = builder.Configuration.GetConnectionString(connectionName);
        if (string.IsNullOrEmpty(connectionString))
        {
            return null;
        }

        return CosmosUtils.ParseConnectionString(connectionString);
    }

    private static MicrosoftAzureCosmosSettings GetSettings(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<MicrosoftAzureCosmosSettings>? configureSettings
    )
    {
        var cosmosConnectionInfo = GetCosmosConnectionInfo(builder, connectionName);
        var settings = new MicrosoftAzureCosmosSettings();
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);

        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (cosmosConnectionInfo is { AccountEndpoint: { } accountEndpoint })
        {
            settings.AccountEndpoint = accountEndpoint;
        }
        else if (cosmosConnectionInfo is { ConnectionString: { } connectionString })
        {
            settings.ConnectionString = connectionString;
        }
        settings.DatabaseName = cosmosConnectionInfo?.DatabaseName;
        settings.ContainerName = cosmosConnectionInfo?.ContainerName;

        configureSettings?.Invoke(settings);

        return settings;
    }

    private static Action<IServiceProvider, CosmosClientOptions>? Wrap(Action<CosmosClientOptions>? action)
    {
        if (action is null)
        {
            return null;
        }

        return (_, clientOptions) => action(clientOptions);
    }

    internal static CosmosClient GetCosmosClient(string connectionName, MicrosoftAzureCosmosSettings settings, CosmosClientOptions clientOptions)
    {
        if (!string.IsNullOrEmpty(settings.ConnectionString))
        {
            return new CosmosClient(settings.ConnectionString, clientOptions);
        }
        else if (settings.AccountEndpoint is not null)
        {
            var credential = settings.Credential ?? AzureCredentialHelper.CreateDefaultAzureCredential();
            return new CosmosClient(settings.AccountEndpoint.OriginalString, credential, clientOptions);
        }
        else
        {
            throw new InvalidOperationException(
                    $"A CosmosClient could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:{connectionName}' or either " +
                    $"{nameof(settings.ConnectionString)} or {nameof(settings.AccountEndpoint)} must be provided " +
                    $"in the '{DefaultConfigSectionName}' or '{DefaultConfigSectionName}:{connectionName}' configuration section.");
        }
    }
}
