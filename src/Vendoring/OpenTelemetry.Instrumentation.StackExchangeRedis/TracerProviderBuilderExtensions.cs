// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenTelemetry.Instrumentation.StackExchangeRedis;
using OpenTelemetry.Internal;
using StackExchange.Redis;

namespace OpenTelemetry.Trace;

/// <summary>
/// Extension methods to simplify registering of dependency instrumentation.
/// </summary>
internal static class TracerProviderBuilderExtensions
{
    /// <summary>
    /// Enables automatic data collection of outgoing requests to Redis.
    /// </summary>
    /// <remarks>
    /// Note: A <see cref="IConnectionMultiplexer"/> will be resolved using the
    /// application <see cref="IServiceProvider"/>.
    /// </remarks>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddRedisInstrumentation(
        this TracerProviderBuilder builder)
        => AddRedisInstrumentation(builder, name: null, connection: null, configure: null, serviceKey: null);

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Redis.
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="connection"><see cref="IConnectionMultiplexer"/> to instrument.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddRedisInstrumentation(
        this TracerProviderBuilder builder,
        IConnectionMultiplexer connection)
    {
        Guard.ThrowIfNull(connection);

        return AddRedisInstrumentation(builder, name: null, connection, configure: null, serviceKey: null);
    }

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Redis.
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="serviceKey">Optional service key used to retrieve the <see cref="IConnectionMultiplexer"/> to instrument from the <see cref="IServiceProvider" />.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddRedisInstrumentation(
        this TracerProviderBuilder builder,
        object serviceKey)
    {
        Guard.ThrowIfNull(serviceKey);

        return AddRedisInstrumentation(builder, name: null, connection: null, serviceKey, configure: null);
    }

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Redis.
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="name">Optional name which is used when retrieving options.</param>
    /// <param name="serviceKey">Optional service key used to retrieve the <see cref="IConnectionMultiplexer"/> to instrument from the <see cref="IServiceProvider" />.</param>
    /// <param name="configure">Callback to configure options.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddRedisInstrumentation(
        this TracerProviderBuilder builder,
        string? name,
        object serviceKey,
        Action<StackExchangeRedisInstrumentationOptions> configure)
    {
        Guard.ThrowIfNull(serviceKey);
        Guard.ThrowIfNull(configure);

        return AddRedisInstrumentation(builder, name: name, connection: null, serviceKey, configure);
    }

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Redis.
    /// </summary>
    /// <remarks>
    /// Note: A <see cref="IConnectionMultiplexer"/> will be resolved using the
    /// application <see cref="IServiceProvider"/>.
    /// </remarks>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="configure">Callback to configure options.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddRedisInstrumentation(
        this TracerProviderBuilder builder,
        Action<StackExchangeRedisInstrumentationOptions> configure)
    {
        Guard.ThrowIfNull(configure);

        return AddRedisInstrumentation(builder, name: null, connection: null, serviceKey: null, configure);
    }

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Redis.
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="connection"><see cref="IConnectionMultiplexer"/> to instrument.</param>
    /// <param name="configure">Callback to configure options.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddRedisInstrumentation(
        this TracerProviderBuilder builder,
        IConnectionMultiplexer connection,
        Action<StackExchangeRedisInstrumentationOptions> configure)
    {
        Guard.ThrowIfNull(connection);
        Guard.ThrowIfNull(configure);

        return AddRedisInstrumentation(builder, name: null, connection, serviceKey: null, configure);
    }

    /// <summary>
    /// Enables automatic data collection of outgoing requests to Redis.
    /// </summary>
    /// <remarks>
    /// Note: If an <see cref="IConnectionMultiplexer"/> is not supplied
    /// using the <paramref name="connection"/> parameter it will be
    /// resolved using the application <see cref="IServiceProvider"/>.
    /// </remarks>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="name">Optional name which is used when retrieving options.</param>
    /// <param name="connection">Optional <see cref="IConnectionMultiplexer"/> to instrument.</param>
    /// <param name="serviceKey">Optional service key used to retrieve the <see cref="IConnectionMultiplexer"/> to instrument from the <see cref="IServiceProvider" />.</param>
    /// <param name="configure">Optional callback to configure options.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddRedisInstrumentation(
        this TracerProviderBuilder builder,
        string? name,
        IConnectionMultiplexer? connection,
        object? serviceKey,
        Action<StackExchangeRedisInstrumentationOptions>? configure)
    {
        Guard.ThrowIfNull(builder);

        name ??= Options.DefaultName;

        builder.AddRedisInstrumentationSharedServices();

        if (configure != null)
        {
            builder.ConfigureServices(services =>
            {
                services.Configure(name, configure);
            });
        }

        return builder
            .AddSource(StackExchangeRedisConnectionInstrumentation.ActivitySource.Name)
            .AddInstrumentation(sp =>
            {
                var instrumentation = sp.GetRequiredService<StackExchangeRedisInstrumentation>();

                connection ??= serviceKey == null
                    ? sp.GetService<IConnectionMultiplexer>()
                    : sp.GetKeyedService<IConnectionMultiplexer>(serviceKey);

                if (connection != null)
                {
                    instrumentation.AddConnection(name, connection);
                }

                return instrumentation;
            });
    }

    /// <summary>
    /// Registers a callback for configuring Redis instrumentation.
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="configure">Callback to configure instrumentation.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder ConfigureRedisInstrumentation(
        this TracerProviderBuilder builder,
        Action<StackExchangeRedisInstrumentation> configure)
    {
        Guard.ThrowIfNull(configure);

        return ConfigureRedisInstrumentation(builder, (sp, instrumentation) => configure(instrumentation));
    }

    /// <summary>
    /// Registers a callback for configuring Redis instrumentation.
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="configure">Callback to configure instrumentation.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder ConfigureRedisInstrumentation(
        this TracerProviderBuilder builder,
        Action<IServiceProvider, StackExchangeRedisInstrumentation> configure)
    {
        Guard.ThrowIfNull(configure);

        if (builder is not IDeferredTracerProviderBuilder deferredTracerProviderBuilder)
        {
            throw new NotSupportedException("ConfigureRedisInstrumentation is not supported on the supplied builder type.");
        }

        builder.AddRedisInstrumentationSharedServices();

        deferredTracerProviderBuilder.Configure(
            (sp, builder) => configure(sp, sp.GetRequiredService<StackExchangeRedisInstrumentation>()));

        return builder;
    }

    private static TracerProviderBuilder AddRedisInstrumentationSharedServices(
        this TracerProviderBuilder builder)
    {
        Guard.ThrowIfNull(builder);

        return builder.ConfigureServices(services =>
        {
            services.TryAddSingleton(
                sp => new StackExchangeRedisInstrumentation(
                    sp.GetRequiredService<IOptionsMonitor<StackExchangeRedisInstrumentationOptions>>()));
        });
    }
}
