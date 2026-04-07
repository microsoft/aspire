// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for <see cref="IDistributedApplicationBuilder"/> properties and related types.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="IDistributedApplicationBuilder"/> is the central type for defining Aspire applications.
/// This class exposes its properties (Configuration, Environment, AppHostDirectory) and provides
/// capabilities to interact with them when direct property export is insufficient.
/// </para>
/// <para>
/// <strong>Builder Properties:</strong>
/// <list type="bullet">
///   <item><description><c>Configuration</c> - Application configuration (connection strings, settings)</description></item>
///   <item><description><c>Environment</c> - Host environment info (property getters plus environment checks)</description></item>
///   <item><description><c>AppHostDirectory</c> - Directory containing the app host</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Lifecycle Events:</strong>
/// <list type="bullet">
///   <item><description><c>subscribeBeforeStart</c> - Called before the application starts</description></item>
///   <item><description><c>subscribeAfterResourcesCreated</c> - Called after resources are created</description></item>
/// </list>
/// </para>
/// </remarks>
internal static class BuilderExports
{
    // Note: Configuration, Environment, AppHostDirectory, and ExecutionContext are accessed via property getters
    // on IDistributedApplicationBuilder which has [AspireExport(ExposeProperties = true)].

    #region Configuration

    /// <summary>
    /// Gets the application configuration.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <returns>The configuration handle.</returns>
    [AspireExport(Description = "Gets the application configuration")]
    public static IConfiguration GetConfiguration(this IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Configuration;
    }

    /// <summary>
    /// Gets a configuration value by key.
    /// </summary>
    /// <param name="configuration">The configuration handle.</param>
    /// <param name="key">The configuration key (e.g., "ConnectionStrings:Default").</param>
    /// <returns>The configuration value, or null if not found.</returns>
    [AspireExport(Description = "Gets a configuration value by key")]
    public static string? GetConfigValue(this IConfiguration configuration, string key)
    {
        return configuration[key];
    }

    /// <summary>
    /// Gets a connection string by name.
    /// </summary>
    /// <param name="configuration">The configuration handle.</param>
    /// <param name="name">The connection string name.</param>
    /// <returns>The connection string value, or null if not found.</returns>
    [AspireExport(Description = "Gets a connection string by name")]
    public static string? GetConnectionString(this IConfiguration configuration, string name)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(name);

        return global::Microsoft.Extensions.Configuration.ConfigurationExtensions.GetConnectionString(configuration, name);
    }

    /// <summary>
    /// Gets a configuration section by key.
    /// </summary>
    /// <param name="configuration">The configuration handle.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns>The configuration section handle.</returns>
    [AspireExport(Description = "Gets a configuration section by key")]
    public static IConfigurationSection GetSection(this IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(key);

        return configuration.GetSection(key);
    }

    /// <summary>
    /// Gets the child sections of a configuration handle.
    /// </summary>
    /// <param name="configuration">The configuration handle.</param>
    /// <returns>The child sections.</returns>
    [AspireExport(Description = "Gets child configuration sections")]
    public static IConfigurationSection[] GetChildren(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return [.. configuration.GetChildren()];
    }

    /// <summary>
    /// Checks whether a configuration section exists.
    /// </summary>
    /// <param name="configuration">The configuration handle.</param>
    /// <param name="key">The configuration key.</param>
    /// <returns><see langword="true"/> when the section exists; otherwise, <see langword="false"/>.</returns>
    [AspireExport(Description = "Checks whether a configuration section exists")]
    public static bool Exists(this IConfiguration configuration, string key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(key);

        return configuration.GetSection(key).Exists();
    }

    #endregion

    #region Host Environment

    /// <summary>
    /// Checks if the environment is Development.
    /// </summary>
    /// <param name="environment">The host environment handle.</param>
    /// <returns>True if running in Development environment.</returns>
    [AspireExport(Description = "Checks if running in Development environment")]
    public static bool IsDevelopment(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return global::Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(environment);
    }

    /// <summary>
    /// Checks if the environment is Production.
    /// </summary>
    /// <param name="environment">The host environment handle.</param>
    /// <returns>True if running in Production environment.</returns>
    [AspireExport(Description = "Checks if running in Production environment")]
    public static bool IsProduction(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return global::Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsProduction(environment);
    }

    /// <summary>
    /// Checks if the environment is Staging.
    /// </summary>
    /// <param name="environment">The host environment handle.</param>
    /// <returns>True if running in Staging environment.</returns>
    [AspireExport(Description = "Checks if running in Staging environment")]
    public static bool IsStaging(this IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return global::Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsStaging(environment);
    }

    /// <summary>
    /// Checks if the environment matches the specified name.
    /// </summary>
    /// <param name="environment">The host environment handle.</param>
    /// <param name="environmentName">The environment name to compare against.</param>
    /// <returns>True if the environment matches the specified name.</returns>
    [AspireExport(Description = "Checks if the environment matches the specified name")]
    public static bool IsEnvironment(this IHostEnvironment environment, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(environmentName);

        return global::Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsEnvironment(environment, environmentName);
    }

    #endregion

    #region Lifecycle Events

    /// <summary>
    /// Subscribes to the BeforeStart event, which fires before the application starts.
    /// </summary>
    /// <remarks>
    /// This event provides access to the service provider and distributed application model,
    /// allowing you to perform final configuration or validation before resources start.
    /// </remarks>
    /// <param name="builder">The builder handle.</param>
    /// <param name="callback">A callback that receives the exported event when the event fires.</param>
    /// <returns>A subscription handle that can be used to unsubscribe.</returns>
    [AspireExport(Description = "Subscribes to the BeforeStart event")]
    public static DistributedApplicationEventSubscription SubscribeBeforeStart(
        this IDistributedApplicationBuilder builder,
        Func<BeforeStartEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, ct) =>
        {
            await callback(@event).ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Subscribes to the AfterResourcesCreated event, which fires after all resources are created.
    /// </summary>
    /// <remarks>
    /// At this point, all resources have been instantiated but may not yet be running.
    /// This is useful for performing cross-resource configuration.
    /// </remarks>
    /// <param name="builder">The builder handle.</param>
    /// <param name="callback">A callback that receives the exported event when the event fires.</param>
    /// <returns>A subscription handle that can be used to unsubscribe.</returns>
    [AspireExport(Description = "Subscribes to the AfterResourcesCreated event")]
    public static DistributedApplicationEventSubscription SubscribeAfterResourcesCreated(
        this IDistributedApplicationBuilder builder,
        Func<AfterResourcesCreatedEvent, Task> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.Eventing.Subscribe<AfterResourcesCreatedEvent>(async (@event, ct) =>
        {
            await callback(@event).ConfigureAwait(false);
        });
    }

    #endregion
}
