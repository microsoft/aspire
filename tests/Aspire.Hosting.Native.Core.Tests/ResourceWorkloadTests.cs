// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Model;
using Aspire.Hosting.Native.Runtime;
using static Aspire.Hosting.Native.Core.Tests.TestServices.RpcTestClient;

namespace Aspire.Hosting.Native.Core.Tests;

public class ResourceWorkloadTests
{
    [Fact]
    public async Task ContainersCanRunWithoutExposingAnEndpoint()
    {
        var executor = new WorkloadTestExecutor();
        executor.ReleaseCleanup();
        using var context = new RuntimeRpcTestContext(executor);
        var endpoint = Result(await context.Integration.InvokeAsync("startContainer", Arguments(
            ("context", context.Writer), ("container", new JsonObject
            {
                ["image"] = "example/background-worker:1", ["targetPort"] = 0,
                ["environment"] = new JsonArray(), ["arguments"] = new JsonArray()
            }))));
        Assert.Equal(0, endpoint["port"]!.GetValue<int>());
        Assert.Empty(context.Read()["resources"]![0]!["urls"]!.AsArray());
        await context.Author.InvokeAsync("retireGeneration", Arguments(
            ("context", context.Session), ("composition", context.Composition)));
        Assert.Single(executor.Removed);
    }

    [Fact]
    public async Task RetirementRevokesAuthorityBeforeWaitingForCleanupAndBlocksReplacement()
    {
        var executor = new WorkloadTestExecutor();
        using var context = new RuntimeRpcTestContext(executor);
        try
        {
            var endpoint = Result(await context.Integration.InvokeAsync("startContainer", Arguments(
                ("context", context.Writer), ("container", new JsonObject
                {
                    ["image"] = "example:1", ["targetPort"] = 6379,
                    ["environment"] = new JsonArray(), ["arguments"] = new JsonArray()
                }))));
            Assert.Equal(5050, endpoint["port"]!.GetValue<int>());
            var retiring = context.Author.InvokeAsync("retireGeneration", Arguments(
                ("context", context.Session), ("composition", context.Composition)));
            await executor.CleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(retiring.IsCompleted);
            Assert.Equal("HANDLE_NOT_FOUND", context.Publish("Running", true)["result"]!["$error"]!["code"]!.GetValue<string>());
            Assert.Equal("OPERATION_REJECTED", context.Author.Invoke("startGeneration", Arguments(
                ("context", context.Session)))["result"]!["$error"]!["code"]!.GetValue<string>());
            executor.ReleaseCleanup();
            Assert.Null((await retiring.WaitAsync(TimeSpan.FromSeconds(10)))["result"]);
            Assert.Single(executor.Removed);
            Assert.NotNull(context.Author.StartGeneration(context.Session));
        }
        finally
        {
            executor.ReleaseCleanup();
        }
    }

    [Fact]
    public async Task DependenciesWaitForIntegrationHealthBeforeStartingTheDependentWorkload()
    {
        using var model = new ApplicationModel();
        var cache = model.AddResource("cache", "example/Redis");
        var web = model.AddResource("web", "example/Web");
        model.AddDependency(web, cache);
        var executor = new WorkloadTestExecutor();
        using var runtime = new RuntimeGeneration(model.Seal(), executor);
        using var cacheOwner = runtime.ClaimResource(cache);
        using var webOwner = runtime.ClaimResource(web);
        try
        {
            var webStart = webOwner.StartWorkload(new ContainerWorkload("example:1", 80, [], []));
            Assert.Empty(executor.Started);
            await cacheOwner.StartWorkload(new ContainerWorkload("example:1", 6379, [], []));
            Assert.False(webStart.IsCompleted);
            cacheOwner.Publish("Running", healthy: true, []);
            await webStart.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal([cache.ResourceId, web.ResourceId], executor.Started.Select(start => start.ResourceId));
        }
        finally
        {
            executor.ReleaseCleanup();
            runtime.Dispose();
            await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(2, executor.Removed.Count);
    }
}
