// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Transport;
using Aspire.Shared;

namespace Aspire.Hosting.Native.Cli;

/// <summary>Implements the existing CLI run backchannel without a native-specific client.</summary>
internal sealed class NativeCliProtocol : IDisposable
{
    private readonly Socket _listener;
    private readonly NativeApplicationServer _server;
    private readonly Action _stop;
    private readonly string _path;
    private readonly Func<CancellationToken, Task<NativeCliDashboardUrls>> _dashboard;
    private readonly NativeCliLogBuffer _logs;
    private readonly NativeServerOptions _options;
    private readonly INativeBackchannelRpc _rpc;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    public NativeCliProtocol(string path, NativeApplicationServer server, Action stop,
        Func<CancellationToken, Task<NativeCliDashboardUrls>> dashboard, NativeCliLogBuffer logs, NativeServerOptions options,
        INativeBackchannelRpc rpc)
    {
        if (!Path.IsPathFullyQualified(path) || File.Exists(path))
        {
            throw new ArgumentException("The backchannel requires a fresh absolute socket path.", nameof(path));
        }
        _path = path;
        _server = server;
        _stop = stop;
        _dashboard = dashboard;
        _logs = logs;
        options.Validate();
        _options = options;
        _rpc = rpc;
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            SocketPermissionHelper.Bind(_listener, path);
            _listener.Listen(options.MaximumConnections);
        }
        catch
        {
            // Bind can succeed before the helper fails to set socket permissions.
            var bound = _listener.IsBound;
            _listener.Dispose();
            if (bound)
            {
                File.Delete(path);
            }
            throw;
        }
    }

    internal bool IsAppHostReady => _ready.Task.IsCompletedSuccessfully;
    internal Task WaitForAppHostReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    public Task RunAsync(CancellationToken cancellationToken) =>
        SocketConnectionListener.RunAsync(_listener, _options.MaximumConnections,
            (stream, token) => _rpc.ServeAsync(stream, DispatchAsync, token), cancellationToken);

    private async Task<NativeBackchannelReply> DispatchAsync(NativeCliRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "GetPipelineStepsAsync")
        {
            // The existing CLI sends [{ "step": "deploy", "traceContext": ... }].
            // A null step requests the complete topologically ordered pipeline.
            if (request.Params is not { ValueKind: JsonValueKind.Array } arguments || arguments.GetArrayLength() != 1 ||
                arguments[0].ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Pipeline inspection requires one request object.");
            }
            var target = arguments[0].TryGetProperty("step", out var step) && step.ValueKind != JsonValueKind.Null
                ? step.GetString() : null;
            var steps = await _server.Publishing.GetStepsAsync(target, cancellationToken).ConfigureAwait(false);
            return NativeBackchannelReply.Value(new NativePipelineSteps(steps.Select(step =>
                new NativePipelineStep(step.Name, step.DependsOn, step.Description, step.Tags, step.ResourceName)).ToArray()),
                NativeCliJsonContext.Default.NativePipelineSteps);
        }
        if (request.Params is { ValueKind: not JsonValueKind.Null } parameters &&
            (parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() != 0))
        {
            throw new ArgumentException("This CLI method takes no arguments.");
        }
        switch (request.Method)
        {
            case "GetCapabilitiesAsync":
                return NativeBackchannelReply.Value(new[] { "baseline.v2", "pipeline-steps.v2" }, NativeCliJsonContext.Default.StringArray);
            case "NotifyAppHostReadyAsync":
                _ready.TrySetResult();
                return NativeBackchannelReply.Value((string?)null, NativeCliJsonContext.Default.String);
            case "RequestStopAsync":
                return NativeBackchannelReply.Value((string?)null, NativeCliJsonContext.Default.String) with
                {
                    // Publishing guests must remain alive until the CLI consumes
                    // completion. Their exit then owns normal session shutdown.
                    AfterResponse = _server.Publishing.WaitsForStop ? _server.Publishing.Stop : _stop
                };
            case "GetPublishingActivitiesAsync":
                return NativeBackchannelReply.Streaming(_server.Publishing.ReadAsync(cancellationToken),
                    NativeCliJsonContext.Default.NativePublishingActivity);
            case "GetDashboardUrlsAsync":
                return NativeBackchannelReply.Value(await _dashboard(cancellationToken).ConfigureAwait(false),
                    NativeCliJsonContext.Default.NativeCliDashboardUrls);
            case "GetResourceStatesAsync":
                return NativeBackchannelReply.Streaming(ResourceStatesAsync(cancellationToken), NativeCliJsonContext.Default.NativeCliResourceState);
            case "GetAppHostLogEntriesAsync":
                return NativeBackchannelReply.Streaming(_logs.ReadAsync(cancellationToken), NativeCliJsonContext.Default.NativeCliLogEntry);
            default:
                throw new MissingMethodException();
        }
    }

    private async IAsyncEnumerable<NativeCliResourceState> ResourceStatesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplicationObserver? previous = null;
        long version = -1;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observer = _server.GetApplicationObserver();
            if (observer is null)
            {
                previous = null;
                version = -1;
                await Task.Delay(_options.RetryInterval, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }
            RuntimeSnapshot snapshot;
            try
            {
                snapshot = observer.ReadNativeObservations();
                if (ReferenceEquals(previous, observer) && snapshot.Version == version)
                {
                    var interval = Math.Max((int)_options.Runtime.ObservationInterval.TotalMilliseconds, 1);
                    await observer.WaitResourceObservations(version, interval).WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }
            }
            catch (ObjectDisposedException)
            {
                continue;
            }
            previous = observer;
            version = snapshot.Version;
            foreach (var resource in snapshot.Resources)
            {
                yield return new NativeCliResourceState(resource.Name, resource.TypeId, resource.State.ToWire(),
                    resource.Urls.ToArray(), resource.Healthy ? "Healthy" : "Unhealthy");
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _listener.Dispose();
        File.Delete(_path);
    }
}
