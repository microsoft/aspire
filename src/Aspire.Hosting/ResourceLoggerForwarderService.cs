// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0005 // Using directive is unnecessary. This warning happens when building this file in Aspire.Hosting.Tests.csproj
using Aspire.Hosting.ApplicationModel;
#pragma warning restore IDE0005 // Using directive is unnecessary.
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// A background service that watches resource logs and forwards them to the host's <see cref="ILogger"/> infrastructure.
/// </summary>
internal sealed class ResourceLoggerForwarderService(
    ResourceNotificationService resourceNotificationService,
    ResourceLoggerService resourceLoggerService,
    IHostEnvironment hostEnvironment,
    ILoggerFactory loggerFactory)
    : BackgroundService
{
    /// <summary>
    /// A callback to be invoked when a log is forwarded to ILogger. The callback is passed the resource name the log is for.<br/>
    /// Used for testing.
    /// </summary>
    public Action<string>? OnResourceLog { get; set; }

    /// <inheritdoc/>
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // We need to pass the stopping token in here because the ResourceNotificationService doesn't stop on host shutdown
        return WatchNotifications(stoppingToken);
    }

    private async Task WatchNotifications(CancellationToken cancellationToken)
    {
        try
        {
            var loggingResourceIds = new HashSet<string>();
            var logWatchTasks = new List<Task>();

            await foreach (var resourceEvent in resourceNotificationService.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                var resourceId = resourceEvent.ResourceId;

                if (loggingResourceIds.Add(resourceId))
                {
                    // Start watching the logs for this resource ID
                    logWatchTasks.Add(WatchResourceLogs(resourceEvent.Resource, resourceId, cancellationToken));
                }
            }

            await Task.WhenAll(logWatchTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // this was expected as the token was canceled
        }
    }

    private async Task WatchResourceLogs(IResource resource, string resourceId, CancellationToken cancellationToken)
    {
        try
        {
            var applicationName = hostEnvironment.ApplicationName;
            var logger = loggerFactory.CreateLogger($"{applicationName}.Resources.{resource.Name}");

            // Use the synchronous subscription path so terminal-state log flushes from DCP reach
            // ILogger before ResourceNotificationService publishes the terminal state to tests.
            using var subscription = resourceLoggerService.Subscribe(resourceId, logEvent =>
            {
                foreach (var line in logEvent)
                {
                    var logLevel = line.IsErrorMessage ? LogLevel.Error : LogLevel.Information;

                    if (logger.IsEnabled(logLevel))
                    {
                        // Log message format here approximates the format shown in the dashboard
                        logger.Log(logLevel, "{LineNumber}: {LineContent}", line.LineNumber, line.Content);
                        OnResourceLog?.Invoke(resourceId);
                    }
                }
            });

            // Keep the subscription alive until the resource log stream completes or the app stops.
            await resourceLoggerService.WaitForCompletionAsync(resourceId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // this was expected as the token was canceled
        }
    }
}
