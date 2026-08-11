// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Json;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Dashboard.Utils;
using Aspire.Otlp.Serialization;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for listing console logs for a resource.
/// Gets log data directly from the AppHost backchannel instead of forwarding to the dashboard.
/// </summary>
internal sealed class ListConsoleLogsTool : CliMcpTool
{
    private readonly IDashboardInfoProvider? _dashboardInfoProvider;
    private readonly IAuxiliaryBackchannelMonitor? _auxiliaryBackchannelMonitor;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger<ListConsoleLogsTool> _logger;

    public ListConsoleLogsTool(IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor, ILogger<ListConsoleLogsTool> logger)
        : this(null, auxiliaryBackchannelMonitor, null, logger)
    {
    }

    public ListConsoleLogsTool(
        IDashboardInfoProvider? dashboardInfoProvider,
        IAuxiliaryBackchannelMonitor? auxiliaryBackchannelMonitor,
        IHttpClientFactory? httpClientFactory,
        ILogger<ListConsoleLogsTool> logger)
    {
        _dashboardInfoProvider = dashboardInfoProvider;
        _auxiliaryBackchannelMonitor = auxiliaryBackchannelMonitor;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

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
                                },
                                "runId": {
                                    "type": "string",
                                    "description": "Dashboard run ID. Omit to query live logs from the current AppHost."
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
        var runId = McpToolHelpers.GetOptionalStringArgument(arguments, "runId");

        try
        {
            var logParser = new LogParser(ConsoleColor.Black);
            var logEntries = new LogEntries(maximumEntryCount: SharedAIHelpers.ConsoleLogsLimit) { BaseLineNumber = 1 };
            int totalLogsCount;

            if (runId is null && _auxiliaryBackchannelMonitor is not null)
            {
                var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(_auxiliaryBackchannelMonitor, _logger, cancellationToken).ConfigureAwait(false);
                if (connection is null)
                {
                    _logger.LogWarning("No Aspire AppHost is currently running");
                    throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
                }

                var excludedResult = await McpToolHelpers.CheckResourceExcludedAsync(connection, resourceName, cancellationToken).ConfigureAwait(false);
                if (excludedResult is not null)
                {
                    return excludedResult;
                }

                await foreach (var logLine in connection.GetResourceLogsAsync(resourceName, follow: false, cancellationToken).ConfigureAwait(false))
                {
                    logEntries.InsertSorted(logParser.CreateLogEntry(logLine.Content, logLine.IsError, resourceName));
                }

                var liveEntries = logEntries.GetEntries();
                totalLogsCount = liveEntries.Count == 0 ? 0 : liveEntries.Last().LineNumber;
            }
            else
            {
                if (_dashboardInfoProvider is null || _httpClientFactory is null)
                {
                    throw new McpProtocolException("Historical console logs require a Dashboard connection.", McpErrorCode.InternalError);
                }

                var (apiToken, apiBaseUrl, _) = await _dashboardInfoProvider.GetDashboardInfoAsync(cancellationToken).ConfigureAwait(false);
                using var client = TelemetryCommandHelpers.CreateApiClient(_httpClientFactory, apiToken);
                var url = DashboardUrls.TelemetryConsoleLogsApiUrl(
                    apiBaseUrl,
                    resourceName,
                    SharedAIHelpers.ConsoleLogsLimit,
                    search,
                    includeHidden: false,
                    runId);

                _logger.LogDebug("Fetching console logs from {Url}", url);
                var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                TelemetryCommandHelpers.EnsureTelemetryApiResponse(response);
                var apiResponse = await response.Content.ReadFromJsonAsync(
                    OtlpJsonSerializerContext.Default.ConsoleLogsApiResponse,
                    cancellationToken).ConfigureAwait(false);

                foreach (var logLine in apiResponse?.Logs ?? [])
                {
                    logEntries.InsertSorted(logParser.CreateLogEntry(logLine.Content, logLine.IsError, logLine.ResourceName));
                }

                totalLogsCount = apiResponse?.TotalCount ?? 0;
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
            if (runId is null && !string.IsNullOrEmpty(search))
            {
                totalLogsCount = entries.Count;
            }

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
            _logger.LogError(ex, "Error retrieving console logs for resource '{ResourceName}'", resourceName);
            if (runId is not null && ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound })
            {
                throw new McpProtocolException(TelemetryCommandHelpers.FormatHistoricalRunNotFound(runId), McpErrorCode.InternalError);
            }

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"Error retrieving console logs for resource '{resourceName}': {ex.Message}" }]
            };
        }
    }
}
