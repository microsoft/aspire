// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Backchannel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Mcp.Tools;

internal sealed record WaitForResourcesResult(
    string Outcome,
    string TargetState,
    string? Error,
    WaitForResourceResultJson[] Resources);

internal sealed record WaitForResourceResultJson(
    string Name,
    string? State,
    string? Health,
    string Outcome,
    string? Error);

[JsonSerializable(typeof(WaitForResourcesResult))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class WaitForResourcesToolJsonContext : JsonSerializerContext;

/// <summary>
/// MCP tool for waiting for application resources to reach a target state.
/// </summary>
internal sealed class WaitForResourcesTool(
    IAuxiliaryBackchannelMonitor auxiliaryBackchannelMonitor,
    ResourceWaitService resourceWaitService,
    ILogger<WaitForResourcesTool> logger) : CliMcpTool
{
    private const string NoEligibleResourcesError = "No eligible resources were found in the selected AppHost.";
    internal const int MaximumResourceNameCount = 100;
    internal const int MaximumResourceNameLength = 256;

    private static readonly JsonElement s_inputSchema = JsonDocument.Parse(
        $$"""
        {
          "type": "object",
          "properties": {
            "resourceNames": {
              "type": "array",
              "maxItems": {{MaximumResourceNameCount}},
              "items": {
                "type": "string",
                "maxLength": {{MaximumResourceNameLength}}
              }
            },
            "targetState": {
              "type": "string",
              "enum": [
                "healthy",
                "up",
                "down"
              ],
              "default": "healthy"
            },
            "timeoutSeconds": {
              "type": "integer",
              "minimum": 1,
              "maximum": 3600,
              "default": 120
            }
          },
          "additionalProperties": false
        }
        """).RootElement;

    public override string Name => KnownMcpTools.WaitForResources;

    public override string Description => "Wait for selected application resources to reach a healthy, up, or down state.";

    public override JsonElement GetInputSchema()
    {
        return s_inputSchema;
    }

    public override async ValueTask<CallToolResult> CallToolAsync(CallToolContext context, CancellationToken cancellationToken)
    {
        var arguments = ParseArguments(context.Arguments);
        IAppHostAuxiliaryBackchannel? connection;
        try
        {
            connection = await AppHostConnectionHelper.GetSelectedConnectionAsync(
                auxiliaryBackchannelMonitor,
                logger,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not McpProtocolException and not OperationCanceledException)
        {
            logger.LogError(
                "Error resolving an Aspire AppHost connection: {Diagnostic}",
                McpToolHelpers.GetBoundedExceptionDiagnostic(ex));
            throw new McpProtocolException(
                "Unable to resolve an Aspire AppHost connection.",
                McpErrorCode.InternalError);
        }

        if (connection is null)
        {
            logger.LogWarning("No Aspire AppHost is currently running");
            throw new McpProtocolException(McpErrorMessages.NoAppHostRunning, McpErrorCode.InternalError);
        }

        if (connection.AppHostInfo?.AppHostPath is not { Length: > 0 })
        {
            logger.LogWarning("The selected AppHost connection does not have a project path");
            throw new McpProtocolException("The selected AppHost project path is not available.", McpErrorCode.InternalError);
        }

        List<ResourceSnapshot> snapshots;
        try
        {
            snapshots = await connection.GetResourceSnapshotsAsync(
                includeHidden: true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not McpProtocolException and not OperationCanceledException)
        {
            logger.LogError(
                "Error retrieving resources from the selected AppHost: {Diagnostic}",
                McpToolHelpers.GetBoundedExceptionDiagnostic(ex));
            throw new McpProtocolException(
                "Unable to retrieve resources from the selected AppHost.",
                McpErrorCode.InternalError);
        }

        var requestedNames = arguments.ResourceNames;
        var targets = requestedNames is { Count: > 0 }
            ? requestedNames.Select(name => ResolveNamedResource(name, snapshots)).ToArray()
            : snapshots
                .Where(static snapshot => !ResourceSnapshotMapper.IsHiddenResource(snapshot))
                .Where(static snapshot => !McpToolHelpers.IsExcludedFromMcp(snapshot))
                .Select(static snapshot => new ResolvedWaitTarget(snapshot, null))
                .ToArray();
        var validTargets = targets
            .Where(static target => target.Resource is not null)
            .Select(static target => target.Resource!)
            .DistinctBy(static target => target.Name, StringComparers.ResourceName)
            .ToArray();
        var noEligibleResources = requestedNames is not { Count: > 0 } && targets.Length == 0;
        IReadOnlyList<ResourceWaitResult> waitResults = noEligibleResources
            ? []
            : await resourceWaitService.WaitForResourcesAsync(
                connection,
                validTargets.Select(static target => target.Name).ToArray(),
                arguments.TargetState,
                arguments.TimeoutSeconds,
                cancellationToken).ConfigureAwait(false);
        var waitResultsByName = waitResults.ToDictionary(
            static result => result.ResourceName,
            StringComparers.ResourceName);
        var resources = targets.Select(target =>
            target.Failure ?? MapWaitResult(waitResultsByName[target.Resource!.Name])).ToArray();

        var result = new WaitForResourcesResult(
            noEligibleResources ? "failure" : GetOverallOutcome(resources),
            ResourceWaitService.GetProtocolValue(arguments.TargetState),
            noEligibleResources ? NoEligibleResourcesError : null,
            resources);
        var resultJson = JsonSerializer.Serialize(
            result,
            WaitForResourcesToolJsonContext.Default.WaitForResourcesResult);

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = $"""
                        # WAIT RESULT

                        {resultJson}
                        """
                }
            ]
        };
    }

    private static WaitForResourceResultJson MapWaitResult(ResourceWaitResult result)
    {
        return new WaitForResourceResultJson(
            result.ResourceName,
            MapResourceState(result.State),
            result.Health,
            GetOutcomeValue(result.Outcome),
            GetError(result));
    }

    private static WaitForResourcesArguments ParseArguments(IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        // MCP arguments arrive as:
        // { "resourceNames": ["api"], "targetState": "healthy", "timeoutSeconds": 120 }
        if (arguments?.Keys.Any(static name =>
            name is not ("resourceNames" or "targetState" or "timeoutSeconds")) == true)
        {
            throw new McpProtocolException(
                "Arguments may contain only 'resourceNames', 'targetState', and 'timeoutSeconds'.",
                McpErrorCode.InvalidParams);
        }

        IReadOnlyList<string>? resourceNames = null;
        if (arguments?.TryGetValue("resourceNames", out var resourceNamesElement) == true)
        {
            if (resourceNamesElement.ValueKind != JsonValueKind.Array ||
                resourceNamesElement.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String))
            {
                throw new McpProtocolException("Argument 'resourceNames' must be an array of strings.", McpErrorCode.InvalidParams);
            }

            if (resourceNamesElement.GetArrayLength() > MaximumResourceNameCount)
            {
                throw new McpProtocolException(
                    $"Argument 'resourceNames' must contain no more than {MaximumResourceNameCount} items.",
                    McpErrorCode.InvalidParams);
            }

            resourceNames = resourceNamesElement
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray();
            if (resourceNames.Any(static name => name.EnumerateRunes().Count() > MaximumResourceNameLength))
            {
                throw new McpProtocolException(
                    $"Each 'resourceNames' item must contain no more than {MaximumResourceNameLength} characters.",
                    McpErrorCode.InvalidParams);
            }
        }

        var targetState = ResourceWaitTarget.Healthy;
        if (arguments?.TryGetValue("targetState", out var targetStateElement) == true)
        {
            if (targetStateElement.ValueKind != JsonValueKind.String ||
                targetStateElement.GetString() is not ("healthy" or "up" or "down"))
            {
                throw new McpProtocolException(
                    "Argument 'targetState' must be one of 'healthy', 'up', or 'down'.",
                    McpErrorCode.InvalidParams);
            }

            targetState = targetStateElement.GetString() switch
            {
                "healthy" => ResourceWaitTarget.Healthy,
                "up" => ResourceWaitTarget.Up,
                "down" => ResourceWaitTarget.Down,
                _ => throw new UnreachableException()
            };
        }

        var timeoutSeconds = 120;
        if (arguments?.TryGetValue("timeoutSeconds", out var timeoutSecondsElement) == true)
        {
            if (timeoutSecondsElement.ValueKind != JsonValueKind.Number ||
                !timeoutSecondsElement.TryGetInt32(out timeoutSeconds) ||
                timeoutSeconds is < 1 or > 3600)
            {
                throw new McpProtocolException(
                    "Argument 'timeoutSeconds' must be an integer from 1 through 3600.",
                    McpErrorCode.InvalidParams);
            }
        }

        return new WaitForResourcesArguments(resourceNames, targetState, timeoutSeconds);
    }

    private static string? MapResourceState(string? state)
    {
        return state switch
        {
            null => null,
            "Active" or
            "Building" or
            "Exited" or
            "FailedToStart" or
            "Finished" or
            "NotStarted" or
            "Running" or
            "RuntimeUnhealthy" or
            "Starting" or
            "Stopping" or
            "ValueMissing" or
            "Waiting" => state,
            _ => "unknown"
        };
    }

    private static string GetOverallOutcome(IReadOnlyList<WaitForResourceResultJson> resources)
    {
        if (resources.Any(static resource => resource.Outcome == "failure"))
        {
            return "failure";
        }

        return resources.Any(static resource => resource.Outcome == "timeout")
            ? "timeout"
            : "success";
    }

    private static string GetOutcomeValue(ResourceWaitOutcome outcome)
    {
        return outcome switch
        {
            ResourceWaitOutcome.Success => "success",
            ResourceWaitOutcome.Timeout => "timeout",
            ResourceWaitOutcome.Failure => "failure",
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }

    private static string? GetError(ResourceWaitResult result)
    {
        return result.Outcome switch
        {
            ResourceWaitOutcome.Success => null,
            ResourceWaitOutcome.Timeout => "Timed out waiting for the target state.",
            ResourceWaitOutcome.Failure when result.ResourceNotFound => "Resource was not found while waiting.",
            ResourceWaitOutcome.Failure when ResourceWaitService.IsTerminalFailureState(result.State) => "Resource entered a terminal failed state.",
            ResourceWaitOutcome.Failure => "Resource wait failed.",
            _ => throw new ArgumentOutOfRangeException(nameof(result))
        };
    }

    private static ResolvedWaitTarget ResolveNamedResource(
        string resourceName,
        IReadOnlyList<ResourceSnapshot> snapshots)
    {
        // Runtime names are unique and take precedence when another resource uses the same
        // value as its display name.
        var runtimeMatch = snapshots.FirstOrDefault(snapshot =>
            string.Equals(snapshot.Name, resourceName, StringComparisons.ResourceName));
        if (runtimeMatch is not null)
        {
            return CreateNamedTarget(runtimeMatch, resourceName);
        }

        var displayNameMatches = snapshots
            .Where(snapshot => string.Equals(
                snapshot.DisplayName,
                resourceName,
                StringComparisons.ResourceName))
            .ToArray();

        return displayNameMatches.Length switch
        {
            1 => CreateNamedTarget(displayNameMatches[0], resourceName),
            > 1 => CreateUnavailableTarget(
                resourceName,
                "Display name is ambiguous; use an exact runtime name."),
            _ => CreateUnavailableTarget(
                resourceName,
                "Resource was not found in the selected AppHost.")
        };
    }

    private static ResolvedWaitTarget CreateNamedTarget(
        ResourceSnapshot snapshot,
        string requestedName)
    {
        if (ResourceSnapshotMapper.IsHiddenResource(snapshot))
        {
            return CreateUnavailableTarget(
                requestedName,
                "Resource is hidden and cannot be waited for through MCP.");
        }

        if (McpToolHelpers.IsExcludedFromMcp(snapshot))
        {
            return CreateUnavailableTarget(
                requestedName,
                "Resource is excluded from MCP.");
        }

        return new ResolvedWaitTarget(snapshot, null);
    }

    private static ResolvedWaitTarget CreateUnavailableTarget(
        string resourceName,
        string error)
    {
        return new ResolvedWaitTarget(
            null,
            new WaitForResourceResultJson(
                resourceName,
                State: null,
                Health: null,
                "failure",
                error));
    }

    private sealed record WaitForResourcesArguments(
        IReadOnlyList<string>? ResourceNames,
        ResourceWaitTarget TargetState,
        int TimeoutSeconds);

    private sealed record ResolvedWaitTarget(
        ResourceSnapshot? Resource,
        WaitForResourceResultJson? Failure);
}
