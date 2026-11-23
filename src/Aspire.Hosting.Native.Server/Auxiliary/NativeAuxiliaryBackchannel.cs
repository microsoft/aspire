// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Transport;
using Microsoft.Extensions.Logging;
using static Aspire.Hosting.Native.Auxiliary.NativeAuxiliaryProtocol;

namespace Aspire.Hosting.Native.Auxiliary;

/// <summary>Exposes the managed Hosting auxiliary protocol over discoverable owner-only sockets.</summary>
internal sealed class NativeAuxiliaryBackchannel : IDisposable
{
    private readonly AppHostSocketManager.AppHostSocketListener _listener;
    private readonly NativeApplicationServer _server;
    private readonly NativeServerOptions _options;
    private readonly INativeBackchannelRpc _rpc;
    private readonly string? _appHostPath;
    private readonly Action<int?> _stop;
    private readonly Func<CancellationToken, Task> _ready;
    private readonly Func<CancellationToken, Task<NativeCliDashboardUrls>> _dashboard;
    private readonly Func<string?> _dashboardApiToken;
    private readonly NativeCliLogBuffer _logs;
    private readonly NativeCliProcessMetadata _cliProcess;
    private readonly Lock _snapshotGate = new();
    private ApplicationObserver? _snapshotObserver;
    private long _observationVersion = -1;
    private long _version;
    private NativeAuxiliaryResourceSnapshot[] _snapshots = [];

    internal string SocketPath => _listener.SocketPath;

    // GuestAppHostProject already stamps the guest's absolute path in ASPIRE_APPHOST_FILEPATH.
    // Treat it as the FilePath projection, ahead of the less-specific AppHost:Path fallback.
    internal static string? ResolveAppHostPath(string? filePath, string? cliFilePath, string? path, string? nativePath) =>
        filePath ?? cliFilePath ?? path ?? nativePath;

    public NativeAuxiliaryBackchannel(string? appHostPath, string homeDirectory, NativeApplicationServer server,
        NativeServerOptions options, Action<int?> stop, Func<CancellationToken, Task> ready,
        Func<CancellationToken, Task<NativeCliDashboardUrls>> dashboard, Func<string?> dashboardApiToken,
        NativeCliLogBuffer logs, NativeCliProcessMetadata cliProcess, INativeBackchannelRpc rpc, ILogger logger)
    {
        options.Validate();
        _appHostPath = appHostPath;
        _server = server;
        _options = options;
        _stop = stop;
        _ready = ready;
        _dashboard = dashboard;
        _dashboardApiToken = dashboardApiToken;
        _logs = logs;
        _cliProcess = cliProcess;
        _rpc = rpc;
        _listener = AppHostSocketManager.CreateSocket(appHostPath, homeDirectory, Environment.ProcessId, logger);
    }

    public Task RunAsync(CancellationToken cancellationToken) =>
        SocketConnectionListener.RunAsync(_listener.Socket, _options.MaximumConnections,
            (stream, token) => _rpc.ServeAsync(stream, DispatchAsync, token), cancellationToken);

