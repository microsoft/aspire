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
using Spectre.Console.Rendering;

namespace Aspire.Cli.Commands;

internal sealed class LsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.AppCommands;

    private readonly IInteractionService _interactionService;
    private readonly IProjectLocator _projectLocator;
    private readonly CliExecutionContext _executionContext;
    private readonly ICliHostEnvironment _hostEnvironment;
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
        ICliHostEnvironment hostEnvironment,
        AspireCliTelemetry telemetry,
        ProfilingTelemetry profilingTelemetry)
        : base("ls", SharedCommandStrings.LsCommandDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _projectLocator = projectLocator;
        _executionContext = executionContext;
        _hostEnvironment = hostEnvironment;
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

        try
        {
            // Live rendering is only for human interactive output. JSON and debug output are consumed by
            // tools/logs, and non-interactive hosts may not support terminal cursor rewrites, so those modes
            // wait for discovery to finish and emit one stable payload.
            var useLiveOutput = format != OutputFormat.Json
                && _hostEnvironment.SupportsInteractiveOutput
                && !_executionContext.DebugMode;

            List<AppHostProjectCandidate> appHosts;
            using (var findAppHostsActivity = _profilingTelemetry.StartLsFindAppHosts(scope.ToString()))
            {
                appHosts = useLiveOutput
                    ? await FindAppHostsWithLiveUpdatesAsync(scope, cancellationToken).ConfigureAwait(false)
                    : await _projectLocator.FindAppHostProjectsAsync(_executionContext.WorkingDirectory, scope, cancellationToken).ConfigureAwait(false);
                findAppHostsActivity.SetAppHostCandidateCount(appHosts.Count);
            }
            profilingActivity.SetAppHostCandidateCount(appHosts.Count);

            var appHostInfos = CreateDisplayInfos(appHosts);

            if (format == OutputFormat.Json)
            {
                var json = JsonSerializer.Serialize(appHostInfos, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
                _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
            }
            else if (!useLiveOutput)
            {
                if (appHostInfos.Count == 0)
                {
                    _interactionService.DisplayMessage(KnownEmojis.Information, SharedCommandStrings.LsNoCandidateAppHostsFound);
                }
                else
                {
                    DisplayTable(appHostInfos);
                }
            }

            return CommandResult.Success();
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken || cancellationToken.IsCancellationRequested)
        {
            _interactionService.DisplayCancellationMessage();
            return CommandResult.Success();
        }
    }

    private async Task<List<AppHostProjectCandidate>> FindAppHostsWithLiveUpdatesAsync(AppHostDiscoveryScope scope, CancellationToken cancellationToken)
    {
        var displayLock = new object();
        var liveAppHostInfos = new List<CandidateAppHostDisplayInfo>();
        var appHosts = new List<AppHostProjectCandidate>();

        await _interactionService.DisplayLiveAsync(BuildLiveSearchRenderable(liveAppHostInfos, isSearching: true), async updateTarget =>
        {
            void OnCandidateFound(AppHostProjectCandidate candidate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Candidate validation runs in parallel. Keep the live render state locked while
                // updating it so Spectre.Console never renders a list that another worker is mutating.
                lock (displayLock)
                {
                    liveAppHostInfos.Add(CreateDisplayInfo(candidate));
                    liveAppHostInfos.Sort(CompareByPath);
                    updateTarget(BuildLiveSearchRenderable(liveAppHostInfos, isSearching: true));
                }
            }

            appHosts = await _projectLocator.FindAppHostProjectsAsync(_executionContext.WorkingDirectory, scope, cancellationToken, OnCandidateFound).ConfigureAwait(false);

            // The final frame should match the authoritative sorted result returned by ProjectLocator,
            // not the order callbacks happened to arrive from parallel validation workers.
            lock (displayLock)
            {
                liveAppHostInfos.Clear();
                liveAppHostInfos.AddRange(CreateDisplayInfos(appHosts));
                updateTarget(BuildLiveSearchRenderable(liveAppHostInfos, isSearching: false));
            }
        }).ConfigureAwait(false);

        return appHosts;
    }

    private List<CandidateAppHostDisplayInfo> CreateDisplayInfos(IEnumerable<AppHostProjectCandidate> appHosts)
    {
        return appHosts.Select(CreateDisplayInfo).ToList();
    }

    private CandidateAppHostDisplayInfo CreateDisplayInfo(AppHostProjectCandidate appHost)
    {
        return new CandidateAppHostDisplayInfo
        {
            Path = appHost.AppHostFile.FullName,
            Language = appHost.Language,
            Status = GetDisplayStatus(appHost.Status)
        };
    }

    private static int CompareByPath(CandidateAppHostDisplayInfo x, CandidateAppHostDisplayInfo y)
    {
        return x.Path.CompareTo(y.Path);
    }

    private static IRenderable BuildLiveSearchRenderable(List<CandidateAppHostDisplayInfo> appHosts, bool isSearching)
    {
        if (appHosts.Count == 0)
        {
            return isSearching
                ? new Markup($"[grey]{InteractionServiceStrings.FindingAppHosts.EscapeMarkup()}[/]")
                : new Markup(SharedCommandStrings.LsNoCandidateAppHostsFound.EscapeMarkup());
        }

        var table = BuildTable(appHosts);

        return isSearching
            ? new Rows(new Markup($"[grey]{InteractionServiceStrings.FindingAppHosts.EscapeMarkup()}[/]"), table)
            : table;
    }

    private void DisplayTable(List<CandidateAppHostDisplayInfo> appHosts)
    {
        _interactionService.DisplayRenderable(BuildTable(appHosts));
    }

    private static Table BuildTable(List<CandidateAppHostDisplayInfo> appHosts)
    {
        var table = new Table();
        table.AddBoldColumn(SharedCommandStrings.HeaderPath);
        table.AddBoldColumn(SharedCommandStrings.HeaderLanguage);
        table.AddBoldColumn(SharedCommandStrings.HeaderStatus);

        foreach (var appHost in appHosts)
        {
            table.AddRow(
                Markup.Escape(appHost.Path),
                Markup.Escape(appHost.Language),
                GetStatusMarkup(appHost.Status));
        }

        return table;
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
    public required string Path { get; init; }

    public required string Language { get; init; }

    public required string Status { get; init; }
}
