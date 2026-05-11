// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Backchannel;
using Aspire.Shared.TerminalHost;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StreamJsonRpc;

namespace Aspire.Hosting.Tests.Backchannel;

[Trait("Partition", "4")]
public class GetTerminalInfoAsyncTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _toDispose = [];
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task ReturnsUnavailable_WhenResourceDoesNotExist()
    {
        var (model, _) = BuildModel(replicaCount: 1, controlListeners: null);

        var target = CreateTarget(model);

        var result = await target.GetTerminalInfoAsync(
            new GetTerminalInfoRequest { ResourceName = "nope" }).DefaultTimeout();

        Assert.False(result.IsAvailable);
        Assert.Null(result.Replicas);
    }

    [Fact]
    public async Task ReturnsUnavailable_WhenResourceHasNoTerminalAnnotation()
    {
        var model = new DistributedApplicationModel(new ResourceCollection
        {
            new CustomResource("plain"),
        });

        var target = CreateTarget(model);

        var result = await target.GetTerminalInfoAsync(
            new GetTerminalInfoRequest { ResourceName = "plain" }).DefaultTimeout();

        Assert.False(result.IsAvailable);
        Assert.Null(result.Replicas);
    }

    [Fact]
    public async Task ReturnsAvailableWithDegradedReplicas_WhenAllControlSocketsAreUnreachable()
    {
        // Build a layout pointing at control sockets nobody is listening on. The new
        // fan-out model treats each per-replica host independently — every host's
        // control RPC will time out, but the call still returns IsAvailable=true with
        // one degraded TerminalReplicaInfo per replica (IsAlive=false, AppHost-known
        // ConsumerUdsPath populated). This mirrors the user-visible behavior in
        // `aspire terminal ps` where the row still appears for each replica even
        // when its host hasn't started yet.
        var (model, hosts) = BuildModel(replicaCount: 2, controlListeners: null);

        var target = CreateTarget(model);

        var result = await target.GetTerminalInfoAsync(
            new GetTerminalInfoRequest { ResourceName = "myapp" }).DefaultTimeout(TimeSpan.FromSeconds(15));

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Replicas);
        Assert.Equal(2, result.Replicas!.Length);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(i, result.Replicas[i].ReplicaIndex);
            Assert.False(result.Replicas[i].IsAlive);
            Assert.Equal(hosts[i].Layout.ConsumerUdsPath, result.Replicas[i].ConsumerUdsPath);
        }
    }

    [Fact]
    public async Task ReturnsPerReplicaInfo_WhenAllHostsReachable()
    {
        // Each per-replica terminal host serves a single session. The AppHost's
        // GetTerminalInfo fans out across them in parallel and assembles the
        // per-replica response array. ReplicaIndex on the wire comes from the
        // AppHost's layout (TerminalHostLayout.ParentReplicaIndex), not from the
        // host's reply, so a misbehaving host can never confuse the AppHost's view.
        var fakeHost0 = await StartFakeControlHostAsync(new TerminalHostSessionInfo
        {
            ProducerUdsPath = "host-claim-p0",
            ConsumerUdsPath = "host-claim-r0",
            IsAlive = true,
            ProducerConnected = true,
        }).DefaultTimeout();

        var fakeHost1 = await StartFakeControlHostAsync(new TerminalHostSessionInfo
        {
            ProducerUdsPath = "host-claim-p1",
            ConsumerUdsPath = "host-claim-r1",
            IsAlive = false,
            ExitCode = 7,
        }).DefaultTimeout();

        var (model, hosts) = BuildModel(
            replicaCount: 2,
            controlListeners: [fakeHost0, fakeHost1]);

        var target = CreateTarget(model);

        var result = await target.GetTerminalInfoAsync(
            new GetTerminalInfoRequest { ResourceName = "myapp" }).DefaultTimeout(TimeSpan.FromSeconds(10));

        Assert.True(result.IsAvailable);
        Assert.Equal(132, result.Columns);
        Assert.Equal(40, result.Rows);
        Assert.Null(result.SocketPath);

        Assert.NotNull(result.Replicas);
        Assert.Equal(2, result.Replicas!.Length);

        Assert.Equal(0, result.Replicas[0].ReplicaIndex);
        Assert.Equal("replica 0", result.Replicas[0].Label);
        // AppHost is the source of truth for the consumer UDS path even though the host
        // echoed back its own claim — verify we trust the layout.
        Assert.Equal(hosts[0].Layout.ConsumerUdsPath, result.Replicas[0].ConsumerUdsPath);
        Assert.True(result.Replicas[0].IsAlive);
        Assert.Null(result.Replicas[0].ExitCode);

        Assert.Equal(1, result.Replicas[1].ReplicaIndex);
        Assert.Equal("replica 1", result.Replicas[1].Label);
        Assert.Equal(hosts[1].Layout.ConsumerUdsPath, result.Replicas[1].ConsumerUdsPath);
        Assert.False(result.Replicas[1].IsAlive);
        Assert.Equal(7, result.Replicas[1].ExitCode);
    }

    [Fact]
    public async Task DegradedReplicaReportedWhenSomeHostsUnreachable()
    {
        // Mixed scenario: replica 0's host is reachable, replica 1's is not.
        // Both show up in the result; replica 1 has IsAlive=false but the
        // AppHost-known consumer path is still reported so the row stays visible.
        var fakeHost0 = await StartFakeControlHostAsync(new TerminalHostSessionInfo
        {
            ProducerUdsPath = "host-claim-p0",
            ConsumerUdsPath = "host-claim-r0",
            IsAlive = true,
            ProducerConnected = true,
        }).DefaultTimeout();

        var (model, hosts) = BuildModel(
            replicaCount: 2,
            controlListeners: [fakeHost0, null]);

        var target = CreateTarget(model);

        var result = await target.GetTerminalInfoAsync(
            new GetTerminalInfoRequest { ResourceName = "myapp" }).DefaultTimeout(TimeSpan.FromSeconds(15));

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Replicas);
        Assert.Equal(2, result.Replicas!.Length);
        Assert.True(result.Replicas[0].IsAlive);
        Assert.False(result.Replicas[1].IsAlive);
        Assert.Equal(hosts[0].Layout.ConsumerUdsPath, result.Replicas[0].ConsumerUdsPath);
        Assert.Equal(hosts[1].Layout.ConsumerUdsPath, result.Replicas[1].ConsumerUdsPath);
    }

    [Fact]
    public async Task GetCapabilities_AdvertisesTerminalsV1()
    {
        var model = new DistributedApplicationModel(new ResourceCollection());
        var target = CreateTarget(model);

        var result = await target.GetCapabilitiesAsync().DefaultTimeout();

        Assert.Contains(AuxiliaryBackchannelCapabilities.V1, result.Capabilities);
        Assert.Contains(AuxiliaryBackchannelCapabilities.V2, result.Capabilities);
        Assert.Contains(AuxiliaryBackchannelCapabilities.Terminals_V1, result.Capabilities);
    }

    /// <summary>
    /// Builds a target resource with a <see cref="TerminalAnnotation"/> wired to one
    /// per-replica <see cref="TerminalHostResource"/> per replica. <paramref name="controlListeners"/>
    /// is matched by index against the hosts: a non-null entry points the corresponding layout
    /// at that fake host's listening UDS, a null entry leaves the layout pointing at a path
    /// nobody is listening on (so the per-host RPC degrades gracefully).
    /// </summary>
    private (DistributedApplicationModel Model, IReadOnlyList<TerminalHostResource> Hosts) BuildModel(
        int replicaCount,
        IReadOnlyList<FakeControlHost?>? controlListeners)
    {
        var baseDir = CreateShortTempDir();

        var target = new CustomResource("myapp");
        var hosts = new TerminalHostResource[replicaCount];
        for (var i = 0; i < replicaCount; i++)
        {
            var perReplicaDir = Path.Combine(baseDir, i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(perReplicaDir);

            var producer = Path.Combine(perReplicaDir, "dcp.sock");
            var consumer = Path.Combine(perReplicaDir, "host.sock");
            // If a fake host is supplied at this index, use its real listening path so
            // the AppHost-side fan-out actually reaches it. Otherwise leave it pointing
            // at a path that does not exist.
            var control = controlListeners is not null && i < controlListeners.Count && controlListeners[i] is { } listener
                ? listener.SocketPath
                : Path.Combine(perReplicaDir, "control.sock");

            var layout = new TerminalHostLayout(
                baseDirectory: baseDir,
                parentReplicaIndex: i,
                producerUdsPath: producer,
                consumerUdsPath: consumer,
                controlUdsPath: control);

            hosts[i] = new TerminalHostResource($"myapp-terminalhost-{i}", target, layout);
        }

        var annotation = new TerminalAnnotation(new TerminalOptions { Columns = 132, Rows = 40 });
        annotation.Initialize(baseDir, hosts);
        target.Annotations.Add(annotation);

        var resources = new ResourceCollection { target };
        foreach (var h in hosts)
        {
            resources.Add(h);
        }

        return (new DistributedApplicationModel(resources), hosts);
    }

    private static AuxiliaryBackchannelRpcTarget CreateTarget(DistributedApplicationModel model)
    {
        var services = new ServiceCollection();
        services.AddSingleton(model);
        var sp = services.BuildServiceProvider();
        return new AuxiliaryBackchannelRpcTarget(NullLogger<AuxiliaryBackchannelRpcTarget>.Instance, sp);
    }

    private async Task<FakeControlHost> StartFakeControlHostAsync(TerminalHostSessionInfo session)
    {
        var dir = CreateShortTempDir();
        var socketPath = Path.Combine(dir, "ctrl.sock");
        var host = new FakeControlHost(socketPath, session);
        await host.StartAsync().ConfigureAwait(false);
        _toDispose.Add(host);
        return host;
    }

    private string CreateShortTempDir()
    {
        // Windows has a 108-byte limit on AF_UNIX paths (and 104 on macOS), and the default
        // %TEMP% can be deep. Allocate a short subdirectory we control.
        var dir = Directory.CreateTempSubdirectory("at-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var d in _toDispose)
        {
            try { await d.DisposeAsync().ConfigureAwait(false); }
            catch { }
        }
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }

    /// <summary>
    /// Stand-in for an <c>aspire.terminalhost</c> process: binds the control UDS and
    /// exposes a single <see cref="TerminalHostControlProtocol.GetSessionMethod"/> that
    /// returns the canned <see cref="TerminalHostSessionInfo"/>.
    /// </summary>
    private sealed class FakeControlHost(string socketPath, TerminalHostSessionInfo session) : IAsyncDisposable
    {
        private Socket? _listenSocket;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoop;
        private readonly List<JsonRpc> _rpcs = [];

        public string SocketPath { get; } = socketPath;

        public Task StartAsync()
        {
            var dir = Path.GetDirectoryName(SocketPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            sock.Bind(new UnixDomainSocketEndPoint(SocketPath));
            sock.Listen(8);
            _listenSocket = sock;

            _cts = new CancellationTokenSource();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                Socket client;
                try
                {
                    client = await _listenSocket!.AcceptAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    var stream = new NetworkStream(client, ownsSocket: true);
                    var formatter = new SystemTextJsonFormatter();
                    var handler = new HeaderDelimitedMessageHandler(stream, stream, formatter);
                    var rpc = new JsonRpc(handler);

                    rpc.AddLocalRpcMethod(
                        TerminalHostControlProtocol.GetSessionMethod,
                        new Func<TerminalHostSessionInfo>(() => session));

                    lock (_rpcs)
                    {
                        _rpcs.Add(rpc);
                    }

                    rpc.StartListening();
                    try { await rpc.Completion.ConfigureAwait(false); }
                    catch { }
                }, ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listenSocket?.Dispose(); } catch { }
            if (_acceptLoop is not null)
            {
                try { await _acceptLoop.ConfigureAwait(false); } catch { }
            }
            lock (_rpcs)
            {
                foreach (var rpc in _rpcs)
                {
                    try { rpc.Dispose(); } catch { }
                }
                _rpcs.Clear();
            }
            try { File.Delete(SocketPath); } catch { }
            _cts?.Dispose();
        }
    }

    private sealed class CustomResource(string name) : Resource(name);
}
