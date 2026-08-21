// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

internal sealed record ListResourcesResult(McpResourceJson[] Resources);

internal sealed class McpResourceUrlJson
{
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public string? Url { get; init; }
    public bool IsInternal { get; init; }
}

internal sealed class McpResourceRelationshipJson
{
    public string? Type { get; init; }
    public string? ResourceName { get; init; }
}

internal sealed class McpResourceJson
{
    public string? Name { get; init; }
    public string? DisplayName { get; init; }
    public string? ResourceType { get; init; }
    public string? State { get; init; }
    public string[] WaitingFor { get; init; } = [];
    public string? StateStyle { get; init; }
    public string? Source { get; init; }
    public int? ExitCode { get; init; }
    public string? HealthStatus { get; init; }
    public string? DashboardUrl { get; init; }
    public McpResourceUrlJson[] Urls { get; init; } = [];
    public McpResourceRelationshipJson[] Relationships { get; init; } = [];
}

[JsonSerializable(typeof(ListResourcesResult))]
[JsonSerializable(typeof(McpResourceJson[]))]
[JsonSerializable(typeof(McpResourceUrlJson[]))]
[JsonSerializable(typeof(McpResourceRelationshipJson[]))]
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

    public override string Description => "List the application resources for the selected AppHost. Includes bounded runtime information such as resource type, state, source, endpoints, health status, and relationships.";

    public override JsonElement GetInputSchema()
    {
        return JsonDocument.Parse("{ \"type\": \"object\", \"properties\": {} }").RootElement;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        IAppHostAuxiliaryBackchannel? connection;
        try
        {
            connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(
                auxiliaryBackchannelMonitor,
                logger,
                cancellationToken).ConfigureAwait(false);
        }
        catch (McpProtocolException) when (auxiliaryBackchannelMonitor.SelectedAppHostPath is not null)
        {
            // The selector is internal routing state. AppHostConnectionHelper logs the unavailable
            // identity for maintainers, but model-facing errors must not echo its absolute path.
            throw new McpProtocolException(
                "The selected AppHost is not available. Start that AppHost and retry.",
                McpErrorCode.InternalError);
        }

        if (connection is null)
        {
            logger.LogWarning("No Aspire AppHost is currently running");
            throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
        }

        if (connection.AppHostInfo?.AppHostPath is not { Length: > 0 } appHostPath)
        {
            logger.LogWarning("The selected AppHost connection does not have a project path");
            throw new McpProtocolException("The selected AppHost project path is not available.", McpErrorCode.InternalError);
        }

        var selectedAppHostPath = auxiliaryBackchannelMonitor.SelectedAppHostPath;
        if (selectedAppHostPath is not null &&
            AppHostPathComparer.PathsEqual(selectedAppHostPath, appHostPath))
        {
            // Preserve the identity the caller selected (including a symlinked spelling) while
            // still requiring it to resolve to the connection chosen by the shared comparer.
            appHostPath = selectedAppHostPath;
        }

        try
        {
            // Get dashboard URL and resource snapshots in parallel
            var dashboardUrlsTask = connection.GetDashboardUrlsAsync(cancellationToken);
            var snapshotsTask = connection.GetResourceSnapshotsAsync(includeHidden: true, cancellationToken);

            await Task.WhenAll(dashboardUrlsTask, snapshotsTask).ConfigureAwait(false);

            var dashboardUrls = await dashboardUrlsTask.ConfigureAwait(false);
            var allSnapshots = await snapshotsTask.ConfigureAwait(false);

            // Hidden snapshots still participate in display-name identity so a dependency on
            // two runtime resources cannot collapse merely because one target is hidden.
            var identitySnapshots = allSnapshots
                .Where(snapshot => !McpToolHelpers.IsExcludedFromMcp(snapshot))
                .ToList();
            var visibleSnapshots = identitySnapshots
                .Where(snapshot => !ResourceSnapshotMapper.IsHiddenResource(snapshot))
                .ToList();

            // Use the dashboard base URL if available
            var dashboardBaseUrl = McpToolHelpers.StripLoginPath(dashboardUrls?.BaseUrlWithLoginToken);
            var resourceIdentities = CreateResourceIdentityMap(identitySnapshots);
            var relationshipTargets = CreateRelationshipTargetMap(visibleSnapshots);

            // Project directly from the snapshot so unrelated properties, volumes, commands,
            // environment values, and health details never enter the MCP serialization boundary.
            var boundedResources = visibleSnapshots.Select(snapshot => new McpResourceJson
            {
                Name = snapshot.Name,
                DisplayName = snapshot.DisplayName,
                ResourceType = snapshot.ResourceType,
                State = snapshot.State,
                WaitingFor = GetBoundedWaitingFor(snapshot, resourceIdentities),
                StateStyle = snapshot.StateStyle,
                Source = GetBoundedSource(snapshot),
                ExitCode = snapshot.ExitCode,
                HealthStatus = snapshot.HealthStatus,
                DashboardUrl = GetDashboardUrl(snapshot, dashboardBaseUrl),
                Urls = snapshot.Urls.Select(url => new McpResourceUrlJson
                {
                    Name = url.Name,
                    DisplayName = url.DisplayProperties?.DisplayName,
                    Url = McpToolHelpers.SanitizeResourceUrl(url.Url),
                    IsInternal = url.IsInternal
                }).ToArray(),
                Relationships = GetBoundedRelationships(snapshot, relationshipTargets)
            }).ToArray();
            var responseData = new ListResourcesResult(boundedResources);
            var resourceGraphData = JsonSerializer.Serialize(responseData, ListResourcesToolJsonContext.RelaxedEscaping.ListResourcesResult);

            var response = $"""
            resource_name is the identifier of resources.
            Console logs for a resource can provide more information about why a resource is not in a running state.

            # RESOURCE DATA

            {resourceGraphData}
            """;

            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = response }]
            };
        }
        catch (Exception ex) when (ex is not McpProtocolException and not OperationCanceledException)
        {
            logger.LogError(
                "Error retrieving resources for AppHost {AppHostPath}: {Diagnostic}",
                appHostPath,
                McpToolHelpers.GetBoundedExceptionDiagnostic(ex));
            throw new McpProtocolException(
                "Unable to retrieve resources from the selected AppHost.",
                McpErrorCode.InternalError);
        }
    }

    private static Dictionary<string, (string WaitingForName, string RelationshipName)> CreateResourceIdentityMap(
        IReadOnlyList<ResourceSnapshot> snapshots)
    {
        var displayNameCounts = new Dictionary<string, int>(StringComparers.ResourceName);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.DisplayName is { } displayName)
            {
                displayNameCounts[displayName] = displayNameCounts.GetValueOrDefault(displayName) + 1;
            }
        }

        var identities = new Dictionary<string, (string WaitingForName, string RelationshipName)>(
            StringComparers.ResourceName);

        // Runtime names take precedence over display names, matching ResolveResources.
        foreach (var snapshot in snapshots)
        {
            var hasDuplicateDisplayName = snapshot.DisplayName is { } displayName &&
                displayNameCounts.GetValueOrDefault(displayName) > 1;
            identities[snapshot.Name] = (
                hasDuplicateDisplayName ? snapshot.Name : snapshot.DisplayName ?? snapshot.Name,
                snapshot.Name);
        }

        foreach (var snapshot in snapshots)
        {
            if (snapshot.DisplayName is not { } displayName || identities.ContainsKey(displayName))
            {
                continue;
            }

            identities[displayName] = displayNameCounts[displayName] > 1
                ? (displayName, displayName)
                : (displayName, snapshot.Name);
        }

        return identities;
    }

    private static Dictionary<string, List<string>> CreateRelationshipTargetMap(
        IReadOnlyList<ResourceSnapshot> snapshots)
    {
        var runtimeNames = snapshots
            .Select(snapshot => snapshot.Name)
            .ToHashSet(StringComparers.ResourceName);
        var targets = new Dictionary<string, List<string>>(StringComparers.ResourceName);

        // Runtime names always resolve to exactly that snapshot, even when another resource
        // uses the same value as its display name.
        foreach (var snapshot in snapshots)
        {
            targets[snapshot.Name] = [snapshot.Name];
        }

        foreach (var snapshot in snapshots)
        {
            if (snapshot.DisplayName is not { } displayName || runtimeNames.Contains(displayName))
            {
                continue;
            }

            if (!targets.TryGetValue(displayName, out var displayTargets))
            {
                displayTargets = [];
                targets.Add(displayName, displayTargets);
            }

            if (!displayTargets.Contains(snapshot.Name, StringComparers.ResourceName))
            {
                displayTargets.Add(snapshot.Name);
            }
        }

        return targets;
    }

    private static string[] GetBoundedWaitingFor(
        ResourceSnapshot snapshot,
        IReadOnlyDictionary<string, (string WaitingForName, string RelationshipName)> resourceIdentities)
    {
        var references = snapshot.WaitingFor;
        if (references is not { Length: > 0 } &&
            GetStringProperty(snapshot, KnownProperties.Resource.WaitingFor) is { } waitingForProperty &&
            !string.IsNullOrWhiteSpace(waitingForProperty))
        {
            references = waitingForProperty.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (references is not { Length: > 0 })
        {
            return [];
        }

        var boundedReferences = new List<string>();
        var seenReferences = new HashSet<string>(StringComparers.ResourceName);

        foreach (var reference in references)
        {
            if (resourceIdentities.TryGetValue(reference, out var identity) &&
                seenReferences.Add(identity.WaitingForName))
            {
                boundedReferences.Add(identity.WaitingForName);
            }
        }

        return [.. boundedReferences];
    }

    private static McpResourceRelationshipJson[] GetBoundedRelationships(
        ResourceSnapshot snapshot,
        IReadOnlyDictionary<string, List<string>> relationshipTargets)
    {
        var relationships = new List<McpResourceRelationshipJson>();
        var seenRelationships = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var relationship in snapshot.Relationships)
        {
            if (!relationshipTargets.TryGetValue(relationship.ResourceName, out var targets))
            {
                continue;
            }

            foreach (var target in targets)
            {
                if (seenRelationships.Add($"{relationship.Type}\0{target}"))
                {
                    relationships.Add(new McpResourceRelationshipJson
                    {
                        Type = relationship.Type,
                        ResourceName = target
                    });
                }
            }
        }

        return [.. relationships];
    }

    private static string? GetDashboardUrl(ResourceSnapshot snapshot, string? dashboardBaseUrl)
    {
        if (dashboardBaseUrl is null)
        {
            return null;
        }

        var dashboardUrl = DashboardUrls.CombineUrl(dashboardBaseUrl, DashboardUrls.ResourcesUrl(snapshot.Name));
        return McpToolHelpers.SanitizeUrl(dashboardUrl);
    }

    private static string? GetBoundedSource(ResourceSnapshot snapshot)
    {
        if (string.Equals(snapshot.ResourceType, KnownResourceTypes.Project, StringComparisons.ResourceType))
        {
            var projectPath = GetStringProperty(snapshot, KnownProperties.Project.Path);
            return projectPath is null ? null : GetCrossPlatformFileName(projectPath);
        }

        if (string.Equals(snapshot.ResourceType, KnownResourceTypes.Executable, StringComparisons.ResourceType))
        {
            var executablePath = GetStringProperty(snapshot, KnownProperties.Executable.Path);
            return executablePath is null ? null : GetCrossPlatformFileName(executablePath);
        }

        if (string.Equals(snapshot.ResourceType, KnownResourceTypes.Container, StringComparisons.ResourceType))
        {
            return GetStringProperty(snapshot, KnownProperties.Container.Image);
        }

        return null;
    }

    private static string GetCrossPlatformFileName(string path)
    {
        var separatorIndex = path.LastIndexOfAny(['/', '\\']);
        return separatorIndex >= 0 ? path[(separatorIndex + 1)..] : path;
    }

    private static string? GetStringProperty(ResourceSnapshot snapshot, string propertyName)
    {
        if (snapshot.Properties.TryGetValue(propertyName, out var value) &&
            value is JsonValue jsonValue &&
            jsonValue.TryGetValue<string>(out var stringValue) &&
            !string.IsNullOrEmpty(stringValue))
        {
            return stringValue;
        }

        return null;
    }
}
