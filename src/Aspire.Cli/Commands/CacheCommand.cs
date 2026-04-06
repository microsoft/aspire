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

    internal sealed class ClearCommand : BaseCommand
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
                var filesDeleted = 0;
                var currentLogFilePath = ExecutionContext.LogFilePath;

                filesDeleted += ClearDirectoryContents(ExecutionContext.CacheDirectory);
                filesDeleted += ClearDirectoryContents(ExecutionContext.SdksDirectory);
                filesDeleted += ClearDirectoryContents(ExecutionContext.PackagesDirectory);
                filesDeleted += ClearDirectoryContents(
                    ExecutionContext.LogsDirectory,
                    skipFile: f => f.FullName.Equals(currentLogFilePath, StringComparison.OrdinalIgnoreCase));

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

        private static readonly EnumerationOptions s_enumerationOptions = new()
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true
        };

        internal static int ClearDirectoryContents(DirectoryInfo? directory, Func<FileInfo, bool>? skipFile = null)
        {
            if (directory is null || !directory.Exists)
            {
                return 0;
            }

            var filesDeleted = 0;

            foreach (var file in directory.EnumerateFiles("*", s_enumerationOptions))
            {
                if (skipFile?.Invoke(file) == true)
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
                    // Continue deleting other files even if some fail (e.g. locked by a running process)
                }
            }

            foreach (var subdirectory in directory.EnumerateDirectories())
            {
                try
                {
                    subdirectory.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Continue deleting other directories even if some fail
                }
            }

            return filesDeleted;
        }
    }
}
