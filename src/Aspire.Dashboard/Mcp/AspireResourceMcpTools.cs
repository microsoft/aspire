// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Shared.ConsoleLogs;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Aspire.Dashboard.Mcp;

/// <summary>
/// MCP tools that require a resource service to be configured.
/// </summary>
internal sealed class AspireResourceMcpTools
{
    private readonly IDashboardClient _dashboardClient;
    private readonly IOptionsMonitor<DashboardOptions> _dashboardOptions;
    private readonly ILogger<AspireResourceMcpTools> _logger;

    public AspireResourceMcpTools(IDashboardClient dashboardClient,
        IOptionsMonitor<DashboardOptions> dashboardOptions,
        ILogger<AspireResourceMcpTools> logger)
    {
        _dashboardClient = dashboardClient;
        _dashboardOptions = dashboardOptions;
        _logger = logger;
    }

    [McpServerTool(Name = "list_resources")]
    [Description("List the application resources. Includes information about their type (.NET project, container, executable), running state, source, HTTP endpoints, health status, commands, configured environment variables, and relationships.")]
    public string ListResources()
    {
        _logger.LogDebug("MCP tool list_resources called");

        try
        {
            var resources = _dashboardClient.GetResources().ToList();
            var filteredResources = GetFilteredResources(resources);

            var resourceGraphData = AIHelpers.GetResponseGraphJson(
                filteredResources,
                _dashboardOptions.CurrentValue,
                includeDashboardUrl: true,
                includeEnvironmentVariables: true,
                getResourceName: r => ResourceViewModel.GetResourceName(r, resources));

            var response = $"""
            resource_name is the identifier of resources. Use the dashboard_link when displaying resource_name. For example: [`frontend-abcxyz`](https://localhost:1234/resource?name=frontend-abcxyz)
            environment_variables is a list of environment variables configured for the resource. Environment variable values aren't provided because they could contain sensitive information.
            Console logs for a resource can provide more information about why a resource is not in a running state.

            # RESOURCE DATA

            {resourceGraphData}
            """;

            return response;
        }
        catch { }

        return "No resources found.";
    }

    private static List<ResourceViewModel> GetFilteredResources(List<ResourceViewModel> resources)
    {
        return resources.Where(r => !AIHelpers.IsResourceAIOptOut(r)).ToList();
    }

    [McpServerTool(Name = "list_console_logs")]
    [Description("List console logs for a resource. The console logs includes standard output from resources and resource commands. Known resource commands are 'start', 'stop' and 'restart' which are used to start and stop resources. Don't print the full console logs in the response to the user. Console logs should be examined when determining why a resource isn't running.")]
    public async Task<string> ListConsoleLogsAsync(
        [Description("The resource name.")]
        string resourceName,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("MCP tool list_console_logs called with resource '{ResourceName}'.", resourceName);

        var resources = _dashboardClient.GetResources().ToList();
        var filteredResources = GetFilteredResources(resources);

        if (AIHelpers.TryGetResource(filteredResources, resourceName, out var resource))
        {
            resourceName = resource.Name;
        }
        else
        {
            return $"Unable to find a resource named '{resourceName}'.";
        }

        var logParser = new LogParser(ConsoleColor.Black, encodeForHtml: true);
        var logEntries = new LogEntries(maximumEntryCount: AIHelpers.ConsoleLogsLimit) { BaseLineNumber = 1 };

        // Add a timeout for getting all console logs.
        using var subscribeConsoleLogsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        subscribeConsoleLogsCts.CancelAfter(TimeSpan.FromSeconds(20));

        try
        {
            await foreach (var entry in _dashboardClient.GetConsoleLogs(resourceName, subscribeConsoleLogsCts.Token).ConfigureAwait(false))
            {
                foreach (var logLine in entry)
                {
                    logEntries.InsertSorted(logParser.CreateLogEntry(logLine.Content, logLine.IsErrorMessage, resourceName));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return $"Timeout getting console logs for `{resourceName}`";
        }

        var entries = logEntries.GetEntries().ToList();
        var totalLogsCount = entries.Count == 0 ? 0 : entries.Last().LineNumber;
        var (trimmedItems, limitMessage) = SharedAIHelpers.GetLimitFromEndWithSummary(
            entries,
            totalLogsCount,
            AIHelpers.ConsoleLogsLimit,
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

        return consoleLogsData;
    }

    [McpServerTool(Name = "execute_resource_command")]
    [Description("Executes a command on a resource. If a resource needs to be restarted and is currently stopped, use the start command instead.")]
    public async Task ExecuteResourceCommand([Description("The resource name")] string resourceName, [Description("The command name")] string commandName)
    {
        _logger.LogDebug("MCP tool execute_resource_command called with resource '{ResourceName}' and command '{CommandName}'.", resourceName, commandName);

        var resources = _dashboardClient.GetResources().ToList();
        var filteredResources = GetFilteredResources(resources);

        if (!AIHelpers.TryGetResource(filteredResources, resourceName, out var resource))
        {
            throw new McpProtocolException($"Resource '{resourceName}' not found.", McpErrorCode.InvalidParams);
        }

        var command = resource.Commands.FirstOrDefault(c => string.Equals(c.Name, commandName, StringComparisons.CommandName));

        if (command is null)
        {
            throw new McpProtocolException($"Command '{commandName}' not found for resource '{resourceName}'.", McpErrorCode.InvalidParams);
        }

        // Block execution when command isn't available.
        if (command.State == CommandViewModelState.Hidden)
        {
            throw new McpProtocolException($"Command '{commandName}' is not available for resource '{resourceName}'.", McpErrorCode.InvalidParams);
        }

        if (command.State == CommandViewModelState.Disabled)
        {
            if (command.Name == CommandViewModel.RestartCommand && resource.Commands.Any(c => c.Name == CommandViewModel.StartCommand && c.State == CommandViewModelState.Enabled))
            {
                throw new McpProtocolException($"Resource '{resourceName}' is stopped. Use the '{CommandViewModel.StartCommand}' command instead of '{CommandViewModel.RestartCommand}'.", McpErrorCode.InvalidParams);
            }

            throw new McpProtocolException($"Command '{commandName}' is currently disabled for resource '{resourceName}'.", McpErrorCode.InvalidParams);
        }

        try
        {
            var response = await _dashboardClient.ExecuteResourceCommandAsync(resource.Name, resource.ResourceType, command, CancellationToken.None).ConfigureAwait(false);

            switch (response.Kind)
            {
                case ResourceCommandResponseKind.Succeeded:
                    return;
                case ResourceCommandResponseKind.Cancelled:
                    throw new McpProtocolException($"Command '{commandName}' was cancelled.", McpErrorCode.InternalError);
                case ResourceCommandResponseKind.Failed:
                default:
                    var message = response.ErrorMessage is { Length: > 0 } ? response.ErrorMessage : "Unknown error. See logs for details.";
                    throw new McpProtocolException($"Command '{commandName}' failed for resource '{resourceName}': {message}", McpErrorCode.InternalError);
            }
        }
        catch (McpProtocolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new McpProtocolException($"Error executing command '{commandName}' for resource '{resourceName}': {ex.Message}", McpErrorCode.InternalError);
        }
    }
}
