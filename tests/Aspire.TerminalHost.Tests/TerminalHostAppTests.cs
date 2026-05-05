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

    [Fact]
    public async Task ReplicaRecyclesAfterProducerDisconnect()
    {
        // End-to-end check of the recycle loop: the host should stay running
        // across a producer disconnect and accept a fresh producer on the
        // same UDS path, with ProducerConnected and RestartCount tracking
        // each cycle. This exercises the path DCP exercises in production
        // when the underlying process exits and gets relaunched.
        var (args, tmp, control) = BuildArgs(1);
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            // Initial state: host is up but no producer has dialed in yet.
            var initial = app.SnapshotReplicas();
            Assert.Single(initial);
            Assert.False(initial[0].ProducerConnected, "Replica should report no producer before any connect.");
            Assert.False(initial[0].IsAlive, "Legacy IsAlive should mirror ProducerConnected.");
            Assert.Equal(0, initial[0].RestartCount);

            // Cycle 1: connect, get accepted, disconnect.
            await using (var producer = await ConnectProducerAsync(args.ProducerUdsPaths[0], TimeSpan.FromSeconds(5)))
            {
                await producer.SendHelloAsync(80, 24, default);
                await producer.SendOutputAsync("first cycle"u8.ToArray(), default);
                await WaitForAsync(
                    () => app.SnapshotReplicas()[0].ProducerConnected,
                    TimeSpan.FromSeconds(5),
                    "ProducerConnected should flip to true after producer dials in.");
            }

            await WaitForAsync(
                () =>
                {
                    var s = app.SnapshotReplicas()[0];
                    return !s.ProducerConnected && s.RestartCount >= 1;
                },
                TimeSpan.FromSeconds(10),
                "After producer disconnect, ProducerConnected should clear and RestartCount should advance.");

            var afterCycle1 = app.SnapshotReplicas()[0];
            Assert.Equal(1, afterCycle1.RestartCount);

            // Cycle 2: a fresh producer should be able to dial the same UDS path.
            // This is the critical DCP-restart scenario.
            await using (var producer = await ConnectProducerAsync(args.ProducerUdsPaths[0], TimeSpan.FromSeconds(10)))
            {
                await producer.SendHelloAsync(80, 24, default);
                await producer.SendOutputAsync("second cycle"u8.ToArray(), default);
                await WaitForAsync(
                    () => app.SnapshotReplicas()[0].ProducerConnected,
                    TimeSpan.FromSeconds(5),
                    "ProducerConnected should flip true again after the second producer dials in.");
            }

            await WaitForAsync(
                () =>
                {
                    var s = app.SnapshotReplicas()[0];
                    return !s.ProducerConnected && s.RestartCount >= 2;
                },
                TimeSpan.FromSeconds(10),
                "After the second producer disconnects, ProducerConnected should clear and RestartCount should reach 2.");

            // Slot itself is still there — IsAlive/ProducerConnected being false
            // is transient, the replica array length is unchanged and the same
            // UDS paths remain in the snapshot.
            var afterCycle2 = app.SnapshotReplicas();
            Assert.Single(afterCycle2);
            Assert.Equal(args.ProducerUdsPaths[0], afterCycle2[0].ProducerUdsPath);
            Assert.Equal(args.ConsumerUdsPaths[0], afterCycle2[0].ConsumerUdsPath);
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ReplicaSnapshotIncludesNewFields()
    {
        // Even before any producer has connected, the snapshot must populate
        // the new fields so older AppHost wire deserialisation never sees a
        // missing-required-property error.
        var (args, tmp, control) = BuildArgs(2);
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            var snap = app.SnapshotReplicas();
            Assert.Equal(2, snap.Length);
            foreach (var r in snap)
            {
                Assert.False(r.ProducerConnected);
                Assert.False(r.IsAlive);
                Assert.Equal(0, r.RestartCount);
                Assert.Null(r.ExitCode);
            }
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// A minimal HMP1 server-role producer for tests. Connects to the UDS path
    /// the terminal host is listening on (producer side) and writes the bare
    /// minimum frames the Hex1bTerminal client expects: a Hello, optional
    /// Output frames, and EOF on dispose.
    /// </summary>
    private sealed class TestHmp1Producer : IAsyncDisposable
    {
        // HMP1 wire format: [type:1B][length:4B LE][payload:N bytes].
        private const byte FrameHello = 0x01;
        private const byte FrameOutput = 0x03;

        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private bool _disposed;

        public TestHmp1Producer(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: true);
        }

        public async Task SendHelloAsync(int width, int height, CancellationToken ct)
        {
            var json = $"{{\"version\":1,\"width\":{width},\"height\":{height}}}";
            await SendFrameAsync(FrameHello, System.Text.Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
        }

        public Task SendOutputAsync(byte[] payload, CancellationToken ct) =>
            SendFrameAsync(FrameOutput, payload, ct);

        private async Task SendFrameAsync(byte type, byte[] payload, CancellationToken ct)
        {
            var header = new byte[5];
            header[0] = type;
            header[1] = (byte)(payload.Length & 0xFF);
            header[2] = (byte)((payload.Length >> 8) & 0xFF);
            header[3] = (byte)((payload.Length >> 16) & 0xFF);
            header[4] = (byte)((payload.Length >> 24) & 0xFF);
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await _stream.WriteAsync(payload, ct).ConfigureAwait(false);
            }
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try { _socket.Shutdown(SocketShutdown.Both); } catch { /* ignore */ }
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<TestHmp1Producer> ConnectProducerAsync(string socketPath, TimeSpan timeout)
    {
        // Retry loop because there is a brief unbound window between recycle
        // iterations on the host side; the test producer should ride through
        // that the same way DCP does in production.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Exception? last = null;
        while (sw.Elapsed < timeout)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
                return new TestHmp1Producer(socket);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                last = ex;
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        throw new TimeoutException(
            $"Timed out connecting to producer UDS '{socketPath}' after {timeout.TotalSeconds:F1}s.", last);
    }

    private static async Task WaitForAsync(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (predicate())
            {
                return;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        throw new TimeoutException($"{failureMessage} (waited {timeout.TotalSeconds:F1}s).");
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
