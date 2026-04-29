// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.Microsoft.DurableTask.AzureManaged;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting to a Durable Task Scheduler with Aspire.
/// </summary>
/// <seealso cref="DurableTaskSchedulerSettings"/>
/// <seealso href="https://learn.microsoft.com/azure/azure-functions/durable/durable-task-scheduler/durable-task-scheduler">Durable Task Scheduler documentation</seealso>
public static class AspireDurableTaskSchedulerExtensions
{
    private const string DefaultConfigSectionName =
        "Aspire:Microsoft:DurableTask:AzureManaged";

    /// <summary>
    /// Registers a Durable Task worker (and optionally a client) connected to a
    /// Durable Task Scheduler. Configures health checks and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureWorker">A delegate to configure the <see cref="IDurableTaskWorkerBuilder"/> (e.g. to register orchestrators and activities).</param>
    /// <param name="configureSettings">An optional delegate to customize <see cref="DurableTaskSchedulerSettings"/>. Invoked after settings are read from configuration.</param>
    /// <param name="includeClient">Whether to also register a <see cref="DurableTaskClient"/>. Defaults to <see langword="true"/>.</param>
    /// <remarks>
    /// Reads the configuration from the <c>Aspire:Microsoft:DurableTask:AzureManaged</c> section.
    /// The connection string is retrieved from the <c>ConnectionStrings</c> configuration section using <paramref name="connectionName"/> as the key.
    /// </remarks>
    /// <example>
    /// Register a Durable Task worker with orchestrators and activities:
    /// <code>
    /// builder.AddDurableTaskSchedulerWorker("scheduler", worker =&gt;
    /// {
    ///     worker.AddTasks(tasks =&gt;
    ///     {
    ///         tasks.AddOrchestrator&lt;MyOrchestrator&gt;();
    ///         tasks.AddActivity&lt;MyActivity&gt;();
    ///     });
    /// });
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="configureWorker"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the connection string is not found.</exception>
    /// <seealso cref="AddKeyedDurableTaskSchedulerWorker"/>
    /// <seealso cref="AddDurableTaskSchedulerClient"/>
    public static void AddDurableTaskSchedulerWorker(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<IDurableTaskWorkerBuilder> configureWorker,
        Action<DurableTaskSchedulerSettings>? configureSettings = null,
        bool includeClient = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);
        ArgumentNullException.ThrowIfNull(configureWorker);

        var settings = ReadSettings(builder, connectionName, configureSettings);

        builder.Services.AddDurableTaskWorker(b =>
        {
            b.UseDurableTaskScheduler(settings.ConnectionString!);
            configureWorker(b);
        });

        if (includeClient)
        {
            builder.Services.AddDurableTaskClient(b =>
            {
                b.UseDurableTaskScheduler(settings.ConnectionString!);
            });
        }

