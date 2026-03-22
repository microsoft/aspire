// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Mcp;
using Aspire.Cli.Mcp.Docs;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Packaging;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.EnvironmentChecker;
using Aspire.Shared.Mcp;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Aspire.Cli.Commands;

/// <summary>
/// Command that starts the MCP (Model Context Protocol) server.
/// This is the new command under 'aspire agent mcp'.
/// </summary>
internal sealed class AgentMcpCommand : BaseCommand
{
    private readonly Dictionary<string, CliMcpTool> _knownTools;
    private readonly IMcpResourceToolRefreshService _resourceToolRefreshService;
    private McpServer? _server;
    private readonly IAuxiliaryBackchannelMonitor _auxiliaryBackchannelMonitor;
    private readonly IMcpTransportFactory _transportFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentMcpCommand> _logger;

    /// <summary>
    /// Gets the dictionary of known MCP tools. Exposed for testing purposes.
    /// </summary>
    internal IReadOnlyDictionary<string, CliMcpTool> KnownTools => _knownTools;

    public AgentMcpCommand(
        IInteractionService interactionService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
        IMcpTransportFactory transportFactory,
        ILoggerFactory loggerFactory,
        ILogger<AgentMcpCommand> logger,
        IPackagingService packagingService,
        IEnvironmentChecker environmentChecker,
        IDocsSearchService docsSearchService,
        IDocsIndexService docsIndexService,
        IHttpClientFactory httpClientFactory,
        AspireCliTelemetry telemetry)
        : base("mcp", AgentCommandStrings.McpCommand_Description, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _auxiliaryBackchannelMonitor = auxiliaryBackchannelMonitor;
        _transportFactory = transportFactory;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _resourceToolRefreshService = new McpResourceToolRefreshService(auxiliaryBackchannelMonitor, loggerFactory.CreateLogger<McpResourceToolRefreshService>());
        _knownTools = new Dictionary<string, CliMcpTool>
        {
            [KnownMcpTools.ListResources] = new ListResourcesTool(auxiliaryBackchannelMonitor, loggerFactory.CreateLogger<ListResourcesTool>()),
            [KnownMcpTools.ListConsoleLogs] = new ListConsoleLogsTool(auxiliaryBackchannelMonitor, loggerFactory.CreateLogger<ListConsoleLogsTool>()),
            [KnownMcpTools.ExecuteResourceCommand] = new ExecuteResourceCommandTool(auxiliaryBackchannelMonitor, loggerFactory.CreateLogger<ExecuteResourceCommandTool>()),
            [KnownMcpTools.ListStructuredLogs] = new ListStructuredLogsTool(auxiliaryBackchannelMonitor, httpClientFactory, loggerFactory.CreateLogger<ListStructuredLogsTool>()),
            [KnownMcpTools.ListTraces] = new ListTracesTool(auxiliaryBackchannelMonitor, httpClientFactory, loggerFactory.CreateLogger<ListTracesTool>()),
            [KnownMcpTools.ListTraceStructuredLogs] = new ListTraceStructuredLogsTool(auxiliaryBackchannelMonitor, httpClientFactory, loggerFactory.CreateLogger<ListTraceStructuredLogsTool>()),
            [KnownMcpTools.SelectAppHost] = new SelectAppHostTool(auxiliaryBackchannelMonitor, executionContext),
            [KnownMcpTools.ListAppHosts] = new ListAppHostsTool(auxiliaryBackchannelMonitor, executionContext),
            [KnownMcpTools.ListIntegrations] = new ListIntegrationsTool(packagingService, executionContext, auxiliaryBackchannelMonitor),
            [KnownMcpTools.Doctor] = new DoctorTool(environmentChecker),
            [KnownMcpTools.RefreshTools] = new RefreshToolsTool(_resourceToolRefreshService),
            [KnownMcpTools.ListDocs] = new ListDocsTool(docsIndexService),
            [KnownMcpTools.SearchDocs] = new SearchDocsTool(docsSearchService, docsIndexService),
            [KnownMcpTools.GetDoc] = new GetDocTool(docsIndexService)
        };
    }

    protected override bool UpdateNotificationsEnabled => false;

