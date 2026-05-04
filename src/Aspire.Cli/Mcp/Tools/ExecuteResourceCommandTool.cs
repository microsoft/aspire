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
        // MCP input schema JSON accepts optional nested command arguments:
        // { "resourceName": "web", "commandName": "click", "arguments": { "selector": "#submit" } }
        using var document = JsonDocument.Parse("""
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
                },
                "arguments": {
                  "type": "object",
                  "description": "Optional invocation arguments to pass to the resource command",
                  "additionalProperties": true
                }
              },
              "required": ["resourceName", "commandName"]
            }
            """);
        return document.RootElement.Clone();
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var toolArguments = context.Arguments;
        if (toolArguments is null ||
            !toolArguments.TryGetValue("resourceName", out var resourceNameElement) ||
            !toolArguments.TryGetValue("commandName", out var commandNameElement))
        {
            throw new McpProtocolException("Missing required arguments 'resourceName' and 'commandName'.", McpErrorCode.InvalidParams);
        }

        var resourceName = resourceNameElement.GetString();
        var commandName = commandNameElement.GetString();

        if (string.IsNullOrEmpty(resourceName) || string.IsNullOrEmpty(commandName))
        {
            throw new McpProtocolException("Arguments 'resourceName' and 'commandName' cannot be empty.", McpErrorCode.InvalidParams);
        }

        JsonElement? commandArguments = null;
        if (toolArguments.TryGetValue("arguments", out var commandArgumentsElement))
        {
            if (commandArgumentsElement.ValueKind != JsonValueKind.Object)
            {
                throw new McpProtocolException("Argument 'arguments' must be a JSON object.", McpErrorCode.InvalidParams);
            }

            commandArguments = commandArgumentsElement.Clone();
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

            var response = await connection.ExecuteResourceCommandAsync(resourceName, commandName, commandArguments, cancellationToken).ConfigureAwait(false);

            if (response.Success)
            {
                var content = new List<TextContentBlock>
                {
                    new() { Text = $"Command '{commandName}' executed successfully on resource '{resourceName}'." }
                };

                if (response.Value is not null)
                {
                    content.Add(new TextContentBlock { Text = response.Value.Value });
                }

                return new CallToolResult
                {
                    Content = [.. content]
                };
            }
            else if (response.Canceled)
            {
                throw new McpProtocolException($"Command '{commandName}' was cancelled.", McpErrorCode.InternalError);
            }
            else
            {
#pragma warning disable CS0618 // Type or member is obsolete
                var message = (response.Message ?? response.ErrorMessage) is { Length: > 0 } errorMsg ? errorMsg : "Unknown error. See logs for details.";
#pragma warning restore CS0618 // Type or member is obsolete
                if (response.ValidationErrors.Length > 0)
                {
                    message = $"{message}{Environment.NewLine}{string.Join(Environment.NewLine, response.ValidationErrors.Select(error => $"{error.ArgumentName}: {error.ErrorMessage}"))}";
                }

                var content = new List<TextContentBlock>
                {
                    new() { Text = $"Command '{commandName}' failed for resource '{resourceName}': {message}" }
                };

                if (response.Value is not null)
                {
                    content.Add(new TextContentBlock { Text = response.Value.Value });
                }

                return new CallToolResult
                {
                    IsError = true,
                    Content = [.. content]
                };
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