        ConfigureObservability(builder, settings, connectionName);
    }

    /// <summary>
    /// Registers a keyed Durable Task worker (and optionally a client) connected to
    /// a Durable Task Scheduler. Configures health checks and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureWorker">A delegate to configure the <see cref="IDurableTaskWorkerBuilder"/>.</param>
    /// <param name="configureSettings">An optional delegate to customize <see cref="DurableTaskSchedulerSettings"/>.</param>
    /// <param name="includeClient">Whether to also register a keyed <see cref="DurableTaskClient"/>. Defaults to <see langword="true"/>.</param>
    /// <remarks>
    /// Reads the configuration from the <c>Aspire:Microsoft:DurableTask:AzureManaged:{name}</c> section,
    /// falling back to the <c>Aspire:Microsoft:DurableTask:AzureManaged</c> section.
    /// The connection string is retrieved from the <c>ConnectionStrings</c> configuration section using <paramref name="name"/> as the key.
    /// </remarks>
    /// <example>
    /// Register a keyed Durable Task worker:
    /// <code>
    /// builder.AddKeyedDurableTaskSchedulerWorker("my-worker", worker =&gt;
    /// {
    ///     worker.AddTasks(tasks =&gt;
    ///     {
    ///         tasks.AddOrchestrator&lt;MyOrchestrator&gt;();
    ///         tasks.AddActivity&lt;MyActivity&gt;();
    ///     });
    /// });
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> or <paramref name="configureWorker"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if mandatory <paramref name="name"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the connection string is not found.</exception>
    /// <seealso cref="AddDurableTaskSchedulerWorker"/>
    /// <seealso cref="AddKeyedDurableTaskSchedulerClient"/>
    public static void AddKeyedDurableTaskSchedulerWorker(
        this IHostApplicationBuilder builder,
        string name,
        Action<IDurableTaskWorkerBuilder> configureWorker,
        Action<DurableTaskSchedulerSettings>? configureSettings = null,
        bool includeClient = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(configureWorker);

        var settings = ReadSettings(builder, name, configureSettings);

        builder.Services.AddDurableTaskWorker(name, b =>
        {
            b.UseDurableTaskScheduler(settings.ConnectionString!);
            configureWorker(b);
        });

        if (includeClient)
        {
            builder.Services.AddDurableTaskClient(name, b =>
            {
                b.UseDurableTaskScheduler(settings.ConnectionString!);
            });
        }

        ConfigureObservability(builder, settings, name);
    }

    /// <summary>
    /// Registers a <see cref="DurableTaskClient"/> connected to a Durable Task Scheduler.
    /// Configures health checks and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read config from and add services to.</param>
    /// <param name="connectionName">A name used to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate to customize <see cref="DurableTaskSchedulerSettings"/>.</param>
    /// <remarks>
    /// <para>
    /// Use this method when you only need to start and manage orchestrations (e.g. schedule new instances,
    /// query status, or send events) without hosting a worker. If you also need to run orchestrators and
    /// activities, use <see cref="AddDurableTaskSchedulerWorker"/> instead, which registers both a worker
    /// and a client by default.
    /// </para>
    /// <para>
    /// Reads the configuration from the <c>Aspire:Microsoft:DurableTask:AzureManaged</c> section.
    /// The connection string is retrieved from the <c>ConnectionStrings</c> configuration section using <paramref name="connectionName"/> as the key.
    /// </para>
    /// </remarks>
    /// <example>
    /// Register a Durable Task client to start orchestrations from an API controller:
    /// <code>
    /// builder.AddDurableTaskSchedulerClient("scheduler");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the connection string is not found.</exception>
    /// <seealso cref="AddDurableTaskSchedulerWorker"/>
    /// <seealso cref="AddKeyedDurableTaskSchedulerClient"/>
    public static void AddDurableTaskSchedulerClient(
        this IHostApplicationBuilder builder,
        string connectionName,
        Action<DurableTaskSchedulerSettings>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var settings = ReadSettings(builder, connectionName, configureSettings);

        builder.Services.AddDurableTaskClient(b =>
        {
            b.UseDurableTaskScheduler(settings.ConnectionString!);
        });

        ConfigureObservability(builder, settings, connectionName);
    }

    /// <summary>
    /// Registers a keyed <see cref="DurableTaskClient"/> connected to a Durable Task Scheduler.
    /// Configures health checks and telemetry.
    /// </summary>
    /// <param name="builder">The <see cref="IHostApplicationBuilder"/> to read config from and add services to.</param>
    /// <param name="name">The name of the component, which is used as the <see cref="ServiceDescriptor.ServiceKey"/> and also to retrieve the connection string from the ConnectionStrings configuration section.</param>
    /// <param name="configureSettings">An optional delegate to customize <see cref="DurableTaskSchedulerSettings"/>.</param>
    /// <remarks>
    /// Reads the configuration from the <c>Aspire:Microsoft:DurableTask:AzureManaged:{name}</c> section,
    /// falling back to the <c>Aspire:Microsoft:DurableTask:AzureManaged</c> section.
    /// The connection string is retrieved from the <c>ConnectionStrings</c> configuration section using <paramref name="name"/> as the key.
    /// </remarks>
    /// <example>
    /// Register a keyed Durable Task client:
    /// <code>
    /// builder.AddKeyedDurableTaskSchedulerClient("my-client");
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown if mandatory <paramref name="name"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the connection string is not found.</exception>
    /// <seealso cref="AddKeyedDurableTaskSchedulerWorker"/>
    /// <seealso cref="AddDurableTaskSchedulerClient"/>
    public static void AddKeyedDurableTaskSchedulerClient(
        this IHostApplicationBuilder builder,
        string name,
        Action<DurableTaskSchedulerSettings>? configureSettings = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var settings = ReadSettings(builder, name, configureSettings);

        builder.Services.AddDurableTaskClient(name, b =>
        {
            b.UseDurableTaskScheduler(settings.ConnectionString!);
        });

        ConfigureObservability(builder, settings, name);
    }

    private static DurableTaskSchedulerSettings ReadSettings(
        IHostApplicationBuilder builder,
        string connectionName,
        Action<DurableTaskSchedulerSettings>? configureSettings)
    {
        var settings = new DurableTaskSchedulerSettings();
        var configSection = builder.Configuration.GetSection(DefaultConfigSectionName);
        var namedConfigSection = configSection.GetSection(connectionName);
        configSection.Bind(settings);
        namedConfigSection.Bind(settings);

        if (builder.Configuration.GetConnectionString(connectionName) is string connectionString)
        {
            settings.ConnectionString = connectionString;
        }

        configureSettings?.Invoke(settings);

        ConnectionStringValidation.ValidateConnectionString(
            settings.ConnectionString, connectionName, DefaultConfigSectionName, $"{DefaultConfigSectionName}:{connectionName}");

        return settings;
    }

    private static void ConfigureObservability(
        IHostApplicationBuilder builder,
        DurableTaskSchedulerSettings settings,
        string connectionName)
    {
        if (!settings.DisableHealthChecks)
        {
            var connectionString = settings.ConnectionString!;
            var endpoint = DurableTaskSchedulerConnectionString.GetEndpoint(connectionString);
            var taskHubName = DurableTaskSchedulerConnectionString.GetTaskHubName(connectionString);

            if (endpoint is null)
            {
                throw new InvalidOperationException(
                    $"Health checks are enabled for Durable Task Scheduler connection '{connectionName}', but the connection string is missing the 'Endpoint' value.");
            }

            if (taskHubName is null)
            {
                throw new InvalidOperationException(
                    $"Health checks are enabled for Durable Task Scheduler connection '{connectionName}', but the connection string is missing the 'TaskHub' value.");
            }

            var healthCheckName = $"DurableTaskScheduler_{connectionName}";

            builder.TryAddHealthCheck(new HealthCheckRegistration(
                healthCheckName,
                _ => new DurableTaskSchedulerHealthCheck(endpoint, taskHubName),
                failureStatus: default,
                tags: default));
        }

        if (!settings.DisableTracing)
        {
            builder.Services.AddOpenTelemetry()
                .WithTracing(t =>
                {
                    t.AddSource("Microsoft.DurableTask");
                });
        }
    }
}
