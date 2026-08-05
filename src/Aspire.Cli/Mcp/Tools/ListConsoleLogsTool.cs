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
    public override string Name => KnownMcpTools.ListConsoleLogs;

    public override string Description => "List console logs for a resource. The console logs includes standard output from resources and resource commands. Known resource commands are 'start', 'stop' and 'restart' which are used to start and stop resources. Don't print the full console logs in the response to the user. Console logs should be examined when determining why a resource isn't running.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
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
                }
              },
              "required": ["resourceName"]
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;

        // Get the resource name from arguments
        string? resourceName = null;
        if (arguments is not null && arguments.TryGetValue("resourceName", out var resourceNameElement) &&
            resourceNameElement.ValueKind == JsonValueKind.String)
        {
            resourceName = resourceNameElement.GetString();
        }

        if (string.IsNullOrEmpty(resourceName))
        {
            throw new McpProtocolException("The resourceName parameter is required.", McpErrorCode.InvalidParams);
        }

        string? search = null;
        if (arguments is not null && arguments.TryGetValue("search", out var searchElement) &&
            searchElement.ValueKind == JsonValueKind.String)
        {
            search = searchElement.GetString();
        }

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
            var logEntries = new LogEntries(maximumEntryCount: SharedAIHelpers.ConsoleLogsLimit) { BaseLineNumber = 1 };

            // Collect logs from the backchannel
            await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: false, cancellationToken).ConfigureAwait(false))
            {
                logEntries.InsertSorted(logParser.CreateLogEntry(logLine.Content, logLine.IsError, resourceName));
            }

            var entries = logEntries.GetEntries().ToList();

            // Console logs have no structured attributes, so all search text is treated as
            // free-text fragments matched against the log content and resource name.
            if (!string.IsNullOrEmpty(search))
            {
                var fragments = SearchTextParser.ParseFragments(search);
                if (fragments.Length > 0)
                {
                    entries = entries.Where(e =>
                        SearchTextParser.MatchesAllFragments(
                            fragments,
                            (e.Content ?? string.Empty, e.RawContent ?? string.Empty, e.ResourcePrefix ?? string.Empty),
                            static (state, fragment) =>
                                state.Item1.Contains(fragment, StringComparisons.FullTextSearch) ||
                                state.Item2.Contains(fragment, StringComparisons.FullTextSearch) ||
                                state.Item3.Contains(fragment, StringComparisons.FullTextSearch)))
                        .ToList();
                }
            }

            // When search is applied, total reflects matching entries. Otherwise, use the
            // last line number which represents the total lines collected by the LogEntries buffer.
            var totalLogsCount = string.IsNullOrEmpty(search)
                ? (entries.Count == 0 ? 0 : entries.Last().LineNumber)
                : entries.Count;

            var (trimmedItems, limitMessage) = SharedAIHelpers.GetLimitFromEndWithSummary(
                entries,
                totalLogsCount,
                SharedAIHelpers.ConsoleLogsLimit,
                "console log",
                "console logs",
                SharedAIHelpers.SerializeLogEntry,
                SharedAIHelpers.EstimateTokenCount);
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
        catch (Exception ex) when (ex is not McpProtocolException)
        {
            logger.LogError(ex, "Error retrieving console logs for resource '{ResourceName}'", resourceName);
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Error retrieving console logs for resource '{resourceName}': {ex.Message}" }]
            };
        }
    }
}
