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
        var matchingConnection = auxiliaryBackchannelMonitor.Connections
            .FirstOrDefault(c =>
            {
                if (c.AppHostInfo?.AppHostPath is null)
                {
                    return false;
                }
                return string.Equals(
                    PathNormalizer.ResolveToFilesystemPath(c.AppHostInfo.AppHostPath),
                    canonicalPath,
                    StringComparisons.FileSystemPath);
            });

        if (matchingConnection == null)
        {
            // List available AppHosts
            var availableAppHosts = auxiliaryBackchannelMonitor.Connections
                .Where(c => c.AppHostInfo?.AppHostPath != null)
                .Select(c => c.AppHostInfo!.AppHostPath)
                .ToList();

            var message = $"No running AppHost found at path '{displayPath}'.";
            if (availableAppHosts.Count > 0)
            {
                message += $" Available AppHosts:\n{string.Join("\n", availableAppHosts.Select(p => $"  - {p}"))}";
            }
            else
            {
                message += " No AppHosts are currently running.";
            }

            return ValueTask.FromResult(new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = message }]
            });
        }

        // Set the selected AppHost path
        auxiliaryBackchannelMonitor.SelectedAppHostPath = canonicalPath;

        return ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Selected AppHost: {displayPath}" }]
        });
    }
}
