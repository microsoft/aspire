// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Aspire.Shared;
using Aspire.Shared.Model.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

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

internal sealed class McpResourceCommandArgumentJson
{
    public required string Name { get; init; }
    public required string InputType { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public Dictionary<string, string?>? Options { get; init; }
    public bool AllowCustomChoice { get; init; }
    public bool Disabled { get; init; }
    public int? MaxLength { get; init; }
    public ResourceCommandArgumentDynamicLoadingJson? DynamicLoading { get; init; }
}

internal sealed class McpResourceCommandJson
{
    public required string State { get; init; }
    public string? Description { get; init; }
    public McpResourceCommandArgumentJson[] ArgumentInputs { get; init; } = [];
}

internal sealed record McpResourcePageJson(int Offset, int Limit, int Total, int? NextOffset);

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
    public Dictionary<string, McpResourceCommandJson> Commands { get; init; } = [];
}

[JsonSerializable(typeof(McpResourceJson[]))]
[JsonSerializable(typeof(McpResourceUrlJson[]))]
[JsonSerializable(typeof(McpResourceRelationshipJson[]))]
[JsonSerializable(typeof(McpResourcePageJson))]
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
    private const int MaxResources = 64;
    private const int MaxUrlsPerResource = 16;
    private const int MaxRelationshipsPerResource = 32;
    private const int MaxWaitingForPerResource = 32;
    private const int MaxTextLength = 256;

    private static readonly JsonElement s_inputSchema = JsonDocument.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "offset": {
              "type": "integer",
              "minimum": 0,
              "maximum": {{int.MaxValue}},
              "default": 0,
              "description": "Zero-based offset in the visible resources ordered by runtime name. Use next_offset from the previous response to continue."
            },
            "limit": {
              "type": "integer",
              "minimum": 1,
              "maximum": {{MaxResources}},
              "default": {{MaxResources}},
              "description": "Maximum number of resources to return in this page."
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    public override string Name => KnownMcpTools.ListResources;

    public override string Description => "List the application resources for the selected AppHost. Includes runtime information such as resource type, state, source, endpoints, health status, relationships, and API-visible command metadata without current or default argument values. Returns up to 64 resources ordered by runtime name; use offset and limit to page through all visible resources.";

    public override JsonElement GetInputSchema()
    {
        return s_inputSchema;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var (offset, limit) = ParseArguments(context.Arguments);
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
            var eligibleSnapshots = allSnapshots
                .Where(snapshot => !McpToolHelpers.IsExcludedFromMcp(snapshot))
                .ToList();
            var visibleSnapshots = eligibleSnapshots
                .Where(snapshot => !ResourceSnapshotMapper.IsHiddenResource(snapshot))
                .OrderBy(snapshot => snapshot.Name, StringComparer.Ordinal)
                .ToList();
            var pageSnapshots = visibleSnapshots.Skip(offset).Take(limit).ToList();

            // Use the dashboard base URL if available
            var dashboardBaseUrl = McpToolHelpers.StripLoginPath(dashboardUrls?.BaseUrlWithLoginToken);
            // Resolve references against the whole snapshot, not just the current page: a
            // dependency on another page must retain its identity and replica ambiguity.
            var resourceIdentities = CreateResourceIdentityMap(eligibleSnapshots);
            var relationshipTargets = CreateRelationshipTargetMap(visibleSnapshots);

            // Project directly from the snapshot so unrelated properties, volumes, command
            // argument values, environment values, and health details never enter serialization.
            var boundedResources = pageSnapshots.Select(snapshot => new McpResourceJson
            {
                Name = GetBoundedText(snapshot.Name),
                DisplayName = GetBoundedText(snapshot.DisplayName),
                ResourceType = GetBoundedText(snapshot.ResourceType),
                State = McpToolHelpers.MapResourceState(snapshot.State),
                WaitingFor = GetBoundedWaitingFor(snapshot, resourceIdentities),
                StateStyle = GetBoundedText(snapshot.StateStyle),
                Source = GetBoundedSource(snapshot),
                ExitCode = snapshot.ExitCode,
                HealthStatus = GetBoundedText(snapshot.HealthStatus),
                DashboardUrl = GetDashboardUrl(snapshot, dashboardBaseUrl),
                Urls = snapshot.Urls.Take(MaxUrlsPerResource).Select(url => new McpResourceUrlJson
                {
                    Name = GetBoundedText(url.Name),
                    DisplayName = GetBoundedText(url.DisplayProperties?.DisplayName),
                    Url = GetBoundedText(McpToolHelpers.SanitizeResourceUrl(url.Url)),
                    IsInternal = url.IsInternal
                }).ToArray(),
                Relationships = GetBoundedRelationships(snapshot, relationshipTargets),
                Commands = GetCommandMetadata(snapshot)
            }).ToArray();
            var resourceGraphData = JsonSerializer.Serialize(boundedResources, ListResourcesToolJsonContext.RelaxedEscaping.McpResourceJsonArray);
            int? nextOffset = offset < visibleSnapshots.Count - boundedResources.Length
                ? offset + boundedResources.Length
                : null;
            var pagination = JsonSerializer.Serialize(
                new McpResourcePageJson(offset, limit, visibleSnapshots.Count, nextOffset),
                ListResourcesToolJsonContext.RelaxedEscaping.McpResourcePageJson);

            var response = $"""
            resource_name is the identifier of resources.
            Console logs for a resource can provide more information about why a resource is not in a running state.
            Command names and argument names can be passed to execute_resource_command. Current and default argument values are never included.
            Use next_offset with the same limit to retrieve the next page. An absent next_offset means there are no more resources.
            Each page reads current state; restart from offset 0 if resource membership changes while paging.

            # PAGINATION

            {pagination}

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

    private static (int Offset, int Limit) ParseArguments(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        // MCP pagination arguments use { "offset": 64, "limit": 64 }. Omitted fields retain
        // the first-page defaults; strings, nulls, fractions, and out-of-range values are errors.
        if (arguments?.Keys.Any(static name => name is not ("offset" or "limit")) == true)
        {
            throw new McpProtocolException(
                "Arguments may contain only 'offset' and 'limit'.",
                McpErrorCode.InvalidParams);
        }

        var offset = 0;
        if (arguments?.TryGetValue("offset", out var offsetElement) == true &&
            (offsetElement.ValueKind != JsonValueKind.Number ||
                !offsetElement.TryGetInt32(out offset) || offset < 0))
        {
            throw new McpProtocolException(
                $"Argument 'offset' must be an integer from 0 through {int.MaxValue}.",
                McpErrorCode.InvalidParams);
        }

        var limit = MaxResources;
        if (arguments?.TryGetValue("limit", out var limitElement) == true &&
            (limitElement.ValueKind != JsonValueKind.Number ||
                !limitElement.TryGetInt32(out limit) || limit is < 1 or > MaxResources))
        {
            throw new McpProtocolException(
                $"Argument 'limit' must be an integer from 1 through {MaxResources}.",
                McpErrorCode.InvalidParams);
        }

        return (offset, limit);
    }

    private static Dictionary<string, McpResourceCommandJson> GetCommandMetadata(ResourceSnapshot snapshot)
    {
        // The general CLI mapper retains non-secret input values. MCP instead exposes only
        // invocation metadata, including disabled API commands but never hidden or UI-only ones.
        // Keep identifiers and choice keys exact, and keep their complete collections: truncating
        // them would advertise a different command or an incomplete invocation contract.
        return snapshot.Commands
            .Where(command => ResourceSnapshotMapper.IsCommandVisibleToApi(command.Visibility) &&
                ResourceSnapshotMapper.IsCommandVisibleToConsumer(command.State, includeDisabledCommands: true))
            .OrderBy(command => command.Name, StringComparer.Ordinal)
            .ToDistinctDictionary(
                command => command.Name,
                command => new McpResourceCommandJson
                {
                    State = string.Equals(command.State, KnownCommandState.Enabled, StringComparison.OrdinalIgnoreCase)
                        ? KnownCommandState.Enabled
                        : KnownCommandState.Disabled,
                    Description = GetBoundedText(command.Description),
                    ArgumentInputs = command.ArgumentInputs.Select(input => new McpResourceCommandArgumentJson
                    {
                        Name = input.Name,
                        InputType = GetBoundedText(input.InputType)!,
                        Description = GetBoundedText(input.Description),
                        Required = input.Required,
                        Options = string.Equals(input.InputType, nameof(InputType.Choice), StringComparison.OrdinalIgnoreCase)
                            ? input.Options?.ToDictionary(option => option.Key, option => GetBoundedText(option.Value))
                            : null,
                        AllowCustomChoice = input.AllowCustomChoice,
                        Disabled = input.Disabled,
                        MaxLength = input.MaxLength,
                        DynamicLoading = ResourceSnapshotMapper.MapDynamicLoading(input.DynamicLoading)
                    }).ToArray()
                });
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
            if (resourceIdentities.TryGetValue(reference, out var identity))
            {
                var boundedName = GetBoundedText(identity.WaitingForName)!;
                if (!seenReferences.Add(boundedName))
                {
                    continue;
                }

                boundedReferences.Add(boundedName);
                if (boundedReferences.Count == MaxWaitingForPerResource)
                {
                    break;
                }
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
                var boundedType = GetBoundedText(relationship.Type);
                var boundedTarget = GetBoundedText(target);
                if (seenRelationships.Add($"{boundedType}\0{boundedTarget}"))
                {
                    relationships.Add(new McpResourceRelationshipJson
                    {
                        Type = boundedType,
                        ResourceName = boundedTarget
                    });
                    if (relationships.Count == MaxRelationshipsPerResource)
                    {
                        return [.. relationships];
                    }
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
        return GetBoundedText(McpToolHelpers.SanitizeUrl(dashboardUrl));
    }

    private static string? GetBoundedSource(ResourceSnapshot snapshot)
    {
        if (string.Equals(snapshot.ResourceType, KnownResourceTypes.Project, StringComparisons.ResourceType))
        {
            var projectPath = GetStringProperty(snapshot, KnownProperties.Project.Path);
            return projectPath is null ? null : GetBoundedText(GetCrossPlatformFileName(projectPath));
        }

        if (string.Equals(snapshot.ResourceType, KnownResourceTypes.Executable, StringComparisons.ResourceType))
        {
            var executablePath = GetStringProperty(snapshot, KnownProperties.Executable.Path);
            return executablePath is null ? null : GetBoundedText(GetCrossPlatformFileName(executablePath));
        }

        if (string.Equals(snapshot.ResourceType, KnownResourceTypes.Container, StringComparisons.ResourceType))
        {
            return GetBoundedText(GetStringProperty(snapshot, KnownProperties.Container.Image));
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

    private static string? GetBoundedText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        // The MCP contract bounds model-facing text in Unicode scalar values. Enumerating runes
        // avoids emitting half of a UTF-16 surrogate pair at the truncation boundary.
        var result = new StringBuilder(Math.Min(value.Length, MaxTextLength * 2));
        var runeCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (runeCount == MaxTextLength)
            {
                break;
            }

            result.Append(Rune.IsControl(rune) ? " " : rune.ToString());
            runeCount++;
        }

        return result.ToString();
    }
}
