// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Dcp;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests.Dcp;

[Trait("Partition", "4")]
public class AppHostLocalStateStoreTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void SetPortAllocation_MergesAllocationsAcrossStoreInstances()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configuration = CreateConfiguration(workspace, "merge-test");
        var firstStore = CreatePortAllocationStore(configuration);
        var secondStore = CreatePortAllocationStore(configuration);

        var firstKey = new PersistentPortAllocationKey("first", "http");
        var secondKey = new PersistentPortAllocationKey("second", "http");
        firstStore.SetPort(firstKey, 12001);
        secondStore.SetPort(secondKey, 12002);

        var allocations = firstStore.GetAllocations();
        Assert.Equal(12001, allocations[firstKey]);
        Assert.Equal(12002, allocations[secondKey]);

        var stateFile = Directory.GetFiles(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "state"),
            "*.json").Single();
        Assert.Matches("^[0-9a-f]{16}\\.json$", Path.GetFileName(stateFile));

        var state = JsonNode.Parse(File.ReadAllText(stateFile));
        Assert.Equal(1, state?["portAllocations"]?["version"]?.GetValue<int>());
        Assert.Equal(12001, state?["portAllocations"]?["resources"]?["first"]?["http"]?["port"]?.GetValue<int>());
        Assert.Equal(12002, state?["portAllocations"]?["resources"]?["second"]?["http"]?["port"]?.GetValue<int>());
        Assert.Equal($"*{Environment.NewLine}", File.ReadAllText(Path.Combine(Path.GetDirectoryName(stateFile)!, ".gitignore")));
    }

    [Fact]
    public void SetPortAllocation_UsesResourceAndEndpointNameComparers()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configuration = CreateConfiguration(workspace, "name-comparer-test");
        var store = CreatePortAllocationStore(configuration);

        store.SetPort(new PersistentPortAllocationKey("Program", "HTTP"), 12001);

        var equivalentKey = new PersistentPortAllocationKey("program", "http");
        Assert.Equal(12001, store.GetAllocations()[equivalentKey]);

        store.SetPort(equivalentKey, 12002);

        var allocation = Assert.Single(store.GetAllocations());
        Assert.Equal(equivalentKey, allocation.Key);
        Assert.Equal(12002, allocation.Value);

        var stateFile = Directory.GetFiles(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "state"),
            "*.json").Single();
        var resources = JsonNode.Parse(File.ReadAllText(stateFile))?["portAllocations"]?["resources"]?.AsObject();
        var resource = Assert.Single(resources!);
        var endpoint = Assert.Single(resource.Value!.AsObject());
        Assert.Equal(12002, endpoint.Value?["port"]?.GetValue<int>());
    }

    [Fact]
    public void SetPortAllocation_PreservesOtherStateKeys()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configuration = CreateConfiguration(workspace, "preserve-test");
        var stateStore = new AppHostLocalStateStore(configuration, NullLogger<AppHostLocalStateStore>.Instance);
        var portAllocationStore = new PersistentPortAllocationStore(
            stateStore,
            NullLogger<PersistentPortAllocationStore>.Instance);
        portAllocationStore.SetPort(new PersistentPortAllocationKey("first", "http"), 12001);

        var stateFile = Directory.GetFiles(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "state"),
            "*.json").Single();
        stateStore.Update("runtime", _ => new JsonObject
        {
            ["identifier"] = "preserved"
        });

        portAllocationStore.SetPort(new PersistentPortAllocationKey("second", "http"), 12002);

        var state = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
        Assert.Equal("preserved", state["runtime"]?["identifier"]?.GetValue<string>());
        Assert.Equal(12001, state["portAllocations"]?["resources"]?["first"]?["http"]?["port"]?.GetValue<int>());
        Assert.Equal(12002, state["portAllocations"]?["resources"]?["second"]?["http"]?["port"]?.GetValue<int>());
    }

    [Fact]
    public void SetPortAllocation_DoesNotOverwriteNewerValueVersion()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configuration = CreateConfiguration(workspace, "newer-version-test");
        var stateStore = new AppHostLocalStateStore(configuration, NullLogger<AppHostLocalStateStore>.Instance);
        stateStore.Update("portAllocations", _ => new JsonObject
        {
            ["version"] = 2,
            ["futureValue"] = "preserved"
        });

        var sink = new TestSink();
        var portAllocationStore = new PersistentPortAllocationStore(
            stateStore,
            new TestLogger<PersistentPortAllocationStore>(new TestLoggerFactory(sink, enabled: true)));
        portAllocationStore.SetPort(new PersistentPortAllocationKey("first", "http"), 12001);
        stateStore.Update("runtime", _ => JsonValue.Create("available"));

        var stateFile = Directory.GetFiles(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "state"),
            "*.json").Single();
        var state = JsonNode.Parse(File.ReadAllText(stateFile))!.AsObject();
        Assert.Equal(2, state["portAllocations"]?["version"]?.GetValue<int>());
        Assert.Equal("preserved", state["portAllocations"]?["futureValue"]?.GetValue<string>());
        Assert.Null(state["portAllocations"]?["resources"]);
        Assert.Equal("available", state["runtime"]?.GetValue<string>());
        Assert.Contains(sink.Writes, write =>
            write.LogLevel == LogLevel.Warning &&
            write.Message?.Contains("unsupported portAllocations AppHost local state value", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Read_MalformedFileLogsWarningAndReturnsEmptyState()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configuration = CreateConfiguration(workspace, "malformed-test");
        var stateStore = new AppHostLocalStateStore(configuration, NullLogger<AppHostLocalStateStore>.Instance);
        var portAllocationStore = new PersistentPortAllocationStore(
            stateStore,
            NullLogger<PersistentPortAllocationStore>.Instance);
        portAllocationStore.SetPort(new PersistentPortAllocationKey("first", "http"), 12001);

        var stateFile = Directory.GetFiles(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "state"),
            "*.json").Single();
        File.WriteAllText(stateFile, "{ invalid");

        var sink = new TestSink();
        var loggingStateStore = new AppHostLocalStateStore(
            configuration,
            new TestLogger<AppHostLocalStateStore>(new TestLoggerFactory(sink, enabled: true)));
        var loggingPortAllocationStore = new PersistentPortAllocationStore(
            loggingStateStore,
            NullLogger<PersistentPortAllocationStore>.Instance);

        Assert.Empty(loggingPortAllocationStore.GetAllocations());
        Assert.Contains(sink.Writes, write =>
            write.LogLevel == LogLevel.Warning &&
            write.Message?.Contains("malformed AppHost local state file", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void StateUsesAppHostIdentityAcrossLauncherWorkloadIds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        const string appHostIdentity = "1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef";
        var directLaunchConfiguration = CreateConfiguration(workspace, workloadId: null, appHostIdentity: appHostIdentity);
        var cliLaunchConfiguration = CreateConfiguration(workspace, "apphost-different-launcher-id", appHostIdentity);
        var key = new PersistentPortAllocationKey("program", "http");

        CreatePortAllocationStore(directLaunchConfiguration).SetPort(key, 12001);

        Assert.Equal(12001, CreatePortAllocationStore(cliLaunchConfiguration).GetAllocations()[key]);
        Assert.Single(Directory.GetFiles(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "state"),
            "*.json"));
    }

    [Fact]
    public void LockTimeoutDisablesSubsequentDiskAccessButKeepsStoreAvailable()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var configuration = CreateConfiguration(workspace, "lock-timeout-test");
        var initialStore = CreatePortAllocationStore(configuration);
        var firstKey = new PersistentPortAllocationKey("first", "http");
        var secondKey = new PersistentPortAllocationKey("second", "http");
        initialStore.SetPort(firstKey, 12001);

        var stateFile = Directory.GetFiles(
            Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "state"),
            "*.json").Single();
        var sink = new TestSink();
        var stateStore = new AppHostLocalStateStore(
            configuration,
            new TestLogger<AppHostLocalStateStore>(new TestLoggerFactory(sink, enabled: true)),
            TimeSpan.FromMilliseconds(100));
        var portAllocationStore = new PersistentPortAllocationStore(
            stateStore,
            NullLogger<PersistentPortAllocationStore>.Instance);

        using (FileLock.Acquire(stateFile + ".lock"))
        {
            Assert.Empty(portAllocationStore.GetAllocations());
        }

        Assert.True(stateStore.IsAvailable);
        portAllocationStore.SetPort(secondKey, 12002);

        var allocations = CreatePortAllocationStore(configuration).GetAllocations();
        var allocation = Assert.Single(allocations);
        Assert.Equal(firstKey, allocation.Key);
        Assert.Equal(12001, allocation.Value);
        Assert.Single(sink.Writes, write =>
            write.LogLevel == LogLevel.Warning &&
            write.Message?.Contains("disk access is disabled for this process", StringComparison.Ordinal) == true);
    }

    private static PersistentPortAllocationStore CreatePortAllocationStore(IConfiguration configuration)
    {
        var stateStore = new AppHostLocalStateStore(configuration, NullLogger<AppHostLocalStateStore>.Instance);
        return new PersistentPortAllocationStore(stateStore, NullLogger<PersistentPortAllocationStore>.Instance);
    }

    private static IConfiguration CreateConfiguration(
        TemporaryWorkspace workspace,
        string? workloadId,
        string? appHostIdentity = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AppHost:Directory"] = workspace.WorkspaceRoot.FullName,
                ["AppHost:DeploymentStatePathSha256"] = appHostIdentity,
                [KnownConfigNames.DcpWorkloadId] = workloadId
            })
            .Build();
    }
}
