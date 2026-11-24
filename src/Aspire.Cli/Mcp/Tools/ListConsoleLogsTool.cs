// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for listing console logs for a resource.
/// Gets log data directly from the AppHost backchannel instead of forwarding to the dashboard.
/// </summary>
internal sealed class ListConsoleLogsTool(IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor, ILogger<ListConsoleLogsTool> logger) : CliMcpTool
{
    internal const int DefaultLimit = 100;
    internal const string TailPosition = "tail";
    internal const string HeadPosition = "head";

    private static readonly string[] s_positions = [TailPosition, HeadPosition];

    private static readonly JsonElement s_inputSchema = JsonDocument.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "resourceName": {
              "type": "string",
              "description": "The resource name."
            },
            "search": {
              "type": "string",
              "description": "Full-text search to filter log content."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": {{SharedAIHelpers.ConsoleLogsLimit}},
              "default": {{DefaultLimit}},
              "description": "Maximum number of log lines to return. Responses are also capped by size."
            },
            "from": {
              "type": "string",
              "enum": ["{{TailPosition}}", "{{HeadPosition}}"],
              "default": "{{TailPosition}}",
              "description": "'tail' returns the latest log lines. 'head' returns the first log lines, which usually explain why a resource failed to start."
            }
          },
          "required": ["resourceName"]
        }
        """).RootElement;

    public override string Name => KnownMcpTools.ListConsoleLogs;

    public override string Description => "List console logs for a resource. The console logs includes standard output from resources and resource commands. Known resource commands are 'start', 'stop' and 'restart' which are used to start and stop resources. Returns the latest log lines by default; use from, limit and search to choose which lines are returned. Don't print the full console logs in the response to the user. Console logs should be examined when determining why a resource isn't running.";

    public override JsonElement GetInputSchema()
    {
        return s_inputSchema;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;

        var resourceName = McpToolHelpers.GetOptionalString(arguments, "resourceName");
        if (string.IsNullOrEmpty(resourceName))
        {
            throw new McpProtocolException("The resourceName parameter is required.", McpErrorCode.InvalidParams);
        }

        var search = McpToolHelpers.GetOptionalString(arguments, "search");
        var limit = McpToolHelpers.GetOptionalInt32(arguments, "limit", minimum: 1, maximum: SharedAIHelpers.ConsoleLogsLimit, defaultValue: DefaultLimit);
        var from = McpToolHelpers.GetOptionalChoice(arguments, "from", s_positions, defaultValue: TailPosition);

        var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(auxiliaryBackchannelMonitor, logger, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            logger.LogWarning("No Aspire AppHost is currently running");
            throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
        }

        // Check if the resource is excluded from MCP before fetching logs.
        // This is the only check needed because the resource name is required for this tool.
        var excludedResult = await McpToolHelpers.CheckResourceExcludedAsync(connection, resourceName, cancellationToken).ConfigureAwait(false);
        if (excludedResult is not null)
        {
            return excludedResult;
        }

        try
        {
            var logParser = new LogParser(ConsoleColor.Black);
            var fragments = SearchTextParser.ParseFragments(search);

            List<string> trimmedItems;
            string limitMessage;
            if (from == HeadPosition)
            {
                // Keep the earliest matching lines, but read the whole stream so the summary can report how many
                // later lines were not returned. Replicas of a resource stream their histories concurrently, so
                // arrival order isn't log order: a slower replica's startup failure can arrive after a faster
                // replica's later lines.
                //
                // Each line gets a sort key before the search filter is applied. A timestamped line uses its own
                // timestamp. An untimestamped line (for example a stack trace continuation) uses the latest
                // timestamp seen so far from the same replica, so it stays next to the line it continues. Lines
                // a replica writes before its first timestamped line are treated as its first output and sort
                // before everything else (DateTime.MinValue). Ties keep arrival order. Anchoring has to happen
                // before filtering because a continuation line can match the search while the timestamped line it
                // continues doesn't; filtering first would leave the match without a position in the log.
                //
                // Only the limit smallest keys are kept, in a max-heap, so memory is bounded by limit rather than
                // by the size of the log.
                var headEntries = new PriorityQueue<LogEntry, HeadSortKey>(limit + 1, HeadSortKeyDescendingComparer.Instance);
                var lastTimestampByReplica = new Dictionary<string, DateTime>(StringComparer.Ordinal);
                var totalLogsCount = 0;
                var arrivalIndex = 0L;
                await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: false, cancellationToken).ConfigureAwait(false))
                {
                    var entry = logParser.CreateLogEntry(logLine.Content, logLine.IsError, resourceName);

                    DateTime timestamp;
                    if (entry.Timestamp is { } entryTimestamp)
                    {
                        timestamp = entryTimestamp;
                        lastTimestampByReplica[logLine.ResourceName] = entryTimestamp;
                    }
                    else if (!lastTimestampByReplica.TryGetValue(logLine.ResourceName, out timestamp))
                    {
                        timestamp = DateTime.MinValue;
                    }

                    var key = new HeadSortKey(timestamp, arrivalIndex++);

                    if (!Matches(entry, fragments))
                    {
                        continue;
                    }

                    totalLogsCount++;
                    if (headEntries.Count < limit)
                    {
                        headEntries.Enqueue(entry, key);
                    }
                    else if (headEntries.TryPeek(out _, out var latestKey) && key.CompareTo(latestKey) < 0)
                    {
                        headEntries.DequeueEnqueue(entry, key);
                    }
                }

                var orderedHeadEntries = headEntries.UnorderedItems
                    .OrderBy(item => item.Priority)
                    .Select(item => item.Element)
                    .ToList();

                (trimmedItems, limitMessage) = SharedAIHelpers.GetLimitFromStartWithSummary(
                    orderedHeadEntries,
                    totalLogsCount,
                    limit,
                    SharedAIHelpers.CompactListTokenLength,
                    "console log",
                    "console logs",
                    SharedAIHelpers.SerializeLogEntry,
                    EstimateLineTokenCount);
            }
            else
            {
                var logEntries = new LogEntries(maximumEntryCount: SharedAIHelpers.ConsoleLogsLimit) { BaseLineNumber = 1 };

                // Collect logs from the backchannel
                await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: false, cancellationToken).ConfigureAwait(false))
                {
                    logEntries.InsertSorted(logParser.CreateLogEntry(logLine.Content, logLine.IsError, resourceName));
                }

                var entries = logEntries.GetEntries().ToList();
                if (fragments.Length > 0)
                {
                    entries = entries.Where(e => Matches(e, fragments)).ToList();
                }

                // When search is applied, total reflects matching entries. Otherwise, use the
                // last line number which represents the total lines collected by the LogEntries buffer.
                var totalLogsCount = string.IsNullOrEmpty(search)
                    ? (entries.Count == 0 ? 0 : entries.Last().LineNumber)
                    : entries.Count;

                (trimmedItems, limitMessage) = SharedAIHelpers.GetLimitFromEndWithSummary(
                    entries,
                    totalLogsCount,
                    limit,
                    SharedAIHelpers.CompactListTokenLength,
                    "console log",
                    "console logs",
                    SharedAIHelpers.SerializeLogEntry,
                    EstimateLineTokenCount);
            }

            var consoleLogsText = SharedAIHelpers.SerializeConsoleLogs(trimmedItems);

            var consoleLogsData = $"""
                {limitMessage}

                # CONSOLE LOGS

                ```plaintext
                {consoleLogsText.Trim()}
                ```
                """;

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = consoleLogsData }]
            };
        }
        catch (Exception ex) when (ex is not McpProtocolException and not OperationCanceledException)
        {
            logger.LogError("Error retrieving console logs for resource '{ResourceName}'", resourceName);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Error retrieving console logs for resource '{resourceName}'." }],
                IsError = true
            };
        }
    }

    // SharedAIHelpers.EstimateTokenCount rounds down and doesn't see the line terminator that SerializeConsoleLogs
    // appends, which loses up to four bytes per line. With up to ConsoleLogsLimit short lines that adds up: 2,000
    // eleven-byte lines are estimated at 4,000 tokens but take about 24 KB. Counting the terminator and rounding up
    // keeps the returned lines within four bytes per budgeted token.
    private static int EstimateLineTokenCount(string line)
    {
        return (Encoding.UTF8.GetByteCount(line) + Environment.NewLine.Length + 3) / 4;
    }

    // Console logs have no structured attributes, so all search text is treated as
    // free-text fragments matched against the log content and resource name.
    private static bool Matches(LogEntry entry, string[] fragments)
    {
        return fragments.Length == 0 || SearchTextParser.MatchesAllFragments(
            fragments,
            (entry.Content ?? string.Empty, entry.RawContent ?? string.Empty, entry.ResourcePrefix ?? string.Empty),
            static (state, fragment) =>
                state.Item1.Contains(fragment, StringComparisons.FullTextSearch) ||
                state.Item2.Contains(fragment, StringComparisons.FullTextSearch) ||
                state.Item3.Contains(fragment, StringComparisons.FullTextSearch));
    }

    private readonly record struct HeadSortKey(DateTime Timestamp, long ArrivalIndex) : IComparable<HeadSortKey>
    {
        public int CompareTo(HeadSortKey other)
        {
            var result = Timestamp.CompareTo(other.Timestamp);
            return result != 0 ? result : ArrivalIndex.CompareTo(other.ArrivalIndex);
        }
    }

    // PriorityQueue dequeues the smallest priority first; inverting the order makes its root the latest kept entry,
    // which is the one to evict when an earlier match arrives.
    private sealed class HeadSortKeyDescendingComparer : IComparer<HeadSortKey>
    {
        public static readonly HeadSortKeyDescendingComparer Instance = new();

        public int Compare(HeadSortKey x, HeadSortKey y)
        {
            return y.CompareTo(x);
        }
    }
}
