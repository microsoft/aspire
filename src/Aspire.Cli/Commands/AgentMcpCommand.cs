// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Mcp;
using Aspire.Cli.Documentation.Docs;
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
    private readonly Dictionary<string, CliMcpTool> _knownTools = [];
    private readonly IMcpResourceToolRefreshService _resourceToolRefreshService;
    private McpServer? _server;
    private readonly IAuxiliaryBackchannelMonitor _auxiliaryBackchannelMonitor;
    private readonly IMcpTransportFactory _transportFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AgentMcpCommand> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IPackagingService _packagingService;
    private readonly IEnvironmentChecker _environmentChecker;
    private readonly IDocsSearchService _docsSearchService;
    private readonly IDocsIndexService _docsIndexService;
    private readonly CliExecutionContext _executionContext;
    private bool _dashboardOnlyMode;

    private static readonly Option<string?> s_dashboardUrlOption = TelemetryCommandHelpers.CreateDashboardUrlOption();
    private static readonly Option<string?> s_apiKeyOption = TelemetryCommandHelpers.CreateApiKeyOption();

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
        _httpClientFactory = httpClientFactory;
        _packagingService = packagingService;
        _environmentChecker = environmentChecker;
        _docsSearchService = docsSearchService;
        _docsIndexService = docsIndexService;
        _executionContext = executionContext;
        _resourceToolRefreshService = new McpResourceToolRefreshService(auxiliaryBackchannelMonitor, loggerFactory.CreateLogger<McpResourceToolRefreshService>());

        Options.Add(s_dashboardUrlOption);
        Options.Add(s_apiKeyOption);
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
        var dashboardUrl = parseResult.GetValue(s_dashboardUrlOption);
        var apiKey = parseResult.GetValue(s_apiKeyOption);

        if (dashboardUrl is not null)
        {
            if (!UrlHelper.IsHttpUrl(dashboardUrl))
            {
                _logger.LogError("Invalid --dashboard-url: {DashboardUrl}", dashboardUrl);
                return ExitCodeConstants.InvalidCommand;
            }

            _dashboardOnlyMode = true;
            var staticProvider = new StaticDashboardInfoProvider(dashboardUrl, apiKey);

            _knownTools[KnownMcpTools.ListStructuredLogs] = new ListStructuredLogsTool(staticProvider, _httpClientFactory, _loggerFactory.CreateLogger<ListStructuredLogsTool>());
            _knownTools[KnownMcpTools.ListTraces] = new ListTracesTool(staticProvider, _httpClientFactory, _loggerFactory.CreateLogger<ListTracesTool>());
            _knownTools[KnownMcpTools.ListTraceStructuredLogs] = new ListTraceStructuredLogsTool(staticProvider, _httpClientFactory, _loggerFactory.CreateLogger<ListTraceStructuredLogsTool>());
        }
        else
        {
            var dashboardInfoProvider = new BackchannelDashboardInfoProvider(_auxiliaryBackchannelMonitor, _logger);

            _knownTools[KnownMcpTools.ListResources] = new ListResourcesTool(_auxiliaryBackchannelMonitor, _loggerFactory.CreateLogger<ListResourcesTool>());
            _knownTools[KnownMcpTools.ListConsoleLogs] = new ListConsoleLogsTool(_auxiliaryBackchannelMonitor, _loggerFactory.CreateLogger<ListConsoleLogsTool>());
            _knownTools[KnownMcpTools.ExecuteResourceCommand] = new ExecuteResourceCommandTool(_auxiliaryBackchannelMonitor, _loggerFactory.CreateLogger<ExecuteResourceCommandTool>());
            _knownTools[KnownMcpTools.ListStructuredLogs] = new ListStructuredLogsTool(dashboardInfoProvider, _httpClientFactory, _loggerFactory.CreateLogger<ListStructuredLogsTool>());
            _knownTools[KnownMcpTools.ListTraces] = new ListTracesTool(dashboardInfoProvider, _httpClientFactory, _loggerFactory.CreateLogger<ListTracesTool>());
            _knownTools[KnownMcpTools.ListTraceStructuredLogs] = new ListTraceStructuredLogsTool(dashboardInfoProvider, _httpClientFactory, _loggerFactory.CreateLogger<ListTraceStructuredLogsTool>());
            _knownTools[KnownMcpTools.SelectAppHost] = new SelectAppHostTool(_auxiliaryBackchannelMonitor, _executionContext);
            _knownTools[KnownMcpTools.ListAppHosts] = new ListAppHostsTool(_auxiliaryBackchannelMonitor, _executionContext);
            _knownTools[KnownMcpTools.ListIntegrations] = new ListIntegrationsTool(_packagingService, _executionContext, _auxiliaryBackchannelMonitor);
            _knownTools[KnownMcpTools.Doctor] = new DoctorTool(_environmentChecker);
            _knownTools[KnownMcpTools.RefreshTools] = new RefreshToolsTool(_resourceToolRefreshService);
            _knownTools[KnownMcpTools.ListDocs] = new ListDocsTool(_docsIndexService);
            _knownTools[KnownMcpTools.SearchDocs] = new SearchDocsTool(_docsSearchService, _docsIndexService);
            _knownTools[KnownMcpTools.GetDoc] = new GetDocTool(_docsIndexService);
        }

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

        tools.AddRange(KnownTools.Select(tool => new Tool
        {
            Name = tool.Value.Name,
            Description = tool.Value.Description,
            InputSchema = tool.Value.GetInputSchema()
        }));

        try
        {
            // In dashboard-only mode, skip resource tool discovery
            if (_dashboardOnlyMode)
            {
                _logger.LogDebug("Dashboard-only mode: skipping resource tool discovery");
            }
            else
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

        // In dashboard-only mode, only allow tools that were registered
        if (_dashboardOnlyMode && !_knownTools.ContainsKey(toolName))
        {
            throw new McpProtocolException(
                $"Tool '{toolName}' is not available in dashboard-only mode. Only telemetry tools (list_structured_logs, list_traces, list_trace_structured_logs) are available when using --dashboard-url.",
                McpErrorCode.MethodNotFound);
        }

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
