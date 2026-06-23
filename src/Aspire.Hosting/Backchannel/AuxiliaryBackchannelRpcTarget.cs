// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Diagnostics;
using Aspire.Shared;
using Aspire.Shared.ConsoleLogs;
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
    IConfiguration configuration,
    ProfilingTelemetry profilingTelemetry,
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
        _ = cancellationToken;
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetCapabilitiesAsync), streaming: false, request?.TraceContext);

        return Task.FromResult(new GetCapabilitiesResponse
        {
            Capabilities = [AuxiliaryBackchannelCapabilities.V1, AuxiliaryBackchannelCapabilities.V2, AuxiliaryBackchannelCapabilities.V3, AuxiliaryBackchannelCapabilities.Terminals_V1]
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
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetAppHostInfoAsync), streaming: false, request?.TraceContext);

        var legacyInfo = await GetAppHostInformationAsync(cancellationToken).ConfigureAwait(false);

        return new GetAppHostInfoResponse
        {
            Pid = legacyInfo.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            AspireHostVersion = AssemblyVersionHelper.GetDisplayVersion(typeof(AuxiliaryBackchannelRpcTarget).Assembly) ?? "unknown",
            AppHostPath = legacyInfo.AppHostPath,
            CliProcessId = legacyInfo.CliProcessId,
            StartedAt = legacyInfo.StartedAt,
            CliLogFilePath = legacyInfo.CliLogFilePath
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
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetDashboardInfoAsync), streaming: false, request?.TraceContext);

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
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetResourcesAsync), streaming: false, request?.TraceContext);
        var snapshots = await GetResourceSnapshotsAsync(SupportsJsonResourceProperties(request?.ClientCapabilities), cancellationToken).ConfigureAwait(false);

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
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(WatchResourcesAsync), streaming: true, request?.TraceContext);
        var filter = request?.Filter;
        var yieldedCount = 0;

        try
        {
            await foreach (var snapshot in WatchResourceSnapshotsAsync(SupportsJsonResourceProperties(request?.ClientCapabilities), cancellationToken).ConfigureAwait(false))
            {
                // Apply filter if specified
                if (!string.IsNullOrEmpty(filter) && !snapshot.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (yieldedCount == 0)
                {
                    activity.AddJsonRpcStreamFirstItemEvent();
                }

                yieldedCount++;
                yield return snapshot;
            }

            activity.AddJsonRpcStreamCompletedEvent();
        }
        finally
        {
            activity.SetJsonRpcStreamItemCount(yieldedCount);
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
        return GetResourceLogsAsync(
            "GetConsoleLogsAsync",
            request.TraceContext,
            request.ResourceName,
            request.Follow,
            request.IncludeHidden,
            request.Search,
            request.Tail,
            cancellationToken);
    }

    /// <summary>
    /// Gets console logs in batches to reduce per-item JSON-RPC stream overhead.
    /// </summary>
    /// <param name="request">The request specifying resource and options.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>An async enumerable of log batches.</returns>
    public IAsyncEnumerable<ResourceLogBatch> GetConsoleLogBatchesAsync(GetConsoleLogsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GetResourceLogBatchesAsync(
            "GetConsoleLogBatchesAsync",
            request.TraceContext,
            request.ResourceName,
            request.Follow,
            request.IncludeHidden,
            request.Search,
            request.Tail,
            cancellationToken);
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
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(CallMcpToolAsync), streaming: false, request.TraceContext);

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
    /// <param name="request">The stop request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The stop response.</returns>
    public async Task<StopAppHostResponse> StopAsync(StopAppHostRequest? request = null, CancellationToken cancellationToken = default)
    {
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(StopAsync), streaming: false, request?.TraceContext);
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
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(ExecuteResourceCommandAsync), streaming: false, request.TraceContext);

        var resourceCommandService = serviceProvider.GetRequiredService<ResourceCommandService>();
        var (arguments, argumentErrorMessage) = CreateCommandArguments(resourceCommandService, request);
        if (argumentErrorMessage is not null)
        {
            return new ExecuteResourceCommandResponse
            {
                Success = false,
                Message = argumentErrorMessage,
#pragma warning disable CS0618 // Type or member is obsolete
                ErrorMessage = argumentErrorMessage,
#pragma warning restore CS0618 // Type or member is obsolete
            };
        }

        ExecuteCommandResult result;
        InteractionInputCollection? loadedArguments = null;
        if (request.ValidateOnly)
        {
            (result, loadedArguments) = await ValidateResourceCommandAsync(resourceCommandService, request.ResourceName, request.CommandName, arguments, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            result = await resourceCommandService.ExecuteCommandAsync(
                request.ResourceName,
                request.CommandName,
                new ResourceCommandExecutionOptions
                {
                    Arguments = arguments,
                    ArgumentsProvided = request.Arguments is not null,
                    NonInteractive = request.NonInteractive
                },
                cancellationToken).ConfigureAwait(false);
        }

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
            ValidationErrors = CreateValidationErrors(result.InvalidArguments),
            ArgumentInputs = request.ReturnArgumentInputs && loadedArguments is not null
                ? loadedArguments.Select(CreateCommandArgument).ToArray()
                : null,
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

    private static (InteractionInputCollection Arguments, string? ErrorMessage) CreateCommandArguments(ResourceCommandService resourceCommandService, ExecuteResourceCommandRequest request)
    {
        var arguments = request.Arguments;
        if (arguments is null)
        {
            return resourceCommandService.CreateCommandArguments(request.ResourceName, request.CommandName, argumentValues: null);
        }

        return arguments.GetValueKind() switch
        {
            JsonValueKind.Object => resourceCommandService.CreateCommandArguments(request.ResourceName, request.CommandName, ConvertObjectArgumentValues(arguments.AsObject())),
            JsonValueKind.Array => resourceCommandService.CreateCommandArguments(request.ResourceName, request.CommandName, ConvertOrderedArgumentValues(arguments.AsArray())),
            _ => throw new InvalidOperationException("Resource command arguments must be a JSON object or array.")
        };
    }

    private static async Task<(ExecuteCommandResult Result, InteractionInputCollection? Arguments)> ValidateResourceCommandAsync(ResourceCommandService resourceCommandService, string resourceName, string commandName, InteractionInputCollection arguments, CancellationToken cancellationToken)
    {
        return await resourceCommandService.ValidateCommandArgumentsAsync(resourceName, commandName, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static ResourceCommandArgumentValidationError[] CreateValidationErrors(InteractionInputCollection? invalidArguments)
    {
        return invalidArguments is null
            ? []
            : invalidArguments
                .SelectMany(argument => argument.ValidationErrors.Select(error => new ResourceCommandArgumentValidationError
                {
                    ArgumentName = argument.Name,
                    ErrorMessage = error
                }))
                .ToArray();
    }

    private static IReadOnlyDictionary<string, string?> ConvertObjectArgumentValues(JsonObject arguments)
    {
        // ExecuteResourceCommandRequest arguments are encoded as a JSON object in the auxiliary backchannel protocol:
        // {
        //   "resourceName": "web-browser-logs",
        //   "commandName": "click",
        //   "arguments": { "selector": "#submit" }
        // }
        var values = new Dictionary<string, string?>(StringComparers.InteractionInputName);
        foreach (var property in arguments)
        {
            values[property.Key] = ConvertArgumentValue(property.Key, property.Value);
        }

        return values;
    }

    private static string?[] ConvertOrderedArgumentValues(JsonArray arguments)
    {
        // ExecuteResourceCommandRequest arguments can be encoded as a JSON array in the auxiliary backchannel protocol:
        // {
        //   "resourceName": "web-browser-logs",
        //   "commandName": "click",
        //   "arguments": [ "#submit" ]
        // }
        return arguments
            .Select((value, index) => ConvertArgumentValue($"[{index}]", value))
            .ToArray();
    }

    private static string? ConvertArgumentValue(string name, JsonNode? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.Number => value.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new InvalidOperationException($"Resource command argument '{name}' must be a string, number, boolean, or null.")
        };
    }

    /// <summary>
    /// Returns the discovery info needed to attach to a resource's terminal session(s). For a
    /// resource configured with <c>WithTerminal()</c>, this enumerates the per-replica
    /// consumer-side UDS endpoints by asking each per-replica terminal host process over its
    /// control UDS in parallel. Returns <see cref="GetTerminalInfoResponse.IsAvailable"/> = false
    /// when the resource has no <see cref="TerminalAnnotation"/>; per-host control RPC failures
    /// (e.g. host not yet started) degrade to <see cref="TerminalReplicaInfo.IsAlive"/> = false
    /// for that replica without failing the whole call.
    /// </summary>
    public async Task<GetTerminalInfoResponse> GetTerminalInfoAsync(GetTerminalInfoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var resource = appModel.Resources.FirstOrDefault(r => string.Equals(r.Name, request.ResourceName, StringComparisons.ResourceName));

        if (resource is null)
        {
            logger.LogDebug("GetTerminalInfo: resource '{ResourceName}' not found.", request.ResourceName);
            return new GetTerminalInfoResponse { IsAvailable = false };
        }

        var terminalAnnotation = resource.Annotations.OfType<TerminalAnnotation>().FirstOrDefault();
        if (terminalAnnotation is null)
        {
            logger.LogDebug("GetTerminalInfo: resource '{ResourceName}' has no TerminalAnnotation.", request.ResourceName);
            return new GetTerminalInfoResponse { IsAvailable = false };
        }

        var (replicas, _) = await CollectReplicaInfosAsync(
            request.ResourceName,
            terminalAnnotation,
            cancellationToken).ConfigureAwait(false);

        return new GetTerminalInfoResponse
        {
            IsAvailable = true,
            Replicas = replicas,
            Columns = terminalAnnotation.Options.Columns,
            Rows = terminalAnnotation.Options.Rows,
        };
    }

    /// <summary>
    /// Fans out across each per-replica terminal host's control UDS in parallel and returns
    /// the assembled per-replica info array plus an aggregate flag indicating whether at
    /// least one host responded. Per-host failures (host not yet started, control RPC timeout)
    /// materialize as a <see cref="TerminalReplicaInfo"/> with the AppHost-known consumer UDS
    /// path and <see cref="TerminalReplicaInfo.IsAlive"/> = false, so callers can always render
    /// a row per replica even when a host hasn't started.
    /// </summary>
    private async Task<(TerminalReplicaInfo[] Replicas, bool AnyHostReachable)> CollectReplicaInfosAsync(
        string resourceName,
        TerminalAnnotation terminalAnnotation,
        CancellationToken cancellationToken)
    {
        var hosts = terminalAnnotation.TerminalHosts;
        var tasks = new Task<(TerminalReplicaInfo Info, bool HostResponded)>[hosts.Count];
        for (var i = 0; i < hosts.Count; i++)
        {
            tasks[i] = QueryReplicaAsync(resourceName, hosts[i], cancellationToken);
        }

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var replicas = new TerminalReplicaInfo[results.Length];
        var anyResponded = false;
        for (var i = 0; i < results.Length; i++)
        {
            replicas[i] = results[i].Info;
            anyResponded |= results[i].HostResponded;
        }
        return (replicas, anyResponded);
    }

    /// <summary>
    /// Queries a single per-replica host's control UDS and translates the response (or the
    /// failure) into a <see cref="TerminalReplicaInfo"/>. The AppHost is the source of truth
    /// for the consumer UDS path and replica index — those come from <see cref="TerminalHostResource.Layout"/>
    /// rather than from the host's echoed reply. The returned <c>HostResponded</c> flag is true
    /// only when the control RPC actually succeeded.
    /// </summary>
    private async Task<(TerminalReplicaInfo Info, bool HostResponded)> QueryReplicaAsync(
        string resourceName,
        TerminalHostResource host,
        CancellationToken cancellationToken)
    {
        var layout = host.Layout;
        var replicaIndex = layout.ParentReplicaIndex;

        Aspire.Shared.TerminalHost.TerminalHostSessionInfo? session = null;
        try
        {
            session = await TerminalHostControlClient.GetSessionAsync(
                layout.ControlUdsPath,
                totalTimeout: TimeSpan.FromSeconds(3),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(
                "Terminal host control RPC timed out for resource '{ResourceName}' replica {ReplicaIndex} (control path '{ControlPath}').",
                resourceName, replicaIndex, layout.ControlUdsPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(
                ex,
                "Terminal host control RPC failed for resource '{ResourceName}' replica {ReplicaIndex} (control path '{ControlPath}').",
                resourceName, replicaIndex, layout.ControlUdsPath);
        }

        var info = new TerminalReplicaInfo
        {
            ReplicaIndex = replicaIndex,
            Label = $"replica {replicaIndex}",
            ConsumerUdsPath = layout.ConsumerUdsPath,
            IsAlive = session?.IsAlive ?? false,
            ExitCode = session?.ExitCode,
            ProducerConnected = session?.ProducerConnected ?? false,
            RestartCount = session?.RestartCount ?? 0,
            CurrentColumns = session?.CurrentColumns,
            CurrentRows = session?.CurrentRows,
            AttachedPeerCount = session?.AttachedPeerCount,
            Peers = ConvertPeers(session?.Peers),
        };
        return (info, session is not null);
    }

    /// <summary>
    /// Translates a host-side <see cref="Aspire.Shared.TerminalHost.TerminalHostPeerInfo"/> array
    /// to the wire-facing <see cref="TerminalPeerInfo"/> array that ships over JsonRpc. Returns
    /// null when the host didn't supply any peer info (older host) so newer clients can detect
    /// "no info available" vs. "info available, currently empty".
    /// </summary>
    private static TerminalPeerInfo[]? ConvertPeers(Aspire.Shared.TerminalHost.TerminalHostPeerInfo[]? hostPeers)
    {
        if (hostPeers is null)
        {
            return null;
        }
        if (hostPeers.Length == 0)
        {
            return Array.Empty<TerminalPeerInfo>();
        }
        var result = new TerminalPeerInfo[hostPeers.Length];
        for (var i = 0; i < hostPeers.Length; i++)
        {
            result[i] = new TerminalPeerInfo
            {
                PeerId = hostPeers[i].PeerId,
                DisplayName = hostPeers[i].DisplayName,
            };
        }
        return result;
    }

    /// <summary>
    /// Lists every <c>WithTerminal</c>-enabled resource in the AppHost, with current grid size and
    /// attached-peer details. Used by <c>aspire terminal ps</c>. Each per-resource snapshot is
    /// independent: a resource whose terminal host hasn't started yet (or whose control RPC times
    /// out) is reported with <see cref="TerminalSummary.IsHostReachable"/> = false rather than
    /// failing the whole listing.
    /// </summary>
    public async Task<ListTerminalsResponse> ListTerminalsAsync(ListTerminalsRequest? request = null, CancellationToken cancellationToken = default)
    {
        _ = request;

        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();

        var terminals = new List<TerminalSummary>();
        foreach (var resource in appModel.Resources)
        {
            var terminalAnnotation = resource.Annotations.OfType<TerminalAnnotation>().FirstOrDefault();
            if (terminalAnnotation is null)
            {
                continue;
            }

            var (replicas, anyHostReachable) = await CollectReplicaInfosAsync(
                resource.Name,
                terminalAnnotation,
                cancellationToken).ConfigureAwait(false);

            terminals.Add(new TerminalSummary
            {
                ResourceName = resource.Name,
                DisplayName = resource.Name,
                ConfiguredColumns = terminalAnnotation.Options.Columns,
                ConfiguredRows = terminalAnnotation.Options.Rows,
                IsHostReachable = anyHostReachable,
                // Always emit the per-replica array, even when no host is reachable.
                // CollectReplicaInfosAsync returns one entry per replica with AppHost-known
                // ReplicaIndex / ConsumerUdsPath populated and IsAlive=false on degraded
                // entries. Dropping the array on aggregate failure would make `aspire
                // terminal ps` blanker in the failure case than in the success case —
                // exactly when users need the diagnostic shape — and would diverge from
                // GetTerminalInfoAsync, which keeps the degraded per-replica shape.
                Replicas = replicas,
            });
        }

        return new ListTerminalsResponse
        {
            Terminals = [.. terminals],
        };
    }

    /// <summary>
    /// Waits for a resource to reach a target status.
    /// </summary>
    public async Task<WaitForResourceResponse> WaitForResourceAsync(WaitForResourceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(WaitForResourceAsync), streaming: false, request.TraceContext);

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
            re => re.Snapshot.State?.Text == KnownResourceStates.Running ||
                KnownResourceStates.TerminalStates.Contains(re.Snapshot.State?.Text, StringComparers.ResourceState) ||
                string.Equals(re.Snapshot.State?.Style, KnownResourceStateStyles.Error, StringComparisons.ResourceState) ||
                re.Snapshot.ExitCode is not null,
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
            CliStartedAt = cliStartedAt,
            CliLogFilePath = configuration[KnownConfigNames.CliLogFilePath]
        });
    }

    /// <summary>
    /// Gets the dashboard URLs for the running AppHost.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The dashboard URL state including health and resolved dashboard URLs.</returns>
    public async Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken = default)
    {
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(GetDashboardUrlsAsync), streaming: false);
        logger.LogDebug("GetDashboardUrlsAsync called on auxiliary backchannel");
        try
        {
            var urls = await DashboardUrlsHelper.GetDashboardUrlsAsync(serviceProvider, logger, cancellationToken).ConfigureAwait(false);
            activity.SetDashboardHealthy(urls.DashboardHealthy);
            return urls;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    /// <summary>
    /// Waits until the AppHost has reached its startup readiness point.
    /// </summary>
    /// <param name="request">The request (currently unused, for future expansion).</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The startup readiness state.</returns>
    public async Task<WaitForAppHostReadyResponse> WaitForAppHostReadyAsync(WaitForAppHostReadyRequest? request = null, CancellationToken cancellationToken = default)
    {
        using var activity = profilingTelemetry.StartJsonRpcServerCall(nameof(WaitForAppHostReadyAsync), streaming: false, request?.TraceContext);

        var startupState = serviceProvider.GetRequiredService<AppHostStartupState>();
        await startupState.WaitForReadyAsync(cancellationToken).ConfigureAwait(false);
        return new WaitForAppHostReadyResponse { IsReady = true };
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
        return await GetResourceSnapshotsAsync(resourcePropertiesAsJson: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ResourceSnapshot>> GetResourceSnapshotsAsync(bool resourcePropertiesAsJson, CancellationToken cancellationToken)
    {
        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var notificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();
        var results = new List<ResourceSnapshot>();

        // This is a point-in-time batch, so the set of resolved secret values is identical for
        // every resource. Compute it once here rather than once per resource.
        var secretParameterValues = GetResolvedSecretParameterValues();

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
                var snapshot = await CreateResourceSnapshotFromEventAsync(resourceEvent, resourcePropertiesAsJson, secretParameterValues, cancellationToken).ConfigureAwait(false);
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
        await foreach (var snapshot in WatchResourceSnapshotsAsync(resourcePropertiesAsJson: false, cancellationToken).ConfigureAwait(false))
        {
            yield return snapshot;
        }
    }

    private async IAsyncEnumerable<ResourceSnapshot> WatchResourceSnapshotsAsync(
        bool resourcePropertiesAsJson,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var notificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();

        var resourceEvents = notificationService.WatchAsync(cancellationToken);

        await foreach (var resourceEvent in resourceEvents.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Recompute the resolved secret values for every event. Secrets can be resolved between
            // events (e.g. interactive parameter entry after the watch starts), so caching the set
            // once outside the loop would let a value that becomes secret later bypass redaction.
            var secretParameterValues = GetResolvedSecretParameterValues();
            var snapshot = await CreateResourceSnapshotFromEventAsync(resourceEvent, resourcePropertiesAsJson, secretParameterValues, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                yield return snapshot;
            }
        }
    }

    private async Task<ResourceSnapshot?> CreateResourceSnapshotFromEventAsync(
        ResourceEvent resourceEvent,
        bool resourcePropertiesAsJson,
        HashSet<string> secretParameterValues,
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

        // Build environment variables. Values that match a secret parameter's value are
        // redacted so secrets don't leak through clients (e.g. aspire describe --format json).
        // The secret values are computed by the caller: once per batch for the one-shot
        // GetResourceSnapshotsAsync, but per event for the streaming WatchResourceSnapshotsAsync
        // so that secrets resolved mid-stream are still redacted.
        var environmentVariables = snapshot.EnvironmentVariables
            .Select(e => new ResourceSnapshotEnvironmentVariable
            {
                Name = e.Name,
                Value = RedactIfSecretValue(e.Value, secretParameterValues),
                IsFromSpec = e.IsFromSpec
            })
            .ToArray();

        // Build properties dictionary from ResourcePropertySnapshot
        // Redact sensitive property values to avoid leaking secrets
        //
        // Stamp the per-replica terminal properties (terminal.enabled, terminal.replicaIndex,
        // terminal.replicaCount) the same way the dashboard gRPC path does so that `aspire describe`
        // and the VS Code extension can detect terminal availability and target the right replica.
        // The sensitive terminal.consumerUdsPath is redacted to null below by the IsSensitive check,
        // which is intentional: the CLI resolves the real socket path via GetTerminalInfoAsync.
        var stampedProperties = TerminalResourceSnapshotProperties.AddTerminalProperties(resource, resourceEvent.ResourceId, snapshot.Properties);
        var properties = new Dictionary<string, JsonNode?>();
        string[]? waitingFor = null;
        foreach (var prop in stampedProperties)
        {
            // Redact sensitive property values
            if (prop.IsSensitive)
            {
                properties[prop.Name] = null;
                continue;
            }

            properties[prop.Name] = resourcePropertiesAsJson
                ? ConvertPropertyValueToJsonNode(prop.Value)
                : ConvertPropertyValueToLegacyJsonNode(prop.Value);

            if (string.Equals(prop.Name, KnownProperties.Resource.WaitingFor, StringComparisons.ResourcePropertyName))
            {
                waitingFor = GetStringArrayPropertyValue(prop.Value);
            }
        }

        // Build commands
        var commands = snapshot.Commands
            .Select(c => new ResourceSnapshotCommand
            {
                Name = c.Name,
                DisplayName = c.DisplayName,
                Description = c.DisplayDescription,
                ArgumentInputs = c.Arguments.Select(CreateCommandArgument).ToArray(),
                Visibility = c.Visibility.ToString(),
                State = c.State.ToString()
            })
            .ToArray();

        return new ResourceSnapshot
        {
            Name = resourceEvent.ResourceId,
            DisplayName = resource.Name,
            ResourceType = snapshot.ResourceType,
            State = snapshot.State?.Text,
            WaitingFor = waitingFor,
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
    /// Redacts an environment variable value when it matches a secret parameter's resolved value
    /// so secrets don't leak through clients (e.g. <c>aspire describe --format json</c>).
    /// </summary>
    /// <remarks>
    /// Matching is value-based: a value is redacted only when it is exactly equal to a resolved secret
    /// parameter value. This has two known limitations:
    /// <list type="bullet">
    /// <item>A non-secret value that coincidentally equals a secret value is also redacted (false positive).</item>
    /// <item>A secret embedded as a substring of a larger composed value (e.g. a connection string) is not
    /// detected, because only exact-equality matches are redacted.</item>
    /// </list>
    /// </remarks>
    private static string? RedactIfSecretValue(string? value, HashSet<string> secretParameterValues)
        => value is not null && secretParameterValues.Contains(value) ? null : value;

    /// <summary>
    /// Collects the resolved values of secret parameters in the application model so they can
    /// be redacted from data sent to clients. Only values that have already been resolved are
    /// returned; this never blocks waiting for interactive parameter resolution.
    /// </summary>
    private HashSet<string> GetResolvedSecretParameterValues()
    {
        var secretValues = new HashSet<string>(StringComparer.Ordinal);

        if (serviceProvider.GetService<DistributedApplicationModel>() is not { } appModel)
        {
            return secretValues;
        }

        foreach (var parameter in appModel.Resources.OfType<ParameterResource>())
        {
            if (!parameter.Secret)
            {
                continue;
            }

            if (parameter.WaitForValueTcs is { } waitForValueTcs)
            {
                // Run mode: peek at the resolved value without waiting for resolution.
                if (waitForValueTcs.Task is { IsCompletedSuccessfully: true } valueTask &&
                    valueTask.Result is { Length: > 0 } value)
                {
                    secretValues.Add(value);
                }
            }
            else
            {
                try
                {
                    if (parameter.ValueInternal is { Length: > 0 } value)
                    {
                        secretValues.Add(value);
                    }
                }
                catch
                {
                    // The parameter's value isn't available (e.g. missing configuration); nothing to redact.
                }
            }
        }

        return secretValues;
    }

    private static ResourceSnapshotCommandArgument CreateCommandArgument(InteractionInput input)
    {
        return new ResourceSnapshotCommandArgument
        {
            Name = input.Name,
            Label = input.Label,
            Description = input.Description,
            EnableDescriptionMarkdown = input.EnableDescriptionMarkdown,
            InputType = input.InputType.ToString(),
            Required = input.Required,
            Placeholder = input.Placeholder,
            Value = input.Value,
            Options = CreateOptionsDictionary(input.Options),
            AllowCustomChoice = input.AllowCustomChoice,
            Disabled = input.Disabled,
            MaxLength = input.MaxLength,
            DynamicLoading = CreateDynamicLoadingMetadata(input.DynamicLoading)
        };
    }

    private static JsonNode? ConvertPropertyValueToJsonNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode jsonNode => jsonNode.DeepClone(),
            string stringValue => JsonValue.Create(stringValue),
            bool boolValue => JsonValue.Create(boolValue),
            byte byteValue => JsonValue.Create(byteValue),
            sbyte sbyteValue => JsonValue.Create(sbyteValue),
            short shortValue => JsonValue.Create(shortValue),
            ushort ushortValue => JsonValue.Create(ushortValue),
            int intValue => JsonValue.Create(intValue),
            uint uintValue => JsonValue.Create(uintValue),
            long longValue => JsonValue.Create(longValue),
            ulong ulongValue => JsonValue.Create(ulongValue),
            float floatValue => JsonValue.Create(floatValue),
            double doubleValue => JsonValue.Create(doubleValue),
            decimal decimalValue => JsonValue.Create(decimalValue),
            System.Collections.IEnumerable enumerable => ConvertEnumerablePropertyValueToJsonArray(enumerable),
            _ => JsonValue.Create(value.ToString())
        };
    }

    private static JsonNode? ConvertPropertyValueToLegacyJsonNode(object? value)
    {
        var stringValue = ConvertPropertyValueToString(value);

        return stringValue is null ? null : JsonValue.Create(stringValue);
    }

    private static string? ConvertPropertyValueToString(object? value)
    {
        return value switch
        {
            null => null,
            JsonValue jsonValue when jsonValue.TryGetValue<string>(out var stringValue) => stringValue,
            JsonValue jsonValue when jsonValue.TryGetValue<bool>(out var boolValue) => boolValue.ToString(CultureInfo.InvariantCulture),
            JsonValue jsonValue when jsonValue.TryGetValue<IFormattable>(out var formattableValue) => formattableValue.ToString(null, CultureInfo.InvariantCulture),
            JsonNode jsonNode => jsonNode.ToJsonString(),
            string stringValue => stringValue,
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            System.Collections.IEnumerable enumerable => ConvertEnumerablePropertyValueToString(enumerable),
            _ => value.ToString()
        };
    }

    private static string ConvertEnumerablePropertyValueToString(System.Collections.IEnumerable enumerable)
    {
        var values = new List<string>();
        foreach (var value in enumerable)
        {
            if (ConvertPropertyValueToString(value) is { } stringValue)
            {
                values.Add(stringValue);
            }
        }

        return string.Join(',', values);
    }

    private static JsonArray ConvertEnumerablePropertyValueToJsonArray(System.Collections.IEnumerable enumerable)
    {
        var array = new JsonArray();
        foreach (var value in enumerable)
        {
            array.Add(ConvertPropertyValueToJsonNode(value));
        }

        return array;
    }

    private static bool SupportsJsonResourceProperties(string[]? clientCapabilities)
    {
        return clientCapabilities?.Contains(AuxiliaryBackchannelCapabilities.V3, StringComparer.Ordinal) == true;
    }

    private static string[]? GetStringArrayPropertyValue(object? value)
    {
        return value switch
        {
            null => null,
            string s => [s],
            IEnumerable<string> strings => strings.Where(static s => !string.IsNullOrEmpty(s)).ToArray(),
            IEnumerable<object> objects => objects.OfType<string>().Where(static s => !string.IsNullOrEmpty(s)).ToArray(),
            System.Collections.IEnumerable enumerable => enumerable.Cast<object>().OfType<string>().Where(static s => !string.IsNullOrEmpty(s)).ToArray(),
            _ => null
        } is { Length: > 0 } values
            ? values
            : null;
    }

    private static Dictionary<string, string?>? CreateOptionsDictionary(IReadOnlyList<KeyValuePair<string, string>>? options)
    {
        if (options is null)
        {
            return null;
        }

        var result = new Dictionary<string, string?>();
        foreach (var option in options)
        {
            result[option.Key] = option.Value;
        }

        return result;
    }

    private static ResourceSnapshotCommandArgumentDynamicLoading? CreateDynamicLoadingMetadata(InputLoadOptions? dynamicLoading)
    {
        return dynamicLoading is null
            ? null
            : new ResourceSnapshotCommandArgumentDynamicLoading
            {
                AlwaysLoadOnStart = dynamicLoading.AlwaysLoadOnStart,
                DependsOnInputs = dynamicLoading.DependsOnInputs?.ToArray()
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
        await foreach (var logLine in GetResourceLogsAsync(
            "GetResourceLogsAsync",
            null,
            resourceName,
            follow,
            includeHidden: true,
            search: null,
            tail: null,
            cancellationToken).ConfigureAwait(false))
        {
            yield return logLine;
        }
    }

    private async IAsyncEnumerable<ResourceLogBatch> GetResourceLogBatchesAsync(
        string rpcMethodName,
        BackchannelTraceContext? traceContext,
        string? resourceName,
        bool follow,
        bool includeHidden,
        string? search,
        int? tail,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var maxBatchSize = follow ? 1 : 256;
        var logLines = GetResourceLogsAsync(
            rpcMethodName,
            traceContext,
            resourceName,
            follow,
            includeHidden,
            search,
            tail,
            cancellationToken);

        await foreach (var batch in logLines.GetBatchesAsync(maxBatchSize, cancellationToken).ConfigureAwait(false))
        {
            yield return new ResourceLogBatch { Lines = batch };
        }
    }

    private async IAsyncEnumerable<ResourceLogLine> GetResourceLogsAsync(
        string rpcMethodName,
        BackchannelTraceContext? traceContext,
        string? resourceName,
        bool follow,
        bool includeHidden,
        string? search,
        int? tail,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var resourceLoggerService = serviceProvider.GetRequiredService<ResourceLoggerService>();
        var appModel = serviceProvider.GetRequiredService<DistributedApplicationModel>();
        var notificationService = serviceProvider.GetRequiredService<ResourceNotificationService>();
        using var activity = profilingTelemetry.StartJsonRpcServerCall(rpcMethodName, streaming: true, traceContext);
        var yieldedCount = 0;
        using var logStreamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var logStreamCancellationToken = logStreamCts.Token;
        Task? completeWriterTask = null;

        try
        {
            // Resolve logical resource names to runtime resource ids before reading logs.
            // Replicated resources can produce multiple ids for a single app model resource.
            var resourcesToLog = resourceName is not null
                ? ResolveResourceIds(appModel, resourceName)
                : ResolveAllResourceIds(appModel);

            // IncludeHidden only filters the all-resource stream. A named resource request is
            // treated as an explicit request for that resource, even when the resource is hidden.
            if (!includeHidden && resourceName is null)
            {
                resourcesToLog = resourcesToLog
                    .Where(resolvedResourceName => !IsHiddenResource(notificationService, resolvedResourceName))
                    .ToList();
            }

            if (resourceName is not null && resourcesToLog.Count == 0)
            {
                logger.LogWarning("Resource '{ResourceName}' not found. No logs will be returned.", resourceName);
                yield break;
            }

            if (resourcesToLog.Count == 0)
            {
                yield break;
            }

            // Server-side tailing only applies to finite snapshots for a single resource. Follow
            // streams return new log lines, so any Tail value on a follow request is ignored here.
            // For multiple resources, the CLI needs the parsed/merged ordering across resources
            // before it can choose the final tail entries without changing observable output order.
            var serverTailLineCount = !follow && tail.GetValueOrDefault() > 0 && resourcesToLog.Count == 1
                ? tail.GetValueOrDefault()
                : 0;

            // Queue<T> capacity only preallocates storage; the dequeue/enqueue logic below
            // enforces the fixed-size tail window.
            Queue<ResourceLogLine>? tailBuffer = serverTailLineCount > 0 ? new Queue<ResourceLogLine>(serverTailLineCount) : null;

            // Read each resource in parallel and filter before writing to the JSON-RPC stream.
            // This keeps large non-matching log histories inside the AppHost instead of forcing
            // the CLI to transfer and parse them just to discard them.
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

                        await foreach (var batch in logStream.WithCancellation(logStreamCancellationToken).ConfigureAwait(false))
                        {
                            foreach (var logLine in batch)
                            {
                                var resourceLogLine = new ResourceLogLine
                                {
                                    ResourceName = name,
                                    LineNumber = logLine.LineNumber,
                                    Content = logLine.Content,
                                    IsError = logLine.IsErrorMessage
                                };

                                // Search on the server as a transport optimization. The CLI applies
                                // its parsed-log filter again so older AppHosts and display-name edge
                                // cases keep the same output.
                                if (!MatchesSearch(resourceLogLine, search))
                                {
                                    continue;
                                }

                                await channel.Writer.WriteAsync(resourceLogLine, logStreamCancellationToken).ConfigureAwait(false);
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
                }, logStreamCancellationToken);
                tasks.Add(task);
            }

            completeWriterTask = Task.WhenAll(tasks).ContinueWith(_ => channel.Writer.Complete(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

            await foreach (var logLine in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (tailBuffer is not null)
                {
                    if (tailBuffer.Count == serverTailLineCount)
                    {
                        tailBuffer.Dequeue();
                    }

                    tailBuffer.Enqueue(logLine);
                    continue;
                }

                if (yieldedCount == 0)
                {
                    activity.AddJsonRpcStreamFirstItemEvent();
                }

                yieldedCount++;
                yield return logLine;
            }

            if (tailBuffer is not null)
            {
                foreach (var logLine in tailBuffer)
                {
                    if (yieldedCount == 0)
                    {
                        activity.AddJsonRpcStreamFirstItemEvent();
                    }

                    yieldedCount++;
                    yield return logLine;
                }
            }

            activity.AddJsonRpcStreamCompletedEvent();
        }
        finally
        {
            // Consumers can stop enumerating a follow stream without cancelling the RPC token.
            // Cancel the per-resource producers so they don't keep watching logs after the
            // JSON-RPC stream has ended.
            await logStreamCts.CancelAsync().ConfigureAwait(false);
            if (completeWriterTask is not null)
            {
                await completeWriterTask.ConfigureAwait(false);
            }

            activity.SetJsonRpcStreamItemCount(yieldedCount);
        }
    }

    private static bool IsHiddenResource(ResourceNotificationService notificationService, string resourceName)
    {
        return notificationService.TryGetCurrentState(resourceName, out var resourceEvent) && resourceEvent.Snapshot.IsHidden;
    }

    private static bool MatchesSearch(ResourceLogLine logLine, string? search)
    {
        if (string.IsNullOrEmpty(search))
        {
            return true;
        }

        if (logLine.Content.Contains(search, StringComparisons.FullTextSearch) ||
            logLine.ResourceName.Contains(search, StringComparisons.FullTextSearch))
        {
            return true;
        }

        // ResourceLoggerService stores raw lines like:
        //   2000-12-29T20:59:59.0000000Z Re\u001b[31mady
        // Strip ANSI control sequences for the server-side pre-filter so a
        // visible-text search for "Ready" does not drop the line before the CLI
        // can apply its parsed-log search semantics.
        return AnsiParser.StripControlSequences(logLine.Content)
            .Contains(search, StringComparisons.FullTextSearch);
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
        return rpcTarget.GetAppHostLogEntriesAsync(cancellationToken: cancellationToken);
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
