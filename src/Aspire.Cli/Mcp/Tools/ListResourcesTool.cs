// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Shared.Model.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

[JsonSerializable(typeof(ResourceJson[]))]
[JsonSerializable(typeof(ResourceUrlJson))]
[JsonSerializable(typeof(ResourceVolumeJson))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, ResourceHealthReportJson>))]
[JsonSerializable(typeof(ResourceRelationshipJson))]
[JsonSerializable(typeof(Dictionary<string, ResourceCommandJson>))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ListResourcesToolJsonContext : JsonSerializerContext
{
    private static ListResourcesToolJsonContext? s_relaxedEscaping;

    /// <summary>
    /// Gets a context with relaxed JSON escaping for non-ASCII character support (pretty-printed).
    /// </summary>
    public static ListResourcesToolJsonContext RelaxedEscaping => s_relaxedEscaping ??= new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}

/// <summary>
/// MCP tool for listing application resources.
/// Gets resource data directly from the AppHost backchannel instead of forwarding to the dashboard.
/// </summary>
internal sealed class ListResourcesTool(IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor, ILogger<ListResourcesTool> logger) : CliMcpTool
{
    public override string Name => KnownMcpTools.ListResources;

    public override string Description => "List the application resources. Includes information about their type (.NET project, container, executable), running state, source, HTTP endpoints, health status, commands, configured environment variables, and relationships.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("{ \"type\": \"object\", \"properties\": {} }").RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(auxiliaryBackchannelMonitor, logger, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            logger.LogWarning("No Aspire AppHost is currently running");
            throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
        }

        try
        {
            // Get dashboard URL and resource snapshots in parallel
            var dashboardUrlsTask = connection.GetDashboardUrlsAsync(cancellationToken);
            var snapshotsTask = connection.GetResourceSnapshotsAsync(cancellationToken);

            await Task.WhenAll(dashboardUrlsTask, snapshotsTask).ConfigureAwait(false);

            var dashboardUrls = await dashboardUrlsTask.ConfigureAwait(false);
            var snapshots = await snapshotsTask.ConfigureAwait(false);

            if (snapshots.Count == 0)
            {
                return new CallToolResult
                {
                    Content = [new TextContentBlock { Text = "No resources found." }]
                };
            }

            // Use the dashboard base URL if available
            var dashboardBaseUrl = dashboardUrls?.BaseUrlWithLoginToken;
            var resources = ResourceSnapshotMapper.MapToResourceJsonList(snapshots, dashboardBaseUrl, includeEnvironmentVariableValues: false);
            var resourceGraphData = JsonSerializer.Serialize(resources.ToArray(), ListResourcesToolJsonContext.RelaxedEscaping.ResourceJsonArray);

            var response = $"""
            resource_name is the identifier of resources.
            environment_variables is a list of environment variables configured for the resource. Environment variable values aren't provided because they could contain sensitive information.
            Console logs for a resource can provide more information about why a resource is not in a running state.

            # RESOURCE DATA

            {resourceGraphData}
            """;

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = response }]
            };
        }
        catch
        {
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = "No resources found." }]
            };
        }
    }
}
