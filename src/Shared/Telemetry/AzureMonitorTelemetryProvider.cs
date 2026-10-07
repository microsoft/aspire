// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Owns Azure Monitor product trace and log providers and their isolated logging services.
/// </summary>
internal sealed class AzureMonitorTelemetryProvider : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly TracerProvider _traceProvider;
    private readonly LoggerProvider _logProvider;
    private readonly Lock _lifecycleLock = new();
    private readonly List<Task<bool>> _flushTasks = [];
    private Task<bool>? _shutdownTask;
    private bool _disposed;

    private AzureMonitorTelemetryProvider(ServiceProvider services, TracerProvider traceProvider, LoggerProvider logProvider, ILogger eventLogger)
    {
        _services = services;
        _traceProvider = traceProvider;
        _logProvider = logProvider;
        EventLogger = eventLogger;
    }

    public ILogger EventLogger { get; }

    internal Resource Resource => _traceProvider.GetResource();

    internal static AzureMonitorTelemetryProvider Create(
        IServiceCollection services, ResourceBuilder resourceBuilder, string activitySourceName, string eventLogCategoryName,
        string connectionString, string storageDirectory) =>
        Create(services, resourceBuilder, activitySourceName, eventLogCategoryName, connectionString, storageDirectory, static _ => { });

    internal static AzureMonitorTelemetryProvider Create(
        IServiceCollection services, ResourceBuilder resourceBuilder, string activitySourceName, string eventLogCategoryName,
        string connectionString, string storageDirectory, Action<TracerProviderBuilder> configureTracing) =>
        Create(services, resourceBuilder, activitySourceName, eventLogCategoryName, connectionString, storageDirectory,
            configureTracing, static _ => { });

    internal static AzureMonitorTelemetryProvider Create(
        IServiceCollection services, ResourceBuilder resourceBuilder, string activitySourceName, string eventLogCategoryName,
        string connectionString, string storageDirectory, Action<TracerProviderBuilder> configureTracing,
        Action<AzureMonitorExporterOptions> configureLogging) =>
        Create(services, resourceBuilder, activitySourceName, eventLogCategoryName, connectionString, storageDirectory,
            configureTracing, static _ => { }, configureLogging);

    internal static AzureMonitorTelemetryProvider Create(
        IServiceCollection services, ResourceBuilder resourceBuilder, string activitySourceName, string eventLogCategoryName,
        string connectionString, string storageDirectory, Action<TracerProviderBuilder> configureTracing,
        Action<OpenTelemetryLoggerOptions> configureLogging, Action<AzureMonitorExporterOptions> configureLogExporter) =>
        Create(services, resourceBuilder, eventLogCategoryName,
            () =>
            {
                // Listen only to product instrumentation, never profiling or diagnostic sources.
                var builder = Sdk.CreateTracerProviderBuilder()
                    .AddSource(activitySourceName)
                    .SetResourceBuilder(resourceBuilder);
                // OnEnd enrichment must run before the export processor queues the span.
                configureTracing(builder);
                return builder.AddAspireAzureMonitorExporter(connectionString, storageDirectory).Build();
            },
            logging =>
            {
                logging.AddAspireAzureMonitorExporter(
                    connectionString,
                    Path.Combine(storageDirectory, "logs"),
                    configureLogExporter);
                configureLogging(logging);
            },
            static _ => { });

    internal static AzureMonitorTelemetryProvider Create(
        IServiceCollection services, ResourceBuilder resourceBuilder, string eventLogCategoryName,
        Func<TracerProvider> createTraceProvider, Action<LoggerProvider> configureLogProvider)
        => Create(services, resourceBuilder, eventLogCategoryName, createTraceProvider, static _ => { }, configureLogProvider);

    private static AzureMonitorTelemetryProvider Create(
        IServiceCollection services, ResourceBuilder resourceBuilder, string eventLogCategoryName,
        Func<TracerProvider> createTraceProvider, Action<OpenTelemetryLoggerOptions> configureLogging,
        Action<LoggerProvider> configureLogProvider)
    {
        var traceProvider = createTraceProvider();
        ServiceProvider? loggingServices = null;
        LoggerProvider? logProvider = null;
        try
        {
            // Managers supply a dedicated collection after checking consent. Do not import
            // application providers, filters, or scopes into the product export pipeline.
            services.AddLogging(builder =>
            {
                builder.AddOpenTelemetry(logging =>
                {
                    logging.IncludeFormattedMessage = true;
                    logging.IncludeScopes = false;
                    logging.SetResourceBuilder(resourceBuilder);
                    configureLogging(logging);
                });
            });
            loggingServices = services.BuildServiceProvider();
            logProvider = loggingServices.GetRequiredService<LoggerProvider>();
            configureLogProvider(logProvider);
            var eventLogger = loggingServices.GetRequiredService<ILoggerFactory>().CreateLogger(eventLogCategoryName);
            return new AzureMonitorTelemetryProvider(loggingServices, traceProvider, logProvider, eventLogger);
        }
        catch
        {
            traceProvider.Shutdown(0);
            traceProvider.Dispose();
            logProvider?.Shutdown(0);
            loggingServices?.Dispose();
            throw;
        }
    }

    public Task<bool> ForceFlushAsync(int timeoutMilliseconds)
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shutdownTask is not null)
            {
                throw new InvalidOperationException("Product telemetry cannot be flushed after shutdown.");
            }

            _flushTasks.RemoveAll(task => task.IsCompleted);
            var flushTask = FlushProvidersAsync(
                () => _traceProvider.ForceFlush(timeoutMilliseconds),
                () => _logProvider.ForceFlush(timeoutMilliseconds));
            _flushTasks.Add(flushTask);

            return flushTask;
        }
    }

    public Task<bool> ShutdownAsync(int timeoutMilliseconds)
    {
        lock (_lifecycleLock)
        {
            if (_shutdownTask is not null)
            {
                return _shutdownTask;
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            _shutdownTask = ShutdownProvidersAsync(timeoutMilliseconds, _flushTasks.ToArray());

            return _shutdownTask;
        }
    }

    private async Task<bool> ShutdownProvidersAsync(int timeoutMilliseconds, Task<bool>[] flushTasks)
    {
        await Task.WhenAll(flushTasks).ConfigureAwait(false);
        return await FlushProvidersAsync(
            () => _traceProvider.Shutdown(timeoutMilliseconds),
            () => _logProvider.Shutdown(timeoutMilliseconds)).ConfigureAwait(false);
    }

    private static async Task<bool> FlushProvidersAsync(Func<bool> flushTraces, Func<bool> flushLogs)
    {
        // Each signal gets the same timeout without adding the two waits together.
        var results = await Task.WhenAll(Task.Run(flushTraces), Task.Run(flushLogs)).ConfigureAwait(false);
        return results.All(result => result);
    }

    public void Dispose()
    {
        Task<bool>? shutdownTask;
        Task<bool>[] flushTasks;
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            shutdownTask = _shutdownTask;
            flushTasks = _flushTasks.ToArray();
        }
        try
        {
            if (shutdownTask is not null)
            {
                // Shutdown also waits for any in-flight force flushes before draining.
                shutdownTask.GetAwaiter().GetResult();
            }
            else
            {
                Task.WhenAll(flushTasks).GetAwaiter().GetResult();
            }
        }
        finally
        {
            try
            {
                // Disposal-only and failed-shutdown paths release storage without another flush.
                _traceProvider.Shutdown(0);
                _traceProvider.Dispose();
            }
            finally
            {
                _logProvider.Shutdown(0);
                _services.Dispose();
            }
        }
    }

}
