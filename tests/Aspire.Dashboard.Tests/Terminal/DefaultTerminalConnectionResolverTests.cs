// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Terminal;
using Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Xunit;
using Value = Google.Protobuf.WellKnownTypes.Value;

namespace Aspire.Dashboard.Tests.Terminal;

public class DefaultTerminalConnectionResolverTests
{
    [Fact]
    public async Task ConnectAsync_WhenClientNotEnabled_ReturnsNull()
    {
        var client = new DisabledDashboardClient();
        var resolver = new DefaultTerminalConnectionResolver(client);

        var stream = await resolver.ConnectAsync("anything", CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task ConnectAsync_WhenResourceNotFound_ReturnsNull()
    {
        var client = new MockDashboardClient(resources:
        [
            CreateTerminalResource("other-abc", displayName: "other", udsPath: "/tmp/other-r0.sock"),
        ]);
        var resolver = new DefaultTerminalConnectionResolver(client);

        var stream = await resolver.ConnectAsync("missing", CancellationToken.None);

        Assert.Null(stream);
    }

    [Theory]
    [InlineData("svc")]
    [InlineData("svc-missing")]
    public async Task ConnectAsync_WhenInstanceNameDoesNotMatch_ReturnsNull(string resourceName)
    {
        var client = new MockDashboardClient(resources:
        [
            CreateTerminalResource("svc-abc", displayName: "svc", udsPath: "/tmp/svc-r0.sock"),
            CreateTerminalResource("svc-def", displayName: "svc", udsPath: "/tmp/svc-r1.sock"),
        ]);
        var resolver = new DefaultTerminalConnectionResolver(client);

        var stream = await resolver.ConnectAsync(resourceName, CancellationToken.None);

        Assert.Null(stream);
    }

    [Theory]
    [InlineData("svc-abc")]
    [InlineData("svc-def")]
    public async Task ConnectAsync_UsesExactInstanceWithoutReplicaMetadata(string resourceName)
    {
        var directory = Directory.CreateTempSubdirectory("aspire-term-");
        try
        {
            var socketPath = Path.Combine(directory.FullName, "terminal.sock");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var client = new MockDashboardClient(resources:
            [
                CreateTerminalResource("svc-abc", displayName: "svc",
                    udsPath: resourceName == "svc-abc" ? socketPath : Path.Combine(directory.FullName, "other.sock")),
                CreateTerminalResource("svc-def", displayName: "svc",
                    udsPath: resourceName == "svc-def" ? socketPath : Path.Combine(directory.FullName, "other.sock"))
            ]);
            var resolver = new DefaultTerminalConnectionResolver(client);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await using var stream = await resolver.ConnectAsync(resourceName, timeout.Token);
            Assert.NotNull(stream);
            using var connection = await listener.AcceptAsync(timeout.Token);
            await stream.WriteAsync(new byte[] { 42 }, timeout.Token);
            var buffer = new byte[1];
            Assert.Equal(1, await connection.ReceiveAsync(buffer, SocketFlags.None, timeout.Token));
            Assert.Equal(42, buffer[0]);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ConnectAsync_WhenSnapshotMissingTerminalEnabledMarker_ReturnsNull()
    {
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "svc-abc",
            displayName: "svc",
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, "0"),
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "1"),
                [KnownProperties.Terminal.ConsumerUdsPath] = StringProperty(KnownProperties.Terminal.ConsumerUdsPath, "/tmp/svc.sock"),
            });
        var client = new MockDashboardClient(resources: [resource]);
        var resolver = new DefaultTerminalConnectionResolver(client);

        var stream = await resolver.ConnectAsync("svc-abc", CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task ConnectAsync_WhenSnapshotMissingConsumerUdsPath_ReturnsNull()
    {
        var resource = ModelTestHelpers.CreateResource(
            resourceName: "svc-abc",
            displayName: "svc",
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
                [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, "0"),
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, "1"),
            });
        var client = new MockDashboardClient(resources: [resource]);
        var resolver = new DefaultTerminalConnectionResolver(client);

        var stream = await resolver.ConnectAsync("svc-abc", CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task ConnectAsync_WhenUdsPathDoesNotExist_FailsToConnect()
    {
        // Resolver locates the snapshot and the path; the actual UDS connect throws
        // because the path is not a live socket. We just assert that the resolver
        // attempted the connection — transport errors bubble up to the WS proxy.
        var directory = Directory.CreateTempSubdirectory("aspire-term-");
        try
        {
            var resource = CreateTerminalResource(
                resourceName: "svc-abc",
                displayName: "svc",
                udsPath: Path.Combine(directory.FullName, "nonexistent.sock"));
            var client = new MockDashboardClient(resources: [resource]);
            var resolver = new DefaultTerminalConnectionResolver(client);

            await Assert.ThrowsAnyAsync<Exception>(() => resolver.ConnectAsync("svc-abc", CancellationToken.None));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ResourceViewModel CreateTerminalResource(string resourceName, string displayName, string udsPath)
    {
        return ModelTestHelpers.CreateResource(
            resourceName: resourceName,
            displayName: displayName,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
                [KnownProperties.Terminal.ConsumerUdsPath] = StringProperty(KnownProperties.Terminal.ConsumerUdsPath, udsPath),
            });
    }

    private static ResourcePropertyViewModel StringProperty(string name, string value)
    {
        return new ResourcePropertyViewModel(
            name,
            new Value { StringValue = value },
            isValueSensitive: false,
            knownProperty: null,
            sortOrder: 0,
            displayName: null,
            isHighlighted: false);
    }

    private sealed class DisabledDashboardClient : IDashboardClient
    {
        public bool IsEnabled => false;
        public Task WhenConnected => Task.CompletedTask;
        public Task WhenResourcesReady => Task.CompletedTask;
        public string ApplicationName => "Disabled";
        public string? MinRequiredVersion => null;
        public DashboardConnectionState ConnectionState => DashboardConnectionState.Connected;
#pragma warning disable CS0067 // Event is never used - required by interface
        public event Action<DashboardConnectionState>? ConnectionStateChanged;
#pragma warning restore CS0067
        public Task ReconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, int interactionId, string inputName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<Stream> AttachTerminalAsync(string terminalId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<WatchTerminalsUpdate> SubscribeTerminalsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task CloseTerminalAsync(string terminalId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ClearConsoleLogsAsync(IReadOnlyList<string> resourceNames, DateTime clearDate) => Task.CompletedTask;
        public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResourceViewModel? GetResource(string resourceName) => null;
        public IReadOnlyList<ResourceViewModel> GetResources() => [];
    }
}
