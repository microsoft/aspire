// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

internal sealed class RefreshToolsTool(IMcpResourceToolRefreshService refreshService) : CliMcpTool
{
    public override string Name => KnownMcpTools.RefreshTools;

    public override string Description => "Requests the server to emit a tools list changed notification so clients can re-fetch the available tools.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("{ \"type\": \"object\", \"properties\": {} }").RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var (resourceToolMap, _) = await refreshService.RefreshResourceToolMapAsync(cancellationToken).ConfigureAwait(false);
        await refreshService.SendToolsListChangedNotificationAsync(cancellationToken).ConfigureAwait(false);

        var totalToolCount = KnownMcpTools.All.Count + resourceToolMap.Count;
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = $"Tools refreshed: {totalToolCount} tools available" }]
        };
    }
}
