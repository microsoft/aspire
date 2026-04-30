// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils.EnvironmentChecker;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class DoctorCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    private readonly IEnvironmentChecker _environmentChecker;
    private readonly IAnsiConsole _ansiConsole;
    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = DoctorCommandStrings.JsonOptionDescription
    };

    public DoctorCommand(
        IEnvironmentChecker environmentChecker,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        IAnsiConsole ansiConsole,
        AspireCliTelemetry telemetry)
        : base("doctor", DoctorCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _environmentChecker = environmentChecker;
        _ansiConsole = ansiConsole;

        Options.Add(s_formatOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var format = parseResult.GetValue(s_formatOption);

        // Run all prerequisite checks
        var results = await InteractionService.ShowStatusAsync(
            DoctorCommandStrings.CheckingPrerequisites,
            async () => await _environmentChecker.CheckAllAsync(cancellationToken));

        if (format == OutputFormat.Json)
        {
            OutputJson(results);
        }
        else
        {
            OutputHumanReadable(results);
        }

        // Exit code: 0 if no failures (warnings are OK), 1 (InvalidCommand) if any failures
        var hasFailures = results.Any(r => r.Status == EnvironmentCheckStatus.Fail);
        return hasFailures ? ExitCodeConstants.InvalidCommand : ExitCodeConstants.Success;
    }

    private void OutputJson(IReadOnlyList<EnvironmentCheckResult> results)
    {
        var passed = results.Count(r => r.Status == EnvironmentCheckStatus.Pass);
        var warnings = results.Count(r => r.Status == EnvironmentCheckStatus.Warning);
        var failed = results.Count(r => r.Status == EnvironmentCheckStatus.Fail);

        var response = new DoctorCheckResponse
        {
            Checks = results.ToList(),
            Summary = new DoctorCheckSummary
            {
                Passed = passed,
                Warnings = warnings,
                Failed = failed
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(response, JsonSourceGenerationContext.RelaxedEscaping.DoctorCheckResponse);
        // Use DisplayRawText to write directly to console without any formatting
        // Structured output always goes to stdout.
        InteractionService.DisplayRawText(json, ConsoleOutput.Standard);
    }

    private void OutputHumanReadable(IReadOnlyList<EnvironmentCheckResult> results)
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
        var warnings = results.Count(r => r.Status == EnvironmentCheckStatus.Warning);
        var failed = results.Count(r => r.Status == EnvironmentCheckStatus.Fail);

        _ansiConsole.MarkupLine($"[bold]{string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.SummaryFormat, passed, warnings, failed)}[/]");

        // Show link to detailed prerequisites if there are warnings or failures
        if (warnings > 0 || failed > 0)
        {
            const string prerequisitesUrl = "https://aka.ms/aspire-prerequisites";
            _ansiConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, DoctorCommandStrings.DetailedPrerequisitesLink, $"[link]{prerequisitesUrl}[/]"));
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
                detailGrid.AddRow(new Markup($"See: [link={result.Link!.EscapeMarkup()}]{result.Link!.EscapeMarkup()}[/]"));
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
            EnvironmentCheckStatus.Warning => (KnownEmojis.Warning, "yellow"),
            EnvironmentCheckStatus.Fail => (KnownEmojis.CrossMark, "red"),
            _ => (KnownEmojis.Information, "grey")
        };
    }

    private static string GetCategoryHeader(string category)
    {
        return category switch
        {
            "sdk" => DoctorCommandStrings.SdkCategoryHeader,
            "container" => DoctorCommandStrings.ContainerCategoryHeader,
            "environment" => DoctorCommandStrings.EnvironmentCategoryHeader,
            _ => category
        };
    }

    private static int GetCategoryOrder(string category)
    {
        return category switch
        {
            "sdk" => 1,
            "container" => 2,
            "environment" => 3,
            _ => 99
        };
    }
}
