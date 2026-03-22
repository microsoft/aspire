// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for executing commands on resources.
/// Executes commands directly via the AppHost backchannel.
/// </summary>
internal sealed class ExecuteResourceCommandTool(
    IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
    ILogger<ExecuteResourceCommandTool> logger) : CliMcpTool
{
    public override string Name => KnownMcpTools.ExecuteResourceCommand;

    public override string Description => "Executes a command on a resource. If a resource needs to be restarted and is currently stopped, use the start command instead.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "resourceName": {
                  "type": "string",
                  "description": "The resource name"
                },
                "commandName": {
                  "type": "string",
                  "description": "The command name"
                }
              },
              "required": ["resourceName", "commandName"]
            }
            """).RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;
        if (arguments is null ||
            !arguments.TryGetValue("resourceName", out var resourceNameElement) ||
            !arguments.TryGetValue("commandName", out var commandNameElement))
        {
            throw new McpProtocolException("Missing required arguments 'resourceName' and 'commandName'.", McpErrorCode.InvalidParams);
        }

        var resourceName = resourceNameElement.GetString();
        var commandName = commandNameElement.GetString();

        if (string.IsNullOrEmpty(resourceName) || string.IsNullOrEmpty(commandName))
        {
            throw new McpProtocolException("Arguments 'resourceName' and 'commandName' cannot be empty.", McpErrorCode.InvalidParams);
        }

        var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(auxiliaryBackchannelMonitor, logger, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            logger.LogWarning("No Aspire AppHost is currently running");
            throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
        }

        try
        {
            logger.LogDebug("Executing command '{CommandName}' on resource '{ResourceName}' via backchannel", commandName, resourceName);

            var response = await connection.ExecuteResourceCommandAsync(resourceName, commandName, cancellationToken).ConfigureAwait(false);

            if (response.Success)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = $"Command '{commandName}' executed successfully on resource '{resourceName}'." }]
                };
            }
            else if (response.Canceled)
            {
                throw new McpProtocolException($"Command '{commandName}' was cancelled.", McpErrorCode.InternalError);
            }
            else
            {
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
            logger.LogError(ex, "Error executing command '{CommandName}' on resource '{ResourceName}'", commandName, resourceName);
            throw new McpProtocolException($"Error executing command '{commandName}' for resource '{resourceName}': {ex.Message}", McpErrorCode.InternalError);
        }
    }
}
