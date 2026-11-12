// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Native.Rpc;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Responds to activation bootstrap but deliberately stalls authority abandonment.</summary>
internal sealed class NativeActivationRpcTestHost : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _run;
    public IPEndPoint Endpoint => (IPEndPoint)_listener.LocalEndpoint;
    public TaskCompletionSource AbandonmentStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NativeActivationRpcTestHost()
    {
        _listener.Start();
        _run = RunAsync();
    }

    private async Task RunAsync()
    {
        try
        {
            using var socket = await _listener.AcceptSocketAsync(_lifetime.Token);
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter()));
            rpc.AddLocalRpcMethod("authenticate", new Func<string, bool>(_ => true));
            rpc.AddLocalRpcMethod("getApplicationServer", new Func<JsonElement>(() =>
                JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    ["$handle"] = "root", ["$type"] = NativeRpcContract.ApplicationServerTypeId
                })));
            rpc.AddLocalRpcMethod("expectIntegrationHost", new Func<string, string>(_ => "authority"));
            rpc.AddLocalRpcMethod("abandonIntegrationHostRegistration", new Func<string, Task<bool>>(async _ =>
            {
                AbandonmentStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, TimeProvider.System, _lifetime.Token);

                return true;
            }));
            rpc.StartListening();
            await rpc.Completion.WaitAsync(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            _listener.Stop();
            _lifetime.Dispose();
        }
    }
}
