// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
                // Keep only the first matching lines, but read the whole stream so the summary can report
                // how many later lines were not returned. The tail buffer below can't be reused here because
                // it evicts the oldest lines once it is full.
                var headEntries = new List<LogEntry>();
                var totalLogsCount = 0;
                await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: false, cancellationToken).ConfigureAwait(false))
                {
                    var entry = logParser.CreateLogEntry(logLine.Content, logLine.IsError, resourceName);
                    if (!Matches(entry, fragments))
                    {
                        continue;
                    }

                    totalLogsCount++;
                    if (headEntries.Count < limit)
                    {
                        headEntries.Add(entry);
                    }
                }

                (trimmedItems, limitMessage) = SharedAIHelpers.GetLimitFromStartWithSummary(
                    headEntries,
                    totalLogsCount,
                    limit,
                    SharedAIHelpers.CompactListTokenLength,
                    "console log",
                    "console logs",
                    SharedAIHelpers.SerializeLogEntry,
                    SharedAIHelpers.EstimateTokenCount);
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
                    SharedAIHelpers.EstimateTokenCount);
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
}
