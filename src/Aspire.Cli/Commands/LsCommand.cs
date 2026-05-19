// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Commands;

internal sealed class LsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IInteractionService _interactionService;
    private readonly IProjectLocator _projectLocator;
    private readonly CliExecutionContext _executionContext;
    private readonly ProfilingTelemetry _profilingTelemetry;

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = SharedCommandStrings.LsFormatOptionDescription
    };

    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = SharedCommandStrings.LsAllOptionDescription
    };

    public LsCommand(
        IInteractionService interactionService,
        IProjectLocator projectLocator,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ProfilingTelemetry profilingTelemetry)
        : base("ls", SharedCommandStrings.LsCommandDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _projectLocator = projectLocator;
        _executionContext = executionContext;
        _profilingTelemetry = profilingTelemetry;

        Options.Add(s_formatOption);
        Options.Add(s_allOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var format = parseResult.GetValue(s_formatOption);
        var includeAll = parseResult.GetValue(s_allOption);
        using var profilingActivity = _profilingTelemetry.StartLsCommand(format.ToString().ToLowerInvariant(), includeAll);

        // `aspire ls` is ambient discovery from the working directory by default, so
        // it should respect git/default filters. `--all` is the explicit escape hatch
        // for users who intentionally want ignored or generated paths included.
        var scope = includeAll
            ? AppHostDiscoveryScope.AllFiles
            : AppHostDiscoveryScope.DefaultFiltered;

        List<AppHostProjectCandidate> appHosts;
        using (var findAppHostsActivity = _profilingTelemetry.StartLsFindAppHosts(scope.ToString()))
        {
            appHosts = await _projectLocator.FindAppHostProjectsAsync(_executionContext.WorkingDirectory, scope, cancellationToken).ConfigureAwait(false);
            findAppHostsActivity.SetAppHostCandidateCount(appHosts.Count);
        }
        profilingActivity.SetAppHostCandidateCount(appHosts.Count);

        var appHostInfos = appHosts.Select(a => new CandidateAppHostDisplayInfo
        {
            RelativePath = System.IO.Path.GetRelativePath(_executionContext.WorkingDirectory.FullName, a.AppHostFile.FullName),
            Path = a.AppHostFile.FullName,
            Language = a.Language,
            Status = GetDisplayStatus(a.Status)
        }).ToList();

        if (format == OutputFormat.Json)
        {
            var json = JsonSerializer.Serialize(appHostInfos, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else if (appHostInfos.Count == 0)
        {
            _interactionService.DisplayMessage(KnownEmojis.Information, SharedCommandStrings.LsNoCandidateAppHostsFound);
        }
        else
        {
            DisplayTable(appHostInfos);
        }

        return CommandResult.Success();
    }

    private void DisplayTable(List<CandidateAppHostDisplayInfo> appHosts)
    {
        var table = new Table();
        table.AddBoldColumn(SharedCommandStrings.HeaderRelativePath);
        table.AddBoldColumn(SharedCommandStrings.HeaderPath);
        table.AddBoldColumn(SharedCommandStrings.HeaderLanguage);
        table.AddBoldColumn(SharedCommandStrings.HeaderStatus);

        foreach (var appHost in appHosts)
        {
            table.AddRow(
                Markup.Escape(appHost.RelativePath),
                Markup.Escape(appHost.Path),
                Markup.Escape(appHost.Language),
                GetStatusMarkup(appHost.Status));
        }

        _interactionService.DisplayRenderable(table);
    }

    private static string GetDisplayStatus(AppHostProjectCandidateStatus status)
    {
        return status switch
        {
            AppHostProjectCandidateStatus.Buildable => "buildable",
            AppHostProjectCandidateStatus.PossiblyUnbuildable => "possibly-unbuildable",
            _ => status.ToString().ToLowerInvariant()
        };
    }

    private static string GetStatusMarkup(string status)
    {
        return status switch
        {
            "buildable" => "[green]buildable[/]",
            "possibly-unbuildable" => "[yellow]possibly-unbuildable[/]",
            _ => Markup.Escape(status)
        };
    }
}

internal sealed class CandidateAppHostDisplayInfo
{
    public required string RelativePath { get; init; }

    public required string Path { get; init; }

    public required string Language { get; init; }

    public required string Status { get; init; }
}
