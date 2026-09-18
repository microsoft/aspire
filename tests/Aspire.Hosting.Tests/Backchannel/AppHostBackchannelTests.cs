// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using System.Net.Sockets;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

namespace Aspire.Hosting.Backchannel;

[Trait("Partition", "4")]
public class AppHostBackchannelTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CanConnectToBackchannel()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        builder.Configuration[KnownConfigNames.UnixSocketPath] = UnixSocketHelper.GetBackchannelSocketPath();

        var backchannelReadyTaskCompletionSource = new TaskCompletionSource<BackchannelReadyEvent>();
        builder.Eventing.Subscribe<BackchannelReadyEvent>((e, ct) => {
            backchannelReadyTaskCompletionSource.SetResult(e);
            return Task.CompletedTask;
        });

        var backchannelConnectedTaskCompletionSource = new TaskCompletionSource<BackchannelConnectedEvent>();
        builder.Eventing.Subscribe<BackchannelConnectedEvent>((e, ct) => {
            backchannelConnectedTaskCompletionSource.SetResult(e);
            return Task.CompletedTask;
        });

        using var app = builder.Build();

        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var backchannelReadyEvent = await backchannelReadyTaskCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(backchannelReadyEvent.SocketPath);
        await socket.ConnectAsync(endpoint).WaitAsync(TimeSpan.FromSeconds(60));

        _ = await backchannelConnectedTaskCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

        using var stream = new NetworkStream(socket, true);
        using var rpc = JsonRpc.Attach(stream);

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task CanStreamResourceStates()
    {
        using var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(outputHelper);
        builder.Configuration[KnownConfigNames.UnixSocketPath] = UnixSocketHelper.GetBackchannelSocketPath();

        builder.AddResource(new TestResource("test"))
               .WithInitialState(new () {
                    ResourceType = "TestResource",
                    State = new ("Running", null),
                    Properties = [new("A", "B"), new("c", "d")],
                    EnvironmentVariables = [new("e", "f", true), new("g", "h", false)]
               });

        var backchannelReadyTaskCompletionSource = new TaskCompletionSource<BackchannelReadyEvent>();
        builder.Eventing.Subscribe<BackchannelReadyEvent>((e, ct) => {
            backchannelReadyTaskCompletionSource.SetResult(e);
            return Task.CompletedTask;
        });

        using var app = builder.Build();

        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var backchannelReadyEvent = await backchannelReadyTaskCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(backchannelReadyEvent.SocketPath);
        await socket.ConnectAsync(endpoint).WaitAsync(TimeSpan.FromSeconds(60));

        using var stream = new NetworkStream(socket, true);
        using var rpc = JsonRpc.Attach(stream);

        var resourceEvents = await rpc.InvokeAsync<IAsyncEnumerable<RpcResourceState>>(
            "GetResourceStatesAsync",
            Array.Empty<object>()
            ).WaitAsync(TimeSpan.FromSeconds(60));

        await foreach (var resourceEvent in resourceEvents)
        {
            Assert.Equal("test", resourceEvent.Resource);
            Assert.Equal("TestResource", resourceEvent.Type);
            Assert.Equal("Running", resourceEvent.State);
            Assert.Empty(resourceEvent.Endpoints);
            break;
        }

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public async Task PipelineInputsAreScopedToTargetStepResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);

        var targetParameter = builder.AddParameter("target-parameter");
        var otherParameter = builder.AddParameter("other-parameter");

        builder.AddContainer("target", "target-image")
            .WithEnvironment("TARGET_PARAMETER", targetParameter)
            .WithPipelineStepFactory(
                "deploy-target",
                _ => Task.CompletedTask,
                requiredBy: [WellKnownPipelineSteps.Deploy]);

        builder.AddContainer("other", "other-image")
            .WithEnvironment("OTHER_PARAMETER", otherParameter)
            .WithPipelineStepFactory(
                "publish-other",
                _ => Task.CompletedTask,
                requiredBy: [WellKnownPipelineSteps.Publish]);

        using var app = builder.Build();
        var rpcTarget = app.Services.GetRequiredService<AppHostRpcTarget>();

        var deployInputs = await rpcTarget.GetPipelineInputsAsync(new GetPipelineInputsRequest { Step = WellKnownPipelineSteps.Deploy });
        var publishInputs = await rpcTarget.GetPipelineInputsAsync(new GetPipelineInputsRequest { Step = WellKnownPipelineSteps.Publish });
        var processParametersInputs = await rpcTarget.GetPipelineInputsAsync(new GetPipelineInputsRequest { Step = WellKnownPipelineSteps.ProcessParameters });
        var allInputs = await rpcTarget.GetPipelineInputsAsync();

        Assert.Equal(["target-parameter"], deployInputs.Inputs.Select(static input => input.Name));
        Assert.Equal(["other-parameter"], publishInputs.Inputs.Select(static input => input.Name));
        Assert.Equal(["other-parameter", "target-parameter"], processParametersInputs.Inputs.Select(static input => input.Name));
        Assert.Equal(["other-parameter", "target-parameter"], allInputs.Inputs.Select(static input => input.Name));
    }

    [Fact]
    public async Task PipelineInputsReportCurrentValueSource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);

        builder.Configuration["Parameters:configured-parameter"] = "configured-value";
        builder.AddParameter("configured-parameter");
        builder.AddParameter("default-parameter", "default-value", publishValueAsDefault: true);
        builder.AddParameter("missing-parameter");
        builder.AddParameter("secret-parameter", secret: true);
        builder.Configuration["Parameters:secret-parameter"] = "secret-value";

        using var app = builder.Build();
        var rpcTarget = app.Services.GetRequiredService<AppHostRpcTarget>();

        var response = await rpcTarget.GetPipelineInputsAsync();
        var inputs = response.Inputs.ToDictionary(static input => input.Name, StringComparer.Ordinal);

        Assert.True(inputs["configured-parameter"].HasValue);
        Assert.Equal("configured-value", inputs["configured-parameter"].Value);
        Assert.Equal("configuration", inputs["configured-parameter"].ValueSource);

        Assert.True(inputs["default-parameter"].HasValue);
        Assert.Null(inputs["default-parameter"].Value);
        Assert.Equal("default", inputs["default-parameter"].ValueSource);

        Assert.False(inputs["missing-parameter"].HasValue);
        Assert.Null(inputs["missing-parameter"].Value);
        Assert.Null(inputs["missing-parameter"].ValueSource);

        Assert.True(inputs["secret-parameter"].HasValue);
        Assert.Null(inputs["secret-parameter"].Value);
        Assert.Equal("configuration", inputs["secret-parameter"].ValueSource);
    }

    [Fact]
    public async Task PipelineResourcesPreserveTypedPropertiesAndRedactSecrets()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: null);
        builder.Configuration[KnownConfigNames.UnixSocketPath] = UnixSocketHelper.GetBackchannelSocketPath();
        var ready = new TaskCompletionSource<BackchannelReadyEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.Eventing.Subscribe<BackchannelReadyEvent>((e, _) =>
        {
            ready.SetResult(e);
            return Task.CompletedTask;
        });

        builder.AddContainer("api", "test-image").WithAnnotation(new ResourceSnapshotAnnotation(new CustomResourceSnapshot
        {
            ResourceType = "Container",
            Properties =
            [
                new ResourcePropertySnapshot("replicas", 3),
                new ResourcePropertySnapshot("enabled", true),
                new ResourcePropertySnapshot("regions", new[] { "east", "west" }),
                new ResourcePropertySnapshot("secret", "secret-value") { IsSensitive = true }
            ]
        }));

        using var app = builder.Build();
        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(60));
        var backchannelReady = await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(backchannelReady.SocketPath)).WaitAsync(TimeSpan.FromSeconds(60));
        using var stream = new NetworkStream(socket);
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        };
        using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, formatter));
        rpc.StartListening();
        var response = await rpc.InvokeAsync<GetPipelineResourcesResponse>("GetPipelineResourcesAsync", new GetPipelineResourcesRequest()).WaitAsync(TimeSpan.FromSeconds(60));
        var resource = Assert.Single(response.Resources);

        Assert.Equal(3, resource.Properties["replicas"]!.GetValue<int>());
        Assert.True(resource.Properties["enabled"]!.GetValue<bool>());
        Assert.Equal("west", resource.Properties["regions"]![1]!.GetValue<string>());
        Assert.Null(resource.Properties["secret"]);
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
    }
}

file sealed class TestResource(string name) : Resource(name)
{

}
