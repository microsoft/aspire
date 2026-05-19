// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Redis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREDOCKERFILEBUILDER001

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Redis resources to the application model.
/// </summary>
public static class RedisBuilderExtensions
{
    /// <summary>
    /// Adds a Redis container to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port to bind the underlying container to.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This resource includes built-in health checks. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the Redis resource is able to service
    /// requests.
    /// </para>
    /// This version of the package defaults to the <inheritdoc cref="RedisContainerImageTags.Tag"/> tag of the <inheritdoc cref="RedisContainerImageTags.Image"/> container image.
    /// </remarks>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the canonical addRedis export with options.")]
    public static IResourceBuilder<RedisResource> AddRedis(this IDistributedApplicationBuilder builder, [ResourceName] string name, int? port)
    {
        return builder.AddRedis(name, port, null);
    }

    /// <summary>
    /// Adds a Redis container to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource. This name will be used as the connection string name when referenced in a dependency.</param>
    /// <param name="port">The host port to bind the underlying container to.</param>
    /// <param name="password">The parameter used to provide the password for the Redis resource. If <see langword="null"/> a random password will be generated.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This resource includes built-in health checks. When this resource is referenced as a dependency
    /// using the <see cref="ResourceBuilderExtensions.WaitFor{T}(IResourceBuilder{T}, IResourceBuilder{IResource})"/>
    /// extension method then the dependent resource will wait until the Redis resource is able to service
    /// requests.
    /// </para>
    /// This version of the package defaults to the <inheritdoc cref="RedisContainerImageTags.Tag"/> tag of the <inheritdoc cref="RedisContainerImageTags.Image"/> container image.
    /// </remarks>
    [AspireExport(Description = "Adds a Redis container resource")]
    public static IResourceBuilder<RedisResource> AddRedis(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        int? port = null,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);

        // StackExchange.Redis doesn't support passwords with commas.
        // See https://github.com/StackExchange/StackExchange.Redis/issues/680 and
        // https://github.com/Azure/azure-dev/issues/4848
        var passwordParameter = password?.Resource ?? ParameterResourceBuilderExtensions.CreateDefaultPasswordParameter(builder, $"{name}-password", special: false);

        var redis = new RedisResource(name, passwordParameter);

        string? connectionString = null;

        builder.Eventing.Subscribe<ConnectionStringAvailableEvent>(redis, async (@event, ct) =>
        {
            connectionString = await redis.GetConnectionStringAsync(ct).ConfigureAwait(false);

            if (connectionString == null)
            {
                throw new DistributedApplicationException($"ConnectionStringAvailableEvent was published for the '{redis.Name}' resource but the connection string was null.");
            }
        });

        var healthCheckKey = $"{name}_check";
        builder.Services.AddHealthChecks().AddRedis(sp => connectionString ?? throw new InvalidOperationException("Connection string is unavailable"), name: healthCheckKey);

