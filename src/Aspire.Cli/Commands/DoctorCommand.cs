// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils.EnvironmentChecker;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class DoctorCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    // The cli-version environment check already surfaces "newer version available"
    // information directly inside `checks[]` with structured metadata. The trailing
    // BaseCommand-driven update banner would print a second, less-structured copy on
    // stderr — pure duplication, plus noise for JSON consumers. Matches the convention
    // already used by other --format json commands (ApiGet, ApiList, DocsSearch, DocsList).

    private readonly IEnvironmentChecker _environmentChecker;
    private readonly IInstallationDiscovery _installationDiscovery;
    private readonly WingetFirstRunProbe _wingetFirstRunProbe;
    private readonly IAnsiConsole _ansiConsole;
    private readonly ILogger<DoctorCommand> _logger;
    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = DoctorCommandStrings.JsonOptionDescription
    };
    private static readonly Option<bool> s_selfOption = new("--self")
    {
        Hidden = true,
    };

    public DoctorCommand(
        IEnvironmentChecker environmentChecker,
        IInstallationDiscovery installationDiscovery,
        WingetFirstRunProbe wingetFirstRunProbe,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        IAnsiConsole ansiConsole,
        ILogger<DoctorCommand> logger,
        AspireCliTelemetry telemetry)
        : base("doctor", DoctorCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _environmentChecker = environmentChecker;
        _installationDiscovery = installationDiscovery;
        _wingetFirstRunProbe = wingetFirstRunProbe;
        _ansiConsole = ansiConsole;
        _logger = logger;

        Options.Add(s_formatOption);
        Options.Add(s_selfOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var format = parseResult.GetValue(s_formatOption);
        var selfOnly = parseResult.GetValue(s_selfOption);

        if (selfOnly)
        {
            var self = InstallationInfoOutput.DescribeSelfSafely(_installationDiscovery, _logger);
            if (format == OutputFormat.Json)
            {
                OutputJson([], self, includeSingleInstallation: true);
            }
            else
            {
                InstallationInfoOutput.OutputTable(_ansiConsole, self);
            }
            return CommandResult.Success();
        }

        var installationsTask = InstallationInfoOutput.DiscoverAllSafelyAsync(_installationDiscovery, _wingetFirstRunProbe, _logger, cancellationToken);

        // Run all prerequisite checks
        var results = await InteractionService.ShowStatusAsync(
            DoctorCommandStrings.CheckingPrerequisites,
            async () => await _environmentChecker.CheckAllAsync(cancellationToken));
        var installations = await installationsTask;
        results = AddInstallationCheck(results, installations);

        if (format == OutputFormat.Json)
        {
            OutputJson(results, installations);
        }
        else
        {
            OutputHumanReadable(results, installations);
        }

        // Exit code: 0 if no failures (warnings are OK), 1 (InvalidCommand) if any failures
        var hasFailures = results.Any(r => r.Status == EnvironmentCheckStatus.Fail);
        return CommandResult.FromExitCode(hasFailures ? CliExitCodes.InvalidCommand : CliExitCodes.Success);
    }

    private void OutputJson(IReadOnlyList<EnvironmentCheckResult> results, IReadOnlyList<InstallationInfo> installations, bool includeSingleInstallation = false)
    {
        var passed = results.Count(r => r.Status == EnvironmentCheckStatus.Pass);
        var info = results.Count(r => r.Status == EnvironmentCheckStatus.Info);
        var warnings = results.Count(r => r.Status == EnvironmentCheckStatus.Warning);
        var failed = results.Count(r => r.Status == EnvironmentCheckStatus.Fail);

        var response = new DoctorCheckResponse
        {
            Checks = results.ToList(),
            Summary = new DoctorCheckSummary
            {
                Passed = passed,
                Info = info,
                Warnings = warnings,
                Failed = failed
            },
            Installations = installations.Count > 1 || includeSingleInstallation ? installations.ToList() : null
        };

        var json = System.Text.Json.JsonSerializer.Serialize(response, JsonSourceGenerationContext.RelaxedEscaping.DoctorCheckResponse);
        // Use DisplayRawText to write directly to console without any formatting
        // Structured output always goes to stdout.
        InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
    }

    private void OutputHumanReadable(IReadOnlyList<EnvironmentCheckResult> results, IReadOnlyList<InstallationInfo> installations)
    {
        _ansiConsole.WriteLine();
        _ansiConsole.MarkupLine($"[bold]{DoctorCommandStrings.EnvironmentCheckHeader}[/]");
        _ansiConsole.WriteLine(new string('=', DoctorCommandStrings.EnvironmentCheckHeader.Length));
        _ansiConsole.WriteLine();

        // Group results by category
        var groupedResults = results
            .GroupBy(r => r.Category)
            .OrderBy(g => GetCategoryOrder(g.Key));

        foreach (var group in groupedResults)
        {
            var categoryHeader = GetCategoryHeader(group.Key);
            _ansiConsole.MarkupLine($"[bold]{categoryHeader}[/]");

            foreach (var result in group)
            {
                OutputCheckResult(result);
            }

            _ansiConsole.WriteLine();
        }

        // Output summary
        var passed = results.Count(r => r.Status == EnvironmentCheckStatus.Pass);
        var info = results.Count(r => r.Status == EnvironmentCheckStatus.Info);
        var warnings = results.Count(r => r.Status == EnvironmentCheckStatus.Warning);
        var failed = results.Count(r => r.Status == EnvironmentCheckStatus.Fail);

        _ansiConsole.MarkupLine($"[bold]{string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.SummaryFormat, passed, info, warnings, failed)}[/]");

        // Show link to detailed prerequisites if there are warnings or failures
        if (warnings > 0 || failed > 0)
        {
            const string prerequisitesUrl = "https://aka.ms/aspire-prerequisites";
            _ansiConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DetailedPrerequisitesLink, MarkupHelpers.SafeLink(InteractionService, prerequisitesUrl)));
        }

        if (installations.Count > 1)
        {
            InstallationInfoOutput.OutputTable(_ansiConsole, installations);
        }
    }

    private void OutputCheckResult(EnvironmentCheckResult result)
    {
        var (icon, color) = GetStatusIconAndColor(result.Status);
        var iconPrefix = ConsoleHelpers.FormatEmojiPrefix(icon, _ansiConsole, suppressColor: true);

        // Primary grid: icon + message (wrapped lines stay aligned with message text)
        var messageGrid = new Grid();
        messageGrid.AddColumn();
        messageGrid.AddRow(
            new Markup($"[{color}]{iconPrefix}{result.Message.EscapeMarkup()}[/]"));

        _ansiConsole.Write(new Padder(messageGrid, new Padding(2, 0)));

        // Secondary grid: details, fix suggestions, and links (indented further than message)
        var hasDetails = !string.IsNullOrEmpty(result.Details);
        var hasFix = !string.IsNullOrEmpty(result.Fix);
        var hasLink = !string.IsNullOrEmpty(result.Link);

        if (hasDetails || hasFix || hasLink)
        {
            var detailGrid = new Grid();
            detailGrid.AddColumn();

            if (hasFix)
            {
                var fixLines = result.Fix!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in fixLines)
                {
                    detailGrid.AddRow(new Markup($"{line.Trim().EscapeMarkup()}"));
                }
            }

            if (hasLink)
            {
                detailGrid.AddRow(new Markup($"See: {MarkupHelpers.SafeLink(InteractionService, result.Link!)}"));
            }

            if (hasDetails)
            {
                detailGrid.AddRow(new Markup($"[dim]Details:[/]"));
                detailGrid.AddRow(new Markup($"[dim]{result.Details!.EscapeMarkup()}[/]"));
            }

            _ansiConsole.Write(new Padder(detailGrid, new Padding(7, 0)));
        }
    }

    private static (KnownEmoji Icon, string Color) GetStatusIconAndColor(EnvironmentCheckStatus status)
    {
        return status switch
        {
            EnvironmentCheckStatus.Pass => (KnownEmojis.CheckMarkButton, "green"),
            EnvironmentCheckStatus.Info => (KnownEmojis.Information, "blue"),
            EnvironmentCheckStatus.Warning => (KnownEmojis.Warning, "yellow"),
            EnvironmentCheckStatus.Fail => (KnownEmojis.CrossMark, "red"),
            _ => (KnownEmojis.Information, "grey")
        };
    }

    private static IReadOnlyList<EnvironmentCheckResult> AddInstallationCheck(IReadOnlyList<EnvironmentCheckResult> results, IReadOnlyList<InstallationInfo> installations)
    {
        if (installations.Count == 0 || IsDiscoveryFailurePlaceholder(installations))
        {
            return results;
        }

        var installationCheck = new EnvironmentCheckResult
        {
            Category = "cli",
            Name = "cli-installations",
            Status = installations.Count == 1 ? EnvironmentCheckStatus.Pass : EnvironmentCheckStatus.Info,
            Message = installations.Count == 1
                ? DoctorCommandStrings.CliInstallationsSingleMessage
                : string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.CliInstallationsMultipleMessageFormat, installations.Count),
            Metadata = new JsonObject
            {
                ["installationCount"] = installations.Count
            }
        };

        return [.. results, installationCheck];
    }

    private static bool IsDiscoveryFailurePlaceholder(IReadOnlyList<InstallationInfo> installations)
        => installations is
        [
            {
                Status: InstallationInfoStatus.Failed,
                StatusReason: var statusReason
            }
        ] && string.Equals(statusReason, DoctorCommandStrings.InstallationDiscoveryFailedReason, StringComparison.Ordinal);

    private static string GetCategoryHeader(string category)
    {
        return category switch
        {
            "sdk" => DoctorCommandStrings.SdkCategoryHeader,
            "aspire" => DoctorCommandStrings.AspireCategoryHeader,
            "cli" => DoctorCommandStrings.CliCategoryHeader,
            "apphost" => DoctorCommandStrings.AppHostCategoryHeader,
            "container" => DoctorCommandStrings.ContainerCategoryHeader,
            "environment" => DoctorCommandStrings.EnvironmentCategoryHeader,
            _ => category
        };
    }

    private static int GetCategoryOrder(string category)
    {
        return category switch
        {
            "aspire" => 0,
            "cli" => 1,
            "apphost" => 2,
            "sdk" => 3,
            "container" => 4,
            "environment" => 5,
            _ => 99
        };
    }
}
