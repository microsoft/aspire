// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// RPC target for the auxiliary backchannel.
/// </summary>
internal sealed class AuxiliaryBackchannelRpcTarget(
    ILogger<AuxiliaryBackchannelRpcTarget> logger,
    IServiceProvider serviceProvider)
{
    private static readonly TimeSpan s_mcpDiscoveryTimeout = TimeSpan.FromSeconds(5);

    #region V2 API Methods

    /// <summary>
    /// Gets the capabilities supported by this auxiliary backchannel.
    /// </summary>
    /// <param name="request">The request (currently unused, for future expansion).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The capabilities response containing supported versions.</returns>
#pragma warning disable CA1822 // Mark members as static - RPC methods cannot be static
    public Task<GetCapabilitiesResponse> GetCapabilitiesAsync(GetCapabilitiesRequest? request = null, CancellationToken cancellationToken = default)
#pragma warning restore CA1822
    {
        _ = request;
        _ = cancellationToken;

        return Task.FromResult(new GetCapabilitiesResponse
        {
            Capabilities = [AuxiliaryBackchannelCapabilities.V1, AuxiliaryBackchannelCapabilities.V2]
        });
    }

    /// <summary>
    /// Gets AppHost information (v2 API with request object).
    /// </summary>
    /// <param name="request">The request (currently unused, for future expansion).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The AppHost information response.</returns>
    public async Task<GetAppHostInfoResponse> GetAppHostInfoAsync(GetAppHostInfoRequest? request = null, CancellationToken cancellationToken = default)
    {
        _ = request;

        var legacyInfo = await GetAppHostInformationAsync(cancellationToken).ConfigureAwait(false);

        return new GetAppHostInfoResponse
        {
            Pid = legacyInfo.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AspireHostVersion = typeof(AuxiliaryBackchannelRpcTarget).Assembly.GetName().Version?.ToString() ?? "unknown",
            AppHostPath = legacyInfo.AppHostPath,
            CliProcessId = legacyInfo.CliProcessId,
            StartedAt = legacyInfo.StartedAt
        };
    }

    /// <summary>
    /// Gets Dashboard information (v2 API with request object).
    /// </summary>
    /// <param name="request">The request (currently unused, for future expansion).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The Dashboard information response.</returns>
    public async Task<GetDashboardInfoResponse> GetDashboardInfoAsync(GetDashboardInfoRequest? request = null, CancellationToken cancellationToken = default)
    {
        _ = request;

        var info = await DashboardUrlsHelper.GetDashboardConnectionInfoAsync(serviceProvider, logger, cancellationToken).ConfigureAwait(false);

        var urls = new List<string>(2);
        if (!string.IsNullOrEmpty(info.BaseUrlWithLoginToken))
        {
            urls.Add(info.BaseUrlWithLoginToken);
        }
        if (!string.IsNullOrEmpty(info.CodespacesUrlWithLoginToken))
        {
            urls.Add(info.CodespacesUrlWithLoginToken);
        }

        return new GetDashboardInfoResponse
        {
            ApiBaseUrl = info.ApiBaseUrl,
            ApiToken = info.ApiToken,
            DashboardUrls = urls.ToArray(),
            IsHealthy = info.IsHealthy
        };
    }

    /// <summary>
    /// Gets resource snapshots (v2 API with request object).
    /// </summary>
    /// <param name="request">The request with optional filtering.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The resources response containing snapshots.</returns>
    public async Task<GetResourcesResponse> GetResourcesAsync(GetResourcesRequest? request = null, CancellationToken cancellationToken = default)
    {
        var snapshots = await GetResourceSnapshotsAsync(cancellationToken).ConfigureAwait(false);

        // Apply filter if specified
        if (!string.IsNullOrEmpty(request?.Filter))
        {
            var filter = request.Filter;
            snapshots = snapshots.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return new GetResourcesResponse
        {
            Resources = snapshots.ToArray()
        };
    }

    /// <summary>
    /// Watches for resource changes (v2 API with request object).
    /// </summary>
    /// <param name="request">The request with optional filtering.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of resource snapshots as they change.</returns>
    public async IAsyncEnumerable<ResourceSnapshot> WatchResourcesAsync(WatchResourcesRequest? request = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var filter = request?.Filter;

        await foreach (var snapshot in WatchResourceSnapshotsAsync(cancellationToken).ConfigureAwait(false))
        {
            // Apply filter if specified
            if (!string.IsNullOrEmpty(filter) && !snapshot.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return snapshot;
        }
    }

    /// <summary>
    /// Gets console logs (v2 API with request object).
    /// </summary>
    /// <param name="request">The request specifying resource and options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of log lines.</returns>
    public IAsyncEnumerable<ResourceLogLine> GetConsoleLogsAsync(GetConsoleLogsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetResourceLogsAsync(request.ResourceName, request.Follow, cancellationToken);
    }

    /// <summary>
    /// Calls an MCP tool on a resource (v2 API with request object).
    /// </summary>
    /// <param name="request">The request specifying resource, tool, and arguments.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The tool call response.</returns>
    public async Task<CallMcpToolResponse> CallMcpToolAsync(CallMcpToolRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Convert JsonElement arguments to Dictionary<string, object?> with proper value conversion
        var arguments = new Dictionary<string, object?>();
        if (request.Arguments is JsonElement argsElement && argsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in argsElement.EnumerateObject())
            {
                arguments[prop.Name] = ConvertJsonElementToObject(prop.Value);
            }
        }

        var result = await CallResourceMcpToolAsync(request.ResourceName, request.ToolName, arguments, cancellationToken).ConfigureAwait(false);

        return new CallMcpToolResponse
        {
            IsError = result.IsError ?? false,
            Content = result.Content.Select(c => new McpToolContentItem
            {
                Type = c.Type,
                Text = (c as ModelContextProtocol.Protocol.TextContentBlock)?.Text
            }).ToArray()
        };
    }

    /// <summary>
    /// Stops the AppHost (v2 API with request object).
    /// </summary>
    /// <param name="request">The request with optional exit code.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stop response.</returns>
    public async Task<StopAppHostResponse> StopAsync(StopAppHostRequest? request = null, CancellationToken cancellationToken = default)
    {
        _ = request; // Exit code not yet used, but available for future expansion
        await StopAppHostAsync(cancellationToken).ConfigureAwait(false);
        return new StopAppHostResponse();
    }

    /// <summary>
    /// Executes a command on a resource.
    /// </summary>
    /// <param name="request">The request containing resource name and command name.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The response indicating success or failure.</returns>
    public async Task<ExecuteResourceCommandResponse> ExecuteResourceCommandAsync(ExecuteResourceCommandRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resourceCommandService = serviceProvider.GetRequiredService<ResourceCommandService>();
        var result = await resourceCommandService.ExecuteCommandAsync(request.ResourceName, request.CommandName, cancellationToken).ConfigureAwait(false);

#pragma warning disable CS0618 // Type or member is obsolete
        var resolvedMessage = result.Message ?? result.ErrorMessage;
#pragma warning restore CS0618 // Type or member is obsolete

        return new ExecuteResourceCommandResponse
        {
            Success = result.Success,
            Canceled = result.Canceled,
#pragma warning disable CS0618 // Type or member is obsolete
            ErrorMessage = resolvedMessage,
#pragma warning restore CS0618 // Type or member is obsolete
            Message = resolvedMessage,
            Value = result.Data is { } v ? new ExecuteResourceCommandResult
            {
                Value = v.Value,
                Format = v.Format switch
                {
                    ApplicationModel.CommandResultFormat.Json => CommandResultFormat.Json,
                    ApplicationModel.CommandResultFormat.Markdown => CommandResultFormat.Markdown,
                    _ => CommandResultFormat.Text
                },
                DisplayImmediately = v.DisplayImmediately
            } : null
        };
    }

    /// <summary>
    /// Waits for a resource to reach a target status.
    /// </summary>
    public async Task<WaitForResourceResponse> WaitForResourceAsync(WaitForResourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var notificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();
        var targetResolution = ResolveWaitTarget(notificationService, request.ResourceName);
        var targetResource = targetResolution.Target;

        if (targetResource is null)
        {
            return new WaitForResourceResponse
            {
                Success = false,
                ResourceNotFound = targetResolution.ResourceNotFound,
                ErrorMessage = targetResolution.ErrorMessage
            };
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            return request.Status switch
            {
                "healthy" => await WaitForHealthyAsync(notificationService, targetResource, linkedCts.Token).ConfigureAwait(false),
                "up" => await WaitForRunningAsync(notificationService, targetResource, linkedCts.Token).ConfigureAwait(false),
                "down" => await WaitForTerminalAsync(notificationService, targetResource, linkedCts.Token).ConfigureAwait(false),
                _ => new WaitForResourceResponse { Success = false, ErrorMessage = $"Unknown status: {request.Status}" }
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new WaitForResourceResponse { Success = false, TimedOut = true, ErrorMessage = $"Timed out waiting for resource '{request.ResourceName}'." };
        }
        catch (DistributedApplicationException ex)
        {
            return new WaitForResourceResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    private static async Task<WaitForResourceResponse> WaitForHealthyAsync(ResourceNotificationService notificationService, WaitResourceTarget target, CancellationToken cancellationToken)
    {
        var resourceEvent = await WaitForResourceEventAsync(
            notificationService,
            target,
            re => ResourceNotificationService.ShouldYieldHealthyWait(WaitBehavior.StopOnResourceUnavailable, re.Snapshot),
            $"Resource '{target.DisplayName}' failed to become healthy before the operation was cancelled.",
            cancellationToken).ConfigureAwait(false);

        if (resourceEvent.Snapshot.HealthStatus != HealthStatus.Healthy)
        {
            throw new DistributedApplicationException($"Stopped waiting for resource '{target.DisplayName}' to become healthy because it failed to start.");
        }

        resourceEvent = await WaitForResourceEventAsync(
            notificationService,
            new WaitResourceTarget(target.DisplayName, resourceEvent.ResourceId, null),
            re => re.Snapshot.ResourceReadyEvent is not null,
            $"Resource '{target.DisplayName}' failed to execute the resource ready event before the operation was cancelled.",
            cancellationToken).ConfigureAwait(false);

        await resourceEvent.Snapshot.ResourceReadyEvent!.EventTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        return new WaitForResourceResponse
        {
            Success = true,
            State = resourceEvent.Snapshot.State?.Text,
            HealthStatus = resourceEvent.Snapshot.HealthStatus?.ToString()
        };
    }

    private static async Task<WaitForResourceResponse> WaitForRunningAsync(ResourceNotificationService notificationService, WaitResourceTarget target, CancellationToken cancellationToken)
    {
        var resourceEvent = await WaitForResourceEventAsync(
            notificationService,
            target,
            re => re.Snapshot.State?.Text == KnownResourceStates.Running || KnownResourceStates.TerminalStates.Contains(re.Snapshot.State?.Text) || re.Snapshot.ExitCode is not null,
            $"Resource '{target.DisplayName}' failed to reach the target state before the operation was cancelled.",
            cancellationToken).ConfigureAwait(false);

        var state = resourceEvent.Snapshot.State?.Text;
        var isRunning = state == KnownResourceStates.Running;

        return new WaitForResourceResponse
        {
            Success = isRunning,
            State = state,
            HealthStatus = resourceEvent.Snapshot.HealthStatus?.ToString(),
            ErrorMessage = isRunning ? null : $"Resource '{target.DisplayName}' failed to reach 'Running' state. Current state: {state ?? "Unknown"}."
        };
    }

    private static async Task<WaitForResourceResponse> WaitForTerminalAsync(ResourceNotificationService notificationService, WaitResourceTarget target, CancellationToken cancellationToken)
    {
        var resourceEvent = await WaitForResourceEventAsync(
            notificationService,
            target,
            re => KnownResourceStates.TerminalStates.Contains(re.Snapshot.State?.Text) || re.Snapshot.ExitCode is not null,
            $"Resource '{target.DisplayName}' failed to reach the target state before the operation was cancelled.",
            cancellationToken).ConfigureAwait(false);

        return new WaitForResourceResponse
        {
            Success = true,
            State = resourceEvent.Snapshot.State?.Text,
            HealthStatus = resourceEvent.Snapshot.HealthStatus?.ToString()
        };
    }

    private static async Task<ResourceEvent> WaitForResourceEventAsync(
        ResourceNotificationService notificationService,
        WaitResourceTarget target,
        Func<ResourceEvent, bool> predicate,
        string cancellationMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var resourceEvent in notificationService.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                if (target.Matches(resourceEvent) && predicate(resourceEvent))
                {
                    return resourceEvent;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new OperationCanceledException(cancellationMessage, ex, ex.CancellationToken);
        }

        throw new OperationCanceledException(cancellationMessage);
    }

    private WaitTargetResolutionResult ResolveWaitTarget(ResourceNotificationService notificationService, string requestedResourceName)
    {
        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        if (notificationService.TryGetCurrentState(requestedResourceName, out var resourceEvent))
        {
            return WaitTargetResolutionResult.Success(new WaitResourceTarget(
                ResolveDisplayName(appModel, requestedResourceName, resourceEvent.ResourceId),
                resourceEvent.ResourceId,
                null));
        }

        // During startup the resource may not have published its first snapshot yet, so fall back to
        // the app model to resolve the requested logical name or resolved resource id.
        var matchingResource = appModel.Resources.SingleOrDefault(resource => string.Equals(resource.Name, requestedResourceName, StringComparisons.ResourceName));
        if (matchingResource is not null)
        {
            var resolvedResourceNames = matchingResource.GetResolvedResourceNames();
            return resolvedResourceNames.Length switch
            {
                1 => WaitTargetResolutionResult.Success(new WaitResourceTarget(requestedResourceName, resolvedResourceNames[0], null)),
                > 1 => WaitTargetResolutionResult.Ambiguous(requestedResourceName),
                _ => WaitTargetResolutionResult.NotFound(requestedResourceName)
            };
        }

        var resolvedMatches = appModel.Resources
            .Select(resource => new { Resource = resource, ResolvedResourceNames = resource.GetResolvedResourceNames() })
            .Where(match => match.ResolvedResourceNames.Any(resourceName => string.Equals(resourceName, requestedResourceName, StringComparisons.ResourceName)))
            .Take(2)
            .ToArray();

        return resolvedMatches.Length switch
        {
            1 => WaitTargetResolutionResult.Success(new WaitResourceTarget(
                resolvedMatches[0].ResolvedResourceNames.Length == 1 ? resolvedMatches[0].Resource.Name : requestedResourceName,
                requestedResourceName,
                null)),
            > 1 => WaitTargetResolutionResult.Ambiguous(requestedResourceName),
            _ => WaitTargetResolutionResult.NotFound(requestedResourceName)
        };
    }

    private static string ResolveDisplayName(DistributedApplicationModel appModel, string requestedResourceName, string resolvedResourceName)
    {
        var matchingResource = appModel.Resources
            .Select(resource => new { Resource = resource, ResolvedResourceNames = resource.GetResolvedResourceNames() })
            .SingleOrDefault(match => match.ResolvedResourceNames.Any(resourceName => string.Equals(resourceName, resolvedResourceName, StringComparisons.ResourceName)));

        return matchingResource is { ResolvedResourceNames.Length: 1 }
            ? matchingResource.Resource.Name
            : requestedResourceName;
    }

    private sealed record WaitResourceTarget(string DisplayName, string? ResourceId, string? ResourceName)
    {
        public bool Matches(ResourceEvent resourceEvent)
        {
            return (ResourceId is not null && string.Equals(resourceEvent.ResourceId, ResourceId, StringComparisons.ResourceName))
                || (ResourceName is not null && string.Equals(resourceEvent.Resource.Name, ResourceName, StringComparisons.ResourceName));
        }
    }

    private sealed record WaitTargetResolutionResult(WaitResourceTarget? Target, bool ResourceNotFound, string ErrorMessage)
    {
        public static WaitTargetResolutionResult Success(WaitResourceTarget target) => new(target, ResourceNotFound: false, string.Empty);

        public static WaitTargetResolutionResult NotFound(string requestedResourceName) => new(
            null,
            ResourceNotFound: true,
            $"Resource '{requestedResourceName}' was not found.");

        public static WaitTargetResolutionResult Ambiguous(string requestedResourceName) => new(
            null,
            ResourceNotFound: false,
            $"Resource '{requestedResourceName}' is ambiguous because it has multiple replicas. Specify the exact instance name.");
    }

    #endregion

    #region V1 API Methods (Legacy - Keep for backward compatibility)

    /// <summary>
    /// Gets information about the AppHost for the MCP server.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The AppHost information including the fully qualified path and process ID.</returns>
    /// <exception cref="InvalidOperationException">Thrown when AppHost information is not available.</exception>
    public Task<AppHostInformation> GetAppHostInformationAsync(CancellationToken cancellationToken = default)
    {
        // The cancellationToken parameter is not currently used, but is retained for API consistency and potential future support for cancellation.
        _ = cancellationToken;

        var configuration = serviceProvider.GetService<IConfiguration>();
        if (configuration is null)
        {
            logger.LogError("Configuration not found.");
            throw new InvalidOperationException("Configuration not found.");
        }

        // First try to get the file path (with extension), otherwise fall back to the path (without extension)
        var appHostPath = configuration["AppHost:FilePath"] ?? configuration["AppHost:Path"];
        if (string.IsNullOrEmpty(appHostPath))
        {
            logger.LogError("AppHost path not found in configuration.");
            throw new InvalidOperationException("AppHost path not found in configuration.");
        }

        // Get the CLI process ID if the AppHost was launched via the CLI
        int? cliProcessId = null;
        var cliPidString = configuration[KnownConfigNames.CliProcessId];
        if (!string.IsNullOrEmpty(cliPidString) && int.TryParse(cliPidString, out var parsedCliPid))
        {
            cliProcessId = parsedCliPid;
        }
        DateTimeOffset? cliStartedAt = null;
        var cliStartedAtString = configuration[KnownConfigNames.CliProcessStarted];
        if (!string.IsNullOrEmpty(cliStartedAtString) && long.TryParse(cliStartedAtString, out var parsedCliStartedAt))
        {
            cliStartedAt = DateTimeOffset.FromUnixTimeSeconds(parsedCliStartedAt);
        }

        return Task.FromResult(new AppHostInformation
        {
            AppHostPath = appHostPath,
            ProcessId = Environment.ProcessId,
            CliProcessId = cliProcessId,
            StartedAt = new DateTimeOffset(Process.GetCurrentProcess().StartTime),
            CliStartedAt = cliStartedAt
        });
    }

    /// <summary>
    /// Gets the dashboard URLs for the running AppHost.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The dashboard URL state including health and resolved dashboard URLs.</returns>
    public async Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken = default)
    {
        logger.LogDebug("GetDashboardUrlsAsync called on auxiliary backchannel");
        return await DashboardUrlsHelper.GetDashboardUrlsAsync(serviceProvider, logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Preserved for backwards compatibility with older CLI versions that call this RPC method.
    /// Always returns <see langword="null"/> because the dashboard MCP server has been removed.
    /// </summary>
#pragma warning disable CA1822 // Mark members as static - RPC methods cannot be static
    public Task<DashboardMcpConnectionInfo?> GetDashboardMcpConnectionInfoAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult<DashboardMcpConnectionInfo?>(null);
    }
#pragma warning restore CA1822

    /// <summary>
    /// Gets the current resource snapshots for all resources.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A list of resource snapshots.</returns>
    public async Task<List<ResourceSnapshot>> GetResourceSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var notificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();
        var results = new List<ResourceSnapshot>();

        // Get current state for each resource directly using TryGetCurrentState
        foreach (var resource in appModel.Resources)
        {
            foreach (var instanceName in resource.GetResolvedResourceNames())
            {
                await AddResult(instanceName).ConfigureAwait(false);
            }
        }

        return results;

        async Task AddResult(string resourceName)
        {
            if (notificationService.TryGetCurrentState(resourceName, out var resourceEvent))
            {
                var snapshot = await CreateResourceSnapshotFromEventAsync(resourceEvent, cancellationToken).ConfigureAwait(false);
                if (snapshot is not null)
                {
                    results.Add(snapshot);
                }
            }
        }
    }

    /// <summary>
    /// Watches for resource snapshot changes and streams them to the client.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of resource snapshots as they change.</returns>
    public async IAsyncEnumerable<ResourceSnapshot> WatchResourceSnapshotsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var notificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();

        var resourceEvents = notificationService.WatchAsync(cancellationToken);

        await foreach (var resourceEvent in resourceEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = await CreateResourceSnapshotFromEventAsync(resourceEvent, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                yield return snapshot;
            }
        }
    }

    private async Task<ResourceSnapshot?> CreateResourceSnapshotFromEventAsync(
        ResourceEvent resourceEvent,
        CancellationToken cancellationToken)
    {
        var resource = resourceEvent.Resource;
        var snapshot = resourceEvent.Snapshot;

        // Get MCP server info if available
        ResourceSnapshotMcpServer? mcpServer = null;
        if (resource is IResourceWithEndpoints resourceWithEndpoints &&
            resourceWithEndpoints.TryGetLastAnnotation<McpServerEndpointAnnotation>(out var mcpAnnotation))
        {
            var endpointUri = await mcpAnnotation.EndpointUrlResolver(resourceWithEndpoints, cancellationToken).ConfigureAwait(false);
            if (endpointUri is not null)
            {
                var tools = await TryListToolsAsync(endpointUri, cancellationToken).ConfigureAwait(false);
                if (tools is not null)
                {
                    mcpServer = new ResourceSnapshotMcpServer
                    {
                        EndpointUrl = endpointUri.ToString(),
                        Tools = tools
                    };
                }
            }
        }

        // Build URLs
        var urls = snapshot.Urls
            .Where(u => !u.IsInactive && !string.IsNullOrEmpty(u.Url))
            .Select(u => new ResourceSnapshotUrl
            {
                Name = u.Name ?? "default",
                Url = u.Url,
                IsInternal = u.IsInternal,
                DisplayProperties = new ResourceSnapshotUrlDisplayProperties
                {
                    DisplayName = string.IsNullOrEmpty(u.DisplayProperties.DisplayName) ? null : u.DisplayProperties.DisplayName,
                    SortOrder = u.DisplayProperties.SortOrder
                }
            })
            .ToArray();

        // Build relationships
        var relationships = snapshot.Relationships
            .Select(r => new ResourceSnapshotRelationship
            {
                ResourceName = r.ResourceName,
                Type = r.Type
            })
            .ToArray();

        // Build health reports
        var healthReports = snapshot.HealthReports
            .Select(h => new ResourceSnapshotHealthReport
            {
                Name = h.Name,
                Status = h.Status?.ToString(),
                Description = h.Description,
                ExceptionText = h.ExceptionText
            })
            .ToArray();

        // Build volumes
        var volumes = snapshot.Volumes
            .Select(v => new ResourceSnapshotVolume
            {
                Source = v.Source,
                Target = v.Target,
                MountType = v.MountType,
                IsReadOnly = v.IsReadOnly
            })
            .ToArray();

        // Build environment variables
        var environmentVariables = snapshot.EnvironmentVariables
            .Select(e => new ResourceSnapshotEnvironmentVariable
            {
                Name = e.Name,
                Value = e.Value,
                IsFromSpec = e.IsFromSpec
            })
            .ToArray();

        // Build properties dictionary from ResourcePropertySnapshot
        // Redact sensitive property values to avoid leaking secrets
        var properties = new Dictionary<string, string?>();
        foreach (var prop in snapshot.Properties)
        {
            // Redact sensitive property values
            if (prop.IsSensitive)
            {
                properties[prop.Name] = null;
                continue;
            }

            // Convert value to string representation
            var stringValue = prop.Value switch
            {
                null => null,
                string s => s,
                IEnumerable<object> enumerable => string.Join(", ", enumerable),
                System.Collections.IEnumerable enumerable => string.Join(", ", enumerable.Cast<object>()),
                _ => prop.Value.ToString()
            };
            properties[prop.Name] = stringValue;
        }

        // Build commands
        var commands = snapshot.Commands
            .Select(c => new ResourceSnapshotCommand
            {
                Name = c.Name,
                DisplayName = c.DisplayName,
                Description = c.DisplayDescription,
                ConfirmationMessage = c.ConfirmationMessage,
                State = c.State.ToString()
            })
            .ToArray();

        return new ResourceSnapshot
        {
            Name = resourceEvent.ResourceId,
            DisplayName = resource.Name,
            ResourceType = snapshot.ResourceType,
            State = snapshot.State?.Text,
            StateStyle = snapshot.State?.Style,
            IsHidden = snapshot.IsHidden,
            HealthStatus = snapshot.HealthStatus?.ToString(),
            ExitCode = snapshot.ExitCode,
            CreatedAt = snapshot.CreationTimeStamp,
            StartedAt = snapshot.StartTimeStamp,
            StoppedAt = snapshot.StopTimeStamp,
            Urls = urls,
            Relationships = relationships,
            HealthReports = healthReports,
            Volumes = volumes,
            EnvironmentVariables = environmentVariables,
            Properties = properties,
            McpServer = mcpServer,
            Commands = commands
        };
    }

    /// <summary>
    /// Watches for resource log output and streams log lines to the client.
    /// </summary>
    /// <param name="resourceName">Optional resource name. If null, streams logs from all resources (only valid with follow=true).</param>
    /// <param name="follow">If true, continuously streams logs. If false, returns existing logs and completes.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of log lines.</returns>
    public async IAsyncEnumerable<ResourceLogLine> GetResourceLogsAsync(
        string? resourceName = null,
        bool follow = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var resourceLoggerService = serviceProvider.GetRequiredService<ResourceLoggerService>();
        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();

        // Step 1: Calculate the resource names
        var resourcesToLog = resourceName is not null
            ? ResolveResourceIds(appModel, resourceName)
            : ResolveAllResourceIds(appModel);

        if (resourceName is not null && resourcesToLog.Count == 0)
        {
            logger.LogWarning("Resource '{ResourceName}' not found. No logs will be returned.", resourceName);
            yield break;
        }

        if (resourcesToLog.Count == 0)
        {
            yield break;
        }

        // Step 2: Stream logs from all resources in parallel via a channel.
        var channel = Channel.CreateUnbounded<ResourceLogLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var tasks = new List<Task>();

        foreach (var name in resourcesToLog)
        {
            var task = Task.Run(async () =>
            {
                try
                {
                    var logStream = follow
                        ? resourceLoggerService.WatchAsync(name)
                        : resourceLoggerService.GetAllAsync(name);

                    await foreach (var batch in logStream.WithCancellation(cancellationToken).ConfigureAwait(false))
                    {
                        foreach (var logLine in batch)
                        {
                            await channel.Writer.WriteAsync(new ResourceLogLine
                            {
                                ResourceName = name,
                                LineNumber = logLine.LineNumber,
                                Content = logLine.Content,
                                IsError = logLine.IsErrorMessage
                            }, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error streaming logs for resource {ResourceName}", name);
                }
            }, cancellationToken);
            tasks.Add(task);
        }

        _ = Task.WhenAll(tasks).ContinueWith(_ => channel.Writer.Complete(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        await foreach (var logLine in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return logLine;
        }
    }

    /// <summary>
    /// Invokes a tool on the MCP server exposed by a resource annotated with <see cref="McpServerEndpointAnnotation"/>.
    /// </summary>
    /// <param name="resourceName">The resource name.</param>
    /// <param name="toolName">The tool name to invoke.</param>
    /// <param name="arguments">Tool arguments.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A JSON representation of the MCP <see cref="CallToolResult"/>.</returns>
    public async Task<CallToolResult> CallResourceMcpToolAsync(
        string resourceName,
        string toolName,
        Dictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();

        var resource = appModel.Resources
            .OfType<IResourceWithEndpoints>()
            .FirstOrDefault(r => string.Equals(r.Name, resourceName, StringComparisons.ResourceName));

        if (resource is null)
        {
            throw new InvalidOperationException($"Resource '{resourceName}' not found.");
        }

        if (!resource.TryGetLastAnnotation<McpServerEndpointAnnotation>(out var annotation))
        {
            throw new InvalidOperationException($"Resource '{resourceName}' does not have an MCP endpoint annotation.");
        }

        var endpointUri = await annotation.EndpointUrlResolver(resource, cancellationToken).ConfigureAwait(false);
        if (endpointUri is null)
        {
            throw new InvalidOperationException($"MCP endpoint for resource '{resourceName}' is not available.");
        }

        var transport = CreateHttpClientTransport(endpointUri);

        McpClient? mcpClient = null;
        try
        {
            mcpClient = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Failed to create MCP client.");

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Invoking tool {Name} with arguments {Arguments}", toolName, JsonSerializer.Serialize(arguments));
            }

            var result = await mcpClient.CallToolAsync(toolName, arguments, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Result: {Result}", JsonSerializer.Serialize(result));
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error invoking tool {ToolName} on resource {ResourceName}", toolName, resourceName);
            throw;
        }
        finally
        {
            if (mcpClient is not null)
            {
                await mcpClient.DisposeAsync().ConfigureAwait(false);
            }

            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Requests the AppHost to stop gracefully. The stop is initiated asynchronously in the background.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>
    /// A task that completes immediately after initiating the stop request. The actual stop occurs asynchronously.
    /// </returns>
    public Task StopAppHostAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken; // Unused but kept for API consistency
        logger.LogDebug("Received request to stop AppHost");

        // Start a background task to delay the stop by 500ms to allow the RPC response to be sent
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);

                // Cancel inflight RPC calls in AppHostRpcTarget before stopping
                var appHostRpcTarget = serviceProvider.GetService<AppHostRpcTarget>();
                appHostRpcTarget?.CancelInflightRpcCalls();

                var lifetime = serviceProvider.GetService<IHostApplicationLifetime>();
                if (lifetime is not null)
                {
                    logger.LogDebug("Stopping AppHost application");
                    lifetime.StopApplication();
                }
                else
                {
                    logger.LogWarning("IHostApplicationLifetime not found, cannot stop AppHost");
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while stopping AppHost");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    private async Task<Tool[]?> TryListToolsAsync(Uri endpointUri, CancellationToken cancellationToken)
    {
        var transport = CreateHttpClientTransport(endpointUri);

        using var timeoutCts = new CancellationTokenSource(s_mcpDiscoveryTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            var mcpClient = await McpClient.CreateAsync(transport, cancellationToken: linked.Token).ConfigureAwait(false);
            try
            {
                var toolsList = await mcpClient.ListToolsAsync(cancellationToken: linked.Token).ConfigureAwait(false);

                return toolsList.Select(c => c.ProtocolTool).ToArray();
            }
            finally
            {
                await mcpClient.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to list tools from MCP endpoint {EndpointUri}", endpointUri);
            return null;
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    private HttpClientTransport CreateHttpClientTransport(Uri endpointUri)
    {
        var httpClientFactory = serviceProvider.GetService<IHttpClientFactory>();
        var httpClient = httpClientFactory?.CreateClient() ?? new HttpClient();

        return new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = endpointUri },
            httpClient,
            serviceProvider.GetRequiredService<ILoggerFactory>(),
            ownsHttpClient: true);
    }

    #endregion

    /// <summary>
    /// Streams AppHost log entries from the hosting process.
    /// Delegates to <see cref="AppHostRpcTarget.GetAppHostLogEntriesAsync"/>.
    /// </summary>
    public IAsyncEnumerable<BackchannelLogEntry> GetAppHostLogEntriesAsync(CancellationToken cancellationToken = default)
    {
        var rpcTarget = serviceProvider.GetRequiredService<AppHostRpcTarget>();
        return rpcTarget.GetAppHostLogEntriesAsync(cancellationToken);
    }

    /// <summary>
    /// Converts a JsonElement to its underlying CLR type for proper serialization.
    /// </summary>
    private static object? ConvertJsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => ConvertJsonNumber(element),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElementToObject).ToArray(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElementToObject(p.Value)),
            _ => element.Clone()
        };
    }

    private static object ConvertJsonNumber(JsonElement element)
    {
        // Try integer types first
        if (element.TryGetInt32(out var i32))
        {
            return i32;
        }

        if (element.TryGetInt64(out var i64))
        {
            return i64;
        }

        // Fall back to double for floating point
        return element.GetDouble();
    }

    /// <summary>
    /// Resolves a resource name (logical or resolved instance name) to the list of matching resource IDs.
    /// Matches by logical name first (returning all instances), then falls back to matching a specific
    /// resolved instance name like "apiservice-abc123".
    /// </summary>
    private static List<string> ResolveResourceIds(DistributedApplicationModel appModel, string resourceName)
    {
        foreach (var resource in appModel.Resources)
        {
            var resolvedNames = resource.GetResolvedResourceNames();

            if (string.Equals(resource.Name, resourceName, StringComparisons.ResourceName))
            {
                return [.. resolvedNames];
            }

            var matchedInstance = resolvedNames.FirstOrDefault(n => string.Equals(n, resourceName, StringComparisons.ResourceName));
            if (matchedInstance is not null)
            {
                return [matchedInstance];
            }
        }

        return [];
    }

    /// <summary>
    /// Returns the resolved resource IDs for all resources in the application model.
    /// </summary>
    private static List<string> ResolveAllResourceIds(DistributedApplicationModel appModel)
    {
        var result = new List<string>();
        foreach (var resource in appModel.Resources)
        {
            result.AddRange(resource.GetResolvedResourceNames());
        }
        return result;
    }
}
