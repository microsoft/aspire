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
        if (arguments is not null && arguments.TryGetValue("resourceName", out var resourceNameElement))
        {
            resourceName = resourceNameElement.GetString();
        }

        if (string.IsNullOrEmpty(resourceName))
        {
            throw new McpProtocolException("The resourceName parameter is required.", McpErrorCode.InvalidParams);
        }

        var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(auxiliaryBackchannelMonitor, logger, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            logger.LogWarning("No Aspire AppHost is currently running");
            throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
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
            var totalLogsCount = entries.Count == 0 ? 0 : entries.Last().LineNumber;
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
