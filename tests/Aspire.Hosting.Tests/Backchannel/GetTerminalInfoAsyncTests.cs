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
        var (model, _) = BuildModel(replicaCount: 1, controlListener: null);

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
    public async Task ReturnsUnavailable_WhenControlSocketIsUnreachable()
    {
        // Build a layout pointing at a control socket nobody is listening on. The retry loop
        // should burn its 3-second budget and report unavailable rather than throw.
        var (model, _) = BuildModel(replicaCount: 2, controlListener: null);

        var target = CreateTarget(model);

        var result = await target.GetTerminalInfoAsync(
            new GetTerminalInfoRequest { ResourceName = "myapp" }).DefaultTimeout(TimeSpan.FromSeconds(10));

        Assert.False(result.IsAvailable);
        Assert.Null(result.Replicas);
    }

    [Fact]
    public async Task ReturnsPerReplicaInfo_WhenHostIsReachable()
    {
        var hostReplicas = new[]
        {
            new TerminalHostReplicaInfo
            {
                Index = 0,
                ProducerUdsPath = "ignored-by-apphost",
                ConsumerUdsPath = "host-claim-r0",
                IsAlive = true,
            },
            new TerminalHostReplicaInfo
            {
                Index = 1,
                ProducerUdsPath = "ignored-by-apphost",
                ConsumerUdsPath = "host-claim-r1",
                IsAlive = false,
                ExitCode = 7,
            },
        };
        var fakeHost = await StartFakeControlHostAsync(hostReplicas).DefaultTimeout();

        var (model, layout) = BuildModel(
            replicaCount: 2,
            controlListener: fakeHost);

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
        Assert.Equal(layout.ConsumerUdsPaths[0], result.Replicas[0].ConsumerUdsPath);
        Assert.True(result.Replicas[0].IsAlive);
        Assert.Null(result.Replicas[0].ExitCode);

        Assert.Equal(1, result.Replicas[1].ReplicaIndex);
        Assert.Equal("replica 1", result.Replicas[1].Label);
        Assert.Equal(layout.ConsumerUdsPaths[1], result.Replicas[1].ConsumerUdsPath);
        Assert.False(result.Replicas[1].IsAlive);
        Assert.Equal(7, result.Replicas[1].ExitCode);
    }

    [Fact]
    public async Task SkipsOutOfRangeIndices_FromMisbehavingHost()
    {
        var hostReplicas = new[]
        {
            new TerminalHostReplicaInfo { Index = 0, ProducerUdsPath = "p0", ConsumerUdsPath = "c0", IsAlive = true },
            new TerminalHostReplicaInfo { Index = 99, ProducerUdsPath = "px", ConsumerUdsPath = "cx", IsAlive = true },
            new TerminalHostReplicaInfo { Index = -1, ProducerUdsPath = "py", ConsumerUdsPath = "cy", IsAlive = true },
        };
        var fakeHost = await StartFakeControlHostAsync(hostReplicas).DefaultTimeout();

        var (model, layout) = BuildModel(replicaCount: 1, controlListener: fakeHost);

        var target = CreateTarget(model);

        var result = await target.GetTerminalInfoAsync(
            new GetTerminalInfoRequest { ResourceName = "myapp" }).DefaultTimeout(TimeSpan.FromSeconds(10));

        Assert.True(result.IsAvailable);
        Assert.NotNull(result.Replicas);
        var single = Assert.Single(result.Replicas!);
        Assert.Equal(0, single.ReplicaIndex);
        Assert.Equal(layout.ConsumerUdsPaths[0], single.ConsumerUdsPath);
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

    private (DistributedApplicationModel Model, TerminalHostLayout Layout) BuildModel(
        int replicaCount,
        FakeControlHost? controlListener)
    {
        var baseDir = CreateShortTempDir();
        Directory.CreateDirectory(Path.Combine(baseDir, "dcp"));
        Directory.CreateDirectory(Path.Combine(baseDir, "host"));

        var producer = new string[replicaCount];
        var consumer = new string[replicaCount];
        for (var i = 0; i < replicaCount; i++)
        {
            producer[i] = Path.Combine(baseDir, "dcp", $"r{i}.sock");
            consumer[i] = Path.Combine(baseDir, "host", $"r{i}.sock");
        }

        // If a fake host is supplied, point the layout at its real socket path. Otherwise use
        // a path that does not exist — the retry loop will exhaust its budget and IsAvailable
        // will be false, exercising the unreachable path.
        var controlPath = controlListener?.SocketPath ?? Path.Combine(baseDir, "control.sock");
        var layout = new TerminalHostLayout(baseDir, producer, consumer, controlPath);

        var target = new CustomResource("myapp");
        var host = new TerminalHostResource("myapp-terminalhost", target, layout);
        var annotation = new TerminalAnnotation(host, new TerminalOptions { Columns = 132, Rows = 40 });
        target.Annotations.Add(annotation);

        var model = new DistributedApplicationModel(new ResourceCollection
        {
            target,
            host,
        });

        return (model, layout);
    }

    private static AuxiliaryBackchannelRpcTarget CreateTarget(DistributedApplicationModel model)
    {
        var services = new ServiceCollection();
        services.AddSingleton(model);
        var sp = services.BuildServiceProvider();
        return new AuxiliaryBackchannelRpcTarget(NullLogger<AuxiliaryBackchannelRpcTarget>.Instance, sp);
    }

    private async Task<FakeControlHost> StartFakeControlHostAsync(TerminalHostReplicaInfo[] replicas)
    {
        var dir = CreateShortTempDir();
        var socketPath = Path.Combine(dir, "ctrl.sock");
        var host = new FakeControlHost(socketPath, replicas);
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

    private sealed class FakeControlHost(string socketPath, TerminalHostReplicaInfo[] replicas) : IAsyncDisposable
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
                        TerminalHostControlProtocol.GetReplicasMethod,
                        new Func<TerminalHostReplicasResponse>(() => new TerminalHostReplicasResponse { Replicas = replicas }));

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
