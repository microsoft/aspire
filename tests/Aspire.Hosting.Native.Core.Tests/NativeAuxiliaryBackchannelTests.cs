// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text.Json.Nodes;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Auxiliary;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Rpc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeAuxiliaryBackchannelTests
{
    [Fact]
    public async Task FirstRequestDeadlineReleasesSilentClientsButNotEstablishedWatches()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = new FakeTimeProvider();
        var options = new NativeServerOptions { TimeProvider = clock, MaximumConnections = 2 };
        var rpc = new RecordingBackchannelRpc(new NativeBackchannelRpc(options, NullLogger.Instance));
        await using var host = new NativeAuxiliaryTestHost(new NativeApplicationServer(), options, rpc);
        var established = host.Connect();
        await rpc.WaitForConnectionAsync(timeout.Token);
        Assert.Equal(["aux.v1", "aux.v2", "aux.v3", "resource-snapshot-versions.v1"],
            (await established.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync")).Capabilities);
        host.Logs.Append(new(Guid.NewGuid(), "ipc.watch", OperationOutcome.Ok, null, null, null, null, null,
            clock.GetUtcNow(), 0, null));
        await using var updates = (await established.InvokeAsync<IAsyncEnumerable<NativeCliLogEntry>>("GetAppHostLogEntriesAsync"))
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(1, updates.Current.SequenceNumber);
        using var silent = new NativeSocketTestClient(new UnixDomainSocketEndPoint(host.SocketPath));
        await rpc.WaitForConnectionAsync(timeout.Token);
        var disconnected = silent.ReadAsync(timeout.Token);
        clock.Advance(options.BackchannelFirstRequestTimeout);
        Assert.Null(await disconnected);
        var nextLog = updates.MoveNextAsync().AsTask();
        clock.Advance(TimeSpan.FromHours(1));
        host.Logs.Append(new(Guid.NewGuid(), "ipc.watch", OperationOutcome.Ok, null, null, null, null, null,
            clock.GetUtcNow(), 0, null));
        Assert.True(await nextLog);
        Assert.Equal(2, updates.Current.SequenceNumber);
        Assert.Empty((await established.InvokeAsync<GetResourcesResponse>("GetResourcesAsync")).Resources);
        var replacement = host.Connect();
        await rpc.WaitForConnectionAsync(timeout.Token);
        Assert.Empty((await replacement.InvokeAsync<GetResourcesResponse>("GetResourcesAsync")).Resources);
    }

    [Fact]
    public async Task RpcImplementationCanBeReplacedWithoutChangingAuxiliaryDependencies()
    {
        var options = new NativeServerOptions();
        var implementation = new RecordingBackchannelRpc(new NativeBackchannelRpc(options, NullLogger.Instance));
        await using var host = new NativeAuxiliaryTestHost(new NativeApplicationServer(), options, implementation);
        var rpc = host.Connect();
        Assert.Equal(["aux.v1", "aux.v2", "aux.v3", "resource-snapshot-versions.v1"],
            (await rpc.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync")).Capabilities);
        Assert.Empty((await rpc.InvokeAsync<GetResourcesResponse>("GetResourcesAsync")).Resources);
        Assert.Equal(["GetCapabilitiesAsync", "GetResourcesAsync"], implementation.Methods);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceWatchHidesRemovedNamesAcrossEmptyRevisionsAndRetirement(bool retireWorkspace)
    {
        var server = new NativeApplicationServer();
        var workspace = server.CreateApplicationWorkspace();
        var revision = workspace.BeginRevision();
        revision.AddResource("removed", "custom/Resource");
        await workspace.CommitRevision(revision);
        await using var host = new NativeAuxiliaryTestHost(server);
        var rpc = host.Connect();
        var stream = await rpc.InvokeAsync<IAsyncEnumerable<ResourceSnapshot>>("WatchResourcesAsync");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var enumerator = stream.GetAsyncEnumerator(timeout.Token);
        Assert.True(await enumerator.MoveNextAsync());
        var initial = enumerator.Current;
        Assert.Equal("removed", initial.Name);
        if (retireWorkspace)
        {
            await workspace.RetireApplicationWorkspace();
        }
        else
        {
            await workspace.CommitRevision(workspace.BeginRevision());
        }
        Assert.Empty((await rpc.InvokeAsync<GetResourcesResponse>("GetResourcesAsync")).Resources);
        Assert.True(await enumerator.MoveNextAsync());
        var removed = enumerator.Current;
        Assert.Equal("removed", removed.Name);
        Assert.Equal("Hidden", removed.State);
        Assert.True(removed.IsHidden);
        Assert.True(removed.Version > initial.Version);
        Assert.Empty(removed.Urls);
        Assert.Empty(removed.Commands);
    }

    [Fact]
    public async Task IndependentSlowResourceWatchDoesNotMissRemovalAcrossCoalescedRevisions()
    {
        var server = new NativeApplicationServer();
        var workspace = server.CreateApplicationWorkspace();
        var revision = workspace.BeginRevision();
        revision.AddResource("original", "custom/Resource");
        await workspace.CommitRevision(revision);
        await using var host = new NativeAuxiliaryTestHost(server);
        var fast = host.Connect();
        var slow = host.Connect();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var fastUpdates = (await fast.InvokeAsync<IAsyncEnumerable<ResourceSnapshot>>("WatchResourcesAsync"))
            .GetAsyncEnumerator(timeout.Token);
        await using var slowUpdates = (await slow.InvokeAsync<IAsyncEnumerable<ResourceSnapshot>>("WatchResourcesAsync"))
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await fastUpdates.MoveNextAsync());
        Assert.True(await slowUpdates.MoveNextAsync());
        var originalVersion = slowUpdates.Current.Version;
        revision = workspace.BeginRevision();
        revision.AddResource("intermediate", "custom/Resource");
        await workspace.CommitRevision(revision);
        Assert.True(await fastUpdates.MoveNextAsync());
        Assert.Equal(("original", "Hidden"), (fastUpdates.Current.Name, fastUpdates.Current.State));
        Assert.True(await fastUpdates.MoveNextAsync());
        Assert.Equal("intermediate", fastUpdates.Current.Name);
        revision = workspace.BeginRevision();
        revision.AddResource("latest", "custom/Resource");
        await workspace.CommitRevision(revision);
        Assert.Equal("latest", Assert.Single((await fast.InvokeAsync<GetResourcesResponse>("GetResourcesAsync")).Resources).Name);
        Assert.True(await slowUpdates.MoveNextAsync());
        Assert.Equal(("original", "Hidden"), (slowUpdates.Current.Name, slowUpdates.Current.State));
        Assert.True(slowUpdates.Current.Version > originalVersion);
        Assert.True(await slowUpdates.MoveNextAsync());
        Assert.Equal("latest", slowUpdates.Current.Name);
        await workspace.RetireApplicationWorkspace();
    }

    [Theory]
    [InlineData("configured-file", "cli-file", "configured-path", "native-file", "configured-file")]
    [InlineData(null, "cli-file", "configured-path", "native-file", "cli-file")]
    [InlineData(null, null, "configured-path", "native-file", "configured-path")]
    [InlineData(null, null, null, "native-file", "native-file")]
    [InlineData(null, null, null, null, null)]
    public void AppHostPathUsesExistingCliProjectionWithoutNativeOverride(
        string? filePath, string? cliFilePath, string? path, string? nativePath, string? expected)
    {
        Assert.Equal(expected, NativeAuxiliaryBackchannel.ResolveAppHostPath(filePath, cliFilePath, path, nativePath));
    }

    [Fact]
    public async Task SharedDiscoveryAndManagedDtoContractsSupportConcurrentClientsAndStop()
    {
        var server = new NativeApplicationServer();
        await using var host = new NativeAuxiliaryTestHost(server);
        var socket = Assert.Single(AppHostSocketManager.FindSockets(host.AppHostPath, host.HomeDirectory,
            Environment.ProcessId, NullLogger.Instance));
        Assert.Equal(host.SocketPath, socket.SocketPath);
        Assert.Equal(Environment.ProcessId, socket.ProcessId);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(host.SocketPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(host.SocketPath)!));
        }
        var first = host.Connect();
        var second = host.Connect();
        var capabilities = await first.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync");
        Assert.Equal(["aux.v1", "aux.v2", "aux.v3", "resource-snapshot-versions.v1"], capabilities.Capabilities);
        var legacy = await second.InvokeAsync<AppHostInformation>("GetAppHostInformationAsync");
        Assert.Equal(Environment.ProcessId, legacy.ProcessId);
        Assert.Equal(host.AppHostPath, legacy.AppHostPath);
        Assert.NotNull(legacy.StableStartedAt);
        var info = await first.InvokeAsync<GetAppHostInfoResponse>("GetAppHostInfoAsync", new GetAppHostInfoRequest());
        Assert.Equal(Environment.ProcessId.ToString(), info.Pid);
        Assert.Equal(legacy.StartedAt, info.StartedAt);
        Assert.Null(await first.InvokeAsync<DashboardMcpConnectionInfo?>("GetDashboardMcpConnectionInfoAsync"));
        var terminal = await first.InvokeAsync<GetTerminalInfoResponse>("GetTerminalInfoAsync",
            new GetTerminalInfoRequest { ResourceName = "missing" });
        Assert.False(terminal.IsAvailable);
        Assert.Empty((await second.InvokeAsync<ListTerminalsResponse>("ListTerminalsAsync")).ResourceTerminals);
        var ready = first.InvokeAsync<WaitForAppHostReadyResponse>("WaitForAppHostReadyAsync");
        Assert.False(ready.IsCompleted);
        await second.NotifyAsync("GetCapabilitiesAsync", new GetCapabilitiesRequest());
        Assert.Equal(capabilities.Capabilities, (await second.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync")).Capabilities);
        host.Ready.SetResult();
        Assert.True((await ready).IsReady);
        await first.InvokeAsync<StopAppHostResponse>("StopAsync", new StopAppHostRequest { ExitCode = 17 });
        await host.Stopped.Task;
        Assert.Equal(17, host.ExitCode);
    }

    [Fact]
    public async Task ResourceSnapshotsRemainTypedAndMonotonicAcrossRevisionsWithoutAuthoringSecrets()
    {
        var server = new NativeApplicationServer();
        var workspace = server.CreateApplicationWorkspace();
        var revision = workspace.BeginRevision();
        var resource = revision.AddResource("cache", "custom/Cache");
        resource.SetResourceConfiguration(new ResourceConfiguration
        {
            Properties = [new ResourceConfigurationProperty { Name = "password", Value = "secret-password" }]
        });
        await workspace.CommitRevision(revision);
        await using var host = new NativeAuxiliaryTestHost(server);
        var rpc = host.Connect();
        var first = Assert.Single((await rpc.InvokeAsync<GetResourcesResponse>("GetResourcesAsync",
            new GetResourcesRequest { ClientCapabilities = ["aux.v3"] })).Resources);
        Assert.Equal("custom/Cache", first.ResourceType);
        Assert.True(first.Version > 0);
        Assert.Equal(1, first.Properties["configurationRevision"]!.GetValue<long>());
        Assert.Equal(["appliedConfigurationRevision", "configurationRevision", "configurationStatus", "resourceId"],
            first.Properties.Keys.OrderBy(key => key).ToArray());
        var oldClient = Assert.Single(await rpc.InvokeAsync<List<ResourceSnapshot>>("GetResourceSnapshotsAsync"));
        Assert.Equal("1", oldClient.Properties["configurationRevision"]!.GetValue<string>());
        Assert.Empty((await rpc.InvokeAsync<GetResourcesResponse>("GetResourcesAsync",
            new GetResourcesRequest { Filter = "nothing" })).Resources);
        Assert.Empty((await rpc.InvokeWithParameterObjectAsync<GetResourcesResponse>("GetResourcesAsync",
            new GetResourcesRequest { Filter = "nothing" })).Resources);
        Assert.Single((await rpc.InvokeWithParameterObjectAsync<GetResourcesResponse>("GetResourcesAsync",
            new { request = new GetResourcesRequest { Filter = "CACHE" } })).Resources);
        var next = workspace.BeginRevision();
        next.AddResource("cache", "custom/Cache");
        await workspace.CommitRevision(next);
        var updated = Assert.Single((await rpc.InvokeAsync<GetResourcesResponse>("GetResourcesAsync")).Resources);
        Assert.True(updated.Version > first.Version);
        Assert.Equal(first.Properties["resourceId"]!.GetValue<string>(), updated.Properties["resourceId"]!.GetValue<string>());
        await workspace.RetireApplicationWorkspace();
        var replacement = server.OpenApplication();
        var generation = replacement.StartGeneration();
        generation.AddResource("cache", "another/Cache");
        generation.CreateExecution();
        var latest = Assert.Single((await rpc.InvokeAsync<GetResourcesResponse>("GetResourcesAsync")).Resources);
        Assert.True(latest.Version > updated.Version);
        replacement.Close();
    }

    [Fact]
    public async Task CompletedLogStreamsReleaseCapacityWithoutClientAbort()
    {
        var server = new NativeApplicationServer();
        var session = server.OpenApplication();
        var composition = session.StartGeneration();
        var resource = composition.AddResource("cache", "custom/Cache");
        var execution = composition.CreateExecution();
        var writer = server.JoinResourceExecution(execution.InviteResourceExecution(resource));
        writer.AppendResourceLog("stdout", "retained log");
        await using var host = new NativeAuxiliaryTestHost(server, new NativeServerOptions { MaximumCliStreams = 1 });
        var rpc = host.Connect();
        foreach (var method in new[] { "GetConsoleLogsAsync", "GetConsoleLogBatchesAsync", "GetConsoleLogsAsync" })
        {
            var descriptor = await rpc.InvokeAsync<NativeCliStreamDescriptor>(method,
                new GetConsoleLogsRequest { ResourceName = "cache" });
            var first = await rpc.InvokeAsync<NativeCliEnumeratorResult>("$/enumerator/next", descriptor.Token);
            Assert.False(first.Finished);
            Assert.Single(first.Values);
            var last = await rpc.InvokeAsync<NativeCliEnumeratorResult>("$/enumerator/next", descriptor.Token);
            Assert.True(last.Finished);
            Assert.Empty(last.Values);
            // Use raw tokens so no client enumerator disposal can send an abort
            // and accidentally hide a server-side capacity leak.
        }
        var watch = await rpc.InvokeAsync<NativeCliStreamDescriptor>("WatchResourcesAsync");
        await rpc.InvokeAsync("$/enumerator/abort", watch.Token);
        session.Close();
    }

    [Fact]
    public async Task RealResourceLogsSupportSearchTailBatchesFollowCancellationAndStreamAbort()
    {
        var server = new NativeApplicationServer();
        var session = server.OpenApplication();
        var composition = session.StartGeneration();
        var resource = composition.AddResource("cache", "custom/Cache");
        var execution = composition.CreateExecution();
        var writer = server.JoinResourceExecution(execution.InviteResourceExecution(resource));
        writer.AppendResourceLog("stdout", "first");
        writer.AppendResourceLog("stderr", "second matching");
        writer.AppendResourceLog("stdout", "third matching");
        await using var host = new NativeAuxiliaryTestHost(server);
        var rpc = host.Connect();
        var request = new GetConsoleLogsRequest { ResourceName = "CACHE", Search = "matching", Tail = 1 };
        var lines = await CollectAsync(await rpc.InvokeAsync<IAsyncEnumerable<ResourceLogLine>>("GetConsoleLogsAsync", request));
        var line = Assert.Single(lines);
        Assert.Equal("third matching", line.Content);
        Assert.False(line.IsError);
        var batches = await CollectAsync(await rpc.InvokeAsync<IAsyncEnumerable<ResourceLogBatch>>("GetConsoleLogBatchesAsync", request));
        Assert.Equal(line.Content, Assert.Single(Assert.Single(batches).Lines).Content);
        var follow = await rpc.InvokeAsync<IAsyncEnumerable<ResourceLogLine>>("GetConsoleLogsAsync",
            new GetConsoleLogsRequest { ResourceName = "cache", Follow = true, Tail = 1 });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var enumerator = follow.GetAsyncEnumerator(cancellation.Token))
        {
            foreach (var content in new[] { "first", "second matching", "third matching" })
            {
                Assert.True(await enumerator.MoveNextAsync());
                Assert.Equal(content, enumerator.Current.Content);
            }
            var next = enumerator.MoveNextAsync().AsTask();
            // Negotiate a second connection while an enumerator is waiting for a new line.
            var second = host.Connect();
            Assert.NotEmpty((await second.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync")).Capabilities);
            writer.AppendResourceLog("stderr", "new matching");
            Assert.True(await next);
            Assert.Equal("new matching", enumerator.Current.Content);
            Assert.True(enumerator.Current.IsError);
            var pending = enumerator.MoveNextAsync().AsTask();
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        var resources = await rpc.InvokeAsync<IAsyncEnumerable<ResourceSnapshot>>("WatchResourcesAsync");
        await using (var enumerator = resources.GetAsyncEnumerator())
        {
            Assert.True(await enumerator.MoveNextAsync());
        }
        Assert.NotEmpty((await rpc.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync")).Capabilities);
        session.Close();
    }

    [Fact]
    public async Task CommandsValidateWithoutDispatchAndWaitReportsRealSuccessFailureAndTimeout()
    {
        var server = new NativeApplicationServer();
        var session = server.OpenApplication();
        var composition = session.StartGeneration();
        var resource = composition.AddResource("cache", "custom/Cache");
        var execution = composition.CreateExecution();
        var writer = server.JoinResourceExecution(execution.InviteResourceExecution(resource));
        writer.DefineResourceCommand("restart", "Restart");
        writer.PublishObservation(new ResourceObservation { State = "Running", Healthy = true, Urls = [] });
        await using var host = new NativeAuxiliaryTestHost(server);
        var rpc = host.Connect();
        var validation = await rpc.InvokeAsync<ExecuteResourceCommandResponse>("ExecuteResourceCommandAsync",
            new ExecuteResourceCommandRequest { ResourceName = "cache", CommandName = "restart", ValidateOnly = true, ReturnArgumentInputs = true });
        Assert.True(validation.Success);
        Assert.Empty(validation.ArgumentInputs!);
        Assert.Empty(writer.ReadResourceCommands().Commands);
        var unsupportedArguments = await rpc.InvokeAsync<ExecuteResourceCommandResponse>("ExecuteResourceCommandAsync",
            new ExecuteResourceCommandRequest
            {
                ResourceName = "cache", CommandName = "restart", ValidateOnly = true,
                Arguments = new JsonObject { ["unsupported"] = "value" }
            });
        Assert.False(unsupportedArguments.Success);
        Assert.Equal("This resource command does not accept arguments.", unsupportedArguments.Message);
        Assert.Empty(writer.ReadResourceCommands().Commands);
        var unknown = await rpc.InvokeAsync<ExecuteResourceCommandResponse>("ExecuteResourceCommandAsync",
            new ExecuteResourceCommandRequest { ResourceName = "cache", CommandName = "unknown" });
        Assert.False(unknown.Success);
        Assert.Equal("Command 'unknown' not found on resource 'cache'.", unknown.Message);
        var actual = rpc.InvokeAsync<ExecuteResourceCommandResponse>("ExecuteResourceCommandAsync",
            new ExecuteResourceCommandRequest { ResourceName = "cache", CommandName = "restart", NonInteractive = false });
        var commands = await writer.WaitResourceCommands(10000);
        var pending = Assert.Single(commands.Commands);
        writer.CompleteResourceCommand(pending.RequestId, new RuntimeOperationResult { Status = "failed", Message = "Real integration failure." });
        Assert.Equal("Real integration failure.", (await actual).Message);
        Assert.False((await actual).Success);
        var healthy = await rpc.InvokeAsync<WaitForResourceResponse>("WaitForResourceAsync",
            new WaitForResourceRequest { ResourceName = "cache", Status = "healthy" });
        Assert.True(healthy.Success);
        Assert.Equal("Healthy", healthy.HealthStatus);
        var missing = await rpc.InvokeAsync<WaitForResourceResponse>("WaitForResourceAsync",
            new WaitForResourceRequest { ResourceName = "missing", Status = "up" });
        Assert.True(missing.ResourceNotFound);
        var timedOut = await rpc.InvokeAsync<WaitForResourceResponse>("WaitForResourceAsync",
            new WaitForResourceRequest { ResourceName = "cache", Status = "down", TimeoutSeconds = 0 });
        Assert.True(timedOut.TimedOut);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waiting = rpc.InvokeWithCancellationAsync<WaitForResourceResponse>("WaitForResourceAsync",
            [new WaitForResourceRequest { ResourceName = "cache", Status = "down" }], cancellation.Token);
        await rpc.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync");
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.NotEmpty((await rpc.InvokeAsync<GetCapabilitiesResponse>("GetCapabilitiesAsync")).Capabilities);
        var mcp = await Assert.ThrowsAsync<RemoteInvocationException>(() => rpc.InvokeAsync<CallMcpToolResponse>("CallMcpToolAsync",
            new CallMcpToolRequest { ResourceName = "cache", ToolName = "unsupported" }));
        Assert.Equal("Resource 'cache' does not have an MCP endpoint annotation.", mcp.Message);
        session.Close();
    }

    [Fact]
    public async Task StopNotificationExecutesAndRemovesDiscoveredSocket()
    {
        string socketPath;
        await using (var host = new NativeAuxiliaryTestHost(new NativeApplicationServer()))
        {
            socketPath = host.SocketPath;
            var rpc = host.Connect();
            await rpc.NotifyAsync("StopAppHostAsync");
            await host.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.False(File.Exists(socketPath));
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var value in source)
        {
            result.Add(value);
        }

        return result;
    }
}