    private async Task<NativeBackchannelReply> DispatchAsync(NativeCliRequest request, CancellationToken cancellationToken)
    {
        var json = NativeAuxiliaryJsonContext.Default;
        switch (request.Method)
        {
            case "GetCapabilitiesAsync":
                ReadRequest(request, json.NativeAuxiliaryEmptyRequest, required: false);
                return NativeBackchannelReply.Value(new NativeAuxiliaryCapabilities(
                    ["aux.v1", "aux.v2", "aux.v3", "resource-snapshot-versions.v1"]), json.NativeAuxiliaryCapabilities);
            case "GetAppHostInformationAsync":
                LegacyArguments(request, 0);
                return NativeBackchannelReply.Value(GetAppHostInformation(), json.NativeAuxiliaryAppHostInformation);
            case "GetAppHostInfoAsync":
                ReadRequest(request, json.NativeAuxiliaryEmptyRequest, required: false);
                var info = GetAppHostInformation();
                return NativeBackchannelReply.Value(new NativeAuxiliaryAppHostInfo(
                    info.ProcessId.ToString(CultureInfo.InvariantCulture),
                    typeof(NativeAuxiliaryBackchannel).Assembly.GetName().Version?.ToString() ?? "unknown",
                    info.AppHostPath, info.CliProcessId, info.StartedAt, info.CliLogFilePath), json.NativeAuxiliaryAppHostInfo);
            case "GetDashboardUrlsAsync":
                LegacyArguments(request, 0);
                return NativeBackchannelReply.Value(await _dashboard(cancellationToken).ConfigureAwait(false),
                    NativeCliJsonContext.Default.NativeCliDashboardUrls);
            case "GetDashboardInfoAsync":
                ReadRequest(request, json.NativeAuxiliaryEmptyRequest, required: false);
                var urls = await _dashboard(cancellationToken).ConfigureAwait(false);
                var apiBaseUrl = urls.BaseUrlWithLoginToken is { } url
                    ? new Uri(url).GetLeftPart(UriPartial.Authority) : null;
                return NativeBackchannelReply.Value(new NativeAuxiliaryDashboardInfo(
                    apiBaseUrl, _dashboardApiToken(),
                    new[] { urls.BaseUrlWithLoginToken, urls.CodespacesUrlWithLoginToken }.OfType<string>().ToArray(),
                    urls.DashboardHealthy), json.NativeAuxiliaryDashboardInfo);
            case "GetDashboardMcpConnectionInfoAsync":
                LegacyArguments(request, 0);
                // Managed Hosting retains this v1 method but always returns null after removing Dashboard MCP.
                return NativeBackchannelReply.Value((string?)null, NativeCliJsonContext.Default.String);
            case "WaitForAppHostReadyAsync":
                ReadRequest(request, json.NativeAuxiliaryEmptyRequest, required: false);
                await _ready(cancellationToken).ConfigureAwait(false);
                return NativeBackchannelReply.Value(new NativeAuxiliaryReady(true), json.NativeAuxiliaryReady);
            case "GetResourcesAsync":
                var resourcesRequest = ReadRequest(request, json.NativeAuxiliaryResourcesRequest, required: false) ?? new();
                return NativeBackchannelReply.Value(new NativeAuxiliaryResources(ReadSnapshots(resourcesRequest)),
                    json.NativeAuxiliaryResources);
            case "GetResourceSnapshotsAsync":
                LegacyArguments(request, 0);
                return NativeBackchannelReply.Value(ReadSnapshots(new()), json.NativeAuxiliaryResourceSnapshotArray);
            case "WatchResourcesAsync":
                var watchRequest = ReadRequest(request, json.NativeAuxiliaryResourcesRequest, required: false) ?? new();
                return NativeBackchannelReply.Streaming(WatchSnapshotsAsync(watchRequest, CancellationToken.None), json.NativeAuxiliaryResourceSnapshot);
            case "WatchResourceSnapshotsAsync":
                LegacyArguments(request, 0);
                return NativeBackchannelReply.Streaming(WatchSnapshotsAsync(new(), CancellationToken.None), json.NativeAuxiliaryResourceSnapshot);
            case "GetConsoleLogsAsync":
                return NativeBackchannelReply.Streaming(
                    ReadLogsAsync(ReadRequest(request, json.NativeAuxiliaryLogsRequest, required: true)!, CancellationToken.None),
                    json.NativeAuxiliaryLogLine);
            case "GetConsoleLogBatchesAsync":
                return NativeBackchannelReply.Streaming(
                    ReadLogBatchesAsync(ReadRequest(request, json.NativeAuxiliaryLogsRequest, required: true)!, CancellationToken.None),
                    json.NativeAuxiliaryLogBatch);
            case "GetResourceLogsAsync":
                var legacyLogs = LegacyArguments(request, 2);
                return NativeBackchannelReply.Streaming(ReadLogsAsync(new()
                {
                    ResourceName = legacyLogs.Length > 0 && legacyLogs[0].ValueKind != JsonValueKind.Null ? legacyLogs[0].GetString() : null,
                    Follow = legacyLogs.Length > 1 && legacyLogs[1].GetBoolean(),
                    IncludeHidden = true
                }, CancellationToken.None), json.NativeAuxiliaryLogLine);
            case "GetAppHostLogEntriesAsync":
                LegacyArguments(request, 0);
                return NativeBackchannelReply.Streaming(_logs.ReadAsync(CancellationToken.None), NativeCliJsonContext.Default.NativeCliLogEntry);
            case "ExecuteResourceCommandAsync":
                return NativeBackchannelReply.Value(await ExecuteCommandAsync(
                    ReadRequest(request, json.NativeAuxiliaryCommandRequest, required: true)!, cancellationToken).ConfigureAwait(false),
                    json.NativeAuxiliaryCommandResponse);
            case "WaitForResourceAsync":
                return NativeBackchannelReply.Value(await WaitForResourceAsync(
                    ReadRequest(request, json.NativeAuxiliaryWaitRequest, required: true)!, cancellationToken).ConfigureAwait(false),
                    json.NativeAuxiliaryWaitResponse);
            case "StopAsync":
                var stop = ReadRequest(request, json.NativeAuxiliaryStopRequest, required: false);
                return NativeBackchannelReply.Value(new NativeAuxiliaryEmptyResponse(), json.NativeAuxiliaryEmptyResponse)
                    with { AfterResponse = () => _stop(stop?.ExitCode) };
            case "StopAppHostAsync":
                LegacyArguments(request, 0);
                return NativeBackchannelReply.Value((string?)null, NativeCliJsonContext.Default.String)
                    with { AfterResponse = () => _stop(null) };
            case "GetTerminalInfoAsync":
                ReadRequest(request, json.NativeAuxiliaryTerminalRequest, required: true);
                return NativeBackchannelReply.Value(new NativeAuxiliaryTerminalInfo(), json.NativeAuxiliaryTerminalInfo);
            case "ListTerminalsAsync":
                ReadRequest(request, json.NativeAuxiliaryEmptyRequest, required: false);
                return NativeBackchannelReply.Value(new NativeAuxiliaryTerminals(), json.NativeAuxiliaryTerminals);
            case "CallMcpToolAsync":
                throw McpUnavailable(ReadRequest(request, json.NativeAuxiliaryMcpRequest, required: true)!.ResourceName);
            case "CallResourceMcpToolAsync":
                var mcp = LegacyArguments(request, 3);
                if (mcp.Length < 2)
                {
                    throw new ArgumentException("An MCP invocation requires a resource and tool name.");
                }
                throw McpUnavailable(mcp[0].GetString() ?? throw new ArgumentException("A resource name is required."));
            default:
                throw new MissingMethodException();
        }
    }

