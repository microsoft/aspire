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
    /// <summary>
    /// Builds a single-replica argument set for the host. Each terminal host process
    /// serves exactly one replica, so the AppHost (and these tests) just hand it one
    /// producer/consumer/control UDS path triple. The replica index is opaque to the
    /// host — callers encode it however they like in the path layout.
    /// </summary>
    private static (TerminalHostArgs args, TestTempDirectory tmp, string controlPath) BuildArgs()
    {
        var tmp = new TestTempDirectory();
        var dcpDir = Path.Combine(tmp.Path, "dcp");
        var hostDir = Path.Combine(tmp.Path, "host");
        var ctrlDir = Path.Combine(tmp.Path, "control");
        Directory.CreateDirectory(dcpDir);
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(ctrlDir);

        var producer = Path.Combine(dcpDir, "r.sock");
        var consumer = Path.Combine(hostDir, "r.sock");
        var control = Path.Combine(ctrlDir, "ctrl.sock");

        var args = TerminalHostArgs.Parse([
            "--producer-uds", producer,
            "--consumer-uds", consumer,
            "--control-uds", control,
        ]);

        return (args, tmp, control);
    }

    [Fact]
    public async Task RunAsyncBindsControlListenerWhenStarted()
    {
        var (args, tmp, control) = BuildArgs();
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
    public async Task ControlEndpointReturnsSessionInfo()
    {
        var (args, tmp, control) = BuildArgs();
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
            var session = await rpc.InvokeAsync<TerminalHostSessionInfo>(
                TerminalHostControlProtocol.GetSessionMethod);

            Assert.Equal(TerminalHostControlProtocol.ProtocolVersion, info.ProtocolVersion);
            Assert.Equal(args.ProducerUdsPath, session.ProducerUdsPath);
            Assert.Equal(args.ConsumerUdsPath, session.ConsumerUdsPath);
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
        var (args, tmp, control) = BuildArgs();
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
    public async Task ConcurrentControlConnectsAreRefusedDownToOne()
    {
        // The control protocol is documented as a single AppHost client (lifecycle,
        // shutdown, stats). Regression for the accept-loop race where two fast
        // back-to-back connects could both observe an empty slot before either
        // ServeClientAsync had registered into _activeRpcs, ending up with two
        // concurrently-served sessions instead of one. The reservation counter
        // increment must happen synchronously under _gate at the accept site.
        var (args, tmp, control) = BuildArgs();
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            // Open many concurrent sockets to maximise the chance of two reaching
            // the accept loop's reservation check at the same time. Whatever the
            // scheduling, exactly one must end up usable for an RPC; the rest
            // must fail (server-closed before the request returns or no response).
            const int concurrency = 8;
            var rawSockets = new Socket[concurrency];
            var connectTasks = new Task[concurrency];
            for (var i = 0; i < concurrency; i++)
            {
                rawSockets[i] = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                connectTasks[i] = rawSockets[i].ConnectAsync(new UnixDomainSocketEndPoint(control));
            }

            try
            {
                await Task.WhenAll(connectTasks).WaitAsync(TimeSpan.FromSeconds(10));

                // Drive an RPC over each socket and count the survivors. Refused
                // sockets either close immediately (read returns 0) or fail the
                // header-delimited read; the StreamJsonRpc completion task surfaces
                // either as a ConnectionLostException / IOException.
                var results = await Task.WhenAll(rawSockets.Select(TryGetInfoAsync));

                var successes = results.Count(static r => r);
                Assert.Equal(1, successes);
            }
            finally
            {
                foreach (var s in rawSockets)
                {
                    try { s.Dispose(); } catch { }
                }
            }
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }

        static async Task<bool> TryGetInfoAsync(Socket socket)
        {
            // Each candidate gets a short window to either ack a GetInfo or be
            // refused. The refused side observes a zero-length read on its
            // NetworkStream which StreamJsonRpc raises as a ConnectionLostException.
            try
            {
                var stream = new NetworkStream(socket, ownsSocket: false);
                using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter()));
                rpc.StartListening();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                _ = await rpc.InvokeAsync<TerminalHostInfoResponse>(TerminalHostControlProtocol.GetInfoMethod).WaitAsync(cts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    [Fact]
    public async Task SnapshotSessionReportsConfiguredPaths()
    {
        var (args, tmp, control) = BuildArgs();
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            var snap = app.SnapshotSession();
            Assert.Equal(args.ProducerUdsPath, snap.ProducerUdsPath);
            Assert.Equal(args.ConsumerUdsPath, snap.ConsumerUdsPath);
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
    public async Task HostStartsCleanlyWithStaleProducerAndConsumerSocketFiles()
    {
        // Regression: when a previous host crashes (or DCP kills the process before it
        // can gracefully unlink its UDS), the next host run hits "address already in use"
        // on Bind unless we explicitly pre-delete the path. Hex1b doesn't do this for us
        // because it doesn't know the path was previously bound by an Aspire host vs
        // some other process. Symmetry with TerminalHostControlListener (which has
        // always pre-deleted its control path).
        if (OperatingSystem.IsWindows())
        {
            // UDS files on Windows behave differently — File.Create at the path doesn't
            // produce a regular file that EADDRINUSEs the next bind, so the scenario
            // doesn't reproduce. The pre-delete still runs as defensive code.
            return;
        }

        var (args, tmp, control) = BuildArgs();
        using var disp = tmp;

        // Pre-place leftovers exactly as a crashed previous host would have left them.
        File.WriteAllBytes(args.ProducerUdsPath, []);
        File.WriteAllBytes(args.ConsumerUdsPath, []);
        Assert.True(File.Exists(args.ProducerUdsPath));
        Assert.True(File.Exists(args.ConsumerUdsPath));

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            // If pre-delete is missing, Bind fails inside the recycle loop and the
            // control listener may still come up (it pre-deletes its own path), but a
            // real producer connect would never succeed. Use the producer dial as the
            // end-to-end signal that both UDS paths are usable.
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            await using var producer = await ConnectProducerAsync(args.ProducerUdsPath, TimeSpan.FromSeconds(10));
            await producer.SendHelloAsync(80, 24, default);
            await WaitForAsync(
                () => app.SnapshotSession().ProducerConnected,
                TimeSpan.FromSeconds(5),
                "Producer should connect after stale UDS files are pre-cleaned.");
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SessionRecyclesAfterProducerDisconnect()
    {
        // End-to-end check of the recycle loop: the host should stay running
        // across a producer disconnect and accept a fresh producer on the
        // same UDS path, with ProducerConnected and RestartCount tracking
        // each cycle. This exercises the path DCP exercises in production
        // when the underlying process exits and gets relaunched.
        var (args, tmp, control) = BuildArgs();
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            // Initial state: host is up but no producer has dialed in yet.
            var initial = app.SnapshotSession();
            Assert.False(initial.ProducerConnected, "Session should report no producer before any connect.");
            Assert.False(initial.IsAlive, "Legacy IsAlive should mirror ProducerConnected.");
            Assert.Equal(0, initial.RestartCount);

            // Cycle 1: connect, get accepted, disconnect.
            await using (var producer = await ConnectProducerAsync(args.ProducerUdsPath, TimeSpan.FromSeconds(5)))
            {
                await producer.SendHelloAsync(80, 24, default);
                await producer.SendOutputAsync("first cycle"u8.ToArray(), default);
                await WaitForAsync(
                    () => app.SnapshotSession().ProducerConnected,
                    TimeSpan.FromSeconds(5),
                    "ProducerConnected should flip to true after producer dials in.");
            }

            await WaitForAsync(
                () =>
                {
                    var s = app.SnapshotSession();
                    return !s.ProducerConnected && s.RestartCount >= 1;
                },
                TimeSpan.FromSeconds(10),
                "After producer disconnect, ProducerConnected should clear and RestartCount should advance.");

            var afterCycle1 = app.SnapshotSession();
            Assert.Equal(1, afterCycle1.RestartCount);

            // Cycle 2: a fresh producer should be able to dial the same UDS path.
            // This is the critical DCP-restart scenario.
            await using (var producer = await ConnectProducerAsync(args.ProducerUdsPath, TimeSpan.FromSeconds(10)))
            {
                await producer.SendHelloAsync(80, 24, default);
                await producer.SendOutputAsync("second cycle"u8.ToArray(), default);
                await WaitForAsync(
                    () => app.SnapshotSession().ProducerConnected,
                    TimeSpan.FromSeconds(5),
                    "ProducerConnected should flip true again after the second producer dials in.");
            }

            await WaitForAsync(
                () =>
                {
                    var s = app.SnapshotSession();
                    return !s.ProducerConnected && s.RestartCount >= 2;
                },
                TimeSpan.FromSeconds(10),
                "After the second producer disconnects, ProducerConnected should clear and RestartCount should reach 2.");

            // Session itself is still there — IsAlive/ProducerConnected being false
            // is transient, the snapshot continues to report the same UDS paths.
            var afterCycle2 = app.SnapshotSession();
            Assert.Equal(args.ProducerUdsPath, afterCycle2.ProducerUdsPath);
            Assert.Equal(args.ConsumerUdsPath, afterCycle2.ConsumerUdsPath);
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task SessionSnapshotIncludesNewFields()
    {
        // Even before any producer has connected, the snapshot must populate
        // the new fields so older AppHost wire deserialisation never sees a
        // missing-required-property error.
        var (args, tmp, control) = BuildArgs();
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            var snap = app.SnapshotSession();
            Assert.False(snap.ProducerConnected);
            Assert.False(snap.IsAlive);
            Assert.Equal(0, snap.RestartCount);
            Assert.Null(snap.ExitCode);
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task DownstreamPrimaryResizeIsForwardedUpstreamAsRawResizeFrame()
    {
        // Regression: the consumer-side multi-head server fires its OnResized
        // event whenever the current primary peer changes the producer dims
        // (RequestPrimary or explicit Resize from primary). The terminal host
        // bridges that event to a raw HMP1 FrameResize (0x05) on the upstream
        // (DCP-facing) connection — bypassing Hex1b's stock Hmp1WorkloadAdapter
        // IsPrimary gate, which would silently drop every resize because DCP's
        // minimal HMP1 server never sends Hello.PrimaryPeerId or RoleChange.
        // Without this bridge, the underlying PTY stayed at its DCP-initial
        // dims forever.
        var (args, tmp, control) = BuildArgs();
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            // Stand up the upstream "DCP" first. Send Hello so the host's
            // upstream-side adapter handshakes successfully and ProducerConnected
            // flips before we attempt the consumer-side connect — otherwise the
            // resize broadcast would race the upstream stream's existence.
            await using var producer = await ConnectProducerAsync(args.ProducerUdsPath, TimeSpan.FromSeconds(5));
            await producer.SendHelloAsync(80, 24, default);

            await WaitForAsync(
                () => app.SnapshotSession().ProducerConnected,
                TimeSpan.FromSeconds(5),
                "ProducerConnected should flip to true after producer dials in.");

            // Wait for the consumer-side UDS server to bind so the dial below
            // doesn't race a not-yet-listening socket.
            await WaitForFileAsync(args.ConsumerUdsPath, TimeSpan.FromSeconds(5));

            // Now connect a minimal raw-frame HMP1 client to the consumer UDS:
            // ClientHello + RequestPrimary, then keep the stream open. We don't
            // need a full Hex1bTerminal because all we're verifying is that the
            // server-side OnResized event (which fires when RequestPrimary
            // promotes us and applies the requested dims) is bridged upstream.
            const int requestedWidth = 123;
            const int requestedHeight = 45;

            await using var consumer = await TestHmp1Consumer.ConnectAsync(
                args.ConsumerUdsPath, TimeSpan.FromSeconds(5));
            await consumer.SendClientHelloAsync("test-consumer", "primary", default);
            await consumer.SendRequestPrimaryAsync(requestedWidth, requestedHeight, default);

            // The frame the test producer should observe upstream:
            // [type=0x05 Resize][length=8 LE][width:4B LE][height:4B LE]
            //
            // Use the predicate variant because the host's Hex1bTerminal may
            // emit additional resize frames upstream during startup or in
            // response to incoming Hello dims (the consumer-side server's
            // Resized event also triggers Hex1bTerminal's own
            // workload.ResizeAsync). We only care that SOME FrameResize lands
            // upstream with the dims the consumer requested via RequestPrimary.
            const byte FrameResize = 0x05;
            var payload = await producer.WaitForMatchingFrameAsync(
                FrameResize,
                p =>
                {
                    if (p.Length != 8)
                    {
                        return false;
                    }
                    var w = p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24);
                    var h = p[4] | (p[5] << 8) | (p[6] << 16) | (p[7] << 24);
                    return w == requestedWidth && h == requestedHeight;
                },
                TimeSpan.FromSeconds(10));

            // Sanity: payload encodes exactly the requested dims (defensive
            // — the predicate already filtered, but keeps the assertion intent
            // explicit on the test surface).
            Assert.Equal(8, payload.Length);
            var observedWidth = payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24);
            var observedHeight = payload[4] | (payload[5] << 8) | (payload[6] << 16) | (payload[7] << 24);
            Assert.Equal(requestedWidth, observedWidth);
            Assert.Equal(requestedHeight, observedHeight);
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

        /// <summary>
        /// Drains HMP1 frames until a frame of the requested type whose
        /// payload passes the <paramref name="match"/> predicate is found.
        /// Useful when upstream may receive multiple frames of the same type
        /// from different sources (e.g., terminal startup resize vs explicit
        /// user resize).
        /// </summary>
        public async Task<byte[]> WaitForMatchingFrameAsync(
            byte expectedType, Func<byte[], bool> match, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            while (true)
            {
                var (type, payload) = await ReadFrameAsync(cts.Token).ConfigureAwait(false);
                if (type == expectedType && match(payload))
                {
                    return payload;
                }
            }
        }

        /// <summary>Reads exactly one HMP1 frame and returns its (type, payload).</summary>
        public async Task<(byte Type, byte[] Payload)> ReadFrameAsync(CancellationToken ct)
        {
            var header = new byte[5];
            await ReadExactlyAsync(header, ct).ConfigureAwait(false);

            var type = header[0];
            var length = (uint)(header[1] | (header[2] << 8) | (header[3] << 16) | (header[4] << 24));
            if (length > 16 * 1024 * 1024)
            {
                throw new InvalidOperationException($"Producer-side reader: frame length {length} exceeds 16MB cap.");
            }

            var payload = new byte[length];
            if (length > 0)
            {
                await ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            }
            return (type, payload);
        }

        private async Task ReadExactlyAsync(byte[] buffer, CancellationToken ct)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"Producer-side reader: stream EOF after {offset} of {buffer.Length} bytes.");
                }
                offset += read;
            }
        }

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

    /// <summary>
    /// A minimal HMP1 client-role consumer for tests. Connects to the consumer
    /// UDS the terminal host is listening on, then writes raw HMP1 frames
    /// (ClientHello, RequestPrimary). Avoids spinning up a full
    /// <c>Hex1bTerminal</c> in a test process where no interactive console is
    /// attached.
    /// </summary>
    private sealed class TestHmp1Consumer : IAsyncDisposable
    {
        private const byte FrameRequestPrimary = 0x07;
        private const byte FrameClientHello = 0x0B;

        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private bool _disposed;

        private TestHmp1Consumer(Socket socket)
        {
            _socket = socket;
            _stream = new NetworkStream(socket, ownsSocket: true);
        }

        public static async Task<TestHmp1Consumer> ConnectAsync(string socketPath, TimeSpan timeout)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Exception? last = null;
            while (sw.Elapsed < timeout)
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).ConfigureAwait(false);
                    return new TestHmp1Consumer(socket);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    last = ex;
                    await Task.Delay(50).ConfigureAwait(false);
                }
            }
            throw new TimeoutException(
                $"Timed out connecting to consumer UDS '{socketPath}' after {timeout.TotalSeconds:F1}s.", last);
        }

        public async Task SendClientHelloAsync(string displayName, string defaultRole, CancellationToken ct)
        {
            // JSON keys are camelCase per Hmp1JsonContext.PropertyNamingPolicy.
            // Roles on the wire are the lowercase strings "primary" / "secondary".
            var json = $"{{\"displayName\":\"{displayName}\",\"defaultRole\":\"{defaultRole}\"}}";
            await SendFrameAsync(FrameClientHello, System.Text.Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
        }

        public async Task SendRequestPrimaryAsync(int cols, int rows, CancellationToken ct)
        {
            var json = $"{{\"cols\":{cols},\"rows\":{rows}}}";
            await SendFrameAsync(FrameRequestPrimary, System.Text.Encoding.UTF8.GetBytes(json), ct).ConfigureAwait(false);
        }

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

    [Fact]
    public async Task ControlSocketIsRestrictedToOwningUser()
    {
        // Skipped on Windows because UnixFileMode is not supported there; the
        // listener intentionally no-ops on Windows for the same reason.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (args, tmp, control) = BuildArgs();
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            await WaitForFileAsync(control, TimeSpan.FromSeconds(10));

            // CSWSH/local-DoS defense: the control socket must be 0600 (UserRead|
            // UserWrite). Anything broader allows a local user to dial it and call
            // ShutdownAsync (no auth) or GetSessionAsync (leaks peer DisplayNames).
            var mode = File.GetUnixFileMode(control);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task ProducerAndConsumerSocketsAreRestrictedToOwningUser()
    {
        // Defense-in-depth (the parent ~/.aspire/trmnl/ dir is already 0700, but per-file
        // 0600 matches what the control socket does and protects in case the dir's perms
        // somehow get relaxed by a future change). The chmod is applied by
        // TerminalReplica.ApplyRestrictiveSocketPermissionsAsync after Hex1b binds.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (args, tmp, _) = BuildArgs();
        using var disp = tmp;

        await using var app = new TerminalHostApp(args, NullLoggerFactory.Instance);
        using var hostCts = new CancellationTokenSource();
        var hostTask = app.RunAsync(hostCts.Token);

        try
        {
            // The producer socket is bound as soon as RunAsync starts; the consumer
            // socket is bound once Hex1bTerminal's HMP1 server initialises (also during
            // RunAsync). The post-bind chmod helper polls for file existence and is
            // best-effort, so we allow up to a few seconds.
            await WaitForFileAsync(args.ProducerUdsPath, TimeSpan.FromSeconds(10));
            await WaitForFileAsync(args.ConsumerUdsPath, TimeSpan.FromSeconds(10));

            await WaitForUnixFileModeAsync(
                args.ProducerUdsPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                TimeSpan.FromSeconds(5));
            await WaitForUnixFileModeAsync(
                args.ConsumerUdsPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                TimeSpan.FromSeconds(5));
        }
        finally
        {
            app.RequestShutdown();
            hostCts.Cancel();
            await hostTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task WaitForUnixFileModeAsync(string path, UnixFileMode expected, TimeSpan timeout)
    {
        // Callers MUST guard this with !OperatingSystem.IsWindows(); the early-return
        // here is purely so the analyzer accepts File.GetUnixFileMode below.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The chmod is applied from a background task in TerminalReplica after the
        // socket binds (Hex1b binds lazily inside RunAsync), so the window between
        // "file exists" and "file has 0600 perms" is observable from the outside.
        // Poll briefly until we see the expected mode.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        UnixFileMode last = default;
        while (sw.Elapsed < timeout)
        {
            try
            {
                last = File.GetUnixFileMode(path);
                if (last == expected)
                {
                    return;
                }
            }
            catch (FileNotFoundException)
            {
                // Socket recycled mid-poll.
            }
            await Task.Delay(50);
        }

        throw new TimeoutException(
            $"Expected '{path}' to have mode {expected} within {timeout.TotalSeconds:F1}s; last observed {last}.");
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
