// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class CacheCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public CacheCommand(
        CommonCommandServices services)
        : base("cache", CacheCommandStrings.Description, services)
    {
        var clearCommand = new ClearCommand(services);

        Subcommands.Add(clearCommand);
    }

    internal sealed class ClearCommand : BaseCommand
    {
        public ClearCommand(CommonCommandServices services)
            : base("clear", CacheCommandStrings.ClearCommand_Description, services)
        {
        }

        protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            try
            {
                var filesDeleted = 0;
                var currentLogFilePath = ExecutionContext.LogFilePath;

                filesDeleted += ClearDirectoryContents(ExecutionContext.CacheDirectory);
                filesDeleted += ClearDirectoryContents(ExecutionContext.SdksDirectory);
                filesDeleted += ClearDirectoryContents(ExecutionContext.PackagesDirectory);
                // Wipe the staging NuGet package cache too. Producers (PrebuiltAppHostServer's
                // temporary nuget.config for the staging channel) deposit SHA-keyed package
                // caches under <ASPIRE_HOME>/.nugetpackages/<sha>; clearing them lets users
                // recover wedged staging restores without filesystem surgery. We hand the parent
                // directory to ClearDirectoryContents so each SHA subdirectory is wiped while
                // the parent itself stays in place for the next staging restore.
                filesDeleted += ClearDirectoryContents(
                    new DirectoryInfo(CliPathHelper.GetStagingNuGetPackagesDirectory(ExecutionContext.AspireHomeDirectory)));
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

                return Task.FromResult(CommandResult.Success());
            }
            catch (Exception ex)
            {
                var errorMessage = string.Format(CultureInfo.CurrentCulture, CacheCommandStrings.CacheClearFailed, ex.Message);
                Telemetry.RecordError(errorMessage, ex);
                InteractionService.DisplayError(errorMessage);
                return Task.FromResult(CommandResult.Failure(CliExitCodes.InvalidCommand));
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
