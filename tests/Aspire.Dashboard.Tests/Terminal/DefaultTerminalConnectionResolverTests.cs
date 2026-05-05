// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

        var stream = await resolver.ConnectAsync("anything", 0, CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task ConnectAsync_WhenResourceNotFound_ReturnsNull()
    {
        var client = new MockDashboardClient(resources:
        [
            CreateTerminalResource("other-abc", displayName: "other", replicaIndex: 0, replicaCount: 1, udsPath: "/tmp/other-r0.sock"),
        ]);
        var resolver = new DefaultTerminalConnectionResolver(client);

        var stream = await resolver.ConnectAsync("missing", 0, CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task ConnectAsync_WhenReplicaIndexDoesNotMatch_ReturnsNull()
    {
        var client = new MockDashboardClient(resources:
        [
            CreateTerminalResource("svc-abc", displayName: "svc", replicaIndex: 0, replicaCount: 2, udsPath: "/tmp/svc-r0.sock"),
            CreateTerminalResource("svc-def", displayName: "svc", replicaIndex: 1, replicaCount: 2, udsPath: "/tmp/svc-r1.sock"),
        ]);
        var resolver = new DefaultTerminalConnectionResolver(client);

        var stream = await resolver.ConnectAsync("svc", 5, CancellationToken.None);

        Assert.Null(stream);
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

        var stream = await resolver.ConnectAsync("svc", 0, CancellationToken.None);

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

        var stream = await resolver.ConnectAsync("svc", 0, CancellationToken.None);

        Assert.Null(stream);
    }

    [Fact]
    public async Task ConnectAsync_WhenUdsPathDoesNotExist_FailsToConnect()
    {
        // Resolver locates the snapshot and the path; the actual UDS connect throws
        // because the path is not a live socket. We just assert that the resolver
        // attempted the connection — transport errors bubble up to the WS proxy.
        var resource = CreateTerminalResource(
            resourceName: "svc-abc",
            displayName: "svc",
            replicaIndex: 0,
            replicaCount: 1,
            udsPath: Path.Combine(Path.GetTempPath(), "nonexistent-aspire-term-" + Guid.NewGuid().ToString("N") + ".sock"));
        var client = new MockDashboardClient(resources: [resource]);
        var resolver = new DefaultTerminalConnectionResolver(client);

        await Assert.ThrowsAnyAsync<Exception>(() => resolver.ConnectAsync("svc", 0, CancellationToken.None));
    }

    private static ResourceViewModel CreateTerminalResource(string resourceName, string displayName, int replicaIndex, int replicaCount, string udsPath)
    {
        return ModelTestHelpers.CreateResource(
            resourceName: resourceName,
            displayName: displayName,
            properties: new Dictionary<string, ResourcePropertyViewModel>
            {
                [KnownProperties.Terminal.Enabled] = StringProperty(KnownProperties.Terminal.Enabled, "true"),
                [KnownProperties.Terminal.ReplicaIndex] = StringProperty(KnownProperties.Terminal.ReplicaIndex, replicaIndex.ToString()),
                [KnownProperties.Terminal.ReplicaCount] = StringProperty(KnownProperties.Terminal.ReplicaCount, replicaCount.ToString()),
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
            priority: 0);
    }

    private sealed class DisabledDashboardClient : IDashboardClient
    {
        public bool IsEnabled => false;
        public Task WhenConnected => Task.CompletedTask;
        public string ApplicationName => "Disabled";
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResourceViewModel? GetResource(string resourceName) => null;
        public IReadOnlyList<ResourceViewModel> GetResources() => [];
    }
}
