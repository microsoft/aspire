// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Server;

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
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NativeCliProtocol(string path, NativeApplicationServer server, Action stop,
        Func<CancellationToken, Task<NativeCliDashboardUrls>> dashboard, NativeCliLogBuffer logs, NativeServerOptions options)
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
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            _listener.Bind(new UnixDomainSocketEndPoint(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            _listener.Listen(1);
        }
        catch
        {
            _listener.Dispose();
            throw;
        }
    }

    internal bool IsAppHostReady => _ready.Task.IsCompletedSuccessfully;
    internal Task WaitForAppHostReadyAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                await NativeBackchannelConnection.ServeAsync(socket, _options, DispatchAsync, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<NativeBackchannelConnection.Reply> DispatchAsync(NativeCliRequest request, CancellationToken cancellationToken)
    {
        if (request.Params is { ValueKind: not JsonValueKind.Null } parameters &&
            (parameters.ValueKind != JsonValueKind.Array || parameters.GetArrayLength() != 0))
        {
            throw new ArgumentException("This CLI method takes no arguments.");
        }
        switch (request.Method)
        {
            case "GetCapabilitiesAsync":
                return NativeBackchannelConnection.Value(new[] { "baseline.v2" }, NativeCliJsonContext.Default.StringArray);
            case "NotifyAppHostReadyAsync":
                _ready.TrySetResult();
                return NativeBackchannelConnection.Value((string?)null, NativeCliJsonContext.Default.String);
            case "RequestStopAsync":
                return NativeBackchannelConnection.Value((string?)null, NativeCliJsonContext.Default.String) with { AfterResponse = _stop };
            case "GetDashboardUrlsAsync":
                return NativeBackchannelConnection.Value(await _dashboard(cancellationToken).ConfigureAwait(false),
                    NativeCliJsonContext.Default.NativeCliDashboardUrls);
            case "GetResourceStatesAsync":
                return NativeBackchannelConnection.Streaming(ResourceStatesAsync(cancellationToken), NativeCliJsonContext.Default.NativeCliResourceState);
            case "GetAppHostLogEntriesAsync":
                return NativeBackchannelConnection.Streaming(_logs.ReadAsync(cancellationToken), NativeCliJsonContext.Default.NativeCliLogEntry);
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
                await Task.Delay(_options.RetryInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }
            ApplicationObservations snapshot;
            try
            {
                snapshot = observer.ReadResourceObservations();
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
                yield return new NativeCliResourceState(resource.Name, resource.TypeId, resource.State,
                    resource.Urls, resource.Healthy ? "Healthy" : "Unhealthy");
            }
        }
    }

    public void Dispose()
    {
        _listener.Dispose();
        File.Delete(_path);
    }
}
