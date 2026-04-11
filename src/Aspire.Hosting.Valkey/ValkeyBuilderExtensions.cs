// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Valkey;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Valkey resources to the application model.
/// </summary>
public static class ValkeyBuilderExtensions
{
    private const string ValkeyContainerDataDirectory = "/data";

    /// <summary>
    /// Adds a Valkey container to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="ValkeyContainerImageTags.Tag"/> tag of the <inheritdoc cref="ValkeyContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port to bind the underlying container to.</param>
    /// <remarks>
    /// This overload is not available in polyglot app hosts. Use <see cref="AddValkey(IDistributedApplicationBuilder, string, int?, IResourceBuilder{ParameterResource})"/> instead.
    ///
    /// <example>
    /// Use in application host
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var valkey = builder.AddValkey("valkey");
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api)
    ///                  .WithReference(valkey);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// <example>
    /// Use in service project with Aspire.StackExchange.Redis package.
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddRedisClient("valkey");
    ///
    /// var multiplexer = builder.Services.BuildServiceProvider()
    ///                                   .GetRequiredService&lt;IConnectionMultiplexer&gt;();
    ///
    /// var db = multiplexer.GetDatabase();
    /// db.HashSet("key", [new HashEntry("hash", "value")]);
    /// var value = db.HashGet("key", "hash");
    /// </code>
    /// </example>
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// check this
    [AspireExportIgnore(Reason = "Convenience overload. Use the overload with optional password parameter instead.")]
    public static IResourceBuilder<ValkeyResource> AddValkey(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        return builder.AddValkey(name, port, null);
    }

    /// <summary>
    /// Adds a Valkey container to the application model.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="ValkeyContainerImageTags.Tag"/> tag of the <inheritdoc cref="ValkeyContainerImageTags.Image"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port to bind the underlying container to.</param>
    /// <param name="password">The parameter used to provide the password for the Valkey resource. If <see langword="null"/> a random password will be generated.</param>
    /// <remarks>
    /// <example>
    /// Use in application host
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var valkey = builder.AddValkey("valkey");
    /// var api = builder.AddProject&lt;Projects.Api&gt;("api)
    ///                  .WithReference(valkey);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    /// <example>
    /// Use in service project with Aspire.StackExchange.Redis package.
    /// <code lang="csharp">
    /// var builder = WebApplication.CreateBuilder(args);
    /// builder.AddRedisClient("valkey");
    ///
    /// var multiplexer = builder.Services.BuildServiceProvider()
    ///                                   .GetRequiredService&lt;IConnectionMultiplexer&gt;();
    ///
    /// var db = multiplexer.GetDatabase();
    /// db.HashSet("key", [new HashEntry("hash", "value")]);
    /// var value = db.HashGet("key", "hash");
    /// </code>
    /// </example>
    /// </remarks>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a Valkey container resource")]
    public static IResourceBuilder<ValkeyResource> AddValkey(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        // StackExchange.Redis doesn't support passwords with commas.
        // See https://github.com/StackExchange/StackExchange.Redis/issues/680 and
        // https://github.com/Azure/azure-dev/issues/4848 
        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var valkey = new ValkeyResource(name, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(valkey, async (@event, ct) =>
        {
            connectionString = await valkey.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);

            if (connectionString is null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{valkey.Name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks()
            .AddRedis(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        return builder.AddResource(valkey)
            .WithEndpoint(port: port, targetPort: 6379, name: ValkeyResource.PrimaryEndpointName)
            .WithImage(ValkeyContainerImageTags.Image, ValkeyContainerImageTags.Tag)
            .WithImageRegistry(ValkeyContainerImageTags.Registry)
            .WithHealthCheck(healthCheckKey)
            // see https://github.com/microsoft/aspire/issues/3838 for why the password is passed this way
            .WithEntrypoint("/bin/sh")
            .WithEnvironment(context =>
            {
                if (valkey.PasswordParameter is { } password)
                {
                    context.EnvironmentVariables["VALKEY_PASSWORD"] = password;
                }
            })
            .WithArgs(context =>
            {
                var valkeyCommand = new List<string>
                {
                    "valkey-server"
                };

                if (valkey.PasswordParameter is not null)
                {
                    valkeyCommand.Add("--requirepass");
                    valkeyCommand.Add("$VALKEY_PASSWORD");
                }

                if (valkey.TryGetLastAnnotation<PersistenceAnnotation>(out var persistenceAnnotation))
                {
                    var interval = (persistenceAnnotation.Interval ?? TimeSpan.FromSeconds(60)).TotalSeconds.ToString(CultureInfo.InvariantCulture);

                    valkeyCommand.Add("--save");
                    valkeyCommand.Add(interval);
                    valkeyCommand.Add(persistenceAnnotation.KeysChangedThreshold.ToString(CultureInfo.InvariantCulture));
                }

                context.Args.Add("-c");
                context.Args.Add(string.Join(' ', valkeyCommand));

                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a Valkey container resource and enables Valkey persistence.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">
    /// A flag that indicates if this is a read-only volume. Setting this to <c>true</c> will disable Valkey persistence.<br/>
    /// Defaults to <c>false</c>.
    /// </param>
    /// <remarks>
    /// <example>
    /// Use <see cref="WithPersistence(IResourceBuilder{ValkeyResource}, TimeSpan?, long)"/> to adjust Valkey persistence configuration, e.g.:
    /// <code lang="csharp">
    /// var cache = builder.AddValkey("cache")
    ///                    .WithDataVolume()
    ///                    .WithPersistence(TimeSpan.FromSeconds(10), 5);
    /// </code>
    /// </example>
    /// </remarks>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data volume for Valkey and enables persistence")]
    public static IResourceBuilder<ValkeyResource> WithDataVolume(
        this IResourceBuilder<ValkeyResource> builder,
        string? name = null,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), ValkeyContainerDataDirectory,
            isReadOnly);
        if (!isReadOnly)
        {
            builder.WithPersistence();
        }

        return builder;
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Valkey container resource and enables Valkey persistence.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">
    /// A flag that indicates if this is a read-only mount. Setting this to <c>true</c> will disable Valkey persistence.<br/>
    /// Defaults to <c>false</c>.
    /// </param>
    /// <remarks>
    /// <example>
    /// Use <see cref="WithPersistence(IResourceBuilder{ValkeyResource}, TimeSpan?, long)"/> to adjust Valkey persistence configuration, e.g.:
    /// <code lang="csharp">
    /// var valkey = builder.AddValkey("valkey")
    ///                    .WithDataBindMount("mydata")
    ///                    .WithPersistence(TimeSpan.FromSeconds(10), 5);
    /// </code>
    /// </example>
    /// </remarks>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data bind mount for Valkey and enables persistence")]
    public static IResourceBuilder<ValkeyResource> WithDataBindMount(
        this IResourceBuilder<ValkeyResource> builder,
        string source,
        bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        builder.WithBindMount(source, ValkeyContainerDataDirectory, isReadOnly);
        if (!isReadOnly)
        {
            builder.WithPersistence();
        }

        return builder;
    }

    /// <summary>
    /// Configures a Valkey container resource for persistence.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="interval">The interval between snapshot exports. Defaults to 60 seconds.</param>
    /// <param name="keysChangedThreshold">The number of key change operations required to trigger a snapshot at the interval. Defaults to 1.</param>
    /// <remarks>
    /// <example>
    /// Use with <see cref="WithDataBindMount(IResourceBuilder{ValkeyResource}, string, bool)"/>
    /// or <see cref="WithDataVolume(IResourceBuilder{ValkeyResource}, string?, bool)"/> to persist Valkey data across sessions with custom persistence configuration, e.g.:
    /// <code lang="csharp">
    /// var cache = builder.AddValkey("cache")
    ///                    .WithDataVolume()
    ///                    .WithPersistence(TimeSpan.FromSeconds(10), 5);
    /// </code>
    /// </example>
    /// </remarks>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures Valkey persistence")]
    public static IResourceBuilder<ValkeyResource> WithPersistence(
        this IResourceBuilder<ValkeyResource> builder,
        TimeSpan? interval = null,
        long keysChangedThreshold = 1)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithAnnotation(
            new PersistenceAnnotation(interval, keysChangedThreshold), ResourceAnnotationMutationBehavior.Replace);
    }

    private sealed class PersistenceAnnotation(TimeSpan? interval, long keysChangedThreshold) : IResourceAnnotation
    {
        public TimeSpan? Interval => interval;
        public long KeysChangedThreshold => keysChangedThreshold;
    }
}
