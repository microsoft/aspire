// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
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
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ConsoleEnvironment _consoleEnvironment;
    private readonly ProfilingTelemetry _profilingTelemetry;
    private readonly TimeProvider _timeProvider;

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = SharedCommandStrings.LsFormatOptionDescription
    };

    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = SharedCommandStrings.LsAllOptionDescription
    };

    private static readonly Option<bool> s_streamOption = new("--stream")
    {
        Description = SharedCommandStrings.LsStreamOptionDescription
    };

    public LsCommand(
        IInteractionService interactionService,
        IProjectLocator projectLocator,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        ICliHostEnvironment hostEnvironment,
        ConsoleEnvironment consoleEnvironment,
        AspireCliTelemetry telemetry,
        ProfilingTelemetry profilingTelemetry,
        TimeProvider? timeProvider = null)
        : base("ls", SharedCommandStrings.LsCommandDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _interactionService = interactionService;
        _projectLocator = projectLocator;
        _executionContext = executionContext;
        _hostEnvironment = hostEnvironment;
        _consoleEnvironment = consoleEnvironment;
        _profilingTelemetry = profilingTelemetry;
        _timeProvider = timeProvider ?? TimeProvider.System;

        Options.Add(s_formatOption);
        Options.Add(s_allOption);
        Options.Add(s_streamOption);

        Validators.Add(result =>
        {
            if (result.GetValue(s_streamOption) && result.GetValue(s_formatOption) != OutputFormat.Json)
            {
                result.AddError(SharedCommandStrings.LsStreamRequiresJson);
            }
        });
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var format = parseResult.GetValue(s_formatOption);
        var includeAll = parseResult.GetValue(s_allOption);
        var stream = parseResult.GetValue(s_streamOption);
        using var profilingActivity = _profilingTelemetry.StartLsCommand(format.ToString().ToLowerInvariant(), includeAll);

        // `aspire ls` is ambient discovery from the working directory by default, so
        // it should respect git/default filters. `--all` is the explicit escape hatch
        // for users who intentionally want ignored or generated paths included.
        var scope = includeAll
            ? AppHostDiscoveryScope.AllFiles
            : AppHostDiscoveryScope.DefaultFiltered;

        try
        {
            var useJsonStream = format == OutputFormat.Json && stream;

            // Status-based progress (with directories searched / AppHosts found) is only useful for the human
            // table view in an interactive terminal. JSON consumers receive structured data instead, and
            // non-interactive hosts may not support spinner status, so they wait for the final table.
            var useInteractiveStatus = format == OutputFormat.Table
                && _hostEnvironment.SupportsInteractiveOutput
                && !_executionContext.DebugMode;

            List<AppHostProjectCandidate> appHosts;
            using (var findAppHostsActivity = _profilingTelemetry.StartLsFindAppHosts(scope.ToString()))
            {
                appHosts = (useInteractiveStatus, useJsonStream) switch
                {
                    (true, _) => await FindAppHostsWithStatusAsync(scope, cancellationToken).ConfigureAwait(false),
                    (_, true) => await FindAppHostsWithJsonStreamAsync(scope, cancellationToken).ConfigureAwait(false),
                    _ => await _projectLocator.FindAppHostProjectsAsync(_executionContext.WorkingDirectory, scope, cancellationToken).ConfigureAwait(false)
                };
                findAppHostsActivity.SetAppHostCandidateCount(appHosts.Count);
            }
            profilingActivity.SetAppHostCandidateCount(appHosts.Count);

            var appHostInfos = CreateDisplayInfos(appHosts);

            if (format == OutputFormat.Json && !useJsonStream)
            {
                var json = JsonSerializer.Serialize(appHostInfos, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
                _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
            }
            else if (!useJsonStream)
            {
                // Both interactive and non-interactive table output land here so the final table render is
                // identical: the only difference is whether discovery showed a live spinner status above.
                if (appHostInfos.Count == 0)
                {
                    _interactionService.DisplayMessage(KnownEmojis.Information, SharedCommandStrings.LsNoCandidateAppHostsFound);
                }
                else
                {
                    _interactionService.DisplayRenderable(BuildTable(appHostInfos, _interactionService.SupportsLinks));
                }
            }

            return CommandResult.Success();
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken || cancellationToken.IsCancellationRequested)
        {
            if (format != OutputFormat.Json || !stream)
            {
                _interactionService.DisplayCancellationMessage();
            }

            return CommandResult.Success();
        }
    }

    private async Task<List<AppHostProjectCandidate>> FindAppHostsWithJsonStreamAsync(AppHostDiscoveryScope scope, CancellationToken cancellationToken)
    {
        var appHosts = new List<AppHostProjectCandidate>();

        await foreach (var candidate in _projectLocator.FindAppHostProjectsStreamAsync(_executionContext.WorkingDirectory, scope, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            appHosts.Add(candidate);
            WriteJsonStreamCandidate(CreateDisplayInfo(candidate));
        }

        appHosts.Sort((x, y) => string.Compare(x.AppHostFile.FullName, y.AppHostFile.FullName, StringComparison.Ordinal));

        return appHosts;
    }

    private void WriteJsonStreamCandidate(CandidateAppHostDisplayInfo candidate)
    {
        var json = JsonSerializer.Serialize(candidate, JsonSourceGenerationContext.Streaming.CandidateAppHostDisplayInfo);

        // `aspire ls --format json --stream` follows the repository-wide NDJSON convention:
        // each line is the same JSON content shape that would otherwise appear as an array
        // item, not a separate event envelope.
        // See https://github.com/ndjson/ndjson-spec for the line-delimited JSON convention.
        // Example:
        //   {"path":"C:\\repo\\AppHost.csproj","language":"C#","status":"buildable"}
        // Keep docs/specs/cli-output-formats.md in sync when changing this shape.
        var writer = _consoleEnvironment.Out.Profile.Out.Writer;
        writer.WriteLine(json);
        writer.Flush();
    }

    private async Task<List<AppHostProjectCandidate>> FindAppHostsWithStatusAsync(AppHostDiscoveryScope scope, CancellationToken cancellationToken)
    {
        var appHosts = new List<AppHostProjectCandidate>();
        // Counters are mutated from the discovery worker thread (directory enumeration is single-threaded, and
        // validation runs in parallel via Parallel.ForEachAsync), but read from the status-refresh loop on a
        // different thread. Use Volatile/Interlocked so the refresh loop observes recent values without locking.
        var directoriesSearched = 0;
        var appHostsFound = 0;

        // Re-render the status text at most ~once per second so a deep filesystem walk doesn't spam the terminal,
        // but still gives the user a visible heartbeat that work is happening.
        var statusRefreshInterval = TimeSpan.FromSeconds(1);

        await _interactionService.ShowDynamicStatusAsync(
            FormatSearchingStatus(directoriesSearched: 0, appHostsFound: 0),
            async updateStatus =>
            {
                using var statusCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var refreshTask = RefreshStatusLoopAsync(updateStatus, statusCancellation.Token);

                try
                {
                    await foreach (var candidate in _projectLocator
                        .FindAppHostProjectsStreamAsync(
                            _executionContext.WorkingDirectory,
                            scope,
                            onDirectoryEnumerated: count => Volatile.Write(ref directoriesSearched, count),
                            cancellationToken: cancellationToken)
                        .ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        appHosts.Add(candidate);
                        Interlocked.Increment(ref appHostsFound);
                    }
                }
                finally
                {
                    updateStatus(FormatSearchingStatus(Volatile.Read(ref directoriesSearched), Volatile.Read(ref appHostsFound)));
                    statusCancellation.Cancel();
                    try
                    {
                        await refreshTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                return CliExitCodes.Success;

                async Task RefreshStatusLoopAsync(Action<string> update, CancellationToken refreshToken)
                {
                    try
                    {
                        while (!refreshToken.IsCancellationRequested)
                        {
                            await Task.Delay(statusRefreshInterval, _timeProvider, refreshToken).ConfigureAwait(false);
                            update(FormatSearchingStatus(Volatile.Read(ref directoriesSearched), Volatile.Read(ref appHostsFound)));
                        }
                    }
                    catch (OperationCanceledException) when (refreshToken.IsCancellationRequested)
                    {
                    }
                }
            }).ConfigureAwait(false);

        appHosts.Sort((x, y) => string.Compare(x.AppHostFile.FullName, y.AppHostFile.FullName, StringComparison.Ordinal));
        return appHosts;
    }

    private static string FormatSearchingStatus(int directoriesSearched, int appHostsFound)
    {
        return string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.LsSearchingForAppHostsStatus, directoriesSearched, appHostsFound);
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

    private static Table BuildTable(List<CandidateAppHostDisplayInfo> appHosts, bool supportsLinks)
    {
        var table = new Table();
        table.AddBoldColumn(SharedCommandStrings.HeaderPath);
        table.AddBoldColumn(SharedCommandStrings.HeaderLanguage);
        table.AddBoldColumn(SharedCommandStrings.HeaderStatus);

        foreach (var appHost in appHosts)
        {
            table.AddRow(
                MarkupHelpers.SafeFileLink(supportsLinks, appHost.Path),
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

// `aspire ls --format json` uses this shape; keep docs/specs/cli-output-formats.md in sync when changing it.
internal sealed class CandidateAppHostDisplayInfo
{
    public required string Path { get; init; }

    public required string Language { get; init; }

    public required string Status { get; init; }
}
