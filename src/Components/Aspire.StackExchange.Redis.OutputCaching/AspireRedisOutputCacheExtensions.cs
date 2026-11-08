// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.StackExchange.Redis;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.OutputCaching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Provides extension methods for adding Redis output caching services to the <see cref="IHostApplicationBuilder"/>.
/// </summary>
public static class AspireRedisOutputCacheExtensions
{
    /// <summary>
    /// Adds Redis output caching services in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="StackExchangeRedisSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional method that can be used for customizing the <see cref="ConfigurationOptions"/>. It's invoked after the options are read from the configuration.</param>
    /// <remarks>
    /// Reads the configuration from "Aspire:StackExchange:Redis" section.
    ///
    /// Also registers <see cref="IConnectionMultiplexer"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging, and telemetry.
    /// </remarks>
    public static void AddRedisOutputCache(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<StackExchangeRedisSettings>? configureSettings = null,
        Action<ConfigurationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        builder.AddRedisClient(connectionName, configureSettings, configureOptions);

        builder.AddRedisOutputCacheCore((RedisOutputCacheOptions options, IServiceProvider sp) =>
        {
            options.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>());
        });
    }

    /// <summary>
    /// Adds Redis output caching services for the given <paramref name="name"/> in the services provided by the <paramref name="builder"/>.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> of the service and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional method that can be used for customizing the <see cref="StackExchangeRedisSettings"/>. It's invoked after the settings are read from the configuration.</param>
    /// <param name="configureOptions">An optional method that can be used for customizing the <see cref="ConfigurationOptions"/>. It's invoked after the options are read from the configuration.</param>
    /// <remarks>
    /// Reads the configuration from "Aspire:StackExchange:Redis:{name}" section.
    ///
    /// Also registers <see cref="IConnectionMultiplexer"/> as a singleton in the services provided by the <paramref name="builder"/>.
    /// Enables retries, corresponding health check, logging, and telemetry.
    /// </remarks>
    public static void AddKeyedRedisOutputCache(
        this IHostApplicationBuilder builder,
        string name,
        Action<StackExchangeRedisSettings>? configureSettings = null,
        Action<ConfigurationOptions>? configureOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        builder.AddKeyedRedisClient(name, configureSettings, configureOptions);

        builder.AddRedisOutputCacheCore((RedisOutputCacheOptions options, IServiceProvider sp) =>
        {
            options.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredKeyedService<IConnectionMultiplexer>(name));
        });
    }

    /// <summary>
    /// Configures the Redis client to provide output caching services through <see cref="IOutputCacheStore"/>.
    /// </summary>
    /// <param name="builder">The <see cref="AspireRedisClientBuilder"/> to configure.</param>
    /// <returns>The <see cref="AspireRedisClientBuilder"/> for method chaining.</returns>
    public static AspireRedisClientBuilder WithOutputCache(this AspireRedisClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.HostBuilder.AddRedisOutputCacheCore((RedisOutputCacheOptions options, IServiceProvider sp) =>
        {
            var key = builder.ServiceKey;
            if (key is null)
            {
                options.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredService<IConnectionMultiplexer>());
            }
            else
            {
                options.ConnectionMultiplexerFactory = () => Task.FromResult(sp.GetRequiredKeyedService<IConnectionMultiplexer>(key));
            }
        });

        return builder;
    }

    private static void AddRedisOutputCacheCore(this IHostApplicationBuilder builder, Action<RedisOutputCacheOptions, IServiceProvider> configureRedisOptions)
    {
        builder.Services.AddOutputCache();

        builder.Services.AddStackExchangeRedisOutputCache(static _ => { });

        builder.Services.AddOptions<RedisOutputCacheOptions>() // note that RedisOutputCacheOptions doesn't support named options
            .Configure(configureRedisOptions);
    }
}
