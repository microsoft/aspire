// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Core.Tests.TestServices;
using Aspire.Hosting.Native.Model;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Core.Tests;

public class NativePublishingTests
{
    [Fact]
    public async Task PublishProjectionPreservesExpressionsWithoutAllocationsSecretsOrRuntimeIdentities()
    {
        var executor = new WorkloadTestExecutor();
        var server = new NativeApplicationServer(executor);
        var session = server.OpenApplication();
        var composition = session.StartGeneration();
        var password = composition.AddSecretParameter("cache-password", "must-not-be-exported");
        var cache = composition.AddContainer("cache", "redis:7.4-alpine")
            .WithEndpoint(new() { Name = "tcp", TargetPort = 6379, Protocol = "TCP", UriScheme = "redis", HostPort = 0, EnvironmentVariable = "" })
            .WithEnvironment("REDIS_PASSWORD", password)
            .WithEntrypoint("/bin/sh")
            .WithArgument(composition.CreateLiteralValue("-c"))
            .WithArgument(composition.CreateLiteralValue("exec redis-server --requirepass \"$REDIS_PASSWORD\""))
            .WithVolume("cache-data", "/data", false);
        var web = composition.AddExecutable("web", Environment.ProcessPath!, AppContext.BaseDirectory);
        var connection = composition.CreateLiteralValue("redis://:")
            .ConcatValue(password.EncodeUriValue())
            .ConcatValue(composition.CreateLiteralValue("@"))
            .ConcatValue(composition.CreateEndpointValue(cache, "tcp", "hostAndPort"));
        web.WithEnvironment("CACHE_URL", connection);
        web.WithArgument(composition.CreateLiteralValue("server.mjs"));
        web.SetResourceConfiguration(new()
        {
            Properties = [new() { Name = "publish.dockerCompose", Value = """{"build":{"context":"."},"image":"web:latest"}""" }]
        });
        web.WaitFor(cache);
        var document = composition.ExportPublishModel();
        var exported = JsonNode.Parse(document)!;
        var application = exported["resources"]!.AsArray().Single(resource => resource!["name"]!.GetValue<string>() == "web")!;
        Assert.Equal(Environment.ProcessPath, application["executable"]!.GetValue<string>());
        Assert.Equal(AppContext.BaseDirectory, application["workingDirectory"]!.GetValue<string>());
        application["executable"] = "/app/host";
        application["workingDirectory"] = "/app";
        await Verifier.Verify(exported.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json").UseDirectory("Snapshots");
        Assert.Empty(executor.Prepared);
        Assert.Empty(executor.Started);
        Assert.Equal(document, composition.ExportPublishModel());
        Assert.Throws<InvalidOperationException>(() => composition.AddContainer("late", "redis"));
        await session.RetireGeneration(composition);
        Assert.Throws<ObjectDisposedException>(composition.ExportPublishModel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingUsesExistingCliStreamAndWaitsForStopAcknowledgement(bool failed)
    {
        var server = new NativeApplicationServer();
        await using var host = NativeCliTestHost.Start(server, new(), () => new());
        server.ConfigurePublishing("""{"steps":[{"name":"publish"},{"name":"deploy","dependsOn":["publish"]}]}""");
        server.BeginPublishing("publish");
        Assert.Equal(["publish"], server.ClaimPublishingSteps());
        server.ReportPublishingActivity("publish", failed ? "Publishing failed." : "Artifacts generated.", true, failed);
        server.CompletePublishing(failed);
        Assert.False(server.WaitForPublishingStop().IsCompleted);
        var stream = await host.Rpc.InvokeAsync<IAsyncEnumerable<PublishingActivity>>("GetPublishingActivitiesAsync");
        var activities = new List<PublishingActivity>();
        await foreach (var activity in stream.WithCancellation(TestContext.Current.CancellationToken))
        {
            activities.Add(activity);
        }
        Assert.Collection(activities,
            activity => Assert.Equal(CompletionStates.InProgress, activity.Data.CompletionState),
            activity => Assert.Equal(failed, activity.Data.IsError),
            activity =>
            {
                Assert.Equal(PublishingActivityTypes.PublishComplete, activity.Type);
                Assert.True(activity.Data.IsComplete);
                Assert.Equal(failed, activity.Data.IsError);
            });
        await host.Rpc.InvokeAsync("RequestStopAsync");
        await server.WaitForPublishingStop().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(["baseline.v2", "pipeline-steps.v2"], await host.Rpc.InvokeAsync<string[]>("GetCapabilitiesAsync"));
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("publish", 1)]
    [InlineData("deploy", 2)]
    public async Task PipelineInspectionWaitsForRegistrationAndFiltersDependencies(string? step, int expectedCount)
    {
        var server = new NativeApplicationServer();
        await using var host = NativeCliTestHost.Start(server, new(), () => new());
        var inspection = host.Rpc.InvokeAsync<GetPipelineStepsResponse>("GetPipelineStepsAsync", new GetPipelineStepsRequest { Step = step });
        server.ConfigurePublishing("""{"steps":[{"name":"publish"},{"name":"deploy","dependsOn":["publish"]}]}""");
        var result = await inspection.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(expectedCount, result.Steps.Length);
        Assert.Equal("publish", result.Steps[0].Name);
        Assert.Empty(result.Steps[0].DependsOn);
        if (expectedCount == 2)
        {
            Assert.Equal("deploy", result.Steps[1].Name);
            Assert.Equal(["publish"], result.Steps[1].DependsOn);
        }
        await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => host.Rpc.InvokeAsync<GetPipelineStepsResponse>(
            "GetPipelineStepsAsync", new GetPipelineStepsRequest { Step = "unknown" }));
    }

    [Fact]
    public void SymbolicProjectionNeverInvokesTheLiteralVisitorForASecret()
    {
        var secret = SymbolicValue.SecretParameter(Guid.NewGuid(), "password", "not-a-literal");
        var projected = secret.UriEncode().Project(
            _ => throw new InvalidOperationException("Secret payload exposed."),
            name => $"parameter:{name}",
            (_, _, _) => throw new InvalidOperationException("Unexpected endpoint."),
            parts => string.Concat(parts),
            value => $"encode({value})");
        Assert.Equal("encode(parameter:password)", projected);
    }
}
