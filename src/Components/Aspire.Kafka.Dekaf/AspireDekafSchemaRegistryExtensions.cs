// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire;
using Aspire.Kafka.Dekaf;
using Dekaf.SchemaRegistry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for registering Dekaf Schema Registry clients.
/// </summary>
public static class AspireDekafSchemaRegistryExtensions
{
    /// <summary>
    /// Registers <see cref="ISchemaRegistryClient"/> as a singleton with a registry connectivity health check.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="connectionName">The name of the connection string containing one or more comma-separated registry URLs.</param>
    /// <param name="configureSettings">Optional settings customization.</param>
    /// <param name="clientFactory">Optional factory for a client owned and disposed by the service provider.</param>
    /// <remarks>
    /// Reads settings from <c>Aspire:Kafka:Dekaf:SchemaRegistry</c> and its named subsection.
    /// Resolve the client in producer and consumer callbacks to use Dekaf's JSON, Avro, or Protobuf serializers.
    /// The health check lists subjects without registering or modifying schemas.
    /// <code>builder.AddDekafSchemaRegistryClient("schema-registry");</code>
    /// </remarks>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddDekafSchemaRegistryClient(this IHostApplicationBuilder builder, string connectionName,
        Action<SchemaRegistrySettings>? configureSettings = null,
        Func<IServiceProvider, SchemaRegistryConfig, ISchemaRegistryClient>? clientFactory = null)
        => AddSchemaRegistry(builder, connectionName, serviceKey: null, configureSettings, clientFactory);

    /// <summary>
    /// Registers <see cref="ISchemaRegistryClient"/> as a keyed singleton with a registry connectivity health check.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="name">The service key and connection string name.</param>
    /// <param name="configureSettings">Optional settings customization.</param>
    /// <param name="clientFactory">Optional factory for a client owned and disposed by the service provider.</param>
    /// <remarks>
    /// Reads settings from <c>Aspire:Kafka:Dekaf:SchemaRegistry</c> and its named subsection.
    /// The health check uses the client registered with the same service key.
    /// <code>builder.AddKeyedDekafSchemaRegistryClient("schema-registry");</code>
    /// </remarks>
    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    public static void AddKeyedDekafSchemaRegistryClient(this IHostApplicationBuilder builder, string name,
        Action<SchemaRegistrySettings>? configureSettings = null,
        Func<IServiceProvider, SchemaRegistryConfig, ISchemaRegistryClient>? clientFactory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        AddSchemaRegistry(builder, name, serviceKey: name, configureSettings, clientFactory);
    }

    [RequiresDynamicCode("Dekaf configuration binding requires dynamic code.")]
    [RequiresUnreferencedCode("Dekaf configuration binding requires unreferenced members to be preserved.")]
    private static void AddSchemaRegistry(IHostApplicationBuilder builder, string connectionName, string? serviceKey,
        Action<SchemaRegistrySettings>? configureSettings,
        Func<IServiceProvider, SchemaRegistryConfig, ISchemaRegistryClient>? clientFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var configuration = DekafKafkaCommon.GetConfiguration(builder, "Aspire:Kafka:Dekaf:SchemaRegistry", connectionName);
        if (builder.Configuration.GetConnectionString(connectionName) is { } connectionString)
        {
            // Native options are init-only, so apply the connection before binding. Dekaf gives
            // Config:Urls precedence over Config:Url; remove that list so a connection string wins.
            // Nulling its indexed keys would bind an array containing nulls. Both URL forms support
            // failover: https://registry-1:8081,https://registry-2:8081.
            configuration = new ConfigurationBuilder().AddInMemoryCollection(configuration.AsEnumerable()
                .Where(entry => !entry.Key.Equals("Config:Urls", StringComparison.OrdinalIgnoreCase)
                    && !entry.Key.StartsWith("Config:Urls:", StringComparison.OrdinalIgnoreCase))).Build();
            configuration["Config:Url"] = connectionString;
        }

        var settings = configuration.Get<SchemaRegistrySettings>() ?? new();
        configureSettings?.Invoke(settings);

        ISchemaRegistryClient CreateClient(IServiceProvider services)
            => clientFactory is null ? new SchemaRegistryClient(settings.Config) : clientFactory(services, settings.Config);

        if (serviceKey is null)
        {
            builder.Services.AddSingleton(CreateClient);
        }
        else
        {
            builder.Services.AddKeyedSingleton<ISchemaRegistryClient>(serviceKey, (services, _) => CreateClient(services));
        }

        if (!settings.DisableHealthChecks)
        {
            builder.TryAddHealthCheck(new HealthCheckRegistration(
                DekafKafkaCommon.GetHealthCheckName("schema_registry", serviceKey),
                services => new SchemaRegistryHealthCheck(serviceKey is null
                    ? services.GetRequiredService<ISchemaRegistryClient>()
                    : services.GetRequiredKeyedService<ISchemaRegistryClient>(serviceKey)),
                failureStatus: default, tags: default, timeout: settings.HealthCheckTimeout));
        }
    }
}
