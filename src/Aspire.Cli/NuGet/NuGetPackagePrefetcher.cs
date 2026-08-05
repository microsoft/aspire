// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SystemCommand = System.CommandLine.Command;

namespace Aspire.Cli.NuGet;

internal sealed class NuGetPackagePrefetcher(ILogger<NuGetPackagePrefetcher> logger, TimeProvider timeProvider, CliExecutionContext executionContext, IFeatures features, IPackagingService packagingService, ICliUpdateNotifier cliUpdateNotifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for command to be selected
        var command = await WaitForCommandSelectionAsync(stoppingToken);
        if (command is null)
        {
            // Selection only fails when the CLI is shutting down, so there is nothing left to prefetch for.
            return;
        }

        var shouldPrefetchTemplates = ShouldPrefetchTemplatePackages(command);
        var shouldPrefetchCli = ShouldPrefetchCliPackages(
            command,
            features.IsFeatureEnabled(KnownFeatures.UpdateNotificationsEnabled, true));

        var prefetchTasks = new List<Task>(capacity: 2);

        // Prefetch template packages if needed
        if (shouldPrefetchTemplates)
        {
            prefetchTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var channels = await packagingService.GetChannelsAsync(stoppingToken);

                    foreach (var channel in channels)
                    {
                        // Discard the results here, we just want them in the cache.
                        _ = await channel.GetTemplatePackagesAsync(executionContext.WorkingDirectory, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogTrace("Template package prefetching was cancelled because the CLI is shutting down.");
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Non-fatal error while prefetching template packages. This is not critical to the operation of the CLI.");
                    // This prefetching is best effort. If it fails we log (above) and then the
                    // background service will exit gracefully. Code paths that depend on this
                    // data will handle the absence of pre-fetched packages gracefully.
                }
            }, stoppingToken));
        }

        // Prefetch CLI packages if needed
        if (shouldPrefetchCli)
        {
            prefetchTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await cliUpdateNotifier.CheckForCliUpdatesAsync(
                        workingDirectory: executionContext.WorkingDirectory,
                        cancellationToken: stoppingToken
                        );
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    logger.LogTrace("CLI package prefetching was cancelled because the CLI is shutting down.");
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Non-fatal error while prefetching CLI packages. This is not critical to the operation of the CLI.");
                }
            }, stoppingToken));
        }

        await PreventOrphanedPrefetchingAsync(prefetchTasks, stoppingToken);
    }

    /// <summary>
    /// Holds the service open until prefetching finishes, so the CLI cannot exit and leave a NuGet
    /// search child process behind.
    /// </summary>
    private static async Task PreventOrphanedPrefetchingAsync(List<Task> prefetchTasks, CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(prefetchTasks);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Waits for the command to be selected, which happens once its action runs. Returns <see langword="null"/>
    /// only when the CLI shuts down first.
    /// </summary>
    private async Task<SystemCommand?> WaitForCommandSelectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await executionContext.CommandSelected.Task.WaitAsync(Timeout.InfiniteTimeSpan, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static bool ShouldPrefetchTemplatePackages(SystemCommand? command)
        => command is BaseCommand { PrefetchesTemplatePackageMetadata: true };

    private static bool ShouldPrefetchCliPackages(SystemCommand? command, bool updateNotificationsEnabled)
        => command is BaseCommand baseCommand && baseCommand.ShouldPrefetchCliPackageMetadata(updateNotificationsEnabled);
}
