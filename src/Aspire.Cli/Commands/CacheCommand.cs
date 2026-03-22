// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class CacheCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public CacheCommand(IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("cache", CacheCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        var clearCommand = new ClearCommand(InteractionService, features, updateNotifier, executionContext, telemetry);

        Subcommands.Add(clearCommand);
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }

    private sealed class ClearCommand : BaseCommand
    {
        public ClearCommand(IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("clear", CacheCommandStrings.ClearCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
        {
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            try
            {
                var cacheDirectory = ExecutionContext.CacheDirectory;
                var filesDeleted = 0;

                // Delete cache files and subdirectories
                if (cacheDirectory.Exists)
                {
                    // Delete all cache files and subdirectories
                    foreach (var file in cacheDirectory.GetFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            file.Delete();
                            filesDeleted++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            // Continue deleting other files even if some fail
                        }
                    }

                    // Delete subdirectories
                    foreach (var directory in cacheDirectory.GetDirectories())
                    {
                        try
                        {
                            directory.Delete(recursive: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            // Continue deleting other directories even if some fail
                        }
                    }
                }

                // Also clear the sdks directory
                var sdksDirectory = ExecutionContext.SdksDirectory;
                if (sdksDirectory.Exists)
                {
                    foreach (var file in sdksDirectory.GetFiles("*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            file.Delete();
                            filesDeleted++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            // Continue deleting other files even if some fail
                        }
                    }

                    // Delete subdirectories
                    foreach (var directory in sdksDirectory.GetDirectories())
                    {
                        try
                        {
                            directory.Delete(recursive: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            // Continue deleting other directories even if some fail
                        }
                    }
                }

                // Also clear the logs directory (skip current process's log file)
                var logsDirectory = ExecutionContext.LogsDirectory;
                var currentLogFilePath = ExecutionContext.LogFilePath;
                if (logsDirectory.Exists)
                {
                    foreach (var file in logsDirectory.GetFiles("*", SearchOption.AllDirectories))
                    {
                        // Skip the current process's log file to avoid deleting it while in use
                        if (file.FullName.Equals(currentLogFilePath, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            file.Delete();
                            filesDeleted++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            // Continue deleting other files even if some fail
                        }
                    }

                    // Delete subdirectories
                    foreach (var directory in logsDirectory.GetDirectories())
                    {
                        try
                        {
                            directory.Delete(recursive: true);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                        {
                            // Continue deleting other directories even if some fail
                        }
                    }
                }

                if (filesDeleted == 0)
                {
                    InteractionService.DisplayMessage(KnownEmojis.Information, CacheCommandStrings.CacheAlreadyEmpty);
                }
                else
                {
                    InteractionService.DisplaySuccess(CacheCommandStrings.CacheCleared);
                }

                return Task.FromResult(ExitCodeConstants.Success);
            }
            catch (Exception ex)
            {
                var errorMessage = string.Format(CultureInfo.CurrentCulture, CacheCommandStrings.CacheClearFailed, ex.Message);
                Telemetry.RecordError(errorMessage, ex);
                InteractionService.DisplayError(errorMessage);
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }
        }
    }
}
