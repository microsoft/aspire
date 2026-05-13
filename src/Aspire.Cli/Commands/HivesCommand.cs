// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class HivesCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public HivesCommand(IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("hives", "Manage Aspire CLI package hives.", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(new ListCommand(interactionService, features, updateNotifier, executionContext, telemetry));
        Subcommands.Add(new DeleteCommand(interactionService, features, updateNotifier, executionContext, telemetry));
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        new HelpAction().Invoke(parseResult);
        return Task.FromResult(ExitCodeConstants.InvalidCommand);
    }

    private sealed class ListCommand : BaseCommand
    {
        public ListCommand(IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("list", "List installed Aspire CLI package hives.", features, updateNotifier, executionContext, interactionService, telemetry)
        {
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            if (!ExecutionContext.HivesDirectory.Exists)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, "No Aspire CLI package hives were found.");
                return Task.FromResult(ExitCodeConstants.Success);
            }

            var hives = ExecutionContext.HivesDirectory
                .EnumerateDirectories()
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => d.Name)
                .ToArray();

            if (hives.Length == 0)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, "No Aspire CLI package hives were found.");
                return Task.FromResult(ExitCodeConstants.Success);
            }

            foreach (var hive in hives)
            {
                InteractionService.DisplayPlainText(hive);
            }

            return Task.FromResult(ExitCodeConstants.Success);
        }
    }

    private sealed class DeleteCommand : BaseCommand
    {
        private static readonly Argument<string> s_nameArgument = new("name")
        {
            Description = "The hive name to delete."
        };

        public DeleteCommand(IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("delete", "Delete an Aspire CLI package hive.", features, updateNotifier, executionContext, interactionService, telemetry)
        {
            Arguments.Add(s_nameArgument);
        }

        protected override bool UpdateNotificationsEnabled => false;

        protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var name = parseResult.GetValue(s_nameArgument);
            if (string.IsNullOrWhiteSpace(name))
            {
                InteractionService.DisplayError("A hive name is required.");
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }

            return ExecuteAsync(name);
        }

        private Task<int> ExecuteAsync(string name)
        {
            if (!IsValidHiveName(name))
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, "Invalid hive name '{0}'.", name));
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }

            var hiveDirectory = new DirectoryInfo(Path.Combine(ExecutionContext.HivesDirectory.FullName, name));
            if (!hiveDirectory.Exists)
            {
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, "Hive '{0}' was not found.", name));
                return Task.FromResult(ExitCodeConstants.ConfigNotFound);
            }

            try
            {
                hiveDirectory.Delete(recursive: true);

                InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, "Deleted Aspire CLI package hive '{0}'.", name));
                return Task.FromResult(ExitCodeConstants.Success);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                var errorMessage = string.Format(CultureInfo.CurrentCulture, "Failed to delete Aspire CLI package hive '{0}': {1}", name, ex.Message);
                Telemetry.RecordError(errorMessage, ex);
                InteractionService.DisplayError(errorMessage);
                return Task.FromResult(ExitCodeConstants.InvalidCommand);
            }
        }

        private static bool IsValidHiveName(string name)
        {
            // Path.GetInvalidFileNameChars is platform-specific. Reject both separators explicitly
            // so a hive name can never traverse outside the hives directory on any OS.
            return name is not "." and not ".."
                && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                && !name.Contains('/', StringComparison.Ordinal)
                && !name.Contains('\\', StringComparison.Ordinal);
        }
    }
}
