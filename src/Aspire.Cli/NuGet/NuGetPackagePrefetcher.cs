// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SystemCommand = System.CommandLine.Command;

namespace Aspire.Cli.NuGet;

internal sealed class NuGetPackagePrefetcher(ILogger<NuGetPackagePrefetcher> logger, CliExecutionContext executionContext, IFeatures features, IPackagingService packagingService, ICliUpdateNotifier cliUpdateNotifier) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for command to be selected
        var command = await WaitForCommandSelectionAsync(stoppingToken);

        var shouldPrefetchTemplates = ShouldPrefetchTemplatePackages(command);
        var shouldPrefetchCli = ShouldPrefetchCliPackages(command);

        // Prefetch template packages if needed
        if (shouldPrefetchTemplates)
        {
            _ = Task.Run(async () =>
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
                 catch (System.Exception ex)
                 {
                     logger.LogDebug(ex, "Non-fatal error while prefetching template packages. This is not critical to the operation of the CLI.");
                     // This prefetching is best effort. If it fails we log (above) and then the
                     // background service will exit gracefully. Code paths that depend on this
                     // data will handle the absence of pre-fetched packages gracefully.
                 }
             }, stoppingToken);
        }

        // Prefetch CLI packages if needed
        if (shouldPrefetchCli)
        {
            _ = Task.Run(async () =>
            {
                if (features.IsFeatureEnabled(KnownFeatures.UpdateNotificationsEnabled, true))
                {
                    try
                    {
                        await cliUpdateNotifier.CheckForCliUpdatesAsync(
                            workingDirectory: executionContext.WorkingDirectory,
                            cancellationToken: stoppingToken
                            );
                    }
                    catch (System.Exception ex)
                    {
                        logger.LogDebug(ex, "Non-fatal error while prefetching CLI packages. This is not critical to the operation of the CLI.");
                    }
                }
            }, stoppingToken);
        }
    }

    private async Task<SystemCommand?> WaitForCommandSelectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Wait for command to be selected, with a timeout
            // If timeout occurs, proceed with default behavior (no command)
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            var command = await executionContext.CommandSelected.Task.WaitAsync(combined.Token);
            return command;
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation occurred - proceed with no command (default behavior)
            return null;
        }
    }

    private static bool ShouldPrefetchTemplatePackages(SystemCommand? command)
    {
        // If the command implements IPackageMetaPrefetchingCommand, use its setting
        if (command is IPackageMetaPrefetchingCommand prefetchingCommand)
        {
            return prefetchingCommand.PrefetchesTemplatePackageMetadata;
        }

        // Default behavior: prefetch templates for all commands except run, publish, deploy
        // Because of this: https://github.com/microsoft/aspire/issues/6956
        return command is null || !IsRuntimeOnlyCommand(command);
    }

    private static bool ShouldPrefetchCliPackages(SystemCommand? command)
    {
        // If the command implements IPackageMetaPrefetchingCommand, use its setting
        if (command is IPackageMetaPrefetchingCommand prefetchingCommand)
        {
            return prefetchingCommand.PrefetchesCliPackageMetadata;
        }

        // Default behavior: always prefetch CLI packages for update notifications
        return true;
    }

    private static bool IsRuntimeOnlyCommand(SystemCommand command)
    {
        var commandName = command.Name;
        return commandName is "run" or "publish" or "deploy" or "do";
    }
}
