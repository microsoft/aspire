// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Shared.TerminalHost;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Aspire.TerminalHost.Tests;

[CollectionDefinition(nameof(TerminalHostAppTestsCollection), DisableParallelization = true)]
public sealed class TerminalHostAppTestsCollection;

[Collection(nameof(TerminalHostAppTestsCollection))]
public class TerminalHostAppTests
{
    private static (TerminalHostArgs args, TestTempDirectory tmp, string controlPath) BuildArgs(int replicaCount)
    {
        var tmp = new TestTempDirectory();
        var dcpDir = Path.Combine(tmp.Path, "dcp");
        var hostDir = Path.Combine(tmp.Path, "host");
        var ctrlDir = Path.Combine(tmp.Path, "control");
        Directory.CreateDirectory(dcpDir);
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(ctrlDir);

        var producers = new string[replicaCount];
        var consumers = new string[replicaCount];
        for (var i = 0; i < replicaCount; i++)
        {
            producers[i] = Path.Combine(dcpDir, $"r{i}.sock");
            consumers[i] = Path.Combine(hostDir, $"r{i}.sock");
        }
        var control = Path.Combine(ctrlDir, "ctrl.sock");

        var argList = new List<string> { "--replica-count", replicaCount.ToString() };
        foreach (var p in producers)
        {
            argList.Add("--producer-uds");
            argList.Add(p);
        }
        foreach (var c in consumers)
        {
            argList.Add("--consumer-uds");
            argList.Add(c);
        }
        argList.Add("--control-uds");
        argList.Add(control);

        return (TerminalHostArgs.Parse([.. argList]), tmp, control);
    }

    [Fact]
    public async Task RunAsyncBindsControlListenerWhenStarted()
    {
        var (args, tmp, control) = BuildArgs(1);
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));
            Assert.True(File.Exists(control));
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ControlEndpointReturnsReplicaInfo()
    {
        var (args, tmp, control) = BuildArgs(2);
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            using var rpc = await OpenControlRpcAsync(control);
            var info = await rpc.InvokeAsync<TerminalHostInfoResponse>(
                TerminalHostControlProtocol.GetInfoMethod);
            var replicas = await rpc.InvokeAsync<TerminalHostReplicasResponse>(
                TerminalHostControlProtocol.GetReplicasMethod);

            Assert.Equal(TerminalHostControlProtocol.ProtocolVersion, info.ProtocolVersion);
            Assert.Equal(2, info.ReplicaCount);
            Assert.Equal(2, replicas.Replicas.Length);
            Assert.Equal(0, replicas.Replicas[0].Index);
            Assert.Equal(1, replicas.Replicas[1].Index);
            Assert.Equal(args.ProducerUdsPaths[0], replicas.Replicas[0].ProducerUdsPath);
            Assert.Equal(args.ConsumerUdsPaths[1], replicas.Replicas[1].ConsumerUdsPath);
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ShutdownRequestCausesRunAsyncToReturn()
    {
        var (args, tmp, control) = BuildArgs(1);
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

        using (var rpc = await OpenControlRpcAsync(control))
        {
            // Fire and forget — the host may close the socket before the RPC ack arrives.
            _ = rpc.InvokeAsync(TerminalHostControlProtocol.ShutdownMethod);
        }

        var exitCode = await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task SnapshotReplicasReturnsConfiguredReplicas()
    {
        var (args, tmp, control) = BuildArgs(3);
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            var snap = app.SnapshotReplicas();
            Assert.Equal(3, snap.Length);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(i, snap[i].Index);
                Assert.Equal(args.ProducerUdsPaths[i], snap[i].ProducerUdsPath);
                Assert.Equal(args.ConsumerUdsPaths[i], snap[i].ConsumerUdsPath);
            }
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task RunAsyncWithBadArgsViaStaticEntryPointReturnsExUsage()
    {
        var exitCode = await TerminalHostApp.RunAsync(["--bogus"], CancellationToken.None);
        Assert.Equal(64, exitCode); // EX_USAGE
    }

    private static async Task<JsonRpc> OpenControlRpcAsync(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var stream = new NetworkStream(socket, ownsSocket: true);
        var formatter = new SystemTextJsonFormatter();
        var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
        var rpc = new JsonRpc(handler);
        rpc.StartListening();
        return rpc;
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                return;
            }
            await Task.Delay(50);
        }

        throw new TimeoutException($"Timed out waiting for '{path}' after {timeout.TotalSeconds:F1}s.");
    }
}
