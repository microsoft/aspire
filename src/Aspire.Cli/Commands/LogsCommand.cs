// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
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

    private readonly ResourceColorMap _resourceColorMap;

    public LogsCommand(
        IInteractionService interactionService,
        IAuxiliaryBackchannelMonitor backchannelMonitor,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        AspireCliTelemetry telemetry,
        ResourceColorMap resourceColorMap,
        ILogger<LogsCommand> logger)
        : base("logs", LogsCommandStrings.Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _resourceColorMap = resourceColorMap;
        _interactionService = interactionService;
        _logger = logger;
        _connectionResolver = new AppHostConnectionResolver(backchannelMonitor, interactionService, executionContext, logger);

        Arguments.Add(s_resourceArgument);
        Options.Add(s_appHostOption);
        Options.Add(s_followOption);
        Options.Add(s_formatOption);
        Options.Add(s_tailOption);
        Options.Add(s_timestampsOption);
        Options.Add(s_includeHiddenOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        using var activity = Telemetry.StartDiagnosticActivity(Name);

        var resourceName = parseResult.GetValue(s_resourceArgument);
        var passedAppHostProjectFile = parseResult.GetValue(s_appHostOption);
        var follow = parseResult.GetValue(s_followOption);
        var format = parseResult.GetValue(s_formatOption);
        var tail = parseResult.GetValue(s_tailOption);
        var timestamps = parseResult.GetValue(s_timestampsOption);
        var includeHidden = parseResult.GetValue(s_includeHiddenOption);

        // Validate --tail value
        if (tail.HasValue && tail.Value < 1)
        {
            _interactionService.DisplayError(LogsCommandStrings.TailMustBePositive);
            return ExitCodeConstants.InvalidCommand;
        }

        var result = await _connectionResolver.ResolveConnectionAsync(
            passedAppHostProjectFile,
            SharedCommandStrings.ScanningForRunningAppHosts,
            string.Format(CultureInfo.CurrentCulture, SharedCommandStrings.SelectAppHost, LogsCommandStrings.SelectAppHostAction),
            SharedCommandStrings.AppHostNotRunning,
            cancellationToken);

        if (!result.Success)
        {
            // No running AppHosts is not an error - similar to Unix 'ps' returning empty
            _interactionService.DisplayMessage(KnownEmojis.Information, result.ErrorMessage);
            return ExitCodeConstants.Success;
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
                _interactionService.DisplayError(string.Format(CultureInfo.CurrentCulture, LogsCommandStrings.ResourceNotFound, resourceName));
                return ExitCodeConstants.InvalidCommand;
            }
        }
        else
        {
            if (!resourceWatcher.GetResources().Any())
            {
                _interactionService.DisplayMessage(KnownEmojis.Information, LogsCommandStrings.NoResourcesFound);
                return ExitCodeConstants.Success;
            }
        }

        if (follow)
        {
            return await ExecuteWatchAsync(connection, resourceWatcher, resourceName, format, tail, timestamps, cancellationToken);
        }
        else
        {
            return await ExecuteGetAsync(connection, resourceWatcher, resourceName, format, tail, timestamps, cancellationToken);
        }
    }

    private async Task<int> ExecuteGetAsync(
        IAppHostAuxiliaryBackchannel connection,
        ResourceSnapshotWatcher resourceWatcher,
        string? resourceName,
        OutputFormat format,
        int? tail,
        bool timestamps,
        CancellationToken cancellationToken)
    {
        // Collect all logs, parsing into LogEntry with resolved resource names sorted by timestamp
        var entries = await _interactionService.ShowStatusAsync(
            LogsCommandStrings.GettingLogs,
            async () => await CollectLogsAsync(connection, resourceWatcher, resourceName, cancellationToken).ConfigureAwait(false));

        // Apply tail filter (tail.Value is guaranteed >= 1 by earlier validation)
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
            foreach (var entry in entries)
            {
                OutputLogLine(entry, format, timestamps);
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
        CancellationToken cancellationToken)
    {
        var logParser = new LogParser(ConsoleColor.Black);

        // If tail is specified, show last N lines first before streaming
        if (tail.HasValue)
        {
            var entries = await _interactionService.ShowStatusAsync(
                LogsCommandStrings.GettingLogs,
                async () => await CollectLogsAsync(connection, resourceWatcher, resourceName, cancellationToken).ConfigureAwait(false));

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
        await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: true, cancellationToken).ConfigureAwait(false))
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
        CancellationToken cancellationToken)
    {
        var logParser = new LogParser(ConsoleColor.Black);
        var logEntries = new LogEntries(int.MaxValue) { BaseLineNumber = 1 };
        // Snapshot the resource list once for the non-follow path since it doesn't change.
        var allSnapshots = resourceWatcher.GetAllResources().ToList();
        await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: false, cancellationToken).ConfigureAwait(false))
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
            var escapedContent = content.EscapeMarkup();
            _interactionService.DisplayMarkupLine($"{timestampPrefix.EscapeMarkup()}[{color}][[{displayName.EscapeMarkup()}]][/] {escapedContent}");
        }
    }

    private static string FormatTimestamp(DateTime timestamp)
    {
        return timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffK", CultureInfo.InvariantCulture);
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