    /// <summary>
    /// Public entry point for executing the MCP server command.
    /// This allows McpStartCommand to delegate to this implementation.
    /// </summary>
    internal Task<int> ExecuteCommandAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return ExecuteAsync(parseResult, cancellationToken);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var icons = McpIconHelper.GetAspireIcons(typeof(AgentMcpCommand).Assembly, "Aspire.Cli.Mcp.Resources");

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "aspire-mcp-server",
                Version = VersionHelper.GetDefaultTemplateVersion(),
                Icons = icons
            },
            Handlers = new McpServerHandlers()
            {
                ListToolsHandler = HandleListToolsAsync,
                CallToolHandler = HandleCallToolAsync
            },
        };

        var transport = _transportFactory.CreateTransport();
        await using var server = McpServer.Create(transport, options, _loggerFactory);

        // Configure the refresh service with the server
        _resourceToolRefreshService.SetMcpServer(server);
        _server = server;

        // Starts the MCP server, it's blocking until cancellation is requested
        await server.RunAsync(cancellationToken);

        // Clear the server reference on exit
        _resourceToolRefreshService.SetMcpServer(null);
        _server = null;

        return ExitCodeConstants.Success;
    }

    private async ValueTask<ListToolsResult> HandleListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("MCP ListTools request received");

        var tools = new List<Tool>();

        tools.AddRange(KnownTools.Values.Select(tool => new Tool
        {
            Name = tool.Name,
            Description = tool.Description,
            InputSchema = tool.GetInputSchema()
        }));

        try
        {
            // Refresh resource tools if needed (e.g., AppHost selection changed or invalidated)
            if (!_resourceToolRefreshService.TryGetResourceToolMap(out var resourceToolMap))
            {
                // Don't send tools/list_changed here — the client already called tools/list
                // and will receive the up-to-date result. Sending a notification during the
                // list handler would cause the client to call tools/list again, creating an
                // infinite loop when tool availability is unstable (e.g., container MCP tools
                // oscillating between available/unavailable).
                (resourceToolMap, _) = await _resourceToolRefreshService.RefreshResourceToolMapAsync(cancellationToken);
            }

            tools.AddRange(resourceToolMap.Select(x => new Tool
            {
                Name = x.Key,
                Description = x.Value.Tool.Description,
                InputSchema = x.Value.Tool.InputSchema
            }));
        }
        catch (Exception ex)
        {
            // Don't fail ListTools if resource discovery fails; still return CLI tools.
            _logger.LogDebug(ex, "Failed to aggregate resource MCP tools");
        }

        _logger.LogDebug("Returning {ToolCount} tools", tools.Count);

        return new ListToolsResult { Tools = [.. tools] };
    }

    private async ValueTask<CallToolResult> HandleCallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var toolName = request.Params?.Name ?? string.Empty;

        _logger.LogDebug("MCP CallTool request received for tool: {ToolName}", toolName);

        if (KnownTools.TryGetValue(toolName, out var tool))
        {
            var args = request.Params?.Arguments is { } a
                ? new Dictionary<string, JsonElement>(a)
                : null;
            var context = new CallToolContext
            {
                Notifier = new McpServerNotifier(_server!),
                McpClient = null,
                Arguments = args,
                ProgressToken = request.Params?.ProgressToken
            };
            return await tool.CallToolAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var toolsRefreshed = false;

        // Refresh resource tools if needed (e.g., AppHost selection changed or invalidated)
        if (!_resourceToolRefreshService.TryGetResourceToolMap(out var resourceToolMap))
        {
            bool changed;
            (resourceToolMap, changed) = await _resourceToolRefreshService.RefreshResourceToolMapAsync(cancellationToken);
            if (changed)
            {
                await _resourceToolRefreshService.SendToolsListChangedNotificationAsync(cancellationToken).ConfigureAwait(false);
            }
            toolsRefreshed = true;
        }

        // Resource MCP tools are invoked via the AppHost backchannel (AppHost proxies to the resource MCP endpoint).
        if (resourceToolMap.TryGetValue(toolName, out var resourceAndTool))
        {
            var connection = await GetSelectedConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (connection == null)
            {
                throw new McpProtocolException(
                    "No Aspire AppHost is currently running. To use resource MCP tools, start an Aspire application (e.g. 'aspire run') and then retry.",
                    McpErrorCode.InternalError);
            }

            var args = request.Params?.Arguments is { } a
                ? new Dictionary<string, JsonElement>(a)
                : null;

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Invoking tool {Name} with arguments {Arguments}", toolName, JsonSerializer.Serialize(args, BackchannelJsonSerializerContext.Default.DictionaryStringJsonElement));
            }

            var result = await connection.CallResourceMcpToolAsync(resourceAndTool.ResourceName, resourceAndTool.Tool.Name, args, cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                throw new McpProtocolException($"Failed to get MCP tool result for '{toolName}'. Try refreshing the tools with 'refresh_tools'.", McpErrorCode.InternalError);
            }

            return result;
        }

        _logger.LogWarning("Unknown tool requested: {ToolName}", toolName);

        // If we haven't refreshed yet, try refreshing once more in case the resource list changed
        if (!toolsRefreshed)
        {
            _resourceToolRefreshService.InvalidateToolMap();
            return await HandleCallToolAsync(request, cancellationToken).ConfigureAwait(false);
        }

        throw new McpProtocolException($"Unknown tool: '{toolName}'", McpErrorCode.MethodNotFound);
    }

    /// <summary>
    /// Gets the appropriate AppHost connection based on the selection logic.
    /// </summary>
    private Task<IAppHostAuxiliaryBackchannel?> GetSelectedConnectionAsync(CancellationToken cancellationToken)
    {
        return AppHostConnectionHelper.GetSelectedConnectionAsync(_auxiliaryBackchannelMonitor, _logger, cancellationToken);
    }
}
