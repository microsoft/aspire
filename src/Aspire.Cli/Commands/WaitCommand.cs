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

    // Bounds the snapshot read that only adds state and endpoint details to an already known outcome.
    private static readonly TimeSpan s_finalSnapshotsTimeout = TimeSpan.FromSeconds(5);

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
                var batch = await _resourceWaitService.WaitUntilDoneAsync(
                    connection,
                    [resourceName],
                    GetWaitTarget(status),
                    timeoutSeconds,
                    cancellationToken).ConfigureAwait(false);
                return batch.Results[0];
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
        var remainingTimeoutSeconds = timeoutSeconds;
        if (waitForAll)
        {
            var selection = await InteractionService.ShowStatusAsync(
                string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForAllResources, statusLabel),
                () => SelectAllResourcesAsync(connection, timeoutSeconds, cancellationToken));

            if (selection is null)
            {
                return DisplayInventoryTimeout(status, timeoutSeconds, format);
            }

            (namesToWait, skippedNames) = selection.Value;

            // The inventory wait spends part of the shared --timeout budget.
            var inventoryElapsed = _timeProvider.GetElapsedTime(startTimestamp);
            remainingTimeoutSeconds = Math.Max(1, (int)Math.Ceiling(timeoutSeconds - inventoryElapsed.TotalSeconds));
        }
        else
        {
            namesToWait = requestedNames;
        }

        _logger.LogDebug("Waiting for {Count} resources to reach status '{Status}' with timeout {Timeout}s", namesToWait.Count, status, timeoutSeconds);

        var batch = namesToWait.Count == 0
            ? new ResourceWaitBatchResult([], [])
            : await InteractionService.ShowStatusAsync(
                waitForAll
                    ? string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForAllResources, statusLabel)
                    : string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.WaitingForResources, namesToWait.Count, statusLabel),
                () => _resourceWaitService.WaitUntilDoneAsync(
                    connection,
                    namesToWait,
                    GetWaitTarget(status),
                    remainingTimeoutSeconds,
                    cancellationToken));
        var results = batch.Results;

        var elapsed = _timeProvider.GetElapsedTime(startTimestamp);

        // Show each resource's current state and endpoints without a follow-up `aspire describe`.
        var finalSnapshots = await GetFinalSnapshotsAsync(
            connection,
            batch.Snapshots,
            results.Select(static r => r.ResourceName).Concat(skippedNames),
            cancellationToken).ConfigureAwait(false);
        var reports = results
            .Select(r => CreateReport(r, finalSnapshots, GetWaitTarget(status), waitForAll))
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

    private async Task<IReadOnlyList<ResourceSnapshot>> GetFinalSnapshotsAsync(
        IAppHostAuxiliaryBackchannel connection,
        IReadOnlyList<ResourceSnapshot> watchedSnapshots,
        IEnumerable<string> resourceNames,
        CancellationToken cancellationToken)
    {
        // The batch watched the snapshots while waiting. A batch that ends quickly can stop watching
        // before the AppHost replayed every resource, so read them only when a resource is missing.
        if (resourceNames.All(name => ResourceWaitService.FindResourceSnapshots(watchedSnapshots, name).Count > 0))
        {
            return watchedSnapshots;
        }

        // The outcome is already known and the snapshots only add details, so do not let a slow
        // AppHost delay reporting it.
        using var timeoutCts = new CancellationTokenSource(s_finalSnapshotsTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            return await connection.GetResourceSnapshotsAsync(includeHidden: true, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The wait results are still meaningful without the extra state and endpoint details.
            _logger.LogDebug(ex, "Unable to read resource snapshots after waiting.");
            return watchedSnapshots;
        }
    }

    private static WaitReport CreateReport(ResourceWaitResult result, IReadOnlyList<ResourceSnapshot> snapshots, ResourceWaitTarget target, bool waitForAll)
    {
        var matches = ResourceWaitService.FindResourceSnapshots(snapshots, result.ResourceName);
        var snapshot = matches.Count == 1 ? matches[0] : null;

        // --all waits on runtime names, which can carry a replica suffix. Show the friendly name like
        // `aspire describe` does; explicitly requested names are echoed back as typed.
        var displayName = waitForAll && snapshot is not null
            ? ResourceSnapshotMapper.GetResourceName(snapshot, snapshots)
            : result.ResourceName;

        // A successful down wait ends on the terminal state the AppHost observed. The snapshots can still
        // hold an earlier Running update when that response arrives before the watch catches up, so the
        // response is the more recent view of the resource.
        var endedOnTerminalState = target is ResourceWaitTarget.Down &&
            result.Outcome is ResourceWaitOutcome.Success &&
            result.State is not null;

        // The wait result reflects the state that ended the wait. Use only it for failures, completions and
        // terminal states: a failure without a state (an ambiguous replica name, an AppHost without wait
        // support) must show its error message rather than the resource's unrelated current state. Otherwise
        // prefer the fresh snapshot, which can include later changes such as health updates.
        var useResultState = result.Outcome is ResourceWaitOutcome.Failure or ResourceWaitOutcome.Completed ||
            endedOnTerminalState ||
            snapshot?.State is null;

        // Report the state and exit code from the same source so they describe the same moment. Borrow the
        // other source's exit code only when it saw the same state, for example an older AppHost that does
        // not return the exit code with a down wait.
        var sameState = snapshot is not null &&
            result.State is not null &&
            string.Equals(snapshot.State, result.State, StringComparisons.ResourceState);
        var exitCode = useResultState
            ? result.ExitCode ?? (sameState ? snapshot!.ExitCode : null)
            : snapshot!.ExitCode ?? (sameState ? result.ExitCode : null);

        return new WaitReport(
            snapshot?.Name ?? result.ResourceName,
            displayName,
            snapshot?.DisplayName,
            result.Outcome,
            Skipped: false,
            useResultState ? result.State : snapshot!.State,
            endedOnTerminalState ? result.Health : snapshot?.HealthStatus ?? result.Health,
            exitCode,
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

    private async Task<(IReadOnlyList<string> NamesToWait, IReadOnlyList<string> SkippedNames)?> SelectAllResourcesAsync(
        IAppHostAuxiliaryBackchannel connection,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        bool reportsExplicitStart;
        IReadOnlyList<ResourceSnapshot> snapshots;

        // The listing is part of the inventory step: building snapshots can take several seconds per
        // MCP endpoint on the AppHost, so it shares the --timeout deadline with the inventory wait.
        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds), _timeProvider))
        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
        {
            try
            {
                reportsExplicitStart = await WaitForResourceInventoryAsync(connection, linkedCts.Token).ConfigureAwait(false);
                snapshots = await connection.GetResourceSnapshotsAsync(includeHidden: true, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }

        var visible = snapshots.Where(static snapshot => !ResourceSnapshotMapper.IsHiddenResource(snapshot)).ToArray();

        var skippedNames = visible
            .Where(snapshot => IsWaitingForExplicitStart(snapshot, reportsExplicitStart))
            .Select(static snapshot => snapshot.Name)
            .ToArray();
        var namesToWait = visible
            .Where(snapshot => !IsWaitingForExplicitStart(snapshot, reportsExplicitStart))
            .Select(static snapshot => snapshot.Name)
            .ToArray();
        return (namesToWait, skippedNames);
    }

    /// <summary>
    /// Waits until a resource listing contains every resource, as far as the AppHost can tell.
    /// </summary>
    /// <returns><see langword="true"/> when the AppHost confirmed its inventory and reports explicit-start metadata.</returns>
    private static async Task<bool> WaitForResourceInventoryAsync(IAppHostAuxiliaryBackchannel connection, CancellationToken cancellationToken)
    {
        // The AppHost lists only resources that already published a state, so a listing taken while it is
        // still starting can be partial, and --all would succeed without waiting for the missing resources.
        if (await connection.WaitForResourcesInitializedAsync(cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        // Older AppHosts cannot confirm their inventory. Their startup readiness point is the closest signal:
        // for an AppHost launched by the CLI it follows the dashboard becoming healthy, which happens after every
        // resource published its initial state. Other AppHosts report ready immediately, so their listing can
        // still be partial while they start.
        await connection.WaitForAppHostReadyAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    private int DisplayInventoryTimeout(string status, int timeoutSeconds, OutputFormat format)
    {
        if (format == OutputFormat.Json)
        {
            var output = new WaitOutput
            {
                Outcome = "timeout",
                Status = status,
                Resources = []
            };
            InteractionService.DisplayRawText(
                JsonSerializer.Serialize(output, WaitCommandJsonContext.RelaxedEscaping.WaitOutput),
                ConsoleOutput.Standard);
            return CliExitCodes.WaitTimeout;
        }

        // Reset cursor position after spinner
        InteractionService.DisplayPlainText("");
        InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, WaitCommandStrings.ResourceInventoryTimedOut, timeoutSeconds));
        return CliExitCodes.WaitTimeout;
    }

    private static bool IsWaitingForExplicitStart(ResourceSnapshot snapshot, bool reportsExplicitStart)
    {
        if (reportsExplicitStart)
        {
            // An explicit-start resource stays idle until someone starts it, so waiting on it would only time
            // out. Before DCP creates it, its state can still be empty. A run that someone started and that has
            // since stopped, such as a closed tracked browser, leaves it idle in the same way: it runs again only
            // when started, and its terminal state may never carry an exit code. --all waits for what the AppHost
            // keeps running, so only an explicit-start resource that is currently starting or running is waited on.
            return snapshot.IsExplicitStart
                && (string.IsNullOrEmpty(snapshot.State) ||
                    string.Equals(snapshot.State, KnownResourceStates.NotStarted, StringComparisons.ResourceState) ||
                    KnownResourceStates.TerminalStates.Contains(snapshot.State, StringComparers.ResourceState));
        }

        // Older AppHosts do not report explicit-start metadata. NotStarted is the closest signal they offer:
        // explicit-start resources stay in it, while auto-started resources normally move on. A resource
        // sampled before its first transition, such as a connection string that is still initializing, is
        // skipped too; skipping is safer than waiting until the timeout for every explicit-start resource.
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
