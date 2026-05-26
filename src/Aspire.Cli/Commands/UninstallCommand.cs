// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Uninstall;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

internal sealed class UninstallCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private static readonly Option<string?> s_channelOption = new("--channel")
    {
        Description = "The Aspire CLI channel or hive label to clean up."
    };

    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = "Clean pr-*, staging, and daily hives."
    };

    private static readonly Option<bool> s_yesOption = new("--yes", "-y")
    {
        Description = "Confirm cleanup without prompting."
    };

    private static readonly Option<bool> s_dryRunOption = new("--dry-run")
    {
        Description = "Show what would be removed without deleting it."
    };

    private static readonly Option<bool> s_removeSharedInstallOption = new("--remove-shared-install")
    {
        Description = "Also remove the shared script install under ~/.aspire/bin and its bundle layout."
    };

    private readonly CliCleanupService _cleanupService;

    public UninstallCommand(CliCleanupService cleanupService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("uninstall", "Clean up Aspire CLI script and PR installs", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _cleanupService = cleanupService;

        Options.Add(s_channelOption);
        Options.Add(s_allOption);
        Options.Add(s_yesOption);
        Options.Add(s_dryRunOption);
        Options.Add(s_removeSharedInstallOption);
        AddNonInteractiveRequiresYesValidator(this, s_yesOption);
        Validators.Add(result =>
        {
            var channel = result.GetValue(s_channelOption);
            var all = result.GetValue(s_allOption);
            if ((string.IsNullOrWhiteSpace(channel) && !all) ||
                (!string.IsNullOrWhiteSpace(channel) && all))
            {
                result.AddError("Specify exactly one of --channel or --all.");
            }
        });
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var channel = parseResult.GetValue(s_channelOption);
        var all = parseResult.GetValue(s_allOption);
        var dryRun = parseResult.GetValue(s_dryRunOption);
        var yes = parseResult.GetValue(s_yesOption);
        var removeSharedInstall = parseResult.GetValue(s_removeSharedInstallOption);
        var channels = _cleanupService.ExpandChannels(channel, all);

        if (channels.Count == 0)
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, "No matching Aspire CLI cleanup targets were found.");
            return CommandResult.Success();
        }

        if (!dryRun && !yes && !await InteractionService.PromptConfirmAsync($"Clean up Aspire CLI channel(s): {string.Join(", ", channels)}?", cancellationToken: cancellationToken))
        {
            return CommandResult.Cancelled();
        }

        var result = await _cleanupService.UninstallAsync(channels, removeSharedInstall, dryRun, cancellationToken);
        HivesCommand.DisplayOperations(InteractionService, result.Operations);

        return result.HasFailures ? CommandResult.Failure(CliExitCodes.InvalidCommand) : CommandResult.Success();
    }
}
