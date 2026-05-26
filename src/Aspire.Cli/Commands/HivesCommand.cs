// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Uninstall;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class HivesCommand : ParentCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    public HivesCommand(HiveEnumerator hives, CliCleanupService cleanupService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("hives", "Manage Aspire CLI package hives", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        Subcommands.Add(new ListCommand(hives, interactionService, features, updateNotifier, executionContext, telemetry));
        Subcommands.Add(new DeleteCommand(cleanupService, interactionService, features, updateNotifier, executionContext, telemetry));
    }

    private sealed class ListCommand(HiveEnumerator hives, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : BaseCommand("list", "List Aspire CLI package hives", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var enumeratedHives = hives.GetHives();
            if (enumeratedHives.Count == 0)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, "No Aspire CLI package hives were found.");
                return Task.FromResult(CommandResult.Success());
            }

            var dogfoodRoot = Path.Combine(ExecutionContext.HivesDirectory.Parent?.FullName ?? ExecutionContext.AspireHomeDirectory.FullName, "dogfood");
            var table = new Table();
            table.AddColumn("Name");
            table.AddColumn("Path");
            table.AddColumn("Dogfood install");

            foreach (var hive in enumeratedHives)
            {
                var hasDogfoodInstall = Directory.Exists(Path.Combine(dogfoodRoot, hive.Name));
                table.AddRow(hive.Name, hive.Path, hasDogfoodInstall ? "yes" : "no");
            }

            InteractionService.DisplayRenderable(table);
            return Task.FromResult(CommandResult.Success());
        }
    }

    private sealed class DeleteCommand : BaseCommand
    {
        private static readonly Argument<string> s_nameArgument = new("name")
        {
            Description = "The hive name to delete."
        };

        private static readonly Option<bool> s_yesOption = new("--yes", "-y")
        {
            Description = "Confirm deletion without prompting."
        };

        private static readonly Option<bool> s_forceOption = new("--force")
        {
            Description = "Delete the hive even when a matching PR dogfood install exists."
        };

        private static readonly Option<bool> s_dryRunOption = new("--dry-run")
        {
            Description = "Show what would be deleted without deleting it."
        };

        private readonly CliCleanupService _cleanupService;

        public DeleteCommand(CliCleanupService cleanupService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
            : base("delete", "Delete an Aspire CLI package hive", features, updateNotifier, executionContext, interactionService, telemetry)
        {
            _cleanupService = cleanupService;
            Arguments.Add(s_nameArgument);
            Options.Add(s_yesOption);
            Options.Add(s_forceOption);
            Options.Add(s_dryRunOption);
            AddNonInteractiveRequiresYesValidator(this, s_yesOption);
        }

        protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var name = parseResult.GetValue(s_nameArgument);
            if (string.IsNullOrWhiteSpace(name))
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, "A hive name is required.");
            }

            var dryRun = parseResult.GetValue(s_dryRunOption);
            var yes = parseResult.GetValue(s_yesOption);
            if (!dryRun && !yes && !await InteractionService.PromptConfirmAsync($"Delete Aspire CLI hive '{name}'?", cancellationToken: cancellationToken))
            {
                return CommandResult.Cancelled();
            }

            var result = await _cleanupService.DeleteHiveAsync(name, parseResult.GetValue(s_forceOption), dryRun, cancellationToken);
            HivesCommand.DisplayOperations(InteractionService, result.Operations);

            return result.HasFailures ? CommandResult.Failure(CliExitCodes.InvalidCommand) : CommandResult.Success();
        }
    }

    internal static void DisplayOperations(IInteractionService interactionService, IEnumerable<CleanupOperation> operations)
    {
        foreach (var operation in operations)
        {
            var status = operation.Status switch
            {
                CleanupOperationStatus.Removed => "removed",
                CleanupOperationStatus.WouldRemove => "would remove",
                CleanupOperationStatus.Skipped => "skipped",
                CleanupOperationStatus.Failed => "failed",
                _ => operation.Status.ToString()
            };
            var reason = string.IsNullOrWhiteSpace(operation.Reason) ? "" : $" ({operation.Reason})";
            interactionService.DisplayPlainText($"{status}: {operation.Path}{reason}");
        }
    }
}