    private NativeAuxiliaryAppHostInformation GetAppHostInformation()
    {
        if (string.IsNullOrEmpty(_appHostPath))
        {
            throw new InvalidOperationException("AppHost path not found in configuration.");
        }
        using var process = Process.GetCurrentProcess();

        return new()
        {
            AppHostPath = _appHostPath,
            ProcessId = Environment.ProcessId,
            StartedAt = new DateTimeOffset(process.StartTime),
            StableStartedAt = ProcessStartTimeHelper.TryGetProcessStartTime(Environment.ProcessId),
            CliProcessId = _cliProcess.ProcessId,
            CliStartedAt = _cliProcess.StartedAt,
            CliStableStartedAt = _cliProcess.StableStartedAt,
            CliLogFilePath = _cliProcess.LogFilePath
        };
    }

    private NativeAuxiliaryResourceSnapshot[] ReadSnapshots(NativeAuxiliaryResourcesRequest request)
    {
        lock (_snapshotGate)
        {
            var observer = _server.GetApplicationObserver();
            var observation = observer?.ReadNativeObservations();
            if (!ReferenceEquals(observer, _snapshotObserver) || (observation?.Version ?? -1) != _observationVersion)
            {
                _snapshotObserver = observer;
                _observationVersion = observation?.Version ?? -1;
                var version = checked(++_version);
                _snapshots = observation?.Resources.Select(resource => new NativeAuxiliaryResourceSnapshot
                {
                    Name = resource.Name,
                    DisplayName = resource.Name,
                    Version = version,
                    ResourceType = resource.TypeId,
                    State = resource.State.ToWire(),
                    StateStyle = resource.State == ResourceState.Failed ? "error" : null,
                    HealthStatus = resource.Healthy ? "Healthy" : "Unhealthy",
                    CreatedAt = observer!.CreatedAt,
                    IsHidden = false,
                    Urls = resource.Urls.Select((url, index) => new NativeAuxiliaryUrl(
                        index == 0 ? "default" : $"endpoint-{index}", url)).ToArray(),
                    Commands = resource.Commands.Select(command => new NativeAuxiliaryCommand
                    {
                        Name = command.Name, DisplayName = command.DisplayName, State = "Enabled"
                    }).ToArray(),
                    // Never expose authoring configuration, launch environments, invitations or authentication
                    // material. These safe observation-only properties contain no parameter/secret values.
                    Properties = new()
                    {
                        ["resourceId"] = JsonSerializer.SerializeToElement(resource.ResourceId.ToString(), NativeAuxiliaryJsonContext.Default.String),
                        ["configurationRevision"] = JsonSerializer.SerializeToElement(resource.ConfigurationRevision, NativeAuxiliaryJsonContext.Default.Int64),
                        ["appliedConfigurationRevision"] = JsonSerializer.SerializeToElement(resource.AppliedConfigurationRevision, NativeAuxiliaryJsonContext.Default.Int64),
                        ["configurationStatus"] = JsonSerializer.SerializeToElement(resource.ConfigurationStatus.ToWire(), NativeAuxiliaryJsonContext.Default.String)
                    }
                }).ToArray() ?? [];
            }
            var typed = request.ClientCapabilities.Contains("aux.v3", StringComparer.Ordinal);

            return _snapshots.Where(snapshot => string.IsNullOrEmpty(request.Filter) ||
                snapshot.Name.Contains(request.Filter, StringComparison.OrdinalIgnoreCase))
                .Select(snapshot => typed ? snapshot : snapshot with
                {
                    Properties = snapshot.Properties.ToDictionary(entry => entry.Key,
                        entry => entry.Value.ValueKind == JsonValueKind.String ? entry.Value :
                            JsonSerializer.SerializeToElement(entry.Value.ToString(), NativeAuxiliaryJsonContext.Default.String))
                }).ToArray();
        }
    }

