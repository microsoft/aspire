// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Tests.Helpers;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Resource = Aspire.Hosting.ApplicationModel.Resource;

namespace Aspire.Hosting.Tests.Dashboard;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

/// <summary>
/// Guards the resource-snapshot stamping path in <see cref="DashboardServiceData"/>
/// that flows terminal availability and per-replica UDS path from the AppHost model into
/// the dashboard's wire snapshot. This is the only path that tells the dashboard a
/// resource has an attachable terminal and which local socket to dial — a regression
/// here can either hide the terminal UI entirely or, worse, leak the UDS path into the
/// non-sensitive properties surface.
/// </summary>
[Trait("Partition", "3")]
public class DashboardServiceDataTerminalTests
{
    [Fact]
    public async Task Snapshot_WithoutTerminalAnnotation_HasNoTerminalProperties()
    {
        var (data, notifications, resource) = CreateHarness(addTerminal: false);
        using var _ = data;

        await notifications.PublishUpdateAsync(resource, s => s with { State = new ResourceStateSnapshot("Running", null) }).DefaultTimeout();

        var snapshot = await data.WaitForResourceAsync(resource.Name, _ => true).DefaultTimeout();

        Assert.DoesNotContain(snapshot.Properties, p => p.Name.StartsWith("terminal.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_WithTerminal_SingleReplica_StampsAllFourPropertiesAndMarksUdsPathSensitive()
    {
        var (data, notifications, resource) = CreateHarness(
            addTerminal: true,
            replicaCount: 1);
        using var _ = data;

        await notifications.PublishUpdateAsync(resource, s => s with { State = new ResourceStateSnapshot("Running", null) }).DefaultTimeout();

        var snapshot = await data.WaitForResourceAsync(resource.Name, r => HasTerminalProperty(r, "terminal.enabled")).DefaultTimeout();

        var enabled = GetTerminalProperty(snapshot, "terminal.enabled");
        var index = GetTerminalProperty(snapshot, "terminal.replicaIndex");
        var count = GetTerminalProperty(snapshot, "terminal.replicaCount");
        var path = GetTerminalProperty(snapshot, "terminal.consumerUdsPath");

        Assert.Equal("true", enabled.Value.StringValue);
        Assert.False(enabled.IsSensitive);
        Assert.Equal("0", index.Value.StringValue);
        Assert.False(index.IsSensitive);
        Assert.Equal("1", count.Value.StringValue);
        Assert.False(count.IsSensitive);
        // The local socket is what attach actually dials; it must be masked in the
        // dashboard's resource details panel. Regressing this would leak a host-local
        // file path through the non-sensitive properties surface.
        Assert.Equal(GetTerminalHosts(resource)[0].Layout.ConsumerUdsPath, path.Value.StringValue);
        Assert.True(path.IsSensitive);
    }

    [Fact]
    public async Task Snapshot_WithTerminal_MultiReplica_PicksMatchingInstanceIndex()
    {
        // Three-replica resource; the snapshot is published for instance index 2.
        // The stamped properties must point at host 2's UDS, not host 0's.
        var (data, notifications, resource) = CreateHarness(
            addTerminal: true,
            replicaCount: 3,
            dcpInstances: [
                new DcpInstance("myapp-aaa", "aaa", 0),
                new DcpInstance("myapp-bbb", "bbb", 1),
                new DcpInstance("myapp-ccc", "ccc", 2),
            ]);
        using var _ = data;

        await notifications.PublishUpdateAsync(resource, "myapp-ccc", s => s with { State = new ResourceStateSnapshot("Running", null) }).DefaultTimeout();

        var snapshot = await data.WaitForResourceAsync("myapp-ccc", r => HasTerminalProperty(r, "terminal.enabled")).DefaultTimeout();

        Assert.Equal("2", GetTerminalProperty(snapshot, "terminal.replicaIndex").Value.StringValue);
        Assert.Equal("3", GetTerminalProperty(snapshot, "terminal.replicaCount").Value.StringValue);
        Assert.Equal(
            GetTerminalHosts(resource)[2].Layout.ConsumerUdsPath,
            GetTerminalProperty(snapshot, "terminal.consumerUdsPath").Value.StringValue);
    }

    [Fact]
    public async Task Snapshot_WithTerminal_NoDcpInstancesAnnotation_FallsBackToIndexZero()
    {
        // Non-DCP resources (or pre-DCP snapshots) won't have a DcpInstancesAnnotation.
        // ResolveReplicaIndex must fall back to 0 so the snapshot still gets a usable
        // consumer UDS path rather than dropping the path entirely.
        var (data, notifications, resource) = CreateHarness(
            addTerminal: true,
            replicaCount: 2,
            dcpInstances: null);
        using var _ = data;

        await notifications.PublishUpdateAsync(resource, s => s with { State = new ResourceStateSnapshot("Running", null) }).DefaultTimeout();

        var snapshot = await data.WaitForResourceAsync(resource.Name, r => HasTerminalProperty(r, "terminal.enabled")).DefaultTimeout();

        Assert.Equal("0", GetTerminalProperty(snapshot, "terminal.replicaIndex").Value.StringValue);
        Assert.Equal(
            GetTerminalHosts(resource)[0].Layout.ConsumerUdsPath,
            GetTerminalProperty(snapshot, "terminal.consumerUdsPath").Value.StringValue);
    }

    [Fact]
    public async Task Snapshot_WithTerminal_ResourceIdNotInInstances_FallsBackToIndexZero()
    {
        // DcpInstancesAnnotation is present but the resourceId published doesn't match
        // any instance.Name — guard against this returning -1 or throwing, which would
        // either crash snapshot integration or out-of-range the host array.
        var (data, notifications, resource) = CreateHarness(
            addTerminal: true,
            replicaCount: 2,
            dcpInstances: [
                new DcpInstance("myapp-aaa", "aaa", 0),
                new DcpInstance("myapp-bbb", "bbb", 1),
            ]);
        using var _ = data;

        await notifications.PublishUpdateAsync(resource, "myapp-unknown", s => s with { State = new ResourceStateSnapshot("Running", null) }).DefaultTimeout();

        var snapshot = await data.WaitForResourceAsync("myapp-unknown", r => HasTerminalProperty(r, "terminal.enabled")).DefaultTimeout();

        Assert.Equal("0", GetTerminalProperty(snapshot, "terminal.replicaIndex").Value.StringValue);
        Assert.Equal(
            GetTerminalHosts(resource)[0].Layout.ConsumerUdsPath,
            GetTerminalProperty(snapshot, "terminal.consumerUdsPath").Value.StringValue);
    }

    private static bool HasTerminalProperty(ResourceSnapshot snapshot, string name)
        => snapshot.Properties.Any(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static (string Name, Google.Protobuf.WellKnownTypes.Value Value, bool IsSensitive, string? DisplayName, bool IsHighlighted, int? SortOrder) GetTerminalProperty(ResourceSnapshot snapshot, string name)
        => snapshot.Properties.Single(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    private static IReadOnlyList<TerminalHostResource> GetTerminalHosts(Resource resource)
        => resource.Annotations.OfType<TerminalAnnotation>().Single().TerminalHosts;

    private static (DashboardServiceData Data, ResourceNotificationService Notifications, TestResource Resource) CreateHarness(
        bool addTerminal,
        int replicaCount = 1,
        IReadOnlyList<DcpInstance>? dcpInstances = null)
    {
        var resource = new TestResource("myapp");

        if (addTerminal)
        {
            // Synthesise per-replica layouts directly rather than going through the
            // public WithTerminal() path so the test stays focused on snapshot stamping
            // and doesn't depend on the full DistributedApplication lifecycle.
            var hosts = new TerminalHostResource[replicaCount];
            var baseDir = Directory.CreateTempSubdirectory("dsdt-").FullName;
            for (var i = 0; i < replicaCount; i++)
            {
                var pseudoId = $"test{i.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(7, '0')}";
                var layout = new TerminalHostLayout(
                    replicaId: pseudoId,
                    parentReplicaIndex: i,
                    producerUdsPath: Path.Combine(baseDir, $"{pseudoId}.dcp.sock"),
                    consumerUdsPath: Path.Combine(baseDir, $"{pseudoId}.host.sock"),
                    controlUdsPath: Path.Combine(baseDir, $"{pseudoId}.ctrl.sock"),
                    metadataPath: Path.Combine(baseDir, $"{pseudoId}.metadata.json"));
                hosts[i] = new TerminalHostResource($"myapp-terminalhost-{i}", resource, layout);
            }
            var annotation = new TerminalAnnotation(new TerminalOptions { Columns = 132, Rows = 40 });
            annotation.Initialize(hosts);
            resource.Annotations.Add(annotation);
        }

        if (dcpInstances is not null)
        {
            resource.Annotations.Add(new DcpInstancesAnnotation([.. dcpInstances]));
        }

        var loggerService = new ResourceLoggerService();
        var notifications = new ResourceNotificationService(
            NullLogger<ResourceNotificationService>.Instance,
            new TestHostApplicationLifetime(),
            new ServiceCollection().BuildServiceProvider(),
            loggerService);
        var interactions = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            new ConfigurationBuilder().Build());
        var data = new DashboardServiceData(
            notifications,
            loggerService,
            NullLogger<DashboardServiceData>.Instance,
            new ResourceCommandService(notifications, loggerService, new ServiceCollection().BuildServiceProvider()),
            interactions);
        return (data, notifications, resource);
    }

    private sealed class TestResource(string name) : Resource(name);

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = new CancellationTokenSource().Token;
        public CancellationToken ApplicationStopped { get; } = new CancellationTokenSource().Token;
        public CancellationToken ApplicationStopping { get; } = new CancellationTokenSource().Token;
        public void StopApplication() { }
    }
}
