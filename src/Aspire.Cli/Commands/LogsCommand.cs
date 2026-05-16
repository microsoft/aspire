// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Commands;

/// <summary>
/// JSON output format for a log line.
/// </summary>
internal sealed class LogLineJson
{
    public required string ResourceName { get; init; }
    public string? Timestamp { get; init; }
    public required string Content { get; init; }
    public required bool IsError { get; init; }
}

/// <summary>
/// Wrapper for logs snapshot output.
/// </summary>
internal sealed class LogsOutput
{
    public required LogLineJson[] Logs { get; init; }
}

[JsonSerializable(typeof(LogLineJson))]
[JsonSerializable(typeof(LogsOutput))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class LogsCommandJsonContext : JsonSerializerContext
{
    // Compact NDJSON for streaming (--follow)
    private static LogsCommandJsonContext? s_ndjson;

    public static LogsCommandJsonContext Ndjson => s_ndjson ??= new LogsCommandJsonContext(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        });

    // Pretty-printed for snapshots
    private static LogsCommandJsonContext? s_snapshot;

    public static LogsCommandJsonContext Snapshot => s_snapshot ??= new LogsCommandJsonContext(
        new JsonSerializerOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true
        });
}

internal sealed class LogsCommand : BaseCommand
{
    internal override HelpGroup HelpGroup => HelpGroup.Monitoring;

    private readonly IInteractionService _interactionService;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly AppHostConnectionResolver _connectionResolver;
    private readonly ILogger<LogsCommand> _logger;

    private static readonly Argument<string?> s_resourceArgument = new("resource")
    {
        Description = LogsCommandStrings.ResourceArgumentDescription,
        Arity = ArgumentArity.ZeroOrOne
    };
    private static readonly OptionWithLegacy<FileInfo?> s_appHostOption = new("--apphost", "--project", SharedCommandStrings.AppHostOptionDescription);
    private static readonly Option<bool> s_followOption = new("--follow", "-f")
    {
        Description = LogsCommandStrings.FollowOptionDescription
    };
    private static readonly Option<OutputFormat> s_formatOption = new("--format")
    {
        Description = LogsCommandStrings.JsonOptionDescription
    };
    private static readonly Option<int?> s_tailOption = new("--tail", "-n")
    {
        Description = LogsCommandStrings.TailOptionDescription
    };
    private static readonly Option<bool> s_timestampsOption = new("--timestamps", "-t")
    {
        Description = LogsCommandStrings.TimestampsOptionDescription
    };
    private static readonly Option<bool> s_includeHiddenOption = new("--include-hidden")
    {
        Description = LogsCommandStrings.IncludeHiddenOptionDescription
    };
    private static readonly Option<string?> s_searchOption = new("--search")
    {
        Description = LogsCommandStrings.SearchOptionDescription
    };

    private readonly ResourceColorMap _resourceColorMap;

