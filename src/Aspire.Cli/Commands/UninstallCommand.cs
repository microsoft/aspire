// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// <c>aspire uninstall</c> — removes an Aspire CLI install placed by the
/// release or PR install scripts. Refuses for packager-managed routes
/// (winget / brew / dotnet-tool) and for the currently running CLI.
/// </summary>
internal sealed class UninstallCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private static readonly Option<int?> s_prOption = new("--pr")
    {
        Description = UninstallCommandStrings.PrOptionDescription,
    };

    private static readonly Argument<string?> s_prefixArgument = new("prefix")
    {
        Description = UninstallCommandStrings.PrefixArgumentDescription,
        Arity = ArgumentArity.ZeroOrOne,
    };

    private static readonly Option<bool> s_yesOption = new("--yes", "-y")
    {
        Description = UninstallCommandStrings.YesOptionDescription,
    };

    private static readonly Option<bool> s_dryRunOption = new("--dry-run")
    {
        Description = UninstallCommandStrings.DryRunOptionDescription,
    };

    private readonly IInstallationUninstaller _uninstaller;
    private readonly IAnsiConsole _ansiConsole;

    public UninstallCommand(
        IInstallationUninstaller uninstaller,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        IAnsiConsole ansiConsole,
        AspireCliTelemetry telemetry)
        : base("uninstall", UninstallCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _uninstaller = uninstaller;
        _ansiConsole = ansiConsole;

        Options.Add(s_prOption);
        Options.Add(s_yesOption);
        Options.Add(s_dryRunOption);
        Arguments.Add(s_prefixArgument);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var prNumber = parseResult.GetValue(s_prOption);
        var prefixArg = parseResult.GetValue(s_prefixArgument);
        var yes = parseResult.GetValue(s_yesOption);
        var dryRun = parseResult.GetValue(s_dryRunOption);

        // Targeting model (per design): require exactly one of --pr or
        // <prefix>. No "operate on running CLI by default" mode — that
        // would be the most dangerous default and is handled by the
        // RefusedSelf branch even if the user passes the prefix explicitly.
        if (prNumber.HasValue && !string.IsNullOrEmpty(prefixArg))
        {
            InteractionService.DisplayError(UninstallCommandStrings.ConflictingTargetsError);
            return CommandResult.Failure(ExitCodeConstants.MissingRequiredArgument);
        }
        if (!prNumber.HasValue && string.IsNullOrEmpty(prefixArg))
        {
            InteractionService.DisplayError(UninstallCommandStrings.MissingTargetError);
            return CommandResult.Failure(ExitCodeConstants.MissingRequiredArgument);
        }

        var prefix = string.IsNullOrEmpty(prefixArg)
            ? Path.Combine(HomeDirectoryResolver.GetUserHomeOrThrow(), ".aspire", "dogfood", FormattableString.Invariant($"pr-{prNumber!.Value}"))
            : Path.GetFullPath(prefixArg);

        var plan = _uninstaller.Plan(prefix);
        PrintPlanHeader(plan);

        switch (plan.Outcome)
        {
            case UninstallOutcome.NothingToDo:
                InteractionService.DisplayMessage(
                    KnownEmojis.Information,
                    string.Format(CultureInfo.CurrentCulture, UninstallCommandStrings.NothingToDoMessage, plan.RefusalReason ?? string.Empty));
                PrintManualSteps(plan);
                return CommandResult.Success();
            case UninstallOutcome.RefusedSelf:
            case UninstallOutcome.RefusedPackagerOwned:
            case UninstallOutcome.RefusedUnknownRoute:
                InteractionService.DisplayError(
                    string.Format(CultureInfo.CurrentCulture, UninstallCommandStrings.RefusalMessage, plan.RefusalReason ?? string.Empty));
                PrintManualSteps(plan);
                return CommandResult.Failure(ExitCodeConstants.InvalidCommand);
            case UninstallOutcome.Proceed:
                break;
        }

        PrintRemovals(plan);
        PrintManualSteps(plan);

        if (dryRun)
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, UninstallCommandStrings.DryRunFooter);
            return CommandResult.Success();
        }

        if (!yes)
        {
            var confirmed = await InteractionService.PromptConfirmAsync(
                UninstallCommandStrings.ConfirmPrompt,
                PromptBinding.CreateDefault(false),
                cancellationToken);
            if (!confirmed)
            {
                InteractionService.DisplayMessage(KnownEmojis.Information, UninstallCommandStrings.UserDeclinedMessage);
                return CommandResult.Success();
            }
        }

        try
        {
            await _uninstaller.ExecuteAsync(plan, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InteractionService.DisplayError(
                string.Format(CultureInfo.CurrentCulture, UninstallCommandStrings.PartialFailureMessage, ex.Message));
            return CommandResult.Failure(ExitCodeConstants.InvalidCommand);
        }

        InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, UninstallCommandStrings.CompletedMessage);
        return CommandResult.Success();
    }

    private void PrintPlanHeader(UninstallPlan plan)
    {
        _ansiConsole.WriteLine();
        _ansiConsole.MarkupLine($"[bold]{string.Format(CultureInfo.CurrentCulture, UninstallCommandStrings.PrefixHeader, plan.Prefix.EscapeMarkup())}[/]");
        var routeDisplay = plan.Route.ToWireString() ?? "unknown";
        _ansiConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, UninstallCommandStrings.RouteHeader, routeDisplay.EscapeMarkup()));
        _ansiConsole.WriteLine();
    }

    private void PrintRemovals(UninstallPlan plan)
    {
        if (plan.Removals.Count == 0)
        {
            return;
        }

        _ansiConsole.MarkupLine($"[bold]{UninstallCommandStrings.PlannedRemovalsHeader.EscapeMarkup()}[/]");
        foreach (var target in plan.Removals)
        {
            _ansiConsole.MarkupLine($"  [red]-[/] {target.EscapeMarkup()}");
        }
        _ansiConsole.WriteLine();
    }

    private void PrintManualSteps(UninstallPlan plan)
    {
        if (plan.ManualSteps.Count == 0)
        {
            return;
        }

        _ansiConsole.MarkupLine($"[bold]{UninstallCommandStrings.ManualStepsHeader.EscapeMarkup()}[/]");
        foreach (var step in plan.ManualSteps)
        {
            _ansiConsole.MarkupLine($"  [yellow]•[/] {step.EscapeMarkup()}");
        }
        _ansiConsole.WriteLine();
    }

}
