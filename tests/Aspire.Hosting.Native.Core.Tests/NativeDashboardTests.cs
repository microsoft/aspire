// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Dashboard;
using Grpc.Core;
using Grpc.Net.Client;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;
using ProtoService = Aspire.DashboardService.Proto.V1.DashboardService;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativeDashboardTests
{
    private const string ApiKey = "native-dashboard-test-key";
    private static Metadata Headers => new() { { "x-resource-service-api-key", ApiKey } };

    [Fact]
    public async Task ShutdownCancelsActiveWatchesBeforeDashboardProcessCleanupAndRejectsReconnects()
    {
        using var context = new RuntimeRpcTestContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await NativeDashboardHost.StartAsync(context.Server, "Native shutdown", ApiKey, 0, timeout.Token);
        using var channel = GrpcChannel.ForAddress(host.Address);
        var client = new ProtoService.DashboardServiceClient(channel);
        using var watch = client.WatchResources(new(), Headers, cancellationToken: timeout.Token);
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var next = watch.ResponseStream.MoveNext(timeout.Token);
        host.BeginShutdown();
        await Assert.ThrowsAsync<RpcException>(() => next.WaitAsync(TimeSpan.FromSeconds(2)));
        var unavailable = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GetApplicationInformationAsync(new(), Headers, cancellationToken: timeout.Token));
        Assert.Equal(StatusCode.Unavailable, unavailable.StatusCode);
    }

    [Fact]
    public async Task ResourceServiceAuthenticatesAndProjectsHealthUrlsCommandsAndLogs()
    {
        using var context = new RuntimeRpcTestContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await NativeDashboardHost.StartAsync(context.Server, "Native test", ApiKey, 0, timeout.Token);
        using var channel = GrpcChannel.ForAddress(host.Address);
        var client = new ProtoService.DashboardServiceClient(channel);
        var unauthorized = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.GetApplicationInformationAsync(new(), cancellationToken: timeout.Token));
        Assert.Equal(StatusCode.Unauthenticated, unauthorized.StatusCode);
        var info = await client.GetApplicationInformationAsync(new(), Headers, cancellationToken: timeout.Token);
        Assert.Equal("Native test", info.ApplicationName);
        context.Publish("Running", healthy: true, "http://localhost:8123");
        context.Integration.Invoke("defineResourceCommand", Arguments(("context", context.Writer),
            ("name", JsonValue.Create("refresh")), ("displayName", JsonValue.Create("Refresh"))));
        context.Integration.Invoke("appendResourceLog", Arguments(("context", context.Writer),
            ("stream", JsonValue.Create("stderr")), ("message", JsonValue.Create("native log"))));
        using var watch = client.WatchResources(new(), Headers, cancellationToken: timeout.Token);
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var resource = Assert.Single(watch.ResponseStream.Current.InitialData.Resources);
        Assert.Equal("cache", resource.Name);
        Assert.NotNull(resource.CreatedAt);
        Assert.Equal("Running", resource.State);
        Assert.Equal(HealthStatus.Healthy, Assert.Single(resource.HealthReports).Status);
        Assert.Equal("http://localhost:8123", Assert.Single(resource.Urls).FullUrl);
        Assert.Equal("http://localhost:8123", Assert.Single(resource.Urls).DisplayProperties.DisplayName);
        Assert.Equal("refresh", Assert.Single(resource.Commands).Name);
        using var logs = client.WatchResourceConsoleLogs(new()
        {
            ResourceName = "cache", SuppressFollow = true
        }, Headers, cancellationToken: timeout.Token);
        Assert.True(await logs.ResponseStream.MoveNext(timeout.Token));
        var line = Assert.Single(logs.ResponseStream.Current.LogLines);
        Assert.Equal("native log", line.Text);
        Assert.True(line.IsStdErr);
        Assert.Equal(1, line.LineNumber);
        Assert.False(await logs.ResponseStream.MoveNext(timeout.Token));
        context.Publish("Failed", healthy: false);
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var failed = Assert.Single(watch.ResponseStream.Current.Changes.Value).Upsert;
        Assert.Equal("Failed", failed.State);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(failed.HealthReports).Status);
    }

    [Fact]
    public async Task GrpcCommandsAndDuplexConfirmationsReachIndependentIntegrationConnection()
    {
        using var context = new RuntimeRpcTestContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await NativeDashboardHost.StartAsync(context.Server, "Native test", ApiKey, 0, timeout.Token);
        using var channel = GrpcChannel.ForAddress(host.Address);
        var client = new ProtoService.DashboardServiceClient(channel);
        context.Integration.Invoke("defineResourceCommand", Arguments(("context", context.Writer),
            ("name", JsonValue.Create("refresh")), ("displayName", JsonValue.Create("Refresh"))));
        var ready = context.Integration.InvokeAsync("waitResourceCommands", Arguments(("context", context.Writer),
            ("timeoutMilliseconds", JsonValue.Create(10000))));
        var execution = client.ExecuteResourceCommandAsync(new()
        {
            ResourceName = "cache", ResourceType = context.Read()["resources"]![0]!["typeId"]!.GetValue<string>(), CommandName = "refresh"
        }, Headers, cancellationToken: timeout.Token);
        var pending = Assert.Single(Result(await ready)["commands"]!.AsArray())!;
        context.Integration.Invoke("completeResourceCommand", Arguments(("context", context.Writer),
            ("requestId", pending["requestId"]), ("result", new JsonObject { ["status"] = "succeeded", ["message"] = "refreshed" })));
        var result = await execution;
        Assert.Equal(ResourceCommandResponseKind.Succeeded, result.Kind);
        Assert.Equal("refreshed", result.Message);
        var operation = Result(context.Integration.Invoke("requestConfirmation", Arguments(("context", context.Writer),
            ("message", JsonValue.Create("Initialize database?")))));
        var completion = context.Integration.InvokeAsync("awaitRuntimeOperation", Arguments(("context", operation)));
        using var interactions = client.WatchInteractions(Headers, cancellationToken: timeout.Token);
        Assert.True(await interactions.ResponseStream.MoveNext(timeout.Token));
        var prompt = interactions.ResponseStream.Current;
        Assert.Equal("Initialize database?", prompt.Message);
        Assert.Equal(WatchInteractionsResponseUpdate.KindOneofCase.MessageBox, prompt.KindCase);
        await interactions.RequestStream.WriteAsync(new()
        {
            InteractionId = prompt.InteractionId, MessageBox = new InteractionMessageBox { Result = true }
        }, timeout.Token);
        Assert.Equal("accepted", Result(await completion.WaitAsync(timeout.Token))["status"]!.GetValue<string>());
        Assert.True(await interactions.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(prompt.InteractionId, interactions.ResponseStream.Current.InteractionId);
        Assert.Equal(WatchInteractionsResponseUpdate.KindOneofCase.Complete, interactions.ResponseStream.Current.KindCase);
    }

    [Fact]
    public async Task WorkspaceRevisionRetainsDashboardUidAndStreamsConfigurationHealthAndRemoval()
    {
        using var context = new WorkspaceRpcTestContext(Aspire.Hosting.Native.Runtime.UnavailableWorkloadExecutor.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await NativeDashboardHost.StartAsync(context.Server, "Native revisions", ApiKey, 0, timeout.Token);
        using var channel = GrpcChannel.ForAddress(host.Address);
        var client = new ProtoService.DashboardServiceClient(channel);
        WorkspaceRpcTestContext.EnsureSuccess(context.PublishHealthy());
        using var watch = client.WatchResources(new(), Headers, cancellationToken: timeout.Token);
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var original = Assert.Single(watch.ResponseStream.Current.InitialData.Resources);
        var revision = context.BeginRevision();
        var cache = context.Author.AddResource(revision, "cache");
        context.Author.AddResource(revision, "added");
        WorkspaceRpcTestContext.EnsureSuccess(context.Author.Invoke("setResourceConfiguration", Arguments(
            ("context", cache), ("configuration", new JsonObject
            {
                ["properties"] = new JsonArray(new JsonObject { ["name"] = "value", ["value"] = "updated" })
            }))));
        WorkspaceRpcTestContext.EnsureSuccess(await context.Author.InvokeAsync("commitRevision",
            Arguments(("context", context.Workspace), ("revision", revision))));
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var changes = watch.ResponseStream.Current.Changes.Value;
        Assert.Equal(2, changes.Count);
        var retained = Assert.Single(changes, change => change.Upsert.Name == "cache").Upsert;
        Assert.Equal(original.Uid, retained.Uid);
        Assert.Equal(original.CreatedAt, retained.CreatedAt);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(retained.HealthReports).Status);
        Assert.Equal("added", Assert.Single(changes, change => change.Upsert.Name != "cache").Upsert.Name);
        WorkspaceRpcTestContext.EnsureSuccess(context.Integration.Invoke("completeResourceConfiguration", Arguments(
            ("context", context.Writer), ("revision", JsonValue.Create(2L)),
            ("result", new JsonObject { ["status"] = "succeeded", ["message"] = "" }))));
        WorkspaceRpcTestContext.EnsureSuccess(context.PublishHealthy());
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var healthy = Assert.Single(watch.ResponseStream.Current.Changes.Value).Upsert;
        Assert.Equal(original.Uid, healthy.Uid);
        Assert.Equal(HealthStatus.Healthy, Assert.Single(healthy.HealthReports).Status);
        var next = context.BeginRevision();
        context.Author.AddResource(next, "added");
        WorkspaceRpcTestContext.EnsureSuccess(await context.Author.InvokeAsync("commitRevision",
            Arguments(("context", context.Workspace), ("revision", next))));
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal("cache", Assert.Single(watch.ResponseStream.Current.Changes.Value).Delete.ResourceName);
    }

    [Fact]
    public async Task ResourceWatchDeletesRetiredGenerationAndAdoptsReplacement()
    {
        using var context = new RuntimeRpcTestContext();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await NativeDashboardHost.StartAsync(context.Server, "Native test", ApiKey, 0, timeout.Token);
        using var channel = GrpcChannel.ForAddress(host.Address);
        var client = new ProtoService.DashboardServiceClient(channel);
        using var watch = client.WatchResources(new(), Headers, cancellationToken: timeout.Token);
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var original = Assert.Single(watch.ResponseStream.Current.InitialData.Resources);
        await context.Author.InvokeAsync("retireGeneration", Arguments(("context", context.Session), ("composition", context.Composition)));
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        Assert.Equal(original.Name, Assert.Single(watch.ResponseStream.Current.Changes.Value).Delete.ResourceName);
        var replacement = context.Author.StartGeneration(context.Session);
        context.Author.AddResource(replacement, "replacement");
        Result(context.Author.Invoke("createExecution", Arguments(("context", replacement))));
        Assert.True(await watch.ResponseStream.MoveNext(timeout.Token));
        var next = Assert.Single(watch.ResponseStream.Current.Changes.Value).Upsert;
        Assert.Equal("replacement", next.Name);
        Assert.NotEqual(original.Uid, next.Uid);
    }
}
