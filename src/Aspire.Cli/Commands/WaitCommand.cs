// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Hosting.ApplicationModel;
using Aspire.Shared.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

// `aspire wait --format json` uses this wrapper; keep docs/specs/cli-output-formats.md in sync when changing it.
internal sealed class WaitOutput
{
    public required string Outcome { get; init; }
    public required string Status { get; init; }
    public required WaitResourceJson[] Resources { get; init; }
}

internal sealed class WaitResourceJson
{
    public required string Name { get; init; }
    public string? DisplayName { get; init; }
    public required string Outcome { get; init; }
    public string? State { get; init; }
    public string? HealthStatus { get; init; }
    public int? ExitCode { get; init; }
    public ResourceUrlJson[] Urls { get; init; } = [];
    public string? Error { get; init; }
}

[JsonSerializable(typeof(WaitOutput))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WaitCommandJsonContext : JsonSerializerContext
{
    private static WaitCommandJsonContext? s_relaxedEscaping;

    /// <summary>
    /// Gets a context with relaxed JSON escaping for non-ASCII character support.
    /// </summary>
    public static WaitCommandJsonContext RelaxedEscaping => s_relaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

internal sealed class WaitCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.ResourceManagement;

    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<WaitCommand> _logger;
    private readonly ResourceWaitService _resourceWaitService;
    private readonly TimeProvider _timeProvider;

    private static readonly Argument<string[]> s_resourceArgument = new("resource")
    {
        Description = WaitCommandStrings.ResourceArgumentDescription,
        Arity = ArgumentArity.ZeroOrMore
    };

    private static readonly Option<bool> s_allOption = new("--all")
    {
        Description = WaitCommandStrings.AllOptionDescription
    };

    private static readonly Option<string> s_statusOption = new("--status")
    {
        Description = WaitCommandStrings.StatusOptionDescription,
        DefaultValueFactory = _ => "healthy"
    };

    internal const int DefaultTimeoutSeconds = 120;

    private static readonly Option<int> s_timeoutOption = new("--timeout")
    {
        Description = WaitCommandStrings.TimeoutOptionDescription,
        DefaultValueFactory = _ => DefaultTimeoutSeconds
    };

    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = WaitCommandStrings.FormatOptionDescription
    };

    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);

    public WaitCommand(
        AppHostConnectionResolver connectionResolver,
        ILogger<WaitCommand> logger,
        ResourceWaitService resourceWaitService,
        CommonCommandServices services,
        TimeProvider timeProvider)
        : base("wait", WaitCommandStrings.Description, services)
    {
        _connectionResolver = connectionResolver;
        _logger = logger;
        _resourceWaitService = resourceWaitService;
        _timeProvider = timeProvider;

        Arguments.Add(s_resourceArgument);
        Options.Add(s_allOption);
        Options.Add(s_statusOption);
        Options.Add(s_timeoutOption);
        Options.Add(s_formatOption);
        Options.Add(s_appHostOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceNames = (parseResult.GetValue(s_resourceArgument) ?? [])
            .Distinct(StringComparers.ResourceName)
            .ToArray();
        var waitForAll = parseResult.GetValue(s_allOption);
        var status = parseResult.GetValue(s_statusOption)!.ToLowerInvariant();
        var timeoutSeconds = parseResult.GetValue(s_timeoutOption);
        var format = parseResult.GetValue(s_formatOption);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);

        if (resourceNames.Length == 0 && !waitForAll)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, WaitCommandStrings.ResourceOrAllRequired);
        }

        if (resourceNames.Length > 0 && waitForAll)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, WaitCommandStrings.ResourceAndAllConflict);
        }

        // Validate status value
        if (!IsValidStatus(status))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.InvalidStatusValue, status));
        }

        // Validate timeout
        if (timeoutSeconds <= 0)
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, WaitCommandStrings.TimeoutMustBePositive);
        }

        // Resolve connection to a running AppHost
        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, WaitCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            return CommandResult.FromExitCode(AppHostConnectionResultHandler.DisplayFailureAsError(result, InteractionService, CliExitCodes.FailedToFindProject));
        }

        var connection = result.Connection!;

        // The single-name human output predates batch waits; keep its messages so existing scripts
        // and documentation that match on them keep working.
        if (!waitForAll && resourceNames.Length == 1 && format != OutputFormat.Json)
        {
            return CommandResult.FromExitCode(await WaitForResourceAsync(connection, resourceNames[0], status, timeoutSeconds, cancellationToken));
        }

        return CommandResult.FromExitCode(await WaitForResourcesAsync(connection, resourceNames, waitForAll, status, timeoutSeconds, format, cancellationToken));
    }

    private async Task<int> WaitForResourceAsync(
        IAppHostAuxiliaryBackchannel connection,
        string resourceName,
        string status,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var statusLabel = GetStatusLabel(status);

        _logger.LogDebug("Waiting for resource '{ResourceName}' to reach status '{Status}' with timeout {Timeout}s", resourceName, status, timeoutSeconds);

        var startTimestamp = _timeProvider.GetTimestamp();

        var response = await InteractionService.ShowStatusAsync<ResourceWaitResult>(
            string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResource, resourceName, statusLabel),
            async () =>
            {
                var results = await _resourceWaitService.WaitUntilDoneAsync(
                    connection,
                    [resourceName],
                    GetWaitTarget(status),
                    timeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                return results[0];
            });

        // Reset cursor position after spinner
        InteractionService.DisplayPlainText("");

        var elapsed = _timeProvider.GetElapsedTime(startTimestamp);
        switch (response.Outcome)
        {
            case ResourceWaitOutcome.Success:
                InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceReachedTargetStatus, resourceName, statusLabel, elapsed.TotalSeconds));
                return CliExitCodes.Success;

            case ResourceWaitOutcome.Completed:
                InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceCompleted, resourceName, elapsed.TotalSeconds));
                return CliExitCodes.Success;

            case ResourceWaitOutcome.Timeout:
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitTimedOut, resourceName, statusLabel, timeoutSeconds));
                return CliExitCodes.WaitTimeout;

            case ResourceWaitOutcome.Failure when response.ResourceNotFound:
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceNotFound, resourceName));
                return CliExitCodes.WaitResourceFailed;

            default:
                InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceEnteredFailedState, resourceName, FormatState(response.State, response.ExitCode) ?? response.ErrorMessage));
                return CliExitCodes.WaitResourceFailed;
        }
    }

    private async Task<int> WaitForResourcesAsync(
        IAppHostAuxiliaryBackchannel connection,
        IReadOnlyList<string> requestedNames,
        bool waitForAll,
        string status,
        int timeoutSeconds,
        OutputFormat format,
        CancellationToken cancellationToken)
    {
        var statusLabel = GetStatusLabel(status);
        var startTimestamp = _timeProvider.GetTimestamp();

        IReadOnlyList<string> namesToWait;
        IReadOnlyList<string> skippedNames = [];
        if (waitForAll)
        {
            var snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
            var visible = snapshots.Where(static snapshot => !ResourceSnapshotMapper.IsHiddenResource(snapshot)).ToArray();

            // Resources stay NotStarted only when they wait for an explicit start: WithExplicitStart,
            // delayed-start DCP resources, or browser log sessions. Auto-started resources move to
            // Starting or Waiting instead, so waiting on a NotStarted resource would only time out.
            skippedNames = visible.Where(IsWaitingForExplicitStart).Select(static snapshot => snapshot.Name).ToArray();
            namesToWait = visible.Where(static snapshot => !IsWaitingForExplicitStart(snapshot)).Select(static snapshot => snapshot.Name).ToArray();
        }
        else
        {
            namesToWait = requestedNames;
        }

        _logger.LogDebug("Waiting for {Count} resources to reach status '{Status}' with timeout {Timeout}s", namesToWait.Count, status, timeoutSeconds);

        IReadOnlyList<ResourceWaitResult> results = namesToWait.Count == 0
            ? []
            : await InteractionService.ShowStatusAsync(
                waitForAll
                    ? string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForAllResources, statusLabel)
                    : string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResources, namesToWait.Count, statusLabel),
                () => _resourceWaitService.WaitUntilDoneAsync(
                    connection,
                    namesToWait,
                    GetWaitTarget(status),
                    timeoutSeconds,
                    cancellationToken));

        var elapsed = _timeProvider.GetElapsedTime(startTimestamp);

        // Read the final snapshots once so every line can show the resource's current state and
        // endpoints without a follow-up `aspire describe`.
        var finalSnapshots = await GetFinalSnapshotsAsync(connection, cancellationToken).ConfigureAwait(false);
        var reports = results
            .Select(r => CreateReport(r, finalSnapshots, waitForAll))
            .Concat(skippedNames.Select(name => CreateSkippedReport(name, finalSnapshots)))
            .ToArray();

        var exitCode = results.Any(static r => r.Outcome == ResourceWaitOutcome.Failure)
            ? CliExitCodes.WaitResourceFailed
            : results.Any(static r => r.Outcome == ResourceWaitOutcome.Timeout)
                ? CliExitCodes.WaitTimeout
                : CliExitCodes.Success;

        if (format == OutputFormat.Json)
        {
            var output = new WaitOutput
            {
                Outcome = exitCode switch
                {
                    CliExitCodes.Success => "success",
                    CliExitCodes.WaitTimeout => "timeout",
                    _ => "failure"
                },
                Status = status,
                Resources = reports.Select(ToJson).ToArray()
            };
            InteractionService.DisplayRawText(
                JsonSerializer.Serialize(output, WaitCommandJsonContext.RelaxedEscaping.WaitOutput),
                ConsoleOutput.Standard);
            return exitCode;
        }

        // Reset cursor position after spinner
        InteractionService.DisplayPlainText("");

        if (reports.Length == 0)
        {
            InteractionService.DisplayMessage(KnownEmojis.Information, WaitCommandStrings.NoResourcesToWaitFor);
            return exitCode;
        }

        foreach (var report in reports)
        {
            DisplayReport(report, statusLabel);
        }

        var waitedCount = results.Count;
        switch (exitCode)
        {
            case CliExitCodes.Success:
                InteractionService.DisplaySuccess(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.AllResourcesDone, waitedCount, elapsed.TotalSeconds));
                break;
            case CliExitCodes.WaitTimeout:
                InteractionService.DisplayError(string.Format(
                    CultureInfo.CurrentCulture,
                    WaitCommandStrings.ResourcesTimedOut,
                    results.Count(static r => r.Outcome == ResourceWaitOutcome.Timeout),
                    waitedCount,
                    statusLabel,
                    timeoutSeconds));
                break;
            default:
                InteractionService.DisplayError(string.Format(
                    CultureInfo.CurrentCulture,
                    WaitCommandStrings.ResourcesFailed,
                    results.Count(static r => r.Outcome == ResourceWaitOutcome.Failure),
                    waitedCount));
                break;
        }

        return exitCode;
    }

    private async Task<IReadOnlyList<ResourceSnapshot>> GetFinalSnapshotsAsync(IAppHostAuxiliaryBackchannel connection, CancellationToken cancellationToken)
    {
        try
        {
            return await connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The wait results are still meaningful without the extra state and endpoint details.
            _logger.LogDebug(ex, "Unable to read resource snapshots after waiting.");
            return [];
        }
    }

    private static WaitReport CreateReport(ResourceWaitResult result, IReadOnlyList<ResourceSnapshot> snapshots, bool waitForAll)
    {
        var matches = ResourceWaitService.FindResourceSnapshots(snapshots, result.ResourceName);
        var snapshot = matches.Count == 1 ? matches[0] : null;

        // --all waits on runtime names, which can carry a replica suffix. Show the friendly name like
        // `aspire describe` does; explicitly requested names are echoed back as typed.
        var displayName = waitForAll && snapshot is not null
            ? ResourceSnapshotMapper.GetResourceName(snapshot, snapshots)
            : result.ResourceName;

        // The wait result reflects the state that ended the wait. Use only it for failures and completions:
        // a failure without a state (an ambiguous replica name, an AppHost without wait support) must show
        // its error message rather than the resource's unrelated current state. Otherwise prefer the
        // fresh snapshot.
        var useResultState = result.Outcome is ResourceWaitOutcome.Failure or ResourceWaitOutcome.Completed;
        return new WaitReport(
            snapshot?.Name ?? result.ResourceName,
            displayName,
            snapshot?.DisplayName,
            result.Outcome,
            Skipped: false,
            useResultState ? result.State : snapshot?.State ?? result.State,
            snapshot?.HealthStatus ?? result.Health,
            useResultState ? result.ExitCode : snapshot?.ExitCode,
            result.ResourceNotFound,
            matches.SelectMany(static s => s.Urls).ToArray(),
            result.Outcome == ResourceWaitOutcome.Failure ? result.ErrorMessage : null);
    }

    private static WaitReport CreateSkippedReport(string name, IReadOnlyList<ResourceSnapshot> snapshots)
    {
        var matches = ResourceWaitService.FindResourceSnapshots(snapshots, name);
        var snapshot = matches.Count > 0 ? matches[0] : null;
        return new WaitReport(
            name,
            snapshot is null ? name : ResourceSnapshotMapper.GetResourceName(snapshot, snapshots),
            snapshot?.DisplayName,
            Outcome: null,
            Skipped: true,
            snapshot?.State ?? KnownResourceStates.NotStarted,
            snapshot?.HealthStatus,
            snapshot?.ExitCode,
            ResourceNotFound: false,
            snapshot?.Urls ?? [],
            Error: null);
    }

    private void DisplayReport(WaitReport report, string statusLabel)
    {
        var state = FormatState(report.State, report.ExitCode) ?? WaitCommandStrings.UnknownState;
        var (emoji, line) = report switch
        {
            { Skipped: true } => (KnownEmojis.Information, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineSkipped, report.DisplayLabel)),
            { Outcome: ResourceWaitOutcome.Success } => (KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineReady, report.DisplayLabel, statusLabel)),
            { Outcome: ResourceWaitOutcome.Completed } => (KnownEmojis.CheckMarkButton, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineCompleted, report.DisplayLabel)),
            { Outcome: ResourceWaitOutcome.Timeout } => (KnownEmojis.PoliceCarLight, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineTimedOut, report.DisplayLabel, statusLabel, state)),
            { Outcome: ResourceWaitOutcome.Cancelled } => (KnownEmojis.StopSign, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineCancelled, report.DisplayLabel, statusLabel, state)),
            { ResourceNotFound: true } => (KnownEmojis.CrossMark, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineNotFound, report.DisplayLabel)),
            _ => (KnownEmojis.CrossMark, string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineFailed, report.DisplayLabel, FormatState(report.State, report.ExitCode) ?? report.Error ?? WaitCommandStrings.UnknownState))
        };

        var endpoints = DescribeCommand.OrderUrls(report.Urls.Where(static u => !u.IsInternal))
            .Select(static u => u.Url)
            .ToArray();
        if (endpoints.Length > 0)
        {
            line = string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceLineWithEndpoints, line, string.Join(", ", endpoints));
        }

        // Write every resource line to stdout, including failures, so the complete list stays together
        // for scripts and agents. The summary error that follows goes to stderr.
        InteractionService.DisplayMessage(emoji, line);
    }

    private static WaitResourceJson ToJson(WaitReport report)
    {
        return new WaitResourceJson
        {
            Name = report.Name,
            DisplayName = report.DisplayName,
            Outcome = report switch
            {
                { Skipped: true } => "skipped",
                { Outcome: ResourceWaitOutcome.Success } => "success",
                { Outcome: ResourceWaitOutcome.Completed } => "completed",
                { Outcome: ResourceWaitOutcome.Timeout } => "timeout",
                { Outcome: ResourceWaitOutcome.Cancelled } => "cancelled",
                _ => "failure"
            },
            State = report.State,
            HealthStatus = report.Health,
            ExitCode = report.ExitCode,
            Urls = report.Urls
                .Select(static u => new ResourceUrlJson
                {
                    Name = u.Name,
                    DisplayName = u.DisplayProperties?.DisplayName,
                    Url = u.Url,
                    IsInternal = u.IsInternal
                })
                .ToArray(),
            Error = report switch
            {
                { ResourceNotFound: true } => string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceNotFound, report.DisplayLabel),
                _ => report.Error
            }
        };
    }

    private static bool IsWaitingForExplicitStart(ResourceSnapshot snapshot)
    {
        return string.Equals(snapshot.State, KnownResourceStates.NotStarted, StringComparisons.ResourceState);
    }

    private static string? FormatState(string? state, int? exitCode)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        return exitCode is int code
            ? string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.StateWithExitCode, state, code)
            : state;
    }

    private static bool IsValidStatus(string status)
    {
        return status is "healthy" or "up" or "down";
    }

    private static string GetStatusLabel(string status)
    {
        return status switch
        {
            "up" => "up (running)",
            "healthy" => "healthy",
            "down" => "down",
            _ => status
        };
    }

    private static ResourceWaitTarget GetWaitTarget(string status)
    {
        return status switch
        {
            "healthy" => ResourceWaitTarget.Healthy,
            "up" => ResourceWaitTarget.Up,
            "down" => ResourceWaitTarget.Down,
            _ => throw new ArgumentOutOfRangeException(nameof(status))
        };
    }

    private sealed record WaitReport(
        string Name,
        string DisplayLabel,
        string? DisplayName,
        ResourceWaitOutcome? Outcome,
        bool Skipped,
        string? State,
        string? Health,
        int? ExitCode,
        bool ResourceNotFound,
        ResourceSnapshotUrl[] Urls,
        string? Error);
}