        var redisBuilder = builder.AddResource(redis)
            .WithEndpoint(port: port, targetPort: 6379, name: RedisResource.PrimaryEndpointName, scheme: RedisResource.StandardRedisScheme)
            .WithImage(RedisContainerImageTags.Image, RedisContainerImageTags.Tag)
            .WithImageRegistry(RedisContainerImageTags.Registry)
            .WithHealthCheck(healthCheckKey)
            // see https://github.com/microsoft/aspire/issues/3838 for why the password is passed this way
            .WithEntrypoint("/bin/sh")
            .WithEnvironment(context =>
            {
                if (redis.PasswordParameter is { } password)
                {
                    context.EnvironmentVariables["REDIS_PASSWORD"] = password;
                }
            })
            .WithArgs(context =>
            {
                var additionalArgs = new List<string>
                {
                    "redis-server"
                };

                if (redis.PasswordParameter is not null)
                {
                    additionalArgs.Add("--requirepass");
                    additionalArgs.Add("$REDIS_PASSWORD");
                }

                if (redis.TryGetLastAnnotation<PersistenceAnnotation>(out var persistenceAnnotation))
                {
                    var interval = (persistenceAnnotation.Interval ?? TimeSpan.FromSeconds(60)).TotalSeconds.ToString(CultureInfo.InvariantCulture);

                    additionalArgs.Add("--save");
                    additionalArgs.Add(interval);
                    additionalArgs.Add(persistenceAnnotation.KeysChangedThreshold.ToString(CultureInfo.InvariantCulture));
                }

                // This is a temporary workaround to allow the args list to be expanded dynamically at run time with additional server certificate arguments.
                if (context.ExecutionContext.IsRunMode)
                {
                    foreach (var arg in additionalArgs)
                    {
                        context.Args.Add(arg);
                    }
                }
                else
                {
                    context.Args.Add("-c");
                    context.Args.Add(string.Join(' ', additionalArgs));
                }

                return Task.CompletedTask;
            })
            .WithCertificateTrustConfiguration(ctx =>
            {
                ctx.Arguments.Add("--tls-ca-cert-file");
                ctx.Arguments.Add(ctx.CertificateBundlePath);

                return Task.CompletedTask;
            })
            .WithHttpsCertificateConfiguration(ctx =>
            {
                ctx.Arguments.Add("--tls-cert-file");
                ctx.Arguments.Add(ctx.CertificatePath);
                ctx.Arguments.Add("--tls-key-file");
                ctx.Arguments.Add(ctx.KeyPath);

                if (ctx.ExecutionContext.IsRunMode)
                {
                    // TODO: Expose this as new API
                    ctx.Arguments.Add("--tls-auth-clients");
                    ctx.Arguments.Add("no");
                }

                if (ctx.Password is not null)
                {
                    var resourceLogger = ctx.ExecutionContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                    var logger = resourceLogger.GetLogger(redis);
                    logger.LogError($"Cannot configure an encrypted certificate for redis resource '{redis.Name}'");
                }

                return Task.CompletedTask;
            });

        if (builder.ExecutionContext.IsRunMode)
        {
            redisBuilder.SubscribeHttpsEndpointsUpdate(ctx =>
            {
                // If a TLS certificate is configured, ensure the Redis resource has an HTTPS endpoint and
                // configure the environment variables to use it.
                redisBuilder
                    .WithEndpoint(targetPort: 6380, name: RedisResource.SecondaryEndpointName)
                    .WithEndpoint(RedisResource.PrimaryEndpointName, e =>
                    {
                        e.UriScheme = RedisResource.TlsRedisScheme;
                        e.TlsEnabled = true;
                    })
                    .WithArgs(argsCtx =>
                    {
                        argsCtx.Args.Add("--tls-port");
                        argsCtx.Args.Add(redis.GetEndpoint(RedisResource.PrimaryEndpointName).Property(EndpointProperty.Port));
                        argsCtx.Args.Add("--port");
                        argsCtx.Args.Add(redis.GetEndpoint(RedisResource.SecondaryEndpointName).Property(EndpointProperty.Port));
                    });
            });
        }