    private async IAsyncEnumerable<NativeAuxiliaryResourceSnapshot> WatchSnapshotsAsync(NativeAuxiliaryResourcesRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        long previous = -1;
        var previousSnapshots = new Dictionary<string, NativeAuxiliaryResourceSnapshot>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeAuxiliaryResourceSnapshot[] snapshots;
            long version;
            try
            {
                lock (_snapshotGate)
                {
                    snapshots = ReadSnapshots(request);
                    version = _version;
                }
            }
            catch (ObjectDisposedException)
            {
                await Task.Delay(_options.RetryInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (version > previous)
            {
                var current = snapshots.ToDictionary(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase);
                // Hosting watchers upsert by name. Each stream must hide names it
                // previously sent but no longer owns, even when intermediate revisions
                // were coalesced or the entire workspace becomes empty.
                foreach (var removed in previousSnapshots.Values.Where(snapshot => !current.ContainsKey(snapshot.Name)))
                {
                    yield return removed with
                    {
                        Version = version, State = "Hidden", IsHidden = true,
                        StateStyle = null, HealthStatus = null, Urls = [], Commands = []
                    };
                }
                foreach (var snapshot in snapshots)
                {
                    yield return snapshot;
                }
                previousSnapshots = current;
                previous = version;
            }
            await Task.Delay(_options.RetryInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<NativeAuxiliaryLogLine> ReadLogsAsync(NativeAuxiliaryLogsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sequences = new Dictionary<Guid, long>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observer = _server.GetApplicationObserver();
            var entries = new List<(DateTimeOffset Timestamp, long Sequence, NativeAuxiliaryLogLine Line)>();
            var resourceCount = 0;
            try
            {
                var resources = observer?.ReadNativeObservations().Resources ?? [];
                foreach (var resource in resources.Where(resource =>
                    request.ResourceName is null || MatchesResource(resource, request.ResourceName)))
                {
                    resourceCount++;
                    var logs = observer!.ReadNativeLogs(resource.ResourceId, sequences.GetValueOrDefault(resource.ResourceId));
                    sequences[resource.ResourceId] = logs.LastSequence;
                    foreach (var log in logs.Entries)
                    {
                        var line = new NativeAuxiliaryLogLine(resource.Name, checked((int)log.Sequence), log.Message, log.Stream == LogStream.Stderr);
                        if (string.IsNullOrEmpty(request.Search) ||
                            line.Content.Contains(request.Search, StringComparison.OrdinalIgnoreCase) ||
                            line.ResourceName.Contains(request.Search, StringComparison.OrdinalIgnoreCase))
                        {
                            entries.Add((log.Timestamp, log.Sequence, line));
                        }
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                if (!request.Follow)
                {
                    yield break;
                }
            }
            // ResourceLoggerService.WatchAsync delivers its retained in-memory backlog before live
            // lines. Tail is only applied to finite single-resource snapshots, never follow streams.
            var ordered = entries.OrderBy(entry => entry.Timestamp)
                .ThenBy(entry => entry.Sequence).Select(entry => entry.Line);
            if (!request.Follow && request.Tail is > 0 && resourceCount == 1)
            {
                ordered = ordered.TakeLast(request.Tail.Value);
            }
            foreach (var line in ordered)
            {
                yield return line;
            }
            if (!request.Follow)
            {
                yield break;
            }
            await Task.Delay(_options.RetryInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<NativeAuxiliaryLogBatch> ReadLogBatchesAsync(NativeAuxiliaryLogsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var batch = new List<NativeAuxiliaryLogLine>();
        var size = request.Follow ? 1 : 256;
        await foreach (var line in ReadLogsAsync(request, cancellationToken).ConfigureAwait(false))
        {
            batch.Add(line);
            if (batch.Count == size)
            {
                yield return new(batch.ToArray());
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            yield return new(batch.ToArray());
        }
    }

    private static bool MatchesResource(RuntimeResourceSnapshot resource, string name) =>
        resource.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || resource.ResourceId.ToString().Equals(name, StringComparison.OrdinalIgnoreCase);

    private InvalidOperationException McpUnavailable(string resourceName)
    {
        var resource = _server.GetApplicationObserver()?.ReadNativeObservations().Resources
            .FirstOrDefault(resource => MatchesResource(resource, resourceName));

        return new(resource is null ? $"Resource '{resourceName}' not found." :
            $"Resource '{resourceName}' does not have an MCP endpoint annotation.");
    }

    private async Task<NativeAuxiliaryCommandResponse> ExecuteCommandAsync(NativeAuxiliaryCommandRequest request, CancellationToken cancellationToken)
    {
        var observer = _server.GetApplicationObserver();
        var resource = observer?.ReadNativeObservations().Resources.FirstOrDefault(resource => MatchesResource(resource, request.ResourceName));
        if (resource is null)
        {
            return CommandResponse(false, $"Resource '{request.ResourceName}' not found.");
        }
        if (!resource.Commands.Any(command => command.Name == request.CommandName))
        {
            return CommandResponse(false, $"Command '{request.CommandName}' not found on resource '{request.ResourceName}'.");
        }
        // Native commands carry no argument metadata and cannot prompt. Reject supplied input rather
        // than pretending to validate/dispatch arguments that the integration will never receive.
        if (request.Arguments is { ValueKind: not JsonValueKind.Null } arguments &&
            !(arguments.ValueKind == JsonValueKind.Object && !arguments.EnumerateObject().Any() ||
                arguments.ValueKind == JsonValueKind.Array && arguments.GetArrayLength() == 0))
        {
            return CommandResponse(false, "This resource command does not accept arguments.");
        }
        if (request.ValidateOnly)
        {
            return CommandResponse(true, null, argumentInputs: request.ReturnArgumentInputs ? [] : null);
        }
        var operation = observer!.InvokeResourceCommand(resource.Name, request.CommandName);
        var result = await operation.AwaitNativeOperation().WaitAsync(cancellationToken).ConfigureAwait(false);

        return CommandResponse(result.Status == RequestOutcome.Succeeded, result.Message, result.Status == RequestOutcome.Cancelled,
            request.ReturnArgumentInputs ? [] : null);
    }

    private static NativeAuxiliaryCommandResponse CommandResponse(bool success, string? message, bool canceled = false,
        NativeAuxiliaryCommandArgument[]? argumentInputs = null) => new()
    {
        Success = success, Canceled = canceled, Message = message, ErrorMessage = message, ArgumentInputs = argumentInputs
    };

    private async Task<NativeAuxiliaryWaitResponse> WaitForResourceAsync(NativeAuxiliaryWaitRequest request, CancellationToken cancellationToken)
    {
        if (request.TimeoutSeconds < 0)
        {
            throw new ArgumentException("The timeout must be non-negative.");
        }
        var observer = _server.GetApplicationObserver();
        var initial = observer?.ReadNativeObservations().Resources.FirstOrDefault(resource => MatchesResource(resource, request.ResourceName));
        if (initial is null)
        {
            return new() { Success = false, ResourceNotFound = true, ErrorMessage = $"Resource '{request.ResourceName}' not found." };
        }
        if (!NativeAuxiliaryProtocol.TryParseWaitTarget(request.Status, out var target))
        {
            return new() { Success = false, ErrorMessage = $"Unknown status: {request.Status}" };
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds), _options.TimeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            while (true)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                var resource = observer!.ReadNativeObservations().Resources.FirstOrDefault(resource => resource.ResourceId == initial.ResourceId);
                if (resource is null)
                {
                    return new() { Success = false, ResourceNotFound = true, ErrorMessage = $"Resource '{request.ResourceName}' not found." };
                }
                var terminal = resource.State.IsTerminal();
                var success = target switch
                {
                    NativeWaitTarget.Up => resource.State == ResourceState.Running,
                    NativeWaitTarget.Healthy => resource.State == ResourceState.Running && resource.Healthy,
                    NativeWaitTarget.Down => terminal,
                    _ => false
                };
                if (success || terminal)
                {
                    return new()
                    {
                        Success = success, State = resource.State.ToWire(), HealthStatus = resource.Healthy ? "Healthy" : "Unhealthy",
                        ErrorMessage = success ? null : $"Resource '{request.ResourceName}' failed to reach the target status. Current state: {resource.State.ToWire()}."
                    };
                }
                await Task.Delay(_options.RetryInterval, _options.TimeProvider, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new() { Success = false, TimedOut = true, ErrorMessage = $"Timed out waiting for resource '{request.ResourceName}'." };
        }
    }

    public void Dispose() => _listener.Dispose();
}
