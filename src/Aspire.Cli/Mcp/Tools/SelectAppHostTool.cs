// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Hosting.Utils;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

/// <summary>
/// MCP tool for selecting which AppHost to use when multiple are running.
/// </summary>
internal sealed class SelectAppHostTool(IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor, CliExecutionContext executionContext) : CliMcpTool
{
    public override string Name => KnownMcpTools.SelectAppHost;

    public override string Description => "Selects which AppHost to use when multiple AppHosts are running. The path can be a fully qualified path or a workspace root relative path.";

    public override ToolAnnotations Annotations => new()
    {
        ReadOnlyHint = false,
        DestructiveHint = false
    };

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "appHostPath": {
                  "type": "string",
                  "description": "The fully qualified or workspace root relative path to the AppHost project."
                }
              },
              "required": ["appHostPath"]
            }
            """).RootElement;
    }

    public override ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = context.Arguments;

        if (arguments == null || !arguments.TryGetValue("appHostPath", out var appHostPathElement))
        {
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "The 'appHostPath' argument is required." }]
            });
        }

        var appHostPath = appHostPathElement.GetString();
        if (string.IsNullOrWhiteSpace(appHostPath))
        {
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "The 'appHostPath' argument cannot be empty." }]
            });
        }

        // Preserve the caller's spelling for diagnostics while using a canonical identity for matching.
        var displayPath = Path.GetFullPath(
            Path.IsPathRooted(appHostPath)
                ? appHostPath
                : Path.Combine(executionContext.WorkingDirectory.FullName, appHostPath));
        var canonicalPath = PathNormalizer.ResolveToFilesystemPath(displayPath);

        // Check if there's a running AppHost with this path
        IAppHostAuxiliaryBackchannel? matchingConnection;
        try
        {
            matchingConnection = AppHostConnectionHelper.FindConnectionByAppHostPath(
                auxiliaryBackchannelMonitor.Connections,
                canonicalPath);
        }
        catch (InvalidOperationException)
        {
            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "Multiple running AppHost instances match that path. Stop the extra instance and retry." }]
            });
        }

        if (matchingConnection == null)
        {
            // The requested and available paths are local machine details. Keep the model-facing
            // error useful but path-free even when the caller supplied an absolute path.
            var hasAvailableAppHosts = auxiliaryBackchannelMonitor.Connections
                .Any(static connection => connection.AppHostInfo?.AppHostPath is not null);
            var message = hasAvailableAppHosts
                ? "No running AppHost matched 'appHostPath'. Other AppHosts are currently running."
                : "No running AppHost matched 'appHostPath'. No AppHosts are currently running.";

            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = message }]
            });
        }

        // Pin the connection's physical identity rather than the caller's spelling. A selected
        // symlink can be retargeted after this call, but it must not redirect later MCP operations
        // to a different running AppHost.
        auxiliaryBackchannelMonitor.SelectedAppHostPath =
            PathNormalizer.ResolveToFilesystemPath(matchingConnection.AppHostInfo!.AppHostPath);

        return ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Selected AppHost: {displayPath}" }]
        });
    }
}