        return redisBuilder;
    }

    /// <summary>
    /// Configures a container resource for Redis Commander which is pre-configured to connect to the <see cref="RedisResource"/> that this method is used on.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="RedisContainerImageTags.RedisCommanderTag"/> tag of the <inheritdoc cref="RedisContainerImageTags.RedisCommanderImage"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for the <see cref="RedisResource"/>.</param>
    /// <param name="configureContainer">Configuration callback for Redis Commander container resource.</param>
    /// <param name="containerName">Override the container name used for Redis Commander.</param>
    /// <returns></returns>
    [AspireExport(Description = "Adds Redis Commander management UI", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RedisResource> WithRedisCommander(this IResourceBuilder<RedisResource> builder, Action<IResourceBuilder<RedisCommanderResource>>? configureContainer = null, string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.Resources.OfType<RedisCommanderResource>().SingleOrDefault() is { } existingRedisCommanderResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existingRedisCommanderResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= "rediscommander";

            var resource = new RedisCommanderResource(containerName);
            var resourceBuilder = builder.ApplicationBuilder.AddResource(resource)
                                      .WithImage(RedisContainerImageTags.RedisCommanderImage, RedisContainerImageTags.RedisCommanderTag)
                                      .WithImageRegistry(RedisContainerImageTags.RedisCommanderRegistry)
                                      .WithHttpEndpoint(targetPort: 8081, name: "http")
                                      .ExcludeFromManifest();

            builder.ApplicationBuilder.Eventing.Subscribe<BeforeResourceStartedEvent>(resource, async (e, ct) =>
            {
                var redisInstances = builder.ApplicationBuilder.Resources.OfType<RedisResource>();

                if (!redisInstances.Any())
                {
                    // No-op if there are no Redis resources present.
                    return;
                }

                var hostsVariableBuilder = new StringBuilder();

                foreach (var redisInstance in redisInstances)
                {
                    // Can't configure redis commander image for HTTPS via environment variables
                    var endpoint = redisInstance.PrimaryEndpoint;
                    if (redisInstance.TryGetEndpoints(out var endpoints))
                    {
                        var secondaryEndpoint = endpoints.FirstOrDefault(ep => string.Equals(ep.Name, RedisResource.SecondaryEndpointName, StringComparisons.EndpointAnnotationName));
                        if (secondaryEndpoint is not null)
                        {
                            endpoint = new EndpointReference(redisInstance, secondaryEndpoint);
                        }
                    }
                    // Redis Commander assumes Redis is being accessed over a default Aspire container network and hardcodes the resource address
                    // This will need to be refactored once updated service discovery APIs are available
                    var hostString = $"{(hostsVariableBuilder.Length > 0 ? "," : string.Empty)}{redisInstance.Name}:{redisInstance.Name}:{endpoint.TargetPort}:0";
                    if (redisInstance.PasswordParameter is not null)
                    {
                        var password = await redisInstance.PasswordParameter.GetValueAsync(ct).ConfigureAwait(false);
                        hostString += $":{password}";
                    }
                    hostsVariableBuilder.Append(hostString);
                }

                resourceBuilder.WithEnvironment("REDIS_HOSTS", hostsVariableBuilder.ToString());
            });

            configureContainer?.Invoke(resourceBuilder);

            resourceBuilder.WithRelationship(builder.Resource, "RedisCommander");

            return builder;
        }
    }

    /// <summary>
    /// Configures a container resource for Redis Insight which is pre-configured to connect to the <see cref="RedisResource"/> that this method is used on.
    /// </summary>
    /// <remarks>
    /// This version of the package defaults to the <inheritdoc cref="RedisContainerImageTags.RedisInsightTag"/> tag of the <inheritdoc cref="RedisContainerImageTags.RedisInsightImage"/> container image.
    /// </remarks>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> for the <see cref="RedisResource"/>.</param>
    /// <param name="configureContainer">Configuration callback for Redis Insight container resource.</param>
    /// <param name="containerName">Override the container name used for Redis Insight.</param>
    /// <returns></returns>
    [AspireExport(Description = "Adds Redis Insight management UI", RunSyncOnBackgroundThread = true)]
    public static IResourceBuilder<RedisResource> WithRedisInsight(this IResourceBuilder<RedisResource> builder, Action<IResourceBuilder<RedisInsightResource>>? configureContainer = null, string? containerName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.ApplicationBuilder.Resources.OfType<RedisInsightResource>().SingleOrDefault() is { } existingRedisCommanderResource)
        {
            var builderForExistingResource = builder.ApplicationBuilder.CreateResourceBuilder(existingRedisCommanderResource);
            configureContainer?.Invoke(builderForExistingResource);
            return builder;
        }
        else
        {
            containerName ??= "redisinsight";

            var resource = new RedisInsightResource(containerName);
            var resourceBuilder = builder.ApplicationBuilder.AddResource(resource)
                .WithImage(RedisContainerImageTags.RedisInsightImage, RedisContainerImageTags.RedisInsightTag)
                .WithImageRegistry(RedisContainerImageTags.RedisInsightRegistry)
                .WithHttpEndpoint(targetPort: 5540, name: "http")
                .WithEnvironment(context =>
                {
                    var redisInstances = builder.ApplicationBuilder.Resources.OfType<RedisResource>();

                    if (!redisInstances.Any())
                    {
                        // No-op if there are no Redis resources present.
                        return;
                    }

                    var counter = 1;

                    foreach (var redisInstance in redisInstances)
                    {
                        // RedisInsight assumes Redis is being accessed over a default Aspire container network and hardcodes the resource address
                        context.EnvironmentVariables[$"RI_REDIS_HOST{counter}"] = redisInstance.PrimaryEndpoint.Property(EndpointProperty.Host);
                        context.EnvironmentVariables[$"RI_REDIS_PORT{counter}"] = redisInstance.PrimaryEndpoint.TargetPort!.Value;
                        context.EnvironmentVariables[$"RI_REDIS_ALIAS{counter}"] = redisInstance.Name;
                        if (redisInstance.TlsEnabled)
                        {
                            context.EnvironmentVariables[$"RI_REDIS_TLS{counter}"] = "true";
                        }
                        if (redisInstance.PasswordParameter is not null)
                        {
                            context.EnvironmentVariables[$"RI_REDIS_PASSWORD{counter}"] = redisInstance.PasswordParameter;
                        }

                        counter++;
                    }
                })
                .WithRelationship(builder.Resource, "RedisInsight")
                .WithCertificateTrustConfiguration(ctx =>
                {
                    var redisInstances = builder.ApplicationBuilder.Resources.OfType<RedisResource>();

                    var counter = 1;

                    foreach (var redisInstance in redisInstances)
                    {
                        // Configure the TLS bundle for each connection
                        ctx.EnvironmentVariables[$"RI_REDIS_TLS_CA_PATH{counter}"] = ctx.CertificateBundlePath;

                        counter++;
                    }

                    return Task.CompletedTask;
                })
                .WithHttpsCertificateConfiguration(ctx =>
                {
                    ctx.EnvironmentVariables["RI_SERVER_TLS_CERT"] = ctx.CertificatePath;
                    ctx.EnvironmentVariables["RI_SERVER_TLS_KEY"] = ctx.KeyPath;

                    if (ctx.Password != null)
                    {
                        var resourceLogger = ctx.ExecutionContext.ServiceProvider.GetRequiredService<ResourceLoggerService>();
                        var logger = resourceLogger.GetLogger(resource);
                        logger.LogError($"Cannot configure an encrypted certificate for redis insight resource '{resource.Name}'");
                    }

                    return Task.CompletedTask;
                })
                .ExcludeFromManifest();

            resourceBuilder.SubscribeHttpsEndpointsUpdate(ctx =>
            {
                // If a TLS certificate is configured, ensure the endpoint uses https scheme
                resourceBuilder.WithEndpoint("http", ep => ep.UriScheme = "https");
            });

            configureContainer?.Invoke(resourceBuilder);

            return builder;
        }
    }

    /// <summary>
    /// Configures the host port that the Redis Commander resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for Redis Commander.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for RedisCommander.</returns>
    [AspireExport("withRedisCommanderHostPort", MethodName = "withHostPort", Description = "Sets the host port for Redis Commander")]
    public static IResourceBuilder<RedisCommanderResource> WithHostPort(this IResourceBuilder<RedisCommanderResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Configures the host port that the Redis Insight resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder for Redis Insight.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The resource builder for RedisInsight.</returns>
    [AspireExport("withRedisInsightHostPort", MethodName = "withHostPort", Description = "Sets the host port for Redis Insight")]
    public static IResourceBuilder<RedisInsightResource> WithHostPort(this IResourceBuilder<RedisInsightResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithEndpoint("http", endpoint =>
        {
            endpoint.Port = port;
        });
    }

    /// <summary>
    /// Adds a named volume for the data folder to a Redis container resource and enables Redis persistence.
    /// </summary>
    /// <remarks>
    /// Use <see cref="WithPersistence(IResourceBuilder{RedisResource}, TimeSpan?, long)"/> to adjust Redis persistence configuration, e.g.:
    /// <code lang="csharp">
    /// var cache = builder.AddRedis("cache")
    ///                    .WithDataVolume()
    ///                    .WithPersistence(TimeSpan.FromSeconds(10), 5);
    /// </code>
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <param name="isReadOnly">
    /// A flag that indicates if this is a read-only volume. Setting this to <c>true</c> will disable Redis persistence.<br/>
    /// Defaults to <c>false</c>.
    /// </param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data volume with persistence")]
    public static IResourceBuilder<RedisResource> WithDataVolume(this IResourceBuilder<RedisResource> builder, string? name = null, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/data", isReadOnly);
        if (!isReadOnly)
        {
            builder.WithPersistence();
        }
        return builder;
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Redis container resource and enables Redis persistence.
    /// </summary>
    /// <remarks>
    /// Use <see cref="WithPersistence(IResourceBuilder{RedisResource}, TimeSpan?, long)"/> to adjust Redis persistence configuration, e.g.:
    /// <code lang="csharp">
    /// var cache = builder.AddRedis("cache")
    ///                    .WithDataBindMount("myredisdata")
    ///                    .WithPersistence(TimeSpan.FromSeconds(10), 5);
    /// </code>
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <param name="isReadOnly">
    /// A flag that indicates if this is a read-only mount. Setting this to <c>true</c> will disable Redis persistence.<br/>
    /// Defaults to <c>false</c>.
    /// </param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Adds a data bind mount with persistence")]
    public static IResourceBuilder<RedisResource> WithDataBindMount(this IResourceBuilder<RedisResource> builder, string source, bool isReadOnly = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        builder.WithBindMount(source, "/data", isReadOnly);
        if (!isReadOnly)
        {
            builder.WithPersistence();
        }
        return builder;
    }

    /// <summary>
    /// Configures a Redis container resource for persistence.
    /// </summary>
    /// <remarks>
    /// Use with <see cref="WithDataBindMount(IResourceBuilder{RedisResource}, string, bool)"/>
    /// or <see cref="WithDataVolume(IResourceBuilder{RedisResource}, string?, bool)"/> to persist Redis data across sessions with custom persistence configuration, e.g.:
    /// <code lang="csharp">
    /// var cache = builder.AddRedis("cache")
    ///                    .WithDataVolume()
    ///                    .WithPersistence(TimeSpan.FromSeconds(10), 5);
    /// </code>
    /// </remarks>
    /// <param name="builder">The resource builder.</param>
    /// <param name="interval">The interval between snapshot exports. Defaults to 60 seconds.</param>
    /// <param name="keysChangedThreshold">The number of key change operations required to trigger a snapshot at the interval. Defaults to 1.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures Redis persistence")]
    public static IResourceBuilder<RedisResource> WithPersistence(this IResourceBuilder<RedisResource> builder, TimeSpan? interval = null, long keysChangedThreshold = 1)
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

    /// <summary>
    /// Adds a named volume for the data folder to a Redis Insight container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="name">The name of the volume. Defaults to an auto-generated name based on the application and resource names.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Each overload targets a different resource builder type, allowing for tailored functionality. Optional volume names enhance usability, enabling users to easily provide custom names while maintaining clear and distinct method signatures.")]
    [AspireExport("withRedisInsightDataVolume", Description = "Adds a data volume for Redis Insight", MethodName = "withDataVolume")]
    public static IResourceBuilder<RedisInsightResource> WithDataVolume(this IResourceBuilder<RedisInsightResource> builder, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithVolume(name ?? VolumeNameGenerator.Generate(builder, "data"), "/data");
    }

    /// <summary>
    /// Adds a bind mount for the data folder to a Redis Insight container resource.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="source">The source directory on the host to mount into the container.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport("withRedisInsightDataBindMount", MethodName = "withDataBindMount", Description = "Adds a data bind mount for Redis Insight")]
    public static IResourceBuilder<RedisInsightResource> WithDataBindMount(this IResourceBuilder<RedisInsightResource> builder, string source)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(source);

        return builder.WithBindMount(source, "/data");
    }

    /// <summary>
    /// Configures the password that the Redis resource is used.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="password">The parameter used to provide the password for the Redis resource. If <see langword="null"/>, no password will be configured.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Configures the password for Redis")]
    public static IResourceBuilder<RedisResource> WithPassword(this IResourceBuilder<RedisResource> builder, IResourceBuilder<ParameterResource>? password)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SetPassword(password?.Resource);
        return builder;
    }

    /// <summary>
    /// Configures the host port that the Redis resource is exposed on instead of using randomly assigned port.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="port">The port to bind on the host. If <see langword="null"/> is used random port will be assigned.</param>
    /// <returns>The <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport(Description = "Sets the host port for Redis")]
    public static IResourceBuilder<RedisResource> WithHostPort(this IResourceBuilder<RedisResource> builder, int? port)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithEndpoint(RedisResource.PrimaryEndpointName, endpoint =>
        {
            endpoint.Port = port;
        });

    }
}
