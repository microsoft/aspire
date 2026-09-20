// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable IDE0005 // Using directive is unnecessary. This using is required when building this file in Aspire.Playground.Tests.csproj.
using Aspire.Hosting.ApplicationModel;
#pragma warning restore IDE0005 // Using directive is unnecessary.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests.Utils;

public static class LoggerNotificationExtensions
{
    /// <summary>
    /// Waits for the specified text to be logged.
    /// </summary>
    /// <param name="app">The <see cref="DistributedApplication" /> instance to watch.</param>
    /// <param name="logText">The text to wait for.</param>
    /// <param name="resourceName">An optional resource name to filter the logs for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public static Task WaitForTextAsync(this DistributedApplication app, string logText, string? resourceName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrEmpty(logText);

        return WaitForTextAsync(app, (log) => log.Contains(logText), resourceName, cancellationToken);
    }

    public static async Task WaitForHealthyAsync<T>(this DistributedApplication app, IResourceBuilder<T> resource, CancellationToken cancellationToken = default) where T : IResource
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(resource);
        var rns = app.Services.GetRequiredService<ResourceNotificationService>();
        await rns.WaitForResourceHealthyAsync(resource.Resource.Name, cancellationToken);
    }

    /// <summary>
    /// Waits for the specified text to be logged.
    /// </summary>
    /// <param name="app">The <see cref="DistributedApplication" /> instance to watch.</param>
    /// <param name="logTexts">Any text to wait for.</param>
    /// <param name="resourceName">An optional resource name to filter the logs for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public static Task WaitForTextAsync(this DistributedApplication app, IEnumerable<string> logTexts, string? resourceName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(logTexts);

        return app.WaitForTextAsync((log) => logTexts.Any(x => log.Contains(x)), resourceName, cancellationToken);
    }

    /// <summary>
    /// Waits for the specified text to be logged.
    /// </summary>
    /// <param name="app">The <see cref="DistributedApplication" /> instance to watch.</param>
    /// <param name="predicate">A predicate checking the text to wait for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public static Task WaitForTextAsync(this DistributedApplication app, Predicate<string> predicate, CancellationToken cancellationToken = default)
        => app.WaitForTextAsync(predicate, resourceName: null, cancellationToken);

    /// <summary>
    /// Waits for the specified text to be logged.
    /// </summary>
    /// <param name="app">The <see cref="DistributedApplication" /> instance to watch.</param>
    /// <param name="predicate">A predicate checking the text to wait for.</param>
    /// <param name="resourceName">An optional resource name to filter the logs for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public static Task WaitForTextAsync(this DistributedApplication app, Predicate<string> predicate, string? resourceName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(predicate);

        var hostApplicationLifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        var watchCts = CancellationTokenSource.CreateLinkedTokenSource(hostApplicationLifetime.ApplicationStopping, cancellationToken);

        var tcs = new TaskCompletionSource();

        _ = Task.Run(() => WatchNotifications(app, resourceName, predicate, tcs, watchCts), watchCts.Token);

        return tcs.Task;
    }

    /// <summary>
    /// Waits for all the specified texts to be logged.
    /// </summary>
    /// <param name="app">The <see cref="DistributedApplication" /> instance to watch.</param>
    /// <param name="logTexts">Any text to wait for.</param>
    /// <param name="resourceName">An optional resource name to filter the logs for.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns></returns>
    public static async Task WaitForAllTextAsync(this DistributedApplication app, IEnumerable<string> logTexts, string? resourceName = null, CancellationToken cancellationToken = default)
    {
        var table = logTexts.ToList();
        try
        {
            await app.WaitForTextAsync((log) =>
            {
                foreach (var text in table)
                {
                    if (log.Contains(text))
                    {
                        table.Remove(text);
                        break;
                    }
                }

                return table.Count == 0;
            }, resourceName, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException te) when (cancellationToken.IsCancellationRequested)
        {
            throw new TaskCanceledException($"Task was canceled before these messages were found: '{string.Join("', '", table)}'", te);
        }
    }

    private static async Task WatchNotifications(DistributedApplication app, string? resourceName, Predicate<string> predicate, TaskCompletionSource tcs, CancellationTokenSource cancellationTokenSource)
    {
        var resourceLoggerService = app.Services.GetRequiredService<ResourceLoggerService>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(LoggerNotificationExtensions));

        var loggingResourceIds = new HashSet<string>();
        var logWatchTasks = new List<Task>();

        try
        {
            await foreach (var resourceEvent in app.ResourceNotifications.WatchAsync(cancellationTokenSource.Token).ConfigureAwait(false))
            {
                if (resourceName != null && !string.Equals(resourceEvent.Resource.Name, resourceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var resourceId = resourceEvent.ResourceId;

                if (loggingResourceIds.Add(resourceId))
                {
                    // Start watching the logs for this resource ID
                    logWatchTasks.Add(WatchResourceLogs(tcs, resourceId, predicate, resourceLoggerService, cancellationTokenSource));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected if the application stops prematurely or the text was detected.
            tcs.TrySetCanceled();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred while watching for resource notifications.");
            tcs.TrySetException(ex);
        }
    }

    private static async Task WatchResourceLogs(TaskCompletionSource tcs, string resourceId, Predicate<string> predicate, ResourceLoggerService resourceLoggerService, CancellationTokenSource cancellationTokenSource)
    {
        await foreach (var logEvent in resourceLoggerService.WatchAsync(resourceId).WithCancellation(cancellationTokenSource.Token).ConfigureAwait(false))
        {
            foreach (var line in logEvent)
            {
                if (predicate(line.Content))
                {
                    tcs.SetResult();
                    cancellationTokenSource.Cancel();
                    return;
                }
            }
        }
    }
}