    public LogsCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IProjectLocator projectLocator,
        AspireCliTelemetry telemetry,
        ICliHostEnvironment hostEnvironment,
        ResourceColorMap resourceColorMap,
        ILogger<LogsCommand> logger,
        ProfilingTelemetry profilingTelemetry)
        : base("logs", LogsCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _resourceColorMap = resourceColorMap;
        _interactionService = interactionService;
        _hostEnvironment = hostEnvironment;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, projectLocator, executionContext, logger, profilingTelemetry);

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_followOption);
        Options.Add(s_formatOption);
        Options.Add(s_tailOption);
        Options.Add(s_timestampsOption);
        Options.Add(s_includeHiddenOption);
        Options.Add(s_searchOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var follow = parseResult.GetValue(s_followOption);
        var format = parseResult.GetValue(s_formatOption);
        var tail = parseResult.GetValue(s_tailOption);
        var timestamps = parseResult.GetValue(s_timestampsOption);
        var includeHidden = parseResult.GetValue(s_includeHiddenOption);
        var search = parseResult.GetValue(s_searchOption);

        // Validate --tail value
        if (tail.HasValue && tail.Value < 1)
        {
            return CommandResult.Failure(ExitCodeConstants.InvalidCommand, LogsCommandStrings.TailMustBePositive);
        }

        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, LogsCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            return CommandResult.FromExitCode(AppHostConnectionResultHandler.DisplayFailureAsInformation(result, _interactionService));
        }

        var connection = result.Connection!;
        var effectiveIncludeHidden = includeHidden || resourceName is not null;
        using var resourceWatcher = new ResourceSnapshotWatcher(connection, effectiveIncludeHidden);
        await resourceWatcher.WaitForInitialLoadAsync(cancellationToken).ConfigureAwait(false);

        // Pre-resolve colors for all resource names so that assignment is
        // deterministic regardless of which resources are displayed.
        var allSnapshots = resourceWatcher.GetAllResources();
        _resourceColorMap.ResolveAll(allSnapshots.Select(s => ResourceSnapshotMapper.GetResourceName(s, allSnapshots)));

        // Validate resource name exists (match by Name or DisplayName since users may pass either)
        if (resourceName is not null)
        {
            if (!ResourceSnapshotMapper.WhereMatchesResourceName(resourceWatcher.GetAllResources(), resourceName).Any())
            {
                return CommandResult.Failure(ExitCodeConstants.InvalidCommand, string.Format(CultureInfo.CurrentCulture, LogsCommandStrings.ResourceNotFound, resourceName));
            }
        }
        else
        {
            if (!resourceWatcher.GetResources().Any())
            {
                _interactionService.DisplayMessage(KnownEmojis.Information, LogsCommandStrings.NoResourcesFound);
                return CommandResult.Success();
            }
        }

        if (follow)
        {
            try
            {
                return CommandResult.FromExitCode(await ExecuteWatchAsync(connection, resourceWatcher, resourceName, format, tail, timestamps, search, cancellationToken));
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken || cancellationToken.IsCancellationRequested)
            {
                return CommandResult.Success();
            }
            catch (Exception ex) when (AppHostFollowDisconnectHelpers.IsExpectedDisconnect(ex))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return CommandResult.Success();
                }

                // Stopping or restarting the AppHost can tear down the JSON-RPC stream while
                // logs --follow is active. Treat the lost stream as a normal end of stream
                // rather than surfacing it as an unexpected CLI failure. Emit the status
                // message on stderr so JSON output on stdout remains parseable.
                AppHostFollowDisconnectHelpers.WriteStatusMessage(_interactionService, connection);

                return CommandResult.Success();
            }
        }
        else
        {
            return CommandResult.FromExitCode(await ExecuteGetAsync(connection, resourceWatcher, resourceName, format, tail, timestamps, search, cancellationToken));
        }
    }

    private async Task<int> ExecuteGetAsync(
        IAppHostAuxiliaryBackchannel connection,
        ResourceSnapshotWatcher resourceWatcher,
        string? resourceName,
        OutputFormat format,
        int? tail,
        bool timestamps,
        string? search,
        CancellationToken cancellationToken)
    {
        // Collect all logs, parsing into LogEntry with resolved resource names sorted by timestamp
        var entries = await _interactionService.ShowStatusAsync(
            LogsCommandStrings.GettingLogs,
            async () => await CollectLogsAsync(connection, resourceWatcher, resourceName, tail, search, cancellationToken).ConfigureAwait(false));

        // Keep the client-side search and tail passes even when a v2 AppHost already applied
        // them. Older AppHosts fall back to the legacy log stream, and this also preserves the
        // CLI's parsed-log search semantics for any edge cases the server-side pre-filter misses.
        if (!string.IsNullOrEmpty(search))
        {
            entries = entries.Where(e => MatchesSearch(e, search)).ToList();
        }

        if (tail.HasValue && entries.Count > tail.Value)
        {
            entries = entries.Skip(entries.Count - tail.Value).ToList();
        }

        // Output the logs
        if (format == OutputFormat.Json)
        {
            // Wrapped JSON for snapshot - single JSON object compatible with jq
            var logsOutput = new LogsOutput
            {
                Logs = entries.Select(entry => new LogLineJson
                {
                    ResourceName = entry.ResourcePrefix ?? string.Empty,
                    Timestamp = timestamps && entry.Timestamp.HasValue ? FormatTimestamp(entry.Timestamp.Value) : null,
                    Content = entry.Content ?? entry.RawContent ?? string.Empty,
                    IsError = entry.Type == LogEntryType.Error
                }).ToArray()
            };
            var json = JsonSerializer.Serialize(logsOutput, LogsCommandJsonContext.Snapshot.LogsOutput);
            // Structured output always goes to stdout.
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
        }
        else
        {
            if (entries.Count == 0)
            {
                _interactionService.DisplayMessage(KnownEmojis.Information, LogsCommandStrings.NoLogsFound);
            }
            else
            {
                foreach (var entry in entries)
                {
                    OutputLogLine(entry, format, timestamps);
                }
            }
        }

        return ExitCodeConstants.Success;
    }

    private async Task<int> ExecuteWatchAsync(
        IAppHostAuxiliaryBackchannel connection,
        ResourceSnapshotWatcher resourceWatcher,
        string? resourceName,
        OutputFormat format,
        int? tail,
        bool timestamps,
        string? search,
        CancellationToken cancellationToken)
    {
        var logParser = new LogParser(ConsoleColor.Black);

        // If tail is specified, show last N lines first before streaming
        if (tail.HasValue)
        {
            var entries = await _interactionService.ShowStatusAsync(
                LogsCommandStrings.GettingLogs,
                async () => await CollectLogsAsync(connection, resourceWatcher, resourceName, tail, search, cancellationToken).ConfigureAwait(false));

            // Apply full-text search filter before tail so tail count reflects matching entries
            if (!string.IsNullOrEmpty(search))
            {
                entries = entries.Where(e => MatchesSearch(e, search)).ToList();
            }

            // Output last N lines
            var tailedEntries = entries.Count > tail.Value
                ? entries.Skip(entries.Count - tail.Value)
                : entries;

            foreach (var entry in tailedEntries)
            {
                OutputLogLine(entry, format, timestamps);
            }
        }

        // Now stream new logs
        var followRequest = new GetConsoleLogsRequest
        {
            ResourceName = resourceName,
            Follow = true,
            Search = search,
            IncludeHidden = resourceName is not null || resourceWatcher.IncludeHidden
        };

        await foreach (var logLine in GetConsoleLogLinesAsync(connection, followRequest, cancellationToken).ConfigureAwait(false))
        {
            // When streaming all resources, skip logs from hidden resources.
            // We filter by exclusion so that new resources appearing after the
            // initial snapshot are included by default.
            if (resourceName is null && !resourceWatcher.IncludeHidden)
            {
                var resource = resourceWatcher.GetResource(logLine.ResourceName);
                if (resource is not null && ResourceSnapshotMapper.IsHiddenResource(resource))
                {
                    continue;
                }
            }

            var entry = ParseLogLine(logLine, logParser, resourceWatcher.GetAllResources());

            // Apply full-text search filter on streamed log content
            if (!string.IsNullOrEmpty(search) && !MatchesSearch(entry, search))
            {
                continue;
            }

            OutputLogLine(entry, format, timestamps);
        }

        return ExitCodeConstants.Success;
    }

    /// <summary>
    /// Collects all logs for a resource (or all resources if resourceName is null), parsing each
    /// into a <see cref="LogEntry"/> with the resolved resource name set on <see cref="LogEntry.ResourcePrefix"/>
    /// and returning entries sorted by timestamp.
    /// </summary>
    private static async Task<IList<LogEntry>> CollectLogsAsync(
        IAppHostAuxiliaryBackchannel connection,
        ResourceSnapshotWatcher resourceWatcher,
        string? resourceName,
        int? tail,
        string? search,
        CancellationToken cancellationToken)
    {
        var logParser = new LogParser(ConsoleColor.Black);
        var logEntries = new LogEntries(int.MaxValue) { BaseLineNumber = 1 };
        // Snapshot the resource list once for the non-follow path since it doesn't change.
        var allSnapshots = resourceWatcher.GetAllResources().ToList();
        // For named resources, V2 AppHosts use Search/Tail to avoid sending non-matching
        // logs over JSON-RPC. The client still applies the same filters after parsing for
        // all-resource compatibility and to keep final output semantics centralized here.
        var request = new GetConsoleLogsRequest
        {
            ResourceName = resourceName,
            Follow = false,
            Search = search,
            Tail = tail,
            IncludeHidden = resourceName is not null || resourceWatcher.IncludeHidden
        };

        await foreach (var logLine in GetConsoleLogLinesAsync(connection, request, cancellationToken).ConfigureAwait(false))
        {
            // When streaming all resources, skip logs from hidden resources
            if (resourceName is null && !resourceWatcher.IncludeHidden)
            {
                var resource = resourceWatcher.GetResource(logLine.ResourceName);
                if (resource is not null && ResourceSnapshotMapper.IsHiddenResource(resource))
                {
                    continue;
                }
            }

            logEntries.InsertSorted(ParseLogLine(logLine, logParser, allSnapshots));
        }
        return logEntries.GetEntries();
    }

    private static async IAsyncEnumerable<ResourceLogLine> GetConsoleLogLinesAsync(
        IAppHostAuxiliaryBackchannel connection,
        GetConsoleLogsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The batch RPC is capability-gated by the connection. Older AppHosts fall back through
        // the line-streaming/legacy RPC paths, while newer AppHosts can reduce JSON-RPC overhead
        // by sending many log lines per stream item.
        await foreach (var batch in connection.GetConsoleLogBatchesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            foreach (var logLine in batch.Lines)
            {
                yield return logLine;
            }
        }
    }

    /// <summary>
    /// Parses a <see cref="ResourceLogLine"/> into a <see cref="LogEntry"/> with the resolved resource name
    /// set on <see cref="LogEntry.ResourcePrefix"/>.
    /// </summary>
    private static LogEntry ParseLogLine(ResourceLogLine logLine, LogParser logParser, IEnumerable<ResourceSnapshot> snapshots)
    {
        var resolvedName = ResolveResourceName(logLine.ResourceName, snapshots);
        return logParser.CreateLogEntry(logLine.Content, logLine.IsError, resolvedName);
    }

    private void OutputLogLine(LogEntry entry, OutputFormat format, bool timestamps)
    {
        var displayName = entry.ResourcePrefix ?? string.Empty;
        var content = entry.Content ?? entry.RawContent ?? string.Empty;
        var displayContent = _hostEnvironment.SupportsAnsi ? content : AnsiParser.StripControlSequences(content);
        var timestampPrefix = timestamps && entry.Timestamp.HasValue ? FormatTimestamp(entry.Timestamp.Value) + " " : string.Empty;

        if (format == OutputFormat.Json)
        {
            // NDJSON for streaming - compact, one object per line
            var logLineJson = new LogLineJson
            {
                ResourceName = displayName,
                Timestamp = timestamps && entry.Timestamp.HasValue ? FormatTimestamp(entry.Timestamp.Value) : null,
                Content = content,
                IsError = entry.Type == LogEntryType.Error
            };
            var output = JsonSerializer.Serialize(logLineJson, LogsCommandJsonContext.Ndjson.LogLineJson);
            // Structured output always goes to stdout.
            _interactionService.DisplayRawText(output, ConsoleOutput.Standard);
        }
        else
        {
            // Colorized output: assign a consistent color to each resource
            var color = _resourceColorMap.GetColor(displayName);
            var escapedContent = displayContent.EscapeMarkup();
            var dimTimestamp = timestampPrefix.Length > 0 ? $"[dim]{timestampPrefix.EscapeMarkup()}[/]" : string.Empty;
            _interactionService.DisplayMarkupLine($"{dimTimestamp}[{color}][[{displayName.EscapeMarkup()}]][/] {escapedContent}");
        }
    }

    private static string FormatTimestamp(DateTime timestamp)
    {
        return timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture);
    }

    private static bool MatchesSearch(LogEntry entry, string search)
    {
        var content = entry.RawContent ?? entry.Content ?? string.Empty;
        var prefix = entry.ResourcePrefix ?? string.Empty;
        return content.Contains(search, StringComparisons.FullTextSearch) ||
               prefix.Contains(search, StringComparisons.FullTextSearch) ||
               AnsiParser.StripControlSequences(content).Contains(search, StringComparisons.FullTextSearch);
    }

    private static string ResolveResourceName(string resourceName, IEnumerable<ResourceSnapshot> snapshots)
    {
        var snapshot = snapshots.FirstOrDefault(s => string.Equals(s.Name, resourceName, StringComparisons.ResourceName));
        if (snapshot is not null)
        {
            return ResourceSnapshotMapper.GetResourceName(snapshot, snapshots);
        }
        return resourceName;
    }
}
